using TestRig.Core.Session;
using TestRig.Tests.Session.Fakes;
using Xunit;

namespace TestRig.Tests.Session;

/// <summary>
/// The busy probe. Ported from rig-lock.tests.ps1 sections 8 (busy signal), 9 (process
/// identity), 10 (orphans) and 11 (player counting).
/// </summary>
public sealed class BusySignalTests
{
    // ---- player counting ---------------------------------------------------

    [Fact]
    public void AMissingOrEmptyLogCountsNobody()
    {
        var rig = new RigFixture();

        Assert.Equal(0, rig.Busy.CountPlayers(null));
        Assert.Equal(0, rig.Busy.CountPlayers(""));
        Assert.Equal(0, rig.Busy.CountPlayers(rig.Paths.ServerLog));

        rig.Fs.AddFile(rig.Paths.ServerLog, "");
        Assert.Equal(0, rig.Busy.CountPlayers(rig.Paths.ServerLog));
    }

    [Fact]
    public void ReadyLinesCount()
    {
        var rig = new RigFixture();
        rig.Fs.AddFile(rig.Paths.ServerLog,
            "Client Bob (7656119800000001) is ready\r\nClient Ann (7656119800000002) is ready\r\n");

        Assert.Equal(2, rig.Busy.CountPlayers(rig.Paths.ServerLog));
    }

    [Fact]
    public void DisconnectLinesSubtract()
    {
        var rig = new RigFixture();
        rig.Fs.AddFile(rig.Paths.ServerLog,
            "Client Bob (1) is ready\r\nClient Ann (2) is ready\r\nClient disconnected: Bob\r\n");

        Assert.Equal(1, rig.Busy.CountPlayers(rig.Paths.ServerLog));
    }

    [Fact]
    public void TheCountFloorsAtZero()
    {
        var rig = new RigFixture();
        rig.Fs.AddFile(rig.Paths.ServerLog, "Client disconnected: Bob\r\nClient disconnected: Ann\r\n");

        Assert.Equal(0, rig.Busy.CountPlayers(rig.Paths.ServerLog));
    }

    [Fact]
    public void UnrelatedLinesAreIgnored()
    {
        var rig = new RigFixture();
        rig.Fs.AddFile(rig.Paths.ServerLog,
            "loading world\r\nsomething about a client somewhere\r\nClient Bob (1) is ready\r\ndone\r\n");

        Assert.Equal(1, rig.Busy.CountPlayers(rig.Paths.ServerLog));
    }

    [Fact]
    public void ALineMatchingBothPatternsCountsAsAJoin()
    {
        // else-if, not two ifs.
        var rig = new RigFixture();
        rig.Fs.AddFile(rig.Paths.ServerLog, "Client disconnected: (x) is ready\r\n");

        Assert.Equal(1, rig.Busy.CountPlayers(rig.Paths.ServerLog));
    }

    // ---- the two things that make the rig busy -----------------------------

    [Fact]
    public void AnIdleRigIsNotBusy()
    {
        var rig = new RigFixture();

        var busy = rig.Busy.Probe();

        Assert.False(busy.Busy);
        Assert.Equal("", busy.Detail);
        Assert.False(busy.ServerLive);
        Assert.False(busy.HostLive);
        Assert.Empty(busy.Instances);
        Assert.Empty(busy.Orphans);
    }

    [Fact]
    public void AServerWithNobodyConnectedIsLiveButNotBusy()
    {
        // Deliberate asymmetry: this is what lets an abandoned server be reclaimed.
        var rig = new RigFixture();
        rig.StartServer(players: 0);

        var busy = rig.Busy.Probe();

        Assert.True(busy.ServerLive);
        Assert.Equal(0, busy.ServerPlayers);
        Assert.False(busy.Busy);
    }

    [Fact]
    public void AServerWithAConnectedPlayerIsBusy()
    {
        var rig = new RigFixture();
        rig.StartServer(players: 2);

        var busy = rig.Busy.Probe();

        Assert.True(busy.Busy);
        Assert.Equal(2, busy.ServerPlayers);
        Assert.Contains("2 player(s) connected to the dedicated server", busy.Detail);
    }

    [Fact]
    public void AnyLiveClientInstanceIsBusy()
    {
        // The bar is lower on this half because the running processes ARE the test.
        var rig = new RigFixture();
        rig.AddInstance("c1");
        rig.StartInstance("c1", 5001);

        var busy = rig.Busy.Probe();

        Assert.True(busy.Busy);
        Assert.Single(busy.Instances);
        Assert.Equal("c1", busy.Instances[0].Name);
        Assert.Equal("client", busy.Instances[0].Role);
        Assert.Contains("1 client instance(s) running: c1=client", busy.Detail);
    }

    [Fact]
    public void ADeadInstanceIsSkippedEntirely()
    {
        var rig = new RigFixture();
        rig.AddInstance("c1");
        rig.Fs.AddFile(rig.Paths.InstancePidFile("c1"), "5001");

        var busy = rig.Busy.Probe();

        Assert.Empty(busy.Instances);
        Assert.False(busy.Busy);
    }

    [Fact]
    public void AHostInstanceIsNamedAsAHostWithItsConnectedCount()
    {
        var rig = new RigFixture();
        rig.AddInstance("h1", role: "host");
        rig.StartInstance("h1", 6001);
        rig.Fs.AddDirectory(rig.Paths.InstanceLogDir("h1"));
        rig.Fs.AddFile(Path.Combine(rig.Paths.InstanceLogDir("h1"), "unity-001.log"),
            "Client Bob (1) is ready\r\n");

        var busy = rig.Busy.Probe();

        Assert.True(busy.HostLive);
        Assert.Equal(["h1"], busy.HostNames);
        Assert.Equal(1, busy.Instances[0].Players);
        Assert.Contains("h1=HOST (1 connected)", busy.Detail);
    }

    [Fact]
    public void AHostWithNoLogYetReportsItsClientCountAsUnknownRatherThanZero()
    {
        // Players distinguishes null (not known) from 0 (known to be nobody). The log must
        // be found first, or a host that has not written one reads as an empty session.
        var rig = new RigFixture();
        rig.AddInstance("h1", role: "host");
        rig.StartInstance("h1", 6001);

        var busy = rig.Busy.Probe();

        Assert.Null(busy.Instances[0].Players);
        Assert.Contains("h1=HOST (connected clients unknown)", busy.Detail);
    }

    [Fact]
    public void TheNewestInstanceLogWins()
    {
        var rig = new RigFixture();
        rig.AddInstance("h1", role: "host");
        rig.StartInstance("h1", 6001);
        var logs = rig.Paths.InstanceLogDir("h1");
        rig.Fs.AddDirectory(logs);
        rig.Fs.AddFile(Path.Combine(logs, "unity-old.log"), "Client A (1) is ready\r\nClient B (2) is ready\r\n");
        rig.Fs.AddFile(Path.Combine(logs, "unity-new.log"), "Client C (3) is ready\r\n");
        rig.Fs.SetLastWrite(Path.Combine(logs, "unity-old.log"), rig.Clock.UtcNow.AddHours(-2));
        rig.Fs.SetLastWrite(Path.Combine(logs, "unity-new.log"), rig.Clock.UtcNow);

        Assert.EndsWith("unity-new.log", rig.Busy.NewestInstanceLog(rig.Paths.InstanceDataDir("h1")), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, rig.Busy.Probe().Instances[0].Players);
    }

    [Fact]
    public void AnUnreadableManifestDegradesToRoleUnknownAndStillCountsAsLive()
    {
        var rig = new RigFixture();
        rig.AddInstance("c1");
        rig.Fs.AddFile(rig.Paths.InstanceManifest("c1"), "{ this is not json");
        rig.StartInstance("c1", 5001);

        var busy = rig.Busy.Probe();

        Assert.Single(busy.Instances);
        Assert.Null(busy.Instances[0].Role);
        Assert.Contains("c1=role unknown", busy.Detail);
        Assert.True(busy.Busy);
    }

    [Fact]
    public void AMissingManifestFallsBackToTheDirectoryName()
    {
        var rig = new RigFixture();
        rig.Fs.AddDirectory(rig.Paths.InstanceDataDir("c1"));
        rig.StartInstance("c1", 5001);

        var busy = rig.Busy.Probe();

        Assert.Equal("c1", busy.Instances[0].Name);
        Assert.Null(busy.Instances[0].Role);
    }

    [Fact]
    public void TheManifestsInstanceNameWinsOverTheDirectoryName()
    {
        var rig = new RigFixture();
        rig.Fs.AddDirectory(rig.Paths.InstanceDataDir("dir-name"));
        rig.Fs.AddFile(rig.Paths.InstanceManifest("dir-name"), """{"instanceName":"friendly","role":"client"}""");
        rig.StartInstance("dir-name", 5001);

        Assert.Equal("friendly", rig.Busy.Probe().Instances[0].Name);
    }

    [Fact]
    public void ServerAndClientReasonsCompose()
    {
        var rig = new RigFixture();
        rig.StartServer(players: 1);
        rig.AddInstance("c1");
        rig.StartInstance("c1", 5001);

        var detail = rig.Busy.Probe().Detail;

        Assert.Contains("1 player(s) connected to the dedicated server", detail);
        Assert.Contains("1 client instance(s) running", detail);
        Assert.Contains(";", detail);
    }

    // ---- process identity --------------------------------------------------

    [Fact]
    public void APidFileNamingALiveProcessOfTheWrongImageIsStale()
    {
        // Windows recycles process ids and the rig's pid files genuinely go stale, because
        // no cleanup runs on a force-kill or a reboot. Trusting the bare number would
        // report the rig busy for ever.
        var rig = new RigFixture();
        rig.AddInstance("c1");
        rig.Fs.AddFile(rig.Paths.InstancePidFile("c1"), "5001");
        rig.Processes.Add(5001, "notepad");

        Assert.False(rig.Busy.IsPidClaimAlive(rig.Paths.InstancePidFile("c1"), [rig.Paths.ClientImage]));
        Assert.Empty(rig.Busy.Probe().Instances);
    }

    [Fact]
    public void TheSameFixtureDoesReportBusyOnceTheImageMatches()
    {
        // The paired discrimination: the check above is a real answer, not a blanket false.
        var rig = new RigFixture();
        rig.AddInstance("c1");
        rig.Fs.AddFile(rig.Paths.InstancePidFile("c1"), "5001");
        rig.Processes.Add(5001, rig.Paths.ClientImage);

        Assert.True(rig.Busy.IsPidClaimAlive(rig.Paths.InstancePidFile("c1"), [rig.Paths.ClientImage]));
        Assert.True(rig.Busy.Probe().Busy);
    }

    [Fact]
    public void AGarbageOrEmptyPidFileIsNotAliveAndDoesNotThrow()
    {
        var rig = new RigFixture();
        rig.Fs.AddFile(Path.Combine(rig.Paths.DediData, "garbage.pid"), "not a number");
        rig.Fs.AddFile(Path.Combine(rig.Paths.DediData, "empty.pid"), "   ");

        Assert.Null(rig.Busy.ReadPid(Path.Combine(rig.Paths.DediData, "garbage.pid")));
        Assert.Null(rig.Busy.ReadPid(Path.Combine(rig.Paths.DediData, "empty.pid")));
        Assert.Null(rig.Busy.ReadPid(Path.Combine(rig.Paths.DediData, "missing.pid")));
        Assert.False(rig.Busy.IsPidClaimAlive(Path.Combine(rig.Paths.DediData, "garbage.pid"), ["anything"]));
    }

    [Fact]
    public void APidFileWithSurroundingWhitespaceStillParses()
    {
        var rig = new RigFixture();
        rig.Fs.AddFile(rig.Paths.ServerPidFile, "  9001\r\n");

        Assert.Equal(9001, rig.Busy.ReadPid(rig.Paths.ServerPidFile));
    }

    [Fact]
    public void AProcessThatStartedLongAfterItsPidFileIsARecycledIdNotTheTrackedProcess()
    {
        // Closes the reuse case the image check cannot: a fresh rocketstation that happens
        // to inherit a stale game.pid's number read as that tracked instance in PowerShell.
        var rig = new RigFixture();
        rig.AddInstance("c1");
        rig.Fs.AddFile(rig.Paths.InstancePidFile("c1"), "5001");
        rig.Fs.SetLastWrite(rig.Paths.InstancePidFile("c1"), rig.Clock.UtcNow.AddHours(-6));
        rig.Processes.Add(5001, rig.Paths.ClientImage, rig.Clock.UtcNow);

        Assert.False(rig.Busy.IsPidClaimAlive(rig.Paths.InstancePidFile("c1"), [rig.Paths.ClientImage]));
    }

    [Fact]
    public void AProcessThatStartedJustBeforeItsPidFileIsBelieved()
    {
        var rig = new RigFixture();
        rig.AddInstance("c1");
        rig.Fs.AddFile(rig.Paths.InstancePidFile("c1"), "5001");
        rig.Fs.SetLastWrite(rig.Paths.InstancePidFile("c1"), rig.Clock.UtcNow);
        rig.Processes.Add(5001, rig.Paths.ClientImage, rig.Clock.UtcNow.AddSeconds(-30));

        Assert.True(rig.Busy.IsPidClaimAlive(rig.Paths.InstancePidFile("c1"), [rig.Paths.ClientImage]));
    }

    [Fact]
    public void AProcessStartedInsideTheMarginIsStillBelievedBecauseTheSafeAnswerIsAlive()
    {
        var rig = new RigFixture();
        rig.AddInstance("c1");
        rig.Fs.AddFile(rig.Paths.InstancePidFile("c1"), "5001");
        rig.Fs.SetLastWrite(rig.Paths.InstancePidFile("c1"), rig.Clock.UtcNow);
        rig.Processes.Add(5001, rig.Paths.ClientImage, rig.Clock.UtcNow.Add(BusyProbe.PidReuseMargin).AddSeconds(-1));

        Assert.True(rig.Busy.IsPidClaimAlive(rig.Paths.InstancePidFile("c1"), [rig.Paths.ClientImage]));
    }

    [Fact]
    public void TheStalePidLoopEndsWithAReclaimableLock()
    {
        // The whole loop end to end: a stale pid file must not keep a dead session's lock
        // alive through the busy self-renew, which is the one failure the timers exist for.
        var rig = new RigFixture();
        rig.AddInstance("c1");
        rig.Fs.AddFile(rig.Paths.InstancePidFile("c1"), "5001");
        rig.Processes.Add(5001, "notepad");
        rig.WriteLockFile("zzz99999",
            refreshedAt: rig.Clock.UtcNow.AddMinutes(-30),
            activeAt: rig.Clock.UtcNow.AddMinutes(-30),
            acquiredAt: rig.Clock.UtcNow.AddMinutes(-30));

        var state = rig.Lock.ReadState("abc12345");

        Assert.False(rig.Busy.Probe().Busy);
        Assert.Equal(LockState.DeadForeign, state.State);
        Assert.Equal(ReclaimReason.Ttl, state.Reclaim);
    }

    // ---- orphans -----------------------------------------------------------

    [Fact]
    public void AnUntrackedProcessInsideARigTreeIsAnOrphan()
    {
        var rig = new RigFixture();
        rig.Processes.Add(7001, rig.Paths.ClientImage);
        rig.ImagePaths[7001] = Path.Combine(RigFixture.InstancesRoot, "c1", "rocketstation.exe");

        var orphans = rig.Busy.FindOrphans();

        Assert.Single(orphans);
        Assert.Equal(7001, orphans[0].ProcessId);
        Assert.Equal(OrphanScope.Rig, orphans[0].Scope);
    }

    [Fact]
    public void TheDevelopersOwnClientIsNeverReported()
    {
        var rig = new RigFixture();
        rig.Processes.Add(7002, rig.Paths.ClientImage);
        rig.ImagePaths[7002] = Path.Combine(RigFixture.SourceInstall, "rocketstation.exe");

        Assert.Empty(rig.Busy.FindOrphans());
    }

    [Fact]
    public void AProcessWhoseImagePathCannotBeReadIsReportedRatherThanDropped()
    {
        var rig = new RigFixture();
        rig.Processes.Add(7003, rig.Paths.ClientImage);

        var orphans = rig.Busy.FindOrphans();

        Assert.Single(orphans);
        Assert.Equal(OrphanScope.Unknown, orphans[0].Scope);
        Assert.Null(orphans[0].ImagePath);
    }

    [Fact]
    public void AnUntrackedDedicatedServerInThisRigsInstallIsOurs()
    {
        var rig = new RigFixture();
        rig.Processes.Add(7004, rig.Paths.ServerImage);
        rig.ImagePaths[7004] = Path.Combine(rig.Paths.DediInstall, "rocketstation_DedicatedServer.exe");

        var orphans = rig.Busy.FindOrphans();

        Assert.Single(orphans);
        Assert.Equal(OrphanScope.Rig, orphans[0].Scope);
    }

    /// <summary>
    /// A dedicated server this rig did not install is not this rig's orphan.
    /// </summary>
    /// <remarks>
    /// It used to be. The rule was "an untracked dedicated server is ours wherever it lives,
    /// because the developer does not run one outside the rig", which is an assumption about
    /// a person that the rig cannot check, and it cost a refusal with no remedy: a reported
    /// orphan blocks every state reset, and this rig cannot stop a process it did not start.
    /// A second clone of this repository, or a server run for anybody else, pinned the first
    /// rig for as long as it lived.
    ///
    /// Measured 2026-08-15 on the shipped binary: an orphaned rocketstation_DedicatedServer
    /// out of a folder no rig owns made "reset --dry-run" exit 6 against a rig home in a temp
    /// folder that had never held a server at all.
    /// </remarks>
    [Fact]
    public void AnUntrackedDedicatedServerSomewhereElseIsNotOurs()
    {
        var rig = new RigFixture();
        rig.Processes.Add(7004, rig.Paths.ServerImage);
        rig.ImagePaths[7004] = @"Z:\somewhere\else\rocketstation_DedicatedServer.exe";

        Assert.Empty(rig.Busy.FindOrphans());
        Assert.True(rig.Planner.CheckGate().Allowed, "somebody else's server must not block this rig's reset");
    }

    /// <summary>
    /// A dedicated server under a SECOND recorded instance root is still ours.
    /// </summary>
    /// <remarks>
    /// The path rule is what carries the whole answer now, so it has to cover every tree this
    /// rig records and not merely the install (CLIENT-007 in the other direction).
    /// </remarks>
    [Fact]
    public void AnUntrackedDedicatedServerUnderARecordedInstanceRootIsOurs()
    {
        var rig = new RigFixture();
        rig.Processes.Add(7005, rig.Paths.ServerImage);
        rig.ImagePaths[7005] = Path.Combine(RigFixture.InstancesRoot, "srv", "rocketstation_DedicatedServer.exe");

        Assert.Equal(OrphanScope.Rig, Assert.Single(rig.Busy.FindOrphans()).Scope);
    }

    /// <summary>
    /// A dedicated server whose image path cannot be read is still reported.
    /// </summary>
    /// <remarks>
    /// The safe direction survives the scoping change: dropping the one process nobody can
    /// identify is how an orphan stays invisible, and this is the case the "wherever it
    /// lives" rule was really covering.
    /// </remarks>
    [Fact]
    public void AnUntrackedDedicatedServerWithNoReadableImagePathIsStillReported()
    {
        var rig = new RigFixture();
        rig.Processes.Add(7006, rig.Paths.ServerImage);

        Assert.Equal(OrphanScope.Unknown, Assert.Single(rig.Busy.FindOrphans()).Scope);
        Assert.False(rig.Planner.CheckGate().Allowed);
    }

    [Fact]
    public void ATrackedProcessIsNotAnOrphan()
    {
        var rig = new RigFixture();
        rig.AddInstance("c1");
        rig.StartInstance("c1", 7005);
        rig.ImagePaths[7005] = Path.Combine(RigFixture.InstancesRoot, "c1", "rocketstation.exe");

        Assert.Empty(rig.Busy.FindOrphans());
        Assert.Contains(7005, rig.Busy.TrackedProcessIds());
    }

    [Fact]
    public void APidFileTracksItsPidEvenWhenTheProcessIsDead()
    {
        var rig = new RigFixture();
        rig.AddInstance("c1");
        rig.Fs.AddFile(rig.Paths.InstancePidFile("c1"), "7006");
        rig.Fs.AddFile(rig.Paths.ServerPidFile, "7007");

        var tracked = rig.Busy.TrackedProcessIds();

        Assert.Contains(7006, tracked);
        Assert.Contains(7007, tracked);
    }

    [Fact]
    public void OrphansNeverMakeTheRigBusyButAreAlwaysNamedInTheDetail()
    {
        // An orphan is unreachable by any launcher action, so counting it as busy would pin
        // the lock live with no way to clear it short of a human-gated break.
        var rig = new RigFixture();
        rig.Processes.Add(7008, rig.Paths.ServerImage);
        rig.ImagePaths[7008] = Path.Combine(rig.Paths.DediInstall, "rocketstation_DedicatedServer.exe");

        var busy = rig.Busy.Probe();

        Assert.False(busy.Busy);
        Assert.Single(busy.Orphans);
        Assert.Contains("UNTRACKED rig game process(es), not counted as busy", busy.Detail);
        Assert.Contains("pid 7008", busy.Detail);
        Assert.Contains("kill them by pid", busy.Detail);
    }

    [Fact]
    public void AnExpiredLockStaysReclaimableWhileOrphansExist()
    {
        var rig = new RigFixture();
        rig.Processes.Add(7009, rig.Paths.ServerImage);
        rig.ImagePaths[7009] = Path.Combine(rig.Paths.DediInstall, "s.exe");
        rig.WriteLockFile("zzz99999",
            refreshedAt: rig.Clock.UtcNow.AddMinutes(-30),
            activeAt: rig.Clock.UtcNow.AddMinutes(-30),
            acquiredAt: rig.Clock.UtcNow.AddMinutes(-30));

        Assert.Equal(LockState.DeadForeign, rig.Lock.ReadState("abc12345").State);
    }

    [Fact]
    public void TheOrphanDetailMarksAnUnreadableImagePath()
    {
        var rig = new RigFixture();
        rig.Processes.Add(7010, rig.Paths.ClientImage);

        Assert.Contains("(image path unreadable)", rig.Busy.Probe().Detail);
    }
}
