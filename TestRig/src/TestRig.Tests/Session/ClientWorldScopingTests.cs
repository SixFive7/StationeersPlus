using TestRig.Core.Session;
using TestRig.Tests.Session.Fakes;
using Xunit;

namespace TestRig.Tests.Session;

/// <summary>
/// Session scoping for the client half's worlds. New behaviour in the port; there is no
/// PowerShell section to port from, because there was no scoping at all.
/// </summary>
/// <remarks>
/// <c>ClientRig/data/&lt;instance&gt;/userdata/saves</c> was emptied by a recursive delete
/// on every reset, unconditionally, with no marker, no baseline and no keep-list, though a
/// listen host writes real worlds there. The repository calls both save roots tier 3 and
/// says "a world's lifetime is session-scoped" without saying that only the
/// dedicated-server half was scoped, so anyone who staged a save into a client instance's
/// save root lost it at the next lock or unlock. It was named the highest-plausibility
/// real-world loss path in the subsystem.
/// </remarks>
public sealed class ClientWorldScopingTests
{
    [Fact]
    public void AClientWorldThatPredatesTheSessionIsKept()
    {
        var rig = new RigFixture();
        rig.AddInstance("h1", role: "host");
        rig.AddClientWorld("h1", "StagedByHand");
        rig.Marker.Write("abc12345", "p", "Start");

        var plan = rig.Planner.Build();

        Assert.DoesNotContain(plan.WorldDeletes, a => a.Half == "client");
        Assert.Contains(plan.Reports, r => r.Kind == ResetReportKind.ClientSavesRetained);
    }

    [Fact]
    public void AClientWorldCreatedDuringTheSessionIsDeleted()
    {
        var rig = new RigFixture();
        rig.AddInstance("h1", role: "host");
        rig.AddClientWorld("h1", "StagedByHand");
        rig.Marker.Write("abc12345", "p", "Start");
        rig.AddClientWorld("h1", "MadeByTheTest");

        var plan = rig.Planner.Build();

        var deletes = plan.WorldDeletes.Where(a => a.Half == "client").ToArray();
        Assert.Single(deletes);
        Assert.Contains("MadeByTheTest", deletes[0].Path);
        Assert.Contains("world 'MadeByTheTest' deleted", deletes[0].Label);
        Assert.Contains("this instance's save root", deletes[0].Reason);
    }

    [Fact]
    public void AStagedClientWorldSurvivesAWholeLockCycle()
    {
        var rig = new RigFixture();
        rig.AddInstance("h1", role: "host");
        rig.AddClientWorld("h1", "StagedByHand");

        var owner = rig.Lease();
        rig.Lock.AssertHeld("Start", owner);
        rig.AddClientWorld("h1", "MadeByTheTest");
        rig.Lock.Release(owner);

        Assert.True(rig.Fs.DirectoryExists(Path.Combine(rig.Paths.InstanceSaveRoot("h1"), "StagedByHand")));
        Assert.False(rig.Fs.DirectoryExists(Path.Combine(rig.Paths.InstanceSaveRoot("h1"), "MadeByTheTest")));
    }

    [Fact]
    public void WithNoMarkerAtAllNoClientWorldIsDeleted()
    {
        var rig = new RigFixture();
        rig.AddInstance("h1", role: "host");
        rig.AddClientWorld("h1", "Whatever");

        var plan = rig.Planner.Build();

        Assert.DoesNotContain(plan.WorldDeletes, a => a.Half == "client");
        var report = plan.Reports.Single(r => r.Kind == ResetReportKind.ClientWorldsNotTracked);
        Assert.False(report.Warn);
        Assert.Contains("there is no session marker", report.Detail);
    }

    [Fact]
    public void ADegradedClientSnapshotKeepsEveryClientWorldAndWarns()
    {
        var rig = new RigFixture();
        rig.AddInstance("h1", role: "host");
        rig.AddClientWorld("h1", "Whatever");
        rig.Fs.AddFile(rig.Paths.DirtyFile, "# unreadable as a marker\n");

        var plan = rig.Planner.Build();

        Assert.DoesNotContain(plan.WorldDeletes, a => a.Half == "client");
        var report = plan.Reports.Single(r => r.Kind == ResetReportKind.ClientWorldsNotTracked);
        Assert.True(report.Warn);
    }

    [Fact]
    public void AFailedClientScanKeepsEveryWorldInThatInstance()
    {
        var rig = new RigFixture();
        rig.AddInstance("h1", role: "host");
        rig.AddClientWorld("h1", "A");
        rig.Marker.Write("abc12345", "p", "Start");
        rig.AddClientWorld("h1", "B");
        rig.Fs.EnumerationFailures[Path.GetFullPath(rig.Paths.InstanceSaveRoot("h1"))] = "denied";

        var plan = rig.Planner.Build();

        Assert.DoesNotContain(plan.WorldDeletes, a => a.Half == "client");
        Assert.Contains(plan.Reports, r => r.Kind == ResetReportKind.ClientWorldsNotTracked && r.Warn);
    }

    [Fact]
    public void EachInstanceIsScopedSeparately()
    {
        var rig = new RigFixture();
        rig.AddInstance("h1", role: "host");
        rig.AddInstance("c2");
        rig.AddClientWorld("h1", "KeptOnH1");
        rig.Marker.Write("abc12345", "p", "Start");
        rig.AddClientWorld("h1", "NewOnH1");
        rig.AddClientWorld("c2", "NewOnC2");

        var deletes = rig.Planner.Build().WorldDeletes.Where(a => a.Half == "client").ToArray();

        Assert.Equal(2, deletes.Length);
        Assert.Contains(deletes, a => a.Path.Contains("NewOnH1"));
        Assert.Contains(deletes, a => a.Path.Contains("NewOnC2"));
        Assert.DoesNotContain(deletes, a => a.Path.Contains("KeptOnH1"));
    }

    [Fact]
    public void LooseFilesInASaveRootAreStillClearedBecauseTheyAreNotWorlds()
    {
        var rig = new RigFixture();
        rig.AddInstance("c1");
        rig.Fs.AddFile(Path.Combine(rig.Paths.InstanceSaveRoot("c1"), "stray.dat"), "leftover");
        rig.AddClientWorld("c1", "AWorld");
        rig.Marker.Write("abc12345", "p", "Start");

        rig.Executor.Run(null, new ResetOptions());

        Assert.False(rig.Fs.FileExists(Path.Combine(rig.Paths.InstanceSaveRoot("c1"), "stray.dat")));
        Assert.True(rig.Fs.DirectoryExists(Path.Combine(rig.Paths.InstanceSaveRoot("c1"), "AWorld")));
    }

    [Fact]
    public void AnInstanceWithNoSaveRootPlansNothingForIt()
    {
        var rig = new RigFixture();
        rig.AddInstance("c1");

        var plan = rig.Planner.Build();

        Assert.DoesNotContain(plan.Actions, a => a.Path.Contains("userdata\\saves", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheClientWorldSetIsRecordedSeparatelyFromTheServerOne()
    {
        var rig = new RigFixture();
        rig.AddServerWorld("Luna");
        rig.AddInstance("h1", role: "host");
        rig.AddClientWorld("h1", "HostedWorld");
        rig.Marker.Write("abc12345", "p", "Start");

        var server = rig.Marker.ReadSessionWorlds(WorldScope.Server);
        var client = rig.Marker.ReadSessionWorlds(WorldScope.Client);

        Assert.True(server.Protects("server/saves/Luna"));
        Assert.False(server.Protects("client/h1/saves/HostedWorld"));
        Assert.True(client.Protects("client/h1/saves/HostedWorld"));
        Assert.False(client.Protects("server/saves/Luna"));
    }

    [Fact]
    public void ACaseOnlyRenameOfAClientWorldDoesNotDeleteIt()
    {
        var rig = new RigFixture();
        rig.AddInstance("h1", role: "host");
        rig.AddClientWorld("h1", "Hosted");
        rig.Marker.Write("abc12345", "p", "Start");
        rig.Fs.DeleteDirectory(Path.Combine(rig.Paths.InstanceSaveRoot("h1"), "Hosted"), recursive: true);
        rig.AddClientWorld("h1", "hosted");

        Assert.DoesNotContain(rig.Planner.Build().WorldDeletes, a => a.Half == "client");
    }

    [Fact]
    public void TheKeptCountForAnInstanceIsReportedWithItsSize()
    {
        var rig = new RigFixture();
        rig.AddInstance("h1", role: "host");
        rig.AddClientWorld("h1", "A");
        rig.AddClientWorld("h1", "B");
        rig.Marker.Write("abc12345", "p", "Start");

        var report = rig.Planner.Build().Reports.Single(r => r.Kind == ResetReportKind.ClientSavesRetained);

        Assert.Equal("h1", report.Instance);
        Assert.Contains("instance saves kept: 2 world(s)", report.Detail);
        Assert.Contains("2 world(s) recorded", report.Detail);
    }
}
