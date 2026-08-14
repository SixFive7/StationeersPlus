using System.Globalization;

namespace TestRig.Core.Session;

/// <summary>
/// The one timestamp format the session files use, and the only parser for it.
/// </summary>
/// <remarks>
/// Written as <c>yyyy-MM-ddTHH:mm:ssZ</c>: second precision, literal Z, no fractional
/// part. Read with <c>AssumeUniversal | AdjustToUniversal</c>, which accepts far more
/// than it writes and treats an offset-less value as UTC. A stamp in the future yields
/// a negative age, which reads as "not expired" and "not past the ceiling"; that is
/// deliberate, because one machine means no clock skew and a hand-edited future stamp
/// is not worth a special case.
/// </remarks>
public static class RigTime
{
    public const string Format = "yyyy-MM-ddTHH:mm:ss'Z'";

    public static string Stamp(DateTimeOffset now) =>
        now.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture);

    /// <summary>Parses a stamp, or returns null when the value is absent or unreadable.</summary>
    public static DateTimeOffset? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return null;
        }
        return new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc));
    }

    /// <summary>Whole minutes between two instants, as the predicates measure them.</summary>
    public static double MinutesSince(DateTimeOffset now, DateTimeOffset then) =>
        (now - then).TotalMinutes;
}
