using Assets.Scripts.Inventory;
using Assets.Scripts.Objects;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    // Surfaces the active fault on the device's ON/OFF BUTTON hover text (the body hover is
    // covered by FaultHoverPatches). The player aims at the flashing button when investigating,
    // so the explanation lands where the cursor already is: the contextual line becomes e.g.
    // "Turn Off (Overloaded: Downstream demand exceeds this transformer's limit! 42.17s)".
    //
    // Vanilla path (decompile L300626): `Thing.GetContextualName(Interactable)` is virtual; the
    // base body switches on `interactable.Action`. None of the power device classes override it,
    // so one postfix on the Thing base reaches every call (verified at runtime by
    // pgp-priority-shedding-hover-probe in earlier sessions).
    //
    // The line and the precedence rule (CYCLE > VVF > OVERLOAD > SHED, one line only) come from
    // FaultHover -- the same single source of truth the body hover and the flash colour use.
    [HarmonyPatch(typeof(Thing), nameof(Thing.GetContextualName))]
    public static class TransformerHoverErrorPatches
    {
        [HarmonyPostfix]
        public static void GetContextualName_Postfix(Thing __instance, Interactable interactable, ref string __result)
        {
            if (interactable == null || __instance == null) return;
            if (interactable.Action != InteractableType.OnOff) return;
            long refId = FaultHover.ResolveFaultRefId(__instance);
            // Active fault line, then (for a throttled transformer, §5.3 / P13) the throttle info line.
            if (FaultHover.TryGetLine(refId, ElectricityTickCounter.CurrentTick, __instance, out var line, out _))
                __result = (__result ?? string.Empty) + " " + line;
            if (TransformerThrottleHover.TryGetLine(__instance, out var throttleLine))
                __result = (__result ?? string.Empty) + " " + throttleLine;
        }
    }
}
