using System.Collections.Concurrent;
using System.Collections.Generic;
using Assets.Scripts.Objects;

namespace PowerGridPlus
{
    // Deprioritization (upstream-side protection) lockout state machine for segmenting devices:
    // when transformers competing for the same input network cannot all be served, the allocator
    // serves them in Priority order and the losers are deprioritized (turned off) for the lockout
    // window. Per-device (keyed by Thing.ReferenceId) entry: the electricity-tick counter value
    // AFTER WHICH the lockout expires, plus the hover payload captured at the deciding site.
    //
    // Lockout fires instantly on first detection (no shortfall counter). The
    // atomic 5-phase electricity tick (AtomicElectricityTickPatch) gives the
    // allocator fresh in-tick supply/demand data, so a 1-tick-blip tolerance
    // is unnecessary: if the allocator sees a real shortfall this tick, the
    // shortfall is real, and the 60-second lockout fires immediately.
    //
    // Hover payload (locked template "Needs D while U competes for S upstream"): NeedsW is the
    // victim's own rigid pull, UpstreamDemandW the input network's total rigid want at the
    // deciding round, UpstreamSupplyW the supply that network could actually raise. The triple
    // rides the per-tick snapshots and the join suffix (the CurrentMismatchFaultRegistry
    // violator-names precedent, numeric edition).
    //
    // Client mirror: hosts decide; clients receive per-tick full snapshots
    // (FaultRegistrySnapshotMessage) carrying REMAINING ticks per device, and
    // store the expiry against the local monotonic clock (MonotonicClock.NowMs)
    // because the electricity-tick counter does not advance on a non-simulating
    // peer. Entries self-expire; a missed packet self-heals on the next
    // snapshot (POWER.md §13 heartbeat model).
    //
    // Persistence: NOT persisted. A lockout in progress at save time clears on
    // load; recompute fires on the first tick after load.
    internal static class DeprioritizedRegistry
    {
        internal const int LockoutDurationTicks = 120;     // 60 seconds at 2 Hz

        private struct HostEntry
        {
            public int UntilTick;
            public float NeedsW;
            public float UpstreamDemandW;
            public float UpstreamSupplyW;
        }

        private struct ClientEntry
        {
            public long ExpiryMs;
            public float NeedsW;
            public float UpstreamDemandW;
            public float UpstreamSupplyW;
        }

        private static readonly ConcurrentDictionary<long, HostEntry> _lockoutUntilTick =
            new ConcurrentDictionary<long, HostEntry>();

        // Client mirror: refId -> expiry in MonotonicClock milliseconds plus the payload.
        private static readonly ConcurrentDictionary<long, ClientEntry> _clientExpiry =
            new ConcurrentDictionary<long, ClientEntry>();

        private static bool IsClientPeer =>
            Assets.Scripts.Networking.NetworkManager.IsActive
            && !Assets.Scripts.Networking.NetworkManager.IsServer;

        // Snapshot receive: replace the whole mirror (self-healing full state).
        internal static void ReplaceClientSnapshot(
            List<(long refId, int remainingTicks, float needsW, float upstreamDemandW, float upstreamSupplyW)> entries)
        {
            _clientExpiry.Clear();
            long now = MonotonicClock.NowMs;
            for (int i = 0; i < entries.Count; i++)
            {
                var (refId, remaining, needsW, upstreamDemandW, upstreamSupplyW) = entries[i];
                if (remaining <= 0) continue;
                _clientExpiry[refId] = new ClientEntry
                {
                    ExpiryMs = now + remaining * 500L,   // 2 Hz tick = 500 ms
                    NeedsW = needsW,
                    UpstreamDemandW = upstreamDemandW,
                    UpstreamSupplyW = upstreamSupplyW,
                };
            }
        }

        // Host snapshot for the per-tick full sync and the join suffix:
        // (refId, remainingTicks, needsW, upstreamDemandW, upstreamSupplyW).
        internal static IEnumerable<(long refId, int remainingTicks, float needsW, float upstreamDemandW, float upstreamSupplyW)>
            SnapshotRemaining(int currentTick)
        {
            foreach (var pair in _lockoutUntilTick)
            {
                int remaining = pair.Value.UntilTick - currentTick;
                if (remaining > 0)
                    yield return (pair.Key, remaining, pair.Value.NeedsW,
                        pair.Value.UpstreamDemandW, pair.Value.UpstreamSupplyW);
            }
        }

        internal static IEnumerable<long> CurrentlyLockedOut(int currentTick)
        {
            foreach (var pair in _lockoutUntilTick)
            {
                if (pair.Value.UntilTick > currentTick)
                    yield return pair.Key;
            }
        }

        // True when the device is currently inside a deprioritization-lockout window (host-side).
        internal static bool IsLockedOut(long referenceId, int currentTick)
        {
            if (!_lockoutUntilTick.TryGetValue(referenceId, out var entry)) return false;
            if (currentTick < entry.UntilTick) return true;
            // Lockout expired; clear so the next allocation can re-engage.
            _lockoutUntilTick.TryRemove(referenceId, out _);
            return false;
        }

        internal static bool IsLockedOut(Thing thing, int currentTick)
        {
            if (thing == null) return false;
            return IsLockedOut(thing.ReferenceId, currentTick);
        }

        // Fires the 60-second lockout immediately, with the hover payload captured at the
        // allocator's deciding site.
        internal static void NoteDeprioritized(long referenceId, int currentTick,
            float needsW, float upstreamDemandW, float upstreamSupplyW)
        {
            _lockoutUntilTick[referenceId] = new HostEntry
            {
                UntilTick = currentTick + LockoutDurationTicks,
                NeedsW = needsW,
                UpstreamDemandW = upstreamDemandW,
                UpstreamSupplyW = upstreamSupplyW,
            };
        }

        // Payload-less convenience form (fixtures and synthetic lockouts): the ScenarioRunner
        // chain fixture reflection-invokes this exact (long, int) signature; keep it stable.
        internal static void NoteDeprioritized(long referenceId, int currentTick)
            => NoteDeprioritized(referenceId, currentTick, 0f, 0f, 0f);

        // External read of the deprioritized flag for IC10 DeprioritizedFault and the
        // visuals. On a non-server peer the host dict is empty; read the mirror.
        internal static bool IsDeprioritized(long referenceId, int currentTick)
        {
            if (IsClientPeer)
                return _clientExpiry.TryGetValue(referenceId, out var entry) && entry.ExpiryMs > MonotonicClock.NowMs;
            return IsLockedOut(referenceId, currentTick);
        }

        // Remaining seconds + the (needs, upstream demand, upstream supply) payload for the hover
        // block, peer-aware. Host-side the tick remainder is smoothed with the intra-tick
        // wall-clock offset so the displayed value ticks down continuously; the authoritative
        // expiry stays tick-based.
        internal static bool TryGetFault(long referenceId, int currentTick, out float secondsLeft,
            out float needsW, out float upstreamDemandW, out float upstreamSupplyW)
        {
            if (IsClientPeer)
            {
                if (_clientExpiry.TryGetValue(referenceId, out var entry))
                {
                    long left = entry.ExpiryMs - MonotonicClock.NowMs;
                    if (left > 0)
                    {
                        secondsLeft = left / 1000f;
                        needsW = entry.NeedsW;
                        upstreamDemandW = entry.UpstreamDemandW;
                        upstreamSupplyW = entry.UpstreamSupplyW;
                        return true;
                    }
                }
                secondsLeft = 0f;
                needsW = 0f;
                upstreamDemandW = 0f;
                upstreamSupplyW = 0f;
                return false;
            }
            if (_lockoutUntilTick.TryGetValue(referenceId, out var host) && host.UntilTick > currentTick)
            {
                secondsLeft = ElectricityTickCounter.SmoothSeconds(host.UntilTick - currentTick);
                needsW = host.NeedsW;
                upstreamDemandW = host.UpstreamDemandW;
                upstreamSupplyW = host.UpstreamSupplyW;
                return true;
            }
            secondsLeft = 0f;
            needsW = 0f;
            upstreamDemandW = 0f;
            upstreamSupplyW = 0f;
            return false;
        }

        internal static int LockoutCount => _lockoutUntilTick.Count;

        // Registry hygiene (RegistryHygiene sweep): remove host entries whose lockout expired
        // (IsLockedOut self-cleans on read, but an entry nobody reads again leaks) or whose device
        // no longer exists (Thing.Find is a plain dictionary lookup, worker-safe; the null test is
        // a reference test, never the Unity lifetime operator). Returns counts via out params.
        internal static void PruneStale(int currentTick, out int expired, out int destroyed)
        {
            expired = 0;
            destroyed = 0;
            foreach (var pair in _lockoutUntilTick)
            {
                if (pair.Value.UntilTick <= currentTick)
                {
                    if (_lockoutUntilTick.TryRemove(pair.Key, out _)) expired++;
                }
                else if (Assets.Scripts.Objects.Thing.Find(pair.Key) is null)
                {
                    if (_lockoutUntilTick.TryRemove(pair.Key, out _)) destroyed++;
                }
            }
        }

        // Defensive escape-hatch for tests / world load. Drops every entry.
        internal static void ClearAll()
        {
            _lockoutUntilTick.Clear();
            _clientExpiry.Clear();
        }

        // User-initiated reset (OFF-as-reset): drop the lockout for one device on
        // whichever peer this runs on. The next allocator pass re-decides.
        internal static void ClearLockout(long referenceId)
        {
            _lockoutUntilTick.TryRemove(referenceId, out _);
            _clientExpiry.TryRemove(referenceId, out _);
        }
    }
}
