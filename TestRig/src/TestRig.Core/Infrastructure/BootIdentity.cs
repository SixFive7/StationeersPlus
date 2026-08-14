using System.Globalization;
using TestRig.Core.Abstractions;

namespace TestRig.Core.Infrastructure;

/// <summary>
/// The machine's boot identity, derived from the OS uptime.
/// </summary>
/// <remarks>
/// What it is for: session.dirty records the boot identity alongside the pid of the
/// session that wrote it. On the next acquisition, a matching boot id means the pid is
/// still meaningful and can be checked; a different one means the machine rebooted under
/// the previous session, so its pid means nothing at all and every world it recorded has
/// to be treated as protected.
///
/// Why not WMI or CIM: Win32_OperatingSystem.LastBootUpTime is the obvious source and it
/// is the wrong one here. It costs hundreds of milliseconds on a cold WMI service, and
/// System.Management is not AOT friendly, so it would drag a dependency into a binary
/// whose whole reason for existing is that it starts fast enough to shell once per
/// playtest check.
///
/// So the boot instant is derived: DateTimeOffset.UtcNow minus Environment.TickCount64.
/// GetTickCount64, which is what TickCount64 reads, counts biased interrupt time and
/// therefore includes time the machine spent asleep, so a laptop that suspends overnight
/// does not appear to have rebooted.
/// </remarks>
public sealed class BootIdentity : IBootIdentity
{
    /// <summary>A shared instance. The type is stateless, so one is enough.</summary>
    public static readonly BootIdentity Instance = new();

    /// <summary>
    /// The bucket the derived boot instant is truncated to, in seconds.
    /// </summary>
    /// <remarks>
    /// The derivation is not exact, so it has to be quantised or two calls minutes apart
    /// would disagree and the rig would read an ordinary session as a reboot.
    ///
    /// Measured on this machine, 2026-08-14: 40 samples in a tight loop spread the
    /// derived instant over 10.4 ms, and two samples eight seconds apart differed by
    /// 0.12 ms. Both numbers come from the ~15.6 ms resolution of the two clocks being
    /// subtracted, not from drift between them.
    ///
    /// 10 seconds is chosen over 1 second because the cost of a wider bucket is nothing
    /// (a reboot moves the derived instant by minutes at least, never by seconds) while
    /// the cost of a narrow one is a boundary straddle: with ~10 ms of jitter, a 1 second
    /// bucket flips roughly one time in a hundred and a 10 second bucket roughly one time
    /// in a thousand.
    ///
    /// The failure direction is deliberate. A spurious change reads as "the machine
    /// rebooted", which makes the reset planner keep every world and restore
    /// conservatively. The opposite mistake, a boot id that failed to change across a
    /// real reboot, is the one that would compare a live pid against a dead session's
    /// record, and no bucket size can produce it.
    ///
    /// What this cannot survive: a step correction to the system clock, which moves
    /// UtcNow without moving the tick count and therefore moves the derived instant by
    /// the size of the correction. Windows slews small corrections rather than stepping
    /// them, so this is rare, and it degrades in the conservative direction.
    /// </remarks>
    public const int BucketSeconds = 10;

    /// <summary>
    /// A stable identifier for the current boot, of the form boot-20260814T031500Z.
    /// </summary>
    public string GetBootId()
    {
        var boot = GetBootInstantUtc();
        return "boot-" + boot.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The derived boot instant, truncated to <see cref="BucketSeconds"/>.
    /// </summary>
    /// <remarks>
    /// Exposed so a diagnostic can print the instant rather than only the opaque id, and
    /// so the suite can assert on the value rather than on the formatting.
    /// </remarks>
    public DateTimeOffset GetBootInstantUtc()
    {
        // Read the uptime first. Whatever gap there is between these two reads lands in
        // the jitter the bucket absorbs, and reading uptime first biases it the same way
        // every time rather than at random.
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var now = DateTimeOffset.UtcNow;

        var raw = now - uptime;

        var bucket = TimeSpan.FromSeconds(BucketSeconds).Ticks;
        var truncated = raw.UtcTicks - (raw.UtcTicks % bucket);
        return new DateTimeOffset(truncated, TimeSpan.Zero);
    }
}
