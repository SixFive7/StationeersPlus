using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace ScenarioRunner
{
    // Scenario: pgp-desire-split-fixture
    //
    // One-shot synthetic diagnostic of the availability-blind within-tier desire split
    // (POWER.md 8.0 backward pass): BackwardDesirePass splits a network's residual rigid
    // demand across a priority tier proportional to EffCap only, with no regard for each
    // supplier's own input-side availability. Combined with whole-victim shedding
    // (DecideVictimsDeepFirst sheds a claim WHOLE when its input net cannot fund it), a
    // physically servable multi-source net may converge dark: the split asks 500 W of a
    // supplier whose input can raise only 100 W, the shed moves the whole 1000 W claim
    // onto the other supplier whose input can raise only 900 W, that sheds too, and the
    // 2-cycle union can keep both off. Whether the model actually holds dark or
    // re-settles served is exactly what this fixture measures; the verdict lines are
    // the data for a later dedi run and for deciding whether the splitter needs an
    // availability term.
    //
    // Like pgp-overload-split-fixture the nets are synthetic mirror objects, but the
    // seam under test is the REAL deciding loop: PowerAllocator.RunAllocationLoop
    // invoked once per case (clearFaults: true, FRESH objects per case), which
    // internally runs BackwardDesirePass, DetectStructuralOverload,
    // ForwardSupplyAndDeprioritize, DecideVictimsDeepFirst, DetectSupplyOverload, and
    // the convergence / 2-cycle-union machinery. The loop mutates only the mirror
    // objects (registry commits live in RunAtomic's tail, which the fixture does not
    // run), so no shared state is touched and no cleanup pass is needed.
    //
    // Common topology per case, wired exactly as GATHER wires live rosters
    // (inNet.Consumers += seg, outNet.Suppliers += seg, seg.OutNetModel = the output
    // net's model; suppliers land in SupplierOrder, RefId ASC at equal priority):
    //
    //   I1 (gen varies) --S1--> N (RigidDemand 1000 W) <--S2-- I2 (gen varies)
    //
    // S1/S2: EffCap 5000 W, CapSetting 5000 W (Setting-limited), ample CableCap,
    // Priority 100, Locked false, zero quiescent (UsedPower 0), InputDrawFactor 1.
    // All three nets: ample WeakestCap (1e9 W) so the 5.7 cable rule stays silent; no
    // elastics, no softs (AvailableElastic and BillableSoftRequestLocal read the empty
    // ctor-initialised lists). topo order [I1, I2, N], topoRev reversed, netList all
    // three. Null CableNetwork references are safe: the loop never dereferences
    // Net.Network or Seg.InNet/OutNet (those feed RunAtomic's commit tail only).
    //
    // Cases:
    //   P1  asymmetric supply (I1 100 W, I2 900 W). Physically servable: S1 can pass
    //       100 and S2 can pass 900, exactly covering the 1000. The blind proportional
    //       split (equal EffCap, equal priority) asks 500 of each; I1 cannot fund 500.
    //       DIAGNOSTIC: no PASS/FAIL; logs the settled state plus a
    //       "DSF P1 VERDICT: SERVED" / "DSF P1 VERDICT: DARK (availability-blind shape
    //       confirmed)" line. The verdict line is the data.
    //   P2  matched supply (I1 500 W, I2 500 W): availability matches the split, so
    //       this MUST serve. Asserted: "DSF P2 PASS" / "DSF P2 FAIL: <numbers>".
    //   P3  dead-input soak (I1 0 W, I2 1100 W). The dead-input carveout (8.3.1: a
    //       dead net sheds nothing) means S1 is never deprioritized; the question is
    //       whether its standing desire share permanently soaks half the demand while
    //       I2 alone could fund the whole 1000 W. DIAGNOSTIC verdict line like P1,
    //       plus the granted numbers.
    //
    // Emits one "DSF <case> STATE ..." line per case (desired throughput, deprioritized
    // flags, granted throughput / pull, per-net unmet), the per-case verdict lines, and
    // a final "[ScenarioRunner] DSF SUMMARY pass=N fail=M verdicts=<P1>,<P3>".
    //
    // Synthetic ReferenceIds live in a 9920xxx band (pgp-chain-fixture owns
    // 99000001xx/99000002xx, pgp-overload-split-fixture 99000003xx,
    // pgp-fault-hover-fixture 99000004xx). Managed-state reflection only; worker-safe;
    // needs no save; one-shot (self-disarms after the run).
    internal static partial class Dispatcher
    {
        private static bool _dsfFired;
        private static int _dsfPass;
        private static int _dsfFail;
        private static string _dsfVerdictP1 = "UNRUN";
        private static string _dsfVerdictP3 = "UNRUN";

        // Synthetic ids (9920xxx band).
        private const long DSF_NET_IN1 = 9920000101L;
        private const long DSF_NET_IN2 = 9920000102L;
        private const long DSF_NET_OUT = 9920000103L;
        private const long DSF_SEG_S1 = 9920000111L;
        private const long DSF_SEG_S2 = 9920000112L;

        // Constructed numbers. DSF_EPS matches the verdict bar the task pins (0.5 W,
        // the same diagnostic tolerance the shortfall classifier uses).
        private const float DSF_EPS = 0.5f;
        private const float DSF_SEG_EFFCAP = 5000f;   // per-seg EffCap AND CapSetting (Setting-limited)
        private const int DSF_SEG_PRIORITY = 100;
        private const float DSF_DEMAND = 1000f;       // N's rigid demand
        private const float DSF_AMPLE_CABLE = 1e9f;   // WeakestCap / CableCap: rule 3 must stay silent

        // Reflection seams (resolved in Setup).
        private static Type _dsfNetT, _dsfSegT, _dsfElasticT;
        private static MethodInfo _dsfRunLoop;

        private static void DsfCheck(string id, bool ok, string detail)
        {
            if (ok) { _dsfPass++; _log?.LogInfo($"[ScenarioRunner] DSF {id} PASS: {detail}"); }
            else { _dsfFail++; _log?.LogError($"[ScenarioRunner] DSF {id} FAIL: {detail}"); }
        }

        private static void Scenario_PgpDesireSplitFixture()
        {
            if (_dsfFired) return;
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-desire-split-fixture")) return;
            _dsfFired = true;
            _dsfPass = 0;
            _dsfFail = 0;
            _dsfVerdictP1 = "UNRUN";
            _dsfVerdictP3 = "UNRUN";

            try
            {
                _log?.LogInfo("[ScenarioRunner] DSF START desire-split-fixture");
                if (DesireSplitFixture_Setup())
                {
                    DesireSplitFixture_P1_AsymmetricSupply();
                    DesireSplitFixture_P2_MatchedSupply();
                    DesireSplitFixture_P3_DeadInputSoak();
                }
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] DSF threw: {e}");
                _dsfFail++;
            }
            _log?.LogInfo($"[ScenarioRunner] DSF SUMMARY pass={_dsfPass} fail={_dsfFail} verdicts={_dsfVerdictP1},{_dsfVerdictP3}");
        }

        // ---- seams ----

        private static bool DesireSplitFixture_Setup()
        {
            var asm = GetModAssembly(PGP_ASSEMBLY);
            const BindingFlags SF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

            var allocT = asm?.GetType("PowerGridPlus.PowerAllocator");
            _dsfNetT = asm?.GetType("PowerGridPlus.PowerAllocator+Net");
            _dsfSegT = asm?.GetType("PowerGridPlus.PowerAllocator+Seg");
            _dsfElasticT = asm?.GetType("PowerGridPlus.PowerAllocator+Elastic");
            // Single overload: RunAllocationLoop(List<Net>, List<Net>, List<Seg>, List<Elastic>,
            // List<Net>, bool). Name-only lookup keeps the seam resilient to the private
            // parameter types.
            _dsfRunLoop = allocT?.GetMethod("RunAllocationLoop", SF);

            bool ok = _dsfRunLoop != null && _dsfNetT != null && _dsfSegT != null && _dsfElasticT != null;
            DsfCheck("SEAMS", ok,
                $"PowerAllocator.RunAllocationLoop={_dsfRunLoop != null} " +
                $"Net/Seg/Elastic mirrors={_dsfNetT != null}/{_dsfSegT != null}/{_dsfElasticT != null}");
            if (!ok)
                _log?.LogError("[ScenarioRunner] DSF cannot run without the allocation-loop + mirror seams; aborting.");
            return ok;
        }

        // ---- mirror-object helpers (private to this partial; throw loudly on a renamed
        //      field so the outer catch logs and counts a FAIL) ----

        private static object DsfNew(Type t) => Activator.CreateInstance(t, nonPublic: true);

        private static void DsfSet(object o, string field, object value)
        {
            var f = o.GetType().GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) throw new InvalidOperationException($"field '{field}' missing on {o.GetType().FullName}");
            f.SetValue(o, value);
        }

        private static object DsfGet(object o, string field)
        {
            var f = o.GetType().GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) throw new InvalidOperationException($"field '{field}' missing on {o.GetType().FullName}");
            return f.GetValue(o);
        }

        private static void DsfAddTo(object o, string listField, object item)
            => ((IList)DsfGet(o, listField)).Add(item);

        private static IList DsfTypedList(Type elementType)
            => (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType));

        // Every Net field the loop's passes read: Id (convergence sets and determinism
        // sorts), RigidDemand / GenSupply (desire and budget math), WeakestCap (rule 3
        // and the soft cable-headroom clamp). Suppliers / Consumers / Elastics / Softs
        // are ctor-initialised readonly lists (the last two stay empty here); Depth is
        // set by the caller for GATHER fidelity (the loop orders by the passed lists).
        private static object DsfNewNet(long id, float rigidDemand, float genSupply)
        {
            object net = DsfNew(_dsfNetT);
            DsfSet(net, "Id", id);
            DsfSet(net, "RigidDemand", rigidDemand);
            DsfSet(net, "GenSupply", genSupply);
            DsfSet(net, "WeakestCap", DSF_AMPLE_CABLE);
            return net;
        }

        // Every Seg field the loop's passes read gets an explicit value: RefId
        // (convergence sets, victim identity), CapSetting / CableCap (StampCapacityOverload's
        // Setting-limited gate), EffCap (the proportional splitter and grant caps),
        // UsedPower (quiescent term in every pull formula; zero here), InputDrawFactor
        // (the pull multiplier m; neutral 1), Priority (tier walk), Locked (every
        // active-gate), OutNetModel (FeedsActiveSeg hop protection). Kind / StepUp /
        // QuiescentAlwaysOn / PartnerRefId / device references keep their defaults:
        // either unread by the loop or neutral at default (false / 0 / null).
        private static object DsfNewSeg(long refId, object outNetModel)
        {
            object seg = DsfNew(_dsfSegT);
            DsfSet(seg, "RefId", refId);
            DsfSet(seg, "CapSetting", DSF_SEG_EFFCAP);
            DsfSet(seg, "CableCap", DSF_AMPLE_CABLE);
            DsfSet(seg, "EffCap", DSF_SEG_EFFCAP);
            DsfSet(seg, "UsedPower", 0f);
            DsfSet(seg, "InputDrawFactor", 1f);
            DsfSet(seg, "Priority", DSF_SEG_PRIORITY);
            DsfSet(seg, "Locked", false);
            DsfSet(seg, "OutNetModel", outNetModel);
            return seg;
        }

        // The settled per-case state the verdicts and log lines read back.
        private struct DsfResult
        {
            public bool S1Depri, S2Depri;
            public float S1Des, S2Des;
            public float S1Thr, S2Thr;
            public float S1Pull, S2Pull;
            public float NUnmet, I1Unmet, I2Unmet;
        }

        // Builds one FRESH three-net topology, runs the real deciding loop once
        // (clearFaults: true, exactly the once-per-tick entry RunAtomic makes), and
        // logs the settled state. Wiring mirrors GATHER's roster registration; list
        // insertion order IS SupplierOrder here (equal priority, RefId ASC).
        private static DsfResult DsfRunCase(string id, float gen1, float gen2)
        {
            object i1 = DsfNewNet(DSF_NET_IN1, 0f, gen1);
            object i2 = DsfNewNet(DSF_NET_IN2, 0f, gen2);
            object n = DsfNewNet(DSF_NET_OUT, DSF_DEMAND, 0f);
            object s1 = DsfNewSeg(DSF_SEG_S1, n);
            object s2 = DsfNewSeg(DSF_SEG_S2, n);
            DsfAddTo(i1, "Consumers", s1);
            DsfAddTo(i2, "Consumers", s2);
            DsfAddTo(n, "Suppliers", s1);
            DsfAddTo(n, "Suppliers", s2);
            // Depth mirrors BuildTopoOrder's topo-index assignment (nets) and RunAtomic's
            // input-net copy (segs). Not read inside the loop (order comes from the
            // passed lists), set for GATHER fidelity.
            DsfSet(i1, "Depth", 0);
            DsfSet(i2, "Depth", 1);
            DsfSet(n, "Depth", 2);
            DsfSet(s1, "Depth", 0);
            DsfSet(s2, "Depth", 1);

            IList topo = DsfTypedList(_dsfNetT);
            topo.Add(i1); topo.Add(i2); topo.Add(n);
            IList topoRev = DsfTypedList(_dsfNetT);
            topoRev.Add(n); topoRev.Add(i2); topoRev.Add(i1);
            IList netList = DsfTypedList(_dsfNetT);
            netList.Add(i1); netList.Add(i2); netList.Add(n);
            IList segs = DsfTypedList(_dsfSegT);
            segs.Add(s1); segs.Add(s2);
            IList elastics = DsfTypedList(_dsfElasticT);

            _dsfRunLoop.Invoke(null, new object[] { topo, topoRev, segs, elastics, netList, true });

            var r = new DsfResult
            {
                S1Depri = (bool)DsfGet(s1, "Deprioritized"),
                S2Depri = (bool)DsfGet(s2, "Deprioritized"),
                S1Des = (float)DsfGet(s1, "DesiredThroughput"),
                S2Des = (float)DsfGet(s2, "DesiredThroughput"),
                S1Thr = (float)DsfGet(s1, "Throughput"),
                S2Thr = (float)DsfGet(s2, "Throughput"),
                S1Pull = (float)DsfGet(s1, "Pull"),
                S2Pull = (float)DsfGet(s2, "Pull"),
                NUnmet = (float)DsfGet(n, "Unmet"),
                I1Unmet = (float)DsfGet(i1, "Unmet"),
                I2Unmet = (float)DsfGet(i2, "Unmet"),
            };
            _log?.LogInfo(
                $"[ScenarioRunner] DSF {id} STATE gen(I1={gen1:0},I2={gen2:0}) " +
                $"desiredThroughput(S1={r.S1Des:0},S2={r.S2Des:0}) " +
                $"deprioritized(S1={r.S1Depri},S2={r.S2Depri}) " +
                $"throughput(S1={r.S1Thr:0},S2={r.S2Thr:0}) pull(S1={r.S1Pull:0},S2={r.S2Pull:0}) " +
                $"unmet(N={r.NUnmet:0},I1={r.I1Unmet:0},I2={r.I2Unmet:0})");
            return r;
        }

        // ---- P1: the availability-blind shape (diagnostic; the verdict line is the data) ----

        private static void DesireSplitFixture_P1_AsymmetricSupply()
        {
            var r = DsfRunCase("P1", 100f, 900f);
            bool served = r.NUnmet <= DSF_EPS;
            _dsfVerdictP1 = served ? "SERVED" : "DARK";
            string verdict = served ? "SERVED" : "DARK (availability-blind shape confirmed)";
            _log?.LogInfo(
                $"[ScenarioRunner] DSF P1 VERDICT: {verdict} " +
                $"demand={DSF_DEMAND:0} served={DSF_DEMAND - r.NUnmet:0} unmet={r.NUnmet:0} " +
                $"(physically servable: S1 could pass {100f:0}, S2 could pass {900f:0})");
        }

        // ---- P2: control (availability matches the split; MUST serve) ----

        private static void DesireSplitFixture_P2_MatchedSupply()
        {
            var r = DsfRunCase("P2", 500f, 500f);
            DsfCheck("P2", r.NUnmet <= DSF_EPS,
                $"matched 500+500 supply must serve the 1000 W net: unmet(N)={r.NUnmet:0} (expect 0) " +
                $"throughput(S1={r.S1Thr:0},S2={r.S2Thr:0}) deprioritized(S1={r.S1Depri},S2={r.S2Depri})");
        }

        // ---- P3: dead-input soak (diagnostic; the verdict line is the data) ----

        private static void DesireSplitFixture_P3_DeadInputSoak()
        {
            var r = DsfRunCase("P3", 0f, 1100f);
            bool served = r.NUnmet <= DSF_EPS;
            _dsfVerdictP3 = served ? "SERVED" : "DARK";
            string verdict = served ? "SERVED" : "DARK (dead-input standing claim soaks the split)";
            _log?.LogInfo(
                $"[ScenarioRunner] DSF P3 VERDICT: {verdict} " +
                $"granted(S1={r.S1Thr:0},S2={r.S2Thr:0}) standingDesire(S1={r.S1Des:0},S2={r.S2Des:0}) " +
                $"demand={DSF_DEMAND:0} served={DSF_DEMAND - r.NUnmet:0} unmet={r.NUnmet:0} " +
                $"(I2 alone could fund the whole {DSF_DEMAND:0})");
        }
    }
}
