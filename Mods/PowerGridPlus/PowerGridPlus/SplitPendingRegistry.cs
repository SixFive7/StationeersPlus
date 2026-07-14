using System.Collections.Concurrent;
using Assets.Scripts;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;

namespace PowerGridPlus
{
    /// <summary>
    ///     Tracks cable networks that have a cable burn in flight: a <see cref="Cable.Break"/> has been
    ///     issued (or queued to the main thread) whose network split has not yet landed. Two consumers:
    ///     <list type="number">
    ///       <item><see cref="VoltageTierEnforcer"/> skips re-burning a network whose previous burn is
    ///       still in flight. This replaces the old fixed 4-tick burn cooldown with a state-based gate:
    ///       there is no magic-number timer.</item>
    ///       <item><see cref="PowerAllocator"/> defers committing durable fault lockouts on an in-flight
    ///       network, so a deprioritization / overload decision is never made against a merged topology that is
    ///       about to split (POWER.md Option C).</item>
    ///     </list>
    ///
    ///     <para>"Split landed" is detected by cable-count change, not a timer. <see cref="Cable.Break"/>
    ///     -> (end of frame) <c>Cable.OnDestroy</c> -> <c>CableNetwork.Remove</c> drops the burned cable
    ///     from <c>CableList</c>, so the network's <c>CableList.Count</c> differs from the count captured
    ///     when the burn was issued. (<c>BreakableCables</c> is per-network, so vanilla's random
    ///     <c>Pick()</c> always burns a cable on the same network we marked.) This clears in one tick on a
    ///     healthy server and self-extends only while the server is frame-starved and the split has
    ///     genuinely not landed yet -- exactly the behaviour the fixed cooldown approximated with a
    ///     constant.</para>
    ///
    ///     <para>Host-only state. Burns are server-side (the <see cref="VoltageTier"/> helpers and the
    ///     §5.7 burn are gated on <c>GameManager.RunSimulation</c>); clients receive the authoritative
    ///     split via vanilla's <c>RebuildCableNetworkEvent</c>, so no PowerGridPlus message carries this
    ///     state. Cleared on world load (<see cref="ClearAll"/>).</para>
    /// </summary>
    internal static class SplitPendingRegistry
    {
        // netRefId -> CableList.Count captured at the moment the burn was issued. Presence means
        // "a burn fired on this network; its split has not landed (cable count unchanged)".
        private static readonly ConcurrentDictionary<long, int> _pending =
            new ConcurrentDictionary<long, int>();

        // netRefId -> reserved for a main-thread burn that has not executed yet. Presence prevents a
        // second enqueue for the same network before the first burn runs. Resolved (removed or promoted
        // to _pending) by the enqueued action when it executes on the main thread.
        private static readonly ConcurrentDictionary<long, byte> _queued =
            new ConcurrentDictionary<long, byte>();

        /// <summary>A burn is queued or in flight on this network (split not yet landed).</summary>
        internal static bool IsPending(long netRefId)
            => _queued.ContainsKey(netRefId) || _pending.ContainsKey(netRefId);

        /// <summary>
        ///     Worker thread: reserve a network for a main-thread burn. Returns false if a burn is
        ///     already queued or in flight, so the caller does not enqueue a duplicate.
        /// </summary>
        internal static bool TryReserve(long netRefId)
        {
            if (_pending.ContainsKey(netRefId)) return false;
            return _queued.TryAdd(netRefId, 0);
        }

        /// <summary>
        ///     Main thread: a burn fired on this network. Record the pre-split cable count so
        ///     <see cref="SweepLanded"/> can detect the split landing, and clear any reservation.
        /// </summary>
        internal static void MarkBurned(long netRefId, int cableCountAtBurn)
        {
            _queued.TryRemove(netRefId, out _);
            _pending[netRefId] = cableCountAtBurn;
        }

        /// <summary>
        ///     Main thread: a reserved burn found no violation after all (topology changed between the
        ///     worker detection and the main-thread execution). Release the reservation.
        /// </summary>
        internal static void Release(long netRefId)
        {
            _queued.TryRemove(netRefId, out _);
        }

        /// <summary>
        ///     Each tick (worker-safe): drop entries whose split has landed (cable count changed) or
        ///     whose network has been deregistered. Reading <c>CableList.Count</c> under the network's
        ///     own lock is the only Unity-free, off-thread-safe signal we need.
        /// </summary>
        internal static void SweepLanded()
        {
            foreach (var kv in _pending)
            {
                var net = Referencable.Find<CableNetwork>(kv.Key);
                if (net == null)
                {
                    _pending.TryRemove(kv.Key, out _);
                    continue;
                }
                int count;
                lock (net.CableList) count = net.CableList.Count;
                if (count != kv.Value)
                    _pending.TryRemove(kv.Key, out _);
            }
        }

        /// <summary>Clear all state. Called on world load so a fresh world starts with no stale pendings.</summary>
        internal static void ClearAll()
        {
            _pending.Clear();
            _queued.Clear();
        }
    }
}
