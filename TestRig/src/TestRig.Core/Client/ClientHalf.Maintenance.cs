using TestRig.Core.Rig;
using TestRig.Core.Session;

namespace TestRig.Core.Client;

/// <summary>What a deploy did.</summary>
public readonly record struct DeployCounts(int Deployed, int Skipped);

public sealed partial class ClientHalf
{
    // =====================================================================
    // update-game
    // =====================================================================

    /// <summary>
    /// Re-links every selected instance from the developer's install.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mechanism is genuinely different from the server half's (a hard-link rebuild here,
    /// a SteamCMD download there) and so is the source (the developer's already-updated
    /// client install, versus Steam app 600760). What is the same is the intent, so one verb
    /// fans out over both.
    /// </para>
    /// <para>
    /// The gate is asserted FIRST, before the pre-flight and before the install path is
    /// resolved (CLIENT-308 fixed). The PowerShell gated nothing here and relied on the gate
    /// inside <c>create</c>, which meant a zero-target update-game was ungated entirely and
    /// the crash marker recorded the session's first mutating action as <c>create</c>.
    /// </para>
    /// <para>
    /// A rebuild replaces the TREE and keeps <c>data/&lt;instance&gt;/</c>, so saves, logs and
    /// the game-written <c>setting.xml</c> survive. Role, ports and identity are kept because
    /// they come out of the registry entry, which the PowerShell only managed for two of the
    /// five (CLIENT-306 fixed). It DOES re-seed the mod set, which un-deploys every
    /// repository mod; that is now warned about at the wipe itself.
    /// </para>
    /// </remarks>
    public async Task UpdateGameAsync(
        IReadOnlyList<InstanceEntry> entries,
        string? callerId = null,
        string desktop = RigConstants.DefaultDesktop,
        CancellationToken ct = default)
    {
        AssertGate("update-game", callerId);

        if (entries.Count == 0)
        {
            Say("[UpdateGame] No client instances are provisioned; nothing to re-link.");
            return;
        }

        var source = _env.StationeersPath();
        Say($"[UpdateGame] Re-linking {entries.Count} instance(s) from {source} "
            + $"(game {_env.InstallVersion(source)}).");

        // Pre-flight the whole set before rebuilding any of it, for the same reason starting
        // does: a half-updated rig is worse than one that refused (CLIENT-305).
        foreach (var entry in entries)
        {
            var paths = _layout.PathsFor(entry.InstanceName, entry);
            if (PidFiles.ClientAlive(_fs, _processes, paths.PidFile))
            {
                throw new RigRefusalException(
                    RigRefusalKind.Refused,
                    $"[UpdateGame] Instance '{entry.InstanceName}' is running. Stop it first: testrig stop "
                    + $"--target {entry.InstanceName} --as <id>");
            }
        }

        foreach (var entry in entries)
        {
            Say($"[UpdateGame] --- {entry.InstanceName}");
            await CreateAsync(
                new CreateOptions
                {
                    Instance = entry.InstanceName,
                    CallerId = callerId,
                    Force = true,
                    Desktop = desktop,
                },
                ct).ConfigureAwait(false);
        }

        Say($"[UpdateGame] {entries.Count} instance(s) re-linked.");
        _output.Value("relinked", entries.Count);
    }

    // =====================================================================
    // update-mods
    // =====================================================================

    /// <summary>
    /// Re-seeds each selected instance's mod set from the developer's mod folder.
    /// </summary>
    /// <remarks>
    /// Same concept as the server half's, different destination: each instance gets its own
    /// copy under <c>data/&lt;instance&gt;/userdata/mods/</c> with its own
    /// <c>modconfig.xml</c>, because StationeersLaunchPad prunes Local entries whose folder is
    /// not under the active save path.
    ///
    /// This WIPES <c>userdata/mods/</c>, so anything a deploy put there goes with it. The
    /// removal is named at the wipe itself, which covers this caller, <c>create --force</c>
    /// and <c>update-game</c> alike, and it names <c>Plans/</c> mods and dev-plugins as well
    /// as released mods (CLIENT-314 fixed).
    /// </remarks>
    public void UpdateMods(IReadOnlyList<InstanceEntry> entries, string? callerId = null)
    {
        AssertGate("update-mods", callerId);

        if (entries.Count == 0)
        {
            Say("[UpdateMods] No client instances are provisioned; nothing to seed.");
            return;
        }

        foreach (var entry in entries)
        {
            var paths = _layout.PathsFor(entry.InstanceName, entry);
            if (PidFiles.ClientAlive(_fs, _processes, paths.PidFile))
            {
                throw new RigRefusalException(
                    RigRefusalKind.Refused,
                    $"[UpdateMods] Instance '{entry.InstanceName}' is running and holds its mod files open. "
                    + $"Stop it first: testrig stop --target {entry.InstanceName} --as <id>");
            }
        }

        foreach (var entry in entries)
        {
            var paths = _layout.PathsFor(entry.InstanceName, entry);
            Say($"[UpdateMods] --- {entry.InstanceName}");

            // The instance's own recorded set, so a re-seed cannot put the developer's copy of
            // the mod under test back beside the deployed one.
            SeedMods(paths, entry.UnderTestMods);
        }

        Say($"[UpdateMods] {entries.Count} instance(s) re-seeded.");
        _output.Value("reseeded", entries.Count);
    }

    // =====================================================================
    // deploy
    // =====================================================================

    /// <summary>
    /// Puts one of THIS repository's built mods into each selected instance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This capability did not exist before the consolidation (CLIENT-331). There was no path
    /// at all from a repository build into an instance: provisioning seeded from the
    /// developer's OWN mod folder, so a driven test measured whatever build happened to be
    /// sitting there. A live run did exactly that with a weeks-old copy, and the only reason
    /// it was caught is that the file sizes happened to differ visibly.
    /// </para>
    /// <para>
    /// DESTINATION, and why it differs from the server half's. An instance has the same two
    /// load paths and the same duplicate-load fatal: <c>BepInEx/plugins/</c> is loaded by the
    /// Chainloader, <c>userdata/mods/Local_&lt;X&gt;/</c> by StationeersLaunchPad, and a DLL
    /// in both makes Awake fire twice and every Harmony patch register twice.
    /// <c>ClientDriver</c> takes the plugins path because it has to load before
    /// StationeersLaunchPad runs, so a repository mod takes the StationeersLaunchPad path
    /// (CLIENT-323).
    /// </para>
    /// </remarks>
    public DeployCounts Deploy(
        IReadOnlyList<InstanceEntry> entries,
        IReadOnlyList<string>? mods = null,
        string? callerId = null,
        string configuration = "Release")
    {
        AssertGate("deploy", callerId);

        if (entries.Count == 0)
        {
            Say("[Deploy] No client instances selected.");
            return new DeployCounts(0, 0);
        }

        var named = mods is { Count: > 0 };

        foreach (var entry in entries)
        {
            var paths = _layout.PathsFor(entry.InstanceName, entry);
            if (PidFiles.ClientAlive(_fs, _processes, paths.PidFile))
            {
                throw new RigRefusalException(
                    RigRefusalKind.Refused,
                    $"[Deploy] Instance '{entry.InstanceName}' is running and holds its loaded plugin DLLs open; "
                    + $"a deploy would fail or leave a half-written file. Stop it first: testrig stop --target "
                    + $"{entry.InstanceName} --as <id>");
            }
        }

        var deployed = 0;
        var skipped = 0;

        foreach (var entry in entries)
        {
            var paths = _layout.PathsFor(entry.InstanceName, entry);

            // With no --mod, the default is THIS INSTANCE'S recorded under-test set and not
            // every released mod. Fanning out over Mods/ was the shape that produced the
            // double load: it deployed builds beside the developer's seeded copies of mods
            // nobody was testing, and every one of those pairs loads twice.
            var names = named ? mods! : entry.UnderTestMods;
            if (names.Count == 0)
            {
                throw new RigRefusalException(
                    RigRefusalKind.Refused,
                    $"'{entry.InstanceName}' records no mods under test, so a deploy with no --mod has nothing "
                    + "to put there. Name one, or record it on the instance so the seed stops providing the "
                    + $"developer's copy:\n\n  testrig create --target {entry.InstanceName} --force --under-test "
                    + "<Mod> --as <id>");
            }

            foreach (var modName in names)
            {
                var build = _mods.Find(modName, configuration);
                if (build is null)
                {
                    Warn($"[{entry.InstanceName}] '{modName}' not found under Mods/, Plans/ or either half's "
                         + "dev-plugins/. Skipping.");
                    skipped++;
                    continue;
                }
                if (!_fs.FileExists(build.Dll))
                {
                    Warn($"[{entry.InstanceName}] the {configuration} build of '{modName}' is not at "
                         + $"{build.Dll}. Skipping. Build it first.");
                    skipped++;
                    continue;
                }

                // The control plane is the one payload that belongs in the OTHER path here,
                // and it is also the only way an existing instance can be moved onto the
                // merged plugin without rebuilding its tree. Routed by the build's own
                // answer, not by name, so this keeps working when the name changes again.
                if (build.LoadPathOn(RigHalf.Client) == LoadPath.Chainloader)
                {
                    // The build the CALLER named, not whatever the layout would resolve to.
                    // Asking for ClientDriver by name has to deploy ClientDriver even on a
                    // rig that has the merged plugin built, or the command silently does
                    // something other than what it says.
                    DeployControlPlugin(
                        paths,
                        new ControlPluginBuild(
                            build.Name,
                            Path.Combine(build.Dir, build.Name + ".sln"),
                            build.Dll));
                    deployed++;
                    continue;
                }

                AssertModIsUnderTestHere(entry, modName);

                var localModDir = LaunchPadMods.DeployedDir(paths.ModsDir, modName);
                _fs.CreateDirectory(localModDir);

                if (_fs.DirectoryExists(build.About))
                {
                    var aboutDst = Path.Combine(localModDir, "About");
                    if (_fs.DirectoryExists(aboutDst)) _fs.DeleteDirectory(aboutDst, recursive: true);
                    TreeOps.CopyTree(_fs, build.About, aboutDst);
                }
                else
                {
                    Warn($"[{entry.InstanceName}] '{modName}' has no About/ folder at {build.About}; "
                         + "StationeersLaunchPad may not load it without About.xml.");
                }

                _fs.CopyFile(build.Dll, LaunchPadMods.DeployedDll(paths.ModsDir, modName), overwrite: true);

                // A tree deployed the other way by hand self-heals (CLIENT-327). The whole
                // stale directory goes, not just the DLL: an About.xml left behind under the
                // Chainloader path is what StationeersLaunchPad keys a second copy off.
                var stale = Path.Combine(paths.BepInEx, "plugins", modName);
                if (_fs.FileExists(Path.Combine(stale, modName + ".dll")))
                {
                    _fs.DeleteDirectory(stale, recursive: true);
                    Say($"[{entry.InstanceName}] removed a stale duplicate at BepInEx/plugins/{modName}/ "
                        + "(two loaders double every Harmony patch).");
                }

                if (ModConfig.AddLocalEntry(_fs, paths.ModConfig, localModDir))
                {
                    Say($"[{entry.InstanceName}] added a modconfig.xml Local entry -> {localModDir}");
                }

                Say($"[{entry.InstanceName}] {modName} -> {localModDir} (StationeersLaunchPad load path)");
                deployed++;
            }
        }

        Say($"[Deploy] clients: {deployed} deployed, {skipped} skipped.");
        _output.Value("deployed", deployed);
        _output.Value("skipped", skipped);
        return new DeployCounts(deployed, skipped);
    }

    /// <summary>
    /// Refuses to deploy a mod this instance was not provisioned to test.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The instance's own record decides, because it is the only thing that also decides what
    /// the seed did. A mod NOT in the set was seeded from the developer's folder at its
    /// published state, deliberately, so deploying beside it would produce two copies, and
    /// StationeersLaunchPad loads both: Awake twice, every Harmony patch registered twice, and
    /// output that looks entirely plausible. That is how the double load happened, and it
    /// happened silently.
    /// </para>
    /// <para>
    /// A refusal rather than a cleanup, because the two cases are not the same. "This
    /// instance tests SprayPaintPlus" is a decision about the whole instance and belongs at
    /// <c>create</c>; a deploy quietly re-deciding it would change what every other mod on the
    /// instance is, one command at a time.
    /// </para>
    /// </remarks>
    private static void AssertModIsUnderTestHere(InstanceEntry entry, string modName)
    {
        if (entry.IsUnderTest(modName)) return;

        var recorded = entry.UnderTestMods.Count == 0
            ? "nothing"
            : string.Join(", ", entry.UnderTestMods);

        throw new RigRefusalException(
            RigRefusalKind.Refused,
            $"'{entry.InstanceName}' is not provisioned to test '{modName}' (it records {recorded}), so it "
            + $"carries the DEVELOPER'S published copy of '{modName}', seeded from their mods folder. Deploying "
            + "beside that copy leaves two, StationeersLaunchPad loads both, and every Harmony patch registers "
            + $"twice with nothing in any log to say so.\n\nRecord it, which re-seeds without '{modName}' and "
            + $"leaves the deploy as its only copy:\n\n  testrig create --target {entry.InstanceName} --force "
            + $"--under-test {modName} --as <id>\n  testrig deploy {modName} --target {entry.InstanceName} "
            + "--as <id>\n\nThe set is per instance and is preserved by later rebuilds. Every mod OUTSIDE it "
            + "stays at the published state on purpose: this repository carries work in progress for those too, "
            + "and an unrelated half-finished mod changing the behaviour of the one under test is exactly what "
            + "that separation prevents.");
    }

    // =====================================================================
    // the lock's reclaim path
    // =====================================================================

    /// <summary>
    /// Force-kills every live instance, for the lock's reclaim of a dead session.
    /// </summary>
    /// <remarks>
    /// DELIBERATELY NOT the ordered teardown (CLIENT-341). That ordering, joiners disconnect,
    /// the world holder saves, the host quits last, exists to end a test cleanly and preserve
    /// its world. Here the session that owned these instances has been silent for at least
    /// the idle ceiling, there is no test left to preserve, and a hung client's control plane
    /// is exactly the thing likely not to answer. A port that "improved" this path by reusing
    /// the ordered teardown would hang on precisely the wedged planes the reclaim exists to
    /// handle.
    /// </remarks>
    public async Task<int> ReclaimAsync(CancellationToken ct = default)
    {
        // Orphan scoping wired, so an untracked process that is the developer's own client is
        // not mistaken for rig debris. EnumerateInstances does not use it, but constructing a
        // probe without it anywhere is how the unwired default spreads.
        var probe = ProcessImagePaths.Probe(_fs, _processes, _paths);
        var live = probe.EnumerateInstances();
        if (live.Count == 0) return 0;

        Warn($"[Lock] Reclaimed the rig from a session that left {live.Count} instance(s) running: "
             + $"{string.Join(", ", live.Select(static i => i.Name))}. Stopping them, because the restore cannot "
             + "clear files a running game holds open.");

        foreach (var instance in live)
        {
            try
            {
                if (await ForceKill.NowAsync(_processes, instance.ProcessId, ct).ConfigureAwait(false))
                {
                    Say($"[Lock]   stopped {instance.Name} (pid {instance.ProcessId}).");
                }
                else
                {
                    Warn($"[Lock]   could not stop {instance.Name} (pid {instance.ProcessId}): it did not exit.");
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                Warn($"[Lock]   could not stop {instance.Name} (pid {instance.ProcessId}): {ex.Message}");
            }
        }

        await _sleeper.DelayAsync(PollInterval, ct).ConfigureAwait(false);
        return live.Count;
    }
}
