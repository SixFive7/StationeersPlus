using System.Diagnostics;
using TestRig.Core.Infrastructure;
using Xunit;

namespace TestRig.Tests.Infrastructure;

/// <summary>
/// The two trivial seams, checked for the two things that are not trivial about them.
/// </summary>
public sealed class SystemClockAndSleeperTests
{
    [Fact]
    public void Clock_ReadsUtcAndAdvances()
    {
        var clock = new SystemClock();

        var first = clock.UtcNow;
        Assert.Equal(TimeSpan.Zero, first.Offset);
        Assert.True((DateTimeOffset.UtcNow - first).Duration() < TimeSpan.FromSeconds(5));

        Thread.Sleep(20);
        Assert.True(clock.UtcNow > first);
    }

    [Fact]
    public async Task Sleeper_ActuallyWaits()
    {
        var sleeper = new SystemSleeper();
        var stopwatch = Stopwatch.StartNew();

        await sleeper.DelayAsync(TimeSpan.FromMilliseconds(120));

        // The timer's resolution is coarse, so the bar is "it waited", not "it waited
        // exactly".
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(80), $"waited only {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Sleeper_TreatsAnElapsedDeadlineAsCompleteRatherThanAnError()
    {
        // Every caller computes its delay from a deadline, so a negative one is an
        // ordinary outcome. Task.Delay throws on it.
        var sleeper = new SystemSleeper();

        await sleeper.DelayAsync(TimeSpan.Zero);
        await sleeper.DelayAsync(TimeSpan.FromSeconds(-30));
    }

    [Fact]
    public async Task Sleeper_HonoursCancellation()
    {
        var sleeper = new SystemSleeper();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sleeper.DelayAsync(TimeSpan.FromSeconds(30), cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sleeper.DelayAsync(TimeSpan.Zero, cts.Token));
    }
}
