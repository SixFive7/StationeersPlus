using System.Text.Json;
using TestRig.Contracts;
using TestRig.Core.Client;
using TestRig.Core.Rig;
using TestRig.Core.Session;
using Xunit;

namespace TestRig.Tests.Client;

/// <summary>
/// Starting, stopping, saving and waiting: the verbs that touch live processes.
/// </summary>
public sealed class LifecycleTests
{
    private static string StatusJson(
        string role = "menu",
        string phase = "menu",
        int plugins = 42,
        bool initialised = true,
        string? clientId = null,
        ConnectedClient[]? roster = null,
        int playersInGame = 0)
    {
        var status = new StatusResponse
        {
            Ok = true,
            Role = role,
            Phase = phase,
            LoadedPluginCount = plugins,
            GameInitialized = initialised,
            ConnectedClients = roster,
            PlayersInGame = playersInGame,
            Instance = clientId is null ? null : new InstanceBlock { ClientId = clientId },
        };
        return JsonSerializer.Serialize(status, RigJsonContext.Default.StatusResponse);
    }

    private static ClientFixture RigWith(params (string Name, string Role)[] instances)
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        foreach (var (name, role) in instances) fixture.Create(name, owner, role);
        fixture.Output.Clear();
        return fixture;
    }

    // =====================================================================
    // start
    // =====================================================================

    [Fact]
    public void StartIsGatedAndAnEmptySetSaysSoRatherThanDoingNothingSilently()
    {
        var fixture = new ClientFixture();
        Assert.Throws<RigRefusalException>(() => fixture.Start([], owner: null));

        var owner = fixture.Lease();
        fixture.Start([], owner);
        Assert.True(fixture.Output.Said("No client instances selected"));
    }

    [Fact]
    public void AMissingTreeRefusesAndNamesWhereTheLocationCameFrom()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        var entry = fixture.Create("x", owner);
        fixture.Fs.DeleteDirectory(fixture.Layout.PathsFor("x").Tree, recursive: true);

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Start([entry], owner));
        Assert.Contains("has no tree at", ex.Message, StringComparison.Ordinal);
        Assert.Contains("That location came from", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--instances-root", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAlreadyRunningInstanceRefusesAndNOTHINGIsStarted()
    {
        // A skipped start is the worst possible outcome: the command comes back looking
        // successful and every assertion afterwards runs against a rig that is not the one the
        // caller asked for.
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        var a = fixture.Create("a", owner);
        var b = fixture.Create("b", owner);

        fixture.Processes.Add(4321, "rocketstation", fixture.Clock.UtcNow);
        PidFiles.Write(fixture.Fs, fixture.Layout.PathsFor("a").PidFile, 4321, fixture.Clock.UtcNow);

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Start([a, b], owner));
        Assert.Contains("Nothing was started", ex.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Launcher.Launches);
    }

    [Fact]
    public void AStalePidFileIsNotedAndTheStartReplacesIt()
    {
        // Refusing to start over a recycled id would make a crashed instance unstartable until
        // somebody deleted the file by hand.
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        var entry = fixture.Create("x", owner);
        fixture.Fs.AddFile(fixture.Layout.PathsFor("x").PidFile, "9999");

        fixture.Output.Clear();
        fixture.Start([entry], owner);

        Assert.True(fixture.Output.Said("Stale game.pid ignored"));
        Assert.Single(fixture.Launcher.Launches);
    }

    [Fact]
    public void TheIsolatedDesktopIsEnsuredAndTheMessageSaysItIsNeverSwitchedTo()
    {
        var fixture = RigWith(("x", "client"));
        var owner = fixture.Lease();

        fixture.Start(fixture.Registry.Read(), owner);

        Assert.Contains(RigConstants.DefaultDesktop, fixture.Launcher.DesktopsEnsured);
        Assert.Equal(RigConstants.DefaultDesktop, Assert.Single(fixture.Launcher.Launches).Desktop);
        Assert.True(fixture.Output.Said("never switched to"));
    }

    [Fact]
    public void NoDesktopWarnsThatTheInstanceWillTakeTheDevelopersForeground()
    {
        var fixture = RigWith(("x", "client"));
        var owner = fixture.Lease();

        fixture.Start(fixture.Registry.Read(), owner, desktop: "");

        Assert.True(fixture.Output.Warned("WILL take the foreground"));
        Assert.True(fixture.Output.Warned("Debugging only"));
        Assert.Null(Assert.Single(fixture.Launcher.Launches).Desktop);
        Assert.Empty(fixture.Launcher.DesktopsEnsured);
    }

    [Fact]
    public void TheCommandLineCarriesAUniqueLogFileAndNeverTheSavePathSetting()
    {
        // -settings SavePath on a client makes StationeersLaunchPad rewrite the DEVELOPER'S
        // shared modconfig.xml with every Local entry deleted. Measured on a first boot: five
        // local mod entries silently removed, nothing warned.
        var fixture = RigWith(("x", "client"));
        var owner = fixture.Lease();

        fixture.Start(fixture.Registry.Read(), owner);

        var launch = Assert.Single(fixture.Launcher.Launches);
        Assert.Contains("-logFile", launch.CommandLine, StringComparison.Ordinal);
        Assert.Contains("unity-", launch.CommandLine, StringComparison.Ordinal);
        Assert.DoesNotContain("SavePath", launch.CommandLine, StringComparison.Ordinal);
        Assert.DoesNotContain("-settings ", launch.CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoInstancesGetTwoDifferentUnityLogPaths()
    {
        // Without a unique path the second starter wins Player.log, the first instance's log is
        // discarded with no error, and Player-prev.log is zeroed by two rotations in one
        // second, destroying the developer's previous log.
        var fixture = RigWith(("a", "client"), ("b", "client"));
        var owner = fixture.Lease();

        fixture.Start(fixture.Registry.Read(), owner);

        var logs = fixture.Launcher.Launches
            .Select(static l => l.CommandLine.Split("-logFile ")[1].Split(' ')[0])
            .ToList();
        Assert.Equal(2, logs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void TheWindowSizeComesFromTheRegistryEntryAndNotFromALauncherDefault()
    {
        // CLIENT-121: the PowerShell passed its own 800/600 defaults, so the launch flags
        // disagreed with the manifest the plugin actually honours.
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        var entry = fixture.CreateWith(new CreateOptions
        {
            Instance = "x", CallerId = owner, Width = 1600, Height = 900,
        });

        fixture.Start([entry], owner);

        var launch = Assert.Single(fixture.Launcher.Launches);
        Assert.Contains("-screen-width 1600", launch.CommandLine, StringComparison.Ordinal);
        Assert.Contains("-screen-height 900", launch.CommandLine, StringComparison.Ordinal);
        Assert.Contains("-screen-fullscreen 0", launch.CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public void TheChildsWorkingDirectoryIsTheDataFolderAndItGetsTheManifestPath()
    {
        var fixture = RigWith(("x", "client"));
        var owner = fixture.Lease();

        fixture.Start(fixture.Registry.Read(), owner);

        var paths = fixture.Layout.PathsFor("x");
        var launch = Assert.Single(fixture.Launcher.Launches);
        Assert.Equal(paths.Data, launch.WorkingDirectory);
        Assert.Equal(paths.Manifest, launch.ManifestPath);
        Assert.Equal(paths.Exe, launch.ExePath);
    }

    [Fact]
    public void ThePidFileRecordsTheProcessStartTimeSoReuseCanBeDetectedExactly()
    {
        var fixture = RigWith(("x", "client"));
        var owner = fixture.Lease();

        fixture.Start(fixture.Registry.Read(), owner);

        var paths = fixture.Layout.PathsFor("x");
        Assert.True(fixture.Fs.FileExists(paths.PidFile + PidFiles.StartedSuffix));
        Assert.True(PidFiles.ClientAlive(fixture.Fs, fixture.Processes, paths.PidFile));
    }

    [Fact]
    public void HostsAreStartedBeforeJoiners()
    {
        var fixture = RigWith(("joiner", "client"), ("host1", "host"));
        var owner = fixture.Lease();

        fixture.Start(fixture.Registry.Read(), owner);

        Assert.Equal(2, fixture.Launcher.Launches.Count);
        Assert.Contains("host1", fixture.Launcher.Launches[0].WorkingDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AHostInTheSetPrintsTheOrderingRuleThatCannotBeEnforcedFromOutside()
    {
        var fixture = RigWith(("host1", "host"), ("joiner", "client"));
        var owner = fixture.Lease();

        fixture.Start(fixture.Registry.Read(), owner);

        Assert.True(fixture.Output.Said("must be IN ITS WORLD before any joiner connects"));
        Assert.True(fixture.Output.Said(Endpoints.Host));
        Assert.True(fixture.Output.Said(Endpoints.Connect));
    }

    [Fact]
    public void ManifestsAreWrittenOnceForTheWholeSetRatherThanOncePerInstance()
    {
        // CLIENT-098: the PowerShell rewrote all N manifests inside the start loop, so an
        // N-instance start wrote N-squared manifests.
        var fixture = RigWith(("a", "client"), ("b", "client"));
        var owner = fixture.Lease();

        fixture.Start(fixture.Registry.Read(), owner);

        foreach (var name in new[] { "a", "b" })
        {
            var manifest = JsonSerializer.Deserialize(
                fixture.Fs.ReadAllText(fixture.Layout.PathsFor(name).Manifest),
                ClientJsonContext.Default.InstanceManifest)!;
            Assert.Equal([27701, 27702], manifest.PeerPorts);
        }
    }

    // =====================================================================
    // stop
    // =====================================================================

    [Fact]
    public void StopNeedsNoLockSoAnOrphanCanAlwaysBeCleanedUp()
    {
        var fixture = RigWith(("x", "client"));
        fixture.Stop(fixture.Registry.Read());
        Assert.True(fixture.Output.Said("Not running"));
    }

    [Fact]
    public void AnEmptyStopSaysSoRatherThanReturningInSilence()
    {
        // CLIENT-196: the PowerShell returned silently here while start printed a message for
        // the identical case, so a stop against an empty rig read as a hang.
        var fixture = new ClientFixture();
        fixture.Stop([]);
        Assert.True(fixture.Output.Said("No client instances selected"));
    }

    [Fact]
    public void AStoppedInstanceStillHasItsPidFileClearedAndItsHostFlagReset()
    {
        // The stopped case is the one that matters most: it is how a CRASHED host gets cleaned
        // up before its next run.
        var fixture = RigWith(("x", "client"));
        var paths = fixture.Layout.PathsFor("x");

        fixture.Fs.AddFile(paths.PidFile, "9999");
        fixture.Fs.AddFile(paths.Settings, "<Settings><StartLocalHost>true</StartLocalHost></Settings>");

        fixture.Stop(fixture.Registry.Read());

        Assert.False(fixture.Fs.FileExists(paths.PidFile));
        Assert.Contains("<StartLocalHost>false</StartLocalHost>", fixture.Fs.ReadAllText(paths.Settings), StringComparison.Ordinal);
    }

    [Fact]
    public void BothStartLocalHostFormsArePatched()
    {
        var fixture = RigWith(("x", "client"));
        var paths = fixture.Layout.PathsFor("x");
        fixture.Fs.AddFile(paths.Settings,
            "<Settings StartLocalHost=\"true\"><StartLocalHost >TRUE</StartLocalHost ></Settings>");

        fixture.Stop(fixture.Registry.Read());

        var text = fixture.Fs.ReadAllText(paths.Settings);
        Assert.DoesNotContain("true", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("StartLocalHost=\"false\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTeardownOrderPutsJoinersFirstAndTheHostLast()
    {
        var fixture = RigWith(("host1", "host"), ("joiner", "client"));
        var owner = fixture.Lease();
        var entries = fixture.Registry.Read();

        StartLive(fixture, "host1", 6001);
        StartLive(fixture, "joiner", 6002);

        fixture.Transport.Standing(27701, Endpoints.Status, ScriptedAnswer.Ok(StatusJson("listenHost", "inWorld", clientId: "900000000001")));
        fixture.Transport.Standing(27702, Endpoints.Status, ScriptedAnswer.Ok(StatusJson("joinedClient", "inWorld")));
        fixture.Transport.Standing(27702, Endpoints.Disconnect, ScriptedAnswer.Ok("""{"ok":true,"result":"menu"}"""));
        fixture.Transport.Standing(27701, Endpoints.Save, ScriptedAnswer.Ok("""{"ok":true,"confirmed":true,"result":"console"}"""));
        fixture.Transport.Standing(27701, Endpoints.Quit, ScriptedAnswer.Ok("""{"ok":true}"""));
        fixture.Transport.Standing(27702, Endpoints.Quit, ScriptedAnswer.Ok("""{"ok":true}"""));

        fixture.Output.Clear();
        fixture.Stop(entries, owner);

        var order = fixture.Output.Lines.First(l => l.Text.StartsWith("[Stop] Order:", StringComparison.Ordinal)).Text;
        Assert.Contains("joiner [joiner] -> host1 [host]", order, StringComparison.Ordinal);
    }

    [Fact]
    public void AHostWithAnAttachedJoinerOutsideTheTeardownRefusesAndStopsNothing()
    {
        var fixture = RigWith(("host1", "host"), ("joiner", "client"));
        var owner = fixture.Lease();

        StartLive(fixture, "host1", 6001);
        StartLive(fixture, "joiner", 6002);

        var roster = new[]
        {
            new ConnectedClient { ClientId = "900000000001", Username = "host1", IsHost = true },
            new ConnectedClient { ClientId = "900000000002", Username = "joiner" },
        };
        fixture.Transport.Standing(27701, Endpoints.Status,
            ScriptedAnswer.Ok(StatusJson("listenHost", "inWorld", clientId: "900000000001", roster: roster)));
        fixture.Transport.Standing(27702, Endpoints.Status, ScriptedAnswer.Ok(StatusJson("joinedClient", "inWorld")));

        var hostOnly = fixture.Registry.Read().Where(static e => e.InstanceName == "host1").ToList();
        var ex = Assert.Throws<RigRefusalException>(() => fixture.Stop(hostOnly, owner));

        Assert.Contains("Nothing was stopped", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--force", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--break-lock", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Transport.Sent, s => s.Path == Endpoints.Quit);
    }

    [Fact]
    public void ForceDowngradesTheHostRefusalToAWarning()
    {
        var fixture = RigWith(("host1", "host"), ("joiner", "client"));
        var owner = fixture.Lease();

        StartLive(fixture, "host1", 6001);
        StartLive(fixture, "joiner", 6002);

        var roster = new[] { new ConnectedClient { ClientId = "900000000002", Username = "joiner" } };
        fixture.Transport.Standing(27701, Endpoints.Status,
            ScriptedAnswer.Ok(StatusJson("listenHost", "menu", clientId: "900000000001", roster: roster)));
        fixture.Transport.Standing(27702, Endpoints.Status, ScriptedAnswer.Ok(StatusJson("joinedClient", "inWorld")));
        fixture.Transport.Standing(27701, Endpoints.Quit, ScriptedAnswer.Ok("""{"ok":true}"""));

        var hostOnly = fixture.Registry.Read().Where(static e => e.InstanceName == "host1").ToList();
        fixture.Stop(hostOnly, owner, force: true);

        Assert.True(fixture.Output.Warned("ending it under them anyway"));
    }

    [Fact]
    public void AnUnclassifiableRunningInstanceRefusesBecauseItMayHoldAnUnsavedWorld()
    {
        var fixture = RigWith(("mystery", "host"));
        var owner = fixture.Lease();
        StartLive(fixture, "mystery", 6001);
        // Silent control plane plus provisioned as a host is possiblyHost.

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Stop(fixture.Registry.Read(), owner));
        Assert.Contains("cannot be classified", ex.Message, StringComparison.Ordinal);
        Assert.Contains("roughly 100 s", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--force", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADisconnectThatFailsStopsTheSequenceRatherThanKillingTheJoiner()
    {
        var fixture = RigWith(("joiner", "client"));
        var owner = fixture.Lease();
        StartLive(fixture, "joiner", 6001);

        fixture.Transport.Standing(27701, Endpoints.Status, ScriptedAnswer.Ok(StatusJson("joinedClient", "inWorld")));
        fixture.Transport.Standing(27701, Endpoints.Disconnect, ScriptedAnswer.Refused("""{"ok":false,"error":"still in a modal"}"""));

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Stop(fixture.Registry.Read(), owner));
        Assert.Contains("never said goodbye", ex.Message, StringComparison.Ordinal);
        Assert.Contains("still in a modal", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Transport.Sent, s => s.Path == Endpoints.Quit);
    }

    [Fact]
    public void ADisconnectAnsweringMenuWithNoOkFieldIsStillASuccess()
    {
        // CLIENT-172. A port keying only on ok would treat a clean disconnect from an older
        // plugin build as a failure and stop the whole teardown.
        var fixture = RigWith(("joiner", "client"));
        var owner = fixture.Lease();
        StartLive(fixture, "joiner", 6001);

        fixture.Transport.Standing(27701, Endpoints.Status, ScriptedAnswer.Ok(StatusJson("joinedClient", "inWorld")));
        fixture.Transport.Standing(27701, Endpoints.Disconnect, ScriptedAnswer.Ok("""{"result":"menu"}"""));
        fixture.Transport.Standing(27701, Endpoints.Quit, ScriptedAnswer.Ok("""{"ok":true}"""));

        fixture.Stop(fixture.Registry.Read(), owner);
        Assert.True(fixture.Output.Said("Left its session"));
    }

    [Fact]
    public void AWorldHolderIsSavedBeforeItIsQuitAndAnUnconfirmedSaveStopsTheSequence()
    {
        var fixture = RigWith(("solo", "client"));
        var owner = fixture.Lease();
        StartLive(fixture, "solo", 6001);

        fixture.Transport.Standing(27701, Endpoints.Status, ScriptedAnswer.Ok(StatusJson("singlePlayer", "inWorld")));
        fixture.Transport.Standing(27701, Endpoints.Save,
            ScriptedAnswer.Refused("""{"ok":true,"confirmed":false,"result":"timeout"}"""));

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Stop(fixture.Registry.Read(), owner));
        Assert.Contains("holds a world and its save was not confirmed", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Transport.Sent, s => s.Path == Endpoints.Quit);
    }

    [Fact]
    public void AConfirmedSaveIsFollowedByAQuitAndThePidFileGoes()
    {
        var fixture = RigWith(("solo", "client"));
        var owner = fixture.Lease();
        StartLive(fixture, "solo", 6001);

        fixture.Transport.Standing(27701, Endpoints.Status, ScriptedAnswer.Ok(StatusJson("singlePlayer", "inWorld")));
        fixture.Transport.Standing(27701, Endpoints.Save,
            ScriptedAnswer.Ok("""{"ok":true,"confirmed":true,"result":"console","confirmedBy":"console","savePath":"D:\\w\\Luna","sizeBytes":2048}"""));
        fixture.Transport.Standing(27701, Endpoints.Quit, ScriptedAnswer.Ok("""{"ok":true}"""));

        fixture.Stop(fixture.Registry.Read(), owner);

        Assert.True(fixture.Output.Said("Save confirmed (console)"));
        Assert.True(fixture.Output.Said("2.0 KB"));
        Assert.True(fixture.Output.Said("Stopped."));
        Assert.False(fixture.Fs.FileExists(fixture.Layout.PathsFor("solo").PidFile));
    }

    [Fact]
    public void AStopThatActuallyStoppedSomethingLeavesTheRigMarkedDirty()
    {
        // CLIENT-213, decided explicitly: stop is ungated so an orphan can be cleaned up, and
        // the PowerShell only wrote the crash marker from inside the lock assertion, so a
        // session whose only mutating action was a stop left the rig looking clean.
        var fixture = RigWith(("x", "client"));
        StartLive(fixture, "x", 6001);
        fixture.Transport.Standing(27701, Endpoints.Status, ScriptedAnswer.Ok(StatusJson()));
        fixture.Transport.Standing(27701, Endpoints.Quit, ScriptedAnswer.Ok("""{"ok":true}"""));

        // The create that built this instance was gated and therefore already marked the
        // rig. Clearing it isolates what the ungated stop does on its own.
        fixture.Rig.Marker.Clear();
        Assert.False(fixture.Rig.MarkerExists());

        fixture.Stop(fixture.Registry.Read());
        Assert.True(fixture.Rig.MarkerExists());
    }

    [Fact]
    public void AStopThatFoundNothingRunningChangesNothingAndMarksNothing()
    {
        var fixture = RigWith(("x", "client"));
        fixture.Rig.Marker.Clear();

        fixture.Stop(fixture.Registry.Read());
        Assert.False(fixture.Rig.MarkerExists());
    }

    // =====================================================================
    // save
    // =====================================================================

    [Fact]
    public void SavingAnInstanceThatIsNotRunningOrNotAnsweringCountsAsAFailure()
    {
        var fixture = RigWith(("a", "client"), ("b", "client"));
        var owner = fixture.Lease();
        StartLive(fixture, "b", 6002);

        fixture.Save(fixture.Registry.Read(), owner);

        Assert.True(fixture.Output.Warned("there is nothing to save"));
        Assert.True(fixture.Output.Warned("Control plane did not answer"));
        Assert.Equal("2", fixture.Output.ValueOf("saveFailed"));
    }

    [Fact]
    public void AJoinedClientIsSkippedRatherThanTriedAndCountedAsAFailure()
    {
        // CLIENT-218: the PowerShell warned and then tried anyway, guaranteeing a 409 and a
        // non-zero failure count on a rig where nothing was wrong.
        var fixture = RigWith(("joiner", "client"));
        var owner = fixture.Lease();
        StartLive(fixture, "joiner", 6001);
        fixture.Transport.Standing(27701, Endpoints.Status, ScriptedAnswer.Ok(StatusJson("joinedClient", "inWorld")));

        fixture.Save(fixture.Registry.Read(), owner);

        Assert.True(fixture.Output.Warned("Save on the host instead"));
        Assert.Equal("0", fixture.Output.ValueOf("saveFailed"));
        Assert.Equal("0", fixture.Output.ValueOf("saveAttempted"));
        Assert.DoesNotContain(fixture.Transport.Sent, s => s.Path == Endpoints.Save);
    }

    [Fact]
    public void ASaveNameIsOptionalOnThisHalfAndTravelsInTheBodyWhenGiven()
    {
        var fixture = RigWith(("solo", "client"));
        var owner = fixture.Lease();
        StartLive(fixture, "solo", 6001);
        fixture.Transport.Standing(27701, Endpoints.Status, ScriptedAnswer.Ok(StatusJson("singlePlayer", "inWorld")));
        fixture.Transport.Standing(27701, Endpoints.Save, ScriptedAnswer.Ok("""{"ok":true,"confirmed":true}"""));

        fixture.Save(fixture.Registry.Read(), owner, "Luna");

        var sent = Assert.Single(fixture.Transport.Sent, s => s.Path == Endpoints.Save);
        Assert.Contains("\"name\":\"Luna\"", sent.Body, StringComparison.Ordinal);
        Assert.Contains("\"wait\":true", sent.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAcceptedButUnconfirmedSaveWarnsAndReportsFailure()
    {
        // ok and confirmed are separate facts, and only the second one survives a teardown.
        var fixture = RigWith(("solo", "client"));
        var owner = fixture.Lease();
        StartLive(fixture, "solo", 6001);
        fixture.Transport.Standing(27701, Endpoints.Status, ScriptedAnswer.Ok(StatusJson("singlePlayer", "inWorld")));
        fixture.Transport.Standing(27701, Endpoints.Save, ScriptedAnswer.Ok("""{"ok":true,"confirmed":false,"result":"timeout"}"""));

        fixture.Save(fixture.Registry.Read(), owner);

        Assert.True(fixture.Output.Warned("not confirmed inside its own timeout"));
        Assert.True(fixture.Output.Warned("NOT saved"));
        Assert.Equal("1", fixture.Output.ValueOf("saveFailed"));
    }

    [Fact]
    public void ARefusalCarriesThePluginsOwnExplanationRatherThanAStatusCode()
    {
        var fixture = RigWith(("solo", "client"));
        var owner = fixture.Lease();
        StartLive(fixture, "solo", 6001);
        fixture.Transport.Standing(27701, Endpoints.Status, ScriptedAnswer.Ok(StatusJson("singlePlayer", "inWorld")));
        fixture.Transport.Standing(27701, Endpoints.Save,
            ScriptedAnswer.Refused("""{"ok":false,"error":"Cannot save game in GameState Loading"}"""));

        fixture.Save(fixture.Registry.Read(), owner);

        Assert.True(fixture.Output.Warned("Cannot save game in GameState Loading"));
        Assert.False(fixture.Output.Warned("409"));
    }

    [Fact]
    public void TheHttpBudgetIsTheRequestedWaitPlusAMarginSoThePluginGivesUpFirst()
    {
        var fixture = RigWith(("solo", "client"));
        var owner = fixture.Lease();
        StartLive(fixture, "solo", 6001);
        fixture.Transport.Standing(27701, Endpoints.Status, ScriptedAnswer.Ok(StatusJson("singlePlayer", "inWorld")));
        fixture.Transport.Standing(27701, Endpoints.Save, ScriptedAnswer.Ok("""{"ok":true,"confirmed":true}"""));

        fixture.SaveFor(fixture.Registry.Read(), owner, waitSeconds: 120);

        var sent = Assert.Single(fixture.Transport.Sent, s => s.Path == Endpoints.Save);
        Assert.Equal(TimeSpan.FromSeconds(150), sent.Timeout);
    }

    // =====================================================================
    // remove
    // =====================================================================

    [Fact]
    public void RemoveDeletesTheTreeAndTheWholeDataDirectoryIncludingTheSaveRoot()
    {
        var fixture = RigWith(("x", "client"));
        var owner = fixture.Lease();
        var paths = fixture.Layout.PathsFor("x");
        fixture.Fs.AddFile(Path.Combine(paths.UserData, "saves", "Luna", "Luna.save"), "world");

        fixture.Remove("x", owner);

        Assert.False(fixture.Fs.DirectoryExists(paths.Tree));
        Assert.False(fixture.Fs.DirectoryExists(paths.Data));
        Assert.Empty(fixture.Registry.Read());
        Assert.True(fixture.Output.Said("source install is untouched"));
    }

    [Fact]
    public void RemovingAHostWithALiveJoinerRefusesMoreStronglyThanStopDoes()
    {
        // A stopped host can be started again; a deleted world cannot.
        var fixture = RigWith(("host1", "host"), ("joiner", "client"));
        var owner = fixture.Lease();
        StartLive(fixture, "joiner", 6002);
        fixture.Transport.Standing(27702, Endpoints.Status, ScriptedAnswer.Ok(StatusJson("joinedClient", "inWorld")));

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Remove("host1", owner));
        Assert.Contains("Nothing was deleted", ex.Message, StringComparison.Ordinal);
        Assert.Contains("removing it deletes its world", ex.Message, StringComparison.Ordinal);
        Assert.True(fixture.Fs.DirectoryExists(fixture.Layout.PathsFor("host1").Tree));
    }

    [Fact]
    public void ARunningInstanceCannotBeRemovedAtAll()
    {
        var fixture = RigWith(("x", "client"));
        var owner = fixture.Lease();
        StartLive(fixture, "x", 6001);

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Remove("x", owner));
        Assert.Contains("Stop it first", ex.Message, StringComparison.Ordinal);
    }

    // =====================================================================
    // wait
    // =====================================================================

    [Fact]
    public void WaitNeedsNoLockAndAnEmptySetSaysSo()
    {
        var fixture = new ClientFixture();
        fixture.Wait([]);
        Assert.True(fixture.Output.Said("No client instances selected"));
    }

    [Fact]
    public void EachInstanceLeavesTheBarrierAsItArrives()
    {
        var fixture = RigWith(("a", "client"), ("b", "client"));
        fixture.Transport.Standing(27701, Endpoints.Status, ScriptedAnswer.Ok(StatusJson()));
        fixture.Transport.Standing(27702, Endpoints.Status, ScriptedAnswer.Ok(StatusJson()));

        fixture.Wait(fixture.Registry.Read(), stage: ReadinessStage.Menu);

        Assert.True(fixture.Output.Said("a reached 'menu'"));
        Assert.True(fixture.Output.Said("b reached 'menu'"));
        Assert.True(fixture.Output.Said("All instances reached 'menu'"));
    }

    [Fact]
    public void ATimeoutProbesEachStragglerOnceMoreAndNamesTheCommonCause()
    {
        var fixture = RigWith(("stuck", "client"));
        fixture.Transport.Standing(27701, Endpoints.Status,
            ScriptedAnswer.Ok(StatusJson(phase: "loading", plugins: 2, initialised: false)));

        var ex = Assert.Throws<RigRefusalException>(() =>
            fixture.Wait(fixture.Registry.Read(), stage: ReadinessStage.Menu, waitSeconds: 6));

        Assert.True(fixture.Output.Warned("plugins=2"));
        Assert.Contains("Steam Workshop", ex.Message, StringComparison.Ordinal);
        Assert.Contains("stop the instance and start it again", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WaitRefreshesALockYouHoldAndIsASilentNoOpWhenYouHoldNothing()
    {
        var fixture = RigWith(("x", "client"));
        var owner = fixture.Lease();
        fixture.Transport.Standing(27701, Endpoints.Status, ScriptedAnswer.Ok(StatusJson()));

        var before = fixture.Rig.ReadLockFile()!.GetOrEmpty(LockFields.RefreshedAt);
        fixture.Clock.AdvanceMinutes(2);
        fixture.Wait(fixture.Registry.Read(), owner);

        Assert.NotEqual(before, fixture.Rig.ReadLockFile()!.GetOrEmpty(LockFields.RefreshedAt));

        // No caller id: nothing is touched and nothing throws.
        var after = fixture.Rig.ReadLockFile()!.GetOrEmpty(LockFields.RefreshedAt);
        fixture.Clock.AdvanceMinutes(1);
        fixture.Wait(fixture.Registry.Read());
        Assert.Equal(after, fixture.Rig.ReadLockFile()!.GetOrEmpty(LockFields.RefreshedAt));
    }

    [Fact]
    public void TheBarrierDoesNotWriteTheLockFileOnceEveryPoll()
    {
        // CLIENT-248: the PowerShell refreshed every 2 s, which is up to 300 durable writes on
        // a 600 s barrier, each taking the named mutex.
        var fixture = RigWith(("stuck", "client"));
        var owner = fixture.Lease();
        fixture.Transport.Standing(27701, Endpoints.Status, ScriptedAnswer.Ok(StatusJson(phase: "loading")));

        // Every refresh takes the session's named mutex, so counting entries counts refreshes.
        var before = fixture.Rig.Mutex.Entered;
        Assert.Throws<RigRefusalException>(() =>
            fixture.Wait(fixture.Registry.Read(), owner, ReadinessStage.Menu, 120));

        var refreshes = fixture.Rig.Mutex.Entered - before;

        // 120 seconds of two-second polls is 60 iterations. Once a minute is two or three;
        // the PowerShell's once-per-poll would be sixty.
        Assert.InRange(refreshes, 1, 6);
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>Makes an instance's process live, with a pid file that claims it.</summary>
    private static void StartLive(ClientFixture fixture, string name, int pid)
    {
        fixture.Processes.Add(pid, "rocketstation", fixture.Clock.UtcNow);
        PidFiles.Write(fixture.Fs, fixture.Layout.PathsFor(name).PidFile, pid, fixture.Clock.UtcNow);
    }
}
