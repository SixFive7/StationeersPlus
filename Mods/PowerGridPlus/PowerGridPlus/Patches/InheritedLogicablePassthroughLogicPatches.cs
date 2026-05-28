using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using HarmonyLib;
using LaunchPadBooster.Networking;

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
    // every patch processed after it (including LogicableInitializePatch, the bridge
    // postfix, recipe cost re-apply, IC10 constants). See
    // Research/Patterns/HarmonyLogicableInheritedMethodTrap.md.
    //
    // Transformer is handled separately in TransformerPassthroughLogicPatches.cs because
    // Transformer overrides the four methods directly -- our base-class patch here would
    // be shadowed by Transformer's override at runtime, so the Transformer-specific patch
    // is still required for the Transformer case.
    [HarmonyPatch(typeof(Device))]
    public static class InheritedLogicablePassthroughLogicPatches
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

            // Defense-in-depth. Vanilla SetLogicValue is server-side in practice -- IC10 ticks
            // on the host, tablet writes route through the host -- so this branch should never
            // run on a client. Belt-and-suspenders: if a future modded writer fires SetLogicValue
            // on a client, we drop the write rather than letting it desync the per-Thing
            // PassthroughModeStore from the host's authoritative value. The 0/false swallow path
            // matches the LogicType-handled exit below.
            if (!NetworkManager.IsServer) return false;

            int newMode = value > 0.5 ? 1 : 0;
            int oldMode = PassthroughModeStore.GetMode((Thing)__instance);
            PassthroughModeStore.SetMode((Thing)__instance, newMode);
            if (oldMode != newMode)
            {
                // Dirty both sides' lists, refresh their consumers (motherboard dropdowns / IC-housing /
                // sensor caches) across the passthrough component, and replicate the new mode to clients
                // so they refresh too. The unified dirty helper in PassthroughTopology covers Battery /
                // PowerTransmitter / PowerReceiver (and Transformer, used by the sibling patch).
                PassthroughTopology.DirtyBridgeNetworks(__instance);
                CableNetworkPatches.ScheduleCascadeForDevice(__instance);
                new PassthroughModeMessage { DeviceId = __instance.ReferenceId, Mode = newMode }.SendAll(0L);
            }
            return false;
        }
    }
}
