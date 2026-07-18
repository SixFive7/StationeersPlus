using HarmonyLib;
// ApcMaterialChanger lives in the bare `Effects` namespace (decompile L191915-L191917),
// alongside its base MaterialChanger; neither is under Assets.Scripts. Same split as
// Objects.SwitchOnOff in SwitchOnOffFaultPatches.
using VanillaApcMaterialChanger = global::Effects.ApcMaterialChanger;

namespace PowerGridPlus.Patches
{
    // Companion to FaultFlashBehaviour's APC charge-LED targeting (DiscoverRenderers tier 0),
    // mirroring SwitchOnOffFaultPatches around vanilla ApcMaterialChanger.RefreshState:
    //
    //   1. While the owning APC is in a fault lockout, the vanilla body is skipped (return
    //      false) so a state transition does not overwrite FaultFlashBehaviour's emissive
    //      material swap with a charge-state material (decompile L191966-192013: RefreshState
    //      dispatches ChangeState material swaps and starts the blink tasks).
    //
    //      Scope caveat: RefreshState is only the SINGLE-WRITE entry point (dispatched from
    //      AreaPowerControl.RefreshAnimState, decompile L390689-390695). The free-running
    //      blink loops (ErrorAnim 250 ms, ChargingAnim / DischargingAnim 500 ms) poll the
    //      parent's Mode / Error / OnOff predicates directly and never re-enter RefreshState,
    //      so a loop already running when the fault lands keeps writing the LED. That is
    //      fine: FaultFlashBehaviour's per-frame LateUpdate re-asserts the flash material,
    //      landing after the loop write in any frame, so a loop write is visible for at most
    //      one frame. This prefix exists to stop NEW single-writes (mode / error / on-off
    //      transitions) from racing the flash, not to kill the loops.
    //
    //   2. OFF-as-reset (POWER.md §10.3, uniform across all faults): toggling the APC OFF
    //      during a fault clears its lockouts immediately so the player can retry without
    //      waiting out the 60-second timer. The next allocator / detector pass re-decides; if
    //      the condition still holds, the lockout re-fires instantly. Returning true lets
    //      vanilla apply the idle-state material so the LED stops flashing.
    [HarmonyPatch(typeof(VanillaApcMaterialChanger))]
    public static class ApcLedFaultPatches
    {
        [HarmonyPrefix, HarmonyPatch("RefreshState")]
        public static bool RefreshState_Prefix(VanillaApcMaterialChanger __instance)
        {
            // ApcMaterialChanger reaches its owner through its serialized public `parent`
            // Thing field (decompile L191919-191920); the APC prefab wires it to the
            // AreaPowerControl. Null or non-Device parent: let vanilla run (its own body
            // early-outs on a null parent).
            if (!(__instance.parent is Assets.Scripts.Objects.Pipes.Device device)) return true;

            long faultRefId = FaultHover.ResolveFaultRefId(device);
            int tick = ElectricityTickCounter.CurrentTick;
            var fault = FaultHover.ActiveFault(faultRefId, tick);
            if (fault == FaultHover.Kind.None) return true;

            if (!device.OnOff)
            {
                // OFF-as-reset: clear every lockout on this device (both host dicts and client
                // mirrors; the per-tick snapshot sync propagates the cleared state to other peers).
                DeprioritizedRegistry.ClearLockout(faultRefId);
                OverloadRegistry.ClearLockout(faultRefId);
                CableOverloadRegistry.ClearLockout(faultRefId);
                CycleFaultRegistry.ClearLockout(faultRefId);
                CurrentMismatchFaultRegistry.ClearLockout(faultRefId);
                return true;   // vanilla applies the idle-state material
            }

            // Fault active and APC ON: skip vanilla so the flash material survives transitions.
            return false;
        }
    }
}
