using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using HarmonyLib;
using Objects.Rockets;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Shared topology helpers for logic passthrough. A "bridge" is a device that, when its
    ///     per-device LogicPassthroughMode is 1, makes the two cable networks it joins mutually
    ///     logic-transparent.
    ///
    ///     Passthrough is TRANSITIVE: a logic reader sees devices on every network reachable through a
    ///     chain of bridges (transformer -> spine -> transformer -> ...), not just the immediate
    ///     neighbour. The bridge graph may contain cycles (a ring of bridged networks, or two bridges
    ///     between the same pair); the visited set makes the walk terminate by folding each network in
    ///     exactly once.
    /// </summary>
    internal static class PassthroughTopology
    {
        /// <summary>
        ///     True if <paramref name="device"/> is a mode-1 logic-passthrough bridge. The per-device
        ///     LogicPassthroughMode is the only runtime gate; the gating is identical to the original
        ///     single-hop merge, so all-port bridging does not change WHICH devices bridge, only how
        ///     many of each bridge's own networks are folded together.
        /// </summary>
        internal static bool IsEnabledBridge(Device device)
        {
            switch (device)
            {
                case Transformer transformer:
                    return PassthroughModeStore.GetMode(transformer) != 0;
                case AreaPowerControl apc:
                    return PassthroughModeStore.GetMode(apc) != 0;
                case Battery battery:
                    return PassthroughModeStore.GetMode(battery) != 0;
                case PowerTransmitter tx:
                    return PassthroughModeStore.GetMode(tx) != 0;
                case PowerReceiver rx:
                    return PassthroughModeStore.GetMode(rx) != 0;
                case RocketPowerUmbilicalMale _:
                case RocketPowerUmbilicalFemale _:
                    // A docked umbilical pair bridges only while connected: gate on a non-null partner so an
                    // undocked half (the partner is severed on launch, restored on land) carries no logic,
                    // per the connected / disconnected requirement.
                    return PassthroughModeStore.GetMode(device) != 0
                        && GetUmbilicalPartner((ElectricalInputOutput)device) != null;
                default:
                    return false;
            }
        }

        /// <summary>
        ///     Returns every cable network reachable from <paramref name="start"/> through a chain of
        ///     enabled mode-1 bridges, EXCLUDING <paramref name="start"/> itself, ordered by
        ///     <see cref="CableNetwork.ReferenceId"/> so the resulting merge order is identical on every
        ///     multiplayer peer. Returns null when <paramref name="start"/> bridges to nothing (the
        ///     overwhelmingly common case: a network with no mode-1 bridge device on it).
        ///
        ///     Cycle-safe: <paramref name="start"/> seeds the visited set, and every network is pushed
        ///     at most once, so a ring of bridged networks terminates instead of looping.
        /// </summary>
        internal static List<CableNetwork> GatherReachable(CableNetwork start)
        {
            var visited = new HashSet<CableNetwork> { start };
            var stack = new Stack<CableNetwork>();
            stack.Push(start);

            while (stack.Count > 0)
            {
                var net = stack.Pop();
                var devices = net.DeviceList;
                for (int i = devices.Count - 1; i >= 0; i--)
                {
                    var device = devices[i];
                    if (device == null) continue;
                    if (!IsEnabledBridge(device)) continue;
                    // All-port bridging: fold in EVERY cable network this enabled bridge connects (power
                    // input, power output, a dedicated data-port network, and a linked dish's far
                    // networks), not just the opposite power side. So a reader on ANY of the device's
                    // ports -- including a separate Data port (rocket transformer, station / large
                    // battery, wireless dish) -- sees across the device.
                    foreach (var other in GetBridgeNetworks(device))
                    {
                        if (other != null && other != net && visited.Add(other))
                            stack.Push(other);
                    }
                }
            }

            if (visited.Count == 1) return null; // start only: not part of any passthrough component
            visited.Remove(start);
            var result = new List<CableNetwork>(visited);
            result.Sort((a, b) => a.ReferenceId.CompareTo(b.ReferenceId));
            return result;
        }

        /// <summary>
        ///     Every cable network a bridging device joins, regardless of its current mode: its power
        ///     input and output sides, its dedicated data-port network (<see cref="Device.DataCableNetwork"/>,
        ///     distinct from the power sides only when the device has a separate Data connector -- rocket
        ///     transformer, station / large battery, wireless dish), and for a linked transmitter /
        ///     receiver the partner's far power and data networks. Yields only non-null networks; callers
        ///     dedupe (a `PowerAndData` connector makes the data network equal to a power side, yielded
        ///     twice). Used by the reachability walk (gated by <see cref="IsEnabledBridge"/>) and to dirty
        ///     + refresh exactly the networks whose merged device list changes when this device's
        ///     passthrough mode is written, for both the 0 -> 1 and 1 -> 0 directions (mode is not
        ///     consulted here on purpose).
        /// </summary>
        internal static IEnumerable<CableNetwork> GetBridgeNetworks(Device device)
        {
            switch (device)
            {
                case Transformer t:
                    if (t.InputNetwork != null) yield return t.InputNetwork;
                    if (t.OutputNetwork != null) yield return t.OutputNetwork;
                    if (t.DataCableNetwork != null) yield return t.DataCableNetwork;
                    break;
                case Battery b:
                    if (b.InputNetwork != null) yield return b.InputNetwork;
                    if (b.OutputNetwork != null) yield return b.OutputNetwork;
                    if (b.DataCableNetwork != null) yield return b.DataCableNetwork;
                    break;
                case AreaPowerControl apc:
                    if (apc.InputNetwork != null) yield return apc.InputNetwork;
                    if (apc.OutputNetwork != null) yield return apc.OutputNetwork;
                    if (apc.DataCableNetwork != null) yield return apc.DataCableNetwork;
                    break;
                case PowerTransmitter tx:
                    if (tx.InputNetwork != null) yield return tx.InputNetwork;
                    if (tx.DataCableNetwork != null) yield return tx.DataCableNetwork;
                    if (tx.LinkedReceiver?.OutputNetwork != null) yield return tx.LinkedReceiver.OutputNetwork;
                    if (tx.LinkedReceiver?.DataCableNetwork != null) yield return tx.LinkedReceiver.DataCableNetwork;
                    break;
                case PowerReceiver rx:
                    if (rx.OutputNetwork != null) yield return rx.OutputNetwork;
                    if (rx.DataCableNetwork != null) yield return rx.DataCableNetwork;
                    if (rx.LinkedPowerTransmitter?.InputNetwork != null) yield return rx.LinkedPowerTransmitter.InputNetwork;
                    if (rx.LinkedPowerTransmitter?.DataCableNetwork != null) yield return rx.LinkedPowerTransmitter.DataCableNetwork;
                    break;
                case RocketPowerUmbilicalMale _:
                case RocketPowerUmbilicalFemale _:
                    foreach (var net in UmbilicalBridgeNetworks((ElectricalInputOutput)device)) yield return net;
                    break;
            }
        }

        /// <summary>
        ///     Dirties both sides' power and data device lists for a bridging device. Dirties BOTH flags
        ///     because CableNetwork.DataDeviceList.get checks the power flag (vanilla quirk; see
        ///     Research/GameClasses/CableNetwork.md). Replaces the per-patch inline dirty logic.
        /// </summary>
        internal static void DirtyBridgeNetworks(Device device)
        {
            foreach (var net in GetBridgeNetworks(device))
                net.DirtyPowerAndDataDeviceLists();
        }

        // Reflection accessors for the private RocketPowerUmbilical*._partnerUmbilical field (IUmbilical
        // exposes no public partner getter). Resolved once per type; readonly so the lookup is thread-safe
        // on the power-tick worker. Each half's field is typed to its partner class, both ElectricalInputOutput.
        private static readonly FieldInfo _malePartnerField =
            AccessTools.Field(typeof(RocketPowerUmbilicalMale), "_partnerUmbilical");
        private static readonly FieldInfo _femalePartnerField =
            AccessTools.Field(typeof(RocketPowerUmbilicalFemale), "_partnerUmbilical");

        /// <summary>
        ///     The docked partner of a rocket power umbilical half, or null when undocked (or when the
        ///     device is not an umbilical). "Connected" for passthrough purposes is exactly a non-null
        ///     partner: the game severs it on launch and restores it on land.
        /// </summary>
        internal static ElectricalInputOutput GetUmbilicalPartner(ElectricalInputOutput umbilical)
        {
            switch (umbilical)
            {
                case RocketPowerUmbilicalMale _:
                    return _malePartnerField?.GetValue(umbilical) as ElectricalInputOutput;
                case RocketPowerUmbilicalFemale _:
                    return _femalePartnerField?.GetValue(umbilical) as ElectricalInputOutput;
                default:
                    return null;
            }
        }

        // Own power-in / power-out / data networks plus the docked partner's, so a docked pair forms one
        // logic-transparent span. The male carries Power-in + a separate Data port; each socket carries
        // Power-out only; the null guards drop the connectors a given half lacks.
        private static IEnumerable<CableNetwork> UmbilicalBridgeNetworks(ElectricalInputOutput umbilical)
        {
            if (umbilical.InputNetwork != null) yield return umbilical.InputNetwork;
            if (umbilical.OutputNetwork != null) yield return umbilical.OutputNetwork;
            if (umbilical.DataCableNetwork != null) yield return umbilical.DataCableNetwork;
            var partner = GetUmbilicalPartner(umbilical);
            if (partner != null)
            {
                if (partner.InputNetwork != null) yield return partner.InputNetwork;
                if (partner.OutputNetwork != null) yield return partner.OutputNetwork;
                if (partner.DataCableNetwork != null) yield return partner.DataCableNetwork;
            }
        }
    }
}
