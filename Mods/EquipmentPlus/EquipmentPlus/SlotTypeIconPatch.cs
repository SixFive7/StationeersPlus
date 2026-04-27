using Assets.Scripts.Objects;
using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace EquipmentPlus
{
    /// <summary>
    /// Vanilla's <c>Slot.PopulateSlotTypeSprites</c> (called once from
    /// <c>InventoryManager.ManagerAwake</c>) scans
    /// <c>Resources/UI/SlotTypes</c> for sprites named
    /// <c>sloticon-&lt;lowercase-enum-name&gt;</c> and registers them in
    /// <c>Slot._slotTypeLookup</c>. There's a <c>sloticon-cartridge</c>
    /// asset but no <c>sloticon-sensorprocessingunit</c>, so the runtime
    /// lookup returns null for SPU and empty sensor-chip slots render
    /// with no hint icon. Vanilla has this same bug on its own lens
    /// slots -- you just don't notice because vanilla ships 2 chips per
    /// lens and they're usually occupied.
    ///
    /// A Postfix on PopulateSlotTypeSprites doesn't help: that method
    /// runs at InventoryManager.ManagerAwake, which fires BEFORE our
    /// plugin patches are installed, so the Postfix never attaches to
    /// the one call that matters. Instead we populate the dictionary
    /// directly at plugin load. Vanilla's later Adds don't collide
    /// because PopulateSlotTypeSprites only adds keys it doesn't have.
    /// </summary>
    internal static class SlotTypeIconPatch
    {
        internal static void RegisterMissingSensorIcon()
        {
            try
            {
                var dict = AccessTools.Field(typeof(Slot), "_slotTypeLookup")
                    .GetValue(null) as Dictionary<int, Sprite>;
                if (dict == null)
                {
                    EquipmentPlusPlugin.Log.LogWarning(
                        "Slot._slotTypeLookup is null; can't register SPU icon");
                    return;
                }

                int spuKey = (int)Slot.Class.SensorProcessingUnit;
                if (dict.TryGetValue(spuKey, out var existing) && existing != null)
                    return;

                Sprite chosen = null;

                // First try: any 'sensor'-named sprite in the same resource
                // folder vanilla uses. Covers future vanilla/mod additions.
                var sprites = Resources.LoadAll<Sprite>("UI/SlotTypes");
                if (sprites != null)
                {
                    foreach (var s in sprites)
                    {
                        if (s == null || string.IsNullOrEmpty(s.name)) continue;
                        if (s.name.IndexOf("sensor", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            chosen = s;
                            break;
                        }
                    }
                }

                // Fallback: reuse the Cartridge icon. It's a chip-shape that
                // communicates "put a chip here" better than a blank slot.
                if (chosen == null &&
                    dict.TryGetValue((int)Slot.Class.Cartridge, out var cart) &&
                    cart != null)
                {
                    chosen = cart;
                }

                if (chosen != null)
                {
                    dict[spuKey] = chosen;
                    EquipmentPlusPlugin.Log.LogInfo(
                        $"Registered SlotTypeIcon for SensorProcessingUnit: {chosen.name}");
                }
                else
                {
                    EquipmentPlusPlugin.Log.LogWarning(
                        "No fallback sprite found for SensorProcessingUnit " +
                        "(no 'sensor' match in Resources/UI/SlotTypes and no Cartridge entry)");
                }
            }
            catch (Exception e)
            {
                EquipmentPlusPlugin.Log.LogWarning(
                    $"SlotTypeIconPatch failed: {e.Message}");
            }
        }
    }
}
