using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Objects.Electrical;
using UnityEngine;

namespace ScenarioRunner
{
    // Scenario: ptp-standalone-suite
    //
    // Grep-able PASS/FAIL regression for the PowerTransmitterPlus v1.9.0 STANDALONE fixes:
    // the mod billing its own wireless transfer debt on the vanilla allocator (no
    // PowerGridPlus). Designed for the Luna_rearch save (7 linked wireless TX-RX pairs
    // feeding the base) with PowerGridPlus stashed, headless, sun frozen at noon.
    //
    // The suite carries its own SOLAR BOOTSTRAP: the save's source grids are solar-only
    // farms parked off-sun (heads at Vertical=0 = pitch -75 deg), which under the frozen
    // zenith generate exactly 0 W, and their tracker ICs hang off the same dead nets, so
    // the islands can never power up on their own. Phase A force-aims every panel at the
    // zenith and a bootstrap window waits for the source nets to come alive before the
    // measurement clock starts. The first run of this suite passed vacuously (every link
    // at 0 W) for exactly this reason.
    //
    // What v1.9.0 adds (Mods/PowerTransmitterPlus/DistanceCostPatches.cs):
    //   - Transfer debt: PowerTransmitter._powerProvided accumulates delivered * multiplier
    //     at UsePower time (UsePowerInflateDebtPatch) and is paid down by the source-side
    //     billing (GetUsedPowerLiftCapPatch surfaces the full debt; vanilla ReceivePower
    //     subtracts what the source paid).
    //   - Standalone debt ceiling: the advertise prefix (GeneratedPowerNoDistanceDeratePatch)
    //     pauses delivery (advertises 0) while debt >= ceiling, with
    //       ceiling = (EffectiveMaxCapacity > 0 ? EffectiveMaxCapacity
    //                                           : PowerTransmitter.MaxPowerTransmission)
    //                 * max(multiplier, 1) * 4
    //     and resumes once the source pays it down.
    //   - Receiver drain-cap lift (ReceiverDrainCapLiftPatch): deliveries above vanilla's
    //     5 kW receiver cap (PowerTransmitter.MaxPowerTransmission) become billable instead
    //     of stranding as receiver debt (free energy).
    //
    // Phases (t = ticks since the scenario started, after the configured Delay Ticks;
    // m = ticks since bootstrap liveness, the measurement clock; Scenario_SunNoon() is
    // composed at the top of every pump, like RearchSuite):
    //   A (t == 0):    resolve PowerTransmitterPlus.ModApi via reflection ONCE (Version,
    //                  EffectiveMaxCapacity, TryGetLink, SourceDrawMultiplier,
    //                  GetTransferDebt, BillingOwner). ModApi missing (old mod build) ->
    //                  warning + "VERDICT api=ABSENT" with everything else SKIP. Then
    //                  enumerate every linked TX-RX pair (TryGetLink per PowerTransmitter):
    //                  log refIds, display name, multiplier, distance, computed debt
    //                  ceiling per pair, and pick the largest-multiplier pair as the
    //                  TOGGLE TARGET. Zero linked pairs -> verdict SKIP. Finally the
    //                  one-shot SOLAR BOOTSTRAP: force-aim every SolarPanel at the
    //                  frozen zenith (RotatableBehaviour.TargetVertical = 0.5, the ratio
    //                  that maps to physical pitch 0 = straight up) and log how many
    //                  panels were re-aimed. See PtsSolarBootstrap for the write path.
    //   BOOT (t 1..60): bootstrap window. Per tick, liveness = ANY enumerated
    //                  transmitter's InputNetwork.PotentialLoad > 0. Progress line every
    //                  10 ticks: bootstrap t=<n> livePairs=<k> maxSrcPotential=<W>
    //                  maxPanelEff=<e> (efficiency sampled across a handful of re-aimed
    //                  panels, so a failed aim is diagnosable from the log alone). On
    //                  liveness at t=L the measurement clock starts (m = t - L) and all
    //                  later phases run on m with the original window lengths (240
    //                  sample ticks, toggle at m=80/m=120, verdict at m>=250). If
    //                  liveness never arrives by t=60: emit "[PtpSuite] SOURCE DEAD: no
    //                  transmitter input net came alive within 60 ticks; environment
    //                  invalid" plus the verdict "api=OK pairs=<n> debt=SKIP
    //                  flow5k=NOTSEEN toggle=SKIP" (grammar unchanged; SKIP means the
    //                  environment could not carry the measurement, not a mod failure).
    //   B (m 1..240):  per tick, per pair, sample the TRANSMITTER half's transfer debt
    //                  (the ceiling-governed accumulator; the receiver half carries only
    //                  relay debt) plus the wireless network's CurrentLoad (= delivered
    //                  watts this tick; the same field LogicType.PowerActual reads) and
    //                  the receiver half's debt as secondary evidence. Track max debt per
    //                  pair and whether any pair's flow exceeded 5000 W on any tick
    //                  (flow5k evidence for the receiver drain-cap lift). One compact log
    //                  line per pair every 10 ticks.
    //                  Toggle sub-test on the TOGGLE TARGET: OnOff=false at m=80,
    //                  OnOff=true at m=120 (via TrySetOnOff, the same reflection-driven
    //                  property/SetOnOff write Dispatcher.PtCampaign.cs uses from this
    //                  pump). Asserts:
    //                    (a) while off, debt does not grow (frozen ledger: UsePower and
    //                        GetUsedPower both gate on OnOff); the outcome is always
    //                        logged as an explicit toggle(a) PASS/FAIL detail line;
    //                    (b) within 40 ticks after re-enable, flow resumes (nonzero
    //                        delivered), debt honors the same per-pair debt bound as the
    //                        global assertion, and by window end the ledger has settled
    //                        (debt <= max(lastFlow * multiplier * 1.5, 1000 W), the
    //                        steady one-tick billing lag with headroom) OR is still
    //                        strictly decreasing (a large re-enable lump legitimately
    //                        drains across several ticks; decreasing = converging);
    //                    (c) across the toggle window, no single-tick debt increase
    //                        exceeds maxPairFlow * multiplier * 1.05 + 1 W (the
    //                        no-free-lump / no-double-bill check: one tick may book at
    //                        most one tick's delivered * multiplier).
    //   C (m >= 250):  exactly one grep-able verdict line:
    //                    [PtpSuite] VERDICT api=OK|ABSENT pairs=<n> debt=PASS|FAIL|SKIP
    //                      flow5k=SEEN|NOTSEEN toggle=PASS|FAIL|SKIP
    //                  debt PASS = every pair's max observed debt <=
    //                  ceiling + maxPairFlow * multiplier * 1.02 + 1 W. One summary line
    //                  per pair (maxDebt vs computed bound) is logged either way; one
    //                  detail line per failed assertion precedes the verdict. debt/toggle
    //                  read SKIP when api=ABSENT, when no linked pairs exist, or when the
    //                  bootstrap window ended SOURCE DEAD; flow5k has no SKIP token,
    //                  NOTSEEN is emitted in those cases.
    //
    // Debt bound rationale (calibrated against a live dedi run): the standalone ceiling
    // is a PAUSE gate, not a clamp, and it is evaluated pre-delivery. Each tick the
    // advertise prefix compares the CURRENT debt against the ceiling and only then does
    // the wireless network deliver, so a tick that starts just below the ceiling can
    // still book one full bill (delivered * multiplier) on top of it before the pause
    // lands on the NEXT advertise. The true invariant is therefore
    //   debt(t) <= ceiling + delivered(t) * multiplier
    // with exactly one burst bill of overshoot by design. On unlimited-cap servers
    // (EffectiveMaxCapacity = 0, the default) delivered(t) is bounded by the source
    // net's PotentialLoad, NOT by any constant (a live run showed single-tick bursts of
    // 113 kW), so every bound here is anchored to the pair's max OBSERVED single-tick
    // flow rather than the ceiling's 5 kW fallback constant; the fallback constant only
    // scales the pause threshold itself. In steady state the ledger runs at exactly one
    // tick's billing lag (constant debt == flow * multiplier, observed 4754 W * 2.545 =
    // 12101 on the calibration run, debts paying down to 0 whenever flow stops), which
    // is what the toggle(b) settle check keys on.
    //
    // PowerGridPlus note: safe to leave configured with PowerGridPlus ALSO loaded. With
    // PGP holding billing ownership (ModApi.ClaimBillingOwnership) the debt-billing
    // patches and the standalone ceiling stand down, so transfer debt stays near zero and
    // debt/toggle trivially pass; the suite is only MEANINGFUL standalone. PGP presence
    // is logged as an info line, never a failure. With PowerTransmitterPlus absent the
    // scenario warns and no-ops (standard RequireModAssembly gate).
    //
    // Threading: every sampled value is managed state (reflection FieldInfo reads of
    // _powerProvided via ModApi, CableNetwork.CurrentLoad floats, LinkedReceiver
    // references), enumerated via OcclusionManager.AllThings.ForEach; no Unity API on the
    // UniTask sim-tick worker (Research/Patterns/ThingEnumerationOffMainThread.md).
    // DisplayName can touch Unity APIs, so it is wrapped per-item and degrades to
    // PrefabName (Dispatcher.DevicePortDump.cs precedent). The OnOff writes go through
    // TrySetOnOff, the mechanism Dispatcher.PtCampaign.cs already drives from this same
    // pump. The solar re-aim writes RotatableBehaviour.TargetVertical, a managed double
    // whose setter defers all Unity transform work to the main thread (DoMoveTask begins
    // with UniTask.SwitchToMainThread when kicked from a worker), so it sits in the same
    // thread-safety class as TrySetOnOff; the bootstrap reads (InputNetwork.PotentialLoad,
    // SolarPanel.GenerationEfficiency, SolarPanel.Vertical) are plain managed fields.
    // Null checks use ReferenceEquals to avoid the UnityEngine.Object == operator.
    internal static partial class Dispatcher
    {
        // ---- window layout (RearchSuite-style pacing; B/C run on the measurement clock m) ----
        private const int PTS_WINDOW_TICKS = 240;       // phase B length (measurement ticks)
        private const int PTS_VERDICT_TICK = 250;       // phase C one-shot
        private const int PTS_LOG_EVERY = 10;           // log cadence (bootstrap progress + per-pair lines)
        private const int PTS_TOGGLE_OFF_TICK = 80;     // spec: OnOff=false at m=80
        private const int PTS_TOGGLE_ON_TICK = 120;     // spec: OnOff=true at m=120
        private const int PTS_RECOVERY_TICKS = 40;      // spec: flow must resume within 40 ticks of re-enable

        // ---- solar bootstrap ----
        // Liveness must arrive within this many ticks of t=1 or the environment is
        // declared invalid (SOURCE DEAD). The head slew from parked (-75 deg) to zenith
        // takes ~18 ticks (RotatableBehaviour moves Vertical at (180 / MaximumVertical)
        // * MovementSpeedVertical = (180/165) * 0.05 ratio per second of Time.deltaTime;
        // the sun-noon freeze zeroes only the orbital clock, not Unity time), and the
        // first watts appear once a head is within 60 degrees of the sun (~4 ticks), so
        // 60 ticks is a factor ~3 of headroom.
        private const int PTS_BOOTSTRAP_DEADLINE_TICKS = 60;
        // RotatableBehaviour.TargetVertical ratio that maps to physical pitch 0 (straight
        // up): SolarPanelArm.SetPitch applies Quaternion.Euler(Lerp(-75, +75, value), 0, 0)
        // on the pitch pivot, and the logic Vertical read is Lerp(15, 165, value) = 90 deg
        // at 0.5 (game decompile 0.2.6403.27689, SolarPanel / SolarPanelArm).
        private const double PTS_PANEL_VERTICAL_TARGET = 0.5;
        // Ratio slop for "already aimed" (mirrors SolarPanel.RotationTolerance = 0.001).
        private const double PTS_PANEL_AIM_TOLERANCE = 0.001;
        private const int PTS_EFF_SAMPLE_CAP = 8;       // panels sampled for GenerationEfficiency evidence

        // Mirrors the "* 4f" in GeneratedPowerNoDistanceDeratePatch.Prefix
        // (Mods/PowerTransmitterPlus/DistanceCostPatches.cs: ceiling = effectiveCap * multiplier * 4f).
        private const float PTS_CEILING_FACTOR = 4f;
        // Headroom on the one legal burst bill in the debt bound
        //   maxDebt <= ceiling + maxPairFlow * multiplier * 1.02 + 1 W.
        // The advertise gate runs pre-delivery, so exactly one tick's bill may land on
        // top of the ceiling (header rationale); 2% covers CurrentLoad-vs-UsePower float
        // rounding. Calibrated on a live dedi run where pairs sat within ~1% of the
        // exact invariant (e.g. maxDebt 268207 vs 50518 + 86420 * 2.526 = 268815).
        private const float PTS_BURST_HEADROOM = 1.02f;
        // Headroom for the single-tick jump check (c): one tick may book at most
        // delivered(t) * multiplier, bounded by the pair's max observed single-tick
        // flow; 5% covers rounding. Tighter than the burst headroom is not needed here
        // because the jump is a delta of two consecutive debt samples.
        private const float PTS_JUMP_HEADROOM = 1.05f;
        // Absolute watt slack added to both bounds so near-zero-flow pairs do not trip
        // on float dust.
        private const float PTS_BOUND_EPSILON = 1.0f;
        // toggle(b) settle target: steady state runs at exactly one tick's billing lag
        // (constant debt == flow * multiplier; observed 4754 * 2.545 = 12101 on the
        // calibration run), so 1.5x that is "settled"; the 1000 W floor covers
        // near-zero flow where a proportional target would be within float noise.
        private const float PTS_SETTLE_FACTOR = 1.5f;
        private const float PTS_SETTLE_FLOOR = 1000f;
        // Float-noise epsilon for "debt did not grow while off" (the ledger is exactly
        // frozen: UsePower and GetUsedPower both early-return when !OnOff).
        private const float PTS_FREEZE_EPSILON = 0.5f;
        // "Nonzero delivered" threshold in watts for flow-resume evidence.
        private const float PTS_FLOW_EPSILON = 0.5f;

        // ---- ModApi reflection cache (resolved once in phase A) ----
        private static bool _ptsApiOk;
        private static int _ptsApiVersion;
        private static MethodInfo _ptsApiEffectiveMaxCapacity;   // static float EffectiveMaxCapacity()
        private static MethodInfo _ptsApiTryGetLink;             // static bool TryGetLink(PowerTransmitter, out float)
        private static MethodInfo _ptsApiSourceDrawMultiplier;   // static float SourceDrawMultiplier(PowerTransmitter)
        private static MethodInfo _ptsApiGetTransferDebt;        // static float GetTransferDebt(WirelessPower)
        private static PropertyInfo _ptsApiBillingOwner;         // static string BillingOwner { get; }

        // ---- per-run state ----
        private static bool _ptsStarted;
        private static bool _ptsDone;
        private static long _ptsStartTick;
        private static float _ptsEffectiveCap;        // ModApi.EffectiveMaxCapacity() sampled once (0 = unlimited)
        // Ceiling base ONLY (cap > 0 ? cap : MaxPowerTransmission): the constant that
        // scales the pause threshold. It does NOT bound delivery when the cap is 0
        // (unlimited); bursts are bounded by source PotentialLoad, so the delivery-side
        // bounds use per-pair max observed flow instead.
        private static float _ptsCeilingBase;
        private static readonly List<PtsPairState> _ptsPairs = new List<PtsPairState>();
        private static PtsPairState _ptsTarget;

        // solar bootstrap bookkeeping
        private static long _ptsMeasureBase = -1;     // t of the liveness tick; m = t - base (-1 = bootstrap window still open)
        private static readonly List<SolarPanel> _ptsEffSample = new List<SolarPanel>();   // GenerationEfficiency evidence panels

        // toggle bookkeeping (all on the TOGGLE TARGET)
        private static bool _ptsToggleOffOk;
        private static bool _ptsToggleOnOk;
        private static bool _ptsPreFlowSeen;          // any delivered > eps before the off toggle
        private static bool _ptsRecoveryFlowSeen;     // any delivered > eps within the recovery window
        private static float _ptsFreezeBaseline = float.NaN;   // debt at m = 81 (first fully-off tick)
        private static int _ptsFreezeTicksChecked;    // off-window ticks the freeze assert actually evaluated
        private static int _ptsFreezeViolations;
        private static float _ptsFreezeWorst;
        private static float _ptsRecoveryMaxDebt;
        // toggle(b) settle sample at the recovery-window end tick: debt(end),
        // debt(end - 1) (LastTxDebt at capture time), and the delivered flow at end.
        private static bool _ptsSettleCaptured;
        private static float _ptsSettleDebtEnd;
        private static float _ptsSettleDebtPrev;
        private static float _ptsSettleFlowEnd;
        private static int _ptsJumpViolations;
        private static float _ptsJumpWorst;
        private static long _ptsJumpWorstTick;

        private sealed class PtsPairState
        {
            public PowerTransmitter Tx;
            public PowerReceiver Rx;
            public string Name;
            public float Distance;
            public float Multiplier;      // sampled once in phase A (distance and k are static for the run)
            public float Ceiling;         // effectiveCapFallback * max(multiplier, 1) * PTS_CEILING_FACTOR
            public float MaxTxDebt;
            public float MaxFlow;
            public float LastTxDebt;
            public bool Flow5kSeen;
        }

        private static void Scenario_PtpStandaloneSuite()
        {
            if (_ptsDone) return;

            // Deterministic full solar while the suite runs: ride the existing sun-noon
            // freeze (one-shot zenith scan + per-tick TimeScale re-arm in
            // Dispatcher.SunNoon.cs). Runs before the PTP gate so the sun is frozen
            // either way, same composition as Scenario_PgpRearchSuite.
            Scenario_SunNoon();

            if (!RequireModAssembly(PTP_ASSEMBLY, "ptp-standalone-suite")) return;

            try
            {
                if (!_ptsStarted)
                {
                    _ptsStarted = true;
                    _ptsStartTick = _ticksSeen;
                    PtsPhaseA();
                    return;
                }

                long t = _ticksSeen - _ptsStartTick;
                if (_ptsMeasureBase < 0)
                {
                    // Bootstrap window: hold the measurement clock until a source island
                    // comes alive (or declare the environment dead at the deadline).
                    PtsBootstrapTick(t);
                    return;
                }

                long m = t - _ptsMeasureBase;   // measurement clock; m=1 is the first sampled tick
                if (m <= PTS_WINDOW_TICKS)
                {
                    PtsPhaseB(m);
                }
                else if (m >= PTS_VERDICT_TICK)
                {
                    _ptsDone = true;
                    PtsPhaseC();
                }
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] [PtpSuite] threw: {e}");
            }
        }

        // ---- Phase A: ModApi resolve + pair inventory + toggle-target pick ----

        private static void PtsPhaseA()
        {
            if (!PtsResolveApi())
            {
                // PowerTransmitterPlus is loaded (RequireModAssembly passed) but the ModApi
                // surface is missing: an old build predating v1.9.0. Everything else SKIP.
                _log?.LogWarning(
                    "[ScenarioRunner] [PtpSuite] PowerTransmitterPlus is loaded but " +
                    "PowerTransmitterPlus.ModApi is absent or incomplete (pre-v1.9.0 build); " +
                    "nothing to measure.");
                _ptsDone = true;
                PtsEmitVerdict("ABSENT", 0, "SKIP", false, "SKIP");
                return;
            }

            string owner = null;
            try { owner = _ptsApiBillingOwner.GetValue(null) as string; } catch { }
            bool pgpLoaded = GetModAssembly(PGP_ASSEMBLY) != null;
            _ptsEffectiveCap = PtsEffectiveMaxCapacity();
            // The ceiling's fallback base: an unlimited cap (0) falls back to vanilla
            // PowerTransmitter.MaxPowerTransmission (5000 W), mirroring
            // GeneratedPowerNoDistanceDeratePatch.Prefix's effectiveCap. This constant
            // scales the pause threshold only; it does not bound per-tick delivery.
            _ptsCeilingBase = _ptsEffectiveCap > 0f ? _ptsEffectiveCap : PowerTransmitter.MaxPowerTransmission;

            _log?.LogInfo(
                $"[ScenarioRunner] [PtpSuite] ModApi resolved: version={_ptsApiVersion} " +
                $"billingOwner={(owner ?? "null")} effectiveCap={_ptsEffectiveCap:F0} (0=unlimited) " +
                $"ceilingBase={_ptsCeilingBase:F0}");

            if (pgpLoaded)
            {
                // Info only, never a failure: with PowerGridPlus claiming billing ownership
                // the debt-billing patches stand down and transfer debt stays near zero, so
                // the suite passes trivially. Standalone (PGP stashed) is the meaningful run.
                _log?.LogInfo(
                    "[ScenarioRunner] [PtpSuite] note: PowerGridPlus is loaded; if it holds " +
                    "billing ownership the transfer debt stays near zero and this suite is " +
                    "only meaningful standalone. Continuing anyway.");
            }

            // Enumerate linked pairs: every PowerTransmitter with a live link per
            // ModApi.TryGetLink (false when unlinked; never surfaces a stale distance).
            var txs = new List<PowerTransmitter>();
            OcclusionManager.AllThings.ForEach(thing =>
            {
                if (thing is PowerTransmitter tx) txs.Add(tx);
            });
            txs.Sort((a, b) => a.ReferenceId.CompareTo(b.ReferenceId));   // deterministic log order

            foreach (var tx in txs)
            {
                if (!PtsTryGetLink(tx, out float distance)) continue;
                var rx = tx.LinkedReceiver;
                if (ReferenceEquals(rx, null)) continue;   // TryGetLink true implies a receiver; belt and braces

                float multiplier = PtsSourceDrawMultiplier(tx);
                // ceiling = effectiveCapFallback * max(multiplier, 1) * 4, verbatim from
                // GeneratedPowerNoDistanceDeratePatch.Prefix (DistanceCostPatches.cs).
                float ceiling = _ptsCeilingBase * Mathf.Max(multiplier, 1f) * PTS_CEILING_FACTOR;

                string name = tx.PrefabName ?? "";
                try
                {
                    // DisplayName may touch Unity APIs; degrade to PrefabName off-thread
                    // (Dispatcher.DevicePortDump.cs precedent).
                    var d = tx.DisplayName;
                    if (!string.IsNullOrEmpty(d)) name = d;
                }
                catch { }

                float debt0 = PtsGetTransferDebt(tx);
                var pair = new PtsPairState
                {
                    Tx = tx,
                    Rx = rx,
                    Name = name,
                    Distance = distance,
                    Multiplier = multiplier,
                    Ceiling = ceiling,
                    MaxTxDebt = debt0,
                    LastTxDebt = debt0,
                };
                _ptsPairs.Add(pair);

                _log?.LogInfo(
                    $"[ScenarioRunner] [PtpSuite] pair tx={tx.ReferenceId} rx={rx.ReferenceId} " +
                    $"name={name} dist={distance:F1}m mult={multiplier:F3} ceiling={ceiling:F0}W " +
                    $"debt0={debt0:F0}");
            }

            if (_ptsPairs.Count == 0)
            {
                _log?.LogWarning(
                    "[ScenarioRunner] [PtpSuite] no linked TX-RX pairs in this world; " +
                    "nothing to measure (expected 7 on Luna_rearch).");
                _ptsDone = true;
                PtsEmitVerdict("OK", 0, "SKIP", false, "SKIP");
                return;
            }

            // TOGGLE TARGET: the pair with the largest multiplier (ties: lowest ReferenceId,
            // already guaranteed by the sorted enumeration order above).
            _ptsTarget = _ptsPairs[0];
            foreach (var p in _ptsPairs)
                if (p.Multiplier > _ptsTarget.Multiplier) _ptsTarget = p;

            _log?.LogInfo(
                $"[ScenarioRunner] [PtpSuite] TOGGLE TARGET tx={_ptsTarget.Tx.ReferenceId} " +
                $"(largest multiplier {_ptsTarget.Multiplier:F3}); OnOff->false at m={PTS_TOGGLE_OFF_TICK}, " +
                $"OnOff->true at m={PTS_TOGGLE_ON_TICK}, recovery window {PTS_RECOVERY_TICKS} ticks " +
                "(m = ticks since bootstrap liveness).");

            PtsSolarBootstrap();
        }

        // ---- Phase A tail: one-shot solar bootstrap ----
        //
        // Why: on Luna_rearch every wireless source grid is a solar-only farm whose
        // panels sit parked at Vertical=0, i.e. physical pitch Lerp(-75, +75, 0) = -75
        // deg (SolarPanelArm.SetPitch), while sun-noon freezes the sun at the zenith
        // (elevation ~89.7 deg). SolarPanelArm.CalculateSolarEfficiency computes
        // 1 - |FacingDirection - WorldSunVector| (= 1 - 2*sin(theta/2), which hits zero
        // at 60 degrees off-axis), so a 75-deg-off head generates exactly 0 W. The
        // tracker ICs that would re-aim the farm are powered by the same dead nets, and
        // a fully dead island advertises PotentialLoad=0 forever (vanilla cross-net
        // advertise is one-tick-lagged PotentialLoad; 0 stays 0), so the island can
        // never bootstrap itself.
        //
        // Fix: write RotatableBehaviour.TargetVertical = 0.5 on every SolarPanel (base
        // class; covers StructureSolarPanelReinforced and every other variant). 0.5 maps
        // to physical pitch 0 = head flat, facing straight up at the frozen zenith (the
        // logic Vertical read would be Lerp(15, 165, 0.5) = 90 deg). Horizontal is
        // deliberately untouched: at pitch 0 the yaw pivot only spins the flat head
        // around the sun axis, so it cannot affect zenith aim, and skipping it avoids
        // slow 360-degree yaw slews. The write is a managed double; the game's own
        // RotatableBehaviour.DoMoveTask does the Unity transform work and switches
        // itself to the main thread when kicked from a worker, so this is the same
        // thread-safety class as TrySetOnOff. The head then LERPs toward the target
        // over subsequent ticks (~18 ticks for the full -75 -> 0 swing); the bootstrap
        // window waits for liveness instead of assuming instant aim. Once the island
        // powers up the tracker ICs reboot and keep writing the same zenith target (the
        // sun is frozen), so nothing fights the forced aim.
        private static void PtsSolarBootstrap()
        {
            var panels = new List<SolarPanel>();
            OcclusionManager.AllThings.ForEach(thing =>
            {
                if (thing is SolarPanel sp) panels.Add(sp);
            });
            panels.Sort((a, b) => a.ReferenceId.CompareTo(b.ReferenceId));   // deterministic sample pick

            int reAimed = 0;
            foreach (var sp in panels)
            {
                var rb = sp.RotatableBehaviour;
                if (ReferenceEquals(rb, null)) continue;

                // Off-aim when either the head or its target is away from the zenith
                // ratio. Covers the parked case (head 0, target 0) and the stalled case
                // (target already 0.5 but the head never moved: re-writing the target
                // re-kicks MoveToTarget).
                bool offAim =
                    Math.Abs(sp.Vertical - PTS_PANEL_VERTICAL_TARGET) > PTS_PANEL_AIM_TOLERANCE
                    || Math.Abs(rb.TargetVertical - PTS_PANEL_VERTICAL_TARGET) > PTS_PANEL_AIM_TOLERANCE;
                if (!offAim) continue;

                rb.TargetVertical = PTS_PANEL_VERTICAL_TARGET;
                reAimed++;
                if (_ptsEffSample.Count < PTS_EFF_SAMPLE_CAP) _ptsEffSample.Add(sp);
            }

            // If fewer than the cap were re-aimed (e.g. the save is already aimed), top
            // the sample up with the first panels so the bootstrap log still carries
            // efficiency evidence.
            for (int i = 0; _ptsEffSample.Count < PTS_EFF_SAMPLE_CAP && i < panels.Count; i++)
                if (!_ptsEffSample.Contains(panels[i])) _ptsEffSample.Add(panels[i]);

            _log?.LogInfo(
                $"[ScenarioRunner] [PtpSuite] solar bootstrap: re-aimed {reAimed} of {panels.Count} solar panels " +
                $"to Vertical={PTS_PANEL_VERTICAL_TARGET:F1} (physical pitch 0, straight up at the frozen zenith); " +
                $"liveness deadline {PTS_BOOTSTRAP_DEADLINE_TICKS} ticks, sampling {_ptsEffSample.Count} panels " +
                "for GenerationEfficiency.");
        }

        // ---- Bootstrap window: wait for a source island to come alive ----
        //
        // Liveness = any enumerated transmitter's InputNetwork.PotentialLoad > 0; that is
        // the precondition for any wireless transfer, so measuring before it is vacuous
        // (the first run of this suite passed with every link at 0 W for exactly that
        // reason). InputNetwork and PotentialLoad are plain managed fields; worker-safe.
        private static void PtsBootstrapTick(long t)
        {
            int livePairs = 0;
            float maxSrcPotential = 0f;
            foreach (var pair in _ptsPairs)
            {
                var tx = pair.Tx;
                if (ReferenceEquals(tx, null)) continue;
                var srcNet = tx.InputNetwork;
                if (ReferenceEquals(srcNet, null)) continue;
                float potential = srcNet.PotentialLoad;
                if (potential > 0f) livePairs++;
                if (potential > maxSrcPotential) maxSrcPotential = potential;
            }

            float maxPanelEff = PtsMaxPanelEff();

            if (livePairs > 0)
            {
                _ptsMeasureBase = t;
                _log?.LogInfo(
                    $"[ScenarioRunner] [PtpSuite] bootstrap LIVE at t={t}: livePairs={livePairs} " +
                    $"maxSrcPotential={maxSrcPotential:F0}W maxPanelEff={maxPanelEff:F2}; measurement window " +
                    $"starts (m=1 next tick; toggle at m={PTS_TOGGLE_OFF_TICK}/{PTS_TOGGLE_ON_TICK}, " +
                    $"verdict at m>={PTS_VERDICT_TICK}).");
                return;
            }

            if (t % PTS_LOG_EVERY == 0)
            {
                _log?.LogInfo(
                    $"[ScenarioRunner] [PtpSuite] bootstrap t={t} livePairs={livePairs} " +
                    $"maxSrcPotential={maxSrcPotential:F0}W maxPanelEff={maxPanelEff:F2}");
            }

            if (t >= PTS_BOOTSTRAP_DEADLINE_TICKS)
            {
                _log?.LogWarning(
                    "[ScenarioRunner] [PtpSuite] SOURCE DEAD: no transmitter input net came alive within " +
                    $"{PTS_BOOTSTRAP_DEADLINE_TICKS} ticks; environment invalid");
                _ptsDone = true;
                PtsEmitVerdict("OK", _ptsPairs.Count, "SKIP", false, "SKIP");
            }
        }

        // Max GenerationEfficiency across the sampled panels (a public float field the
        // game recomputes every frame on the main thread via ElectricityManager's
        // SolarProcessing loop; a plain managed read here). Diagnoses a failed aim from
        // the log alone: a value climbing toward ~1.00 means the heads are slewing onto
        // the frozen sun; pinned at 0.00 means the aim write did not land (or the sun is
        // not actually at the zenith).
        private static float PtsMaxPanelEff()
        {
            float max = 0f;
            foreach (var sp in _ptsEffSample)
            {
                if (ReferenceEquals(sp, null)) continue;
                float eff = sp.GenerationEfficiency;
                if (eff > max) max = eff;
            }
            return max;
        }

        // ---- Phase B: per-tick sampling + toggle sub-test (measurement clock m) ----

        private static void PtsPhaseB(long m)
        {
            bool logTick = m % PTS_LOG_EVERY == 0;

            foreach (var pair in _ptsPairs)
            {
                var tx = pair.Tx;
                if (ReferenceEquals(tx, null)) continue;

                float txDebt = PtsGetTransferDebt(tx);
                float rxDebt = PtsGetTransferDebt(pair.Rx);
                // Delivered watts this tick: the wireless network's CurrentLoad
                // (CableNetwork.OnPowerTick sets CurrentLoad = PowerTick.Consumed; on the
                // TX this is OutputNetwork, the same field LogicType.PowerActual reads).
                float flow = 0f;
                var wireless = tx.OutputNetwork;
                if (!ReferenceEquals(wireless, null)) flow = wireless.CurrentLoad;

                if (txDebt > pair.MaxTxDebt) pair.MaxTxDebt = txDebt;
                if (flow > pair.MaxFlow) pair.MaxFlow = flow;
                // Strictly above vanilla's 5 kW receiver drain cap
                // (PowerTransmitter.MaxPowerTransmission): only billable because
                // ReceiverDrainCapLiftPatch lifts the receiver-side drain.
                if (flow > PowerTransmitter.MaxPowerTransmission) pair.Flow5kSeen = true;

                if (ReferenceEquals(pair, _ptsTarget))
                    PtsToggleBookkeeping(m, txDebt, flow);

                pair.LastTxDebt = txDebt;

                if (logTick)
                {
                    _log?.LogInfo(
                        $"[ScenarioRunner] [PtpSuite] m={m} tx={tx.ReferenceId} debt={txDebt:F0} " +
                        $"max={pair.MaxTxDebt:F0} flow={flow:F0}W rxDebt={rxDebt:F0}");
                }
            }

            // Toggle actions AFTER sampling, so the m=80 sample is the last pre-off value
            // and the m=120 sample is the last fully-off value.
            if (m == PTS_TOGGLE_OFF_TICK) PtsToggle(false, m);
            else if (m == PTS_TOGGLE_ON_TICK) PtsToggle(true, m);
        }

        private static void PtsToggleBookkeeping(long m, float txDebt, float flow)
        {
            if (m <= PTS_TOGGLE_OFF_TICK)
            {
                // Pre-toggle evidence: without flow before the off toggle, "flow resumes"
                // is untestable and the toggle verdict becomes SKIP.
                if (flow > PTS_FLOW_EPSILON) _ptsPreFlowSeen = true;
            }
            else if (m <= PTS_TOGGLE_ON_TICK)
            {
                // (a) frozen ledger while off. The OnOff write lands in the pump postfix
                // AFTER m=80, so m=81 is the first tick simulated fully off; use its
                // debt as the freeze baseline and assert from m=82 on. The 80->81 edge
                // is still covered by the jump check (c).
                if (float.IsNaN(_ptsFreezeBaseline))
                {
                    _ptsFreezeBaseline = txDebt;
                }
                else
                {
                    _ptsFreezeTicksChecked++;
                    if (txDebt > _ptsFreezeBaseline + PTS_FREEZE_EPSILON)
                    {
                        _ptsFreezeViolations++;
                        float over = txDebt - _ptsFreezeBaseline;
                        if (over > _ptsFreezeWorst) _ptsFreezeWorst = over;
                    }
                }
            }
            else if (m <= PTS_TOGGLE_ON_TICK + PTS_RECOVERY_TICKS)
            {
                // (b) recovery: nonzero delivered within the window; the max debt seen
                // here is judged in phase C against the SAME per-pair bound as the
                // global debt assertion (PtsDebtBound), not the bare ceiling.
                if (flow > PTS_FLOW_EPSILON) _ptsRecoveryFlowSeen = true;
                if (txDebt > _ptsRecoveryMaxDebt) _ptsRecoveryMaxDebt = txDebt;

                // Window-end settle sample: LastTxDebt still holds the previous tick's
                // debt at this point (PhaseB updates it after this call), so the pair
                // (debtEnd, debtPrev) supports the strictly-decreasing test.
                if (m == PTS_TOGGLE_ON_TICK + PTS_RECOVERY_TICKS)
                {
                    _ptsSettleCaptured = true;
                    _ptsSettleDebtEnd = txDebt;
                    _ptsSettleDebtPrev = _ptsTarget.LastTxDebt;
                    _ptsSettleFlowEnd = flow;
                }
            }

            // (c) no-free-lump / no-double-bill: a single tick may book at most one
            // tick's legitimate bill, delivered(t) * multiplier. delivered(t) is bounded
            // by the pair's max observed single-tick flow (pair.MaxFlow, updated in
            // PhaseB BEFORE this call, so a burst tick is judged against a max that
            // includes itself). NOT bounded by the ceiling's 5 kW fallback constant:
            // with an unlimited cap, delivery is bounded by source PotentialLoad only
            // (113 kW bursts observed live). Negative jumps (pay-down, including the
            // one-tick lump settle after re-enable) are always fine.
            if (m > PTS_TOGGLE_OFF_TICK && m <= PTS_TOGGLE_ON_TICK + PTS_RECOVERY_TICKS)
            {
                float jump = txDebt - _ptsTarget.LastTxDebt;
                float limit = _ptsTarget.MaxFlow * Mathf.Max(_ptsTarget.Multiplier, 1f) * PTS_JUMP_HEADROOM
                              + PTS_BOUND_EPSILON;
                if (jump > limit)
                {
                    _ptsJumpViolations++;
                    if (jump > _ptsJumpWorst) { _ptsJumpWorst = jump; _ptsJumpWorstTick = m; }
                }
            }
        }

        private static void PtsToggle(bool on, long m)
        {
            var tx = _ptsTarget?.Tx;
            if (ReferenceEquals(tx, null))
            {
                _log?.LogWarning($"[ScenarioRunner] [PtpSuite] TOGGLE m={m} skipped: target transmitter is gone.");
                return;
            }

            // TrySetOnOff (Dispatcher.PtCampaign.cs): reflection write of the OnOff
            // property, falling back to a SetOnOff(bool) method. Same mechanism the
            // pgp-pt-topology-all scenario drives from this pump; managed interactable
            // state, no Unity API, so it is safe on the sim-tick worker.
            bool ok = TrySetOnOff(tx, on);
            if (on) _ptsToggleOnOk = ok; else _ptsToggleOffOk = ok;

            _log?.LogInfo($"[ScenarioRunner] [PtpSuite] TOGGLE m={m} tx={tx.ReferenceId} OnOff->{on} ok={ok}");
            if (on && !ok && _ptsToggleOffOk)
            {
                _log?.LogError(
                    $"[ScenarioRunner] [PtpSuite] TOGGLE restore FAILED: transmitter {tx.ReferenceId} " +
                    $"was switched off at m={PTS_TOGGLE_OFF_TICK} and could not be switched back on; the save " +
                    "is left with this dish OFF.");
            }
        }

        // ---- Phase C: one-shot verdict ----

        // Per-pair debt bound: ceiling + maxPairFlow * multiplier * 1.02 + 1 W. The
        // first term is the pause threshold the advertise prefix enforces; the second is
        // the one legal burst bill of overshoot (the gate runs pre-delivery, header
        // rationale); the headroom and watt slack cover float rounding. MaxFlow is the
        // pair's max observed single-tick delivered watts across the whole window, an
        // upper bound on any delivered(t).
        private static float PtsDebtBound(PtsPairState pair)
        {
            return pair.Ceiling
                   + pair.MaxFlow * Mathf.Max(pair.Multiplier, 1f) * PTS_BURST_HEADROOM
                   + PTS_BOUND_EPSILON;
        }

        private static void PtsPhaseC()
        {
            // debt: per pair, maxDebt <= ceiling + maxPairFlow * multiplier * 1.02 + 1 W.
            // One summary line per pair either way, so a green run still shows the
            // margins; the computed bound is in the line on failure too.
            bool debtPass = true;
            foreach (var pair in _ptsPairs)
            {
                float bound = PtsDebtBound(pair);
                bool ok = pair.MaxTxDebt <= bound;
                if (!ok) debtPass = false;
                _log?.LogInfo(
                    $"[ScenarioRunner] [PtpSuite] detail debt {(ok ? "OK" : "FAIL")} tx={PtsRefId(pair.Tx)}: " +
                    $"maxDebt={pair.MaxTxDebt:F0} {(ok ? "<=" : ">")} bound={bound:F0} " +
                    $"(ceiling={pair.Ceiling:F0} + maxFlow={pair.MaxFlow:F0} * mult={pair.Multiplier:F3} " +
                    $"* {PTS_BURST_HEADROOM:F2} + {PTS_BOUND_EPSILON:F0})");
            }

            // flow5k: any pair above vanilla's 5 kW receiver cap on any tick.
            bool flow5k = false;
            float maxFlowSeen = 0f;
            foreach (var pair in _ptsPairs)
            {
                if (pair.Flow5kSeen) flow5k = true;
                if (pair.MaxFlow > maxFlowSeen) maxFlowSeen = pair.MaxFlow;
            }
            _log?.LogInfo(
                $"[ScenarioRunner] [PtpSuite] flow summary: maxFlowSeen={maxFlowSeen:F0}W " +
                $"threshold={PowerTransmitter.MaxPowerTransmission:F0}W (vanilla receiver drain cap)");

            // toggle verdict.
            string toggle;
            if (!_ptsToggleOffOk)
            {
                toggle = "SKIP";
                _log?.LogInfo($"[ScenarioRunner] [PtpSuite] detail toggle SKIP: OnOff->false write failed at m={PTS_TOGGLE_OFF_TICK}.");
            }
            else if (!_ptsPreFlowSeen)
            {
                toggle = "SKIP";
                _log?.LogInfo(
                    "[ScenarioRunner] [PtpSuite] detail toggle SKIP: target pair delivered no power " +
                    "before the off toggle; flow-resume is untestable on a dead link.");
            }
            else
            {
                // (a) frozen ledger while off: always logged explicitly, PASS or FAIL,
                // so the freeze evaluation is visible in the output even on a green run.
                bool freezeOk = _ptsFreezeViolations == 0;
                if (freezeOk)
                    _log?.LogInfo(
                        $"[ScenarioRunner] [PtpSuite] detail toggle(a) PASS: debt frozen while off " +
                        $"(baseline={_ptsFreezeBaseline:F0}, {_ptsFreezeTicksChecked} tick(s) checked, " +
                        $"epsilon={PTS_FREEZE_EPSILON:F1}).");
                else
                    _log?.LogInfo(
                        $"[ScenarioRunner] [PtpSuite] detail toggle(a) FAIL: debt grew while off " +
                        $"({_ptsFreezeViolations} of {_ptsFreezeTicksChecked} tick(s), worst " +
                        $"+{_ptsFreezeWorst:F0} over baseline {_ptsFreezeBaseline:F0}).");

                // (b) three parts: flow resumed; the post-re-enable max debt honors the
                // SAME per-pair bound as the global debt assertion; and by window end
                // the ledger has settled to at most max(lastFlow * multiplier * 1.5,
                // 1000 W) (steady state is exactly one tick's billing lag, so 1.5x is
                // settled-with-headroom; the floor covers near-zero flow) OR the debt is
                // still strictly decreasing (a large re-enable lump may legitimately
                // still be draining; decreasing means converging, not runaway).
                float recoveryBound = PtsDebtBound(_ptsTarget);
                bool recoveryDebtOk = _ptsRecoveryMaxDebt <= recoveryBound;
                float settleTarget = Mathf.Max(
                    _ptsSettleFlowEnd * Mathf.Max(_ptsTarget.Multiplier, 1f) * PTS_SETTLE_FACTOR,
                    PTS_SETTLE_FLOOR);
                bool settleOk = _ptsSettleCaptured
                    && (_ptsSettleDebtEnd <= settleTarget
                        || _ptsSettleDebtEnd < _ptsSettleDebtPrev - PTS_FREEZE_EPSILON);
                bool recoveryOk = _ptsRecoveryFlowSeen && recoveryDebtOk && settleOk;

                if (!_ptsRecoveryFlowSeen)
                    _log?.LogInfo(
                        $"[ScenarioRunner] [PtpSuite] detail toggle(b) FAIL: no delivered power within " +
                        $"{PTS_RECOVERY_TICKS} ticks after re-enable (restoreWriteOk={_ptsToggleOnOk}).");
                if (_ptsRecoveryFlowSeen && !recoveryDebtOk)
                    _log?.LogInfo(
                        $"[ScenarioRunner] [PtpSuite] detail toggle(b) FAIL: post-re-enable debt " +
                        $"{_ptsRecoveryMaxDebt:F0} > bound={recoveryBound:F0} (same per-pair bound as the " +
                        "global debt assertion).");
                if (_ptsRecoveryFlowSeen && recoveryDebtOk && !settleOk)
                    _log?.LogInfo(_ptsSettleCaptured
                        ? $"[ScenarioRunner] [PtpSuite] detail toggle(b) FAIL: ledger not settled at window " +
                          $"end: debt={_ptsSettleDebtEnd:F0} > settleTarget={settleTarget:F0} " +
                          $"(max(lastFlow={_ptsSettleFlowEnd:F0} * mult * {PTS_SETTLE_FACTOR:F1}, " +
                          $"{PTS_SETTLE_FLOOR:F0})) and not decreasing (prev={_ptsSettleDebtPrev:F0})."
                        : "[ScenarioRunner] [PtpSuite] detail toggle(b) FAIL: settle sample not captured " +
                          "(target vanished before the recovery window end).");

                // (c) single-tick jump bound anchored to the pair's max observed flow.
                bool jumpOk = _ptsJumpViolations == 0;
                float jumpLimitFinal = _ptsTarget.MaxFlow * Mathf.Max(_ptsTarget.Multiplier, 1f)
                                       * PTS_JUMP_HEADROOM + PTS_BOUND_EPSILON;
                if (!jumpOk)
                    _log?.LogInfo(
                        $"[ScenarioRunner] [PtpSuite] detail toggle(c) FAIL: {_ptsJumpViolations} single-tick " +
                        $"debt jump(s) above maxPairFlow*multiplier*{PTS_JUMP_HEADROOM:F2}+{PTS_BOUND_EPSILON:F0} " +
                        $"(final limit {jumpLimitFinal:F0}), worst +{_ptsJumpWorst:F0} at m={_ptsJumpWorstTick}.");

                // One-line sub-result summary so every toggle check is visible even on a
                // fully green run (the calibration run's (a) passed invisibly).
                _log?.LogInfo(
                    $"[ScenarioRunner] [PtpSuite] toggle summary: a={(freezeOk ? "PASS" : "FAIL")} " +
                    $"b={(recoveryOk ? "PASS" : "FAIL")} c={(jumpOk ? "PASS" : "FAIL")} " +
                    $"(recoveryMaxDebt={_ptsRecoveryMaxDebt:F0} bound={recoveryBound:F0} " +
                    $"settleDebt={_ptsSettleDebtEnd:F0} settleTarget={settleTarget:F0} " +
                    $"settlePrev={_ptsSettleDebtPrev:F0} jumpLimit={jumpLimitFinal:F0})");

                toggle = freezeOk && recoveryOk && jumpOk ? "PASS" : "FAIL";
            }

            PtsEmitVerdict("OK", _ptsPairs.Count, debtPass ? "PASS" : "FAIL", flow5k, toggle);
        }

        private static void PtsEmitVerdict(string api, int pairs, string debt, bool flow5k, string toggle)
        {
            _log?.LogInfo(
                $"[ScenarioRunner] [PtpSuite] VERDICT api={api} pairs={pairs} debt={debt} " +
                $"flow5k={(flow5k ? "SEEN" : "NOTSEEN")} toggle={toggle}");
        }

        // ---- ModApi reflection helpers ----

        private static bool PtsResolveApi()
        {
            _ptsApiOk = false;
            try
            {
                var asm = GetModAssembly(PTP_ASSEMBLY);
                var api = asm?.GetType("PowerTransmitterPlus.ModApi");
                if (api == null) return false;

                const BindingFlags PS = BindingFlags.Public | BindingFlags.Static;
                _ptsApiEffectiveMaxCapacity = api.GetMethod("EffectiveMaxCapacity", PS, null, Type.EmptyTypes, null);
                _ptsApiTryGetLink = api.GetMethod("TryGetLink", PS, null,
                    new[] { typeof(PowerTransmitter), typeof(float).MakeByRefType() }, null);
                _ptsApiSourceDrawMultiplier = api.GetMethod("SourceDrawMultiplier", PS, null,
                    new[] { typeof(PowerTransmitter) }, null);
                _ptsApiGetTransferDebt = api.GetMethod("GetTransferDebt", PS, null,
                    new[] { typeof(WirelessPower) }, null);
                _ptsApiBillingOwner = api.GetProperty("BillingOwner", PS);

                var versionField = api.GetField("Version", PS);
                _ptsApiVersion = versionField?.GetRawConstantValue() is int v ? v : 0;

                _ptsApiOk =
                    _ptsApiEffectiveMaxCapacity != null
                    && _ptsApiTryGetLink != null
                    && _ptsApiSourceDrawMultiplier != null
                    && _ptsApiGetTransferDebt != null
                    && _ptsApiBillingOwner != null;

                if (!_ptsApiOk)
                    _log?.LogWarning(
                        "[ScenarioRunner] [PtpSuite] ModApi member resolve incomplete: " +
                        $"effectiveMaxCapacity={_ptsApiEffectiveMaxCapacity != null} " +
                        $"tryGetLink={_ptsApiTryGetLink != null} " +
                        $"sourceDrawMultiplier={_ptsApiSourceDrawMultiplier != null} " +
                        $"getTransferDebt={_ptsApiGetTransferDebt != null} " +
                        $"billingOwner={_ptsApiBillingOwner != null}");
            }
            catch (Exception e)
            {
                _log?.LogWarning($"[ScenarioRunner] [PtpSuite] ModApi reflection threw: {e.Message}");
                _ptsApiOk = false;
            }
            return _ptsApiOk;
        }

        private static float PtsEffectiveMaxCapacity()
        {
            try { return _ptsApiEffectiveMaxCapacity.Invoke(null, null) is float f ? f : 0f; }
            catch { return 0f; }
        }

        private static bool PtsTryGetLink(PowerTransmitter tx, out float distance)
        {
            distance = 0f;
            try
            {
                var args = new object[] { tx, 0f };
                bool linked = _ptsApiTryGetLink.Invoke(null, args) is bool b && b;
                if (args[1] is float d) distance = d;
                return linked;
            }
            catch { return false; }
        }

        private static float PtsSourceDrawMultiplier(PowerTransmitter tx)
        {
            try { return _ptsApiSourceDrawMultiplier.Invoke(null, new object[] { tx }) is float f ? f : 1f; }
            catch { return 1f; }
        }

        // Reads the private _powerProvided transfer-debt accumulator of either wireless
        // half through ModApi.GetTransferDebt. The TRANSMITTER half is the ceiling-governed
        // source-billing debt (UsePowerInflateDebtPatch inflates it, the advertise prefix
        // reads it); the RECEIVER half is relay debt only, sampled as secondary evidence.
        private static float PtsGetTransferDebt(WirelessPower half)
        {
            if (ReferenceEquals(half, null)) return 0f;
            try { return _ptsApiGetTransferDebt.Invoke(null, new object[] { half }) is float f ? f : 0f; }
            catch { return 0f; }
        }

        private static string PtsRefId(PowerTransmitter tx)
        {
            return ReferenceEquals(tx, null) ? "?" : tx.ReferenceId.ToString();
        }
    }
}
