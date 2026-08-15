using TestRig.Core.Abstractions;
using TestRig.Core.Client;
using TestRig.Core.Infrastructure;
using TestRig.Core.Rig;
using TestRig.Core.Session;
using TestRig.Tests.Infrastructure;
using TestRig.Tests.Session.Fakes;
using Xunit;

namespace TestRig.Tests.Client;

/// <summary>
/// One instance, built on a REAL filesystem, counted by walking the tree.
/// </summary>
/// <remarks>
///     <para>
///     Every other client test runs over <see cref="FakeFileSystem"/>, which is right for
///     decisions and wrong for this one. The question here is not "was the seed function
///     called": it is "how many copies of this mod are on disk, and which one is it". The
///     suite has now three times certified behaviour the rig never had, and each time the
///     assertion was written from the same misunderstanding as the code, so this one counts
///     directories the way StationeersLaunchPad enumerates them.
///     </para>
///     <para>
///     What it exercises for real: <c>SystemFileSystem</c>, the layout, the registry file,
///     the mod seed copying the developer's folder, the modconfig rewrite, and the deploy.
///     What is still faked is only what has no filesystem at all (the clock, the process
///     table, the desktop launcher) and the game install, which is a handful of stub files
///     rather than 1,050 hard links.
///     </para>
/// </remarks>
public sealed class RealInstanceTreeTests : IDisposable
{
    private readonly TempDirectory _temp = new("realtree");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task ExactlyOneCopyOfTheModUnderTestIsOnDiskAndItIsTheDeployedBuild()
    {
        var rig = Build();

        // The developer HAS a published copy installed, and this repository builds a different
        // one. Only the instance's recorded set decides which of the two it gets.
        rig.AddDeveloperMod("SprayPaintPlus", "the developer's published build");
        rig.AddDeveloperMod("EquipmentPlus", "the developer's published EquipmentPlus");
        rig.AddRepositoryMod("SprayPaintPlus", "THIS REPOSITORY'S BUILD");

        var entry = await rig.Half.CreateAsync(new CreateOptions
        {
            Instance = "hostie",
            CallerId = rig.Owner,
            Role = "host",
            UnderTest = ["SprayPaintPlus"],
        }).ConfigureAwait(true);

        rig.Half.Deploy([entry], ["SprayPaintPlus"], rig.Owner);

        var mods = Path.Combine(rig.Paths.ClientDataDir, "hostie", "userdata", "mods");

        // Counted by walking the tree, the way StationeersLaunchPad enumerates it: every
        // folder under mods/ that carries an About.xml is a mod it will load.
        var loadable = Directory
            .EnumerateDirectories(mods)
            .Where(d => File.Exists(Path.Combine(d, "About", "About.xml")))
            .Select(Path.GetFileName)
            .ToList();

        var copies = loadable.Where(d =>
            d!.Equals("SprayPaintPlus", StringComparison.OrdinalIgnoreCase)
            || d.Equals("Local_SprayPaintPlus", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.Single(copies);
        Assert.Equal("Local_SprayPaintPlus", copies[0]);

        // And it is THIS repository's build, byte for byte, not the developer's.
        var deployed = Path.Combine(mods, "Local_SprayPaintPlus", "SprayPaintPlus.dll");
        Assert.True(File.Exists(deployed));
        Assert.Equal("THIS REPOSITORY'S BUILD", File.ReadAllText(deployed));

        // The mod NOT under test is present, once, at the developer's published state. That is
        // the whole reason the set is explicit: this repository carries work in progress for
        // that mod too, and it must not reach an instance testing something else.
        var other = Path.Combine(mods, "EquipmentPlus", "EquipmentPlus.dll");
        Assert.True(File.Exists(other));
        Assert.Equal("the developer's published EquipmentPlus", File.ReadAllText(other));
        Assert.DoesNotContain("Local_EquipmentPlus", loadable);

        // The instance's own modconfig lists exactly one path per mod, and the one for the mod
        // under test is the deployed folder. A second entry is a second load.
        var listed = ModConfig.Read(SystemFileSystem.Instance, Path.Combine(mods, "..", "modconfig.xml"))
            .Select(e => e.Path)
            .Where(path => path.Contains("SprayPaintPlus", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Single(listed);
        Assert.EndsWith("Local_SprayPaintPlus", listed[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARebuildAfterADeployStillLeavesExactlyOneCopyOnceItIsRedeployed()
    {
        // create --force wipes userdata/mods and re-seeds. The set is preserved, so the
        // developer's copy still does not come back, and the instance is left with NO copy
        // until the re-deploy: no copy is a state attestation reports, two copies is not.
        var rig = Build();
        rig.AddDeveloperMod("SprayPaintPlus", "the developer's published build");
        rig.AddRepositoryMod("SprayPaintPlus", "THIS REPOSITORY'S BUILD");

        var entry = await rig.Half.CreateAsync(new CreateOptions
        {
            Instance = "hostie", CallerId = rig.Owner, UnderTest = ["SprayPaintPlus"],
        }).ConfigureAwait(true);
        rig.Half.Deploy([entry], ["SprayPaintPlus"], rig.Owner);

        var rebuilt = await rig.Half.CreateAsync(new CreateOptions
        {
            Instance = "hostie", CallerId = rig.Owner, Force = true,
        }).ConfigureAwait(true);

        var mods = Path.Combine(rig.Paths.ClientDataDir, "hostie", "userdata", "mods");
        Assert.Equal(["SprayPaintPlus"], rebuilt.UnderTestMods);
        Assert.False(Directory.Exists(Path.Combine(mods, "SprayPaintPlus")));
        Assert.False(Directory.Exists(Path.Combine(mods, "Local_SprayPaintPlus")));

        rig.Half.Deploy([rebuilt], ["SprayPaintPlus"], rig.Owner);

        Assert.False(Directory.Exists(Path.Combine(mods, "SprayPaintPlus")));
        Assert.Equal(
            "THIS REPOSITORY'S BUILD",
            File.ReadAllText(Path.Combine(mods, "Local_SprayPaintPlus", "SprayPaintPlus.dll")));
    }

    // ---- the composition, over a real filesystem ---------------------------

    private RealRig Build() => new(_temp.Path);

    /// <summary>A client half over <see cref="SystemFileSystem"/> in a temp tree.</summary>
    private sealed class RealRig
    {
        public RealRig(string root)
        {
            var fs = SystemFileSystem.Instance;
            var home = Path.Combine(root, "TestRig");
            var repoRoot = root;
            UserData = Path.Combine(root, "userdata");
            var install = Path.Combine(root, "Stationeers");

            // The smallest install that gets past the resolver's own markers. The tree build
            // hard-links whatever is there, so a handful of stub files is a whole install as
            // far as this test is concerned.
            Directory.CreateDirectory(Path.Combine(install, "rocketstation_Data", "Managed"));
            Directory.CreateDirectory(Path.Combine(install, "rocketstation_Data", "StreamingAssets"));
            Directory.CreateDirectory(Path.Combine(install, "BepInEx", "core"));
            Directory.CreateDirectory(Path.Combine(install, "BepInEx", "config"));
            File.WriteAllText(Path.Combine(install, "rocketstation.exe"), "MZ");
            File.WriteAllText(Path.Combine(install, "rocketstation_Data", "Managed", "Assembly-CSharp.dll"), "MZ");
            File.WriteAllText(
                Path.Combine(install, "rocketstation_Data", "StreamingAssets", "version.ini"),
                "UPDATEVERSION=Update 0.2.6428.27798\r\n");
            File.WriteAllText(Path.Combine(install, "BepInEx", "core", "BepInEx.dll"), "MZ");
            File.WriteAllText(
                Path.Combine(install, "BepInEx", "config", "stationeers.launchpad.cfg"),
                "[General]\r\nSavePathOverride = \r\n");

            Directory.CreateDirectory(home);
            Directory.CreateDirectory(UserData);
            File.WriteAllText(
                Path.Combine(UserData, "modconfig.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<ModConfig>\r\n  <Core Enabled=\"true\">\r\n"
                + "    <Path />\r\n  </Core>\r\n</ModConfig>\r\n");

            Paths = new RigPaths(home, Path.Combine(root, "instances"), install, UserData);
            RepoRoot = repoRoot;

            var clock = new FakeClock();
            var output = new RecordingOutput();
            var env = new RigEnvironment(
                fs, home, SystemAmbient.Instance, repoRoot,
                userDataDir: UserData, stationeersPath: install);
            var registry = new RigRegistry(fs, output, Paths, new FakeCrossProcessLock());
            var layout = new ClientLayout(fs, env, Paths, output, registry, null);
            var mods = new ModBuilds(fs, env);
            var processes = new FakeProcessTable();
            var worlds = new WorldScanner(fs, Paths);
            var launcher = new LauncherIdentity(4242, "testrig", "REALTREE");
            var busy = new BusyProbe(fs, processes, Paths, static _ => null);
            var marker = new DirtyMarker(fs, clock, processes, new FakeBootIdentity(), Paths, worlds, launcher);
            var shared = new SharedStateReader(fs, new FakeRegistry(), clock, Paths.SharedDataDir, Paths.PlayerPrefsKey);
            var state = new SessionStateStore(fs, clock, Paths, shared);

            var sessionLock = new SessionLockService(
                fs, clock, new FakeSleeper(clock), new FakeCrossProcessLock(), output, Paths, busy, marker,
                launcher, null, null, state);

            Owner = sessionLock.AcquireAsync(new AcquireOptions
            {
                Purpose = "real instance tree", KeepState = true, Tool = "testrig",
            }).GetAwaiter().GetResult().Owner!;

            Half = new ClientHalf(
                fs, processes, clock, new FakeSleeper(clock), output, Paths, env, layout, registry,
                new ControlPlane(new FakeControlTransport(), output), mods, new FakeInstanceLauncher(),
                sessionLock, marker);
        }

        public ClientHalf Half { get; }

        public RigPaths Paths { get; }

        public string Owner { get; }

        private string UserData { get; }

        private string RepoRoot { get; }

        /// <summary>The developer's published copy of a mod, in their own folder and modconfig.</summary>
        public void AddDeveloperMod(string name, string dll)
        {
            var folder = Path.Combine(UserData, "mods", name);
            Directory.CreateDirectory(Path.Combine(folder, "About"));
            File.WriteAllText(Path.Combine(folder, "About", "About.xml"), "<About />");
            File.WriteAllText(Path.Combine(folder, name + ".dll"), dll);

            var config = Path.Combine(UserData, "modconfig.xml");
            var entries = ModConfig.Read(SystemFileSystem.Instance, config).ToList();
            entries.Add(ModConfigEntry.Local(folder));
            ModConfig.Write(SystemFileSystem.Instance, config, entries);
        }

        /// <summary>This repository's build of a mod.</summary>
        public void AddRepositoryMod(string name, string dll)
        {
            var root = Path.Combine(RepoRoot, "Mods", name, name);
            Directory.CreateDirectory(Path.Combine(root, "bin", "Release"));
            Directory.CreateDirectory(Path.Combine(root, "About"));
            File.WriteAllText(Path.Combine(root, "About", "About.xml"), "<About />");
            File.WriteAllText(Path.Combine(root, "bin", "Release", name + ".dll"), dll);
        }
    }

    /// <summary>A launcher that starts nothing. No test here runs a game.</summary>
    private sealed class FakeInstanceLauncher : IInstanceLauncher
    {
        public void EnsureDesktop(string desktop)
        {
        }

        public uint Start(InstanceLaunch launch) => 0;
    }
}
