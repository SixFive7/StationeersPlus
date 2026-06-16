using System.Collections.Concurrent;
using System.Collections.Generic;

namespace PowerGridPlus
{
    /// <summary>
    ///     Steady "no upstream supply" cue for a contributor (transformer / APC / PT pair) whose INPUT
    ///     network has no effective supply at all (POWER.md §8.3 dead-input carveout). This is NOT a
    ///     fault lockout: there is no 60 s timer, no flash, no orange/red colour. A dead-input
    ///     contributor idles (it is not shed), so it recovers the instant its input is powered. The
    ///     allocator rebuilds the server set every tick from the converged state; the cue is purely a
    ///     hover hint.
    ///
    ///     <para>Mirrored to clients via the per-tick fault snapshot (<see cref="FaultRegistrySnapshotMessage.KindDeadInput"/>)
    ///     so the hover shows on every peer. The client mirror uses a short MonotonicClock TTL refreshed
    ///     by the heartbeat, the same shape as the lockout registries, but the carried value is only a
    ///     keep-alive, not a countdown (the cue has no timer to display).</para>
    /// </summary>
    internal static class DeadInputRegistry
    {
        // Client-mirror keep-alive: how long a snapshot entry survives without a refresh. The host sends
        // the full set every tick while non-empty, so 2 ticks (1 s) comfortably bridges one heartbeat.
        private const int HeartbeatTtlTicks = 2;

        // Server: contributor ReferenceIds on a dead input this tick. Rebuilt every allocator pass.
        private static readonly ConcurrentDictionary<long, byte> _serverSet =
            new ConcurrentDictionary<long, byte>();

        // Client: refId -> MonotonicClock expiry, replaced by each received snapshot.
        private static readonly ConcurrentDictionary<long, long> _clientExpiryMs =
            new ConcurrentDictionary<long, long>();

        private static bool IsClientPeer =>
            Assets.Scripts.Networking.NetworkManager.IsActive
            && !Assets.Scripts.Networking.NetworkManager.IsServer;

        /// <summary>Server: clear the set at the start of an allocator pass, before re-marking.</summary>
        internal static void BeginServerPass() => _serverSet.Clear();

        internal static void MarkDeadInput(long referenceId) => _serverSet[referenceId] = 0;

        /// <summary>True if this contributor's input network has no upstream supply (host: live set; client: synced mirror).</summary>
        internal static bool IsDeadInput(long referenceId)
        {
            if (IsClientPeer)
                return _clientExpiryMs.TryGetValue(referenceId, out var expiry) && expiry > MonotonicClock.NowMs;
            return _serverSet.ContainsKey(referenceId);
        }

        /// <summary>Server: per-tick heartbeat snapshot. The int is a keep-alive TTL, not a countdown.</summary>
        internal static IEnumerable<KeyValuePair<long, int>> SnapshotRemaining()
        {
            foreach (var kv in _serverSet)
                yield return new KeyValuePair<long, int>(kv.Key, HeartbeatTtlTicks);
        }

        /// <summary>Client: replace the mirror from a received snapshot.</summary>
        internal static void ReplaceClientSnapshot(List<KeyValuePair<long, int>> entries)
        {
            _clientExpiryMs.Clear();
            long now = MonotonicClock.NowMs;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].Value <= 0) continue;
                _clientExpiryMs[entries[i].Key] = now + entries[i].Value * 500L;
            }
        }

        internal static int Count => _serverSet.Count;

        internal static void ClearAll()
        {
            _serverSet.Clear();
            _clientExpiryMs.Clear();
        }
    }
}
