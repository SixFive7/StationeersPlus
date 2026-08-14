using TestRig.Core.Session;
using TestRig.Tests.Session.Fakes;
using Xunit;

namespace TestRig.Tests.Session;

/// <summary>
/// The bulk world-delete ceiling. New in the port: belt and braces over the tri-state
/// world scan.
/// </summary>
/// <remarks>
/// The tri-state scan closes the enumeration failure that produced an empty recorded set.
/// This closes the class of failure: whatever the cause, a plan that wants to delete more
/// than a handful of worlds in one restore is refused, and the refusal names every world it
/// was about to remove. On this machine the defect's plan was 25 worlds and 185 MB, and it
/// looked entirely ordinary.
/// </remarks>
public sealed class BulkDeleteCeilingTests
{
    private static readonly string[] TwentyFive =
    [
        "APC-Luna", "GyroTest", "Luna", "LunaA1", "LunaMultiClient", "LunaSppTest", "Luna_blocker",
        "Luna_burn_demo", "Luna_current", "Luna_debug", "Luna_heal", "Luna_mixedwire",
        "Luna_pgp_burnpersist", "Luna_pgp_overload", "Luna_pgp_power", "Luna_pgp_priority",
        "Luna_pgp_priority_baseline", "Luna_pgp_residue", "Luna_pgp_rocket", "Luna_pgp_test",
        "Luna_pgp_tierburn", "Luna_pt_base", "Luna_rearch", "Luna_revbug", "pgp-task2-luna",
    ];

    private static RigFixture RigWithEmptyRecordedSetAnd(params string[] worlds)
    {
        var rig = new RigFixture();
        rig.Fs.AddDirectory(rig.Paths.ServerSaveRoot);
        rig.Marker.Write("abc12345", "p", "Start");
        foreach (var name in worlds) rig.AddServerWorld(name);
        return rig;
    }

    [Fact]
    public void TheCeilingIsAKnownSmallNumber()
    {
        Assert.Equal(5, ResetPlan.BulkDeleteCeiling);
    }

    [Fact]
    public void APlanAtTheCeilingIsAllowed()
    {
        var rig = RigWithEmptyRecordedSetAnd("w1", "w2", "w3", "w4", "w5");

        var plan = rig.Planner.Build();

        Assert.Equal(5, plan.WorldDeleteCount);
        Assert.False(plan.BulkDeleteCeilingExceeded);
    }

    [Fact]
    public void APlanPastTheCeilingIsFlagged()
    {
        var rig = RigWithEmptyRecordedSetAnd("w1", "w2", "w3", "w4", "w5", "w6");

        var plan = rig.Planner.Build();

        Assert.Equal(6, plan.WorldDeleteCount);
        Assert.True(plan.BulkDeleteCeilingExceeded);
        Assert.Contains(plan.Reports, r => r.Kind == ResetReportKind.BulkWorldDeleteRefused && r.Warn);
    }

    [Fact]
    public void TheRefusalNamesEveryWorldItWasAboutToRemove()
    {
        var rig = RigWithEmptyRecordedSetAnd(TwentyFive);

        var report = rig.Planner.Build().Reports.Single(r => r.Kind == ResetReportKind.BulkWorldDeleteRefused);

        Assert.Contains("REFUSING to delete 25 worlds", report.Detail);
        foreach (var name in TwentyFive) Assert.Contains(name, report.Detail);
        Assert.Contains("--allow-bulk-world-delete", report.Detail);
    }

    [Fact]
    public void APlanPastTheCeilingDeletesNothingAtAll()
    {
        var rig = RigWithEmptyRecordedSetAnd(TwentyFive);
        var worldFilesBefore = rig.Fs.EnumerateFiles(rig.Paths.ServerSaveRoot, "*", recurse: true).Count;

        var run = rig.Executor.Run(null, new ResetOptions());

        Assert.True(run.Refused);
        Assert.Empty(run.Performed);
        Assert.Empty(rig.Fs.DeletedTrees);
        foreach (var name in TwentyFive)
        {
            Assert.True(rig.Fs.DirectoryExists(Path.Combine(rig.Paths.ServerSaveRoot, name)));
        }
        Assert.Equal(worldFilesBefore, rig.Fs.EnumerateFiles(rig.Paths.ServerSaveRoot, "*", recurse: true).Count);
    }

    [Fact]
    public void TheRefusalIsWarnedRatherThanSilent()
    {
        var rig = RigWithEmptyRecordedSetAnd(TwentyFive);

        rig.Executor.Run(null, new ResetOptions());

        Assert.True(rig.Output.Warned("REFUSING to delete 25 worlds"));
        Assert.True(rig.Output.Warned("Luna_pgp_priority_baseline"));
    }

    [Fact]
    public void TheOverrideFlagLetsALegitimateBulkDeleteThrough()
    {
        var rig = RigWithEmptyRecordedSetAnd(TwentyFive);

        var run = rig.Executor.Run(null, new ResetOptions { AllowBulkWorldDelete = true });

        Assert.False(run.Refused);
        Assert.Equal(25, run.Performed.Count(static a => a.Kind == ResetActionKind.DeleteTree));
        foreach (var name in TwentyFive)
        {
            Assert.False(rig.Fs.DirectoryExists(Path.Combine(rig.Paths.ServerSaveRoot, name)));
        }
    }

    [Fact]
    public void ARefusedBulkDeleteLeavesTheMarkerSetSoTheDebtIsNotSilentlyDropped()
    {
        var rig = RigWithEmptyRecordedSetAnd(TwentyFive);

        rig.Executor.Run(null, new ResetOptions());

        Assert.True(rig.MarkerExists());
    }

    [Fact]
    public void ARefusedBulkDeleteStillWritesTheSharedStateSnapshotWithoutMovingTheResetStamp()
    {
        var rig = RigWithEmptyRecordedSetAnd(TwentyFive);
        rig.State.Save("2026-08-01T00:00:00Z");

        rig.Executor.Run(null, new ResetOptions());

        Assert.Equal("2026-08-01T00:00:00Z", rig.State.ReadLastResetUtc());
    }

    [Fact]
    public void ADryRunSaysTheRealResetWouldBeRefusedByTheCeiling()
    {
        var rig = RigWithEmptyRecordedSetAnd(TwentyFive);

        rig.Executor.Run(null, new ResetOptions { WhatIf = true });

        Assert.True(rig.Output.Warned("REFUSING to delete 25 worlds"));
    }

    [Fact]
    public void ClientAndServerWorldDeletesCountTowardsTheSameCeiling()
    {
        var rig = new RigFixture();
        rig.Fs.AddDirectory(rig.Paths.ServerSaveRoot);
        rig.AddInstance("h1", role: "host");
        rig.Fs.AddDirectory(rig.Paths.InstanceSaveRoot("h1"));
        rig.Marker.Write("abc12345", "p", "Start");
        for (var i = 0; i < 3; i++) rig.AddServerWorld($"s{i}");
        for (var i = 0; i < 3; i++) rig.AddClientWorld("h1", $"c{i}");

        var plan = rig.Planner.Build();

        Assert.Equal(6, plan.WorldDeleteCount);
        Assert.True(plan.BulkDeleteCeilingExceeded);
    }

    [Fact]
    public void AnOrdinarySessionThatCreatedOneWorldIsUnaffected()
    {
        var rig = new RigFixture();
        rig.AddServerWorld("Luna");
        rig.Marker.Write("abc12345", "p", "Start");
        rig.AddServerWorld("MadeByTheTest");

        var run = rig.Executor.Run(null, new ResetOptions());

        Assert.False(run.Refused);
        Assert.False(rig.Fs.DirectoryExists(Path.Combine(rig.Paths.ServerSaveRoot, "MadeByTheTest")));
        Assert.True(rig.Fs.DirectoryExists(Path.Combine(rig.Paths.ServerSaveRoot, "Luna")));
    }

    [Fact]
    public void TheDefectShapeEndToEndIsCaughtTwiceOverByTheScanAndTheCeiling()
    {
        // The exact failure: a scan that throws, followed by a restore once it works again.
        // The tri-state scan already keeps everything, so the ceiling never even fires; both
        // guards are asserted here so removing either one is visible.
        var rig = new RigFixture();
        foreach (var name in TwentyFive) rig.AddServerWorld(name);
        rig.Fs.EnumerationFailures[Path.GetFullPath(rig.Paths.ServerSaveRoot)] = "access is denied";
        rig.Marker.Write("abc12345", "p", "Start");
        rig.Fs.EnumerationFailures.Clear();

        var plan = rig.Planner.Build();
        Assert.Equal(0, plan.WorldDeleteCount);
        Assert.False(plan.BulkDeleteCeilingExceeded);

        rig.Executor.Run(null, new ResetOptions());
        Assert.Empty(rig.Fs.DeletedTrees);
        foreach (var name in TwentyFive)
        {
            Assert.True(rig.Fs.DirectoryExists(Path.Combine(rig.Paths.ServerSaveRoot, name)));
        }
    }
}
