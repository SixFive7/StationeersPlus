using System.Collections.Concurrent;
using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;

namespace PowerGridPlus
{
    // Per-Transformer dispatch-priority value. Keyed by Thing.ReferenceId, value
    // is a non-negative int (default 100). Used by TransformerAllocator's strict-
    // priority allocation: a higher priority gets first dibs on input-network
    // supply; lower priorities get the leftover; a transformer that cannot get
    // its full OutputMaximum is deprioritized for 10 seconds (DeprioritizedRegistry).
    //
    // Persistence: PrioritySideCar reads / writes a separate side-car XML inside
    // the save ZIP. PrioritySaveLoadPatches restores state in Thing.OnFinishedLoad
    // for every Transformer.
    //
    // Server-authoritative: the host owns this store; client writes (IC10 chip
    // and tablet) reach the host via SetLogicValueMessage / SetLogicFromClient
    // and reroute through TransformerPriorityLogicPatches.SetLogicValuePatch
    // which is server-gated. The host broadcasts changes via PriorityMessage.
    internal static class PriorityStore
    {
        internal const int DefaultPriority = 100;

        private static readonly ConcurrentDictionary<long, int> _byReference =
            new ConcurrentDictionary<long, int>();

        // Monotonic version counter incremented on every write. TransformerAllocator
        // includes it in its per-network cache key so that intra-tick priority
        // changes (an IC10 chip writes Priority and then reads DeprioritizedFault in the same
        // tick; the join-suffix restore on a late-joining client; the side-car
        // restore in Thing.OnFinishedLoad) invalidate stale allocations without
        // requiring a per-tick lazy-clear.
        internal static int Version { get; private set; }
        private static void BumpVersion() { Version++; }

        internal static int GetPriority(Thing thing)
        {
            if (thing == null) return DefaultPriority;
            if (_byReference.TryGetValue(thing.ReferenceId, out var p))
                return p;
            return DefaultPriority;
        }

        internal static int GetPriority(long referenceId)
        {
            if (_byReference.TryGetValue(referenceId, out var p))
                return p;
            return DefaultPriority;
        }

        internal static void SetPriority(Thing thing, int priority)
        {
            if (thing == null) return;
            _byReference[thing.ReferenceId] = priority < 0 ? 0 : priority;
            BumpVersion();
        }

        internal static void SetPriorityByReference(long referenceId, int priority)
        {
            _byReference[referenceId] = priority < 0 ? 0 : priority;
            BumpVersion();
        }

        internal static IEnumerable<KeyValuePair<long, int>> SnapshotEntries()
        {
            foreach (var pair in _byReference) yield return pair;
        }

        internal static void RestoreFromSideCar(long referenceId, int priority)
        {
            SetPriorityByReference(referenceId, priority);
        }

        // Also bumped from DeprioritizedRegistry deprioritized-state changes so a freshly-deprioritized
        // or freshly-released transformer re-allocates immediately, not on next tick.
        internal static void BumpVersionExternal() { BumpVersion(); }
    }
}
