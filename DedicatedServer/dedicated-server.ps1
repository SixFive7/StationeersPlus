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
    Acquire the session lock for this whole test session (it spans many
    start/stop cycles). Requires -Purpose. Prints a short owner id to reuse via
    -As. Rules: DedicatedServer/session.lock.template.

.PARAMETER RefreshLock
    Bump the lock timer while actively driving a test. Requires -As.

.PARAMETER Unlock
    Release the session lock. Requires -As, or human-authorized -Force.

.PARAMETER Purpose
    With -Lock: short human-readable reason, e.g. "Playtesting network paint for
    SprayPaintPlus". Shown to the user when another session is blocked.

.PARAMETER As
    The owner id printed by -Lock. Pass it on every mutating command so the
    launcher knows the command comes from the lock holder.

.PARAMETER Force
    Break a LIVE lock held by another session (with -Lock / -Unlock / -Stop).
    Agents may use this ONLY when the user explicitly authorizes it.

.PARAMETER TtlMinutes
    With -Lock / -RefreshLock: inactivity window before the lock timer lapses.
    Default 10. A running server with a connected player keeps the lock live
    regardless of the timer.

.PARAMETER Release
    With -Stop: also release the session lock after stopping (when it is yours,
    already dead, or you were authorized to -Force).

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
    [switch] $Force,
    [int]    $TtlMinutes = 10,

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

$LockFile       = Join-Path $ServerRoot 'session.lock'
$LockTemplate   = Join-Path $ServerRoot 'session.lock.template'

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
# Mechanism and rules: session.lock.template (single source of truth). The lock
# spans a whole test session (many start/stop cycles) so a second agent cannot
# stomp the shared install. Liveness = timer fresh OR a player connected.

function Get-NowUtc {
    [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ss'Z'")
}

function Read-Lock {
    # Returns an ordered hashtable of lock fields, or $null if no usable lock.
    if (-not (Test-Path $LockFile)) { return $null }
    $fields = [ordered]@{}
    foreach ($line in (Get-Content -ErrorAction SilentlyContinue $LockFile)) {
        $t = $line.Trim()
        if (-not $t -or $t.StartsWith('#')) { continue }
        $eq = $t.IndexOf('=')
        if ($eq -lt 1) { continue }
        $fields[$t.Substring(0, $eq).Trim()] = $t.Substring($eq + 1).Trim()
    }
    if (-not $fields.Contains('owner')) { return $null }
    return $fields
}

function Write-Lock {
    param([Parameter(Mandatory)] $Fields)
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine('# Stationeers Dedicated Server - ACTIVE session lock (auto-managed; do not hand-edit).')
    [void]$sb.AppendLine('# Mechanism and rules: session.lock.template (single source of truth).')
    foreach ($k in $Fields.Keys) {
        [void]$sb.AppendLine("$k=$($Fields[$k])")
    }
    $tmp = "$LockFile.tmp"
    Set-Content -Path $tmp -Value $sb.ToString() -Encoding utf8 -NoNewline
    Move-Item -Path $tmp -Destination $LockFile -Force
}

function Test-LockTimerExpired {
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

function Get-LockAgeText {
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

function Get-ConnectedPlayerCount {
    # Currently-connected client count for the live server. The 'clients' /
    # 'status' console commands write to the in-game console, not the Unity
    # -logFile, so they cannot be scraped; the connection lifecycle IS logged,
    # so we scan server.log via Measure-PlayersInLog. Reads the log directly: no
    # stdin round-trip, no dependence on the host wrapper, unaffected by the
    # no-client simulation pause. Returns 0 when the server is not running
    # (favours freeing the dedi, per session.lock.template).
    if (-not (Test-PidAlive (Get-PidFromFile $ServerPidFile))) { return 0 }
    return (Measure-PlayersInLog $LogFile)
}

function Get-LockState {
    # States: None, Mine, LiveForeign, DeadForeign.
    param([string] $CallerId)
    $lock = Read-Lock
    if (-not $lock) { return [pscustomobject]@{ State = 'None'; Lock = $null; Players = $null } }
    if ($CallerId -and $lock['owner'] -eq $CallerId) {
        return [pscustomobject]@{ State = 'Mine'; Lock = $lock; Players = $null }
    }
    if (-not (Test-LockTimerExpired $lock)) {
        return [pscustomobject]@{ State = 'LiveForeign'; Lock = $lock; Players = $null }
    }
    # Timer expired. Player-aware tie-break only if a server is still running.
    if (-not (Test-PidAlive (Get-PidFromFile $ServerPidFile))) {
        return [pscustomobject]@{ State = 'DeadForeign'; Lock = $lock; Players = 0 }
    }
    $players = Get-ConnectedPlayerCount
    if ($players -ge 1) {
        # A player is connected: an active session self-renews so a brief
        # disconnect still gets a full TTL grace.
        $lock['refreshed_at'] = Get-NowUtc
        Write-Lock $lock
        return [pscustomobject]@{ State = 'LiveForeign'; Lock = $lock; Players = $players }
    }
    return [pscustomobject]@{ State = 'DeadForeign'; Lock = $lock; Players = 0 }
}

function Format-ForeignLock {
    param([Parameter(Mandatory)] $State)
    $lk = $State.Lock
    $players = if ($State.Players) { "; $($State.Players) player(s) connected" } else { '' }
    return "    purpose : $($lk['purpose'])`n    owner   : $($lk['owner'])`n    active  : $(Get-LockAgeText $lk)$players"
}

function Assert-MutatingAllowed {
    # Gate for every mutating action except -Stop (which has its own gate).
    param([Parameter(Mandatory)] [string] $Action)
    $st = Get-LockState -CallerId $As
    switch ($st.State) {
        'Mine' {
            $lk = $st.Lock
            $lk['refreshed_at'] = Get-NowUtc
            Write-Lock $lk
            return
        }
        'None' {
            throw "[$Action] No session lock is held. Acquire one first:`n    dedicated-server.ps1 -Lock -Purpose `"<what you are testing>`"`nthen pass -As <id> on every mutating command. See session.lock.template."
        }
        'DeadForeign' {
            throw "[$Action] No live session lock is held (a previous lock expired). Re-acquire:`n    dedicated-server.ps1 -Lock -Purpose `"<what you are testing>`"`nSee session.lock.template."
        }
        'LiveForeign' {
            throw "[$Action] The dedicated server is locked by another session.`n$(Format-ForeignLock $st)`nDo NOT proceed. Report this purpose to the user and let the user decide. Only the user may authorize -Force. See session.lock.template."
        }
    }
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

    Write-Host "[Bootstrap] Done. Next: DedicatedServer/dedicated-server.ps1 -SyncMods, then -DeployMods, then -Start."
}

# ---- deploy mods ----------------------------------------------------------

function Invoke-DeployMods {
    Assert-MutatingAllowed -Action 'DeployMods'
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
    # the lock when it is yours, already dead, or you were authorized to -Force.
    $st = Get-LockState -CallerId $As
    if ($st.State -eq 'LiveForeign') {
        if (-not $Force) {
            throw "[Stop] Refusing to stop a server held by another live session.`n$(Format-ForeignLock $st)`nReport to the user. Only the user may authorize -Force. See session.lock.template."
        }
        Write-Warning "[Stop] -Force: stopping a server held by another live session ('$($st.Lock['purpose'])')."
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
        $lock = Read-Lock
        if (-not $lock) {
            Write-Host "[Stop] No session lock to release."
        }
        elseif (($As -and $lock['owner'] -eq $As) -or $Force -or (Test-LockTimerExpired $lock)) {
            Remove-Item -Force -ErrorAction SilentlyContinue $LockFile
            Write-Host "[Stop] Session lock released."
        }
        else {
            Write-Warning "[Stop] -Release ignored: lock held by '$($lock['owner'])', not you. Use -Unlock -As <id>, or get user authorization for -Force."
        }
    }
    Write-Host "[Stop] Done."
}

# ---- session lock actions -------------------------------------------------

function Invoke-Lock {
    if (-not $Purpose) {
        throw "-Lock requires -Purpose `"<short reason>`", e.g. -Purpose `"Playtesting network paint for SprayPaintPlus`". See session.lock.template."
    }
    $st = Get-LockState -CallerId $As
    switch ($st.State) {
        'Mine' {
            $owner = $st.Lock['owner']
            Write-Lock ([ordered]@{
                owner = $owner; purpose = $Purpose
                acquired_at = $st.Lock['acquired_at']; refreshed_at = (Get-NowUtc)
                ttl_minutes = $TtlMinutes; host = $env:COMPUTERNAME
            })
            Write-Host "[Lock] Re-asserted session lock (owner $owner). Pass -As $owner on mutating commands."
            return
        }
        'LiveForeign' {
            if (-not $Force) {
                throw "Cannot acquire: the dedicated server is locked by another session.`n$(Format-ForeignLock $st)`nReport this purpose to the user. Only the user may authorize -Force. See session.lock.template."
            }
            Write-Warning "[Lock] -Force: breaking a live lock held by '$($st.Lock['purpose'])' (owner $($st.Lock['owner']))."
        }
        'DeadForeign' {
            if (Test-PidAlive (Get-PidFromFile $ServerPidFile)) {
                Write-Warning "[Lock] Reclaiming an expired lock; stopping its orphaned server first."
                Stop-ServerProcesses
            }
        }
    }
    $owner = [guid]::NewGuid().ToString('N').Substring(0, 8)
    Write-Lock ([ordered]@{
        owner = $owner; purpose = $Purpose
        acquired_at = (Get-NowUtc); refreshed_at = (Get-NowUtc)
        ttl_minutes = $TtlMinutes; host = $env:COMPUTERNAME
    })
    Write-Host "[Lock] Acquired session lock."
    Write-Host "[Lock]   owner   : $owner   (pass -As $owner on every mutating command)"
    Write-Host "[Lock]   purpose : $Purpose"
    Write-Host "[Lock]   ttl     : $TtlMinutes min (refresh with -RefreshLock -As $owner while actively testing)"
    Write-Host "[Lock] Rules: session.lock.template."
}

function Invoke-RefreshLock {
    if (-not $As) { throw "-RefreshLock requires -As <id> (the owner id printed by -Lock)." }
    $lock = Read-Lock
    if (-not $lock) { throw "No session lock to refresh. Acquire one: -Lock -Purpose `"<reason>`"." }
    if ($lock['owner'] -ne $As) {
        throw "Refresh refused: the lock is held by owner '$($lock['owner'])' (purpose: $($lock['purpose'])), not '$As'. Your reservation has lapsed. Report to the user; do not touch the server. See session.lock.template."
    }
    $lock['refreshed_at'] = Get-NowUtc
    if ($PSBoundParameters.ContainsKey('TtlMinutes')) { $lock['ttl_minutes'] = $TtlMinutes }
    Write-Lock $lock
    Write-Host "[RefreshLock] Refreshed (owner $As, ttl $($lock['ttl_minutes']) min)."
}

function Invoke-Unlock {
    $lock = Read-Lock
    if (-not $lock) { Write-Host "[Unlock] No session lock present."; return }
    if (-not ($As -and $lock['owner'] -eq $As) -and -not $Force) {
        throw "Unlock refused: the lock is held by owner '$($lock['owner'])' (purpose: $($lock['purpose'])), not '$As'. Report to the user. Only the user may authorize -Force. See session.lock.template."
    }
    Remove-Item -Force -ErrorAction SilentlyContinue $LockFile
    Write-Host "[Unlock] Session lock released (was owner $($lock['owner']))."
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

    $lock = Read-Lock
    if (-not $lock) {
        Write-Host "session lock: none"
    }
    else {
        $expired = Test-LockTimerExpired $lock
        $own = if ($As -and $lock['owner'] -eq $As) { 'YOURS' }
               elseif ($As) { "held by another session ($($lock['owner']))" }
               else { "owner $($lock['owner'])" }
        Write-Host "session lock: $own"
        Write-Host "  purpose:    $($lock['purpose'])"
        Write-Host "  timer:      $(if ($expired) { 'expired' } else { 'fresh' }); ttl $($lock['ttl_minutes']) min; refreshed $(Get-LockAgeText $lock)"
        if ($serverAlive) {
            $players = Get-ConnectedPlayerCount
            $note = if ($expired) {
                if ($players -ge 1) { '  (lock still LIVE: player connected)' } else { '  (timer expired, no player; reclaimable)' }
            } else { '' }
            Write-Host "  players:    $players connected$note"
        }
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

# ---- sync mods ------------------------------------------------------------

function Invoke-SyncMods {
    Assert-MutatingAllowed -Action 'SyncMods'
    if (-not (Test-Path $ServerExe)) {
        throw "Server not bootstrapped. Run -Bootstrap first."
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

Operations manual:  DedicatedServer/CLAUDE.md
Session-lock rules: DedicatedServer/session.lock.template (READ FIRST)

Session lock (acquire before ANY mutating command; pass -As <id> thereafter):
  DedicatedServer/dedicated-server.ps1 -Lock -Purpose "<what you are testing>" [-TtlMinutes 10]
  DedicatedServer/dedicated-server.ps1 -RefreshLock -As <id>      (while actively testing)
  DedicatedServer/dedicated-server.ps1 -Unlock -As <id>           (release when done)
  Breaking another session's LIVE lock (-Force) is human-gated: only on the user's say-so.

Setup (mutating; needs the lock):
  DedicatedServer/dedicated-server.ps1 -Bootstrap -As <id>
  DedicatedServer/dedicated-server.ps1 -SyncMods -As <id> [-FromModConfig <path>]
  DedicatedServer/dedicated-server.ps1 -DeployMods -As <id> [-Mod <name>] [-Configuration Release|Debug]

Lifecycle (agent-driven, all non-blocking unless noted):
  DedicatedServer/dedicated-server.ps1 -Start -As <id> -Load <SaveName> -Map <Map>  [-GamePort N -UpdatePort N]
  DedicatedServer/dedicated-server.ps1 -Start -As <id> -New <Map>                    [-GamePort N -UpdatePort N]
  DedicatedServer/dedicated-server.ps1 -Status [-As <id>]
  DedicatedServer/dedicated-server.ps1 -Logs [-Tail N] [-Grep pattern]
  DedicatedServer/dedicated-server.ps1 -Save -As <id> -Name <SaveName>          (waits for log confirmation)
  DedicatedServer/dedicated-server.ps1 -SendCommand -As <id> -Command '<text>'
  DedicatedServer/dedicated-server.ps1 -Stop -As <id> [-SaveAs <SaveName>] [-Release]   (waits for clean exit)
"@
