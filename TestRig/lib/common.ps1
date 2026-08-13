<#
    Shared launcher helpers for TestRig/testrig.ps1.

    THIS FILE EXISTS BECAUSE THE TWO HALVES USED TO ANSWER THE SAME QUESTION
    DIFFERENTLY. Every function here had two or three implementations across
    dedicated-server.ps1 and client-rig.ps1, and each set had drifted:

      - "is that pid alive" was a bare Get-Process on the server half, so a
        recycled process id made -Start, -DeployMods and -SyncMods refuse and made
        -Status report a dead server as up. The client half checked the process
        image and said in a comment why. Now there is one answer and it checks the
        image.
      - "where is the game install" had three validity tests (a managed assembly,
        rocketstation.exe, BepInEx/config) and therefore three ways to be told the
        path was wrong. Now there is one test that names exactly what is missing.
      - "read a pid file" cast with [int] in both launchers, which THROWS on a
        corrupt file, next to a library version using TryParse. The library version
        won; both copies are gone.
      - "what game version is this" read a version.txt that has never existed in
        any Stationeers install, so every provision stamp recorded the Unity engine
        version instead and no game update could ever be detected. The version.ini
        reader in rig-reset.ps1 won.
      - modconfig.xml had three writers and three formats, all of which the
        baseline stores byte for byte. One writer now.

    Nothing in here knows about verbs, targets or the lock. It is dot-sourced by
    testrig.ps1 before lib/server.ps1 and lib/client.ps1, and by
    testrig.tests.ps1, which is what makes these testable offline.
#>

# ---- wiring ---------------------------------------------------------------

function Initialize-RigCommon {
    <#
        Point the shared helpers at a TestRig-shaped root. Called by testrig.ps1
        with its own folder, and by the test suite with a temp tree. The parameter
        is -RigHome and not -Home because $HOME is a read-only automatic variable
        and a parameter of that name cannot be bound at all.
    #>
    param(
        [Parameter(Mandatory)] [string] $RigHome,
        [string] $RepoRoot,
        [string] $BuildProps,
        [string] $SteamcmdPath,
        [string] $UserDataDir
    )
    $script:RigHome     = $RigHome
    $script:RigRepoRoot = if ($RepoRoot) { $RepoRoot } else { Split-Path -Parent $RigHome }
    $script:RigBuildProps = if ($BuildProps) { $BuildProps } else { Join-Path $script:RigRepoRoot 'Directory.Build.props' }

    # Injectable so the suite never reads the developer's real environment.
    $script:RigSteamcmdOverride = $SteamcmdPath
    $script:RigUserDataOverride = $UserDataDir

    # Cleared so a re-point cannot serve a path resolved against the previous one.
    $script:RigStationeersPathCache = $null
}

function Get-RigHomePath { return $script:RigHome }
function Get-RigRepoRoot { return $script:RigRepoRoot }

# ---- constants, declared once ---------------------------------------------
#
# Each of these used to be declared in two or three places. The game ports are the
# sharpest case: client-rig.ps1 hardcoded the dedicated server's 28015/28016 in its
# collision table while dedicated-server.ps1 declared them independently as
# parameter defaults, so changing one did not change the other and the collision
# check would have gone quietly wrong.

# What Get-Process reports for each half, minus the extension. The lock library and
# the state reset take these as parameters; testrig.ps1 passes these values in, so
# all four consumers agree by construction.
$script:RigServerImageName = 'rocketstation_DedicatedServer'
$script:RigClientImageName = 'rocketstation'

# The dedicated server's UDP ports. Offset +1000 from the Stationeers client
# defaults so a server and a client coexist on one machine.
$script:RigServerGamePort   = 28016
$script:RigServerUpdatePort = 28015

# Client instance port bands, both derived from the instance index so a rig
# provisioned with no flags never collides with itself.
$script:RigControlPortBase = 27700
$script:RigGamePortBase    = 27800

# Readiness: how many loaded plugins mean "the mod set is up". Below this the
# splash screen is still drawing.
$script:RigStageMinPlugins = 10

# Control-plane HTTP timeout bounds for the client half.
$script:RigControlTimeoutFloorSeconds   = 120
$script:RigControlTimeoutMarginSeconds  = 30
$script:RigControlTimeoutCeilingSeconds = 3600
$script:RigControlLongPathSeconds       = 300
$script:RigControlLongPaths = @('/host', '/connect', '/save', '/load', '/newworld', '/waitfor')

# How long a blocking wait waits, by default, ON BOTH HALVES.
#
# It was 30 on the server and 300 on the client for the same flag and the same
# meaning, so a 60 second save confirmed on one half and warned "may have
# completed silently or failed" on the other. 300 wins because of which way the
# two errors point: a save that is genuinely slow (a populated world is hundreds
# of megabytes and the archive is written and zipped before the log line lands)
# produces a FALSE WARNING under a 30 second budget, and the whole contract of
# this action is that it warns rather than claiming success. A false warning is
# indistinguishable from a real one, so the short default was spending the
# contract's credibility. Too long a default costs only latency, and only on a
# save that failed.
$script:RigWaitDefaultSeconds = 300

# Process-teardown grace, both halves. This is the ONLY thing -TimeoutSeconds
# means anywhere on this launcher.
$script:RigTimeoutDefaultSeconds = 30

function Get-RigServerImageName   { return $script:RigServerImageName }
function Get-RigClientImageName   { return $script:RigClientImageName }
function Get-RigServerGamePort    { return $script:RigServerGamePort }
function Get-RigServerUpdatePort  { return $script:RigServerUpdatePort }
function Get-RigControlPortBase   { return $script:RigControlPortBase }
function Get-RigGamePortBase      { return $script:RigGamePortBase }
function Get-RigStageMinPlugins   { return $script:RigStageMinPlugins }
function Get-RigWaitDefaultSeconds    { return $script:RigWaitDefaultSeconds }
function Get-RigTimeoutDefaultSeconds { return $script:RigTimeoutDefaultSeconds }

function Get-RigReservedGamePorts {
    # Ports an instance's game port must never take. A second RakNet socket on an
    # already-bound port does not fail: both bindings coexist and traffic routes by
    # destination address, so the joiner reaches SOMETHING and the test is
    # confidently wrong. This refusal is the only warning there will ever be.
    #
    # The dedicated server's two entries are computed from the constants above
    # rather than typed again, which is the whole point of this file.
    return @{
        27015 = "the Stationeers client's own default UpdatePort"
        27016 = "the Stationeers client's own default GamePort"
        $script:RigServerUpdatePort = "this rig's dedicated server UpdatePort"
        $script:RigServerGamePort   = "this rig's dedicated server GamePort"
    }
}

# ---- environment ----------------------------------------------------------

function Get-RigStationeersPath {
    <#
        The developer's Stationeers CLIENT install, from Directory.Build.props at
        the repo root.

        ONE validity test, replacing three. dedicated-server.ps1 checked for
        rocketstation_Data\Managed\Assembly-CSharp.dll, client-rig.ps1 checked for
        rocketstation.exe, and rig-reset.ps1 checked for BepInEx\config and returned
        $null instead of throwing. A path pointing at a dedicated-server install
        passed the first and failed the second, with two different messages, and the
        third silently degraded. Both markers are checked here because both halves
        genuinely need both: the client rig hard-links rocketstation_Data and runs
        rocketstation.exe, and the server half mirrors the managed assemblies'
        sibling BepInEx tree out of the same install.
    #>
    if ($script:RigStationeersPathCache) { return $script:RigStationeersPathCache }
    if (-not (Test-Path -LiteralPath $script:RigBuildProps)) {
        throw "Directory.Build.props not found at $($script:RigBuildProps). Copy Directory.Build.props.template to Directory.Build.props and set <StationeersPath>. See DEV.md."
    }
    $xml  = [xml](Get-Content -Raw -LiteralPath $script:RigBuildProps)
    $path = $xml.Project.PropertyGroup.StationeersPath
    if ([string]::IsNullOrWhiteSpace($path)) {
        throw "<StationeersPath> in Directory.Build.props is empty. Set it to your Stationeers client install. See DEV.md."
    }
    $path = ([string]$path).Trim()
    $missing = @()
    if (-not (Test-Path -LiteralPath (Join-Path $path 'rocketstation.exe')))                         { $missing += 'rocketstation.exe' }
    if (-not (Test-Path -LiteralPath (Join-Path $path 'rocketstation_Data\Managed\Assembly-CSharp.dll'))) { $missing += 'rocketstation_Data\Managed\Assembly-CSharp.dll' }
    if ($missing.Count -gt 0) {
        throw "<StationeersPath>=$path is missing $($missing -join ' and '). This rig needs the Stationeers CLIENT install: the client half hard-links its tree, and the server half mirrors its BepInEx loader. A dedicated-server install has neither. See DEV.md."
    }
    $script:RigStationeersPathCache = $path
    return $path
}

function Get-RigSteamcmdPath {
    $p = if ($script:RigSteamcmdOverride) { $script:RigSteamcmdOverride } else { $env:STEAMCMD_PATH }
    if ([string]::IsNullOrWhiteSpace($p)) {
        throw "STEAMCMD_PATH environment variable is not set. Set it to the absolute path of steamcmd.exe. See DEV.md."
    }
    if (-not (Test-Path -LiteralPath $p)) {
        throw "STEAMCMD_PATH=$p does not exist. See DEV.md."
    }
    return $p
}

function Get-RigUserDataPath {
    # The game's own user-data root: Documents\My Games\Stationeers. Resolved from
    # the Windows shell folder rather than hardcoded, so nothing here is tied to
    # one developer's layout. TIER 1: read-only from the rig, always.
    if ($script:RigUserDataOverride) { return $script:RigUserDataOverride }
    return (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'My Games\Stationeers')
}

function Get-RigDefaultInstancesRoot {
    <#
        Where a NEW client instance tree is built, and where that answer came from.

        ONE resolution order, replacing three (the launcher's, the state reset's and
        the playtest harness's, the last of which omitted the environment-variable
        step). An instance that already EXISTS uses the root recorded in its
        registry entry instead; that lookup lives in lib/client.ps1 because it needs
        the registry.
    #>
    param([string] $Override)
    if ($Override) {
        return [pscustomobject]@{ Root = $Override; Source = '-InstancesRoot (typed on this command)' }
    }
    if ($env:STATIONEERS_CLIENTRIG_ROOT) {
        return [pscustomobject]@{ Root = $env:STATIONEERS_CLIENTRIG_ROOT; Source = '$env:STATIONEERS_CLIENTRIG_ROOT' }
    }
    return [pscustomobject]@{
        Root   = (Join-Path (Join-Path $script:RigHome 'ClientRig') 'instances')
        Source = 'the default ClientRig/instances folder'
    }
}

# ---- process identity -----------------------------------------------------
#
# Both wrappers go through the lock library's Get-RigLiveProcess, which checks the
# process IMAGE and not just the number. That check is load bearing: Windows
# recycles process ids and these pid files outlive their processes on a force-kill,
# a crash or a reboot.

function Test-RigServerProcessAlive {
    param([Nullable[int]] $TargetPid)
    return ($null -ne (Get-RigLiveProcess -TargetPid $TargetPid -ImageName $script:RigServerImageName))
}

function Test-RigClientProcessAlive {
    param([Nullable[int]] $TargetPid)
    return ($null -ne (Get-RigLiveProcess -TargetPid $TargetPid -ImageName $script:RigClientImageName))
}

function Test-RigWrapperProcessAlive {
    # The dedicated server's host wrapper is a pwsh process, so the image check
    # names the shell rather than the game. Without a name at all a recycled id
    # would report the wrapper alive and -Stop would refuse to clean up.
    param([Nullable[int]] $TargetPid)
    if (-not $TargetPid) { return $false }
    foreach ($img in @('pwsh', 'powershell')) {
        if ($null -ne (Get-RigLiveProcess -TargetPid $TargetPid -ImageName $img)) { return $true }
    }
    return $false
}

# ---- child process arguments ----------------------------------------------

function ConvertTo-RigProcessArgument {
    <#
        One argument, quoted for a Windows command line.

        This is not decoration. An unquoted argument list joined with plain spaces
        is what broke every lock acquisition the playtest harness attempted: the
        purpose string contains spaces by nature, so `-Purpose the first-use notice`
        arrived at the launcher as `-Purpose the` followed by positional junk that
        bound to an int parameter, and every check in every suite died before it
        started.
    #>
    param([string] $Value)
    if ($null -eq $Value) { return '""' }
    if ($Value -eq '')    { return '""' }
    if ($Value -match '[\s"]') { return '"' + ($Value -replace '"', '\"') + '"' }
    return $Value
}

function ConvertTo-RigCommandLine {
    param([Parameter(Mandatory)] [object[]] $Arguments)
    return (($Arguments | ForEach-Object { ConvertTo-RigProcessArgument ([string]$_) }) -join ' ')
}

# ---- game version ---------------------------------------------------------

function Get-RigInstallVersion {
    <#
        The game version an install carries, from
        <data folder>\StreamingAssets\version.ini, whose first line reads
        "UPDATEVERSION=Update 0.2.6428.27798".

        Delegates to Get-RigGameVersion in rig-reset.ps1 rather than repeating the
        read. The launcher's own copy read a version.txt at the install root; no
        such file has ever existed in any Stationeers install, so every
        provision.stamp recorded the executable's Unity FileVersion instead, which
        is a different string from the one the baseline records. Nothing could
        compare a stamp against a baseline, and a game update could never mark
        anything stale. That is exactly what happened on 2026-08-12.
    #>
    param([string] $InstallDir)
    if (-not (Get-Command Get-RigGameVersion -ErrorAction SilentlyContinue)) { return 'unknown' }
    return (Get-RigGameVersion -SourceInstall $InstallDir)
}

# ---- modconfig.xml --------------------------------------------------------
#
# ONE reader and ONE writer, replacing three writers with three formats. The
# baseline stores every modconfig.xml by content and restores it byte for byte, so
# a format difference between writers silently invalidated stored baseline content
# depending on which action last touched the file.

function Get-RigModConfigEntries {
    <#
        Parse a modconfig.xml into ordered entries. Document order is preserved
        because it carries load-order intent.

        Every entry keeps its Enabled value rather than being filtered here. A
        caller that wants only the enabled ones filters; a caller rewriting a
        developer's file in place must NOT drop the disabled ones, because
        re-enabling one afterwards is a normal thing to do.
    #>
    param([Parameter(Mandatory)] [string] $Path)
    $out = New-Object System.Collections.Generic.List[object]
    if (-not (Test-Path -LiteralPath $Path)) { return $out.ToArray() }
    $xml = [xml](Get-Content -Raw -LiteralPath $Path)
    if (-not $xml.ModConfig) { return $out.ToArray() }
    foreach ($node in $xml.ModConfig.ChildNodes) {
        if ($node.NodeType -ne [System.Xml.XmlNodeType]::Element) { continue }
        $entry = [pscustomobject]@{
            Kind       = [string]$node.LocalName
            Enabled    = ([string]$node.Enabled -eq 'true')
            Path       = if ($node.Path) { [string]$node.Path.Value } else { '' }
            WorkshopId = if ($node.WorkshopId) { [string]$node.WorkshopId.Value } else { '' }
        }
        $out.Add($entry)
    }
    return $out.ToArray()
}

function Write-RigModConfigFile {
    <#
        Write the canonical modconfig.xml. A <Core> entry is always emitted first
        whether or not the caller passed one, because every consumer of this file
        expects it and no caller has ever wanted it absent.
    #>
    param(
        [Parameter(Mandatory)] [string] $Path,
        [AllowNull()] [AllowEmptyCollection()] $Entries = @()
    )
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
    [void]$sb.AppendLine('<ModConfig xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">')
    [void]$sb.AppendLine('  <Core Enabled="true">')
    [void]$sb.AppendLine('    <Path />')
    [void]$sb.AppendLine('  </Core>')
    foreach ($e in @($Entries)) {
        if ($null -eq $e) { continue }
        if ([string]$e.Kind -eq 'Core') { continue }
        $enabled = if ($e.Enabled) { 'true' } else { 'false' }
        $kind    = if ([string]$e.Kind) { [string]$e.Kind } else { 'Local' }
        [void]$sb.AppendLine("  <$kind Enabled=`"$enabled`">")
        [void]$sb.AppendLine("    <Path Value=`"$([System.Security.SecurityElement]::Escape([string]$e.Path))`" />")
        if ([string]$e.WorkshopId) {
            [void]$sb.AppendLine("    <WorkshopId Value=`"$([System.Security.SecurityElement]::Escape([string]$e.WorkshopId))`" />")
        }
        [void]$sb.AppendLine("  </$kind>")
    }
    [void]$sb.AppendLine('</ModConfig>')
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    Set-Content -LiteralPath $Path -Value $sb.ToString() -Encoding utf8 -NoNewline
}

function Add-RigModConfigLocalEntry {
    <#
        Ensure the file has an enabled <Local> entry pointing at $LocalModDir.
        Idempotent: returns $true when an entry was added, $false when it was
        already there. A missing file is created with Core plus this one entry.
    #>
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $LocalModDir
    )
    $entries = @(Get-RigModConfigEntries -Path $Path)
    foreach ($e in $entries) {
        if ([string]$e.Kind -eq 'Local' -and
            [string]$e.Path -and
            ([string]$e.Path).TrimEnd('\', '/').Equals(($LocalModDir.TrimEnd('\', '/')), [StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }
    $entries += [pscustomobject]@{ Kind = 'Local'; Enabled = $true; Path = $LocalModDir; WorkshopId = '' }
    Write-RigModConfigFile -Path $Path -Entries $entries
    return $true
}

# ---- repository mods ------------------------------------------------------

function Get-RigModBuild {
    <#
        Where a mod's built DLL is, and what kind of thing it is.

        Search order, unchanged from the dedicated server's: Mods/<X>, then
        Plans/<X>, then either half's dev-plugins/<X>. Mods/ wins on a tie.
        Returns $null when the name matches nothing.
    #>
    param(
        [Parameter(Mandatory)] [string] $Mod,
        [string] $Configuration = 'Release'
    )
    $candidates = @(
        @{ Dir = (Join-Path (Join-Path $script:RigRepoRoot 'Mods')  $Mod); Kind = 'mod' }
        @{ Dir = (Join-Path (Join-Path $script:RigRepoRoot 'Plans') $Mod); Kind = 'plan' }
        @{ Dir = (Join-Path (Join-Path $script:RigHome 'DedicatedServer\dev-plugins') $Mod); Kind = 'devplugin-server' }
        @{ Dir = (Join-Path (Join-Path $script:RigHome 'ClientRig\dev-plugins') $Mod);       Kind = 'devplugin-client' }
    )
    foreach ($c in $candidates) {
        if (-not (Test-Path -LiteralPath $c.Dir)) { continue }
        return [pscustomobject]@{
            Name          = $Mod
            Dir           = $c.Dir
            Kind          = $c.Kind
            Configuration = $Configuration
            Dll           = (Join-Path $c.Dir "$Mod\bin\$Configuration\$Mod.dll")
            About         = (Join-Path $c.Dir "$Mod\About")
        }
    }
    return $null
}

function Get-RigDeployableMods {
    # Every released mod, which is what a deploy with no -Mod means. Plans/ and
    # dev-plugins/ are deliberately excluded: work in progress and rig tooling are
    # deployed by name or not at all.
    $modsRoot = Join-Path $script:RigRepoRoot 'Mods'
    if (-not (Test-Path -LiteralPath $modsRoot)) { return @() }
    return @(Get-ChildItem -LiteralPath $modsRoot -Directory -ErrorAction SilentlyContinue |
             Where-Object { $_.Name -ne 'Template' } |
             Sort-Object Name |
             ForEach-Object { $_.Name })
}

# ---- readiness ------------------------------------------------------------

function Test-RigStageReached {
    <#
        Is this /status payload at or past the named stage. Pure, so the thresholds
        can be pinned by a test with no game running.

        The three stages are genuinely different and conflating them is a real trap:
        a loaded plugin count alone is not "ready", because the splash screen is
        still drawing and it suppresses the in-game windows.
    #>
    param(
        $Status,
        [Parameter(Mandatory)] [ValidateSet('ping', 'modsLoaded', 'menu', 'inWorld')] [string] $Stage
    )
    if ($Stage -eq 'ping') { return ($null -ne $Status) }
    if ($null -eq $Status) { return $false }
    switch ($Stage) {
        'modsLoaded' { return ([int]$Status.loadedPluginCount -gt $script:RigStageMinPlugins) }
        'menu'       { return ($Status.gameInitialized -eq $true -and [string]$Status.phase -eq 'menu') }
        'inWorld'    { return ([string]$Status.phase -eq 'inWorld') }
    }
    return $false
}

# ---- refusals -------------------------------------------------------------
#
# A REFUSAL IS A FEATURE OF THIS LAUNCHER, NOT AN ERROR PATH. Seven things
# genuinely cannot mean the same thing on both halves, and each one is a place
# where an agent's model of the rig is about to be wrong. A refusal that only says
# no leaves that model wrong; one that says what the verb needs, why this target
# cannot provide it, and the exact command that would work, corrects it. That is
# cheaper than any document, because it arrives at the moment of the mistake.
#
# Every entry names an alternative. A refusal with no Instead is a bug, and
# testrig.tests.ps1 fails the suite if one appears.

$script:RigRefusalSentinel = "[testrig refusal]`n"

function Get-RigRefusalSentinel { return $script:RigRefusalSentinel }

function Get-RigRefusalTable {
    <#
        The complete refusal matrix, as data.

        TargetKind is 'server', 'instance', 'clients', 'all' or 'any'. A refusal
        fires when the verb matches and the target kind matches. '{target}' in
        Instead is replaced with the target that was actually named.
    #>
    return @(
        [pscustomobject]@{
            Verb = 'start'; TargetKind = 'server'; Condition = 'no-world'
            What = "'start' on the dedicated server has to enter a world in the same call. The server takes -load <save> <map> or -new <map> on its own command line and there is no way to bring it up to a menu and decide later; a client instance is the opposite, and boots to the menu with no world at all."
            Instead = 'testrig start -Target server -Load <SaveName> -Map <Map>   (or -New <Map>)'
            InsteadLabel = 'Name the world:'
            Reference = 'TestRig/MANUAL.md, "Verbs"'
        }
        [pscustomobject]@{
            Verb = 'call'; TargetKind = 'server'; Condition = ''
            What = "'call' sends an HTTP request to the ClientDriver control plane running INSIDE a game client, and reads the parsed answer back. The dedicated server has no such plane: it is driven through its stdin by a wrapper process polling a control file, which is fire and forget. It also has no player character at all (it runs with IsBatchMode true, so CreateCharacterAndTakeControl never runs and LocalClientId stays 0), so every /player, /inventory, /cursor and /input path has nothing to act on."
            Instead = "testrig send -Target server -Command '<console text>'"
            InsteadLabel = 'Use the stdin channel:'
            Reference = 'Research/GameSystems/ListenHost.md'
        }
        [pscustomobject]@{
            Verb = 'call'; TargetKind = 'all'; Condition = ''
            What = "'call' cannot fan out across both halves: -Target all includes the dedicated server, which has no HTTP control plane. Fanning out over the instances alone is a different command from talking to the server, on purpose, because one returns a parsed answer per instance and the other is fire and forget."
            Instead = 'testrig call -Target clients -Path <path> [-Body <json>]'
            InsteadLabel = 'Fan out over the clients:'
            Reference = 'TestRig/MANUAL.md (the endpoint catalogue)'
        }
        [pscustomobject]@{
            Verb = 'send'; TargetKind = 'instance'; Condition = ''
            What = "'send' writes one line to the dedicated server's stdin through its host wrapper. A client instance has no stdin anybody can reach: it is launched with CreateProcessW on an isolated desktop and driven entirely over its HTTP control plane, which returns a structured answer instead of nothing."
            Instead = 'testrig call -Target {target} -Path /console/run -Body ''{"command":"<console text>"}'''
            InsteadLabel = 'Use the control plane:'
            Reference = 'TestRig/MANUAL.md (the endpoint catalogue)'
        }
        [pscustomobject]@{
            Verb = 'send'; TargetKind = 'clients'; Condition = ''
            What = "'send' is the dedicated server's stdin channel. There is nothing to fan it out over: a client instance has no stdin anybody can reach."
            Instead = 'testrig call -Target clients -Path /console/run -Body ''{"command":"<console text>"}'''
            InsteadLabel = 'Use the control plane:'
            Reference = 'TestRig/MANUAL.md (the endpoint catalogue)'
        }
        [pscustomobject]@{
            Verb = 'send'; TargetKind = 'all'; Condition = ''
            What = "'send' is the dedicated server's stdin channel and -Target all includes client instances, which have no stdin anybody can reach. The two control channels are not one channel with two transports: stdin is fire and forget, the HTTP plane answers."
            Instead = "testrig send -Target server -Command '<console text>'"
            InsteadLabel = 'Name the server:'
            Reference = 'TestRig/MANUAL.md, "Verbs"'
        }
        [pscustomobject]@{
            Verb = 'create'; TargetKind = 'server'; Condition = ''
            What = "'create' hard-links a fresh copy of the developer's game install into a new instance tree, one of N. The dedicated server is not one of N: it is a single install downloaded from Steam app 600760 by SteamCMD, with its BepInEx loader mirrored out of the client install. Those are different operations on different sources, so one verb cannot be a rename of the other."
            Instead = 'testrig update-game -Target server'
            InsteadLabel = 'Install or refresh the server:'
            Reference = 'TestRig/MANUAL.md, "Working sequences"'
        }
        [pscustomobject]@{
            Verb = 'create'; TargetKind = 'all'; Condition = ''
            What = "'create' builds ONE named client instance. It has no rig-wide meaning: the dedicated server is not an instance, and the other instances already exist."
            Instead = 'testrig create -Target <newInstanceName> [-Role host]'
            InsteadLabel = 'Name the instance:'
            Reference = 'TestRig/MANUAL.md, "The client half"'
        }
        [pscustomobject]@{
            Verb = 'remove'; TargetKind = 'server'; Condition = ''
            What = "'remove' deletes an instance tree and its save root. The dedicated server has no equivalent and the absence is deliberate: cleaning it is the developer's call, because its data/ tree holds worlds that predate any session and nothing here is allowed to decide they are disposable."
            Instead = 'delete TestRig/DedicatedServer/install/ by hand, then: testrig update-game -Target server'
            InsteadLabel = 'To rebuild the binaries:'
            Reference = 'TestRig/MANUAL.md, "The dedicated server half"'
        }
        [pscustomobject]@{
            Verb = 'remove'; TargetKind = 'all'; Condition = ''
            What = "'remove' deletes one named instance and its world. It is never rig-wide: -Target all would delete every world on the client half in one command, which no test has ever wanted and no undo exists for."
            Instead = 'testrig remove -Target <instanceName>'
            InsteadLabel = 'Name the instance:'
            Reference = 'TestRig/CLAUDE.md'
        }
        [pscustomobject]@{
            Verb = 'remove'; TargetKind = 'clients'; Condition = ''
            What = "'remove' deletes one named instance and its world. -Target clients would delete every one of them at once, which no test has ever wanted and no undo exists for."
            Instead = 'testrig remove -Target <instanceName>'
            InsteadLabel = 'Name the instance:'
            Reference = 'TestRig/CLAUDE.md'
        }
        [pscustomobject]@{
            Verb = 'snapshot'; TargetKind = 'server'; Condition = ''
            What = "'snapshot' fetches /status from each instance's control plane. The dedicated server has no control plane, so there is no equivalent blob to fetch. What it can answer about itself comes from its own state files and its log."
            Instead = 'testrig status -Target server   (and: testrig logs -Target server -Grep <pattern>)'
            InsteadLabel = 'Ask the server directly:'
            Reference = 'TestRig/MANUAL.md, "Verbs"'
        }
        [pscustomobject]@{
            Verb = 'snapshot'; TargetKind = 'all'; Condition = ''
            What = "'snapshot' is a control-plane fan-out and -Target all includes the dedicated server, which has none. Mixing a half that answers with a half that cannot would produce a file whose shape depends on what happened to be running."
            Instead = 'testrig snapshot -Target clients [-OutFile before.json]'
            InsteadLabel = 'Snapshot the clients:'
            Reference = 'TestRig/MANUAL.md, "The client half"'
        }
        [pscustomobject]@{
            Verb = 'wait'; TargetKind = 'server'; Condition = 'client-stage'
            What = "the readiness stages 'ping', 'modsLoaded' and 'menu' are client-instance states. A dedicated server has no control plane to ping, and never has a menu at all: it enters its world from the command line, so the only readiness question about it is whether that world is loaded and the simulation is ticking."
            Instead = 'testrig wait -Target server -Stage inWorld [-WaitSeconds 600]'
            InsteadLabel = 'Wait for the world:'
            Reference = 'TestRig/MANUAL.md, "Readiness"'
        }
        [pscustomobject]@{
            Verb = 'save'; TargetKind = 'server'; Condition = 'no-name'
            What = "the dedicated server's save is a console command that takes a name, and there is no 'save under the current name' form of it: the console has no notion of the world's current name to fall back on. A client instance does, which is why -SaveName is optional there and required here."
            Instead = 'testrig save -Target server -SaveName <SaveName>'
            InsteadLabel = 'Name the save:'
            Reference = 'TestRig/MANUAL.md, "Verbs"'
        }
        [pscustomobject]@{
            Verb = 'lock'; TargetKind = 'narrow'; Condition = ''
            What = "the session lock is RIG-WIDE and cannot be taken over half of it. The two halves share the developer's one game install and the per-Windows-user Unity state that nothing separates (PlayerCookie-v2.xml, the HKCU PlayerPrefs key), which is why there is one lock rather than two."
            Instead = 'testrig lock -Purpose "<what you are testing>"'
            InsteadLabel = 'Take the whole rig:'
            Reference = 'TestRig/CLAUDE.md, "The session lock covers the whole rig"'
        }
        [pscustomobject]@{
            Verb = 'unlock'; TargetKind = 'narrow'; Condition = ''
            What = "the session lock is RIG-WIDE and cannot be released for half of it."
            Instead = 'testrig unlock -As <id>'
            InsteadLabel = 'Release the whole rig:'
            Reference = 'TestRig/CLAUDE.md, "The session lock covers the whole rig"'
        }
        [pscustomobject]@{
            Verb = 'refresh-lock'; TargetKind = 'narrow'; Condition = ''
            What = "the session lock is RIG-WIDE and its timer is not per half."
            Instead = 'testrig refresh-lock -As <id>'
            InsteadLabel = 'Refresh the whole rig:'
            Reference = 'TestRig/CLAUDE.md, "The session lock covers the whole rig"'
        }
        [pscustomobject]@{
            Verb = 'capture-baseline'; TargetKind = 'narrow'; Condition = ''
            What = "the baseline is ONE definition of a clean rig covering both halves, exactly as one lock does. Capturing half of it would leave the other half restored to whatever an older capture said."
            Instead = 'testrig capture-baseline -As <id> [-Force]'
            InsteadLabel = 'Capture the whole rig:'
            Reference = 'TestRig/MANUAL.md, "State hygiene"'
        }
        [pscustomobject]@{
            Verb = 'reset'; TargetKind = 'narrow'; Condition = ''
            What = "the state reset is rig-wide by construction: it plans over both halves in one pass and clears the session marker only when every action in that plan succeeded. A half reset would leave the marker set and the next session would restore anyway."
            Instead = 'testrig reset -As <id> [-DryRun]'
            InsteadLabel = 'Reset the whole rig:'
            Reference = 'TestRig/MANUAL.md, "State hygiene"'
        }
        [pscustomobject]@{
            Verb = '*'; TargetKind = 'server'; Condition = 'instance-flags'
            What = "these flags describe one of N client instances ({flags}): an instance's identity, its ports, its window and its role in a session. The dedicated server is a single install with a single identity, so none of them has anything to bind to here."
            Instead = 'testrig create -Target <instanceName> -Role host -GamePort <port>'
            InsteadLabel = 'These belong to an instance:'
            Reference = 'TestRig/CLAUDE.md, "Two ways to host a world"'
        }
    )
}

function Get-RigRefusal {
    # The matching refusal, or $null. Verb '*' entries match any verb, which is how
    # the instance-flags refusal covers the whole surface without being repeated.
    param(
        [Parameter(Mandatory)] [string] $Verb,
        [Parameter(Mandatory)] [string] $TargetKind,
        [string] $Condition = ''
    )
    foreach ($r in (Get-RigRefusalTable)) {
        if ($r.Verb -ne $Verb -and $r.Verb -ne '*') { continue }
        if ($r.TargetKind -ne $TargetKind -and $r.TargetKind -ne 'any') { continue }
        if ([string]$r.Condition -ne [string]$Condition) { continue }
        return $r
    }
    return $null
}

function Format-RigRefusal {
    <#
        Render one refusal. The shape is fixed and every part of it earns its place:
        the command as typed, what the verb needs and why this target cannot provide
        it, a command that WOULD work, and where the durable explanation lives.
    #>
    param(
        [Parameter(Mandatory)] $Refusal,
        [Parameter(Mandatory)] [string] $Verb,
        [string] $Target = '',
        [hashtable] $Substitutions = @{}
    )
    $what    = [string]$Refusal.What
    $instead = [string]$Refusal.Instead
    foreach ($k in $Substitutions.Keys) {
        $what    = $what.Replace("{$k}", [string]$Substitutions[$k])
        $instead = $instead.Replace("{$k}", [string]$Substitutions[$k])
    }
    if ($Target) {
        $what    = $what.Replace('{target}', $Target)
        $instead = $instead.Replace('{target}', $Target)
    }

    $typed = if ($Target) { "testrig $Verb -Target $Target" } else { "testrig $Verb" }
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add($typed)
    foreach ($chunk in (Split-RigText -Text $what -Width 74)) {
        if ($lines.Count -eq 1) { $lines.Add("  x $chunk") } else { $lines.Add("    $chunk") }
    }
    $label = if ([string]$Refusal.InsteadLabel) { [string]$Refusal.InsteadLabel } else { 'Instead:' }
    $lines.Add("    $label  $instead")
    if ([string]$Refusal.Reference) { $lines.Add("    Why: $([string]$Refusal.Reference)") }
    return ($lines -join "`n")
}

function Split-RigText {
    # Word wrap, so a refusal reads as prose in a terminal instead of one long line.
    param([Parameter(Mandatory)] [string] $Text, [int] $Width = 74)
    $out   = New-Object System.Collections.Generic.List[string]
    $line  = ''
    foreach ($word in ($Text -split '\s+')) {
        if (-not $word) { continue }
        if (-not $line) { $line = $word; continue }
        if (($line.Length + 1 + $word.Length) -le $Width) { $line = "$line $word" }
        else { $out.Add($line); $line = $word }
    }
    if ($line) { $out.Add($line) }
    return $out.ToArray()
}

# ---- verbs and targets ----------------------------------------------------
#
# The verb surface's DECISIONS live here rather than in testrig.ps1 so they can be
# exercised with no rig, no registry and no game: which verbs default to the whole
# rig, what a -Target resolves to, and which combinations refuse. testrig.ps1 keeps
# only the wiring that needs real state.

function Get-RigVerbDefaultTarget {
    <#
        RIG-WIDE VERBS DEFAULT TO 'all'. That default is the point of this
        launcher: an agent asked to update the rig must not be able to update one
        half and walk away believing it updated the rig. Everything that acts on a
        specific running thing requires an explicit target instead, so a typo
        neither narrows nor widens the blast radius.
    #>
    param([Parameter(Mandatory)] [string] $Verb)
    switch ($Verb) {
        'lock'             { return 'all' }
        'unlock'           { return 'all' }
        'refresh-lock'     { return 'all' }
        'capture-baseline' { return 'all' }
        'reset'            { return 'all' }
        'status'           { return 'all' }
        'list'             { return 'all' }
        'update-game'      { return 'all' }
        'update-mods'      { return 'all' }
        'deploy'           { return 'all' }
        'logs'             { return 'all' }
        default            { return '' }
    }
}

function Resolve-RigTarget {
    <#
        Turn -Target into a decision: which half, and which instances.

        Kind is 'all', 'server', 'clients' or 'instance'. An unknown instance name
        is a THROW naming what is provisioned, never a silent empty set: an empty
        set makes a stop look successful and a start look done.
    #>
    param(
        [string] $Target,
        [Parameter(Mandatory)] [string] $Verb,
        [AllowNull()] [AllowEmptyCollection()] [string[]] $KnownInstances = @(),
        [switch] $AllowUnknown
    )
    $all = @($KnownInstances)
    $spec = $Target
    if (-not $spec) {
        $spec = Get-RigVerbDefaultTarget -Verb $Verb
        if (-not $spec) {
            throw "'$Verb' needs an explicit -Target: 'server', 'clients', or one or more instance names. It acts on a specific running thing, so it will not guess. See what exists with: testrig list"
        }
    }

    switch ($spec.ToLowerInvariant()) {
        'all'     { return [pscustomobject]@{ Kind = 'all';     Server = $true;  Names = $all; Spec = $spec } }
        'server'  { return [pscustomobject]@{ Kind = 'server';  Server = $true;  Names = @();  Spec = $spec } }
        'clients' { return [pscustomobject]@{ Kind = 'clients'; Server = $false; Names = $all; Spec = $spec } }
    }

    $wanted = @($spec.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if ($wanted.Count -eq 0) {
        throw "-Target '$spec' names nothing. Use 'all', 'server', 'clients', or one or more instance names."
    }
    if (-not $AllowUnknown) {
        foreach ($w in $wanted) {
            if ($all -notcontains $w) {
                $known = if ($all.Count -gt 0) { $all -join ', ' } else { '(none provisioned)' }
                throw "-Target '$w' is not a provisioned instance, and it is not 'all', 'server' or 'clients'. Provisioned: $known. Create it with: testrig create -Target $w [-Role host]"
            }
        }
    }
    # Names is built with @(...) inside the object literal, so a single name stays a
    # one-element array. A scalar here would later be enumerated character by
    # character by anything that foreach-es it.
    return [pscustomobject]@{ Kind = 'instance'; Server = $false; Names = @($wanted); Spec = $spec }
}

function Assert-RigVerbApplies {
    <#
        Fire every refusal that applies to this verb and target, BEFORE the lock is
        asserted and before any work: a refusal corrects the caller's model of the
        rig, and is worth nothing once a side effect has already happened.

        -Options carries what the decision needs from the command line, because
        reading it out of a caller's scope is exactly the per-scope trap that made
        $PSBoundParameters tests silently answer false:
            Stage               the readiness stage asked for
            SaveName            the save name given, if any
            HasWorld            whether a world argument was given to start
            TypedInstanceFlags  instance-shape flags the caller actually typed
    #>
    param(
        [Parameter(Mandatory)] [string] $Verb,
        [Parameter(Mandatory)] $Resolved,
        [hashtable] $Options = @{}
    )
    $kind  = [string]$Resolved.Kind
    $shown = if ($Resolved.Spec) { [string]$Resolved.Spec } else { $kind }
    $stage    = [string]$Options['Stage']
    $saveName = [string]$Options['SaveName']
    $hasWorld = [bool]$Options['HasWorld']
    # The Where-Object is not decoration: @($null) has a Count of ONE in
    # PowerShell, so an -Options with no TypedInstanceFlags key at all would
    # otherwise look like one typed flag and fire the instance-shape refusal on
    # every server-targeted command.
    $typed    = @($Options['TypedInstanceFlags'] | Where-Object { $_ })

    # The lock, the baseline and the reset are rig-wide by construction.
    if (@('lock', 'unlock', 'refresh-lock', 'capture-baseline', 'reset') -contains $Verb) {
        if ($kind -ne 'all') { Deny-RigVerb -Verb $Verb -TargetKind 'narrow' -Target $shown }
        return
    }

    switch ($Verb) {
        'call' {
            if ($kind -eq 'server') { Deny-RigVerb -Verb 'call' -TargetKind 'server' -Target $shown }
            if ($kind -eq 'all')    { Deny-RigVerb -Verb 'call' -TargetKind 'all'    -Target $shown }
        }
        'send' {
            if ($kind -eq 'instance') { Deny-RigVerb -Verb 'send' -TargetKind 'instance' -Target $shown }
            if ($kind -eq 'clients')  { Deny-RigVerb -Verb 'send' -TargetKind 'clients'  -Target $shown }
            if ($kind -eq 'all')      { Deny-RigVerb -Verb 'send' -TargetKind 'all'      -Target $shown }
        }
        'create' {
            if ($kind -eq 'server') { Deny-RigVerb -Verb 'create' -TargetKind 'server' -Target $shown }
            if ($kind -eq 'all' -or $kind -eq 'clients') { Deny-RigVerb -Verb 'create' -TargetKind 'all' -Target $shown }
        }
        'remove' {
            if ($kind -eq 'server')  { Deny-RigVerb -Verb 'remove' -TargetKind 'server'  -Target $shown }
            if ($kind -eq 'all')     { Deny-RigVerb -Verb 'remove' -TargetKind 'all'     -Target $shown }
            if ($kind -eq 'clients') { Deny-RigVerb -Verb 'remove' -TargetKind 'clients' -Target $shown }
        }
        'snapshot' {
            if ($kind -eq 'server') { Deny-RigVerb -Verb 'snapshot' -TargetKind 'server' -Target $shown }
            if ($kind -eq 'all')    { Deny-RigVerb -Verb 'snapshot' -TargetKind 'all'    -Target $shown }
        }
        'wait' {
            if ($Resolved.Server -and @('ping', 'modsLoaded', 'menu') -contains $stage) {
                Deny-RigVerb -Verb 'wait' -TargetKind 'server' -Condition 'client-stage' -Target $shown
            }
        }
        'save' {
            if ($Resolved.Server -and -not $saveName) {
                Deny-RigVerb -Verb 'save' -TargetKind 'server' -Condition 'no-name' -Target $shown
            }
        }
        'start' {
            if ($Resolved.Server -and -not $hasWorld) {
                Deny-RigVerb -Verb 'start' -TargetKind 'server' -Condition 'no-world' -Target $shown
            }
        }
    }

    # Instance-shape flags against the one install that has no instances. Only on a
    # target of exactly 'server': under -Target all they legitimately describe the
    # client half.
    if ($kind -eq 'server' -and $typed.Count -gt 0) {
        Deny-RigVerb -Verb '*' -TargetKind 'server' -Condition 'instance-flags' -Target $shown `
            -DisplayVerb $Verb `
            -Substitutions @{ flags = (($typed | ForEach-Object { "-$_" }) -join ', ') }
    }
}

function Deny-RigVerb {
    <#
        Refuse, teaching. Throws a message carrying the sentinel so testrig.ps1
        prints it plainly and exits 2 rather than dumping a PowerShell error with a
        stack trace over the top of it.
    #>
    param(
        [Parameter(Mandatory)] [string] $Verb,
        [Parameter(Mandatory)] [string] $TargetKind,
        [string] $Condition = '',
        [string] $Target = '',
        [string] $DisplayVerb = '',
        [hashtable] $Substitutions = @{}
    )
    $refusal = Get-RigRefusal -Verb $Verb -TargetKind $TargetKind -Condition $Condition
    if (-not $refusal) {
        throw "No refusal is defined for verb '$Verb' on target kind '$TargetKind' (condition '$Condition'). That is a bug in the refusal matrix in TestRig/lib/common.ps1, not a problem with the command."
    }
    # -DisplayVerb exists for the '*' entries, which match any verb: the echoed
    # command has to be the one the caller actually typed, not the wildcard.
    $shown = if ($DisplayVerb) { $DisplayVerb } else { $Verb }
    throw ($script:RigRefusalSentinel + (Format-RigRefusal -Refusal $refusal -Verb $shown -Target $Target -Substitutions $Substitutions))
}
