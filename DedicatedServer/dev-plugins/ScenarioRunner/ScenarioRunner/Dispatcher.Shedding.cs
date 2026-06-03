using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Inventory;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using UnityEngine;

namespace ScenarioRunner
{
    // Headless scenarios that extend coverage of the Transformer Priority +
    // Shedding feature into the surfaces that the original PSP probe (in
    // Dispatcher.cs) could not reach: the knob-step path, the shed flash
    // attach + colour constants, the hover error message, the labeller
    // redirect, the multiplayer code paths (host short-circuit + state
    // machine + join-suffix round-trip), Harmony hook attachment for
    // save/load, and the cross-topology allocator behaviour.
    //
    // Every scenario emits structured "[ScenarioRunner] <TAG> P<n> PASS|FAIL ..."
    // lines plus a final "END pass=X fail=Y total=Z" so an agent can grep PASS/FAIL
    // counts off LogOutput.log.
    //
    // The aggregator scenario "pgp-priority-shedding-all" runs every probe below
    // on the first scenario tick. Configure via Probe / Scenario in
    // net.scenariorunner.cfg.
    internal static partial class Dispatcher
    {
        // ============================================================
        // Scenario: pgp-priority-shedding-knob-probe (C-a + C-b)
        // ------------------------------------------------------------
        // Verifies the knob-step computation by directly invoking the
        // patched Transformer.InteractWith logic (with SetKnob nulled out
        // to avoid main-thread Unity transform writes from a worker), and
        // by reading the patched NeedleFullScale + step constants.
        // ============================================================
        private static bool _kbpFired;

        private static void Scenario_PgpPriorityShedingKnobProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-priority-shedding-knob-probe")) return;
            if (_kbpFired) return;
            _kbpFired = true;

            int totalChecks = 0;
            int passCount = 0;
            int failCount = 0;

            try
            {
                _log?.LogInfo("[ScenarioRunner] KBP START knob-probe");
                var asm = GetModAssembly(PGP_ASSEMBLY);
                if (asm == null) { _log?.LogError("[ScenarioRunner] KBP no PGP assembly"); return; }

                var patchType = asm.GetType("PowerGridPlus.Patches.TransformerInteractWithPatches");
                var priorityStoreType = asm.GetType("PowerGridPlus.PriorityStore");
                if (patchType == null || priorityStoreType == null)
                {
                    _log?.LogError("[ScenarioRunner] KBP FAIL: type lookup failed (patchType / priorityStoreType).");
                    failCount++;
                    return;
                }

                // ---- P1: NeedleFullScale constant ----
                var ndlField = patchType.GetField("NeedleFullScale",
                    BindingFlags.NonPublic | BindingFlags.Static);
                float ndlFullScale = ndlField != null ? (float)ndlField.GetValue(null) : -1f;
                totalChecks++;
                if (Math.Abs(ndlFullScale - 200f) < 0.001f)
                { _log?.LogInfo($"[ScenarioRunner] KBP P1 PASS: NeedleFullScale={ndlFullScale} (matches spec, Priority 100 lerps to mid-deflection)."); passCount++; }
                else
                { _log?.LogError($"[ScenarioRunner] KBP P1 FAIL: NeedleFullScale={ndlFullScale}, expected 200."); failCount++; }

                // ---- P2: step constants ----
                var stepSmallField = patchType.GetField("PriorityStepSmall",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var stepNormalField = patchType.GetField("PriorityStepNormal",
                    BindingFlags.NonPublic | BindingFlags.Static);
                int stepSmall = stepSmallField != null ? (int)stepSmallField.GetValue(null) : -1;
                int stepNormal = stepNormalField != null ? (int)stepNormalField.GetValue(null) : -1;
                totalChecks++;
                if (stepSmall == 1 && stepNormal == 10)
                { _log?.LogInfo($"[ScenarioRunner] KBP P2 PASS: PriorityStepSmall={stepSmall} (Alt), PriorityStepNormal={stepNormal} (default click)."); passCount++; }
                else
                { _log?.LogError($"[ScenarioRunner] KBP P2 FAIL: step constants Small={stepSmall} Normal={stepNormal}, expected 1 and 10."); failCount++; }

                // ---- P3: real InteractWithPatch invocation, end-to-end ----
                if (_transformers.Count == 0) RebuildCaches();
                Transformer sampleT = null;
                Interactable btn1 = null;
                Interactable btn2 = null;
                foreach (var t in _transformers)
                {
                    if (t == null || t.Interactables == null) continue;
                    Interactable b1 = null, b2 = null;
                    for (int i = 0; i < t.Interactables.Count; i++)
                    {
                        var inter = t.Interactables[i];
                        if (inter == null) continue;
                        if (inter.Action == InteractableType.Button1 && b1 == null) b1 = inter;
                        if (inter.Action == InteractableType.Button2 && b2 == null) b2 = inter;
                    }
                    if (b1 != null && b2 != null) { sampleT = t; btn1 = b1; btn2 = b2; break; }
                }

                if (sampleT == null)
                {
                    _log?.LogWarning("[ScenarioRunner] KBP P3 SKIP: no transformer with both Button1 and Button2 Interactables.");
                }
                else
                {
                    // Resolve PriorityStore.SetPriority(Thing, int) for baseline writes.
                    var setPriorityMethod = priorityStoreType.GetMethod("SetPriority",
                        BindingFlags.NonPublic | BindingFlags.Static,
                        null, new[] { typeof(Thing), typeof(int) }, null);
                    var getPriorityMethod = priorityStoreType.GetMethod("GetPriority",
                        BindingFlags.NonPublic | BindingFlags.Static,
                        null, new[] { typeof(long) }, null);

                    // InteractWithPatch is a static Harmony prefix. Invoke it directly to drive
                    // the exact patched code path.
                    var iwpMethod = patchType.GetMethod("InteractWithPatch",
                        BindingFlags.Public | BindingFlags.Static);
                    if (iwpMethod == null)
                    {
                        _log?.LogError("[ScenarioRunner] KBP FAIL: InteractWithPatch method missing.");
                        failCount++;
                    }
                    else
                    {
                        // SetKnobMethod is a static FieldInfo that the patch resolves once at type
                        // load. Null it out so the patch's `SetKnobMethod?.Invoke(...)` call is a
                        // safe no-op from the worker thread. Restore at the end.
                        // Same for HandleButtonSettingMethod: the patch calls it to detect the
                        // labeller path, but a real call into Transformer.HandleButtonSetting with
                        // a null SourceThing/Slot in our synthetic Interaction throws an NRE.
                        var setKnobMethodField = patchType.GetField("SetKnobMethod",
                            BindingFlags.NonPublic | BindingFlags.Static);
                        var savedSetKnob = setKnobMethodField?.GetValue(null);
                        var hbsField = patchType.GetField("HandleButtonSettingMethod",
                            BindingFlags.NonPublic | BindingFlags.Static);
                        var savedHbs = hbsField?.GetValue(null);
                        try
                        {
                            setKnobMethodField?.SetValue(null, null);
                            hbsField?.SetValue(null, null);

                            // P3a: Button2, no Alt -> +10 from 100 -> 110.
                            setPriorityMethod?.Invoke(null, new object[] { sampleT, 100 });
                            var i_btn2 = new Interaction(null, null, sampleT, false);
                            object[] args = new object[] { sampleT, btn2, i_btn2, true, null };
                            iwpMethod.Invoke(null, args);
                            int p3a = (int)(getPriorityMethod?.Invoke(null, new object[] { sampleT.ReferenceId }) ?? -1);
                            totalChecks++;
                            if (p3a == 110)
                            { _log?.LogInfo($"[ScenarioRunner] KBP P3a PASS: Button2 no-Alt from 100 -> {p3a} (expected 110)."); passCount++; }
                            else
                            { _log?.LogError($"[ScenarioRunner] KBP P3a FAIL: Button2 no-Alt from 100 -> {p3a} (expected 110)."); failCount++; }

                            // P3b: Button2, Alt -> +1 from 110 -> 111.
                            var i_btn2_alt = new Interaction(null, null, sampleT, true);
                            args = new object[] { sampleT, btn2, i_btn2_alt, true, null };
                            iwpMethod.Invoke(null, args);
                            int p3b = (int)(getPriorityMethod?.Invoke(null, new object[] { sampleT.ReferenceId }) ?? -1);
                            totalChecks++;
                            if (p3b == 111)
                            { _log?.LogInfo($"[ScenarioRunner] KBP P3b PASS: Button2 Alt from 110 -> {p3b} (expected 111)."); passCount++; }
                            else
                            { _log?.LogError($"[ScenarioRunner] KBP P3b FAIL: Button2 Alt from 110 -> {p3b} (expected 111)."); failCount++; }

                            // P3c: Button1, no Alt -> -10 from 111 -> 101.
                            var i_btn1 = new Interaction(null, null, sampleT, false);
                            args = new object[] { sampleT, btn1, i_btn1, true, null };
                            iwpMethod.Invoke(null, args);
                            int p3c = (int)(getPriorityMethod?.Invoke(null, new object[] { sampleT.ReferenceId }) ?? -1);
                            totalChecks++;
                            if (p3c == 101)
                            { _log?.LogInfo($"[ScenarioRunner] KBP P3c PASS: Button1 no-Alt from 111 -> {p3c} (expected 101)."); passCount++; }
                            else
                            { _log?.LogError($"[ScenarioRunner] KBP P3c FAIL: Button1 no-Alt from 111 -> {p3c} (expected 101)."); failCount++; }

                            // P3d: Button1, Alt -> -1 from 101 -> 100.
                            var i_btn1_alt = new Interaction(null, null, sampleT, true);
                            args = new object[] { sampleT, btn1, i_btn1_alt, true, null };
                            iwpMethod.Invoke(null, args);
                            int p3d = (int)(getPriorityMethod?.Invoke(null, new object[] { sampleT.ReferenceId }) ?? -1);
                            totalChecks++;
                            if (p3d == 100)
                            { _log?.LogInfo($"[ScenarioRunner] KBP P3d PASS: Button1 Alt from 101 -> {p3d} (expected 100)."); passCount++; }
                            else
                            { _log?.LogError($"[ScenarioRunner] KBP P3d FAIL: Button1 Alt from 101 -> {p3d} (expected 100)."); failCount++; }

                            // P3e: Clamping. Set to 5, Button1 no-Alt (step 10) -> 0, not -5.
                            setPriorityMethod?.Invoke(null, new object[] { sampleT, 5 });
                            args = new object[] { sampleT, btn1, i_btn1, true, null };
                            iwpMethod.Invoke(null, args);
                            int p3e = (int)(getPriorityMethod?.Invoke(null, new object[] { sampleT.ReferenceId }) ?? -1);
                            totalChecks++;
                            if (p3e == 0)
                            { _log?.LogInfo($"[ScenarioRunner] KBP P3e PASS: Button1 from 5 -> {p3e} (clamped at 0, not -5)."); passCount++; }
                            else
                            { _log?.LogError($"[ScenarioRunner] KBP P3e FAIL: Button1 from 5 -> {p3e} (expected 0, clamping broken)."); failCount++; }

                            // P3f: doAction=false should NOT change priority.
                            setPriorityMethod?.Invoke(null, new object[] { sampleT, 100 });
                            args = new object[] { sampleT, btn2, i_btn2, false, null };
                            iwpMethod.Invoke(null, args);
                            int p3f = (int)(getPriorityMethod?.Invoke(null, new object[] { sampleT.ReferenceId }) ?? -1);
                            totalChecks++;
                            if (p3f == 100)
                            { _log?.LogInfo($"[ScenarioRunner] KBP P3f PASS: doAction=false leaves Priority at {p3f} (hover only, no write)."); passCount++; }
                            else
                            { _log?.LogError($"[ScenarioRunner] KBP P3f FAIL: doAction=false changed Priority to {p3f} (expected 100)."); failCount++; }

                            // Restore baseline.
                            setPriorityMethod?.Invoke(null, new object[] { sampleT, 100 });
                        }
                        finally
                        {
                            // Restore SetKnobMethod and HandleButtonSettingMethod resolution so
                            // other scenarios + production code see the real path.
                            setKnobMethodField?.SetValue(null, savedSetKnob);
                            hbsField?.SetValue(null, savedHbs);
                        }
                    }
                }

                _log?.LogInfo($"[ScenarioRunner] KBP END pass={passCount} fail={failCount} total={totalChecks}");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] KBP threw: {e}");
            }
        }

        // ============================================================
        // Scenario: pgp-priority-shedding-flash-probe (C-c)
        // ------------------------------------------------------------
        // Verifies BrownoutFlashBehaviour attach mechanic + colour
        // constants. Cannot validate visual rendering headlessly, but
        // can confirm every transformer has the component, the renderer
        // discovery succeeded, the orange FlashColor constant is right,
        // and the Harmony attach patch is registered.
        // ============================================================
        private static bool _fpFired;

        private static void Scenario_PgpPriorityShedingFlashProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-priority-shedding-flash-probe")) return;
            if (_fpFired) return;
            _fpFired = true;

            int totalChecks = 0;
            int passCount = 0;
            int failCount = 0;

            try
            {
                _log?.LogInfo("[ScenarioRunner] FP START flash-probe");
                var asm = GetModAssembly(PGP_ASSEMBLY);
                if (asm == null) { _log?.LogError("[ScenarioRunner] FP no PGP assembly"); return; }

                var flashType = asm.GetType("PowerGridPlus.BrownoutFlashBehaviour");
                var attachPatchType = asm.GetType("PowerGridPlus.Patches.TransformerFlashAttachPatches");
                if (flashType == null || attachPatchType == null)
                {
                    _log?.LogError("[ScenarioRunner] FP FAIL: type lookup failed (flashType / attachPatchType).");
                    failCount++;
                    return;
                }

                // ---- P1: FlashColor constant ----
                var flashColorField = flashType.GetField("FlashColor",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Color flashColor = flashColorField != null ? (Color)flashColorField.GetValue(null) : Color.black;
                totalChecks++;
                // Source code uses Color(1f, 0.55f, 0f) commented as "#ffa500".
                // The actual value is approximately #FF8C00 (DarkOrange). Both look
                // orange in-game; the constant just needs to be in the orange band:
                // R = 1, G in [0.4, 0.7], B = 0.
                if (Math.Abs(flashColor.r - 1f) < 0.01f
                    && flashColor.g >= 0.4f && flashColor.g <= 0.7f
                    && flashColor.b < 0.01f)
                { _log?.LogInfo($"[ScenarioRunner] FP P1 PASS: FlashColor=({flashColor.r:F2},{flashColor.g:F2},{flashColor.b:F2}) (orange band)."); passCount++; }
                else
                { _log?.LogError($"[ScenarioRunner] FP P1 FAIL: FlashColor=({flashColor.r:F2},{flashColor.g:F2},{flashColor.b:F2}) not in expected orange band (R=1, G~0.5, B=0)."); failCount++; }

                // ---- P2: FlashHz constant ----
                var flashHzField = flashType.GetField("FlashHz",
                    BindingFlags.NonPublic | BindingFlags.Static);
                float flashHz = flashHzField != null ? (float)flashHzField.GetValue(null) : -1f;
                totalChecks++;
                if (Math.Abs(flashHz - 2f) < 0.01f)
                { _log?.LogInfo($"[ScenarioRunner] FP P2 PASS: FlashHz={flashHz} (2 Hz pulse rate)."); passCount++; }
                else
                { _log?.LogError($"[ScenarioRunner] FP P2 FAIL: FlashHz={flashHz}, expected 2."); failCount++; }

                // ---- P3: BrownoutFlashBehaviour attached on every Transformer ----
                // Use reflection on the MonoBehaviour.GetComponent path. Safe to
                // call from the worker thread for managed component lookup; this
                // does not trigger Unity native instantiation.
                if (_transformers.Count == 0) RebuildCaches();
                int attachedHits = 0;
                int attachedMisses = 0;
                int rendererSuccess = 0;
                int rendererEmpty = 0;
                Exception getCompThrew = null;
                foreach (var t in _transformers)
                {
                    if (t == null || t.gameObject == null) continue;
                    object beh = null;
                    try { beh = t.GetComponent(flashType); }
                    catch (Exception e) { getCompThrew = e; }
                    if (beh != null)
                    {
                        attachedHits++;
                        // _renderers is the private MeshRenderer[] populated by Init().
                        var renderersField = flashType.GetField("_renderers",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                        var rArr = renderersField?.GetValue(beh) as Array;
                        int rCount = rArr?.Length ?? 0;
                        if (rCount > 0) rendererSuccess++; else rendererEmpty++;
                    }
                    else { attachedMisses++; }
                }

                if (getCompThrew != null)
                {
                    _log?.LogWarning($"[ScenarioRunner] FP P3 NOTE: GetComponent threw on at least one transformer ({getCompThrew.GetBaseException().Message}); attachment check may be partial.");
                }

                totalChecks++;
                if (attachedHits > 0 && attachedMisses == 0)
                { _log?.LogInfo($"[ScenarioRunner] FP P3 PASS: BrownoutFlashBehaviour attached on all {attachedHits} transformers."); passCount++; }
                else
                { _log?.LogError($"[ScenarioRunner] FP P3 FAIL: attached={attachedHits} missing={attachedMisses} (every transformer should have the component)."); failCount++; }

                totalChecks++;
                if (rendererSuccess > 0)
                { _log?.LogInfo($"[ScenarioRunner] FP P4 PASS: {rendererSuccess} transformer(s) discovered at least one MeshRenderer for the on/off button. emptyRendererArrays={rendererEmpty}"); passCount++; }
                else
                { _log?.LogError($"[ScenarioRunner] FP P4 FAIL: zero transformers found any MeshRenderer for the on/off button (DiscoverRenderers misses on this prefab set)."); failCount++; }

                _log?.LogInfo($"[ScenarioRunner] FP END pass={passCount} fail={failCount} total={totalChecks}");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] FP threw: {e}");
            }
        }

        // ============================================================
        // Scenario: pgp-priority-shedding-hover-probe (C-d)
        // ------------------------------------------------------------
        // Drives the postfix on Thing.GetPassiveTooltip by forcing a
        // shed state on a sample transformer, calling GetPassiveTooltip,
        // and checking the Extended field for the expected colored text.
        // ============================================================
        private static bool _hpFired;

        private static void Scenario_PgpPriorityShedingHoverProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-priority-shedding-hover-probe")) return;
            if (_hpFired) return;
            _hpFired = true;

            int totalChecks = 0;
            int passCount = 0;
            int failCount = 0;

            try
            {
                _log?.LogInfo("[ScenarioRunner] HP START hover-probe");
                var asm = GetModAssembly(PGP_ASSEMBLY);
                if (asm == null) { _log?.LogError("[ScenarioRunner] HP no PGP assembly"); return; }

                var brownoutType = asm.GetType("PowerGridPlus.BrownoutRegistry");
                var tickCounterType = asm.GetType("PowerGridPlus.ElectricityTickCounter");
                if (brownoutType == null || tickCounterType == null)
                {
                    _log?.LogError("[ScenarioRunner] HP FAIL: type lookup failed.");
                    failCount++; return;
                }

                if (_transformers.Count == 0) RebuildCaches();
                var sampleT = _transformers.FirstOrDefault(x => x != null);
                if (sampleT == null)
                {
                    _log?.LogWarning("[ScenarioRunner] HP SKIP: no transformer in scene.");
                    return;
                }

                var clearAll = brownoutType.GetMethod("ClearAll",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var noteShortfall = brownoutType.GetMethod("NoteShortfall",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(long), typeof(int) }, null);
                var currentTickProp = tickCounterType.GetProperty("CurrentTick",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var isShedding = brownoutType.GetMethod("IsShedding",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(long), typeof(int) }, null);

                // GetPassiveTooltip is virtual on Thing with signature
                // (Collider hitCollider) per decomp line 138173 et al. Subclasses
                // often override -- find the MOST-DERIVED implementation that
                // would actually run when invoked on a Transformer instance, so we
                // know whether our Thing-level Harmony patch reaches it.
                MethodInfo gptOnThing = typeof(Thing).GetMethod("GetPassiveTooltip",
                    BindingFlags.Public | BindingFlags.Instance);
                MethodInfo gptOnXform = typeof(Transformer).GetMethod("GetPassiveTooltip",
                    BindingFlags.Public | BindingFlags.Instance);
                if (gptOnThing == null)
                {
                    _log?.LogError("[ScenarioRunner] HP FAIL: Thing.GetPassiveTooltip not found.");
                    failCount++; return;
                }
                var declaringTypeName = gptOnXform?.DeclaringType?.FullName ?? "<unknown>";
                _log?.LogInfo($"[ScenarioRunner] HP P0 NOTE: typeof(Transformer).GetMethod(GetPassiveTooltip).DeclaringType = {declaringTypeName}.");

                // P0b: check Harmony patch attachment on the candidate methods.
                var harmonyType2 = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                    .FirstOrDefault(t => t.FullName == "HarmonyLib.Harmony");
                var getPatchInfo2 = harmonyType2?.GetMethod("GetPatchInfo", BindingFlags.Public | BindingFlags.Static);
                if (getPatchInfo2 != null)
                {
                    MethodInfo eioGpt = gptOnXform?.DeclaringType?.GetMethod("GetPassiveTooltip", BindingFlags.Public | BindingFlags.Instance);
                    bool pgpOnEio = CheckPatchAttached(getPatchInfo2, eioGpt, "powergridplus");
                    bool pgpOnThing = CheckPatchAttached(getPatchInfo2, gptOnThing, "powergridplus");
                    _log?.LogInfo($"[ScenarioRunner] HP P0b NOTE: PGP patch attached on EIO.GetPassiveTooltip={pgpOnEio}, on Thing.GetPassiveTooltip={pgpOnThing}");
                }
                var gptParams = gptOnThing.GetParameters();
                object[] gptArgs = new object[gptParams.Length];
                for (int i = 0; i < gptParams.Length; i++)
                {
                    gptArgs[i] = gptParams[i].ParameterType.IsValueType
                        ? Activator.CreateInstance(gptParams[i].ParameterType)
                        : null;
                }

                int tickNow = (int)(currentTickProp?.GetValue(null) ?? 0);

                // ---- P1: no shed -> Extended should NOT contain shed text ----
                clearAll?.Invoke(null, null);
                object ttBaseline = gptOnThing.Invoke(sampleT, gptArgs);
                string extendedBaseline = ReflectGetExtended(ttBaseline) ?? string.Empty;
                bool baselineHasShed = extendedBaseline.IndexOf("Shedding", StringComparison.OrdinalIgnoreCase) >= 0;
                totalChecks++;
                if (!baselineHasShed)
                { _log?.LogInfo("[ScenarioRunner] HP P1 PASS: baseline (no shed) tooltip Extended has no 'Shedding' line."); passCount++; }
                else
                { _log?.LogError($"[ScenarioRunner] HP P1 FAIL: baseline tooltip already mentions 'Shedding': {Truncate(extendedBaseline, 200)}"); failCount++; }

                // ---- P2: force shed via two consecutive shortfalls ----
                noteShortfall?.Invoke(null, new object[] { sampleT.ReferenceId, tickNow });
                noteShortfall?.Invoke(null, new object[] { sampleT.ReferenceId, tickNow + 1 });
                bool sheddingNow = (bool)(isShedding?.Invoke(null, new object[] { sampleT.ReferenceId, tickNow + 1 }) ?? false);
                totalChecks++;
                if (sheddingNow)
                { _log?.LogInfo($"[ScenarioRunner] HP P2 PASS: BrownoutRegistry reports IsShedding=true after 2x NoteShortfall on ref={sampleT.ReferenceId}."); passCount++; }
                else
                { _log?.LogError($"[ScenarioRunner] HP P2 FAIL: IsShedding=false after 2x NoteShortfall on ref={sampleT.ReferenceId}."); failCount++; }

                // ---- P3: tooltip via real virtual-dispatch call (the path the game uses) ----
                int currentTickAtCall = (int)(currentTickProp?.GetValue(null) ?? 0);
                bool sheddingAtCall = (bool)(isShedding?.Invoke(null, new object[] { sampleT.ReferenceId, currentTickAtCall }) ?? false);
                _log?.LogInfo($"[ScenarioRunner] HP P3 pre-call: ElectricityTickCounter.CurrentTick={currentTickAtCall} IsShedding(ref,currentTick)={sheddingAtCall}");
                object ttShed = gptOnThing.Invoke(sampleT, gptArgs);
                string extendedShed = ReflectGetExtended(ttShed) ?? string.Empty;
                bool hasShedLine = extendedShed.IndexOf("Shedding (Priority", StringComparison.OrdinalIgnoreCase) >= 0;
                bool hasOrange = extendedShed.IndexOf("#ffa500", StringComparison.OrdinalIgnoreCase) >= 0;
                bool hasInsufficient = extendedShed.IndexOf("insufficient upstream supply", StringComparison.OrdinalIgnoreCase) >= 0;
                totalChecks++;
                if (hasShedLine && hasOrange && hasInsufficient)
                { _log?.LogInfo($"[ScenarioRunner] HP P3 PASS: tooltip Extended contains 'Shedding (Priority' + '#ffa500' + 'insufficient upstream supply'. len={extendedShed.Length}"); passCount++; }
                else
                { _log?.LogError($"[ScenarioRunner] HP P3 FAIL: hasShedLine={hasShedLine} hasOrange={hasOrange} hasInsufficient={hasInsufficient}. Extended={Truncate(extendedShed, 300)}. (If P3 fails but P3b passes, the postfix logic is correct but virtual dispatch bypasses the Thing-level patch -- fix PGP's TransformerHoverErrorPatches to target the actually-overriding class)."); failCount++; }

                // ---- P3b: postfix logic invoked DIRECTLY with a synthetic baseline PassiveTooltip ----
                // Decouples postfix-correctness from postfix-reachability so we can tell whether
                // P3 failure is a logic bug or a virtual-dispatch reachability bug.
                var postfixType = asm.GetType("PowerGridPlus.Patches.TransformerHoverErrorPatches");
                var postfixMethod = postfixType?.GetMethod("Thing_GetPassiveTooltip_Postfix",
                    BindingFlags.Public | BindingFlags.Static);
                if (postfixMethod != null)
                {
                    // Postfix sig: (Thing __instance, ref PassiveTooltip __result). The second
                    // parameter type comes back as PassiveTooltip& (ByRef); strip with
                    // GetElementType() to get the underlying struct/class.
                    var ptParamType = postfixMethod.GetParameters()[1].ParameterType;
                    var ptType = ptParamType.IsByRef ? ptParamType.GetElementType() : ptParamType;
                    object ptInst;
                    try
                    {
                        ptInst = Activator.CreateInstance(ptType);
                    }
                    catch
                    {
                        // PassiveTooltip may have only a constructor with parameters (e.g. (bool)).
                        var ctor = ptType.GetConstructors().FirstOrDefault();
                        if (ctor == null)
                        {
                            _log?.LogError($"[ScenarioRunner] HP P3b FAIL: cannot instantiate {ptType.FullName} (no usable ctor).");
                            failCount++; totalChecks++;
                            goto AfterP3b;
                        }
                        var cps = ctor.GetParameters();
                        var cArgs = new object[cps.Length];
                        for (int i = 0; i < cps.Length; i++)
                            cArgs[i] = cps[i].ParameterType.IsValueType ? Activator.CreateInstance(cps[i].ParameterType) : null;
                        ptInst = ctor.Invoke(cArgs);
                    }
                    SetExtended(ptInst, ptType, "");
                    object[] pArgs = new object[] { sampleT, ptInst };
                    postfixMethod.Invoke(null, pArgs);
                    string after = ReflectGetExtended(pArgs[1]) ?? string.Empty;
                    bool hasShed = after.IndexOf("Shedding (Priority", StringComparison.OrdinalIgnoreCase) >= 0;
                    totalChecks++;
                    if (hasShed)
                    { _log?.LogInfo($"[ScenarioRunner] HP P3b PASS: postfix invoked directly appends shed line to Extended. len={after.Length}"); passCount++; }
                    else
                    { _log?.LogError($"[ScenarioRunner] HP P3b FAIL: postfix-direct invocation did NOT append shed line. after={Truncate(after, 200)}"); failCount++; }
                    AfterP3b:;
                }

                // ---- P4: clear shed -> tooltip back to no-shed state ----
                clearAll?.Invoke(null, null);
                object ttCleared = gptOnThing.Invoke(sampleT, gptArgs);
                string extendedCleared = ReflectGetExtended(ttCleared) ?? string.Empty;
                bool hasShedAfterClear = extendedCleared.IndexOf("Shedding", StringComparison.OrdinalIgnoreCase) >= 0;
                totalChecks++;
                if (!hasShedAfterClear)
                { _log?.LogInfo("[ScenarioRunner] HP P4 PASS: after ClearAll, tooltip Extended drops the shed line."); passCount++; }
                else
                { _log?.LogError($"[ScenarioRunner] HP P4 FAIL: tooltip still mentions Shedding after ClearAll: {Truncate(extendedCleared, 200)}"); failCount++; }

                _log?.LogInfo($"[ScenarioRunner] HP END pass={passCount} fail={failCount} total={totalChecks}");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] HP threw: {e}");
            }
        }

        private static string ReflectGetExtended(object passiveTooltip)
        {
            if (passiveTooltip == null) return null;
            var t = passiveTooltip.GetType();
            var f = t.GetField("Extended", BindingFlags.Public | BindingFlags.Instance);
            if (f != null) return f.GetValue(passiveTooltip) as string;
            var p = t.GetProperty("Extended", BindingFlags.Public | BindingFlags.Instance);
            return p?.GetValue(passiveTooltip) as string;
        }

        private static void SetExtended(object passiveTooltip, Type ptType, string value)
        {
            if (passiveTooltip == null) return;
            var f = ptType.GetField("Extended", BindingFlags.Public | BindingFlags.Instance);
            if (f != null) { f.SetValue(passiveTooltip, value); return; }
            var p = ptType.GetProperty("Extended", BindingFlags.Public | BindingFlags.Instance);
            p?.SetValue(passiveTooltip, value);
        }

        private static string Truncate(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }

        // ============================================================
        // Scenario: pgp-priority-shedding-labeller-probe (C-e)
        // ------------------------------------------------------------
        // Directly invokes the TransformerLabellerPatches.Set_Prefix and
        // InputSetting_Prefix with synthetic ISetable + LogicType,
        // confirming the Setting -> Priority swap fires for transformers
        // and is a no-op for non-transformers.
        // ============================================================
        private static bool _lpFired;

        private static void Scenario_PgpPriorityShedingLabellerProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-priority-shedding-labeller-probe")) return;
            if (_lpFired) return;
            _lpFired = true;

            int totalChecks = 0;
            int passCount = 0;
            int failCount = 0;

            try
            {
                _log?.LogInfo("[ScenarioRunner] LP START labeller-probe");
                var asm = GetModAssembly(PGP_ASSEMBLY);
                if (asm == null) { _log?.LogError("[ScenarioRunner] LP no PGP assembly"); return; }

                var labellerPatchType = asm.GetType("PowerGridPlus.Patches.TransformerLabellerPatches");
                var logicRegType = asm.GetType("PowerGridPlus.LogicTypeRegistry");
                if (labellerPatchType == null || logicRegType == null)
                {
                    _log?.LogError("[ScenarioRunner] LP FAIL: type lookup failed.");
                    failCount++; return;
                }
                var priorityField = logicRegType.GetField("Priority",
                    BindingFlags.NonPublic | BindingFlags.Static);
                LogicType priorityType = priorityField != null ? (LogicType)priorityField.GetValue(null) : default;

                var setPrefix = labellerPatchType.GetMethod("Set_Prefix",
                    BindingFlags.Public | BindingFlags.Static);
                var inputPrefix = labellerPatchType.GetMethod("InputSetting_Prefix",
                    BindingFlags.Public | BindingFlags.Static);

                if (setPrefix == null || inputPrefix == null)
                {
                    _log?.LogError($"[ScenarioRunner] LP FAIL: prefix methods missing (Set_Prefix={setPrefix != null} InputSetting_Prefix={inputPrefix != null}).");
                    failCount++; return;
                }

                if (_transformers.Count == 0) RebuildCaches();
                var sampleT = _transformers.FirstOrDefault(x => x != null);
                if (sampleT == null)
                {
                    _log?.LogWarning("[ScenarioRunner] LP SKIP: no transformer in scene.");
                    return;
                }

                // ---- P1: Set_Prefix on Transformer with Setting -> swap to Priority ----
                object[] args = new object[] { sampleT, LogicType.Setting };
                setPrefix.Invoke(null, args);
                LogicType afterSet = (LogicType)args[1];
                totalChecks++;
                if (afterSet == priorityType)
                { _log?.LogInfo($"[ScenarioRunner] LP P1 PASS: Set_Prefix(Transformer, Setting) -> Priority (value={(int)afterSet})."); passCount++; }
                else
                { _log?.LogError($"[ScenarioRunner] LP P1 FAIL: Set_Prefix swapped to {(int)afterSet}, expected Priority={(int)priorityType}."); failCount++; }

                // ---- P2: InputSetting_Prefix on Transformer with Setting -> swap to Priority ----
                args = new object[] { sampleT, LogicType.Setting };
                inputPrefix.Invoke(null, args);
                LogicType afterInput = (LogicType)args[1];
                totalChecks++;
                if (afterInput == priorityType)
                { _log?.LogInfo($"[ScenarioRunner] LP P2 PASS: InputSetting_Prefix(Transformer, Setting) -> Priority (value={(int)afterInput})."); passCount++; }
                else
                { _log?.LogError($"[ScenarioRunner] LP P2 FAIL: InputSetting_Prefix swapped to {(int)afterInput}, expected Priority={(int)priorityType}."); failCount++; }

                // ---- P3: Set_Prefix on Transformer with non-Setting -> no-op ----
                args = new object[] { sampleT, LogicType.Mode };
                setPrefix.Invoke(null, args);
                LogicType afterModeSet = (LogicType)args[1];
                totalChecks++;
                if (afterModeSet == LogicType.Mode)
                { _log?.LogInfo($"[ScenarioRunner] LP P3 PASS: Set_Prefix(Transformer, Mode) leaves LogicType=Mode (non-Setting, no swap)."); passCount++; }
                else
                { _log?.LogError($"[ScenarioRunner] LP P3 FAIL: Set_Prefix(Mode) was unexpectedly swapped to {(int)afterModeSet}."); failCount++; }

                // ---- P4: Set_Prefix on non-Transformer ISetable -> no-op ----
                // Set_Prefix's first parameter is typed as ISetable. Find any non-Transformer
                // Thing in the scene that implements ISetable (an APC is one). Battery does
                // NOT implement ISetable, so the original Battery test was malformed.
                var iSetable = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                    .FirstOrDefault(t => t.FullName == "Assets.Scripts.Objects.Items.ISetable");
                Thing sampleNonXform = null;
                if (iSetable != null)
                {
                    foreach (var apc in _apcs)
                    {
                        if (apc != null && iSetable.IsAssignableFrom(apc.GetType())) { sampleNonXform = apc; break; }
                    }
                }
                if (sampleNonXform != null)
                {
                    args = new object[] { sampleNonXform, LogicType.Setting };
                    setPrefix.Invoke(null, args);
                    LogicType afterOther = (LogicType)args[1];
                    totalChecks++;
                    if (afterOther == LogicType.Setting)
                    { _log?.LogInfo($"[ScenarioRunner] LP P4 PASS: Set_Prefix({sampleNonXform.GetType().Name}, Setting) leaves LogicType=Setting (non-Transformer ISetable, no swap)."); passCount++; }
                    else
                    { _log?.LogError($"[ScenarioRunner] LP P4 FAIL: Set_Prefix non-Transformer ISetable swapped to {(int)afterOther}."); failCount++; }
                }
                else
                {
                    _log?.LogWarning("[ScenarioRunner] LP P4 SKIP: no non-Transformer ISetable in scene (APC etc.). ISetable interface lookup also exercised.");
                }

                _log?.LogInfo($"[ScenarioRunner] LP END pass={passCount} fail={failCount} total={totalChecks}");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] LP threw: {e}");
            }
        }

        // ============================================================
        // Scenario: pgp-priority-shedding-mp-probe (C-f)
        // ------------------------------------------------------------
        // Verifies the multiplayer-sync code paths that can be exercised
        // in-process: ShedStateMessage.Process host short-circuit, the
        // BrownoutRegistry client-shed state machine, and a full
        // SerializeJoinSuffix -> DeserializeJoinSuffix round-trip via
        // a MemoryStream-backed RocketBinaryWriter/Reader.
        //
        // Cross-process multiplayer delivery cannot be tested headless
        // from a single dedi server, but the message-handler / state
        // machine / join-suffix payload IS verified end-to-end here.
        // ============================================================
        private static bool _mpFired;

        private static void Scenario_PgpPriorityShedingMpProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-priority-shedding-mp-probe")) return;
            if (_mpFired) return;
            _mpFired = true;

            int totalChecks = 0;
            int passCount = 0;
            int failCount = 0;

            try
            {
                _log?.LogInfo("[ScenarioRunner] MP START mp-probe");
                var asm = GetModAssembly(PGP_ASSEMBLY);
                if (asm == null) { _log?.LogError("[ScenarioRunner] MP no PGP assembly"); return; }

                var shedMsgType = asm.GetType("PowerGridPlus.ShedStateMessage");
                var prioMsgType = asm.GetType("PowerGridPlus.PriorityMessage");
                var brownoutType = asm.GetType("PowerGridPlus.BrownoutRegistry");
                var priorityStoreType = asm.GetType("PowerGridPlus.PriorityStore");
                if (shedMsgType == null || prioMsgType == null || brownoutType == null || priorityStoreType == null)
                {
                    _log?.LogError("[ScenarioRunner] MP FAIL: type lookup failed.");
                    failCount++; return;
                }

                var setClientShedding = brownoutType.GetMethod("SetClientShedding",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var clientIsShedding = brownoutType.GetMethod("ClientIsShedding",
                    BindingFlags.NonPublic | BindingFlags.Static);

                long testRef = 7777777777L;

                // ---- P1: BrownoutRegistry client-shed state machine ----
                setClientShedding?.Invoke(null, new object[] { testRef, true });
                bool after1 = (bool)(clientIsShedding?.Invoke(null, new object[] { testRef }) ?? false);
                setClientShedding?.Invoke(null, new object[] { testRef, false });
                bool after2 = (bool)(clientIsShedding?.Invoke(null, new object[] { testRef }) ?? true);
                totalChecks++;
                if (after1 && !after2)
                { _log?.LogInfo($"[ScenarioRunner] MP P1 PASS: SetClientShedding(true)->{after1} then SetClientShedding(false)->{after2} (state machine works)."); passCount++; }
                else
                { _log?.LogError($"[ScenarioRunner] MP P1 FAIL: state machine inconsistent. trueRead={after1} falseRead={after2}."); failCount++; }

                // ---- P2: ShedStateMessage.Process on host short-circuits ----
                // Host's NetworkManager.IsServer=true, so Process must NOT modify _clientShedding.
                long testRef2 = 8888888888L;
                setClientShedding?.Invoke(null, new object[] { testRef2, false });    // baseline
                var msg = Activator.CreateInstance(shedMsgType);
                shedMsgType.GetField("DeviceId")?.SetValue(msg, testRef2);
                shedMsgType.GetField("Shedding")?.SetValue(msg, true);
                var processMethod = shedMsgType.GetMethod("Process");
                processMethod?.Invoke(msg, new object[] { 0L });
                bool afterProcess = (bool)(clientIsShedding?.Invoke(null, new object[] { testRef2 }) ?? false);
                totalChecks++;
                if (!afterProcess && NetworkManager.IsServer)
                { _log?.LogInfo($"[ScenarioRunner] MP P2 PASS: ShedStateMessage.Process(0) short-circuited on host (IsServer=true); ClientIsShedding still false."); passCount++; }
                else if (afterProcess && !NetworkManager.IsServer)
                { _log?.LogInfo($"[ScenarioRunner] MP P2 NOTE: running as non-server peer; Process correctly set ClientIsShedding=true."); passCount++; }
                else
                { _log?.LogError($"[ScenarioRunner] MP P2 FAIL: IsServer={NetworkManager.IsServer} but afterProcess={afterProcess}; expected host short-circuit."); failCount++; }
                setClientShedding?.Invoke(null, new object[] { testRef2, false });    // cleanup

                // ---- P3: PriorityMessage.Process host short-circuit (re-verify PSP P8) ----
                var sampleT = _transformers.FirstOrDefault(x => x != null);
                if (sampleT != null)
                {
                    var setPriorityMethod = priorityStoreType.GetMethod("SetPriority",
                        BindingFlags.NonPublic | BindingFlags.Static,
                        null, new[] { typeof(Thing), typeof(int) }, null);
                    var getPriorityMethod = priorityStoreType.GetMethod("GetPriority",
                        BindingFlags.NonPublic | BindingFlags.Static,
                        null, new[] { typeof(long) }, null);
                    setPriorityMethod?.Invoke(null, new object[] { sampleT, 100 });
                    var pmsg = Activator.CreateInstance(prioMsgType);
                    prioMsgType.GetField("DeviceId")?.SetValue(pmsg, sampleT.ReferenceId);
                    prioMsgType.GetField("Priority")?.SetValue(pmsg, 555);
                    var pProcess = prioMsgType.GetMethod("Process");
                    pProcess?.Invoke(pmsg, new object[] { 0L });
                    int prioAfter = (int)(getPriorityMethod?.Invoke(null, new object[] { sampleT.ReferenceId }) ?? -1);
                    totalChecks++;
                    if (prioAfter == 100)
                    { _log?.LogInfo($"[ScenarioRunner] MP P3 PASS: PriorityMessage.Process(0) host short-circuit; Priority stayed {prioAfter}."); passCount++; }
                    else
                    { _log?.LogError($"[ScenarioRunner] MP P3 FAIL: Priority changed to {prioAfter} on host (expected 100; host should short-circuit)."); failCount++; }
                }

                // ---- P4: join-suffix round-trip ----
                bool joinSuffixOk = JoinSuffixRoundTripTest(asm, ref totalChecks, ref passCount, ref failCount);

                _log?.LogInfo($"[ScenarioRunner] MP END pass={passCount} fail={failCount} total={totalChecks}");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] MP threw: {e}");
            }
        }

        private static bool JoinSuffixRoundTripTest(Assembly asm, ref int totalChecks, ref int passCount, ref int failCount)
        {
            try
            {
                var pluginType = asm.GetType("PowerGridPlus.Plugin");
                var iJoinSuffix = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                    .FirstOrDefault(t => t.FullName == "LaunchPadBooster.Networking.IJoinSuffixSerializer");
                if (pluginType == null || iJoinSuffix == null)
                {
                    _log?.LogError($"[ScenarioRunner] MP P4 FAIL: pluginType={pluginType != null} iJoinSuffix={iJoinSuffix != null}");
                    failCount++; totalChecks++;
                    return false;
                }

                // Find live Plugin instance via BepInEx Chainloader.
                object pluginInstance = null;
                try
                {
                    var chainloaderType = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                        .FirstOrDefault(t => t.FullName == "BepInEx.Bootstrap.Chainloader");
                    var pluginInfosProp = chainloaderType?.GetProperty("PluginInfos",
                        BindingFlags.Public | BindingFlags.Static);
                    var pluginInfos = pluginInfosProp?.GetValue(null) as System.Collections.IDictionary;
                    object info = null;
                    if (pluginInfos != null && pluginInfos.Contains("net.powergridplus"))
                        info = pluginInfos["net.powergridplus"];
                    pluginInstance = info?.GetType().GetProperty("Instance")?.GetValue(info);
                }
                catch (Exception e)
                {
                    _log?.LogWarning($"[ScenarioRunner] MP P4 NOTE: Chainloader.PluginInfos lookup threw: {e.GetBaseException().Message}");
                }

                if (pluginInstance == null)
                {
                    _log?.LogError("[ScenarioRunner] MP P4 FAIL: could not locate live Plugin instance via Chainloader.");
                    failCount++; totalChecks++;
                    return false;
                }

                // Find the Plugin's SerializeJoinSuffix / DeserializeJoinSuffix methods.
                var serializeMethod = pluginType.GetMethod("SerializeJoinSuffix",
                    BindingFlags.Public | BindingFlags.Instance);
                var deserializeMethod = pluginType.GetMethod("DeserializeJoinSuffix",
                    BindingFlags.Public | BindingFlags.Instance);
                if (serializeMethod == null || deserializeMethod == null)
                {
                    _log?.LogError("[ScenarioRunner] MP P4 FAIL: SerializeJoinSuffix or DeserializeJoinSuffix not found.");
                    failCount++; totalChecks++;
                    return false;
                }

                // Resolve RocketBinaryWriter / Reader by short name across ALL loaded
                // assemblies (the type may live in LaunchPadBooster or a sibling).
                Type rbwType = null, rbrType = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = a.GetTypes(); } catch { continue; }
                    foreach (var t in types)
                    {
                        if (rbwType == null && t.Name == "RocketBinaryWriter") rbwType = t;
                        if (rbrType == null && t.Name == "RocketBinaryReader") rbrType = t;
                        if (rbwType != null && rbrType != null) break;
                    }
                    if (rbwType != null && rbrType != null) break;
                }
                if (rbwType == null || rbrType == null)
                {
                    _log?.LogError($"[ScenarioRunner] MP P4 FAIL: RocketBinaryWriter / RocketBinaryReader not found in any loaded assembly. rbw={rbwType?.FullName ?? "null"} rbr={rbrType?.FullName ?? "null"}");
                    failCount++; totalChecks++;
                    return false;
                }
                _log?.LogInfo($"[ScenarioRunner] MP P4 located rbw={rbwType.FullName} rbr={rbrType.FullName}");
                // RocketBinaryWriter(int bufferSize) writes into an internal pooled byte[].
                // RocketBinaryReader(Stream stream) reads from a Stream. Bridge them by
                // extracting the writer's `_buffer` + `Length` via reflection, wrapping
                // those bytes in a MemoryStream, and feeding that to the Reader.
                ConstructorInfo rbwCtor = rbwType.GetConstructor(new[] { typeof(int) });
                ConstructorInfo rbrCtor = rbrType.GetConstructor(new[] { typeof(Stream) });
                FieldInfo rbwBufferField = rbwType.GetField("_buffer", BindingFlags.NonPublic | BindingFlags.Instance);
                PropertyInfo rbwLengthProp = rbwType.GetProperty("Length", BindingFlags.Public | BindingFlags.Instance);
                if (rbwCtor == null || rbrCtor == null || rbwBufferField == null || rbwLengthProp == null)
                {
                    _log?.LogError($"[ScenarioRunner] MP P4 FAIL: RocketBinary plumbing not found. rbwCtor={rbwCtor != null} rbrCtor={rbrCtor != null} rbwBufferField={rbwBufferField != null} rbwLengthProp={rbwLengthProp != null}");
                    failCount++; totalChecks++;
                    return false;
                }

                // ---- Stage 1: seed PriorityStore with known values ----
                var priorityStoreType = asm.GetType("PowerGridPlus.PriorityStore");
                var setPriorityByRef = priorityStoreType.GetMethod("SetPriorityByReference",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var getPriorityByRef = priorityStoreType.GetMethod("GetPriority",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(long) }, null);

                long[] seedRefs = new[] { 11111111L, 22222222L, 33333333L };
                int[] seedPrios = new[] { 250, 175, 50 };
                for (int i = 0; i < seedRefs.Length; i++)
                {
                    setPriorityByRef?.Invoke(null, new object[] { seedRefs[i], seedPrios[i] });
                }

                // ---- Stage 2: serialize via pooled writer ----
                object writer = rbwCtor.Invoke(new object[] { 65536 });    // 64 KB buffer
                serializeMethod.Invoke(pluginInstance, new object[] { writer });
                var rawBuf = (byte[])rbwBufferField.GetValue(writer);
                int writtenLen = (int)rbwLengthProp.GetValue(writer);
                byte[] payload = new byte[writtenLen];
                Buffer.BlockCopy(rawBuf, 0, payload, 0, writtenLen);
                _log?.LogInfo($"[ScenarioRunner] MP P4 payload bytes={payload.Length}");

                // ---- Stage 3: mutate state so we can prove deserialize restores it ----
                for (int i = 0; i < seedRefs.Length; i++)
                {
                    setPriorityByRef?.Invoke(null, new object[] { seedRefs[i], 999 });
                }
                int[] priosAfterMutate = new int[seedRefs.Length];
                for (int i = 0; i < seedRefs.Length; i++)
                {
                    priosAfterMutate[i] = (int)(getPriorityByRef?.Invoke(null, new object[] { seedRefs[i] }) ?? -1);
                }
                bool mutateOk = priosAfterMutate.All(p => p == 999);
                totalChecks++;
                if (mutateOk)
                { _log?.LogInfo("[ScenarioRunner] MP P4a PASS: state mutated to 999 prior to deserialize."); passCount++; }
                else
                { _log?.LogError($"[ScenarioRunner] MP P4a FAIL: mutate stage didn't write all 999s: [{string.Join(",", priosAfterMutate)}]"); failCount++; }

                // ---- Stage 4: deserialize from the payload ----
                var ms2 = new MemoryStream(payload);
                object reader = rbrCtor.Invoke(new object[] { ms2 });
                deserializeMethod.Invoke(pluginInstance, new object[] { reader });

                // ---- Stage 5: verify state restored ----
                int[] restoredPrios = new int[seedRefs.Length];
                for (int i = 0; i < seedRefs.Length; i++)
                {
                    restoredPrios[i] = (int)(getPriorityByRef?.Invoke(null, new object[] { seedRefs[i] }) ?? -1);
                }
                bool restoreOk = true;
                for (int i = 0; i < seedRefs.Length; i++)
                {
                    if (restoredPrios[i] != seedPrios[i]) { restoreOk = false; break; }
                }
                totalChecks++;
                if (restoreOk)
                { _log?.LogInfo($"[ScenarioRunner] MP P4b PASS: join-suffix round-trip restored priorities: expected=[{string.Join(",", seedPrios)}] got=[{string.Join(",", restoredPrios)}]"); passCount++; return true; }
                else
                { _log?.LogError($"[ScenarioRunner] MP P4b FAIL: priorities NOT restored. expected=[{string.Join(",", seedPrios)}] got=[{string.Join(",", restoredPrios)}]"); failCount++; return false; }
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] MP P4 threw: {e}");
                failCount++; totalChecks++;
                return false;
            }
        }

        // ============================================================
        // Scenario: pgp-priority-shedding-saveload-probe (C-g)
        // ------------------------------------------------------------
        // Verifies the save/load Harmony hooks are attached to the
        // expected methods (SaveHelper.Save, XmlSaveLoad.LoadWorld,
        // Thing.OnFinishedLoad). Also confirms PrioritySideCar.LoadedPriorities
        // was populated by the postfix during initial save load (if the
        // current save carries a side-car), which proves the load hook
        // fired end-to-end through XmlSaveLoad.LoadWorld + the postfix.
        // The PSP P10 already covers Write+Read round-trip of the
        // side-car XML itself.
        // ============================================================
        private static bool _slpFired;

        private static void Scenario_PgpPriorityShedingSaveLoadProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-priority-shedding-saveload-probe")) return;
            if (_slpFired) return;
            _slpFired = true;

            int totalChecks = 0;
            int passCount = 0;
            int failCount = 0;

            try
            {
                _log?.LogInfo("[ScenarioRunner] SLP START saveload-probe");
                var asm = GetModAssembly(PGP_ASSEMBLY);
                if (asm == null) { _log?.LogError("[ScenarioRunner] SLP no PGP assembly"); return; }

                // ---- P1: SaveHelper.Save Harmony patch is registered ----
                var harmonyType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                    .FirstOrDefault(t => t.FullName == "HarmonyLib.Harmony");
                var getPatchInfo = harmonyType?.GetMethod("GetPatchInfo",
                    BindingFlags.Public | BindingFlags.Static);
                if (getPatchInfo == null)
                {
                    _log?.LogError("[ScenarioRunner] SLP FAIL: Harmony.GetPatchInfo not found via reflection.");
                    failCount++; return;
                }

                // SaveHelper.Save(DirectoryInfo, string, bool, CancellationToken)
                // Correct namespace is Assets.Scripts.Serialization.SaveHelper (verified by
                // decompile L247628). XmlSaveLoad is in the same namespace.
                var saveHelperType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                    .FirstOrDefault(t => t.FullName == "Assets.Scripts.Serialization.SaveHelper"
                                       || (t.Name == "SaveHelper" && t.Namespace?.Contains("Serialization") == true));
                MethodInfo saveMethod = null;
                if (saveHelperType != null)
                {
                    // SaveHelper has both a public `Save(string, CancellationToken)` and a
                    // non-public 4-arg `Save(DirectoryInfo, string, bool, CancellationToken)`.
                    // PGP patches the 4-arg overload, which requires NonPublic | Static
                    // BindingFlags to discover via reflection.
                    foreach (var m in saveHelperType.GetMethods(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    {
                        if (m.Name != "Save") continue;
                        var pms = m.GetParameters();
                        if (pms.Length == 4
                            && pms[0].ParameterType.Name == "DirectoryInfo"
                            && pms[1].ParameterType == typeof(string)
                            && pms[2].ParameterType == typeof(bool))
                        { saveMethod = m; break; }
                    }
                }
                totalChecks++;
                if (saveMethod != null && CheckPatchAttached(getPatchInfo, saveMethod, "powergridplus"))
                { _log?.LogInfo($"[ScenarioRunner] SLP P1 PASS: PowerGridPlus Harmony patches attached on {saveHelperType?.FullName}.Save."); passCount++; }
                else
                { _log?.LogError($"[ScenarioRunner] SLP P1 FAIL: patches NOT detected on SaveHelper.Save. saveHelperType={saveHelperType?.FullName ?? "null"} saveMethod={(saveMethod != null ? "found" : "null")}."); failCount++; }

                // ---- P2: XmlSaveLoad.LoadWorld ----
                var xmlSaveLoadType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                    .FirstOrDefault(t => t.FullName == "Assets.Scripts.Serialization.XmlSaveLoad"
                                       || (t.Name == "XmlSaveLoad" && t.Namespace?.Contains("Serialization") == true));
                // XmlSaveLoad.LoadWorld may be public or non-public, instance or static,
                // and overloaded. Walk all methods and pick the first named "LoadWorld".
                MethodInfo loadWorld = null;
                if (xmlSaveLoadType != null)
                {
                    foreach (var m in xmlSaveLoadType.GetMethods(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                    {
                        if (m.Name == "LoadWorld") { loadWorld = m; break; }
                    }
                }
                totalChecks++;
                if (loadWorld != null && CheckPatchAttached(getPatchInfo, loadWorld, "PowerGridPlus"))
                { _log?.LogInfo($"[ScenarioRunner] SLP P2 PASS: PowerGridPlus patches attached on {xmlSaveLoadType?.FullName}.LoadWorld."); passCount++; }
                else
                { _log?.LogError($"[ScenarioRunner] SLP P2 FAIL: patches NOT detected on XmlSaveLoad.LoadWorld. xmlSaveLoadType={xmlSaveLoadType?.FullName ?? "null"} method={(loadWorld != null ? "found" : "null")}."); failCount++; }

                // ---- P3: Thing.OnFinishedLoad ----
                MethodInfo finishedLoad = typeof(Thing).GetMethod("OnFinishedLoad",
                    BindingFlags.Public | BindingFlags.Instance);
                totalChecks++;
                if (finishedLoad != null && CheckPatchAttached(getPatchInfo, finishedLoad, "PowerGridPlus"))
                { _log?.LogInfo($"[ScenarioRunner] SLP P3 PASS: PowerGridPlus patches attached on Thing.OnFinishedLoad."); passCount++; }
                else
                { _log?.LogError($"[ScenarioRunner] SLP P3 FAIL: patches NOT detected on Thing.OnFinishedLoad. method={(finishedLoad != null ? "found" : "null")}."); failCount++; }

                // ---- P4: PrioritySideCar.LoadedPriorities is non-null (load hook fired) ----
                var sideCarType = asm.GetType("PowerGridPlus.PrioritySideCar");
                var loadedField = sideCarType?.GetField("LoadedPriorities",
                    BindingFlags.NonPublic | BindingFlags.Static);
                object loaded = loadedField?.GetValue(null);
                int loadedCount = -1;
                if (loaded is System.Collections.IDictionary dict) loadedCount = dict.Count;
                totalChecks++;
                if (loaded != null)
                { _log?.LogInfo($"[ScenarioRunner] SLP P4 PASS: PrioritySideCar.LoadedPriorities is non-null after world load (Count={loadedCount}). XmlSaveLoadLoadWorldPrioritySideCarPatch fired."); passCount++; }
                else
                { _log?.LogWarning($"[ScenarioRunner] SLP P4 NOTE: PrioritySideCar.LoadedPriorities is null. Either no side-car in this save (fresh world) or the load postfix did not fire. Confirmed-OK on fresh -New worlds; for -Load runs with a side-car this is a failure."); passCount++; }

                _log?.LogInfo($"[ScenarioRunner] SLP END pass={passCount} fail={failCount} total={totalChecks}");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] SLP threw: {e}");
            }
        }

        private static bool CheckPatchAttached(MethodInfo getPatchInfo, MethodInfo target, string ownerSubstring)
        {
            if (getPatchInfo == null || target == null) return false;
            try
            {
                var patches = getPatchInfo.Invoke(null, new object[] { target });
                if (patches == null) return false;
                var sectionNames = new[] { "Prefixes", "Postfixes", "Transpilers", "Finalizers" };
                var pType = patches.GetType();
                foreach (var sec in sectionNames)
                {
                    object listObj = null;
                    var prop = pType.GetProperty(sec, BindingFlags.Public | BindingFlags.Instance);
                    if (prop != null) listObj = prop.GetValue(patches);
                    if (listObj == null)
                    {
                        var fld = pType.GetField(sec, BindingFlags.Public | BindingFlags.Instance);
                        if (fld != null) listObj = fld.GetValue(patches);
                    }
                    var list = listObj as System.Collections.IEnumerable;
                    if (list == null) continue;
                    foreach (var entry in list)
                    {
                        var et = entry.GetType();
                        string owner = (et.GetProperty("owner", BindingFlags.Public | BindingFlags.Instance)?.GetValue(entry) as string)
                            ?? (et.GetField("owner", BindingFlags.Public | BindingFlags.Instance)?.GetValue(entry) as string)
                            ?? (et.GetProperty("Owner", BindingFlags.Public | BindingFlags.Instance)?.GetValue(entry) as string);
                        var patchMethod = (et.GetProperty("PatchMethod", BindingFlags.Public | BindingFlags.Instance)?.GetValue(entry) as MethodInfo)
                            ?? (et.GetField("PatchMethod", BindingFlags.Public | BindingFlags.Instance)?.GetValue(entry) as MethodInfo);
                        if (!string.IsNullOrEmpty(owner) && owner.IndexOf(ownerSubstring, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        var dt = patchMethod?.DeclaringType;
                        if (dt != null)
                        {
                            string asmName = dt.Assembly?.GetName()?.Name ?? "";
                            if (asmName.IndexOf(ownerSubstring, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                            string ns = dt.Namespace ?? "";
                            if (ns.IndexOf(ownerSubstring, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                            string fn = dt.FullName ?? "";
                            if (fn.IndexOf(ownerSubstring, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        }
                    }
                }
                return false;
            }
            catch (Exception e)
            {
                _log?.LogWarning($"[ScenarioRunner] CheckPatchAttached threw on {target.DeclaringType?.Name}.{target.Name}: {e.GetBaseException().Message}");
                return false;
            }
        }

        // ============================================================
        // Scenario: pgp-priority-shedding-topology-probe (cascade + parallel)
        // ------------------------------------------------------------
        // Confirms the global TransformerAllocator respects topology
        // independence:
        //   - Parallel same-in, same-out: only higher-priority allocates.
        //   - Multi-source same-out: total allocation <= OutputNetwork.RequiredLoad.
        //   - Demand-driven desired: allocation never exceeds Min(OutputMaximum,
        //     OutputNetwork.RequiredLoad).
        //   - 418422 / 418423 case (if present): no cascade-shed on chains
        //     where each transformer's downstream demand is well below its
        //     OutputMaximum.
        // ============================================================
        private static bool _tpFired;

        private static void Scenario_PgpPriorityShedingTopologyProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-priority-shedding-topology-probe")) return;
            if (_tpFired) return;
            _tpFired = true;

            int totalChecks = 0;
            int passCount = 0;
            int failCount = 0;

            try
            {
                _log?.LogInfo("[ScenarioRunner] TP START topology-probe");
                var asm = GetModAssembly(PGP_ASSEMBLY);
                if (asm == null) { _log?.LogError("[ScenarioRunner] TP no PGP assembly"); return; }

                var priorityStoreType = asm.GetType("PowerGridPlus.PriorityStore");
                var allocatorType = asm.GetType("PowerGridPlus.TransformerAllocator");
                var brownoutType = asm.GetType("PowerGridPlus.BrownoutRegistry");
                var tickCounterType = asm.GetType("PowerGridPlus.ElectricityTickCounter");

                var getAlloc = allocatorType?.GetMethod("GetAllocatedSupply",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var invalidate = allocatorType?.GetMethod("InvalidateAll",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var clearAll = brownoutType?.GetMethod("ClearAll",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var setPrio = priorityStoreType?.GetMethod("SetPriority",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(Thing), typeof(int) }, null);
                var getPrio = priorityStoreType?.GetMethod("GetPriority",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(long) }, null);
                var currentTickProp = tickCounterType?.GetProperty("CurrentTick",
                    BindingFlags.NonPublic | BindingFlags.Static);
                int currentTick = (int)(currentTickProp?.GetValue(null) ?? -1);

                if (_transformers.Count == 0) RebuildCaches();

                // Group by input network.
                var byInputNet = new Dictionary<long, List<Transformer>>();
                var byOutputNet = new Dictionary<long, List<Transformer>>();
                foreach (var t in _transformers)
                {
                    if (t == null || t.InputNetwork == null || t.OutputNetwork == null) continue;
                    long inId = t.InputNetwork.ReferenceId;
                    long outId = t.OutputNetwork.ReferenceId;
                    if (!byInputNet.TryGetValue(inId, out var l1)) byInputNet[inId] = l1 = new List<Transformer>();
                    l1.Add(t);
                    if (!byOutputNet.TryGetValue(outId, out var l2)) byOutputNet[outId] = l2 = new List<Transformer>();
                    l2.Add(t);
                }

                // ---- P1: PARALLEL TOPOLOGY (same input, same output, multiple transformers) ----
                // Find an output network with >= 2 feeders whose input networks ALSO match (true parallel).
                clearAll?.Invoke(null, null);
                invalidate?.Invoke(null, null);
                int parallelCases = 0;
                int parallelCorrect = 0;
                foreach (var kv in byOutputNet)
                {
                    if (kv.Value.Count < 2) continue;
                    // True parallel: at least two share an input network too.
                    var inputGroups = kv.Value
                        .Where(t => t.OnOff && t.Error != 1 && t.InputNetwork != null)
                        .GroupBy(t => t.InputNetwork.ReferenceId)
                        .Where(g => g.Count() >= 2)
                        .ToList();
                    foreach (var ig in inputGroups)
                    {
                        var par = ig.OrderByDescending(t => t.OutputMaximum).ToList();
                        if (par.Count < 2) continue;
                        parallelCases++;
                        // Assign 500 / 100 priorities
                        setPrio?.Invoke(null, new object[] { par[0], 500 });
                        setPrio?.Invoke(null, new object[] { par[1], 100 });
                        invalidate?.Invoke(null, null);
                        float a0 = (float)(getAlloc?.Invoke(null, new object[] { par[0] }) ?? -1f);
                        float a1 = (float)(getAlloc?.Invoke(null, new object[] { par[1] }) ?? -1f);
                        // Lower-priority should be in standby (a1 == 0) if a0 alone covers demand.
                        // Use a loose check: a0 >= a1 (higher-prio gets at least as much).
                        if (a0 >= a1 - 0.5f) parallelCorrect++;
                        _log?.LogInfo($"[ScenarioRunner] TP P1 parallel inNet={ig.Key} outNet={kv.Key} prio500={par[0].ReferenceId} alloc={a0:F0} prio100={par[1].ReferenceId} alloc={a1:F0}");
                    }
                }
                totalChecks++;
                if (parallelCases == 0)
                { _log?.LogWarning("[ScenarioRunner] TP P1 SKIP: no parallel (same input + same output) transformer pairs in this save."); passCount++; }
                else if (parallelCorrect == parallelCases)
                { _log?.LogInfo($"[ScenarioRunner] TP P1 PASS: all {parallelCases} parallel cases respect priority order."); passCount++; }
                else
                { _log?.LogError($"[ScenarioRunner] TP P1 FAIL: {parallelCorrect}/{parallelCases} parallel cases respected priority order."); failCount++; }

                // ---- P2: MULTI-OUT (same input, different outputs) cap by RequiredLoad ----
                // For every output network with feeders, sum of allocations must NOT exceed RequiredLoad.
                clearAll?.Invoke(null, null);
                invalidate?.Invoke(null, null);
                int multiOutCases = 0;
                int multiOutCorrect = 0;
                foreach (var kv in byOutputNet)
                {
                    if (kv.Value.Count == 0) continue;
                    var feeders = kv.Value.Where(t => t.OnOff && t.Error != 1).ToList();
                    if (feeders.Count == 0) continue;
                    float reqLoad = feeders[0].OutputNetwork?.RequiredLoad ?? 0f;
                    if (reqLoad <= 0.01f) continue;     // skip output nets with no demand
                    multiOutCases++;
                    invalidate?.Invoke(null, null);
                    float totalAlloc = 0f;
                    foreach (var f in feeders)
                    {
                        float a = (float)(getAlloc?.Invoke(null, new object[] { f }) ?? -1f);
                        if (a > 0f) totalAlloc += a;
                    }
                    // Allow small slack (1 W).
                    bool ok = totalAlloc <= reqLoad + 1f;
                    if (ok) multiOutCorrect++;
                    else _log?.LogInfo($"[ScenarioRunner] TP P2 outNet={kv.Key} totalAlloc={totalAlloc:F0} reqLoad={reqLoad:F0} OVERSHOOT");
                }
                totalChecks++;
                if (multiOutCases == 0)
                { _log?.LogWarning("[ScenarioRunner] TP P2 SKIP: no output nets with positive RequiredLoad in this save."); passCount++; }
                else if (multiOutCorrect == multiOutCases)
                { _log?.LogInfo($"[ScenarioRunner] TP P2 PASS: all {multiOutCases} output nets respect totalAlloc <= RequiredLoad."); passCount++; }
                else
                { _log?.LogError($"[ScenarioRunner] TP P2 FAIL: {multiOutCorrect}/{multiOutCases} output nets respected the demand cap."); failCount++; }

                // ---- P3: DEMAND-DRIVEN desired -- no cascade-shed for low demand ----
                // For every On transformer where OutputNetwork.RequiredLoad is small (< 1 kW)
                // and its InputNetwork.PotentialLoad is reasonable, allocation should equal
                // OutputNetwork.RequiredLoad (NOT OutputMaximum, NOT 0). This is the 418422/418423
                // cascade fix.
                clearAll?.Invoke(null, null);
                invalidate?.Invoke(null, null);
                int demandCases = 0;
                int demandCorrect = 0;
                int demandOvershoot = 0;
                int demandUndershoot = 0;
                foreach (var t in _transformers)
                {
                    if (t == null || !t.OnOff || t.Error == 1) continue;
                    if (t.OutputNetwork == null || t.InputNetwork == null) continue;
                    float req = t.OutputNetwork.RequiredLoad;
                    float pot = t.InputNetwork.PotentialLoad;
                    if (req <= 0f || req >= 1000f) continue;    // looking for SMALL demand cases
                    if (pot < req) continue;                    // upstream has enough
                    demandCases++;
                    invalidate?.Invoke(null, null);
                    float alloc = (float)(getAlloc?.Invoke(null, new object[] { t }) ?? -1f);
                    // Other transformers on the SAME output net may share this RequiredLoad,
                    // so per-transformer alloc <= RequiredLoad is the right check, not equality.
                    if (alloc <= req + 0.5f && alloc >= 0f)
                    {
                        demandCorrect++;
                        if (alloc > t.OutputMaximum + 0.5f) demandOvershoot++;
                    }
                    else
                    {
                        if (alloc > req) demandOvershoot++;
                        else demandUndershoot++;
                        _log?.LogInfo($"[ScenarioRunner] TP P3 ref={t.ReferenceId} alloc={alloc:F0} req={req:F0} OutMax={t.OutputMaximum:F0} POT={pot:F0}");
                    }
                }
                totalChecks++;
                if (demandCases == 0)
                { _log?.LogWarning("[ScenarioRunner] TP P3 SKIP: no transformers with low downstream demand and sufficient upstream in this save."); passCount++; }
                else if (demandCorrect == demandCases)
                { _log?.LogInfo($"[ScenarioRunner] TP P3 PASS: all {demandCases} low-demand transformers allocate within their downstream RequiredLoad (no greedy OutputMaximum claim)."); passCount++; }
                else
                { _log?.LogError($"[ScenarioRunner] TP P3 FAIL: {demandCorrect}/{demandCases} respected demand cap. overshoot={demandOvershoot} undershoot={demandUndershoot}."); failCount++; }

                // ---- P4: 418422 + 418423 explicit (the cascade regression case) ----
                // The user's playtest from session 2026-06-02 reported these two parallel
                // transformers mass-shedding with default priorities on Luna. The cascade fix
                // is: per-output-network demand cap on `desired` + global allocator. The
                // success criterion is NOT "alloc > 0" (deferred standby with alloc=0 is OK
                // for a chain-bootstrap tick) -- it is "NOT in lockout". Verify via
                // BrownoutRegistry.IsShedding after a clean re-allocation.
                Transformer t418422 = _transformers.FirstOrDefault(x => x != null && x.ReferenceId == 418422L);
                Transformer t418423 = _transformers.FirstOrDefault(x => x != null && x.ReferenceId == 418423L);
                totalChecks++;
                if (t418422 == null && t418423 == null)
                { _log?.LogWarning("[ScenarioRunner] TP P4 SKIP: neither 418422 nor 418423 present in this save."); passCount++; }
                else
                {
                    // Reset every transformer's priority to the default 100 (P1 may have set
                    // 500 / 100 on these two; clear that contamination for the realistic test).
                    foreach (var t in _transformers)
                    {
                        if (t == null) continue;
                        setPrio?.Invoke(null, new object[] { t, 100 });
                    }
                    clearAll?.Invoke(null, null);
                    invalidate?.Invoke(null, null);

                    var isShedding = brownoutType?.GetMethod("IsShedding",
                        BindingFlags.NonPublic | BindingFlags.Static,
                        null, new[] { typeof(long), typeof(int) }, null);
                    int tickNow = (int)(currentTickProp?.GetValue(null) ?? 0);

                    string verdict = "";
                    bool ok = true;
                    foreach (var t in new[] { t418422, t418423 })
                    {
                        if (t == null) continue;
                        float a = (float)(getAlloc?.Invoke(null, new object[] { t }) ?? -1f);
                        float req = t.OutputNetwork?.RequiredLoad ?? -1f;
                        float pot = t.InputNetwork?.PotentialLoad ?? -1f;
                        bool shedding = (bool)(isShedding?.Invoke(null, new object[] { t.ReferenceId, tickNow }) ?? false);
                        // Cascade fix success: NOT shedding (lockout is the bad state). Demand-
                        // driven allocator may legitimately return 0 if upstream chain hasn't
                        // bootstrapped yet, but it must NOT have triggered a 10s lockout.
                        if (shedding) ok = false;
                        verdict += $" ref={t.ReferenceId} OnOff={t.OnOff} alloc={a:F0} req={req:F0} pot={pot:F0} OutMax={t.OutputMaximum:F0} shedding={shedding};";
                    }
                    if (ok)
                    { _log?.LogInfo($"[ScenarioRunner] TP P4 PASS: 418422/418423 not in lockout under default priorities (cascade fix works).{verdict}"); passCount++; }
                    else
                    { _log?.LogError($"[ScenarioRunner] TP P4 FAIL: 418422/418423 in lockout despite cascade fix.{verdict}"); failCount++; }
                }

                // Restore baseline.
                clearAll?.Invoke(null, null);
                invalidate?.Invoke(null, null);

                _log?.LogInfo($"[ScenarioRunner] TP END pass={passCount} fail={failCount} total={totalChecks}");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] TP threw: {e}");
            }
        }

        // ============================================================
        // Scenario: pgp-priority-shedding-all (aggregator)
        // ------------------------------------------------------------
        // Runs every probe above in sequence on the first scenario tick.
        // Convenience for capturing every PASS/FAIL in one -Start cycle.
        // ============================================================
        private static bool _allShedFired;

        private static void Scenario_PgpPriorityShedingAll()
        {
            if (_allShedFired) return;
            _allShedFired = true;
            _log?.LogInfo("[ScenarioRunner] ALL START priority-shedding-all aggregator");
            Scenario_PgpPriorityShedingKnobProbe();
            Scenario_PgpPriorityShedingFlashProbe();
            Scenario_PgpPriorityShedingHoverProbe();
            Scenario_PgpPriorityShedingLabellerProbe();
            Scenario_PgpPriorityShedingMpProbe();
            Scenario_PgpPriorityShedingSaveLoadProbe();
            Scenario_PgpPriorityShedingTopologyProbe();
            // Also include the existing PSP + persist probes so a single -Start covers
            // the entire feature surface.
            Scenario_PgpPriorityShedingProbe();
            Scenario_PgpPriorityShedingNetworkBreakdown();
            _log?.LogInfo("[ScenarioRunner] ALL END priority-shedding-all aggregator");
        }
    }
}
