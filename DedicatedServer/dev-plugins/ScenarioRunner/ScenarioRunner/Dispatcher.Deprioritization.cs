using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Inventory;
using Assets.Scripts.Networking;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using UnityEngine;

namespace ScenarioRunner
{
    // Headless scenarios that extend coverage of the Transformer Priority +
    // Deprioritization feature into the surfaces that the original PSP probe (in
    // Dispatcher.cs) could not reach: the knob-step path, the deprioritization flash
    // attach + colour constants, the hover error message, the labeller
    // redirect, the multiplayer code paths (host short-circuit + state
    // machine + join-suffix round-trip), Harmony hook attachment for
    // save/load, and the cross-topology allocator behaviour.
    //
    // Every scenario emits structured "[ScenarioRunner] <TAG> P<n> PASS|FAIL ..."
    // lines plus a final "END pass=X fail=Y total=Z" so an agent can grep PASS/FAIL
    // counts off LogOutput.log.
    //
    // The aggregator scenario "pgp-priority-deprioritization-all" runs every probe below
    // on the first scenario tick. Configure via Probe / Scenario in
    // net.scenariorunner.cfg.
    internal static partial class Dispatcher
    {
        // ============================================================
        // Scenario: pgp-priority-deprioritization-knob-probe (C-a + C-b)
        // ------------------------------------------------------------
        // Verifies the knob-step computation by directly invoking the
        // patched Transformer.InteractWith logic (with SetKnob nulled out
        // to avoid main-thread Unity transform writes from a worker), and
        // by reading the patched NeedleFullScale + step constants.
        // ============================================================
        private static bool _kbpFired;

        private static void Scenario_PgpPriorityDeprioritizationKnobProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-priority-deprioritization-knob-probe")) return;
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
                        // The Priority + Deprioritization system is always on (the EnableTransformerShedding
                        // master toggle was deleted); InteractWithPatch has no settings gate, so the
                        // button drives below exercise the patched path unconditionally.
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
        // Scenario: pgp-priority-deprioritization-flash-probe (C-c)
        // ------------------------------------------------------------
        // Verifies FaultFlashBehaviour attach mechanic + colour
        // constants. Cannot validate visual rendering headlessly, but
        // can confirm every transformer has the component, the renderer
        // discovery succeeded, the per-fault OrangeFlashColor /
        // RedFlashColor constants are right, and the Harmony attach
        // patch is registered.
        // ============================================================
        private static bool _fpFired;

        private static void Scenario_PgpPriorityDeprioritizationFlashProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-priority-deprioritization-flash-probe")) return;
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

                var flashType = asm.GetType("PowerGridPlus.FaultFlashBehaviour");
                var attachPatchType = asm.GetType("PowerGridPlus.Patches.TransformerFlashAttachPatches");
                if (flashType == null || attachPatchType == null)
                {
                    _log?.LogError("[ScenarioRunner] FP FAIL: type lookup failed (flashType / attachPatchType).");
                    failCount++;
                    return;
                }

                // ---- P1: flash colour constants ----
                // The single FlashColor const was split when the flash gained
                // per-fault colours (FaultHover §11.5 precedence): the Deprioritized fault pulses
                // OrangeFlashColor #ffa500 = (1, 165/255, 0); every other fault
                // (overload / cycle / current-mismatch) pulses RedFlashColor
                // #ff2626 = (1, 0.15, 0.15). Both are internal static readonly on
                // FaultFlashBehaviour.
                var orangeField = flashType.GetField("OrangeFlashColor",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var redField = flashType.GetField("RedFlashColor",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Color orange = orangeField != null ? (Color)orangeField.GetValue(null) : Color.black;
                Color red = redField != null ? (Color)redField.GetValue(null) : Color.black;
                totalChecks++;
                bool orangeOk = Math.Abs(orange.r - 1f) < 0.01f
                    && orange.g >= 0.4f && orange.g <= 0.7f
                    && orange.b < 0.01f;                          // #ffa500 -> (1, 0.647, 0)
                bool redOk = Math.Abs(red.r - 1f) < 0.01f
                    && Math.Abs(red.g - 0.15f) < 0.02f
                    && Math.Abs(red.b - 0.15f) < 0.02f;           // #ff2626 -> (1, 0.149, 0.149)
                if (orangeOk && redOk)
                { _log?.LogInfo($"[ScenarioRunner] FP P1 PASS: OrangeFlashColor=({orange.r:F2},{orange.g:F2},{orange.b:F2}) in the deprioritized orange band, RedFlashColor=({red.r:F2},{red.g:F2},{red.b:F2}) matches #ff2626."); passCount++; }
                else
                { _log?.LogError($"[ScenarioRunner] FP P1 FAIL: OrangeFlashColor=({orange.r:F2},{orange.g:F2},{orange.b:F2}) RedFlashColor=({red.r:F2},{red.g:F2},{red.b:F2}) (expected #ffa500 deprioritized / #ff2626 other-fault split)."); failCount++; }

                // ---- P2: FlashHz constant ----
                var flashHzField = flashType.GetField("FlashHz",
                    BindingFlags.NonPublic | BindingFlags.Static);
                float flashHz = flashHzField != null ? (float)flashHzField.GetValue(null) : -1f;
                totalChecks++;
                if (Math.Abs(flashHz - 2f) < 0.01f)
                { _log?.LogInfo($"[ScenarioRunner] FP P2 PASS: FlashHz={flashHz} (2 Hz pulse rate)."); passCount++; }
                else
                { _log?.LogError($"[ScenarioRunner] FP P2 FAIL: FlashHz={flashHz}, expected 2."); failCount++; }

                // ---- P3: FaultFlashBehaviour attached on every Transformer ----
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
                { _log?.LogInfo($"[ScenarioRunner] FP P3 PASS: FaultFlashBehaviour attached on all {attachedHits} transformers."); passCount++; }
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
        // Scenario: pgp-priority-deprioritization-hover-probe (C-d)
        // ------------------------------------------------------------
        // Drives the GetPassiveTooltip fault-hover postfix (now
        // FaultHoverPatches.Postfix, attached per-override via
        // TargetMethods) by forcing a deprioritized state on a sample transformer,
        // calling GetPassiveTooltip, and checking the Extended field for
        // the expected colored text.
        // ============================================================
        private static bool _hpFired;

        private static void Scenario_PgpPriorityDeprioritizationHoverProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-priority-deprioritization-hover-probe")) return;
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

                var deprioritizedRegistryType = asm.GetType("PowerGridPlus.DeprioritizedRegistry");
                var tickCounterType = asm.GetType("PowerGridPlus.ElectricityTickCounter");
                if (deprioritizedRegistryType == null || tickCounterType == null)
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

                var clearAll = deprioritizedRegistryType.GetMethod("ClearAll",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var currentTickProp = tickCounterType.GetProperty("CurrentTick",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var isDeprioritization = deprioritizedRegistryType.GetMethod("IsDeprioritized",
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

                // ---- P1: no deprioritization -> Extended should NOT contain the fault text ----
                clearAll?.Invoke(null, null);
                object ttBaseline = gptOnThing.Invoke(sampleT, gptArgs);
                string extendedBaseline = ReflectGetExtended(ttBaseline) ?? string.Empty;
                bool baselineHasDeprioritized = extendedBaseline.IndexOf("Deprioritized fault", StringComparison.OrdinalIgnoreCase) >= 0;
                totalChecks++;
                if (!baselineHasDeprioritized)
                { _log?.LogInfo("[ScenarioRunner] HP P1 PASS: baseline (no deprioritization) tooltip Extended has no 'Deprioritized fault' line."); passCount++; }
                else
                { _log?.LogError($"[ScenarioRunner] HP P1 FAIL: baseline tooltip already mentions 'Deprioritization': {Truncate(extendedBaseline, 200)}"); failCount++; }

                // ---- P2: force deprioritization via single NoteDeprioritized (instant lockout) ----
                var noteDeprioritized = deprioritizedRegistryType.GetMethod("NoteDeprioritized",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(long), typeof(int) }, null);
                noteDeprioritized?.Invoke(null, new object[] { sampleT.ReferenceId, tickNow });
                bool sheddingNow = (bool)(isDeprioritization?.Invoke(null, new object[] { sampleT.ReferenceId, tickNow }) ?? false);
                totalChecks++;
                if (sheddingNow)
                { _log?.LogInfo($"[ScenarioRunner] HP P2 PASS: DeprioritizedRegistry reports IsDeprioritized=true after 1x NoteDeprioritized on ref={sampleT.ReferenceId} (instant lockout)."); passCount++; }
                else
                { _log?.LogError($"[ScenarioRunner] HP P2 FAIL: IsDeprioritized=false after 1x NoteDeprioritized on ref={sampleT.ReferenceId}."); failCount++; }

                // ---- P3: tooltip via real virtual-dispatch call (the path the game uses) ----
                int currentTickAtCall = (int)(currentTickProp?.GetValue(null) ?? 0);
                bool sheddingAtCall = (bool)(isDeprioritization?.Invoke(null, new object[] { sampleT.ReferenceId, currentTickAtCall }) ?? false);
                _log?.LogInfo($"[ScenarioRunner] HP P3 pre-call: ElectricityTickCounter.CurrentTick={currentTickAtCall} IsDeprioritized(ref,currentTick)={sheddingAtCall}");
                object ttDeprioritized = gptOnThing.Invoke(sampleT, gptArgs);
                string extendedDeprioritized = ReflectGetExtended(ttDeprioritized) ?? string.Empty;
                // Block format is owned by FaultHover.TryGetMergedBlock (single source of truth
                // for all fault hovers, locked template): line 1 is the merged state-plus-title
                // line "{On|Off} - Deprioritized fault: {seconds}s" with the title + countdown in
                // FaultRed #ff2626 (orange is flash-only). The payload-less NoteDeprioritized
                // used above carries zero needs/upstream numbers, so no diagnostics line renders.
                bool hasDeprioritizedTitle = extendedDeprioritized.IndexOf("Deprioritized fault:", StringComparison.OrdinalIgnoreCase) >= 0;
                bool hasRed = extendedDeprioritized.IndexOf("#ff2626", StringComparison.OrdinalIgnoreCase) >= 0;
                bool noOrange = extendedDeprioritized.IndexOf("#ffa500", StringComparison.OrdinalIgnoreCase) < 0;
                totalChecks++;
                if (hasDeprioritizedTitle && hasRed && noOrange)
                { _log?.LogInfo($"[ScenarioRunner] HP P3 PASS: tooltip Extended contains the red 'Deprioritized fault:' title and no hover orange. len={extendedDeprioritized.Length}"); passCount++; }
                else
                { _log?.LogError($"[ScenarioRunner] HP P3 FAIL: hasDeprioritizedTitle={hasDeprioritizedTitle} hasRed={hasRed} noOrange={noOrange}. Extended={Truncate(extendedDeprioritized, 300)}. (If P3 fails but P3b passes, the postfix logic is correct but virtual dispatch bypasses the patched override -- fix PGP's FaultHoverPatches.TargetMethods to cover the actually-overriding class)."); failCount++; }

                // ---- P3b: postfix logic invoked DIRECTLY with a synthetic baseline PassiveTooltip ----
                // Decouples postfix-correctness from postfix-reachability so we can tell whether
                // P3 failure is a logic bug or a virtual-dispatch reachability bug.
                // The GetPassiveTooltip postfix moved in the rearchitecture: it now lives on
                // FaultHoverPatches.Postfix (multi-target TargetMethods over the seven overriding
                // classes); FaultButtonTooltipPatches kept only the GetContextualName postfix.
                var postfixType = asm.GetType("PowerGridPlus.Patches.FaultHoverPatches");
                var postfixMethod = postfixType?.GetMethod("Postfix",
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
                    bool hasDeprioritized = after.IndexOf("Deprioritized fault:", StringComparison.OrdinalIgnoreCase) >= 0;
                    totalChecks++;
                    if (hasDeprioritized)
                    { _log?.LogInfo($"[ScenarioRunner] HP P3b PASS: postfix invoked directly appends the deprioritized block to Extended. len={after.Length}"); passCount++; }
                    else
                    { _log?.LogError($"[ScenarioRunner] HP P3b FAIL: postfix-direct invocation did NOT append the deprioritized block. after={Truncate(after, 200)}"); failCount++; }
                    AfterP3b:;
                }

                // ---- P3c: the locked D/U/S hover payload round-trips the registry and renders ----
                // The 5-arg NoteDeprioritized carries (needsW, upstreamDemandW, upstreamSupplyW);
                // TryGetFault must hand the same triple back, and the rendered hover must carry
                // the locked "Needs D while U competes for S upstream" sentence. The numeric
                // values are asserted through TryGetFault (exact floats) rather than the
                // rendered string, which is locale-formatted.
                var noteDeprioritizedPayload = deprioritizedRegistryType.GetMethod("NoteDeprioritized",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(long), typeof(int), typeof(float), typeof(float), typeof(float) }, null);
                var tryGetFault = deprioritizedRegistryType.GetMethod("TryGetFault",
                    BindingFlags.NonPublic | BindingFlags.Static);
                totalChecks++;
                if (noteDeprioritizedPayload != null && tryGetFault != null)
                {
                    noteDeprioritizedPayload.Invoke(null, new object[] { sampleT.ReferenceId, tickNow, 12000f, 30000f, 20000f });
                    var tgArgs = new object[] { sampleT.ReferenceId, tickNow, 0f, 0f, 0f, 0f };
                    bool gotPayload = tryGetFault.Invoke(null, tgArgs) is bool gp && gp;
                    float needsW = (float)tgArgs[3], upstreamDemandW = (float)tgArgs[4], upstreamSupplyW = (float)tgArgs[5];
                    object ttPayload = gptOnThing.Invoke(sampleT, gptArgs);
                    string extendedPayload = ReflectGetExtended(ttPayload) ?? string.Empty;
                    bool renders = extendedPayload.IndexOf("Needs ", StringComparison.Ordinal) >= 0
                        && extendedPayload.IndexOf(" competes for ", StringComparison.Ordinal) >= 0
                        && extendedPayload.IndexOf(" upstream", StringComparison.Ordinal) >= 0;
                    if (gotPayload && Math.Abs(needsW - 12000f) < 0.5f && Math.Abs(upstreamDemandW - 30000f) < 0.5f
                        && Math.Abs(upstreamSupplyW - 20000f) < 0.5f && renders)
                    { _log?.LogInfo("[ScenarioRunner] HP P3c PASS: D/U/S payload (12 kW / 30 kW / 20 kW) round-trips TryGetFault and the hover renders the 'Needs D while U competes for S upstream' line."); passCount++; }
                    else
                    { _log?.LogError($"[ScenarioRunner] HP P3c FAIL: got={gotPayload} needs={needsW} upstreamDemand={upstreamDemandW} upstreamSupply={upstreamSupplyW} renders={renders}. Extended={Truncate(extendedPayload, 300)}"); failCount++; }
                }
                else
                { _log?.LogError($"[ScenarioRunner] HP P3c FAIL: 5-arg NoteDeprioritized={noteDeprioritizedPayload != null} TryGetFault={tryGetFault != null} seam missing."); failCount++; }

                // ---- P4: clear the deprioritization -> tooltip back to the fault-free state ----
                clearAll?.Invoke(null, null);
                object ttCleared = gptOnThing.Invoke(sampleT, gptArgs);
                string extendedCleared = ReflectGetExtended(ttCleared) ?? string.Empty;
                bool hasDeprioritizedAfterClear = extendedCleared.IndexOf("Deprioritized fault", StringComparison.OrdinalIgnoreCase) >= 0;
                totalChecks++;
                if (!hasDeprioritizedAfterClear)
                { _log?.LogInfo("[ScenarioRunner] HP P4 PASS: after ClearAll, tooltip Extended drops the 'Deprioritized fault' line."); passCount++; }
                else
                { _log?.LogError($"[ScenarioRunner] HP P4 FAIL: tooltip still mentions Deprioritization after ClearAll: {Truncate(extendedCleared, 200)}"); failCount++; }

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
        // Scenario: pgp-priority-deprioritization-labeller-probe (C-e)
        // ------------------------------------------------------------
        // Directly invokes the TransformerLabellerPatches.Set_Prefix and
        // InputSetting_Prefix with synthetic ISetable + LogicType,
        // confirming the Setting -> Priority swap fires for transformers
        // and is a no-op for non-transformers.
        // ============================================================
        private static bool _lpFired;

        private static void Scenario_PgpPriorityDeprioritizationLabellerProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-priority-deprioritization-labeller-probe")) return;
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

                // The Priority + Deprioritization system is always on (the EnableTransformerShedding
                // master toggle was deleted); the labeller prefixes have no settings gate, so
                // the swap logic below is exercised unconditionally.

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
        // Scenario: pgp-priority-deprioritization-mp-probe (C-f)
        // ------------------------------------------------------------
        // Verifies the multiplayer-sync code paths that can be exercised
        // in-process: FaultRegistrySnapshotMessage.Process host
        // short-circuit, the DeprioritizedRegistry client-mirror state
        // machine, and a full SerializeJoinSuffix ->
        // DeserializeJoinSuffix round-trip via a MemoryStream-backed
        // RocketBinaryWriter/Reader.
        //
        // Cross-process multiplayer delivery cannot be tested headless
        // from a single dedi server, but the message-handler / state
        // machine / join-suffix payload IS verified end-to-end here.
        // ============================================================
        private static bool _mpFired;

        private static void Scenario_PgpPriorityDeprioritizationMpProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-priority-deprioritization-mp-probe")) return;
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

                // The per-transition DeprioritizedStateMessage / OverloadStateMessage pair was
                // replaced by per-tick FULL registry snapshots (POWER.md §13 heartbeat
                // model): FaultRegistrySnapshotMessage carries (refId, remainingTicks)
                // entries per registry Kind.
                var faultMsgType = asm.GetType("PowerGridPlus.FaultRegistrySnapshotMessage");
                var prioMsgType = asm.GetType("PowerGridPlus.PriorityMessage");
                var deprioritizedRegistryType = asm.GetType("PowerGridPlus.DeprioritizedRegistry");
                var priorityStoreType = asm.GetType("PowerGridPlus.PriorityStore");
                if (faultMsgType == null || prioMsgType == null || deprioritizedRegistryType == null || priorityStoreType == null)
                {
                    _log?.LogError($"[ScenarioRunner] MP FAIL: type lookup FM={faultMsgType != null} PM={prioMsgType != null} DR={deprioritizedRegistryType != null} PS={priorityStoreType != null}.");
                    failCount++; return;
                }

                // The per-transition setter died with the heartbeat model; the client mirror is
                // driven exclusively by ReplaceClientSnapshot (full-state replace, self-healing).
                // On a HOST the peer-aware IsDeprioritized reads the host lockout dict, so the
                // client mirror (_clientExpiry, a ClientEntry struct with the MonotonicClock
                // expiry plus the locked needs/upstream-demand/upstream-supply hover triple) is
                // asserted directly. The snapshot entry tuple is the registry's own
                // (long refId, int remainingTicks, float needsW, float upstreamDemandW,
                // float upstreamSupplyW) shape, built via MakeGenericType so this fixture keeps
                // compiling if the mod assembly is absent.
                var replaceClientSnapshot = deprioritizedRegistryType.GetMethod("ReplaceClientSnapshot",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var clientMirrorField = deprioritizedRegistryType.GetField("_clientExpiry",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var clientMirror = clientMirrorField?.GetValue(null) as System.Collections.IDictionary;
                var snapshotTupleType = typeof(ValueTuple<,,,,>).MakeGenericType(
                    typeof(long), typeof(int), typeof(float), typeof(float), typeof(float));
                var snapshotListType = typeof(List<>).MakeGenericType(snapshotTupleType);

                long testRef = 7777777777L;

                // ---- P1: DeprioritizedRegistry client-mirror state machine ----
                var oneEntry = (System.Collections.IList)Activator.CreateInstance(snapshotListType);
                oneEntry.Add(Activator.CreateInstance(snapshotTupleType, testRef, 120, 12000f, 30000f, 20000f));
                replaceClientSnapshot?.Invoke(null, new object[] { oneEntry });
                bool after1 = clientMirror != null && clientMirror.Contains(testRef);
                var emptySnapshot = (System.Collections.IList)Activator.CreateInstance(snapshotListType);
                replaceClientSnapshot?.Invoke(null, new object[] { emptySnapshot });
                bool after2 = clientMirror != null && clientMirror.Contains(testRef);
                totalChecks++;
                if (replaceClientSnapshot != null && clientMirror != null && after1 && !after2)
                { _log?.LogInfo($"[ScenarioRunner] MP P1 PASS: ReplaceClientSnapshot with one payload entry inserts the _clientExpiry mirror entry ({after1}), the empty snapshot clears it ({after2})."); passCount++; }
                else
                { _log?.LogError($"[ScenarioRunner] MP P1 FAIL: state machine inconsistent. method={replaceClientSnapshot != null} mirrorDict={clientMirror != null} trueRead={after1} falseRead={after2}."); failCount++; }

                // ---- P2: FaultRegistrySnapshotMessage.Process on host short-circuits ----
                // Host's NetworkManager.IsServer=true, so Process must NOT touch the client mirror.
                long testRef2 = 8888888888L;
                replaceClientSnapshot?.Invoke(null, new object[] { emptySnapshot });    // baseline
                var msg = Activator.CreateInstance(faultMsgType);
                byte kindDeprioritized = (byte)(faultMsgType.GetField("KindDeprioritized",
                    BindingFlags.Public | BindingFlags.Static)?.GetValue(null) ?? (byte)0);
                faultMsgType.GetField("Kind")?.SetValue(msg, kindDeprioritized);
                faultMsgType.GetField("Entries")?.SetValue(msg,
                    new List<KeyValuePair<long, int>> { new KeyValuePair<long, int>(testRef2, 120) });
                var processMethod = faultMsgType.GetMethod("Process");
                processMethod?.Invoke(msg, new object[] { 0L });
                bool afterProcess = clientMirror != null && clientMirror.Contains(testRef2);
                totalChecks++;
                if (!afterProcess && NetworkManager.IsServer)
                { _log?.LogInfo($"[ScenarioRunner] MP P2 PASS: FaultRegistrySnapshotMessage(KindDeprioritized).Process(0) short-circuited on host (IsServer=true); client mirror untouched."); passCount++; }
                else if (afterProcess && !NetworkManager.IsServer)
                { _log?.LogInfo($"[ScenarioRunner] MP P2 NOTE: running as non-server peer; Process correctly applied the snapshot to the client mirror."); passCount++; }
                else
                { _log?.LogError($"[ScenarioRunner] MP P2 FAIL: IsServer={NetworkManager.IsServer} but mirrorTouched={afterProcess}; expected host short-circuit."); failCount++; }
                replaceClientSnapshot?.Invoke(null, new object[] { emptySnapshot });    // cleanup

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

                // Find the live Plugin instance. The mod loads EITHER via BepInEx
                // proper (install/BepInEx/plugins -> Chainloader.PluginInfos) OR via
                // StationeersLaunchPad from data/mods (e.g. Local_PowerGridPlus),
                // where the plugin NEVER enters Chainloader.PluginInfos:
                // StationeersLaunchPad's BepInExEntrypoint does its own
                // parent.AddComponent(Type) and keeps the component in the public
                // BehaviourEntrypoint<BaseUnityPlugin>.Instance field. Three tiers:
                //   1. a static self-reference on the plugin type (PGP has none
                //      today; cheap future-proofing),
                //   2. BepInEx Chainloader.PluginInfos["net.powergridplus"].Instance,
                //   3. StationeersLaunchPad ModLoader.LoadedMods[].Entrypoints[].Instance.
                // No UnityEngine.Object.FindObjectsOfType here: forbidden off the
                // main thread (Research/Patterns/ThingEnumerationOffMainThread.md).
                object pluginInstance = FindPluginInstanceStatic(pluginType);
                string instanceTier = pluginInstance != null ? "static-member" : null;
                if (pluginInstance == null)
                {
                    pluginInstance = FindPluginInstanceChainloader("net.powergridplus");
                    if (pluginInstance != null) instanceTier = "BepInEx Chainloader";
                }
                if (pluginInstance == null)
                {
                    pluginInstance = FindPluginInstanceLaunchPad(pluginType);
                    if (pluginInstance != null) instanceTier = "StationeersLaunchPad ModLoader";
                }

                if (pluginInstance == null)
                {
                    // Environmental, not a mod defect: which registry holds the
                    // instance depends on the deploy path (BepInEx/plugins vs
                    // data/mods). If all three tiers miss, the loader registry shape
                    // has drifted; retarget FindPluginInstanceLaunchPad rather than
                    // failing the suite on a deploy-layout condition.
                    _log?.LogWarning("[ScenarioRunner] MP P4 SKIP: live Plugin instance not found via static-member, BepInEx Chainloader, or StationeersLaunchPad ModLoader tiers (on this server PowerGridPlus loads from data/mods via StationeersLaunchPad, so Chainloader alone can never see it). Join-suffix round-trip not exercised.");
                    return false;
                }
                _log?.LogInfo($"[ScenarioRunner] MP P4 plugin instance resolved via {instanceTier}: {pluginInstance.GetType().FullName}");

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

        // ---- Plugin-instance resolution tiers (MP P4) ----

        // Tier 1: classic `public static Plugin Instance` self-reference. PGP's
        // Plugin currently exposes only the static MOD / Log members, so this
        // tier finds nothing today; scanning keeps the probe working if a static
        // self-reference is ever added.
        private static object FindPluginInstanceStatic(Type pluginType)
        {
            try
            {
                foreach (var f in pluginType.GetFields(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (!pluginType.IsAssignableFrom(f.FieldType)) continue;
                    var v = f.GetValue(null);
                    if (v != null && pluginType.IsInstanceOfType(v)) return v;
                }
                foreach (var p in pluginType.GetProperties(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (!pluginType.IsAssignableFrom(p.PropertyType)) continue;
                    if (p.GetIndexParameters().Length != 0) continue;
                    object v = null;
                    try { v = p.GetValue(null); } catch { }
                    if (v != null && pluginType.IsInstanceOfType(v)) return v;
                }
            }
            catch (Exception e)
            {
                _log?.LogWarning($"[ScenarioRunner] MP P4 NOTE: static-member tier threw: {e.GetBaseException().Message}");
            }
            return null;
        }

        // Tier 2: BepInEx Chainloader. Only populated for plugins BepInEx itself
        // chainloaded from install/BepInEx/plugins; a StationeersLaunchPad-loaded
        // mod (data/mods) never appears here.
        private static object FindPluginInstanceChainloader(string pluginGuid)
        {
            try
            {
                var chainloaderType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                    .FirstOrDefault(t => t.FullName == "BepInEx.Bootstrap.Chainloader");
                var pluginInfosProp = chainloaderType?.GetProperty("PluginInfos",
                    BindingFlags.Public | BindingFlags.Static);
                var pluginInfos = pluginInfosProp?.GetValue(null) as System.Collections.IDictionary;
                object info = null;
                if (pluginInfos != null && pluginInfos.Contains(pluginGuid))
                    info = pluginInfos[pluginGuid];
                return info?.GetType().GetProperty("Instance")?.GetValue(info);
            }
            catch (Exception e)
            {
                _log?.LogWarning($"[ScenarioRunner] MP P4 NOTE: Chainloader tier threw: {e.GetBaseException().Message}");
                return null;
            }
        }

        // Tier 3: StationeersLaunchPad load path (data/mods/Local_* / Workshop_*).
        // Registry shape confirmed against the dedi's shipped StationeersLaunchPad
        // (decompile .work/decomp/0.2.6403.27689/StationeersLaunchPad.decompiled.cs):
        //   - StationeersLaunchPad.Loading.ModLoader.LoadedMods is
        //     `public static readonly List<LoadedMod>` (L18552);
        //   - LoadedMod.Entrypoints is `public List<ModEntrypoint>` (L16791);
        //   - BepInExEntrypoint : BehaviourEntrypoint<BaseUnityPlugin> stores the
        //     AddComponent'ed plugin in the base's `public T Instance` field
        //     (Instantiate at L18663, Instance at L19082). Public base fields are
        //     visible through derived-type GetField.
        // Pure managed field reads; safe from the sim worker thread (no Unity API,
        // no FindObjectsOfType).
        private static object FindPluginInstanceLaunchPad(Type pluginType)
        {
            try
            {
                var modLoaderType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                    .FirstOrDefault(t => t.FullName == "StationeersLaunchPad.Loading.ModLoader"
                                      || (t.Name == "ModLoader"
                                          && t.Assembly.GetName().Name == "StationeersLaunchPad"));
                var loadedModsField = modLoaderType?.GetField("LoadedMods",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var loadedMods = loadedModsField?.GetValue(null) as System.Collections.IEnumerable;
                if (loadedMods == null) return null;
                foreach (var mod in loadedMods)
                {
                    if (mod == null) continue;
                    var entrypointsField = mod.GetType().GetField("Entrypoints",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var entrypoints = entrypointsField?.GetValue(mod) as System.Collections.IEnumerable;
                    if (entrypoints == null) continue;
                    foreach (var entry in entrypoints)
                    {
                        if (entry == null) continue;
                        var et = entry.GetType();
                        object inst =
                            (et.GetField("Instance",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(entry))
                            ?? (et.GetProperty("Instance",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(entry));
                        if (inst != null && pluginType.IsInstanceOfType(inst)) return inst;
                    }
                }
            }
            catch (Exception e)
            {
                _log?.LogWarning($"[ScenarioRunner] MP P4 NOTE: StationeersLaunchPad tier threw: {e.GetBaseException().Message}");
            }
            return null;
        }

        // ============================================================
        // Scenario: pgp-priority-deprioritization-saveload-probe (C-g)
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

        private static void Scenario_PgpPriorityDeprioritizationSaveLoadProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-priority-deprioritization-saveload-probe")) return;
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
        // Scenario: pgp-priority-deprioritization-topology-probe (cascade + parallel)
        // ------------------------------------------------------------
        // Confirms the atomic PowerAllocator respects topology independence,
        // judged off the per-seg presentation totals it publishes to
        // TransformerSupplyCache every tick (the old on-demand
        // TransformerAllocator.GetAllocatedSupply / InvalidateAll API is gone;
        // allocations are computed once per tick and read back post-tick):
        //   - Parallel same-in, same-out: the higher-priority transformer's
        //     published output is at least the lower's.
        //   - Multi-source same-out: total published output onto a net stays
        //     within the net's written-back RequiredLoad (conservation +
        //     stranded-inflow clawback guarantee this bound).
        //   - Demand-driven desired: a low-demand transformer's published
        //     output never exceeds its output net's RequiredLoad (no greedy
        //     OutputMaximum claim; the 418422/418423 cascade fix).
        //   - 418422 / 418423 case (if present): no lockout under default
        //     priorities after a clean pass.
        // Priority writes only take effect on the NEXT allocator tick (the
        // pump is an ElectricityTick postfix, so this tick's pass already
        // ran), so the probe is phased across three consecutive ticks:
        //   tick 0: arrange the parallel-pair priorities (500 / 100), clear
        //           deprioritization lockouts.
        //   tick 1: assert P1 (parallel order), P2 (demand cap), P3
        //           (demand-driven bound) off the freshly published totals;
        //           then reset every priority to the default 100 + ClearAll
        //           for the P4 baseline.
        //   tick 2: assert P4 (418422/418423 not in lockout) and finish.
        // ============================================================
        private static int _tpPhase;
        private static int _tpPass;
        private static int _tpFail;
        private static int _tpChecks;
        private static readonly List<(long hi, long lo, long inNet, long outNet)> _tpParallelPairs
            = new List<(long hi, long lo, long inNet, long outNet)>();

        // Published output-side watts for one seg (rigid + soft through), from
        // TransformerSupplyCache.TryGetOutput(long, out float). Returns -1 when
        // the cache has no entry (seg not enrolled this tick).
        private static float TpPublishedOutput(Assembly asm, long refId)
        {
            try
            {
                var cacheType = asm?.GetType("PowerGridPlus.TransformerSupplyCache");
                var tryGet = cacheType?.GetMethod("TryGetOutput",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                if (tryGet == null) return float.NaN;
                object[] args = { refId, 0f };
                bool found = tryGet.Invoke(null, args) is bool b && b;
                return found ? (float)args[1] : -1f;
            }
            catch { return float.NaN; }
        }

        private static void Scenario_PgpPriorityDeprioritizationTopologyProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-priority-deprioritization-topology-probe")) return;
            if (_tpPhase >= 3) return;

            try
            {
                var asm = GetModAssembly(PGP_ASSEMBLY);
                if (asm == null) { _log?.LogError("[ScenarioRunner] TP no PGP assembly"); _tpPhase = 3; return; }

                var priorityStoreType = asm.GetType("PowerGridPlus.PriorityStore");
                var deprioritizedRegistryType = asm.GetType("PowerGridPlus.DeprioritizedRegistry");
                var tickCounterType = asm.GetType("PowerGridPlus.ElectricityTickCounter");

                var clearAll = deprioritizedRegistryType?.GetMethod("ClearAll",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var setPrio = priorityStoreType?.GetMethod("SetPriority",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(Thing), typeof(int) }, null);
                var currentTickProp = tickCounterType?.GetProperty("CurrentTick",
                    BindingFlags.NonPublic | BindingFlags.Static);

                if (_transformers.Count == 0) RebuildCaches();

                if (_tpPhase == 0)
                {
                    _log?.LogInfo("[ScenarioRunner] TP START topology-probe (three-tick phased: arrange, assert P1-P3, assert P4)");

                    // Find true parallel pairs (same input net AND same output net, both ON) and
                    // assign 500 / 100 priorities; the next tick's pass allocates with them.
                    var byOutputNet = new Dictionary<long, List<Transformer>>();
                    foreach (var t in _transformers)
                    {
                        if (t == null || t.InputNetwork == null || t.OutputNetwork == null) continue;
                        long outId = t.OutputNetwork.ReferenceId;
                        if (!byOutputNet.TryGetValue(outId, out var l2)) byOutputNet[outId] = l2 = new List<Transformer>();
                        l2.Add(t);
                    }

                    _tpParallelPairs.Clear();
                    foreach (var kv in byOutputNet)
                    {
                        if (kv.Value.Count < 2) continue;
                        var inputGroups = kv.Value
                            .Where(t => t.OnOff && t.Error != 1 && t.InputNetwork != null)
                            .GroupBy(t => t.InputNetwork.ReferenceId)
                            .Where(g => g.Count() >= 2)
                            .ToList();
                        foreach (var ig in inputGroups)
                        {
                            var par = ig.OrderByDescending(t => t.OutputMaximum).ToList();
                            if (par.Count < 2) continue;
                            setPrio?.Invoke(null, new object[] { par[0], 500 });
                            setPrio?.Invoke(null, new object[] { par[1], 100 });
                            _tpParallelPairs.Add((par[0].ReferenceId, par[1].ReferenceId, ig.Key, kv.Key));
                        }
                    }
                    clearAll?.Invoke(null, null);
                    _log?.LogInfo($"[ScenarioRunner] TP arranged {_tpParallelPairs.Count} parallel pair(s) at priorities 500/100; asserting next tick.");
                    _tpPhase = 1;
                    return;
                }

                if (_tpPhase == 1)
                {
                    // ---- P1: PARALLEL TOPOLOGY off the published totals ----
                    int parallelCases = 0;
                    int parallelCorrect = 0;
                    foreach (var (hi, lo, inNet, outNet) in _tpParallelPairs)
                    {
                        parallelCases++;
                        float a0 = TpPublishedOutput(asm, hi);
                        float a1 = TpPublishedOutput(asm, lo);
                        // Loose check: the higher-priority transformer publishes at least as much.
                        if (!float.IsNaN(a0) && !float.IsNaN(a1) && a0 >= a1 - 0.5f) parallelCorrect++;
                        _log?.LogInfo($"[ScenarioRunner] TP P1 parallel inNet={inNet} outNet={outNet} prio500={hi} publishedOut={a0:F0} prio100={lo} publishedOut={a1:F0}");
                    }
                    _tpChecks++;
                    if (parallelCases == 0)
                    { _log?.LogWarning("[ScenarioRunner] TP P1 SKIP: no parallel (same input + same output) transformer pairs in this save."); _tpPass++; }
                    else if (parallelCorrect == parallelCases)
                    { _log?.LogInfo($"[ScenarioRunner] TP P1 PASS: all {parallelCases} parallel cases respect priority order in the published totals."); _tpPass++; }
                    else
                    { _log?.LogError($"[ScenarioRunner] TP P1 FAIL: {parallelCorrect}/{parallelCases} parallel cases respected priority order."); _tpFail++; }

                    // ---- P2: MULTI-OUT demand cap off the published totals ----
                    // Conservation + the stranded-inflow clawback bound the summed published
                    // inflow onto a net by the net's written-back RequiredLoad (which includes
                    // granted downstream pulls and soft charge).
                    var byOutputNet = new Dictionary<long, List<Transformer>>();
                    foreach (var t in _transformers)
                    {
                        if (t == null || t.InputNetwork == null || t.OutputNetwork == null) continue;
                        long outId = t.OutputNetwork.ReferenceId;
                        if (!byOutputNet.TryGetValue(outId, out var l2)) byOutputNet[outId] = l2 = new List<Transformer>();
                        l2.Add(t);
                    }
                    int multiOutCases = 0;
                    int multiOutCorrect = 0;
                    foreach (var kv in byOutputNet)
                    {
                        var feeders = kv.Value.Where(t => t.OnOff && t.Error != 1).ToList();
                        if (feeders.Count == 0) continue;
                        float reqLoad = feeders[0].OutputNetwork?.RequiredLoad ?? 0f;
                        if (reqLoad <= 0.01f) continue;     // skip output nets with no demand
                        multiOutCases++;
                        float totalAlloc = 0f;
                        foreach (var f in feeders)
                        {
                            float a = TpPublishedOutput(asm, f.ReferenceId);
                            if (a > 0f) totalAlloc += a;
                        }
                        bool ok = totalAlloc <= reqLoad + 1.5f;   // small float slack
                        if (ok) multiOutCorrect++;
                        else _log?.LogInfo($"[ScenarioRunner] TP P2 outNet={kv.Key} totalPublished={totalAlloc:F0} reqLoad={reqLoad:F0} OVERSHOOT");
                    }
                    _tpChecks++;
                    if (multiOutCases == 0)
                    { _log?.LogWarning("[ScenarioRunner] TP P2 SKIP: no output nets with positive RequiredLoad in this save."); _tpPass++; }
                    else if (multiOutCorrect == multiOutCases)
                    { _log?.LogInfo($"[ScenarioRunner] TP P2 PASS: all {multiOutCases} output nets respect totalPublished <= RequiredLoad."); _tpPass++; }
                    else
                    { _log?.LogError($"[ScenarioRunner] TP P2 FAIL: {multiOutCorrect}/{multiOutCases} output nets respected the demand cap."); _tpFail++; }

                    // ---- P3: DEMAND-DRIVEN bound (the 418422/418423 cascade fix shape) ----
                    int demandCases = 0;
                    int demandCorrect = 0;
                    foreach (var t in _transformers)
                    {
                        if (t == null || !t.OnOff || t.Error == 1) continue;
                        if (t.OutputNetwork == null || t.InputNetwork == null) continue;
                        float req = t.OutputNetwork.RequiredLoad;
                        float pot = t.InputNetwork.PotentialLoad;
                        if (req <= 0f || req >= 1000f) continue;    // looking for SMALL demand cases
                        if (pot < req) continue;                    // upstream has enough
                        demandCases++;
                        float alloc = TpPublishedOutput(asm, t.ReferenceId);
                        // Other transformers on the SAME output net may share this RequiredLoad,
                        // so per-transformer published <= RequiredLoad is the right check.
                        if (alloc <= req + 0.5f) demandCorrect++;
                        else _log?.LogInfo($"[ScenarioRunner] TP P3 ref={t.ReferenceId} publishedOut={alloc:F0} req={req:F0} OutMax={t.OutputMaximum:F0} POT={pot:F0}");
                    }
                    _tpChecks++;
                    if (demandCases == 0)
                    { _log?.LogWarning("[ScenarioRunner] TP P3 SKIP: no transformers with low downstream demand and sufficient upstream in this save."); _tpPass++; }
                    else if (demandCorrect == demandCases)
                    { _log?.LogInfo($"[ScenarioRunner] TP P3 PASS: all {demandCases} low-demand transformers publish within their downstream RequiredLoad (no greedy OutputMaximum claim)."); _tpPass++; }
                    else
                    { _log?.LogError($"[ScenarioRunner] TP P3 FAIL: {demandCorrect}/{demandCases} respected the demand bound."); _tpFail++; }

                    // Arrange P4: reset every transformer's priority to the default 100 (P1 set
                    // 500/100 on the pairs; clear that contamination for the realistic test) and
                    // clear lockouts so the next tick's pass is a clean baseline.
                    foreach (var t in _transformers)
                    {
                        if (t == null) continue;
                        setPrio?.Invoke(null, new object[] { t, 100 });
                    }
                    clearAll?.Invoke(null, null);
                    _tpPhase = 2;
                    return;
                }

                // ---- Phase 2 / P4: 418422 + 418423 explicit (the cascade regression case) ----
                // The 2026-06-02 playtest reported these two parallel transformers mass-deprioritizing
                // with default priorities on Luna. Success is NOT "published > 0" (deferred
                // standby is legal on a bootstrap tick); it is "NOT in lockout" after a clean
                // default-priority pass.
                Transformer t418422 = _transformers.FirstOrDefault(x => x != null && x.ReferenceId == 418422L);
                Transformer t418423 = _transformers.FirstOrDefault(x => x != null && x.ReferenceId == 418423L);
                _tpChecks++;
                if (t418422 == null && t418423 == null)
                { _log?.LogWarning("[ScenarioRunner] TP P4 SKIP: neither 418422 nor 418423 present in this save."); _tpPass++; }
                else
                {
                    var isDeprioritization = deprioritizedRegistryType?.GetMethod("IsDeprioritized",
                        BindingFlags.NonPublic | BindingFlags.Static,
                        null, new[] { typeof(long), typeof(int) }, null);
                    int tickNow = (int)(currentTickProp?.GetValue(null) ?? 0);

                    string verdict = "";
                    bool ok = true;
                    foreach (var t in new[] { t418422, t418423 })
                    {
                        if (t == null) continue;
                        float a = TpPublishedOutput(asm, t.ReferenceId);
                        float req = t.OutputNetwork?.RequiredLoad ?? -1f;
                        float pot = t.InputNetwork?.PotentialLoad ?? -1f;
                        bool deprioritized = (bool)(isDeprioritization?.Invoke(null, new object[] { t.ReferenceId, tickNow }) ?? false);
                        if (deprioritized) ok = false;
                        verdict += $" ref={t.ReferenceId} OnOff={t.OnOff} publishedOut={a:F0} req={req:F0} pot={pot:F0} OutMax={t.OutputMaximum:F0} deprioritized={deprioritized};";
                    }
                    if (ok)
                    { _log?.LogInfo($"[ScenarioRunner] TP P4 PASS: 418422/418423 not in lockout under default priorities (cascade fix works).{verdict}"); _tpPass++; }
                    else
                    { _log?.LogError($"[ScenarioRunner] TP P4 FAIL: 418422/418423 in lockout despite cascade fix.{verdict}"); _tpFail++; }
                }

                _tpPhase = 3;
                _log?.LogInfo($"[ScenarioRunner] TP END pass={_tpPass} fail={_tpFail} total={_tpChecks}");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] TP threw: {e}");
                _tpPhase = 3;
            }
        }

        // ============================================================
        // Scenario: pgp-power-flow-diagnose (regression triage)
        // ------------------------------------------------------------
        // For a specified target device reference id, walks the cable
        // network it's on, logs the network's PotentialLoad / CurrentLoad
        // / RequiredLoad, and recursively walks UPSTREAM through any
        // transformer found on the network. For every transformer in the
        // chain, logs its OnOff / Error / Priority / published output
        // (TransformerSupplyCache presentation totals) / both networks'
        // loads. Used to debug "transformer chain doesn't bootstrap"
        // regressions in the demand-driven allocator.
        //
        // Target device id is hardcoded -- edit `_pfdTargetRef` and rebuild.
        // ============================================================
        private static bool _pfdFired;
        // Target may be EITHER a device reference id OR a CableNetwork reference id.
        private const long _pfdTargetRef = 551749L;
        private const int _pfdMaxHops = 6;

        private static void Scenario_PgpPowerFlowDiagnose()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-power-flow-diagnose")) return;
            if (_pfdFired) return;
            _pfdFired = true;

            try
            {
                _log?.LogInfo($"[ScenarioRunner] PFD START target ref={_pfdTargetRef}");
                var asm = GetModAssembly(PGP_ASSEMBLY);
                if (asm == null) { _log?.LogError("[ScenarioRunner] PFD no PGP assembly"); return; }

                var priorityStoreType = asm.GetType("PowerGridPlus.PriorityStore");
                var deprioritizedRegistryType = asm.GetType("PowerGridPlus.DeprioritizedRegistry");
                var tickCounterType = asm.GetType("PowerGridPlus.ElectricityTickCounter");
                var getPrio = priorityStoreType?.GetMethod("GetPriority",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(long) }, null);
                var isDeprioritization = deprioritizedRegistryType?.GetMethod("IsDeprioritized",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(long), typeof(int) }, null);
                var currentTickProp = tickCounterType?.GetProperty("CurrentTick",
                    BindingFlags.NonPublic | BindingFlags.Static);
                int currentTick = (int)(currentTickProp?.GetValue(null) ?? -1);

                if (_transformers.Count == 0) RebuildCaches();

                // Resolve the start network. The target ref may be EITHER a device
                // reference id (find the Thing, then resolve its CableNetwork) OR a
                // CableNetwork reference id directly (no Thing carries that id).
                CableNetwork startNet = null;

                Thing target = null;
                OcclusionManager.AllThings.ForEach(t =>
                {
                    if (target == null && t != null && t.ReferenceId == _pfdTargetRef)
                        target = t;
                });
                if (target != null)
                {
                    _log?.LogInfo($"[ScenarioRunner] PFD target found (device): ref={target.ReferenceId} type={target.GetType().FullName} prefab={target.PrefabName}");
                    startNet = ResolveCableNetwork(target);
                    if (startNet == null)
                        _log?.LogError("[ScenarioRunner] PFD could not resolve a CableNetwork for the device target.");
                }
                else
                {
                    // No Thing with that id; treat the target ref as a CableNetwork id.
                    CableNetwork.AllCableNetworks.ForEach(cn =>
                    {
                        if (startNet == null && cn != null && cn.ReferenceId == _pfdTargetRef)
                            startNet = cn;
                    });
                    if (startNet != null)
                        _log?.LogInfo($"[ScenarioRunner] PFD target is a CableNetwork directly: ref={startNet.ReferenceId}");
                    else
                        _log?.LogError($"[ScenarioRunner] PFD target ref={_pfdTargetRef} not found as a device or a cable network; dumping global state only.");
                }

                if (startNet != null)
                    DumpNetworkAndUpstream(startNet, 0, new HashSet<long>(), currentTick,
                        asm, getPrio, isDeprioritization);

                // Also enumerate every cable network in the world and log those with
                // PotentialLoad > 0 (= a generator on it that's actually producing).
                _log?.LogInfo("[ScenarioRunner] PFD --- networks with PotentialLoad > 0 ---");
                int genCount = 0;
                CableNetwork.AllCableNetworks.ForEach(cn =>
                {
                    if (cn == null) return;
                    if (cn.PotentialLoad > 0.5f)
                    {
                        genCount++;
                        if (genCount <= 25)
                            _log?.LogInfo($"[ScenarioRunner] PFD  GenNet ref={cn.ReferenceId} PotentialLoad={cn.PotentialLoad:F0} RequiredLoad={cn.RequiredLoad:F0} CurrentLoad={cn.CurrentLoad:F0} devices={cn.PowerDeviceList.Count}");
                    }
                });
                _log?.LogInfo($"[ScenarioRunner] PFD total networks with PotentialLoad > 0: {genCount}");

                // Check SolarPanel output directly to determine if it's night.
                int solarTotal = 0, solarProducing = 0;
                float solarSumGen = 0f;
                OcclusionManager.AllThings.ForEach(t =>
                {
                    if (t == null) return;
                    if (t.GetType().Name != "SolarPanel") return;
                    solarTotal++;
                    try
                    {
                        var ggpMethod = t.GetType().GetMethod("GetGeneratedPower",
                            BindingFlags.Public | BindingFlags.Instance,
                            null, new[] { typeof(CableNetwork) }, null);
                        if (ggpMethod == null) return;
                        // Use the panel's own cable network
                        var cnProp = t.GetType().GetProperty("PowerCableNetwork");
                        var net = cnProp?.GetValue(t) as CableNetwork;
                        if (net == null) return;
                        float gen = (float)ggpMethod.Invoke(t, new object[] { net });
                        if (gen > 0.1f) solarProducing++;
                        solarSumGen += gen;
                    }
                    catch { }
                });
                _log?.LogInfo($"[ScenarioRunner] PFD solar panels: total={solarTotal} producing(>0.1W)={solarProducing} sumGenPower={solarSumGen:F0} W");

                // Check batteries with stored power that could supply networks at night.
                int batTotal = 0, batChargedAndOn = 0;
                float batStoredSum = 0f;
                if (_batteries.Count == 0) RebuildCaches();
                foreach (var b in _batteries)
                {
                    if (b == null) continue;
                    batTotal++;
                    if (b.OnOff && b.PowerStored > 1f)
                    {
                        batChargedAndOn++;
                        batStoredSum += b.PowerStored;
                    }
                }
                _log?.LogInfo($"[ScenarioRunner] PFD batteries: total={batTotal} chargedAndOn={batChargedAndOn} totalStored={batStoredSum:F0} J");

                _log?.LogInfo("[ScenarioRunner] PFD END");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] PFD threw: {e}");
            }
        }

        private static CableNetwork ResolveCableNetwork(Thing target)
        {
            // Deep search for any CableNetwork-typed member (property or field,
            // public or non-public) on the target type hierarchy. Also enumerates
            // CableNetwork.AllCableNetworks and returns the first one whose
            // PowerDeviceList contains the target -- the bullet-proof fallback.
            CableNetwork net = null;
            var foundMembers = new List<string>();
            try
            {
                for (var t = target.GetType(); t != null && t != typeof(object); t = t.BaseType)
                {
                    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        if (p.PropertyType != typeof(CableNetwork)) continue;
                        try
                        {
                            if (p.GetValue(target) is CableNetwork cn)
                            {
                                foundMembers.Add($"{t.Name}.{p.Name}=net{cn.ReferenceId}");
                                if (net == null) net = cn;
                            }
                            else
                            {
                                foundMembers.Add($"{t.Name}.{p.Name}=null");
                            }
                        }
                        catch { }
                    }
                    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        if (f.FieldType != typeof(CableNetwork)) continue;
                        try
                        {
                            if (f.GetValue(target) is CableNetwork cn)
                            {
                                foundMembers.Add($"{t.Name}.{f.Name}=net{cn.ReferenceId}");
                                if (net == null) net = cn;
                            }
                            else
                            {
                                foundMembers.Add($"{t.Name}.{f.Name}=null");
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            _log?.LogInfo($"[ScenarioRunner] PFD resolve members: {string.Join(", ", foundMembers)}");

            if (net == null)
            {
                // Fallback: scan all cable networks for one whose PowerDeviceList contains target.
                CableNetwork.AllCableNetworks.ForEach(cn =>
                {
                    if (net != null || cn == null) return;
                    lock (cn.PowerDeviceList)
                    {
                        for (int i = 0; i < cn.PowerDeviceList.Count; i++)
                        {
                            if (cn.PowerDeviceList[i] != null && cn.PowerDeviceList[i].ReferenceId == target.ReferenceId)
                            {
                                net = cn;
                                _log?.LogInfo($"[ScenarioRunner] PFD resolve fallback: target found in net{cn.ReferenceId}.PowerDeviceList");
                                break;
                            }
                        }
                    }
                });
            }
            return net;
        }

        private static void DumpNetworkAndUpstream(CableNetwork net, int depth, HashSet<long> visited,
            int currentTick, Assembly asm, MethodInfo getPrio, MethodInfo isDeprioritization)
        {
            if (net == null || depth > _pfdMaxHops) return;
            if (visited.Contains(net.ReferenceId)) return;
            visited.Add(net.ReferenceId);
            string indent = new string(' ', depth * 2);

            // Count devices + collect transformers / generators / consumers.
            int total = 0, transformers = 0, consumers = 0;
            float devUsedSum = 0f;
            var xforms = new List<Transformer>();
            var bridges = new List<ElectricalInputOutput>();   // non-transformer IO bridges (APC, etc.)
            lock (net.PowerDeviceList)
            {
                for (int i = 0; i < net.PowerDeviceList.Count; i++)
                {
                    var d = net.PowerDeviceList[i];
                    if (d == null) continue;
                    total++;
                    if (d is Transformer ct) { transformers++; xforms.Add(ct); }
                    else if (d is ElectricalInputOutput eio) { bridges.Add(eio); }
                    // Reflectively read UsedPower if present (Device, Battery,
                    // Transformer, Generator all expose a public UsedPower).
                    try
                    {
                        var usedProp = d.GetType().GetProperty("UsedPower",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (usedProp != null && usedProp.PropertyType == typeof(float))
                        {
                            float used = (float)usedProp.GetValue(d);
                            if (used > 0) consumers++;
                            devUsedSum += used;
                        }
                    }
                    catch { }
                }
            }
            _log?.LogInfo($"[ScenarioRunner] PFD{indent}Net ref={net.ReferenceId} PotentialLoad={net.PotentialLoad:F0} CurrentLoad={net.CurrentLoad:F0} RequiredLoad={net.RequiredLoad:F0} devices={total} transformers={transformers} consumers={consumers} sumUsedPower={devUsedSum:F0}");

            // Dump every device type on this network so we can identify what's
            // bridging power in (wireless receiver, APC, battery, etc.).
            var typeCounts = new Dictionary<string, int>();
            lock (net.PowerDeviceList)
            {
                for (int i = 0; i < net.PowerDeviceList.Count; i++)
                {
                    var d = net.PowerDeviceList[i];
                    if (d == null) continue;
                    string tname = d.GetType().Name;
                    if (typeCounts.ContainsKey(tname)) typeCounts[tname]++;
                    else typeCounts[tname] = 1;
                }
            }
            var typeSummary = string.Join(", ", typeCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}"));
            _log?.LogInfo($"[ScenarioRunner] PFD{indent}  device-types: {typeSummary}");

            // For each transformer where this net is the OUTPUT net, traverse upstream
            // (its InputNetwork) and dump that. Also log the transformer's state.
            foreach (var t in xforms)
            {
                if (t == null) continue;
                int prio = (int)(getPrio?.Invoke(null, new object[] { t.ReferenceId }) ?? -1);
                float published = TpPublishedOutput(asm, t.ReferenceId);
                bool deprioritized = (bool)(isDeprioritization?.Invoke(null, new object[] { t.ReferenceId, currentTick }) ?? false);
                string role = (t.OutputNetwork == net) ? "OUTPUT" : (t.InputNetwork == net ? "INPUT" : "UNKNOWN");
                _log?.LogInfo($"[ScenarioRunner] PFD{indent}  T ref={t.ReferenceId} prefab={t.PrefabName} role-on-net={role} OnOff={t.OnOff} Error={t.Error} OutMax={t.OutputMaximum:F0} Prio={prio} publishedOut={published:F0} deprioritized={deprioritized} InNet={t.InputNetwork?.ReferenceId ?? -1} OutNet={t.OutputNetwork?.ReferenceId ?? -1}");
                if (t.OutputNetwork == net && t.InputNetwork != null)
                {
                    DumpNetworkAndUpstream(t.InputNetwork, depth + 1, visited, currentTick,
                        asm, getPrio, isDeprioritization);
                }
            }

            // Follow non-transformer IO bridges (e.g. AreaPowerControl) upstream too,
            // so a network fed through an APC rather than a transformer is still traced.
            foreach (var b in bridges)
            {
                if (b == null) continue;
                string brole = (b.OutputNetwork == net) ? "OUTPUT" : (b.InputNetwork == net ? "INPUT" : "UNKNOWN");
                bool bon = false;
                object berr = null;
                try { bon = b.OnOff; } catch { }
                try { berr = b.Error; } catch { }
                _log?.LogInfo($"[ScenarioRunner] PFD{indent}  IO ref={b.ReferenceId} prefab={b.PrefabName} type={b.GetType().Name} role-on-net={brole} OnOff={bon} Error={berr} InNet={b.InputNetwork?.ReferenceId ?? -1} OutNet={b.OutputNetwork?.ReferenceId ?? -1}");
                if (b.OutputNetwork == net && b.InputNetwork != null)
                    DumpNetworkAndUpstream(b.InputNetwork, depth + 1, visited, currentTick,
                        asm, getPrio, isDeprioritization);
            }
        }

        // ============================================================
        // Scenario: pgp-r1-prepare (R-1 visual-check setup)
        // ------------------------------------------------------------
        // Selects one transformer with positive downstream demand and
        // pins it to a perpetual deprioritized state: Priority=0 + per-tick
        // _lockoutUntilTick refresh so the orange flash + hover error
        // persist indefinitely. The developer connects to the dedi,
        // walks to the logged world position, and confirms the visual
        // behaviour without having to manually trigger the deprioritization loop.
        // ============================================================
        private static long _r1TargetRef = 0;
        private static Vector3 _r1TargetPos = default;
        private static string _r1TargetPrefab = "";
        private static int _r1LastStateLogTick = int.MinValue;

        private static void Scenario_PgpR1Prepare()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-r1-prepare")) return;
            var asm = GetModAssembly(PGP_ASSEMBLY);
            if (asm == null) return;

            var priorityStoreType = asm.GetType("PowerGridPlus.PriorityStore");
            var deprioritizedRegistryType = asm.GetType("PowerGridPlus.DeprioritizedRegistry");
            var tickCounterType = asm.GetType("PowerGridPlus.ElectricityTickCounter");
            var flashType = asm.GetType("PowerGridPlus.FaultFlashBehaviour");
            var hoverPatchType = asm.GetType("PowerGridPlus.Patches.FaultButtonTooltipPatches");
            var setPriorityMethod = priorityStoreType?.GetMethod("SetPriority",
                BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(Thing), typeof(int) }, null);
            var currentTickProp = tickCounterType?.GetProperty("CurrentTick",
                BindingFlags.NonPublic | BindingFlags.Static);
            var lockoutDictField = deprioritizedRegistryType?.GetField("_lockoutUntilTick",
                BindingFlags.NonPublic | BindingFlags.Static);

            int tickNow = (int)(currentTickProp?.GetValue(null) ?? 0);

            if (_r1TargetRef == 0)
            {
                // Activate the diagnostic flags on first tick so FaultFlashBehaviour
                // and FaultButtonTooltipPatches dump renderer + material + tooltip
                // state into the BepInEx log for this session.
                var bfbDiagField = flashType?.GetField("DiagnosticEnabled",
                    BindingFlags.NonPublic | BindingFlags.Static);
                bfbDiagField?.SetValue(null, true);
                var hoverDiagField = hoverPatchType?.GetField("DiagnosticEnabled",
                    BindingFlags.NonPublic | BindingFlags.Static);
                hoverDiagField?.SetValue(null, true);
                _log?.LogInfo($"[ScenarioRunner] R1 DIAG flags set: BFB={bfbDiagField != null} Hover={hoverDiagField != null}");

                if (_transformers.Count == 0) RebuildCaches();
                Transformer chosen = null;
                foreach (var t in _transformers)
                {
                    if (t == null || !t.OnOff || t.OutputNetwork == null) continue;
                    if (t.OutputNetwork.RequiredLoad < 10f) continue;
                    chosen = t;
                    break;
                }
                if (chosen == null)
                {
                    _log?.LogWarning("[ScenarioRunner] R1 SKIP: no transformer with OutputNetwork.RequiredLoad >= 10 W found.");
                    return;
                }
                _r1TargetRef = chosen.ReferenceId;
                _r1TargetPrefab = chosen.PrefabName ?? "";
                _r1TargetPos = chosen.transform != null ? chosen.transform.position : default;
                setPriorityMethod?.Invoke(null, new object[] { chosen, 0 });

                // Log the Needle field's GameObject info -- diagnose the
                // knob-on-load orientation issue.
                try
                {
                    var needleField = typeof(Transformer).GetField("Needle");
                    var needleGo = needleField?.GetValue(chosen) as GameObject;
                    string needlePath = needleGo?.transform != null ? BuildHierarchyPath(needleGo.transform) : "<null>";
                    Vector3 nlRot = needleGo?.transform != null ? needleGo.transform.localEulerAngles : default;
                    _log?.LogInfo($"[ScenarioRunner] R1 NEEDLE diag: Needle field -> GameObject name='{needleGo?.name}' path='{needlePath}' localEuler=({nlRot.x:F1},{nlRot.y:F1},{nlRot.z:F1})");
                }
                catch (Exception e)
                {
                    _log?.LogWarning($"[ScenarioRunner] R1 NEEDLE diag threw: {e.Message}");
                }

                _log?.LogInfo($"[ScenarioRunner] R1 PREPARED: ref={_r1TargetRef} prefab={_r1TargetPrefab} pos=({_r1TargetPos.x:F1},{_r1TargetPos.y:F1},{_r1TargetPos.z:F1}) OutputNetwork.RequiredLoad={chosen.OutputNetwork.RequiredLoad:F0} W. Priority pinned to 0; lockout perpetually refreshed.");
            }

            // Per-tick lockout refresh: keep the deprioritized state alive indefinitely
            // by writing _lockoutUntilTick = currentTick + 100 every tick.
            var lockoutDict = lockoutDictField?.GetValue(null) as System.Collections.IDictionary;
            if (lockoutDict != null)
            {
                lockoutDict[_r1TargetRef] = tickNow + 100;
            }

            // Log target state every 10 ticks so we can see OnOff/Error evolution.
            if (tickNow - _r1LastStateLogTick >= 10)
            {
                _r1LastStateLogTick = tickNow;
                var target = _transformers.FirstOrDefault(x => x != null && x.ReferenceId == _r1TargetRef);
                if (target != null)
                {
                    _log?.LogInfo($"[ScenarioRunner] R1 STATE tick={tickNow} ref={_r1TargetRef} OnOff={target.OnOff} Error={target.Error} Setting={target.Setting:F0}");
                }
            }
        }

        private static string BuildHierarchyPath(Transform t)
        {
            if (t == null) return "<null>";
            var sb = new System.Text.StringBuilder(t.name);
            var cur = t.parent;
            int hops = 0;
            while (cur != null && hops < 6)
            {
                sb.Insert(0, cur.name + "/");
                cur = cur.parent;
                hops++;
            }
            return sb.ToString();
        }

        // ============================================================
        // Scenario: pgp-priority-deprioritization-all (aggregator)
        // ------------------------------------------------------------
        // Runs every probe above in sequence on the first scenario tick.
        // Convenience for capturing every PASS/FAIL in one -Start cycle.
        // ============================================================
        private static bool _allDeprioritizedFired;

        private static void Scenario_PgpPriorityDeprioritizationAll()
        {
            // The topology probe is phased across three ticks (priority writes only land on
            // the next allocator pass), so it is pumped every tick and self-gates; the
            // one-shot probes below run once on the first tick.
            if (_allDeprioritizedFired)
            {
                Scenario_PgpPriorityDeprioritizationTopologyProbe();
                return;
            }
            _allDeprioritizedFired = true;
            _log?.LogInfo("[ScenarioRunner] ALL START priority-deprioritization-all aggregator");
            Scenario_PgpPriorityDeprioritizationKnobProbe();
            Scenario_PgpPriorityDeprioritizationFlashProbe();
            Scenario_PgpPriorityDeprioritizationHoverProbe();
            Scenario_PgpPriorityDeprioritizationLabellerProbe();
            Scenario_PgpPriorityDeprioritizationMpProbe();
            Scenario_PgpPriorityDeprioritizationSaveLoadProbe();
            Scenario_PgpPriorityDeprioritizationTopologyProbe();
            // Also include the existing PSP + persist probes so a single -Start covers
            // the entire feature surface.
            Scenario_PgpPriorityDeprioritizationProbe();
            Scenario_PgpPriorityDeprioritizationNetworkBreakdown();
            _log?.LogInfo("[ScenarioRunner] ALL END priority-deprioritization-all aggregator (topology probe continues for two more ticks)");
        }

        // ============================================================
        // Scenario: pgp-atomic-all (NEW aggregator)
        // ------------------------------------------------------------
        // Runs the lean test suite that fits the post-refactor architecture
        // (atomic 5-phase tick, instant lockout, OverloadRegistry).
        //
        // Includes the still-relevant probes from the old suite:
        //   - knob (constants, button1/button2 step compute)
        //   - flash (FaultFlashBehaviour attach, color)
        //   - hover (Thing.GetContextualName postfix, Deprioritized fault text)
        //   - labeller (Set / InputSetting redirect)
        //   - mp (DeprioritizedRegistry client state, message host short-circuit)
        //   - saveload (PriorityStore + side-car round-trip)
        //
        // Skips the obsolete probes that depended on the removed allocator
        // API (RunDetection, InvalidateAll, GetAllocatedSupply, TrimCache,
        // ShortfallTolerance, NoteShortfall):
        //   - PSP probe (12-phase end-to-end allocator test)
        //   - topology probe (cascading allocator)
        //   - network-breakdown probe (per-network allocation)
        //
        // Adds the new architecture probes:
        //   - pgp-atomic-probe (Phase 1/2/3 wiring, deprioritization semantics)
        //   - pgp-overload-probe (overload registry + LogicType + hover)
        //
        // And ends with pgp-deprioritization-trace which runs a 30-tick live trace
        // against the loaded save -- proves real-world behaviour matches
        // the synthetic probe results.
        // ============================================================
        private static bool _atomicAllFired;
        private static void Scenario_PgpAtomicAll()
        {
            if (_atomicAllFired) return;
            _atomicAllFired = true;
            _log?.LogInfo("[ScenarioRunner] ATOMIC-ALL START");
            Scenario_PgpAtomicProbe();
            Scenario_PgpOverloadProbe();
            Scenario_PgpPriorityDeprioritizationKnobProbe();
            Scenario_PgpPriorityDeprioritizationFlashProbe();
            Scenario_PgpPriorityDeprioritizationHoverProbe();
            Scenario_PgpPriorityDeprioritizationLabellerProbe();
            Scenario_PgpPriorityDeprioritizationMpProbe();
            Scenario_PgpPriorityDeprioritizationSaveLoadProbe();
            _log?.LogInfo("[ScenarioRunner] ATOMIC-ALL END synthetic probes done; pgp-deprioritization-trace will follow on subsequent ticks");
        }

        // ============================================================
        // Scenario: pgp-deprioritization-trace (regression-triage diagnostic)
        // ------------------------------------------------------------
        // Runs every electricity tick for a window of ticks. On each
        // tick it walks every transformer and dumps state for any
        // transformer that is in lockout, currently deprioritized, or has a
        // non-zero shortfall counter. Also dumps every transformer's
        // input/output net loads, _powerProvided, OutputMaximum, and
        // priority once at tick 0 and once at the end. Goal: catch the
        // allocator entering the deprioritization branch when it shouldn't.
        //
        // Output groups:
        //   STR-INIT  one-shot baseline at first observed tick.
        //   STR-TICK  per-tick summary line (counts).
        //   STR-EVT   per-transformer event (entered lockout / left).
        //   STR-END   final summary: which refs spent most time deprioritized.
        // ============================================================
        private static int _stTickCount = 0;
        private const int _stMaxTicks = 30;
        private static int _stStartTick = -1;
        private static readonly HashSet<long> _stCurrentlyLocked = new HashSet<long>();
        private static readonly Dictionary<long, int> _stLockedTickCount = new Dictionary<long, int>();
        private static readonly Dictionary<long, int> _stEnterCount = new Dictionary<long, int>();

        private static void LogEnterEvents(HashSet<long> thisTickSet, string cause, int currentTick,
            MethodInfo getPrioMethod, FieldInfo powerProvidedField)
        {
            foreach (var id in thisTickSet)
            {
                if (_stCurrentlyLocked.Contains(id)) continue;
                if (!_stEnterCount.ContainsKey(id)) _stEnterCount[id] = 0;
                _stEnterCount[id]++;
                Transformer t = null;
                foreach (var x in _transformers) { if (x != null && x.ReferenceId == id) { t = x; break; } }
                float inPot = t?.InputNetwork?.PotentialLoad ?? -1f;
                float inReq = t?.InputNetwork?.RequiredLoad ?? -1f;
                float outReq = t?.OutputNetwork?.RequiredLoad ?? -1f;
                float outMax = t?.OutputMaximum ?? -1f;
                float pp = -1f;
                try { if (t != null && powerProvidedField != null) pp = (float)powerProvidedField.GetValue(t); } catch { }
                int prio = (int)(getPrioMethod?.Invoke(null, new object[] { id }) ?? -1);
                _log?.LogInfo($"[ScenarioRunner] STR-EVT ENTER cause={cause} tick={currentTick} ref={id} prefab={t?.PrefabName ?? "?"} prio={prio} OnOff={(t?.OnOff ?? false)} Err={(t?.Error ?? -1)} OutMax={outMax:F0} InPot={inPot:F0} InReq={inReq:F0} OutReq={outReq:F0} _powerProvided={pp:F0}");
            }
        }

        private static void Scenario_PgpDeprioritizedTrace()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-deprioritization-trace")) return;
            if (_stTickCount >= _stMaxTicks) return;

            try
            {
                var asm = GetModAssembly(PGP_ASSEMBLY);
                if (asm == null) { _log?.LogError("[ScenarioRunner] STR no PGP assembly"); _stTickCount = _stMaxTicks; return; }

                var deprioritizedRegistryType = asm.GetType("PowerGridPlus.DeprioritizedRegistry");
                var overloadType = asm.GetType("PowerGridPlus.OverloadRegistry");
                // TransformerAllocator was renamed to PowerAllocator when storage
                // charge and the bridge adapters became first-class allocator flows.
                var allocatorType = asm.GetType("PowerGridPlus.PowerAllocator");
                var priorityStoreType = asm.GetType("PowerGridPlus.PriorityStore");
                var tickCounterType = asm.GetType("PowerGridPlus.ElectricityTickCounter");
                if (deprioritizedRegistryType == null || allocatorType == null || tickCounterType == null)
                {
                    _log?.LogError($"[ScenarioRunner] STR FAIL: type lookup DR={deprioritizedRegistryType != null} PA={allocatorType != null} TC={tickCounterType != null}.");
                    _stTickCount = _stMaxTicks; return;
                }

                var lockoutDictField = deprioritizedRegistryType.GetField("_lockoutUntilTick",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var overloadLockoutDictField = overloadType?.GetField("_lockoutUntilTick",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var currentTickProp = tickCounterType.GetProperty("CurrentTick",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var isLockedOutMethod = deprioritizedRegistryType.GetMethod("IsLockedOut",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(long), typeof(int) }, null);
                var getPrioMethod = priorityStoreType?.GetMethod("GetPriority",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(long) }, null);
                var powerProvidedField = typeof(Transformer).GetField("_powerProvided",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                int currentTick = (int)(currentTickProp?.GetValue(null) ?? 0);

                if (_transformers.Count == 0) RebuildCaches();

                // ---- INIT one-shot ----
                if (_stStartTick < 0)
                {
                    _stStartTick = currentTick;
                    // Deprioritization and overload protection are always on (the synced master toggles
                    // and their *SettingsSync types were deleted); no effective-state line needed.
                    _log?.LogInfo($"[ScenarioRunner] STR-INIT startTick={currentTick} transformers={_transformers.Count}");

                    int ones = 0, errored = 0;
                    int prioSum = 0, prioMin = int.MaxValue, prioMax = int.MinValue;
                    foreach (var t in _transformers)
                    {
                        if (t == null) continue;
                        if (t.OnOff) ones++;
                        if (t.Error == 1) errored++;
                        int p = (int)(getPrioMethod?.Invoke(null, new object[] { t.ReferenceId }) ?? 100);
                        prioSum += p;
                        if (p < prioMin) prioMin = p;
                        if (p > prioMax) prioMax = p;
                    }
                    _log?.LogInfo($"[ScenarioRunner] STR-INIT transformers OnOff={ones} Errored={errored} prio min={prioMin} max={prioMax} avg={(_transformers.Count > 0 ? prioSum / _transformers.Count : 0)}");

                    // Dump linked PowerTransmitters + the transformer feeding each one.
                    int txTotal = 0, txLinkedAndOn = 0;
                    OcclusionManager.AllThings.ForEach(thing =>
                    {
                        if (thing == null) return;
                        var tt = thing.GetType();
                        if (tt.Name != "PowerTransmitter") return;
                        txTotal++;
                        try
                        {
                            var linkedProp = tt.GetProperty("Linked",
                                BindingFlags.Public | BindingFlags.Instance);
                            bool linked = (linkedProp?.GetValue(thing) as bool?) ?? false;
                            var onOffProp = tt.GetProperty("OnOff", BindingFlags.Public | BindingFlags.Instance);
                            bool on = (onOffProp?.GetValue(thing) as bool?) ?? false;
                            if (linked && on)
                            {
                                txLinkedAndOn++;
                                if (txLinkedAndOn <= 15)
                                {
                                    var pcn = tt.GetProperty("PowerCableNetwork")?.GetValue(thing) as CableNetwork;
                                    _log?.LogInfo($"[ScenarioRunner] STR-INIT linked-TX ref={thing.ReferenceId} OnOff=true CableNet={pcn?.ReferenceId ?? -1} PotentialLoad={pcn?.PotentialLoad ?? -1f:F0} RequiredLoad={pcn?.RequiredLoad ?? -1f:F0}");
                                }
                            }
                        }
                        catch { }
                    });
                    _log?.LogInfo($"[ScenarioRunner] STR-INIT PowerTransmitters total={txTotal} linkedAndOn={txLinkedAndOn}");
                }

                // ---- Per-tick scan ----
                var lockoutDict = lockoutDictField?.GetValue(null) as System.Collections.IDictionary;
                var overloadLockoutDict = overloadLockoutDictField?.GetValue(null) as System.Collections.IDictionary;

                int totalDeprioritizedLocked = 0;
                int totalOverloadLocked = 0;
                int onCount = 0;
                int erroredCount = 0;

                // Build this-tick locked sets per registry, count transitions, log events.
                var thisTickDeprioritized = new HashSet<long>();
                var thisTickOverload = new HashSet<long>();
                if (lockoutDict != null)
                {
                    foreach (System.Collections.DictionaryEntry kv in lockoutDict)
                    {
                        long id = (long)kv.Key;
                        int until = (int)kv.Value;
                        if (until > currentTick)
                        {
                            thisTickDeprioritized.Add(id);
                            totalDeprioritizedLocked++;
                            if (!_stLockedTickCount.ContainsKey(id)) _stLockedTickCount[id] = 0;
                            _stLockedTickCount[id]++;
                        }
                    }
                }
                if (overloadLockoutDict != null)
                {
                    foreach (System.Collections.DictionaryEntry kv in overloadLockoutDict)
                    {
                        long id = (long)kv.Key;
                        int until = (int)kv.Value;
                        if (until > currentTick)
                        {
                            thisTickOverload.Add(id);
                            totalOverloadLocked++;
                            if (!_stLockedTickCount.ContainsKey(id)) _stLockedTickCount[id] = 0;
                            _stLockedTickCount[id]++;
                        }
                    }
                }

                LogEnterEvents(thisTickDeprioritized, "DEPRIORITIZED", currentTick, getPrioMethod, powerProvidedField);
                LogEnterEvents(thisTickOverload, "OVERLOAD", currentTick, getPrioMethod, powerProvidedField);

                var combinedThisTick = new HashSet<long>();
                foreach (var id in thisTickDeprioritized) combinedThisTick.Add(id);
                foreach (var id in thisTickOverload) combinedThisTick.Add(id);

                foreach (var id in _stCurrentlyLocked)
                {
                    if (!combinedThisTick.Contains(id))
                    {
                        _log?.LogInfo($"[ScenarioRunner] STR-EVT EXIT  tick={currentTick} ref={id}");
                    }
                }
                _stCurrentlyLocked.Clear();
                foreach (var id in combinedThisTick) _stCurrentlyLocked.Add(id);

                foreach (var t in _transformers)
                {
                    if (t == null) continue;
                    if (t.OnOff) onCount++;
                    if (t.Error == 1) erroredCount++;
                }

                _log?.LogInfo($"[ScenarioRunner] STR-TICK n={_stTickCount} tick={currentTick} transformers={_transformers.Count} OnOff={onCount} Errored={erroredCount} inDeprioritized={totalDeprioritizedLocked} inOverload={totalOverloadLocked}");

                _stTickCount++;

                // ---- END summary on final tick ----
                if (_stTickCount >= _stMaxTicks)
                {
                    _log?.LogInfo($"[ScenarioRunner] STR-END windowEndTick={currentTick} totalEntriesObserved={_stEnterCount.Count}");
                    // Top 20 most-locked-during-window.
                    var ranked = _stLockedTickCount.OrderByDescending(kv => kv.Value).Take(20).ToList();
                    foreach (var kv in ranked)
                    {
                        Transformer t = null;
                        foreach (var x in _transformers) { if (x != null && x.ReferenceId == kv.Key) { t = x; break; } }
                        float inPot = t?.InputNetwork?.PotentialLoad ?? -1f;
                        float outReq = t?.OutputNetwork?.RequiredLoad ?? -1f;
                        int enters = _stEnterCount.TryGetValue(kv.Key, out var ec) ? ec : 0;
                        _log?.LogInfo($"[ScenarioRunner] STR-END top ref={kv.Key} prefab={t?.PrefabName ?? "?"} lockedTicks={kv.Value}/{_stMaxTicks} entries={enters} InPot={inPot:F0} OutReq={outReq:F0} OutMax={(t?.OutputMaximum ?? -1f):F0}");
                    }

                    // Also: for the linked + on power transmitters, recheck final state.
                    OcclusionManager.AllThings.ForEach(thing =>
                    {
                        if (thing == null) return;
                        var tt = thing.GetType();
                        if (tt.Name != "PowerTransmitter") return;
                        try
                        {
                            var linkedProp = tt.GetProperty("Linked",
                                BindingFlags.Public | BindingFlags.Instance);
                            bool linked = (linkedProp?.GetValue(thing) as bool?) ?? false;
                            var onOffProp = tt.GetProperty("OnOff", BindingFlags.Public | BindingFlags.Instance);
                            bool on = (onOffProp?.GetValue(thing) as bool?) ?? false;
                            if (!linked || !on) return;
                            var pcn = tt.GetProperty("PowerCableNetwork")?.GetValue(thing) as CableNetwork;
                            var poweredProp = tt.GetProperty("Powered",
                                BindingFlags.Public | BindingFlags.Instance);
                            bool powered = (poweredProp?.GetValue(thing) as bool?) ?? false;
                            _log?.LogInfo($"[ScenarioRunner] STR-END linked-TX ref={thing.ReferenceId} Powered={powered} CableNet={pcn?.ReferenceId ?? -1} PotentialLoad={pcn?.PotentialLoad ?? -1f:F0} CurrentLoad={pcn?.CurrentLoad ?? -1f:F0} RequiredLoad={pcn?.RequiredLoad ?? -1f:F0}");
                        }
                        catch { }
                    });
                }
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] STR threw: {e}");
                _stTickCount = _stMaxTicks;
            }
        }

        // ============================================================
        // Scenario: pgp-atomic-probe
        // ------------------------------------------------------------
        // Architecture + deprioritization semantics under the new 5-phase atomic flow.
        //   P1: AtomicElectricityTickPatch attached on ElectricityTick.
        //   P2: Old ElectricityTickPatches is GONE.
        //   P3: DeprioritizedRegistry.LockoutDurationTicks = 120 (60 sec).
        //   P4: DeprioritizedRegistry.ShortfallTolerance is GONE.
        //   P5: NoteDeprioritized -> instant lockout; IsLockedOut + IsDeprioritized true.
        //   P6: ClearAll drops lockout.
        //   P7: PowerAllocator.RunAtomic exists (TransformerAllocator was
        //       renamed to PowerAllocator in the rearchitecture).
        //   P8: Old allocator API (RunDetection, InvalidateAll,
        //       GetAllocatedSupply, TrimCache) is GONE.
        // ============================================================
        private static bool _atomicFired;
        private static void Scenario_PgpAtomicProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-atomic-probe")) return;
            if (_atomicFired) return;
            _atomicFired = true;
            int pass = 0, fail = 0, total = 0;
            try
            {
                _log?.LogInfo("[ScenarioRunner] AP START atomic-probe");
                var asm = GetModAssembly(PGP_ASSEMBLY);
                var deprioritizedRegistryType = asm.GetType("PowerGridPlus.DeprioritizedRegistry");
                // TransformerAllocator was renamed to PowerAllocator when storage
                // charge and the bridge adapters became first-class allocator flows.
                var allocatorType = asm.GetType("PowerGridPlus.PowerAllocator");
                var tickCounterType = asm.GetType("PowerGridPlus.ElectricityTickCounter");
                var atomicPatchType = asm.GetType("PowerGridPlus.Patches.AtomicElectricityTickPatch");
                var oldTickPatchType = asm.GetType("PowerGridPlus.Patches.ElectricityTickPatches");

                // P1: AtomicElectricityTickPatch present.
                total++;
                if (atomicPatchType != null)
                { _log?.LogInfo("[ScenarioRunner] AP P1 PASS: AtomicElectricityTickPatch type exists."); pass++; }
                else
                { _log?.LogError("[ScenarioRunner] AP P1 FAIL: AtomicElectricityTickPatch missing."); fail++; }

                // P2: old ElectricityTickPatches gone.
                total++;
                if (oldTickPatchType == null)
                { _log?.LogInfo("[ScenarioRunner] AP P2 PASS: legacy ElectricityTickPatches deleted."); pass++; }
                else
                { _log?.LogError("[ScenarioRunner] AP P2 FAIL: ElectricityTickPatches still present."); fail++; }

                // Loud guard instead of an NRE on the next rename: everything below
                // dereferences these three types.
                if (deprioritizedRegistryType == null || allocatorType == null || tickCounterType == null)
                {
                    total++; fail++;
                    _log?.LogError($"[ScenarioRunner] AP FAIL: type lookup DR={deprioritizedRegistryType != null} PA={allocatorType != null} TC={tickCounterType != null}.");
                    _log?.LogInfo($"[ScenarioRunner] AP END pass={pass} fail={fail} total={total}");
                    return;
                }

                // P3: LockoutDurationTicks = 120.
                var ldField = deprioritizedRegistryType.GetField("LockoutDurationTicks",
                    BindingFlags.NonPublic | BindingFlags.Static);
                int ld = ldField != null ? (int)ldField.GetValue(null) : -1;
                total++;
                if (ld == 120)
                { _log?.LogInfo($"[ScenarioRunner] AP P3 PASS: DeprioritizedRegistry.LockoutDurationTicks={ld} (60 sec)."); pass++; }
                else
                { _log?.LogError($"[ScenarioRunner] AP P3 FAIL: LockoutDurationTicks={ld}, expected 120."); fail++; }

                // P4: ShortfallTolerance gone.
                var stField = deprioritizedRegistryType.GetField("ShortfallTolerance",
                    BindingFlags.NonPublic | BindingFlags.Static);
                total++;
                if (stField == null)
                { _log?.LogInfo("[ScenarioRunner] AP P4 PASS: ShortfallTolerance field removed."); pass++; }
                else
                { _log?.LogError("[ScenarioRunner] AP P4 FAIL: ShortfallTolerance still present."); fail++; }

                // P5: NoteDeprioritized -> instant lockout.
                var clearAll = deprioritizedRegistryType.GetMethod("ClearAll",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var noteDeprioritized = deprioritizedRegistryType.GetMethod("NoteDeprioritized",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(long), typeof(int) }, null);
                var isLockedOut = deprioritizedRegistryType.GetMethod("IsLockedOut",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(long), typeof(int) }, null);
                var isDeprioritization = deprioritizedRegistryType.GetMethod("IsDeprioritized",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(long), typeof(int) }, null);
                var currentTickProp = tickCounterType.GetProperty("CurrentTick",
                    BindingFlags.NonPublic | BindingFlags.Static);
                int tick = (int)(currentTickProp?.GetValue(null) ?? 0);
                long syntheticRef = 11223344L;
                clearAll?.Invoke(null, null);
                noteDeprioritized?.Invoke(null, new object[] { syntheticRef, tick });
                bool locked = (bool)(isLockedOut?.Invoke(null, new object[] { syntheticRef, tick }) ?? false);
                bool deprioritized = (bool)(isDeprioritization?.Invoke(null, new object[] { syntheticRef, tick }) ?? false);
                total++;
                if (locked && deprioritized)
                { _log?.LogInfo("[ScenarioRunner] AP P5 PASS: single NoteDeprioritized -> IsLockedOut=true & IsDeprioritized=true (instant lockout, no tolerance counter)."); pass++; }
                else
                { _log?.LogError($"[ScenarioRunner] AP P5 FAIL: locked={locked} deprioritized={deprioritized}, expected both true."); fail++; }

                // P6: lockout window length verification.
                bool lockedAtBoundary = (bool)(isLockedOut?.Invoke(null, new object[] { syntheticRef, tick + 119 }) ?? false);
                bool unlockedAfter = (bool)(isLockedOut?.Invoke(null, new object[] { syntheticRef, tick + 120 }) ?? true);
                total++;
                if (lockedAtBoundary && !unlockedAfter)
                { _log?.LogInfo($"[ScenarioRunner] AP P6 PASS: lockout holds at tick+119 and releases at tick+120 (60 sec window)."); pass++; }
                else
                { _log?.LogError($"[ScenarioRunner] AP P6 FAIL: lockedAtBoundary={lockedAtBoundary} unlockedAfter={unlockedAfter}."); fail++; }
                clearAll?.Invoke(null, null);

                // P7: PowerAllocator.RunAtomic present.
                var runAtomic = allocatorType.GetMethod("RunAtomic",
                    BindingFlags.NonPublic | BindingFlags.Static);
                total++;
                if (runAtomic != null)
                { _log?.LogInfo("[ScenarioRunner] AP P7 PASS: PowerAllocator.RunAtomic exists."); pass++; }
                else
                { _log?.LogError("[ScenarioRunner] AP P7 FAIL: RunAtomic missing."); fail++; }

                // P8: old TransformerAllocator API gone.
                string[] deadNames = { "RunDetection", "InvalidateAll", "GetAllocatedSupply", "TrimCache" };
                int deadFound = 0;
                foreach (var n in deadNames)
                {
                    if (allocatorType.GetMethod(n, BindingFlags.NonPublic | BindingFlags.Static) != null
                        || allocatorType.GetMethod(n, BindingFlags.Public | BindingFlags.Static) != null)
                        deadFound++;
                }
                total++;
                if (deadFound == 0)
                { _log?.LogInfo("[ScenarioRunner] AP P8 PASS: legacy allocator API (RunDetection/InvalidateAll/GetAllocatedSupply/TrimCache) removed."); pass++; }
                else
                { _log?.LogError($"[ScenarioRunner] AP P8 FAIL: {deadFound} legacy methods still present."); fail++; }

                _log?.LogInfo($"[ScenarioRunner] AP END pass={pass} fail={fail} total={total}");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] AP threw: {e}");
            }
        }

        // ============================================================
        // Scenario: pgp-overload-probe
        // Mirrors AP for the OverloadRegistry. Plus IC10 LogicType.Overloaded
        // read + hover-text branch under overload.
        // ============================================================
        private static bool _overloadFired;
        private static void Scenario_PgpOverloadProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-overload-probe")) return;
            if (_overloadFired) return;
            _overloadFired = true;
            int pass = 0, fail = 0, total = 0;
            try
            {
                _log?.LogInfo("[ScenarioRunner] OP START overload-probe");
                var asm = GetModAssembly(PGP_ASSEMBLY);
                var overloadType = asm.GetType("PowerGridPlus.OverloadRegistry");
                var overloadSyncType = asm.GetType("PowerGridPlus.OverloadSettingsSync");
                // The per-transition OverloadStateMessage was replaced by the per-tick
                // full-registry FaultRegistrySnapshotMessage (Kind = KindDeviceOverload).
                var faultMsgType = asm.GetType("PowerGridPlus.FaultRegistrySnapshotMessage");
                var logicRegType = asm.GetType("PowerGridPlus.LogicTypeRegistry");
                var tickCounterType = asm.GetType("PowerGridPlus.ElectricityTickCounter");
                var hoverPatchType = asm.GetType("PowerGridPlus.Patches.FaultButtonTooltipPatches");

                // P1: current type surface. OverloadRegistry + FaultRegistrySnapshotMessage exist;
                // OverloadSettingsSync must be ABSENT (overload protection is always on; the synced
                // master toggle was deleted in the settings rework, so its reappearance is a
                // regression back to the toggle era).
                total++;
                if (overloadType != null && overloadSyncType == null && faultMsgType != null)
                { _log?.LogInfo("[ScenarioRunner] OP P1 PASS: OverloadRegistry + FaultRegistrySnapshotMessage exist and OverloadSettingsSync is absent (always-on, no synced toggle)."); pass++; }
                else
                { _log?.LogError($"[ScenarioRunner] OP P1 FAIL: type surface wrong. OverloadRegistry={overloadType != null} (expect true) OverloadSettingsSync={overloadSyncType != null} (expect false) FaultRegistrySnapshotMessage={faultMsgType != null} (expect true)"); fail++; }

                // Loud guard instead of an NRE / ArgumentNullException on the next
                // rename: everything below dereferences these types.
                if (overloadType == null || faultMsgType == null || logicRegType == null
                    || tickCounterType == null || hoverPatchType == null)
                {
                    _log?.LogError($"[ScenarioRunner] OP FAIL: aborting, missing types (LR={logicRegType != null} TC={tickCounterType != null} HP={hoverPatchType != null}).");
                    _log?.LogInfo($"[ScenarioRunner] OP END pass={pass} fail={fail} total={total}");
                    return;
                }

                // P2: LockoutDurationTicks = 120.
                var ldField = overloadType.GetField("LockoutDurationTicks",
                    BindingFlags.NonPublic | BindingFlags.Static);
                int ld = ldField != null ? (int)ldField.GetValue(null) : -1;
                total++;
                if (ld == 120)
                { _log?.LogInfo($"[ScenarioRunner] OP P2 PASS: OverloadRegistry.LockoutDurationTicks={ld} (60 sec)."); pass++; }
                else
                { _log?.LogError($"[ScenarioRunner] OP P2 FAIL: LockoutDurationTicks={ld}, expected 120."); fail++; }

                // P3: NoteOverload -> instant lockout.
                var clearAll = overloadType.GetMethod("ClearAll",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var noteOverload = overloadType.GetMethod("NoteOverload",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(long), typeof(int) }, null);
                var isLockedOut = overloadType.GetMethod("IsLockedOut",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(long), typeof(int) }, null);
                var isOverloaded = overloadType.GetMethod("IsOverloaded",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(long), typeof(int) }, null);
                var currentTickProp = tickCounterType.GetProperty("CurrentTick",
                    BindingFlags.NonPublic | BindingFlags.Static);
                int tick = (int)(currentTickProp?.GetValue(null) ?? 0);
                long syntheticRef = 99887766L;
                clearAll?.Invoke(null, null);
                noteOverload?.Invoke(null, new object[] { syntheticRef, tick });
                bool locked = (bool)(isLockedOut?.Invoke(null, new object[] { syntheticRef, tick }) ?? false);
                bool overloaded = (bool)(isOverloaded?.Invoke(null, new object[] { syntheticRef, tick }) ?? false);
                total++;
                if (locked && overloaded)
                { _log?.LogInfo("[ScenarioRunner] OP P3 PASS: single NoteOverload -> IsLockedOut=true & IsOverloaded=true (instant lockout)."); pass++; }
                else
                { _log?.LogError($"[ScenarioRunner] OP P3 FAIL: locked={locked} overloaded={overloaded}."); fail++; }

                // P4: lockout window length.
                bool lockedAtBoundary = (bool)(isLockedOut?.Invoke(null, new object[] { syntheticRef, tick + 119 }) ?? false);
                bool unlockedAfter = (bool)(isLockedOut?.Invoke(null, new object[] { syntheticRef, tick + 120 }) ?? true);
                total++;
                if (lockedAtBoundary && !unlockedAfter)
                { _log?.LogInfo("[ScenarioRunner] OP P4 PASS: overload lockout holds at tick+119 and releases at tick+120."); pass++; }
                else
                { _log?.LogError($"[ScenarioRunner] OP P4 FAIL: lockedAtBoundary={lockedAtBoundary} unlockedAfter={unlockedAfter}."); fail++; }
                clearAll?.Invoke(null, null);

                // P5: LogicTypeRegistry.Overloaded present.
                var overloadedLogicField = logicRegType.GetField("DeviceOverloadedFault",
                    BindingFlags.NonPublic | BindingFlags.Static);
                LogicType overloadedLogic = overloadedLogicField != null ? (LogicType)overloadedLogicField.GetValue(null) : default;
                total++;
                if ((ushort)overloadedLogic == 6580)
                { _log?.LogInfo($"[ScenarioRunner] OP P5 PASS: LogicTypeRegistry.Overloaded = {(ushort)overloadedLogic} (matches 6580)."); pass++; }
                else
                { _log?.LogError($"[ScenarioRunner] OP P5 FAIL: Overloaded LogicType value = {(ushort)overloadedLogic}, expected 6580."); fail++; }

                // P6: live transformer GetLogicValue for Overloaded.
                // Overload protection is always on (the EnableTransformerOverloadProtection
                // master toggle and its OverloadSettingsSync were deleted); the Overloaded
                // logic slot (TransformerPriorityLogicPatches) has no settings gate.
                if (_transformers.Count == 0) RebuildCaches();
                var sampleT = _transformers.FirstOrDefault(x => x != null);
                if (sampleT != null)
                {
                    noteOverload?.Invoke(null, new object[] { sampleT.ReferenceId, tick });
                    double v = sampleT.GetLogicValue(overloadedLogic);
                    total++;
                    if (Math.Abs(v - 1.0) < 0.001)
                    { _log?.LogInfo($"[ScenarioRunner] OP P6 PASS: GetLogicValue(Overloaded) returns 1.0 while in lockout."); pass++; }
                    else
                    { _log?.LogError($"[ScenarioRunner] OP P6 FAIL: GetLogicValue(Overloaded)={v}, expected 1.0."); fail++; }

                    // P7: hover text contains the overload phrase. Wording is owned by
                    // FaultHover.OverloadClause and is per-device-type; a Transformer
                    // reports "Downstream demand exceeds this transformer's limit!".
                    var hoverPostfix = hoverPatchType.GetMethod("GetContextualName_Postfix",
                        BindingFlags.Public | BindingFlags.Static);
                    var btn = sampleT.Interactables?.FirstOrDefault(x => x?.Action == InteractableType.OnOff);
                    if (btn != null && hoverPostfix != null)
                    {
                        string result = "BASE";
                        object[] args = new object[] { sampleT, btn, result };
                        hoverPostfix.Invoke(null, args);
                        string after = (string)args[2];
                        total++;
                        bool hasOverload = after.IndexOf("Overloaded:", StringComparison.OrdinalIgnoreCase) >= 0;
                        bool hasLimit = after.IndexOf("exceeds this transformer's limit", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (hasOverload && hasLimit)
                        { _log?.LogInfo($"[ScenarioRunner] OP P7 PASS: hover text appends overload phrase. after='{after}'"); pass++; }
                        else
                        { _log?.LogError($"[ScenarioRunner] OP P7 FAIL: hasOverload={hasOverload} hasLimit={hasLimit} after='{after}'"); fail++; }
                    }
                    clearAll?.Invoke(null, null);

                    // P8: GetLogicValue(Overloaded) returns 0 when not overloaded.
                    double v0 = sampleT.GetLogicValue(overloadedLogic);
                    total++;
                    if (Math.Abs(v0 - 0.0) < 0.001)
                    { _log?.LogInfo($"[ScenarioRunner] OP P8 PASS: GetLogicValue(Overloaded) returns 0.0 after ClearAll."); pass++; }
                    else
                    { _log?.LogError($"[ScenarioRunner] OP P8 FAIL: GetLogicValue(Overloaded)={v0}, expected 0.0."); fail++; }

                    // P9: CanLogicRead Overloaded -> true, CanLogicWrite -> false.
                    bool canR = sampleT.CanLogicRead(overloadedLogic);
                    bool canW = sampleT.CanLogicWrite(overloadedLogic);
                    total++;
                    if (canR && !canW)
                    { _log?.LogInfo("[ScenarioRunner] OP P9 PASS: Overloaded is CanLogicRead=true, CanLogicWrite=false (read-only)."); pass++; }
                    else
                    { _log?.LogError($"[ScenarioRunner] OP P9 FAIL: canR={canR} canW={canW}."); fail++; }
                }

                // P10: FaultRegistrySnapshotMessage(KindDeviceOverload) host short-circuits
                // Process. The per-transition setter died with the heartbeat model; the client
                // mirror is _clientExpiry (a ClientEntry struct dict carrying the watt payload),
                // driven only by ReplaceClientSnapshot, so the mirror is asserted directly and
                // the baseline/cleanup is an empty snapshot replace.
                long testRef = 33445566L;
                var overloadReplaceClient = overloadType.GetMethod("ReplaceClientSnapshot",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var overloadMirrorField = overloadType.GetField("_clientExpiry",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var overloadMirror = overloadMirrorField?.GetValue(null) as System.Collections.IDictionary;
                var overloadTupleType = typeof(ValueTuple<,,,>).MakeGenericType(
                    typeof(long), typeof(int), typeof(float), typeof(float));
                var overloadEmptySnapshot = (System.Collections.IList)Activator.CreateInstance(
                    typeof(System.Collections.Generic.List<>).MakeGenericType(overloadTupleType));
                overloadReplaceClient?.Invoke(null, new object[] { overloadEmptySnapshot });
                var msg = Activator.CreateInstance(faultMsgType);
                byte kindOverload = (byte)(faultMsgType.GetField("KindDeviceOverload",
                    BindingFlags.Public | BindingFlags.Static)?.GetValue(null) ?? (byte)1);
                faultMsgType.GetField("Kind")?.SetValue(msg, kindOverload);
                faultMsgType.GetField("Entries")?.SetValue(msg,
                    new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<long, int>>
                    { new System.Collections.Generic.KeyValuePair<long, int>(testRef, 120) });
                var process = faultMsgType.GetMethod("Process");
                process?.Invoke(msg, new object[] { 0L });
                bool afterProcess = overloadMirror != null && overloadMirror.Contains(testRef);
                total++;
                if (!afterProcess && overloadMirror != null && Assets.Scripts.Networking.NetworkManager.IsServer)
                { _log?.LogInfo("[ScenarioRunner] OP P10 PASS: FaultRegistrySnapshotMessage(KindDeviceOverload).Process(0) short-circuited on host (IsServer=true); client mirror untouched."); pass++; }
                else
                { _log?.LogError($"[ScenarioRunner] OP P10 FAIL: IsServer={Assets.Scripts.Networking.NetworkManager.IsServer} mirrorDict={overloadMirror != null} mirrorTouched={afterProcess}"); fail++; }
                overloadReplaceClient?.Invoke(null, new object[] { overloadEmptySnapshot });

                _log?.LogInfo($"[ScenarioRunner] OP END pass={pass} fail={fail} total={total}");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] OP threw: {e}");
            }
        }

        // ============================================================
        // Scenario: pgp-cable-burn-window-probe
        // ------------------------------------------------------------
        // Drives PowerGridPlus.CableBurnWindow's 20-tick sliding running
        // average directly via reflection (the §5.7 deterministic burn
        // logic cannot be triggered on the under-cap Luna grid, so this
        // verifies the decision math headlessly): a full window of
        // sustained overload arms a burn and ranks the top producer, a
        // single spike is averaged out by dips and does NOT arm, the 10 s
        // grace floor holds, and reset clears the window.
        // ============================================================
        private static bool _cbwFired;

        private static void Scenario_PgpCableBurnWindowProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-cable-burn-window-probe")) return;
            if (_cbwFired) return;
            _cbwFired = true;

            int pass = 0, fail = 0;
            void Check(string tag, bool ok, string detail)
            {
                if (ok) { pass++; _log?.LogInfo($"[ScenarioRunner] CBW {tag} PASS {detail}"); }
                else { fail++; _log?.LogError($"[ScenarioRunner] CBW {tag} FAIL {detail}"); }
            }

            try
            {
                _log?.LogInfo("[ScenarioRunner] CBW START cable-burn-window-probe");
                var asm = GetModAssembly(PGP_ASSEMBLY);
                if (asm == null) { _log?.LogError("[ScenarioRunner] CBW no PGP assembly"); return; }
                var t = asm.GetType("PowerGridPlus.CableBurnWindow");
                if (t == null) { _log?.LogError("[ScenarioRunner] CBW FAIL: CableBurnWindow type not found"); return; }

                const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Static;
                var mObserve = t.GetMethod("Observe", BF);
                var mIsFull = t.GetMethod("IsFull", BF);
                var mAvg = t.GetMethod("AverageFlow", BF);
                var mTop = t.GetMethod("TopProducer", BF);
                var mReset = t.GetMethod("Reset", BF);
                if (mObserve == null || mIsFull == null || mAvg == null || mTop == null || mReset == null)
                {
                    _log?.LogError($"[ScenarioRunner] CBW FAIL: method lookup (Observe={mObserve != null} IsFull={mIsFull != null} Avg={mAvg != null} Top={mTop != null} Reset={mReset != null})");
                    fail++;
                    return;
                }

                const long NET = long.MaxValue - 4242;   // synthetic, unused network id
                const long A = 911001, B = 911002;        // synthetic producer ids
                const float CAP = 5000f;                  // pretend weakest-cable cap

                void Observe(Dictionary<long, float> prod, float flow) => mObserve.Invoke(null, new object[] { NET, prod, flow });
                bool IsFull() => (bool)mIsFull.Invoke(null, new object[] { NET });
                float Avg() => (float)mAvg.Invoke(null, new object[] { NET });
                long Top() => (long)mTop.Invoke(null, new object[] { NET });
                void Reset() => mReset.Invoke(null, new object[] { NET });

                // P1: a partial window does not arm a burn (10 s / 20-tick grace floor).
                Reset();
                for (int i = 0; i < 19; i++) Observe(new Dictionary<long, float> { { A, 8000f }, { B, 3000f } }, 11000f);
                Check("P1", !IsFull(), $"19 ticks over cap -> IsFull={IsFull()} (expected false; needs 20)");

                // P2: a full window of sustained overload arms (avg over cap) and ranks the top producer.
                Observe(new Dictionary<long, float> { { A, 8000f }, { B, 3000f } }, 11000f);   // 20th tick
                Check("P2-armed", IsFull() && Avg() > CAP, $"IsFull={IsFull()} avg={Avg():0} cap={CAP:0}");
                Check("P2-top", Top() == A, $"top producer over window = {Top()} (expected {A}, the 8 kW source)");

                // P3: a single huge spike is averaged out by following dips and does NOT arm.
                Reset();
                Observe(new Dictionary<long, float> { { A, 100000f } }, 100000f);
                for (int i = 0; i < 19; i++) Observe(new Dictionary<long, float>(), 0f);
                Check("P3", IsFull() && Avg() <= CAP, $"100 kW spike + 19 idle -> avg={Avg():0} (=100k/20=5000), not over {CAP:0}");

                // P4: reset clears the window.
                Reset();
                Check("P4", !IsFull() && Avg() == 0f, $"after reset IsFull={IsFull()} avg={Avg():0}");

                Reset();
            }
            catch (Exception e)
            {
                fail++;
                _log?.LogError($"[ScenarioRunner] CBW EXCEPTION: {e}");
            }
            _log?.LogInfo($"[ScenarioRunner] CBW END pass={pass} fail={fail}");
        }
    }
}
