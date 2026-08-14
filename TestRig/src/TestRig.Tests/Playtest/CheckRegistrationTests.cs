using TestRig.Playtest.Model;
using Xunit;

namespace TestRig.Tests.Playtest;

/// <summary>
///     What a check may declare, and what it may not.
/// </summary>
public sealed class CheckRegistrationTests
{
    [Fact]
    public void ACheckNeedsAName()
    {
        Assert.Throws<PlaytestUsageException>(() => new CheckSpec(string.Empty, "s", [new InstanceSpec("hostie")]));
        Assert.Throws<PlaytestUsageException>(() => new CheckSpec("   ", "s", [new InstanceSpec("hostie")]));
    }

    [Fact]
    public void ACheckNeedsAtLeastOneInstance()
    {
        var thrown = Assert.Throws<PlaytestUsageException>(() => new CheckSpec("a check", "s", []));
        Assert.Contains("does not create instances", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryInstanceNeedsAName() =>
        Assert.Throws<PlaytestUsageException>(() => new CheckSpec("a check", "s", [new InstanceSpec(string.Empty)]));

    [Fact]
    public void AnInstanceCannotBeDeclaredTwice()
    {
        // Bring-up and teardown both walk this list by name, so a duplicate starts and stops
        // the same process twice.
        var thrown = Assert.Throws<PlaytestUsageException>(() =>
            new CheckSpec("a check", "s", [new InstanceSpec("hostie"), new InstanceSpec("HOSTIE")]));

        Assert.Contains("declared twice", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRoleDefaultsToClient()
    {
        var spec = new CheckSpec("a check", "s", [new InstanceSpec("joiner")]);
        Assert.Equal(InstanceRole.Client, spec.Instances[0].Role);
    }

    [Fact]
    public void AHostCannotDeclareBothAWorldAndASave()
    {
        // Defect P-01: documented as mutually exclusive and enforced by nothing, so both were
        // sent when both were present and the host was asked for two different things at once.
        var thrown = Assert.Throws<PlaytestUsageException>(() =>
            new CheckSpec("a check", "s", [new InstanceSpec("hostie", InstanceRole.Host, World: "Lunar", Save: "existing")]));

        Assert.Contains("Exactly one", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EitherOneOnItsOwnIsFine()
    {
        _ = new CheckSpec("a", "s", [new InstanceSpec("hostie", InstanceRole.Host, World: "Lunar")]);
        _ = new CheckSpec("b", "s", [new InstanceSpec("hostie", InstanceRole.Host, Save: "existing")]);
        _ = new CheckSpec("c", "s", [new InstanceSpec("hostie", InstanceRole.Host)]);
    }

    [Fact]
    public void AClientCannotDeclareAWorldOrASaveBecauseBringUpWouldIgnoreIt()
    {
        Assert.Throws<PlaytestUsageException>(() => new CheckSpec("a", "s", [new InstanceSpec("joiner", World: "Lunar")]));
        Assert.Throws<PlaytestUsageException>(() => new CheckSpec("b", "s", [new InstanceSpec("joiner", Save: "existing")]));
    }

    [Fact]
    public void AHostCannotDeclareConnectToBecauseAHostIsJoinedRatherThanJoining()
    {
        Assert.Throws<PlaytestUsageException>(() =>
            new CheckSpec("a", "s", [new InstanceSpec("hostie", InstanceRole.Host, ConnectTo: "other")]));
    }

    [Fact]
    public void AClientCannotDeclareAGamePortBecauseThatIsAHostsListenPort()
    {
        Assert.Throws<PlaytestUsageException>(() => new CheckSpec("a", "s", [new InstanceSpec("joiner", GamePort: 27800)]));
    }

    [Fact]
    public void EveryInstanceDeclaredAsAClientWithNoHostIsAUsedIdiom()
    {
        // It leaves bring-up at the menu so the body can drive the host endpoint itself, which
        // is the only way to reach the window between "reached the menu" and "hosts".
        var spec = new CheckSpec("a check", "s", [new InstanceSpec("joiner"), new InstanceSpec("hostie")]);
        Assert.Empty(spec.HostNames);
        Assert.Equal(["joiner", "hostie"], spec.InstanceNames);
    }

    [Fact]
    public void InstanceOrderIsPreservedBecauseTeardownWalksIt()
    {
        var spec = new CheckSpec("a check", "s",
        [
            new InstanceSpec("joiner"),
            new InstanceSpec("hostie", InstanceRole.Host, World: "Lunar"),
        ]);

        Assert.Equal(["joiner", "hostie"], spec.InstanceNames);
        Assert.Equal(["hostie"], spec.HostNames);
    }

    [Fact]
    public void ThePurposeDefaultsToTheChecksOwnName()
    {
        Assert.Equal("Playtest: a check", new CheckSpec("a check", "s", [new InstanceSpec("hostie")]).Purpose);
        Assert.Equal("something else", new CheckSpec("a check", "s", [new InstanceSpec("hostie")], purpose: "something else").Purpose);
    }

    [Fact]
    public void TheAddressDefaultsToLoopback() =>
        Assert.Equal("127.0.0.1", new InstanceSpec("joiner").Address);

    [Fact]
    public void TheSourceFileIsSuppliedByTheCompilerAndNotByTheCheck()
    {
        // A check cannot lie about a value it does not provide, and it cannot drift, because
        // moving the file changes the answer.
        var spec = new CheckSpec("a check", "s", [new InstanceSpec("hostie")]);
        Assert.EndsWith("CheckRegistrationTests.cs", spec.SourceFile, StringComparison.OrdinalIgnoreCase);
    }
}
