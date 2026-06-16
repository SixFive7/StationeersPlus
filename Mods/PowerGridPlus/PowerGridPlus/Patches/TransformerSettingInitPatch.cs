using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Initialises <c>Transformer.Setting = OutputMaximum</c> when a transformer enters the world
    ///     (POWER.md §5.3): a freshly placed transformer runs at full rated throughput by default
    ///     instead of vanilla's Setting = 0, because under PowerGridPlus the in-world knob controls
    ///     Priority and almost no player will ever touch Setting directly.
    ///
    ///     <para>Saved worlds keep their Setting: during load, <c>Thing.OnRegistered</c> fires from
    ///     inside <c>Thing.Create</c> BEFORE <c>DeserializeSave</c> applies the saved value
    ///     (Research/Patterns/SaveLoadOrdering.md "OnRegistered fires before DeserializeSave"), so
    ///     this unconditional init is overwritten by the saved Setting for loaded transformers and
    ///     sticks only for fresh constructions. MP joins likewise receive the host's value via
    ///     DeserializeOnJoin after creation.</para>
    /// </summary>
    [HarmonyPatch(typeof(Assets.Scripts.Objects.Thing), nameof(Assets.Scripts.Objects.Thing.OnRegistered))]
    public static class TransformerSettingInitPatch
    {
        public static void Postfix(Assets.Scripts.Objects.Thing __instance, Cell cell)
        {
            if (!(__instance is Transformer transformer)) return;
            transformer.Setting = transformer.OutputMaximum;
        }
    }
}
