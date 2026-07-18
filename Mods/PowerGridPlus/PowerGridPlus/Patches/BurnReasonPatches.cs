using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using LaunchPadBooster.Networking;
using UnityEngine;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Hooks the <see cref="CableRuptured"/> registration so a pending burn-reason (recorded by the
    ///     caller in <see cref="BurnReasonRegistry"/> just before invoking <see cref="Cable.Break"/>) gets
    ///     attached to the freshly-spawned wreckage and replicated to clients; and patches the wreckage's
    ///     hover tooltip to show it (with a grey legacy fallback when no store has a reason).
    /// </summary>
    // Class-level [HarmonyPatch] is REQUIRED for PatchAll to process the per-method patches below:
    // this class targets three different methods, so it cannot put a single target on the class; the
    // bare marker tells PatchAll to apply each method's own [HarmonyPatch(target)]. Without it the whole
    // class is silently skipped and none of the three postfixes attach (the same trap that hid
    // FaultButtonTooltipPatches). Confirmed via ScenarioRunner: GetPatchInfo(CableRuptured.OnRegistered)
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
            if (!BurnReasonRegistry.TryConsumePending(__instance.LocalGrid, out var reason))
                return;
            BurnReasonRegistry.Attach(__instance, reason);
            // Replicate to clients: every burn writer sits behind the RunSimulation gate, so a
            // connected client never records a reason of its own; without this its hover shows the
            // legacy fallback for wreckage that burned mid-session. SendAll no-ops with no clients.
            if (GameManager.RunSimulation)
                new BurnReasonSyncMessage { WreckageRefId = __instance.ReferenceId, Reason = reason }.SendAll(0L);
        }

        // Single source for the hover line: a recorded reason renders as before; wreckage with no
        // entry in any store (burned before this mod was installed, or records lost) gets a calm
        // grey fallback instead of nothing, so the line never silently vanishes. "Burned:" stays
        // orange in both for consistency.
        internal static string BuildBurnLine(Thing wreckage)
        {
            if (BurnReasonRegistry.TryGetReason(wreckage, out var reason))
                return "<color=#ffa500>Burned:</color> " + reason;
            return "<color=#ffa500>Burned:</color> <color=#9aa0a6>cause predates this mod's records</color>";
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
            var prefix = string.IsNullOrEmpty(__result.Extended) ? string.Empty : (__result.Extended + "\n");
            __result.Extended = prefix + BuildBurnLine(__instance);
            // No-ALT visibility: the crosshair HUD path (InventoryManager.NormalModeThing, decompile
            // 287864+) only displays a body tooltip whose Title is non-empty (the empty-Title gate,
            // Research/Patterns/PassiveTooltipPipelines.md); wreckage falls through the vanilla chain
            // with the all-empty struct, so the burn line used to render under ALT only. Fill the
            // empty Title with the wreckage's DisplayName, the same move FaultHoverPatches makes for
            // lockout faults. A Title a vanilla override already set is respected.
            if (string.IsNullOrEmpty(__result.Title))
                __result.Title = __instance.DisplayName;
        }

        // Secondary re-apply (POWERTODO 0.2): when the cursor thing exposes ANY interactable
        // affordance (deconstruct / wrench / pickup), the HUD path calls
        // Tooltip.SetValuesForInteractable, which REPLACES the whole PassiveTooltip struct
        // (decompile 254408) and erases the Extended text injected above. This postfix re-applies
        // the burn line after the clobber. Harmless no-op when the wreckage exposes no
        // interactable (the method is simply not called for it).
        [HarmonyPostfix, HarmonyPatch(typeof(Assets.Scripts.UI.Tooltip), "SetValuesForInteractable")]
        public static void SetValuesForInteractable_Postfix(ref PassiveTooltip tooltip, Thing CursorThing)
        {
            if (!(CursorThing is CableRuptured))
                return;
            var prefix = string.IsNullOrEmpty(tooltip.Extended) ? string.Empty : (tooltip.Extended + "\n");
            tooltip.Extended = prefix + BuildBurnLine(CursorThing);
        }
    }
}
