using System.Collections.Concurrent;
using System.Collections.Generic;
using Assets.Scripts.Objects;

namespace PowerGridPlus
{
    // CABLE_OVERLOADED lockout state machine: the cable-overflow half of the former
    // single overload fault (POWER.md 5.7 rule 3). The suppliers could deliver the
    // flow, but the output network's weakest cable cannot carry it, so every supplier
    // and elastic on that network goes offline instead of burning the cable.
    // OverloadRegistry keeps the capacity half (a Setting-limited supplier or an
    // elastic bank that cannot cover downstream demand); the two registries are
    // independently observable so the hover, the flash resolution, and the IC10
    // CableOverloaded slot can name the specific cause.
    //
    // Parallel API to OverloadRegistry: same 120-tick lockout, same heartbeat /
    // SnapshotRemaining / ReplaceClientSnapshot surface, same hygiene sweep. Each
    // entry additionally records the flow that tripped the rule (FlowW) and the
    // network's weakest-cable cap (CapW) so the hover can show the numbers; the
    // payload rides the per-tick snapshots and the join suffix (the
    // CurrentMismatchFaultRegistry violator-names precedent).
    //
    // Client mirror model identical to DeprioritizedRegistry: per-tick full snapshots
    // carry remaining ticks + payload; the client stores expiry against
    // MonotonicClock.
    //
    // Persistence: not persisted. Cross-network state, follows the device.
    internal static class CableOverloadRegistry
    {
        internal const int LockoutDurationTicks = DeprioritizedRegistry.LockoutDurationTicks;

        private struct HostEntry
        {
            public int UntilTick;
            public float FlowW;
            public float CapW;
        }

        private struct ClientEntry
        {
            public long ExpiryMs;
            public float FlowW;
            public float CapW;
        }

        private static readonly ConcurrentDictionary<long, HostEntry> _lockoutUntilTick =
            new ConcurrentDictionary<long, HostEntry>();

        private static readonly ConcurrentDictionary<long, ClientEntry> _clientExpiry =
            new ConcurrentDictionary<long, ClientEntry>();

        private static bool IsClientPeer =>
            Assets.Scripts.Networking.NetworkManager.IsActive
            && !Assets.Scripts.Networking.NetworkManager.IsServer;

        internal static void NoteCableOverload(long referenceId, int currentTick, float flowW, float capW)
        {
            _lockoutUntilTick[referenceId] = new HostEntry
            {
                UntilTick = currentTick + LockoutDurationTicks,
                FlowW = flowW,
                CapW = capW,
            };
        }

        internal static bool IsLockedOut(long referenceId, int currentTick)
        {
            if (!_lockoutUntilTick.TryGetValue(referenceId, out var entry)) return false;
            if (currentTick < entry.UntilTick) return true;
            _lockoutUntilTick.TryRemove(referenceId, out _);
            return false;
        }

        internal static bool IsLockedOut(Thing thing, int currentTick)
        {
            if (thing == null) return false;
            return IsLockedOut(thing.ReferenceId, currentTick);
        }

        internal static bool IsCableOverloaded(long referenceId, int currentTick)
        {
            if (IsClientPeer)
                return _clientExpiry.TryGetValue(referenceId, out var entry) && entry.ExpiryMs > MonotonicClock.NowMs;
            return IsLockedOut(referenceId, currentTick);
        }

        // Remaining seconds + the (flow, cap) payload for the hover lines, peer-aware.
        internal static bool TryGetFault(long referenceId, int currentTick,
            out float secondsLeft, out float flowW, out float capW)
        {
            if (IsClientPeer)
            {
                if (_clientExpiry.TryGetValue(referenceId, out var entry))
                {
                    long left = entry.ExpiryMs - MonotonicClock.NowMs;
                    if (left > 0)
                    {
                        secondsLeft = left / 1000f;
                        flowW = entry.FlowW;
                        capW = entry.CapW;
                        return true;
                    }
                }
                secondsLeft = 0f;
                flowW = 0f;
                capW = 0f;
                return false;
            }
            if (_lockoutUntilTick.TryGetValue(referenceId, out var host) && host.UntilTick > currentTick)
            {
                secondsLeft = ElectricityTickCounter.SmoothSeconds(host.UntilTick - currentTick);
                flowW = host.FlowW;
                capW = host.CapW;
                return true;
            }
            secondsLeft = 0f;
            flowW = 0f;
            capW = 0f;
            return false;
        }

        // Host snapshot for the per-tick full sync and the join suffix:
        // (refId, remainingTicks, flowW, capW).
        internal static IEnumerable<(long refId, int remainingTicks, float flowW, float capW)> SnapshotRemaining(int currentTick)
        {
            foreach (var pair in _lockoutUntilTick)
            {
                int remaining = pair.Value.UntilTick - currentTick;
                if (remaining > 0)
                    yield return (pair.Key, remaining, pair.Value.FlowW, pair.Value.CapW);
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

        internal static void ReplaceClientSnapshot(List<(long refId, int remainingTicks, float flowW, float capW)> entries)
        {
            _clientExpiry.Clear();
            long now = MonotonicClock.NowMs;
            for (int i = 0; i < entries.Count; i++)
            {
                var (refId, remaining, flowW, capW) = entries[i];
                if (remaining <= 0) continue;
                _clientExpiry[refId] = new ClientEntry
                {
                    ExpiryMs = now + remaining * 500L,
                    FlowW = flowW,
                    CapW = capW,
                };
            }
        }

        internal static void ClearLockout(long referenceId)
        {
            _lockoutUntilTick.TryRemove(referenceId, out _);
            _clientExpiry.TryRemove(referenceId, out _);
        }

        internal static void ClearAll()
        {
            _lockoutUntilTick.Clear();
            _clientExpiry.Clear();
        }

        // Registry hygiene (RegistryHygiene sweep): remove host entries whose lockout expired or
        // whose device no longer exists. Same shape and safety notes as DeprioritizedRegistry.PruneStale.
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
                else if (Thing.Find(pair.Key) is null)
                {
                    if (_lockoutUntilTick.TryRemove(pair.Key, out _)) destroyed++;
                }
            }
        }

        internal static int LockoutCount => _lockoutUntilTick.Count;
    }
}
