using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;

namespace ScenarioRunner
{
    // pgp-2cycle-freeze
    //
    // Verifies the Sweep allocator's RE-DECIDABLE shed/overload loop fixes the Legacy "60-second freeze"
    // (a.k.a. the shed<->overload 2-cycle). The bug: Legacy's grow-only fixed-point loop can commit a shed
    // in an early round, then in a later round an overload elsewhere frees the budget the shed was
    // protecting -- but the already-committed shed never un-commits, so the consumer is locked out for the
    // full 120-tick (60 s) brownout lockout even though its supply returned the SAME tick. Sweep clears
    // Shed/Overloaded every round and recomputes against the settled state, so the unnecessary shed is
    // never committed.
    //
    // Construction at runtime (mirrors pgp-shed-multilevel's harness; reuses Sv* helpers):
    //   Phase A (observe, TF_OBS ticks): measure every transformer's peak downstream demand.
    //   Phase B (pick): choose an input net F that is itself transformer-fed (multi-level) with >=2 ON
    //     consumer transformers. Y = the consumer with the LARGEST downstream demand (the one that will
    //     overload and free the most budget). X = a smaller sibling consumer (downstream <= 0.8 * Y's),
    //     forced to the LOWEST priority so it is the shed victim; Y gets the HIGHEST priority so it
    //     survives to overload; every other consumer of F gets a middling priority so X is the sole victim.
    //   Phase C (setup): throttle Y.Setting to ~0.85 * its downstream demand so Y OVERLOADS (downstream
    //     wants more than Y delivers) while still drawing nearly its full demand from F. Throttle F's
    //     feeder transformers so F's budget covers Y + every other consumer fully but only ~40% of X ->
    //     X is the marginal, sole, lowest-priority victim. Drain all batteries/APC cells (no elastic
    //     backfill), re-drained every trace tick.
    //   Phase D (trace, TF_TRACE ticks): per tick record IsShedding(X) and IsOverloaded(Y).
    //   Phase E (verdict): reads SweepAllocatorSync.Effective and self-labels.
    //
    // Run the SAME save twice, toggle flipped:
    //   - Legacy (Enable Sweep Allocator = false): expect "LEGACY BUG REPRODUCED" -- Y overloaded AND X
    //     frozen-shed. This is the positive control: it proves the construction actually creates the freeze.
    //   - Sweep  (Enable Sweep Allocator = true):  expect "SWEEP PASS" -- Y overloaded but X NOT shed.
    // The proof of the fix is the A/B: identical construction, Legacy freezes X, Sweep does not.
    internal static partial class Dispatcher
    {
        private const int TF_OBS = 6;
        private const int TF_TRACE = 25;
        private const float TF_DEMAND_MIN = 100f;   // X must present at least this much draw
        private const float TF_Y_MIN = 200f;        // Y's downstream must want at least this (real overload)
        private const float TF_SETTING_CAP = 4000f; // keep Y.Setting below the normal cable tier cap (5000) so Y is Setting-limited, not cable-limited

        private static int _tfLastTick = int.MinValue;
        private static readonly Dictionary<long, float> _tfPeak = new Dictionary<long, float>();
        private static long _tfFanout = -1;
        private static long _tfX = -1;
        private static long _tfY = -1;
        private static readonly List<long> _tfFeeders = new List<long>();
        private static bool _tfPickFailed;
        private static bool _tfVerdictDone;
        private static int _tfSetupTick = int.MinValue;
        private static int _tfTraceCount;
        private static int _tfXShedTicks;
        private static int _tfYOverTicks;
        private static float _tfYSetting;
        private static float _tfTarget;
        private static float _tfDemandSum;

        private static void Scenario_Pgp2CycleFreeze()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-2cycle-freeze")) return;
            if (_ticksSeen < _delayTicks) return;
            if (_ticksSeen == _tfLastTick) return;
            _tfLastTick = (int)_ticksSeen;
            var asm = GetModAssembly(PGP_ASSEMBLY);
            if (_transformers.Count == 0) RebuildCaches();
            if (_tfPickFailed || _tfVerdictDone) return;

            int since = (int)_ticksSeen - _delayTicks;
            if (since < TF_OBS) { TfObserve(); return; }
            if (_tfFanout == -1 && _tfSetupTick == int.MinValue)
            {
                TfObserve();
                TfPick(asm);
                if (_tfPickFailed) return;
                TfSetup();
                _tfSetupTick = (int)_ticksSeen;
                return;
            }
            if ((int)_ticksSeen - _tfSetupTick <= TF_TRACE) { TfTrace(asm); return; }
            _tfVerdictDone = true;
            TfVerdict(asm);
        }

        private static void TfObserve()
        {
            foreach (var t in _transformers)
            {
                if (t == null || t.OutputNetwork == null) continue;
                float d = t.OutputNetwork.RequiredLoad;
                if (!_tfPeak.TryGetValue(t.ReferenceId, out var prev) || d > prev)
                    _tfPeak[t.ReferenceId] = d;
            }
        }

        private static void TfPick(Assembly asm)
        {
            var setPrioRef = asm?.GetType("PowerGridPlus.PriorityStore")?.GetMethod("SetPriorityByReference",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(long), typeof(int) }, null);

            // Group ON consumer transformers by their input network; record which nets are themselves
            // transformer-fed (so we can throttle the feeder = multi-level).
            var consumersByIn = new Dictionary<long, List<Transformer>>();
            var fedNets = new HashSet<long>();
            foreach (var t in _transformers)
            {
                if (t == null || !t.OnOff || t.Error == 1 || t.InputNetwork == null || t.OutputNetwork == null) continue;
                if (t.InputNetwork.ReferenceId == t.OutputNetwork.ReferenceId) continue;
                long inId = t.InputNetwork.ReferenceId;
                if (!consumersByIn.TryGetValue(inId, out var lst)) consumersByIn[inId] = lst = new List<Transformer>();
                lst.Add(t);
                fedNets.Add(t.OutputNetwork.ReferenceId);
            }

            float Demand(Transformer t) => _tfPeak.TryGetValue(t.ReferenceId, out var d) ? d : 0f;

            long bestF = -1; Transformer bestY = null; Transformer bestX = null; List<Transformer> bestCons = null;
            foreach (var kv in consumersByIn)
            {
                if (kv.Value.Count < 2) continue;
                if (!fedNets.Contains(kv.Key)) continue;          // multi-level only (need a feeder to throttle)
                // BOTH X and Y must be NON-step-up: step-up transformers are never shed-eligible (POWER.md
                // §5.2), so if X were step-up it could never be the shed victim and the freeze contest is
                // degenerate (the only eligible victim is then Y regardless of priority). Require shed-
                // eligible consumers so the low-priority X is genuinely the contested victim.
                var sorted = kv.Value.Where(c => !TfStepUp(asm, c.InputNetwork, c.OutputNetwork))
                    .OrderByDescending(Demand).ToList();
                if (sorted.Count < 2) continue;                   // need >=2 shed-eligible consumers
                var y = sorted[0];
                if (Demand(y) < TF_Y_MIN) continue;               // Y must overload meaningfully
                // X = largest sibling whose demand is <= 0.8*Y (so Y's freed budget can cover it) and a real draw.
                Transformer x = null;
                for (int i = 1; i < sorted.Count; i++)
                {
                    float dx = Demand(sorted[i]);
                    if (dx >= TF_DEMAND_MIN && dx <= 0.8f * Demand(y)) { x = sorted[i]; break; }
                }
                if (x == null) continue;
                var fNet = SvNet(kv.Key);
                float dsum = kv.Value.Sum(Demand);
                if (fNet == null || fNet.PotentialLoad < dsum * 0.8f) continue;   // F must normally supply them
                if (bestF == -1 || Demand(y) > Demand(bestY)) { bestF = kv.Key; bestY = y; bestX = x; bestCons = kv.Value; }
            }

            if (bestF == -1)
            {
                _log?.LogError("[ScenarioRunner] 2CYC PICK FAIL: no transformer-fed fanout with a large overload-capable consumer Y plus a smaller sibling X in this save's current activity.");
                _tfPickFailed = true;
                return;
            }

            _tfFanout = bestF; _tfX = bestX.ReferenceId; _tfY = bestY.ReferenceId;
            _tfDemandSum = bestCons.Sum(Demand);
            _tfXShedTicks = 0; _tfYOverTicks = 0; _tfTraceCount = 0;

            // Priorities: X lowest (sheds first), Y highest (survives to overload), others middling.
            foreach (var t in bestCons)
            {
                int p = t.ReferenceId == _tfX ? 10 : (t.ReferenceId == _tfY ? 1000 : 500);
                setPrioRef?.Invoke(null, new object[] { t.ReferenceId, p });
            }
            int xPrioStore = TfGetPriority(asm, _tfX);   // read back to confirm the priority write actually took effect
            int yPrioStore = TfGetPriority(asm, _tfY);

            _tfFeeders.Clear();
            foreach (var t in _transformers)
                if (t != null && t.OnOff && t.Error != 1 && t.OutputNetwork != null && t.OutputNetwork.ReferenceId == bestF)
                    _tfFeeders.Add(t.ReferenceId);

            var fNetLog = SvNet(bestF);
            _log?.LogInfo($"[ScenarioRunner] 2CYC PICK tick={_ticksSeen} fanoutF={bestF} F.Pot={(fNetLog != null ? fNetLog.PotentialLoad : 0f):0} " +
                $"X={_tfX}(demand={Demand(bestX):0},storePrio={xPrioStore}) Y={_tfY}(demand={Demand(bestY):0},storePrio={yPrioStore}) setPrioFound={setPrioRef != null} " +
                $"others={string.Join(",", bestCons.Where(t => t.ReferenceId != _tfX && t.ReferenceId != _tfY).Select(t => $"{t.ReferenceId}:{Demand(t):0}"))} " +
                $"demandSum={_tfDemandSum:0} feeders={string.Join(",", _tfFeeders)}");
            _log?.LogInfo($"[ScenarioRunner] 2CYC PICK-CLASS X={_tfX}({bestX.PrefabName} stepUp={TfStepUp(asm, bestX.InputNetwork, bestX.OutputNetwork)}) " +
                $"Y={_tfY}({bestY.PrefabName} stepUp={TfStepUp(asm, bestY.InputNetwork, bestY.OutputNetwork)})");
        }

        private static bool TfStepUp(Assembly asm, CableNetwork inNet, CableNetwork outNet)
        {
            try
            {
                var t = asm?.GetType("PowerGridPlus.PowerAllocator");
                var m = t?.GetMethod("IsStepUp",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(CableNetwork), typeof(CableNetwork) }, null);
                return m?.Invoke(null, new object[] { inNet, outNet }) is bool b && b;
            }
            catch { return false; }
        }

        private static void TfSetup()
        {
            var yT = SvTransformer(_tfY);
            var xT = SvTransformer(_tfX);
            if (yT == null || xT == null) { _tfPickFailed = true; _log?.LogError("[ScenarioRunner] 2CYC SETUP: X/Y vanished."); return; }

            float yDemand = _tfPeak.TryGetValue(_tfY, out var dy) ? dy : 0f;
            float xDemand = _tfPeak.TryGetValue(_tfX, out var dx) ? dx : 0f;

            // Throttle Y so its downstream out-demands it (overload) while it still draws nearly its full
            // demand from F (freed budget on Y's trip ~= this draw).
            float ySetting = Math.Min(yDemand * 0.85f, TF_SETTING_CAP);
            ySetting = Math.Max(ySetting, yT.UsedPower + 30f);
            yT.Setting = ySetting;
            _tfYSetting = ySetting;
            float yDraw = ySetting + yT.UsedPower;
            float xDraw = xDemand + xT.UsedPower;

            // Sum of every OTHER consumer's draw (full demand; not throttled).
            float sumOthers = 0f;
            foreach (var t in _transformers)
            {
                if (t == null || t.InputNetwork == null || t.InputNetwork.ReferenceId != _tfFanout) continue;
                if (t.ReferenceId == _tfX || t.ReferenceId == _tfY) continue;
                if (!t.OnOff || t.Error == 1) continue;
                sumOthers += _tfPeak.TryGetValue(t.ReferenceId, out var d) ? d : 0f;
            }

            // F budget = cover Y + every other consumer fully + only 40% of X -> X is the sole marginal victim.
            _tfTarget = yDraw + sumOthers + 0.4f * xDraw;

            int n = Math.Max(_tfFeeders.Count, 1);
            float per = _tfTarget / n;
            foreach (var fref in _tfFeeders)
            {
                var f = SvTransformer(fref);
                if (f == null) continue;
                f.Setting = Math.Max(per + f.UsedPower, f.UsedPower + 100f);   // EffCap ~= per; above quiescent (not a dead input)
            }
            TfDrainFLocal();
            _log?.LogInfo($"[ScenarioRunner] 2CYC SETUP tick={_ticksSeen} Y.Setting={ySetting:0} (downstream demand {yDemand:0}, forces overload) " +
                $"yDraw={yDraw:0} xDraw={xDraw:0} sumOthers={sumOthers:0} F.targetBudget={_tfTarget:0} over {n} feeder(s); F-local elastics drained (feeder upstream untouched).");
        }

        private static void TfTrace(Assembly asm)
        {
            TfDrainFLocal();    // keep F's local stores empty so no backfill recovers X (feeder upstream untouched)
            _tfTraceCount++;
            var F = SvNet(_tfFanout);
            var xT = SvTransformer(_tfX);
            var yT = SvTransformer(_tfY);
            bool xShed = SvReg(asm, "PowerGridPlus.BrownoutRegistry", "IsShedding", _tfX);
            bool yOver = SvReg(asm, "PowerGridPlus.OverloadRegistry", "IsOverloaded", _tfY);
            bool yShed = SvReg(asm, "PowerGridPlus.BrownoutRegistry", "IsShedding", _tfY);
            if (xShed) _tfXShedTicks++;
            if (yOver) _tfYOverTicks++;
            float xReq = xT != null && xT.OutputNetwork != null ? xT.OutputNetwork.RequiredLoad : 0f;
            float yReq = yT != null && yT.OutputNetwork != null ? yT.OutputNetwork.RequiredLoad : 0f;
            float xDrawF = 0f, yDrawF = 0f, yGenG = 0f;
            try { if (xT != null && xT.InputNetwork != null) xDrawF = xT.GetUsedPower(xT.InputNetwork); } catch { }
            try { if (yT != null && yT.InputNetwork != null) yDrawF = yT.GetUsedPower(yT.InputNetwork); } catch { }
            try { if (yT != null && yT.OutputNetwork != null) yGenG = yT.GetGeneratedPower(yT.OutputNetwork); } catch { }
            _log?.LogInfo($"[ScenarioRunner] 2CYC t={_ticksSeen} " +
                (F != null ? $"F.Req={F.RequiredLoad:0} F.Pot={F.PotentialLoad:0} F.Short={F.ShortfallLoad:0} " : "F=<null> ") +
                $"| X={_tfX} shed={xShed} outReq={xReq:0} drawF={xDrawF:0} | Y={_tfY} overload={yOver} shed={yShed} downstreamReq={yReq:0} drawF={yDrawF:0} genG={yGenG:0}");
        }

        private static void TfVerdict(Assembly asm)
        {
            if (_tfTraceCount == 0) { _log?.LogError("[ScenarioRunner] 2CYC VERDICT: setup incomplete (no trace)."); return; }
            bool sweep = TfSweepEffective(asm);
            int half = Math.Max(1, _tfTraceCount / 2);
            bool xShed = _tfXShedTicks >= half;
            bool yOver = _tfYOverTicks >= half;

            _log?.LogInfo($"[ScenarioRunner] 2CYC VERDICT detail: sweepEffective={sweep} trace={_tfTraceCount}t " +
                $"X.shedTicks={_tfXShedTicks}/{_tfTraceCount} Y.overloadTicks={_tfYOverTicks}/{_tfTraceCount} " +
                $"Y.Setting={_tfYSetting:0} F.targetBudget={_tfTarget:0} demandSum={_tfDemandSum:0}");

            if (!yOver)
            {
                _log?.LogWarning("[ScenarioRunner] 2CYC VERDICT: INCONCLUSIVE -- Y did not overload on most ticks; the construction did not bite (raise the load on Y's downstream net, or pick a save with a heavier multi-level grid). No freeze conclusion possible.");
                return;
            }

            if (sweep)
            {
                if (!xShed)
                    _log?.LogInfo("[ScenarioRunner] 2CYC VERDICT: SWEEP PASS -- Y overloaded, X NOT frozen-shed. The re-decidable loop dropped the unnecessary shed once Y's overload freed F's budget. The 60s-freeze is gone.");
                else
                    _log?.LogError("[ScenarioRunner] 2CYC VERDICT: SWEEP FAIL -- X is shed despite Y's overload freeing F's budget. The re-decidable loop did NOT drop the unnecessary shed. Inspect the trace.");
            }
            else
            {
                if (xShed)
                    _log?.LogInfo("[ScenarioRunner] 2CYC VERDICT: LEGACY BUG REPRODUCED -- Y overloaded AND X frozen-shed. This is the 60s-freeze the Sweep allocator fixes (positive control). Re-run with Enable Sweep Allocator = true and expect SWEEP PASS.");
                else
                    _log?.LogWarning("[ScenarioRunner] 2CYC VERDICT: LEGACY NO-FREEZE -- X was not shed under Legacy; the construction is too weak on this save to force the 2-cycle, so the Sweep run is not a meaningful contrast. Strengthen the construction (bigger Y, tighter F throttle) and retry.");
            }
        }

        private static bool TfSweepEffective(Assembly asm)
        {
            try
            {
                var t = asm?.GetType("PowerGridPlus.SweepAllocatorSync");
                var p = t?.GetProperty("Effective",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                return p?.GetValue(null) is bool b && b;
            }
            catch { return false; }
        }

        private static int TfGetPriority(Assembly asm, long refId)
        {
            try
            {
                var t = asm?.GetType("PowerGridPlus.PriorityStore");
                var m = t?.GetMethod("GetPriority",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(long) }, null);
                return m?.Invoke(null, new object[] { refId }) is int i ? i : -1;
            }
            catch { return -1; }
        }

        // Zero ONLY the elastic stores whose OUTPUT network is F, so F's local battery/APC backfill cannot
        // mask the contention -- WITHOUT draining the feeder's own upstream supply. A global drain (the
        // pgp-shed-multilevel approach) kills a battery-backed feeder and makes F dead, which on this save
        // turns the contest degenerate. Reuses SvZeroStored from the shed-multilevel harness.
        private static void TfDrainFLocal()
        {
            foreach (var b in _batteries)
                if (b != null && b.OutputNetwork != null && b.OutputNetwork.ReferenceId == _tfFanout) SvZeroStored(b);
            foreach (var a in _apcs)
                if (a != null && a.OutputNetwork != null && a.OutputNetwork.ReferenceId == _tfFanout) SvZeroStored(a.Battery);
        }
    }
}
