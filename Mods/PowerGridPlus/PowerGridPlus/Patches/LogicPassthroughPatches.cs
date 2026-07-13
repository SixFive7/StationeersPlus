using System.Collections.Generic;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Makes bridging devices logic-transparent: a logic reader on one side of a bridge sees
    ///     devices on the other side. Passthrough is TRANSITIVE -- it follows a chain of mode-1 bridges
    ///     to its transitive closure, so a reader on one network sees every device on every network
    ///     reachable through bridges, not just the immediate neighbour. Cyclic bridge graphs are safe
    ///     (the walk folds each network in once). The set of bridge devices and their per-device gating
    ///     live in <see cref="PassthroughTopology"/>.
    ///
    ///     Mechanism: postfix on <see cref="CableNetwork.RefreshPowerAndDataDeviceLists"/>. The merge
    ///     reads the live <see cref="CableNetwork.DeviceList"/> of every network in the local network's
    ///     passthrough component and appends each entry (deduped) into the local data device list, so
    ///     the bridging devices' own <see cref="LogicType"/> slots become readable from anywhere in the
    ///     component as a side effect. Because the merge reads live device lists, it is correct as long
    ///     as the local network is refreshed after any component change; cross-network invalidation (so
    ///     a change on a far network rebuilds this network's list) is handled by the dirty-propagation
    ///     in <see cref="CableNetworkPatches"/>.
    ///
    ///     Cadence: <c>RefreshPowerAndDataDeviceLists</c> runs only when a device list was dirtied and
    ///     then read (structural changes, not per tick -- see Research/GameClasses/CableNetwork.md), so
    ///     this walk is amortized over topology edits, not paid on the frame path.
    /// </summary>
    [HarmonyPatch(typeof(CableNetwork), "RefreshPowerAndDataDeviceLists")]
    public static class LogicPassthroughPatches
    {
        // Capture the data-dirty flag before the base method clears it; the postfix only merges when
        // the data list was actually rebuilt this call, to avoid mutating a stale cached list.
        [HarmonyPrefix]
        public static void Prefix(bool ___DataDeviceListDirty, out bool __state)
        {
            __state = ___DataDeviceListDirty;
        }

        [HarmonyPostfix]
        public static void Postfix(CableNetwork __instance, bool __state, List<Device> ____dataDeviceList)
        {
            if (!__state) return;

            var reachable = PassthroughTopology.GatherReachable(__instance);
            if (reachable == null) return; // not part of a passthrough component

            // Dedupe with a HashSet (O(1) membership) rather than List.Contains (O(n)), so merging a
            // component stays linear in its total device count instead of quadratic. Headroom for large
            // bridged webs. Seed it with the local devices the vanilla refresh already placed in the list.
            var seen = new HashSet<Device>();
            for (int i = ____dataDeviceList.Count - 1; i >= 0; i--)
            {
                var local = ____dataDeviceList[i];
                if (local != null) seen.Add(local);
            }

            for (int n = 0; n < reachable.Count; n++)
            {
                var remote = reachable[n].DeviceList;
                for (int j = remote.Count - 1; j >= 0; j--)
                {
                    var remoteDevice = remote[j];
                    if (remoteDevice == null) continue;
                    if (seen.Add(remoteDevice))
                        ____dataDeviceList.Add(remoteDevice);
                }
            }
        }
    }
}
