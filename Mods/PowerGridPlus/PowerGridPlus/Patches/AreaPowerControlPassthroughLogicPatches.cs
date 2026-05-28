using Assets.Scripts.Networking;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using HarmonyLib;
using LaunchPadBooster.Networking;

namespace PowerGridPlus.Patches
{
    // Wires the writable LogicPassthroughMode slot onto AreaPowerControl. Reading
    // returns the current per-device mode (0 or 1, defaulting to 1 via
    // PassthroughModeStore.GetDefaultMode when no override has been set). Writing
    // flips the mode and dirties both sides' data device lists so the next data-list
    // refresh picks up the change. Mirrors TransformerPassthroughLogicPatches.
    [HarmonyPatch(typeof(AreaPowerControl))]
    public static class AreaPowerControlPassthroughLogicPatches
    {
        [HarmonyPrefix, HarmonyPatch(nameof(AreaPowerControl.CanLogicRead))]
        public static bool CanLogicReadPatch(LogicType logicType, ref bool __result)
        {
            if (logicType == LogicTypeRegistry.LogicPassthroughMode)
            {
                __result = true;
                return false;
            }
            return true;
        }

        [HarmonyPrefix, HarmonyPatch(nameof(AreaPowerControl.CanLogicWrite))]
        public static bool CanLogicWritePatch(LogicType logicType, ref bool __result)
        {
            if (logicType == LogicTypeRegistry.LogicPassthroughMode)
            {
                __result = true;
                return false;
            }
            return true;
        }

        [HarmonyPrefix, HarmonyPatch(nameof(AreaPowerControl.GetLogicValue))]
        public static bool GetLogicValuePatch(AreaPowerControl __instance, LogicType logicType, ref double __result)
        {
            if (logicType == LogicTypeRegistry.LogicPassthroughMode)
            {
                __result = PassthroughModeStore.GetMode((Assets.Scripts.Objects.Thing)__instance);
                return false;
            }
            return true;
        }

        [HarmonyPrefix, HarmonyPatch(nameof(AreaPowerControl.SetLogicValue))]
        public static bool SetLogicValuePatch(AreaPowerControl __instance, LogicType logicType, double value)
        {
            if (logicType == LogicTypeRegistry.LogicPassthroughMode)
            {
                // Defense-in-depth. Same reasoning as TransformerPassthroughLogicPatches /
                // InheritedLogicablePassthroughLogicPatches: vanilla SetLogicValue is server-side,
                // but a future modded writer firing on a client would desync the store, so we drop
                // client-side writes explicitly.
                if (!NetworkManager.IsServer) return false;

                int newMode = value > 0.5 ? 1 : 0;
                int oldMode = PassthroughModeStore.GetMode((Assets.Scripts.Objects.Thing)__instance);
                PassthroughModeStore.SetMode((Assets.Scripts.Objects.Thing)__instance, newMode);
                if (oldMode != newMode)
                {
                    PassthroughTopology.DirtyBridgeNetworks(__instance);
                    CableNetworkPatches.ScheduleCascadeForDevice(__instance);
                    new PassthroughModeMessage { DeviceId = __instance.ReferenceId, Mode = newMode }.SendAll(0L);
                }
                return false;
            }
            return true;
        }
    }
}
