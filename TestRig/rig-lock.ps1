# =============================================================================
# TestRig session lock - shared implementation
# =============================================================================
# Dot-sourced by BOTH launchers:
#     TestRig/DedicatedServer/dedicated-server.ps1
#     TestRig/ClientRig/client-rig.ps1
#
# There is ONE lock for the whole rig, at TestRig/session.lock, because the two
# halves are not independent resources. They share the developer's single game
# install, the per-Windows-user Unity state
# (%USERPROFILE%\AppData\LocalLow\Rocketwerkz\rocketstation\PlayerCookie-v2.xml)
# and the HKCU\Software\Rocketwerkz\rocketstation PlayerPrefs key, and a
# multiplayer test drives both halves at once. Two independent locks would let
# one agent's client rig walk into another agent's server session, and would
# make an agent acquire two locks in an order that can deadlock against an agent
# acquiring them the other way round.
#
# The rules are in TestRig/session.lock.template (single source of truth). This
# file is only the mechanism. Keeping the mechanism in one place is deliberate:
# a second copy of the timer, ownership and force-break logic would drift, and
# the half that drifted would be the half with the weaker guarantee.
#
# Everything here is prefixed Rig* so dot-sourcing cannot collide with a
# launcher's own helpers.
#
# TWO CONCURRENCY PRIMITIVES, DOING DIFFERENT JOBS
#   1. session.lock is the SESSION lock: a coarse, human-scale reservation that
#      spans many start/stop cycles and expires on a timer.
#   2. A named system mutex is the CRITICAL SECTION around every read-modify-write
#      of that file. Without it, "read the state, decide, write" is not atomic:
#      two agents that both observe 'None' each mint an owner id, both write, the
#      second wins, and the first walks away holding an id that is not in the
#      file. It finds out on its next mutating command, which may be minutes and
#      one full provision later.
#   The mutex is never held across anything slow. Process teardown, the busy
#   probe and every Write-Host happen outside it.
#
# STATE HYGIENE HANGS OFF ACQUISITION
#   A NEW lock is a session boundary, so New-RigLock calls Invoke-RigReset
#   (TestRig/rig-reset.ps1) to clear what the previous session left behind. The
#   lock is the only mandatory choke point that already exists, which is why the
#   reset lives here rather than in a rule somebody has to remember. Re-asserting
#   a lock you already hold does NOT reset; -KeepState opts out loudly.
#
# Tests: TestRig/rig-lock.tests.ps1 (offline, no game, no network, runs against a
# temp directory through Initialize-RigLockPaths). Run it after any change here.
# The reset has its own suite at TestRig/rig-reset.tests.ps1; this file's suite
# does not dot-source rig-reset.ps1, so the lock tests never reset anything.
# =============================================================================

# ---- paths ----------------------------------------------------------------
# Every path the library uses is set in one place so the whole mechanism can be
# pointed at a temp directory by the test suite. Called at dot-source time with
# this file's own folder, so a launcher sees exactly the behaviour it always had.

function Initialize-RigLockPaths {
    # Point the library at a TestRig-shaped root. The parameter is -RigHome and
    # not -Home because $HOME is a read-only automatic variable in PowerShell and
    # a parameter of that name cannot be bound at all.
    #
    # The image names and the instance root are parameters for the same reason the
    # paths are: without them the process-identity checks below could not be
    # exercised offline.
    param(
        [Parameter(Mandatory)] [string] $RigHome,
        [string] $ServerImageName = 'rocketstation_DedicatedServer',
        [string] $ClientImageName = 'rocketstation',
        [string] $InstanceRoot
    )

    $script:RigLockHome  = $RigHome
    $script:RigLockFile  = Join-Path $RigHome 'session.lock'
    $script:RigLockRules = Join-Path $RigHome 'session.lock.template'

    # Activity probes for each half. Paths are fixed relative to TestRig/, so the
    # library can see whether EITHER half is busy regardless of which launcher
    # dot-sourced it.
    $script:RigDediServerPid = Join-Path $RigHome 'DedicatedServer\data\server.pid'
    $script:RigDediServerLog = Join-Path $RigHome 'DedicatedServer\data\server.log'
    $script:RigClientDataDir = Join-Path $RigHome 'ClientRig\data'
    $script:RigDediInstallDir = Join-Path $RigHome 'DedicatedServer\install'

    # Process identity. A pid file alone is not proof that the rig is busy; see
    # Get-RigLiveProcess.
    $script:RigServerImage = $ServerImageName
    $script:RigClientImage = $ClientImageName

    # Where client instance trees live. Mirrors client-rig.ps1's own resolution
    # order, because instances normally sit on the game install's volume rather
    # than inside TestRig/.
    $script:RigClientInstanceRoot =
        if     ($InstanceRoot)                      { $InstanceRoot }
        elseif ($env:STATIONEERS_CLIENTRIG_ROOT)    { $env:STATIONEERS_CLIENTRIG_ROOT }
        else                                        { Join-Path $RigHome 'ClientRig\instances' }

    # The critical section belongs to one lock FILE, so a re-point gets a new
    # mutex. Otherwise a test run would serialise against a real rig session and
    # (worse) a real session would serialise against a test.
    $script:RigLockMutexObj      = $null
    $script:RigLockMutexFullName = $null
}

function Get-RigLockRulesPath { return $script:RigLockRules }
function Get-RigLockFilePath  { return $script:RigLockFile }
function Get-RigLockHomePath  { return $script:RigLockHome }

# ---- the critical section -------------------------------------------------

function Get-RigLockMutexName {
    # Derived from the lock file path rather than being a fixed string, because
    # the mutex guards one specific lock file. A test home therefore gets its own
    # critical section and can neither be blocked by, nor block, a real session.
    $key = $script:RigLockFile.ToLowerInvariant()
    $sha = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($key))
    $hex = [System.BitConverter]::ToString($sha[0..7]).Replace('-', '')
    return "StationeersTestRig.SessionLock.$hex"
}

function Get-RigLockMutex {
    if ($script:RigLockMutexObj) { return $script:RigLockMutexObj }
    $name = Get-RigLockMutexName
    # Global\ first so the mechanism still works if a rig process ever runs in a
    # different Terminal Services session; Local\ is the fallback when creating a
    # Global\ kernel object is denied (SeCreateGlobalPrivilege). Every rig process
    # runs as the same interactive user, so they all land in the same namespace.
    foreach ($ns in @('Global', 'Local')) {
        try {
            $script:RigLockMutexObj      = [System.Threading.Mutex]::new($false, "$ns\$name")
            $script:RigLockMutexFullName = "$ns\$name"
            return $script:RigLockMutexObj
        }
        catch [System.UnauthorizedAccessException] { continue }
        catch [System.IO.IOException] { continue }
    }
    throw "Could not create the rig-lock critical section (mutex '$name') in either the Global or the Local namespace. Without it, two agents can acquire the rig lock at the same time, so the launcher refuses to continue."
}

function Get-RigLockMutexFullName {
    Get-RigLockMutex | Out-Null
    return $script:RigLockMutexFullName
}

function Invoke-WithRigLockMutex {
    # Run $Body as the sole holder of the rig-lock critical section.
    #
    # BOUNDED, never infinite: every critical section here is a couple of small
    # file operations, so anything past the timeout means another process is hung
    # rather than busy, and an agent is better served by a clear error than by a
    # wait that never ends.
    param(
        [Parameter(Mandatory)] [scriptblock] $Body,
        [string] $Context = 'update the rig lock',
        [int] $TimeoutSeconds = 15
    )
    $m = Get-RigLockMutex
    $held = $false
    try {
        try {
            $held = $m.WaitOne([TimeSpan]::FromSeconds($TimeoutSeconds))
        }
        catch [System.Threading.AbandonedMutexException] {
            # A process was killed while holding the mutex. The wait SUCCEEDED and
            # this process now owns it; the exception only reports that the previous
            # owner never released. Carrying on is safe because the lock file itself
            # is never left half-written: Write-RigLock stages to a temp file and
            # swaps it in with a single atomic replace.
            $held = $true
        }
        if (-not $held) {
            throw "Timed out after ${TimeoutSeconds}s waiting for the rig-lock critical section ($($script:RigLockMutexFullName)) while trying to $Context. Every critical section here is a few small file operations, so this means another process is hung while holding it, not merely busy. The lock file was NOT modified. Look for a stuck launcher process."
        }
        return (& $Body)
    }
    finally {
        if ($held) { try { $m.ReleaseMutex() } catch { } }
    }
}

# ---- primitives -----------------------------------------------------------

function Get-RigNowUtc {
    [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ss'Z'")
}

function Get-RigPidFromFile {
    param([string] $File)
    if (-not $File -or -not (Test-Path -LiteralPath $File)) { return $null }
    $raw = (Get-Content -Raw -ErrorAction SilentlyContinue -LiteralPath $File)
    if (-not $raw) { return $null }
    $val = $raw.Trim()
    if (-not $val) { return $null }
    $parsed = 0
    if (-not [int]::TryParse($val, [ref]$parsed)) { return $null }
    return $parsed
}

function Get-RigLiveProcess {
    # The process behind a pid file, but ONLY if it is really the process the file
    # claims. Returns $null otherwise.
    #
    # The image-name check is load bearing. Windows recycles process ids, and the
    # rig's pid files genuinely go stale: neither launcher's cleanup runs on a
    # force-kill or a reboot, so a server.pid or game.pid can outlive its process
    # and its number can later belong to something unrelated. Trusting the bare
    # number would report the rig as busy forever, and the expired-but-busy
    # self-renew in Get-RigLockState would then keep another session's dead lock
    # alive with no timer able to reclaim it. That is the one failure the timer
    # exists to prevent.
    #
    # A process whose identity cannot be confirmed is not proof of life.
    param(
        [Nullable[int]] $TargetPid,
        [string] $ImageName
    )
    if (-not $TargetPid) { return $null }
    $p = Get-Process -Id $TargetPid -ErrorAction SilentlyContinue
    if (-not $p) { return $null }
    if ($ImageName -and $p.Name -ne $ImageName) { return $null }
    return $p
}

function Test-RigPidAlive {
    param(
        [Nullable[int]] $TargetPid,
        [string] $ImageName
    )
    return ($null -ne (Get-RigLiveProcess -TargetPid $TargetPid -ImageName $ImageName))
}

function Read-RigLockText {
    # Read the lock file with the widest possible share mode.
    #
    # FILE_SHARE_DELETE matters and is not decoration: Write-RigLock swaps the new
    # file in with MoveFileEx(REPLACE_EXISTING), which needs DELETE access on the
    # target. A reader that held the file open without sharing delete would make a
    # concurrent writer fail. Retries cover the reverse race (reading in the
    # instant the file is being replaced), and exhausting them THROWS rather than
    # reporting "no lock", because a read failure that reads as "the rig is free"
    # is exactly the answer that gets a live session stomped.
    param([string] $Path)
    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return $null }
    $share = ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
    for ($attempt = 1; $attempt -le 8; $attempt++) {
        try {
            $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, $share)
            try {
                $sr = [System.IO.StreamReader]::new($fs)
                try { return $sr.ReadToEnd() } finally { $sr.Dispose() }
            }
            finally { $fs.Dispose() }
        }
        catch [System.IO.FileNotFoundException]      { return $null }
        catch [System.IO.DirectoryNotFoundException] { return $null }
        catch [System.IO.IOException]                { Start-Sleep -Milliseconds (5 * $attempt) }
        catch [System.UnauthorizedAccessException]   { Start-Sleep -Milliseconds (5 * $attempt) }
    }
    throw "Could not read the rig lock file at $Path after 8 attempts. Refusing to treat an unreadable lock as an absent one. Check for a process holding it open."
}

function Read-RigLock {
    # Returns an ordered hashtable of lock fields, or $null if no usable lock.
    # A file with no 'owner' key is not a lock: that covers an empty file, a
    # comment-only file, and anything hand-broken.
    $text = Read-RigLockText -Path $script:RigLockFile
    if ($null -eq $text) { return $null }
    $fields = [ordered]@{}
    foreach ($line in ($text -split "`r?`n")) {
        $t = $line.Trim()
        if (-not $t -or $t.StartsWith('#')) { continue }
        $eq = $t.IndexOf('=')
        if ($eq -lt 1) { continue }
        # Split on the FIRST '=' only, so a value may contain '=' and round-trip.
        $fields[$t.Substring(0, $eq).Trim()] = $t.Substring($eq + 1).Trim()
    }
    if (-not $fields.Contains('owner')) { return $null }
    return $fields
}

function Write-RigLock {
    # Stage to a per-process temp file, then swap it in with a single atomic
    # replace. A concurrent reader therefore sees either the whole old file or the
    # whole new one, never a partial write and never a moment with no file at all.
    # The temp name carries the process id so two writers can never collide on it
    # even outside the critical section.
    param([Parameter(Mandatory)] $Fields)
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine('# Stationeers TestRig - ACTIVE session lock (auto-managed; do not hand-edit).')
    [void]$sb.AppendLine('# Covers BOTH halves: TestRig/DedicatedServer/ and TestRig/ClientRig/.')
    [void]$sb.AppendLine('# Mechanism and rules: session.lock.template (single source of truth).')
    foreach ($k in $Fields.Keys) {
        [void]$sb.AppendLine("$k=$($Fields[$k])")
    }
    $dir = Split-Path -Parent $script:RigLockFile
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

    $tmp = "$($script:RigLockFile).$PID-$([guid]::NewGuid().ToString('N').Substring(0, 8)).tmp"
    Set-Content -LiteralPath $tmp -Value $sb.ToString() -Encoding utf8 -NoNewline
    try {
        for ($attempt = 1; $attempt -le 10; $attempt++) {
            try {
                [System.IO.File]::Move($tmp, $script:RigLockFile, $true)
                return
            }
            catch [System.IO.IOException]              { Start-Sleep -Milliseconds (5 * $attempt) }
            catch [System.UnauthorizedAccessException]  { Start-Sleep -Milliseconds (5 * $attempt) }
        }
        throw "Could not replace the rig lock file at $($script:RigLockFile) after 10 attempts. Something is holding it open. The lock was not updated."
    }
    finally {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    }
}

function Remove-RigLockFile {
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        if (-not (Test-Path -LiteralPath $script:RigLockFile)) { return }
        try {
            Remove-Item -LiteralPath $script:RigLockFile -Force -ErrorAction Stop
            return
        }
        catch { Start-Sleep -Milliseconds (5 * $attempt) }
    }
    throw "Could not delete the rig lock file at $($script:RigLockFile) after 10 attempts. Something is holding it open; the lock was NOT released."
}

# ---- the timer ------------------------------------------------------------

function Test-RigLockTimerExpired {
    # Fail closed on every unreadable input. A timer field that cannot be trusted
    # must never make a lock look fresher than it is, so a missing, unparseable or
    # negative value is treated as expired.
    param([Parameter(Mandatory)] $Lock)
    $ttl = 10
    if ($Lock.Contains('ttl_minutes')) {
        $parsed = 0
        if ([int]::TryParse($Lock['ttl_minutes'], [ref]$parsed) -and $parsed -ge 0) { $ttl = $parsed }
        else { return $true }
    }
    if (-not $Lock.Contains('refreshed_at')) { return $true }
    try {
        $r = [DateTime]::Parse($Lock['refreshed_at'],
            [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal)
    } catch { return $true }
    # A refreshed_at in the future yields a negative age, which is simply not
    # expired. Clock skew between two machines cannot happen here (one machine),
    # and a hand-edited future stamp is not worth a special case.
    return (([DateTime]::UtcNow - $r).TotalMinutes -gt $ttl)
}

function Get-RigLockAgeText {
    param([Parameter(Mandatory)] $Lock)
    try {
        $r = [DateTime]::Parse($Lock['refreshed_at'],
            [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal)
        return "$([int](([DateTime]::UtcNow - $r).TotalMinutes)) min ago"
    } catch { return 'unknown' }
}

# ---- is the rig actually busy ---------------------------------------------

function Measure-PlayersInLog {
    # Pure helper: net connected-client count from a server.log-format file.
    # Each completed join logs "Client <name> (<id>) is ready"; each leave logs
    # "Client disconnected: ...". server.log truncates per launch, so the whole
    # file is the current run; net = (ready events) - (disconnected events).
    # Side-effect-free and takes an explicit path, so it can be unit-tested
    # offline against synthetic logs without a running server or a real client.
    # A listen host runs the same server-side connection code, so its own Unity
    # log is a second legitimate input; see Get-RigClientInstanceStates.
    #
    # Note the interaction with a force-killed server: it leaves N "is ready"
    # lines with no matching disconnects, so this count stays high forever. That
    # only ever reaches the busy signal once the pid check has already passed, and
    # the pid check now verifies process identity (Get-RigLiveProcess), so a
    # recycled id can no longer resurrect a dead server's player count.
    # The parameter is deliberately NOT mandatory: a mandatory parameter handed
    # $null prompts, and a lock check must never block on a prompt.
    param([string] $Path)
    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return 0 }
    $ready = 0
    $disc  = 0
    foreach ($line in (Get-Content -ErrorAction SilentlyContinue -LiteralPath $Path)) {
        if ($line -match 'Client .*\) is ready') { $ready++ }
        elseif ($line -match 'Client disconnected:') { $disc++ }
    }
    $net = $ready - $disc
    if ($net -lt 0) { return 0 }
    return $net
}

function Get-RigNewestInstanceLog {
    # Newest Unity log for a client instance. Each -Start writes a fresh
    # unity-<stamp>.log, so the newest file is the current run.
    param([Parameter(Mandatory)] [string] $InstanceDir)
    $logDir = Join-Path $InstanceDir 'logs'
    if (-not (Test-Path -LiteralPath $logDir)) { return $null }
    $newest = Get-ChildItem -Path $logDir -Filter 'unity-*.log' -File -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if (-not $newest) { return $null }
    return $newest.FullName
}

function Get-RigClientInstanceStates {
    # One record per client-rig instance whose game process is alive.
    #
    # FILESYSTEM ONLY, no HTTP, no contact with the game. That is a deliberate
    # constraint, not an omission. This runs on the path of every gated command,
    # and a control-plane request to an instance that is mid-world-load can block
    # for seconds; a lock check that hangs is worse than one that is slightly less
    # precise. Everything reported here comes from files the client-rig launcher
    # already writes: data/<name>/game.pid, data/<name>/instance.json and
    # data/<name>/logs/unity-*.log.
    #
    # Role comes from the manifest and degrades gracefully: an instance
    # provisioned before the manifest carried a 'role' field, or one whose
    # manifest is missing or half-written, reports Role = $null and is described
    # as "role unknown". It still counts as a live instance, so liveness never
    # depends on the field being there.
    $out = New-Object System.Collections.Generic.List[object]
    if (-not (Test-Path -LiteralPath $script:RigClientDataDir)) { return $out.ToArray() }

    foreach ($pidFile in (Get-ChildItem -Path $script:RigClientDataDir -Filter 'game.pid' -Recurse -File -ErrorAction SilentlyContinue)) {
        $procId = Get-RigPidFromFile $pidFile.FullName
        if (-not (Test-RigPidAlive -TargetPid $procId -ImageName $script:RigClientImage)) { continue }

        $dir  = Split-Path -Parent $pidFile.FullName
        $name = Split-Path -Leaf $dir
        $role = $null
        $manifestPath = Join-Path $dir 'instance.json'
        if (Test-Path -LiteralPath $manifestPath) {
            try {
                $m = (Get-Content -Raw -ErrorAction Stop -LiteralPath $manifestPath) | ConvertFrom-Json -ErrorAction Stop
                if ($m) {
                    if ($m.PSObject.Properties['role'] -and $m.role)                 { $role = [string]$m.role }
                    if ($m.PSObject.Properties['instanceName'] -and $m.instanceName) { $name = [string]$m.instanceName }
                }
            }
            catch { }   # unreadable manifest degrades to "role unknown"
        }

        # $null means "not known", 0 means "known to be nobody". Measure-PlayersInLog
        # answers 0 for a missing file, so the log has to be found first or a host
        # that has not written one yet would read as an empty session.
        $players = $null
        if ($role -eq 'host') {
            $log = Get-RigNewestInstanceLog $dir
            if ($log) { $players = Measure-PlayersInLog $log }
        }

        $out.Add([pscustomobject]@{
            Name      = $name
            ProcessId = $procId
            Role      = $role
            Players   = $players
        })
    }
    return $out.ToArray()
}

function Get-RigTrackedProcessIds {
    # Every process id the rig currently claims through a pid file.
    $ids = New-Object 'System.Collections.Generic.HashSet[int]'
    $sp = Get-RigPidFromFile $script:RigDediServerPid
    if ($null -ne $sp) { [void]$ids.Add($sp) }
    if (Test-Path -LiteralPath $script:RigClientDataDir) {
        foreach ($f in (Get-ChildItem -Path $script:RigClientDataDir -Filter 'game.pid' -Recurse -File -ErrorAction SilentlyContinue)) {
            $v = Get-RigPidFromFile $f.FullName
            if ($null -ne $v) { [void]$ids.Add($v) }
        }
    }
    # Comma-wrapped: PowerShell unrolls any IEnumerable it returns, which would
    # hand the caller the ids instead of the set (and $null for an empty one).
    return , $ids
}

function Get-RigOrphanProcesses {
    # Game processes the rig is running but no longer tracks: a killed launcher, a
    # crashed test, a pid file deleted while its process lived on. They are
    # invisible to every other mechanism here, yet they still hold a control-plane
    # port, a game port and possibly a connection to a server. The next -Start
    # then binds different ports and two clients with the same identity end up in
    # one world, which is precisely the "test that is confidently wrong" this rig
    # exists to rule out.
    #
    # DELIBERATELY NOT PART OF THE BUSY SIGNAL. An orphan is by definition not
    # reachable through any launcher action, so counting it as busy would pin the
    # lock live with no way to clear it short of -BreakLock, which is human-gated.
    # That would turn a stray process into a permanently unreclaimable rig, the
    # exact failure the timer exists to prevent. Report it loudly instead, and let
    # the timer keep working.
    #
    # Scoped so the developer's OWN Stationeers client is never reported: it has
    # the same image name, but it runs out of the real install rather than a rig
    # tree. An untracked dedicated server is ours wherever it lives, because the
    # developer does not run one outside the rig.
    $tracked = Get-RigTrackedProcessIds
    $roots   = @($script:RigDediInstallDir, $script:RigClientInstanceRoot) | Where-Object { $_ }
    $names   = @($script:RigServerImage, $script:RigClientImage) | Where-Object { $_ } | Select-Object -Unique
    $out     = New-Object System.Collections.Generic.List[object]
    if ($names.Count -eq 0) { return $out.ToArray() }

    foreach ($p in (Get-Process -Name $names -ErrorAction SilentlyContinue)) {
        if ($tracked.Contains($p.Id)) { continue }
        $path = $null
        try { $path = $p.Path } catch { }

        $scope = 'foreign'
        if (-not $path) {
            # Cannot read the image path (rare for a same-user process). Reported
            # rather than dismissed: silently dropping the one process we cannot
            # identify is how an orphan stays invisible.
            $scope = 'unknown'
        }
        else {
            foreach ($r in $roots) {
                if ($path.StartsWith($r, [StringComparison]::OrdinalIgnoreCase)) { $scope = 'rig'; break }
            }
        }
        # An untracked dedicated server is ours wherever it lives. The rule only
        # holds while the two image names are actually distinct: it works because
        # rocketstation_DedicatedServer is unmistakable and rocketstation is not,
        # so if both are configured to the same name there is no distinction to
        # trade on and the path scoping is the only honest signal.
        if ($scope -ne 'rig' -and $p.Name -eq $script:RigServerImage -and $script:RigServerImage -ne $script:RigClientImage) {
            $scope = 'rig'
        }
        if ($scope -eq 'foreign') { continue }

        $out.Add([pscustomobject]@{
            Name      = $p.Name
            ProcessId = $p.Id
            Path      = $path
            Scope     = $scope
        })
    }
    return $out.ToArray()
}

function Get-RigBusySignal {
    # Is the rig actually in use right now, on either half. This is what keeps a
    # live test alive past its timer, so an agent that is mid-run does not lose
    # the rig to a second agent between two commands.
    #
    # Dedicated server: a player is connected. The 'clients' / 'status' console
    # commands write to the in-game console rather than the Unity -logFile, so
    # they cannot be scraped; the connection lifecycle IS logged, so server.log
    # is scanned instead. A server running with nobody connected is NOT busy,
    # which is what lets an abandoned server be reclaimed.
    #
    # Client rig: any provisioned instance's game process is alive. The bar is
    # deliberately lower than the server's, because on this half the running
    # processes ARE the test (there is no human to connect), and because the
    # thing another agent would do is -Stop -All, which destroys a run in
    # progress with no way to tell afterwards that it happened.
    #
    # The reason text names WHAT is happening, not just how many processes are up.
    # It is what a human reads when deciding whether to authorise -BreakLock, and
    # "2 client instance(s) running" cannot tell a live hosted test at minute 40
    # from two instances somebody forgot to stop. Liveness itself does not depend
    # on any of the extra detail: an alive process is busy whatever its role.
    $reasons       = @()
    $serverPlayers = 0
    $serverLive    = Test-RigPidAlive -TargetPid (Get-RigPidFromFile $script:RigDediServerPid) -ImageName $script:RigServerImage
    if ($serverLive) {
        $serverPlayers = Measure-PlayersInLog $script:RigDediServerLog
        if ($serverPlayers -ge 1) { $reasons += "$serverPlayers player(s) connected to the dedicated server" }
    }

    $instances = @(Get-RigClientInstanceStates)
    $hostInstances = @($instances | Where-Object { $_.Role -eq 'host' })
    if ($instances.Count -ge 1) {
        $parts = @()
        foreach ($i in $instances) {
            if ($i.Role -eq 'host') {
                $who = if ($null -eq $i.Players) { 'connected clients unknown' }
                       else { "$($i.Players) connected" }
                $parts += "$($i.Name)=HOST ($who)"
            }
            elseif ($i.Role) { $parts += "$($i.Name)=$($i.Role)" }
            else             { $parts += "$($i.Name)=role unknown" }
        }
        $reasons += "$($instances.Count) client instance(s) running: $($parts -join ', ')"
    }

    # Orphans are appended to the reason text but NEVER to $reasons, so they are
    # visible everywhere the reason is read (including the -BreakLock refusal a
    # human acts on) without making the rig busy. See Get-RigOrphanProcesses.
    $orphans = @(Get-RigOrphanProcesses)
    $busy    = ($reasons.Count -gt 0)
    $detail  = ($reasons -join '; ')
    if ($orphans.Count -ge 1) {
        $names  = ($orphans | ForEach-Object { "$($_.Name) pid $($_.ProcessId)$(if ($_.Scope -eq 'unknown') { ' (image path unreadable)' })" }) -join ', '
        $note   = "$($orphans.Count) UNTRACKED rig game process(es), not counted as busy: $names. Nothing here can stop them; kill them by pid."
        $detail = if ($detail) { "$detail; $note" } else { $note }
    }

    return [pscustomobject]@{
        Busy          = $busy
        Detail        = $detail
        HostLive      = ($hostInstances.Count -ge 1)
        HostNames     = @($hostInstances | ForEach-Object { $_.Name })
        Instances     = $instances
        Orphans       = $orphans
        ServerLive    = $serverLive
        ServerPlayers = $serverPlayers
    }
}

# ---- state ----------------------------------------------------------------

function Resolve-RigLockState {
    # Pure classification: no reads, no writes, no probing. The caller decides
    # where the file read and the (comparatively expensive) busy probe happen,
    # which is what lets the critical section stay small.
    #
    # States: None, Mine, LiveForeign, DeadForeign.
    # Renew marks the one case that needs a write to keep the rules true: a
    # foreign lock whose timer lapsed while the rig is genuinely busy gets a full
    # fresh TTL, so a brief gap (a client restarting, a player reconnecting) does
    # not hand a live test to somebody else.
    param(
        $Lock,
        [string] $CallerId,
        $Busy
    )
    if (-not $Lock) {
        return [pscustomobject]@{ State = 'None'; Lock = $null; Busy = $null; Renew = $false }
    }
    if ($CallerId -and $Lock['owner'] -eq $CallerId) {
        return [pscustomobject]@{ State = 'Mine'; Lock = $Lock; Busy = $null; Renew = $false }
    }
    if (-not (Test-RigLockTimerExpired $Lock)) {
        return [pscustomobject]@{ State = 'LiveForeign'; Lock = $Lock; Busy = $null; Renew = $false }
    }
    if ($Busy -and $Busy.Busy) {
        return [pscustomobject]@{ State = 'LiveForeign'; Lock = $Lock; Busy = $Busy.Detail; Renew = $true }
    }
    return [pscustomobject]@{ State = 'DeadForeign'; Lock = $Lock; Busy = $null; Renew = $false }
}

function Test-RigBusyProbeNeeded {
    # The busy probe only changes the answer for a foreign lock whose timer has
    # lapsed. Everything else is decided by the file alone.
    param($Lock, [string] $CallerId)
    if (-not $Lock) { return $false }
    if ($CallerId -and $Lock['owner'] -eq $CallerId) { return $false }
    return (Test-RigLockTimerExpired $Lock)
}

function Get-RigLockState {
    # Classify the lock, and perform the two writes the classification implies:
    # the expired-but-busy self-renew always, and the owner's timer refresh when
    # -RefreshIfMine is passed (which is how a mutating command refreshes without
    # a second round trip, and without a window between the check and the bump).
    param(
        [string] $CallerId,
        [switch] $RefreshIfMine
    )
    # Pre-read OUTSIDE the critical section, used only to decide whether the busy
    # probe is worth running. The probe walks both halves' data trees, and nothing
    # slow belongs inside the mutex. The authoritative read happens under it.
    $busy = $null
    $pre  = Read-RigLock
    if (Test-RigBusyProbeNeeded -Lock $pre -CallerId $CallerId) { $busy = Get-RigBusySignal }

    return Invoke-WithRigLockMutex -Context 'read the rig lock state' -Body {
        $lock = Read-RigLock
        $b    = $busy
        # The pre-read can disagree with the authoritative read when another agent
        # wrote in between. Rare, and correctness beats speed here.
        if ($null -eq $b -and (Test-RigBusyProbeNeeded -Lock $lock -CallerId $CallerId)) { $b = Get-RigBusySignal }

        $st = Resolve-RigLockState -Lock $lock -CallerId $CallerId -Busy $b
        if ($st.State -eq 'Mine' -and $RefreshIfMine) {
            $lock['refreshed_at'] = Get-RigNowUtc
            Write-RigLock $lock
        }
        elseif ($st.Renew) {
            $lock['refreshed_at'] = Get-RigNowUtc
            Write-RigLock $lock
        }
        $st
    }
}

function Format-ForeignRigLock {
    param([Parameter(Mandatory)] $State)
    $lk = $State.Lock
    $busy = if ($State.Busy) { "; $($State.Busy)" } else { '' }
    return "    purpose : $($lk['purpose'])`n    owner   : $($lk['owner'])`n    active  : $(Get-RigLockAgeText $lk)$busy"
}

# ---- the gate -------------------------------------------------------------

function Assert-RigLockHeld {
    # Gate for every mutating action on either half. Holding the lock refreshes
    # its timer, which is what makes "any mutating command also refreshes it"
    # true without a separate call. Classification and refresh happen in ONE
    # critical section, so no other agent can slip between them.
    param(
        [Parameter(Mandatory)] [string] $Action,
        [string] $CallerId,
        [Parameter(Mandatory)] [string] $Tool   # 'dedicated-server.ps1' or 'client-rig.ps1'
    )
    $st = Get-RigLockState -CallerId $CallerId -RefreshIfMine
    switch ($st.State) {
        'Mine' { return }
        'None' {
            throw "[$Action] No rig session lock is held. Acquire one first:`n    $Tool -Lock -Purpose `"<what you are testing>`"`nthen pass -As <id> on every mutating command. One lock covers BOTH TestRig halves. See TestRig/session.lock.template."
        }
        'DeadForeign' {
            throw "[$Action] No live rig session lock is held (a previous lock expired). Re-acquire:`n    $Tool -Lock -Purpose `"<what you are testing>`"`nSee TestRig/session.lock.template."
        }
        'LiveForeign' {
            throw "[$Action] The test rig is locked by another session.`n$(Format-ForeignRigLock $st)`nDo NOT proceed. Report this purpose to the user and let the user decide. Only the user may authorize -BreakLock. See TestRig/session.lock.template."
        }
    }
}

function Update-RigLockIfMine {
    # Best-effort refresh for a long-running read-only action (a readiness
    # barrier can outlast the TTL on its own). Silent no-op when the caller
    # holds nothing, so it never turns a read-only command into a gated one.
    param([string] $CallerId)
    if (-not $CallerId) { return }
    Invoke-WithRigLockMutex -Context 'refresh the rig lock' -Body {
        $lock = Read-RigLock
        if (-not $lock -or $lock['owner'] -ne $CallerId) { return }
        $lock['refreshed_at'] = Get-RigNowUtc
        Write-RigLock $lock
    }
}

# ---- acquire / refresh / release ------------------------------------------

function New-RigLock {
    # Acquire, re-assert, or (human-authorized) break-and-take the rig lock.
    # Returns the owner id.
    #
    # -WaitSeconds turns a refusal into a bounded QUEUE: poll until the rig frees
    # up or the budget runs out. It is a queue and not a reservation, and that
    # limitation is stated rather than papered over: there is no ordering
    # fairness, so three waiters do not get the rig in the order they arrived.
    # Default 0 keeps the historical fail-fast behaviour.
    #
    # A NEW lock also resets the rig's between-session state (see the call to
    # Invoke-RigReset at the bottom). -KeepState opts out, loudly.
    param(
        [Parameter(Mandatory)] [string] $Purpose,
        [string] $CallerId,
        [int] $TtlMinutes = 10,
        [switch] $BreakLock,
        [Parameter(Mandatory)] [string] $Tool,
        [scriptblock] $OnReclaim,
        [int] $WaitSeconds = 0,
        [int] $PollSeconds = 5,
        [switch] $KeepState
    )
    if ($PollSeconds -lt 1) { $PollSeconds = 1 }
    $deadline  = (Get-Date).AddSeconds([Math]::Max(0, $WaitSeconds))
    $announced = $false

    while ($true) {
        # Probe outside the critical section (see Get-RigLockState for why).
        $busy = $null
        $pre  = Read-RigLock
        if (Test-RigBusyProbeNeeded -Lock $pre -CallerId $CallerId) { $busy = Get-RigBusySignal }

        $outcome = Invoke-WithRigLockMutex -Context 'acquire the rig lock' -Body {
            $lock = Read-RigLock
            $b    = $busy
            if ($null -eq $b -and (Test-RigBusyProbeNeeded -Lock $lock -CallerId $CallerId)) { $b = Get-RigBusySignal }
            $st = Resolve-RigLockState -Lock $lock -CallerId $CallerId -Busy $b

            if ($st.State -eq 'LiveForeign' -and -not $BreakLock) {
                # Someone else's live lock. Give it its fresh TTL if the rig is
                # busy, then report the refusal to the caller for printing.
                if ($st.Renew) {
                    $lock['refreshed_at'] = Get-RigNowUtc
                    Write-RigLock $lock
                }
                return [pscustomobject]@{ Result = 'Blocked'; State = $st }
            }

            if ($st.State -eq 'Mine') {
                $owner = $lock['owner']
                Write-RigLock ([ordered]@{
                    owner = $owner; purpose = $Purpose
                    acquired_at = $lock['acquired_at']; refreshed_at = (Get-RigNowUtc)
                    ttl_minutes = $TtlMinutes; host = $env:COMPUTERNAME
                })
                return [pscustomobject]@{ Result = 'Reasserted'; Owner = $owner; State = $st }
            }

            # None, DeadForeign, or LiveForeign with an authorized -BreakLock.
            $owner = [guid]::NewGuid().ToString('N').Substring(0, 8)
            Write-RigLock ([ordered]@{
                owner = $owner; purpose = $Purpose
                acquired_at = (Get-RigNowUtc); refreshed_at = (Get-RigNowUtc)
                ttl_minutes = $TtlMinutes; host = $env:COMPUTERNAME
            })
            return [pscustomobject]@{ Result = 'Acquired'; Owner = $owner; State = $st }
        }

        if ($outcome.Result -eq 'Blocked') {
            if ((Get-Date) -ge $deadline) {
                $waited = if ($WaitSeconds -gt 0) { " after waiting ${WaitSeconds}s" } else { '' }
                throw "Cannot acquire${waited}: the test rig is locked by another session.`n$(Format-ForeignRigLock $outcome.State)`nReport this purpose to the user. Only the user may authorize -BreakLock. See TestRig/session.lock.template."
            }
            if (-not $announced) {
                Write-Host "[Lock] Rig is held by another session; queueing for up to ${WaitSeconds}s (no ordering fairness is promised)."
                Write-Host (Format-ForeignRigLock $outcome.State)
                $announced = $true
            }
            else {
                $left = [int]([Math]::Max(0, ($deadline - (Get-Date)).TotalSeconds))
                Write-Host "[Lock]   still held by '$($outcome.State.Lock['purpose'])'; ${left}s left."
            }
            $sleep = [int][Math]::Min($PollSeconds, [Math]::Max(1, ($deadline - (Get-Date)).TotalSeconds))
            Start-Sleep -Seconds $sleep
            continue
        }

        # Everything below is outside the critical section on purpose: printing
        # is slow, and OnReclaim tears down an orphaned process, which can take
        # tens of seconds. The lock is already ours by this point, so the reclaim
        # runs under our own reservation instead of blocking every other agent.
        if ($outcome.Result -eq 'Reasserted') {
            # NO state reset on this branch, and that is load bearing. Re-asserting
            # a lock you already hold is what an agent does mid-test to change the
            # purpose or the TTL; resetting here would wipe the run that is in
            # progress. Only a genuinely new acquisition is a session boundary.
            Write-Host "[Lock] Re-asserted the rig session lock (owner $($outcome.Owner)). Pass -As $($outcome.Owner) on mutating commands."
            Write-Host "[Lock]   state was NOT reset: this is the same session, not a new one."
            return $outcome.Owner
        }
        if ($outcome.State.State -eq 'LiveForeign') {
            Write-Warning "[Lock] -BreakLock: broke a live lock held by '$($outcome.State.Lock['purpose'])' (owner $($outcome.State.Lock['owner']))."
        }
        elseif ($outcome.State.State -eq 'DeadForeign' -and $OnReclaim) {
            & $OnReclaim
        }
        Write-Host "[Lock] Acquired the rig session lock (covers BOTH TestRig halves)."
        Write-Host "[Lock]   owner   : $($outcome.Owner)   (pass -As $($outcome.Owner) on every mutating command, on either launcher)"
        Write-Host "[Lock]   purpose : $Purpose"
        Write-Host "[Lock]   ttl     : $TtlMinutes min (refresh with -RefreshLock -As $($outcome.Owner) while actively testing)"
        Write-Host "[Lock] Rules: TestRig/session.lock.template."

        # STATE HYGIENE, at the one choke point an agent cannot route around.
        # A new lock is a session boundary, so the rig is cleaned of what the last
        # session left behind (TestRig/rig-reset.ps1). Deliberately placed AFTER
        # the owner id is printed: if the reset throws, the caller still knows the
        # id it needs to unlock with.
        #
        # Guarded on the function existing so rig-lock.ps1 stays usable, and
        # testable, on its own. The launchers dot-source both files.
        if (Get-Command Invoke-RigReset -ErrorAction SilentlyContinue) {
            try {
                Invoke-RigReset -KeepState:$KeepState -Reason 'lock acquisition' | Out-Null
            }
            catch {
                Write-Warning "[Lock] The rig state reset FAILED. You DO hold the lock (owner $($outcome.Owner)), but the rig may be half reset and is not safe to test on. Fix the cause, then release and re-take the lock: $Tool -Unlock -As $($outcome.Owner) followed by $Tool -Lock -Purpose `"...`". Re-asserting the lock you already hold does NOT reset."
                throw
            }
        }
        return $outcome.Owner
    }
}

function Update-RigLock {
    param(
        [Parameter(Mandatory)] [string] $CallerId,
        [Nullable[int]] $TtlMinutes
    )
    $msg = Invoke-WithRigLockMutex -Context 'refresh the rig lock' -Body {
        $lock = Read-RigLock
        if (-not $lock) { throw "No rig session lock to refresh. Acquire one: -Lock -Purpose `"<reason>`"." }
        if ($lock['owner'] -ne $CallerId) {
            throw "Refresh refused: the rig lock is held by owner '$($lock['owner'])' (purpose: $($lock['purpose'])), not '$CallerId'. Your reservation has lapsed. Report to the user; do not touch the rig. See TestRig/session.lock.template."
        }
        $lock['refreshed_at'] = Get-RigNowUtc
        if ($null -ne $TtlMinutes) { $lock['ttl_minutes'] = $TtlMinutes }
        Write-RigLock $lock
        "[RefreshLock] Refreshed (owner $CallerId, ttl $($lock['ttl_minutes']) min)."
    }
    Write-Host $msg
}

function Remove-RigLock {
    # Release the lock.
    #
    # -Force is the routine same-session override and covers exactly one refusal:
    # releasing while a listen-host instance is still live. It NEVER breaks
    # another session's lock; that is -BreakLock, and it is human-gated. The
    # ownership check therefore runs first, so -Force alone can never take a lock
    # off somebody else.
    #
    # The host refusal exists because a live host owns a world that other
    # instances are connected to. Releasing the rig out from under it invites the
    # next agent to -Stop -All, and the world goes down mid-test with nothing left
    # to say it happened.
    param(
        [string] $CallerId,
        [switch] $BreakLock,
        [switch] $Force
    )
    # Probe outside the critical section.
    $busy = Get-RigBusySignal

    $msg = Invoke-WithRigLockMutex -Context 'release the rig lock' -Body {
        $lock = Read-RigLock
        if (-not $lock) { return "[Unlock] No rig session lock present." }
        if (-not ($CallerId -and $lock['owner'] -eq $CallerId) -and -not $BreakLock) {
            throw "Unlock refused: the rig lock is held by owner '$($lock['owner'])' (purpose: $($lock['purpose'])), not '$CallerId'. Report to the user. Only the user may authorize -BreakLock. See TestRig/session.lock.template."
        }
        if ($busy.HostLive -and -not $Force) {
            throw "Unlock refused: a listen-host instance is still live ($($busy.HostNames -join ', ')). Releasing now leaves a hosted world running with no session owning it, and the next agent's -Stop -All takes it down mid-test. Stop the instances first (client-rig.ps1 -Stop -All -As <id>), or pass -Force if you really mean to release while it runs. Rig state: $($busy.Detail)"
        }
        Remove-RigLockFile
        "[Unlock] Rig session lock released (was owner $($lock['owner']))."
    }
    Write-Host $msg
}

function Test-RigLockReleasableOnStop {
    # The -Stop -Release predicate, in the library so both launchers share it and
    # so it can be pinned by a test.
    #
    # ORDERING DEPENDENCY, DO NOT REORDER: this predicate has NO busy term, so on
    # its own it will happily release a foreign lock whose timer has lapsed even
    # while the rig is mid-test. That is safe only because -Stop calls
    # Get-RigLockState FIRST, and that call's expired-and-busy branch self-renews
    # the lock and reports LiveForeign, so -Stop throws before it ever reaches
    # here. Swap the two and a busy foreign session loses its lock to an unrelated
    # -Stop -Release. TestRig/rig-lock.tests.ps1 pins both halves of this.
    param(
        $Lock,
        [string] $CallerId,
        [switch] $BreakLock
    )
    if (-not $Lock) { return $true }                                  # nothing to release
    if ($CallerId -and $Lock['owner'] -eq $CallerId) { return $true }  # yours
    if ($BreakLock) { return $true }                                   # human-authorized
    return (Test-RigLockTimerExpired $Lock)                            # already dead
}

# ---- status ---------------------------------------------------------------

function Write-RigLockStatus {
    # One block BOTH launchers print from their -Status action, so the same
    # reservation reads identically whichever half you ask. Reading it from one
    # place is the point: a second copy would be the first thing to drift.
    param([string] $CallerId)
    $lock = Read-RigLock
    if (-not $lock) {
        Write-Host "rig lock:     none"
        return
    }
    $expired = Test-RigLockTimerExpired $lock
    $own = if ($CallerId -and $lock['owner'] -eq $CallerId) { 'YOURS' }
           elseif ($CallerId) { "held by another session ($($lock['owner']))" }
           else { "owner $($lock['owner'])" }
    Write-Host "rig lock:     $own"
    Write-Host "  purpose:    $($lock['purpose'])"
    Write-Host "  timer:      $(if ($expired) { 'expired' } else { 'fresh' }); ttl $($lock['ttl_minutes']) min; refreshed $(Get-RigLockAgeText $lock)"
    $busy = Get-RigBusySignal
    if ($busy.Busy) {
        $note = if ($expired) { '  (lock still LIVE: rig is busy)' } else { '' }
        Write-Host "  rig busy:   $($busy.Detail)$note"
        if ($busy.HostLive) {
            Write-Host "  hosting:    $($busy.HostNames -join ', ')  (-Unlock refuses while a host is live; -Force overrides)"
        }
    }
    elseif ($expired) {
        Write-Host "  rig busy:   no; timer expired, so the lock is reclaimable"
    }
    Write-RigOrphanWarning
}

function Write-RigOrphanWarning {
    # Named separately so both launchers can call it from anywhere, not only from
    # the lock block. An orphan is never busy, so nothing else surfaces it.
    $orphans = @(Get-RigOrphanProcesses)
    if ($orphans.Count -eq 0) { return }
    Write-Warning "$($orphans.Count) UNTRACKED rig game process(es) are running. No pid file claims them, so no launcher action can stop them and they are NOT counted as busy. They still hold their control-plane and game ports, which is enough to make the next test bind different ports and assert against the wrong process."
    foreach ($o in $orphans) {
        $where = if ($o.Path) { $o.Path } else { '<image path unreadable>' }
        Write-Host "  orphan:     $($o.Name) pid $($o.ProcessId)  $where"
    }
    Write-Host "  orphan:     stop them with  Stop-Process -Id <pid> -Force"
}

# Default wiring: the real rig, rooted at this file's own folder. Resolved from
# the file and not from the caller, so either launcher gets the same lock no
# matter where it is invoked from.
Initialize-RigLockPaths -RigHome $PSScriptRoot
