using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;

namespace PowerGridPlus
{
    /// <summary>
    ///     Discharge-delivery audit: the second direction of the grant-vs-store seam detector
    ///     (POWER.md §8.8). The charge-delivery audit (<see cref="ChargeDeliveryAudit"/>) proves a
    ///     granted charge LANDS in the store; this auditor proves a granted discharge LEAVES the
    ///     store as granted, no more and no less. The allocator's elastic share (rigid share + soft
    ///     top-up since the elastic-to-soft transfer rung, POWER.md §9.2) is min-clamped onto every
    ///     donor's GetGeneratedPower advertise, and on a Served net the write-back settlement must
    ///     drain the store exactly that share: an over-drain means store energy was consumed that
    ///     the allocator never granted (phantom discharge), an under-drain means the grid was told
    ///     power flowed that never left the store (phantom supply somewhere else).
    ///
    ///     <para><b>Observation at the settlement site, not field diffing</b> (the charge audit's
    ///     rationale, same float32 store-quantization argument): the drained amount is the
    ///     settlement debit the write-back applies (Core/WriteBack.ApplyDebit feeds
    ///     <see cref="RecordDrain"/>), so drain == grant holds by construction and the audit
    ///     exists to catch a settlement-path change that breaks the identity.</para>
    ///
    ///     <para><b>Charge / discharge same-tick disambiguation is by-record by construction.</b>
    ///     A battery can charge and discharge in the same tick on distinct networks (POWER.md
    ///     §7.1); the two audits can never confuse the flows because they consume DIFFERENT
    ///     settlement records: the charge audit sums the plan's credits, this auditor sums the
    ///     plan's debits. The umbilical phase-2 cell-to-cell crossing mutates PowerStored
    ///     directly (never through a settlement record) and the battery atmos self-drain sits
    ///     outside the plan entirely, so neither pollutes a drain sum.</para>
    ///
    ///     <para><b>The APC cell is out of scope.</b> The APC's cell-cover drain settles through
    ///     its own write-back debit lane, deliberately unpublished here: ALLOCATE publishes no
    ///     APC discharge grants and the settlement records no APC drain, so there is nothing to
    ///     compare (the APC ledger is likewise never settled by LedgerAdoption). The APC's
    ///     advertised discharge stays bounded by its bundled
    ///     SoftSupplyShareCache cap, and its credit side is covered by the charge audit.</para>
    ///
    ///     <para><b>Comparison</b> (ENFORCE tail, after the write-back settlement): for every store
    ///     the allocator rostered as an elastic this tick (the per-tick grant snapshot ALLOCATE
    ///     publishes via <see cref="PublishGrants"/>: refId -> granted watts, discharge-side net,
    ///     store kind; zero-share entries included so an ungranted drain on a Served net is
    ///     caught), the drained sum must equal the grant. Gates: the discharge-side net must be
    ///     classified Served this tick (on an unmet net every elastic is overload-tripped to share
    ///     0 and its advertise is zeroed by the lockout postfixes: honest darkness, the same
    ///     exemption as the charge audit);
    ///     zero-grant zero-drain passes trivially. Drains on stores with NO grant record are
    ///     outside the allocator's model (roster-absent: errored, short-circuited, the vanilla
    ///     fallback on the first ticks after load while the share cache is stale) and are not a
    ///     grant-vs-drain seam by definition; they are not audited, mirroring the charge audit.
    ///     No efficiency band: discharge has no configured loss, so the band is exact both ways.</para>
    ///
    ///     <para><b>Tolerance: 0.5 W</b>, the ConservationChecker basis (kW-scale chunk-assembled
    ///     sums; vanilla may drain a provider across several consumer settles).</para>
    ///
    ///     <para><b>Always-on, no config entry</b> (the ledger-audit posture). Counts are exact and
    ///     never throttled; only the log line is: one aggregated warning at most once per 600 ticks
    ///     while new anomalies arrive, carrying totals since load, the worst offender, and the
    ///     latest. Zero anomalies produce zero lines. Cleared on world load. The ScenarioRunner
    ///     rearch suite reflection-drives <see cref="IsViolation"/> with synthetic cases and reads
    ///     the counters across its window; keep the member names stable.</para>
    ///
    ///     <para>Threading: grants swap by volatile reference (allocator worker); drains are
    ///     recorded from the write-back settlement on the power worker; the tail comparison runs
    ///     later on the same worker. The suite reads counters from the sim-tick pump.</para>
    /// </summary>
    internal static class DischargeDeliveryAudit
    {
        // kW-scale chunk-assembled sums; see the class doc for the basis.
        internal const float Tolerance = 0.5f;

        private const int WarnCooldownTicks = 600;   // one aggregated warning per ~5 minutes at 2 Hz

        internal struct Grant
        {
            public float Granted;   // watts of discharge the allocator granted this store this tick
            public long NetId;      // the discharge-side (output) network the grant was made on
            public byte Kind;       // ChargeDeliveryAudit.KindBattery / KindUmbilical (APC cell never published)
        }

        private static volatile Dictionary<long, Grant> _grants = new Dictionary<long, Grant>();

        /// <summary>ALLOCATE publish tail: swap in this tick's discharge-grant snapshot.</summary>
        internal static void PublishGrants(Dictionary<long, Grant> grants)
        {
            _grants = grants;
        }

        // refId -> (tick stamped, drained watts summed this tick, store kind). Stale entries are
        // ignored by tick mismatch and overwritten on the next drain.
        private static readonly ConcurrentDictionary<long, (int tick, float drained, byte kind)> _drains =
            new ConcurrentDictionary<long, (int, float, byte)>();

        /// <summary>
        ///     Record a settlement store drain (the debit the write-back applies to the store).
        ///     Called from Core/WriteBack.ApplyDebit; single-threaded per tick on the power worker.
        /// </summary>
        internal static void RecordDrain(long refId, byte kind, float drained)
        {
            if (drained <= 0f) return;
            int now = ElectricityTickCounter.CurrentTick;
            float prior = _drains.TryGetValue(refId, out var e) && e.tick == now ? e.drained : 0f;
            _drains[refId] = (now, prior + drained, kind);
        }

        /// <summary>
        ///     The pure comparison predicate: does a (granted, drained) pair break the
        ///     drain-equals-grant contract? Exact band both ways (discharge carries no configured
        ///     efficiency loss). Reflection-driven by the ScenarioRunner rearch suite; keep the
        ///     signature stable.
        /// </summary>
        internal static bool IsViolation(float granted, float drained)
        {
            return drained > granted + Tolerance
                   || drained < granted - Tolerance;
        }

        // Exact running totals since load; reflection surface for the rearch suite.
        internal static long ViolationTicks { get; private set; }        // ticks with >= 1 violating store
        internal static long ViolationStoreTicks { get; private set; }   // (store, tick) violation observations
        internal static int DistinctStoreCount => _distinctStores.Count;
        internal static long WorstRefId { get; private set; }
        internal static float WorstGranted { get; private set; }
        internal static float WorstDrained { get; private set; }
        internal static int WorstTick { get; private set; }
        internal static long LastRefId { get; private set; }
        internal static float LastGranted { get; private set; }
        internal static float LastDrained { get; private set; }
        internal static int LastTick { get; private set; }
        internal static int WarningsEmitted { get; private set; }
        internal static string LastWarning { get; private set; }

        private static readonly HashSet<long> _distinctStores = new HashSet<long>();
        private static long _storeTicksAtLastWarn;
        private static int _lastWarnTick = -WarnCooldownTicks;

        /// <summary>
        ///     ENFORCE tail: compare every rostered store's drained sum against its granted
        ///     discharge share and emit the throttled aggregated warning when due. Runs after
        ///     the write-back settlement so the drains are final for the tick.
        /// </summary>
        internal static void RunEnforceTail(int currentTick)
        {
            var grants = _grants;
            bool anyThisTick = false;

            foreach (var pair in grants)
            {
                var grant = pair.Value;
                float drained = _drains.TryGetValue(pair.Key, out var d) && d.tick == currentTick
                    ? d.drained : 0f;

                if (grant.Granted <= Tolerance && drained <= Tolerance) continue;   // trivially clean

                // Honest darkness: on an unmet net the elastics are overload-tripped to share 0
                // and their advertises are zeroed by the lockout postfixes (the charge audit's
                // exemption, mirrored); only a Served net promises drained == granted.
                if (!ShortfallDiagnostics.TryClassify(grant.NetId, out byte cls)
                    || cls != ShortfallDiagnostics.Served)
                    continue;

                if (!IsViolation(grant.Granted, drained)) continue;

                anyThisTick = true;
                ViolationStoreTicks++;
                _distinctStores.Add(pair.Key);
                LastRefId = pair.Key;
                LastGranted = grant.Granted;
                LastDrained = drained;
                LastTick = currentTick;
                float delta = drained - grant.Granted;
                if (delta < 0f) delta = -delta;
                float worstDelta = WorstDrained - WorstGranted;
                if (worstDelta < 0f) worstDelta = -worstDelta;
                if (WorstRefId == 0L || delta > worstDelta)
                {
                    WorstRefId = pair.Key;
                    WorstGranted = grant.Granted;
                    WorstDrained = drained;
                    WorstTick = currentTick;
                }
            }

            if (anyThisTick) ViolationTicks++;
            EmitWarningIfDue(currentTick);
        }

        private static void EmitWarningIfDue(int currentTick)
        {
            if (ViolationStoreTicks == _storeTicksAtLastWarn) return;
            if (currentTick - _lastWarnTick < WarnCooldownTicks) return;
            _lastWarnTick = currentTick;
            _storeTicksAtLastWarn = ViolationStoreTicks;
            WarningsEmitted++;
            LastWarning =
                "[PowerGridPlus] Discharge-delivery audit: store drain diverged from the allocator's discharge grant on "
                + ViolationStoreTicks.ToString(CultureInfo.InvariantCulture)
                + " store-tick(s) across " + DistinctStoreCount.ToString(CultureInfo.InvariantCulture)
                + " store(s) since load (" + ViolationTicks.ToString(CultureInfo.InvariantCulture)
                + " tick(s) affected; worst: store " + WorstRefId.ToString(CultureInfo.InvariantCulture)
                + " granted " + WorstGranted.ToString("F2", CultureInfo.InvariantCulture)
                + " W vs drained " + WorstDrained.ToString("F2", CultureInfo.InvariantCulture)
                + " W at tick " + WorstTick.ToString(CultureInfo.InvariantCulture)
                + "; latest: store " + LastRefId.ToString(CultureInfo.InvariantCulture)
                + " granted " + LastGranted.ToString("F2", CultureInfo.InvariantCulture)
                + " W vs drained " + LastDrained.ToString("F2", CultureInfo.InvariantCulture)
                + " W at tick " + LastTick.ToString(CultureInfo.InvariantCulture)
                + "). Granted discharge must leave the store whole on a served network. This is a"
                + " delivery-path bug; please report it.";
            Plugin.Log?.LogWarning(LastWarning);
        }

        /// <summary>World-load reset: drop the previous world's grants, drains, and counters.</summary>
        internal static void Clear()
        {
            _grants = new Dictionary<long, Grant>();
            _drains.Clear();
            ViolationTicks = 0;
            ViolationStoreTicks = 0;
            _distinctStores.Clear();
            WorstRefId = 0L;
            WorstGranted = 0f;
            WorstDrained = 0f;
            WorstTick = 0;
            LastRefId = 0L;
            LastGranted = 0f;
            LastDrained = 0f;
            LastTick = 0;
            WarningsEmitted = 0;
            LastWarning = null;
            _storeTicksAtLastWarn = 0;
            _lastWarnTick = -WarnCooldownTicks;
        }
    }
}
