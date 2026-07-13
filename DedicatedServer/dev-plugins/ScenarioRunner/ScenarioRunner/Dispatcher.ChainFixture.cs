using System;
using System.Collections.Generic;
using System.Reflection;

namespace ScenarioRunner
{
    // Scenario: pgp-chain-fixture
    //
    // Synthetic seven-link chain proof of PowerGridPlus's leaf-first shedding policy
    // (POWER.md section 0 decision 24 stage 4) against the mod's flagship topology, driven
    // across dispatcher ticks (one fixture tick per simulation tick). The chain, linear:
    //
    //   link 1: producer net N1 (controlled head-end supply, solar-scale, GenSupply is the
    //           fixture's throttle knob)
    //   link 2: step-up transformer L2, N1 -> N2 (StepUp flag: never sheds, section 5.2)
    //   link 3: wireless transmitter/receiver pair L3, N2 -> N3. Substituted by a
    //           transformer-style hop (multiplier 1, no distance overhead): a synthetic
    //           dish pair is impractical with the fixture toolkit, and the hop-protection
    //           path under test is the same FeedsActiveSeg mechanism either way, so the
    //           substitution changes nothing about the policy being proven. StepUp=false,
    //           so its protection comes ONLY from FeedsActiveSeg.
    //   link 4: step-down transformer L4, N3 -> N4 (StepUp=false; FeedsActiveSeg-protected)
    //   link 5: battery bank net N4. The bank is soft-only storage: soft (storage charge)
    //           never enters budgets, shed victim selection, or overload detection
    //           (POWER.md section 9.6), so the rigid walk ignores it; the link exists so
    //           the trunk crosses a storage net exactly like the flagship base.
    //   link 6: step-up transformer L6, N4 -> N5 (StepUp flag)
    //   link 7: three leaf step-down transformers L7a / L7b / L7c, N5 -> N6a / N6b / N6c,
    //           each feeding a rigid 1000 W consumer, with distinct priorities
    //           100 / 50 / 10 (lowest priority sheds first).
    //
    // What is REAL and what is modelled. The fixture cannot spawn Things, so the chain is
    // a synthetic model, but every decision seam it exercises is the mod's own code via
    // reflection:
    //   - Victim CHOICE per contended net per round: PowerAllocator.SelectShedVictims
    //     (the same pure selector pgp-shed-victim-fixture drives).
    //   - Hop protection per round: PowerAllocator.FeedsActiveSeg, invoked against
    //     reflection-built mirror Seg/Net instances whose Locked/Shed flags track the
    //     fixture chain (plus five one-shot predicate checks). If the mirror surface is
    //     unresolvable the fixture counts a FAIL and falls back to its own hop flags.
    //   - Lockout semantics: the real BrownoutRegistry (NoteShed on a commit, IsLockedOut
    //     gating the next tick, SnapshotRemaining for the countdown, the expiry boundary,
    //     ClearLockout for cleanup), keyed by the real ElectricityTickCounter.CurrentTick.
    //   - Liveness vocabulary: NetLiveness's Live / DeadUnmet / DeadNoSupply byte
    //     constants, applied through the same formula the allocator publishes
    //     (Unmet > Eps -> DeadUnmet; supply present -> Live; else DeadNoSupply; the 60 s
    //     DeadUnmet hold is not modelled).
    //   The walk connecting those seams mirrors the documented allocation loop: rounds
    //   that clear and re-decide shed flags until the shed set is stable, a backward
    //   desire pass where a locked seg stops desiring but a merely-shed one does not, and
    //   a source-to-leaf forward pass granting within each net's budget.
    //
    // Tick schedule (t = fixture ticks since start) and assertions:
    //   t=0        resolve seams (P1), FeedsActiveSeg predicate checks (P2a-P2e), build.
    //   t=1..3     PHASE A full supply (3500 W head-end, 3000 W leaf demand): P3 = no
    //              victims, no overload condition, every net Live, all leaves served.
    //   t=4..6     PHASE B zero practical load (consumers off, full supply): P4 = nothing
    //              sheds or faults; no net ever DeadUnmet (an unloaded chain must never
    //              fault; unloaded trunk nets legitimately read DeadNoSupply).
    //   t=7..9     PHASE C1 deficit step 1 (2600 W): P5 = exactly L7c (priority 10) sheds
    //              on the first deficit tick and enters the real lockout; no further
    //              victims while it is locked out; N6c reads DeadUnmet, N6a/N6b stay
    //              served.
    //   t=10..12   PHASE C2 deficit step 2 (1600 W): P6 = exactly L7b (priority 50).
    //   t=13..15   PHASE C3 deficit step 3 (600 W): P7 = exactly L7a (priority 100); over
    //              the whole run no mid-chain hop (L2 / L3 / L4 / L6) ever appears as a
    //              shed victim or in the shed registry; once every leaf is locked the
    //              no-practical-load trunk never sheds and never reads DeadUnmet.
    //   t=16..17   PHASE D recovery (supply restored, leaves still locked): P8 = the
    //              registry's remaining-ticks countdown decreases between consecutive
    //              ticks for every locked leaf.
    //   t=18       P9 = expiry boundary per leaf: IsLockedOut is true one tick before the
    //              lockout's expiry tick and false AT it (probing with a future tick, the
    //              same trick pgp-atomic-probe uses, so the 60 s expiry is proven without
    //              waiting; the false probe also self-cleans the registry entry).
    //   t=19       P10 = with the lockouts gone every leaf re-engages: full serve, no
    //              victims, every net Live. Cleanup (ClearLockout on every fixture ref)
    //              and the summary line:
    //                [ScenarioRunner] CHAIN SUMMARY pass=N fail=M
    //
    // Per tick the fixture logs one grep-able state line:
    //   [ScenarioRunner] CHAIN t=<n> phase=<X> supply=<W> victims=[...] locked=[...]
    //     leafGrants=[a,b,c] verdicts=[...]
    //
    // Synthetic ReferenceIds live in a 99000001xx band no live save reaches; the only
    // shared mutable state touched is BrownoutRegistry (synthetic refs only, cleaned on
    // every exit path). Managed-state reflection only; worker-safe; needs no save (run on
    // any world, including a fresh -New).
    internal static partial class Dispatcher
    {
        private const float CF_EPS = 0.01f;              // PowerAllocator.Eps
        private const float CF_LEAF_DEMAND = 1000f;
        private const float CF_LEAF_CAP = 2000f;
        private const float CF_TRUNK_CAP = 100000f;
        private const float CF_SUPPLY_FULL = 3500f;
        private const float CF_SUPPLY_STEP1 = 2600f;
        private const float CF_SUPPLY_STEP2 = 1600f;
        private const float CF_SUPPLY_STEP3 = 600f;

        private sealed class ChainNet
        {
            public long Id;
            public string Name;
            public float GenSupply;
            public float RigidDemand;
            public readonly List<ChainSeg> Suppliers = new List<ChainSeg>();
            public readonly List<ChainSeg> Consumers = new List<ChainSeg>();
            // per-round results
            public float FirmIn;
            public float Unmet;
            public byte Verdict;
            public object MirrorNet;        // PowerAllocator+Net mirror (for FeedsActiveSeg)
        }

        private sealed class ChainSeg
        {
            public long RefId;
            public string Name;
            public ChainNet In;
            public ChainNet Out;
            public float EffCap;
            public int Priority;
            public bool StepUp;
            public bool IsTrunkHop;         // L2 / L3 / L4 / L6: must never shed
            // per-tick / per-round state
            public bool Locked;             // real BrownoutRegistry lockout, read at tick start
            public bool Shed;               // re-decided per round
            public float DesiredPull;
            public float Throughput;
            public object MirrorSeg;        // PowerAllocator+Seg mirror (for FeedsActiveSeg)
        }

        private static int _cfTick = -1;
        private static bool _cfDone;
        private static int _cfPass;
        private static int _cfFail;
        private static bool _cfBuilt;
        private static bool _cfMirrorsOk;

        private static readonly List<ChainNet> _cfNets = new List<ChainNet>();   // topo order, source first
        private static readonly List<ChainSeg> _cfSegs = new List<ChainSeg>();
        private static ChainNet _cfN1;
        private static ChainSeg _cfL7a, _cfL7b, _cfL7c;

        // Reflection seams.
        private static MethodInfo _cfSelect;             // PowerAllocator.SelectShedVictims
        private static MethodInfo _cfFeedsActiveSeg;     // PowerAllocator.FeedsActiveSeg(Seg)
        private static Type _cfSegType;                  // PowerAllocator+Seg
        private static Type _cfNetType;                  // PowerAllocator+Net
        private static MethodInfo _cfNoteShed;           // BrownoutRegistry.NoteShed(long, int)
        private static MethodInfo _cfIsLockedOut;        // BrownoutRegistry.IsLockedOut(long, int)
        private static MethodInfo _cfSnapshotRemaining;  // BrownoutRegistry.SnapshotRemaining(int)
        private static MethodInfo _cfClearLockout;       // BrownoutRegistry.ClearLockout(long)
        private static PropertyInfo _cfCurrentTick;      // ElectricityTickCounter.CurrentTick
        private static byte _cfLive = 1, _cfDeadUnmet = 2, _cfDeadNoSupply = 3;

        // Cross-phase accumulators.
        private static readonly List<long> _cfAllVictims = new List<long>();
        private static readonly List<long> _cfTickVictims = new List<long>();
        private static int _cfOverloadHits;
        private static int _cfPhaseVictimStart;                     // _cfAllVictims count at phase start
        private static readonly Dictionary<long, int> _cfRemainingPrev = new Dictionary<long, int>();

        private static void ChainCheck(string id, bool ok, string detail)
        {
            if (ok) { _cfPass++; _log?.LogInfo($"[ScenarioRunner] CHAIN {id} PASS: {detail}"); }
            else { _cfFail++; _log?.LogError($"[ScenarioRunner] CHAIN {id} FAIL: {detail}"); }
        }

        private static void Scenario_PgpChainFixture()
        {
            if (_cfDone) return;
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-chain-fixture")) return;

            try
            {
                _cfTick++;
                if (_cfTick == 0) { ChainFixture_Setup(); return; }
                if (!_cfBuilt) { _cfDone = true; return; }   // setup failed loudly already

                // Phase parameters for this tick.
                float supply;
                float demand;
                string phase;
                if (_cfTick <= 3) { supply = CF_SUPPLY_FULL; demand = CF_LEAF_DEMAND; phase = "A-full"; }
                else if (_cfTick <= 6) { supply = CF_SUPPLY_FULL; demand = 0f; phase = "B-noload"; }
                else if (_cfTick <= 9) { supply = CF_SUPPLY_STEP1; demand = CF_LEAF_DEMAND; phase = "C1-2600"; }
                else if (_cfTick <= 12) { supply = CF_SUPPLY_STEP2; demand = CF_LEAF_DEMAND; phase = "C2-1600"; }
                else if (_cfTick <= 15) { supply = CF_SUPPLY_STEP3; demand = CF_LEAF_DEMAND; phase = "C3-600"; }
                else { supply = CF_SUPPLY_FULL; demand = CF_LEAF_DEMAND; phase = "D-recovery"; }

                bool phaseStart = _cfTick == 1 || _cfTick == 4 || _cfTick == 7 || _cfTick == 10
                                  || _cfTick == 13 || _cfTick == 16;
                if (phaseStart) _cfPhaseVictimStart = _cfAllVictims.Count;

                ChainFixture_RunTick(phase, supply, demand);

                // Phase-boundary assertions.
                switch (_cfTick)
                {
                    case 3: ChainFixture_AssertPhaseA(); break;
                    case 6: ChainFixture_AssertPhaseB(); break;
                    case 9: ChainFixture_AssertDeficitStep("P5", _cfL7c, "C1", new[] { _cfL7c.Out }); break;
                    case 12: ChainFixture_AssertDeficitStep("P6", _cfL7b, "C2", new[] { _cfL7b.Out, _cfL7c.Out }); break;
                    case 15: ChainFixture_AssertPhaseC3(); break;
                    case 16:
                        // Seed the countdown baseline for the P8 comparison at t=17.
                        _cfRemainingPrev.Clear();
                        foreach (var kv in ChainFixture_ReadRemaining()) _cfRemainingPrev[kv.Key] = kv.Value;
                        break;
                    case 17: ChainFixture_AssertCountdown(); break;
                    case 18: ChainFixture_AssertExpiryBoundary(); break;
                    case 19: ChainFixture_AssertRecoveryAndFinish(); break;
                }
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] CHAIN threw: {e}");
                _cfFail++;
                ChainFixture_Finish();
            }
        }

        // ---- t=0: seams, predicate checks, construction ----

        private static void ChainFixture_Setup()
        {
            var asm = GetModAssembly(PGP_ASSEMBLY);
            var allocatorType = asm?.GetType("PowerGridPlus.PowerAllocator");
            var brownoutType = asm?.GetType("PowerGridPlus.BrownoutRegistry");
            var tickType = asm?.GetType("PowerGridPlus.ElectricityTickCounter");
            var livenessType = asm?.GetType("PowerGridPlus.NetLiveness");
            const BindingFlags SF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

            _cfSelect = allocatorType?.GetMethod("SelectShedVictims", SF);
            _cfFeedsActiveSeg = allocatorType?.GetMethod("FeedsActiveSeg", SF);
            _cfSegType = asm?.GetType("PowerGridPlus.PowerAllocator+Seg");
            _cfNetType = asm?.GetType("PowerGridPlus.PowerAllocator+Net");
            _cfNoteShed = brownoutType?.GetMethod("NoteShed", SF, null, new[] { typeof(long), typeof(int) }, null);
            _cfIsLockedOut = brownoutType?.GetMethod("IsLockedOut", SF, null, new[] { typeof(long), typeof(int) }, null);
            _cfSnapshotRemaining = brownoutType?.GetMethod("SnapshotRemaining", SF);
            _cfClearLockout = brownoutType?.GetMethod("ClearLockout", SF, null, new[] { typeof(long) }, null);
            _cfCurrentTick = tickType?.GetProperty("CurrentTick", SF);

            var liveField = livenessType?.GetField("Live", SF);
            var deadUnmetField = livenessType?.GetField("DeadUnmet", SF);
            var deadNoSupplyField = livenessType?.GetField("DeadNoSupply", SF);
            bool livenessOk = liveField != null && deadUnmetField != null && deadNoSupplyField != null;
            if (livenessOk)
            {
                _cfLive = (byte)liveField.GetValue(null);
                _cfDeadUnmet = (byte)deadUnmetField.GetValue(null);
                _cfDeadNoSupply = (byte)deadNoSupplyField.GetValue(null);
                livenessOk = _cfLive != _cfDeadUnmet && _cfLive != _cfDeadNoSupply && _cfDeadUnmet != _cfDeadNoSupply;
            }

            bool coreOk = _cfSelect != null && _cfNoteShed != null && _cfIsLockedOut != null
                          && _cfSnapshotRemaining != null && _cfClearLockout != null && _cfCurrentTick != null;
            ChainCheck("P1", coreOk && livenessOk,
                $"stable seams resolved: SelectShedVictims={_cfSelect != null} NoteShed={_cfNoteShed != null} " +
                $"IsLockedOut={_cfIsLockedOut != null} SnapshotRemaining={_cfSnapshotRemaining != null} " +
                $"ClearLockout={_cfClearLockout != null} CurrentTick={_cfCurrentTick != null} " +
                $"NetLiveness(Live={_cfLive},DeadUnmet={_cfDeadUnmet},DeadNoSupply={_cfDeadNoSupply} distinct={livenessOk})");
            if (!coreOk)
            {
                _log?.LogError("[ScenarioRunner] CHAIN cannot run without the core seams; aborting.");
                ChainFixture_Finish();
                return;
            }

            ChainFixture_CheckFeedsActiveSegPredicate();
            ChainFixture_Build();
            _cfBuilt = true;

            _log?.LogInfo(
                "[ScenarioRunner] CHAIN construction: N1(producer, GenSupply throttled per phase) " +
                "-> L2(step-up, StepUp flag) -> N2 -> L3(wireless pair SUBSTITUTED by a transformer-style hop, " +
                "multiplier 1; FeedsActiveSeg-protected, the same mechanism a dish pair rides) -> N3 " +
                "-> L4(step-down, FeedsActiveSeg-protected) -> N4(battery bank net; the bank is soft-only " +
                "storage and soft never enters budgets or shed selection, so the rigid walk ignores it) " +
                "-> L6(step-up, StepUp flag) -> N5 -> L7a(prio 100)/L7b(prio 50)/L7c(prio 10, sheds first) " +
                $"-> N6a/N6b/N6c with {CF_LEAF_DEMAND:0} W rigid consumers each. " +
                $"Leaf caps {CF_LEAF_CAP:0} W, trunk caps {CF_TRUNK_CAP:0} W, multiplier 1, quiescent 0.");
        }

        // Five one-shot checks against the real FeedsActiveSeg predicate with mirror
        // Seg/Net instances: a hop is protected while any OTHER consumer of its output net
        // is active; Shed / Locked / Overloaded children do not protect; the hop itself in
        // the list is skipped by the ReferenceEquals guard.
        private static void ChainFixture_CheckFeedsActiveSegPredicate()
        {
            _cfMirrorsOk = false;
            if (_cfFeedsActiveSeg == null || _cfSegType == null || _cfNetType == null)
            {
                ChainCheck("P2", false,
                    $"FeedsActiveSeg mirror surface unresolvable (method={_cfFeedsActiveSeg != null} " +
                    $"Seg={_cfSegType != null} Net={_cfNetType != null}); per-round hop flags fall back to the fixture's own computation.");
                return;
            }

            try
            {
                object hop = ChainFixture_NewMirrorSeg(1L);
                object child = ChainFixture_NewMirrorSeg(2L);
                object net = ChainFixture_NewMirrorNet(10L);
                ChainFixture_MirrorNetAddConsumer(net, child);
                ChainFixture_SetMirror(hop, "OutNetModel", net);

                bool r1 = ChainFixture_InvokeFeedsActiveSeg(hop);
                ChainCheck("P2a", r1, $"hop with one active child on its output net is protected (got {r1}).");

                ChainFixture_SetMirror(child, "Shed", true);
                bool r2 = ChainFixture_InvokeFeedsActiveSeg(hop);
                ChainCheck("P2b", !r2, $"a Shed child does not protect the hop (got {r2}).");
                ChainFixture_SetMirror(child, "Shed", false);

                ChainFixture_SetMirror(child, "Locked", true);
                bool r3 = ChainFixture_InvokeFeedsActiveSeg(hop);
                ChainCheck("P2c", !r3, $"a Locked (locked-out) child does not protect the hop (got {r3}).");
                ChainFixture_SetMirror(child, "Locked", false);

                ChainFixture_SetMirror(child, "Overloaded", true);
                bool r4 = ChainFixture_InvokeFeedsActiveSeg(hop);
                ChainCheck("P2d", !r4, $"an Overloaded child does not protect the hop (got {r4}).");
                ChainFixture_SetMirror(child, "Overloaded", false);

                object selfNet = ChainFixture_NewMirrorNet(11L);
                ChainFixture_MirrorNetAddConsumer(selfNet, hop);
                ChainFixture_SetMirror(hop, "OutNetModel", selfNet);
                bool r5 = ChainFixture_InvokeFeedsActiveSeg(hop);
                ChainCheck("P2e", !r5, $"the hop itself in its output net's consumer list is skipped (got {r5}).");

                _cfMirrorsOk = true;
            }
            catch (Exception e)
            {
                ChainCheck("P2", false, $"FeedsActiveSeg mirror checks threw: {e.GetBaseException().Message}; falling back to fixture hop flags.");
                _cfMirrorsOk = false;
            }
        }

        private static object ChainFixture_NewMirrorSeg(long refId)
        {
            object seg = Activator.CreateInstance(_cfSegType, nonPublic: true);
            ChainFixture_SetMirror(seg, "RefId", refId);
            return seg;
        }

        private static object ChainFixture_NewMirrorNet(long id)
        {
            object net = Activator.CreateInstance(_cfNetType, nonPublic: true);
            ChainFixture_SetMirror(net, "Id", id);
            return net;
        }

        private static void ChainFixture_MirrorNetAddConsumer(object mirrorNet, object mirrorSeg)
        {
            var consumersField = _cfNetType.GetField("Consumers", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var list = consumersField?.GetValue(mirrorNet) as System.Collections.IList;
            list?.Add(mirrorSeg);
        }

        private static void ChainFixture_SetMirror(object instance, string field, object value)
        {
            var f = instance.GetType().GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            f?.SetValue(instance, value);
        }

        private static bool ChainFixture_InvokeFeedsActiveSeg(object mirrorSeg)
        {
            return _cfFeedsActiveSeg.Invoke(null, new[] { mirrorSeg }) is bool b && b;
        }

        // ---- Chain construction ----

        private static ChainNet ChainFixture_AddNet(long id, string name)
        {
            var n = new ChainNet { Id = id, Name = name };
            if (_cfMirrorsOk) n.MirrorNet = ChainFixture_NewMirrorNet(id);
            _cfNets.Add(n);
            return n;
        }

        private static ChainSeg ChainFixture_AddSeg(long refId, string name, ChainNet input, ChainNet output,
            float cap, int priority, bool stepUp, bool trunkHop)
        {
            var s = new ChainSeg
            {
                RefId = refId,
                Name = name,
                In = input,
                Out = output,
                EffCap = cap,
                Priority = priority,
                StepUp = stepUp,
                IsTrunkHop = trunkHop,
            };
            input.Consumers.Add(s);
            output.Suppliers.Add(s);
            if (_cfMirrorsOk)
            {
                s.MirrorSeg = ChainFixture_NewMirrorSeg(refId);
                ChainFixture_SetMirror(s.MirrorSeg, "Priority", priority);
                ChainFixture_SetMirror(s.MirrorSeg, "StepUp", stepUp);
                ChainFixture_SetMirror(s.MirrorSeg, "OutNetModel", output.MirrorNet);
                ChainFixture_MirrorNetAddConsumer(input.MirrorNet, s.MirrorSeg);
            }
            _cfSegs.Add(s);
            return s;
        }

        private static void ChainFixture_Build()
        {
            _cfNets.Clear();
            _cfSegs.Clear();

            _cfN1 = ChainFixture_AddNet(9900000101L, "N1-producer");
            var n2 = ChainFixture_AddNet(9900000102L, "N2");
            var n3 = ChainFixture_AddNet(9900000103L, "N3");
            var n4 = ChainFixture_AddNet(9900000104L, "N4-batteryBank");
            var n5 = ChainFixture_AddNet(9900000105L, "N5");
            var n6a = ChainFixture_AddNet(9900000106L, "N6a");
            var n6b = ChainFixture_AddNet(9900000107L, "N6b");
            var n6c = ChainFixture_AddNet(9900000108L, "N6c");

            ChainFixture_AddSeg(9900000202L, "L2-stepUp", _cfN1, n2, CF_TRUNK_CAP, 100, stepUp: true, trunkHop: true);
            ChainFixture_AddSeg(9900000203L, "L3-wirelessHop", n2, n3, CF_TRUNK_CAP, 100, stepUp: false, trunkHop: true);
            ChainFixture_AddSeg(9900000204L, "L4-stepDown", n3, n4, CF_TRUNK_CAP, 100, stepUp: false, trunkHop: true);
            ChainFixture_AddSeg(9900000206L, "L6-stepUp", n4, n5, CF_TRUNK_CAP, 100, stepUp: true, trunkHop: true);
            _cfL7a = ChainFixture_AddSeg(9900000207L, "L7a-leaf", n5, n6a, CF_LEAF_CAP, 100, stepUp: false, trunkHop: false);
            _cfL7b = ChainFixture_AddSeg(9900000208L, "L7b-leaf", n5, n6b, CF_LEAF_CAP, 50, stepUp: false, trunkHop: false);
            _cfL7c = ChainFixture_AddSeg(9900000209L, "L7c-leaf", n5, n6c, CF_LEAF_CAP, 10, stepUp: false, trunkHop: false);
        }

        // ---- Per-tick walk: mirrors the documented allocation loop over the chain ----

        private static int ChainFixture_RealTick()
        {
            return _cfCurrentTick?.GetValue(null) is int i ? i : 0;
        }

        private static bool ChainFixture_HopProtected(ChainSeg seg)
        {
            if (seg.StepUp) return true;
            if (_cfMirrorsOk && seg.MirrorSeg != null)
            {
                try { return ChainFixture_InvokeFeedsActiveSeg(seg.MirrorSeg); }
                catch { }
            }
            // Fallback: the fixture's own hop computation (any other ACTIVE consumer on
            // the seg's output net; matches the predicate semantics checked in P2a-P2e).
            foreach (var child in seg.Out.Consumers)
                if (!ReferenceEquals(child, seg) && !child.Locked && !child.Shed) return true;
            return false;
        }

        private static void ChainFixture_SyncMirrors()
        {
            if (!_cfMirrorsOk) return;
            foreach (var s in _cfSegs)
            {
                if (s.MirrorSeg == null) continue;
                ChainFixture_SetMirror(s.MirrorSeg, "Locked", s.Locked);
                ChainFixture_SetMirror(s.MirrorSeg, "Shed", s.Shed);
            }
        }

        private static void ChainFixture_RunTick(string phase, float supply, float demand)
        {
            int tickNow = ChainFixture_RealTick();
            _cfTickVictims.Clear();

            // Head-end supply + leaf demand for this phase.
            _cfN1.GenSupply = supply;
            _cfL7a.Out.RigidDemand = demand;
            _cfL7b.Out.RigidDemand = demand;
            _cfL7c.Out.RigidDemand = demand;

            // Real lockout gate (mirrors GATHER's IsPowerLocked read).
            foreach (var s in _cfSegs)
                s.Locked = _cfIsLockedOut.Invoke(null, new object[] { s.RefId, tickNow }) is bool b && b;

            // Deciding rounds: clear shed, desire, forward grant + shed, until stable.
            int maxRounds = 2 * _cfSegs.Count + 4;
            HashSet<long> prevShed = null;
            for (int round = 0; round < maxRounds; round++)
            {
                foreach (var s in _cfSegs) s.Shed = false;

                // Backward desire pass (leaf -> source). A LOCKED seg stops desiring; a
                // merely-shed one does not (DesireActive semantics), which is why the
                // deciding rounds stay re-decidable.
                for (int ni = _cfNets.Count - 1; ni >= 0; ni--)
                {
                    var n = _cfNets[ni];
                    float desire = n.RigidDemand;
                    foreach (var c in n.Consumers)
                        if (!c.Locked) desire += c.DesiredPull;
                    foreach (var s in n.Suppliers)
                        s.DesiredPull = s.Locked ? 0f : Math.Min(desire, s.EffCap);
                }

                ChainFixture_SyncMirrors();

                // Forward pass (source -> leaf): budget, shed decision via the REAL
                // selector with hop protection, then sequential grants.
                foreach (var n in _cfNets)
                {
                    float firmIn = n.GenSupply;
                    foreach (var s in n.Suppliers)
                        if (!s.Locked && !s.Shed) firmIn += s.Throughput;
                    n.FirmIn = firmIn;

                    float budget = firmIn - n.RigidDemand;
                    if (budget < 0f) budget = 0f;

                    if (firmIn > CF_EPS)
                    {
                        float claims = 0f;
                        foreach (var c in n.Consumers)
                            if (!c.Locked && !c.Shed) claims += c.DesiredPull;
                        if (claims > budget + CF_EPS)
                        {
                            var candidates = new List<(long, int, float, bool)>();
                            foreach (var c in n.Consumers)
                                if (!c.Locked && !c.Shed)
                                    candidates.Add((c.RefId, c.Priority, c.DesiredPull, ChainFixture_HopProtected(c)));
                            var victims = (List<long>)_cfSelect.Invoke(null, new object[] { candidates, claims - budget });
                            foreach (long refId in victims)
                            {
                                foreach (var c in n.Consumers)
                                    if (c.RefId == refId) { c.Shed = true; break; }
                            }
                        }
                    }

                    float remaining = budget;
                    foreach (var c in n.Consumers)
                    {
                        if (c.Locked || c.Shed) { c.Throughput = 0f; continue; }
                        float grant = Math.Min(c.DesiredPull, remaining);
                        if (grant < 0f) grant = 0f;
                        c.Throughput = grant;
                        remaining -= grant;
                    }

                    float activeWant = n.RigidDemand;
                    foreach (var c in n.Consumers)
                        if (!c.Locked && !c.Shed) activeWant += c.DesiredPull;
                    n.Unmet = Math.Max(0f, activeWant - firmIn);
                }

                var curShed = new HashSet<long>();
                foreach (var s in _cfSegs) if (s.Shed) curShed.Add(s.RefId);
                if (prevShed != null && curShed.SetEquals(prevShed)) break;
                prevShed = curShed;
            }

            // Overload condition (POWER.md section 8.4 structural shape): a seg pushing at
            // its capacity with unmet downstream rigid demand. Caps are generous, so any
            // hit is a fixture-visible fault.
            foreach (var s in _cfSegs)
                if (!s.Locked && !s.Shed && s.DesiredPull >= s.EffCap - CF_EPS && s.Out.Unmet > CF_EPS)
                    _cfOverloadHits++;

            // Commit new sheds into the REAL registry (mirrors the RunAtomic tail).
            foreach (var s in _cfSegs)
            {
                if (s.Locked || !s.Shed) continue;
                _cfNoteShed.Invoke(null, new object[] { s.RefId, tickNow });
                _cfTickVictims.Add(s.RefId);
                _cfAllVictims.Add(s.RefId);
                _log?.LogInfo($"[ScenarioRunner] CHAIN shed event t={_cfTick} phase={phase} victim={s.Name} ref={s.RefId} priority={s.Priority} (round-decided, committed to BrownoutRegistry)");
            }

            // Liveness verdicts (NetLiveness formula, fixture-evaluated with the mod's
            // byte constants; the 60 s DeadUnmet hold is not modelled).
            foreach (var n in _cfNets)
            {
                bool hasSupply = n.FirmIn > CF_EPS;
                n.Verdict = n.Unmet > CF_EPS ? _cfDeadUnmet : hasSupply ? _cfLive : _cfDeadNoSupply;
            }

            string VerdictName(byte v) => v == _cfLive ? "Live" : v == _cfDeadUnmet ? "DeadUnmet" : v == _cfDeadNoSupply ? "DeadNoSupply" : $"?{v}";
            var lockedNames = new List<string>();
            foreach (var s in _cfSegs) if (s.Locked) lockedNames.Add(s.Name);
            var victimNames = new List<string>();
            foreach (long v in _cfTickVictims)
                foreach (var s in _cfSegs)
                    if (s.RefId == v) victimNames.Add(s.Name);
            var verdictParts = new List<string>();
            foreach (var n in _cfNets) verdictParts.Add($"{n.Name}={VerdictName(n.Verdict)}");
            _log?.LogInfo(
                $"[ScenarioRunner] CHAIN t={_cfTick} phase={phase} supply={supply:0} demand={demand:0} " +
                $"victims=[{string.Join(",", victimNames)}] locked=[{string.Join(",", lockedNames)}] " +
                $"leafGrants=[{_cfL7a.Throughput:0},{_cfL7b.Throughput:0},{_cfL7c.Throughput:0}] " +
                $"verdicts=[{string.Join(" ", verdictParts)}]");
        }

        // ---- Phase assertions ----

        private static bool ChainFixture_AllNetsLive(out string detail)
        {
            var bad = new List<string>();
            foreach (var n in _cfNets)
                if (n.Verdict != _cfLive) bad.Add($"{n.Name}={n.Verdict}");
            detail = bad.Count == 0 ? "all nets Live" : "non-Live: " + string.Join(",", bad);
            return bad.Count == 0;
        }

        private static void ChainFixture_AssertPhaseA()
        {
            bool served = Math.Abs(_cfL7a.Throughput - CF_LEAF_DEMAND) < 0.5f
                          && Math.Abs(_cfL7b.Throughput - CF_LEAF_DEMAND) < 0.5f
                          && Math.Abs(_cfL7c.Throughput - CF_LEAF_DEMAND) < 0.5f;
            bool live = ChainFixture_AllNetsLive(out string liveDetail);
            ChainCheck("P3", _cfAllVictims.Count == 0 && _cfOverloadHits == 0 && served && live,
                $"full supply: victims={_cfAllVictims.Count} (expect 0), overloadHits={_cfOverloadHits} (expect 0), " +
                $"leaf grants=[{_cfL7a.Throughput:0},{_cfL7b.Throughput:0},{_cfL7c.Throughput:0}] (expect {CF_LEAF_DEMAND:0} each), {liveDetail}.");
        }

        private static void ChainFixture_AssertPhaseB()
        {
            bool anyDeadUnmet = false;
            foreach (var n in _cfNets) if (n.Verdict == _cfDeadUnmet) anyDeadUnmet = true;
            bool anyLocked = false;
            foreach (var s in _cfSegs) if (s.Locked) anyLocked = true;
            ChainCheck("P4", _cfAllVictims.Count == 0 && _cfOverloadHits == 0 && !anyDeadUnmet && !anyLocked,
                $"zero practical load: victims={_cfAllVictims.Count} (expect 0), overloadHits={_cfOverloadHits} (expect 0), " +
                $"anyDeadUnmet={anyDeadUnmet} (expect false; an unloaded chain never faults, idle trunk nets read DeadNoSupply), " +
                $"anyLockout={anyLocked} (expect false).");
        }

        private static void ChainFixture_AssertDeficitStep(string id, ChainSeg expectedVictim, string label, ChainNet[] darkNets)
        {
            // Victims recorded during this 3-tick phase window (slice of the cumulative
            // list from the phase-start index): exactly the expected leaf, once, and the
            // settled last tick is quiet (the victim is locked out, its desire excluded,
            // so the survivors fit the throttled budget with no further shedding).
            int windowCount = _cfAllVictims.Count - _cfPhaseVictimStart;
            bool exactlyExpected = windowCount == 1
                && _cfAllVictims[_cfPhaseVictimStart] == expectedVictim.RefId;
            bool settledQuiet = _cfTickVictims.Count == 0;
            bool locked = expectedVictim.Locked;
            bool darkOk = true;
            foreach (var dn in darkNets) if (dn.Verdict != _cfDeadUnmet) darkOk = false;
            bool othersServed = true;
            foreach (var s in new[] { _cfL7a, _cfL7b, _cfL7c })
            {
                if (s == expectedVictim || s.Locked) continue;
                if (Math.Abs(s.Throughput - CF_LEAF_DEMAND) > 0.5f) othersServed = false;
            }
            ChainCheck(id, exactlyExpected && settledQuiet && locked && darkOk && othersServed,
                $"{label} deficit: window victims={windowCount} (expect exactly 1: {expectedVictim.Name}, priority {expectedVictim.Priority}, " +
                $"match={exactlyExpected}), settled tick quiet={settledQuiet}, real lockout active={locked}, " +
                $"dark leaf net(s) DeadUnmet={darkOk}, surviving leaves fully served={othersServed}.");
        }

        private static void ChainFixture_AssertPhaseC3()
        {
            // The C3-specific victim: exactly L7a within this phase's window.
            int windowCount = _cfAllVictims.Count - _cfPhaseVictimStart;
            bool aSeen = windowCount == 1 && _cfAllVictims[_cfPhaseVictimStart] == _cfL7a.RefId;

            // Cumulative: no mid-chain hop ever shed (fixture walk) nor locked out (real
            // registry), across every phase so far.
            bool hopVictim = false;
            foreach (long v in _cfAllVictims)
                foreach (var s in _cfSegs)
                    if (s.RefId == v && s.IsTrunkHop) hopVictim = true;
            bool hopLocked = false;
            int tickNow = ChainFixture_RealTick();
            foreach (var s in _cfSegs)
                if (s.IsTrunkHop && _cfIsLockedOut.Invoke(null, new object[] { s.RefId, tickNow }) is bool b && b)
                    hopLocked = true;

            // With every leaf locked the trunk carries no practical load: it must not shed
            // and must not read DeadUnmet (Live at the producer, DeadNoSupply downstream).
            bool trunkUnmet = false;
            foreach (var n in _cfNets)
                if (n != _cfL7a.Out && n != _cfL7b.Out && n != _cfL7c.Out && n.Verdict == _cfDeadUnmet)
                    trunkUnmet = true;

            ChainCheck("P7", aSeen && !hopVictim && !hopLocked && !trunkUnmet && _cfTickVictims.Count == 0 && _cfOverloadHits == 0,
                $"C3 deficit + aftermath: exactly {_cfL7a.Name} shed in the window (windowVictims={windowCount}, match={aSeen}); across ALL phases no mid-chain hop was a shed victim " +
                $"(hopVictim={hopVictim}) or entered the shed registry (hopLocked={hopLocked}); with every leaf locked the " +
                $"no-practical-load trunk never reads DeadUnmet ({!trunkUnmet}) and stays shed-free (quiet={_cfTickVictims.Count == 0}); " +
                $"overloadHits={_cfOverloadHits} (expect 0).");
        }

        private static Dictionary<long, int> ChainFixture_ReadRemaining()
        {
            var result = new Dictionary<long, int>();
            int tickNow = ChainFixture_RealTick();
            if (_cfSnapshotRemaining.Invoke(null, new object[] { tickNow }) is System.Collections.IEnumerable snap)
            {
                foreach (var item in snap)
                {
                    if (item is KeyValuePair<long, int> pair)
                        result[pair.Key] = pair.Value;
                }
            }
            return result;
        }

        private static void ChainFixture_AssertCountdown()
        {
            // t=16 seeded _cfRemainingPrev; here at t=17 every locked leaf's remaining
            // ticks must have decreased (the real ElectricityTickCounter advanced).
            var now = ChainFixture_ReadRemaining();
            if (_cfRemainingPrev.Count == 0)
            {
                // Seed missed (first recovery tick did not run); seed now and fail loudly.
                ChainCheck("P8", false, "countdown baseline missing at t=17 (recovery tick t=16 did not sample).");
                return;
            }
            bool allPresent = true, allDecreasing = true;
            var parts = new List<string>();
            foreach (var leaf in new[] { _cfL7a, _cfL7b, _cfL7c })
            {
                bool hadPrev = _cfRemainingPrev.TryGetValue(leaf.RefId, out int r1);
                bool hasNow = now.TryGetValue(leaf.RefId, out int r2);
                if (!hadPrev || !hasNow) allPresent = false;
                else if (r2 >= r1) allDecreasing = false;
                parts.Add($"{leaf.Name}:{(hadPrev ? r1.ToString() : "-")}->{(hasNow ? r2.ToString() : "-")}");
            }
            ChainCheck("P8", allPresent && allDecreasing,
                $"recovery under lockout: SnapshotRemaining countdown decreases per real tick for every locked leaf ({string.Join(" ", parts)}).");
        }

        private static void ChainFixture_AssertExpiryBoundary()
        {
            // Probe each leaf's expiry with a FUTURE tick argument: locked at (until - 1),
            // released AT until. The released probe also self-cleans the registry entry,
            // which is exactly the state t=19 needs (un-shed without waiting 60 seconds).
            var remaining = ChainFixture_ReadRemaining();
            int tickNow = ChainFixture_RealTick();
            bool ok = true;
            var parts = new List<string>();
            foreach (var leaf in new[] { _cfL7a, _cfL7b, _cfL7c })
            {
                if (!remaining.TryGetValue(leaf.RefId, out int rem) || rem <= 0)
                {
                    ok = false;
                    parts.Add($"{leaf.Name}:noEntry");
                    continue;
                }
                int until = tickNow + rem;
                bool lockedBefore = _cfIsLockedOut.Invoke(null, new object[] { leaf.RefId, until - 1 }) is bool b1 && b1;
                bool lockedAt = _cfIsLockedOut.Invoke(null, new object[] { leaf.RefId, until }) is bool b2 && b2;
                if (!lockedBefore || lockedAt) ok = false;
                parts.Add($"{leaf.Name}:rem={rem} until-1={lockedBefore} until={lockedAt}");
            }
            ChainCheck("P9", ok,
                $"expiry boundary: every leaf holds at (until - 1) and releases at until, per the 60 second lockout semantics ({string.Join(" ", parts)}).");
        }

        private static void ChainFixture_AssertRecoveryAndFinish()
        {
            bool served = Math.Abs(_cfL7a.Throughput - CF_LEAF_DEMAND) < 0.5f
                          && Math.Abs(_cfL7b.Throughput - CF_LEAF_DEMAND) < 0.5f
                          && Math.Abs(_cfL7c.Throughput - CF_LEAF_DEMAND) < 0.5f;
            bool live = ChainFixture_AllNetsLive(out string liveDetail);
            ChainCheck("P10", served && live && _cfTickVictims.Count == 0,
                $"post-expiry re-engage: leaf grants=[{_cfL7a.Throughput:0},{_cfL7b.Throughput:0},{_cfL7c.Throughput:0}] " +
                $"(expect {CF_LEAF_DEMAND:0} each), no victims (quiet={_cfTickVictims.Count == 0}), {liveDetail}.");
            ChainFixture_Finish();
        }

        private static void ChainFixture_Finish()
        {
            // Leave the registry as found: drop any fixture lockout still present.
            try
            {
                if (_cfClearLockout != null)
                    foreach (var s in _cfSegs)
                        _cfClearLockout.Invoke(null, new object[] { s.RefId });
            }
            catch (Exception e)
            {
                _log?.LogWarning($"[ScenarioRunner] CHAIN cleanup threw: {e.GetBaseException().Message}");
            }
            _cfDone = true;
            _log?.LogInfo($"[ScenarioRunner] CHAIN SUMMARY pass={_cfPass} fail={_cfFail}");
        }
    }
}
