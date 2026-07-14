using System.Globalization;

namespace PowerGridPlus
{
    /// <summary>
    ///     Registry hygiene sweep: every 600 ticks (about 5 minutes at the 2 Hz power tick), prune
    ///     the five fault registries (Deprioritized / Overload / CableOverload / CycleFault /
    ///     CurrentMismatchFault) and
    ///     the PoweredOwnership quarantine of entries that can no longer matter: EXPIRED lockouts
    ///     (the IsLockedOut read path self-cleans, but an entry nobody queries again leaks forever;
    ///     e.g. a device that faulted once and then idled out of every roster) and entries for
    ///     DESTROYED Things (a deconstructed device's ReferenceId is never queried again, so its
    ///     entry is immortal without this sweep). The quarantine is swept for the destroyed class
    ///     only: a quarantined device has no expiry by design, but a deconstructed one can never
    ///     be swept again. The registries are small in practice, so this is a hygiene bound,
    ///     not a hot-path fix: it guarantees session-long memory is bounded by the number of
    ///     CURRENTLY faulted devices instead of the number of devices that EVER faulted.
    ///
    ///     <para><b>Worker-thread lookup safety.</b> The sweep runs inside the atomic tick on the
    ///     power worker. Destroyed-device detection uses <c>Thing.Find(refId)</c>, a plain managed
    ///     dictionary lookup (the OffAsResetSweep precedent), and the verdict is a REFERENCE test
    ///     (<c>is null</c>), never the Unity <c>==</c> lifetime operator, so no Unity API is
    ///     touched off the main thread. A Thing destroyed on the main thread mid-sweep either
    ///     still resolves (pruned next sweep) or resolves null (pruned now); both are correct.
    ///     ConcurrentDictionary enumeration with concurrent TryRemove is safe by contract.</para>
    ///
    ///     <para>Counters are exact totals since load; one Info line per sweep that actually
    ///     removed something (the 600-tick cadence is its own throttle). Client mirrors are not
    ///     swept: they are replaced wholesale by every per-tick snapshot and the sweep only runs
    ///     on the simulating host anyway. DeadInputRegistry needs no sweep (rebuilt from scratch
    ///     every tick). Cleared on world load (the registries themselves are cleared there too).</para>
    /// </summary>
    internal static class RegistryHygiene
    {
        private const int SweepEveryTicks = 600;   // ~5 minutes at 2 Hz

        private static int _lastSweepTick;
        private static bool _everSwept;

        // Exact running totals since load; reflection surface for the rearch suite.
        internal static long SweepsRun { get; private set; }
        internal static long ExpiredPruned { get; private set; }
        internal static long DestroyedPruned { get; private set; }
        internal static int LastSweepTick => _lastSweepTick;

        /// <summary>Run the sweep when due (single flag comparison otherwise). Power worker only.</summary>
        internal static void MaybeRun(int currentTick)
        {
            if (_everSwept && currentTick - _lastSweepTick < SweepEveryTicks) return;
            if (!_everSwept)
            {
                // Anchor the cadence at the first tick after load without sweeping immediately:
                // the registries were just cleared by the load path, so there is nothing to prune.
                _everSwept = true;
                _lastSweepTick = currentTick;
                return;
            }
            _lastSweepTick = currentTick;
            SweepsRun++;

            int expired = 0, destroyed = 0, e, d;
            DeprioritizedRegistry.PruneStale(currentTick, out e, out d); expired += e; destroyed += d;
            OverloadRegistry.PruneStale(currentTick, out e, out d); expired += e; destroyed += d;
            CableOverloadRegistry.PruneStale(currentTick, out e, out d); expired += e; destroyed += d;
            CycleFaultRegistry.PruneStale(currentTick, out e, out d); expired += e; destroyed += d;
            CurrentMismatchFaultRegistry.PruneStale(currentTick, out e, out d); expired += e; destroyed += d;
            destroyed += PoweredOwnership.PruneDestroyedQuarantine();

            ExpiredPruned += expired;
            DestroyedPruned += destroyed;
            if (expired + destroyed > 0)
            {
                Plugin.Log?.LogInfo(
                    "[PowerGridPlus] Registry hygiene: pruned " + expired.ToString(CultureInfo.InvariantCulture)
                    + " expired and " + destroyed.ToString(CultureInfo.InvariantCulture)
                    + " destroyed-device entr(ies) at tick " + currentTick.ToString(CultureInfo.InvariantCulture)
                    + " (totals since load: " + ExpiredPruned.ToString(CultureInfo.InvariantCulture)
                    + " expired, " + DestroyedPruned.ToString(CultureInfo.InvariantCulture) + " destroyed).");
            }
        }

        /// <summary>World-load reset: restart the cadence for the new world.</summary>
        internal static void Clear()
        {
            _lastSweepTick = 0;
            _everSwept = false;
            SweepsRun = 0;
            ExpiredPruned = 0;
            DestroyedPruned = 0;
        }
    }
}
