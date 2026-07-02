using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    // Atomic electricity tick: takes over ElectricityManager.ElectricityTick and
    // splits the per-network Initialise+CalculateState+ApplyState into two outer
    // passes, with our global shed+overload allocator between them. This gives
    // the allocator fresh in-tick supply/demand data (OBSERVE's CalculateState
    // populates PowerTick.Required/.Potential per network from this tick's
    // GetUsedPower/GetGeneratedPower calls) and lets shed/overload decisions
    // take effect in the SAME tick (ENFORCE's re-CalculateState reads the
    // flags via our patched GetGeneratedPower/GetUsedPower returning 0 for
    // locked-out transformers).
    //
    // The phase names below are PowerGridPlus's own atomic-tick phases (POWER.md
    // section 2 is the source of truth). SETUP/OBSERVE is the combined
    // Initialise + CalculateState step: the per-network reset/gather walk that
    // populates fresh this-tick supply/demand state.
    //
    // Compared to vanilla's flow (per network: Init -> Calc -> Apply, all
    // atomic per network):
    //   - vanilla does roughly:   for each net: Init+Calc+Apply, then per-device
    //                             OnPowerTick, then CircuitHolders.Execute.
    //   - atomic flow does:       for each net: Init+Calc  (OBSERVE)
    //                             global allocator           (ALLOCATE)
    //                             for each net: Init+Calc+Apply  (ENFORCE)
    //                             per-device OnPowerTick (DEVICE TICK, vanilla copy)
    //                             CircuitHolders.Execute (LOGIC TICK, vanilla copy)
    //
    // Cost: one extra Initialise+CalculateState pass per network per tick. For
    // a populated Lunar save (~209 networks at 2 Hz) this is well under one ms.
    //
    // Vanilla compatibility:
    //   - All per-device IPowered.OnPowerTick patches (BatteryLight,
    //     ScriptedScreens, HaulerMod) still fire in DEVICE TICK.
    //   - CableNetwork.OnPowerTick is NOT called -- its body is inlined into
    //     OBSERVE + ENFORCE + the trailing field copies. No mod in the surveyed set
    //     patches CableNetwork.OnPowerTick (see ENFORCE comment for the audit).
    //     Re-Volt is the only known mod that wraps the power tick at this level,
    //     and PowerGridPlus refuses to load when Re-Volt is detected (Plugin.cs
    //     TryFindIncompatibleMod).
    //
    // Threading: vanilla calls ElectricityTick on the UniTask ThreadPool worker
    // ("Electronics THread Exception" message in vanilla's catch block confirms).
    // Our prefix runs on the same worker. Managed-memory only; no Unity API
    // calls in the loops.
    [HarmonyPatch(typeof(ElectricityManager), nameof(ElectricityManager.ElectricityTick))]
    public static class AtomicElectricityTickPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (!GameManager.RunSimulation) return false;
            try
            {
                // Tick counter: shared with BrownoutRegistry / OverloadRegistry
                // lockout-expiry math. Advance once per tick, at the very
                // start, so every read inside this flow sees the same value.
                ElectricityTickCounter.Advance();
                int currentTick = ElectricityTickCounter.CurrentTick;

                // One-shot unmodelled-bridge census (Stage 2b unknown-bridge lane): on the first
                // atomic tick after a world load, log every ElectricalInputOutput subclass in the
                // scene that no seg adapter models and the segmenter roster does not know (one line
                // per type). Those devices stay on vanilla behaviour inside OBSERVE/ENFORCE, the
                // conservative fallback; the census only makes the gap visible. A single flag check
                // on every later tick.
                UnknownBridgeCensus.RunIfPending();

                // ----------------------------------------------------------------
                // OBSERVE (SETUP/OBSERVE). Initialise + CalculateState per network
                // with CURRENT state. Populates PowerTick.Required / .Potential /
                // .Providers from fresh this-tick GetUsedPower / GetGeneratedPower
                // calls. Our patched GetGeneratedPower / GetUsedPower run here;
                // for transformers locked-out from a PREVIOUS tick they return 0,
                // which is correct (their contribution to fresh state should be
                // 0). For transformers not yet in any lockout, they return the
                // normal vanilla-faithful values.
                //
                // We also clear BreakableFuses / BreakableCables here. Vanilla
                // never clears them across ticks (Pick() picks a random index
                // without removing), so they accumulate across our extra OBSERVE
                // walk. Clearing once per tick keeps the cable-burn check
                // grounded in the current tick's state and incidentally fixes
                // the vanilla accumulation drift.
                // ----------------------------------------------------------------
                CableNetwork.AllCableNetworks.ForEach(net =>
                {
                    if (net == null) return;
                    var pt = net.PowerTick;
                    if (pt == null) return;
                    pt.BreakableFuses.Clear();
                    pt.BreakableCables.Clear();
                    if (net.DeviceList.Count == 0) return;
                    pt.Initialise(net);
                    pt.CalculateState();
                });

                // Server-side OFF-as-reset: clear every lockout on devices the player has switched
                // off (POWER.md §10.3). Runs before fault detection so a toggled-off device is
                // clean when the detectors re-evaluate.
                OffAsResetSweep.Run(currentTick);

                // ----------------------------------------------------------------
                // PROTECT (wrong-tier burn): wrong-tier cable burns (POWER.md §4.3 order:
                // tier burns run BEFORE cycle detection so the cycle walk never wastes work
                // on a junction that is about to vanish). Burn requests marshal to the main
                // thread, so the actual split lands after this tick; the next tick's
                // OBSERVE observes the post-burn topology.
                // ----------------------------------------------------------------
                VoltageTierEnforcer.Run();

                // ----------------------------------------------------------------
                // PROTECT (cycle detection): pre-allocator CYCLE_FAULT detection. PowerGridPlus's
                // own directed-SCC graph over the segmenting devices (CycleGraphBuilder)
                // finds every powered closed power loop and faults every segmenter on
                // it for 60 s. Each faulted device then contributes 0 on both terminals
                // (CycleFaultEnforcementPatches), so the loop dissolves before the
                // allocator runs and ALLOCATE never sees the cycle's inflated
                // Potential / Required (POWER.md §4.3). No cable is burned for cycles.
                // ----------------------------------------------------------------
                var cycleFaulted = CycleGraphBuilder.FindCycleFaultedSegmenters();
                foreach (long refId in cycleFaulted)
                    CycleFaultRegistry.NoteCycleFault(refId, currentTick);

                // PROTECT (producer-isolation): A power producer wired to a rigid consumer
                // with no transformer between them enters VARIABLE_VOLTAGE_FAULT and stops generating.
                // Always-on (no toggle), per POWER.md and the developer's decision.
                int newVvf = VariableVoltageFaultDetector.Run(currentTick);

                // Re-observe once if anything was newly faulted this tick, so ALLOCATE sees the dissolved
                // loop / silenced producer (devices faulted on a PRIOR tick already read 0 in OBSERVE via
                // the enforcement postfixes -- CycleFaultEnforcementPatches for segmenters,
                // ProducerFaultEnforcementPatches for producers -- so only NEW faults need it).
                if (cycleFaulted.Count > 0 || newVvf > 0)
                {
                    CableNetwork.AllCableNetworks.ForEach(net =>
                    {
                        if (net == null) return;
                        var pt = net.PowerTick;
                        if (pt == null || net.DeviceList.Count == 0) return;
                        pt.Initialise(net);
                        pt.CalculateState();
                    });
                }

                // ----------------------------------------------------------------
                // ALLOCATE. Global atomic shed + overload allocator
                // reads every network's freshly-populated PowerTick.Required /
                // .Potential, decides which transformers shed (input shortfall)
                // and which overload (downstream demand > capacity), and writes
                // the lockout flags to BrownoutRegistry / OverloadRegistry.
                // ----------------------------------------------------------------
                PowerAllocator.RunAtomic(currentTick);
                // Per-tick full fault-registry snapshots to clients (all four registries,
                // POWER.md §13 heartbeat model).
                PowerAllocator.SyncFaultSnapshots(currentTick);

                // ----------------------------------------------------------------
                // ENFORCE. Re-Initialise + CalculateState + ApplyState
                // per network. The second CalculateState's GetUsedPower /
                // GetGeneratedPower see the freshly-set lockout flags via our
                // patches and return 0 for transformers newly locked out this
                // tick. Vanilla ApplyState then computes _powerRatio, breaks
                // overloaded fuses / cables, distributes power. Trailing field
                // copies mirror vanilla CableNetwork.OnPowerTick L253670-L253680.
                // ----------------------------------------------------------------
                // ENFORCE iterates networks UPSTREAM-FIRST (shallow depth first), the order the
                // allocator's DEPTH phase just computed. Processing a transformer's input network
                // before its output network means the output network's CalculateState reads an
                // InputNetwork.PotentialLoad that was already refreshed THIS tick (the write at the
                // end of this body), so multi-stage transformer chains see current supply instead of
                // last tick's. Without the ordering a downstream network read the prior tick's
                // upstream PotentialLoad, so a chain under variable load oscillated power on/off every
                // tick (the net-492209 regression). Order is a hint for correctness of the lag fix,
                // not of the per-network math: each network is still enforced exactly once, and a
                // trailing sweep covers any network the allocator's roster did not include.
                void EnforceNet(CableNetwork net)
                {
                    if (net == null) return;
                    var pt = net.PowerTick;
                    if (pt == null) return;
                    if (net.DeviceList.Count == 0) return;
                    pt.Initialise(net);
                    pt.CalculateState();
                    pt.ApplyState();
                    net.DuringTickLoad = 0f;
                    net.RequiredLoad = pt.Required;
                    net.CurrentLoad = pt.Consumed;
                    net.PotentialLoad = pt.Potential;
                    net.ShortfallLoad = pt.Required > pt.Potential
                        ? pt.Required - pt.Potential : 0f;
                }

                var enforced = new HashSet<long>();
                var ordered = PowerAllocator.ShallowFirstNetworks;
                if (ordered != null)
                {
                    for (int i = 0; i < ordered.Count; i++)
                    {
                        var net = ordered[i];
                        if (net == null || !enforced.Add(net.ReferenceId)) continue;
                        EnforceNet(net);
                    }
                }
                CableNetwork.AllCableNetworks.ForEach(net =>
                {
                    if (net == null || !enforced.Add(net.ReferenceId)) return;
                    EnforceNet(net);
                });

                // ----------------------------------------------------------------
                // DEVICE TICK: per-device IPowered.OnPowerTick. Vanilla copy.
                // Covers Battery state updates, generator fuel consumption,
                // and every IPowered patch in the mod ecosystem (BatteryLight,
                // ScriptedScreens, HaulerMod, etc).
                // ----------------------------------------------------------------
                ElectricityManager.AllPoweredThings.ForEach(p => p?.OnPowerTick());

                // ----------------------------------------------------------------
                // LOGIC TICK: CircuitHolders.Execute. Vanilla copy. Runs IC10
                // chips on the standard schedule.
                // ----------------------------------------------------------------
                CircuitHolders.Execute();
            }
            catch (Exception ex)
            {
                // Mirror vanilla's catch-and-log shape so any other layer that
                // expects a non-throwing ElectricityTick sees the same surface.
                UnityEngine.Debug.LogError($"[PowerGridPlus] Atomic electricity tick threw: {ex.Message}\n{ex.StackTrace}");
            }
            return false;     // skip vanilla
        }
    }
}
