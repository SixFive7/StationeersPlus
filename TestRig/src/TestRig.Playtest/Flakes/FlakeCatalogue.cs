using System.Text.Json.Nodes;
using TestRig.Contracts;
using TestRig.Playtest.Values;

namespace TestRig.Playtest.Flakes;

/// <summary>
///     The flake taxonomy: ordered detectors, first match wins.
/// </summary>
/// <remarks>
///     <para>
///     <b>Every one of these ends a check as inconclusive, never as a failure.</b> No remedy
///     anywhere can produce a fail.
///     </para>
///     <para>
///     The catalogue is an instance, not process-global state. Defect P-04: in PowerShell
///     <c>Register-PlaytestFlake</c> mutated a script-scoped array that the runner never reset
///     between checks, so a check file that registered a detector at load time permanently
///     altered the taxonomy for every later check in the run. A suite here builds one
///     catalogue and owns it.
///     </para>
/// </remarks>
public sealed class FlakeCatalogue
{
    private readonly List<FlakeDetector> _detectors;

    /// <summary>Builds the catalogue with the nine shipped detectors, in resolution order.</summary>
    public FlakeCatalogue() => _detectors = [.. Shipped()];

    /// <summary>
    ///     Detectors that threw while classifying, and were skipped.
    /// </summary>
    /// <remarks>
    ///     A broken detector may never swallow a probe, so a throw is caught, recorded here
    ///     and treated as "did not match". Silence would let one bad detector turn every
    ///     genuine flake into an unexplained inconclusive.
    /// </remarks>
    public List<string> Warnings { get; } = [];

    /// <summary>The detectors, in resolution order.</summary>
    public IReadOnlyList<FlakeDetector> Detectors => _detectors;

    /// <summary>
    ///     Adds a detector.
    /// </summary>
    /// <param name="detector">The detector.</param>
    /// <param name="before">
    ///     Insert immediately in front of the detector with this name. When the named
    ///     detector does not exist the new one is appended to the END, so a typo cannot
    ///     silently promote a detector to the front.
    /// </param>
    /// <remarks>
    ///     Without <paramref name="before"/> the detector is prepended, because one added
    ///     later is almost always more specific than the ones already there.
    /// </remarks>
    public void Register(FlakeDetector detector, string? before = null)
    {
        ArgumentNullException.ThrowIfNull(detector);

        if (string.IsNullOrEmpty(before))
        {
            _detectors.Insert(0, detector);
            return;
        }

        var at = _detectors.FindIndex(d => string.Equals(d.Name, before, StringComparison.Ordinal));
        if (at < 0) _detectors.Add(detector);
        else _detectors.Insert(at, detector);
    }

    /// <summary>Resolves a probe to the first detector that matches, or null.</summary>
    public FlakeDetector? Resolve(FlakeProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        foreach (var detector in _detectors)
        {
            bool matched;
            try
            {
                matched = detector.Test(probe);
            }
            catch (Exception ex)
            {
                Warnings.Add(
                    $"[Playtest] Flake detector '{detector.Name}' threw while classifying a {probe.Kind.ToString().ToLowerInvariant()} probe and was skipped: {ex.Message}");
                continue;
            }

            if (matched) return detector;
        }

        return null;
    }

    /// <summary>
    ///     The nine shipped detectors, in the order they resolve.
    /// </summary>
    /// <remarks>
    ///     The ordering is load-bearing and is preserved verbatim from the PowerShell
    ///     taxonomy:
    ///     <list type="bullet">
    ///     <item><c>connect-first-attempt</c> is first because it is documented behaviour
    ///     rather than a defect: a client that has just disconnected is still settling. It
    ///     therefore wins over <c>instance-dead</c> and <c>transport-error</c> for anything on
    ///     <c>/connect</c>.</item>
    ///     <item><c>launchpad-workshop-park</c> sits above <c>boot-timeout</c>, so a barrier
    ///     probe whose last status shows two or fewer plugins classifies as the park (restart)
    ///     rather than a slow boot.</item>
    ///     <item><c>control-plane-silent</c> sits above <c>instance-dead</c>, so a blocking
    ///     call's silence is explained rather than read as a dead process.</item>
    ///     <item><c>instance-dead</c> sits above <c>transport-error</c>, so a refused
    ///     connection is a restart rather than three pointless retries.</item>
    ///     </list>
    /// </remarks>
    public static IReadOnlyList<FlakeDetector> Shipped() =>
    [
        new FlakeDetector(
            "connect-first-attempt",
            "POST /connect fails on a first attempt and succeeds on a later one. Documented behaviour: the client is still settling from the previous disconnect.",
            FlakeRemedy.Retry, 3, 10,
            "TestRig/RESEARCH.md, Plugin lifecycle traps",
            probe =>
            {
                if (probe.Kind is not (ProbeKind.Action or ProbeKind.Transport)) return false;
                if (Paths.Bare(probe.Path) != Endpoints.Connect) return false;
                if (!string.IsNullOrEmpty(probe.Error)) return true;
                if (probe.Response is null) return true;
                if (string.Equals(ValueText.Render(SelectPath.Select(probe.Response, "result")), "timeout", StringComparison.OrdinalIgnoreCase)) return true;

                return probe.Response is JsonObject obj
                       && obj.TryGetPropertyValue("ok", out var ok)
                       && !ValueText.AsBoolean(ok);
            }),

        new FlakeDetector(
            "launchpad-workshop-park",
            "A failed Steam Workshop query parks StationeersLaunchPad on its own error screen forever: loadedPluginCount stuck at 2 with gameInitialized false. It clears on a restart of that instance.",
            FlakeRemedy.RestartInstance, 2, 5,
            "TestRig/RESEARCH.md, Plugin lifecycle traps",
            probe =>
            {
                var status = probe.Status;
                if (status?.LoadedPluginCount is not { } plugins) return false;
                return plugins <= 2 && status.GameInitialized != true;
            }),

        new FlakeDetector(
            "host-not-hosting",
            "POST /host answered but the host-side authority disagrees: /status.hosting is not true, or /status.role is not listenHost. NetworkServer.Host() gives up quietly after three failed binds, so the call returning proves nothing.",
            FlakeRemedy.Abort, 1, 0,
            "TestRig/MANUAL.md, Working sequences",
            probe =>
            {
                if (probe.Kind != ProbeKind.PostState) return false;
                if (Paths.Bare(probe.Path) != Endpoints.Host) return false;
                if (probe.Status is null) return true;
                return probe.Status.Hosting != true
                       || !string.Equals(probe.Status.Role, "listenHost", StringComparison.Ordinal);
            }),

        new FlakeDetector(
            "joiner-not-in-roster",
            "POST /connect answered ok but the HOST roster does not carry the joiner. The joining side reporting success is not evidence that anything joined; the server-side roster is.",
            FlakeRemedy.Abort, 1, 0,
            "TestRig/MANUAL.md, the /status fields a multiplayer test reads",
            probe => probe.Kind == ProbeKind.PostState && Paths.Bare(probe.Path) == Endpoints.Connect),

        new FlakeDetector(
            "lock-lost",
            "The rig session lock is no longer ours. The suite releases and re-takes the lock per check, so losing it to another agent mid-suite is possible and is never a mod defect.",
            FlakeRemedy.Abort, 1, 0,
            "TestRig/CLAUDE.md, The session lock covers the whole rig",
            probe => probe.Kind == ProbeKind.Lock),

        new FlakeDetector(
            "control-plane-silent",
            "The control plane did not answer while a blocking endpoint was in flight. A blocking call freezes that instance whole control plane, /ping included, so the silence is explained and is waited out rather than counted against anything.",
            FlakeRemedy.Retry, 6, 10,
            "TestRig/MANUAL.md, Flags",
            probe => probe.Kind == ProbeKind.Transport && probe.Blocking),

        // MaxAttempts is 2, not the 1 the PowerShell taxonomy declared. A remedy only runs on
        // the path between "this attempt failed" and "try again", so a detector with
        // MaxAttempts 1 throws before its remedy is ever reached: instance-dead has declared
        // a restart since the day it was written and has never once performed one. Two
        // attempts is the smallest number that makes the declared remedy real, and
        // RemedyIsReachable in the suite now holds every detector to it.
        new FlakeDetector(
            "instance-dead",
            "The control plane refused the connection with no blocking call in flight, so the process is gone or its listener died.",
            FlakeRemedy.RestartInstance, 2, 5,
            "TestRig/RESEARCH.md, Plugin lifecycle traps",
            probe => probe.Kind == ProbeKind.Transport && DeadPattern.IsMatch(probe.Error)),

        new FlakeDetector(
            "boot-timeout",
            "An instance did not reach the requested readiness stage inside the barrier, and it is not the Workshop park. Roughly 100 s from cold is normal; longer than the barrier is not.",
            FlakeRemedy.RestartInstance, 2, 5,
            "TestRig/MANUAL.md, Readiness",
            probe => probe.Kind == ProbeKind.Barrier),

        new FlakeDetector(
            "transport-error",
            "A control-plane request failed at the transport layer and nothing more specific matched.",
            FlakeRemedy.Retry, 3, 3,
            "TestRig/MANUAL.md, the endpoint catalogue",
            probe => probe.Kind == ProbeKind.Transport),
    ];

    private static readonly System.Text.RegularExpressions.Regex DeadPattern =
        new("refused|actively refused|No connection could be made|unable to connect",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
}
