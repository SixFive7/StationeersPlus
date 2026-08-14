using TestRig.Core.Abstractions;
using TestRig.Core.Client;
using TestRig.Core.Rig;
using TestRig.Core.Server;
using TestRig.Core.Session;
using TestRig.Playtest.Model;

namespace TestRig.Playtest.Seams;

/// <summary>
///     The control plane, over Core's own transport.
/// </summary>
/// <remarks>
///     <para>
///     <b>A non-2xx answer is a RESULT, not a fault.</b> The PowerShell transport threw on
///     any non-2xx with the body in the message, so a 409 refusal arrived at the harness
///     wearing a transport fault's clothes: it was retried three times as a rig flake and
///     then reported under a detector that misdiagnosed it. Only a request that never
///     completed at all, a refused connection, a timeout, a socket fault, throws here.
///     </para>
///     <para>
///     Blocking once, at the boundary. The engine is synchronous by design: a check body
///     reads as a sequence of steps because that is what it is.
///     </para>
/// </remarks>
public sealed class CoreRigTransport(IControlTransport transport) : IRigTransport
{
    public TransportResponse Send(int port, string path, string? bodyJson, TimeSpan timeout)
    {
        ControlAnswer answer;
        try
        {
            answer = transport.SendAsync(port, path, bodyJson, timeout).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            throw new RigTransportException(
                $"the control plane on port {port} could not be reached for {path}: {ex.Message}", ex);
        }

        if (!answer.Answered)
        {
            throw new RigTransportException(
                $"the control plane on port {port} did not answer {path}: {answer.TransportError}");
        }

        return new TransportResponse(answer.HttpStatus, answer.Body ?? string.Empty);
    }
}

/// <summary>The rig registry, over Core's own reader.</summary>
/// <remarks>
///     A corrupt or missing <c>rig.json</c> is an empty rig here exactly as it is everywhere
///     else, and it warns through the same output sink. A check that names an instance the
///     registry does not know then fails to resolve it, with the known names listed, rather
///     than driving a port nothing is listening on.
/// </remarks>
public sealed class CoreRigRegistry(RigRegistry registry) : IRigRegistry
{
    public IReadOnlyList<RigInstanceRow> Rows() =>
    [
        .. registry.Read().Select(static e => new RigInstanceRow(
            e.InstanceName,
            e.Port,
            e.RoleOr(),
            e.RecordedRoot.Length == 0 ? null : e.RecordedRoot)),
    ];
}

/// <summary>
///     The five launcher verbs, over the session lock and the client half.
/// </summary>
/// <remarks>
///     <para>
///     <b>The owner id is a return value, not a line to scrape.</b> The PowerShell harness
///     recovered it with a regex over launcher prose, and that line has in fact never been
///     printed, so every check in every suite would have thrown inconclusive and then
///     unlocked with the id it never received, leaving the rig locked by a session that
///     could not release it. Here <see cref="LockAcquireResult.Owner"/> is a field on a
///     typed result and this adapter simply passes it on.
///     </para>
///     <para>
///     Deliberately five verbs and no more, each naming ONE instance. A rig-wide stop would
///     reach every instance on the machine including another session's live test.
///     </para>
/// </remarks>
public sealed class CoreRigLauncher : IRigLauncher
{
    private readonly SessionLockService _lock;
    private readonly ClientHalf _clients;
    private readonly CapturingOutput _recorder;
    private readonly Action? _onReclaim;
    private readonly string _desktop;

    /// <param name="recorder">
    /// The sink the lock service and the client half were built with. Acquisition prose is
    /// captured through it and becomes <c>hygiene-reset.txt</c> in the evidence bundle, so
    /// the report is exactly what the rig said rather than a paraphrase.
    /// </param>
    /// <param name="onReclaim">Tears down what a reclaimed session left running. See the lock service.</param>
    public CoreRigLauncher(
        SessionLockService sessionLock,
        ClientHalf clients,
        CapturingOutput recorder,
        Action? onReclaim = null,
        string desktop = RigConstants.DefaultDesktop)
    {
        _lock = sessionLock;
        _clients = clients;
        _recorder = recorder;
        _onReclaim = onReclaim;
        _desktop = desktop;
    }

    public LockGrant AcquireLock(string purpose, int ttlMinutes, int waitSeconds)
    {
        _recorder.Begin();

        try
        {
            var result = _lock.AcquireAsync(new AcquireOptions
            {
                Purpose = purpose,

                // Only when the caller named one. Forwarding a zero would overwrite a
                // deliberately long ceiling with a default on every re-assert, which is the
                // recorded regression this nullability exists for.
                TtlMinutes = ttlMinutes > 0 ? ttlMinutes : null,
                WaitSeconds = waitSeconds,
                Tool = "testrig",
                OnReclaim = _onReclaim,
            }).GetAwaiter().GetResult();

            return LockGrant.Granted(result.Owner, _recorder.End());
        }
        catch (RigRefusalException ex)
        {
            return LockGrant.Refused(ex.Message, _recorder.End(), RigExitCodes.For(ex.Kind));
        }
        catch (RigConfigurationException ex)
        {
            return LockGrant.Refused(ex.Message, _recorder.End(), RigExitCodes.Failed);
        }
    }

    public LauncherResult ReleaseLock(string owner) => Attempt("unlock", () =>
    {
        var release = _lock.Release(owner);
        return release.Status == ReleaseStatus.NotYours
            ? LauncherResult.Failed(release.Message, RigExitCodes.LockHeldByOther, release.Message)
            : LauncherResult.Ok(release.Message);
    });

    public LauncherResult RefreshLock(string owner) => Attempt("refresh-lock", () =>
        LauncherResult.Ok(_lock.Refresh(owner).Message));

    public LauncherResult StartInstance(string name, string owner) => Attempt("start", () =>
    {
        _recorder.Begin();
        _clients.StartAsync(_clients.Registry.Entries([name]), owner, _desktop).GetAwaiter().GetResult();
        return LauncherResult.Ok(_recorder.End());
    });

    public LauncherResult StopInstance(string name, string owner, int timeoutSeconds, bool force) =>
        Attempt("stop", () =>
        {
            _recorder.Begin();
            _clients
                .StopAsync(_clients.Registry.Entries([name]), owner, timeoutSeconds, waitSeconds: 0, saveName: null, force: force)
                .GetAwaiter().GetResult();
            return LauncherResult.Ok(_recorder.End());
        });

    /// <summary>
    ///     Turns a refusal into a result rather than letting it escape.
    /// </summary>
    /// <remarks>
    ///     The engine treats a launcher failure as its own outcome, and a teardown that threw
    ///     would skip the lock release in the caller's finally. An instance left up holds the
    ///     rig; a lock left held blocks every other agent too.
    /// </remarks>
    private static LauncherResult Attempt(string verb, Func<LauncherResult> body)
    {
        try
        {
            return body();
        }
        catch (RigRefusalException ex)
        {
            return LauncherResult.Failed($"{verb}: {ex.Message}", RigExitCodes.For(ex.Kind), ex.Message);
        }
        catch (RigConfigurationException ex)
        {
            return LauncherResult.Failed($"{verb}: {ex.Message}", RigExitCodes.Failed, ex.Message);
        }
    }
}

/// <summary>
///     Where the developer's own save folder is, and whether the answer is usable.
/// </summary>
/// <remarks>
///     <b>Defect P-06 was here, not in the comparison.</b> The harness computed this path in a
///     composition root with no tests at all; a wrong path produced two missing listings,
///     which hashed to the same sentinel, compared equal, and reported the tier-1 safety check
///     as clean. The comparison now has its own verdict for a root that was absent at both
///     ends, and the resolution has this, which refuses to produce a path it cannot derive
///     rather than returning something plausible.
/// </remarks>
public static class Tier1SaveFolder
{
    /// <summary>The folder under the user-data root that holds worlds.</summary>
    public const string Leaf = "saves";

    /// <summary>The path, or a refusal naming why it could not be derived.</summary>
    /// <exception cref="PlaytestUsageException">The user-data folder itself is unknown.</exception>
    public static string Require(RigPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var root = paths.UserSaveRoot;
        if (!string.IsNullOrEmpty(root)) return root;

        throw new PlaytestUsageException(
            "The developer's user-data folder could not be resolved, so the tier-1 save folder has no path and " +
            "the safety check would watch nothing. That is not a clean result: it is the one check whose job is " +
            "to notice the rig writing into the developer's saves, and it cannot fail if it is pointed at a path " +
            "that does not exist. Set TESTRIG_USERDATA, or fix the Documents folder lookup, and run again.");
    }

    /// <summary>
    ///     The path plus a warning when the folder is not there, for a caller that reports
    ///     rather than refuses.
    /// </summary>
    /// <returns>The resolved root, and null when it exists or a sentence when it does not.</returns>
    public static (string Root, string? Warning) Resolve(IFileSystem files, RigPaths paths)
    {
        ArgumentNullException.ThrowIfNull(files);

        var root = Require(paths);
        if (files.DirectoryExists(root)) return (root, null);

        return (root,
            $"the tier-1 save folder '{root}' does not exist, so the safety check will watch NOTHING. This is a " +
            "wrong path, not a clean result.");
    }
}
