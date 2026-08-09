<#
.SYNOPSIS
    Offline test suite for the TestRig state hygiene reset (TestRig/rig-reset.ps1).

.DESCRIPTION
    The reset is the mechanism that stops one agent's test failing because of
    what an unrelated test left behind, and it is DESTRUCTIVE by nature, so it is
    the second piece of the rig that has to be correct rather than merely tidy.
    This suite exercises it end to end.

    It runs entirely offline: no game, no dedicated server, no client instance,
    no network, no lock on the real rig. Every test points both libraries at a
    throwaway directory through Initialize-RigResetPaths (which re-points the lock
    library too), and the suite refuses to start if that redirection did not take.
    A fingerprint of the real rig's own state files is taken before the run and
    verified untouched after it.

    The developer's install, user-data folder and per-user Unity state are faked
    inside the temp tree as well, so nothing outside it is read and nothing
    outside it can possibly be written.

    No Pester, for the same reason the lock suite has none: a dependency that has
    to be installed before the reset can be tested is a dependency that stops the
    reset from being tested.

.PARAMETER Section
    Run only sections whose name matches this wildcard. Default: all.
#>
[CmdletBinding()]
param(
    [string] $Section = '*'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

. (Join-Path $PSScriptRoot 'rig-lock.ps1')
. (Join-Path $PSScriptRoot 'rig-reset.ps1')

# =============================================================================
# Assert helpers (same shape as rig-lock.tests.ps1)
# =============================================================================

$script:Passed   = 0
$script:Failed   = 0
$script:Failures = @()
$script:CurrentSection = ''

function Start-Section {
    param([string] $Name)
    $script:CurrentSection = $Name
    Write-Host ''
    Write-Host "== $Name " -NoNewline
    Write-Host ('=' * [Math]::Max(3, 60 - $Name.Length))
}

function Add-Pass { param([string] $Name) $script:Passed++; Write-Host "  pass  $Name" }
function Add-Fail {
    param([string] $Name, [string] $Detail)
    $script:Failed++
    $script:Failures += "[$script:CurrentSection] $Name`n        $Detail"
    Write-Host "  FAIL  $Name"
    Write-Host "        $Detail"
}

function Assert-True {
    param([bool] $Condition, [string] $Name, [string] $Detail = '')
    if ($Condition) { Add-Pass $Name } else { Add-Fail $Name "expected true. $Detail" }
}

function Assert-False {
    param([bool] $Condition, [string] $Name, [string] $Detail = '')
    if (-not $Condition) { Add-Pass $Name } else { Add-Fail $Name "expected false. $Detail" }
}

function Assert-Equal {
    param($Expected, $Actual, [string] $Name)
    if ($Expected -eq $Actual) { Add-Pass $Name }
    else { Add-Fail $Name "expected [$Expected], got [$Actual]" }
}

function Assert-Match {
    param([string] $Text, [string] $Pattern, [string] $Name)
    if ($Text -match $Pattern) { Add-Pass $Name }
    else { Add-Fail $Name "text did not match /$Pattern/. Text was: $Text" }
}

function Assert-Throws {
    param([scriptblock] $Body, [string] $Name, [string] $Pattern)
    try {
        $null = & $Body 6>$null 3>$null
        Add-Fail $Name 'expected a throw; the call returned normally'
    }
    catch {
        $msg = $_.Exception.Message
        if ($Pattern -and $msg -notmatch $Pattern) { Add-Fail $Name "threw, but the message did not match /$Pattern/. Message: $msg" }
        else { Add-Pass $Name }
    }
}

function Assert-NoThrow {
    param([scriptblock] $Body, [string] $Name)
    try { $null = & $Body 6>$null 3>$null; Add-Pass $Name }
    catch { Add-Fail $Name "unexpected throw: $($_.Exception.Message)" }
}

function Assert-FileExists {
    param([string] $Path, [string] $Name)
    Assert-True (Test-Path -LiteralPath $Path) $Name "missing: $Path"
}

function Assert-FileGone {
    param([string] $Path, [string] $Name)
    Assert-False (Test-Path -LiteralPath $Path) $Name "still present: $Path"
}

function Invoke-Quiet { param([scriptblock] $Body) return (& $Body 6>$null 3>$null) }

function Test-SectionSelected { param([string] $Name) return ($Name -like $Section) }

# =============================================================================
# Fixtures
# =============================================================================

$script:TempRoot    = $null
$script:SourceDir   = $null   # fake "developer's game install"
$script:UserDataDir = $null   # fake Documents\My Games\Stationeers
$script:SharedDir   = $null   # fake LocalLow\Rocketwerkz\rocketstation
$script:RealHome    = $PSScriptRoot

# A pid that cannot belong to a live process.
$script:DeadPid = 999999999

# A registry key that does not exist, so the PlayerPrefs read exercises its
# missing-key path and this suite can never touch the real Unity PlayerPrefs.
$script:FakePrefsKey = 'HKCU:\Software\StationeersTestRig\NoSuchKeyForTests'

function Use-TestPaths {
    # Both image names are pwsh so a fixture can use a real live process id as
    # "the game is running", and every outward path points inside the temp tree.
    Initialize-RigResetPaths -RigHome $script:TempRoot `
        -SourceInstall $script:SourceDir `
        -InstanceRoot (Join-Path $script:TempRoot 'ClientRig\instances') `
        -UserDataDir $script:UserDataDir `
        -SharedDataDir $script:SharedDir `
        -PlayerPrefsKey $script:FakePrefsKey `
        -ServerImageName 'pwsh' -ClientImageName 'pwsh' -HostWrapperImageNames @('pwsh')
}

function New-SourceInstall {
    # A fake developer install: just the BepInEx/config tree the reset copies out
    # of. Its stationeers.launchpad.cfg deliberately carries NO SavePathOverride,
    # which is the real shape and the reason the re-apply exists.
    $cfg = Join-Path $script:SourceDir 'BepInEx\config'
    New-Item -ItemType Directory -Force -Path $cfg | Out-Null
    Set-Content -LiteralPath (Join-Path $cfg 'stationeers.launchpad.cfg') -Encoding utf8 -Value @(
        '## Settings file was created by plugin StationeersLaunchPad'
        ''
        '[General]'
        'SomeSetting = pristine'
    )
    Set-Content -LiteralPath (Join-Path $cfg 'net.inspectorplus.cfg') -Encoding utf8 -Value @(
        '[Server - Headless]'
        'Force Unpause Without Client = false'
    )
    Set-Content -LiteralPath (Join-Path $cfg 'net.spraypaintplus.cfg') -Encoding utf8 -Value @(
        '[Client - Visual]'
        'Beam Width = 0.05'
    )
}

function New-TestHome {
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ("rigreset-tests-" + [guid]::NewGuid().ToString('N').Substring(0, 10))
    $script:TempRoot    = $root
    $script:SourceDir   = Join-Path $root '_source-install'
    $script:UserDataDir = Join-Path $root '_userdata'
    $script:SharedDir   = Join-Path $root '_locallow'
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    New-SourceInstall
    Reset-TestHome
    Use-TestPaths

    # The redirection is checked, never assumed. If it silently failed, every
    # test below would be operating on the real rig.
    if ((Get-RigResetHomePath) -ne $root) {
        throw "SAFETY ABORT: Initialize-RigResetPaths did not repoint the rig home. It is still $(Get-RigResetHomePath)."
    }
    if ((Get-RigResetHomePath) -eq $script:RealHome) {
        throw "SAFETY ABORT: the test home resolves to the REAL rig at $script:RealHome."
    }
    if ((Get-RigLockFilePath) -ne (Join-Path $root 'session.lock')) {
        throw "SAFETY ABORT: the lock library was not repointed with the reset library; it is still $(Get-RigLockFilePath)."
    }
    return $root
}

function Reset-TestHome {
    # _altroot stands in for an instances root on another volume (E:\StationeersRig on a real rig).
    # It is wiped with everything else so a tree left there by one section cannot leak into the next.
    foreach ($p in @('ClientRig', 'DedicatedServer', '_userdata', '_locallow', '_altroot')) {
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue (Join-Path $script:TempRoot $p)
    }
    Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $script:TempRoot 'session.lock')
    Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $script:TempRoot 'session.state.json')
    foreach ($p in @(
        'ClientRig\data', 'ClientRig\instances',
        'DedicatedServer\data',
        'DedicatedServer\install\BepInEx\config',
        'DedicatedServer\install\BepInEx\scenariorunner\requests',
        'DedicatedServer\install\BepInEx\scenariorunner\give',
        'DedicatedServer\install\BepInEx\inspector\requests',
        'DedicatedServer\install\BepInEx\inspector\snapshots',
        '_userdata\mods', '_locallow\Blueprints'
    )) {
        New-Item -ItemType Directory -Force -Path (Join-Path $script:TempRoot $p) | Out-Null
    }
}

function Get-InstanceDataDir { param([string] $Name) return (Join-Path $script:TempRoot "ClientRig\data\$Name") }
function Get-InstanceTreeDir { param([string] $Name) return (Join-Path $script:TempRoot "ClientRig\instances\$Name") }
function Get-InstanceBepInEx { param([string] $Name) return (Join-Path (Get-InstanceTreeDir $Name) 'BepInEx') }

function Set-TestRegistry {
    # ClientRig/data/rig.json, written the way client-rig.ps1 -Provision writes it. The reset reads
    # the instances root out of this file, so a fixture that puts a tree somewhere other than the
    # default root has to record it here too, exactly as a real provision would.
    param([Parameter(Mandatory)] $Entries)
    $file = Join-Path $script:TempRoot 'ClientRig\data\rig.json'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $file) | Out-Null
    (,@($Entries) | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $file -Encoding utf8
}

function New-TestInstance {
    # A provisioned instance, dirty in every way the reset is supposed to clean,
    # laid out exactly as client-rig.ps1 lays one out.
    #
    # -TreeRoot puts the hard-linked tree somewhere other than ClientRig/instances/, which is the
    # normal case on a real rig: hard links cannot cross volumes, so the trees sit on the game
    # install's drive. data/<name>/ always stays beside the rig.
    param(
        [Parameter(Mandatory)] [string] $Name,
        [string] $Role = 'client',
        [string] $RawPid,
        [string] $TreeRoot,
        [switch] $NoManifest,
        [switch] $BrokenManifest,
        [switch] $NoTree,
        [switch] $NoLaunchPadConfig
    )
    $data = Get-InstanceDataDir $Name
    $tree = if ($TreeRoot) { Join-Path $TreeRoot $Name } else { Get-InstanceTreeDir $Name }
    $bep  = Join-Path $tree 'BepInEx'
    New-Item -ItemType Directory -Force -Path $data, (Join-Path $data 'logs'),
        (Join-Path $data 'userdata\saves\PreviousWorld'), (Join-Path $data 'userdata\mods\Local_Example') | Out-Null

    if ($BrokenManifest)      { Set-Content -LiteralPath (Join-Path $data 'instance.json') -Value '{ this is not json' -Encoding utf8 }
    elseif (-not $NoManifest) {
        ([ordered]@{ instanceName = $Name; index = 1; role = $Role; port = 27701; gamePort = 27801 } |
            ConvertTo-Json -Depth 4) | Set-Content -LiteralPath (Join-Path $data 'instance.json') -Encoding utf8
    }
    Set-Content -LiteralPath (Join-Path $data 'provision.stamp') -Value 'provisioned from version 0.0.0' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $data 'setting.xml') -Value '<SettingData><StartLocalHost>true</StartLocalHost></SettingData>' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $data 'imgui.ini') -Value '[Window][Debug]' -Encoding utf8
    $pidValue = if ($PSBoundParameters.ContainsKey('RawPid')) { $RawPid } else { "$script:DeadPid" }
    Set-Content -LiteralPath (Join-Path $data 'game.pid') -Value $pidValue -Encoding utf8
    Set-Content -LiteralPath (Join-Path $data 'logs\unity-20260808-120000.log') -Value 'a line from a dead run' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $data 'logs\unity-20260809-120000.log') -Value 'another dead run' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $data 'userdata\saves\PreviousWorld\PreviousWorld.save') -Value 'zip bytes' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $data 'userdata\mods\Local_Example\Example.dll') -Value 'dll bytes' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $data 'userdata\modconfig.xml') -Value '<ModConfig />' -Encoding utf8

    if ($NoTree) { return $data }

    New-Item -ItemType Directory -Force -Path (Join-Path $bep 'config'), (Join-Path $bep 'cache'),
        (Join-Path $bep 'inspector\requests'), (Join-Path $bep 'inspector\snapshots'),
        (Join-Path $bep 'plugins\ClientDriver') | Out-Null
    if (-not $NoLaunchPadConfig) {
        Set-Content -LiteralPath (Join-Path $bep 'config\stationeers.launchpad.cfg') -Encoding utf8 -Value @(
            '## Settings file was created by plugin StationeersLaunchPad'
            ''
            '[General]'
            'SomeSetting = pristine'
            "SavePathOverride = $(Join-Path $data 'userdata')"
        )
    }
    # A value a previous test flipped through POST /config/set, which persists.
    Set-Content -LiteralPath (Join-Path $bep 'config\net.spraypaintplus.cfg') -Encoding utf8 -Value @(
        '[Client - Visual]'
        'Beam Width = 9.99'
    )
    # A config file the source install does not have at all.
    Set-Content -LiteralPath (Join-Path $bep 'config\net.leftover.cfg') -Value 'Left = behind' -Encoding utf8
    # Not a .cfg: must survive the config re-copy untouched.
    Set-Content -LiteralPath (Join-Path $bep 'config\notes.txt') -Value 'not a config' -Encoding utf8

    Set-Content -LiteralPath (Join-Path $bep 'LogOutput.log') -Value 'stale bepinex log' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $bep 'LogOutput.log.1') -Value 'older stale bepinex log' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $bep 'cache\chainloader_typeloader.dat') -Value 'stale cache' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $bep 'inspector\requests\leftover.json') -Value '{}' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $bep 'inspector\snapshots\snapshot_old.json') -Value '{}' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $bep 'plugins\ClientDriver\ClientDriver.dll') -Value 'plugin bytes' -Encoding utf8
    return $data
}

function New-TestServerState {
    # The dedicated half, dirty in every way the reset is supposed to clean.
    param(
        [string] $ServerPid,
        [string] $HostPid,
        [string] $Scenario = 'pgp-cable-burn-probe'
    )
    $install = Join-Path $script:TempRoot 'DedicatedServer\install'
    $data    = Join-Path $script:TempRoot 'DedicatedServer\data'
    Set-Content -LiteralPath (Join-Path $install 'BepInEx\config\net.scenariorunner.cfg') -Encoding utf8 -Value @(
        '## Settings file was created by plugin ScenarioRunner v0.1.0'
        '## Plugin GUID: net.scenariorunner'
        ''
        '[Probe]'
        ''
        '## Scenario id to run after world load. Empty string disables the probe.'
        '# Setting type: String'
        '# Default value: '
        "Scenario = $Scenario"
        ''
        '## How many simulation ticks to wait after world load before the scenario fires.'
        '# Setting type: Int32'
        '# Default value: 5'
        'Delay Ticks = 5'
        ''
        'Log Inventory On First Tick = false'
    )
    Set-Content -LiteralPath (Join-Path $install 'BepInEx\config\net.powergridplus.cfg') -Value 'Something = 1' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $install 'BepInEx\scenariorunner\requests\probe.json') -Value '{}' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $install 'BepInEx\scenariorunner\give\give.json') -Value '{}' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $install 'BepInEx\inspector\requests\probe.json') -Value '{}' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $install 'BepInEx\inspector\snapshots\snapshot_old.json') -Value '{}' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $install 'modconfig.xml') -Value '<ModConfig />' -Encoding utf8

    Set-Content -LiteralPath (Join-Path $data 'setting.xml') -Value '<SettingData><UseSteamP2P>true</UseSteamP2P></SettingData>' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $data 'control.cmd') -Value 'save "leftover"' -Encoding utf8
    $serverPidValue = if ($PSBoundParameters.ContainsKey('ServerPid')) { $ServerPid } else { "$script:DeadPid" }
    $hostPidValue   = if ($PSBoundParameters.ContainsKey('HostPid'))   { $HostPid }   else { "$script:DeadPid" }
    Set-Content -LiteralPath (Join-Path $data 'server.pid') -Value $serverPidValue -Encoding utf8
    Set-Content -LiteralPath (Join-Path $data 'host.pid')   -Value $hostPidValue   -Encoding utf8
    New-Item -ItemType Directory -Force -Path (Join-Path $data 'saves\Luna'), (Join-Path $data 'mods\Local_Example') | Out-Null
    Set-Content -LiteralPath (Join-Path $data 'saves\Luna\Luna.save') -Value 'zip bytes' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $data 'mods\Local_Example\Example.dll') -Value 'dll bytes' -Encoding utf8
}

function Get-TreeFingerprint {
    # Every file under the temp root, with size and content hash. Strong enough to
    # prove a "no side effects" claim rather than merely suggest it.
    param([string] $Root)
    $lines = foreach ($f in (Get-ChildItem -LiteralPath $Root -Recurse -File -Force -ErrorAction SilentlyContinue | Sort-Object FullName)) {
        $h = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash
        "$($f.FullName.Substring($Root.Length))|$($f.Length)|$h"
    }
    return ($lines -join "`n")
}

# =============================================================================
# Sections
# =============================================================================

function Test-Paths {
    if (-not (Test-SectionSelected 'paths')) { return }
    Start-Section 'paths (injection, and the lock library follows)'
    Reset-TestHome
    Use-TestPaths

    Assert-Equal $script:TempRoot (Get-RigResetHomePath) 'the rig home follows the injected path'
    Assert-Equal (Join-Path $script:TempRoot 'session.state.json') (Get-RigResetStateFilePath) 'the shared-state baseline lives inside the rig home'
    Assert-Equal $script:SourceDir (Get-RigResetSourceInstall) 'an explicit source install is used when it has BepInEx\config'

    # The lock library must be looking at the same rig, because the reset asks it
    # whether that rig is busy.
    Assert-Equal (Join-Path $script:TempRoot 'session.lock') (Get-RigLockFilePath) 'Initialize-RigResetPaths repoints the lock library too'

    # A source install that does not look like one is refused rather than guessed.
    Initialize-RigResetPaths -RigHome $script:TempRoot -SourceInstall (Join-Path $script:TempRoot '_nope') `
        -SharedDataDir $script:SharedDir -PlayerPrefsKey $script:FakePrefsKey
    Assert-True ($null -eq (Get-RigResetSourceInstall)) 'a source install without BepInEx\config resolves to nothing, not to a guess'
    Use-TestPaths

    Assert-True ((Get-Command Invoke-RigReset).Parameters.ContainsKey('WhatIf')) 'Invoke-RigReset takes -WhatIf'
    Assert-True ((Get-Command Invoke-RigReset).Parameters.ContainsKey('KeepState')) 'Invoke-RigReset takes -KeepState'
    Assert-True ((Get-Command New-RigLock).Parameters.ContainsKey('KeepState')) 'New-RigLock takes -KeepState so a launcher can opt out'
}

function Test-Plan {
    if (-not (Test-SectionSelected 'plan')) { return }
    Start-Section 'the plan is data, and computing it changes nothing'
    Reset-TestHome
    New-TestInstance -Name 'client1' -Role 'client' | Out-Null
    New-TestInstance -Name 'host1'   -Role 'host'   | Out-Null
    New-TestServerState

    $before = Get-TreeFingerprint $script:TempRoot
    $plan   = Invoke-Quiet { Get-RigResetPlan }
    $after  = Get-TreeFingerprint $script:TempRoot
    Assert-Equal $before $after 'Get-RigResetPlan has NO side effects: not one byte moved'

    Assert-Equal 2 $plan.Instances.Count 'the plan finds both provisioned instances'
    Assert-True ($plan.Actions.Count -gt 0) 'the plan has actions for a dirty rig'

    $labels = @($plan.Actions | Where-Object { $_.Instance -eq 'client1' } | ForEach-Object { $_.Label })
    foreach ($want in @('setting.xml', 'save', 'log', 'imgui.ini', 'game.pid', 'config re-copied', 'SavePathOverride', 'LogOutput.log', 'cache', 'inspector request', 'inspector snapshot')) {
        Assert-True (($labels -join '; ') -match [regex]::Escape($want)) "the plan names the '$want' target for client1" "labels: $($labels -join '; ')"
    }

    # Ordering is the safety property: the SavePathOverride re-apply must come
    # AFTER the config copy that wipes it, per instance.
    $idxCopy = -1
    $idxSave = -1
    for ($i = 0; $i -lt $plan.Actions.Count; $i++) {
        $a = $plan.Actions[$i]
        if ($a.Instance -ne 'client1') { continue }
        if ($idxCopy -lt 0 -and $a.Kind -eq 'CopyConfigTree')             { $idxCopy = $i }
        if ($idxSave -lt 0 -and $a.Kind -eq 'ReapplySavePathOverride')    { $idxSave = $i }
    }
    Assert-True ($idxCopy -ge 0 -and $idxSave -gt $idxCopy) 'the SavePathOverride re-apply is ordered AFTER the config re-copy' "copy=$idxCopy reapply=$idxSave"
    Assert-True $plan.Actions[$idxSave].AfterCopy 'the re-apply is marked as following a copy, which is what makes a failed write fatal'

    $srvLabels = @($plan.Actions | Where-Object { $_.Half -eq 'server' } | ForEach-Object { $_.Label }) -join '; '
    foreach ($want in @('Scenario blanked', 'scenariorunner request', 'scenariorunner give', 'inspector request', 'inspector snapshot', 'setting.xml', 'server.pid', 'host.pid', 'control.cmd')) {
        Assert-True ($srvLabels -match [regex]::Escape($want)) "the plan names the server '$want' target" "labels: $srvLabels"
    }

    $reports = @($plan.Reports | ForEach-Object { $_.Kind })
    Assert-True ($reports -contains 'SavesRetained') 'the plan REPORTS the retained dedicated-server saves rather than deleting them'

    # An empty rig, and a rig with nothing on disk at all.
    Reset-TestHome
    $plan = Invoke-Quiet { Get-RigResetPlan }
    Assert-Equal 0 $plan.Instances.Count 'an empty rig has no instances'
    Assert-Equal 0 $plan.Actions.Count 'an empty rig has nothing to reset'

    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue (Join-Path $script:TempRoot 'ClientRig')
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue (Join-Path $script:TempRoot 'DedicatedServer')
    Assert-NoThrow { Get-RigResetPlan } 'a rig with no ClientRig or DedicatedServer folder at all does not throw'
    Assert-NoThrow { Invoke-RigReset } 'resetting a rig with no folders at all does not throw'
    Reset-TestHome

    # A partially provisioned instance: data/ exists, the tree does not.
    New-TestInstance -Name 'halfbuilt' -Role 'client' -NoTree | Out-Null
    $plan = Invoke-Quiet { Get-RigResetPlan }
    Assert-True (@($plan.Reports | Where-Object { $_.Kind -eq 'NoTree' }).Count -eq 1) 'a partially provisioned instance is reported, not skipped silently'
    Assert-NoThrow { Invoke-RigReset } 'a partially provisioned instance resets without throwing'
    Assert-FileGone (Join-Path (Get-InstanceDataDir 'halfbuilt') 'setting.xml') 'a partially provisioned instance still gets its data/ state reset'
}

function Test-ClientReset {
    if (-not (Test-SectionSelected 'client')) { return }
    Start-Section 'client half: every target gone, every preserved thing present'
    Reset-TestHome
    $data = New-TestInstance -Name 'client1' -Role 'client'
    $bep  = Get-InstanceBepInEx 'client1'

    $res = Invoke-Quiet { Invoke-RigReset }
    Assert-False $res.Refused 'the reset ran on an idle rig'
    Assert-False $res.Skipped 'the reset was not skipped'
    Assert-Equal 0 $res.Failures.Count "no action failed ($($res.Failures -join '; '))"

    Assert-FileGone (Join-Path $data 'setting.xml') 'setting.xml is deleted (it carries StartLocalHost)'
    Assert-FileGone (Join-Path $data 'imgui.ini') 'imgui.ini is deleted (panel layout reframes screenshots)'
    Assert-FileGone (Join-Path $data 'game.pid') 'a stale game.pid is deleted'
    Assert-Equal 0 (@(Get-ChildItem -LiteralPath (Join-Path $data 'userdata\saves') -Force)).Count 'the previous session worlds are gone'
    Assert-Equal 0 (@(Get-ChildItem -LiteralPath (Join-Path $data 'logs') -Force)).Count 'the Unity logs are gone'
    Assert-FileGone (Join-Path $bep 'LogOutput.log') 'LogOutput.log is deleted'
    Assert-FileGone (Join-Path $bep 'LogOutput.log.1') 'the rotated LogOutput.log.1 is deleted too'
    Assert-Equal 0 (@(Get-ChildItem -LiteralPath (Join-Path $bep 'cache') -Force)).Count 'the BepInEx assembly cache is emptied'
    Assert-FileExists (Join-Path $bep 'cache') 'the BepInEx cache folder itself is recreated'
    Assert-Equal 0 (@(Get-ChildItem -LiteralPath (Join-Path $bep 'inspector\requests') -Force)).Count 'unconsumed InspectorPlus requests are gone'
    Assert-Equal 0 (@(Get-ChildItem -LiteralPath (Join-Path $bep 'inspector\snapshots') -Force)).Count 'stale InspectorPlus snapshots are gone'

    # The directories themselves survive, because the next launch writes into them.
    Assert-FileExists (Join-Path $data 'userdata\saves') 'the save root directory survives'
    Assert-FileExists (Join-Path $data 'logs') 'the log directory survives'

    # PRESERVED.
    Assert-FileExists (Join-Path $data 'instance.json') 'the instance manifest is preserved'
    Assert-FileExists (Join-Path $data 'provision.stamp') 'the provision stamp is preserved'
    Assert-FileExists (Join-Path $data 'userdata\mods\Local_Example\Example.dll') 'the seeded mods are preserved (re-seeding is provisioning job)'
    Assert-FileExists (Join-Path $data 'userdata\modconfig.xml') 'the instance modconfig.xml is preserved'
    Assert-FileExists (Join-Path $bep 'plugins\ClientDriver\ClientDriver.dll') 'the deployed ClientDriver plugin is preserved'
    Assert-FileExists (Join-Path $bep 'config\notes.txt') 'a non-cfg file in the config folder is not touched'

    # The registry is never touched by the reset.
    Set-Content -LiteralPath (Join-Path $script:TempRoot 'ClientRig\data\rig.json') -Value '[{"instanceName":"client1"}]' -Encoding utf8
    Invoke-Quiet { Invoke-RigReset } | Out-Null
    Assert-FileExists (Join-Path $script:TempRoot 'ClientRig\data\rig.json') 'the rig registry is preserved (deleting it loses every instance definition)'

    # Config values a previous test flipped are put back from the source install.
    Assert-Match (Get-Content -Raw -LiteralPath (Join-Path $bep 'config\net.spraypaintplus.cfg')) 'Beam Width = 0\.05' 'a config value a previous test flipped is restored from the source install'
    Assert-FileGone (Join-Path $bep 'config\net.leftover.cfg') 'a config file the source install does not have is removed'
    Assert-FileExists (Join-Path $bep 'config\net.inspectorplus.cfg') 'a config the instance was missing is copied in'

    # Reset twice in a row: idempotent, and no failures on an already-clean rig.
    $res = Invoke-Quiet { Invoke-RigReset }
    Assert-Equal 0 $res.Failures.Count 'resetting an already-clean instance does nothing and fails nothing'
}

function Test-InstancesRoot {
    if (-not (Test-SectionSelected 'instancesroot')) { return }
    Start-Section 'the instances root comes from the registry, not from an assumption'
    Reset-TestHome

    # A real rig looks like this: the trees are on the game install's volume (the launcher's
    # -InstancesRoot / STATIONEERS_CLIENTRIG_ROOT), while data/ stays beside the rig. The reset used
    # to join ITS configured root to the instance name, find nothing, and skip the config re-copy and
    # the SavePathOverride re-apply while reporting only "no instance tree".
    $alt = Join-Path $script:TempRoot '_altroot'
    New-Item -ItemType Directory -Force -Path $alt | Out-Null
    $data = New-TestInstance -Name 'hostie' -Role 'host' -TreeRoot $alt
    $bep  = Join-Path $alt 'hostie\BepInEx'
    Set-TestRegistry @([ordered]@{
        instanceName = 'hostie'; index = 1; role = 'host'; port = 27701; gamePort = 27801
        clientId = '900000000001'; username = 'hostie'; instancesRoot = $alt
    })

    $map = Get-RigClientInstanceRootMap
    Assert-Equal $alt $map['hostie'] 'the instances root is read out of rig.json'
    Assert-Equal (Join-Path $alt 'hostie') (Get-RigInstanceTree -Name 'hostie' -RootMap $map).Path 'the tree path is built from the recorded root'
    Assert-Match (Get-RigInstanceTree -Name 'hostie' -RootMap $map).Source 'recorded in rig.json' 'and it says where that answer came from'

    $plan = Invoke-Quiet { Get-RigResetPlan }
    Assert-Equal 0 (@($plan.Reports | Where-Object { $_.Kind -eq 'NoTree' })).Count 'an instance whose tree is on another root is no longer reported as treeless'
    $copy = @($plan.Actions | Where-Object { $_.Kind -eq 'CopyConfigTree' })
    Assert-Equal 1 $copy.Count 'the config re-copy IS planned for a tree outside the default root'
    Assert-Equal (Join-Path $bep 'config') $copy[0].Path 'and it targets the config folder inside the recorded tree'
    $reapply = @($plan.Actions | Where-Object { $_.Kind -eq 'ReapplySavePathOverride' })
    Assert-Equal 1 $reapply.Count 'so is the SavePathOverride re-apply'
    Assert-True $reapply[0].AfterCopy 'and it is marked as following the copy, which is what makes a failed write fatal'

    Assert-Match (Get-Content -Raw -LiteralPath (Join-Path $bep 'config\net.spraypaintplus.cfg')) 'Beam Width = 9\.99' 'fixture check: a previous test value is in place'
    Invoke-Quiet { Invoke-RigReset } | Out-Null
    Assert-Match (Get-Content -Raw -LiteralPath (Join-Path $bep 'config\net.spraypaintplus.cfg')) 'Beam Width = 0\.05' `
        'THE FIX: the config re-copy reaches a tree on another root instead of being skipped'
    Assert-Equal (Join-Path $data 'userdata') (Get-RigSavePathOverride -BepInExDir $bep) `
        'and SavePathOverride is re-applied there, so the instance keeps its own save root'
    Assert-FileGone (Join-Path $bep 'LogOutput.log') 'the BepInEx log in the recorded tree is cleared too'
    Assert-FileGone (Join-Path $data 'setting.xml') 'and the data/ half is reset exactly as before'

    # An entry from before the field existed: today's behaviour, said out loud.
    Reset-TestHome
    New-Item -ItemType Directory -Force -Path $alt | Out-Null
    $data = New-TestInstance -Name 'legacy' -Role 'client' -TreeRoot $alt
    Set-TestRegistry @([ordered]@{ instanceName = 'legacy'; index = 1; role = 'client'; port = 27701 })
    $plan   = Invoke-Quiet { Get-RigResetPlan }
    $noTree = @($plan.Reports | Where-Object { $_.Kind -eq 'NoTree' })
    Assert-Equal 1 $noTree.Count 'an entry that records no root falls back to the configured one rather than throwing'
    Assert-Match $noTree[0].Detail 'records none' 'and the report says the path came from the fallback, not from the registry'
    Assert-Match $noTree[0].Detail 'NOT re-copied' 'the report names what was skipped, instead of only that a tree was missing'
    Assert-NoThrow { Invoke-RigReset } 'the reset still runs for an entry with no recorded root'
    Assert-FileGone (Join-Path $data 'setting.xml') 'and its data/ state is still reset'

    # No registry at all, and a half-written one: an empty map, never a throw.
    Reset-TestHome
    New-TestInstance -Name 'client1' -Role 'client' | Out-Null
    Assert-Equal 0 (Get-RigClientInstanceRootMap).Count 'a rig with no rig.json yields an empty root map'
    Set-Content -LiteralPath (Join-Path $script:TempRoot 'ClientRig\data\rig.json') -Value '[{ not json' -Encoding utf8
    Assert-NoThrow { Get-RigClientInstanceRootMap } 'a half-written rig.json does not throw'
    Assert-Equal 0 (Get-RigClientInstanceRootMap).Count 'and yields an empty map, so every instance uses the configured root'
    Assert-NoThrow { Invoke-RigReset } 'the reset still runs with an unreadable registry'
    Assert-FileGone (Join-Path (Get-InstanceDataDir 'client1') 'setting.xml') 'and a default-rooted instance is reset as usual'
    Assert-FileExists (Join-Path $script:TempRoot 'ClientRig\data\rig.json') 'the registry itself is still never touched by the reset'
}

function Test-SavePathOverride {
    if (-not (Test-SectionSelected 'savepath')) { return }
    Start-Section 'SavePathOverride survives the config re-copy (THE assertion)'
    Reset-TestHome
    $data = New-TestInstance -Name 'host1' -Role 'host'
    $bep  = Get-InstanceBepInEx 'host1'
    $want = Join-Path $data 'userdata'

    Assert-Equal $want (Get-RigSavePathOverride -BepInExDir $bep) 'fixture check: the instance starts with its own save root'

    # HAZARD, measured rather than asserted from memory: the config re-copy on its
    # own WIPES the redirect, and an instance without it writes its worlds into the
    # developer's tier-1 save folder.
    $copy = @((Invoke-Quiet { Get-RigResetPlan }).Actions | Where-Object { $_.Kind -eq 'CopyConfigTree' })[0]
    Invoke-RigResetAction -Action $copy
    Assert-True ($null -eq (Get-RigSavePathOverride -BepInExDir $bep)) `
        'HAZARD: the config re-copy ALONE wipes SavePathOverride, which is why the re-apply exists'

    # And the whole reset puts it back, which is the guarantee.
    Reset-TestHome
    $data = New-TestInstance -Name 'host1' -Role 'host'
    $bep  = Get-InstanceBepInEx 'host1'
    $want = Join-Path $data 'userdata'
    Invoke-Quiet { Invoke-RigReset } | Out-Null
    Assert-Equal $want (Get-RigSavePathOverride -BepInExDir $bep) `
        'GUARANTEE: after a full reset the instance still points at its OWN save root, never the developer folder'
    Assert-Match (Get-Content -Raw -LiteralPath (Join-Path $bep 'config\stationeers.launchpad.cfg')) 'SomeSetting = pristine' `
        'the rest of the StationeersLaunchPad config really was replaced from the source install'

    # A client instance is treated the same way.
    Reset-TestHome
    $data = New-TestInstance -Name 'client1' -Role 'client'
    $bep  = Get-InstanceBepInEx 'client1'
    Invoke-Quiet { Invoke-RigReset } | Out-Null
    Assert-Equal (Join-Path $data 'userdata') (Get-RigSavePathOverride -BepInExDir $bep) 'a client instance keeps its own save root too'

    # The shared writer, called directly, keeps the provision-time semantics:
    # fatal for a host, loud for a client, when the config is not there to write.
    Reset-TestHome
    $data = New-TestInstance -Name 'nocfg' -Role 'client' -NoLaunchPadConfig
    $bep  = Get-InstanceBepInEx 'nocfg'
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $script:SourceDir   # so the reset cannot re-create it
    Assert-Throws { Set-RigSavePathOverride -BepInExDir $bep -UserDataDir (Join-Path $data 'userdata') -InstanceRole 'host' } `
        'a HOST with no StationeersLaunchPad config throws rather than being left without the redirect' 'Refusing to leave a host'
    Assert-False (Invoke-Quiet { Set-RigSavePathOverride -BepInExDir $bep -UserDataDir (Join-Path $data 'userdata') -InstanceRole 'client' }) `
        'a CLIENT with no StationeersLaunchPad config warns and reports failure instead of throwing'

    # Whether a failed re-apply is FATAL depends on whether this reset is what
    # broke it, and both directions matter.
    $plan = Invoke-Quiet { Get-RigResetPlan }
    $act  = @($plan.Actions | Where-Object { $_.Kind -eq 'ReapplySavePathOverride' })[0]
    Assert-Equal 'client' $act.Role 'the plan carries the instance role for the re-apply'
    Assert-False $act.AfterCopy 'with no source install the re-apply is not marked as following a config copy'

    # Nothing was wiped: warn, relabel, and let the session start. Failing here
    # would make the lock unobtainable, and -Provision -Force needs the lock, so
    # the rig would be unrepairable.
    Assert-NoThrow { Invoke-RigResetAction -Action $act } 'a missing redirect that this reset did NOT cause is a warning, not a hard stop (the rig must stay lockable)'
    Assert-Match $act.Label 'NOT written' 'the printed summary says the redirect was not written, rather than claiming it was'

    # This reset DID wipe it: fatal, because the next -Start would write worlds
    # into the developer's tier-1 folder because of something this code did.
    $act2 = @((Invoke-Quiet { Get-RigResetPlan }).Actions | Where-Object { $_.Kind -eq 'ReapplySavePathOverride' })[0]
    $act2.AfterCopy = $true
    Assert-Throws { Invoke-RigResetAction -Action $act2 } 'a redirect this reset wiped and could not write back is FATAL, even on a client' 'NO separate save root'

    # And an unknown role is treated as a host, because the expensive mistake is
    # assuming a host is a client.
    $act3 = @((Invoke-Quiet { Get-RigResetPlan }).Actions | Where-Object { $_.Kind -eq 'ReapplySavePathOverride' })[0]
    $act3.Role = 'unknown'
    $act3.AfterCopy = $true
    Assert-Throws { Invoke-RigResetAction -Action $act3 } 'an instance with an UNKNOWN role is treated as a host' 'Refusing to leave a host'

    New-SourceInstall
    Use-TestPaths
}

function Test-PidHandling {
    if (-not (Test-SectionSelected 'pid')) { return }
    Start-Section 'pid files: live survives, dead goes, recycled is trusted in neither direction'
    Reset-TestHome

    # A LIVE instance. $PID stands in for the game because the test wiring set the
    # client image name to pwsh.
    $data = New-TestInstance -Name 'live1' -Role 'client' -RawPid "$PID"
    $plan = Invoke-Quiet { Get-RigResetPlan }
    Assert-Equal 0 (@($plan.Actions | Where-Object { $_.Path -like '*game.pid' })).Count 'a LIVE instance game.pid is never planned for deletion'
    Assert-True (@($plan.Reports | Where-Object { $_.Kind -eq 'PreservedLivePid' }).Count -ge 1) 'the live pid file is reported as preserved'

    # ... but a live instance also makes the rig busy, so the reset refuses
    # outright. Both guarantees hold at once.
    $res = Invoke-Quiet { Invoke-RigReset }
    Assert-True $res.Refused 'a reset with a live instance is refused (the busy guard fires first)'
    Assert-FileExists (Join-Path $data 'game.pid') 'the live pid file is still there after the refusal'
    Assert-FileExists (Join-Path $data 'setting.xml') 'the refusal deleted nothing at all'

    # A dead pid.
    Reset-TestHome
    $data = New-TestInstance -Name 'dead1' -Role 'client' -RawPid "$script:DeadPid"
    Invoke-Quiet { Invoke-RigReset } | Out-Null
    Assert-FileGone (Join-Path $data 'game.pid') 'a pid file naming a dead process is deleted'

    # A live process with the WRONG image: a recycled id, which must not be
    # trusted as proof of life.
    Reset-TestHome
    $data = New-TestInstance -Name 'recycled' -Role 'client' -RawPid "$PID"
    Initialize-RigResetPaths -RigHome $script:TempRoot -SourceInstall $script:SourceDir `
        -InstanceRoot (Join-Path $script:TempRoot 'ClientRig\instances') `
        -UserDataDir $script:UserDataDir -SharedDataDir $script:SharedDir -PlayerPrefsKey $script:FakePrefsKey `
        -ServerImageName 'rocketstation_DedicatedServer' -ClientImageName 'rocketstation'
    Assert-True (Test-RigResetPidStale -File (Join-Path $data 'game.pid') -ImageNames @('rocketstation')) `
        'a live process with the wrong image name is treated as a dead pid file'
    Invoke-Quiet { Invoke-RigReset } | Out-Null
    Assert-FileGone (Join-Path $data 'game.pid') 'the recycled pid file is deleted rather than protected forever'
    Use-TestPaths

    # Garbage and empty pid files.
    Reset-TestHome
    $data = New-TestInstance -Name 'garbage' -Role 'client' -RawPid 'not-a-number'
    Assert-NoThrow { Get-RigResetPlan } 'a non-numeric pid file does not throw while planning'
    Assert-NoThrow { Invoke-RigReset } 'a non-numeric pid file does not throw while resetting'
    Assert-FileGone (Join-Path $data 'game.pid') 'a non-numeric pid file is treated as stale and removed'

    Reset-TestHome
    $data = New-TestInstance -Name 'empty' -Role 'client' -RawPid ''
    Assert-NoThrow { Invoke-RigReset } 'an empty pid file does not throw'
    Assert-FileGone (Join-Path $data 'game.pid') 'an empty pid file is treated as stale and removed'

    # Server-side pid files follow the same rules, including the host wrapper.
    Reset-TestHome
    New-TestServerState -ServerPid "$script:DeadPid" -HostPid "$script:DeadPid"
    Invoke-Quiet { Invoke-RigReset } | Out-Null
    $sd = Join-Path $script:TempRoot 'DedicatedServer\data'
    Assert-FileGone (Join-Path $sd 'server.pid') 'a stale server.pid is deleted'
    Assert-FileGone (Join-Path $sd 'host.pid') 'a stale host.pid is deleted'
    Assert-FileGone (Join-Path $sd 'control.cmd') 'a stale control.cmd is deleted once nothing is left to consume it'

    Reset-TestHome
    New-TestServerState -ServerPid "$PID" -HostPid "$script:DeadPid"
    $res = Invoke-Quiet { Invoke-RigReset }
    Assert-True $res.Refused 'a reset with a live dedicated server process is refused, even with nobody connected'
    Assert-FileExists (Join-Path $sd 'server.pid') 'the live server.pid survives'
    Assert-FileExists (Join-Path $sd 'control.cmd') 'control.cmd is not removed while the server is alive'
}

function Test-ServerReset {
    if (-not (Test-SectionSelected 'server')) { return }
    Start-Section 'server half: scenario blanked, drop files cleared, saves kept'
    Reset-TestHome
    New-TestServerState -Scenario 'pgp-cable-burn-probe'
    $install = Join-Path $script:TempRoot 'DedicatedServer\install'
    $data    = Join-Path $script:TempRoot 'DedicatedServer\data'
    $srCfg   = Join-Path $install 'BepInEx\config\net.scenariorunner.cfg'

    Assert-Equal 'pgp-cable-burn-probe' (Get-RigConfigSettingValue -Path $srCfg -Setting 'Scenario') 'fixture check: a scenario is selected'
    Invoke-Quiet { Invoke-RigReset } | Out-Null

    Assert-True ([string]::IsNullOrEmpty((Get-RigConfigSettingValue -Path $srCfg -Setting 'Scenario'))) 'the ScenarioRunner Scenario value is blanked'
    $text = Get-Content -Raw -LiteralPath $srCfg
    Assert-Match $text '## Settings file was created by plugin ScenarioRunner' 'blanking keeps the file header comment'
    Assert-Match $text '## Scenario id to run after world load' 'blanking keeps the setting description comment'
    Assert-Match $text '\[Probe\]' 'blanking keeps the section header'
    Assert-Equal '5' (Get-RigConfigSettingValue -Path $srCfg -Setting 'Delay Ticks') 'blanking leaves every other value alone'
    Assert-Equal 'false' (Get-RigConfigSettingValue -Path $srCfg -Setting 'Log Inventory On First Tick') 'blanking leaves the last value alone too'

    Assert-Equal 0 (@(Get-ChildItem -LiteralPath (Join-Path $install 'BepInEx\scenariorunner\requests') -Force)).Count 'stray ScenarioRunner requests are cleared'
    Assert-Equal 0 (@(Get-ChildItem -LiteralPath (Join-Path $install 'BepInEx\scenariorunner\give') -Force)).Count 'stray ScenarioRunner give files are cleared'
    Assert-Equal 0 (@(Get-ChildItem -LiteralPath (Join-Path $install 'BepInEx\inspector\requests') -Force)).Count 'stray InspectorPlus requests are cleared'
    Assert-Equal 0 (@(Get-ChildItem -LiteralPath (Join-Path $install 'BepInEx\inspector\snapshots') -Force)).Count 'stale InspectorPlus snapshots are cleared'
    Assert-FileGone (Join-Path $data 'setting.xml') "the dedicated server's setting.xml is deleted"

    # PRESERVED.
    Assert-FileExists (Join-Path $data 'saves\Luna\Luna.save') 'staged worlds are preserved'
    Assert-FileExists (Join-Path $data 'mods\Local_Example\Example.dll') 'synced mods are preserved'
    Assert-FileExists (Join-Path $install 'modconfig.xml') 'the baked modconfig.xml is preserved'
    Assert-FileExists (Join-Path $install 'BepInEx\config\net.powergridplus.cfg') 'other server configs are NOT reset (rig-owned versus mod-owned is undecided)'
    Assert-Equal 'Something = 1' (Get-Content -Raw -LiteralPath (Join-Path $install 'BepInEx\config\net.powergridplus.cfg')).Trim() 'and their values are untouched'

    # A config touched after the reset is REPORTED on the next one.
    Start-Sleep -Milliseconds 1100
    Set-Content -LiteralPath (Join-Path $install 'BepInEx\config\net.powergridplus.cfg') -Value 'Something = 2' -Encoding utf8
    $plan = Invoke-Quiet { Get-RigResetPlan }
    $touched = @($plan.Reports | Where-Object { $_.Kind -eq 'ConfigTouched' })
    Assert-Equal 1 $touched.Count 'a server config changed since the last reset is reported'
    Assert-Match $touched[0].Detail 'net\.powergridplus\.cfg' 'the report names the file that moved'

    # An absent scenariorunner config is not an error.
    Reset-TestHome
    Assert-NoThrow { Invoke-RigReset } 'a server half with no ScenarioRunner config does not throw'
}

function Test-WhatIf {
    if (-not (Test-SectionSelected 'whatif')) { return }
    Start-Section '-WhatIf changes nothing'
    Reset-TestHome
    New-TestInstance -Name 'client1' -Role 'client' | Out-Null
    New-TestServerState

    $before = Get-TreeFingerprint $script:TempRoot
    $res    = Invoke-Quiet { Invoke-RigReset -WhatIf }
    $after  = Get-TreeFingerprint $script:TempRoot
    Assert-Equal $before $after '-WhatIf did not change one byte, including the shared-state baseline'
    Assert-Equal 0 (@($res.Performed)).Count '-WhatIf performed no actions'
    Assert-FileGone (Get-RigResetStateFilePath) '-WhatIf did not write the shared-state baseline'
}

function Test-BusyRefusal {
    if (-not (Test-SectionSelected 'busy')) { return }
    Start-Section 'the reset refuses while the rig is in use'
    Reset-TestHome
    $data = New-TestInstance -Name 'client1' -Role 'client' -RawPid "$PID"
    New-TestServerState

    # Fingerprinted per half rather than over the whole temp root, because a
    # refusal still writes the session's shared-state baseline at the rig root
    # (see below). Nothing inside either half may move.
    $beforeClient = Get-TreeFingerprint (Join-Path $script:TempRoot 'ClientRig')
    $beforeServer = Get-TreeFingerprint (Join-Path $script:TempRoot 'DedicatedServer')
    $res = Invoke-Quiet { Invoke-RigReset }
    Assert-True  $res.Refused 'the reset refuses while a client instance is live'
    Assert-Equal $beforeClient (Get-TreeFingerprint (Join-Path $script:TempRoot 'ClientRig')) 'a refused reset deleted nothing on the client half'
    Assert-Equal $beforeServer (Get-TreeFingerprint (Join-Path $script:TempRoot 'DedicatedServer')) 'a refused reset deleted nothing on the server half'
    Assert-Match $res.RefusalReason 'client instance' 'the refusal names what is running'

    # A refused reset still captures the baseline. Without it this session would
    # diff against a PREVIOUS session's snapshot at unlock and report that
    # session's changes as its own.
    Assert-FileExists (Get-RigResetStateFilePath) 'a refused reset still captures this session shared-state baseline'

    # A lock acquired while the rig is busy still succeeds and still prints an
    # owner id: an unclean rig must not become an unlockable one.
    Reset-TestHome
    New-TestInstance -Name 'client1' -Role 'client' -RawPid "$PID" | Out-Null
    $owner = $null
    Assert-NoThrow { $script:BusyOwner = New-RigLock -Purpose 'busy rig probe' -Tool 'rig-reset.tests.ps1' } `
        'acquiring the lock on a busy rig still succeeds (the reset is skipped, not the lock)'
    Assert-Match "$script:BusyOwner" '^[0-9a-f]{8}$' 'the owner id is still returned when the reset was skipped'
    Assert-FileExists (Join-Path (Get-InstanceDataDir 'client1') 'setting.xml') 'and the busy instance state was left alone'
}

function Test-LockIntegration {
    if (-not (Test-SectionSelected 'lock')) { return }
    Start-Section 'lock integration: a NEW lock resets, re-asserting one does not'
    Reset-TestHome
    New-TestInstance -Name 'client1' -Role 'client' | Out-Null
    $data = Get-InstanceDataDir 'client1'

    $owner = Invoke-Quiet { New-RigLock -Purpose 'a fresh session' -Tool 'rig-reset.tests.ps1' }
    Assert-Match "$owner" '^[0-9a-f]{8}$' 'a new lock was acquired'
    Assert-FileGone (Join-Path $data 'setting.xml') 'acquiring a NEW lock reset the rig by construction, with nothing extra to remember'
    Assert-FileExists (Get-RigResetStateFilePath) 'a new lock captured the shared-state baseline'

    # Re-assert: an agent changing its purpose or TTL mid-test must not have its
    # own run wiped.
    Set-Content -LiteralPath (Join-Path $data 'setting.xml') -Value '<SettingData />' -Encoding utf8
    New-Item -ItemType Directory -Force -Path (Join-Path $data 'userdata\saves\MidTestWorld') | Out-Null
    Set-Content -LiteralPath (Join-Path $data 'userdata\saves\MidTestWorld\MidTestWorld.save') -Value 'in progress' -Encoding utf8
    $again = Invoke-Quiet { New-RigLock -Purpose 'same session, new purpose' -CallerId $owner -Tool 'rig-reset.tests.ps1' }
    Assert-Equal $owner $again 're-asserting keeps the same owner id'
    Assert-FileExists (Join-Path $data 'setting.xml') 'RE-ASSERTING a lock does NOT reset: mid-test state survives'
    Assert-FileExists (Join-Path $data 'userdata\saves\MidTestWorld\MidTestWorld.save') 'a world created mid-test survives a lock re-assert'

    # Taking the rig again after a release IS a new session, so it does reset.
    Invoke-Quiet { Remove-RigLock -CallerId $owner } | Out-Null
    Invoke-Quiet { New-RigLock -Purpose 'a second session' -Tool 'rig-reset.tests.ps1' } | Out-Null
    Assert-FileGone (Join-Path $data 'userdata\saves\MidTestWorld\MidTestWorld.save') 'releasing and re-taking the lock IS a new session, so it resets'

    # -KeepState: skipped, and loudly.
    Reset-TestHome
    New-TestInstance -Name 'client1' -Role 'client' | Out-Null
    $data = Get-InstanceDataDir 'client1'
    $before = Get-TreeFingerprint (Join-Path $script:TempRoot 'ClientRig')
    $owner  = Invoke-Quiet { New-RigLock -Purpose 'staged on purpose' -Tool 'rig-reset.tests.ps1' -KeepState }
    $after  = Get-TreeFingerprint (Join-Path $script:TempRoot 'ClientRig')
    Assert-Equal $before $after '-KeepState left the whole client tree untouched'
    Assert-FileExists (Join-Path $data 'userdata\saves\PreviousWorld\PreviousWorld.save') '-KeepState kept the staged world'

    # The warning and information streams are merged into the output so the text
    # itself can be asserted. -WarningVariable is not available here: these are
    # simple functions by design (rig-lock.ps1 is written the same way), so they
    # have no common parameters.
    Reset-TestHome
    New-TestInstance -Name 'client1' -Role 'client' | Out-Null
    $keepText = (Invoke-RigReset -KeepState 3>&1 6>&1 | Out-String)
    Assert-Match $keepText 'KeepState' '-KeepState says out loud that it skipped the reset'
    Assert-Match $keepText 'inherits whatever the previous one left behind' '-KeepState names the consequence, not just the flag'
    Assert-Match $keepText 'would have reset' '-KeepState prints exactly what it skipped'
    Assert-FileExists (Join-Path (Get-InstanceDataDir 'client1') 'setting.xml') '-KeepState really did skip the deletes'
}

function Test-SharedState {
    if (-not (Test-SectionSelected 'shared')) { return }
    Start-Section 'shared state is reported, never restored'
    Reset-TestHome

    Set-Content -LiteralPath (Join-Path $script:SharedDir 'PlayerCookie-v2.xml') -Encoding utf8 -Value @(
        '<PlayerCookie>'
        '  <Worlds>'
        '    <World Name="one" />'
        '    <World Name="two" />'
        '  </Worlds>'
        '</PlayerCookie>'
    )
    Set-Content -LiteralPath (Join-Path $script:SharedDir 'Blueprints\one.blueprint') -Value 'bp' -Encoding utf8

    $snap = Get-RigSharedStateSnapshot
    Assert-Equal '2' $snap.Values['cookie.worlds'] 'the snapshot counts the cookie World blocks'
    Assert-Equal '1' $snap.Values['blueprints.files'] 'the snapshot counts the blueprint files'
    Assert-True ($snap.Values.Contains('cookie.bytes')) 'the snapshot records the cookie size'
    Assert-Equal 'unreadable' $snap.Values['prefs'] 'a PlayerPrefs key that does not exist degrades instead of throwing'

    Assert-Equal 0 (Compare-RigSharedState -Before $snap -After $snap).Count 'a snapshot compared against itself reports no drift'

    # A real change is named.
    Set-Content -LiteralPath (Join-Path $script:SharedDir 'Blueprints\two.blueprint') -Value 'bp' -Encoding utf8
    $delta = @(Compare-RigSharedState -Before $snap)
    Assert-Equal 1 $delta.Count 'one change produces exactly one drift line'
    Assert-Match $delta[0] "blueprints\.files : '1' -> '2'" 'the drift line names the old and new value'

    # And it survives the JSON round trip through the baseline file.
    Save-RigSharedStateBaseline -Snapshot $snap -LastResetUtc (Get-RigNowUtc)
    $reloaded = Get-RigSharedStateBaseline
    Assert-True ($null -ne $reloaded) 'the baseline round trips through disk'
    $delta = @(Compare-RigSharedState -Before $reloaded)
    Assert-Equal 1 $delta.Count 'the drift survives the JSON round trip'
    Assert-Match $delta[0] "blueprints\.files : '1' -> '2'" 'the reloaded baseline names the same delta'
    Assert-NoThrow { Write-RigSharedStateDrift } 'the drift report renders without throwing'

    # The snapshot is READ ONLY. Nothing about the shared state may move.
    $before = Get-TreeFingerprint $script:SharedDir
    $null = Get-RigSharedStateSnapshot
    $null = Compare-RigSharedState -Before $snap
    Invoke-Quiet { Write-RigSharedStateDrift } | Out-Null
    Assert-Equal $before (Get-TreeFingerprint $script:SharedDir) 'snapshotting and comparing never writes to the shared per-user state'

    # There is deliberately no way to put it back.
    Assert-True ($null -eq (Get-Command 'Restore-RigSharedState' -ErrorAction SilentlyContinue)) 'no restore function exists, because restoring it would be the forbidden write'

    Reset-TestHome
    Assert-NoThrow { Get-RigSharedStateSnapshot } 'a missing shared-state folder does not throw'
    Assert-Equal 'absent' (Get-RigSharedStateSnapshot).Values['cookie.bytes'] 'a missing cookie is recorded as absent'
    Remove-Item -Force -ErrorAction SilentlyContinue (Get-RigResetStateFilePath)
    Assert-NoThrow { Write-RigSharedStateDrift } 'no baseline at all is reported, not thrown'
}

function Test-Robustness {
    if (-not (Test-SectionSelected 'robust')) { return }
    Start-Section 'robustness: broken inputs behave rather than throw'
    Reset-TestHome

    New-TestInstance -Name 'broken' -Role 'client' -BrokenManifest | Out-Null
    Assert-Equal 'unknown' (Get-RigInstanceRole -DataDir (Get-InstanceDataDir 'broken')) 'a half-written manifest degrades to an unknown role'
    Assert-NoThrow { Get-RigResetPlan } 'a half-written manifest does not throw while planning'
    Assert-NoThrow { Invoke-RigReset } 'a half-written manifest does not stop the reset (the redirect is still written, as a host)'
    Assert-FileGone (Join-Path (Get-InstanceDataDir 'broken') 'setting.xml') 'and the instance was still reset'

    Reset-TestHome
    New-TestInstance -Name 'nomanifest' -Role 'client' -NoManifest | Out-Null
    Assert-NoThrow { Invoke-RigReset } 'an instance with no manifest at all resets without throwing'

    # No source install: the config re-copy is skipped LOUDLY and everything else
    # still runs. A missing Directory.Build.props must not silently mean "clean".
    Reset-TestHome
    New-TestInstance -Name 'client1' -Role 'client' | Out-Null
    Initialize-RigResetPaths -RigHome $script:TempRoot -SourceInstall (Join-Path $script:TempRoot '_gone') `
        -InstanceRoot (Join-Path $script:TempRoot 'ClientRig\instances') `
        -UserDataDir $script:UserDataDir -SharedDataDir $script:SharedDir -PlayerPrefsKey $script:FakePrefsKey `
        -ServerImageName 'pwsh' -ClientImageName 'pwsh'
    $plan = Invoke-Quiet { Get-RigResetPlan }
    Assert-Equal 0 (@($plan.Actions | Where-Object { $_.Kind -eq 'CopyConfigTree' })).Count 'with no source install there is no config re-copy'
    Assert-True (@($plan.Reports | Where-Object { $_.Kind -eq 'ConfigCopySkipped' }).Count -eq 1) 'the skipped config re-copy is reported loudly'
    Invoke-Quiet { Invoke-RigReset } | Out-Null
    Assert-FileGone (Join-Path (Get-InstanceDataDir 'client1') 'setting.xml') 'the rest of the reset still ran'
    Assert-Equal (Join-Path (Get-InstanceDataDir 'client1') 'userdata') (Get-RigSavePathOverride -BepInExDir (Get-InstanceBepInEx 'client1')) `
        'and SavePathOverride is still asserted even when no config was copied'
    Use-TestPaths

    # A stale seeded mod is REPORTED, never deleted: the fix is a re-provision.
    Reset-TestHome
    New-TestInstance -Name 'client1' -Role 'client' | Out-Null
    $srcMod = Join-Path $script:UserDataDir 'mods\Local_Example'
    New-Item -ItemType Directory -Force -Path $srcMod | Out-Null
    Set-Content -LiteralPath (Join-Path $srcMod 'Example.dll') -Value 'newer dll bytes' -Encoding utf8
    (Get-Item -LiteralPath (Join-Path $srcMod 'Example.dll')).LastWriteTimeUtc = [DateTime]::UtcNow.AddHours(1)
    $plan = Invoke-Quiet { Get-RigResetPlan }
    $stale = @($plan.Reports | Where-Object { $_.Kind -eq 'StaleMod' })
    Assert-Equal 1 $stale.Count 'a seeded mod older than its source is reported'
    Assert-Match $stale[0].Detail 'Provision -Force' 'the stale-mod report names the fix'
    Invoke-Quiet { Invoke-RigReset } | Out-Null
    Assert-FileExists (Join-Path (Get-InstanceDataDir 'client1') 'userdata\mods\Local_Example\Example.dll') 'the stale mod is NOT deleted, only named'

    # A failing action is loud and names the instance, and the rest still runs.
    Reset-TestHome
    New-TestInstance -Name 'client1' -Role 'client' | Out-Null
    $plan = Invoke-Quiet { Get-RigResetPlan }
    $plan.Actions += (New-RigResetAction -Half 'client' -Instance 'client1' -Kind 'BlankSetting' `
        -Path (Join-Path (Get-InstanceBepInEx 'client1') 'config\stationeers.launchpad.cfg') -Setting 'NoSuchSetting' `
        -Label 'a deliberately impossible action' -Reason 'test fixture')
    Assert-Throws { Invoke-RigReset -Plan $plan } 'a failed action throws rather than passing silently' 'HALF RESET'
    Assert-FileGone (Join-Path (Get-InstanceDataDir 'client1') 'setting.xml') 'the actions that could run still ran before the failure was reported'
}

# =============================================================================
# Run
# =============================================================================

Write-Host 'TestRig state hygiene: offline test suite'
Write-Host "  library : $(Join-Path $PSScriptRoot 'rig-reset.ps1')"

# Fingerprint the REAL rig's own state before anything, and verify it after. This
# suite is a test of a destructive mechanism; it must never touch the rig it is
# testing.
$script:RealWatch = @{}
foreach ($rel in @(
    'session.lock', 'session.state.json',
    'DedicatedServer\data\setting.xml', 'DedicatedServer\data\server.pid', 'DedicatedServer\data\control.cmd',
    'DedicatedServer\install\BepInEx\config\net.scenariorunner.cfg'
)) {
    $p = Join-Path $script:RealHome $rel
    $script:RealWatch[$rel] = if (Test-Path -LiteralPath $p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash } else { '<absent>' }
}
$script:RealSaveCount = @(Get-ChildItem -LiteralPath (Join-Path $script:RealHome 'DedicatedServer\data\saves') -Directory -ErrorAction SilentlyContinue).Count
Write-Host "  real rig: $($script:RealWatch.Count) state file(s) fingerprinted, $script:RealSaveCount dedicated-server save(s) counted (all verified untouched at the end)"

$testHome = New-TestHome
Write-Host "  temp    : $testHome"

try {
    Test-Paths
    Test-Plan
    Test-ClientReset
    Test-InstancesRoot
    Test-SavePathOverride
    Test-PidHandling
    Test-ServerReset
    Test-WhatIf
    Test-BusyRefusal
    Test-LockIntegration
    Test-SharedState
    Test-Robustness
}
finally {
    Start-Section 'safety'
    foreach ($rel in @($script:RealWatch.Keys | Sort-Object)) {
        $p   = Join-Path $script:RealHome $rel
        $now = if (Test-Path -LiteralPath $p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash } else { '<absent>' }
        Assert-Equal $script:RealWatch[$rel] $now "the REAL $rel was not touched by this run"
    }
    $nowSaves = @(Get-ChildItem -LiteralPath (Join-Path $script:RealHome 'DedicatedServer\data\saves') -Directory -ErrorAction SilentlyContinue).Count
    Assert-Equal $script:RealSaveCount $nowSaves 'the REAL dedicated-server save tree still has every world it started with'

    if ($script:TempRoot -and (Test-Path -LiteralPath $script:TempRoot)) {
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $script:TempRoot
    }
    Assert-False (Test-Path -LiteralPath $script:TempRoot) 'the temp home was cleaned up'
}

Write-Host ''
Write-Host ('-' * 64)
Write-Host "assertions: $($script:Passed) passed, $($script:Failed) failed"
if ($script:Failed -gt 0) {
    Write-Host ''
    Write-Host 'failures:'
    $script:Failures | ForEach-Object { Write-Host "  - $_" }
    Write-Host ''
    Write-Host 'RESULT: FAIL'
    exit 1
}
Write-Host 'RESULT: PASS'
exit 0
