// Derived from Re-Volt by Sukasa (https://github.com/sukasa/revolt), MIT License (Copyright (c) 2025 Sukasa).

using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Exposes a transformer's current throughput as the Power Actual logic value.
    /// </summary>
    [HarmonyPatch(typeof(Transformer))]
    public static class TransformerLogicPatches
    {
        [HarmonyPrefix, HarmonyPatch(nameof(Transformer.CanLogicRead))]
        public static bool CanLogicReadPatch(LogicType logicType, ref bool __result)
        {
            if (!Settings.EnableTransformerLogicAdditions.Value)
                return true;

            if (logicType == LogicType.PowerActual)
            {
                __result = true;
                return false;
            }

            return true;
        }

        [HarmonyPrefix, HarmonyPatch(nameof(Transformer.GetLogicValue))]
        public static bool GetLogicValuePatch(LogicType logicType, ref double __result, float ____powerProvided)
        {
            if (!Settings.EnableTransformerLogicAdditions.Value)
                return true;

            if (logicType == LogicType.PowerActual)
            {
                __result = ____powerProvided;
                return false;
            }

            return true;
        }
    }
}
