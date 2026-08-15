namespace TestRig.Playtest.Model;

/// <summary>
///     The three outcomes a check can reach, and nothing else.
/// </summary>
/// <remarks>
///     The whole harness is built around one asymmetry: an inconclusive costs a re-run, a
///     false fail costs a developer a day chasing a bug that is not there. So the only
///     thing that may produce <see cref="Fail"/> is an assert verb that read a value from
///     the authority and found the wrong one. Everything else is
///     <see cref="Inconclusive"/>, <b>including a bug in the check itself</b>.
/// </remarks>
public enum CheckOutcome
{
    /// <summary>The check made its observation and the value was right. Exit code 0.</summary>
    Pass,

    /// <summary>An assert verb read a value and found the wrong one. The mod is the suspect. Exit code 1.</summary>
    Fail,

    /// <summary>The rig, not the mod. Nothing was learned about the mod. Exit code 2.</summary>
    Inconclusive,
}

/// <summary>
///     Which of the two signalled kinds an exception carries.
/// </summary>
/// <remarks>
///     There is deliberately no <c>Pass</c> here: a pass is the absence of a signal, so a
///     check cannot declare itself passing. There is also no way for a check to construct
///     a <see cref="Fail"/>; see <see cref="PlaytestSignal"/>.
/// </remarks>
public enum SignalKind
{
    Fail,
    Inconclusive,
}

/// <summary>
///     Every detector name the library itself can put on a result.
/// </summary>
/// <remarks>
///     The vocabulary is open: a check may pass any string to
///     <c>SetInconclusive</c>, and the shipped Spray Paint Plus suite uses fourteen names
///     of its own. These are the ones the engine owns, so a report reader can tell an
///     engine verdict from a check's own.
/// </remarks>
public static class Detectors
{
    // ---- the only detector a fail can carry -------------------------------

    /// <summary>An assert verb read a value from the authority and it was wrong.</summary>
    public const string Assertion = "assertion";

    // ---- inconclusive, raised by the engine -------------------------------

    /// <summary>The check declared it cannot make its observation.</summary>
    public const string CheckDeclined = "check-declined";

    /// <summary>Anything unmarked thrown out of a check body, including a bug in the check.</summary>
    public const string UnclassifiedError = "unclassified-error";

    /// <summary>An instance the check named is not in the rig registry.</summary>
    public const string InstanceNotProvisioned = "instance-not-provisioned";

    /// <summary>An endpoint refused and no flake detector matched.</summary>
    public const string ActionRefused = "action-refused";

    /// <summary>A launcher start of an instance failed.</summary>
    public const string InstanceStartFailed = "instance-start-failed";

    /// <summary>A launcher restart of an instance failed.</summary>
    public const string InstanceRestartFailed = "instance-restart-failed";

    /// <summary>A reader could not reach its source and nothing more specific matched.</summary>
    public const string ReaderUnreachable = "reader-unreachable";

    /// <summary>The rig lock could not be taken, or was taken without an owner id.</summary>
    public const string RigUnavailable = "rig-unavailable";

    // ---- inconclusive, raised by attestation ------------------------------

    /// <summary>The build under test is not on disk.</summary>
    public const string BinaryMissing = "binary-missing";

    /// <summary>An instance has no readable provision stamp.</summary>
    public const string ProvisionStampMissing = "provision-stamp-missing";

    /// <summary>The build under test is not deployed into the instance.</summary>
    public const string BinaryNotDeployed = "binary-not-deployed";

    /// <summary>
    ///     The instance was provisioned to test this mod, and nothing was ever deployed.
    /// </summary>
    /// <remarks>
    ///     Distinct from <see cref="BinaryNotDeployed"/> because the remedy and the meaning
    ///     differ. An instance that records a mod under test does NOT seed the developer's
    ///     copy of it, deliberately, so a missing deploy leaves the instance with no copy at
    ///     all rather than with the wrong one. Reporting that as the general case would send a
    ///     reader looking for a stale file that cannot be there.
    /// </remarks>
    public const string UnderTestNotDeployed = "under-test-not-deployed";

    /// <summary>
    ///     A check's mod is not in the under-test set of an instance the check names.
    /// </summary>
    /// <remarks>
    ///     Raised before bring-up, so it costs no game process. The instance carries the
    ///     DEVELOPER'S published copy of that mod, and a check running there would measure a
    ///     build this repository did not produce while reporting on one it did.
    /// </remarks>
    public const string ModNotUnderTestHere = "mod-not-under-test-here";

    /// <summary>The deployed file's content hash differs from the build under test.</summary>
    public const string BinaryStale = "binary-stale";

    /// <summary>The running process reports no configuration at all for the mod's guid.</summary>
    public const string BinaryConfigMismatch = "binary-config-mismatch";

    /// <summary>The mod's identity could not be derived from the check's own location.</summary>
    public const string ModIdentityUnresolved = "mod-identity-unresolved";

    // ---- the two post-hoc gates -------------------------------------------

    /// <summary>The body completed without ever attesting the binary under test.</summary>
    public const string BinaryNotAttested = "binary-not-attested";

    /// <summary>
    ///     The body completed without making a single assertion. Defect P-02: the
    ///     PowerShell harness had no assertion counter anywhere, so a check with a valid
    ///     binary block and an empty body reported a clean pass, and its own suite
    ///     registered exactly that shape twice without ever asserting the outcome.
    /// </summary>
    public const string NoAssertions = "no-assertions";
}
