using System.Globalization;
using TestRig.Core.Rig;
using TestRig.Core.Session;

namespace TestRig.Core.Server;

/// <summary>Which world a start enters, and how.</summary>
/// <remarks>
/// There is no third option and no "no world". The dedicated server takes <c>-load</c> or
/// <c>-new</c> on its own command line and has no menu to sit at, which is why <c>start</c>
/// means something different here from what it means on a client instance.
/// </remarks>
public sealed record ServerStartWorld(string? Load, string? Map, string? New)
{
    public bool IsLoad => !string.IsNullOrEmpty(Load);
}

public sealed partial class ServerHalf
{
    /// <summary>How long <c>start</c> waits for the game to register its pid.</summary>
    public const int RegistrationSeconds = 20;

    // =====================================================================
    // start
    // =====================================================================

    /// <summary>
    /// Launches the server INTO A WORLD, through a host wrapper that owns its stdin.
    /// </summary>
    public async Task StartAsync(
        ServerStartWorld world,
        string? callerId = null,
        int gamePort = 0,
        int updatePort = 0,
        CancellationToken ct = default)
    {
        AssertGate("start", callerId);

        if (gamePort <= 0) gamePort = RigConstants.ServerGamePort;
        if (updatePort <= 0) updatePort = RigConstants.ServerUpdatePort;

        if (!_fs.FileExists(_paths.Exe))
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                $"The dedicated server is not installed at {_paths.Exe}. Run: testrig update-game --target "
                + "server --as <id>");
        }

        _fs.CreateDirectory(_paths.DataDir);

        AssertWorldArguments(world);
        if (world.IsLoad)
        {
            AssertSaveIsLoadable(world.Load!);
        }
        else
        {
            AssertMapIsReal(world.New!);
            WarnAboutNewWorldSaves(world.New!);
        }

        if (WrapperAlive || ServerAlive)
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                $"The dedicated server is already running (host PID {HostPid}, server PID {ServerPid}). Run: "
                + "testrig stop --target server --as <id>, or check: testrig status");
        }

        foreach (var file in new[] { _paths.HostPidFile, _paths.PidFile, _paths.ControlFile })
        {
            PidFiles.Delete(_fs, file);
        }

        var wrapperArgs = new List<string>
        {
            "host-mode",
            "--game-port", gamePort.ToString(CultureInfo.InvariantCulture),
            "--update-port", updatePort.ToString(CultureInfo.InvariantCulture),
        };
        if (world.IsLoad)
        {
            wrapperArgs.AddRange(["--load", world.Load!, "--map", world.Map!]);
        }
        else
        {
            wrapperArgs.AddRange(["--new", world.New!]);
        }

        var (hostPid, hostStarted) = _launcher.StartWrapper(
            _launcherPath, string.Join('\0', wrapperArgs), _paths.Root);

        PidFiles.Write(_fs, _paths.HostPidFile, hostPid, hostStarted);

        Say($"[Start] Host wrapper launched (PID {hostPid}).");
        Say("[Start] Waiting for server process to register...");

        var deadline = _clock.UtcNow.AddSeconds(RegistrationSeconds);
        while (_clock.UtcNow < deadline)
        {
            if (ServerAlive)
            {
                Say($"[Start] Server PID {ServerPid}.");
                Say($"[Start] Log:    {_paths.LogFile}");
                Say("[Start] The process being up is NOT the world being ready. Wait for it with:");
                Say("[Start]   testrig wait --target server --stage inWorld --wait-seconds 600");
                _output.Value("serverPid", ServerPid);
                return;
            }

            if (!PidFiles.WrapperAlive(_processes, hostPid))
            {
                throw new RigRefusalException(
                    RigRefusalKind.Refused,
                    $"Host wrapper exited before the server registered. Inspect {_paths.LogFile}.");
            }

            await _sleeper.DelayAsync(PollInterval, ct).ConfigureAwait(false);
        }

        throw new RigRefusalException(
            RigRefusalKind.Refused,
            $"Server did not register within {RegistrationSeconds} seconds. Inspect {_paths.LogFile} and run: "
            + "testrig status --target server");
    }

    private static void AssertWorldArguments(ServerStartWorld world)
    {
        if (!string.IsNullOrEmpty(world.Load) && !string.IsNullOrEmpty(world.New))
        {
            throw new RigRefusalException(RigRefusalKind.Refused, "Specify either --load or --new, not both.");
        }
        if (!string.IsNullOrEmpty(world.Load) && string.IsNullOrEmpty(world.Map))
        {
            throw new RigRefusalException(RigRefusalKind.Refused, "--load requires --map <Map>.");
        }
        if (string.IsNullOrEmpty(world.Load) && string.IsNullOrEmpty(world.New))
        {
            throw new RigRefusalException(RigRefusalKind.Refused, "Missing --load or --new.");
        }
    }

    /// <summary>
    /// Refuses a <c>--load</c> that would silently create a brand-new empty world.
    /// </summary>
    /// <remarks>
    /// SERVER-053 fixed, spec D-02. The PowerShell checked only that the FOLDER existed. A
    /// folder holding a mismatched basename, no <c>.save</c> file at all, or two of them
    /// makes the game start a BRAND NEW EMPTY WORLD under that name while the operator
    /// believes a populated save loaded, and every assertion afterwards runs against an empty
    /// planet. The save's own file has to be there, and there has to be exactly one.
    /// </remarks>
    public void AssertSaveIsLoadable(string saveName)
    {
        var dir = _paths.World(saveName);

        if (!_fs.DirectoryExists(dir))
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                $"Save '{saveName}' not found at {dir}. The developer is the sole save manager; ask them to "
                + "provide it, or use --new <Map>.");
        }

        var saves = _fs.EnumerateFiles(dir, "*.save", recurse: false);
        var expected = Path.Combine(dir, saveName + ".save");

        if (saves.Count == 0)
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                $"Save '{saveName}' has a folder at {dir} but no .save file in it. The game would treat that as "
                + "a new world and start an EMPTY one under this name, with nothing reported. Restore the save "
                + "into that folder, or use --new <Map> if an empty world is what you meant.");
        }

        if (!_fs.FileExists(expected))
        {
            var found = string.Join(", ", saves.Select(Path.GetFileName));
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                $"Save '{saveName}' has a folder at {dir} but no {saveName}.save in it (it holds: {found}). The "
                + "game matches the save file by the folder's own name, so it would start an EMPTY world under "
                + "this name instead. Rename the folder to match the save, or the save to match the folder.");
        }

        if (saves.Count > 1)
        {
            var found = string.Join(", ", saves.Select(Path.GetFileName));
            Warn($"[Start] {dir} holds more than one .save file ({found}). {saveName}.save is the one that will "
                 + "be loaded; the others are ignored and are probably debris from a rename.");
        }
    }

    /// <summary>
    /// Refuses a <c>--new</c> naming a world this install does not have.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured: <c>--new Moon</c> was accepted here, the server booted for ninety seconds,
    /// logged <c>No such world name: Moon. Valid worlds: ...</c> and then ran indefinitely with
    /// no world at all. Nothing failed, and the readiness barrier reported the world loaded.
    /// The set it would have accepted is on disk before launch, so the whole round trip is
    /// avoidable, and naming the valid set is what turns a refusal into an answer.
    /// </para>
    /// <para>
    /// An unreadable catalogue validates NOTHING and says so. The game is the authority; a
    /// data-file layout this reader does not recognise must not become a refusal of a world
    /// the server would have started.
    /// </para>
    /// </remarks>
    public void AssertMapIsReal(string map)
    {
        var catalogue = ServerWorlds.Read(_fs, _paths.InstallDir);

        if (!catalogue.Readable)
        {
            Warn($"[Start] Could not read the world catalogue under {_paths.InstallDir}, so '{map}' was not "
                 + "checked. If it is wrong the server boots, logs \"No such world name\", and then runs with no "
                 + "world at all; the readiness wait catches that, ninety seconds later.");
            return;
        }

        if (catalogue.Accepts(map)) return;

        throw new RigRefusalException(
            RigRefusalKind.Refused,
            $"'{map}' is not a world this install has, so the server would boot, log \"No such world name: "
            + $"{map}\", and then run forever with no world. Valid worlds: "
            + $"{string.Join(", ", catalogue.Names)}. Read from "
            + $"{Path.Combine(_paths.InstallDir, "rocketstation_DedicatedServer_Data", "StreamingAssets", ServerWorlds.WorldsFolder)}, "
            + "where the accepted name is the World Id inside each world's own .xml and is not always the folder "
            + "name.");
    }

    /// <summary>
    /// Warns that a brand-new world cannot autosave until it has been saved by name once.
    /// </summary>
    /// <remarks>
    /// SERVER-081, spec D-03. A <c>-new</c> world has an empty <c>CurrentStationName</c>, so
    /// every autosave fails with "Save Failed: Folder name is empty." until a first NAMED
    /// save assigns one, and the only channel that can do that is the stdin path. The
    /// PowerShell offered <c>-New</c> as a first-class option with no warning attached, so a
    /// soak run could produce hours of simulation and no save at all.
    /// </remarks>
    private void WarnAboutNewWorldSaves(string map)
    {
        Warn($"[Start] --new {map} creates a world with no station name, and every autosave fails with \"Save "
             + "Failed: Folder name is empty.\" until one is assigned. Assign it with a first NAMED save as soon "
             + "as the world is up: testrig save --target server --save-name <SaveName> --as <id>. Until then "
             + "nothing this server simulates is persisted.");
    }

    // =====================================================================
    // host mode (the wrapper's own body)
    // =====================================================================

    /// <summary>
    /// The host wrapper: starts the game, then relays control-file lines into its stdin.
    /// </summary>
    /// <remarks>
    /// Runs in the WRAPPER process, not the launcher's. It re-validates its own arguments and
    /// re-applies the port defaults, because it is a real entry point that can be invoked
    /// directly (SERVER-066 to SERVER-068).
    /// </remarks>
    public async Task HostModeAsync(
        ServerStartWorld world,
        int gamePort = 0,
        int updatePort = 0,
        CancellationToken ct = default)
    {
        AssertWorldArguments(world);
        if (gamePort <= 0) gamePort = RigConstants.ServerGamePort;
        if (updatePort <= 0) updatePort = RigConstants.ServerUpdatePort;

        // The same guard 'start' applies, repeated here because this IS a real entry point:
        // the wrapper is spawned as a detached process and can also be invoked by hand, so it
        // cannot rely on its caller having checked. Without it a missing install surfaces as a
        // raw CreateProcess failure from a process nobody is watching.
        if (!_fs.FileExists(_paths.Exe))
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                $"The dedicated server is not installed at {_paths.Exe}, so host mode has nothing to start. Run: "
                + "testrig update-game --target server --as <id>");
        }

        var args = new List<string>
        {
            "-batchmode",
            "-nographics",
            "-settingspath", _paths.SettingXml,
            // With -logFile present, ConsoleWindow.Print routes through UnityEngine.Debug.Log
            // into the file, so the log is the complete console record (SERVER-071).
            "-logFile", _paths.LogFile,
            // -settings SavePath is passed HERE and never on a client. That asymmetry is
            // deliberate and load-bearing: it is what puts this half's worlds under
            // data/saves/, and on a client it would make StationeersLaunchPad rewrite the
            // developer's shared modconfig.xml with every Local entry deleted (SERVER-072).
            "-settings", "SavePath", _paths.DataDir,
            "-settings", "GamePort", gamePort.ToString(CultureInfo.InvariantCulture),
            "-settings", "UpdatePort", updatePort.ToString(CultureInfo.InvariantCulture),
            // Loopback only, and no router involvement at all. Neither spec listed these as
            // safety behaviour and both are (SERVER-074, SERVER-077).
            "-settings", "LocalIpAddress", "127.0.0.1",
            "-settings", "AutoSave", "true",
            "-settings", "AutoPauseServer", "false",
            "-settings", "UPNPEnabled", "false",
            "-settings", "ServerName", "Local Test",
            "-settings", "ServerMaxPlayers", "4",
            "-settings", "ServerAuthSecret", "x",
        };

        if (world.IsLoad) args.AddRange(["-load", world.Load!, world.Map!]);
        else args.AddRange(["-new", world.New!]);

        using var game = _launcher.StartGame(_paths.Exe, string.Join('\0', args), _paths.InstallDir);
        PidFiles.Write(_fs, _paths.PidFile, game.Pid, game.StartTimeUtc);

        try
        {
            while (!game.HasExited && !ct.IsCancellationRequested)
            {
                if (_fs.FileExists(_paths.ControlFile))
                {
                    // A brief settle. The writer uses a durable write, so the file is complete
                    // the moment it appears; this is defensive rather than load-bearing.
                    await _sleeper.DelayAsync(TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false);

                    try
                    {
                        var command = _fs.ReadAllText(_paths.ControlFile).Trim();
                        _fs.DeleteFile(_paths.ControlFile);
                        if (command.Length > 0) game.WriteLine(command);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                                   or FileNotFoundException or ObjectDisposedException)
                    {
                        // Locked or already gone. Retried on the next tick.
                    }
                }

                await _sleeper.DelayAsync(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            game.CloseInput();
            PidFiles.Delete(_fs, _paths.PidFile);
            PidFiles.Delete(_fs, _paths.HostPidFile);
            _fs.DeleteFile(_paths.ControlFile);
        }
    }

    // =====================================================================
    // save
    // =====================================================================

    /// <summary>
    /// Queues a named save and waits for the game to confirm it landed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A save name is REQUIRED on this half, and that is not an oversight: the console's save
    /// command takes a name and has no "save under the current name" form, because the
    /// console has no notion of the world's current name to fall back on (SERVER-102).
    /// </para>
    /// <para>
    /// Two independent confirmations, and either is enough. The LOG, anchored and
    /// case-sensitive and accepting the nameless first-time line; and the FILE, because the
    /// stdin channel has two recorded no-op observations and a confirmation that only ever
    /// reads a log cannot tell "the command did nothing" from "the log format moved"
    /// (SERVER-106 fixed).
    /// </para>
    /// <para>
    /// The contract is identical to the client half's and stays identical: confirmed or warn,
    /// never both (SERVER-108).
    /// </para>
    /// </remarks>
    public async Task<bool> SaveAsync(
        string saveName,
        string? callerId = null,
        int waitSeconds = 0,
        CancellationToken ct = default)
    {
        AssertGate("save", callerId);
        if (waitSeconds <= 0) waitSeconds = RigConstants.WaitDefaultSeconds;

        var outcome = await SaveAndConfirmAsync(saveName, waitSeconds, ct).ConfigureAwait(false);

        if (outcome.Confirmed)
        {
            Say($"[Save] Confirmed. ({outcome.Evidence})");
            _output.Value("saveConfirmed", true);
            return true;
        }

        _output.Value("saveConfirmed", false);

        if (outcome.Verdict == SaveVerdict.Failed)
        {
            Warn($"[Save] The server reported the save FAILED: {outcome.Evidence}. Treat this world as NOT "
                 + "saved.");
            return false;
        }

        Warn($"[Save] No confirmation within {waitSeconds}s ({outcome.Evidence}). Treat this world as NOT saved: "
             + "it may have completed silently or failed. testrig logs --target server --grep Saved shows what the "
             + "server actually did, and the stdin channel itself has recorded no-op observations, so also check "
             + $"whether {Path.Combine(_paths.World(saveName), saveName + ".save")} exists.");
        return false;
    }

    /// <summary>Queues the save and waits, without the gate or the reporting.</summary>
    private async Task<SaveOutcome> SaveAndConfirmAsync(string saveName, int waitSeconds, CancellationToken ct)
    {
        var worldDir = _paths.World(saveName);
        var saveFile = Path.Combine(worldDir, saveName + ".save");

        var folderExisted = _fs.DirectoryExists(worldDir);
        var sizeBefore = _fs.FileExists(saveFile) ? _fs.GetFileLength(saveFile) : -1;
        var writtenBefore = _fs.FileExists(saveFile) ? _fs.GetLastWriteTimeUtc(saveFile) : DateTimeOffset.MinValue;

        // Captured BEFORE the command is queued (SERVER-096 fixed). The PowerShell captured
        // it after the rename, so a confirmation written in between was already behind the
        // offset and could never match.
        var watcher = new ServerLogWatcher(_fs, _paths.LogFile);

        await SendCommandAsync($"save \"{saveName}\"", ct).ConfigureAwait(false);
        Say($"[Save] Queued save '{saveName}' on the server. Waiting for confirmation (up to {waitSeconds}s)...");

        var deadline = _clock.UtcNow.AddSeconds(waitSeconds);
        var foreignFailures = new List<string>();

        while (_clock.UtcNow < deadline)
        {
            // THE FILESYSTEM WITNESS RUNS FIRST, and that ordering is the fix, not a tidy-up.
            // It used to run after the line scan, which returned the instant any line
            // classified, so a failure line belonging to somebody else pre-empted the "second,
            // independent witness" this method is built around and the witness was never
            // consulted at all. It is the reliable half: a file that grew is not an opinion.
            if (_fs.FileExists(saveFile))
            {
                var size = _fs.GetFileLength(saveFile);
                var written = _fs.GetLastWriteTimeUtc(saveFile);
                if (sizeBefore < 0 || written > writtenBefore || size != sizeBefore)
                {
                    return new SaveOutcome(
                        SaveVerdict.Confirmed,
                        $"{saveFile} is {size} bytes, written {written:u}");
                }
            }

            foreach (var line in watcher.NewLines())
            {
                var verdict = SaveConfirmation.Classify(line, saveName, folderExisted);
                if (verdict is not null) return verdict;

                // A failure that is not ours (an autosave on a --new world fails every 300 s
                // until a first named save assigns a station name) is kept as evidence and
                // reported if the wait runs out, rather than being turned into this save's
                // verdict.
                if (SaveConfirmation.IsFailureLine(line)) foreignFailures.Add(line.Trim());
            }

            await _sleeper.DelayAsync(PollInterval, ct).ConfigureAwait(false);
        }

        var detail = foreignFailures.Count == 0
            ? $"nothing confirmed within {waitSeconds}s"
            : $"nothing confirmed within {waitSeconds}s; the log did carry {foreignFailures.Count} failure line(s) "
              + $"that name no save of this name, so they were not treated as this save's: {string.Join("; ", foreignFailures.Take(3))}";

        return new SaveOutcome(SaveVerdict.Timeout, detail);
    }

    // =====================================================================
    // stop
    // =====================================================================

    /// <summary>
    /// Tears down the server and its wrapper, and cleans the state files.
    /// </summary>
    /// <remarks>
    /// Does NOT touch the session lock. Used by the stop verb and by the lock's reclaim of a
    /// dead session, which is why it takes its grace period as a parameter rather than
    /// reading one from a launcher scope a reclaim does not have (SERVER-110).
    /// </remarks>
    public async Task TeardownAsync(int graceSeconds = 0, CancellationToken ct = default)
    {
        if (graceSeconds <= 0) graceSeconds = RigConstants.TeardownGraceSeconds;

        var serverPid = ServerPid;
        var hostPid = HostPid;

        if (ServerAlive && WrapperAlive)
        {
            Say("[Stop] Sending 'quit' via host wrapper...");
            try
            {
                await SendCommandAsync("quit", ct).ConfigureAwait(false);
            }
            catch (RigRefusalException ex)
            {
                // A failure to queue the quit warns rather than aborting the teardown: the
                // force-kill below is what actually guarantees the process goes.
                Warn($"[Stop] {ex.Message}");
            }

            var deadline = _clock.UtcNow.AddSeconds(graceSeconds);
            while (_clock.UtcNow < deadline)
            {
                if (!ServerAlive) break;
                await _sleeper.DelayAsync(PollInterval, ct).ConfigureAwait(false);
            }
        }

        if (ServerAlive && serverPid is not null)
        {
            Warn($"[Stop] Server still alive after {graceSeconds}s; force-killing.");
            await ForceKillAsync(serverPid.Value, ct).ConfigureAwait(false);
        }

        // SERVER-115 fixed, spec D-11: the PowerShell killed the wrapper immediately, before
        // its 250 ms poll could notice the game had gone and run its own cleanup, so that
        // finally block was dead code on the normal teardown route and had never been
        // exercised there. A short grace lets it run, which is the only path that closes the
        // game's stdin cleanly.
        if (WrapperAlive)
        {
            var wrapperDeadline = _clock.UtcNow.AddSeconds(2);
            while (_clock.UtcNow < wrapperDeadline && WrapperAlive)
            {
                await _sleeper.DelayAsync(PollInterval, ct).ConfigureAwait(false);
            }
        }

        if (WrapperAlive && hostPid is not null)
        {
            await ForceKillAsync(hostPid.Value, ct).ConfigureAwait(false);
        }

        foreach (var file in new[] { _paths.HostPidFile, _paths.PidFile, _paths.ControlFile })
        {
            PidFiles.Delete(_fs, file);
        }
    }

    /// <summary>
    /// Terminates a pid and waits until it is really gone.
    /// </summary>
    /// <remarks>
    /// The wait is the load-bearing half. Terminating returns as soon as the request is
    /// accepted, and this half then deletes the pid files, so a process still unwinding becomes
    /// an UNTRACKED game process, which is one of the three conditions the state restore
    /// refuses on. That is how a release-time restore came to be skipped after a force-killed
    /// host: nothing was lost, because the both-ends guarantee caught it at the next
    /// acquisition, but the release half never fired. A timeout warns rather than throwing,
    /// because the caller has already decided this process must go.
    /// </remarks>
    private async Task ForceKillAsync(int pid, CancellationToken ct)
    {
        try
        {
            await _processes.StopAsync(pid, RigConstants.ProcessExitGrace, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Warn($"[Stop] Could not stop pid {pid}: {ex.Message}");
            return;
        }

        if (!_processes.IsRunning(pid)) return;

        Warn($"[Stop] PID {pid} was killed but is still in the process table after "
             + $"{RigConstants.ProcessExitGrace.TotalSeconds:0}s. It is untracked from here on, so the state restore will "
             + "refuse until it goes. Check with: testrig status");
    }

    /// <summary>
    /// Stops the dedicated server, optionally saving first.
    /// </summary>
    /// <remarks>
    /// The save confirmation uses the WAIT budget, never the teardown grace. This branch was
    /// the last place in the rig where the two were still conflated: it fed the teardown
    /// grace into a save confirmation, so raising the kill timeout also, silently, raised how
    /// long a save was given to land (SERVER-125).
    /// </remarks>
    public async Task StopAsync(
        string? callerId = null,
        string? saveName = null,
        int teardownSeconds = 0,
        int waitSeconds = 0,
        CancellationToken ct = default)
    {
        if (teardownSeconds <= 0) teardownSeconds = RigConstants.TeardownGraceSeconds;
        if (waitSeconds <= 0) waitSeconds = RigConstants.WaitDefaultSeconds;

        var serverAlive = ServerAlive;
        var wrapperAlive = WrapperAlive;

        if (!serverAlive && !wrapperAlive)
        {
            Say("[Stop] Dedicated server: nothing running.");
            // The state files still go: they outlive their processes on a force-kill, a crash
            // or a reboot, and a stale one makes the next start refuse (SERVER-118).
            foreach (var file in new[] { _paths.HostPidFile, _paths.PidFile, _paths.ControlFile })
            {
                PidFiles.Delete(_fs, file);
            }
            return;
        }

        if (!string.IsNullOrEmpty(saveName) && serverAlive && wrapperAlive)
        {
            Say($"[Stop] Saving as '{saveName}' first...");
            try
            {
                var outcome = await SaveAndConfirmAsync(saveName, waitSeconds, ct).ConfigureAwait(false);
                if (!outcome.Confirmed)
                {
                    Warn($"[Stop] No save confirmation within {waitSeconds}s ({outcome.Evidence}); continuing "
                         + "with quit. Treat that world as NOT saved.");
                }
            }
            catch (RigRefusalException ex)
            {
                Warn($"[Stop] Save failed: {ex.Message}");
            }
        }
        else if (!string.IsNullOrEmpty(saveName))
        {
            Warn("[Stop] --save-name ignored: the server or its host wrapper is not running.");
        }

        await TeardownAsync(teardownSeconds, ct).ConfigureAwait(false);
        Say("[Stop] Dedicated server stopped.");
    }

    // =====================================================================
    // wait
    // =====================================================================

    /// <summary>
    /// Blocks until the server has actually reached a stage, and says so from inside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This asks the process, and it used to infer.</b> The old barrier dropped a minimal
    /// InspectorPlus request into the requests folder and treated its DELETION as proof the
    /// world was loaded and the simulation ticking. Measured 2026-08-15: <c>--new Moon</c> was
    /// rejected by the game with "No such world name", the server ran on with no world at all,
    /// InspectorPlus consumed the probe anyway, and this returned "the world is loaded and the
    /// simulation is ticking". A barrier that reports success when the thing never happened is
    /// worse than no barrier, because every assertion after it is made against a rig nobody
    /// checked.
    /// </para>
    /// <para>
    /// The evidence now is <c>/status.phase</c> from the merged plugin, which loads into this
    /// half and listens on <see cref="ControlPort"/>. That is a statement by the process about
    /// its own game state rather than an inference from a file, and it is the same signal the
    /// client half has always used, so both halves answer the same question the same way.
    /// </para>
    /// <para>
    /// A rejected world name is a FAILURE and not a timeout. The game prints
    /// <c>No such world name: X. Valid worlds: ...</c> once and then runs indefinitely, so
    /// waiting the full budget would report the wrong cause after ten minutes. The log is
    /// scanned on every poll and that line ends the wait immediately, carrying the game's own
    /// list of what it would have accepted.
    /// </para>
    /// <para>
    /// <b>It takes the caller id and refreshes the lock (SERVER-140 fixed, spec D-01).</b>
    /// Both CLAUDE.md and MANUAL.md state that wait refreshes a lock you hold; the client half
    /// did it and this half did not, so the documented
    /// <c>wait --target server --stage inWorld --wait-seconds 600</c> was a ten-minute
    /// blocking wait against a ten-minute TTL on a rig that is by definition not busy.
    /// </para>
    /// </remarks>
    public async Task<bool> WaitAsync(
        ReadinessStage stage = ReadinessStage.InWorld,
        string? callerId = null,
        int waitSeconds = 0,
        CancellationToken ct = default)
    {
        if (waitSeconds <= 0) waitSeconds = RigConstants.WaitDefaultSeconds;

        _lock.RefreshIfMine(callerId);
        var lastRefresh = _clock.UtcNow;

        if (stage == ReadinessStage.Process)
        {
            var processDeadline = _clock.UtcNow.AddSeconds(waitSeconds);
            while (_clock.UtcNow < processDeadline)
            {
                if (ServerAlive)
                {
                    Say("[Wait] Dedicated server process is up.");
                    return true;
                }
                await _sleeper.DelayAsync(PollInterval, ct).ConfigureAwait(false);
                lastRefresh = MaybeRefresh(callerId, lastRefresh);
            }

            throw new RigRefusalException(
                RigRefusalKind.Refused,
                $"[Wait] The dedicated server process did not come up within {waitSeconds}s. Inspect "
                + $"{_paths.LogFile}.");
        }

        if (!ServerAlive)
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                "[Wait] The dedicated server is not running, so there is no world to wait for. Start it first: "
                + "testrig start --target server --as <id> --new <Map>");
        }

        Say($"[Wait] Polling the server's control plane on 127.0.0.1:{ControlPort} for stage "
            + $"'{ReadinessStages.Name(stage)}', up to {waitSeconds}s.");

        var deadline = _clock.UtcNow.AddSeconds(waitSeconds);
        var everAnswered = false;
        var lastPhase = "(never answered)";
        var lastError = "";

        while (_clock.UtcNow < deadline)
        {
            // Before the status read, every time: the rejection is printed once, early, and
            // then nothing changes for as long as anybody is willing to wait.
            AssertWorldNameWasAccepted();

            if (!ServerAlive)
            {
                throw new RigRefusalException(
                    RigRefusalKind.Refused,
                    $"[Wait] The dedicated server exited while waiting for '{ReadinessStages.Name(stage)}'. "
                    + $"Inspect {_paths.LogFile}.");
            }

            var (status, error) = await StatusAsync(5, ct).ConfigureAwait(false);
            if (status is not null)
            {
                everAnswered = true;
                lastPhase = string.IsNullOrEmpty(status.Phase) ? "(no phase)" : status.Phase;

                if (ReadinessStages.Reached(status, stage))
                {
                    Say($"[Wait] The server reports phase '{lastPhase}': "
                        + $"'{ReadinessStages.Name(stage)}' reached.");
                    _output.Value("serverPhase", lastPhase);
                    return true;
                }
            }
            else
            {
                lastError = error;
            }

            await _sleeper.DelayAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            lastRefresh = MaybeRefresh(callerId, lastRefresh);
        }

        // Two genuinely different failures, named apart rather than merged into one sentence
        // that has to cover both: nothing ever answered (the plugin is missing), or it
        // answered and never got there (the world is slow, or there is no world).
        throw new RigRefusalException(
            RigRefusalKind.Refused,
            everAnswered
                ? $"[Wait] The dedicated server did not reach '{ReadinessStages.Name(stage)}' within "
                  + $"{waitSeconds}s; its control plane last reported phase '{lastPhase}'. A populated save takes "
                  + $"minutes to load. If the phase is not moving, read the log for what it is doing: "
                  + "testrig logs --target server --tail 40"
                : $"[Wait] Nothing answered on 127.0.0.1:{ControlPort} within {waitSeconds}s"
                  + (string.IsNullOrEmpty(lastError) ? "" : $" (last error: {lastError})")
                  + ". The server process is up, so the TestRig plugin is either not deployed or did not load, "
                  + "and without it nothing here can prove a world is loaded. Deploy it and restart the server: "
                  + "testrig deploy TestRig --target server --as <id>. Do not fall back to inferring readiness "
                  + "from an InspectorPlus probe being consumed: measured, that happens with no world loaded at "
                  + "all.");
    }

    /// <summary>
    /// Fails at once when the game rejected the world name it was started with.
    /// </summary>
    /// <remarks>
    /// The game logs <c>No such world name: X. Valid worlds: ...</c> and then keeps running,
    /// forever, with no world. There is no other outward sign: the process is up, the control
    /// plane answers, and the phase simply never becomes <c>inWorld</c>. Carrying the game's
    /// own list into the refusal is what makes this an answer rather than a timeout.
    /// </remarks>
    private void AssertWorldNameWasAccepted()
    {
        if (!_fs.FileExists(_paths.LogFile)) return;

        string? rejection = null;
        try
        {
            foreach (var line in _fs.ReadLines(_paths.LogFile))
            {
                if (line.Contains(ServerWorlds.RejectionMarker, StringComparison.Ordinal)) rejection = line;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A log that cannot be read is not evidence of anything. The wait continues and
            // fails on its own budget if the world genuinely never loads.
            return;
        }

        if (rejection is null) return;

        throw new RigRefusalException(
            RigRefusalKind.Refused,
            "[Wait] The game REJECTED the world name this server was started with, so there is no world and "
            + "there never will be; the process stays up regardless. Its own words:\n\n  "
            + rejection.Trim()
            + $"\n\nStop it and start again with a world from that list: testrig stop --target server --as <id>, "
            + "then testrig start --target server --new <Map> --as <id>. The launcher validates --new against the "
            + "install before launching, so a name that got this far was either typed against an older binary or "
            + "the world catalogue could not be read.");
    }

    /// <summary>Refreshes at most once a minute, against a ten-minute TTL.</summary>
    private DateTimeOffset MaybeRefresh(string? callerId, DateTimeOffset lastRefresh)
    {
        if (_clock.UtcNow - lastRefresh < TimeSpan.FromMinutes(1)) return lastRefresh;
        _lock.RefreshIfMine(callerId);
        return _clock.UtcNow;
    }
}
