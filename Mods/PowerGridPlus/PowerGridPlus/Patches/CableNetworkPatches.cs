// Derived from Re-Volt by Sukasa (https://github.com/sukasa/revolt), MIT License (Copyright (c) 2025 Sukasa).

using System;
using System.Reflection;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using PowerGridPlus.Power;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Swaps every <see cref="CableNetwork"/>'s <see cref="PowerTick"/> for a <see cref="PowerGridTick"/>
    ///     at construction time, and forwards device-list invalidations so the tick rebuilds its caches.
    /// </summary>
    [HarmonyPatch(typeof(CableNetwork))]
    public static class CableNetworkPatches
    {
        private static readonly FieldInfo TickField = typeof(CableNetwork).GetField(nameof(CableNetwork.PowerTick));

        [HarmonyPostfix, HarmonyPatch(nameof(CableNetwork.DirtyPowerAndDataDeviceLists))]
        public static void DirtyPowerAndDataDeviceListsPatch(CableNetwork __instance)
        {
            if (__instance.PowerTick is PowerGridTick tick)
                tick.IsDirty = true;
        }

        [HarmonyPostfix, HarmonyPatch(MethodType.Constructor, new Type[0])]
        public static void Constructor_None(CableNetwork __instance) => Inject(__instance);

        [HarmonyPostfix, HarmonyPatch(MethodType.Constructor, new Type[] { typeof(Cable) })]
        public static void Constructor_Cable(CableNetwork __instance) => Inject(__instance);

        [HarmonyPostfix, HarmonyPatch(MethodType.Constructor, new Type[] { typeof(long) })]
        public static void Constructor_Long(CableNetwork __instance) => Inject(__instance);

        private static void Inject(CableNetwork network) => TickField.SetValue(network, new PowerGridTick());
    }
}
