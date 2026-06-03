using System.Collections.Concurrent;
using System.Collections.Generic;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using LaunchPadBooster.Networking;

namespace PowerGridPlus
{
    // Strict-priority allocation of input-network supply among the transformers
    // pulling from it. Runs lazily on demand (called from Transformer's patched
    // GetGeneratedPower / GetUsedPower paths), caches per-tick per-input-network.
    //
    // Algorithm:
    //   For each input cable network with N >= 1 transformers pulling from it:
    //     1. Sort transformers by (Priority desc, ReferenceId asc). Deterministic
    //        across host and clients because Priority is synced and ReferenceId
    //        is server-allocated.
    //     2. Budget = inputNetwork.PotentialLoad.
    //     3. For each transformer T in sorted order:
    //          - If T is currently in lockout (BrownoutRegistry), allocate 0 and
    //            skip; leave the budget for lower-priority transformers.
    //          - If T is not turned on / Error == 1 / has no output network /
    //            its output network's supply path is broken, allocate 0; skip.
    //          - desired = T.OutputMaximum (throughput is hardcoded at max).
    //          - If desired <= budget: allocate desired; budget -= desired;
    //            BrownoutRegistry.NoteSupplyOk(T).
    //          - Else: allocate 0; BrownoutRegistry.NoteShortfall(T, tick).
    //            After ShortfallTolerance consecutive shortfall ticks the
    //            transformer is locked out for LockoutDurationTicks (10 s @ 2 Hz).
    //
    // Cache: results are cached per (inputNetwork.ReferenceId, tick) and invalidated
    // on tick rollover. A given tick recomputes only once per input network,
    // regardless of how many transformers call in.
    //
    // Multiplayer determinism: each peer runs this independently against
    // already-synced inputs (Priority via PriorityMessage / join-suffix, OnOff /
    // Error / OutputMaximum via vanilla device sync, PotentialLoad via the
    // network's previous-tick state which is byte-identical) and reaches the
    // same allocation.
    //
    // Gate: when Settings.EnableTransformerShedding is false, every transformer
    // gets its full OutputMaximum unconditionally and BrownoutRegistry is never
    // touched. Falls back to vanilla TransformerExploitPatches behaviour.
    internal static class TransformerAllocator
    {
        private struct CacheEntry
        {
            public int Tick;
            public int PriorityVersion;                     // PriorityStore.Version at compute time
            public Dictionary<long, float> Allocations;     // referenceId -> allocated supply (W)
        }

        // One entry per InputNetwork.ReferenceId. ConcurrentDictionary because the
        // power tick fans out across worker threads.
        private static readonly ConcurrentDictionary<long, CacheEntry> _byInputNetwork =
            new ConcurrentDictionary<long, CacheEntry>();

        // Returns the W the transformer is allowed to provide this tick. 0 means
        // shed / off / no input. Always >= 0.
        internal static float GetAllocatedSupply(Transformer t)
        {
            if (t == null) return 0f;
            // Feature gate: when disabled, fall back to vanilla cap. Use the
            // ShedSettingsSync.Effective accessor (not Settings.Enable*.Value
            // directly) so client allocation matches the host's authoritative
            // value, not the client's local config.
            if (!ShedSettingsSync.Effective)
                return t.OnOff ? (float)t.Setting : 0f;

            if (t.InputNetwork == null || t.OutputNetwork == null) return 0f;
            if (!t.OnOff || t.Error == 1) return 0f;

            int tick = ElectricityTickCounter.CurrentTick;
            int priorityVersion = PriorityStore.Version;
            var inputNet = t.InputNetwork;

            if (_byInputNetwork.TryGetValue(inputNet.ReferenceId, out var entry)
                && entry.Tick == tick
                && entry.PriorityVersion == priorityVersion)
            {
                return entry.Allocations.TryGetValue(t.ReferenceId, out var v) ? v : 0f;
            }

            // Recompute for this input network this tick.
            var fresh = Compute(inputNet, tick);
            _byInputNetwork[inputNet.ReferenceId] = new CacheEntry
            {
                Tick = tick,
                PriorityVersion = priorityVersion,
                Allocations = fresh,
            };
            return fresh.TryGetValue(t.ReferenceId, out var got) ? got : 0f;
        }

        private static Dictionary<long, float> Compute(CableNetwork inputNet, int tick)
        {
            var contributors = new List<Transformer>();
            lock (inputNet.PowerDeviceList)
            {
                for (int i = 0; i < inputNet.PowerDeviceList.Count; i++)
                {
                    if (inputNet.PowerDeviceList[i] is Transformer ct
                        && ct.InputNetwork == inputNet
                        && ct.OutputNetwork != null
                        && ct.OnOff
                        && ct.Error != 1)
                    {
                        contributors.Add(ct);
                    }
                }
            }

            // Deterministic sort: Priority desc, then ReferenceId asc.
            contributors.Sort(CompareForAllocation);

            float budget = inputNet.PotentialLoad;
            if (budget < 0f) budget = 0f;

            // Per-output-network remaining demand tracker. Multiple transformers can
            // feed the same OutputNetwork (parallel redundancy pattern). The strict
            // priority winner claims the output demand first; the loser sees zero
            // remaining demand and allocates 0 with no shed (it's just standing by).
            var outDemand = new System.Collections.Generic.Dictionary<long, float>();

            var result = new Dictionary<long, float>(contributors.Count);
            for (int i = 0; i < contributors.Count; i++)
            {
                var ct = contributors[i];

                // Existing lockout wins: zero allocation, budget unchanged for
                // lower-priority transformers.
                if (BrownoutRegistry.IsLockedOut(ct.ReferenceId, tick))
                {
                    result[ct.ReferenceId] = 0f;
                    continue;
                }

                // Desired draw = downstream demand on the OutputNetwork, capped by
                // OutputMaximum. RequiredLoad reflects the previous tick's
                // ApplyState sum of GetUsedPower over the OutputNetwork's devices,
                // so it carries the propagated demand from the bottom of a chain
                // to the top with one-tick latency per hop.
                long outId = ct.OutputNetwork.ReferenceId;
                if (!outDemand.TryGetValue(outId, out var remainingOutDemand))
                {
                    remainingOutDemand = ct.OutputNetwork.RequiredLoad;
                    if (remainingOutDemand < 0f) remainingOutDemand = 0f;
                }
                float desired = UnityEngine.Mathf.Min(ct.OutputMaximum, remainingOutDemand);

                // No (more) demand on the OutputNetwork: standby. Don't shed; reset
                // the shortfall counter so an idle parallel transformer never enters
                // lockout.
                if (desired <= 0.01f)
                {
                    result[ct.ReferenceId] = 0f;
                    BrownoutRegistry.NoteSupplyOk(ct.ReferenceId);
                    continue;
                }

                if (desired <= budget)
                {
                    result[ct.ReferenceId] = desired;
                    budget -= desired;
                    outDemand[outId] = remainingOutDemand - desired;
                    BrownoutRegistry.NoteSupplyOk(ct.ReferenceId);
                }
                else if (budget > 0.01f)
                {
                    // Genuine shortfall: input has SOME supply but less than what
                    // we need. NoteShortfall counts; after ShortfallTolerance ticks
                    // the lockout fires.
                    BrownoutRegistry.NoteShortfall(ct.ReferenceId, tick);
                    result[ct.ReferenceId] = 0f;
                    // Budget unchanged; leftover (if any) goes to lower-priority.
                }
                else
                {
                    // Input network has no supply at all: either the upstream chain
                    // is still bootstrapping (demand hasn't propagated up yet, takes
                    // 1 tick per hop) or the chain is genuinely dead. Either way,
                    // shedding individual transformers here just masks a network-wide
                    // problem and creates 10-second lockouts that never let the chain
                    // recover. Reset the shortfall counter so a transient bootstrap
                    // gap doesn't enter lockout.
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
            if (ap != bp) return bp.CompareTo(ap);          // desc
            return a.ReferenceId.CompareTo(b.ReferenceId);  // asc tiebreak
        }

        // Invoked from ElectricityTickPatches once per electricity tick. Clears
        // cache entries older than the current tick so the memory does not grow
        // when networks merge / split.
        internal static void TrimCache(int currentTick)
        {
            foreach (var pair in _byInputNetwork)
            {
                if (pair.Value.Tick < currentTick - 2)
                {
                    _byInputNetwork.TryRemove(pair.Key, out _);
                }
            }
        }

        // Defensive escape-hatch for callers that change priority + immediately need
        // a recomputation on the next GetAllocatedSupply (probes, tests, or some
        // future race-prone code path). Normal flow relies on PriorityStore.Version
        // bumping which the cache key honours; this method drops the entire cache
        // unconditionally.
        internal static void InvalidateAll()
        {
            _byInputNetwork.Clear();
        }

        // Tracks the set of currently-shedding device IDs at the host so the
        // SyncTransitions sweep can detect deltas tick-to-tick and broadcast a
        // ShedStateMessage on every change. Keyed by Transformer.ReferenceId.
        private static readonly System.Collections.Generic.HashSet<long> _lastTickShedSet =
            new System.Collections.Generic.HashSet<long>();
        private static readonly object _shedSetLock = new object();

        // Called from ElectricityTickPatches.ElectricityTickPrefix after the
        // tick counter advances and the per-network caches are trimmed. Walks
        // BrownoutRegistry.CurrentlyLockedOut and diffs against the snapshot
        // from the previous tick; broadcasts ShedStateMessage{true} for entries,
        // ShedStateMessage{false} for exits. Host-only; no-op on a client or
        // when shedding is disabled.
        internal static void SyncShedTransitions(int currentTick)
        {
            if (!Assets.Scripts.Networking.NetworkManager.IsActive) return;
            if (!Assets.Scripts.Networking.NetworkManager.IsServer) return;

            var current = new System.Collections.Generic.HashSet<long>();
            foreach (var id in BrownoutRegistry.CurrentlyLockedOut(currentTick))
                current.Add(id);

            lock (_shedSetLock)
            {
                // Entries: in current, not in last.
                foreach (var id in current)
                {
                    if (!_lastTickShedSet.Contains(id))
                    {
                        new ShedStateMessage { DeviceId = id, Shedding = true }.SendAll(0L);
                    }
                }
                // Exits: in last, not in current.
                foreach (var id in _lastTickShedSet)
                {
                    if (!current.Contains(id))
                    {
                        new ShedStateMessage { DeviceId = id, Shedding = false }.SendAll(0L);
                    }
                }
                _lastTickShedSet.Clear();
                foreach (var id in current) _lastTickShedSet.Add(id);
            }
        }
    }
}
