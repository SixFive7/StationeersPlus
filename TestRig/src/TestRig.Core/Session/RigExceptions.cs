using TestRig.Core.Abstractions;

namespace TestRig.Core.Session;

/// <summary>
/// Why a rig operation stopped. The CLI maps these to exit codes.
/// </summary>
/// <remarks>
/// The PowerShell rig exited 1 for every lock refusal, so contention, a lapsed
/// reservation, an unlock by a non-owner, a mutex timeout and a genuinely broken rig
/// were indistinguishable to a caller by exit code alone (spec 02-lock C.10). The
/// playtest harness papered over that by treating any non-zero exit as
/// "inconclusive / rig-unavailable". A caller should be able to branch without parsing
/// prose.
/// </remarks>
public enum RigRefusalKind
{
    /// <summary>Something the rig could not do at all: a file it could not read or replace.</summary>
    Broken,

    /// <summary>The caller asked for something the rules forbid, with a working alternative.</summary>
    Refused,

    /// <summary>Another session holds the lock.</summary>
    HeldByAnotherSession,

    /// <summary>No live lock is held by this caller.</summary>
    NoLockHeld,

    /// <summary>The rig is in use, so the requested state change is unsafe.</summary>
    RigBusy,
}

/// <summary>A refusal, carrying the kind so a caller does not have to read the sentence.</summary>
/// <remarks>
/// Not sealed, for exactly one subclass: <see cref="RigSessionStartException"/>. Every catch
/// in the rig and in the CLI is written against this type, and the acquisition failure that
/// subclass describes is a refusal in every way that matters to them, so widening the
/// hierarchy is what keeps a caller that only knows about refusals behaving as it did.
/// </remarks>
public class RigRefusalException : Exception
{
    public RigRefusalException(RigRefusalKind kind, string message, Refusal? refusal = null)
        : base(message)
    {
        Kind = kind;
        Refusal = refusal;
    }

    public RigRefusalException(RigRefusalKind kind, string message, Exception? inner, Refusal? refusal = null)
        : base(message, inner)
    {
        Kind = kind;
        Refusal = refusal;
    }

    public RigRefusalKind Kind { get; }

    /// <summary>The teaching form (what, why, what works instead), when there is one.</summary>
    public Refusal? Refusal { get; }
}

/// <summary>
/// The lock was TAKEN, and then the session could not be started on top of it.
/// </summary>
/// <remarks>
/// <para>
/// Acquisition is two steps and only the first one is atomic: the lock file is written inside
/// the critical section, and the between-session state reset runs afterwards, outside it,
/// under the reservation that write created. So a reset that throws leaves a REAL lock on
/// disk owned by a caller that just saw an exception.
/// </para>
/// <para>
/// <b>The owner id is the whole point of the type.</b> Without it the failure is
/// indistinguishable from "the rig was never yours", and a caller that holds a typed result
/// rather than the console cannot recover the id from anywhere: the playtest harness took the
/// lock, was told only that the acquisition was refused, and left the rig held by an id it
/// never saw. Measured 2026-08-16 on a live suite: owner 8dd76948, three checks lost to
/// rig-unavailable behind it, cleared by hand.
/// </para>
/// </remarks>
public sealed class RigSessionStartException : RigRefusalException
{
    public RigSessionStartException(string owner, string message, Exception inner)
        : base(
            inner is RigRefusalException refusal ? refusal.Kind : RigRefusalKind.Broken,
            message,
            inner,
            (inner as RigRefusalException)?.Refusal) =>
        Owner = owner;

    /// <summary>The session id the lock file carries. Release with it; it is a real reservation.</summary>
    public string Owner { get; }
}
