using TestRig.Core.Abstractions;

namespace TestRig.Core.Infrastructure;

/// <summary>
/// The named-mutex critical section that serialises rig lock acquisition.
/// </summary>
/// <remarks>
/// What this protects: two agents on this machine both reading session.lock, both seeing
/// it free, and both writing themselves in as the owner. Measured without a working
/// critical section: four simultaneous winners per round across 20 rounds.
///
/// Two things the PowerShell got wrong and this type makes impossible.
///
/// The namespace fallback was silent. It tried Global\ and dropped to Local\ per process
/// with nothing logged, so two processes could resolve to two different kernel objects
/// and not be serialised against each other at all, while both reported success. Here the
/// resolved name and the fallback are properties, so a caller can print them and a test
/// can assert on them.
///
/// Abandonment was swallowed. AbandonedMutexException means the wait SUCCEEDED and this
/// thread now owns the mutex, but that the previous holder died without releasing, so
/// whatever it was in the middle of writing may be half written. Catching it and
/// returning a plain success loses the only warning the OS gives that session.lock is
/// possibly torn. It is surfaced as <see cref="MutexAcquisition.AcquiredAbandoned"/>.
/// </remarks>
public sealed class CrossProcessLock : ICrossProcessLock, IDisposable
{
    /// <summary>
    /// The rig's own critical section name.
    /// </summary>
    /// <remarks>
    /// One rig per machine, so one name. It is not keyed on the repository path on
    /// purpose: two clones of this repository would still contend for the same single
    /// game install and the same per-Windows-user Unity state, so they must serialise
    /// against each other.
    /// </remarks>
    public const string DefaultName = "StationeersPlus.TestRig.Session";

    private readonly Mutex _mutex;

    public CrossProcessLock() : this(DefaultName)
    {
    }

    public CrossProcessLock(string baseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);

        // A backslash in a mutex name separates the namespace from the name, so one in
        // the caller's string would silently relocate the object.
        if (baseName.Contains('\\'))
        {
            throw new ArgumentException(
                $"A cross-process lock name may not contain a backslash: '{baseName}'. The Global\\ or " +
                "Local\\ prefix is chosen here, not by the caller.",
                nameof(baseName));
        }

        try
        {
            _mutex = new Mutex(initiallyOwned: false, @"Global\" + baseName);
            Name = @"Global\" + baseName;
            IsProcessLocal = false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException or WaitHandleCannotBeOpenedException)
        {
            // Creating in the global namespace needs SeCreateGlobalPrivilege, which an
            // interactive user normally has and a service or container user may not.
            // Falling back keeps the rig usable within one session, and IsProcessLocal
            // says so out loud rather than pretending the guarantee still holds.
            _mutex = new Mutex(initiallyOwned: false, @"Local\" + baseName);
            Name = @"Local\" + baseName;
            IsProcessLocal = true;
        }
    }

    public string Name { get; }

    public bool IsProcessLocal { get; }

    /// <remarks>
    /// Returns null only when the wait timed out. An abandoned mutex is an acquisition:
    /// the holder must still be disposed or the next waiter inherits the abandonment.
    /// </remarks>
    public IDisposable? TryEnter(TimeSpan timeout, out MutexAcquisition outcome)
    {
        var wait = Clamp(timeout);

        bool entered;
        try
        {
            entered = _mutex.WaitOne(wait, exitContext: false);
            outcome = entered ? MutexAcquisition.Acquired : MutexAcquisition.TimedOut;
        }
        catch (AbandonedMutexException)
        {
            // The wait succeeded. This thread owns the mutex now.
            entered = true;
            outcome = MutexAcquisition.AcquiredAbandoned;
        }

        return entered ? new Holder(_mutex) : null;
    }

    public void Dispose() => _mutex.Dispose();

    private static TimeSpan Clamp(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero) return TimeSpan.Zero;
        return timeout.TotalMilliseconds > int.MaxValue ? Timeout.InfiniteTimeSpan : timeout;
    }

    /// <summary>
    /// Ownership of the critical section, released on dispose.
    /// </summary>
    private sealed class Holder : IDisposable
    {
        private readonly Mutex _mutex;
        private readonly int _ownerThreadId;
        private bool _released;

        public Holder(Mutex mutex)
        {
            _mutex = mutex;
            _ownerThreadId = Environment.CurrentManagedThreadId;
        }

        public void Dispose()
        {
            if (_released) return;
            _released = true;

            // A Win32 mutex is owned by the thread that waited on it, so releasing it
            // from another one throws ApplicationException with a message about an
            // unsynchronized block of code, which says nothing about the actual mistake.
            // Anything that acquires this must complete on the thread that acquired it.
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                throw new InvalidOperationException(
                    $"The rig's cross-process lock was acquired on thread {_ownerThreadId} and released on " +
                    $"thread {Environment.CurrentManagedThreadId}. A named mutex is owned by the thread that " +
                    "waited on it, so the critical section must be entered and left on one thread. Do not await " +
                    "across it.");
            }

            _mutex.ReleaseMutex();
        }
    }
}
