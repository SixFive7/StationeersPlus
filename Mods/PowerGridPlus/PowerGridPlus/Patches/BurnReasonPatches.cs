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
    // Class-level [HarmonyPatch] is REQUIRED for PatchAll to process the per-method patches below:
    // this class targets three different methods, so it cannot put a single target on the class; the
    // bare marker tells PatchAll to apply each method's own [HarmonyPatch(target)]. Without it the whole
    // class is silently skipped and none of the three postfixes attach (the same trap that hid
    // TransformerHoverErrorPatches). Confirmed via ScenarioRunner: GetPatchInfo(CableRuptured.OnRegistered)
    // had no PGP postfix until this attribute was added.
    [HarmonyPatch]
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

        // Virtual-dispatch trap (POWERTODO 0.2): Thing.GetPassiveTooltip is virtual and Structure overrides
        // it. CableRuptured : SmallGrid : Structure, so MouseManager.Idle's virtual call routes to
        // Structure.GetPassiveTooltip and a postfix on Thing.GetPassiveTooltip NEVER fires. Patch the
        // Structure override instead. The `is CableRuptured` filter still gates correctly; only the method
        // Harmony attaches to changes. (CableRuptured does not declare its own override, and SmallGrid does
        // not either, so Structure is the override-bearing ancestor that runs at runtime.)
        [HarmonyPostfix, HarmonyPatch(typeof(Structure), nameof(Structure.GetPassiveTooltip))]
        public static void Structure_GetPassiveTooltip_Postfix(Thing __instance, ref PassiveTooltip __result)
        {
            if (!(__instance is CableRuptured))
                return;
            var reason = BurnReasonRegistry.GetAttached(__instance);
            if (string.IsNullOrEmpty(reason))
                return;
            var prefix = string.IsNullOrEmpty(__result.Extended) ? string.Empty : (__result.Extended + "\n");
            __result.Extended = prefix + "<color=#ffa500>Burned:</color> " + reason;
        }

        // Secondary re-apply (POWERTODO 0.2): when the cursor thing exposes ANY interactable
        // affordance (deconstruct / wrench / pickup), MouseManager.Idle calls
        // Tooltip.SetValuesForInteractable, which REPLACES the whole PassiveTooltip struct
        // (decompile L237486 / L288646) and erases the Extended text injected above. This postfix
        // re-applies the burn line after the clobber. Harmless no-op when the wreckage exposes no
        // interactable (the method is simply not called for it).
        [HarmonyPostfix, HarmonyPatch(typeof(Assets.Scripts.UI.Tooltip), "SetValuesForInteractable")]
        public static void SetValuesForInteractable_Postfix(ref PassiveTooltip tooltip, Thing CursorThing)
        {
            if (!(CursorThing is CableRuptured))
                return;
            var reason = BurnReasonRegistry.GetAttached(CursorThing);
            if (string.IsNullOrEmpty(reason))
                return;
            var prefix = string.IsNullOrEmpty(tooltip.Extended) ? string.Empty : (tooltip.Extended + "\n");
            tooltip.Extended = prefix + "<color=#ffa500>Burned:</color> " + reason;
        }
    }
}
