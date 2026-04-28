<#
.SYNOPSIS
    Stationeers Dedicated Server launcher (agent-driven lifecycle).

.DESCRIPTION
    Bootstraps, deploys mods to, starts, controls, and stops a self-contained
    Stationeers Dedicated Server install rooted at <repo>/DedicatedServer/.

    -Start launches a detached host wrapper that owns the server process and
    relays commands written to a control file into the server's stdin. The
    launcher returns immediately. Subsequent invocations (-Save, -SendCommand,
    -Stop, -Status, -Logs) coordinate via PID files and the control file under
    DedicatedServer/data/.

    Operating manual: DedicatedServer/CLAUDE.md.
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
    Save name to load (must exist under DedicatedServer/data/saves/).

.PARAMETER Map
    Map id (Moon, Mars, Europa, Vulcan, Loulan, Venus, Mimas, ...).

.PARAMETER New
    Create a new world on the given map.

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
    With -Save: how long to wait for the save confirmation. Default 30.

.PARAMETER Status
    Report whether the host wrapper and server are running, PIDs, uptime,
    and the last log line.

.PARAMETER Logs
    Print the dedicated server log. Pair with -Tail or -Grep.

.PARAMETER Tail
    With -Logs: number of trailing lines to print. Default 50.

.PARAMETER Grep
    With -Logs: filter the log by a regex.

.PARAMETER HostMode
    Internal: run as the host wrapper. Invoked by -Start via Start-Process.
    Do not invoke directly.
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

    [switch] $Stop,
    [string] $SaveAs,
    [int]    $TimeoutSeconds = 30,

    [switch] $SendCommand,
    [string] $Command,

    [switch] $Save,
    [string] $Name,
    [int]    $WaitSeconds = 30,

    [switch] $Status,

    [switch] $Logs,
    [int]    $Tail = 50,
    [string] $Grep,

    [switch] $HostMode
)

$ErrorActionPreference = 'Stop'

$ServerRoot    = $PSScriptRoot
$RepoRoot      = Split-Path -Parent $ServerRoot
$InstallDir    = Join-Path $ServerRoot 'install'
$DataDir       = Join-Path $ServerRoot 'data'
$ServerExe     = Join-Path $InstallDir 'rocketstation_DedicatedServer.exe'
$BuildPropsXml = Join-Path $RepoRoot 'Directory.Build.props'

$LogFile       = Join-Path $DataDir 'server.log'
$ControlFile   = Join-Path $DataDir 'control.cmd'
$ServerPidFile = Join-Path $DataDir 'server.pid'
$HostPidFile   = Join-Path $DataDir 'host.pid'

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

# ---- bootstrap ------------------------------------------------------------

function Invoke-Bootstrap {
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

    Write-Host "[Bootstrap] Done. Next: DedicatedServer/dedicated-server.ps1 -DeployMods, then -Start."
}

# ---- deploy mods ----------------------------------------------------------

function Invoke-DeployMods {
    if (-not (Test-Path $ServerExe)) {
        throw "Server not bootstrapped. Run -Bootstrap first."
    }
    $modsRoot = Join-Path $RepoRoot 'Mods'
    if (-not (Test-Path $modsRoot)) {
        throw "Mods/ directory not found at repo root."
    }

    if ($Mod) {
        $targets = @(Join-Path $modsRoot $Mod)
        if (-not (Test-Path $targets[0])) {
            throw "Mod folder not found: $($targets[0])"
        }
    }
    else {
        $targets = Get-ChildItem -Directory -Path $modsRoot |
            Where-Object { $_.Name -ne 'Template' } |
            ForEach-Object { $_.FullName }
    }

    $serverPlugins = Join-Path $InstallDir 'BepInEx\plugins'
    $deployed = 0
    $skipped  = 0
    foreach ($modDir in $targets) {
        $modName = Split-Path -Leaf $modDir
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
    $wrapperArgs = @('-NoProfile', '-NonInteractive', '-File', $PSCommandPath, '-HostMode')
    if ($Load) { $wrapperArgs += @('-Load', $Load, '-Map', $Map) }
    else       { $wrapperArgs += @('-New', $New) }

    $hostProc = Start-Process `
        -FilePath $pwsh `
        -ArgumentList $wrapperArgs `
        -WindowStyle Hidden `
        -PassThru
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
        '-settings', 'GamePort',         '27016'
        '-settings', 'UpdatePort',       '27015'
        '-settings', 'AutoSave',         'true'
        '-settings', 'UPNPEnabled',      'false'
        '-settings', 'ServerName',       'Local Test'
        '-settings', 'ServerMaxPlayers', '4'
        '-settings', 'ServerPassword',   'x'
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

function Invoke-Stop {
    $serverPidVal = Get-PidFromFile $ServerPidFile
    $hostPidVal   = Get-PidFromFile $HostPidFile
    $serverAlive  = Test-PidAlive $serverPidVal
    $hostAlive    = Test-PidAlive $hostPidVal

    if (-not $serverAlive -and -not $hostAlive) {
        Write-Host "[Stop] Nothing running."
        foreach ($f in @($HostPidFile, $ServerPidFile, $ControlFile)) {
            Remove-Item -Force $f -ErrorAction SilentlyContinue
        }
        return
    }

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

    if ($serverAlive -and $hostAlive) {
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
    Write-Host "[Stop] Done."
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

# ---- dispatch -------------------------------------------------------------

if ($HostMode)    { Invoke-HostMode;    return }
if ($Bootstrap)   { Invoke-Bootstrap;   return }
if ($DeployMods)  { Invoke-DeployMods;  return }
if ($Start)       { Invoke-Start;       return }
if ($Stop)        { Invoke-Stop;        return }
if ($SendCommand) { Invoke-SendCommand; return }
if ($Save)        { Invoke-Save;        return }
if ($Status)      { Invoke-Status;      return }
if ($Logs)        { Invoke-Logs;        return }

Write-Host @"
Stationeers Dedicated Server launcher.

Operations manual: DedicatedServer/CLAUDE.md

Setup:
  DedicatedServer/dedicated-server.ps1 -Bootstrap
  DedicatedServer/dedicated-server.ps1 -DeployMods [-Mod <name>] [-Configuration Release|Debug]

Lifecycle (agent-driven, all non-blocking unless noted):
  DedicatedServer/dedicated-server.ps1 -Start  -Load <SaveName> -Map <Map>
  DedicatedServer/dedicated-server.ps1 -Start  -New <Map>
  DedicatedServer/dedicated-server.ps1 -Status
  DedicatedServer/dedicated-server.ps1 -Logs [-Tail N] [-Grep pattern]
  DedicatedServer/dedicated-server.ps1 -Save -Name <SaveName>           (waits for log confirmation)
  DedicatedServer/dedicated-server.ps1 -SendCommand -Command '<text>'
  DedicatedServer/dedicated-server.ps1 -Stop [-SaveAs <SaveName>]       (waits for clean exit)
"@
