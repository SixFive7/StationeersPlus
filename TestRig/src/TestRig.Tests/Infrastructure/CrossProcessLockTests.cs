using TestRig.Core.Abstractions;
using TestRig.Core.Infrastructure;
using Xunit;

namespace TestRig.Tests.Infrastructure;

/// <summary>
/// CrossProcessLock against real named mutexes.
/// </summary>
public sealed class CrossProcessLockTests
{
    private static string UniqueName() => "TestRigTests." + Guid.NewGuid().ToString("N");

    [Fact]
    public void TryEnter_AcquiresAndReports()
    {
        using var critical = new CrossProcessLock(UniqueName());

        using var holder = critical.TryEnter(TimeSpan.FromSeconds(5), out var outcome);

        Assert.NotNull(holder);
        Assert.Equal(MutexAcquisition.Acquired, outcome);
    }

    [Fact]
    public void ResolvedName_IsGlobalAndSaysSo()
    {
        // The PowerShell fell back from Global\ to Local\ per process with nothing
        // logged, so two processes could resolve to two different kernel objects and not
        // be serialised at all while both reported success.
        using var critical = new CrossProcessLock(UniqueName());

        Assert.StartsWith(@"Global\", critical.Name, StringComparison.Ordinal);
        Assert.False(critical.IsProcessLocal);
    }

    [Fact]
    public void DefaultName_IsTheRigsOwn()
    {
        using var critical = new CrossProcessLock();

        Assert.Equal(@"Global\" + CrossProcessLock.DefaultName, critical.Name);
    }

    [Fact]
    public void ConstructorRefusesANameCarryingItsOwnNamespace()
    {
        // A backslash separates the namespace from the name, so one in the caller's
        // string silently relocates the kernel object.
        Assert.Throws<ArgumentException>(() => new CrossProcessLock(@"Global\Something"));
    }

    [Fact]
    public void TwoInstancesOfTheSameNameSerialiseAgainstEachOther()
    {
        var name = UniqueName();
        using var first = new CrossProcessLock(name);
        using var second = new CrossProcessLock(name);

        using var held = first.TryEnter(TimeSpan.FromSeconds(5), out var firstOutcome);
        Assert.Equal(MutexAcquisition.Acquired, firstOutcome);

        // Held on this thread, so a second wait from another thread must time out. The
        // wait has to happen off-thread: a mutex is re-entrant for its owner.
        var blocked = RunOnOwnThread(() =>
        {
            var holder = second.TryEnter(TimeSpan.FromMilliseconds(200), out var outcome);
            holder?.Dispose();
            return outcome;
        });

        Assert.Equal(MutexAcquisition.TimedOut, blocked);
    }

    [Fact]
    public void ReleasingLetsTheNextWaiterIn()
    {
        var name = UniqueName();
        using var critical = new CrossProcessLock(name);

        critical.TryEnter(TimeSpan.FromSeconds(5), out _)!.Dispose();

        using var again = critical.TryEnter(TimeSpan.FromSeconds(5), out var outcome);
        Assert.NotNull(again);
        Assert.Equal(MutexAcquisition.Acquired, outcome);
    }

    [Fact]
    public void AbandonmentIsSurfacedRatherThanSwallowed()
    {
        // A thread that owns a mutex and dies abandons it, exactly as a killed process
        // does. AbandonedMutexException means the wait SUCCEEDED, so a holder must come
        // back: swallowing it loses the only warning the OS gives that session.lock may
        // be half written.
        var name = UniqueName();
        using var abandoner = new CrossProcessLock(name);

        RunOnOwnThread(() =>
        {
            var holder = abandoner.TryEnter(TimeSpan.FromSeconds(5), out var outcome);
            Assert.NotNull(holder);
            Assert.Equal(MutexAcquisition.Acquired, outcome);

            // Deliberately not disposed: the thread ends owning it.
            return outcome;
        });

        using var next = new CrossProcessLock(name);
        using var recovered = next.TryEnter(TimeSpan.FromSeconds(5), out var recoveredOutcome);

        Assert.Equal(MutexAcquisition.AcquiredAbandoned, recoveredOutcome);
        Assert.NotNull(recovered);
    }

    [Fact]
    public void AnAbandonedAcquisitionStillOwnsTheMutex()
    {
        var name = UniqueName();
        using var abandoner = new CrossProcessLock(name);
        RunOnOwnThread(() =>
        {
            abandoner.TryEnter(TimeSpan.FromSeconds(5), out var outcome);
            return outcome;
        });

        using var next = new CrossProcessLock(name);
        var recovered = next.TryEnter(TimeSpan.FromSeconds(5), out var outcome);
        Assert.Equal(MutexAcquisition.AcquiredAbandoned, outcome);

        // If the holder were fake, another waiter would get straight in.
        using var third = new CrossProcessLock(name);
        var blocked = RunOnOwnThread(() =>
        {
            var holder = third.TryEnter(TimeSpan.FromMilliseconds(200), out var blockedOutcome);
            holder?.Dispose();
            return blockedOutcome;
        });
        Assert.Equal(MutexAcquisition.TimedOut, blocked);

        recovered!.Dispose();
    }

    [Fact]
    public void DisposingTheHolderTwiceIsHarmless()
    {
        using var critical = new CrossProcessLock(UniqueName());

        var holder = critical.TryEnter(TimeSpan.FromSeconds(5), out _);
        Assert.NotNull(holder);
        holder.Dispose();
        holder.Dispose();
    }

    /// <summary>
    /// Runs on a dedicated thread and lets it die, so mutex ownership dies with it.
    /// </summary>
    /// <remarks>
    /// A thread pool thread would not do: it survives the work item, so the mutex would
    /// stay owned and never be abandoned.
    /// </remarks>
    private static MutexAcquisition RunOnOwnThread(Func<MutexAcquisition> work)
    {
        var result = MutexAcquisition.TimedOut;
        var thread = new Thread(() => result = work()) { IsBackground = true };
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(20));
        return result;
    }
}
