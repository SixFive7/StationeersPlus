using System.Collections.Concurrent;
using System.Collections.Generic;
using Assets.Scripts.Objects;

namespace PowerGridPlus
{
    // Shed (upstream-side protection) lockout state machine for segmenting devices.
    // Per-device (keyed by Thing.ReferenceId) entry in _lockoutUntilTick: the
    // electricity-tick counter value AFTER WHICH the shed lockout expires.
    //
    // Lockout fires instantly on first detection (no shortfall counter). The
    // atomic 5-phase electricity tick (AtomicElectricityTickPatch) gives the
    // allocator fresh in-tick supply/demand data, so a 1-tick-blip tolerance
    // is unnecessary: if the allocator sees a real shortfall this tick, the
    // shortfall is real, and the 60-second lockout fires immediately.
    //
    // Client mirror: hosts decide; clients receive per-tick full snapshots
    // (FaultRegistrySnapshotMessage) carrying REMAINING ticks per device, and
    // store the expiry against the local monotonic clock (MonotonicClock.NowMs)
    // because the electricity-tick counter does not advance on a non-simulating
    // peer. Entries self-expire; a missed packet self-heals on the next
    // snapshot (POWER.md §13 heartbeat model).
    //
    // Persistence: NOT persisted. A shed in progress at save time clears on
    // load; recompute fires on the first tick after load.
    internal static class BrownoutRegistry
    {
        internal const int LockoutDurationTicks = 120;     // 60 seconds at 2 Hz

        private static readonly ConcurrentDictionary<long, int> _lockoutUntilTick =
            new ConcurrentDictionary<long, int>();

        // Client mirror: refId -> expiry in MonotonicClock milliseconds.
        private static readonly ConcurrentDictionary<long, long> _clientExpiryMs =
            new ConcurrentDictionary<long, long>();

        private static bool IsClientPeer =>
            Assets.Scripts.Networking.NetworkManager.IsActive
            && !Assets.Scripts.Networking.NetworkManager.IsServer;

        // Snapshot receive: replace the whole mirror (self-healing full state).
        internal static void ReplaceClientSnapshot(List<KeyValuePair<long, int>> remainingTicksByRef)
        {
            _clientExpiryMs.Clear();
            long now = MonotonicClock.NowMs;
            for (int i = 0; i < remainingTicksByRef.Count; i++)
            {
                var pair = remainingTicksByRef[i];
                if (pair.Value <= 0) continue;
                _clientExpiryMs[pair.Key] = now + pair.Value * 500L;   // 2 Hz tick = 500 ms
            }
        }

        internal static void SetClientShedding(long referenceId, bool shedding)
        {
            if (shedding) _clientExpiryMs[referenceId] = MonotonicClock.NowMs + LockoutDurationTicks * 500L;
            else _clientExpiryMs.TryRemove(referenceId, out _);
        }

        // Snapshot of currently-locked-out devices with remaining ticks (host-side).
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

        // True when the device is currently inside a shed-lockout window (host-side).
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

        // Fires the 60-second lockout immediately.
        internal static void NoteShed(long referenceId, int currentTick)
        {
            _lockoutUntilTick[referenceId] = currentTick + LockoutDurationTicks;
        }

        // External read of the shedding flag for IC10 LogicType.Shedding and the
        // visuals. On a non-server peer the host dict is empty; read the mirror.
        internal static bool IsShedding(long referenceId, int currentTick)
        {
            if (IsClientPeer)
                return _clientExpiryMs.TryGetValue(referenceId, out var expiry) && expiry > MonotonicClock.NowMs;
            return IsLockedOut(referenceId, currentTick);
        }

        // Remaining lockout seconds for the hover countdown (POWER.md §11.2), peer-aware. Host-side
        // the tick remainder is smoothed with the intra-tick wall-clock offset so the displayed value
        // ticks down continuously; the authoritative expiry stays tick-based.
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

        // Defensive escape-hatch for tests / world load. Drops every entry.
        internal static void ClearAll()
        {
            _lockoutUntilTick.Clear();
            _clientExpiryMs.Clear();
        }

        // User-initiated reset (OFF-as-reset): drop the lockout for one device on
        // whichever peer this runs on. The next allocator pass re-decides.
        internal static void ClearLockout(long referenceId)
        {
            _lockoutUntilTick.TryRemove(referenceId, out _);
            _clientExpiryMs.TryRemove(referenceId, out _);
        }
    }
}
