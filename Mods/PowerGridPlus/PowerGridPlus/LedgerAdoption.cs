using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;

namespace PowerGridPlus
{
    /// <summary>
    ///     Vanilla <c>_powerProvided</c> ledger adoption (Stage 3). The per-class private ledger on
    ///     Transformer / AreaPowerControl / PowerTransmitter / PowerReceiver is vanilla's deferred
    ///     billing handshake, it is NEVER zeroed by the game, and it persists into saves. Under
    ///     this mod the routed segmenters bill their FRESH allocator pull instead of the ledger,
    ///     which removes vanilla's restoring force: vanilla bills <c>min(cap, ledger)</c>, so any
    ///     residue self-drains through the next bills, while a fresh-pull bill never drains it.
    ///     The ledger therefore degenerates into a residue accumulator nobody owns, and whatever
    ///     lands in it stays forever, including into the save. PowerGridPlus owns billing for
    ///     these devices, so it settles their ledgers at a well-defined point in the tick instead
    ///     of leaving vanilla to accumulate into them.
    ///
    ///     <para><b>Derived per-tick ledger flow</b> (decompile 0.2.6403.27689). Both mutation
    ///     paths run inside ApplyState: a consumer is paid in per-provider CHUNKS, one
    ///     <c>device.ReceivePower(net, chunk)</c> per provider drained (PowerTick.ConsumePower,
    ///     lines 271820-271839), and a provider is charged ONCE with its total drained energy,
    ///     <c>device.UsePower(net, EnergyUsed)</c> (PowerProvider.ApplyPower, lines 271690-271696).
    ///     Per device kind, with T = TotalThrough (granted delivery), P = TotalPull (fresh bill,
    ///     T * max(m,1) + quiescent q), under exploit mitigation ON and the PowerTransmitterPlus
    ///     billing handshake held:</para>
    ///
    ///     <list type="bullet">
    ///       <item><b>Transformer</b> (vanilla UsePower / ReceivePower / GetUsedPower at
    ///       424757-424792): the output enforce adds the drained T; the input enforce subtracts,
    ///       via the PowerGridPlus ReceivePower prefix (TransformerExploitPatches), (chunk - q)
    ///       PER CHUNK with sub-quiescent chunks skipped, so a pull paid in k chunks
    ///       under-decrements by (k - 1) * q every fully paid tick: a slow UNBOUNDED positive leak
    ///       on any multi-provider input network. On top of that, any tick whose input pays less
    ///       than the output drained (ramp steps riding the one-tick PotentialLoad mirrors)
    ///       strands its difference permanently. The 2.2-2.4x-bill plateaus observed on the Luna
    ///       chargers and solar feeders are exactly this: about two ticks' worth of ramp
    ///       under-payment plus the chunk leak, stranded with no drain path.</item>
    ///       <item><b>PowerTransmitter half</b> (UsePower 408424-408430 adds the wireless-side
    ///       drain, at most the delivery-gated T; ReceivePower 408432-408444 subtracts the FULL
    ///       payment of the fresh bill P): steady drift T - P = -((m - 1) * T + q) per conducting
    ///       tick, strictly NEGATIVE (the free-energy credit class; confirmed live as 7 squashes
    ///       per tick, one per link). Ramp under-payment can strand a positive residue. Nothing
    ///       reads the transmitter ledger while the pair is enrolled: the PowerGridPlus
    ///       GetUsedPower prefix bills the fresh pull, and PowerTransmitterPlus's standalone debt
    ///       ceiling reads it only when no billing owner holds the handshake.</item>
    ///       <item><b>PowerReceiver half</b>: the relay cycle is LOAD-BEARING. Its
    ///       GetUsedPower(wireless) = min-lift(debt) (vanilla 408206-408230 plus the
    ///       PowerTransmitterPlus ReceiverDrainCapLiftPatch, always active) is what drains the
    ///       transmitter's advertise on the wireless network, and the resulting ReceivePower
    ///       traffic drives both halves' beam VisualizerIntensity. The healthy cycle carries at
    ///       most one tick's throughput T; any residue ABOVE T is stuck forever (the wireless
    ///       network pays exactly min(debt, T) per tick once debt >= T, so overshoot never
    ///       drains) and presents a permanent phantom shortfall on the wireless network
    ///       (Required = lifted debt vs Potential = T).</item>
    ///       <item><b>AreaPowerControl</b> (390985-391057): NOT settled. Vanilla UsePower drains
    ///       the internal cell against a POSITIVE ledger one tick after the cell covers an output
    ///       shortfall (the cell-discharge carrier), and GetUsedPower uses Max(ledger, quiescent)
    ///       so a negative never discounts a bill. Swept at load only.</item>
    ///     </list>
    ///
    ///     <para><b>Policy</b>, two lanes, both host-only inside the atomic tick:</para>
    ///
    ///     <list type="bullet">
    ///       <item><b>World-load sweep</b> (<see cref="RunSweepIfPending"/>): on the first atomic
    ///       tick after a world load (armed at plugin load and re-armed by
    ///       FaultRegistryLoadPatches, same lifecycle as UnknownBridgeCensus), zero the ledger on
    ///       every modeled segmenter class, both signs, BEFORE the first OBSERVE so a stale saved
    ///       credit (observed: -176,226 on transmitter 464520 in the Luna save) can never bill as
    ///       free energy and a stale debt never lump-bills. One Info line per zeroed device plus a
    ///       summary line.</item>
    ///       <item><b>Per-tick ENFORCE-tail settle</b> (<see cref="SettleEnforceTail"/>): after
    ///       all ApplyState passes (this tick's chunked mutations are complete), each enrolled
    ///       segmenter's ledger is SET to its vanilla-equivalent standing value: Transformer := T
    ///       (one tick's output drain awaiting billing; this also makes the LogicType.PowerActual
    ///       readout equal the documented "current throughput"); PowerTransmitter := 0 (its bill
    ///       was already paid in full this tick); PowerReceiver := min(debt, T) (preserves the
    ///       live relay cycle and the beam visuals, shears only the stuck overshoot).
    ///       Deterministic; kills both the negative-drift and the stranded-residue classes every
    ///       tick, and saves carry at most one tick's standing value. Writes are skipped when the
    ///       value is already within tolerance of its standing value.</item>
    ///     </list>
    ///
    ///     <para><b>Diagnostics</b>: the settle never warns for routine work. A pre-settle value
    ///     below -0.5 W increments a silent negative counter (the free-energy metric); a
    ///     pre-settle value at or above the high-water threshold, 4 x max(TotalPull, 250 W), or a
    ///     non-finite value, counts as a high-water event. With the per-tick settle in place a
    ///     healthy device's pre-settle value is bounded by one standing value plus one tick's
    ///     flow (about 2.5x the bill in the worst observed ramp tick), so 4x sits above
    ///     everything a healthy grid produces and zero warnings are expected on a healthy save.
    ///     High-water events log ONE GLOBALLY throttled warning per 600 ticks carrying the
    ///     running totals and the worst offender since the last warning, so a genuine leak stays
    ///     visible without a per-device line flood at every world event.</para>
    ///
    ///     <para>Field access: wireless halves route through
    ///     <see cref="PowerTransmitterPlusInterop"/> (PowerTransmitterPlus ModApi
    ///     GetTransferDebt / SetTransferDebt when the 1.9.0+ surface resolved, cached FieldInfo on
    ///     the vanilla field otherwise); Transformer / AreaPowerControl use cached FieldInfo here.
    ///     Nothing reflects per tick beyond Get/SetValue on the cached handles.</para>
    ///
    ///     <para>Threading: both entry points run on the power worker inside
    ///     AtomicElectricityTickPatch; managed state only. IC10 reads of the transformer ledger
    ///     (LogicType.PowerActual via TransformerLogicPatches) happen in the LOGIC phase after the
    ///     settle, so scripts see the standing throughput.</para>
    /// </summary>
    internal static class LedgerAdoption
    {
        private const float Tolerance = 0.5f;          // Watts; matches ConservationChecker
        private const int WarnCooldownTicks = 600;     // one GLOBAL high-water warning per ~5 minutes at 2 Hz
        private const float HighWaterFactor = 4f;      // threshold = 4 x max(TotalPull, floor)
        private const float HighWaterFloorW = 250f;    // keeps idle / tiny segs off zero thresholds

        private static readonly FieldInfo TransformerLedgerField =
            typeof(Transformer).GetField("_powerProvided", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo ApcLedgerField =
            typeof(AreaPowerControl).GetField("_powerProvided", BindingFlags.NonPublic | BindingFlags.Instance);

        private static bool _sweepPending = true;      // armed at plugin load; re-armed on world load

        // Running totals since load (reported in the global high-water warning).
        private static long _negativeSettles;          // pre-settle value < -0.5 W (free-energy hole squashed)
        private static long _highWaterEvents;          // pre-settle value >= threshold, or non-finite

        // Global throttle + worst-offender tracking for the high-water detector.
        private static int _lastGlobalWarnTick = int.MinValue;
        private static float _worstValue;
        private static float _worstThreshold;
        private static string _worstKind;
        private static long _worstRefId;

        /// <summary>Arm the world-load sweep to run on the next atomic tick.</summary>
        internal static void Arm() => _sweepPending = true;

        /// <summary>
        ///     Run the world-load ledger sweep once if armed; otherwise a single flag check. Called
        ///     at the top of the atomic tick, before OBSERVE.
        /// </summary>
        internal static void RunSweepIfPending()
        {
            if (!_sweepPending) return;
            _sweepPending = false;
            _negativeSettles = 0;
            _highWaterEvents = 0;
            _lastGlobalWarnTick = int.MinValue;
            _worstValue = 0f;
            _worstThreshold = 0f;
            _worstKind = null;
            _worstRefId = 0L;
            try
            {
                Sweep();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"Ledger adoption sweep failed (ledgers keep their saved values): {e.Message}");
            }
        }

        private static void Sweep()
        {
            int segmenters = 0, credits = 0, debts = 0;
            ElectricityManager.AllPoweredThings.ForEach(powered =>
            {
                switch (powered)
                {
                    case Transformer t:
                        segmenters++;
                        SweepField(t, TransformerLedgerField, "Transformer", t.ReferenceId, ref credits, ref debts);
                        break;
                    case AreaPowerControl apc:
                        segmenters++;
                        SweepField(apc, ApcLedgerField, "AreaPowerControl", apc.ReferenceId, ref credits, ref debts);
                        break;
                    case PowerTransmitter _:
                    case PowerReceiver _:
                        segmenters++;
                        var half = (WirelessPower)powered;
                        if (!PowerTransmitterPlusInterop.TryGetWirelessDebt(half, out float v)) break;
                        if (!SweepValue(v)) break;
                        if (PowerTransmitterPlusInterop.TrySetWirelessDebt(half, 0f))
                            LogSwept(powered.GetType().Name, half.ReferenceId, v, ref credits, ref debts);
                        break;
                }
            });
            Plugin.Log?.LogInfo(
                "Ledger adoption sweep: " + (credits + debts).ToString(CultureInfo.InvariantCulture)
                + " segmenter ledger(s) zeroed (" + credits.ToString(CultureInfo.InvariantCulture)
                + " stale credit(s), " + debts.ToString(CultureInfo.InvariantCulture)
                + " residual debt(s)) across " + segmenters.ToString(CultureInfo.InvariantCulture)
                + " modeled segmenter(s).");
        }

        // NaN counts as sweep-worthy (poison is reset like any stale value).
        private static bool SweepValue(float v) => float.IsNaN(v) || v > Tolerance || v < -Tolerance;

        private static void SweepField(object instance, FieldInfo field, string kind, long refId,
            ref int credits, ref int debts)
        {
            if (field == null) return;
            float v;
            try { v = field.GetValue(instance) is float f ? f : float.NaN; }
            catch { return; }
            if (!SweepValue(v)) return;
            try { field.SetValue(instance, 0f); }
            catch { return; }
            LogSwept(kind, refId, v, ref credits, ref debts);
        }

        private static void LogSwept(string kind, long refId, float value, ref int credits, ref int debts)
        {
            bool credit = float.IsNaN(value) || value < 0f;
            if (credit) credits++; else debts++;
            Plugin.Log?.LogInfo(
                "Ledger adoption: zeroed " + (credit ? "stale credit" : "residual debt")
                + " on " + kind + " " + refId.ToString(CultureInfo.InvariantCulture)
                + " (_powerProvided was " + value.ToString("F2", CultureInfo.InvariantCulture) + ").");
        }

        /// <summary>
        ///     ENFORCE tail: settle every enrolled, settle-eligible segmenter's ledger to its
        ///     vanilla-equivalent standing value for this tick (see the class doc for the per-kind
        ///     derivation). Samples the pre-settle value first for the silent negative counter and
        ///     the warn-only high-water detector; never warns for routine settling.
        /// </summary>
        internal static void SettleEnforceTail(int currentTick)
        {
            var roster = PoweredPresentation.Roster;
            for (int i = 0; i < roster.Count; i++)
            {
                var e = roster[i];
                if (!e.SettleLedger) continue;
                switch (e.Anchor)
                {
                    case Transformer t:
                        // Standing value = this tick's output drain awaiting billing (vanilla
                        // semantics), which is exactly the granted TotalThrough; also the value
                        // LogicType.PowerActual is documented to mean.
                        SettleField(t, TransformerLedgerField, "Transformer", e.RefId,
                            e.TotalThrough, e.TotalPull, currentTick);
                        break;
                    case PowerTransmitter pt:
                        // The fresh pull was billed and paid on the input network this tick;
                        // nothing remains owed. 0 also squashes the structural negative drift.
                        SettleWireless(pt, "PowerTransmitter", e.RefId, 0f, e.TotalPull, currentTick);
                        if (e.Partner is PowerReceiver pr)
                        {
                            // Preserve the live relay cycle (the receiver's debt drives the
                            // wireless-side settle and the beam visuals) up to one tick's
                            // throughput; shear only the stuck overshoot above it.
                            if (PowerTransmitterPlusInterop.TryGetWirelessDebt(pr, out float rxDebt))
                            {
                                float standing = rxDebt < e.TotalThrough ? rxDebt : e.TotalThrough;
                                if (standing < 0f || float.IsNaN(standing) || float.IsInfinity(standing))
                                    standing = 0f;
                                SettleWireless(pr, "PowerReceiver", pr.ReferenceId, standing,
                                    e.TotalPull, currentTick);
                            }
                        }
                        break;
                }
            }
        }

        private static void SettleField(object instance, FieldInfo field, string kind, long refId,
            float standing, float totalPull, int currentTick)
        {
            if (field == null) return;
            float v;
            try { v = field.GetValue(instance) is float f ? f : float.NaN; }
            catch { return; }
            Observe(kind, refId, v, totalPull, currentTick);
            if (!NeedsWrite(v, standing)) return;
            try { field.SetValue(instance, standing); }
            catch { }
        }

        private static void SettleWireless(WirelessPower half, string kind, long refId,
            float standing, float totalPull, int currentTick)
        {
            if (half == null) return;
            if (!PowerTransmitterPlusInterop.TryGetWirelessDebt(half, out float v)) return;
            Observe(kind, refId, v, totalPull, currentTick);
            if (!NeedsWrite(v, standing)) return;
            PowerTransmitterPlusInterop.TrySetWirelessDebt(half, standing);
        }

        private static bool NeedsWrite(float value, float standing)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return true;
            float diff = value - standing;
            return diff > 0.01f || diff < -0.01f;
        }

        // Pre-settle diagnostics: count negatives silently, feed the warn-only high-water
        // detector. The detector is globally throttled; the only per-event state kept is the
        // worst offender since the last warning.
        private static void Observe(string kind, long refId, float value, float totalPull, int currentTick)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                NoteHighWater(kind, refId, value, 0f, currentTick);
                return;
            }
            if (value < -Tolerance)
            {
                _negativeSettles++;
                return;
            }
            float threshold = HighWaterFactor * (totalPull > HighWaterFloorW ? totalPull : HighWaterFloorW);
            if (value >= threshold)
                NoteHighWater(kind, refId, value, threshold, currentTick);
        }

        private static void NoteHighWater(string kind, long refId, float value, float threshold, int currentTick)
        {
            _highWaterEvents++;
            // Rank offenders by how far the value exceeds its own threshold; a non-finite value
            // (threshold 0) ranks worst.
            float worstExcess = _worstKind == null ? float.MinValue
                : (_worstThreshold > 0f ? _worstValue - _worstThreshold : float.MaxValue);
            float excess = threshold > 0f ? value - threshold : float.MaxValue;
            if (excess >= worstExcess)
            {
                _worstValue = value;
                _worstThreshold = threshold;
                _worstKind = kind;
                _worstRefId = refId;
            }
            if (currentTick - _lastGlobalWarnTick < WarnCooldownTicks) return;
            _lastGlobalWarnTick = currentTick;
            Plugin.Log?.LogWarning(
                "[PowerGridPlus] Ledger high-water: " + _highWaterEvents.ToString(CultureInfo.InvariantCulture)
                + " event(s) since load (worst: " + _worstKind + " "
                + _worstRefId.ToString(CultureInfo.InvariantCulture)
                + " at " + _worstValue.ToString("F2", CultureInfo.InvariantCulture)
                + " W vs threshold " + _worstThreshold.ToString("F2", CultureInfo.InvariantCulture)
                + " W). A recurring high-water means something upstream is accumulating into a"
                + " ledger PowerGridPlus owns. Negative settles since load: "
                + _negativeSettles.ToString(CultureInfo.InvariantCulture) + ".");
            _worstKind = null;
            _worstValue = 0f;
            _worstThreshold = 0f;
            _worstRefId = 0L;
        }
    }
}
