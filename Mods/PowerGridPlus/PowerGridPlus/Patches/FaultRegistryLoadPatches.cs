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
            // Stage 3 ledger adoption: re-arm the world-load _powerProvided sweep (zeroes every
            // modeled segmenter's saved ledger on the first atomic tick, killing stale credits),
            // and drop the previous world's Powered-presentation snapshots so no stale device
            // references or health verdicts leak across a hot-swap. The first atomic tick
            // republishes both.
            LedgerAdoption.Arm();
            PoweredPresentation.Clear();
            // The per-net shortfall classification snapshot is per-world diagnostics; drop it so
            // the census never joins a stale world's net ids. The first atomic tick republishes.
            ShortfallDiagnostics.Clear();
            // The partial-power sentinel's counters and throttle are per-world diagnostics too.
            PartialPowerSentinel.Clear();
            // The per-tick solar / wind first-read latches and the deferred emergency-light
            // toggles hold the previous world's ReferenceIds / Thing references; drop them on a
            // hot-swap.
            SolarOutputLatchPatches.Clear();
            WindTurbineOutputLatchPatches.Clear();
            ConsumerDemandLatchPatches.Clear();
            EmergencyLightToggleQueue.Clear();
            // The charge / discharge delivery audits' grants / credits / drains / counters and
            // the per-tick delivery-shaping allowances are per-world state too.
            ChargeDeliveryAudit.Clear();
            DischargeDeliveryAudit.Clear();
            DeliveryTickLedger.Clear();
            // The tick-duration watchdog re-warms its median ring against the new world; the
            // Powered-set conformance tracking and the registry hygiene cadence restart; the
            // save/load self-check re-arms for its one-shot on the first atomic tick.
            TickDurationWatchdog.Clear();
            PoweredSetConformance.Clear();
            RegistryHygiene.Clear();
            SaveLoadSelfCheck.Arm();
            // The electricity-tick counter is relative (lockout = currentTick + 120); clearing the
            // registries is sufficient, no counter reset needed.
        }
    }
}
