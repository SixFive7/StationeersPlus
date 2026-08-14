using TestRig.Playtest.Model;
using TestRig.Playtest.Runner;
using TestRig.Tests.Playtest.Fakes;
using Xunit;

namespace TestRig.Tests.Playtest;

/// <summary>
///     Guaranteed teardown, and the lock policy around it.
/// </summary>
/// <remarks>
///     Instances are stopped one at a time, by name, joiners first and hosts last. A rig-wide
///     stop would reach every instance on the machine including another session's live test.
///     A stop that fails does not skip the release, because an instance left up holds the rig
///     but a lock left held blocks every other agent too.
/// </remarks>
public sealed class TeardownTests
{
    private static string _checkFile = string.Empty;

    /// <summary>
    ///     A rig with the mod under test genuinely seeded, so attestation passes and the check
    ///     body actually runs.
    /// </summary>
    /// <remarks>
    ///     Without the seed every one of these would end at the attestation gate before the
    ///     body, and half of them would then be asserting on a path they never reached, which
    ///     is the shape this suite exists to avoid.
    /// </remarks>
    private static PlaytestFixture Rig()
    {
        var fixture = new PlaytestFixture();
        fixture.WithInstance("hostie", 27701, "host");
        fixture.WithInstance("joiner", 27702);
        _checkFile = fixture.SeedMod("SprayPaintPlus", "net.spraypaintplus",
            System.Text.Encoding.UTF8.GetBytes("the build"), ["hostie", "joiner"]);
        return fixture;
    }

    private static CheckSpec Spec() => new("a check", "s",
    [
        new InstanceSpec("hostie", InstanceRole.Host, World: "Lunar"),
        new InstanceSpec("joiner", InstanceRole.Client, ConnectTo: "hostie"),
    ], sourceFile: _checkFile);

    private static CheckResult Run(PlaytestFixture fixture, Action<IPlaytestContext>? body = null, CheckSpec? spec = null)
    {
        var checkSpec = spec ?? Spec();
        var check = new TestCheck(checkSpec, body ?? (_ => { }));
        return new CheckRunner(fixture.Dependencies).Run(check, null, "01-a-check", 0);
    }

    [Fact]
    public void TheLockIsTakenOnceAndReleasedOncePerCheck()
    {
        var fixture = Rig();
        Run(fixture);

        Assert.Single(fixture.Launcher.Calls, c => c.StartsWith("lock ", StringComparison.Ordinal));
        Assert.Single(fixture.Launcher.Calls, c => c.StartsWith("unlock ", StringComparison.Ordinal));
    }

    [Fact]
    public void TheLockPurposeAndTtlComeFromTheCheck()
    {
        var fixture = Rig();
        var spec = new CheckSpec("a named check", "s", [new InstanceSpec("hostie", InstanceRole.Host)], ttlMinutes: 25, sourceFile: _checkFile);
        Run(fixture, spec: spec);

        Assert.Contains(fixture.Launcher.Calls, c => c.Contains("Playtest: a named check", StringComparison.Ordinal));
        Assert.Contains(fixture.Launcher.Calls, c => c.Contains("ttl=25", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDefaultTtlIsLongerThanTheLaunchersOwnBecauseACheckOutlivesTenMinutes()
    {
        var spec = new CheckSpec("a check", "s", [new InstanceSpec("hostie")]);
        Assert.Equal(20, spec.TtlMinutes);
    }

    [Fact]
    public void InstancesAreStoppedByNameOneAtATime()
    {
        var fixture = Rig();
        Run(fixture);

        var stops = fixture.Launcher.Calls.Where(c => c.StartsWith("stop ", StringComparison.Ordinal)).ToList();
        Assert.Equal(["stop joiner", "stop hostie"], stops);
    }

    [Fact]
    public void JoinersAreStoppedBeforeTheInstanceHoldingTheWorld()
    {
        var fixture = Rig();
        Run(fixture);

        var stops = fixture.Launcher.Calls.Where(c => c.StartsWith("stop ", StringComparison.Ordinal)).ToList();
        Assert.True(stops.IndexOf("stop joiner") < stops.IndexOf("stop hostie"));
    }

    [Fact]
    public void TeardownNeverIssuesARigWideStop()
    {
        var fixture = Rig();
        Run(fixture);

        Assert.DoesNotContain(fixture.Launcher.Calls, c => c.Equals("stop all", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fixture.Launcher.Calls, c => c.Equals("stop clients", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AStopThatIsRefusedIsRetriedWithForceAndTheSuccessIsRecorded()
    {
        // Defect P-13. This success path had never executed in any test: the PowerShell fake
        // launcher's stop handler did not inspect the force flag at all, so it could not
        // represent a launcher that refuses without it and succeeds with it, and the only test
        // that reached the branch failed BOTH attempts and landed on the double-failure note.
        // The launcher genuinely refuses to quit on top of a world whose save it could not
        // confirm, and a check's world is created fresh with no station name, so the refusal
        // fires on EVERY host check.
        var fixture = Rig();
        fixture.Launcher.StopRefusesWithoutForce.Add("hostie");

        var result = Run(fixture);

        Assert.Contains("stop hostie", fixture.Launcher.Calls);
        Assert.Contains("stop hostie -Force", fixture.Launcher.Calls);
        Assert.Contains(result.TeardownNotes, n => n.StartsWith("stopped 'hostie' with -Force after:", StringComparison.Ordinal));
        Assert.DoesNotContain(result.TeardownNotes, n => n.Contains("failed even with -Force", StringComparison.Ordinal));
    }

    [Fact]
    public void AStopThatFailsEvenWithForceIsRecordedAndDoesNotStopTheOtherStops()
    {
        var fixture = Rig();
        fixture.Launcher.StopAlwaysFails.Add("joiner");

        var result = Run(fixture);

        Assert.Contains(result.TeardownNotes, n => n.Contains("failed even with -Force", StringComparison.Ordinal));
        Assert.Contains("stop hostie", fixture.Launcher.Calls);
    }

    [Fact]
    public void AFailingStopNeverSkipsTheRelease()
    {
        var fixture = Rig();
        fixture.Launcher.StopAlwaysFails.Add("hostie");
        fixture.Launcher.StopAlwaysFails.Add("joiner");

        Run(fixture);
        Assert.Contains(fixture.Launcher.Calls, c => c.StartsWith("unlock ", StringComparison.Ordinal));
    }

    [Fact]
    public void AFailingReleaseIsRecordedAndNeverChangesTheOutcome()
    {
        var fixture = Rig();
        fixture.Launcher.ReleaseSucceeds = false;

        var result = Run(fixture, ctx => ctx.AssertValue("hostie", Reader.Status, TestRig.Playtest.Values.ValueMatcher.Is(true), "hosting", "hosting"));

        Assert.Contains(result.TeardownNotes, n => n.Contains("could not be released", StringComparison.Ordinal));
        Assert.NotEqual(CheckOutcome.Fail, result.Outcome);
    }

    [Fact]
    public void ARefusedLockIsInconclusiveAndNothingIsDriven()
    {
        var fixture = Rig();
        fixture.Launcher.LockSucceeds = false;

        var result = Run(fixture);

        Assert.Equal(CheckOutcome.Inconclusive, result.Outcome);
        Assert.Equal(Detectors.RigUnavailable, result.Detector);
        Assert.DoesNotContain(fixture.Launcher.Calls, c => c.StartsWith("start ", StringComparison.Ordinal));
    }

    [Fact]
    public void ALockGrantedWithoutAnOwnerIsUnusableAndSaysSo()
    {
        // The PowerShell harness recovered the owner id with a regex over launcher prose, and
        // that line has never once been printed, so every check would have thrown
        // rig-unavailable and then unlocked with the id it never received. The seam is typed
        // now; this is what happens when the launcher still answers with nothing.
        var fixture = Rig();
        fixture.Launcher.Owner = string.Empty;

        var result = Run(fixture);

        Assert.Equal(CheckOutcome.Inconclusive, result.Outcome);
        Assert.Equal(Detectors.RigUnavailable, result.Detector);
        Assert.Contains("no owner id", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOwnerIdReachesEveryMutatingCommand()
    {
        var fixture = Rig();
        fixture.Launcher.Owner = "deadbeef";
        var result = Run(fixture);

        Assert.Equal("deadbeef", result.LockOwner);
        Assert.Contains("unlock deadbeef", fixture.Launcher.Calls);
    }

    [Fact]
    public void ABodyThatThrowsStillGetsTheWholeTeardown()
    {
        var fixture = Rig();
        var result = Run(fixture, _ => throw new InvalidOperationException("the check exploded"));

        Assert.Equal(CheckOutcome.Inconclusive, result.Outcome);
        Assert.Equal(Detectors.UnclassifiedError, result.Detector);
        Assert.Contains("stop joiner", fixture.Launcher.Calls);
        Assert.Contains("stop hostie", fixture.Launcher.Calls);
        Assert.Contains(fixture.Launcher.Calls, c => c.StartsWith("unlock ", StringComparison.Ordinal));
    }

    [Fact]
    public void ACheckThatNeverStartedAnythingStopsNothing()
    {
        var fixture = Rig();
        fixture.Launcher.LockSucceeds = false;
        Run(fixture);

        Assert.DoesNotContain(fixture.Launcher.Calls, c => c.StartsWith("stop ", StringComparison.Ordinal));
    }

    [Fact]
    public void TheStopTimeoutIsTheOneTheHarnessHasAlwaysUsed() =>
        Assert.Equal(60, CheckRunner.StopTimeoutSeconds);
}
