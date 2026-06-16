using Assets.Scripts.Objects;
using HarmonyLib;
// SwitchOnOff and DevicePart live in the bare `Objects` namespace (decompile
// L137865 + L138371 + L138394), not `Assets.Scripts.Objects`. Stationeers uses
// both namespaces for different classes.
using VanillaSwitchOnOff = global::Objects.SwitchOnOff;
using VanillaDevicePart = global::Objects.DevicePart;

namespace PowerGridPlus.Patches
{
    // Two jobs around vanilla SwitchOnOff.RefreshColorState for every device that hosts a fault
    // flash (the FlashAttachPatches set):
    //
    //   1. While the parent device is in a fault lockout, the vanilla body is skipped (return
    //      false) so BrownoutFlashBehaviour's emissive material swap is not overwritten on the next
    //      on/off/error state transition (decompile L138462-138497: RefreshColorState does a full
    //      material swap).
    //
    //   2. OFF-as-reset (POWER.md §10.3, uniform across all four faults): toggling the device OFF
    //      during a fault clears its lockouts immediately so the player can retry without waiting
    //      out the 60-second timer. The next allocator / detector pass re-decides; if the condition
    //      still holds, the lockout re-fires instantly. Returning true lets vanilla apply the
    //      off-state material so the button stops flashing.
    [HarmonyPatch(typeof(VanillaSwitchOnOff))]
    public static class SwitchOnOffShedPatches
    {
        [HarmonyPrefix, HarmonyPatch("RefreshColorState")]
        public static bool RefreshColorState_Prefix(VanillaSwitchOnOff __instance)
        {
            var parentField = AccessTools.Field(typeof(VanillaDevicePart), "parentThing");
            var parent = parentField?.GetValue(__instance) as Thing;
            if (!(parent is Assets.Scripts.Objects.Pipes.Device device)) return true;

            long faultRefId = FaultHover.ResolveFaultRefId(device);
            int tick = ElectricityTickCounter.CurrentTick;
            var fault = FaultHover.ActiveFault(faultRefId, tick);
            if (fault == FaultHover.Kind.None) return true;

            if (!device.OnOff)
            {
                // OFF-as-reset: clear every lockout on this device (both host dicts and client
                // mirrors; the per-tick snapshot sync propagates the cleared state to other peers).
                BrownoutRegistry.ClearLockout(faultRefId);
                OverloadRegistry.ClearLockout(faultRefId);
                CycleFaultRegistry.ClearLockout(faultRefId);
                VariableVoltageFaultRegistry.ClearLockout(faultRefId);
                return true;   // vanilla applies the off-state material
            }

            // Fault active and device ON: skip vanilla so the flash material survives transitions.
            return false;
        }
    }
}
