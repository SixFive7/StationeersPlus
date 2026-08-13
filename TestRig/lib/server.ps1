<#
    The dedicated-server half of TestRig/testrig.ps1.

    This file was TestRig/DedicatedServer/dedicated-server.ps1 and is the same code:
    the move to a dot-sourced function library is what let one launcher carry both
    halves, and what let the duplicate helpers (pid reading, process liveness, the
    install path, the game version, the modconfig writer) collapse into
    TestRig/lib/common.ps1 instead of drifting apart in two scripts.

    Everything here is a function. There is no param block and no dispatch: the verb
    surface, the target resolution and the refusal matrix live in testrig.ps1, and
    the session lock lives in TestRig/rig-lock.ps1, which testrig.ps1 dot-sources
    before this file.

    Script variables are prefixed Srv because both halves share one script scope
    once testrig.ps1 has dot-sourced them, and both halves used to declare a
    $script:SrvDataDir, a $script:SrvRepoRoot and a $RigRoot of their own.

    Operating manual: TestRig/MANUAL.md.
    Rig conventions:  TestRig/CLAUDE.md.
#>

function Initialize-RigServer {
    <#
        Point this half at a TestRig-shaped root. -LauncherPath is the testrig.ps1
        the host wrapper re-invokes; it is passed in rather than read from
        $PSCommandPath because in a dot-sourced library that variable names THIS
        file, and pwsh -File on a library with no param block would do nothing at
        all.
    #>
    param(
        [Parameter(Mandatory)] [string] $RigHome,
        [string] $LauncherPath
    )
    $script:SrvRoot        = Join-Path $RigHome 'DedicatedServer'
    $script:SrvRepoRoot    = Split-Path -Parent $RigHome
    $script:SrvInstallDir  = Join-Path $script:SrvRoot 'install'
    $script:SrvDataDir     = Join-Path $script:SrvRoot 'data'
    $script:SrvExe         = Join-Path $script:SrvInstallDir 'rocketstation_DedicatedServer.exe'
    $script:SrvLogFile     = Join-Path $script:SrvDataDir 'server.log'
    $script:SrvControlFile = Join-Path $script:SrvDataDir 'control.cmd'
    $script:SrvPidFile     = Join-Path $script:SrvDataDir 'server.pid'
    $script:SrvHostPidFile = Join-Path $script:SrvDataDir 'host.pid'
    $script:SrvLauncher    = $LauncherPath
}

function Get-RigServerPaths {
    # For the test suite and for status reporting: everything this half owns, in one
    # object, so a test can assert on the layout without reaching for script scope.
    return [pscustomobject]@{
        Root        = $script:SrvRoot
        InstallDir  = $script:SrvInstallDir
        DataDir     = $script:SrvDataDir
        Exe         = $script:SrvExe
        LogFile     = $script:SrvLogFile
        ControlFile = $script:SrvControlFile
        PidFile     = $script:SrvPidFile
        HostPidFile = $script:SrvHostPidFile
        SaveRoot    = (Join-Path $script:SrvDataDir 'saves')
        ModsDir     = (Join-Path $script:SrvDataDir 'mods')
        PluginsDir  = (Join-Path $script:SrvInstallDir 'BepInEx\plugins')
        ModConfig   = (Join-Path $script:SrvInstallDir 'modconfig.xml')
    }
}

# ---- session lock ---------------------------------------------------------
# Mechanism and rules: TestRig/CLAUDE.md and TestRig/MANUAL.md;
# implementation: TestRig/rig-lock.ps1, dot-sourced by testrig.ps1 before this
# file and shared with the client half. The lock is RIG-WIDE: it spans a whole
# test session (many start/stop cycles) and covers the client instances too, so a
# second agent cannot stomp the shared install or the shared per-user Unity state.
# Liveness = timer fresh OR the rig is busy (a player connected here, or a live
# client instance).
#
# Only the server-specific adapter lives here. Everything else (Read-RigLock,
# Write-RigLock, the timer, ownership, the break-lock gate) is in the library, and
# the lock VERBS live in testrig.ps1 because one lock covers both halves.

function Assert-RigServerMutatingAllowed {
    # Gate for every mutating action except stop (which has its own gate).
    param(
        [Parameter(Mandatory)] [string] $Action,
        [string] $As
    )
    Assert-RigLockHeld -Action $Action -CallerId $As -Tool 'testrig.ps1'
}

function Get-ConnectedPlayerCount {
    # Currently-connected client count for the live server. The 'clients' /
    # 'status' console commands write to the in-game console, not the Unity
    # -logFile, so they cannot be scraped; the connection lifecycle IS logged,
    # so we scan server.log via Measure-PlayersInLog (library). Reads the log
    # directly: no stdin round-trip, no dependence on the host wrapper,
    # unaffected by the no-client simulation pause. Returns 0 when the server is
    # not running (favours freeing the rig, per TestRig/CLAUDE.md).
    if (-not (Test-RigServerProcessAlive (Get-RigPidFromFile $script:SrvPidFile))) { return 0 }
    return (Measure-PlayersInLog $script:SrvLogFile)
}

# ---- update-game ----------------------------------------------------------
#
# Named for the concept and not for the mechanism. It used to be -Bootstrap here
# and -Provision -Force on the client half, which is how one agent asked to
# "update the testrig" updated exactly one half and had no way to notice: two
# spellings of "refresh the game binaries" with no cross-reference in either
# launcher's help. The mechanisms genuinely differ (SteamCMD app 600760 here, a
# re-link from the developer's already-updated client install there) but the verb
# does not, so testrig.ps1 fans one verb out over two implementations.

function Invoke-RigServerUpdateGame {
    param([string] $As)
    Assert-RigServerMutatingAllowed -Action 'update-game' -As $As
    Write-Host "[Bootstrap] Verifying environment..."
    $stationeers = Get-RigStationeersPath
    $steamcmd    = Get-RigSteamcmdPath
    Write-Host "[Bootstrap]   StationeersPath: $stationeers"
    Write-Host "[Bootstrap]   STEAMCMD_PATH:   $steamcmd"
    Write-Host "[Bootstrap]   Server install:  $script:SrvInstallDir"
    Write-Host "[Bootstrap]   Server data:     $script:SrvDataDir"

    foreach ($dir in @($script:SrvInstallDir, $script:SrvDataDir)) {
        if (-not (Test-Path $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
    }

    Write-Host "[Bootstrap] Running SteamCMD (app 600760)..."
    & $steamcmd `
        +force_install_dir $script:SrvInstallDir `
        +login anonymous `
        +app_update 600760 validate `
        +quit
    if ($LASTEXITCODE -ne 0) {
        throw "SteamCMD failed with exit code $LASTEXITCODE."
    }
    if (-not (Test-Path $script:SrvExe)) {
        throw "Bootstrap: rocketstation_DedicatedServer.exe missing after SteamCMD run."
    }
    Write-Host "[Bootstrap] SteamCMD install complete."

    Write-Host "[Bootstrap] Mirroring BepInEx tree from client install..."
    $srcBepInEx = Join-Path $stationeers 'BepInEx'
    $dstBepInEx = Join-Path $script:SrvInstallDir 'BepInEx'
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
            Copy-Item -Path $src -Destination (Join-Path $script:SrvInstallDir $f) -Force
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
        $launchPadZipDir     = Join-Path $script:SrvRepoRoot ".work\launchpad-server"
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

    Write-Host "[Bootstrap] Done. Next: testrig update-mods -Target server, then testrig deploy, then testrig start -Target server."
}

# ---- deploy ---------------------------------------------------------------
#
# The local modconfig writer that used to live here is gone. It string-replaced
# </ModConfig> and produced a third file format alongside the one the full sync
# wrote and the one the client half wrote, all three of which the baseline stores
# and restores byte for byte. Add-RigModConfigLocalEntry in lib/common.ps1 is the
# single writer now.

function Deploy-DevPluginMirror {
    # Deploy a TestRig/DedicatedServer/dev-plugins/<X>/ target by mirroring it into the
    # StationeersLaunchPad load path (data/mods/Local_<X>/) and ensuring the
    # modconfig.xml has a matching <Local> entry. Defensively removes any stale
    # install/BepInEx/plugins/<X>/<X>.dll left over from a pre-mirror layout, so
    # the duplicate-load trap documented in TestRig/MANUAL.md cannot
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
    $localModDir = Join-Path $script:SrvDataDir "mods\Local_$ModName"
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
    $bepInExPluginDll = Join-Path $script:SrvInstallDir "BepInEx\plugins\$ModName\$ModName.dll"
    if (Test-Path $bepInExPluginDll) {
        Remove-Item -Force $bepInExPluginDll
        Write-Host "[DeployMods] ${ModName}: removed stale duplicate at install/BepInEx/plugins/$ModName/$ModName.dll"
    }

    $added = Add-RigModConfigLocalEntry -Path (Join-Path $script:SrvInstallDir 'modconfig.xml') -LocalModDir $localModDir
    if ($added) {
        Write-Host "[Deploy] ${ModName}: added modconfig.xml Local entry -> $localModDir"
    }
    Write-Host "[Deploy] $ModName -> $localModDir (StationeersLaunchPad load path)"
    return $true
}

function Invoke-RigServerDeploy {
    <#
        Put one of THIS repository's built mods onto the dedicated server.

        Two destinations, decided by what the target is, and the split is not
        cosmetic: this half has two load paths and the same DLL in both is fatal.
        install/BepInEx/plugins/<X>/ is loaded by the BepInEx Chainloader;
        data/mods/Local_<X>/ is loaded by StationeersLaunchPad. With a DLL in both,
        Awake fires twice, every Harmony patch registers twice and every
        side-effecting patch doubles. Dev-plugins take the StationeersLaunchPad path
        because they need an About.xml; released mods take the plugins path.

        The client half has the same two paths and the same trap, and resolves it
        the other way for the same reason. That is why one deploy verb cannot use
        one destination.
    #>
    param(
        [string] $As,
        [string[]] $Mods = @(),
        [string] $Configuration = 'Release'
    )
    Assert-RigServerMutatingAllowed -Action 'deploy' -As $As
    if (-not (Test-Path $script:SrvExe)) {
        throw "The dedicated server is not installed at $($script:SrvExe). Run: testrig update-game -Target server -As <id>"
    }
    $existingServer = Get-RigPidFromFile $script:SrvPidFile
    $existingHost   = Get-RigPidFromFile $script:SrvHostPidFile
    if ((Test-RigServerProcessAlive $existingServer) -or (Test-RigWrapperProcessAlive $existingHost)) {
        throw "The dedicated server is running (host PID $existingHost, server PID $existingServer). The Mono runtime holds an exclusive lock on every loaded plugin DLL on Windows; a deploy would fail with a sharing violation, or worse, leave a half-written DLL the next start picks up as broken plugin bytes. Run: testrig stop -Target server -As <id>"
    }

    $names = @($Mods)
    if ($names.Count -eq 0) { $names = @(Get-RigDeployableMods) }
    if ($names.Count -eq 0) { throw "No mods to deploy: Mods/ has no mod folders other than Template." }

    $serverPlugins = Join-Path $script:SrvInstallDir 'BepInEx\plugins'
    $deployed = 0
    $skipped  = 0
    foreach ($modName in $names) {
        $build = Get-RigModBuild -Mod $modName -Configuration $Configuration
        if (-not $build) {
            Write-Warning "[$modName] not found under Mods/, Plans/ or either half's dev-plugins/. Skipping."
            $skipped++
            continue
        }
        if ($build.Kind -eq 'devplugin-server' -or $build.Kind -eq 'devplugin-client') {
            if (Deploy-DevPluginMirror -ModDir $build.Dir -ModName $modName -Configuration $Configuration) { $deployed++ }
            else { $skipped++ }
            continue
        }
        if (-not (Test-Path $build.Dll)) {
            Write-Warning "[$modName] $Configuration build not found at $($build.Dll). Skipping. Build it first."
            $skipped++
            continue
        }
        $dstDir = Join-Path $serverPlugins $modName
        if (-not (Test-Path $dstDir)) { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null }
        Copy-Item -Path $build.Dll -Destination $dstDir -Force
        Write-Host "[Deploy] $modName -> $dstDir (BepInEx Chainloader load path)"
        $deployed++
    }
    Write-Host "[Deploy] server: $deployed deployed, $skipped skipped."
    return [pscustomobject]@{ Deployed = $deployed; Skipped = $skipped }
}

# ---- start (detached) -----------------------------------------------------

function Invoke-RigServerStart {
    <#
        Launch the server INTO A WORLD. There is no other way: the dedicated server
        takes -load or -new on its own command line and has no menu to sit at, which
        is why 'start' means something different here from what it means on a client
        instance and why testrig.ps1 refuses a world-less start on this target
        rather than quietly doing half of it.
    #>
    param(
        [string] $As,
        [string] $Load,
        [string] $Map,
        [string] $New,
        [int] $GamePort,
        [int] $UpdatePort
    )
    Assert-RigServerMutatingAllowed -Action 'start' -As $As
    if (-not $GamePort)   { $GamePort   = Get-RigServerGamePort }
    if (-not $UpdatePort) { $UpdatePort = Get-RigServerUpdatePort }
    if (-not (Test-Path $script:SrvExe)) {
        throw "The dedicated server is not installed at $($script:SrvExe). Run: testrig update-game -Target server -As <id>"
    }
    if (-not (Test-Path $script:SrvDataDir)) {
        New-Item -ItemType Directory -Path $script:SrvDataDir -Force | Out-Null
    }
    if ($Load -and $New) { throw "Specify either -Load or -New, not both." }
    if ($Load -and -not $Map) { throw "-Load requires -Map <Map>." }
    if ($Load) {
        $saveDir = Join-Path $script:SrvDataDir "saves\$Load"
        if (-not (Test-Path $saveDir)) {
            throw "Save '$Load' not found at $saveDir. The developer is the sole save manager; ask them to provide it, or use -New <Map>."
        }
    }

    $existingHost   = Get-RigPidFromFile $script:SrvHostPidFile
    $existingServer = Get-RigPidFromFile $script:SrvPidFile
    if ((Test-RigWrapperProcessAlive $existingHost) -or (Test-RigServerProcessAlive $existingServer)) {
        throw "The dedicated server is already running (host PID $existingHost, server PID $existingServer). Run: testrig stop -Target server -As <id>, or check: testrig status"
    }
    foreach ($f in @($script:SrvHostPidFile, $script:SrvPidFile, $script:SrvControlFile)) {
        Remove-Item -Force $f -ErrorAction SilentlyContinue
    }

    $pwsh = (Get-Process -Id $PID).Path
    # The wrapper re-invokes testrig.ps1, NOT this library: a dot-sourced library
    # has no param block, so pwsh -File against it would run nothing and the server
    # would never start. $script:SrvLauncher is set by Initialize-RigServer.
    $wrapperArgs = @('-NoProfile', '-NonInteractive', '-File', $script:SrvLauncher, 'host-mode',
                     '-GamePort', $GamePort, '-UpdatePort', $UpdatePort)
    if ($Load) { $wrapperArgs += @('-Load', $Load, '-Map', $Map) }
    else       { $wrapperArgs += @('-New', $New) }

    # CreateNoWindow skips conhost allocation entirely; Start-Process -WindowStyle Hidden flashes a brief focus claim on Win10/11.
    # Every argument is quoted through the shared helper: an unquoted list joined
    # with plain spaces is what once broke every lock acquisition in the playtest
    # harness, and this list carries a save name that can contain a space.
    $wrapperArgString = ConvertTo-RigCommandLine -Arguments $wrapperArgs
    $hostPsi = [System.Diagnostics.ProcessStartInfo]::new()
    $hostPsi.FileName         = $pwsh
    $hostPsi.Arguments        = $wrapperArgString
    $hostPsi.UseShellExecute  = $false
    $hostPsi.CreateNoWindow   = $true
    $hostPsi.WorkingDirectory = $script:SrvRoot
    $hostProc = [System.Diagnostics.Process]::Start($hostPsi)
    Set-Content -Path $script:SrvHostPidFile -Value $hostProc.Id

    Write-Host "[Start] Host wrapper launched (PID $($hostProc.Id))."
    Write-Host "[Start] Waiting for server process to register..."

    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        $serverPidVal = Get-RigPidFromFile $script:SrvPidFile
        if ((Test-RigServerProcessAlive $serverPidVal)) {
            Write-Host "[Start] Server PID $serverPidVal."
            Write-Host "[Start] Log:    $($script:SrvLogFile)"
            Write-Host "[Start] The process being up is NOT the world being ready. Wait for it with:"
            Write-Host "[Start]   testrig wait -Target server -Stage inWorld -WaitSeconds 600"
            return
        }
        if (-not (Test-RigWrapperProcessAlive $hostProc.Id)) {
            throw "Host wrapper exited before the server registered. Inspect $($script:SrvLogFile)."
        }
        Start-Sleep -Milliseconds 500
    }
    throw "Server did not register within 20 seconds. Inspect $($script:SrvLogFile) and run: testrig status -Target server"
}

# ---- host wrapper (internal) ---------------------------------------------

function Invoke-RigServerHostMode {
    param(
        [string] $Load,
        [string] $Map,
        [string] $New,
        [int] $GamePort,
        [int] $UpdatePort
    )
    if ($Load -and -not $Map) { throw "[HostMode] -Load requires -Map." }
    if (-not $Load -and -not $New) { throw "[HostMode] missing -Load or -New." }
    if (-not $GamePort)   { $GamePort   = Get-RigServerGamePort }
    if (-not $UpdatePort) { $UpdatePort = Get-RigServerUpdatePort }

    $settingPath = Join-Path $script:SrvDataDir 'setting.xml'
    $serverArgs = @(
        '-batchmode'
        '-nographics'
        '-settingspath', $settingPath
        '-logFile',      $script:SrvLogFile
        '-settings', 'SavePath',         $script:SrvDataDir
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

    $argString = ConvertTo-RigCommandLine -Arguments $serverArgs

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName               = $script:SrvExe
    $psi.Arguments              = $argString
    $psi.RedirectStandardInput  = $true
    $psi.UseShellExecute        = $false
    $psi.WorkingDirectory       = $script:SrvInstallDir
    $psi.CreateNoWindow         = $true

    $proc = [System.Diagnostics.Process]::Start($psi)
    Set-Content -Path $script:SrvPidFile -Value $proc.Id

    try {
        while (-not $proc.HasExited) {
            if (Test-Path $script:SrvControlFile) {
                # Brief settle so we don't read mid-write (writer uses atomic rename, but be defensive).
                Start-Sleep -Milliseconds 50
                try {
                    $cmd = (Get-Content -Raw -ErrorAction Stop $script:SrvControlFile).Trim()
                    Remove-Item -Force -ErrorAction Stop $script:SrvControlFile
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
        Remove-Item -Force -ErrorAction SilentlyContinue $script:SrvPidFile
        Remove-Item -Force -ErrorAction SilentlyContinue $script:SrvHostPidFile
        Remove-Item -Force -ErrorAction SilentlyContinue $script:SrvControlFile
    }
}

# ---- send command ---------------------------------------------------------

function Send-ServerCommand {
    param(
        [Parameter(Mandatory)] [string] $Cmd,
        [int] $WaitForFreeSeconds = 5
    )
    $serverPidVal = Get-RigPidFromFile $script:SrvPidFile
    if (-not (Test-RigServerProcessAlive $serverPidVal)) {
        throw "Server is not running."
    }
    $hostPidVal = Get-RigPidFromFile $script:SrvHostPidFile
    if (-not (Test-RigWrapperProcessAlive $hostPidVal)) {
        throw "Host wrapper is not running; cannot relay commands. Clean up the orphaned server with: testrig stop -Target server -As <id>"
    }

    $deadline = (Get-Date).AddSeconds($WaitForFreeSeconds)
    while ((Test-Path $script:SrvControlFile) -and ((Get-Date) -lt $deadline)) {
        Start-Sleep -Milliseconds 100
    }
    if (Test-Path $script:SrvControlFile) {
        throw "Previous control command still pending after ${WaitForFreeSeconds}s."
    }

    $tmpFile = "$script:SrvControlFile.tmp"
    Set-Content -Path $tmpFile -Value $Cmd -NoNewline
    Move-Item -Path $tmpFile -Destination $script:SrvControlFile -Force
}

function Invoke-RigServerSend {
    # The dedicated server's control channel: one line into its stdin, through the
    # wrapper's control file. Fire and forget by necessity, because the console
    # writes its answers to the in-game console and not to the -logFile, so there is
    # nothing to read back. That is the whole reason 'call' and 'send' are two verbs
    # rather than one with two transports.
    param(
        [string] $As,
        [Parameter(Mandatory)] [string] $Command
    )
    Assert-RigServerMutatingAllowed -Action 'send' -As $As
    Send-ServerCommand -Cmd $Command
    Write-Host "[Send] Queued on the server's stdin: $Command"
}

# ---- save ----------------------------------------------------------------

function Wait-LogPattern {
    param(
        [Parameter(Mandatory)] [string] $Pattern,
        [int] $TimeoutSec = 30
    )
    if (-not (Test-Path $script:SrvLogFile)) { return $false }
    $startLen = (Get-Item $script:SrvLogFile).Length
    $deadline = (Get-Date).AddSeconds($TimeoutSec)

    while ((Get-Date) -lt $deadline) {
        $currentLen = (Get-Item $script:SrvLogFile).Length
        if ($currentLen -gt $startLen) {
            $stream = [System.IO.File]::Open($script:SrvLogFile, 'Open', 'Read', 'ReadWrite')
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

function Invoke-RigServerSave {
    <#
        Queue a named save and wait for the log to confirm it landed.

        -SaveName is REQUIRED on this half, and that is not an oversight: the
        console's save command takes a name and has no "save under the current
        name" form, because the console has no notion of the world's current name
        to fall back on. A client instance does, which is why the same flag is
        optional there. testrig.ps1 refuses a nameless save here rather than
        inventing a name.

        -WaitSeconds is the confirmation budget, 300 by default, the same flag with
        the same meaning and now the same default as on the client half.
    #>
    param(
        [string] $As,
        [Parameter(Mandatory)] [string] $SaveName,
        [int] $WaitSeconds = 0
    )
    Assert-RigServerMutatingAllowed -Action 'save' -As $As
    if (-not $WaitSeconds) { $WaitSeconds = Get-RigWaitDefaultSeconds }
    Send-ServerCommand -Cmd ('save "{0}"' -f $SaveName)
    Write-Host "[Save] Queued save '$SaveName' on the server. Waiting for confirmation (up to ${WaitSeconds}s)..."
    $confirmed = Wait-LogPattern -Pattern ("Saved.*" + [regex]::Escape($SaveName)) -TimeoutSec $WaitSeconds
    if ($confirmed) {
        Write-Host "[Save] Confirmed."
        return $true
    }
    Write-Warning "[Save] No 'Saved $SaveName' line in the log within ${WaitSeconds}s. Treat this world as NOT saved: it may have completed silently or failed. testrig logs -Target server -Grep Saved shows what the server actually did."
    return $false
}

# ---- stop ----------------------------------------------------------------

function Stop-RigServerProcesses {
    # Tear down server + host wrapper and clean pid/control files. Does NOT touch
    # the session lock. Used by the stop verb and by the lock's reclaim of a dead
    # lock, which is why it takes its grace period as a parameter rather than
    # reading one from a launcher scope that a reclaim does not have.
    param([int] $TimeoutSeconds = 0)
    if (-not $TimeoutSeconds) { $TimeoutSeconds = Get-RigTimeoutDefaultSeconds }
    $serverPidVal = Get-RigPidFromFile $script:SrvPidFile
    $hostPidVal   = Get-RigPidFromFile $script:SrvHostPidFile

    if ((Test-RigServerProcessAlive $serverPidVal) -and (Test-RigWrapperProcessAlive $hostPidVal)) {
        Write-Host "[Stop] Sending 'quit' via host wrapper..."
        try { Send-ServerCommand -Cmd 'quit' } catch { Write-Warning "[Stop] $_" }

        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while ((Get-Date) -lt $deadline) {
            if (-not (Test-RigServerProcessAlive $serverPidVal)) { break }
            Start-Sleep -Milliseconds 500
        }
    }

    if (Test-RigServerProcessAlive $serverPidVal) {
        Write-Warning "[Stop] Server still alive after ${TimeoutSeconds}s; force-killing."
        Stop-Process -Id $serverPidVal -Force -ErrorAction SilentlyContinue
    }
    if (Test-RigWrapperProcessAlive $hostPidVal) {
        Stop-Process -Id $hostPidVal -Force -ErrorAction SilentlyContinue
    }

    foreach ($f in @($script:SrvHostPidFile, $script:SrvPidFile, $script:SrvControlFile)) {
        Remove-Item -Force $f -ErrorAction SilentlyContinue
    }
}

function Invoke-RigServerStop {
    <#
        Stop the dedicated server, optionally saving first.

        The lock-state gate and the -Release handling are NOT here. They are in
        testrig.ps1 because they are rig-wide and identical for both halves, and
        because the ordering dependency between them (ask for the lock state
        BEFORE releasing) has to hold across a stop that touches both halves, not
        once per half. The client half used to carry its own inline copy of the
        release predicate, untested, next to the tested one the server half called.

        -SaveName here uses -WaitSeconds for its confirmation, not -TimeoutSeconds.
        -TimeoutSeconds is teardown grace and nothing else. This branch was the last
        place in the rig where those two were still conflated: it fed the teardown
        grace into a save confirmation, so raising the kill timeout also, silently,
        raised how long a save was given to land.
    #>
    param(
        [string] $As,
        [string] $SaveName,
        [int] $TimeoutSeconds = 0,
        [int] $WaitSeconds = 0
    )
    if (-not $TimeoutSeconds) { $TimeoutSeconds = Get-RigTimeoutDefaultSeconds }
    if (-not $WaitSeconds)    { $WaitSeconds    = Get-RigWaitDefaultSeconds }

    $serverAlive = Test-RigServerProcessAlive (Get-RigPidFromFile $script:SrvPidFile)
    $hostAlive   = Test-RigWrapperProcessAlive (Get-RigPidFromFile $script:SrvHostPidFile)

    if (-not $serverAlive -and -not $hostAlive) {
        Write-Host "[Stop] Dedicated server: nothing running."
        foreach ($f in @($script:SrvHostPidFile, $script:SrvPidFile, $script:SrvControlFile)) {
            Remove-Item -Force $f -ErrorAction SilentlyContinue
        }
        return
    }

    if ($SaveName -and $serverAlive -and $hostAlive) {
        Write-Host "[Stop] Saving as '$SaveName' first..."
        try {
            Send-ServerCommand -Cmd ('save "{0}"' -f $SaveName)
            $confirmed = Wait-LogPattern -Pattern ("Saved.*" + [regex]::Escape($SaveName)) -TimeoutSec $WaitSeconds
            if (-not $confirmed) {
                Write-Warning "[Stop] No save confirmation within ${WaitSeconds}s; continuing with quit. Treat that world as NOT saved."
            }
        }
        catch {
            Write-Warning "[Stop] Save failed: $_"
        }
    }
    elseif ($SaveName) {
        Write-Warning "[Stop] -SaveName ignored: the server or its host wrapper is not running."
    }
    Stop-RigServerProcesses -TimeoutSeconds $TimeoutSeconds
    Write-Host "[Stop] Dedicated server stopped."
}

# ---- readiness ------------------------------------------------------------

function Invoke-RigServerWait {
    <#
        Block until the server's world is loaded and the simulation is ticking.

        This half had no readiness barrier at all. Its manual documented three
        hand-rolled patterns instead, and every caller picked one and wrote it out
        again; meanwhile the client half had one flag. This is the recommended
        pattern of the three, in code: drop a minimal InspectorPlus request into the
        requests folder and poll for the plugin to delete it. The pump runs off
        ElectricityManager.ElectricityTick, so the file is consumed only once the
        world is loaded and ticking, and its deletion is the readiness signal.

        It needs InspectorPlus deployed with "Force Unpause Without Client" set, or
        the simulation stays paused with nobody connected and the request is never
        consumed. That is named in the timeout message rather than assumed, because
        an unconsumed probe and a slow world look identical from out here.

        The only other stage this half can answer is 'process', which is just the
        pid being alive and is NOT readiness. testrig.ps1 refuses the client-only
        stages on this target rather than pretending.
    #>
    param(
        [ValidateSet('process', 'inWorld')] [string] $Stage = 'inWorld',
        [int] $WaitSeconds = 0
    )
    if (-not $WaitSeconds) { $WaitSeconds = Get-RigWaitDefaultSeconds }
    $serverPidVal = Get-RigPidFromFile $script:SrvPidFile

    if ($Stage -eq 'process') {
        $deadline = (Get-Date).AddSeconds($WaitSeconds)
        while ((Get-Date) -lt $deadline) {
            if (Test-RigServerProcessAlive (Get-RigPidFromFile $script:SrvPidFile)) {
                Write-Host "[Wait] Dedicated server process is up."
                return $true
            }
            Start-Sleep -Milliseconds 500
        }
        throw "[Wait] The dedicated server process did not come up within ${WaitSeconds}s. Inspect $($script:SrvLogFile)."
    }

    if (-not (Test-RigServerProcessAlive $serverPidVal)) {
        throw "[Wait] The dedicated server is not running, so there is no world to wait for. Start it first: testrig start -Target server -As <id> -New <Map>"
    }

    $requestsDir = Join-Path $script:SrvInstallDir 'BepInEx\inspector\requests'
    $probeName   = "testrig-ready-$([Guid]::NewGuid().ToString('N').Substring(0, 8)).json"
    $probePath   = Join-Path $requestsDir $probeName
    if (-not (Test-Path $requestsDir)) { New-Item -ItemType Directory -Force -Path $requestsDir | Out-Null }
    Set-Content -LiteralPath $probePath -Value '{"types": ["CableNetwork"], "maxMonoBehaviours": 1}' -Encoding utf8

    Write-Host "[Wait] Dropped an InspectorPlus readiness probe at $probePath; waiting up to ${WaitSeconds}s for the server to consume it."
    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    try {
        while ((Get-Date) -lt $deadline) {
            if (-not (Test-Path -LiteralPath $probePath)) {
                Write-Host "[Wait] Probe consumed: the world is loaded and the simulation is ticking."
                return $true
            }
            if (-not (Test-RigServerProcessAlive (Get-RigPidFromFile $script:SrvPidFile))) {
                throw "[Wait] The dedicated server exited while the readiness probe was pending. Inspect $($script:SrvLogFile)."
            }
            Start-Sleep -Seconds 2
        }
    }
    finally {
        Remove-Item -Force -ErrorAction SilentlyContinue -LiteralPath $probePath
    }
    throw "[Wait] The dedicated server did not consume the readiness probe within ${WaitSeconds}s. Either the world is still loading (a populated save takes minutes), or InspectorPlus is not deployed, or its 'Force Unpause Without Client' setting is off, in which case the simulation is paused with nobody connected and no probe will ever be consumed. Check install/BepInEx/config/net.inspectorplus.cfg, then: testrig logs -Target server -Tail 40"
}

# ---- status & logs --------------------------------------------------------

function Write-RigServerStatus {
    <#
        The server's own block. The rig-wide lock block is NOT printed here: it is
        printed once by testrig.ps1, above both halves, because there is one lock
        and printing it per half made "the first line of status" a different thing
        depending on which launcher you asked.
    #>
    $hostPidVal   = Get-RigPidFromFile $script:SrvHostPidFile
    $serverPidVal = Get-RigPidFromFile $script:SrvPidFile
    $hostAlive    = Test-RigWrapperProcessAlive $hostPidVal
    $serverAlive  = Test-RigServerProcessAlive $serverPidVal

    $hostLine   = if ($hostAlive)   { "running (PID $hostPidVal)" }   else { 'stopped' }
    $serverLine = if ($serverAlive) {
        $sp = Get-Process -Id $serverPidVal -ErrorAction SilentlyContinue
        $up = if ($sp) { ((Get-Date) - $sp.StartTime).ToString('hh\:mm\:ss') } else { '?' }
        "running (PID $serverPidVal, up $up)"
    }
    else { 'stopped' }

    Write-Host "server (dedicated):"
    Write-Host "  host wrapper: $hostLine"
    Write-Host "  process:      $serverLine"
    if (-not (Test-Path $script:SrvExe)) {
        Write-Host "  install:      NOT INSTALLED at $($script:SrvInstallDir). Run: testrig update-game -Target server -As <id>"
    }

    if (Test-Path $script:SrvLogFile) {
        $lastLine = Get-Content -Tail 1 $script:SrvLogFile -ErrorAction SilentlyContinue
        Write-Host "  last log:     $lastLine"
    }
    if (Test-Path $script:SrvControlFile) {
        $pending = (Get-Content -Raw -ErrorAction SilentlyContinue $script:SrvControlFile).Trim()
        Write-Host "  pending cmd:  $pending"
    }
    if ($serverAlive) {
        Write-Host "  players:      $(Get-ConnectedPlayerCount) connected"
    }

    $worlds = @(Get-ChildItem -LiteralPath (Join-Path $script:SrvDataDir 'saves') -Directory -ErrorAction SilentlyContinue)
    Write-Host "  worlds:       $($worlds.Count) under data/saves/"

    if ($serverAlive -and -not $hostAlive) {
        Write-Warning "The server is alive but its host wrapper is gone, so nothing can relay a console command to it. Terminate the orphan: testrig stop -Target server -As <id>"
    }
}

function Get-RigServerVersionReport {
    <#
        What game version this half carries, and whether it matches the developer's
        client install.

        This is half of the answer the rig-wide status owes an agent asked to
        "update the testrig". Nothing used to compare the two, and the only
        staleness the rig reported at all was per client instance, which is
        precisely why an agent updated the client half and left this one behind.
    #>
    $installed = Get-RigInstallVersion -InstallDir $script:SrvInstallDir
    $source    = 'unknown'
    try { $source = Get-RigInstallVersion -InstallDir (Get-RigStationeersPath) } catch { $source = 'unknown' }
    $stale = ($installed -ne 'unknown' -and $source -ne 'unknown' -and $installed -ne $source)
    return [pscustomobject]@{
        Half      = 'server'
        Present   = (Test-Path $script:SrvExe)
        Version   = $installed
        Source    = $source
        Stale     = $stale
        Remedy    = 'testrig update-game -Target server -As <id>'
    }
}

function Get-RigServerModStaleness {
    <#
        Which of this half's deployed payloads are older than what they came from.

        Two payload kinds, matching the two load paths: mods synced out of the
        developer's own folder (data/mods/) and this repository's built plugins
        (install/BepInEx/plugins/). Both are only ever REPORTED, never deleted or
        re-copied here, for the same reason the state reset only reports them: the
        fix is a deploy or an update, and deleting a payload to signal staleness
        would break a rig instead of describing it.
    #>
    $rows = New-Object System.Collections.Generic.List[object]

    $srcMods = Join-Path (Get-RigUserDataPath) 'mods'
    $dstMods = Join-Path $script:SrvDataDir 'mods'
    foreach ($d in @(Get-ChildItem -LiteralPath $dstMods -Directory -ErrorAction SilentlyContinue)) {
        $bare = $d.Name -replace '^(Workshop|Local)_', ''
        $src  = Join-Path $srcMods $bare
        if (-not (Test-Path -LiteralPath $src)) { continue }
        $srcTime = Get-RigNewestBuildTime -Path $src
        $dstTime = Get-RigNewestBuildTime -Path $d.FullName
        if ($srcTime -and $dstTime -and $srcTime -gt $dstTime) {
            $rows.Add([pscustomobject]@{
                Half = 'server'; Kind = 'seeded mod'; Name = $d.Name
                Deployed = $dstTime; Source = $srcTime
                Remedy = 'testrig update-mods -Target server -As <id>'
            })
        }
    }

    $plugins = Join-Path $script:SrvInstallDir 'BepInEx\plugins'
    foreach ($d in @(Get-ChildItem -LiteralPath $plugins -Directory -ErrorAction SilentlyContinue)) {
        $build = Get-RigModBuild -Mod $d.Name -Configuration 'Release'
        if (-not $build -or -not (Test-Path -LiteralPath $build.Dll)) { continue }
        $srcTime = (Get-Item -LiteralPath $build.Dll).LastWriteTimeUtc
        $dstTime = Get-RigNewestBuildTime -Path $d.FullName
        if ($dstTime -and $srcTime -gt $dstTime) {
            $rows.Add([pscustomobject]@{
                Half = 'server'; Kind = 'deployed plugin'; Name = $d.Name
                Deployed = $dstTime; Source = $srcTime
                Remedy = "testrig deploy $($d.Name) -Target server -As <id>"
            })
        }
    }
    return $rows.ToArray()
}

function Invoke-RigServerLogs {
    param([int] $Tail = 50, [string] $Grep)
    if (-not (Test-Path $script:SrvLogFile)) {
        Write-Host "No dedicated-server log at $($script:SrvLogFile)."
        return
    }
    Write-Host "== server: $($script:SrvLogFile)"
    if ($Grep) { Get-Content $script:SrvLogFile | Select-String -Pattern $Grep }
    else       { Get-Content -Tail $Tail $script:SrvLogFile }
}

# ---- update-mods ----------------------------------------------------------
#
# Was -SyncMods. Same concept as the client half's mod seed, which was spelled
# -Provision -Force there, which is how "update the testrig" could hit one half
# and not the other. One verb now, fanned out by testrig.ps1.

function Invoke-RigServerUpdateMods {
    <#
        Mirror the developer's enabled mod set onto the server.

        Reads their modconfig.xml (read-only on the source, always), copies each
        enabled Workshop and Local entry into data/mods/<Source>_<Name>/, and bakes
        an install/modconfig.xml of Local entries pointing at the copies. That
        replicates StationeersLaunchPad's Export Mod Package without driving the UI.
        See Research/Workflows/StationeersLaunchPadDedicatedServer.md.

        Local mods are scanned from <SavePath>/mods/, which is data/mods/, NOT
        install/mods/.

        THIS WIPES data/mods/, so anything deployed there by 'testrig deploy' goes
        with it. That is the pre-existing order (sync first, deploy second) and the
        function now says so out loud instead of leaving it to be rediscovered.
    #>
    param(
        [string] $As,
        [string] $FromModConfig
    )
    Assert-RigServerMutatingAllowed -Action 'update-mods' -As $As
    if (-not (Test-Path $script:SrvExe)) {
        throw "The dedicated server is not installed at $($script:SrvExe). Run: testrig update-game -Target server -As <id>"
    }
    $existingServer = Get-RigPidFromFile $script:SrvPidFile
    $existingHost   = Get-RigPidFromFile $script:SrvHostPidFile
    if ((Test-RigServerProcessAlive $existingServer) -or (Test-RigWrapperProcessAlive $existingHost)) {
        throw "The dedicated server is running (host PID $existingHost, server PID $existingServer). StationeersLaunchPad holds the synced mod files open for class scanning; overwriting them while loaded fails with a sharing violation or leaves a half-written tree. Run: testrig stop -Target server -As <id>"
    }
    if (-not $FromModConfig) {
        $FromModConfig = Join-Path (Get-RigUserDataPath) 'modconfig.xml'
    }
    if (-not (Test-Path $FromModConfig)) {
        throw "Source modconfig not found at $FromModConfig. Pass -FromModConfig <path> to override."
    }

    Write-Host "[UpdateMods] server source: $FromModConfig"
    $entries = New-Object System.Collections.Generic.List[object]
    foreach ($e in (Get-RigModConfigEntries -Path $FromModConfig)) {
        if (-not $e.Enabled) { continue }
        switch ($e.Kind) {
            'Core' { }   # implicit; the writer always emits one
            'Workshop' {
                $wid = $e.WorkshopId
                if (-not $wid) {
                    Write-Warning "[UpdateMods] Workshop entry without WorkshopId; using the basename of $($e.Path)"
                    $wid = Split-Path -Leaf $e.Path
                }
                $entries.Add([pscustomobject]@{ Source = $e.Path; DestName = "Workshop_$wid" })
            }
            'Local' {
                if (-not $e.Path) { continue }
                $entries.Add([pscustomobject]@{ Source = $e.Path; DestName = "Local_$(Split-Path -Leaf $e.Path)" })
            }
            default { Write-Warning "[UpdateMods] Unknown modconfig entry type '$($e.Kind)'; ignoring" }
        }
    }

    $modsDir = Join-Path $script:SrvDataDir 'mods'
    $wiped = @(Get-ChildItem -LiteralPath $modsDir -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
    if (Test-Path $modsDir) {
        Write-Host "[UpdateMods] Wiping $modsDir"
        Remove-Item -Recurse -Force $modsDir
    }
    New-Item -ItemType Directory -Path $modsDir -Force | Out-Null

    $copied  = 0
    $skipped = 0
    foreach ($e in $entries) {
        if (-not (Test-Path $e.Source)) {
            Write-Warning "[UpdateMods] [$($e.DestName)] source missing: $($e.Source) (skipping)"
            $skipped++
            continue
        }
        Copy-Item -Recurse -Path $e.Source -Destination (Join-Path $modsDir $e.DestName)
        Write-Host "[UpdateMods] $($e.DestName) <- $($e.Source)"
        $copied++
    }

    # The baked file, through the one shared writer. Path values are the copied
    # folder NAMES, resolved by StationeersLaunchPad against the save path.
    $baked = @($entries |
        Where-Object { Test-Path (Join-Path $modsDir $_.DestName) } |
        ForEach-Object { [pscustomobject]@{ Kind = 'Local'; Enabled = $true; Path = $_.DestName; WorkshopId = '' } })
    $configPath = Join-Path $script:SrvInstallDir 'modconfig.xml'
    Write-RigModConfigFile -Path $configPath -Entries $baked

    Write-Host "[UpdateMods] Wrote $configPath with $copied Local entries (Core + $copied)."
    Write-Host "[UpdateMods] server: $copied copied, $skipped skipped (missing source)."

    # Name what the wipe took that this repository put there, because a re-deploy
    # is the only way it comes back and nothing else would say it had gone.
    $repoMods = @(Get-RigDeployableMods)
    $lost = @($wiped | Where-Object { $_ -match '^Local_(.+)$' -and $repoMods -contains $Matches[1] })
    if ($lost.Count -gt 0) {
        Write-Warning "[UpdateMods] The wipe removed $($lost.Count) folder(s) this repository had deployed: $($lost -join ', '). Re-deploy them: testrig deploy <Mod> -Target server -As <id>"
    }
    return [pscustomobject]@{ Copied = $copied; Skipped = $skipped }
}
