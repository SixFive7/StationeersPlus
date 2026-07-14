using System.Collections.Concurrent;
using System.Collections.Generic;
using Assets.Scripts.Objects;

namespace PowerGridPlus
{
    // CYCLE_FAULT lockout state machine for segmenting devices (POWER.md §4.5).
    // Parallel API to OverloadRegistry and DeprioritizedRegistry: same shape, same
    // lockout duration constant, separate dictionary so each protection mode is
    // independently observable and the hover error can name the specific cause.
    //
    // A segmenting device enters CYCLE_FAULT when PROTECT (cycle detection)'s directed-SCC walk
    // (CycleGraphBuilder) finds it on a powered closed power loop. Every member
    // contributes 0 on both terminals for the lockout window, dissolving the
    // loop without burning a cable.
    //
    // Client mirror model identical to DeprioritizedRegistry: per-tick full snapshots
    // carry remaining ticks; the client stores expiry against MonotonicClock.
    //
    // Persistence: not persisted. Cross-network state, follows the device.
    internal static class CycleFaultRegistry
    {
        // Literal 120, do NOT derive from a tick-rate constant.
        // Assumes 2 Hz electricity tick. If game tick rate changes, review.
        internal const int LockoutDurationTicks = 120;     // 60 seconds at 2 Hz

        private static readonly ConcurrentDictionary<long, int> _lockoutUntilTick =
            new ConcurrentDictionary<long, int>();

        private static readonly ConcurrentDictionary<long, long> _clientExpiryMs =
            new ConcurrentDictionary<long, long>();

        private static bool IsClientPeer =>
            Assets.Scripts.Networking.NetworkManager.IsActive
            && !Assets.Scripts.Networking.NetworkManager.IsServer;

        internal static void NoteCycleFault(long referenceId, int currentTick)
        {
            _lockoutUntilTick[referenceId] = currentTick + LockoutDurationTicks;
        }

        internal static bool IsLockedOut(long referenceId, int currentTick)
        {
            if (!_lockoutUntilTick.TryGetValue(referenceId, out var until)) return false;
            if (currentTick < until) return true;
            _lockoutUntilTick.TryRemove(referenceId, out _);
            return false;
        }

        internal static bool IsLockedOut(Thing thing, int currentTick)
        {
            if (thing == null) return false;
            return IsLockedOut(thing.ReferenceId, currentTick);
        }

        internal static bool IsCycleFaulted(long referenceId, int currentTick)
        {
            if (IsClientPeer)
                return _clientExpiryMs.TryGetValue(referenceId, out var expiry) && expiry > MonotonicClock.NowMs;
            return IsLockedOut(referenceId, currentTick);
        }

        internal static bool TryGetSecondsLeft(long referenceId, int currentTick, out float secondsLeft)
        {
            if (IsClientPeer)
            {
                if (_clientExpiryMs.TryGetValue(referenceId, out var expiry))
                {
                    long left = expiry - MonotonicClock.NowMs;
                    if (left > 0) { secondsLeft = left / 1000f; return true; }
                }
                secondsLeft = 0f;
                return false;
            }
            if (_lockoutUntilTick.TryGetValue(referenceId, out var until) && until > currentTick)
            {
                secondsLeft = ElectricityTickCounter.SmoothSeconds(until - currentTick);
                return true;
            }
            secondsLeft = 0f;
            return false;
        }

        internal static IEnumerable<KeyValuePair<long, int>> SnapshotRemaining(int currentTick)
        {
            foreach (var pair in _lockoutUntilTick)
            {
                int remaining = pair.Value - currentTick;
                if (remaining > 0)
                    yield return new KeyValuePair<long, int>(pair.Key, remaining);
            }
        }

        internal static IEnumerable<long> CurrentlyLockedOut(int currentTick)
        {
            foreach (var pair in _lockoutUntilTick)
            {
                if (pair.Value > currentTick)
                    yield return pair.Key;
            }
        }

        internal static void ReplaceClientSnapshot(List<KeyValuePair<long, int>> remainingTicksByRef)
        {
            _clientExpiryMs.Clear();
            long now = MonotonicClock.NowMs;
            for (int i = 0; i < remainingTicksByRef.Count; i++)
            {
                var pair = remainingTicksByRef[i];
                if (pair.Value <= 0) continue;
                _clientExpiryMs[pair.Key] = now + pair.Value * 500L;
            }
        }

        // User-initiated reset (OFF-as-reset). Drops the lockout for a single
        // device from BOTH dicts, so a player toggling OFF clears the state on
        // whichever peer they are on.
        internal static void ClearLockout(long referenceId)
        {
            _lockoutUntilTick.TryRemove(referenceId, out _);
            _clientExpiryMs.TryRemove(referenceId, out _);
        }

        internal static void ClearAll()
        {
            _lockoutUntilTick.Clear();
            _clientExpiryMs.Clear();
        }

        // Registry hygiene (RegistryHygiene sweep): remove host entries whose lockout expired or
        // whose device no longer exists. Same shape and safety notes as DeprioritizedRegistry.PruneStale.
        internal static void PruneStale(int currentTick, out int expired, out int destroyed)
        {
            expired = 0;
            destroyed = 0;
            foreach (var pair in _lockoutUntilTick)
            {
                if (pair.Value <= currentTick)
                {
                    if (_lockoutUntilTick.TryRemove(pair.Key, out _)) expired++;
                }
                else if (Assets.Scripts.Objects.Thing.Find(pair.Key) is null)
                {
                    if (_lockoutUntilTick.TryRemove(pair.Key, out _)) destroyed++;
                }
            }
        }

        internal static int LockoutCount => _lockoutUntilTick.Count;
    }
}
