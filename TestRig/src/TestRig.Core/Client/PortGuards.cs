using TestRig.Core.Rig;
using TestRig.Core.Session;

namespace TestRig.Core.Client;

/// <summary>
/// The port refusals, both directions, symmetric.
/// </summary>
/// <remarks>
/// <para>
/// A second TCP listener on a taken port fails loudly, so the control-plane check is mostly
/// book-keeping. RakNet does not behave that way: two UDP bindings on one port COEXIST, and
/// which socket receives a datagram is decided by its destination address, not by who bound
/// first. Nothing errors, nothing warns, and the joiner ends up talking to whichever
/// binding won. The test then passes or fails against a session nobody chose, and that
/// failure is invisible from inside the game, so it has to be refused out here before
/// anything is launched (CLIENT-040).
/// </para>
/// <para>
/// CLIENT-043 fixed. The PowerShell checked the game port against the range, the reserved
/// table, peer game ports AND peer control ports, and checked the control port against
/// nothing but peer control ports. It also skipped the instance under construction on both
/// sides, so <c>create --port N --game-port N</c> was accepted on one instance: legal on the
/// wire, since they are different protocols, and ambiguous in every later reading of that
/// port. Both directions are checked here, and the two candidates are checked against each
/// other.
/// </para>
/// </remarks>
public static class PortGuards
{
    /// <summary>Refuses a control port that is out of range, reserved, or already claimed.</summary>
    public static void AssertControlPortFree(
        IReadOnlyList<InstanceEntry> registry,
        string instanceName,
        int candidate,
        int ownGamePort = 0)
    {
        AssertInRange(candidate, "--port", RigConstants.ControlPortBase);
        AssertNotReserved(candidate, "--port");

        if (ownGamePort > 0 && ownGamePort == candidate)
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                $"--port {candidate} is also this instance's --game-port. They are different protocols so both "
                + "binds would succeed, but every later reading of that port is then ambiguous: a status line, "
                + "a log entry or a netstat row cannot say which socket it means. Pick different ports.");
        }

        foreach (var peer in registry)
        {
            if (RigRegistry.SameInstance(peer.InstanceName, instanceName)) continue;

            if (peer.Port == candidate)
            {
                throw new RigRefusalException(
                    RigRefusalKind.Refused,
                    $"Port {candidate} is already used by instance '{peer.InstanceName}'. Pick a different --port.");
            }

            if (peer.GamePortOr(0) == candidate)
            {
                throw new RigRefusalException(
                    RigRefusalKind.Refused,
                    $"--port {candidate} is instance '{peer.InstanceName}' game port. They are different "
                    + "protocols so the bind would succeed, but every later reading of that port is then "
                    + "ambiguous. Pick a different --port.");
            }
        }
    }

    /// <summary>Refuses a game port that is out of range, reserved, or already claimed.</summary>
    /// <remarks>
    /// A null or empty registry is allowed on purpose: the very first create on a fresh rig
    /// has no <c>rig.json</c> at all (CLIENT-038).
    /// </remarks>
    public static void AssertGamePortFree(
        IReadOnlyList<InstanceEntry>? registry,
        string instanceName,
        int candidate,
        int ownControlPort = 0)
    {
        AssertInRange(candidate, "--game-port", RigConstants.GamePortBase);

        if (RigConstants.ReservedGamePorts.TryGetValue(candidate, out var reason))
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                $"--game-port {candidate} is {reason}. Two RakNet sockets on one port do not conflict, they "
                + "coexist and route by destination address, so a joiner would reach whichever one won and the "
                + $"test would be wrong with no error anywhere. Pick another port; the rig's own band is "
                + $"{RigConstants.GamePortBase} plus the instance index.");
        }

        if (ownControlPort > 0 && ownControlPort == candidate)
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                $"--game-port {candidate} is also this instance's --port. They are different protocols so both "
                + "binds would succeed, but every later reading of that port is then ambiguous. Pick different "
                + "ports.");
        }

        foreach (var peer in registry ?? [])
        {
            if (RigRegistry.SameInstance(peer.InstanceName, instanceName)) continue;

            if (peer.GamePortOr(0) == candidate)
            {
                throw new RigRefusalException(
                    RigRefusalKind.Refused,
                    $"--game-port {candidate} is already used by instance '{peer.InstanceName}'. Two instances "
                    + "sharing a game port coexist silently and route by destination address; pick a different "
                    + "--game-port.");
            }

            if (peer.Port == candidate)
            {
                throw new RigRefusalException(
                    RigRefusalKind.Refused,
                    $"--game-port {candidate} is instance '{peer.InstanceName}' control-plane port. They are "
                    + "different protocols so the bind would succeed, but every later reading of that port is "
                    + "then ambiguous. Pick a different --game-port.");
            }
        }
    }

    private static void AssertInRange(int candidate, string flag, int band)
    {
        if (candidate >= RigConstants.MinPort && candidate <= RigConstants.MaxPort) return;

        throw new RigRefusalException(
            RigRefusalKind.Refused,
            $"{flag} {candidate} is out of range. Use {RigConstants.MinPort}-{RigConstants.MaxPort}; the rig's "
            + $"own band is {band} plus the instance index.");
    }

    /// <summary>
    /// Refuses a reserved port on the control plane too.
    /// </summary>
    /// <remarks>
    /// The reserved entries name UDP sockets, and a TCP listener on the same number does not
    /// conflict on the wire. It is refused anyway, for the reason the whole table exists: a
    /// port that means two things is a port whose every later reading is ambiguous, and the
    /// rig's own control band is wide open.
    /// </remarks>
    private static void AssertNotReserved(int candidate, string flag)
    {
        if (!RigConstants.ReservedGamePorts.TryGetValue(candidate, out var reason)) return;

        throw new RigRefusalException(
            RigRefusalKind.Refused,
            $"{flag} {candidate} is {reason}. A TCP listener there would not conflict on the wire, but a port "
            + "that means two things makes every later reading of it ambiguous. Pick another; the rig's own "
            + $"control band is {RigConstants.ControlPortBase} plus the instance index.");
    }
}
