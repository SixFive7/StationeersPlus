using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using HarmonyLib;
using JetBrains.Annotations;
using System.Reflection;
using UnityEngine;

// AdvancedTablet save/load handling below mirrors SensorLenses'. See
// SensorLensesPatches.cs for the cross-cutting design notes.

namespace EquipmentPlus
{
    // --- Dynamic slot unlock/lock ---
    //
    // AdvancedTablet doesn't declare OnChildEnter/ExitInventory (inherited from Thing),
    // so [HarmonyPatch(typeof, nameof)] fails with AccessTools.DeclaredMethod. Use
    // TargetMethod() to resolve via Type.GetMethod which walks inheritance.

    [HarmonyPatch]
    public class TabletSlotOnInsert
    {
        [UsedImplicitly]
        static MethodBase TargetMethod() =>
            typeof(AdvancedTablet).GetMethod("OnChildEnterInventory",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        // Declared as Thing. Harmony patches the base method; this fires for
        // every Thing's OnChildEnterInventory. Declaring AdvancedTablet would
        // force a castclass on each call and crash on any non-AdvancedTablet.
        [UsedImplicitly]
        public static void Postfix(Thing __instance)
        {
            if (!(__instance is AdvancedTablet tablet)) return;
            DynamicSlots.RefreshSlots(tablet, Slot.Class.Cartridge);
        }
    }

    [HarmonyPatch]
    public class TabletSlotOnRemove
    {
        [UsedImplicitly]
        static MethodBase TargetMethod() =>
            typeof(AdvancedTablet).GetMethod("OnChildExitInventory",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        [UsedImplicitly]
        public static void Postfix(Thing __instance)
        {
            if (!(__instance is AdvancedTablet tablet)) return;
            DynamicSlots.RefreshSlots(tablet, Slot.Class.Cartridge);
        }
    }

    // Cartridge cycling now lives in ScrollDispatchPatches (Ctrl+scroll).
    // The previous Ctrl+click cycle (ClickCyclePatch) was removed in
    // TODO B Phase 2 cleanup; the scroll bindings supersede it.

    // --- Save/Load: dynamic slot rebuild after deserialize ---
    //
    // Active-cartridge persistence moved to ActiveSlotSideCar (side-car file
    // in the save ZIP, removal-safe). World.xml stays vanilla. The
    // PendingActiveCartridge dict is now populated by the LoadWorld postfix
    // from the side-car; ActiveSlotPersistence's OnFinishedLoad postfix
    // consumes it as before.

    [HarmonyPatch(typeof(AdvancedTablet), nameof(AdvancedTablet.DeserializeSave))]
    public class TabletDeserializeSavePatch
    {
        [UsedImplicitly]
        public static void Postfix(AdvancedTablet __instance)
        {
            DynamicSlots.RefreshSlots(__instance, Slot.Class.Cartridge);
        }
    }
}
