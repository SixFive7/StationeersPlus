using TestRig.Core.Abstractions;
using TestRig.Core.Session;
using TestRig.Tests.Session.Fakes;
using Xunit;

namespace TestRig.Tests.Session;

/// <summary>
/// Status rendering, which the PowerShell suite never asserted a single character of.
/// </summary>
/// <remarks>
/// Three writers were covered only by "it did not throw", which was named the largest
/// coverage gap in that suite. Rendering returns lines here, so it is assertable.
/// </remarks>
public sealed class StatusRendererTests
{
    private static IReadOnlyList<StatusLine> Render(RigFixture rig, string? callerId) =>
        StatusRenderer.Render(rig.Lock.GetStatus(callerId), callerId, rig.Clock.UtcNow);

    private static string Text(IReadOnlyList<StatusLine> lines) => string.Join("\n", lines.Select(static l => l.Text));

    [Fact]
    public void AFreeRigSaysThereIsNoLock()
    {
        var rig = new RigFixture();

        var lines = Render(rig, null);

        Assert.Equal("rig lock:     none", lines[0].Text);
        Assert.Contains("rig state:  clean (restored; no session has mutated it since)", Text(lines));
    }

    [Fact]
    public void YourOwnLockIsLabelledYours()
    {
        var rig = new RigFixture();
        var owner = rig.Lease("network paint check");

        var lines = Render(rig, owner);

        Assert.Equal("rig lock:     YOURS", lines[0].Text);
        Assert.Contains("  purpose:    network paint check", Text(lines));
    }

    [Fact]
    public void AForeignLockIsLabelledAsAnotherSessionWhenACallerIdIsGiven()
    {
        var rig = new RigFixture();
        rig.WriteLockFile("zzz99999", "their test");

        Assert.Equal("rig lock:     held by another session (zzz99999)", Render(rig, "abc12345")[0].Text);
        Assert.Equal("rig lock:     owner zzz99999", Render(rig, null)[0].Text);
    }

    [Fact]
    public void TheTimerLineNamesFreshnessTtlAndAge()
    {
        var rig = new RigFixture();
        var owner = rig.Lease();
        rig.Clock.AdvanceMinutes(3);

        Assert.Contains("  timer:      fresh; ttl 10 min; refreshed 3 min ago", Text(Render(rig, owner)));
    }

    [Fact]
    public void TheTimerLineSaysExpiredOnceTheHeartbeatLapses()
    {
        var rig = new RigFixture();
        var owner = rig.Lease();
        rig.Clock.AdvanceMinutes(30);

        Assert.Contains("  timer:      expired; ttl 10 min; refreshed 30 min ago", Text(Render(rig, owner)));
    }

    [Fact]
    public void TheIdleLineNamesTheCeilingAndTheCountdown()
    {
        var rig = new RigFixture();
        var owner = rig.Lease();
        rig.Clock.AdvanceMinutes(20);

        Assert.Contains("  idle:       owner last acted 20 min ago; ceiling 60 min (40 min left)", Text(Render(rig, owner)));
    }

    [Fact]
    public void TheIdleLineSaysReachedPastTheCeiling()
    {
        var rig = new RigFixture();
        var owner = rig.Lease();
        rig.Clock.AdvanceMinutes(90);

        Assert.Contains("ceiling 60 min (reached)", Text(Render(rig, owner)));
    }

    [Fact]
    public void ABusyRigNamesWhatIsRunning()
    {
        var rig = new RigFixture();
        rig.AddInstance("c1");
        rig.StartInstance("c1", 5001);
        var owner = rig.Lease();

        Assert.Contains("  rig busy:   1 client instance(s) running: c1=client", Text(Render(rig, owner)));
    }

    [Fact]
    public void ABusyRigWithAnExpiredLockSaysTheLockIsStillLive()
    {
        var rig = new RigFixture();
        rig.AddInstance("c1");
        rig.StartInstance("c1", 5001);
        var owner = rig.Lease();
        rig.Clock.AdvanceMinutes(30);

        Assert.Contains("(lock still LIVE: rig is busy)", Text(Render(rig, owner)));
    }

    [Fact]
    public void ABusyRigPastTheCeilingSaysTheLockIsReclaimableAnyway()
    {
        var rig = new RigFixture();
        rig.AddInstance("c1");
        rig.StartInstance("c1", 5001);
        var owner = rig.Lease();
        rig.Clock.AdvanceMinutes(120);

        Assert.Contains("(lock is RECLAIMABLE anyway: past the idle ceiling)", Text(Render(rig, owner)));
    }

    [Fact]
    public void ALiveHostIsNamedWithTheUnlockRefusalItCauses()
    {
        var rig = new RigFixture();
        rig.AddInstance("h1", role: "host");
        rig.StartInstance("h1", 6001);
        var owner = rig.Lease();

        Assert.Contains("  hosting:    h1  (unlock refuses while a host is live; --force overrides)", Text(Render(rig, owner)));
    }

    [Fact]
    public void AnIdleRigWithAnExpiredLockSaysWhyItIsReclaimable()
    {
        var rig = new RigFixture();
        var owner = rig.Lease();
        rig.Clock.AdvanceMinutes(30);

        Assert.Contains("  rig busy:   no; timer expired, so the lock is reclaimable", Text(Render(rig, owner)));
    }

    [Fact]
    public void AnIdleRigPastTheCeilingSaysSo()
    {
        var rig = new RigFixture();
        var owner = rig.Lease();
        rig.Clock.AdvanceMinutes(120);

        Assert.Contains("  rig busy:   no; past the idle ceiling, so the lock is reclaimable", Text(Render(rig, owner)));
    }

    [Fact]
    public void ADirtyRigNamesTheSessionThatLeftItAndWhatIsLeftOfIt()
    {
        var rig = new RigFixture();
        rig.Marker.Write("old00001", "a previous test", "Start");

        var text = Text(Render(rig, null));

        Assert.Contains("rig state:  DIRTY", text);
        Assert.Contains("by owner old00001 (Start)", text);
        Assert.Contains("its launcher process is gone", text);
        Assert.Contains("nothing is left of that session", text);
    }

    [Fact]
    public void ADirtyRigWithALiveWriterDoesNotClaimNothingIsLeftOfIt()
    {
        var rig = new RigFixture();
        rig.Processes.Add(4242, "pwsh");
        rig.Marker.Write("old00001", "a previous test", "Start");

        var text = Text(Render(rig, null));

        Assert.Contains("its launcher process is STILL RUNNING (pid 4242)", text);
        Assert.DoesNotContain("nothing is left of that session", text);
    }

    [Fact]
    public void TheProtectedWorldCountIsReportedForBothHalves()
    {
        var rig = new RigFixture();
        rig.AddServerWorld("Luna");
        rig.AddServerWorld("Mars");
        rig.AddInstance("h1", role: "host");
        rig.AddClientWorld("h1", "Hosted");
        rig.Marker.Write("abc12345", "p", "Start");

        var text = Text(Render(rig, null));

        Assert.Contains("  worlds:     2 dedicated-server world(s) were here when that session started and are kept", text);
        Assert.Contains("  instance worlds: 1 client-instance world(s) were here when that session started", text);
    }

    [Fact]
    public void ADegradedWorldSetIsNamedRatherThanCounted()
    {
        // A marker from THIS boot that carries no world set at all, which is degraded case
        // four. Without the boot id it would be the reboot case instead, and the reason
        // printed would be a different one.
        var rig = new RigFixture();
        rig.Fs.AddFile(rig.Paths.DirtyFile, $"owner=old00001\nboot_id={rig.Boot.BootId}\n");

        var text = Text(Render(rig, null));

        Assert.Contains("no dedicated-server world will be deleted", text);
        Assert.Contains("records no dedicated-server world set at all", text);
        Assert.Contains("no client-instance world will be deleted", text);
    }

    [Fact]
    public void OrphansAreWarnedAboutAndListedByPid()
    {
        var rig = new RigFixture();
        rig.Processes.Add(7001, rig.Paths.ServerImage);
        rig.ImagePaths[7001] = Path.Combine(rig.Paths.DediInstall, "server.exe");

        var lines = Render(rig, null);

        Assert.Contains(lines, l => l.Level == OutputLevel.Warning && l.Text.Contains("1 UNTRACKED rig game process(es) are running"));
        Assert.Contains(lines, l => l.Text.Contains("orphan:     rocketstation_DedicatedServer pid 7001"));
        Assert.Contains(lines, l => l.Text.Contains("stop them by pid"));
    }

    [Fact]
    public void AnOrphanWithNoReadableImagePathSaysSo()
    {
        var rig = new RigFixture();
        rig.Processes.Add(7002, rig.Paths.ClientImage);

        Assert.Contains("<image path unreadable>", Text(Render(rig, null)));
    }

    [Fact]
    public void NoOrphansMeansNoOrphanLinesAtAll()
    {
        var rig = new RigFixture();

        Assert.DoesNotContain("orphan", Text(Render(rig, null)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StatusNeverWritesToTheLockFile()
    {
        var rig = new RigFixture();
        rig.AddInstance("c1");
        rig.StartInstance("c1", 5001);
        rig.WriteLockFile("zzz99999",
            refreshedAt: rig.Clock.UtcNow.AddMinutes(-30),
            activeAt: rig.Clock.UtcNow.AddMinutes(-30));
        var before = rig.Fs.Fingerprint();

        Render(rig, "abc12345");

        Assert.Equal(before, rig.Fs.Fingerprint());
    }

    [Fact]
    public void StatusEntersNoCriticalSectionAtAll()
    {
        var rig = new RigFixture();
        rig.WriteLockFile("zzz99999");
        var entriesBefore = rig.Mutex.Entered;

        Render(rig, "abc12345");

        Assert.Equal(entriesBefore, rig.Mutex.Entered);
    }
}
