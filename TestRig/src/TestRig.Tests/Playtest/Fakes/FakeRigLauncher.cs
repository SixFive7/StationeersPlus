using TestRig.Playtest.Seams;

namespace TestRig.Tests.Playtest.Fakes;

/// <summary>
///     The launcher, as five verbs a test can script.
/// </summary>
/// <remarks>
///     <b>The stop handler inspects the force flag</b>, which the PowerShell fake did not, and
///     that omission is why defect P-13 was possible: the forced-stop retry's SUCCESS path had
///     never executed in any test, because the only fake that reached the branch failed both
///     attempts. The launcher genuinely refuses to quit on top of a world whose save it could
///     not confirm, and every host check hits that refusal, so the branch runs on every real
///     run and was covered by nothing.
/// </remarks>
public sealed class FakeRigLauncher : IRigLauncher
{
    /// <summary>Every verb invoked, in order, as "verb target[ -Force]".</summary>
    public List<string> Calls { get; } = [];

    public string Owner { get; set; } = "a1b2c3";

    public bool LockSucceeds { get; set; } = true;

    public string LockMessage { get; set; } = "another session holds the rig";

    public string StateResetReport { get; set; } = "rig state: clean\nnothing to restore";

    /// <summary>Instances whose plain stop fails. A forced stop still succeeds.</summary>
    public HashSet<string> StopRefusesWithoutForce { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Instances whose stop fails even with force.</summary>
    public HashSet<string> StopAlwaysFails { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Instances whose start fails.</summary>
    public HashSet<string> StartFails { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool ReleaseSucceeds { get; set; } = true;

    public bool RefreshSucceeds { get; set; } = true;

    /// <summary>Runs when an instance is started, so a test can reset that instance's state.</summary>
    public Action<string>? OnStart { get; set; }

    public LockGrant AcquireLock(string purpose, int ttlMinutes, int waitSeconds, bool keepState = false)
    {
        // keep-state is recorded rather than ignored: it is the only way to hand a staged rig
        // to the next check, and forwarding it was missing entirely (PLAYTEST-247).
        Calls.Add($"lock {purpose} ttl={ttlMinutes} wait={waitSeconds}{(keepState ? " -KeepState" : string.Empty)}");
        return LockSucceeds
            ? LockGrant.Granted(Owner, StateResetReport)
            : LockGrant.Refused(LockMessage, StateResetReport);
    }

    public LauncherResult ReleaseLock(string owner, bool keepState = false)
    {
        Calls.Add($"unlock {owner}{(keepState ? " -KeepState" : string.Empty)}");
        return ReleaseSucceeds ? LauncherResult.Ok() : LauncherResult.Failed("the rig refused to release the lock");
    }

    public LauncherResult RefreshLock(string owner)
    {
        Calls.Add($"refresh-lock {owner}");
        return RefreshSucceeds ? LauncherResult.Ok() : LauncherResult.Failed("the lock is held by another session");
    }

    public LauncherResult StartInstance(string name, string owner)
    {
        Calls.Add($"start {name}");
        OnStart?.Invoke(name);
        return StartFails.Contains(name) ? LauncherResult.Failed($"could not start {name}", 3) : LauncherResult.Ok();
    }

    public LauncherResult StopInstance(string name, string owner, int timeoutSeconds, bool force)
    {
        Calls.Add($"stop {name}{(force ? " -Force" : string.Empty)}");

        if (StopAlwaysFails.Contains(name)) return LauncherResult.Failed($"{name} would not stop", 7);
        if (!force && StopRefusesWithoutForce.Contains(name))
            return LauncherResult.Failed("refusing to quit on a world whose save could not be confirmed", 5);

        return LauncherResult.Ok();
    }
}

/// <summary>The rig registry, as rows a test hands over.</summary>
public sealed class FakeRigRegistry : IRigRegistry
{
    private readonly List<RigInstanceRow> _rows = [];

    public FakeRigRegistry Add(
        string name,
        int port,
        string role = "client",
        string? instancesRoot = null,
        IReadOnlyList<string>? underTest = null)
    {
        _rows.Add(new RigInstanceRow(name, port, role, instancesRoot, underTest ?? []));
        return this;
    }

    public IReadOnlyList<RigInstanceRow> Rows() => _rows;
}

/// <summary>Log files backed by an in-memory map, for tests that are not about file sharing.</summary>
/// <remarks>
///     The reader's own tests use the REAL implementation over a real file, because the sharing
///     mode is the only interesting thing about it and a double in front of it would test
///     nothing.
/// </remarks>
public sealed class FakeLogFiles : ILogFiles
{
    public Dictionary<string, IReadOnlyList<string>> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Exists(string path) => Files.ContainsKey(path);

    public long Length(string path) => Files.TryGetValue(path, out var lines) ? lines.Sum(l => l.Length + 1) : 0;

    public IReadOnlyList<string> ReadAllLines(string path) => Files.TryGetValue(path, out var lines) ? lines : [];
}
