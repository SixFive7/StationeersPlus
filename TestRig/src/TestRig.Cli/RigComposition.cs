using TestRig.Cli.Dispatch;
using TestRig.Core.Abstractions;
using TestRig.Core.Client;
using TestRig.Core.Infrastructure;
using TestRig.Core.Rig;
using TestRig.Core.Server;
using TestRig.Core.Session;

namespace TestRig.Cli;

/// <summary>
/// Where the rig lives, and everything the session subsystem needs to reach it.
/// </summary>
/// <remarks>
/// <para>
/// The composition root. Nothing below this line constructs a seam, and nothing above it
/// knows what a seam is. The four environment overrides mirror
/// <c>Initialize-RigCommon -BuildProps -SteamcmdPath -UserDataDir</c>, which are the
/// injection points that made the PowerShell suite able to run offline; they are the only
/// reason a test can exercise the real lock without touching the real rig.
/// </para>
/// <para>
/// <c>TESTRIG_HOME</c> in particular is load bearing for the suite: it points the whole rig
/// at a throwaway tree, so the tests never take the one real session lock and never see the
/// developer's saves.
/// </para>
/// </remarks>
public sealed class RigComposition : IDisposable
{
    private readonly HttpControlTransport _transport;
    private readonly SystemFileDownloader _downloader;

    private RigComposition(
        RigPaths paths,
        string instancesRootSource,
        string typedInstancesRoot,
        RigEnvironment env,
        RigRegistry registry,
        ClientLayout layout,
        IFileSystem fs,
        IClock clock,
        ISleeper sleeper,
        ICrossProcessLock mutex,
        CapturingOutput output)
    {
        Paths = paths;
        InstancesRootSource = instancesRootSource;
        FileSystem = fs;
        Clock = clock;
        Env = env;
        Registry = registry;
        Layout = layout;
        Recorder = output;

        var launcher = new LauncherIdentity(
            Environment.ProcessId,
            Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "testrig"),
            Environment.MachineName);

        Worlds = new WorldScanner(fs, paths);
        Surface = new MutableSurface(fs, paths, Worlds);

        // Orphan scoping wired. Without it an untracked rocketstation process is scoped
        // Unknown, reported as an orphan, and blocks every reset, so the rig refuses to
        // restore itself whenever the developer has the game open.
        Busy = ProcessImagePaths.Probe(fs, SystemProcessTable.Instance, paths);

        Marker = new DirtyMarker(fs, clock, SystemProcessTable.Instance, BootIdentity.Instance, paths, Worlds, launcher);

        // Read only, and it has no counterpart writer anywhere: this state is shared with the
        // developer's own client and cannot be isolated, so the rig reports what moved instead
        // of pretending it can put it back.
        SharedState = new SharedStateReader(
            fs, SystemRegistry.Instance, clock, paths.SharedDataDir, paths.PlayerPrefsKey);

        State = new SessionStateStore(fs, clock, paths, SharedState);
        Baseline = new BaselineStore(fs, clock, paths, Surface, output, launcher);
        Planner = new ResetPlanner(fs, clock, paths, Surface, Baseline, Worlds, Marker, Busy, State);
        Reset = new ResetExecutor(fs, clock, output, Planner, Marker, State);
        Lock = new SessionLockService(
            fs, clock, sleeper, mutex, output, paths, Busy, Marker, launcher, Reset, mintOwnerId: null, State);

        _transport = new HttpControlTransport();
        _downloader = new SystemFileDownloader();

        var mods = new ModBuilds(fs, env);
        var control = new ControlPlane(_transport, output);

        ClientHalf = new ClientHalf(
            fs, SystemProcessTable.Instance, clock, SystemSleeper.Instance, output,
            paths, env, layout, registry, control, mods, new DesktopInstanceLauncher(), Lock, Marker);

        ServerHalf = new ServerHalf(
            fs, SystemProcessTable.Instance, clock, SystemSleeper.Instance, output,
            paths, env, mods, Lock,
            new SystemServerProcessLauncher(), new SystemSteamCmdRunner(), _downloader, new SystemArchiveExtractor(),
            // The host wrapper re-invokes THIS executable in host mode. Passing a path that
            // is not a real entry point is SERVER-056: the wrapper starts, runs nothing, and
            // the 20-second registration barrier throws with the server never having existed.
            ResolveLauncherPath());

        Clients = new ClientHalfAdapter(ClientHalf, output);
        Server = new ServerHalfAdapter(ServerHalf, output);

        TypedInstancesRoot = typedInstancesRoot;
    }

    public RigPaths Paths { get; }

    /// <summary>Where the instances root came from, printed by the surface so a caller can see it took.</summary>
    public string InstancesRootSource { get; }

    /// <summary><c>--instances-root</c> exactly as typed, or the empty string.</summary>
    public string TypedInstancesRoot { get; }

    public IFileSystem FileSystem { get; }
    public IClock Clock { get; }

    /// <summary>
    /// The sink every half was built with, which can also keep a copy of what it emitted.
    /// </summary>
    /// <remarks>
    /// Wrapping unconditionally rather than only for the playtest verb: the recorder forwards
    /// everything and captures only inside a window somebody opened, so it changes nothing
    /// for any other verb, and a composition that had two shapes would be a composition where
    /// one of them was never exercised.
    /// </remarks>
    public CapturingOutput Recorder { get; }

    /// <summary>The control-plane transport, for a caller building its own client of it.</summary>
    public IControlTransport Transport => _transport;

    public RigEnvironment Env { get; }
    public RigRegistry Registry { get; }
    public ClientLayout Layout { get; }
    public WorldScanner Worlds { get; }
    public MutableSurface Surface { get; }
    public BusyProbe Busy { get; }
    public DirtyMarker Marker { get; }

    /// <summary>The shared per-user state, reported at a session boundary and never restored.</summary>
    public SharedStateReader SharedState { get; }

    public SessionStateStore State { get; }
    public BaselineStore Baseline { get; }
    public ResetPlanner Planner { get; }
    public ResetExecutor Reset { get; }
    public SessionLockService Lock { get; }

    /// <summary>The client half itself, for a caller that needs Core rather than the CLI shape.</summary>
    public ClientHalf ClientHalf { get; }

    /// <summary>The dedicated-server half itself.</summary>
    public ServerHalf ServerHalf { get; }

    /// <summary>The client half behind the dispatcher's synchronous shape.</summary>
    public IClientHalf Clients { get; }

    /// <summary>The dedicated-server half behind the dispatcher's synchronous shape.</summary>
    public IServerHalf Server { get; }

    /// <summary>
    /// Instance names as the REGISTRY records them.
    /// </summary>
    /// <remarks>
    /// Not the data directories <see cref="MutableSurface.InstanceNames"/> lists, because
    /// every client-half verb needs a registry row (a port, a role, a recorded root) and a
    /// bare directory has none. The two agree on a healthy rig; where they differ, the
    /// directory is debris from an interrupted create, which is the reset's business rather
    /// than a target a verb can act on.
    /// </remarks>
    public IReadOnlyList<string> InstanceNames() => Registry.Names();

    public void Dispose()
    {
        _transport.Dispose();
        _downloader.Dispose();
    }

    /// <summary>
    /// The executable the host wrapper re-invokes.
    /// </summary>
    /// <remarks>
    /// <c>Environment.ProcessPath</c> is the running binary, which is exactly right for the
    /// published single file and for a <c>dotnet run</c> alike. It is null only in a hosted
    /// runtime with no process file, where the wrapper could not be started anyway.
    /// </remarks>
    private static string ResolveLauncherPath() => Environment.ProcessPath ?? "testrig";

    /// <summary>
    /// The <c>TestRig/</c> directory: the binary's own folder, or <c>TESTRIG_HOME</c>.
    /// </summary>
    /// <remarks>
    /// Keeps the tree relocatable, exactly as deriving everything from the launcher's own
    /// directory did before. The override exists for the suite, and for nothing else.
    /// </remarks>
    public static string ResolveRigHome()
    {
        var rigHome = Environment.GetEnvironmentVariable("TESTRIG_HOME");
        if (string.IsNullOrWhiteSpace(rigHome)) rigHome = AppContext.BaseDirectory;
        return rigHome.TrimEnd(Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// Builds the whole rig, in two phases, because the instances root is recorded IN the rig.
    /// </summary>
    /// <remarks>
    /// Phase one resolves the launcher's default root, which is enough to find
    /// <c>ClientRig/data/rig.json</c> (that path does not depend on the instances root at
    /// all). Phase two asks <see cref="ClientLayout.LibraryInstanceRoot"/> where the trees
    /// ACTUALLY are and rebuilds the paths object around that answer.
    ///
    /// It matters because the reset and the orphan scan both key off
    /// <see cref="RigPaths.InstanceRoot"/>. A rig built under an explicit root, with no
    /// <c>STATIONEERS_CLIENTRIG_ROOT</c> set in this shell, would otherwise have both of them
    /// watching <c>ClientRig/instances</c>, a folder that has never held anything, and report
    /// a clean rig while the real trees sat untouched on another volume (CLIENT-007,
    /// CLIENT-026).
    /// </remarks>
    public static RigComposition Build(IOutput sink, string instancesRootOverride, ICrossProcessLock mutex)
    {
        var output = sink as CapturingOutput ?? new CapturingOutput(sink);
        var rigHome = ResolveRigHome();
        var (defaultRoot, source) = ResolveInstancesRoot(rigHome, instancesRootOverride);
        var repoRoot = Path.GetDirectoryName(rigHome.TrimEnd(Path.DirectorySeparatorChar));
        var userData = ResolveUserData();
        var fs = SystemFileSystem.Instance;

        // ONE reader for the developer's install, and it is Core's. An earlier arrangement
        // had this file parse Directory.Build.props for the session subsystem while
        // RigEnvironment parsed it again for the halves, which is two implementations of the
        // same lookup that had to agree about validation forever.
        var env = new RigEnvironment(
            fs, rigHome, SystemAmbient.Instance, repoRoot,
            userDataDir: userData,
            stationeersPath: Environment.GetEnvironmentVariable("TESTRIG_STATIONEERS_PATH"));

        var sourceInstall = env.StationeersPathOrNull();
        var provisional = new RigPaths(rigHome, defaultRoot, sourceInstall, userData);

        // A SEPARATE cross-process section from the session lock's. Re-entering the session
        // mutex from inside a gated command would deadlock in the real implementation and
        // throw in the fake, and neither is a useful way to discover the nesting.
        var registry = new RigRegistry(fs, output, provisional, new CrossProcessLock(RigRegistry.MutexName));
        var typed = string.IsNullOrWhiteSpace(instancesRootOverride) ? null : instancesRootOverride;
        var layout = new ClientLayout(fs, env, provisional, output, registry, typed);

        var effectiveRoot = layout.LibraryInstanceRoot();
        var paths = new RigPaths(
            rigHome,
            effectiveRoot,
            sourceInstall,
            userData,
            additionalInstanceRoots: layout.RecordedRoots(),
            sharedDataDir: ResolveSharedData(),
            playerPrefsKey: Environment.GetEnvironmentVariable("TESTRIG_PLAYERPREFSKEY"));

        // Both consumers of the paths object are rebuilt on the final one, so nothing is
        // left holding the provisional root.
        var finalRegistry = new RigRegistry(fs, output, paths, new CrossProcessLock(RigRegistry.MutexName));
        var finalLayout = new ClientLayout(fs, env, paths, output, finalRegistry, typed);

        var reportedSource = string.Equals(effectiveRoot, defaultRoot, StringComparison.OrdinalIgnoreCase)
            ? source
            : "the instances root recorded in rig.json";

        return new RigComposition(
            paths, reportedSource, instancesRootOverride ?? string.Empty, env, finalRegistry, finalLayout,
            fs, SystemClock.Instance, SystemSleeper.Instance, mutex, output);
    }

    /// <summary>
    /// Instances root, in precedence order, each with the string the surface prints.
    /// </summary>
    /// <remarks>
    /// An instance that already exists uses the root recorded when it was provisioned;
    /// this is only the default for a new one.
    /// </remarks>
    public static (string Root, string Source) ResolveInstancesRoot(string rigHome, string typedOverride)
    {
        if (!string.IsNullOrWhiteSpace(typedOverride))
            return (typedOverride, "--instances-root, typed on this command");

        var env = Environment.GetEnvironmentVariable("STATIONEERS_CLIENTRIG_ROOT");
        if (!string.IsNullOrWhiteSpace(env))
            return (env, "the STATIONEERS_CLIENTRIG_ROOT environment variable");

        return (Path.Combine(rigHome, "ClientRig", "instances"), "the default ClientRig/instances folder");
    }

    /// <summary>
    /// The developer's Stationeers user-data folder. Tier 1: read-only from the rig, always.
    /// </summary>
    private static string ResolveUserData()
    {
        var env = Environment.GetEnvironmentVariable("TESTRIG_USERDATA");
        if (!string.IsNullOrWhiteSpace(env)) return env;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "My Games",
            "Stationeers");
    }

    /// <summary>
    /// The per-user folder Unity fixes and nothing can redirect (RESET-009).
    /// </summary>
    /// <remarks>
    /// There is no SpecialFolder for LocalLow, so it is built off the profile directory the
    /// same way the game does. Read only from the rig, and the override exists for the suite.
    /// </remarks>
    private static string ResolveSharedData()
    {
        var env = Environment.GetEnvironmentVariable("TESTRIG_SHAREDDATA");
        if (!string.IsNullOrWhiteSpace(env)) return env;

        var profile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (string.IsNullOrWhiteSpace(profile))
        {
            profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return string.IsNullOrWhiteSpace(profile)
            ? string.Empty
            : Path.Combine(profile, "AppData", "LocalLow", "Rocketwerkz", "rocketstation");
    }
}
