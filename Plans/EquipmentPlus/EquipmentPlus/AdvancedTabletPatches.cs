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

    // Ctrl+click cycling lives in ClickCyclePatch (see comment there).

    // --- Save/Load: custom save data for active cartridge persistence ---
    //
    // AdvancedTablet declares SerializeSave and DeserializeSave itself (not
    // inherited), so plain [HarmonyPatch(typeof, nameof)] works.

    [HarmonyPatch(typeof(AdvancedTablet), nameof(AdvancedTablet.SerializeSave))]
    public class AdvancedTabletSerializeSavePatch
    {
        [UsedImplicitly]
        public static void Postfix(AdvancedTablet __instance, ref ThingSaveData __result)
        {
            if (__result == null) return;
            if (__result is EquipmentPlusTabletSaveData) return; // already upgraded

            // Vanilla returns Assets.Scripts.Objects.Items.AdvancedTabletSaveData
            // (or a DynamicThingSaveData for plain tablets). Copy inherited
            // fields into our subclass, then record the currently-active
            // cartridge so we can restore it on load.
            var upgraded = new EquipmentPlusTabletSaveData();
            CopyFields(__result, upgraded);
            upgraded.ActiveCartridgeReferenceId =
                __instance.Cartridge != null ? __instance.Cartridge.ReferenceId : 0L;
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

    [HarmonyPatch(typeof(AdvancedTablet), nameof(AdvancedTablet.DeserializeSave))]
    public class TabletSaveLoadPatch
    {
        [UsedImplicitly]
        public static void Postfix(AdvancedTablet __instance, ThingSaveData savedData)
        {
            // Mode cannot be restored yet -- CartridgeSlots is rebuilt by our
            // RefreshSlots later, and vanilla hasn't placed cartridges into
            // the slots yet either. Stash the saved reference id and apply
            // it in OnFinishedLoad.
            if (savedData is EquipmentPlusTabletSaveData sd)
                ActiveSlotPersistence.PendingActiveCartridge[__instance.ReferenceId] =
                    sd.ActiveCartridgeReferenceId;

            DynamicSlots.RefreshSlots(__instance, Slot.Class.Cartridge);
        }
    }
}
