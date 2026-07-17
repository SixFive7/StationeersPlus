using System.Threading;

namespace PowerGridPlus.Core
{
    /// <summary>
    ///     Arm-and-drain world-load reset (the round-3 tier-1 fix, POWER.md §0 decision 24 stage 0).
    ///     <c>XmlSaveLoad.LoadWorld</c> is async: its postfix fires at load START on the main thread
    ///     while a previous world's tick can still be in flight on the worker, so clearing the
    ///     tick-owned plain containers there could corrupt them or let an in-flight publish restore
    ///     the OLD world's state after the clear. Instead the load patch only ARMS this boundary;
    ///     the clears run at the top of the next atomic tick, on the worker, strictly before any
    ///     tick work touches the containers.
    ///
    ///     <para>Load-time restores that must NOT be deferred (the burn-reason sidecar repopulates
    ///     during the load itself) stay in the load patch; see FaultRegistryLoadPatches.</para>
    /// </summary>
    internal static class LoadBoundary
    {
        private static int _armed;

        internal static void Arm()
        {
            Interlocked.Exchange(ref _armed, 1);
        }

        /// <summary>Worker, top of the atomic tick: run the deferred clears exactly once per load.</summary>
        internal static void DrainPending()
        {
            if (Interlocked.CompareExchange(ref _armed, 0, 1) != 1) return;

            // Fault lockouts are transient by design; they recompute from live topology.
            DeprioritizedRegistry.ClearAll();
            OverloadRegistry.ClearAll();
            CableOverloadRegistry.ClearAll();
            CycleFaultRegistry.ClearAll();
            CurrentMismatchFaultRegistry.ClearAll();
            DeadInputRegistry.ClearAll();
            UndersuppliedRegistry.ClearAll();

            // Burn/split machinery (the burn-reason registry itself is cleared IMMEDIATELY at load,
            // before the sidecar restore repopulates it; see FaultRegistryLoadPatches).
            SplitPendingRegistry.ClearAll();
            CableBurnWindow.ClearAll();
            DeviceOutputSanitizer.ClearReported();

            // Per-tick published snapshots and the ownership model.
            PoweredPresentation.Clear();
            ShortfallDiagnostics.Clear();
            NetLiveness.Clear();
            SegControlSnapshot.Clear();
            PoweredOwnership.Clear();

            // Audits, watchdog, hygiene.
            ChargeDeliveryAudit.Clear();
            DischargeDeliveryAudit.Clear();
            TickDurationWatchdog.Clear();
            PoweredSetConformance.Clear();
            RegistryHygiene.Clear();

            // The B + D1 data plane.
            GridSnapshot.Clear();
            WriteBack.Clear();
            MainThreadDebitQueue.Clear();

            // Deferred control writes from the previous world.
            Patches.EmergencyLightToggleQueue.Clear();
        }
    }
}
