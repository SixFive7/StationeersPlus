using TestRig.Core.Session;
using TestRig.Tests.Session.Fakes;
using Xunit;

namespace TestRig.Tests.Session;

/// <summary>
/// Classification. Ported from rig-lock.tests.ps1 section 2 (state machine) and the
/// evaluation-order assertions in section 4.
/// </summary>
public sealed class LockStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private static string Ago(double minutes) => RigTime.Stamp(Now.AddMinutes(-minutes));

    private static FieldText Held(string owner, double refreshedMinutesAgo, double activeMinutesAgo, string ceiling = "60")
    {
        var fields = new FieldText();
        fields.Set(LockFields.Owner, owner);
        fields.Set(LockFields.Purpose, "somebody else's test");
        fields.Set(LockFields.AcquiredAt, Ago(activeMinutesAgo));
        fields.Set(LockFields.RefreshedAt, Ago(refreshedMinutesAgo));
        fields.Set(LockFields.ActiveAt, Ago(activeMinutesAgo));
        fields.Set(LockFields.TtlMinutes, "10");
        fields.Set(LockFields.IdleCeilingMinutes, ceiling);
        return fields;
    }

    private static BusySignal Busy(string detail = "1 client instance(s) running: c1=client") =>
        new(true, detail, false, [], [], [], false, 0);

    [Fact]
    public void NoLockFileIsNone()
    {
        var state = LockClassifier.Resolve(null, "abc12345", null, Now);

        Assert.Equal(LockState.None, state.State);
        Assert.Null(state.Lock);
        Assert.Null(state.BusyDetail);
        Assert.False(state.Renew);
        Assert.Equal(ReclaimReason.None, state.Reclaim);
    }

    [Fact]
    public void YourOwnFreshLockIsMine()
    {
        var state = LockClassifier.Resolve(Held("abc12345", 1, 1), "abc12345", null, Now);

        Assert.Equal(LockState.Mine, state.State);
        Assert.False(state.Renew);
        Assert.Equal(ReclaimReason.None, state.Reclaim);
    }

    [Fact]
    public void ALockPastItsCeilingIsStillMineToItsOwner()
    {
        // Reclaimable is not revoked. If the owner comes back before anybody else takes it,
        // its command runs, bumps active_at, and the countdown restarts. First come.
        var state = LockClassifier.Resolve(Held("abc12345", 120, 120), "abc12345", Busy(), Now);

        Assert.Equal(LockState.Mine, state.State);
        Assert.Equal(ReclaimReason.None, state.Reclaim);
        Assert.False(state.Renew);
    }

    [Fact]
    public void AFreshForeignLockIsLiveForeign()
    {
        var state = LockClassifier.Resolve(Held("zzz99999", 1, 1), "abc12345", null, Now);

        Assert.Equal(LockState.LiveForeign, state.State);
        Assert.False(state.Renew);
        Assert.Null(state.BusyDetail);
    }

    [Fact]
    public void AnExpiredForeignLockOnAnIdleRigIsDeadForeignByTtl()
    {
        var state = LockClassifier.Resolve(Held("zzz99999", 30, 30), "abc12345", BusySignal.Idle(), Now);

        Assert.Equal(LockState.DeadForeign, state.State);
        Assert.Equal(ReclaimReason.Ttl, state.Reclaim);
        Assert.Null(state.BusyDetail);
    }

    [Fact]
    public void AnExpiredForeignLockOnABusyRigStaysLiveAndRenews()
    {
        var state = LockClassifier.Resolve(Held("zzz99999", 30, 30), "abc12345", Busy(), Now);

        Assert.Equal(LockState.LiveForeign, state.State);
        Assert.True(state.Renew);
        Assert.Equal("1 client instance(s) running: c1=client", state.BusyDetail);
        Assert.Equal(ReclaimReason.None, state.Reclaim);
    }

    [Fact]
    public void PastTheCeilingIsDeadForeignEvenOnABusyRig()
    {
        var state = LockClassifier.Resolve(Held("zzz99999", 1, 120), "abc12345", Busy(), Now);

        Assert.Equal(LockState.DeadForeign, state.State);
        Assert.Equal(ReclaimReason.IdleCeiling, state.Reclaim);
        Assert.Equal("1 client instance(s) running: c1=client", state.BusyDetail);
        Assert.False(state.Renew);
    }

    [Fact]
    public void TheCeilingIsTestedBeforeTheTtlSoTheHungAgentCaseTerminates()
    {
        // The whole reason for the ordering: a busy rig self-renews refreshed_at, so a lock
        // held by a hung agent with one forgotten instance is permanently fresh by the TTL.
        // refreshed_at is FRESH here and active_at is old, which is exactly the shape the
        // self-renew produces.
        var hungAgent = Held("zzz99999", refreshedMinutesAgo: 0, activeMinutesAgo: 180);

        Assert.False(LockFields.IsTimerExpired(hungAgent, Now));
        Assert.True(LockFields.IsIdleCeilingExceeded(hungAgent, Now));

        var state = LockClassifier.Resolve(hungAgent, "abc12345", Busy(), Now);
        Assert.Equal(LockState.DeadForeign, state.State);
        Assert.Equal(ReclaimReason.IdleCeiling, state.Reclaim);
    }

    [Fact]
    public void ACommandWithNoCallerIdClassifiesAnyLockAsForeign()
    {
        var state = LockClassifier.Resolve(Held("abc12345", 1, 1), null, null, Now);

        Assert.Equal(LockState.LiveForeign, state.State);
        Assert.Equal(LockState.LiveForeign, LockClassifier.Resolve(Held("abc12345", 1, 1), "", null, Now).State);
    }

    [Fact]
    public void OwnershipIsMatchedCaseInsensitively()
    {
        Assert.Equal(LockState.Mine, LockClassifier.Resolve(Held("abc12345", 1, 1), "ABC12345", null, Now).State);
    }

    [Fact]
    public void ADeadForeignLockOnAnIdleRigCarriesNoBusyDetail()
    {
        var state = LockClassifier.Resolve(Held("zzz99999", 1, 120), "abc12345", BusySignal.Idle(), Now);

        Assert.Equal(LockState.DeadForeign, state.State);
        Assert.Null(state.BusyDetail);
    }

    // ---- whether the probe runs at all -------------------------------------

    [Fact]
    public void NoLockNeedsNoProbe()
    {
        Assert.False(LockClassifier.IsBusyProbeNeeded(null, "abc12345", Now));
    }

    [Fact]
    public void YourOwnLockNeedsNoProbe()
    {
        Assert.False(LockClassifier.IsBusyProbeNeeded(Held("abc12345", 1, 1), "abc12345", Now));
        Assert.False(LockClassifier.IsBusyProbeNeeded(Held("abc12345", 300, 300), "abc12345", Now));
    }

    [Fact]
    public void AFreshForeignLockNeedsNoProbeSoItsRefusalCannotNameWhatIsRunning()
    {
        Assert.False(LockClassifier.IsBusyProbeNeeded(Held("zzz99999", 1, 1), "abc12345", Now));
    }

    [Fact]
    public void AnExpiredForeignLockNeedsTheProbeToDecide()
    {
        Assert.True(LockClassifier.IsBusyProbeNeeded(Held("zzz99999", 30, 30), "abc12345", Now));
    }

    [Fact]
    public void ACeilingCaseNeedsTheProbeToReportEvenThoughItDoesNotDecide()
    {
        Assert.True(LockClassifier.IsBusyProbeNeeded(Held("zzz99999", 1, 120), "abc12345", Now));
    }

    // ---- the writes classification implies ---------------------------------

    [Fact]
    public void AMineStateWithoutRefreshWritesNothing()
    {
        var rig = new RigFixture();
        var owner = rig.Lease();
        var before = rig.LockText();

        var state = rig.Lock.ReadStateAndRenew(owner, refreshIfMine: false);

        Assert.Equal(LockState.Mine, state.State);
        Assert.Equal(before, rig.LockText());
    }

    [Fact]
    public void AMineStateWithRefreshMovesBothClocks()
    {
        var rig = new RigFixture();
        var owner = rig.Lease();
        var acquiredAt = rig.ReadLockFile()!.GetOrEmpty(LockFields.AcquiredAt);
        rig.Clock.AdvanceMinutes(3);

        rig.Lock.ReadStateAndRenew(owner, refreshIfMine: true);

        var after = rig.ReadLockFile()!;
        Assert.Equal(RigTime.Stamp(rig.Clock.UtcNow), after.Get(LockFields.RefreshedAt));
        Assert.Equal(RigTime.Stamp(rig.Clock.UtcNow), after.Get(LockFields.ActiveAt));
        Assert.Equal(acquiredAt, after.Get(LockFields.AcquiredAt));
    }

    [Fact]
    public void TheBusySelfRenewMovesTheHeartbeatAndNotTheCeilingAnchor()
    {
        // The entire mechanical reason active_at exists as a separate field. Anchoring the
        // ceiling on refreshed_at would let a rig with one forgotten instance renew itself
        // past the ceiling for ever.
        var rig = new RigFixture();
        rig.AddInstance("c1");
        rig.StartInstance("c1", 5001);
        rig.WriteLockFile("zzz99999",
            refreshedAt: rig.Clock.UtcNow.AddMinutes(-30),
            activeAt: rig.Clock.UtcNow.AddMinutes(-30),
            acquiredAt: rig.Clock.UtcNow.AddMinutes(-30));

        var state = rig.Lock.ReadStateAndRenew("mine0001", refreshIfMine: false);

        Assert.Equal(LockState.LiveForeign, state.State);
        Assert.True(state.Renew);

        var after = rig.ReadLockFile()!;
        Assert.Equal(RigTime.Stamp(rig.Clock.UtcNow), after.Get(LockFields.RefreshedAt));
        Assert.Equal(RigTime.Stamp(rig.Clock.UtcNow.AddMinutes(-30)), after.Get(LockFields.ActiveAt));
    }

    [Fact]
    public void ReadStateIsAGenuineQueryAndWritesNothingEvenOnTheRenewBranch()
    {
        // status was the only read-only operation in PowerShell because the one classifier
        // wrote on this branch. Reading and renewing are separate methods here, and this is
        // the read.
        var rig = new RigFixture();
        rig.AddInstance("c1");
        rig.StartInstance("c1", 5001);
        rig.WriteLockFile("zzz99999",
            refreshedAt: rig.Clock.UtcNow.AddMinutes(-30),
            activeAt: rig.Clock.UtcNow.AddMinutes(-30));
        var before = rig.LockText();

        var state = rig.Lock.ReadState("mine0001");

        Assert.Equal(LockState.LiveForeign, state.State);
        Assert.True(state.Renew);
        Assert.Equal(before, rig.LockText());
    }

    [Fact]
    public void EveryLockFileWriteHappensInsideTheCriticalSection()
    {
        // The fake critical section throws if it is ever entered while already held, and
        // counts entries. A path that read-modify-wrote the file outside it would show up
        // as a write with no entry.
        var rig = new RigFixture();
        var owner = rig.Lease();
        var entriesBefore = rig.Mutex.Entered;

        rig.Lock.ReadStateAndRenew(owner, refreshIfMine: true);
        rig.Lock.Refresh(owner);
        rig.Lock.RefreshIfMine(owner);

        Assert.True(rig.Mutex.Entered >= entriesBefore + 3);
        Assert.Equal(1, rig.Mutex.MaxConcurrentHolders);
    }
}
