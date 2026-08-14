using TestRig.Core.Session;
using TestRig.Tests.Session.Fakes;
using Xunit;

namespace TestRig.Tests.Session;

/// <summary>
/// The gate and the heartbeat. Ported from rig-lock.tests.ps1 sections 2, 6 (ownership)
/// and the mutex assertions of section 16.
/// </summary>
public sealed class LockGateAndRefreshTests
{
    [Fact]
    public void TheGateRefusesWhenNoLockIsHeld()
    {
        var rig = new RigFixture();

        var ex = Assert.Throws<RigRefusalException>(() => rig.Lock.AssertHeld("Start", "abc12345"));

        Assert.Equal(RigRefusalKind.NoLockHeld, ex.Kind);
        Assert.Contains("[Start] No rig session lock is held", ex.Message);
        Assert.Contains("lock --purpose", ex.Message);
        Assert.Contains("TestRig/CLAUDE.md", ex.Message);
    }

    [Fact]
    public void TheGateRefusesOnALockThatExpiredWithADifferentMessage()
    {
        var rig = new RigFixture();
        rig.WriteLockFile("zzz99999",
            refreshedAt: rig.Clock.UtcNow.AddMinutes(-30),
            activeAt: rig.Clock.UtcNow.AddMinutes(-30),
            acquiredAt: rig.Clock.UtcNow.AddMinutes(-30));

        var ex = Assert.Throws<RigRefusalException>(() => rig.Lock.AssertHeld("Start", "abc12345"));

        Assert.Equal(RigRefusalKind.NoLockHeld, ex.Kind);
        Assert.Contains("No live rig session lock is held", ex.Message);
    }

    [Fact]
    public void TheGateRefusesOnAForeignLiveLockAndNamesTheHolder()
    {
        var rig = new RigFixture();
        rig.WriteLockFile("zzz99999", "somebody else's test");

        var ex = Assert.Throws<RigRefusalException>(() => rig.Lock.AssertHeld("Start", "abc12345"));

        Assert.Equal(RigRefusalKind.HeldByAnotherSession, ex.Kind);
        Assert.Contains("locked by another session", ex.Message);
        Assert.Contains("somebody else's test", ex.Message);
        Assert.Contains("owner   : zzz99999", ex.Message);
        Assert.Contains("Do NOT proceed", ex.Message);
    }

    [Fact]
    public void TheGatePassesForTheOwnerAndRefreshesBothClocksAsASideEffect()
    {
        var rig = new RigFixture();
        var owner = rig.Lease();
        rig.Clock.AdvanceMinutes(4);

        rig.Lock.AssertHeld("Start", owner);

        var fields = rig.ReadLockFile()!;
        Assert.Equal(RigTime.Stamp(rig.Clock.UtcNow), fields.Get(LockFields.RefreshedAt));
        Assert.Equal(RigTime.Stamp(rig.Clock.UtcNow), fields.Get(LockFields.ActiveAt));
    }

    [Fact]
    public void TheGateMarksTheRigDirtyOnTheFirstMutatingActionOnly()
    {
        var rig = new RigFixture();
        var owner = rig.Lease();

        rig.Lock.AssertHeld("Start", owner);
        Assert.True(rig.MarkerExists());
        Assert.True(rig.Output.Said("Rig marked dirty (first mutating action of this session: Start)"));

        rig.Output.Clear();
        rig.Lock.AssertHeld("Save", owner);
        Assert.False(rig.Output.Said("Rig marked dirty"));
    }

    [Fact]
    public void TheFirstMutationsTimestampAndReasonSurviveLaterCommands()
    {
        var rig = new RigFixture();
        var owner = rig.Lease();
        rig.Lock.AssertHeld("Start", owner);
        var markedAt = FieldText.Parse(rig.MarkerText()).Get(DirtyMarker.KeyMarkedAt);

        rig.Clock.AdvanceMinutes(20);
        rig.Lock.AssertHeld("Save", owner);

        var marker = FieldText.Parse(rig.MarkerText());
        Assert.Equal(markedAt, marker.Get(DirtyMarker.KeyMarkedAt));
        Assert.Equal("Start", marker.Get(DirtyMarker.KeyReason));
    }

    [Fact]
    public void AMarkerWriteFailureIsAWarningAndNotARefusal()
    {
        // Losing crash detection is bad; refusing every mutating command because a file
        // could not be written is worse, and would make the rig unusable rather than
        // merely unverified.
        var rig = new RigFixture();
        var owner = rig.Lease();
        rig.Fs.WriteFailures[Path.GetFullPath(rig.Paths.DirtyFile)] = "held open by something";

        rig.Lock.AssertHeld("Start", owner);

        Assert.True(rig.Output.Warned("Could not write the crash marker"));
        Assert.True(rig.Output.Warned("The action continues"));
        Assert.False(rig.MarkerExists());
    }

    [Fact]
    public void TheGateRefusesForANonOwnerEvenWithTheRightPurpose()
    {
        var rig = new RigFixture();
        rig.Lease("a real session");

        Assert.Throws<RigRefusalException>(() => rig.Lock.AssertHeld("Start", "wrong001"));
        Assert.False(rig.MarkerExists());
    }

    // ---- refresh -----------------------------------------------------------

    [Fact]
    public void RefreshRequiresACallerId()
    {
        var rig = new RigFixture();
        rig.Lease();

        var ex = Assert.Throws<RigRefusalException>(() => rig.Lock.Refresh(null));

        Assert.Equal(RigRefusalKind.Refused, ex.Kind);
        Assert.Contains("--as <id>", ex.Message);
    }

    [Fact]
    public void RefreshingWithNoLockRefuses()
    {
        var rig = new RigFixture();

        var ex = Assert.Throws<RigRefusalException>(() => rig.Lock.Refresh("abc12345"));

        Assert.Equal(RigRefusalKind.NoLockHeld, ex.Kind);
        Assert.Contains("No rig session lock to refresh", ex.Message);
    }

    [Fact]
    public void ANonOwnerCannotRefresh()
    {
        var rig = new RigFixture();
        rig.WriteLockFile("zzz99999", "their test");

        var ex = Assert.Throws<RigRefusalException>(() => rig.Lock.Refresh("abc12345"));

        Assert.Equal(RigRefusalKind.HeldByAnotherSession, ex.Kind);
        Assert.Contains("held by owner 'zzz99999'", ex.Message);
        Assert.Contains("Your reservation has lapsed", ex.Message);
        Assert.Equal("zzz99999", rig.ReadLockFile()!.Get(LockFields.Owner));
    }

    [Fact]
    public void TheOwnerCanRefreshAndBothClocksMove()
    {
        var rig = new RigFixture();
        var owner = rig.Lease();
        rig.Clock.AdvanceMinutes(7);

        var result = rig.Lock.Refresh(owner);

        Assert.Equal(owner, result.Owner);
        Assert.Equal(10, result.TtlMinutes);
        Assert.Equal(60, result.IdleCeilingMinutes);
        Assert.Contains("[RefreshLock] Refreshed (owner", result.Message);

        var fields = rig.ReadLockFile()!;
        Assert.Equal(RigTime.Stamp(rig.Clock.UtcNow), fields.Get(LockFields.RefreshedAt));
        Assert.Equal(RigTime.Stamp(rig.Clock.UtcNow), fields.Get(LockFields.ActiveAt));
    }

    [Fact]
    public void ATypedTtlIsAppliedAndAnUntypedOneLeavesTheStoredValueAlone()
    {
        var rig = new RigFixture();
        var owner = rig.Lease();

        rig.Lock.Refresh(owner, ttlMinutes: 25);
        Assert.Equal("25", rig.ReadLockFile()!.Get(LockFields.TtlMinutes));

        rig.Lock.Refresh(owner);
        Assert.Equal("25", rig.ReadLockFile()!.Get(LockFields.TtlMinutes));
    }

    [Fact]
    public void ATypedCeilingIsAppliedAndAnUntypedOneLeavesTheStoredValueAlone()
    {
        var rig = new RigFixture();
        var owner = rig.Lease();

        rig.Lock.Refresh(owner, idleCeilingMinutes: 240);
        Assert.Equal("240", rig.ReadLockFile()!.Get(LockFields.IdleCeilingMinutes));

        rig.Lock.Refresh(owner);
        Assert.Equal("240", rig.ReadLockFile()!.Get(LockFields.IdleCeilingMinutes));
    }

    [Fact]
    public void ALegacyLockWithNoCeilingFieldIsRepairedToTheDefault()
    {
        var rig = new RigFixture();
        rig.WriteLockFile("abc12345", ceiling: null);

        var result = rig.Lock.Refresh("abc12345");

        Assert.Equal(60, result.IdleCeilingMinutes);
        Assert.Equal("60", rig.ReadLockFile()!.Get(LockFields.IdleCeilingMinutes));
    }

    [Fact]
    public void RefreshPreservesUnknownFields()
    {
        var rig = new RigFixture();
        rig.WriteLockFile("abc12345", extra: [("experiment", "keep me")]);

        rig.Lock.Refresh("abc12345");

        Assert.Equal("keep me", rig.ReadLockFile()!.Get("experiment"));
    }

    [Fact]
    public void OwnershipIsMatchedCaseInsensitivelyOnRefresh()
    {
        var rig = new RigFixture();
        rig.WriteLockFile("abc12345");

        var result = rig.Lock.Refresh("ABC12345");

        Assert.Equal("abc12345", result.Owner);
    }

    // ---- the barrier refresh -----------------------------------------------

    [Fact]
    public void TheBarrierRefreshMovesBothClocksForTheOwner()
    {
        var rig = new RigFixture();
        var owner = rig.Lease();
        rig.Clock.AdvanceMinutes(9);

        rig.Lock.RefreshIfMine(owner);

        var fields = rig.ReadLockFile()!;
        Assert.Equal(RigTime.Stamp(rig.Clock.UtcNow), fields.Get(LockFields.RefreshedAt));
        Assert.Equal(RigTime.Stamp(rig.Clock.UtcNow), fields.Get(LockFields.ActiveAt));
    }

    [Fact]
    public void TheBarrierRefreshIsASilentNoOpForANonOwner()
    {
        var rig = new RigFixture();
        rig.WriteLockFile("zzz99999");
        var before = rig.LockText();

        rig.Lock.RefreshIfMine("abc12345");

        Assert.Equal(before, rig.LockText());
        Assert.Empty(rig.Output.Lines);
    }

    [Fact]
    public void TheBarrierRefreshIsASilentNoOpForAnEmptyCallerId()
    {
        var rig = new RigFixture();
        rig.WriteLockFile("zzz99999");
        var before = rig.LockText();

        rig.Lock.RefreshIfMine("");
        rig.Lock.RefreshIfMine(null);

        Assert.Equal(before, rig.LockText());
        Assert.Empty(rig.Output.Lines);
    }

    [Fact]
    public void TheBarrierRefreshDoesNotThrowWithNoLockAtAll()
    {
        var rig = new RigFixture();

        rig.Lock.RefreshIfMine("abc12345");

        Assert.False(rig.LockFileExists());
    }

    // ---- the critical section ----------------------------------------------

    [Fact]
    public void AHungHolderOfTheCriticalSectionIsAClearErrorRatherThanAWaitThatNeverEnds()
    {
        var rig = new RigFixture();
        rig.Mutex.AlwaysTimeOut = true;

        var ex = Assert.Throws<RigRefusalException>(() => rig.Lock.Refresh("abc12345"));

        Assert.Equal(RigRefusalKind.Broken, ex.Kind);
        Assert.Contains("Timed out after 15s", ex.Message);
        Assert.Contains("The lock file was NOT modified", ex.Message);
        Assert.Contains("refresh the rig lock", ex.Message);
    }

    [Fact]
    public async Task AnAbandonedCriticalSectionIsRecoveredImmediatelyAndReported()
    {
        // AbandonedMutexException means a process was killed while holding it: the wait
        // SUCCEEDED and this process now owns it. Carrying on is safe because the lock file
        // is never left half-written.
        var rig = new RigFixture();
        rig.Mutex.NextIsAbandoned = true;

        var result = await rig.Lock.AcquireAsync(rig.Acquire());

        Assert.False(string.IsNullOrEmpty(result.Owner));
        Assert.True(rig.Output.Warned("died without releasing"));
    }

    [Fact]
    public async Task AProcessLocalCriticalSectionIsReportedRatherThanSilent()
    {
        // PowerShell fell back from Global to Local per process with nothing logged, so two
        // processes could resolve differently and not be serialised at all. Measured cost
        // without a working critical section: four simultaneous winners per round, 20 rounds.
        var rig = new RigFixture();
        rig.Mutex.IsProcessLocal = true;

        await rig.Lock.AcquireAsync(rig.Acquire());

        Assert.True(rig.Output.Warned("process-local"));
        Assert.True(rig.Output.Warned("four simultaneous winners"));
    }

    [Fact]
    public void TheCriticalSectionIsNeverEnteredReEntrantly()
    {
        var rig = new RigFixture();
        var owner = rig.Lease();

        rig.Lock.AssertHeld("Start", owner);
        rig.Lock.Refresh(owner);
        rig.Lock.Release(owner);

        Assert.Equal(1, rig.Mutex.MaxConcurrentHolders);
    }

    [Fact]
    public void AHundredRefreshesInARowPreserveTheOwnerAndExactlyEightFields()
    {
        var rig = new RigFixture();
        var owner = rig.Lease();

        for (var i = 0; i < 100; i++)
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(1));
            rig.Lock.Refresh(owner);
        }

        var fields = rig.ReadLockFile()!;
        Assert.Equal(owner, fields.Get(LockFields.Owner));
        Assert.Equal(8, fields.Count);
        Assert.Equal(RigTime.Stamp(rig.Clock.UtcNow), fields.Get(LockFields.RefreshedAt));
    }
}
