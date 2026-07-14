using System.Threading;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Util;

namespace PowerGridPlus
{
    /// <summary>
    ///     Wrong-tier cable enforcement (POWER.md §3 / §4.3, Option B + C).
    ///
    ///     <para><b>Detection runs on the worker thread; burns run on the main thread.</b> Tier
    ///     detection only reads cached state (cable types, <c>Connection.GetCable</c> which uses the
    ///     cached <c>LocalGrid</c>, the network's device list), so it is safe in PROTECT (wrong-tier burn) of the
    ///     atomic tick. The actual <c>Cable.Break</c> and its victim selection (the mixed-tier boundary
    ///     walk uses <c>ConnectedCables</c>, which reads <c>Transform.position</c> and is main-thread
    ///     only) are marshalled to the main thread via <see cref="UnityMainThreadDispatcher"/>. There,
    ///     <c>Break</c> runs synchronously and the network split lands at end of frame -- before the next
    ///     tick. The old per-network 4-tick burn cooldown is gone; a network with a burn in flight is
    ///     gated by <see cref="SplitPendingRegistry"/> instead (state-based, no magic-number timer).</para>
    ///
    ///     <para><b>Two triggers.</b> (1) <see cref="Run"/> is the per-tick backstop: it re-checks every
    ///     network each tick, so a violation that arises from any path -- including future or modded
    ///     build paths the immediate hook does not cover -- is caught within one tick. Cheap in steady
    ///     state: the tier verdict is served from <see cref="GetTierInfo"/>'s cache (event-evicted via
    ///     <see cref="InvalidateNet"/>, cable-count belt), and the per-device walk only runs on nets
    ///     that pass the flow gate. (2) <see cref="RequestRecheck"/> is called by the topology
    ///     postfixes in VoltageTierPatches (the CableNetwork constructors and Add; the 0.2.6403 game
    ///     update removed the old <c>OnNetworkChanged</c> event these used to subscribe to). It
    ///     re-checks immediately when topology changes, so a freshly created violation burns
    ///     synchronously before any tick sees it (Option B's zero-tick reaction). Both funnel
    ///     through the same <see cref="DetectViolation"/> / <see cref="ExecuteBurn"/> pair and respect
    ///     the pending gate, so they never double-burn a network. A third layer, the registration
    ///     guard in WiringGuardPatches, refuses fresh cursor-less placements outright (decision 31)
    ///     before either trigger needs to burn anything.</para>
    /// </summary>
    internal static class VoltageTierEnforcer
    {
        internal struct TierInfo
        {
            public int CableCount;
            public Cable.Type? Tier;          // uniform tier, or the first seen when mixed
            public Cable.Type? LowestTier;    // lowest tier present (cap basis for CableMax)
            public bool Mixed;
            public bool StackedTheft;         // a member cable's cell seats a DIFFERENT-tier cable
        }

        internal enum TierViolationKind { None, Mixed, Transformer, ApcPort, Misplaced }

        internal struct TierViolation
        {
            public TierViolationKind Kind;
            public Device Device;             // transformer / apc / misplaced device; null for Mixed
            internal static readonly TierViolation None = new TierViolation { Kind = TierViolationKind.None };
        }

        // Per-network tier scan, recomputed when the network's cable count changes. Cable count is a
        // reliable proxy: tier membership only changes when a cable is added or removed. Concurrent:
        // written from both the power worker (the snapshot builder / per-tick backstop) and the
        // main thread (the OnNetworkChanged re-check), which corrupted the old plain Dictionary
        // (round-3 tier-1 finding; POWER.md §0 decision 24 stage 0).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, TierInfo> _tierCache =
            new System.Collections.Concurrent.ConcurrentDictionary<long, TierInfo>();

        // Coalescing flag for the OnNetworkChanged-driven main-thread re-check (0 = idle, 1 = scheduled).
        private static int _recheckScheduled;

        /// <summary>
        ///     Event-driven cache eviction: the topology postfixes in VoltageTierPatches call this on
        ///     every membership mutation (construct, Add, Remove), so a same-count membership swap (a
        ///     blueprint reprint replacing N cables with N different-tier cables) cannot serve a stale
        ///     verdict. The <see cref="GetTierInfo"/> cable-count compare stays as the belt.
        /// </summary>
        internal static void InvalidateNet(long netId) => _tierCache.TryRemove(netId, out _);

        /// <summary>
        ///     Whole-cache eviction, for mutations whose affected network cannot be named: destroying a
        ///     SEATED cable clears its cell by reference equality, and any co-located unseated cable
        ///     (a theft victim on ANOTHER network) silently becomes a null-seat orphan; the single-slot
        ///     cell model gives the destroy site no way to find that other network. Removals are rare
        ///     (burns, deconstructions, rebuild churn), so the cost is one lazy recompute wave over the
        ///     nets the next tick touches, and steady state stays cached.
        /// </summary>
        internal static void InvalidateAll() => _tierCache.Clear();

        internal static TierInfo GetTierInfo(CableNetwork network)
        {
            if (network == null) return default;
            int count;
            lock (network.CableList) count = network.CableList.Count;

            if (_tierCache.TryGetValue(network.ReferenceId, out var cached) && cached.CableCount == count)
                return cached;

            var info = new TierInfo { CableCount = count };
            lock (network.CableList)
            {
                int lowestRank = int.MaxValue;
                for (int i = 0; i < network.CableList.Count; i++)
                {
                    var cable = network.CableList[i];
                    if (cable == null) continue;
                    if (!info.Tier.HasValue) info.Tier = cable.CableType;
                    else if (cable.CableType != info.Tier.Value) info.Mixed = true;
                    int rank = TierRank(cable.CableType);
                    if (rank < lowestRank)
                    {
                        lowestRank = rank;
                        info.LowestTier = cable.CableType;
                    }

                    // Theft scan: a cable whose own cell seats a DIFFERENT cable was stacked over by a
                    // cursor-less print (the victim's SmallCell back-pointer survives the overwrite;
                    // Research/GameClasses/SmallCell.md). Different tier makes it this enforcer's case
                    // even when the thief sits on another network entirely. A NULL seat is the
                    // aftermath of that thief being destroyed while seated (the reference-equality
                    // cell clear empties the slot): the cable is an orphaned ghost, grid-invisible to
                    // rebuild floods, and flags the same way (observed live on the Luna_mixedwire heal
                    // 2026-07-14: the boundary burn of a seated thief orphaned the super underneath).
                    if (cable.IsBeingDestroyed || cable.SmallCell == null) continue;
                    var seated = cable.SmallCell.Cable;
                    if (seated == null
                        || (!ReferenceEquals(seated, cable)
                            && !seated.IsBeingDestroyed && seated.CableType != cable.CableType))
                    {
                        info.StackedTheft = true;
                    }
                }
            }
            _tierCache[network.ReferenceId] = info;
            return info;
        }

        private static int TierRank(Cable.Type t)
        {
            switch (t)
            {
                case Cable.Type.normal: return 1;
                case Cable.Type.heavy: return 2;
                case Cable.Type.superHeavy: return 3;
                default: return 0;
            }
        }

        /// <summary>
        ///     Main-thread detection (the topology-event re-check and the pre-burn re-detect): the
        ///     gates read the write-back-published net load fields (last tick's truth; the retired
        ///     vanilla PowerTick fields are never updated any more), and the device walk snapshots
        ///     the live list. The worker's per-tick backstop goes through
        ///     <see cref="DetectViolation(Core.GridSnapshot.NetRow)"/> instead.
        ///
        ///     Two gates, per violation kind (POWER.md decision 31):
        ///     ACTIVITY (mixed-tier): any electrical life on the net, supply OR demand. The old
        ///     min(Potential, Required) flow gate deadlocked the 2026-07-13 mixed-wire incident:
        ///     the mainline pair's overload lockout re-zeroed the published demand every cycle, so
        ///     min() never rose above zero and the burn waited forever on flow the burn itself was
        ///     blocking. A mixed net whose feed is live burns even when nothing draws yet; only
        ///     genuinely dead decorative wiring is left alone (it burns the tick power first touches
        ///     it).
        ///     FLOW (device kinds): unchanged min() gate (POWER.md §4.4 analog); a wrong-tier device
        ///     on an idle net destroys nothing until power actually moves.
        /// </summary>
        internal static TierViolation DetectViolation(CableNetwork net)
        {
            if (net == null) return TierViolation.None;

            float potential = net.PotentialLoad;
            float required = net.RequiredLoad;
            bool active = potential > 0f || required > 0f;
            bool flowing = (potential < required ? potential : required) > 0f;

            System.Collections.Generic.List<Device> devices = null;
            if (flowing)
            {
                lock (net.PowerDeviceList)
                {
                    devices = new System.Collections.Generic.List<Device>(net.PowerDeviceList);
                }
            }
            return DetectViolationCore(net, devices, flowing, active);
        }

        /// <summary>
        ///     Worker detection from the tick snapshot (no vanilla list or field reads). The activity
        ///     gate additionally sees the snapshot's own RigidDemand (this-tick device demand,
        ///     computed at SNAPSHOT from device state, independent of last tick's allocation), so a
        ///     lockout that zeroes the published sums cannot hide a demanded mixed net.
        /// </summary>
        internal static TierViolation DetectViolation(Core.GridSnapshot.NetRow nr)
        {
            if (nr == null) return TierViolation.None;

            bool active = nr.RigidDemand > 0f || nr.RequiredSum > 0f || nr.PotentialSum > 0f;
            bool flowing = (nr.PotentialSum < nr.RequiredSum ? nr.PotentialSum : nr.RequiredSum) > 0f;

            System.Collections.Generic.List<Device> devices = null;
            if (flowing)
            {
                devices = new System.Collections.Generic.List<Device>(nr.Rows.Count);
                for (int i = 0; i < nr.Rows.Count; i++) devices.Add(nr.Rows[i].Device);
            }
            return DetectViolationCore(nr.Network, devices, flowing, active);
        }

        private static TierViolation DetectViolationCore(
            CableNetwork net, System.Collections.Generic.List<Device> devices, bool flowing, bool active)
        {
            var info = GetTierInfo(net);

            // 1a. Stacked theft (including null-seat orphans): NOT activity-gated. The resolution is a
            //     surgical repair (destroy at most the stacked thief, re-seat the survivor; no rupture
            //     burn), and the broken nets are typically idle by construction: the orphaned fragment
            //     carries no devices, so an activity gate would leave it corrupted forever (observed on
            //     the Luna_mixedwire heal, 2026-07-14).
            if (info.StackedTheft) return new TierViolation { Kind = TierViolationKind.Mixed };

            // 1b. Mixed-tier network: activity-gated (a burn destroys a cable; idle decorative wiring
            //     waits until power first touches it). Victim selection happens at burn time on the
            //     main thread via ResolveMixedTierNetwork.
            if (info.Mixed && active) return new TierViolation { Kind = TierViolationKind.Mixed };

            // 2-4 are flow-gated: no devices were collected on a non-flowing net.
            if (!flowing || devices == null) return TierViolation.None;

            // 2-4. Per-device, priority transformer pair -> APC port -> misplaced device.
            Transformer transformerForBurn = null;
            AreaPowerControl apcMismatch = null;
            Device misplacedDevice = null;

            for (int i = 0; i < devices.Count; i++)
            {
                var device = devices[i];
                if (device == null) continue;

                if (transformerForBurn == null
                    && device is Transformer transformer
                    && VoltageTier.IsTransformerTierViolated(transformer)
                    && VoltageTier.IsTransformerActivelyConducting(transformer))
                {
                    transformerForBurn = transformer;
                    break;   // highest-priority device case
                }

                if (apcMismatch == null
                    && device is AreaPowerControl apc
                    && VoltageTier.FindMismatchedApcCable(apc) != null)
                {
                    apcMismatch = apc;
                }

                // Data-only-port carve-out: only judge a device on a network it reaches through a
                // POWER port. A device wired to this network solely via its exclusive data-only port
                // imposes no tier requirement and must never burn (the snapshot rows and
                // PowerDeviceList both exclude it today, but the explicit guard keeps that
                // guarantee robust). See VoltageTier.ReachesNetworkViaPowerPort.
                if (misplacedDevice == null
                    && info.Tier.HasValue
                    && VoltageTier.ReachesNetworkViaPowerPort(device, net)
                    && !VoltageTier.IsAllowedOnTier(device, info.Tier.Value))
                {
                    misplacedDevice = device;
                }
            }

            if (transformerForBurn != null)
                return new TierViolation { Kind = TierViolationKind.Transformer, Device = transformerForBurn };
            if (apcMismatch != null)
                return new TierViolation { Kind = TierViolationKind.ApcPort, Device = apcMismatch };
            if (misplacedDevice != null)
                return new TierViolation { Kind = TierViolationKind.Misplaced, Device = misplacedDevice };
            return TierViolation.None;
        }

        /// <summary>
        ///     Main thread only: re-detect the violation on this network (topology may have changed since
        ///     the worker queued it), select the victim cable(s), and burn. Records the burn in
        ///     <see cref="SplitPendingRegistry"/> so the network is not re-burned and the allocator
        ///     defers its durable decisions until the split lands; releases the reservation if the
        ///     violation has vanished.
        /// </summary>
        private static void ExecuteBurn(long netRefId)
        {
            var net = Referencable.Find<CableNetwork>(netRefId);
            if (net == null) { SplitPendingRegistry.Release(netRefId); return; }

            int countBefore;
            lock (net.CableList) countBefore = net.CableList.Count;

            var v = DetectViolationAtBurn(net);
            bool burned = false;
            switch (v.Kind)
            {
                case TierViolationKind.Mixed:
                    burned = VoltageTier.ResolveMixedTierNetwork(net);
                    break;
                case TierViolationKind.Transformer:
                    burned = VoltageTier.BurnTransformerBothCables(v.Device as Transformer);
                    break;
                case TierViolationKind.ApcPort:
                    var apc = v.Device as AreaPowerControl;
                    burned = VoltageTier.BurnPortMismatchCable(VoltageTier.FindMismatchedApcCable(apc), apc);
                    break;
                case TierViolationKind.Misplaced:
                    burned = VoltageTier.BurnCableForMisplacedDevice(v.Device, net);
                    break;
            }

            if (burned) SplitPendingRegistry.MarkBurned(netRefId, countBefore);
            else SplitPendingRegistry.Release(netRefId);
        }

        /// <summary>
        ///     Pre-burn re-detect (main thread): confirms the violation STILL EXISTS after the
        ///     worker-to-main-thread hop, without re-applying the activity gate. The reserving path
        ///     (the per-tick backstop or the event recheck) already gated on its own truth; the
        ///     write-back-published fields this method could see are last tick's and read ZERO on
        ///     device-less trunk fragments, so re-gating here silently released every reservation on
        ///     such nets and stalled the heal cascade (observed on the Luna_mixedwire heal run,
        ///     2026-07-14: two stack repairs, then permanent worker-reserve / main-release ping-pong).
        ///     The flow gate for the device kinds is preserved: without flow no device list is built
        ///     and kinds 2-4 return None, exactly as before.
        /// </summary>
        private static TierViolation DetectViolationAtBurn(CableNetwork net)
        {
            if (net == null) return TierViolation.None;

            float potential = net.PotentialLoad;
            float required = net.RequiredLoad;
            bool flowing = (potential < required ? potential : required) > 0f;

            System.Collections.Generic.List<Device> devices = null;
            if (flowing)
            {
                lock (net.PowerDeviceList)
                {
                    devices = new System.Collections.Generic.List<Device>(net.PowerDeviceList);
                }
            }
            // active: true. The reserving path already applied its activity gate on its own truth;
            // re-gating here on last-tick published fields stalled the heal cascade (see the method doc).
            return DetectViolationCore(net, devices, flowing, active: true);
        }

        /// <summary>
        ///     Per-tick backstop (PROTECT (wrong-tier burn), worker thread). Clears landed pendings, then re-checks
        ///     every network and marshals a burn to the main thread for any non-pending violation.
        /// </summary>
        internal static void Run(Core.GridSnapshot snap)
        {
            // State-based pending clear: drop networks whose burn-induced split has landed (cable count
            // changed). Replaces the old fixed 4-tick cooldown.
            SplitPendingRegistry.SweepLanded();
            if (snap == null) return;

            for (int i = 0; i < snap.Nets.Count; i++)
            {
                try
                {
                    var nr = snap.Nets[i];
                    long refId = nr.Id;
                    if (SplitPendingRegistry.IsPending(refId)) continue;   // burn in flight; wait for the split

                    var v = DetectViolation(nr);
                    if (v.Kind == TierViolationKind.None) continue;

                    // Reserve before enqueuing so a concurrent OnNetworkChanged re-check cannot double-burn.
                    if (!SplitPendingRegistry.TryReserve(refId)) continue;
                    EnqueueBurn(refId);
                }
                catch (System.Exception ex)
                {
                    Plugin.Log?.LogWarning(
                        $"[PowerGridPlus] Tier enforcement failed on network {snap.Nets[i].Id}: {ex.Message}");
                }
            }
        }

        /// <summary>
        ///     Called from the topology postfixes in VoltageTierPatches (main thread). Coalesces a
        ///     single main-thread re-check of all networks, so a violation created by any topology
        ///     mutation is caught and burned synchronously before the next tick (Option B). Gated on
        ///     host simulation: clients never burn (they receive the authoritative split over the wire).
        /// </summary>
        internal static void RequestRecheck()
        {
            if (!GameManager.RunSimulation) return;
            // Coalesce: if a re-check is already scheduled, this mutation rides it.
            if (Interlocked.CompareExchange(ref _recheckScheduled, 1, 0) != 0) return;

            var dispatcher = TryGetDispatcher();
            if (dispatcher == null) { Interlocked.Exchange(ref _recheckScheduled, 0); return; }
            dispatcher.Enqueue(RunRecheckAll);
        }

        private static void RunRecheckAll()
        {
            Interlocked.Exchange(ref _recheckScheduled, 0);
            if (!GameManager.RunSimulation || GameManager.GameState != GameState.Running) return;

            CableNetwork.AllCableNetworks.ForEach(net =>
            {
                if (net == null) return;
                long refId = net.ReferenceId;
                if (SplitPendingRegistry.IsPending(refId)) return;

                var v = DetectViolation(net);
                if (v.Kind == TierViolationKind.None) return;

                if (!SplitPendingRegistry.TryReserve(refId)) return;
                ExecuteBurn(refId);   // already on the main thread: burn synchronously, split lands this frame
            });
        }

        private static void EnqueueBurn(long netRefId)
        {
            var dispatcher = TryGetDispatcher();
            if (dispatcher == null)
            {
                // No dispatcher (should not happen mid-sim): drop the reservation so the next tick retries.
                SplitPendingRegistry.Release(netRefId);
                return;
            }
            dispatcher.Enqueue(() => ExecuteBurn(netRefId));
        }

        private static UnityMainThreadDispatcher TryGetDispatcher()
        {
            try { return UnityMainThreadDispatcher.Exists() ? UnityMainThreadDispatcher.Instance() : null; }
            catch { return null; }
        }
    }
}
