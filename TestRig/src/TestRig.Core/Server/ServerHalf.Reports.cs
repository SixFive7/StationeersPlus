using System.Globalization;
using System.Text.RegularExpressions;
using TestRig.Core.Rig;

namespace TestRig.Core.Server;

/// <summary>What game version this half carries, against the developer's install.</summary>
public sealed record ServerVersionReport(
    bool Present,
    string Version,
    string Source,
    bool Stale,
    string Remedy)
{
    public string Half => "server";
}

/// <summary>One stale payload on the server half. Reported, never fixed.</summary>
/// <param name="LoadPath">
/// Which loader the payload belongs to. Carried so a remedy cannot MOVE a payload while
/// claiming to refresh it (spec D-14, SERVER-043 and SERVER-154 fixed).
/// </param>
public sealed record ServerStalenessRow(
    string Kind,
    string Name,
    LoadPath LoadPath,
    DateTimeOffset Deployed,
    DateTimeOffset Source,
    string Remedy);

public sealed partial class ServerHalf
{
    // =====================================================================
    // status
    // =====================================================================

    /// <summary>
    /// The server's own block.
    /// </summary>
    /// <remarks>
    /// The rig-wide lock block is NOT printed here: it is printed once, above both halves,
    /// because there is one lock and printing it per half made "the first line of status" a
    /// different thing depending on which half you asked.
    /// </remarks>
    public void Status()
    {
        var hostPid = HostPid;
        var serverPid = ServerPid;
        var hostAlive = WrapperAlive;
        var serverAlive = ServerAlive;

        Say("server (dedicated):");
        Say($"  host wrapper: {(hostAlive ? $"running (PID {hostPid})" : "stopped")}");
        Say($"  process:      {(serverAlive ? $"running (PID {serverPid}, up {Uptime(serverPid)})" : "stopped")}");

        if (!_fs.FileExists(_paths.Exe))
        {
            Say($"  install:      NOT INSTALLED at {_paths.InstallDir}. Run: testrig update-game --target "
                + "server --as <id>");
        }

        if (_fs.FileExists(_paths.LogFile))
        {
            var tail = _fs.ReadTailLines(_paths.LogFile, 1);
            Say($"  last log:     {(tail.Count > 0 ? tail[0] : "")}");
        }

        if (_fs.FileExists(_paths.ControlFile))
        {
            try
            {
                Say($"  pending cmd:  {_fs.ReadAllText(_paths.ControlFile).Trim()}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Say("  pending cmd:  (present but could not be read)");
            }
        }

        if (serverAlive)
        {
            var players = ConnectedPlayers();
            var observed = PlayerCountObserved();
            Say($"  players:      {players} connected"
                + (players == 0 && !observed
                    ? "  (no connection line has ever appeared in this log, so 0 may mean the log format "
                      + "moved rather than that nobody is here)"
                    : ""));
        }

        var worlds = TreeOps.ChildDirectoryNames(_fs, _paths.SaveRoot);
        Say($"  worlds:       {worlds.Count} under data/saves/");

        if (serverAlive && !hostAlive)
        {
            Warn("The server is alive but its host wrapper is gone, so nothing can relay a console command to "
                 + "it. Terminate the orphan: testrig stop --target server --as <id>");
        }
    }

    /// <summary>
    /// How long the server process has been up.
    /// </summary>
    /// <remarks>
    /// SERVER-142 fixed, spec D-18 and D-19. The PowerShell formatted this as
    /// <c>hh\:mm\:ss</c>, so a soak run past 24 hours displayed a day-truncated figure and
    /// looked like it had just restarted; and it read the start time with no guard at all,
    /// which can throw access denied. Days are shown when there are any, and an unreadable
    /// start time is <c>?</c> rather than a stack trace over the whole status block.
    /// </remarks>
    private string Uptime(int? pid)
    {
        if (pid is null) return "?";

        var info = _processes.TryGetMatching(pid.Value, RigConstants.ServerImageName);
        if (info is null) return "?";

        var up = _clock.UtcNow - info.Value.StartTimeUtc;
        if (up < TimeSpan.Zero) up = TimeSpan.Zero;

        return up.TotalDays >= 1
            ? up.ToString(@"d\.hh\:mm\:ss", CultureInfo.InvariantCulture)
            : up.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    }

    // =====================================================================
    // version
    // =====================================================================

    /// <summary>
    /// What game version this half carries, and whether it matches the developer's install.
    /// </summary>
    /// <remarks>
    /// Half of the answer the rig-wide status owes an agent asked to "update the testrig".
    /// Nothing used to compare the two, and the only staleness the rig reported at all was
    /// per client instance, which is precisely why an agent updated the client half and left
    /// this one behind. Stale is true only when BOTH versions are known and they differ.
    /// </remarks>
    public ServerVersionReport VersionReport()
    {
        var installed = _env.InstallVersion(_paths.InstallDir);
        var source = _env.SourceVersionOrUnknown();

        var stale = installed != RigEnvironment.UnknownVersion
                    && source != RigEnvironment.UnknownVersion
                    && !string.Equals(installed, source, StringComparison.Ordinal);

        return new ServerVersionReport(
            _fs.FileExists(_paths.Exe),
            installed,
            source,
            stale,
            "testrig update-game --target server --as <id>");
    }

    // =====================================================================
    // mod staleness
    // =====================================================================

    /// <summary>
    /// Deployed payloads older than what they came from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two payload kinds, matching the two load paths: mods synced out of the developer's own
    /// folder (<c>data/mods/</c>) and this repository's built plugins
    /// (<c>install/BepInEx/plugins/</c>). Both are only ever REPORTED, never deleted or
    /// re-copied, for the same reason the state reset only reports them: the fix is a deploy
    /// or an update, and deleting a payload to signal staleness would break a rig instead of
    /// describing it (SERVER-155).
    /// </para>
    /// <para>
    /// A Workshop folder resolves through the developer's OWN modconfig (SERVER-153 fixed,
    /// spec D-09). The PowerShell stripped <c>Workshop_&lt;id&gt;</c> down to the
    /// published-file id and then looked for that id under the LOCAL mods folder, where it can
    /// never be, so the existence test failed and 93% of a seeded set was silently exempt.
    /// </para>
    /// <para>
    /// A dev-plugin found in the Chainloader folder is reported with the remedy for its OWN
    /// load path (SERVER-043, SERVER-154 fixed): the PowerShell printed a remedy that would
    /// have moved the payload rather than refreshing it.
    /// </para>
    /// </remarks>
    public IReadOnlyList<ServerStalenessRow> ModStaleness()
    {
        var rows = new List<ServerStalenessRow>();

        var userData = _env.UserDataPath();
        var sourceMods = Path.Combine(userData, "mods");
        var byFolder = SourcePathsByDestinationFolder(Path.Combine(userData, "modconfig.xml"), sourceMods);

        foreach (var dir in EnumerateOrEmpty(_paths.ModsDir))
        {
            var folder = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(folder)) continue;

            if (!byFolder.TryGetValue(folder, out var source)) continue;
            if (!_fs.DirectoryExists(source)) continue;

            var sourceTime = _env.NewestBuildTime(source);
            var deployedTime = _env.NewestBuildTime(dir);
            if (sourceTime is null || deployedTime is null || sourceTime <= deployedTime) continue;

            rows.Add(new ServerStalenessRow(
                "seeded mod", folder, LoadPath.LaunchPad, deployedTime.Value, sourceTime.Value,
                "testrig update-mods --target server --as <id>"));
        }

        foreach (var dir in EnumerateOrEmpty(_paths.PluginsDir))
        {
            var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name)) continue;

            var build = _mods.Find(name);
            if (build is null || !_fs.FileExists(build.Dll)) continue;

            var deployedTime = _env.NewestBuildTime(dir);
            if (deployedTime is null) continue;

            var sourceTime = _fs.GetLastWriteTimeUtc(build.Dll);
            if (sourceTime <= deployedTime) continue;

            var belongs = build.LoadPathOn(RigHalf.Server);
            var remedy = belongs == LoadPath.Chainloader
                ? $"testrig deploy {name} --target server --as <id>"
                : $"testrig deploy {name} --target server --as <id>   (note: this payload belongs in the "
                  + "StationeersLaunchPad load path at data/mods/Local_" + name + "/, and deploy will put it "
                  + "there and remove this copy)";

            rows.Add(new ServerStalenessRow(
                "deployed plugin", name, belongs, deployedTime.Value, sourceTime, remedy));
        }

        return rows;
    }

    private IReadOnlyList<string> EnumerateOrEmpty(string path)
    {
        if (!_fs.DirectoryExists(path)) return [];
        try
        {
            return _fs.EnumerateDirectories(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Maps each destination folder name back to the source path the developer's modconfig
    /// records for it.
    /// </summary>
    /// <remarks>
    /// The only route by which a <c>Workshop_&lt;id&gt;</c> folder can be resolved at all: the
    /// published-file id is not a folder name anywhere on disk, and only the modconfig knows
    /// where the Workshop content actually sits.
    /// </remarks>
    internal Dictionary<string, string> SourcePathsByDestinationFolder(string modConfigPath, string sourceMods)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!_fs.FileExists(modConfigPath)) return map;

        foreach (var entry in ModConfig.Read(_fs, modConfigPath))
        {
            if (string.IsNullOrEmpty(entry.Path)) continue;

            var leaf = Path.GetFileName(entry.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            if (string.Equals(entry.Kind, "Workshop", StringComparison.Ordinal))
            {
                var id = string.IsNullOrEmpty(entry.WorkshopId) ? leaf : entry.WorkshopId;
                map[$"Workshop_{id}"] = entry.Path;
            }
            else if (string.Equals(entry.Kind, "Local", StringComparison.Ordinal) && !string.IsNullOrEmpty(leaf))
            {
                map[$"Local_{leaf}"] = entry.Path;
            }
        }

        if (_fs.DirectoryExists(sourceMods))
        {
            foreach (var name in TreeOps.ChildDirectoryNames(_fs, sourceMods))
            {
                map.TryAdd($"Local_{name}", Path.Combine(sourceMods, name));
            }
        }

        return map;
    }

    // =====================================================================
    // logs
    // =====================================================================

    /// <summary>How many matching lines a grep prints before it stops and says so.</summary>
    public const int GrepMatchCap = LogFilter.MatchCap;

    /// <summary>
    /// Prints the dedicated server's log.
    /// </summary>
    /// <remarks>
    /// <c>--tail</c> and <c>--grep</c> are INDEPENDENT (SERVER-159 fixed, spec D-20): the
    /// PowerShell silently ignored the tail whenever a pattern was given, although the manual
    /// documented the two flags as independent, and streamed the whole file through a
    /// pipeline to do it. How they combine is <see cref="LogFilter"/>'s, shared with the
    /// client half so the two cannot answer differently again.
    /// </remarks>
    public void Logs(int tail = 50, string? grep = null)
    {
        if (!_fs.FileExists(_paths.LogFile))
        {
            Say($"No dedicated-server log at {_paths.LogFile}.");
            return;
        }

        Say($"== server: {_paths.LogFile}");

        if (string.IsNullOrEmpty(grep))
        {
            foreach (var line in _fs.ReadTailLines(_paths.LogFile, tail)) Say(line);
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

        var result = LogFilter.Apply(_fs.ReadLines(_paths.LogFile), pattern, tail);
        foreach (var line in result.Shown) Say(line);

        if (LogFilter.Trimmed(result) is { } note) Warn(note);
    }
}
