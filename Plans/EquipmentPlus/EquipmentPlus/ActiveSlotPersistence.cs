using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using HarmonyLib;
using JetBrains.Annotations;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace EquipmentPlus
{
    /// <summary>
    /// Vanilla's load sequence places every child of a Thing (sensor chips
    /// inside lenses, cartridges inside a tablet) one at a time AFTER the
    /// parent's DeserializeSave returns. Each MoveToSlot fires the parent's
    /// OnChildEnterInventory, which for SensorLenses unconditionally sets
    /// <c>Sensor = child</c>. As a result, the last chip to re-enter wins
    /// and any value we set from saved data is immediately overwritten.
    ///
    /// AdvancedTablet has an analogous problem: <c>Mode</c> isn't persisted
    /// at all, so it resets to 0 (first cartridge) on every load.
    ///
    /// This class runs the saved active-slot restore from <c>OnFinishedLoad</c>
    /// -- a Thing-lifecycle hook that fires after child slots are populated.
    /// Save-time capture is done by <c>SensorLensesSerializeSavePatch</c> and
    /// <c>AdvancedTabletSerializeSavePatch</c>; at deserialize time the
    /// saved value is stashed in a dictionary here; <c>OnFinishedLoad</c>
    /// reads the dictionary and applies.
    /// </summary>
    internal static class ActiveSlotPersistence
    {
        // Keyed by Thing.ReferenceId. Populated at DeserializeSave time from
        // the corresponding SaveData subclass; consumed at OnFinishedLoad;
        // cleared after use so stale entries don't leak between games.
        internal static readonly Dictionary<long, long> PendingActiveSensor =
            new Dictionary<long, long>();
        internal static readonly Dictionary<long, long> PendingActiveCartridge =
            new Dictionary<long, long>();
    }

    // OnFinishedLoad isn't declared on SensorLenses or AdvancedTablet; it's
    // inherited from DynamicThing (or higher). We patch via TargetMethod so
    // Harmony resolves the inherited entry -- declaring __instance as Thing
    // keeps the cast safe for every subclass Harmony routes through this
    // patch.

    [HarmonyPatch]
    public class SensorLensesOnFinishedLoadPatch
    {
        [UsedImplicitly]
        static MethodBase TargetMethod() =>
            typeof(SensorLenses).GetMethod("OnFinishedLoad",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        [UsedImplicitly]
        public static void Postfix(Thing __instance)
        {
            if (!(__instance is SensorLenses lenses)) return;
            if (!ActiveSlotPersistence.PendingActiveSensor.TryGetValue(
                    lenses.ReferenceId, out long savedId))
                return;

            ActiveSlotPersistence.PendingActiveSensor.Remove(lenses.ReferenceId);

            if (savedId == 0L)
            {
                lenses.Sensor = null;
                return;
            }

            var target = Referencable.Find<SensorProcessingUnit>(savedId);
            if (target != null && lenses.Slots.Any(s => s.Get() == target))
                lenses.Sensor = target;
        }
    }

    [HarmonyPatch]
    public class AdvancedTabletOnFinishedLoadPatch
    {
        [UsedImplicitly]
        static MethodBase TargetMethod() =>
            typeof(AdvancedTablet).GetMethod("OnFinishedLoad",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        [UsedImplicitly]
        public static void Postfix(Thing __instance)
        {
            if (!(__instance is AdvancedTablet tablet)) return;
            if (!ActiveSlotPersistence.PendingActiveCartridge.TryGetValue(
                    tablet.ReferenceId, out long savedId))
                return;

            ActiveSlotPersistence.PendingActiveCartridge.Remove(tablet.ReferenceId);

            if (savedId == 0L || tablet.CartridgeSlots == null) return;

            // Find the CartridgeSlots index whose occupant matches the
            // saved cartridge reference id, and set Mode to that index.
            // Thing.set_Mode propagates via Interactable.set_State; we also
            // call the private GetCartridge() to force the display to
            // reflect the new active cartridge immediately.
            for (int i = 0; i < tablet.CartridgeSlots.Count; i++)
            {
                var occ = tablet.CartridgeSlots[i].Get();
                if (occ != null && occ.ReferenceId == savedId)
                {
                    tablet.Mode = i;
                    AccessTools.Method(typeof(AdvancedTablet), "GetCartridge")
                        ?.Invoke(tablet, null);
                    return;
                }
            }
        }
    }
}
