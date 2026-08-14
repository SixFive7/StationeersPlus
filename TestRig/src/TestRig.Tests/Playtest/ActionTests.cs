using TestRig.Contracts;
using TestRig.Playtest.Model;
using TestRig.Playtest.Runner;
using TestRig.Playtest.Values;
using TestRig.Tests.Playtest.Fakes;
using Xunit;

namespace TestRig.Tests.Playtest;

/// <summary>
///     Driving endpoints: the retry bounds, the degraded pass, and what happens when an
///     endpoint refuses.
/// </summary>
public sealed class ActionTests
{
    private static (PlaytestFixture Fixture, PlaytestContext Context) Rig(params string[] instances)
    {
        var fixture = new PlaytestFixture();
        var port = 27701;
        foreach (var name in instances) fixture.WithInstance(name, port++);

        var spec = new CheckSpec("a check", "summary", [.. instances.Select(n => new InstanceSpec(n))]);
        return (fixture, fixture.Context(spec));
    }

    [Fact]
    public void ACallThatWorksFirstTimeIsNotDegraded()
    {
        var (_, ctx) = Rig("hostie");
        var result = ctx.Act("hostie", Endpoints.Status);

        Assert.Equal(1, result.Attempts);
        Assert.False(result.Degraded);
        Assert.False(ctx.Degraded);
        Assert.Equal(200, result.HttpStatus);
    }

    [Fact]
    public void ATransportErrorIsRetriedUpToTheDetectorsBoundAndThenSucceeds()
    {
        var (fixture, ctx) = Rig("hostie");
        fixture.Transport.TransportFailureMessage = "the operation timed out";
        fixture.Transport.TransportFailures[Endpoints.Status] = 2;

        var result = ctx.Act("hostie", Endpoints.Status);
        Assert.Equal(3, result.Attempts);
        Assert.True(result.Degraded);
        Assert.True(ctx.Degraded);
        Assert.Equal(2, ctx.Retries);
        Assert.Equal(3, ctx.WorstAttempts);
        Assert.Contains("transport-error", ctx.RecordedDetectors);
    }

    [Fact]
    public void ExhaustingTheBoundEndsTheCheckAsInconclusiveAndNeverAsAFailure()
    {
        var (fixture, ctx) = Rig("hostie");
        fixture.Transport.TransportFailureMessage = "the operation timed out";
        fixture.Transport.TransportFailures[Endpoints.Status] = 99;

        var thrown = Assert.Throws<PlaytestSignal>(() => ctx.Act("hostie", Endpoints.Status));
        Assert.Equal(SignalKind.Inconclusive, thrown.Kind);
        Assert.Equal("transport-error", thrown.Detector);
        Assert.Contains("never failed", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoRetryMeansOneAttemptWhateverTheDetectorAllows()
    {
        var (fixture, ctx) = Rig("hostie");
        fixture.Transport.TransportFailureMessage = "the operation timed out";
        fixture.Transport.TransportFailures[Endpoints.Status] = 1;

        Assert.Throws<PlaytestSignal>(() => ctx.Act("hostie", Endpoints.Status, noRetry: true));
        Assert.Equal(1, ctx.WorstAttempts);
    }

    [Fact]
    public void TheRetryGapGoesThroughTheInjectedSleeperSoItCostsNothing()
    {
        var (fixture, ctx) = Rig("hostie");
        fixture.Transport.TransportFailureMessage = "the operation timed out";
        fixture.Transport.TransportFailures[Endpoints.Status] = 1;

        ctx.Act("hostie", Endpoints.Status);
        Assert.Contains(TimeSpan.FromSeconds(3), fixture.Sleeper.Delays);
    }

    [Fact]
    public void ARefusalNothingExplainsIsActionRefusedAndSaysWhyThatIsNotAFailure()
    {
        var (fixture, ctx) = Rig("hostie");
        fixture.Transport.Refusals[Endpoints.ConfigSet] = (1, 200, "no such key");

        var thrown = Assert.Throws<PlaytestSignal>(() =>
            ctx.Act("hostie", Endpoints.ConfigSet, new ConfigSetRequest { Guid = "g", Section = "s", Key = "k", Value = "v" }));

        Assert.Equal(Detectors.ActionRefused, thrown.Detector);
        Assert.Contains("only a value read back through a reader can say that", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusalAtFourHundredAndNineIsClassifiedWithItsBodyInHand()
    {
        // PowerShell threw on any non-2xx with the body inside an exception message, so a 409
        // arrived wearing a transport fault's clothes and was retried three times as a rig
        // flake before being reported under a detector that misdiagnosed it.
        var (fixture, ctx) = Rig("joiner");
        fixture.Transport.Refusals[Endpoints.Connect] = (99, 409, "duplicate identity");

        var thrown = Assert.Throws<PlaytestSignal>(() =>
            ctx.Act("joiner", Endpoints.Connect, new ConnectRequest { Address = "127.0.0.1", Port = 27801 }, blocking: true));

        Assert.Equal("connect-first-attempt", thrown.Detector);
    }

    [Fact]
    public void AnOkFalseBodyAtTwoHundredIsNotASuccess()
    {
        Assert.False(PlaytestContext.IsSuccess(string.Empty, 200, System.Text.Json.Nodes.JsonNode.Parse("""{"ok":false}""")));
        Assert.True(PlaytestContext.IsSuccess(string.Empty, 200, System.Text.Json.Nodes.JsonNode.Parse("""{"ok":true}""")));
    }

    [Fact]
    public void ABodyWithNoOkPropertyAtTwoHundredIsASuccess()
    {
        Assert.True(PlaytestContext.IsSuccess(string.Empty, 200, System.Text.Json.Nodes.JsonNode.Parse("""{"value":1}""")));
        Assert.True(PlaytestContext.IsSuccess(string.Empty, 200, null));
    }

    [Fact]
    public void ATransportErrorIsNeverASuccessWhateverTheStatusSays()
    {
        Assert.False(PlaytestContext.IsSuccess("refused", 200, System.Text.Json.Nodes.JsonNode.Parse("""{"ok":true}""")));
    }

    [Fact]
    public void AnUnknownEndpointIsRefusedBeforeAnythingIsSentAndNamesTheRealOnes()
    {
        // The refusal matrix used to tell callers to drive /console/run, which has never
        // existed. A typed path cannot be typo'd, and this is the answer for one that is.
        var (fixture, ctx) = Rig("hostie");
        var thrown = Assert.Throws<PlaytestUsageException>(() => ctx.Act("hostie", "/console/run"));

        Assert.Contains(Endpoints.ConsoleExec, thrown.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Transport.Requests);
    }

    [Fact]
    public void ABlockingCallGetsTheLongTimeoutAndTheOthersDoNot()
    {
        Assert.Equal(330, PlaytestContext.BlockingTimeoutSeconds);
        Assert.Equal(120, PlaytestContext.DefaultTimeoutSeconds);
        Assert.Contains(Endpoints.Host, PlaytestContext.BlockingPaths);
        Assert.Contains(Endpoints.Connect, PlaytestContext.BlockingPaths);
        Assert.Contains(Endpoints.Save, PlaytestContext.BlockingPaths);
        Assert.Contains(Endpoints.Load, PlaytestContext.BlockingPaths);
        Assert.Contains(Endpoints.NewWorld, PlaytestContext.BlockingPaths);
        Assert.Contains(Endpoints.WaitFor, PlaytestContext.BlockingPaths);
        Assert.DoesNotContain(Endpoints.Status, PlaytestContext.BlockingPaths);
    }

    [Fact]
    public void SilenceDuringABlockingCallIsExplainedRatherThanReadAsADeadProcess()
    {
        var (fixture, ctx) = Rig("hostie");
        fixture.Transport.TransportFailureMessage = "actively refused";
        fixture.Transport.TransportFailures[Endpoints.Save] = 99;

        var thrown = Assert.Throws<PlaytestSignal>(() => ctx.Act("hostie", Endpoints.Save, new SaveRequest { Name = "w" }, blocking: true));
        Assert.Equal("control-plane-silent", thrown.Detector);
    }

    [Fact]
    public void ADeadInstanceIsRestartedRatherThanRetriedPointlessly()
    {
        var (fixture, ctx) = Rig("hostie");
        fixture.Transport.TransportFailureMessage = "No connection could be made because the target machine actively refused it";
        fixture.Transport.TransportFailures[Endpoints.Status] = 1;

        ctx.Act("hostie", Endpoints.Status);
        Assert.Contains("stop hostie", fixture.Launcher.Calls);
        Assert.Contains("start hostie", fixture.Launcher.Calls);
        Assert.Contains("instance-dead", ctx.RecordedDetectors);
    }

    [Fact]
    public void AReaderThatCannotReachItsSourceIsInconclusiveAndNeverRetries()
    {
        // A reader that fails once ends the check: reading is not an action and re-reading a
        // value that was not there does not make it appear.
        var (fixture, ctx) = Rig("hostie");
        fixture.Transport.TransportFailureMessage = "the operation timed out";
        fixture.Transport.TransportFailures[Endpoints.Status] = 99;

        var thrown = Assert.Throws<PlaytestSignal>(() => ctx.Read("hostie", Reader.Status, "hosting"));
        Assert.Equal(SignalKind.Inconclusive, thrown.Kind);
        Assert.Equal("transport-error", thrown.Detector);
        Assert.Single(fixture.Transport.Requests);
    }

    [Fact]
    public void AReaderErrorWithNoMatchingDetectorIsReaderUnreachable()
    {
        var (fixture, ctx) = Rig("hostie");
        fixture.Transport.Refusals[Endpoints.Status] = (1, 500, "something threw inside the route");

        var thrown = Assert.Throws<PlaytestSignal>(() => ctx.Read("hostie", Reader.Status, "hosting"));
        Assert.Equal("transport-error", thrown.Detector);
        Assert.Contains("nothing can be concluded", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInstanceThatIsNotProvisionedNamesTheCommandThatFixesIt()
    {
        var fixture = new PlaytestFixture();
        var spec = new CheckSpec("a check", "s", [new InstanceSpec("ghost")]);
        var ctx = fixture.Context(spec);

        var thrown = Assert.Throws<PlaytestSignal>(() => ctx.Read("ghost", Reader.Status));
        Assert.Equal(Detectors.InstanceNotProvisioned, thrown.Detector);
        Assert.Contains("testrig create -Target ghost", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLockIsRefreshedAtMostOnceAMinuteAndOnlyWhileDrivingSomething()
    {
        var (fixture, ctx) = Rig("hostie");

        ctx.Act("hostie", Endpoints.Status);
        Assert.DoesNotContain("refresh-lock a1b2c3", fixture.Launcher.Calls);

        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        ctx.Act("hostie", Endpoints.Status);
        Assert.Single(fixture.Launcher.Calls, c => c.StartsWith("refresh-lock", StringComparison.Ordinal));

        ctx.Act("hostie", Endpoints.Status);
        Assert.Single(fixture.Launcher.Calls, c => c.StartsWith("refresh-lock", StringComparison.Ordinal));
    }

    [Fact]
    public void LosingTheLockMidCheckIsInconclusiveAndAborts()
    {
        var (fixture, ctx) = Rig("hostie");
        fixture.Launcher.RefreshSucceeds = false;
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));

        var thrown = Assert.Throws<PlaytestSignal>(() => ctx.Act("hostie", Endpoints.Status));
        Assert.Equal("lock-lost", thrown.Detector);
        Assert.Contains("no longer owns the rig", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThereIsNoBackgroundRefresherAndNothingRefreshesWithoutAnOwner()
    {
        var fixture = new PlaytestFixture().WithInstance("hostie", 27701);
        var spec = new CheckSpec("a check", "s", [new InstanceSpec("hostie")]);
        var ctx = fixture.Context(spec, owner: string.Empty);

        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        ctx.Act("hostie", Endpoints.Status);
        Assert.DoesNotContain(fixture.Launcher.Calls, c => c.StartsWith("refresh-lock", StringComparison.Ordinal));
    }

    [Fact]
    public void AnActionResultHandsBackTheTypedResponseTheEndpointProduced()
    {
        var (_, ctx) = Rig("hostie");
        var result = ctx.Act("hostie", Endpoints.SpawnStructure, new SpawnStructureRequest { Prefab = "StructureCableStraight", ColorIndex = 1 });

        var spawned = result.As<SpawnStructureResponse>();
        Assert.NotNull(spawned);
        Assert.True(spawned.ReferenceId > 0);
        Assert.Equal(1, spawned.ColorIndex);
    }

    [Fact]
    public void ARestartRequiresTheLockBecauseEveryMutatingCommandCarriesTheOwner()
    {
        var fixture = new PlaytestFixture().WithInstance("hostie", 27701);
        var spec = new CheckSpec("a check", "s", [new InstanceSpec("hostie")]);
        var ctx = fixture.Context(spec, owner: string.Empty);

        Assert.Throws<PlaytestUsageException>(() => ctx.RestartInstance("hostie"));
    }

    [Fact]
    public void ARestartThatCannotStartTheInstanceIsInconclusive()
    {
        var (fixture, ctx) = Rig("hostie");
        fixture.Launcher.StartFails.Add("hostie");

        var thrown = Assert.Throws<PlaytestSignal>(() => ctx.RestartInstance("hostie", "because"));
        Assert.Equal(Detectors.InstanceRestartFailed, thrown.Detector);
    }

    [Fact]
    public void ARestartStopsAndStartsThatOneInstanceByNameAndNeverRigWide()
    {
        var (fixture, ctx) = Rig("hostie", "joiner");
        ctx.RestartInstance("hostie", "the workshop park");

        Assert.Equal(["stop hostie", "start hostie"], fixture.Launcher.Calls);
        Assert.DoesNotContain(fixture.Launcher.Calls, c => c.Contains("all", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fixture.Launcher.Calls, c => c.Contains("clients", StringComparison.OrdinalIgnoreCase));
    }
}
