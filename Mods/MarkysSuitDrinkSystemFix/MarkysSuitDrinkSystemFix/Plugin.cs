using System;
using Assets.Scripts.Objects;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace MarkysSuitDrinkSystemFix
{
    // Compatibility patch for Marky's Suit Drink System (by Marky, Workshop item 3644610659).
    //
    // The Sanitation update changed Entity.Hydrate(float) to Entity.Hydrate(Mole). Marky's mod still
    // calls the removed float overload from its Suit.InteractWith prefix. Because the missing method is
    // resolved when the prefix is JIT-compiled, it throws MissingMethodException on EVERY invocation,
    // not only when a drink happens; the inventory UI calls InteractWith every frame to refresh the
    // interaction text, so the log fills with the exception and the Drink action never hydrates.
    //
    // This patch removes Marky's broken Suit.InteractWith prefix and installs a corrected one that calls
    // Hydrate(Mole). His other patches (the Water Tank slot, the "Drink" name, the slot label) are left
    // in place because none of them touch the removed method.
    //
    // Marky's mod is a BepInEx plugin loaded by StationeersLaunchPad, which AddComponents plugin types
    // directly and bypasses the BepInEx Chainloader, so [BepInDependency] does NOT order our Awake after
    // his. Ordering is enforced two ways instead: an <OrderAfter> entry in About.xml (StationeersLaunchPad's
    // own load-order graph), and deferring the unpatch to Prefab.OnPrefabsLoaded, which fires once on the
    // main thread after every plugin Awake has run (see Research/GameSystems/ModLoadSequence.md). By then
    // Marky's PatchAll is guaranteed complete regardless of sort settings. If his mod is absent, this
    // plugin resolves nothing and does nothing.
    [BepInDependency("stationeers.launchpad", BepInDependency.DependencyFlags.HardDependency)]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "net.markyssuitdrinksystemfix";
        public const string PluginName = "MarkysSuitDrinkSystemFix";
        public const string PluginVersion = "1.0.1";

        internal static ManualLogSource Log;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            _harmony = new Harmony(PluginGuid);

            // Defer until Marky's Suit Drink System has applied its patches in its own Awake.
            Prefab.OnPrefabsLoaded += OnAllModsLoaded;
            Log.LogInfo($"{PluginName} v{PluginVersion} loaded; fix deferred until all mods are loaded.");
        }

        private void OnAllModsLoaded()
        {
            Prefab.OnPrefabsLoaded -= OnAllModsLoaded;
            try
            {
                SuitDrinkPatches.Apply(_harmony);
            }
            catch (Exception e)
            {
                Log.LogError($"Failed to apply Marky's Suit Drink System fix: {e}");
            }
        }
    }
}
