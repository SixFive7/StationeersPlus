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
# =============================================================================

# TestRig/ - this file's own folder. Resolved from the file, not from the
# caller, so either launcher gets the same lock no matter where it is invoked.
$script:RigLockHome     = $PSScriptRoot
$script:RigLockFile = Join-Path $script:RigLockHome 'session.lock'
$script:RigLockRules = Join-Path $script:RigLockHome 'session.lock.template'

# Activity probes for each half. Paths are fixed relative to TestRig/, so the
# library can see whether EITHER half is busy regardless of which launcher
# dot-sourced it.
$script:RigDediServerPid = Join-Path $script:RigLockHome 'DedicatedServer\data\server.pid'
$script:RigDediServerLog = Join-Path $script:RigLockHome 'DedicatedServer\data\server.log'
$script:RigClientDataDir = Join-Path $script:RigLockHome 'ClientRig\data'

function Get-RigNowUtc {
    [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ss'Z'")
}

function Get-RigPidFromFile {
    param([string] $File)
    if (-not $File -or -not (Test-Path $File)) { return $null }
    $raw = (Get-Content -Raw -ErrorAction SilentlyContinue $File)
    if (-not $raw) { return $null }
    $val = $raw.Trim()
    if (-not $val) { return $null }
    $parsed = 0
    if (-not [int]::TryParse($val, [ref]$parsed)) { return $null }
    return $parsed
}

function Test-RigPidAlive {
    param([Nullable[int]] $TargetPid)
    if (-not $TargetPid) { return $false }
    [bool](Get-Process -Id $TargetPid -ErrorAction SilentlyContinue)
}

function Read-RigLock {
    # Returns an ordered hashtable of lock fields, or $null if no usable lock.
    if (-not (Test-Path $script:RigLockFile)) { return $null }
    $fields = [ordered]@{}
    foreach ($line in (Get-Content -ErrorAction SilentlyContinue $script:RigLockFile)) {
        $t = $line.Trim()
        if (-not $t -or $t.StartsWith('#')) { continue }
        $eq = $t.IndexOf('=')
        if ($eq -lt 1) { continue }
        $fields[$t.Substring(0, $eq).Trim()] = $t.Substring($eq + 1).Trim()
    }
    if (-not $fields.Contains('owner')) { return $null }
    return $fields
}

function Write-RigLock {
    param([Parameter(Mandatory)] $Fields)
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine('# Stationeers TestRig - ACTIVE session lock (auto-managed; do not hand-edit).')
    [void]$sb.AppendLine('# Covers BOTH halves: TestRig/DedicatedServer/ and TestRig/ClientRig/.')
    [void]$sb.AppendLine('# Mechanism and rules: session.lock.template (single source of truth).')
    foreach ($k in $Fields.Keys) {
        [void]$sb.AppendLine("$k=$($Fields[$k])")
    }
    $tmp = "$($script:RigLockFile).tmp"
    Set-Content -Path $tmp -Value $sb.ToString() -Encoding utf8 -NoNewline
    Move-Item -Path $tmp -Destination $script:RigLockFile -Force
}

function Test-RigLockTimerExpired {
    param([Parameter(Mandatory)] $Lock)
    $ttl = 10
    if ($Lock.Contains('ttl_minutes')) { [void][int]::TryParse($Lock['ttl_minutes'], [ref]$ttl) }
    if (-not $Lock.Contains('refreshed_at')) { return $true }
    try {
        $r = [DateTime]::Parse($Lock['refreshed_at'],
            [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal)
    } catch { return $true }
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

function Measure-PlayersInLog {
    # Pure helper: net connected-client count from a server.log-format file.
    # Each completed join logs "Client <name> (<id>) is ready"; each leave logs
    # "Client disconnected: ...". server.log truncates per launch, so the whole
    # file is the current run; net = (ready events) - (disconnected events).
    # Side-effect-free and takes an explicit path, so it can be unit-tested
    # offline against synthetic logs without a running server or a real client.
    param([Parameter(Mandatory)] [string] $Path)
    if (-not (Test-Path $Path)) { return 0 }
    $ready = 0
    $disc  = 0
    foreach ($line in (Get-Content -ErrorAction SilentlyContinue $Path)) {
        if ($line -match 'Client .*\) is ready') { $ready++ }
        elseif ($line -match 'Client disconnected:') { $disc++ }
    }
    $net = $ready - $disc
    if ($net -lt 0) { return 0 }
    return $net
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
    $reasons = @()

    if (Test-RigPidAlive (Get-RigPidFromFile $script:RigDediServerPid)) {
        $players = Measure-PlayersInLog $script:RigDediServerLog
        if ($players -ge 1) { $reasons += "$players player(s) connected to the dedicated server" }
    }

    if (Test-Path $script:RigClientDataDir) {
        $live = 0
        foreach ($pidFile in (Get-ChildItem -Path $script:RigClientDataDir -Filter 'game.pid' -Recurse -File -ErrorAction SilentlyContinue)) {
            if (Test-RigPidAlive (Get-RigPidFromFile $pidFile.FullName)) { $live++ }
        }
        if ($live -ge 1) { $reasons += "$live client instance(s) running" }
    }

    return [pscustomobject]@{
        Busy   = ($reasons.Count -gt 0)
        Detail = ($reasons -join '; ')
    }
}

function Get-RigLockState {
    # States: None, Mine, LiveForeign, DeadForeign.
    param([string] $CallerId)
    $lock = Read-RigLock
    if (-not $lock) { return [pscustomobject]@{ State = 'None'; Lock = $null; Busy = $null } }
    if ($CallerId -and $lock['owner'] -eq $CallerId) {
        return [pscustomobject]@{ State = 'Mine'; Lock = $lock; Busy = $null }
    }
    if (-not (Test-RigLockTimerExpired $lock)) {
        return [pscustomobject]@{ State = 'LiveForeign'; Lock = $lock; Busy = $null }
    }
    # Timer expired. Activity-aware tie-break before declaring it dead.
    $busy = Get-RigBusySignal
    if ($busy.Busy) {
        # The rig is in use: self-renew so a brief gap (a client restarting, a
        # player reconnecting) still gets a full TTL of grace.
        $lock['refreshed_at'] = Get-RigNowUtc
        Write-RigLock $lock
        return [pscustomobject]@{ State = 'LiveForeign'; Lock = $lock; Busy = $busy.Detail }
    }
    return [pscustomobject]@{ State = 'DeadForeign'; Lock = $lock; Busy = $null }
}

function Format-ForeignRigLock {
    param([Parameter(Mandatory)] $State)
    $lk = $State.Lock
    $busy = if ($State.Busy) { "; $($State.Busy)" } else { '' }
    return "    purpose : $($lk['purpose'])`n    owner   : $($lk['owner'])`n    active  : $(Get-RigLockAgeText $lk)$busy"
}

function Get-RigLockRulesPath { return $script:RigLockRules }
function Get-RigLockFilePath  { return $script:RigLockFile }

function Assert-RigLockHeld {
    # Gate for every mutating action on either half. Holding the lock refreshes
    # its timer, which is what makes "any mutating command also refreshes it"
    # true without a separate call.
    param(
        [Parameter(Mandatory)] [string] $Action,
        [string] $CallerId,
        [Parameter(Mandatory)] [string] $Tool   # 'dedicated-server.ps1' or 'client-rig.ps1'
    )
    $st = Get-RigLockState -CallerId $CallerId
    switch ($st.State) {
        'Mine' {
            $lk = $st.Lock
            $lk['refreshed_at'] = Get-RigNowUtc
            Write-RigLock $lk
            return
        }
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
    $lock = Read-RigLock
    if (-not $lock -or $lock['owner'] -ne $CallerId) { return }
    $lock['refreshed_at'] = Get-RigNowUtc
    Write-RigLock $lock
}

function New-RigLock {
    # Acquire, re-assert, or (human-authorized) break-and-take the rig lock.
    # Returns the owner id.
    param(
        [Parameter(Mandatory)] [string] $Purpose,
        [string] $CallerId,
        [int] $TtlMinutes = 10,
        [switch] $BreakLock,
        [Parameter(Mandatory)] [string] $Tool,
        [scriptblock] $OnReclaim
    )
    $st = Get-RigLockState -CallerId $CallerId
    switch ($st.State) {
        'Mine' {
            $owner = $st.Lock['owner']
            Write-RigLock ([ordered]@{
                owner = $owner; purpose = $Purpose
                acquired_at = $st.Lock['acquired_at']; refreshed_at = (Get-RigNowUtc)
                ttl_minutes = $TtlMinutes; host = $env:COMPUTERNAME
            })
            Write-Host "[Lock] Re-asserted the rig session lock (owner $owner). Pass -As $owner on mutating commands."
            return $owner
        }
        'LiveForeign' {
            if (-not $BreakLock) {
                throw "Cannot acquire: the test rig is locked by another session.`n$(Format-ForeignRigLock $st)`nReport this purpose to the user. Only the user may authorize -BreakLock. See TestRig/session.lock.template."
            }
            Write-Warning "[Lock] -BreakLock: breaking a live lock held by '$($st.Lock['purpose'])' (owner $($st.Lock['owner']))."
        }
        'DeadForeign' {
            if ($OnReclaim) { & $OnReclaim }
        }
    }
    $owner = [guid]::NewGuid().ToString('N').Substring(0, 8)
    Write-RigLock ([ordered]@{
        owner = $owner; purpose = $Purpose
        acquired_at = (Get-RigNowUtc); refreshed_at = (Get-RigNowUtc)
        ttl_minutes = $TtlMinutes; host = $env:COMPUTERNAME
    })
    Write-Host "[Lock] Acquired the rig session lock (covers BOTH TestRig halves)."
    Write-Host "[Lock]   owner   : $owner   (pass -As $owner on every mutating command, on either launcher)"
    Write-Host "[Lock]   purpose : $Purpose"
    Write-Host "[Lock]   ttl     : $TtlMinutes min (refresh with -RefreshLock -As $owner while actively testing)"
    Write-Host "[Lock] Rules: TestRig/session.lock.template."
    return $owner
}

function Update-RigLock {
    param(
        [Parameter(Mandatory)] [string] $CallerId,
        [Nullable[int]] $TtlMinutes
    )
    $lock = Read-RigLock
    if (-not $lock) { throw "No rig session lock to refresh. Acquire one: -Lock -Purpose `"<reason>`"." }
    if ($lock['owner'] -ne $CallerId) {
        throw "Refresh refused: the rig lock is held by owner '$($lock['owner'])' (purpose: $($lock['purpose'])), not '$CallerId'. Your reservation has lapsed. Report to the user; do not touch the rig. See TestRig/session.lock.template."
    }
    $lock['refreshed_at'] = Get-RigNowUtc
    if ($null -ne $TtlMinutes) { $lock['ttl_minutes'] = $TtlMinutes }
    Write-RigLock $lock
    Write-Host "[RefreshLock] Refreshed (owner $CallerId, ttl $($lock['ttl_minutes']) min)."
}

function Remove-RigLock {
    param(
        [string] $CallerId,
        [switch] $BreakLock
    )
    $lock = Read-RigLock
    if (-not $lock) { Write-Host "[Unlock] No rig session lock present."; return }
    if (-not ($CallerId -and $lock['owner'] -eq $CallerId) -and -not $BreakLock) {
        throw "Unlock refused: the rig lock is held by owner '$($lock['owner'])' (purpose: $($lock['purpose'])), not '$CallerId'. Report to the user. Only the user may authorize -BreakLock. See TestRig/session.lock.template."
    }
    Remove-Item -Force -ErrorAction SilentlyContinue $script:RigLockFile
    Write-Host "[Unlock] Rig session lock released (was owner $($lock['owner']))."
}

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
    }
    elseif ($expired) {
        Write-Host "  rig busy:   no; timer expired, so the lock is reclaimable"
    }
}
