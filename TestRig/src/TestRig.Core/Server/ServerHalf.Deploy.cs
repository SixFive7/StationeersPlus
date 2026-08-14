using TestRig.Core.Rig;
using TestRig.Core.Session;

namespace TestRig.Core.Server;

/// <summary>What a server-half deploy or sync did.</summary>
public readonly record struct ServerCounts(int Done, int Skipped);

public sealed partial class ServerHalf
{
    // =====================================================================
    // deploy
    // =====================================================================

    /// <summary>
    /// Puts this repository's built mods onto the dedicated server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// TWO DESTINATIONS, decided by what the target is, and the split is not cosmetic: this
    /// half has two load paths and the same DLL in both is fatal.
    /// <c>install/BepInEx/plugins/&lt;X&gt;/</c> is loaded by the BepInEx Chainloader;
    /// <c>data/mods/Local_&lt;X&gt;/</c> is loaded by StationeersLaunchPad. With a DLL in
    /// both, Awake fires twice, every Harmony patch registers twice and every side-effecting
    /// patch doubles. Dev-plugins take the StationeersLaunchPad path because they need an
    /// About.xml; released mods take the plugins path.
    /// </para>
    /// <para>
    /// The client half has the same two paths and the same trap, and resolves it the other
    /// way for the same reason. That is why one deploy verb cannot use one destination.
    /// </para>
    /// </remarks>
    public ServerCounts Deploy(
        IReadOnlyList<string>? mods = null,
        string? callerId = null,
        string configuration = "Release")
    {
        AssertGate("deploy", callerId);

        if (!_fs.FileExists(_paths.Exe))
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                $"The dedicated server is not installed at {_paths.Exe}. Run: testrig update-game --target "
                + "server --as <id>");
        }

        if (ServerAlive || WrapperAlive)
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                $"The dedicated server is running (host PID {HostPid}, server PID {ServerPid}). The Mono runtime "
                + "holds an exclusive lock on every loaded plugin DLL on Windows; a deploy would fail with a "
                + "sharing violation, or worse, leave a half-written DLL the next start picks up as broken "
                + "plugin bytes. Run: testrig stop --target server --as <id>");
        }

        var names = mods is { Count: > 0 } ? mods : _mods.DeployableMods();
        if (names.Count == 0)
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                "No mods to deploy: Mods/ has no mod folders other than Template.");
        }

        var deployed = 0;
        var skipped = 0;

        foreach (var modName in names)
        {
            var build = _mods.Find(modName, configuration);
            if (build is null)
            {
                Warn($"[{modName}] not found under Mods/, Plans/ or either half's dev-plugins/. Skipping.");
                skipped++;
                continue;
            }

            if (build.LoadPathOn(RigHalf.Server) == LoadPath.LaunchPad)
            {
                if (DeployToLaunchPad(build)) deployed++;
                else skipped++;
                continue;
            }

            if (!_fs.FileExists(build.Dll))
            {
                Warn($"[{modName}] {configuration} build not found at {build.Dll}. Skipping. Build it first.");
                skipped++;
                continue;
            }

            var destination = Path.Combine(_paths.PluginsDir, modName);
            _fs.CreateDirectory(destination);
            _fs.CopyFile(build.Dll, Path.Combine(destination, modName + ".dll"), overwrite: true);

            RemoveStaleCopy(modName, Path.Combine(_paths.ModsDir, "Local_" + modName), "StationeersLaunchPad");

            Say($"[Deploy] {modName} -> {destination} (BepInEx Chainloader load path)");
            deployed++;
        }

        Say($"[Deploy] server: {deployed} deployed, {skipped} skipped.");
        _output.Value("deployed", deployed);
        _output.Value("skipped", skipped);

        // Returned as a value rather than written to the output stream (SERVER-046 fixed):
        // the PowerShell's return object was not suppressed by the dispatcher, so it printed
        // after the human-readable lines and read as stray output.
        return new ServerCounts(deployed, skipped);
    }

    /// <summary>
    /// Mirrors a dev-plugin into the StationeersLaunchPad load path.
    /// </summary>
    /// <remarks>
    /// StationeersLaunchPad keys mods off <c>Local_&lt;X&gt;/About/About.xml</c>, so the
    /// About folder is mirrored as well as the DLL. The <c>&lt;Local&gt;</c> entry written
    /// into the install's modconfig carries the ABSOLUTE path, which is correct: a rooted
    /// value bypasses StationeersLaunchPad's <c>&lt;localDir&gt;</c> prefix step and matches
    /// the discovered mod's own path (SERVER-035).
    /// </remarks>
    private bool DeployToLaunchPad(ModBuild build)
    {
        if (!_fs.FileExists(build.Dll))
        {
            Warn($"[{build.Name}] {build.Configuration} build not found at {build.Dll}. Skipping.");
            return false;
        }

        var localModDir = Path.Combine(_paths.ModsDir, "Local_" + build.Name);
        _fs.CreateDirectory(localModDir);

        if (_fs.DirectoryExists(build.About))
        {
            var aboutDst = Path.Combine(localModDir, "About");
            if (_fs.DirectoryExists(aboutDst)) _fs.DeleteDirectory(aboutDst, recursive: true);
            TreeOps.CopyTree(_fs, build.About, aboutDst);
        }
        else
        {
            Warn($"[{build.Name}] no About/ folder at {build.About}; StationeersLaunchPad may not load this "
                 + "plugin without About.xml.");
        }

        _fs.CopyFile(build.Dll, Path.Combine(localModDir, build.Name + ".dll"), overwrite: true);

        RemoveStaleCopy(build.Name, Path.Combine(_paths.PluginsDir, build.Name), "BepInEx Chainloader");

        if (ModConfig.AddLocalEntry(_fs, _paths.ModConfig, localModDir))
        {
            Say($"[Deploy] {build.Name}: added modconfig.xml Local entry -> {localModDir}");
        }

        Say($"[Deploy] {build.Name} -> {localModDir} (StationeersLaunchPad load path)");
        return true;
    }

    /// <summary>
    /// Removes a payload sitting in the OTHER load path.
    /// </summary>
    /// <remarks>
    /// SERVER-034 fixed. The PowerShell deleted only the DLL, leaving <c>About.xml</c> and
    /// the whole <c>About/</c> folder behind in the plugins folder, which is exactly what
    /// StationeersLaunchPad keys a second copy off. The whole directory goes, and the fact
    /// that a payload was found in both paths is REPORTED rather than silently corrected,
    /// because a tree in that state was deployed by something and its owner should know.
    /// </remarks>
    private void RemoveStaleCopy(string modName, string otherPath, string otherLoader)
    {
        if (!_fs.DirectoryExists(otherPath)) return;

        _fs.DeleteDirectory(otherPath, recursive: true);
        Warn($"[Deploy] {modName}: a copy was also present in the {otherLoader} load path at {otherPath} and "
             + "has been removed. A payload in both load paths makes Awake fire twice and registers every "
             + "Harmony patch twice, which doubles every side-effecting patch.");
    }

    // =====================================================================
    // update-mods
    // =====================================================================

    /// <summary>
    /// Mirrors the developer's enabled mod set onto the server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reads their <c>modconfig.xml</c> (read-only on the source, always), copies each
    /// enabled Workshop and Local entry into <c>data/mods/&lt;Source&gt;_&lt;Name&gt;/</c>,
    /// and bakes an <c>install/modconfig.xml</c> of Local entries pointing at the copies.
    /// That replicates StationeersLaunchPad's Export Mod Package without driving the UI.
    /// </para>
    /// <para>
    /// The baked Path values are BARE FOLDER NAMES, resolved by StationeersLaunchPad against
    /// the save path. That form is correct and verified end to end; do not "fix" it to
    /// absolute paths (SERVER-178). It is the opposite of the absolute path a per-mod deploy
    /// writes, and both are right for their own reasons.
    /// </para>
    /// <para>
    /// THIS WIPES <c>data/mods/</c>, so anything a deploy put there goes with it. That is the
    /// pre-existing order (sync first, deploy second) and it is said out loud rather than
    /// left to be rediscovered.
    /// </para>
    /// </remarks>
    public ServerCounts UpdateMods(string? callerId = null, string? fromModConfig = null)
    {
        AssertGate("update-mods", callerId);

        if (!_fs.FileExists(_paths.Exe))
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                $"The dedicated server is not installed at {_paths.Exe}. Run: testrig update-game --target "
                + "server --as <id>");
        }

        if (ServerAlive || WrapperAlive)
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                $"The dedicated server is running (host PID {HostPid}, server PID {ServerPid}). "
                + "StationeersLaunchPad holds the synced mod files open for class scanning; overwriting them "
                + "while loaded fails with a sharing violation or leaves a half-written tree. Run: testrig stop "
                + "--target server --as <id>");
        }

        var source = string.IsNullOrWhiteSpace(fromModConfig)
            ? Path.Combine(_env.UserDataPath(), "modconfig.xml")
            : fromModConfig;

        if (!_fs.FileExists(source))
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                $"Source modconfig not found at {source}. Pass --from-modconfig <path> to override.");
        }

        Say($"[UpdateMods] server source: {source}");

        var planned = new List<(string Source, string DestName)>();
        foreach (var entry in ModConfig.Read(_fs, source))
        {
            if (!entry.Enabled) continue;

            switch (entry.Kind)
            {
                case "Core":
                    // Implicit: the writer always emits one.
                    break;

                case "Workshop":
                {
                    var id = entry.WorkshopId;
                    if (string.IsNullOrEmpty(id))
                    {
                        Warn($"[UpdateMods] Workshop entry without WorkshopId; using the basename of {entry.Path}");
                        id = Path.GetFileName(entry.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    }
                    planned.Add((entry.Path, "Workshop_" + id));
                    break;
                }

                case "Local":
                {
                    if (string.IsNullOrEmpty(entry.Path)) continue;
                    var leaf = Path.GetFileName(entry.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    planned.Add((entry.Path, "Local_" + leaf));
                    break;
                }

                default:
                    Warn($"[UpdateMods] Unknown modconfig entry type '{entry.Kind}'; ignoring");
                    break;
            }
        }

        // Recorded BEFORE the wipe, because after it there is nothing left to name.
        var wiped = TreeOps.ChildDirectoryNames(_fs, _paths.ModsDir);

        if (_fs.DirectoryExists(_paths.ModsDir))
        {
            Say($"[UpdateMods] Wiping {_paths.ModsDir}");
            _fs.DeleteDirectory(_paths.ModsDir, recursive: true);
        }
        _fs.CreateDirectory(_paths.ModsDir);

        var copied = 0;
        var skipped = 0;

        foreach (var (from, destName) in planned)
        {
            if (!_fs.DirectoryExists(from))
            {
                Warn($"[UpdateMods] [{destName}] source missing: {from} (skipping)");
                skipped++;
                continue;
            }

            TreeOps.CopyTree(_fs, from, Path.Combine(_paths.ModsDir, destName));
            Say($"[UpdateMods] {destName} <- {from}");
            copied++;
        }

        var baked = planned
            .Where(p => _fs.DirectoryExists(Path.Combine(_paths.ModsDir, p.DestName)))
            .Select(static p => new ModConfigEntry("Local", true, p.DestName, ""))
            .ToList();

        ModConfig.Write(_fs, _paths.ModConfig, baked);

        Say($"[UpdateMods] Wrote {_paths.ModConfig} with {baked.Count} Local entries (Core + {baked.Count}).");
        Say($"[UpdateMods] server: {copied} copied, {skipped} skipped (missing source).");

        // SERVER-180 fixed: the PowerShell intersected against RELEASED MODS ONLY, so every
        // wiped Plans/ mod and every wiped dev-plugin, which is exactly where dev-plugins are
        // deployed on this half, disappeared with no warning from the one message whose job
        // is naming what the wipe took.
        var lost = _mods.RepositoryFoldersAmong(wiped);
        if (lost.Count > 0)
        {
            Warn($"[UpdateMods] The wipe removed {lost.Count} folder(s) this repository had deployed: "
                 + $"{string.Join(", ", lost)}. Re-deploy them: testrig deploy <Mod> --target server --as <id>");
        }

        _output.Value("copied", copied);
        _output.Value("skipped", skipped);
        return new ServerCounts(copied, skipped);
    }
}
