using TestRig.Core.Rig;
using TestRig.Core.Server;
using TestRig.Core.Session;
using Xunit;

namespace TestRig.Tests.Server;

/// <summary>
/// The dedicated server: starting into a world, the stdin channel, saving, stopping, waiting.
/// </summary>
public sealed class ServerLifecycleTests
{
    private static ServerStartWorld Load(string save, string map) => new(save, map, null);

    private static ServerStartWorld New(string map) => new(null, null, map);

    // =====================================================================
    // start
    // =====================================================================

    [Fact]
    public void StartIsGatedAndRefusesWhenTheServerIsNotInstalled()
    {
        var fixture = new ServerFixture();
        Assert.Throws<RigRefusalException>(() => fixture.Start(New("Mars")));

        var owner = fixture.Lease();
        var ex = Assert.Throws<RigRefusalException>(() => fixture.Start(New("Mars"), owner));
        Assert.Contains("not installed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("testrig update-game --target server", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadAndNewAreMutuallyExclusiveAndLoadNeedsAMap()
    {
        var fixture = new ServerFixture().Installed();
        var owner = fixture.Lease();

        Assert.Contains("not both",
            Assert.Throws<RigRefusalException>(() => fixture.Start(new ServerStartWorld("A", "Mars", "Mars"), owner)).Message,
            StringComparison.Ordinal);

        Assert.Contains("--load requires --map",
            Assert.Throws<RigRefusalException>(() => fixture.Start(new ServerStartWorld("A", null, null), owner)).Message,
            StringComparison.Ordinal);

        Assert.Contains("Missing --load or --new",
            Assert.Throws<RigRefusalException>(() => fixture.Start(new ServerStartWorld(null, null, null), owner)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingSaveFolderIsRefusedAndTheDeveloperIsNamedAsTheSaveManager()
    {
        var fixture = new ServerFixture().Installed();
        var owner = fixture.Lease();

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Start(Load("Ghost", "Mars"), owner));
        Assert.Contains("sole save manager", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--new <Map>", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFolderWithNoSaveFileIsRefusedBecauseTheGameWouldStartANEWEMPTYWORLD()
    {
        // SERVER-053, spec D-02. The PowerShell checked only that the folder existed, so a
        // folder with no .save in it made the game start a brand new empty world under that
        // name while the operator believed a populated save had loaded.
        var fixture = new ServerFixture().Installed();
        var owner = fixture.Lease();
        fixture.Fs.AddDirectory(fixture.Paths.World("Luna"));

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Start(Load("Luna", "Mars"), owner));
        Assert.Contains("no .save file", ex.Message, StringComparison.Ordinal);
        Assert.Contains("EMPTY", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMismatchedBasenameIsRefusedBecauseTheGameMatchesOnTheFoldersOwnName()
    {
        var fixture = new ServerFixture().Installed();
        var owner = fixture.Lease();
        fixture.Fs.AddFile(Path.Combine(fixture.Paths.World("Luna"), "LunaOld.save"), "world");

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Start(Load("Luna", "Mars"), owner));
        Assert.Contains("no Luna.save in it", ex.Message, StringComparison.Ordinal);
        Assert.Contains("LunaOld.save", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoSaveFilesLoadTheMatchingOneAndSayTheOthersAreIgnored()
    {
        var fixture = new ServerFixture().Installed().World("Luna");
        var owner = fixture.Lease();
        fixture.Fs.AddFile(Path.Combine(fixture.Paths.World("Luna"), "Luna_backup.save"), "old");

        fixture.Half.AssertSaveIsLoadable("Luna");
        Assert.True(fixture.Output.Warned("more than one .save file"));
        Assert.True(fixture.Output.Warned("Luna.save is the one that will be loaded"));
    }

    [Fact]
    public void ANewWorldWarnsThatNothingWillAutosaveUntilItHasBeenSavedByName()
    {
        // SERVER-081, spec D-03. A -new world has an empty CurrentStationName, so every
        // autosave fails with "Save Failed: Folder name is empty." until a first NAMED save
        // assigns one, and the PowerShell offered --new with no warning at all.
        var fixture = new ServerFixture().Installed();
        var owner = fixture.Lease();

        fixture.Start(New("Mars"), owner);

        Assert.True(fixture.Output.Warned("Folder name is empty"));
        Assert.True(fixture.Output.Warned("testrig save --target server --save-name"));
    }

    [Fact]
    public void AnAlreadyRunningServerIsRefusedWithBothPids()
    {
        var fixture = new ServerFixture().Installed().Running();
        var owner = fixture.Lease();

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Start(New("Mars"), owner));
        Assert.Contains("9100", ex.Message, StringComparison.Ordinal);
        Assert.Contains("9101", ex.Message, StringComparison.Ordinal);
        Assert.Contains("testrig stop --target server", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWrapperReInvokesTheLauncherInHostModeWithEveryArgument()
    {
        var fixture = new ServerFixture().Installed().World("Luna");
        var owner = fixture.Lease();

        fixture.Start(Load("Luna", "Mars"), owner, gamePort: 28116, updatePort: 28115);

        var wrapper = Assert.Single(fixture.Launcher.Wrappers);
        Assert.Equal(ServerFixture.LauncherPath, wrapper.Exe);
        Assert.Equal(fixture.Paths.Root, wrapper.WorkingDirectory);
        Assert.Equal(
            ["host-mode", "--game-port", "28116", "--update-port", "28115", "--load", "Luna", "--map", "Mars"],
            wrapper.Arguments);
    }

    [Fact]
    public void ThePortsDefaultToTheSharedConstants()
    {
        var fixture = new ServerFixture().Installed();
        var owner = fixture.Lease();

        fixture.Start(New("Mars"), owner);

        var wrapper = Assert.Single(fixture.Launcher.Wrappers);
        Assert.Contains(RigConstants.ServerGamePort.ToString(System.Globalization.CultureInfo.InvariantCulture), wrapper.Arguments);
        Assert.Contains(RigConstants.ServerUpdatePort.ToString(System.Globalization.CultureInfo.InvariantCulture), wrapper.Arguments);
    }

    [Fact]
    public void TheRegistrationBarrierSaysPlainlyThatUpIsNotReady()
    {
        var fixture = new ServerFixture().Installed();
        var owner = fixture.Lease();

        fixture.Start(New("Mars"), owner);

        Assert.True(fixture.Output.Said("The process being up is NOT the world being ready"));
        Assert.True(fixture.Output.Said("testrig wait --target server --stage inWorld"));
    }

    [Fact]
    public void AWrapperThatDiesBeforeTheServerRegistersNamesTheLog()
    {
        var fixture = new ServerFixture().Installed();
        var owner = fixture.Lease();
        // A wrapper that dies on boot: nothing registers the game, and nothing registers
        // the wrapper either.
        fixture.Launcher.AfterWrapperStarted = null;
        fixture.Launcher.Processes = null;

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Start(New("Mars"), owner));
        Assert.Contains("exited before the server registered", ex.Message, StringComparison.Ordinal);
        Assert.Contains(fixture.Paths.LogFile, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StaleStateFilesAreClearedBeforeTheWrapperIsLaunched()
    {
        var fixture = new ServerFixture().Installed();
        var owner = fixture.Lease();
        fixture.Fs.AddFile(fixture.Paths.ControlFile, "quit");

        fixture.Start(New("Mars"), owner);
        Assert.False(fixture.Fs.FileExists(fixture.Paths.ControlFile));
    }

    // =====================================================================
    // host mode: the wrapper's own body
    // =====================================================================

    [Fact]
    public void TheGameIsLaunchedHeadlessIntoItsWorldWithEverySafetySetting()
    {
        var fixture = new ServerFixture().Installed().World("Luna");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        fixture.HostMode(Load("Luna", "Mars"), cts.Token);

        var game = Assert.Single(fixture.Launcher.Games);
        Assert.Equal(fixture.Paths.Exe, game.Exe);
        Assert.Equal(fixture.Paths.InstallDir, game.WorkingDirectory);

        var args = game.Arguments;
        Assert.Contains("-batchmode", args);
        Assert.Contains("-nographics", args);
        Assert.Contains("-logFile", args);

        // The server DOES pass SavePath, and the asymmetry with the client half is deliberate.
        AssertSetting(args, "SavePath", fixture.Paths.DataDir);
        // Loopback only, and no router involvement at all.
        AssertSetting(args, "LocalIpAddress", "127.0.0.1");
        AssertSetting(args, "UPNPEnabled", "false");
        AssertSetting(args, "AutoSave", "true");
        AssertSetting(args, "AutoPauseServer", "false");
        AssertSetting(args, "ServerName", "Local Test");
        AssertSetting(args, "ServerMaxPlayers", "4");
        AssertSetting(args, "ServerAuthSecret", "x");

        var loadAt = Array.IndexOf(args, "-load");
        Assert.True(loadAt >= 0);
        Assert.Equal("Luna", args[loadAt + 1]);
        Assert.Equal("Mars", args[loadAt + 2]);
    }

    [Fact]
    public void ANewWorldTakesTheNewFlagInstead()
    {
        var fixture = new ServerFixture().Installed();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        fixture.HostMode(New("Europa"), cts.Token);

        var args = Assert.Single(fixture.Launcher.Games).Arguments;
        var newAt = Array.IndexOf(args, "-new");
        Assert.True(newAt >= 0);
        Assert.Equal("Europa", args[newAt + 1]);
        Assert.DoesNotContain("-load", args);
    }

    [Fact]
    public void TheWrapperWritesTheGamePidWithItsStartTimeAndCleansUpOnExit()
    {
        var fixture = new ServerFixture().Installed();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        fixture.HostMode(New("Mars"), cts.Token);

        // The finally block runs on the way out, which is the path the PowerShell's own
        // cleanup never reached on a normal teardown.
        Assert.False(fixture.Fs.FileExists(fixture.Paths.PidFile));
        Assert.False(fixture.Fs.FileExists(fixture.Paths.HostPidFile));
        Assert.True(fixture.Launcher.LastGame!.InputClosed);
    }

    [Fact]
    public void TheWrapperRelaysAControlFileIntoTheGamesStdinAndDeletesIt()
    {
        var fixture = new ServerFixture().Installed();
        fixture.Fs.AddFile(fixture.Paths.ControlFile, "save \"Luna\"");

        using var cts = new CancellationTokenSource();
        // One poll: the loop reads the control file, then the cancellation ends it.
        fixture.Client.Rig.Sleeper.OnDelay = n => { if (n >= 2) cts.Cancel(); };

        fixture.HostMode(New("Mars"), cts.Token);

        Assert.Equal(["save \"Luna\""], fixture.Launcher.LastGame!.StdIn);
        Assert.False(fixture.Fs.FileExists(fixture.Paths.ControlFile));
    }

    // =====================================================================
    // the stdin channel
    // =====================================================================

    [Fact]
    public void SendingRefusesWhenTheGameOrTheWrapperIsGone()
    {
        var fixture = new ServerFixture().Installed();
        var owner = fixture.Lease();

        Assert.Contains("Server is not running",
            Assert.Throws<RigRefusalException>(() => fixture.Send("quit", owner)).Message, StringComparison.Ordinal);

        fixture.Processes.Add(9101, RigConstants.ServerImageName, fixture.Clock.UtcNow);
        PidFiles.Write(fixture.Fs, fixture.Paths.PidFile, 9101, fixture.Clock.UtcNow);

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Send("quit", owner));
        Assert.Contains("cannot relay commands", ex.Message, StringComparison.Ordinal);
        Assert.Contains("orphaned server", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACommandIsWrittenDurablySoTheWrapperNeverReadsAPartialWrite()
    {
        var fixture = new ServerFixture().Installed().Running();
        var owner = fixture.Lease();

        fixture.Send("save \"Luna\"", owner);

        Assert.Equal("save \"Luna\"", fixture.Fs.ReadAllText(fixture.Paths.ControlFile));
        Assert.Contains(fixture.Fs.DurableWrites, w => w.Equals(fixture.Paths.ControlFile, StringComparison.OrdinalIgnoreCase));
        Assert.True(fixture.Output.Said("Queued on the server's stdin"));
    }

    [Fact]
    public void APendingCommandThatIsNeverConsumedIsReportedRatherThanOverwritten()
    {
        var fixture = new ServerFixture().Installed().Running();
        var owner = fixture.Lease();
        fixture.Fs.AddFile(fixture.Paths.ControlFile, "an earlier command");

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Send("quit", owner));
        Assert.Contains("still pending", ex.Message, StringComparison.Ordinal);
        Assert.Equal("an earlier command", fixture.Fs.ReadAllText(fixture.Paths.ControlFile));
    }

    // =====================================================================
    // save
    // =====================================================================

    [Fact]
    public void ANamedConfirmationInTheLogConfirmsTheSave()
    {
        var fixture = new ServerFixture().Installed().Running().World("Luna");
        var owner = fixture.Lease();
        fixture.Log("starting up");

        fixture.Client.Rig.Sleeper.OnDelay = _ => fixture.Log("Saved Luna");

        Assert.True(fixture.Save("Luna", owner, 30));
        Assert.True(fixture.Output.Said("[Save] Confirmed."));
    }

    [Fact]
    public void ABracketedSourcePrefixCanNEVERConfirmASave()
    {
        // Spec D-06. The PowerShell matched "Saved.*<name>" anywhere in a line, so
        // "[Station Notepad] Saved file system to ..." confirmed a save named 'notepad', and
        // any line mentioning both words confirmed names like 'json' or 'install'.
        Assert.False(SaveConfirmation.IsNamedConfirmation("[Station Notepad] Saved file system to disk", "notepad"));
        Assert.False(SaveConfirmation.IsNamedConfirmation("[Station Notepad] Saved file system to disk", "json"));
        Assert.False(SaveConfirmation.IsNamedConfirmation("something Saved Luna something", "Luna"));
        Assert.True(SaveConfirmation.IsNamedConfirmation("Saved Luna", "Luna"));
    }

    [Fact]
    public void TheConfirmationIsCaseSensitiveAndTheNameMustBeTheWholeRemainder()
    {
        Assert.False(SaveConfirmation.IsNamedConfirmation("saved Luna", "Luna"));
        Assert.False(SaveConfirmation.IsNamedConfirmation("Saved LunaBackup", "Luna"));
        Assert.True(SaveConfirmation.IsNamedConfirmation("Saved Luna.", "Luna"));
        Assert.True(SaveConfirmation.IsNamedConfirmation("12:04:55 Saved Luna", "Luna"));
    }

    [Fact]
    public void AFirstTimeSaveIsConfirmedByTheNamelessLineOnlyWhenTheFolderIsNew()
    {
        // Spec D-05. A save into a folder that does not exist yet goes down NewSaveTask and
        // prints "Created new save", which carries no name at all, so the most common rig
        // operation ALWAYS reported a false warning.
        Assert.NotNull(SaveConfirmation.Classify("Created new save", "Luna", folderExistedBefore: false));
        Assert.Null(SaveConfirmation.Classify("Created new save", "Luna", folderExistedBefore: true));
    }

    [Fact]
    public void TheGamesOwnFailureLinesEndTheWaitImmediately()
    {
        foreach (var line in SaveConfirmation.FailureMarkers)
        {
            var outcome = SaveConfirmation.Classify($"{line}: Folder name is empty.", "Luna", false);
            Assert.NotNull(outcome);
            Assert.Equal(SaveVerdict.Failed, outcome!.Verdict);
        }
    }

    [Fact]
    public void TheFileOnDiskIsASECONDINDEPENDENTWITNESS()
    {
        // The stdin channel has two recorded no-op observations at two game versions, so a
        // confirmation that only ever reads a log cannot tell "the command did nothing" from
        // "the log format moved".
        var fixture = new ServerFixture().Installed().Running();
        var owner = fixture.Lease();
        fixture.Log("starting up");

        fixture.Client.Rig.Sleeper.OnDelay = _ =>
            fixture.Fs.AddFile(Path.Combine(fixture.Paths.World("Luna"), "Luna.save"), "world bytes");

        Assert.True(fixture.Save("Luna", owner, 30));
        Assert.True(fixture.Output.Said("Luna.save"));
    }

    [Fact]
    public void AServerThatSaysNothingWarnsAndNamesBothPlacesToLook()
    {
        var fixture = new ServerFixture().Installed().Running();
        var owner = fixture.Lease();
        fixture.Log("nothing relevant");

        Assert.False(fixture.Save("Luna", owner, 4));
        Assert.True(fixture.Output.Warned("NOT saved"));
        Assert.True(fixture.Output.Warned("testrig logs --target server --grep Saved"));
        Assert.True(fixture.Output.Warned("Luna.save"));
    }

    [Fact]
    public void AReportedFailureIsDistinguishedFromATimeout()
    {
        var fixture = new ServerFixture().Installed().Running();
        var owner = fixture.Lease();
        fixture.Log("starting up");
        fixture.Client.Rig.Sleeper.OnDelay = _ => fixture.Log("Save Failed: Folder name is empty.");

        Assert.False(fixture.Save("Luna", owner, 30));
        Assert.True(fixture.Output.Warned("reported the save FAILED"));
    }

    [Fact]
    public void TheLogBaselineIsCapturedBEFORETheCommandIsQueued()
    {
        // SERVER-096: the PowerShell captured it after the rename, so a confirmation written in
        // between was already behind the offset and could never match. Here the confirmation
        // appears in the same instant the command is queued.
        var fixture = new ServerFixture().Installed().Running();
        var owner = fixture.Lease();
        fixture.Log("before the save");

        // The line lands the moment the control file is written, which is inside SendCommand.
        fixture.Client.Rig.Sleeper.OnDelay = _ => fixture.Log("Saved Luna");

        Assert.True(fixture.Save("Luna", owner, 30));
    }

    // =====================================================================
    // stop
    // =====================================================================

    [Fact]
    public void NothingRunningStillClearsTheThreeStateFiles()
    {
        var fixture = new ServerFixture().Installed();
        fixture.Fs.AddFile(fixture.Paths.PidFile, "1");
        fixture.Fs.AddFile(fixture.Paths.HostPidFile, "2");
        fixture.Fs.AddFile(fixture.Paths.ControlFile, "quit");

        fixture.Stop();

        Assert.True(fixture.Output.Said("nothing running"));
        Assert.False(fixture.Fs.FileExists(fixture.Paths.PidFile));
        Assert.False(fixture.Fs.FileExists(fixture.Paths.HostPidFile));
        Assert.False(fixture.Fs.FileExists(fixture.Paths.ControlFile));
    }

    [Fact]
    public void ASaveNameWithNothingRunningIsReportedAsIgnored()
    {
        var fixture = new ServerFixture().Installed();
        fixture.Processes.Add(9101, RigConstants.ServerImageName, fixture.Clock.UtcNow);
        PidFiles.Write(fixture.Fs, fixture.Paths.PidFile, 9101, fixture.Clock.UtcNow);

        fixture.Stop(saveName: "Luna");
        Assert.True(fixture.Output.Warned("--save-name ignored"));
    }

    [Fact]
    public void AQuitIsQueuedThroughTheWrapperBeforeAnythingIsKilled()
    {
        var fixture = new ServerFixture().Installed().Running();
        fixture.Client.Rig.Sleeper.OnDelay = _ =>
        {
            fixture.Processes.Kill(9101);
            fixture.Processes.Kill(9100);
        };

        fixture.Stop();

        Assert.True(fixture.Output.Said("Sending 'quit' via host wrapper"));
        Assert.DoesNotContain(9101, fixture.Processes.StopRequests);
    }

    [Fact]
    public void AServerThatIgnoresTheQuitIsForceKilledAndTheWarningNamesTheGrace()
    {
        var fixture = new ServerFixture().Installed().Running();

        fixture.Stop(teardownSeconds: 4);

        Assert.True(fixture.Output.Warned("still alive after 4s"));
        Assert.Contains(9101, fixture.Processes.StopRequests);
        Assert.False(fixture.Fs.FileExists(fixture.Paths.PidFile));
    }

    [Fact]
    public void TheWrapperIsGivenAMomentToRunItsOwnCleanupBeforeItIsKilled()
    {
        // SERVER-115, spec D-11: the PowerShell killed the wrapper immediately, before its
        // 250 ms poll could notice the game had gone, so its finally block was dead code on
        // the normal teardown route and had never been exercised there.
        var fixture = new ServerFixture().Installed().Running();
        var wrapperKilledAt = -1;
        var polls = 0;

        fixture.Client.Rig.Sleeper.OnDelay = _ =>
        {
            polls++;
            fixture.Processes.Kill(9101);
            if (polls >= 3 && wrapperKilledAt < 0)
            {
                wrapperKilledAt = polls;
                fixture.Processes.Kill(9100);
            }
        };

        fixture.Stop(teardownSeconds: 10);

        Assert.True(wrapperKilledAt > 0, "the wrapper was never given a chance to exit on its own");
        Assert.DoesNotContain(9100, fixture.Processes.StopRequests);
    }

    [Fact]
    public void TheSaveConfirmationUsesTheWaitBudgetAndNeverTheTeardownGrace()
    {
        // SERVER-125: this branch was the last place where the two were conflated, so raising
        // the kill timeout also silently raised how long a save was given to land.
        var fixture = new ServerFixture().Installed().Running();
        fixture.Log("starting up");

        var start = fixture.Clock.UtcNow;
        fixture.Stop(saveName: "Luna", teardownSeconds: 4, waitSeconds: 20);

        // The save wait ran for its own 20 seconds, not the teardown's 4.
        Assert.True((fixture.Clock.UtcNow - start).TotalSeconds >= 20);
        Assert.True(fixture.Output.Warned("No save confirmation within 20s"));
    }

    // =====================================================================
    // wait
    // =====================================================================

    [Fact]
    public void TheProcessStageIsExplicitlyNotReadiness()
    {
        var fixture = new ServerFixture().Installed().Running();
        Assert.True(fixture.Wait(ReadinessStage.Process));
        Assert.True(fixture.Output.Said("process is up"));
        Assert.False(fixture.Output.Said("simulation is ticking"));
    }

    [Fact]
    public void WaitingForAWorldOnAServerThatIsNotRunningNamesTheStartCommand()
    {
        var fixture = new ServerFixture().Installed();
        var ex = Assert.Throws<RigRefusalException>(() => fixture.Wait());
        Assert.Contains("no world to wait for", ex.Message, StringComparison.Ordinal);
        Assert.Contains("testrig start --target server", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProbeCarriesAGuidFragmentSoTwoWaitsCannotDeleteEachOthers()
    {
        var fixture = new ServerFixture().Installed().Running();
        var seen = new List<string>();
        fixture.Client.Rig.Sleeper.OnDelay = _ =>
        {
            foreach (var file in fixture.Fs.AllFiles())
            {
                if (file.Contains("testrig-ready-", StringComparison.Ordinal) && !seen.Contains(file)) seen.Add(file);
            }
            foreach (var file in seen) fixture.Fs.DeleteFile(file);
        };

        Assert.True(fixture.Wait(ReadinessStage.InWorld, waitSeconds: 30));

        var first = Assert.Single(seen);
        Assert.Matches(@"testrig-ready-[0-9a-f]{8}\.json$", first);
    }

    [Fact]
    public void ConsumingTheProbeIsTheReadinessSignal()
    {
        var fixture = new ServerFixture().Installed().Running();
        fixture.Client.Rig.Sleeper.OnDelay = _ =>
        {
            foreach (var file in fixture.Fs.AllFiles().Where(f => f.Contains("testrig-ready-", StringComparison.Ordinal)))
            {
                fixture.Fs.DeleteFile(file);
            }
        };

        Assert.True(fixture.Wait(ReadinessStage.InWorld, waitSeconds: 30));
        Assert.True(fixture.Output.Said("the world is loaded and the simulation is ticking"));
    }

    [Fact]
    public void AnUnconsumedProbeNamesAllThreeCausesRatherThanAssumingOne()
    {
        // An unconsumed probe and a slow world look identical from out here.
        var fixture = new ServerFixture().Installed().Running();

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Wait(ReadinessStage.InWorld, waitSeconds: 6));

        Assert.Contains("still loading", ex.Message, StringComparison.Ordinal);
        Assert.Contains("InspectorPlus is not deployed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Force Unpause Without Client", ex.Message, StringComparison.Ordinal);
        Assert.Contains("net.inspectorplus.cfg", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProbeIsDeletedWhateverHappens()
    {
        var fixture = new ServerFixture().Installed().Running();
        Assert.Throws<RigRefusalException>(() => fixture.Wait(ReadinessStage.InWorld, waitSeconds: 6));

        Assert.DoesNotContain(fixture.Fs.AllFiles(), f => f.Contains("testrig-ready-", StringComparison.Ordinal));
    }

    [Fact]
    public void AServerThatExitsWhileTheProbeIsPendingIsReportedAsSuch()
    {
        var fixture = new ServerFixture().Installed().Running();
        fixture.Client.Rig.Sleeper.OnDelay = _ => fixture.Processes.Kill(9101);

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Wait(ReadinessStage.InWorld, waitSeconds: 30));
        Assert.Contains("exited while the readiness probe was pending", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WaitOnTHISHALFRefreshesALockYouHold()
    {
        // SERVER-140, spec D-01. Both CLAUDE.md and MANUAL.md state that wait refreshes a lock
        // you hold; the client half did it and this half did NOT, so the documented ten-minute
        // wait ran against a ten-minute TTL on a rig that is by definition not busy.
        var fixture = new ServerFixture().Installed().Running();
        var owner = fixture.Lease();

        fixture.Clock.AdvanceMinutes(3);
        var before = fixture.Client.Rig.ReadLockFile()!.GetOrEmpty(LockFields.RefreshedAt);

        fixture.Client.Rig.Sleeper.OnDelay = _ =>
        {
            foreach (var file in fixture.Fs.AllFiles().Where(f => f.Contains("testrig-ready-", StringComparison.Ordinal)))
            {
                fixture.Fs.DeleteFile(file);
            }
        };

        Assert.True(fixture.Wait(ReadinessStage.InWorld, owner, 30));
        Assert.NotEqual(before, fixture.Client.Rig.ReadLockFile()!.GetOrEmpty(LockFields.RefreshedAt));
    }

    private static void AssertSetting(string[] args, string name, string value)
    {
        for (var i = 0; i + 2 < args.Length; i++)
        {
            if (args[i] == "-settings" && args[i + 1] == name && args[i + 2] == value) return;
        }
        Assert.Fail($"-settings {name} {value} is not on the command line: {string.Join(" ", args)}");
    }
}
