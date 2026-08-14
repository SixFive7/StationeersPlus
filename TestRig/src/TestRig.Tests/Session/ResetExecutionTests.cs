using TestRig.Core.Session;
using TestRig.Tests.Session.Fakes;
using Xunit;

namespace TestRig.Tests.Session;

/// <summary>
/// Running a plan. Ported from rig-reset.tests.ps1 sections client, server, whatif, busy,
/// lock, release and robust.
/// </summary>
public sealed class ResetExecutionTests
{
    private static RigFixture Provisioned(string instance = "c1", string role = "client")
    {
        var rig = new RigFixture();
        rig.AddInstance(instance, role);
        rig.RegisterInstanceRoot((instance, RigFixture.InstancesRoot));
        // The real source install carries this file, and CopyConfigTree deletes any .cfg
        // the source lacks, which would otherwise take the redirect with it.
        rig.Fs.AddFile(
            Path.Combine(RigFixture.SourceInstall, "BepInEx", "config", SavePathOverride.ConfigLeaf),
            "# stock launchpad config");
        return rig;
    }

    [Fact]
    public void EveryClientTargetIsActuallyGoneAfterARun()
    {
        var rig = Provisioned();
        var data = rig.Paths.InstanceDataDir("c1");
        var bep = Path.Combine(RigFixture.InstancesRoot, "c1", "BepInEx");
        rig.Fs.AddFile(Path.Combine(data, "setting.xml"), "<x/>");
        rig.Fs.AddFile(Path.Combine(data, "imgui.ini"), "layout");
        rig.Fs.AddFile(Path.Combine(data, "logs", "unity-1.log"), "log");
        rig.Fs.AddFile(Path.Combine(bep, "LogOutput.log"), "log");
        rig.Fs.AddFile(Path.Combine(bep, "inspector", "requests", "r.json"), "{}");

        rig.Executor.Run(null, new ResetOptions());

        Assert.False(rig.Fs.FileExists(Path.Combine(data, "setting.xml")));
        Assert.False(rig.Fs.FileExists(Path.Combine(data, "imgui.ini")));
        Assert.False(rig.Fs.FileExists(Path.Combine(data, "logs", "unity-1.log")));
        Assert.False(rig.Fs.FileExists(Path.Combine(bep, "LogOutput.log")));
        Assert.False(rig.Fs.FileExists(Path.Combine(bep, "inspector", "requests", "r.json")));
    }

    [Fact]
    public void TheLogAndSaveDirectoriesThemselvesSurvive()
    {
        var rig = Provisioned();
        var logs = Path.Combine(rig.Paths.InstanceDataDir("c1"), "logs");
        rig.Fs.AddFile(Path.Combine(logs, "unity-1.log"), "log");

        rig.Executor.Run(null, new ResetOptions());

        Assert.True(rig.Fs.DirectoryExists(logs));
    }

    [Fact]
    public void TheBepInExCacheIsEmptiedAndRecreated()
    {
        var rig = Provisioned();
        var cache = Path.Combine(RigFixture.InstancesRoot, "c1", "BepInEx", "cache");
        rig.Fs.AddFile(Path.Combine(cache, "asm.dat"), "cached");

        rig.Executor.Run(null, new ResetOptions());

        Assert.True(rig.Fs.DirectoryExists(cache));
        Assert.False(rig.Fs.FileExists(Path.Combine(cache, "asm.dat")));
    }

    [Fact]
    public void SeededModsAndDeployedPluginsArePreserved()
    {
        var rig = Provisioned();
        var mod = Path.Combine(rig.Paths.InstanceUserData("c1"), "mods", "SprayPaintPlus", "x.dll");
        var plugin = Path.Combine(RigFixture.InstancesRoot, "c1", "BepInEx", "plugins", "ClientDriver.dll");
        rig.Fs.AddFile(mod, "mod");
        rig.Fs.AddFile(plugin, "plugin");

        rig.Executor.Run(null, new ResetOptions());

        Assert.True(rig.Fs.FileExists(mod));
        Assert.True(rig.Fs.FileExists(plugin));
    }

    [Fact]
    public void ADeliberateFileOutsideTheRecordedSurfaceSurvivesEveryRestore()
    {
        var rig = Provisioned();
        var kept = Path.Combine(RigFixture.InstancesRoot, "c1", "rocketstation_Data", "Managed", "MyExperiment.dll");
        rig.Fs.AddFile(kept, "experiment");

        rig.Executor.Run(null, new ResetOptions());
        rig.Executor.Run(null, new ResetOptions());

        Assert.True(rig.Fs.FileExists(kept));
        Assert.Equal("experiment", rig.Fs.ReadAllText(kept));
    }

    [Fact]
    public void TheConfigCopyWipesTheRedirectAndTheResetPutsItBack()
    {
        // The measured hazard, produced rather than asserted from memory: run the copy
        // action alone and the redirect is gone; run the whole reset and it is back.
        var rig = Provisioned();
        var bep = Path.Combine(RigFixture.InstancesRoot, "c1", "BepInEx");
        rig.Fs.AddFile(Path.Combine(RigFixture.SourceInstall, "BepInEx", "config", SavePathOverride.ConfigLeaf),
            "SomeSetting = 1");

        var copyOnly = new ResetAction("client", "c1", ResetActionKind.CopyConfigTree,
            Path.Combine(bep, "config"), "copy", "why",
            Source: Path.Combine(RigFixture.SourceInstall, "BepInEx", "config"));
        rig.Executor.Perform(copyOnly);
        Assert.Null(SavePathOverride.Read(rig.Fs, bep));

        rig.Executor.Run(null, new ResetOptions());
        Assert.Equal(rig.Paths.InstanceUserData("c1"), SavePathOverride.Read(rig.Fs, bep));
    }

    [Fact]
    public void AFailedSavePathReApplyIsRelabelledSoTheSummaryCannotClaimIt()
    {
        // RESET-183. ReapplySavePathOverride is the one action standing between an instance
        // and the developer's tier-1 save folder. On a client a failure is non-fatal by
        // design (failing would make the lock unobtainable and the rig unrepairable), but the
        // outcome summary still printed "SavePathOverride re-applied", so the reset claimed
        // the write it had just warned it could not do.
        var rig = Provisioned();
        var bep = Path.Combine(RigFixture.InstancesRoot, "c1", "BepInEx");
        rig.Fs.DeleteFile(SavePathOverride.ConfigPath(bep));

        var action = new ResetAction(
            "client", "c1", ResetActionKind.ReapplySavePathOverride, bep,
            Label: "SavePathOverride re-applied", Reason: "why",
            Target: rig.Paths.InstanceUserData("c1"), Role: "client");

        var performed = rig.Executor.Perform(action);

        Assert.Equal(ResetExecutor.FailedSavePathOverrideLabel, performed.Label);
        Assert.Contains("NOT re-applied", performed.Label, StringComparison.Ordinal);
        Assert.True(rig.Output.Warned("no separate save root"));

        // And a re-apply that worked keeps its own label, so the relabel cannot hide a success.
        rig.Fs.AddFile(SavePathOverride.ConfigPath(bep), "SavePathOverride = ");
        Assert.Equal("SavePathOverride re-applied", rig.Executor.Perform(action).Label);
    }

    [Fact]
    public void ASettingThatVanishedBetweenThePlanAndTheExecuteIsReportedRatherThanNoOped()
    {
        // RESET-184. The planner only plans this when GetSetting returned a non-empty value,
        // so reaching here with nothing to blank means the file changed underneath the reset,
        // and discarding the answer leaves whatever that setting arms still armed.
        var rig = Provisioned();
        var config = Path.Combine(RigFixture.InstancesRoot, "c1", "BepInEx", "config", "net.scenario.cfg");
        rig.Fs.AddFile(config, "# no such setting here\nOther = 1\n");

        var action = new ResetAction(
            "client", "c1", ResetActionKind.BlankSetting, config,
            Label: "scenario disarmed", Reason: "why", Setting: "Scenario");

        var ex = Assert.Throws<RigRefusalException>(() => rig.Executor.Perform(action));

        Assert.Contains("setting 'Scenario' not found", ex.Message, StringComparison.Ordinal);
        Assert.Contains(config, ex.Message, StringComparison.Ordinal);

        // With the setting present it blanks it and says nothing.
        rig.Fs.AddFile(config, "Scenario = something\n");
        rig.Executor.Perform(action);
        Assert.Equal(string.Empty, ConfigFile.GetSetting(rig.Fs, config, "Scenario"));
    }

    [Fact]
    public void CopyConfigTreeRemovesOnlyOrphanCfgFiles()
    {
        var rig = Provisioned();
        var cfgDir = Path.Combine(RigFixture.InstancesRoot, "c1", "BepInEx", "config");
        var srcDir = Path.Combine(RigFixture.SourceInstall, "BepInEx", "config");
        rig.Fs.AddFile(Path.Combine(srcDir, "net.known.cfg"), "known");
        rig.Fs.AddFile(Path.Combine(cfgDir, "net.invented.cfg"), "invented by a test");
        rig.Fs.AddFile(Path.Combine(cfgDir, "notes.txt"), "not a config");

        rig.Executor.Run(null, new ResetOptions());

        Assert.True(rig.Fs.FileExists(Path.Combine(cfgDir, "net.known.cfg")));
        Assert.False(rig.Fs.FileExists(Path.Combine(cfgDir, "net.invented.cfg")));
        Assert.True(rig.Fs.FileExists(Path.Combine(cfgDir, "notes.txt")));
    }

    [Fact]
    public void AFailedRedirectAfterAConfigCopyIsFatalEvenOnAClient()
    {
        var rig = Provisioned();
        var bep = Path.Combine(RigFixture.InstancesRoot, "c1", "BepInEx");
        rig.Fs.DeleteFile(Path.Combine(bep, "config", SavePathOverride.ConfigLeaf));
        rig.Fs.DeleteFile(Path.Combine(RigFixture.SourceInstall, "BepInEx", "config", SavePathOverride.ConfigLeaf));

        var ex = Assert.Throws<RigRefusalException>(() => rig.Executor.Run(null, new ResetOptions()));

        Assert.Contains("HALF RESET", ex.Message);
        Assert.Contains("SavePathOverride re-applied failed", ex.Message);
        Assert.True(rig.Output.Warned("do not start this instance until the redirect is in place"));
    }

    [Fact]
    public void AMissingRedirectConfigThrowsForAHost()
    {
        // A host CREATES a world, and a host with no redirect creates it inside the
        // developer's saves.
        var rig = new RigFixture();

        var ex = Assert.Throws<RigRefusalException>(() =>
            SavePathOverride.Write(rig.Fs, rig.Output, @"C:\nowhere\BepInEx", @"C:\userdata", "host", "h1"));

        Assert.Contains("Refusing to leave a host without the redirect", ex.Message);
    }

    [Fact]
    public void AMissingRedirectConfigWarnsAndReturnsFalseForAClient()
    {
        var rig = new RigFixture();

        var written = SavePathOverride.Write(rig.Fs, rig.Output, @"C:\nowhere\BepInEx", @"C:\userdata", "client", "c1");

        Assert.False(written);
        Assert.True(rig.Output.Warned("Treat this as a stop, not a note"));
    }

    [Fact]
    public void AnUnknownRoleIsTreatedAsAHost()
    {
        // The expensive mistake is assuming a host is a client.
        var rig = new RigFixture();

        Assert.Throws<RigRefusalException>(() =>
            SavePathOverride.Write(rig.Fs, rig.Output, @"C:\nowhere\BepInEx", @"C:\userdata", "unknown", "x1"));
    }

    [Fact]
    public void TheRedirectIsRewrittenInPlaceRatherThanAppendedTwice()
    {
        var rig = Provisioned();
        var bep = Path.Combine(RigFixture.InstancesRoot, "c1", "BepInEx");

        SavePathOverride.Write(rig.Fs, rig.Output, bep, @"D:\new-root", "client", "c1");
        SavePathOverride.Write(rig.Fs, rig.Output, bep, @"D:\newer-root", "client", "c1");

        var lines = rig.Fs.ReadLines(SavePathOverride.ConfigPath(bep));
        Assert.Single(lines, l => l.StartsWith("SavePathOverride", StringComparison.Ordinal));
        Assert.Equal(@"D:\newer-root", SavePathOverride.Read(rig.Fs, bep));
    }

    [Fact]
    public void TheRedirectIsAppendedWhenTheConfigDoesNotCarryItYet()
    {
        var rig = new RigFixture();
        var bep = @"C:\tree\BepInEx";
        rig.Fs.AddFile(SavePathOverride.ConfigPath(bep), "[Section]\r\nOther = 1\r\n");

        Assert.True(SavePathOverride.Write(rig.Fs, rig.Output, bep, @"D:\root", "client", "c1"));
        Assert.Equal(@"D:\root", SavePathOverride.Read(rig.Fs, bep));
        Assert.Contains("Other = 1", rig.Fs.ReadAllText(SavePathOverride.ConfigPath(bep)));
    }

    // ---- blanking ----------------------------------------------------------

    [Fact]
    public void BlankingASettingLeavesEveryCommentAndOtherValueIntact()
    {
        var rig = new RigFixture();
        var cfg = Path.Combine(rig.Paths.DediInstall, "BepInEx", "config", "net.scenariorunner.cfg");
        rig.Fs.AddFile(cfg,
            "## Settings file\r\n[General]\r\n\r\n## Which scenario to run\r\n# Setting type: String\r\nScenario = probe-1\r\nOther = keep me\r\n");

        rig.Executor.Run(null, new ResetOptions());

        var text = rig.Fs.ReadAllText(cfg);
        Assert.Contains("## Settings file", text);
        Assert.Contains("[General]", text);
        Assert.Contains("## Which scenario to run", text);
        Assert.Contains("Other = keep me", text);
        Assert.Equal("", ConfigFile.GetSetting(rig.Fs, cfg, "Scenario"));
        Assert.Equal("keep me", ConfigFile.GetSetting(rig.Fs, cfg, "Other"));
    }

    [Fact]
    public void OnlyTheFirstNonCommentMatchIsBlanked()
    {
        var rig = new RigFixture();
        var cfg = @"C:\cfg\x.cfg";
        rig.Fs.AddFile(cfg, "# Scenario = commented\r\nScenario = one\r\nScenario = two\r\n");

        Assert.True(ConfigFile.BlankSetting(rig.Fs, cfg, "Scenario"));

        var lines = rig.Fs.ReadLines(cfg);
        Assert.Equal("# Scenario = commented", lines[0]);
        Assert.Equal("Scenario = ", lines[1]);
        Assert.Equal("Scenario = two", lines[2]);
    }

    [Fact]
    public void BlankingASettingThatIsNotThereReportsFalseAndChangesNothing()
    {
        var rig = new RigFixture();
        var cfg = @"C:\cfg\x.cfg";
        rig.Fs.AddFile(cfg, "Other = 1\r\n");
        var before = rig.Fs.ReadAllText(cfg);

        Assert.False(ConfigFile.BlankSetting(rig.Fs, cfg, "Scenario"));
        Assert.Equal(before, rig.Fs.ReadAllText(cfg));
        Assert.False(ConfigFile.BlankSetting(rig.Fs, @"C:\cfg\missing.cfg", "Scenario"));
    }

    [Fact]
    public void ACommentedSettingIsNotReadAsAValue()
    {
        var rig = new RigFixture();
        var cfg = @"C:\cfg\x.cfg";
        rig.Fs.AddFile(cfg, "# Scenario = commented\r\n");

        // Null, not empty: a commented line carries no value at all, where a blanked one
        // carries an empty one. The planner distinguishes them, because only the second
        // means "already blanked, nothing to do".
        Assert.Null(ConfigFile.GetSetting(rig.Fs, cfg, "Scenario"));
    }

    // ---- dry run -----------------------------------------------------------

    [Fact]
    public void ADryRunMovesNotOneByte()
    {
        var rig = Provisioned();
        rig.Fs.AddFile(Path.Combine(rig.Paths.InstanceDataDir("c1"), "imgui.ini"), "layout");
        rig.AddServerWorld("Luna");
        rig.Marker.Write("abc12345", "p", "Start");
        rig.AddServerWorld("MadeByTheTest");
        var before = rig.Fs.Fingerprint();

        var run = rig.Executor.Run(null, new ResetOptions { WhatIf = true });

        Assert.True(run.WhatIf);
        Assert.Empty(run.Performed);
        Assert.Equal(before, rig.Fs.Fingerprint());
    }

    [Fact]
    public void ADryRunOnABusyRigSaysTheRealResetWouldBeRefused()
    {
        // The PowerShell dry run returned before the busy gate, even though the gate had
        // already been computed, so it printed a full plan and never said so.
        var rig = Provisioned();
        rig.StartServer(players: 1);

        var run = rig.Executor.Run(null, new ResetOptions { WhatIf = true });

        Assert.True(run.WhatIf);
        Assert.True(rig.Output.Warned("the real reset would be REFUSED because the rig is in use"));
        Assert.True(rig.Output.Warned("1 player(s) connected"));

        // And it comes back as data. A dry run is never itself Refused, so a caller branching
        // on that alone learned nothing from the one answer a dry run exists to give.
        Assert.False(run.Refused);
        Assert.True(run.WouldRefuse);
        Assert.Contains("player(s) connected", run.WouldRefuseReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ADryRunOnACleanRigWouldNotBeRefused()
    {
        var rig = Provisioned();

        var run = rig.Executor.Run(null, new ResetOptions { WhatIf = true });

        Assert.False(run.WouldRefuse);
        Assert.Equal(string.Empty, run.WouldRefuseReason);
    }

    [Fact]
    public void ADryRunWithKeepStateSaysTheRestoreWouldBeSkippedEntirely()
    {
        var rig = Provisioned();

        rig.Executor.Run(null, new ResetOptions { WhatIf = true, KeepState = true });

        Assert.True(rig.Output.Warned("--keep-state would SKIP the restore entirely"));
    }

    [Fact]
    public void ADryRunOnACleanRigSaysThereIsNothingToDo()
    {
        var rig = new RigFixture();

        rig.Executor.Run(null, new ResetOptions { WhatIf = true });

        Assert.True(rig.Output.Said("nothing (the rig is already clean)"));
    }

    // ---- refusal -----------------------------------------------------------

    [Fact]
    public void ABusyRigRefusesAndDeletesNothingOnEitherHalf()
    {
        var rig = Provisioned();
        rig.Fs.AddFile(Path.Combine(rig.Paths.InstanceDataDir("c1"), "imgui.ini"), "layout");
        rig.Fs.AddFile(rig.Paths.ServerSettingXml, "<x/>");
        rig.StartServer(players: 1);
        var before = rig.Fs.Fingerprint();

        var run = rig.Executor.Run(null, new ResetOptions());

        Assert.True(run.Refused);
        Assert.Contains("player(s) connected", run.RefusalReason);
        Assert.True(rig.Output.Warned("State reset SKIPPED: the rig is in use"));
        Assert.True(rig.Output.Warned("Nothing was deleted"));
        Assert.True(rig.Fs.FileExists(Path.Combine(rig.Paths.InstanceDataDir("c1"), "imgui.ini")));
        Assert.True(rig.Fs.FileExists(rig.Paths.ServerSettingXml));
        Assert.NotEqual(before, rig.Fs.Fingerprint());
    }

    [Fact]
    public void ARefusedResetStillWritesTheSharedStateSnapshotWithoutMovingTheResetStamp()
    {
        // Without it this session's unlock would diff against a previous session's snapshot
        // and report that session's changes as its own.
        var rig = Provisioned();
        rig.State.Save("2026-08-01T00:00:00Z");
        rig.StartServer(players: 1);

        rig.Executor.Run(null, new ResetOptions());

        Assert.True(rig.Fs.FileExists(rig.Paths.SessionStateFile));
        Assert.Equal("2026-08-01T00:00:00Z", rig.State.ReadLastResetUtc());
    }

    [Fact]
    public void ARefusedResetLeavesTheMarkerSet()
    {
        var rig = Provisioned();
        rig.Marker.Write("abc12345", "p", "Start");
        rig.StartServer(players: 1);

        rig.Executor.Run(null, new ResetOptions());

        Assert.True(rig.MarkerExists());
    }

    // ---- keep-state --------------------------------------------------------

    [Fact]
    public void KeepStateSkipsEverythingAndSaysSoInThreeSpecificPhrases()
    {
        var rig = Provisioned();
        rig.Fs.AddFile(Path.Combine(rig.Paths.InstanceDataDir("c1"), "imgui.ini"), "layout");
        rig.Marker.Write("abc12345", "p", "Start");
        var before = rig.Fs.Fingerprint();

        var run = rig.Executor.Run(null, new ResetOptions { KeepState = true });

        Assert.True(run.Skipped);
        Assert.False(run.Refused);
        Assert.True(rig.Output.Warned("SKIPPED on purpose"));
        Assert.True(rig.Output.Warned("dedicated-server worlds included"));
        Assert.True(rig.Output.Warned("the dirty marker stays set so the next session cleans up"));
        Assert.True(rig.MarkerExists());
        Assert.True(rig.Fs.FileExists(Path.Combine(rig.Paths.InstanceDataDir("c1"), "imgui.ini")));
        Assert.NotEqual(before, rig.Fs.Fingerprint());
    }

    [Fact]
    public void KeepStatePrintsTheWholeSkippedPlan()
    {
        var rig = Provisioned();
        rig.Fs.AddFile(Path.Combine(rig.Paths.InstanceDataDir("c1"), "imgui.ini"), "layout");

        rig.Executor.Run(null, new ResetOptions { KeepState = true });

        Assert.True(rig.Output.Said("would have reset"));
        Assert.True(rig.Output.Said("imgui.ini"));
    }

    [Fact]
    public void KeepStateStillWritesTheSharedStateSnapshotAndKeepsThePreviousResetStamp()
    {
        var rig = Provisioned();
        rig.State.Save("2026-08-01T00:00:00Z");

        rig.Executor.Run(null, new ResetOptions { KeepState = true });

        Assert.Equal("2026-08-01T00:00:00Z", rig.State.ReadLastResetUtc());
    }

    [Fact]
    public void OnABusyRigTheRefusalMessageAppearsRatherThanTheKeepStateOne()
    {
        // Both are no-ops, but which message appears is a real behavioural detail.
        var rig = Provisioned();
        rig.StartServer(players: 1);

        var run = rig.Executor.Run(null, new ResetOptions { KeepState = true });

        Assert.True(run.Refused);
        Assert.False(run.Skipped);
        Assert.True(rig.Output.Warned("State reset SKIPPED: the rig is in use"));
        Assert.False(rig.Output.Warned("SKIPPED on purpose"));
    }

    // ---- marker clearing ---------------------------------------------------

    [Fact]
    public void ACompletedRestoreClearsTheMarker()
    {
        var rig = Provisioned();
        rig.Marker.Write("abc12345", "p", "Start");

        var run = rig.Executor.Run(null, new ResetOptions());

        Assert.Empty(run.Failures);
        Assert.False(rig.MarkerExists());
    }

    [Fact]
    public void ARestoreThatFailedOnAnActionLeavesTheMarkerAndThrows()
    {
        var rig = Provisioned();
        var imgui = Path.Combine(rig.Paths.InstanceDataDir("c1"), "imgui.ini");
        rig.Fs.AddFile(imgui, "layout");
        rig.Fs.DeleteFailures[Path.GetFullPath(imgui)] = "held open";
        rig.Marker.Write("abc12345", "p", "Start");

        var ex = Assert.Throws<RigRefusalException>(() => rig.Executor.Run(null, new ResetOptions()));

        Assert.Contains("HALF RESET", ex.Message);
        Assert.Contains("imgui.ini failed", ex.Message);
        Assert.True(rig.MarkerExists());
    }

    [Fact]
    public void ExecutionContinuesPastAFailingActionAndOnlyThrowsAtTheEnd()
    {
        var rig = Provisioned();
        var data = rig.Paths.InstanceDataDir("c1");
        var imgui = Path.Combine(data, "imgui.ini");
        var settings = Path.Combine(data, "setting.xml");
        rig.Fs.AddFile(settings, "<x/>");
        rig.Fs.AddFile(imgui, "layout");
        rig.Fs.DeleteFailures[Path.GetFullPath(settings)] = "held open";

        Assert.Throws<RigRefusalException>(() => rig.Executor.Run(null, new ResetOptions()));

        Assert.True(rig.Fs.FileExists(settings));
        Assert.False(rig.Fs.FileExists(imgui));
    }

    [Fact]
    public void TheResetStampMovesForwardOnlyOnAPerformingRun()
    {
        var rig = Provisioned();
        rig.State.Save("2026-08-01T00:00:00Z");

        rig.Executor.Run(null, new ResetOptions());

        Assert.Equal(RigTime.Stamp(rig.Clock.UtcNow), rig.State.ReadLastResetUtc());
    }

    [Fact]
    public void ASecondRunOnAnAlreadyCleanRigIsIdempotentAndPlansNothingNew()
    {
        var rig = Provisioned();
        rig.Fs.AddFile(Path.Combine(rig.Paths.InstanceDataDir("c1"), "imgui.ini"), "layout");

        rig.Executor.Run(null, new ResetOptions());
        var after = rig.Fs.Fingerprint();
        rig.Executor.Run(null, new ResetOptions());

        Assert.Equal(after, rig.Fs.Fingerprint());
    }

    [Fact]
    public void TheOutcomeAlwaysNamesTheCleanStateItRestoredTo()
    {
        var rig = new RigFixture();

        rig.Executor.Run(null, new ResetOptions());
        Assert.True(rig.Output.Said("clean state: no baseline (built-in delete list only)"));

        rig.Output.Clear();
        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");
        rig.Output.Clear();
        rig.Executor.Run(null, new ResetOptions());
        Assert.True(rig.Output.Said("clean state: the captured baseline"));
    }

    [Fact]
    public void AnEmptyRunSaysThereWasNothingToClear()
    {
        var rig = new RigFixture();

        rig.Executor.Run(null, new ResetOptions());

        Assert.True(rig.Output.Said("nothing to clear"));
        Assert.True(rig.Output.Said("resets BETWEEN sessions only"));
    }

    // ---- the two lock call sites -------------------------------------------

    [Fact]
    public async Task ANewLockResetsAndReleasingResetsAgain()
    {
        var rig = new RigFixture();
        rig.AddServerWorld("Luna");
        var owner = (await rig.Lock.AcquireAsync(rig.Acquire())).Owner;
        rig.Lock.AssertHeld("Start", owner);
        rig.AddServerWorld("MadeByTheTest");

        rig.Lock.Release(owner);

        Assert.False(rig.Fs.DirectoryExists(Path.Combine(rig.Paths.ServerSaveRoot, "MadeByTheTest")));
        Assert.True(rig.Fs.DirectoryExists(Path.Combine(rig.Paths.ServerSaveRoot, "Luna")));
        Assert.False(rig.MarkerExists());
    }

    [Fact]
    public async Task AcquisitionOnACleanRigIsAFreeNoOp()
    {
        var rig = new RigFixture();
        rig.AddServerWorld("Luna");
        var before = rig.Fs.Fingerprint();

        await rig.Lock.AcquireAsync(rig.Acquire());

        Assert.True(rig.Fs.DirectoryExists(Path.Combine(rig.Paths.ServerSaveRoot, "Luna")));
        Assert.NotEqual(before, rig.Fs.Fingerprint());
        Assert.False(rig.MarkerExists());
    }

    [Fact]
    public async Task TheKeepStateDebtIsPaidByTheNextPlainLock()
    {
        var rig = new RigFixture();
        rig.AddServerWorld("Luna");

        var first = (await rig.Lock.AcquireAsync(rig.Acquire())).Owner;
        rig.Lock.AssertHeld("Start", first);
        rig.AddServerWorld("KeptOnPurpose");
        rig.Lock.Release(first, keepState: true);

        Assert.True(rig.Fs.DirectoryExists(Path.Combine(rig.Paths.ServerSaveRoot, "KeptOnPurpose")));
        Assert.True(rig.MarkerExists());

        var second = (await rig.Lock.AcquireAsync(new AcquireOptions { Purpose = "handoff", KeepState = true })).Owner;
        Assert.True(rig.Fs.DirectoryExists(Path.Combine(rig.Paths.ServerSaveRoot, "KeptOnPurpose")));
        rig.Lock.Release(second, keepState: true);

        var third = (await rig.Lock.AcquireAsync(rig.Acquire())).Owner;
        Assert.NotNull(third);
        Assert.False(rig.Fs.DirectoryExists(Path.Combine(rig.Paths.ServerSaveRoot, "KeptOnPurpose")));
        Assert.True(rig.Fs.DirectoryExists(Path.Combine(rig.Paths.ServerSaveRoot, "Luna")));
    }

    [Fact]
    public async Task AHandoffSessionThatMutatesAdoptsTheInheritedWorldDeliberately()
    {
        // Decided explicitly in the port and pinned here, because it was invisible,
        // undocumented and untested in PowerShell: the marker rewrite on a different owner
        // re-records the world set, so a world inherited through --keep-state is promoted to
        // predating the new session and is kept.
        var rig = new RigFixture();
        rig.AddServerWorld("Luna");

        var first = (await rig.Lock.AcquireAsync(rig.Acquire())).Owner;
        rig.Lock.AssertHeld("Start", first);
        rig.AddServerWorld("InheritedWorld");
        rig.Lock.Release(first, keepState: true);

        var second = (await rig.Lock.AcquireAsync(new AcquireOptions { Purpose = "handoff", KeepState = true })).Owner;
        rig.Lock.AssertHeld("Start", second);

        var snapshot = rig.Marker.ReadSessionWorlds(WorldScope.Server);
        Assert.Equal(second, FieldText.Parse(rig.MarkerText()).Get(DirtyMarker.KeyOwner));
        Assert.True(snapshot.Protects("server/saves/InheritedWorld"));

        rig.Lock.Release(second);
        Assert.True(rig.Fs.DirectoryExists(Path.Combine(rig.Paths.ServerSaveRoot, "InheritedWorld")));
    }
}
