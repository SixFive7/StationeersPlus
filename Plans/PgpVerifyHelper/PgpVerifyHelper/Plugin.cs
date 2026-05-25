using System;
using Assets.Scripts.Objects;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace PgpVerifyHelper
{
    /// <summary>
    ///     Developer-tooling plugin for verifying Power Grid Plus (and related power mods) headlessly.
    ///     Drives scenario execution from a config string so an agent can flip a value, restart the
    ///     dedicated server, and observe the resulting state without a connected client.
    ///
    ///     This plugin is NOT a release mod. It lives under Plans/ because it is a testing aid; it
    ///     does not register with `MOD.Networking.Required` and does not need to ship to players.
    ///
    ///     Scenario list lives in <see cref="ScenarioRunner"/>. Each scenario is a string id matched
    ///     against the `Scenario` config value. The empty string disables the helper.
    /// </summary>
    [BepInDependency("stationeers.launchpad", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("net.powergridplus", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "net.pgpverifyhelper";
        public const string PluginName = "PgpVerifyHelper";
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<string> Scenario;
        internal static ConfigEntry<int> DelayTicks;
        internal static ConfigEntry<bool> LogInventoryOnFirstTick;

        private void Awake()
        {
            Log = Logger;

            Scenario = Config.Bind(
                "Verify", "Scenario", "",
                new ConfigDescription(
                    "Scenario id to run after world load. Empty string disables the helper. " +
                    "Known scenarios: 'inventory' (logs how many of each power entity loaded), " +
                    "'battery-charge-snapshot' (logs PowerStored on every Battery once per second). " +
                    "See Plans/PgpVerifyHelper/README.md for the full list."));

            DelayTicks = Config.Bind(
                "Verify", "Delay Ticks", 5,
                new ConfigDescription(
                    "How many ElectricityTicks to wait after world load before the scenario fires. " +
                    "A handful of ticks lets the power simulation settle so initial transients " +
                    "do not pollute the snapshot."));

            LogInventoryOnFirstTick = Config.Bind(
                "Verify", "Log Inventory On First Tick", true,
                new ConfigDescription(
                    "When true, on the first scenario tick, log a one-line inventory of power " +
                    "entities (counts of Battery / Transformer / AreaPowerControl / CableNetwork). " +
                    "This runs regardless of which Scenario is selected and is cheap."));

            Prefab.OnPrefabsLoaded += OnPrefabsLoaded;

            Log.LogInfo($"{PluginName} v{PluginVersion} loaded; scenario='{Scenario.Value}'");
        }

        private void OnPrefabsLoaded()
        {
            Prefab.OnPrefabsLoaded -= OnPrefabsLoaded;
            try
            {
                var harmony = new Harmony(PluginGuid);
                harmony.PatchAll();

                ScenarioRunner.Initialize(Log, Scenario.Value, DelayTicks.Value, LogInventoryOnFirstTick.Value);

                Log.LogInfo($"{PluginName} patches applied; ScenarioRunner armed for '{Scenario.Value}'");
            }
            catch (Exception e)
            {
                Log.LogError($"{PluginName} failed to apply patches: {e}");
            }
        }
    }
}
