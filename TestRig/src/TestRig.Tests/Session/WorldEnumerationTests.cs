using TestRig.Core.Session;
using TestRig.Tests.Session.Fakes;
using Xunit;

namespace TestRig.Tests.Session;

/// <summary>
/// The tri-state world scan, and the defect it fixes.
/// </summary>
/// <remarks>
/// The PowerShell enumeration swallowed every failure and returned an empty list; the
/// marker wrote that as <c>worlds=</c>; the snapshot reader tested the KEY's presence, not
/// its value, so it answered recorded-and-not-degraded; and the planner's predicate was
/// then true for every world. On this machine that plan was 25 DeleteTree actions over
/// 185 MB, irreversible, with no warning at all.
///
/// These are the assertions that would have caught it.
/// </remarks>
public sealed class WorldEnumerationTests
{
    [Fact]
    public void AnEmptySaveRootEnumeratesToARealEmptyAnswer()
    {
        // Empty-set-is-real is deliberate and stays: the rig genuinely had no worlds.
        var rig = new RigFixture();
        rig.Fs.AddDirectory(rig.Paths.ServerSaveRoot);

        var scan = rig.Worlds.ScanServer();

        Assert.Equal(WorldScanStatus.Enumerated, scan.Status);
        Assert.True(scan.IsUsable);
        Assert.Empty(scan.Worlds);
        Assert.Null(scan.FailureDetail);
    }

    [Fact]
    public void AMissingSaveRootIsAlsoARealEmptyAnswer()
    {
        // Nothing to delete, and any world that appears there later was created after this
        // moment, so recording an empty set is correct rather than merely safe.
        var rig = new RigFixture();

        var scan = rig.Worlds.ScanServer();

        Assert.Equal(WorldScanStatus.Enumerated, scan.Status);
        Assert.Empty(scan.Worlds);
    }

    [Fact]
    public void WorldsAreEnumeratedWithRigRelativeKeys()
    {
        var rig = new RigFixture();
        rig.AddServerWorld("Luna");
        rig.AddServerWorld("APC-Luna");

        var scan = rig.Worlds.ScanServer();

        Assert.Equal(WorldScanStatus.Enumerated, scan.Status);
        Assert.Equal(2, scan.Worlds.Count);
        Assert.Equal(["APC-Luna", "Luna"], scan.Worlds.Select(static w => w.Name));
        Assert.Equal("server/saves/Luna", scan.Worlds.Single(static w => w.Name == "Luna").Key);
        Assert.Null(scan.Worlds[0].Instance);
    }

    [Fact]
    public void AFailedEnumerationIsNeverAnEmptySet()
    {
        // THE assertion. In PowerShell this exact situation produced an empty list that was
        // indistinguishable from "there really are no worlds".
        var rig = new RigFixture();
        rig.AddServerWorld("Luna");
        rig.Fs.EnumerationFailures[Path.GetFullPath(rig.Paths.ServerSaveRoot)] = "access is denied";

        var scan = rig.Worlds.ScanServer();

        Assert.Equal(WorldScanStatus.Failed, scan.Status);
        Assert.False(scan.IsUsable);
        Assert.Empty(scan.Worlds);
        Assert.NotNull(scan.FailureDetail);
        Assert.Contains("could not be enumerated", scan.FailureDetail);
        Assert.Contains("access is denied", scan.FailureDetail);
    }

    [Fact]
    public void AFailedEnumerationOmitsTheWorldsKeyFromTheMarkerEntirely()
    {
        var rig = new RigFixture();
        rig.AddServerWorld("Luna");
        rig.Fs.EnumerationFailures[Path.GetFullPath(rig.Paths.ServerSaveRoot)] = "access is denied";

        rig.Marker.Write("abc12345", "p", "Start");

        var fields = FieldText.Parse(rig.MarkerText());
        Assert.False(fields.Contains(DirtyMarker.KeyWorlds));
        Assert.NotEqual("", fields.GetOrEmpty(DirtyMarker.KeyOwner));
    }

    [Fact]
    public void AFailedEnumerationLandsInTheDegradedPathThatKeepsEveryWorld()
    {
        var rig = new RigFixture();
        rig.AddServerWorld("Luna");
        rig.Fs.EnumerationFailures[Path.GetFullPath(rig.Paths.ServerSaveRoot)] = "access is denied";
        rig.Marker.Write("abc12345", "p", "Start");

        var snapshot = rig.Marker.ReadSessionWorlds(WorldScope.Server);

        Assert.False(snapshot.Recorded);
        Assert.True(snapshot.Degraded);
        Assert.Contains("records no dedicated-server world set at all", snapshot.Reason);
    }

    [Fact]
    public void AWorldEnumerationThatThrowsKeepsEveryWorld()
    {
        // End to end, which is what the defect actually cost: mark the rig dirty while the
        // enumeration is failing, then plan a restore once it works again.
        var rig = new RigFixture();
        foreach (var name in new[] { "Luna", "LunaA1", "APC-Luna", "GyroTest", "Luna_debug" })
        {
            rig.AddServerWorld(name);
        }
        rig.Fs.EnumerationFailures[Path.GetFullPath(rig.Paths.ServerSaveRoot)] = "transient sharing violation";
        rig.Marker.Write("abc12345", "p", "Start");
        rig.Fs.EnumerationFailures.Clear();

        var plan = rig.Planner.Build();

        Assert.Equal(0, plan.WorldDeleteCount);
        Assert.Empty(plan.WorldDeletes);
        Assert.Contains(plan.Reports, r => r.Kind == ResetReportKind.WorldsNotTracked && r.Warn);
    }

    [Fact]
    public void AnEmptyRecordedSetStillMeansWhatItAlwaysMeant()
    {
        // The other half of the fix: empty-set-is-real must keep working, and be
        // distinguishable from failure.
        var rig = new RigFixture();
        rig.Fs.AddDirectory(rig.Paths.ServerSaveRoot);
        rig.Marker.Write("abc12345", "p", "Start");

        var fields = FieldText.Parse(rig.MarkerText());
        Assert.True(fields.Contains(DirtyMarker.KeyWorlds));
        Assert.Equal("", fields.Get(DirtyMarker.KeyWorlds));

        var snapshot = rig.Marker.ReadSessionWorlds(WorldScope.Server);
        Assert.True(snapshot.Recorded);
        Assert.False(snapshot.Degraded);
        Assert.Equal(0, snapshot.Count);
    }

    [Fact]
    public void AWorldCreatedAfterAnEmptyRecordedSetIsPlannedForDeletion()
    {
        var rig = new RigFixture();
        rig.Fs.AddDirectory(rig.Paths.ServerSaveRoot);
        rig.Marker.Write("abc12345", "p", "Start");
        rig.AddServerWorld("MadeByTheTest");

        var plan = rig.Planner.Build();

        Assert.Equal(1, plan.WorldDeleteCount);
        Assert.Contains("MadeByTheTest", plan.WorldDeletes.Single().Label);
    }

    [Fact]
    public void AWorldNameThatCannotRoundTripFailsTheWholeScanRatherThanBeingDeleted()
    {
        // A directory named " Luna" is legal on NTFS, and the PowerShell reader trimmed it
        // on the way back, so the key matched nothing and the world was deleted. One exotic
        // name now costs a session its world scoping, loudly, instead of costing somebody a
        // world, silently.
        var rig = new RigFixture();
        rig.AddServerWorld(" Luna");

        var scan = rig.Worlds.ScanServer();

        Assert.Equal(WorldScanStatus.Failed, scan.Status);
        Assert.Contains("cannot be recorded in the session marker", scan.FailureDetail);
    }

    [Fact]
    public void AWorldWithALeadingSpaceSurvivesARestore()
    {
        var rig = new RigFixture();
        rig.AddServerWorld(" Luna");
        rig.AddServerWorld("Luna");
        rig.Marker.Write("abc12345", "p", "Start");

        // The key is omitted entirely rather than recorded as a trimmed name that would
        // then match the wrong directory, or match nothing at all.
        var fields = FieldText.Parse(rig.MarkerText());
        Assert.False(fields.Contains(DirtyMarker.KeyWorlds));

        var plan = rig.Planner.Build();

        Assert.Equal(0, plan.WorldDeleteCount);
        Assert.True(rig.Fs.DirectoryExists(Path.Combine(rig.Paths.ServerSaveRoot, " Luna")));
        Assert.True(rig.Fs.DirectoryExists(Path.Combine(rig.Paths.ServerSaveRoot, "Luna")));
    }

    [Theory]
    [InlineData("Luna")]
    [InlineData("APC-Luna")]
    [InlineData("Luna_pgp_priority_baseline")]
    [InlineData("world with spaces inside")]
    [InlineData("world=with=equals")]
    public void OrdinaryNamesRoundTrip(string name)
    {
        Assert.True(WorldKey.IsRoundTrippable(name));
    }

    [Theory]
    [InlineData(" Luna")]
    [InlineData("Luna ")]
    [InlineData("Lu|na")]
    [InlineData("Lu\nna")]
    [InlineData("#Luna")]
    [InlineData("")]
    public void NamesThatCannotBeRepresentedAreRejected(string name)
    {
        Assert.False(WorldKey.IsRoundTrippable(name));
    }

    [Fact]
    public void AWorldNameContainingAnEqualsSignSurvivesTheRoundTrip()
    {
        // Only the FIRST equals splits a field, so the value keeps the rest.
        var rig = new RigFixture();
        rig.AddServerWorld("world=with=equals");
        rig.Marker.Write("abc12345", "p", "Start");

        var snapshot = rig.Marker.ReadSessionWorlds(WorldScope.Server);

        Assert.True(snapshot.Recorded);
        Assert.True(snapshot.Protects("server/saves/world=with=equals"));
    }

    [Fact]
    public void ClientWorldsAreScannedPerInstanceWithTheirOwnKeys()
    {
        var rig = new RigFixture();
        rig.AddInstance("h1", role: "host");
        rig.AddClientWorld("h1", "HostedWorld");

        var scan = rig.Worlds.ScanClients();

        Assert.Equal(WorldScanStatus.Enumerated, scan.Status);
        Assert.Single(scan.Worlds);
        Assert.Equal("client/h1/saves/HostedWorld", scan.Worlds[0].Key);
        Assert.Equal("h1", scan.Worlds[0].Instance);
    }

    [Fact]
    public void AFailureOnOneInstanceFailsTheWholeClientScan()
    {
        // Partial knowledge is worse than none: the missing instance's worlds would be
        // absent from the recorded set and therefore deleted.
        var rig = new RigFixture();
        rig.AddInstance("c1");
        rig.AddInstance("c2");
        rig.AddClientWorld("c1", "A");
        rig.AddClientWorld("c2", "B");
        rig.Fs.EnumerationFailures[Path.GetFullPath(rig.Paths.InstanceSaveRoot("c2"))] = "denied";

        var scan = rig.Worlds.ScanClients();

        Assert.Equal(WorldScanStatus.Failed, scan.Status);
        Assert.Empty(scan.Worlds);
    }

    [Fact]
    public void NoClientDataDirectoryIsAnEmptyAnswerNotAFailure()
    {
        var rig = new RigFixture();
        rig.Fs.DeleteDirectory(rig.Paths.ClientDataDir, recursive: true);

        var scan = rig.Worlds.ScanClients();

        Assert.Equal(WorldScanStatus.Enumerated, scan.Status);
        Assert.Empty(scan.Worlds);
    }

    [Fact]
    public void TheClientWorldSetIsRecordedInItsOwnMarkerKey()
    {
        var rig = new RigFixture();
        rig.AddInstance("h1", role: "host");
        rig.AddClientWorld("h1", "HostedWorld");

        rig.Marker.Write("abc12345", "p", "Start");

        var fields = FieldText.Parse(rig.MarkerText());
        Assert.True(fields.Contains(DirtyMarker.KeyClientWorlds));
        Assert.Equal("client/h1/saves/HostedWorld", fields.Get(DirtyMarker.KeyClientWorlds));
    }

    [Fact]
    public void AFailedClientScanOmitsOnlyTheClientKey()
    {
        var rig = new RigFixture();
        rig.AddServerWorld("Luna");
        rig.AddInstance("c1");
        rig.Fs.EnumerationFailures[Path.GetFullPath(rig.Paths.InstanceSaveRoot("c1"))] = "denied";
        rig.AddClientWorld("c1", "A");

        rig.Marker.Write("abc12345", "p", "Start");

        var fields = FieldText.Parse(rig.MarkerText());
        Assert.True(fields.Contains(DirtyMarker.KeyWorlds));
        Assert.Equal("server/saves/Luna", fields.Get(DirtyMarker.KeyWorlds));
        Assert.False(fields.Contains(DirtyMarker.KeyClientWorlds));
    }

    [Fact]
    public void TheWorldsValueIsPipeJoinedBecausePipeIsIllegalInADirectoryName()
    {
        var rig = new RigFixture();
        rig.AddServerWorld("Luna");
        rig.AddServerWorld("Mars");

        rig.Marker.Write("abc12345", "p", "Start");

        Assert.Equal("server/saves/Luna|server/saves/Mars",
            FieldText.Parse(rig.MarkerText()).Get(DirtyMarker.KeyWorlds));
    }
}
