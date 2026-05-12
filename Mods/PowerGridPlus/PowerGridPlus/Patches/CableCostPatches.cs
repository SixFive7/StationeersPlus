using System;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     NEW-2: multiplies the ingredient cost of crafting a super-heavy cable coil.
    ///
    ///     NOT YET IMPLEMENTED in this build. The recipe-overlay path (a GameData XML that the game's
    ///     recipe loader patch-replaces by prefab name -- see Research/GameSystems/RecipeDataLoading.md)
    ///     and the runtime path (find the recipe in the fabricator's RecipeComparable and scale its
    ///     reagents) both need two facts verified in-game first: (1) the super-heavy cable coil's prefab
    ///     name, and (2) which fabricator crafts it. Both are tracked in TODO.md. Until then the config
    ///     entry exists but is inert, and we log a warning if the player has changed it from the default.
    /// </summary>
    internal static class CableCostPatches
    {
        internal static void ApplyRecipeCost()
        {
            float multiplier = Settings.SuperHeavyCableCostMultiplier.Value;
            if (Math.Abs(multiplier - 1.0f) < 0.0001f)
                return;

            Plugin.Log?.LogWarning(
                $"Super-Heavy Cable Cost Multiplier is set to {multiplier:0.##}, but recipe patching (NEW-2) is " +
                "not implemented in this build yet -- the super-heavy cable coil recipe is unchanged. " +
                "See TODO.md (\"NEW-2 -- super-heavy cable cost multiplier\").");
        }
    }
}
