using TestRig.Contracts;
using TestRig.Core.Rig;
using TestRig.Playtest.Model;
using TestRig.Playtest.Runner;
using TestRig.Tests.Playtest.Fakes;
using Xunit;

namespace TestRig.Tests.Playtest;

/// <summary>
///     Bring-up: hosts first and all the way into their world, then joiners, with every
///     post-condition read back from the authority.
/// </summary>
public sealed class BringUpTests
{
    private static PlaytestFixture Rig()
    {
        var fixture = new PlaytestFixture();
        fixture.WithInstance("hostie", 27701, "host");
        fixture.WithInstance("joiner", 27702);
        return fixture;
    }

    private static CheckSpec HostAndJoiner() => new("a check", "s",
    [
        new InstanceSpec("hostie", InstanceRole.Host, World: "Lunar"),
        new InstanceSpec("joiner", InstanceRole.Client, ConnectTo: "hostie"),
    ]);

    [Fact]
    public void HostsAreStartedBeforeAnyClient()
    {
        var fixture = Rig();
        var ctx = fixture.Context(HostAndJoiner());
        new CheckRunner(fixture.Dependencies).BringUp(ctx);

        var starts = fixture.Launcher.Calls.Where(c => c.StartsWith("start ", StringComparison.Ordinal)).ToList();
        Assert.Equal(["start hostie", "start joiner"], starts);
    }

    [Fact]
    public void AHostWithAWorldIsDrivenIntoItAndTheWorldNameReachesTheEndpoint()
    {
        var fixture = Rig();
        var ctx = fixture.Context(HostAndJoiner());
        new CheckRunner(fixture.Dependencies).BringUp(ctx);

        Assert.Equal("Lunar", fixture.Transport.State("hostie").RequestedWorld);
        Assert.Contains("hostie POST /host", fixture.Transport.Requests);
    }

    [Fact]
    public void AHostSpecWithNeitherWorldNorSaveStopsAtTheMenu()
    {
        // A used idiom: it leaves the window between "reached the menu" and "hosts or
        // connects", which is the only place entitlement can be stripped.
        var fixture = Rig();
        var spec = new CheckSpec("a check", "s", [new InstanceSpec("hostie", InstanceRole.Host)]);
        var ctx = fixture.Context(spec);
        new CheckRunner(fixture.Dependencies).BringUp(ctx);

        Assert.DoesNotContain("hostie POST /host", fixture.Transport.Requests);
        Assert.Equal("menu", fixture.Transport.State("hostie").Phase);
    }

    [Fact]
    public void HostingIsConfirmedFromTheHostsOwnStatusAndNotFromTheCall()
    {
        // The endpoint answers 200 and the authority disagrees. NetworkServer.Host() gives up
        // quietly after three failed binds, so the call returning proves nothing.
        var fixture = Rig();
        fixture.Transport.HostDoesNotStick.Add("hostie");
        var ctx = fixture.Context(HostAndJoiner());

        var thrown = Assert.Throws<PlaytestSignal>(() => new CheckRunner(fixture.Dependencies).BringUp(ctx));
        Assert.Equal("host-not-hosting", thrown.Detector);
        Assert.Contains("is not hosting", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("never failed", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AJoinerIsConfirmedFromTheHostRosterAndNotFromItsOwnAnswer()
    {
        var fixture = Rig();
        var ctx = fixture.Context(HostAndJoiner());
        new CheckRunner(fixture.Dependencies).BringUp(ctx);

        Assert.Equal(2, fixture.Transport.State("hostie").Roster.Count);
    }

    [Fact]
    public void AJoinerThatNeverAppearsInTheRosterIsInconclusive()
    {
        var fixture = Rig();
        fixture.Transport.JoinerNeverArrives = true;
        var ctx = fixture.Context(HostAndJoiner());

        var thrown = Assert.Throws<PlaytestSignal>(() => new CheckRunner(fixture.Dependencies).BringUp(ctx));
        Assert.Equal("joiner-not-in-roster", thrown.Detector);
        Assert.Equal(SignalKind.Inconclusive, thrown.Kind);
    }

    [Fact]
    public void AStartThatFailsIsInconclusiveAndTheInstanceIsStillRegisteredForTeardown()
    {
        // The process may exist even when the launcher reported a failure.
        var fixture = Rig();
        fixture.Launcher.StartFails.Add("hostie");
        var ctx = fixture.Context(HostAndJoiner());

        var thrown = Assert.Throws<PlaytestSignal>(() => new CheckRunner(fixture.Dependencies).BringUp(ctx));
        Assert.Equal(Detectors.InstanceStartFailed, thrown.Detector);
        Assert.Contains("hostie", ctx.Started);
    }

    [Fact]
    public void BringUpNeverTargetsTheWholeRig()
    {
        var fixture = Rig();
        var ctx = fixture.Context(HostAndJoiner());
        new CheckRunner(fixture.Dependencies).BringUp(ctx);

        Assert.DoesNotContain(fixture.Launcher.Calls, c => c.Contains(" all", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fixture.Launcher.Calls, c => c.Contains("clients", StringComparison.OrdinalIgnoreCase));
    }

    // ---- barriers ---------------------------------------------------------

    [Fact]
    public void TheMenuStageNeedsBothInitialisationAndThePhase()
    {
        Assert.True(PlaytestContext.Reached(new StatusResponse { GameInitialized = true, Phase = "menu" }, Stage.Menu));
        Assert.False(PlaytestContext.Reached(new StatusResponse { GameInitialized = false, Phase = "menu" }, Stage.Menu));
        Assert.False(PlaytestContext.Reached(new StatusResponse { GameInitialized = true, Phase = "loading" }, Stage.Menu));
    }

    [Fact]
    public void TheOtherStagesReadWhatTheyAreDocumentedToRead()
    {
        Assert.True(PlaytestContext.Reached(new StatusResponse(), Stage.Ping));
        Assert.True(PlaytestContext.Reached(new StatusResponse { Phase = "inWorld" }, Stage.InWorld));
        Assert.False(PlaytestContext.Reached(new StatusResponse { Phase = "loading" }, Stage.InWorld));
    }

    [Fact]
    public void TheHarnessAndTheWaitVerbAgreeAboutModsLoadedAtEveryCount()
    {
        // This file carried '> 10' and Core's ReadinessStages carried '>= 10', so
        // 'testrig wait --stage modsLoaded' and a harness barrier disagreed at exactly one
        // plugin count. Both were tested, so both stayed green while they disagreed. One
        // constant now, compared the same way in both places.
        foreach (var count in new[] { 0, 2, RigConstants.StageMinPlugins - 1, RigConstants.StageMinPlugins, 11, 42 })
        {
            var status = new StatusResponse { LoadedPluginCount = count };

            Assert.Equal(
                ReadinessStages.Reached(status, ReadinessStage.ModsLoaded),
                PlaytestContext.Reached(status, Stage.ModsLoaded));
        }

        Assert.True(PlaytestContext.Reached(
            new StatusResponse { LoadedPluginCount = RigConstants.StageMinPlugins }, Stage.ModsLoaded));
        Assert.False(PlaytestContext.Reached(
            new StatusResponse { LoadedPluginCount = RigConstants.StageMinPlugins - 1 }, Stage.ModsLoaded));
    }

    [Fact]
    public void ABarrierThatIsReachedReturnsTheStatusAtThatMoment()
    {
        var fixture = Rig();
        var ctx = fixture.Context(HostAndJoiner());
        var status = ctx.WaitStage("hostie", Stage.Menu, 30);
        Assert.Equal("menu", status.Phase);
    }

    [Fact]
    public void ABarrierThatTimesOutRestartsAndTriesAgainBeforeGivingUp()
    {
        var fixture = Rig();
        fixture.Transport.State("hostie").Phase = "loading";
        var ctx = fixture.Context(HostAndJoiner());

        var thrown = Assert.Throws<PlaytestSignal>(() => ctx.WaitStage("hostie", Stage.Menu, 20, 5));
        Assert.Equal("boot-timeout", thrown.Detector);

        // Two attempts means ONE restart between them: the remedy runs on the path from
        // "this attempt failed" to "try again", and the last attempt has no such path.
        Assert.Equal(1, fixture.Launcher.Calls.Count(c => c == "start hostie"));
    }

    [Fact]
    public void ABarrierMessageCarriesTheLastStatusItSaw()
    {
        var fixture = Rig();
        fixture.Transport.State("hostie").Phase = "loading";
        var ctx = fixture.Context(HostAndJoiner());

        var thrown = Assert.Throws<PlaytestSignal>(() => ctx.WaitStage("hostie", Stage.Menu, 20, 5));
        Assert.Contains("phase=loading", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("plugins=42", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWorkshopParkIsToldApartFromASlowBootAndRestarts()
    {
        var fixture = Rig();
        var state = fixture.Transport.State("hostie");
        state.Phase = "loading";
        state.GameInitialized = false;
        state.LoadedPluginCount = 2;
        var ctx = fixture.Context(HostAndJoiner());

        var thrown = Assert.Throws<PlaytestSignal>(() => ctx.WaitStage("hostie", Stage.Menu, 20, 5));
        Assert.Equal("launchpad-workshop-park", thrown.Detector);
    }

    [Fact]
    public void ARestartInsideABarrierCanRescueTheInstance()
    {
        var fixture = Rig();
        fixture.Transport.State("hostie").Phase = "loading";
        fixture.Launcher.OnStart = name => fixture.Transport.State(name).Phase = "menu";
        var ctx = fixture.Context(HostAndJoiner());

        var status = ctx.WaitStage("hostie", Stage.Menu, 20, 5);
        Assert.Equal("menu", status.Phase);
        Assert.True(ctx.Degraded);
    }

    [Fact]
    public void ABarrierSpendsTimeThroughTheInjectedClockRatherThanTheWallClock()
    {
        var fixture = Rig();
        fixture.Transport.State("hostie").Phase = "loading";
        var before = fixture.Clock.UtcNow;
        var ctx = fixture.Context(HostAndJoiner());

        Assert.Throws<PlaytestSignal>(() => ctx.WaitStage("hostie", Stage.Menu, 300, 5));
        Assert.True(fixture.Clock.UtcNow - before >= TimeSpan.FromSeconds(300));
    }

    [Fact]
    public void ABarrierCannotSpinForeverOnAFrozenClock()
    {
        // Two brakes, not one: the wall-clock deadline is the budget, and the poll cap is what
        // stops a frozen or injected clock turning a barrier into an infinite loop. That is
        // the difference between a harness that reports a boot timeout and one that hangs
        // holding the rig.
        var fixture = new PlaytestFixture();
        fixture.WithInstance("hostie", 27701, "host");
        fixture.Transport.State("hostie").Phase = "loading";

        var frozen = new FrozenSleeper();
        var ctx = new PlaytestContext(
            new PlaytestDependencies
            {
                Transport = fixture.Transport, Launcher = fixture.Launcher, Registry = fixture.Registry,
                Files = fixture.Files, LogFiles = fixture.LogFiles, Clock = fixture.Clock, Sleeper = frozen,
                RigHome = PlaytestFixture.RigHomePath, Tier1SaveRoot = fixture.Tier1SaveRoot,
            },
            new CheckSpec("a check", "s", [new InstanceSpec("hostie", InstanceRole.Host)]),
            new TestRig.Playtest.Flakes.FlakeCatalogue(), null, "a1b2c3");

        Assert.Throws<PlaytestSignal>(() => ctx.WaitStage("hostie", Stage.Menu, 300, 5));
        Assert.InRange(frozen.Calls, 1, 200);
    }

    private sealed class FrozenSleeper : TestRig.Core.Abstractions.ISleeper
    {
        public int Calls { get; private set; }

        public Task DelayAsync(TimeSpan duration, CancellationToken ct = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }
}
