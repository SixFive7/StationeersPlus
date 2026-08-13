# =============================================================================
# TestRig state hygiene - shared implementation
# =============================================================================
# Dot-sourced by BOTH launchers, immediately after rig-lock.ps1:
#     TestRig/testrig.ps1 (through TestRig/lib/server.ps1 and TestRig/lib/client.ps1)
#
# WHAT THIS IS FOR
#   One rig is shared by every agent on this machine, and the rig keeps state
#   between sessions: a save a previous test mutated, a config value a previous
#   test flipped through POST /config/set (which defaults to save:true), an
#   InspectorPlus request file that was never consumed and fires on the NEXT
#   launch, a log a -Grep assertion then matches a line out of. None of that is a
#   bug in any one test. It is the previous test's garbage failing the next one.
#
#   This file removes that garbage at the ONE choke point that already exists and
#   is already enforced in code: acquiring the session lock. An agent cannot get
#   the rig without getting it clean, and cannot route around it, because
#   Assert-RigLockHeld refuses every mutating action without a lock. A rule in a
#   document would be a rule an agent skips.
#
# WHAT IT DOES NOT DO, SAID PLAINLY
#   The reset runs BETWEEN sessions, and a session deliberately spans many
#   start/stop cycles. Two unrelated tests run under one lock therefore get NO
#   reset between them. Making hygiene per-test would make the unit of hygiene
#   smaller than the unit of ownership, which is a lock-model change and its own
#   piece of work. Until then: one lock, one test subject. If the next thing you
#   are about to test is unrelated to the last, release the lock and take it
#   again.
#
# WHY IT IS CHEAP ENOUGH TO DO ON EVERY ACQUISITION
#   The mutable surface of a client instance is two directories: data/<instance>/
#   and the instance's BepInEx/ real copy. The other ~1,050 files in the tree are
#   read-only hard links into the developer's install. So a full reset is a
#   handful of deletes plus a small config copy, not a re-provision.
#
# THE THREE FACTS THAT MAKE THIS DANGEROUS IF DONE CARELESSLY
#   1. Re-copying BepInEx/config WIPES SavePathOverride, and an instance without
#      it writes its worlds into the developer's tier-1 save folder. The re-apply
#      after the copy is the single most important line in this file, and
#      Set-RigSavePathOverride lives here (not in lib/client.ps1) so provisioning
#      and resetting write that redirect through ONE implementation. Two copies
#      of a tier-1 safety write is exactly the drift that overwrites somebody's
#      saves.
#   2. A pid file is not proof of life and not proof of death. Windows recycles
#      process ids and these files outlive their processes on a force-kill or a
#      reboot, so every pid here goes through the same process-image check
#      rig-lock.ps1 uses. A live instance's game.pid must survive; a recycled id
#      must not be trusted in either direction.
#   3. Deleting a dedicated-server world is the ONLY irreversible thing here, and
#      it is hundreds of megabytes of somebody's test state. See the next block.
#
# A WORLD'S LIFETIME IS SESSION-SCOPED, AND THE SESSION SAYS SO ITSELF
#   TestRig/session.dirty is written before a session's first mutating action and
#   records the dedicated-server world set as it stood at that moment. This file
#   deletes a world if and only if the marker recorded a set and the world is not
#   in it, which makes the rule exactly "this session created it". A world that
#   was on the rig when the session started is ALWAYS kept.
#
#   The baseline used to decide this, and it was wrong in a way that read as safe.
#   Test-RigBaselineStale inspects the game version, the instance-name set and
#   files of class 'payload'; it never looks at class 'world'. So staging a world
#   deliberately (copying a tier-2 source over tier 3, which is what the repo's
#   save rules prescribe for restoring a save under test) left the baseline
#   reading FRESH while the staged world was absent from it, and the next session
#   boundary deleted the very thing the test was about.
#
#   Every way of not getting a clean answer out of the marker keeps every world
#   and names which way it was: no marker, an unreadable one, one with no world
#   set (written before this existed), or one from before the last reboot. Keeping
#   a stale world costs a manual delete; deleting a live one costs the test.
#   Get-RigSessionWorldSnapshot in rig-lock.ps1 is where those four cases live.
#
# WHAT IT MAY TOUCH, AND WHAT IT MAY NOT
#   Writes are confined to two places: the rig's own state under TestRig/, and the
#   per-instance BepInEx tree of each provisioned client instance. That second one
#   is normally NOT under TestRig/, because hard links cannot cross volumes and the
#   trees therefore sit on the game install's drive; where each one went is read
#   from the launcher's registry (Get-RigClientInstanceRootMap), never guessed.
#   Two reads leave both places and both are read-only: copying BepInEx/config out
#   of the source install, and the shared-state snapshot (PlayerCookie-v2.xml, the
#   PlayerPrefs key, Blueprints/). The developer's save folder is tier 1 and is not
#   touched at all.
#
# SHARED STATE IS REPORTED, NEVER RESTORED
#   PlayerCookie-v2.xml, HKCU\Software\Rocketwerkz\rocketstation and Blueprints\
#   cannot be isolated: persistentDataPath is fixed inside globalgamemanagers.
#   Writing them back would itself be the forbidden write, so this file only
#   snapshots them at lock time and prints the delta at unlock. That converts
#   "invisible until a test fails" into "named at the session boundary", which is
#   the honest half of the guarantee.
#
# Everything here is prefixed Rig* so dot-sourcing cannot collide with a
# launcher's own helpers. rig-lock.ps1 is dot-sourced first and its helpers
# (Get-RigLiveProcess, Get-RigPidFromFile, Get-RigBusySignal, Get-RigNowUtc) are
# reused rather than duplicated.
#
# Tests: TestRig/rig-reset.tests.ps1 (offline, no game, no network, entirely
# against a temp directory through Initialize-RigResetPaths). Run it after any
# change here, together with TestRig/rig-lock.tests.ps1.
# =============================================================================

# ---- paths ----------------------------------------------------------------
# Every path the library uses is set in one place so the whole mechanism can be
# pointed at a temp directory by the test suite, exactly like the lock library.
# Called at dot-source time with this file's own folder, so a launcher sees the
# real rig with no extra wiring.

function Initialize-RigResetPaths {
    # The parameter is -RigHome and not -Home because $HOME is a read-only
    # automatic variable and a parameter of that name cannot be bound at all.
    #
    # This ALSO re-points the lock library at the same home. The reset asks the
    # lock library whether the rig is busy, and a reset pointed at a temp tree
    # while the busy probe still watched the real rig would be a reset making
    # decisions about the wrong machine.
    param(
        [Parameter(Mandatory)] [string] $RigHome,
        [string] $SourceInstall,
        [string] $InstanceRoot,
        [string] $UserDataDir,
        [string] $SharedDataDir,
        [string] $PlayerPrefsKey = 'HKCU:\Software\Rocketwerkz\rocketstation',
        [string] $ServerImageName = 'rocketstation_DedicatedServer',
        [string] $ClientImageName = 'rocketstation',
        [string[]] $HostWrapperImageNames = @('pwsh', 'powershell')
    )

    $script:RigResetHome = $RigHome

    # Client half. The instance root here is the FALLBACK: each instance's own root is read from the
    # launcher's registry (Get-RigClientInstanceRootMap), because -InstancesRoot is a launcher flag
    # this library cannot see and instances routinely live on the game install's volume. This value
    # is what an entry that records no root gets, and what the lock library's orphan scan watches.
    $script:RigResetClientData = Join-Path $RigHome 'ClientRig\data'
    $script:RigResetClientInstances =
        if     ($InstanceRoot)                   { $InstanceRoot }
        elseif ($env:STATIONEERS_CLIENTRIG_ROOT) { $env:STATIONEERS_CLIENTRIG_ROOT }
        else                                     { Join-Path $RigHome 'ClientRig\instances' }

    # Server half.
    $script:RigResetDediInstall = Join-Path $RigHome 'DedicatedServer\install'
    $script:RigResetDediData    = Join-Path $RigHome 'DedicatedServer\data'

    # State this file owns, all inside TestRig/ and all gitignored by the
    # deny-all rule on the rig root.
    $script:RigResetStateFile   = Join-Path $RigHome 'session.state.json'
    $script:RigResetBaselineDir = Join-Path $RigHome 'baseline'

    # Read-only sources OUTSIDE the rig. Left unresolved rather than guessed: a
    # reset that cannot find the source install skips the config re-copy loudly
    # instead of inventing a path.
    $script:RigResetSourceInstall = $SourceInstall
    $script:RigResetUserDataDir   = if ($UserDataDir) { $UserDataDir }
                                    else { Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'My Games\Stationeers' }
    $script:RigResetSharedDataDir = if ($SharedDataDir) { $SharedDataDir }
                                    else { Join-Path $env:USERPROFILE 'AppData\LocalLow\Rocketwerkz\rocketstation' }
    $script:RigResetPlayerPrefsKey = $PlayerPrefsKey

    # Process identity, so the pid checks can be exercised offline.
    $script:RigResetServerImage = $ServerImageName
    $script:RigResetClientImage = $ClientImageName
    $script:RigResetHostImages  = $HostWrapperImageNames

    # Re-point the LOCK library at the same home and the same instance root. The
    # reset asks it whether the rig is busy, and the two must never be looking at
    # different trees: a reset that consulted the real rig's busy signal before
    # deleting inside a temp tree (or the reverse) would be making its one safety
    # decision about the wrong machine. This also fixes the instance root when a
    # launcher was given -InstancesRoot, which neither library can guess.
    Initialize-RigLockPaths -RigHome $RigHome `
        -ServerImageName $ServerImageName -ClientImageName $ClientImageName `
        -InstanceRoot $script:RigResetClientInstances
}

function Get-RigResetHomePath      { return $script:RigResetHome }
function Get-RigResetStateFilePath { return $script:RigResetStateFile }
function Get-RigBaselineDirPath    { return $script:RigResetBaselineDir }
function Get-RigBaselineFilePath   { return (Join-Path $script:RigResetBaselineDir 'manifest.json') }
function Get-RigBaselineStoreDir   { return (Join-Path $script:RigResetBaselineDir 'content') }

function Get-RigResetSourceInstall {
    # The developer's Stationeers install, used ONLY as a read-only source for
    # BepInEx/config. Explicit wiring wins; otherwise it comes from
    # Directory.Build.props at the repo root, which is where both launchers read
    # it from and the one place in this repository that knows it.
    if ($script:RigResetSourceInstall) {
        if (Test-Path -LiteralPath (Join-Path $script:RigResetSourceInstall 'BepInEx\config')) {
            return $script:RigResetSourceInstall
        }
        return $null
    }
    $repoRoot = Split-Path -Parent $script:RigResetHome
    if (-not $repoRoot) { return $null }
    $props = Join-Path $repoRoot 'Directory.Build.props'
    if (-not (Test-Path -LiteralPath $props)) { return $null }
    try {
        $xml  = [xml](Get-Content -Raw -LiteralPath $props -ErrorAction Stop)
        $path = $xml.Project.PropertyGroup.StationeersPath
        if ([string]::IsNullOrWhiteSpace($path)) { return $null }
        $path = ([string]$path).Trim()
        if (-not (Test-Path -LiteralPath (Join-Path $path 'BepInEx\config'))) { return $null }
        return $path
    }
    catch { return $null }
}

# ===========================================================================
# THE BASELINE: what "clean" actually means, written down
# ===========================================================================
# Before this existed, "clean" was a hardcoded list of things to delete inside
# this file. That is enough to remove obvious garbage and not enough to answer
# the question that matters: what SHOULD the rig look like. The visible
# consequence was that server configs were left alone with the comment
# "rig-owned versus mod-owned is undecided", because nothing knew what their
# correct values were.
#
# A baseline is a capture of the rig at a moment somebody declared correct. With
# one, that question has an answer: a config's baseline bytes ARE its correct
# value.
#
# WHAT A BASELINE DOES NOT DECIDE: WORLDS. It once did, and the argument was
# "a world absent from the baseline is this session's". That is false whenever a
# world is staged deliberately after a capture, and staleness cannot notice
# because it never looks at class 'world'. Worlds are now decided by the session
# marker instead (see the block at the top of this file); their records here are
# kept for the record only, and nothing reads them to delete anything.
#
# WHAT IS STORED, AND WHY NOT EVERYTHING
#   Two representations were on the table.
#
#   A FULL COPY of the mutable surface, restored by mirroring it back. Rejected.
#   It is gigabytes (the dedicated server's worlds and every instance's seeded
#   mods), it is slow enough that nobody would re-capture it often, and worst of
#   all it would silently roll back deployed mod builds: every -DeployMods would
#   have to be followed by a re-capture or the next restore would put the old
#   DLL back, and "my fix is not in the game" is the quietest possible failure.
#
#   A MANIFEST of paths, sizes and hashes, restored by the existing surgical
#   deletes. Also not enough on its own: a manifest can detect that a config file
#   changed, but it cannot put the old bytes back.
#
#   So this is both, split by class:
#     config   small, rig-owned, restorable: every *.cfg under an instance's
#              BepInEx/config and under the dedicated server's, plus each
#              modconfig.xml. Bytes ARE stored, and the restore copies them back.
#              Kilobytes in total.
#     payload  hashed and inventoried, never stored and never restored: deployed
#              plugins and seeded mods. This is the class where rolling back
#              would undo a deliberate deploy, so it is only ever REPORTED.
#     worlds   dedicated-server saves, recorded by name and size, never hashed
#              (a world is hundreds of megabytes) and never stored. INFORMATIONAL
#              ONLY: they document what was on the rig when somebody declared it
#              correct, and nothing reads them back. They are still captured
#              because they cost a directory listing and answer "what was there",
#              which is the question a human asks of an old baseline.
#
# STALENESS IS LOUD, NEVER SILENT
#   A baseline captured before a game update or a mod rebuild describes a rig
#   that no longer exists. That is reported at every acquisition, names the exact
#   reason, and names -CaptureBaseline as the fix. It does NOT block the lock:
#   an unclean rig must not become an unlockable one, and a stale baseline is
#   still better than none. Staleness no longer changes what happens to a world
#   either way: worlds are not the baseline's business any more, and a stale
#   baseline restores configs exactly as a fresh one does.

function Get-RigGameVersion {
    # The game version string. Used as the baseline's staleness anchor: when this
    # moves, every config default and every plugin in the rig may have moved too.
    #
    # It lives in StreamingAssets\version.ini, whose first line reads
    # "UPDATEVERSION=Update 0.2.6420.27780". The data folder is named for the
    # product, so the client and the dedicated server each have their own, and
    # both are checked because this function serves both halves.
    #
    # This used to read a "version.txt" at the install root. No such file has
    # ever existed, so the function returned 'unknown' every time, and the
    # caller in Test-RigBaselineStale skips its comparison on 'unknown'. The
    # net effect was that a game update could never mark a baseline stale, which
    # is the one thing the anchor exists to catch. Found on 2026-08-12, when
    # 0.2.6420.27780 shipped and the rig noticed nothing: both client instances
    # silently kept July binaries because the provision hard-links had broken,
    # and -Status reported them healthy throughout.
    param([string] $SourceInstall)
    if (-not $SourceInstall) { $SourceInstall = Get-RigResetSourceInstall }
    if (-not $SourceInstall) { return 'unknown' }
    foreach ($dataDir in @('rocketstation_Data', 'rocketstation_DedicatedServer_Data')) {
        try {
            $v = Join-Path $SourceInstall (Join-Path $dataDir 'StreamingAssets\version.ini')
            if (-not (Test-Path -LiteralPath $v)) { continue }
            # -TotalCount 1: the file is the whole changelog, ~170 KB, and only
            # its first line carries the version.
            $line = (Get-Content -LiteralPath $v -TotalCount 1 -ErrorAction Stop)
            if (-not $line) { continue }
            # "UPDATEVERSION=Update <version>". Both the key and the word Update
            # are optional in the match so a format tweak degrades to the raw
            # value rather than to 'unknown'.
            if ($line -match '(\d+(?:\.\d+)+)') { return $Matches[1] }
            $t = ($line -replace '^\s*UPDATEVERSION\s*=\s*', '').Trim()
            if ($t) { return $t }
        } catch { }
    }
    return 'unknown'
}

function Get-RigFileHash {
    param([string] $Path)
    try { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash }
    catch { return $null }
}

function New-RigSurfaceRecord {
    param(
        [Parameter(Mandatory)] [string] $Key,
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [ValidateSet('config', 'payload', 'world')] [string] $Class,
        [string] $Half = '',
        [string] $Instance = ''
    )
    [pscustomobject]@{ Key = $Key; Path = $Path; Class = $Class; Half = $Half; Instance = $Instance }
}

function Get-RigMutableSurface {
    # Every file the baseline has an opinion about, with a stable key.
    #
    # ONE definition, used by the capture and by the restore, so the two can
    # never disagree about what the surface is. The key is deliberately a
    # rig-relative string rather than an absolute path: instance trees live on
    # whichever volume the launcher put them on, and a baseline that stopped
    # matching after an -InstancesRoot change would be worse than none.
    #
    # NOT included, on purpose: logs, caches, InspectorPlus requests and
    # snapshots, pid files, imgui.ini, setting.xml and the client save roots.
    # Every one of those is unconditionally deleted by the reset, so recording
    # what they looked like says nothing about whether the rig is clean. The ~1,050
    # hard links per instance are not included either, and must never be: they
    # share file data with the developer's install, so copying or writing one
    # reaches into a read-only tree.
    #
    # THIS IS AN ALLOW-LIST, AND THAT IS THE POINT, NOT AN OVERSIGHT.
    #   The reset only ever acts on paths that appear here or in the hardcoded
    #   target list, so anything an agent deliberately puts ANYWHERE ELSE in an
    #   instance tree survives every restore untouched and is never reported as
    #   drift to be scrubbed. That is what makes a deliberate, instance-scoped
    #   environment change expressible at all: dropping a real (never hard-linked)
    #   assembly into an instance's own rocketstation_Data\Managed\ to fix a
    #   per-instance load failure, for example, is a permanent property of that
    #   instance, not this session's garbage, and nothing here will remove it or
    #   call it a difference from stock.
    #   A deny-list would have the opposite default and would quietly delete
    #   exactly those deliberate changes, which is why this is not one. If a
    #   future class of file DOES need restoring, add it here explicitly, with a
    #   class, rather than widening the sweep.
    $out     = New-Object System.Collections.Generic.List[object]
    $rootMap = Get-RigClientInstanceRootMap

    foreach ($name in (Get-RigResetInstanceNames)) {
        $data = Join-Path $script:RigResetClientData $name
        $tree = (Get-RigInstanceTree -Name $name -RootMap $rootMap).Path
        $bep  = Join-Path $tree 'BepInEx'

        $cfgDir = Join-Path $bep 'config'
        foreach ($f in @(Get-ChildItem -LiteralPath $cfgDir -Filter '*.cfg' -File -ErrorAction SilentlyContinue)) {
            $out.Add((New-RigSurfaceRecord -Key "client/$name/bepinex-config/$($f.Name)" -Path $f.FullName -Class 'config' -Half 'client' -Instance $name))
        }
        $mc = Join-Path $data 'userdata\modconfig.xml'
        if (Test-Path -LiteralPath $mc) {
            $out.Add((New-RigSurfaceRecord -Key "client/$name/modconfig.xml" -Path $mc -Class 'config' -Half 'client' -Instance $name))
        }
        foreach ($f in @(Get-ChildItem -LiteralPath (Join-Path $bep 'plugins') -Recurse -File -ErrorAction SilentlyContinue)) {
            $rel = $f.FullName.Substring((Join-Path $bep 'plugins').Length).TrimStart('\', '/').Replace('\', '/')
            $out.Add((New-RigSurfaceRecord -Key "client/$name/plugins/$rel" -Path $f.FullName -Class 'payload' -Half 'client' -Instance $name))
        }
        foreach ($f in @(Get-ChildItem -LiteralPath (Join-Path $data 'userdata\mods') -Recurse -File -ErrorAction SilentlyContinue)) {
            $rel = $f.FullName.Substring((Join-Path $data 'userdata\mods').Length).TrimStart('\', '/').Replace('\', '/')
            $out.Add((New-RigSurfaceRecord -Key "client/$name/mods/$rel" -Path $f.FullName -Class 'payload' -Half 'client' -Instance $name))
        }
    }

    $install = $script:RigResetDediInstall
    foreach ($f in @(Get-ChildItem -LiteralPath (Join-Path $install 'BepInEx\config') -Filter '*.cfg' -File -ErrorAction SilentlyContinue)) {
        $out.Add((New-RigSurfaceRecord -Key "server/bepinex-config/$($f.Name)" -Path $f.FullName -Class 'config' -Half 'server'))
    }
    $smc = Join-Path $install 'modconfig.xml'
    if (Test-Path -LiteralPath $smc) {
        $out.Add((New-RigSurfaceRecord -Key 'server/modconfig.xml' -Path $smc -Class 'config' -Half 'server'))
    }
    foreach ($f in @(Get-ChildItem -LiteralPath (Join-Path $install 'BepInEx\plugins') -Recurse -File -ErrorAction SilentlyContinue)) {
        $rel = $f.FullName.Substring((Join-Path $install 'BepInEx\plugins').Length).TrimStart('\', '/').Replace('\', '/')
        $out.Add((New-RigSurfaceRecord -Key "server/plugins/$rel" -Path $f.FullName -Class 'payload' -Half 'server'))
    }
    # The world set comes from Get-RigServerWorlds (rig-lock.ps1) rather than a
    # second enumeration here. The session marker records the SAME set with the
    # SAME keys, and a world is deleted on the two agreeing, so there must not be
    # two places that decide what a world is called. Both libraries are pointed at
    # one rig home by Initialize-RigResetPaths, so they cannot be looking at
    # different save trees either.
    foreach ($w in @(Get-RigServerWorlds)) {
        $out.Add((New-RigSurfaceRecord -Key $w.Key -Path $w.Path -Class 'world' -Half 'server'))
    }
    # PLAIN array, deliberately NOT comma-wrapped, and the difference matters.
    # `return ,$arr` protects a single-element result from being unrolled into a
    # scalar, which is right for a list of STRINGS a caller indexes directly (see
    # Compare-RigSharedState). It is wrong here, because every caller writes
    # @(Get-RigMutableSurface): @() around a comma-wrapped array keeps the outer
    # wrapper, so the caller gets ONE element that is the whole array, and
    # $rec.Key then evaluates to an array of keys through member enumeration. The
    # failure is loud in a parameter binder but silent anywhere it is only tested
    # for truthiness. Returning the plain array makes @() correct and an empty
    # result an empty array.
    return $out.ToArray()
}

function Get-RigBaselineStoredPath {
    # Where a config file's captured bytes live. The key is turned into a flat,
    # safe file name rather than a nested path, because a key contains characters
    # (and a depth) that do not survive being pasted into a directory tree.
    param([Parameter(Mandatory)] [string] $Key)
    $sha  = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($Key.ToLowerInvariant()))
    $hex  = [System.BitConverter]::ToString($sha[0..7]).Replace('-', '')
    $leaf = Split-Path -Leaf $Key
    return (Join-Path (Get-RigBaselineStoreDir) "$hex-$leaf")
}

function Get-RigBaseline {
    # The captured baseline, or $null. Returns the parsed manifest with its Files
    # flattened into a hashtable keyed the same way Get-RigMutableSurface keys the
    # live rig, so a lookup is one indexer and never a scan.
    if (-not $script:RigResetBaselineDir) { return $null }
    $manifest = Get-RigBaselineFilePath
    if (-not (Test-Path -LiteralPath $manifest)) { return $null }
    try { $raw = (Get-Content -Raw -LiteralPath $manifest -ErrorAction Stop) | ConvertFrom-Json -ErrorAction Stop }
    catch { return $null }
    if (-not $raw) { return $null }
    $byKey = @{}
    foreach ($f in @($raw.files)) {
        if ($null -eq $f -or -not $f.PSObject.Properties['key']) { continue }
        $byKey[[string]$f.key] = $f
    }
    return [pscustomobject]@{
        CapturedUtc   = [string]$raw.capturedUtc
        CapturedBy    = [string]$raw.capturedBy
        GameVersion   = [string]$raw.gameVersion
        SourceInstall = [string]$raw.sourceInstall
        Host          = [string]$raw.host
        Instances     = @($raw.instances)
        Files         = $byKey
        Raw           = $raw
    }
}

function Test-RigBaselineStale {
    # Is the baseline still describing this rig. Reasons are returned rather than
    # summarised, because "stale" on its own tells an agent nothing about whether
    # it should re-capture or investigate.
    param($Baseline)
    if (-not $Baseline) { $Baseline = Get-RigBaseline }
    $reasons = New-Object System.Collections.Generic.List[string]
    if (-not $Baseline) {
        return [pscustomobject]@{ Present = $false; Stale = $true; Reasons = @('no baseline has ever been captured') }
    }

    $nowVersion = Get-RigGameVersion
    if ($Baseline.GameVersion -and $nowVersion -ne 'unknown' -and $Baseline.GameVersion -ne $nowVersion) {
        $reasons.Add("the game moved from $($Baseline.GameVersion) to $nowVersion since the baseline was captured")
    }

    $now  = @(Get-RigResetInstanceNames)
    $then = @($Baseline.Instances | ForEach-Object { [string]$_ })
    foreach ($n in $now)  { if ($then -notcontains $n) { $reasons.Add("instance '$n' exists now and was not in the baseline") } }
    foreach ($n in $then) { if ($now  -notcontains $n) { $reasons.Add("instance '$n' was in the baseline and is gone now") } }

    # A payload the baseline never saw, or one whose bytes moved, means somebody
    # deployed a plugin or re-seeded a mod. That is legitimate and deliberate, and
    # it is exactly the moment the baseline needs re-taking, so it is a staleness
    # reason and never an action.
    $moved = 0
    foreach ($rec in (Get-RigMutableSurface)) {
        if ($rec.Class -ne 'payload') { continue }
        $b = $Baseline.Files[$rec.Key]
        if (-not $b) { $moved++; continue }
        if ((Get-RigFileHash $rec.Path) -ne [string]$b.sha256) { $moved++ }
    }
    if ($moved -gt 0) { $reasons.Add("$moved deployed plugin or seeded mod file(s) differ from the baseline (a rebuild or a re-seed since it was captured)") }

    return [pscustomobject]@{
        Present = $true
        Stale   = ($reasons.Count -gt 0)
        Reasons = $reasons.ToArray()
    }
}

function New-RigBaselineCapture {
    # Declare the rig as it stands to be the definition of clean.
    #
    # This is the ONLY way the baseline changes, and it is an explicit action on
    # both launchers (-CaptureBaseline) rather than something that happens on its
    # own. A baseline that re-captured itself automatically would happily bless
    # whatever mess triggered the capture, and the next agent would inherit it as
    # "correct" with nothing to say otherwise.
    #
    # It refuses on a busy rig, because a config half-written by a running game is
    # not a definition of anything. -Force overrides that, in the ordinary sense
    # -Force has everywhere in this rig: it waves away a refusal inside your own
    # session, and never touches anybody else's lock.
    param(
        [string] $CapturedBy = '',
        [switch] $Force,
        [switch] $WhatIf
    )
    $gate = Test-RigResetAllowed
    if (-not $gate.Allowed -and -not $Force) {
        throw "Refusing to capture a baseline while the rig is in use ($($gate.Reason)). A config file the game is holding open, or a world mid-save, is not a definition of 'clean'. Stop what is running and capture again, or pass -Force if you are certain the running thing cannot write to the rig."
    }
    if (-not $gate.Allowed) {
        Write-Warning "[Baseline] -Force: capturing while the rig is in use ($($gate.Reason)). Whatever those processes have half-written is about to become the definition of a clean rig."
    }

    $surface = @(Get-RigMutableSurface)
    $files   = New-Object System.Collections.Generic.List[object]
    $stored  = 0
    foreach ($rec in $surface) {
        $entry = [ordered]@{ key = $rec.Key; class = $rec.Class; half = $rec.Half; instance = $rec.Instance }
        if ($rec.Class -eq 'world') {
            # Recorded, never hashed (a world is hundreds of MB), never stored,
            # and INFORMATIONAL: no restore reads this back. A world's lifetime is
            # decided by the session marker, which knows what predates the session
            # rather than what predates a capture somebody took last month.
            $bytes = 0
            $m = Get-ChildItem -LiteralPath $rec.Path -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum
            if ($m.Sum) { $bytes = [long]$m.Sum }
            $entry['bytes']  = $bytes
            $entry['sha256'] = ''
        }
        else {
            $fi = Get-Item -LiteralPath $rec.Path -ErrorAction SilentlyContinue
            $entry['bytes']  = if ($fi) { [long]$fi.Length } else { 0 }
            $entry['sha256'] = [string](Get-RigFileHash $rec.Path)
            if ($rec.Class -eq 'config' -and -not $WhatIf) {
                $dest = Get-RigBaselineStoredPath -Key $rec.Key
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
                Copy-Item -LiteralPath $rec.Path -Destination $dest -Force -ErrorAction Stop
                $stored++
            }
            elseif ($rec.Class -eq 'config') { $stored++ }
        }
        $files.Add([pscustomobject]$entry)
    }

    $record = [ordered]@{
        capturedUtc   = (Get-RigNowUtc)
        capturedBy    = $CapturedBy
        gameVersion   = (Get-RigGameVersion)
        sourceInstall = [string](Get-RigResetSourceInstall)
        host          = $env:COMPUTERNAME
        instances     = @(Get-RigResetInstanceNames)
        files         = $files.ToArray()
    }

    if ($WhatIf) {
        Write-Host "[Baseline] -WhatIf: nothing was written. A capture would record $($files.Count) entries ($stored config file(s) stored by content)."
        return [pscustomobject]@{ WhatIf = $true; Entries = $files.Count; Stored = $stored; Record = $record }
    }

    # Drop any stored content the new capture did not re-write, so the store
    # never accumulates files from instances that no longer exist.
    $keep = @{}
    foreach ($rec in $surface) { if ($rec.Class -eq 'config') { $keep[(Get-RigBaselineStoredPath -Key $rec.Key)] = $true } }
    foreach ($f in @(Get-ChildItem -LiteralPath (Get-RigBaselineStoreDir) -File -ErrorAction SilentlyContinue)) {
        if (-not $keep.ContainsKey($f.FullName)) { Remove-Item -LiteralPath $f.FullName -Force -ErrorAction SilentlyContinue }
    }

    New-Item -ItemType Directory -Force -Path $script:RigResetBaselineDir | Out-Null
    ($record | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath (Get-RigBaselineFilePath) -Encoding utf8

    $byClass = @($files | Group-Object Class | ForEach-Object { "$($_.Count) $($_.Name)" }) -join ', '
    Write-Host "[Baseline] Captured the rig as the new definition of clean."
    Write-Host "[Baseline]   file      : $(Get-RigBaselineFilePath)"
    Write-Host "[Baseline]   game      : $($record.gameVersion)"
    Write-Host "[Baseline]   instances : $(if ($record.instances.Count) { $record.instances -join ', ' } else { 'none' })"
    Write-Host "[Baseline]   recorded  : $($files.Count) entries ($byClass)"
    Write-Host "[Baseline]   stored    : $stored config file(s) kept by content, so a restore can put their exact bytes back"
    Write-Host "[Baseline] Plugins and seeded mods are recorded but NEVER restored: rolling one back would undo a deliberate deploy."
    Write-Host "[Baseline] Worlds are recorded for the record only. What happens to a world is decided by TestRig/session.dirty, which records the worlds that predate each session; a capture does not protect a world and never did reliably."
    Write-Host "[Baseline] Re-capture after a game update, a mod rebuild or a re-provision. Until then the rig restores to THIS."
    return [pscustomobject]@{ WhatIf = $false; Entries = $files.Count; Stored = $stored; Record = $record }
}

function Compare-RigBaselineConfig {
    # Config-class drift only, which is cheap (a handful of small files) and is
    # the class the restore can actually act on. Payload drift is deliberately not
    # computed here: it belongs to staleness, not to a restore.
    param($Baseline)
    if (-not $Baseline) { $Baseline = Get-RigBaseline }
    $out = New-Object System.Collections.Generic.List[string]
    if (-not $Baseline) { return , $out.ToArray() }
    $seen = @{}
    foreach ($rec in (Get-RigMutableSurface)) {
        if ($rec.Class -ne 'config') { continue }
        $seen[$rec.Key] = $true
        $b = $Baseline.Files[$rec.Key]
        if (-not $b)                                        { $out.Add("$($rec.Key) : new since the baseline") }
        elseif ((Get-RigFileHash $rec.Path) -ne [string]$b.sha256) { $out.Add("$($rec.Key) : contents changed since the baseline") }
    }
    foreach ($k in @($Baseline.Files.Keys | Sort-Object)) {
        if ([string]$Baseline.Files[$k].class -ne 'config') { continue }
        if (-not $seen.ContainsKey($k)) { $out.Add("$k : in the baseline, missing now") }
    }
    # Comma-wrapped: PowerShell unrolls a returned array, so a single-line report
    # would come back as a bare string and a caller indexing [0] would get its
    # first CHARACTER. The same trap Compare-RigSharedState documents.
    return , $out.ToArray()
}

# ---- the tier-1 safety write ----------------------------------------------

function Set-RigSavePathOverride {
    # Points an instance at its OWN user-data root, which is the single thing
    # standing between a driven session and the developer's tier-1 save folder.
    #
    # It lives HERE, in the shared file, because two callers need it and they
    # must not drift: Invoke-Provision writes it when an instance is built, and
    # the reset re-writes it after re-copying BepInEx/config, which WIPES it. Two
    # copies of this function is how one of them quietly stops matching the other
    # and an instance ends up writing worlds into the developer's saves.
    #
    # SavePathOverride moves StationSaveUtils.DefaultPath itself, which is the
    # only lever that also separates modconfig.xml.
    #
    # DO NOT reach for the launch flag "-settings SavePath" instead. It moves the
    # save tree but NOT DefaultPath, so StationeersLaunchPad scans an empty
    # <SavePath>\mods\, finds nothing, and rewrites the DEVELOPER'S SHARED
    # modconfig.xml with every <Local> entry deleted. Observed on a first boot:
    # five local mod entries silently removed from the developer's own config,
    # and nothing warned. That flag is never passed by this rig.
    #
    # A failure to write it is fatal for a host and merely loud for a client.
    # That asymmetry is the whole point: a joining client reads a world the
    # server owns and writes none of its own, while a host CREATES a world, and a
    # host with no redirect creates it inside the developer's saves.
    param(
        [Parameter(Mandatory)] [string] $BepInExDir,
        [Parameter(Mandatory)] [string] $UserDataDir,
        [Parameter(Mandatory)] [string] $InstanceRole,
        [string] $InstanceName = '',
        [string] $Context = 'Provision',
        [switch] $Quiet
    )
    $who   = if ($InstanceName) { "[$InstanceName] " } else { '' }
    $lpCfg = Join-Path $BepInExDir 'config\stationeers.launchpad.cfg'
    if (-not (Test-Path -LiteralPath $lpCfg)) {
        $why = "${who}stationeers.launchpad.cfg not found at $lpCfg, so SavePathOverride could not be written and this instance has NO separate save root: everything it writes lands in the developer's own user-data folder, which is tier 1 and off-limits. Launch the instance once to generate the config, then rebuild it: testrig create -Target <name> -Force -As <id>."
        if ($InstanceRole -eq 'host') {
            throw "$why`nRefusing to leave a host without the redirect: a host creates a world, and that world would be created inside the developer's saves."
        }
        Write-Warning "$why`nTreat this as a stop, not a note: do not start this instance until the redirect is in place."
        return $false
    }
    $line = "SavePathOverride = " + $UserDataDir
    $content = Get-Content -LiteralPath $lpCfg
    if ($content -match '^SavePathOverride\s*=') {
        $content = $content -replace '^SavePathOverride\s*=.*$', $line
    } else {
        $content += $line
    }
    Set-Content -LiteralPath $lpCfg -Value $content -Encoding utf8
    if (-not $Quiet) { Write-Host "[$Context] SavePathOverride -> $UserDataDir" }
    return $true
}

function Get-RigSavePathOverride {
    # Read back what the instance's StationeersLaunchPad config actually says.
    # Used by the tests to prove the redirect survives the config re-copy, and
    # useful from a launcher when diagnosing an instance that is writing to the
    # wrong place.
    param([Parameter(Mandatory)] [string] $BepInExDir)
    $lpCfg = Join-Path $BepInExDir 'config\stationeers.launchpad.cfg'
    if (-not (Test-Path -LiteralPath $lpCfg)) { return $null }
    foreach ($line in (Get-Content -LiteralPath $lpCfg -ErrorAction SilentlyContinue)) {
        if ($line -match '^\s*SavePathOverride\s*=\s*(.*)$') { return $Matches[1].Trim() }
    }
    return $null
}

# ---- small helpers --------------------------------------------------------

function New-RigResetAction {
    param(
        [Parameter(Mandatory)] [string] $Half,
        [string] $Instance = '',
        [Parameter(Mandatory)] [string] $Kind,
        [Parameter(Mandatory)] [string] $Path,
        [string] $Source = '',
        [string] $Filter = '',
        [string] $Setting = '',
        [string] $Target = '',
        [string] $Role = '',
        [int] $Items = 0,
        [switch] $AfterCopy,
        [Parameter(Mandatory)] [string] $Label,
        [Parameter(Mandatory)] [string] $Reason
    )
    [pscustomobject]@{
        Half = $Half; Instance = $Instance; Kind = $Kind; Path = $Path
        Source = $Source; Filter = $Filter; Setting = $Setting; Target = $Target; Role = $Role
        Items = $Items; AfterCopy = [bool]$AfterCopy; Label = $Label; Reason = $Reason
    }
}

function New-RigResetReport {
    param(
        [Parameter(Mandatory)] [string] $Half,
        [string] $Instance = '',
        [Parameter(Mandatory)] [string] $Kind,
        [Parameter(Mandatory)] [string] $Detail,
        [switch] $Warn
    )
    [pscustomobject]@{ Half = $Half; Instance = $Instance; Kind = $Kind; Detail = $Detail; Warn = [bool]$Warn }
}

function Measure-RigDirectoryContents {
    # Top-level entry count for a directory, 0 for a missing one. Used to report
    # honest counts in the plan instead of "some files".
    param([string] $Path, [string] $Filter = '')
    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return 0 }
    if ($Filter) {
        return @(Get-ChildItem -LiteralPath $Path -Filter $Filter -Force -ErrorAction SilentlyContinue).Count
    }
    return @(Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue).Count
}

function Test-RigResetPidStale {
    # True when a pid FILE exists and the process it names is not the process it
    # claims to be. Both directions matter: a live instance's game.pid must
    # survive, and a recycled id must not keep a dead run's pid file alive
    # forever. A garbage or empty file is stale (there is nothing to protect).
    param(
        [Parameter(Mandatory)] [string] $File,
        [string[]] $ImageNames
    )
    if (-not (Test-Path -LiteralPath $File)) { return $false }   # nothing to delete
    $procId = Get-RigPidFromFile $File
    if ($null -eq $procId) { return $true }
    foreach ($img in @($ImageNames | Where-Object { $_ })) {
        if (Get-RigLiveProcess -TargetPid $procId -ImageName $img) { return $false }
    }
    return $true
}

function Get-RigResetInstanceNames {
    # Every provisioned client instance, from the one place that always has one
    # directory per instance: ClientRig/data/<name>/. rig.json is a file, so it
    # is naturally excluded.
    if (-not (Test-Path -LiteralPath $script:RigResetClientData)) { return @() }
    return @(Get-ChildItem -LiteralPath $script:RigResetClientData -Directory -ErrorAction SilentlyContinue |
             Sort-Object Name | ForEach-Object { $_.Name })
}

function Get-RigClientInstanceRootMap {
    # instanceName -> the instances root recorded in ClientRig/data/rig.json when that instance was
    # provisioned, for every entry that carries one.
    #
    # This is why it exists. The launcher takes -InstancesRoot, and instance trees normally live on
    # the game install's volume rather than inside TestRig/, so the reset cannot assume one root and
    # cannot see a launcher flag. It used to join ITS configured root to each instance name, which is
    # right only when the two happen to agree: with the trees on another volume the reset found no
    # BepInEx tree, reported "no instance tree" and silently skipped the config re-copy and the
    # SavePathOverride re-apply, which is half of what the reset is for. The registry is the one
    # place that records where each tree actually went, so it is read here rather than guessed.
    #
    # A missing, unreadable or half-written rig.json yields an empty map and every instance falls
    # back to the configured root, which is the behaviour before the field existed.
    $map = @{}
    $registry = Join-Path $script:RigResetClientData 'rig.json'
    if (-not (Test-Path -LiteralPath $registry)) { return $map }
    try {
        $entries = @((Get-Content -Raw -LiteralPath $registry -ErrorAction Stop) | ConvertFrom-Json -ErrorAction Stop)
    }
    catch { return $map }
    foreach ($e in $entries) {
        if ($null -eq $e) { continue }
        if (-not $e.PSObject.Properties['instanceName'] -or -not $e.instanceName)   { continue }
        if (-not $e.PSObject.Properties['instancesRoot'] -or -not $e.instancesRoot) { continue }
        $map[[string]$e.instanceName] = [string]$e.instancesRoot
    }
    return $map
}

function Get-RigInstanceTree {
    # Where this instance's hard-linked tree is, and where that answer came from. The source travels
    # with the path so a "no tree" report can say whether it looked where the registry pointed or
    # where the library defaults to, which is the difference between a genuinely unprovisioned
    # instance and one the reset simply could not find.
    param(
        [Parameter(Mandatory)] [string] $Name,
        $RootMap
    )
    if ($null -ne $RootMap -and $RootMap.ContainsKey($Name)) {
        return [pscustomobject]@{
            Path   = (Join-Path $RootMap[$Name] $Name)
            Source = 'the instances root recorded in rig.json'
        }
    }
    return [pscustomobject]@{
        Path   = (Join-Path $script:RigResetClientInstances $Name)
        Source = 'the configured instances root (this entry records none; a rebuild with testrig create -Force records it)'
    }
}

function Get-RigInstanceRole {
    # Role from the manifest, degrading to 'unknown' rather than throwing. The
    # reset treats an unknown role as a host for the SavePathOverride refusal,
    # because the expensive mistake is assuming a host is a client.
    param([Parameter(Mandatory)] [string] $DataDir)
    $manifest = Join-Path $DataDir 'instance.json'
    if (-not (Test-Path -LiteralPath $manifest)) { return 'unknown' }
    try {
        $m = (Get-Content -Raw -LiteralPath $manifest -ErrorAction Stop) | ConvertFrom-Json -ErrorAction Stop
        if ($m -and $m.PSObject.Properties['role'] -and $m.role) { return [string]$m.role }
    }
    catch { }
    return 'unknown'
}

function Get-RigNewestBuildTime {
    # Newest write time under a folder, preferring assemblies. A seeded mod is
    # stale when its DLL is older than the source tree's; walking every file of
    # every mod would cost more than the answer is worth.
    param([Parameter(Mandatory)] [string] $Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $files = @(Get-ChildItem -LiteralPath $Path -Recurse -File -Filter '*.dll' -ErrorAction SilentlyContinue)
    if ($files.Count -eq 0) {
        $files = @(Get-ChildItem -LiteralPath $Path -Recurse -File -ErrorAction SilentlyContinue)
    }
    if ($files.Count -eq 0) { return $null }
    return ($files | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc
}

function Get-RigBaselineConfigActions {
    # Per-file restore actions for one config scope, from the baseline.
    #
    # This is what replaces "re-copy BepInEx/config from the developer's install"
    # once a baseline exists. The source install is a reasonable PROXY for a clean
    # config (it is where a provision seeds from) but it is not the rig's config:
    # it is the developer's own, it moves when they change a setting in their own
    # game, and it has no opinion at all about the dedicated server's files. A
    # baseline does.
    #
    # Only files that actually differ are planned, so a clean rig plans nothing
    # and the printed summary stays honest about what moved.
    param(
        [Parameter(Mandatory)] $Baseline,
        [Parameter(Mandatory)] [string] $Prefix,
        [Parameter(Mandatory)] [string] $TargetDir,
        [Parameter(Mandatory)] [string] $Half,
        [string] $Instance = '',
        [Parameter(Mandatory)] $LiveRecords
    )
    $out  = New-Object System.Collections.Generic.List[object]
    $seen = @{}
    foreach ($rec in $LiveRecords) {
        $seen[$rec.Key] = $rec
    }
    foreach ($key in @($Baseline.Files.Keys | Where-Object { $_ -like "$Prefix*" } | Sort-Object)) {
        $b = $Baseline.Files[$key]
        if ([string]$b.class -ne 'config') { continue }
        $stored = Get-RigBaselineStoredPath -Key $key
        $leaf   = Split-Path -Leaf $key
        if (-not (Test-Path -LiteralPath $stored)) {
            # The manifest names a file whose bytes are not in the store. Nothing
            # can be restored from it, and pretending otherwise would overwrite a
            # real config with nothing.
            continue
        }
        # The target is derived from the key, not from the live file, so a config
        # a session DELETED is restored rather than quietly staying missing. That
        # is the case a hash comparison alone cannot see.
        $target = Join-Path $TargetDir $leaf
        $live   = $seen[$key]
        if ($live -and (Get-RigFileHash $live.Path) -eq [string]$b.sha256) { continue }
        $why = if ($live) { 'its contents moved since the baseline was captured' } else { 'it was deleted since the baseline was captured' }
        $out.Add((New-RigResetAction -Half $Half -Instance $Instance -Kind 'RestoreBaselineFile' -Path $target -Source $stored `
            -Label "$leaf restored from the baseline" -Reason $why))
    }
    foreach ($rec in $LiveRecords) {
        if ($Baseline.Files.ContainsKey($rec.Key)) { continue }
        $out.Add((New-RigResetAction -Half $Half -Instance $Instance -Kind 'DeleteFile' -Path $rec.Path `
            -Label "$(Split-Path -Leaf $rec.Key) removed (not in the baseline)" -Reason 'a config file created after the baseline was captured is this session garbage by the same argument as a value it flipped'))
    }
    # Plain array, for the same reason Get-RigMutableSurface returns one: every
    # caller wraps this in @().
    return $out.ToArray()
}

# ---- the plan -------------------------------------------------------------

function Get-RigResetPlan {
    # What a reset WOULD do, as data, with no side effects whatsoever. That
    # separation is deliberate: the destructive half of this file is inspectable
    # and testable without running it, and a reset nobody can dry-run is a reset
    # nobody will trust.
    #
    # Only targets that actually exist become actions, so the printed counts are
    # honest rather than aspirational. The one exception is the SavePathOverride
    # re-apply, which is planned for every instance with a BepInEx tree whether
    # or not the config copy happens: re-writing it is idempotent, and the cost
    # of skipping it once is a world written into the developer's saves.
    param([switch] $KeepState)

    $now      = Get-RigNowUtc
    $actions  = New-Object System.Collections.Generic.List[object]
    $reports  = New-Object System.Collections.Generic.List[object]
    $source   = Get-RigResetSourceInstall
    $lastReset = $null
    $baseline  = Get-RigSharedStateBaseline
    if ($baseline -and $baseline.PSObject.Properties['LastResetUtc']) { $lastReset = $baseline.LastResetUtc }

    # ---- the captured baseline, and how much of it may be trusted ----
    # Read once and passed down, because every branch below asks the same two
    # questions: does a baseline exist, and is it still describing this rig.
    $base       = Get-RigBaseline
    $surfaceAll = @(Get-RigMutableSurface)
    $baseState  = Test-RigBaselineStale -Baseline $base
    if (-not $baseState.Present) {
        $reports.Add((New-RigResetReport -Half 'rig' -Kind 'BaselineAbsent' -Warn `
            -Detail "no baseline has been captured, so 'clean' falls back to the built-in delete list. Server configs are not restored, because nothing here knows what they should look like. Capture one on an idle rig: testrig capture-baseline -As <id>"))
    }
    elseif ($baseState.Stale) {
        $reports.Add((New-RigResetReport -Half 'rig' -Kind 'BaselineStale' -Warn `
            -Detail "the baseline (captured $($base.CapturedUtc), game $($base.GameVersion)) no longer describes this rig: $($baseState.Reasons -join '; '). Config files are still restored from it. Re-capture on an idle rig: testrig capture-baseline -As <id>"))
    }
    else {
        $reports.Add((New-RigResetReport -Half 'rig' -Kind 'BaselineUsed' `
            -Detail "restoring to the baseline captured $($base.CapturedUtc) (game $($base.GameVersion), $($base.Files.Count) entries)"))
    }

    # ---- which dedicated-server worlds belong to THIS session ----
    # Read once, here, next to the baseline it used to be part of, so the two are
    # visibly separate now: the baseline says what a CONFIG should contain, the
    # session marker says which WORLDS predate the session. Nothing about a
    # baseline (present, absent, fresh or stale) changes a world's fate any more.
    $sessionWorlds = Get-RigSessionWorldSnapshot

    # ---- client half, per provisioned instance ----
    # Read once, used for every instance: the trees are wherever -Provision put them, which is
    # normally the game install's volume rather than inside TestRig/.
    $rootMap   = Get-RigClientInstanceRootMap
    $instances = Get-RigResetInstanceNames
    foreach ($name in $instances) {
        $data     = Join-Path $script:RigResetClientData $name
        $treeInfo = Get-RigInstanceTree -Name $name -RootMap $rootMap
        $tree     = $treeInfo.Path
        $bepinex  = Join-Path $tree 'BepInEx'
        $userData = Join-Path $data 'userdata'
        $role     = Get-RigInstanceRole -DataDir $data

        # setting.xml carries StartLocalHost. An instance that silently comes up
        # hosting when a test believes it is a joiner is exactly the failure this
        # mechanism exists to prevent, and -Start writes the file it needs anyway.
        $settings = Join-Path $data 'setting.xml'
        if (Test-Path -LiteralPath $settings) {
            $actions.Add((New-RigResetAction -Half 'client' -Instance $name -Kind 'DeleteFile' -Path $settings `
                -Label 'setting.xml' -Reason 'carries StartLocalHost; -Start rewrites what it needs'))
        }

        # Worlds from the previous session. Test B loading test A's mutated world
        # under the same name is the plainest form of this whole problem.
        $saves = Join-Path $userData 'saves'
        $n = Measure-RigDirectoryContents $saves
        if ($n -gt 0) {
            $actions.Add((New-RigResetAction -Half 'client' -Instance $name -Kind 'DeleteContents' -Path $saves -Items $n `
                -Label "$n save(s)" -Reason 'a previous session world loaded under the same name'))
        }

        # Unity logs are never rotated, so a -Grep assertion happily matches a
        # line written by a run that ended yesterday.
        $logs = Join-Path $data 'logs'
        $n = Measure-RigDirectoryContents $logs
        if ($n -gt 0) {
            $actions.Add((New-RigResetAction -Half 'client' -Instance $name -Kind 'DeleteContents' -Path $logs -Items $n `
                -Label "$n log(s)" -Reason 'never rotated; a -Grep matches a dead run'))
        }

        # Panel layout and visibility persist, so a screenshot frames differently
        # than it did last session for no reason the test can see.
        $imgui = Join-Path $data 'imgui.ini'
        if (Test-Path -LiteralPath $imgui) {
            $actions.Add((New-RigResetAction -Half 'client' -Instance $name -Kind 'DeleteFile' -Path $imgui `
                -Label 'imgui.ini' -Reason 'panel layout persists and reframes screenshots'))
        }

        # A LIVE instance keeps its pid file. See Test-RigResetPidStale.
        $pidFile = Join-Path $data 'game.pid'
        if (Test-Path -LiteralPath $pidFile) {
            if (Test-RigResetPidStale -File $pidFile -ImageNames @($script:RigResetClientImage)) {
                $actions.Add((New-RigResetAction -Half 'client' -Instance $name -Kind 'DeleteFile' -Path $pidFile `
                    -Label 'stale game.pid' -Reason 'no live game process claims it'))
            }
            else {
                $reports.Add((New-RigResetReport -Half 'client' -Instance $name -Kind 'PreservedLivePid' `
                    -Detail "game.pid kept: process $(Get-RigPidFromFile $pidFile) is a live game client"))
            }
        }

        if (Test-Path -LiteralPath $bepinex) {
            # POST /config/set defaults to save:true, so every value a previous
            # test flipped is sticky until something puts it back.
            #
            # THE BASELINE WINS OVER THE SOURCE INSTALL when it covers this
            # instance. The source install is the developer's own game and only
            # ever was a proxy for a clean config; the baseline is the rig's own
            # captured state and is exact. The copy stays as the fallback for an
            # instance the baseline has never seen (a fresh provision), and for a
            # rig that has no baseline at all.
            $cfgDir  = Join-Path $bepinex 'config'
            $copied  = $false
            $prefix  = "client/$name/bepinex-config/"
            $covered = $false
            if ($base) { $covered = @($base.Files.Keys | Where-Object { $_ -like "$prefix*" }).Count -gt 0 }

            if ($covered) {
                $liveCfg = @($surfaceAll | Where-Object { $_.Class -eq 'config' -and $_.Key -like "$prefix*" })
                $cfgActs = @(Get-RigBaselineConfigActions -Baseline $base -Prefix $prefix -TargetDir $cfgDir `
                                -Half 'client' -Instance $name -LiveRecords $liveCfg)
                foreach ($a in $cfgActs) {
                    $actions.Add($a)
                    # Only a write that actually touches the StationeersLaunchPad
                    # config can wipe SavePathOverride, and only then is a failed
                    # re-apply this reset's fault. Marking every restore as a copy
                    # would make an unrelated failure fatal for no reason.
                    if ((Split-Path -Leaf $a.Path) -eq 'stationeers.launchpad.cfg') { $copied = $true }
                }
                # modconfig.xml is a separate scope with its own target folder. It
                # is included because StationeersLaunchPad has been observed
                # rewriting a modconfig and silently dropping every Local entry,
                # which is the kind of damage nothing else here would notice.
                $mcPrefix = "client/$name/modconfig.xml"
                $liveMc   = @($surfaceAll | Where-Object { $_.Key -eq $mcPrefix })
                foreach ($a in @(Get-RigBaselineConfigActions -Baseline $base -Prefix $mcPrefix -TargetDir $userData `
                                    -Half 'client' -Instance $name -LiveRecords $liveMc)) {
                    $actions.Add($a)
                }
            }
            elseif ($source) {
                $srcCfg = Join-Path $source 'BepInEx\config'
                $actions.Add((New-RigResetAction -Half 'client' -Instance $name -Kind 'CopyConfigTree' -Path $cfgDir -Source $srcCfg `
                    -Label 'BepInEx config re-copied' -Reason 'POST /config/set persists by default, so a flipped value is sticky'))
                $copied = $true
                if ($base) {
                    $reports.Add((New-RigResetReport -Half 'client' -Instance $name -Kind 'BaselineMissesInstance' -Warn `
                        -Detail "the baseline has no config for this instance, so its BepInEx config was re-copied from the source install instead. That is the pre-baseline behaviour and is only approximately right. Capture a baseline once this instance is set up as you want it: -CaptureBaseline"))
                }
            }
            else {
                $reports.Add((New-RigResetReport -Half 'client' -Instance $name -Kind 'ConfigCopySkipped' -Warn `
                    -Detail 'BepInEx config NOT re-copied: no baseline covers this instance AND the source install could not be resolved from Directory.Build.props. Any plugin setting a previous test changed is still in place.'))
            }

            # ALWAYS, and always AFTER the copy. The copy wipes SavePathOverride,
            # and an instance without it writes into the developer's tier-1 save
            # folder. Nothing in this file matters more than this ordering.
            $actions.Add((New-RigResetAction -Half 'client' -Instance $name -Kind 'ReapplySavePathOverride' -Path $bepinex `
                -Target $userData -Role $role -AfterCopy:$copied `
                -Label 'SavePathOverride re-applied' -Reason 'the config re-copy wipes it; without it the instance writes into the developer tier-1 save folder'))

            $n = Measure-RigDirectoryContents $bepinex 'LogOutput.log*'
            if ($n -gt 0) {
                $actions.Add((New-RigResetAction -Half 'client' -Instance $name -Kind 'DeleteGlob' -Path $bepinex -Filter 'LogOutput.log*' -Items $n `
                    -Label 'LogOutput.log' -Reason 'never rotated; a -Logs -Grep matches a dead run'))
            }

            $cache = Join-Path $bepinex 'cache'
            if (Test-Path -LiteralPath $cache) {
                $actions.Add((New-RigResetAction -Half 'client' -Instance $name -Kind 'DeleteDirectory' -Path $cache `
                    -Label 'BepInEx cache' -Reason 'stale assembly cache after a plugin rebuild'))
            }

            # An unprocessed request file is picked up on the NEXT launch, so
            # another session's request fires inside your test.
            $req = Join-Path $bepinex 'inspector\requests'
            $n = Measure-RigDirectoryContents $req
            if ($n -gt 0) {
                $actions.Add((New-RigResetAction -Half 'client' -Instance $name -Kind 'DeleteContents' -Path $req -Items $n `
                    -Label "$n inspector request(s)" -Reason 'an unconsumed request file fires on the next launch'))
            }
            $snap = Join-Path $bepinex 'inspector\snapshots'
            $n = Measure-RigDirectoryContents $snap
            if ($n -gt 0) {
                $actions.Add((New-RigResetAction -Half 'client' -Instance $name -Kind 'DeleteContents' -Path $snap -Items $n `
                    -Label "$n inspector snapshot(s)" -Reason 'timestamped with no rotation, so "read the newest" picks up a stale one'))
            }
        }
        else {
            # The path is named WITH its source, because "no tree" and "the reset looked in the
            # wrong place" used to read identically. If this says the configured root while the
            # instance was provisioned somewhere else, the fix is -Provision -Force, which records
            # the root; the reset then finds the BepInEx config it is skipping here.
            $reports.Add((New-RigResetReport -Half 'client' -Instance $name -Kind 'NoTree' -Warn `
                -Detail "no instance tree at $tree (from $($treeInfo.Source)); only its data/ state was reset, so the BepInEx config was NOT re-copied and SavePathOverride was NOT re-applied"))
        }

        # PRESERVED, and reported instead: re-seeding needs the developer's
        # modconfig.xml and is provisioning's job, so a stale mod is named here
        # and fixed with -Provision -Force rather than deleted from under a test.
        $seeded = Join-Path $userData 'mods'
        if (Test-Path -LiteralPath $seeded) {
            $srcMods = Join-Path $script:RigResetUserDataDir 'mods'
            foreach ($d in @(Get-ChildItem -LiteralPath $seeded -Directory -ErrorAction SilentlyContinue)) {
                $peer = Join-Path $srcMods $d.Name
                if (-not (Test-Path -LiteralPath $peer)) { continue }
                $mine  = Get-RigNewestBuildTime -Path $d.FullName
                $their = Get-RigNewestBuildTime -Path $peer
                if ($mine -and $their -and $their -gt $mine) {
                    $reports.Add((New-RigResetReport -Half 'client' -Instance $name -Kind 'StaleMod' -Warn `
                        -Detail "seeded mod '$($d.Name)' is older than the source tree ($($mine.ToString('u')) vs $($their.ToString('u'))). Re-seed it: testrig create -Target $name -Force -As <id>"))
                }
            }
        }
    }

    # ---- server half ----
    $dediInstall = $script:RigResetDediInstall
    $dediData    = $script:RigResetDediData

    # The Scenario value selects which probe fires on the next boot, so a session
    # that forgets to blank it injects its scenario into an unrelated test's log.
    # The value is blanked and the FILE is left alone: everything else in it is
    # somebody's deliberate setting.
    $srCfg = Join-Path $dediInstall 'BepInEx\config\net.scenariorunner.cfg'
    if (Test-Path -LiteralPath $srCfg) {
        $cur = Get-RigConfigSettingValue -Path $srCfg -Setting 'Scenario'
        if ($cur) {
            $actions.Add((New-RigResetAction -Half 'server' -Kind 'BlankSetting' -Path $srCfg -Setting 'Scenario' `
                -Label "ScenarioRunner Scenario blanked (was '$cur')" -Reason 'it selects which probe fires on the next boot'))
        }
    }

    foreach ($pair in @(
        @{ Path = (Join-Path $dediInstall 'BepInEx\scenariorunner\requests'); Label = 'scenariorunner request(s)'; Reason = 'a stray drop file is consumed on the next boot' }
        @{ Path = (Join-Path $dediInstall 'BepInEx\scenariorunner\give');     Label = 'scenariorunner give file(s)'; Reason = 'a stray drop file is consumed on the next boot' }
        @{ Path = (Join-Path $dediInstall 'BepInEx\inspector\requests');      Label = 'inspector request(s)';       Reason = 'an unconsumed request file fires on the next launch' }
        @{ Path = (Join-Path $dediInstall 'BepInEx\inspector\snapshots');     Label = 'inspector snapshot(s)';      Reason = 'timestamped with no rotation, so "read the newest" picks up a stale one' }
    )) {
        $n = Measure-RigDirectoryContents $pair.Path
        if ($n -gt 0) {
            $actions.Add((New-RigResetAction -Half 'server' -Kind 'DeleteContents' -Path $pair.Path -Items $n `
                -Label "$n $($pair.Label)" -Reason $pair.Reason))
        }
    }

    # The wrapper's finally block does not run on a force-kill or a reboot, so
    # these outlive their processes. Same process-image check as everywhere else.
    $serverPid = Join-Path $dediData 'server.pid'
    $hostPid   = Join-Path $dediData 'host.pid'
    $control   = Join-Path $dediData 'control.cmd'
    $serverStale = Test-RigResetPidStale -File $serverPid -ImageNames @($script:RigResetServerImage)
    $hostStale   = Test-RigResetPidStale -File $hostPid   -ImageNames $script:RigResetHostImages
    $serverLive  = (Test-Path -LiteralPath $serverPid) -and -not $serverStale
    $hostLive    = (Test-Path -LiteralPath $hostPid)   -and -not $hostStale
    if ($serverStale) {
        $actions.Add((New-RigResetAction -Half 'server' -Kind 'DeleteFile' -Path $serverPid `
            -Label 'stale server.pid' -Reason 'no live dedicated server claims it'))
    }
    elseif ($serverLive) {
        $reports.Add((New-RigResetReport -Half 'server' -Kind 'PreservedLivePid' `
            -Detail "server.pid kept: process $(Get-RigPidFromFile $serverPid) is a live dedicated server"))
    }
    if ($hostStale) {
        $actions.Add((New-RigResetAction -Half 'server' -Kind 'DeleteFile' -Path $hostPid `
            -Label 'stale host.pid' -Reason 'no live host wrapper claims it'))
    }
    elseif ($hostLive) {
        $reports.Add((New-RigResetReport -Half 'server' -Kind 'PreservedLivePid' `
            -Detail "host.pid kept: process $(Get-RigPidFromFile $hostPid) is a live host wrapper"))
    }
    if ((Test-Path -LiteralPath $control) -and -not $serverLive -and -not $hostLive) {
        $actions.Add((New-RigResetAction -Half 'server' -Kind 'DeleteFile' -Path $control `
            -Label 'stale control.cmd' -Reason 'a queued command nothing is left to consume'))
    }

    # data/setting.xml carries a SavePath that stopped existing at the TestRig
    # restructure and UseSteamP2P=true. Every flag the launcher needs is passed
    # on each -Start, so a regenerated default file is the correct one.
    $dediSettings = Join-Path $dediData 'setting.xml'
    if (Test-Path -LiteralPath $dediSettings) {
        $actions.Add((New-RigResetAction -Half 'server' -Kind 'DeleteFile' -Path $dediSettings `
            -Label 'setting.xml' -Reason 'carries stale SavePath and UseSteamP2P; -Start passes every flag it needs'))
    }

    # ---- server worlds: keep everything that predates this session ----
    #
    # THE RULE, and it is the whole rule: a world is deleted if and only if the
    # session marker recorded a world set and this world is not in it. A world
    # that was on disk when the session started is ALWAYS kept, baseline or no
    # baseline, fresh or stale.
    #
    # The baseline used to decide this, and the failure is worth writing down
    # because the code read as safe. Test-RigBaselineStale inspects the game
    # version, the instance-name set and files of class 'payload'. Worlds are
    # class 'world', so THE WORLD SET IS INVISIBLE TO STALENESS. Stage a world
    # deliberately (copy a tier-2 source over tier 3, which is exactly what the
    # repo's own save rules prescribe for restoring a save under test) and the
    # baseline still read FRESH, still did not list that world, and the next
    # session boundary deleted it as "a session that is over". The staged save
    # WAS the test.
    #
    # The marker cannot make that mistake: it is written before the session's
    # first mutating action, so everything it lists is older than the session by
    # construction, and a world staged before the lock is in it. Everything else
    # about this decision fails closed, in Get-RigSessionWorldSnapshot.
    $saveRoot = Join-Path $dediData 'saves'
    if (Test-Path -LiteralPath $saveRoot) {
        $worlds = @(Get-RigServerWorlds)
        if ($worlds.Count -gt 0) {
            if (-not $sessionWorlds.Recorded) {
                # Named as its own report rather than buried in the kept line,
                # because "nothing is being deleted" is exactly the sentence an
                # agent needs when it expected a cleanup and did not get one. A
                # genuine degradation warns; a rig with no marker at all is the
                # ordinary clean state and is merely stated.
                $reports.Add((New-RigResetReport -Half 'server' -Kind 'WorldsNotTracked' -Warn:$sessionWorlds.Degraded `
                    -Detail "no dedicated-server world is deleted by this restore: $($sessionWorlds.Reason)"))
            }
            $keptCount = 0
            $keptBytes = 0
            foreach ($w in $worlds) {
                $m = Get-ChildItem -LiteralPath $w.Path -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum
                $wb = if ($m.Sum) { [long]$m.Sum } else { 0 }
                if ($sessionWorlds.Recorded -and -not $sessionWorlds.Keys.ContainsKey($w.Key)) {
                    $actions.Add((New-RigResetAction -Half 'server' -Kind 'DeleteTree' -Path $w.Path `
                        -Label ("world '{0}' deleted ({1:N1} MB)" -f $w.Name, ($wb / 1MB)) `
                        -Reason 'it was not on the rig when this session first touched it, so this session created it and its lifetime ends with the lock'))
                }
                else {
                    $keptCount++
                    $keptBytes += $wb
                }
            }
            if ($keptCount -gt 0) {
                $why = if ($sessionWorlds.Recorded) { "they were already here when this session started ($($sessionWorlds.Count) world(s) recorded)" }
                       else { $sessionWorlds.Reason }
                $reports.Add((New-RigResetReport -Half 'server' -Kind 'SavesRetained' `
                    -Detail ("data/saves kept: {0} world(s), {1:N1} MB ({2})" -f $keptCount, ($keptBytes / 1MB), $why)))
            }
        }
    }

    # ---- server config: restored from the baseline, or reported without one ----
    #
    # "rig-owned versus mod-owned is undecided" was the honest answer while nothing
    # recorded what these files should contain. A baseline decides it: whatever was
    # captured IS the rig-owned value, and a value that moved since is this
    # session's and goes back. Without a baseline the old report stands, because
    # resetting a value nobody classified would be its own silent breakage.
    $srvCfgDir = Join-Path $dediInstall 'BepInEx\config'
    $srvCovered = $false
    if ($base) { $srvCovered = @($base.Files.Keys | Where-Object { $_ -like 'server/bepinex-config/*' }).Count -gt 0 }
    if ($srvCovered) {
        $liveSrv = @($surfaceAll | Where-Object { $_.Class -eq 'config' -and $_.Key -like 'server/bepinex-config/*' })
        foreach ($a in @(Get-RigBaselineConfigActions -Baseline $base -Prefix 'server/bepinex-config/' -TargetDir $srvCfgDir `
                            -Half 'server' -LiveRecords $liveSrv)) {
            # net.scenariorunner.cfg is already handled above by blanking exactly
            # one value and leaving the rest of the file alone. Restoring the whole
            # file from the baseline as well would fight that, and would put back
            # whatever Scenario the baseline happened to capture.
            if ((Split-Path -Leaf $a.Path) -eq 'net.scenariorunner.cfg') { continue }
            $actions.Add($a)
        }
        $liveMc = @($surfaceAll | Where-Object { $_.Key -eq 'server/modconfig.xml' })
        foreach ($a in @(Get-RigBaselineConfigActions -Baseline $base -Prefix 'server/modconfig.xml' -TargetDir $dediInstall `
                            -Half 'server' -LiveRecords $liveMc)) {
            $actions.Add($a)
        }
    }
    elseif ($lastReset) {
        $cfgDir = Join-Path $dediInstall 'BepInEx\config'
        if (Test-Path -LiteralPath $cfgDir) {
            try { $since = [DateTime]::Parse($lastReset, [System.Globalization.CultureInfo]::InvariantCulture,
                        [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal) }
            catch { $since = $null }
            if ($since) {
                $touched = @(Get-ChildItem -LiteralPath $cfgDir -Filter '*.cfg' -File -ErrorAction SilentlyContinue |
                             Where-Object { $_.Name -ne 'net.scenariorunner.cfg' -and $_.LastWriteTimeUtc -gt $since } |
                             ForEach-Object { $_.Name })
                if ($touched.Count -gt 0) {
                    $reports.Add((New-RigResetReport -Half 'server' -Kind 'ConfigTouched' -Warn `
                        -Detail "server config changed since the last reset and is NOT reset here (no baseline covers the server, so rig-owned versus mod-owned is still undecided): $($touched -join ', '). Capture a baseline to make these restorable: -CaptureBaseline"))
                }
            }
        }
    }

    return [pscustomobject]@{
        GeneratedUtc  = $now
        RigHome       = $script:RigResetHome
        SourceInstall = $source
        Instances     = $instances
        Actions       = $actions.ToArray()
        Reports       = $reports.ToArray()
        KeepState     = [bool]$KeepState
        LastResetUtc  = $lastReset
        Baseline      = $baseState
        SessionWorlds = $sessionWorlds
    }
}

function Get-RigConfigSettingValue {
    # Read one BepInEx config value without parsing the whole file into a model.
    # Side-effect free, and used by the plan, so it must never write.
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Setting
    )
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $pattern = '^\s*' + [regex]::Escape($Setting) + '\s*=\s*(.*)$'
    foreach ($line in (Get-Content -LiteralPath $Path -ErrorAction SilentlyContinue)) {
        if ($line -match '^\s*#') { continue }
        if ($line -match $pattern) { return $Matches[1].Trim() }
    }
    return $null
}

function Set-RigConfigSettingBlank {
    # Blank one value and leave the rest of the file exactly as it was, comments
    # included. Rewriting the file from a model would silently drop every comment
    # BepInEx wrote, and those comments are the only documentation a plugin's
    # settings have.
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Setting
    )
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    $pattern = '^(\s*' + [regex]::Escape($Setting) + '\s*=).*$'
    $lines   = Get-Content -LiteralPath $Path
    $hit     = $false
    $out     = foreach ($line in $lines) {
        if (-not $hit -and $line -notmatch '^\s*#' -and $line -match $pattern) {
            $hit = $true
            "$($Matches[1]) "
        }
        else { $line }
    }
    if (-not $hit) { return $false }
    Set-Content -LiteralPath $Path -Value $out -Encoding utf8
    return $true
}

# ---- shared state: report, never restore ----------------------------------

function Get-RigSharedStateSnapshot {
    # Cheap, non-invasive facts about the state NOTHING can isolate:
    # PlayerCookie-v2.xml, the PlayerPrefs key and Blueprints\ are per-Windows-user
    # and shared with the developer's own client, because persistentDataPath is
    # fixed in the serialized PlayerSettings inside globalgamemanagers and editing
    # app.info was tested and does nothing.
    #
    # READ ONLY, always. Restoring any of this would itself be the write the save
    # rules forbid, so this function has no counterpart that puts it back and must
    # never grow one.
    $values = [ordered]@{}

    $cookie = Join-Path $script:RigResetSharedDataDir 'PlayerCookie-v2.xml'
    if (Test-Path -LiteralPath $cookie) {
        try {
            $values['cookie.bytes'] = [string](Get-Item -LiteralPath $cookie -ErrorAction Stop).Length
            $text = Get-Content -Raw -LiteralPath $cookie -ErrorAction Stop
            $values['cookie.worlds'] = [string]([regex]::Matches($text, '<World[\s>]')).Count
        }
        catch { $values['cookie.bytes'] = 'unreadable' }
    }
    else { $values['cookie.bytes'] = 'absent' }

    try {
        $key = Get-Item -LiteralPath $script:RigResetPlayerPrefsKey -ErrorAction Stop
        foreach ($n in @($key.GetValueNames() | Sort-Object)) {
            $v = $key.GetValue($n)
            $s = if ($v -is [byte[]]) { "bytes[$($v.Length)]" } else { [string]$v }
            # Long values are hashed rather than stored: the snapshot exists to
            # spot a CHANGE, and a multi-kilobyte blob in a JSON file nobody reads
            # is not worth the disk.
            if ($s.Length -gt 200) {
                $sha = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($s))
                $s = "sha256:" + [System.BitConverter]::ToString($sha[0..7]).Replace('-', '')
            }
            $values["prefs.$n"] = $s
        }
    }
    catch { $values['prefs'] = 'unreadable' }

    $bp = Join-Path $script:RigResetSharedDataDir 'Blueprints'
    $values['blueprints.files'] = [string]@(Get-ChildItem -LiteralPath $bp -Recurse -File -ErrorAction SilentlyContinue).Count

    return [pscustomobject]@{
        CapturedUtc    = (Get-RigNowUtc)
        SharedDataDir  = $script:RigResetSharedDataDir
        PlayerPrefsKey = $script:RigResetPlayerPrefsKey
        Values         = $values
    }
}

function ConvertTo-RigStateMap {
    # Snapshots survive a JSON round trip, so a Values bag arrives either as the
    # ordered hashtable it was written as or as the PSCustomObject ConvertFrom-Json
    # produces. Both are flattened here so the comparison never has to care.
    param($Values)
    $map = @{}
    if ($null -eq $Values) { return $map }
    if ($Values -is [System.Collections.IDictionary]) {
        foreach ($k in $Values.Keys) { $map["$k"] = [string]$Values[$k] }
        return $map
    }
    foreach ($p in $Values.PSObject.Properties) { $map[$p.Name] = [string]$p.Value }
    return $map
}

function Compare-RigSharedState {
    # The drift report. Returns one line per difference and an empty array when
    # nothing moved. It never restores anything, and there is deliberately no
    # function here that could.
    param(
        [Parameter(Mandatory)] $Before,
        $After
    )
    if (-not $After) { $After = Get-RigSharedStateSnapshot }
    $a = ConvertTo-RigStateMap $Before.Values
    $b = ConvertTo-RigStateMap $After.Values
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($k in @($a.Keys | Sort-Object)) {
        if (-not $b.ContainsKey($k)) { $out.Add("$k : '$($a[$k])' -> gone"); continue }
        if ($a[$k] -ne $b[$k])       { $out.Add("$k : '$($a[$k])' -> '$($b[$k])'") }
    }
    foreach ($k in @($b.Keys | Sort-Object)) {
        if (-not $a.ContainsKey($k)) { $out.Add("$k : new -> '$($b[$k])'") }
    }
    # Comma-wrapped: PowerShell unrolls a returned array, so a single-line drift
    # report would come back as a bare string and a caller indexing [0] would get
    # its first CHARACTER instead of the line.
    return , $out.ToArray()
}

function Get-RigSharedStateBaseline {
    # The baseline captured at the start of the current session, or $null.
    if (-not $script:RigResetStateFile -or -not (Test-Path -LiteralPath $script:RigResetStateFile)) { return $null }
    try { return (Get-Content -Raw -LiteralPath $script:RigResetStateFile -ErrorAction Stop) | ConvertFrom-Json -ErrorAction Stop }
    catch { return $null }
}

function Save-RigSharedStateBaseline {
    # One small JSON file inside TestRig/, covered by the deny-all gitignore on
    # the rig root. Carries the last reset time too, which is what lets the next
    # reset report which server config files moved since.
    param(
        $Snapshot,
        [string] $LastResetUtc
    )
    if (-not $Snapshot) { $Snapshot = Get-RigSharedStateSnapshot }
    $record = [ordered]@{
        CapturedUtc    = $Snapshot.CapturedUtc
        SharedDataDir  = $Snapshot.SharedDataDir
        PlayerPrefsKey = $Snapshot.PlayerPrefsKey
        LastResetUtc   = $LastResetUtc
        Values         = $Snapshot.Values
    }
    $dir = Split-Path -Parent $script:RigResetStateFile
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    ($record | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $script:RigResetStateFile -Encoding utf8
}

function Write-RigSharedStateDrift {
    # Printed by both launchers at -Unlock. This fixes nothing; it turns state
    # that was invisible until a later test failed into a line at the session
    # boundary, which is the practical half of the guarantee.
    $baseline = Get-RigSharedStateBaseline
    if (-not $baseline) {
        Write-Host "[State] No shared-state baseline for this session, so no drift report."
        return
    }
    $delta = Compare-RigSharedState -Before $baseline
    if ($delta.Count -eq 0) {
        Write-Host "[State] Shared per-user state is unchanged since the lock was taken (PlayerCookie-v2.xml, PlayerPrefs, Blueprints)."
        return
    }
    Write-Host "[State] Shared per-user state MOVED during this session. It cannot be isolated and is never restored, so this is a report:"
    foreach ($d in $delta) { Write-Host "[State]   $d" }
    Write-Host "[State] These are shared with the developer's own client. Nothing here is save data."
}

# ---- the reset ------------------------------------------------------------

function Test-RigResetAllowed {
    # Resetting under a live game process is destructive nonsense: the files being
    # deleted are the ones something has open. An orphan counts too, because an
    # untracked game process writes to exactly the same folders.
    #
    # Unlike the LOCK's busy signal this does count a running dedicated server
    # with nobody connected, and does count orphans. Both remedies are bounded and
    # in the operator's hands (stop the server, kill the pid), so neither can pin
    # the rig the way counting an orphan as lock-busy would.
    $busy = Get-RigBusySignal
    $why  = @()
    if ($busy.Busy)              { $why += $busy.Detail }
    if ($busy.ServerLive)        { $why += 'the dedicated server process is alive' }
    if ($busy.Orphans.Count -ge 1) {
        $names = ($busy.Orphans | ForEach-Object { "$($_.Name) pid $($_.ProcessId)" }) -join ', '
        $why += "untracked rig game process(es) are running: $names"
    }
    return [pscustomobject]@{
        Allowed = ($why.Count -eq 0)
        Reason  = ($why -join '; ')
        Busy    = $busy
    }
}

function Invoke-RigReset {
    # Perform a plan. Returns what it did; throws only when an action FAILED,
    # because a half-reset instance that nobody hears about is worse than a loud
    # stop.
    #
    # -WhatIf prints the plan and changes nothing, including the baseline file.
    # -KeepState is the escape hatch and is deliberately loud: "I staged that save
    # on purpose" has to stay possible without becoming the silent default.
    param(
        $Plan,
        [switch] $KeepState,
        [switch] $WhatIf,
        [string] $Reason = 'session start'
    )
    $result = [pscustomobject]@{
        Refused = $false; RefusalReason = ''
        Skipped = $false
        Performed = @(); Failures = @(); Plan = $null
    }

    $gate = Test-RigResetAllowed
    if (-not $Plan) { $Plan = Get-RigResetPlan -KeepState:$KeepState }
    $result.Plan = $Plan

    # The previous reset's timestamp, carried forward on every path that does not
    # perform one. Overwriting it with $null would erase the only reference point
    # the "which server config moved since the last reset" report has.
    $prev = Get-RigSharedStateBaseline
    $prevResetUtc = if ($prev -and $prev.PSObject.Properties['LastResetUtc']) { $prev.LastResetUtc } else { $null }

    if ($WhatIf) {
        # Before every write below, including the baseline: -WhatIf changes nothing.
        Write-Host "[Reset] -WhatIf: nothing was changed. The reset would do:"
        Write-RigResetPlanSummary -Plan $Plan -Prefix '[Reset]  ' -IncludeReports
        return $result
    }

    if (-not $gate.Allowed) {
        $result.Refused = $true
        $result.RefusalReason = $gate.Reason
        Write-Warning "[Reset] State reset SKIPPED: the rig is in use ($($gate.Reason)). Nothing was deleted. Stop what is running (testrig stop -Target all -As <id>, or kill an untracked pid), then release and re-take the lock to get a clean rig. This session starts on whatever the previous one left behind."
        # The baseline is still captured. Without it, this session's unlock would
        # diff against a PREVIOUS session's snapshot and report that session's
        # changes as this one's, which is worse than no report at all.
        Save-RigSharedStateBaseline -LastResetUtc $prevResetUtc
        return $result
    }

    if ($KeepState) {
        # The marker is deliberately NOT cleared here (only a completed restore
        # clears it, further down), and that is what carries the debt: the next
        # session restores unless it also passes -KeepState. It is also what keeps
        # every dedicated-server world, since nothing is deleted on this path at
        # all and the world set that session recorded stays on disk with it.
        $result.Skipped = $true
        Write-Warning "[Reset] -KeepState: the between-session state reset was SKIPPED on purpose. This session inherits whatever the previous one left behind, dedicated-server worlds included, and the dirty marker stays set so the next session cleans up."
        Write-RigResetPlanSummary -Plan $Plan -Prefix '[Reset]   would have reset' -IncludeReports
        Save-RigSharedStateBaseline -LastResetUtc $prevResetUtc
        return $result
    }

    $done      = New-Object System.Collections.Generic.List[object]
    $failures  = New-Object System.Collections.Generic.List[string]
    foreach ($a in $Plan.Actions) {
        try {
            Invoke-RigResetAction -Action $a
            $done.Add($a)
        }
        catch {
            $who = if ($a.Instance) { "instance '$($a.Instance)'" } else { "the $($a.Half) half" }
            $failures.Add("$who : $($a.Label) failed ($($a.Kind) $($a.Path)): $($_.Exception.Message)")
        }
    }
    $result.Performed = $done.ToArray()
    $result.Failures  = $failures.ToArray()

    Write-RigResetOutcome -Plan $Plan -Performed $result.Performed -Reason $Reason

    # Baseline AFTER the reset, so the session's shared-state comparison starts
    # from the state the session actually begins with.
    Save-RigSharedStateBaseline -LastResetUtc (Get-RigNowUtc)

    # THE MARKER IS CLEARED HERE AND NOWHERE ELSE, and only when every action
    # succeeded. That is the whole contract: "no marker" means "a restore ran to
    # completion", so a partial restore leaves the rig marked and the next
    # acquisition tries again. Clearing it on a failure would turn one bad reset
    # into a rig that never gets cleaned and never says so.
    if ($failures.Count -eq 0 -and (Get-Command Clear-RigDirtyMarker -ErrorAction SilentlyContinue)) {
        try { Clear-RigDirtyMarker }
        catch { Write-Warning "[Reset] The restore completed but the dirty marker could not be cleared: $($_.Exception.Message). The rig is clean; the next acquisition will simply restore an already-clean rig, which costs nothing." }
    }

    if ($failures.Count -gt 0) {
        foreach ($f in $failures) { Write-Warning "[Reset] $f" }
        throw "The rig state reset failed on $($failures.Count) action(s), so at least one instance is HALF RESET and must not be trusted for a test:`n  $($failures -join "`n  ")`nFix the cause (a file held open by a process, a permission), then -Unlock and take the lock again; re-asserting a lock you already hold does not reset."
    }
    return $result
}

function Invoke-RigResetAction {
    # One action. Every branch is deliberately narrow: nothing here takes a
    # wildcard from the caller, and every path came out of Get-RigResetPlan, which
    # only ever builds paths under the rig home or under an instance tree the
    # launcher itself recorded in rig.json.
    param([Parameter(Mandatory)] $Action)
    switch ($Action.Kind) {
        'DeleteFile' {
            Remove-Item -LiteralPath $Action.Path -Force -ErrorAction Stop
        }
        'DeleteGlob' {
            foreach ($f in @(Get-ChildItem -LiteralPath $Action.Path -Filter $Action.Filter -File -Force -ErrorAction SilentlyContinue)) {
                Remove-Item -LiteralPath $f.FullName -Force -ErrorAction Stop
            }
        }
        'DeleteContents' {
            foreach ($e in @(Get-ChildItem -LiteralPath $Action.Path -Force -ErrorAction SilentlyContinue)) {
                Remove-Item -LiteralPath $e.FullName -Recurse -Force -ErrorAction Stop
            }
        }
        'DeleteDirectory' {
            Remove-Item -LiteralPath $Action.Path -Recurse -Force -ErrorAction Stop
            # Recreated empty because provisioning creates it too: BepInEx writes
            # its assembly cache there and a missing folder is one more thing for
            # a first launch to get wrong.
            New-Item -ItemType Directory -Force -Path $Action.Path | Out-Null
        }
        'DeleteTree' {
            # Like DeleteDirectory but the folder does NOT come back. Used for a
            # dedicated-server world: an empty directory where a world used to be
            # is not a neutral leftover, it is something the game and every save
            # listing has to decide what to do with.
            Remove-Item -LiteralPath $Action.Path -Recurse -Force -ErrorAction Stop
        }
        'RestoreBaselineFile' {
            # Put a captured config back, byte for byte. The source is always a
            # file inside TestRig/baseline/content/ that a capture wrote; nothing
            # here ever copies out of the developer's install, and nothing takes a
            # path from a caller.
            if (-not (Test-Path -LiteralPath $Action.Source)) {
                throw "the baseline no longer has stored content at $($Action.Source), so nothing could be restored"
            }
            $dir = Split-Path -Parent $Action.Path
            if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
            Copy-Item -LiteralPath $Action.Source -Destination $Action.Path -Force -ErrorAction Stop
        }
        'CopyConfigTree' {
            # Copy every .cfg the source install has over the instance's, then
            # delete any .cfg the instance has that the source does not: a config
            # a previous test's plugin created is garbage by the same argument as
            # a value it flipped. Nothing but *.cfg is touched.
            New-Item -ItemType Directory -Force -Path $Action.Path | Out-Null
            $srcFiles = @(Get-ChildItem -LiteralPath $Action.Source -Filter '*.cfg' -File -ErrorAction Stop)
            $keep = @{}
            foreach ($f in $srcFiles) {
                $keep[$f.Name] = $true
                Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $Action.Path $f.Name) -Force -ErrorAction Stop
            }
            foreach ($f in @(Get-ChildItem -LiteralPath $Action.Path -Filter '*.cfg' -File -ErrorAction SilentlyContinue)) {
                if (-not $keep.ContainsKey($f.Name)) { Remove-Item -LiteralPath $f.FullName -Force -ErrorAction Stop }
            }
        }
        'ReapplySavePathOverride' {
            # THE line this whole file is careful about. It runs after
            # CopyConfigTree because the plan orders it that way, and the copy
            # wipes the value it writes.
            #
            # An unknown role is treated as a host, because the expensive mistake
            # is assuming a host is a client.
            #
            # WHETHER A FAILURE IS FATAL depends on whether THIS reset is what
            # broke it, and that distinction is load bearing in both directions:
            #   - AfterCopy: the reset just re-copied the config and therefore
            #     wiped a redirect that was working. Leaving that quiet would let
            #     the next -Start write worlds into the developer's tier-1 save
            #     folder because of something this code did. Fatal, named.
            #   - No copy: nothing was wiped. The instance is in exactly the state
            #     provisioning already refused to leave it in (no
            #     stationeers.launchpad.cfg because it has never been launched).
            #     Failing here would make the lock unobtainable, and -Provision
            #     -Force needs the lock, so the rig would be unrepairable. Warn
            #     loudly, relabel so the printed summary does not claim a write
            #     that did not happen, and let the session start. POST /host
            #     independently refuses a world on a non-isolated save root.
            $role = if ($Action.Role -and $Action.Role -ne 'unknown') { $Action.Role } else { 'host' }
            $ok = $false
            try {
                $ok = Set-RigSavePathOverride -BepInExDir $Action.Path -UserDataDir $Action.Target `
                        -InstanceRole $role -InstanceName $Action.Instance -Context 'Reset' -Quiet
            }
            catch {
                if ($Action.AfterCopy) { throw }
                Write-Warning "[Reset] $($_.Exception.Message)"
                $ok = $false
            }
            if (-not $ok) {
                if ($Action.AfterCopy) {
                    throw "SavePathOverride could not be written back after the config re-copy, so this instance now has NO separate save root and would write worlds into the developer's tier-1 save folder."
                }
                $Action.Label = 'SavePathOverride NOT written (no StationeersLaunchPad config; launch once, then: testrig create -Target <name> -Force -As <id>)'
            }
        }
        'BlankSetting' {
            if (-not (Set-RigConfigSettingBlank -Path $Action.Path -Setting $Action.Setting)) {
                throw "setting '$($Action.Setting)' not found in $($Action.Path)"
            }
        }
        default { throw "unknown reset action kind '$($Action.Kind)'" }
    }
}

function Write-RigResetPlanSummary {
    param(
        [Parameter(Mandatory)] $Plan,
        [string] $Prefix = '[Reset]  ',
        [switch] $IncludeReports
    )
    if ($Plan.Actions.Count -eq 0) { Write-Host "$Prefix nothing (the rig is already clean)" }
    foreach ($g in ($Plan.Actions | Group-Object { if ($_.Instance) { $_.Instance } else { $_.Half } })) {
        Write-Host "$Prefix $($g.Name): $((@($g.Group | ForEach-Object { $_.Label })) -join ', ')"
    }
    if ($IncludeReports) { Write-RigResetReports -Plan $Plan }
}

function Write-RigResetReports {
    param([Parameter(Mandatory)] $Plan)
    foreach ($r in $Plan.Reports) {
        $who = if ($r.Instance) { "$($r.Instance)" } else { $r.Half }
        if ($r.Warn) { Write-Warning "[Reset] $who : $($r.Detail)" }
        else         { Write-Host    "[Reset]   kept  $who : $($r.Detail)" }
    }
}

function Write-RigResetOutcome {
    # A silent reset is indistinguishable from no reset when something later goes
    # wrong, so what happened is printed per instance, every time.
    param(
        [Parameter(Mandatory)] $Plan,
        [Parameter(Mandatory)] $Performed,
        [string] $Reason = 'session start'
    )
    $count = @($Performed).Count
    $scope = if ($Plan.Instances.Count -gt 0) { "$($Plan.Instances.Count) client instance(s) and the dedicated server" } else { 'the dedicated server' }
    Write-Host "[Reset] State reset on $Reason, over $scope ($count action(s))."
    if ($count -eq 0) {
        Write-Host "[Reset]   nothing to clear; the rig was already clean."
    }
    foreach ($g in (@($Performed) | Group-Object { if ($_.Instance) { $_.Instance } else { $_.Half } })) {
        Write-Host "[Reset]   $($g.Name): $((@($g.Group | ForEach-Object { $_.Label })) -join ', ')"
    }
    Write-Host "[Reset]   kept: rig.json, instance manifests, provision stamps, seeded mods, deployed plugins, the hard links."
    if ($Plan.PSObject.Properties['Baseline'] -and $Plan.Baseline) {
        $b = $Plan.Baseline
        $how = if (-not $b.Present) { 'no baseline (built-in delete list only)' }
               elseif ($b.Stale)    { 'a STALE baseline (config still restored from it)' }
               else                 { 'the captured baseline' }
        Write-Host "[Reset]   clean state: $how."
    }
    # Worlds get their own line whatever happened, including "nothing", because
    # this is the only irreversible thing here and a silent delete of somebody's
    # world is precisely the failure the session snapshot exists to prevent.
    if ($Plan.PSObject.Properties['SessionWorlds'] -and $Plan.SessionWorlds) {
        $sw  = $Plan.SessionWorlds
        $how = if ($sw.Recorded) { "$($sw.Count) dedicated-server world(s) predated this session and are kept; any world this session created is deleted" }
               else             { "no dedicated-server world was deleted ($($sw.Reason))" }
        Write-Host "[Reset]   worlds: $how."
    }
    Write-RigResetReports -Plan $Plan
    Write-Host "[Reset] This resets BETWEEN sessions only. A session spans many start/stop cycles, so two unrelated tests under THIS one lock get no reset between them: release and re-take the lock when the subject changes."
}

# Default wiring: the real rig, rooted at this file's own folder, exactly like
# rig-lock.ps1. Resolved from the file and not from the caller, so either
# launcher gets the same behaviour no matter where it is invoked from.
Initialize-RigResetPaths -RigHome $PSScriptRoot
