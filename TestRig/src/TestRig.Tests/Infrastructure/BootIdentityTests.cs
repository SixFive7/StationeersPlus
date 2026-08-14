using TestRig.Core.Infrastructure;
using Xunit;

namespace TestRig.Tests.Infrastructure;

/// <summary>
/// BootIdentity: stable across calls, plausible as an instant.
/// </summary>
public sealed class BootIdentityTests
{
    private readonly BootIdentity _boot = new();

    [Fact]
    public void GetBootId_IsStableAcrossCalls()
    {
        // The property that matters. An id that moves between two calls in one session
        // makes the rig read an ordinary session as a reboot.
        var first = _boot.GetBootId();

        for (var i = 0; i < 200; i++)
        {
            Assert.Equal(first, _boot.GetBootId());
        }
    }

    [Fact]
    public async Task GetBootId_IsStableAcrossASleep()
    {
        var first = _boot.GetBootId();
        await Task.Delay(TimeSpan.FromMilliseconds(750));

        Assert.Equal(first, _boot.GetBootId());
    }

    [Fact]
    public void GetBootId_AgreesBetweenInstances()
    {
        // Two instances stand in for two processes: the marker is written by one process
        // and read by the next, so the value cannot be cached per object.
        Assert.Equal(new BootIdentity().GetBootId(), new BootIdentity().GetBootId());
        Assert.Equal(BootIdentity.Instance.GetBootId(), new BootIdentity().GetBootId());
    }

    [Fact]
    public void GetBootId_HasTheDocumentedShape()
    {
        var id = _boot.GetBootId();

        Assert.StartsWith("boot-", id, StringComparison.Ordinal);
        Assert.Equal("boot-yyyyMMddTHHmmssZ".Length, id.Length);
        Assert.EndsWith("Z", id, StringComparison.Ordinal);
    }

    [Fact]
    public void GetBootInstantUtc_IsInThePastAndMatchesTheUptime()
    {
        var instant = _boot.GetBootInstantUtc();
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);

        Assert.True(instant < DateTimeOffset.UtcNow, "the machine booted before now");

        // Within one bucket of the independently derived value.
        var expected = DateTimeOffset.UtcNow - uptime;
        Assert.True(
            (expected - instant).Duration() < TimeSpan.FromSeconds(BootIdentity.BucketSeconds + 1),
            $"derived boot instant {instant:O} is not within a bucket of {expected:O}");
    }

    [Fact]
    public void GetBootInstantUtc_IsTruncatedToTheBucket()
    {
        var instant = _boot.GetBootInstantUtc();
        var bucket = TimeSpan.FromSeconds(BootIdentity.BucketSeconds).Ticks;

        Assert.Equal(0, instant.UtcTicks % bucket);
    }
}
