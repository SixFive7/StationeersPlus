using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Serialization;
using HarmonyLib;
using JetBrains.Annotations;

namespace EquipmentPlus
{
    /// <summary>
    /// Mutates item prefabs to add extra blocked slots before any instances spawn.
    /// Runs once per session via three lifecycle hooks.
    /// </summary>
    public static class PrefabPatch
    {
        private static bool _hasRun;

        internal static void Apply()
        {
            if (_hasRun)
                return;
            _hasRun = true;

            int extra = EquipmentPlusPlugin.MaxExtraSlots;

            // Sensor Lenses
            var sensorLenses = Prefab.Find("ItemSensorLenses");
            if (sensorLenses != null)
            {
                EquipmentPlusPlugin.Log.LogInfo(
                    $"ItemSensorLenses: {sensorLenses.Slots.Count} vanilla slot(s), adding {extra} dynamic slots");
                DynamicSlots.AddBlockedSlots(sensorLenses, extra, Slot.Class.SensorProcessingUnit);
            }

            // Advanced Tablet
            var advancedTablet = Prefab.Find("ItemAdvancedTablet");
            if (advancedTablet != null)
            {
                EquipmentPlusPlugin.Log.LogInfo(
                    $"ItemAdvancedTablet: {advancedTablet.Slots.Count} vanilla slot(s), adding {extra} dynamic slots");
                DynamicSlots.AddBlockedSlots(advancedTablet, extra, Slot.Class.Cartridge);
            }
        }
    }

    [HarmonyPatch(typeof(Assets.Scripts.Objects.World), nameof(Assets.Scripts.Objects.World.StartNewWorld))]
    public class StartNewWorldPatch
    {
        [UsedImplicitly]
        public static void Prefix() => PrefabPatch.Apply();
    }

    [HarmonyPatch(typeof(XmlSaveLoad), nameof(XmlSaveLoad.LoadWorld))]
    public class LoadWorldPatch
    {
        [UsedImplicitly]
        public static void Prefix() => PrefabPatch.Apply();
    }

    [HarmonyPatch(typeof(Assets.Scripts.NetworkClient), "ProcessJoinData")]
    public class ProcessJoinDataPatch
    {
        [UsedImplicitly]
        public static void Prefix() => PrefabPatch.Apply();
    }
}
