using System.Globalization;

namespace TestRig.Core.Session;

/// <summary>How a lock file relates to the caller asking about it.</summary>
/// <remarks>
/// Four states, not seven. The conceptual "held by a dead process" is deliberately not
/// one of them: <b>the session lock records no process identity at all</b>, and never
/// has. A session spans many launcher processes (an agent's launcher exits between
/// commands), so a dead owner is indistinguishable from an idle one, and the idle
/// ceiling is the entire substitute. There is no IsOwnerAlive to write here.
/// </remarks>
public enum LockState
{
    /// <summary>No usable lock file.</summary>
    None,

    /// <summary>The caller owns it. A lock past its ceiling is still Mine to its owner.</summary>
    Mine,

    /// <summary>Somebody else's, and still alive by the rules.</summary>
    LiveForeign,

    /// <summary>Somebody else's, and the rules already consider it dead. Reclaimable without a break.</summary>
    DeadForeign,
}

/// <summary>Why a dead foreign lock is reclaimable.</summary>
public enum ReclaimReason
{
    None,

    /// <summary>The heartbeat lapsed and the rig is idle.</summary>
    Ttl,

    /// <summary>The owner has not acted for longer than the ceiling. Reclaimable even on a busy rig.</summary>
    IdleCeiling,
}

/// <summary>The classification, plus the writes it implies.</summary>
/// <param name="Renew">
/// The caller must bump <c>refreshed_at</c> AND NOTHING ELSE. Only ever true on a
/// foreign lock over a busy rig.
/// </param>
public sealed record LockStateSnapshot(
    LockState State,
    FieldText? Lock,
    string? BusyDetail,
    bool Renew,
    ReclaimReason Reclaim);

/// <summary>Field names and the timer predicates over them.</summary>
public static class LockFields
{
    public const string Owner = "owner";
    public const string Purpose = "purpose";
    public const string AcquiredAt = "acquired_at";
    public const string RefreshedAt = "refreshed_at";
    public const string ActiveAt = "active_at";
    public const string TtlMinutes = "ttl_minutes";
    public const string IdleCeilingMinutes = "idle_ceiling_minutes";
    public const string Host = "host";

    public const int DefaultTtlMinutes = 10;
    public const int DefaultIdleCeilingMinutes = 60;

    /// <summary>
    /// Owner comparison.
    /// </summary>
    /// <remarks>
    /// Deliberately ordinal-IGNORE-case. PowerShell's <c>-eq</c> on strings is
    /// case-insensitive, so <c>-As ABC12345</c> matched a lock owned by <c>abc12345</c>.
    /// Owner ids are minted lowercase so no real collision exists, and a C# <c>==</c>
    /// would silently drop that forgiveness for a hand-typed id (spec 02-lock race R-13).
    /// Ordinal, not culture-aware: an id is hex, never text in a locale.
    /// </remarks>
    public static bool SameOwner(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
        && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the heartbeat has lapsed.
    /// </summary>
    /// <remarks>
    /// Fails closed on anything unreadable, and note the deliberate asymmetry: a MISSING
    /// <c>ttl_minutes</c> uses the 10-minute default and the lock may still be fresh,
    /// while a missing <c>refreshed_at</c> is expired outright. Strictly greater, so
    /// exactly at the limit is not expired.
    /// </remarks>
    public static bool IsTimerExpired(FieldText fields, DateTimeOffset now)
    {
        var ttl = DefaultTtlMinutes;
        if (fields.Contains(TtlMinutes))
        {
            if (!int.TryParse(fields.GetOrEmpty(TtlMinutes), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                || parsed < 0)
            {
                return true;
            }
            ttl = parsed;
        }

        if (!fields.Contains(RefreshedAt)) return true;
        var refreshed = RigTime.TryParse(fields.GetOrEmpty(RefreshedAt));
        if (refreshed is null) return true;

        return RigTime.MinutesSince(now, refreshed.Value) > ttl;
    }

    /// <summary>
    /// The ceiling's anchor: when the owner last acted.
    /// </summary>
    /// <remarks>
    /// The fallback order is fail-closed in the same sense the TTL is: never pick a field
    /// that could make the lock look FRESHER than it is. <c>acquired_at</c> is older than
    /// any owner action and is therefore strictly safer than <c>refreshed_at</c>, which
    /// the busy self-renew moves.
    /// </remarks>
    public static DateTimeOffset? GetActiveAt(FieldText fields)
    {
        foreach (var key in new[] { ActiveAt, AcquiredAt, RefreshedAt })
        {
            if (!fields.Contains(key)) continue;
            var parsed = RigTime.TryParse(fields.GetOrEmpty(key));
            if (parsed is not null) return parsed;
        }
        return null;
    }

    /// <summary>The configured ceiling, or null when it is present but unreadable.</summary>
    public static int? GetIdleCeiling(FieldText fields)
    {
        if (!fields.Contains(IdleCeilingMinutes)) return DefaultIdleCeilingMinutes;
        if (int.TryParse(fields.GetOrEmpty(IdleCeilingMinutes), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed >= 0)
        {
            return parsed;
        }
        return null;
    }

    /// <summary>The configured TTL, or the default when absent or unreadable.</summary>
    public static int GetTtl(FieldText fields)
    {
        if (!fields.Contains(TtlMinutes)) return DefaultTtlMinutes;
        return int.TryParse(fields.GetOrEmpty(TtlMinutes), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
               && parsed >= 0
            ? parsed
            : DefaultTtlMinutes;
    }

    /// <summary>Whether the owner has been silent past the absolute ceiling. Strictly greater.</summary>
    public static bool IsIdleCeilingExceeded(FieldText fields, DateTimeOffset now)
    {
        var ceiling = GetIdleCeiling(fields);
        if (ceiling is null) return true;
        var active = GetActiveAt(fields);
        if (active is null) return true;
        return RigTime.MinutesSince(now, active.Value) > ceiling.Value;
    }
}

/// <summary>Pure classification: no reads, no writes, no probing.</summary>
public static class LockClassifier
{
    /// <summary>
    /// Classifies a lock for a caller.
    /// </summary>
    /// <remarks>
    /// Evaluation order is not arbitrary. The ceiling test MUST precede the TTL test: a
    /// busy rig self-renews <c>refreshed_at</c> every time anyone looks at it, so a lock
    /// held by a hung agent with one forgotten client instance is permanently fresh by
    /// the TTL and would never reach the reclaim branch. Testing the ceiling first is
    /// what makes the hung-agent case terminate.
    ///
    /// The Mine branch has no timer term: a lock past its ceiling is still Mine to its
    /// owner. The ceiling makes a lock reclaimable, not revoked. First come.
    ///
    /// An empty caller id never matches, so a command with no id against any existing
    /// lock classifies as foreign.
    /// </remarks>
    public static LockStateSnapshot Resolve(FieldText? fields, string? callerId, BusySignal? busy, DateTimeOffset now)
    {
        if (fields is null)
        {
            return new LockStateSnapshot(LockState.None, null, null, false, ReclaimReason.None);
        }

        if (LockFields.SameOwner(callerId, fields.Get(LockFields.Owner)))
        {
            return new LockStateSnapshot(LockState.Mine, fields, null, false, ReclaimReason.None);
        }

        if (LockFields.IsIdleCeilingExceeded(fields, now))
        {
            // The probe is not needed to DECIDE here (a reclaim happens either way) but it
            // is needed to REPORT: taking a rig off a session that still has a world up is
            // the loudest thing this subsystem does, and the message has to name what is
            // running.
            var detail = busy is { Busy: true } ? busy.Detail : null;
            return new LockStateSnapshot(LockState.DeadForeign, fields, detail, false, ReclaimReason.IdleCeiling);
        }

        if (!LockFields.IsTimerExpired(fields, now))
        {
            // A fresh foreign lock never runs the probe, so the refusal text for it cannot
            // name what is running. Only the expired-but-busy and ceiling paths carry detail.
            return new LockStateSnapshot(LockState.LiveForeign, fields, null, false, ReclaimReason.None);
        }

        if (busy is { Busy: true })
        {
            // The self-renew moves refreshed_at ONLY. Bumping active_at here would be the
            // bug the ceiling exists to prevent: a forgotten instance would renew a hung
            // agent's reservation for ever.
            return new LockStateSnapshot(LockState.LiveForeign, fields, busy.Detail, true, ReclaimReason.None);
        }

        return new LockStateSnapshot(LockState.DeadForeign, fields, null, false, ReclaimReason.Ttl);
    }

    /// <summary>Whether the busy probe would change or decorate the answer.</summary>
    public static bool IsBusyProbeNeeded(FieldText? fields, string? callerId, DateTimeOffset now)
    {
        if (fields is null) return false;
        if (LockFields.SameOwner(callerId, fields.Get(LockFields.Owner))) return false;
        if (LockFields.IsIdleCeilingExceeded(fields, now)) return true;
        return LockFields.IsTimerExpired(fields, now);
    }

    /// <summary>
    /// The <c>stop --release</c> predicate.
    /// </summary>
    /// <remarks>
    /// This predicate has NO busy term. On its own it will happily release a foreign lock
    /// whose timer has lapsed while the rig is mid-test. It is safe only because the
    /// caller classifies FIRST, and the expired-and-busy branch reports LiveForeign, so
    /// the caller refuses before ever reaching here. Swap the two and a busy foreign
    /// session loses its lock to an unrelated stop.
    /// </remarks>
    public static bool IsReleasableOnStop(FieldText? fields, string? callerId, bool breakLock, DateTimeOffset now)
    {
        if (fields is null) return true;
        if (LockFields.SameOwner(callerId, fields.Get(LockFields.Owner))) return true;
        if (breakLock) return true;
        if (LockFields.IsIdleCeilingExceeded(fields, now)) return true;
        return LockFields.IsTimerExpired(fields, now);
    }
}
