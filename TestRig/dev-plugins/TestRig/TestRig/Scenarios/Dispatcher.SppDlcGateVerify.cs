using System;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Objects;

namespace TestRig.Scenarios
{
    // Scenario: spp-dlc-gate-verify
    //
    // One-shot. Verifies Spray Paint Plus's DLC entitlement gate without a client and
    // without owning the Metallic Paints DLC, by driving the shared-DLC pool directly.
    //
    // What it asserts, over every index in GameManager.CustomColors:
    //   Case 1  pool empty                    -> vanilla colors allowed, DLC colors blocked
    //   Case 2  pool has MetallicPaints       -> every color allowed
    //   Case 3  pool empty again              -> back to Case 1 (no state stuck on)
    //
    // Entitlement is the entire subject. Every scroll below runs under
    // ColorCyclingMode.AllColors, the most permissive mode, so the only thing that can
    // take a color out of the walk is the gate. Before v1.11.0 there was a fourth case
    // driving the "Enable Metallic Paints" toggle, which allowed a DLC color while
    // keeping it out of the scroll cycle. That toggle is gone: the ColorCyclingMode
    // ladder replaced it, and its restriction is relative to the color already on the
    // can, so no per-index predicate can express it. DlcPaintGate.IsColorInCycle now
    // delegates straight to IsColorAllowed, which is why the two expectations below
    // always coincide; keeping both asserted is the tripwire for a future client-local
    // filter leaking into the entitlement answer. Coverage of the mode itself lives in
    // spp-settings-merge-verify (P2 for the ladder, P5 for WithinFamily).
    //
    // It also drives ColorCyclerPatch.NextColorInCycle, the actual scroll-step function, so
    // the test covers the behavior a player sees rather than just the predicate: with an
    // empty pool, scrolling forward from the last vanilla color must wrap to index 0 and
    // skip the DLC block entirely.
    //
    // Why the pool is safe to write here: SharedDLCManager.SharedDLC is a plain static
    // ushort whose setter only raises a network flag when IsServer && HasClients. This runs
    // on a headless dedi with nobody connected, so the write is local and reversible, and
    // Case 3 restores it. A batch-mode dedi never seeds the pool anyway
    // (SharedDLCManager.HostFinishedLoad skips seeding under IsBatchMode), so the natural
    // starting state is empty, which is exactly the state a non-owner sees.
    //
    // Reflection throughout: the gate and the scroll helper are internal / private to the
    // mod, and ScenarioRunner has no build-time dependency on it. ColorCyclingMode is
    // resolved as a Type and its member read with Enum.Parse for the same reason, so the
    // scenario never names the enum at compile time. Managed state only, so this is safe
    // on the UniTask worker the sim-tick pump runs on.

    internal static partial class Dispatcher
    {
        private const string SPP_ASSEMBLY = "SprayPaintPlus";

        private static bool _sppGateVerifyFired;

        private static void Scenario_SppDlcGateVerify()
        {
            if (!RequireModAssembly(SPP_ASSEMBLY, "spp-dlc-gate-verify")) return;
            if (_sppGateVerifyFired) return;
            _sppGateVerifyFired = true;

            try
            {
                _log?.LogInfo("[ScenarioRunner] spp-dlc-gate-verify START");

                var asm = GetModAssembly(SPP_ASSEMBLY);
                var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

                var gate = asm.GetType("SprayPaintPlus.DlcPaintGate");
                var cycler = asm.GetType("SprayPaintPlus.ColorCyclerPatch");
                var modeT = asm.GetType("SprayPaintPlus.ColorCyclingMode");
                if (gate == null || cycler == null || modeT == null)
                {
                    _log?.LogError("[ScenarioRunner] spp-dlc-gate | could not resolve mod types " +
                                   $"(gate={gate != null} cycler={cycler != null} mode={modeT != null}), aborting");
                    return;
                }

                var isAllowed = gate.GetMethod("IsColorAllowed", flags);
                var isInCycle = gate.GetMethod("IsColorInCycle", flags);

                // Four parameters since v1.11.0: (int from, int colorCount, bool forward,
                // ColorCyclingMode mode). Bound by explicit type array so an added overload
                // cannot make this ambiguous.
                var nextInCycle = cycler.GetMethod("NextColorInCycle", flags, null,
                    new[] { typeof(int), typeof(int), typeof(bool), modeT }, null);

                // The most permissive mode, so the gate is the only filter in play. Parsed
                // off the reflected type, never named at compile time.
                object allColors = Enum.IsDefined(modeT, "AllColors")
                    ? Enum.Parse(modeT, "AllColors")
                    : null;

                if (isAllowed == null || isInCycle == null || nextInCycle == null || allColors == null)
                {
                    _log?.LogError("[ScenarioRunner] spp-dlc-gate | could not resolve gate methods " +
                                   $"(isColorAllowed={isAllowed != null} isColorInCycle={isInCycle != null} " +
                                   $"nextColorInCycle={nextInCycle != null} allColorsMode={allColors != null}), aborting");
                    return;
                }

                var colors = GameManager.Instance?.CustomColors;
                if (colors == null || colors.Count == 0)
                {
                    _log?.LogError("[ScenarioRunner] spp-dlc-gate | CustomColors unavailable, aborting");
                    return;
                }
                int count = colors.Count;

                // DLC.SharedDLCManager.SharedDLC (public static ushort).
                var sharedType = Type.GetType("DLC.SharedDLCManager, Assembly-CSharp");
                var sharedProp = sharedType?.GetProperty("SharedDLC", flags);
                if (sharedProp == null)
                {
                    _log?.LogError("[ScenarioRunner] spp-dlc-gate | could not resolve SharedDLCManager.SharedDLC, aborting");
                    return;
                }

                ushort originalPool = (ushort)sharedProp.GetValue(null);
                const ushort METALLIC_PAINTS = 0x100;
                _log?.LogInfo($"[ScenarioRunner] spp-dlc-gate | swatches={count} poolAtStart=0x{originalPool:X} " +
                              $"cyclingMode={allColors}");

                bool ok = true;

                try
                {
                    // ---- Case 1: nobody in the session owns Metallic Paints ----
                    sharedProp.SetValue(null, (ushort)0);
                    ok &= Report("case1-pool-empty", colors, count, isAllowed, isInCycle,
                        expectDlcAllowed: false, expectDlcInCycle: false);

                    // The scroll a player actually performs. Forward from the last vanilla
                    // color must wrap past the DLC block to index 0.
                    int fromLastVanilla = 11;
                    int stepped = (int)nextInCycle.Invoke(null, new object[] { fromLastVanilla, count, true, allColors });
                    bool wrapOk = stepped == 0;
                    ok &= wrapOk;
                    _log?.LogInfo($"[ScenarioRunner] spp-dlc-gate | case1 scroll {fromLastVanilla} forward -> {stepped} " +
                                  $"(expected 0, skipping the DLC block) {(wrapOk ? "PASS" : "FAIL")}");

                    // Backward off index 0 must land on the last NON-DLC color, not index 15.
                    int steppedBack = (int)nextInCycle.Invoke(null, new object[] { 0, count, false, allColors });
                    bool backOk = steppedBack == 11;
                    ok &= backOk;
                    _log?.LogInfo($"[ScenarioRunner] spp-dlc-gate | case1 scroll 0 backward -> {steppedBack} " +
                                  $"(expected 11) {(backOk ? "PASS" : "FAIL")}");

                    // A can already sitting on a gated color must still be able to scroll OFF it.
                    int offGated = (int)nextInCycle.Invoke(null, new object[] { 12, count, true, allColors });
                    bool offOk = offGated == 0;
                    ok &= offOk;
                    _log?.LogInfo($"[ScenarioRunner] spp-dlc-gate | case1 scroll 12 forward -> {offGated} " +
                                  $"(expected 0, can must escape a gated color) {(offOk ? "PASS" : "FAIL")}");

                    // ---- Case 2: someone in the session owns it ----
                    sharedProp.SetValue(null, METALLIC_PAINTS);
                    ok &= Report("case2-pool-has-metallic", colors, count, isAllowed, isInCycle,
                        expectDlcAllowed: true, expectDlcInCycle: true);

                    int steppedOwned = (int)nextInCycle.Invoke(null, new object[] { 11, count, true, allColors });
                    bool ownedOk = steppedOwned == 12;
                    ok &= ownedOk;
                    _log?.LogInfo($"[ScenarioRunner] spp-dlc-gate | case2 scroll 11 forward -> {steppedOwned} " +
                                  $"(expected 12, owner reaches the DLC block) {(ownedOk ? "PASS" : "FAIL")}");

                    // ---- Case 3: entitlement withdrawn, nothing latched on ----
                    sharedProp.SetValue(null, (ushort)0);
                    ok &= Report("case3-pool-empty-again", colors, count, isAllowed, isInCycle,
                        expectDlcAllowed: false, expectDlcInCycle: false);

                    // The scroll is where a latch would show first, since case 2 walked it
                    // into the DLC block. Same step as case 1, same answer expected.
                    int steppedAgain = (int)nextInCycle.Invoke(null, new object[] { 11, count, true, allColors });
                    bool againOk = steppedAgain == 0;
                    ok &= againOk;
                    _log?.LogInfo($"[ScenarioRunner] spp-dlc-gate | case3 scroll 11 forward -> {steppedAgain} " +
                                  $"(expected 0, the case 2 walk left nothing latched) {(againOk ? "PASS" : "FAIL")}");
                }
                finally
                {
                    sharedProp.SetValue(null, originalPool);
                    _log?.LogInfo($"[ScenarioRunner] spp-dlc-gate | restored pool=0x{originalPool:X}");
                }

                _log?.LogInfo($"[ScenarioRunner] spp-dlc-gate | RESULT {(ok ? "ALL PASS" : "FAILURES PRESENT")}");
                _log?.LogInfo("[ScenarioRunner] spp-dlc-gate-verify END");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] spp-dlc-gate-verify threw: {e}");
            }
        }

        // Walks every swatch and checks the two predicates against expectations. A swatch is
        // treated as DLC-gated purely by whether the mod itself reports it unavailable with an
        // empty pool, which is established once in case 1 and reused: deciding "is this a DLC
        // color" from PaintOnly would be testing the mod against the wrong oracle.
        private static bool[] _sppDlcColor;

        private static bool Report(string label, System.Collections.Generic.List<ColorSwatch> colors, int count,
            MethodInfo isAllowed, MethodInfo isInCycle,
            bool expectDlcAllowed, bool expectDlcInCycle)
        {
            if (_sppDlcColor == null)
            {
                // First pass (case 1, empty pool): whatever the gate refuses IS the gated set.
                _sppDlcColor = new bool[count];
                for (int i = 0; i < count; i++)
                    _sppDlcColor[i] = !(bool)isAllowed.Invoke(null, new object[] { i });

                int gatedCount = 0;
                for (int i = 0; i < count; i++) if (_sppDlcColor[i]) gatedCount++;
                _log?.LogInfo($"[ScenarioRunner] spp-dlc-gate | gated set discovered: {gatedCount} of {count} colors");
            }

            bool ok = true;
            for (int i = 0; i < count; i++)
            {
                bool allowed = (bool)isAllowed.Invoke(null, new object[] { i });
                bool inCycle = (bool)isInCycle.Invoke(null, new object[] { i });

                bool wantAllowed = _sppDlcColor[i] ? expectDlcAllowed : true;
                bool wantInCycle = _sppDlcColor[i] ? expectDlcInCycle : true;

                if (allowed != wantAllowed || inCycle != wantInCycle)
                {
                    ok = false;
                    _log?.LogError(
                        $"[ScenarioRunner] spp-dlc-gate | {label} FAIL idx={i} name={colors[i]?.Name} " +
                        $"dlcColor={_sppDlcColor[i]} allowed={allowed} (want {wantAllowed}) " +
                        $"inCycle={inCycle} (want {wantInCycle})");
                }
            }

            _log?.LogInfo($"[ScenarioRunner] spp-dlc-gate | {label} {(ok ? "PASS" : "FAIL")} " +
                          $"(expect gated: allowed={expectDlcAllowed} inCycle={expectDlcInCycle})");
            return ok;
        }
    }
}
