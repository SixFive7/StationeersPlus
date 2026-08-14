using TestRig.Core.Session;
using TestRig.Tests.Session.Fakes;
using Xunit;

namespace TestRig.Tests.Session;

/// <summary>
/// Release. Ported from rig-lock.tests.ps1 sections 2, 6, 7 and 13 (release ordering and
/// the live-host refusal), plus race R-1, which the PowerShell suite never executed at all.
/// </summary>
public sealed class LockReleaseTests
{
    [Fact]
    public void ReleasingWithNoLockSaysSoAndDoesNotThrow()
    {
        var rig = new RigFixture();

        var result = rig.Lock.Release("abc12345");

        Assert.Equal(ReleaseStatus.NoLock, result.Status);
        Assert.Contains("No rig session lock present", result.Message);
    }

    [Fact]
    public void TheOwnerCanReleaseAndTheLockFileIsGone()
    {
        var rig = new RigFixture();
        var owner = rig.Lease();

        var result = rig.Lock.Release(owner);

        Assert.Equal(ReleaseStatus.Released, result.Status);
        Assert.Equal(owner, result.Owner);
        Assert.Contains($"released (was owner {owner})", result.Message);
        Assert.False(rig.LockFileExists());
    }

    [Fact]
    public void ANonOwnerCannotReleaseAndTheLockSurvives()
    {
        var rig = new RigFixture();
        rig.WriteLockFile("zzz99999", "their test");

        var ex = Assert.Throws<RigRefusalException>(() => rig.Lock.Release("abc12345"));

        Assert.Contains("held by owner 'zzz99999'", ex.Message);
        Assert.Contains("Only the user may authorize --break-lock", ex.Message);
        Assert.True(rig.LockFileExists());
        Assert.Equal("zzz99999", rig.ReadLockFile()!.Get(LockFields.Owner));
    }

    [Fact]
    public void ForceAloneNeverTakesALockOffSomebodyElse()
    {
        // The ownership check runs BEFORE the force check. --force overrides exactly one
        // refusal, the live listen host, and never touches ownership.
        var rig = new RigFixture();
        rig.WriteLockFile("zzz99999", "their test");

        Assert.Throws<RigRefusalException>(() => rig.Lock.Release("abc12345", force: true));
        Assert.True(rig.LockFileExists());
        Assert.Equal("zzz99999", rig.ReadLockFile()!.Get(LockFields.Owner));
    }

    [Fact]
    public void BreakLockSatisfiesTheOwnershipTerm()
    {
        var rig = new RigFixture();
        rig.WriteLockFile("zzz99999", "their test");

        var result = rig.Lock.Release("abc12345", breakLock: true);

        Assert.Equal(ReleaseStatus.Released, result.Status);
        Assert.False(rig.LockFileExists());
    }

    [Fact]
    public void ReleasingIsRefusedWhileAListenHostIsLive()
    {
        var rig = new RigFixture();
        rig.AddInstance("h1", role: "host");
        rig.StartInstance("h1", 6001);
        var owner = rig.Lease();

        var ex = Assert.Throws<RigRefusalException>(() => rig.Lock.Release(owner));

        Assert.Equal(RigRefusalKind.RigBusy, ex.Kind);
        Assert.Contains("a listen-host instance is still live (h1)", ex.Message);
        Assert.Contains("Stop the instances first", ex.Message);
        Assert.True(rig.LockFileExists());
    }

    [Fact]
    public void ForceOverridesTheLiveHostRefusalAndOnlyThatOne()
    {
        var rig = new RigFixture();
        rig.AddInstance("h1", role: "host");
        rig.StartInstance("h1", 6001);
        var owner = rig.Lease();

        var result = rig.Lock.Release(owner, force: true);

        Assert.Equal(ReleaseStatus.Released, result.Status);
        Assert.False(rig.LockFileExists());
    }

    [Fact]
    public void BreakLockDoesNotBypassTheLiveHostRefusal()
    {
        var rig = new RigFixture();
        rig.AddInstance("h1", role: "host");
        rig.StartInstance("h1", 6001);
        rig.WriteLockFile("zzz99999");

        var ex = Assert.Throws<RigRefusalException>(() => rig.Lock.Release("abc12345", breakLock: true));

        Assert.Equal(RigRefusalKind.RigBusy, ex.Kind);
        Assert.True(rig.LockFileExists());
    }

    [Fact]
    public void BreakLockAndForceTogetherAreTheAuthorisedPathThroughBoth()
    {
        var rig = new RigFixture();
        rig.AddInstance("h1", role: "host");
        rig.StartInstance("h1", 6001);
        rig.WriteLockFile("zzz99999");

        var result = rig.Lock.Release("abc12345", breakLock: true, force: true);

        Assert.Equal(ReleaseStatus.Released, result.Status);
        Assert.False(rig.LockFileExists());
    }

    [Fact]
    public void AClientInstanceDoesNotTriggerTheHostRefusal()
    {
        var rig = new RigFixture();
        rig.AddInstance("c1", role: "client");
        rig.StartInstance("c1", 6002);
        var owner = rig.Lease();

        var result = rig.Lock.Release(owner);

        Assert.Equal(ReleaseStatus.Released, result.Status);
        Assert.True(result.Busy.Busy);
        Assert.False(result.Busy.HostLive);
    }

    [Fact]
    public async Task TheRestoreRunsWhileTheSessionStillOwnsTheRig()
    {
        var rig = new RigFixture(wireRestore: false);
        var seen = new List<bool>();
        var service = new SessionLockService(
            rig.Fs, rig.Clock, rig.Sleeper, rig.Mutex, rig.Output, rig.Paths, rig.Busy, rig.Marker, rig.Launcher,
            new ObservingRestore(rig, seen), rig.MintOwnerId);
        var owner = (await service.AcquireAsync(rig.Acquire())).Owner;

        service.Release(owner);

        Assert.Single(seen);
        Assert.True(seen[0]);
        Assert.False(rig.LockFileExists());
    }

    private sealed class ObservingRestore : IRigRestore
    {
        private readonly RigFixture _rig;
        private readonly List<bool> _lockPresentDuringRestore;

        public ObservingRestore(RigFixture rig, List<bool> sink)
        {
            _rig = rig;
            _lockPresentDuringRestore = sink;
        }

        public ResetRun Restore(bool keepState, string reason)
        {
            if (reason == "lock release") _lockPresentDuringRestore.Add(_rig.LockFileExists());
            return new ResetRun(false, "", false, false, [], [], null!);
        }
    }

    [Fact]
    public async Task KeepStateSkipsTheRestoreAndSaysWhy()
    {
        var rig = new RigFixture(wireRestore: false);
        var restore = new FakeRestore();
        var service = new SessionLockService(
            rig.Fs, rig.Clock, rig.Sleeper, rig.Mutex, rig.Output, rig.Paths, rig.Busy, rig.Marker, rig.Launcher,
            restore, rig.MintOwnerId);
        var owner = (await service.AcquireAsync(rig.Acquire())).Owner;
        restore.Calls.Clear();
        rig.Output.Clear();

        var result = service.Release(owner, keepState: true);

        Assert.Equal(ReleaseStatus.Released, result.Status);
        Assert.True(result.RestoreSkipped);
        Assert.Empty(restore.Calls);
        Assert.True(rig.Output.Warned("released WITHOUT restoring it"));
        Assert.True(rig.Output.Warned("The dirty marker stays set"));
    }

    [Fact]
    public async Task AFailedRestoreStillReleases()
    {
        // The marker is only cleared by a restore that completed, so a failure leaves the
        // rig marked and the next acquisition restores it. Refusing to release would leave
        // a hung session holding the rig on top of a mess.
        var rig = new RigFixture(wireRestore: false);
        var restore = new FakeRestore();
        var service = new SessionLockService(
            rig.Fs, rig.Clock, rig.Sleeper, rig.Mutex, rig.Output, rig.Paths, rig.Busy, rig.Marker, rig.Launcher,
            restore, rig.MintOwnerId);
        var owner = (await service.AcquireAsync(rig.Acquire())).Owner;
        restore.Throws = new RigRefusalException(RigRefusalKind.Broken, "an action failed");

        var result = service.Release(owner);

        Assert.Equal(ReleaseStatus.Released, result.Status);
        Assert.Equal("an action failed", result.RestoreFailure);
        Assert.False(rig.LockFileExists());
        Assert.True(rig.Output.Warned("The restore FAILED on the way out"));
        Assert.True(rig.Output.Warned("Nothing is lost except the tidy exit"));
    }

    [Fact]
    public async Task ALockTakenByAnotherSessionDuringTheRestoreIsLeftAlone()
    {
        // The phase-3 re-check. An authorised break from elsewhere could have replaced the
        // file during the slow restore, and deleting a lock this command never validated
        // would be exactly the stomp the mechanism prevents.
        var rig = new RigFixture(wireRestore: false);
        var service = new SessionLockService(
            rig.Fs, rig.Clock, rig.Sleeper, rig.Mutex, rig.Output, rig.Paths, rig.Busy, rig.Marker, rig.Launcher,
            new StealingRestore(rig), rig.MintOwnerId);
        var owner = (await service.AcquireAsync(rig.Acquire())).Owner;

        var result = service.Release(owner);

        Assert.Equal(ReleaseStatus.Stolen, result.Status);
        Assert.Contains("NOT released", result.Message);
        Assert.Contains("belongs to owner 'thief001'", result.Message);
        Assert.True(rig.LockFileExists());
        Assert.Equal("thief001", rig.ReadLockFile()!.Get(LockFields.Owner));
        Assert.NotEqual(owner, rig.ReadLockFile()!.Get(LockFields.Owner));
    }

    private sealed class StealingRestore : IRigRestore
    {
        private readonly RigFixture _rig;

        public StealingRestore(RigFixture rig) => _rig = rig;

        public ResetRun Restore(bool keepState, string reason)
        {
            // Only during the release, so the acquisition that set the test up is untouched.
            if (reason == "lock release") _rig.WriteLockFile("thief001", "an authorised break");
            return new ResetRun(false, "", false, false, [], [], null!);
        }
    }

    [Fact]
    public async Task ALockThatVanishedDuringTheRestoreIsReportedRatherThanFailing()
    {
        var rig = new RigFixture(wireRestore: false);
        var service = new SessionLockService(
            rig.Fs, rig.Clock, rig.Sleeper, rig.Mutex, rig.Output, rig.Paths, rig.Busy, rig.Marker, rig.Launcher,
            new VanishingRestore(rig), rig.MintOwnerId);
        var owner = (await service.AcquireAsync(rig.Acquire())).Owner;

        var result = service.Release(owner);

        Assert.Equal(ReleaseStatus.AlreadyGone, result.Status);
        Assert.Contains("already gone by the time the restore finished", result.Message);
    }

    private sealed class VanishingRestore : IRigRestore
    {
        private readonly RigFixture _rig;

        public VanishingRestore(RigFixture rig) => _rig = rig;

        public ResetRun Restore(bool keepState, string reason)
        {
            if (reason == "lock release") _rig.Fs.DeleteFile(_rig.Paths.LockFile);
            return new ResetRun(false, "", false, false, [], [], null!);
        }
    }

    // ---- stop --release ----------------------------------------------------

    [Fact]
    public async Task StopReleaseGoesThroughTheSameThreePhasesAsUnlock()
    {
        // Race R-1, which the PowerShell suite never executed. The stop path read the lock
        // outside the mutex, ran the slow restore, then deleted the file with no
        // post-restore ownership re-check, so an acquisition that completed during the
        // restore had its brand-new lock file deleted.
        var rig = new RigFixture(wireRestore: false);
        var service = new SessionLockService(
            rig.Fs, rig.Clock, rig.Sleeper, rig.Mutex, rig.Output, rig.Paths, rig.Busy, rig.Marker, rig.Launcher,
            new StealingRestore(rig), rig.MintOwnerId);
        var owner = (await service.AcquireAsync(rig.Acquire())).Owner;

        var result = service.ReleaseForStop(owner);

        Assert.Equal(ReleaseStatus.Stolen, result.Status);
        Assert.True(rig.LockFileExists());
        Assert.Equal("thief001", rig.ReadLockFile()!.Get(LockFields.Owner));
    }

    [Fact]
    public void StopReleaseReleasesYourOwnLock()
    {
        var rig = new RigFixture();
        var owner = rig.Lease();

        var result = rig.Lock.ReleaseForStop(owner);

        Assert.Equal(ReleaseStatus.Released, result.Status);
        Assert.False(rig.LockFileExists());
    }

    [Fact]
    public void StopReleaseRefusesAFreshForeignLock()
    {
        var rig = new RigFixture();
        rig.WriteLockFile("zzz99999", "their live test");

        var ex = Assert.Throws<RigRefusalException>(() => rig.Lock.ReleaseForStop("abc12345"));

        Assert.Contains("--release ignored", ex.Message);
        Assert.Contains("held by 'zzz99999'", ex.Message);
        Assert.True(rig.LockFileExists());
    }

    [Fact]
    public void StopReleaseTakesAnExpiredForeignLockOnAnIdleRig()
    {
        var rig = new RigFixture();
        rig.WriteLockFile("zzz99999",
            refreshedAt: rig.Clock.UtcNow.AddMinutes(-30),
            activeAt: rig.Clock.UtcNow.AddMinutes(-30),
            acquiredAt: rig.Clock.UtcNow.AddMinutes(-30));

        var result = rig.Lock.ReleaseForStop("abc12345");

        Assert.Equal(ReleaseStatus.Released, result.Status);
        Assert.False(rig.LockFileExists());
    }

    [Fact]
    public void StopMustClassifyBeforeReleasingOrABusyForeignSessionLosesItsLock()
    {
        // The shipped ordering, executed rather than re-implemented in the test body: the
        // classifier reports LiveForeign for an expired lock over a busy rig, and that is
        // what stops the release predicate, which has no busy term at all, from firing.
        var rig = new RigFixture();
        rig.AddInstance("c1");
        rig.StartInstance("c1", 5001);
        rig.WriteLockFile("zzz99999",
            refreshedAt: rig.Clock.UtcNow.AddMinutes(-30),
            activeAt: rig.Clock.UtcNow.AddMinutes(-30),
            acquiredAt: rig.Clock.UtcNow.AddMinutes(-30));

        var state = rig.Lock.ReadState("abc12345");
        Assert.Equal(LockState.LiveForeign, state.State);

        // The predicate alone would have said yes, which is the hazard.
        Assert.True(LockClassifier.IsReleasableOnStop(rig.ReadLockFile(), "abc12345", false, rig.Clock.UtcNow));
        Assert.True(rig.LockFileExists());
    }

    [Fact]
    public void ReleasingWhileBusyIsReportedSoACallerCanWarn()
    {
        var rig = new RigFixture();
        rig.AddInstance("c1");
        rig.StartInstance("c1", 5001);
        var owner = rig.Lease();

        var result = rig.Lock.Release(owner);

        Assert.Equal(ReleaseStatus.Released, result.Status);
        Assert.True(result.Busy.Busy);
        Assert.Contains("c1=client", result.Busy.Detail);
    }
}
