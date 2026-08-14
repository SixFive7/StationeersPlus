using TestRig.Core.Abstractions;

namespace TestRig.Core.Infrastructure;

/// <summary>
/// Real delays.
/// </summary>
/// <remarks>
/// Separate from <see cref="SystemClock"/> because the two are independently
/// substitutable: a readiness barrier under test wants time to advance without the
/// 2 second poll interval actually elapsing, and a boot barrier that legitimately
/// waits 300 seconds would otherwise make the suite unrunnable.
/// </remarks>
public sealed class SystemSleeper : ISleeper
{
    /// <summary>A shared instance. The type is stateless, so one is enough.</summary>
    public static readonly SystemSleeper Instance = new();

    /// <remarks>
    /// A non-positive duration is a completed task rather than an argument error.
    /// Every caller computes its delay from a deadline, so "the deadline already
    /// passed" is an ordinary outcome and must not throw.
    /// </remarks>
    public Task DelayAsync(TimeSpan duration, CancellationToken ct = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            return ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask;
        }

        return Task.Delay(duration, ct);
    }
}
