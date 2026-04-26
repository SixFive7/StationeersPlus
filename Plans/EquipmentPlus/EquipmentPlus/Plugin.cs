using Assets.Scripts.Objects;
using Assets.Scripts.Serialization;
using Assets.Scripts.Util;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using LaunchPadBooster;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace EquipmentPlus
{
    [BepInDependency("stationeers.launchpad", BepInDependency.DependencyFlags.HardDependency)]
    [BepInIncompatibility("BetterAdvancedTablet")]
    [BepInIncompatibility("com.doggo.improved_configuration")]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class EquipmentPlusPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "net.equipmentplus";
        public const string PluginName = "EquipmentPlus";
        public const string PluginVersion = "1.0.0";

        internal static readonly Mod MOD = new Mod(PluginName, PluginVersion);
        internal static ManualLogSource Log;

        internal const int MaxExtraSlots = 100;

        // Conflicting mod assembly names (case-insensitive). StationeersLaunchPad bypasses BepInIncompatibility.
        private static readonly (string Assembly, string DisplayName)[] ConflictingMods =
        {
            ("betteradvancedtablet", "Better Advanced Tablet"),
            ("improvedconfiguration", "ImprovedConfiguration"),
            // Slot Configuration Cartridge (Workshop 3578912665) by Otis B., absorbed into BES.
            // Its GUID is uk.org.marginal.stationeers.SlotConfigurationCartridge.
            ("slotconfigurationcartridge", "Slot Configuration Cartridge"),
            ("betterheadlampmod", "Better Headlamp"),
        };

        void Awake()
        {
            Log = Logger;
            Prefab.OnPrefabsLoaded += OnAllModsLoaded;
        }

        private void OnAllModsLoaded()
        {
            Prefab.OnPrefabsLoaded -= OnAllModsLoaded;

            var conflicts = new List<string>();
            foreach (var (asmName, displayName) in ConflictingMods)
            {
                if (AppDomain.CurrentDomain.GetAssemblies()
                    .Any(a => a.GetName().Name.Equals(asmName, StringComparison.OrdinalIgnoreCase)))
                {
                    Log.LogError($"CONFLICT: {displayName} is loaded. " +
                                 $"EquipmentPlus replaces it, please disable {displayName}.");
                    conflicts.Add(displayName);
                }
            }

            if (conflicts.Count > 0)
            {
                Log.LogFatal("EquipmentPlus NOT LOADED. Disable the conflicting mods and restart.");
                StartCoroutine(RepeatWarning(string.Join(", ", conflicts)));
                return;
            }

            try
            {
                MOD.Networking.Required = true;
                MOD.Networking.RegisterMessage<SetActiveSensorMessage>();

                // Register our save-data subclasses via LaunchPadBooster so
                // future XmlSaveLoad.AddExtraTypes callers pick them up via
                // LaunchPadBooster's prefix, AND inject directly into XmlSaveLoad.ExtraTypes
                // + invalidate the cached XmlSerializer. See comment on
                // RegisterSaveDataTypeLate for why both are needed.
                MOD.AddSaveDataType<SensorLensesSaveData>();
                MOD.AddSaveDataType<EquipmentPlusTabletSaveData>();
                RegisterSaveDataTypeLate(typeof(SensorLensesSaveData));
                RegisterSaveDataTypeLate(typeof(EquipmentPlusTabletSaveData));

                var harmony = new Harmony(PluginGuid);
                harmony.PatchAll();
                Log.LogInfo("Patches applied successfully");

                // Fill in the SensorProcessingUnit slot-type icon that
                // vanilla leaves blank. Done directly rather than via a
                // Postfix on Slot.PopulateSlotTypeSprites because that
                // method runs before our plugin is loaded.
                SlotTypeIconPatch.RegisterMissingSensorIcon();
            }
            catch (Exception e)
            {
                Log.LogFatal($"Failed to apply patches: {e}");
            }
        }

        private static void RegisterSaveDataTypeLate(Type t)
        {
            try
            {
                var extraTypesField = AccessTools.Field(typeof(XmlSaveLoad), "ExtraTypes");
                var current = extraTypesField.GetValue(null) as Type[];
                if (current == null)
                {
                    extraTypesField.SetValue(null, new[] { t });
                }
                else if (!current.Contains(t))
                {
                    var next = new Type[current.Length + 1];
                    Array.Copy(current, next, current.Length);
                    next[current.Length] = t;
                    extraTypesField.SetValue(null, next);
                }

                // Force the WorldData XmlSerializer to be regenerated on next
                // access with the updated ExtraTypes. The field is private.
                var worldDataField = AccessTools.Field(typeof(Serializers), "_worldData");
                worldDataField?.SetValue(null, null);
            }
            catch (Exception e)
            {
                Log.LogWarning($"Late save-type registration failed: {e.Message}");
            }
        }

        private static IEnumerator RepeatWarning(string conflicts)
        {
            var msg = $"[EquipmentPlus] NOT LOADED! Conflicting mods: {conflicts}. " +
                      "Disable them and restart.";
            while (true)
            {
                Debug.LogError(msg);
                yield return new WaitForSeconds(5f);
            }
        }
    }
}
