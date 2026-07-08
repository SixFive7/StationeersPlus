using System.Globalization;

namespace PowerGridPlus
{
    /// <summary>
    ///     Save/load one-shot self-check: on the first atomic tick after a world load (armed at
    ///     plugin load, re-armed by FaultRegistryLoadPatches on every load), verify the load path
    ///     left the mod's state machine in its contract shape, before PROTECT has had a chance to
    ///     write anything new. Three assertions:
    ///
    ///     <list type="bullet">
    ///       <item><b>Fault registries empty.</b> Faults are transient-by-spec (not serialized;
    ///       FaultRegistryLoadPatches wipes all four registries at LoadWorld), so any entry here
    ///       means an out-of-band writer ran between the load wipe and the first tick.</item>
    ///       <item><b>Priority sidecar restored == loaded.</b> Every entry the sidecar loaded
    ///       (PrioritySideCar.LoadedPriorities) must have been consumed by a matching
    ///       Thing.OnFinishedLoad restore. A shortfall usually means a sidecar entry for a
    ///       transformer no longer in the save (benign staleness, e.g. deconstructed while the
    ///       mod was uninstalled) but can also be a broken restore hook; either way the developer
    ///       should see it once.</item>
    ///       <item><b>Ledger sweep ran.</b> The world-load _powerProvided sweep
    ///       (LedgerAdoption.RunSweepIfPending) must have fired before this check, or stale saved
    ///       ledgers can bill through the session (the -176k free-energy class).</item>
    ///     </list>
    ///
    ///     <para>Exactly one Info line on pass; one Warning line carrying every failed clause on
    ///     mismatch. Runs on the power worker at the top of the atomic tick, after the ledger
    ///     sweep and before OBSERVE/PROTECT. The ScenarioRunner rearch suite reads
    ///     <see cref="Ran"/> / <see cref="Passed"/> across its window; keep the member names
    ///     stable.</para>
    /// </summary>
    internal static class SaveLoadSelfCheck
    {
        private static bool _pending = true;   // armed at plugin load; re-armed on world load
        private static int _prioritiesRestored;

        // Reflection surface for the rearch suite.
        internal static bool Ran { get; private set; }
        internal static bool Passed { get; private set; }
        internal static int PriorityLoaded { get; private set; }
        internal static int PriorityRestored { get; private set; }

        /// <summary>Re-arm for the next world (FaultRegistryLoadPatches; also resets the counters).</summary>
        internal static void Arm()
        {
            _pending = true;
            _prioritiesRestored = 0;
            Ran = false;
            Passed = false;
            PriorityLoaded = 0;
            PriorityRestored = 0;
        }

        /// <summary>One priority sidecar entry landed in PriorityStore (ThingOnFinishedLoadPriorityPatch).</summary>
        internal static void NotePriorityRestored() => _prioritiesRestored++;

        /// <summary>
        ///     Run the one-shot check if armed; a single flag comparison otherwise. Called at the
        ///     top of the atomic tick, after LedgerAdoption.RunSweepIfPending and before
        ///     OBSERVE/PROTECT (so the registries are still untouched by this tick's detectors).
        /// </summary>
        internal static void RunIfPending(int currentTick)
        {
            if (!_pending) return;
            _pending = false;
            Ran = true;

            int faultEntries = BrownoutRegistry.LockoutCount
                               + OverloadRegistry.LockoutCount
                               + CycleFaultRegistry.LockoutCount
                               + VariableVoltageFaultRegistry.LockoutCount;
            PriorityLoaded = PrioritySideCar.LoadedPriorities?.Count ?? 0;
            PriorityRestored = _prioritiesRestored;
            bool sweepRan = LedgerAdoption.SweepHasRun;

            bool registriesClean = faultEntries == 0;
            bool priorityMatch = PriorityRestored == PriorityLoaded;
            Passed = registriesClean && priorityMatch && sweepRan;

            if (Passed)
            {
                Plugin.Log?.LogInfo(
                    "[PowerGridPlus] Save/load self-check: OK (fault registries empty, priority sidecar "
                    + PriorityRestored.ToString(CultureInfo.InvariantCulture) + "/"
                    + PriorityLoaded.ToString(CultureInfo.InvariantCulture)
                    + " restored, ledger sweep ran) at tick "
                    + currentTick.ToString(CultureInfo.InvariantCulture) + ".");
            }
            else
            {
                Plugin.Log?.LogWarning(
                    "[PowerGridPlus] Save/load self-check: MISMATCH at tick "
                    + currentTick.ToString(CultureInfo.InvariantCulture)
                    + " (fault registry entries: " + faultEntries.ToString(CultureInfo.InvariantCulture)
                    + ", expected 0; priority sidecar restored "
                    + PriorityRestored.ToString(CultureInfo.InvariantCulture)
                    + " of " + PriorityLoaded.ToString(CultureInfo.InvariantCulture)
                    + " loaded" + (priorityMatch ? "" : ", expected equal; a shortfall usually means a"
                    + " sidecar entry for a transformer no longer in the save")
                    + "; ledger sweep ran: " + (sweepRan ? "yes" : "NO")
                    + "). The load path left unexpected state; please report it.");
            }
        }
    }
}
