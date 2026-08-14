using System.Text.Json;
using Xunit;

namespace TestRig.Tests.Cli;

/// <summary>
/// The session lock as the CLI presents it, and the exit code every outcome maps to.
/// </summary>
/// <remarks>
/// <para>
/// <c>TESTRIG-OWNER &lt;id&gt;</c> is pinned by executing a real acquisition. The PowerShell
/// suite pinned it with two greps over the launcher's source text, and the line has never
/// once printed: <c>New-RigLock</c> returned a bare string, so <c>$outcome.Owner</c> was
/// always null and the guard around the line was always false. The playtest harness requires
/// that line by regex and throws rig-unavailable without it, then unlocks with the id it
/// never got, leaving the rig locked by a session that cannot release it.
/// </para>
/// <para>
/// Exit codes: PowerShell had 0, 1 and 2, so contention, a lapsed reservation, an unlock by a
/// non-owner and a genuinely broken rig were indistinguishable, and the playtest harness
/// collapsed every non-zero exit into "inconclusive / rig-unavailable" as a result.
/// </para>
/// </remarks>
[Collection("cli")]
public sealed class LockAndExitCodeTests(CliFixture rig)
{
    private const int Ok = 0;
    private const int Failed = 1;
    private const int Usage = 2;
    private const int Refused = 3;
    private const int LockHeldByOther = 4;
    private const int LockNotHeld = 5;

    [Fact]
    public void ASuccessfulAcquisitionPrintsTheOwnerContractLineExactlyOnceAndLast()
    {
        var home = rig.NewHome("owner");
        var result = rig.RunIn(home, "lock", "--purpose", "pin the contract line", "--keep-state");

        Assert.Equal(Ok, result.ExitCode);

        var contract = result.OutLines.Where(l => l.StartsWith("TESTRIG-OWNER ", StringComparison.Ordinal)).ToArray();
        Assert.Single(contract);
        Assert.Equal(contract[0], result.OutLines[^1]);

        var owner = contract[0]["TESTRIG-OWNER ".Length..].Trim();
        Assert.Equal(8, owner.Length);
        Assert.True(owner.All(Uri.IsHexDigit), $"'{owner}' is not eight hex characters");
    }

    [Fact]
    public void TheOwnerIsAValueUnderJsonRatherThanALineToParse()
    {
        var home = rig.NewHome("ownerjson");
        var result = rig.RunIn(home, "lock", "--purpose", "structured", "--keep-state", "--json");

        using var doc = result.Json();
        var owner = doc.RootElement.GetProperty("values").GetProperty("owner").GetString();
        Assert.False(string.IsNullOrWhiteSpace(owner));
        Assert.DoesNotContain("TESTRIG-OWNER", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusDoesNotEmitTheOwnerContractLine()
    {
        // status records an owner too, and it is frequently somebody else's. A harness reading
        // TESTRIG-OWNER out of a status report would take a lock it does not hold.
        var (home, _) = rig.LockedHome("statusowner");
        var result = rig.RunIn(home, "status");
        Assert.DoesNotContain("TESTRIG-OWNER", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void LockWithoutAPurposeIsAUsageErrorThatShowsAWorkedExample()
    {
        var result = rig.Run("lock");
        Assert.Equal(Usage, result.ExitCode);
        Assert.Contains("--purpose", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("TestRig/CLAUDE.md", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void AGatedVerbAgainstAnotherSessionsLockExitsFour()
    {
        var (home, _) = rig.LockedHome("contended");
        var result = rig.RunIn(home, "deploy", "--target", "server", "--as", "notmine1");
        Assert.Equal(LockHeldByOther, result.ExitCode);
    }

    [Fact]
    public void AGatedVerbWithNoLockAtAllExitsFive()
    {
        var result = rig.Run("deploy", "--target", "server", "--as", "nobody00");
        Assert.Equal(LockNotHeld, result.ExitCode);
    }

    [Fact]
    public void UnlockingSomebodyElsesLockExitsFourRatherThanOne()
    {
        var (home, _) = rig.LockedHome("foreignunlock");
        var result = rig.RunIn(home, "unlock", "--as", "notmine2");
        Assert.Equal(LockHeldByOther, result.ExitCode);
    }

    /// <summary>
    /// A release that released nothing exits five, not zero.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured on the shipped binary before the fix: <c>testrig unlock --as deadbeef</c> on a
    /// rig with no lock at all printed "[Unlock] No rig session lock present." and exited 0.
    /// Zero is the code a caller reads as "released", so an agent that mistyped its owner id,
    /// or whose lock had been reclaimed under it, was told its session ended cleanly. The
    /// message said otherwise and only a human reads messages.
    /// </para>
    /// <para>
    /// The cause was a per-caller <c>status == NotYours ? 4 : 0</c> written three times over,
    /// where <see cref="TestRig.Core.Session.ReleaseStatus.NotYours"/> is the one arm that can
    /// never be reached: the authorising predicates throw
    /// <c>HeldByAnotherSession</c> first. Every reachable non-release fell through to zero.
    /// </para>
    /// </remarks>
    [Fact]
    public void UnlockingWithNoLockPresentExitsFiveRatherThanReportingARelease()
    {
        var result = rig.Run("unlock", "--as", "nobody00");

        Assert.Equal(LockNotHeld, result.ExitCode);
        Assert.Contains("No rig session lock present", result.All, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same fall-through reached <c>stop --release</c>, which is how a session ends.
    /// </summary>
    /// <remarks>
    /// Only <c>--release</c> carries the code. A bare <c>stop</c> on an unlocked rig still
    /// exits 0, and <see cref="StopNeedsNoLockOfItsOwnSoAnOrphanCanAlwaysBeCleanedUp"/> pins
    /// that: orphan cleanup must always be possible, and it never claimed to release anything.
    /// </remarks>
    [Fact]
    public void StopReleaseWithNoLockPresentExitsFiveWhileABareStopStillExitsZero()
    {
        var home = rig.NewHome("stopreleasenolock");

        var released = rig.RunIn(home, "stop", "--target", "clients", "--as", "nobody00", "--release", "--json");
        using var doc = released.Json();
        Assert.Equal(LockNotHeld, released.ExitCode);
        Assert.Equal("NoLock", doc.RootElement.GetProperty("values").GetProperty("releaseStatus").GetString());

        // The teardown itself still ran and still reported it finished.
        Assert.Contains("[Stop] Done.", released.All, StringComparison.Ordinal);

        var bare = rig.RunIn(home, "stop", "--target", "clients");
        Assert.Equal(Ok, bare.ExitCode);
    }

    /// <summary>
    /// The whole "you hold no lock" family, measured together against the real binary.
    /// </summary>
    /// <remarks>
    /// Written as one case on purpose. The defect was not that any single verb was wrong; it
    /// was that four verbs answering the same question disagreed, and nothing compared them.
    /// A verb added later that forgets the code fails here rather than in a session.
    /// </remarks>
    [Fact]
    public void EveryVerbThatNeedsALockAgreesOnFiveWhenThereIsNoneAtAll()
    {
        var home = rig.NewHome("nolockfamily");

        Assert.Equal(LockNotHeld, rig.RunIn(home, "unlock", "--as", "nobody00").ExitCode);
        Assert.Equal(LockNotHeld, rig.RunIn(home, "refresh-lock", "--as", "nobody00").ExitCode);
        Assert.Equal(LockNotHeld, rig.RunIn(home, "reset", "--as", "nobody00", "--dry-run").ExitCode);
        Assert.Equal(LockNotHeld, rig.RunIn(home, "capture-baseline", "--as", "nobody00").ExitCode);
        Assert.Equal(LockNotHeld, rig.RunIn(home, "deploy", "--target", "server", "--as", "nobody00").ExitCode);
        Assert.Equal(
            LockNotHeld,
            rig.RunIn(home, "stop", "--target", "clients", "--as", "nobody00", "--release").ExitCode);
    }

    [Fact]
    public void TheOwnerCanReleaseItsOwnLock()
    {
        var (home, owner) = rig.LockedHome("release");
        var result = rig.RunIn(home, "unlock", "--as", owner, "--keep-state");
        Assert.Equal(Ok, result.ExitCode);

        // And the rig is free afterwards.
        var after = rig.RunIn(home, "status", "--json");
        using var doc = after.Json();
        Assert.Equal("None", doc.RootElement.GetProperty("values").GetProperty("lockState").GetString());
    }

    [Fact]
    public void RefreshLockWithoutAnOwnerIdSaysWhichFlagIsMissing()
    {
        var (home, _) = rig.LockedHome("refreshnoas");
        var result = rig.RunIn(home, "refresh-lock");
        Assert.Equal(Usage, result.ExitCode);
        Assert.Contains("requires --as <id>", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void RefreshLockAppliesATimerOnlyWhenItWasTyped()
    {
        // The recorded regression: 'refresh-lock -TtlMinutes 20' once tested a function's own
        // empty $PSBoundParameters and never applied the new TTL.
        var (home, owner) = rig.LockedHome("ttl");

        var untyped = rig.RunIn(home, "refresh-lock", "--as", owner, "--json");
        using (var doc = untyped.Json())
        {
            var values = doc.RootElement.GetProperty("values");
            Assert.Equal(10, values.GetProperty("ttlMinutes").GetInt32());
            Assert.Equal(60, values.GetProperty("idleCeilingMinutes").GetInt32());
        }

        var typed = rig.RunIn(home, "refresh-lock", "--as", owner, "--ttl-minutes", "25", "--json");
        using (var doc = typed.Json())
        {
            Assert.Equal(25, doc.RootElement.GetProperty("values").GetProperty("ttlMinutes").GetInt32());
        }

        // And the ceiling it did not mention keeps the value the lock already had.
        var again = rig.RunIn(home, "refresh-lock", "--as", owner, "--json");
        using (var doc = again.Json())
        {
            var values = doc.RootElement.GetProperty("values");
            Assert.Equal(25, values.GetProperty("ttlMinutes").GetInt32());
            Assert.Equal(60, values.GetProperty("idleCeilingMinutes").GetInt32());
        }
    }

    [Fact]
    public void LockForwardsATimerOnlyWhenItWasTyped()
    {
        var home = rig.NewHome("locktimers");

        var first = rig.RunIn(
            home, "lock", "--purpose", "long wait for a human", "--idle-ceiling-minutes", "240",
            "--keep-state", "--json");

        string owner;
        using (var doc = first.Json())
        {
            var values = doc.RootElement.GetProperty("values");
            Assert.Equal(240, values.GetProperty("idleCeilingMinutes").GetInt32());
            owner = values.GetProperty("owner").GetString()!;
        }

        // Re-asserting without naming the ceiling must not silently drop it back to 60. The
        // PowerShell launcher forwarded its own defaults whether or not they were typed and
        // the re-assert branch wrote them unconditionally, so exactly this happened.
        var reassert = rig.RunIn(home, "lock", "--purpose", "still waiting", "--as", owner, "--keep-state", "--json");
        using (var doc = reassert.Json())
        {
            Assert.Equal(240, doc.RootElement.GetProperty("values").GetProperty("idleCeilingMinutes").GetInt32());
        }
    }

    [Fact]
    public void LockDoesNotQueueUnlessAskedTo()
    {
        // --wait-seconds defaults to 300 globally and means 0 here: forwarding the global
        // default would turn every contended lock into a five-minute queue. This must come
        // back promptly, not in five minutes.
        var (home, _) = rig.LockedHome("nowait");
        var started = DateTime.UtcNow;
        var result = rig.RunIn(home, "lock", "--purpose", "second session", "--keep-state");
        var elapsed = DateTime.UtcNow - started;

        Assert.NotEqual(Ok, result.ExitCode);
        Assert.True(elapsed < TimeSpan.FromSeconds(30), $"a contended lock queued for {elapsed}");
    }

    [Fact]
    public void StopRefusesUnderAnotherSessionsLiveLockAndSaysWhoHoldsIt()
    {
        var (home, _) = rig.LockedHome("stopforeign");
        var result = rig.RunIn(home, "stop", "--target", "all", "--as", "notmine3");

        Assert.Equal(LockHeldByOther, result.ExitCode);
        Assert.Contains("another live session", result.All, StringComparison.Ordinal);
        Assert.Contains("--break-lock", result.All, StringComparison.Ordinal);
    }

    [Fact]
    public void StopNeedsNoLockOfItsOwnSoAnOrphanCanAlwaysBeCleanedUp()
    {
        var result = rig.Run("stop", "--target", "clients", "--json");
        using var doc = result.Json();
        Assert.NotEqual(LockNotHeld, doc.RootElement.GetProperty("exitCode").GetInt32());
    }

    /// <summary>
    /// A teardown that did not finish must not be followed by a release.
    /// </summary>
    /// <remarks>
    /// Releasing over a rig that is still up leaves instances running with no lock and no
    /// timer to save anybody.
    ///
    /// The failure is produced for real: a live process claiming an instance's
    /// <c>game.pid</c>, with no control plane answering, is classified as possibly holding a
    /// world and the teardown refuses rather than killing it. That is the one shape of failed
    /// teardown an offline suite can create, and it is the shape that matters, because it is
    /// what a wedged instance mid-boot looks like.
    /// </remarks>
    [Fact]
    public void AFailedTeardownDoesNotReleaseTheLock()
    {
        // Provisioned as a HOST, which is what makes a silent control plane a refusal: a
        // silent client cannot be holding a world and is killed after the grace period.
        var home = rig.NewHome("failedstop");
        CliFixture.ProvisionRoles(home, ("hostie", "host"));
        var owner = rig.TakeLock(home, "failedstop");
        using var standIn = CliFixture.ClaimInstanceWithALiveProcess(home, "hostie");

        var stop = rig.RunIn(home, "stop", "--target", "all", "--as", owner, "--release");
        Assert.Equal(Refused, stop.ExitCode);
        Assert.Contains("cannot be classified", stop.All, StringComparison.Ordinal);

        var after = rig.RunIn(home, "status", "--as", owner, "--json");
        using var doc = after.Json();
        Assert.Equal("Mine", doc.RootElement.GetProperty("values").GetProperty("lockState").GetString());
    }

    [Fact]
    public void StopReadsTheLockStateBeforeItTouchesAnything()
    {
        // Do not reorder: the expired-and-busy branch of the read self-renews a foreign lock
        // and reports LiveForeign, which is what makes the later release safe. The release
        // predicate has no busy term of its own.
        var (home, owner) = rig.LockedHome("stoporder");
        var result = rig.RunIn(home, "stop", "--target", "clients", "--as", owner, "--json");
        using var doc = result.Json();
        Assert.Equal("Mine", doc.RootElement.GetProperty("values").GetProperty("lockState").GetString());
    }

    [Fact]
    public void WaitNeedsNoLockButRefreshesOneYouHold()
    {
        var (home, owner) = rig.LockedHome("waitrefresh", "hostie");
        var result = rig.RunIn(home, "wait", "--target", "hostie", "--as", owner, "--wait-seconds", "1", "--json");
        using var doc = result.Json();

        // It reached the client half's barrier rather than stopping at a lock gate.
        var lines = string.Join("\n", doc.RootElement.GetProperty("lines")
            .EnumerateArray().Select(l => l.GetProperty("text").GetString()));
        Assert.Contains("[Wait] Barrier: 1 instance(s)", lines, StringComparison.Ordinal);

        // And the lock the caller holds is still theirs afterwards: a barrier legitimately
        // outlasts the TTL, so it refreshes rather than losing the rig halfway through.
        var after = rig.RunIn(home, "status", "--as", owner, "--json");
        using var status = after.Json();
        Assert.Equal("Mine", status.RootElement.GetProperty("values").GetProperty("lockState").GetString());
    }

    [Fact]
    public void AVerbThatCannotApplyExitsThreeAndAVerbThatIsMistypedExitsTwo()
    {
        Assert.Equal(Refused, rig.Run("send", "--target", "clients", "--command", "x").ExitCode);
        Assert.Equal(Usage, rig.Run("snd", "--target", "server").ExitCode);
    }

    /// <summary>
    /// "Your machine is not set up" exits one and prints plainly.
    /// </summary>
    /// <remarks>
    /// Distinct from a refusal (3), which means the command was well formed and the rig
    /// declines to do it, and from a usage error (2), which means the command itself was
    /// wrong. This one means neither: the rig would do it and cannot, because something it
    /// depends on is missing. Every message of this kind names DEV.md, and none of them dumps
    /// a stack trace.
    /// </remarks>
    [Fact]
    public void AMisconfiguredMachineExitsOneAndSaysWhatIsMissing()
    {
        var (home, owner) = rig.LockedHome("misconfigured");
        var result = rig.RunIn(home, "update-game", "--target", "server", "--as", owner);

        Assert.Equal(Failed, result.ExitCode);
        Assert.Contains("STEAMCMD_PATH", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("DEV.md", result.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void NoVerbAndHelpBothExitZero()
    {
        Assert.Equal(Ok, rig.Run().ExitCode);
        Assert.Equal(Ok, rig.Run("help").ExitCode);
    }

    [Fact]
    public void ResetNeedsTheLockAndDryRunChangesNothing()
    {
        var (home, owner) = rig.LockedHome("dryrun");
        var result = rig.RunIn(home, "reset", "--as", owner, "--dry-run", "--json");
        using var doc = result.Json();
        var values = doc.RootElement.GetProperty("values");

        Assert.Equal(Ok, doc.RootElement.GetProperty("exitCode").GetInt32());
        Assert.True(values.GetProperty("whatIf").GetBoolean());
        Assert.Equal(0, values.GetProperty("worldDeletes").GetInt32());
    }

    [Fact]
    public void CaptureBaselineNeedsTheLock()
    {
        var noLock = rig.Run("capture-baseline", "--as", "nobody00");
        Assert.Equal(LockNotHeld, noLock.ExitCode);

        // --force overrides the busy gate. Another agent's game process shows up in this
        // machine's process table whatever rig home this run points at, so without it the
        // outcome would depend on what else is running.
        var (home, owner) = rig.LockedHome("baseline");
        var held = rig.RunIn(home, "capture-baseline", "--as", owner, "--force", "--json");
        using var doc = held.Json();
        Assert.True(
            doc.RootElement.GetProperty("values").TryGetProperty("entries", out _),
            $"capture-baseline reported nothing\n{held.All}");
    }
}
