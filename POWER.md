# Power Grid Plus: Power System Specification

Authoritative spec for the post-refactor power simulation. Reads as a reference, not a tutorial. Defines invariants, algorithms, and behaviour rules in enough detail to implement against without consulting the conversation that produced it.

## 0. Resolved decisions (dated override blocks, newest first; these override any contradicting text below)

Decision numbers are global across the dated blocks (source comments cite them as "decision N" / "§0.N"), so a newer block continues the numbering of the older one instead of restarting at 1.

### Resolved decisions (2026-07-02): the unified-flow rearchitecture

Shipped in commits e9ef3c1f (Stage 1: unified flow classes + complete segment presentation), 2a00d674 (Stage 2b: segment adapter contract + PowerTransmitterPlus ModApi handshake), and 31017f38 (Stage 3: Powered presentation + ledger adoption + diagnostics). The affected body sections (§2, §5.0.2-§5.0.3, §6.3, §6.6, §7.2-§7.6, §8.0.3-§8.0.4, §8.2-§8.4.2, §8.8, §9, §10.6-§10.7, §12, §14.2, §16, §17, §18, §19) are rewritten or extended to match; where any remaining older text contradicts these decisions, the decisions win.

9. **One demand vector, two riding flow classes; the surplus walk is deleted.** Storage charge (battery, APC cell, umbilical cell) is the SOFT class riding the SAME `BackwardDesirePass` / `ForwardSupplyAndShed` sweep as rigid demand, as a (rigid, soft) demand vector: the same priority-tier-first, proportional-within-a-tier splitter, with each contributor's soft capacity capped at `EffCap - rigid desired throughput`, granted forward out of the firm residual only, quiescent draw carried exactly once (on the rigid pull when any rigid flows, else on the soft pull). The separate single-pass surplus walk, `GrantThrough`, `SoftReqTotal`, and `Net.ElasticDelivered` are deleted (§9).
10. **Faults are rigid-only.** Shed, structural overload, supply overload, cable overflow, and the dead-input cue all evaluate the rigid component only. Unmet soft desire clamps silently: never a shed, never an overload, never a lockout, never a hover cue (§9.6).
11. **Complete per-segment presentation totals.** The allocator publish tail writes `TransformerSupplyCache.Set(RefId, TotalThrough, TotalPull)` for EVERY routed seg kind (Transformer, wireless PT/PR pair, APC), where `TotalThrough = rigid + soft output delivery` and `TotalPull = TotalThrough * max(m, 1) + quiescent` whenever the seg conducts, so a granted soft flow always has a carrier on both terminals of its segment (§8.0.4). Network mirrors, hover tooltips, and IC10 throughput reads include storage-charge flow.
12. **Segment adapter contract.** GATHER consults one `ISegAdapter` per modelled bridge class (`SegAdapters.cs`): Routed (Transformer, linked wireless pair, APC) versus Buffered (rocket umbilical, formalized store-and-forward; the 0.2.6403 female half transfers bidirectionally in vanilla phase 2). Unknown `ElectricalInputOutput` subclasses stay on vanilla behaviour and are reported once per type at world load (§5.0.3, §8.8).
13. **Three-tier PowerTransmitterPlus interop with a billing-ownership handshake.** ModApi v1 preferred (`EffectiveMaxCapacity` / `TryGetLink` / `SourceDrawMultiplier` / debt accessors + `ClaimBillingOwnership("net.powergridplus")`; PowerTransmitterPlus's native wireless debt billing stands down while the claim is held), legacy 1.8.0 reflection chain second, vanilla-absent fallback third (§6.6). The PT-pair seg is sized by a STATIC link rating, never the live `InputNetwork.PotentialLoad` (§6.3).
14. **Powered presentation policy.** A healthy routed segmenter (enrolled, unfaulted, conducting or idle on a supplied input with no unmet rigid demand) presents `Powered = True`: `AllowSetPower` postfixes block vanilla's false edge, the ENFORCE tail raises the true edge (one frame of marshal lag). Dark-input and faulted segmenters keep vanilla behaviour (§10.6).
15. **Ledger adoption.** PowerGridPlus owns the `_powerProvided` ledgers of the routed segmenters it bills for: a world-load sweep zeroes them (both signs, NaN included), and a per-tick ENFORCE-tail settle sets Transformer := `TotalThrough` (so `PowerActual` reads true throughput), transmitter := 0, receiver := `min(debt, TotalThrough)`. The APC is exempt per tick (its positive ledger is vanilla's deferred cell discharge). Ownership is verified by an exact, always-on ledger audit (tick-boundary identity plus a bracketed shadow sum over every vanilla write site, with a non-finite backstop) instead of a threshold detector (§10.7).
16. **Machine-checked diagnostics.** `ConservationChecker` (per-net inflow == outflow within 0.5 W + the per-seg pull invariant, `Server - Diagnostics` / `Enable Conservation Check` default true, 600-tick warning throttle), `ShortfallDiagnostics` (per-net Served / Dry / Throttled / Deadlock classification; Deadlock is the regression signal, expected 0), and `UnknownBridgeCensus` (one-shot log of unmodelled bridge types left on vanilla) (§8.8, §17.43, §17.44).

### Resolved decisions (2026-06-10)

This spec was partly implemented (pass 1, 2026-06-09/10) and then reviewed with the developer, who locked the decisions below. Where the body of this document contradicts one of these (the spec has a couple of internal contradictions and one wrong vanilla value), **these decisions win**. The implementation checklist and current status live in `POWERTODO.md` ("2026-06-10 decisions and current status") and `POWER_DEVIATIONS.md` (file-by-file done/remaining). The standing instruction is to implement EVERYTHING (no deferring), building and testing after each step.

1. **Single inner-tick architecture.** The SETUP / OBSERVE and ENFORCE phases run the VANILLA `PowerTick` (Initialise / CalculateState / ApplyState), not a custom `PowerGridTick` subclass. All PowerGridPlus behaviour comes from device-method postfixes and the atomic PROTECT phase. `PowerGridTick.cs` is removed; its former jobs (wrong-tier burn -> PROTECT, generator-overflow burn §5.7, the vanilla recursive belt-and-braces) are relocated.
2. **Cable caps are non-mutating.** `Cable.MaxVoltage` (a per-instance, save-serialized field; misleadingly named, it is a Watts cap) is NOT rewritten. The configured per-tier caps come from a helper that every cap-reader consults, including vanilla's own cable-burn check (patched). Defaults: normal **5000**, heavy **100000** (true vanilla; §5.6's "50000" is wrong), super-heavy **0 = unlimited**; all three configurable. The README states caps are runtime-enforced, not baked into the save.
3. **Failure colour is distinct (resolves the §11 / open-questions contradiction).** SHED = orange `#ffa500`; OVERLOAD / CYCLE_FAULT / VARIABLE_VOLTAGE_FAULT = red `#ff2626`. Highest-precedence active fault picks the colour: CYCLE > VVF > OVERLOAD > SHED.
4. **Cycle detection is a DIRECTED-SCC walk (replaces §4.2.5's undirected bipartite DFS).** Nodes = cable networks; each segmenter contributes one directed edge InputNetwork -> OutputNetwork; a cycle = a strongly-connected component of size >= 2 (Tarjan). Edges gated on OnOff + both-networks-non-null + Input != Output; wireless PT/PR via the shared `WirelessNetwork` node. The undirected model false-positives on parallel same-direction transformers/batteries (normal redundancy); the directed model does not. Only powered SCCs fault (min(Potential,Required) > 0 on a member network).
5. **Producer-isolation (VVF) is ALWAYS-ON, fault+zero, with a cable-burn fallback for unknown producers.** Known producers (the classifier list) fault and stop generating (reversible; button-bearing ones flash red, solar/wind/RTG hover-only); no cable burn for known producers (resolves §1.6.5's internal contradiction). A producer-LIKE device NOT in the known list (new game version / modded) falls back to the original cable-burn handling so it is still caught. No enable/disable toggle.
6. **Fault visuals on every faultable device.** The flash + hover-countdown attach to every segmenter (Transformer, Battery, AreaPowerControl, PowerTransmitter, PowerReceiver, RocketPowerUmbilicalMale; RocketPowerUmbilicalFemale hover-only) and every button-bearing producer; solar/wind/RTG are hover-only.
7. **Emergency lights are a configurable prefab list.** `EnableEmergencyLights` applies to a configurable comma-separated list of light prefab names (default `StructureWallLightBattery`), not a single hardcoded prefab.
8. **One allocator, no toggle.** PowerGridPlus has exactly one power allocator (§8). An earlier development pass carried two DECIDE-phase strategies behind a runtime toggle (`EnableSweepAllocator`); the toggle, the alternate strategy, and the second code path are all removed. The surviving allocator is the topological-order, iterated fixed-point design described in §8. Throughout this spec "the allocator" means that single implementation; there is no longer a "Sweep" / "Legacy" distinction, no `SweepAllocatorSync`, and no allocator-selection setting. The per-feature master toggles `EnableTransformerShedding` and `EnableTransformerOverloadProtection` remain (they enable or disable the shed and overload passes), but they do not select between allocators.

## 1. Architectural overview

PowerGridPlus owns `ElectricityManager.ElectricityTick` via a Harmony prefix-return-false. The mod replaces vanilla's per-network iterate-then-flow with an atomic multi-phase flow that:

- Computes fresh per-network supply and demand BEFORE any actual power flow occurs (SETUP / OBSERVE).
- Detects structural faults (wrong-tier burn, cycle-fault, producer-isolation) before allocation (PROTECT).
- Runs a global allocator that decides every shed and every soft-demand share in a single deterministic pass (ALLOCATE).
- Re-runs vanilla power flow with the allocator's decisions already in effect (ENFORCE).
- Runs vanilla's per-device post-tick and IC10 phases (DEVICE TICK and LOGIC TICK).

The mod's invariants:

- Voltage tiers are always on. Cables of different tiers are mutually incompatible; transformers and Area Power Controllers (APCs) bridge between them.
- Recursive (cycling) cable networks are always faulted. PowerGridPlus's PROTECT phase detects every powered cycle and trips every segmenting device in it into CYCLE_FAULT for 60 s. No cables burn from cycles. Vanilla's `CheckForRecursiveProviders` and destructive cable burn still run unsuppressed as belt-and-braces; in normal operation it never fires because every cycle has already been broken by faulting its members.
- Re-Volt is incompatible and refused at load.
- The tick is atomic: every shed or soft-demand allocation that the allocator decides at tick N takes effect at tick N's downstream device flow. No one-tick latency. No oscillation.

## 2. The atomic electricity tick (phase names)

`AtomicElectricityTickPatch.Prefix` replaces vanilla `ElectricityTick`'s body. The phases below are the names used throughout this spec.

**Naming note (SETUP / OBSERVE).** The code performs the per-tick reset and the vanilla `PowerTick.Initialise + CalculateState` as ONE combined per-network pass (`AtomicElectricityTickPatch.cs`, a single `CableNetwork.AllCableNetworks.ForEach` that clears `BreakableCables` / `BreakableFuses`, then calls `Initialise` then `CalculateState`); the contributor / elastic / soft rosters are gathered inside the allocator's own GATHER step, not in a separate pre-pass. Splitting SETUP from OBSERVE as two distinct code phases would imply a split that does not exist. This spec therefore presents them as one combined **SETUP / OBSERVE** step that maps to vanilla `Initialise + CalculateState` plus the per-tick reset, and names the two halves only when distinguishing the per-tick reset (SETUP) from the supply/demand read (OBSERVE) clarifies a point.

1. **SETUP / OBSERVE.** Advance the shared tick counter (`ElectricityTickCounter.Advance`). Before the first network read, two one-shot load hooks fire if armed (armed at plugin load, re-armed on every world load): the unknown-bridge census (§8.8) and the world-load ledger sweep (§10.7), so a stale saved `_powerProvided` can never bill through the first tick. Then for each cable network: clear `BreakableCables` / `BreakableFuses` (vanilla never clears them across ticks; clearing once per tick keeps the cable-burn check grounded in the current tick and fixes the vanilla accumulation drift), then `PowerTick.Initialise(net)` then `PowerTick.CalculateState()`. This populates `PowerTick.Required` and `PowerTick.Potential` per network using the device-level `GetUsedPower` and `GetGeneratedPower` reads. Soft-demand devices' postfixes pass through with the raw values during this step (more below). `OffAsResetSweep.Run` also fires here, clearing every lockout on devices the player has switched off (§10.3), before the PROTECT detectors re-evaluate.

2. **PROTECT.** Structural-fault detection, before allocation, so the allocator never sees a fault's inflated `Potential` / `Required`. In order: wrong-tier cable burn (`VoltageTierEnforcer.Run`, §3 / §4.3), cycle-fault detection (`CycleGraphBuilder.FindCycleFaultedSegmenters` -> `CycleFaultRegistry`, the directed-SCC walk of §4.2.5), then producer-isolation / VARIABLE_VOLTAGE_FAULT (`VariableVoltageFaultDetector.Run`, §8.5). If anything was newly cycle-faulted or VVF-faulted this tick, OBSERVE is re-run once so ALLOCATE sees the dissolved loop / silenced producer (devices faulted on a PRIOR tick already read 0 via the enforcement postfixes).

3. **ALLOCATE.** `PowerAllocator.RunAtomic(currentTick)`, the allocator (§8). Reads every network's freshly populated `PowerTick.Required` / `Potential`, builds the topological order, iterates the fixed-point loop, and decides every shed, overload, elastic discharge share, and storage-charge (soft) grant. Outputs:
   - Set of segmenting devices freshly entering shed lockout (written to `BrownoutRegistry`).
   - Set freshly entering overload lockout (written to `OverloadRegistry`).
   - Per-elastic-supplier discharge share (written to `SoftSupplyShareCache`) and per-soft-demand-device charge share (written to `SoftDemandShareCache`).
   - Per-routed-contributor exact in-tick presentation totals, rigid + soft (`TransformerSupplyCache`, written for Transformer, wireless PT/PR pair, and APC alike, §8.0.4); per-APC fresh passthrough draw (`ApcPassthroughCache`) and cell-only discharge share (`ApcCellDischargeCache`).
   - The presentation snapshots: the healthy-segmenter set plus enrolled-seg roster (`PoweredPresentation`, §10.6) and the per-net shortfall classification (`ShortfallDiagnostics`, §8.8), each swapped in by volatile reference.
   - Per-tick full fault-registry snapshots broadcast to clients (one `FaultRegistrySnapshotMessage` per kind: shed / overload / cycle / variable-voltage / dead-input, §13), via `PowerAllocator.SyncFaultSnapshots`.

4. **ENFORCE.** For each cable network, in the shallow-first order the allocator published (`ShallowFirstNetworks`, §8): `Initialise + CalculateState + ApplyState`. The re-`CalculateState` reads the freshly set shed/overload flags via our `GetGeneratedPower` / `GetUsedPower` patches, returning 0 for locked-out devices, reads each pass-through device's exact in-tick draw from `TransformerSupplyCache` / `ApcPassthroughCache`, and reads soft-demand / elastic allocations via the `SoftDemandShareCache` / `SoftSupplyShareCache` postfixes. Vanilla `ApplyState` then runs unmodified. Trailing field copies mirror vanilla `CableNetwork.OnPowerTick`: `RequiredLoad`, `CurrentLoad`, `PotentialLoad`, `ShortfallLoad`. Iterating upstream-first (topological order) is what lets each network's `CalculateState` run after its feeders' `PotentialLoad` was refreshed this tick (§8); a trailing sweep covers any network the allocator roster did not include. After the last network's `ApplyState`, the ENFORCE TAIL runs, still on the power worker: `PoweredPresentation.ReconcileEnforceTail` re-asserts `Powered = True` on idle healthy segmenters (§10.6), then `LedgerAdoption.SettleEnforceTail` settles every enrolled segmenter's `_powerProvided` ledger (§10.7), so the DEVICE TICK and LOGIC TICK below read reconciled state.

5. **DEVICE TICK.** `ElectricityManager.AllPoweredThings.ForEach(p => p?.OnPowerTick())`. Vanilla copy. Every `IPowered.OnPowerTick` patch from other mods (BatteryLight, HaulerMod, ScriptedScreens) fires here as in vanilla.

6. **LOGIC TICK.** `CircuitHolders.Execute()`. Vanilla copy. IC10 chips tick.

Threading: vanilla schedules `ElectricityTick` on the UniTask ThreadPool worker. The atomic prefix inherits that thread. No Unity API calls inside SETUP / OBSERVE through ENFORCE; managed memory only.

> Source-comment note: the atomic-tick code (`AtomicElectricityTickPatch.cs` and the allocator) uses these phase names in its comments. The code still runs the work as the vanilla `PowerTick` passes: SETUP / OBSERVE is the combined `Initialise` + `CalculateState` read, ENFORCE is the re-`Initialise` + `CalculateState` + `ApplyState` pass.

## 3. Voltage tiers (always-on invariant)

Stationeers has three cable tiers: normal, heavy, super-heavy. PowerGridPlus enforces tier integrity:

- A cable network is uniform in tier. A cable of one tier connected to a network of another tier triggers the lower-tier cable to burn at the junction, splitting the network. Tier violations are detected two ways (§4.3): on the worker thread each tick (the PROTECT-phase wrong-tier scan, the backstop) and immediately on `CableNetwork.OnNetworkChanged` (a main-thread event fired by every membership mutation, so a junction is caught the instant it is built or loaded). The burn itself always executes on the main thread, so the split lands synchronously, before the next tick conducts across the junction. A network with a burn in flight is gated by `SplitPendingRegistry` until its split lands (detected by cable-count change), which replaces the old fixed burn cooldown with a state signal.
- Devices are tier-restricted: generators and stationary batteries on heavy; high-draw machines (Carbon Sequester, Furnace, Advanced Furnace, Arc Furnace, Centrifuge, Recycler, Ice Crusher, Hydraulic Pipe Bender, Deep Miner) may use heavy or normal; normal-cable devices include lights, IC10, sensors, doors, hydroponics, etc.; super-heavy is cables and transformers only.
- Transformers and APCs are tier-exempt. They are the bridges.

This invariant has no toggle. The `EnableVoltageTiers` setting is removed; the always-on rules apply unconditionally.

## 4. Recursive networks (cycle-fault invariant)

PowerGridPlus replaces vanilla's "burn one cable in the cycle" response with a new fault mode: every segmenting device that participates in a detected powered cycle enters CYCLE_FAULT, a third lockout state parallel to SHED and OVERLOAD. Detection runs in the PROTECT phase. The cycle dissolves immediately because every device in it contributes 0 to its output network for the 60-second lockout duration. No cables burn from cycles.

Rationale:
- Burning a cable is destructive (player loses material), often in a non-obvious spot, and breaks immersion if the loop happens to be unpowered or only briefly powered.
- A fault on every dual-terminal device in the loop is a clear, localised, reversible signal: each "device-in-loop" turns off (red flash, hover text), the loop is broken because each device contributes 0, and after 60 s the devices auto-retry. If the player has rewired correctly the fault clears; if not, the fault re-fires immediately.
- This unifies the response: the player learns "red flash on a dual-terminal device == this device is faulted; check hover for cause" (cause = overload OR cycle-fault).

### 4.1 What vanilla detects

The recursive check walks `ElectricalInputOutput.IsProviderToDevice` (a depth-first search through `InputNetwork.PowerTick.InputOutputDevices` chains), capped at 512 hops via `Device.MaxProviderRecursionIterations`. The check covers:

- Direct 2-node cycles: T1 input on N1 + output on N2, T2 input on N2 + output on N1.
- Arbitrary-length cycles through any combination of `ElectricalInputOutput` devices: transformers, APCs, stationary batteries.
- Cycles up to 512 distinct intermediate devices.

Detection action: one cable on the cycle (or one fuse, if any exist on the anchor's network) is added to the breakable set per detected anchor. ENFORCE's `ApplyState` runs `BreakSingleCable` / `BreakSingleFuse` same-tick, picking one entry randomly.

### 4.2 What vanilla misses

Two known gaps:

1. **Wireless cycles.** `PowerTransmitter` and `PowerReceiver` extend `WirelessPower : Device`, NOT `ElectricalInputOutput`. They never appear in any network's `InputOutputDevices` list and do not override `IsProviderToDevice`. A cycle that closes through a transmitter-receiver wireless hop is invisible to vanilla detection. With PowerGridPlus modelling PT/PR pairs as transformers (§6), wireless cycles must be detected by PowerGridPlus itself: a depth-first walk that follows `PowerTransmitter.LinkedReceiver` as an additional edge alongside `ElectricalInputOutput.InputNetwork`. When found, mark a cable on the cycle for burn the same way vanilla does. This is a PowerGridPlus extension.

2. **`_networkTraversalRecord` reuse bug.** Vanilla's `CheckForRecursiveProviders` calls `_networkTraversalRecord.Clear()` ONCE at the start, then iterates anchors WITHOUT clearing between them. The visited set carries over between anchor iterations, so an anchor whose ReferenceId was added during an earlier anchor's walk gets pruned immediately on its own first call. Concrete failure: two anchors A1 and A2 on the same network, A2 in A1's walk path. A1's walk does not close a cycle and returns false. A2 starts its own walk, finds itself already in the visited set, returns false immediately, missing any A2-anchored cycle that runs through a chain disjoint from A1's. Fix: clear `_networkTraversalRecord` inside the foreach, before each anchor. PowerGridPlus applies this fix via Harmony (transpiler or full-body prefix). Cost: one List clear per anchor, anchor counts are small (under 10 typical per network).

3. **Multi-hop loops with no provider anchor.** Vanilla `CheckForRecursiveProviders` walks outward only from a network's existing `Providers` list (`InputOutputDevices` entries that already report supplying power on the network). A cycle that closes through a chain where no participant currently reports as a provider is invisible: battery-only loops (every battery's `_powerProvided` is 0 because no downstream device has pulled yet), APC-only loops, self-sustaining mutual-feed loops where every network's `Required` is 0 because the loop is feeding itself. Vanilla anchors on the wrong vertex or finds no anchor at all; it never starts the walk.

4. **Single-anchor blind spots.** Even when vanilla finds an anchor, it walks from one anchor at a time and bails the moment it closes a single cycle. A topology with two disjoint cycles sharing a network anchors on one, breaks one cable, then the second cycle persists until the next tick re-detects it. Cumulative cost: cascading partial-tick burns where a single pass should have caught everything.

### 4.2.5 PowerGridPlus's own DFS (replaces vanilla-driven detection)

The PROTECT-phase cycle detector does NOT rely on `BreakableCables.Count > 0` or `CheckForRecursiveProviders` results as the cycle signal. Vanilla's walk has the structural gaps in §4.2 (wireless invisible, single-anchor blind spots, multi-hop loops with no provider anchor, battery-only / APC-only cycles with `Required = 0` everywhere). PowerGridPlus builds and walks its own graph.

Algorithm:

1. **Build the bipartite graph.** Nodes are (a) every `CableNetwork` in `CableNetwork.AllCableNetworks` and (b) every segmenting device in `SegmentingDeviceRegistry.AllSegmentingDevices` (per §5.0 and §8.0.5, sorted by `ReferenceId ASC`). Edges are undirected: each segmenting device contributes one edge to its `InputNetwork` and one edge to its `OutputNetwork`. Wireless PT-PR links contribute an additional network-to-network edge subject to the OnOff gate in §6.5.

2. **DFS from every segmenting device.** Walk from each segmenting device in sorted order. Track a per-walk visited set (cleared per starting device, never reused across walks); a back edge to a vertex already on the current DFS stack identifies a cycle.

3. **Mark cycle participants.** Every segmenting device on a cycle path is registered for CYCLE_FAULT (60s lockout). Devices that appear in multiple detected cycles get registered once with the longest applicable timer (effectively just the standard 60s, since all cycles fire at the same tick).

4. **Determinism.** Sorting `SegmentingDeviceRegistry.AllSegmentingDevices` by `ReferenceId ASC` before the DFS walk guarantees identical traversal order across MP peers without any float dependence; pair this with the §8.0.5 sort key for the allocator and the cycle detector becomes a free MP-deterministic operation.

5. **Vanilla detection ignored.** `CheckForRecursiveProviders` still runs as part of OBSERVE (we do not suppress it) and still populates `BreakableCables` / `BreakableFuses`. PowerGridPlus reads NONE of these as a cycle signal. They remain active as belt-and-braces per §17.7 / §17.25; in normal operation PGP's DFS catches every cycle first and the PROTECT phase never invokes `BreakSingleCable` / `BreakSingleFuse`. If a future bug ever lets a cycle slip past PGP's DFS, vanilla's destructive burn fires as the safety net.

This covers the gaps vanilla misses: wireless edges are first-class graph edges; multi-hop loops are walked end-to-end regardless of anchor presence; battery-only and APC-only cycles are walked because every battery and APC is a graph node regardless of its current `_powerProvided` value; self-sustaining mutual-feed loops are walked because the graph topology is purely structural and never consults `Required`.

### 4.3 Pre-allocator burn (PROTECT phase)

> 2026-06-13 (B+C rework): wrong-tier burns no longer attempt a same-tick network re-enumeration. The
> split from `Cable.Break()` is parented to `Cable.OnDestroy` and lands on the main thread at end of
> frame (see `Research/GameClasses/Cable.md`, "Network split on destruction"), so it is observed by the
> NEXT tick, not mid-tick. The cycle DFS therefore walks the pre-tier-split topology; this is
> harmless because a cycle through a soon-to-burn cable is real this tick and is dissolved by zeroing its
> members (§4) regardless of whether the cable is gone. The original post-burn re-enumeration step is
> removed. See POWER_DEVIATIONS.md P1 for the full rationale and the dedi verification.

The cycle burn fires BEFORE the allocator runs, so the allocator never sees a cycle's inflated `Potential` / `Required` numbers. PROTECT sits between SETUP / OBSERVE and ALLOCATE, and runs three steps in this order:

1. **OBSERVE (prerequisite)**: per-network `Initialise + CalculateState`. Vanilla `CheckForRecursiveProviders` runs and appends `BreakableCables` / `BreakableFuses` entries (PGP ignores these for cycle detection per §4.2.5; they remain as belt-and-braces).
2. **Wrong-tier detection (worker thread)**: `VoltageTierEnforcer.Run` clears landed pendings (`SplitPendingRegistry.SweepLanded`), then walks every cable network and detects any tier violation per the §3 invariant using cached state only (`Connection.GetCable` reads the cached `LocalGrid`, cable types, the device list -- no `Transform` access, so it is worker-safe). A detected violation on a non-pending network is reserved in `SplitPendingRegistry` and its burn is marshalled to the main thread (`UnityMainThreadDispatcher`), where the mixed-tier victim walk (`ConnectedCables`, main-thread only) and `Cable.Break()` run synchronously; the split lands at end of frame. This is the per-tick backstop. The immediate path is the `CableNetwork.OnNetworkChanged` subscription (main thread), which re-checks and burns the instant any membership mutation creates a junction -- before any tick conducts across it.
3. **Cycle detection and CYCLE_FAULT**: run the §4.2.5 DFS over `SegmentingDeviceRegistry.AllSegmentingDevices` (sorted by `ReferenceId ASC`) plus the wireless edges from §6.5, identify segmenting devices on each cycle path, mark them for CYCLE_FAULT (§4.5). It walks the current topology (any tier split queued by the wrong-tier scan lands next tick); a cycle through a doomed cable is dissolved by zeroing its members this tick regardless. PGP's DFS is the cycle signal; `BreakableCables` is not consulted.
4. **Producer-isolation (VARIABLE_VOLTAGE_FAULT)**: the §8.5 walk runs right after cycle detection.
5. **PROTECT re-observe**: if any network's state changed during the cycle / producer detectors (newly cycle-faulted or VVF members now contributing 0), re-run `Initialise + CalculateState` so ALLOCATE sees clean state.
6. **ALLOCATE**: the allocator runs against the clean post-fault state, and skips committing new shed / overload lockouts on any network with a burn in flight (`SplitPendingRegistry.IsPending`), so no durable decision is made against a merged topology that is about to split (Option C).
7. **ENFORCE**: as before. `ApplyState` no longer needs to drive cycle-burns; the PROTECT phase handled them.

Wrong-tier detection before cycle detection is the locked order within PROTECT. Doing it the other way would let cycle detection walk through wrong-tier cables that are about to vanish, wasting the work and (worse) potentially marking devices that the wrong-tier burn would have isolated anyway.

### 4.4 Only powered loops burn

A cycle whose loop carries no current is invisible to the player and burning a cable there would break immersion. The PROTECT phase burns only when the loop is actually carrying power:

For each `BreakableCables` / `BreakableFuses` entry, look up the entry's network. The loop is "powered" when the network's `_actual = min(Potential, Required) > 0`. Only burn powered entries; carry unpowered entries forward by simply NOT acting on them (they re-detect next tick automatically because `CheckForRecursiveProviders` re-walks every tick).

This means an unused cycle (e.g., a build-time mistake in an unpowered grid) sits silently until something downstream pulls power; the moment power flows, the cable burns.

### 4.5 Cycle-fault: how loops break

PowerGridPlus replaces vanilla's cable burn for cycles with a CYCLE_FAULT mode. When the PROTECT phase detects a powered cycle:

1. Identify every segmenting device whose `InputNetwork` or `OutputNetwork` participates in the loop. This is the full §5.0 segmenter list applied uniformly: `Transformer`, `Battery`, `AreaPowerControl`, `PowerTransmitter`, `PowerReceiver`, `RocketPowerUmbilicalMale`, `RocketPowerUmbilicalFemale`. PowerConnection is excluded per §5.0.1.
2. Mark all of them for CYCLE_FAULT, with no class-specific exemption. A Battery on a cycle gets CYCLE_FAULT (red flash, hover countdown), stops bridging power through its input + output terminals, and stops contributing to elastic supply on its output net for the full 60 s. An APC gets the same treatment. PT/PR pairs get it on both the PT and PR side. RocketPowerUmbilicalMale gets the visual flash; RocketPowerUmbilicalFemale uses the hover-only path per §5.0.2.
3. Each device enters the new `CycleFaultRegistry` with `_lockoutUntilTick = currentTick + 120` (60 seconds).
4. While in CYCLE_FAULT each device's contribution to power flow is 0 (same effect as SHED / OVERLOAD). For elastic suppliers (Battery, APC, RocketPowerUmbilical*) this means `GetGeneratedPower` returns 0 via the SoftSupplyShareCache postfix path (§7.3.0.1); the device does not discharge into its output network while locked out, even if it has stored energy.
5. Because every segmenting device in the loop is now contributing 0, the loop is broken: the allocator sees clean state on the next iteration of §8.0 within the same tick. No cable is destroyed.
6. After 60 seconds the fault auto-clears. The next allocator pass re-detects: if the cycle still exists, CYCLE_FAULT re-fires. If the player has rewired correctly, devices re-engage.
7. The OFF-as-reset (§10.3) applies uniformly to every CYCLE_FAULTed segmenter that has a clickable OnOff button: a player toggling the device OFF then ON clears its lockout instantly, allowing manual retry. For RocketPowerUmbilicalFemale (no OnOff button) the only manual reset is to wait out the timer.

This unifies the "device is faulted, hover for cause" experience:
- Orange flash + "(Shedding: insufficient upstream supply)" -> upstream undersupply.
- Red flash + "(Overloaded: downstream demand exceeds this device's limit)" -> downstream overcommitment (device-specific wording, §11.1).
- Red flash + "(Cycle Fault: this device is part of a closed loop)" -> topological loop.

CYCLE_FAULT uses the same red flash code path as OVERLOAD because both are "downstream / topology" faults the player needs to fix, not just upstream supply issues.

### 4.6 No cable burn from cycles

The vanilla `BreakableCables` / `BreakableFuses` lists are still populated by OBSERVE's `CalculateState` (vanilla detection runs), but PowerGridPlus does NOT consume them as a cycle signal: per §4.2.5 cycle detection is PGP's own DFS over the segmenting-device graph. The PROTECT cycle detector never calls `BreakSingleCable` / `BreakSingleFuse`. The lists are cleared at the start of the next tick by vanilla `Initialise`. They remain populated and live as belt-and-braces: if a future bug ever lets a cycle slip past PGP's DFS, vanilla's burn fires as the safety net (§17.7 / §17.25).

The cable-burn check for non-cycle overloads (`cable.MaxVoltage < _actual` when caused by direct generator supply only) is retained per §5.7. Transformer-side overflow does not burn cables either (it trips overloads).

## 5. Transformer model

### 5.0 What counts as a "segmenting device" (level boundary)

Every device that holds two distinct cable / wireless network references AND has Input/Output power-flow semantics segments the power-flow graph and counts as a level boundary in the §8.0 cascade. The cascade walks the segmentation tree, not just the transformer subset.

Verified concrete classes (confirmed against the 0.2.6228.27061 decompile):

1. `Transformer : ElectricalInputOutput` (line 403300). Cable -> cable bridge with throttle.
2. `Battery : ElectricalInputOutput` (line 370616). Cable -> cable bridge with internal store.
3. `AreaPowerControl : ElectricalInputOutput` (line 369509). Cable -> cable bridge with internal cell + `_powerProvided` interlock.
4. `PowerTransmitter : WirelessPower` (line 387065). Cable -> wireless bridge.
5. `PowerReceiver : WirelessPower` (line 386861). Wireless -> cable bridge.
6. `RocketPowerUmbilicalFemale : ElectricalInputOutput` (line 147895). Cable -> rocket-umbilical bridge with internal cell `PowerMaximum = 10000`.
7. `RocketPowerUmbilicalMale : ElectricalInputOutput` (line 148269). Rocket-umbilical -> cable bridge with internal cell `PowerMaximum = 10000`.

All seven use the `InputConnection` / `OutputConnection` / `InputNetwork` / `OutputNetwork` quadruple and inherit the short-circuit gate (`InputNetwork == OutputNetwork && OutputNetwork != null` returns `IsOperable = false`, see §7.1).

### 5.0.1 `PowerConnection` is vestigial dead code; PowerGridPlus ignores it

`PowerConnection : Electrical` (line 386738) is verified to be a vestigial / orphan class in the Stationeers codebase. Definitive findings:

- No `StructurePowerConnection` prefab exists in vanilla. Stationeering.com's Stationpedia scrape returns 404. No localization key, no kit, no recipe, no build-menu entry. Players never encounter it.
- The class's `private CableNetwork GetOtherNetwork(...)` helper is never called from anywhere else in the assembly.
- `is PowerConnection` is never checked anywhere in vanilla or in any other mod (PowerTransmitterPlus, PowerGridPlus, SprayPaintPlus).
- The class inherits `Device.IsPowerProvider = false`. The CableNetwork tick does NOT treat it as a power-providing or segmenting device.
- Apart from the class definition at L386738, the only literal occurrences of the token `PowerConnection` in the entire decompile are an unrelated `LandingPadDataPowerConnection` class at L173323 and local-variable names like `_inputPowerConnection`.
- Most likely an early-development cable-bridge concept replaced by the Transformer / AreaPowerControl / Battery model in shipping play and never removed from the C# tree (mirrors the pattern of the empty `PortableGenerator : DynamicThing { }` stub at the neighbouring lines).

PowerGridPlus treatment: ignore `PowerConnection` entirely. Do NOT add to producer-isolation, cycle detection, segmenting-device list, allocator contributor list, or fault-eligible set. The existing pattern-matched casts in PowerGridPlus (`device is Transformer ct && ct.InputNetwork == net`) already null-check correctly, so no defensive code change is needed.

POWER.md §5.0 lists 7 segmenting device classes (Transformer, Battery, AreaPowerControl, PowerTransmitter, PowerReceiver, RocketPowerUmbilicalMale, RocketPowerUmbilicalFemale). PowerConnection is NOT one of them.

### 5.0.2 Cascade and storage-charge flow apply uniformly to (1)-(7)

The cascade (§8.0) and the storage-charge flow (§9) treat all of (1) through (7) uniformly. PowerConnection plays no role.

Visual feedback varies by class: `RocketPowerUmbilicalFemale` has NO visual indicator on the device itself; fault state is communicated ONLY by hover text on the device. The Female side has no clickable `InteractableType.OnOff` interactable to host a material-swap flash, so it reuses the hover-only path defined for non-flash producers in §8.5 (a `GetPassiveTooltip` postfix appends the fault line + countdown to `__result.Extended`). See §11.4 for the full per-class flash / hover coverage.

Idle hover behaviour on `RocketPowerUmbilicalFemale`: when the device is NOT in a fault state, PGP does NOT touch the hover text at all. The vanilla `GetPassiveTooltip` output (which may be blank or show vanilla pre-built status text) is what the player sees. The PGP postfix early-returns if `BrownoutRegistry`, `OverloadRegistry`, `CycleFaultRegistry`, and `ProducerFaultRegistry` all report "not locked" for this `ReferenceId`. PGP injects content only when a fault is active.

### 5.0.3 The segment adapter contract (ISegAdapter / SegSpec)

GATHER (§8.0.0.1) does not open-code each bridge class; it consults one `ISegAdapter` per modelled class (`SegAdapters.cs`). An adapter answers `Describes(device)` (pure type membership, state-independent) and `TryDescribe(device, out SegSpec)` (the device's per-tick PHYSICAL description: flow kind, terminal networks, capacities, distance multiplier `m`, quiescent draw, pair partner; false when the device presents no flow surface right now). GATHER attaches allocator POLICY on top of the description: priority, lockout state, shed / overload bookkeeping. Two flow models:

- **Routed**: power crosses the device within the tick it is granted; the device becomes one allocator `Seg` (a pull-through contributor). The output side delivers `TotalThrough`; the input side bills `TotalThrough * max(m, 1) + quiescent` in the same tick (§8.0.4).
- **Buffered**: power stops in an internal cell; nothing crosses within the tick. The device contributes storage roster entries instead of a `Seg` (cell charge = Soft demander, cell discharge = Elastic supplier); the physical crossing to the far side happens outside the allocator.

The four adapters (`SegAdapters.cs`):

| Adapter | Device class | Kind | Capacity | m |
|---|---|---|---|---|
| `TransformerAdapter` | `Transformer` | Routed | `EffCap = min(Setting, min(input cable cap, output cable cap)) - UsedPower`, clamped to >= 0 | 1 |
| `WirelessPairAdapter` | linked `PowerTransmitter` + `PowerReceiver`, ONE seg anchored on the transmitter (§6.2) | Routed | static link rating (§6.3) bounded by `min(input cable cap / m, output cable cap)`, minus BOTH halves' quiescent draws | interop `m >= 1` (§6.6) |
| `ApcAdapter` | `AreaPowerControl` | Routed | cable caps only (`CapacitySetting = float.MaxValue`: the §8.4 hit-max rule never applies); the internal cell is NOT part of the routed description | 1 |
| `UmbilicalAdapter` | `RocketPowerUmbilical` (Male + Female via the shared abstract base, game 0.2.6403+) | Buffered | soft = `min(charge rate, input cable cap, cell headroom)`; elastic = `min(discharge rate, output cable cap, cell store)` | n/a |

`Battery` and `PowerReceiver` carry no adapter of their own: the battery is pure storage enrolled directly by GATHER (§8.0.0.1), and the receiver is described through its linked transmitter (a cycle-faulted partner locks the pair). An unlimited (config-0) cap maps to a finite sentinel of 1e9 W, never `float.MaxValue`: the structural-overload detector sums supplier EffCaps, and a MaxValue term would overflow the sum to +Infinity.

The umbilical's Buffered classification formalizes the store-and-forward model the allocator applies to it: grid to near cell this tick (Soft), cell to partner cell in vanilla phase 2 (the DEVICE TICK, outside the allocator), partner cell to far grid on a later tick (Elastic). Since game 0.2.6403 the phase-2 crossing is BIDIRECTIONAL: the Male pushes station-to-rocket, and the Female runs its own TransferProgress-gated rocket-to-station transfer. The allocator never models the crossing itself; it only sees each cell's level change between ticks (see `Research/GameClasses/RocketPowerUmbilical.md`).

Any `ElectricalInputOutput` subclass that no adapter describes and the segmenter roster does not know is an unknown bridge: it keeps its vanilla power methods, GATHER sums its `GetUsedPower` / `GetGeneratedPower` as plain rigid demand / generation on each side (the conservative fallback, §8.0.0.2), and the one-shot census reports it at world load (§8.8).

### 5.1 Direction classification

Every transformer is classified at runtime as `StepUp`, `StepDown`, or `SameTier`:

- Read the cable tier of `InputNetwork` (looking at the cable type predominant on the network).
- Read the cable tier of `OutputNetwork`.
- `Direction = inputTier < outputTier ? StepUp : (inputTier > outputTier ? StepDown : SameTier)`.

Cable tier ordering: normal (1) < heavy (2) < super-heavy (3). The classifier reads cable type directly off the network's `Cables` collection; it does not need the `EnableVoltageTiers` flag because tiers are always on.

Edge case: a network in transition (mid-burn) may briefly have mixed cables. The classifier uses the cable with the highest tier as authoritative for that tick; classification stabilises after the burn completes.

### 5.2 Direction governs shed eligibility

- **Step-up transformers never shed.** They are pass-through points. Their `GetGeneratedPower` formula runs vanilla-faithfully, capped at `OutputMaximum` and `InputNetwork.Potential - _powerProvided`. The allocator's shed pass skips them entirely.
- **Step-down and same-tier transformers are shed-eligible.** They participate in the shed cascade described in §7.

### 5.3 Throughput rating, Setting, Priority, and dial

PowerGridPlus separates three concepts that vanilla conflated:

- **`OutputMaximum`** (vanilla field, prefab-specific): the transformer's *rated* throughput. 5 kW small, 50 kW large, etc. Hard ceiling on what the transformer can ever do. Not directly writable.
- **`Setting`** (vanilla field, `[0, OutputMaximum]` range): the transformer's *active* throughput cap this tick. Vanilla treats Setting as the dial-controlled throughput. PowerGridPlus keeps `Setting` fully vanilla-writable from IC10 and reverts the previous PGP behaviour of redirecting `Setting` writes to `Priority`. IC10 scripts that read or write Setting work exactly as in vanilla.
- **`Priority`** (PGP-added store, non-negative integer): the dispatch priority used by the shed cascade. Non-negative integer, no upper cap, default 100. Stored in `PriorityStore` keyed by `Thing.ReferenceId`. Persisted via `PrioritySideCar`. `Priority = 0` means the lowest priority value; the device still receives allocation if any supply remains after higher-priority devices are served. `Priority = 0` is NOT a sentinel for "disabled" or "skip" -- a player who wants to disable a transformer toggles its OnOff button or sets `Setting = 0` instead.

Init-time defaults (PGP-imposed):

- **On new transformer construction**: when a player places a transformer and the build completes, PowerGridPlus sets `Setting = OutputMaximum` so the freshly placed transformer runs at full rated throughput by default.
- **On world load**: PowerGridPlus does NOT overwrite `Setting` on existing transformers. A transformer that has an IC10-throttled `Setting < OutputMaximum` saved in the world keeps its saved value. Only freshly placed transformers get the OutputMaximum default.
- This matches the spec's "transformer runs at full rated throughput by default" assumption for new builds while preserving any deliberate IC10 throttle the player set in a previous session. In practice almost no player will ever touch `Setting`; the visible knob writes Priority. But legacy or sophisticated IC10 scripts that write `Setting` to dynamically throttle a transformer continue to work, and their saved state is honoured across save / load.

In-world writers redirect to Priority:

- The in-world **dial / knob** writes to `Priority`, not `Setting`. Button1 / Button2 step Priority by ±10 (no Alt) or ±1 (with Alt).
- The **Labeller** tool, when used on a transformer's dial, writes to `Priority`, not `Setting`. (The Labeller is the player-held tool that reads / writes numeric device values; PGP's existing label patch covers this.)
- Any other future in-world UI that writes the transformer's "knob value" should also be redirected to Priority. Add per case.

**Throttle hover warning (deviation P13).** A transformer whose `Setting` is below its `OutputMaximum` is running a custom throttle that, under PowerGridPlus, can only have come from IC10 (the dial writes Priority) or a legacy / vanilla save where the player dialed the vanilla Setting down. To keep that from being a mystery dark subnet, PGP surfaces it: a neutral info line (no flash, no countdown) appears on BOTH the transformer's case hover and its on/off button hover (`TransformerThrottleHover`, stacked into `FaultHoverPatches` and `TransformerHoverErrorPatches`):

> Throttled to {Setting} W of {OutputMaximum} W by a custom IC10 "Setting" value. The dial sets priority.

Fresh transformers default `Setting = OutputMaximum` (above), so the line never shows on a default build. It stacks BELOW any active fault line, so a throttled transformer that also overloads shows both (and the throttle explains the overload, §8.4). The saved `Setting` is still honoured (not migrated, §5.3); the warning just makes the IC10-or-rebuild fix discoverable instead of silently leaving a dark subnet. The colour is a muted amber (`#d9a441`) to read as "advanced use" without mimicking the shed orange.

IC10 access:

- `LogicType.Setting` reads return the live `Transformer.Setting` value. Writes update `Transformer.Setting`, clamped to `[0, OutputMaximum]`. Pure vanilla. Default after init = `OutputMaximum`.
- `LogicType.Maximum` returns `OutputMaximum` (vanilla).
- `LogicType.Ratio` returns `Setting / OutputMaximum` (vanilla).
- `LogicType.Priority` reads and writes the `PriorityStore` entry. Read returns the int priority; write quantises to non-negative int.
- `LogicType.Shedding` returns 1 during SHED lockout, 0 otherwise (read-only).
- `LogicType.Overloaded` returns 1 during OVERLOAD lockout, 0 otherwise (read-only).
- `LogicType.CycleFault` returns 1 during CYCLE_FAULT lockout, 0 otherwise (read-only).

**Formulas in this document use `Setting` for the active throughput cap, NOT `OutputMaximum`.** Setting starts equal to OutputMaximum and can be reduced by IC10 to dynamically throttle a transformer. See §5.5 / §5.6 / §8.4 for explicit usage. OutputMaximum still appears in formulas only as the *upper bound* of what Setting can be (the prefab rating).

### 5.4 Per-input-network priority comparison

This is the rule that replaces the old global priority sort. Priorities are compared ONLY among transformers that share the same input cable network. A transformer at priority 100 on the main grid is not compared with a transformer at priority 100 on a mid-net even if both are at the same depth from the supply. This is what makes "minimize the blast radius" tractable: each input network's siblings compete only with each other.

### 5.5 Binary state and effective throughput formula

A transformer (or APC, or PT/PR pair) has a binary operating state per tick: ON or LOCKED-OUT (shed OR overload). There is no "throttled" intermediate state. A transformer either gets its full needed draw from its input network or it sheds entirely.

State semantics:

- **ON**: input network supplies the transformer's full required draw, output network demand is at or below `Setting`. Working normally; `actual_throughput = min(Setting, output_demand)`.
- **SHED**: input network cannot supply the transformer's full required draw. Lockout for 60 seconds. Contributes 0 to its output network.
- **OVERLOAD**: input adequate; output demand exceeds `Setting`. Lockout for 60 seconds. Contributes 0 to its output network.

Both SHED and OVERLOAD result in the transformer being "off" for downstream purposes (0 W contribution). The cause differs and so do the visuals and IC10 flags.

### 5.6 Effective throughput formula

A transformer's effective throughput cap per tick is:

```
effective_cap = min(Setting, InputCable.MaxVoltage, OutputCable.MaxVoltage) - UsedPower
```

Where:

- `Setting`: vanilla per-transformer Watts cap (`[0, OutputMaximum]`). Default after PGP init = `OutputMaximum`. Player IC10 scripts can reduce it to dynamically throttle. In-world knob writes Priority, not Setting (§5.3).
- `InputConnection.GetCable().MaxVoltage`: input cable's Watts cap (misleadingly named; see §5.7).
- `OutputConnection.GetCable().MaxVoltage`: output cable's Watts cap.
- `UsedPower`: own quiescent draw (vanilla `Device.UsedPower`, default 10 W, per-prefab).

Actual throughput on a given tick is further bounded by downstream draw:

```
actual_throughput = min(effective_cap, downstream_demand)
```

Where `downstream_demand` is the rigid + allocated soft demand reaching this transformer's output network during ALLOCATE.

### 5.7 What burns cables, what triggers overload

> 2026-06-13 (P2 deterministic rework): the generator-overflow burn is now a DETERMINISTIC 20-tick
> running average (`CableBurnWindow`), not a per-tick probability roll. There is no RNG and no setting
> (`CableBurnFactor` is removed). A cable burns when the 20-tick (10 s at 2 Hz) running average of the
> generator power flowing on a network exceeds the weakest cable's cap; the victim is the cable at the
> output of the generator that produced the most over that window. See POWER_DEVIATIONS.md P2 for the
> full design and the dedi verification. Rule 3a below is updated to match; the overload side (3b) is
> unchanged.

The vanilla cable-burn check `cable.MaxVoltage < _actual` is retained but tightened. A cable burns only when GENERATOR-direct supply alone exceeds the cable cap. Transformer-derived overflow does NOT burn the cable; it trips the upstream transformers as overload.

Per network N at the end of ALLOCATE:

1. Compute `generator_supply_N` = sum of `GetGeneratedPower(N)` for every device on N that is NEITHER an `ElectricalInputOutput` NOR a `WirelessPower` (i.e., true generators: SolarPanel, CoalGenerator, RTG, WindTurbine, HydrogenBurner, etc.). Batteries discharging on N are NOT counted here; they self-cap and never push past the cable.
2. Compute `actual_flow_N` = `min(N.Potential, N.Required)` as vanilla does.
3. Apply rules:
    - Observe `min(generator_supply_N, actual_flow_N)` into network N's 20-tick sliding window (`CableBurnWindow`) every tick. If the 20-tick running average exceeds the weakest cable's cap, BURN the cable at the output of the generator that produced the most over the window (deterministic; ties by lowest ReferenceId). A full window is required first (10 s grace), and the window resets on a burn. This replaces the old per-tick `generator_supply_N > cap` instantaneous test (and the interim probability ramp) so a transient spike must be sustained, or countered by a dip within 10 s, to burn.
    - Else if `actual_flow_N > cable.MaxVoltage`: the excess comes from upstream transformers / PT/PR pairs / batteries. Trip EVERY upstream segmenting device feeding N (every transformer, APC, PT, battery whose `OutputNetwork == N`) into OVERLOAD. NO cable burn.
    - Else: no action.

The "generator + transformer" mixed case is uncommon but well-defined: if `generator_supply_N` alone is under the cap, but transformer contributions push the total over, the transformers take the trip; the cable survives because the generators alone wouldn't have killed it.

Batteries do not contribute to the cable-burn risk because their `GetGeneratedPower` is elastically capped at the rigid shortfall (§7.3); they only push as much as is being pulled from the downstream side, which is by construction at or below the network's demand and therefore at or below the cable's draw budget.

This rule means a player who lays out an under-sized cable carrying a heavy transformer's output sees the transformer trip (a clear, recoverable signal: "upgrade this transformer or split the load") rather than the cable burning silently in a random spot.

### 5.6 Cable max settings

Cable `MaxVoltage` per tier is read at runtime from the live `Cable` instance, not hardcoded. PowerGridPlus exposes three server-authoritative settings to override per tier (all values in Watts):

- `CableNormalMaxWatts`: default 5000 (vanilla normal value).
- `CableHeavyMaxWatts`: default 50000 (vanilla heavy value).
- `CableSuperHeavyMaxWatts`: default 0 (= unlimited, internally stored as `float.MaxValue`).

A configured value of `0` means unlimited and is normalised internally to `float.MaxValue`. The setting description says "0 = unlimited" so operators know the rule.

PowerGridPlus does NOT rewrite cable `MaxVoltage` (D2 non-mutating, deviation P14): `Cable.MaxVoltage` is a per-instance serialized field, so writing it would bake the configured caps into the save and survive mod removal. Instead every cap reader (battery / APC headroom, the generator-overflow burn check, the allocator) consults the `CableMax` helper, which reads the configured per-tier setting live at use-time, and vanilla's own cable-burn check is patched to do the same (`PowerTickPatches.GetBreakableCables_Prefix`), so both read the same number. Removing the mod reverts cables to vanilla ratings with no save contamination. This subsumes the legacy `EnableUnlimitedSuperHeavyCables` setting (equivalent to `CableSuperHeavyMaxWatts = 0`); the legacy setting is removed.

All formulas in §5.5 and elsewhere are generic across tiers: the per-tier numeric values come from settings, not hardcoded constants.

## 6. Power transmitter model

Power transmitter / receiver pairs are first-class transformers at the allocator layer. The synthetic transformer is a MODEL the allocator builds in ALLOCATE by reading existing fields. The actual energy bookkeeping runs through vanilla `PowerTick.Initialise / CalculateState / ApplyState` in OBSERVE and ENFORCE, calling `GetGeneratedPower`, `GetUsedPower`, `UsePower`, `ReceivePower`. PowerGridPlus's per-device touches on `PowerTransmitter` / `PowerReceiver` are the lockout postfix (returns 0 when the pair is locked out, §6.4), the fresh input-draw bill and the delivery gate (`PowerTransmitterDrawPatches`: the transmitter bills the pair's exact in-tick `TotalPull` from `TransformerSupplyCache`, and a last-priority postfix clamps the advertised wireless delivery to the granted `TotalThrough`, §8.0.4), the `AllowSetPower` postfixes of the Powered presentation policy (§10.6), and the ENFORCE-tail ledger settle (§10.7).

### 6.1 Pairing and topology

- `PowerTransmitter` sits on its `InputNetwork` (cable side). It is a CONSUMER on that net.
- `PowerReceiver` sits on its `OutputNetwork` (cable side). It is a GENERATOR on that net.
- The wireless link is itself a `WirelessNetwork` (subclass of `CableNetwork`) that is the SAME OBJECT on both ends. Vanilla guarantees `PT.OutputNetwork === PR.InputNetwork` (same `WirelessNetwork` reference) once a PR's `LinkedPowerTransmitter` setter writes the join.
- Pairing is via cross-references: `PowerTransmitter.LinkedReceiver` and `PowerReceiver._linkedPowerTransmitter`. There is no central registry. PowerGridPlus enumerates pairs by scanning `PowerTransmitter` instances and reading `LinkedReceiver`.
- Unlinked transmitters and unlinked receivers contribute nothing to the allocator. They are skipped in classification.

### 6.2 Synthetic transformer model

The allocator constructs a `TransformerSurrogate` per linked PT:

- Input network = `PT.InputNetwork` (the source cable network).
- Output network = `PR.OutputNetwork` (the destination cable network).
- Anchor `Thing` and `ReferenceId` = PT's (so flash, hover text, IC10, Priority, and lockout registries all key off the PT).
- `Direction` classification: §5.1 applied to tier of PT's input vs PR's output.
- `Priority`: dial-controlled identically to a wired transformer, stored in `PriorityStore` keyed by PT's ReferenceId.
- `BrownoutFlashBehaviour` attached to the PT's on/off button. Shed and overload visual + hover text apply to the PT's button.

### 6.3 Effective throughput and efficiency

Vanilla has no `TransmissionEfficiency` field on PowerTransmitter. Two distance-loss models exist depending on whether PowerTransmitterPlus is loaded:

- **Vanilla only.** `PowerTransmitter.PowerLossOverDistance` is an `AnimationCurve` keyed on `distance / _MaxTransmitterDistance` (where `_MaxTransmitterDistance` = 500 m). Output = `min(PT.MaxPowerTransmission, InputNetwork.PotentialLoad) - curve.Evaluate(distance/500) * PT.MaxPowerTransmission`. `PT.MaxPowerTransmission` defaults to 5000 W.
- **PowerTransmitterPlus loaded.** PowerTransmitterPlus does NOT derate delivered power for distance. It removes the 5000 W ceiling (`GetGeneratedPower` returns `min(MaxTransferCapacity, InputNetwork.PotentialLoad)`, with `MaxTransferCapacity` 0 = unlimited by default) and instead inflates the SOURCE-side draw: the transmitter's input network is billed `delivered * m`, where `m = 1 + k * distance_km` (k configurable, default 5, clamped to >= 1). The distance penalty is paid by the source, not subtracted from the deliverable. `MicrowaveEfficiency = 1 / m` exists only as an IC10 readout, never applied to any energy quantity.

So the two models differ in WHERE distance bites: vanilla lowers the deliverable, PowerTransmitterPlus raises the source draw. PowerGridPlus accounts for both.

The allocator sizes the pair from a **STATIC link rating**, never from the live `PT.GetGeneratedPower(WirelessOutputNetwork)` or `InputNetwork.PotentialLoad`. Reading the live potential (as the vanilla and PowerTransmitterPlus `GetGeneratedPower` do) created a cross-tick zero fixed point: on a transformer-fed source the potential reads 0 until something pulls, so the cap collapsed to 0, the pair desired 0, nothing ever pulled, and a false OVERLOAD re-armed forever. The forward supply sweep (§8.0.3) is the only throttle on actually delivered power; the rating only sizes a genuine OVERLOAD breach (delivered demand above what the link itself can carry, independent of the source). Per `WirelessPairAdapter` (`SegAdapters.cs`):

```
link_rating = PowerTransmitterPlus loaded
                ? (EffectiveMaxCapacity > 0 ? EffectiveMaxCapacity : 1e9 sentinel)   // 0 = unlimited
                : max(0, MaxPowerTransmission - PowerLossOverDistance(distance / 500) * MaxPowerTransmission)

cable_cap   = min(input cable cap / m, output cable cap, 1e9 sentinel)
static_cap  = min(link_rating, cable_cap)                 // the pair's CapSetting (§8.4 threshold)

PT_effective_cap = static_cap - PT.UsedPower - PR.UsedPower   // both dish quiescent draws, clamped >= 0
```

Here `m` is the PowerTransmitterPlus source-draw multiplier (1 under vanilla or when PowerTransmitterPlus is absent, and exactly 1 for an unlinked transmitter). The input-cable bound is divided by `m` because a long link draws `delivered * m` through its input cable, and the pair's demand on its INPUT network is `throughput * m`, not `throughput` (§8.4.2). The allocator reads `m` and the capacity through `PowerTransmitterPlusInterop` (the three-tier bridge, §6.6). Because `static_cap <= cable_cap` by construction, the §8.0.3 structural-overload rule's cable-limited exclusion (`CapSetting > CableCap`) can never fire for a PT pair: a cable-bound breach reads as a genuine OVERLOAD instead of silently under-delivering with no hover.

PT/PR pairs are shed-eligible (§5.2) when the pair's Direction classifies as StepDown or SameTier.

### 6.4 Shed / overload enforcement

Shed and overload lockouts on a PT/PR pair take the link offline: PT draws 0, PR generates 0, downstream goes dark for the 60-second lockout. Same OFF-as-reset behaviour as wired transformers, applied via the PT's on/off button.

Enforcement is via the same registries (`BrownoutRegistry`, `OverloadRegistry`) keyed by PT.ReferenceId. PowerGridPlus's `GetGeneratedPower` postfix on `PowerTransmitter` checks the registries and returns 0 when locked out (this is one of the very few places PowerGridPlus needs to touch a PT/PR per-device method, and it is additive over PowerTransmitterPlus's existing patches: Harmony priority is set late so PowerTransmitterPlus computes its distance-loss number first, then the lockout postfix overrides to 0 if applicable).

### 6.5 Wireless edge gating (OnOff, bidirectional pair enumeration)

A wireless edge between a `PowerTransmitter` PT and a `PowerReceiver` PR exists in the PROTECT-phase cycle graph if and only if:

1. The pair is established: `PT.LinkedReceiver != null` AND `PR._linkedPowerTransmitter != null`. Both cross-references must be set; either being null means the pair is half-broken (a load-time edge case or a mid-pairing transient) and no edge is contributed.
2. BOTH ends are turned ON: `PT.OnOff == true` AND `PR.OnOff == true`. A player turning either dish OFF removes the edge from the graph; cycle walking treats the link as cut.

Fault state does NOT affect edge existence. Per the refined skip-while-faulted rule (§17.39), the PROTECT-phase DFS walks through faulted devices for NEW cycle detection, so faulted PT or PR continues to contribute its edge to the graph. This is consistent with the wired transformer case: a faulted Transformer's `InputConnection` and `OutputConnection` are still graph edges; only its 0-throughput contribution falls out of the allocator's math.

Pair enumeration: walking only `PowerTransmitter` instances and dereferencing `LinkedReceiver` would miss multi-PR fan-out from a single PT, since the PT-side list can only hold one canonical link at a time and the second PR's edge would be invisible. PGP walks BOTH the PowerTransmitter and PowerReceiver instance lists, building the edge set as a deduplicated `HashSet<(long PtRefId, long PrRefId)>` keyed by the pair's two ReferenceIds. The HashSet de-dupes when both walks encounter the same pair. This catches every linked pair regardless of which side carries the canonical reference.

### 6.6 PowerTransmitterPlus compatibility

PowerTransmitterPlus and PowerGridPlus coexist through a resolved-once interop bridge (`PowerTransmitterPlusInterop.cs`; probed at first use, everything cached, no per-tick reflection; PowerGridPlus never hard-references PowerTransmitterPlus, so it stays an optional dependency). The atomic Prefix-return-false on `ElectricityManager.ElectricityTick` replaces only the outer scheduler; per-device methods still run inside OBSERVE and ENFORCE, and PowerTransmitterPlus's auto-aim patches, link-visibility patches, save side-cars, and IC10 readouts survive unchanged under every tier. Three resolution tiers, degrading in order:

1. **ModApi tier** (PowerTransmitterPlus 1.9.0+, preferred). The public, versioned cross-mod surface `PowerTransmitterPlus.ModApi` (requires `Version >= 1`; members are only ever added, never renamed or removed). PowerGridPlus binds `EffectiveMaxCapacity()` (the configured delivery cap in Watts, 0 = unlimited), `TryGetLink(transmitter, out distanceMeters)` (false when unlinked, so a dropped link's stale cached distance never surfaces), `SourceDrawMultiplier(transmitter)` (the factor `m`), and, bound leniently, the `GetTransferDebt` / `SetTransferDebt` ledger accessors that the ledger adoption uses (§10.7). It then calls `ClaimBillingOwnership("net.powergridplus")` exactly once.
2. **Legacy tier** (the shipped Workshop 1.8.0 line). `DistanceCostShared.SourceDrawMultiplier` (public wrapper, added after the 1.8.0 tag) or the internal `GetMultiplier` (the identical computation; the shipped Workshop build has only this), plus `MaxCapacityConfigSync.GetEffectiveMaxCapacity` and the vanilla `_linkedReceiverDistance` field. No ownership handshake exists at this tier; PowerGridPlus's delivery gate + fresh-pull billing (§8.0.4) keep the ledgers bounded instead.
3. **Absent.** `m = 1` and the vanilla link model (`MaxPowerTransmission` minus the `PowerLossOverDistance` delivery loss, §6.3).

The tiers agree by construction (the ModApi forwards to the same internals the legacy tier binds), so switching tiers never changes the allocator's numbers for the same world state. Exactly one Info line states the resolved tier and, on the ModApi tier, the claim outcome. Without the multiplier the allocator would under-count a long link's source-network draw by a factor of `m` and the no-partial-power invariant would fail on the transmitter's input network (§8.4.2).

**The billing-ownership handshake.** While the `"net.powergridplus"` claim is held, PowerTransmitterPlus's native wireless debt billing stands down: its `UsePower` debt inflation, its `GetUsedPower` source-side cap lift, and its standalone debt ceiling all no-op, and PowerGridPlus's allocator is the single billing authority for wireless links. The capacity advertise (the link-rating definition PowerGridPlus's delivery gate clamps), the receiver drain-cap lift, the beam visuals, and the link handling stay active on the PowerTransmitterPlus side. Each PowerTransmitterPlus billing patch checks `ModApi.BillingOwner` per call, so a late claim is safe regardless of plugin load order. Re-claiming the same id is idempotent; a claim while a different owner holds it is rejected (PowerGridPlus then logs the rejection and relies on the delivery-gate / fresh-pull containment, exactly as at the legacy tier). `ReleaseBillingOwnership` restores native billing.

**PowerTransmitterPlus standalone rules (for reference).** These define the behaviour the handshake stands down and the always-on parts of the environment the allocator runs in (`DistanceCostPatches.cs`):

- **Bounded debt ceiling** (standalone only, no billing owner): the advertise prefix pauses delivery (advertises 0) while the transmitter's unpaid `_powerProvided` debt is at or above `ceiling = effectiveCap * max(m, 1) * 4`, where `effectiveCap` is the configured Max Transfer Capacity, or the vanilla `MaxPowerTransmission` (5000 W) when the cap is unlimited (0). Delivery resumes as the source pays the debt down (one warning per pause episode; the episode ends once the debt falls below half the ceiling). This bounds the native debt runaway on an insufficient source and keeps the lump bill after an OnOff cycle finite.
- **Receiver drain-cap lift** (ALWAYS active, including under the handshake): vanilla `PowerReceiver.GetUsedPower(wireless)` bills `min(MaxPowerTransmission + UsedPower, debt)`, so a link delivering above 5 kW would strand the excess as receiver debt the source is never billed for (free energy). The lift raises the bound to `min(cap + UsedPower, debt)` for a configured cap above 5000, and to the full debt when the cap is unlimited (0); it never lowers the vanilla result. Flows above 5 kW are therefore billable end to end.
- **Unlinked multiplier is exactly 1.0**: `ModApi.SourceDrawMultiplier` returns 1 for a null or unlinked transmitter (and `TryGetLink` returns false), so the stale cached `_linkedReceiverDistance` a dropped link leaves behind never inflates a bill. For a linked transmitter, `m = 1 + k * distance_m / 1000` (k host-synced, default 5; `k <= 0` gives `m = 1`; never below 1).

Beyond the interop, the PowerGridPlus-side touches on PT/PR per-device methods are the ones listed in the §6 intro and §6.4: the lockout zero, the fresh input-draw bill plus the delivery gate (§8.0.4), the `AllowSetPower` presentation postfixes (§10.6), and the ENFORCE-tail ledger settle (§10.7). None conflicts with anything PowerTransmitterPlus does under any tier.

## 7. Battery and APC anatomy, rigid vs soft demand

### 7.1 Stationary battery anatomy: dual-terminal device

Stationary batteries in vanilla Stationeers (`Assets.Scripts.Objects.Pipes.Battery`, including the Small, Large, and MorePowerMod's Nuclear variants) are `ElectricalInputOutput` instances, the same base class as `Transformer` and `AreaPowerControl`. Each battery has:

- `InputConnection` and `OutputConnection`: two distinct cable terminals.
- `InputNetwork` and `OutputNetwork`: potentially different `CableNetwork` references.
- A single energy store `PowerStored` (Watt-ticks, clamped `[0, PowerMaximum]`).

Vanilla per-tick semantics, confirmed in `Battery.GetUsedPower`, `Battery.GetGeneratedPower`, `Battery.ReceivePower`, `Battery.UsePower`:

- **Input network: consumer only.** `GetUsedPower(InputNetwork)` returns `PowerMaximum - PowerStored` (headroom). `ReceivePower(InputNetwork, x)` credits the cell. Both methods return 0 / no-op when called with the Output network.
- **Output network: generator only.** `GetGeneratedPower(OutputNetwork)` returns `PowerStored`. `UsePower(OutputNetwork, x)` debits the cell. Both methods return 0 / no-op when called with the Input network.
- **Simultaneous in-tick charge + discharge is allowed and independent on distinct networks.** Stationary batteries have no per-tick interlock between the two terminals. On any single tick a battery on distinct Input and Output networks can both credit (from upstream on Input) and debit (to downstream on Output) its `PowerStored`. Net cell delta per tick = `received_on_input - delivered_on_output`. Both contributions clamp into `[0, PowerMaximum]` separately; no coupling state. There is no `_powerProvided` field on `Battery`.
- **Same-network short-circuit gate.** `ElectricalInputOutput.IsOperable` returns false when `InputNetwork == OutputNetwork && OutputNetwork != null`. All four power methods early-return 0 / no-op when not operable. Hover text shows "Device Short Circuited" in red. PowerGridPlus inherits this gate for free and does not need to detect it separately.

APCs share the dual-terminal architecture (`AreaPowerControl : ElectricalInputOutput`) BUT add a tick-coupling state `_powerProvided` that vanilla uses to pass power input -> output. As a consequence, on any single tick an APC's internal cell either charges (input surplus consumed by the cell) OR discharges (output deficit covered by the cell), never both within the same tick. Stationary batteries have no `_powerProvided` and no such interlock.

Charge / discharge rate caps (Watts):

- Stationary batteries: per-prefab server-authoritative caps in PowerGridPlus settings: `StationBatteryChargeRate` / `StationBatteryDischargeRate` (small), `LargeBatteryChargeRate` / `LargeBatteryDischargeRate` (large), `NuclearBatteryChargeRate` / `NuclearBatteryDischargeRate` (MorePowerMod nuclear; type detected at runtime).
- APC: `ApcBatteryChargeRate` (charge cap) and `ApcBatteryDischargeRate` (discharge cap), both server-authoritative settings read at use-time, not baked into any field. The charge cap is read in `ComputeChargeCap`; the discharge ceiling is owned by PowerGridPlus via `ApcDischargeRateRegistry` (per-APC overridable, session-only, since vanilla has no discharge-rate field) and does NOT reuse or mutate the vanilla `BatteryChargeRate` field.
- Effective cap is further clamped by the connected cable's `MaxVoltage` (Watts cap): `EffectiveChargeCap = min(ConfiguredChargeRate, InputConnection.GetCable().MaxVoltage)`. Symmetric on discharge.

### 7.2 Rigid versus soft demand classification

Each device's `GetUsedPower(net)` declares a demand. The shed pass cares about which demands are RIGID (must be satisfied or the device fails) and which are SOFT (can be fractionally allocated without harm).

Rigid:
- Lights, machines, IC10 chips, sensors, doors, hydroponics, every "vanilla operates or fails" device.
- APC passthrough portion (downstream consumers behind the APC). Modelled structurally: the APC is a routed contributor (§5.0.3), so its passthrough rides the seg's pull through the demand vector rather than being summed into the input network's rigid demand; see §7.5.

Soft demand on Input network only:
- Stationary batteries (charge cap headroom).
- Large stationary batteries.
- Nuclear batteries (MorePowerMod, detected at runtime via type name).
- APC's internal cell charge demand.
- Rocket umbilical cells (both halves, Buffered per §5.0.3).

Elastic supply on Output network only:
- Stationary batteries when they have stored energy and downstream rigid demand exceeds upstream rigid supply.
- APC's internal cell on the same condition (subject to the in-tick interlock above).
- Rocket umbilical cells with stored energy.

Rigid and soft ride ONE demand vector through the same backward/forward sweep (§8.0.3): rigid alone drives every fault decision, soft is granted out of the firm residual only, and unmet soft clamps silently (§9).

Note: Power Transmitters are NEITHER rigid nor soft. They are transformers, treated by §6.

### 7.3 Battery and APC as elastic supply on the Output network

Generators (coal generator, solar panel, RTG, wind, hydrogen burner) are rigid supply: they produce their `GetGeneratedPower` value whether anything consumes it or not. Batteries and APC cells are elastic supply: PowerGridPlus's allocator discharges them only to fill the rigid shortfall on their Output network, never more.

**Per-battery effective cap accounts for both rate AND stored energy:**

```
effective_discharge_i = min(DischargeRateCap_i, PowerStored_i)
```

A battery with charge 19 W-tick stored cannot deliver 200 W this tick even if its rate cap allows; the effective cap is 19 for that battery.

**Algorithm per Output network N (the elastic-share pass, `PowerAllocator.RunAtomic` step 4, run after DECIDE converges):**

1. Compute the residual rigid shortfall `S_N = RigidDemand_N + PullsGranted_N - GenSupply_N - InflowCommitted_N`, floored at 0. `PullsGranted_N` is the rigid input draw granted to contributors consuming FROM N, and `InflowCommitted_N` is the contributor throughput arriving ON N, both from the converged forward sweep (§8.0.3), so storage backfills only what generators plus upstream contributors left unmet.
2. If `S_N <= 0`: every battery and APC cell on N discharges 0 (share 0). Done. Locked or overload-tripped elastics always get share 0.
3. Else for each non-locked, non-overloaded battery / APC cell / umbilical on N, compute `effective_discharge_i` (= min(rate cap, cable cap, stored), §8.0.0.1).
4. Compute `effective_total = sum(effective_discharge_i)`.
5. If `S_N >= effective_total`: every supplier delivers its full `effective_discharge_i`. Residue `S_N - effective_total` is rigid demand still unmet (which the joint allocator §8 then treats as triggering shed/overload on suppliers).
6. If `S_N < effective_total`: proportional share, each supplier delivers `share_i = effective_discharge_i * (S_N / effective_total)`. By construction `share_i <= effective_discharge_i`, so no saturation occurs.

Worked example: 2 batteries on the same output network, both with discharge rate 200 W. Battery A is full (stored >> 200). Battery B has only 19 stored. Shortfall 300 W on the network.

- effective_A = min(200, full) = 200.
- effective_B = min(200, 19) = 19.
- effective_total = 219.
- `S_N = 300 >= effective_total = 219`, so each delivers its full effective: A = 200, B = 19. Sum 219. Residue 81 W rigid still unmet. The allocator (§8) then considers the rigid supply ceiling on N to be `G_N + 219`, and if `D_N > G_N + 219` triggers shed somewhere upstream.

Note we do NOT throttle Battery A to "equal share" (300/2 = 150). Battery A delivers its full capable amount; Battery B contributes whatever it can; the residue surfaces as unmet rigid demand to the allocator.

For shed planning (§8.0 iteration) the network's rigid supply ceiling is `G_N + sum_of_effective_discharge_i` plus inbound flow from non-shed upstream transformers. If rigid demand exceeds this ceiling, shed cascade or overload triggers.

### 7.3.0.1 Critical implementation note: SoftSupplyShareCache

**Without an explicit `Battery.GetGeneratedPower` postfix the elastic-cap rule is allocator math that never reaches ENFORCE's vanilla `CalculateState`.** The adversarial review confirmed this is the central gap that breaks the "no partial power" invariant. Same for APC's `GetGeneratedPower`.

The §7.3 algorithm sets a per-battery `delivered_i` per tick. To make this stick at ENFORCE, PowerGridPlus writes the value into a `SoftSupplyShareCache` (parallel to `SoftDemandShareCache` for charge demand) and adds Harmony postfixes:

```csharp
// Battery.GetGeneratedPower postfix
__result = Mathf.Min(__result, SoftSupplyShareCache.GetShare(__instance.ReferenceId));

// AreaPowerControl.GetGeneratedPower postfix (same shape)
// RocketPowerUmbilicalFemale.GetGeneratedPower postfix (same shape)
// RocketPowerUmbilicalMale.GetGeneratedPower postfix (same shape)
```

Cache lifecycle is a freshness-stamp model. The cache holds entries keyed by `ReferenceId` with payload `(long tickWritten, float share)`. ALLOCATE writes `(currentNetworkTick, share)` for every active supplier as the iteration converges. The `GetGeneratedPower` postfix reads the cache: if an entry exists AND `tickWritten >= currentNetworkTick - 1`, the postfix returns the cached share. Otherwise (entry older than one tick or absent), the postfix falls back to vanilla (`_powerRatio * stored`). Self-cleaning: stale entries from cable breaks or supplier reassignment are naturally distrusted on read; no explicit invalidation step is needed. In-memory only, not serialised.

With this cache + postfix in place, the §7.3 elastic discharge cap propagates to vanilla `_powerRatio` math. Without it, ENFORCE sees raw `PowerStored` as Potential, and any time `0 < PowerStored < rigid_demand` (a normal "battery almost empty" gameplay state) produces `_powerRatio < 1` -> partial-power on rigid devices.

### 7.3.1 Local-battery failsafe

A battery's discharge to its OUTPUT network is fully independent of any state on its INPUT network. If an upstream transformer feeding the battery's input net sheds, the battery cannot charge but its output side continues to discharge to the output network's rigid loads as long as it has stored energy.

Example:
```
N0 (generator)  ->  T1 (SHED)  ->  N1 (rigid lights, plus battery B input)
                                   B output -> N2 (rigid lights)
```

When T1 sheds: N1 has 0 inflow, N1's lights go dark (B's Input terminal is consumer-only and cannot back-feed N1). But N2's lights continue to run on B's discharge as long as B has stored energy. This is the "kept-alive by local battery" failsafe and is the natural consequence of the dual-terminal model (§7.1); no special-case code is required.

The symmetric case for APCs has the in-tick interlock caveat (§7.1): if the APC chose to charge from its input this tick, it cannot also discharge to its output this tick. Stationary batteries have no such interlock.

### 7.4 Battery as elastic demand on the Input network

A battery's `GetUsedPower(InputNetwork)` reports `PowerMaximum - PowerStored` (full headroom) in vanilla. PowerGridPlus treats the charge request as SOFT demand: GATHER enrolls each battery with `Request = min(EffectiveChargeCap, PowerMaximum - PowerStored)` (§7.6), the request propagates leaf-to-source through the same `BackwardDesirePass` as rigid demand, and the forward sweep grants it out of the firm residual only (§9). The granted share lands in `SoftDemandShareCache`, and the `Battery.GetUsedPower` postfix min-clamps the reported charge demand to it, so each battery charges exactly its grant. A battery is simultaneously a soft demander on its Input network AND an elastic supplier on its Output network. Per §7.1 the two roles run independently in the same tick when the two networks are distinct.

### 7.5 Splitting APC demand

`AreaPowerControl.GetUsedPower(InputNetwork)` returns passthrough + internal-charge bundled in vanilla. PowerGridPlus splits the two STRUCTURALLY at GATHER time (§8.0.0.1):

- **Passthrough** is the APC's routed seg (§5.0.3): its share of the output network's demand rides the (rigid, soft) demand vector like any other contributor, so the passthrough portion is never summed into the input network's rigid demand directly, and it needs no subtraction heuristics.
- **Internal-charge** is a separate Soft request on the input network: `Request = min(ComputeChargeCap(apc), cell.PowerDelta)`, where `ComputeChargeCap` is the configured `ApcBatteryChargeRate` bounded by the input cable's remaining headroom after the APC's own passthrough (`AreaPowerControlPatches.ComputeChargeCap`) and `PowerDelta` is the inserted cell's remaining headroom.

The APC is the one `QuiescentAlwaysOn` contributor (§8.0.3 pass 1): vanilla bills its 10 W quiescent whenever `OnOff && OutputNetwork != null` regardless of throughput, so the allocator funds an idle APC's bare quiescent as a quiescent-only pull (`Pull = 10, Throughput = 0`) and the patch bills it only under the same vanilla gate. The cell-charge portion bills 0 when the APC has no fresh `SoftDemandShareCache` share this tick (roster-absent: errored, short-circuited, or output-less), closing the unfunded phantom-draw hole the partial-power sentinel exposed (2026-07-07).

At ENFORCE the `AreaPowerControl.GetUsedPower` prefix rebuilds the input-side bill from fresh allocator figures: the quiescent `UsedPower`, plus the fresh total passthrough from `ApcPassthroughCache` (rigid + soft-charge flow crossing the APC, §8.0.4), plus the charge portion `min(ComputeChargeCap, cell.PowerDelta, SoftDemandShareCache share)`. The internal-charge portion is storage-charge flow (§9); the passthrough portion is rigid.

### 7.6 Soft-demand request value

Each soft-demand store declares a per-tick `Request`, capped three ways (charge rate cap, cable cap, remaining headroom), computed at GATHER per class:

- Stationary battery: `Request = min(EffectiveChargeCap, PowerMaximum - PowerStored)`, where `EffectiveChargeCap = min(per-prefab configured charge rate, input cable tier cap)` (`StationaryBatteryPatches.EffectiveChargeCap`). With `EnableBatteryLimits` off, the rate-cap term is unlimited and only the headroom binds.
- APC internal cell: `Request = min(ComputeChargeCap(apc), cell.PowerDelta)` (§7.5).
- Rocket umbilical cell: `Request = min(configured charge rate, input cable tier cap, PowerMaximum - PowerStored)` (`UmbilicalAdapter`, §5.0.3).

The request is a first-class SOFT demand on the store's input network: it aggregates into `Net.SoftRequestLocal`, rides the same backward/forward sweep as rigid demand (§8.0.3 / §9), and the granted share is published to `SoftDemandShareCache`, which the per-class `GetUsedPower` patches min-clamp against. There is no separate request pipeline and no shared headroom helper; the request computation lives in GATHER and the adapters as above.

## 8. The allocator

PowerGridPlus has exactly one power allocator (decision §0.8). It runs in the ALLOCATE phase (`PowerAllocator.RunAtomic`) and decides every shed, every overload, every elastic discharge share, and every storage-charge (soft) grant for the whole grid in one deterministic pass, against the fresh per-network `Required` / `Potential` that OBSERVE populated. There is no second strategy and no toggle: "the allocator" throughout this spec is the single implementation described here.

### 8.0 What the allocator does (overview)

`RunAtomic` runs in three stages, then a shared tail:

1. **GATHER** (§8.0.0.1): build the per-network rigid-demand and generator-supply numbers and the contributor / elastic / soft rosters from `SegmentingDeviceRegistry`.
2. **ORDER** (§8.0.2): a true topological (Kahn) order over the live contributor edges, replacing the deleted minimum-depth BFS. Publishes `ShallowFirstNetworks` for ENFORCE.
3. **DECIDE** (§8.0.3): an iterated fixed-point loop (`RunAllocationLoop`) that settles shed and overload and computes each contributor's exact in-tick throughput.
4. **Shared tail** (§8.0.4): dead-input cue, lockout commit (with the elastic-overload network retry), elastic-share pass (§7.3), and the publish tail that writes the share caches, the per-contributor presentation totals for every routed seg kind, the Powered-presentation and shortfall snapshots, and runs the conservation check (§8.8). Storage-charge grants are decided inside DECIDE's forward sweep (§9); the tail only publishes them.

Shed, overload, and cycle-fault interact non-trivially inside a single tick, which is why DECIDE iterates rather than running a single sweep:

- A shed transformer contributes 0 to its output network. Its siblings on the same output network now bear more load and may individually exceed their `Setting` -> OVERLOAD.
- An overloaded transformer also contributes 0. Its input network is no longer drawn from. Its siblings on the same INPUT network now have more headroom.
- A cycle-faulted device contributes 0 on both sides; cycle-fault is decided in the PROTECT phase (§4.5), so cycle-faulted contributors enter the allocator already marked `Locked` and conduct 0. The live graph the allocator sees is therefore a DAG.
- A battery on an output network whose rigid demand exceeds upstream supply plus the battery's discharge contribution is the elastic-supply ceiling case (§7.3); the loop accounts for it.

The whole iteration happens inside ALLOCATE of one game tick. From outside, one tick produces one joint result; the atomic invariant (§17.1) holds.

### 8.0.1 Overload is grow-only / sticky; only shed is re-decidable

This is the rule that makes the loop both correct and convergent.

- **OVERLOAD is grow-only (sticky).** The overload flags (`seg.Overloaded`, `e.Overloaded`) are cleared exactly once per tick, at loop entry in `RunAllocationLoop`, and NEVER inside a round. Once a round sets an overload it stays set for the rest of the tick, so an overload commits even if a same-tick shed in its subnetwork would have removed the condition. This is intended: overload is the structural signal the player must fix (a transformer that genuinely cannot serve its downstream), not a transient artifact of the iteration order.
- **SHED is re-decidable.** `ForwardSupplyAndShed` clears `seg.Shed` for every contributor at the top of each round and recomputes shed fresh against the settled state. So a shed that another device's overload later frees the budget for is retracted within the same tick; an unnecessary shed never freezes for 60 s.

Together these realize the fault precedence **CYCLE > VVF > OVERLOAD > SHED** (decision §0.3): structural overload is evaluated BEFORE the shed pass each round (§8.0.3), so a transformer that structurally cannot serve its downstream is diagnosed OVERLOAD, the higher-precedence fault, rather than getting shed first and mislabeled as input-starved.

**Resetting a sticky fault.** A committed overload (or cycle-fault, or VVF) clears only two ways: the 60-second lockout timeout (§10.2), or a player turn-off. `OffAsResetSweep` (§10.3) clears every fault registry for a device the player switched off; a switched-off device also leaves the allocator's roster entirely (the GATHER step skips `!OnOff` segmenters), so turning it back on re-introduces it and the fault re-detects on the next pass if it still holds. There is no mid-tick auto-clear of a committed overload.

### 8.0.0.1 GATHER: rosters and per-network numbers

GATHER walks `CableNetwork.AllCableNetworks` for the rigid numbers and `SegmentingDeviceRegistry.EnumerateSorted()` (sorted `ReferenceId ASC`, §8.0.5) for the structural rosters.

- **Per-network rigid demand + generator supply.** For every device on a network that is NOT a segmenter (segmenters are modelled structurally), `GetUsedPower > 0` adds to `RigidDemand` and `GetGeneratedPower > 0` adds to `GenSupply`. VVF-faulted producers already read 0 here (their enforcement postfix), so a silenced producer contributes no supply.
- **Contributors (`Seg`)**: `Transformer`, linked `PowerTransmitter` / `PowerReceiver` pairs (one `Seg` anchored on the PT, §6.2), and `AreaPowerControl`. Each carries the physical description its adapter produced (§5.0.3): its effective cap (`min(CapSetting, cable caps) - quiescent draw`, where `CapSetting` is `Transformer.Setting`, the pair's static link rating, or +inf for the APC), the input-draw factor `m`, plus the policy GATHER attaches: its `Priority`, its step-up flag (§5.2), and a `Locked` flag (true if cycle-faulted, in a prior-tick shed / overload window, or paired with a cycle-faulted partner half: it conducts 0 and is not re-decided).
- **Elastic suppliers (`Elastic`)**: a `Battery`, `AreaPowerControl` cell, or `RocketPowerUmbilical*` with stored energy, discharging onto its output network only to fill rigid shortfall. Effective discharge = `min(rate cap, cable cap, stored)`.
- **Soft demanders (`Soft`)**: a `Battery`, APC cell, or umbilical charging from its input network out of the firm residual only. Request = `min(charge rate cap, cable cap, headroom)` (§7.6).

The §5.0 segmenter list is exhaustive; any unhandled segmenting class is a hole through which `_powerRatio < 1` leaks on rigid downstream loads (§8.0.0.2 keeps vanilla scaling as the net under that hole). Battery / APC / umbilical are modelled as elastic suppliers and soft demanders rather than pull-through contributors; only Transformer, the PT/PR pair, and APC's passthrough are pull-through `Seg`s.

Each contributor class is classified by direction (§5.1), priority-sorted (§5.4), elastic-supply-capped (§7.3) where applicable, and shed / overload-eligible per §8.0.3. Catching every segmenting class is what stops the undersupply cases the adversarial review flagged (a battery on an undersupplied output net; an APC with a depleting cell; a PT/PR pair feeding past its effective cap, especially with a PowerTransmitterPlus distance derate; a rocket umbilical with a rigid load on the rocket side) from reaching ENFORCE's `CalculateState` as inflated `Required` and producing `_powerRatio < 1` on rigid loads. Every segmenting class also needs a `GetGeneratedPower` lockout postfix that returns 0 when in SHED, OVERLOAD, CYCLE_FAULT, or VARIABLE_VOLTAGE_FAULT, so a locked-out contributor truly contributes 0 to the enforced flow.

### 8.0.0.2 Vanilla `_powerRatio < 1` is retained as the safety net

Even after the spec's invariants are fully implemented, the vanilla `_powerRatio = Potential / Required` scaling code path is INTENTIONALLY LEFT INTACT. Rationale per the user: "I do want to keep the vanilla partial powered logic in case we miss something or another mod opens up a gap in our safety net, as that is better than producing phantom power or some other mod error."

PowerGridPlus's claim is that the vanilla partial-power branch becomes unreachable in normal gameplay under the redesigned model. If a third-party mod adds a device class that PowerGridPlus doesn't classify, OR a future game update introduces a new code path, vanilla scaling gracefully degrades the gameplay rather than producing phantom power or corrupted state. The presence of this safety net is itself an invariant (§17.35).

On allocator-managed networks the unreachability claim is monitored, not assumed: the partial-power sentinel (`PartialPowerSentinel.cs`, §8.8) reads every network's settled `_powerRatio` at the ENFORCE tail and counts any network the allocator marked SERVED and ratio-deprivable that shows a ratio below 1 (an under-advertising presentation cache, a billing regression, a device the roster missed). Unmet managed networks (Dry / Throttled / Deadlock) are exempt: there the sub-1 ratio IS the safety net doing its designed job while the device loop powers consumers off whole. Bridge-only networks (wireless carriers, hop nets) are exempt by derivation (§8.8). Always-on, exact counters, one aggregated warning per 600 ticks (invariant §17.45).

### 8.0.2 ORDER: topological (Kahn) order over live contributor edges

`BuildTopoOrder` builds a true topological order over the live contributor edges `InNet -> OutNet`, replacing the deleted minimum-depth BFS. Why topological and not min-depth: a diamond (a network fed through two contributor chains of different length) takes the shorter depth under a min-depth scheme, so the longer feeder's supply is not yet finalized when that network is processed, and the residual a multi-stage chain leaves is exactly the one-tick supply-propagation lag (§8.0.6). A topological order lands every network AFTER all the networks that feed it, diamonds included, so the backward / forward sweep sees fresh upstream supply at every depth with no residual lag.

Mechanics:
- In-degree counts only non-`Locked` suppliers. Cycle-faulted segments are `Locked` and excluded, so the live graph is a forest / DAG.
- Ready networks (in-degree 0) are popped in `ReferenceId ASC` order, and the unprocessed tail is kept sorted by `ReferenceId` as networks become ready, so the pop order is deterministic across MP peers.
- `Net.Depth` is set to the topo index (used to align contributor depth and as the iteration order).
- A residual cycle (should not occur after PROTECT-phase cycle removal) is appended in `ReferenceId` order with a `LogWarning`.

ORDER publishes `ShallowFirstNetworks` (the topo order, source -> leaf) for ENFORCE to iterate (§2 step ENFORCE reads it). Iterating ENFORCE upstream-first is what lets each network's `CalculateState` run after its feeders' `PotentialLoad` was refreshed this tick.

### 8.0.3 DECIDE: the iterated fixed-point loop

`RunAllocationLoop` iterates a backward / forward sweep to a fixed point. Flag lifecycle once per tick at loop entry: `seg.Shed`, `seg.Overloaded`, and `e.Overloaded` are all cleared once here. Inside the loop, SHED is re-decided every round and OVERLOAD only ever grows (§8.0.1).

Each round runs four passes in this fixed order:

1. **`BackwardDesirePass`** (leaf -> source, over the reversed topo order), carrying BOTH flow classes in one walk as a (rigid, soft) demand vector. RIGID: each contributor's `DesiredThroughput` is its share of its output network's residual rigid need (`RigidDemand + consumer pulls - GenSupply`, floored at 0; priority tier DESC, proportional by `EffCap` within a tier, capped at `EffCap`, §8.3.2); `DesiredPull` is the matching input-side draw (`throughput * max(InputDrawFactor, 1) + UsedPower`). An idle contributor whose class bills its quiescent whenever ON (`QuiescentAlwaysOn`, today only the APC, §7.5) presents a bare quiescent-only `DesiredPull = UsedPower`; every other idle contributor presents 0. SOFT: the network's soft desire (`SoftRequestLocal + active consumers' SoftDesiredPull`) splits over the SAME suppliers with the same tier-first proportional splitter, but each contributor's soft capacity is its residual headroom `EffCap - DesiredThroughput`, so soft never displaces rigid capacity; `SoftDesiredPull = SoftDesiredThroughput * max(InputDrawFactor, 1)` plus the quiescent draw ONLY when the contributor carries no rigid pull (the quiescent is carried exactly once, on the rigid pull when any rigid flows, else on the soft pull; in the forward pass the soft pull carries only the remainder the GRANTED rigid pull left uncovered, `max(0, UsedPower - Pull)`, so the seg invariant stays exact even when a quiescent-bearing rigid pull is granted partially). The soft split runs independently of the rigid residual: a net with zero rigid residual still routes charge. Eligibility differs by class: rigid desires gate on `DesireActive(s) = !s.Locked && !s.Overloaded` (shed is deliberately IGNORED so a previously-shed contributor still presents its desired pull and can be reconsidered), while soft desires gate on the stricter `IsActive` (also not shed): soft never drives a shed decision, so a shed contributor's charge desire must not size its suppliers, or the delivered soft would strand on its input net billed-but-unconsumed; an un-shed next round restores the desire one round later. Generators are subtracted first; elastic storage is the documented last resort (§7.3) and is not modelled in this pass (it absorbs the per-net shortfall in the forward sweep / elastic-share pass, matching the gen -> transformer -> battery supply order).

2. **`DetectStructuralOverload`** (desire-based, evaluated BEFORE shed). A network whose post-shed demand exceeds `GenSupply + AvailableElastic + sum of its non-shed suppliers' EffCap` overloads its `Setting`-limited suppliers (input-limited PT pairs included, taken offline). Suppliers are summed at `EffCap` even when already overloaded, so the condition keeps re-detecting them rather than oscillating (an overloaded supplier contributes 0, which would otherwise make the network look relieved). Cable-limited suppliers (`CapSetting > CableCap`) and APCs (no rating) are excluded; cable overflow is rule 3 below. This pass is desire-based and has no forward dependency, so it is safe to run before the forward pass; running it before shed is what gives a structurally-overcommitted transformer the OVERLOAD diagnosis instead of a SHED mislabel (§8.0.1, precedence).

3. **`ForwardSupplyAndShed`** (source -> leaf, over the topo order, so every supplier's `Throughput` is already final). For each network it computes the supply actually arriving (`firmIn = GenSupply + active suppliers' Throughput`, plus `AvailableElastic`) and `budget = avail - RigidDemand`. Shed is RE-DECIDED against the RIGID claims only: `seg.Shed` is cleared for every seg at the top of the pass (when not settle-only), then if the active consumers' desired rigid pulls exceed the budget victims shed whole (never partial) per the tier-major best-fit selection in `SelectShedVictims` (§8.3) until the rest fit; step-up segments never shed (§5.2); a network with no supply at all (`avail <= Eps`) sheds nothing (dead-input idle, §8.3.1). Survivors are then granted highest-priority-first, and each contributor's exact `Throughput`, `Pull`, and the per-net `InflowCommitted` / `PullsGranted` / `RigidServed` / `Unmet` are written. AFTER the rigid grants, SOFT (storage charge) is granted per network out of the FIRM residual only: the pool is `firmIn - RigidDemand - granted rigid pulls` floored at 0 (never elastic: a battery must not discharge to charge another store), plus the soft inflow arriving through the net's active suppliers (granted when THEIR input nets were processed earlier in topo order), capped by the weakest cable's remaining headroom (`CableMax.WeakestCapOnNetwork - (RigidServed + granted rigid pulls)`). One ratio `softRatio = min(1, softAvail / softDemand)` scales every local charge request and every active consumer's soft pull on the net; a consumer's granted soft throughput is further capped at its remaining headroom `EffCap - Throughput`, and a shed / locked / overloaded consumer gets zero soft. The per-net `SoftGrantedLocal` / `SoftPullsGranted` and per-seg `SoftThrough` / `SoftPull` are written. Shed decisions, budgets, and `Unmet` never see the soft class; unmet soft desire is silently clamped (§9.6).

4. **`DetectSupplyOverload`** (after the forward pass, because both rules read the forward pass's `Unmet` / `PullsGranted`). Two rules: the elastic hit-max (a network still `Unmet` after gen + inflow + full elastic discharge trips its live elastics, §8.4.1, so a storage-fed subnet goes dark cleanly), and the §5.7 cable overflow (flow above the weakest cable cap with generators alone under it trips every supplier + elastic on the network instead of burning the cable).

The **structural-overload-before-shed-before-supply-overload** ordering is load-bearing: a shed that relieves an over-demanded network is honoured in pass 3 of the SAME round, so pass 4 sees the reduced demand. Combined with grow-only overload (§8.0.1), this kills the shed <-> overload 2-cycle (§8.0.7).

**Convergence.** After the four passes, the `(shed, overload, elastic-overload)` RefId sets are collected. If they equal the previous round's sets, the loop has converged. If they equal the round-before-previous sets, the loop is in a 2-cycle between two states: the intermediate state's flags are OR-ed in (a safe superset, never under-protective), then one `BackwardDesirePass` + `ForwardSupplyAndShed(settleOnly: true)` settles throughputs without re-deciding, and the loop exits. The round cap is `2 * segs.Count + 4`; exhausting it logs a `LogWarning` and keeps the last settled state (internally consistent and safe, possibly not minimal). Only shed can oscillate (overload is monotonic), so the 2-cycle guard plus the union fallback is sufficient.

**The stranded-inflow clawback.** The fixed point deliberately keeps shed contributors visible to the backward desire pass (shed is re-decided every round, so a shed seg must keep presenting its claim to be reconsidered). The converged state can therefore carry inflow granted to fund a claim the same forward pass then shed: committed and billed upstream, taken by nobody. After the loop, the lockout commits, the dead-input rebuild, and the shortfall census (all of which read the deciding state), any tick that shed at least one contributor runs a clawback in `RunAtomic`: walking leaf to source, every network whose total committed inflow (rigid plus soft) exceeds its total consumption (served demand, granted pulls, charge, and soft pulls; soft counts because the soft stage funds charge from the rigid firm residual) takes the surplus back from its active suppliers in reverse grant order, shrinking each seg's published throughput and pull together (a fully clawed always-on-quiescent seg keeps its funded quiescent pull, `QuiescentAlwaysOn`, since vanilla bills that draw regardless of throughput) and landing the pull reduction on the supplier's input network before that network is visited. There is no re-split and no re-grant (a full settle re-pass was tried and re-granted the freed budget to other branches, reshaping real allocation on trip ticks), so every seg off the stranded chains keeps its deciding-pass numbers to the bit and the only real-world change is that nothing is billed upstream for power nobody consumes. No decision is re-opened.

### 8.0.4 The shared tail (commit, shares, publish, snapshots)

After DECIDE converges, `RunAtomic` runs a single shared tail against the converged per-net / per-seg fields:

1. **Dead-input cue** (§8.3.1): a contributor whose input network has no effective supply at all idles instead of shedding; if it is actively trying to pass power downstream it is flagged in `DeadInputRegistry` for a steady "no upstream supply" hover (no lockout, instant recovery).
2. **Commit lockouts.** For each non-`Locked` contributor not on a split-pending network (Option C, §4.3 step ALLOCATE), a `Shed` flag stamps `BrownoutRegistry` and an `Overloaded` flag stamps `OverloadRegistry`. Prior-tick lockouts (already `Locked`) carry unchanged.
3. **Elastic-overload network retry, then reset** (§8.4.1): a network with a newly overloaded elastic is a candidate. Before locking its overload cohort, the allocator retries at the network level: if the cohort's combined discharge would cover the residual demand the locks are cleared (recovered, no timer reset), otherwise one shared fresh expiry is stamped across the cohort (arms the 60 s lockout and keeps the cohort phase-synced). Transformers keep their per-device §8.4 timer; this retry is the elastic-specific, network-property branch.
4. **Elastic shares** (§7.3) -> `SoftSupplyShareCache`: per output network, batteries / APC cells / umbilicals cover only the residual rigid shortfall (`RigidDemand + PullsGranted - GenSupply - InflowCommitted`, floored at 0), proportional against effective caps.
5. **Storage-charge shares** -> `SoftDemandShareCache`: the per-device soft grants the forward sweep already computed (§8.0.3 pass 3 / §9). There is no separate distribution pass; the former surplus walk is deleted.
6. **Presentation totals for EVERY routed seg kind.** For each contributor the tail computes `TotalThrough = Throughput + SoftThrough` (output-side delivery, rigid + storage-charge flow) and `TotalPull = Pull + SoftPull` (input-side bill), which whenever the seg conducts equals `TotalThrough * max(m, 1) + quiescent` exactly, and writes `TransformerSupplyCache.Set(RefId, TotalThrough, TotalPull)` for `Transformer`, wireless PT/PR pair, AND `AreaPowerControl` alike. An inactive contributor (shed / overloaded / cycle-faulted) caches all-zero totals. An idle always-on-quiescent APC caches `(0, quiescent)`. Publishing totals for every kind is what guarantees a granted soft flow has a carrier on BOTH terminals of its segment: a battery charging behind a transformer or wireless pair sees the charge advertised downstream AND billed upstream in the same tick (the pre-rearchitecture rigid-only cache writes were the deadlock that left chargers idling at 0 W forever with no fault anywhere). The APC additionally publishes:
   - `ApcPassthroughCache.Set(RefId, TotalThrough)`: the fresh input-side passthrough bill, rigid + soft-charge flow crossing the APC (there is no `GrantThrough` field any more; `SoftThrough` subsumed it).
   - `ApcCellDischargeCache.SetShare(RefId, cellShare)`: the cell-only elastic share, so `DischargeSpeed` means the cell rate consistently across storage classes (deviation P9).
   - `SoftSupplyShareCache.SetShare(RefId, TotalThrough + cellShare)`: the bundled supply cap, because vanilla `AreaPowerControl.GetGeneratedPower` bundles passthrough with the cell (`AvailablePower`).
7. **Powered-presentation snapshot** (§10.6): the healthy-segmenter set (`healthy = IsActive && (conducting || idle on a supplied input with no unmet rigid demand)`; a pair publishes under both halves' ReferenceIds) plus the enrolled-seg roster carrying each seg's `TotalThrough` / `TotalPull` and its ledger-settle eligibility, swapped into `PoweredPresentation` by volatile reference like the share caches.
8. **Shortfall classification snapshot** (§8.8): every allocator net's end-of-tick RIGID state (Served / Dry / Throttled / Deadlock) into `ShortfallDiagnostics`, same volatile-swap publication.
9. **Conservation check** (§8.8, config-gated): per net, granted inflow must equal granted outflow within 0.5 W; per seg, `TotalPull == TotalThrough * max(m, 1) + quiescent`.

The reason the presentation caches exist is the vanilla `_powerProvided` accumulator, which is filled during the PREVIOUS tick's `ApplyState` and so lags the output-side delivery by one tick (see `Research/GameClasses/Device.md`). The allocator sizes upstream supply to each contributor's CURRENT pull, so billing a stale `_powerProvided` on the input would leave the input network short by the one-tick demand change. Each routed class is therefore reported from the fresh cache, unconditionally (no allocator-selection gate; decision §0.8):
- The `TransformerExploitPatches` prefixes: `Transformer.GetGeneratedPower` reports the `TransformerSupplyCache` output (`TotalThrough`, exact delivery); `Transformer.GetUsedPower` reports the cached input draw (`TotalPull`) instead of `min(Setting + UsedPower, _powerProvided)`. Conservation holds (input == output + conversion loss), so the free-power exploit stays closed.
- `AreaPowerControlPatches.GetUsedPowerPatch` bills quiescent (only when `OnOff && OutputNetwork != null`, the vanilla gate) + the `ApcPassthroughCache` passthrough (input == output, no lag) + the cell-charge portion capped to its `SoftDemandShareCache` share; a missing share (roster-absent APC) bills 0 charge (§7.5).
- `PowerTransmitterDrawPatches.GetUsedPowerPatch` reports the cached input draw (`TotalPull`) for the PT/PR pair (the transmitter bills the pair's input cable; the receiver's `InputNetwork` is null so it already returns 0), and its last-priority `DeliveryGatePatch` postfix on `PowerTransmitter.GetGeneratedPower` clamps the advertised wireless delivery to the granted `TotalThrough`, so delivery follows the grant and a transfer debt cannot be seeded by ungated delivery during a startup or shed transient.

**Battery and Umbilical are terminal storage, not pass-through, and need no in-tick throughput cache.** Their discharge onto an output network is gated to the elastic-share pass (`SoftSupplyShareCache`, §7.3) and their charge draw to the storage-charge grant (`SoftDemandShareCache`, §9), both computed fresh in-tick from the same supply-accurate converged sweep and already current. A PT pair's advertise is NOT left as headroom: the delivery gate clamps it to the granted `TotalThrough`, so both terminals of the pair carry the exact granted figures.

Both `TransformerSupplyCache` and `ApcPassthroughCache` are tick-stamped, in-memory, self-cleaning per-`ReferenceId` stores (same shape as `SoftSupplyShareCache`): a read older than one tick falls back to "no fresh value", so the reporting patch reports 0 (or its vanilla formula) until the allocator roster includes the device again. The allocator writes in ALLOCATE; ENFORCE (same tick) reads the current value; OBSERVE (before ALLOCATE) reads last tick's, both inside the one-tick window, and OBSERVE's transformer output does not feed the allocator's own model.

### 8.0.5 MP-safe ordering: integer-only keys

Every ordering decision in the allocator uses integer keys only. Floats are excluded because IEEE 754 sums are order-sensitive and bit-exact identity across peers is fragile; a float on a sort decision would be the first float dependence in the ordering path. The keys:

- **Topological order**: `ReferenceId ASC` tiebreak on ready-node pops (§8.0.2).
- **Supplier dispatch order** (`SupplierOrder`): `(priority DESC, ReferenceId ASC)`. Higher-priority contributors dispatch first.
- **Shed victim selection** (`SelectShedVictims`, §8.3): tier-major best-fit-decreasing over `(priority ASC, claim DESC quantised to whole Watts, ReferenceId ASC)`. Tiers go priority ASC and a tier is exhausted before the next is touched; within the tier, the smallest quantised claim that covers the remaining deficit alone sheds and ends selection (tie: lowest `ReferenceId`), else the largest claim sheds (tie: lowest `ReferenceId`) and the deficit shrinks. Claims floor to whole Watts via `(int)Math.Floor`; the deficit rounds up past the Eps tolerance via `(int)Math.Ceiling(deficit - Eps)`, so the selected set always restores claims under budget in float terms (never an under-shed that would trip the §8.4 elastic hit-max) at the cost of at most one extra small victim on sub-Watt boundaries. Every comparison is integer / `ReferenceId` only, and the selector is a pure static function of (candidates, deficit), pinned by ScenarioRunner's `pgp-shed-victim-fixture` synthetic cases (§8.3.3). It replaced the flat `(priority ASC, ReferenceId ASC)` walk, whose over-shedding cost §8.3.3 documents.

Quantising the claim to whole Watts erases any ulp-level float divergence between peers (Watts are O(10^3-10^5), well clear of float precision boundaries); the cast is at the comparator only, the underlying float drives physics. The within-tier proportional demand split (§8.3.2) is float, but is bit-identical across peers on the shared mono runtime, so it never makes a divergent ordering decision. `MergeDeterminismPatches` enforces deterministic network-id ordering on cable merges, so the keys stay stable across peers.

The same `ReferenceId ASC` enumeration (`SegmentingDeviceRegistry.EnumerateSorted`) is used everywhere PGP walks segmenting devices: the PROTECT-phase cycle DFS (§4.2.5), the PROTECT-phase producer-isolation walk (§8.5), and the allocator GATHER. One sort key across the whole tick; the gain is MP determinism with no float dependence anywhere in the ordering.

### 8.0.6 The `>=` power-met boundary

Vanilla `PowerTick.CacheState` sets `_isPowerMet = (Potential - Required) > 0f` (STRICT `>`), so a network whose `Potential` exactly equals its `Required` reads as NOT powered and its rigid loads go dark. The allocator reports exact throughput (no headroom), so a fully served network lands at `Potential == Required` and the strict test would darken it.

`PowerTickPatches.CacheState_PowerMetBoundary` is an UNCONDITIONAL postfix (no allocator-selection gate; decision §0.8). When `Required > 0`, the strict test had set not-met, and `Potential >= Required - Eps`, it sets `_isPowerMet = true` and `_powerRatio = 1`. It only nudges the exact-balance boundary; a genuinely short network (`Potential < Required - Eps`) is untouched, and vanilla semantics are otherwise unchanged. `BreakSingleFuse` / `BreakSingleCable` call `CacheState` again after lowering `Required` to a cable / fuse cap; the postfix re-runs correctly against the reduced figure.

**Tick-scoped read coherence.** The exact-balance guarantee above only holds if OBSERVE, ALLOCATE, and ENFORCE all see the same device outputs within one atomic tick; two main-thread mutators used to break that (the 2026-07-07 transition-dip finding, §8.8):

- **Solar first-read latch** (`SolarOutputLatchPatches.cs`): `ElectricityManager.SolarProcessing` rewrites one radiator's `GenerationEfficiency` per FixedUpdate frame, so on a large farm each panel's `GetGeneratedPower` is a step function that can step BETWEEN the allocator's grant and vanilla's ENFORCE read, leaving `Potential` below the granted `Required` for that tick. The latch caches each panel's first computed output per atomic tick (concurrent map keyed by ReferenceId + tick counter; gated on `RunSimulation`; cleared on world load) and serves repeat reads from the cache, so the efficiency step lands in the NEXT tick's allocation instead of tearing the current one. Main-thread readers (tooltips) may race the cache; the per-entry atomic swap means they see old or new, never torn.
- **Emergency-light toggle queue** (`EmergencyLightToggleQueue`, drained by the atomic tick prefix before OBSERVE): the emergency-light latch decisions used to fire `OnServer.Interact` mid-tick, adding or removing lamp + cell-charge demand between GATHER and ENFORCE. Decisions are now queued (one pending state per light, last wins) and issued at the next tick boundary, making the flips tick-atomic. The latch semantics themselves are unchanged.

### 8.0.7 Why this design (the two defects it closes)

Two concrete defects motivate the topological-order, re-decidable, exact-throughput design.

1. **One-tick supply-propagation lag (the multi-stage-chain oscillation).** On a multi-stage transformer chain under variable load, a min-depth order plus capacity-headroom advertising plus the lagging `_powerProvided` input draw let a downstream network's demand and its upstream supply drift one tick out of phase, so the chain flickers power on and off. The topological order (supply finalized before each network is read), exact throughput, the fresh per-class input-draw caches, and the `>=` boundary together close the loop within one tick: input equals output on every pass-through class, and a fully served chain stays powered at exact balance.

2. **The shed <-> overload 2-cycle ("60-second freeze").** A grow-only-everything loop can commit a shed AND an overload that are individually justified but jointly contradictory: device A sheds because its input looks short, which relieves device B's network so B's overload should clear, but if the overload were committed and never retracted within the tick, the relief that would have made A's shed unnecessary never registers, and both devices lock out for 60 seconds. The re-decidable shed (cleared and recomputed every round) plus the structural-overload-before-shed ordering settle A's shed and B's relief in the same round, so the contradictory pair is never committed. (Overload itself stays grow-only / sticky, §8.0.1: a transformer that structurally cannot serve its downstream is a real fault and is meant to commit; only an unnecessary SHED is retracted.)

### 8.5 Producer-isolation rule and VARIABLE_VOLTAGE_FAULT (strict-literal)

PowerGridPlus enforces a strict-literal rule: a producer's network may contain ONLY other producers and `Transformer` instances. Any other device on a producer's network is a violation. Explicitly: `Battery`, `AreaPowerControl`, `PowerTransmitter`, `PowerReceiver`, `RocketPowerUmbilicalMale`, and `RocketPowerUmbilicalFemale` do NOT satisfy producer-isolation, even though they are segmenting devices. Only `Transformer` does. Rigid consumers (lights, machines, IC10, etc.) and the other six segmenting classes are all violators.

What counts:
- **Producer**: SolarPanel (and Large / HeavyDouble variants), WindTurbineGenerator (and Large), PowerGeneratorPipe / GasFuelGenerator, PowerGeneratorSlot / SolidFuelGenerator (coal genny), StirlingEngine, RadioscopicThermalGenerator (RTG), TurbineGenerator (the small wall turbine), and PowerConnector (the portable-generator dock). Identified at runtime via `GetGeneratedPower(net) > 0` while the device is NEITHER an `ElectricalInputOutput` NOR a `WirelessPower`, OR by class-list lookup. (TurbineGenerator and PowerConnector were a §8.5 omission, added in deviation P11: both override `GetGeneratedPower`, so leaving them off would drop them to the unknown-producer cable-burn fallback per D6.) Note: `PowerConnection` is a DIFFERENT, vestigial dead-code class (§5.0.1) and is never a producer.
- **Transformer**: only `Transformer` instances (NOT Battery, NOT APC, NOT PT/PR, NOT RocketPowerUmbilical).
- **Violator**: any device on the producer's network that is neither a producer nor a `Transformer`. This includes rigid consumers AND every non-Transformer segmenting device class (Battery, AreaPowerControl, PowerTransmitter, PowerReceiver, RocketPowerUmbilicalMale, RocketPowerUmbilicalFemale).

**Active-source gate.** A producer is faulted only while it is an ACTIVE source (the form that actually feeds unregulated voltage), decided by `ProducerClassifier.IsActiveProducer`. A producer with an on/off button (gas / solid / stirling) counts while it is ON; a `PowerConnector` counts while its docked generator is delivering (the dock is a transparent proxy, it forwards the generator's power and has no source of its own, so an empty or switched-off dock is inert and never faults); a buttonless producer (solar / wind / wall turbine / RTG) always counts, since it cannot be switched off and produces whenever the environment allows. The connector reads the docked generator's raw `PowerGenerated`, not the connector's `GetGeneratedPower` (the VVF enforcement postfix zeroes that, so gating on it would oscillate). This is the VVF analog of the elastic-overload ON cohort (§8.4.1); an inactive producer is neither faulted nor counted toward the violation, so a producer that is off / not delivering is transparent to the rule exactly like a `Transformer` is.

Per-tick check, in the PROTECT phase right after the cycle-fault detector:

```
for each cable network N:
    active_producers_on_N = [d for d in N.DeviceList if IsActiveProducer(d)]      # the cohort (active-source gate)
    transformers_on_N     = [d for d in N.DeviceList if d is Transformer]
    violators_on_N        = [d for d in N.DeviceList if not IsProducer(d) and not (d is Transformer)]
    # NOTE: violator test is class-based IsProducer, so an INACTIVE producer (off generator, empty
    # dock) is neither a violator nor in the cohort -- it is transparent to the rule, like a Transformer.

    violating = active_producers_on_N and violators_on_N

    # The LOCK is committed per NETWORK, not per producer (retry-before-reset, synced expiry; see
    # "Network-level commit" below). A candidate net (a NEW violator this tick, or a toggle-requested
    # retry) either RECOVERS (clears the whole producer cohort if no longer violating) or RESETS
    # (stamps every active producer to one shared fresh 60 s expiry). The per-producer branch here is
    # only the VISUAL choice (flash vs hover) plus the unknown-producer cable-burn fallback:
    if violating:
        for each active producer P in active_producers_on_N:
            if P has a flashable on/off button:   attach red flash + extend the on/off button hover
            elif P has an extendable hover:        extend the device-body hover only (no flash)
        for each UNKNOWN producer-like U on N (producing but outside the known class list):
            find a cable adjacent to a violator
            BurnReasonRegistry.RegisterPending(cable, "Power producing devices can only connect to a transformer or other producers (adjacent <producer-label>)")
            cable.Break()
```

Examples:
- Solar panel + transformer on same net: VALID (canonical).
- Solar panel + solar panel + transformer: VALID (multiple producers ok).
- Solar panel + solar panel, no transformer, no consumers: VALID (producer-only network with nothing downstream; see "Producer-only network" below).
- Solar panel + battery: INVALID (battery is not a transformer; the rule requires a transformer between solar and battery).
- Solar panel + APC: INVALID (APC is not a transformer).
- Solar panel + light: INVALID (light is a rigid consumer).
- Solar panel + transformer + light: INVALID (light is on the producer's network; the rule requires the transformer to be the ONLY non-producer on the network).
- Coal generator + arc furnace: INVALID (arc furnace must be downstream of a transformer).
- Solar panel + faulted transformer T (CYCLE_FAULT or OVERLOAD): VALID (T is a `Transformer` regardless of its fault state; see "Faulted transformer counts" below).

The rule forces the canonical hub-and-spoke power topology: producers feed a heavy backbone with transformers; transformers feed distribution networks with batteries, APCs, and loads.

**Producer-only network (no transformer, no consumers).** A cable network that contains only producers (e.g. Solar wired to Solar with nothing else attached) is a valid harmless idle configuration. No producer-isolation violation fires; no VVF; no hover warning; no cable burn. The producers run, generate their `GetGeneratedPower` value, and the power has nowhere to go (so vanilla `Required = 0` and the surplus dissipates), but that is harmless. The producer-isolation walk fires only when `producers_on_N AND violators_on_N` is non-empty; a producer-only or producer-empty network never crosses that threshold.

**Faulted transformer counts.** The producer-isolation walk treats every `Transformer` instance as a `Transformer` regardless of its current fault state. A Solar wired to a `Transformer` T1 that is currently in CYCLE_FAULT (or OVERLOAD, or SHED) still satisfies the rule; the network has a producer and a Transformer, no violators, no VVF. T1's fault is its own concern: its `GetGeneratedPower` is 0 for the lockout window, so the producer's power has nowhere to flow until T1 recovers, but the player is not double-punished with a VVF on top of T1's existing red flash. The player traces the dead downstream back to T1, reads T1's hover ("Cycle Fault" / "Overloaded"), and fixes that. No special diagnostic on the producer.

Action choice (per producer):

- **Flash producers** (have on/off button): GasFuelGenerator, PowerGeneratorPipe, SolidFuelGenerator (coal), StirlingEngine. Get `ProducerFaultFlashBehaviour` attached (red flash, countdown hover on the button).
- **Hover-only producers** (no on/off button, but the player naturally hovers the device body to investigate why it is not producing): SolarPanel, WindTurbineGenerator, RadioscopicThermalGenerator (RTG). Get a `Thing.GetPassiveTooltip` postfix that appends the fault line + countdown to `__result.Extended`. No flash visual (nothing to flash); the player reads the fault from the hover.
- **Cable-burn producers** (no on/off button AND no useful hover): currently EMPTY after the hover-only path. The cable-burn fallback exists only for future producer classes that have neither flash nor a useful hover surface; none of the current vanilla producer set falls here. The existing `BurnReasonRegistry` + persistence (§11.6) remain ready if such a class appears.

When a producer enters VARIABLE_VOLTAGE_FAULT (flash or hover) it contributes 0 to its network for the lockout via a `GetGeneratedPower` postfix that returns 0 while the registry has the device locked.

**Network-level commit (mirrors the elastic-overload retry, §8.4.1).** VARIABLE_VOLTAGE_FAULT is stored per-producer (`VariableVoltageFaultRegistry`; each producer flashes / hovers / snapshots independently) but committed at the NETWORK level with a RETRY before any reset. A net is a commit candidate when an active producer NEWLY violates this tick, or a toggle requested a retry (`OffAsResetSweep` flags the net via `VariableVoltageFaultDetector.RequestRetry` when it clears a producer's lock, the OFF-as-reset edge). On a candidate net:

- **Recover.** If it no longer violates (a transformer was added, or there is no active producer / no rigid consumer left), the whole producer cohort's locks are CLEARED.
- **Reset.** If it still violates, every active producer is stamped to ONE shared fresh expiry (`currentTick + LockoutDurationTicks`), which arms the 60 s lockout and keeps the cohort phase-synced.

A stable all-locked violating net is NOT a candidate, so its synced timer counts down rather than being re-stamped (this is what prevents a frozen countdown on a buttonless producer). The retry makes toggling any buttoned producer a network-level recovery attempt for every producer sharing its network: a buttonless solar / wind on the same net clears together with the buttoned generator the player toggled, and the cohort either resolves (wiring now fixed) or re-faults on a fresh synced timer ("clear, then resolve or immediately fault again"). Like overload there is no free auto-recovery mid-lockout: a fix with no interaction clears at the 60 s expiry; a toggle (the §10.3 OFF-as-reset) is the instant retry. The commit is self-resolving (after it the net is either all-recovered or all-locked, so neither branch re-triggers next tick).

The hover-only mechanism is verified viable:
- SolarPanel's vanilla `GetPassiveTooltip` (decompile L400076-L400089) already writes `passiveTooltip.State = SolarInfo()` showing generation rate, efficiency, and health. Appending the fault line to `__result.Extended` stacks naturally below.
- WindTurbineGenerator and RTG have no vanilla `GetPassiveTooltip` override (blank tooltip from `Thing.GetPassiveTooltip` base at L300658). The fault line is the only content the player sees, which is acceptable: the player who hovers wind / RTG investigating "why is this producing nothing" gets a direct, immediate answer.
- `WorldCursor.Idle()` (L223086-L223198) re-polls `GetPassiveTooltip(hitCollider)` every frame while the player hovers, so the `{n}s` countdown updates smoothly (typically 60+ FPS).

### 8.6 Why this replaces the vanilla brownout

Without the producer-isolation rule, vanilla scales rigid devices proportionally when supply is short on a network with no upstream transformer to shed (the "irreducible brownout" case). With the rule, that configuration is itself a fault the player must address, eliminating the silent brownout. Trade-offs:

- Stricter: a player who built a 800 W solar panel directly feeding a 1500 W light cluster used to get dim lights; now they get a faulted solar panel (or burned cable) and a clear instruction to add a transformer.
- More structural: encourages the canonical hub-and-spoke power layout (producers -> heavy backbone -> transformers -> normal load networks).
- No silent brownouts: every undersupply scenario produces a visible fault somewhere.

The vanilla brownout path is therefore reserved for the case where the configuration is legal (producer connects only to segmenting devices, no direct rigid load) BUT the segmenting device's input network is still undersupplied. That case is handled by the upstream shed cascade (§8.0).

### 8.7 LogicType.ProducerFault = 6582

Read-only int LogicType, added by PowerGridPlus, next free slot after `CycleFault = 6581`. Reads 1 while the device is in VARIABLE_VOLTAGE_FAULT lockout, 0 otherwise. Server-derived, replicated to clients via the per-tick fault snapshot (`FaultRegistrySnapshotMessage` KindVariableVoltage). Exposed on every classifier producer that has a DEVICE-SPECIFIC logic surface: solar, both wind turbines, the small turbine, and the gas / solid-fuel / stirling generators (wind / turbine expose `PowerGeneration`, solar its tracking logic, so each is logic-readable). NOT exposed on `PowerConnector` (a dynamic-generator dock) or `RadioscopicThermalGenerator` (RTG, a rocket-internal component): neither declares a logic surface, so the read would be unreachable (see `Research/GameSystems/PowerProducerLogicReadability.md`). Both can still ENTER VVF (detection); it is hover-only / zeroed-output on them. The exposure resolves each producer's ACTUAL runtime `CanLogicRead` / `GetLogicValue` via `AccessTools`, so it follows a future override (e.g. on GasFuelGenerator) instead of breaking silently; an instance filter keeps the read off non-producers (and off PowerConnector / RTG) when a resolved target is a shared base method (deviation P10).

### 8.1 Goal

Decide which contributors (transformers, PT/PR pairs, APCs) enter shed or overload lockout this tick so that every surviving contributor's input network can satisfy its rigid demand AND every surviving contributor's output network demand is within its `Setting`. Minimise the count of subnets that go dark; satisfy higher-priority sibling subnets first; if siblings tie on priority, shed the one with the larger presented pull first.

### 8.2 Per-network inputs the shed decision reads

The shed decision reads the GATHER numbers (§8.0.0.1) and the converged backward-pass demand:

1. **Rigid demand** per network: the sum of `GetUsedPower` for rigid (non-segmenter) devices; soft-demand stores are excluded (their charge requests ride the soft class of the same sweep, §9), and an APC's input draw is split structurally so only the passthrough portion is rigid (§7.5). Stored as `Net.RigidDemand`.
2. **Generator supply** per network: the sum of direct generators' `GetGeneratedPower` (`Net.GenSupply`). Elastic suppliers are tracked separately (`AvailableElastic`, §7.3) and added as the documented last resort.
3. **Presented pull** per contributor: its `DesiredPull` from the backward desire pass (§8.0.3), the input-side draw it would need to serve its share of its output network's demand. This is the claim the shed decision weighs against the input network's budget.

There is no precomputed subtree-demand recursion; the backward desire pass over the topological order resolves each contributor's pull with downstream demand already folded in.

### 8.3 The shed decision (per input network)

Shed is decided in `ForwardSupplyAndShed` (§8.0.3), per input network, and re-decided every round (§8.0.1). For a network whose budget the active consumers' claims exceed:

1. The budget the network can pass to its consumers is `avail - RigidDemand` (floored at 0), where `avail = GenSupply + active suppliers' Throughput + AvailableElastic`.
2. If the sum of active consumers' claims (`DesiredPull`) exceeds the budget, `SelectShedVictims` picks the victim set for the whole deficit in one pass (tier-major best-fit-decreasing, §8.0.5): lowest priority tier first, and within a tier the smallest quantised claim covering the remaining deficit alone (else the largest claim, repeatedly). Every victim sheds whole (never partial).
3. Step-up contributors are never victims (§5.2); if only step-up / non-sheddable consumers remain, the budget is accepted as-is.
4. A network with NO supply at all (`avail <= Eps`) sheds nothing: its contributors idle (the dead-input carveout, §8.3.1), so a permanently-unsupplied input does not cycle 60-second lockouts.

Per-input-network scope (§5.4): claims compete only among siblings on the same input network. A high-priority contributor on one network is never weighed against an equal-priority contributor on a different network.

A shed committed at the shared tail (§8.0.4) writes the contributor's RefId into `BrownoutRegistry` with `_lockoutUntilTick = currentTick + 120` (60 seconds at 2 Hz). Within the tick the shed is provisional and can be retracted (§8.0.1); the commit happens once, after the loop converges.

### 8.3.1 Dead-input carveout and the no-upstream-supply cue

The shed decision (§8.3) sheds the lowest-priority consumers when an input network's supply is short. One case is carved out: an input network with NO effective supply at all (`GenSupply + InflowCommitted + AvailableElastic <= 0`, the same `avail` the forward pass computes). Shedding such a network's consumers would lock them out for 60 s and re-fire every expiry forever, since the condition is permanent until the input is powered. Instead, a contributor (transformer / APC / PT pair) on a dead input simply IDLES: it is not shed, takes no lockout, does not flash, and recovers the instant its input is powered (no lockout to wait out). This is a deliberate carveout (POWER_DEVIATIONS P7).

So the player still gets a signal, a dead-input contributor that is actively trying to pass power downstream (its modelled `Throughput > 0`) shows a steady, neutral-grey hover line `(No upstream supply)` with no countdown. It is an INFO cue, not a fault: lowest hover precedence (any real fault on the same device wins), it never drives the flash, and it carries no 60 s timer. The set is recomputed every tick from the converged allocator state (`DeadInputRegistry`) and mirrored to clients via the per-tick fault snapshot (`KindDeadInput`), so the cue shows identically on every peer. Batteries and rocket umbilicals are pure suppliers, not pull-through consumers, so they do not receive the cue.

### 8.3.2 Parallel-supplier demand split (priority-tiered, proportional within a tier)

The shed victim order (§8.3) and the §8.4 overload trigger settle which suppliers drop, but not how a MET demand divides across parallel suppliers (transformers / APCs / PT pairs) feeding one output network. The backward desire pass (§8.0.3) splits it greedy by PRIORITY TIER, proportional by capacity WITHIN a tier:

- **Across tiers (greedy by priority).** Higher-priority suppliers fill before lower ones. A high-priority bank carries the load to its combined cap; lower-priority banks engage only once it is maxed. This makes priority a primary/backup control, consistent with its shed meaning (high priority delivers first and sheds last).
- **Within a tier (proportional by EffCap).** Suppliers at the SAME priority each deliver `tierGive * EffCap_i / sum(EffCap)`, so equal-priority suppliers share the tier's load in proportion to capacity (a 50 kW and a 5 kW transformer at equal priority split ~10:1, not 50/50). The split is self-bounding (each share <= its own EffCap) and conserves the tier total.

Interaction with §8.4: a tier delivering its full combined cap has every member at `EffCap` (all overload-eligible if demand is still unmet); a tier delivering less than its cap has every member at the same sub-cap fraction (none overload). So within a tier, overload is all-or-nothing, and across tiers the fully-engaged higher tiers are the ones that can trip. Determinism: the tier ORDER is integer-keyed (priority DESC, RefId ASC, §8.0.5); the within-tier proportional division is float but bit-identical across peers on the shared mono runtime, so the §8.4 discriminator probes still behave identically on every peer.

The SAME splitter carries the soft class (§9): a network's soft desire divides over the same priority tiers with the same proportional rule, except each supplier's within-tier weight is its residual headroom `EffCap - DesiredThroughput` instead of `EffCap`, so soft never displaces rigid and a rigid-saturated supplier takes no charge flow. Because soft rides the same priority tiers, a high-priority contributor's downstream storage charges before a low-priority one's; this is a deliberate behaviour change from the deleted priority-blind surplus pass (§9).

### 8.4 Overload: per-transformer hit-max trigger

An overload trips a transformer that is delivering at its active `Setting` cap while the output network still has unmet rigid demand. The trigger is per-transformer, not per-network. Note: a player who reduced Setting below OutputMaximum via IC10 will see overload fire at the lower threshold; this is intentional (they explicitly asked the transformer to throttle below its rated max).

Concrete check per transformer T after DECIDE settles:

- `T.actual_throughput == T.Setting`, AND
- T's output network has unmet rigid demand (`rigid_demand > total_supply_arriving_on_T.OutputNetwork`).

When both hold, T enters `OverloadRegistry` with the 60-second lockout. Other transformers feeding the same output network are NOT auto-tripped; each one trips only if it independently meets the hit-cap condition.

Examples (assuming Setting = OutputMaximum at default):

- T1 (Setting 500 W) and T2 (Setting 500 W) feed an output network with rigid demand 1500 W. Both run at 500 W (their Setting) with 500 W unmet downstream. Both trip.
- T1 (input-cable-throttled to 200 W of its 500 W Setting) and T2 (Setting 500 W) feed an output network with rigid demand 900 W. T1 runs at 200 W (below its Setting), T2 runs at 500 W (at Setting). Only T2 trips. T1's actual throughput is bounded by cable cap, not by its own Setting, so the OverloadRegistry trigger does not fire for it.
- IC10 example: a player IC10 script writes `Setting = 200` on T1 to throttle it. T1's effective_cap drops to 200 - UsedPower. If downstream demand exceeds 200, T1 trips OVERLOAD (it hit its Setting with unmet demand). The player can later raise Setting back to OutputMaximum to clear the constraint.

Rationale for the per-transformer trigger: the player-facing diagnostic is "this transformer is being asked to push past its rated throughput." A throttled-by-upstream transformer is not the cause and should not be punished. The hit-max transformer is the visible culprit and the one the player needs to upgrade or split.

For PT/PR pairs the overload threshold is the pair's static link rating bounded by its cables (`CapSetting = static_cap`, §6.3) as the equivalent of `Setting`. The same hit-the-cap-with-unmet-demand condition applies on the OUTPUT side. The pair's INPUT-side draw is a separate matter (`delivered * m` under PowerTransmitterPlus, §8.4.2). PT/PR Priority follows the same rules as wired transformers (the knob writes Priority; the pair has no writable Setting of its own).

**Input-limited pairs (historical deviation P6, superseded by the static rating).** An earlier build sized the pair from the live deliverable (`min(rated cap, InputNetwork.PotentialLoad)`) and needed an input-limited carve-out (`Seg.InputLimited`) that routed source-bottleneck shortfalls to SHED instead of OVERLOAD. The 2026-07-02 rearchitecture replaced the live read with the static link rating (§6.3), so the pair's cap no longer tracks the source potential and the carve-out is gone (`InputLimited` no longer exists). An undersupplied source now surfaces through the normal channels: the forward sweep sheds the pair when its input network cannot fund its `delivered * m` pull (§8.3), or idles it without a lockout on a dead input (§8.3.1). The structural-overload rule treats the pair as Setting-limited (its `CapSetting <= CableCap` by construction, §6.3), so only downstream rigid demand genuinely exceeding the rating-bounded `EffCap` trips OVERLOAD; input-limited pairs caught by that rule are taken offline with it. Either way the pair goes offline whole (no-partial-power); the diagnosis matches the actual bottleneck.

The `OverloadRegistry` is keyed by `ReferenceId` (per-device, consistent with `BrownoutRegistry`). No NetworkId-keyed variant is needed; the elastic re-arm sync in §8.4.1 is commit-time phase alignment of per-device entries, not a separate network-keyed store.

### 8.4.1 Elastic supplier overload: network-level retry and synced re-arm

§8.4's hit-max trigger is per-transformer. The same OVERLOAD outcome extends to elastic suppliers (Battery, AreaPowerControl, RocketPowerUmbilical*) so storage-fed networks honour the no-partial-power invariant: when a network's COMBINED effective discharge from all its elastic suppliers (Σ `min(rate cap, cable cap, stored)`) still leaves rigid demand unmet, every elastic supplier on that network trips OVERLOAD (the elastic hit-max rule of `DetectSupplyOverload`, §8.0.3), contributes 0, and the subnet goes dark cleanly instead of partial-powering. Two batteries that individually fall short but together cover the load do NOT trip: the forward sweep sums their discharge (`AvailableElastic`) before the `Unmet` test, and only a genuinely unmeetable load trips them.

Unlike the per-transformer trigger, the elastic OVERLOAD is a NETWORK property ("this network's storage cannot meet its load"). `OverloadRegistry` stays per-device (keyed by `ReferenceId`; each device flashes / hovers / snapshots independently), but the commit is network-level with a RETRY before any reset. A net is a commit candidate when at least one of its elastics newly overloads this tick. Its overload cohort is every elastic on the net overloaded this tick or already overload-locked (all ON, since the §10.3 OFF-as-reset sweep clears the lockouts of switched-off devices every tick). The commit then:

- **Retry.** If the cohort's combined effective discharge would cover the net's residual demand, the situation has recovered (the load dropped, a device was toggled back on, or supply was added), so the cohort's overload locks are CLEARED and they rejoin next tick. No timer is reset.
- **Reset.** Only if the retry still leaves demand unmet are the cohort's per-device locks (re)stamped to ONE shared fresh expiry (`currentTick + LockoutDurationTicks`), which both arms the 60 s lockout and keeps the cohort phase-synced.

The shared expiry matters because the per-device timers can otherwise drift out of phase (one toggled off then on, or one locked first by an unrelated fault), after which the suppliers re-arm one at a time, each re-overloading alone because its siblings are still locked, leaving a network they could JOINTLY power dark forever. Syncing guarantees an individually-too-weak-but-jointly-sufficient bank always re-arms together. The retry makes a player toggle (or any supply change) an immediate network-level recovery attempt rather than a blanket timer reset: toggling one device on retries the whole on-cohort at once and recovers the net the same tick if they jointly suffice, and only eats a fresh 60 s if they genuinely cannot. The commit is self-resolving (after it the net is either all-recovered or all-locked, so neither branch re-triggers next tick); devices locked for CYCLE_FAULT / SHED are not in the overload cohort; a split-pending network defers its commit (Option C). Transformers keep their per-device §8.4 timer (the culprit transformer is genuinely device-specific; this is a network property).

### 8.4.2 PT/PR pair input-side draw (distance overhead)

The §8.4 overload check is an OUTPUT-side test (the pair delivering its full deliverable with downstream still unmet). The pair's INPUT side needs separate accounting because the deliverable and the source draw are not equal under PowerTransmitterPlus: the source network is billed `delivered * m`, where `m = 1 + k * distance_km` is PowerTransmitterPlus's source-draw multiplier (§6.3; `m = 1` under vanilla or with PowerTransmitterPlus absent, where delivered == drawn).

The allocator therefore models a PT/PR pair's demand on its INPUT network as `throughput * m + quiescent`, not `throughput + quiescent`, and bounds the input cable at `input_cable_cap / m`. It reads `m` from PowerTransmitterPlus by reflection (`PowerTransmitterPlusInterop.SourceDrawMultiplier`, a soft dependency returning 1 when PowerTransmitterPlus is not loaded). Without this, a long link feeding off a constrained source network is under-counted by a factor of `m`: the allocator believes the input is covered, sheds nothing, and ENFORCE then computes `_powerRatio < 1` on the input network and brown-outs every rigid device on it, the exact no-partial-power violation the design exists to prevent. With it, the source network's true `delivered * m` draw enters the shed decision, so an unaffordable link sheds cleanly (its input subnet goes dark, not partial) and the input cable is protected against the inflated current.

This is the PowerGridPlus-side half; PowerTransmitterPlus charging the source for distance is by design. The static link rating (the §8.4 threshold) is unchanged by this section: downstream rigid demand exceeding the pair's rating-bounded cap is the OVERLOAD trigger, while a source that cannot fund the pair's `delivered * m` pull sheds it through the normal §8.3 budget math (the former live-cap input-limited carve-out, deviation P6, is superseded; see §8.4).

### 8.3.3 Shed victim selection example (best-fit-decreasing)

Three sheddable siblings T_a, T_b, T_c on the same input network, all priority 100, ReferenceIds ascending in that order. Their presented pulls (`DesiredPull`, rounded to whole Watts) are 500, 1000, 2000 respectively. The input budget the network can pass is 2500 W, so the deficit is 1000 W.

`SelectShedVictims` (tier-major best-fit-decreasing, §8.0.5) looks for the smallest single claim that covers the 1000 W deficit: T_b (1000) covers exactly, so T_b sheds and selection ends. T_a and T_c survive; one device sheds where the earlier flat (priority ASC, ReferenceId ASC) walk shed T_a (500) and then T_b (1000), two victims where one sufficed.

When no single claim covers the deficit, the largest claim sheds first and the rule re-applies to the remainder within the tier (claims 500 and 700 against a 1000 W deficit shed 700 then 500); only when a tier is exhausted with deficit remaining does selection move to the next tier. The policy and both ReferenceId tie rules are pinned by ScenarioRunner's `pgp-shed-victim-fixture` synthetic cases, which drive the pure selector directly with the exact numbers above.

### 8.8 Self-diagnostics: conservation check, shortfall classification, unknown-bridge census, partial-power sentinel

Four diagnostic surfaces watch the allocator. None of them feeds back into any decision: allocation math, ordering, and cache contents are untouched by all four.

**Conservation check** (`ConservationChecker.cs`, gated by `Server - Diagnostics` / `Enable Conservation Check`, default true). Audits the converged grants at the end of ALLOCATE, so a violation is by definition a PowerGridPlus bug, never a player problem. Two invariants:

- **Per network: granted inflow == granted outflow within 0.5 W.** Outflow = rigid demand served + local storage charge granted + rigid pulls granted + soft pulls granted. Inflow = supplier rigid throughput + supplier soft throughput + granted elastic discharge + the generator power the grants imply, derived as the residual `outflow - non-generator inflow` clamped to `[0, GenSupply]` so both failure directions surface: a residual above `GenSupply` means power was granted out of nothing, a negative residual means a contributor was granted throughput nobody consumes (billed upstream, wasted). Unused generator capacity is curtailment, not a violation.
- **Per contributor seg: `TotalPull == TotalThrough * max(m, 1) + quiescent` whenever the seg conducts** (`TotalThrough > 0.01`). A non-conducting seg is checked one-sided (`TotalPull <= quiescent + tolerance`): a grant below the quiescent draw carries zero throughput and may bill any partial amount up to the quiescent. Any drift is a code bug (a double-billed quiescent, a lost distance factor, a class granted on one terminal only).

Warnings are throttled to once per network / per seg per 600 ticks (about 5 minutes at the 2 Hz power tick) and carry a per-component breakdown.

**Shortfall classification** (`ShortfallDiagnostics.cs`). ALLOCATE labels every allocator net's end-of-tick RIGID state and publishes one immutable snapshot per tick by volatile reference swap. The byte values are a cross-assembly contract with the ScenarioRunner census (renumbering breaks its buckets):

- **Served** (0): no unmet rigid demand.
- **Dry** (1): unmet, with every remaining feed genuinely exhausted: each active supplier is saturated or draws from an input net that retained no undelivered supply. Source-side shortage (dead-input chains, unaimed solar islands); honest darkness.
- **Throttled** (2): unmet, with some feed valve deliberately closed: a locked / shed / overloaded supplier, a zero-effective-capacity supplier (a `Setting = 0` "firewall", or rate-limited to zero), or a locked / overloaded elastic on the net. Honest darkness (the player or a fault closed the valve).
- **Deadlock** (3): unmet while the allocator's own accounting says supply existed: an ACTIVE supplier had headroom above 0.5 W AND its input network retained undelivered supply. On a correct allocator this is impossible (an unmet net's suppliers either sit at their caps or drained their inputs); it is the invisible-deadlock regression shape and must be zero on a healthy build (invariant §17.44). Checked BEFORE the throttle rung so a genuine routing failure is never masked by an unrelated closed valve on the same net.

Diagnostics only: nothing in the mod reads it back; the `pgp-rearch-suite` census (§18) joins the snapshot via reflection. A net absent from the snapshot was outside allocator scope this tick.

**Unknown-bridge census** (`UnknownBridgeCensus.cs`). On the first atomic tick after a world load (armed at plugin load, re-armed on every world load), every scene device that is an `ElectricalInputOutput` subclass but neither in `SegmentingDeviceRegistry`'s known set nor described by any adapter (§5.0.3) is reported, one Info line per TYPE. Reporting is the ONLY handling: an unknown bridge keeps its vanilla power methods unpatched and GATHER sums it as plain rigid demand / generation on each side (the conservative fallback, §8.0.0.2). The census makes the gap visible instead of silent when a third-party mod ships its own two-port power device.

**Partial-power sentinel** (`PartialPowerSentinel.cs`, always-on, no config entry; same posture as the ledger audit, §10.7). The no-partial-power contract detector: at the ENFORCE tail, after every ApplyState has settled, it reads each network's final `_powerRatio` and counts a violation on any network that is (1) in this tick's `ShortfallDiagnostics` snapshot, (2) classified Served, and (3) ratio-deprivable: it has at least one plain (non-segmenter) power device, or a storage charge request this tick. The third gate is derived from vanilla ApplyState: the ratio's only effects are the `usedPower *= _powerRatio` billing scale and the power-on/off ladder, so only a plain consumer (scaled delivery, then powered off) or a charging store (`Battery.ReceivePower` adds the ratio-scaled chunk stream to `PowerStored`, so delivered charge = grant * ratio) can be deprived of granted power. A network whose every power member is a routed segmenter is inert and OUT of contract: bills and advertises are cache-governed, downstream delivery is governed by each member's own published cache on its output network, and Powered is reconciled at the ENFORCE tail. That excludes every wireless carrier network by construction (membership is the two dish halves; under the billing handshake its mirrors are structurally asymmetric, unclamped receiver drain vs delivery-gate-clamped advertise, so ratio below 1 is its normal conducting state, live-proven inert by a full-rate charge soak at carrier ratio 0.27) and the tower-top hop networks (receiver + transformer only). The violation threshold is exact (`ratio < 1f`, no invented epsilon): every healthy vanilla path assigns the literal 1f, and the mod's CacheState postfix (§8.0.6) forces 1f whenever supply is within 0.01 W of demand. Counters are exact and never throttled (violation ticks, violating net-ticks, distinct nets, worst and latest captures with net id / ratio / Required / Potential / tick); one aggregated warning per 600 ticks while new violations arrive; zero violations produce zero log lines. Positive control: the ScenarioRunner `pgp-partial-power-injection` scenario under-advertises one supplier's published totals on an in-scope net for a bounded window and asserts the counters rise and the worst / latest captures name the net, then that recovery silences it. The always-on APC quiescent is funded by the allocator (§8.0.3) and errored transformers report a 0 bill under mitigation, so an idle or errored segmenter can no longer hold a served network one quiescent below its vanilla Required (the 2026-07-07 natural-sun soak finding); tick-scoped read coherence (the solar first-read latch and the emergency-light toggle queue, §8.0.6 material) removes the transition-dip class the same soak exposed.

### 8.9 Status

The allocator described in §8.0 is the sole power path: there is one allocator, no toggle, and no alternate strategy (decision §0.8). In-game validation on the dedicated server is tracked separately (see `PLAYTEST.md`); this spec does not carry dated dedi results.

## 9. Storage-charge flow (the Soft class)

> Supersedes the former "surplus distribution pass" design of this section (2026-07-02, decision §0.9): the separate priority-blind surplus walk is deleted; storage charge is the SOFT flow class riding the same backward/forward sweep as rigid demand (§8.0.3).

### 9.1 Goal

Charge every storage cell (stationary battery, APC cell, rocket umbilical cell) out of supply that rigid demand does not need, across any number of intermediate contributors, without ever displacing rigid load, tripping a fault, or double-counting a request. There is no second accounting system: soft is the second component of the one (rigid, soft) demand vector the allocator sweeps.

### 9.2 Where charge power comes from

Soft is funded per network by the FIRM residual only: `GenSupply + active suppliers' rigid Throughput - RigidDemand - granted rigid pulls`, floored at 0, plus the soft inflow arriving through the net's active suppliers. Elastic discharge NEVER funds charging: a battery must not discharge to charge another store. The pool is additionally capped by the weakest cable's remaining headroom on the network (`CableMax.WeakestCapOnNetwork - (RigidServed + granted rigid pulls)`), so a charge grant cannot push a network past its tier rating. A consequence of the funding rule: a soft grant on a network implies every rigid pull there was granted whole (a partially granted rigid pull leaves a firm residual of exactly zero).

### 9.3 Request flow upstream (the backward half)

Every storage cell raises a `Request` (§7.6) on its input network, summed into `Net.SoftRequestLocal`. The `BackwardDesirePass` (§8.0.3 pass 1) carries the soft class leaf-to-source in the SAME walk as rigid: `net.SoftDesire = SoftRequestLocal + sum of active consumer segs' SoftDesiredPull`, split over the net's suppliers by the same priority-tier-first, proportional-within-a-tier splitter (§8.3.2), with each contributor capped at its residual headroom `EffCap - DesiredThroughput` (soft never displaces rigid capacity), and `SoftDesiredPull = SoftDesiredThroughput * max(m, 1)` plus the quiescent draw only when the contributor carries no rigid pull (the quiescent is carried exactly once). The proportional split is what kills the deleted surplus walk's double-count by construction: parallel contributors DIVIDE a downstream request instead of each propagating it whole. A locked / shed / overloaded contributor propagates zero soft desire.

### 9.4 Grant flow downstream (the forward half)

`ForwardSupplyAndShed` (§8.0.3 pass 3) grants soft per network AFTER the rigid grants, out of the §9.2 pool: one ratio `softRatio = min(1, softAvail / softDemand)` scales every local charge request and every active consumer's soft pull on the network, and a consumer's granted soft throughput is further capped at its remaining headroom `EffCap - Throughput`. Because networks are processed in topological order, the soft inflow a network receives through its suppliers was granted when THEIR input networks were processed, so a charge flow crosses any chain of contributors within the single tick, with no propagation lag and no iteration beyond the DECIDE loop it already rides.

### 9.5 Storage, enforcement, and presentation

The final per-device charge share is written to `SoftDemandShareCache[ReferenceId]`; the `Battery.GetUsedPower` postfix, the `AreaPowerControl.GetUsedPower` prefix (charge portion only, §7.5), and the rocket umbilical patches min-clamp the reported charge demand to it, so each store charges exactly its grant. The charge flow crossing each contributor is part of that contributor's published `TotalThrough` / `TotalPull` (§8.0.4), so the flow is PRESENTED: it has a carrier on both terminals of every segment it crosses, and network `PotentialLoad` / `RequiredLoad` mirrors, hover tooltips, and IC10 reads of transformer / wireless throughput include storage-charge flow, not just running machines. (The pre-rearchitecture design granted charge in a side walk that published only APC figures; a granted-but-unpresented charge flow behind a transformer or wireless pair was a permanent fault-free deadlock, chargers idling at 0 W forever.) During ENFORCE's re-`CalculateState` the postfixes return the allocated values, `Required <= Potential` holds on every network by construction, vanilla `_powerRatio` stays 1, and nothing flickers.

### 9.6 Faults never fire for soft

Every fault decision (shed §8.3, structural overload §8.0.3, elastic supply overload §8.4.1, cable overflow §5.7, and the dead-input cue §8.3.1) evaluates the RIGID component only. Excess soft desire is CLAMPED, never a fault: a 5 kW transformer asked for 50 kW of battery charge passes 5 kW of charge with no overload, no shed, no lockout, and no hover cue; unmet charge simply waits for headroom. Charge never displaces rigid: the backward split caps soft at each contributor's post-rigid headroom (§9.3) and the forward grant funds it from the post-rigid firm residual (§9.2). Because soft rides the same priority tiers as rigid (§8.3.2), a high-priority contributor's downstream storage charges before a low-priority one's; this is a deliberate behaviour change from the deleted priority-blind surplus walk.

## 10. Protection state machine

### 10.0 Tick-rate constants are literal

All lockout-duration constants in the protection state machine are literal integer tick counts (e.g. `currentTick + 120` for a 60-second lock at 2 Hz). The literal `120` is NOT derived from a `TICK_RATE_HZ` variable, and there is no runtime tick-rate query. Assumes the vanilla electricity tick rate of 2 Hz. If a game update changes the electricity tick rate, every literal in §10.1, §10.2, §4.5, §8.3, §8.5, §11.2 must be reviewed and adjusted manually. The 60 second figure itself is a human constant, not a performance constant: it is sized to player movement and deduction speed (walk to the device, read the flash / hover / IC10 state), so a tick-rate change adjusts the literals to PRESERVE 60 seconds, never to re-tune the duration.

### 10.1 Shed lockout

- Triggered by §8.3 cascade.
- Stored in `BrownoutRegistry._lockoutUntilTick[refId] = currentTick + 120` (120 ticks × 0.5 s = 60 seconds).
- `BrownoutRegistry.IsLockedOut(refId, tick)` returns true while `tick < until`.
- Auto-clears when timer expires; next allocator pass re-decides.
- Timer-only. Mid-cooldown topology fixes (player rewires upstream, raises a transformer's Setting, adds a generator) do NOT shorten the timer. The full 60 s runs to completion; the player either waits or uses the OFF-as-reset (§10.3) for an instant manual retry.
- No early release, deliberately. The 60 second lockout gives the player time to troubleshoot intermittent issues that would otherwise disappear before being noticed; an early-release mechanism would erase the evidence of transient faults. The timer-only rule above is not a simplification awaiting a fix; it is the point.
- The 60 second duration is player-movement and deduction-speed oriented: long enough to physically walk to the faulting device and read the flash / hover / IC10 state before it clears. It is not a tunable performance constant (§10.0).
- Consequence: the cold-start boot shed observed on a masters-on load is the system working as designed. A deep chain whose upstream supply has not yet propagated sheds instantly (the no-partial-power contract demands whole-device shedding on transiently wrong numbers too), and the lockout that follows is the diagnostic affordance, not a defect. No arming window or clean-tick hysteresis is planned; the former "cold-start shed trap" TODO entry is resolved by this ruling.

### 10.2 Overload lockout

- Triggered by §8.4 detection.
- Stored in `OverloadRegistry._lockoutUntilTick[refId] = currentTick + 120`.
- Parallel API to shed: `IsLockedOut`, `IsOverloaded`, `NoteOverload`, `ClearLockout`, `ClearAll`.
- Timer-only, same as shed. Topology fixes during the lockout window (player rewires downstream, splits load across more transformers) do NOT shorten the timer.

### 10.2.1 Timer-only invariant applies to all four faults

CYCLE_FAULT and VARIABLE_VOLTAGE_FAULT follow the same rule: the 60 s countdown runs to completion regardless of mid-window topology changes. The lockout window is not re-validated against current topology mid-flight; only the post-timeout allocator pass re-checks. Rationale: re-checking topology every tick during the lockout would let the player observe the fault flicker if their fix is marginal (e.g. cycle that briefly opens then re-closes). The timer-only model gives a clean 60 s "this is broken, fix it and either wait or toggle OFF/ON" signal.

The optimisation invariant in §17.39 (skip-while-faulted) is refined: a faulted device contributes 0 to power flow for the entire window (so allocator math naturally excludes it), but cycle DFS WALKS through it as if conducting when checking for NEW cycles formed by mid-window rewires. Only the original-loop re-check skips faulted participants; newly formed loops that route through a faulted device are still detected, with new participants getting fresh 60 s timers and existing fault timers untouched. Producer-isolation behaves the same way: a producer already in VVF skips its own topology re-check this tick, but a different producer newly violating because of mid-window rewire is still caught.

### 10.3 OFF-as-reset

When a player toggles a transformer (or PT) OFF during a flash:
- `SwitchOnOffShedPatches.RefreshColorState_Prefix` detects `OnOff == false && (shed OR overload)`.
- Server-side: calls `BrownoutRegistry.ClearLockout(refId)` and `OverloadRegistry.ClearLockout(refId)`.
- Client-side: calls `SetClientShedding(refId, false)` and `SetClientOverloaded(refId, false)`.
- Returns true so vanilla updates the button material to off-state.
- Server's `SyncShedTransitions` / `SyncOverloadTransitions` on the next tick broadcast the cleared state to clients.

When the player toggles ON again, the next allocator pass re-evaluates. If conditions still warrant shed/overload, the lockout re-fires instantly.

**Server-side authority: the OFF-as-reset sweep.** The `SwitchOnOffShedPatches` path above is the client-side visual clear; it runs in the rendering path, which a headless dedicated server never executes. The authoritative clear is `OffAsResetSweep`, run every tick in the SETUP / OBSERVE phase (before the PROTECT detectors re-evaluate, §2). It walks the devices currently in any of the four lockouts and clears all of a device's lockouts when the player has switched that device OFF.

A device counts as switched-off only when it actually has an on/off control and that control is off: `HasOnOffState && !OnOff`. This distinction matters because a buttonless device (one whose prefab carries no `InteractableType.OnOff` interactable: solar panels, both wind turbines, the wall turbine, the RTG, the bare power-connector dock) reports `OnOff == false` permanently. That is the absence of an on/off concept, not an OFF gesture, so such devices are NOT swept. Sweeping them would clear and immediately re-note their VARIABLE_VOLTAGE_FAULT every tick, freezing the hover countdown at its full value instead of letting it count down on its own 60 s timer. Which producers carry an OnOff interactable is catalogued in `Research/GameSystems/PowerProducerOnOffState.md`: the three fuel generators (gas / solid / stirling) do, and are reset normally; the other six producers do not.

**Power Connector dock special-case.** The Power Connector is a buttonless dock that forwards a docked portable generator's power, so its own `OnOff` is permanently false while the real source is the docked `DynamicGenerator` (a `DraggableThing`, never itself on the cable network; the connector is the network producer). The sweep treats the connector as reset-eligible exactly when it is NOT delivering, `!ProducerClassifier.ConnectorIsDelivering`: no generator docked, or the docked generator's raw `PowerGenerated` is 0 (off or out of fuel). With a generator delivering and misconfigured the connector's VVF counts down normally; the moment the player switches the generator off (or pulls it from the dock) the next tick clears the fault, matching the player's actual OFF gesture. The access (`PowerConnector.ConnectedDynamicGenerator?.PowerGenerated`) is public, no reflection, and the presence check is a plain reference test (the sweep runs on the power-tick worker, so it avoids the Unity `(bool)`/`==null` native operator). Reading the generator's raw `PowerGenerated` rather than the connector's enforcement-zeroed `GetGeneratedPower` keeps the signal stable while the connector is faulted (no oscillation). Clearing the connector's lock here also flags its network for the §8.5 network-level retry, so a buttoned producer toggled on the same net retries the whole cohort.

### 10.4 Visual restoration

`BrownoutFlashBehaviour` on shed/overload exit:
1. Calls `RestoreBaseline()` to drop the orange material.
2. Calls `ForceVanillaRefresh()` which invokes `SwitchOnOff.RefreshColorState` via reflection. Vanilla picks the correct material from current `(OnOff, Powered, HasPowerState, Error)`. Avoids the green-button-while-off bug.

### 10.5 Save/load behaviour: fault states are transient

All four fault registries (`BrownoutRegistry`, `OverloadRegistry`, `CycleFaultRegistry`, `ProducerFaultRegistry` for VARIABLE_VOLTAGE_FAULT) clear on save load and recompute on the first tick after load. None of the `_lockoutUntilTick` countdowns are serialised. Rationale: faults are auto-clearing diagnostics with a 60-second wall-clock semantic; if the underlying topology is still broken, the first post-load tick re-fires the fault, and if the player fixed it offline, the post-load state is correctly clean. Persisting a half-elapsed countdown across an arbitrary-length save gap has no useful meaning.

This is the unified rule across CYCLE_FAULT, VARIABLE_VOLTAGE_FAULT, OVERLOAD, and SHED: every fault timer is in-memory only.

**No grace period on save load.** The first post-load tick runs the full allocator pass. Any violation that is still present in the loaded topology re-fires its fault with the full 60 s `_lockoutUntilTick = currentTick + 120` lockout. There is no shorter "warm-up" timer, no grace tick, and no half-elapsed countdown carried over. A player who saved with a cycle, an overload, or a producer-isolation violation present loads into the same world, sees the full red countdown the moment the first electricity tick runs, and accepts that the save was made with a violation in place. The OFF-as-reset (§10.3) remains available immediately after load if the player wants to retry without waiting.

The `BurnReasonSideCar` (§11.6) is the only fault-related state that DOES persist across save / load. It records per-instance hover reasons on already-broken `CableRuptured` wreckage, which is durable game state, not a transient lockout countdown.

### 10.6 Powered presentation policy (healthy routed segmenters)

Vanilla `PowerTick.ApplyState` derives a device's Powered flag from what it consumed this tick, and its only un-power path is gated on `Device.AllowSetPower(net)` (ApplyState is the flag's sole writer in the game). Under fresh-pull billing (§8.0.4) a HEALTHY routed segmenter that idles at zero throughput (a charger transformer whose batteries are full, an idle dish pair) bills a fresh pull of 0, so vanilla flips it to Powered=False and players read "device broken" on a device the allocator is routing normally. Policy: a routed segmenter the allocator considers healthy presents `Powered = True` even when idle (`PoweredPresentation.cs`).

**Healthy** (published per tick by the ALLOCATE tail, §8.0.4): enrolled in this tick's contributor roster, carrying no fault (not cycle-faulted, not shed, not overloaded, this tick or from a prior-tick lockout), and either conducting (`TotalThrough > 0`) or idle on an input network that has effective supply and no unmet rigid demand. A wireless pair publishes health under BOTH halves' ReferenceIds so transmitter and receiver present the same verdict. An idle segmenter on a DARK input (a night-time solar feed) is deliberately NOT healthy: vanilla un-powers it, matching the dead-input hover cue (§8.3.1). Faulted, dead-input, switched-off, and unenrolled devices keep exact vanilla Powered behaviour.

Mechanics, two halves around vanilla's own writer:

- **Block the False edge.** `AllowSetPower` postfixes on `Transformer` / `AreaPowerControl` / `PowerTransmitter` / `PowerReceiver` (`Patches/PoweredPresentationPatches.cs`) flip the gate to false for any device in the healthy set, so ApplyState can never un-power a healthy segmenter (and no False/True double transition, with its replication churn, ever happens).
- **Raise the True edge.** The ENFORCE tail (`PoweredPresentation.ReconcileEnforceTail`, §2) calls the vanilla `SetPowerFromThread(net, true)` for each healthy segmenter still reading Powered=False. The write self-marshals to the main thread, so the rising edge lands with one frame of marshal lag (an idle healthy charger can read Powered=False for the single tick spanning the marshal, e.g. right after world load); it fires only on a real False -> True transition, so a steadily healthy segmenter causes zero per-tick traffic.

Server-authoritative by construction: ApplyState and the ENFORCE tail run only inside the host's atomic tick, and `SetPower` routes through the normal interactable replication, so clients mirror the host's flag. On a client peer the published healthy set stays empty and the postfixes no-op. Between world load and the first atomic tick the set is also empty, so everything reads unhealthy and vanilla behaviour applies (the safe default).

### 10.7 Ledger adoption (`_powerProvided`)

The private per-class `_powerProvided` ledger on `Transformer` / `AreaPowerControl` / `PowerTransmitter` / `PowerReceiver` is vanilla's deferred billing handshake (`UsePower` charges it output-side, `GetUsedPower` bills it, `ReceivePower` repays it). The game never zeroes it; at 0.2.6403 it is not serialized into saves (no SaveData member carries it, per the write-site census in `LedgerAuditPatches.cs`), so a nonzero value is runtime accumulation within the session, residue from an older-version save, or an external writer. Under fresh-pull billing (§8.0.4) vanilla's restoring force is gone: vanilla bills `min(cap, ledger)` so residue self-drains through later bills, while a fresh-pull bill never drains it, so the ledger degenerates into a residue accumulator nobody owns. Concretely: a conducting transmitter drifts `-((m - 1) * TotalThrough + quiescent)` per tick under the billing handshake (the free-energy credit class; a long-running session accumulated a stranded -176,226 W credit on one transmitter, a dormant free-power source the moment vanilla billing reads the ledger again), and a multi-provider transformer input leaks a slow positive plateau (the input repayment is chunked per provider while the quiescent is subtracted per chunk). PowerGridPlus owns billing for these devices, so it also owns their ledgers (`LedgerAdoption.cs`), both lanes host-only inside the atomic tick:

- **World-load sweep** (`RunSweepIfPending`, §2 step 1): on the first atomic tick after a load (armed at plugin load, re-armed on every world load), zero the ledger on every modelled segmenter, both signs, NaN included, BEFORE the first OBSERVE can serve a stale value through the vanilla `GetUsedPower` fallback. One Info line per zeroed device plus a summary.
- **Per-tick ENFORCE-tail settle** (`SettleEnforceTail`, §2 step 4): after all ApplyState mutations, each enrolled, settle-eligible segmenter's ledger is SET to its vanilla-equivalent standing value: `Transformer := TotalThrough` (one tick's output drain awaiting billing, which also makes the `LogicType.PowerActual` read equal the documented "current throughput", §12); `PowerTransmitter := 0` (its fresh bill was already paid in full this tick); `PowerReceiver := min(debt, TotalThrough)` (preserves the load-bearing relay cycle, whose lifted `GetUsedPower` drains the transmitter's advertise on the wireless network and drives both halves' beam visuals, shearing only the stuck overshoot above one tick's throughput; negative or non-finite standing values clamp to 0). Writes are skipped inside a 0.01 W tolerance. Per-tick negatives therefore never survive a tick; each is counted silently as the free-energy metric.
- **APC exempt from the per-tick settle** (swept at load only): vanilla `AreaPowerControl.UsePower` drains the internal cell against a POSITIVE ledger one tick after the cell covers an output shortfall (the ledger is the cell-discharge carrier), and its `GetUsedPower` takes `Max(ledger, quiescent)` so a negative never discounts a bill; settling it per tick would hand the cell free energy. The `Transformer` is settled only while `EnableTransformerExploitMitigation` is on (mitigation off leaves vanilla owning the transformer ledger).
- **Exact ledger audit** (always-on, no config entry): the settle makes each owned ledger's lifecycle deterministic, so violations are detected exactly instead of by threshold. Layer B: the settle records the post-settle value; at the start of the next atomic tick the field must equal it EXACTLY (nothing legitimate writes `_powerProvided` between the settle and the next ApplyState; the complete write-site census lives in `LedgerAuditPatches.cs`). Layer A+: Priority.First/Priority.Last wrappers bracket all six vanilla write sites (Transformer / PowerTransmitter / PowerReceiver x UsePower / ReceivePower); observed deltas accumulate into a per-device double shadow sum, each mutation's BEFORE must equal the last recorded AFTER (a discontinuity is a foreign write between two known operations), and at the ENFORCE tail the pre-settle field must equal boundary + shadow within 0.01 W (a miss is a foreign write on an unobserved path). A NaN/Infinity pre-settle value is the fourth class; the settle repairs it. Counts are exact and never throttled; the tick boundary emits ONE aggregated warning per 600 ticks while new anomalies arrive, carrying per-class totals since load and the worst offender. Zero anomalies produce zero lines. There is no corrective clamp beyond the settle itself; the audit only reports. Tracking follows the settle set: the sweep clears it on world load, devices leaving the enrolled set drop out at the settle tail.

Field access: wireless halves route through the PowerTransmitterPlus ModApi debt accessors (`GetTransferDebt` / `SetTransferDebt`) when the ModApi tier resolved (§6.6), else through cached `FieldInfo` on the vanilla field (which exists with or without PowerTransmitterPlus); `Transformer` / `AreaPowerControl` use cached `FieldInfo` directly. The audit wrappers read the injected field directly, which today is bitwise-identical to the ModApi route (`GetTransferDebt` is a plain field read); a future PowerTransmitterPlus build that transformed the value would surface as boundary anomalies. Any external reader of the field sees at most one tick's standing value per ledger.

## 11. Visual feedback

Four distinct failure modes (after the producer-fault addition in §11.5). Colour identifies the family of cause; hover text identifies the specific cause and includes a live countdown.

### 11.1 Colour and hover text inventory

| Fault | Colour | Hex | Hover text template |
|---|---|---|---|
| SHED | orange | `#ffa500` band | `(Shedding: Insufficient upstream supply! {n}s)` |
| OVERLOAD | red | `#ff2626` | `(Overloaded: {device clause} {n}s)` (device-specific, see below) |
| CYCLE_FAULT | red | `#ff2626` | `(Cycle Fault: This device is part of a loop! {n}s)` |
| VARIABLE_VOLTAGE_FAULT | red | `#ff2626` | `Variable Voltage Fault: connected to <ClassName> without transformer ({n}s)` |

`{n}` is the remaining lockout countdown in seconds with two-decimal precision (e.g. `4.32s` or `4,32s` per locale). Recomputed on every `Thing.GetContextualName` / `Thing.GetPassiveTooltip` poll. Ticks down continuously from 60.00 to 0.00.

`{device clause}` in the OVERLOAD hover is device-specific: a single "transformer limit" template would be wrong on a battery, APC, or umbilical, which can also enter OVERLOAD (§8.4). The clause names the constraint the hovered device actually hit, resolved by the C# type of the instance the player is pointing at:

- `Transformer`: `Downstream demand exceeds this transformer's limit!`
- `Battery` (stationary batteries and subclasses): `Downstream demand exceeds this battery's discharge rate!`
- `AreaPowerControl`: `Downstream demand exceeds this APC's output!`
- `RocketPowerUmbilicalMale` / `RocketPowerUmbilicalFemale`: `Downstream demand exceeds this umbilical's discharge rate!`
- `PowerTransmitter` / `PowerReceiver`: `This power link cannot carry the downstream demand!` (the pair shares one message; a PowerReceiver aliases to its PowerTransmitter for fault state per §6.2, but the hovered instance selects the clause)
- any other overload-capable device: `Downstream demand exceeds this device's limit!` (generic fallback)

Implementation: `FaultHover.OverloadClause(Thing)`, called from `FaultHover.TryGetLine`. The hovered instance is threaded in from both hover patches (`FaultHoverPatches` body tooltip, `TransformerHoverErrorPatches` OnOff-button contextual name).

`<ClassName>` in the VVF hover names the offending violator: the device on the producer's network that triggered the producer-isolation walk. Implementation: the PROTECT-phase producer-isolation walk records the violator class name(s) onto the producer's `ProducerFaultRegistry` entry as it marks the producer for VVF. The hover formatter reads it for display. When multiple violators exist on the same network, the registry stores the violator class names in walk-order; the hover formatter renders the first two by walk-order separated by `, `, with a trailing `, ...` if more than two are present. Example renders: `connected to Battery without transformer`, `connected to Battery, AreaPowerControl without transformer`, `connected to Battery, AreaPowerControl, ... without transformer`. The class name is the C# class short name (`Battery`, `AreaPowerControl`, `PowerTransmitter`, etc.), not the localized prefab display name, so the message is unambiguous for cross-language players reading reports.

Renamed from VARIABLE_VOLTAGE_FAULT to VARIABLE_VOLTAGE_FAULT throughout the spec: it conveys the WHY (producers have variable / unregulated voltage and need a transformer as regulator) to the player better than the prior name.

The three red faults (overload + cycle-fault + producer-fault) share the colour because all three indicate a "structural / topology" problem the player must fix by rewiring or resizing. Orange (shed) is reserved for the "your upstream supply is insufficient" case which the player addresses differently (add generators / batteries / reduce load).

Burned cables get their own per-instance hover text via the existing `BurnReasonRegistry` + `BurnReasonPatches` (see §11.6).

Player review and edit: the four hover-text templates above are the single source of truth. Adjustments to wording go here; subsequent code reads from this spec.

### 11.2 Live countdown computation

The countdown `{n}` is computed at hover-text poll time. Tick-based math gives only 0.5-second granularity, which is too coarse for the requested two-decimal display. PowerGridPlus uses wall-clock interpolation between tick events for the display value only; the underlying fault state remains tick-driven for MP determinism.

```csharp
// Capture wall-clock start when the fault is first noted (per-device).
faultStartTime[refId] = Time.realtimeSinceStartup;

// At hover-poll time:
float elapsed = Time.realtimeSinceStartup - faultStartTime[refId];
float remaining = Mathf.Max(0f, 60f - elapsed);
string displayText = $"{remaining:F2}s";   // "4.32s"
```

The display value drifts continuously between ticks. The actual fault-clearing decision is still tick-based (`tick >= lockoutUntilTick`), so the registry's tick comparison is unaffected. Player visibility is purely cosmetic.

The Stationeers UI re-polls `Thing.GetContextualName` and `Thing.GetPassiveTooltip` via `WorldCursor.Idle()` every frame (60+ FPS), so the centisecond display updates smoothly without any timer subscription.

Decimal separator: locale-driven. The countdown formats as `tickRemaining.ToString("0.00", CultureInfo.CurrentCulture)`. EU locales (nl-NL, de-DE, fr-FR, etc.) render as `4,32s`; US / UK locales render as `4.32s`. The display matches the player's system culture; no override is applied.

When a player toggles OFF (OFF-as-reset, §10.3) the registries clear immediately and the next hover poll shows the device's normal-state text (no fault prefix).

### 11.3 Pulse and renderer machinery

Pulse rate for all four faults: 2 Hz.

Renderer discovery (shared): walks the `InteractableType.OnOff` `Interactable`'s `Animator` GameObject subtree for `MeshRenderer`s. Falls back to name-substring match (`OnOff`, `Button`, `Switch`) if the precise path fails.

Material strategy (shared): per-renderer Material instance (clones the on-state material with `_EmissionMap` cleared so the tint shows through). Original sharedMaterial is restored on exit, then `RefreshColorState` is invoked to ensure correctness against current state.

Implementation note: the existing `BrownoutFlashBehaviour` already does this work on Transformers. PowerGridPlus generalises it (renames internal `_transformer` to `_device` of type `Thing`) so the same behaviour attaches to every segmenting device class.

### 11.4 Flash visuals on every segmenting device

Every segmenting device class with a clickable `InteractableType.OnOff` interactable gets a flash behaviour attached at registration. Verified producer / segmenting device coverage:

| Class | OnOff button? | Flash attaches? |
|---|---|---|
| Transformer | yes | yes |
| Battery | yes | yes |
| AreaPowerControl | yes | yes |
| PowerTransmitter | yes | yes |
| PowerReceiver | yes | yes |
| RocketPowerUmbilicalMale | yes | yes |
| RocketPowerUmbilicalFemale | no (no clickable OnOff interactable on the device) | no -- hover-only path (§8.5 hover-only producer pattern reused: a `GetPassiveTooltip` postfix appends the fault line + countdown). The Female side has no visual flash; fault state is communicated only by hover text on the device. |
| SolidFuelGenerator (coal) | yes | yes (producer-fault path) |
| GasFuelGenerator | yes | yes |
| StirlingEngine | yes | yes |
| SolarPanel | no | no (hover-only path per §8.5; `GetPassiveTooltip` postfix) |
| WindTurbineGenerator | no | no (hover-only path per §8.5) |
| RadioscopicThermalGenerator (RTG) | no | no (hover-only path per §8.5) |

PowerConnection is vestigial dead code (§5.0.1) and is not listed: it never participates in any registry, fault, or flash.

When a fault should fire but the device has no flashable button, PGP uses the hover-only path for SolarPanel / WindTurbineGenerator / RTG / RocketPowerUmbilicalFemale (a `GetPassiveTooltip` postfix appends the fault line + countdown to `__result.Extended`). The per-cable burn-reason path (§11.6) is reserved as a third-tier fallback for future producer classes that have neither a flashable button NOR a useful hover surface; no current vanilla class falls there.

### 11.5 Precedence among multiple simultaneous faults

A device can in principle hit more than one fault state on the same tick. Precedence for the flash colour and hover-text:

1. CYCLE_FAULT (highest)
2. VARIABLE_VOLTAGE_FAULT
3. OVERLOAD
4. SHED (lowest)

Only ONE fault appears in hover text per tick: the highest-precedence active fault, on a single line with its countdown. Lower-precedence faults that are also active are NOT shown -- no stacking. The flash uses the highest-precedence fault's colour (CYCLE_FAULT red, VARIABLE_VOLTAGE_FAULT red, OVERLOAD red, SHED orange `#ffa500`).

IC10 readings (`Shedding`, `Overloaded`, `CycleFault`, `VariableVoltageFault`) all read independently of precedence; a device in two states simultaneously reads 1 on both relevant LogicTypes. Only the hover text and flash collapse to the highest-precedence fault.

### 11.6 Burned-cable hover text (existing infrastructure, currently broken)

PowerGridPlus has `BurnReasonRegistry.cs` and `Patches/BurnReasonPatches.cs` that attach a per-instance reason string to each `CableRuptured` wreckage:

- `RegisterPending(cable, reason)` is called before `cable.Break()`.
- A Harmony Postfix on `CableRuptured.OnRegistered` consumes the pending reason via `LocalGrid` lookup and attaches it to the wreckage via `ConditionalWeakTable`.
- A Harmony Postfix is INTENDED to inject `<color=#ffa500>Burned:</color> {reason}` into the hover tooltip on `CableRuptured` instances.

**Bug, fixed in Phase 0.2 of POWERTODO**: the existing Postfix is attached to `Thing.GetPassiveTooltip`, but `Structure : Thing` overrides this method, and `CableRuptured : SmallGrid : Structure` virtual-dispatches the call to `Structure.GetPassiveTooltip`. The Postfix never fires for wreckage hovers in-game. Same virtual-dispatch trap the `TransformerHoverErrorPatches.cs` file header documents explicitly. The fix retargets the Postfix at `Structure.GetPassiveTooltip` and, if a secondary clobber by `Tooltip.SetValuesForInteractable` is observed, adds a parallel re-apply Postfix on that method too. See POWERTODO Phase 0.2 for the concrete patch.

The same trap applies to the planned `ProducerFaultHoverPatches` for SolarPanel / WindTurbineGenerator / RTG: each producer class's `GetPassiveTooltip` must be patched directly (Harmony's class-hierarchy walk handles classes that inherit the method without overriding it), not via the Thing base.

Existing burn-reason strings (already in code):
- `Overloaded -- sustained network throughput exceeded this cable's rating ({MaxVoltage} W)` (Power tick rolling-average burn)
- `Wrong voltage -- {label} was bridging incompatible cable tiers`
- `Wrong voltage -- {CableType} cable was bridging into a different cable tier`
- `Wrong voltage -- the adjacent {label} doesn't accept {CableType} cable on this port`
- `Wrong voltage -- the adjacent {label} doesn't accept {CableType} cable`

New burn-reason string for §8.5 producer-fault fallback (added):
- `Power producing devices can only connect to a transformer (adjacent {producer-label})`

Save/load: per-instance burn reasons survive save/load via the `BurnReasonSideCar` pattern (added). Implementation follows the established side-car convention used by `PrioritySideCar`, `PassthroughSideCar`, `AutoAimSideCar` (PowerTransmitterPlus), and `GlowSideCar` (SprayPaintPlus).

Files:
- `Mods/PowerGridPlus/PowerGridPlus/BurnReasonSideCar.cs`: data store + serializer + ZIP read/write helpers. Side-car ZIP entry name: `pwrgridplus-burnreason.xml`. Serializes `BurnReasonSideCarData { List<BurnReasonEntry { long ReferenceId; string Reason }> Entries }`.
- `Mods/PowerGridPlus/PowerGridPlus/Patches/BurnReasonSaveLoadPatches.cs`: three Harmony patches.
  - Save: Postfix on the private `SaveHelper.Save(DirectoryInfo, string, bool, CancellationToken)` overload. Snapshots `_attachedByReference` on the main thread (prefix), then writes the side-car XML into the save ZIP after the archive seals (postfix).
  - Load: Postfix on `XmlSaveLoad.LoadWorld`. Reads the side-car XML from the extracted temp dir.
  - Per-instance restore: Postfix on `Thing.OnFinishedLoad`. Filters `__instance is CableRuptured`, looks up its `ReferenceId` in the loaded dictionary, calls `BurnReasonRegistry.RestoreFromSideCar(wreckage, reason)`.
- `BurnReasonRegistry.cs` (modified): adds a parallel `ConcurrentDictionary<long, string> _attachedByReference` updated alongside the existing `_attached` `ConditionalWeakTable`. The mirror is needed for snapshot purposes because `ConditionalWeakTable` is not enumerable on .NET Framework 4.7.2. Adds `SnapshotAttached()` and `RestoreFromSideCar(Thing, string)` methods.

Versioning: `BurnReasonSideCar` follows the established SprayPaintPlus / PowerTransmitterPlus side-car pattern. No `<Version>` field. No schema version number. No migration code. On parse failure (corrupt XML, structural mismatch, IO error) the load Postfix wraps the read in `try` / `catch`, calls `LogWarning` on the exception, and sets `LoadedBurnReasons = null`. The per-Thing tooltip Postfix treats a null `LoadedBurnReasons` (or a missing `ReferenceId` lookup) as "no side-car" and falls back to the generic burned-cable hover text. This is the same posture every other PGP-style side-car uses, so a future schema change rolls out via a clean read failure on the old shape rather than a versioned migration path.

Mod-removal safety (verified pattern):
- Mod uninstalled, save loaded by vanilla: the `pwrgridplus-burnreason.xml` ZIP entry is unknown to vanilla. `world.xml` is untouched (no foreign `xsi:type` markers). The wreckage Things load normally but without their hover reason.
- Vanilla saves: `SaveHelper.Save` rebuilds the ZIP via `ZipOutputStream` with only the five vanilla entries. The orphan side-car is silently dropped from the new ZIP.
- Mod re-installed (without intervening vanilla save): side-car is read, reasons are re-attached during the next load. Hover text returns.
- Mod re-installed AFTER an intervening vanilla save: side-car was dropped on that vanilla save. Data is lost. Acceptable trade-off; the alternative (parking data in `world.xml`) would break mod-removal safety.

## 12. IC10 logic surface

Custom `LogicType` values (registered via `Patterns/Logic/LogicTypeNumbers.cs`):

- `LogicPassthroughMode = 6577`. Writable per-device. 0 = logic-opaque (vanilla), 1 = logic-transparent. Applies to transformers, batteries, APCs, and PT/PR pairs. Logic passthrough behavior is solely configured by this per-device value (or the per-class server master toggle: `EnableTransformerLogicPassthrough`, `EnableBatteryLogicPassthrough`, `EnableAreaPowerControlLogicPassthrough`, `EnablePowerTransmitterLogicPassthrough`). It is independent of OnOff state and independent of fault state: a CYCLE_FAULTed Transformer with `LogicPassthroughMode = 1` continues to bridge logic reads between its Input and Output networks for the entire 60 s lockout window. Power flow is 0 during the fault; logic flow is unaffected.
- `Priority = 6578`. Writable per-transformer. Non-negative integer, no upper cap, default 100. Quantises to non-negative int on write. `0` means lowest priority (still allocated if supply remains), NOT a "disabled" sentinel.
- `Shedding = 6579`. Read-only. 1 = shed lockout active.
- `Overloaded = 6580`. Read-only. 1 = overload lockout active.
- `CycleFault = 6581`. Read-only. 1 = cycle-fault lockout active.
- `VariableVoltageFault = 6582`. Read-only. 1 = VVF lockout active (producers on a network with anything other than producers + transformers).
- `MaxChargeSpeed = 6583`. Read-only on every device with an elastic-supply / elastic-demand cell: Battery, AreaPowerControl, RocketPowerUmbilicalMale, RocketPowerUmbilicalFemale. Returns the configured per-prefab charge rate cap (W). Replaces the prior PGP repurpose of `ImportQuantity` on Battery.
- `MaxDischargeSpeed = 6584`. Same device set as MaxChargeSpeed. Returns the configured per-prefab discharge rate cap (W). Replaces the prior PGP repurpose of `ExportQuantity` on Battery.
- `ChargeSpeed = 6585`. Same device set. Returns the ACTUAL charge rate this tick (W), post-elastic-allocation. Reads from `SoftDemandShareCache[refId]`. Value is updated live as the allocator (ALLOCATE) computes each supplier's share; the field holds the most recent post-ALLOCATE value between ticks. Not latched. IC10 reads in practice see the end-of-tick value because IC10 (the LOGIC TICK phase) doesn't run during the electricity-tick passes (SETUP / OBSERVE through ENFORCE).
- `DischargeSpeed = 6586`. Same device set. Returns the ACTUAL discharge rate this tick (W), post-elastic-allocation, i.e. the cell's discharge rate. For Battery / RocketPowerUmbilical* (pure elastic suppliers) this reads `SoftSupplyShareCache[refId]`, which already IS the cell rate. For an APC the `SoftSupplyShareCache` entry is the BUNDLED supply (passthrough + grant-through + cell) that feeds the bundled vanilla `GetGeneratedPower` surface, so the APC's `DischargeSpeed` reads the CELL-only share from a separate `ApcCellDischargeCache`, keeping the value consistent across every storage device (deviation P9). Same live-update / non-latched semantics as `ChargeSpeed`; IC10 reads see the end-of-tick value.

All four LogicTypes (6583..6586) appear on Battery, AreaPowerControl, RocketPowerUmbilicalMale, RocketPowerUmbilicalFemale. For APC the "cell" is the inserted `BatteryCell`; the rates apply to its charge / discharge. For RocketUmbilical the cell is the device's internal `PowerMaximum = 10000` store.

The prior PGP repurpose of vanilla `LogicType.ImportQuantity` (29) and `ExportQuantity` (31) on Battery is REMOVED, hard. The mod has not yet been deployed, so no backward-compatibility shim is required. Vanilla meaning reverts on Battery (which is nothing meaningful, since Battery has no import / export slots).

`Setting` keeps its vanilla read/write semantics for IC10 access (clamped `[0, OutputMaximum]`). PowerGridPlus initialises `Setting = OutputMaximum` at world load. The in-world knob and Labeller tool redirect writes to `Priority`. See §5.3.

Transformer `PowerActual` (the PGP-added read, §19.3) reads the `_powerProvided` ledger AFTER the ENFORCE-tail settle (§10.7), so it reports the transformer's ACTUAL current throughput this tick, storage-charge flow included; before the 2026-07-02 rearchitecture the read reported the ledger's drifting billed plateau instead. More generally, every throughput reading on transformers and wireless pairs (device reads and the network `PowerPotential` / `PowerActual` mirrors) includes storage-charge flow, not just running machines (§9.5).

The `Ic10ConstantsPatcher` injects these names into the IC10 syntax highlighter at prefab-load time.

The full per-device-type LogicType inventory (vanilla and PowerGridPlus-added, with location references) lives at the end of this document in §19 "IC10 logic field inventory."

## 13. Multiplayer

- Sheds and overloads run on the host (the peer with `RunSimulation == true`).
- Fault state is broadcast to clients as ONE per-tick full-snapshot message, `FaultRegistrySnapshotMessage`, with a `Kind` byte selecting the registry (shed = 0, overload = 1, cycle = 2, variable-voltage = 3, dead-input = 4). This single message replaces the former per-transition `ShedStateMessage` / `OverloadStateMessage` / `CycleFaultState` / `VariableVoltageFaultState` diff messages outright (deviation P16); transitions are implicit in the snapshot deltas.
- Clients mirror each registry against a monotonic wall clock (deviation P15): the snapshot carries each entry's REMAINING ticks, the client stores `expiry = MonotonicClock.NowMs + remaining * 500 ms` and reads "still faulted" as `expiry > now`. `ElectricityTickCounter` advances only on the simulating peer, so a client cannot use an absolute until-tick.
- Priority writes broadcast via `PriorityMessage` (separate from the fault snapshot).
- Join-suffix snapshot (`Plugin.SerializeJoinSuffix`) ships: per-device passthrough mode, per-transformer priority, the `EnableTransformerShedding` / `EnableTransformerOverloadProtection` toggles, and a `(ReferenceId, remainingTicks)` snapshot of the four COUNTDOWN registries (shed, overload, cycle, variable-voltage, the last also with violator names). The dead-input cue is intentionally NOT in the handshake (see "Mid-cooldown client join" below).

**Per-tick full registry sync (heartbeat).** `PowerAllocator.SyncFaultSnapshots` broadcasts a `FaultRegistrySnapshotMessage` for each of the FIVE snapshot kinds every tick: the four lockout registries (`BrownoutRegistry` shed, `OverloadRegistry`, `CycleFaultRegistry`, `VariableVoltageFaultRegistry`) listing every currently-locked `ReferenceId` plus its REMAINING tick count (the VVF snapshot also carries each producer's violator names), and the `DeadInputRegistry` membership cue (deviation P7; the carried int is a keep-alive TTL, not a countdown). A kind's packet fires every tick while it is non-empty, PLUS exactly one EMPTY packet on the non-empty -> empty transition (the `_xxxWasNonEmpty` flags), so an OFF-as-reset, or the §8.5 network-retry recover, clears the client mirror immediately instead of waiting out the local expiry. Each tick is its own heartbeat: clients overwrite their mirror with the received list, so a lost or reordered packet self-heals on the next tick. There is no separate diff message; this snapshot is the authoritative and only fault-state channel (deviation P16). Because the VVF network commit (§8.5) stamps a producer cohort to ONE shared expiry, every cohort member carries the same remaining-tick count in the snapshot, so clients render their countdowns ticking down together.

**Mid-cooldown client join.** The join handshake bundles the four COUNTDOWN registries (shed, overload, cycle, variable-voltage, the VVF entries carrying violator names) into the join-suffix payload, each as `(long ReferenceId, int remainingTicks)`, so a joining client lands mid-lockout with the correct countdown showing on every faulted device. Without this, a join during a 60 s lockout window would render the device as un-faulted on the client until the next per-tick snapshot arrived, and the countdown would restart at 60 instead of resuming from the actual remaining time. The dead-input cue is intentionally NOT bundled: it has no countdown and only a 2-tick keep-alive TTL, so the joining client picks it up on the first per-tick heartbeat (within one tick) rather than carrying durable join state.

**Clock skew acceptable.** The countdown `{n}s` is wall-clock-interpolated per peer using `Time.realtimeSinceStartup` (§11.2). Host and client display values can drift by ~50-200 ms over a 60 s lockout depending on render-rate skew and network latency; this is purely cosmetic and never affects the authoritative fault-clear decision (which is tick-based on the host). See §11.2.

Soft-demand and elastic shares are not synchronised explicitly: the allocator runs host-only (the atomic tick is gated on `GameManager.RunSimulation`), so the share caches exist only on the host, and clients simply mirror the resulting device and network state through the game's normal replication.

## 14. Settings

### 14.1 Vanilla power-related fields by device class

Reference inventory verified against the 0.2.6228.27061 decompile. Per-prefab values (PowerMaximum on different battery prefabs etc.) are NOT in this table; only the C# class defaults appear.

**`AreaPowerControl`** (line 369509):

| Field | Type | Default | Purpose |
|---|---|---|---|
| `BatteryChargeRate` | float | 1000 | Watts used to charge the inserted cell from input side |
| (no DischargeRate) | -- | -- | Vanilla has no discharge cap on APC |
| `Battery` | BatteryCell (computed) | n/a | Reads inserted portable cell |
| `_powerProvided` | float | 0 | Per-tick interlock for charge/discharge |
| `UsedPower` | float (inherited) | 10 | Own quiescent draw |

**`Battery`** (line 370616, stationary battery base; all stationary variants share this class with prefab-overridden values):

| Field | Type | Default | Purpose |
|---|---|---|---|
| `PowerMaximum` | float | 3600000 | Max stored energy (3.6 MWh on base; prefab-overrides for large / nuclear) |
| `PowerStored` | float (clamped property) | 0 | Current stored energy |
| (no rate fields) | -- | -- | Vanilla has NO per-tick rate cap on stationary batteries |
| `LOSS_NORMAL` | const float | 10 | Per-tick atmospheric leak |
| `LOSS_IN_COLD` | const float | 50 | Per-tick atmospheric leak in sub-zero |
| `UsedPower` | float (inherited) | 10 | Own quiescent draw |

**`Transformer`** (line 403300, for comparison):

| Field | Type | Default | Purpose |
|---|---|---|---|
| `OutputMaximum` | float | 10000 | Max throughput watts (per-prefab) |
| `Setting` | double (synced) | 0 | Throughput cap, vanilla semantics kept; the in-world knob and Labeller writes redirect to Priority instead (section 5.3) |
| `_powerProvided` | float | 0 | Per-tick downstream draw accumulator |
| `UsedPower` | float (inherited) | 10 | Own quiescent draw |

**`BatteryCell`** (line 321224, portable cells inserted into APCs / chargers):

| Field | Type | Default | Purpose |
|---|---|---|---|
| `PowerMaximum` | float | 36000 (per-prefab override) | Max stored |
| `PowerStored` | float (clamped property) | 0 | Current stored |
| (no rate fields) | -- | -- | Rate imposed externally by whatever charges/discharges |

Stationeers does NOT have separate C# classes for StationaryBatteryLarge / NuclearBattery; all stationary batteries share the `Battery` class with prefab-overridden `PowerMaximum`. PowerGridPlus's per-prefab rate caps in `StationaryBatteryPatches.GetChargeCap` switch on `PrefabName` to apply the right cap.

### 14.2 PowerGridPlus settings

Verified complete against `Mods/PowerGridPlus/PowerGridPlus/Settings.cs` (every bound group and entry is listed). All are server-authoritative (every Section starts with `Server -`). Maintenance: regenerate this table against `Settings.cs` whenever a setting is added, renamed, or re-defaulted.

**Cable Simulation:**

| Setting | Default | Order | Purpose |
|---|---|---|---|
| `CableNormalMaxWatts` | 5000 | 20 | NEW. Normal cable Watts cap. `0` = unlimited. |
| `CableHeavyMaxWatts` | 100000 | 30 | NEW. Heavy cable Watts cap; default 100000 matches vanilla (decision 2). `0` = unlimited. |
| `CableSuperHeavyMaxWatts` | 0 | 40 | NEW. Super-heavy cable cap. Default 0 (unlimited) mirrors historical behaviour. |

**Cable Costs:**

| Setting | Default | Order | Purpose |
|---|---|---|---|
| `SuperHeavyCableCostMultiplier` | 2.0 | 10 | Section `Server - Cable Costs`, key `Super-Heavy Cable Cost Multiplier`. Multiplies the ingredient cost of crafting a super-heavy cable coil (2.0 doubles it; 1.0 = vanilla). Applied to the crafting recipe at load time (tagged RequireRestart); existing coils in the world are unaffected. |

**Voltage Tiers:**

| Setting | Default | Order | Purpose |
|---|---|---|---|
| `ExtraHeavyCableDevices` | (empty) | 10 | Section `Server - Voltage Tiers`, key `Extra Heavy-Cable Devices`. Comma-separated extra device prefab names allowed on heavy cable, on top of the built-in high-draw machine list (§3); matched against the device's `PrefabName`. For modded high-draw machines. The tiers themselves are always on (no toggle, §14.3). |

**Batteries:**

| Setting | Default | Order | Purpose |
|---|---|---|---|
| `EnableBatteryLimits` | true | 10 | Master toggle for per-prefab rate caps |
| `StationBatteryChargeRate` | 5000 | 20 | Small battery charge cap (W) |
| `StationBatteryDischargeRate` | 10000 | 30 | Small battery discharge cap (W) |
| `LargeBatteryChargeRate` | 25000 | 40 | Large battery charge cap (W) |
| `LargeBatteryDischargeRate` | 50000 | 50 | Large battery discharge cap (W) |
| `NuclearBatteryChargeRate` | 25000 | 60 | Nuclear battery charge cap (W, MorePowerMod) |
| `NuclearBatteryDischargeRate` | 50000 | 70 | Nuclear battery discharge cap (W) |
| `RocketBatteryMediumChargeRate` | 5000 | 72 | Key `Rocket Battery (Medium) Charge Rate`. Charge cap (W) for the rocket Battery (Medium) (StructureBatteryMedium); per device, capped further by the input cable. |
| `RocketBatteryMediumDischargeRate` | 10000 | 74 | Key `Rocket Battery (Medium) Discharge Rate`. Discharge cap (W) for StructureBatteryMedium; capped further by the output cable. |
| `RocketBatterySmallChargeRate` | 2500 | 76 | Key `Auxiliary Rocket Battery Charge Rate`. Charge cap (W) for the Auxiliary Rocket Battery (StructureBatterySmall); capped further by the input cable. |
| `RocketBatterySmallDischargeRate` | 5000 | 78 | Key `Auxiliary Rocket Battery Discharge Rate`. Discharge cap (W) for StructureBatterySmall; capped further by the output cable. |
| `BatteryChargeEfficiency` | 1.0 | 80 | Fraction of incoming power actually stored |
| `EnableBatteryLogicAdditions` | true | 90 | Expose Import/Export Quantity IC10 reads |
| `EnableBatteryLogicPassthrough` | true | 100 | Master toggle for battery logic passthrough |

**Area Power Control:**

| Setting | Default | Order | Purpose |
|---|---|---|---|
| `EnableAreaPowerControlFix` | true | 10 | Master toggle for APC leak fix and cable caps |
| `ApcBatteryChargeRate` | 1000 | 15 | APC charge cap (W); mirrors vanilla `BatteryChargeRate` default. Read at compute-time by `AreaPowerControlPatches.ComputeChargeCap`; the vanilla `AreaPowerControl.BatteryChargeRate` field is NOT mutated (D2 non-mutating, deviation P14). With `Enable APC Power Fix` off, vanilla's own field value runs. |
| `ApcBatteryDischargeRate` | 1000 | 17 | NEW. APC discharge cap (W). Vanilla has no parallel field; PowerGridPlus tracks per-APC via a static dictionary keyed by `ReferenceId`. Default mirrors charge for symmetry. |
| `EnableAreaPowerControlLogicPassthrough` | true | 20 | Master toggle for APC logic passthrough |

**Transformers and protection:**

| Setting | Default | Purpose |
|---|---|---|
| `EnableTransformerExploitMitigation` | true | Close transformer free-power exploit |
| `EnableTransformerShedding` | true | Master toggle for shed |
| `EnableTransformerOverloadProtection` | true | Master toggle for overload |
| `EnableTransformerLogicAdditions` | true | Power Actual read etc. |
| `EnableTransformerLogicPassthrough` | true | Master toggle for transformer logic passthrough |

**Rocket Umbilical:**

| Setting | Default | Order | Purpose |
|---|---|---|---|
| `EnableRocketUmbilicalLimits` | true | 10 | Master toggle for RocketUmbilical rate caps + the four soft-power LogicTypes (MaxChargeSpeed / MaxDischargeSpeed / ChargeSpeed / DischargeSpeed) on Male and Female. If false, RocketUmbilical falls back to vanilla rate behaviour (`PowerMaximum` per tick implicit cap). |
| `RocketUmbilicalChargeRate` | 10000 | 20 | Per-pair charge rate cap (W). Default matches vanilla `PowerMaximum`. Applies to both Male and Female halves of every linked pair. |
| `RocketUmbilicalDischargeRate` | 10000 | 30 | Per-pair discharge rate cap (W). |
| `EnableUmbilicalLogicPassthrough` | true | 40 | Key `Enable Umbilical Logic Passthrough`. Master kill-switch for umbilical logic passthrough: a docked Male + Socket pair is logic-transparent; each half honours its per-device `LogicPassthroughMode` (writing one half mirrors the value to its docked partner), both default to mode 1, persisted across save / load. An undocked umbilical bridges nothing. |

**Power transmitter:**

| Setting | Default | Purpose |
|---|---|---|
| `EnablePowerTransmitterLogicPassthrough` | true | Master toggle for PT/PR logic passthrough |

**Diagnostics:**

| Setting | Default | Order | Purpose |
|---|---|---|---|
| `EnableConservationCheck` | true | 10 | NEW. Section `Server - Diagnostics`, key `Enable Conservation Check`. Per-tick allocator self-audit (§8.8): per-network granted inflow must equal granted outflow within 0.5 W, and every routed contributor must bill its input exactly `TotalThrough * max(m, 1) + quiescent`. Violations log a warning with a per-component breakdown, throttled to once per network / per seg per 600 ticks. Costs a few microseconds per tick; leave on unless chasing maximum performance. |

**Emergency lights:**

| Setting | Default | Purpose |
|---|---|---|
| `EnableEmergencyLights` | true | Key `Enable Wall Light Battery Emergency Mode` (Order 10). Wall Light Battery devices behave as emergency backup lights: lamp off while grid-powered, on (from the internal cell) when grid power is lost. Per-light opt-out via Mode 1. |
| `EmergencyLightPrefabs` | StructureWallLightBattery | Key `Emergency Light Prefabs` (Order 20). Comma-separated light prefab names that get the emergency-backup behaviour while the master toggle is on; entries must be battery-backed wall lights (the `WallLightBattery` device class); matched against the device's `PrefabName`. |

### 14.3 Removed (with dead code)

- `EnableVoltageTiers` (now always-on).
- `EnableUnlimitedSuperHeavyCables` (subsumed by `CableSuperHeavyMaxWatts = 0`).

### 14.4 Settings rename rules

BepInEx keys `(Section, Key)` identify entries. Renaming either orphans the saved value (BepInEx resets to default on next load). The cable max settings and `ApcBatteryDischargeRate` are NEW (no rename). If any existing setting's Section or Key changes in this refactor, the About.xml `<ChangeLog>` carries a player-facing note per project CLAUDE.md guidance.

### 14.5 APC field handling at runtime

PowerGridPlus never mutates `AreaPowerControl.BatteryChargeRate` (decision §0.2 non-mutating, deviation P14). The charge cap is read at compute time: `AreaPowerControlPatches.ComputeChargeCap` reads `Settings.ApcBatteryChargeRate.Value` live and bounds it by the input cable's remaining headroom after the APC's own passthrough (§7.5); with `Enable APC Power Fix` off, vanilla's own field value runs untouched. A third-party mod reading the vanilla field therefore sees the unmodified vanilla value, not the configured cap. The discharge cap has no parallel vanilla field; PowerGridPlus tracks it in `ApcDischargeRateRegistry` (per-APC, session-only, defaulting to `Settings.ApcBatteryDischargeRate.Value` when no override exists). The registry is queried by the elastic-supply allocator (§7.3) and the `MaxDischargeSpeed` logic read; it does not need to back any vanilla code path because no vanilla code reads a discharge cap on APC.

## 15. Compatibility

- Re-Volt (`ReVolt.dll`): hard-refused at load by `Plugin.TryFindIncompatibleMod`. The two mods rewrite the same vanilla power-tick path.
- MoreCables: same.
- MorePowerMod (Nuclear Battery): supported via runtime type detection. Nuclear battery is treated as a soft-demand device with the dedicated rate cap setting.
- BatteryLight, HaulerMod, ScriptedScreens, KeypadModFix: patch per-device `IPowered.OnPowerTick`, unaffected by our `ElectricityTick` takeover (their patches fire in the DEVICE TICK phase of the atomic flow).

### 15.1 Stationpedia integration: footers built lazily, no cache

PowerGridPlus injects mod-specific footer text into the in-game Stationpedia entries for transformers, APCs, batteries, and super-heavy cable so players see PGP-relevant rate caps and behaviour notes without leaving the entry. The footers (`BuildTransformerFooter`, `BuildApcFooter`, `BuildBatteryFooter`, `BuildSuperHeavyCableFooter`, and any sibling builders) are built lazily on every `Localization.GetThingDescription` postfix call, with live `Settings.*.Value` reads. No cache, no warming, no lifecycle hook.

Rationale: settings cannot be changed mid-game (the StationeersLaunchPad settings GUI is main-menu only, §17.42). During a game session every `Settings.*.Value` read returns a frozen value, so "lazy + live read" and "cache once at load" produce identical text. Lazy is simpler: no startup hook to register, no cache invalidation to reason about, no surprise stale text if a future settings-change event bus is added without updating the cache. The per-call cost is a small string-build that runs only when the player actually opens a Stationpedia entry; the Stationpedia UI does not poll.

## 16. Performance characteristics

Per tick on a Lunar save with ~200 cable networks, ~120 transformers, ~30 soft-demand devices:

- SETUP / OBSERVE: 1× device-walk per network. ≈ 400 device-method calls.
- ALLOCATE: O(N) Kahn topological order + the fixed-point loop (bounded 2N+4 rounds, typically 1-2; each round carries both flow classes, so there is no separate charge-distribution walk) + the publish tail, where N = contributor count. ≈ 1000 method calls, ≈ 100 µs.
- ENFORCE: another 1× device walk per network, plus the ENFORCE tail (Powered reconcile + ledger settle over the enrolled roster, §10.6 / §10.7). ≈ 400 calls.
- DEVICE TICK + LOGIC TICK: vanilla copy, no overhead.

Aggregate cost is well under 1 ms per tick at 2 Hz: rounding error against the broader simulation.

## 17. Invariants the implementation must preserve

1. Tick atomicity: every decision at tick N takes effect at tick N. No latency. No oscillation.
2. Priority comparisons are local to a single input network.
3. Shed cascade is bottom-up; the smallest possible subnet sheds first.
4. Storage charge (the Soft class) rides the same priority-tiered backward/forward sweep as rigid demand (§9): funded per network from the firm residual only (never from elastic discharge), split tier-first and proportional-to-residual-headroom within a tier, capped by cable headroom at every hop, and it never displaces rigid capacity and never triggers a fault.
5. Soft demand never causes rigid devices to flicker; soft devices scale with available headroom but never below 0.
6. Voltage tiers are enforced regardless of any toggle.
7. Cycles are faulted regardless of any toggle. PowerGridPlus's PROTECT-phase detection covers cycles through `ElectricalInputOutput` device chains AND cycles through PT/PR wireless links; both produce CYCLE_FAULT, not a cable burn. Vanilla's recursive-provider detector runs as belt-and-braces (§17.25).
8. OFF clears the lockout. ON re-evaluates.
9. Shed transformer's downstream is dark for 60 s regardless of mid-window surplus availability.
10. PT/PR pairs behave identically to wired transformers at the allocator layer; their per-device methods continue to run under PowerTransmitterPlus's distance-loss math without coordination.
11. Stationary batteries are dual-terminal `ElectricalInputOutput` devices. Charge demand fires on the Input network; discharge supply fires on the Output network. Both flow simultaneously per tick on distinct networks with no interlock; net cell delta = received_on_input - delivered_on_output.
12. APCs are dual-terminal but their internal `_powerProvided` couples both sides per tick; charge and discharge of the inserted cell are in-tick exclusive.
13. A battery or APC with `InputNetwork == OutputNetwork` is short-circuited via vanilla `ElectricalInputOutput.IsOperable`. All four power methods return 0 / no-op. Hover shows "Device Short Circuited". PowerGridPlus relies on the vanilla gate and does not implement parallel detection.
14. A single transformer can never burn a cable by overdraw: its `effective_cap` is bounded by `min(OutputMaximum, InputCable.MaxVoltage, OutputCable.MaxVoltage)`. Cable burning from overdraw requires MULTIPLE transformers feeding one network past the cable cap collectively (standard vanilla cable burn).
15. Cable caps per tier are server-authoritative via settings and NON-MUTATING (decision §0.2): `Cable.MaxVoltage` is never written. Every cap reader (battery / APC headroom, the allocator's effective-cap formula, the generator-overflow burn check) consults the `CableMax` helper, which reads the configured per-tier value live at use time, and vanilla's own cable-burn check is patched to consult the same helper, so both read the same number. `0` means unlimited and is normalised internally to `float.MaxValue` (clamped to a finite 1e9 sentinel where it enters the allocator's capacity sums, §5.0.3).
16. The PT/PR pair's cap is a STATIC link rating computed per tick from whichever distance model is active (§6.3): PowerTransmitterPlus's configured Max Transfer Capacity (0 = unlimited, mapped to a finite 1e9 sentinel) with distance repriced as the source-draw multiplier `m`, or the vanilla `MaxPowerTransmission` minus the `PowerLossOverDistance` delivery loss when PowerTransmitterPlus is absent. It is never the live `InputNetwork.PotentialLoad` (the live read collapses to a cross-tick zero fixed point on transformer-fed sources). PowerGridPlus does not hardcode either model.
17. All power simulation values are Watts. There are no amps or volts in the simulation; the misleadingly named `Cable.MaxVoltage` is a Watts cap compared directly against accumulated Watts in `PowerTick.GetBreakableCables`.
18. Every segmenting device (Transformer, Battery, AreaPowerControl, PowerTransmitter, PowerReceiver, RocketPowerUmbilicalFemale, RocketPowerUmbilicalMale) is a level boundary in the §8.0 cascade. The list is exhaustive against the 0.2.6228.27061 decompile. PowerConnection is excluded per §5.0.1.
19. Transformer operational state is binary per tick: ON (working) or LOCKED-OUT (shed XOR overload). There is no "throttled" intermediate state. A transformer either gets its full required input or sheds; a transformer either delivers within `OutputMaximum` or overloads.
20. Shed and overload converge to a joint fixed point inside ALLOCATE of one tick (§8.0). External observers never see the iteration; one tick produces one joint result.
21. Cycles burn only when the loop is powered (network `_actual > 0`); unpowered cycles persist silently until current flows (§4.4).
22. The cable-burn check is retained ONLY for direct generator overflow. Transformer or battery contributions causing a cable overflow trip the upstream segmenting devices into OVERLOAD instead of burning the cable (§5.7).
23. Battery discharge is elastically capped at `min(DischargeRateCap, PowerStored)` per battery (§7.3); proportional split is computed against effective caps, not the static rate cap. A high-cap battery is never throttled by a low-stored sibling.
24. A battery's discharge to its OUTPUT network is independent of any state on its INPUT network. The "kept-alive by local battery" failsafe is automatic from the dual-terminal model (§7.3.1).
25. Cycles are resolved by CYCLE_FAULT (§4.5), not by burning cables. PowerGridPlus's PROTECT phase runs its OWN DFS over the segmenting-device graph (§4.2.5); it does NOT consume vanilla's `BreakableCables` / `BreakableFuses` lists as a cycle signal. PGP's DFS catches the gaps vanilla misses: wireless cycles (PT/PR are first-class graph nodes), multi-hop loops with no provider anchor, battery-only and APC-only cycles where every network's `Required` is 0, self-sustaining mutual-feed loops. Every segmenting device on a detected cycle enters CYCLE_FAULT for 60 seconds and the loop breaks immediately because every device in it contributes 0. Vanilla `PowerTick.CheckForRecursiveProviders` runs unsuppressed as belt-and-braces; in normal operation it never fires because PGP's DFS has already broken every cycle by faulting its members. If vanilla's destructive cable burn ever does fire, that signals a bug in PGP's DFS and vanilla acts as the safety net.
26. CYCLE_FAULT is the third lockout state (parallel to SHED and OVERLOAD). Visuals share the red flash with OVERLOAD; hover text differs.
27. Producer-isolation invariant (strict-literal): producers must only connect to other producers and `Transformer` instances (§8.5). Battery, AreaPowerControl, PowerTransmitter, PowerReceiver, RocketPowerUmbilicalMale, and RocketPowerUmbilicalFemale do NOT satisfy producer-isolation despite being segmenters. Violations trigger VARIABLE_VOLTAGE_FAULT on flash-capable producers (red flash + countdown hover) or the hover-only path on Solar / Wind / RTG (GetPassiveTooltip postfix appends the fault line). The producer-isolation walk treats every `Transformer` instance as a Transformer regardless of its fault state; a producer wired to a CYCLE_FAULTed transformer does NOT get VVF (§8.5). A producer-only network (producers + no other devices) is silent: no VVF, no warning. The vanilla "irreducible brownout" case no longer exists in valid configurations.
28. The allocator uses ONLY integer keys for every ordering decision (the Kahn topological order's `ReferenceId` tiebreak, supplier dispatch `priority DESC / ReferenceId ASC`, shed victim selection over `priority ASC / claim DESC / ReferenceId ASC`), for MP determinism. Floats never enter an ordering decision; the shed-victim claim is quantised to integer Watts via `(int)Math.Floor` and the shed deficit via `(int)Math.Ceiling(deficit - Eps)` (§8.0.5).
29. Fault hover text includes a live countdown `{n}s` recomputed on each `Thing.GetContextualName` poll. The four templates live in §11.1 as the single source of truth.
30. Flash visuals attach to every segmenting device with an `InteractableType.OnOff` interactable AND every flash-capable producer (per the §11.4 table). Non-flash producers (SolarPanel, WindTurbineGenerator, RTG) use the hover-only path: a universal-base `Thing.GetPassiveTooltip` postfix appends the fault line + countdown to their tooltip. The cable-burn fallback (§11.6) is reserved for future producer classes that have neither flash nor a useful hover surface.
31. `PowerConnection` is vestigial dead code (§5.0.1). PowerGridPlus ignores it entirely: not segmenting, not a Transformer for producer-isolation, not fault-eligible, not in cycle detection. Vanilla itself never consults it (`IsPowerProvider = false`, no `is PowerConnection` checks).
32. Burned-cable per-instance reasons survive save/load via `BurnReasonSideCar` (§11.6). The side-car ZIP entry pattern is mod-removal safe: uninstalling PowerGridPlus leaves `world.xml` untouched and the orphan side-car entry is dropped on the next vanilla save.
33. SoftSupplyShareCache propagates the §7.3 elastic discharge cap into ENFORCE via Harmony postfixes on `Battery.GetGeneratedPower`, `AreaPowerControl.GetGeneratedPower`, `RocketPowerUmbilicalMale.GetGeneratedPower`, `RocketPowerUmbilicalFemale.GetGeneratedPower`. Without these postfixes the allocator's elastic decisions do not reach vanilla `_powerRatio` math, and rigid devices on undersupplied output nets partial-power. The postfixes are not optional.
34. Every segmenting device class (per §5.0) enters the allocator's roster (§8.0.0.1): Transformer, APC, and the linked PT/PR pair as pull-through contributors; Battery, APC cell, and RocketPowerUmbilical* as elastic suppliers / soft demanders. Any segmenting class left out would be a partial-power hole (an undersupplied contributor feeding a rigid load that reaches ENFORCE as inflated `Required`); the vanilla `_powerRatio < 1` net (§8.0.0.2) catches anything an unclassified future class might reintroduce.
35. Vanilla `_powerRatio = Potential / Required` partial scaling is INTENTIONALLY RETAINED as a safety net. The spec's claim is that this branch becomes unreachable in correctly-classified configurations; if a future mod or game update introduces an unclassified device class, vanilla scaling gracefully degrades rather than corrupting state. Per-user directive: phantom power or corruption is worse than partial-power.
36. `Setting` keeps its vanilla read / write semantics for IC10 access (§5.3). PowerGridPlus does NOT redirect `Setting` writes from IC10. Only the in-world knob and the Labeller tool redirect writes to `Priority`. PowerGridPlus initialises `Setting = OutputMaximum` ONLY on new transformer construction; saved Setting values are preserved across save / load so IC10-throttled transformers retain their throttle.
37. All throughput formulas (§5.5, §5.6, §8.0, §8.4, §9) use `Setting` for the active throughput cap. `OutputMaximum` appears only as the prefab rating and the upper bound of Setting. An IC10 script that writes `Setting < OutputMaximum` reduces the transformer's effective_cap and can trigger SHED or OVERLOAD at the lower threshold.
38. The prior PGP repurpose of vanilla `LogicType.ImportQuantity` (29) and `ExportQuantity` (31) on Battery is REMOVED. Battery now exposes `MaxChargeSpeed` (6583), `MaxDischargeSpeed` (6584), `ChargeSpeed` (6585, actual this tick), `DischargeSpeed` (6586, actual this tick) instead. Breaking change for existing IC10 scripts that read ImportQuantity / ExportQuantity on Battery; documented in `<ChangeLog>`.
39. Skip-while-faulted optimisation (refined). A device in CYCLE_FAULT, VARIABLE_VOLTAGE_FAULT, OVERLOAD, or SHED is non-conducting for the entire 60 s lockout window (§10.2.1). The skip-while-faulted shortcut applies ONLY to the question "is the original loop still here?": when the next allocator pass asks whether to re-fire the same fault on the same participant, the participant's faulted-ness is enough to say "yes, this device is still locked out, do not re-validate the topology around it." For NEW cycle detection (the PROTECT-phase DFS) the walk treats faulted segmenters as if they were conducting: their input and output edges remain in the graph and a new cycle that runs through a faulted device is still detected. If a new cycle is found, only the previously non-faulted participants are newly registered for CYCLE_FAULT with a fresh 60 s timer; existing fault timers on devices already locked out are NOT extended, reset, or shortened.

    Worked example. T1, T2, T3 form a cycle and all three enter CYCLE_FAULT at t=0 with `_lockoutUntilTick = currentTick + 120`. At t=10 s the player rewires: the original T1-T2-T3 loop is broken AND a new T1-T4-T5 loop is formed (T1 sits at the corner of the rewire). The PROTECT-phase DFS walks the new graph; because the walk treats faulted T1 as conducting, the new T1-T4-T5 cycle is detected. T4 and T5 are newly registered with full 60 s timers (50 s of which remain "behind" T1's clock). T1 keeps its existing timer (50 s remaining). At t=60 s T1's original timer expires; at t=70 s T4's and T5's timers expire; on the next allocator pass after each expiry, the cycle is re-checked. As long as T1-T4-T5 is still closed, all three re-fault. The original T1-T2-T3 loop is gone, so T2 and T3 stay clear when their timers expire at t=60 s.

    Implementation summary across the four fault paths:
    - Cycle detection DFS (PROTECT phase): faulted segmenters are walked through, not skipped. New cycles among any combination of faulted and non-faulted devices are detected; only the not-yet-faulted participants get new registry entries.
    - Producer-isolation (VARIABLE_VOLTAGE_FAULT, PROTECT phase): producers already in VVF skip the topology re-check on the current tick. The lockout is timer-only, and the producer's contribution is already 0 via `GetGeneratedPower` postfix. After the timer expires, the post-timeout allocator pass re-walks the network; if the violation persists, VVF re-fires with a fresh 60 s.
    - Shed / overload allocator math (ALLOCATE): faulted devices contribute 0 supply and 0 cap, so they fall out of allocation naturally; no explicit short-circuit is needed. The fixed-point loop is free to walk through them.
40. Fault-state transience across save / load (§10.5). All four fault registries (`BrownoutRegistry`, `OverloadRegistry`, `CycleFaultRegistry`, `ProducerFaultRegistry`) clear on world load and recompute on the first post-load tick. The `_lockoutUntilTick` countdowns are in-memory only; never serialised. The first post-load tick applies the full 60 s lockout to any rediscovered violation with no grace period (§10.5). `BurnReasonSideCar` is the sole exception and records durable per-instance hover state on already-broken cable wreckage, not transient fault timers.
41. Cycle-detection topology-change skip. PGP tracks `lastTopologyChangeTick` per `CableNetwork`. The PROTECT-phase cycle-detection DFS is skipped on a network whose `lastTopologyChangeTick < lastCheckedTick` (no structural change since the previous walk). Topology-change events that bump the counter: cable place / destroy / burn (wrong-tier burns count), segmenter place / destroy. The skip applies per network; a single network's change does not invalidate the cached result on disjoint networks. On a stable grid (the common case), the cycle detector is a near-zero-cost no-op every tick.
42. Settings are immutable during a game session. The StationeersLaunchPad in-game mod settings GUI is main-menu only; the game does not expose a runtime "open mod settings" panel. Every `Settings.*.Value` read during play returns the same value, so PGP code can treat settings as frozen for the duration of the session. This justifies lazy / no-cache patterns (Stationpedia footers in §15.1) and removes the need for a settings-change event bus.
43. Conservation is MACHINE-CHECKED every tick (§8.8). With `Enable Conservation Check` on (default), the allocator audits its own converged grants at the end of ALLOCATE: per network, granted inflow equals granted outflow within 0.5 W; per routed contributor, `TotalPull == TotalThrough * max(m, 1) + quiescent` whenever it conducts (one-sided against the quiescent when it does not). A violation is an allocator bug by definition and logs a throttled warning with a per-component breakdown; a healthy build logs zero violations.
44. The shortfall census reads ZERO Deadlock networks on a healthy build (§8.8): no network may end a tick with unmet rigid demand while an active supplier retains headroom above 0.5 W AND that supplier's input network retains undelivered supply. Deadlock > 0 is the invisible-deadlock regression signal (the shape of the pre-rearchitecture charge deadlock) and fails the `pgp-rearch-suite` census.
45. The partial-power sentinel reads ZERO violations on a healthy build (§8.8): no ratio-deprivable network the allocator classified Served may end its ENFORCE with vanilla `_powerRatio < 1`. A sub-1 ratio there means the published presentation diverged from the allocator's grants (the §8.0.0.2 safety net engaged where it must be unreachable). Out of contract by derivation: unmet networks (Dry / Throttled / Deadlock, whole-device darkness is the designed honest failure mode) and bridge-only networks (every power member a routed segmenter: wireless carrier nets, whose asymmetric mirrors sit below ratio 1 in normal conduction, and tower-top hop nets), because they contain no member whose operation or stored energy the ratio can shrink.

## 18. Test plan summary

Per major behaviour, a probe scenario must exist:

- Atomic tick: AP probe (verifies architectural wiring).
- Overload: OP probe.
- Shed cascade with bottom-up propagation: new probe.
- Per-input-network priority comparison: new probe.
- Storage-charge flow (soft riding the sweep, charging across intermediate segments): covered by the `pgp-rearch-suite` charge leg below.
- PT/PR as transformer: new probe.
- Soft-demand cap (no `_powerProvided` runaway): new probe with 60-tick state evolution.
- OFF-as-reset: new probe.
- Multiplayer transition broadcast: existing MP probe extended.
- Visual flash: existing FP probe.
- Hover text: existing OP P7 covers overload; add shed equivalent.
- Knob: existing KBP probe.
- Labeller: existing LP probe.
- Save/load: existing SLP probe.

Three dedicated-server ScenarioRunner suites carry the plan (each implemented as a single phased scenario, one scenario per server run):

- **`pgp-rearch-suite`**: the unified-flow regression suite. Charge regression (a known nuclear-battery pair charging across a 7-link wireless farm; `PowerStored` trajectory must rise to full), the shortfall census over the `ShortfallDiagnostics` snapshot (§8.8; the Deadlock bucket is the regression signal and must be 0, invariant §17.44), the Powered-presentation check (idle healthy chargers read Powered=True, with a >= 3-consecutive-tick debounce for the one-frame marshal lag of the rising edge, §10.6), and the ledger check (no negative `_powerProvided` on any wireless half after the sweep, settle silent, §10.7), with the conservation checker expected at zero violations throughout (§17.43). Emits one `VERDICT charge=... shortfalls=... powered=... ledger=...` line.
- **`ptp-standalone-suite`**: PowerTransmitterPlus WITHOUT PowerGridPlus loaded (the §6.6 standalone rules). Steady links conserve; transmitter debt stays bounded by the ceiling plus one burst bill (`debt(t) <= ceiling + delivered(t) * m`); flows above 5 kW are billed to the source (the receiver drain-cap lift, `flow5k` leg); an OnOff cycle leaves the debt bounded and resumes clean. Emits `VERDICT api=... pairs=... debt=... flow5k=... toggle=...`.
- **`pgp-atomic-all`**: the composite aggregator running every legacy probe family above (atomic tick AP, overload OP, knob KBP, flash FP, hover HP, labeller LP, multiplayer MP, save/load SLP). Pass/fail counts logged per probe; the final tally is compared against a baseline of 100% pass.

## 19. IC10 logic field inventory

### 19.0 Quick-reference table (one row per unique LogicType name)

For correction-by-number convenience. The "Where used" column lists the device classes that expose this LogicType under PowerGridPlus's enabled patches; meaning is summarised one-line.

| # | LogicType name | Value | Source | R/W | Where used | Meaning |
|---|---|---|---|---|---|---|
| 1 | Power | 1 | vanilla | R | every IPowered device | `Powered ? 1 : 0` |
| 2 | Open | 2 | vanilla | R/W | doors, panels, RocketUmbilicalMale | `IsOpen` |
| 3 | Mode | 3 | vanilla | R or R/W | Battery (R), APC (R), SolidFuelGenerator (hidden), GasFuelGenerator, WirelessPower (R) | multi-state animation / power-mode enum |
| 4 | Error | 4 | vanilla | R | most power devices | error-state interactable |
| 5 | Activate | 9 | vanilla | R/W (mostly hidden on power devices) | most devices, gated by HasActivateState | activate interactable |
| 6 | Lock | 10 | vanilla | R/W | most devices | `IsLocked` |
| 7 | Charge | 11 | vanilla | R | Battery, APC, SolarPanel, WirelessPower | "available stored / generated power" |
| 8 | Setting | 12 | vanilla | R/W | Transformer | vanilla throttle value `[0, OutputMaximum]`. PGP keeps vanilla read/write semantics for IC10 (does NOT redirect). Default init = OutputMaximum. Knob and Labeller redirect to Priority instead. |
| 9 | Horizontal | 20 | vanilla | R/W | SolarPanel, WirelessPower | horizontal angle (degrees) |
| 10 | Vertical | 21 | vanilla | R/W | SolarPanel, WirelessPower | vertical angle (degrees) |
| 11 | Maximum | 23 | vanilla | R | Battery (PowerMaximum), Transformer (OutputMaximum), APC, SolarPanel | "maximum capacity" semantic |
| 12 | Ratio | 24 | vanilla | R | Battery, Transformer, APC, SolarPanel | "current / maximum" 0..1 |
| 13 | PowerPotential | 25 | vanilla | R | Battery, Transformer (inherited), APC, WirelessPower | `InputNetwork.PotentialLoad` |
| 14 | PowerActual | 26 | vanilla on Battery/APC/WirelessPower; PGP-added on Transformer | R | Battery, APC, WirelessPower, Transformer (PGP) | `OutputNetwork.CurrentLoad`. On Transformer, PGP reads the `_powerProvided` ledger AFTER the ENFORCE-tail settle (§10.7), so it equals the ACTUAL current throughput this tick, storage-charge flow included |
| 15 | On | 28 | vanilla | R/W | every IPowered device | `OnOff` |
| 16 | ImportQuantity | 29 | vanilla on import devices; PGP repurpose REMOVED | R | import devices only | vanilla: slot count. Previous PGP exposure on Battery is removed; use `MaxChargeSpeed` (#38) instead. |
| 17 | ExportQuantity | 31 | vanilla on export devices; PGP repurpose REMOVED | R | export devices only | vanilla: slot count. Previous PGP exposure on Battery is removed; use `MaxDischargeSpeed` (#39) instead. |
| 18 | HorizontalRatio | 34 | vanilla | R/W | SolarPanel, WirelessPower | horizontal as 0..1 |
| 19 | VerticalRatio | 35 | vanilla | R/W | SolarPanel, WirelessPower | vertical as 0..1 |
| 20 | Color | 38 | vanilla | R/W | most devices | paint swatch index |
| 21 | PowerGeneration | 65 | vanilla | R | WindTurbineGenerator, GasFuelGenerator, SolidFuelGenerator, StirlingEngine | "watts produced this tick" |
| 22 | PositionX | 76 | vanilla | R | WirelessPower | x world position of ray emitter |
| 23 | PositionY | 77 | vanilla | R | WirelessPower | y world position |
| 24 | PositionZ | 78 | vanilla | R | WirelessPower | z world position |
| 25 | ReferenceId | 217 | vanilla | R | every Thing | the long `Thing.ReferenceId` |
| 26 | MicrowaveSourceDraw | 6571 | PowerTransmitterPlus | R | PT, PR | W pulled from source cable on TX side |
| 27 | MicrowaveDestinationDraw | 6572 | PowerTransmitterPlus | R | PT, PR | W delivered to receiver's downstream net |
| 28 | MicrowaveTransmissionLoss | 6573 | PowerTransmitterPlus | R | PT, PR | `SourceDraw - DestinationDraw` |
| 29 | MicrowaveEfficiency | 6574 | PowerTransmitterPlus | R | PT, PR | `1 / (1 + k * distance_km)`, 0..1 |
| 30 | MicrowaveAutoAimTarget | 6575 | PowerTransmitterPlus | R/W | PT, PR | target Thing `ReferenceId`; write to slew dish |
| 31 | MicrowaveLinkedPartner | 6576 | PowerTransmitterPlus | R | PT, PR | linked partner's `ReferenceId` |
| 32 | LogicPassthroughMode | 6577 | PGP | R/W | Transformer, Battery, APC, PT, PR | 0 = vanilla opaque, 1 = logic-transparent bridge |
| 33 | Priority | 6578 | PGP | R/W | Transformer, PT (anchor of PT/PR pair) | dispatch priority `int >= 0`, default 100 |
| 34 | Shedding | 6579 | PGP | R | Transformer, PT | 1 = SHED lockout active (insufficient upstream supply) |
| 35 | Overloaded | 6580 | PGP | R | Transformer, PT | 1 = OVERLOAD lockout active (downstream demand exceeds the transformer's active `Setting` cap, or the wireless pair's static link rating, §8.4) |
| 36 | CycleFault | 6581 | PGP | R | every segmenting device | 1 = CYCLE_FAULT lockout active (device is part of a closed loop) |
| 37 | VariableVoltageFault | 6582 | PGP | R | every classifier producer WITH a logic surface (Solar, Wind, Large Wind, Gas, Solid-fuel/Coal, Stirling, small Turbine); NOT PowerConnector or RTG (no logic surface) | 1 = VVF lockout active (producer wired to anything other than producers or transformers) |
| 38 | MaxChargeSpeed | 6583 | PGP | R | Battery, APC, RocketPowerUmbilicalMale, RocketPowerUmbilicalFemale | configured per-prefab charge rate cap in W |
| 39 | MaxDischargeSpeed | 6584 | PGP | R | Battery, APC, RocketPowerUmbilicalMale, RocketPowerUmbilicalFemale | configured per-prefab discharge rate cap in W |
| 40 | ChargeSpeed | 6585 | PGP | R | Battery, APC, RocketPowerUmbilicalMale, RocketPowerUmbilicalFemale | actual charge rate this tick (W), post-elastic-allocation, from `SoftDemandShareCache` |
| 41 | DischargeSpeed | 6586 | PGP | R | Battery, APC, RocketPowerUmbilicalMale, RocketPowerUmbilicalFemale | actual CELL discharge rate this tick (W), post-elastic-allocation; from `SoftSupplyShareCache` (Battery / umbilical) or `ApcCellDischargeCache` (APC cell-only, since its supply cache is bundled) |

Plus, on the separate `LogicSlotType` enum (slot-keyed reads from a parent device, NOT direct `LogicType`):

| # | LogicSlotType name | Source | R/W | Where used | Meaning |
|---|---|---|---|---|---|
| 42 | Charge | vanilla | R | BatteryCell-bearing devices (APC slot, charger slot, etc.) | inserted cell's `PowerStored` |
| 43 | ChargeRatio | vanilla | R | same | inserted cell's `PowerRatio` |

### 19.1 Device base class (inherited surface)

Comprehensive list of every power-related `LogicType` exposed on every power device. Verified against the 0.2.6228.27061 decompile. Entries are numbered for reference.

### 19.1 Device base class (inherited surface)

Read or write gated per device by `HasXxxState` flags. Listed once.

1. **LogicType.ReferenceId** (217, vanilla, read-only) - `Thing.ReferenceId`. Always readable.
2. **LogicType.Color** (38, vanilla, read+write) - paint swatch index. Gated by `HasColorState`.
3. **LogicType.Activate** (9, vanilla, read+write) - interactable activate. Gated by `HasActivateState`.
4. **LogicType.Power** (1, vanilla, read-only) - `Powered ? 1 : 0`. Gated by `HasPowerState`.
5. **LogicType.Open** (2, vanilla, read+write) - `IsOpen`. Gated by `HasOpenState`.
6. **LogicType.Mode** (3, vanilla, read+write) - generic mode. Gated by `HasModeState`.
7. **LogicType.Error** (4, vanilla, read-only) - error interactable. Gated by `HasErrorState`.
8. **LogicType.Lock** (10, vanilla, read+write) - `IsLocked`. Gated by `HasLockState`.
9. **LogicType.On** (28, vanilla, read+write) - `OnOff`. Gated by `HasOnOffState`.

### 19.2 Battery (stationary)

Class `Battery : ElectricalInputOutput` at decompile L370616. Used by `StructureBattery`, `StructureBatteryLarge`, `StationBatteryNuclear` prefabs.

10. **LogicType.Charge** (11, vanilla, read-only) - `AvailablePower` = `PowerStored`. `Battery.GetLogicValue:370856`.
11. **LogicType.Maximum** (23, vanilla, read-only) - `PowerMaximum`. `Battery.GetLogicValue:370857`.
12. **LogicType.Ratio** (24, vanilla, read-only) - `PowerStored / PowerMaximum`. `Battery.GetLogicValue:370858`.
13. **LogicType.PowerPotential** (25, vanilla, read-only) - `InputNetwork.PotentialLoad`. `Battery.GetLogicValue:370859`.
14. **LogicType.PowerActual** (26, vanilla, read-only) - `OutputNetwork.CurrentLoad`. `Battery.GetLogicValue:370860`.
15. **LogicType.Mode** (3, vanilla, read-only) - explicitly NOT writable per `Battery.CanLogicWrite:370867`.
16. **LogicType.MaxChargeSpeed** (6583, PGP-added, read-only) - effective charge cap in Watts (`StationaryBatteryPatches.GetChargeCap`). REPLACES the prior PGP repurpose of `ImportQuantity` on Battery.
17. **LogicType.MaxDischargeSpeed** (6584, PGP-added, read-only) - effective discharge cap in Watts (`StationaryBatteryPatches.GetDischargeCap`). REPLACES the prior PGP repurpose of `ExportQuantity` on Battery.
18. **LogicType.ChargeSpeed** (6585, PGP-added, read-only) - actual charge rate this tick (W), post-elastic-allocation. Reads from `SoftDemandShareCache[refId]`.
19. **LogicType.DischargeSpeed** (6586, PGP-added, read-only) - actual CELL discharge rate this tick (W), post-elastic-allocation. Reads from `SoftSupplyShareCache[refId]` (Battery / umbilical); an APC reads the cell-only `ApcCellDischargeCache[refId]` because its supply cache is bundled (deviation P9).
20. **LogicType.LogicPassthroughMode** (6577, PGP-added, read+write) - per-device passthrough override. Default 1 on batteries.

### 19.3 Transformer

Class `Transformer : ElectricalInputOutput` at decompile L403300.

19. **LogicType.Setting** (12, vanilla, read+write) - vanilla `_outputSetting` in `[0, OutputMaximum]`. PGP does NOT intercept Setting: IC10 reads return the live Setting and writes update it (the vanilla `[0, OutputMaximum]` clamp), exactly as in vanilla. Only the in-world knob and the Labeller redirect to `Priority` (§5.3). `TransformerPriorityLogicPatches.cs` wires Priority / Shedding / Overloaded / CycleFault, not Setting.
20. **LogicType.Maximum** (23, vanilla, read-only) - `OutputMaximum`. `Transformer.GetLogicValue:403532`.
21. **LogicType.Ratio** (24, vanilla, read-only) - vanilla `Setting / OutputMaximum`. **PGP override**: returns 1.0 when shedding effective (Setting is hardcoded at OutputMaximum). `TransformerPriorityLogicPatches.cs`.
22. **LogicType.PowerPotential** (25, vanilla, read-only) - `InputNetwork.PotentialLoad`. Inherited.
23. **LogicType.PowerActual** (26, **PGP-added** read slot, read-only) - vanilla does NOT expose this on Transformer; PGP returns `_powerProvided`, which the ENFORCE-tail ledger settle (§10.7) sets to the allocator's `TotalThrough` every tick before the LOGIC phase runs, so the read equals the transformer's ACTUAL current throughput (rigid + storage-charge flow), not the drifting billed-ledger plateau the pre-settle ledger carried. `TransformerLogicPatches.cs`. Gated by `EnableTransformerLogicAdditions`.
24. **LogicType.LogicPassthroughMode** (6577, PGP-added, read+write) - per-Transformer override. Default 1 on small transformers, 0 elsewhere.
25. **LogicType.Priority** (6578, PGP-added, read+write) - dispatch priority int >= 0, default 100. Backing `PriorityStore`. Replicated via `PriorityMessage`.
26. **LogicType.Shedding** (6579, PGP-added, read-only) - 1 when in shed lockout. Server-derived from `BrownoutRegistry`. Replicated via `FaultRegistrySnapshotMessage` (KindShed).
27. **LogicType.Overloaded** (6580, PGP-added, read-only) - 1 when in overload lockout. From `OverloadRegistry`. Replicated via `FaultRegistrySnapshotMessage` (KindOverload).
28. **LogicType.CycleFault** (6581, PGP-added, read-only) - 1 when in cycle-fault lockout. From `CycleFaultRegistry`. Replicated via `CycleFaultStateMessage`. NEW.

### 19.4 AreaPowerControl (APC)

Class `AreaPowerControl : ElectricalInputOutput` at L369509.

29. **LogicType.Charge** (11, vanilla, read-only) - `AvailablePower` = `InputNetwork.PotentialLoad + Battery.PowerStored`. `AreaPowerControl.GetLogicValue:369720`.
30. **LogicType.Maximum** (23, vanilla, read-only) - inserted cell's `PowerMaximum` (or 0 if no cell). `AreaPowerControl.GetLogicValue:369721`.
31. **LogicType.Ratio** (24, vanilla, read-only) - cell's `PowerStored / PowerMaximum`. `AreaPowerControl.GetLogicValue:369722`.
32. **LogicType.PowerPotential** (25, vanilla, read-only) - inherited. `AreaPowerControl.GetLogicValue:369723`.
33. **LogicType.PowerActual** (26, vanilla, read-only) - `OutputNetwork.CurrentLoad`. `AreaPowerControl.GetLogicValue:369724`.
34. **LogicType.Mode** (3, vanilla, read-only) - vanilla PowerMode enum (Idle, Discharging, Charging, Discharged, Charged). Server-set from cell state.
35. **LogicType.LogicPassthroughMode** (6577, PGP-added, read+write) - per-APC override. Default 1 (logic-transparent).

### 19.5 SolarPanel (and Large, HeavyDouble variants)

Class `SolarPanel : Electrical` at L399762.

36. **LogicType.Horizontal** (20, vanilla, read+write) - `Horizontal * MaximumHorizontal` degrees. Writable.
37. **LogicType.Vertical** (21, vanilla, read+write) - lerped to [15, 165] degrees.
38. **LogicType.HorizontalRatio** (34, vanilla, read+write) - raw 0..1, modulo on write.
39. **LogicType.VerticalRatio** (35, vanilla, read+write) - raw 0..1.
40. **LogicType.Charge** (11, vanilla, read-only) - `GenerationRate` (W produced this tick).
41. **LogicType.Maximum** (23, vanilla, read-only) - theoretical `PowerGenerated()` at current sun + weather.
42. **LogicType.Ratio** (24, vanilla, read-only) - `GenerationEfficiency` (0..1).

### 19.6 WindTurbineGenerator

Class `WindTurbineGenerator : Device` at L138706.

43. **LogicType.PowerGeneration** (65, vanilla, read-only) - `_generatedPower` (W produced this tick).

### 19.7 GasFuelGenerator / PowerGeneratorPipe

Class `PowerGeneratorPipe : DeviceInputOutput` at L375414.

44. **LogicType.PowerGeneration** (65, vanilla, read-only) - `_energyAsPower` (W produced this tick, = combustion energy × 17% efficiency).

### 19.8 SolidFuelGenerator (StationGenerator coal genny)

Class `SolidFuelGenerator : PowerGeneratorSlot : DeviceImport` at L400538.

45. **LogicType.PowerGeneration** (65, vanilla, read-only) - `PowerGenerated` (20000 W) when `PoweredTicks > 0 && OnOff`, else 0.
46. **LogicType.Mode** (3, vanilla, explicitly NOT readable) - per `SolidFuelGenerator.CanLogicRead:400656`.

### 19.9 StirlingEngine

Class `StirlingEngine : DeviceInputOutput` at L402334.

47. **LogicType.PowerGeneration** (65, vanilla, read-only) - generator output. Inherited from generator base.

### 19.10 RadioscopicThermalGenerator (RTG)

Class `RadioscopicThermalGenerator : Electrical` at L395566. No custom logic surface; `PowerGenerated` is a constant (50000 W) NOT exposed as a LogicType.

(Only inherits Device base surface: Power, On, ReferenceId, Color, Error, Lock.)

### 19.11 PowerConnection (dynamic-generator coupler)

Class `PowerConnection : Electrical` at L386738. No custom logic surface beyond Device base. PowerConnection is a passive 2-port bridge; it has no IC10-readable state.

### 19.12 WirelessPower base (PowerTransmitter + PowerReceiver share this)

Class `WirelessPower : ElectricalInputOutput` at L405441. Both `PowerTransmitter : WirelessPower` (L387065) and `PowerReceiver : WirelessPower` (L386861) inherit this surface.

48. **LogicType.Horizontal** (20, vanilla, read+write) - `Horizontal * 360` degrees.
49. **LogicType.Vertical** (21, vanilla, read+write) - `Vertical * 180` degrees, clamped [0, 180].
50. **LogicType.HorizontalRatio** (34, vanilla, read+write) - raw 0..1.
51. **LogicType.VerticalRatio** (35, vanilla, read+write) - raw 0..1.
52. **LogicType.Charge** (11, vanilla, read-only) - `AvailablePower` = `PotentialLoad` from input network.
53. **LogicType.PowerPotential** (25, vanilla, read-only) - `InputNetwork.PotentialLoad`.
54. **LogicType.PowerActual** (26, vanilla, read-only) - `OutputNetwork.CurrentLoad` (includes any storage-charge flow crossing the pair, §9.5).
55. **LogicType.PositionX** (76, vanilla, read-only) - `RayPosition.x`.
56. **LogicType.PositionY** (77, vanilla, read-only) - `RayPosition.y`.
57. **LogicType.PositionZ** (78, vanilla, read-only) - `RayPosition.z`.
58. **LogicType.Mode** (3, vanilla, read-only) - link state. Explicitly NOT writable.

PowerTransmitterPlus-added (TX and RX only):

59. **LogicType.MicrowaveSourceDraw** (6571, PTP-added, read-only) - W pulled from source on TX side; resolves to TX's value on RX. `LogicReadoutPatches.cs`.
60. **LogicType.MicrowaveDestinationDraw** (6572, PTP-added, read-only) - W delivered to receiver's downstream net.
61. **LogicType.MicrowaveTransmissionLoss** (6573, PTP-added, read-only) - `SourceDraw - DestinationDraw`.
62. **LogicType.MicrowaveEfficiency** (6574, PTP-added, read-only) - `1 / (1 + k * distance_km)`, 0..1.
63. **LogicType.MicrowaveAutoAimTarget** (6575, PTP-added, read+write conditional on `EnableAutoAim`) - target Thing ReferenceId.
64. **LogicType.MicrowaveLinkedPartner** (6576, PTP-added, read-only) - linked partner's ReferenceId.

PGP-added (TX and RX):

65. **LogicType.LogicPassthroughMode** (6577, PGP-added, read+write) - per-device passthrough. Default 1.
66. **LogicType.Priority** (6578, PGP-added, read+write on PT only) - PT/PR pair's dispatch priority.
67. **LogicType.Shedding** (6579, PGP-added, read-only on PT only) - 1 when pair is in shed lockout.
68. **LogicType.Overloaded** (6580, PGP-added, read-only on PT only) - 1 when pair is in overload lockout.
69. **LogicType.CycleFault** (6581, PGP-added, read-only on PT only) - 1 when pair is in cycle-fault lockout.

### 19.13 RocketPowerUmbilicalFemale

Class at L147895. Extends `ElectricalInputOutput` but does NOT override logic methods. Only Device base surface available.

(`_powerStored` is internal; not IC10-readable.)

### 19.14 RocketPowerUmbilicalMale

Class at L148269. Overrides at L148535. Restricted surface:

70. **LogicType.Activate** (9, vanilla, explicitly NOT readable) - per `CanLogicRead:148537`. Server-side action only.
71. **LogicType.Open** (2, vanilla, write-only-special) - writes route through `OnServer.Interact(InteractOpen, ...)` when OnOff and no error.
72. **LogicType.Mode** (3, vanilla, explicitly NOT writable).

### 19.15 BatteryCell (portable)

Class at L321224. Not a Device on the cable grid. Exposes via `LogicSlotType` (slot-keyed reads from a parent device), not direct `LogicType`.

73. **LogicSlotType.Charge** (slot-keyed) - `PowerStored`.
74. **LogicSlotType.ChargeRatio** (slot-keyed) - `PowerRatio`.

### 19.16 Cable

Class `Cable : SmallSingleGrid` at L371283. Not `ILogicable`. NO IC10 surface. `MaxVoltage` is static, not exposed.

### 19.17 Cross-device LogicType summary

For quick reference, the LogicTypes that appear on many power devices:

- **Power** (1), **On** (28), **Error** (4), **Lock** (10), **Color** (38), **ReferenceId** (217): every IPowered device.
- **Setting** (12): Transformer only.
- **Mode** (3): Battery, APC, SolidFuelGenerator (hidden), WirelessPower, GasFuelGenerator.
- **Maximum** (23), **Ratio** (24): Battery, Transformer, APC, SolarPanel.
- **Charge** (11): Battery, APC, SolarPanel, WirelessPower TX/RX.
- **PowerPotential** (25), **PowerActual** (26): Battery, Transformer (PGP-added), APC, WirelessPower.
- **PowerGeneration** (65): WindTurbineGenerator, GasFuelGenerator, SolidFuelGenerator, StirlingEngine.
- **ImportQuantity** (29), **ExportQuantity** (31): vanilla slot-related; PGP no longer repurposes them on Battery (use MaxChargeSpeed / MaxDischargeSpeed instead).
- **MaxChargeSpeed** (6583), **MaxDischargeSpeed** (6584), **ChargeSpeed** (6585), **DischargeSpeed** (6586): PGP-added on Battery; configured cap and live actual rate.
- **Horizontal** (20), **Vertical** (21), **HorizontalRatio** (34), **VerticalRatio** (35): SolarPanel, WirelessPower.
- **PositionX/Y/Z** (76/77/78): WirelessPower only.

### 19.18 Lockout-related LogicTypes summary

- **Shedding** (6579, PGP, read-only): upstream supply insufficient. Reset by 60s timer or OFF-toggle.
- **Overloaded** (6580, PGP, read-only): downstream demand exceeds the transformer's active `Setting` cap (the static link rating on a PT/PR pair, §8.4). Reset by 60s timer or OFF-toggle.
- **CycleFault** (6581, PGP, read-only): device is part of a closed loop. Reset by 60s timer or OFF-toggle. NEW.

All three exposed on Transformer and (for the PT/PR pair) on PowerTransmitter. Both Shedding and Overloaded and CycleFault read independently; multiple can be 1 simultaneously.
