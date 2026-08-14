using TestRig.Core.Abstractions;
using TestRig.Core.Rig;
using TestRig.Core.Session;

namespace TestRig.Core.Client;

/// <summary>The control plugin an instance runs, resolved rather than named in code.</summary>
/// <param name="Name">Also the folder it is deployed into under <c>BepInEx/plugins/</c>.</param>
public sealed record ControlPluginBuild(string Name, string Sln, string Dll);

/// <summary>Every path one instance owns, and where its tree location came from.</summary>
public sealed record InstancePaths(
    string Name,
    string Tree,
    string Exe,
    string BepInEx,
    string Root,
    string RootSource,
    string Data,
    string Manifest,
    string PidFile,
    string Settings,
    string UserData,
    string LogDir)
{
    /// <summary>The instance's own BepInEx log, which is what <c>logs</c> prints.</summary>
    public string BepInExLog => Path.Combine(BepInEx, "LogOutput.log");

    /// <summary>The instance's own modconfig, resolved by StationeersLaunchPad against the save path.</summary>
    public string ModConfig => Path.Combine(UserData, "modconfig.xml");

    /// <summary>The instance's seeded mod folder.</summary>
    public string ModsDir => Path.Combine(UserData, "mods");

    /// <summary>The provision stamp, beside the manifest.</summary>
    public string Stamp => Path.Combine(Data, "provision.stamp");
}

/// <summary>
/// Where each instance's tree lives, and how that answer is reached.
/// </summary>
/// <remarks>
/// <para>
/// The recorded root is what makes <c>--instances-root</c> stick. Provisioning writes the
/// resolved root into the registry entry and every later action reads it back, so the flag
/// is typed once rather than on every command. Before that, provisioning honoured the flag
/// and start, stop, call and the state reset all fell back to <c>instances/</c> beside the
/// launcher: start reported a provisioned instance as having no tree at a path nothing had
/// ever built, and the reset found no BepInEx config to re-copy and said only that there
/// was no tree.
/// </para>
/// <para>
/// Precedence, and why:
/// </para>
/// <list type="number">
/// <item><c>--instances-root</c> as TYPED on this command. An explicit flag has to win, or
/// a tree could never be moved (CLIENT-025).</item>
/// <item>The root recorded in the registry entry (CLIENT-026).</item>
/// <item>The launcher default, reported with its own source string (CLIENT-027).</item>
/// </list>
/// </remarks>
public sealed class ClientLayout
{
    private readonly IFileSystem _fs;
    private readonly RigEnvironment _env;
    private readonly RigPaths _paths;
    private readonly IOutput _output;
    private readonly RigRegistry _registry;

    /// <summary>
    /// Instance names whose fallback notice has already been printed.
    /// </summary>
    /// <remarks>
    /// Initialised ONCE, here, as a readonly field (CLIENT-028 fixed). The PowerShell
    /// initialised the same map both inside its initialiser and at file scope, so calling
    /// the initialiser twice in one process reset the suppression and a four-instance
    /// command could print eight notices.
    /// </remarks>
    private readonly HashSet<string> _fallbackAnnounced = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="typedInstancesRoot">
    /// The value of <c>--instances-root</c> AS TYPED, or null when the caller did not type
    /// it. Whether it was typed is a different question from whether it has a value
    /// (CLIENT-004): a typed root wins over the one recorded in an instance's entry, and an
    /// untyped one must not.
    /// </param>
    public ClientLayout(
        IFileSystem fs,
        RigEnvironment env,
        RigPaths paths,
        IOutput output,
        RigRegistry registry,
        string? typedInstancesRoot = null)
    {
        _fs = fs;
        _env = env;
        _paths = paths;
        _output = output;
        _registry = registry;

        InstancesRootTyped = !string.IsNullOrWhiteSpace(typedInstancesRoot);

        var resolved = env.DefaultInstancesRoot(typedInstancesRoot);
        InstancesDir = resolved.Root;
        InstancesDirSource = resolved.Source;
    }

    /// <summary>Whether <c>--instances-root</c> was typed on this command.</summary>
    public bool InstancesRootTyped { get; }

    /// <summary>The launcher's own default root, before any registry entry is consulted.</summary>
    public string InstancesDir { get; }

    /// <summary>Where <see cref="InstancesDir"/> came from, for a message that names its own source.</summary>
    public string InstancesDirSource { get; }

    /// <summary>The <c>ClientRig/</c> folder itself.</summary>
    public string ClientRoot => Path.Combine(_env.RigHome, "ClientRig");

    /// <summary>
    /// The control plugin an instance is built with: its name, its solution and its DLL.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The merged <c>TestRig</c> plugin wins when its build exists, and <c>ClientDriver</c>
    /// is the fallback. Both trees exist during the parity window, deliberately, so this
    /// resolves rather than hardcoding either: a name in the code is what made the merged
    /// plugin undeployable, and hardcoding the NEW name instead would strand every rig that
    /// has not built it yet.
    /// </para>
    /// <para>
    /// Whichever wins takes the BepInEx Chainloader path, not StationeersLaunchPad's: the
    /// control plane has to be up before StationeersLaunchPad runs, and a payload in both
    /// paths makes Awake fire twice and registers every Harmony patch twice.
    /// </para>
    /// </remarks>
    public ControlPluginBuild ControlPlugin
    {
        get
        {
            var merged = ControlPluginAt(Path.Combine(_env.RigHome, "dev-plugins"), ControlPlugins.Merged);
            return _fs.FileExists(merged.Dll)
                ? merged
                : ControlPluginAt(Path.Combine(ClientRoot, "dev-plugins"), ControlPlugins.Legacy);
        }
    }

    private static ControlPluginBuild ControlPluginAt(string devPlugins, string name) =>
        new(name,
            Path.Combine(devPlugins, name, name + ".sln"),
            Path.Combine(devPlugins, name, name, "bin", "Release", name + ".dll"));

    /// <summary>The control plugin's solution, named in the build-it-first warning.</summary>
    public string PluginSln => ControlPlugin.Sln;

    /// <summary>The control plugin's build output that gets deployed into every tree.</summary>
    public string PluginDll => ControlPlugin.Dll;

    /// <summary>
    /// Every distinct root the registry records, in registry order.
    /// </summary>
    /// <remarks>
    /// CLIENT-007. The shared session libraries take ONE instance root, so a rig split
    /// across two roots (only reachable by moving one instance) had its orphan scan and its
    /// config re-copy watching just the first. Exposing the whole set lets a caller point
    /// the scan at each in turn instead of silently covering one.
    /// </remarks>
    public IReadOnlyList<string> RecordedRoots()
    {
        var seen = new List<string>();
        foreach (var entry in _registry.Read())
        {
            var root = entry.RecordedRoot;
            if (root.Length == 0) continue;
            if (seen.Contains(root, StringComparer.OrdinalIgnoreCase)) continue;
            seen.Add(root);
        }
        return seen;
    }

    /// <summary>
    /// The single root the shared session libraries should be pointed at.
    /// </summary>
    /// <remarks>
    /// A RECORDED root wins over the launcher default here, and only here: a rig whose
    /// instances were built under an explicit root has its trees there whether or not this
    /// shell happens to have the environment variable set. <see cref="InstancesDir"/>
    /// itself is deliberately NOT overwritten (CLIENT-008), so the notice an entry with no
    /// recorded root prints still names the real source.
    /// </remarks>
    public string LibraryInstanceRoot()
    {
        if (InstancesRootTyped) return InstancesDir;
        var recorded = RecordedRoots();
        return recorded.Count >= 1 ? recorded[0] : InstancesDir;
    }

    // ---- root resolution ---------------------------------------------------

    /// <summary>Resolves an instance's root, looking its entry up from the registry.</summary>
    public ResolvedRoot ResolveRoot(string name) => ResolveRootCore(name, _registry.Find(name));

    /// <summary>
    /// Resolves an instance's root from an entry the caller already has.
    /// </summary>
    /// <remarks>
    /// An optimisation, not a second code path (CLIENT-031). The overload set preserves the
    /// PowerShell's three-way distinction: looked up, supplied (even as null), or overridden
    /// at provision time. Collapsing them makes every path resolution re-read
    /// <c>rig.json</c>.
    /// </remarks>
    public ResolvedRoot ResolveRoot(string name, InstanceEntry? entry) => ResolveRootCore(name, entry);

    private ResolvedRoot ResolveRootCore(string name, InstanceEntry? entry)
    {
        if (InstancesRootTyped)
        {
            return new ResolvedRoot(InstancesDir, "--instances-root (typed on this command)");
        }

        var recorded = entry?.RecordedRoot ?? "";
        if (recorded.Length > 0)
        {
            return new ResolvedRoot(recorded, "recorded in the registry at provision time");
        }

        // An entry that predates the recorded-root field gets a note rather than a throw:
        // an old rig must keep working. One notice per instance name, ever (CLIENT-028).
        if (entry is not null && _fallbackAnnounced.Add(name))
        {
            _output.Line(OutputLevel.Info,
                $"[Rig] Instance '{name}' was provisioned before the instances root was recorded; using "
                + $"{InstancesDirSource} ({InstancesDir}). Re-record it with: testrig create --target {name} "
                + "--force --as <id>");
        }

        return new ResolvedRoot(InstancesDir, InstancesDirSource);
    }

    // ---- paths -------------------------------------------------------------

    /// <summary>Every path an instance owns, resolving its root from the registry.</summary>
    public InstancePaths PathsFor(string name) => Build(name, ResolveRoot(name));

    /// <summary>Every path an instance owns, using an entry the caller already has.</summary>
    public InstancePaths PathsFor(string name, InstanceEntry? entry) => Build(name, ResolveRoot(name, entry));

    /// <summary>
    /// Every path an instance owns, in a root named explicitly.
    /// </summary>
    /// <remarks>
    /// Used exactly once, at provision time, before an entry exists (CLIENT-030).
    /// </remarks>
    public InstancePaths PathsInRoot(string name, string root) =>
        Build(name, new ResolvedRoot(root, "this provision"));

    private InstancePaths Build(string name, ResolvedRoot root)
    {
        var tree = Path.Combine(root.Root, name);
        var data = _paths.InstanceDataDir(name);

        return new InstancePaths(
            Name: name,
            Tree: tree,
            Exe: Path.Combine(tree, "rocketstation.exe"),
            BepInEx: Path.Combine(tree, "BepInEx"),
            Root: root.Root,
            RootSource: root.Source,
            Data: data,
            Manifest: _paths.InstanceManifest(name),
            PidFile: _paths.InstancePidFile(name),
            Settings: Path.Combine(data, "setting.xml"),
            UserData: _paths.InstanceUserData(name),
            LogDir: _paths.InstanceLogDir(name));
    }

    // ---- the volume guard (CLIENT-011, CLIENT-012) -------------------------

    /// <summary>
    /// Refuses an instances root on a different volume from the game install.
    /// </summary>
    /// <remarks>
    /// Hard links cannot cross volumes. Without this the failure is either a seven gigabyte
    /// real copy or an opaque link error part way through a 1,050 file tree.
    /// </remarks>
    /// <exception cref="RigRefusalException">The two roots are on different volumes.</exception>
    public static void AssertSameVolume(string gameInstall, string instancesRoot)
    {
        var installRoot = Path.GetPathRoot(Path.GetFullPath(gameInstall)) ?? "";
        var targetRoot = Path.GetPathRoot(Path.GetFullPath(instancesRoot)) ?? "";

        if (string.Equals(installRoot, targetRoot, StringComparison.OrdinalIgnoreCase)) return;

        throw new RigRefusalException(
            RigRefusalKind.Refused,
            "Instance trees must be on the same NTFS volume as the game install, because hard links cannot "
            + "cross volumes and a real copy would cost about 7 GB per instance.\n"
            + $"    game install      : {installRoot}\n"
            + $"    instances would be: {targetRoot}  ({instancesRoot})\n"
            + $"Point the rig at a folder on '{installRoot}':\n"
            + $"    $env:STATIONEERS_CLIENTRIG_ROOT = '{installRoot}StationeersRig'\n"
            + $"or pass --instances-root '{installRoot}StationeersRig' on every call. Record the choice in DEV.md.");
    }

    /// <summary>Convenience for the ambient install path.</summary>
    public void AssertSameVolumeAsInstall(string instancesRoot) =>
        AssertSameVolume(_env.StationeersPath(), instancesRoot);

    /// <summary>Whether an instance's game process is alive, by verified pid.</summary>
    public bool IsRunning(IProcessTable processes, InstancePaths paths) =>
        PidFiles.ClientAlive(_fs, processes, paths.PidFile);
}
