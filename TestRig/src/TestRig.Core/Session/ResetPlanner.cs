using TestRig.Core.Abstractions;

namespace TestRig.Core.Session;

/// <summary>
/// Builds the restore plan. Pure data: producing it moves not one byte.
/// </summary>
/// <remarks>
/// The action order below is also the execution order, because the executor runs the list
/// in order. That makes it a behavioural contract, not an artefact: nothing matters more
/// than ReapplySavePathOverride coming after the config write that wipes it.
///
/// Only targets that actually exist become actions, so printed counts are honest. The one
/// exception is ReapplySavePathOverride, planned for every instance with a BepInEx
/// directory whether or not a config write happened, because re-writing it is idempotent
/// and the cost of skipping it once is a world in the developer's tier-1 folder.
/// </remarks>
public sealed class ResetPlanner
{
    private readonly IFileSystem _fs;
    private readonly IClock _clock;
    private readonly RigPaths _paths;
    private readonly MutableSurface _surface;
    private readonly BaselineStore _baseline;
    private readonly WorldScanner _worlds;
    private readonly DirtyMarker _marker;
    private readonly BusyProbe _busy;
    private readonly SessionStateStore _state;

    public ResetPlanner(
        IFileSystem fs,
        IClock clock,
        RigPaths paths,
        MutableSurface surface,
        BaselineStore baseline,
        WorldScanner worlds,
        DirtyMarker marker,
        BusyProbe busy,
        SessionStateStore state)
    {
        _fs = fs;
        _clock = clock;
        _paths = paths;
        _surface = surface;
        _baseline = baseline;
        _worlds = worlds;
        _marker = marker;
        _busy = busy;
        _state = state;
    }

    /// <summary>
    /// Whether the rig will tolerate a state change right now.
    /// </summary>
    /// <remarks>
    /// Three ORed conditions, and two of them are deliberately stricter than the lock's
    /// idea of busy. A dedicated server with nobody connected is not lock-busy (which is
    /// what lets an abandoned server be reclaimed) but it does block a reset, because it
    /// writes to the folders being deleted. An orphan is deliberately never lock-busy (an
    /// unreachable process would pin the rig permanently) but it does block a reset, for
    /// the same reason. Both remedies are bounded and in the operator's hands.
    /// </remarks>
    public ResetGate CheckGate()
    {
        var busy = _busy.Probe();
        var why = new List<string>();

        if (busy.Busy) why.Add(busy.Detail);
        if (busy.ServerLive) why.Add("the dedicated server process is alive");
        if (busy.Orphans.Count >= 1)
        {
            why.Add("untracked rig game process(es) are running: "
                    + string.Join(", ", busy.Orphans.Select(static o => $"{o.Name} pid {o.ProcessId}")));
        }

        return new ResetGate(why.Count == 0, string.Join("; ", why), busy);
    }

    /// <summary>The source install, only when it looks like one.</summary>
    /// <remarks>
    /// A path that does not carry <c>BepInEx\config</c> resolves to null rather than a
    /// guess, so a missing install is reported loudly instead of producing a copy from
    /// nowhere.
    /// </remarks>
    public string? ResolveSourceInstall()
    {
        var install = _paths.SourceInstall;
        if (string.IsNullOrEmpty(install)) return null;
        return _fs.DirectoryExists(Path.Combine(install, "BepInEx", "config")) ? install : null;
    }

    public ResetPlan Build(bool keepState = false)
    {
        var actions = new List<ResetAction>();
        var reports = new List<ResetReport>();

        var source = ResolveSourceInstall();
        var lastReset = _state.ReadLastResetUtc();

        // One surface enumeration, reused. PowerShell enumerated it twice per plan (once
        // here and once inside the staleness check) and hashed every payload file both
        // times, on every lock and every unlock.
        var surfaceAll = _surface.Enumerate();
        var baseline = _baseline.Read();
        var staleness = _baseline.CheckStale(baseline, surfaceAll);

        if (!staleness.Present)
        {
            reports.Add(new ResetReport("rig", null, ResetReportKind.BaselineAbsent,
                "no baseline has ever been captured, so client configs fall back to a copy from the source install "
                + "and server configs are only reported. Capture one: testrig capture-baseline --as <id>", Warn: true));
        }
        else if (staleness.Stale)
        {
            reports.Add(new ResetReport("rig", null, ResetReportKind.BaselineStale,
                "the baseline is STALE and configs are still restored from it: " + string.Join("; ", staleness.Reasons),
                Warn: true));
        }
        else
        {
            reports.Add(new ResetReport("rig", null, ResetReportKind.BaselineUsed,
                $"restoring to the baseline captured {baseline!.CapturedUtc} (game {baseline.GameVersion}, "
                + $"{baseline.Files.Count} entries)"));
        }

        var serverWorlds = _marker.ReadSessionWorlds(WorldScope.Server);
        var clientWorlds = _marker.ReadSessionWorlds(WorldScope.Client);

        var rootMap = _surface.InstanceRootMap();
        var instances = _surface.InstanceNames();

        foreach (var name in instances)
        {
            PlanInstance(name, rootMap, baseline, surfaceAll, source, clientWorlds, actions, reports);
        }

        PlanServer(baseline, surfaceAll, lastReset, serverWorlds, actions, reports);

        var worldDeletes = actions.Count(static a => a.Kind == ResetActionKind.DeleteTree);
        var ceilingExceeded = worldDeletes > ResetPlan.BulkDeleteCeiling;
        if (ceilingExceeded)
        {
            reports.Add(new ResetReport("rig", null, ResetReportKind.BulkWorldDeleteRefused, BulkDeleteDetail(actions), Warn: true));
        }

        return new ResetPlan(
            RigTime.Stamp(_clock.UtcNow),
            _paths.RigHome,
            source,
            instances,
            actions,
            reports,
            keepState,
            lastReset,
            staleness,
            serverWorlds,
            clientWorlds,
            worldDeletes,
            ceilingExceeded);
    }

    /// <summary>The bulk-delete refusal, naming every world it was about to remove.</summary>
    internal static string BulkDeleteDetail(IReadOnlyList<ResetAction> actions)
    {
        var names = actions
            .Where(static a => a.Kind == ResetActionKind.DeleteTree)
            .Select(static a => Path.GetFileName(a.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            .ToArray();

        return $"REFUSING to delete {names.Length} worlds in one restore, which is past the ceiling of "
               + $"{ResetPlan.BulkDeleteCeiling}. A session that legitimately created that many is vanishingly rare; a "
               + "world set that reads as empty because its enumeration failed produces exactly this plan, and that "
               + "defect once put 25 real worlds and 185 MB on the list with no warning at all. Nothing is deleted. "
               + "The worlds at risk are: " + string.Join(", ", names)
               + ". If every one of those really is this session's, re-run with --allow-bulk-world-delete.";
    }

    private void PlanInstance(
        string name,
        IReadOnlyDictionary<string, string> rootMap,
        Baseline? baseline,
        IReadOnlyList<SurfaceRecord> surfaceAll,
        string? source,
        SessionWorldSnapshot clientWorlds,
        List<ResetAction> actions,
        List<ResetReport> reports)
    {
        var data = _paths.InstanceDataDir(name);
        var treeInfo = _surface.TreeFor(name, rootMap);
        var bepinex = Path.Combine(treeInfo.Path, "BepInEx");
        var userData = _paths.InstanceUserData(name);
        var role = _surface.RoleOf(name);

        // setting.xml carries StartLocalHost. An instance that silently comes up hosting
        // when a test believes it is a joiner is exactly the failure this prevents.
        var settings = Path.Combine(data, "setting.xml");
        if (_fs.FileExists(settings))
        {
            actions.Add(new ResetAction("client", name, ResetActionKind.DeleteFile, settings,
                "setting.xml", "carries StartLocalHost; start rewrites what it needs"));
        }

        PlanClientWorlds(name, clientWorlds, actions, reports);

        var logs = _paths.InstanceLogDir(name);
        var logCount = RigFiles.CountEntries(_fs, logs);
        if (logCount > 0)
        {
            actions.Add(new ResetAction("client", name, ResetActionKind.DeleteContents, logs,
                $"{logCount} log(s)", "never rotated; a grep matches a dead run", Items: logCount));
        }

        var imgui = Path.Combine(data, "imgui.ini");
        if (_fs.FileExists(imgui))
        {
            actions.Add(new ResetAction("client", name, ResetActionKind.DeleteFile, imgui,
                "imgui.ini", "panel layout persists and reframes screenshots"));
        }

        var pidFile = _paths.InstancePidFile(name);
        if (_fs.FileExists(pidFile))
        {
            if (_busy.IsPidClaimAlive(pidFile, [_paths.ClientImage]))
            {
                reports.Add(new ResetReport("client", name, ResetReportKind.PreservedLivePid,
                    $"game.pid kept: process {_busy.ReadPid(pidFile)} is a live game client"));
            }
            else
            {
                actions.Add(new ResetAction("client", name, ResetActionKind.DeleteFile, pidFile,
                    "stale game.pid", "no live game process claims it"));
            }
        }

        if (_fs.DirectoryExists(bepinex))
        {
            var cfgDir = Path.Combine(bepinex, "config");
            var copied = false;
            var prefix = $"client/{name}/bepinex-config/";
            var covered = baseline is not null
                          && baseline.Files.Keys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

            if (covered)
            {
                var liveCfg = surfaceAll
                    .Where(r => r.Class == SurfaceClass.Config && r.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                foreach (var action in _baseline.ConfigActions(baseline!, prefix, cfgDir, "client", name, liveCfg))
                {
                    actions.Add(action);
                    // Only a write that actually touches the StationeersLaunchPad config can
                    // wipe SavePathOverride, and only then is a failed re-apply this reset's
                    // fault. Marking every restore as a copy would make an unrelated failure
                    // fatal for no reason.
                    if (string.Equals(Path.GetFileName(action.Path), SavePathOverride.ConfigLeaf, StringComparison.OrdinalIgnoreCase))
                    {
                        copied = true;
                    }
                }

                var mcPrefix = $"client/{name}/modconfig.xml";
                var liveMc = surfaceAll.Where(r => string.Equals(r.Key, mcPrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
                actions.AddRange(_baseline.ConfigActions(baseline!, mcPrefix, userData, "client", name, liveMc));
            }
            else if (source is not null)
            {
                actions.Add(new ResetAction("client", name, ResetActionKind.CopyConfigTree, cfgDir,
                    "BepInEx config re-copied", "config/set persists by default, so a flipped value is sticky",
                    Source: Path.Combine(source, "BepInEx", "config")));
                copied = true;

                if (baseline is not null)
                {
                    reports.Add(new ResetReport("client", name, ResetReportKind.BaselineMissesInstance,
                        "the baseline has no config for this instance, so its BepInEx config was re-copied from the "
                        + "source install instead. That is the pre-baseline behaviour and is only approximately right. "
                        + "Capture a baseline once this instance is set up as you want it.", Warn: true));
                }
            }
            else
            {
                reports.Add(new ResetReport("client", name, ResetReportKind.ConfigCopySkipped,
                    "BepInEx config NOT re-copied: no baseline covers this instance AND the source install could not "
                    + "be resolved. Any plugin setting a previous test changed is still in place.", Warn: true));
            }

            // ALWAYS, and always AFTER the copy. The copy wipes SavePathOverride, and an
            // instance without it writes into the developer's tier-1 save folder. Nothing
            // in this planner matters more than this ordering.
            actions.Add(new ResetAction("client", name, ResetActionKind.ReapplySavePathOverride, bepinex,
                "SavePathOverride re-applied",
                "the config re-copy wipes it; without it the instance writes into the developer tier-1 save folder",
                Target: userData, Role: role, AfterCopy: copied));

            var logOutput = RigFiles.CountEntries(_fs, bepinex, "LogOutput.log*");
            if (logOutput > 0)
            {
                actions.Add(new ResetAction("client", name, ResetActionKind.DeleteGlob, bepinex,
                    "LogOutput.log", "never rotated; a log grep matches a dead run",
                    Filter: "LogOutput.log*", Items: logOutput));
            }

            var cache = Path.Combine(bepinex, "cache");
            if (_fs.DirectoryExists(cache))
            {
                actions.Add(new ResetAction("client", name, ResetActionKind.DeleteDirectory, cache,
                    "BepInEx cache", "stale assembly cache after a plugin rebuild"));
            }

            AddContentsAction(actions, "client", name, Path.Combine(bepinex, "inspector", "requests"),
                "inspector request(s)", "an unconsumed request file fires on the next launch");
            AddContentsAction(actions, "client", name, Path.Combine(bepinex, "inspector", "snapshots"),
                "inspector snapshot(s)", "timestamped with no rotation, so \"read the newest\" picks up a stale one");
        }
        else
        {
            // The path is named WITH its source, because "no tree" and "the reset looked in
            // the wrong place" used to read identically.
            reports.Add(new ResetReport("client", name, ResetReportKind.NoTree,
                $"no instance tree at {treeInfo.Path} (from {treeInfo.Source}); only its data state was reset, so the "
                + "BepInEx config was NOT re-copied and SavePathOverride was NOT re-applied", Warn: true));
        }

        PlanStaleMods(name, userData, reports);
    }

    /// <summary>
    /// Session scoping for the client half's worlds.
    /// </summary>
    /// <remarks>
    /// New in the port. PowerShell emptied this directory with a recursive DeleteContents
    /// on every reset, unconditionally, with no marker, no baseline and no keep-list,
    /// though a listen host writes real worlds here. The repository documents both save
    /// roots as tier 3 and says "a world's lifetime is session-scoped" without saying that
    /// only the dedicated-server half was scoped, so anyone who staged a save into a client
    /// instance's save root lost it at the next lock or unlock. It was named the
    /// highest-plausibility real-world loss path in the whole subsystem.
    ///
    /// Loose files at the top of a save root are not worlds and are still cleared, which
    /// keeps the old behaviour for everything the scoping does not cover.
    /// </remarks>
    private void PlanClientWorlds(
        string name,
        SessionWorldSnapshot clientWorlds,
        List<ResetAction> actions,
        List<ResetReport> reports)
    {
        var saveRoot = _paths.InstanceSaveRoot(name);
        if (!_fs.DirectoryExists(saveRoot)) return;

        var scan = _worlds.ScanInstance(name);
        if (scan.Status != WorldScanStatus.Enumerated)
        {
            reports.Add(new ResetReport("client", name, ResetReportKind.ClientWorldsNotTracked,
                $"no world in this instance's save root is deleted by this restore: {scan.FailureDetail}", Warn: true));
            return;
        }

        if (scan.Worlds.Count == 0)
        {
            var loose = RigFiles.CountEntries(_fs, saveRoot, "*");
            if (loose > 0)
            {
                actions.Add(new ResetAction("client", name, ResetActionKind.DeleteGlob, saveRoot,
                    $"{loose} loose file(s) in the save root", "not a world, and nothing here should outlive a session",
                    Filter: "*", Items: loose));
            }
            return;
        }

        if (!clientWorlds.Recorded)
        {
            reports.Add(new ResetReport("client", name, ResetReportKind.ClientWorldsNotTracked,
                $"no client-instance world is deleted by this restore: {clientWorlds.Reason}",
                Warn: clientWorlds.Degraded));
        }

        var keptCount = 0;
        long keptBytes = 0;

        foreach (var world in scan.Worlds)
        {
            var bytes = RigFiles.DirectoryBytes(_fs, world.Path);
            if (clientWorlds.Recorded && !clientWorlds.Protects(world.Key))
            {
                actions.Add(new ResetAction("client", name, ResetActionKind.DeleteTree, world.Path,
                    $"world '{world.Name}' deleted ({bytes / 1048576.0:N1} MB)",
                    "it was not in this instance's save root when this session first touched the rig, so this "
                    + "session created it and its lifetime ends with the lock"));
            }
            else
            {
                keptCount++;
                keptBytes += bytes;
            }
        }

        if (keptCount > 0)
        {
            var why = clientWorlds.Recorded
                ? $"they were already here when this session started ({clientWorlds.Count} world(s) recorded)"
                : clientWorlds.Reason;
            reports.Add(new ResetReport("client", name, ResetReportKind.ClientSavesRetained,
                $"instance saves kept: {keptCount} world(s), {keptBytes / 1048576.0:N1} MB ({why})"));
        }

        var looseFiles = RigFiles.CountEntries(_fs, saveRoot, "*");
        if (looseFiles > 0)
        {
            actions.Add(new ResetAction("client", name, ResetActionKind.DeleteGlob, saveRoot,
                $"{looseFiles} loose file(s) in the save root", "not a world, and nothing here should outlive a session",
                Filter: "*", Items: looseFiles));
        }
    }

    /// <summary>Reported, never deleted: re-seeding needs the developer's modconfig and is provisioning's job.</summary>
    private void PlanStaleMods(string name, string userData, List<ResetReport> reports)
    {
        var seeded = Path.Combine(userData, "mods");
        if (!_fs.DirectoryExists(seeded)) return;
        if (string.IsNullOrEmpty(_paths.UserDataDir)) return;

        var sourceMods = Path.Combine(_paths.UserDataDir, "mods");
        foreach (var dir in _fs.EnumerateDirectories(seeded))
        {
            var modName = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var peer = Path.Combine(sourceMods, modName);
            if (!_fs.DirectoryExists(peer)) continue;

            var mine = NewestBuildTime(dir);
            var theirs = NewestBuildTime(peer);
            if (mine is null || theirs is null || theirs <= mine) continue;

            reports.Add(new ResetReport("client", name, ResetReportKind.StaleMod,
                $"seeded mod '{modName}' is older than the source tree ({mine:u} vs {theirs:u}). Re-seed it: "
                + $"testrig create --target {name} --force --as <id>", Warn: true));
        }
    }

    /// <summary>Newest write time under a folder, assemblies preferred.</summary>
    public DateTimeOffset? NewestBuildTime(string path)
    {
        if (!_fs.DirectoryExists(path)) return null;
        var files = _fs.EnumerateFiles(path, "*.dll", recurse: true);
        if (files.Count == 0) files = _fs.EnumerateFiles(path, "*", recurse: true);
        if (files.Count == 0) return null;

        DateTimeOffset? newest = null;
        foreach (var file in files)
        {
            var at = _fs.GetLastWriteTimeUtc(file);
            if (newest is null || at > newest) newest = at;
        }
        return newest;
    }

    private void PlanServer(
        Baseline? baseline,
        IReadOnlyList<SurfaceRecord> surfaceAll,
        string? lastReset,
        SessionWorldSnapshot serverWorlds,
        List<ResetAction> actions,
        List<ResetReport> reports)
    {
        var install = _paths.DediInstall;

        // The Scenario value selects which probe fires on the next boot, so a session that
        // forgets to blank it injects its scenario into an unrelated test's log. The VALUE
        // is blanked and the FILE is left alone: everything else in it is deliberate.
        //
        // Both plugin names, because the merge renamed the file. ScenarioRunner wrote
        // net.scenariorunner.cfg and the merged TestRig plugin writes net.sixfive7.testrig.cfg;
        // during the parity window both trees exist and either may be the one deployed. A
        // reset that knew only the old name would leave a probe armed on the new plugin.
        foreach (var leaf in RigConfigFiles.ScenarioCarrying)
        {
            var cfg = Path.Combine(install, "BepInEx", "config", leaf);
            if (!_fs.FileExists(cfg)) continue;

            var current = ConfigFile.GetSetting(_fs, cfg, RigConfigFiles.ScenarioSetting);
            if (string.IsNullOrEmpty(current)) continue;

            actions.Add(new ResetAction("server", null, ResetActionKind.BlankSetting, cfg,
                $"{leaf} Scenario blanked (was '{current}')",
                "it selects which probe fires on the next boot", Setting: RigConfigFiles.ScenarioSetting));
        }

        AddContentsAction(actions, "server", null, Path.Combine(install, "BepInEx", "scenariorunner", "requests"),
            "scenariorunner request(s)", "a stray drop file is consumed on the next boot");
        AddContentsAction(actions, "server", null, Path.Combine(install, "BepInEx", "scenariorunner", "give"),
            "scenariorunner give file(s)", "a stray drop file is consumed on the next boot");
        AddContentsAction(actions, "server", null, Path.Combine(install, "BepInEx", "inspector", "requests"),
            "inspector request(s)", "an unconsumed request file fires on the next launch");
        AddContentsAction(actions, "server", null, Path.Combine(install, "BepInEx", "inspector", "snapshots"),
            "inspector snapshot(s)", "timestamped with no rotation, so \"read the newest\" picks up a stale one");

        // The wrapper's cleanup does not run on a force-kill or a reboot, so these outlive
        // their processes. Same process-image check as everywhere else.
        var serverPidExists = _fs.FileExists(_paths.ServerPidFile);
        var serverLive = serverPidExists && _busy.IsPidClaimAlive(_paths.ServerPidFile, [_paths.ServerImage]);
        var hostPidExists = _fs.FileExists(_paths.HostPidFile);
        var hostLive = hostPidExists && _busy.IsPidClaimAlive(_paths.HostPidFile, _paths.HostWrapperImages);

        if (serverPidExists && !serverLive)
        {
            actions.Add(new ResetAction("server", null, ResetActionKind.DeleteFile, _paths.ServerPidFile,
                "stale server.pid", "no live dedicated server claims it"));
        }
        else if (serverLive)
        {
            reports.Add(new ResetReport("server", null, ResetReportKind.PreservedLivePid,
                $"server.pid kept: process {_busy.ReadPid(_paths.ServerPidFile)} is a live dedicated server"));
        }

        if (hostPidExists && !hostLive)
        {
            actions.Add(new ResetAction("server", null, ResetActionKind.DeleteFile, _paths.HostPidFile,
                "stale host.pid", "no live host wrapper claims it"));
        }
        else if (hostLive)
        {
            reports.Add(new ResetReport("server", null, ResetReportKind.PreservedLivePid,
                $"host.pid kept: process {_busy.ReadPid(_paths.HostPidFile)} is a live host wrapper"));
        }

        if (_fs.FileExists(_paths.ControlCmdFile) && !serverLive && !hostLive)
        {
            actions.Add(new ResetAction("server", null, ResetActionKind.DeleteFile, _paths.ControlCmdFile,
                "stale control.cmd", "a queued command nothing is left to consume"));
        }

        if (_fs.FileExists(_paths.ServerSettingXml))
        {
            actions.Add(new ResetAction("server", null, ResetActionKind.DeleteFile, _paths.ServerSettingXml,
                "setting.xml", "carries stale SavePath and UseSteamP2P; start passes every flag it needs"));
        }

        PlanServerWorlds(serverWorlds, actions, reports);
        PlanServerConfig(baseline, surfaceAll, lastReset, install, actions, reports);
    }

    /// <summary>
    /// THE RULE, and it is the whole rule: a world is deleted if and only if the session
    /// marker recorded a world set and this world is not in it.
    /// </summary>
    /// <remarks>
    /// A world on the rig when the session started is always kept, baseline or no baseline,
    /// fresh or stale. The baseline used to decide this and the failure is worth writing
    /// down because the code read as safe: staleness inspects the game version, the
    /// instance-name set and files of class payload, and worlds are class world, so the
    /// world set was invisible to staleness. Staging a world deliberately left the baseline
    /// reading fresh, still not listing that world, and the next session boundary deleted
    /// it. The staged save WAS the test.
    /// </remarks>
    private void PlanServerWorlds(SessionWorldSnapshot serverWorlds, List<ResetAction> actions, List<ResetReport> reports)
    {
        if (!_fs.DirectoryExists(_paths.ServerSaveRoot)) return;

        var scan = _worlds.ScanServer();
        if (scan.Status != WorldScanStatus.Enumerated)
        {
            reports.Add(new ResetReport("server", null, ResetReportKind.WorldsNotTracked,
                $"no dedicated-server world is deleted by this restore: {scan.FailureDetail}", Warn: true));
            return;
        }

        if (scan.Worlds.Count == 0) return;

        if (!serverWorlds.Recorded)
        {
            // Its own report rather than buried in the kept line, because "nothing is being
            // deleted" is exactly the sentence an agent needs when it expected a cleanup and
            // did not get one. A genuine degradation warns; a rig with no marker at all is
            // the ordinary clean state and is merely stated.
            reports.Add(new ResetReport("server", null, ResetReportKind.WorldsNotTracked,
                $"no dedicated-server world is deleted by this restore: {serverWorlds.Reason}",
                Warn: serverWorlds.Degraded));
        }

        var keptCount = 0;
        long keptBytes = 0;

        foreach (var world in scan.Worlds)
        {
            var bytes = RigFiles.DirectoryBytes(_fs, world.Path);
            if (serverWorlds.Recorded && !serverWorlds.Protects(world.Key))
            {
                actions.Add(new ResetAction("server", null, ResetActionKind.DeleteTree, world.Path,
                    $"world '{world.Name}' deleted ({bytes / 1048576.0:N1} MB)",
                    "it was not on the rig when this session first touched it, so this session created it and its "
                    + "lifetime ends with the lock"));
            }
            else
            {
                keptCount++;
                keptBytes += bytes;
            }
        }

        if (keptCount > 0)
        {
            var why = serverWorlds.Recorded
                ? $"they were already here when this session started ({serverWorlds.Count} world(s) recorded)"
                : serverWorlds.Reason;
            reports.Add(new ResetReport("server", null, ResetReportKind.SavesRetained,
                $"data/saves kept: {keptCount} world(s), {keptBytes / 1048576.0:N1} MB ({why})"));
        }
    }

    private void PlanServerConfig(
        Baseline? baseline,
        IReadOnlyList<SurfaceRecord> surfaceAll,
        string? lastReset,
        string install,
        List<ResetAction> actions,
        List<ResetReport> reports)
    {
        var cfgDir = Path.Combine(install, "BepInEx", "config");
        var covered = baseline is not null
                      && baseline.Files.Keys.Any(static k => k.StartsWith("server/bepinex-config/", StringComparison.OrdinalIgnoreCase));

        if (covered)
        {
            var liveSrv = surfaceAll
                .Where(static r => r.Class == SurfaceClass.Config
                                   && r.Key.StartsWith("server/bepinex-config/", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            foreach (var action in _baseline.ConfigActions(baseline!, "server/bepinex-config/", cfgDir, "server", null, liveSrv))
            {
                // Already handled above by blanking exactly one value and leaving the rest of
                // the file alone. Restoring the whole file from the baseline as well would
                // fight that, and would put back whatever Scenario the baseline captured.
                if (RigConfigFiles.CarriesScenario(action.Path)) continue;
                actions.Add(action);
            }

            var liveMc = surfaceAll.Where(static r => string.Equals(r.Key, "server/modconfig.xml", StringComparison.OrdinalIgnoreCase)).ToArray();
            actions.AddRange(_baseline.ConfigActions(baseline!, "server/modconfig.xml", install, "server", null, liveMc));
            return;
        }

        if (lastReset is null || !_fs.DirectoryExists(cfgDir)) return;

        var since = RigTime.TryParse(lastReset);
        if (since is null) return;

        var touched = _fs.EnumerateFiles(cfgDir, "*.cfg", recurse: false)
            .Where(f => !RigConfigFiles.CarriesScenario(f))
            .Where(f => _fs.GetLastWriteTimeUtc(f) > since.Value)
            .Select(Path.GetFileName)
            .ToArray();

        if (touched.Length == 0) return;

        reports.Add(new ResetReport("server", null, ResetReportKind.ConfigTouched,
            "server config changed since the last reset and is NOT reset here (no baseline covers the server, so "
            + $"rig-owned versus mod-owned is still undecided): {string.Join(", ", touched)}. Capture a baseline to "
            + "make these restorable: testrig capture-baseline --as <id>", Warn: true));
    }

    private void AddContentsAction(List<ResetAction> actions, string half, string? instance, string path, string label, string reason)
    {
        var count = RigFiles.CountEntries(_fs, path);
        if (count <= 0) return;
        actions.Add(new ResetAction(half, instance, ResetActionKind.DeleteContents, path,
            $"{count} {label}", reason, Items: count));
    }
}
