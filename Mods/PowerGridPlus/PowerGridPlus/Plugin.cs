using System;
using Assets.Scripts.Objects;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using LaunchPadBooster;
using PowerGridPlus.Patches;

namespace PowerGridPlus
{
    /// <summary>
    ///     Power Grid Plus -- a pure-patch overhaul of the Stationeers power simulation, derived from
    ///     Sukasa's Re-Volt (MIT), plus a three-tier transmission-voltage backbone.
    /// </summary>
    [BepInDependency("stationeers.launchpad", BepInDependency.DependencyFlags.HardDependency)]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "net.powergridplus";
        public const string PluginName = "Power Grid Plus";
        public const string PluginVersion = "0.1.0";

        internal static readonly Mod MOD = new Mod(PluginName, PluginVersion);
        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            Settings.Bind(Config);
            Prefab.OnPrefabsLoaded += OnPrefabsLoaded;
            Log.LogInfo($"{PluginName} v{PluginVersion} loaded; patches deferred to prefab load");
        }

        private void OnPrefabsLoaded()
        {
            Prefab.OnPrefabsLoaded -= OnPrefabsLoaded;
            try
            {
                MOD.Networking.Required = true;

                var harmony = new Harmony(PluginGuid);
                harmony.PatchAll();

                CableCostPatches.ApplyRecipeCost();
                Ic10ConstantsPatcher.Apply();

                Log.LogInfo($"{PluginName} patches applied");
            }
            catch (Exception e)
            {
                Log.LogFatal($"{PluginName} failed to apply patches: {e}");
            }
        }
    }
}
