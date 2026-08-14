namespace TestRig.Core.Session;

/// <summary>
/// The process exit codes the rig uses, declared once.
/// </summary>
/// <remarks>
/// <para>
/// The PowerShell rig used 0, 1 and 2, so contention, a lapsed reservation, an unlock by a
/// non-owner, a mutex timeout and a genuinely broken rig were indistinguishable to a caller
/// by exit code alone. The playtest harness collapsed every non-zero exit into
/// "inconclusive / rig-unavailable" as a result, which is how a refusal that a retry would
/// never fix looked exactly like a rig that was momentarily busy.
/// </para>
/// <para>
/// They live in Core rather than in the entry point because the playtest engine records the
/// code a lock attempt produced into its evidence bundle, and two tables that had to agree
/// about what 4 means is precisely the drift this port keeps removing.
/// </para>
/// </remarks>
public static class RigExitCodes
{
    /// <summary>Did what you asked.</summary>
    public const int Ok = 0;

    /// <summary>Tried and failed, including "your machine is not set up".</summary>
    public const int Failed = 1;

    /// <summary>The command itself was wrong: an unknown verb, a missing value, a bad flag.</summary>
    public const int UsageError = 2;

    /// <summary>Refused, with a working alternative named.</summary>
    public const int Refused = 3;

    /// <summary>The lock is held by another session.</summary>
    public const int LockHeldByOther = 4;

    /// <summary>No lock is held by you.</summary>
    public const int LockNotHeld = 5;

    /// <summary>The rig is in use, so the requested state change is unsafe.</summary>
    public const int RigBusy = 6;

    /// <summary>This binary does not match the source tree beside it.</summary>
    public const int StaleBinary = 7;

    /// <summary>
    /// A playtest run in which nothing failed but something could not be measured.
    /// </summary>
    /// <remarks>
    /// Its own code, and not <see cref="Failed"/>, because the two mean opposite things about
    /// the mod: a fail accuses it, an inconclusive says the rig never got far enough to have
    /// an opinion. A caller that cannot tell them apart will eventually treat one as the
    /// other, and the whole three-outcome model exists to stop that.
    /// </remarks>
    public const int PlaytestInconclusive = 8;

    /// <summary>The code a typed refusal maps to.</summary>
    public static int For(RigRefusalKind kind) => kind switch
    {
        RigRefusalKind.Refused => Refused,
        RigRefusalKind.HeldByAnotherSession => LockHeldByOther,
        RigRefusalKind.NoLockHeld => LockNotHeld,
        RigRefusalKind.RigBusy => RigBusy,
        _ => Failed,
    };

    /// <summary>The code a release outcome maps to.</summary>
    /// <remarks>
    /// <para>
    /// Declared once because three callers need it and each had written its own
    /// <c>status == NotYours ? 4 : 0</c>, which is the drift this table exists to remove. All
    /// three agreed on the one arm that cannot happen (the authorising predicates throw
    /// <see cref="RigRefusalKind.HeldByAnotherSession"/> before a
    /// <see cref="ReleaseStatus.NotYours"/> is ever constructed) and all three fell through to
    /// <see cref="Ok"/> on the two that can.
    /// </para>
    /// <para>
    /// Measured on the shipped binary: <c>testrig unlock --as deadbeef</c> against a rig with
    /// no lock at all exited 0 while printing "No rig session lock present". Zero is the code
    /// a caller reads as "released", so an agent that mistyped its owner id, or whose lock had
    /// been reclaimed under it, was told its session had ended cleanly. The same fall-through
    /// reached <c>stop --release</c> and the playtest engine's own teardown.
    /// </para>
    /// <para>
    /// <see cref="ReleaseStatus.AlreadyGone"/> stays <see cref="Ok"/> deliberately: the restore
    /// ran, the caller holds nothing afterwards, and that is the state the caller asked for.
    /// <see cref="ReleaseStatus.Stolen"/> does not, because the rig belongs to somebody else by
    /// the time the restore finishes and their lock was deliberately left alone.
    /// </para>
    /// </remarks>
    public static int For(ReleaseStatus status) => status switch
    {
        ReleaseStatus.Released => Ok,
        ReleaseStatus.AlreadyGone => Ok,
        ReleaseStatus.NoLock => LockNotHeld,
        ReleaseStatus.Stolen => LockHeldByOther,
        ReleaseStatus.NotYours => LockHeldByOther,
        _ => Failed,
    };
}
