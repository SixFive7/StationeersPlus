using TestRig.Core.Session;
using TestRig.Tests.Session.Fakes;
using Xunit;

namespace TestRig.Tests.Session;

/// <summary>
/// Acquisition. Ported from rig-lock.tests.ps1 sections 2, 4, 7 (BreakLock) and 14
/// (queueing), plus the two defects the port fixes.
/// </summary>
public sealed class LockAcquireTests
{
    [Fact]
    public async Task AcquiringAFreeRigReturnsAnOwnerIdAsATypedField()
    {
        // FIX 2, and the reason this subsystem was written first. In PowerShell New-RigLock
        // returned a bare string, so the launcher's $outcome.Owner was always null and the
        // TESTRIG-OWNER line it guards has never once printed. The harness requires that
        // line by regex, throws inconclusive/rig-unavailable without it, and then unlocks
        // with the id it never got, leaving the rig locked by a session that cannot release
        // it. Nothing here parses prose.
        var rig = new RigFixture();

        var result = await rig.Lock.AcquireAsync(rig.Acquire("network paint check"));

        Assert.False(string.IsNullOrWhiteSpace(result.Owner));
        Assert.Equal(AcquireKind.Acquired, result.Kind);
        Assert.Equal("network paint check", result.Purpose);
        Assert.Equal(10, result.TtlMinutes);
        Assert.Equal(60, result.IdleCeilingMinutes);
    }

    [Fact]
    public async Task TheOwnerIdIsAlsoEmittedAsAMachineReadableValue()
    {
        var rig = new RigFixture();

        var result = await rig.Lock.AcquireAsync(rig.Acquire());

        Assert.Equal(result.Owner, rig.Output.ValueOf("owner"));
        Assert.Equal("Acquired", rig.Output.ValueOf("acquireKind"));
    }

    [Fact]
    public void TheDefaultOwnerIdIsEightLowercaseHexCharacters()
    {
        for (var i = 0; i < 20; i++)
        {
            var id = SessionLockService.DefaultOwnerId();
            Assert.Equal(8, id.Length);
            Assert.Matches("^[0-9a-f]{8}$", id);
        }
    }

    [Fact]
    public void MintedOwnerIdsAreDistinct()
    {
        var ids = Enumerable.Range(0, 200).Select(static _ => SessionLockService.DefaultOwnerId()).ToHashSet();

        Assert.Equal(200, ids.Count);
    }

    [Fact]
    public async Task AcquisitionWritesTheEightCanonicalFields()
    {
        var rig = new RigFixture();

        var result = await rig.Lock.AcquireAsync(rig.Acquire("probe"));
        var fields = rig.ReadLockFile()!;

        Assert.Equal(
            ["owner", "purpose", "acquired_at", "refreshed_at", "active_at", "ttl_minutes", "idle_ceiling_minutes", "host"],
            fields.Keys);
        Assert.Equal(result.Owner, fields.Get("owner"));
        Assert.Equal("probe", fields.Get("purpose"));
        Assert.Equal("10", fields.Get("ttl_minutes"));
        Assert.Equal("60", fields.Get("idle_ceiling_minutes"));
        Assert.Equal("RIGTEST", fields.Get("host"));
        Assert.Equal(RigTime.Stamp(rig.Clock.UtcNow), fields.Get("acquired_at"));
        Assert.Equal(fields.Get("acquired_at"), fields.Get("refreshed_at"));
        Assert.Equal(fields.Get("acquired_at"), fields.Get("active_at"));
    }

    [Fact]
    public async Task AcquisitionRefusesWithNoPurpose()
    {
        var rig = new RigFixture();

        var ex = await Assert.ThrowsAsync<RigRefusalException>(
            () => rig.Lock.AcquireAsync(new AcquireOptions { Purpose = "  " }));

        Assert.Equal(RigRefusalKind.Refused, ex.Kind);
        Assert.Contains("--purpose", ex.Message);
        Assert.False(rig.LockFileExists());
    }

    [Fact]
    public async Task TakingTheLockDoesNotMarkTheRigDirty()
    {
        // Nothing has been mutated yet, and that is precisely what lets a world staged
        // between lock and the first mutating command still be recorded and kept.
        var rig = new RigFixture();

        await rig.Lock.AcquireAsync(rig.Acquire());

        Assert.False(rig.MarkerExists());
    }

    [Fact]
    public async Task AFreshForeignLockBlocksAndLeavesTheExistingOwnerInPlace()
    {
        var rig = new RigFixture();
        rig.WriteLockFile("zzz99999", "somebody else's test");

        var ex = await Assert.ThrowsAsync<RigRefusalException>(() => rig.Lock.AcquireAsync(rig.Acquire()));

        Assert.Equal(RigRefusalKind.HeldByAnotherSession, ex.Kind);
        Assert.Contains("Cannot acquire", ex.Message);
        Assert.Contains("somebody else's test", ex.Message);
        Assert.Contains("Only the user may authorize --break-lock", ex.Message);
        Assert.Equal("zzz99999", rig.ReadLockFile()!.Get(LockFields.Owner));
    }

    [Fact]
    public async Task ReAssertingKeepsTheOwnerAndTheOriginalAcquisitionTime()
    {
        var rig = new RigFixture();
        var owner = rig.Lease("first purpose");
        var acquiredAt = rig.ReadLockFile()!.GetOrEmpty(LockFields.AcquiredAt);
        rig.Clock.AdvanceMinutes(5);

        var result = await rig.Lock.AcquireAsync(new AcquireOptions { Purpose = "second purpose", CallerId = owner });

        Assert.Equal(AcquireKind.Reasserted, result.Kind);
        Assert.Equal(owner, result.Owner);
        var fields = rig.ReadLockFile()!;
        Assert.Equal(acquiredAt, fields.Get(LockFields.AcquiredAt));
        Assert.Equal("second purpose", fields.Get(LockFields.Purpose));
        Assert.Equal(RigTime.Stamp(rig.Clock.UtcNow), fields.Get(LockFields.RefreshedAt));
        Assert.Equal(RigTime.Stamp(rig.Clock.UtcNow), fields.Get(LockFields.ActiveAt));
    }

    [Fact]
    public async Task ReAssertingSaysTheStateWasNotReset()
    {
        var rig = new RigFixture();
        var owner = rig.Lease();

        var result = await rig.Lock.AcquireAsync(new AcquireOptions { Purpose = "again", CallerId = owner });

        Assert.False(result.StateWasReset);
        Assert.True(rig.Output.Said("state was NOT reset"));
        Assert.True(rig.Output.Said("Re-asserted"));
    }

    [Fact]
    public async Task ReAssertingDoesNotResetTheStateAtAll()
    {
        var rig = new RigFixture(wireRestore: false);
        var restore = new FakeRestore();
        var service = new SessionLockService(
            rig.Fs, rig.Clock, rig.Sleeper, rig.Mutex, rig.Output, rig.Paths, rig.Busy, rig.Marker, rig.Launcher,
            restore, rig.MintOwnerId);

        var first = await service.AcquireAsync(new AcquireOptions { Purpose = "one" });
        Assert.Single(restore.Calls);

        await service.AcquireAsync(new AcquireOptions { Purpose = "two", CallerId = first.Owner });
        Assert.Single(restore.Calls);
    }

    [Fact]
    public async Task ReAssertingWithoutTypingACeilingKeepsTheRaisedOne()
    {
        // The fixed defect. A session that took the rig with a 240-minute ceiling (the
        // documented way to wait for a human) and later re-asserted to change its purpose
        // was silently dropped back to 60, with nothing warning. Nullable options model
        // "was this typed" explicitly rather than inferring it from a default.
        var rig = new RigFixture();
        var first = await rig.Lock.AcquireAsync(new AcquireOptions { Purpose = "waiting on a human", IdleCeilingMinutes = 240, TtlMinutes = 20 });
        Assert.Equal(240, first.IdleCeilingMinutes);

        var again = await rig.Lock.AcquireAsync(new AcquireOptions { Purpose = "still waiting", CallerId = first.Owner });

        Assert.Equal(240, again.IdleCeilingMinutes);
        Assert.Equal(20, again.TtlMinutes);
        Assert.Equal("240", rig.ReadLockFile()!.Get(LockFields.IdleCeilingMinutes));
        Assert.Equal("20", rig.ReadLockFile()!.Get(LockFields.TtlMinutes));
    }

    [Fact]
    public async Task ReAssertingWithAnExplicitCeilingDoesChangeIt()
    {
        var rig = new RigFixture();
        var first = await rig.Lock.AcquireAsync(new AcquireOptions { Purpose = "p", IdleCeilingMinutes = 240 });

        var again = await rig.Lock.AcquireAsync(
            new AcquireOptions { Purpose = "p", CallerId = first.Owner, IdleCeilingMinutes = 30 });

        Assert.Equal(30, again.IdleCeilingMinutes);
        Assert.Equal("30", rig.ReadLockFile()!.Get(LockFields.IdleCeilingMinutes));
    }

    [Fact]
    public async Task AnExpiredLockOnAnIdleRigIsReclaimedWithANewOwnerId()
    {
        var rig = new RigFixture();
        rig.WriteLockFile("zzz99999",
            refreshedAt: rig.Clock.UtcNow.AddMinutes(-30),
            activeAt: rig.Clock.UtcNow.AddMinutes(-30),
            acquiredAt: rig.Clock.UtcNow.AddMinutes(-30));

        var result = await rig.Lock.AcquireAsync(rig.Acquire());

        Assert.Equal(AcquireKind.ReclaimedExpired, result.Kind);
        Assert.NotEqual("zzz99999", result.Owner);
        Assert.Equal(result.Owner, rig.ReadLockFile()!.Get(LockFields.Owner));
    }

    [Fact]
    public async Task ReclaimingPastTheCeilingWarnsTwiceWhenTheRigIsBusy()
    {
        var rig = new RigFixture();
        rig.AddInstance("c1");
        rig.StartInstance("c1", 5001);
        rig.WriteLockFile("zzz99999", "abandoned test",
            refreshedAt: rig.Clock.UtcNow,
            activeAt: rig.Clock.UtcNow.AddMinutes(-180),
            acquiredAt: rig.Clock.UtcNow.AddMinutes(-180));

        var result = await rig.Lock.AcquireAsync(rig.Acquire());

        Assert.Equal(AcquireKind.ReclaimedIdleCeiling, result.Kind);
        Assert.True(rig.Output.Warned("IDLE WATCHDOG: reclaimed the rig from owner zzz99999"));
        Assert.True(rig.Output.Warned("past its 60 min idle ceiling"));
        Assert.True(rig.Output.Warned("the rig was still BUSY when it was reclaimed"));
        Assert.True(rig.Output.Warned("c1=client"));
    }

    [Fact]
    public async Task ReclaimingPastTheCeilingOnAnIdleRigWarnsOnlyOnce()
    {
        var rig = new RigFixture();
        rig.WriteLockFile("zzz99999", "abandoned test",
            refreshedAt: rig.Clock.UtcNow,
            activeAt: rig.Clock.UtcNow.AddMinutes(-180),
            acquiredAt: rig.Clock.UtcNow.AddMinutes(-180));

        await rig.Lock.AcquireAsync(rig.Acquire());

        Assert.True(rig.Output.Warned("IDLE WATCHDOG: reclaimed the rig"));
        Assert.False(rig.Output.Warned("still BUSY when it was reclaimed"));
    }

    [Fact]
    public async Task AReclaimRunsTheTeardownCallback()
    {
        var rig = new RigFixture();
        var reclaimed = 0;
        rig.WriteLockFile("zzz99999",
            refreshedAt: rig.Clock.UtcNow.AddMinutes(-30),
            activeAt: rig.Clock.UtcNow.AddMinutes(-30),
            acquiredAt: rig.Clock.UtcNow.AddMinutes(-30));

        await rig.Lock.AcquireAsync(new AcquireOptions { Purpose = "p", OnReclaim = () => reclaimed++ });

        Assert.Equal(1, reclaimed);
    }

    [Fact]
    public async Task AnOrdinaryAcquisitionOnAFreeRigDoesNotRunTheTeardownCallback()
    {
        var rig = new RigFixture();
        var reclaimed = 0;

        await rig.Lock.AcquireAsync(new AcquireOptions { Purpose = "p", OnReclaim = () => reclaimed++ });

        Assert.Equal(0, reclaimed);
    }

    // ---- break-lock --------------------------------------------------------

    [Fact]
    public async Task BreakLockTakesALiveForeignLockAndMintsANewOwner()
    {
        var rig = new RigFixture();
        rig.WriteLockFile("zzz99999", "somebody else's live test");

        var result = await rig.Lock.AcquireAsync(new AcquireOptions { Purpose = "authorised", BreakLock = true });

        Assert.Equal(AcquireKind.Broke, result.Kind);
        Assert.NotEqual("zzz99999", result.Owner);
        Assert.True(rig.Output.Warned("--break-lock: broke a live lock held by 'somebody else's live test' (owner zzz99999)"));
    }

    [Fact]
    public async Task BreakingALiveLockDoesNotRunTheTeardownCallback()
    {
        // A break leaves the previous session's processes running, deliberately: a human
        // authorised taking the reservation, not necessarily killing what is on the rig.
        var rig = new RigFixture();
        var reclaimed = 0;
        rig.WriteLockFile("zzz99999", "live");

        await rig.Lock.AcquireAsync(new AcquireOptions { Purpose = "p", BreakLock = true, OnReclaim = () => reclaimed++ });

        Assert.Equal(0, reclaimed);
    }

    [Fact]
    public async Task ALiveLockIsNeverBrokenImplicitly()
    {
        var rig = new RigFixture();
        rig.WriteLockFile("zzz99999", "live");

        await Assert.ThrowsAsync<RigRefusalException>(() => rig.Lock.AcquireAsync(rig.Acquire()));

        Assert.Equal("zzz99999", rig.ReadLockFile()!.Get(LockFields.Owner));
    }

    // ---- queueing ----------------------------------------------------------

    [Fact]
    public async Task WithNoWaitBudgetTheFirstBlockFailsAtOnce()
    {
        var rig = new RigFixture();
        rig.WriteLockFile("zzz99999", "live");

        await Assert.ThrowsAsync<RigRefusalException>(() => rig.Lock.AcquireAsync(rig.Acquire()));

        Assert.Empty(rig.Sleeper.Delays);
    }

    [Fact]
    public async Task AWaitBudgetPollsAndThenGivesUp()
    {
        var rig = new RigFixture();
        rig.WriteLockFile("zzz99999", "live",
            refreshedAt: rig.Clock.UtcNow.AddYears(1),
            activeAt: rig.Clock.UtcNow.AddYears(1),
            acquiredAt: rig.Clock.UtcNow.AddYears(1));

        var ex = await Assert.ThrowsAsync<RigRefusalException>(
            () => rig.Lock.AcquireAsync(new AcquireOptions { Purpose = "p", WaitSeconds = 30, PollSeconds = 5 }));

        Assert.Contains("after waiting 30s", ex.Message);
        Assert.NotEmpty(rig.Sleeper.Delays);
        Assert.All(rig.Sleeper.Delays, d => Assert.True(d <= TimeSpan.FromSeconds(5)));
        Assert.True(rig.Sleeper.Delays.Sum(static d => d.TotalSeconds) <= 30);
    }

    [Fact]
    public async Task AQueuedAcquisitionSucceedsTheInstantTheRigIsFree()
    {
        var rig = new RigFixture();
        rig.WriteLockFile("zzz99999", "live",
            refreshedAt: rig.Clock.UtcNow.AddYears(1),
            activeAt: rig.Clock.UtcNow.AddYears(1),
            acquiredAt: rig.Clock.UtcNow.AddYears(1));

        // The other session releases after the second poll.
        rig.Sleeper.OnDelay = n => { if (n == 2) rig.Fs.DeleteFile(rig.Paths.LockFile); };

        var result = await rig.Lock.AcquireAsync(new AcquireOptions { Purpose = "p", WaitSeconds = 300, PollSeconds = 5 });

        Assert.Equal(AcquireKind.Acquired, result.Kind);
        Assert.Equal(2, rig.Sleeper.Delays.Count);
        Assert.True(rig.Output.Said("queueing"));
    }

    [Fact]
    public async Task TheQueueBannerIsPrintedOnceAndThenACountdown()
    {
        var rig = new RigFixture();
        rig.WriteLockFile("zzz99999", "somebody else's test",
            refreshedAt: rig.Clock.UtcNow.AddYears(1),
            activeAt: rig.Clock.UtcNow.AddYears(1),
            acquiredAt: rig.Clock.UtcNow.AddYears(1));

        await Assert.ThrowsAsync<RigRefusalException>(
            () => rig.Lock.AcquireAsync(new AcquireOptions { Purpose = "p", WaitSeconds = 20, PollSeconds = 5 }));

        Assert.Equal(1, rig.Output.Lines.Count(static l => l.Text.Contains("queueing")));
        Assert.True(rig.Output.Lines.Count(static l => l.Text.Contains("still held by")) >= 1);
    }

    [Fact]
    public async Task APollIntervalBelowOneSecondIsClamped()
    {
        var rig = new RigFixture();
        rig.WriteLockFile("zzz99999", "live",
            refreshedAt: rig.Clock.UtcNow.AddYears(1),
            activeAt: rig.Clock.UtcNow.AddYears(1),
            acquiredAt: rig.Clock.UtcNow.AddYears(1));

        await Assert.ThrowsAsync<RigRefusalException>(
            () => rig.Lock.AcquireAsync(new AcquireOptions { Purpose = "p", WaitSeconds = 3, PollSeconds = 0 }));

        Assert.All(rig.Sleeper.Delays, d => Assert.True(d >= TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task AQueueingAgentKeepsABusyHoldersLockAlive()
    {
        // Intended: every poll runs the busy self-renew, so the holder's heartbeat stays
        // fresh while somebody waits. The ceiling is what still ends it.
        var rig = new RigFixture();
        rig.AddInstance("c1");
        rig.StartInstance("c1", 5001);
        var start = rig.Clock.UtcNow;
        rig.WriteLockFile("zzz99999", "live",
            refreshedAt: start.AddMinutes(-30),
            activeAt: start.AddMinutes(-30),
            acquiredAt: start.AddMinutes(-30));

        await Assert.ThrowsAsync<RigRefusalException>(
            () => rig.Lock.AcquireAsync(new AcquireOptions { Purpose = "p", WaitSeconds = 10, PollSeconds = 5 }));

        var after = rig.ReadLockFile()!;
        Assert.Equal("zzz99999", after.Get(LockFields.Owner));
        // The first poll renewed the heartbeat, which is why later polls found a fresh lock
        // and did not need to renew again.
        Assert.Equal(RigTime.Stamp(start), after.Get(LockFields.RefreshedAt));
        Assert.False(LockFields.IsTimerExpired(after, rig.Clock.UtcNow));
        // And the ceiling anchor never moved, so the holder still runs out of rope.
        Assert.Equal(RigTime.Stamp(start.AddMinutes(-30)), after.Get(LockFields.ActiveAt));
    }

    // ---- the restore hook --------------------------------------------------

    [Fact]
    public async Task AcquisitionRunsTheRestore()
    {
        var rig = new RigFixture(wireRestore: false);
        var restore = new FakeRestore();
        var service = new SessionLockService(
            rig.Fs, rig.Clock, rig.Sleeper, rig.Mutex, rig.Output, rig.Paths, rig.Busy, rig.Marker, rig.Launcher,
            restore, rig.MintOwnerId);

        await service.AcquireAsync(rig.Acquire());

        Assert.Single(restore.Calls);
        Assert.False(restore.Calls[0].KeepState);
        Assert.Equal("lock acquisition", restore.Calls[0].Reason);
    }

    [Fact]
    public async Task KeepStateIsPassedThroughToTheRestore()
    {
        var rig = new RigFixture(wireRestore: false);
        var restore = new FakeRestore();
        var service = new SessionLockService(
            rig.Fs, rig.Clock, rig.Sleeper, rig.Mutex, rig.Output, rig.Paths, rig.Busy, rig.Marker, rig.Launcher,
            restore, rig.MintOwnerId);

        await service.AcquireAsync(new AcquireOptions { Purpose = "p", KeepState = true });

        Assert.True(restore.Calls[0].KeepState);
    }

    [Fact]
    public async Task TheOwnerIdIsEmittedBeforeAFailingRestoreThrows()
    {
        // Ordering that matters: a caller whose reset fails still knows the id it needs to
        // unlock with.
        var rig = new RigFixture(wireRestore: false);
        var restore = new FakeRestore { Throws = new RigRefusalException(RigRefusalKind.Broken, "half reset") };
        var service = new SessionLockService(
            rig.Fs, rig.Clock, rig.Sleeper, rig.Mutex, rig.Output, rig.Paths, rig.Busy, rig.Marker, rig.Launcher,
            restore, rig.MintOwnerId);

        await Assert.ThrowsAsync<RigSessionStartException>(() => service.AcquireAsync(rig.Acquire()));

        Assert.NotNull(rig.Output.ValueOf("owner"));
        Assert.True(rig.Output.Warned("The rig state reset FAILED"));
        Assert.True(rig.Output.Warned("You DO hold the lock"));
        Assert.True(rig.LockFileExists());
    }

    [Fact]
    public async Task AFailingRestoreThrowsTheOWNERIdAndNotJustAMessage()
    {
        // The console line above is not enough on its own, and this is the difference that
        // cost a live suite three checks: a caller holding a typed result rather than a
        // terminal could not recover the id from anywhere, so it reported "the lock could not
        // be taken" while a real reservation sat on disk with nobody able to name it.
        var rig = new RigFixture(wireRestore: false);
        var restore = new FakeRestore { Throws = new RigRefusalException(RigRefusalKind.Broken, "half reset") };
        var service = new SessionLockService(
            rig.Fs, rig.Clock, rig.Sleeper, rig.Mutex, rig.Output, rig.Paths, rig.Busy, rig.Marker, rig.Launcher,
            restore, rig.MintOwnerId);

        var ex = await Assert.ThrowsAsync<RigSessionStartException>(() => service.AcquireAsync(rig.Acquire()));

        // The id in the exception is the id in the lock file, so releasing with it works.
        Assert.Equal(rig.ReadLockFile()!.GetOrEmpty(LockFields.Owner), ex.Owner);
        Assert.Contains(ex.Owner, ex.Message, StringComparison.Ordinal);
        Assert.Contains("half reset", ex.Message, StringComparison.Ordinal);

        // Still a refusal to every caller that only knows about refusals, and still the same
        // exit code, so widening the type changed nothing for the CLI.
        Assert.IsAssignableFrom<RigRefusalException>(ex);
        Assert.Equal(RigRefusalKind.Broken, ex.Kind);
        Assert.Equal(RigExitCodes.Failed, RigExitCodes.For(ex.Kind));

        // And it really is releasable with what came back.
        Assert.Equal(ReleaseStatus.Released, service.Release(ex.Owner).Status);
        Assert.False(rig.LockFileExists());
    }

    [Fact]
    public async Task AnIoFailureInTheRestoreCarriesTheOwnerToo()
    {
        // The reset throws RigRefusalException for a failed ACTION and IOException for a
        // failure underneath one. Both leave the same real lock behind, so both have to carry
        // the id; only the first one did while the type was chosen by the thrower.
        var rig = new RigFixture(wireRestore: false);
        var restore = new FakeRestore { Throws = new IOException("IO_SharingViolation_File, unity-20260816-020316.log") };
        var service = new SessionLockService(
            rig.Fs, rig.Clock, rig.Sleeper, rig.Mutex, rig.Output, rig.Paths, rig.Busy, rig.Marker, rig.Launcher,
            restore, rig.MintOwnerId);

        var ex = await Assert.ThrowsAsync<RigSessionStartException>(() => service.AcquireAsync(rig.Acquire()));

        Assert.Equal(rig.ReadLockFile()!.GetOrEmpty(LockFields.Owner), ex.Owner);
        Assert.Equal(RigRefusalKind.Broken, ex.Kind);
        Assert.Contains("unity-20260816-020316.log", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoRestoreWiredADirtyRigSaysSoRatherThanSilentlyDropping()
    {
        var rig = new RigFixture(wireRestore: false);
        var service = new SessionLockService(
            rig.Fs, rig.Clock, rig.Sleeper, rig.Mutex, rig.Output, rig.Paths, rig.Busy, rig.Marker, rig.Launcher,
            null, rig.MintOwnerId);
        rig.Marker.Write("old00001", "previous session", "Start");

        await service.AcquireAsync(rig.Acquire());

        Assert.True(rig.Output.Warned("No restore implementation is wired in"));
        Assert.True(rig.MarkerExists());
    }

    [Fact]
    public async Task ADirtyRigIsReportedAtAcquisition()
    {
        var rig = new RigFixture(wireRestore: false);
        var service = new SessionLockService(
            rig.Fs, rig.Clock, rig.Sleeper, rig.Mutex, rig.Output, rig.Paths, rig.Busy, rig.Marker, rig.Launcher,
            new FakeRestore(), rig.MintOwnerId);
        rig.Marker.Write("old00001", "previous session", "Start");

        await service.AcquireAsync(rig.Acquire());

        Assert.True(rig.Output.Warned("The rig was left DIRTY"));
        Assert.True(rig.Output.Warned("the restore runs now"));
    }

    [Fact]
    public async Task ADirtyRigWithKeepStateSaysTheLeftoversAreDeliberate()
    {
        var rig = new RigFixture(wireRestore: false);
        var service = new SessionLockService(
            rig.Fs, rig.Clock, rig.Sleeper, rig.Mutex, rig.Output, rig.Paths, rig.Busy, rig.Marker, rig.Launcher,
            new FakeRestore(), rig.MintOwnerId);
        rig.Marker.Write("old00001", "previous session", "Start");

        await service.AcquireAsync(new AcquireOptions { Purpose = "p", KeepState = true });

        Assert.True(rig.Output.Warned("ON PURPOSE"));
        Assert.True(rig.Output.Warned("The marker stays set"));
    }

    [Fact]
    public async Task ANewlineInThePurposeCannotCorruptTheLockFile()
    {
        // purpose is unescaped free text from the command line, and the format has no
        // escaping: a newline would split the line into a bogus second key on the next parse.
        var rig = new RigFixture();

        await rig.Lock.AcquireAsync(new AcquireOptions { Purpose = "line one\nowner=hijacked" });

        var fields = rig.ReadLockFile()!;
        Assert.Equal(8, fields.Count);
        Assert.NotEqual("hijacked", fields.Get(LockFields.Owner));
        Assert.DoesNotContain("\n", fields.GetOrEmpty(LockFields.Purpose));
    }
}
