using System.Text.Json;
using TestRig.Contracts;
using System.Text.RegularExpressions;
using TestRig.Core.Rig;

namespace TestRig.Core.Client;

/// <summary>One row of the client half's version report.</summary>
public sealed record ClientVersionRow(
    string Name,
    bool Present,
    string Version,
    string Source,
    bool Stale,
    string Remedy);

/// <summary>One stale payload on the client half. Reported, never fixed.</summary>
public sealed record ClientStalenessRow(
    string Instance,
    string Kind,
    string Name,
    DateTimeOffset Deployed,
    DateTimeOffset Source,
    string Remedy);

/// <summary>One row of <c>list</c>.</summary>
public sealed record ClientListRow(
    string InstanceName,
    int Index,
    string Role,
    string LiveRole,
    string Hosting,
    string Clients,
    int Port,
    int GamePort,
    string ClientId,
    string Username,
    string ProvisionedUtc);

public sealed partial class ClientHalf
{
    // =====================================================================
    // status
    // =====================================================================

    /// <summary>
    /// Per-instance detail.
    /// </summary>
    /// <remarks>
    /// The rig-wide lock block is NOT printed here: it is printed once, above both halves,
    /// because there is one lock and printing it per half made "the first line of status" a
    /// different thing depending on which half you asked.
    /// </remarks>
    public async Task StatusAsync(IReadOnlyList<InstanceEntry> entries, CancellationToken ct = default)
    {
        if (entries.Count == 0)
        {
            Say("clients: none provisioned. Create one: testrig create --target client1 --as <id>");
            return;
        }

        Say($"clients ({entries.Count}):");

        var runtimes = InstanceRoles.Classify(await RuntimesAsync(entries, ct).ConfigureAwait(false));

        foreach (var rt in runtimes)
        {
            var entry = rt.Entry;
            Say($"{rt.Name}:");
            Say($"  process:    {(rt.Alive ? $"running (PID {rt.ProcessId})" : "stopped")}");
            Say($"  role:       {InstanceRoles.Name(rt.Class)} [{rt.ClassSource}]");
            Say($"  ports:      {entry.Port} control plane (TCP), {rt.GamePort} game (UDP)");
            Say($"  identity:   {entry.UsernameOr(rt.Name)} ({entry.ClientIdOr()})");
            Say($"  tree:       {rt.Paths.Tree}{(_fs.DirectoryExists(rt.Paths.Tree) ? "" : "  MISSING")}"
                + $"  [{rt.Paths.RootSource}]");

            if (rt.Alive && rt.Answered)
            {
                var status = rt.Status!;
                Say($"  phase:      {status.Phase} (gameInitialized={status.GameInitialized}, "
                    + $"plugins={status.LoadedPluginCount})");

                // Status used to print no network information at all, which made "is this
                // thing hosting, and did the other instance actually arrive" unanswerable
                // from the launcher (CLIENT-286). The three unknown cases each read
                // differently on purpose: unreported, -, n/a.
                var liveRole = rt.LiveRole.Length > 0 ? rt.LiveRole : "unreported";
                var hosting = rt.Hosting is null ? "unreported" : rt.Hosting.Value.ToString();
                var hostPort = rt.HostPort > 0 ? rt.HostPort.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-";
                var joiners = rt.JoinerCount is null ? "n/a" : rt.JoinerCount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Say($"  network:    liveRole={liveRole} hosting={hosting} hostPort={hostPort} "
                    + $"connectedClients={joiners}");

                if (!string.IsNullOrEmpty(status.ServerAddress))
                {
                    Say($"  joined to:  {status.ServerAddress}:{status.ServerPort} "
                        + $"({status.NetworkRole}/{status.NetworkState})");
                }

                foreach (var client in status.ConnectedClients ?? [])
                {
                    Say($"  client:     {client.Username} ({client.ClientId})");
                }

                // The observable check on the never-take-the-foreground constraint
                // (CLIENT-289).
                Say($"  foreground: {status.Foreground?.Verdict} (ownDesktop={status.Foreground?.OwnDesktop})");
                Say($"  inputGate:  open={status.GameplayInputGateOpen}");

                // The peer identity conflict (CLIENT-291). The PowerShell read it from
                // status.instance.peers.conflictDetected, and the typed contract says plainly
                // that /status carries no such block: the flag lives on /identity, and
                // /status.instance carries only the peer PORT list. A PowerShell property
                // access on a missing member yields null and prints nothing, so this warning
                // has never once fired.
                //
                // One extra request recovers it, and only when there is a peer to conflict
                // with: on a single-instance rig the question cannot arise, so nothing is
                // asked.
                var peerPorts = status.Instance?.PeerPorts ?? [];
                if (peerPorts.Length > 1)
                {
                    var identity = await _control
                        .CallAsync(entry.Port, Endpoints.Identity, null, 5,
                            RigJsonContext.Default.IdentityResponse, ct)
                        .ConfigureAwait(false);

                    if (identity.Body is { DuplicateIdentity: true } conflict)
                    {
                        Warn($"  identity conflict: {conflict.DuplicateIdentityDetail}");
                    }
                }
            }
            else if (rt.Alive)
            {
                Say($"  control:    not answering yet ({rt.Error})");
            }
        }
    }

    // =====================================================================
    // list
    // =====================================================================

    /// <summary>
    /// Every instance, ordered by index.
    /// </summary>
    /// <remarks>
    /// Only instances whose process is ALIVE are probed, so listing a cold rig makes no HTTP
    /// call at all and still answers instantly; the live columns are <c>-</c> for the rest
    /// (CLIENT-295). A live instance whose control plane is silent shows <c>no answer</c>
    /// rather than a blank, because a blank reads as "not asked" (CLIENT-296).
    /// </remarks>
    public async Task<IReadOnlyList<ClientListRow>> ListAsync(
        IReadOnlyList<InstanceEntry> entries, CancellationToken ct = default)
    {
        if (entries.Count == 0) return [];

        var ordered = entries.OrderBy(static e => e.Index).ToList();
        var runtimes = InstanceRoles.Classify(await RuntimesAsync(ordered, ct).ConfigureAwait(false));

        var rows = new List<ClientListRow>(runtimes.Count);
        foreach (var rt in runtimes)
        {
            var live = "-";
            if (rt.Alive) live = rt.LiveRole.Length > 0 ? rt.LiveRole : "no answer";

            rows.Add(new ClientListRow(
                rt.Name,
                rt.Entry.Index,
                rt.Entry.RoleOr(),
                live,
                rt.Hosting is null ? "-" : rt.Hosting.Value.ToString(),
                rt.JoinerCount is null ? "-" : rt.JoinerCount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                rt.Entry.Port,
                rt.GamePort,
                rt.Entry.ClientIdOr(),
                rt.Entry.UsernameOr(rt.Name),
                rt.Entry.ProvisionedUtc ?? ""));
        }

        return rows;
    }

    // =====================================================================
    // logs
    // =====================================================================

    /// <summary>How many matching lines a grep prints before it stops and says so.</summary>
    /// <remarks>
    /// A modded client's <c>LogOutput.log</c> after a long session is large: a recorded run
    /// ingested over 500,000 lines. The PowerShell streamed all of them through a pipeline
    /// and printed every match (CLIENT-300).
    /// </remarks>
    public const int GrepMatchCap = LogFilter.MatchCap;

    /// <summary>
    /// Prints an instance's log.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two logs, not one (CLIENT-302 fixed). The BepInEx log is the default and is what the
    /// PowerShell read; every failure BEFORE BepInEx loads lands in
    /// <c>data/&lt;instance&gt;/logs/unity-&lt;stamp&gt;.log</c>, which no verb ever printed,
    /// so the launcher could not show a hard boot failure at all. <paramref name="unity"/>
    /// selects it, and a missing BepInEx log now names it.
    /// </para>
    /// <para>
    /// <c>--tail</c> and <c>--grep</c> are INDEPENDENT: the grep searches the WHOLE file and
    /// the tail is the window over its matches, which is what the surface has always said and
    /// what neither half used to do. See <see cref="LogFilter"/>, shared with the server half
    /// so the two cannot answer differently again.
    /// </para>
    /// </remarks>
    public void Logs(string instance, int tail = 50, string? grep = null, bool unity = false)
    {
        var paths = _layout.PathsFor(instance);
        var log = unity ? NewestUnityLog(paths) : paths.BepInExLog;

        if (string.IsNullOrEmpty(log))
        {
            Say($"== {instance} : no Unity log under {paths.LogDir}. The instance has never been started from "
                + "this rig.");
            return;
        }

        Say($"== {instance} : {log}");

        if (!_fs.FileExists(log))
        {
            Say($"No {(unity ? "Unity" : "BepInEx")} log at {log}.");
            if (!unity)
            {
                var unityLog = NewestUnityLog(paths);
                Say(string.IsNullOrEmpty(unityLog)
                    ? "Nothing has been logged for this instance at all, which means it has never started."
                    : $"A failure before BepInEx loads lands in the Unity log instead. Read it with: testrig "
                      + $"logs --target {instance} --unity   ({unityLog})");
            }
            return;
        }

        if (string.IsNullOrEmpty(grep))
        {
            foreach (var line in _fs.ReadTailLines(log, tail)) Say(line);
            return;
        }

        Regex pattern;
        try
        {
            pattern = new Regex(grep, RegexOptions.None, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            Warn($"--grep '{grep}' is not a valid regular expression ({ex.Message}).");
            return;
        }

        var result = LogFilter.Apply(_fs.ReadLines(log), pattern, tail);
        foreach (var line in result.Shown) Say(line);

        if (LogFilter.Trimmed(result) is { } note) Warn(note);
    }

    /// <summary>The newest per-run Unity log, or the empty string.</summary>
    private string NewestUnityLog(InstancePaths paths)
    {
        if (!_fs.DirectoryExists(paths.LogDir)) return "";

        string? newest = null;
        var newestAt = DateTimeOffset.MinValue;

        foreach (var file in _fs.EnumerateFiles(paths.LogDir, "unity-*.log", recurse: false))
        {
            var at = _fs.GetLastWriteTimeUtc(file);
            if (newest is null || at > newestAt)
            {
                newest = file;
                newestAt = at;
            }
        }

        return newest ?? "";
    }

    // =====================================================================
    // version and staleness
    // =====================================================================

    /// <summary>
    /// What game version each instance was built from, against what the install carries now.
    /// </summary>
    /// <remarks>
    /// The provision stamp is the record, and until the version reader was fixed that stamp
    /// held the Unity ENGINE version while the baseline held the version.ini string, so this
    /// comparison was not merely absent: it could not have worked (CLIENT-335).
    ///
    /// Stale is true only when BOTH versions are known and they differ. The literal
    /// <c>unknown</c> is the sentinel three separate comparisons test against, so it must
    /// stay a string rather than becoming null.
    /// </remarks>
    public IReadOnlyList<ClientVersionRow> VersionReport(IReadOnlyList<InstanceEntry> entries)
    {
        var source = _env.SourceVersionOrUnknown();
        var rows = new List<ClientVersionRow>(entries.Count);

        foreach (var entry in entries)
        {
            var paths = _layout.PathsFor(entry.InstanceName, entry);
            var version = RigEnvironment.UnknownVersion;

            if (_fs.FileExists(paths.Stamp))
            {
                try
                {
                    var stamp = JsonSerializer.Deserialize(
                        _fs.ReadAllText(paths.Stamp), ClientJsonContext.Default.ProvisionStamp);
                    if (!string.IsNullOrEmpty(stamp?.SourceVersion)) version = stamp!.SourceVersion;
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    version = RigEnvironment.UnknownVersion;
                }
            }

            rows.Add(new ClientVersionRow(
                entry.InstanceName,
                _fs.FileExists(paths.Exe),
                version,
                source,
                version != RigEnvironment.UnknownVersion
                && source != RigEnvironment.UnknownVersion
                && !string.Equals(version, source, StringComparison.Ordinal),
                $"testrig update-game --target {entry.InstanceName} --as <id>"));
        }

        return rows;
    }

    /// <summary>
    /// Deployed payloads older than what they came from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only ever REPORTED, never fixed here (CLIENT-339): the remedy is a deploy or an
    /// update, and deleting a payload to signal staleness would break a rig rather than
    /// describe it.
    /// </para>
    /// <para>
    /// A Workshop folder is resolved through the developer's own modconfig rather than by
    /// stripping its prefix (CLIENT-338 fixed). The PowerShell stripped
    /// <c>Workshop_&lt;id&gt;</c> down to the published-file id and then looked for that id
    /// under the LOCAL mods folder, where it can never be, so every Workshop mod was silently
    /// exempt from staleness. That is 93% of a seeded set.
    /// </para>
    /// </remarks>
    public IReadOnlyList<ClientStalenessRow> ModStaleness(IReadOnlyList<InstanceEntry> entries)
    {
        var rows = new List<ClientStalenessRow>();
        var userData = _env.UserDataPath();
        var sourceMods = Path.Combine(userData, "mods");
        var byFolder = SourcePathsByDestinationFolder(Path.Combine(userData, "modconfig.xml"), sourceMods);

        foreach (var entry in entries)
        {
            var paths = _layout.PathsFor(entry.InstanceName, entry);
            var seeded = paths.ModsDir;
            if (!_fs.DirectoryExists(seeded)) continue;

            foreach (var dir in _fs.EnumerateDirectories(seeded))
            {
                var folder = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(folder)) continue;

                var bare = StripSourcePrefix(folder);

                // A repository build wins: the remedy is a deploy, not a re-seed.
                var build = _mods.Find(bare);
                if (build is not null && _fs.FileExists(build.Dll))
                {
                    var buildTime = _fs.GetLastWriteTimeUtc(build.Dll);
                    var deployed = _env.NewestBuildTime(dir);
                    if (deployed is not null && buildTime > deployed)
                    {
                        rows.Add(new ClientStalenessRow(
                            entry.InstanceName, "deployed mod", folder, deployed.Value, buildTime,
                            $"testrig deploy {bare} --target {entry.InstanceName} --as <id>"));
                    }
                    continue;
                }

                // Otherwise the source is whatever the developer's modconfig says it is,
                // which for a Workshop entry is a path nothing could derive from the id.
                if (!byFolder.TryGetValue(folder, out var source))
                {
                    var guess = Path.Combine(sourceMods, bare);
                    if (!_fs.DirectoryExists(guess)) continue;
                    source = guess;
                }
                if (!_fs.DirectoryExists(source)) continue;

                var sourceTime = _env.NewestBuildTime(source);
                var dstTime = _env.NewestBuildTime(dir);
                if (sourceTime is null || dstTime is null || sourceTime <= dstTime) continue;

                rows.Add(new ClientStalenessRow(
                    entry.InstanceName, "seeded mod", folder, dstTime.Value, sourceTime.Value,
                    $"testrig update-mods --target {entry.InstanceName} --as <id>"));
            }
        }

        return rows;
    }

    /// <summary>
    /// Maps each destination folder name back to the SOURCE path the developer's modconfig
    /// records for it.
    /// </summary>
    /// <remarks>
    /// The only way a <c>Workshop_&lt;id&gt;</c> folder can be resolved at all: the id is not
    /// a folder name anywhere on disk, and only the modconfig knows where the Workshop
    /// content actually sits.
    /// </remarks>
    internal Dictionary<string, string> SourcePathsByDestinationFolder(string modConfigPath, string sourceMods)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!_fs.FileExists(modConfigPath)) return map;

        foreach (var entry in ModConfig.Read(_fs, modConfigPath))
        {
            if (string.IsNullOrEmpty(entry.Path)) continue;

            var leaf = Path.GetFileName(entry.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(leaf)) continue;

            if (string.Equals(entry.Kind, "Workshop", StringComparison.Ordinal))
            {
                var id = string.IsNullOrEmpty(entry.WorkshopId) ? leaf : entry.WorkshopId;
                map[$"Workshop_{id}"] = entry.Path;
            }
            else if (string.Equals(entry.Kind, "Local", StringComparison.Ordinal))
            {
                map[$"Local_{leaf}"] = entry.Path;
                // A seeded local mod also appears under the instance's own copy, whose leaf
                // matches the source leaf.
                map[leaf] = entry.Path;
            }
        }

        // Anything the developer has on disk but not in the config still resolves by name.
        if (_fs.DirectoryExists(sourceMods))
        {
            foreach (var name in TreeOps.ChildDirectoryNames(_fs, sourceMods))
            {
                map.TryAdd(name, Path.Combine(sourceMods, name));
                map.TryAdd($"Local_{name}", Path.Combine(sourceMods, name));
            }
        }

        return map;
    }

    /// <summary>Strips the <c>Workshop_</c> or <c>Local_</c> prefix a sync writes.</summary>
    internal static string StripSourcePrefix(string folder)
    {
        if (folder.StartsWith("Workshop_", StringComparison.Ordinal)) return folder["Workshop_".Length..];
        if (folder.StartsWith("Local_", StringComparison.Ordinal)) return folder["Local_".Length..];
        return folder;
    }
}
