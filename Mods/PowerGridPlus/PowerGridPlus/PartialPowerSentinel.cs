using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;

namespace PowerGridPlus
{
    /// <summary>
    ///     Partial-power sentinel: the no-partial-power contract detector (POWER.md §8.0.0.2 /
    ///     §8.8). Shedding exists so a device is never activated with partial power: on every
    ///     network the allocator marked fully SERVED this tick, vanilla's ratio scaling
    ///     (<c>PowerTick.ApplyState</c>, <c>usedPower *= _powerRatio</c>) must never engage. The
    ///     allocator advertises exact grants, so a served network lands at
    ///     <c>Potential >= Required</c> and every healthy path stores a ratio of exactly 1; a
    ///     ratio below 1 on a served network means the published presentation diverged from the
    ///     allocator's decision (an under-advertising cache, a billing patch regression, a device
    ///     the roster missed). That class of bug is otherwise invisible: devices flicker or scale
    ///     while every allocator invariant still holds.
    ///
    ///     <para><b>Scope: three gates, all required.</b> (1) The network is in this tick's
    ///     <see cref="ShortfallDiagnostics"/> snapshot (absence == outside allocator scope,
    ///     vanilla's business by design). (2) It is classified
    ///     <see cref="ShortfallDiagnostics.Served"/>: on an unmet managed network
    ///     (Dry / Throttled / Deadlock) a sub-1 ratio is vanilla's designed honest-darkness
    ///     path (the device loop powers consumers OFF whole; the ratio only scales the billing
    ///     of the dying tick), and Deadlock is the shortfall census's surface, not this one.
    ///     (3) It is RATIO-DEPRIVABLE (<see cref="ShortfallDiagnostics.InRatioScope"/>, stamped
    ///     by the ALLOCATE publish tail): at least one non-segmenter power device, or at least
    ///     one storage charge request this tick. Derivation from vanilla ApplyState (0.2.6403
    ///     decompile): the ratio's only effects are <c>usedPower *= _powerRatio</c> feeding
    ///     ConsumePower and the power-on/off ladder. A plain consumer is deprived directly
    ///     (scaled delivery, then powered off); a charging store is deprived because
    ///     <c>Battery.ReceivePower</c> adds the ratio-scaled chunk stream to <c>PowerStored</c>
    ///     (delivered charge = grant * ratio), so storage-bearing nets MUST stay in scope. A
    ///     net whose every power member is a routed segmenter is INERT: each member's bill and
    ///     advertise are cache-governed, delivered downstream flow is governed by the member's
    ///     own published cache on ITS output network (never recomputed from this network's
    ///     ratio), and the Powered False edge is blocked by the AllowSetPower postfixes plus
    ///     the ENFORCE-tail reconcile. The loudest inert case is every wireless CARRIER
    ///     network: membership is exactly the dish halves (both bridges), and under the
    ///     billing handshake its vanilla mirrors are structurally asymmetric (Required = the
    ///     receiver's unclamped debt-lift drain, Potential = the delivery-gate-clamped
    ///     advertise), so ratio below 1 is its NORMAL conducting state; a 20-minute live soak
    ///     delivered the full charge at carrier ratio 0.27 with the conservation checker
    ///     silent. Tower-top hop nets (receiver + transformer only) are the same shape.
    ///     Excluding bridge-only nets loses no protection: they contain no member whose
    ///     operation or stored energy the ratio can shrink.</para>
    ///
    ///     <para><b>Violation threshold: ratio &lt; 1f exactly, no invented epsilon.</b>
    ///     Derivation from the vanilla computation (PowerTick.CacheState, 0.2.6403 decompile):
    ///     every healthy path assigns the LITERAL 1f (the <c>Potential > 0 &amp;&amp; Required > 0</c>
    ///     guard's else-branch, the power-met branch inside the clamp, and the clamp's upper
    ///     bound), so 1f is an exact sentinel value, and the only sub-1 producer is the division
    ///     <c>Potential / Required</c> on an unmet demanded network. On top of that the mod's own
    ///     CacheState postfix (<see cref="Patches.PowerTickPatches.CacheState_PowerMetBoundary"/>)
    ///     forces the literal 1f whenever <c>Potential >= Required - 0.01</c> W, so any ratio that
    ///     survives below 1f already implies a supply gap above the allocator's 0.01 W epsilon,
    ///     pre-filtered at the source. IEEE division of near-equal operands can round a sub-half-ulp
    ///     gap to exactly 1f; that reads as healthy, which is correct at watt scale.</para>
    ///
    ///     <para><b>Always-on, no config entry</b> (same posture as the ledger audit; the
    ///     ConservationChecker toggle predates that pattern). Counts are exact and never
    ///     throttled; only the log line is: one aggregated warning, at most once per 600 ticks,
    ///     whenever new violations were recorded since the last line, carrying the totals since
    ///     load, the worst offender since load (lowest ratio), and the latest offender. Zero
    ///     violations produce zero log lines. Healthy cost per tick: one snapshot probe and one
    ///     float compare per managed network.</para>
    ///
    ///     <para><b>Read point.</b> The ENFORCE tail (AtomicElectricityTickPatch), after every
    ///     network's ApplyState has settled on the power worker: the last CacheState of the tick
    ///     has run (including the BreakSingleFuse / BreakSingleCable re-cache), so the field holds
    ///     the exact ratio the device loop scaled with. <c>_powerRatio</c> is read via a Harmony
    ///     FieldRef exactly like <see cref="Patches.PowerTickPatches"/>. Networks the ENFORCE pass
    ///     skipped (null PowerTick, empty DeviceList) are skipped here too: their PowerTick state
    ///     is stale.</para>
    ///
    ///     <para><b>Positive control.</b> ScenarioRunner's <c>pgp-partial-power-injection</c>
    ///     scenario reflection-drives <see cref="IsViolation"/> with synthetic cases, then
    ///     deliberately under-advertises one supplier's published presentation entry after
    ///     ALLOCATE for a bounded window and asserts these counters rise and the worst / latest
    ///     captures name the injected net (field asserts, immune to the warning throttle). Keep
    ///     the member names stable; the scenario resolves them by name.</para>
    ///
    ///     <para>Threading: all state is touched from the power worker only (RunEnforceTail
    ///     inside the atomic tick, Clear from the world-load postfix while no tick runs).</para>
    /// </summary>
    internal static class PartialPowerSentinel
    {
        private const int WarnCooldownTicks = 600;   // one aggregated warning per ~5 minutes at 2 Hz; matches LedgerAdoption

        private static readonly AccessTools.FieldRef<PowerTick, float> PowerRatioRef =
            AccessTools.FieldRefAccess<PowerTick, float>("_powerRatio");

        // Exact running totals since load. Never throttled; reported in the aggregated warning.
        // Reflection surface for the pgp-partial-power-injection positive control; keep names stable.
        internal static long ViolationTicks { get; private set; }      // ticks with >= 1 violating net
        internal static long ViolationNetTicks { get; private set; }   // (net, tick) violation observations
        internal static int DistinctNetCount => _distinctNets.Count;   // distinct violating nets since load
        internal static long WorstNetId { get; private set; }          // lowest-ratio capture since load
        internal static float WorstRatio { get; private set; } = 1f;
        internal static float WorstRequired { get; private set; }
        internal static float WorstPotential { get; private set; }
        internal static int WorstTick { get; private set; }
        internal static int WarningsEmitted { get; private set; }
        internal static string LastWarning { get; private set; }

        private static readonly HashSet<long> _distinctNets = new HashSet<long>();

        // Latest offender: recency for the warning line (the worst capture alone would pin an
        // old burst) and the injection fixture's throttle-immune assert surface.
        internal static long LastNetId { get; private set; }
        internal static float LastRatio { get; private set; } = 1f;
        internal static int LastTick { get; private set; }

        // Log throttle: counts are exact above; only the line is throttled.
        private static long _netTicksAtLastWarn;
        private static int _lastWarnTick = -WarnCooldownTicks;

        // Per-sweep scratch, hoisted so the AllCableNetworks visit lambda allocates nothing.
        private static int _tickInFlight;
        private static bool _anyThisTick;
        private static readonly System.Action<CableNetwork> _visit = VisitNet;

        /// <summary>
        ///     The pure violation predicate: (in the ratio contract's scope this tick, shortfall
        ///     class, vanilla power ratio) -> is the no-partial-power contract broken.
        ///     <paramref name="inRatioScope"/> is the conjunction the caller computes from the
        ///     published snapshot: present in this tick's allocator snapshot AND ratio-deprivable
        ///     (see the class doc's scope derivation; wireless carrier and bridge-only hop nets
        ///     read false). Factored out so the ScenarioRunner fixture can drive it with
        ///     synthetic cases; no live-net or Unity dependency.
        /// </summary>
        internal static bool IsViolation(bool inRatioScope, byte shortfallClass, float ratio)
        {
            return inRatioScope
                   && shortfallClass == ShortfallDiagnostics.Served
                   && ratio < 1f;
        }

        /// <summary>
        ///     ENFORCE tail: sweep every cable network, count contract violations on
        ///     allocator-served networks, and emit the throttled aggregated warning when due.
        /// </summary>
        internal static void RunEnforceTail(int currentTick)
        {
            _tickInFlight = currentTick;
            _anyThisTick = false;
            CableNetwork.AllCableNetworks.ForEach(_visit);
            if (_anyThisTick) ViolationTicks++;
            EmitWarningIfDue(currentTick);
        }

        private static void VisitNet(CableNetwork net)
        {
            if (net == null) return;
            var pt = net.PowerTick;
            if (pt == null) return;
            if (net.DeviceList.Count == 0) return;   // ENFORCE skipped it: PowerTick state is stale

            long id = net.ReferenceId;
            bool inScope = ShortfallDiagnostics.TryClassify(id, out byte cls)
                           && ShortfallDiagnostics.InRatioScope(id);
            float ratio = PowerRatioRef(pt);
            if (!IsViolation(inScope, cls, ratio)) return;

            _anyThisTick = true;
            ViolationNetTicks++;
            _distinctNets.Add(id);
            LastNetId = id;
            LastRatio = ratio;
            LastTick = _tickInFlight;
            if (ratio < WorstRatio || WorstNetId == 0L)
            {
                WorstNetId = id;
                WorstRatio = ratio;
                WorstRequired = pt.Required;
                WorstPotential = pt.Potential;
                WorstTick = _tickInFlight;
            }
        }

        // One aggregated warning per cooldown window while new violations arrive; zero violations
        // produce zero log lines, ever. First violation after load warns immediately.
        private static void EmitWarningIfDue(int currentTick)
        {
            if (ViolationNetTicks == _netTicksAtLastWarn) return;
            if (currentTick - _lastWarnTick < WarnCooldownTicks) return;
            _lastWarnTick = currentTick;
            _netTicksAtLastWarn = ViolationNetTicks;
            WarningsEmitted++;
            LastWarning =
                "[PowerGridPlus] Partial-power sentinel: vanilla ratio scaling engaged on "
                + ViolationNetTicks.ToString(CultureInfo.InvariantCulture)
                + " net-tick(s) across " + DistinctNetCount.ToString(CultureInfo.InvariantCulture)
                + " allocator-served network(s) since load ("
                + ViolationTicks.ToString(CultureInfo.InvariantCulture)
                + " tick(s) affected; worst: network " + WorstNetId.ToString(CultureInfo.InvariantCulture)
                + " ratio " + WorstRatio.ToString("F6", CultureInfo.InvariantCulture)
                + " with Required " + WorstRequired.ToString("F2", CultureInfo.InvariantCulture)
                + " W vs Potential " + WorstPotential.ToString("F2", CultureInfo.InvariantCulture)
                + " W at tick " + WorstTick.ToString(CultureInfo.InvariantCulture)
                + "; latest: network " + LastNetId.ToString(CultureInfo.InvariantCulture)
                + " ratio " + LastRatio.ToString("F6", CultureInfo.InvariantCulture)
                + " at tick " + LastTick.ToString(CultureInfo.InvariantCulture)
                + "). The allocator marked these networks fully served, so partial scaling must"
                + " never engage on them. This is an allocator presentation bug; please report it.";
            Plugin.Log?.LogWarning(LastWarning);
        }

        /// <summary>World-load reset: drop the previous world's counters and throttle state.</summary>
        internal static void Clear()
        {
            ViolationTicks = 0;
            ViolationNetTicks = 0;
            _distinctNets.Clear();
            WorstNetId = 0L;
            WorstRatio = 1f;
            WorstRequired = 0f;
            WorstPotential = 0f;
            WorstTick = 0;
            WarningsEmitted = 0;
            LastWarning = null;
            LastNetId = 0L;
            LastRatio = 1f;
            LastTick = 0;
            _netTicksAtLastWarn = 0;
            _lastWarnTick = -WarnCooldownTicks;
        }
    }
}
