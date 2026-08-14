using TestRig.Core.Session;
using TestRig.Tests.Session.Fakes;
using Xunit;

namespace TestRig.Tests.Session;

/// <summary>
/// The planner. Ported from rig-reset.tests.ps1 sections plan, client, pid, server,
/// instancesroot, savepath, busy, robust and whatif.
/// </summary>
public sealed class ResetPlanTests
{
    private static RigFixture Provisioned(string instance = "c1", string role = "client")
    {
        var rig = new RigFixture();
        rig.AddInstance(instance, role);
        rig.RegisterInstanceRoot((instance, RigFixture.InstancesRoot));
        rig.Fs.AddFile(
            Path.Combine(RigFixture.SourceInstall, "BepInEx", "config", SavePathOverride.ConfigLeaf),
            "# stock launchpad config");
        return rig;
    }

    [Fact]
    public void PlanningIsPureDataAndMovesNotOneByte()
    {
        var rig = Provisioned();
        rig.Fs.AddFile(Path.Combine(rig.Paths.InstanceDataDir("c1"), "setting.xml"), "<x/>");
        rig.AddServerWorld("Luna");
        rig.Marker.Write("abc12345", "p", "Start");
        rig.AddServerWorld("MadeByTheTest");
        var before = rig.Fs.Fingerprint();

        var plan = rig.Planner.Build();

        Assert.NotEmpty(plan.Actions);
        Assert.Equal(before, rig.Fs.Fingerprint());
    }

    [Fact]
    public void AnEmptyRigPlansNothing()
    {
        var rig = new RigFixture();

        var plan = rig.Planner.Build();

        Assert.Empty(plan.Actions);
        Assert.Empty(plan.Instances);
        Assert.Equal(0, plan.WorldDeleteCount);
    }

    [Fact]
    public void EveryProvisionedInstanceIsFound()
    {
        var rig = new RigFixture();
        rig.AddInstance("c1");
        rig.AddInstance("h1", role: "host");

        Assert.Equal(["c1", "h1"], rig.Planner.Build().Instances);
    }

    [Fact]
    public void OnlyTargetsThatExistBecomeActions()
    {
        var rig = Provisioned();

        var plan = rig.Planner.Build();

        Assert.DoesNotContain(plan.Actions, a => a.Label == "setting.xml" && a.Instance == "c1");
        Assert.DoesNotContain(plan.Actions, a => a.Label == "imgui.ini");
        Assert.DoesNotContain(plan.Actions, a => a.Label.Contains("log(s)"));
    }

    [Fact]
    public void EveryClientTargetIsPlannedWhenItExists()
    {
        var rig = Provisioned();
        var data = rig.Paths.InstanceDataDir("c1");
        var bep = Path.Combine(RigFixture.InstancesRoot, "c1", "BepInEx");
        rig.Fs.AddFile(Path.Combine(data, "setting.xml"), "<x/>");
        rig.Fs.AddFile(Path.Combine(data, "imgui.ini"), "layout");
        rig.Fs.AddFile(Path.Combine(data, "logs", "unity-1.log"), "log");
        rig.Fs.AddFile(Path.Combine(bep, "LogOutput.log"), "log");
        rig.Fs.AddFile(Path.Combine(bep, "cache", "asm.dat"), "cache");
        rig.Fs.AddFile(Path.Combine(bep, "inspector", "requests", "r.json"), "{}");
        rig.Fs.AddFile(Path.Combine(bep, "inspector", "snapshots", "s.json"), "{}");

        var labels = rig.Planner.Build().Actions.Where(a => a.Instance == "c1").Select(a => a.Label).ToArray();

        Assert.Contains("setting.xml", labels);
        Assert.Contains("imgui.ini", labels);
        Assert.Contains("1 log(s)", labels);
        Assert.Contains("LogOutput.log", labels);
        Assert.Contains("BepInEx cache", labels);
        Assert.Contains("1 inspector request(s)", labels);
        Assert.Contains("1 inspector snapshot(s)", labels);
        Assert.Contains("SavePathOverride re-applied", labels);
        Assert.Contains("BepInEx config re-copied", labels);
    }

    [Fact]
    public void SavePathOverrideIsPlannedAfterTheConfigCopyAndMarkedAsFollowingIt()
    {
        // Nothing in the planner matters more than this ordering: the copy wipes the
        // redirect, and an instance without it writes into the developer's tier-1 folder.
        var rig = Provisioned();

        var actions = rig.Planner.Build().Actions.Where(a => a.Instance == "c1").ToArray();
        var copy = Array.FindIndex(actions, a => a.Kind == ResetActionKind.CopyConfigTree);
        var reapply = Array.FindIndex(actions, a => a.Kind == ResetActionKind.ReapplySavePathOverride);

        Assert.True(copy >= 0);
        Assert.True(reapply > copy);
        Assert.True(actions[reapply].AfterCopy);
        Assert.Equal(rig.Paths.InstanceUserData("c1"), actions[reapply].Target);
        Assert.Equal("client", actions[reapply].Role);
    }

    [Fact]
    public void SavePathOverrideIsPlannedEvenWhenNoConfigWriteHappens()
    {
        // Re-writing it is idempotent, and the cost of skipping it once is a world in the
        // developer's tier-1 folder.
        var rig = new RigFixture();
        rig.AddInstance("c1");
        rig.RegisterInstanceRoot(("c1", RigFixture.InstancesRoot));

        var actions = rig.Planner.Build().Actions.Where(a => a.Instance == "c1").ToArray();
        var reapply = actions.Single(a => a.Kind == ResetActionKind.ReapplySavePathOverride);

        Assert.False(reapply.AfterCopy);
        Assert.DoesNotContain(actions, a => a.Kind == ResetActionKind.CopyConfigTree);
        Assert.Contains(rig.Planner.Build().Reports, r => r.Kind == ResetReportKind.ConfigCopySkipped && r.Warn);
    }

    [Fact]
    public void AnUnknownRoleIsCarriedThroughSoTheExecutorCanTreatItAsAHost()
    {
        var rig = new RigFixture();
        rig.Fs.AddDirectory(rig.Paths.InstanceDataDir("c1"));
        rig.RegisterInstanceRoot(("c1", RigFixture.InstancesRoot));
        rig.Fs.AddDirectory(Path.Combine(RigFixture.InstancesRoot, "c1", "BepInEx"));

        var reapply = rig.Planner.Build().Actions.Single(a => a.Kind == ResetActionKind.ReapplySavePathOverride);

        Assert.Equal("unknown", reapply.Role);
    }

    // ---- the instance tree registry ----------------------------------------

    [Fact]
    public void TheTreeRootComesFromTheRegistryNotFromTheConfiguredRoot()
    {
        // The regression fix: the reset used to join ITS configured root to each instance
        // name, which is right only when the two happen to agree. With trees on another
        // volume it found no BepInEx tree and silently skipped the config re-copy and the
        // redirect, which is half of what the reset is for.
        var rig = new RigFixture();
        rig.AddInstance("c1", withTree: false);
        rig.RegisterInstanceRoot(("c1", @"F:\other-volume"));
        rig.Fs.AddDirectory(Path.Combine(@"F:\other-volume", "c1", "BepInEx", "config"));
        rig.Fs.AddFile(Path.Combine(@"F:\other-volume", "c1", "BepInEx", "config", SavePathOverride.ConfigLeaf), "x = y");
        rig.Fs.AddDirectory(Path.Combine(RigFixture.SourceInstall, "BepInEx", "config"));

        var actions = rig.Planner.Build().Actions.Where(a => a.Instance == "c1").ToArray();

        Assert.Contains(actions, a => a.Kind == ResetActionKind.ReapplySavePathOverride
                                      && a.Path.StartsWith(@"F:\other-volume", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(rig.Planner.Build().Reports, r => r.Kind == ResetReportKind.NoTree);
    }

    [Fact]
    public void AnEntryWithNoRecordedRootFallsBackAndTheReportSaysSo()
    {
        var rig = new RigFixture();
        rig.AddInstance("c1", withTree: false);

        var report = rig.Planner.Build().Reports.Single(r => r.Kind == ResetReportKind.NoTree);

        Assert.True(report.Warn);
        Assert.Contains("the configured instances root", report.Detail);
        Assert.Contains("SavePathOverride was NOT re-applied", report.Detail);
    }

    [Fact]
    public void ANoTreeReportNamesWhereItLookedAndWhereThatCameFrom()
    {
        var rig = new RigFixture();
        rig.AddInstance("c1", withTree: false);
        rig.RegisterInstanceRoot(("c1", @"F:\gone"));

        var report = rig.Planner.Build().Reports.Single(r => r.Kind == ResetReportKind.NoTree);

        Assert.Contains(@"F:\gone\c1", report.Detail);
        Assert.Contains("recorded in rig.json", report.Detail);
    }

    [Fact]
    public void AMissingOrHalfWrittenRegistryYieldsAnEmptyMapWithoutThrowing()
    {
        var rig = new RigFixture();
        Assert.Empty(rig.Surface.InstanceRootMap());

        rig.Fs.AddFile(rig.Paths.ClientRegistryFile, "[{\"instanceName\":\"c1\",");
        Assert.Empty(rig.Surface.InstanceRootMap());

        rig.Fs.AddFile(rig.Paths.ClientRegistryFile, "[{\"instanceName\":\"c1\"}]");
        Assert.Empty(rig.Surface.InstanceRootMap());

        rig.Fs.AddFile(rig.Paths.ClientRegistryFile, "{}");
        Assert.Empty(rig.Surface.InstanceRootMap());
    }

    [Fact]
    public void TheRegistryFileIsNeverTouchedByThePlan()
    {
        var rig = Provisioned();
        rig.RegisterInstanceRoot(("c1", RigFixture.InstancesRoot));

        var plan = rig.Planner.Build();

        Assert.DoesNotContain(plan.Actions, a => a.Path.EndsWith("rig.json", StringComparison.OrdinalIgnoreCase));
    }

    // ---- pid handling ------------------------------------------------------

    [Fact]
    public void ALiveInstancePidIsPreservedAndReported()
    {
        var rig = Provisioned();
        rig.StartInstance("c1", 5001);

        var plan = rig.Planner.Build();

        Assert.DoesNotContain(plan.Actions, a => a.Label == "stale game.pid");
        var report = plan.Reports.Single(r => r.Kind == ResetReportKind.PreservedLivePid);
        Assert.Contains("game.pid kept: process 5001 is a live game client", report.Detail);
        Assert.False(report.Warn);
    }

    [Fact]
    public void ADeadInstancePidIsDeleted()
    {
        var rig = Provisioned();
        rig.Fs.AddFile(rig.Paths.InstancePidFile("c1"), "5001");

        var plan = rig.Planner.Build();

        Assert.Contains(plan.Actions, a => a.Label == "stale game.pid" && a.Kind == ResetActionKind.DeleteFile);
    }

    [Fact]
    public void ALivePidOfTheWrongImageIsStale()
    {
        var rig = Provisioned();
        rig.Fs.AddFile(rig.Paths.InstancePidFile("c1"), "5001");
        rig.Processes.Add(5001, "notepad");

        Assert.Contains(rig.Planner.Build().Actions, a => a.Label == "stale game.pid");
    }

    [Fact]
    public void GarbageAndEmptyPidFilesAreStale()
    {
        var rig = Provisioned();
        rig.Fs.AddFile(rig.Paths.InstancePidFile("c1"), "not a number");
        Assert.Contains(rig.Planner.Build().Actions, a => a.Label == "stale game.pid");

        rig.Fs.AddFile(rig.Paths.InstancePidFile("c1"), "");
        Assert.Contains(rig.Planner.Build().Actions, a => a.Label == "stale game.pid");
    }

    [Fact]
    public void TheServerAndHostPidsBehaveSymmetrically()
    {
        var rig = new RigFixture();
        rig.Fs.AddFile(rig.Paths.ServerPidFile, "9001");
        rig.Fs.AddFile(rig.Paths.HostPidFile, "9002");

        var labels = rig.Planner.Build().Actions.Select(a => a.Label).ToArray();

        Assert.Contains("stale server.pid", labels);
        Assert.Contains("stale host.pid", labels);
    }

    [Fact]
    public void ALiveServerAndHostAreBothPreservedAndReported()
    {
        var rig = new RigFixture();
        rig.StartServer();
        rig.Fs.AddFile(rig.Paths.HostPidFile, "9002");
        rig.Processes.Add(9002, "pwsh", rig.Clock.UtcNow.AddMinutes(-1));

        var plan = rig.Planner.Build();

        Assert.DoesNotContain(plan.Actions, a => a.Label.Contains("stale server.pid"));
        Assert.DoesNotContain(plan.Actions, a => a.Label.Contains("stale host.pid"));
        Assert.Equal(2, plan.Reports.Count(r => r.Kind == ResetReportKind.PreservedLivePid));
    }

    [Fact]
    public void ControlCmdIsKeptWhileTheServerLives()
    {
        var rig = new RigFixture();
        rig.StartServer();
        rig.Fs.AddFile(rig.Paths.ControlCmdFile, "save");

        Assert.DoesNotContain(rig.Planner.Build().Actions, a => a.Label == "stale control.cmd");
    }

    [Fact]
    public void ControlCmdIsDeletedWhenNeitherServerNorHostIsLive()
    {
        var rig = new RigFixture();
        rig.Fs.AddFile(rig.Paths.ControlCmdFile, "save");

        Assert.Contains(rig.Planner.Build().Actions, a => a.Label == "stale control.cmd");
    }

    // ---- the server half ---------------------------------------------------

    [Fact]
    public void TheScenarioValueIsBlankedWhenItIsSetAndTheOldValueIsNamed()
    {
        var rig = new RigFixture();
        var cfg = Path.Combine(rig.Paths.DediInstall, "BepInEx", "config", "net.scenariorunner.cfg");
        rig.Fs.AddFile(cfg, "# a comment\r\nScenario = spp-settings-merge-verify\r\nOther = keep\r\n");

        var action = rig.Planner.Build().Actions.Single(a => a.Kind == ResetActionKind.BlankSetting);

        Assert.Equal("Scenario", action.Setting);
        Assert.Contains("was 'spp-settings-merge-verify'", action.Label);
        Assert.Contains("selects which probe fires on the next boot", action.Reason);
    }

    [Fact]
    public void TheScenarioValueIsNotBlankedWhenItIsAlreadyEmpty()
    {
        var rig = new RigFixture();
        var cfg = Path.Combine(rig.Paths.DediInstall, "BepInEx", "config", "net.scenariorunner.cfg");
        rig.Fs.AddFile(cfg, "Scenario = \r\n");

        Assert.DoesNotContain(rig.Planner.Build().Actions, a => a.Kind == ResetActionKind.BlankSetting);
    }

    [Fact]
    public void TheFourServerDropDirectoriesAreCleared()
    {
        var rig = new RigFixture();
        foreach (var rel in new[]
        {
            @"BepInEx\scenariorunner\requests", @"BepInEx\scenariorunner\give",
            @"BepInEx\inspector\requests", @"BepInEx\inspector\snapshots",
        })
        {
            rig.Fs.AddFile(Path.Combine(rig.Paths.DediInstall, rel, "drop.json"), "{}");
        }

        var labels = rig.Planner.Build().Actions
            .Where(a => a.Kind == ResetActionKind.DeleteContents).Select(a => a.Label).ToArray();

        Assert.Contains("1 scenariorunner request(s)", labels);
        Assert.Contains("1 scenariorunner give file(s)", labels);
        Assert.Contains("1 inspector request(s)", labels);
        Assert.Contains("1 inspector snapshot(s)", labels);
    }

    [Fact]
    public void TheServersSettingXmlIsDeleted()
    {
        var rig = new RigFixture();
        rig.Fs.AddFile(rig.Paths.ServerSettingXml, "<settings/>");

        var action = rig.Planner.Build().Actions.Single(a => a.Path == rig.Paths.ServerSettingXml);

        Assert.Equal("setting.xml", action.Label);
        Assert.Contains("stale SavePath and UseSteamP2P", action.Reason);
    }

    [Fact]
    public void ServerConfigDriftIsReportedWhenNoBaselineCoversTheServer()
    {
        var rig = new RigFixture();
        rig.State.Save(RigTime.Stamp(rig.Clock.UtcNow.AddHours(-1)));
        var cfgDir = Path.Combine(rig.Paths.DediInstall, "BepInEx", "config");
        rig.Fs.AddFile(Path.Combine(cfgDir, "net.someplugin.cfg"), "Value = 1");
        rig.Fs.SetLastWrite(Path.Combine(cfgDir, "net.someplugin.cfg"), rig.Clock.UtcNow);
        rig.Fs.AddFile(Path.Combine(cfgDir, "net.scenariorunner.cfg"), "Scenario = ");
        rig.Fs.SetLastWrite(Path.Combine(cfgDir, "net.scenariorunner.cfg"), rig.Clock.UtcNow);

        var report = rig.Planner.Build().Reports.Single(r => r.Kind == ResetReportKind.ConfigTouched);

        Assert.True(report.Warn);
        Assert.Contains("net.someplugin.cfg", report.Detail);
        Assert.DoesNotContain("net.scenariorunner.cfg", report.Detail);
    }

    [Fact]
    public void AConfigOlderThanTheLastResetIsNotReportedAsDrift()
    {
        var rig = new RigFixture();
        rig.State.Save(RigTime.Stamp(rig.Clock.UtcNow));
        var cfgDir = Path.Combine(rig.Paths.DediInstall, "BepInEx", "config");
        rig.Fs.AddFile(Path.Combine(cfgDir, "net.someplugin.cfg"), "Value = 1");
        rig.Fs.SetLastWrite(Path.Combine(cfgDir, "net.someplugin.cfg"), rig.Clock.UtcNow.AddHours(-2));

        Assert.DoesNotContain(rig.Planner.Build().Reports, r => r.Kind == ResetReportKind.ConfigTouched);
    }

    // ---- stale mods --------------------------------------------------------

    [Fact]
    public void AStaleSeededModIsReportedAndNeverDeleted()
    {
        var rig = new RigFixture();
        rig.AddInstance("c1");
        var seeded = Path.Combine(rig.Paths.InstanceUserData("c1"), "mods", "SprayPaintPlus");
        rig.Fs.AddFile(Path.Combine(seeded, "SprayPaintPlus.dll"), "old");
        rig.Fs.SetLastWrite(Path.Combine(seeded, "SprayPaintPlus.dll"), rig.Clock.UtcNow.AddDays(-2));
        var source = Path.Combine(RigFixture.UserData, "mods", "SprayPaintPlus");
        rig.Fs.AddFile(Path.Combine(source, "SprayPaintPlus.dll"), "new");
        rig.Fs.SetLastWrite(Path.Combine(source, "SprayPaintPlus.dll"), rig.Clock.UtcNow);

        var plan = rig.Planner.Build();
        var report = plan.Reports.Single(r => r.Kind == ResetReportKind.StaleMod);

        Assert.True(report.Warn);
        Assert.Contains("seeded mod 'SprayPaintPlus' is older than the source tree", report.Detail);
        Assert.Contains("testrig create --target c1 --force", report.Detail);
        Assert.DoesNotContain(plan.Actions, a => a.Path.Contains("SprayPaintPlus"));
    }

    [Fact]
    public void AnUpToDateSeededModIsNotReported()
    {
        var rig = new RigFixture();
        rig.AddInstance("c1");
        var seeded = Path.Combine(rig.Paths.InstanceUserData("c1"), "mods", "SprayPaintPlus");
        rig.Fs.AddFile(Path.Combine(seeded, "SprayPaintPlus.dll"), "same");
        var source = Path.Combine(RigFixture.UserData, "mods", "SprayPaintPlus");
        rig.Fs.AddFile(Path.Combine(source, "SprayPaintPlus.dll"), "same");

        Assert.DoesNotContain(rig.Planner.Build().Reports, r => r.Kind == ResetReportKind.StaleMod);
    }

    [Fact]
    public void AModWithNoPeerInTheSourceTreeIsIgnored()
    {
        var rig = new RigFixture();
        rig.AddInstance("c1");
        rig.Fs.AddFile(Path.Combine(rig.Paths.InstanceUserData("c1"), "mods", "OnlyHere", "x.dll"), "x");

        Assert.DoesNotContain(rig.Planner.Build().Reports, r => r.Kind == ResetReportKind.StaleMod);
    }

    // ---- the busy gate -----------------------------------------------------

    [Fact]
    public void AnIdleRigAllowsAReset()
    {
        var rig = new RigFixture();

        var gate = rig.Planner.CheckGate();

        Assert.True(gate.Allowed);
        Assert.Equal("", gate.Reason);
    }

    [Fact]
    public void AnIdleRunningServerBlocksAResetEvenThoughItIsNotLockBusy()
    {
        // Stricter than the lock's idea of busy, deliberately: an idle server still writes
        // to the folders being deleted.
        var rig = new RigFixture();
        rig.StartServer(players: 0);

        var gate = rig.Planner.CheckGate();

        Assert.False(gate.Allowed);
        Assert.Contains("the dedicated server process is alive", gate.Reason);
        Assert.False(rig.Busy.Probe().Busy);
    }

    [Fact]
    public void ALiveClientInstanceBlocksAReset()
    {
        var rig = new RigFixture();
        rig.AddInstance("c1");
        rig.StartInstance("c1", 5001);

        var gate = rig.Planner.CheckGate();

        Assert.False(gate.Allowed);
        Assert.Contains("c1=client", gate.Reason);
    }

    [Fact]
    public void AnOrphanBlocksAResetEvenThoughItIsNeverLockBusy()
    {
        // The reset counts it because an orphan writes to exactly the folders being deleted.
        var rig = new RigFixture();
        rig.Processes.Add(7001, rig.Paths.ServerImage);
        rig.ImagePaths[7001] = Path.Combine(rig.Paths.DediInstall, "s.exe");

        var gate = rig.Planner.CheckGate();

        Assert.False(gate.Allowed);
        Assert.Contains("untracked rig game process(es) are running", gate.Reason);
        Assert.Contains("pid 7001", gate.Reason);
        Assert.False(rig.Busy.Probe().Busy);
    }

    // ---- the baseline report -----------------------------------------------

    [Fact]
    public void NoBaselineIsReportedAsAbsentAndWarns()
    {
        var rig = new RigFixture();

        var report = rig.Planner.Build().Reports.Single(r => r.Kind == ResetReportKind.BaselineAbsent);

        Assert.True(report.Warn);
        Assert.Contains("no baseline has ever been captured", report.Detail);
    }

    [Fact]
    public void AFreshBaselineIsReportedAsUsedWithoutWarning()
    {
        var rig = new RigFixture();
        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");

        var report = rig.Planner.Build().Reports.Single(r => r.Kind == ResetReportKind.BaselineUsed);

        Assert.False(report.Warn);
        Assert.Contains("restoring to the baseline captured", report.Detail);
    }

    [Fact]
    public void AStaleBaselineWarnsButNeverBlocks()
    {
        var rig = new RigFixture();
        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");
        rig.AddInstance("newInstance");

        var plan = rig.Planner.Build();
        var report = plan.Reports.Single(r => r.Kind == ResetReportKind.BaselineStale);

        Assert.True(report.Warn);
        Assert.Contains("the baseline is STALE", report.Detail);
        Assert.Contains("newInstance", report.Detail);
        Assert.NotEmpty(plan.Actions);
    }

    [Fact]
    public void PlanningDoesNotThrowOnAHalfProvisionedRig()
    {
        var rig = new RigFixture();
        rig.Fs.AddDirectory(rig.Paths.InstanceDataDir("half"));
        rig.Fs.AddFile(rig.Paths.InstanceManifest("half"), "not json at all");

        var plan = rig.Planner.Build();

        Assert.Contains("half", plan.Instances);
        Assert.Contains(plan.Reports, r => r.Kind == ResetReportKind.NoTree);
    }

    [Fact]
    public void NoSourceInstallResolvesToNullRatherThanAGuess()
    {
        var rig = new RigFixture();

        Assert.Null(rig.Planner.ResolveSourceInstall());

        rig.Fs.AddDirectory(Path.Combine(RigFixture.SourceInstall, "BepInEx", "config"));
        Assert.Equal(RigFixture.SourceInstall, rig.Planner.ResolveSourceInstall());
    }

    [Fact]
    public void ActionsAreGroupedByInstanceOrHalf()
    {
        var rig = Provisioned();
        rig.Fs.AddFile(Path.Combine(rig.Paths.InstanceDataDir("c1"), "imgui.ini"), "x");
        rig.Fs.AddFile(rig.Paths.ServerSettingXml, "<x/>");

        var plan = rig.Planner.Build();

        Assert.Contains(plan.Actions, a => a.Group == "c1");
        Assert.Contains(plan.Actions, a => a.Group == "server");
        Assert.Equal("instance 'c1'", plan.Actions.First(a => a.Instance == "c1").Who);
        Assert.Equal("the server half", plan.Actions.First(a => a.Instance is null).Who);
    }
}
