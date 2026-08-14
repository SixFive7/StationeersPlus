using TestRig.Core.Rig;
using TestRig.Core.Server;
using TestRig.Core.Session;
using TestRig.Tests.Client;
using TestRig.Tests.Session.Fakes;
using Xunit;

namespace TestRig.Tests.Server;

/// <summary>
/// update-game, deploy, update-mods, and the reports the rig-wide status is built from.
/// </summary>
public sealed class ServerMaintenanceTests
{
    // =====================================================================
    // update-game
    // =====================================================================

    [Fact]
    public void UpdateGameIsGatedAndRefusesWhileTheServerIsRunning()
    {
        // SERVER-028: the PowerShell had NO guard here, unlike deploy and update-mods. Run
        // against a live server it fails part way with sharing violations and leaves a
        // half-mirrored BepInEx tree, which is worse than either guarded verb would produce.
        var fixture = new ServerFixture().Installed().Running();
        Assert.Throws<RigRefusalException>(() => fixture.Half.UpdateGame());

        var owner = fixture.Lease();
        var ex = Assert.Throws<RigRefusalException>(() => fixture.Half.UpdateGame(owner));

        Assert.Contains("half-written tree", ex.Message, StringComparison.Ordinal);
        Assert.Contains("testrig stop --target server", ex.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.SteamCmd.Runs);
    }

    [Fact]
    public void SteamCmdIsRunWithTheAppIdAndValidate()
    {
        var fixture = new ServerFixture();
        var owner = fixture.Lease();
        fixture.SteamCmd.OnRun = () => fixture.Installed();

        fixture.Half.UpdateGame(owner);

        var run = Assert.Single(fixture.SteamCmd.Runs);
        Assert.Equal(ClientFixture.SteamCmd, run.Path);
        Assert.Equal(
            ["+force_install_dir", fixture.Paths.InstallDir, "+login", "anonymous", "+app_update", "600760", "validate", "+quit"],
            run.Arguments);
    }

    [Fact]
    public void ANonZeroExitAndAMissingExeAreBothRefusedWithTheirOwnMessages()
    {
        var fixture = new ServerFixture();
        var owner = fixture.Lease();
        fixture.SteamCmd.ExitCode = 8;

        Assert.Contains("exit code 8",
            Assert.Throws<RigRefusalException>(() => fixture.Half.UpdateGame(owner)).Message, StringComparison.Ordinal);

        fixture.SteamCmd.ExitCode = 0;
        Assert.Contains("missing after the SteamCMD run",
            Assert.Throws<RigRefusalException>(() => fixture.Half.UpdateGame(owner)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheClientsBepInExTreeIsMirroredWholeAndTheLoaderFilesComeWithIt()
    {
        var fixture = new ServerFixture();
        var owner = fixture.Lease();
        fixture.SteamCmd.OnRun = () => fixture.Fs.AddFile(fixture.Paths.Exe, "MZ");

        fixture.Half.UpdateGame(owner);

        Assert.True(fixture.Fs.FileExists(Path.Combine(fixture.Paths.BepInEx, "core", "BepInEx.dll")));
        Assert.True(fixture.Fs.FileExists(Path.Combine(fixture.Paths.InstallDir, "winhttp.dll")));
        Assert.True(fixture.Fs.FileExists(Path.Combine(fixture.Paths.InstallDir, "doorstop_config.ini")));
    }

    [Fact]
    public void TheMirroredBepInExVersionIsPrinted()
    {
        // SERVER-017. A mirror is the one operation that can leave the server on a different
        // BepInEx from the client, and this line is the only place that number is printed.
        var fixture = new ServerFixture();
        var owner = fixture.Lease();
        fixture.Fs.SetBinaryVersion(
            Path.Combine(RigFixture.SourceInstall, "BepInEx", "core", "BepInEx.dll"), "5.4.22.0", "5.4.22");
        fixture.SteamCmd.OnRun = () => fixture.Fs.AddFile(fixture.Paths.Exe, "MZ");

        fixture.Half.UpdateGame(owner);

        Assert.True(fixture.Output.Said("BepInEx mirrored"));
        Assert.True(fixture.Output.Said("version 5.4.22.0"));
    }

    [Fact]
    public void TheStationeersLaunchPadVersionComesFromTheDllsOwnVersionResource()
    {
        // SERVER-018 and SERVER-020. The port read a version.txt sidecar and a
        // StationeersLaunchPad-<version> marker file, and neither exists in a real install
        // (the plugin folder holds four DLLs), so the version was ALWAYS empty, the overlay
        // always skipped, and RG.ImGui.dll never reached the dedicated server. Everything
        // downstream (temp-name download, length check, version-keyed cache) was unreachable.
        var fixture = new ServerFixture();
        var owner = fixture.Lease();
        var sourceDll = Path.Combine(
            RigFixture.SourceInstall, "BepInEx", "plugins", "StationeersLaunchPad", "StationeersLaunchPad.dll");

        // No sidecar and no marker file anywhere, exactly as on the real install.
        fixture.Fs.SetBinaryVersion(sourceDll, "0.5.0.0", "0.5.0");
        fixture.SteamCmd.OnRun = () => fixture.Fs.AddFile(fixture.Paths.Exe, "MZ");

        fixture.Half.UpdateGame(owner);

        // Read off the MIRRORED copy: a real file copy carries the version resource with the
        // bytes, which is what makes reading it after the mirror the right moment.
        var download = Assert.Single(fixture.Downloader.Downloads);
        Assert.Equal(
            "https://github.com/StationeersLaunchPad/StationeersLaunchPad/releases/download/"
            + "v0.5.0/StationeersLaunchPad-server-v0.5.0.zip",
            download.Url);
        Assert.False(fixture.Output.Warned("Could not read a version"));
    }

    [Fact]
    public void ProductVersionWinsOverFileVersionAndTrailingMetadataIsStripped()
    {
        // A .NET AssemblyInformationalVersion lands in ProductVersion and is what the release
        // tag matches; a "+sha" suffix on it must not end up in the URL.
        var fixture = new ServerFixture().Installed();
        Assert.Equal("2.4.1", fixture.Half.LaunchPadVersion());

        fixture.Fs.SetBinaryVersion(fixture.Paths.LaunchPadDll, "9.9.9.9", "1.2.3+deadbee");

        Assert.Equal("1.2.3", fixture.Half.LaunchPadVersion());

        // A build that stamps only the numeric one still gets an answer.
        fixture.Fs.SetBinaryVersion(fixture.Paths.LaunchPadDll, "9.9.9.9", "");
        Assert.Equal("9.9.9.9", fixture.Half.LaunchPadVersion());

        // And an absent DLL is empty rather than a wrong URL.
        fixture.Fs.DeleteFile(fixture.Paths.LaunchPadDll);
        Assert.Equal("", fixture.Half.LaunchPadVersion());
    }

    [Fact]
    public void AClientInstallWithNoBepInExIsRefusedNamingStationeersLaunchPad()
    {
        var fixture = new ServerFixture();
        var owner = fixture.Lease();
        fixture.SteamCmd.OnRun = () => fixture.Fs.AddFile(fixture.Paths.Exe, "MZ");
        fixture.Fs.DeleteDirectory(Path.Combine(RigFixture.SourceInstall, "BepInEx"), recursive: true);

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Half.UpdateGame(owner));
        Assert.Contains("StationeersLaunchPad", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheServerZipIsCachedInsideTheRigAndNotUnderAnUndatedWorkFolder()
    {
        // SERVER-021: the repository rule is that everything under .work/ lives in a dated
        // session folder, and the PowerShell wrote a permanent undated .work/launchpad-server/.
        var fixture = new ServerFixture();
        var owner = fixture.Lease();
        fixture.SteamCmd.OnRun = () =>
        {
            fixture.Fs.AddFile(fixture.Paths.Exe, "MZ");
            fixture.Fs.SetBinaryVersion(
                Path.Combine(RigFixture.SourceInstall, "BepInEx", "plugins", "StationeersLaunchPad", "StationeersLaunchPad.dll"),
                "2.4.1.0", "2.4.1");
        };

        fixture.Half.UpdateGame(owner);

        var download = Assert.Single(fixture.Downloader.Downloads);
        Assert.Contains("v2.4.1", download.Url, StringComparison.Ordinal);
        Assert.StartsWith(fixture.Paths.DataDir, download.Destination, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".work", download.Destination, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2.4.1", fixture.Half.LaunchPadCacheDir("2.4.1"), StringComparison.Ordinal);
    }

    [Fact]
    public void ATruncatedDownloadNeverBecomesTheCachedCopy()
    {
        // SERVER-022. The PowerShell wrote straight to the final cache path, so a partial or
        // zero-byte file poisoned every later run: the next update-game saw the file, skipped
        // the download, and called the extractor on a corrupt archive, which threw uncaught
        // AFTER SteamCMD had already replaced the BepInEx tree.
        var fixture = new ServerFixture();
        var owner = fixture.Lease();
        fixture.Downloader.Content = "";
        fixture.SteamCmd.OnRun = () =>
        {
            fixture.Fs.AddFile(fixture.Paths.Exe, "MZ");
            fixture.Fs.SetBinaryVersion(
                Path.Combine(RigFixture.SourceInstall, "BepInEx", "plugins", "StationeersLaunchPad", "StationeersLaunchPad.dll"),
                "2.4.1.0", "2.4.1");
        };

        fixture.Half.UpdateGame(owner);

        var cached = Path.Combine(fixture.Half.LaunchPadCacheDir("2.4.1"), "StationeersLaunchPad-server-v2.4.1.zip");
        Assert.False(fixture.Fs.FileExists(cached));
        Assert.False(fixture.Fs.FileExists(cached + ".partial"));
        Assert.True(fixture.Output.Warned("download failed"));
        Assert.True(fixture.Output.Warned("RG.ImGui"));
    }

    [Fact]
    public void ACachedArchiveThatWillNotOpenIsDeletedSoTheNextRunReDownloadsIt()
    {
        var fixture = new ServerFixture();
        var owner = fixture.Lease();
        fixture.Extractor.Throws = new InvalidDataException("central directory corrupt");
        fixture.SteamCmd.OnRun = () =>
        {
            fixture.Fs.AddFile(fixture.Paths.Exe, "MZ");
            fixture.Fs.SetBinaryVersion(
                Path.Combine(RigFixture.SourceInstall, "BepInEx", "plugins", "StationeersLaunchPad", "StationeersLaunchPad.dll"),
                "2.4.1.0", "2.4.1");
        };

        fixture.Half.UpdateGame(owner);

        var cached = Path.Combine(fixture.Half.LaunchPadCacheDir("2.4.1"), "StationeersLaunchPad-server-v2.4.1.zip");
        Assert.False(fixture.Fs.FileExists(cached));
        Assert.True(fixture.Output.Warned("has been deleted so the next update-game downloads it again"));
    }

    [Fact]
    public void TheOverlayCopiesTheServerZipFilesOverTheMirroredPlugin()
    {
        var fixture = new ServerFixture();
        var owner = fixture.Lease();
        fixture.SteamCmd.OnRun = () =>
        {
            fixture.Fs.AddFile(fixture.Paths.Exe, "MZ");
            fixture.Fs.SetBinaryVersion(
                Path.Combine(RigFixture.SourceInstall, "BepInEx", "plugins", "StationeersLaunchPad", "StationeersLaunchPad.dll"),
                "2.4.1.0", "2.4.1");
        };

        fixture.Half.UpdateGame(owner);

        // RG.ImGui.dll is in the server zip and not in the client install, which is the whole
        // reason the overlay exists.
        var pluginDir = Path.GetDirectoryName(fixture.Paths.LaunchPadDll)!;
        Assert.True(fixture.Fs.FileExists(Path.Combine(pluginDir, "RG.ImGui.dll")));
        Assert.True(fixture.Output.Said("overlaid 2 files"));
    }

    [Fact]
    public void NoStationeersLaunchPadMeansTheOverlayIsSkippedWithAWarningRatherThanAFailure()
    {
        var fixture = new ServerFixture();
        var owner = fixture.Lease();
        fixture.SteamCmd.OnRun = () => fixture.Fs.AddFile(fixture.Paths.Exe, "MZ");

        fixture.Half.UpdateGame(owner);

        Assert.True(fixture.Output.Warned("Mods will not load until StationeersLaunchPad is installed"));
        Assert.Empty(fixture.Downloader.Downloads);
        Assert.True(fixture.Output.Said("Done."));
    }

    // =====================================================================
    // deploy
    // =====================================================================

    [Fact]
    public void AReleasedModTakesTheChainloaderPathOnThisHalf()
    {
        var fixture = new ServerFixture().Installed();
        var owner = fixture.Lease();
        fixture.Client.AddRepositoryMod("SprayPaintPlus");

        var counts = fixture.Half.Deploy(["SprayPaintPlus"], owner);

        Assert.Equal(new ServerCounts(1, 0), counts);
        Assert.True(fixture.Fs.FileExists(Path.Combine(fixture.Paths.PluginsDir, "SprayPaintPlus", "SprayPaintPlus.dll")));
        Assert.True(fixture.Output.Said("BepInEx Chainloader load path"));
    }

    [Fact]
    public void ADevPluginTakesTheLaunchPadPathWithItsAboutMirrorAndAnAbsoluteLocalEntry()
    {
        // The absolute path is correct here: a rooted value bypasses StationeersLaunchPad's
        // localDir prefix step and matches the discovered mod's own path.
        var fixture = new ServerFixture().Installed();
        var owner = fixture.Lease();
        var root = Path.Combine(RigFixture.Home, "DedicatedServer", "dev-plugins", "ScenarioRunner", "ScenarioRunner");
        fixture.Fs.AddFile(Path.Combine(root, "bin", "Release", "ScenarioRunner.dll"), "probe");
        fixture.Fs.AddFile(Path.Combine(root, "About", "About.xml"), "<About />");

        fixture.Half.Deploy(["ScenarioRunner"], owner);

        var local = Path.Combine(fixture.Paths.ModsDir, "Local_ScenarioRunner");
        Assert.True(fixture.Fs.FileExists(Path.Combine(local, "ScenarioRunner.dll")));
        Assert.True(fixture.Fs.FileExists(Path.Combine(local, "About", "About.xml")));

        var entries = ModConfig.Read(fixture.Fs, fixture.Paths.ModConfig);
        Assert.Contains(entries, e => e.Path.Equals(local, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ACopyInTheOTHERLoadPathIsRemovedWHOLEAndTheReasonIsReported()
    {
        // SERVER-034: the PowerShell deleted only the DLL, leaving About.xml and the whole
        // About/ folder behind, which is exactly what StationeersLaunchPad keys a second copy
        // off.
        var fixture = new ServerFixture().Installed();
        var owner = fixture.Lease();
        fixture.Client.AddRepositoryMod("SprayPaintPlus");

        var stale = Path.Combine(fixture.Paths.ModsDir, "Local_SprayPaintPlus");
        fixture.Fs.AddFile(Path.Combine(stale, "SprayPaintPlus.dll"), "old");
        fixture.Fs.AddFile(Path.Combine(stale, "About", "About.xml"), "<About />");

        fixture.Half.Deploy(["SprayPaintPlus"], owner);

        Assert.False(fixture.Fs.DirectoryExists(stale));
        Assert.True(fixture.Output.Warned("a payload in both load paths"));
    }

    [Fact]
    public void DeployRefusesWhileTheServerHoldsItsPluginDllsOpen()
    {
        var fixture = new ServerFixture().Installed().Running();
        var owner = fixture.Lease();
        fixture.Client.AddRepositoryMod("Mod");

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Half.Deploy(["Mod"], owner));
        Assert.Contains("exclusive lock on every loaded plugin DLL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeployRefusesWhenTheServerIsNotInstalledAtAll()
    {
        var fixture = new ServerFixture();
        var owner = fixture.Lease();
        var ex = Assert.Throws<RigRefusalException>(() => fixture.Half.Deploy(null, owner));
        Assert.Contains("not installed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDeployCountsComeBackAsAValueRatherThanBeingPrintedAfterTheReadableLines()
    {
        // SERVER-046: the PowerShell's return object was not suppressed by the dispatcher, so
        // it printed after the human-readable lines and read as stray output.
        var fixture = new ServerFixture().Installed();
        var owner = fixture.Lease();
        fixture.Client.AddRepositoryMod("A");

        var counts = fixture.Half.Deploy(["A", "Ghost"], owner);

        Assert.Equal(new ServerCounts(1, 1), counts);
        Assert.Equal("1", fixture.Output.ValueOf("deployed"));
        Assert.Equal("1", fixture.Output.ValueOf("skipped"));
        Assert.DoesNotContain(fixture.Output.Lines, l => l.Text.Contains("Deployed =", StringComparison.Ordinal));
    }

    // =====================================================================
    // update-mods
    // =====================================================================

    [Fact]
    public void EveryEnabledEntryIsCopiedUnderItsSourcePrefixedName()
    {
        var fixture = new ServerFixture().Installed();
        var owner = fixture.Lease();
        fixture.Fs.AddFile(@"C:\workshop\2345\mod.dll", "workshop");

        var counts = fixture.Half.UpdateMods(owner);

        Assert.True(fixture.Fs.FileExists(Path.Combine(fixture.Paths.ModsDir, "Workshop_2345", "mod.dll")));
        Assert.True(fixture.Fs.FileExists(Path.Combine(fixture.Paths.ModsDir, "Local_HandMade", "About", "About.xml")));
        Assert.Equal(new ServerCounts(2, 0), counts);
    }

    [Fact]
    public void TheBakedConfigCarriesBAREFOLDERNAMESAndNotAbsolutePaths()
    {
        // Resolved by StationeersLaunchPad against the save path. This form is verified end to
        // end; it is the opposite of the absolute path a per-mod deploy writes, and both are
        // right for their own reasons.
        var fixture = new ServerFixture().Installed();
        var owner = fixture.Lease();
        fixture.Fs.AddFile(@"C:\workshop\2345\mod.dll", "workshop");

        fixture.Half.UpdateMods(owner);

        var entries = ModConfig.Read(fixture.Fs, fixture.Paths.ModConfig);
        Assert.Equal(["Core", "Local", "Local"], entries.Select(static e => e.Kind));
        Assert.Contains(entries, e => e.Path == "Workshop_2345");
        Assert.Contains(entries, e => e.Path == "Local_HandMade");
        Assert.DoesNotContain(entries, e => e.Path.Contains(':', StringComparison.Ordinal));
    }

    [Fact]
    public void ADisabledEntryIsSkippedAndAMissingSourceIsCountedAsSkipped()
    {
        var fixture = new ServerFixture().Installed();
        var owner = fixture.Lease();
        fixture.Fs.AddFile(Path.Combine(RigFixture.UserData, "modconfig.xml"),
            """
            <ModConfig>
              <Core Enabled="true"><Path /></Core>
              <Local Enabled="false"><Path Value="C:\off" /></Local>
              <Workshop Enabled="true"><Path Value="C:\gone" /><WorkshopId Value="9" /></Workshop>
            </ModConfig>
            """);

        var counts = fixture.Half.UpdateMods(owner);

        Assert.Equal(new ServerCounts(0, 1), counts);
        Assert.True(fixture.Output.Warned("source missing"));
        Assert.DoesNotContain(ModConfig.Read(fixture.Fs, fixture.Paths.ModConfig), e => e.Path.Contains("off", StringComparison.Ordinal));
    }

    [Fact]
    public void AWorkshopEntryWithNoIdFallsBackToItsBasenameAndSaysSo()
    {
        var fixture = new ServerFixture().Installed();
        var owner = fixture.Lease();
        fixture.Fs.AddFile(Path.Combine(RigFixture.UserData, "modconfig.xml"),
            """
            <ModConfig>
              <Workshop Enabled="true"><Path Value="C:\workshop\NamedFolder" /></Workshop>
            </ModConfig>
            """);
        fixture.Fs.AddFile(@"C:\workshop\NamedFolder\mod.dll", "x");

        fixture.Half.UpdateMods(owner);

        Assert.True(fixture.Output.Warned("without WorkshopId"));
        Assert.True(fixture.Fs.DirectoryExists(Path.Combine(fixture.Paths.ModsDir, "Workshop_NamedFolder")));
    }

    [Fact]
    public void AnUnknownEntryKindIsReportedAndIgnored()
    {
        var fixture = new ServerFixture().Installed();
        var owner = fixture.Lease();
        fixture.Fs.AddFile(Path.Combine(RigFixture.UserData, "modconfig.xml"),
            """<ModConfig><Nonsense Enabled="true"><Path Value="C:\x" /></Nonsense></ModConfig>""");

        fixture.Half.UpdateMods(owner);
        Assert.True(fixture.Output.Warned("Unknown modconfig entry type 'Nonsense'"));
    }

    [Fact]
    public void TheWipeWarningNamesPlansModsAndDevPluginsAndNotOnlyReleasedOnes()
    {
        // SERVER-180: the PowerShell intersected against RELEASED MODS ONLY, so every wiped
        // Plans/ mod and every wiped dev-plugin, which is exactly where dev-plugins are
        // deployed on this half, disappeared with no warning.
        var fixture = new ServerFixture().Installed();
        var owner = fixture.Lease();
        fixture.Fs.AddFile(Path.Combine(ClientFixture.RepoRoot, "Plans", "Draft", "Draft", "bin", "Release", "Draft.dll"), "x");
        fixture.Fs.AddFile(
            Path.Combine(RigFixture.Home, "DedicatedServer", "dev-plugins", "ScenarioRunner", "ScenarioRunner", "bin", "Release", "ScenarioRunner.dll"),
            "x");

        fixture.Fs.AddFile(Path.Combine(fixture.Paths.ModsDir, "Local_Draft", "Draft.dll"), "deployed");
        fixture.Fs.AddFile(Path.Combine(fixture.Paths.ModsDir, "Local_ScenarioRunner", "ScenarioRunner.dll"), "deployed");
        fixture.Fs.AddFile(@"C:\workshop\2345\mod.dll", "workshop");

        fixture.Half.UpdateMods(owner);

        Assert.True(fixture.Output.Warned("Local_Draft"));
        Assert.True(fixture.Output.Warned("Local_ScenarioRunner"));
        Assert.True(fixture.Output.Warned("Re-deploy them"));
    }

    [Fact]
    public void UpdateModsRefusesWhileTheServerHoldsTheSyncedFilesOpen()
    {
        var fixture = new ServerFixture().Installed().Running();
        var owner = fixture.Lease();

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Half.UpdateMods(owner));
        Assert.Contains("holds the synced mod files open", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingSourceModConfigNamesTheOverrideFlag()
    {
        var fixture = new ServerFixture().Installed();
        var owner = fixture.Lease();
        fixture.Fs.DeleteFile(Path.Combine(RigFixture.UserData, "modconfig.xml"));

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Half.UpdateMods(owner));
        Assert.Contains("--from-modconfig", ex.Message, StringComparison.Ordinal);
    }

    // =====================================================================
    // reports
    // =====================================================================

    [Fact]
    public void TheVersionReportComparesTheInstalledServerAgainstTheDevelopersClient()
    {
        var fixture = new ServerFixture().Installed();
        fixture.Fs.AddFile(
            Path.Combine(fixture.Paths.InstallDir, "rocketstation_DedicatedServer_Data", "StreamingAssets", "version.ini"),
            "UPDATEVERSION=Update 0.2.6000.10000\r\n");

        var report = fixture.Half.VersionReport();

        Assert.True(report.Present);
        Assert.Equal("0.2.6000.10000", report.Version);
        Assert.Equal("0.2.6428.27798", report.Source);
        Assert.True(report.Stale);
        Assert.Contains("testrig update-game --target server", report.Remedy, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownVersionOnEitherSideMeansCannotTellRatherThanDiffers()
    {
        var fixture = new ServerFixture().Installed();
        var report = fixture.Half.VersionReport();

        Assert.Equal(RigEnvironment.UnknownVersion, report.Version);
        Assert.False(report.Stale);
    }

    [Fact]
    public void AWorkshopModCANReportStalenessBecauseItsSourceComesFromTheModConfig()
    {
        // SERVER-153, spec D-09. The PowerShell stripped Workshop_<id> to the published-file id
        // and looked for it under the LOCAL mods folder, where it can never be, so 93% of a
        // seeded set was silently exempt.
        var fixture = new ServerFixture().Installed();

        fixture.Fs.AddFile(@"C:\workshop\2345\mod.dll", "new");
        fixture.Fs.SetLastWrite(@"C:\workshop\2345\mod.dll", new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero));

        var deployed = Path.Combine(fixture.Paths.ModsDir, "Workshop_2345", "mod.dll");
        fixture.Fs.AddFile(deployed, "old");
        fixture.Fs.SetLastWrite(deployed, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var row = Assert.Single(fixture.Half.ModStaleness());
        Assert.Equal("seeded mod", row.Kind);
        Assert.Equal("Workshop_2345", row.Name);
        Assert.Equal(LoadPath.LaunchPad, row.LoadPath);
        Assert.Contains("testrig update-mods --target server", row.Remedy, StringComparison.Ordinal);
    }

    [Fact]
    public void AStalePluginCarriesTheLoadPathItBelongsInSoARemedyCannotMoveIt()
    {
        // SERVER-043 and SERVER-154, spec D-14: the remedy the PowerShell printed for a
        // dev-plugin found in the Chainloader folder would have MOVED the payload rather than
        // refreshing it, with nothing saying so.
        var fixture = new ServerFixture().Installed();
        var root = Path.Combine(RigFixture.Home, "ClientRig", "dev-plugins", "ClientDriver", "ClientDriver");
        var build = Path.Combine(root, "bin", "Release", "ClientDriver.dll");
        fixture.Fs.AddFile(build, "new");
        fixture.Fs.SetLastWrite(build, new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero));

        var deployed = Path.Combine(fixture.Paths.PluginsDir, "ClientDriver", "ClientDriver.dll");
        fixture.Fs.AddFile(deployed, "old");
        fixture.Fs.SetLastWrite(deployed, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var row = Assert.Single(fixture.Half.ModStaleness());
        Assert.Equal("deployed plugin", row.Kind);
        Assert.Equal(LoadPath.LaunchPad, row.LoadPath);
        Assert.Contains("belongs in the StationeersLaunchPad load path", row.Remedy, StringComparison.Ordinal);
    }

    [Fact]
    public void StalenessIsOnlyEverReportedAndNeverFixed()
    {
        var fixture = new ServerFixture().Installed();
        fixture.Fs.AddFile(@"C:\workshop\2345\mod.dll", "new");
        fixture.Fs.SetLastWrite(@"C:\workshop\2345\mod.dll", new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero));
        var deployed = Path.Combine(fixture.Paths.ModsDir, "Workshop_2345", "mod.dll");
        fixture.Fs.AddFile(deployed, "old");
        fixture.Fs.SetLastWrite(deployed, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        fixture.Fs.DeletedTrees.Clear();
        fixture.Half.ModStaleness();

        Assert.True(fixture.Fs.FileExists(deployed));
        Assert.Empty(fixture.Fs.DeletedTrees);
    }

    // =====================================================================
    // status
    // =====================================================================

    [Fact]
    public void StatusReportsBothProcessesTheLastLogLineAndTheWorldCount()
    {
        var fixture = new ServerFixture().Installed().Running().World("Luna");
        fixture.Log("first line", "the newest line");
        fixture.Fs.AddFile(fixture.Paths.ControlFile, "save \"Luna\"");

        fixture.Half.Status();

        Assert.True(fixture.Output.Said("host wrapper: running (PID 9100)"));
        Assert.True(fixture.Output.Said("running (PID 9101, up"));
        Assert.True(fixture.Output.Said("last log:     the newest line"));
        Assert.True(fixture.Output.Said("pending cmd:  save \"Luna\""));
        Assert.True(fixture.Output.Said("worlds:       1 under data/saves/"));
    }

    [Fact]
    public void UptimePastADayShowsTheDaysRatherThanTruncatingThem()
    {
        // SERVER-142, spec D-18: the PowerShell formatted this as hh:mm:ss, so a soak run past
        // 24 hours displayed a day-truncated figure and looked like it had just restarted.
        var fixture = new ServerFixture().Installed();
        fixture.Processes.Add(9101, RigConstants.ServerImageName, fixture.Clock.UtcNow.AddDays(-3).AddHours(-4));
        PidFiles.Write(fixture.Fs, fixture.Paths.PidFile, 9101, fixture.Clock.UtcNow.AddDays(-3).AddHours(-4));

        fixture.Half.Status();
        Assert.True(fixture.Output.Said("up 3.04:00:00"));
    }

    [Fact]
    public void AnUnreadableStartTimeIsAQuestionMarkRatherThanAStackTrace()
    {
        // Spec D-19: the PowerShell read StartTime with no guard at all, which can throw
        // access denied and take the whole status block with it.
        var fixture = new ServerFixture().Installed();
        fixture.Fs.AddFile(fixture.Paths.PidFile, "9101");

        fixture.Half.Status();
        Assert.True(fixture.Output.Said("stopped"));
    }

    [Fact]
    public void AnOrphanedServerWithNoWrapperIsWarnedAbout()
    {
        var fixture = new ServerFixture().Installed();
        fixture.Processes.Add(9101, RigConstants.ServerImageName, fixture.Clock.UtcNow);
        PidFiles.Write(fixture.Fs, fixture.Paths.PidFile, 9101, fixture.Clock.UtcNow);

        fixture.Half.Status();

        Assert.True(fixture.Output.Warned("nothing can relay a console command"));
        Assert.True(fixture.Output.Warned("Terminate the orphan"));
    }

    [Fact]
    public void AZeroPlayerCountSaysWhetherTheLogHasEVERCarriedAConnectionLine()
    {
        // SERVER-006, spec D-06: the two patterns behind the count are unverified against any
        // current build, and this count gates the session lock's busy state. "Never seen one"
        // and "nobody is here" are different answers.
        var fixture = new ServerFixture().Installed().Running();
        fixture.Log("no connection lines at all");

        fixture.Half.Status();
        Assert.True(fixture.Output.Said("may mean the log format moved"));

        fixture.Output.Clear();
        fixture.Log("Client Bob (76561198000000) is ready");
        fixture.Half.Status();
        Assert.True(fixture.Output.Said("players:      1 connected"));
        Assert.False(fixture.Output.Said("may mean the log format moved"));
    }

    [Fact]
    public void APlayerCountIsZeroWhenTheServerIsNotRunningAtAll()
    {
        // Deliberate: it favours freeing the rig.
        var fixture = new ServerFixture().Installed();
        fixture.Log("Client Bob (76561198000000) is ready");
        Assert.Equal(0, fixture.Half.ConnectedPlayers());
    }

    // =====================================================================
    // logs
    // =====================================================================

    [Fact]
    public void AMissingLogSaysSoAndReturns()
    {
        var fixture = new ServerFixture().Installed();
        fixture.Half.Logs();
        Assert.True(fixture.Output.Said("No dedicated-server log at"));
    }

    [Fact]
    public void GrepFiltersTheWholeFileAndTailIsTheWindowOverTheMatchesHereToo()
    {
        // SERVER-159, spec D-20: the PowerShell silently ignored the tail whenever a pattern
        // was given, although the manual documented the two flags as independent. The port
        // then honoured it in the wrong order, tailing the FILE and grepping that window, so
        // a match older than the last N lines was invisible. Both halves share one
        // implementation now (LogFilter), which is why this test is a twin of the client's.
        var fixture = new ServerFixture().Installed();
        fixture.Log("Saved Luna");
        fixture.Log([.. Enumerable.Range(1, 40).Select(i => $"noise {i}")]);
        fixture.Log("Saved Titan");

        fixture.Half.Logs(grep: "^Saved ");
        Assert.True(fixture.Output.Said("Saved Luna"));
        Assert.True(fixture.Output.Said("Saved Titan"));

        fixture.Output.Clear();
        fixture.Half.Logs(tail: 5, grep: "^Saved ");
        Assert.True(fixture.Output.Said("Saved Luna"));
        Assert.True(fixture.Output.Said("Saved Titan"));

        fixture.Output.Clear();
        fixture.Half.Logs(tail: 1, grep: "^Saved ");
        Assert.False(fixture.Output.Said("Saved Luna"));
        Assert.True(fixture.Output.Said("Saved Titan"));
    }

    [Fact]
    public void TheDefaultIsTheLastFiftyLines()
    {
        var fixture = new ServerFixture().Installed();
        fixture.Log([.. Enumerable.Range(1, 100).Select(i => $"line {i}")]);

        fixture.Half.Logs();
        Assert.True(fixture.Output.Said("line 100"));
        Assert.True(fixture.Output.Said("line 51"));
        Assert.False(fixture.Output.Said("line 50"));
    }
}
