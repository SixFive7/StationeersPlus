using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    // Wires the writable LogicPassthroughMode slot onto Transformer. Reading
    // returns the current per-device mode (0 or 1, falling through to the
    // PrefabName default when no override has ever been set). Writing flips the
    // mode and dirties both sides' data device lists so the next data-list
    // refresh picks up the change.
    [HarmonyPatch(typeof(Transformer))]
    public static class TransformerPassthroughLogicPatches
    {
        [HarmonyPrefix, HarmonyPatch(nameof(Transformer.CanLogicRead))]
        public static bool CanLogicReadPatch(LogicType logicType, ref bool __result)
        {
            if (logicType == LogicTypeRegistry.LogicPassthroughMode)
            {
                __result = true;
                return false;
            }
            return true;
        }

        [HarmonyPrefix, HarmonyPatch(nameof(Transformer.CanLogicWrite))]
        public static bool CanLogicWritePatch(LogicType logicType, ref bool __result)
        {
            if (logicType == LogicTypeRegistry.LogicPassthroughMode)
            {
                __result = true;
                return false;
            }
            return true;
        }

        [HarmonyPrefix, HarmonyPatch(nameof(Transformer.GetLogicValue))]
        public static bool GetLogicValuePatch(Transformer __instance, LogicType logicType, ref double __result)
        {
            if (logicType == LogicTypeRegistry.LogicPassthroughMode)
            {
                __result = PassthroughModeStore.GetMode(__instance);
                return false;
            }
            return true;
        }

        [HarmonyPrefix, HarmonyPatch(nameof(Transformer.SetLogicValue))]
        public static bool SetLogicValuePatch(Transformer __instance, LogicType logicType, double value)
        {
            if (logicType == LogicTypeRegistry.LogicPassthroughMode)
            {
                int newMode = value > 0.5 ? 1 : 0;
                int oldMode = PassthroughModeStore.GetMode(__instance);
                PassthroughModeStore.SetMode(__instance, newMode);
                if (oldMode != newMode)
                {
                    __instance.InputNetwork?.DirtyDataDeviceList();
                    __instance.OutputNetwork?.DirtyDataDeviceList();
                }
                return false;
            }
            return true;
        }
    }
}
