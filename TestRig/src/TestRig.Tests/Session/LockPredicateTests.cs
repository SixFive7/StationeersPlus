using TestRig.Core.Session;
using Xunit;

namespace TestRig.Tests.Session;

/// <summary>
/// The two timers. Ported from rig-lock.tests.ps1 sections 3 (TTL) and 4 (idle watchdog).
/// </summary>
/// <remarks>
/// Two timers, two anchors, two jobs, deliberately not collapsed into one. The TTL asks
/// "is anyone USING the rig" and a busy rig self-renews it. The ceiling asks "is the OWNER
/// still there" and a busy rig cannot touch it.
/// </remarks>
public sealed class LockPredicateTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private static FieldText Lock(
        string? refreshedAt = null,
        string? activeAt = null,
        string? acquiredAt = null,
        string? ttl = "10",
        string? ceiling = "60")
    {
        var fields = new FieldText();
        fields.Set(LockFields.Owner, "abc12345");
        if (acquiredAt is not null) fields.Set(LockFields.AcquiredAt, acquiredAt);
        if (refreshedAt is not null) fields.Set(LockFields.RefreshedAt, refreshedAt);
        if (activeAt is not null) fields.Set(LockFields.ActiveAt, activeAt);
        if (ttl is not null) fields.Set(LockFields.TtlMinutes, ttl);
        if (ceiling is not null) fields.Set(LockFields.IdleCeilingMinutes, ceiling);
        return fields;
    }

    private static string Ago(double minutes) => RigTime.Stamp(Now.AddMinutes(-minutes));

    // ---- TTL ---------------------------------------------------------------

    [Fact]
    public void AFreshLockIsNotExpired()
    {
        Assert.False(LockFields.IsTimerExpired(Lock(refreshedAt: Ago(0)), Now));
        Assert.False(LockFields.IsTimerExpired(Lock(refreshedAt: Ago(5)), Now));
    }

    [Fact]
    public void ExactlyAtTheLimitIsNotExpiredBecauseTheComparisonIsStrictlyGreater()
    {
        Assert.False(LockFields.IsTimerExpired(Lock(refreshedAt: Ago(10)), Now));
        Assert.True(LockFields.IsTimerExpired(Lock(refreshedAt: Ago(10.001)), Now));
    }

    [Fact]
    public void PastTheTtlIsExpired()
    {
        Assert.True(LockFields.IsTimerExpired(Lock(refreshedAt: Ago(11)), Now));
        Assert.True(LockFields.IsTimerExpired(Lock(refreshedAt: Ago(600)), Now));
    }

    [Fact]
    public void ACustomTtlIsHonoured()
    {
        Assert.False(LockFields.IsTimerExpired(Lock(refreshedAt: Ago(15), ttl: "20"), Now));
        Assert.True(LockFields.IsTimerExpired(Lock(refreshedAt: Ago(15), ttl: "5"), Now));
    }

    [Fact]
    public void AMissingTtlUsesTheTenMinuteDefaultAndTheLockMayStillBeFresh()
    {
        Assert.False(LockFields.IsTimerExpired(Lock(refreshedAt: Ago(5), ttl: null), Now));
        Assert.True(LockFields.IsTimerExpired(Lock(refreshedAt: Ago(11), ttl: null), Now));
        Assert.Equal(10, LockFields.DefaultTtlMinutes);
    }

    [Fact]
    public void AMissingRefreshedAtIsExpiredOutright()
    {
        // The deliberate asymmetry: an absent TTL is a default, an absent heartbeat is death.
        Assert.True(LockFields.IsTimerExpired(Lock(refreshedAt: null), Now));
    }

    [Fact]
    public void AnUnparseableTtlFailsClosed()
    {
        Assert.True(LockFields.IsTimerExpired(Lock(refreshedAt: Ago(0), ttl: "banana"), Now));
        Assert.True(LockFields.IsTimerExpired(Lock(refreshedAt: Ago(0), ttl: ""), Now));
    }

    [Fact]
    public void ANegativeTtlFailsClosed()
    {
        Assert.True(LockFields.IsTimerExpired(Lock(refreshedAt: Ago(0), ttl: "-1"), Now));
    }

    [Fact]
    public void AnUnparseableRefreshedAtFailsClosed()
    {
        Assert.True(LockFields.IsTimerExpired(Lock(refreshedAt: "yesterday"), Now));
    }

    [Fact]
    public void AZeroTtlExpiresImmediatelyButNotAtTheInstantItWasWritten()
    {
        Assert.False(LockFields.IsTimerExpired(Lock(refreshedAt: Ago(0), ttl: "0"), Now));
        Assert.True(LockFields.IsTimerExpired(Lock(refreshedAt: Ago(0.5), ttl: "0"), Now));
    }

    [Fact]
    public void AFutureStampIsSimplyNotExpired()
    {
        // One machine means no clock skew, and a hand-edited future stamp is not worth a
        // special case: a negative age reads as fresh.
        Assert.False(LockFields.IsTimerExpired(Lock(refreshedAt: RigTime.Stamp(Now.AddHours(5))), Now));
    }

    [Fact]
    public void TheStoredTtlIsReadBack()
    {
        Assert.Equal(20, LockFields.GetTtl(Lock(ttl: "20")));
        Assert.Equal(10, LockFields.GetTtl(Lock(ttl: null)));
        Assert.Equal(10, LockFields.GetTtl(Lock(ttl: "rubbish")));
    }

    // ---- the ceiling -------------------------------------------------------

    [Fact]
    public void ARecentlyActiveOwnerIsInsideTheCeiling()
    {
        Assert.False(LockFields.IsIdleCeilingExceeded(Lock(activeAt: Ago(0)), Now));
        Assert.False(LockFields.IsIdleCeilingExceeded(Lock(activeAt: Ago(59)), Now));
    }

    [Fact]
    public void ExactlyAtTheCeilingIsNotExceeded()
    {
        Assert.False(LockFields.IsIdleCeilingExceeded(Lock(activeAt: Ago(60)), Now));
        Assert.True(LockFields.IsIdleCeilingExceeded(Lock(activeAt: Ago(60.001)), Now));
    }

    [Fact]
    public void ASilentOwnerPassesTheCeiling()
    {
        Assert.True(LockFields.IsIdleCeilingExceeded(Lock(activeAt: Ago(61)), Now));
        Assert.True(LockFields.IsIdleCeilingExceeded(Lock(activeAt: Ago(1440)), Now));
    }

    [Fact]
    public void ARaisedCeilingIsHonoured()
    {
        Assert.False(LockFields.IsIdleCeilingExceeded(Lock(activeAt: Ago(200), ceiling: "240"), Now));
        Assert.True(LockFields.IsIdleCeilingExceeded(Lock(activeAt: Ago(250), ceiling: "240"), Now));
    }

    [Fact]
    public void AMissingCeilingUsesSixty()
    {
        Assert.Equal(60, LockFields.GetIdleCeiling(Lock(ceiling: null)));
        Assert.False(LockFields.IsIdleCeilingExceeded(Lock(activeAt: Ago(30), ceiling: null), Now));
        Assert.True(LockFields.IsIdleCeilingExceeded(Lock(activeAt: Ago(90), ceiling: null), Now));
        Assert.Equal(60, LockFields.DefaultIdleCeilingMinutes);
    }

    [Fact]
    public void AnUnreadableCeilingFailsClosed()
    {
        Assert.Null(LockFields.GetIdleCeiling(Lock(ceiling: "soon")));
        Assert.True(LockFields.IsIdleCeilingExceeded(Lock(activeAt: Ago(0), ceiling: "soon"), Now));
        Assert.Null(LockFields.GetIdleCeiling(Lock(ceiling: "-5")));
        Assert.True(LockFields.IsIdleCeilingExceeded(Lock(activeAt: Ago(0), ceiling: "-5"), Now));
    }

    [Fact]
    public void ALockWithNoUsableAnchorAtAllIsPastTheCeiling()
    {
        var fields = new FieldText();
        fields.Set(LockFields.Owner, "abc12345");

        Assert.Null(LockFields.GetActiveAt(fields));
        Assert.True(LockFields.IsIdleCeilingExceeded(fields, Now));
    }

    [Fact]
    public void TheAnchorFallbackOrderNeverPicksAFresherField()
    {
        // active_at is the real answer.
        var full = Lock(activeAt: Ago(5), acquiredAt: Ago(90), refreshedAt: Ago(0));
        Assert.Equal(Now.AddMinutes(-5), LockFields.GetActiveAt(full));

        // Without it, acquired_at, which is older than any owner action and therefore safer
        // than refreshed_at, which the busy self-renew moves.
        var noActive = Lock(activeAt: null, acquiredAt: Ago(90), refreshedAt: Ago(0));
        Assert.Equal(Now.AddMinutes(-90), LockFields.GetActiveAt(noActive));
        Assert.True(LockFields.IsIdleCeilingExceeded(noActive, Now));

        // Only then refreshed_at.
        var onlyRefreshed = Lock(activeAt: null, acquiredAt: null, refreshedAt: Ago(3));
        Assert.Equal(Now.AddMinutes(-3), LockFields.GetActiveAt(onlyRefreshed));
    }

    [Fact]
    public void AnUnparseableActiveAtFallsThroughToTheNextKey()
    {
        var fields = Lock(activeAt: "rubbish", acquiredAt: Ago(90), refreshedAt: Ago(0));

        Assert.Equal(Now.AddMinutes(-90), LockFields.GetActiveAt(fields));
    }

    // ---- owner comparison --------------------------------------------------

    [Fact]
    public void OwnerComparisonIsCaseInsensitiveAsPowerShellsWas()
    {
        // Deliberate: a hand-typed --as ABC12345 matched a lock owned by abc12345 in
        // PowerShell, and a C# == would have silently dropped that forgiveness.
        Assert.True(LockFields.SameOwner("abc12345", "ABC12345"));
        Assert.True(LockFields.SameOwner("AbC12345", "aBc12345"));
        Assert.True(LockFields.SameOwner("abc12345", "abc12345"));
    }

    [Fact]
    public void DifferentOwnersDoNotMatch()
    {
        Assert.False(LockFields.SameOwner("abc12345", "def67890"));
    }

    [Fact]
    public void AnEmptyCallerIdNeverMatches()
    {
        Assert.False(LockFields.SameOwner("", "abc12345"));
        Assert.False(LockFields.SameOwner(null, "abc12345"));
        Assert.False(LockFields.SameOwner("abc12345", null));
        Assert.False(LockFields.SameOwner("", ""));
    }

    // ---- the stop predicate ------------------------------------------------

    [Fact]
    public void NothingToReleaseIsReleasable()
    {
        Assert.True(LockClassifier.IsReleasableOnStop(null, "abc12345", false, Now));
    }

    [Fact]
    public void YourOwnLockIsReleasableOnStop()
    {
        Assert.True(LockClassifier.IsReleasableOnStop(Lock(refreshedAt: Ago(0), activeAt: Ago(0)), "abc12345", false, Now));
    }

    [Fact]
    public void AFreshForeignLockIsNotReleasableOnStop()
    {
        Assert.False(LockClassifier.IsReleasableOnStop(Lock(refreshedAt: Ago(0), activeAt: Ago(0)), "zzz99999", false, Now));
    }

    [Fact]
    public void AnExpiredForeignLockIsReleasableOnStop()
    {
        Assert.True(LockClassifier.IsReleasableOnStop(Lock(refreshedAt: Ago(30), activeAt: Ago(30)), "zzz99999", false, Now));
    }

    [Fact]
    public void AForeignLockPastItsCeilingIsReleasableOnStop()
    {
        Assert.True(LockClassifier.IsReleasableOnStop(Lock(refreshedAt: Ago(0), activeAt: Ago(90)), "zzz99999", false, Now));
    }

    [Fact]
    public void BreakLockMakesAnyLockReleasableOnStop()
    {
        Assert.True(LockClassifier.IsReleasableOnStop(Lock(refreshedAt: Ago(0), activeAt: Ago(0)), "zzz99999", true, Now));
    }

    [Fact]
    public void ThePredicateHasNoBusyTermAtAll()
    {
        // Documented hazard: on its own it releases a foreign lock whose timer lapsed even
        // mid-test. Safety comes from the caller classifying FIRST. This assertion pins the
        // hazard so the guard elsewhere cannot be quietly removed as redundant.
        var expiredAndBusy = Lock(refreshedAt: Ago(30), activeAt: Ago(30));

        Assert.True(LockClassifier.IsReleasableOnStop(expiredAndBusy, "zzz99999", false, Now));
    }

    // ---- text helpers ------------------------------------------------------

    [Fact]
    public void AgeTextReadsInWholeMinutes()
    {
        Assert.Equal("5 min ago", LockMessages.AgeText(Lock(refreshedAt: Ago(5)), Now));
        Assert.Equal("0 min ago", LockMessages.AgeText(Lock(refreshedAt: Ago(0)), Now));
        Assert.Equal("unknown", LockMessages.AgeText(Lock(refreshedAt: "rubbish"), Now));
    }

    [Fact]
    public void IdleTextReadsInWholeMinutes()
    {
        Assert.Equal("90 min", LockMessages.IdleText(Lock(activeAt: Ago(90)), Now));

        var anchorless = new FieldText();
        anchorless.Set(LockFields.Owner, "abc12345");
        Assert.Equal("unknown", LockMessages.IdleText(anchorless, Now));
    }

    [Fact]
    public void TheCeilingCountdownSaysWhatIsLeft()
    {
        Assert.Equal("30 min left", LockMessages.IdleRemainingText(Lock(activeAt: Ago(30)), Now));
        Assert.Equal("reached", LockMessages.IdleRemainingText(Lock(activeAt: Ago(90)), Now));
        Assert.Equal(
            "unreadable, so the ceiling counts as already reached",
            LockMessages.IdleRemainingText(Lock(activeAt: Ago(30), ceiling: "soon"), Now));
    }

    [Fact]
    public void TheForeignLockBodyNamesPurposeOwnerAgeAndIdle()
    {
        var state = new LockStateSnapshot(
            LockState.LiveForeign, Lock(refreshedAt: Ago(3), activeAt: Ago(12)), null, false, ReclaimReason.None);

        var body = LockMessages.FormatForeignLock(state, Now);

        Assert.Contains("purpose :", body);
        Assert.Contains("owner   : abc12345", body);
        Assert.Contains("active  : 3 min ago", body);
        Assert.Contains("idle    : 12 min since the owner last acted", body);
        Assert.Contains("48 min left", body);
    }

    [Fact]
    public void TheForeignLockBodyAppendsBusyDetailWhenThereIsAny()
    {
        var state = new LockStateSnapshot(
            LockState.LiveForeign, Lock(refreshedAt: Ago(3), activeAt: Ago(12)),
            "1 client instance(s) running: c1=client", true, ReclaimReason.None);

        Assert.Contains("active  : 3 min ago; 1 client instance(s) running: c1=client",
            LockMessages.FormatForeignLock(state, Now));
    }
}
