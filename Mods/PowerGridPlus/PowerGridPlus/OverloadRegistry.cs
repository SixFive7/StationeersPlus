using System.Collections.Concurrent;
using System.Collections.Generic;
using Assets.Scripts.Objects;

namespace PowerGridPlus
{
    // Overload (downstream-side protection) lockout state machine for segmenting
    // devices and elastic suppliers. Parallel API to BrownoutRegistry: same shape,
    // same lockout duration, separate dictionary so the protection modes are
    // independently observable and the hover error can name the specific cause.
    //
    // A device enters overload per POWER.md §8.4 (delivering at its Setting-like
    // cap with unmet downstream rigid demand), the elastic analog (a storage
    // device at its full effective discharge with rigid demand still unmet), or
    // the §5.7 cable-overflow rule. Detection is per-tick atomic in Phase 2;
    // lockout fires instantly (no tolerance counter).
    //
    // Client mirror model identical to BrownoutRegistry: per-tick full snapshots
    // carry remaining ticks; the client stores expiry against MonotonicClock.
    //
    // Persistence: not persisted. Cross-network state, follows the device.
    internal static class OverloadRegistry
    {
        internal const int LockoutDurationTicks = BrownoutRegistry.LockoutDurationTicks;

        private static readonly ConcurrentDictionary<long, int> _lockoutUntilTick =
            new ConcurrentDictionary<long, int>();

        private static readonly ConcurrentDictionary<long, long> _clientExpiryMs =
            new ConcurrentDictionary<long, long>();

        private static bool IsClientPeer =>
            Assets.Scripts.Networking.NetworkManager.IsActive
            && !Assets.Scripts.Networking.NetworkManager.IsServer;

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

        internal static void SetClientOverloaded(long referenceId, bool overloaded)
        {
            if (overloaded) _clientExpiryMs[referenceId] = MonotonicClock.NowMs + LockoutDurationTicks * 500L;
            else _clientExpiryMs.TryRemove(referenceId, out _);
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

        internal static void NoteOverload(long referenceId, int currentTick)
        {
            _lockoutUntilTick[referenceId] = currentTick + LockoutDurationTicks;
        }

        internal static bool IsOverloaded(long referenceId, int currentTick)
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

        internal static int LockoutCount => _lockoutUntilTick.Count;

        internal static void ClearAll()
        {
            _lockoutUntilTick.Clear();
            _clientExpiryMs.Clear();
        }

        internal static void ClearLockout(long referenceId)
        {
            _lockoutUntilTick.TryRemove(referenceId, out _);
            _clientExpiryMs.TryRemove(referenceId, out _);
        }
    }
}
