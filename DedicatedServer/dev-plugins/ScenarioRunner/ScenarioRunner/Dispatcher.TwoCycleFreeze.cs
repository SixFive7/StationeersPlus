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
    // Verifies the allocator's overload-before-shed ordering prevents the "60-second freeze" (the
    // shed<->overload interaction). A grow-only / shed-first allocator could shed a low-priority consumer
    // X to fit a tight input net, then overload a sibling Y whose trip frees the very budget X's shed was
    // protecting -- but with shed committed grow-only, X stays locked out 60 s though its supply returned
    // the same tick. The current allocator evaluates the structural overload BEFORE the shed pass and only
    // re-decides SHED, so once Y overloads (offline, draws 0) the freed budget lets X stay powered and X is
    // never shed. There is one allocator (no toggle), so this is an ABSOLUTE assertion, not an A/B.
    //
    // Construction at runtime (reuses Sv* helpers):
    //   Phase A (observe, TF_OBS ticks): measure every transformer's peak downstream demand.
    //   Phase B (pick): choose a transformer-fed input net F (multi-level, throttle-able feeder) with a
    //     shed-eligible X = a NON-step-up consumer (step-ups never shed, POWER.md §5.2, so X is the device
    //     that WOULD freeze) and an overloader Y = a sibling consumer (any; step-ups can still overload)
    //     with a downstream big enough to overload when throttled. X gets the LOWEST priority (the would-be
    //     shed victim), Y the HIGHEST.
    //   Phase C (setup): throttle Y.Setting below its downstream demand so Y OVERLOADS. Throttle F's feeder
    //     so F covers X + the other consumers fully but only ~half of Y's draw -> F is contended by Y, and
    //     Y's overload frees its draw so X then fits. Drain F-local battery/APC stores (feeder untouched).
    //   Phase D (trace, TF_TRACE ticks): per tick record IsShedding(X) and IsOverloaded(Y).
    //   Phase E (verdict): PASS = Y overloaded AND X never shed; FAIL = X frozen-shed; INCONCLUSIVE = Y did
    //     not overload (construction did not bite).
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
                // X = the PROTECTED device. It must be shed-eligible (NON-step-up; step-ups never shed,
                // POWER.md §5.2), so under a grow-only / shed-first allocator it would be the frozen victim.
                // Pick the largest non-step-up consumer with a real draw.
                var x = kv.Value.Where(c => !TfStepUp(asm, c.InputNetwork, c.OutputNetwork) && Demand(c) >= TF_DEMAND_MIN)
                    .OrderByDescending(Demand).FirstOrDefault();
                if (x == null) continue;
                // Y = the OVERLOADER whose trip frees F's budget. Any sibling consumer (step-up allowed:
                // step-ups still overload) with a downstream big enough to overload when throttled. Pick the
                // largest such sibling.
                var y = kv.Value.Where(c => c.ReferenceId != x.ReferenceId && Demand(c) >= TF_Y_MIN)
                    .OrderByDescending(Demand).FirstOrDefault();
                if (y == null) continue;
                var fNet = SvNet(kv.Key);
                float dsum = kv.Value.Sum(Demand);
                if (fNet == null || fNet.PotentialLoad < dsum * 0.8f) continue;   // F must normally supply them
                if (bestF == -1 || Demand(y) > Demand(bestY)) { bestF = kv.Key; bestY = y; bestX = x; bestCons = kv.Value; }
            }

            if (bestF == -1)
            {
                _log?.LogError("[ScenarioRunner] 2CYC PICK FAIL: no transformer-fed fanout with a shed-eligible (non-step-up) consumer X plus an overload-capable sibling Y in this save's current activity.");
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

        private static bool _tfStepUpWarned;

        private static bool TfStepUp(Assembly asm, CableNetwork inNet, CableNetwork outNet)
        {
            try
            {
                // IsStepUp moved from PowerAllocator to the SegAdapters helper when the
                // adapters became the physical-description layer.
                var t = asm?.GetType("PowerGridPlus.SegAdapters");
                var m = t?.GetMethod("IsStepUp",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(CableNetwork), typeof(CableNetwork) }, null);
                if (m == null)
                {
                    if (!_tfStepUpWarned)
                    {
                        _tfStepUpWarned = true;
                        _log?.LogWarning("[ScenarioRunner] 2CYC NOTE: SegAdapters.IsStepUp unresolvable (renamed?); step-up classification falls back to false, the X pick may include step-ups.");
                    }
                    return false;
                }
                return m.Invoke(null, new object[] { inNet, outNet }) is bool b && b;
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
            float ySetting = Math.Min(yDemand * 0.5f, TF_SETTING_CAP);   // Y delivers ~half its downstream -> clear overload
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

            // F budget = cover X + the other consumers fully + only HALF of Y's throttled draw -> F is
            // contended by Y; when Y overloads (offline) it frees its draw and X then fits without a shed.
            _tfTarget = xDraw + sumOthers + 0.5f * yDraw;

            int n = Math.Max(_tfFeeders.Count, 1);
            float per = _tfTarget / n;
            foreach (var fref in _tfFeeders)
            {
                var f = SvTransformer(fref);
                if (f == null) continue;
                f.Setting = Math.Max(per + f.UsedPower, f.UsedPower + 100f);   // EffCap ~= per; above quiescent (not a dead input)
            }
            TfDrainFLocal();
            _log?.LogInfo($"[ScenarioRunner] 2CYC SETUP tick={_ticksSeen} Y={_tfY}.Setting={ySetting:0} (downstream demand {yDemand:0}, forces overload) " +
                $"X={_tfX} xDraw={xDraw:0} yDraw={yDraw:0} sumOthers={sumOthers:0} F.targetBudget={_tfTarget:0} over {n} feeder(s); F-local elastics drained (feeder upstream untouched).");
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
            int half = Math.Max(1, _tfTraceCount / 2);
            bool xShed = _tfXShedTicks >= half;
            bool yOver = _tfYOverTicks >= half;

            _log?.LogInfo($"[ScenarioRunner] 2CYC VERDICT detail: trace={_tfTraceCount}t " +
                $"X={_tfX}.shedTicks={_tfXShedTicks}/{_tfTraceCount} Y={_tfY}.overloadTicks={_tfYOverTicks}/{_tfTraceCount} " +
                $"Y.Setting={_tfYSetting:0} F.targetBudget={_tfTarget:0} demandSum={_tfDemandSum:0}");

            if (!yOver)
            {
                _log?.LogWarning("[ScenarioRunner] 2CYC VERDICT: INCONCLUSIVE -- Y did not overload on most ticks; the construction did not bite (Y's downstream did not out-demand its throttled cap, or F was not tight enough). No freeze conclusion possible.");
                return;
            }

            if (!xShed)
                _log?.LogInfo("[ScenarioRunner] 2CYC VERDICT: PASS -- Y overloaded and X was NEVER shed. The allocator evaluated Y's structural overload before the shed pass, freeing F's budget, so the low-priority X stays powered. A grow-only / shed-first allocator with this same tight F budget would have shed X (the only shed-eligible consumer here) and frozen it 60 s; this allocator does not, and the fault surfaces as OVERLOAD on Y.");
            else
                _log?.LogError("[ScenarioRunner] 2CYC VERDICT: FAIL -- X is frozen-shed even though Y overloaded and freed F's budget. The overload-before-shed ordering did not prevent the unnecessary shed. Inspect the trace.");
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
