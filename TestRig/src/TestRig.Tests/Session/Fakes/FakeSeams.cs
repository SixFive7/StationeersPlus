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
        return this;
    }

    public ProcessInfo? TryGet(int pid) => _live.TryGetValue(pid, out var info) ? info : null;

    public ProcessInfo? TryGetMatching(int pid, string expectedImageName)
    {
        if (!_live.TryGetValue(pid, out var info)) return null;
        return string.Equals(info.ImageName, expectedImageName, StringComparison.OrdinalIgnoreCase) ? info : null;
    }

    public IReadOnlyList<ProcessInfo> FindByImage(string imageName) =>
        [.. _live.Values.Where(p => string.Equals(p.ImageName, imageName, StringComparison.OrdinalIgnoreCase)).OrderBy(static p => p.Pid)];

    public Task<bool> StopAsync(int pid, TimeSpan grace, CancellationToken ct = default)
    {
        StopRequests.Add(pid);
        return Task.FromResult(_live.Remove(pid));
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
