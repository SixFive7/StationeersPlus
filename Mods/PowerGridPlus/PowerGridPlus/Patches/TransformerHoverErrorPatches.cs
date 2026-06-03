using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    // Surfaces the brownout / shed state on the transformer's hover (passive
    // tooltip).
    //
    // Virtual-dispatch trap (HarmonyLogicableInheritedMethodTrap): patching
    // Thing.GetPassiveTooltip is INCORRECT because ElectricalInputOutput
    // overrides GetPassiveTooltip (verified via reflection on Luna save:
    // typeof(Transformer).GetMethod("GetPassiveTooltip").DeclaringType =
    // Assets.Scripts.Objects.Electrical.ElectricalInputOutput). A
    // Transformer (subclass of ElectricalInputOutput) call resolves to the
    // EIO override which never passes through the Thing slot, so a postfix
    // on Thing.GetPassiveTooltip never fires for transformer hovers.
    //
    // Fix: patch ElectricalInputOutput.GetPassiveTooltip directly. The
    // `is Transformer` filter inside the postfix keeps non-Transformer EIOs
    // (e.g. an APC or wireless dish that also inherits EIO) unaffected.
    //
    // The tooltip Extended field is the standard pattern (vanilla devices that
    // surface error overlays do it this way, e.g. AirConditioner with
    // errorGameString). We append a colored line when the transformer is
    // currently shed.
    [HarmonyPatch(typeof(ElectricalInputOutput), nameof(Thing.GetPassiveTooltip))]
    public static class TransformerHoverErrorPatches
    {
        [HarmonyPostfix]
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
