using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using HarmonyLib;
using JetBrains.Annotations;
using LaunchPadBooster.Networking;
using System.Linq;
using System.Reflection;
using UnityEngine;

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

    // Ctrl+click cycling moved to ClickCyclePatch (patches
    // InventoryManager.HandlePrimaryUse, which actually fires on click for
    // these items -- Item.OnUsePrimary is gated behind AllowSelfUse=false
    // for both SensorLenses and AdvancedTablet, so patching there was a
    // dead hook).

    // --- Save/Load: custom save data for active sensor persistence ---
    //
    // SensorLenses inherits SerializeSave/DeserializeSave from Thing (doesn't override).
    // Using [HarmonyPatch(typeof(SensorLenses), nameof(...))] fails because Harmony's
    // AccessTools.DeclaredMethod only looks at methods declared on the exact type.
    // The TargetMethod() pattern resolves the inherited MethodInfo via Type.GetMethod,
    // which walks inheritance. __instance is typed as SensorLenses so Harmony skips
    // the Postfix for non-SensorLenses Things.

    [HarmonyPatch]
    public class SensorLensesSerializeSavePatch
    {
        [UsedImplicitly]
        static MethodBase TargetMethod() =>
            typeof(SensorLenses).GetMethod("SerializeSave",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        // Declared as Thing (see note in SensorLensesSyncPatches.cs): Harmony patches
        // the base Thing.SerializeSave and this Postfix fires for every Thing in the
        // save. We filter here and only upgrade the save data for SensorLenses.
        [UsedImplicitly]
        public static void Postfix(Thing __instance, ref ThingSaveData __result)
        {
            if (!(__instance is SensorLenses lenses)) return;
            if (__result == null) return;
            if (__result is SensorLensesSaveData) return; // already upgraded

            // Vanilla returns a DynamicThingSaveData. We need a SensorLensesSaveData
            // so the active chip reference id persists. Copy all inherited fields
            // by reflection, then set our extra field.
            var upgraded = new SensorLensesSaveData();
            CopyFields(__result, upgraded);
            upgraded.ActiveSensorReferenceId = lenses.Sensor?.ReferenceId ?? 0L;
            __result = upgraded;
        }

        private static void CopyFields(object src, object dst)
        {
            var t = src.GetType();
            while (t != null && t != typeof(object))
            {
                foreach (var fi in t.GetFields(
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (!fi.IsInitOnly)
                        fi.SetValue(dst, fi.GetValue(src));
                }
                t = t.BaseType;
            }
        }
    }

    [HarmonyPatch]
    public class SensorLensesSaveLoadPatch
    {
        [UsedImplicitly]
        static MethodBase TargetMethod() =>
            typeof(SensorLenses).GetMethod("DeserializeSave",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(ThingSaveData) }, null);

        // Declared as Thing. See note in SensorLensesSyncPatches.cs.
        // Without this, every non-SensorLenses Thing in the save throws
        // InvalidCastException and the whole load aborts.
        [UsedImplicitly]
        public static void Postfix(Thing __instance, ThingSaveData saveData)
        {
            if (!(__instance is SensorLenses lenses)) return;

            // Stash the saved active-chip id for ActiveSlotPersistence to
            // apply in OnFinishedLoad. Setting lenses.Sensor here is
            // pointless because vanilla's OnChildEnterInventory fires for
            // each chip as it's restored and reassigns Sensor to whatever
            // chip entered last. We have to wait until all chips are in.
            if (saveData is SensorLensesSaveData sd)
                ActiveSlotPersistence.PendingActiveSensor[lenses.ReferenceId] =
                    sd.ActiveSensorReferenceId;

            DynamicSlots.RefreshSlots(lenses, Slot.Class.SensorProcessingUnit);
        }
    }
}
