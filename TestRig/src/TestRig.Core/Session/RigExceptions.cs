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
public sealed class RigRefusalException : Exception
{
    public RigRefusalException(RigRefusalKind kind, string message, Refusal? refusal = null)
        : base(message)
    {
        Kind = kind;
        Refusal = refusal;
    }

    public RigRefusalKind Kind { get; }

    /// <summary>The teaching form (what, why, what works instead), when there is one.</summary>
    public Refusal? Refusal { get; }
}
