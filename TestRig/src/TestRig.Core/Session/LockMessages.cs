namespace TestRig.Core.Session;

/// <summary>
/// Every sentence the lock can say. Kept in one place so a refusal can be asserted
/// against by content rather than by scraping a console.
/// </summary>
/// <remarks>
/// The PowerShell suite asserted a single character of none of the status rendering:
/// three writers were covered only by "it did not throw" (spec 02-lock H.3.2, named as
/// the largest coverage gap in that suite). Rendering here returns lines rather than
/// printing them, so the port can assert them.
/// </remarks>
public static class LockMessages
{
    public const string Rules = "TestRig/CLAUDE.md";

    /// <summary>"&lt;N&gt; min ago", or 'unknown' when the heartbeat will not parse.</summary>
    public static string AgeText(FieldText fields, DateTimeOffset now)
    {
        var refreshed = RigTime.TryParse(fields.Get(LockFields.RefreshedAt));
        if (refreshed is null) return "unknown";
        return $"{(int)RigTime.MinutesSince(now, refreshed.Value)} min ago";
    }

    /// <summary>"&lt;N&gt; min", or 'unknown' when nothing in the fallback chain parses.</summary>
    public static string IdleText(FieldText fields, DateTimeOffset now)
    {
        var active = LockFields.GetActiveAt(fields);
        if (active is null) return "unknown";
        return $"{(int)RigTime.MinutesSince(now, active.Value)} min";
    }

    /// <summary>How long is left on the ceiling.</summary>
    public static string IdleRemainingText(FieldText fields, DateTimeOffset now)
    {
        var ceiling = LockFields.GetIdleCeiling(fields);
        var active = LockFields.GetActiveAt(fields);
        if (ceiling is null || active is null) return "unreadable, so the ceiling counts as already reached";

        var left = ceiling.Value - RigTime.MinutesSince(now, active.Value);
        if (left <= 0) return "reached";
        return $"{(int)Math.Ceiling(left)} min left";
    }

    /// <summary>The four-line body a foreign-lock refusal carries.</summary>
    public static string FormatForeignLock(LockStateSnapshot state, DateTimeOffset now)
    {
        var fields = state.Lock;
        if (fields is null) return string.Empty;

        var active = AgeText(fields, now);
        if (!string.IsNullOrEmpty(state.BusyDetail)) active = $"{active}; {state.BusyDetail}";

        return string.Join(
            "\n",
            $"    purpose : {fields.GetOrEmpty(LockFields.Purpose)}",
            $"    owner   : {fields.GetOrEmpty(LockFields.Owner)}",
            $"    active  : {active}",
            $"    idle    : {IdleText(fields, now)} since the owner last acted ({IdleRemainingText(fields, now)} on the idle ceiling)");
    }

    public static string GateNoLock(string action, string tool) =>
        $"[{action}] No rig session lock is held. Acquire one first:\n"
        + $"    {tool} lock --purpose \"<what you are testing>\"\n"
        + $"then pass --as <id> on every mutating command. One lock covers BOTH TestRig halves. See {Rules}.";

    public static string GateDeadLock(string action, string tool) =>
        $"[{action}] No live rig session lock is held (a previous lock expired). Re-acquire:\n"
        + $"    {tool} lock --purpose \"<what you are testing>\"\n"
        + $"See {Rules}.";

    public static string GateForeignLock(string action, LockStateSnapshot state, DateTimeOffset now) =>
        $"[{action}] The test rig is locked by another session.\n"
        + FormatForeignLock(state, now) + "\n"
        + "Do NOT proceed. Report this purpose to the user and let the user decide. Only the user may "
        + $"authorize --break-lock. See {Rules}.";

    public static string AcquireBlocked(LockStateSnapshot state, int waitedSeconds, DateTimeOffset now)
    {
        var waited = waitedSeconds > 0 ? $" after waiting {waitedSeconds}s" : string.Empty;
        return $"Cannot acquire{waited}: the test rig is locked by another session.\n"
               + FormatForeignLock(state, now) + "\n"
               + $"Report this purpose to the user. Only the user may authorize --break-lock. See {Rules}.";
    }

    public const string RefreshNoLock =
        "No rig session lock to refresh. Acquire one: testrig lock --purpose \"<reason>\".";

    public static string RefreshNotYours(FieldText fields, string? callerId) =>
        $"Refresh refused: the rig lock is held by owner '{fields.GetOrEmpty(LockFields.Owner)}' "
        + $"(purpose: {fields.GetOrEmpty(LockFields.Purpose)}), not '{callerId}'. Your reservation has lapsed. "
        + $"Report to the user; do not touch the rig. See {Rules}.";

    public static string UnlockNotYours(FieldText fields, string? callerId) =>
        $"Unlock refused: the rig lock is held by owner '{fields.GetOrEmpty(LockFields.Owner)}' "
        + $"(purpose: {fields.GetOrEmpty(LockFields.Purpose)}), not '{callerId}'. Report to the user. "
        + $"Only the user may authorize --break-lock. See {Rules}.";

    public static string UnlockHostLive(IReadOnlyList<string> hostNames, string busyDetail, string? callerId) =>
        $"Unlock refused: a listen-host instance is still live ({string.Join(", ", hostNames)}). Releasing now "
        + "leaves a hosted world running with no session owning it, and the next agent stopping the rig takes it "
        + $"down mid-test. Stop the instances first (testrig stop --target clients --as {callerId ?? "<id>"}), or pass "
        + $"--force if you really mean to release while it runs. Rig state: {busyDetail}";

    public const string UnlockGone =
        "[Unlock] The rig session lock was already gone by the time the restore finished.";

    public static string UnlockStolen(string newOwner, string newPurpose, string oldOwner) =>
        $"[Unlock] NOT released: the lock now belongs to owner '{newOwner}' ('{newPurpose}'), not to the one this "
        + $"command validated ({oldOwner}). Somebody took the rig while the restore was running; leaving their lock alone.";

    public static string StopNotYours(string owner) =>
        $"[Stop] --release ignored: the lock is held by '{owner}', not you. Use: testrig unlock --as <id>, or get "
        + "the user's authorization for --break-lock.";

    public static string StopForeignLive(LockStateSnapshot state, DateTimeOffset now) =>
        "[Stop] Refusing to stop a rig held by another live session.\n"
        + FormatForeignLock(state, now) + "\n"
        + $"Report this to the user. Only the user may authorize --break-lock. See {Rules}.";

    public static string MutexTimeout(int seconds, string mutexName, string context) =>
        $"Timed out after {seconds}s waiting for the rig-lock critical section ({mutexName}) while trying to "
        + $"{context}. Every critical section here is a few small file operations, so this means another process is "
        + "hung while holding it, not merely busy. The lock file was NOT modified. Look for a stuck launcher process.";

    public static string MarkerWriteFailed(string path, string message) =>
        $"[Lock] Could not write the crash marker at {path}: {message}. The action continues, but if this session "
        + "dies the next acquisition will not know the rig was left dirty.";

    public static string BrokeLiveLock(string purpose, string owner) =>
        $"[Lock] --break-lock: broke a live lock held by '{purpose}' (owner {owner}).";

    public static string WatchdogReclaimed(string owner, string purpose, string idleText, int ceiling) =>
        $"[Lock] IDLE WATCHDOG: reclaimed the rig from owner {owner} ('{purpose}'), whose last action was "
        + $"{idleText} ago, past its {ceiling} min idle ceiling. That session did not release the rig; it is not "
        + "being punished for being slow, only for being silent.";

    public static string WatchdogReclaimedBusy(string detail) =>
        $"[Lock] IDLE WATCHDOG: the rig was still BUSY when it was reclaimed: {detail}. Whatever was running belongs "
        + "to the reclaimed session and is about to be stopped and reset. If that was a live test somebody cared "
        + "about, this is where it ended.";

    public static string DirtyKeepState(string describe) =>
        $"[Lock] The rig was left DIRTY by the previous session ({describe}), and --keep-state says do not restore "
        + "it. You are starting on that session's leftovers ON PURPOSE. The marker stays set, so the next acquisition "
        + "that does not pass --keep-state will restore.";

    public static string DirtyRestoring(string describe) =>
        $"[Lock] The rig was left DIRTY: {describe}. The previous session did not restore it, so the restore runs "
        + "now, before you get a rig to test on.";

    public static string ResetFailed(string owner, string tool) =>
        $"[Lock] The rig state reset FAILED. You DO hold the lock (owner {owner}), but the rig may be half reset and "
        + $"is not safe to test on. Fix the cause, then release and re-take the lock: {tool} unlock --as {owner} "
        + $"followed by {tool} lock --purpose \"...\". Re-asserting the lock you already hold does NOT reset.";

    public const string NoRestoreImplementation =
        "[Lock] No restore implementation is wired in, so the dirty rig was NOT restored and the marker stays set. "
        + "This is a wiring fault, not a rig fault.";

    public const string KeepStateOnRelease =
        "[Unlock] --keep-state: the rig is being released WITHOUT restoring it. Everything this session changed "
        + "stays on the rig for the next one, on purpose. The dirty marker stays set, so the next lock restores "
        + "unless that agent also passes --keep-state.";

    public static string RestoreFailedOnRelease(string message) =>
        $"[Unlock] The restore FAILED on the way out: {message}";

    public const string RestoreFailedOnReleaseDetail =
        "[Unlock] The lock is still being released, and the rig stays marked dirty, so the next acquisition restores "
        + "it before that agent gets to test. Nothing is lost except the tidy exit.";

    public static string OrphanWarning(int count) =>
        $"{count} UNTRACKED rig game process(es) are running. No pid file claims them, so no launcher action can "
        + "stop them and they are NOT counted as busy. They still hold their control-plane and game ports, which is "
        + "enough to make the next test bind different ports and assert against the wrong process.";

    public static string MarkedDirty(string action) =>
        $"[Lock] Rig marked dirty (first mutating action of this session: {action}). It is restored at unlock, or by "
        + "the next acquisition if this session does not get that far.";

    public const string ProcessLocalMutex =
        "[Lock] The cross-process critical section fell back off the Global namespace and is process-local. Two "
        + "launchers that resolve differently are not serialised at all. Measured cost without a working critical "
        + "section: four simultaneous winners per round across 20 rounds.";

    public const string AbandonedMutex =
        "[Lock] The previous holder of the critical section died without releasing it. The lock file is written "
        + "atomically, so nothing can be half-written, but a launcher process was killed mid-operation.";
}
