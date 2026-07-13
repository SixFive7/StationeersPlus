// Derived from Re-Volt by Sukasa (https://github.com/sukasa/revolt), MIT License (Copyright (c) 2025 Sukasa).

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Assets.Scripts;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using HarmonyLib;
using StationeersPlus.Shared;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Propagates device-list invalidations across a logic-passthrough component so transitive
    ///     passthrough stays correct when a far network changes. Power simulation is untouched
    ///     here: the whole power tick runs in AtomicElectricityTickPatch, and the vanilla
    ///     per-network PowerTick trio is never called.
    /// </summary>
    [HarmonyPatch(typeof(CableNetwork))]
    public static class CableNetworkPatches
    {
        // Re-entrancy guard for the transitive passthrough dirty-propagation below. The propagation
        // loop calls DirtyPowerAndDataDeviceLists on the other component members, which re-enters this
        // same postfix; the guard stops that from cascading into another full component walk. Thread-
        // static because DirtyPowerAndDataDeviceLists can be reached from the worker power thread and
        // the main thread, so the guard must be per-thread.
        [ThreadStatic] private static bool _propagatingPassthroughDirty;

        [HarmonyPostfix, HarmonyPatch(nameof(CableNetwork.DirtyPowerAndDataDeviceLists))]
        public static void DirtyPowerAndDataDeviceListsPatch(CableNetwork __instance)
        {
            // Transitive logic passthrough invalidation. A network's merged data device list includes
            // the devices of every network in its passthrough component (see LogicPassthroughPatches).
            // The game only dirties the network that directly changed, so when this network's membership
            // changes the other component members would keep serving a stale merged list. Dirty them
            // here; the merge reads live DeviceLists, so it is correct once the dependents are dirtied.
            if (_propagatingPassthroughDirty) return;       // inside a propagation already: just mark the tick, do not cascade
            if (!GameManager.RunSimulation) return;          // skip world-load / join churn: every network is dirtied then anyway

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

            // B: refresh consumers (motherboard dropdowns, IC-housing / sensor caches) on the
            // transitively-reachable far networks whose merged data device lists just changed. Vanilla
            // only cascades the network a device physically joined, so a device built on one bridged
            // network never refreshes a motherboard on another network in the same passthrough
            // component; this covers that. The device's OWN two networks (the flip case) are handled by
            // ScheduleCascadeForDevice from the write paths. Diff-guarded, marshaled to the main thread.
            ScheduleCascade(reachable);
        }

        // --- B: device-list-change refresh cascade -------------------------------------------------
        //
        // A passthrough merge changes WHAT is in a network's DataDeviceList without going through the
        // vanilla device-add path, so no consumer is notified and motherboard dropdowns / IC-housing /
        // sensor caches stay stale until a real add/remove or a motherboard replug. We re-fire the
        // vanilla "device list changed" signal (OnDeviceConnectToNetwork on each physical member) on the
        // affected networks. Two entry points feed the same diff-guarded, main-thread cascade:
        //   - the propagation postfix above, for the transitive reachable set (far-build coverage);
        //   - ScheduleCascadeForDevice, from the server SetLogicValue write and the client mode message,
        //     for the bridge device's own component (covers passthrough turning both on AND off).
        //
        // All cascade work runs on the main thread via MainThreadDispatcher: DirtyPowerAndDataDeviceLists
        // (hence this postfix) can be reached from the UniTask power-worker thread, and the cascade
        // touches Unity UI rebuilds. See Research/Patterns/MainThreadDispatcher.md and
        // Research/GameClasses/Motherboard.md.

        private sealed class MergedSignature
        {
            public bool Initialized;
            public int Count;
            public ulong Xor;
        }

        // Per-network snapshot of the last merged-set signature we cascaded for. ConditionalWeakTable so
        // entries vanish when a network is GC'd (merge / split). Touched only on the main thread.
        private static readonly ConditionalWeakTable<CableNetwork, MergedSignature> _lastSignature =
            new ConditionalWeakTable<CableNetwork, MergedSignature>();

        internal static void ScheduleCascade(IList<CableNetwork> networks)
        {
            if (networks == null || networks.Count == 0) return;
            var snapshot = new List<CableNetwork>(networks);
            MainThreadDispatcher.Enqueue(() =>
            {
                for (int i = 0; i < snapshot.Count; i++)
                    CascadeNetwork(snapshot[i]);
            });
        }

        // Cascade the bridge device's own component: its two bridge-side networks plus everything
        // transitively reachable from either side, computed on the main thread so the topology reads do
        // not race the sim. Covers both flip directions: on 0 -> 1 the post-write reachability is the new
        // (larger) component; on 1 -> 0 the union of the two now-split sides equals the old component.
        internal static void ScheduleCascadeForDevice(Device device)
        {
            if (device == null) return;
            MainThreadDispatcher.Enqueue(() =>
            {
                var affected = new HashSet<CableNetwork>();
                foreach (var net in PassthroughTopology.GetBridgeNetworks(device))
                {
                    if (net == null) continue;
                    affected.Add(net);
                    var reachable = PassthroughTopology.GatherReachable(net);
                    if (reachable != null)
                        for (int i = 0; i < reachable.Count; i++) affected.Add(reachable[i]);
                }
                foreach (var net in affected) CascadeNetwork(net);
            });
        }

        // Refresh every cable network. For global changes that alter which devices bridge (for example
        // the per-kind passthrough defaults arriving on a client), where there is no single device or
        // component to scope to. Each dirty also fires the propagation postfix; the per-network
        // diff-guard means only networks whose merged set actually changed rebuild their consumers,
        // so this is cheap apart from the (rare) global change.
        internal static void RefreshAllNetworks()
        {
            var all = new List<CableNetwork>(CableNetwork.AllCableNetworks.ActiveCount);
            CableNetwork.AllCableNetworks.ForEach(n => { if (n != null) all.Add(n); });
            for (int i = 0; i < all.Count; i++)
                all[i].DirtyPowerAndDataDeviceLists();
            ScheduleCascade(all);
        }

        // Main-thread only. Re-reads the (now-fresh) merged DataDeviceList; if its membership changed
        // since we last cascaded this network, notifies every physical member that the list changed.
        private static void CascadeNetwork(CableNetwork network)
        {
            if (network == null) return;

            // Reading DataDeviceList rebuilds it on the dirty flag (running the passthrough merge), so the
            // signature reflects the post-merge set and the consumers below read fresh data.
            var merged = network.DataDeviceList;
            int count = merged.Count;
            ulong xor = 0UL;
            for (int i = 0; i < count; i++)
            {
                var d = merged[i];
                if (d != null) xor ^= (ulong)d.ReferenceId;
            }

            var sig = _lastSignature.GetOrCreateValue(network);
            if (sig.Initialized && sig.Count == count && sig.Xor == xor)
                return; // merged set unchanged: nothing to refresh
            sig.Initialized = true;
            sig.Count = count;
            sig.Xor = xor;

            // Fire the vanilla "device list changed" signal on every PHYSICAL member of this network
            // (DeviceList, not the merged DataDeviceList). The host Computer forwards it to its
            // motherboard's OnDeviceListChanged; IC housings and LogicBase sensors invalidate their
            // sorted caches. We call OnDeviceConnectToNetwork directly (not RefreshNetworkDevice) to skip
            // the OnAddCableNetwork side effect; the argument is a reference member, ignored by the
            // consumers we target.
            var members = network.DeviceList;
            Device reference = null;
            for (int i = 0; i < members.Count; i++)
            {
                if (members[i] != null) { reference = members[i]; break; }
            }
            if (reference == null) return;
            for (int i = 0; i < members.Count; i++)
            {
                var d = members[i];
                if (d != null) d.OnDeviceConnectToNetwork(reference);
            }
        }

    }
}
