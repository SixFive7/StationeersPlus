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

            if (TryFindIncompatibleMod(out var modName))
            {
                Log.LogFatal($"{PluginName} refuses to load: incompatible mod '{modName}' is also loaded. " +
                             "Both mods rewrite or extend the same vanilla power-tick / cable-type surface and would " +
                             "silently fight or guess. Disable one of them. No patches applied.");
                return;
            }

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

        /// <summary>
        ///     Detects mods whose patch surface overlaps Power Grid Plus and would silently fight or guess.
        ///     Matches by loaded-assembly name because StationeersMods-loaded mods do not show up in
        ///     <c>BepInEx.Bootstrap.Chainloader.PluginInfos</c> (Re-Volt loads through StationeersMods, not
        ///     BepInEx directly).
        /// </summary>
        private static bool TryFindIncompatibleMod(out string modName)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                var n = assemblies[i].GetName().Name;
                // Re-Volt (Sukasa) ships its plugin assembly as "ReVolt.dll" with namespace "ReVolt".
                if (string.Equals(n, "ReVolt", StringComparison.OrdinalIgnoreCase))
                {
                    modName = "Re-Volt";
                    return true;
                }
                // MoreCables (spacebuilder2020) is not subscribed locally; match the most likely
                // assembly-name variants without overreaching.
                if (string.Equals(n, "MoreCables", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(n, "MoreCablesMod", StringComparison.OrdinalIgnoreCase))
                {
                    modName = "MoreCables";
                    return true;
                }
            }
            modName = null;
            return false;
        }
    }
}
