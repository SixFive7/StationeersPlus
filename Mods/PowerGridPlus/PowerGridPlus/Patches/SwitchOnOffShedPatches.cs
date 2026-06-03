using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
// SwitchOnOff and DevicePart live in the bare `Objects` namespace (decompile
// L137865 + L138371 + L138394), not `Assets.Scripts.Objects`. Stationeers uses
// both namespaces for different classes.
using VanillaSwitchOnOff = global::Objects.SwitchOnOff;
using VanillaDevicePart = global::Objects.DevicePart;

namespace PowerGridPlus.Patches
{
    // Suppresses vanilla SwitchOnOff.RefreshColorState while a Transformer is
    // shedding so that BrownoutFlashBehaviour's orange material swap is not
    // overwritten on the next on/off/error state transition.
    //
    // Decompile L138462-138497: RefreshColorState picks a SwitchColorState
    // (Off / On / OnPowered / Error) based on the parent Thing's OnOff,
    // Powered, HasPowerState, and Error flags, then does
    // `switchRenderer.material = off | on | onPowered` (full material swap)
    // on the FIRST tick the state changes. The vanilla materials have a baked
    // `_EmissionMap` for the on/onPowered cases, so per-property emission
    // writes against those materials cannot override the green glow.
    //
    // With this prefix, while the parent Transformer is shedding the body of
    // RefreshColorState is skipped entirely (return false). The companion
    // BrownoutFlashBehaviour swaps the renderer's material to a runtime
    // orange-emissive instance for the duration of the shed and restores the
    // vanilla material on shed exit.
    [HarmonyPatch(typeof(VanillaSwitchOnOff))]
    public static class SwitchOnOffShedPatches
    {
        [HarmonyPrefix, HarmonyPatch("RefreshColorState")]
        public static bool RefreshColorState_Prefix(VanillaSwitchOnOff __instance)
        {
            if (!ShedSettingsSync.Effective) return true;
            var parentField = AccessTools.Field(typeof(VanillaDevicePart), "parentThing");
            var parent = parentField?.GetValue(__instance) as Thing;
            if (!(parent is Transformer transformer)) return true;
            if (!BrownoutRegistry.IsShedding(transformer.ReferenceId, ElectricityTickCounter.CurrentTick))
                return true;
            // Skip vanilla body so the orange material set by BrownoutFlashBehaviour
            // is not replaced when state would otherwise transition.
            return false;
        }
    }
}
