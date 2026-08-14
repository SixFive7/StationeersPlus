using TestRig.Core.Session;

namespace TestRig.Tests.Session.Fakes;

/// <summary>
/// A whole rig, wired from fakes, entirely in memory.
/// </summary>
/// <remarks>
/// The PowerShell suites pointed the real libraries at a temp directory and had to assert
/// that the redirection took, because a mistake there would have driven the developer's
/// actual rig. Nothing here can reach a disk at all: there is no path from these fakes to
/// the filesystem, so <c>TestRig/session.lock</c>, <c>TestRig/session.dirty</c> and the 25
/// real worlds under <c>DedicatedServer/data/saves/</c> are unreachable by construction
/// rather than by a guard that could be forgotten.
/// </remarks>
public sealed class RigFixture
{
    public const string Home = @"C:\rigtest\TestRig";
    public const string InstancesRoot = @"D:\rig-instances";
    public const string SourceInstall = @"E:\Steam\steamapps\common\Stationeers";
    public const string UserData = @"C:\Users\dev\Documents\My Games\Stationeers";

    /// <summary>The per-user folder nothing can isolate. Read at a session boundary, never written.</summary>
    public const string SharedData = @"C:\Users\dev\AppData\LocalLow\Rocketwerkz\rocketstation";

    private int _ownerCounter;

    public RigFixture(bool wireRestore = true)
    {
        Fs = new FakeFileSystem();
        Clock = new FakeClock();
        Sleeper = new FakeSleeper(Clock);
        Processes = new FakeProcessTable();
        Boot = new FakeBootIdentity();
        Mutex = new FakeCrossProcessLock();
        Output = new RecordingOutput();
        Registry = new FakeRegistry();

        Paths = new RigPaths(Home, InstancesRoot, SourceInstall, UserData, sharedDataDir: SharedData);
        Launcher = new LauncherIdentity(4242, "pwsh", "RIGTEST");

        Fs.AddDirectory(Home);
        Fs.AddDirectory(Paths.DediData);
        Fs.AddDirectory(Paths.DediInstall);
        Fs.AddDirectory(Paths.ClientDataDir);

        Worlds = new WorldScanner(Fs, Paths);
        Busy = new BusyProbe(Fs, Processes, Paths, pid => ImagePaths.TryGetValue(pid, out var p) ? p : null);
        Marker = new DirtyMarker(Fs, Clock, Processes, Boot, Paths, Worlds, Launcher);
        Surface = new MutableSurface(Fs, Paths, Worlds);
        Baseline = new BaselineStore(Fs, Clock, Paths, Surface, Output, Launcher);
        SharedState = new SharedStateReader(Fs, Registry, Clock, Paths.SharedDataDir, Paths.PlayerPrefsKey);
        State = new SessionStateStore(Fs, Clock, Paths, SharedState);
        Planner = new ResetPlanner(Fs, Clock, Paths, Surface, Baseline, Worlds, Marker, Busy, State);
        Executor = new ResetExecutor(Fs, Clock, Output, Planner, Marker, State);

        Lock = new SessionLockService(
            Fs, Clock, Sleeper, Mutex, Output, Paths, Busy, Marker, Launcher,
            wireRestore ? Executor : null,
            MintOwnerId,
            State);
    }

    public FakeFileSystem Fs { get; }
    public FakeClock Clock { get; }
    public FakeSleeper Sleeper { get; }
    public FakeProcessTable Processes { get; }
    public FakeBootIdentity Boot { get; }
    public FakeCrossProcessLock Mutex { get; }
    public FakeRegistry Registry { get; }
    public RecordingOutput Output { get; }
    public RigPaths Paths { get; }
    public LauncherIdentity Launcher { get; }

    public WorldScanner Worlds { get; }
    public BusyProbe Busy { get; }
    public DirtyMarker Marker { get; }
    public MutableSurface Surface { get; }
    public BaselineStore Baseline { get; }
    public SharedStateReader SharedState { get; }
    public SessionStateStore State { get; }
    public ResetPlanner Planner { get; }
    public ResetExecutor Executor { get; }
    public SessionLockService Lock { get; }

    /// <summary>pid to executable path, for orphan scoping.</summary>
    public Dictionary<int, string> ImagePaths { get; } = [];

    /// <summary>Deterministic 8-hex owner ids, so a test can name the one it expects.</summary>
    public string MintOwnerId() => $"a000{++_ownerCounter:x4}";

    // ---- world helpers -----------------------------------------------------

    public string AddServerWorld(string name, int fileCount = 1, int bytesEach = 1024)
    {
        var dir = Path.Combine(Paths.ServerSaveRoot, name);
        Fs.AddDirectory(dir);
        for (var i = 0; i < fileCount; i++)
        {
            Fs.AddFile(Path.Combine(dir, $"world{i}.xml"), new string('x', bytesEach));
        }
        return dir;
    }

    public string AddClientWorld(string instance, string name)
    {
        var dir = Path.Combine(Paths.InstanceSaveRoot(instance), name);
        Fs.AddDirectory(dir);
        Fs.AddFile(Path.Combine(dir, "world.xml"), "content");
        return dir;
    }

    // ---- instance helpers --------------------------------------------------

    /// <summary>Provisions an instance: data dir, manifest, tree, BepInEx config.</summary>
    public void AddInstance(string name, string role = "client", bool withTree = true)
    {
        Fs.AddDirectory(Paths.InstanceDataDir(name));
        Fs.AddDirectory(Paths.InstanceUserData(name));
        Fs.AddFile(Paths.InstanceManifest(name), $$"""{"instanceName":"{{name}}","role":"{{role}}"}""");

        if (!withTree) return;

        var tree = Path.Combine(InstancesRoot, name);
        var bep = Path.Combine(tree, "BepInEx");
        Fs.AddDirectory(Path.Combine(bep, "config"));
        Fs.AddFile(Path.Combine(bep, "config", SavePathOverride.ConfigLeaf),
            $"SavePathOverride = {Paths.InstanceUserData(name)}");
    }

    /// <summary>Records where an instance's tree really went, as provisioning does.</summary>
    public void RegisterInstanceRoot(params (string Instance, string Root)[] entries)
    {
        var json = "[" + string.Join(",", entries.Select(e =>
            $$"""{"instanceName":"{{e.Instance}}","instancesRoot":"{{e.Root.Replace("\\", "\\\\")}}"}""")) + "]";
        Fs.AddFile(Paths.ClientRegistryFile, json);
    }

    /// <summary>Makes an instance's game process live, with a pid file that claims it.</summary>
    public void StartInstance(string name, int pid)
    {
        Processes.Add(pid, Paths.ClientImage, Clock.UtcNow.AddMinutes(-1));
        Fs.AddFile(Paths.InstancePidFile(name), pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Makes the dedicated server live, optionally with players connected.</summary>
    public void StartServer(int pid = 9001, int players = 0)
    {
        Processes.Add(pid, Paths.ServerImage, Clock.UtcNow.AddMinutes(-1));
        Fs.AddFile(Paths.ServerPidFile, pid.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var lines = new List<string>();
        for (var i = 0; i < players; i++) lines.Add($"Client Bob{i} (76561198000000{i:D2}) is ready");
        Fs.AddFile(Paths.ServerLog, string.Join("\r\n", lines));
    }

    // ---- lock helpers ------------------------------------------------------

    /// <summary>Writes a lock file by hand, so a test can age it or corrupt it.</summary>
    public void WriteLockFile(
        string owner,
        string purpose = "probe",
        DateTimeOffset? acquiredAt = null,
        DateTimeOffset? refreshedAt = null,
        DateTimeOffset? activeAt = null,
        string? ttl = "10",
        string? ceiling = "60",
        params (string Key, string Value)[] extra)
    {
        var fields = new FieldText();
        fields.Set(LockFields.Owner, owner);
        fields.Set(LockFields.Purpose, purpose);
        fields.Set(LockFields.AcquiredAt, RigTime.Stamp(acquiredAt ?? Clock.UtcNow));
        fields.Set(LockFields.RefreshedAt, RigTime.Stamp(refreshedAt ?? Clock.UtcNow));
        fields.Set(LockFields.ActiveAt, RigTime.Stamp(activeAt ?? Clock.UtcNow));
        if (ttl is not null) fields.Set(LockFields.TtlMinutes, ttl);
        if (ceiling is not null) fields.Set(LockFields.IdleCeilingMinutes, ceiling);
        fields.Set(LockFields.Host, "RIGTEST");
        foreach (var (key, value) in extra) fields.Set(key, value);

        Fs.AddFile(Paths.LockFile, fields.Render(["# test-written lock"]));
    }

    public FieldText? ReadLockFile() => Lock.ReadLock();

    public string LockText() => Fs.ReadAllText(Paths.LockFile);

    public bool LockFileExists() => Fs.FileExists(Paths.LockFile);

    public bool MarkerExists() => Fs.FileExists(Paths.DirtyFile);

    public string MarkerText() => Fs.ReadAllText(Paths.DirtyFile);

    public AcquireOptions Acquire(string purpose = "probe") => new() { Purpose = purpose };

    /// <summary>Acquires and returns the owner id, for the many tests that need one.</summary>
    public string Lease(string purpose = "probe")
    {
        var result = Lock.AcquireAsync(new AcquireOptions { Purpose = purpose }).GetAwaiter().GetResult();
        Output.Clear();
        return result.Owner;
    }
}
