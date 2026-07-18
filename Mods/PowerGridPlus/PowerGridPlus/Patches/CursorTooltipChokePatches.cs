using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Inventory;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     The all-surfaces visibility choke point (user decision 2026-07-18: every mod hover
    ///     shows on the casing with and without ALT and with any tool in hand). Two gaps made
    ///     that untrue before (Research/Patterns/PassiveTooltipPipelines.md): the crosshair HUD
    ///     displays a passive tooltip only when its Title is non-empty, and the tool-in-hand
    ///     branches of <c>InventoryManager.NormalModeThing</c> build their tooltip from the
    ///     action instance, bypassing <c>GetPassiveTooltip</c> and every postfix on it.
    ///
    ///     <para>Rather than patching each construction site, this pair brackets the crosshair
    ///     pipeline with a flag (NormalModeThing prefix sets it, a finalizer clears it) and
    ///     completes the final tooltip at the single display call
    ///     (<c>Tooltip.HandleToolTipDisplay</c>): append the mod block when the pipeline missed
    ///     it, and fill an empty Title so the display gate passes. Purely additive: vanilla text
    ///     is never removed or reordered, the Title is filled only when vanilla left it empty,
    ///     and a block already present (the GetPassiveTooltip or button patches ran) is never
    ///     appended twice, detected by its first line in either field. The ALT pipeline never
    ///     sets the flag and needs none of this (it renders Extended-only tooltips natively).</para>
    /// </summary>
    [HarmonyPatch]
    internal static class CursorTooltipChokePatches
    {
        // Main-thread UI pipeline only; a plain flag suffices.
        private static bool _inCursorPipeline;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(InventoryManager), "NormalModeThing")]
        private static void NormalModeThing_Prefix() => _inCursorPipeline = true;

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(InventoryManager), "NormalModeThing")]
        private static void NormalModeThing_Finalizer() => _inCursorPipeline = false;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Assets.Scripts.UI.Tooltip), "HandleToolTipDisplay")]
        private static void HandleToolTipDisplay_Prefix(ref PassiveTooltip passiveTooltip)
        {
            if (!_inCursorPipeline) return;
            var thing = CursorManager.CursorThing;
            if (thing == null) return;

            string block = null;
            long refId = FaultHover.ResolveFaultRefId(thing);
            int tick = ElectricityTickCounter.CurrentTick;
            if (FaultHover.TryGetMergedBlock(refId, tick, thing, out var faultBlock, out _))
                block = faultBlock;
            else if (thing is CableRuptured)
                block = BurnReasonPatches.BuildBurnLine(thing);
            if (string.IsNullOrEmpty(block)) return;

            // First-line marker: the title line is deterministic per state, so its presence in
            // either field means an earlier patch on this pipeline already carries the block.
            string marker = block;
            int nl = block.IndexOf('\n');
            if (nl > 0) marker = block.Substring(0, nl);
            bool present =
                (passiveTooltip.Title != null && passiveTooltip.Title.Contains(marker))
                || (passiveTooltip.Extended != null && passiveTooltip.Extended.Contains(marker));
            if (!present)
            {
                string aligned = "<align=left>" + block + "</align>";
                passiveTooltip.Extended = string.IsNullOrEmpty(passiveTooltip.Extended)
                    ? aligned
                    : passiveTooltip.Extended + "\n" + aligned;
            }
            if (string.IsNullOrEmpty(passiveTooltip.Title))
                passiveTooltip.Title = thing.DisplayName;
        }
    }
}
