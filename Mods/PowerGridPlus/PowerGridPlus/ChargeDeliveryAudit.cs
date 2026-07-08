using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;

namespace PowerGridPlus
{
    /// <summary>
    ///     Charge-delivery audit: the grant-vs-credit seam detector, the fifth self-diagnostic
    ///     surface (POWER.md §8.8) next to the conservation check, the shortfall census, the ledger
    ///     audit, and the partial-power sentinel. The allocator's GRANTS are audited by the
    ///     conservation checker and the vanilla-facing BILLS by the ledger audit and the sentinel,
    ///     but what actually lands in a STORE (a PowerStored credit) was unaudited: a delivery-side
    ///     adjustment (a quiescent subtraction, a ledger dance, a clamp) can silently under- or
    ///     over-credit a store relative to the charge the allocator granted, with every other
    ///     invariant green (the APC quiescent-subtraction wrinkle that motivated this auditor).
    ///
    ///     <para><b>Observation via the argument stream, not field diffing.</b> The store fields
    ///     are float32, so a PowerStored delta quantizes at the store's magnitude (16 J per ulp
    ///     on a 230 MJ nuclear bank: the 13c soak measured 39.37 W grants landing as 32, 25.58
    ///     as 32, 0.67 as 0, all multiples of 16). That rounding is storage physics, not a
    ///     delivery seam, so the credited amount is derived from the delivered ARGUMENT after
    ///     every patch adjustment, through the implementation's own credit gates, clamped by the
    ///     pre-call headroom: <c>credited = min(delivered-after-adjustments, headroom-before)</c>
    ///     (the vanilla Clamp at PowerMaximum reported truthfully, so a near-full store's clamp
    ///     truncation never fires the audit; the grant is sized from the same float headroom, so
    ///     the two sides meet exactly). Battery and umbilical credits are computed by the
    ///     Priority.First/Last brackets in Patches.ChargeDeliveryObservationPatches (gates cited
    ///     there against the decompile); the APC records its exact credit at the source inside
    ///     its own ReceivePowerPatch. Only calls on the store's INPUT network count: the
    ///     umbilical's phase-2 cell-to-cell crossing passes a null network and vanishes from the
    ///     sum by construction, and discharge (UsePower) and the battery's OnAtmosphericTick
    ///     self-drain are different methods entirely, so neither can pollute a credit.</para>
    ///
    ///     <para><b>Grant-moot recognition.</b> A store whose charge gate legitimately closed
    ///     between the ALLOCATE grant and ENFORCE is not a seam: the APC cell's IsCharged is the
    ///     Mode display state, updated by a main-thread interact that can land inside the tick,
    ///     so on the tick a cell fills the grant exists while the bill and delivery correctly
    ///     carry no charge (granted 1000 / credited 0, the farm-APC tick-868 finding: one event
    ///     per fill edge, sentinel silent because the supplier's surplus advertise keeps the net
    ///     met). The APC billing and delivery patches call <see cref="MarkChargeGateClosed"/>
    ///     when a fresh share exists but the cell can take no charge; a marked store's grant is
    ///     skipped this tick. Battery and umbilical bills are headroom-gated (no display-state
    ///     flag), stable within the tick, so they need no marker.</para>
    ///
    ///     <para><b>Comparison</b> (ENFORCE tail, after every ApplyState): for every store the
    ///     allocator granted charge this tick (the per-tick grant snapshot ALLOCATE publishes via
    ///     <see cref="PublishGrants"/>: refId -> granted watts, charge-side net, store kind), the
    ///     credited sum must equal the grant. Gates: the charge-side net must be classified Served
    ///     this tick (on an unmet net vanilla ratio-scales deliveries by design, honest darkness,
    ///     same exemption as the partial-power sentinel); APC entries are skipped when
    ///     EnableAreaPowerControlFix is off (vanilla owns that delivery path); Battery entries
    ///     compare against the band [granted * BatteryChargeEfficiency, granted] because the
    ///     configured efficiency loss is legitimate (and its sub-500 W per-chunk exemption keeps
    ///     the actual inside that band); zero-grant zero-credit passes trivially. Stores with
    ///     credits but NO grant record are outside the allocator's model (a short-circuited
    ///     battery, a master toggled off, vanilla fallback) and are not a grant-vs-credit seam by
    ///     definition; they are not audited.</para>
    ///
    ///     <para><b>Tolerance: 0.5 W.</b> Same basis as the ConservationChecker tolerance and the
    ///     shortfall classifier's DiagEps: the compared quantities are kW-scale sums assembled
    ///     over multiple float operations (per-provider chunks, min-chains, the ratio multiply),
    ///     so the worst realistic rounding is tens of ulps at 25-50 kW (ulp(25 kW) ~ 0.002 W),
    ///     well under 0.5 W, while every real seam so far has been quiescent-scale (10 W) or
    ///     larger. The allocator's own 0.01 W epsilon is for single-value comparisons, not
    ///     chunk-assembled sums.</para>
    ///
    ///     <para><b>Always-on, no config entry</b> (the ledger-audit posture). Counts are exact
    ///     and never throttled; only the log line is: one aggregated warning at most once per 600
    ///     ticks while new anomalies arrive, carrying totals since load, the worst offender
    ///     (largest |credited - granted|), and the latest. Zero anomalies produce zero lines.
    ///     Cleared on world load. The ScenarioRunner rearch suite reflection-drives
    ///     <see cref="IsViolation"/> with synthetic cases and reads the counters across its
    ///     window; keep the member names stable.</para>
    ///
    ///     <para>Threading: grants swap by volatile reference (allocator worker); credits are
    ///     recorded from ApplyState on the power worker; the tail comparison runs on the same
    ///     worker after every ApplyState. The suite reads counters from the sim-tick pump.</para>
    /// </summary>
    internal static class ChargeDeliveryAudit
    {
        internal const byte KindBattery = 0;
        internal const byte KindApcCell = 1;
        internal const byte KindUmbilical = 2;

        // kW-scale chunk-assembled sums; see the class doc for the basis.
        internal const float Tolerance = 0.5f;

        private const int WarnCooldownTicks = 600;   // one aggregated warning per ~5 minutes at 2 Hz

        internal struct Grant
        {
            public float Granted;   // watts of charge the allocator granted this store this tick
            public long NetId;      // the charge-side (input) network the grant was made on
            public byte Kind;       // KindBattery / KindApcCell / KindUmbilical
        }

        private static volatile Dictionary<long, Grant> _grants = new Dictionary<long, Grant>();

        /// <summary>ALLOCATE publish tail: swap in this tick's charge-grant snapshot.</summary>
        internal static void PublishGrants(Dictionary<long, Grant> grants)
        {
            _grants = grants;
        }

        // refId -> (tick stamped, credited watts summed this tick, store kind). Stale entries are
        // ignored by tick mismatch and overwritten on the next credit.
        private static readonly ConcurrentDictionary<long, (int tick, float credited, byte kind)> _credits =
            new ConcurrentDictionary<long, (int, float, byte)>();

        /// <summary>
        ///     Record an observed store credit (argument-derived: the delivered amount after every
        ///     patch adjustment, through the implementation's credit gates, clamped by the
        ///     pre-call headroom) for one ReceivePower call on the store's input network. Called
        ///     from the observation brackets / the APC delivery patch inside ApplyState;
        ///     single-threaded per tick on the power worker.
        /// </summary>
        internal static void RecordCredit(long refId, byte kind, float credited)
        {
            if (credited <= 0f) return;
            int now = ElectricityTickCounter.CurrentTick;
            float prior = _credits.TryGetValue(refId, out var e) && e.tick == now ? e.credited : 0f;
            _credits[refId] = (now, prior + credited, kind);
        }

        // Stores whose charge gate legitimately closed between the ALLOCATE grant and ENFORCE
        // this tick (the APC cell's Mode-based IsCharged fill edge): the grant is moot, not a
        // seam. Tick-stamped; stale entries expire by tick mismatch.
        private static readonly ConcurrentDictionary<long, int> _gateClosed =
            new ConcurrentDictionary<long, int>();

        /// <summary>Mark a store's charge grant moot for this tick (its charge gate closed after
        /// the grant was made). Called from the APC billing / delivery patches.</summary>
        internal static void MarkChargeGateClosed(long refId)
        {
            _gateClosed[refId] = ElectricityTickCounter.CurrentTick;
        }

        /// <summary>
        ///     The pure comparison predicate: does a (granted, credited) pair break the
        ///     credit-equals-grant contract? <paramref name="efficiencyFloor"/> is 1 for exact
        ///     stores and the configured charge efficiency (0..1] for batteries, whose configured
        ///     loss keeps a legitimate credit inside [granted * floor, granted]. Reflection-driven
        ///     by the ScenarioRunner rearch suite; keep the signature stable.
        /// </summary>
        internal static bool IsViolation(float granted, float credited, float efficiencyFloor)
        {
            if (!(efficiencyFloor > 0f) || efficiencyFloor > 1f) efficiencyFloor = 1f;
            return credited > granted + Tolerance
                   || credited < granted * efficiencyFloor - Tolerance;
        }

        // Exact running totals since load; reflection surface for the rearch suite.
        internal static long ViolationTicks { get; private set; }        // ticks with >= 1 violating store
        internal static long ViolationStoreTicks { get; private set; }   // (store, tick) violation observations
        internal static int DistinctStoreCount => _distinctStores.Count;
        internal static long WorstRefId { get; private set; }
        internal static float WorstGranted { get; private set; }
        internal static float WorstCredited { get; private set; }
        internal static int WorstTick { get; private set; }
        internal static long LastRefId { get; private set; }
        internal static float LastGranted { get; private set; }
        internal static float LastCredited { get; private set; }
        internal static int LastTick { get; private set; }
        internal static int WarningsEmitted { get; private set; }
        internal static string LastWarning { get; private set; }

        private static readonly HashSet<long> _distinctStores = new HashSet<long>();
        private static long _storeTicksAtLastWarn;
        private static int _lastWarnTick = -WarnCooldownTicks;

        /// <summary>
        ///     ENFORCE tail: compare every granted store's credited sum against its grant and emit
        ///     the throttled aggregated warning when due. Runs after every network's ApplyState so
        ///     the credits are final for the tick (the umbilical crossing runs later in the device
        ///     tick but never enters the sum; see the class doc).
        /// </summary>
        internal static void RunEnforceTail(int currentTick)
        {
            var grants = _grants;
            bool anyThisTick = false;

            foreach (var pair in grants)
            {
                var grant = pair.Value;
                float credited = _credits.TryGetValue(pair.Key, out var c) && c.tick == currentTick
                    ? c.credited : 0f;

                if (grant.Granted <= Tolerance && credited <= Tolerance) continue;   // trivially clean

                // Vanilla owns the APC delivery path when the fix master is off.
                if (grant.Kind == KindApcCell
                    && (Settings.EnableAreaPowerControlFix == null || !Settings.EnableAreaPowerControlFix.Value))
                    continue;

                // Grant-moot recognition: the store's charge gate closed after the grant was
                // made (the APC cell fill edge; see the class doc). Not a seam; skip.
                if (_gateClosed.TryGetValue(pair.Key, out int closedTick) && closedTick == currentTick)
                    continue;

                // Honest darkness: on an unmet net vanilla ratio-scales the delivery by design
                // (the partial-power sentinel's exemption); only a Served net promises
                // delivered == granted.
                if (!ShortfallDiagnostics.TryClassify(grant.NetId, out byte cls)
                    || cls != ShortfallDiagnostics.Served)
                    continue;

                float floor = 1f;
                if (grant.Kind == KindBattery && Settings.BatteryChargeEfficiency != null)
                    floor = Settings.BatteryChargeEfficiency.Value;

                if (!IsViolation(grant.Granted, credited, floor)) continue;

                anyThisTick = true;
                ViolationStoreTicks++;
                _distinctStores.Add(pair.Key);
                LastRefId = pair.Key;
                LastGranted = grant.Granted;
                LastCredited = credited;
                LastTick = currentTick;
                float delta = credited - grant.Granted;
                if (delta < 0f) delta = -delta;
                float worstDelta = WorstCredited - WorstGranted;
                if (worstDelta < 0f) worstDelta = -worstDelta;
                if (WorstRefId == 0L || delta > worstDelta)
                {
                    WorstRefId = pair.Key;
                    WorstGranted = grant.Granted;
                    WorstCredited = credited;
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
                "[PowerGridPlus] Charge-delivery audit: store credit diverged from the allocator's charge grant on "
                + ViolationStoreTicks.ToString(CultureInfo.InvariantCulture)
                + " store-tick(s) across " + DistinctStoreCount.ToString(CultureInfo.InvariantCulture)
                + " store(s) since load (" + ViolationTicks.ToString(CultureInfo.InvariantCulture)
                + " tick(s) affected; worst: store " + WorstRefId.ToString(CultureInfo.InvariantCulture)
                + " granted " + WorstGranted.ToString("F2", CultureInfo.InvariantCulture)
                + " W vs credited " + WorstCredited.ToString("F2", CultureInfo.InvariantCulture)
                + " W at tick " + WorstTick.ToString(CultureInfo.InvariantCulture)
                + "; latest: store " + LastRefId.ToString(CultureInfo.InvariantCulture)
                + " granted " + LastGranted.ToString("F2", CultureInfo.InvariantCulture)
                + " W vs credited " + LastCredited.ToString("F2", CultureInfo.InvariantCulture)
                + " W at tick " + LastTick.ToString(CultureInfo.InvariantCulture)
                + "). Granted charge must land in the store whole on a served network. This is a"
                + " delivery-path bug; please report it.";
            Plugin.Log?.LogWarning(LastWarning);
        }

        /// <summary>World-load reset: drop the previous world's grants, credits, and counters.</summary>
        internal static void Clear()
        {
            _grants = new Dictionary<long, Grant>();
            _credits.Clear();
            _gateClosed.Clear();
            ViolationTicks = 0;
            ViolationStoreTicks = 0;
            _distinctStores.Clear();
            WorstRefId = 0L;
            WorstGranted = 0f;
            WorstCredited = 0f;
            WorstTick = 0;
            LastRefId = 0L;
            LastGranted = 0f;
            LastCredited = 0f;
            LastTick = 0;
            WarningsEmitted = 0;
            LastWarning = null;
            _storeTicksAtLastWarn = 0;
            _lastWarnTick = -WarnCooldownTicks;
        }
    }
}
