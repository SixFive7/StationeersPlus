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
    ///     build paths the immediate hook does not cover -- is caught within one tick. (2)
    ///     <see cref="RequestRecheck"/> is subscribed to <c>CableNetwork.OnNetworkChanged</c> (a
    ///     main-thread-only event fired by every membership mutation: placement, merge, split, load,
    ///     device add). It re-checks immediately when topology changes, so a freshly created violation
    ///     burns synchronously before any tick sees it (Option B's zero-tick reaction). Both funnel
    ///     through the same <see cref="DetectViolation"/> / <see cref="ExecuteBurn"/> pair and respect
    ///     the pending gate, so they never double-burn a network.</para>
    /// </summary>
    internal static class VoltageTierEnforcer
    {
        internal struct TierInfo
        {
            public int CableCount;
            public Cable.Type? Tier;          // uniform tier, or the first seen when mixed
            public Cable.Type? LowestTier;    // lowest tier present (cap basis for CableMax)
            public bool Mixed;
        }

        internal enum TierViolationKind { None, Mixed, Transformer, ApcPort, Misplaced }

        internal struct TierViolation
        {
            public TierViolationKind Kind;
            public Device Device;             // transformer / apc / misplaced device; null for Mixed
            internal static readonly TierViolation None = new TierViolation { Kind = TierViolationKind.None };
        }

        // Per-network tier scan, recomputed when the network's cable count changes. Cable count is a
        // reliable proxy: tier membership only changes when a cable is added or removed.
        private static readonly System.Collections.Generic.Dictionary<long, TierInfo> _tierCache =
            new System.Collections.Generic.Dictionary<long, TierInfo>();

        // Coalescing flag for the OnNetworkChanged-driven main-thread re-check (0 = idle, 1 = scheduled).
        private static int _recheckScheduled;

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
        ///     Worker- and main-thread-safe detection: returns the first tier violation on this network
        ///     (cached reads only -- no <c>ConnectedCables</c> / <c>Transform</c> access). Victim
        ///     selection for the mixed case is deferred to <see cref="ExecuteBurn"/> on the main thread.
        /// </summary>
        internal static TierViolation DetectViolation(CableNetwork net)
        {
            if (net == null) return TierViolation.None;
            var pt = net.PowerTick;
            if (pt == null) return TierViolation.None;

            // Power-flow gate: only networks actually carrying power burn (POWER.md §4.4 analog). An idle
            // or unpowered network destroys nothing; the next tick that carries power re-checks it.
            float actual = pt.Potential < pt.Required ? pt.Potential : pt.Required;
            if (actual <= 0f) return TierViolation.None;

            var info = GetTierInfo(net);

            // 1. Mixed-tier network (root cause): victim is chosen at burn time on the main thread.
            if (info.Mixed) return new TierViolation { Kind = TierViolationKind.Mixed };

            // 2-4. Per-device, priority transformer pair -> APC port -> misplaced device.
            Transformer transformerForBurn = null;
            AreaPowerControl apcMismatch = null;
            Device misplacedDevice = null;

            lock (net.PowerDeviceList)
            {
                var list = net.PowerDeviceList;
                for (int i = 0; i < list.Count; i++)
                {
                    var device = list[i];
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

                    if (misplacedDevice == null
                        && info.Tier.HasValue
                        && !VoltageTier.IsAllowedOnTier(device, info.Tier.Value))
                    {
                        misplacedDevice = device;
                    }
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

            var v = DetectViolation(net);
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
        ///     Per-tick backstop (PROTECT (wrong-tier burn), worker thread). Clears landed pendings, then re-checks
        ///     every network and marshals a burn to the main thread for any non-pending violation.
        /// </summary>
        internal static void Run()
        {
            // State-based pending clear: drop networks whose burn-induced split has landed (cable count
            // changed). Replaces the old fixed 4-tick cooldown.
            SplitPendingRegistry.SweepLanded();

            CableNetwork.AllCableNetworks.ForEach(net =>
            {
                if (net == null) return;
                long refId = net.ReferenceId;
                if (SplitPendingRegistry.IsPending(refId)) return;   // burn in flight; wait for the split

                var v = DetectViolation(net);
                if (v.Kind == TierViolationKind.None) return;

                // Reserve before enqueuing so a concurrent OnNetworkChanged re-check cannot double-burn.
                if (!SplitPendingRegistry.TryReserve(refId)) return;
                EnqueueBurn(refId);
            });
        }

        /// <summary>
        ///     Subscribed to <c>CableNetwork.OnNetworkChanged</c> (main thread). Coalesces a single
        ///     main-thread re-check of all networks, so a violation created by any topology mutation is
        ///     caught and burned synchronously before the next tick (Option B). Gated on host
        ///     simulation: clients never burn (they receive the authoritative split over the wire).
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
