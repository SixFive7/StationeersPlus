using TestRig.Core.Abstractions;
using TestRig.Core.Rig;
using TestRig.Core.Session;

namespace TestRig.Core.Client;

/// <summary>
/// The client half: N isolated Stationeers game clients, provisioned, driven and torn down.
/// </summary>
/// <remarks>
/// <para>
/// The boundary between this and the in-game plugin is process creation. This type owns
/// everything OUTSIDE a game process, and everything that must keep working when a process
/// is dead or wedged: building an instance tree, the isolated Win32 desktop, starting and
/// stopping, pid files, and fanning one request across the rig. The plugin owns everything
/// inside a process, which is everything needing the Unity main thread or the game's own
/// types. There is no third category.
/// </para>
/// <para>
/// An instance is a hard-linked copy of the developer's real install on the same NTFS
/// volume, so it costs a few megabytes instead of seven gigabytes. Nothing the game or a
/// mod writes to is ever a hard link, because a hard link shares the file data and a write
/// would reach back into the developer's install. The source install is treated as strictly
/// read-only.
/// </para>
/// </remarks>
public sealed partial class ClientHalf
{
    private readonly IFileSystem _fs;
    private readonly IProcessTable _processes;
    private readonly IClock _clock;
    private readonly ISleeper _sleeper;
    private readonly IOutput _output;
    private readonly RigPaths _paths;
    private readonly RigEnvironment _env;
    private readonly ClientLayout _layout;
    private readonly RigRegistry _registry;
    private readonly ControlPlane _control;
    private readonly ModBuilds _mods;
    private readonly IInstanceLauncher _launcher;
    private readonly SessionLockService _lock;
    private readonly DirtyMarker _marker;

    public ClientHalf(
        IFileSystem fs,
        IProcessTable processes,
        IClock clock,
        ISleeper sleeper,
        IOutput output,
        RigPaths paths,
        RigEnvironment env,
        ClientLayout layout,
        RigRegistry registry,
        ControlPlane control,
        ModBuilds mods,
        IInstanceLauncher launcher,
        SessionLockService sessionLock,
        DirtyMarker marker)
    {
        _fs = fs;
        _processes = processes;
        _clock = clock;
        _sleeper = sleeper;
        _output = output;
        _paths = paths;
        _env = env;
        _layout = layout;
        _registry = registry;
        _control = control;
        _mods = mods;
        _launcher = launcher;
        _lock = sessionLock;
        _marker = marker;
    }

    /// <summary>The instance registry, for a caller that has to resolve a target first.</summary>
    public RigRegistry Registry => _registry;

    /// <summary>Path resolution, for a caller that needs a tree location.</summary>
    public ClientLayout Layout => _layout;

    // ---- the gate ----------------------------------------------------------

    /// <summary>
    /// Every action that changes rig state goes through one gate.
    /// </summary>
    /// <remarks>
    /// Without it, a stop of the whole rig tears down another agent's live test with no
    /// trace, a remove deletes an instance's save root out from under a run, and two
    /// concurrent creates read the registry before either writes it, pick the same free
    /// index, and hand two instances one ClientId (CLIENT-013). That last one is also
    /// guarded inside <see cref="RigRegistry.Update{T}"/>, because a lock assertion is a
    /// point-in-time check and cannot serialise a read-modify-write on its own.
    /// </remarks>
    private void AssertGate(string action, string? callerId) =>
        _lock.AssertHeld(action, callerId, "testrig");

    private void Say(string text) => _output.Line(OutputLevel.Info, text);

    private void Detail(string text) => _output.Line(OutputLevel.Detail, text);

    private void Warn(string text) => _output.Line(OutputLevel.Warning, text);

    // ---- pass 1: what is each instance right now ---------------------------

    /// <summary>
    /// Process liveness plus one <c>/status</c>, with no interpretation beyond the live role.
    /// </summary>
    public async Task<InstanceRuntime> RuntimeAsync(InstanceEntry entry, CancellationToken ct = default)
    {
        var paths = _layout.PathsFor(entry.InstanceName, entry);
        var live = PidFiles.LiveProcess(_fs, _processes, paths.PidFile, [RigConstants.ClientImageName]);
        var claimed = PidFiles.Read(_fs, paths.PidFile);

        Contracts.StatusResponse? status = null;
        var error = "";

        if (live is not null)
        {
            // Only when the process is alive: a status read against a dead instance costs
            // the full timeout and can only ever answer "no".
            (status, error) = await _control.StatusAsync(entry.Port, 5, ct).ConfigureAwait(false);
        }

        var liveRole = InstanceRoles.LiveRoleOf(status);

        return new InstanceRuntime
        {
            Name = entry.InstanceName,
            Entry = entry,
            Paths = paths,
            ProcessId = live?.Pid ?? claimed,
            Alive = live is not null,
            Status = status,
            Error = error,
            ProvisionedRole = entry.Role ?? "",
            GamePort = entry.GamePortOr(0),
            LiveRole = liveRole,
            Phase = status?.Phase ?? "",
            Hosting = status is null ? null : status.Hosting,
            HostPort = status?.HostPort ?? 0,
            JoinerCount = InstanceRoles.AttachedJoinerCount(status, liveRole),
        };
    }

    /// <summary>
    /// Pass 1 over a set of entries, probing them CONCURRENTLY.
    /// </summary>
    /// <remarks>
    /// CLIENT-142 fixed. Classification legitimately needs the whole rig, but the PowerShell
    /// probed serially at five seconds each, so one wedged instance outside a teardown cost
    /// five seconds before anything happened and four wedged instances cost twenty. The
    /// whole-rig view is the part that matters; the serial probing was not.
    /// </remarks>
    public async Task<IReadOnlyList<InstanceRuntime>> RuntimesAsync(
        IReadOnlyList<InstanceEntry> entries, CancellationToken ct = default)
    {
        if (entries.Count == 0) return [];
        if (entries.Count == 1) return [await RuntimeAsync(entries[0], ct).ConfigureAwait(false)];

        var probes = entries.Select(e => RuntimeAsync(e, ct)).ToArray();
        return await Task.WhenAll(probes).ConfigureAwait(false);
    }

    /// <summary>Pass 1 over the WHOLE registry, then pass 2. The input every teardown needs.</summary>
    public async Task<IReadOnlyList<InstanceRuntime>> ClassifyRigAsync(CancellationToken ct = default)
    {
        var runtimes = await RuntimesAsync(_registry.Read(), ct).ConfigureAwait(false);
        return InstanceRoles.Classify(runtimes);
    }

    /// <summary>How long to poll between liveness checks during a teardown.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>How long a readiness barrier waits between probes.</summary>
    private static readonly TimeSpan BarrierInterval = TimeSpan.FromSeconds(2);
}
