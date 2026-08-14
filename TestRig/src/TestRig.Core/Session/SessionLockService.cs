using TestRig.Core.Abstractions;

namespace TestRig.Core.Session;

/// <summary>The restore half of a session boundary. Optional: the lock stays usable without one.</summary>
public interface IRigRestore
{
    /// <summary>Runs a state restore. Throws when the restore leaves the rig half reset.</summary>
    ResetRun Restore(bool keepState, string reason);
}

/// <summary>How an acquisition turned out.</summary>
public enum AcquireKind
{
    /// <summary>A brand-new reservation on a free rig.</summary>
    Acquired,

    /// <summary>The caller already held it. Same session, so no reset.</summary>
    Reasserted,

    /// <summary>Taken from a session whose heartbeat lapsed on an idle rig.</summary>
    ReclaimedExpired,

    /// <summary>Taken from a session that has been silent past its ceiling, busy rig or not.</summary>
    ReclaimedIdleCeiling,

    /// <summary>Taken from a genuinely live session, on the user's authorization.</summary>
    Broke,
}

/// <summary>What to acquire, and how hard to try.</summary>
public sealed record AcquireOptions
{
    public required string Purpose { get; init; }

    /// <summary>The caller's existing owner id, when re-asserting.</summary>
    public string? CallerId { get; init; }

    /// <summary>
    /// Null means "not typed". On a re-assert a null carries the STORED value forward.
    /// </summary>
    /// <remarks>
    /// The PowerShell launcher forwarded its own defaults whether or not the user typed
    /// them, and the re-assert branch wrote them unconditionally, so a session that took
    /// the rig with a 240-minute ceiling (the documented way to wait for a human) and
    /// later re-asserted to change its purpose was silently dropped back to 60, with no
    /// warning (spec 02-lock defect D2). Nullable here, so "was this typed" is modelled
    /// explicitly rather than inferred by comparing against a default.
    /// </remarks>
    public int? TtlMinutes { get; init; }

    /// <inheritdoc cref="TtlMinutes"/>
    public int? IdleCeilingMinutes { get; init; }

    /// <summary>Take a LIVE foreign lock. Human-gated by policy, not by code.</summary>
    public bool BreakLock { get; init; }

    /// <summary>Skip the acquisition-side restore, loudly.</summary>
    public bool KeepState { get; init; }

    /// <summary>Queue for up to this long instead of failing fast.</summary>
    public int WaitSeconds { get; init; }

    /// <summary>Poll interval while queueing. Clamped to at least one second.</summary>
    public int PollSeconds { get; init; } = 5;

    /// <summary>Launcher name, for message text.</summary>
    public string Tool { get; init; } = "testrig";

    /// <summary>Tears down whatever the reclaimed session left running. Only on a reclaim, never on a break.</summary>
    public Action? OnReclaim { get; init; }
}

/// <summary>
/// The result of acquiring, carrying the owner id as a typed field.
/// </summary>
/// <remarks>
/// This is the whole point of the type. In PowerShell <c>New-RigLock</c> returned a bare
/// string, so the launcher's <c>$outcome.Owner</c> was always null and the machine-readable
/// <c>TESTRIG-OWNER &lt;id&gt;</c> line has never once printed. The playtest harness
/// requires that line by regex and throws inconclusive/rig-unavailable without it, then
/// unlocks with the id it never got, leaving the rig locked by a session that cannot
/// release it. The pinning assertion greps the launcher's SOURCE TEXT, so the suite is
/// green while the feature has never worked (spec 02-lock defect D1).
///
/// No caller parses prose here. The id is a field, and the CLI emits it through
/// <see cref="IOutput.Value"/>.
/// </remarks>
public sealed record LockAcquireResult(
    string Owner,
    AcquireKind Kind,
    string Purpose,
    int TtlMinutes,
    int IdleCeilingMinutes,
    bool StateWasReset,
    string? BusyDetail);

/// <summary>What a refresh did.</summary>
public sealed record RefreshResult(string Owner, int TtlMinutes, int IdleCeilingMinutes)
{
    public string Message =>
        $"[RefreshLock] Refreshed (owner {Owner}, ttl {TtlMinutes} min heartbeat, {IdleCeilingMinutes} min idle ceiling).";
}

/// <summary>How a release ended.</summary>
public enum ReleaseStatus
{
    /// <summary>There was nothing to release.</summary>
    NoLock,

    /// <summary>The lock file was deleted.</summary>
    Released,

    /// <summary>It vanished during the restore.</summary>
    AlreadyGone,

    /// <summary>Somebody else's lock was in its place by the time the restore finished. Left alone.</summary>
    Stolen,

    /// <summary>The caller had no authority to release it.</summary>
    NotYours,
}

/// <param name="RestoreFailure">The restore's exception message, when it failed. The lock is released anyway.</param>
public sealed record ReleaseResult(
    ReleaseStatus Status,
    string? Owner,
    string Message,
    bool RestoreSkipped,
    string? RestoreFailure,
    BusySignal Busy);

/// <summary>Everything <c>status</c> reports, as data.</summary>
public sealed record LockStatus(
    LockState State,
    FieldText? Lock,
    bool TimerExpired,
    bool CeilingExceeded,
    BusySignal Busy,
    DirtyState Dirty,
    SessionWorldSnapshot ServerWorlds,
    SessionWorldSnapshot ClientWorlds);

/// <summary>
/// The session lock: a coarse, human-scale, advisory reservation over the whole rig.
/// </summary>
/// <remarks>
/// It is not a mutual-exclusion primitive over a resource the OS can arbitrate. It is a
/// cooperative reservation whose enforcement lives entirely in <see cref="AssertHeld"/>
/// being called at the top of every mutating operation; nothing stops a process that
/// never calls the gate.
///
/// Two distinct concurrency primitives, doing different jobs, deliberately not merged:
/// the SESSION lock (a file, coarse, spanning many start/stop cycles, expiring on two
/// timers), and the CRITICAL SECTION (a named system mutex guarding every
/// read-modify-write of that file, held for milliseconds).
/// </remarks>
public sealed class SessionLockService
{
    private static readonly string[] Header =
    [
        "# Stationeers TestRig - ACTIVE session lock (auto-managed; do not hand-edit).",
        "# Covers BOTH halves: TestRig/DedicatedServer/ and TestRig/ClientRig/.",
        "# Mechanism and rules: TestRig/CLAUDE.md and TestRig/MANUAL.md.",
    ];

    /// <summary>How long to wait for the critical section before concluding a process is hung.</summary>
    /// <remarks>
    /// Bounded, never infinite. Every critical section here is a couple of small file
    /// operations plus, on the contended paths, one busy probe, so anything past this
    /// means another process is hung rather than busy, and an agent is better served by a
    /// clear error than by a wait that never ends.
    /// </remarks>
    public static readonly TimeSpan MutexTimeout = TimeSpan.FromSeconds(15);

    private readonly IFileSystem _fs;
    private readonly IClock _clock;
    private readonly ISleeper _sleeper;
    private readonly ICrossProcessLock _mutex;
    private readonly IOutput _output;
    private readonly RigPaths _paths;
    private readonly BusyProbe _busy;
    private readonly DirtyMarker _marker;
    private readonly LauncherIdentity _launcher;
    private readonly Func<string> _mintOwnerId;
    private readonly IRigRestore? _restore;

    public SessionLockService(
        IFileSystem fs,
        IClock clock,
        ISleeper sleeper,
        ICrossProcessLock mutex,
        IOutput output,
        RigPaths paths,
        BusyProbe busy,
        DirtyMarker marker,
        LauncherIdentity launcher,
        IRigRestore? restore = null,
        Func<string>? mintOwnerId = null)
    {
        _fs = fs;
        _clock = clock;
        _sleeper = sleeper;
        _mutex = mutex;
        _output = output;
        _paths = paths;
        _busy = busy;
        _marker = marker;
        _launcher = launcher;
        _restore = restore;
        _mintOwnerId = mintOwnerId ?? DefaultOwnerId;
    }

    /// <summary>8 lowercase hex characters, as the file format and every message assume.</summary>
    public static string DefaultOwnerId() => Guid.NewGuid().ToString("N")[..8];

    // ---- reading -----------------------------------------------------------

    /// <summary>The lock file's fields, or null when there is no usable lock.</summary>
    /// <remarks>
    /// Null means "no lock" for exactly three reasons: the file does not exist, it
    /// vanished mid-read, or the parsed field set has no <c>owner</c> key (which covers an
    /// empty file, a comment-only file, pure garbage, and anything hand-broken). It does
    /// NOT cover an unreadable file: <see cref="RigFiles.ReadTextOrNull"/> throws for
    /// that, because a read failure that reads as "the rig is free" is exactly the answer
    /// that gets a live session stomped.
    /// </remarks>
    public FieldText? ReadLock()
    {
        var text = RigFiles.ReadTextOrNull(_fs, _paths.LockFile, "rig lock file");
        if (text is null) return null;
        var fields = FieldText.Parse(text);
        return fields.Contains(LockFields.Owner) ? fields : null;
    }

    /// <summary>
    /// Classifies without writing anything. A genuine query.
    /// </summary>
    /// <remarks>
    /// PowerShell had one entry point that classified AND performed the writes the
    /// classification implied, so <c>stop</c>'s "just tell me who holds it" call wrote to
    /// disk, and <c>status</c> was the only genuinely read-only operation in the whole
    /// library. Here reading and renewing are two methods, and this one is the read.
    /// </remarks>
    public LockStateSnapshot ReadState(string? callerId)
    {
        var now = _clock.UtcNow;
        var fields = ReadLock();
        BusySignal? busy = null;
        if (LockClassifier.IsBusyProbeNeeded(fields, callerId, now)) busy = _busy.Probe();
        return LockClassifier.Resolve(fields, callerId, busy, now);
    }

    /// <summary>
    /// Classifies and performs the writes the classification implies, atomically.
    /// </summary>
    /// <remarks>
    /// Two branches, mutually exclusive by construction: a Mine state with
    /// <paramref name="refreshIfMine"/> moves BOTH clocks (the owner is acting), and a
    /// Renew state moves <c>refreshed_at</c> ONLY (the rig renewed itself because it is
    /// busy). Renew is only ever true on a foreign lock, so the two cannot both apply.
    ///
    /// The busy probe runs INSIDE the critical section. PowerShell computed it outside and
    /// consumed it inside, so the snapshot could be seconds old by the time it decided
    /// anything: an instance that started during the probe was not seen, and a foreign
    /// lock that should have been kept alive could be classified DeadForeign and reclaimed
    /// (spec 02-lock race R-2). It already broke its own "nothing slow inside the mutex"
    /// rule on the rare disagreement path, for exactly this reason. The probe is a
    /// directory walk plus a process-table query, both bounded; correctness wins.
    /// </remarks>
    public LockStateSnapshot ReadStateAndRenew(string? callerId, bool refreshIfMine)
    {
        return WithMutex("read the rig lock state", () =>
        {
            var now = _clock.UtcNow;
            var fields = ReadLock();
            BusySignal? busy = null;
            if (LockClassifier.IsBusyProbeNeeded(fields, callerId, now)) busy = _busy.Probe();

            var state = LockClassifier.Resolve(fields, callerId, busy, now);

            if (state.State == LockState.Mine && refreshIfMine && state.Lock is not null)
            {
                var updated = state.Lock.Clone();
                updated.Set(LockFields.RefreshedAt, RigTime.Stamp(now));
                updated.Set(LockFields.ActiveAt, RigTime.Stamp(now));
                WriteLock(updated);
            }
            else if (state.Renew && state.Lock is not null)
            {
                var updated = state.Lock.Clone();
                updated.Set(LockFields.RefreshedAt, RigTime.Stamp(now));
                WriteLock(updated);
            }

            return state;
        });
    }

    /// <summary>Everything <c>status</c> needs. Reads only: no renew, no probe side effects.</summary>
    public LockStatus GetStatus(string? callerId)
    {
        var now = _clock.UtcNow;
        var fields = ReadLock();
        var busy = _busy.Probe();

        var state = fields is null
            ? LockState.None
            : LockFields.SameOwner(callerId, fields.Get(LockFields.Owner))
                ? LockState.Mine
                : LockState.LiveForeign;

        return new LockStatus(
            state,
            fields,
            fields is not null && LockFields.IsTimerExpired(fields, now),
            fields is not null && LockFields.IsIdleCeilingExceeded(fields, now),
            busy,
            _marker.GetState(),
            _marker.ReadSessionWorlds(WorldScope.Server),
            _marker.ReadSessionWorlds(WorldScope.Client));
    }

    // ---- acquiring ---------------------------------------------------------

    /// <summary>Acquires, re-asserts, reclaims or breaks the session lock.</summary>
    public async Task<LockAcquireResult> AcquireAsync(AcquireOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Purpose))
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                $"'lock' requires --purpose \"<short reason>\", e.g. --purpose \"Playtesting network paint for "
                + $"SprayPaintPlus\". See {LockMessages.Rules}.");
        }

        var pollSeconds = Math.Max(1, options.PollSeconds);
        var waitSeconds = Math.Max(0, options.WaitSeconds);
        var deadline = _clock.UtcNow.AddSeconds(waitSeconds);
        var announced = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var attempt = TryAcquireOnce(options);

            if (attempt.Blocked is not null)
            {
                var now = _clock.UtcNow;
                if (now >= deadline)
                {
                    throw new RigRefusalException(
                        RigRefusalKind.HeldByAnotherSession,
                        LockMessages.AcquireBlocked(attempt.Blocked, waitSeconds, now));
                }

                if (!announced)
                {
                    _output.Line(OutputLevel.Info, "[Lock] Rig is held by another session; queueing. It is a queue, not a reservation: no ordering fairness is promised.");
                    _output.Line(OutputLevel.Info, LockMessages.FormatForeignLock(attempt.Blocked, now));
                    announced = true;
                }
                else
                {
                    var left = (int)Math.Max(0, (deadline - now).TotalSeconds);
                    var purpose = attempt.Blocked.Lock?.GetOrEmpty(LockFields.Purpose) ?? string.Empty;
                    _output.Line(OutputLevel.Info, $"[Lock]   still held by '{purpose}'; {left}s left.");
                }

                var sleep = Math.Min(pollSeconds, Math.Max(1, (int)(deadline - now).TotalSeconds));
                await _sleeper.DelayAsync(TimeSpan.FromSeconds(sleep), ct).ConfigureAwait(false);
                continue;
            }

            return FinishAcquire(options, attempt);
        }
    }

    private readonly record struct AcquireAttempt(
        LockStateSnapshot? Blocked,
        string Owner,
        AcquireKind Kind,
        int TtlMinutes,
        int IdleCeilingMinutes,
        LockStateSnapshot State);

    private AcquireAttempt TryAcquireOnce(AcquireOptions options)
    {
        return WithMutex("acquire the rig lock", () =>
        {
            var now = _clock.UtcNow;
            var fields = ReadLock();
            BusySignal? busy = null;
            if (LockClassifier.IsBusyProbeNeeded(fields, options.CallerId, now)) busy = _busy.Probe();

            var state = LockClassifier.Resolve(fields, options.CallerId, busy, now);

            if (state.State == LockState.LiveForeign && !options.BreakLock)
            {
                if (state.Renew && state.Lock is not null)
                {
                    var renewed = state.Lock.Clone();
                    renewed.Set(LockFields.RefreshedAt, RigTime.Stamp(now));
                    WriteLock(renewed);
                }
                return new AcquireAttempt(state, "", AcquireKind.Acquired, 0, 0, state);
            }

            if (state.State == LockState.Mine && state.Lock is not null)
            {
                var owner = state.Lock.GetOrEmpty(LockFields.Owner);
                var ttl = options.TtlMinutes ?? LockFields.GetTtl(state.Lock);
                var ceiling = options.IdleCeilingMinutes
                              ?? LockFields.GetIdleCeiling(state.Lock)
                              ?? LockFields.DefaultIdleCeilingMinutes;

                WriteLock(BuildLockFields(
                    owner,
                    options.Purpose,
                    acquiredAt: state.Lock.GetOrEmpty(LockFields.AcquiredAt),
                    now,
                    ttl,
                    ceiling));

                return new AcquireAttempt(null, owner, AcquireKind.Reasserted, ttl, ceiling, state);
            }

            var newOwner = _mintOwnerId();
            var newTtl = options.TtlMinutes ?? LockFields.DefaultTtlMinutes;
            var newCeiling = options.IdleCeilingMinutes ?? LockFields.DefaultIdleCeilingMinutes;

            WriteLock(BuildLockFields(newOwner, options.Purpose, RigTime.Stamp(now), now, newTtl, newCeiling));

            var kind = state.State switch
            {
                LockState.LiveForeign => AcquireKind.Broke,
                LockState.DeadForeign when state.Reclaim == ReclaimReason.IdleCeiling => AcquireKind.ReclaimedIdleCeiling,
                LockState.DeadForeign => AcquireKind.ReclaimedExpired,
                _ => AcquireKind.Acquired,
            };

            return new AcquireAttempt(null, newOwner, kind, newTtl, newCeiling, state);
        });
    }

    /// <summary>
    /// Everything after the critical section: the warnings, the reclaim callback, the
    /// owner id, and the restore.
    /// </summary>
    /// <remarks>
    /// Ordering here is load bearing. The owner id is emitted BEFORE the restore can
    /// throw, so a caller whose reset fails still knows the id it needs to unlock with.
    /// The re-assert returns before the restore, because re-asserting a lock you already
    /// hold is not a session boundary. The reclaim callback and the restore run outside
    /// the mutex because they are slow, and the lock is already ours by then, so they run
    /// under our own reservation instead of blocking every other agent.
    /// </remarks>
    private LockAcquireResult FinishAcquire(AcquireOptions options, AcquireAttempt attempt)
    {
        if (attempt.Kind == AcquireKind.Reasserted)
        {
            _output.Line(OutputLevel.Info,
                $"[Lock] Re-asserted the rig session lock (owner {attempt.Owner}). Pass --as {attempt.Owner} on mutating commands.");
            _output.Line(OutputLevel.Info,
                "[Lock]   state was NOT reset: this is the same session, not a new one.");
            _output.Value("owner", attempt.Owner);
            _output.Value("acquireKind", nameof(AcquireKind.Reasserted));
            return new LockAcquireResult(
                attempt.Owner, AcquireKind.Reasserted, options.Purpose,
                attempt.TtlMinutes, attempt.IdleCeilingMinutes, StateWasReset: false, attempt.State.BusyDetail);
        }

        var now = _clock.UtcNow;

        if (attempt.Kind == AcquireKind.Broke && attempt.State.Lock is not null)
        {
            // A break does NOT run OnReclaim: it leaves the previous session's processes
            // running, deliberately, because a human authorised taking the reservation and
            // not necessarily killing what is on the rig.
            _output.Line(OutputLevel.Warning, LockMessages.BrokeLiveLock(
                attempt.State.Lock.GetOrEmpty(LockFields.Purpose),
                attempt.State.Lock.GetOrEmpty(LockFields.Owner)));
        }
        else if (attempt.State.State == LockState.DeadForeign && attempt.State.Lock is not null)
        {
            if (attempt.State.Reclaim == ReclaimReason.IdleCeiling)
            {
                _output.Line(OutputLevel.Warning, LockMessages.WatchdogReclaimed(
                    attempt.State.Lock.GetOrEmpty(LockFields.Owner),
                    attempt.State.Lock.GetOrEmpty(LockFields.Purpose),
                    LockMessages.IdleText(attempt.State.Lock, now),
                    LockFields.GetIdleCeiling(attempt.State.Lock) ?? LockFields.DefaultIdleCeilingMinutes));

                if (!string.IsNullOrEmpty(attempt.State.BusyDetail))
                {
                    _output.Line(OutputLevel.Warning, LockMessages.WatchdogReclaimedBusy(attempt.State.BusyDetail));
                }
            }

            options.OnReclaim?.Invoke();
        }

        _output.Line(OutputLevel.Info, "[Lock] Acquired the rig session lock (covers BOTH TestRig halves).");
        _output.Line(OutputLevel.Info, $"[Lock]   owner   : {attempt.Owner}   (pass --as {attempt.Owner} on every mutating command)");
        _output.Line(OutputLevel.Info, $"[Lock]   purpose : {options.Purpose}");
        _output.Line(OutputLevel.Info, $"[Lock]   ttl     : {attempt.TtlMinutes} min heartbeat (refresh with: {options.Tool} refresh-lock --as {attempt.Owner}, while actively testing)");
        _output.Line(OutputLevel.Info, $"[Lock]   idle    : {attempt.IdleCeilingMinutes} min ceiling; after that long with no action from you, another agent may reclaim the rig even if it is busy");
        _output.Line(OutputLevel.Info, $"[Lock] Rules: {LockMessages.Rules}.");

        // THE machine-readable contract. A field, not a sentence a caller regexes out of
        // stdout: the PowerShell line it replaces has never once printed.
        _output.Value("owner", attempt.Owner);
        _output.Value("acquireKind", attempt.Kind.ToString());

        var dirty = _marker.GetState();
        if (dirty.Dirty)
        {
            _output.Line(OutputLevel.Warning, options.KeepState
                ? LockMessages.DirtyKeepState(DirtyMarker.Describe(dirty))
                : LockMessages.DirtyRestoring(DirtyMarker.Describe(dirty)));
        }

        var wasReset = false;
        if (_restore is not null)
        {
            try
            {
                var run = _restore.Restore(options.KeepState, "lock acquisition");
                wasReset = !run.Skipped && !run.Refused;
            }
            catch (Exception ex) when (ex is RigRefusalException or InvalidOperationException or IOException)
            {
                _output.Line(OutputLevel.Warning, LockMessages.ResetFailed(attempt.Owner, options.Tool));
                throw;
            }
        }
        else if (dirty.Dirty && !options.KeepState)
        {
            _output.Line(OutputLevel.Warning, LockMessages.NoRestoreImplementation);
        }

        return new LockAcquireResult(
            attempt.Owner, attempt.Kind, options.Purpose,
            attempt.TtlMinutes, attempt.IdleCeilingMinutes, wasReset, attempt.State.BusyDetail);
    }

    // ---- refreshing --------------------------------------------------------

    /// <summary>The explicit heartbeat. Moves both clocks: the owner is saying "I am still here".</summary>
    public RefreshResult Refresh(string? callerId, int? ttlMinutes = null, int? idleCeilingMinutes = null)
    {
        if (string.IsNullOrWhiteSpace(callerId))
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                "'refresh-lock' requires --as <id> (the owner id printed by 'lock').");
        }

        return WithMutex("refresh the rig lock", () =>
        {
            var fields = ReadLock();
            if (fields is null)
            {
                throw new RigRefusalException(RigRefusalKind.NoLockHeld, LockMessages.RefreshNoLock);
            }
            if (!LockFields.SameOwner(callerId, fields.Get(LockFields.Owner)))
            {
                throw new RigRefusalException(RigRefusalKind.HeldByAnotherSession, LockMessages.RefreshNotYours(fields, callerId));
            }

            var now = _clock.UtcNow;
            var updated = fields.Clone();
            updated.Set(LockFields.RefreshedAt, RigTime.Stamp(now));
            updated.Set(LockFields.ActiveAt, RigTime.Stamp(now));
            if (ttlMinutes is not null) updated.Set(LockFields.TtlMinutes, ttlMinutes.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (idleCeilingMinutes is not null) updated.Set(LockFields.IdleCeilingMinutes, idleCeilingMinutes.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // Upgrade repair: a lock written before the field existed gets the default,
            // appended at the end of the field order, which nothing depends on.
            if (!updated.Contains(LockFields.IdleCeilingMinutes))
            {
                updated.Set(LockFields.IdleCeilingMinutes, LockFields.DefaultIdleCeilingMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            WriteLock(updated);

            return new RefreshResult(
                updated.GetOrEmpty(LockFields.Owner),
                LockFields.GetTtl(updated),
                LockFields.GetIdleCeiling(updated) ?? LockFields.DefaultIdleCeilingMinutes);
        });
    }

    /// <summary>
    /// Best-effort refresh from a readiness barrier. Never throws, never prints.
    /// </summary>
    /// <remarks>
    /// A barrier IS the owner working: bounded, foreground, attached to a test in
    /// progress, so it moves <c>active_at</c> as well as the heartbeat. What the rules
    /// forbid is a background refresher with no test behind it, and that is a rule, not
    /// something this method can enforce.
    /// </remarks>
    public void RefreshIfMine(string? callerId)
    {
        if (string.IsNullOrWhiteSpace(callerId)) return;

        WithMutex("refresh the rig lock", () =>
        {
            var fields = ReadLock();
            if (fields is null || !LockFields.SameOwner(callerId, fields.Get(LockFields.Owner))) return 0;

            var now = _clock.UtcNow;
            var updated = fields.Clone();
            updated.Set(LockFields.RefreshedAt, RigTime.Stamp(now));
            updated.Set(LockFields.ActiveAt, RigTime.Stamp(now));
            WriteLock(updated);
            return 0;
        });
    }

    // ---- the gate ----------------------------------------------------------

    /// <summary>
    /// The gate every mutating operation calls first.
    /// </summary>
    /// <remarks>
    /// Classification and refresh happen in one critical section, so no other agent can
    /// slip between them. The marker write that follows is outside it, and the gated
    /// action after that is unprotected: an agent past the ceiling can reclaim the rig
    /// mid-action. That is inherent to a session lock spanning processes, is bounded by
    /// the two timers, and is not fixable without holding a kernel object across a game
    /// launch.
    ///
    /// A marker failure is deliberately not fatal. Losing crash detection is bad;
    /// refusing every mutating command because a file could not be written is worse, and
    /// would make the rig unusable rather than merely unverified.
    /// </remarks>
    public void AssertHeld(string action, string? callerId, string tool = "testrig")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        var state = ReadStateAndRenew(callerId, refreshIfMine: true);
        switch (state.State)
        {
            case LockState.Mine when state.Lock is not null:
                try
                {
                    if (_marker.Write(
                            state.Lock.GetOrEmpty(LockFields.Owner),
                            state.Lock.GetOrEmpty(LockFields.Purpose),
                            action))
                    {
                        _output.Line(OutputLevel.Info, LockMessages.MarkedDirty(action));
                    }
                }
                catch (Exception ex) when (ex is RigRefusalException or IOException or UnauthorizedAccessException)
                {
                    _output.Line(OutputLevel.Warning, LockMessages.MarkerWriteFailed(_paths.DirtyFile, ex.Message));
                }
                return;

            case LockState.None:
                throw new RigRefusalException(RigRefusalKind.NoLockHeld, LockMessages.GateNoLock(action, tool));

            case LockState.DeadForeign:
                throw new RigRefusalException(RigRefusalKind.NoLockHeld, LockMessages.GateDeadLock(action, tool));

            default:
                throw new RigRefusalException(
                    RigRefusalKind.HeldByAnotherSession,
                    LockMessages.GateForeignLock(action, state, _clock.UtcNow));
        }
    }

    // ---- releasing ---------------------------------------------------------

    /// <summary>Releases the lock the caller owns, restoring the rig on the way out.</summary>
    public ReleaseResult Release(string? callerId, bool breakLock = false, bool force = false, bool keepState = false) =>
        ReleaseCore(callerId, keepState, refuseOnLiveHost: !force, authorise: fields =>
        {
            if (LockFields.SameOwner(callerId, fields.Get(LockFields.Owner))) return null;
            if (breakLock) return null;
            // The ownership check runs BEFORE the force check, so --force alone can never
            // take a lock off somebody else. --force overrides exactly one refusal, the
            // live listen host, and never touches ownership.
            return LockMessages.UnlockNotYours(fields, callerId);
        });

    /// <summary>
    /// Releases as part of <c>stop --release</c>.
    /// </summary>
    /// <remarks>
    /// This goes through the SAME three-phase release as unlock, which the PowerShell
    /// path did not: it read the lock outside the mutex, ran the slow restore, then
    /// deleted the file with no post-restore ownership re-check, so an acquisition that
    /// completed during the restore had its brand-new lock file deleted (spec 02-lock race
    /// R-1, defect D6). <c>Remove-RigLock</c> guarded precisely this and <c>stop</c> did
    /// not.
    ///
    /// The caller must classify first: this predicate has no busy term, and it is the
    /// caller's LiveForeign refusal that stops a busy foreign session losing its lock.
    /// </remarks>
    public ReleaseResult ReleaseForStop(string? callerId, bool breakLock = false, bool keepState = false)
    {
        var now = _clock.UtcNow;
        return ReleaseCore(callerId, keepState, refuseOnLiveHost: false, authorise: fields =>
            LockClassifier.IsReleasableOnStop(fields, callerId, breakLock, now)
                ? null
                : LockMessages.StopNotYours(fields.GetOrEmpty(LockFields.Owner)));
    }

    private ReleaseResult ReleaseCore(
        string? callerId,
        bool keepState,
        bool refuseOnLiveHost,
        Func<FieldText, string?> authorise)
    {
        // PHASE 1: validate under the mutex, with the busy probe computed inside it. The
        // PowerShell version probed before the critical section and never re-checked, so a
        // listen host that came up in between did not trigger the refusal (race R-3).
        BusySignal busy = BusySignal.Idle();
        var validated = WithMutex("check the rig lock before releasing", () =>
        {
            busy = _busy.Probe();
            var fields = ReadLock();
            if (fields is null) return null;

            var refusal = authorise(fields);
            if (refusal is not null)
            {
                throw new RigRefusalException(RigRefusalKind.HeldByAnotherSession, refusal);
            }

            if (refuseOnLiveHost && busy.HostLive)
            {
                throw new RigRefusalException(
                    RigRefusalKind.RigBusy,
                    LockMessages.UnlockHostLive(busy.HostNames, busy.Detail, callerId));
            }

            return fields;
        });

        if (validated is null)
        {
            return new ReleaseResult(ReleaseStatus.NoLock, null, "[Unlock] No rig session lock present.", false, null, busy);
        }

        var owner = validated.GetOrEmpty(LockFields.Owner);

        // PHASE 2: the restore, with no mutex held. This is where the between-session
        // guarantee is earned: the session that made the mess pays for it while it still
        // owns the rig and the rig is provably idle. The mutex must never be held across
        // anything slow.
        var restoreSkipped = false;
        string? restoreFailure = null;

        if (keepState)
        {
            restoreSkipped = true;
            _output.Line(OutputLevel.Warning, LockMessages.KeepStateOnRelease);
        }
        else if (_restore is not null)
        {
            try
            {
                _restore.Restore(keepState: false, "lock release");
            }
            catch (Exception ex) when (ex is RigRefusalException or InvalidOperationException or IOException)
            {
                // A failed restore still releases. The marker is only cleared by a restore
                // that completed, so a failure leaves the rig marked and the next
                // acquisition restores it before handing it over. Refusing to release would
                // leave a hung session holding the rig on top of a mess.
                restoreFailure = ex.Message;
                _output.Line(OutputLevel.Warning, LockMessages.RestoreFailedOnRelease(ex.Message));
                _output.Line(OutputLevel.Warning, LockMessages.RestoreFailedOnReleaseDetail);
            }
        }

        // PHASE 3: re-validate and delete, under the mutex again. An authorized break from
        // elsewhere could have replaced the file during the restore, and deleting a lock
        // this command never validated would be exactly the stomp the mechanism prevents.
        return WithMutex("release the rig lock", () =>
        {
            var current = ReadLock();
            if (current is null)
            {
                return new ReleaseResult(ReleaseStatus.AlreadyGone, owner, LockMessages.UnlockGone, restoreSkipped, restoreFailure, busy);
            }

            var currentOwner = current.GetOrEmpty(LockFields.Owner);
            if (!LockFields.SameOwner(currentOwner, owner))
            {
                return new ReleaseResult(
                    ReleaseStatus.Stolen, owner,
                    LockMessages.UnlockStolen(currentOwner, current.GetOrEmpty(LockFields.Purpose), owner),
                    restoreSkipped, restoreFailure, busy);
            }

            RigFiles.Delete(_fs, _paths.LockFile, "rig lock file");
            return new ReleaseResult(
                ReleaseStatus.Released, owner,
                $"[Unlock] Rig session lock released (was owner {owner}).",
                restoreSkipped, restoreFailure, busy);
        });
    }

    // ---- plumbing ----------------------------------------------------------

    private FieldText BuildLockFields(string owner, string purpose, string acquiredAt, DateTimeOffset now, int ttl, int ceiling)
    {
        var stamp = RigTime.Stamp(now);
        var fields = new FieldText();
        fields.Set(LockFields.Owner, owner);
        fields.Set(LockFields.Purpose, SanitisePurpose(purpose));
        fields.Set(LockFields.AcquiredAt, string.IsNullOrEmpty(acquiredAt) ? stamp : acquiredAt);
        fields.Set(LockFields.RefreshedAt, stamp);
        fields.Set(LockFields.ActiveAt, stamp);
        fields.Set(LockFields.TtlMinutes, ttl.ToString(System.Globalization.CultureInfo.InvariantCulture));
        fields.Set(LockFields.IdleCeilingMinutes, ceiling.ToString(System.Globalization.CultureInfo.InvariantCulture));
        fields.Set(LockFields.Host, _launcher.HostName);
        return fields;
    }

    /// <summary>Control characters would split the line into a bogus second key on the next parse.</summary>
    private static string SanitisePurpose(string purpose)
    {
        var cleaned = new string(purpose.Select(static c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return cleaned;
    }

    private void WriteLock(FieldText fields) =>
        RigFiles.WriteAtomic(_fs, _paths.LockFile, fields.Render(Header), "rig lock file");

    /// <summary>
    /// Runs a body as sole holder of the cross-process critical section.
    /// </summary>
    /// <remarks>
    /// An abandoned mutex means a process was killed while holding it. The wait SUCCEEDED
    /// and this process now owns it, so the body runs and the handle is released as usual.
    /// Carrying on is safe because the lock file is never left half-written (staged temp
    /// plus atomic replace). Measured in the PowerShell suite: recovery is immediate, not
    /// a timeout.
    ///
    /// A process-local fallback is reported rather than silent. PowerShell fell back from
    /// Global to Local per process with nothing logged, so two processes could resolve
    /// differently and not be serialised at all; the measured cost without a working
    /// critical section was four simultaneous winners per round across 20 rounds.
    /// </remarks>
    private T WithMutex<T>(string context, Func<T> body)
    {
        using var held = _mutex.TryEnter(MutexTimeout, out var outcome);
        if (outcome == MutexAcquisition.TimedOut)
        {
            throw new RigRefusalException(
                RigRefusalKind.Broken,
                LockMessages.MutexTimeout((int)MutexTimeout.TotalSeconds, _mutex.Name, context));
        }

        if (outcome == MutexAcquisition.AcquiredAbandoned)
        {
            _output.Line(OutputLevel.Warning, LockMessages.AbandonedMutex);
        }

        if (_mutex.IsProcessLocal)
        {
            _output.Line(OutputLevel.Warning, LockMessages.ProcessLocalMutex);
        }

        return body();
    }
}
