using System.Collections.Concurrent;
using System.Collections.Generic;

namespace PowerGridPlus
{
    /// <summary>
    ///     The player-facing faces of the two dark-net verdicts (decision 33; names approved by
    ///     the user). A network that WANTS power but is dark explains itself on every device it
    ///     carries:
    ///
    ///     <para><b>Undersupplied</b> (DEAD_UNMET, amber): supply reaches the net but cannot fund
    ///     its rigid demand, shedding exhausted; the 60 s hold rhythm applies.</para>
    ///
    ///     <para><b>No power source</b> (DEAD_NOSUPPLY, grey): nothing energized reaches the net
    ///     at all this tick (no inflow, no local generation, no unlocked store); recovers the
    ///     tick supply returns. Covers the store-in-lockout room that previously showed nothing
    ///     (user decision 2026-07-18).</para>
    ///
    ///     <para>Both faces carry ALL source components (user decision 2026-07-18, "be complete
    ///     on all possible sources"): upstream inflow through feeder segmenters, local generator
    ///     supply, and local store discharge available (0 while a store is locked out), plus a
    ///     pointer at the strongest feeder (a LOCKED feeder is named when no live one exists; the
    ///     locked device is exactly the thing to go look at). Consumerless dark nets are not
    ///     marked (an empty wire run needs no face).</para>
    ///
    ///     <para>NOT a fault lockout: no timer to display, no flash; the verdict machinery itself
    ///     (NetLiveness) is unchanged. Keyed by NETWORK ReferenceId; rebuilt every allocator pass
    ///     at the liveness tail; mirrored to clients via the per-tick fault snapshot
    ///     (<see cref="FaultRegistrySnapshotMessage.KindUndersupplied"/>) with the
    ///     DeadInputRegistry keep-alive TTL shape (the carried int is a TTL, not a countdown),
    ///     and intentionally not in the join handshake (the first heartbeat refreshes it within a
    ///     tick).</para>
    /// </summary>
    internal static class UndersuppliedRegistry
    {
        internal const byte FaceUndersupplied = 0;   // DEAD_UNMET: fed but short
        internal const byte FaceNoPowerSource = 1;   // DEAD_NOSUPPLY: nothing energized reaches it

        // Client-mirror keep-alive: the host sends the full set every tick while non-empty, so
        // 2 ticks (1 s) comfortably bridges one heartbeat.
        private const int HeartbeatTtlTicks = 2;

        internal struct Info
        {
            public byte Face;         // FaceUndersupplied / FaceNoPowerSource
            public float NeedsW;      // the net's rigid want (own machines + active claims)
            public float UpstreamW;   // inflow committed through feeder segmenters this tick
            public float GenW;        // local generator supply on the net
            public float StorageW;    // local store discharge available (0 while locked out)
            public long FeederRefId;  // strongest feeder seg (locked fallback); 0 when none
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

        internal static void MarkNet(long netId, byte face, float needsW,
            float upstreamW, float genW, float storageW, long feederRefId)
            => _serverByNet[netId] = new Info
            {
                Face = face,
                NeedsW = needsW,
                UpstreamW = upstreamW,
                GenW = genW,
                StorageW = storageW,
                FeederRefId = feederRefId,
            };

        /// <summary>Dark-net face for a network (host: live set; client: synced mirror).</summary>
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
        internal static IEnumerable<(long netId, int ttl, byte face, float needsW, float upstreamW, float genW, float storageW, long feederRefId)> SnapshotRemaining()
        {
            foreach (var kv in _serverByNet)
                yield return (kv.Key, HeartbeatTtlTicks, kv.Value.Face, kv.Value.NeedsW,
                    kv.Value.UpstreamW, kv.Value.GenW, kv.Value.StorageW, kv.Value.FeederRefId);
        }

        /// <summary>Client: replace the mirror from a received snapshot.</summary>
        internal static void ReplaceClientSnapshot(List<(long netId, int ttl, byte face, float needsW, float upstreamW, float genW, float storageW, long feederRefId)> entries)
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
                        Face = entries[i].face,
                        NeedsW = entries[i].needsW,
                        UpstreamW = entries[i].upstreamW,
                        GenW = entries[i].genW,
                        StorageW = entries[i].storageW,
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
