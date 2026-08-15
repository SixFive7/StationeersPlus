using TestRig.Contracts;
using TestRig.Core.Rig;
using TestRig.Core.Server;
using TestRig.Core.Session;
using TestRig.Tests.Client;
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
    public void TheGamesOwnFailureLinesEndTheWaitImmediatelyWhenTheyAreOurs()
    {
        foreach (var marker in SaveConfirmation.FailureMarkers)
        {
            var outcome = SaveConfirmation.Classify($"{marker}: Luna is locked by another process.", "Luna", false);
            Assert.NotNull(outcome);
            Assert.Equal(SaveVerdict.Failed, outcome!.Verdict);

            // Anchored, like the confirmation beside it. An unanchored match lets a bracketed
            // source prefix decide a verdict.
            Assert.False(SaveConfirmation.IsFailureLine($"[Station Notepad] {marker}: Luna"));
            Assert.True(SaveConfirmation.IsFailureLine($"12:04:55 {marker}: Luna"));
        }
    }

    [Fact]
    public void AnAutosaveFailureOnANewWorldIsNotThisSavesFailure()
    {
        // Measured 2026-08-14 on the real server. A --new world has no station name, so its
        // autosave fails every 300 s with exactly this line. The unanchored, unscoped IsFailure
        // attributed one of them to a manual save that had in fact printed nothing at all, and
        // reported "the server reported the save FAILED" for a save whose outcome was unknown.
        const string autosave = "Save Failed: Folder name is empty.";

        Assert.True(SaveConfirmation.IsFailureLine(autosave));
        Assert.False(SaveConfirmation.IsFailureOf(autosave, "Luna"));
        Assert.Null(SaveConfirmation.Classify(autosave, "Luna", folderExistedBefore: false));

        // A failure that names our save is still ours, so the early-out survives.
        Assert.True(SaveConfirmation.IsFailureOf("Save Failed: Luna could not be written.", "Luna"));
    }

    [Fact]
    public void AForeignFailureLineDoesNotPreEmptTheFilesystemWitness()
    {
        // The line scan used to run BEFORE the filesystem check and return on the first match,
        // so a foreign failure line meant the second, independent witness was never consulted.
        var fixture = new ServerFixture().Installed().Running();
        var owner = fixture.Lease();
        fixture.Log("starting up");

        fixture.Client.Rig.Sleeper.OnDelay = _ =>
        {
            fixture.Log("Save Failed: Folder name is empty.");
            fixture.Fs.AddFile(Path.Combine(fixture.Paths.World("Luna"), "Luna.save"), "world bytes");
        };

        Assert.True(fixture.Save("Luna", owner, 30));
        Assert.True(fixture.Output.Said("[Save] Confirmed."));
        Assert.False(fixture.Output.Warned("reported the save FAILED"));
    }

    [Fact]
    public void AnUnattributableFailureIsCarriedIntoTheTimeoutReportRatherThanBecomingTheVerdict()
    {
        var fixture = new ServerFixture().Installed().Running();
        var owner = fixture.Lease();
        fixture.Log("starting up");
        fixture.Client.Rig.Sleeper.OnDelay = _ => fixture.Log("Save Failed: Folder name is empty.");

        Assert.False(fixture.Save("Luna", owner, 4));
        Assert.False(fixture.Output.Warned("reported the save FAILED"));
        Assert.True(fixture.Output.Warned("No confirmation within"));
        Assert.True(fixture.Output.Warned("Folder name is empty"));
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
        fixture.Client.Rig.Sleeper.OnDelay = _ => fixture.Log("Save Failed: Luna could not be written.");

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
        // Genuinely nothing running: no game process to hold a world, and no wrapper either.
        // A save cannot be delivered by any channel, so the name cannot be honoured and
        // saying so out loud is the whole of the correct behaviour. A dropped save that
        // reports nothing is how a caller learns afterwards that the world went.
        //
        // This test used to plant a LIVE game process with a dead wrapper and call that
        // "nothing running". That is the ORPHANED server, it is saveable through the
        // control plane, and it is the case below.
        var fixture = new ServerFixture().Installed();

        fixture.Stop(saveName: "Luna");

        Assert.True(fixture.Output.Warned("--save-name ignored"));
        Assert.True(fixture.Output.Said("nothing running"));
    }

    [Fact]
    public void AnOrphanedServerWhosePlaneAnswersIsSavedRatherThanHavingTheSaveNameDropped()
    {
        // The wrapper was the test only because the save went out over the wrapper's stdin
        // control file. It goes to the server's own console on /console/exec now, exactly as
        // the quit does, so a dead wrapper stops neither. The guard that still required one
        // silently ignored --save-name here and killed the world the caller had explicitly
        // asked to keep, which is data loss on the one path that asked for the opposite.
        var fixture = new ServerFixture().Installed().Running().AnsweringStatus();
        fixture.Processes.Kill(9100);
        fixture.Client.Transport.Standing(
            ServerHalf.ControlPort, Endpoints.ConsoleExec, ScriptedAnswer.Ok("{\"ok\":true}"));

        var landed = false;
        fixture.Client.Rig.Sleeper.OnDelay = _ =>
        {
            if (!landed)
            {
                // The world hits the disk, which is the save confirmation's own witness.
                fixture.Fs.AddFile(Path.Combine(fixture.Paths.World("Luna"), "Luna.save"), "world bytes");
                landed = true;
                return;
            }

            fixture.Processes.Kill(9101);
        };

        fixture.Stop(saveName: "Luna");

        var console = fixture.Client.Transport.Sent
            .Where(sent => sent.Port == ServerHalf.ControlPort && sent.Path == Endpoints.ConsoleExec)
            .Select(static sent => sent.Body ?? "")
            .ToList();

        Assert.False(fixture.Output.Warned("--save-name ignored"));
        Assert.Contains(console, body => body.Contains("save", StringComparison.Ordinal)
                                         && body.Contains("Luna", StringComparison.Ordinal));
        Assert.True(fixture.Output.Said("Submitted save \"Luna\" to the server's own console"));

        // Attempted AND landed: stop warns exactly when the save is not confirmed, so the
        // absence of that warning is this path's statement that the world is on disk.
        Assert.False(fixture.Output.Warned("No save confirmation within"));

        // And in that order: the world is saved, and only then is the server asked to quit.
        var saveAt = console.FindIndex(body => body.Contains("Luna", StringComparison.Ordinal));
        var quitAt = console.FindIndex(body => body.Contains("quit", StringComparison.Ordinal));
        Assert.True(quitAt > saveAt && saveAt >= 0, $"save at {saveAt}, quit at {quitAt}");
    }

    [Fact]
    public void AnOrphanedServerWithNoPlaneAtAllStillReportsTheSaveNameAsIgnored()
    {
        // The other half of the same guard, and it must not soften. Nothing scripted on this
        // port, so the plane is silent: a server whose plugin is not deployed or did not
        // load, whose wrapper is gone, and which therefore has no channel a save could
        // travel on. Attempting one anyway would report a save that never happened.
        var fixture = new ServerFixture().Installed().Running();
        fixture.Processes.Kill(9100);

        fixture.Stop(saveName: "Luna", teardownSeconds: 4);

        Assert.True(fixture.Output.Warned("--save-name ignored"));
        Assert.DoesNotContain(
            fixture.Client.Transport.Sent,
            sent => sent.Path == Endpoints.ConsoleExec && (sent.Body ?? "").Contains("save", StringComparison.Ordinal));
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
    public void TheQuitGoesThroughTheControlPlaneRatherThanStdinWhenThePluginAnswers()
    {
        // Measured 2026-08-15 on 0.2.6428.27798, against a server in world: a stdin quit left
        // the process alive 90 s later with a ZERO-byte log delta, while POST /console/exec
        // quit it in 2.55 s with a full Unity shutdown dump. A console 'help' in between
        // returned 452 lines, so the server was healthy and the stdin quit was ignored rather
        // than lost. The control plane is therefore tried first, and stdin is the fallback.
        var fixture = new ServerFixture().Installed().Running();
        fixture.Client.Transport.Standing(
            ServerHalf.ControlPort, Endpoints.ConsoleExec, ScriptedAnswer.Ok("{\"ok\":true}"));
        fixture.Client.Rig.Sleeper.OnDelay = _ => fixture.Processes.Kill(9101);

        fixture.Stop();

        Assert.Contains(
            fixture.Client.Transport.Sent,
            sent => sent.Port == ServerHalf.ControlPort
                    && sent.Path == Endpoints.ConsoleExec
                    && (sent.Body ?? "").Contains("quit"));
        Assert.False(fixture.Output.Said("Sending 'quit' via host wrapper"));
        Assert.DoesNotContain(9101, fixture.Processes.StopRequests);
    }

    [Fact]
    public void AnOrphanedServerIsAskedToQuitThroughItsOwnPlaneRatherThanKilledOutright()
    {
        // The stdin path needs the wrapper, so a server whose wrapper died was always
        // force-killed. The control plane does not, so it now gets a graceful quit.
        var fixture = new ServerFixture().Installed().Running();
        fixture.Processes.Kill(9100);
        fixture.Client.Transport.Standing(
            ServerHalf.ControlPort, Endpoints.ConsoleExec, ScriptedAnswer.Ok("{\"ok\":true}"));
        fixture.Client.Rig.Sleeper.OnDelay = _ => fixture.Processes.Kill(9101);

        fixture.Stop();

        Assert.DoesNotContain(9101, fixture.Processes.StopRequests);
    }

    [Fact]
    public void AForceKillAfterAnAcceptedControlPlaneQuitSaysThatIsNotTheNormalOutcome()
    {
        // The two force-kill cases mean opposite things. After stdin it is expected; after a
        // quit the console accepted, roughly 2.5 s is normal and 30 s is a real problem.
        var fixture = new ServerFixture().Installed().Running();
        fixture.Client.Transport.Standing(
            ServerHalf.ControlPort, Endpoints.ConsoleExec, ScriptedAnswer.Ok("{\"ok\":true}"));

        fixture.Stop(teardownSeconds: 4);

        Assert.True(fixture.Output.Warned("after a control-plane quit it accepted"));
        Assert.Contains(9101, fixture.Processes.StopRequests);
    }

    [Fact]
    public void AControlPlaneThatRefusesTheQuitFallsBackToTheWrappersStdin()
    {
        var fixture = new ServerFixture().Installed().Running();
        fixture.Client.Transport.Standing(
            ServerHalf.ControlPort, Endpoints.ConsoleExec, ScriptedAnswer.Refused("{\"ok\":false}"));
        fixture.Client.Rig.Sleeper.OnDelay = _ => fixture.Processes.Kill(9101);

        fixture.Stop();

        Assert.True(fixture.Output.Said("Sending 'quit' via host wrapper"));
    }

    [Fact]
    public void AForceKillWithOnlyStdinAvailableNamesTheDeployThatWouldFixIt()
    {
        // Nothing scripted on /console/exec, so the plane is silent: exactly a server whose
        // plugin is not deployed or did not load. The force-kill is then correct, and the
        // message has to say so rather than reading as a fault.
        var fixture = new ServerFixture().Installed().Running();

        fixture.Stop(teardownSeconds: 4);

        Assert.True(fixture.Output.Warned("deploy TestRig --target server"));
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
    public void ThePhaseTheServerReportsIsTheReadinessSignal()
    {
        // The evidence is a statement by the process about its own game state, read from the
        // merged plugin on the server's own port. It used to be an INFERENCE from an
        // InspectorPlus request file being deleted, which is not evidence of a world at all.
        var fixture = new ServerFixture().Installed().Running().AnsweringStatus("inWorld");

        Assert.True(fixture.Wait(ReadinessStage.InWorld, waitSeconds: 30));
        Assert.True(fixture.Output.Said("'inWorld' reached"));
    }

    [Fact]
    public void AServerSittingAtSomeOtherPhaseIsNotReady()
    {
        // The measured defect, in one test: --new Moon was rejected, the server ran on with no
        // world, and the old barrier returned "the world is loaded and the simulation is
        // ticking". Here the plane answers and simply never reaches inWorld, and the wait must
        // fail rather than report a world that does not exist.
        var fixture = new ServerFixture().Installed().Running().AnsweringStatus("loading");

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Wait(ReadinessStage.InWorld, waitSeconds: 6));

        Assert.Contains("did not reach 'inWorld'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("last reported phase 'loading'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInspectorPlusProbeIsNoLongerWrittenOrTrusted()
    {
        // Consuming a probe is not evidence of anything: measured on 2026-08-15, InspectorPlus
        // consumed one four seconds into a run whose world name the game had rejected, on a
        // server that had no world and never would. Nothing writes one any more, so nothing can
        // read one as readiness by accident.
        var fixture = new ServerFixture().Installed().Running();

        Assert.Throws<RigRefusalException>(() => fixture.Wait(ReadinessStage.InWorld, waitSeconds: 6));

        Assert.DoesNotContain(fixture.Fs.AllFiles(), f => f.Contains("testrig-ready-", StringComparison.Ordinal));
    }

    [Fact]
    public void AControlPlaneThatNeverAnswersIsNamedAsSuchAndNamesTheDeploy()
    {
        // The two failures are genuinely different and are reported apart: nothing answered
        // (the plugin is missing) against answered-but-never-got-there (a slow or absent
        // world). One sentence covering both would leave a caller guessing which they had.
        var fixture = new ServerFixture().Installed().Running();

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Wait(ReadinessStage.InWorld, waitSeconds: 6));

        Assert.Contains("Nothing answered on 127.0.0.1:27750", ex.Message, StringComparison.Ordinal);
        Assert.Contains("testrig deploy TestRig --target server", ex.Message, StringComparison.Ordinal);
        Assert.Contains("InspectorPlus probe", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARejectedWorldNameFailsAtOnceAndCarriesTheGamesOwnList()
    {
        // The game prints this once and then runs forever with no world, so waiting the whole
        // budget would report a timeout ten minutes after the real answer was already in the
        // log. The list it prints is the most useful thing in the refusal.
        var fixture = new ServerFixture().Installed().Running().RejectedTheWorldName("Moon")
            .AnsweringStatus("menu");

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Wait(ReadinessStage.InWorld, waitSeconds: 600));

        Assert.Contains("REJECTED the world name", ex.Message, StringComparison.Ordinal);
        Assert.Contains("No such world name: Moon", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Valid worlds: Europa3, Lunar", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PingAndModsLoadedAreReachableOnThisHalfNow()
    {
        // Two of the three stages that used to be refused here. The merged plugin loads into
        // this half, so there is a plane to ping and a loaded-plugin count to count.
        var fixture = new ServerFixture().Installed().Running().AnsweringStatus("menu", plugins: 42);

        Assert.True(fixture.Wait(ReadinessStage.Ping, waitSeconds: 30));
        Assert.True(fixture.Wait(ReadinessStage.ModsLoaded, waitSeconds: 30));
    }

    [Fact]
    public void AServerThatExitsWhileWaitingIsReportedAsSuch()
    {
        var fixture = new ServerFixture().Installed().Running();
        fixture.Client.Rig.Sleeper.OnDelay = _ => fixture.Processes.Kill(9101);

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Wait(ReadinessStage.InWorld, waitSeconds: 30));
        Assert.Contains("exited while waiting for 'inWorld'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WaitOnTHISHALFRefreshesALockYouHold()
    {
        // SERVER-140, spec D-01. Both CLAUDE.md and MANUAL.md state that wait refreshes a lock
        // you hold; the client half did it and this half did NOT, so the documented ten-minute
        // wait ran against a ten-minute TTL on a rig that is by definition not busy.
        var fixture = new ServerFixture().Installed().Running().AnsweringStatus("inWorld");
        var owner = fixture.Lease();

        fixture.Clock.AdvanceMinutes(3);
        var before = fixture.Client.Rig.ReadLockFile()!.GetOrEmpty(LockFields.RefreshedAt);

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
