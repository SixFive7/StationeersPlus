using TestRig.Contracts;
using TestRig.Core.Abstractions;
using TestRig.Core.Rig;
using TestRig.Core.Session;

namespace TestRig.Core.Server;

/// <summary>
/// The dedicated-server half: one headless install, driven through its own control plane.
/// </summary>
/// <remarks>
/// <para>
/// It has NO player character at all. It runs with <c>IsBatchMode</c> true, so
/// <c>CreateCharacterAndTakeControl</c> never runs and <c>LocalClientId</c> stays 0. Use it
/// when the test wants a server that is not a player: soak runs, probes, save-edit round
/// trips. When the test needs a host who plays, that is a listen host on the client half,
/// and the two are not interchangeable.
/// </para>
/// <para>
/// A wrapper owns the process and holds its redirected stdin, and that stdin reaches
/// nothing: the console command a headless server acts on arrives from inside the process,
/// through the merged plugin's <c>/console/exec</c> on <see cref="ControlPort"/>. Both verbs
/// that have to make the game DO something (<c>save</c> and the graceful half of
/// <c>stop</c>) go that way, and the console's own answers come back with them. What still
/// comes from the log or the filesystem is what the log and the filesystem are better
/// witnesses for: a save that lands frames later, and the connection lifecycle.
/// </para>
/// </remarks>
public sealed partial class ServerHalf
{
    private readonly IFileSystem _fs;
    private readonly IProcessTable _processes;
    private readonly IClock _clock;
    private readonly ISleeper _sleeper;
    private readonly IOutput _output;
    private readonly ServerPaths _paths;
    private readonly RigPaths _rigPaths;
    private readonly RigEnvironment _env;
    private readonly ModBuilds _mods;
    private readonly SessionLockService _lock;
    private readonly Client.ControlPlane _control;
    private readonly IServerProcessLauncher _launcher;
    private readonly ISteamCmdRunner _steamcmd;
    private readonly IFileDownloader _downloader;
    private readonly IArchiveExtractor _extractor;
    private readonly string _launcherPath;

    /// <param name="launcherPath">
    /// The executable the host wrapper re-invokes in host mode. It is passed in rather than
    /// discovered, because the wrapper has to name a REAL entry point: in the PowerShell rig
    /// a dot-sourced library had no param block, so <c>pwsh -File</c> against it ran nothing
    /// and the server never started (SERVER-056).
    /// </param>
    public ServerHalf(
        IFileSystem fs,
        IProcessTable processes,
        IClock clock,
        ISleeper sleeper,
        IOutput output,
        RigPaths rigPaths,
        RigEnvironment env,
        ModBuilds mods,
        SessionLockService sessionLock,
        Client.ControlPlane control,
        IServerProcessLauncher launcher,
        ISteamCmdRunner steamcmd,
        IFileDownloader downloader,
        IArchiveExtractor extractor,
        string launcherPath)
    {
        _fs = fs;
        _processes = processes;
        _clock = clock;
        _sleeper = sleeper;
        _output = output;
        _rigPaths = rigPaths;
        _paths = new ServerPaths(rigPaths);
        _env = env;
        _mods = mods;
        _lock = sessionLock;
        _control = control;
        _launcher = launcher;
        _steamcmd = steamcmd;
        _downloader = downloader;
        _extractor = extractor;
        _launcherPath = launcherPath;
    }

    /// <summary>Every path this half owns, so a test can assert on the layout.</summary>
    public ServerPaths Paths => _paths;

    /// <summary>
    /// The port the merged plugin listens on inside the server process.
    /// </summary>
    /// <remarks>
    /// This half HAS a control plane now, and that is the single fact behind three fixed
    /// defects. One plugin loads into both halves, so <c>/status</c>, <c>/ping</c> and every
    /// other route answer here exactly as they do on a client instance, on a port of their own.
    /// Until the merge there were two plugins and only the client one had a listener, which is
    /// why <c>call --target server</c> refused, why readiness had to be inferred from a file
    /// disappearing, and why a rejected world name could not be seen from outside at all.
    /// </remarks>
    public static int ControlPort => RigConstants.ServerControlPort;

    /// <summary>The server's own <c>/status</c>, or null with the reason it did not answer.</summary>
    public Task<(StatusResponse? Status, string Error)> StatusAsync(
        int timeoutSeconds = 5, CancellationToken ct = default) =>
        _control.StatusAsync(ControlPort, timeoutSeconds, ct);

    private void Say(string text) => _output.Line(OutputLevel.Info, text);

    private void Warn(string text) => _output.Line(OutputLevel.Warning, text);

    /// <summary>The gate for every mutating action except <c>stop</c>, which has its own.</summary>
    private void AssertGate(string action, string? callerId) =>
        _lock.AssertHeld(action, callerId, "testrig");

    // ---- liveness ----------------------------------------------------------

    /// <summary>Whether the game process this half tracks is alive.</summary>
    public bool ServerAlive => PidFiles.ServerAlive(_fs, _processes, _paths.PidFile);

    /// <summary>Whether the host wrapper is alive.</summary>
    public bool WrapperAlive => PidFiles.WrapperAlive(_fs, _processes, _paths.HostPidFile);

    /// <summary>The pid the server's file claims, alive or not.</summary>
    public int? ServerPid => PidFiles.Read(_fs, _paths.PidFile);

    /// <summary>The pid the wrapper's file claims, alive or not.</summary>
    public int? HostPid => PidFiles.Read(_fs, _paths.HostPidFile);

    // ---- connected players -------------------------------------------------

    /// <summary>
    /// Connected clients on the live server, or 0 when it is not running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 0 when the process is not alive, deliberately: it favours freeing the rig (SERVER-005).
    /// </para>
    /// <para>
    /// Scanned out of the log rather than asked of the console. The connection lifecycle IS
    /// logged (SERVER-006), and this count gates the session lock, so it must answer for a
    /// server with no plugin deployed as well as one with. There is no <c>clients</c> console
    /// command to ask in any case: it existed once and does not at 0.2.6428.27798, while
    /// <c>status</c> still does and writes to the in-game console rather than the Unity
    /// <c>-logFile</c>, so scraping it was never an option either.
    /// </para>
    /// <para>
    /// <b>Spec D-06: the two patterns behind this are unverified against any current build.</b>
    /// Neither appears in the live log that was inspected, and this count gates the session
    /// lock's busy state, so a wrong pattern makes a busy server look reclaimable mid-test.
    /// The counting itself is deliberately the session layer's one implementation rather than
    /// a second copy. What is added here is <see cref="PlayerCountObserved"/>: whether the log
    /// carries ANY connection-lifecycle line at all, so a pattern that has stopped matching
    /// reads as "never seen one" rather than silently as zero.
    /// </para>
    /// </remarks>
    public int ConnectedPlayers()
    {
        if (!ServerAlive) return 0;

        // Orphan scoping wired even though counting players does not use it. Constructing a
        // probe without it ANYWHERE is how the unwired default spreads: the next reader copies
        // the shortest construction they can find, and the one that matters answers Unknown
        // for the developer's own client and blocks every reset.
        return Client.ProcessImagePaths.Probe(_fs, _processes, _rigPaths).CountPlayers(_paths.LogFile);
    }

    /// <summary>
    /// Whether the log has ever carried a connection-lifecycle line.
    /// </summary>
    /// <remarks>
    /// False on a server that has genuinely never had a client, and false on a server whose
    /// log format has moved on. Those two cases look identical from a count alone, which is
    /// the whole reason this exists.
    /// </remarks>
    public bool PlayerCountObserved()
    {
        if (!_fs.FileExists(_paths.LogFile)) return false;

        try
        {
            foreach (var line in _fs.ReadLines(_paths.LogFile))
            {
                if (line.Contains(") is ready", StringComparison.Ordinal)
                    || line.Contains("Client disconnected:", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    // ---- the stdin control channel -----------------------------------------

    /// <summary>How long a queued command waits for the previous one to be consumed.</summary>
    public const int ControlFreeWaitSeconds = 5;

    /// <summary>
    /// Queues one line on the server's stdin, through the wrapper's control file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written durably: temp file in the same directory, flushed, then an atomic rename onto
    /// the target. The wrapper therefore never reads a partial write, and its 50 ms settle
    /// is defensive rather than load-bearing (SERVER-092).
    /// </para>
    /// <para>
    /// <b>Spec D-04, resolved: this channel does not reach the game, and the reason is not a
    /// bug in the relay.</b> The launcher's whole half of it works (the wrapper consumed a
    /// control file in 0.26 s, measured), and the game acts on nothing that arrives here,
    /// because no reader on a headless server is attached to a redirected pipe. The mechanism
    /// is on <c>SubmitSaveAsync</c> in ServerHalf.Lifecycle.cs, and the game internals are in
    /// <c>Research/GameSystems/DedicatedServerSettings.md</c>.
    /// </para>
    /// <para>
    /// It survives as a FALLBACK and only as one. <c>save</c> and <c>stop</c> both go through
    /// <see cref="Endpoints.ConsoleExec"/> first and reach here only on a server whose plugin
    /// is not deployed or did not load, where there is nothing else to try. The one caller
    /// that is not a fallback is <see cref="SendAsync"/>, the <c>send</c> verb, which is a
    /// direct request for this channel and says what it is worth every time it is used.
    /// </para>
    /// </remarks>
    public async Task SendCommandAsync(string command, CancellationToken ct = default)
    {
        if (!ServerAlive) throw new RigRefusalException(RigRefusalKind.Refused, "Server is not running.");

        if (!WrapperAlive)
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                "Host wrapper is not running; cannot relay commands. Clean up the orphaned server with: "
                + "testrig stop --target server --as <id>");
        }

        var deadline = _clock.UtcNow.AddSeconds(ControlFreeWaitSeconds);
        while (_fs.FileExists(_paths.ControlFile) && _clock.UtcNow < deadline)
        {
            await _sleeper.DelayAsync(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
        }

        if (_fs.FileExists(_paths.ControlFile))
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                $"Previous control command still pending after {ControlFreeWaitSeconds}s.");
        }

        // No trailing newline: the wrapper's reader trims, so one would be harmless, and its
        // absence keeps the file byte-identical to what was asked for.
        _fs.WriteAllTextDurable(_paths.ControlFile, command);
    }

    /// <summary>
    /// The dedicated server's OTHER control channel: one line into its stdin.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fire and forget, and now measured to be fire and nothing. It is kept as a verb because
    /// it is the only way to exercise the wrapper's relay end to end, and because removing a
    /// verb an agent may have in a script is a worse failure than a verb that says plainly
    /// what it does; but every use warns, because a channel that looks alive and is not is
    /// the thing that cost this rig months of misattributed silence.
    /// </para>
    /// <para>
    /// <c>call --target server --path /console/exec</c> is what this verb was for: it reaches
    /// the same console from inside the process, and it answers with the lines the command
    /// printed instead of with nothing.
    /// </para>
    /// </remarks>
    public async Task SendAsync(string command, string? callerId = null, CancellationToken ct = default)
    {
        AssertGate("send", callerId);
        await SendCommandAsync(command, ct).ConfigureAwait(false);
        Say($"[Send] Queued on the server's stdin: {command}");
        Warn("[Send] That stdin does not reach the game. No reader on a headless server is attached to a "
             + "redirected pipe, and a command written there was measured on 0.2.6428.27798 to produce a "
             + "zero-byte log delta. Use the console the server does read: testrig call --target server --path "
             + Endpoints.ConsoleExec + " --body '{\"command\":\"" + command + "\"}'");
    }

    // ---- the HTTP control plane --------------------------------------------

    /// <summary>
    /// One HTTP request to the server's own control plane, answer parsed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same shape as the client half's <c>call</c>, against the same plugin, because it IS
    /// the same plugin: the merged build loads into the dedicated server and listens on
    /// <see cref="ControlPort"/>. The verb used to refuse here with text describing the
    /// pre-merge world ("the dedicated server has no such plane") while the plane was up and
    /// answering, which is worse than no refusal at all: it teaches something false at the
    /// exact moment a caller is forming a model of the rig.
    /// </para>
    /// <para>
    /// Routes the server genuinely cannot serve still refuse, and the refusal comes from the
    /// PLUGIN, which knows which process it is in. It has no player character (batch mode, so
    /// <c>CreateCharacterAndTakeControl</c> never runs and <c>LocalClientId</c> stays 0), so
    /// every <c>/player</c>, <c>/inventory</c>, <c>/cursor</c> and <c>/input</c> path answers
    /// with what it needs, why this host cannot provide it, and a command that works.
    /// </para>
    /// </remarks>
    public async Task CallAsync(
        string path,
        string? body = null,
        string? callerId = null,
        int callTimeoutSeconds = 0,
        CancellationToken ct = default)
    {
        AssertGate("call", callerId);

        if (!ServerAlive)
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                "[Call] The dedicated server is not running, so its control plane is not listening. Start it "
                + "first: testrig start --target server --as <id> --new <Map>");
        }

        var timeout = _control.TimeoutSecondsFor(path, body, callTimeoutSeconds);

        if (!Endpoints.Exists(path))
        {
            var suggestions = Endpoints.Suggest(path);
            var hint = suggestions.Count > 0
                ? $" Did you mean one of: {string.Join(", ", suggestions)}?"
                : $" GET {Endpoints.Help} on the running server lists every path.";
            Warn($"[Call] '{path}' is not a path the plugin answers.{hint}");
        }

        Say($"[Call] server {path} on 127.0.0.1:{ControlPort} (up to {timeout}s)");

        var answer = await _control.RawAsync(ControlPort, path, body, timeout, ct).ConfigureAwait(false);

        if (!answer.Answered)
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                $"[Call] Nothing answered on 127.0.0.1:{ControlPort}: {answer.TransportError}. The server process "
                + "is up, so the plugin is either not deployed or did not load. Deploy it and restart the server: "
                + "testrig deploy TestRig --target server --as <id>");
        }

        var ok = Client.ClientHalf.CallSucceeded(answer);

        if (!ok)
        {
            Warn($"[server] {Client.ControlPlane.ErrorDetail(answer)}");
            _output.Value("error", Client.ControlPlane.ErrorDetail(answer));
        }

        if (!string.IsNullOrWhiteSpace(answer.Body))
        {
            Say(Client.JsonText.Pretty(answer.Body));
            if (ok) _output.Value("response", answer.Body);
        }

        _output.Value("callFailed", ok ? 0 : 1);

        if (!ok)
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                "[Call] The dedicated server's control plane refused or failed the request. Its answer is above; "
                + "read it rather than the status code, because a refusal and a lookup failure both arrive as "
                + "ok=false.");
        }
    }

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
}
