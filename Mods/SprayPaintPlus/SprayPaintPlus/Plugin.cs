using Assets.Scripts.Objects;
using Assets.Scripts.Serialization;
using Assets.Scripts.Util;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LaunchPadBooster;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SprayPaintPlus
{
    [BepInDependency("stationeers.launchpad", BepInDependency.DependencyFlags.HardDependency)]
    [BepInIncompatibility("net.elmo.stationeers.ColorCycler")]
    [BepInIncompatibility("net.elmo.stationeers.NetworkPainter")]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class SprayPaintPlusPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "net.spraypaintplus";
        public const string PluginName = "SprayPaintPlus";
        public const string PluginVersion = "1.5.0";

        internal static readonly Mod MOD = new Mod(PluginName, PluginVersion);

        internal static ManualLogSource Log;

        // Client settings
        internal static ConfigEntry<bool> InvertColorScrollDirection;
        internal static ConfigEntry<bool> PaintSingleItemByDefault;

        // Server settings
        internal static ConfigEntry<bool> UnlimitedSprayPaintUses;
        internal static ConfigEntry<bool> SuppressSprayPaintPollution;
        internal static ConfigEntry<bool> EnableNetworkPainting;
        internal static ConfigEntry<bool> NetworkPaintPipes;
        internal static ConfigEntry<bool> NetworkPaintCables;
        internal static ConfigEntry<bool> NetworkPaintChutes;
        internal static ConfigEntry<bool> NetworkPaintWalls;
        internal static ConfigEntry<bool> NetworkPaintLargeStructures;
        internal static ConfigEntry<bool> NetworkPaintRails;
        internal static ConfigEntry<bool> EnableGlowPaint;

        private static readonly string[] ConflictingAssemblies = { "ColorCycler", "NetworkPainter" };

        void Awake()
        {
            Log = Logger;
            BindConfig();

            // StationeersLaunchPad loads mods progressively; conflicting assemblies may not exist
            // yet when our Awake() fires. Prefab.OnPrefabsLoaded fires after StationeersLaunchPad
            // finishes loading all mods. No patches are applied until the check passes.
            Prefab.OnPrefabsLoaded += OnAllModsLoaded;
        }

        private void OnAllModsLoaded()
        {
            Prefab.OnPrefabsLoaded -= OnAllModsLoaded;

            var conflicts = new List<string>();
            foreach (var name in ConflictingAssemblies)
            {
                if (AppDomain.CurrentDomain.GetAssemblies()
                    .Any(a => a.GetName().Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    conflicts.Add(name);
                    Log.LogError($"CONFLICT: {name}.dll is loaded. SprayPaintPlus replaces it.");
                }
            }

            if (conflicts.Count > 0)
            {
                Log.LogFatal("SprayPaintPlus NOT LOADED. Disable the conflicting mods and restart.");
                StartCoroutine(RepeatWarning(string.Join(", ", conflicts)));
                return;
            }

            try
            {
                MOD.Networking.Required = true;
                MOD.Networking.RegisterMessage<SprayCanColorMessage>();
                MOD.Networking.RegisterMessage<PaintModifierMessage>();

                // Register GlowThingSaveData via LaunchPadBooster so XmlSaveLoad
                // ExtraTypes picks it up, AND inject directly as a fallback
                // for load-order races. See Research/GameSystems/SaveDataRegistration.md.
                MOD.AddSaveDataType<GlowThingSaveData>();
                RegisterSaveDataTypeLate(typeof(GlowThingSaveData));

                var harmony = new Harmony(PluginGuid);
                harmony.PatchAll();
                Log.LogInfo("Patches applied successfully");
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
            var msg = $"[SprayPaintPlus] NOT LOADED! Conflicting mods: {conflicts}. " +
                      "Disable them and restart.";
            while (true)
            {
                Debug.LogError(msg);
                yield return new WaitForSeconds(5f);
            }
        }

        private void BindConfig()
        {
            PaintSingleItemByDefault = Config.Bind(
                "Client - Preferences", "Paint Single Item By Default", false,
                new ConfigDescription(
                    "(Client-local) Changes the default painting behavior. " +
                    "When enabled, painting targets a single item by default " +
                    "and you hold Shift to paint the entire network instead. " +
                    "When disabled (default), painting targets the entire network " +
                    "and Shift restricts to a single item. " +
                    "Each player can set this independently.",
                    null,
                    new KeyValuePair<string, int>("Order", 10)));

            InvertColorScrollDirection = Config.Bind(
                "Client - Preferences", "Invert Color Scroll Direction", false,
                new ConfigDescription(
                    "(Client-local) Reverses the mouse wheel direction when scrolling " +
                    "through spray can colors. Each player can set this independently.",
                    null,
                    new KeyValuePair<string, int>("Order", 20)));

            UnlimitedSprayPaintUses = Config.Bind(
                "Server - Consumables", "Unlimited Spray Paint Uses", true,
                new ConfigDescription(
                    "(Server-authoritative) Makes all spray cans infinite. " +
                    "When disabled, spray cans are consumed after their normal number of uses. " +
                    "Only the server's value matters in multiplayer.",
                    null,
                    new KeyValuePair<string, int>("Order", 10)));

            SuppressSprayPaintPollution = Config.Bind(
                "Server - Consumables", "Suppress Spray Paint Pollution", true,
                new ConfigDescription(
                    "(Server-authoritative) Prevents spray cans from releasing pollutant gas " +
                    "into the atmosphere when used. " +
                    "Only the server's value matters in multiplayer.",
                    null,
                    new KeyValuePair<string, int>("Order", 20)));

            EnableGlowPaint = Config.Bind(
                "Server - Glow Paint", "Enable Glow Paint", true,
                new ConfigDescription(
                    "(Server-authoritative) When enabled, painting a Thing with the Spray Paint Gun makes it " +
                    "glow (emissive material); painting with a bare Spray Paint can " +
                    "keeps the normal, non-glowing paint. When disabled, the gun behaves like a can. " +
                    "Only the server's value matters in multiplayer.",
                    null,
                    new KeyValuePair<string, int>("Order", 10)));

            EnableNetworkPainting = Config.Bind(
                "Server - Network Painting", "Enable Network Painting", true,
                new ConfigDescription(
                    "(Server-authoritative) When spray-painting a pipe, cable, or chute, " +
                    "the entire connected network is painted at once. " +
                    "When disabled, only the targeted item is painted regardless of modifiers. " +
                    "Only the server's value matters in multiplayer.",
                    null,
                    new KeyValuePair<string, int>("Order", 10)));

            NetworkPaintPipes = Config.Bind(
                "Server - Network Painting", "Network Paint Pipes", true,
                new ConfigDescription(
                    "(Server-authoritative) Includes pipe networks (pipes, passive vents, hydroponic trays) " +
                    "when painting an entire network. " +
                    "Has no effect if Enable Network Painting is disabled. " +
                    "Only the server's value matters in multiplayer.",
                    null,
                    new KeyValuePair<string, int>("Order", 20)));

            NetworkPaintCables = Config.Bind(
                "Server - Network Painting", "Network Paint Cables", true,
                new ConfigDescription(
                    "(Server-authoritative) Includes cable networks when painting an entire network. " +
                    "Has no effect if Enable Network Painting is disabled. " +
                    "Only the server's value matters in multiplayer.",
                    null,
                    new KeyValuePair<string, int>("Order", 30)));

            NetworkPaintChutes = Config.Bind(
                "Server - Network Painting", "Network Paint Chutes", true,
                new ConfigDescription(
                    "(Server-authoritative) Includes chute networks when painting an entire network. " +
                    "Has no effect if Enable Network Painting is disabled. " +
                    "Only the server's value matters in multiplayer.",
                    null,
                    new KeyValuePair<string, int>("Order", 40)));

            NetworkPaintWalls = Config.Bind(
                "Server - Network Painting", "Network Paint Walls", true,
                new ConfigDescription(
                    "(Server-authoritative) When spray-painting a wall, all same-type walls " +
                    "bounding the same room are painted too. " +
                    "Has no effect if Enable Network Painting is disabled. " +
                    "Only the server's value matters in multiplayer.",
                    null,
                    new KeyValuePair<string, int>("Order", 50)));

            NetworkPaintRails = Config.Bind(
                "Server - Network Painting", "Network Paint Rails", true,
                new ConfigDescription(
                    "(Server-authoritative) When spray-painting a robotic arm rail, junction, bypass, or dock, " +
                    "every piece on the same robotic arm assembly is painted too. " +
                    "Has no effect if Enable Network Painting is disabled. " +
                    "Only the server's value matters in multiplayer.",
                    null,
                    new KeyValuePair<string, int>("Order", 60)));

            NetworkPaintLargeStructures = Config.Bind(
                "Server - Network Painting", "Network Paint Large Structures", true,
                new ConfigDescription(
                    "(Server-authoritative) When spray-painting a frame, girder, or other large structure, " +
                    "all orthogonally-connected structures of the same exact type are painted too. " +
                    "Has no effect if Enable Network Painting is disabled. " +
                    "Only the server's value matters in multiplayer.",
                    null,
                    new KeyValuePair<string, int>("Order", 70)));
        }
    }
}
