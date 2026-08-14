using TestRig.Core.Abstractions;

namespace TestRig.Core.Infrastructure;

/// <summary>
/// The machine's wall clock.
/// </summary>
/// <remarks>
/// This is the only place in the rig that is allowed to read the clock. Everything
/// else takes <see cref="IClock"/>, which is what lets the suite exercise the lock's
/// 10 minute TTL and 60 minute idle ceiling without spending either. The PowerShell
/// suites could not: they read the clock directly, so the timer behaviour was tested
/// by rewriting timestamps into the lock file and hoping the reader agreed.
/// </remarks>
public sealed class SystemClock : IClock
{
    /// <summary>A shared instance. The type is stateless, so one is enough.</summary>
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
