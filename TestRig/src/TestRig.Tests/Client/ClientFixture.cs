using TestRig.Contracts;
using TestRig.Core.Abstractions;
using TestRig.Core.Client;
using TestRig.Core.Rig;
using TestRig.Tests.Session.Fakes;

namespace TestRig.Tests.Client;

/// <summary>The machine's ambient values, scripted.</summary>
public sealed class FakeAmbient : IAmbient
{
    public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string MyDocuments { get; set; } = @"C:\Users\dev\Documents";

    public string MachineName { get; set; } = "RIGTEST";

    public string? GetVariable(string name) => Variables.TryGetValue(name, out var value) ? value : null;
}

/// <summary>One scripted control-plane answer.</summary>
public sealed record ScriptedAnswer(int Status, string? Body, string? TransportError = null)
{
    public static ScriptedAnswer Ok(string body) => new(RigStatus.Ok, body);

    public static ScriptedAnswer Refused(string body) => new(RigStatus.Refused, body);

    public static ScriptedAnswer Silent(string reason = "connection refused") => new(0, null, reason);
}

/// <summary>
/// A control-plane transport a test scripts by (port, path).
/// </summary>
/// <remarks>
/// It answers with BYTES, exactly as the real transport does, so the typed layer above it
/// deserialises the same way in the suite as it does against a live plugin. The PowerShell
/// suite faked this one level higher, with a script block returning already-parsed objects
/// whose field names nobody checked against the plugin: 399 assertions passed against a
/// shape the plugin has never emitted.
/// </remarks>
public sealed class FakeControlTransport : IControlTransport
{
    private readonly Dictionary<string, Queue<ScriptedAnswer>> _script = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ScriptedAnswer> _standing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every request, in order, so a test can assert on what was actually sent.</summary>
    public List<(int Port, string Path, string? Body, TimeSpan Timeout)> Sent { get; } = [];

    /// <summary>Ports with no scripted answer at all behave as if nothing is listening.</summary>
    public ScriptedAnswer Default { get; set; } = ScriptedAnswer.Silent();

    private static string Key(int port, string path) => $"{port} {Endpoints.Normalize(path.Split('?')[0])}";

    /// <summary>Answers every request to this endpoint the same way.</summary>
    public FakeControlTransport Standing(int port, string path, ScriptedAnswer answer)
    {
        _standing[Key(port, path)] = answer;
        return this;
    }

    /// <summary>Answers the next request to this endpoint, once.</summary>
    public FakeControlTransport Once(int port, string path, ScriptedAnswer answer)
    {
        var key = Key(port, path);
        if (!_script.TryGetValue(key, out var queue)) _script[key] = queue = new Queue<ScriptedAnswer>();
        queue.Enqueue(answer);
        return this;
    }

    public Task<ControlAnswer> SendAsync(
        int port, string path, string? bodyJson, TimeSpan timeout, CancellationToken ct = default)
    {
        Sent.Add((port, path, bodyJson, timeout));

        var key = Key(port, path);
        var answer = _script.TryGetValue(key, out var queue) && queue.Count > 0
            ? queue.Dequeue()
            : _standing.TryGetValue(key, out var standing) ? standing : Default;

        return Task.FromResult(new ControlAnswer(answer.Status, answer.Body, answer.TransportError));
    }
}

/// <summary>An instance launcher that records instead of launching.</summary>
public sealed class FakeInstanceLauncher : IInstanceLauncher
{
    public List<InstanceLaunch> Launches { get; } = [];

    public List<string> DesktopsEnsured { get; } = [];

    /// <summary>The pid handed back, incremented per launch.</summary>
    public int NextPid { get; set; } = 5000;

    /// <summary>Set to have the launch report a process that is not actually in the table.</summary>
    public bool RegisterInProcessTable { get; set; } = true;

    public FakeProcessTable? Processes { get; set; }

    public FakeClock? Clock { get; set; }

    public void EnsureDesktop(string desktopName) => DesktopsEnsured.Add(desktopName);

    public uint Start(InstanceLaunch launch)
    {
        Launches.Add(launch);
        var pid = NextPid++;

        if (RegisterInProcessTable && Processes is not null)
        {
            Processes.Add(pid, "rocketstation", Clock?.UtcNow ?? DateTimeOffset.UtcNow);
        }

        return (uint)pid;
    }
}

/// <summary>
/// A whole client half, wired from fakes, entirely in memory.
/// </summary>
/// <remarks>
/// Nothing here can reach a real disk, a real process or a real socket. The developer's
/// actual rig is unreachable by construction rather than by a guard that could be forgotten.
/// </remarks>
public sealed class ClientFixture
{
    /// <param name="ambientInstancesRoot">
    /// What <c>STATIONEERS_CLIENTRIG_ROOT</c> holds in this fixture's environment, or null to
    /// leave it UNSET.
    /// </param>
    /// <remarks>
    /// The unset case has to be reachable, and it was not: every fixture set the variable, so
    /// the branch where the launcher default is the only ambient answer and an instance's
    /// recorded root has to override it was never exercised offline at all. That branch is
    /// what stands between <c>create --force</c> and relocating a rebuilt instance onto the
    /// default root, orphaning its 1,053 hard-linked files.
    /// </remarks>
    public ClientFixture(string? typedInstancesRoot = null, string? ambientInstancesRoot = InstancesRoot)
    {
        Rig = new RigFixture();

        Ambient = new FakeAmbient();
        // On the SAME volume as the source install, because hard links cannot cross one and
        // the provision refuses rather than falling back to a seven gigabyte copy.
        if (ambientInstancesRoot is not null)
        {
            Ambient.Variables["STATIONEERS_CLIENTRIG_ROOT"] = ambientInstancesRoot;
        }
        Transport = new FakeControlTransport();
        RegistryMutex = new FakeCrossProcessLock { Name = "Global\\StationeersPlus.TestRig.Registry.TEST" };
        Launcher = new FakeInstanceLauncher { Processes = Rig.Processes, Clock = Rig.Clock };

        SeedSourceInstall();

        Env = new RigEnvironment(
            Rig.Fs,
            RigFixture.Home,
            Ambient,
            repoRoot: RepoRoot,
            buildProps: BuildProps,
            steamcmdPath: SteamCmd,
            userDataDir: RigFixture.UserData);

        Registry = new RigRegistry(Rig.Fs, Rig.Output, Rig.Paths, RegistryMutex);
        Layout = new ClientLayout(Rig.Fs, Env, Rig.Paths, Rig.Output, Registry, typedInstancesRoot);
        Control = new ControlPlane(Transport, Rig.Output);
        Mods = new ModBuilds(Rig.Fs, Env);

        Half = new ClientHalf(
            Rig.Fs, Rig.Processes, Rig.Clock, Rig.Sleeper, Rig.Output,
            Rig.Paths, Env, Layout, Registry, Control, Mods, Launcher, Rig.Lock, Rig.Marker);
    }

    public const string RepoRoot = @"C:\rigtest";
    public const string BuildProps = @"C:\rigtest\Directory.Build.props";
    public const string SteamCmd = @"C:\tools\steamcmd.exe";

    /// <summary>
    /// Where instance trees are built.
    /// </summary>
    /// <remarks>
    /// On the same volume as <see cref="RigFixture.SourceInstall"/>, necessarily: hard links
    /// cannot cross one, and the provision refuses rather than falling back to a seven
    /// gigabyte copy. The session fixture's own instance root is on a different volume and
    /// describes a different thing, the reset planner's view, so the two do not have to match.
    /// </remarks>
    public const string InstancesRoot = @"E:\rig-instances";

    public RigFixture Rig { get; }
    public FakeAmbient Ambient { get; }
    public FakeControlTransport Transport { get; }
    public FakeCrossProcessLock RegistryMutex { get; }
    public FakeInstanceLauncher Launcher { get; }
    public RigEnvironment Env { get; }
    public RigRegistry Registry { get; }
    public ClientLayout Layout { get; }
    public ControlPlane Control { get; }
    public ModBuilds Mods { get; }
    public ClientHalf Half { get; }

    public FakeFileSystem Fs => Rig.Fs;
    public RecordingOutput Output => Rig.Output;
    public FakeProcessTable Processes => Rig.Processes;
    public FakeClock Clock => Rig.Clock;

    /// <summary>The owner id of the lease this fixture holds, once one has been taken.</summary>
    public string? Owner { get; private set; }

    /// <summary>
    /// Takes the session lock and returns the owner id every gated call needs.
    /// </summary>
    /// <remarks>
    /// Idempotent: the lock is rig-wide and a second acquisition against a live lease is a
    /// refusal, so a test that has already leased gets the same id back rather than a throw.
    /// </remarks>
    public string Lease(string purpose = "client tests") => Owner ??= Rig.Lease(purpose);

    /// <summary>
    /// A minimal but VALID Stationeers client install, plus the props file that names it.
    /// </summary>
    /// <remarks>
    /// Both install markers are present because the resolver checks both, and a fixture that
    /// satisfied only one would let a regression in that check pass unnoticed.
    /// </remarks>
    private void SeedSourceInstall()
    {
        Fs.AddFile(BuildProps,
            $"""
             <Project>
               <PropertyGroup>
                 <StationeersPath>{RigFixture.SourceInstall}</StationeersPath>
               </PropertyGroup>
             </Project>
             """);

        Fs.AddFile(SteamCmd, "steamcmd");

        Fs.AddFile(Path.Combine(RigFixture.SourceInstall, "rocketstation.exe"), "MZ");
        Fs.AddFile(Path.Combine(RigFixture.SourceInstall, "rocketstation_Data", "Managed", "Assembly-CSharp.dll"), "MZ");
        Fs.AddFile(Path.Combine(RigFixture.SourceInstall, "rocketstation_Data", "app.info"), "company\nproduct");
        Fs.AddFile(Path.Combine(RigFixture.SourceInstall, "rocketstation_Data", "globalgamemanagers"), "unity");
        Fs.AddFile(Path.Combine(RigFixture.SourceInstall, "rocketstation_Data", "StreamingAssets", "version.ini"),
            "UPDATEVERSION=Update 0.2.6428.27798\r\nchangelog\r\n");
        Fs.AddFile(Path.Combine(RigFixture.SourceInstall, "MonoBleedingEdge", "EmbedRuntime", "mono.dll"), "mono");
        Fs.AddFile(Path.Combine(RigFixture.SourceInstall, "BepInEx", "core", "BepInEx.dll"), "bepinex");
        Fs.AddFile(Path.Combine(RigFixture.SourceInstall, "BepInEx", "config", "stationeers.launchpad.cfg"),
            "[General]\r\nSavePathOverride = \r\n");
        Fs.AddFile(Path.Combine(RigFixture.SourceInstall, "BepInEx", "LogOutput.log"), "old developer log");
        Fs.AddFile(Path.Combine(RigFixture.SourceInstall, "BepInEx", "cache", "stale.dat"), "cache");
        Fs.AddFile(Path.Combine(RigFixture.SourceInstall, "doorstop_config.ini"), "[General]");
        Fs.AddFile(Path.Combine(RigFixture.SourceInstall, "winhttp.dll"), "MZ");
        Fs.AddFile(Path.Combine(RigFixture.SourceInstall, "imgui.ini"), "should be skipped");

        // The developer's own mod set, which is a read-only source in both directions.
        Fs.AddFile(Path.Combine(RigFixture.UserData, "modconfig.xml"),
            $"""
             <?xml version="1.0" encoding="utf-8"?>
             <ModConfig>
               <Core Enabled="true"><Path /></Core>
               <Workshop Enabled="true"><Path Value="C:\workshop\2345" /><WorkshopId Value="2345" /></Workshop>
               <Local Enabled="true"><Path Value="{Path.Combine(RigFixture.UserData, "mods", "HandMade")}" /></Local>
             </ModConfig>
             """);
        Fs.AddFile(Path.Combine(RigFixture.UserData, "mods", "HandMade", "About", "About.xml"), "<About />");
        Fs.AddFile(Path.Combine(RigFixture.UserData, "modrepos.xml"), "<repos />");
    }

    /// <summary>
    /// Puts the DEVELOPER'S published copy of a mod in their own folder, and lists it.
    /// </summary>
    /// <remarks>
    /// The state that matters for the under-test rule: this repository builds the mod AND the
    /// developer has a published copy of it installed. Only the instance's recorded set says
    /// which of the two a given instance gets.
    /// </remarks>
    public void AddDeveloperMod(string name, string dll = "the developer's published build")
    {
        var folder = Path.Combine(RigFixture.UserData, "mods", name);
        Fs.AddFile(Path.Combine(folder, "About", "About.xml"), "<About />");
        Fs.AddFile(Path.Combine(folder, name + ".dll"), dll);

        var config = Path.Combine(RigFixture.UserData, "modconfig.xml");
        var entries = ModConfig.Read(Fs, config).ToList();
        entries.Add(ModConfigEntry.Local(folder));
        ModConfig.Write(Fs, config, entries);
    }

    /// <summary>Builds a repository mod so deploy and staleness have something to find.</summary>
    public void AddRepositoryMod(string name, string dll = "build", string? about = "<About />")
    {
        Fs.AddFile(Path.Combine(RepoRoot, "Mods", name, name, "bin", "Release", name + ".dll"), dll);
        if (about is not null)
        {
            Fs.AddFile(Path.Combine(RepoRoot, "Mods", name, name, "About", "About.xml"), about);
        }
    }

    // ---- synchronous wrappers ----------------------------------------------
    //
    // The verbs are async because they drive HTTP and poll. xUnit's analyzer refuses a
    // blocking wait inside a TEST METHOD, and rightly, so the blocking lives here instead:
    // every fake completes synchronously, so nothing can actually deadlock, and the tests
    // stay readable and can use Assert.Throws rather than ThrowsAsync everywhere.

    /// <summary>Creates one instance through the real code path.</summary>
    public InstanceEntry Create(
        string name,
        string owner,
        string? role = null,
        bool seedMods = false,
        IReadOnlyList<string>? underTest = null) =>
        CreateWith(new CreateOptions
        {
            Instance = name,
            CallerId = owner,
            Role = role,
            SeedMods = seedMods,
            UnderTest = underTest,
        });

    public InstanceEntry CreateWith(CreateOptions options) =>
        Half.CreateAsync(options).GetAwaiter().GetResult();

    public void Remove(string instance, string? owner = null, bool force = false) =>
        Half.RemoveAsync(instance, owner, force).GetAwaiter().GetResult();

    public void Start(IReadOnlyList<InstanceEntry> entries, string? owner = null, string desktop = "StationeersRig") =>
        Half.StartAsync(entries, owner, desktop).GetAwaiter().GetResult();

    public void Stop(
        IReadOnlyList<InstanceEntry> entries,
        string? owner = null,
        int teardownSeconds = 0,
        int waitSeconds = 0,
        string? saveName = null,
        bool force = false) =>
        Half.StopAsync(entries, owner, teardownSeconds, waitSeconds, saveName, force).GetAwaiter().GetResult();

    public void Save(IReadOnlyList<InstanceEntry> entries, string? owner = null, string? saveName = null) =>
        SaveFor(entries, owner, saveName);

    public void SaveFor(
        IReadOnlyList<InstanceEntry> entries,
        string? owner = null,
        string? saveName = null,
        int waitSeconds = 0) =>
        Half.SaveAsync(entries, owner, saveName, waitSeconds).GetAwaiter().GetResult();

    public void Wait(
        IReadOnlyList<InstanceEntry> entries,
        string? owner = null,
        ReadinessStage stage = ReadinessStage.Menu,
        int waitSeconds = 0) =>
        Half.WaitAsync(entries, owner, stage, waitSeconds).GetAwaiter().GetResult();

    public void Call(
        IReadOnlyList<InstanceEntry> entries,
        string path,
        string? body = null,
        string? owner = null,
        int timeoutSeconds = 0) =>
        Half.CallAsync(entries, path, body, owner, timeoutSeconds).GetAwaiter().GetResult();

    public string Snapshot(IReadOnlyList<InstanceEntry> entries, string? outFile = null) =>
        Half.SnapshotAsync(entries, outFile).GetAwaiter().GetResult();

    public IReadOnlyList<ClientListRow> List(IReadOnlyList<InstanceEntry> entries) =>
        Half.ListAsync(entries).GetAwaiter().GetResult();

    public void Status(IReadOnlyList<InstanceEntry> entries) =>
        Half.StatusAsync(entries).GetAwaiter().GetResult();

    public void UpdateGame(IReadOnlyList<InstanceEntry> entries, string? owner = null) =>
        Half.UpdateGameAsync(entries, owner).GetAwaiter().GetResult();

    public IReadOnlyList<InstanceRuntime> ClassifyRig() =>
        Half.ClassifyRigAsync().GetAwaiter().GetResult();

    public InstanceRuntime Runtime(InstanceEntry entry) =>
        Half.RuntimeAsync(entry).GetAwaiter().GetResult();

    public bool ReachedStage(int port, ReadinessStage stage) =>
        Control.ReachedStageAsync(port, stage).GetAwaiter().GetResult();
}
