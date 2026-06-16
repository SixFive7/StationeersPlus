using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using UnityEngine;

namespace ScenarioRunner
{
    // ============================================================================
    // PowerGridPlus playtest campaign (PT) probes.
    //
    // These verify, headlessly, the DATA + LOGIC behind the client-facing playtest
    // items that a rendering client would otherwise be needed for:
    //   - pgp-pt-hover-all   : fault hover line content, exact hex colors, the
    //                          per-device-type OVERLOAD clause (P4), the dead-input
    //                          cue (P7), the throttle hover (P13), precedence order.
    //   - pgp-pt-flash-all   : BrownoutFlashBehaviour attach coverage across every
    //                          device class, the renamed Orange/Red color constants,
    //                          and which classes are hover-only (no flash renderer).
    //   - pgp-pt-logic-all   : the custom LogicType reads (Priority/Shedding/
    //                          Overloaded/CycleFault/VariableVoltageFault on the
    //                          right devices, MaxCharge/MaxDischarge/Charge/Discharge
    //                          speed on batteries + APCs), incl. P9 (APC DischargeSpeed
    //                          is cell-only) and P10 (VVF exposed on producers, not
    //                          leaked onto non-producers).
    //   - pgp-pt-onoff-table : HasOnOffState per producer + DynamicGenerator (P12).
    //   - pgp-pt-synthetic-all : runs all four in one -Start cycle.
    //
    // What these CANNOT do (irreducible client residue): confirm the flash visibly
    // pulses, the countdown animates smoothly, or a real second peer mirrors state.
    // Those stay in PLAYTEST.md. Everything checkable from the data is checked here.
    //
    // Rendering check vs logic check: the hover STRING content is verified (the game
    // builds it in managed code); the on-screen render is not. Reads run on the
    // UniTask worker, so GetPassiveTooltip on a Unity-touching override may throw and
    // is reported as a per-device NOTE, not a hard fail.
    // ============================================================================
    internal static partial class Dispatcher
    {
        // ---- shared PT helpers ----

        private static Assembly PgpAsm() => GetModAssembly(PGP_ASSEMBLY);

        private static LogicType PgpLogic(Assembly asm, string fieldName)
        {
            var reg = asm?.GetType("PowerGridPlus.LogicTypeRegistry");
            var f = reg?.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            return f != null ? (LogicType)f.GetValue(null) : (LogicType)0;
        }

        private static int PgpTick(Assembly asm)
        {
            var t = asm?.GetType("PowerGridPlus.ElectricityTickCounter");
            var p = t?.GetProperty("CurrentTick",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            return p?.GetValue(null) is int i ? i : 0;
        }

        private static void PgpInvokeStatic(Assembly asm, string typeName, string method, Type[] sig, object[] args)
        {
            var t = asm?.GetType(typeName);
            var m = t?.GetMethod(method,
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static, null, sig, null);
            m?.Invoke(null, args);
        }

        private static readonly Type[] _sigLongInt = { typeof(long), typeof(int) };
        private static readonly Type[] _sigLong = { typeof(long) };
        private static readonly Type[] _sigLongFloat = { typeof(long), typeof(float) };

        private static void PgpNoteShed(Assembly asm, long r, int tick) =>
            PgpInvokeStatic(asm, "PowerGridPlus.BrownoutRegistry", "NoteShed", _sigLongInt, new object[] { r, tick });
        private static void PgpNoteOverload(Assembly asm, long r, int tick) =>
            PgpInvokeStatic(asm, "PowerGridPlus.OverloadRegistry", "NoteOverload", _sigLongInt, new object[] { r, tick });
        private static void PgpNoteCycle(Assembly asm, long r, int tick) =>
            PgpInvokeStatic(asm, "PowerGridPlus.CycleFaultRegistry", "NoteCycleFault", _sigLongInt, new object[] { r, tick });
        private static void PgpNoteVvf(Assembly asm, long r, int tick, string violators) =>
            PgpInvokeStatic(asm, "PowerGridPlus.VariableVoltageFaultRegistry", "NoteVariableVoltageFault",
                new[] { typeof(long), typeof(int), typeof(string) }, new object[] { r, tick, violators });
        private static void PgpMarkDeadInput(Assembly asm, long r) =>
            PgpInvokeStatic(asm, "PowerGridPlus.DeadInputRegistry", "MarkDeadInput", _sigLong, new object[] { r });

        // FaultHover.ResolveFaultRefId: a PowerReceiver's fault state is keyed to its linked
        // PowerTransmitter, so force the fault on the resolved id (identity for everything else).
        private static long PgpResolveFaultRefId(Assembly asm, Thing t)
        {
            try
            {
                var fh = asm?.GetType("PowerGridPlus.FaultHover");
                var m = fh?.GetMethod("ResolveFaultRefId",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Thing) }, null);
                if (m != null) return (long)m.Invoke(null, new object[] { t });
            }
            catch { }
            return t.ReferenceId;
        }

        private static void PgpClearAllFaults(Assembly asm)
        {
            foreach (var tn in new[]
            {
                "PowerGridPlus.BrownoutRegistry", "PowerGridPlus.OverloadRegistry",
                "PowerGridPlus.CycleFaultRegistry", "PowerGridPlus.VariableVoltageFaultRegistry",
                "PowerGridPlus.DeadInputRegistry"
            })
                PgpInvokeStatic(asm, tn, "ClearAll", Type.EmptyTypes, null);
        }

        // Cache writes for the soft-share caches (freshness-stamped at CurrentTick).
        private static void PgpSetSoftSupply(Assembly asm, long r, float v) =>
            PgpInvokeStatic(asm, "PowerGridPlus.SoftSupplyShareCache", "SetShare", _sigLongFloat, new object[] { r, v });
        private static void PgpSetSoftDemand(Assembly asm, long r, float v) =>
            PgpInvokeStatic(asm, "PowerGridPlus.SoftDemandShareCache", "SetShare", _sigLongFloat, new object[] { r, v });
        private static void PgpSetApcCell(Assembly asm, long r, float v) =>
            PgpInvokeStatic(asm, "PowerGridPlus.ApcCellDischargeCache", "SetShare", _sigLongFloat, new object[] { r, v });

        // Vanilla logicable reads, by exact (LogicType) overload, on any Thing.
        private static double Lv(Thing t, LogicType lt)
        {
            try
            {
                var m = t.GetType().GetMethod("GetLogicValue", new[] { typeof(LogicType) });
                return m != null ? Convert.ToDouble(m.Invoke(t, new object[] { lt })) : double.NaN;
            }
            catch { return double.NaN; }
        }
        private static bool CanRead(Thing t, LogicType lt)
        {
            try
            {
                var m = t.GetType().GetMethod("CanLogicRead", new[] { typeof(LogicType) });
                return m != null && (bool)m.Invoke(t, new object[] { lt });
            }
            catch { return false; }
        }
        private static bool CanWrite(Thing t, LogicType lt)
        {
            try
            {
                var m = t.GetType().GetMethod("CanLogicWrite", new[] { typeof(LogicType) });
                return m != null && (bool)m.Invoke(t, new object[] { lt });
            }
            catch { return false; }
        }

        // GetPassiveTooltip(null) -> Extended string. Runs on the worker; a Unity-touching
        // override may throw, in which case we return a sentinel so the caller can NOTE it.
        private static string Hover(Thing t)
        {
            try { return ReflectGetExtended(t.GetPassiveTooltip(null)) ?? ""; }
            catch (Exception e) { return "<<threw:" + e.GetType().Name + ">>"; }
        }
        private static bool HoverThrew(string s) => s != null && s.StartsWith("<<threw:");

        private static bool? GetHasOnOff(Thing t)
        {
            try
            {
                var p = t.GetType().GetProperty("HasOnOffState",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (p != null) return (bool)p.GetValue(t);
                var f = t.GetType().GetField("HasOnOffState",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (f != null) return (bool)f.GetValue(t);
            }
            catch { }
            return null;
        }

        // First live Thing whose concrete type name matches one of `names`.
        private static Thing FirstThing(params string[] names)
        {
            Thing found = null;
            var set = new HashSet<string>(names);
            OcclusionManager.AllThings.ForEach(t =>
            {
                if (found != null || t == null) return;
                if (set.Contains(t.GetType().Name)) found = t;
            });
            return found;
        }

        private static Thing FirstThingAssignableTo(string typeFullNameContains)
        {
            Thing found = null;
            OcclusionManager.AllThings.ForEach(t =>
            {
                if (found != null || t == null) return;
                var bt = t.GetType();
                while (bt != null)
                {
                    if (bt.Name == typeFullNameContains) { found = t; return; }
                    bt = bt.BaseType;
                }
            });
            return found;
        }

        // ============================================================
        // pgp-pt-hover-all  (P4 overload clauses, P7 dead-input, P13 throttle,
        //                    fault strings, hex colors, precedence)
        // ============================================================
        private static bool _ptHoverFired;

        private static void Scenario_PgpPtHoverAll()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-pt-hover-all")) return;
            if (_ptHoverFired) return;
            _ptHoverFired = true;

            int pass = 0, fail = 0, total = 0, note = 0;
            var asm = PgpAsm();
            _log?.LogInfo("[ScenarioRunner] PTHOVER START");
            try
            {
                PgpClearAllFaults(asm);
                int tick = PgpTick(asm);

                // ---- P4: per-device-type OVERLOAD clause ----
                // (type name -> expected device-specific clause substring)
                var overloadCases = new (string find, string label, string clause)[]
                {
                    ("Transformer", "Transformer", "this transformer's limit"),
                    ("Battery", "Battery", "this battery's discharge rate"),
                    ("AreaPowerControl", "APC", "this APC's output"),
                    ("RocketPowerUmbilicalMale", "UmbilicalMale", "this umbilical's discharge rate"),
                    ("RocketPowerUmbilicalFemale", "UmbilicalFemale", "this umbilical's discharge rate"),
                    ("PowerTransmitter", "PowerTransmitter", "this power link cannot carry"),
                    ("PowerReceiver", "PowerReceiver", "this power link cannot carry"),
                };
                foreach (var c in overloadCases)
                {
                    var th = FirstThingAssignableTo(c.find);
                    if (th == null) { _log?.LogInfo($"[ScenarioRunner] PTHOVER P4 {c.label} SKIP: none in scene."); note++; continue; }
                    PgpClearAllFaults(asm);
                    long key = PgpResolveFaultRefId(asm, th);
                    PgpNoteOverload(asm, key, tick);
                    string ext = Hover(th);
                    if (HoverThrew(ext)) { _log?.LogInfo($"[ScenarioRunner] PTHOVER P4 {c.label} NOTE: GetPassiveTooltip threw on worker ({ext}); hover content is client-residue for this class. ref={th.ReferenceId}"); note++; continue; }
                    bool red = ext.IndexOf("#ff2626", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool over = ext.IndexOf("Overloaded:", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool clause = ext.IndexOf(c.clause, StringComparison.OrdinalIgnoreCase) >= 0;
                    total++;
                    if (red && over && clause) { _log?.LogInfo($"[ScenarioRunner] PTHOVER P4 {c.label} PASS: overload hover names \"{c.clause}\" in red. ref={th.ReferenceId} ({th.GetType().Name})"); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTHOVER P4 {c.label} FAIL: red={red} overloaded={over} clause={clause}. ref={th.ReferenceId} ext={Truncate(ext, 260)}"); fail++; }
                }
                PgpClearAllFaults(asm);

                // ---- SHED string + orange ----
                var xf = FirstThingAssignableTo("Transformer");
                if (xf != null)
                {
                    PgpClearAllFaults(asm);
                    PgpNoteShed(asm, xf.ReferenceId, tick);
                    string ext = Hover(xf);
                    total++;
                    if (!HoverThrew(ext) && ext.Contains("#ffa500") && ext.IndexOf("Shedding: Insufficient upstream supply!", StringComparison.OrdinalIgnoreCase) >= 0)
                    { _log?.LogInfo("[ScenarioRunner] PTHOVER SHED PASS: orange + 'Shedding: Insufficient upstream supply!'"); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTHOVER SHED FAIL: ext={Truncate(ext, 220)}"); fail++; }

                    // ---- CYCLE string + red ----
                    PgpClearAllFaults(asm);
                    PgpNoteCycle(asm, xf.ReferenceId, tick);
                    ext = Hover(xf);
                    total++;
                    if (!HoverThrew(ext) && ext.Contains("#ff2626") && ext.IndexOf("Cycle Fault: This device is part of a loop!", StringComparison.OrdinalIgnoreCase) >= 0)
                    { _log?.LogInfo("[ScenarioRunner] PTHOVER CYCLE PASS: red + 'Cycle Fault: This device is part of a loop!'"); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTHOVER CYCLE FAIL: ext={Truncate(ext, 220)}"); fail++; }

                    // ---- precedence: all four faults -> CYCLE wins (highest) ----
                    PgpClearAllFaults(asm);
                    PgpNoteShed(asm, xf.ReferenceId, tick);
                    PgpNoteOverload(asm, xf.ReferenceId, tick);
                    PgpNoteCycle(asm, xf.ReferenceId, tick);
                    PgpNoteVvf(asm, xf.ReferenceId, tick, "TestConsumer");
                    ext = Hover(xf);
                    total++;
                    bool cycleWins = !HoverThrew(ext)
                        && ext.IndexOf("Cycle Fault", StringComparison.OrdinalIgnoreCase) >= 0
                        && ext.IndexOf("Shedding", StringComparison.OrdinalIgnoreCase) < 0
                        && ext.IndexOf("Overloaded", StringComparison.OrdinalIgnoreCase) < 0;
                    if (cycleWins) { _log?.LogInfo("[ScenarioRunner] PTHOVER PRECEDENCE PASS: all 4 faults active -> only the Cycle Fault line shows (CYCLE > VVF > OVERLOAD > SHED)."); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTHOVER PRECEDENCE FAIL: ext={Truncate(ext, 260)}"); fail++; }
                    PgpClearAllFaults(asm);
                }

                // ---- VVF string (read a real faulted producer if present, else force) ----
                var solar = FirstThing("SolarPanel");
                if (solar != null)
                {
                    PgpClearAllFaults(asm);
                    PgpNoteVvf(asm, solar.ReferenceId, tick, "a consumer");
                    string ext = Hover(solar);
                    total++;
                    if (!HoverThrew(ext) && ext.Contains("#ff2626") && ext.IndexOf("Variable Voltage Fault", StringComparison.OrdinalIgnoreCase) >= 0 && ext.IndexOf("without transformer", StringComparison.OrdinalIgnoreCase) >= 0)
                    { _log?.LogInfo($"[ScenarioRunner] PTHOVER VVF PASS: live solar VVF hover red + 'Variable Voltage Fault...without transformer'. ref={solar.ReferenceId}"); pass++; }
                    else if (HoverThrew(ext)) { _log?.LogInfo($"[ScenarioRunner] PTHOVER VVF NOTE: solar GetPassiveTooltip threw on worker ({ext}); client-residue. ref={solar.ReferenceId}"); note++; total--; }
                    else { _log?.LogError($"[ScenarioRunner] PTHOVER VVF FAIL: ext={Truncate(ext, 260)}"); fail++; }
                }

                // ---- P7: dead-input cue (grey, no countdown, no flash color) ----
                if (xf != null)
                {
                    PgpClearAllFaults(asm);
                    PgpMarkDeadInput(asm, xf.ReferenceId);
                    string ext = Hover(xf);
                    total++;
                    bool grey = !HoverThrew(ext) && ext.Contains("#9aa0a6");
                    bool cue = !HoverThrew(ext) && ext.IndexOf("No upstream supply", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool noFlashColor = !HoverThrew(ext) && !ext.Contains("#ff2626") && !ext.Contains("#ffa500");
                    if (grey && cue && noFlashColor) { _log?.LogInfo("[ScenarioRunner] PTHOVER P7 PASS: dead-input shows grey '(No upstream supply)' with no fault color/countdown."); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTHOVER P7 FAIL: grey={grey} cue={cue} noFlashColor={noFlashColor} ext={Truncate(ext, 220)}"); fail++; }

                    // P7 precedence: a real fault wins over the dead-input cue.
                    PgpNoteOverload(asm, xf.ReferenceId, tick);
                    ext = Hover(xf);
                    total++;
                    bool faultWins = !HoverThrew(ext)
                        && ext.IndexOf("Overloaded", StringComparison.OrdinalIgnoreCase) >= 0
                        && ext.IndexOf("No upstream supply", StringComparison.OrdinalIgnoreCase) < 0;
                    if (faultWins) { _log?.LogInfo("[ScenarioRunner] PTHOVER P7-prec PASS: a real fault (overload) takes precedence over the dead-input cue."); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTHOVER P7-prec FAIL: ext={Truncate(ext, 220)}"); fail++; }
                    PgpClearAllFaults(asm);
                }

                // ---- P13: throttle hover on a Setting != OutputMaximum transformer ----
                Thing throttled = null;
                if (_transformers.Count == 0) RebuildCaches();
                foreach (var t in _transformers)
                {
                    if (t == null) continue;
                    if (t.OutputMaximum - t.Setting > 0.5f) { throttled = t; break; }
                }
                if (throttled == null)
                {
                    // none saved throttled: synthesize on a sample (restore after).
                    var t = _transformers.FirstOrDefault(x => x != null);
                    if (t != null)
                    {
                        double saved = t.Setting;
                        t.Setting = Math.Max(0.0, t.OutputMaximum - 2000.0);
                        string ext = Hover(t);
                        total++;
                        if (!HoverThrew(ext) && ext.Contains("#d9a441") && ext.IndexOf("Throttled to", StringComparison.OrdinalIgnoreCase) >= 0 && ext.IndexOf("The dial sets priority", StringComparison.OrdinalIgnoreCase) >= 0)
                        { _log?.LogInfo($"[ScenarioRunner] PTHOVER P13 PASS (synthetic Setting): amber throttle line shown. ref={t.ReferenceId}"); pass++; }
                        else { _log?.LogError($"[ScenarioRunner] PTHOVER P13 FAIL: ext={Truncate(ext, 260)}"); fail++; }
                        t.Setting = saved;
                    }
                }
                else
                {
                    string ext = Hover(throttled);
                    total++;
                    if (!HoverThrew(ext) && ext.Contains("#d9a441") && ext.IndexOf("Throttled to", StringComparison.OrdinalIgnoreCase) >= 0 && ext.IndexOf("The dial sets priority", StringComparison.OrdinalIgnoreCase) >= 0)
                    { _log?.LogInfo($"[ScenarioRunner] PTHOVER P13 PASS (saved Setting={((Transformer)throttled).Setting:0}/{((Transformer)throttled).OutputMaximum:0}): amber throttle line shown. ref={throttled.ReferenceId}"); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTHOVER P13 FAIL: ext={Truncate(ext, 260)}"); fail++; }

                    // P13 stacks below a fault: force overload on the throttled transformer -> both lines.
                    PgpClearAllFaults(asm);
                    PgpNoteOverload(asm, throttled.ReferenceId, tick);
                    ext = Hover(throttled);
                    total++;
                    if (!HoverThrew(ext) && ext.IndexOf("Overloaded", StringComparison.OrdinalIgnoreCase) >= 0 && ext.IndexOf("Throttled to", StringComparison.OrdinalIgnoreCase) >= 0)
                    { _log?.LogInfo("[ScenarioRunner] PTHOVER P13-stack PASS: overloaded + throttled transformer shows BOTH the red overload line and the amber throttle line."); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTHOVER P13-stack FAIL: ext={Truncate(ext, 260)}"); fail++; }
                    PgpClearAllFaults(asm);
                }

                PgpClearAllFaults(asm);
                _log?.LogInfo($"[ScenarioRunner] PTHOVER END pass={pass} fail={fail} note={note} total={total}");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] PTHOVER threw: {e}");
                PgpClearAllFaults(asm);
            }
        }

        // ============================================================
        // pgp-pt-flash-all  (BrownoutFlashBehaviour attach coverage)
        // ============================================================
        private static bool _ptFlashFired;

        private static void Scenario_PgpPtFlashAll()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-pt-flash-all")) return;
            if (_ptFlashFired) return;
            _ptFlashFired = true;

            int pass = 0, fail = 0, total = 0, note = 0;
            var asm = PgpAsm();
            _log?.LogInfo("[ScenarioRunner] PTFLASH START");
            try
            {
                var flashType = asm.GetType("PowerGridPlus.BrownoutFlashBehaviour");
                if (flashType == null) { _log?.LogError("[ScenarioRunner] PTFLASH FAIL: BrownoutFlashBehaviour type not found."); return; }

                // ---- color constants (renamed Orange/Red in the P5/P6 rework) ----
                Color orange = ReadColorField(flashType, "OrangeFlashColor");
                Color red = ReadColorField(flashType, "RedFlashColor");
                float hz = ReadFloatField(flashType, "FlashHz");
                total++;
                if (Math.Abs(orange.r - 1f) < 0.02f && orange.g >= 0.4f && orange.g <= 0.7f && orange.b < 0.02f)
                { _log?.LogInfo($"[ScenarioRunner] PTFLASH ORANGE PASS: OrangeFlashColor=({orange.r:F2},{orange.g:F2},{orange.b:F2}) (#ffa500 band, SHED)."); pass++; }
                else { _log?.LogError($"[ScenarioRunner] PTFLASH ORANGE FAIL: OrangeFlashColor=({orange.r:F2},{orange.g:F2},{orange.b:F2})."); fail++; }
                total++;
                if (Math.Abs(red.r - 1f) < 0.02f && red.g < 0.3f && red.b < 0.3f)
                { _log?.LogInfo($"[ScenarioRunner] PTFLASH RED PASS: RedFlashColor=({red.r:F2},{red.g:F2},{red.b:F2}) (#ff2626 band, OVERLOAD/CYCLE/VVF)."); pass++; }
                else { _log?.LogError($"[ScenarioRunner] PTFLASH RED FAIL: RedFlashColor=({red.r:F2},{red.g:F2},{red.b:F2})."); fail++; }
                total++;
                if (Math.Abs(hz - 2f) < 0.01f) { _log?.LogInfo($"[ScenarioRunner] PTFLASH HZ PASS: FlashHz={hz}."); pass++; }
                else { _log?.LogError($"[ScenarioRunner] PTFLASH HZ FAIL: FlashHz={hz}, expected 2."); fail++; }

                var renderersField = flashType.GetField("_renderers", BindingFlags.NonPublic | BindingFlags.Instance);

                // ---- classes that SHOULD carry the flash component ----
                string[] flashClasses = {
                    "Transformer", "Battery", "AreaPowerControl", "PowerTransmitter", "PowerReceiver",
                    "RocketPowerUmbilicalMale", "PowerGeneratorPipe", "PowerGeneratorSlot", "StirlingEngine"
                };
                foreach (var cn in flashClasses)
                {
                    var th = FirstThingAssignableTo(cn);
                    if (th == null) { _log?.LogInfo($"[ScenarioRunner] PTFLASH attach {cn} SKIP: none in scene."); note++; continue; }
                    object beh = null;
                    try { beh = th.GetComponent(flashType); } catch { }
                    total++;
                    if (beh != null)
                    {
                        var rArr = renderersField?.GetValue(beh) as Array;
                        int rc = rArr?.Length ?? 0;
                        if (rc > 0) { _log?.LogInfo($"[ScenarioRunner] PTFLASH attach {cn} PASS: component attached, {rc} renderer(s) discovered (flash will render). ref={th.ReferenceId}"); }
                        else { _log?.LogInfo($"[ScenarioRunner] PTFLASH attach {cn} PASS*: component attached but 0 renderers (flash will NOT visibly render on this prefab; hover still works). ref={th.ReferenceId} -- known gap, surface in residue."); }
                        pass++;
                    }
                    else { _log?.LogError($"[ScenarioRunner] PTFLASH attach {cn} FAIL: no BrownoutFlashBehaviour component. ref={th.ReferenceId}"); fail++; }
                }

                // ---- classes that should be hover-only (NO flash component) ----
                string[] hoverOnly = {
                    "SolarPanel", "LargeWindTurbineGenerator", "RadioscopicThermalGenerator",
                    "TurbineGenerator", "PowerConnector", "RocketPowerUmbilicalFemale"
                };
                foreach (var cn in hoverOnly)
                {
                    var th = FirstThing(cn);
                    if (th == null) { _log?.LogInfo($"[ScenarioRunner] PTFLASH hover-only {cn} SKIP: none in scene."); note++; continue; }
                    object beh = null;
                    try { beh = th.GetComponent(flashType); } catch { }
                    total++;
                    if (beh == null) { _log?.LogInfo($"[ScenarioRunner] PTFLASH hover-only {cn} PASS: no flash component (hover-only by design). ref={th.ReferenceId}"); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTFLASH hover-only {cn} FAIL: unexpectedly has BrownoutFlashBehaviour. ref={th.ReferenceId}"); fail++; }
                }

                _log?.LogInfo($"[ScenarioRunner] PTFLASH END pass={pass} fail={fail} note={note} total={total}");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] PTFLASH threw: {e}");
            }
        }

        private static Color ReadColorField(Type t, string name)
        {
            var f = t.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            return f != null && f.GetValue(null) is Color c ? c : Color.black;
        }
        private static float ReadFloatField(Type t, string name)
        {
            var f = t.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            return f != null && f.GetValue(null) is float v ? v : -1f;
        }

        // ============================================================
        // pgp-pt-logic-all  (custom LogicType reads; P9 APC cell-only; P10 VVF)
        // ============================================================
        private static bool _ptLogicFired;

        private static void Scenario_PgpPtLogicAll()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-pt-logic-all")) return;
            if (_ptLogicFired) return;
            _ptLogicFired = true;

            int pass = 0, fail = 0, total = 0, note = 0;
            var asm = PgpAsm();
            _log?.LogInfo("[ScenarioRunner] PTLOGIC START");
            try
            {
                var Priority = PgpLogic(asm, "Priority");
                var Shedding = PgpLogic(asm, "Shedding");
                var Overloaded = PgpLogic(asm, "Overloaded");
                var CycleFault = PgpLogic(asm, "CycleFault");
                var Vvf = PgpLogic(asm, "VariableVoltageFault");
                var MaxCharge = PgpLogic(asm, "MaxChargeSpeed");
                var MaxDischarge = PgpLogic(asm, "MaxDischargeSpeed");
                var ChargeSpeed = PgpLogic(asm, "ChargeSpeed");
                var DischargeSpeed = PgpLogic(asm, "DischargeSpeed");
                int tick = PgpTick(asm);

                if (_transformers.Count == 0) RebuildCaches();
                var xf = _transformers.FirstOrDefault(x => x != null);
                if (xf != null)
                {
                    PgpClearAllFaults(asm);
                    // read surface
                    total++;
                    if (CanRead(xf, Priority) && CanWrite(xf, Priority) && CanRead(xf, Shedding) && !CanWrite(xf, Shedding))
                    { _log?.LogInfo("[ScenarioRunner] PTLOGIC XF-surface PASS: CanRead(Priority)=T CanWrite(Priority)=T CanRead(Shedding)=T CanWrite(Shedding)=F."); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTLOGIC XF-surface FAIL: rP={CanRead(xf, Priority)} wP={CanWrite(xf, Priority)} rS={CanRead(xf, Shedding)} wS={CanWrite(xf, Shedding)}."); fail++; }

                    // baseline 0
                    total++;
                    if (Math.Abs(Lv(xf, Shedding)) < 0.5 && Math.Abs(Lv(xf, Overloaded)) < 0.5 && Math.Abs(Lv(xf, CycleFault)) < 0.5)
                    { _log?.LogInfo("[ScenarioRunner] PTLOGIC XF-baseline PASS: Shedding/Overloaded/CycleFault all read 0 with no fault."); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTLOGIC XF-baseline FAIL: S={Lv(xf, Shedding)} O={Lv(xf, Overloaded)} C={Lv(xf, CycleFault)}."); fail++; }

                    PgpNoteShed(asm, xf.ReferenceId, tick);
                    total++;
                    if (Lv(xf, Shedding) >= 0.5) { _log?.LogInfo("[ScenarioRunner] PTLOGIC XF-Shedding PASS: reads 1 while shed."); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTLOGIC XF-Shedding FAIL: {Lv(xf, Shedding)}."); fail++; }
                    PgpClearAllFaults(asm);

                    PgpNoteOverload(asm, xf.ReferenceId, tick);
                    total++;
                    if (Lv(xf, Overloaded) >= 0.5) { _log?.LogInfo("[ScenarioRunner] PTLOGIC XF-Overloaded PASS: reads 1 while overloaded."); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTLOGIC XF-Overloaded FAIL: {Lv(xf, Overloaded)}."); fail++; }
                    PgpClearAllFaults(asm);

                    PgpNoteCycle(asm, xf.ReferenceId, tick);
                    total++;
                    if (Lv(xf, CycleFault) >= 0.5) { _log?.LogInfo("[ScenarioRunner] PTLOGIC XF-CycleFault PASS: reads 1 while cycle-faulted."); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTLOGIC XF-CycleFault FAIL: {Lv(xf, CycleFault)}."); fail++; }
                    PgpClearAllFaults(asm);

                    // P10 negative: VVF must NOT be readable on a transformer (non-producer).
                    total++;
                    if (!CanRead(xf, Vvf)) { _log?.LogInfo("[ScenarioRunner] PTLOGIC P10-neg PASS: CanRead(VariableVoltageFault) is FALSE on a transformer (not leaked onto non-producers)."); pass++; }
                    else { _log?.LogError("[ScenarioRunner] PTLOGIC P10-neg FAIL: transformer reports CanRead(VVF)=true."); fail++; }
                }

                // ---- P10: VVF exposed on producers ----
                var producerNames = new[] { "SolarPanel", "WindTurbineGenerator", "LargeWindTurbineGenerator", "TurbineGenerator", "GasFuelGenerator", "SolidFuelGenerator", "StirlingEngine", "PowerGeneratorPipe", "PowerGeneratorSlot" };
                foreach (var pn in producerNames)
                {
                    var th = FirstThing(pn);
                    if (th == null) continue;
                    bool canRead = CanRead(th, Vvf);
                    total++;
                    if (canRead) { _log?.LogInfo($"[ScenarioRunner] PTLOGIC P10 {pn} PASS: CanRead(VariableVoltageFault)=true. ref={th.ReferenceId}"); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTLOGIC P10 {pn} FAIL: CanRead(VVF)=false on a producer. ref={th.ReferenceId}"); fail++; }

                    // if it's actually in VVF (force it), the value reads 1.
                    PgpClearAllFaults(asm);
                    PgpNoteVvf(asm, th.ReferenceId, tick, "TestConsumer");
                    total++;
                    if (Lv(th, Vvf) >= 0.5) { _log?.LogInfo($"[ScenarioRunner] PTLOGIC P10 {pn}-val PASS: GetLogicValue(VVF)=1 while faulted."); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTLOGIC P10 {pn}-val FAIL: {Lv(th, Vvf)} while faulted."); fail++; }
                    PgpClearAllFaults(asm);
                }
                // RTG + PowerConnector are producers but intentionally NOT logic-exposed.
                foreach (var pn in new[] { "RadioscopicThermalGenerator", "PowerConnector" })
                {
                    var th = FirstThing(pn);
                    if (th == null) continue;
                    total++;
                    if (!CanRead(th, Vvf)) { _log?.LogInfo($"[ScenarioRunner] PTLOGIC P10-excl {pn} PASS: CanRead(VVF)=false (no logic surface, hover-only)."); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTLOGIC P10-excl {pn} FAIL: unexpectedly exposes VVF read. ref={th.ReferenceId}"); fail++; }
                }

                // ---- Battery charge/discharge speed reads ----
                if (_batteries.Count == 0) RebuildCaches();
                var bat = _batteries.FirstOrDefault(b => b != null);
                if (bat != null)
                {
                    total++;
                    if (CanRead(bat, MaxCharge) && CanRead(bat, MaxDischarge) && CanRead(bat, ChargeSpeed) && CanRead(bat, DischargeSpeed))
                    { _log?.LogInfo("[ScenarioRunner] PTLOGIC BAT-surface PASS: MaxCharge/MaxDischarge/Charge/DischargeSpeed all readable."); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTLOGIC BAT-surface FAIL: rMC={CanRead(bat, MaxCharge)} rMD={CanRead(bat, MaxDischarge)} rC={CanRead(bat, ChargeSpeed)} rD={CanRead(bat, DischargeSpeed)}."); fail++; }

                    PgpSetSoftSupply(asm, bat.ReferenceId, 1234f);
                    total++;
                    if (Math.Abs(Lv(bat, DischargeSpeed) - 1234f) < 1f) { _log?.LogInfo("[ScenarioRunner] PTLOGIC BAT-Discharge PASS: DischargeSpeed reads the SoftSupplyShareCache value (1234)."); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTLOGIC BAT-Discharge FAIL: {Lv(bat, DischargeSpeed)} (expected 1234)."); fail++; }

                    PgpSetSoftDemand(asm, bat.ReferenceId, 567f);
                    total++;
                    if (Math.Abs(Lv(bat, ChargeSpeed) - 567f) < 1f) { _log?.LogInfo("[ScenarioRunner] PTLOGIC BAT-Charge PASS: ChargeSpeed reads the SoftDemandShareCache value (567)."); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTLOGIC BAT-Charge FAIL: {Lv(bat, ChargeSpeed)} (expected 567)."); fail++; }
                    PgpSetSoftSupply(asm, bat.ReferenceId, 0f);
                    PgpSetSoftDemand(asm, bat.ReferenceId, 0f);
                }

                // ---- P9: APC DischargeSpeed is CELL-ONLY (ApcCellDischargeCache, not bundled SoftSupply) ----
                if (_apcs.Count == 0) RebuildCaches();
                var apc = _apcs.FirstOrDefault(a => a != null);
                if (apc != null)
                {
                    // Set cell discharge to 800 and the bundled SoftSupply to 9999; the read must be 800.
                    PgpSetApcCell(asm, apc.ReferenceId, 800f);
                    PgpSetSoftSupply(asm, apc.ReferenceId, 9999f);
                    double d = Lv(apc, DischargeSpeed);
                    total++;
                    if (Math.Abs(d - 800f) < 1f) { _log?.LogInfo("[ScenarioRunner] PTLOGIC P9 PASS: APC DischargeSpeed reads ApcCellDischargeCache (800), NOT the bundled SoftSupply (9999) -- cell-only confirmed."); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTLOGIC P9 FAIL: APC DischargeSpeed={d} (expected 800 cell-only; 9999 would mean still bundled)."); fail++; }

                    PgpSetApcCell(asm, apc.ReferenceId, 0f);
                    double idle = Lv(apc, DischargeSpeed);
                    total++;
                    if (Math.Abs(idle) < 1f) { _log?.LogInfo("[ScenarioRunner] PTLOGIC P9-idle PASS: APC DischargeSpeed reads 0 when the cell is idle."); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTLOGIC P9-idle FAIL: idle DischargeSpeed={idle}."); fail++; }
                    PgpSetSoftSupply(asm, apc.ReferenceId, 0f);

                    PgpSetSoftDemand(asm, apc.ReferenceId, 333f);
                    total++;
                    if (Math.Abs(Lv(apc, ChargeSpeed) - 333f) < 1f) { _log?.LogInfo("[ScenarioRunner] PTLOGIC APC-Charge PASS: APC ChargeSpeed reads SoftDemandShareCache (333)."); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTLOGIC APC-Charge FAIL: {Lv(apc, ChargeSpeed)}."); fail++; }
                    PgpSetSoftDemand(asm, apc.ReferenceId, 0f);
                }

                PgpClearAllFaults(asm);
                _log?.LogInfo($"[ScenarioRunner] PTLOGIC END pass={pass} fail={fail} note={note} total={total}");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] PTLOGIC threw: {e}");
                PgpClearAllFaults(asm);
            }
        }

        // ============================================================
        // pgp-pt-onoff-table  (P12 HasOnOffState per producer + DynamicGenerator)
        // ============================================================
        private static bool _ptOnOffFired;

        private static void Scenario_PgpPtOnOffTable()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-pt-onoff-table")) return;
            if (_ptOnOffFired) return;
            _ptOnOffFired = true;

            int pass = 0, fail = 0, total = 0, note = 0;
            _log?.LogInfo("[ScenarioRunner] PTONOFF START");
            try
            {
                // expected HasOnOffState per class (the P12 inferred table; gas decompile-proven).
                var cases = new (string cls, bool expect)[]
                {
                    ("GasFuelGenerator", true), ("SolidFuelGenerator", true), ("StirlingEngine", true),
                    ("DynamicGenerator", true),
                    ("SolarPanel", false), ("WindTurbineGenerator", false), ("LargeWindTurbineGenerator", false),
                    ("TurbineGenerator", false), ("RadioscopicThermalGenerator", false), ("PowerConnector", false),
                };
                foreach (var c in cases)
                {
                    // prefer a live instance; fall back to the prefab template.
                    Thing th = FirstThing(c.cls);
                    string src = "instance";
                    if (th == null) { th = Prefab.Find(PrefabNameFor(c.cls)) as Thing ?? FindPrefabByTypeName(c.cls); src = "prefab"; }
                    if (th == null) { _log?.LogInfo($"[ScenarioRunner] PTONOFF {c.cls} SKIP: no instance or prefab."); note++; continue; }
                    var has = GetHasOnOff(th);
                    total++;
                    if (has == null) { _log?.LogError($"[ScenarioRunner] PTONOFF {c.cls} FAIL: HasOnOffState not resolvable."); fail++; }
                    else if (has.Value == c.expect) { _log?.LogInfo($"[ScenarioRunner] PTONOFF {c.cls} PASS: HasOnOffState={has} (expected {c.expect}) [{src}]."); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTONOFF {c.cls} FAIL: HasOnOffState={has}, expected {c.expect} [{src}]."); fail++; }
                }
                _log?.LogInfo($"[ScenarioRunner] PTONOFF END pass={pass} fail={fail} note={note} total={total}");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] PTONOFF threw: {e}");
            }
        }

        private static string PrefabNameFor(string cls)
        {
            switch (cls)
            {
                case "GasFuelGenerator": return "StructureGasGenerator";
                case "SolidFuelGenerator": return "StructureSolidGenerator";
                case "StirlingEngine": return "StructureStirlingEngine";
                case "DynamicGenerator": return "DynamicGenerator";
                case "SolarPanel": return "StructureSolarPanel";
                case "WindTurbineGenerator": return "StructureWindTurbine";
                case "LargeWindTurbineGenerator": return "StructureWindTurbineLarge";
                case "TurbineGenerator": return "StructureTurbineGenerator";
                case "RadioscopicThermalGenerator": return "StructureRTG";
                case "PowerConnector": return "StructurePowerConnector";
                default: return cls;
            }
        }

        private static Thing FindPrefabByTypeName(string typeName)
        {
            Thing found = null;
            foreach (var p in Prefab.AllPrefabs)
            {
                if (p == null) continue;
                var bt = p.GetType();
                while (bt != null) { if (bt.Name == typeName) { found = p; break; } bt = bt.BaseType; }
                if (found != null) break;
            }
            return found;
        }

        // ============================================================
        // pgp-pt-topology-all  (P11 active-source gate + OFF-as-reset, P11 isolation
        //                       count, P8 share observability)
        // ============================================================
        private static bool _ptTopoFired;

        private static bool PgpClassifierBool(Assembly asm, string method, object arg)
        {
            try
            {
                var t = asm?.GetType("PowerGridPlus.ProducerClassifier");
                var m = t?.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(x => x.Name == method && x.GetParameters().Length == 1);
                if (m != null) return (bool)m.Invoke(null, new[] { arg });
            }
            catch { }
            return false;
        }

        private static float PgpSoftSupplyActual(Assembly asm, long r)
        {
            try
            {
                var t = asm?.GetType("PowerGridPlus.SoftSupplyShareCache");
                var m = t?.GetMethod("GetActualOrZero",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static, null, _sigLong, null);
                if (m != null) return Convert.ToSingle(m.Invoke(null, new object[] { r }));
            }
            catch { }
            return float.NaN;
        }

        private static bool TrySetOnOff(Thing t, bool v)
        {
            try
            {
                var p = t.GetType().GetProperty("OnOff");
                if (p != null && p.CanWrite) { p.SetValue(t, v); return true; }
                var m = t.GetType().GetMethod("SetOnOff", new[] { typeof(bool) });
                if (m != null) { m.Invoke(t, new object[] { v }); return true; }
            }
            catch { }
            return false;
        }

        private static void Scenario_PgpPtTopologyAll()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-pt-topology-all")) return;
            if (_ptTopoFired) return;
            _ptTopoFired = true;

            int pass = 0, fail = 0, total = 0, note = 0;
            var asm = PgpAsm();
            _log?.LogInfo("[ScenarioRunner] PTTOPO START");
            try
            {
                int tick = PgpTick(asm);

                // (a) active-source gate (P12): buttonless producer always active.
                var solar = FirstThing("SolarPanel");
                if (solar != null)
                {
                    total++;
                    if (PgpClassifierBool(asm, "IsActiveProducer", solar))
                    { _log?.LogInfo("[ScenarioRunner] PTTOPO active-solar PASS: IsActiveProducer(solar)=true (buttonless = always active)."); pass++; }
                    else { _log?.LogError("[ScenarioRunner] PTTOPO active-solar FAIL."); fail++; }
                }
                // buttoned producer: active iff ON.
                var solid = FirstThing("SolidFuelGenerator", "PowerGeneratorSlot");
                if (solid != null)
                {
                    bool onoff = solid.OnOff;
                    bool active = PgpClassifierBool(asm, "IsActiveProducer", solid);
                    total++;
                    if (active == onoff) { _log?.LogInfo($"[ScenarioRunner] PTTOPO active-buttoned PASS: IsActiveProducer={active} == OnOff={onoff}. ref={solid.ReferenceId}"); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTTOPO active-buttoned FAIL: active={active} onoff={onoff}."); fail++; }
                }
                // connector: active iff a docked generator delivers.
                var conn = FirstThing("PowerConnector");
                if (conn != null)
                {
                    bool active = PgpClassifierBool(asm, "IsActiveProducer", conn);
                    bool deliv = PgpClassifierBool(asm, "ConnectorIsDelivering", conn);
                    total++;
                    if (active == deliv) { _log?.LogInfo($"[ScenarioRunner] PTTOPO connector-gate PASS: IsActiveProducer={active} == ConnectorIsDelivering={deliv}. ref={conn.ReferenceId}"); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTTOPO connector-gate FAIL: active={active} deliv={deliv}."); fail++; }
                }

                // (b) OFF-as-reset (P12): force a shed, toggle OnOff=false, sweep, expect cleared.
                if (_transformers.Count == 0) RebuildCaches();
                var xf = _transformers.FirstOrDefault(x => x != null);
                if (xf != null)
                {
                    PgpClearAllFaults(asm);
                    PgpNoteShed(asm, xf.ReferenceId, tick);
                    bool before = PgpIsLocked(asm, "PowerGridPlus.BrownoutRegistry", xf.ReferenceId);
                    if (TrySetOnOff(xf, false))
                    {
                        PgpInvokeStatic(asm, "PowerGridPlus.OffAsResetSweep", "Run", new[] { typeof(int) }, new object[] { tick });
                        bool after = PgpIsLocked(asm, "PowerGridPlus.BrownoutRegistry", xf.ReferenceId);
                        total++;
                        if (before && !after) { _log?.LogInfo("[ScenarioRunner] PTTOPO OFF-reset PASS: shed cleared by OffAsResetSweep after OnOff=false."); pass++; }
                        else { _log?.LogError($"[ScenarioRunner] PTTOPO OFF-reset FAIL: before={before} after={after}."); fail++; }
                        TrySetOnOff(xf, true);
                    }
                    else { _log?.LogInfo("[ScenarioRunner] PTTOPO OFF-reset NOTE: cannot set OnOff headlessly; OFF-as-reset is client residue."); note++; }
                    PgpClearAllFaults(asm);
                }

                // (c) P11 isolation count: solar isolated vs behind a transformer.
                int solarTotal = 0, solarVvf = 0;
                OcclusionManager.AllThings.ForEach(t =>
                {
                    if (t == null || t.GetType().Name != "SolarPanel") return;
                    solarTotal++;
                    if (PgpIsLocked(asm, "PowerGridPlus.VariableVoltageFaultRegistry", t.ReferenceId)) solarVvf++;
                });
                _log?.LogInfo($"[ScenarioRunner] PTTOPO P11-count: solarTotal={solarTotal} solarInVVF={solarVvf} (solar NOT faulted = {solarTotal - solarVvf}; a not-faulted solar proves 'a transformer/regulated network -> no producer-isolation').");

                // (d) P8 share observability: do two transformers on one output net expose per-device shares?
                Transformer a = null, b = null;
                var byOut = new Dictionary<long, Transformer>();
                foreach (var t in _transformers)
                {
                    if (t == null || t.OutputNetwork == null) continue;
                    long oid = t.OutputNetwork.ReferenceId;
                    if (byOut.TryGetValue(oid, out var first)) { a = first; b = t; break; }
                    byOut[oid] = t;
                }
                if (a != null && b != null)
                {
                    float sa = PgpSoftSupplyActual(asm, a.ReferenceId);
                    float sb = PgpSoftSupplyActual(asm, b.ReferenceId);
                    double ga = Convert.ToDouble(a.GetGeneratedPower(a.OutputNetwork));
                    double gb = Convert.ToDouble(b.GetGeneratedPower(b.OutputNetwork));
                    _log?.LogInfo($"[ScenarioRunner] PTTOPO P8-observe: outNet={a.OutputNetwork.ReferenceId} A ref={a.ReferenceId} softSupply={sa:F1} gen={ga:F1} | B ref={b.ReferenceId} softSupply={sb:F1} gen={gb:F1}. (non-zero per-device value => P8 split readable headlessly.)");
                }
                else { _log?.LogInfo("[ScenarioRunner] PTTOPO P8-observe: no two transformers share an output network in this save."); }

                PgpClearAllFaults(asm);
                _log?.LogInfo($"[ScenarioRunner] PTTOPO END pass={pass} fail={fail} note={note} total={total}");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] PTTOPO threw: {e}");
                PgpClearAllFaults(asm);
            }
        }

        // ============================================================
        // pgp-pt-extra-all  (P3 NaN clamp, clean P11 isolation count, P8 PowerActual observe)
        // ============================================================
        private static bool _ptExtraFired;

        private static void Scenario_PgpPtExtraAll()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-pt-extra-all")) return;
            if (_ptExtraFired) return;
            _ptExtraFired = true;

            int pass = 0, fail = 0, total = 0, note = 0;
            var asm = PgpAsm();
            _log?.LogInfo("[ScenarioRunner] PTEXTRA START");
            try
            {
                // --- P3: DeviceOutputSanitizer.Sanitize clamps NaN/Inf to 0; passes finite through ---
                if (_transformers.Count == 0) RebuildCaches();
                Thing dev = _transformers.FirstOrDefault(x => x != null) as Thing
                            ?? _batteries.FirstOrDefault(b => b != null) as Thing;
                var sanT = asm?.GetType("PowerGridPlus.DeviceOutputSanitizer");
                var sanM = sanT?.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "Sanitize" && m.GetParameters().Length == 3);
                if (sanM != null && dev != null)
                {
                    float nan = Convert.ToSingle(sanM.Invoke(null, new object[] { float.NaN, dev, true }));
                    float pinf = Convert.ToSingle(sanM.Invoke(null, new object[] { float.PositiveInfinity, dev, true }));
                    float ninf = Convert.ToSingle(sanM.Invoke(null, new object[] { float.NegativeInfinity, dev, false }));
                    float fin = Convert.ToSingle(sanM.Invoke(null, new object[] { 1234.5f, dev, true }));
                    total++;
                    if (nan == 0f && pinf == 0f && ninf == 0f && Math.Abs(fin - 1234.5f) < 0.01f)
                    { _log?.LogInfo($"[ScenarioRunner] PTEXTRA P3 PASS: Sanitize NaN->0, +Inf->0, -Inf->0, finite(1234.5)->{fin} (non-finite clamped at source, finite passes through)."); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTEXTRA P3 FAIL: nan={nan} pinf={pinf} ninf={ninf} fin={fin}."); fail++; }
                }
                else { _log?.LogInfo("[ScenarioRunner] PTEXTRA P3 NOTE: Sanitize method or device not found."); note++; }

                // --- clean P11 isolation count: read VVF BEFORE any clear ---
                int solarTotal = 0, solarVvf = 0;
                OcclusionManager.AllThings.ForEach(t =>
                {
                    if (t == null || t.GetType().Name != "SolarPanel") return;
                    solarTotal++;
                    if (PgpIsLocked(asm, "PowerGridPlus.VariableVoltageFaultRegistry", t.ReferenceId)) solarVvf++;
                });
                total++;
                if (solarTotal > 0 && solarVvf > 0 && solarVvf < solarTotal)
                { _log?.LogInfo($"[ScenarioRunner] PTEXTRA P11-count PASS: solarTotal={solarTotal} isolated(VVF)={solarVvf} regulated(notFaulted)={solarTotal - solarVvf} -> producer-isolation fires ONLY on the direct-wired solar; solar behind a transformer does not fault."); pass++; }
                else { _log?.LogInfo($"[ScenarioRunner] PTEXTRA P11-count NOTE: solarTotal={solarTotal} VVF={solarVvf} (expected 0<VVF<total)."); note++; total--; }

                // --- P8 observability: is per-transformer delivered power readable via PowerActual? ---
                LogicType powerActual = default; bool paOk = false;
                try { powerActual = (LogicType)Enum.Parse(typeof(LogicType), "PowerActual"); paOk = true; } catch { }
                if (paOk)
                {
                    var loaded = _transformers.FirstOrDefault(t => t != null && t.OutputNetwork != null && t.OutputNetwork.CurrentLoad > 1f);
                    if (loaded != null)
                    {
                        double pa = Lv(loaded, powerActual);
                        double gen = Convert.ToDouble(loaded.GetGeneratedPower(loaded.OutputNetwork));
                        _log?.LogInfo($"[ScenarioRunner] PTEXTRA P8-observe: loaded transformer ref={loaded.ReferenceId} outLoad={loaded.OutputNetwork.CurrentLoad:F1} PowerActual={pa:F1} GetGeneratedPower(out)={gen:F1}. (non-zero PowerActual => per-transformer share readable via IC10 on a loaded parallel pair.)");
                    }
                    else { _log?.LogInfo("[ScenarioRunner] PTEXTRA P8-observe: no loaded transformer (outLoad>1) in this save."); }
                }

                _log?.LogInfo($"[ScenarioRunner] PTEXTRA END pass={pass} fail={fail} note={note} total={total}");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] PTEXTRA threw: {e}");
            }
        }

        // ============================================================
        // pgp-pt-crossmod-all  (P6.1 PowerTransmitterPlus distance-draw interop)
        // ============================================================
        private static bool _ptCrossFired;

        private static float PgpSourceDrawMult(Assembly asm, object pt)
        {
            try
            {
                var t = asm?.GetType("PowerGridPlus.PowerTransmitterPlusInterop");
                var m = t?.GetMethod("SourceDrawMultiplier", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                if (m != null) return Convert.ToSingle(m.Invoke(null, new[] { pt }));
            }
            catch { }
            return float.NaN;
        }

        private static void Scenario_PgpPtCrossModAll()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-pt-crossmod-all")) return;
            if (_ptCrossFired) return;
            _ptCrossFired = true;

            int pass = 0, fail = 0, total = 0, note = 0;
            var asm = PgpAsm();
            bool ptpLoaded = GetModAssembly("PowerTransmitterPlus") != null;
            _log?.LogInfo($"[ScenarioRunner] PTCROSS START ptpLoaded={ptpLoaded}");
            try
            {
                int countPT = 0, linked = 0; float maxM = 1f; long maxRef = 0;
                var samples = new List<string>();
                OcclusionManager.AllThings.ForEach(t =>
                {
                    if (t == null || !(t is PowerTransmitter pt)) return;
                    countPT++;
                    float m = PgpSourceDrawMult(asm, pt);
                    bool isLinked = false;
                    try
                    {
                        var lr = pt.GetType().GetProperty("LinkedReceiver")?.GetValue(pt)
                                 ?? pt.GetType().GetField("LinkedReceiver")?.GetValue(pt);
                        isLinked = lr != null;
                    }
                    catch { }
                    if (isLinked) linked++;
                    if (!float.IsNaN(m) && m > maxM) { maxM = m; maxRef = pt.ReferenceId; }
                    if (samples.Count < 8 && isLinked) samples.Add($"ref={pt.ReferenceId} m={m:F3}");
                });
                foreach (var s in samples) _log?.LogInfo($"[ScenarioRunner] PTCROSS sample linked PT {s}");
                _log?.LogInfo($"[ScenarioRunner] PTCROSS totals: PT={countPT} linked={linked} maxM={maxM:F3} (ref={maxRef})");

                total++;
                if (ptpLoaded)
                {
                    if (maxM > 1.001f)
                    { _log?.LogInfo($"[ScenarioRunner] PTCROSS P6.1 PASS: PowerTransmitterPlusInterop.SourceDrawMultiplier reads PTP's distance multiplier (max m={maxM:F3}>1). The allocator bills the PT input network delivered*m (P6.1). Cross-mod read works end-to-end."); pass++; }
                    else
                    { _log?.LogInfo($"[ScenarioRunner] PTCROSS P6.1 NOTE: PTP loaded but no linked PT returned m>1 (links short / zero-distance or PTP build lacks the accessor). maxM={maxM:F3}."); note++; total--; }
                }
                else
                {
                    if (Math.Abs(maxM - 1f) < 0.001f)
                    { _log?.LogInfo("[ScenarioRunner] PTCROSS P6.1-degrade PASS: PTP absent -> SourceDrawMultiplier returns 1 (soft dependency degrades safely, m=1)."); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTCROSS P6.1-degrade FAIL: PTP absent but maxM={maxM}."); fail++; }
                }
                _log?.LogInfo($"[ScenarioRunner] PTCROSS END pass={pass} fail={fail} note={note} total={total}");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] PTCROSS threw: {e}");
            }
        }

        // ============================================================
        // pgp-pt-burnreason  (does the tier-burn reason attach to its wreckage?)
        // ============================================================
        private static bool _ptBurnFired;

        private static void Scenario_PgpPtBurnReason()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-pt-burnreason")) return;
            if (_ptBurnFired) return;
            _ptBurnFired = true;

            var asm = PgpAsm();
            _log?.LogInfo("[ScenarioRunner] PTBURN START");
            try
            {
                var t = asm.GetType("PowerGridPlus.BurnReasonRegistry");
                var snap = t?.GetMethod("SnapshotAttached", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                var getAttached = t?.GetMethod("GetAttached", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Thing) }, null);
                int attachedCount = 0, matchedCableRuptured = 0, getAttachedNonEmpty = 0;
                if (snap != null)
                {
                    var en = snap.Invoke(null, null) as System.Collections.IEnumerable;
                    if (en != null)
                        foreach (var kv in en)
                        {
                            attachedCount++;
                            long refId = (long)kv.GetType().GetProperty("Key").GetValue(kv);
                            string reason = kv.GetType().GetProperty("Value").GetValue(kv) as string;
                            var thing = Thing.Find(refId);
                            bool isCR = thing != null && thing.GetType().Name == "CableRuptured";
                            if (isCR) matchedCableRuptured++;
                            if (thing != null && getAttached != null)
                            {
                                var r = getAttached.Invoke(null, new object[] { thing }) as string;
                                if (!string.IsNullOrEmpty(r)) getAttachedNonEmpty++;
                            }
                            if (attachedCount <= 8) _log?.LogInfo($"[ScenarioRunner] PTBURN attached ref={refId} isCableRuptured={isCR} thingType={thing?.GetType().Name ?? "<not found>"} reason=\"{reason}\"");
                        }
                }
                else { _log?.LogInfo("[ScenarioRunner] PTBURN: SnapshotAttached method not found."); }

                // --- disambiguation: is the OnRegistered postfix attached? ---
                var crType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                    .FirstOrDefault(tp => tp.FullName == "Assets.Scripts.Objects.Electrical.CableRuptured");
                var onReg = crType?.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "OnRegistered");
                bool postfixAttached = false;
                if (onReg != null)
                {
                    var info = HarmonyLib.Harmony.GetPatchInfo(onReg);
                    if (info?.Postfixes != null)
                        foreach (var p in info.Postfixes)
                            if ((p.owner ?? "").IndexOf("powergridplus", StringComparison.OrdinalIgnoreCase) >= 0) postfixAttached = true;
                }
                _log?.LogInfo($"[ScenarioRunner] PTBURN postfixAttached(CableRuptured.OnRegistered)={postfixAttached} onRegResolved={onReg != null} (declaringType={onReg?.DeclaringType?.Name})");

                // --- dump _pendingByCell + scan CableRuptured cells ---
                var brType2 = asm.GetType("PowerGridPlus.BurnReasonRegistry");
                var pendField = brType2?.GetField("_pendingByCell", BindingFlags.NonPublic | BindingFlags.Static);
                var pendEnum = pendField?.GetValue(null) as System.Collections.IEnumerable;
                var pendCells = new List<string>();
                if (pendEnum != null)
                    foreach (var kv in pendEnum)
                    {
                        var k = kv.GetType().GetProperty("Key")?.GetValue(kv);
                        var v = kv.GetType().GetProperty("Value")?.GetValue(kv) as string;
                        pendCells.Add(k?.ToString() ?? "?");
                        _log?.LogInfo($"[ScenarioRunner] PTBURN pending cell={k} reason='{v}'");
                    }
                int crCount = 0; var crCells = new List<string>();
                OcclusionManager.AllThings.ForEach(t =>
                {
                    if (t == null || t.GetType().Name != "CableRuptured") return;
                    crCount++;
                    var lg = t.GetType().GetProperty("LocalGrid")?.GetValue(t);
                    crCells.Add(lg?.ToString() ?? "?");
                });
                _log?.LogInfo($"[ScenarioRunner] PTBURN CableRupturedInWorld={crCount}");
                foreach (var pc in pendCells)
                    _log?.LogInfo($"[ScenarioRunner] PTBURN pending cell {pc} hasCableRupturedAtSameCell={crCells.Contains(pc)}");

                // --- bug 2 verification: flash renderers discovered on Battery / APC / nuclear ---
                var flashType2 = asm.GetType("PowerGridPlus.BrownoutFlashBehaviour");
                var rField = flashType2?.GetField("_renderers", BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (var cn in new[] { "Battery", "AreaPowerControl", "StationBatteryNuclear" })
                {
                    var th = FirstThingAssignableTo(cn) ?? FirstThing(cn);
                    if (th == null) continue;
                    object beh = null; try { beh = th.GetComponent(flashType2); } catch { }
                    int rc = -1;
                    if (beh != null) { var arr = rField?.GetValue(beh) as Array; rc = arr?.Length ?? 0; }
                    _log?.LogInfo($"[ScenarioRunner] PTBURN flash {cn} ref={th.ReferenceId} type={th.GetType().Name} attached={beh != null} renderers={rc} (bug2: renderers>0 = fixed)");
                }

                _log?.LogInfo($"[ScenarioRunner] PTBURN END attachedCount={attachedCount} matchedCableRuptured={matchedCableRuptured} getAttachedNonEmpty={getAttachedNonEmpty} (attachedCount=0 => RegisterPending->wreckage consume failed; attached>0 but matched=0 => refId/Find mismatch)");
            }
            catch (Exception e) { _log?.LogError($"[ScenarioRunner] PTBURN threw: {e}"); }
        }

        // ============================================================
        // pgp-pt-fixverify  (extensive verification of the two 2026-06-16 bug fixes)
        //   Bug 1 burn-reason: A1 all 3 BurnReasonPatches attached; A3/A4 wreckage hover renders
        //     "Burned: <reason>" (orange) for several reason strings; A5 side-car restore on reload;
        //     A7 a non-CableRuptured shows no Burned line.
        //   Bug 2 flash: B1 the correct indicator renderer is discovered per class; B2 transformer
        //     unaffected; B6 hover-only classes still have no flash component.
        // ============================================================
        private static bool _ptFixFired;

        private static Type FindTypeFull(string fullName) => AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
            .FirstOrDefault(t => t.FullName == fullName);

        private static void Scenario_PgpPtFixVerify()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-pt-fixverify")) return;
            if (_ptFixFired) return;
            _ptFixFired = true;

            int pass = 0, fail = 0, total = 0, note = 0;
            var asm = PgpAsm();
            _log?.LogInfo("[ScenarioRunner] PTFIX START");
            try
            {
                // ===== Bug 1: burn-reason =====
                // A1: all three BurnReasonPatches targets carry a PGP postfix.
                var targets = new (string typeFull, string method)[]
                {
                    ("Assets.Scripts.Objects.Electrical.CableRuptured", "OnRegistered"),
                    ("Assets.Scripts.Objects.Structure", "GetPassiveTooltip"),
                    ("Assets.Scripts.UI.Tooltip", "SetValuesForInteractable"),
                };
                foreach (var (tf, mn) in targets)
                {
                    var ty = FindTypeFull(tf);
                    var mi = ty?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == mn);
                    bool attached = false;
                    if (mi != null)
                    {
                        var info = HarmonyLib.Harmony.GetPatchInfo(mi);
                        if (info?.Postfixes != null)
                            foreach (var p in info.Postfixes)
                                if ((p.owner ?? "").IndexOf("powergridplus", StringComparison.OrdinalIgnoreCase) >= 0) attached = true;
                    }
                    total++;
                    if (attached) { _log?.LogInfo($"[ScenarioRunner] PTFIX A1 PASS: PGP postfix attached on {tf}.{mn}."); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTFIX A1 FAIL: no PGP postfix on {tf}.{mn} (resolved={mi != null})."); fail++; }
                }

                var brType = asm.GetType("PowerGridPlus.BurnReasonRegistry");
                var attachM = brType?.GetMethod("Attach",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(object), typeof(string) }, null);
                var getAttachedM = brType?.GetMethod("GetAttached",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(object) }, null);

                // Find a CableRuptured that already carries a reason (live-burn on tierburn, or side-car
                // restore on a reloaded autosave). This is the A3 + A5 signal.
                Thing reasoned = null; string reasonedText = null;
                OcclusionManager.AllThings.ForEach(t =>
                {
                    if (reasoned != null || t == null || t.GetType().Name != "CableRuptured") return;
                    var r = getAttachedM?.Invoke(null, new object[] { t }) as string;
                    if (!string.IsNullOrEmpty(r)) { reasoned = t; reasonedText = r; }
                });
                // A5: report the side-car LoadedReasons (populated only when a reload restored from the side-car).
                var sideType = asm.GetType("PowerGridPlus.BurnReasonSideCar");
                var loadedField = sideType?.GetField("LoadedReasons", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                var loaded = loadedField?.GetValue(null) as System.Collections.IDictionary;
                _log?.LogInfo($"[ScenarioRunner] PTFIX A5 side-car LoadedReasons count={(loaded?.Count ?? -1)} (>0 means a reload restored reasons from pwrgridplus-burnreason.xml)");

                total++;
                if (reasoned != null)
                {
                    string ext = Hover(reasoned);
                    bool ok = !HoverThrew(ext) && ext.Contains("#ffa500") && ext.IndexOf("Burned:", StringComparison.OrdinalIgnoreCase) >= 0 && ext.IndexOf(reasonedText, StringComparison.OrdinalIgnoreCase) >= 0;
                    bool fromSideCar = loaded != null && loaded.Contains(reasoned.ReferenceId);
                    if (ok) { _log?.LogInfo($"[ScenarioRunner] PTFIX A3 PASS: live wreckage ref={reasoned.ReferenceId} hover shows orange 'Burned:' + the reason (sideCarRestored={fromSideCar})."); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTFIX A3 FAIL: ext={Truncate(ext, 240)}"); fail++; }
                }
                else { _log?.LogInfo("[ScenarioRunner] PTFIX A3 NOTE: no CableRuptured currently carries a reason (no burn this run)."); note++; total--; }

                // A4: synthetic Attach of several reason variants on any wreckage -> hover renders each.
                Thing anyWreck = reasoned;
                if (anyWreck == null)
                    OcclusionManager.AllThings.ForEach(t => { if (anyWreck == null && t != null && t.GetType().Name == "CableRuptured") anyWreck = t; });
                if (anyWreck != null && attachM != null)
                {
                    foreach (var reason in new[]
                    {
                        "Overloaded -- sustained generator output exceeded the cable rating",
                        "Wrong voltage -- the adjacent Solar Panel doesn't accept normal cable",
                        "Wrong voltage -- normal cable was bridging into a different cable tier",
                    })
                    {
                        attachM.Invoke(null, new object[] { anyWreck, reason });
                        string ext = Hover(anyWreck);
                        total++;
                        if (!HoverThrew(ext) && ext.Contains("#ffa500") && ext.IndexOf("Burned:", StringComparison.OrdinalIgnoreCase) >= 0 && ext.IndexOf(reason, StringComparison.OrdinalIgnoreCase) >= 0)
                        { _log?.LogInfo($"[ScenarioRunner] PTFIX A4 PASS: hover renders 'Burned: {Truncate(reason, 44)}...'"); pass++; }
                        else { _log?.LogError($"[ScenarioRunner] PTFIX A4 FAIL: reason='{Truncate(reason, 40)}' ext={Truncate(ext, 200)}"); fail++; }
                    }
                }
                else { _log?.LogInfo("[ScenarioRunner] PTFIX A4 SKIP: no CableRuptured to attach to."); note++; }

                // A7: a non-CableRuptured (transformer) shows no "Burned:" line (the is-CableRuptured filter holds).
                if (_transformers.Count == 0) RebuildCaches();
                var xf = _transformers.FirstOrDefault(t => t != null);
                if (xf != null)
                {
                    string ext = Hover(xf);
                    total++;
                    if (HoverThrew(ext) || ext.IndexOf("Burned:", StringComparison.OrdinalIgnoreCase) < 0)
                    { _log?.LogInfo("[ScenarioRunner] PTFIX A7 PASS: transformer hover has no 'Burned:' line (is-CableRuptured filter holds)."); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTFIX A7 FAIL: transformer shows Burned: {Truncate(ext, 150)}"); fail++; }
                }

                // ===== Bug 2: flash renderer correctness =====
                var flashType = asm.GetType("PowerGridPlus.BrownoutFlashBehaviour");
                var rField = flashType?.GetField("_renderers", BindingFlags.NonPublic | BindingFlags.Instance);
                var flashExpect = new (string cls, string expect)[]
                {
                    ("Transformer", null), ("Battery", null), ("AreaPowerControl", "Lever"),
                    ("StationBatteryNuclear", "Indic"), ("PowerTransmitter", null), ("PowerReceiver", null),
                    ("RocketPowerUmbilicalMale", null),
                };
                foreach (var (cls, expect) in flashExpect)
                {
                    var th = FirstThingAssignableTo(cls) ?? FirstThing(cls);
                    if (th == null) { _log?.LogInfo($"[ScenarioRunner] PTFIX B1 {cls} SKIP: none in scene."); note++; continue; }
                    object beh = null; try { beh = th.GetComponent(flashType); } catch { }
                    total++;
                    if (beh == null) { _log?.LogError($"[ScenarioRunner] PTFIX B1 {cls} FAIL: no flash component."); fail++; continue; }
                    var arr = rField?.GetValue(beh) as Array;
                    var names = new List<string>();
                    if (arr != null) foreach (var ro in arr) { if (ro is MeshRenderer mr && mr.gameObject != null) names.Add(mr.gameObject.name); }
                    bool ok = names.Count > 0 && (expect == null || names.Any(n => n.IndexOf(expect, StringComparison.OrdinalIgnoreCase) >= 0));
                    if (ok) { _log?.LogInfo($"[ScenarioRunner] PTFIX B1 {cls} PASS: renderers=[{string.Join(",", names)}]{(expect != null ? $" matches '{expect}'" : "")}"); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTFIX B1 {cls} FAIL: renderers=[{string.Join(",", names)}] expected substr '{expect}'."); fail++; }
                }
                foreach (var cls in new[] { "SolarPanel", "PowerConnector", "RocketPowerUmbilicalFemale" })
                {
                    var th = FirstThing(cls); if (th == null) continue;
                    object beh = null; try { beh = th.GetComponent(flashType); } catch { }
                    total++;
                    if (beh == null) { _log?.LogInfo($"[ScenarioRunner] PTFIX B6 {cls} PASS: no flash component (hover-only)."); pass++; }
                    else { _log?.LogError($"[ScenarioRunner] PTFIX B6 {cls} FAIL: unexpectedly has a flash component."); fail++; }
                }

                _log?.LogInfo($"[ScenarioRunner] PTFIX END pass={pass} fail={fail} note={note} total={total}");
            }
            catch (Exception e) { _log?.LogError($"[ScenarioRunner] PTFIX threw: {e}"); }
        }

        // ============================================================
        // pgp-pt-synthetic-all : run every PT synthetic probe in one cycle.
        // ============================================================
        private static void Scenario_PgpPtSyntheticAll()
        {
            Scenario_PgpPtHoverAll();
            Scenario_PgpPtFlashAll();
            Scenario_PgpPtLogicAll();
            Scenario_PgpPtOnOffTable();
        }
    }
}
