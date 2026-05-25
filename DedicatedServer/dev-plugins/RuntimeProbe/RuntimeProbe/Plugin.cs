using System;
using Assets.Scripts.Objects;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace RuntimeProbe
{
    /// <summary>
    ///     RuntimeProbe is a developer plugin that runs scenario-driven runtime probes
    ///     on the Stationeers dedicated server. Scenarios are read-and-log diagnostics
    ///     and reflection-driven calls into other mods' patch surfaces, pumped from a
    ///     simulation tick so the probe fires on every game tick the world is running.
    ///
    ///     The plugin is intentionally NOT a release mod. It lives under
    ///     <c>DedicatedServer/dev-plugins/</c> next to the dedi launcher, ships as a
    ///     single DLL, and never gets a Workshop handle. Scenarios that need a
    ///     specific mod's patches loaded (PowerGridPlus today) gracefully no-op when
    ///     that mod is absent. See <c>README.md</c> next to this file for the scenario
    ///     catalogue and how to add new scenarios.
    /// </summary>
    [BepInDependency("stationeers.launchpad", BepInDependency.DependencyFlags.HardDependency)]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "net.runtimeprobe";
        public const string PluginName = "RuntimeProbe";
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<string> Scenario;
        internal static ConfigEntry<int> DelayTicks;
        internal static ConfigEntry<bool> LogInventoryOnFirstTick;

        private void Awake()
        {
            Log = Logger;

            Scenario = Config.Bind(
                "Probe", "Scenario", "",
                new ConfigDescription(
                    "Scenario id to run after world load. Empty string disables the probe. " +
                    "General scenarios: 'inventory' (counts of power entities by type), " +
                    "'battery-charge-snapshot' (every 5 ticks, log every Battery's PowerStored). " +
                    "PowerGridPlus-specific scenarios (require the net.powergridplus plugin loaded; otherwise no-op): " +
                    "'pgp-transformer-conservation', 'pgp-battery-efficiency-probe', " +
                    "'pgp-apc-idle-probe', 'pgp-cable-burn-probe'. " +
                    "See DedicatedServer/dev-plugins/RuntimeProbe/README.md for the full catalogue."));

            DelayTicks = Config.Bind(
                "Probe", "Delay Ticks", 5,
                new ConfigDescription(
                    "How many simulation ticks to wait after world load before the scenario fires. " +
                    "A handful of ticks lets the simulation settle so initial transients do not " +
                    "pollute the snapshot."));

            LogInventoryOnFirstTick = Config.Bind(
                "Probe", "Log Inventory On First Tick", true,
                new ConfigDescription(
                    "When true, on the first scenario tick log a one-line inventory of power " +
                    "entities (counts of Battery / Transformer / AreaPowerControl / CableNetwork / " +
                    "CableFuse). Runs regardless of which Scenario is selected and is cheap."));

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
