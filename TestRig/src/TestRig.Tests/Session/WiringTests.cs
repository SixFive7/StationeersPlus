using TestRig.Core.Abstractions;
using TestRig.Core.Client;
using TestRig.Core.Rig;
using TestRig.Core.Session;
using TestRig.Tests.Client;
using TestRig.Tests.Session.Fakes;
using Xunit;

namespace TestRig.Tests.Session;

/// <summary>
///     The joins between subsystems that nothing exercised while each one stood alone.
/// </summary>
/// <remarks>
///     Every case here is a wire that was left unconnected, and each one fails in the same
///     silent direction: a probe that reports the wrong scope, a scan watching a tree nothing
///     was ever built in, a reset that blanks a config file the deployed plugin no longer
///     writes. None of them throws; all of them make the rig confidently wrong.
/// </remarks>
public sealed class WiringTests
{
    // ---- orphan scoping ----------------------------------------------------

    /// <summary>
    ///     An untracked game process outside every rig tree is the developer's own client.
    /// </summary>
    /// <remarks>
    ///     Without the image-path resolver a probe answers <see cref="OrphanScope.Unknown"/>
    ///     for it, which is REPORTED as an orphan, and a reported orphan blocks every state
    ///     reset. Unwired, the rig refuses to restore itself whenever the developer has the
    ///     game open.
    /// </remarks>
    [Fact]
    public void TheDevelopersOwnClientIsNotReportedAsAnOrphanWhenScopingIsWired()
    {
        var rig = new RigFixture();
        rig.Processes.Add(7777, rig.Paths.ClientImage, rig.Clock.UtcNow.AddMinutes(-5));
        rig.ImagePaths[7777] = Path.Combine(RigFixture.SourceInstall, "rocketstation.exe");

        Assert.Empty(rig.Busy.FindOrphans());

        // The same process with no path resolver at all cannot be told from rig debris, so it
        // is reported rather than dropped, and the reset it blocks is the one that matters.
        var unwired = new BusyProbe(rig.Fs, rig.Processes, rig.Paths);
        var reported = Assert.Single(unwired.FindOrphans());
        Assert.Equal(OrphanScope.Unknown, reported.Scope);
    }

    /// <summary>
    ///     A rig split across two instance roots reports orphans under BOTH of them.
    /// </summary>
    /// <remarks>
    ///     CLIENT-007. The session libraries took one instance root, so every untracked
    ///     process running out of the second one scoped Foreign and was never reported at all.
    /// </remarks>
    [Fact]
    public void AnOrphanUnderASecondRecordedRootIsStillOurs()
    {
        const string other = @"D:\other-instances";
        var paths = new RigPaths(
            RigFixture.Home, RigFixture.InstancesRoot, RigFixture.SourceInstall, RigFixture.UserData,
            additionalInstanceRoots: [other]);

        var rig = new RigFixture();
        rig.Processes.Add(8888, paths.ClientImage, rig.Clock.UtcNow.AddMinutes(-5));

        var probe = new BusyProbe(rig.Fs, rig.Processes, paths, _ => Path.Combine(other, "joiner", "rocketstation.exe"));

        var orphan = Assert.Single(probe.FindOrphans());
        Assert.Equal(OrphanScope.Rig, orphan.Scope);
    }

    [Fact]
    public void AnAdditionalRootThatRepeatsThePrimaryOneIsNotCountedTwice()
    {
        var paths = new RigPaths(
            RigFixture.Home, RigFixture.InstancesRoot, additionalInstanceRoots: [RigFixture.InstancesRoot, "", "  "]);

        Assert.Empty(paths.AdditionalInstanceRoots);
        Assert.Equal([RigFixture.InstancesRoot], paths.AllInstanceRoots);
    }

    // ---- the rig's own config files ---------------------------------------

    /// <summary>
    ///     The reset blanks the armed scenario in the MERGED plugin's config as well.
    /// </summary>
    /// <remarks>
    ///     The merge renamed the file: <c>ScenarioRunner</c> wrote
    ///     <c>net.scenariorunner.cfg</c> and the plugin that replaces it writes
    ///     <c>net.sixfive7.testrig.cfg</c>. A reset that knew only the old name would leave a
    ///     probe armed, and an armed probe injects itself into an unrelated test's log with a
    ///     line that reads as entirely plausible.
    /// </remarks>
    [Theory]
    [InlineData(RigConfigFiles.ScenarioRunner)]
    [InlineData(RigConfigFiles.TestRig)]
    public void TheResetBlanksAnArmedScenarioInEitherPluginsConfig(string leaf)
    {
        var rig = new RigFixture();
        var cfg = Path.Combine(rig.Paths.DediInstall, "BepInEx", "config", leaf);
        rig.Fs.AddFile(cfg, "[General]\r\nScenario = sun-noon\r\nOther = keep\r\n");

        var blank = rig.Planner.Build().Actions.Single(a =>
            a.Kind == ResetActionKind.BlankSetting && a.Path.EndsWith(leaf, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(RigConfigFiles.ScenarioSetting, blank.Setting);
        Assert.Contains("sun-noon", blank.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void ABlankScenarioValueNeedsNoAction()
    {
        var rig = new RigFixture();
        rig.Fs.AddFile(
            Path.Combine(rig.Paths.DediInstall, "BepInEx", "config", RigConfigFiles.TestRig),
            "[General]\r\nScenario = \r\n");

        Assert.DoesNotContain(
            rig.Planner.Build().Actions,
            a => a.Kind == ResetActionKind.BlankSetting);
    }

    [Fact]
    public void EveryConfigTheRigsOwnPluginsWriteIsNamedInOnePlace()
    {
        Assert.Equal(
            [RigConfigFiles.ClientDriver, RigConfigFiles.ScenarioRunner, RigConfigFiles.TestRig],
            RigConfigFiles.All);

        Assert.True(RigConfigFiles.CarriesScenario(@"C:\x\BepInEx\config\NET.SIXFIVE7.TESTRIG.CFG"));
        Assert.False(RigConfigFiles.CarriesScenario(@"C:\x\BepInEx\config\net.clientdriver.cfg"));
        Assert.True(RigConfigFiles.IsRigOwned(@"C:\x\net.clientdriver.cfg"));
        Assert.False(RigConfigFiles.IsRigOwned(@"C:\x\net.inspectorplus.cfg"));
    }

    // ---- deploy target resolution -----------------------------------------

    /// <summary>
    ///     <c>TestRig/dev-plugins/</c> is searched, so the merged plugin can be deployed.
    /// </summary>
    /// <remarks>
    ///     It replaces one plugin in each half's own folder and shares a name with neither, so
    ///     it sits above both. It was missing from the search entirely, which is why nothing
    ///     could deploy it at all.
    /// </remarks>
    [Fact]
    public void TheMergedPluginIsFoundUnderTheRigsOwnDevPlugins()
    {
        var fixture = new ClientFixture();
        fixture.Fs.AddFile(
            Path.Combine(RigFixture.Home, "dev-plugins", "TestRig", "TestRig", "bin", "Release", "TestRig.dll"),
            "build");

        var build = fixture.Mods.Find("TestRig");

        Assert.NotNull(build);
        Assert.Equal(ModKind.DevPluginRig, build!.Kind);
        Assert.True(build.IsControlPlane);

        // It carries an About.xml, so on the dedicated server it takes StationeersLaunchPad's
        // load path rather than the Chainloader's, exactly as ScenarioRunner did.
        Assert.Equal(LoadPath.LaunchPad, build.LoadPathOn(RigHalf.Server));
    }

    /// <summary>
    ///     The control plugin an instance is built with is RESOLVED, not named in code.
    /// </summary>
    /// <remarks>
    ///     The client half hardcoded <c>ClientDriver.sln</c> and <c>ClientDriver.dll</c>, which
    ///     is why the merged plugin could never reach an instance. Hardcoding the new name
    ///     instead would strand every rig that has not built it yet, so both trees are
    ///     honoured and the merged one wins when its build exists.
    /// </remarks>
    [Fact]
    public void TheMergedPluginWinsOverClientDriverWhenItsBuildExists()
    {
        var fixture = new ClientFixture();
        var clientDriver = Path.Combine(
            RigFixture.Home, "ClientRig", "dev-plugins", "ClientDriver", "ClientDriver", "bin", "Release",
            "ClientDriver.dll");
        fixture.Fs.AddFile(clientDriver, "old");

        Assert.Equal("ClientDriver", fixture.Layout.ControlPlugin.Name);
        Assert.Equal(clientDriver, fixture.Layout.PluginDll);

        var merged = Path.Combine(
            RigFixture.Home, "dev-plugins", "TestRig", "TestRig", "bin", "Release", "TestRig.dll");
        fixture.Fs.AddFile(merged, "new");

        Assert.Equal("TestRig", fixture.Layout.ControlPlugin.Name);
        Assert.Equal(merged, fixture.Layout.PluginDll);
    }

    /// <summary>The instance's plugin folder is named after whichever one was deployed.</summary>
    [Fact]
    public void CreateDeploysTheResolvedControlPluginUnderItsOwnName()
    {
        var fixture = new ClientFixture();
        fixture.Fs.AddFile(
            Path.Combine(RigFixture.Home, "dev-plugins", "TestRig", "TestRig", "bin", "Release", "TestRig.dll"),
            "build");

        var owner = fixture.Lease();
        fixture.Create("hostie", owner);

        var deployed = Path.Combine(
            ClientFixture.InstancesRoot, "hostie", "BepInEx", "plugins", "TestRig", "TestRig.dll");
        Assert.True(fixture.Fs.FileExists(deployed), fixture.Output.All);
    }

    // ---- the supersession set ----------------------------------------------

    /// <summary>
    ///     The set covers all three rig plugins, and it is symmetric.
    /// </summary>
    /// <remarks>
    ///     <c>ScenarioRunner</c> was absent, so deploying the merged plugin swept
    ///     <c>ClientDriver</c> and left the scenario dispatcher loading beside it.
    /// </remarks>
    [Fact]
    public void EveryRigPluginSupersedesEveryOtherOne()
    {
        Assert.Equal(["TestRig", "ClientDriver", "ScenarioRunner"], ControlPlugins.Names);

        Assert.Equal(["ClientDriver", "ScenarioRunner"], ControlPlugins.Superseded("TestRig"));
        Assert.Equal(["TestRig", "ClientDriver"], ControlPlugins.Superseded("ScenarioRunner"));
        Assert.Equal(["TestRig", "ScenarioRunner"], ControlPlugins.Superseded("ClientDriver"));

        // Case-insensitively, because these are folder names on NTFS.
        Assert.Equal(["ClientDriver", "ScenarioRunner"], ControlPlugins.Superseded("testrig"));
    }

    /// <summary>
    ///     Sweeping is a question about the NAME; the client's Chainloader path is not.
    /// </summary>
    /// <remarks>
    ///     <c>IsControlPlane</c> used to answer both, and because <c>ScenarioRunner</c> lives
    ///     under the dedicated server's own <c>dev-plugins/</c> it answered false, so the
    ///     sweep never saw it. Merging the two questions instead would have moved
    ///     <c>ScenarioRunner</c> onto a client instance's Chainloader path, which is where the
    ///     client's control plane has to be and nothing else may go.
    /// </remarks>
    [Fact]
    public void ScenarioRunnerIsARigPluginButNotTheClientsControlPlane()
    {
        var fixture = new ClientFixture();
        fixture.Fs.AddFile(
            Path.Combine(
                RigFixture.Home, "DedicatedServer", "dev-plugins", "ScenarioRunner", "ScenarioRunner",
                "bin", "Release", "ScenarioRunner.dll"),
            "build");

        var build = fixture.Mods.Find("ScenarioRunner");

        Assert.NotNull(build);
        Assert.Equal(ModKind.DevPluginServer, build!.Kind);
        Assert.True(build.IsRigPlugin);
        Assert.False(build.IsControlPlane);
        Assert.Equal(LoadPath.LaunchPad, build.LoadPathOn(RigHalf.Client));
        Assert.Equal(LoadPath.LaunchPad, build.LoadPathOn(RigHalf.Server));
    }

    /// <summary>A mod under test is not a rig plugin, whatever it is called.</summary>
    [Fact]
    public void AModUnderTestIsNeverSweptAsASupersededRigPlugin()
    {
        var fixture = new ClientFixture();
        fixture.Fs.AddFile(
            Path.Combine(ClientFixture.RepoRoot, "Mods", "SprayPaintPlus", "SprayPaintPlus", "bin", "Release",
                "SprayPaintPlus.dll"),
            "build");

        var build = fixture.Mods.Find("SprayPaintPlus");

        Assert.NotNull(build);
        Assert.False(build!.IsRigPlugin);
        Assert.False(build.IsControlPlane);
    }

    // ---- instance names are case-insensitive -------------------------------

    /// <summary>
    ///     An instance name is a directory name, so it is matched the way NTFS matches one.
    /// </summary>
    /// <remarks>
    ///     The target resolver has always compared case-insensitively, matching PowerShell's
    ///     <c>-eq</c>. An ordinal comparison one layer down made <c>--target HOSTIE</c>
    ///     resolve at the launcher and then fail to resolve in the registry, and a create with
    ///     different casing would have written a second entry pointing at the first one's tree.
    /// </remarks>
    [Fact]
    public void TheRegistryMatchesAnInstanceNameCaseInsensitively()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.Create("hostie", owner);

        Assert.NotNull(fixture.Registry.Find("HOSTIE"));
        Assert.Equal("hostie", Assert.Single(fixture.Registry.Entries(["HoStIe"])).InstanceName);
    }

    [Fact]
    public void ARebuildUnderADifferentCasingUpdatesTheEntryRatherThanAddingASecond()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.Create("hostie", owner, role: "host");

        fixture.CreateWith(new CreateOptions { Instance = "HOSTIE", CallerId = owner, Force = true, SeedMods = false });

        var entry = Assert.Single(fixture.Registry.Read());

        // And the role survives, because nothing was typed this time.
        Assert.Equal("host", entry.RoleOr());
    }

    // ---- the filesystem seam's append -------------------------------------

    [Fact]
    public void AppendCreatesTheFileAndThenAddsToIt()
    {
        var fs = new FakeFileSystem();
        fs.AddDirectory(@"C:\bundle");

        fs.AppendAllText(@"C:\bundle\lock.txt", "acquired\n");
        fs.AppendAllText(@"C:\bundle\lock.txt", "released\n");

        Assert.Equal("acquired\nreleased\n", fs.ReadAllText(@"C:\bundle\lock.txt"));
    }

    // ---- the tier-1 save root ----------------------------------------------

    [Fact]
    public void TheUserSaveRootIsNullWhenTheUserDataFolderIsUnknown()
    {
        Assert.Null(new RigPaths(RigFixture.Home).UserSaveRoot);
        Assert.Equal(
            Path.Combine(RigFixture.UserData, "saves"),
            new RigPaths(RigFixture.Home, userDataDir: RigFixture.UserData).UserSaveRoot);
    }

    // ---- exit codes --------------------------------------------------------

    /// <summary>
    ///     One table, so the entry point and the playtest bundle cannot disagree about a code.
    /// </summary>
    [Theory]
    [InlineData(RigRefusalKind.Refused, RigExitCodes.Refused)]
    [InlineData(RigRefusalKind.HeldByAnotherSession, RigExitCodes.LockHeldByOther)]
    [InlineData(RigRefusalKind.NoLockHeld, RigExitCodes.LockNotHeld)]
    [InlineData(RigRefusalKind.RigBusy, RigExitCodes.RigBusy)]
    [InlineData(RigRefusalKind.Broken, RigExitCodes.Failed)]
    public void EveryRefusalKindHasItsOwnExitCode(RigRefusalKind kind, int expected)
    {
        Assert.Equal(expected, RigExitCodes.For(kind));
    }
}
