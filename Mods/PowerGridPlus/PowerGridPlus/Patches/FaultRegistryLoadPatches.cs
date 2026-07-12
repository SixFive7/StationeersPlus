using Assets.Scripts.Serialization;
using HarmonyLib;
using PowerGridPlus.Core;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     World-load reset, arm-and-drain edition (POWER.md §0 decision 24 stage 0).
    ///     <c>XmlSaveLoad.LoadWorld</c> is async, so this postfix fires at load START on the main
    ///     thread while a previous world's tick can still be in flight on the worker. Only two kinds
    ///     of work are safe (and necessary) HERE:
    ///
    ///     <list type="bullet">
    ///       <item><b>Pre-restore clears</b>: the burn-reason mirror must empty BEFORE the sidecar's
    ///       per-Thing <c>OnFinishedLoad</c> restore repopulates it during this same load.</item>
    ///       <item><b>One-shot arms</b>: flags read by the first atomic tick (census, ledger sweep,
    ///       save/load self-check, and the <see cref="LoadBoundary"/> that performs every remaining
    ///       clear ON THE WORKER at the top of the next tick, where nothing can race it).</item>
    ///     </list>
    /// </summary>
    [HarmonyPatch(typeof(XmlSaveLoad), nameof(XmlSaveLoad.LoadWorld))]
    public static class FaultRegistryLoadPatches
    {
        [HarmonyPostfix]
        public static void LoadWorld_Postfix()
        {
            // Burn reasons re-attach from the side-car per Thing (BurnReasonSaveLoadPatches); this
            // clear must precede those restores, so it cannot be deferred to the tick boundary.
            BurnReasonRegistry.ClearAll();

            UnknownBridgeCensus.Arm();
            LedgerAdoption.Arm();
            SaveLoadSelfCheck.Arm();
            LoadBoundary.Arm();
            // The electricity-tick counter is relative (lockout = currentTick + 120); clearing the
            // registries is sufficient, no counter reset needed.
        }
    }
}
