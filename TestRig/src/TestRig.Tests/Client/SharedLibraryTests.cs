using TestRig.Contracts;
using TestRig.Core.Rig;
using TestRig.Core.Session;
using TestRig.Tests.Session.Fakes;
using Xunit;

namespace TestRig.Tests.Client;

/// <summary>
/// The shared library both halves depend on: constants, environment resolution, mod config,
/// mod builds and readiness.
/// </summary>
/// <remarks>
/// Every one of these had two or three implementations in the PowerShell rig and each set
/// had drifted. These assertions exist to make a second implementation impossible to add
/// without breaking something.
/// </remarks>
public sealed class SharedLibraryTests
{
    // ---- constants (COMMON-007 to COMMON-017) ------------------------------

    [Fact]
    public void TheReservedPortTableIsComputedFromTheServerConstantsRatherThanTypedAgain()
    {
        // The whole reason the shared library exists: the client half used to hardcode
        // 28015/28016 in its collision table while the server half declared them
        // independently, so changing one did not change the other.
        Assert.True(RigConstants.ReservedGamePorts.ContainsKey(RigConstants.ServerGamePort));
        Assert.True(RigConstants.ReservedGamePorts.ContainsKey(RigConstants.ServerUpdatePort));
        Assert.True(RigConstants.ReservedGamePorts.ContainsKey(RigConstants.StationeersDefaultGamePort));
        Assert.True(RigConstants.ReservedGamePorts.ContainsKey(RigConstants.StationeersDefaultUpdatePort));
        Assert.Equal(4, RigConstants.ReservedGamePorts.Count);
    }

    [Fact]
    public void EveryReservedPortCarriesAHumanReadableReason()
    {
        foreach (var (port, reason) in RigConstants.ReservedGamePorts)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.DoesNotContain(port.ToString(System.Globalization.CultureInfo.InvariantCulture), reason);
        }
    }

    [Fact]
    public void TheServerPortsAreOffsetAThousandFromTheClientDefaultsSoBothCoexist()
    {
        Assert.Equal(1000, RigConstants.ServerGamePort - RigConstants.StationeersDefaultGamePort);
        Assert.Equal(1000, RigConstants.ServerUpdatePort - RigConstants.StationeersDefaultUpdatePort);
    }

    [Fact]
    public void BothHalvesShareOneBlockingWaitDefaultAndOneTeardownGrace()
    {
        // It was 30 on the server and 300 on the client for the same flag, so a 60 second save
        // confirmed on one half and warned on the other.
        Assert.Equal(300, RigConstants.WaitDefaultSeconds);
        Assert.Equal(30, RigConstants.TeardownGraceSeconds);
        Assert.NotEqual(RigConstants.WaitDefaultSeconds, RigConstants.TeardownGraceSeconds);
    }

    [Fact]
    public void EveryLongPathIsAnEndpointThePluginActuallyAnswers()
    {
        foreach (var path in RigConstants.ControlLongPaths)
        {
            Assert.True(Endpoints.Exists(path), $"the long-path list names {path}, which the router does not serve");
        }
    }

    // ---- the install path (COMMON-018 to COMMON-022) ----------------------

    [Fact]
    public void TheInstallPathIsReadFromTheBuildPropsAndTrimmed()
    {
        var fixture = new ClientFixture();
        Assert.Equal(RigFixture.SourceInstall, fixture.Env.StationeersPath());
    }

    [Fact]
    public void AMissingBuildPropsNamesTheTemplateAndDevMd()
    {
        var fs = new FakeFileSystem();
        var env = new RigEnvironment(fs, RigFixture.Home, new FakeAmbient(),
            repoRoot: ClientFixture.RepoRoot, buildProps: ClientFixture.BuildProps);

        var ex = Assert.Throws<RigConfigurationException>(() => env.StationeersPath());
        Assert.Contains("Directory.Build.props.template", ex.Message, StringComparison.Ordinal);
        Assert.Contains("DEV.md", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyStationeersPathIsItsOwnMessage()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(ClientFixture.BuildProps, "<Project><PropertyGroup><StationeersPath>   </StationeersPath></PropertyGroup></Project>");
        var env = new RigEnvironment(fs, RigFixture.Home, new FakeAmbient(),
            repoRoot: ClientFixture.RepoRoot, buildProps: ClientFixture.BuildProps);

        var ex = Assert.Throws<RigConfigurationException>(() => env.StationeersPath());
        Assert.Contains("is empty", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADedicatedServerInstallIsRefusedAndTheMessageNamesExactlyWhatIsMissing()
    {
        // One validity test replacing three. A dedicated-server install passed the server
        // half's check and failed the client half's, with two different messages.
        var fs = new FakeFileSystem();
        fs.AddFile(ClientFixture.BuildProps,
            $"<Project><PropertyGroup><StationeersPath>{RigFixture.SourceInstall}</StationeersPath></PropertyGroup></Project>");
        fs.AddFile(Path.Combine(RigFixture.SourceInstall, "rocketstation_DedicatedServer.exe"), "MZ");

        var env = new RigEnvironment(fs, RigFixture.Home, new FakeAmbient(),
            repoRoot: ClientFixture.RepoRoot, buildProps: ClientFixture.BuildProps);

        var ex = Assert.Throws<RigConfigurationException>(() => env.StationeersPath());
        Assert.Contains("rocketstation.exe", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Assembly-CSharp.dll", ex.Message, StringComparison.Ordinal);
        Assert.Contains("CLIENT install", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePathIsCachedAndTheCacheCanBeCleared()
    {
        var fixture = new ClientFixture();
        Assert.Equal(RigFixture.SourceInstall, fixture.Env.StationeersPath());

        // Break the props file. The cache must still answer.
        fixture.Fs.AddFile(ClientFixture.BuildProps, "<Project />");
        Assert.Equal(RigFixture.SourceInstall, fixture.Env.StationeersPath());

        // Clearing it makes the next read hit the broken file, which proves the cache was
        // real and that clearing it works.
        fixture.Env.ForgetInstallCache();
        Assert.Throws<RigConfigurationException>(() => fixture.Env.StationeersPath());
    }

    // ---- SteamCMD (COMMON-023 to COMMON-025) ------------------------------

    [Fact]
    public void SteamCmdComesFromTheInjectedOverrideFirstThenTheEnvironment()
    {
        var fixture = new ClientFixture();
        Assert.Equal(ClientFixture.SteamCmd, fixture.Env.SteamcmdPath());

        var ambient = new FakeAmbient();
        ambient.Variables["STEAMCMD_PATH"] = @"D:\steam\steamcmd.exe";
        var fs = new FakeFileSystem();
        fs.AddFile(@"D:\steam\steamcmd.exe", "steamcmd");

        var env = new RigEnvironment(fs, RigFixture.Home, ambient,
            repoRoot: ClientFixture.RepoRoot, buildProps: ClientFixture.BuildProps);
        Assert.Equal(@"D:\steam\steamcmd.exe", env.SteamcmdPath());
    }

    [Fact]
    public void AnUnsetSteamCmdAndAMissingOneAreDifferentMessages()
    {
        var fs = new FakeFileSystem();
        var env = new RigEnvironment(fs, RigFixture.Home, new FakeAmbient());
        Assert.Contains("is not set", Assert.Throws<RigConfigurationException>(() => env.SteamcmdPath()).Message,
            StringComparison.Ordinal);

        var ambient = new FakeAmbient();
        ambient.Variables["STEAMCMD_PATH"] = @"D:\gone.exe";
        var missing = new RigEnvironment(fs, RigFixture.Home, ambient);
        Assert.Contains("does not exist", Assert.Throws<RigConfigurationException>(() => missing.SteamcmdPath()).Message,
            StringComparison.Ordinal);
    }

    // ---- user data and the instances root (COMMON-026 to COMMON-029) ------

    [Fact]
    public void TheUserDataRootIsResolvedFromTheShellFolderAndNeverHardcoded()
    {
        var ambient = new FakeAmbient { MyDocuments = @"X:\Docs" };
        var env = new RigEnvironment(new FakeFileSystem(), RigFixture.Home, ambient);
        Assert.Equal(@"X:\Docs\My Games\Stationeers", env.UserDataPath());
    }

    [Fact]
    public void TheInstancesRootHasThreeSourcesAndEachNamesItself()
    {
        var ambient = new FakeAmbient();
        var env = new RigEnvironment(new FakeFileSystem(), RigFixture.Home, ambient);

        var typed = env.DefaultInstancesRoot(@"E:\typed");
        Assert.Equal(@"E:\typed", typed.Root);
        Assert.Contains("typed on this command", typed.Source, StringComparison.Ordinal);

        ambient.Variables["STATIONEERS_CLIENTRIG_ROOT"] = @"E:\fromenv";
        var fromEnv = env.DefaultInstancesRoot();
        Assert.Equal(@"E:\fromenv", fromEnv.Root);
        Assert.Contains("STATIONEERS_CLIENTRIG_ROOT", fromEnv.Source, StringComparison.Ordinal);

        ambient.Variables.Clear();
        var fallback = env.DefaultInstancesRoot();
        Assert.Equal(Path.Combine(RigFixture.Home, "ClientRig", "instances"), fallback.Root);
        Assert.Contains("default ClientRig/instances", fallback.Source, StringComparison.Ordinal);
    }

    // ---- the game version (COMMON-039, COMMON-040) ------------------------

    [Fact]
    public void TheGameVersionComesFromVersionIniAndDegradesToTheLiteralUnknown()
    {
        var fixture = new ClientFixture();
        Assert.Equal("0.2.6428.27798", fixture.Env.InstallVersion(RigFixture.SourceInstall));

        // The literal matters: three separate staleness comparisons test against it, and a
        // null would change every verdict from "cannot tell" to "differs".
        Assert.Equal("unknown", fixture.Env.InstallVersion(@"D:\nothing"));
        Assert.Equal("unknown", RigEnvironment.UnknownVersion);
    }

    [Fact]
    public void TheDedicatedServerDataFolderIsTriedWhenTheClientOneIsAbsent()
    {
        var fixture = new ClientFixture();
        fixture.Fs.AddFile(@"D:\server\rocketstation_DedicatedServer_Data\StreamingAssets\version.ini",
            "UPDATEVERSION=Update 0.2.1.2\r\n");
        Assert.Equal("0.2.1.2", fixture.Env.InstallVersion(@"D:\server"));
    }

    // ---- readiness (COMMON-062 to COMMON-067) -----------------------------

    [Fact]
    public void PingIsSatisfiedByAnyPayloadAndEverythingElseNeedsOne()
    {
        Assert.True(ReadinessStages.Reached(new StatusResponse(), ReadinessStage.Ping));
        Assert.False(ReadinessStages.Reached(null, ReadinessStage.Ping));

        foreach (var stage in new[] { ReadinessStage.ModsLoaded, ReadinessStage.Menu, ReadinessStage.InWorld })
        {
            Assert.False(ReadinessStages.Reached(null, stage));
        }
    }

    /// <summary>
    /// The <c>modsLoaded</c> boundary, at the exact count, and both facts <c>menu</c> needs.
    /// </summary>
    /// <remarks>
    /// The PowerShell compared with <c>-gt</c> against a constant named for a MINIMUM, so the
    /// effective threshold was one higher than the number every reader saw, and its own suite
    /// only ever exercised 22 and 2, which straddle the discrepancy without touching it. The
    /// three counts around the boundary are what says which rule is in force.
    /// </remarks>
    [Fact]
    public void ModsLoadedIsAtLeastTheMinimumAndMenuNeedsBothFacts()
    {
        Assert.False(ReadinessStages.Reached(
            new StatusResponse { LoadedPluginCount = RigConstants.StageMinPlugins - 1 }, ReadinessStage.ModsLoaded));
        Assert.True(ReadinessStages.Reached(
            new StatusResponse { LoadedPluginCount = RigConstants.StageMinPlugins }, ReadinessStage.ModsLoaded));
        Assert.True(ReadinessStages.Reached(
            new StatusResponse { LoadedPluginCount = RigConstants.StageMinPlugins + 1 }, ReadinessStage.ModsLoaded));

        // The two counts the PowerShell suite did test, kept: 2 is what a transient Steam
        // Workshop failure looks like from outside, and 22 is an ordinary modded client.
        Assert.False(ReadinessStages.Reached(new StatusResponse { LoadedPluginCount = 2 }, ReadinessStage.ModsLoaded));
        Assert.True(ReadinessStages.Reached(new StatusResponse { LoadedPluginCount = 22 }, ReadinessStage.ModsLoaded));

        // A plugin count alone is not readiness: the splash screen is still drawing and it
        // suppresses the in-game windows.
        Assert.False(ReadinessStages.Reached(
            new StatusResponse { Phase = "menu", GameInitialized = false }, ReadinessStage.Menu));
        Assert.False(ReadinessStages.Reached(
            new StatusResponse { Phase = "loading", GameInitialized = true }, ReadinessStage.Menu));
        Assert.True(ReadinessStages.Reached(
            new StatusResponse { Phase = "menu", GameInitialized = true }, ReadinessStage.Menu));
    }

    [Fact]
    public void OnlyTheMenuStageIsClientOnly()
    {
        // It was three (COMMON-123) and two stopped being true when the plugins merged: the
        // dedicated server has a control plane to ping and a loaded-plugin count to reach
        // modsLoaded with, on its own port. A menu is the one thing it genuinely never has,
        // because it takes -load or -new on its command line and enters that world directly.
        Assert.True(ReadinessStages.IsClientOnly(ReadinessStage.Menu));
        Assert.False(ReadinessStages.IsClientOnly(ReadinessStage.Ping));
        Assert.False(ReadinessStages.IsClientOnly(ReadinessStage.ModsLoaded));
        Assert.False(ReadinessStages.IsClientOnly(ReadinessStage.InWorld));
        Assert.False(ReadinessStages.IsClientOnly(ReadinessStage.Process));
    }

    // ---- mod builds (COMMON-056 to COMMON-061) ----------------------------

    [Fact]
    public void ModsBeatsPlansOnANameClash()
    {
        var fixture = new ClientFixture();
        fixture.AddRepositoryMod("Clash");
        fixture.Fs.AddFile(Path.Combine(ClientFixture.RepoRoot, "Plans", "Clash", "Clash", "bin", "Release", "Clash.dll"), "plan");

        var build = fixture.Mods.Find("Clash");
        Assert.NotNull(build);
        Assert.Equal(ModKind.Mod, build!.Kind);
    }

    [Fact]
    public void ADevPluginResolvesFromEitherHalfAndKnowsItsOwnLoadPath()
    {
        var fixture = new ClientFixture();
        fixture.Fs.AddFile(
            Path.Combine(RigFixture.Home, "DedicatedServer", "dev-plugins", "Probe", "Probe", "bin", "Release", "Probe.dll"),
            "probe");

        var build = fixture.Mods.Find("Probe");
        Assert.NotNull(build);
        Assert.Equal(ModKind.DevPluginServer, build!.Kind);

        // The load path is what stops a staleness remedy MOVING a payload while claiming to
        // refresh it.
        Assert.Equal(LoadPath.LaunchPad, build.LoadPathOn(RigHalf.Server));
        Assert.Equal(LoadPath.LaunchPad, build.LoadPathOn(RigHalf.Client));
    }

    [Fact]
    public void AReleasedModTakesTheChainloaderPathOnTheServerAndTheLaunchPadPathOnAClient()
    {
        var fixture = new ClientFixture();
        fixture.AddRepositoryMod("Released");

        var build = fixture.Mods.Find("Released")!;
        Assert.Equal(LoadPath.Chainloader, build.LoadPathOn(RigHalf.Server));
        Assert.Equal(LoadPath.LaunchPad, build.LoadPathOn(RigHalf.Client));
    }

    [Fact]
    public void ASdkStyleOutputPathWithATargetFrameworkFolderIsStillFound()
    {
        // COMMON-058. The PowerShell hardcoded the pre-SDK layout, so any project gaining a
        // target framework in its output path became invisible to deploy AND to staleness,
        // reported only as "not found. Skipping."
        var fixture = new ClientFixture();
        fixture.Fs.AddFile(
            Path.Combine(ClientFixture.RepoRoot, "Mods", "Sdk", "Sdk", "bin", "Release", "net472", "Sdk.dll"),
            "sdk");

        var build = fixture.Mods.Find("Sdk");
        Assert.NotNull(build);
        Assert.True(fixture.Fs.FileExists(build!.Dll));
        Assert.Contains("net472", build.Dll, StringComparison.Ordinal);
    }

    [Fact]
    public void AFlatOutputPathStillWinsWhenBothExist()
    {
        var fixture = new ClientFixture();
        fixture.AddRepositoryMod("Both");
        fixture.Fs.AddFile(
            Path.Combine(ClientFixture.RepoRoot, "Mods", "Both", "Both", "bin", "Release", "net472", "Both.dll"),
            "tfm");

        var build = fixture.Mods.Find("Both")!;
        Assert.DoesNotContain("net472", build.Dll, StringComparison.Ordinal);
    }

    [Fact]
    public void DeployableModsExcludesTemplateAndEverythingOutsideMods()
    {
        var fixture = new ClientFixture();
        fixture.AddRepositoryMod("Beta");
        fixture.AddRepositoryMod("Alpha");
        fixture.AddRepositoryMod("Template");
        fixture.Fs.AddFile(Path.Combine(ClientFixture.RepoRoot, "Plans", "Draft", "Draft", "bin", "Release", "Draft.dll"), "x");

        Assert.Equal(["Alpha", "Beta"], fixture.Mods.DeployableMods());
    }

    [Fact]
    public void AnAbsentModsFolderIsAnEmptySetRatherThanAThrow()
    {
        var fixture = new ClientFixture();
        Assert.Empty(fixture.Mods.DeployableMods());
        Assert.Null(fixture.Mods.Find("Nothing"));
    }

    [Fact]
    public void TheWipeWarningCoversPlansAndDevPluginsAndNotOnlyReleasedMods()
    {
        // Spec D-10. The PowerShell intersected against released mods only, so every wiped
        // Plans/ mod and every wiped dev-plugin, which is exactly where dev-plugins land,
        // disappeared with no warning from the one message whose job is naming what went.
        var fixture = new ClientFixture();
        fixture.AddRepositoryMod("Released");
        fixture.Fs.AddFile(Path.Combine(ClientFixture.RepoRoot, "Plans", "Draft", "Draft", "bin", "Release", "Draft.dll"), "x");
        fixture.Fs.AddFile(
            Path.Combine(RigFixture.Home, "ClientRig", "dev-plugins", "ClientDriver", "ClientDriver", "bin", "Release", "ClientDriver.dll"),
            "x");

        var lost = fixture.Mods.RepositoryFoldersAmong(
            ["Local_Released", "Local_Draft", "Local_ClientDriver", "Workshop_2345", "Local_Unrelated"]);

        Assert.Equal(["Local_Released", "Local_Draft", "Local_ClientDriver"], lost);
    }

    // ---- newest build time -------------------------------------------------

    [Fact]
    public void NewestBuildTimePrefersAssembliesAndFallsBackToEverything()
    {
        var fixture = new ClientFixture();
        var dir = @"C:\payload";

        fixture.Fs.AddFile(Path.Combine(dir, "About", "About.xml"), "<About />");
        fixture.Fs.SetLastWrite(Path.Combine(dir, "About", "About.xml"), new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero), fixture.Env.NewestBuildTime(dir));

        fixture.Fs.AddFile(Path.Combine(dir, "Mod.dll"), "dll");
        fixture.Fs.SetLastWrite(Path.Combine(dir, "Mod.dll"), new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero));

        // The DLL is older, and it still wins: the assembly is what actually changed.
        Assert.Equal(new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero), fixture.Env.NewestBuildTime(dir));
        Assert.Null(fixture.Env.NewestBuildTime(@"C:\nothing-here"));
    }
}
