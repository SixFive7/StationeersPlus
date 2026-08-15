using System.Text.Json;
using System.Text.RegularExpressions;
using TestRig.Core.Abstractions;

namespace TestRig.Core.Session;

/// <summary>One live client instance, as the filesystem describes it.</summary>
/// <param name="Name">From <c>instance.json</c> when readable, otherwise the directory name.</param>
/// <param name="ProcessId">The pid its <c>game.pid</c> claims, verified alive.</param>
/// <param name="Role">host / client / null when the manifest is missing or unreadable.</param>
/// <param name="Players">null means "not known"; 0 means "known to be nobody".</param>
public sealed record InstanceState(string Name, int ProcessId, string? Role, int? Players);

/// <summary>How an untracked game process relates to this rig.</summary>
public enum OrphanScope
{
    /// <summary>Its image lives inside one of THIS rig's own trees.</summary>
    Rig,

    /// <summary>
    /// A game process running out of somebody else's tree: the developer's own client, a
    /// second rig home, a dedicated server nothing here installed. Never reported.
    /// </summary>
    Foreign,

    /// <summary>
    /// Its image path could not be read. Reported anyway: silently dropping the one
    /// process nobody can identify is how an orphan stays invisible.
    /// </summary>
    Unknown,
}

/// <summary>An untracked rig game process: claimed by no pid file, so no launcher action can stop it.</summary>
public sealed record OrphanProcess(string Name, int ProcessId, string? ImagePath, OrphanScope Scope);

/// <summary>Whether the rig is in use right now. Distinct from "locked".</summary>
public sealed record BusySignal(
    bool Busy,
    string Detail,
    bool HostLive,
    IReadOnlyList<string> HostNames,
    IReadOnlyList<InstanceState> Instances,
    IReadOnlyList<OrphanProcess> Orphans,
    bool ServerLive,
    int ServerPlayers)
{
    public static BusySignal Idle() => new(false, string.Empty, false, [], [], [], false, 0);
}

/// <summary>
/// The busy probe: filesystem and process table only. No HTTP, no contact with the game.
/// </summary>
/// <remarks>
/// That constraint is deliberate, not an omission. This runs on the path of every gated
/// command whose lock is expired or past the ceiling, and a control-plane request to an
/// instance that is mid-world-load can block for seconds. A lock check that hangs is
/// worse than one that is slightly less precise.
///
/// Exactly two things make the rig busy, and the asymmetry is deliberate: a player
/// connected to the dedicated server (a server running with nobody connected is NOT
/// busy, which is what lets an abandoned server be reclaimed), and any provisioned
/// client instance's process being alive (on that half the running processes ARE the
/// test). Orphans are appended to the detail but never make the rig busy: an orphan is
/// unreachable by any launcher action, so counting it would pin the lock live with no
/// way to clear it short of a human-gated break, which is the exact failure the timers
/// exist to prevent.
/// </remarks>
public sealed partial class BusyProbe
{
    private readonly IFileSystem _fs;
    private readonly IProcessTable _processes;
    private readonly RigPaths _paths;
    private readonly Func<int, string?> _imagePathOf;

    /// <param name="imagePathOf">
    /// Resolves a pid to its executable path, for orphan scoping. Optional because
    /// <see cref="ProcessInfo"/> carries no path: without it, an untracked process with
    /// the client image cannot be told from the developer's own running client, and is
    /// reported as <see cref="OrphanScope.Unknown"/> rather than dropped.
    /// </param>
    public BusyProbe(IFileSystem fs, IProcessTable processes, RigPaths paths, Func<int, string?>? imagePathOf = null)
    {
        _fs = fs;
        _processes = processes;
        _paths = paths;
        _imagePathOf = imagePathOf ?? (static _ => null);
    }

    public BusySignal Probe()
    {
        var reasons = new List<string>();

        var serverLive = IsPidClaimAlive(_paths.ServerPidFile, [_paths.ServerImage]);
        var serverPlayers = 0;
        if (serverLive)
        {
            serverPlayers = CountPlayers(_paths.ServerLog);
            if (serverPlayers >= 1)
            {
                reasons.Add($"{serverPlayers} player(s) connected to the dedicated server");
            }
        }

        var instances = EnumerateInstances();
        var hostNames = instances.Where(static i => i.Role == "host").Select(static i => i.Name).ToArray();

        if (instances.Count >= 1)
        {
            var parts = instances.Select(static i => i.Role switch
            {
                "host" => i.Players is null
                    ? $"{i.Name}=HOST (connected clients unknown)"
                    : $"{i.Name}=HOST ({i.Players} connected)",
                null => $"{i.Name}=role unknown",
                _ => $"{i.Name}={i.Role}",
            });
            reasons.Add($"{instances.Count} client instance(s) running: {string.Join(", ", parts)}");
        }

        var orphans = FindOrphans();

        var busy = reasons.Count > 0;
        var detail = string.Join("; ", reasons);

        if (orphans.Count >= 1)
        {
            var names = string.Join(", ", orphans.Select(static o =>
                o.Scope == OrphanScope.Unknown
                    ? $"{o.Name} pid {o.ProcessId} (image path unreadable)"
                    : $"{o.Name} pid {o.ProcessId}"));
            var note = $"{orphans.Count} UNTRACKED rig game process(es), not counted as busy: {names}. "
                       + "Nothing here can stop them; kill them by pid.";
            detail = detail.Length > 0 ? $"{detail}; {note}" : note;
        }

        return new BusySignal(busy, detail, hostNames.Length > 0, hostNames, instances, orphans, serverLive, serverPlayers);
    }

    /// <summary>Net connected clients from a server-format log. Pure and side-effect free.</summary>
    /// <remarks>
    /// There is no console command to ask: <c>clients</c> existed once and is gone at
    /// 0.2.6428.27798, and <c>status</c>, which remains, writes to the in-game console rather
    /// than the Unity log file. The connection lifecycle IS logged, so the log is scanned
    /// instead, which also means this answers on a server with no plugin deployed and the
    /// control plane could not. A force-killed server leaves N ready
    /// lines with no matching disconnects and the count stays high for ever, which only
    /// reaches the busy signal once the pid check has already passed, and the pid check
    /// verifies process identity.
    /// </remarks>
    public int CountPlayers(string? path)
    {
        if (string.IsNullOrEmpty(path) || !_fs.FileExists(path)) return 0;

        IReadOnlyList<string> lines;
        try
        {
            lines = _fs.ReadLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        var ready = 0;
        var disconnected = 0;
        foreach (var line in lines)
        {
            // else-if, not two ifs: a line matching both patterns counts as a join.
            if (ReadyLine().IsMatch(line)) ready++;
            else if (DisconnectLine().IsMatch(line)) disconnected++;
        }

        var net = ready - disconnected;
        return net < 0 ? 0 : net;
    }

    /// <summary>One record per LIVE client instance. Dead instances are skipped entirely.</summary>
    public IReadOnlyList<InstanceState> EnumerateInstances()
    {
        var result = new List<InstanceState>();
        if (!_fs.DirectoryExists(_paths.ClientDataDir)) return result;

        IReadOnlyList<string> pidFiles;
        try
        {
            pidFiles = _fs.EnumerateFiles(_paths.ClientDataDir, "game.pid", recurse: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        foreach (var pidFile in pidFiles.OrderBy(static p => p, StringComparer.OrdinalIgnoreCase))
        {
            var claimed = ReadPid(pidFile);
            if (claimed is null) continue;
            if (!IsPidClaimAlive(pidFile, [_paths.ClientImage])) continue;

            var dir = Path.GetDirectoryName(pidFile);
            if (string.IsNullOrEmpty(dir)) continue;

            var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string? role = null;

            var manifest = Path.Combine(dir, "instance.json");
            if (_fs.FileExists(manifest))
            {
                try
                {
                    using var doc = JsonDocument.Parse(_fs.ReadAllText(manifest));
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        if (doc.RootElement.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String)
                        {
                            role = r.GetString();
                        }
                        if (doc.RootElement.TryGetProperty("instanceName", out var n)
                            && n.ValueKind == JsonValueKind.String
                            && !string.IsNullOrEmpty(n.GetString()))
                        {
                            name = n.GetString()!;
                        }
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    // An unreadable manifest degrades to "role unknown". It still counts as a
                    // live instance, so liveness never depends on the field being there.
                }
            }

            int? players = null;
            if (role == "host")
            {
                var log = NewestInstanceLog(dir);
                if (log is not null) players = CountPlayers(log);
            }

            result.Add(new InstanceState(name, claimed.Value, role, players));
        }

        return result;
    }

    /// <summary>Newest <c>unity-*.log</c> under the instance's log directory, or null.</summary>
    /// <remarks>
    /// Each start writes a fresh <c>unity-&lt;stamp&gt;.log</c>, so the newest file is the
    /// current run. It must be found before players are counted, or a host that has not
    /// written one yet would read as an empty session rather than as unknown.
    /// </remarks>
    public string? NewestInstanceLog(string instanceDir)
    {
        var logs = Path.Combine(instanceDir, "logs");
        if (!_fs.DirectoryExists(logs)) return null;

        string? newest = null;
        var newestAt = DateTimeOffset.MinValue;
        try
        {
            foreach (var file in _fs.EnumerateFiles(logs, "unity-*.log", recurse: false))
            {
                var at = _fs.GetLastWriteTimeUtc(file);
                if (newest is null || at > newestAt)
                {
                    newest = file;
                    newestAt = at;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return newest;
    }

    /// <summary>Every pid the rig claims, alive or not. "Tracked" means "claimed by a pid file".</summary>
    public IReadOnlySet<int> TrackedProcessIds()
    {
        var tracked = new HashSet<int>();
        var serverPid = ReadPid(_paths.ServerPidFile);
        if (serverPid is not null) tracked.Add(serverPid.Value);

        if (_fs.DirectoryExists(_paths.ClientDataDir))
        {
            try
            {
                foreach (var pidFile in _fs.EnumerateFiles(_paths.ClientDataDir, "game.pid", recurse: true))
                {
                    var claimed = ReadPid(pidFile);
                    if (claimed is not null) tracked.Add(claimed.Value);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A pid file we cannot read is a pid we cannot claim. The consequence is an
                // extra orphan line, never a missed stop.
            }
        }

        return tracked;
    }

    /// <summary>Untracked game processes that belong to THIS rig.</summary>
    /// <remarks>
    /// <para>
    /// The image path decides it, and nothing else does. A process is ours when its
    /// executable lives under one of this rig's own trees (the dedicated-server install, or
    /// any recorded instance root), unknown when its path cannot be read at all, and
    /// somebody else's otherwise.
    /// </para>
    /// <para>
    /// There used to be one exception: an untracked dedicated server was scoped to this rig
    /// WHEREVER it lived, on the reasoning that the developer does not run one outside the
    /// rig. That is an assumption about a person, and the rig cannot check it. What it did
    /// check was every other dedicated server on the machine, which it then reported as an
    /// orphan, and a reported orphan blocks every state reset with no remedy this rig can
    /// offer: it cannot stop a process it did not start. A second clone of this repository,
    /// or a server the developer runs for anybody else, pinned the first rig permanently.
    /// </para>
    /// <para>
    /// Measured 2026-08-15: an orphaned <c>rocketstation_DedicatedServer</c> that no pid file
    /// claimed, running out of a folder no rig owns, made <c>reset --dry-run</c> exit 6
    /// against a rig home in a temp folder that had never held a server at all. It also
    /// failed the CLI suite, which is how it was found. The gate's own justification is that
    /// an orphan "writes to exactly the folders being deleted"; a server outside this rig's
    /// install writes to its own, so the refusal was not protecting anything.
    /// </para>
    /// <para>
    /// The rig's own server can only ever run from <c>DediInstall</c> (<c>ServerPaths.Exe</c>
    /// is built from it and <c>update-game</c> installs into it), so the path rule already
    /// covers every server this rig could have started, and one whose path cannot be read is
    /// still reported as <see cref="OrphanScope.Unknown"/> rather than dropped.
    /// </para>
    /// </remarks>
    public IReadOnlyList<OrphanProcess> FindOrphans()
    {
        var result = new List<OrphanProcess>();
        var names = new List<string>();
        if (!string.IsNullOrEmpty(_paths.ServerImage)) names.Add(_paths.ServerImage);
        if (!string.IsNullOrEmpty(_paths.ClientImage) && !names.Contains(_paths.ClientImage, StringComparer.OrdinalIgnoreCase))
        {
            names.Add(_paths.ClientImage);
        }
        if (names.Count == 0) return result;

        var tracked = TrackedProcessIds();
        var roots = new List<string>();
        if (!string.IsNullOrEmpty(_paths.DediInstall)) roots.Add(_paths.DediInstall);

        // Every recorded root, not just the primary one. A rig split across two roots had
        // every process under the second one scoped Foreign and therefore never reported
        // (CLIENT-007).
        foreach (var root in _paths.AllInstanceRoots)
        {
            if (!string.IsNullOrEmpty(root)) roots.Add(root);
        }

        foreach (var image in names)
        {
            foreach (var proc in _processes.FindByImage(image))
            {
                if (tracked.Contains(proc.Pid)) continue;

                var path = _imagePathOf(proc.Pid);
                var scope = OrphanScope.Foreign;
                if (string.IsNullOrEmpty(path))
                {
                    scope = OrphanScope.Unknown;
                }
                else
                {
                    foreach (var root in roots)
                    {
                        if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        {
                            scope = OrphanScope.Rig;
                            break;
                        }
                    }
                }

                if (scope == OrphanScope.Foreign) continue;
                result.Add(new OrphanProcess(proc.ImageName, proc.Pid, path, scope));
            }
        }

        return result;
    }

    /// <summary>The pid a pid file claims, or null when it is missing, empty or not a number.</summary>
    public int? ReadPid(string? pidFile)
    {
        if (string.IsNullOrEmpty(pidFile) || !_fs.FileExists(pidFile)) return null;
        string text;
        try
        {
            text = _fs.ReadAllText(pidFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        text = text.Trim();
        if (text.Length == 0) return null;
        return int.TryParse(text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var pid) ? pid : null;
    }

    /// <summary>
    /// Whether a pid file names a process that is genuinely still the one it claimed.
    /// </summary>
    /// <remarks>
    /// The image-name check is load bearing: Windows recycles process ids, and the rig's
    /// pid files genuinely go stale because no cleanup runs on a force-kill or a reboot.
    /// Trusting the bare number would report the rig busy for ever, and the
    /// expired-but-busy self-renew would then keep a dead session's lock alive with no
    /// timer able to reclaim it.
    ///
    /// The image check does not close reuse by the SAME image, which the PowerShell rig
    /// left open (spec 02-lock D.4, defect D5). This closes it with the start time: a
    /// process that started well AFTER the pid file was written cannot be the process the
    /// file was written for. The margin is generous and the failure direction is safe:
    /// inside it, the answer stays "alive", which keeps the rig busy and keeps the pid
    /// file, rather than deleting a live instance's claim.
    /// </remarks>
    public bool IsPidClaimAlive(string pidFile, IReadOnlyList<string> imageNames)
    {
        var claimed = ReadPid(pidFile);
        if (claimed is null) return false;

        ProcessInfo? match = null;
        foreach (var image in imageNames)
        {
            match = string.IsNullOrEmpty(image)
                ? _processes.TryGet(claimed.Value)
                : _processes.TryGetMatching(claimed.Value, image);
            if (match is not null) break;
        }
        if (match is null) return false;

        try
        {
            var written = _fs.GetLastWriteTimeUtc(pidFile);
            if (match.Value.StartTimeUtc - written > PidReuseMargin) return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No timestamp to compare. Fall back to the image check alone, which is what
            // the PowerShell rig did on every call.
        }

        return true;
    }

    /// <summary>
    /// How much later than its pid file a process may have started and still be believed.
    /// </summary>
    /// <remarks>
    /// Covers a launcher that writes the claim before the process is up, plus filesystem
    /// timestamp granularity. A recycled pid is days or hours later, never seconds.
    /// </remarks>
    public static readonly TimeSpan PidReuseMargin = TimeSpan.FromMinutes(5);

    [GeneratedRegex(@"Client .*\) is ready", RegexOptions.CultureInvariant)]
    private static partial Regex ReadyLine();

    [GeneratedRegex(@"Client disconnected:", RegexOptions.CultureInvariant)]
    private static partial Regex DisconnectLine();
}
