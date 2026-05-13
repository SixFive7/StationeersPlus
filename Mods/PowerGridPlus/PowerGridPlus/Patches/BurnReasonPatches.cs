using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using UnityEngine;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Hooks the <see cref="CableRuptured"/> registration so a pending burn-reason (recorded by the
    ///     caller in <see cref="BurnReasonRegistry"/> just before invoking <see cref="Cable.Break"/>) gets
    ///     attached to the freshly-spawned wreckage; and patches the wreckage's hover tooltip to show it.
    /// </summary>
    public static class BurnReasonPatches
    {
        // CableRuptured.OnRegistered runs synchronously inside Cable.Break -> Constructor.SpawnConstruct ->
        // Thing.Create. The wreckage's LocalGrid is set by base.OnRegistered before this postfix fires.
        [HarmonyPostfix, HarmonyPatch(typeof(CableRuptured), nameof(CableRuptured.OnRegistered))]
        public static void CableRuptured_OnRegistered_Postfix(CableRuptured __instance)
        {
            if (__instance == null) return;
            if (BurnReasonRegistry.TryConsumePending(__instance.LocalGrid, out var reason))
                BurnReasonRegistry.Attach(__instance, reason);
        }

        // CableRuptured does not override GetPassiveTooltip; patching Thing.GetPassiveTooltip and filtering
        // by type is the cleanest way to extend the wreckage's hover text without affecting other things.
        [HarmonyPostfix, HarmonyPatch(typeof(Thing), nameof(Thing.GetPassiveTooltip))]
        public static void Thing_GetPassiveTooltip_Postfix(Thing __instance, ref PassiveTooltip __result)
        {
            if (!(__instance is CableRuptured))
                return;
            var reason = BurnReasonRegistry.GetAttached(__instance);
            if (string.IsNullOrEmpty(reason))
                return;
            var prefix = string.IsNullOrEmpty(__result.Extended) ? string.Empty : (__result.Extended + "\n");
            __result.Extended = prefix + "<color=#ffa500>Burned:</color> " + reason;
        }
    }
}
