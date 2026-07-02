using Assets.Scripts.Serialization;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Clears all transient fault state when a world loads (POWERTODO 0.4). The four fault registries
    ///     (Brownout / Overload / CycleFault / VariableVoltageFault) are in-memory only and NOT serialized;
    ///     a fault is a transient lockout, not a persistent property of a Thing. Wiping them on
    ///     <c>XmlSaveLoad.LoadWorld</c> avoids leftover state bleeding in when a host hot-swaps saves
    ///     without restarting the game. The first tick after load recomputes any still-warranted fault
    ///     from current topology, applying the full 60 s lockout fresh.
    ///
    ///     <para>Also clears the session-only <see cref="ApcDischargeRateRegistry"/> overrides so a new
    ///     world starts from the server-setting defaults.</para>
    /// </summary>
    [HarmonyPatch(typeof(XmlSaveLoad), nameof(XmlSaveLoad.LoadWorld))]
    public static class FaultRegistryLoadPatches
    {
        [HarmonyPostfix]
        public static void LoadWorld_Postfix()
        {
            BrownoutRegistry.ClearAll();
            OverloadRegistry.ClearAll();
            CycleFaultRegistry.ClearAll();
            VariableVoltageFaultRegistry.ClearAll();
            ApcDischargeRateRegistry.ClearAll();
            // Burn reasons re-attach from the side-car per Thing (BurnReasonSaveLoadPatches); clear
            // the in-memory reference mirror so the previous world's entries do not leak across.
            BurnReasonRegistry.ClearAll();
            // In-flight cable-burn split tracking is per-world transient state; a hot-swapped save
            // starts with no pending splits.
            SplitPendingRegistry.ClearAll();
            // The §5.7 generator-overflow running-average windows are per-world transient state too.
            CableBurnWindow.ClearAll();
            // Reset the "already named this broken device in-game" set so a hot-swapped world re-reports.
            DeviceOutputSanitizer.ClearReported();
            // The dead-input "no upstream supply" cue is recomputed every tick; clear the client mirror on load.
            DeadInputRegistry.ClearAll();
            // Re-arm the one-shot unmodelled-bridge census so the freshly loaded world's device set
            // is re-surveyed on its first atomic tick (fresh worlds are covered by the armed-at-load
            // default; this hook covers hot-swapped saves).
            UnknownBridgeCensus.Arm();
            // The electricity-tick counter is relative (lockout = currentTick + 120); clearing the
            // registries is sufficient, no counter reset needed.
        }
    }
}
