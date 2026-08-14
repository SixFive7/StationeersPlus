using TestRig.Contracts;
using TestRig.Playtest.Model;
using TestRig.Playtest.Runner;
using TestRig.Tests.Playtest.Fakes;
using Xunit;

namespace TestRig.Tests.Playtest;

/// <summary>
///     Joining: confirm from the host, poll the roster, and retry from the menu.
/// </summary>
/// <remarks>
///     This helper exists because of a specific failure. On 2026-08-11 four of eight checks
///     came back inconclusive with joiner-not-in-roster, and none of them was a join problem:
///     ten of ten hand-driven joins landed on the same rig the same evening. What failed was
///     the SECOND connect, the one a check body issues after disconnecting to change a client
///     half, because that path had its own copy of the logic and the copy did not retry.
/// </remarks>
public sealed class ConnectJoinerTests
{
    private static (PlaytestFixture Fixture, PlaytestContext Context) Rig()
    {
        var fixture = new PlaytestFixture();
        fixture.WithInstance("hostie", 27701, "host");
        fixture.WithInstance("joiner", 27702);

        var host = fixture.Transport.State("hostie");
        host.Hosting = true;
        host.Role = "listenHost";
        host.HostPort = 27801;
        host.Phase = "inWorld";
        host.Roster.Add(new ConnectedClient { ClientId = "900000000001", Username = "hostie", IsHost = true });

        var spec = new CheckSpec("a check", "s",
        [
            new InstanceSpec("hostie", InstanceRole.Host, World: "Lunar"),
            new InstanceSpec("joiner"),
        ]);

        return (fixture, fixture.Context(spec));
    }

    [Fact]
    public void ACleanJoinTakesOneAttemptAndIsConfirmedFromTheHostRoster()
    {
        var (fixture, ctx) = Rig();
        var join = ctx.ConnectJoiner("joiner", "hostie");

        Assert.Equal(1, join.Attempts);
        Assert.Equal(2, join.Roster.Count);
        Assert.Contains(join.Roster, r => r.Username == "joiner");
        Assert.False(ctx.Degraded);
        _ = fixture;
    }

    [Fact]
    public void ThePortIsReadOffTheHostRatherThanGuessed()
    {
        var (fixture, ctx) = Rig();
        fixture.Transport.State("hostie").HostPort = 27999;
        ctx.ConnectJoiner("joiner", "hostie");

        Assert.Contains(fixture.Transport.Bodies, b => b.Contains("\"port\":27999", StringComparison.Ordinal));
    }

    [Fact]
    public void AHostWithNoGamePortIsRefusedBeforeAnythingIsDialled()
    {
        var (fixture, ctx) = Rig();
        fixture.Transport.State("hostie").HostPort = 0;

        var thrown = Assert.Throws<PlaytestSignal>(() => ctx.ConnectJoiner("joiner", "hostie"));
        Assert.Equal("host-not-hosting", thrown.Detector);
        Assert.Contains("nothing to join", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRosterIsPolledBecauseInWorldAndTheRowAppearingAreDifferentInstants()
    {
        var (fixture, ctx) = Rig();
        fixture.Transport.RosterPollsBeforeArrival = 3;

        var join = ctx.ConnectJoiner("joiner", "hostie");
        Assert.Equal(1, join.Attempts);
        Assert.Equal(2, join.Roster.Count);
    }

    [Fact]
    public void AFirstAttemptThatTimesOutIsRetriedFromTheMenu()
    {
        // Three failures, because the connect endpoint's own retry bound is three: the helper's
        // outer loop only takes over once the inner one has given up, which is exactly the
        // window the hand-rolled copies of this logic never covered.
        var (fixture, ctx) = Rig();
        fixture.Transport.ConnectFailuresBeforeSuccess = 3;

        var join = ctx.ConnectJoiner("joiner", "hostie");
        Assert.Equal(2, join.Attempts);
        Assert.True(ctx.Degraded);
        Assert.Contains("connect-first-attempt", ctx.RecordedDetectors);
        Assert.Contains("joiner POST /disconnect", fixture.Transport.Requests);
    }

    [Fact]
    public void EveryAttemptExhaustedWithoutARosterRowIsInconclusive()
    {
        var (fixture, ctx) = Rig();
        fixture.Transport.JoinerNeverArrives = true;

        var thrown = Assert.Throws<PlaytestSignal>(() => ctx.ConnectJoiner("joiner", "hostie", attempts: 2, rosterPollSeconds: 4));
        Assert.Equal("joiner-not-in-roster", thrown.Detector);
        Assert.Contains("did not grow", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenEveryAttemptDiedAtTheConnectItsOwnSignalSurvives()
    {
        // Rethrowing the last error keeps its detector instead of relabelling it as a roster
        // problem it is not.
        var (fixture, ctx) = Rig();
        fixture.Transport.ConnectFailuresBeforeSuccess = 99;

        var thrown = Assert.Throws<PlaytestSignal>(() => ctx.ConnectJoiner("joiner", "hostie", attempts: 2));
        Assert.Equal("connect-first-attempt", thrown.Detector);
    }

    [Fact]
    public void TheSequenceBeforeConnectIsReadFromTheAttemptThatLanded()
    {
        // Anything the mod prints once per JOIN appears once per attempt, so a check that
        // baselined before the helper ran counted three lines after three attempts and failed
        // a correct mod.
        var (fixture, ctx) = Rig();
        var joiner = fixture.Transport.State("joiner");
        joiner.Print("console", "noise before any attempt");
        fixture.Transport.ConnectFailuresBeforeSuccess = 3;

        var first = joiner.NextSeq;
        var join = ctx.ConnectJoiner("joiner", "hostie");

        Assert.NotNull(join.SeqBeforeConnect);
        Assert.True(join.SeqBeforeConnect >= first);
        Assert.Equal(2, join.Attempts);
    }

    [Fact]
    public void TheRetryGapAndTheMenuBarrierGoThroughTheInjectedSleeper()
    {
        var (fixture, ctx) = Rig();
        fixture.Transport.ConnectFailuresBeforeSuccess = 3;

        ctx.ConnectJoiner("joiner", "hostie", gapSeconds: 10);
        Assert.Contains(TimeSpan.FromSeconds(10), fixture.Sleeper.Delays);
    }

    [Fact]
    public void AnExplicitPortOverridesTheOneTheHostReports()
    {
        var (fixture, ctx) = Rig();
        ctx.ConnectJoiner("joiner", "hostie", port: 28123);
        Assert.Contains(fixture.Transport.Bodies, b => b.Contains("\"port\":28123", StringComparison.Ordinal));
    }

    [Fact]
    public void TheAddressReachesTheEndpointToo()
    {
        var (fixture, ctx) = Rig();
        ctx.ConnectJoiner("joiner", "hostie", address: "127.0.0.2");
        Assert.Contains(fixture.Transport.Bodies, b => b.Contains("\"address\":\"127.0.0.2\"", StringComparison.Ordinal));
    }

    [Fact]
    public void BothInstancesHaveToBelongToTheCheck()
    {
        var (_, ctx) = Rig();
        Assert.Throws<PlaytestUsageException>(() => ctx.ConnectJoiner("stranger", "hostie"));
        Assert.Throws<PlaytestUsageException>(() => ctx.ConnectJoiner("joiner", "stranger"));
    }
}
