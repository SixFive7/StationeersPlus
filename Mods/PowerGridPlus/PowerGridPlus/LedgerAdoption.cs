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
    ///     billing handshake and is NEVER zeroed by the game. At 0.2.6403 it is not serialized
    ///     into saves (no SaveData member carries it; see the write-site census in
    ///     Patches/LedgerAuditPatches.cs), so a nonzero value is runtime accumulation within the
    ///     session, an older-version save, or an external writer. Under this mod the routed
    ///     segmenters bill their FRESH allocator pull instead of the ledger, which removes
    ///     vanilla's restoring force: vanilla bills <c>min(cap, ledger)</c>, so any residue
    ///     self-drains through the next bills, while a fresh-pull bill never drains it. The
    ///     ledger therefore degenerates into a residue accumulator nobody owns, and whatever
    ///     lands in it stays for the rest of the session. PowerGridPlus owns billing for
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
    ///       summary line. The sweep also clears the ledger-audit tracking map, so the boundary
    ///       check below never compares across a world load or against a value the sweep just
    ///       zeroed.</item>
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
    ///     <para><b>Diagnostics: exact ledger audit</b> (always-on, no config entry; replaces the
    ///     former approximate 4x high-water detector). The settle makes the ledger's lifecycle
    ///     fully deterministic, so ownership violations are detected EXACTLY rather than by
    ///     threshold, in four anomaly classes:</para>
    ///
    ///     <list type="bullet">
    ///       <item><b>Boundary</b> (layer B, <see cref="AuditTickBoundary"/>): the settle records
    ///       each owned ledger's post-settle value; nothing legitimate writes the field between
    ///       that write and the next tick's power section (the only vanilla writers live inside
    ///       ApplyState, see the LedgerAuditPatches census). At the start of the next atomic tick
    ///       the field must equal the recorded value EXACTLY (float identity; we wrote it, and
    ///       both write routes store the exact float). Any deviation is an out-of-band writer.
    ///       Checked only for devices settled last tick and only when no world load intervened
    ///       (the sweep clears the map).</item>
    ///       <item><b>Bracket discontinuity</b> (layer A+, <see cref="NoteMutation"/>): the
    ///       LedgerAuditPatches wrappers bracket every legitimate mutation with Priority.First /
    ///       Priority.Last captures; each mutation's BEFORE must equal the last recorded AFTER
    ///       (or the boundary value for the first mutation of the tick). A discontinuity is a
    ///       foreign write BETWEEN two known operations; the jump is folded into the shadow sum
    ///       so it is counted exactly once and does not also trip the settle-tail check.</item>
    ///       <item><b>Unobserved path</b> (layer A+ tail, inside <see cref="SettleEnforceTail"/>):
    ///       observed deltas accumulate into a per-device double shadow sum; at the ENFORCE tail,
    ///       before the settle, the field must equal boundary + shadow within 0.01 W. A miss is a
    ///       foreign write that did not pass between two observed operations (e.g. after the last
    ///       mutation, or on a device with no mutations this tick).</item>
    ///       <item><b>Non-finite</b>: a NaN / Infinity pre-settle value; the settle repairs it to
    ///       the standing value on the spot.</item>
    ///     </list>
    ///
    ///     <para>Counts are exact and never throttled; only the log line is throttled: the tick
    ///     boundary emits ONE aggregated warning, at most once per 600 ticks, whenever new
    ///     anomalies were recorded since the last line, carrying the totals since load per class
    ///     and the worst offender since the last warning (so a short burst is still fully
    ///     reported once the window reopens). Zero anomalies produce zero log lines. Devices
    ///     leaving the enrolled set are dropped from tracking at the settle tail; devices
    ///     entering it are audited from their first settled tick onward.</para>
    ///
    ///     <para>Field access: wireless halves route through
    ///     <see cref="PowerTransmitterPlusInterop"/> (PowerTransmitterPlus ModApi
    ///     GetTransferDebt / SetTransferDebt when the 1.9.0+ surface resolved, cached FieldInfo on
    ///     the vanilla field otherwise); Transformer / AreaPowerControl use cached FieldInfo here.
    ///     Nothing reflects per tick beyond Get/SetValue on the cached handles. The audit wrappers
    ///     read the fields via Harmony injection (no reflection at runtime).</para>
    ///
    ///     <para>Threading: all entry points (boundary check, mutation notes, settle) run on the
    ///     power worker inside AtomicElectricityTickPatch and vanilla ApplyState; the tracking map
    ///     and counters are touched only from that worker and cleared by the world-load sweep.
    ///     IC10 reads of the transformer ledger (LogicType.PowerActual via TransformerLogicPatches)
    ///     happen in the LOGIC phase after the settle, so scripts see the standing throughput.</para>
    /// </summary>
    internal static class LedgerAdoption
    {
        private const float Tolerance = 0.5f;          // Watts; matches ConservationChecker
        private const int WarnCooldownTicks = 600;     // one GLOBAL audit warning per ~5 minutes at 2 Hz
        private const float TailToleranceW = 0.01f;    // |field - (boundary + shadow)| beyond this = unobserved write

        /// <summary>Observed ledger operations, the bracket-window endpoints for audit reporting.</summary>
        internal enum Site : byte
        {
            Boundary,                  // the tick-start identity check re-baseline
            TransformerUsePower,
            TransformerReceivePower,
            TransmitterUsePower,
            TransmitterReceivePower,
            ReceiverUsePower,
            ReceiverReceivePower,
            Settle,                    // the ENFORCE-tail settle write
        }

        private enum AuditKind : byte { Transformer, PowerTransmitter, PowerReceiver }

        private enum AnomalyClass : byte { None, Boundary, UnobservedPath, BracketDiscontinuity, NonFinite }

        /// <summary>
        ///     Per-device audit state, keyed by ReferenceId. Entries are created at the first
        ///     settle that records the device and reused every tick after (no steady-state
        ///     allocation); a device that leaves the enrolled set is purged at the settle tail.
        /// </summary>
        private sealed class AuditEntry
        {
            public object Device;      // Transformer or WirelessPower; the boundary-read route
            public AuditKind Kind;
            public float Settled;      // post-settle field value (layer B reference)
            public float Baseline;     // field value observed at this tick's boundary
            public double Shadow;      // sum of observed in-tick deltas since the boundary
            public float LastAfter;    // field value after the last observed operation
            public Site LastSite;      // last observed operation (bracket-window reporting)
            public int SettleStamp;    // tick of the settle that last recorded this entry (purge)
            public bool Armed;         // boundary ran since the last settle: in-tick checks active
        }

        private static readonly FieldInfo TransformerLedgerField =
            typeof(Transformer).GetField("_powerProvided", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo ApcLedgerField =
            typeof(AreaPowerControl).GetField("_powerProvided", BindingFlags.NonPublic | BindingFlags.Instance);

        private static bool _sweepPending = true;      // armed at plugin load; re-armed on world load

        // Audit tracking map + purge scratch (power worker only; cleared by the sweep).
        private static readonly Dictionary<long, AuditEntry> _audit = new Dictionary<long, AuditEntry>();
        private static readonly List<long> _purgeScratch = new List<long>();

        // Exact running totals since load. Never throttled; reported in the aggregated warning.
        private static long _negativeSettles;          // pre-settle value < -0.5 W (free-energy hole squashed)
        private static long _boundaryAnomalies;        // field != recorded settled value at tick start
        private static long _unobservedAnomalies;      // pre-settle field != boundary + shadow
        private static long _bracketAnomalies;         // mutation BEFORE != last recorded AFTER
        private static long _nonFiniteAnomalies;       // NaN / Infinity pre-settle value

        // Global log throttle + worst-offender capture since the last warning. Raw numbers only;
        // the line is formatted at warn time, so the anomaly path allocates nothing. The line is
        // emitted from the tick-boundary check (one flag comparison per tick), which guarantees a
        // burst of anomalies is reported even if it stops before the throttle window reopens.
        private static long _totalAtLastWarn;
        private static int _lastGlobalWarnTick = -WarnCooldownTicks;
        private static AnomalyClass _worstClass = AnomalyClass.None;
        private static float _worstMagnitude;
        private static AuditKind _worstKind;
        private static long _worstRefId;
        private static float _worstA;                  // boundary: settled; unobserved: baseline; bracket: last AFTER
        private static float _worstB;                  // boundary/unobserved: found; bracket: found BEFORE; non-finite: the value
        private static double _worstShadow;            // unobserved: the shadow sum
        private static Site _worstSiteFrom;
        private static Site _worstSiteTo;

        /// <summary>Arm the world-load sweep to run on the next atomic tick.</summary>
        internal static void Arm() => _sweepPending = true;

        /// <summary>True once the world-load sweep has fired for the current world (the save/load
        /// self-check's third clause; read right after RunSweepIfPending on the same tick).</summary>
        internal static bool SweepHasRun => !_sweepPending;

        /// <summary>
        ///     Run the world-load ledger sweep once if armed; otherwise a single flag check. Called
        ///     at the top of the atomic tick, before OBSERVE and before <see cref="AuditTickBoundary"/>,
        ///     so a fired sweep always clears the audit map before any boundary comparison.
        /// </summary>
        internal static void RunSweepIfPending()
        {
            if (!_sweepPending) return;
            _sweepPending = false;
            _negativeSettles = 0;
            _boundaryAnomalies = 0;
            _unobservedAnomalies = 0;
            _bracketAnomalies = 0;
            _nonFiniteAnomalies = 0;
            _totalAtLastWarn = 0;
            _lastGlobalWarnTick = -WarnCooldownTicks;
            ResetWorst();
            _audit.Clear();
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

        // ------------------------------------------------------------------
        // Layer B: tick-boundary identity check.
        // ------------------------------------------------------------------

        /// <summary>
        ///     Verify every ledger settled last tick still holds its recorded value EXACTLY, then
        ///     re-baseline the per-device shadow accounting for this tick. Called at the top of the
        ///     atomic tick, immediately after <see cref="RunSweepIfPending"/> (a fired sweep leaves
        ///     the map empty, so a fresh world or hot-swapped save is never compared) and before
        ///     the first OBSERVE (no mutation can precede the check; the only vanilla writers run
        ///     inside ApplyState). A deviation is an out-of-band writer between last tick's settle
        ///     and now. Re-baselining to the FOUND value makes one foreign write count exactly
        ///     once (here), not again in the bracket or settle-tail checks.
        /// </summary>
        internal static void AuditTickBoundary(int currentTick)
        {
            foreach (var kv in _audit)
            {
                var e = kv.Value;
                if (!TryReadLedger(e, out float current))
                {
                    e.Armed = false;   // unreadable this tick: skip the in-tick checks too
                    continue;
                }
                // Exact float identity: both settle routes store the exact float we recorded, and
                // nothing legitimate writes between the settle and this point. A NaN also lands
                // here (NaN != anything), which is correct: the settle never records a non-finite
                // value, so a non-finite at the boundary IS a foreign write.
                if (!(current == e.Settled))
                {
                    _boundaryAnomalies++;
                    NoteWorst(AnomalyClass.Boundary, e.Kind, kv.Key, Magnitude(current - e.Settled),
                        e.Settled, current, 0.0, Site.Settle, Site.Boundary);
                }
                e.Baseline = current;
                e.Shadow = 0.0;
                e.LastAfter = current;
                e.LastSite = Site.Boundary;
                e.Armed = true;
            }
            EmitWarningIfDue(currentTick);
        }

        // ------------------------------------------------------------------
        // Layer A+: observed shadow sum with bracket continuity.
        // ------------------------------------------------------------------

        /// <summary>
        ///     Record one observed ledger mutation (called by the LedgerAuditPatches postfixes with
        ///     the Priority.First BEFORE and Priority.Last AFTER captures). Untracked devices (not
        ///     settled last tick: the APC, unenrolled segmenters, everything on a client peer) fall
        ///     out on the dictionary miss. Cost on the hot path: one lookup plus two double adds.
        /// </summary>
        internal static void NoteMutation(long refId, Site site, float before, float after)
        {
            if (!_audit.TryGetValue(refId, out var e) || !e.Armed) return;
            if (!(before == e.LastAfter))
            {
                _bracketAnomalies++;
                NoteWorst(AnomalyClass.BracketDiscontinuity, e.Kind, refId, Magnitude(before - e.LastAfter),
                    e.LastAfter, before, 0.0, e.LastSite, site);
                // Fold the foreign jump into the shadow so the settle-tail check stays exact for
                // any REMAINING unobserved writes; this discontinuity is already counted here.
                e.Shadow += (double)before - e.LastAfter;
            }
            e.Shadow += (double)after - before;
            e.LastAfter = after;
            e.LastSite = site;
        }

        /// <summary>
        ///     ENFORCE tail: settle every enrolled, settle-eligible segmenter's ledger to its
        ///     vanilla-equivalent standing value for this tick (see the class doc for the per-kind
        ///     derivation). Before each write the pre-settle value feeds the exact audit: the
        ///     non-finite backstop, the silent negative counter, and the unobserved-path check
        ///     (field == boundary + shadow within 0.01 W). After the settle the resulting value is
        ///     recorded for the next tick's boundary check; devices no longer enrolled are dropped
        ///     from tracking.
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
                        SettleTransformer(t, e.RefId, e.TotalThrough, currentTick);
                        break;
                    case PowerTransmitter pt:
                        // The fresh pull was billed and paid on the input network this tick;
                        // nothing remains owed. 0 also squashes the structural negative drift.
                        SettleWireless(pt, AuditKind.PowerTransmitter, e.RefId, 0f, currentTick);
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
                                SettleWireless(pr, AuditKind.PowerReceiver, pr.ReferenceId, standing,
                                    currentTick);
                            }
                        }
                        break;
                }
            }
            PurgeStale(currentTick);
        }

        private static void SettleTransformer(Transformer t, long refId, float standing, int currentTick)
        {
            var field = TransformerLedgerField;
            if (field == null) return;
            float v;
            try { v = field.GetValue(t) is float f ? f : float.NaN; }
            catch { return; }
            AuditPreSettle(AuditKind.Transformer, refId, v);
            if (NeedsWrite(v, standing))
            {
                try { field.SetValue(t, standing); }
                catch { return; }   // value now unknown: leave the device untracked this tick
                // Re-read so layer B compares against what the field actually holds, not what we
                // asked for (defensive; SetValue stores the exact float today).
                try { v = field.GetValue(t) is float f ? f : float.NaN; }
                catch { return; }
            }
            RecordSettled(t, AuditKind.Transformer, refId, v, currentTick);
        }

        private static void SettleWireless(WirelessPower half, AuditKind kind, long refId,
            float standing, int currentTick)
        {
            if (half == null) return;
            if (!PowerTransmitterPlusInterop.TryGetWirelessDebt(half, out float v)) return;
            AuditPreSettle(kind, refId, v);
            if (NeedsWrite(v, standing))
            {
                if (!PowerTransmitterPlusInterop.TrySetWirelessDebt(half, standing)) return;
                // Re-read through the same route so layer B holds the exact stored value even if a
                // future PowerTransmitterPlus ModApi build clamps or transforms the write.
                if (!PowerTransmitterPlusInterop.TryGetWirelessDebt(half, out v)) return;
            }
            RecordSettled(half, kind, refId, v, currentTick);
        }

        private static bool NeedsWrite(float value, float standing)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return true;
            float diff = value - standing;
            return diff > 0.01f || diff < -0.01f;
        }

        // Pre-settle audit: the non-finite backstop, the silent negative counter (the free-energy
        // metric), and the layer A+ settle-tail identity (field == boundary + shadow).
        private static void AuditPreSettle(AuditKind kind, long refId, float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                _nonFiniteAnomalies++;
                NoteWorst(AnomalyClass.NonFinite, kind, refId, float.PositiveInfinity,
                    0f, value, 0.0, Site.Boundary, Site.Settle);
                return;   // the settle repairs it (NeedsWrite is true for non-finite)
            }
            if (value < -Tolerance) _negativeSettles++;
            if (_audit.TryGetValue(refId, out var e) && e.Armed)
            {
                double expected = e.Baseline + e.Shadow;
                double diff = value - expected;
                if (diff > TailToleranceW || diff < -TailToleranceW)
                {
                    _unobservedAnomalies++;
                    NoteWorst(AnomalyClass.UnobservedPath, kind, refId, Magnitude((float)diff),
                        e.Baseline, value, e.Shadow, e.LastSite, Site.Settle);
                }
            }
        }

        private static void RecordSettled(object device, AuditKind kind, long refId,
            float settledValue, int currentTick)
        {
            if (!_audit.TryGetValue(refId, out var e))
            {
                e = new AuditEntry();
                _audit[refId] = e;
            }
            e.Device = device;
            e.Kind = kind;
            e.Settled = settledValue;
            e.SettleStamp = currentTick;
            e.Armed = false;           // armed again by the next tick's boundary check
            e.LastSite = Site.Settle;
        }

        // Drop tracking for every device the settle did not touch this tick (left the enrolled
        // set, lost settle eligibility, or its field became unreachable). Scratch list reused.
        private static void PurgeStale(int currentTick)
        {
            _purgeScratch.Clear();
            foreach (var kv in _audit)
            {
                if (kv.Value.SettleStamp != currentTick)
                    _purgeScratch.Add(kv.Key);
            }
            for (int i = 0; i < _purgeScratch.Count; i++)
                _audit.Remove(_purgeScratch[i]);
            _purgeScratch.Clear();
        }

        private static bool TryReadLedger(AuditEntry e, out float value)
        {
            value = 0f;
            if (e.Kind == AuditKind.Transformer)
            {
                var field = TransformerLedgerField;
                if (field == null || !(e.Device is Transformer t)) return false;
                try
                {
                    if (field.GetValue(t) is float f) { value = f; return true; }
                }
                catch { }
                return false;
            }
            return e.Device is WirelessPower half
                   && PowerTransmitterPlusInterop.TryGetWirelessDebt(half, out value);
        }

        // ------------------------------------------------------------------
        // Anomaly capture + throttled aggregated warning.
        // ------------------------------------------------------------------

        // Rank key for the worst-offender capture: non-finite deviations rank above everything.
        private static float Magnitude(float diff)
        {
            if (float.IsNaN(diff) || float.IsInfinity(diff)) return float.PositiveInfinity;
            return diff < 0f ? -diff : diff;
        }

        private static void NoteWorst(AnomalyClass cls, AuditKind kind, long refId, float magnitude,
            float a, float b, double shadow, Site siteFrom, Site siteTo)
        {
            if (_worstClass == AnomalyClass.None || magnitude >= _worstMagnitude)
            {
                _worstClass = cls;
                _worstMagnitude = magnitude;
                _worstKind = kind;
                _worstRefId = refId;
                _worstA = a;
                _worstB = b;
                _worstShadow = shadow;
                _worstSiteFrom = siteFrom;
                _worstSiteTo = siteTo;
            }
        }

        // Called once per tick from the boundary check: emit the aggregated warning when new
        // anomalies were recorded since the last line and the throttle window has passed. Healthy
        // grids pay one long-compare per tick and log nothing, ever.
        private static void EmitWarningIfDue(int currentTick)
        {
            long total = _boundaryAnomalies + _unobservedAnomalies + _bracketAnomalies + _nonFiniteAnomalies;
            if (total == _totalAtLastWarn) return;
            if (currentTick - _lastGlobalWarnTick < WarnCooldownTicks) return;
            _lastGlobalWarnTick = currentTick;
            _totalAtLastWarn = total;
            Plugin.Log?.LogWarning(
                "[PowerGridPlus] Ledger audit: " + total.ToString(CultureInfo.InvariantCulture)
                + " anomaly(ies) since load (boundary " + _boundaryAnomalies.ToString(CultureInfo.InvariantCulture)
                + ", unobserved-path " + _unobservedAnomalies.ToString(CultureInfo.InvariantCulture)
                + ", bracket " + _bracketAnomalies.ToString(CultureInfo.InvariantCulture)
                + ", non-finite " + _nonFiniteAnomalies.ToString(CultureInfo.InvariantCulture)
                + "; worst: " + FormatWorst()
                + "). An anomaly means something outside PowerGridPlus wrote a ledger it owns."
                + " Negative settles since load: " + _negativeSettles.ToString(CultureInfo.InvariantCulture) + ".");
            ResetWorst();
        }

        private static string FormatWorst()
        {
            if (_worstClass == AnomalyClass.None) return "none";
            string where = KindName(_worstKind) + " " + _worstRefId.ToString(CultureInfo.InvariantCulture);
            switch (_worstClass)
            {
                case AnomalyClass.Boundary:
                    return "tick-boundary write on " + where
                           + " (settled " + _worstA.ToString("F2", CultureInfo.InvariantCulture)
                           + " W, found " + _worstB.ToString("F2", CultureInfo.InvariantCulture) + " W)";
                case AnomalyClass.UnobservedPath:
                    return "unobserved write on " + where
                           + " (expected " + (_worstA + _worstShadow).ToString("F2", CultureInfo.InvariantCulture)
                           + " W = boundary " + _worstA.ToString("F2", CultureInfo.InvariantCulture)
                           + " + shadow " + _worstShadow.ToString("F2", CultureInfo.InvariantCulture)
                           + ", found " + _worstB.ToString("F2", CultureInfo.InvariantCulture)
                           + " W; last observed op " + SiteName(_worstSiteFrom) + ")";
                case AnomalyClass.BracketDiscontinuity:
                    return "foreign write between " + SiteName(_worstSiteFrom) + " and " + SiteName(_worstSiteTo)
                           + " on " + where
                           + " (" + _worstA.ToString("F2", CultureInfo.InvariantCulture)
                           + " W -> " + _worstB.ToString("F2", CultureInfo.InvariantCulture) + " W)";
                default:
                    return "non-finite pre-settle value (" + _worstB.ToString(CultureInfo.InvariantCulture)
                           + ") on " + where;
            }
        }

        private static void ResetWorst()
        {
            _worstClass = AnomalyClass.None;
            _worstMagnitude = 0f;
            _worstKind = AuditKind.Transformer;
            _worstRefId = 0L;
            _worstA = 0f;
            _worstB = 0f;
            _worstShadow = 0.0;
            _worstSiteFrom = Site.Boundary;
            _worstSiteTo = Site.Boundary;
        }

        private static string KindName(AuditKind kind)
        {
            switch (kind)
            {
                case AuditKind.Transformer: return "Transformer";
                case AuditKind.PowerTransmitter: return "PowerTransmitter";
                default: return "PowerReceiver";
            }
        }

        private static string SiteName(Site site)
        {
            switch (site)
            {
                case Site.Boundary: return "tick-boundary";
                case Site.TransformerUsePower: return "Transformer.UsePower";
                case Site.TransformerReceivePower: return "Transformer.ReceivePower";
                case Site.TransmitterUsePower: return "PowerTransmitter.UsePower";
                case Site.TransmitterReceivePower: return "PowerTransmitter.ReceivePower";
                case Site.ReceiverUsePower: return "PowerReceiver.UsePower";
                case Site.ReceiverReceivePower: return "PowerReceiver.ReceivePower";
                default: return "settle";
            }
        }
    }
}
