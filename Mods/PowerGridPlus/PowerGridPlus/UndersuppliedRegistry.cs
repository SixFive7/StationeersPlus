using System.Collections.Concurrent;
using System.Collections.Generic;

namespace PowerGridPlus
{
    /// <summary>
    ///     The player-facing face of the DEAD_UNMET verdict (decision 33; the name Undersupplied
    ///     was approved by the user): a network whose rigid demand cannot be fully funded by the
    ///     supply that reaches it, with shedding exhausted, goes dark whole and holds 60 s. Until
    ///     now that state had no cue at all. Every device on such a network shows the amber
    ///     "Undersupplied" info block: the needs-vs-delivers numbers plus a pointer at the
    ///     strongest feeding segmenter, so a dark room diagnoses itself. NOT a fault lockout: no
    ///     timer to display, no flash, instant recovery when supply returns; the verdict machinery
    ///     itself (NetLiveness) is unchanged.
    ///
    ///     <para>Keyed by NETWORK ReferenceId (the state is per-net, not per-device). Rebuilt
    ///     every allocator pass at the liveness tail; mirrored to clients via the per-tick fault
    ///     snapshot (<see cref="FaultRegistrySnapshotMessage.KindUndersupplied"/>) with the
    ///     DeadInputRegistry keep-alive TTL shape (the carried int is a TTL, not a countdown),
    ///     and intentionally not in the join handshake (the first heartbeat refreshes it within a
    ///     tick).</para>
    /// </summary>
    internal static class UndersuppliedRegistry
    {
        // Client-mirror keep-alive: the host sends the full set every tick while non-empty, so
        // 2 ticks (1 s) comfortably bridges one heartbeat.
        private const int HeartbeatTtlTicks = 2;

        internal struct Info
        {
            public float NeedsW;      // the net's rigid want (own machines + active claims)
            public float AvailW;      // the supply that actually reached the net
            public long FeederRefId;  // the strongest supplier seg; 0 when none (generator-only net)
        }

        // Server: netId -> info this tick. Rebuilt every allocator pass.
        private static readonly ConcurrentDictionary<long, Info> _serverByNet =
            new ConcurrentDictionary<long, Info>();

        private struct ClientEntry
        {
            public long ExpiryMs;
            public Info Info;
        }

        // Client: netId -> MonotonicClock expiry + info, replaced by each received snapshot.
        private static readonly ConcurrentDictionary<long, ClientEntry> _clientByNet =
            new ConcurrentDictionary<long, ClientEntry>();

        private static bool IsClientPeer =>
            Assets.Scripts.Networking.NetworkManager.IsActive
            && !Assets.Scripts.Networking.NetworkManager.IsServer;

        /// <summary>Server: clear the set at the start of an allocator pass, before re-marking.</summary>
        internal static void BeginServerPass() => _serverByNet.Clear();

        internal static void MarkUndersupplied(long netId, float needsW, float availW, long feederRefId)
            => _serverByNet[netId] = new Info { NeedsW = needsW, AvailW = availW, FeederRefId = feederRefId };

        /// <summary>Undersupplied info for a network (host: live set; client: synced mirror).</summary>
        internal static bool TryGet(long netId, out Info info)
        {
            if (IsClientPeer)
            {
                if (_clientByNet.TryGetValue(netId, out var e) && e.ExpiryMs > MonotonicClock.NowMs)
                {
                    info = e.Info;
                    return true;
                }
                info = default;
                return false;
            }
            return _serverByNet.TryGetValue(netId, out info);
        }

        /// <summary>Server: per-tick heartbeat snapshot. The int is a keep-alive TTL, not a countdown.</summary>
        internal static IEnumerable<(long netId, int ttl, float needsW, float availW, long feederRefId)> SnapshotRemaining()
        {
            foreach (var kv in _serverByNet)
                yield return (kv.Key, HeartbeatTtlTicks, kv.Value.NeedsW, kv.Value.AvailW, kv.Value.FeederRefId);
        }

        /// <summary>Client: replace the mirror from a received snapshot.</summary>
        internal static void ReplaceClientSnapshot(List<(long netId, int ttl, float needsW, float availW, long feederRefId)> entries)
        {
            _clientByNet.Clear();
            long now = MonotonicClock.NowMs;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].ttl <= 0) continue;
                _clientByNet[entries[i].netId] = new ClientEntry
                {
                    ExpiryMs = now + entries[i].ttl * 500L,
                    Info = new Info
                    {
                        NeedsW = entries[i].needsW,
                        AvailW = entries[i].availW,
                        FeederRefId = entries[i].feederRefId,
                    },
                };
            }
        }

        internal static int Count => _serverByNet.Count;

        internal static void ClearAll()
        {
            _serverByNet.Clear();
            _clientByNet.Clear();
        }
    }
}
