using TestRig.Core.Session;
using TestRig.Tests.Session.Fakes;
using Xunit;

namespace TestRig.Tests.Session;

/// <summary>
/// Session-scoped world tracking. Ported from rig-reset.tests.ps1 section sessionworlds
/// (61 assertions) and section C of the specification.
/// </summary>
/// <remarks>
/// The rule, and it is the whole rule: a world is deleted if and only if the session
/// marker recorded a world set and this world is not in it.
/// </remarks>
public sealed class SessionWorldTests
{
    // ---- the five outcomes -------------------------------------------------

    [Fact]
    public void NoMarkerAtAllIsNotRecordedAndNotDegraded()
    {
        var rig = new RigFixture();
        rig.AddServerWorld("Luna");

        var snapshot = rig.Marker.ReadSessionWorlds(WorldScope.Server);

        Assert.False(snapshot.Recorded);
        Assert.False(snapshot.Degraded);
        Assert.Contains("there is no session marker", snapshot.Reason);
        Assert.Equal(0, snapshot.Count);
    }

    [Fact]
    public void AMarkerFilePresentButUnreadableAsAMarkerIsDegraded()
    {
        var rig = new RigFixture();
        rig.Fs.AddFile(rig.Paths.DirtyFile, "# comments only\n");

        var snapshot = rig.Marker.ReadSessionWorlds(WorldScope.Server);

        Assert.False(snapshot.Recorded);
        Assert.True(snapshot.Degraded);
        Assert.Contains("could not be read as a marker", snapshot.Reason);
    }

    [Fact]
    public void AMarkerFromBeforeTheLastBootIsDegraded()
    {
        var rig = new RigFixture();
        rig.AddServerWorld("Luna");
        rig.Marker.Write("abc12345", "p", "Start");
        rig.Boot.Reboot();

        var snapshot = rig.Marker.ReadSessionWorlds(WorldScope.Server);

        Assert.False(snapshot.Recorded);
        Assert.True(snapshot.Degraded);
        Assert.Contains("written before the machine last started", snapshot.Reason);
    }

    [Fact]
    public void AMarkerWithNoWorldsKeyIsDegraded()
    {
        var rig = new RigFixture();
        rig.Marker.Write("abc12345", "p", "Start");
        var fields = FieldText.Parse(rig.MarkerText());
        var stripped = new FieldText();
        foreach (var key in fields.Keys)
        {
            if (key is DirtyMarker.KeyWorlds or DirtyMarker.KeyClientWorlds) continue;
            stripped.Set(key, fields.GetOrEmpty(key));
        }
        rig.Fs.AddFile(rig.Paths.DirtyFile, stripped.Render([]));

        var snapshot = rig.Marker.ReadSessionWorlds(WorldScope.Server);

        Assert.False(snapshot.Recorded);
        Assert.True(snapshot.Degraded);
        Assert.Contains("records no dedicated-server world set at all", snapshot.Reason);
    }

    [Fact]
    public void AMarkerWithAWorldsKeyIsRecordedAndNotDegraded()
    {
        var rig = new RigFixture();
        rig.AddServerWorld("Luna");
        rig.AddServerWorld("Mars");
        rig.Marker.Write("abc12345", "p", "Start");

        var snapshot = rig.Marker.ReadSessionWorlds(WorldScope.Server);

        Assert.True(snapshot.Recorded);
        Assert.False(snapshot.Degraded);
        Assert.Equal("", snapshot.Reason);
        Assert.Equal(2, snapshot.Count);
        Assert.True(snapshot.Protects("server/saves/Luna"));
        Assert.True(snapshot.Protects("server/saves/Mars"));
        Assert.False(snapshot.Protects("server/saves/Venus"));
    }

    [Fact]
    public void TheRecordedSetIsCaseInsensitive()
    {
        // PowerShell hashtables are case-insensitive, so a case-only rename of a world was
        // harmless there. A default HashSet would make it a live delete bug.
        var rig = new RigFixture();
        rig.AddServerWorld("Luna");
        rig.Marker.Write("abc12345", "p", "Start");

        var snapshot = rig.Marker.ReadSessionWorlds(WorldScope.Server);

        Assert.True(snapshot.Protects("server/saves/luna"));
        Assert.True(snapshot.Protects("SERVER/SAVES/LUNA"));
    }

    [Fact]
    public void ACaseOnlyRenameDoesNotDeleteTheWorld()
    {
        var rig = new RigFixture();
        rig.AddServerWorld("Luna");
        rig.Marker.Write("abc12345", "p", "Start");

        // Rename Luna -> luna, which on NTFS is the same directory under a different label.
        rig.Fs.DeleteDirectory(Path.Combine(rig.Paths.ServerSaveRoot, "Luna"), recursive: true);
        rig.AddServerWorld("luna");

        var plan = rig.Planner.Build();

        Assert.Equal(0, plan.WorldDeleteCount);
    }

    // ---- the rule ----------------------------------------------------------

    [Fact]
    public void AWorldOnTheRigWhenTheSessionStartedIsKept()
    {
        var rig = new RigFixture();
        rig.AddServerWorld("Luna");
        rig.Marker.Write("abc12345", "p", "Start");

        var plan = rig.Planner.Build();

        Assert.Equal(0, plan.WorldDeleteCount);
        Assert.Contains(plan.Reports, r => r.Kind == ResetReportKind.SavesRetained);
        Assert.Contains(plan.Reports, r => r.Detail.Contains("data/saves kept: 1 world(s)"));
        Assert.Contains(plan.Reports, r => r.Detail.Contains("already here when this session started (1 world(s) recorded)"));
    }

    [Fact]
    public void AWorldCreatedDuringTheSessionIsPlannedForDeletion()
    {
        var rig = new RigFixture();
        rig.AddServerWorld("Luna");
        rig.Marker.Write("abc12345", "p", "Start");
        rig.AddServerWorld("MadeByTheTest");

        var plan = rig.Planner.Build();

        Assert.Equal(1, plan.WorldDeleteCount);
        var action = plan.WorldDeletes.Single();
        Assert.Equal(ResetActionKind.DeleteTree, action.Kind);
        Assert.Contains("world 'MadeByTheTest' deleted", action.Label);
        Assert.Contains("it was not on the rig when this session first touched it", action.Reason);
        Assert.Contains("its lifetime ends with the lock", action.Reason);
    }

    [Fact]
    public void AMidSessionWorldNeverJoinsTheRecordedSet()
    {
        // The idempotence per (owner, boot) is what makes the set correct.
        var rig = new RigFixture();
        rig.AddServerWorld("Luna");
        rig.Marker.Write("abc12345", "p", "Start");
        rig.AddServerWorld("MadeByTheTest");

        Assert.False(rig.Marker.Write("abc12345", "p", "Save"));

        var snapshot = rig.Marker.ReadSessionWorlds(WorldScope.Server);
        Assert.Equal(1, snapshot.Count);
        Assert.False(snapshot.Protects("server/saves/MadeByTheTest"));
    }

    [Fact]
    public void EveryDegradedCaseKeepsEveryWorldAndSaysWhichCaseItWas()
    {
        var rig = new RigFixture();
        rig.AddServerWorld("Luna");
        rig.AddServerWorld("Mars");
        rig.Fs.AddFile(rig.Paths.DirtyFile, "# nothing readable\n");

        var plan = rig.Planner.Build();

        Assert.Equal(0, plan.WorldDeleteCount);
        var report = plan.Reports.Single(r => r.Kind == ResetReportKind.WorldsNotTracked);
        Assert.True(report.Warn);
        Assert.Contains("no dedicated-server world is deleted by this restore", report.Detail);
        Assert.Contains("could not be read as a marker", report.Detail);
    }

    [Fact]
    public void TheOrdinaryCleanStateIsStatedRatherThanWarned()
    {
        var rig = new RigFixture();
        rig.AddServerWorld("Luna");

        var plan = rig.Planner.Build();

        var report = plan.Reports.Single(r => r.Kind == ResetReportKind.WorldsNotTracked);
        Assert.False(report.Warn);
        Assert.Contains("there is no session marker", report.Detail);
    }

    // ---- the data-loss case this exists for --------------------------------

    [Fact]
    public void AWorldStagedBeforeTheFirstMutatingCommandSurvivesEvenWithAFreshBaseline()
    {
        // THE DATA-LOSS CASE. Staleness inspects the game version, the instance-name set and
        // files of class payload; worlds are class world, so the world set is invisible to
        // it. Staging a world deliberately left the baseline reading FRESH, still not
        // listing that world, and the next session boundary deleted it. The staged save WAS
        // the test.
        var rig = new RigFixture();
        rig.AddServerWorld("Luna");
        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");

        var baseline = rig.Baseline.Read()!;
        Assert.False(rig.Baseline.CheckStale(baseline).Stale);
        Assert.False(baseline.Files.ContainsKey("server/saves/StagedByHand"));

        // Stage a world by hand, exactly as the repository's save rules prescribe, BEFORE
        // the session's first mutating command.
        rig.AddServerWorld("StagedByHand");

        rig.Marker.Write("abc12345", "p", "Start");
        rig.AddServerWorld("MadeByTheTest");

        var plan = rig.Planner.Build();

        Assert.Equal(1, plan.WorldDeleteCount);
        Assert.Contains("MadeByTheTest", plan.WorldDeletes.Single().Path);
        Assert.DoesNotContain(plan.WorldDeletes, a => a.Path.Contains("StagedByHand"));
    }

    [Fact]
    public void AWorldStagedAfterTheFirstMutatingCommandIsDeleted()
    {
        // The documented rule, and a documented data-loss path: stage a save you want to
        // keep BEFORE that first command.
        var rig = new RigFixture();
        rig.AddServerWorld("Luna");
        rig.Marker.Write("abc12345", "p", "Start");
        rig.AddServerWorld("StagedTooLate");

        var plan = rig.Planner.Build();

        Assert.Equal(1, plan.WorldDeleteCount);
        Assert.Contains("StagedTooLate", plan.WorldDeletes.Single().Path);
    }

    [Fact]
    public void ARenameDuringASessionDestroysTheWorldAtTheBoundary()
    {
        // Pinned because it is counter-intuitive: any "let me rename this so it survives"
        // instinct does the opposite.
        var rig = new RigFixture();
        rig.AddServerWorld("Luna");
        rig.Marker.Write("abc12345", "p", "Start");
        rig.Fs.DeleteDirectory(Path.Combine(rig.Paths.ServerSaveRoot, "Luna"), recursive: true);
        rig.AddServerWorld("Luna_keep");

        var plan = rig.Planner.Build();

        Assert.Equal(1, plan.WorldDeleteCount);
        Assert.Contains("Luna_keep", plan.WorldDeletes.Single().Path);
    }

    [Fact]
    public void TakingTheLockDoesNotArmTheMarkerSoAWorldStagedAfterItIsStillProtected()
    {
        var rig = new RigFixture();
        rig.AddServerWorld("Luna");
        var owner = rig.Lease();

        rig.AddServerWorld("StagedAfterTheLock");
        rig.Lock.AssertHeld("Start", owner);

        var snapshot = rig.Marker.ReadSessionWorlds(WorldScope.Server);
        Assert.True(snapshot.Recorded);
        Assert.True(snapshot.Protects("server/saves/StagedAfterTheLock"));
        Assert.Equal(0, rig.Planner.Build().WorldDeleteCount);
    }

    [Fact]
    public void CaptureBaselineIsAGatedVerbSoItArmsTheMarkerAndProtectsEveryWorld()
    {
        var rig = new RigFixture();
        foreach (var name in new[] { "Luna", "Mars", "Venus" }) rig.AddServerWorld(name);
        var owner = rig.Lease();

        rig.Lock.AssertHeld("capture-baseline", owner);
        rig.Baseline.Capture(rig.Planner.CheckGate(), owner);

        var snapshot = rig.Marker.ReadSessionWorlds(WorldScope.Server);
        Assert.Equal(3, snapshot.Count);
        Assert.Equal(0, rig.Planner.Build().WorldDeleteCount);
    }

    [Fact]
    public void CapturingABaselineNeverTouchesAWorldDirectory()
    {
        var rig = new RigFixture();
        rig.AddServerWorld("Luna", fileCount: 3);
        var before = rig.Fs.Fingerprint();

        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");

        Assert.True(rig.Fs.DirectoryExists(Path.Combine(rig.Paths.ServerSaveRoot, "Luna")));
        Assert.Empty(rig.Fs.DeletedTrees);
        Assert.Equal(3, rig.Fs.EnumerateFiles(Path.Combine(rig.Paths.ServerSaveRoot, "Luna"), "*", recurse: true).Count);
        Assert.NotEqual(before, rig.Fs.Fingerprint());
    }

    [Fact]
    public void AWorldIsRecordedInTheBaselineWithoutAHashAndWithItsByteCount()
    {
        var rig = new RigFixture();
        rig.AddServerWorld("Luna", fileCount: 2, bytesEach: 512);

        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");

        var entry = rig.Baseline.Read()!.Files["server/saves/Luna"];
        Assert.Equal(SurfaceClass.World, entry.Class);
        Assert.Equal("", entry.Sha256);
        Assert.Equal(1024, entry.Bytes);
    }

    [Fact]
    public void NeitherAFreshNorAStaleBaselineIsAWorldAuthority()
    {
        var rig = new RigFixture();
        rig.AddServerWorld("Luna");
        rig.Baseline.Capture(rig.Planner.CheckGate(), "abc12345");
        rig.AddServerWorld("NotInTheBaseline");

        // Fresh: the world is absent from the manifest and still survives.
        Assert.False(rig.Baseline.CheckStale(rig.Baseline.Read()).Stale);
        Assert.Equal(0, rig.Planner.Build().WorldDeleteCount);

        // Stale: no more and no less a world authority.
        rig.AddInstance("newInstance");
        Assert.True(rig.Baseline.CheckStale(rig.Baseline.Read()).Stale);
        Assert.Equal(0, rig.Planner.Build().WorldDeleteCount);
    }

    // ---- reporting ---------------------------------------------------------

    [Fact]
    public void EachDeletedWorldIsItsOwnLabelledActionWithItsSize()
    {
        var rig = new RigFixture();
        rig.Fs.AddDirectory(rig.Paths.ServerSaveRoot);
        rig.Marker.Write("abc12345", "p", "Start");
        rig.AddServerWorld("A", fileCount: 1, bytesEach: 2 * 1024 * 1024);
        rig.AddServerWorld("B", fileCount: 1, bytesEach: 1024);

        var plan = rig.Planner.Build();

        Assert.Equal(2, plan.WorldDeleteCount);
        Assert.Contains(plan.WorldDeletes, a => a.Label.Contains("world 'A' deleted (2.0 MB)"));
        Assert.Contains(plan.WorldDeletes, a => a.Label.Contains("world 'B' deleted (0.0 MB)"));
    }

    [Fact]
    public void TheWorldsLineIsAlwaysProducedEvenWhenNothingHappened()
    {
        var rig = new RigFixture();
        rig.AddServerWorld("Luna");
        rig.Marker.Write("abc12345", "p", "Start");

        var plan = rig.Planner.Build();
        rig.Executor.WriteOutcome(plan, [], "test");

        Assert.True(rig.Output.Said("worlds:"));
        Assert.True(rig.Output.Said("none deleted (1 recorded as predating this session)"));
    }

    [Fact]
    public void TheWorldsLineNamesTheDegradedReasonWhenThereIsNoRecordedSet()
    {
        var rig = new RigFixture();
        rig.AddServerWorld("Luna");

        var plan = rig.Planner.Build();
        rig.Executor.WriteOutcome(plan, [], "test");

        Assert.True(rig.Output.Said("worlds:"));
        Assert.True(rig.Output.Said("there is no session marker"));
    }

    [Fact]
    public void TheSnapshotIsReadOncePerPlanAndNeverMutatesAnything()
    {
        var rig = new RigFixture();
        rig.AddServerWorld("Luna");
        rig.Marker.Write("abc12345", "p", "Start");
        var before = rig.Fs.Fingerprint();

        rig.Planner.Build();
        rig.Planner.Build();

        Assert.Equal(before, rig.Fs.Fingerprint());
    }
}
