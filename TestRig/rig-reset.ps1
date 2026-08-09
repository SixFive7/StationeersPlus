# =============================================================================
# TestRig state hygiene - shared implementation
# =============================================================================
# Dot-sourced by BOTH launchers, immediately after rig-lock.ps1:
#     TestRig/DedicatedServer/dedicated-server.ps1
#     TestRig/ClientRig/client-rig.ps1
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
# THE TWO FACTS THAT MAKE THIS DANGEROUS IF DONE CARELESSLY
#   1. Re-copying BepInEx/config WIPES SavePathOverride, and an instance without
#      it writes its worlds into the developer's tier-1 save folder. The re-apply
#      after the copy is the single most important line in this file, and
#      Set-RigSavePathOverride lives here (not in client-rig.ps1) so provisioning
#      and resetting write that redirect through ONE implementation. Two copies
#      of a tier-1 safety write is exactly the drift that overwrites somebody's
#      saves.
#   2. A pid file is not proof of life and not proof of death. Windows recycles
#      process ids and these files outlive their processes on a force-kill or a
#      reboot, so every pid here goes through the same process-image check
#      rig-lock.ps1 uses. A live instance's game.pid must survive; a recycled id
#      must not be trusted in either direction.
#
# NEVER OUTSIDE TestRig/
#   Two reads leave the rig folder and both are read-only: copying BepInEx/config
#   out of the source install, and the shared-state snapshot (PlayerCookie-v2.xml,
#   the PlayerPrefs key, Blueprints/). Nothing outside TestRig/ is ever written.
#   The developer's save folder is tier 1 and is not touched at all.
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

    # Client half.
    $script:RigResetClientData = Join-Path $RigHome 'ClientRig\data'
    $script:RigResetClientInstances =
        if     ($InstanceRoot)                   { $InstanceRoot }
        elseif ($env:STATIONEERS_CLIENTRIG_ROOT) { $env:STATIONEERS_CLIENTRIG_ROOT }
        else                                     { Join-Path $RigHome 'ClientRig\instances' }

    # Server half.
    $script:RigResetDediInstall = Join-Path $RigHome 'DedicatedServer\install'
    $script:RigResetDediData    = Join-Path $RigHome 'DedicatedServer\data'

    # State this file owns, both inside TestRig/ and both gitignored by the
    # deny-all rule on the rig root.
    $script:RigResetStateFile = Join-Path $RigHome 'session.state.json'

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
        $why = "${who}stationeers.launchpad.cfg not found at $lpCfg, so SavePathOverride could not be written and this instance has NO separate save root: everything it writes lands in the developer's own user-data folder, which is tier 1 and off-limits. Launch the instance once to generate the config, then re-run -Provision -Force."
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

    # ---- client half, per provisioned instance ----
    $instances = Get-RigResetInstanceNames
    foreach ($name in $instances) {
        $data     = Join-Path $script:RigResetClientData $name
        $tree     = Join-Path $script:RigResetClientInstances $name
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
            $cfgDir = Join-Path $bepinex 'config'
            $copied = $false
            if ($source) {
                $srcCfg = Join-Path $source 'BepInEx\config'
                $actions.Add((New-RigResetAction -Half 'client' -Instance $name -Kind 'CopyConfigTree' -Path $cfgDir -Source $srcCfg `
                    -Label 'BepInEx config re-copied' -Reason 'POST /config/set persists by default, so a flipped value is sticky'))
                $copied = $true
            }
            else {
                $reports.Add((New-RigResetReport -Half 'client' -Instance $name -Kind 'ConfigCopySkipped' -Warn `
                    -Detail 'BepInEx config NOT re-copied: the source install could not be resolved from Directory.Build.props. Any plugin setting a previous test changed is still in place.'))
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
            $reports.Add((New-RigResetReport -Half 'client' -Instance $name -Kind 'NoTree' `
                -Detail "no instance tree at $tree; only its data/ state was reset"))
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
                        -Detail "seeded mod '$($d.Name)' is older than the source tree ($($mine.ToString('u')) vs $($their.ToString('u'))). Re-provision to refresh it: -Provision -Force -Instance $name"))
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

    # PRESERVED and reported. Staged worlds are deliberate setup, so deleting
    # them destroys somebody's afternoon; there is no retention policy anywhere,
    # which is worth saying out loud once a session.
    $saveRoot = Join-Path $dediData 'saves'
    if (Test-Path -LiteralPath $saveRoot) {
        $worlds = @(Get-ChildItem -LiteralPath $saveRoot -Directory -ErrorAction SilentlyContinue)
        if ($worlds.Count -gt 0) {
            $bytes = 0
            foreach ($w in $worlds) {
                $m = Get-ChildItem -LiteralPath $w.FullName -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum
                if ($m.Sum) { $bytes += $m.Sum }
            }
            $reports.Add((New-RigResetReport -Half 'server' -Kind 'SavesRetained' `
                -Detail ("data/saves kept: {0} world(s), {1:N1} MB, no retention policy" -f $worlds.Count, ($bytes / 1MB))))
        }
    }

    # PRESERVED and reported. Which of these are rig-owned and which are mod-owned
    # is not decided, and resetting a value nobody classified would be its own
    # silent breakage. Naming the ones that moved is the honest middle.
    if ($lastReset) {
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
                        -Detail "server config changed since the last reset and is NOT reset here (rig-owned versus mod-owned is undecided): $($touched -join ', ')"))
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
        Write-Warning "[Reset] State reset SKIPPED: the rig is in use ($($gate.Reason)). Nothing was deleted. Stop what is running (client-rig.ps1 -Stop -All -As <id>, dedicated-server.ps1 -Stop -As <id>, or kill an untracked pid), then release and re-take the lock to get a clean rig. This session starts on whatever the previous one left behind."
        # The baseline is still captured. Without it, this session's unlock would
        # diff against a PREVIOUS session's snapshot and report that session's
        # changes as this one's, which is worse than no report at all.
        Save-RigSharedStateBaseline -LastResetUtc $prevResetUtc
        return $result
    }

    if ($KeepState) {
        $result.Skipped = $true
        Write-Warning "[Reset] -KeepState: the between-session state reset was SKIPPED on purpose. This session inherits whatever the previous one left behind."
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

    if ($failures.Count -gt 0) {
        foreach ($f in $failures) { Write-Warning "[Reset] $f" }
        throw "The rig state reset failed on $($failures.Count) action(s), so at least one instance is HALF RESET and must not be trusted for a test:`n  $($failures -join "`n  ")`nFix the cause (a file held open by a process, a permission), then -Unlock and take the lock again; re-asserting a lock you already hold does not reset."
    }
    return $result
}

function Invoke-RigResetAction {
    # One action. Every branch is deliberately narrow: nothing here takes a
    # wildcard from the caller, and every path came out of Get-RigResetPlan, which
    # only ever builds paths under the rig home or the instance root.
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
                $Action.Label = 'SavePathOverride NOT written (no StationeersLaunchPad config; launch once, then -Provision -Force)'
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
    Write-Host "[Reset]   kept: rig.json, instance manifests, provision stamps, seeded mods, the dedicated server's saves and mods, the hard links."
    Write-RigResetReports -Plan $Plan
    Write-Host "[Reset] This resets BETWEEN sessions only. A session spans many start/stop cycles, so two unrelated tests under THIS one lock get no reset between them: release and re-take the lock when the subject changes."
}

# Default wiring: the real rig, rooted at this file's own folder, exactly like
# rig-lock.ps1. Resolved from the file and not from the caller, so either
# launcher gets the same behaviour no matter where it is invoked from.
Initialize-RigResetPaths -RigHome $PSScriptRoot
