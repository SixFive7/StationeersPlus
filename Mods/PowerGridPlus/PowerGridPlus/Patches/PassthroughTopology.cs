using System.Collections.Generic;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Shared topology helpers for logic passthrough. A "bridge" is a device that, when its
    ///     per-device LogicPassthroughMode is 1 (and its server toggle is on), makes the two cable
    ///     networks it joins mutually logic-transparent.
    ///
    ///     Passthrough is TRANSITIVE: a logic reader sees devices on every network reachable through a
    ///     chain of bridges (transformer -> spine -> transformer -> ...), not just the immediate
    ///     neighbour. The bridge graph may contain cycles (a ring of bridged networks, or two bridges
    ///     between the same pair); the visited set makes the walk terminate by folding each network in
    ///     exactly once.
    /// </summary>
    internal static class PassthroughTopology
    {
        /// <summary>True if any of the four passthrough server toggles is on. Cheap pre-check so the
        /// per-network walk is skipped entirely when passthrough is globally off.</summary>
        internal static bool AnyPassthroughEnabled() =>
            PassthroughSettingsSync.EffectiveTransformer
            || PassthroughSettingsSync.EffectiveApc
            || PassthroughSettingsSync.EffectiveBattery
            || PassthroughSettingsSync.EffectivePowerTransmitter;

        /// <summary>
        ///     If <paramref name="device"/> is an enabled, mode-1 bridge sitting on
        ///     <paramref name="from"/>, returns the cable network on its other side; otherwise null.
        ///     Mirrors the per-type gating (server toggle + per-device mode) of the original single-hop
        ///     merge exactly, so enabling transitivity does not change which devices bridge.
        /// </summary>
        internal static CableNetwork GetOtherSide(Device device, CableNetwork from)
        {
            switch (device)
            {
                case Transformer transformer:
                    if (!PassthroughSettingsSync.EffectiveTransformer) return null;
                    if (PassthroughModeStore.GetMode(transformer) == 0) return null;
                    return transformer.InputNetwork == from ? transformer.OutputNetwork : transformer.InputNetwork;

                case AreaPowerControl apc:
                    // APC has no per-device mode; it bridges whenever its server toggle is on.
                    if (!PassthroughSettingsSync.EffectiveApc) return null;
                    return apc.InputNetwork == from ? apc.OutputNetwork : apc.InputNetwork;

                case Battery battery:
                    if (!PassthroughSettingsSync.EffectiveBattery) return null;
                    if (PassthroughModeStore.GetMode(battery) == 0) return null;
                    return battery.InputNetwork == from ? battery.OutputNetwork : battery.InputNetwork;

                case PowerTransmitter tx:
                    if (!PassthroughSettingsSync.EffectivePowerTransmitter) return null;
                    if (PassthroughModeStore.GetMode(tx) == 0) return null;
                    return tx.LinkedReceiver?.OutputNetwork;

                case PowerReceiver rx:
                    if (!PassthroughSettingsSync.EffectivePowerTransmitter) return null;
                    if (PassthroughModeStore.GetMode(rx) == 0) return null;
                    return rx.LinkedPowerTransmitter?.InputNetwork;

                default:
                    return null;
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
                    var other = GetOtherSide(device, net);
                    if (other != null && other != net && visited.Add(other))
                        stack.Push(other);
                }
            }

            if (visited.Count == 1) return null; // start only: not part of any passthrough component
            visited.Remove(start);
            var result = new List<CableNetwork>(visited);
            result.Sort((a, b) => a.ReferenceId.CompareTo(b.ReferenceId));
            return result;
        }

        /// <summary>
        ///     The two cable networks a bridging device joins, regardless of its current mode (a
        ///     transformer / battery's input and output sides; a transmitter / receiver and its linked
        ///     partner's far side). Yields only non-null networks. Used to dirty + refresh exactly the
        ///     networks whose merged device list changes when this device's passthrough mode is written,
        ///     for both the 0 -> 1 and 1 -> 0 directions (mode is not consulted here on purpose).
        /// </summary>
        internal static IEnumerable<CableNetwork> GetBridgeNetworks(Device device)
        {
            switch (device)
            {
                case Transformer t:
                    if (t.InputNetwork != null) yield return t.InputNetwork;
                    if (t.OutputNetwork != null) yield return t.OutputNetwork;
                    break;
                case Battery b:
                    if (b.InputNetwork != null) yield return b.InputNetwork;
                    if (b.OutputNetwork != null) yield return b.OutputNetwork;
                    break;
                case PowerTransmitter tx:
                    if (tx.InputNetwork != null) yield return tx.InputNetwork;
                    if (tx.LinkedReceiver?.OutputNetwork != null) yield return tx.LinkedReceiver.OutputNetwork;
                    break;
                case PowerReceiver rx:
                    if (rx.OutputNetwork != null) yield return rx.OutputNetwork;
                    if (rx.LinkedPowerTransmitter?.InputNetwork != null) yield return rx.LinkedPowerTransmitter.InputNetwork;
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
    }
}
