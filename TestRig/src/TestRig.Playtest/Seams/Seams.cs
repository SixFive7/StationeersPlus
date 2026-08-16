namespace TestRig.Playtest.Seams;

/// <summary>One control-plane answer: the status it arrived at, and the body.</summary>
/// <param name="HttpStatus">The transport status. Diagnostic; never branch on it alone.</param>
/// <param name="Body">The raw JSON, exactly as the plugin wrote it.</param>
public readonly record struct TransportResponse(int HttpStatus, string Body);

/// <summary>
///     A request that did not complete: a refused connection, a timeout, a socket fault.
/// </summary>
/// <remarks>
///     <b>A non-2xx answer is NOT one of these.</b> The PowerShell transport threw on any
///     non-2xx with the body in the message, so a 409 refusal arrived at the harness wearing
///     a transport fault's clothes and was retried three times as a rig flake before being
///     reported under a detector that misdiagnosed it. Here a refusal comes back as a normal
///     <see cref="TransportResponse"/> with its status and body intact, and the action layer
///     classifies it with the response in hand, which is what lets a duplicate-identity
///     refusal on <c>/connect</c> reach the detector that understands it.
/// </remarks>
public sealed class RigTransportException : Exception
{
    public RigTransportException(string message) : base(message)
    {
    }

    public RigTransportException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>The control plane, as one call.</summary>
/// <remarks>
///     A body is sent as POST, its absence as GET. The plugin's router never reads the
///     method, so this is convention rather than contract, but it keeps a capture readable.
/// </remarks>
public interface IRigTransport
{
    TransportResponse Send(int port, string path, string? bodyJson, TimeSpan timeout);
}

/// <summary>What the launcher did.</summary>
/// <param name="Success">Whether the verb completed.</param>
/// <param name="ExitCode">The launcher's own code, for the evidence bundle.</param>
/// <param name="Message">
///     One line explaining a failure. This replaces taking the first non-blank line of
///     concatenated stdout and stderr, which six sites did in two different orders (defect
///     P-12) and two of them without filtering blanks, so a leading blank line produced an
///     empty explanation.
/// </param>
/// <param name="Output">Everything the verb printed, verbatim, for the bundle.</param>
public sealed record LauncherResult(bool Success, int ExitCode, string Message, string Output)
{
    public static LauncherResult Ok(string output = "") => new(true, 0, string.Empty, output);

    public static LauncherResult Failed(string message, int exitCode = 1, string output = "") =>
        new(false, exitCode, message, output);
}

/// <summary>What acquiring the rig session lock produced.</summary>
/// <param name="Success">Whether the lock was granted.</param>
/// <param name="Owner">
///     The session id every subsequent mutating command must carry.
///     <para>
///     <b>This field is the whole reason the launcher seam is typed.</b> The PowerShell
///     harness recovered the owner id with a regex over launcher prose
///     (<c>^\s*TESTRIG-OWNER\s+([0-9a-fA-F]{6,16})\s*$</c>), and that line has in fact never
///     been printed, because the launcher guarded the write on a property its own lock
///     function never returned. Every check in every suite would have thrown
///     <c>inconclusive/rig-unavailable</c> and then unlocked with the id it never received.
///     Two documents describe the line as working and both test assertions covering it are
///     source-text greps, so the suite is green and actively certifies the no-op.
///     </para>
/// </param>
/// <param name="StateResetReport">
///     The between-session state reset, as the launcher reported it. Written to the bundle
///     BEFORE success is checked, so a refused lock still leaves its explanation behind.
/// </param>
/// <param name="Message">One line explaining a refusal.</param>
/// <param name="ExitCode">The launcher's own code.</param>
public sealed record LockGrant(
    bool Success,
    string Owner,
    string StateResetReport,
    string Message,
    int ExitCode)
{
    public static LockGrant Granted(string owner, string stateResetReport = "") =>
        new(true, owner, stateResetReport, string.Empty, 0);

    public static LockGrant Refused(string message, string stateResetReport = "", int exitCode = 1) =>
        new(false, string.Empty, stateResetReport, message, exitCode);

    /// <summary>
    ///     The rig WAS reserved, and then the session could not be started on it.
    /// </summary>
    /// <remarks>
    ///     A failed acquisition is not the same thing as an untaken lock, and collapsing the
    ///     two is what leaked the rig. Acquisition writes the lock file first and runs the
    ///     between-session state reset afterwards; when that reset fails, the caller holds a
    ///     real reservation under <paramref name="owner"/> and must give it back, even though
    ///     nothing may be driven on top of it. <see cref="Success"/> stays false precisely
    ///     because nothing may be driven.
    /// </remarks>
    public static LockGrant TakenWithoutASession(string owner, string message, string stateResetReport = "", int exitCode = 1) =>
        new(false, owner, stateResetReport, message, exitCode);

    /// <summary>The lock is on disk under <see cref="Owner"/> although the session never started.</summary>
    public bool NeedsRelease => !Success && !string.IsNullOrWhiteSpace(Owner);
}

/// <summary>
///     Everything the harness does to the rig, as five verbs.
/// </summary>
/// <remarks>
///     Deliberately five and no more: <c>lock</c>, <c>unlock</c>, <c>refresh-lock</c>,
///     <c>start</c> and <c>stop</c>, each naming ONE instance where an instance is involved.
///     Never a rig-wide target, never <c>reset</c>, <c>create</c> or <c>deploy</c>. A
///     rig-wide stop reaches every instance on the machine including another session's live
///     test, and creating an instance costs minutes and rebuilds a tree the caller may not
///     have meant to rebuild.
/// </remarks>
public interface IRigLauncher
{
    /// <param name="keepState">
    /// Skip the state restore at BOTH ends of this check's session.
    /// </param>
    /// <remarks>
    /// PLAYTEST-247. The rig resets between sessions and the harness takes one lock PER
    /// CHECK, so without this there is no way to hand a staged rig from one check to the
    /// next: whatever the first one built is restored away before the second one starts.
    /// Off by default, because a check that silently inherits another check's leftovers is
    /// the failure the per-check reset exists to prevent.
    /// </remarks>
    LockGrant AcquireLock(string purpose, int ttlMinutes, int waitSeconds, bool keepState = false);

    /// <inheritdoc cref="AcquireLock"/>
    LauncherResult ReleaseLock(string owner, bool keepState = false);

    LauncherResult RefreshLock(string owner);

    LauncherResult StartInstance(string name, string owner);

    LauncherResult StopInstance(string name, string owner, int timeoutSeconds, bool force);
}

/// <summary>One row of the client rig registry.</summary>
/// <param name="InstanceName">The instance's name, which is what a check names.</param>
/// <param name="Port">Its control-plane port.</param>
/// <param name="Role">The role it was provisioned as. The LIVE role is /status.role.</param>
/// <param name="InstancesRoot">
///     Where the game trees live, which is normally the game install's volume rather than
///     under TestRig/. Two roots, both correct: the tree is here, the instance DATA is under
///     the rig home.
/// </param>
/// <param name="UnderTest">
///     The mods this instance was provisioned to TEST, deployed from the repository build.
///     A mod outside this set is the developer's published copy, seeded from their folder, and
///     is not what a check about that mod may measure.
/// </param>
public sealed record RigInstanceRow(
    string InstanceName,
    int Port,
    string Role,
    string? InstancesRoot,
    IReadOnlyList<string> UnderTest);

/// <summary>The rig registry.</summary>
public interface IRigRegistry
{
    IReadOnlyList<RigInstanceRow> Rows();
}

/// <summary>
///     Reading a log file the game still holds open.
/// </summary>
/// <remarks>
///     Separate from the general filesystem seam because of the sharing mode: the game holds
///     <c>BepInEx/LogOutput.log</c> open for append while it runs, so an ordinary read fails
///     exactly when a check needs it most. This is one of the two things the engine needs
///     that <c>TestRig.Core.Abstractions.IFileSystem</c> does not offer.
/// </remarks>
public interface ILogFiles
{
    bool Exists(string path);

    long Length(string path);

    /// <summary>Reads every line with FileShare.ReadWrite | Delete.</summary>
    IReadOnlyList<string> ReadAllLines(string path);
}
