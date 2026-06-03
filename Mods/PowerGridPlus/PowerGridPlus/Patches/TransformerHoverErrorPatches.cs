using Assets.Scripts.Inventory;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    // Surfaces the brownout / shed state on the transformer's on/off button
    // hover text. The hover-on-body PassiveTooltip approach was reverted in
    // this session: the player doesn't aim at the transformer body (the box)
    // when investigating why a button is flashing -- they aim at the button
    // itself. Patching the contextual hover-line for that specific
    // Interactable puts the explanation where the cursor already is.
    //
    // Vanilla path (decompile L300626): `Thing.GetContextualName(Interactable)`
    // is virtual. Default body switches on `interactable.Action` and returns
    // strings like ActionStrings.TurnOff / TurnOn for `InteractableType.OnOff`.
    // We postfix the base method and append the shed reason when the target
    // Thing is a Transformer that's currently shed AND the queried interactable
    // is its OnOff button -- so the hover text becomes "Turn Off (Shedding,
    // Priority N: insufficient upstream supply, auto-clears in <=10 s)".
    //
    // Virtual-dispatch trap (HarmonyLogicableInheritedMethodTrap): the base
    // method on Thing isn't overridden on ElectricalInputOutput / Transformer,
    // so a postfix targeting `typeof(Thing).GetContextualName` reaches every
    // Transformer call correctly. (Verified by reflection at runtime; see
    // pgp-priority-shedding-hover-probe.)
    [HarmonyPatch(typeof(Thing), nameof(Thing.GetContextualName))]
    public static class TransformerHoverErrorPatches
    {
        // Diagnostic gate: when true, log once the first time we append shed
        // text to a transformer's on/off button hover so we can confirm the
        // postfix is reached. Toggle via reflection from a probe scenario.
        internal static bool DiagnosticEnabled = false;
        private static long _lastDiagnosticRef = 0;

        [HarmonyPostfix]
        public static void GetContextualName_Postfix(Thing __instance, Interactable interactable, ref string __result)
        {
            if (interactable == null) return;
            if (interactable.Action != InteractableType.OnOff) return;
            if (!(__instance is Transformer transformer)) return;
            if (!ShedSettingsSync.Effective) return;
            if (!BrownoutRegistry.IsShedding(transformer.ReferenceId, ElectricityTickCounter.CurrentTick))
                return;

            string suffix = " <color=#ffa500>(Shedding: insufficient upstream supply)</color>";
            __result = (__result ?? string.Empty) + suffix;

            if (DiagnosticEnabled && _lastDiagnosticRef != transformer.ReferenceId)
            {
                _lastDiagnosticRef = transformer.ReferenceId;
                Plugin.Log?.LogInfo($"[HOVER-DIAG] GetContextualName postfix fired ref={transformer.ReferenceId} prefab={transformer.PrefabName} result='{__result}'");
            }
        }
    }
}
