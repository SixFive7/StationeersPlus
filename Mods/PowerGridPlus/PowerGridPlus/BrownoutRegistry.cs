using System.Collections.Concurrent;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;

namespace PowerGridPlus
{
    // Transient shed-state machine for transformers. Two cooperating counters per
    // transformer (keyed by Thing.ReferenceId):
    //
    //   _shortfallCount[id]  : consecutive ticks the transformer's
    //                          input-network share was insufficient. Reset to 0
    //                          on any tick where the transformer gets its full
    //                          OutputMaximum.
    //   _lockoutUntilTick[id]: the electricity-tick counter value AFTER WHICH the
    //                          shed expires. Set to currentTick + 20 (10 seconds
    //                          at 2 Hz) when _shortfallCount reaches the
    //                          tolerance threshold.
    //
    // The tick counter increments once per electricity tick via
    // ElectricityTickPatches and ticks identically on host and clients. Multiplayer
    // determinism: both peers run the same strict-priority allocation against
    // already-synced inputs (Priority, OutputMaximum, InputNetwork.PotentialLoad)
    // and reach the same shed conclusion in the same tick.
    //
    // Persistence: NOT persisted. A brownout in progress at save time clears on
    // load; recompute fires on the first tick after load. The vanilla
    // ElectricalInputOutput / Device state is the only thing the save round-trips.
    //
    // Cross-process / cross-network: the registry is shared across all networks
    // because a transformer can be reallocated to a new InputNetwork (cable cut /
    // splice) and we want its shed state to follow the transformer, not the
    // network. ConditionalWeakTable would be cleaner against destroyed
    // transformers but is overkill here; the registry only grows in absolute
    // count, never in concrete size beyond active transformers.
    internal static class BrownoutRegistry
    {
        internal const int ShortfallTolerance = 2;       // ticks of insufficient supply before lockout fires
        internal const int LockoutDurationTicks = 20;     // 10 seconds at 2 Hz

        private static readonly ConcurrentDictionary<long, int> _shortfallCount =
            new ConcurrentDictionary<long, int>();
        private static readonly ConcurrentDictionary<long, int> _lastShortfallTick =
            new ConcurrentDictionary<long, int>();
        private static readonly ConcurrentDictionary<long, int> _lockoutUntilTick =
            new ConcurrentDictionary<long, int>();

        // Client-side replication of host shed states. On a non-server peer, the
        // host's BrownoutRegistry is invisible; ShedStateMessage broadcasts every
        // transition, and the client mirrors them here. IsShedding consults this
        // dict on the client; the lockout-tick comparison runs only on the host.
        private static readonly ConcurrentDictionary<long, bool> _clientShedding =
            new ConcurrentDictionary<long, bool>();

        internal static void SetClientShedding(long referenceId, bool shedding)
        {
            if (shedding) _clientShedding[referenceId] = true;
            else _clientShedding.TryRemove(referenceId, out _);
        }

        internal static bool ClientIsShedding(long referenceId)
        {
            return _clientShedding.TryGetValue(referenceId, out var v) && v;
        }

        // Snapshot of currently-locked-out devices (host-side). Used by
        // TransformerAllocator at tick start to detect transitions and broadcast
        // ShedStateMessage, and by Plugin.SerializeJoinSuffix to ship the current
        // state to a freshly-joining client.
        internal static System.Collections.Generic.IEnumerable<long> CurrentlyLockedOut(int currentTick)
        {
            foreach (var pair in _lockoutUntilTick)
            {
                if (pair.Value > currentTick)
                    yield return pair.Key;
            }
        }

        // True when the transformer is currently inside a lockout window.
        internal static bool IsLockedOut(long referenceId, int currentTick)
        {
            if (!_lockoutUntilTick.TryGetValue(referenceId, out var until)) return false;
            if (currentTick < until) return true;
            // Lockout expired; clear so the next allocation can re-engage.
            _lockoutUntilTick.TryRemove(referenceId, out _);
            return false;
        }

        internal static bool IsLockedOut(Thing thing, int currentTick)
        {
            if (thing == null) return false;
            return IsLockedOut(thing.ReferenceId, currentTick);
        }

        // Called when the transformer DID get its full OutputMaximum this tick.
        // Resets the consecutive-shortfall counter.
        internal static void NoteSupplyOk(long referenceId)
        {
            _shortfallCount.TryRemove(referenceId, out _);
            _lastShortfallTick.TryRemove(referenceId, out _);
        }

        // Called when the transformer COULD NOT get its full OutputMaximum this
        // tick. Tracks consecutive ticks of shortfall; when the count reaches
        // ShortfallTolerance, sets the lockout-until counter and returns true.
        // Non-consecutive shortfalls (a gap of 1+ ticks) reset the counter.
        internal static bool NoteShortfall(long referenceId, int currentTick)
        {
            // If currently locked out, no need to count further; already shed.
            if (IsLockedOut(referenceId, currentTick))
                return true;

            // Consecutive vs reset.
            int newCount;
            if (_lastShortfallTick.TryGetValue(referenceId, out var lastTick)
                && currentTick == lastTick + 1)
            {
                newCount = _shortfallCount.AddOrUpdate(referenceId, 1, (_, v) => v + 1);
            }
            else
            {
                newCount = 1;
                _shortfallCount[referenceId] = 1;
            }
            _lastShortfallTick[referenceId] = currentTick;

            if (newCount >= ShortfallTolerance)
            {
                _lockoutUntilTick[referenceId] = currentTick + LockoutDurationTicks;
                _shortfallCount.TryRemove(referenceId, out _);
                _lastShortfallTick.TryRemove(referenceId, out _);
                return true;
            }
            return false;
        }

        // Test-only / external read of the shedding flag for IC10 LogicType.Shedding.
        // Returns true if either the transformer is in lockout or its current
        // shortfall count has reached the tolerance threshold (shed is imminent).
        // Plain IsLockedOut would miss the "about to shed" tick; reading Shedding
        // via IC10 should report 1 the moment the lockout is established.
        //
        // On a non-server peer, the host-side _lockoutUntilTick dict is empty;
        // fall back to the client-replicated _clientShedding dict.
        internal static bool IsShedding(long referenceId, int currentTick)
        {
            if (Assets.Scripts.Networking.NetworkManager.IsActive
                && !Assets.Scripts.Networking.NetworkManager.IsServer)
            {
                return ClientIsShedding(referenceId);
            }
            return IsLockedOut(referenceId, currentTick);
        }

        // Called from Plugin.OnPrefabsLoaded or ElectricityTickPatches once per
        // some interval to drop stale entries for destroyed transformers. Cheap
        // because the only thing that grows the dictionaries is per-transformer
        // entries.
        internal static int LockoutCount => _lockoutUntilTick.Count;
        internal static int ShortfallCount => _shortfallCount.Count;

        // Defensive escape-hatch for tests / scenarios that need a clean
        // shedding-state baseline. Drops every shortfall counter and lockout
        // entry. Normal flow does not call this (lockouts expire on their own
        // via the tick comparison in IsLockedOut).
        internal static void ClearAll()
        {
            _shortfallCount.Clear();
            _lastShortfallTick.Clear();
            _lockoutUntilTick.Clear();
        }
    }
}
