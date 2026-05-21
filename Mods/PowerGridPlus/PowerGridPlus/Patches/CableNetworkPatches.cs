// Derived from Re-Volt by Sukasa (https://github.com/sukasa/revolt), MIT License (Copyright (c) 2025 Sukasa).

using System;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using PowerGridPlus.Power;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Swaps every <see cref="CableNetwork"/>'s <see cref="PowerTick"/> for a <see cref="PowerGridTick"/>
    ///     at construction time, forwards device-list invalidations so the tick rebuilds its caches, and
    ///     propagates those invalidations across a logic-passthrough component so transitive passthrough
    ///     stays correct when a far network changes.
    /// </summary>
    [HarmonyPatch(typeof(CableNetwork))]
    public static class CableNetworkPatches
    {
        private static readonly FieldInfo TickField = typeof(CableNetwork).GetField(nameof(CableNetwork.PowerTick));

        // Re-entrancy guard for the transitive passthrough dirty-propagation below. The propagation
        // loop calls DirtyPowerAndDataDeviceLists on the other component members, which re-enters this
        // same postfix; the guard stops that from cascading into another full component walk. Thread-
        // static because DirtyPowerAndDataDeviceLists can be reached from the worker power thread and
        // the main thread, so the guard must be per-thread.
        [ThreadStatic] private static bool _propagatingPassthroughDirty;

        [HarmonyPostfix, HarmonyPatch(nameof(CableNetwork.DirtyPowerAndDataDeviceLists))]
        public static void DirtyPowerAndDataDeviceListsPatch(CableNetwork __instance)
        {
            if (__instance.PowerTick is PowerGridTick tick)
                tick.IsDirty = true;

            // Transitive logic passthrough invalidation. A network's merged data device list includes
            // the devices of every network in its passthrough component (see LogicPassthroughPatches).
            // The game only dirties the network that directly changed, so when this network's membership
            // changes the other component members would keep serving a stale merged list. Dirty them
            // here; the merge reads live DeviceLists, so it is correct once the dependents are dirtied.
            if (_propagatingPassthroughDirty) return;       // inside a propagation already: just mark the tick, do not cascade
            if (!GameManager.RunSimulation) return;          // skip world-load / join churn: every network is dirtied then anyway
            if (!PassthroughTopology.AnyPassthroughEnabled()) return;

            var reachable = PassthroughTopology.GatherReachable(__instance);
            if (reachable == null) return;                   // not part of a passthrough component: nothing to invalidate

            try
            {
                _propagatingPassthroughDirty = true;
                for (int i = 0; i < reachable.Count; i++)
                    reachable[i].DirtyPowerAndDataDeviceLists(); // dirties BOTH flags; DataDeviceList.get checks the power flag (vanilla quirk)
            }
            finally
            {
                _propagatingPassthroughDirty = false;
            }
        }

        [HarmonyPostfix, HarmonyPatch(MethodType.Constructor, new Type[0])]
        public static void Constructor_None(CableNetwork __instance) => Inject(__instance);

        [HarmonyPostfix, HarmonyPatch(MethodType.Constructor, new Type[] { typeof(Cable) })]
        public static void Constructor_Cable(CableNetwork __instance) => Inject(__instance);

        [HarmonyPostfix, HarmonyPatch(MethodType.Constructor, new Type[] { typeof(long) })]
        public static void Constructor_Long(CableNetwork __instance) => Inject(__instance);

        private static void Inject(CableNetwork network) => TickField.SetValue(network, new PowerGridTick());
    }
}
