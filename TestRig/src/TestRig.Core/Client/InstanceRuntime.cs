using System.Globalization;
using TestRig.Contracts;

namespace TestRig.Core.Client;

/// <summary>What an instance IS, for the purposes of tearing it down safely.</summary>
public enum InstanceClass
{
    /// <summary>Its process is not running.</summary>
    Stopped,

    /// <summary>Joined to somebody else's session. Leaves first, and must disconnect cleanly.</summary>
    Joiner,

    /// <summary>At the menu, booting, or in a world nobody else is in.</summary>
    Standalone,

    /// <summary>Hosting. Outlives every client that was in its world.</summary>
    Host,

    /// <summary>
    /// Cannot be ruled out as somebody's host.
    /// </summary>
    /// <remarks>
    /// The paranoid classification, and the one that earns its keep: with no control plane
    /// it cannot be asked to save a world, so killing it would take an unsaved world with it.
    /// </remarks>
    PossiblyHost,

    /// <summary>Not yet classified. Pass 2 replaces this for every runtime it sees.</summary>
    Unknown,
}

/// <summary>
/// One instance as pass 1 found it, and as pass 2 classified it.
/// </summary>
/// <remarks>
/// Two passes on purpose. Pass 1 asks each live instance what it is. Pass 2 classifies, and
/// classification needs the WHOLE rig: an instance whose control plane does not answer is
/// only safely a joiner while nobody in the rig is joined to anything, because the moment
/// somebody is joined, the silent process is a candidate for the thing they joined to.
/// </remarks>
public sealed class InstanceRuntime
{
    public required string Name { get; init; }

    public required InstanceEntry Entry { get; init; }

    public required InstancePaths Paths { get; init; }

    /// <summary>The pid its file claims, verified. Null when there is no live claim.</summary>
    public int? ProcessId { get; init; }

    /// <summary>Whether the process is alive, by verified pid and image.</summary>
    public bool Alive { get; init; }

    /// <summary>The parsed <c>/status</c>, or null.</summary>
    public StatusResponse? Status { get; init; }

    /// <summary>
    /// Whether the control plane answered.
    /// </summary>
    /// <remarks>
    /// A separate fact from <see cref="Alive"/>, and every classification below depends on
    /// the distinction (CLIENT-145): a process that is up but silent is the dangerous case.
    /// </remarks>
    public bool Answered => Status is not null;

    /// <summary>Why the control plane did not answer, when it did not.</summary>
    public string Error { get; init; } = "";

    /// <summary>The role recorded at provision time. Advisory.</summary>
    public string ProvisionedRole { get; init; } = "";

    public int GamePort { get; init; }

    /// <summary><c>menu</c>, <c>singlePlayer</c>, <c>joinedClient</c>, <c>listenHost</c>, <c>dedicated</c>, or empty.</summary>
    public string LiveRole { get; init; } = "";

    public string Phase { get; init; } = "";

    /// <summary>Null when the field was absent, which is not the same as false.</summary>
    public bool? Hosting { get; init; }

    public int HostPort { get; init; }

    /// <summary>
    /// Other clients in this instance's session, or null when it cannot be told.
    /// </summary>
    /// <remarks>
    /// NULL AND ZERO ARE DIFFERENT and two callers branch on which it is (CLIENT-140).
    /// Collapsing null to zero turns a teardown refusal into a silent proceed.
    /// </remarks>
    public int? JoinerCount { get; init; }

    // ---- filled by pass 2 --------------------------------------------------

    public InstanceClass Class { get; set; } = InstanceClass.Unknown;

    /// <summary>A human-readable reason for the classification, which status prints verbatim.</summary>
    public string ClassSource { get; set; } = "";

    /// <summary>Whether this instance holds a world that would be lost if it were killed.</summary>
    public bool OwnsWorld { get; set; }

    /// <summary>Whether it must leave a session before anything else happens to it.</summary>
    public bool NeedsDisconnect { get; set; }

    /// <summary>The ClientId the registry recorded, as a string.</summary>
    public string RegisteredClientId => Entry.ClientIdOr("");
}

/// <summary>
/// Blocking reasons, and the stale roster entries that are worth naming but must not block.
/// </summary>
/// <param name="Reasons">Non-empty means the teardown is refused without an override.</param>
/// <param name="StaleRosterEntries">
/// Roster rows for instances that are part of this teardown and have ALREADY exited.
/// </param>
/// <remarks>
/// CLIENT-160 fixed. The PowerShell built its "about to leave" set from every runtime in
/// the teardown INCLUDING the ones already classified stopped, so a roster entry for an
/// instance that had already exited read as cleared and vanished. Reporting it as a
/// blocking reason instead would be worse: a host whose joiner crashed could never be torn
/// down. So it is reported separately, named, and does not block.
/// </remarks>
public sealed record TeardownRisk(
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> StaleRosterEntries)
{
    public bool Blocked => Reasons.Count > 0;

    public static readonly TeardownRisk None = new([], []);
}

/// <summary>The classification rules. Pure, so every branch is reachable from a test.</summary>
public static class InstanceRoles
{
    /// <summary>
    /// The live role the plugin computed, or the empty string when it cannot be told.
    /// </summary>
    /// <remarks>
    /// Prefers <c>/status.role</c>, so nothing out here re-derives it and walks into the
    /// IsClient trap: a listen host reports <c>isServer</c> TRUE and <c>isClient</c> FALSE,
    /// exactly like a dedicated server (CLIENT-131). The derivation below is only for a
    /// plugin build from before <c>role</c> existed, and it reads <c>networkRole</c> rather
    /// than <c>isClient</c> for the same reason (CLIENT-132 to CLIENT-134).
    ///
    /// A status that answers nothing useful yields the empty string rather than a guess
    /// (CLIENT-135).
    /// </remarks>
    public static string LiveRoleOf(StatusResponse? status)
    {
        if (status is null) return "";
        if (!string.IsNullOrEmpty(status.Role)) return status.Role!;

        if (string.Equals(status.NetworkRole, "Server", StringComparison.Ordinal))
        {
            return status.BatchMode == true ? "dedicated" : "listenHost";
        }
        if (string.Equals(status.NetworkRole, "Client", StringComparison.Ordinal)) return "joinedClient";
        if (string.Equals(status.Phase, "inWorld", StringComparison.Ordinal)) return "singlePlayer";
        if (string.Equals(status.Phase, "menu", StringComparison.Ordinal)) return "menu";
        return "";
    }

    /// <summary>
    /// This instance's own ClientId, as a string, preferring the lossless field.
    /// </summary>
    /// <remarks>
    /// <c>/status.localClientId</c> is emitted as a JSON NUMBER and a ClientId is above
    /// 2^53, so a value read through a double loses precision, which is exactly the failure
    /// these ids exist to detect. <c>/status.instance.clientId</c> carries the same value as
    /// text. The string wins whenever it is there.
    /// </remarks>
    public static string OwnClientId(StatusResponse? status)
    {
        if (status is null) return "";
        if (!string.IsNullOrEmpty(status.Instance?.ClientId)) return status.Instance!.ClientId!;
        return status.LocalClientId == 0 ? "" : status.LocalClientId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// How many OTHER clients are in this instance's session, or null when it cannot be told.
    /// </summary>
    /// <remarks>
    /// Only meaningful for something that owns a session: a joiner's own player count
    /// describes the HOST's session, not one of its own (CLIENT-137). The host consumes a
    /// ClientId too and appears in its own roster, so its own id is excluded (CLIENT-138).
    /// Without a roster the count falls back to <c>playersInGame - 1</c> clamped at zero
    /// (CLIENT-139), and with neither field it is null, meaning "cannot be told", never zero.
    /// </remarks>
    public static int? AttachedJoinerCount(StatusResponse? status, string liveRole)
    {
        if (status is null) return null;
        if (liveRole is not ("listenHost" or "dedicated" or "singlePlayer")) return null;

        var own = OwnClientId(status);

        if (status.ConnectedClients is { } roster)
        {
            var count = 0;
            foreach (var client in roster)
            {
                var id = client.ClientId ?? "";
                if (own.Length > 0 && string.Equals(id, own, StringComparison.Ordinal)) continue;
                count++;
            }
            return count;
        }

        if (status.PlayersInGame > 0)
        {
            var n = status.PlayersInGame - 1;
            return n < 0 ? 0 : n;
        }

        return null;
    }

    /// <summary>
    /// Pass 2: classifies every runtime at once.
    /// </summary>
    /// <param name="runtimes">
    /// THE WHOLE RIG, not the target set. <c>anyoneJoined</c> is computed across all of it
    /// (CLIENT-148), which is what makes a silent instance safe on a cold boot and paranoid
    /// the moment anybody is joined. Passing only the targets makes a silent instance look
    /// safe while an untargeted joiner is attached to it, and that is the single subtlest
    /// piece of logic on this half.
    /// </param>
    /// <remarks>
    /// An empty set is a legitimate input (CLIENT-147): a status naming an instance that is
    /// not provisioned produces one.
    /// </remarks>
    public static IReadOnlyList<InstanceRuntime> Classify(IReadOnlyList<InstanceRuntime> runtimes)
    {
        var anyoneJoined = runtimes.Any(static r => string.Equals(r.LiveRole, "joinedClient", StringComparison.Ordinal));

        foreach (var rt in runtimes)
        {
            if (!rt.Alive)
            {
                rt.Class = InstanceClass.Stopped;
                rt.ClassSource = "process not running";
                continue;
            }

            if (rt.Answered)
            {
                var reported = rt.LiveRole.Length > 0 ? rt.LiveRole : "unreported";
                rt.ClassSource = $"control plane (role={reported})";

                if (rt.LiveRole is "listenHost" or "dedicated")
                {
                    rt.Class = InstanceClass.Host;
                    rt.OwnsWorld = string.Equals(rt.Phase, "inWorld", StringComparison.Ordinal);
                }
                else if (rt.LiveRole == "singlePlayer")
                {
                    rt.Class = InstanceClass.Standalone;
                    rt.OwnsWorld = string.Equals(rt.Phase, "inWorld", StringComparison.Ordinal);
                }
                else if (rt.LiveRole == "joinedClient")
                {
                    rt.Class = InstanceClass.Joiner;
                    rt.NeedsDisconnect = true;
                }
                else if (rt.LiveRole == "menu")
                {
                    rt.Class = InstanceClass.Standalone;
                }
                else if (string.Equals(rt.Phase, "inWorld", StringComparison.Ordinal))
                {
                    // It answered, it is in a world, and it will not say whose. Nothing here
                    // can rule out that the world is its own (CLIENT-154).
                    rt.Class = InstanceClass.PossiblyHost;
                    rt.OwnsWorld = true;
                }
                else
                {
                    // Answered, not in a world: booting or loading. There is no world to lose.
                    rt.Class = InstanceClass.Standalone;
                }
                continue;
            }

            // No answer at all.
            if (string.Equals(rt.ProvisionedRole, "host", StringComparison.OrdinalIgnoreCase))
            {
                rt.Class = InstanceClass.PossiblyHost;
                rt.ClassSource = "provisioned as a host; control plane silent";
            }
            else if (anyoneJoined)
            {
                rt.Class = InstanceClass.PossiblyHost;
                rt.ClassSource = "control plane silent while another instance is joined to something, so this "
                                 + "one cannot be ruled out as its host";
            }
            else
            {
                rt.Class = InstanceClass.Joiner;
                rt.ClassSource = rt.ProvisionedRole.Length > 0
                    ? "provisioned as a client; control plane silent"
                    : "registry entry predates --role; control plane silent";
            }
        }

        return runtimes;
    }

    /// <summary>
    /// Reasons it is not safe to take this host down, or delete its world, right now.
    /// </summary>
    /// <remarks>
    /// The joiners that are part of this teardown do not count: they are about to be
    /// disconnected first, in order. What counts is anything attached that will still be
    /// there afterwards (CLIENT-161 to CLIENT-164).
    /// </remarks>
    public static TeardownRisk HostTeardownRisk(
        InstanceRuntime host,
        IReadOnlyList<InstanceRuntime> inTeardown,
        IReadOnlyList<InstanceRuntime> outside)
    {
        var reasons = new List<string>();
        var stale = new List<string>();

        // Only the ones that are still ALIVE will actually leave. A stopped member has gone
        // already, and a roster row still naming it is stale rather than cleared.
        var leaving = new HashSet<string>(
            inTeardown.Where(static r => r.Alive).Select(static r => r.RegisteredClientId).Where(static id => id.Length > 0),
            StringComparer.Ordinal);
        var departed = new HashSet<string>(
            inTeardown.Where(static r => !r.Alive).Select(static r => r.RegisteredClientId).Where(static id => id.Length > 0),
            StringComparer.Ordinal);

        var roster = host.Status?.ConnectedClients;
        if (roster is not null)
        {
            var own = OwnClientId(host.Status);
            foreach (var client in roster)
            {
                var id = client.ClientId ?? "";
                var who = string.IsNullOrEmpty(client.Username) ? id : $"{client.Username} ({id})";

                if (own.Length > 0 && string.Equals(id, own, StringComparison.Ordinal)) continue;
                if (leaving.Contains(id)) continue;

                if (departed.Contains(id))
                {
                    stale.Add($"'{host.Name}' still lists client {who}, which is part of this teardown but has "
                              + "already exited: the host is holding a peer that never said goodbye");
                    continue;
                }

                reasons.Add($"client {who} is connected to '{host.Name}' and is not part of this teardown");
            }
        }
        else if (host.JoinerCount is > 0)
        {
            // No roster in this plugin build, so the count is all there is and it cannot be
            // attributed to anyone.
            var inSet = inTeardown.Count(static r => r.Class == InstanceClass.Joiner && r.Alive);
            if (host.JoinerCount > inSet)
            {
                reasons.Add($"'{host.Name}' reports {host.JoinerCount} connected client(s) and only {inSet} of "
                            + "them are in this teardown (this build's /status carries no roster, so they cannot "
                            + "be matched by id)");
            }
        }

        foreach (var other in outside)
        {
            if (!other.Alive) continue;
            if (string.Equals(other.LiveRole, "joinedClient", StringComparison.Ordinal))
            {
                reasons.Add($"'{other.Name}' is a joined client and is not part of this teardown");
            }
            else if (!other.Answered)
            {
                reasons.Add($"'{other.Name}' is running but its control plane does not answer, so it cannot be "
                            + "ruled out as a joiner");
            }
        }

        return new TeardownRisk(reasons, stale);
    }

    /// <summary>
    /// The order a teardown runs in.
    /// </summary>
    /// <remarks>
    /// Joiners leave first, then anything holding a world of its own, then hosts, then the
    /// ones that could not be classified. The host outlives every client that was in its
    /// world, which is the whole point of ordering this at all (CLIENT-203). Registry
    /// insertion order used to decide it, which normally meant the host went FIRST and took
    /// the world down under every joiner still in it.
    /// </remarks>
    public static readonly IReadOnlyList<InstanceClass> TeardownOrder =
    [
        InstanceClass.Stopped,
        InstanceClass.Joiner,
        InstanceClass.Standalone,
        InstanceClass.Host,
        InstanceClass.PossiblyHost,
    ];

    /// <summary>Sorts a target set into teardown order, anything unrecognised last.</summary>
    public static IReadOnlyList<InstanceRuntime> InTeardownOrder(IReadOnlyList<InstanceRuntime> targets)
    {
        var sequence = new List<InstanceRuntime>(targets.Count);
        foreach (var cls in TeardownOrder) sequence.AddRange(targets.Where(r => r.Class == cls));
        sequence.AddRange(targets.Where(static r => !TeardownOrder.Contains(r.Class)));
        return sequence;
    }

    /// <summary>The name a message uses for a class.</summary>
    public static string Name(InstanceClass cls) => cls switch
    {
        InstanceClass.Stopped => "stopped",
        InstanceClass.Joiner => "joiner",
        InstanceClass.Standalone => "standalone",
        InstanceClass.Host => "host",
        InstanceClass.PossiblyHost => "possiblyHost",
        _ => "unknown",
    };
}
