using Assets.Scripts.Objects;
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
        public const string PluginVersion = "1.1.1";

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

        private static readonly string[] ConflictingAssemblies = { "ColorCycler", "NetworkPainter" };

        void Awake()
        {
            Log = Logger;
            BindConfig();

            // SLP loads mods progressively — conflicting assemblies may not exist
            // yet when our Awake() fires. Prefab.OnPrefabsLoaded fires after SLP
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

                var harmony = new Harmony(PluginGuid);
                harmony.PatchAll();
                Log.LogInfo("Patches applied successfully");
            }
            catch (Exception e)
            {
                Log.LogFatal($"Failed to apply patches: {e}");
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
            InvertColorScrollDirection = Config.Bind(
                "Client", "Invert Color Scroll Direction", false,
                "(Client-side) Reverses the mouse wheel direction when scrolling " +
                "through spray can colors. Each player can set this independently.");

            PaintSingleItemByDefault = Config.Bind(
                "Client", "Paint Single Item By Default", false,
                "(Client-side) Changes the default painting behavior. " +
                "When enabled, painting targets a single item by default " +
                "and you hold Shift to paint the entire network instead. " +
                "When disabled (default), painting targets the entire network " +
                "and Shift restricts to a single item. " +
                "Each player can set this independently.");

            UnlimitedSprayPaintUses = Config.Bind(
                "Server", "Unlimited Spray Paint Uses", true,
                "(Server-side) Makes all spray cans infinite. " +
                "When disabled, spray cans are consumed after their normal number of uses. " +
                "Only the server's value matters in multiplayer.");

            SuppressSprayPaintPollution = Config.Bind(
                "Server", "Suppress Spray Paint Pollution", true,
                "(Server-side) Prevents spray cans from releasing pollutant gas " +
                "into the atmosphere when used. " +
                "Only the server's value matters in multiplayer.");

            EnableNetworkPainting = Config.Bind(
                "Server", "Enable Network Painting", true,
                "(Server-side) When spray-painting a pipe, cable, or chute, " +
                "the entire connected network is painted at once. " +
                "When disabled, only the targeted item is painted regardless of modifiers. " +
                "Only the server's value matters in multiplayer.");

            NetworkPaintPipes = Config.Bind(
                "Server", "Network Paint Pipes", true,
                "(Server-side) Includes pipe networks (pipes, passive vents, hydroponic trays) " +
                "when painting an entire network. " +
                "Has no effect if Enable Network Painting is disabled. " +
                "Only the server's value matters in multiplayer.");

            NetworkPaintCables = Config.Bind(
                "Server", "Network Paint Cables", true,
                "(Server-side) Includes cable networks when painting an entire network. " +
                "Has no effect if Enable Network Painting is disabled. " +
                "Only the server's value matters in multiplayer.");

            NetworkPaintChutes = Config.Bind(
                "Server", "Network Paint Chutes", true,
                "(Server-side) Includes chute networks when painting an entire network. " +
                "Has no effect if Enable Network Painting is disabled. " +
                "Only the server's value matters in multiplayer.");
        }
    }
}
