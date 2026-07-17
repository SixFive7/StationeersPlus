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
    // CableOverloadRegistry; IC10 CableOverloadedFault = 6587). Each entry carries a payload
    // that rides SnapshotRemaining, the join suffix, and the hover diagnostics line: the
    // capacity family a (valueW, capW, storageW) triple (storageW = the internal-storage slice
    // of capW), the cable family a (flowW, capW) pair. Fault precedence on one device:
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
    //   - Registries: the real OverloadRegistry (5-arg note: value, cap, storage) and
    //     CableOverloadRegistry (4-arg note: flow, cap), plus IsLockedOut, TryGetFault
    //     payload reads, SnapshotRemaining wire shape, and ClearLockout cleanup, keyed by
    //     the real ElectricityTickCounter.CurrentTick.
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
    //   P2c/P2d decision-33 split: rule 1a stays silent when only forwarded child claims
    //       exceed the cap (DetectResidualOverload owns that trip at settle, residual payload),
    //       and a Deprioritized supplier's EffCap stays in the capacity sum, so a same-tick
    //       shed cannot manufacture a false overload on the surviving co-supplier.
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
    //   P5  payload wire shape: SnapshotRemaining yields 5-field
    //       (refId, remainingTicks, valueW, capW, storageW) entries on OverloadRegistry and
    //       4-field (refId, remainingTicks, flowW, capW) on CableOverloadRegistry, with the
    //       constructed payloads (storageW = 0 on the synthetic capacity net).
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
        private const long OSF_NET_RESIDUAL = 9900000303L;
        private const long OSF_NET_CAPFIX = 9900000304L;
        private const long OSF_SEG_RES_A = 9900000321L;
        private const long OSF_SEG_RES_B = 9900000322L;
        private const long OSF_SEG_RES_CHILD = 9900000323L;
        private const long OSF_SEG_FIX_A = 9900000324L;
        private const long OSF_SEG_FIX_B = 9900000325L;
        private const long OSF_IC10_FALLBACK = 9900000316L;

        // Constructed numbers, asserted back within OSF_EPS.
        private const float OSF_EPS = 0.5f;
        private const float OSF_CAP_DEMAND = 3000f;    // rigid desire on the capacity net
        private const float OSF_RES_OWN = 500f;        // P2c: own rigid demand, under the cap alone
        private const float OSF_RES_CHILD = 2500f;     // P2c: forwarded child claim
        private const float OSF_RES_TOTAL = OSF_RES_OWN + OSF_RES_CHILD;   // P2c residual = 3000
        private const float OSF_FIX_DEMAND = 1700f;    // P2d: fits only when the shed supplier's cap counts
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
        private static MethodInfo _osfOverTryGet, _osfCableTryGet;       // overload: +out storageW; cable: (out seconds, flow, cap)
        private static MethodInfo _osfOverSnap, _osfCableSnap;           // SnapshotRemaining(int)
        private static MethodInfo _osfOverCurr, _osfCableCurr;           // CurrentlyLockedOut(int)
        private static MethodInfo _osfOverClear, _osfCableClear;         // ClearLockout(long)
        private static MethodInfo _osfDetectStructural, _osfDetectResidual, _osfDetectSupply;
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
                OverloadSplitFixture_P2cd_ResidualSplit(tick);
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
            // OverloadRegistry.NoteOverload widened to a (valueW, capW, storageW) triple; the
            // cable registry's NoteCableOverload stays a (flowW, capW) pair.
            Type[] sigNoteOver = { typeof(long), typeof(int), typeof(float), typeof(float), typeof(float) };
            Type[] sigNoteCable = { typeof(long), typeof(int), typeof(float), typeof(float) };
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

            _osfNoteOver = _osfOverT?.GetMethod("NoteOverload", SF, null, sigNoteOver, null);
            _osfNoteCable = _osfCableT?.GetMethod("NoteCableOverload", SF, null, sigNoteCable, null);
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
            _osfDetectResidual = allocT?.GetMethod("DetectResidualOverload", SF);
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
            bool allocOk = _osfDetectStructural != null && _osfDetectResidual != null && _osfDetectSupply != null
                && _osfNetT != null && _osfSegT != null && _osfElasticT != null;
            bool hoverOk = _osfActiveFault != null && _osfTryGetMerged != null;
            bool tickOk = _osfCurrentTick != null;

            OsfCheck("P1", registriesOk && allocOk && hoverOk && tickOk && logicOk,
                $"seams: OverloadRegistry(NoteOverload5/IsLockedOut/IsOverloaded/TryGetFault/Snapshot/Currently/Clear)={registriesOk && _osfOverT != null} " +
                $"CableOverloadRegistry(NoteCableOverload4/IsLockedOut/IsCableOverloaded/TryGetFault/Snapshot/Currently/Clear)={_osfCableT != null && _osfNoteCable != null} " +
                $"allocator(DetectStructuralOverload={_osfDetectStructural != null} DetectResidualOverload={_osfDetectResidual != null} DetectSupplyOverload={_osfDetectSupply != null} Net/Seg/Elastic mirrors={_osfNetT != null && _osfSegT != null && _osfElasticT != null}) " +
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

        // storageW is the internal-storage slice of the overload cap; the cable registry has no
        // storage split, so the cable route ignores it and takes the (flowW, capW) note.
        private static void OsfNote(bool cable, long refId, int tick, float valueW, float capW, float storageW = 0f)
        {
            if (cable)
                _osfNoteCable.Invoke(null, new object[] { refId, tick, valueW, capW });
            else
                _osfNoteOver.Invoke(null, new object[] { refId, tick, valueW, capW, storageW });
            if (!_osfNotedRefs.Contains(refId)) _osfNotedRefs.Add(refId);
        }

        private static bool OsfLocked(bool cable, long refId, int tick)
            => (cable ? _osfCableLocked : _osfOverLocked).Invoke(null, new object[] { refId, tick }) is bool b && b;

        // OverloadRegistry.TryGetFault has an extra out storageW; CableOverloadRegistry.TryGetFault
        // does not. storageW comes back 0 for the cable route.
        private static bool OsfTryGetFault(bool cable, long refId, int tick,
            out float seconds, out float valueW, out float capW, out float storageW)
        {
            if (cable)
            {
                var args = new object[] { refId, tick, 0f, 0f, 0f };
                bool okc = _osfCableTryGet.Invoke(null, args) is bool bc && bc;
                seconds = (float)args[2];
                valueW = (float)args[3];
                capW = (float)args[4];
                storageW = 0f;
                return okc;
            }
            var oargs = new object[] { refId, tick, 0f, 0f, 0f, 0f };
            bool oko = _osfOverTryGet.Invoke(null, oargs) is bool bo && bo;
            seconds = (float)oargs[2];
            valueW = (float)oargs[3];
            capW = (float)oargs[4];
            storageW = (float)oargs[5];
            return oko;
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
            // OverloadStorageW is the Seg-only storage slice written at the DetectStructuralOverload
            // site; the cable route (Seg or Elastic) has no storage split, so only read it for the
            // capacity route.
            float storageW = cable ? 0f : (float)OsfGet(segOrElastic, "OverloadStorageW");
            OsfNote(cable, refId, tick, valueW, capW, storageW);
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
            bool gotFault = OsfTryGetFault(false, OSF_SEG_A, tick, out float secA, out float valA, out float capA, out float storA);
            // The capacity net has no elastic storage, so the storage slice comes back 0
            // (the storageW>0 breakdown path is covered by pgp-fault-hover-fixture).
            bool tryOk = gotFault && secA > 0f
                && Math.Abs(valA - OSF_CAP_DEMAND) < OSF_EPS && Math.Abs(capA - OSF_CAP_COMBINED) < OSF_EPS
                && Math.Abs(storA) < OSF_EPS;
            OsfCheck("P2b", inOver && !inCable && tryOk,
                $"commit lands in OverloadRegistry only (over={inOver} cable={inCable}); " +
                $"TryGetFault(A) -> found={gotFault} secondsLeft={secA:0.00} payload=({valA:0},{capA:0} storage={storA:0}) " +
                $"expected ({OSF_CAP_DEMAND:0},{OSF_CAP_COMBINED:0} storage=0).");
        }

        // ---- P2c/P2d: the decision-33 1a/1b split and the capacity-sum fix ----

        private static void OverloadSplitFixture_P2cd_ResidualSplit(int tick)
        {
            // P2c: own rigid demand (500 W) sits under the combined cap, so the per-round rule 1a
            // stays silent; the forwarded child claim (2500 W) pushes the settle-time residual
            // (3000 W) over the cap, so DetectResidualOverload owns the trip with the residual
            // payload. Detection-level only; commit routing is P2b's job.
            var netList = OsfTypedList(_osfNetT);
            var segList = OsfTypedList(_osfSegT);
            object net = OsfNewNet(OSF_NET_RESIDUAL, OSF_RES_OWN, 0f, OSF_AMPLE_CABLE);
            object supA = OsfNewSeg(OSF_SEG_RES_A, OSF_CAP_EFF_A, OSF_AMPLE_CABLE, OSF_CAP_EFF_A);
            object supB = OsfNewSeg(OSF_SEG_RES_B, OSF_CAP_EFF_B, OSF_AMPLE_CABLE, OSF_CAP_EFF_B);
            object child = OsfNewSeg(OSF_SEG_RES_CHILD, OSF_AMPLE_SETTING, OSF_AMPLE_CABLE, OSF_AMPLE_SETTING);
            OsfSet(child, "DesiredPull", OSF_RES_CHILD);
            OsfAddTo(net, "Suppliers", supA);
            OsfAddTo(net, "Suppliers", supB);
            OsfAddTo(net, "Consumers", child);
            netList.Add(net);
            segList.Add(supA);
            segList.Add(supB);
            segList.Add(child);

            _osfDetectStructural.Invoke(null, new object[] { netList, segList });
            bool silentA = !(bool)OsfGet(supA, "Overloaded");
            bool silentB = !(bool)OsfGet(supB, "Overloaded");

            _osfDetectResidual.Invoke(null, new object[] { netList, segList });
            bool aOver = (bool)OsfGet(supA, "Overloaded"), aCable = (bool)OsfGet(supA, "CableOverloaded");
            bool bOver = (bool)OsfGet(supB, "Overloaded"), bCable = (bool)OsfGet(supB, "CableOverloaded");
            bool childOver = (bool)OsfGet(child, "Overloaded");
            float aVal = (float)OsfGet(supA, "OverloadValueW"), aCap = (float)OsfGet(supA, "OverloadCapW");
            bool payloadOk = Math.Abs(aVal - OSF_RES_TOTAL) < OSF_EPS && Math.Abs(aCap - OSF_CAP_COMBINED) < OSF_EPS;
            OsfCheck("P2c", silentA && silentB && aOver && bOver && !aCable && !bCable && !childOver && payloadOk,
                $"1a/1b split: rule 1a silent on own demand {OSF_RES_OWN:0} under cap {OSF_CAP_COMBINED:0} (Asilent={silentA} Bsilent={silentB}); " +
                $"DetectResidualOverload trips both suppliers (A={aOver} B={bOver} cableBits={aCable}/{bCable} childStamped={childOver}) " +
                $"payload=({aVal:0},{aCap:0}) expected ({OSF_RES_TOTAL:0},{OSF_CAP_COMBINED:0}).");

            // P2d: a same-tick Deprioritized supplier's hardware cap STAYS in the capacity sum
            // (decision-33 (b)), so a shed of one supplier cannot manufacture a false overload on
            // the survivor: demand 1700 W against 1000 W active + 800 W shed = 1800 W cap, no trip
            // from either rule.
            var netList2 = OsfTypedList(_osfNetT);
            var segList2 = OsfTypedList(_osfSegT);
            object net2 = OsfNewNet(OSF_NET_CAPFIX, OSF_FIX_DEMAND, 0f, OSF_AMPLE_CABLE);
            object supC = OsfNewSeg(OSF_SEG_FIX_A, OSF_CAP_EFF_A, OSF_AMPLE_CABLE, OSF_CAP_EFF_A);
            object supD = OsfNewSeg(OSF_SEG_FIX_B, OSF_CAP_EFF_B, OSF_AMPLE_CABLE, OSF_CAP_EFF_B);
            OsfSet(supD, "Deprioritized", true);
            OsfAddTo(net2, "Suppliers", supC);
            OsfAddTo(net2, "Suppliers", supD);
            netList2.Add(net2);
            segList2.Add(supC);
            segList2.Add(supD);

            _osfDetectStructural.Invoke(null, new object[] { netList2, segList2 });
            _osfDetectResidual.Invoke(null, new object[] { netList2, segList2 });
            bool cOver = (bool)OsfGet(supC, "Overloaded");
            bool dOver = (bool)OsfGet(supD, "Overloaded");
            OsfCheck("P2d", !cOver && !dOver,
                $"deprioritized supplier counted in the cap: demand {OSF_FIX_DEMAND:0} vs {OSF_CAP_EFF_A:0} active + {OSF_CAP_EFF_B:0} shed; " +
                $"no false stamp (activeSupplier={cOver} shedSupplier={dOver}, both expected false).");
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
            bool gotFault = OsfTryGetFault(true, OSF_SEG_CABLE, tick, out float sec, out float flow, out float cap, out float _);
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
                // "Device overloaded fault" title and carries the 2026-07-15 hover-overhaul
                // diagnostics: "Downstream demand of X exceeds the available Y" plus the
                // "Short by" line; with storage 0 the breakdown line is absent (live per-device
                // labels are covered by PTHOVER P4).
                _osfCableClear.Invoke(null, new object[] { OSF_PRECEDENCE_REF });
                var argsOver = new object[] { OSF_PRECEDENCE_REF, tick, null, null, null };
                bool gotOver = _osfTryGetMerged.Invoke(null, argsOver) is bool b2 && b2;
                string lineOver = argsOver[3] as string ?? "";
                string outKindOver = argsOver[4]?.ToString() ?? "<null>";
                OsfCheck("P4b2", gotOver && outKindOver == "DeviceOverload"
                        && lineOver.Contains("<color=#ff2626>Device overloaded fault: ")
                        && lineOver.Contains("Downstream demand of ") && lineOver.Contains(" exceeds the available ")
                        && lineOver.Contains("Short by ")
                        && !lineOver.Contains("("),
                    $"capacity kind renders the 'Device overloaded fault:' title plus the Downstream-demand and Short-by diagnostics " +
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

            // OverloadRegistry snapshots are now 5-field (refId, remainingTicks, valueW, capW,
            // storageW); the synthetic capacity net has no elastic storage, so storageW is 0.
            // CableOverloadRegistry stays 4-field (refId, remainingTicks, flowW, capW).
            OsfCheckSnapshotShape("P5a", _osfOverSnap, _osfOverCurr, OSF_SEG_A, tick, lockoutTicks,
                OSF_CAP_DEMAND, OSF_CAP_COMBINED, 5, 0f, "transformer-overloaded");
            OsfCheckSnapshotShape("P5b", _osfCableSnap, _osfCableCurr, OSF_SEG_CABLE, tick, lockoutTicks,
                OSF_CABLE_FLOW, OSF_CABLE_CAP, 4, 0f, "cable-overloaded");
        }

        private static void OsfCheckSnapshotShape(string id, MethodInfo snap, MethodInfo curr, long wantRef,
            int tick, int lockoutTicks, float expValue, float expCap, int expectedFields, float expStorage, string label)
        {
            bool found = false;
            bool shapeOk = false;
            int remaining = -1;
            float gotV = float.NaN, gotC = float.NaN, gotS = float.NaN;
            if (snap.Invoke(null, new object[] { tick }) is IEnumerable en)
            {
                foreach (var item in en)
                {
                    var it = item.GetType();
                    var f1 = it.GetField("Item1");
                    var f2 = it.GetField("Item2");
                    var f3 = it.GetField("Item3");
                    var f4 = it.GetField("Item4");
                    var f5 = expectedFields >= 5 ? it.GetField("Item5") : null;
                    shapeOk = f1 != null && f2 != null && f3 != null && f4 != null
                        && (expectedFields < 5 || f5 != null) && it.GetFields().Length == expectedFields;
                    if (!shapeOk) break;
                    if (Convert.ToInt64(f1.GetValue(item)) != wantRef) continue;
                    found = true;
                    remaining = Convert.ToInt32(f2.GetValue(item));
                    gotV = Convert.ToSingle(f3.GetValue(item));
                    gotC = Convert.ToSingle(f4.GetValue(item));
                    gotS = f5 != null ? Convert.ToSingle(f5.GetValue(item)) : 0f;
                    break;
                }
            }
            bool inCensus = false;
            if (curr?.Invoke(null, new object[] { tick }) is IEnumerable ce)
                foreach (var o in ce)
                    if (o is long r && r == wantRef) { inCensus = true; break; }
            bool storageOk = expectedFields < 5 || Math.Abs(gotS - expStorage) < OSF_EPS;
            OsfCheck(id, found && shapeOk && inCensus && remaining > 0 && remaining <= lockoutTicks
                    && Math.Abs(gotV - expValue) < OSF_EPS && Math.Abs(gotC - expCap) < OSF_EPS && storageOk,
                $"{label} SnapshotRemaining entry shape ({expectedFields}-field): found={found} shapeOk={shapeOk} " +
                $"CurrentlyLockedOut={inCensus} remaining={remaining} (0 < r <= {lockoutTicks}) payload=({gotV:0},{gotC:0}" +
                $"{(expectedFields >= 5 ? $",storage={gotS:0}" : "")}) expected ({expValue:0},{expCap:0}" +
                $"{(expectedFields >= 5 ? $",storage={expStorage:0}" : "")}).");
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
                    OsfTryGetFault(cable, refId, tick, out float sec, out float v, out float c, out float _);
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
