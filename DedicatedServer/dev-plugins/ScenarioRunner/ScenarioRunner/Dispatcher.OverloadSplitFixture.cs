using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;

namespace ScenarioRunner
{
    // Scenario: pgp-overload-split-fixture
    //
    // One-shot synthetic proof of the OVERLOADED fault split: the single overload fault
    // is now two independently observable kinds. "Device overloaded" is the capacity
    // family (POWER.md 8.4 hit-max: the output net's rigid desire exceeds the suppliers'
    // combined Setting-derived caps; OverloadRegistry; IC10 DeviceOverloadedFault = 6580).
    // "Cable overloaded" is the cable-overflow family (POWER.md 5.7 rule 3: the flow
    // exceeds the net's weakest cable rating while the suppliers could otherwise fund it;
    // CableOverloadRegistry; IC10 CableOverloadedFault = 6587). Both registries carry a
    // per-entry payload pair (valueW/flowW, capW) that rides SnapshotRemaining, the join
    // suffix, and the hover diagnostics line. Fault precedence on one device:
    // CYCLE > CURRENT-MISMATCH > CABLE-OVERLOADED > DEVICE-OVERLOADED > DEPRIORITIZED.
    //
    // Like pgp-chain-fixture, the fixture cannot spawn Things, so the nets are synthetic,
    // but every decision seam is the mod's own code via reflection:
    //   - Capacity detection: PowerAllocator.DetectStructuralOverload (rule 1) against
    //     reflection-built PowerAllocator+Net / +Seg instances.
    //   - Cable detection: PowerAllocator.DetectSupplyOverload (rule 3) against the same
    //     mirror surface plus a PowerAllocator+Elastic.
    //   - Commit routing: the fixture mirrors the RunAtomic commit tail (CableOverloaded
    //     kind bit routes NoteCableOverload, else NoteOverload, payload from the detection
    //     site); the full tail is not invokable synthetically because it needs live
    //     CableNetwork instances for the split-pending gate.
    //   - Registries: the real OverloadRegistry / CableOverloadRegistry (4-arg notes,
    //     IsLockedOut, TryGetFault payload reads, SnapshotRemaining wire shape,
    //     ClearLockout cleanup), keyed by the real ElectricityTickCounter.CurrentTick.
    //   - Publish surface: FaultHover.ActiveFault / TryGetMergedBlock (the same
    //     resolution the hover surfaces and the flash colour use), plus the real
    //     Transformer.GetLogicValue path for the IC10 CableOverloadedFault slot when a
    //     live transformer is available.
    //
    // Phases (all within one tick; the fixture is one-shot):
    //   P1  seam check: both registries + their new members resolve; the allocator
    //       detection seams and mirror types resolve; LogicTypeRegistry.CableOverloadedFault
    //       reads 6587.
    //   P2  Device Overloaded: a synthetic net whose rigid desire (3000 W) exceeds
    //       two suppliers' combined Setting-derived caps (1000 + 800 W) while the cable
    //       cap is ample; both suppliers land in OverloadRegistry (not the cable
    //       registry) with payload (3000, 1800) within epsilon.
    //   P3  Cable overloaded: a synthetic net whose suppliers could fund the demand
    //       (Setting ample) but the flow (5000 W) exceeds WeakestCap (2000 W); the
    //       supplier and the elastic land in CableOverloadRegistry with payload
    //       (5000, 2000); the IC10 CableOverloadedFault slot reads 1 on a live
    //       transformer via the real GetLogicValue path (registry-state fallback when
    //       the save has no clean transformer), and the capacity DeviceOverloadedFault
    //       slot stays 0 for it.
    //   P4  precedence: one refId noted in BOTH registries resolves to CableOverload on
    //       FaultHover.ActiveFault, the merged hover block for the winning kind is the
    //       locked template (red "Cable overloaded fault: <s>s" title, no parentheses,
    //       plus the "Pushing X onto a Y wire" diagnostics), the capacity kind renders
    //       the "Device overloaded fault" title with the "Drawing X of Y" diagnostics,
    //       and dropping the cable entry falls back to the capacity kind.
    //   P5  payload wire shape: SnapshotRemaining on both registries yields 4-field
    //       (refId, remainingTicks, valueW, capW) entries with the constructed payloads.
    //   P6  LIVE SURVEY (after synthetic cleanup): both registries' remaining entries
    //       with refId, prefab name (resolved via one OcclusionManager.AllThings walk),
    //       secondsLeft, and the payload formatted like the hover ("drawing X of Y" /
    //       "pushing X onto a Y wire"). This is how an operator confirms a live base's
    //       faulted pair now reads Cable overloaded fault with real numbers before
    //       logging in.
    //
    // Emits per-assertion "OSF P<n> PASS/FAIL" lines and a final
    // "[ScenarioRunner] OSF SUMMARY pass=N fail=M".
    //
    // Synthetic ReferenceIds live in a 99000003xx band (pgp-chain-fixture owns the
    // 99000001xx/99000002xx bands); the only shared mutable state touched is the two
    // overload registries (fixture refs plus one transiently noted live transformer,
    // all cleared on every exit path, including the throw path). Managed-state
    // reflection only; worker-safe; needs no save (P6 is simply empty on a fresh -New).
    internal static partial class Dispatcher
    {
        private static bool _osfFired;
        private static int _osfPass;
        private static int _osfFail;

        // Synthetic ids (99000003xx band).
        private const long OSF_NET_CAPACITY = 9900000301L;
        private const long OSF_NET_CABLE = 9900000302L;
        private const long OSF_SEG_A = 9900000311L;
        private const long OSF_SEG_B = 9900000312L;
        private const long OSF_SEG_CABLE = 9900000313L;
        private const long OSF_ELASTIC_CABLE = 9900000314L;
        private const long OSF_PRECEDENCE_REF = 9900000315L;
        private const long OSF_IC10_FALLBACK = 9900000316L;

        // Constructed numbers, asserted back within OSF_EPS.
        private const float OSF_EPS = 0.5f;
        private const float OSF_CAP_DEMAND = 3000f;    // rigid desire on the capacity net
        private const float OSF_CAP_EFF_A = 1000f;     // supplier A Setting-derived cap
        private const float OSF_CAP_EFF_B = 800f;      // supplier B Setting-derived cap
        private const float OSF_CAP_COMBINED = OSF_CAP_EFF_A + OSF_CAP_EFF_B;
        private const float OSF_AMPLE_CABLE = 100000f; // ample cable cap (rule 3 must stay silent)
        private const float OSF_CABLE_FLOW = 5000f;    // (RigidDemand - Unmet) + PullsGranted on the cable net
        private const float OSF_CABLE_CAP = 2000f;     // WeakestCap (weakest cable rating)
        private const float OSF_AMPLE_SETTING = 100000f; // Setting ample: capacity rule must not own the trip

        // Reflection seams (resolved in P1).
        private static Type _osfOverT, _osfCableT, _osfNetT, _osfSegT, _osfElasticT;
        private static MethodInfo _osfNoteOver, _osfNoteCable;
        private static MethodInfo _osfOverLocked, _osfCableLocked;       // IsLockedOut(long, int)
        private static MethodInfo _osfOverIsOver, _osfCableIsOver;       // peer-aware readers
        private static MethodInfo _osfOverTryGet, _osfCableTryGet;       // TryGetFault(long, int, out, out, out)
        private static MethodInfo _osfOverSnap, _osfCableSnap;           // SnapshotRemaining(int)
        private static MethodInfo _osfOverCurr, _osfCableCurr;           // CurrentlyLockedOut(int)
        private static MethodInfo _osfOverClear, _osfCableClear;         // ClearLockout(long)
        private static MethodInfo _osfDetectStructural, _osfDetectSupply;
        private static MethodInfo _osfActiveFault, _osfTryGetMerged;
        private static PropertyInfo _osfCurrentTick;

        // Every refId the fixture noted into either registry, cleared on every exit path.
        private static readonly List<long> _osfNotedRefs = new List<long>();

        private static void OsfCheck(string id, bool ok, string detail)
        {
            if (ok) { _osfPass++; _log?.LogInfo($"[ScenarioRunner] OSF {id} PASS: {detail}"); }
            else { _osfFail++; _log?.LogError($"[ScenarioRunner] OSF {id} FAIL: {detail}"); }
        }

        private static void Scenario_PgpOverloadSplitFixture()
        {
            if (_osfFired) return;
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-overload-split-fixture")) return;
            _osfFired = true;
            _osfPass = 0;
            _osfFail = 0;
            _osfNotedRefs.Clear();

            try
            {
                _log?.LogInfo("[ScenarioRunner] OSF START overload-split-fixture");
                if (!OverloadSplitFixture_Setup())
                {
                    OverloadSplitFixture_Cleanup();
                    _log?.LogInfo($"[ScenarioRunner] OSF SUMMARY pass={_osfPass} fail={_osfFail}");
                    return;
                }

                int tick = OsfTick();
                OverloadSplitFixture_P2_TransformerOverloaded(tick);
                OverloadSplitFixture_P3_CableOverloaded(tick);
                OverloadSplitFixture_P4_Precedence(tick);
                OverloadSplitFixture_P5_WireShape(tick);

                // Drop every fixture entry BEFORE the live survey so the operator census
                // shows only real faults.
                OverloadSplitFixture_Cleanup();
                OverloadSplitFixture_P6_LiveSurvey(OsfTick());
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] OSF threw: {e}");
                _osfFail++;
                OverloadSplitFixture_Cleanup();
            }
            _log?.LogInfo($"[ScenarioRunner] OSF SUMMARY pass={_osfPass} fail={_osfFail}");
        }

        // ---- P1: seams ----

        private static bool OverloadSplitFixture_Setup()
        {
            var asm = GetModAssembly(PGP_ASSEMBLY);
            const BindingFlags SF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
            Type[] sigNote = { typeof(long), typeof(int), typeof(float), typeof(float) };
            Type[] sigLongInt = { typeof(long), typeof(int) };
            Type[] sigLong = { typeof(long) };

            _osfOverT = asm?.GetType("PowerGridPlus.OverloadRegistry");
            _osfCableT = asm?.GetType("PowerGridPlus.CableOverloadRegistry");
            var allocT = asm?.GetType("PowerGridPlus.PowerAllocator");
            _osfNetT = asm?.GetType("PowerGridPlus.PowerAllocator+Net");
            _osfSegT = asm?.GetType("PowerGridPlus.PowerAllocator+Seg");
            _osfElasticT = asm?.GetType("PowerGridPlus.PowerAllocator+Elastic");
            var hoverT = asm?.GetType("PowerGridPlus.FaultHover");
            var tickT = asm?.GetType("PowerGridPlus.ElectricityTickCounter");

            _osfNoteOver = _osfOverT?.GetMethod("NoteOverload", SF, null, sigNote, null);
            _osfNoteCable = _osfCableT?.GetMethod("NoteCableOverload", SF, null, sigNote, null);
            _osfOverLocked = _osfOverT?.GetMethod("IsLockedOut", SF, null, sigLongInt, null);
            _osfCableLocked = _osfCableT?.GetMethod("IsLockedOut", SF, null, sigLongInt, null);
            _osfOverIsOver = _osfOverT?.GetMethod("IsOverloaded", SF, null, sigLongInt, null);
            _osfCableIsOver = _osfCableT?.GetMethod("IsCableOverloaded", SF, null, sigLongInt, null);
            _osfOverTryGet = _osfOverT?.GetMethod("TryGetFault", SF);
            _osfCableTryGet = _osfCableT?.GetMethod("TryGetFault", SF);
            _osfOverSnap = _osfOverT?.GetMethod("SnapshotRemaining", SF);
            _osfCableSnap = _osfCableT?.GetMethod("SnapshotRemaining", SF);
            _osfOverCurr = _osfOverT?.GetMethod("CurrentlyLockedOut", SF);
            _osfCableCurr = _osfCableT?.GetMethod("CurrentlyLockedOut", SF);
            _osfOverClear = _osfOverT?.GetMethod("ClearLockout", SF, null, sigLong, null);
            _osfCableClear = _osfCableT?.GetMethod("ClearLockout", SF, null, sigLong, null);
            _osfDetectStructural = allocT?.GetMethod("DetectStructuralOverload", SF);
            _osfDetectSupply = allocT?.GetMethod("DetectSupplyOverload", SF);
            _osfActiveFault = hoverT?.GetMethod("ActiveFault", SF);
            _osfTryGetMerged = hoverT?.GetMethod("TryGetMergedBlock", SF);
            _osfCurrentTick = tickT?.GetProperty("CurrentTick", SF);

            // The IC10 constant: LogicTypeRegistry.CableOverloaded must resolve and read 6587
            // (the centralised Patterns/Logic assignment).
            ushort cableLogicValue = (ushort)PgpLogic(asm, "CableOverloadedFault");
            bool logicOk = cableLogicValue == 6587;

            bool registriesOk = _osfNoteOver != null && _osfNoteCable != null
                && _osfOverLocked != null && _osfCableLocked != null
                && _osfOverIsOver != null && _osfCableIsOver != null
                && _osfOverTryGet != null && _osfCableTryGet != null
                && _osfOverSnap != null && _osfCableSnap != null
                && _osfOverCurr != null && _osfCableCurr != null
                && _osfOverClear != null && _osfCableClear != null;
            bool allocOk = _osfDetectStructural != null && _osfDetectSupply != null
                && _osfNetT != null && _osfSegT != null && _osfElasticT != null;
            bool hoverOk = _osfActiveFault != null && _osfTryGetMerged != null;
            bool tickOk = _osfCurrentTick != null;

            OsfCheck("P1", registriesOk && allocOk && hoverOk && tickOk && logicOk,
                $"seams: OverloadRegistry(NoteOverload4/IsLockedOut/IsOverloaded/TryGetFault/Snapshot/Currently/Clear)={registriesOk && _osfOverT != null} " +
                $"CableOverloadRegistry(NoteCableOverload4/IsLockedOut/IsCableOverloaded/TryGetFault/Snapshot/Currently/Clear)={_osfCableT != null && _osfNoteCable != null} " +
                $"allocator(DetectStructuralOverload={_osfDetectStructural != null} DetectSupplyOverload={_osfDetectSupply != null} Net/Seg/Elastic mirrors={_osfNetT != null && _osfSegT != null && _osfElasticT != null}) " +
                $"FaultHover(ActiveFault={_osfActiveFault != null} TryGetMergedBlock={_osfTryGetMerged != null}) CurrentTick={tickOk} " +
                $"LogicTypeRegistry.CableOverloadedFault={cableLogicValue} (expect 6587)");

            if (!(registriesOk && allocOk && tickOk))
            {
                _log?.LogError("[ScenarioRunner] OSF cannot run without the registry + allocator + tick seams; aborting.");
                return false;
            }
            return true;
        }

        // ---- mirror-object helpers (throw loudly on a renamed field; the outer catch
        //      logs, counts a FAIL, and still cleans the registries) ----

        private static object OsfNew(Type t) => Activator.CreateInstance(t, nonPublic: true);

        private static void OsfSet(object o, string field, object value)
        {
            var f = o.GetType().GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) throw new InvalidOperationException($"field '{field}' missing on {o.GetType().FullName}");
            f.SetValue(o, value);
        }

        private static object OsfGet(object o, string field)
        {
            var f = o.GetType().GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) throw new InvalidOperationException($"field '{field}' missing on {o.GetType().FullName}");
            return f.GetValue(o);
        }

        private static void OsfAddTo(object o, string listField, object item)
            => ((IList)OsfGet(o, listField)).Add(item);

        private static IList OsfTypedList(Type elementType)
            => (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType));

        private static int OsfTick() => _osfCurrentTick?.GetValue(null) is int i ? i : 0;

        private static object OsfNewNet(long id, float rigidDemand, float genSupply, float weakestCap)
        {
            object net = OsfNew(_osfNetT);
            OsfSet(net, "Id", id);
            OsfSet(net, "RigidDemand", rigidDemand);
            OsfSet(net, "GenSupply", genSupply);
            OsfSet(net, "WeakestCap", weakestCap);
            return net;
        }

        private static object OsfNewSeg(long refId, float capSetting, float cableCap, float effCap)
        {
            object seg = OsfNew(_osfSegT);
            OsfSet(seg, "RefId", refId);
            OsfSet(seg, "CapSetting", capSetting);
            OsfSet(seg, "CableCap", cableCap);
            OsfSet(seg, "EffCap", effCap);
            return seg;
        }

        // ---- registry wrappers ----

        private static void OsfNote(bool cable, long refId, int tick, float valueW, float capW)
        {
            (cable ? _osfNoteCable : _osfNoteOver).Invoke(null, new object[] { refId, tick, valueW, capW });
            if (!_osfNotedRefs.Contains(refId)) _osfNotedRefs.Add(refId);
        }

        private static bool OsfLocked(bool cable, long refId, int tick)
            => (cable ? _osfCableLocked : _osfOverLocked).Invoke(null, new object[] { refId, tick }) is bool b && b;

        private static bool OsfTryGetFault(bool cable, long refId, int tick,
            out float seconds, out float valueW, out float capW)
        {
            var args = new object[] { refId, tick, 0f, 0f, 0f };
            bool ok = (cable ? _osfCableTryGet : _osfOverTryGet).Invoke(null, args) is bool b && b;
            seconds = (float)args[2];
            valueW = (float)args[3];
            capW = (float)args[4];
            return ok;
        }

        private static void OsfClear(long refId)
        {
            _osfOverClear?.Invoke(null, new object[] { refId });
            _osfCableClear?.Invoke(null, new object[] { refId });
        }

        private static void OverloadSplitFixture_Cleanup()
        {
            try
            {
                foreach (long r in _osfNotedRefs) OsfClear(r);
                _osfNotedRefs.Clear();
            }
            catch (Exception e)
            {
                _log?.LogWarning($"[ScenarioRunner] OSF cleanup threw: {e.GetBaseException().Message}");
            }
        }

        // Mirrors the RunAtomic commit tail's kind routing for one detected mirror object
        // (Seg or Elastic): the CableOverloaded bit routes the entry to
        // CableOverloadRegistry, else OverloadRegistry, payload from the detection site.
        private static void OsfCommit(object segOrElastic, int tick)
        {
            if (!((bool)OsfGet(segOrElastic, "Overloaded"))) return;
            long refId = (long)OsfGet(segOrElastic, "RefId");
            bool cable = (bool)OsfGet(segOrElastic, "CableOverloaded");
            float valueW = (float)OsfGet(segOrElastic, "OverloadValueW");
            float capW = (float)OsfGet(segOrElastic, "OverloadCapW");
            OsfNote(cable, refId, tick, valueW, capW);
        }

        // ---- P2: capacity family (Device overloaded) ----

        private static void OverloadSplitFixture_P2_TransformerOverloaded(int tick)
        {
            // Rigid desire 3000 W against two Setting-limited suppliers (1000 + 800 W)
            // with ample cable caps: rule 1 territory, rule 3 must stay silent.
            var netList = OsfTypedList(_osfNetT);
            var segList = OsfTypedList(_osfSegT);
            object net = OsfNewNet(OSF_NET_CAPACITY, OSF_CAP_DEMAND, 0f, OSF_AMPLE_CABLE);
            object segA = OsfNewSeg(OSF_SEG_A, OSF_CAP_EFF_A, OSF_AMPLE_CABLE, OSF_CAP_EFF_A);
            object segB = OsfNewSeg(OSF_SEG_B, OSF_CAP_EFF_B, OSF_AMPLE_CABLE, OSF_CAP_EFF_B);
            OsfAddTo(net, "Suppliers", segA);
            OsfAddTo(net, "Suppliers", segB);
            netList.Add(net);
            segList.Add(segA);
            segList.Add(segB);

            _osfDetectStructural.Invoke(null, new object[] { netList, segList });

            bool aOver = (bool)OsfGet(segA, "Overloaded"), aCable = (bool)OsfGet(segA, "CableOverloaded");
            bool bOver = (bool)OsfGet(segB, "Overloaded"), bCable = (bool)OsfGet(segB, "CableOverloaded");
            float aVal = (float)OsfGet(segA, "OverloadValueW"), aCap = (float)OsfGet(segA, "OverloadCapW");
            float bVal = (float)OsfGet(segB, "OverloadValueW"), bCap = (float)OsfGet(segB, "OverloadCapW");
            bool payloadOk = Math.Abs(aVal - OSF_CAP_DEMAND) < OSF_EPS && Math.Abs(aCap - OSF_CAP_COMBINED) < OSF_EPS
                && Math.Abs(bVal - OSF_CAP_DEMAND) < OSF_EPS && Math.Abs(bCap - OSF_CAP_COMBINED) < OSF_EPS;
            OsfCheck("P2a", aOver && bOver && !aCable && !bCable && payloadOk,
                $"DetectStructuralOverload trips both Setting-limited suppliers as the CAPACITY kind: " +
                $"A(over={aOver} cableBit={aCable} payload=({aVal:0},{aCap:0})) B(over={bOver} cableBit={bCable} payload=({bVal:0},{bCap:0})) " +
                $"expected payload ({OSF_CAP_DEMAND:0},{OSF_CAP_COMBINED:0}) = (net rigid desire, combined Setting-derived cap).");

            OsfCommit(segA, tick);
            OsfCommit(segB, tick);

            bool inOver = OsfLocked(false, OSF_SEG_A, tick) && OsfLocked(false, OSF_SEG_B, tick);
            bool inCable = OsfLocked(true, OSF_SEG_A, tick) || OsfLocked(true, OSF_SEG_B, tick);
            bool gotFault = OsfTryGetFault(false, OSF_SEG_A, tick, out float secA, out float valA, out float capA);
            bool tryOk = gotFault && secA > 0f
                && Math.Abs(valA - OSF_CAP_DEMAND) < OSF_EPS && Math.Abs(capA - OSF_CAP_COMBINED) < OSF_EPS;
            OsfCheck("P2b", inOver && !inCable && tryOk,
                $"commit lands in OverloadRegistry only (over={inOver} cable={inCable}); " +
                $"TryGetFault(A) -> found={gotFault} secondsLeft={secA:0.00} payload=({valA:0},{capA:0}) " +
                $"expected ({OSF_CAP_DEMAND:0},{OSF_CAP_COMBINED:0}).");
        }

        // ---- P3: cable family (Cable Overloaded) ----

        private static void OverloadSplitFixture_P3_CableOverloaded(int tick)
        {
            // Suppliers could fund the demand (Setting ample) but the flow exceeds the
            // weakest cable rating: rule 3 territory. Unmet stays 0 so the rule 2
            // (elastic hit-max, capacity family) loop cannot own the elastic first.
            var netList = OsfTypedList(_osfNetT);
            var elasticList = OsfTypedList(_osfElasticT);
            object net = OsfNewNet(OSF_NET_CABLE, OSF_CABLE_FLOW, 0f, OSF_CABLE_CAP);
            OsfSet(net, "Unmet", 0f);
            OsfSet(net, "PullsGranted", 0f);
            object seg = OsfNewSeg(OSF_SEG_CABLE, OSF_AMPLE_SETTING, OSF_CABLE_CAP, OSF_CABLE_CAP);
            object elastic = OsfNew(_osfElasticT);
            OsfSet(elastic, "RefId", OSF_ELASTIC_CABLE);
            OsfSet(elastic, "EffDischarge", 3000f);
            OsfAddTo(net, "Suppliers", seg);
            OsfAddTo(net, "Elastics", elastic);
            netList.Add(net);
            elasticList.Add(elastic);

            _osfDetectSupply.Invoke(null, new object[] { netList, elasticList });

            bool sOver = (bool)OsfGet(seg, "Overloaded"), sCable = (bool)OsfGet(seg, "CableOverloaded");
            float sVal = (float)OsfGet(seg, "OverloadValueW"), sCap = (float)OsfGet(seg, "OverloadCapW");
            bool eOver = (bool)OsfGet(elastic, "Overloaded"), eCable = (bool)OsfGet(elastic, "CableOverloaded");
            float eVal = (float)OsfGet(elastic, "OverloadValueW"), eCap = (float)OsfGet(elastic, "OverloadCapW");
            bool payloadOk = Math.Abs(sVal - OSF_CABLE_FLOW) < OSF_EPS && Math.Abs(sCap - OSF_CABLE_CAP) < OSF_EPS
                && Math.Abs(eVal - OSF_CABLE_FLOW) < OSF_EPS && Math.Abs(eCap - OSF_CABLE_CAP) < OSF_EPS;
            OsfCheck("P3a", sOver && sCable && eOver && eCable && payloadOk,
                $"DetectSupplyOverload rule 3 trips the supplier AND the elastic as the CABLE kind: " +
                $"seg(over={sOver} cableBit={sCable} payload=({sVal:0},{sCap:0})) elastic(over={eOver} cableBit={eCable} payload=({eVal:0},{eCap:0})) " +
                $"expected payload ({OSF_CABLE_FLOW:0},{OSF_CABLE_CAP:0}) = (flow, weakest cable cap).");

            OsfCommit(seg, tick);
            OsfCommit(elastic, tick);

            bool inCable = OsfLocked(true, OSF_SEG_CABLE, tick) && OsfLocked(true, OSF_ELASTIC_CABLE, tick);
            bool inOver = OsfLocked(false, OSF_SEG_CABLE, tick) || OsfLocked(false, OSF_ELASTIC_CABLE, tick);
            bool gotFault = OsfTryGetFault(true, OSF_SEG_CABLE, tick, out float sec, out float flow, out float cap);
            bool tryOk = gotFault && sec > 0f
                && Math.Abs(flow - OSF_CABLE_FLOW) < OSF_EPS && Math.Abs(cap - OSF_CABLE_CAP) < OSF_EPS;
            OsfCheck("P3b", inCable && !inOver && tryOk,
                $"commit lands in CableOverloadRegistry only (cable={inCable} over={inOver}); " +
                $"TryGetFault(seg) -> found={gotFault} secondsLeft={sec:0.00} payload=({flow:0},{cap:0}) " +
                $"expected ({OSF_CABLE_FLOW:0},{OSF_CABLE_CAP:0}).");

            OverloadSplitFixture_P3c_Ic10Read(tick);
        }

        // IC10-style read of the new CableOverloaded slot via the real GetLogicValue path
        // when a live, currently fault-free transformer exists; else via registry state.
        private static void OverloadSplitFixture_P3c_Ic10Read(int tick)
        {
            var asm = GetModAssembly(PGP_ASSEMBLY);
            var cableLogic = PgpLogic(asm, "CableOverloadedFault");
            var overLogic = PgpLogic(asm, "DeviceOverloadedFault");

            if (_transformers.Count == 0) RebuildCaches();
            Transformer live = null;
            foreach (var t in _transformers)
            {
                if (t == null) continue;
                if (OsfLocked(false, t.ReferenceId, tick) || OsfLocked(true, t.ReferenceId, tick)) continue;
                live = t;
                break;
            }

            if (live != null && (ushort)cableLogic != 0)
            {
                OsfNote(true, live.ReferenceId, tick, OSF_CABLE_FLOW, OSF_CABLE_CAP);
                double vCable = Lv(live, cableLogic);
                double vOver = Lv(live, overLogic);
                OsfClear(live.ReferenceId);
                double vAfter = Lv(live, cableLogic);
                OsfCheck("P3c", Math.Abs(vCable - 1.0) < 0.001 && Math.Abs(vOver) < 0.001 && Math.Abs(vAfter) < 0.001,
                    $"live transformer ref={live.ReferenceId} GetLogicValue while cable-locked: CableOverloaded={vCable} (expect 1), " +
                    $"Overloaded={vOver} (expect 0, the split keeps the slots independent); after ClearLockout CableOverloaded={vAfter} (expect 0).");
            }
            else
            {
                // Registry-state fallback (no clean live transformer in this save).
                OsfNote(true, OSF_IC10_FALLBACK, tick, OSF_CABLE_FLOW, OSF_CABLE_CAP);
                bool on = _osfCableIsOver.Invoke(null, new object[] { OSF_IC10_FALLBACK, tick }) is bool b1 && b1;
                bool onOver = _osfOverIsOver.Invoke(null, new object[] { OSF_IC10_FALLBACK, tick }) is bool b2 && b2;
                OsfClear(OSF_IC10_FALLBACK);
                bool off = _osfCableIsOver.Invoke(null, new object[] { OSF_IC10_FALLBACK, tick }) is bool b3 && b3;
                OsfCheck("P3c", on && !onOver && !off,
                    $"no live transformer available; registry-state fallback: IsCableOverloaded={on} (expect true), " +
                    $"IsOverloaded={onOver} (expect false), after ClearLockout IsCableOverloaded={off} (expect false).");
            }
        }

        // ---- P4: precedence on the publish surface ----

        private static void OverloadSplitFixture_P4_Precedence(int tick)
        {
            if (_osfActiveFault == null)
            {
                OsfCheck("P4", false, "FaultHover.ActiveFault seam unresolved (see P1); precedence not verifiable.");
                return;
            }

            // One refId qualifying for BOTH overload kinds must resolve to the
            // higher-precedence Cable Overloaded on the publish surface.
            OsfNote(false, OSF_PRECEDENCE_REF, tick, OSF_CAP_DEMAND, OSF_CAP_COMBINED);
            OsfNote(true, OSF_PRECEDENCE_REF, tick, OSF_CABLE_FLOW, OSF_CABLE_CAP);
            object kindBoth = _osfActiveFault.Invoke(null, new object[] { OSF_PRECEDENCE_REF, tick });
            OsfCheck("P4a", kindBoth?.ToString() == "CableOverload",
                $"ActiveFault with both overload kinds active resolves to '{kindBoth}' " +
                "(expect CableOverload; precedence CYCLE > CURRENT-MISMATCH > CABLE-OVERLOADED > DEVICE-OVERLOADED > DEPRIORITIZED).");

            if (_osfTryGetMerged != null)
            {
                // TryGetMergedBlock(long, int, Thing, out string, out Kind): the single hover
                // renderer under the locked template (every fault and both info states render
                // through it; the callers only add alignment/size tags). A null Thing is safe and
                // just omits the "On - " switch prefix, so the assertions pin the fault wording
                // itself: the red "Cable overloaded fault: {countdown}s" title line (sentence
                // case, no parentheses) over the capitalised "Pushing X onto a Y wire"
                // diagnostics.
                var args = new object[] { OSF_PRECEDENCE_REF, tick, null, null, null };
                bool got = _osfTryGetMerged.Invoke(null, args) is bool b && b;
                string line = args[3] as string ?? "";
                string outKind = args[4]?.ToString() ?? "<null>";
                OsfCheck("P4b", got && outKind == "CableOverload"
                        && line.Contains("<color=#ff2626>Cable overloaded fault: ")
                        && line.Contains("Pushing ") && line.Contains(" onto a ")
                        && !line.Contains("(") && !line.Contains("Cable Overloaded"),
                    $"merged hover block for the winning kind is the red 'Cable overloaded fault:' title plus " +
                    $"the Pushing diagnostics, parentheses gone (got={got} kind={outKind} line='{Truncate(line, 200)}').");

                // P4b2: the capacity kind's block with a null Thing falls back to the generic
                // "Device overloaded fault" title and carries the locked "Drawing X of Y"
                // diagnostics (live per-device labels are covered by PTHOVER P4).
                _osfCableClear.Invoke(null, new object[] { OSF_PRECEDENCE_REF });
                var argsOver = new object[] { OSF_PRECEDENCE_REF, tick, null, null, null };
                bool gotOver = _osfTryGetMerged.Invoke(null, argsOver) is bool b2 && b2;
                string lineOver = argsOver[3] as string ?? "";
                string outKindOver = argsOver[4]?.ToString() ?? "<null>";
                OsfCheck("P4b2", gotOver && outKindOver == "DeviceOverload"
                        && lineOver.Contains("<color=#ff2626>Device overloaded fault: ")
                        && lineOver.Contains("Drawing ") && lineOver.Contains(" of ")
                        && !lineOver.Contains("("),
                    $"capacity kind renders the 'Device overloaded fault:' title plus the Drawing diagnostics " +
                    $"(got={gotOver} kind={outKindOver} line='{Truncate(lineOver, 200)}').");
            }
            else
            {
                OsfCheck("P4b", false, "FaultHover.TryGetMergedBlock seam unresolved (see P1).");
                _osfCableClear.Invoke(null, new object[] { OSF_PRECEDENCE_REF });
            }

            // With the cable entry gone the same refId resolves to the capacity kind.
            object kindOver2 = _osfActiveFault.Invoke(null, new object[] { OSF_PRECEDENCE_REF, tick });
            OsfCheck("P4c", kindOver2?.ToString() == "DeviceOverload",
                $"after clearing the cable entry the same refId resolves to '{kindOver2}' (expect DeviceOverload, the capacity kind).");
            OsfClear(OSF_PRECEDENCE_REF);
        }

        // ---- P5: SnapshotRemaining wire shape ----

        private static void OverloadSplitFixture_P5_WireShape(int tick)
        {
            int lockoutTicks = 120;
            var ld = _osfOverT.GetField("LockoutDurationTicks", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            if (ld?.GetValue(null) is int i && i > 0) lockoutTicks = i;

            OsfCheckSnapshotShape("P5a", _osfOverSnap, _osfOverCurr, OSF_SEG_A, tick, lockoutTicks,
                OSF_CAP_DEMAND, OSF_CAP_COMBINED, "transformer-overloaded");
            OsfCheckSnapshotShape("P5b", _osfCableSnap, _osfCableCurr, OSF_SEG_CABLE, tick, lockoutTicks,
                OSF_CABLE_FLOW, OSF_CABLE_CAP, "cable-overloaded");
        }

        private static void OsfCheckSnapshotShape(string id, MethodInfo snap, MethodInfo curr, long wantRef,
            int tick, int lockoutTicks, float expValue, float expCap, string label)
        {
            bool found = false;
            bool fourFields = false;
            int remaining = -1;
            float gotV = float.NaN, gotC = float.NaN;
            if (snap.Invoke(null, new object[] { tick }) is IEnumerable en)
            {
                foreach (var item in en)
                {
                    var it = item.GetType();
                    var f1 = it.GetField("Item1");
                    var f2 = it.GetField("Item2");
                    var f3 = it.GetField("Item3");
                    var f4 = it.GetField("Item4");
                    fourFields = f1 != null && f2 != null && f3 != null && f4 != null && it.GetFields().Length == 4;
                    if (!fourFields) break;
                    if (Convert.ToInt64(f1.GetValue(item)) != wantRef) continue;
                    found = true;
                    remaining = Convert.ToInt32(f2.GetValue(item));
                    gotV = Convert.ToSingle(f3.GetValue(item));
                    gotC = Convert.ToSingle(f4.GetValue(item));
                    break;
                }
            }
            bool inCensus = false;
            if (curr?.Invoke(null, new object[] { tick }) is IEnumerable ce)
                foreach (var o in ce)
                    if (o is long r && r == wantRef) { inCensus = true; break; }
            OsfCheck(id, found && fourFields && inCensus && remaining > 0 && remaining <= lockoutTicks
                    && Math.Abs(gotV - expValue) < OSF_EPS && Math.Abs(gotC - expCap) < OSF_EPS,
                $"{label} SnapshotRemaining entry shape (refId, remainingTicks, valueW, capW): found={found} fourFields={fourFields} " +
                $"CurrentlyLockedOut={inCensus} remaining={remaining} (0 < r <= {lockoutTicks}) payload=({gotV:0},{gotC:0}) expected ({expValue:0},{expCap:0}).");
        }

        // ---- P6: live survey (operator census; informational, no PASS/FAIL) ----

        private static void OverloadSplitFixture_P6_LiveSurvey(int tick)
        {
            // (cable? , refId, secondsLeft, valueW, capW)
            var entries = new List<(bool cable, long refId, float seconds, float valueW, float capW)>();
            void Gather(bool cable, MethodInfo snap)
            {
                if (!(snap.Invoke(null, new object[] { tick }) is IEnumerable en)) return;
                foreach (var item in en)
                {
                    var f1 = item.GetType().GetField("Item1");
                    if (f1 == null) continue;
                    long refId = Convert.ToInt64(f1.GetValue(item));
                    OsfTryGetFault(cable, refId, tick, out float sec, out float v, out float c);
                    entries.Add((cable, refId, sec, v, c));
                }
            }
            Gather(false, _osfOverSnap);
            Gather(true, _osfCableSnap);

            // One AllThings walk resolves every surveyed refId to its prefab name.
            var names = new Dictionary<long, string>();
            var want = new HashSet<long>();
            foreach (var e in entries) want.Add(e.refId);
            if (want.Count > 0)
            {
                OcclusionManager.AllThings.ForEach(t =>
                {
                    if (t == null || !want.Contains(t.ReferenceId)) return;
                    names[t.ReferenceId] = string.IsNullOrEmpty(t.PrefabName) ? t.GetType().Name : t.PrefabName;
                });
            }

            int overCount = 0, cableCount = 0;
            foreach (var e in entries) { if (e.cable) cableCount++; else overCount++; }
            _log?.LogInfo($"[ScenarioRunner] OSF SURVEY transformer-overloaded entries={overCount} cable-overloaded entries={cableCount} (live registry census after fixture cleanup)");

            foreach (var e in entries)
            {
                string name = names.TryGetValue(e.refId, out var n) ? n : "<not-found>";
                string secs = e.seconds.ToString("0.00", CultureInfo.InvariantCulture);
                if (e.cable)
                    _log?.LogInfo($"[ScenarioRunner] OSF SURVEY cable-overloaded ref={e.refId} prefab={name} secondsLeft={secs} pushing {OsfFmtWatts(e.valueW)} onto a {OsfFmtWatts(e.capW)} wire");
                else
                    _log?.LogInfo($"[ScenarioRunner] OSF SURVEY device-overloaded ref={e.refId} prefab={name} secondsLeft={secs} drawing {OsfFmtWatts(e.valueW)} of {OsfFmtWatts(e.capW)}");
            }
            if (entries.Count == 0)
                _log?.LogInfo("[ScenarioRunner] OSF SURVEY no live overload entries in either registry this tick.");
        }

        // Watt formatting mirroring FaultHover.FmtWatts (integer watts below 1000, one-decimal
        // kilowatts at or above), invariant culture so the log stays machine-parseable.
        private static string OsfFmtWatts(float watts)
        {
            if (watts < 1000f) return watts.ToString("0", CultureInfo.InvariantCulture) + " W";
            return (watts / 1000f).ToString("0.0", CultureInfo.InvariantCulture) + " kW";
        }
    }
}
