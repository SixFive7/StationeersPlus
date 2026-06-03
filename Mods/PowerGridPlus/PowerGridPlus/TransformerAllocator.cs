using System.Collections.Concurrent;
using System.Collections.Generic;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using LaunchPadBooster.Networking;

namespace PowerGridPlus
{
    // Global strict-priority allocation of cable-network supply among ALL active
    // transformers. Single per-tick pass replaces the older per-input-network
    // compute, so any valid topology resolves correctly:
    //
    //   - Same input, same output (parallel redundancy): higher-priority claims
    //     first; the others see reduced output-network demand and stand by.
    //   - Different inputs, same output (multi-source redundancy): the global
    //     priority sort gives the highest-priority transformer first dibs on the
    //     shared OutputNetwork.RequiredLoad. Lower-priority sources see the
    //     leftover demand and either supply it or stand by.
    //   - Cascading chains (T1 in NetA -> NetB, T2 in NetB -> NetC, ...): each
    //     transformer's `desired` is bounded by its OutputNetwork.RequiredLoad,
    //     so demand at the leaf propagates up the chain one hop per tick. The
    //     chain converges to steady state in O(chain depth) ticks. The
    //     defer-shed-on-zero-budget rule (see below) prevents lockouts during
    //     this transient.
    //   - Circular topologies (T1 -> T2 -> ... -> T1): the algorithm processes
    //     each transformer once per tick in priority order. There is no
    //     recursion; each transformer reads its InputNetwork.PotentialLoad
    //     (snapshot from the previous tick's ApplyState) so cycles cannot
    //     infinite-loop. Steady-state behaviour depends on the cycle's net
    //     supply / demand but the algorithm always terminates.
    //
    // Algorithm:
    //   1. Walk CableNetwork.AllCableNetworks; for each, walk its PowerDeviceList
    //      to find transformers whose InputNetwork == that network (dedup
    //      condition; a transformer is on both its input and output network's
    //      lists). Collect into a global contributors list.
    //   2. Sort contributors globally by (Priority desc, ReferenceId asc).
    //      ReferenceId is server-allocated + synced, so the order matches across
    //      host and clients.
    //   3. Per-input-network `budgets` dict and per-output-network `outDemands`
    //      dict are populated lazily. The first contributor on a given input net
    //      seeds `budget = InputNetwork.PotentialLoad`; subsequent contributors
    //      on the same input net read the running value.
    //   4. For each contributor in sorted order:
    //        desired = min(OutputMaximum, outDemand[outId])
    //        if desired <= 0:                  standby (no demand left)
    //        else if desired <= budget[inId]:  grant; decrement both trackers
    //        else if budget[inId] > 0:         genuine shortfall -> shed
    //        else:                             defer (input has no supply yet)
    //
    // Cache: ConcurrentDictionary<long,float> keyed by Transformer.ReferenceId,
    // computed lazily on the first GetAllocatedSupply call per (tick, priority-
    // version). Subsequent calls within the same tick hit the cache. The cache
    // is invalidated on tick rollover OR priority-version change.
    //
    // Determinism: every input is replicated (priorities via PriorityMessage +
    // join-suffix, OnOff/Error/OutputMaximum/PotentialLoad/RequiredLoad via
    // vanilla device + network sync). Same inputs -> same allocations on every
    // peer. The shed transitions detected from this allocation are broadcast
    // via ShedStateMessage in ElectricityTickPatches.
    //
    // Threading: the power tick fans out across UniTask ThreadPool workers. The
    // global compute is double-checked-locked so concurrent GetAllocatedSupply
    // callers don't race the compute itself.
    //
    // Gate: when ShedSettingsSync.Effective is false, GetAllocatedSupply returns
    // the vanilla Setting field (transformer behaves as in vanilla, ignoring all
    // priority+shedding machinery).
    internal static class TransformerAllocator
    {
        private static volatile Dictionary<long, float> _cachedAllocations;
        private static int _cachedTick = -1;
        private static int _cachedVersion = -1;
        private static readonly object _computeLock = new object();

        internal static float GetAllocatedSupply(Transformer t)
        {
            if (t == null) return 0f;
            if (!ShedSettingsSync.Effective)
                return t.OnOff ? (float)t.Setting : 0f;
            if (t.InputNetwork == null || t.OutputNetwork == null) return 0f;
            if (!t.OnOff || t.Error == 1) return 0f;

            int tick = ElectricityTickCounter.CurrentTick;
            int version = PriorityStore.Version;

            var cached = _cachedAllocations;
            if (cached != null && _cachedTick == tick && _cachedVersion == version)
            {
                return cached.TryGetValue(t.ReferenceId, out var v) ? v : 0f;
            }

            lock (_computeLock)
            {
                if (_cachedAllocations != null && _cachedTick == tick && _cachedVersion == version)
                {
                    return _cachedAllocations.TryGetValue(t.ReferenceId, out var v2) ? v2 : 0f;
                }
                var fresh = ComputeGlobal(tick);
                _cachedAllocations = fresh;
                _cachedTick = tick;
                _cachedVersion = version;
                return fresh.TryGetValue(t.ReferenceId, out var v3) ? v3 : 0f;
            }
        }

        private static Dictionary<long, float> ComputeGlobal(int tick)
        {
            // (1) Gather every eligible transformer across all cable networks. A
            // transformer is on both its input and output network's PowerDeviceList,
            // so dedup by counting it only on its InputNetwork pass.
            var contributors = new List<Transformer>();
            CableNetwork.AllCableNetworks.ForEach(net =>
            {
                if (net == null) return;
                lock (net.PowerDeviceList)
                {
                    for (int i = 0; i < net.PowerDeviceList.Count; i++)
                    {
                        if (net.PowerDeviceList[i] is Transformer ct
                            && ct.InputNetwork == net
                            && ct.OutputNetwork != null
                            && ct.OnOff
                            && ct.Error != 1)
                        {
                            contributors.Add(ct);
                        }
                    }
                }
            });

            // (2) Global priority sort: priority desc, ReferenceId asc tiebreak.
            contributors.Sort(CompareForAllocation);

            // (3) Per-input-network budget and per-output-network demand trackers.
            var budgets = new Dictionary<long, float>();
            var outDemands = new Dictionary<long, float>();
            var result = new Dictionary<long, float>(contributors.Count);

            // (4) Walk in sorted order, allocating from input budget and output demand.
            for (int i = 0; i < contributors.Count; i++)
            {
                var ct = contributors[i];

                // Existing lockout: zero allocation, trackers untouched (lower
                // priorities can still use the budget and demand).
                if (BrownoutRegistry.IsLockedOut(ct.ReferenceId, tick))
                {
                    result[ct.ReferenceId] = 0f;
                    continue;
                }

                long inId = ct.InputNetwork.ReferenceId;
                long outId = ct.OutputNetwork.ReferenceId;

                if (!budgets.TryGetValue(inId, out var budget))
                {
                    budget = ct.InputNetwork.PotentialLoad;
                    if (budget < 0f) budget = 0f;
                }
                if (!outDemands.TryGetValue(outId, out var remOut))
                {
                    remOut = ct.OutputNetwork.RequiredLoad;
                    if (remOut < 0f) remOut = 0f;
                }

                float desired = UnityEngine.Mathf.Min(ct.OutputMaximum, remOut);

                if (desired <= 0.01f)
                {
                    // Standby: no demand left on the output network. Don't shed; a
                    // parallel transformer waiting for primary to fail isn't in
                    // shortfall.
                    result[ct.ReferenceId] = 0f;
                    BrownoutRegistry.NoteSupplyOk(ct.ReferenceId);
                    continue;
                }

                if (desired <= budget)
                {
                    result[ct.ReferenceId] = desired;
                    budgets[inId] = budget - desired;
                    outDemands[outId] = remOut - desired;
                    BrownoutRegistry.NoteSupplyOk(ct.ReferenceId);
                }
                else if (budget > 0.01f)
                {
                    // Genuine shortfall: input has SOME supply but less than what
                    // this transformer needs. NoteShortfall counts consecutive
                    // ticks; ShortfallTolerance hits trigger the lockout.
                    BrownoutRegistry.NoteShortfall(ct.ReferenceId, tick);
                    result[ct.ReferenceId] = 0f;
                    // budgets[inId] unchanged; lower-priority might still use it.
                }
                else
                {
                    // Input has no supply at all: either chain is bootstrapping
                    // (1 tick of latency per hop for demand to propagate up) or
                    // genuinely dead. Don't shed; the transformer just sits idle
                    // until something upstream changes.
                    result[ct.ReferenceId] = 0f;
                    BrownoutRegistry.NoteSupplyOk(ct.ReferenceId);
                }
            }
            return result;
        }

        private static int CompareForAllocation(Transformer a, Transformer b)
        {
            int ap = PriorityStore.GetPriority(a.ReferenceId);
            int bp = PriorityStore.GetPriority(b.ReferenceId);
            if (ap != bp) return bp.CompareTo(ap);
            return a.ReferenceId.CompareTo(b.ReferenceId);
        }

        // Legacy escape-hatch for callers that change priorities and immediately
        // need a recomputation (tests, ScenarioRunner probes). The version-based
        // cache invalidation covers normal flow.
        internal static void InvalidateAll()
        {
            _cachedAllocations = null;
            _cachedTick = -1;
            _cachedVersion = -1;
        }

        // No-op kept for ABI compatibility with ElectricityTickPatches. The old
        // per-network cache had stale entries to trim across network merges; the
        // new global cache is one dict per tick, replaced wholesale.
        internal static void TrimCache(int currentTick) { /* intentionally empty */ }

        // -------------------------------------------------------------------
        // Shed-transition broadcast (host -> client). Diff this tick's locked-out
        // set against last tick's snapshot and broadcast ShedStateMessage per
        // change.
        // -------------------------------------------------------------------

        private static readonly HashSet<long> _lastTickShedSet = new HashSet<long>();
        private static readonly object _shedSetLock = new object();

        internal static void SyncShedTransitions(int currentTick)
        {
            if (!Assets.Scripts.Networking.NetworkManager.IsActive) return;
            if (!Assets.Scripts.Networking.NetworkManager.IsServer) return;

            var current = new HashSet<long>();
            foreach (var id in BrownoutRegistry.CurrentlyLockedOut(currentTick))
                current.Add(id);

            lock (_shedSetLock)
            {
                foreach (var id in current)
                {
                    if (!_lastTickShedSet.Contains(id))
                        new ShedStateMessage { DeviceId = id, Shedding = true }.SendAll(0L);
                }
                foreach (var id in _lastTickShedSet)
                {
                    if (!current.Contains(id))
                        new ShedStateMessage { DeviceId = id, Shedding = false }.SendAll(0L);
                }
                _lastTickShedSet.Clear();
                foreach (var id in current) _lastTickShedSet.Add(id);
            }
        }
    }
}
