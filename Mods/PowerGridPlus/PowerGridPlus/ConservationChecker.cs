using System.Collections.Generic;
using System.Globalization;

namespace PowerGridPlus
{
    /// <summary>
    ///     Post-ALLOCATE conservation audit over the allocator's converged grants (config-gated by
    ///     <c>Enable Conservation Check</c>). Two invariants, both properties of the allocator's own
    ///     bookkeeping, so a violation is a code bug in PowerGridPlus, never a player problem:
    ///
    ///     <list type="bullet">
    ///       <item>Per network: granted inflow == granted outflow within 0.5 W. Inflow = contributor
    ///       throughput arriving (rigid + soft) + granted elastic discharge + the generator power the
    ///       grants imply (derived as the residual, clamped to the network's actual generator supply;
    ///       unused generator capacity is curtailed, not a violation). Outflow = rigid demand served +
    ///       storage charge granted locally + contributor pulls granted (rigid + soft). An outflow the
    ///       inflow cannot fund means power was granted out of nothing; an inflow above the outflow
    ///       means a contributor was granted throughput nobody consumes (billed upstream, wasted).</item>
    ///       <item>Per contributor seg: TotalPull == TotalThrough * max(m,1) + quiescent whenever the
    ///       seg conducts (exact by construction in the forward sweep). A seg granted less than its
    ///       quiescent draw carries TotalThrough == 0 and may bill any partial amount up to the
    ///       quiescent, so that case is checked one-sided.</item>
    ///     </list>
    ///
    ///     <para>Warnings are throttled to once per network / per seg per 600 ticks (~5 minutes at the
    ///     2 Hz power tick) so a persistent bug logs a heartbeat instead of a flood. State is touched
    ///     only from the power worker (single-threaded per tick), so plain dictionaries suffice.
    ///     Logging goes through <see cref="Plugin.Log"/> (BepInEx, safe off the main thread).</para>
    /// </summary>
    internal static class ConservationChecker
    {
        private const float Tolerance = 0.5f;        // Watts
        private const int WarnCooldownTicks = 600;   // once per net / seg per ~5 minutes

        private static readonly Dictionary<long, int> _lastNetWarn = new Dictionary<long, int>();
        private static readonly Dictionary<long, int> _lastSegWarn = new Dictionary<long, int>();

        internal static bool Enabled => Settings.EnableConservationCheck != null
                                        && Settings.EnableConservationCheck.Value;

        /// <summary>
        ///     Audit one network's converged grants. All arguments are the allocator's own per-tick
        ///     quantities: <paramref name="segInflow"/> = rigid throughput arriving through active
        ///     supplier segs, <paramref name="softInflow"/> = soft throughput arriving the same way,
        ///     <paramref name="elasticGranted"/> = the summed elastic discharge shares,
        ///     <paramref name="rigidServed"/> = min(available supply, rigid demand),
        ///     <paramref name="rigidPulls"/> / <paramref name="softPulls"/> = consumer seg pulls
        ///     granted, <paramref name="storageCharge"/> = local storage charge granted.
        /// </summary>
        internal static void CheckNet(long netId, int currentTick, float genSupply, float segInflow,
            float softInflow, float elasticGranted, float rigidServed, float rigidPulls,
            float softPulls, float storageCharge)
        {
            float outflow = rigidServed + storageCharge + rigidPulls + softPulls;
            float nonGenInflow = segInflow + softInflow + elasticGranted;

            // Generator power is drawn on demand up to the network's supply; the residual the other
            // inflows leave is what the grants imply the generators covered. Clamping exposes both
            // failure directions: a residual above genSupply means outflow the inflow cannot fund
            // (power from nothing); a negative residual means inflow above outflow (wasted grant).
            float genGranted = outflow - nonGenInflow;
            if (genGranted < 0f) genGranted = 0f;
            else if (genGranted > genSupply) genGranted = genSupply;

            float inflow = nonGenInflow + genGranted;
            float delta = inflow - outflow;
            if (delta <= Tolerance && delta >= -Tolerance) return;
            if (!ShouldWarn(_lastNetWarn, netId, currentTick)) return;

            Plugin.Log?.LogWarning(
                "[PowerGridPlus] Conservation violation on network " + netId.ToString(CultureInfo.InvariantCulture)
                + ": inflow " + inflow.ToString("F2", CultureInfo.InvariantCulture)
                + " W vs outflow " + outflow.ToString("F2", CultureInfo.InvariantCulture)
                + " W (delta " + delta.ToString("F2", CultureInfo.InvariantCulture)
                + " W). Components: gen " + genGranted.ToString("F2", CultureInfo.InvariantCulture)
                + "/" + genSupply.ToString("F2", CultureInfo.InvariantCulture)
                + ", segIn " + segInflow.ToString("F2", CultureInfo.InvariantCulture)
                + ", softIn " + softInflow.ToString("F2", CultureInfo.InvariantCulture)
                + ", elastic " + elasticGranted.ToString("F2", CultureInfo.InvariantCulture)
                + " | rigidServed " + rigidServed.ToString("F2", CultureInfo.InvariantCulture)
                + ", charge " + storageCharge.ToString("F2", CultureInfo.InvariantCulture)
                + ", rigidPulls " + rigidPulls.ToString("F2", CultureInfo.InvariantCulture)
                + ", softPulls " + softPulls.ToString("F2", CultureInfo.InvariantCulture)
                + ". This is an allocator bug; please report it.");
        }

        /// <summary>
        ///     Audit one contributor seg's published totals: the input-side pull must equal the
        ///     output-side throughput times the input-draw factor plus the quiescent draw. Exact by
        ///     construction, so any drift is a code bug (double-billed quiescent, a lost distance
        ///     factor, a class granted on one terminal only).
        /// </summary>
        internal static void CheckSeg(long refId, string kind, int currentTick, float totalThrough,
            float totalPull, float drawFactor, float usedPower)
        {
            float m = drawFactor > 1f ? drawFactor : 1f;
            bool violated;
            float expected;
            if (totalThrough > 0.01f)
            {
                expected = totalThrough * m + usedPower;
                float diff = totalPull - expected;
                violated = diff > Tolerance || diff < -Tolerance;
            }
            else
            {
                // Not conducting: the pull may be any partial grant up to the quiescent draw.
                expected = usedPower;
                violated = totalPull > usedPower + Tolerance;
            }
            if (!violated) return;
            if (!ShouldWarn(_lastSegWarn, refId, currentTick)) return;

            Plugin.Log?.LogWarning(
                "[PowerGridPlus] Seg pull invariant violated on " + kind
                + " " + refId.ToString(CultureInfo.InvariantCulture)
                + ": TotalPull " + totalPull.ToString("F2", CultureInfo.InvariantCulture)
                + " W, expected " + expected.ToString("F2", CultureInfo.InvariantCulture)
                + " W (TotalThrough " + totalThrough.ToString("F2", CultureInfo.InvariantCulture)
                + ", m " + m.ToString("F3", CultureInfo.InvariantCulture)
                + ", quiescent " + usedPower.ToString("F2", CultureInfo.InvariantCulture)
                + "). This is an allocator bug; please report it.");
        }

        private static bool ShouldWarn(Dictionary<long, int> lastWarn, long key, int currentTick)
        {
            if (lastWarn.TryGetValue(key, out int last) && currentTick - last < WarnCooldownTicks)
                return false;
            lastWarn[key] = currentTick;
            return true;
        }
    }
}
