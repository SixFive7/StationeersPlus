<#
.SYNOPSIS
    Provisions, launches and fans out across N isolated Stationeers game-client instances.

.DESCRIPTION
    The launcher half of the client rig. The other half is the ClientDriver plugin, which is the
    control plane inside each instance.

    The boundary between the two is process creation. This script owns everything outside a game
    process, and everything that must keep working when a process is dead or wedged: provisioning
    an instance tree, creating the isolated Win32 desktop, starting and stopping, PID files, and
    fanning one command out across the rig. The plugin owns everything inside a process, which is
    everything that needs the Unity main thread or the game's own types. There is no third category.

    An instance is a hard-linked copy of the developer's real install on the same NTFS volume, so it
    costs a few megabytes instead of seven gigabytes. Nothing the game or a mod writes to is ever a
    hard link, because a hard link shares the file data and a write would reach back into the
    developer's install.

    Every instance runs on a separate Win32 desktop that is created but never switched to. That is
    what stops the game taking the developer's foreground: the no-activate show flag alone loses
    (measured 40 focus steals out of 40 samples), a separate desktop wins (0 out of 55).

    The source install is treated as strictly read-only.

    Operating manual: README.md next to this script.
    Durable internals:  RESEARCH.md next to this script.
    Repository conventions: CLAUDE.md (root).
    Developer environment: DEV.md.

.PARAMETER Provision
    Build or rebuild an instance: hard-link the game tree, seed its mod set, write its manifest.

.PARAMETER Instance
    Instance name, or a comma-separated list where an action accepts several.

.PARAMETER Port
    Control-plane TCP port for the instance. Defaults to 27700 plus the instance's index.

.PARAMETER ClientId
    Decimal ulong this instance presents as its player identity. Must be non-zero and unique across
    the rig. Defaults to 900000000000 plus the instance's index.

.PARAMETER Username
    Player name this instance presents. Defaults to the instance name.

.PARAMETER Width
    Window width in pixels. Default 800.

.PARAMETER Height
    Window height in pixels. Default 600.

.PARAMETER ForceGameplayInput
    Provision the instance with the cursor gate held open, so synthetic input reaches the game's
    per-frame consumers. Correct only for an instance nobody is sitting at, which is every instance
    this script creates, so it defaults on.

.PARAMETER SeedMods
    Copy the developer's local mod folders into the instance and repoint its modconfig.xml at the
    copy. On by default; the instance loads no local mods without it.

.PARAMETER Force
    Rebuild an instance that already exists, or override a refusal that is safe to override.

.PARAMETER All
    Apply the action to every provisioned instance.

.PARAMETER Start
    Launch the instance on the rig's isolated desktop.

.PARAMETER Stop
    Terminate the instance and clean up its PID file.

.PARAMETER Status
    Report each instance: provisioned, running, control-plane answering, phase, identity.

.PARAMETER List
    List provisioned instances and their manifests.

.PARAMETER Remove
    Delete an instance tree. Refuses while the instance is running.

.PARAMETER Desktop
    Name of the Win32 desktop to run instances on. Pass an empty string to run on the developer's
    desktop, which reintroduces the focus theft and is for debugging only.

.PARAMETER Wait
    Block until every selected instance reaches a readiness stage. The barrier across the rig.

.PARAMETER Stage
    Readiness stage for -Wait: ping, modsLoaded, menu, or inWorld.

.PARAMETER Broadcast
    Send one HTTP request to every selected instance and report each answer. The fan-out.

.PARAMETER Call
    Send one HTTP request to one instance.

.PARAMETER Path
    Control-plane path for -Broadcast or -Call, for example /config/set.

.PARAMETER Body
    JSON request body for -Broadcast or -Call. Omit for a GET.

.PARAMETER Snapshot
    Fetch /status from every selected instance in one go.

.PARAMETER OutFile
    Write the -Snapshot result to this path instead of the console.

.PARAMETER TimeoutSeconds
    Timeout for -Wait and for process teardown. Default 300 for -Wait, 30 for -Stop.

.PARAMETER Logs
    Tail the instance's BepInEx log.

.PARAMETER Tail
    Lines for -Logs. Default 50.

.PARAMETER Grep
    Regex filter for -Logs, applied to the whole file.

.PARAMETER InstancesRoot
    Where the hard-linked instance trees live. MUST be on the same NTFS volume as the game install,
    because hard links cannot cross volumes. Defaults to the STATIONEERS_CLIENTRIG_ROOT environment
    variable, then to instances/ beside this script. Set the environment variable in DEV.md when the
    repository and the game install are on different drives, which is the common case.
#>
[CmdletBinding()]
param(
    [switch] $Provision,
    [string] $Instance,
    [int]    $Port = 0,
    [string] $ClientId,
    [string] $Username,
    [int]    $Width  = 800,
    [int]    $Height = 600,
    [bool]   $ForceGameplayInput = $true,
    [bool]   $SeedMods = $true,
    [switch] $Force,

    [switch] $All,

    [switch] $Start,
    [string] $Desktop = 'StationeersRig',

    [switch] $Stop,

    [switch] $Status,
    [switch] $List,
    [switch] $Remove,

    [switch] $Wait,
    [ValidateSet('ping', 'modsLoaded', 'menu', 'inWorld')]
    [string] $Stage = 'menu',

    [switch] $Broadcast,
    [switch] $Call,
    [string] $Path,
    [string] $Body,

    [switch] $Snapshot,
    [string] $OutFile,

    [int]    $TimeoutSeconds = 0,

    [switch] $Logs,
    [int]    $Tail = 50,
    [string] $Grep,

    [string] $InstancesRoot
)

$ErrorActionPreference = 'Stop'

$RigRoot       = $PSScriptRoot
# <repo>/TestRig/ClientRig -> <repo>
$RepoRoot      = (Resolve-Path (Join-Path $RigRoot '..\..')).Path
$BuildPropsXml = Join-Path $RepoRoot 'Directory.Build.props'

# The instance trees are hard links into the game install, so they must sit on the install's
# volume. The repository frequently does not, so this is relocatable and the volume check below
# turns a wrong setting into a clear message rather than a 7 GB copy.
$InstancesDir  = if ($InstancesRoot) { $InstancesRoot }
                 elseif ($env:STATIONEERS_CLIENTRIG_ROOT) { $env:STATIONEERS_CLIENTRIG_ROOT }
                 else { Join-Path $RigRoot 'instances' }

# Per-instance state (manifest, settings, save root, logs, PID file) is ordinary files, not links,
# so it stays beside the script regardless of which volume the trees are on.
$DataDir       = Join-Path $RigRoot 'data'
$RigRegistry   = Join-Path $DataDir 'rig.json'

$PluginDll     = Join-Path $RigRoot 'ClientDriver\bin\Release\ClientDriver.dll'

# ---- environment helpers --------------------------------------------------

function Get-StationeersPath {
    if (-not (Test-Path $BuildPropsXml)) {
        throw "Directory.Build.props not found at repo root. Copy Directory.Build.props.template to Directory.Build.props and set <StationeersPath>. See DEV.md."
    }
    $xml  = [xml](Get-Content -Raw $BuildPropsXml)
    $path = $xml.Project.PropertyGroup.StationeersPath
    if ([string]::IsNullOrWhiteSpace($path)) {
        throw "<StationeersPath> in Directory.Build.props is empty. Set it to your Stationeers client install. See DEV.md."
    }
    if (-not (Test-Path (Join-Path $path 'rocketstation.exe'))) {
        throw "<StationeersPath>=$path does not contain rocketstation.exe. This rig links the CLIENT install, not the dedicated server. See DEV.md."
    }
    return $path
}

function Get-UserDataPath {
    # The game's own user-data root: Documents\My Games\Stationeers. Resolved from the Windows
    # shell folder rather than hardcoded, so nothing here is tied to one developer's layout.
    return (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'My Games\Stationeers')
}

function Assert-SameVolume {
    param([string] $A, [string] $B)
    $ra = [IO.Path]::GetPathRoot((Resolve-Path $A).Path)
    $rb = [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($B))
    if ($ra -ne $rb) {
        throw @"
Instance trees must be on the same NTFS volume as the game install, because hard links cannot cross
volumes and a real copy would cost about 7 GB per instance.
    game install    : $ra
    instances would be: $rb  ($B)
Point the rig at a folder on '$ra':
    `$env:STATIONEERS_CLIENTRIG_ROOT = '${ra}StationeersRig'
or pass -InstancesRoot '${ra}StationeersRig' on every call. Record the choice in DEV.md.
"@
    }
}

function Get-PidFromFile {
    param([string] $File)
    if (-not (Test-Path $File)) { return $null }
    $raw = (Get-Content -Raw -ErrorAction SilentlyContinue $File)
    if (-not $raw) { return $null }
    $val = $raw.Trim()
    if (-not $val) { return $null }
    [int]$val
}

function Test-PidAlive {
    param([Nullable[int]] $TargetPid)
    if (-not $TargetPid) { return $false }
    [bool](Get-Process -Id $TargetPid -ErrorAction SilentlyContinue)
}

# ---- the rig registry -----------------------------------------------------
#
# One file listing every instance. It is what makes -All work, and it is where each instance's
# manifest gets its peerPorts list, which is what lets an instance notice a sibling claiming the
# same ClientId.

function Read-Registry {
    if (-not (Test-Path $RigRegistry)) { return @() }
    try {
        $json = Get-Content -Raw $RigRegistry | ConvertFrom-Json
        if ($null -eq $json) { return @() }
        return @($json)
    }
    catch {
        Write-Warning "rig.json could not be parsed ($($_.Exception.Message)); treating the rig as empty."
        return @()
    }
}

function Write-Registry {
    param([Parameter(Mandatory)] $Entries)
    New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
    $tmp = "$RigRegistry.tmp"
    ,@($Entries) | ConvertTo-Json -Depth 8 | Set-Content -Path $tmp -Encoding utf8
    Move-Item -Path $tmp -Destination $RigRegistry -Force
}

function Get-InstanceEntry {
    param([Parameter(Mandatory)] [string] $Name)
    Read-Registry | Where-Object { $_.instanceName -eq $Name } | Select-Object -First 1
}

function Resolve-Targets {
    # Which instances an action applies to: -All, an explicit -Instance list, or a refusal.
    #
    # The local is deliberately NOT named $all. PowerShell variable names are case-insensitive, so
    # `$all = Read-Registry` silently overwrites the -All switch parameter with a non-empty array,
    # and every action then behaves as though -All had been passed. That bug shipped once and made
    # `-Stop -Instance nope` stop the whole rig instead of refusing.
    $registry = Read-Registry
    if ($All) {
        if ($registry.Count -eq 0) { throw "No instances are provisioned. Run: client-rig.ps1 -Provision -Instance client1" }
        return $registry
    }
    if (-not $Instance) {
        throw "Specify -Instance <name[,name]> or -All. Run -List to see what is provisioned."
    }
    $wanted = $Instance.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ }
    $hits = @()
    foreach ($w in $wanted) {
        $e = $registry | Where-Object { $_.instanceName -eq $w } | Select-Object -First 1
        if (-not $e) { throw "Instance '$w' is not provisioned. Run -List, or provision it with -Provision -Instance $w." }
        $hits += $e
    }
    return $hits
}

function Get-InstancePaths {
    param([Parameter(Mandatory)] [string] $Name)
    [pscustomobject]@{
        Name     = $Name
        Tree     = Join-Path $InstancesDir $Name
        Exe      = Join-Path $InstancesDir "$Name\rocketstation.exe"
        BepInEx  = Join-Path $InstancesDir "$Name\BepInEx"
        Data     = Join-Path $DataDir $Name
        Manifest = Join-Path $DataDir "$Name\instance.json"
        PidFile  = Join-Path $DataDir "$Name\game.pid"
        Settings = Join-Path $DataDir "$Name\setting.xml"
        UserData = Join-Path $DataDir "$Name\userdata"
        LogDir   = Join-Path $DataDir "$Name\logs"
    }
}

# ---- provisioning ---------------------------------------------------------

function Copy-LinkedTree {
    param(
        [Parameter(Mandatory)] [string] $SrcDir,
        [Parameter(Mandatory)] [string] $DstDir,
        [string[]] $RealCopyRelative = @()
    )
    New-Item -ItemType Directory -Force -Path $DstDir | Out-Null
    Get-ChildItem $SrcDir -Recurse -Directory | ForEach-Object {
        $rel = $_.FullName.Substring($SrcDir.Length).TrimStart('\')
        New-Item -ItemType Directory -Force -Path (Join-Path $DstDir $rel) | Out-Null
    }
    Get-ChildItem $SrcDir -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($SrcDir.Length).TrimStart('\')
        $target = Join-Path $DstDir $rel
        if ($RealCopyRelative -contains $rel) {
            Copy-Item $_.FullName $target -Force
            $script:copiedFiles++; $script:copiedBytes += $_.Length
        } else {
            New-Item -ItemType HardLink -Path $target -Value $_.FullName | Out-Null
            $script:linkedFiles++; $script:linkedBytes += $_.Length
        }
    }
}

function Invoke-Provision {
    if (-not $Instance) { throw "-Provision requires -Instance <name>." }
    if ($Instance.Contains(',')) { throw "-Provision takes one instance at a time." }

    $source = Get-StationeersPath

    $p = Get-InstancePaths -Name $Instance
    if ((Test-Path $p.Tree) -and -not $Force) {
        throw "Instance '$Instance' already exists at $($p.Tree). Pass -Force to rebuild it, or -Remove -Instance $Instance to delete it first."
    }
    if (Test-PidAlive (Get-PidFromFile $p.PidFile)) {
        throw "Instance '$Instance' is running. Stop it first: client-rig.ps1 -Stop -Instance $Instance"
    }

    # Index decides the defaults for port and identity, so provisioning three instances with no
    # flags produces three distinct, non-colliding ones.
    $registry = Read-Registry
    $existing = $registry | Where-Object { $_.instanceName -eq $Instance } | Select-Object -First 1
    $index = if ($existing) { [int]$existing.index } else {
        $used = @($registry | ForEach-Object { [int]$_.index })
        $i = 1; while ($used -contains $i) { $i++ }; $i
    }

    $effPort = if ($Port -gt 0) { $Port } else { 27700 + $index }
    $effId   = if ($ClientId) { $ClientId } else { (900000000000 + $index).ToString() }
    $effName = if ($Username) { $Username } else { $Instance }

    [uint64]$parsedId = 0
    if (-not [uint64]::TryParse($effId, [ref]$parsedId)) {
        throw "-ClientId '$effId' is not a decimal ulong."
    }
    if ($parsedId -eq 0) {
        throw "-ClientId 0 is the batch-mode sentinel and would collide with every other zero-id client. Pick a non-zero value."
    }
    $clash = $registry | Where-Object { $_.instanceName -ne $Instance -and $_.clientId -eq $effId } | Select-Object -First 1
    if ($clash) {
        throw "ClientId $effId is already used by instance '$($clash.instanceName)'. The server keys a player's body on this id, so both instances would resolve onto one character. Pick a different -ClientId."
    }
    $portClash = $registry | Where-Object { $_.instanceName -ne $Instance -and [int]$_.port -eq $effPort } | Select-Object -First 1
    if ($portClash) {
        throw "Port $effPort is already used by instance '$($portClash.instanceName)'. Pick a different -Port."
    }

    # Checked here, after the cheap identity and port guards, so a name clash is reported before a
    # volume misconfiguration and the caller fixes one thing at a time.
    Assert-SameVolume -A $source -B $InstancesDir

    if (Test-Path $p.Tree) {
        Write-Host "[Provision] Removing existing tree $($p.Tree) ..."
        Remove-Item $p.Tree -Recurse -Force
    }

    $script:linkedFiles = 0; $script:copiedFiles = 0
    $script:linkedBytes = 0; $script:copiedBytes = 0

    New-Item -ItemType Directory -Force -Path $p.Tree, $p.Data, $p.UserData, $p.LogDir | Out-Null

    # Files the game or a mod writes into the install root. These must NEVER be hard links: a hard
    # link shares the file data, so a write here would reach into the developer's install.
    $realCopyRootFiles = @('doorstop_config.ini', 'Fixing The Controls modifiers.ini')
    # Regenerated, and resolved relative to the working directory, so not worth carrying.
    $skipRootFiles     = @('imgui.ini', 'output_log.txt')

    Write-Host "[Provision] Linking rocketstation_Data ..."
    # app.info is a real copy purely so a write cannot reach the source. It is NOT a
    # persistentDataPath lever: the player takes company and product from the serialized
    # PlayerSettings inside globalgamemanagers, and editing app.info changes nothing.
    Copy-LinkedTree -SrcDir (Join-Path $source 'rocketstation_Data') -DstDir (Join-Path $p.Tree 'rocketstation_Data') -RealCopyRelative @('app.info')

    Write-Host "[Provision] Linking MonoBleedingEdge ..."
    Copy-LinkedTree -SrcDir (Join-Path $source 'MonoBleedingEdge') -DstDir (Join-Path $p.Tree 'MonoBleedingEdge')

    Write-Host "[Provision] Copying BepInEx (real copy: config, plugins, cache and logs must be per-instance) ..."
    Copy-Item (Join-Path $source 'BepInEx') (Join-Path $p.Tree 'BepInEx') -Recurse -Force
    # A separate BepInEx root is what buys per-instance config, plugins, cache, LogOutput.log and
    # InspectorPlus request/snapshot folders in one move. The BepInEx root is always
    # <dir of rocketstation.exe>\BepInEx and no environment variable relocates it.
    Remove-Item (Join-Path $p.Tree 'BepInEx\LogOutput.log*') -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $p.Tree 'BepInEx\cache')     -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $p.Tree 'BepInEx\inspector') -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path (Join-Path $p.Tree 'BepInEx\cache') | Out-Null
    $bep = Get-ChildItem (Join-Path $p.Tree 'BepInEx') -Recurse -File | Measure-Object Length -Sum
    $script:copiedFiles += $bep.Count; $script:copiedBytes += $bep.Sum

    Write-Host "[Provision] Handling root files ..."
    Get-ChildItem $source -File | ForEach-Object {
        if ($skipRootFiles -contains $_.Name) { return }
        $target = Join-Path $p.Tree $_.Name
        if ($realCopyRootFiles -contains $_.Name) {
            Copy-Item $_.FullName $target -Force
            $script:copiedFiles++; $script:copiedBytes += $_.Length
        } else {
            New-Item -ItemType HardLink -Path $target -Value $_.FullName | Out-Null
            $script:linkedFiles++; $script:linkedBytes += $_.Length
        }
    }

    Invoke-DeployPlugin -Paths $p

    if ($SeedMods) { Invoke-SeedMods -Paths $p }

    # Register before writing manifests, because every manifest carries the whole rig's port list.
    $entry = [pscustomobject]@{
        instanceName = $Instance
        index        = $index
        port         = $effPort
        clientId     = $effId
        username     = $effName
        width        = $Width
        height       = $Height
        forceGameplayInput = [bool]$ForceGameplayInput
        provisionedUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'")
    }
    $registry = @($registry | Where-Object { $_.instanceName -ne $Instance }) + $entry
    Write-Registry $registry
    Write-AllManifests

    Write-Host ""
    Write-Host "[Provision] Instance '$Instance' built."
    Write-Host ("[Provision]   hard-linked : {0,6} files, {1,8:N1} MB shared (near-zero new disk)" -f $script:linkedFiles, ($script:linkedBytes/1MB))
    Write-Host ("[Provision]   real copies : {0,6} files, {1,8:N1} MB new disk" -f $script:copiedFiles, ($script:copiedBytes/1MB))
    Write-Host "[Provision]   port        : $effPort"
    Write-Host "[Provision]   clientId    : $effId"
    Write-Host "[Provision]   username    : $effName"
    Write-Host "[Provision]   manifest    : $($p.Manifest)"
    Write-Host "[Provision] Next: client-rig.ps1 -Start -Instance $Instance"
}

function Invoke-DeployPlugin {
    param([Parameter(Mandatory)] $Paths)
    if (-not (Test-Path $PluginDll)) {
        Write-Warning "[$($Paths.Name)] ClientDriver.dll not found at $PluginDll. Build it first: dotnet build ClientDriver.sln -c Release. The instance will run without a control plane."
        return
    }
    $dst = Join-Path $Paths.BepInEx 'plugins\ClientDriver'
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    Copy-Item $PluginDll (Join-Path $dst 'ClientDriver.dll') -Force
    # BepInEx/plugins/ is loaded by the Chainloader directly, before StationeersLaunchPad runs.
    # The DLL must not ALSO sit under a StationeersLaunchPad mod folder: two loaders means Awake
    # twice and every Harmony patch registered twice.
    Write-Host "[Provision] ClientDriver -> $dst"
}

function Invoke-SeedMods {
    param([Parameter(Mandatory)] $Paths)

    $userData = Get-UserDataPath
    $srcMods  = Join-Path $userData 'mods'
    $srcCfg   = Join-Path $userData 'modconfig.xml'
    if (-not (Test-Path $srcCfg)) {
        Write-Warning "[$($Paths.Name)] No modconfig.xml at $srcCfg; skipping the mod seed. The instance will load Workshop mods only."
        return
    }

    Write-Host "[Provision] Seeding mods from the user data folder (read-only source) ..."
    $dstMods = Join-Path $Paths.UserData 'mods'
    if (Test-Path $dstMods) { Remove-Item $dstMods -Recurse -Force }
    if (Test-Path $srcMods) { Copy-Item $srcMods $dstMods -Recurse -Force }
    else { New-Item -ItemType Directory -Force -Path $dstMods | Out-Null }

    # Local mod entries are absolute paths, and StationeersLaunchPad prunes entries whose folder is
    # not under the active save path, so each instance needs its own copy and its own modconfig.
    $xml = Get-Content $srcCfg -Raw
    if (Test-Path $srcMods) { $xml = $xml.Replace($srcMods, $dstMods) }
    Set-Content -Path (Join-Path $Paths.UserData 'modconfig.xml') -Value $xml -Encoding utf8 -NoNewline

    foreach ($f in @('modrepos.xml', 'PlayerCosmetics_0.xml')) {
        $src = Join-Path $userData $f
        if (Test-Path $src) { Copy-Item $src (Join-Path $Paths.UserData $f) -Force }
    }

    # SavePathOverride moves StationSaveUtils.DefaultPath itself, which is the only lever that
    # separates modconfig.xml.
    #
    # DO NOT reach for the launch flag "-settings SavePath" instead. It moves the save tree but
    # NOT DefaultPath, so StationeersLaunchPad scans an empty <SavePath>\mods\, finds nothing, and
    # rewrites the DEVELOPER'S SHARED modconfig.xml with every <Local> entry deleted. Observed on a
    # first boot: five local mod entries silently removed from the developer's own config, and
    # nothing warned. That flag is never passed by this script.
    $lpCfg = Join-Path $Paths.BepInEx 'config\stationeers.launchpad.cfg'
    if (Test-Path $lpCfg) {
        $line = "SavePathOverride = " + $Paths.UserData
        $content = Get-Content $lpCfg
        if ($content -match '^SavePathOverride\s*=') {
            $content = $content -replace '^SavePathOverride\s*=.*$', $line
        } else {
            $content += $line
        }
        Set-Content -Path $lpCfg -Value $content -Encoding utf8
        Write-Host "[Provision] SavePathOverride -> $($Paths.UserData)"
    } else {
        Write-Warning "[$($Paths.Name)] stationeers.launchpad.cfg not found at $lpCfg; SavePathOverride not set. The instance would share the developer's user-data folder. Launch it once to generate the config, then re-run -Provision -Force."
    }
}

function Write-AllManifests {
    # Every manifest carries the whole rig's port list, so an instance can ask its siblings who
    # they are. Rewritten for every instance whenever the registry changes, which is why this is
    # one function rather than a step inside provisioning.
    $registry = Read-Registry
    $ports = @($registry | ForEach-Object { [int]$_.port })
    foreach ($e in $registry) {
        $p = Get-InstancePaths -Name $e.instanceName
        New-Item -ItemType Directory -Force -Path $p.Data | Out-Null
        $manifest = [ordered]@{
            instanceName  = $e.instanceName
            port          = [int]$e.port
            clientId      = [string]$e.clientId
            username      = [string]$e.username
            window        = [ordered]@{ forceWindowed = $true; width = [int]$e.width; height = [int]$e.height }
            gameplayInput = [ordered]@{ force = [bool]$e.forceGameplayInput; everywhere = $false }
            savePath      = $p.UserData
            desktop       = $Desktop
            rigRoot       = $RigRoot
            peerPorts     = $ports
        }
        $tmp = "$($p.Manifest).tmp"
        $manifest | ConvertTo-Json -Depth 6 | Set-Content -Path $tmp -Encoding utf8
        Move-Item -Path $tmp -Destination $p.Manifest -Force
    }
}

# ---- launching ------------------------------------------------------------

function Add-LauncherType {
    if ([System.Management.Automation.PSTypeName]'ClientRigLauncher'.Type) { return }
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;

// Launches a process on a named Win32 desktop without activating its window.
//
// The desktop is the mechanism, not an optimisation. STARTF_USESHOWWINDOW with SW_SHOWNOACTIVATE
// alone loses: wShowWindow only governs the first ShowWindow(SW_SHOWDEFAULT), and Unity calls
// ShowWindow itself once its window exists, so the flag is ignored. Measured 40 focus steals out
// of 40 samples over two minutes. A window on another desktop cannot appear on the developer's
// screen and cannot touch their foreground or input queue at all: 0 out of 55.
//
// SwitchDesktop is deliberately NOT imported, and must never be. Switching is the one call that
// would put a driven instance in front of the developer.
//
// .NET's ProcessStartInfo cannot express either lpDesktop or wShowWindow with
// UseShellExecute = false, which is the whole reason for the P/Invoke.
public static class ClientRigLauncher
{
    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess; public IntPtr hThread; public uint dwProcessId; public uint dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
        public int dwX; public int dwY; public int dwXSize; public int dwYSize;
        public int dwXCountChars; public int dwYCountChars; public int dwFillAttribute;
        public int dwFlags; public short wShowWindow; public short cbReserved2;
        public IntPtr lpReserved2; public IntPtr hStdInput; public IntPtr hStdOutput; public IntPtr hStdError;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(
        string lpApplicationName, StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles,
        uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateDesktopW(string desktop, string device, IntPtr devmode,
        int flags, uint accessMask, IntPtr attributes);

    const int  STARTF_USESHOWWINDOW = 0x00000001;
    const uint GENERIC_ALL = 0x10000000;

    // Creates the desktop if it does not exist, or opens it if it does. The desktop object stays
    // alive as long as a process is running on it and disappears on its own afterwards, so the
    // handle does not need to outlive this call and there is nothing to clean up.
    public static void EnsureDesktop(string name)
    {
        IntPtr h = CreateDesktopW(name, null, IntPtr.Zero, 0, GENERIC_ALL, IntPtr.Zero);
        if (h == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    // CreateProcessW may write into lpCommandLine, so it has to be a mutable buffer.
    public static uint Start(string exe, string commandLine, string workingDir, short showWindow, string desktop)
    {
        STARTUPINFO si = new STARTUPINFO();
        si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
        si.dwFlags = STARTF_USESHOWWINDOW;
        si.wShowWindow = showWindow;
        if (!string.IsNullOrEmpty(desktop)) si.lpDesktop = desktop;

        StringBuilder cmd = new StringBuilder(commandLine, commandLine.Length + 64);

        PROCESS_INFORMATION pi;
        if (!CreateProcessW(exe, cmd, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, workingDir, ref si, out pi))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        uint procId = pi.dwProcessId;
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return procId;
    }
}
'@
}

function Format-Arg {
    param([string] $Value)
    if ($Value -match '[\s"]') { return '"' + ($Value -replace '"', '\"') + '"' }
    return $Value
}

function Invoke-Start {
    Add-LauncherType
    $targets = Resolve-Targets

    if ($Desktop) {
        [ClientRigLauncher]::EnsureDesktop($Desktop)
        Write-Host "[Start] Desktop: WinSta0\$Desktop (created if absent, never switched to)"
    } else {
        Write-Warning "[Start] No -Desktop given. Instances will run on the developer's desktop and WILL take the foreground. Debugging only."
    }

    foreach ($e in $targets) {
        $p = Get-InstancePaths -Name $e.instanceName
        if (-not (Test-Path $p.Exe)) {
            Write-Warning "[$($e.instanceName)] Not provisioned (no $($p.Exe)). Skipping."
            continue
        }
        $running = Get-PidFromFile $p.PidFile
        if (Test-PidAlive $running) {
            Write-Warning "[$($e.instanceName)] Already running (PID $running). Skipping."
            continue
        }

        New-Item -ItemType Directory -Force -Path $p.Data, $p.UserData, $p.LogDir | Out-Null
        Write-AllManifests

        $stamp    = Get-Date -Format 'yyyyMMdd-HHmmss'
        $unityLog = Join-Path $p.LogDir "unity-$stamp.log"

        # -logFile with a unique path is mandatory, and not for the reason it looks like. Two
        # instances without it both start fine; what happens is that the second starter wins
        # Player.log, the first instance's log is discarded with no error, and Player-prev.log is
        # zeroed by two rotations in one second, destroying the developer's previous log.
        #
        # -settings SavePath is deliberately absent. See the note in Invoke-SeedMods.
        #
        # -screen-* are kept even though the game overwrites them a moment later, so the window is
        # the right size before the plugin's patches run and there is no fullscreen flash.
        $argv = @(
            '-logFile', $unityLog,
            '-settingspath', $p.Settings,
            '-screen-width', $Width, '-screen-height', $Height, '-screen-fullscreen', '0'
        )

        $commandLine = ((@($p.Exe) + $argv) | ForEach-Object { Format-Arg ([string]$_) }) -join ' '

        # The plugin finds its manifest through this variable first, and through the working
        # directory second. CreateProcessW with a null environment block inherits ours.
        $env:STATIONEERS_CLIENTRIG_MANIFEST = $p.Manifest

        $SW_SHOWNOACTIVATE = 4
        $desktopSpec = if ($Desktop) { "WinSta0\$Desktop" } else { '' }

        try {
            $procId = [ClientRigLauncher]::Start($p.Exe, $commandLine, $p.Data, [int16]$SW_SHOWNOACTIVATE, $desktopSpec)
        }
        finally {
            Remove-Item Env:\STATIONEERS_CLIENTRIG_MANIFEST -ErrorAction SilentlyContinue
        }

        Set-Content -Path $p.PidFile -Value $procId
        Write-Host "[$($e.instanceName)] PID $procId, port $($e.port), log $unityLog"
    }

    Write-Host "[Start] Boot to the main menu takes roughly 100 seconds. Wait for it with:"
    Write-Host "[Start]   client-rig.ps1 -Wait -All -Stage menu"
}

# ---- stopping -------------------------------------------------------------

function Invoke-Stop {
    $targets = Resolve-Targets
    $timeout = if ($TimeoutSeconds -gt 0) { $TimeoutSeconds } else { 30 }

    foreach ($e in $targets) {
        $p = Get-InstancePaths -Name $e.instanceName
        $procId = Get-PidFromFile $p.PidFile
        if (-not (Test-PidAlive $procId)) {
            Remove-Item -Force -ErrorAction SilentlyContinue $p.PidFile
            Write-Host "[$($e.instanceName)] Not running."
            continue
        }

        # Ask politely through the control plane first: a clean Application.Quit lets the game
        # flush its own state instead of being killed mid-write.
        try {
            Invoke-Control -Port ([int]$e.port) -Path '/quit' -BodyJson '{"hard":false}' -TimeoutSec 5 | Out-Null
            Write-Host "[$($e.instanceName)] Quit requested."
        }
        catch {
            Write-Host "[$($e.instanceName)] Control plane did not answer; going straight to a kill."
        }

        $deadline = (Get-Date).AddSeconds($timeout)
        while ((Get-Date) -lt $deadline -and (Test-PidAlive $procId)) { Start-Sleep -Milliseconds 500 }

        if (Test-PidAlive $procId) {
            Write-Warning "[$($e.instanceName)] Still alive after ${timeout}s; killing PID $procId."
            Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 500
        }
        Remove-Item -Force -ErrorAction SilentlyContinue $p.PidFile
        Write-Host "[$($e.instanceName)] Stopped."
    }
}

function Invoke-Remove {
    if (-not $Instance) { throw "-Remove requires -Instance <name>." }
    $p = Get-InstancePaths -Name $Instance
    if (Test-PidAlive (Get-PidFromFile $p.PidFile)) {
        throw "Instance '$Instance' is running. Stop it first: client-rig.ps1 -Stop -Instance $Instance"
    }
    if (Test-Path $p.Tree) { Remove-Item $p.Tree -Recurse -Force }
    if (Test-Path $p.Data) { Remove-Item $p.Data -Recurse -Force }
    Write-Registry (Read-Registry | Where-Object { $_.instanceName -ne $Instance })
    Write-AllManifests
    Write-Host "[Remove] Instance '$Instance' deleted. The source install is untouched: only hard links and per-instance copies were removed."
}

# ---- the control plane, from outside --------------------------------------

function Invoke-Control {
    # One HTTP call to one instance's control plane. Returns the parsed response, or throws.
    param(
        [Parameter(Mandatory)] [int] $Port,
        [Parameter(Mandatory)] [string] $Path,
        [string] $BodyJson,
        [int] $TimeoutSec = 30
    )
    $uri = "http://127.0.0.1:$Port$Path"
    if ($BodyJson) {
        return Invoke-RestMethod -Uri $uri -Method Post -Body $BodyJson -ContentType 'application/json' -TimeoutSec $TimeoutSec
    }
    return Invoke-RestMethod -Uri $uri -Method Get -TimeoutSec $TimeoutSec
}

function Test-InstanceStage {
    # Is this instance at or past the named readiness stage. The three stages are genuinely
    # different and conflating them is a real trap: loadedPluginCount alone is not "ready", because
    # the splash screen is still drawing and it suppresses the in-game ImGui windows.
    param(
        [Parameter(Mandatory)] [int] $Port,
        [Parameter(Mandatory)] [string] $Want
    )
    try {
        if ($Want -eq 'ping') { Invoke-Control -Port $Port -Path '/ping' -TimeoutSec 3 | Out-Null; return $true }
        $s = Invoke-Control -Port $Port -Path '/status' -TimeoutSec 5
        switch ($Want) {
            'modsLoaded' { return ([int]$s.loadedPluginCount -gt 10) }
            'menu'       { return ($s.gameInitialized -eq $true -and $s.phase -eq 'menu') }
            'inWorld'    { return ($s.phase -eq 'inWorld') }
        }
        return $false
    }
    catch { return $false }
}

function Invoke-Wait {
    $targets = Resolve-Targets
    $timeout = if ($TimeoutSeconds -gt 0) { $TimeoutSeconds } else { 300 }
    $deadline = (Get-Date).AddSeconds($timeout)

    Write-Host "[Wait] Barrier: $($targets.Count) instance(s) must reach stage '$Stage' within ${timeout}s."
    $pending = @{}
    foreach ($e in $targets) { $pending[$e.instanceName] = [int]$e.port }

    while ($pending.Count -gt 0 -and (Get-Date) -lt $deadline) {
        foreach ($name in @($pending.Keys)) {
            if (Test-InstanceStage -Port $pending[$name] -Want $Stage) {
                Write-Host "[Wait] $name reached '$Stage'."
                $pending.Remove($name)
            }
        }
        if ($pending.Count -eq 0) { break }
        Start-Sleep -Seconds 2
    }

    if ($pending.Count -gt 0) {
        # The most common cause is worth naming: a transient Steam Workshop query failure parks
        # StationeersLaunchPad on its own error screen forever with the plugin count stuck at 2.
        foreach ($name in $pending.Keys) {
            $port = $pending[$name]
            $detail = try {
                $s = Invoke-Control -Port $port -Path '/status' -TimeoutSec 5
                "phase=$($s.phase) gameInitialized=$($s.gameInitialized) plugins=$($s.loadedPluginCount)"
            } catch { 'control plane did not answer' }
            Write-Warning "[$name] Did not reach '$Stage': $detail"
        }
        throw "[Wait] $($pending.Count) instance(s) did not reach '$Stage' within ${timeout}s. If plugins is stuck at 2 with gameInitialized false, StationeersLaunchPad hit a transient Steam Workshop failure and is parked on its error screen: stop the instance and start it again, which clears it."
    }
    Write-Host "[Wait] All instances reached '$Stage'."
}

function Invoke-Broadcast {
    if (-not $Path) { throw "-Broadcast requires -Path <control-plane path>, for example -Path /config/set." }
    $targets = Resolve-Targets
    Write-Host "[Broadcast] $Path -> $($targets.Count) instance(s)"
    $failed = 0
    foreach ($e in $targets) {
        try {
            $r = Invoke-Control -Port ([int]$e.port) -Path $Path -BodyJson $Body -TimeoutSec 60
            $ok = if ($null -ne $r.ok) { $r.ok } else { $true }
            if (-not $ok) { $failed++ }
            Write-Host "[$($e.instanceName)] ok=$ok"
            $r | ConvertTo-Json -Depth 6 -Compress
        }
        catch {
            $failed++
            Write-Warning "[$($e.instanceName)] $($_.Exception.Message)"
        }
    }
    if ($failed -gt 0) {
        throw "[Broadcast] $failed of $($targets.Count) instance(s) failed. A partial broadcast leaves the rig in mixed state; fix and re-run before drawing any conclusion from a test."
    }
}

function Invoke-Call {
    if (-not $Path) { throw "-Call requires -Path <control-plane path>." }
    if (-not $Instance) { throw "-Call requires -Instance <name>." }
    $e = Get-InstanceEntry -Name $Instance
    if (-not $e) { throw "Instance '$Instance' is not provisioned. Run -List." }
    $r = Invoke-Control -Port ([int]$e.port) -Path $Path -BodyJson $Body -TimeoutSec 120
    $r | ConvertTo-Json -Depth 10
}

function Invoke-Snapshot {
    $targets = Resolve-Targets
    $rows = @()
    foreach ($e in $targets) {
        $row = [ordered]@{ instanceName = $e.instanceName; port = [int]$e.port }
        try   { $row['status'] = Invoke-Control -Port ([int]$e.port) -Path '/status' -TimeoutSec 15 }
        catch { $row['error'] = $_.Exception.Message }
        $rows += [pscustomobject]$row
    }
    $json = ,@($rows) | ConvertTo-Json -Depth 12
    if ($OutFile) {
        Set-Content -Path $OutFile -Value $json -Encoding utf8
        Write-Host "[Snapshot] $($rows.Count) instance(s) -> $OutFile"
    } else {
        $json
    }
}

# ---- status and logs ------------------------------------------------------

function Invoke-Status {
    $registry = Read-Registry
    if ($registry.Count -eq 0) {
        Write-Host "No instances are provisioned. Create one: client-rig.ps1 -Provision -Instance client1"
        return
    }
    $wanted = if ($Instance) { @($Instance.Split(',') | ForEach-Object { $_.Trim() }) } else { $null }
    foreach ($e in $registry) {
        if ($wanted -and ($wanted -notcontains $e.instanceName)) { continue }
        $p = Get-InstancePaths -Name $e.instanceName
        $procId = Get-PidFromFile $p.PidFile
        $alive = Test-PidAlive $procId

        $line = if ($alive) { "running (PID $procId)" } else { 'stopped' }
        Write-Host "$($e.instanceName):"
        Write-Host "  process:    $line"
        Write-Host "  port:       $($e.port)"
        Write-Host "  identity:   $($e.username) ($($e.clientId))"
        Write-Host "  tree:       $($p.Tree)"

        if ($alive) {
            try {
                $s = Invoke-Control -Port ([int]$e.port) -Path '/status' -TimeoutSec 5
                Write-Host "  phase:      $($s.phase) (gameInitialized=$($s.gameInitialized), plugins=$($s.loadedPluginCount))"
                Write-Host "  foreground: $($s.foreground.verdict) (ownDesktop=$($s.foreground.ownDesktop))"
                Write-Host "  inputGate:  open=$($s.gameplayInputGateOpen)"
                if ($s.instance.peers.conflictDetected) {
                    Write-Warning "  identity conflict: $($s.instance.peers.conflict)"
                }
            }
            catch {
                Write-Host "  control:    not answering yet ($($_.Exception.Message))"
            }
        }
    }
}

function Invoke-List {
    $registry = Read-Registry
    if ($registry.Count -eq 0) {
        Write-Host "No instances are provisioned."
        return
    }
    $registry | Sort-Object index | Format-Table instanceName, index, port, clientId, username, provisionedUtc -AutoSize
}

function Invoke-Logs {
    if (-not $Instance) { throw "-Logs requires -Instance <name>." }
    $p = Get-InstancePaths -Name $Instance
    $log = Join-Path $p.BepInEx 'LogOutput.log'
    if (-not (Test-Path $log)) {
        Write-Host "No BepInEx log at $log."
        return
    }
    if ($Grep) { Get-Content $log | Select-String -Pattern $Grep }
    else       { Get-Content -Tail $Tail $log }
}

# ---- dispatch -------------------------------------------------------------

if ($Provision) { Invoke-Provision; return }
if ($Start)     { Invoke-Start;     return }
if ($Stop)      { Invoke-Stop;      return }
if ($Remove)    { Invoke-Remove;    return }
if ($Wait)      { Invoke-Wait;      return }
if ($Broadcast) { Invoke-Broadcast; return }
if ($Call)      { Invoke-Call;      return }
if ($Snapshot)  { Invoke-Snapshot;  return }
if ($Status)    { Invoke-Status;    return }
if ($List)      { Invoke-List;      return }
if ($Logs)      { Invoke-Logs;      return }

@"
Stationeers client rig. Provisions and drives N isolated game clients.

Operating manual:  $(Join-Path $RigRoot 'README.md')
Durable internals: $(Join-Path $RigRoot 'RESEARCH.md')

Instance trees are hard links into the game install, so they must be on the install's volume.
Set this once per shell (or record it in DEV.md) when the repository is on a different drive:
    `$env:STATIONEERS_CLIENTRIG_ROOT = '<drive of the game install>\StationeersRig'
Current instances root: $InstancesDir

Build the plugin first (the instances get whatever is in bin/Release):
    dotnet build $(Join-Path $RigRoot 'ClientDriver.sln') -c Release

Provision (once per instance; port and identity default off the instance index):
    client-rig.ps1 -Provision -Instance client1
    client-rig.ps1 -Provision -Instance client2
    client-rig.ps1 -Provision -Instance client1 -Force          (rebuild, picks up a new plugin build)

Lifecycle:
    client-rig.ps1 -Start  -All                                 (isolated desktop, never takes focus)
    client-rig.ps1 -Wait   -All -Stage menu                     (barrier; roughly 100 s from cold)
    client-rig.ps1 -Status -All
    client-rig.ps1 -Stop   -All
    client-rig.ps1 -Remove -Instance client1

Fan-out:
    client-rig.ps1 -Broadcast -All -Path /config/set -Body '{"guid":"net.example","section":"S","key":"K","value":"true"}'
    client-rig.ps1 -Call -Instance client1 -Path /input/scroll -Body '{"notches":1}'
    client-rig.ps1 -Snapshot -All -OutFile before.json
    client-rig.ps1 -Wait -All -Stage inWorld -TimeoutSeconds 600

Diagnosis:
    client-rig.ps1 -Call -Instance client1 -Path /diag/input
    client-rig.ps1 -Logs -Instance client1 -Grep 'ClientDriver'

Traps this script already handles for you, documented in README.md:
    -settings SavePath is never passed (it makes StationeersLaunchPad delete the developer's local mods)
    -logFile is always unique (otherwise the developer's Player-prev.log is zeroed)
    -nographics is never passed (Unity refuses it without -batchmode and pops a modal error)
"@
