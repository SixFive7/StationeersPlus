using System.Text.Json.Nodes;
using TestRig.Contracts;
using TestRig.Playtest;
using TestRig.Playtest.Flakes;
using Xunit;

namespace TestRig.Tests.Playtest;

/// <summary>
///     The flake taxonomy: nine detectors, ordered, first match wins, every one of them
///     ending a check as inconclusive and never as a failure.
/// </summary>
public sealed class FlakeTaxonomyTests
{
    private static FlakeCatalogue Catalogue() => new();

    private static StatusResponse Status(bool hosting = true, string role = "listenHost", int plugins = 42, bool initialized = true) => new()
    {
        Ok = true, Hosting = hosting, Role = role, LoadedPluginCount = plugins, GameInitialized = initialized, Phase = "inWorld",
    };

    [Fact]
    public void TheNineShippedDetectorsAreAllPresentAndInOrder()
    {
        string[] expected =
        [
            "connect-first-attempt", "launchpad-workshop-park", "host-not-hosting", "joiner-not-in-roster",
            "lock-lost", "control-plane-silent", "instance-dead", "boot-timeout", "transport-error",
        ];

        Assert.Equal(expected, Catalogue().Detectors.Select(d => d.Name).ToArray());
    }

    [Fact]
    public void EveryDetectorCarriesTheThreeThingsAReportNeeds()
    {
        foreach (var detector in Catalogue().Detectors)
        {
            Assert.False(string.IsNullOrWhiteSpace(detector.Summary), $"{detector.Name} has no summary, and the summary is embedded verbatim in the inconclusive message");
            Assert.False(string.IsNullOrWhiteSpace(detector.Reference), $"{detector.Name} has no reference");
            Assert.True(detector.MaxAttempts >= 1, $"{detector.Name} must permit at least one attempt");
        }
    }

    [Fact]
    public void NoDetectorRetriesWithoutABound()
    {
        // There is no unbounded retry anywhere in the harness, because an agent that hangs on
        // a wedged rig is worse than one that reports inconclusive and frees the lock.
        foreach (var detector in Catalogue().Detectors)
        {
            Assert.InRange(detector.MaxAttempts, 1, 10);
            Assert.InRange(detector.GapSeconds, 0, 60);
        }
    }

    [Fact]
    public void NoDetectorDeclaresARemedyItCanNeverPerform()
    {
        // A remedy only runs on the path between "this attempt failed" and "try again", so a
        // detector with one attempt throws before its remedy is reached. instance-dead
        // declared a restart and MaxAttempts 1 from the day it was written, so its restart
        // has never once happened, on either of the two sites that interpret a remedy.
        foreach (var detector in Catalogue().Detectors.Where(d => d.Remedy != FlakeRemedy.Abort))
        {
            Assert.True(detector.MaxAttempts >= 2,
                $"{detector.Name} declares {detector.Remedy} but permits only {detector.MaxAttempts} attempt, so the remedy can never run");
        }
    }

    [Fact]
    public void AnAbortRemedyNeverSleeps()
    {
        foreach (var detector in Catalogue().Detectors.Where(d => d.Remedy == FlakeRemedy.Abort))
        {
            Assert.Equal(1, detector.MaxAttempts);
            Assert.Equal(0, detector.GapSeconds);
        }
    }

    [Fact]
    public void ThereAreThreeRemediesBecauseWaitAndRetryWereTheSameThing()
    {
        // PowerShell declared four. wait and retry both slept the gap and re-issued the call,
        // with no code path anywhere distinguishing them: wait was documentation wearing a
        // remedy's clothes, and its real content lived in its own MaxAttempts and GapSeconds.
        Assert.Equal(3, Enum.GetValues<FlakeRemedy>().Length);
        var silent = Catalogue().Detectors.Single(d => d.Name == "control-plane-silent");
        Assert.Equal(FlakeRemedy.Retry, silent.Remedy);
        Assert.Equal(6, silent.MaxAttempts);
        Assert.Equal(10, silent.GapSeconds);
    }

    // ---- one fixture per detector ----------------------------------------

    [Fact]
    public void ConnectFirstAttemptMatchesATimedOutConnect()
    {
        var probe = new FlakeProbe(ProbeKind.Action, "joiner", Endpoints.Connect,
            Response: JsonNode.Parse("""{"ok":false,"result":"timeout"}"""));

        Assert.Equal("connect-first-attempt", Catalogue().Resolve(probe)?.Name);
    }

    [Fact]
    public void ConnectFirstAttemptMatchesARefusalWithAnOkFalseBody()
    {
        // The duplicate-identity refusal shape, which the PowerShell fake never produced.
        var probe = new FlakeProbe(ProbeKind.Action, "joiner", Endpoints.Connect,
            Response: JsonNode.Parse("""{"ok":false,"error":"duplicate identity","peers":[],"override":"allowDuplicateIdentity"}"""));

        Assert.Equal("connect-first-attempt", Catalogue().Resolve(probe)?.Name);
    }

    [Fact]
    public void ConnectFirstAttemptWinsOverInstanceDeadAndTransportError()
    {
        // It is first because it is documented behaviour rather than a defect: a client that
        // has just disconnected is still settling.
        var probe = new FlakeProbe(ProbeKind.Transport, "joiner", Endpoints.Connect, Error: "actively refused");
        Assert.Equal("connect-first-attempt", Catalogue().Resolve(probe)?.Name);
    }

    [Fact]
    public void ConnectFirstAttemptIgnoresOtherPaths()
    {
        var probe = new FlakeProbe(ProbeKind.Action, "joiner", Endpoints.Host, Response: JsonNode.Parse("""{"ok":false}"""));
        Assert.NotEqual("connect-first-attempt", Catalogue().Resolve(probe)?.Name);
    }

    [Fact]
    public void LaunchpadWorkshopParkMatchesTwoPluginsAndNoInitialisation()
    {
        var probe = new FlakeProbe(ProbeKind.Barrier, "hostie", Stage: "menu", Status: Status(plugins: 2, initialized: false));
        var detector = Catalogue().Resolve(probe);
        Assert.Equal("launchpad-workshop-park", detector?.Name);
        Assert.Equal(FlakeRemedy.RestartInstance, detector!.Remedy);
    }

    [Fact]
    public void LaunchpadWorkshopParkSitsAboveBootTimeout()
    {
        // So a barrier probe whose last status shows two or fewer plugins classifies as the
        // park rather than as a slow boot.
        var park = new FlakeProbe(ProbeKind.Barrier, "hostie", Status: Status(plugins: 1, initialized: false));
        var slow = new FlakeProbe(ProbeKind.Barrier, "hostie", Status: Status(plugins: 40, initialized: false));
        Assert.Equal("launchpad-workshop-park", Catalogue().Resolve(park)?.Name);
        Assert.Equal("boot-timeout", Catalogue().Resolve(slow)?.Name);
    }

    [Fact]
    public void LaunchpadWorkshopParkDoesNotFireOnAnInitialisedInstance()
    {
        var probe = new FlakeProbe(ProbeKind.Barrier, "hostie", Status: Status(plugins: 2, initialized: true));
        Assert.Equal("boot-timeout", Catalogue().Resolve(probe)?.Name);
    }

    [Fact]
    public void HostNotHostingMatchesAPostStateThatDisagreesWithTheCall()
    {
        // NetworkServer.Host() gives up quietly after three failed binds, so the call
        // returning proves nothing.
        var probe = new FlakeProbe(ProbeKind.PostState, "hostie", Endpoints.Host, Status: Status(hosting: false));
        var detector = Catalogue().Resolve(probe);
        Assert.Equal("host-not-hosting", detector?.Name);
        Assert.Equal(FlakeRemedy.Abort, detector!.Remedy);
    }

    [Fact]
    public void HostNotHostingAlsoMatchesTheWrongRole()
    {
        var probe = new FlakeProbe(ProbeKind.PostState, "hostie", Endpoints.Host, Status: Status(role: "singlePlayer"));
        Assert.Equal("host-not-hosting", Catalogue().Resolve(probe)?.Name);
    }

    [Fact]
    public void HostNotHostingMatchesAMissingStatusEntirely()
    {
        Assert.Equal("host-not-hosting", Catalogue().Resolve(new FlakeProbe(ProbeKind.PostState, "hostie", Endpoints.Host))?.Name);
    }

    [Fact]
    public void HostNotHostingDoesNotFireWhenTheHostIsActuallyHosting()
    {
        Assert.Null(Catalogue().Resolve(new FlakeProbe(ProbeKind.PostState, "hostie", Endpoints.Host, Status: Status())));
    }

    [Fact]
    public void JoinerNotInRosterMatchesAConnectPostState()
    {
        // Defect P-03: no site in the PowerShell library ever constructed this probe, so the
        // detector's own test was unreachable in production and was raised by name instead.
        var detector = Catalogue().Resolve(new FlakeProbe(ProbeKind.PostState, "joiner", Endpoints.Connect));
        Assert.Equal("joiner-not-in-roster", detector?.Name);
        Assert.Equal(FlakeRemedy.Abort, detector!.Remedy);
    }

    [Fact]
    public void LockLostMatchesAnyLockProbe()
    {
        var detector = Catalogue().Resolve(new FlakeProbe(ProbeKind.Lock, Error: "another session holds it"));
        Assert.Equal("lock-lost", detector?.Name);
        Assert.Equal(FlakeRemedy.Abort, detector!.Remedy);
    }

    [Fact]
    public void ControlPlaneSilentMatchesSilenceDuringABlockingCall()
    {
        var probe = new FlakeProbe(ProbeKind.Transport, "hostie", Endpoints.Host, Error: "timed out", Blocking: true);
        Assert.Equal("control-plane-silent", Catalogue().Resolve(probe)?.Name);
    }

    [Fact]
    public void ControlPlaneSilentSitsAboveInstanceDead()
    {
        // A blocking call freezes that instance's whole control plane, so the silence is
        // explained rather than read as a dead process.
        var blocking = new FlakeProbe(ProbeKind.Transport, "hostie", Endpoints.Save, Error: "actively refused", Blocking: true);
        var plain = new FlakeProbe(ProbeKind.Transport, "hostie", Endpoints.Status, Error: "actively refused");
        Assert.Equal("control-plane-silent", Catalogue().Resolve(blocking)?.Name);
        Assert.Equal("instance-dead", Catalogue().Resolve(plain)?.Name);
    }

    [Theory]
    [InlineData("The remote server refused the connection.")]
    [InlineData("No connection could be made because the target machine actively refused it")]
    [InlineData("unable to connect to the remote server")]
    public void InstanceDeadMatchesEveryRefusedConnectionWording(string error)
    {
        var probe = new FlakeProbe(ProbeKind.Transport, "hostie", Endpoints.Status, Error: error);
        var detector = Catalogue().Resolve(probe);
        Assert.Equal("instance-dead", detector?.Name);
        Assert.Equal(FlakeRemedy.RestartInstance, detector!.Remedy);
    }

    [Fact]
    public void InstanceDeadSitsAboveTransportErrorSoARefusalIsARestartNotThreeRetries()
    {
        var refused = new FlakeProbe(ProbeKind.Transport, "hostie", Endpoints.Status, Error: "actively refused");
        var other = new FlakeProbe(ProbeKind.Transport, "hostie", Endpoints.Status, Error: "the operation timed out");
        Assert.Equal("instance-dead", Catalogue().Resolve(refused)?.Name);
        Assert.Equal("transport-error", Catalogue().Resolve(other)?.Name);
    }

    [Fact]
    public void BootTimeoutMatchesAnyBarrier()
    {
        var detector = Catalogue().Resolve(new FlakeProbe(ProbeKind.Barrier, "hostie", Stage: "inWorld", Status: Status(initialized: false, plugins: 40)));
        Assert.Equal("boot-timeout", detector?.Name);
        Assert.Equal(2, detector!.MaxAttempts);
    }

    [Fact]
    public void TransportErrorIsTheCatchAll()
    {
        var detector = Catalogue().Resolve(new FlakeProbe(ProbeKind.Transport, "hostie", Endpoints.Status, Error: "something else entirely"));
        Assert.Equal("transport-error", detector?.Name);
        Assert.Equal(3, detector!.MaxAttempts);
    }

    [Fact]
    public void AnActionProbeThatNothingExplainsResolvesToNothing()
    {
        var probe = new FlakeProbe(ProbeKind.Action, "hostie", Endpoints.ConfigSet, Response: JsonNode.Parse("""{"ok":false,"error":"no such key"}"""));
        Assert.Null(Catalogue().Resolve(probe));
    }

    // ---- resolution and registration --------------------------------------

    [Fact]
    public void ADetectorThatThrowsIsSkippedAndReportedRatherThanSwallowingTheProbe()
    {
        var catalogue = Catalogue();
        catalogue.Register(new FlakeDetector("explodes", "s", FlakeRemedy.Abort, 1, 0, "r", _ => throw new InvalidOperationException("boom")));

        var detector = catalogue.Resolve(new FlakeProbe(ProbeKind.Lock));
        Assert.Equal("lock-lost", detector?.Name);
        Assert.Single(catalogue.Warnings);
        Assert.Contains("explodes", catalogue.Warnings[0], StringComparison.Ordinal);
        Assert.Contains("skipped", catalogue.Warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ANewDetectorIsPrependedBecauseALaterOneIsUsuallyMoreSpecific()
    {
        var catalogue = Catalogue();
        catalogue.Register(new FlakeDetector("mine", "s", FlakeRemedy.Abort, 1, 0, "r", _ => true));
        Assert.Equal("mine", catalogue.Detectors[0].Name);
        Assert.Equal("mine", catalogue.Resolve(new FlakeProbe(ProbeKind.Lock))?.Name);
    }

    [Fact]
    public void BeforeInsertsInFrontOfTheNamedDetector()
    {
        var catalogue = Catalogue();
        catalogue.Register(new FlakeDetector("mine", "s", FlakeRemedy.Abort, 1, 0, "r", _ => true), before: "lock-lost");

        var names = catalogue.Detectors.Select(d => d.Name).ToList();
        Assert.Equal(names.IndexOf("lock-lost") - 1, names.IndexOf("mine"));
    }

    [Fact]
    public void BeforeAnUnknownNameAppendsRatherThanPromoting()
    {
        // A typo must not silently promote a detector to the front of the resolution order.
        var catalogue = Catalogue();
        catalogue.Register(new FlakeDetector("mine", "s", FlakeRemedy.Abort, 1, 0, "r", _ => true), before: "no-such-detector");
        Assert.Equal("mine", catalogue.Detectors[^1].Name);
    }

    [Fact]
    public void TheCatalogueIsPerRunAndNotProcessGlobal()
    {
        // Defect P-04: registration mutated script-scoped state the runner never reset, so a
        // check file that registered a detector at load time permanently altered the taxonomy
        // for every later check in the run.
        var one = Catalogue();
        one.Register(new FlakeDetector("mine", "s", FlakeRemedy.Abort, 1, 0, "r", _ => true));

        Assert.Equal(10, one.Detectors.Count);
        Assert.Equal(9, Catalogue().Detectors.Count);
    }

    [Fact]
    public void TheListingNamesEveryDetectorAndSaysWhatTheyAllMean()
    {
        var listing = PlaytestListing.Flakes(Catalogue());
        foreach (var detector in Catalogue().Detectors) Assert.Contains(detector.Name, listing, StringComparison.Ordinal);
        Assert.Contains("INCONCLUSIVE, never as a failure", listing, StringComparison.Ordinal);
        Assert.Contains("first match wins", listing, StringComparison.Ordinal);
    }
}
