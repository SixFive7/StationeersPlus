using System;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Reagents;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Super-heavy cable cost multiplier: scales the ingredient cost of crafting a super-heavy
    ///     cable coil (the Electronics Printer recipe for <c>ItemCableCoilSuperHeavy</c> -- vanilla
    ///     is Time 8, Energy 800, Constantan 0.5, Electrum 0.5).
    ///
    ///     The shipped <c>GameData/cable-recipes.xml</c> overlay does the headline 2x bump through the
    ///     game's own recipe loader (a <c>&lt;RecipeData&gt;</c> with a matching <c>&lt;PrefabName&gt;</c>
    ///     patch-replaces the vanilla entry -- see <c>Research/GameSystems/RecipeDataLoading.md</c>), so the
    ///     default cost increase works with no runtime patching at all. <see cref="ApplyRecipeCost"/> then
    ///     re-applies the recipe at runtime when the configured multiplier differs from the overlay's 2x,
    ///     so the config value wins. (Note: a dedicated-server deploy via <c>-DeployMods</c> copies only the
    ///     DLL, not the <c>GameData/</c> folder, so on that test path the overlay is absent and only the
    ///     runtime path -- when the multiplier is non-default -- applies. A real install ships the whole
    ///     mod folder, including <c>GameData/</c>.)
    /// </summary>
    internal static class CableCostPatches
    {
        // Vanilla recipe values for ItemCableCoilSuperHeavy (rocketstation_Data/StreamingAssets/Data/electronics.xml).
        private const int VanillaTime = 8;
        private const int VanillaEnergy = 800;
        private const double VanillaConstantan = 0.5;
        private const double VanillaElectrum = 0.5;
        private const float OverlayMultiplier = 2.0f;

        internal static void ApplyRecipeCost()
        {
            float multiplier = Settings.SuperHeavyCableCostMultiplier.Value;
            if (Math.Abs(multiplier - OverlayMultiplier) < 0.0001f)
                return; // the GameData overlay already set 2x.

            try
            {
                var recipe = new Recipe
                {
                    Time = VanillaTime,
                    Energy = VanillaEnergy,
                    Constantan = VanillaConstantan * multiplier,
                    Electrum = VanillaElectrum * multiplier,
                };
                var recipeData = new WorldManager.RecipeData
                {
                    PrefabName = "ItemCableCoilSuperHeavy",
                    Recipe = recipe,
                };
                ElectronicsPrinter.RecipeComparable.AddRecipe(recipeData, null);
                ElectronicsPrinter.RecipeComparable.GenerateRecipieList();
                Plugin.Log?.LogInfo(
                    $"Super-Heavy Cable Cost Multiplier {multiplier:0.##} applied: ItemCableCoilSuperHeavy " +
                    $"-> Constantan {VanillaConstantan * multiplier:0.##}, Electrum {VanillaElectrum * multiplier:0.##}.");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning(
                    $"Couldn't apply Super-Heavy Cable Cost Multiplier {multiplier:0.##} at runtime ({e.Message}). " +
                    "The super-heavy cable coil recipe stays at the 2x default from the GameData overlay (or vanilla if " +
                    "the overlay isn't deployed). See TODO.md if this needs fixing.");
            }
        }
    }
}
