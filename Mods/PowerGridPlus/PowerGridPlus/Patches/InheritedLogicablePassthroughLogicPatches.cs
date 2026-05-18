using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    // Wires the writable LogicPassthroughMode slot onto Device subclasses that
    // INHERIT the four logic-port methods (CanLogicRead / CanLogicWrite / GetLogicValue /
    // SetLogicValue) rather than overriding them. Targets here: Battery, PowerTransmitter,
    // PowerReceiver -- all three rely on Device's base implementation.
    //
    // Why this targets Device instead of the individual subclasses: attribute-style
    // [HarmonyPatch(typeof(Battery), nameof(Battery.SetLogicValue))] throws
    // "Undefined target method" at PatchAll time when Battery does not declare
    // SetLogicValue itself, which bails the whole Harmony batch and silently disables
    // every patch processed after it (including DeviceInitializePatch, the bridge
    // postfix, recipe cost re-apply, IC10 constants). See
    // Research/Patterns/HarmonyDeviceInheritedMethodTrap.md.
    //
    // Transformer is handled separately in TransformerPassthroughLogicPatches.cs because
    // Transformer overrides the four methods directly -- our base-class patch here would
    // be shadowed by Transformer's override at runtime, so the Transformer-specific patch
    // is still required for the Transformer case.
    [HarmonyPatch(typeof(Device))]
    public static class InheritedDevicePassthroughLogicPatches
    {
        private static bool IsBridge(Device l) =>
            l is Battery || l is PowerTransmitter || l is PowerReceiver;

        [HarmonyPrefix, HarmonyPatch(nameof(Device.CanLogicRead), new[] { typeof(LogicType) })]
        public static bool CanLogicReadPatch(Device __instance, LogicType logicType, ref bool __result)
        {
            if (logicType != LogicTypeRegistry.LogicPassthroughMode) return true;
            if (!IsBridge(__instance)) return true;
            __result = true;
            return false;
        }

        [HarmonyPrefix, HarmonyPatch(nameof(Device.CanLogicWrite), new[] { typeof(LogicType) })]
        public static bool CanLogicWritePatch(Device __instance, LogicType logicType, ref bool __result)
        {
            if (logicType != LogicTypeRegistry.LogicPassthroughMode) return true;
            if (!IsBridge(__instance)) return true;
            __result = true;
            return false;
        }

        [HarmonyPrefix, HarmonyPatch(nameof(Device.GetLogicValue), new[] { typeof(LogicType) })]
        public static bool GetLogicValuePatch(Device __instance, LogicType logicType, ref double __result)
        {
            if (logicType != LogicTypeRegistry.LogicPassthroughMode) return true;
            if (!IsBridge(__instance)) return true;
            __result = PassthroughModeStore.GetMode((Thing)__instance);
            return false;
        }

        [HarmonyPrefix, HarmonyPatch(nameof(Device.SetLogicValue), new[] { typeof(LogicType), typeof(double) })]
        public static bool SetLogicValuePatch(Device __instance, LogicType logicType, double value)
        {
            if (logicType != LogicTypeRegistry.LogicPassthroughMode) return true;
            if (!IsBridge(__instance)) return true;

            int newMode = value > 0.5 ? 1 : 0;
            int oldMode = PassthroughModeStore.GetMode((Thing)__instance);
            PassthroughModeStore.SetMode((Thing)__instance, newMode);
            if (oldMode != newMode)
                DirtyBothSides(__instance);
            return false;
        }

        private static void DirtyBothSides(Device l)
        {
            switch (l)
            {
                case Battery battery:
                    battery.InputNetwork?.DirtyPowerAndDataDeviceLists();
                    battery.OutputNetwork?.DirtyPowerAndDataDeviceLists();
                    break;
                case PowerTransmitter tx:
                    tx.InputNetwork?.DirtyPowerAndDataDeviceLists();
                    tx.LinkedReceiver?.OutputNetwork?.DirtyPowerAndDataDeviceLists();
                    break;
                case PowerReceiver rx:
                    rx.OutputNetwork?.DirtyPowerAndDataDeviceLists();
                    rx.LinkedPowerTransmitter?.InputNetwork?.DirtyPowerAndDataDeviceLists();
                    break;
            }
        }
    }
}
