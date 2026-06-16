using System.Collections.Concurrent;
using System.Collections.Generic;
using Assets.Scripts.Objects;

namespace PowerGridPlus
{
    // VARIABLE_VOLTAGE_FAULT lockout state machine for power producers
    // (POWER.md §8.5 producer-isolation). Parallel API to the other three
    // registries; each entry additionally records the offending device names
    // (violatorNames) so the hover can name what caused the fault.
    //
    // Client mirror model identical to BrownoutRegistry (per-tick full
    // snapshots carry remaining ticks + violators; expiry stored against
    // MonotonicClock).
    //
    // Persistence: not persisted. Cross-network state, follows the producer.
    internal static class VariableVoltageFaultRegistry
    {
        private struct HostEntry
        {
            public int UntilTick;
            public string ViolatorNames;
        }

        private struct ClientEntry
        {
            public long ExpiryMs;
            public string ViolatorNames;
        }

        // Literal 120, do NOT derive from a tick-rate constant.
        // Assumes 2 Hz electricity tick. If game tick rate changes, review.
        internal const int LockoutDurationTicks = 120;     // 60 seconds at 2 Hz

        private static readonly ConcurrentDictionary<long, HostEntry> _lockoutUntilTick =
            new ConcurrentDictionary<long, HostEntry>();

        private static readonly ConcurrentDictionary<long, ClientEntry> _clientExpiry =
            new ConcurrentDictionary<long, ClientEntry>();

        private static bool IsClientPeer =>
            Assets.Scripts.Networking.NetworkManager.IsActive
            && !Assets.Scripts.Networking.NetworkManager.IsServer;

        internal static void NoteVariableVoltageFault(long referenceId, int currentTick, string violatorNames)
        {
            _lockoutUntilTick[referenceId] = new HostEntry
            {
                UntilTick = currentTick + LockoutDurationTicks,
                ViolatorNames = violatorNames,
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

        internal static bool IsVariableVoltageFaulted(long referenceId, int currentTick)
        {
            if (IsClientPeer)
                return _clientExpiry.TryGetValue(referenceId, out var entry) && entry.ExpiryMs > MonotonicClock.NowMs;
            return IsLockedOut(referenceId, currentTick);
        }

        // Remaining seconds + violator names for the hover line, peer-aware.
        internal static bool TryGetFault(long referenceId, int currentTick, out float secondsLeft, out string violatorNames)
        {
            if (IsClientPeer)
            {
                if (_clientExpiry.TryGetValue(referenceId, out var entry))
                {
                    long left = entry.ExpiryMs - MonotonicClock.NowMs;
                    if (left > 0)
                    {
                        secondsLeft = left / 1000f;
                        violatorNames = entry.ViolatorNames;
                        return true;
                    }
                }
                secondsLeft = 0f;
                violatorNames = null;
                return false;
            }
            if (_lockoutUntilTick.TryGetValue(referenceId, out var host) && host.UntilTick > currentTick)
            {
                secondsLeft = ElectricityTickCounter.SmoothSeconds(host.UntilTick - currentTick);
                violatorNames = host.ViolatorNames;
                return true;
            }
            secondsLeft = 0f;
            violatorNames = null;
            return false;
        }

        // Host snapshot for the per-tick full sync: (refId, remainingTicks, violators).
        internal static IEnumerable<(long refId, int remainingTicks, string violators)> SnapshotRemaining(int currentTick)
        {
            foreach (var pair in _lockoutUntilTick)
            {
                int remaining = pair.Value.UntilTick - currentTick;
                if (remaining > 0)
                    yield return (pair.Key, remaining, pair.Value.ViolatorNames ?? string.Empty);
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

        internal static void ReplaceClientSnapshot(List<(long refId, int remainingTicks, string violators)> entries)
        {
            _clientExpiry.Clear();
            long now = MonotonicClock.NowMs;
            for (int i = 0; i < entries.Count; i++)
            {
                var (refId, remaining, violators) = entries[i];
                if (remaining <= 0) continue;
                _clientExpiry[refId] = new ClientEntry
                {
                    ExpiryMs = now + remaining * 500L,
                    ViolatorNames = violators,
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

        internal static int LockoutCount => _lockoutUntilTick.Count;
    }
}
