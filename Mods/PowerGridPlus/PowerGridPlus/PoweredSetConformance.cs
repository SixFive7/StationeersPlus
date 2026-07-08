using System.Collections.Generic;
using System.Globalization;

namespace PowerGridPlus
{
    /// <summary>
    ///     Powered-set conformance assert (ENFORCE tail): every segmenter ALLOCATE published as
    ///     HEALTHY this tick must actually read <c>Powered == true</c> once the presentation
    ///     machinery has had its say. The Powered policy has two halves (the AllowSetPower
    ///     postfixes block the False edge inside ApplyState; the ENFORCE-tail reconcile asserts
    ///     the True edge), and a regression in either half (a postfix that stops matching, a
    ///     roster that publishes under the wrong ReferenceId, a reconcile that skips a member)
    ///     leaves a healthy device presenting "broken" to the player, the exact diagnostic trap
    ///     the policy exists to kill. This auditor closes the loop by reading the flag back.
    ///
    ///     <para><b>One-tick marshal grace.</b> The reconcile writes through the vanilla
    ///     <c>Device.SetPowerFromThread</c>, which self-marshals to the main thread, so a freshly
    ///     healthy device legitimately still reads false at THIS tick's tail; the write lands
    ///     within a frame or two, far inside the 500 ms to the next tick. A violation is
    ///     therefore counted only for a device that reads false while healthy on two CONSECUTIVE
    ///     tick tails (healthy-and-dark last tick AND healthy-and-dark again this tick), which is
    ///     exactly the shape a real regression produces (permanently dark) and never the rising
    ///     edge. The same grace covers a pair's partner half.</para>
    ///
    ///     <para><b>Always-on, no config entry</b> (the ledger-audit posture). Counts are exact
    ///     and never throttled; one aggregated warning at most once per 600 ticks while new
    ///     violations arrive. Zero violations produce zero lines. Cleared on world load. Zero
    ///     steady-state allocation: the two tracking sets are persistent and swap/clear per tick.
    ///     The ScenarioRunner rearch suite reads the counters across its window; keep the member
    ///     names stable.</para>
    ///
    ///     <para>Threading: runs at the ENFORCE tail on the power worker, right after
    ///     PoweredPresentation.ReconcileEnforceTail, reading the same published roster.
    ///     <c>Thing.Powered</c> is a managed flag; reading it off-thread is the established
    ///     pattern (the suite and the presentation code both do).</para>
    /// </summary>
    internal static class PoweredSetConformance
    {
        private const int WarnCooldownTicks = 600;   // one aggregated warning per ~5 minutes at 2 Hz

        // Devices that were healthy-and-dark at the previous tick's tail (grace tracking).
        private static HashSet<long> _darkLastTick = new HashSet<long>();
        private static HashSet<long> _darkThisTick = new HashSet<long>();

        // Exact running totals since load; reflection surface for the rearch suite.
        internal static long ViolationTicks { get; private set; }        // ticks with >= 1 violating device
        internal static long ViolationDeviceTicks { get; private set; }  // (device, tick) violation observations
        internal static int DistinctDeviceCount => _distinctDevices.Count;
        internal static long LastRefId { get; private set; }
        internal static int LastTick { get; private set; }
        internal static int WarningsEmitted { get; private set; }
        internal static string LastWarning { get; private set; }

        private static readonly HashSet<long> _distinctDevices = new HashSet<long>();
        private static long _deviceTicksAtLastWarn;
        private static int _lastWarnTick = -WarnCooldownTicks;

        /// <summary>
        ///     ENFORCE tail: read Powered back on every healthy roster member (anchor and pair
        ///     partner) and count the ones dark for the second consecutive tick.
        /// </summary>
        internal static void RunEnforceTail(int currentTick)
        {
            var roster = PoweredPresentation.Roster;
            _darkThisTick.Clear();
            bool anyThisTick = false;

            for (int i = 0; i < roster.Count; i++)
            {
                var e = roster[i];
                if (!e.Healthy) continue;
                var anchor = e.Anchor;
                if (anchor != null && !anchor.Powered)
                    anyThisTick |= NoteDark(e.RefId, currentTick);
                var partner = e.Partner;
                if (partner != null && !partner.Powered)
                    anyThisTick |= NoteDark(partner.ReferenceId, currentTick);
            }

            if (anyThisTick) ViolationTicks++;
            // Swap the tracking sets: this tick's dark-healthy set becomes next tick's grace set.
            var tmp = _darkLastTick;
            _darkLastTick = _darkThisTick;
            _darkThisTick = tmp;
            EmitWarningIfDue(currentTick);
        }

        private static bool NoteDark(long refId, int currentTick)
        {
            _darkThisTick.Add(refId);
            if (!_darkLastTick.Contains(refId)) return false;   // rising-edge grace (marshal latency)
            ViolationDeviceTicks++;
            _distinctDevices.Add(refId);
            LastRefId = refId;
            LastTick = currentTick;
            return true;
        }

        private static void EmitWarningIfDue(int currentTick)
        {
            if (ViolationDeviceTicks == _deviceTicksAtLastWarn) return;
            if (currentTick - _lastWarnTick < WarnCooldownTicks) return;
            _lastWarnTick = currentTick;
            _deviceTicksAtLastWarn = ViolationDeviceTicks;
            WarningsEmitted++;
            LastWarning =
                "[PowerGridPlus] Powered-set conformance: a healthy segmenter read Powered=false past the one-tick marshal grace on "
                + ViolationDeviceTicks.ToString(CultureInfo.InvariantCulture)
                + " device-tick(s) across " + DistinctDeviceCount.ToString(CultureInfo.InvariantCulture)
                + " device(s) since load (" + ViolationTicks.ToString(CultureInfo.InvariantCulture)
                + " tick(s) affected; latest: device " + LastRefId.ToString(CultureInfo.InvariantCulture)
                + " at tick " + LastTick.ToString(CultureInfo.InvariantCulture)
                + "). The Powered presentation policy must keep every healthy segmenter lit. This is a"
                + " presentation bug; please report it.";
            Plugin.Log?.LogWarning(LastWarning);
        }

        /// <summary>World-load reset: drop the previous world's tracking and counters.</summary>
        internal static void Clear()
        {
            _darkLastTick.Clear();
            _darkThisTick.Clear();
            ViolationTicks = 0;
            ViolationDeviceTicks = 0;
            _distinctDevices.Clear();
            LastRefId = 0L;
            LastTick = 0;
            WarningsEmitted = 0;
            LastWarning = null;
            _deviceTicksAtLastWarn = 0;
            _lastWarnTick = -WarnCooldownTicks;
        }
    }
}
