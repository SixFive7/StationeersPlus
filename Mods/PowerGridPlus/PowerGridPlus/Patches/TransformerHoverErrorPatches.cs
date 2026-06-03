using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    // Surfaces the brownout / shed state on the transformer's hover (passive
    // tooltip). Mirrors BurnReasonPatches: Thing.GetPassiveTooltip is virtual;
    // Transformer does not override it, so we postfix the base method and filter
    // by `is Transformer`.
    //
    // The tooltip Extended field is the standard pattern (vanilla devices that
    // surface error overlays do it this way, e.g. AirConditioner with
    // errorGameString). We append a colored line when the transformer is
    // currently shed.
    public static class TransformerHoverErrorPatches
    {
        [HarmonyPostfix, HarmonyPatch(typeof(Thing), nameof(Thing.GetPassiveTooltip))]
        public static void Thing_GetPassiveTooltip_Postfix(Thing __instance, ref PassiveTooltip __result)
        {
            if (!(__instance is Transformer transformer))
                return;
            if (!ShedSettingsSync.Effective)
                return;
            if (!BrownoutRegistry.IsShedding(transformer.ReferenceId, ElectricityTickCounter.CurrentTick))
                return;

            int p = PriorityStore.GetPriority(transformer.ReferenceId);
            var msg = $"<color=#ffa500>Shedding (Priority {p}):</color> insufficient upstream supply on input network. " +
                      $"Lower-priority transformers shed first; the lockout auto-clears in up to 10 seconds.";
            var prefix = string.IsNullOrEmpty(__result.Extended) ? string.Empty : (__result.Extended + "\n");
            __result.Extended = prefix + msg;
        }
    }
}
