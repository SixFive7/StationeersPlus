<#
.SYNOPSIS
    Stationeers Dedicated Server launcher (agent-driven lifecycle).

.DESCRIPTION
    Bootstraps, deploys mods to, starts, controls, and stops a self-contained
    Stationeers Dedicated Server install rooted at <repo>/TestRig/DedicatedServer/.

    -Start launches a detached host wrapper that owns the server process and
    relays commands written to a control file into the server's stdin. The
    launcher returns immediately. Subsequent invocations (-Save, -SendCommand,
    -Stop, -Status, -Logs) coordinate via PID files and the control file under
    TestRig/DedicatedServer/data/.

    Operating manual: TestRig/DedicatedServer/CLAUDE.md.
    Repository conventions: CLAUDE.md (root).
    Developer environment: DEV.md.

.PARAMETER Bootstrap
    Install / refresh the dedicated server via SteamCMD and mirror the BepInEx
    loader from the client install.

.PARAMETER DeployMods
    Copy built mod DLLs from Mods/<X>/<X>/bin/<Configuration>/<X>.dll into the
    server's BepInEx/plugins/<X>/.

.PARAMETER Mod
    Limit -DeployMods to one mod (folder name under Mods/).

.PARAMETER Configuration
    Build configuration to source from. Default Release.

.PARAMETER Start
    Launch the server detached. Specify -Load <SaveName> -Map <Map> or -New <Map>.

.PARAMETER Load
    Save name to load (must exist under TestRig/DedicatedServer/data/saves/).

.PARAMETER Map
    World id. Verified valid ids in 0.2.6228.27061: Lunar, Mars2, Europa3,
    MimasHerschel, Venus, Vulcan2 (and Vulcan, marked deprecated).

.PARAMETER New
    Create a new world on the given map.

.PARAMETER GamePort
    Server's UDP GamePort. Default 28016 (offset by +1000 from the
    Stationeers client default 27016 so the dedicated server can run
    alongside a client on the same machine without binding conflicts).

.PARAMETER UpdatePort
    Server's UDP UpdatePort. Default 28015 (paired with GamePort).

.PARAMETER Stop
    Send 'quit' to a running server, wait for clean exit, then force-kill if
    the timeout elapses. Pair with -SaveAs to save first.

.PARAMETER SaveAs
    With -Stop: save the world under this name and wait for confirmation
    before sending 'quit'.

.PARAMETER TimeoutSeconds
    Per-step timeout for save confirmation and clean exit. Default 30.

.PARAMETER SendCommand
    Forward a raw command string to the server's stdin. Pair with -Command.

.PARAMETER Command
    The raw command text for -SendCommand.

.PARAMETER Save
    Send a 'save "<Name>"' command and wait for confirmation in the log.

.PARAMETER Name
    Save name for -Save.

.PARAMETER WaitSeconds
    How long a blocking wait waits. With -Save: how long to wait for the save
    confirmation (default 30). With -Lock: how long to QUEUE for the rig when
    another session holds it (default 0, meaning fail immediately). The two
    defaults differ, so the value is only applied to the action you actually
    passed it with.

.PARAMETER Status
    Report whether the host wrapper and server are running, PIDs, uptime,
    and the last log line.

.PARAMETER Logs
    Print the dedicated server log. Pair with -Tail or -Grep.

.PARAMETER Tail
    With -Logs: number of trailing lines to print. Default 50.

.PARAMETER Grep
    With -Logs: filter the log by a regex.

.PARAMETER SyncMods
    Mirror the client's mod set onto the server install. Reads the user's
    modconfig.xml (read-only on the source), copies each enabled Workshop /
    Local mod into <install>/mods/<Source>_<DirName>/, and writes a baked
    <install>/modconfig.xml with Local entries pointing at the copies. This
    replicates StationeersLaunchPad's "Export Mod Package" feature without
    needing the UI. See Research/Workflows/StationeersLaunchPadDedicatedServer.md.

.PARAMETER FromModConfig
    With -SyncMods: path to the source modconfig.xml. Default
    %USERPROFILE%\Documents\My Games\Stationeers\modconfig.xml.

.PARAMETER Lock
    Acquire the RIG session lock for this whole test session (it spans many
    start/stop cycles). One lock covers both TestRig halves, so the owner id it
    prints is also the id client-rig.ps1 expects. Requires -Purpose. Pair with
    -WaitSeconds N to queue instead of failing when another session holds it.
    Rules: TestRig/session.lock.template.

.PARAMETER RefreshLock
    Bump the lock timer while actively driving a test. Requires -As.

.PARAMETER Unlock
    Release the rig session lock. Requires -As, or human-authorized -BreakLock.
    Refuses while a client-rig listen-host instance is still live; -Force
    overrides that one refusal.

.PARAMETER Force
    Override a refusal inside your OWN session. Today that is exactly one thing:
    -Unlock -Force releases while a listen-host instance is still running.
    -Force never breaks another session's lock; that is -BreakLock.

.PARAMETER Purpose
    With -Lock: short human-readable reason, e.g. "Playtesting network paint for
    SprayPaintPlus". Shown to the user when another session is blocked.

.PARAMETER As
    The owner id printed by -Lock. Pass it on every mutating command so the
    launcher knows the command comes from the lock holder.

.PARAMETER BreakLock
    Break a LIVE lock held by another session (with -Lock / -Unlock / -Stop).
    Agents may use this ONLY when the user explicitly authorizes it. Deliberately
    not called -Force: on client-rig.ps1 -Force is the routine "rebuild my own
    instance" override, and one flag name cannot mean both.

.PARAMETER TtlMinutes
    With -Lock / -RefreshLock: inactivity window before the lock timer lapses.
    Default 10. A busy rig (a player connected to the running server, or a live
    client-rig instance) keeps the lock live regardless of the timer.

.PARAMETER Release
    With -Stop: also release the session lock after stopping (when it is yours,
    already dead, or you were authorized to -Force).

.PARAMETER HostMode
    Internal: run as the host wrapper. Spawned by -Start via .NET
    ProcessStartInfo with CreateNoWindow set, so no console window is allocated
    and no foreground focus is claimed. Do not invoke directly.
#>
[CmdletBinding()]
param(
    [switch] $Bootstrap,

    [switch] $DeployMods,
    [string] $Mod,
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    [switch] $Start,
    [string] $Load,
    [string] $Map,
    [string] $New,
    [int]    $GamePort   = 28016,
    [int]    $UpdatePort = 28015,

    [switch] $Stop,
    [string] $SaveAs,
    [int]    $TimeoutSeconds = 30,
    [switch] $Release,

    [switch] $SendCommand,
    [string] $Command,

    [switch] $Save,
    [string] $Name,
    [int]    $WaitSeconds = 30,

    [switch] $Status,

    [switch] $Logs,
    [int]    $Tail = 50,
    [string] $Grep,

    [switch] $SyncMods,
    [string] $FromModConfig,

    [switch] $Lock,
    [switch] $RefreshLock,
    [switch] $Unlock,
    [string] $Purpose,
    [string] $As,
    [switch] $BreakLock,
    [switch] $Force,
    [int]    $TtlMinutes = 10,

    [switch] $HostMode
)

$ErrorActionPreference = 'Stop'

# A function's own $PSBoundParameters is its own, and it is EMPTY for a function
# declared without a param block: it does not fall through to the script's. So
# "was this switch actually passed, or is it sitting at its default" has to be
# captured here, at script scope, under a name a function can read. Getting this
# wrong is silent: -RefreshLock -TtlMinutes 20 used to test the function's empty
# dictionary and never applied the new TTL at all.
$InvokedWith = $PSBoundParameters

$ServerRoot    = $PSScriptRoot
# <repo>/TestRig/DedicatedServer -> <repo>/TestRig -> <repo>
$TestRigRoot   = Split-Path -Parent $ServerRoot
$RepoRoot      = Split-Path -Parent $TestRigRoot
$InstallDir    = Join-Path $ServerRoot 'install'
$DataDir       = Join-Path $ServerRoot 'data'
$ServerExe     = Join-Path $InstallDir 'rocketstation_DedicatedServer.exe'
$BuildPropsXml = Join-Path $RepoRoot 'Directory.Build.props'

$LogFile       = Join-Path $DataDir 'server.log'
$ControlFile   = Join-Path $DataDir 'control.cmd'
$ServerPidFile = Join-Path $DataDir 'server.pid'
$HostPidFile   = Join-Path $DataDir 'host.pid'

# The session lock is rig-wide (it covers TestRig/ClientRig/ too) and its whole
# mechanism lives in one shared file, so the two halves cannot drift apart on
# the timer, ownership or force-break rules. Rules: TestRig/session.lock.template.
$RigLockLib = Join-Path $TestRigRoot 'rig-lock.ps1'
if (-not (Test-Path $RigLockLib)) {
    throw "Shared rig-lock implementation not found at $RigLockLib. It is committed alongside this launcher; restore it before driving the rig."
}
. $RigLockLib

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
    $managedDll = Join-Path $path 'rocketstation_Data\Managed\Assembly-CSharp.dll'
    if (-not (Test-Path $managedDll)) {
        throw "<StationeersPath>=$path does not contain rocketstation_Data\Managed\Assembly-CSharp.dll. Verify the path. See DEV.md."
    }
    return $path
}

function Get-SteamcmdPath {
    $p = $env:STEAMCMD_PATH
    if ([string]::IsNullOrWhiteSpace($p)) {
        throw "STEAMCMD_PATH environment variable is not set. Set it to the absolute path of steamcmd.exe. See DEV.md."
    }
    if (-not (Test-Path $p)) {
        throw "STEAMCMD_PATH=$p does not exist. See DEV.md."
    }
    return $p
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

# ---- session lock ---------------------------------------------------------
# Mechanism and rules: TestRig/session.lock.template (single source of truth);
# implementation: TestRig/rig-lock.ps1, dot-sourced above and shared with
# client-rig.ps1. The lock is RIG-WIDE: it spans a whole test session (many
# start/stop cycles) and covers the client rig too, so a second agent cannot
# stomp the shared install or the shared per-user Unity state. Liveness = timer
# fresh OR the rig is busy (a player connected here, or a live client instance).
#
# Only the dedi-specific adapters live here. Everything else (Read-RigLock,
# Write-RigLock, the timer, ownership, the break-lock gate) is in the library.

function Assert-MutatingAllowed {
    # Gate for every mutating action except -Stop (which has its own gate).
    param([Parameter(Mandatory)] [string] $Action)
    Assert-RigLockHeld -Action $Action -CallerId $As -Tool 'dedicated-server.ps1'
}

function Get-ConnectedPlayerCount {
    # Currently-connected client count for the live server. The 'clients' /
    # 'status' console commands write to the in-game console, not the Unity
    # -logFile, so they cannot be scraped; the connection lifecycle IS logged,
    # so we scan server.log via Measure-PlayersInLog (library). Reads the log
    # directly: no stdin round-trip, no dependence on the host wrapper,
    # unaffected by the no-client simulation pause. Returns 0 when the server is
    # not running (favours freeing the rig, per session.lock.template).
    if (-not (Test-PidAlive (Get-PidFromFile $ServerPidFile))) { return 0 }
    return (Measure-PlayersInLog $LogFile)
}

# ---- bootstrap ------------------------------------------------------------

function Invoke-Bootstrap {
    Assert-MutatingAllowed -Action 'Bootstrap'
    Write-Host "[Bootstrap] Verifying environment..."
    $stationeers = Get-StationeersPath
    $steamcmd    = Get-SteamcmdPath
    Write-Host "[Bootstrap]   StationeersPath: $stationeers"
    Write-Host "[Bootstrap]   STEAMCMD_PATH:   $steamcmd"
    Write-Host "[Bootstrap]   Server install:  $InstallDir"
    Write-Host "[Bootstrap]   Server data:     $DataDir"

    foreach ($dir in @($InstallDir, $DataDir)) {
        if (-not (Test-Path $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
    }

    Write-Host "[Bootstrap] Running SteamCMD (app 600760)..."
    & $steamcmd `
        +force_install_dir $InstallDir `
        +login anonymous `
        +app_update 600760 validate `
        +quit
    if ($LASTEXITCODE -ne 0) {
        throw "SteamCMD failed with exit code $LASTEXITCODE."
    }
    if (-not (Test-Path $ServerExe)) {
        throw "Bootstrap: rocketstation_DedicatedServer.exe missing after SteamCMD run."
    }
    Write-Host "[Bootstrap] SteamCMD install complete."

    Write-Host "[Bootstrap] Mirroring BepInEx tree from client install..."
    $srcBepInEx = Join-Path $stationeers 'BepInEx'
    $dstBepInEx = Join-Path $InstallDir 'BepInEx'
    if (-not (Test-Path $srcBepInEx)) {
        throw "Client BepInEx not found at $srcBepInEx. Install StationeersLaunchPad on the client first."
    }
    if (Test-Path $dstBepInEx) {
        Remove-Item -Recurse -Force $dstBepInEx
    }
    Copy-Item -Recurse -Path $srcBepInEx -Destination $dstBepInEx

    foreach ($f in @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'changelog.txt')) {
        $src = Join-Path $stationeers $f
        if (Test-Path $src) {
            Copy-Item -Path $src -Destination (Join-Path $InstallDir $f) -Force
        }
    }

    $bepInExDll = Join-Path $dstBepInEx 'core\BepInEx.dll'
    if (Test-Path $bepInExDll) {
        $version = (Get-Item $bepInExDll).VersionInfo.FileVersion
        Write-Host "[Bootstrap] BepInEx mirrored, version $version."
    }

    # Overlay the StationeersLaunchPad server-zip release. Adds RG.ImGui.dll
    # which is in the server zip but not the client install. Other DLLs are
    # byte-identical so the overlay is a no-op for them.
    Write-Host "[Bootstrap] Overlaying StationeersLaunchPad server-zip release..."
    $launchPadVersion = $null
    $launchPadDll     = Join-Path $dstBepInEx 'plugins\StationeersLaunchPad\StationeersLaunchPad.dll'
    if (Test-Path $launchPadDll) {
        $launchPadVersion = (Get-Item $launchPadDll).VersionInfo.ProductVersion
    }
    if (-not $launchPadVersion) {
        Write-Warning "[Bootstrap] StationeersLaunchPad.dll not found at $launchPadDll; skipping server-zip overlay. Mods will not load until StationeersLaunchPad is installed."
    }
    else {
        $launchPadReleaseUrl = "https://github.com/StationeersLaunchPad/StationeersLaunchPad/releases/download/v$launchPadVersion/StationeersLaunchPad-server-v$launchPadVersion.zip"
        $launchPadZipDir     = Join-Path $RepoRoot ".work\launchpad-server"
        $launchPadZipPath    = Join-Path $launchPadZipDir "StationeersLaunchPad-server-v$launchPadVersion.zip"
        $launchPadExtractDir = Join-Path $launchPadZipDir "extracted-v$launchPadVersion"
        if (-not (Test-Path $launchPadZipDir)) { New-Item -ItemType Directory -Path $launchPadZipDir -Force | Out-Null }
        if (-not (Test-Path $launchPadZipPath)) {
            Write-Host "[Bootstrap]   downloading $launchPadReleaseUrl"
            try {
                Invoke-WebRequest -Uri $launchPadReleaseUrl -OutFile $launchPadZipPath -UseBasicParsing
            }
            catch {
                Write-Warning "[Bootstrap]   download failed: $_. Skipping overlay; mod loading may be missing RG.ImGui."
                $launchPadZipPath = $null
            }
        }
        if ($launchPadZipPath -and (Test-Path $launchPadZipPath)) {
            if (Test-Path $launchPadExtractDir) { Remove-Item -Recurse -Force $launchPadExtractDir }
            Expand-Archive -Path $launchPadZipPath -DestinationPath $launchPadExtractDir -Force
            $srcDir = Join-Path $launchPadExtractDir "StationeersLaunchPad"
            $dstDir = Split-Path -Parent $launchPadDll
            foreach ($f in (Get-ChildItem -File -Path $srcDir)) {
                Copy-Item -Path $f.FullName -Destination (Join-Path $dstDir $f.Name) -Force
            }
            Write-Host "[Bootstrap]   overlaid $((Get-ChildItem -File -Path $srcDir).Count) files from server zip into $dstDir"
        }
    }

    Write-Host "[Bootstrap] Done. Next: TestRig/DedicatedServer/dedicated-server.ps1 -SyncMods, then -DeployMods, then -Start."
}

# ---- deploy mods ----------------------------------------------------------

function Add-ModConfigLocalEntry {
    # Idempotently ensure install/modconfig.xml has a <Local Enabled="true">
    # entry pointing at $LocalModDir. If the file is missing, bootstrap a fresh
    # one with just Core + this entry. Returns $true when an entry was added,
    # $false when the entry was already present.
    param([Parameter(Mandatory)] [string] $LocalModDir)
    $configPath = Join-Path $InstallDir 'modconfig.xml'
    if (-not (Test-Path $configPath)) {
        $fresh = @"
<?xml version="1.0" encoding="utf-8"?>
<ModConfig xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <Core Enabled="true">
    <Path />
  </Core>
  <Local Enabled="true">
    <Path Value="$LocalModDir" />
  </Local>
</ModConfig>
"@
        Set-Content -Path $configPath -Value $fresh -Encoding utf8
        return $true
    }
    $content = Get-Content -Raw $configPath
    $needle = "Path Value=`"$LocalModDir`""
    if ($content.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return $false
    }
    $block = "  <Local Enabled=`"true`">`r`n    <Path Value=`"$LocalModDir`" />`r`n  </Local>`r`n"
    $newContent = $content.Replace('</ModConfig>', "$block</ModConfig>")
    Set-Content -Path $configPath -Value $newContent -Encoding utf8 -NoNewline
    return $true
}

function Deploy-DevPluginMirror {
    # Deploy a TestRig/DedicatedServer/dev-plugins/<X>/ target by mirroring it into the
    # StationeersLaunchPad load path (data/mods/Local_<X>/) and ensuring the
    # modconfig.xml has a matching <Local> entry. Defensively removes any stale
    # install/BepInEx/plugins/<X>/<X>.dll left over from a pre-mirror layout, so
    # the duplicate-load trap documented in TestRig/DedicatedServer/CLAUDE.md cannot
    # fire even on a repo that was previously deployed the other way.
    param(
        [Parameter(Mandatory)] [string] $ModDir,
        [Parameter(Mandatory)] [string] $ModName,
        [Parameter(Mandatory)] [string] $Configuration
    )
    $dllSrc = Join-Path $ModDir "$ModName\bin\$Configuration\$ModName.dll"
    if (-not (Test-Path $dllSrc)) {
        Write-Warning "[$ModName] $Configuration build not found at $dllSrc. Skipping."
        return $false
    }
    $aboutSrc    = Join-Path $ModDir "$ModName\About"
    $localModDir = Join-Path $DataDir "mods\Local_$ModName"
    if (-not (Test-Path $localModDir)) {
        New-Item -ItemType Directory -Path $localModDir -Force | Out-Null
    }
    # Mirror About/ (StationeersLaunchPad keys mods off Local_<X>/About/About.xml).
    if (Test-Path $aboutSrc) {
        $aboutDst = Join-Path $localModDir 'About'
        if (Test-Path $aboutDst) { Remove-Item -Recurse -Force $aboutDst }
        Copy-Item -Recurse -Path $aboutSrc -Destination $localModDir
    }
    else {
        Write-Warning "[$ModName] no About/ folder at $aboutSrc; StationeersLaunchPad may not load this plugin without About.xml."
    }
    # Copy the DLL into the local mod folder.
    Copy-Item -Path $dllSrc -Destination (Join-Path $localModDir "$ModName.dll") -Force

    # Defensively remove a stale install/BepInEx/plugins/<X>/ copy. The
    # dev-plugin path is data/mods/Local_<X>/ only; the duplicate would
    # double every Harmony patch.
    $bepInExPluginDll = Join-Path $InstallDir "BepInEx\plugins\$ModName\$ModName.dll"
    if (Test-Path $bepInExPluginDll) {
        Remove-Item -Force $bepInExPluginDll
        Write-Host "[DeployMods] ${ModName}: removed stale duplicate at install/BepInEx/plugins/$ModName/$ModName.dll"
    }

    $added = Add-ModConfigLocalEntry -LocalModDir $localModDir
    if ($added) {
        Write-Host "[DeployMods] ${ModName}: added modconfig.xml Local entry -> $localModDir"
    }
    Write-Host "[DeployMods] $ModName -> $localModDir (dev-plugin)"
    return $true
}

function Invoke-DeployMods {
    Assert-MutatingAllowed -Action 'DeployMods'
    if (-not (Test-Path $ServerExe)) {
        throw "Server not bootstrapped. Run -Bootstrap first."
    }
    $existingServer = Get-PidFromFile $ServerPidFile
    $existingHost   = Get-PidFromFile $HostPidFile
    if ((Test-PidAlive $existingServer) -or (Test-PidAlive $existingHost)) {
        throw "Server is running (host PID $existingHost, server PID $existingServer). The Mono runtime holds an exclusive lock on every loaded plugin DLL on Windows; -DeployMods will fail with a sharing violation, or worse, leave a half-written DLL the next -Start picks up as broken plugin bytes. Run -Stop first."
    }
    $modsRoot       = Join-Path $RepoRoot 'Mods'
    $plansRoot      = Join-Path $RepoRoot 'Plans'
    $devPluginsRoot = Join-Path $RepoRoot 'TestRig\DedicatedServer\dev-plugins'
    if (-not (Test-Path $modsRoot)) {
        throw "Mods/ directory not found at repo root."
    }

    if ($Mod) {
        # Explicit -Mod: accept Mods/<name>/, Plans/<name>/, or
        # TestRig/DedicatedServer/dev-plugins/<name>/. Mods/ wins on a tie. Plans/ and
        # dev-plugins/ entries are work-in-progress / dev-only tooling (e.g.
        # ScenarioRunner); they are not auto-deployed when -Mod is omitted.
        $candidate = Join-Path $modsRoot $Mod
        if (-not (Test-Path $candidate)) {
            $candidate = Join-Path $plansRoot $Mod
        }
        if (-not (Test-Path $candidate)) {
            $candidate = Join-Path $devPluginsRoot $Mod
        }
        if (-not (Test-Path $candidate)) {
            throw "Mod folder not found under Mods/$Mod, Plans/$Mod, or TestRig/DedicatedServer/dev-plugins/$Mod."
        }
        $targets = @($candidate)
    }
    else {
        $targets = Get-ChildItem -Directory -Path $modsRoot |
            Where-Object { $_.Name -ne 'Template' } |
            ForEach-Object { $_.FullName }
    }

    $serverPlugins  = Join-Path $InstallDir 'BepInEx\plugins'
    $devPluginsRootNorm = (Resolve-Path $devPluginsRoot -ErrorAction SilentlyContinue)?.Path
    $deployed = 0
    $skipped  = 0
    foreach ($modDir in $targets) {
        $modName = Split-Path -Leaf $modDir
        # Dev-plugins go to data/mods/Local_<X>/ with a modconfig entry.
        # Mods/ and Plans/ go to install/BepInEx/plugins/<X>/<X>.dll as before.
        $isDevPlugin = $false
        if ($devPluginsRootNorm) {
            $modDirNorm = (Resolve-Path $modDir).Path
            $isDevPlugin = $modDirNorm.StartsWith($devPluginsRootNorm, [StringComparison]::OrdinalIgnoreCase)
        }
        if ($isDevPlugin) {
            if (Deploy-DevPluginMirror -ModDir $modDir -ModName $modName -Configuration $Configuration) {
                $deployed++
            }
            else {
                $skipped++
            }
            continue
        }
        $dllPath = Join-Path $modDir "$modName\bin\$Configuration\$modName.dll"
        if (-not (Test-Path $dllPath)) {
            Write-Warning "[$modName] $Configuration build not found at $dllPath. Skipping."
            $skipped++
            continue
        }
        $dstDir = Join-Path $serverPlugins $modName
        if (-not (Test-Path $dstDir)) {
            New-Item -ItemType Directory -Path $dstDir -Force | Out-Null
        }
        Copy-Item -Path $dllPath -Destination $dstDir -Force
        Write-Host "[DeployMods] $modName -> $dstDir"
        $deployed++
    }
    Write-Host "[DeployMods] $deployed deployed, $skipped skipped."
}

# ---- start (detached) -----------------------------------------------------

function Invoke-Start {
    Assert-MutatingAllowed -Action 'Start'
    if (-not (Test-Path $ServerExe)) {
        throw "Server not bootstrapped. Run -Bootstrap first."
    }
    if (-not (Test-Path $DataDir)) {
        New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
    }
    if ($Load -and $New) { throw "Specify either -Load or -New, not both." }
    if (-not $Load -and -not $New) { throw "Specify -Load <SaveName> -Map <Map> or -New <Map>." }
    if ($Load -and -not $Map) { throw "-Load requires -Map <Map>." }
    if ($Load) {
        $saveDir = Join-Path $DataDir "saves\$Load"
        if (-not (Test-Path $saveDir)) {
            throw "Save '$Load' not found at $saveDir. The developer is the sole save manager; ask them to provide it, or use -New <Map>."
        }
    }

    $existingHost   = Get-PidFromFile $HostPidFile
    $existingServer = Get-PidFromFile $ServerPidFile
    if ((Test-PidAlive $existingHost) -or (Test-PidAlive $existingServer)) {
        throw "Server is already running (host PID $existingHost, server PID $existingServer). Run -Stop first or check -Status."
    }
    foreach ($f in @($HostPidFile, $ServerPidFile, $ControlFile)) {
        Remove-Item -Force $f -ErrorAction SilentlyContinue
    }

    $pwsh = (Get-Process -Id $PID).Path
    $wrapperArgs = @('-NoProfile', '-NonInteractive', '-File', $PSCommandPath, '-HostMode',
                     '-GamePort', $GamePort, '-UpdatePort', $UpdatePort)
    if ($Load) { $wrapperArgs += @('-Load', $Load, '-Map', $Map) }
    else       { $wrapperArgs += @('-New', $New) }

    # CreateNoWindow skips conhost allocation entirely; Start-Process -WindowStyle Hidden flashes a brief focus claim on Win10/11.
    $wrapperArgString = ($wrapperArgs | ForEach-Object {
        if ($_ -match '\s|"') { '"' + ($_ -replace '"', '\"') + '"' } else { "$_" }
    }) -join ' '
    $hostPsi = [System.Diagnostics.ProcessStartInfo]::new()
    $hostPsi.FileName         = $pwsh
    $hostPsi.Arguments        = $wrapperArgString
    $hostPsi.UseShellExecute  = $false
    $hostPsi.CreateNoWindow   = $true
    $hostPsi.WorkingDirectory = $ServerRoot
    $hostProc = [System.Diagnostics.Process]::Start($hostPsi)
    Set-Content -Path $HostPidFile -Value $hostProc.Id

    Write-Host "[Start] Host wrapper launched (PID $($hostProc.Id))."
    Write-Host "[Start] Waiting for server process to register..."

    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        $serverPidVal = Get-PidFromFile $ServerPidFile
        if ((Test-PidAlive $serverPidVal)) {
            Write-Host "[Start] Server PID $serverPidVal."
            Write-Host "[Start] Log:    $LogFile"
            Write-Host "[Start] Use -Status / -Logs / -Save / -SendCommand / -Stop to control."
            return
        }
        if (-not (Test-PidAlive $hostProc.Id)) {
            throw "Host wrapper exited before the server registered. Inspect $LogFile."
        }
        Start-Sleep -Milliseconds 500
    }
    throw "Server did not register within 20 seconds. Inspect $LogFile and run -Status."
}

# ---- host wrapper (internal) ---------------------------------------------

function Invoke-HostMode {
    if ($Load -and -not $Map) { throw "[HostMode] -Load requires -Map." }
    if (-not $Load -and -not $New) { throw "[HostMode] missing -Load or -New." }

    $settingPath = Join-Path $DataDir 'setting.xml'
    $serverArgs = @(
        '-batchmode'
        '-nographics'
        '-settingspath', $settingPath
        '-logFile',      $LogFile
        '-settings', 'SavePath',         $DataDir
        '-settings', 'GamePort',         "$GamePort"
        '-settings', 'UpdatePort',       "$UpdatePort"
        '-settings', 'LocalIpAddress',   '127.0.0.1'
        '-settings', 'AutoSave',         'true'
        '-settings', 'AutoPauseServer',  'false'
        '-settings', 'UPNPEnabled',      'false'
        '-settings', 'ServerName',       'Local Test'
        '-settings', 'ServerMaxPlayers', '4'
        '-settings', 'ServerAuthSecret', 'x'
    )
    if ($Load) { $serverArgs += @('-load', $Load, $Map) }
    else       { $serverArgs += @('-new', $New) }

    $argString = ($serverArgs | ForEach-Object {
        if ($_ -match '\s|"') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
    }) -join ' '

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName               = $ServerExe
    $psi.Arguments              = $argString
    $psi.RedirectStandardInput  = $true
    $psi.UseShellExecute        = $false
    $psi.WorkingDirectory       = $InstallDir
    $psi.CreateNoWindow         = $true

    $proc = [System.Diagnostics.Process]::Start($psi)
    Set-Content -Path $ServerPidFile -Value $proc.Id

    try {
        while (-not $proc.HasExited) {
            if (Test-Path $ControlFile) {
                # Brief settle so we don't read mid-write (writer uses atomic rename, but be defensive).
                Start-Sleep -Milliseconds 50
                try {
                    $cmd = (Get-Content -Raw -ErrorAction Stop $ControlFile).Trim()
                    Remove-Item -Force -ErrorAction Stop $ControlFile
                    if ($cmd) {
                        $proc.StandardInput.WriteLine($cmd)
                        $proc.StandardInput.Flush()
                    }
                }
                catch {
                    # File locked or already gone; retry next tick.
                }
            }
            Start-Sleep -Milliseconds 250
        }
    }
    finally {
        try { $proc.StandardInput.Close() } catch { }
        Remove-Item -Force -ErrorAction SilentlyContinue $ServerPidFile
        Remove-Item -Force -ErrorAction SilentlyContinue $HostPidFile
        Remove-Item -Force -ErrorAction SilentlyContinue $ControlFile
    }
}

# ---- send command ---------------------------------------------------------

function Send-ServerCommand {
    param(
        [Parameter(Mandatory)] [string] $Cmd,
        [int] $WaitForFreeSeconds = 5
    )
    $serverPidVal = Get-PidFromFile $ServerPidFile
    if (-not (Test-PidAlive $serverPidVal)) {
        throw "Server is not running."
    }
    $hostPidVal = Get-PidFromFile $HostPidFile
    if (-not (Test-PidAlive $hostPidVal)) {
        throw "Host wrapper is not running; cannot relay commands. Use -Stop to clean up the orphaned server."
    }

    $deadline = (Get-Date).AddSeconds($WaitForFreeSeconds)
    while ((Test-Path $ControlFile) -and ((Get-Date) -lt $deadline)) {
        Start-Sleep -Milliseconds 100
    }
    if (Test-Path $ControlFile) {
        throw "Previous control command still pending after ${WaitForFreeSeconds}s."
    }

    $tmpFile = "$ControlFile.tmp"
    Set-Content -Path $tmpFile -Value $Cmd -NoNewline
    Move-Item -Path $tmpFile -Destination $ControlFile -Force
}

function Invoke-SendCommand {
    Assert-MutatingAllowed -Action 'SendCommand'
    if (-not $Command) { throw "-SendCommand requires -Command <text>." }
    Send-ServerCommand -Cmd $Command
    Write-Host "[SendCommand] Queued: $Command"
}

# ---- save ----------------------------------------------------------------

function Wait-LogPattern {
    param(
        [Parameter(Mandatory)] [string] $Pattern,
        [int] $TimeoutSec = 30
    )
    if (-not (Test-Path $LogFile)) { return $false }
    $startLen = (Get-Item $LogFile).Length
    $deadline = (Get-Date).AddSeconds($TimeoutSec)

    while ((Get-Date) -lt $deadline) {
        $currentLen = (Get-Item $LogFile).Length
        if ($currentLen -gt $startLen) {
            $stream = [System.IO.File]::Open($LogFile, 'Open', 'Read', 'ReadWrite')
            try {
                $stream.Seek($startLen, 'Begin') | Out-Null
                $reader = [System.IO.StreamReader]::new($stream)
                $newContent = $reader.ReadToEnd()
                $reader.Close()
                if ($newContent -match $Pattern) {
                    return $true
                }
            }
            finally {
                $stream.Close()
            }
        }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Invoke-Save {
    Assert-MutatingAllowed -Action 'Save'
    if (-not $Name) { throw "-Save requires -Name <SaveName>." }
    Send-ServerCommand -Cmd ('save "{0}"' -f $Name)
    Write-Host "[Save] Queued save '$Name'. Waiting for confirmation (up to ${WaitSeconds}s)..."
    $confirmed = Wait-LogPattern -Pattern ("Saved.*" + [regex]::Escape($Name)) -TimeoutSec $WaitSeconds
    if ($confirmed) {
        Write-Host "[Save] Confirmed."
    }
    else {
        Write-Warning "[Save] No 'Saved $Name' line in log within ${WaitSeconds}s. Save may have completed silently or failed; inspect -Logs."
    }
}

# ---- stop ----------------------------------------------------------------

function Stop-ServerProcesses {
    # Tear down server + host wrapper and clean pid/control files. Does NOT
    # touch the session lock. Used by -Stop and by -Lock reclaim of a dead lock.
    $serverPidVal = Get-PidFromFile $ServerPidFile
    $hostPidVal   = Get-PidFromFile $HostPidFile

    if ((Test-PidAlive $serverPidVal) -and (Test-PidAlive $hostPidVal)) {
        Write-Host "[Stop] Sending 'quit' via host wrapper..."
        try { Send-ServerCommand -Cmd 'quit' } catch { Write-Warning "[Stop] $_" }

        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while ((Get-Date) -lt $deadline) {
            if (-not (Test-PidAlive $serverPidVal)) { break }
            Start-Sleep -Milliseconds 500
        }
    }

    if (Test-PidAlive $serverPidVal) {
        Write-Warning "[Stop] Server still alive after ${TimeoutSeconds}s; force-killing."
        Stop-Process -Id $serverPidVal -Force -ErrorAction SilentlyContinue
    }
    if (Test-PidAlive $hostPidVal) {
        Stop-Process -Id $hostPidVal -Force -ErrorAction SilentlyContinue
    }

    foreach ($f in @($HostPidFile, $ServerPidFile, $ControlFile)) {
        Remove-Item -Force $f -ErrorAction SilentlyContinue
    }
}

function Invoke-Stop {
    # -Stop is allowed unless a LIVE foreign lock exists (so orphan / expired
    # cleanup needs no ceremony). It does not require -As. -Release also frees
    # the lock when it is yours, already dead, or you were authorized to
    # -BreakLock.
    #
    # ORDERING DEPENDENCY, DO NOT REORDER. This Get-RigLockState call MUST come
    # before the -Release block at the bottom of this function.
    # Test-RigLockReleasableOnStop has no busy term, so on its own it would
    # release a foreign lock the moment its timer lapsed, even with a test in
    # full flight. What makes that safe is exactly this call happening first: its
    # expired-and-busy branch self-renews the lock and reports LiveForeign, so we
    # throw below and never reach the release. Swap the two and an unrelated
    # -Stop -Release tears the rig out from under a live session.
    # TestRig/rig-lock.tests.ps1 pins both halves of this.
    $st = Get-RigLockState -CallerId $As
    if ($st.State -eq 'LiveForeign') {
        if (-not $BreakLock) {
            throw "[Stop] Refusing to stop a server held by another live session.`n$(Format-ForeignRigLock $st)`nReport to the user. Only the user may authorize -BreakLock. See TestRig/session.lock.template."
        }
        Write-Warning "[Stop] -BreakLock: stopping a server held by another live session ('$($st.Lock['purpose'])')."
    }

    $serverAlive = Test-PidAlive (Get-PidFromFile $ServerPidFile)
    $hostAlive   = Test-PidAlive (Get-PidFromFile $HostPidFile)

    if (-not $serverAlive -and -not $hostAlive) {
        Write-Host "[Stop] Nothing running."
        foreach ($f in @($HostPidFile, $ServerPidFile, $ControlFile)) {
            Remove-Item -Force $f -ErrorAction SilentlyContinue
        }
    }
    else {
        if ($SaveAs -and $serverAlive -and $hostAlive) {
            Write-Host "[Stop] Saving as '$SaveAs' first..."
            try {
                Send-ServerCommand -Cmd ('save "{0}"' -f $SaveAs)
                $confirmed = Wait-LogPattern -Pattern ("Saved.*" + [regex]::Escape($SaveAs)) -TimeoutSec $TimeoutSeconds
                if (-not $confirmed) {
                    Write-Warning "[Stop] No save confirmation within ${TimeoutSeconds}s; continuing with quit."
                }
            }
            catch {
                Write-Warning "[Stop] Save failed: $_"
            }
        }
        elseif ($SaveAs) {
            Write-Warning "[Stop] -SaveAs ignored: server or host wrapper is not running."
        }
        Stop-ServerProcesses
    }

    if ($Release) {
        # Safe only because of the Get-RigLockState guard at the top of this
        # function; see the ORDERING DEPENDENCY note there before touching this.
        $lock = Read-RigLock
        if (-not $lock) {
            Write-Host "[Stop] No rig session lock to release."
        }
        elseif (Test-RigLockReleasableOnStop -Lock $lock -CallerId $As -BreakLock:$BreakLock) {
            Remove-Item -Force -ErrorAction SilentlyContinue (Get-RigLockFilePath)
            Write-Host "[Stop] Rig session lock released."
        }
        else {
            Write-Warning "[Stop] -Release ignored: lock held by '$($lock['owner'])', not you. Use -Unlock -As <id>, or get user authorization for -BreakLock."
        }
    }
    Write-Host "[Stop] Done."
}

# ---- session lock actions -------------------------------------------------

function Invoke-Lock {
    if (-not $Purpose) {
        throw "-Lock requires -Purpose `"<short reason>`", e.g. -Purpose `"Playtesting network paint for SprayPaintPlus`". See TestRig/session.lock.template."
    }
    # -WaitSeconds means something different per action on this launcher (-Save
    # defaults to 30), so -Lock only queues when the caller actually passed it.
    $lockWait = if ($InvokedWith.ContainsKey('WaitSeconds')) { $WaitSeconds } else { 0 }
    # Reclaiming a DEAD lock while this half still has an orphaned server up is
    # the one dedi-specific step; everything else is the shared implementation.
    # The reclaim runs AFTER the lock is minted, so the teardown happens under our
    # own reservation rather than inside the acquisition critical section.
    New-RigLock -Purpose $Purpose -CallerId $As -TtlMinutes $TtlMinutes -BreakLock:$BreakLock `
        -WaitSeconds $lockWait -Tool 'dedicated-server.ps1' -OnReclaim {
            if (Test-PidAlive (Get-PidFromFile $ServerPidFile)) {
                Write-Warning "[Lock] Reclaimed an expired lock; stopping its orphaned server."
                Stop-ServerProcesses
            }
        } | Out-Null
}

function Invoke-RefreshLock {
    if (-not $As) { throw "-RefreshLock requires -As <id> (the owner id printed by -Lock)." }
    if ($InvokedWith.ContainsKey('TtlMinutes')) { Update-RigLock -CallerId $As -TtlMinutes $TtlMinutes }
    else                                        { Update-RigLock -CallerId $As }
}

function Invoke-Unlock {
    Remove-RigLock -CallerId $As -BreakLock:$BreakLock -Force:$Force
}

# ---- status & logs --------------------------------------------------------

function Invoke-Status {
    $hostPidVal   = Get-PidFromFile $HostPidFile
    $serverPidVal = Get-PidFromFile $ServerPidFile
    $hostAlive    = Test-PidAlive $hostPidVal
    $serverAlive  = Test-PidAlive $serverPidVal

    $hostLine   = if ($hostAlive)   { "running (PID $hostPidVal)" }   else { 'stopped' }
    $serverLine = if ($serverAlive) {
        $sp = Get-Process -Id $serverPidVal -ErrorAction SilentlyContinue
        $up = if ($sp) { ((Get-Date) - $sp.StartTime).ToString('hh\:mm\:ss') } else { '?' }
        "running (PID $serverPidVal, up $up)"
    }
    else { 'stopped' }

    Write-Host "host wrapper: $hostLine"
    Write-Host "server:       $serverLine"

    if (Test-Path $LogFile) {
        $lastLine = Get-Content -Tail 1 $LogFile -ErrorAction SilentlyContinue
        Write-Host "last log:     $lastLine"
    }
    if (Test-Path $ControlFile) {
        $pending = (Get-Content -Raw -ErrorAction SilentlyContinue $ControlFile).Trim()
        Write-Host "pending cmd:  $pending"
    }

    if ($serverAlive) {
        Write-Host "players:      $(Get-ConnectedPlayerCount) connected"
    }
    # The lock is rig-wide, so this block is the shared one; client-rig.ps1
    # -Status prints exactly the same thing.
    Write-RigLockStatus -CallerId $As

    if ($serverAlive -and -not $hostAlive) {
        Write-Warning "Server is alive but host wrapper is gone. Use -Stop to terminate the orphan."
    }
}

function Invoke-Logs {
    if (-not (Test-Path $LogFile)) {
        Write-Host "No log file at $LogFile."
        return
    }
    if ($Grep) {
        Get-Content $LogFile | Select-String -Pattern $Grep
    }
    else {
        Get-Content -Tail $Tail $LogFile
    }
}

# ---- sync mods ------------------------------------------------------------

function Invoke-SyncMods {
    Assert-MutatingAllowed -Action 'SyncMods'
    if (-not (Test-Path $ServerExe)) {
        throw "Server not bootstrapped. Run -Bootstrap first."
    }
    $existingServer = Get-PidFromFile $ServerPidFile
    $existingHost   = Get-PidFromFile $HostPidFile
    if ((Test-PidAlive $existingServer) -or (Test-PidAlive $existingHost)) {
        throw "Server is running (host PID $existingHost, server PID $existingServer). StationeersLaunchPad has the synced mod files open for class scanning; overwriting them while loaded will fail with a sharing violation or leave a half-written tree. Run -Stop first."
    }
    if (-not $FromModConfig) {
        $FromModConfig = Join-Path $env:USERPROFILE "Documents\My Games\Stationeers\modconfig.xml"
    }
    if (-not (Test-Path $FromModConfig)) {
        throw "Source modconfig not found at $FromModConfig. Pass -FromModConfig <path> to override."
    }

    Write-Host "[SyncMods] Source: $FromModConfig"
    $xml = [xml](Get-Content -Raw $FromModConfig)

    # Walk child nodes in document order to preserve load order intent.
    $entries = New-Object System.Collections.Generic.List[hashtable]
    foreach ($node in $xml.ModConfig.ChildNodes) {
        if ($node.NodeType -ne [System.Xml.XmlNodeType]::Element) { continue }
        if ($node.Enabled -ne 'true') { continue }
        switch ($node.LocalName) {
            'Core' {
                # Core is implicit; we always emit a Core entry in the output. Skip here.
            }
            'Workshop' {
                $srcPath = $node.Path.Value
                $wid     = $node.WorkshopId.Value
                if (-not $wid) { Write-Warning "[SyncMods] Workshop entry without WorkshopId; using basename of $srcPath"; $wid = Split-Path -Leaf $srcPath }
                $entries.Add(@{
                    Source   = $srcPath
                    DestName = "Workshop_$wid"
                    Type     = 'Workshop'
                })
            }
            'Local' {
                $srcPath  = $node.Path.Value
                if (-not $srcPath) { continue }
                $dirName  = Split-Path -Leaf $srcPath
                $entries.Add(@{
                    Source   = $srcPath
                    DestName = "Local_$dirName"
                    Type     = 'Local'
                })
            }
            default { Write-Warning "[SyncMods] Unknown modconfig entry type '$($node.LocalName)'; ignoring" }
        }
    }

    # Local mods are scanned from <SavePath>/mods/ (= <DataDir>/mods/), NOT <install>/mods/.
    # See Research/Workflows/StationeersLaunchPadDedicatedServer.md for the resolution.
    $modsDir = Join-Path $DataDir 'mods'
    if (Test-Path $modsDir) {
        Write-Host "[SyncMods] Wiping $modsDir"
        Remove-Item -Recurse -Force $modsDir
    }
    New-Item -ItemType Directory -Path $modsDir -Force | Out-Null

    $copied  = 0
    $skipped = 0
    foreach ($e in $entries) {
        if (-not (Test-Path $e.Source)) {
            Write-Warning "[SyncMods] [$($e.DestName)] source missing: $($e.Source) (skipping)"
            $skipped++
            continue
        }
        $dest = Join-Path $modsDir $e.DestName
        Copy-Item -Recurse -Path $e.Source -Destination $dest
        Write-Host "[SyncMods] $($e.DestName) <- $($e.Source)"
        $copied++
    }

    # Write the baked modconfig.xml at <install>/modconfig.xml.
    $configPath = Join-Path $InstallDir 'modconfig.xml'
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
    [void]$sb.AppendLine('<ModConfig xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">')
    [void]$sb.AppendLine('  <Core Enabled="true">')
    [void]$sb.AppendLine('    <Path />')
    [void]$sb.AppendLine('  </Core>')
    foreach ($e in $entries) {
        if (-not (Test-Path (Join-Path $modsDir $e.DestName))) { continue }   # don't write entries for missing sources
        [void]$sb.AppendLine('  <Local Enabled="true">')
        [void]$sb.AppendLine("    <Path Value=`"$($e.DestName)`" />")
        [void]$sb.AppendLine('  </Local>')
    }
    [void]$sb.AppendLine('</ModConfig>')
    Set-Content -Path $configPath -Value $sb.ToString() -Encoding utf8

    Write-Host "[SyncMods] Wrote $configPath with $copied Local entries (Core + $copied)."
    Write-Host "[SyncMods] $copied copied, $skipped skipped (missing source)."
}

# ---- dispatch -------------------------------------------------------------

if ($HostMode)    { Invoke-HostMode;    return }
if ($Lock)        { Invoke-Lock;        return }
if ($RefreshLock) { Invoke-RefreshLock; return }
if ($Unlock)      { Invoke-Unlock;      return }
if ($Bootstrap)   { Invoke-Bootstrap;   return }
if ($DeployMods)  { Invoke-DeployMods;  return }
if ($SyncMods)    { Invoke-SyncMods;    return }
if ($Start)       { Invoke-Start;       return }
if ($Stop)        { Invoke-Stop;        return }
if ($SendCommand) { Invoke-SendCommand; return }
if ($Save)        { Invoke-Save;        return }
if ($Status)      { Invoke-Status;      return }
if ($Logs)        { Invoke-Logs;        return }

Write-Host @"
Stationeers Dedicated Server launcher.

Rig conventions:    TestRig/CLAUDE.md
Operations manual:  TestRig/DedicatedServer/CLAUDE.md
Session-lock rules: TestRig/session.lock.template (READ FIRST)

Session lock (acquire before ANY mutating command; pass -As <id> thereafter).
ONE lock covers BOTH halves, so this id is also what client-rig.ps1 expects:
  TestRig/DedicatedServer/dedicated-server.ps1 -Lock -Purpose "<what you are testing>" [-TtlMinutes 10] [-WaitSeconds 0]
  TestRig/DedicatedServer/dedicated-server.ps1 -RefreshLock -As <id>      (while actively testing)
  TestRig/DedicatedServer/dedicated-server.ps1 -Unlock -As <id> [-Force]  (release when done)
  -Lock -WaitSeconds N queues for up to N seconds instead of failing at once when another
  session holds the rig. It is a queue, not a reservation: no ordering fairness is promised.
  -Unlock refuses while a client-rig listen-host instance is live; -Force overrides that
  one refusal and nothing else.
  Breaking another session's LIVE lock (-BreakLock) is human-gated: only on the user's say-so.
  -BreakLock is NOT -Force. -Force never breaks a lock on either launcher.

Setup (mutating; needs the lock):
  TestRig/DedicatedServer/dedicated-server.ps1 -Bootstrap -As <id>
  TestRig/DedicatedServer/dedicated-server.ps1 -SyncMods -As <id> [-FromModConfig <path>]
  TestRig/DedicatedServer/dedicated-server.ps1 -DeployMods -As <id> [-Mod <name>] [-Configuration Release|Debug]

Lifecycle (agent-driven, all non-blocking unless noted):
  TestRig/DedicatedServer/dedicated-server.ps1 -Start -As <id> -Load <SaveName> -Map <Map>  [-GamePort N -UpdatePort N]
  TestRig/DedicatedServer/dedicated-server.ps1 -Start -As <id> -New <Map>                    [-GamePort N -UpdatePort N]
  TestRig/DedicatedServer/dedicated-server.ps1 -Status [-As <id>]
  TestRig/DedicatedServer/dedicated-server.ps1 -Logs [-Tail N] [-Grep pattern]
  TestRig/DedicatedServer/dedicated-server.ps1 -Save -As <id> -Name <SaveName>          (waits for log confirmation)
  TestRig/DedicatedServer/dedicated-server.ps1 -SendCommand -As <id> -Command '<text>'
  TestRig/DedicatedServer/dedicated-server.ps1 -Stop -As <id> [-SaveAs <SaveName>] [-Release]   (waits for clean exit)
"@
