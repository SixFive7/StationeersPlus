using TestRig.Contracts;
using TestRig.Playtest.Model;
using TestRig.Playtest.Runner;
using TestRig.Playtest.Values;
using TestRig.Tests.Playtest.Fakes;
using Xunit;

namespace TestRig.Tests.Playtest;

/// <summary>
///     Asserting on the authority, never on the actor's own answer.
/// </summary>
/// <remarks>
///     An endpoint's own 200 is a statement about the request, not about the world. This comes
///     from two live failures: a connect answered ok while nothing had joined, and an arm
///     reported confirmed while the host-side check was inconclusive.
/// </remarks>
public sealed class AuthorityTests
{
    private static (PlaytestFixture Fixture, PlaytestContext Context) Rig(params string[] instances)
    {
        var fixture = new PlaytestFixture();
        var port = 27701;
        foreach (var name in instances) fixture.WithInstance(name, port++);

        var spec = new CheckSpec("a check", "summary", [.. instances.Select(n => new InstanceSpec(n))]);
        return (fixture, fixture.Context(spec));
    }

    // ---- the type-level guard ---------------------------------------------

    [Fact]
    public void AnInstanceThatIsNotOneOfTheChecksIsRefusedWithTheReasonWhy()
    {
        var (_, ctx) = Rig("hostie");
        var thrown = Assert.Throws<PlaytestUsageException>(() => ctx.Read("stranger", Reader.Status));
        Assert.Contains("not one of this check's instances", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("AUTHORITY", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGuardCoversDrivingAsWellAsReading()
    {
        var (_, ctx) = Rig("hostie");
        Assert.Throws<PlaytestUsageException>(() => ctx.Act("stranger", Endpoints.Status));
    }

    [Fact]
    public void AMisuseOfTheGuardIsInconclusiveAndNeverAFailure()
    {
        var (_, ctx) = Rig("hostie");
        var thrown = Record.Exception(() => ctx.Read("stranger", Reader.Status))!;
        Assert.Equal(CheckOutcome.Inconclusive, SignalClassifier.Classify(thrown).Outcome);
    }

    [Fact]
    public void AnActionResultIsNotSomethingAnAssertVerbCanAccept()
    {
        // In PowerShell this needed a runtime guard that string-coerced its argument and
        // rejected anything rendering as @{...}. Here it is the type signature: there is no
        // overload of any assert verb that takes an ActionResult.
        var asserts = typeof(IPlaytestContext).GetMethods()
            .Where(m => m.Name.StartsWith("Assert", StringComparison.Ordinal));

        Assert.NotEmpty(asserts);
        foreach (var method in asserts)
        {
            Assert.DoesNotContain(method.GetParameters(), p => p.ParameterType == typeof(ActionResult));
        }
    }

    [Fact]
    public void ThereIsNoBareBooleanAssertAnywhereOnTheApi()
    {
        // There is deliberately no AssertOk, no AssertTrue and no AssertResponse.
        var names = typeof(IPlaytestContext).GetMethods().Select(m => m.Name).ToList();
        Assert.DoesNotContain("AssertOk", names);
        Assert.DoesNotContain("AssertTrue", names);
        Assert.DoesNotContain("AssertResponse", names);
        Assert.Contains("AssertValue", names);
    }

    // ---- reading through the catalogue ------------------------------------

    [Fact]
    public void TheStatusReaderReadsWhatTheProcessComputed()
    {
        var (fixture, ctx) = Rig("hostie");
        fixture.Transport.State("hostie").Hosting = true;
        fixture.Transport.State("hostie").Role = "listenHost";

        Assert.Equal("True", ctx.Read("hostie", Reader.Status, "hosting").Text);
        Assert.Equal("listenHost", ctx.Read("hostie", Reader.Status, "role").Text);
    }

    [Fact]
    public void TheRosterReaderIsTheServerSideAnswer()
    {
        var (fixture, ctx) = Rig("hostie");
        var state = fixture.Transport.State("hostie");
        state.Hosting = true;
        state.Roster.Add(new ConnectedClient { ClientId = "1", Username = "host", IsHost = true });
        state.Roster.Add(new ConnectedClient { ClientId = "2", Username = "joiner", IsHost = false });

        Assert.Equal("2", ctx.Read("hostie", Reader.Roster, "count").Text);
        Assert.Equal("joiner", ctx.Read("hostie", Reader.Roster, "username", of: "2").Text);
    }

    [Fact]
    public void ARosterReadOnANonServerIsEmptyBecauseThatIsWhatThePluginAnswers()
    {
        // The "did the joiner arrive" question is the host's roster, never the joiner's own
        // answer, and the plugin enforces that by returning an empty list off a server.
        var (fixture, ctx) = Rig("joiner");
        fixture.Transport.State("joiner").Roster.Add(new ConnectedClient { ClientId = "2", Username = "joiner" });
        Assert.Equal("0", ctx.Read("joiner", Reader.Roster, "count").Text);
    }

    [Fact]
    public void TheConfigReaderNarrowsBySectionAndKey()
    {
        var (fixture, ctx) = Rig("hostie");
        fixture.Transport.State("hostie").SetConfig("net.example", "Server - Glow Paint", "Glow Paint", "false");

        var observation = ctx.Read("hostie", Reader.Config, "value", "Server - Glow Paint/Glow Paint", new ConfigRequest { Guid = "net.example" });
        Assert.Equal("false", observation.Text);
    }

    [Fact]
    public void TheThingReaderNarrowsToARowAndThenToAFieldRow()
    {
        var (fixture, ctx) = Rig("hostie");
        var state = fixture.Transport.State("hostie");
        state.Things["442"] = new FakeThing { ReferenceId = 442, PrefabName = "StructureCableStraight", CustomColorIndex = 4, Authoritative = true };
        state.Things["442"].Members["EmissionColor.r"] = "0";

        var args = new ThingRequest { RefIds = "442", Fields = "EmissionColor.r" };
        Assert.Equal("4", ctx.Read("hostie", Reader.Thing, "customColorIndex", "442", args).Text);
        Assert.Equal("True", ctx.Read("hostie", Reader.Thing, "location.authoritative", "442", args).Text);
        Assert.Equal("0", ctx.Read("hostie", Reader.Thing, "value", "442/EmissionColor.r", args).Text);
    }

    [Fact]
    public void TheThingReaderResolvesAFieldByItsResolvedNameToo()
    {
        var (fixture, ctx) = Rig("hostie");
        fixture.Transport.State("hostie").Things["442"] = new FakeThing { ReferenceId = 442 };
        fixture.Transport.State("hostie").Things["442"].Members["CustomColor.Index"] = "12";

        var args = new ThingRequest { RefIds = "442", Fields = "CustomColor.Index" };
        Assert.Equal("12", ctx.Read("hostie", Reader.Thing, "value", "442/CustomColor.Index", args).Text);
    }

    [Fact]
    public void ThePlayerReaderReturnsThePlayerBlockAndNotTheEnvelope()
    {
        // Defect P-16. The catalogue has always documented this reader as "the player block
        // only" and the narrowing had no player case, so it returned the whole
        // {ok, epoch, player} envelope and a check written to the documentation read absent.
        var (fixture, ctx) = Rig("hostie");
        fixture.Transport.State("hostie").Player = new PlayerBlock { Present = true, DisplayName = "tester", Position = [1.5, 2.5, 3.5] };

        Assert.Equal("True", ctx.Read("hostie", Reader.Player, "present").Text);
        Assert.Equal("tester", ctx.Read("hostie", Reader.Player, "displayName").Text);
    }

    [Fact]
    public void APlayerPositionIsAnArrayBecauseThatIsWhatThePluginEmits()
    {
        var (_, ctx) = Rig("hostie");
        Assert.Equal("1.5", ctx.Read("hostie", Reader.Player, "position[0]").Text);
        Assert.Null(ctx.Read("hostie", Reader.Player, "position.x").Value);
    }

    [Fact]
    public void TheDlcReaderReadsTheStateBlockWhereTheRealEndpointPutsIt()
    {
        // The exact shape mismatch the port exists to make impossible: the old fake answered
        // {ok, owned} at the top level while every real check reads state.owned.
        var (fixture, ctx) = Rig("hostie");
        var state = fixture.Transport.State("hostie");
        state.Owned = "MetallicPaints";
        state.Shared = "MetallicPaints";

        Assert.Equal("MetallicPaints", ctx.Read("hostie", Reader.Dlc, "state.owned").Text);
        Assert.Equal("MetallicPaints", ctx.Read("hostie", Reader.Dlc, "state.shared").Text);
        Assert.Null(ctx.Read("hostie", Reader.Dlc, "owned").Value);
    }

    [Fact]
    public void TheNearbyReaderCarriesCustomColorIndexAndNotColorIndex()
    {
        // The old fake named this field colorIndex, so a check reading customColorIndex got
        // absent from the fake and a real number from the endpoint. A name divergence that
        // reads as an absent field is the hardest kind to notice.
        var (fixture, ctx) = Rig("hostie");
        fixture.Transport.State("hostie").Things["442"] = new FakeThing { ReferenceId = 442, CustomColorIndex = 9 };

        Assert.Equal("9", ctx.Read("hostie", Reader.Nearby, "customColorIndex", of: "442").Text);
        Assert.Equal("1", ctx.Read("hostie", Reader.Nearby, "count").Text);
    }

    [Fact]
    public void TheConsoleReaderAppliesEverySeverFilterItIsGiven()
    {
        // The largest single coverage hole in the PowerShell suite: fifteen console-count
        // assertions across six shipped checks, and a fake that ignored since, contains,
        // source and limit and answered count:1 forever.
        var (fixture, ctx) = Rig("hostie");
        var state = fixture.Transport.State("hostie");
        state.Print("console", "[Spray Paint Plus] before the baseline");

        var seq = ctx.Read("hostie", Reader.Console, "nextSeq", readerArgs: new ConsoleLogRequest { Limit = 1 });
        var since = long.Parse(seq.Text, System.Globalization.CultureInfo.InvariantCulture);

        state.Print("console", "[Spray Paint Plus] Network Paint Cables is turned off");
        state.Print("console", "[Spray Paint Plus] Network Paint Cables is turned off");
        state.Print("bepinex", "[Spray Paint Plus] Network Paint Cables is turned off");
        state.Print("console", "something else entirely");

        var counted = ctx.Read("hostie", Reader.Console, "count",
            readerArgs: new ConsoleLogRequest { Since = since, Source = "console", Contains = "Network Paint Cables", Limit = 200 });

        Assert.Equal("2", counted.Text);
    }

    [Fact]
    public void TheConsoleReaderCountsNothingWhenTheWindowExcludesEverything()
    {
        var (fixture, ctx) = Rig("hostie");
        fixture.Transport.State("hostie").Print("console", "[Spray Paint Plus] Network Paint Cables is turned off");

        var seq = ctx.Read("hostie", Reader.Console, "nextSeq", readerArgs: new ConsoleLogRequest { Limit = 1 });
        var since = long.Parse(seq.Text, System.Globalization.CultureInfo.InvariantCulture);

        var counted = ctx.Read("hostie", Reader.Console, "count",
            readerArgs: new ConsoleLogRequest { Since = since, Source = "console", Contains = "Network Paint Cables", Limit = 200 });

        Assert.Equal("0", counted.Text);
    }

    [Fact]
    public void ConsoleLinesAreObjectsWithASourceAndAText()
    {
        var (fixture, ctx) = Rig("hostie");
        fixture.Transport.State("hostie").Print("console", "hello");

        Assert.Equal("hello", ctx.Read("hostie", Reader.Console, "lines[0].text").Text);
        Assert.Equal("console", ctx.Read("hostie", Reader.Console, "lines[0].src").Text);
    }

    [Fact]
    public void EveryReaderThatTakesNoQueryIgnoresReaderArgs()
    {
        var (fixture, ctx) = Rig("hostie");
        ctx.Read("hostie", Reader.Status, ".", string.Empty, new ConfigRequest { Guid = "net.example" });
        Assert.Contains("hostie GET /status", fixture.Transport.Requests);
    }

    // ---- assert verbs -----------------------------------------------------

    [Fact]
    public void AnAssertionThatReadsTheRightValuePassesAndReturnsItsObservation()
    {
        var (fixture, ctx) = Rig("hostie");
        fixture.Transport.State("hostie").Hosting = true;

        var observation = ctx.AssertValue("hostie", Reader.Status, ValueMatcher.Is(true), "the host owns the world", "hosting");
        Assert.Equal("True", observation.Text);
        Assert.Equal(1, ctx.AssertionCount);
    }

    [Fact]
    public void AnAssertionThatReadsTheWrongValueFails()
    {
        var (_, ctx) = Rig("hostie");
        var thrown = Assert.Throws<PlaytestSignal>(() =>
            ctx.AssertValue("hostie", Reader.Status, ValueMatcher.Is(true), "the host owns the world", "hosting"));

        Assert.Equal(SignalKind.Fail, thrown.Kind);
        Assert.Equal(Detectors.Assertion, thrown.Detector);
    }

    [Fact]
    public void AFailureMessageNamesTheReadingTheExpectationAndTheReason()
    {
        var (_, ctx) = Rig("hostie");
        var thrown = Assert.Throws<PlaytestSignal>(() =>
            ctx.AssertValue("hostie", Reader.Status, ValueMatcher.Is(true), "a report saying hosting was False is a puzzle", "hosting"));

        Assert.Contains("hostie.status.hosting", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("is [True]", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("but it was [False]", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("a report saying hosting was False is a puzzle", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedBoundSaysWhyRatherThanJustPrintingTheValue()
    {
        var (_, ctx) = Rig("hostie");
        var thrown = Assert.Throws<PlaytestSignal>(() =>
            ctx.AssertValue("hostie", Reader.Status, ValueMatcher.AtMost(3), "a typo must not read as a pass", "noSuchField"));

        Assert.Equal(SignalKind.Fail, thrown.Kind);
        Assert.Contains("ABSENT", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryAssertionNeedsAReason()
    {
        var (_, ctx) = Rig("hostie");
        Assert.Throws<PlaytestUsageException>(() =>
            ctx.AssertValue("hostie", Reader.Status, ValueMatcher.Is(true), string.Empty, "hosting"));
    }

    [Fact]
    public void EveryAssertionCounts()
    {
        var (fixture, ctx) = Rig("hostie", "joiner");
        fixture.Transport.State("hostie").Hosting = true;

        ctx.AssertValue("hostie", Reader.Status, ValueMatcher.Is(true), "one", "hosting");
        ctx.AssertAgreement(["hostie", "joiner"], Reader.Status, "two", "saveRootIsolated");

        Assert.Equal(2, ctx.AssertionCount);
    }

    [Fact]
    public void AReadOnItsOwnIsNotAnAssertion()
    {
        var (_, ctx) = Rig("hostie");
        ctx.Read("hostie", Reader.Status, "hosting");
        Assert.Equal(0, ctx.AssertionCount);
    }

    [Fact]
    public void AgreementNeedsAtLeastTwoInstances()
    {
        var (_, ctx) = Rig("hostie");
        var thrown = Assert.Throws<PlaytestUsageException>(() => ctx.AssertAgreement(["hostie"], Reader.Status, "why", "hosting"));
        Assert.Contains("agreement with itself", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AgreementPassesWhenBothInstancesSayTheSameThing()
    {
        var (_, ctx) = Rig("hostie", "joiner");
        var observations = ctx.AssertAgreement(["hostie", "joiner"], Reader.Status, "both halves agree", "saveRootIsolated");
        Assert.Equal(2, observations.Count);
    }

    [Fact]
    public void AgreementFailsWhenTheyDisagreeAndSaysWhichIsWhich()
    {
        var (fixture, ctx) = Rig("hostie", "joiner");
        fixture.Transport.State("hostie").Phase = "inWorld";

        var thrown = Assert.Throws<PlaytestSignal>(() => ctx.AssertAgreement(["hostie", "joiner"], Reader.Status, "both should be in world", "phase"));
        Assert.Equal(SignalKind.Fail, thrown.Kind);
        Assert.Contains("hostie=[inWorld]", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("joiner=[menu]", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AgreementOnTheWrongValueIsItsOwnFailure()
    {
        var (_, ctx) = Rig("hostie", "joiner");
        var thrown = Assert.Throws<PlaytestSignal>(() =>
            ctx.AssertAgreement(["hostie", "joiner"], Reader.Status, "both should be in world", "phase", isValue: "inWorld", pinValue: true));

        Assert.Contains("agree about status.phase but on the wrong value", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangeReproducesTheBaselineRequestExactlyIncludingItsReaderArgs()
    {
        // This was a shipped defect: without the args the re-read went out as a bare thing
        // read, the endpoint answered 400, and every before-and-after check on a per-Thing
        // field ended inconclusive with no comparison made.
        var (fixture, ctx) = Rig("hostie");
        var state = fixture.Transport.State("hostie");
        state.Things["442"] = new FakeThing { ReferenceId = 442, CustomColorIndex = 1 };

        var baseline = ctx.Read("hostie", Reader.Thing, "customColorIndex", "442", new ThingRequest { RefIds = "442", Fields = "CustomColor" });
        state.Things["442"].CustomColorIndex = 4;

        var after = ctx.AssertChange(baseline, "the stroke landed", to: 4);
        Assert.Equal("4", after.Text);
        Assert.All(fixture.Transport.Requests.Where(r => r.Contains("/thing", StringComparison.Ordinal)), r => Assert.Contains("/thing", r, StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Transport.Requests, r => r.EndsWith(" GET /thing?", StringComparison.Ordinal));
    }

    [Fact]
    public void AReReadWithoutTheReaderArgsWouldBeARealFourHundred()
    {
        // The fake reproduces the endpoint's own 400 for a query-less thing read, which is
        // what makes the assertion above a measurement rather than a decoration.
        var (_, ctx) = Rig("hostie");
        var thrown = Assert.Throws<PlaytestSignal>(() => ctx.Read("hostie", Reader.Thing, "customColorIndex", "442"));
        Assert.Equal(SignalKind.Inconclusive, thrown.Kind);
        Assert.Contains("400", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangeUnchangedFailsWhenTheValueMoved()
    {
        var (fixture, ctx) = Rig("hostie");
        var state = fixture.Transport.State("hostie");
        state.Things["445"] = new FakeThing { ReferenceId = 445, CustomColorIndex = 1 };

        var baseline = ctx.Read("hostie", Reader.Thing, "customColorIndex", "445", new ThingRequest { RefIds = "445", Fields = "CustomColor" });
        state.Things["445"].CustomColorIndex = 4;

        var thrown = Assert.Throws<PlaytestSignal>(() => ctx.AssertChange(baseline, "nobody aimed at it", unchanged: true));
        Assert.Equal(SignalKind.Fail, thrown.Kind);
        Assert.Contains("expected to stay at [1] and is now [4]", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangeToFailsWhenTheValueDidNotArrive()
    {
        var (fixture, ctx) = Rig("hostie");
        fixture.Transport.State("hostie").Things["445"] = new FakeThing { ReferenceId = 445, CustomColorIndex = 1 };

        var baseline = ctx.Read("hostie", Reader.Thing, "customColorIndex", "445", new ThingRequest { RefIds = "445", Fields = "CustomColor" });
        var thrown = Assert.Throws<PlaytestSignal>(() => ctx.AssertChange(baseline, "the stroke should have landed", to: 4));

        Assert.Contains("expected to become [4] and reads [1] (baseline was [1])", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangeNeedsExactlyOneOfItsTwoModes()
    {
        var (fixture, ctx) = Rig("hostie");
        fixture.Transport.State("hostie").Things["445"] = new FakeThing { ReferenceId = 445 };
        var baseline = ctx.Read("hostie", Reader.Thing, "customColorIndex", "445", new ThingRequest { RefIds = "445" });

        Assert.Throws<PlaytestUsageException>(() => ctx.AssertChange(baseline, "why", to: 4, unchanged: true));
        Assert.Throws<PlaytestUsageException>(() => ctx.AssertChange(baseline, "why"));
    }

    [Fact]
    public void ChangeUnchangedPassesWhenNothingMoved()
    {
        var (fixture, ctx) = Rig("hostie");
        fixture.Transport.State("hostie").Things["445"] = new FakeThing { ReferenceId = 445, CustomColorIndex = 1 };

        var baseline = ctx.Read("hostie", Reader.Thing, "customColorIndex", "445", new ThingRequest { RefIds = "445", Fields = "CustomColor" });
        var after = ctx.AssertChange(baseline, "nobody aimed at it", unchanged: true);
        Assert.Equal("1", after.Text);
    }
}
