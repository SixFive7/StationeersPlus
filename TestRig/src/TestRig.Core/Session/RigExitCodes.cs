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
}
