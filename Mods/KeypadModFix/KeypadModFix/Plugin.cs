using System;
using Assets.Scripts.Objects;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace KeypadModFix
{
    // Temporary compatibility patch for KeypadMod (by WIKUS, Workshop item 3478434324).
    //
    // KeypadMod is a StationeersMods content mod loaded by StationeersLaunchPad, so its
    // assembly is not present yet when this BepInEx plugin's Awake() runs. All patching is
    // therefore deferred to Prefab.OnPrefabsLoaded, which fires once on the main thread after
    // every mod has finished loading (see Research/GameSystems/ModLoadSequence.md). If
    // KeypadMod is not installed, this plugin resolves nothing and does nothing.
    [BepInDependency("stationeers.launchpad", BepInDependency.DependencyFlags.HardDependency)]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "net.keypadmodfix";
        public const string PluginName = "KeypadModFix";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            _harmony = new Harmony(PluginGuid);

            // Defer until KeypadMod's assembly is loaded by StationeersLaunchPad.
            Prefab.OnPrefabsLoaded += OnAllModsLoaded;
            Log.LogInfo($"{PluginName} v{PluginVersion} loaded; patches deferred until all mods are loaded.");
        }

        private void OnAllModsLoaded()
        {
            Prefab.OnPrefabsLoaded -= OnAllModsLoaded;
            try
            {
                KeypadPatches.Apply(_harmony);
            }
            catch (Exception e)
            {
                Log.LogError($"Failed to apply KeypadMod patches: {e}");
            }
        }
    }
}
