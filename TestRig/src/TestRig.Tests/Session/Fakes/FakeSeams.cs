using TestRig.Core.Abstractions;

namespace TestRig.Tests.Session.Fakes;

/// <summary>A clock a test drives. Nothing in the suite sleeps for real.</summary>
public sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset? start = null) =>
        UtcNow = start ?? new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow { get; set; }

    public void Advance(TimeSpan by) => UtcNow += by;

    public void AdvanceMinutes(double minutes) => UtcNow += TimeSpan.FromMinutes(minutes);
}

/// <summary>
/// A sleeper that advances the clock instead of waiting, and records what it was asked for.
/// </summary>
/// <remarks>
/// Advancing is what makes a queue test honest: the deadline really does arrive, and it
/// arrives because time moved, not because the assertion was written to tolerate either
/// answer.
/// </remarks>
public sealed class FakeSleeper : ISleeper
{
    private readonly FakeClock _clock;

    public FakeSleeper(FakeClock clock) => _clock = clock;

    public List<TimeSpan> Delays { get; } = [];

    /// <summary>Runs before each delay, so a test can release a lock mid-wait.</summary>
    public Action<int>? OnDelay { get; set; }

    public Task DelayAsync(TimeSpan duration, CancellationToken ct = default)
    {
        Delays.Add(duration);
        OnDelay?.Invoke(Delays.Count);
        _clock.UtcNow += duration;
        return Task.CompletedTask;
    }
}

/// <summary>A scriptable process table: pids, images and start times a test controls.</summary>
public sealed class FakeProcessTable : IProcessTable
{
    private readonly Dictionary<int, ProcessInfo> _live = [];

    public List<int> StopRequests { get; } = [];

    public FakeProcessTable Add(int pid, string image, DateTimeOffset? startedAt = null)
    {
        _live[pid] = new ProcessInfo(pid, image, startedAt ?? new DateTimeOffset(2026, 8, 14, 11, 0, 0, TimeSpan.Zero));
        return this;
    }

    public FakeProcessTable Kill(int pid)
    {
        _live.Remove(pid);
        _lingering.Remove(pid);
        return this;
    }

    /// <remarks>
    /// A LINGERING process is reported as absent here, deliberately: the real
    /// <see cref="ProcessInfo"/> carries a start time, and a process that is unwinding
    /// refuses to hand one over, so the real implementation returns no match for it too.
    /// </remarks>
    public ProcessInfo? TryGet(int pid)
    {
        if (_lingering.ContainsKey(pid)) return null;
        return _live.TryGetValue(pid, out var info) ? info : null;
    }

    public ProcessInfo? TryGetMatching(int pid, string expectedImageName)
    {
        if (!_live.TryGetValue(pid, out var info)) return null;
        return string.Equals(info.ImageName, expectedImageName, StringComparison.OrdinalIgnoreCase) ? info : null;
    }

    /// <summary>
    /// Whether the pid is still listed, INCLUDING a killed one that has not gone yet.
    /// </summary>
    /// <remarks>
    /// The real one answers from <c>HasExited</c>, which a terminating process still serves,
    /// where <see cref="TryGet"/> reports it as absent because its start time cannot be read.
    /// Modelling that asymmetry is the whole point: a lingering process is VISIBLE here and
    /// INVISIBLE to TryGet, which is exactly what let a teardown declare a live process gone.
    /// Each observation counts the linger down, whichever query made it, because the real
    /// process does eventually go.
    /// </remarks>
    public bool IsRunning(int pid)
    {
        var listed = _live.ContainsKey(pid) || _lingering.ContainsKey(pid);
        Settle(pid);
        return listed;
    }

    public IReadOnlyList<ProcessInfo> FindByImage(string imageName) =>
        [.. _live.Values.Where(p => string.Equals(p.ImageName, imageName, StringComparison.OrdinalIgnoreCase)).OrderBy(static p => p.Pid)];

    /// <summary>
    /// Pids that stay in the table for N further lookups after being killed.
    /// </summary>
    /// <remarks>
    /// The real thing does this: a game client force-killed mid-frame takes seconds to unwind,
    /// and Windows is not obliged to have reaped it when the terminate call returns. Without a
    /// way to model it, the only test that could see the teardown returning too early would be
    /// a real rig session, which is where it WAS found. A process that is still listed after
    /// its pid file has been deleted is an untracked game process, and an untracked game
    /// process is one of the three conditions the state restore refuses on.
    /// </remarks>
    public Dictionary<int, int> LingerAfterStop { get; } = [];

    /// <summary>Models a kill whose process takes <paramref name="lookups"/> further polls to go.</summary>
    public FakeProcessTable LingersWhenKilled(int pid, int lookups)
    {
        LingerAfterStop[pid] = lookups;
        return this;
    }

    /// <summary>
    /// Puts a pid straight into the lingering state: describable no longer, listed still.
    /// </summary>
    /// <remarks>
    /// The state a process is in after a CLEAN quit while it unwinds, and the one that
    /// produced the defect. The teardown's own liveness check reads a start time and so
    /// reports the process gone; the reset's orphan scan, moments later, reports it running.
    /// A test cannot reach this through StopAsync, because the clean path never calls it.
    /// </remarks>
    public FakeProcessTable LingersInvisiblyAfterQuit(int pid, int lookups)
    {
        _lingering[pid] = lookups;
        _live.Remove(pid);
        return this;
    }

    public Task<bool> StopAsync(int pid, TimeSpan grace, CancellationToken ct = default)
    {
        StopRequests.Add(pid);

        if (LingerAfterStop.TryGetValue(pid, out var lookups) && lookups > 0)
        {
            _lingering[pid] = lookups;
            _live.Remove(pid);

            // The terminate request was accepted, which is all the real one reports when it is
            // given no grace. The process is still in the table.
            return Task.FromResult(false);
        }

        return Task.FromResult(_live.Remove(pid));
    }

    private readonly Dictionary<int, int> _lingering = [];

    /// <summary>Counts down a lingering process, dropping it once its lookups are used up.</summary>
    private void Settle(int pid)
    {
        if (!_lingering.TryGetValue(pid, out var left)) return;

        if (left <= 1)
        {
            _lingering.Remove(pid);
            return;
        }

        _lingering[pid] = left - 1;
    }
}

/// <summary>The machine's boot identity, injectable because no test can reboot the machine.</summary>
public sealed class FakeBootIdentity : IBootIdentity
{
    public string BootId { get; set; } = "boot:2026-08-14T06:00:00Z";

    public string GetBootId() => BootId;

    /// <summary>Simulates a reboot: the id changes, so every marker predates it.</summary>
    public void Reboot(string id = "boot:2026-08-15T06:00:00Z") => BootId = id;
}

/// <summary>
/// A cross-process critical section that actually serialises, and can be made to fail.
/// </summary>
/// <remarks>
/// It counts concurrent holders and asserts the count never exceeds one, so a code path
/// that reads or writes the lock file outside the section is caught by construction rather
/// than by a reviewer noticing. It also records how many times the section was entered,
/// which is how a test proves the release really is three phases.
/// </remarks>
public sealed class FakeCrossProcessLock : ICrossProcessLock
{
    private int _holders;

    public string Name { get; set; } = "Global\\StationeersTestRig.SessionLock.TEST";

    public bool IsProcessLocal { get; set; }

    /// <summary>Makes every attempt time out, as a hung holder would.</summary>
    public bool AlwaysTimeOut { get; set; }

    /// <summary>Reports the next acquisition as abandoned, as a killed holder would.</summary>
    public bool NextIsAbandoned { get; set; }

    public int Entered { get; private set; }

    public int MaxConcurrentHolders { get; private set; }

    public IDisposable? TryEnter(TimeSpan timeout, out MutexAcquisition outcome)
    {
        if (AlwaysTimeOut)
        {
            outcome = MutexAcquisition.TimedOut;
            return null;
        }

        outcome = NextIsAbandoned ? MutexAcquisition.AcquiredAbandoned : MutexAcquisition.Acquired;
        NextIsAbandoned = false;

        Entered++;
        _holders++;
        if (_holders > MaxConcurrentHolders) MaxConcurrentHolders = _holders;
        if (_holders > 1) throw new InvalidOperationException("The critical section was entered re-entrantly.");

        return new Release(this);
    }

    private sealed class Release : IDisposable
    {
        private readonly FakeCrossProcessLock _owner;
        private bool _done;

        public Release(FakeCrossProcessLock owner) => _owner = owner;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _owner._holders--;
        }
    }
}

/// <summary>
/// A registry a test writes into, so the shared-state snapshot is exercisable offline.
/// </summary>
/// <remarks>
/// Deliberately has no writer on the <see cref="IRegistry"/> side either: values are seeded
/// through <see cref="Set"/>, which is a TEST affordance and not part of the interface. The
/// rig must never be able to put this state back.
/// </remarks>
public sealed class FakeRegistry : IRegistry
{
    private readonly Dictionary<string, Dictionary<string, string>> _keys =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Keys that exist but cannot be read, as an unelevated read of a locked key is.</summary>
    public HashSet<string> Unreadable { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FakeRegistry Set(string keyPath, string name, string value)
    {
        if (!_keys.TryGetValue(keyPath, out var values))
        {
            values = new Dictionary<string, string>(StringComparer.Ordinal);
            _keys[keyPath] = values;
        }
        values[name] = value;
        return this;
    }

    public FakeRegistry Remove(string keyPath, string name)
    {
        if (_keys.TryGetValue(keyPath, out var values)) values.Remove(name);
        return this;
    }

    public IReadOnlyList<KeyValuePair<string, string>>? TryReadValues(string keyPath)
    {
        if (Unreadable.Contains(keyPath)) return null;
        if (!_keys.TryGetValue(keyPath, out var values)) return null;

        return [.. values.OrderBy(static v => v.Key, StringComparer.Ordinal)];
    }
}

/// <summary>Captures everything the rig says, as structure.</summary>
public sealed class RecordingOutput : IOutput
{
    public List<(OutputLevel Level, string Text)> Lines { get; } = [];

    public List<(string Key, object? Value)> Values { get; } = [];

    public List<Refusal> Refusals { get; } = [];

    public void Line(OutputLevel level, string text) => Lines.Add((level, text));

    public void Value(string key, object? value) => Values.Add((key, value));

    public void Refusal(Refusal refusal) => Refusals.Add(refusal);

    public string? ValueOf(string key) =>
        Values.Where(v => v.Key == key).Select(static v => v.Value?.ToString()).LastOrDefault();

    public bool Said(string fragment) =>
        Lines.Any(l => l.Text.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    public bool Warned(string fragment) =>
        Lines.Any(l => l.Level == OutputLevel.Warning && l.Text.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    public string All => string.Join("\n", Lines.Select(static l => $"{l.Level}: {l.Text}"));

    public void Clear()
    {
        Lines.Clear();
        Values.Clear();
        Refusals.Clear();
    }
}

/// <summary>A restore that records what it was asked to do, and can be made to fail.</summary>
public sealed class FakeRestore : TestRig.Core.Session.IRigRestore
{
    public List<(bool KeepState, string Reason)> Calls { get; } = [];

    public Exception? Throws { get; set; }

    public TestRig.Core.Session.ResetRun Result { get; set; } =
        new(false, "", false, false, [], [], null!);

    public TestRig.Core.Session.ResetRun Restore(bool keepState, string reason)
    {
        Calls.Add((keepState, reason));
        if (Throws is not null) throw Throws;
        return Result;
    }
}
