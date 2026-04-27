using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using HarmonyLib;
using JetBrains.Annotations;
using System.Reflection;

namespace EquipmentPlus
{
    // --- Dynamic slot unlock/lock ---

    [HarmonyPatch(typeof(SensorLenses), nameof(SensorLenses.OnChildEnterInventory))]
    public class SensorLensesSlotOnInsert
    {
        [UsedImplicitly]
        public static void Postfix(SensorLenses __instance)
        {
            DynamicSlots.RefreshSlots(__instance, Slot.Class.SensorProcessingUnit);
        }
    }

    [HarmonyPatch(typeof(SensorLenses), nameof(SensorLenses.OnChildExitInventory))]
    public class SensorLensesSlotOnRemove
    {
        [UsedImplicitly]
        public static void Postfix(SensorLenses __instance)
        {
            DynamicSlots.RefreshSlots(__instance, Slot.Class.SensorProcessingUnit);
        }
    }

    // Worn-lens chip cycling now lives in ScrollDispatchPatches (LeftShift+scroll).
    // The previous Ctrl+click cycle (ClickCyclePatch on
    // InventoryManager.HandlePrimaryUse) was removed in TODO B Phase 2 cleanup.

    // --- Save/Load: dynamic slot rebuild after deserialize ---
    //
    // Active-chip persistence moved to ActiveSlotSideCar (side-car file in the
    // save ZIP, removal-safe). World.xml stays vanilla: an absent mod no longer
    // breaks a load. ActiveSlotPersistence's OnFinishedLoad postfix consumes
    // PendingActiveSensor, now populated by the LoadWorld postfix from the
    // side-car instead of from a SaveData subclass.
    //
    // SensorLenses inherits DeserializeSave from Thing; TargetMethod walks
    // inheritance via Type.GetMethod where AccessTools.DeclaredMethod would not.

    [HarmonyPatch]
    public class SensorLensesDeserializeSavePatch
    {
        [UsedImplicitly]
        static MethodBase TargetMethod() =>
            typeof(SensorLenses).GetMethod("DeserializeSave",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(ThingSaveData) }, null);

        [UsedImplicitly]
        public static void Postfix(Thing __instance)
        {
            if (!(__instance is SensorLenses lenses)) return;
            DynamicSlots.RefreshSlots(lenses, Slot.Class.SensorProcessingUnit);
        }
    }
}
