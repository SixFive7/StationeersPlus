<#
.SYNOPSIS
    Offline test suite for the unified launcher (TestRig/testrig.ps1 and the two
    half libraries under TestRig/lib/).

.DESCRIPTION
    THIS LAYER HAD NO TESTS AT ALL. The two launchers it replaces were 3,736 lines
    with zero coverage, which is how three blocking defects hid in the process
    seam at once: a pid check with no image test that made a recycled process id
    refuse every mutating command, a game-version reader pointed at a file that has
    never existed, and an unquoted argument list that broke every lock the playtest
    harness tried to take. Each of those is a one-line assertion here.

    It runs entirely offline: no game, no dedicated server, no client instance, no
    network, no lock on the real rig. Paths point at a throwaway directory through
    Initialize-RigCommon / Initialize-RigServer / Initialize-RigClient, and a
    fingerprint of the real rig's own state files is taken before the run and
    verified untouched after it.

    No Pester, for the same reason the other three suites have none: a dependency
    that has to be installed before the rig can be tested is a dependency that
    stops the rig from being tested.

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
. (Join-Path $PSScriptRoot 'lib\common.ps1')
. (Join-Path $PSScriptRoot 'lib\server.ps1')
. (Join-Path $PSScriptRoot 'lib\client.ps1')

# =============================================================================
# Assert helpers (same shape as rig-lock.tests.ps1 and rig-reset.tests.ps1)
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

function Invoke-Quiet { param([scriptblock] $Body) return (& $Body 6>$null 3>$null) }

function Test-SectionSelected { param([string] $Name) return ($Name -like $Section) }

# =============================================================================
# Fixtures
# =============================================================================

$script:TempRoot  = $null   # the fake TestRig/
$script:SourceDir = $null   # the fake "developer's game install"
$script:RepoDir   = $null   # the fake repository root
$script:UserData  = $null   # the fake Documents\My Games\Stationeers
$script:RealHome  = $PSScriptRoot
$script:DeadPid   = 999999999

function New-TempTree {
    $script:RepoDir   = Join-Path ([IO.Path]::GetTempPath()) ("testrig-tests-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    $script:TempRoot  = Join-Path $script:RepoDir 'TestRig'
    $script:SourceDir = Join-Path $script:RepoDir 'FakeInstall'
    $script:UserData  = Join-Path $script:RepoDir 'FakeUserData'

    foreach ($d in @(
        $script:TempRoot
        (Join-Path $script:TempRoot 'DedicatedServer\install')
        (Join-Path $script:TempRoot 'DedicatedServer\data')
        (Join-Path $script:TempRoot 'ClientRig\data')
        (Join-Path $script:TempRoot 'ClientRig\instances')
        (Join-Path $script:SourceDir 'rocketstation_Data\Managed')
        (Join-Path $script:SourceDir 'rocketstation_Data\StreamingAssets')
        (Join-Path $script:UserData 'mods')
        (Join-Path $script:RepoDir 'Mods\ExampleMod\ExampleMod\bin\Release')
        (Join-Path $script:RepoDir 'Mods\ExampleMod\ExampleMod\About')
        (Join-Path $script:RepoDir 'Mods\Template')
        (Join-Path $script:RepoDir 'Plans\PlanMod\PlanMod\bin\Release')
    )) {
        New-Item -ItemType Directory -Force -Path $d | Out-Null
    }

    # A plausible client install: both markers the one path resolver checks, plus
    # the version.ini the one version reader reads.
    Set-Content -LiteralPath (Join-Path $script:SourceDir 'rocketstation.exe') -Value 'not really an exe' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $script:SourceDir 'rocketstation_Data\Managed\Assembly-CSharp.dll') -Value 'not really a dll' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $script:SourceDir 'rocketstation_Data\StreamingAssets\version.ini') `
        -Value "UPDATEVERSION=Update 0.2.9999.12345`nsomething else entirely" -Encoding utf8

    Set-Content -LiteralPath (Join-Path $script:RepoDir 'Directory.Build.props') -Encoding utf8 -Value @"
<Project>
  <PropertyGroup>
    <StationeersPath>$($script:SourceDir)</StationeersPath>
  </PropertyGroup>
</Project>
"@

    Set-Content -LiteralPath (Join-Path $script:RepoDir 'Mods\ExampleMod\ExampleMod\bin\Release\ExampleMod.dll') -Value 'mod bytes' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $script:RepoDir 'Plans\PlanMod\PlanMod\bin\Release\PlanMod.dll') -Value 'plan bytes' -Encoding utf8
}

function Use-TestPaths {
    param([string] $InstancesRoot, [switch] $InstancesRootTyped)
    Initialize-RigCommon -RigHome $script:TempRoot -RepoRoot $script:RepoDir `
        -BuildProps (Join-Path $script:RepoDir 'Directory.Build.props') `
        -SteamcmdPath (Join-Path $script:SourceDir 'rocketstation.exe') `
        -UserDataDir $script:UserData
    Initialize-RigServer -RigHome $script:TempRoot -LauncherPath (Join-Path $script:TempRoot 'testrig.ps1')
    if ($InstancesRoot) {
        Initialize-RigClient -RigHome $script:TempRoot -InstancesRoot $InstancesRoot -InstancesRootTyped:$InstancesRootTyped
    }
    else {
        Initialize-RigClient -RigHome $script:TempRoot
    }
}

function Set-FakeRegistry {
    # rig.json as the launcher writes it, so the target resolver and the entry
    # lookup see a realistic rig with no game anywhere near it.
    param([string[]] $Names = @('hostie', 'joiner'))
    $i = 0
    $entries = @($Names | ForEach-Object {
        $i++
        [pscustomobject]@{
            instanceName = $_
            index        = $i
            role         = if ($i -eq 1) { 'host' } else { 'client' }
            port         = (Get-RigControlPortBase) + $i
            gamePort     = (Get-RigGamePortBase) + $i
            clientId     = "90000000000$i"
            username     = $_
            width        = 800
            height       = 600
            forceGameplayInput = $true
            instancesRoot = (Join-Path $script:TempRoot 'ClientRig\instances')
            provisionedUtc = '2026-01-01T00:00:00Z'
        }
    })
    New-Item -ItemType Directory -Force -Path (Join-Path $script:TempRoot 'ClientRig\data') | Out-Null
    ,@($entries) | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $script:TempRoot 'ClientRig\data\rig.json') -Encoding utf8
}

function Resolve-ForTest {
    # What testrig.ps1's own wrapper does, minus the registry read.
    param([string] $Verb, [string] $Target, [switch] $AllowUnknown, [string[]] $Known = @('hostie', 'joiner'))
    return (Resolve-RigTarget -Target $Target -Verb $Verb -KnownInstances $Known -AllowUnknown:$AllowUnknown)
}

function Assert-Refusal {
    <#
        A refusal must do all four things or it is not a refusal, it is an error
        message: fire, carry the sentinel so the launcher can print it plainly,
        explain, and name a command that works.
    #>
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [scriptblock] $Body,
        [string] $Alternative = 'testrig '
    )
    try {
        $null = & $Body 6>$null 3>$null
        Add-Fail $Name 'expected a refusal; the call returned normally'
        return
    }
    catch {
        $msg = "$($_.Exception.Message)"
        if (-not $msg.StartsWith((Get-RigRefusalSentinel))) {
            Add-Fail $Name "threw, but not as a refusal (no sentinel). Message: $msg"
            return
        }
        if ($msg -notlike "*$Alternative*") {
            Add-Fail $Name "refused, but named no working alternative containing '$Alternative'. Message: $msg"
            return
        }
        Add-Pass $Name
    }
}

# =============================================================================
# SECTIONS
# =============================================================================

function Test-Wiring {
    if (-not (Test-SectionSelected 'wiring')) { return }
    Start-Section 'wiring: the libraries point where they were told'

    Use-TestPaths
    Assert-Equal $script:TempRoot (Get-RigHomePath) 'the shared helpers point at the temp rig'
    Assert-Equal $script:RepoDir  (Get-RigRepoRoot) 'and at the temp repository'
    Assert-Match (Get-RigLockFilePath) ([regex]::Escape($script:TempRoot)) 'the lock library was re-pointed too'
    Assert-Match (Get-RigResetHomePath) ([regex]::Escape($script:TempRoot)) 'and so was the reset library'

    $p = Get-RigServerPaths
    Assert-Match $p.InstallDir  'DedicatedServer.install$'      'the server half knows its install dir'
    Assert-Match $p.PidFile     'DedicatedServer.data.server\.pid$' 'and its pid file'
    Assert-Match $p.ModConfig   'modconfig\.xml$'               'and its baked modconfig'
    Assert-Match (Get-RigClientRegistryPath) 'ClientRig.data.rig\.json$' 'the client half knows its registry'

    # One image-name literal, not three.
    Assert-Equal 'rocketstation_DedicatedServer' (Get-RigServerImageName) 'the server image name is declared once'
    Assert-Equal 'rocketstation'                 (Get-RigClientImageName) 'the client image name is declared once'
}

function Test-VerbDefaults {
    if (-not (Test-SectionSelected 'verbs')) { return }
    Start-Section 'verbs: which ones default to the whole rig'

    # THE MOTIVATING FAILURE. update-game and update-mods must default to BOTH
    # halves, because an agent asked to update the rig updated one half and had no
    # way to notice.
    foreach ($v in @('update-game', 'update-mods', 'deploy', 'status', 'list', 'logs', 'reset',
                     'lock', 'unlock', 'refresh-lock', 'capture-baseline')) {
        Assert-Equal 'all' (Get-RigVerbDefaultTarget -Verb $v) "'$v' defaults to the whole rig"
    }
    foreach ($v in @('start', 'stop', 'save', 'wait', 'call', 'send', 'create', 'remove', 'snapshot')) {
        Assert-Equal '' (Get-RigVerbDefaultTarget -Verb $v) "'$v' has no default target and must be told one"
    }

    $r = Resolve-ForTest -Verb 'update-game'
    Assert-True  $r.Server 'update-game with no -Target includes the dedicated server'
    Assert-Equal 2 $r.Names.Count 'and every provisioned instance'
    Assert-Equal 'all' $r.Kind 'and reports itself as rig-wide'

    Assert-Throws { Resolve-ForTest -Verb 'stop' } "'stop' with no target refuses rather than guessing" 'needs an explicit -Target'
    Assert-Throws { Resolve-ForTest -Verb 'start' } "'start' with no target refuses rather than guessing" 'testrig list'
}

function Test-TargetResolution {
    if (-not (Test-SectionSelected 'targets')) { return }
    Start-Section 'targets: what -Target resolves to'

    $s = Resolve-ForTest -Verb 'status' -Target 'server'
    Assert-True  $s.Server 'server names the dedicated server'
    Assert-Equal 0 $s.Names.Count 'and no instances'

    $c = Resolve-ForTest -Verb 'status' -Target 'clients'
    Assert-False $c.Server 'clients excludes the dedicated server'
    Assert-Equal 2 $c.Names.Count 'and names every instance'

    $one = Resolve-ForTest -Verb 'stop' -Target 'hostie'
    Assert-Equal 'instance' $one.Kind 'one name resolves to an instance target'
    Assert-Equal 1 $one.Names.Count 'with exactly one instance'
    # A single-element list that collapsed to a scalar would be enumerated
    # character by character by every foreach downstream.
    Assert-True  ($one.Names -is [array]) 'a single instance name stays an ARRAY, not a scalar'
    Assert-Equal 'hostie' $one.Names[0] 'and the name survives intact'

    $two = Resolve-ForTest -Verb 'stop' -Target 'hostie, joiner'
    Assert-Equal 2 $two.Names.Count 'a comma list resolves to both, whitespace and all'

    Assert-Throws { Resolve-ForTest -Verb 'stop' -Target 'nope' } `
        'an unknown instance name THROWS rather than resolving to nothing' 'not a provisioned instance'
    Assert-Throws { Resolve-ForTest -Verb 'stop' -Target 'nope' } `
        'and the refusal lists what IS provisioned' 'hostie'
    Assert-Throws { Resolve-ForTest -Verb 'stop' -Target 'hostie,nope' } `
        'one bad name in a list fails the whole command' 'nope'

    # create is the one verb that names something that does not exist yet.
    Assert-NoThrow { Resolve-ForTest -Verb 'create' -Target 'brandnew' -AllowUnknown } `
        'create accepts a name that is not provisioned yet'

    $empty = Resolve-ForTest -Verb 'status' -Target 'clients' -Known @()
    Assert-Equal 0 $empty.Names.Count 'clients on an empty rig resolves to nothing rather than throwing'
    Assert-Equal 'ALL' (Resolve-ForTest -Verb 'status' -Target 'ALL').Spec.ToUpperInvariant() 'target matching is case-insensitive'
}

function Test-RefusalMatrix {
    if (-not (Test-SectionSelected 'refusals')) { return }
    Start-Section 'refusals: every one fires, and every one names a way out'

    # The table itself is a contract: a refusal with no alternative is a bug.
    $table = @(Get-RigRefusalTable)
    Assert-True ($table.Count -ge 18) 'the refusal matrix is populated' "count was $($table.Count)"
    foreach ($r in $table) {
        $who = "$($r.Verb)/$($r.TargetKind)$(if ($r.Condition) { "/$($r.Condition)" })"
        Assert-True ([bool]$r.What)      "$who explains what the verb needs"
        Assert-True ([bool]$r.Instead)   "$who names an alternative"
        Assert-True ([bool]$r.Reference) "$who points at the durable explanation"
        Assert-True ("$($r.Instead)" -like '*testrig *') "$who names a command a caller can actually type"
    }

    # Impossibility 2 and 4: the control channel, and the player character.
    Assert-Refusal 'call on the server refuses and points at send' `
        { Assert-RigVerbApplies -Verb 'call' -Resolved (Resolve-ForTest -Verb 'call' -Target 'server') } `
        'testrig send -Target server'
    Assert-Refusal 'call on the whole rig refuses and points at the clients' `
        { Assert-RigVerbApplies -Verb 'call' -Resolved (Resolve-ForTest -Verb 'call' -Target 'all') } `
        'testrig call -Target clients'
    Assert-Refusal 'send at an instance refuses and points at call' `
        { Assert-RigVerbApplies -Verb 'send' -Resolved (Resolve-ForTest -Verb 'send' -Target 'hostie') } `
        'testrig call -Target hostie'
    Assert-Refusal 'send at the clients refuses' `
        { Assert-RigVerbApplies -Verb 'send' -Resolved (Resolve-ForTest -Verb 'send' -Target 'clients') } `
        'testrig call -Target clients'
    Assert-Refusal 'send at the whole rig refuses and points at the server' `
        { Assert-RigVerbApplies -Verb 'send' -Resolved (Resolve-ForTest -Verb 'send' -Target 'all') } `
        'testrig send -Target server'
    Assert-NoThrow { Assert-RigVerbApplies -Verb 'send' -Resolved (Resolve-ForTest -Verb 'send' -Target 'server') } `
        'send at the server is allowed'
    Assert-NoThrow { Assert-RigVerbApplies -Verb 'call' -Resolved (Resolve-ForTest -Verb 'call' -Target 'clients') } `
        'call at the clients is allowed'

    # Impossibility 6: provision is not bootstrap.
    Assert-Refusal 'create on the server refuses and points at update-game' `
        { Assert-RigVerbApplies -Verb 'create' -Resolved (Resolve-ForTest -Verb 'create' -Target 'server') } `
        'testrig update-game -Target server'
    Assert-Refusal 'create rig-wide refuses and asks for one name' `
        { Assert-RigVerbApplies -Verb 'create' -Resolved (Resolve-ForTest -Verb 'create' -Target 'all') } `
        'testrig create -Target'
    Assert-Refusal 'create at the clients refuses and asks for one name' `
        { Assert-RigVerbApplies -Verb 'create' -Resolved (Resolve-ForTest -Verb 'create' -Target 'clients') } `
        'testrig create -Target'

    # The deliberate asymmetry: there is no server remove, and remove is never wide.
    Assert-Refusal 'remove on the server refuses and says what to do instead' `
        { Assert-RigVerbApplies -Verb 'remove' -Resolved (Resolve-ForTest -Verb 'remove' -Target 'server') } `
        'testrig update-game -Target server'
    Assert-Refusal 'remove rig-wide refuses' `
        { Assert-RigVerbApplies -Verb 'remove' -Resolved (Resolve-ForTest -Verb 'remove' -Target 'all') } `
        'testrig remove -Target'
    Assert-Refusal 'remove at the clients refuses' `
        { Assert-RigVerbApplies -Verb 'remove' -Resolved (Resolve-ForTest -Verb 'remove' -Target 'clients') } `
        'testrig remove -Target'

    # No control plane on the server half.
    Assert-Refusal 'snapshot on the server refuses and points at status' `
        { Assert-RigVerbApplies -Verb 'snapshot' -Resolved (Resolve-ForTest -Verb 'snapshot' -Target 'server') } `
        'testrig status -Target server'
    Assert-Refusal 'snapshot rig-wide refuses and points at the clients' `
        { Assert-RigVerbApplies -Verb 'snapshot' -Resolved (Resolve-ForTest -Verb 'snapshot' -Target 'all') } `
        'testrig snapshot -Target clients'

    # Impossibility 1: world entry at start.
    Assert-Refusal 'start on the server with no world refuses and names -Load / -New' `
        { Assert-RigVerbApplies -Verb 'start' -Resolved (Resolve-ForTest -Verb 'start' -Target 'server') -Options @{ HasWorld = $false } } `
        'testrig start -Target server -Load'
    Assert-NoThrow { Assert-RigVerbApplies -Verb 'start' -Resolved (Resolve-ForTest -Verb 'start' -Target 'server') -Options @{ HasWorld = $true } } `
        'start on the server WITH a world is allowed'
    Assert-NoThrow { Assert-RigVerbApplies -Verb 'start' -Resolved (Resolve-ForTest -Verb 'start' -Target 'hostie') -Options @{ HasWorld = $false } } `
        'start on an instance needs no world, because it boots to the menu'

    # Readiness stages are not shared.
    foreach ($stage in @('ping', 'modsLoaded', 'menu')) {
        Assert-Refusal "wait -Stage $stage on the server refuses and names inWorld" `
            { Assert-RigVerbApplies -Verb 'wait' -Resolved (Resolve-ForTest -Verb 'wait' -Target 'server') -Options @{ Stage = $stage } } `
            'testrig wait -Target server -Stage inWorld'
    }
    Assert-NoThrow { Assert-RigVerbApplies -Verb 'wait' -Resolved (Resolve-ForTest -Verb 'wait' -Target 'server') -Options @{ Stage = 'inWorld' } } `
        'wait -Stage inWorld on the server is allowed'
    Assert-NoThrow { Assert-RigVerbApplies -Verb 'wait' -Resolved (Resolve-ForTest -Verb 'wait' -Target 'hostie') -Options @{ Stage = 'menu' } } `
        'wait -Stage menu on an instance is allowed'

    # Impossibility 3, in the shape it takes at the surface: the server's save
    # needs a name and a client's does not.
    Assert-Refusal 'save on the server with no name refuses and names -SaveName' `
        { Assert-RigVerbApplies -Verb 'save' -Resolved (Resolve-ForTest -Verb 'save' -Target 'server') -Options @{ SaveName = '' } } `
        'testrig save -Target server -SaveName'
    Assert-NoThrow { Assert-RigVerbApplies -Verb 'save' -Resolved (Resolve-ForTest -Verb 'save' -Target 'server') -Options @{ SaveName = 'Luna' } } `
        'save on the server WITH a name is allowed'
    Assert-NoThrow { Assert-RigVerbApplies -Verb 'save' -Resolved (Resolve-ForTest -Verb 'save' -Target 'hostie') -Options @{ SaveName = '' } } `
        'save on an instance needs no name'

    # Impossibility 5: N instances versus one install.
    Assert-Refusal 'instance-shape flags against the server refuse' `
        { Assert-RigVerbApplies -Verb 'status' -Resolved (Resolve-ForTest -Verb 'status' -Target 'server') -Options @{ TypedInstanceFlags = @('Role', 'GamePort') } } `
        'testrig create -Target'
    Assert-NoThrow { Assert-RigVerbApplies -Verb 'status' -Resolved (Resolve-ForTest -Verb 'status' -Target 'all') -Options @{ TypedInstanceFlags = @('Role') } } `
        'the same flags under -Target all are fine, because they describe the client half'

    # The lock covers the whole rig and cannot be taken over half of it.
    foreach ($v in @('lock', 'unlock', 'refresh-lock', 'capture-baseline', 'reset')) {
        Assert-Refusal "$v on half the rig refuses" `
            { Assert-RigVerbApplies -Verb $v -Resolved (Resolve-ForTest -Verb $v -Target 'server') } `
            'testrig '
        Assert-NoThrow { Assert-RigVerbApplies -Verb $v -Resolved (Resolve-ForTest -Verb $v) } "$v rig-wide is allowed"
    }

    # The rendering itself, since the shape is the whole point.
    $r = Get-RigRefusal -Verb 'send' -TargetKind 'instance'
    $text = Format-RigRefusal -Refusal $r -Verb 'send' -Target 'hostie'
    Assert-Match $text '^testrig send -Target hostie' 'a refusal echoes the command as typed'
    Assert-Match $text '(?m)^  x '                    'then explains, marked'
    Assert-Match $text '(?m)^    Why: '               'and points at the durable explanation'
    Assert-False ($text -match '\{target\}')          'and every placeholder was substituted'

    Assert-Throws { Deny-RigVerb -Verb 'status' -TargetKind 'server' -Condition 'no-such-condition' } `
        'asking for a refusal that is not in the matrix is reported as a bug in the matrix' 'bug in the refusal matrix'
}

function Test-ArgumentMarshalling {
    if (-not (Test-SectionSelected 'arguments')) { return }
    Start-Section 'arguments: what crosses a process boundary'

    # THE FAILURE THIS EXISTS FOR. An unquoted argument list joined with plain
    # spaces broke every lock the playtest harness took: the purpose string
    # contains spaces by nature, so '-Purpose the first-use notice cap' arrived as
    # '-Purpose the' plus positional junk that bound to an int parameter.
    Assert-Equal '"a b"'        (ConvertTo-RigProcessArgument 'a b')      'an argument with a space is quoted'
    Assert-Equal 'plain'        (ConvertTo-RigProcessArgument 'plain')    'an argument without one is left alone'
    Assert-Equal 'C:\rig\x.ps1' (ConvertTo-RigProcessArgument 'C:\rig\x.ps1') 'a path with no space keeps its backslashes'
    Assert-Equal '"say \"hi\""' (ConvertTo-RigProcessArgument 'say "hi"') 'embedded quotes are escaped, not dropped'
    Assert-Equal '""'           (ConvertTo-RigProcessArgument '')         'an empty argument survives as an empty argument'

    $line = ConvertTo-RigCommandLine -Arguments @('-NoProfile', '-File', 'C:\rig\testrig.ps1', 'host-mode', '-Load', 'My World', '-Map', 'Lunar')
    Assert-Match $line '"My World"' 'a save name with a space survives the command line'
    Assert-Equal 8 (($line -split ' (?=(?:[^"]*"[^"]*")*[^"]*$)').Count) 'and the list still has exactly its own arguments'

    # The wrapper the server start spawns re-invokes testrig.ps1, never a library:
    # a dot-sourced library has no param block, so pwsh -File against one would run
    # nothing at all and the server would never come up.
    $srv = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'lib\server.ps1')
    Assert-Match $srv '\$script:SrvLauncher, .host-mode.' 'the host wrapper is spawned as testrig.ps1 host-mode'
    Assert-Match $srv 'ConvertTo-RigCommandLine -Arguments \$wrapperArgs' 'and its argument list goes through the quoting helper'
    Assert-Match $srv 'ConvertTo-RigCommandLine -Arguments \$serverArgs'  'as does the server process argument list'
    $cli = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'lib\client.ps1')
    Assert-Match $cli 'ConvertTo-RigCommandLine -Arguments \(@\(\$p\.Exe\) \+ \$argv\)' 'and the client launch command line'
}

function Test-ConsolidatedHelpers {
    if (-not (Test-SectionSelected 'helpers')) { return }
    Start-Section 'helpers: one implementation each, and it is the right one'

    Use-TestPaths

    # ---- the install path: ONE validity test, replacing three ----
    Assert-Equal $script:SourceDir (Get-RigStationeersPath) 'the install path resolves from Directory.Build.props'
    $badRepo = Join-Path $script:RepoDir 'BadProps'
    New-Item -ItemType Directory -Force -Path $badRepo | Out-Null
    Set-Content -LiteralPath (Join-Path $badRepo 'Directory.Build.props') -Encoding utf8 -Value @"
<Project><PropertyGroup><StationeersPath>$badRepo</StationeersPath></PropertyGroup></Project>
"@
    Initialize-RigCommon -RigHome $script:TempRoot -RepoRoot $script:RepoDir -BuildProps (Join-Path $badRepo 'Directory.Build.props')
    Assert-Throws { Get-RigStationeersPath } 'an install missing both markers is refused' 'rocketstation.exe'
    Assert-Throws { Get-RigStationeersPath } 'and the refusal names the managed assembly too' 'Assembly-CSharp.dll'
    Initialize-RigCommon -RigHome $script:TempRoot -RepoRoot $script:RepoDir -BuildProps (Join-Path $script:RepoDir 'NoSuchFile.props')
    Assert-Throws { Get-RigStationeersPath } 'a missing Directory.Build.props is refused by name' 'Directory.Build.props'
    Use-TestPaths

    # ---- reading a pid file: TryParse, and no launcher copy shadowing it ----
    $pidFile = Join-Path $script:TempRoot 'garbage.pid'
    Set-Content -LiteralPath $pidFile -Value 'not-a-number' -Encoding utf8
    Assert-NoThrow { Get-RigPidFromFile $pidFile } 'a corrupt pid file does not throw'
    Assert-Equal $null (Get-RigPidFromFile $pidFile) 'it reads as nothing'
    Set-Content -LiteralPath $pidFile -Value '4321' -Encoding utf8
    Assert-Equal 4321 (Get-RigPidFromFile $pidFile) 'a good pid file reads back'
    Assert-Equal $null (Get-RigPidFromFile (Join-Path $script:TempRoot 'no-such.pid')) 'a missing pid file reads as nothing'
    # Only the library version may exist: a launcher copy that cast with [int]
    # would throw on the corrupt file above.
    Assert-Equal 1 (@(Get-Command Get-RigPidFromFile -All).Count) 'there is exactly ONE pid reader in scope'

    # ---- process liveness: the image is checked on BOTH halves ----
    Assert-False (Test-RigServerProcessAlive $script:DeadPid) 'a dead pid is not a live server'
    Assert-False (Test-RigClientProcessAlive $script:DeadPid) 'a dead pid is not a live client'
    Assert-False (Test-RigWrapperProcessAlive $script:DeadPid) 'a dead pid is not a live host wrapper'
    # THE DEFECT. This process is real and alive, and it is not the game. The
    # server half's old check was a bare Get-Process, so a recycled process id made
    # start, deploy and sync all refuse and made status report a dead server as up.
    Assert-False (Test-RigServerProcessAlive $PID) 'a LIVE process that is not the server is not the server'
    Assert-False (Test-RigClientProcessAlive $PID) 'a LIVE process that is not a game client is not a client'
    Assert-True  (Test-RigWrapperProcessAlive $PID) 'but this pwsh IS a valid host wrapper image'

    # ---- game version: version.ini, not a file that has never existed ----
    Assert-Equal '0.2.9999.12345' (Get-RigInstallVersion -InstallDir $script:SourceDir) 'the game version comes out of version.ini'
    Assert-Equal 'unknown' (Get-RigInstallVersion -InstallDir (Join-Path $script:RepoDir 'NoSuchInstall')) 'an install with no version.ini reads as unknown'
    # A version.txt is not consulted: no Stationeers install has ever had one, and
    # believing it is what made every provision stamp record the Unity version.
    Set-Content -LiteralPath (Join-Path $script:SourceDir 'version.txt') -Value 'DO-NOT-READ-ME' -Encoding utf8
    Assert-Equal '0.2.9999.12345' (Get-RigInstallVersion -InstallDir $script:SourceDir) 'a stray version.txt is ignored'
    Remove-Item -Force -LiteralPath (Join-Path $script:SourceDir 'version.txt')

    # ---- the reserved port table is DERIVED, not typed twice ----
    $reserved = Get-RigReservedGamePorts
    Assert-True $reserved.ContainsKey((Get-RigServerGamePort))   "the server's game port is reserved against instances"
    Assert-True $reserved.ContainsKey((Get-RigServerUpdatePort)) "the server's update port is reserved against instances"
    Assert-True $reserved.ContainsKey(27016) "the game client's own port is reserved too"
    Assert-Equal 28016 (Get-RigServerGamePort)  'the server game port is 28016'
    Assert-Equal 27700 (Get-RigControlPortBase) 'the control-plane band is 27700 plus the index'
    Assert-Equal 27800 (Get-RigGamePortBase)    'the game-port band is 27800 plus the index'

    # ---- readiness thresholds, declared once ----
    Assert-True  (Test-RigStageReached -Status ([pscustomobject]@{ phase = 'inWorld' }) -Stage 'inWorld') 'inWorld is a phase'
    Assert-False (Test-RigStageReached -Status ([pscustomobject]@{ phase = 'menu' }) -Stage 'inWorld')    'and menu is not it'
    Assert-True  (Test-RigStageReached -Status ([pscustomobject]@{ gameInitialized = $true; phase = 'menu' }) -Stage 'menu') 'menu needs the game initialized'
    Assert-False (Test-RigStageReached -Status ([pscustomobject]@{ gameInitialized = $false; phase = 'menu' }) -Stage 'menu') 'and refuses it otherwise'
    Assert-True  (Test-RigStageReached -Status ([pscustomobject]@{ loadedPluginCount = 22 }) -Stage 'modsLoaded') 'modsLoaded is a plugin count'
    Assert-False (Test-RigStageReached -Status ([pscustomobject]@{ loadedPluginCount = 2 })  -Stage 'modsLoaded') 'and 2 is the parked-on-an-error-screen count'
    Assert-Equal 10 (Get-RigStageMinPlugins) 'the plugin threshold is declared once'
    Assert-False (Test-RigStageReached -Status $null -Stage 'ping') 'a status that never arrived is not a ping'

    # ---- the two blocking-wait defaults are now one ----
    Assert-Equal 300 (Get-RigWaitDefaultSeconds)    'a blocking wait defaults to 300 seconds on BOTH halves'
    Assert-Equal 30  (Get-RigTimeoutDefaultSeconds) 'teardown grace stays 30, and is never a save budget'
}

function Test-ModConfig {
    if (-not (Test-SectionSelected 'modconfig')) { return }
    Start-Section 'modconfig: one reader, one writer, one format'

    Use-TestPaths
    $cfg = Join-Path $script:TempRoot 'modconfig.xml'
    Set-Content -LiteralPath $cfg -Encoding utf8 -Value @'
<?xml version="1.0" encoding="utf-8"?>
<ModConfig xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <Core Enabled="true">
    <Path />
  </Core>
  <Workshop Enabled="true">
    <Path Value="C:\ws\1234" />
    <WorkshopId Value="1234" />
  </Workshop>
  <Local Enabled="false">
    <Path Value="C:\mods\Disabled" />
  </Local>
</ModConfig>
'@
    $e = @(Get-RigModConfigEntries -Path $cfg)
    Assert-Equal 3 $e.Count 'every entry is read, Core included'
    Assert-Equal 'Workshop' $e[1].Kind 'a Workshop entry keeps its kind'
    Assert-Equal '1234'     $e[1].WorkshopId 'and its id'
    Assert-False $e[2].Enabled 'a disabled entry is read as disabled, not dropped'

    $out = Join-Path $script:TempRoot 'written.xml'
    Write-RigModConfigFile -Path $out -Entries $e
    $again = @(Get-RigModConfigEntries -Path $out)
    Assert-Equal 3 $again.Count 'a written file round-trips'
    Assert-Equal '1234' $again[1].WorkshopId 'the WorkshopId survives the round trip'
    Assert-False $again[2].Enabled 'and so does a disabled entry, which a re-bake used to drop'
    Assert-Match (Get-Content -Raw -LiteralPath $out) '<Core Enabled="true">' 'Core is always emitted'

    $added = Add-RigModConfigLocalEntry -Path $out -LocalModDir 'C:\mods\Local_ExampleMod'
    Assert-True $added 'a new Local entry is added'
    $added2 = Add-RigModConfigLocalEntry -Path $out -LocalModDir 'C:\mods\Local_ExampleMod'
    Assert-False $added2 'and adding it again is a no-op, so a deploy is idempotent'
    Assert-Equal 4 (@(Get-RigModConfigEntries -Path $out)).Count 'the file still has exactly one copy of it'

    $fresh = Join-Path $script:TempRoot 'fresh.xml'
    Assert-True (Add-RigModConfigLocalEntry -Path $fresh -LocalModDir 'C:\mods\Local_X') 'a missing file is created'
    Assert-Equal 2 (@(Get-RigModConfigEntries -Path $fresh)).Count 'with Core plus the one entry'
}

function Test-ModResolution {
    if (-not (Test-SectionSelected 'mods')) { return }
    Start-Section 'mods: finding what to deploy'

    Use-TestPaths
    $m = Get-RigModBuild -Mod 'ExampleMod'
    Assert-Equal 'mod' $m.Kind 'a released mod is found under Mods/'
    Assert-Match $m.Dll 'bin.Release.ExampleMod\.dll$' 'with its Release build path'
    Assert-Match $m.About 'ExampleMod.About$' 'and its About folder'
    Assert-Equal 'plan' (Get-RigModBuild -Mod 'PlanMod').Kind 'a work-in-progress mod is found under Plans/'
    Assert-Equal $null (Get-RigModBuild -Mod 'NoSuchMod') 'a name that matches nothing resolves to nothing'
    Assert-Match (Get-RigModBuild -Mod 'ExampleMod' -Configuration 'Debug').Dll 'bin.Debug' '-Configuration picks the build'

    $all = @(Get-RigDeployableMods)
    Assert-True ($all -contains 'ExampleMod') 'a deploy with no name covers every released mod'
    Assert-False ($all -contains 'Template')  'and never the Template scaffold'
    Assert-False ($all -contains 'PlanMod')   'and never a Plans/ mod, which is deployed by name or not at all'
}

function Test-InstanceRootResolution {
    if (-not (Test-SectionSelected 'instances')) { return }
    Start-Section 'instances: one root resolution chain'

    $saved = $env:STATIONEERS_CLIENTRIG_ROOT
    try {
        $env:STATIONEERS_CLIENTRIG_ROOT = $null
        Use-TestPaths
        $d = Get-RigDefaultInstancesRoot
        Assert-Match $d.Root 'ClientRig.instances$' 'with nothing set, a new instance goes beside the rig'
        Assert-Match $d.Source 'default' 'and says so'

        $env:STATIONEERS_CLIENTRIG_ROOT = 'D:\SomewhereElse'
        $d2 = Get-RigDefaultInstancesRoot
        Assert-Equal 'D:\SomewhereElse' $d2.Root 'the environment variable wins over the default'
        Assert-Match $d2.Source 'STATIONEERS_CLIENTRIG_ROOT' 'and is named as the source'

        $d3 = Get-RigDefaultInstancesRoot -Override 'E:\Typed'
        Assert-Equal 'E:\Typed' $d3.Root 'a typed root wins over both'
        Assert-Match $d3.Source 'typed on this command' 'and is named as the source'
    }
    finally {
        $env:STATIONEERS_CLIENTRIG_ROOT = $saved
    }

    Use-TestPaths
    Set-FakeRegistry
    $entries = @(Get-RigClientEntries -Names @('hostie'))
    Assert-Equal 1 $entries.Count 'a known instance resolves to its registry entry'
    Assert-Equal 'host' $entries[0].role 'with its recorded role'
    Assert-Throws { Get-RigClientEntries -Names @('ghost') } 'an unknown instance is refused by name' "Instance 'ghost' is not provisioned"
    Assert-Throws { Get-RigClientEntries -Names @('ghost') } 'and the refusal lists what exists' 'hostie'
    Assert-Equal 2 (@(Get-RigClientEntries -All)).Count 'the whole rig resolves to every entry'

    $p = Get-InstancePaths -Name 'hostie'
    Assert-Match $p.Tree     'instances.hostie$'        'an instance tree hangs off the recorded root'
    Assert-Match $p.UserData 'data.hostie.userdata$'    'its save root is per-instance state, not part of the tree'
    Assert-Match $p.PidFile  'data.hostie.game\.pid$'   'and so is its pid file'
}

function Test-ServerHalf {
    if (-not (Test-SectionSelected 'server')) { return }
    Start-Section 'server half: gates that do not need a game'

    Use-TestPaths
    # Every mutating verb is gated on the lock, and there is no lock here.
    Assert-Throws { Invoke-Quiet { Invoke-RigServerUpdateGame -As 'nobody' } } 'update-game is gated on the lock' 'lock'
    Assert-Throws { Invoke-Quiet { Invoke-RigServerUpdateMods -As 'nobody' } } 'update-mods is gated on the lock' 'lock'
    Assert-Throws { Invoke-Quiet { Invoke-RigServerDeploy -As 'nobody' } }     'deploy is gated on the lock' 'lock'
    Assert-Throws { Invoke-Quiet { Invoke-RigServerStart -As 'nobody' -New 'Lunar' } } 'start is gated on the lock' 'lock'
    Assert-Throws { Invoke-Quiet { Invoke-RigServerSend -As 'nobody' -Command 'status' } } 'send is gated on the lock' 'lock'

    # Stopping a server that is not running is a clean no-op, because cleaning up
    # after a crash must never need ceremony.
    Assert-NoThrow { Invoke-Quiet { Invoke-RigServerStop -As 'nobody' } } 'stopping nothing is not an error'

    # A version report with no install still answers, and says it is not installed.
    $v = Get-RigServerVersionReport
    Assert-False $v.Present 'the server reports itself as not installed when it is not'
    Assert-Equal 'server' $v.Half 'and labels its half'
    Assert-Match $v.Remedy 'testrig update-game -Target server' 'and names the fix'

    Assert-NoThrow { Invoke-Quiet { Write-RigServerStatus } } 'the server status block runs against an empty tree'
    Assert-NoThrow { Invoke-Quiet { Invoke-RigServerLogs -Tail 5 } } 'so does the log reader with no log'
    Assert-Equal 0 (@(Get-RigServerModStaleness)).Count 'and an empty server half reports no stale payloads'
}

function Test-ClientHalf {
    if (-not (Test-SectionSelected 'client')) { return }
    Start-Section 'client half: gates that do not need a game'

    Use-TestPaths
    Set-FakeRegistry
    $entries = @(Get-RigClientEntries -All)

    Assert-Throws { Invoke-Quiet { Invoke-RigClientCreate -As 'nobody' -Instance 'newone' } } 'create is gated on the lock' 'lock'
    Assert-Throws { Invoke-Quiet { Invoke-RigClientDeploy -As 'nobody' -Entries $entries } } 'deploy is gated on the lock' 'lock'
    Assert-Throws { Invoke-Quiet { Invoke-RigClientUpdateMods -As 'nobody' -Entries $entries } } 'update-mods is gated on the lock' 'lock'
    Assert-Throws { Invoke-Quiet { Invoke-RigClientSave -As 'nobody' -Entries $entries } } 'save is gated on the lock' 'lock'
    Assert-Throws { Invoke-Quiet { Invoke-RigClientRemove -As 'nobody' -Instance 'hostie' } } 'remove is gated on the lock' 'lock'
    Assert-Throws { Invoke-Quiet { Invoke-RigClientCall -As 'nobody' -Entries $entries -Path '/status' } } 'call is gated on the lock' 'lock'
    Assert-Throws { Invoke-Quiet { Invoke-RigClientStart -As 'nobody' -Entries $entries } } 'start is gated on the lock' 'lock'

    Assert-Throws { Invoke-Quiet { Invoke-RigClientCreate -As 'nobody' -Instance 'a,b' } } 'create refuses a comma list' 'one instance at a time'
    Assert-Throws { Invoke-Quiet { Invoke-RigClientRemove -As 'nobody' -Instance 'a,b' } } 'remove refuses a comma list' 'one instance at a time'

    # An empty selection never becomes a rig-wide action by accident.
    Assert-NoThrow { Invoke-Quiet { Invoke-RigClientStop -As 'nobody' -Entries @() } } 'stopping nothing is not an error'
    Assert-NoThrow { Invoke-Quiet { Invoke-RigClientWait -Entries @() -Stage 'menu' } } 'waiting for nothing is not an error'

    # The derived control-plane timeout, which a fixed constant used to win over.
    Assert-Equal 120 (Get-ControlTimeoutSeconds -Path '/status' -BodyJson '')            'a short endpoint gets the floor'
    Assert-Equal 300 (Get-ControlTimeoutSeconds -Path '/connect' -BodyJson '')           'a long endpoint gets the long floor'
    Assert-Equal 330 (Get-ControlTimeoutSeconds -Path '/connect' -BodyJson '{"timeoutMs":300000}') 'a request asking for five minutes gets five minutes plus the margin'
    Assert-Equal 999 (Get-ControlTimeoutSeconds -Path '/connect' -BodyJson '{"timeoutMs":300000}' -Override 999) 'and an explicit override wins over all of it'
    Assert-Equal 300000 (Get-RequestedTimeoutMs -Path '/x?timeoutMs=300000' -BodyJson '') 'a timeout in the query string is read'
    Assert-Equal 0 (Get-RequestedTimeoutMs -Path '/x' -BodyJson 'not json at all')        'and a body that is not JSON never throws'

    # Snapshot output paths: the rig folder is the floor for anything relative.
    Assert-Throws { Resolve-RigOutFile -Value '..\..\escape.json' } 'a snapshot path that climbs out of the rig is refused' 'climbs out'
    Assert-Throws { Resolve-RigOutFile -Value 'C:relative.json' }   'a drive-relative snapshot path is refused' 'drive-relative'
    Assert-Match  (Resolve-RigOutFile -Value 'before.json') ([regex]::Escape($script:TempRoot)) 'and a plain name lands inside the rig folder'

    $rows = @(Get-RigClientListRows -Entries $entries)
    Assert-Equal 2 $rows.Count 'the instance table has a row per instance'
    Assert-Equal 'hostie' $rows[0].instanceName 'sorted by index'
    Assert-Equal 'host' $rows[0].role 'and carries the provisioned role'

    $vr = @(Get-RigClientVersionReport -Entries $entries)
    Assert-Equal 2 $vr.Count 'the version report covers every instance'
    Assert-Equal 'unknown' $vr[0].Version 'an instance with no provision stamp reports unknown'
    Assert-Match $vr[0].Remedy 'testrig update-game -Target hostie' 'and names the fix for that instance'
}

function Test-DispatchSurface {
    if (-not (Test-SectionSelected 'dispatch')) { return }
    Start-Section 'dispatch: the launcher itself, parsed not run'

    # The dispatcher is the one piece that cannot be exercised without a rig, so
    # what is checked here is its SHAPE: every verb it advertises is a verb it
    # dispatches, and every verb it dispatches is one it advertises. A verb that
    # exists in one list and not the other is a command that either cannot be run
    # or cannot be found.
    $launcher = Join-Path $PSScriptRoot 'testrig.ps1'
    Assert-True (Test-Path -LiteralPath $launcher) 'the launcher is where every doc says it is'

    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($launcher, [ref]$null, [ref]$errors)
    Assert-Equal 0 (@($errors).Count) 'the launcher parses clean'

    $text = Get-Content -Raw -LiteralPath $launcher
    $declared = @([regex]::Matches($text, "(?s)\`$KnownVerbs = @\((.*?)\)")[0].Groups[1].Value |
                  ForEach-Object { [regex]::Matches($_, "'([a-z-]+)'") } |
                  ForEach-Object { $_.Value.Trim("'") })
    Assert-True ($declared.Count -ge 20) 'the launcher declares its verb list' "found $($declared.Count)"

    $dispatched = @([regex]::Matches($text, "(?m)^\s{8}'([a-z-]+)'\s+\{") | ForEach-Object { $_.Groups[1].Value })
    foreach ($v in $dispatched) {
        Assert-True ($declared -contains $v) "the dispatched verb '$v' is one the launcher advertises"
    }
    foreach ($v in @($declared | Where-Object { $_ -notin @('help', 'host-mode') })) {
        Assert-True ($dispatched -contains $v) "the advertised verb '$v' is actually dispatched"
    }

    # The old entry points are gone, not shimmed. A shim is what turns one system
    # back into two.
    Assert-False (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'DedicatedServer\dedicated-server.ps1')) 'the old server launcher is deleted'
    Assert-False (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'ClientRig\client-rig.ps1'))             'the old client launcher is deleted'

    # The machine-readable owner line is a contract with the playtest harness,
    # which used to scrape the id out of prose with two regexes.
    Assert-Match $text 'TESTRIG-OWNER \$\(\$outcome\.Owner\)' 'a successful lock prints the owner id as a machine-readable line'
    $harness = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'playtest\playtest-lib.ps1')
    Assert-Match $harness 'TESTRIG-OWNER' 'and the playtest harness reads that line'
    Assert-False ($harness -match '\[Lock\]\\s\+owner') 'and no longer scrapes the human-readable block'

    # The stop ordering dependency, which is the one thing in this launcher that
    # cannot be seen by reading either half alone.
    $stopIdx    = $text.IndexOf('$st = Get-RigLockState -CallerId $As')
    $releaseIdx = $text.IndexOf('if ($Release) { Invoke-RigReleaseAfterStop }')
    Assert-True ($stopIdx -gt 0 -and $releaseIdx -gt $stopIdx) 'stop asks for the lock STATE before it releases the lock'
    Assert-Match $text 'Test-RigLockReleasableOnStop' 'and the release uses the tested shared predicate, not an inline copy'
    $cli = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'lib\client.ps1')
    Assert-False ($cli -match 'Test-RigLockTimerExpired \$lock') 'the client half no longer carries its own untested release predicate'
}

# =============================================================================
# RUN
# =============================================================================

# A fingerprint of the REAL rig's state files, so a bug in this suite that
# escaped the temp tree is caught rather than shrugged at.
$script:RealWatch = @{}
foreach ($rel in @('session.lock', 'session.dirty', 'session.state.json', 'ClientRig\data\rig.json')) {
    $p = Join-Path $script:RealHome $rel
    $script:RealWatch[$rel] = if (Test-Path -LiteralPath $p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash } else { '<absent>' }
}

try {
    New-TempTree
    Use-TestPaths
    if ((Get-RigLockFilePath) -notlike "$($script:TempRoot)*") {
        throw "The redirection did not take: the lock library still points at $(Get-RigLockFilePath). Refusing to run, because every later test would be operating on the REAL rig."
    }

    Test-Wiring
    Test-VerbDefaults
    Test-TargetResolution
    Test-RefusalMatrix
    Test-ArgumentMarshalling
    Test-ConsolidatedHelpers
    Test-ModConfig
    Test-ModResolution
    Test-InstanceRootResolution
    Test-ServerHalf
    Test-ClientHalf
    Test-DispatchSurface
}
finally {
    Start-Section 'safety'
    foreach ($rel in @($script:RealWatch.Keys | Sort-Object)) {
        $p   = Join-Path $script:RealHome $rel
        $now = if (Test-Path -LiteralPath $p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash } else { '<absent>' }
        Assert-Equal $script:RealWatch[$rel] $now "the REAL $rel was not touched by this run"
    }
    if ($script:RepoDir -and (Test-Path -LiteralPath $script:RepoDir)) {
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $script:RepoDir
    }
    Assert-False (Test-Path -LiteralPath $script:RepoDir) 'the temp tree was cleaned up'
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
