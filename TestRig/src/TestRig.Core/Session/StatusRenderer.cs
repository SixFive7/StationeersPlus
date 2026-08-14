using TestRig.Core.Abstractions;

namespace TestRig.Core.Session;

/// <summary>One rendered status line.</summary>
public readonly record struct StatusLine(OutputLevel Level, string Text);

/// <summary>
/// Turns a <see cref="LockStatus"/> into lines.
/// </summary>
/// <remarks>
/// Returned rather than printed so the port can assert them. The PowerShell suite
/// asserted not one character of the three status writers: they were covered only by
/// "it did not throw", which was named the largest coverage gap in that suite. A renderer
/// that returns data is testable; one that writes to a console is not.
/// </remarks>
public static class StatusRenderer
{
    public static IReadOnlyList<StatusLine> Render(LockStatus status, string? callerId, DateTimeOffset now)
    {
        var lines = new List<StatusLine>();

        if (status.Lock is null)
        {
            lines.Add(new StatusLine(OutputLevel.Info, "rig lock:     none"));
        }
        else
        {
            var fields = status.Lock;
            var owner = fields.GetOrEmpty(LockFields.Owner);
            var own = LockFields.SameOwner(callerId, owner)
                ? "YOURS"
                : !string.IsNullOrEmpty(callerId)
                    ? $"held by another session ({owner})"
                    : $"owner {owner}";

            lines.Add(new StatusLine(OutputLevel.Info, $"rig lock:     {own}"));
            lines.Add(new StatusLine(OutputLevel.Info, $"  purpose:    {fields.GetOrEmpty(LockFields.Purpose)}"));
            lines.Add(new StatusLine(OutputLevel.Info,
                $"  timer:      {(status.TimerExpired ? "expired" : "fresh")}; ttl {LockFields.GetTtl(fields)} min; "
                + $"refreshed {LockMessages.AgeText(fields, now)}"));
            lines.Add(new StatusLine(OutputLevel.Info,
                $"  idle:       owner last acted {LockMessages.IdleText(fields, now)} ago; ceiling "
                + $"{LockFields.GetIdleCeiling(fields)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unreadable"} min "
                + $"({LockMessages.IdleRemainingText(fields, now)})"));
        }

        if (status.Busy.Busy)
        {
            var note = status.CeilingExceeded
                ? "  (lock is RECLAIMABLE anyway: past the idle ceiling)"
                : status.TimerExpired
                    ? "  (lock still LIVE: rig is busy)"
                    : string.Empty;
            lines.Add(new StatusLine(OutputLevel.Info, $"  rig busy:   {status.Busy.Detail}{note}"));

            if (status.Busy.HostLive)
            {
                lines.Add(new StatusLine(OutputLevel.Info,
                    $"  hosting:    {string.Join(", ", status.Busy.HostNames)}  (unlock refuses while a host is live; --force overrides)"));
            }
        }
        else if (status.Lock is not null && status.CeilingExceeded)
        {
            lines.Add(new StatusLine(OutputLevel.Info, "  rig busy:   no; past the idle ceiling, so the lock is reclaimable"));
        }
        else if (status.Lock is not null && status.TimerExpired)
        {
            lines.Add(new StatusLine(OutputLevel.Info, "  rig busy:   no; timer expired, so the lock is reclaimable"));
        }

        lines.AddRange(RenderDirty(status));
        lines.AddRange(RenderOrphans(status.Busy.Orphans));
        return lines;
    }

    private static IEnumerable<StatusLine> RenderDirty(LockStatus status)
    {
        if (!status.Dirty.Dirty)
        {
            yield return new StatusLine(OutputLevel.Info, "  rig state:  clean (restored; no session has mutated it since)");
            yield break;
        }

        yield return new StatusLine(OutputLevel.Info, $"  rig state:  DIRTY - {DirtyMarker.Describe(status.Dirty)}");

        if (status.Dirty.Crashed)
        {
            yield return new StatusLine(OutputLevel.Info,
                "  rig state:  nothing is left of that session, so the next lock restores the rig before granting it.");
        }

        yield return status.ServerWorlds.Recorded
            ? new StatusLine(OutputLevel.Info,
                $"  worlds:     {status.ServerWorlds.Count} dedicated-server world(s) were here when that session "
                + "started and are kept; any other world is that session's and the restore deletes it.")
            : new StatusLine(OutputLevel.Info,
                $"  worlds:     no dedicated-server world will be deleted ({status.ServerWorlds.Reason}).");

        yield return status.ClientWorlds.Recorded
            ? new StatusLine(OutputLevel.Info,
                $"  instance worlds: {status.ClientWorlds.Count} client-instance world(s) were here when that session "
                + "started and are kept; any other is that session's and the restore deletes it.")
            : new StatusLine(OutputLevel.Info,
                $"  instance worlds: no client-instance world will be deleted ({status.ClientWorlds.Reason}).");
    }

    private static IEnumerable<StatusLine> RenderOrphans(IReadOnlyList<OrphanProcess> orphans)
    {
        if (orphans.Count == 0) yield break;

        yield return new StatusLine(OutputLevel.Warning, LockMessages.OrphanWarning(orphans.Count));
        foreach (var orphan in orphans)
        {
            var where = orphan.Scope == OrphanScope.Unknown ? "<image path unreadable>" : orphan.ImagePath ?? "";
            yield return new StatusLine(OutputLevel.Info, $"  orphan:     {orphan.Name} pid {orphan.ProcessId}  {where}");
        }
        yield return new StatusLine(OutputLevel.Info, "  orphan:     stop them by pid; no launcher action can reach them.");
    }
}
