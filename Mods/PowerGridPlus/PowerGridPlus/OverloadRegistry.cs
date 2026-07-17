using System.Collections.Concurrent;
using System.Collections.Generic;
using Assets.Scripts.Objects;

namespace PowerGridPlus
{
    // Overload (downstream-side capacity protection) lockout state machine for
    // segmenting devices and elastic suppliers. Parallel API to DeprioritizedRegistry:
    // same shape, same lockout duration, separate dictionary so the protection
    // modes are independently observable and the hover error can name the
    // specific cause.
    //
    // This registry carries the CAPACITY overload family only: the POWER.md 8.4
    // hit-max rule (a Setting-limited supplier whose output network's rigid
    // desire exceeds the suppliers' combined deliverable cap) and its elastic
    // analog (a storage bank at full effective discharge with rigid demand still
    // unmet). The 5.7 cable-overflow rule publishes into CableOverloadRegistry
    // instead, so the two overload kinds stay independently observable.
    // Detection is per-tick atomic in ALLOCATE; lockout fires instantly (no
    // tolerance counter).
    //
    // Each entry additionally records the rigid draw that tripped the rule
    // (ValueW), the combined deliverable cap the rule computed (CapW), and the
    // internal-storage (battery/elastic) component of that cap (StorageW) so the
    // hover can split the cap into its upstream part (CapW - StorageW) and its
    // storage part; the payload rides the per-tick snapshots and the join suffix
    // (the CurrentMismatchFaultRegistry violator-names precedent).
    //
    // Client mirror model identical to DeprioritizedRegistry: per-tick full snapshots
    // carry remaining ticks + payload; the client stores expiry against
    // MonotonicClock.
    //
    // Persistence: not persisted. Cross-network state, follows the device.
    internal static class OverloadRegistry
    {
        internal const int LockoutDurationTicks = DeprioritizedRegistry.LockoutDurationTicks;

        private struct HostEntry
        {
            public int UntilTick;
            public float ValueW;
            public float CapW;
            public float StorageW;
        }

        private struct ClientEntry
        {
            public long ExpiryMs;
            public float ValueW;
            public float CapW;
            public float StorageW;
        }

        private static readonly ConcurrentDictionary<long, HostEntry> _lockoutUntilTick =
            new ConcurrentDictionary<long, HostEntry>();

        private static readonly ConcurrentDictionary<long, ClientEntry> _clientExpiry =
            new ConcurrentDictionary<long, ClientEntry>();

        private static bool IsClientPeer =>
            Assets.Scripts.Networking.NetworkManager.IsActive
            && !Assets.Scripts.Networking.NetworkManager.IsServer;

        internal static void ReplaceClientSnapshot(List<(long refId, int remainingTicks, float valueW, float capW, float storageW)> entries)
        {
            _clientExpiry.Clear();
            long now = MonotonicClock.NowMs;
            for (int i = 0; i < entries.Count; i++)
            {
                var (refId, remaining, valueW, capW, storageW) = entries[i];
                if (remaining <= 0) continue;
                _clientExpiry[refId] = new ClientEntry
                {
                    ExpiryMs = now + remaining * 500L,
                    ValueW = valueW,
                    CapW = capW,
                    StorageW = storageW,
                };
            }
        }

        // Host snapshot for the per-tick full sync and the join suffix:
        // (refId, remainingTicks, valueW, capW, storageW).
        internal static IEnumerable<(long refId, int remainingTicks, float valueW, float capW, float storageW)> SnapshotRemaining(int currentTick)
        {
            foreach (var pair in _lockoutUntilTick)
            {
                int remaining = pair.Value.UntilTick - currentTick;
                if (remaining > 0)
                    yield return (pair.Key, remaining, pair.Value.ValueW, pair.Value.CapW, pair.Value.StorageW);
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

        internal static void NoteOverload(long referenceId, int currentTick, float valueW, float capW, float storageW)
        {
            _lockoutUntilTick[referenceId] = new HostEntry
            {
                UntilTick = currentTick + LockoutDurationTicks,
                ValueW = valueW,
                CapW = capW,
                StorageW = storageW,
            };
        }

        internal static bool IsOverloaded(long referenceId, int currentTick)
        {
            if (IsClientPeer)
                return _clientExpiry.TryGetValue(referenceId, out var entry) && entry.ExpiryMs > MonotonicClock.NowMs;
            return IsLockedOut(referenceId, currentTick);
        }

        // Remaining seconds + the (value, cap, storage) payload for the hover lines, peer-aware.
        internal static bool TryGetFault(long referenceId, int currentTick,
            out float secondsLeft, out float valueW, out float capW, out float storageW)
        {
            if (IsClientPeer)
            {
                if (_clientExpiry.TryGetValue(referenceId, out var entry))
                {
                    long left = entry.ExpiryMs - MonotonicClock.NowMs;
                    if (left > 0)
                    {
                        secondsLeft = left / 1000f;
                        valueW = entry.ValueW;
                        capW = entry.CapW;
                        storageW = entry.StorageW;
                        return true;
                    }
                }
                secondsLeft = 0f;
                valueW = 0f;
                capW = 0f;
                storageW = 0f;
                return false;
            }
            if (_lockoutUntilTick.TryGetValue(referenceId, out var host) && host.UntilTick > currentTick)
            {
                secondsLeft = ElectricityTickCounter.SmoothSeconds(host.UntilTick - currentTick);
                valueW = host.ValueW;
                capW = host.CapW;
                storageW = host.StorageW;
                return true;
            }
            secondsLeft = 0f;
            valueW = 0f;
            capW = 0f;
            storageW = 0f;
            return false;
        }

        internal static int LockoutCount => _lockoutUntilTick.Count;

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

        internal static void ClearAll()
        {
            _lockoutUntilTick.Clear();
            _clientExpiry.Clear();
        }

        internal static void ClearLockout(long referenceId)
        {
            _lockoutUntilTick.TryRemove(referenceId, out _);
            _clientExpiry.TryRemove(referenceId, out _);
        }
    }
}
