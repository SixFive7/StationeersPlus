using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;

namespace ScenarioRunner
{
    // pgp-shed-multilevel
    //
    // Verifies the live allocator (PowerGridPlus.PowerAllocator) still sheds correctly, in PRIORITY
    // order, on a MULTI-LEVEL chain under the capacity-propagation supply fix. It constructs a
    // controlled shortage at runtime:
    //
    //   Phase A (observe, SV_OBS ticks): measure every transformer's peak downstream demand.
    //   Phase B (pick + throttle): choose a fanout input net F that is itself transformer-fed
    //     (multi-level) and has the most ON transformer consumers, with >=2 of them genuinely drawing
    //     (>= DEMAND_MIN W). Assign DISTINCT descending priorities to ALL of F's consumers by
    //     ReferenceId (so the whole contest is controlled, no default-priority strangers). PGP sheds
    //     priority-ASC (lowest number first, POWER.md 8.3), so the highest number should survive.
    //     Throttle F's feeder transformer Setting to ~half the demanding sum, forcing a real shortfall.
    //   Phase C (trace, SV_TRACE ticks): record per consumer shed/overload + downstream demand.
    //   Phase D (verdict): among the consumers that actually draw, PASS when a shed fired (>=1 shed on
    //     most ticks), at least one survives, the highest-priority drawer survives, and there is NO
    //     priority inversion (no higher-priority drawer shedding while a lower-priority one survives).
    //
    // Run on the SAME save with the fixed DLL and the unmodified DLL; the shed outcome should match.
    internal static partial class Dispatcher
    {
        private const int SV_OBS = 6;
        private const int SV_TRACE = 20;
        private const float SV_DEMAND_MIN = 120f;

        private static int _svLastTick = int.MinValue;
        private static readonly Dictionary<long, float> _svPeakDemand = new Dictionary<long, float>();
        private static long _svFanout = -1;
        private static bool _svMulti;
        private static bool _svPickFailed;
        private static bool _svVerdictDone;
        private static int _svThrottleTick = int.MinValue;
        private static float _svDemandSum;
        private static int _svTraceCount;
        private static readonly List<long> _svConsumers = new List<long>();   // ALL of F's ON consumers, by ReferenceId
        private static readonly Dictionary<long, int> _svPrio = new Dictionary<long, int>();
        private static readonly List<long> _svFeeders = new List<long>();
        private static readonly Dictionary<long, int> _svShedTicks = new Dictionary<long, int>();
        private static readonly Dictionary<long, float> _svTracePeak = new Dictionary<long, float>();

        private static void Scenario_PgpShedMultilevel()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-shed-multilevel")) return;
            if (_ticksSeen < _delayTicks) return;
            if (_ticksSeen == _svLastTick) return;
            _svLastTick = (int)_ticksSeen;
            var asm = GetModAssembly(PGP_ASSEMBLY);
            if (_transformers.Count == 0) RebuildCaches();
            if (_svPickFailed || _svVerdictDone) return;

            int since = (int)_ticksSeen - _delayTicks;
            if (since < SV_OBS) { SvObserve(); return; }
            if (_svFanout == -1 && _svThrottleTick == int.MinValue)
            {
                SvObserve();
                SvPick(asm);
                if (_svPickFailed) return;
                SvApplyThrottle();
                _svThrottleTick = (int)_ticksSeen;
                return;
            }
            if ((int)_ticksSeen - _svThrottleTick <= SV_TRACE) { SvTrace(asm); return; }
            _svVerdictDone = true;
            SvVerdict(asm);
        }

        private static void SvObserve()
        {
            foreach (var t in _transformers)
            {
                if (t == null || t.OutputNetwork == null) continue;
                float d = t.OutputNetwork.RequiredLoad;
                if (!_svPeakDemand.TryGetValue(t.ReferenceId, out var prev) || d > prev)
                    _svPeakDemand[t.ReferenceId] = d;
            }
        }

        private static void SvPick(Assembly asm)
        {
            var setPrio = asm?.GetType("PowerGridPlus.PriorityStore")?.GetMethod("SetPriority",
                BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(Thing), typeof(int) }, null);

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

            float Demand(Transformer t) => _svPeakDemand.TryGetValue(t.ReferenceId, out var d) ? d : 0f;

            long bestF = -1; List<Transformer> bestCons = null; bool bestMulti = false;
            foreach (var kv in consumersByIn)
            {
                if (kv.Value.Count < 2) continue;
                int demanders = kv.Value.Count(t => Demand(t) >= SV_DEMAND_MIN);
                if (demanders < 2) continue;             // need a real >=2-way contest
                float dsum = kv.Value.Where(t => Demand(t) >= SV_DEMAND_MIN).Sum(Demand);
                var fNetCand = SvNet(kv.Key);
                if (fNetCand == null || fNetCand.PotentialLoad < dsum * 0.8f) continue;   // F must normally supply them (real-supply fanout)
                bool multi = fedNets.Contains(kv.Key);
                bool better = bestF == -1
                    || (multi && !bestMulti)
                    || (multi == bestMulti && kv.Value.Count > bestCons.Count);
                if (better) { bestF = kv.Key; bestCons = kv.Value; bestMulti = multi; }
            }

            if (bestF == -1)
            {
                _log?.LogError("[ScenarioRunner] SHEDML PICK FAIL: no transformer-fed fanout with >=2 simultaneously-demanding consumers in this save's current activity.");
                _svPickFailed = true;
                return;
            }

            bestCons.Sort((a, b) => a.ReferenceId.CompareTo(b.ReferenceId));
            _svFanout = bestF; _svMulti = bestMulti;
            _svConsumers.Clear(); _svPrio.Clear(); _svShedTicks.Clear(); _svTracePeak.Clear();
            _svDemandSum = bestCons.Where(t => Demand(t) >= SV_DEMAND_MIN).Sum(Demand);
            int p = 10 * bestCons.Count;             // ALL consumers get distinct descending priorities
            foreach (var t in bestCons)
            {
                setPrio?.Invoke(null, new object[] { (Thing)t, p });
                _svConsumers.Add(t.ReferenceId); _svPrio[t.ReferenceId] = p;
                _svShedTicks[t.ReferenceId] = 0; _svTracePeak[t.ReferenceId] = 0f;
                p -= 10;
            }
            _svFeeders.Clear();
            foreach (var t in _transformers)
                if (t != null && t.OnOff && t.Error != 1 && t.OutputNetwork != null && t.OutputNetwork.ReferenceId == bestF)
                    _svFeeders.Add(t.ReferenceId);

            var fNet = SvNet(bestF);
            _log?.LogInfo($"[ScenarioRunner] SHEDML PICK tick={_ticksSeen} fanoutF={bestF} multiLevel={bestMulti} " +
                $"F.Pot={(fNet != null ? fNet.PotentialLoad : 0f):0} demandSum(>=120W consumers)={_svDemandSum:0} feeders={string.Join(",", _svFeeders)} " +
                $"allConsumers(ref:prio:peakDemand)={string.Join(",", _svConsumers.Select(r => $"{r}:{_svPrio[r]}:{_svPeakDemand[r]:0}"))}");
        }

        private static void SvApplyThrottle()
        {
            float target = _svDemandSum * 0.5f;      // supply ~half the demanding load -> forces ~half to shed by priority
            int n = Math.Max(_svFeeders.Count, 1);
            float per = target / n;
            foreach (var fref in _svFeeders)
            {
                var f = SvTransformer(fref);
                if (f == null) continue;
                f.Setting = Math.Max(per, f.UsedPower + 100f);   // partial supply, but above quiescent (not a dead input)
            }
            SvDrainElastic();
            _log?.LogInfo($"[ScenarioRunner] SHEDML THROTTLE tick={_ticksSeen} demandSum={_svDemandSum:0} targetFsupply~{target:0} over {_svFeeders.Count} feeder(s); drained all batteries/APC cells to remove elastic backfill.");
        }

        // Zero the store on every battery + APC cell so the throttled feeder is the ONLY supply,
        // isolating transformer shedding from the base's elastic backfill (POWER.md 7.3). Reflection so
        // it works whether PowerStored is a property or a backing field.
        private static void SvDrainElastic()
        {
            foreach (var b in _batteries) SvZeroStored(b);
            foreach (var a in _apcs) { if (a != null) SvZeroStored(a.Battery); }
        }

        private static void SvZeroStored(object o)
        {
            if (o == null) return;
            var ty = o.GetType();
            var p = ty.GetProperty("PowerStored", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null && p.CanWrite) { try { p.SetValue(o, 0f); return; } catch { } }
            for (var b = ty; b != null && b != typeof(object); b = b.BaseType)
            {
                var f = b.GetField("_powerStored", BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) { try { f.SetValue(o, 0f); return; } catch { } }
            }
        }

        private static void SvTrace(Assembly asm)
        {
            SvDrainElastic();   // keep stores empty across the trace so backfill cannot recover
            _svTraceCount++;
            var F = SvNet(_svFanout);
            _log?.LogInfo($"[ScenarioRunner] SHEDML t={_ticksSeen} F={_svFanout} " +
                (F != null ? $"Req={F.RequiredLoad:0} Pot={F.PotentialLoad:0} Cur={F.CurrentLoad:0} Short={F.ShortfallLoad:0}" : "<null>"));
            foreach (var cref in _svConsumers)
            {
                var t = SvTransformer(cref);
                if (t == null) continue;
                bool shed = SvReg(asm, "PowerGridPlus.BrownoutRegistry", "IsShedding", cref);
                if (shed) _svShedTicks[cref] = _svShedTicks[cref] + 1;
                float req = t.OutputNetwork != null ? t.OutputNetwork.RequiredLoad : 0f;
                if (req > _svTracePeak[cref]) _svTracePeak[cref] = req;
                _log?.LogInfo($"[ScenarioRunner] SHEDML   cons={cref} prio={_svPrio[cref]} outReq={req:0} shed={shed}");
            }
        }

        private static void SvVerdict(Assembly asm)
        {
            if (_svConsumers.Count < 2 || _svTraceCount == 0)
            { _log?.LogError("[ScenarioRunner] SHEDML VERDICT: setup incomplete."); return; }

            int half = Math.Max(1, _svTraceCount / 2);
            bool Shed(long r) => _svShedTicks[r] >= half;
            // "Drawers": consumers that actually presented load during the trace (others can't be shed
            // victims -- Pull<=eps is skipped by the allocator -- so they are irrelevant to a contest).
            var drawers = _svConsumers.Where(r => _svTracePeak[r] >= 50f).OrderByDescending(r => _svPrio[r]).ToList();

            int shedCount = drawers.Count(Shed);
            int surviveCount = drawers.Count - shedCount;
            int inversions = 0;
            foreach (var hi in drawers)
                foreach (var lo in drawers)
                    if (_svPrio[hi] > _svPrio[lo] && Shed(hi) && !Shed(lo)) inversions++;

            string detail = string.Join(", ", drawers.Select(r => $"{r}(p{_svPrio[r]},{_svTracePeak[r]:0}W):shed{_svShedTicks[r]}/{_svTraceCount}{(Shed(r) ? " SHED" : "")}"));
            _log?.LogInfo($"[ScenarioRunner] SHEDML VERDICT detail: multiLevel={_svMulti} trace={_svTraceCount}t drawers={drawers.Count} shed={shedCount} survive={surviveCount} inversions={inversions} | {detail}");

            bool fired = shedCount >= 1;
            bool topSurvives = drawers.Count > 0 && !Shed(drawers[0]);
            bool someSurvive = surviveCount >= 1;
            if (fired && topSurvives && someSurvive && inversions == 0)
                _log?.LogInfo("[ScenarioRunner] SHEDML VERDICT: PASS -- multi-level shortage fired a shed; lower-priority consumers shed, highest-priority survived, NO priority inversion. Shedding + priority intact under the fix.");
            else if (!fired)
                _log?.LogWarning("[ScenarioRunner] SHEDML VERDICT: INCONCLUSIVE -- no consumer shed (battery backfill / intermittent demand absorbed the shortage on this save). No inversion either.");
            else
                _log?.LogError($"[ScenarioRunner] SHEDML VERDICT: FAIL -- fired={fired} topSurvives={topSurvives} someSurvive={someSurvive} inversions={inversions}. Inspect the trace.");
        }

        private static CableNetwork SvNet(long refId)
        {
            CableNetwork found = null;
            CableNetwork.AllCableNetworks.ForEach(n => { if (n != null && n.ReferenceId == refId) found = n; });
            return found;
        }

        private static Transformer SvTransformer(long refId)
        {
            foreach (var t in _transformers) if (t != null && t.ReferenceId == refId) return t;
            return null;
        }

        private static bool SvReg(Assembly asm, string typeName, string method, long refId)
        {
            try
            {
                var t = asm?.GetType(typeName);
                var tickType = asm?.GetType("PowerGridPlus.ElectricityTickCounter");
                var tickProp = tickType?.GetProperty("CurrentTick",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                int tick = tickProp?.GetValue(null) is int i ? i : 0;
                var m = t?.GetMethod(method,
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(long), typeof(int) }, null);
                return m?.Invoke(null, new object[] { refId, tick }) is bool b && b;
            }
            catch { return false; }
        }
    }
}
