using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    // Attaches a BrownoutFlashBehaviour to every Transformer instance so the
    // on/off button visual flashes orange while the transformer is shed.
    //
    // Hook point: Thing.OnRegistered (called for every Thing as it joins the
    // world, both fresh placements and save / join loads). Transformer does not
    // override OnRegistered, so the base Thing version fires first; we postfix
    // and attach if a transformer is in scope.
    //
    // Idempotent: we check for an existing component on the same GameObject to
    // avoid double-attach if OnRegistered fires more than once for the same Thing
    // (vanilla does not re-register an alive Thing, but defensive).
    [HarmonyPatch(typeof(Assets.Scripts.Objects.Thing), nameof(Assets.Scripts.Objects.Thing.OnRegistered))]
    public static class TransformerFlashAttachPatches
    {
        public static void Postfix(Assets.Scripts.Objects.Thing __instance, Cell cell)
        {
            if (!(__instance is Transformer transformer)) return;
            if (transformer.gameObject == null) return;
            var existing = transformer.GetComponent<BrownoutFlashBehaviour>();
            if (existing != null) return;
            var beh = transformer.gameObject.AddComponent<BrownoutFlashBehaviour>();
            beh.Init(transformer);
        }
    }
}
