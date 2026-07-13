using System;
using Assets.Scripts;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using PowerGridPlus.Core;

namespace PowerGridPlus.Patches
{
    // The atomic electricity tick, B + D1 data-plane edition (POWER.md section 0 decisions 24-26):
    // one prefix replaces ElectricityManager.ElectricityTick outright, and vanilla's per-network
    // PowerTick trio (Initialise / CalculateState / ApplyState) is NEVER called. The pipeline:
    //
    //   0. HOUSEKEEPING   load-boundary drain (world-load clears run HERE on the worker, never on
    //                     the load path racing an in-flight tick), debit-queue reconcile, censuses,
    //                     ledger sweeps, registry hygiene, the emergency-light toggle drain.
    //   1. SNAPSHOT       GridSnapshot.Build: topology (lock(DeviceList) + the power-port
    //                     predicate; the PowerDeviceList lazy getter is never touched) plus the
    //                     single boundary read of every device's demand / output / control.
    //   2. PROTECT        tier enforcement, cycle detection, producer isolation, the OFF-as-reset
    //                     sweep; all fed from the snapshot. Newly VVF-locked producers are zeroed
    //                     IN the snapshot (no second observation pass).
    //   3. ALLOCATE       PowerAllocator.RunAtomic consumes the snapshot, converges, publishes the
    //                     presentation caches and the write-back plan.
    //   4. WRITE-BACK     WriteBack.Run applies the plan: net HUD/MP/logic fields, fuse protection,
    //                     the deterministic generator-overflow burn, storage settlement (credits =
    //                     grants by construction), and the consumer accumulator drains (decision 26:
    //                     main-thread queue + worker-direct, dead nets freeze).
    //   5. TAIL           Powered ownership sweep + presentation reconcile + ledger settle + the
    //                     conformance / delivery audits.
    //   6. DEVICE TICK    per-device IPowered.OnPowerTick (vanilla copy; third-party device-tick
    //                     patches still fire).
    //   7. LOGIC          CircuitHolders.Execute (vanilla copy).
    //
    // Threading: vanilla calls ElectricityTick on the UniTask ThreadPool worker; the prefix runs on
    // the same worker. The only main-thread crossings are the self-marshaling vanilla calls
    // (SetPowerFromThread, Cable/CableFuse.Break) and the batched accumulator-debit post.
    [HarmonyPatch(typeof(ElectricityManager), nameof(ElectricityManager.ElectricityTick))]
    public static class AtomicElectricityTickPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            // Clients and paused sims run nothing, mirroring vanilla's RunSimulation gate; a client
            // receives net loads and fault snapshots over the wire.
            if (!GameManager.RunSimulation) return false;

            long tickStartTs = System.Diagnostics.Stopwatch.GetTimestamp();
            long allocMicros = 0;

            try
            {
                ElectricityTickCounter.Advance();
                int currentTick = ElectricityTickCounter.CurrentTick;

                // ---- 0. HOUSEKEEPING ----
                LoadBoundary.DrainPending();
                MainThreadDebitQueue.Reconcile();
                UnknownBridgeCensus.RunIfPending();
                ReceivePowerOverrideCensus.RunIfPending();
                LedgerAdoption.RunSweepIfPending();
                LedgerAdoption.AuditTickBoundary(currentTick);
                SaveLoadSelfCheck.RunIfPending(currentTick);
                RegistryHygiene.MaybeRun(currentTick);
                EmergencyLightToggleQueue.Drain();

                // ---- 1. SNAPSHOT (topology + the single boundary read) ----
                var snap = GridSnapshot.Build(currentTick);

                // ---- 2. PROTECT ----
                OffAsResetSweep.Run(currentTick);
                VoltageTierEnforcer.Run(snap);
                var cycleFaulted = CycleGraphBuilder.FindCycleFaultedSegmenters(snap);
                foreach (long refId in cycleFaulted)
                    CycleFaultRegistry.NoteCycleFault(refId, currentTick);
                int newVvf = VariableVoltageFaultDetector.Run(currentTick, snap);
                if (newVvf > 0 || cycleFaulted.Count > 0)
                {
                    // Newly locked producers stop supplying THIS tick: zero their table rows in
                    // place (the old OBSERVE / re-observe pair collapsed into this). Newly locked
                    // segmenters are consumed by GATHER through the registries directly.
                    snap.ZeroFaultedProducers(currentTick);
                }

                // ---- 3. ALLOCATE ----
                long allocStartTs = System.Diagnostics.Stopwatch.GetTimestamp();
                PowerAllocator.RunAtomic(currentTick);
                allocMicros = TickDurationWatchdog.TimestampDeltaToMicros(allocStartTs,
                    System.Diagnostics.Stopwatch.GetTimestamp());
                PowerAllocator.SyncFaultSnapshots(currentTick);

                // ---- 4. WRITE-BACK ----
                WriteBack.Run(currentTick, snap);

                // ---- 5. TAIL ----
                PoweredPresentation.ReconcileEnforceTail();
                PoweredOwnership.SweepEnforceTail(currentTick, snap);
                LedgerAdoption.SettleEnforceTail(currentTick);
                PoweredSetConformance.RunEnforceTail(currentTick);
                ChargeDeliveryAudit.RunEnforceTail(currentTick);
                DischargeDeliveryAudit.RunEnforceTail(currentTick);

                // ---- 6. DEVICE TICK (vanilla copy) ----
                ElectricityManager.AllPoweredThings.ForEach(p => p?.OnPowerTick());

                // ---- 7. LOGIC (vanilla copy) ----
                CircuitHolders.Execute();

                TickDurationWatchdog.RecordTick(currentTick,
                    TickDurationWatchdog.TimestampDeltaToMicros(tickStartTs,
                        System.Diagnostics.Stopwatch.GetTimestamp()),
                    allocMicros);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError(
                    $"[PowerGridPlus] Atomic electricity tick threw: {ex.Message}\n{ex.StackTrace}");
            }
            return false;   // vanilla ElectricityTick never runs
        }
    }
}
