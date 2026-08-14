using TestRig.Core.Session;
using TestRig.Tests.Session.Fakes;
using Xunit;

namespace TestRig.Tests.Session;

/// <summary>
/// Baseline capture, staleness and config restore. Ported from rig-reset.tests.ps1
/// sections baseline (43) and baselinerestore (39).
/// </summary>
public sealed class BaselineTests
{
    private static RigFixture WithInstance(string instance = "c1")
    {
        var rig = new RigFixture();
        rig.AddInstance(instance);
        rig.RegisterInstanceRoot((instance, RigFixture.InstancesRoot));
        // The real source install carries this file, and CopyConfigTree deletes any .cfg
        // the source lacks, which would otherwise take the redirect with it.
        rig.Fs.AddFile(
            Path.Combine(RigFixture.SourceInstall, "BepInEx", "config", SavePathOverride.ConfigLeaf),
            "# stock launchpad config");
        return rig;
    }

    private static void GameVersion(RigFixture rig, string version)
    {
        rig.Fs.AddFile(
            Path.Combine(RigFixture.SourceInstall, "rocketstation_Data", "StreamingAssets", "version.ini"),
            $"UPDATEVERSION={version}\r\nthe rest of a 170 KB changelog\r\nmore lines\r\n");
    }

    // ---- capture -----------------------------------------------------------

    [Fact]
    public void ThereIsNoBaselineUntilOneIsCaptured()
    {
        var rig = new RigFixture();

        Assert.Null(rig.Baseline.Read());
        var staleness = rig.Baseline.CheckStale(null);
        Assert.False(staleness.Present);
        Assert.True(staleness.Stale);
        Assert.Equal(["no baseline has ever been captured"], staleness.Reasons);
    }

    [Fact]
    public void CaptureWritesAManifestThatReadsBack()
    {
        var rig = WithInstance();
        GameVersion(rig, "0.2.6420.27780");

        var capture = rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");

        Assert.False(capture.WhatIf);
        Assert.True(capture.Entries > 0);
        var baseline = rig.Baseline.Read()!;
        Assert.Equal("abc12345", baseline.CapturedBy);
        Assert.Equal("0.2.6420.27780", baseline.GameVersion);
        Assert.Equal(RigTime.Stamp(rig.Clock.UtcNow), baseline.CapturedUtc);
        Assert.Equal("RIGTEST", baseline.Host);
        Assert.Equal(["c1"], baseline.Instances);
    }

    [Fact]
    public void AConfigIsStoredByContentAndAPayloadIsOnlyHashed()
    {
        var rig = WithInstance();
        var cfgKey = $"client/c1/bepinex-config/{SavePathOverride.ConfigLeaf}";
        rig.Fs.AddFile(Path.Combine(RigFixture.InstancesRoot, "c1", "BepInEx", "plugins", "ClientDriver.dll"), "payload");

        var capture = rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");

        var baseline = rig.Baseline.Read()!;
        Assert.Equal(SurfaceClass.Config, baseline.Files[cfgKey].Class);
        Assert.NotEqual("", baseline.Files[cfgKey].Sha256);
        Assert.True(rig.Fs.FileExists(rig.Baseline.StoredPath(cfgKey)));

        var payloadKey = "client/c1/plugins/ClientDriver.dll";
        Assert.Equal(SurfaceClass.Payload, baseline.Files[payloadKey].Class);
        Assert.NotEqual("", baseline.Files[payloadKey].Sha256);
        Assert.False(rig.Fs.FileExists(rig.Baseline.StoredPath(payloadKey)));
        Assert.Equal(1, capture.Stored);
    }

    [Fact]
    public void TheStoredPathIsFlatAndDerivedFromTheLowercasedKey()
    {
        var rig = new RigFixture();

        var path = rig.Baseline.StoredPath("client/c1/bepinex-config/net.example.cfg");

        Assert.Equal(rig.Paths.BaselineStore, Path.GetDirectoryName(path));
        Assert.EndsWith("-net.example.cfg", path, StringComparison.Ordinal);
        Assert.Equal(path, rig.Baseline.StoredPath("CLIENT/C1/BEPINEX-CONFIG/net.example.cfg"));
    }

    [Fact]
    public void CaptureRefusesWhileTheRigIsInUse()
    {
        var rig = WithInstance();
        rig.StartServer(players: 1);

        var ex = Assert.Throws<RigRefusalException>(() => rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345"));

        Assert.Equal(RigRefusalKind.RigBusy, ex.Kind);
        Assert.Contains("not a definition of 'clean'", ex.Message);
        Assert.False(rig.Fs.FileExists(rig.Paths.BaselineManifest));
    }

    [Fact]
    public void ForceCapturesAnywayAndSaysWhatThatMeans()
    {
        var rig = WithInstance();
        rig.StartServer(players: 1);

        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345", force: true);

        Assert.True(rig.Output.Warned("--force: capturing while the rig is in use"));
        Assert.True(rig.Output.Warned("half-written is about to become the definition of a clean rig"));
        Assert.NotNull(rig.Baseline.Read());
    }

    [Fact]
    public void WhatIfWritesNothing()
    {
        var rig = WithInstance();
        var before = rig.Fs.Fingerprint();

        var capture = rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345", whatIf: true);

        Assert.True(capture.WhatIf);
        Assert.True(capture.Entries > 0);
        Assert.Equal(1, capture.Stored);
        Assert.Equal(before, rig.Fs.Fingerprint());
        Assert.True(rig.Output.Said("--what-if: nothing was written"));
    }

    [Fact]
    public void TheStoreIsPrunedWhenAnInstanceDisappears()
    {
        var rig = WithInstance();
        rig.AddInstance("c2");
        rig.RegisterInstanceRoot(("c1", RigFixture.InstancesRoot), ("c2", RigFixture.InstancesRoot));
        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");
        var goneKey = $"client/c2/bepinex-config/{SavePathOverride.ConfigLeaf}";
        Assert.True(rig.Fs.FileExists(rig.Baseline.StoredPath(goneKey)));

        rig.Fs.DeleteDirectory(rig.Paths.InstanceDataDir("c2"), recursive: true);
        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");

        Assert.False(rig.Fs.FileExists(rig.Baseline.StoredPath(goneKey)));
        Assert.True(rig.Fs.FileExists(rig.Baseline.StoredPath($"client/c1/bepinex-config/{SavePathOverride.ConfigLeaf}")));
    }

    [Fact]
    public void TheCaptureSaysOutLoudThatItDoesNotProtectAWorld()
    {
        var rig = WithInstance();
        rig.AddServerWorld("Luna");

        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");

        Assert.True(rig.Output.Said("a capture does not protect a world and never did reliably"));
        Assert.True(rig.Output.Said("session.dirty decides them"));
        Assert.True(rig.Output.Said("plugins and seeded mods are recorded for staleness only"));
    }

    // ---- the game version anchor -------------------------------------------

    [Fact]
    public void TheGameVersionComesFromVersionIniAndNotFromAFileThatNeverExisted()
    {
        // The historical defect: this used to read a version.txt at the install root. No
        // such file has ever existed, so it answered 'unknown' on every real install, and
        // staleness skips its comparison on 'unknown', which meant a game update could never
        // mark a baseline stale, the one thing the anchor exists for.
        var rig = new RigFixture();
        rig.Fs.AddFile(Path.Combine(RigFixture.SourceInstall, "version.txt"), "9.9.9.9");
        Assert.Equal("unknown", rig.Baseline.GameVersion());

        GameVersion(rig, "0.2.6420.27780");
        Assert.Equal("0.2.6420.27780", rig.Baseline.GameVersion());
    }

    [Fact]
    public void TheDedicatedServerDataDirectoryIsTheSecondCandidate()
    {
        var rig = new RigFixture();
        rig.Fs.AddFile(
            Path.Combine(RigFixture.SourceInstall, "rocketstation_DedicatedServer_Data", "StreamingAssets", "version.ini"),
            "UPDATEVERSION=0.2.1.2\r\n");

        Assert.Equal("0.2.1.2", rig.Baseline.GameVersion());
    }

    [Fact]
    public void AVersionLineWithNoNumberFallsBackToStrippingThePrefix()
    {
        var rig = new RigFixture();
        rig.Fs.AddFile(
            Path.Combine(RigFixture.SourceInstall, "rocketstation_Data", "StreamingAssets", "version.ini"),
            "UPDATEVERSION = beta-channel\r\n");

        Assert.Equal("beta-channel", rig.Baseline.GameVersion());
    }

    // ---- staleness ---------------------------------------------------------

    [Fact]
    public void AGameUpdateMakesTheBaselineStale()
    {
        var rig = WithInstance();
        GameVersion(rig, "0.2.6420.27780");
        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");
        GameVersion(rig, "0.2.6500.28000");

        var staleness = rig.Baseline.CheckStale(rig.Baseline.Read());

        Assert.True(staleness.Present);
        Assert.True(staleness.Stale);
        Assert.Contains("the game moved from 0.2.6420.27780 to 0.2.6500.28000 since the baseline was captured",
            staleness.Reasons);
    }

    [Fact]
    public void AnUnknownCurrentVersionNeverMarksItStale()
    {
        var rig = WithInstance();
        GameVersion(rig, "0.2.6420.27780");
        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");
        rig.Fs.DeleteFile(Path.Combine(RigFixture.SourceInstall, "rocketstation_Data", "StreamingAssets", "version.ini"));

        Assert.DoesNotContain(rig.Baseline.CheckStale(rig.Baseline.Read()).Reasons, r => r.Contains("the game moved"));
    }

    [Fact]
    public void ANewInstanceMakesTheBaselineStale()
    {
        var rig = WithInstance();
        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");
        rig.AddInstance("c2");

        Assert.Contains("instance 'c2' exists now and was not in the baseline",
            rig.Baseline.CheckStale(rig.Baseline.Read()).Reasons);
    }

    [Fact]
    public void ARemovedInstanceMakesTheBaselineStale()
    {
        var rig = WithInstance();
        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");
        rig.Fs.DeleteDirectory(rig.Paths.InstanceDataDir("c1"), recursive: true);

        Assert.Contains("instance 'c1' was in the baseline and is gone now",
            rig.Baseline.CheckStale(rig.Baseline.Read()).Reasons);
    }

    [Fact]
    public void ARebuiltPluginMakesTheBaselineStale()
    {
        var rig = WithInstance();
        var plugin = Path.Combine(RigFixture.InstancesRoot, "c1", "BepInEx", "plugins", "ClientDriver.dll");
        rig.Fs.AddFile(plugin, "build 1");
        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");

        rig.Fs.AddFile(plugin, "build 2");

        Assert.Contains("1 deployed plugin or seeded mod file(s) differ from the baseline (a rebuild or a re-seed since it was captured)",
            rig.Baseline.CheckStale(rig.Baseline.Read()).Reasons);
    }

    [Fact]
    public void ANewPluginFileAlsoCountsAsDrift()
    {
        var rig = WithInstance();
        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");
        rig.Fs.AddFile(Path.Combine(RigFixture.InstancesRoot, "c1", "BepInEx", "plugins", "New.dll"), "new");

        Assert.Contains(rig.Baseline.CheckStale(rig.Baseline.Read()).Reasons, r => r.Contains("differ from the baseline"));
    }

    [Fact]
    public void ReCapturingIsTheFix()
    {
        var rig = WithInstance();
        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");
        rig.AddInstance("c2");
        Assert.True(rig.Baseline.CheckStale(rig.Baseline.Read()).Stale);

        rig.RegisterInstanceRoot(("c1", RigFixture.InstancesRoot), ("c2", RigFixture.InstancesRoot));
        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");

        Assert.False(rig.Baseline.CheckStale(rig.Baseline.Read()).Stale);
    }

    [Fact]
    public void AFreshBaselineHasNoReasons()
    {
        var rig = WithInstance();
        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");

        var staleness = rig.Baseline.CheckStale(rig.Baseline.Read());

        Assert.True(staleness.Present);
        Assert.False(staleness.Stale);
        Assert.Empty(staleness.Reasons);
    }

    // ---- restore from the baseline -----------------------------------------

    [Fact]
    public void AChangedConfigIsRestoredByteForByte()
    {
        var rig = WithInstance();
        var cfg = Path.Combine(RigFixture.InstancesRoot, "c1", "BepInEx", "config", "net.example.cfg");
        rig.Fs.AddFile(cfg, "Value = correct");
        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");
        rig.Fs.AddFile(cfg, "Value = flipped by a test");

        rig.Executor.Run(null, new ResetOptions());

        Assert.Equal("Value = correct", rig.Fs.ReadAllText(cfg));
    }

    [Fact]
    public void ADeletedConfigIsRestoredRatherThanStayingMissing()
    {
        // The target is derived from the KEY, not from the live file.
        var rig = WithInstance();
        var cfg = Path.Combine(RigFixture.InstancesRoot, "c1", "BepInEx", "config", "net.example.cfg");
        rig.Fs.AddFile(cfg, "Value = correct");
        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");
        rig.Fs.DeleteFile(cfg);

        rig.Executor.Run(null, new ResetOptions());

        Assert.True(rig.Fs.FileExists(cfg));
        Assert.Equal("Value = correct", rig.Fs.ReadAllText(cfg));
    }

    [Fact]
    public void AConfigInventedAfterTheCaptureIsDeleted()
    {
        var rig = WithInstance();
        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");
        var invented = Path.Combine(RigFixture.InstancesRoot, "c1", "BepInEx", "config", "net.invented.cfg");
        rig.Fs.AddFile(invented, "created by a previous test's plugin");

        rig.Executor.Run(null, new ResetOptions());

        Assert.False(rig.Fs.FileExists(invented));
    }

    [Fact]
    public void AnUnchangedConfigPlansNothingAtAll()
    {
        var rig = WithInstance();
        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");

        var plan = rig.Planner.Build();

        Assert.DoesNotContain(plan.Actions, a => a.Kind == ResetActionKind.RestoreBaselineFile);
        Assert.DoesNotContain(plan.Actions, a => a.Kind == ResetActionKind.CopyConfigTree);
    }

    [Fact]
    public void ABaselineEntryWithNoStoredContentIsSkippedRatherThanOverwritingWithNothing()
    {
        var rig = WithInstance();
        var cfg = Path.Combine(RigFixture.InstancesRoot, "c1", "BepInEx", "config", "net.example.cfg");
        rig.Fs.AddFile(cfg, "Value = correct");
        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");
        rig.Fs.DeleteFile(rig.Baseline.StoredPath("client/c1/bepinex-config/net.example.cfg"));
        rig.Fs.AddFile(cfg, "Value = flipped");

        rig.Executor.Run(null, new ResetOptions());

        Assert.Equal("Value = flipped", rig.Fs.ReadAllText(cfg));
    }

    [Fact]
    public void TheRedirectSurvivesABaselineDrivenRestore()
    {
        var rig = WithInstance();
        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");
        var bep = Path.Combine(RigFixture.InstancesRoot, "c1", "BepInEx");
        rig.Fs.AddFile(SavePathOverride.ConfigPath(bep), "SavePathOverride = C:\\wrong");

        rig.Executor.Run(null, new ResetOptions());

        Assert.Equal(rig.Paths.InstanceUserData("c1"), SavePathOverride.Read(rig.Fs, bep));
    }

    [Fact]
    public void ABaselineThatMissesAnInstanceFallsBackToTheSourceInstallAndSaysSo()
    {
        var rig = WithInstance();
        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");
        rig.AddInstance("c2");
        rig.RegisterInstanceRoot(("c1", RigFixture.InstancesRoot), ("c2", RigFixture.InstancesRoot));

        var plan = rig.Planner.Build();

        Assert.Contains(plan.Actions, a => a.Instance == "c2" && a.Kind == ResetActionKind.CopyConfigTree);
        var report = plan.Reports.Single(r => r.Kind == ResetReportKind.BaselineMissesInstance);
        Assert.True(report.Warn);
        Assert.Equal("c2", report.Instance);
    }

    [Fact]
    public void TheServerConfigIsRestoredFromTheBaselineRatherThanMerelyReported()
    {
        var rig = new RigFixture();
        var cfg = Path.Combine(rig.Paths.DediInstall, "BepInEx", "config", "net.server.cfg");
        rig.Fs.AddFile(cfg, "Value = correct");
        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");
        rig.Fs.AddFile(cfg, "Value = flipped");

        rig.Executor.Run(null, new ResetOptions());

        Assert.Equal("Value = correct", rig.Fs.ReadAllText(cfg));
    }

    [Fact]
    public void TheScenarioRunnerConfigIsExemptFromBaselineRestoreSoBlankingIsNotFought()
    {
        var rig = new RigFixture();
        var cfg = Path.Combine(rig.Paths.DediInstall, "BepInEx", "config", "net.scenariorunner.cfg");
        rig.Fs.AddFile(cfg, "Scenario = captured-value\r\n");
        rig.Fs.AddFile(Path.Combine(rig.Paths.DediInstall, "BepInEx", "config", "net.other.cfg"), "x = 1");
        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");
        rig.Fs.AddFile(cfg, "Scenario = a-later-probe\r\n");

        var plan = rig.Planner.Build();
        Assert.DoesNotContain(plan.Actions, a => a.Kind == ResetActionKind.RestoreBaselineFile
                                                 && a.Path.EndsWith("net.scenariorunner.cfg", StringComparison.OrdinalIgnoreCase));

        rig.Executor.Run(plan, new ResetOptions());
        Assert.Equal("", ConfigFile.GetSetting(rig.Fs, cfg, "Scenario"));
    }

    [Fact]
    public void ServerModconfigIsCoveredByItsOwnScope()
    {
        var rig = new RigFixture();
        rig.Fs.AddFile(Path.Combine(rig.Paths.DediInstall, "BepInEx", "config", "net.a.cfg"), "x = 1");
        rig.Fs.AddFile(Path.Combine(rig.Paths.DediInstall, "modconfig.xml"), "<config>correct</config>");
        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");
        rig.Fs.AddFile(Path.Combine(rig.Paths.DediInstall, "modconfig.xml"), "<config>mangled</config>");

        rig.Executor.Run(null, new ResetOptions());

        Assert.Equal("<config>correct</config>", rig.Fs.ReadAllText(Path.Combine(rig.Paths.DediInstall, "modconfig.xml")));
    }

    [Fact]
    public void ARestoreFromTheBaselineIsIdempotent()
    {
        var rig = WithInstance();
        var cfg = Path.Combine(RigFixture.InstancesRoot, "c1", "BepInEx", "config", "net.example.cfg");
        rig.Fs.AddFile(cfg, "Value = correct");
        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");
        rig.Fs.AddFile(cfg, "Value = flipped");

        rig.Executor.Run(null, new ResetOptions());
        var after = rig.Fs.Fingerprint();
        rig.Executor.Run(null, new ResetOptions());

        Assert.Equal(after, rig.Fs.Fingerprint());
    }

    [Fact]
    public void AnInstanceNameContainingBracketsStillMatchesItsPrefix()
    {
        // PowerShell's -like treats '[' and ']' as metacharacters, so an instance named
        // like this broke the prefix filter silently.
        var rig = new RigFixture();
        rig.AddInstance("c[1]");
        rig.RegisterInstanceRoot(("c[1]", RigFixture.InstancesRoot));
        var cfg = Path.Combine(RigFixture.InstancesRoot, "c[1]", "BepInEx", "config", "net.example.cfg");
        rig.Fs.AddFile(cfg, "Value = correct");
        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");
        rig.Fs.AddFile(cfg, "Value = flipped");

        rig.Executor.Run(null, new ResetOptions());

        Assert.Equal("Value = correct", rig.Fs.ReadAllText(cfg));
    }

    [Fact]
    public void ABrokenManifestDegradesToNoBaselineRatherThanThrowing()
    {
        var rig = WithInstance();
        rig.Fs.AddDirectory(rig.Paths.BaselineDir);
        rig.Fs.AddFile(rig.Paths.BaselineManifest, "{ not json");

        Assert.Null(rig.Baseline.Read());
        var plan = rig.Planner.Build();
        Assert.Contains(plan.Reports, r => r.Kind == ResetReportKind.BaselineAbsent);
        Assert.Contains(plan.Actions, a => a.Kind == ResetActionKind.CopyConfigTree);
    }

    [Fact]
    public void AManifestEntryWithNoKeyIsSkipped()
    {
        var rig = new RigFixture();
        rig.Fs.AddDirectory(rig.Paths.BaselineDir);
        rig.Fs.AddFile(rig.Paths.BaselineManifest,
            """{"capturedUtc":"2026-08-14T12:00:00Z","files":[{"class":"config"},{"key":"server/modconfig.xml","class":"config"}]}""");

        var baseline = rig.Baseline.Read()!;

        Assert.Single(baseline.Files);
        Assert.True(baseline.Files.ContainsKey("server/modconfig.xml"));
    }

    [Fact]
    public void BaselineKeysAreMatchedCaseInsensitively()
    {
        var rig = WithInstance();
        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");

        var baseline = rig.Baseline.Read()!;

        Assert.True(baseline.Files.ContainsKey($"CLIENT/C1/BEPINEX-CONFIG/{SavePathOverride.ConfigLeaf}"));
    }

    [Fact]
    public void TheMutableSurfaceIsAnAllowListAndExcludesLogsPidFilesAndSaveRoots()
    {
        var rig = WithInstance();
        rig.Fs.AddFile(Path.Combine(rig.Paths.InstanceDataDir("c1"), "logs", "unity-1.log"), "log");
        rig.Fs.AddFile(rig.Paths.InstancePidFile("c1"), "5001");
        rig.Fs.AddFile(Path.Combine(rig.Paths.InstanceDataDir("c1"), "imgui.ini"), "layout");
        rig.AddClientWorld("c1", "AWorld");

        var keys = rig.Surface.Enumerate().Select(static r => r.Key).ToArray();

        Assert.DoesNotContain(keys, k => k.Contains("logs"));
        Assert.DoesNotContain(keys, k => k.Contains("game.pid"));
        Assert.DoesNotContain(keys, k => k.Contains("imgui.ini"));
        Assert.DoesNotContain(keys, k => k.Contains("client/c1/saves"));
    }

    [Fact]
    public void SurfaceKeysUseForwardSlashesSoTheySurviveAnInstancesRootChange()
    {
        var rig = WithInstance();
        rig.Fs.AddFile(Path.Combine(RigFixture.InstancesRoot, "c1", "BepInEx", "plugins", "Sub", "Nested.dll"), "x");

        var keys = rig.Surface.Enumerate().Select(static r => r.Key).ToArray();

        Assert.Contains("client/c1/plugins/Sub/Nested.dll", keys);
        Assert.DoesNotContain(keys, k => k.Contains('\\'));
    }

    [Fact]
    public void TheServerSurfaceCoversConfigsPluginsModconfigAndWorlds()
    {
        var rig = new RigFixture();
        rig.Fs.AddFile(Path.Combine(rig.Paths.DediInstall, "BepInEx", "config", "net.a.cfg"), "x");
        rig.Fs.AddFile(Path.Combine(rig.Paths.DediInstall, "BepInEx", "plugins", "ScenarioRunner.dll"), "x");
        rig.Fs.AddFile(Path.Combine(rig.Paths.DediInstall, "modconfig.xml"), "<c/>");
        rig.AddServerWorld("Luna");

        var records = rig.Surface.Enumerate();

        Assert.Contains(records, r => r.Key == "server/bepinex-config/net.a.cfg" && r.Class == SurfaceClass.Config);
        Assert.Contains(records, r => r.Key == "server/plugins/ScenarioRunner.dll" && r.Class == SurfaceClass.Payload);
        Assert.Contains(records, r => r.Key == "server/modconfig.xml" && r.Class == SurfaceClass.Config);
        Assert.Contains(records, r => r.Key == "server/saves/Luna" && r.Class == SurfaceClass.World);
    }
}
