using TestRig.Core.Rig;
using TestRig.Playtest.Attestation;
using TestRig.Playtest.Evidence;
using TestRig.Playtest.Flakes;
using TestRig.Playtest.Model;
using TestRig.Playtest.Runner;
using TestRig.Tests.Session.Fakes;

namespace TestRig.Tests.Playtest.Fakes;

/// <summary>
///     A whole harness, wired to fakes, with nothing real underneath except the decisions.
/// </summary>
/// <remarks>
///     The clock and the sleeper are injected, so a 300 second barrier and a 10 second retry
///     gap are exercised exactly as they run for real and cost nothing. Time genuinely moves,
///     which is what makes a deadline test honest: the barrier expires because time passed,
///     not because the assertion tolerates either answer.
/// </remarks>
public sealed class PlaytestFixture
{
    public const string RigHomePath = @"C:\rig";

    public const string Tier1Path = @"C:\Users\dev\Documents\My Games\Stationeers\saves";

    public const string EvidencePath = @"C:\work\playtest";

    public const string RepoRoot = @"C:\repo";

    public PlaytestFixture()
    {
        Files.AddDirectory(RigHomePath);
        Files.AddDirectory(EvidencePath);
        Files.AddDirectory(Tier1Path);
    }

    public FakeFileSystem Files { get; } = new();

    public FakeClock Clock { get; } = new();

    public FakeRigTransport Transport { get; } = new();

    public FakeRigLauncher Launcher { get; } = new();

    public FakeRigRegistry Registry { get; } = new();

    public FakeLogFiles LogFiles { get; } = new();

    public List<string> Log { get; } = [];

    public string Tier1SaveRoot { get; set; } = Tier1Path;

    private FakeSleeper? _sleeper;

    /// <summary>The injected sleeper, so a test can see what the harness waited for.</summary>
    public FakeSleeper Sleeper => _sleeper ??= new FakeSleeper(Clock);

    public PlaytestDependencies Dependencies => new()
    {
        Transport = Transport,
        Launcher = Launcher,
        Registry = Registry,
        Files = Files,
        LogFiles = LogFiles,
        Clock = Clock,
        Sleeper = Sleeper,
        RigHome = RigHomePath,
        Tier1SaveRoot = Tier1SaveRoot,
        Log = Log.Add,
    };

    /// <summary>
    ///     The mod the fixture's instances are provisioned to test unless a test says otherwise.
    /// </summary>
    /// <remarks>
    ///     Every check in this suite is about SprayPaintPlus, because attestation derives the
    ///     mod from the check's own file path and <see cref="SeedMod"/> writes that path.
    /// </remarks>
    public const string DefaultModUnderTest = "SprayPaintPlus";

    /// <summary>Registers an instance in the registry and in the fake control plane.</summary>
    /// <param name="underTest">
    ///     The mods this instance is provisioned to test. The harness refuses before bring-up
    ///     when a check's mod is not in this set, so a fixture leaving it empty would make
    ///     every check inconclusive for a reason that has nothing to do with what it asserts.
    /// </param>
    public PlaytestFixture WithInstance(
        string name, int port, string role = "client", IReadOnlyList<string>? underTest = null)
    {
        Registry.Add(name, port, role, @"E:\rig\instances", underTest ?? [DefaultModUnderTest]);
        Transport.Add(name, port);
        return this;
    }

    /// <summary>A context for a check, with an evidence folder unless told otherwise.</summary>
    public PlaytestContext Context(CheckSpec spec, bool withEvidence = true, string owner = "a1b2c3")
    {
        CheckEvidence? evidence = null;
        if (withEvidence)
        {
            var bundle = new EvidenceBundle(Files, EvidencePath, "tests", Clock.UtcNow);
            evidence = bundle.NewCheck(1, spec.Name);
        }

        return new PlaytestContext(Dependencies, spec, new FlakeCatalogue(), evidence, owner);
    }

    /// <summary>
    ///     Lays down everything attestation needs to pass for a mod, on every instance.
    /// </summary>
    /// <remarks>
    ///     The deployed bytes are the build's bytes, because the comparison is a content hash.
    ///     A test that wants a stale build writes different bytes; a test that wants to prove
    ///     the length-only defect is gone writes DIFFERENT bytes of the SAME length.
    /// </remarks>
    public string SeedMod(string modName, string guid, byte[] build, IReadOnlyList<string> instances, byte[]? deployed = null, bool liveConfig = true)
    {
        var checkFile = Path.Combine(RepoRoot, "Mods", modName, "playtests", modName + "Check.cs");
        var modRoot = Path.Combine(RepoRoot, "Mods", modName, modName);

        Files.AddDirectory(Path.Combine(modRoot, "About"));
        Files.AddFile(Path.Combine(modRoot, "About", "About.xml"),
            $"<ModMetadata>\n  <Name>{modName}</Name>\n  <ModID>{guid}</ModID>\n</ModMetadata>");

        Files.AddDirectory(Path.Combine(modRoot, "bin", "Release"));
        Files.AddFile(Path.Combine(modRoot, "bin", "Release", modName + ".dll"), build);

        foreach (var instance in instances)
        {
            var data = BinaryAttestation.InstanceDataFolder(RigHomePath, instance);
            Files.AddDirectory(data);
            Files.AddFile(Path.Combine(data, BinaryAttestation.ProvisionStampName),
                $"{{\"instanceName\":\"{instance}\",\"role\":\"client\"}}");

            // Local_<Mod>, through Core's own helper: the rig deploys there, so a fixture
            // seeding the unprefixed path would make attestation pass against a layout no real
            // instance has. That is exactly how the wrong deployed path stayed green.
            var deployedPath = Path.Combine(data, LaunchPadMods.DeployedRelativeDll(modName));
            Files.AddDirectory(Path.GetDirectoryName(deployedPath)!);
            Files.AddFile(deployedPath, deployed ?? build);

            // The live half: the running process must report configuration for the guid.
            if (liveConfig) Transport.State(instance).SetConfig(guid, "Client - Group", "Key", "true");
        }

        return checkFile;
    }
}

/// <summary>A check whose body is whatever the test says it is.</summary>
public sealed class TestCheck : IPlaytestCheck
{
    private readonly Action<IPlaytestContext> _body;

    public TestCheck(CheckSpec spec, Action<IPlaytestContext> body)
    {
        Spec = spec;
        _body = body;
    }

    public CheckSpec Spec { get; }

    public void Run(IPlaytestContext context) => _body(context);
}
