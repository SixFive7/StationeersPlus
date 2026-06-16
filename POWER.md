# Power Grid Plus — Power System Specification

Authoritative spec for the post-refactor power simulation. Reads as a reference, not a tutorial. Defines invariants, algorithms, and behaviour rules in enough detail to implement against without consulting the conversation that produced it.

## 0. Resolved decisions (2026-06-10) — these override any contradicting text below

This spec was partly implemented (pass 1, 2026-06-09/10) and then reviewed with the developer, who locked the decisions below. Where the body of this document contradicts one of these (the spec has a couple of internal contradictions and one wrong vanilla value), **these decisions win**. The implementation checklist and current status live in `POWERTODO.md` ("2026-06-10 decisions and current status") and `POWER_DEVIATIONS.md` (file-by-file done/remaining). The standing instruction is to implement EVERYTHING (no deferring), building and testing after each step.

1. **Single inner-tick architecture.** Atomic Phases 1 and 3 run the VANILLA `PowerTick` (Initialise / CalculateState / ApplyState), not a custom `PowerGridTick` subclass. All PowerGridPlus behaviour comes from device-method postfixes and the atomic Phase 1.5. `PowerGridTick.cs` is to be removed once its current jobs (wrong-tier burn -> Phase 1.5a, generator-overflow burn §5.7, the vanilla recursive belt-and-braces) are relocated. (Pass 1 kept `PowerGridTick`; that is being reversed.)
2. **Cable caps are non-mutating.** `Cable.MaxVoltage` (a per-instance, save-serialized field; misleadingly named — it is a Watts cap) is NOT rewritten. The configured per-tier caps come from a helper that every cap-reader consults, including vanilla's own cable-burn check (patched). Defaults: normal **5000**, heavy **100000** (true vanilla; §5.6's "50000" is wrong), super-heavy **0 = unlimited** — all three configurable. The README states caps are runtime-enforced, not baked into the save.
3. **Failure colour is distinct (resolves the §11 / open-questions contradiction).** SHED = orange `#ffa500`; OVERLOAD / CYCLE_FAULT / VARIABLE_VOLTAGE_FAULT = red `#ff2626`. Highest-precedence active fault picks the colour: CYCLE > VVF > OVERLOAD > SHED.
4. **Cycle detection is a DIRECTED-SCC walk (replaces §4.2.5's undirected bipartite DFS).** Nodes = cable networks; each segmenter contributes one directed edge InputNetwork -> OutputNetwork; a cycle = a strongly-connected component of size >= 2 (Tarjan). Edges gated on OnOff + both-networks-non-null + Input != Output; wireless PT/PR via the shared `WirelessNetwork` node. The undirected model false-positives on parallel same-direction transformers/batteries (normal redundancy); the directed model does not. Only powered SCCs fault (min(Potential,Required) > 0 on a member network).
5. **Producer-isolation (VVF) is ALWAYS-ON, fault+zero, with a cable-burn fallback for unknown producers.** Known producers (the classifier list) fault and stop generating (reversible; button-bearing ones flash red, solar/wind/RTG hover-only) — no cable burn for known producers (resolves §1.6.5's internal contradiction). A producer-LIKE device NOT in the known list (new game version / modded) falls back to the original cable-burn handling so it is still caught. No enable/disable toggle.
6. **Fault visuals on every faultable device.** The flash + hover-countdown attach to every segmenter (Transformer, Battery, AreaPowerControl, PowerTransmitter, PowerReceiver, RocketPowerUmbilicalMale; RocketPowerUmbilicalFemale hover-only) and every button-bearing producer; solar/wind/RTG are hover-only.
7. **Emergency lights are a configurable prefab list.** `EnableEmergencyLights` applies to a configurable comma-separated list of light prefab names (default `StructureWallLightBattery`), not a single hardcoded prefab.

## 1. Architectural overview

PowerGridPlus owns `ElectricityManager.ElectricityTick` via a Harmony prefix-return-false. The mod replaces vanilla's per-network iterate-then-flow with a five-phase atomic flow that:

- Computes fresh per-network supply and demand BEFORE any actual power flow occurs (Phase 1).
- Runs a global allocator that decides every shed and every soft-demand share in a single deterministic pass (Phase 2).
- Re-runs vanilla power flow with the allocator's decisions already in effect (Phase 3).
- Runs vanilla's per-device post-tick and IC10 phases (Phases 4 and 5).

The mod's invariants:

- Voltage tiers are always on. Cables of different tiers are mutually incompatible; transformers and Area Power Controllers (APCs) bridge between them.
- Recursive (cycling) cable networks are always faulted. PowerGridPlus's Phase 1.5b detects every powered cycle and trips every segmenting device in it into CYCLE_FAULT for 60 s. No cables burn from cycles. Vanilla's `CheckForRecursiveProviders` and destructive cable burn still run unsuppressed as belt-and-braces; in normal operation it never fires because every cycle has already been broken by faulting its members.
- Re-Volt is incompatible and refused at load.
- The 5-phase tick is atomic: every shed or soft-demand allocation that the allocator decides at tick N takes effect at tick N's downstream device flow. No one-tick latency. No oscillation.

## 2. The five-phase atomic electricity tick

`AtomicElectricityTickPatch.Prefix` replaces vanilla `ElectricityTick`'s body. Within the prefix:

1. **OBSERVE.** For each cable network: `PowerTick.Initialise(net)` then `PowerTick.CalculateState()`. This populates `PowerTick.Required` and `PowerTick.Potential` per network using the device-level `GetUsedPower` and `GetGeneratedPower` reads. Soft-demand devices' postfixes pass through with the raw values during this phase (more below).

2. **DECIDE.** `TransformerAllocator.RunAtomic(currentTick)`. Walks the global topology, classifies each transformer's direction, computes rigid demand, executes the bottom-up shed cascade, then walks the surplus distribution. Outputs:
   - Set of transformers freshly entering shed lockout (written to `BrownoutRegistry`).
   - Set of transformers freshly entering overload lockout (written to `OverloadRegistry`).
   - Per-soft-demand-device "allocated share" (written to `SoftDemandShareCache`).
   - Per-tick full fault-registry snapshots broadcast to clients (one `FaultRegistrySnapshotMessage` per kind: shed / overload / cycle / variable-voltage / dead-input, §13).

3. **ENFORCE.** For each cable network: `Initialise + CalculateState + ApplyState`. The re-`CalculateState` reads the freshly set shed/overload flags via our `GetGeneratedPower`/`GetUsedPower` patches, returning 0 for locked-out transformers, and reads soft-demand allocations via the `SoftDemandShareCache` postfix. Vanilla `ApplyState` then runs unmodified. Trailing field copies mirror vanilla `CableNetwork.OnPowerTick`: `RequiredLoad`, `CurrentLoad`, `PotentialLoad`, `ShortfallLoad`.

4. **PER-DEVICE POWERTICK.** `ElectricityManager.AllPoweredThings.ForEach(p => p?.OnPowerTick())`. Vanilla copy. Every `IPowered.OnPowerTick` patch from other mods (BatteryLight, HaulerMod, ScriptedScreens) fires here as in vanilla.

5. **CIRCUIT HOLDERS.** `CircuitHolders.Execute()`. Vanilla copy. IC10 chips tick.

Threading: vanilla schedules `ElectricityTick` on the UniTask ThreadPool worker. The atomic prefix inherits that thread. No Unity API calls inside Phases 1-3; managed memory only.

## 3. Voltage tiers (always-on invariant)

Stationeers has three cable tiers: normal, heavy, super-heavy. PowerGridPlus enforces tier integrity:

- A cable network is uniform in tier. A cable of one tier connected to a network of another tier triggers the lower-tier cable to burn at the junction, splitting the network. Tier violations are detected two ways (§4.3): on the worker thread each tick (Phase 1.5a, the backstop) and immediately on `CableNetwork.OnNetworkChanged` (a main-thread event fired by every membership mutation, so a junction is caught the instant it is built or loaded). The burn itself always executes on the main thread, so the split lands synchronously, before the next tick conducts across the junction. A network with a burn in flight is gated by `SplitPendingRegistry` until its split lands (detected by cable-count change), which replaces the old fixed burn cooldown with a state signal.
- Devices are tier-restricted: generators and stationary batteries on heavy; high-draw machines (Carbon Sequester, Furnace, Advanced Furnace, Arc Furnace, Centrifuge, Recycler, Ice Crusher, Hydraulic Pipe Bender, Deep Miner) may use heavy or normal; normal-cable devices include lights, IC10, sensors, doors, hydroponics, etc.; super-heavy is cables and transformers only.
- Transformers and APCs are tier-exempt. They are the bridges.

This invariant has no toggle. The `EnableVoltageTiers` setting is removed; the always-on rules apply unconditionally.

## 4. Recursive networks (cycle-fault invariant)

PowerGridPlus replaces vanilla's "burn one cable in the cycle" response with a new fault mode: every segmenting device that participates in a detected powered cycle enters CYCLE_FAULT, a third lockout state parallel to SHED and OVERLOAD. The cycle dissolves immediately because every device in it contributes 0 to its output network for the 60-second lockout duration. No cables burn from cycles.

Rationale:
- Burning a cable is destructive (player loses material), often in a non-obvious spot, and breaks immersion if the loop happens to be unpowered or only briefly powered.
- A fault on every dual-terminal device in the loop is a clear, localised, reversible signal: each "device-in-loop" turns off (red flash, hover text), the loop is broken because each device contributes 0, and after 60 s the devices auto-retry. If the player has rewired correctly the fault clears; if not, the fault re-fires immediately.
- This unifies the response: the player learns "red flash on a dual-terminal device == this device is faulted; check hover for cause" (cause = overload OR cycle-fault).

### 4.1 What vanilla detects

The recursive check walks `ElectricalInputOutput.IsProviderToDevice` (a depth-first search through `InputNetwork.PowerTick.InputOutputDevices` chains), capped at 512 hops via `Device.MaxProviderRecursionIterations`. The check covers:

- Direct 2-node cycles: T1 input on N1 + output on N2, T2 input on N2 + output on N1.
- Arbitrary-length cycles through any combination of `ElectricalInputOutput` devices: transformers, APCs, stationary batteries.
- Cycles up to 512 distinct intermediate devices.

Detection action: one cable on the cycle (or one fuse, if any exist on the anchor's network) is added to the breakable set per detected anchor. Phase 3's `ApplyState` runs `BreakSingleCable` / `BreakSingleFuse` same-tick, picking one entry randomly.

### 4.2 What vanilla misses

Two known gaps:

1. **Wireless cycles.** `PowerTransmitter` and `PowerReceiver` extend `WirelessPower : Device`, NOT `ElectricalInputOutput`. They never appear in any network's `InputOutputDevices` list and do not override `IsProviderToDevice`. A cycle that closes through a transmitter-receiver wireless hop is invisible to vanilla detection. With PowerGridPlus modelling PT/PR pairs as transformers (§6), wireless cycles must be detected by PowerGridPlus itself: a depth-first walk that follows `PowerTransmitter.LinkedReceiver` as an additional edge alongside `ElectricalInputOutput.InputNetwork`. When found, mark a cable on the cycle for burn the same way vanilla does. This is a PowerGridPlus extension.

2. **`_networkTraversalRecord` reuse bug.** Vanilla's `CheckForRecursiveProviders` calls `_networkTraversalRecord.Clear()` ONCE at the start, then iterates anchors WITHOUT clearing between them. The visited set carries over between anchor iterations, so an anchor whose ReferenceId was added during an earlier anchor's walk gets pruned immediately on its own first call. Concrete failure: two anchors A1 and A2 on the same network, A2 in A1's walk path. A1's walk does not close a cycle and returns false. A2 starts its own walk, finds itself already in the visited set, returns false immediately, missing any A2-anchored cycle that runs through a chain disjoint from A1's. Fix: clear `_networkTraversalRecord` inside the foreach, before each anchor. PowerGridPlus applies this fix via Harmony (transpiler or full-body prefix). Cost: one List clear per anchor, anchor counts are small (under 10 typical per network).

3. **Multi-hop loops with no provider anchor.** Vanilla `CheckForRecursiveProviders` walks outward only from a network's existing `Providers` list (`InputOutputDevices` entries that already report supplying power on the network). A cycle that closes through a chain where no participant currently reports as a provider is invisible: battery-only loops (every battery's `_powerProvided` is 0 because no downstream device has pulled yet), APC-only loops, self-sustaining mutual-feed loops where every network's `Required` is 0 because the loop is feeding itself. Vanilla anchors on the wrong vertex or finds no anchor at all; it never starts the walk.

4. **Single-anchor blind spots.** Even when vanilla finds an anchor, it walks from one anchor at a time and bails the moment it closes a single cycle. A topology with two disjoint cycles sharing a network anchors on one, breaks one cable, then the second cycle persists until the next tick re-detects it. Cumulative cost: cascading partial-tick burns where a single pass should have caught everything.

### 4.2.5 PowerGridPlus's own DFS (replaces vanilla-driven detection)

Phase 1.5b does NOT rely on `BreakableCables.Count > 0` or `CheckForRecursiveProviders` results as the cycle signal. Vanilla's walk has the structural gaps in §4.2 (wireless invisible, single-anchor blind spots, multi-hop loops with no provider anchor, battery-only / APC-only cycles with `Required = 0` everywhere). PowerGridPlus builds and walks its own graph.

Algorithm:

1. **Build the bipartite graph.** Nodes are (a) every `CableNetwork` in `CableNetwork.AllCableNetworks` and (b) every segmenting device in `SegmentingDeviceRegistry.AllSegmentingDevices` (per §5.0 and §8.0.1, sorted by `ReferenceId ASC`). Edges are undirected: each segmenting device contributes one edge to its `InputNetwork` and one edge to its `OutputNetwork`. Wireless PT-PR links contribute an additional network-to-network edge subject to the OnOff gate in §6.5.

2. **DFS from every segmenting device.** Walk from each segmenting device in sorted order. Track a per-walk visited set (cleared per starting device, never reused across walks); a back edge to a vertex already on the current DFS stack identifies a cycle.

3. **Mark cycle participants.** Every segmenting device on a cycle path is registered for CYCLE_FAULT (60s lockout). Devices that appear in multiple detected cycles get registered once with the longest applicable timer (effectively just the standard 60s, since all cycles fire at the same tick).

4. **Determinism.** Sorting `SegmentingDeviceRegistry.AllSegmentingDevices` by `ReferenceId ASC` before the DFS walk guarantees identical traversal order across MP peers without any float dependence; pair this with the §8.0.1 sort key for the allocator and Phase 1.5b becomes a free MP-deterministic operation.

5. **Vanilla detection ignored.** `CheckForRecursiveProviders` still runs as part of Phase 1 (we do not suppress it) and still populates `BreakableCables` / `BreakableFuses`. PowerGridPlus reads NONE of these as a cycle signal. They remain active as belt-and-braces per §17.7 / §17.25; in normal operation PGP's DFS catches every cycle first and Phase 1.5b never invokes `BreakSingleCable` / `BreakSingleFuse`. If a future bug ever lets a cycle slip past PGP's DFS, vanilla's destructive burn fires as the safety net.

This covers the gaps vanilla misses: wireless edges are first-class graph edges; multi-hop loops are walked end-to-end regardless of anchor presence; battery-only and APC-only cycles are walked because every battery and APC is a graph node regardless of its current `_powerProvided` value; self-sustaining mutual-feed loops are walked because the graph topology is purely structural and never consults `Required`.

### 4.3 Pre-allocator burn (Phase 1.5)

> 2026-06-13 (B+C rework): wrong-tier burns no longer attempt a same-tick network re-enumeration. The
> split from `Cable.Break()` is parented to `Cable.OnDestroy` and lands on the main thread at end of
> frame (see `Research/GameClasses/Cable.md`, "Network split on destruction"), so it is observed by the
> NEXT tick, not mid-tick. The cycle DFS (1.5b) therefore walks the pre-tier-split topology; this is
> harmless because a cycle through a soon-to-burn cable is real this tick and is dissolved by zeroing its
> members (§4) regardless of whether the cable is gone. The original "Phase 1.5a-post re-enumeration"
> step is removed. See POWER_DEVIATIONS.md P1 for the full rationale and the dedi verification.

The cycle burn fires BEFORE the allocator runs, so the allocator never sees a cycle's inflated `Potential` / `Required` numbers. Phase 1.5 sits between Phase 1 (OBSERVE) and Phase 2 (DECIDE):

1. **Phase 1**: per-network `Initialise + CalculateState`. Vanilla `CheckForRecursiveProviders` runs and appends `BreakableCables` / `BreakableFuses` entries (PGP ignores these for cycle detection per §4.2.5; they remain as belt-and-braces).
2. **Phase 1.5a (wrong-tier detection, worker thread)**: `VoltageTierEnforcer.Run` clears landed pendings (`SplitPendingRegistry.SweepLanded`), then walks every cable network and detects any tier violation per the §3 invariant using cached state only (`Connection.GetCable` reads the cached `LocalGrid`, cable types, the device list -- no `Transform` access, so it is worker-safe). A detected violation on a non-pending network is reserved in `SplitPendingRegistry` and its burn is marshalled to the main thread (`UnityMainThreadDispatcher`), where the mixed-tier victim walk (`ConnectedCables`, main-thread only) and `Cable.Break()` run synchronously; the split lands at end of frame. This is the per-tick backstop. The immediate path is the `CableNetwork.OnNetworkChanged` subscription (main thread), which re-checks and burns the instant any membership mutation creates a junction -- before any tick conducts across it.
3. **Phase 1.5b (cycle detection and CYCLE_FAULT)**: run the §4.2.5 DFS over `SegmentingDeviceRegistry.AllSegmentingDevices` (sorted by `ReferenceId ASC`) plus the wireless edges from §6.5, identify segmenting devices on each cycle path, mark them for CYCLE_FAULT (§4.5). It walks the current topology (any tier split queued in 1.5a lands next tick); a cycle through a doomed cable is dissolved by zeroing its members this tick regardless. PGP's DFS is the cycle signal; `BreakableCables` is not consulted.
4. **Phase 1.5 re-observe**: for any network whose state changed during 1.5b (newly cycle-faulted or VVF members now contributing 0), re-run `Initialise + CalculateState` so Phase 2 sees clean state.
5. **Phase 2**: allocator runs against the clean post-cycle-fault state, and skips committing new shed / overload lockouts on any network with a burn in flight (`SplitPendingRegistry.IsPending`), so no durable decision is made against a merged topology that is about to split (Option C).
6. **Phase 3**: ENFORCE as before. `ApplyState` no longer needs to drive cycle-burns; Phase 1.5b handled them.

Phase 1.5a then 1.5b is the locked order. Doing it the other way would let cycle detection walk through wrong-tier cables that are about to vanish, wasting the work and (worse) potentially marking devices that the upcoming 1.5a burn would have isolated anyway.

### 4.4 Only powered loops burn

A cycle whose loop carries no current is invisible to the player and burning a cable there would break immersion. Phase 1.5 burns only when the loop is actually carrying power:

For each `BreakableCables` / `BreakableFuses` entry, look up the entry's network. The loop is "powered" when the network's `_actual = min(Potential, Required) > 0`. Only burn powered entries; carry unpowered entries forward by simply NOT acting on them (they re-detect next tick automatically because `CheckForRecursiveProviders` re-walks every tick).

This means an unused cycle (e.g., a build-time mistake in an unpowered grid) sits silently until something downstream pulls power; the moment power flows, the cable burns.

### 4.5 Cycle-fault: how loops break

PowerGridPlus replaces vanilla's cable burn for cycles with a CYCLE_FAULT mode. When Phase 1.5 detects a powered cycle:

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

The vanilla `BreakableCables` / `BreakableFuses` lists are still populated by Phase 1's `CalculateState` (vanilla detection runs), but PowerGridPlus does NOT consume them as a cycle signal: per §4.2.5 cycle detection is PGP's own DFS over the segmenting-device graph. Phase 1.5b never calls `BreakSingleCable` / `BreakSingleFuse`. The lists are cleared at the start of the next tick by vanilla `Initialise`. They remain populated and live as belt-and-braces: if a future bug ever lets a cycle slip past PGP's DFS, vanilla's burn fires as the safety net (§17.7 / §17.25).

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

### 5.0.2 Cascade and surplus walk apply uniformly to (1)–(7)

The cascade (§8.0) and surplus walk (§9) treat all of (1) through (7) uniformly. PowerConnection plays no role.

Visual feedback varies by class: `RocketPowerUmbilicalFemale` has NO visual indicator on the device itself; fault state is communicated ONLY by hover text on the device. The Female side has no clickable `InteractableType.OnOff` interactable to host a material-swap flash, so it reuses the hover-only path defined for non-flash producers in §8.5 (a `GetPassiveTooltip` postfix appends the fault line + countdown to `__result.Extended`). See §11.4 for the full per-class flash / hover coverage.

Idle hover behaviour on `RocketPowerUmbilicalFemale`: when the device is NOT in a fault state, PGP does NOT touch the hover text at all. The vanilla `GetPassiveTooltip` output (which may be blank or show vanilla pre-built status text) is what the player sees. The PGP postfix early-returns if `BrownoutRegistry`, `OverloadRegistry`, `CycleFaultRegistry`, and `ProducerFaultRegistry` all report "not locked" for this `ReferenceId`. PGP injects content only when a fault is active.

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

Where `downstream_demand` is the rigid + allocated soft demand reaching this transformer's output network during Phase 2.

### 5.7 What burns cables, what triggers overload

> 2026-06-13 (P2 deterministic rework): the generator-overflow burn is now a DETERMINISTIC 20-tick
> running average (`CableBurnWindow`), not a per-tick probability roll. There is no RNG and no setting
> (`CableBurnFactor` is removed). A cable burns when the 20-tick (10 s at 2 Hz) running average of the
> generator power flowing on a network exceeds the weakest cable's cap; the victim is the cable at the
> output of the generator that produced the most over that window. See POWER_DEVIATIONS.md P2 for the
> full design and the dedi verification. Rule 3a below is updated to match; the overload side (3b) is
> unchanged.

The vanilla cable-burn check `cable.MaxVoltage < _actual` is retained but tightened. A cable burns only when GENERATOR-direct supply alone exceeds the cable cap. Transformer-derived overflow does NOT burn the cable; it trips the upstream transformers as overload.

Per network N at the end of Phase 2:

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

Power transmitter / receiver pairs are first-class transformers at the allocator layer. PowerGridPlus does NOT patch any per-device power method on `PowerTransmitter` or `PowerReceiver`; the synthetic transformer is a MODEL the allocator builds in Phase 2 by reading existing fields. The actual energy bookkeeping continues to run through vanilla `PowerTick.Initialise / CalculateState / ApplyState` in Phases 1 and 3, calling `GetGeneratedPower`, `GetUsedPower`, `UsePower`, `ReceivePower` exactly as in vanilla.

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

`PT.GetGeneratedPower(WirelessOutputNetwork)`, read during Phase 1 OBSERVE, returns the link's **deliverable** cap (what the receiver side can be handed this tick): under vanilla the distance-derated number, under PowerTransmitterPlus the un-derated `min(MaxTransferCapacity, InputNetwork.PotentialLoad)`. It is the OUTPUT-side cap in both models and does NOT include the PowerTransmitterPlus source-draw multiplier.

The synthetic transformer's `effective_cap` (§5.5 form) is:

```
PT_effective_cap = min(
    PT.GetGeneratedPower(WirelessOutputNetwork),       // deliverable cap (output side)
    PT.InputConnection.GetCable().MaxVoltage / m,       // input cable carries delivered * m
    PR.OutputConnection.GetCable().MaxVoltage           // output cable carries delivered
  ) - PT.UsedPower - PR.UsedPower                       // both dish quiescent draws
```

Here `m` is the PowerTransmitterPlus source-draw multiplier (1 under vanilla or when PowerTransmitterPlus is absent). The input-cable bound is divided by `m` because a long link draws `delivered * m` through its input cable, and the pair's demand on its INPUT network is `throughput * m`, not `throughput` (§8.4.2). The allocator reads `m` via `PowerTransmitterPlusInterop.SourceDrawMultiplier` (reflection; returns 1 when PowerTransmitterPlus is absent).

PT/PR pairs are shed-eligible (§5.2) when the pair's Direction classifies as StepDown or SameTier.

### 6.4 Shed / overload enforcement

Shed and overload lockouts on a PT/PR pair take the link offline: PT draws 0, PR generates 0, downstream goes dark for the 60-second lockout. Same OFF-as-reset behaviour as wired transformers, applied via the PT's on/off button.

Enforcement is via the same registries (`BrownoutRegistry`, `OverloadRegistry`) keyed by PT.ReferenceId. PowerGridPlus's `GetGeneratedPower` postfix on `PowerTransmitter` checks the registries and returns 0 when locked out (this is one of the very few places PowerGridPlus needs to touch a PT/PR per-device method, and it is additive over PowerTransmitterPlus's existing patches: Harmony priority is set late so PowerTransmitterPlus computes its distance-loss number first, then the lockout postfix overrides to 0 if applicable).

### 6.5 Wireless edge gating (OnOff, bidirectional pair enumeration)

A wireless edge between a `PowerTransmitter` PT and a `PowerReceiver` PR exists in Phase 1.5b's bipartite graph if and only if:

1. The pair is established: `PT.LinkedReceiver != null` AND `PR._linkedPowerTransmitter != null`. Both cross-references must be set; either being null means the pair is half-broken (a load-time edge case or a mid-pairing transient) and no edge is contributed.
2. BOTH ends are turned ON: `PT.OnOff == true` AND `PR.OnOff == true`. A player turning either dish OFF removes the edge from the graph; cycle walking treats the link as cut.

Fault state does NOT affect edge existence. Per the refined skip-while-faulted rule (§17.39), Phase 1.5b's DFS walks through faulted devices for NEW cycle detection, so faulted PT or PR continues to contribute its edge to the graph. This is consistent with the wired transformer case: a faulted Transformer's `InputConnection` and `OutputConnection` are still graph edges; only its 0-throughput contribution falls out of Phase 2's allocator math.

Pair enumeration: walking only `PowerTransmitter` instances and dereferencing `LinkedReceiver` would miss multi-PR fan-out from a single PT, since the PT-side list can only hold one canonical link at a time and the second PR's edge would be invisible. PGP walks BOTH the PowerTransmitter and PowerReceiver instance lists, building the edge set as a deduplicated `HashSet<(long PtRefId, long PrRefId)>` keyed by the pair's two ReferenceIds. The HashSet de-dupes when both walks encounter the same pair. This catches every linked pair regardless of which side carries the canonical reference.

### 6.6 PowerTransmitterPlus compatibility

PowerTransmitterPlus and PowerGridPlus coexist with one small, optional coordination point. The atomic Prefix-return-false on `ElectricityManager.ElectricityTick` replaces only the outer scheduler. Per-device methods (`PowerTransmitter.GetGeneratedPower`, `UsePower`, `GetUsedPower`, `ReceivePower`) still run inside Phases 1 and 3 via `PowerTick.Initialise / CalculateState / ApplyState`. PowerTransmitterPlus's distance-cost quartet, auto-aim patches, link-visibility patches, save side-cars, and IC10 readouts all survive unchanged.

The one coordination point: PowerGridPlus reads PowerTransmitterPlus's `DistanceCostShared.SourceDrawMultiplier(PowerTransmitter)` by reflection to model the pair's `delivered * m` source-side draw (§6.3 / §8.4.2). It is a soft dependency (PowerGridPlus never hard-references PowerTransmitterPlus and degrades to `m = 1` when PowerTransmitterPlus is absent), so the two mods still load and run independently. Without it the allocator under-counts a long link's source-network draw and the no-partial-power invariant fails on the transmitter's input network.

The only PowerGridPlus additions touching PT/PR per-device methods are:
- A late-priority postfix on `PowerTransmitter.GetGeneratedPower` to return 0 when locked out.
- (Optional) a hover-text postfix on the PT button when locked out.

Neither conflicts with anything PowerTransmitterPlus does.

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
- APC passthrough portion (downstream consumers behind the APC, summed into APC's reported `GetUsedPower` on its INPUT net). Transparent to the shed math; split out at allocator time per §7.5.

Soft demand on Input network only:
- Stationary batteries (charge cap headroom).
- Large stationary batteries.
- Nuclear batteries (MorePowerMod, detected at runtime via type name).
- APC's internal cell charge demand.

Elastic supply on Output network only:
- Stationary batteries when they have stored energy and downstream rigid demand exceeds upstream rigid supply.
- APC's internal cell on the same condition (subject to the in-tick interlock above).

Note: Power Transmitters are NEITHER rigid nor soft. They are transformers, treated by §6.

### 7.3 Battery and APC as elastic supply on the Output network

Generators (coal generator, solar panel, RTG, wind, hydrogen burner) are rigid supply: they produce their `GetGeneratedPower` value whether anything consumes it or not. Batteries and APC cells are elastic supply: PowerGridPlus's allocator discharges them only to fill the rigid shortfall on their Output network, never more.

**Per-battery effective cap accounts for both rate AND stored energy:**

```
effective_discharge_i = min(DischargeRateCap_i, PowerStored_i)
```

A battery with charge 19 W-tick stored cannot deliver 200 W this tick even if its rate cap allows; the effective cap is 19 for that battery.

**Algorithm per Output network N with rigid demand `D_N` and generator supply `G_N`:**

1. If `G_N >= D_N`: every battery and APC on N discharges 0. Done.
2. Else: shortfall `S_N = D_N - G_N`. For each battery/APC on N, compute `effective_discharge_i = min(DischargeRateCap_i, PowerStored_i)`.
3. Compute `effective_total = sum(effective_discharge_i)`.
4. If `S_N >= effective_total`: every battery delivers its full `effective_discharge_i`. Residue `S_N - effective_total` is rigid demand still unmet (which the joint allocator §8 then treats as triggering shed/overload on suppliers).
5. If `S_N < effective_total`: proportional share, each battery delivers `share_i = effective_discharge_i × (S_N / effective_total)`. By construction `share_i <= effective_discharge_i`, so no saturation occurs.

Worked example: 2 batteries on the same output network, both with discharge rate 200 W. Battery A is full (stored >> 200). Battery B has only 19 stored. Shortfall 300 W on the network.

- effective_A = min(200, full) = 200.
- effective_B = min(200, 19) = 19.
- effective_total = 219.
- `S_N = 300 >= effective_total = 219`, so each delivers its full effective: A = 200, B = 19. Sum 219. Residue 81 W rigid still unmet. The allocator (§8) then considers the rigid supply ceiling on N to be `G_N + 219`, and if `D_N > G_N + 219` triggers shed somewhere upstream.

Note we do NOT throttle Battery A to "equal share" (300/2 = 150). Battery A delivers its full capable amount; Battery B contributes whatever it can; the residue surfaces as unmet rigid demand to the allocator.

For shed planning (§8.0 iteration) the network's rigid supply ceiling is `G_N + sum_of_effective_discharge_i` plus inbound flow from non-shed upstream transformers. If rigid demand exceeds this ceiling, shed cascade or overload triggers.

### 7.3.0.1 Critical implementation note: SoftSupplyShareCache

**Without an explicit `Battery.GetGeneratedPower` postfix the elastic-cap rule is allocator math that never reaches Phase 3's vanilla `CalculateState`.** The adversarial review confirmed this is the central gap that breaks the "no partial power" invariant. Same for APC's `GetGeneratedPower`.

The §7.3 algorithm sets a per-battery `delivered_i` per tick. To make this stick at Phase 3, PowerGridPlus writes the value into a `SoftSupplyShareCache` (parallel to `SoftDemandShareCache` for charge demand) and adds Harmony postfixes:

```csharp
// Battery.GetGeneratedPower postfix
__result = Mathf.Min(__result, SoftSupplyShareCache.GetShare(__instance.ReferenceId));

// AreaPowerControl.GetGeneratedPower postfix (same shape)
// RocketPowerUmbilicalFemale.GetGeneratedPower postfix (same shape)
// RocketPowerUmbilicalMale.GetGeneratedPower postfix (same shape)
```

Cache lifecycle is a freshness-stamp model. The cache holds entries keyed by `ReferenceId` with payload `(long tickWritten, float share)`. Phase 2 (joint shed / overload / cycle-fault iteration) writes `(currentNetworkTick, share)` for every active supplier as the iteration converges. The `GetGeneratedPower` postfix reads the cache: if an entry exists AND `tickWritten >= currentNetworkTick - 1`, the postfix returns the cached share. Otherwise (entry older than one tick or absent), the postfix falls back to vanilla (`_powerRatio * stored`). Self-cleaning: stale entries from cable breaks or supplier reassignment are naturally distrusted on read; no explicit invalidation step is needed. In-memory only, not serialised.

With this cache + postfix in place, the §7.3 elastic discharge cap propagates to vanilla `_powerRatio` math. Without it, Phase 3 sees raw `PowerStored` as Potential, and any time `0 < PowerStored < rigid_demand` (a normal "battery almost empty" gameplay state) produces `_powerRatio < 1` -> partial-power on rigid devices.

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

A battery's `GetUsedPower(InputNetwork)` reports `PowerMaximum - PowerStored` (full headroom). PowerGridPlus's allocator treats this as soft demand: the battery charges up to its `EffectiveChargeCap` with whatever surplus arrives on its Input network, scaled by available headroom (§9 surplus distribution). A battery is simultaneously a soft demander on its Input network AND an elastic supplier on its Output network. Per §7.1 the two roles run independently in the same tick when the two networks are distinct.

### 7.5 Splitting APC demand

`AreaPowerControl.GetUsedPower(InputNetwork)` returns passthrough + internal-charge bundled. PowerGridPlus splits these two at allocator time:

- Passthrough = APC's output network's rigid demand. Read via Phase 1's populated `PowerTick.Required` on `APC.OutputNetwork` minus any soft demand on that net.
- Internal-charge = APC's reported `GetUsedPower(InputNetwork)` − passthrough. Floor at 0 if the math goes transiently negative.

The internal-charge portion enters the soft-demand request flow (§9). The passthrough portion stays in rigid demand.

### 7.6 Soft-demand request value

Each soft-demand device declares a `RequestedShare` = its `EffectiveChargeCap`, further capped by the battery's remaining headroom (`PowerMaximum - PowerStored`). The dynamic "scale charge by network headroom" math already living on APC is extracted into a shared helper `SoftDemandHeadroomCalculator` and called from the patch on every soft-demand device's `GetUsedPower`.

## 8. The shed + overload allocator pass

### 8.0 Joint shed / overload / cycle-fault computation (iteration to fixed point)

Shed, overload, and cycle-fault interact in non-trivial ways inside a single tick:

- A shed transformer contributes 0 to its output network. Its siblings on the same output network now bear more load and may individually exceed their `Setting` -> OVERLOAD.
- An overloaded transformer also contributes 0. Its input network is no longer drawing from. Its siblings on the same INPUT network now have more headroom.
- A cycle-faulted device contributes 0 on both sides. Its participation in any subtree disappears for the tick.
- A battery on an output network with rigid demand greater than upstream supply + its discharge contributes triggers the upstream shed cascade. The shed iteration must account for the elastic supply ceiling.

Because vanilla mods cannot iterate across ticks without observable lag, the joint state must converge inside Phase 2. Algorithm:

```
state = {sheds: empty set, overloads: empty set, cycle_faults: empty set (from Phase 1.5)}
repeat:
    new_state = evaluate_one_round(state)
    if new_state == state: break
    state = new_state
commit state to BrownoutRegistry / OverloadRegistry / CycleFaultRegistry
```

Where `evaluate_one_round` does (within Phase 2; Phase 1.5 has already populated `cycle_faults` before Phase 2 starts):

1. **Battery elastic-supply re-evaluation per output network.** For each output network N with rigid demand `D_N`, generator supply `G_N`, and battery effective caps:
   - `effective_total_N = sum(min(DischargeRateCap_i, PowerStored_i))` for batteries / APCs on N that are not in `state`.
   - `available_N = G_N + effective_total_N + sum_of_supplying_transformers_actual_throughput`.
   - If `D_N > available_N`: the network is undersupplied. Walk UP from N's segmenting devices to find shed candidates per the §8 priority rules. If no upstream shed-eligible device exists (i.e., generators / batteries directly serve rigid loads), accept the brownout per §8.5.

2. **Shed evaluation, bottom-up.** For each segmenting device (transformer, APC, PT/PR pair, battery, etc.) in deepest-first order:
   - Compute the device's full required input draw assuming current `state`: `required_input = actual_throughput + UsedPower` where `actual_throughput = min(effective_cap, downstream_demand_with_state)`.
   - If the device's input network cannot supply `required_input` from `(generators + battery_discharge_effective_caps + sibling_transformers_already_supplying)`, mark for SHED.
   - Per-input-network priority sort (lowest priority + largest demand sheds first) governs which siblings shed when multiple options exist.

3. **Overload evaluation, top-down.** For each segmenting device:
   - Compute output network demand assuming current `state`.
   - If `output_demand_share > Setting`, mark for OVERLOAD.
   - Output cable overload (per §5.7): if `actual_flow > OutputCable.MaxVoltage` and overflow comes from upstream segmenting devices (not direct generators), mark ALL upstream devices feeding this network for OVERLOAD.

4. **Return new state.**

Convergence guarantee: each iteration can only ADD entries to `sheds` / `overloads` / `cycle_faults`, never remove (cycle_faults and producer_faults specifically are frozen after Phase 1.5; they don't change during Phase 2 iteration). Each segmenting device can be added at most once. Therefore the loop terminates in at most `N` iterations where `N` is the segmenting-device count.

External observation: the entire iteration happens inside Phase 2 of one game tick. From outside Phase 2 there is one joint result. The atomic invariant (POWER.md §17.1) holds.

### 8.0.0.1 Allocator contributor list MUST include every segmenting device

The current `TransformerAllocator.RunAtomic` iterates only `is Transformer` instances. POWER.md §5.0 enumerates 7 segmenting device classes (Transformer, Battery, AreaPowerControl, PowerTransmitter, PowerReceiver, RocketPowerUmbilicalMale, RocketPowerUmbilicalFemale). The allocator MUST process all seven as contributors; any unhandled class is a hole through which `_powerRatio < 1` leaks on rigid downstream loads.

Per the adversarial review:
- Battery on an undersupplied output net with rigid loads -> partial.
- APC with depleting internal cell and rigid downstream -> partial.
- PT/PR pair (especially with PowerTransmitterPlus distance derate) feeding a rigid net beyond their effective cap -> partial.
- RocketPowerUmbilical with internal cell and rigid load on the rocket side -> partial.

Each must be added to the contributor list, classified by direction (§5.1), priority-sorted (§5.4), elastic-supply-capped (§7.3) where applicable, and shed/overload-eligible per §8.0. The shed cascade then catches the undersupply case BEFORE Phase 3's `CalculateState` reads inflated Required values.

POWERTODO Phase 5 / 6 already covers this expansion at the algorithm level. The new requirement layered in this round: every segmenting device class needs a `GetGeneratedPower` lockout postfix that returns 0 when in SHED, OVERLOAD, CYCLE_FAULT, or VARIABLE_VOLTAGE_FAULT. Today only Transformer has this postfix (`TransformerExploitPatches.GetGeneratedPowerPatch`).

### 8.0.0.2 Vanilla `_powerRatio < 1` is retained as the safety net

Even after the spec's invariants are fully implemented, the vanilla `_powerRatio = Potential / Required` scaling code path is INTENTIONALLY LEFT INTACT. Rationale per the user: "I do want to keep the vanilla partial powered logic in case we miss something or another mod opens up a gap in our safety net, as that is better than producing phantom power or some other mod error."

PowerGridPlus's claim is that the vanilla partial-power branch becomes unreachable in normal gameplay under the redesigned model. If a third-party mod adds a device class that PowerGridPlus doesn't classify, OR a future game update introduces a new code path, vanilla scaling gracefully degrades the gameplay rather than producing phantom power or corrupted state. The presence of this safety net is itself an invariant (§17.31).

### 8.0.1 MP-safe iteration ordering (integer-only sort key)

The iteration's deterministic order MUST use only integer keys. Floats are excluded because:
- IEEE 754 sums are order-sensitive; bit-exact identity across peers is fragile.
- The existing PowerGridPlus `CompareForAllocation` is already integer-only `(priority DESC, ReferenceId ASC)`.
- The proposed `(depth, priority, demand, ReferenceId)` would have been the first float dependence on a sort decision in the codebase — an avoidable footgun.

The locked sort key for Phase 2 iteration ordering:

```
(depth ASC, priority DESC, ReferenceId ASC)
```

- `depth`: integer level computed from generator-root distance via BFS (§5.5). Deterministic across peers when the cable network graph is identical.
- `priority`: integer per-transformer/per-PT setting (default 100). Deterministic per `PriorityStore`.
- `ReferenceId`: long, globally unique per Thing. Deterministic by save semantics.

`priority DESC` matches the existing `CompareForAllocation` convention: higher-priority devices dispatch first. The cascade still shed lowest-priority sibling first when the input cable budget is exceeded; the "shed candidate" selection is a separate priority comparison within the chosen sibling set on each input network (see §8.3 in the historical sub-section, where lowest priority sheds first).

If a magnitude-based tiebreaker is required for sibling selection, the demand must be quantised to integer Watts before entering the key:

```
demandWatts = (int)Math.Floor(actual_demand_float)
key = (depth ASC, priority DESC, demandWatts DESC, ReferenceId ASC)
```

Casting to int erases any ulp-level float divergence between peers (Watts are O(10^3-10^5), well clear of float precision boundaries). The cast is applied at the comparator call site only; the underlying float values are still used for physics. PowerGridPlus's existing `MergeDeterminismPatches` already enforces deterministic network-id ordering on cable merges, so the integer keys remain stable across peers.

The same `ReferenceId ASC` rule is used everywhere PGP enumerates segmenting devices: `SegmentingDeviceRegistry.AllSegmentingDevices` is sorted by `ReferenceId ASC` before the Phase 1.5b DFS walk (§4.2.5), before the Phase 1.5c producer-isolation walk (§8.5), and before every Phase 2 iteration round. One sort key across the whole tick. The cost is one `OrderBy` per per-tick walk; the gain is "MP determinism free, no float dependence anywhere in the iteration order."

### 8.5 Producer-isolation rule and VARIABLE_VOLTAGE_FAULT (strict-literal)

PowerGridPlus enforces a strict-literal rule: a producer's network may contain ONLY other producers and `Transformer` instances. Any other device on a producer's network is a violation. Explicitly: `Battery`, `AreaPowerControl`, `PowerTransmitter`, `PowerReceiver`, `RocketPowerUmbilicalMale`, and `RocketPowerUmbilicalFemale` do NOT satisfy producer-isolation, even though they are segmenting devices. Only `Transformer` does. Rigid consumers (lights, machines, IC10, etc.) and the other six segmenting classes are all violators.

What counts:
- **Producer**: SolarPanel (and Large / HeavyDouble variants), WindTurbineGenerator (and Large), PowerGeneratorPipe / GasFuelGenerator, PowerGeneratorSlot / SolidFuelGenerator (coal genny), StirlingEngine, RadioscopicThermalGenerator (RTG), TurbineGenerator (the small wall turbine), and PowerConnector (the portable-generator dock). Identified at runtime via `GetGeneratedPower(net) > 0` while the device is NEITHER an `ElectricalInputOutput` NOR a `WirelessPower`, OR by class-list lookup. (TurbineGenerator and PowerConnector were a §8.5 omission, added in deviation P11: both override `GetGeneratedPower`, so leaving them off would drop them to the unknown-producer cable-burn fallback per D6.) Note: `PowerConnection` is a DIFFERENT, vestigial dead-code class (§5.0.1) and is never a producer.
- **Transformer**: only `Transformer` instances (NOT Battery, NOT APC, NOT PT/PR, NOT RocketPowerUmbilical).
- **Violator**: any device on the producer's network that is neither a producer nor a `Transformer`. This includes rigid consumers AND every non-Transformer segmenting device class (Battery, AreaPowerControl, PowerTransmitter, PowerReceiver, RocketPowerUmbilicalMale, RocketPowerUmbilicalFemale).

**Active-source gate.** A producer is faulted only while it is an ACTIVE source (the form that actually feeds unregulated voltage), decided by `ProducerClassifier.IsActiveProducer`. A producer with an on/off button (gas / solid / stirling) counts while it is ON; a `PowerConnector` counts while its docked generator is delivering (the dock is a transparent proxy, it forwards the generator's power and has no source of its own, so an empty or switched-off dock is inert and never faults); a buttonless producer (solar / wind / wall turbine / RTG) always counts, since it cannot be switched off and produces whenever the environment allows. The connector reads the docked generator's raw `PowerGenerated`, not the connector's `GetGeneratedPower` (the VVF enforcement postfix zeroes that, so gating on it would oscillate). This is the VVF analog of the elastic-overload ON cohort (§8.4.1); an inactive producer is neither faulted nor counted toward the violation, so a producer that is off / not delivering is transparent to the rule exactly like a `Transformer` is.

Per-tick check, in Phase 1.5c right after the cycle-fault detector (1.5b):

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

Decide which transformers (including PT/PR pairs and other segmenting devices) enter shed or overload lockout this tick so that every surviving transformer's input network can satisfy its rigid demand AND every surviving transformer's output network demand is within its `Setting`. Minimise the count of subnets that go dark; satisfy higher-priority sibling subnets first; if siblings tie on priority, shed the one with the larger downstream demand first.

### 8.2 Pre-walk

Before any sort or compare:

1. For each network, compute the rigid demand: walk `PowerDeviceList`, sum `GetUsedPower` for rigid devices. Skip soft-demand devices. For APCs, sum only the passthrough portion (§7.1). Cache as `rigidDemandByNet[netRef]`.
2. For each network, compute the rigid supply (generators + non-shed-transformers' offered `GetGeneratedPower`). Cache as `rigidSupplyByNet[netRef]`.
3. For each step-down / same-tier transformer, compute `subtreeRigidDemand[T]` recursively: sum of (rigid demand on `T.OutputNetwork`) + (every downstream transformer's `subtreeRigidDemand`). For step-up transformers, this is undefined (they don't participate).

### 8.3 Bottom-up cascade

The cascade walks transformers from deepest to shallowest. At each transformer T:

1. Find T's `inputNet`.
2. Look at T's siblings on `inputNet` — other transformers consuming from the same input network.
3. Compute the rigid budget on `inputNet`: `rigidSupplyByNet[inputNet] − non_transformer_rigid_demand_on_inputNet`.
4. Each sibling's claim is its `subtreeRigidDemand` (post any deeper sheds already decided).
5. If the sum of siblings' claims ≤ budget, no sheds needed at this level. Proceed up.
6. If not, sort siblings by (priority ASC, subtreeRigidDemand DESC, RefId ASC). Walk this order — the FIRST sibling in this sort is the FIRST to shed.
7. Shed the first sibling. Subtract its `subtreeRigidDemand` from the level's claim total. Check fit again. Repeat until claims fit budget or all shed-eligible siblings are gone.
8. For step-up siblings in the loop: SKIP. They are not shed-eligible. Their claim stays.

Recursive interaction: when T sheds, T's `subtreeRigidDemand` becomes 0 (T contributes nothing downstream). When T does NOT shed but had deeper sheds resolve its subtree, T's claim already reflects those deeper sheds (because we walked bottom-up first).

Single-pass guarantee: each transformer is visited at most once; the order is bottom-up so by the time a transformer is evaluated, its subtree state is final.

Result: write each shed transformer's RefId into `BrownoutRegistry` with `_lockoutUntilTick = currentTick + 120` (60 seconds at 2 Hz).

### 8.3.1 Dead-input carveout and the no-upstream-supply cue

The shed step sheds the lowest-priority consumers when an input network's supply is short. One case is carved out: an input network with NO effective supply at all (`generators + upstream inflow + live battery discharge <= 0`, the same `totalAvail` the shed pass computes). Shedding such a network's consumers would lock them out for 60 s and re-fire every expiry forever, since the condition is permanent until the input is powered. Instead, a contributor (transformer / APC / PT pair) on a dead input simply IDLES: it is not shed, takes no lockout, does not flash, and recovers the instant its input is powered (no lockout to wait out). This is a deliberate pass-1 carveout (POWER_DEVIATIONS P7).

So the player still gets a signal, a dead-input contributor that is actively trying to pass power downstream (its modelled `Throughput > 0`) shows a steady, neutral-grey hover line `(No upstream supply)` with no countdown. It is an INFO cue, not a fault: lowest hover precedence (any real fault on the same device wins), it never drives the flash, and it carries no 60 s timer. The set is recomputed every tick from the converged allocator state (`DeadInputRegistry`) and mirrored to clients via the per-tick fault snapshot (`KindDeadInput`), so the cue shows identically on every peer. Batteries and rocket umbilicals are pure suppliers, not pull-through consumers, so they do not receive the cue.

### 8.3.2 Parallel-supplier demand split (priority-tiered, proportional within a tier)

§8 fixes the shed victim order and the §8.4 overload trigger but not how a MET demand divides across parallel suppliers (transformers / APCs / PT pairs) feeding one output network. The split is greedy by PRIORITY TIER, proportional by capacity WITHIN a tier:

- **Across tiers (greedy by priority).** Higher-priority suppliers fill before lower ones. A high-priority bank carries the load to its combined cap; lower-priority banks engage only once it is maxed. This makes priority a primary/backup control, consistent with its shed meaning (high priority delivers first and sheds last).
- **Within a tier (proportional by EffCap).** Suppliers at the SAME priority each deliver `tierGive * EffCap_i / sum(EffCap)`, so equal-priority suppliers share the tier's load in proportion to capacity (a 50 kW and a 5 kW transformer at equal priority split ~10:1, not 50/50). The split is self-bounding (each share <= its own EffCap) and conserves the tier total.

Interaction with §8.4: a tier delivering its full combined cap has every member at `EffCap` (all overload-eligible if demand is still unmet); a tier delivering less than its cap has every member at the same sub-cap fraction (none overload). So within a tier, overload is all-or-nothing, and across tiers the fully-engaged higher tiers are the ones that can trip. Determinism: the tier ORDER is integer-keyed (priority DESC, RefId ASC, §8.0.1); the within-tier proportional division is float but bit-identical across peers on the shared mono runtime, so the §8.4 discriminator probes still behave identically on every peer.

### 8.4 Overload: per-transformer hit-max trigger

An overload trips a transformer that is delivering at its active `Setting` cap while the output network still has unmet rigid demand. The trigger is per-transformer, not per-network. Note: a player who reduced Setting below OutputMaximum via IC10 will see overload fire at the lower threshold; this is intentional (they explicitly asked the transformer to throttle below its rated max).

Concrete check per transformer T after Phase 2 settles:

- `T.actual_throughput == T.Setting`, AND
- T's output network has unmet rigid demand (`rigid_demand > total_supply_arriving_on_T.OutputNetwork`).

When both hold, T enters `OverloadRegistry` with the 60-second lockout. Other transformers feeding the same output network are NOT auto-tripped; each one trips only if it independently meets the hit-cap condition.

Examples (assuming Setting = OutputMaximum at default):

- T1 (Setting 500 W) and T2 (Setting 500 W) feed an output network with rigid demand 1500 W. Both run at 500 W (their Setting) with 500 W unmet downstream. Both trip.
- T1 (input-cable-throttled to 200 W of its 500 W Setting) and T2 (Setting 500 W) feed an output network with rigid demand 900 W. T1 runs at 200 W (below its Setting), T2 runs at 500 W (at Setting). Only T2 trips. T1's actual throughput is bounded by cable cap, not by its own Setting, so the OverloadRegistry trigger does not fire for it.
- IC10 example: a player IC10 script writes `Setting = 200` on T1 to throttle it. T1's effective_cap drops to 200 - UsedPower. If downstream demand exceeds 200, T1 trips OVERLOAD (it hit its Setting with unmet demand). The player can later raise Setting back to OutputMaximum to clear the constraint.

Rationale for the per-transformer trigger: the player-facing diagnostic is "this transformer is being asked to push past its rated throughput." A throttled-by-upstream transformer is not the cause and should not be punished. The hit-max transformer is the visible culprit and the one the player needs to upgrade or split.

For PT/PR pairs the overload threshold is the pair's live deliverable cap (`PT.GetGeneratedPower(WirelessOutputNetwork)`, §6.3) as the equivalent of `Setting`. The same hit-the-cap-with-unmet-demand condition applies on the OUTPUT side. The pair's INPUT-side draw is a separate matter (`delivered * m` under PowerTransmitterPlus, §8.4.2). PT/PR Priority follows the same rules as wired transformers (the knob writes Priority; the pair has no writable Setting of its own).

**Input-limited pairs SHED, not OVERLOAD (deviation P6).** The deliverable cap collapses two different bottlenecks into one number: the link's own rated transfer cap, and the input network's available power (`GetGeneratedPower = min(rated cap, InputNetwork.PotentialLoad)`). When the deliverable is held down by the input (`liveCap >= InputNetwork.PotentialLoad - Eps`, the `Seg.InputLimited` flag), the SOURCE, not the link, is the bottleneck, so a downstream shortfall is routed to SHED ("insufficient upstream supply", pointing the player at the transmitter's input network) rather than OVERLOAD ("the link cannot carry the demand"). Only when the deliverable is bound by the link's own rated cap (input richer than the rating) does it OVERLOAD. Both take the pair offline (no-partial-power); only the diagnosis and the registry (`BrownoutRegistry` vs `OverloadRegistry`) differ. PowerTransmitterPlus applies no output-side loss, so `liveCap == PotentialLoad` exactly when input-limited; under an unlimited PowerTransmitterPlus cap every shortfall is therefore a SHED (the link is never the rating bottleneck). A long vanilla link whose distance loss pulls `liveCap` below `PotentialLoad` reads as link-limited (OVERLOAD), which is truthful, the loss is the link's own.

The `OverloadRegistry` is keyed by `ReferenceId` (per-device, consistent with `BrownoutRegistry`). No NetworkId-keyed variant is needed; the elastic re-arm sync in §8.4.1 is commit-time phase alignment of per-device entries, not a separate network-keyed store.

### 8.4.1 Elastic supplier overload: network-level retry and synced re-arm

§8.4's hit-max trigger is per-transformer. The same OVERLOAD outcome extends to elastic suppliers (Battery, AreaPowerControl, RocketPowerUmbilical*) so storage-fed networks honour the no-partial-power invariant: when a network's COMBINED effective discharge from all its elastic suppliers (Σ `min(rate cap, cable cap, stored)`) still leaves rigid demand unmet, every elastic supplier on that network trips OVERLOAD, contributes 0, and the subnet goes dark cleanly instead of partial-powering. Two batteries that individually fall short but together cover the load do NOT trip: the allocator sums their discharge before deciding (`EvaluateDemand` -> `AvailableElastic`), and only a genuinely unmeetable load trips them.

Unlike the per-transformer trigger, the elastic OVERLOAD is a NETWORK property ("this network's storage cannot meet its load"). `OverloadRegistry` stays per-device (keyed by `ReferenceId`; each device flashes / hovers / snapshots independently), but the commit is network-level with a RETRY before any reset. A net is a commit candidate when at least one of its elastics newly overloads this tick. Its overload cohort is every elastic on the net overloaded this tick or already overload-locked (all ON, since the §10.3 OFF-as-reset sweep clears the lockouts of switched-off devices every tick). The commit then:

- **Retry.** If the cohort's combined effective discharge would cover the net's residual demand, the situation has recovered (the load dropped, a device was toggled back on, or supply was added), so the cohort's overload locks are CLEARED and they rejoin next tick. No timer is reset.
- **Reset.** Only if the retry still leaves demand unmet are the cohort's per-device locks (re)stamped to ONE shared fresh expiry (`currentTick + LockoutDurationTicks`), which both arms the 60 s lockout and keeps the cohort phase-synced.

The shared expiry matters because the per-device timers can otherwise drift out of phase (one toggled off then on, or one locked first by an unrelated fault), after which the suppliers re-arm one at a time, each re-overloading alone because its siblings are still locked, leaving a network they could JOINTLY power dark forever. Syncing guarantees an individually-too-weak-but-jointly-sufficient bank always re-arms together. The retry makes a player toggle (or any supply change) an immediate network-level recovery attempt rather than a blanket timer reset: toggling one device on retries the whole on-cohort at once and recovers the net the same tick if they jointly suffice, and only eats a fresh 60 s if they genuinely cannot. The commit is self-resolving (after it the net is either all-recovered or all-locked, so neither branch re-triggers next tick); devices locked for CYCLE_FAULT / SHED are not in the overload cohort; a split-pending network defers its commit (Option C). Transformers keep their per-device §8.4 timer (the culprit transformer is genuinely device-specific; this is a network property).

### 8.4.2 PT/PR pair input-side draw (distance overhead)

The §8.4 overload check is an OUTPUT-side test (the pair delivering its full deliverable with downstream still unmet). The pair's INPUT side needs separate accounting because the deliverable and the source draw are not equal under PowerTransmitterPlus: the source network is billed `delivered * m`, where `m = 1 + k * distance_km` is PowerTransmitterPlus's source-draw multiplier (§6.3; `m = 1` under vanilla or with PowerTransmitterPlus absent, where delivered == drawn).

The allocator therefore models a PT/PR pair's demand on its INPUT network as `throughput * m + quiescent`, not `throughput + quiescent`, and bounds the input cable at `input_cable_cap / m`. It reads `m` from PowerTransmitterPlus by reflection (`PowerTransmitterPlusInterop.SourceDrawMultiplier`, a soft dependency returning 1 when PowerTransmitterPlus is not loaded). Without this, a long link feeding off a constrained source network is under-counted by a factor of `m`: the allocator believes the input is covered, sheds nothing, and vanilla Phase 3 then computes `_powerRatio < 1` on the input network and brown-outs every rigid device on it, the exact no-partial-power violation the design exists to prevent. With it, the source network's true `delivered * m` draw enters the shed cascade, so an unaffordable link sheds cleanly (its input subnet goes dark, not partial) and the input cable is protected against the inflated current.

This is the PowerGridPlus-side half; PowerTransmitterPlus charging the source for distance is by design. The deliverable cap itself (the §8.4 threshold) is unchanged by this section: a pair delivering its full output-side cap with downstream demand unmet is still the trigger. Whether that fires OVERLOAD or SHED is the §8.4 input-limited carve-out (deviation P6): if the deliverable is bound by the input's `PotentialLoad` rather than the link's rating, it SHEDS.

### 8.5 Tiebreak example

Three siblings T_a, T_b, T_c on the same input network, all priority 100. Their `subtreeRigidDemand` is 0.5, 1.0, 2.0 respectively. Input budget allows 2.5.

Sort by (priority ASC, demand DESC, RefId ASC): T_c (2.0), T_b (1.0), T_a (0.5). Shed T_c first (largest demand → biggest single-shot demand reduction). After T_c sheds, remaining claim = 1.5 ≤ 2.5. Done. T_a and T_b survive.

If we'd shed T_a or T_b first we'd still have a residual deficit; the rule converges in fewer sheds.

## 9. The surplus distribution pass

### 9.1 Goal

After the shed cascade decides which subnets stay alive, distribute leftover supply (= total rigid supply − total surviving rigid demand) to soft-demand devices. The distribution is priority-blind: it cares only about topology (where the surplus can flow) and proportional fairness.

### 9.2 Where surplus exists

A network N has SURPLUS = `rigidSupplyByNet[N] − rigidDemandByNet[N]` if the cascade is complete and all siblings on N are satisfied. The surplus is what's available for soft-demand devices on N or downstream of N.

### 9.3 Request flow upstream

Every soft-demand device on network N raises a `RequestedShare` (§7.2). Requests are aggregated:

- Per-network: sum of soft requests directly attached to N.
- Per-downstream-transformer: each non-shed transformer T whose input is N propagates the sum of its output network's requests upstream, scaled if T has a `TransmissionEfficiency` factor (PT/PR pairs only).

The aggregation walks from leaves upward. Each non-shed transformer becomes a propagating node: `T.propagatedRequest = sum of (downstream requests through T.output, scaled by efficiency if PT/PR, capped by T.Setting × efficiency)`.

Shed transformers propagate 0 requests upstream.

### 9.4 Allocation walk downstream

After requests are aggregated, allocate downstream from each supply source:

1. At each network N, compute `availableSurplus`: rigid supply minus rigid demand minus any surplus already allocated to higher-up requests on this same wire (relevant for super-trunks).
2. Sum the requests pointed at N: direct soft devices on N + propagated requests from non-shed downstream transformers.
3. If `sum(requests) ≤ availableSurplus`: each request granted in full. Record allocated shares.
4. If `sum(requests) > availableSurplus`: each request gets `requested_share × (availableSurplus / sum(requests))`. Pure proportional. Priorities are NOT consulted at this step — requests compete on size alone.
5. Each non-shed downstream transformer that received an allocation passes its share down to the next level, where the same proportional split applies among that level's soft devices and further-downstream non-shed transformers.
6. Each allocated transformer respects its own throughput cap (`Setting`, scaled by `TransmissionEfficiency` if PT/PR). If an upstream allocation exceeds the transformer's pass-through capacity, it caps at capacity — the excess simply doesn't flow further, no error.
7. Each cable's `MaxVoltage` is also a cap. Allocation respects wire capacity at every hop.

### 9.5 Storage and enforcement

The final per-device share is written to `SoftDemandShareCache[ReferenceId]`. The `Battery.GetUsedPower`, `NuclearBattery.GetUsedPower`, `AreaPowerControl.GetUsedPower` (charge portion only), and `PowerTransmitter.GetUsedPower` postfixes read from this cache and clamp `__result` accordingly.

During Phase 3's re-`CalculateState`, the postfixes return the allocated value, vanilla `_powerRatio = 1` on every network (because Required ≤ Potential is now an invariant of the distribution), no scaling cascades upward, no `_powerProvided` drift, no flicker.

### 9.6 Why single-pass works

The proportional rule plus headroom cap at every hop means:
- No allocation exceeds physical capacity (wire, transformer, etc.).
- Total allocated ≤ total surplus at every level (by construction of the proportional split).
- Therefore vanilla's `_powerRatio` stays at 1 on every network.

Iterating to redistribute "wasted" surplus would only matter if a fraction got allocated below physical max somewhere AND another branch was starved. The proportional rule already balances this within a single pass; the residual is bounded by the deepest tree level and is not worth iterating for.

## 10. Protection state machine

### 10.0 Tick-rate constants are literal

All lockout-duration constants in the protection state machine are literal integer tick counts (e.g. `currentTick + 120` for a 60-second lock at 2 Hz). The literal `120` is NOT derived from a `TICK_RATE_HZ` variable, and there is no runtime tick-rate query. Assumes the vanilla electricity tick rate of 2 Hz. If a game update changes the electricity tick rate, every literal in §10.1, §10.2, §4.5, §8.3, §8.5, §11.2 must be reviewed and adjusted manually.

### 10.1 Shed lockout

- Triggered by §8.3 cascade.
- Stored in `BrownoutRegistry._lockoutUntilTick[refId] = currentTick + 120` (120 ticks × 0.5 s = 60 seconds).
- `BrownoutRegistry.IsLockedOut(refId, tick)` returns true while `tick < until`.
- Auto-clears when timer expires; next allocator pass re-decides.
- Timer-only. Mid-cooldown topology fixes (player rewires upstream, raises a transformer's Setting, adds a generator) do NOT shorten the timer. The full 60 s runs to completion; the player either waits or uses the OFF-as-reset (§10.3) for an instant manual retry.

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

**Server-side authority: the OFF-as-reset sweep.** The `SwitchOnOffShedPatches` path above is the client-side visual clear; it runs in the rendering path, which a headless dedicated server never executes. The authoritative clear is `OffAsResetSweep`, run every tick in Phase 1 of the atomic tick (§8.0). It walks the devices currently in any of the four lockouts and clears all of a device's lockouts when the player has switched that device OFF.

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

## 11. Visual feedback

Four distinct failure modes (after the producer-fault addition in §11.5). Colour identifies the family of cause; hover text identifies the specific cause and includes a live countdown.

### 11.1 Colour and hover text inventory

| Fault | Colour | Hex | Hover text template |
|---|---|---|---|
| SHED | orange | `#ff8c00` band | `(Shedding: Insufficient upstream supply! {n}s)` |
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

`<ClassName>` in the VVF hover names the offending violator: the device on the producer's network that triggered the producer-isolation walk. Implementation: the Phase 1.5c producer-isolation walk records the violator class name(s) onto the producer's `ProducerFaultRegistry` entry as it marks the producer for VVF. The hover formatter reads it for display. When multiple violators exist on the same network, the registry stores the violator class names in walk-order; the hover formatter renders the first two by walk-order separated by `, `, with a trailing `, ...` if more than two are present. Example renders: `connected to Battery without transformer`, `connected to Battery, AreaPowerControl without transformer`, `connected to Battery, AreaPowerControl, ... without transformer`. The class name is the C# class short name (`Battery`, `AreaPowerControl`, `PowerTransmitter`, etc.), not the localized prefab display name, so the message is unambiguous for cross-language players reading reports.

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

Only ONE fault appears in hover text per tick: the highest-precedence active fault, on a single line with its countdown. Lower-precedence faults that are also active are NOT shown -- no stacking. The flash uses the highest-precedence fault's colour (CYCLE_FAULT red, VARIABLE_VOLTAGE_FAULT red, OVERLOAD red, SHED orange `#ff8c00`).

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
- `ChargeSpeed = 6585`. Same device set. Returns the ACTUAL charge rate this tick (W), post-elastic-allocation. Reads from `SoftDemandShareCache[refId]`. Value is updated live as Phase 2 allocator computes each supplier's share; the field holds the most recent post-Phase-2 value between ticks. Not latched. IC10 reads in practice see the end-of-tick value because IC10 (Phase 5) doesn't run during electricity tick iterations (Phases 1 to 3).
- `DischargeSpeed = 6586`. Same device set. Returns the ACTUAL discharge rate this tick (W), post-elastic-allocation, i.e. the cell's discharge rate. For Battery / RocketPowerUmbilical* (pure elastic suppliers) this reads `SoftSupplyShareCache[refId]`, which already IS the cell rate. For an APC the `SoftSupplyShareCache` entry is the BUNDLED supply (passthrough + grant-through + cell) that feeds the bundled vanilla `GetGeneratedPower` surface, so the APC's `DischargeSpeed` reads the CELL-only share from a separate `ApcCellDischargeCache`, keeping the value consistent across every storage device (deviation P9). Same live-update / non-latched semantics as `ChargeSpeed`; IC10 reads see the end-of-tick value.

All four LogicTypes (6583..6586) appear on Battery, AreaPowerControl, RocketPowerUmbilicalMale, RocketPowerUmbilicalFemale. For APC the "cell" is the inserted `BatteryCell`; the rates apply to its charge / discharge. For RocketUmbilical the cell is the device's internal `PowerMaximum = 10000` store.

The prior PGP repurpose of vanilla `LogicType.ImportQuantity` (29) and `ExportQuantity` (31) on Battery is REMOVED, hard. The mod has not yet been deployed, so no backward-compatibility shim is required. Vanilla meaning reverts on Battery (which is nothing meaningful, since Battery has no import / export slots).

`Setting` keeps its vanilla read/write semantics for IC10 access (clamped `[0, OutputMaximum]`). PowerGridPlus initialises `Setting = OutputMaximum` at world load. The in-world knob and Labeller tool redirect writes to `Priority`. See §5.3.

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

Soft-demand shares are derived; they are not synchronised explicitly. Each peer computes them locally from the synced `Required`/`Potential` and the shed state.

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
| `Setting` | double (synced) | 0 | Knob value (PGP repurposes as Priority) |
| `_powerProvided` | float | 0 | Per-tick downstream draw accumulator |
| `UsedPower` | float (inherited) | 10 | Own quiescent draw |

**`BatteryCell`** (line 321224, portable cells inserted into APCs / chargers):

| Field | Type | Default | Purpose |
|---|---|---|---|
| `PowerMaximum` | float | 36000 (per-prefab override) | Max stored |
| `PowerStored` | float (clamped property) | 0 | Current stored |
| (no rate fields) | -- | -- | Rate imposed externally by whatever charges/discharges |

Stationeers does NOT have separate C# classes for StationaryBatteryLarge / NuclearBattery — all stationary batteries share the `Battery` class with prefab-overridden `PowerMaximum`. PowerGridPlus's per-prefab rate caps in `StationaryBatteryPatches.GetChargeCap` switch on `PrefabName` to apply the right cap.

### 14.2 PowerGridPlus settings

Verified against `Mods/PowerGridPlus/PowerGridPlus/Settings.cs`. All are server-authoritative (Section starts with `Server -`).

**Cable Simulation:**

| Setting | Default | Order | Purpose |
|---|---|---|---|
| `CableNormalMaxWatts` | 5000 | 20 | NEW. Normal cable Watts cap. `0` = unlimited. |
| `CableHeavyMaxWatts` | 50000 | 30 | NEW. Heavy cable Watts cap. `0` = unlimited. |
| `CableSuperHeavyMaxWatts` | 0 | 40 | NEW. Super-heavy cable cap. Default 0 (unlimited) mirrors historical behaviour. |

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

**Power transmitter:**

| Setting | Default | Purpose |
|---|---|---|
| `EnablePowerTransmitterLogicPassthrough` | true | Master toggle for PT/PR logic passthrough |

**Emergency lights:**

| Setting | Default | Purpose |
|---|---|---|
| `EnableEmergencyLights` | true | Replace lights' fail material with emergency override on power loss |

### 14.3 Removed (with dead code)

- `EnableVoltageTiers` (now always-on).
- `EnableUnlimitedSuperHeavyCables` (subsumed by `CableSuperHeavyMaxWatts = 0`).

### 14.4 Settings rename rules

BepInEx keys `(Section, Key)` identify entries. Renaming either orphans the saved value (BepInEx resets to default on next load). The cable max settings and `ApcBatteryDischargeRate` are NEW (no rename). If any existing setting's Section or Key changes in this refactor, the About.xml `<ChangeLog>` carries a player-facing note per project CLAUDE.md guidance.

### 14.5 APC field handling at runtime

PowerGridPlus rewrites `AreaPowerControl.BatteryChargeRate` directly at mod load (and re-applies on each settings change) so that any vanilla code path or third-party mod reading the field sees the configured value. The discharge cap has no parallel vanilla field; PowerGridPlus maintains a static `Dictionary<long, float>` keyed by APC `ReferenceId`, defaulting to `Settings.ApcBatteryDischargeRate.Value`. The dictionary is queried by the elastic supply allocator (§7.3); it does not need to back any vanilla code path because no vanilla code reads a discharge cap on APC.

## 15. Compatibility

- Re-Volt (`ReVolt.dll`): hard-refused at load by `Plugin.TryFindIncompatibleMod`. The two mods rewrite the same vanilla power-tick path.
- MoreCables: same.
- MorePowerMod (Nuclear Battery): supported via runtime type detection. Nuclear battery is treated as a soft-demand device with the dedicated rate cap setting.
- BatteryLight, HaulerMod, ScriptedScreens, KeypadModFix: patch per-device `IPowered.OnPowerTick`, unaffected by our `ElectricityTick` takeover (their patches fire in Phase 4 of the atomic flow).

### 15.1 Stationpedia integration: footers built lazily, no cache

PowerGridPlus injects mod-specific footer text into the in-game Stationpedia entries for transformers, APCs, batteries, and super-heavy cable so players see PGP-relevant rate caps and behaviour notes without leaving the entry. The footers (`BuildTransformerFooter`, `BuildApcFooter`, `BuildBatteryFooter`, `BuildSuperHeavyCableFooter`, and any sibling builders) are built lazily on every `Localization.GetThingDescription` postfix call, with live `Settings.*.Value` reads. No cache, no warming, no lifecycle hook.

Rationale: settings cannot be changed mid-game (the StationeersLaunchPad settings GUI is main-menu only, §17.42). During a game session every `Settings.*.Value` read returns a frozen value, so "lazy + live read" and "cache once at load" produce identical text. Lazy is simpler: no startup hook to register, no cache invalidation to reason about, no surprise stale text if a future settings-change event bus is added without updating the cache. The per-call cost is a small string-build that runs only when the player actually opens a Stationpedia entry; the Stationpedia UI does not poll.

## 16. Performance characteristics

Per tick on a Lunar save with ~200 cable networks, ~120 transformers, ~30 soft-demand devices:

- Phase 1 (observe): 1× device-walk per network. ≈ 400 device-method calls.
- Phase 2 (decide): O(N log N) sort of transformers + O(N) cascade + O(N) surplus walk where N = transformer count. ≈ 1000 method calls, ≈ 100 µs.
- Phase 3 (enforce): another 1× device walk per network. ≈ 400 calls.
- Phase 4 + 5: vanilla copy, no overhead.

Aggregate cost is well under 1 ms per tick at 2 Hz — rounding error against the broader simulation.

## 17. Invariants the implementation must preserve

1. Tick atomicity: every decision at tick N takes effect at tick N. No latency. No oscillation.
2. Priority comparisons are local to a single input network.
3. Shed cascade is bottom-up; the smallest possible subnet sheds first.
4. Surplus distribution is priority-blind and pure-proportional within each hop's headroom.
5. Soft demand never causes rigid devices to flicker; soft devices scale with available headroom but never below 0.
6. Voltage tiers are enforced regardless of any toggle.
7. Cycles are faulted regardless of any toggle. PowerGridPlus's Phase 1.5b detection covers cycles through `ElectricalInputOutput` device chains AND cycles through PT/PR wireless links; both produce CYCLE_FAULT, not a cable burn. Vanilla's recursive-provider detector runs as belt-and-braces (§17.25).
8. OFF clears the lockout. ON re-evaluates.
9. Shed transformer's downstream is dark for 60 s regardless of mid-window surplus availability.
10. PT/PR pairs behave identically to wired transformers at the allocator layer; their per-device methods continue to run under PowerTransmitterPlus's distance-loss math without coordination.
11. Stationary batteries are dual-terminal `ElectricalInputOutput` devices. Charge demand fires on the Input network; discharge supply fires on the Output network. Both flow simultaneously per tick on distinct networks with no interlock; net cell delta = received_on_input - delivered_on_output.
12. APCs are dual-terminal but their internal `_powerProvided` couples both sides per tick; charge and discharge of the inserted cell are in-tick exclusive.
13. A battery or APC with `InputNetwork == OutputNetwork` is short-circuited via vanilla `ElectricalInputOutput.IsOperable`. All four power methods return 0 / no-op. Hover shows "Device Short Circuited". PowerGridPlus relies on the vanilla gate and does not implement parallel detection.
14. A single transformer can never burn a cable by overdraw: its `effective_cap` is bounded by `min(OutputMaximum, InputCable.MaxVoltage, OutputCable.MaxVoltage)`. Cable burning from overdraw requires MULTIPLE transformers feeding one network past the cable cap collectively (standard vanilla cable burn).
15. Cable `MaxVoltage` per tier is server-authoritative via settings; `0` means unlimited and is normalised internally to `float.MaxValue`. Mod-load patches the per-prefab `MaxVoltage` so vanilla cable-burn and PowerGridPlus headroom formula read the same value.
16. PT/PR effective throughput is read at runtime via `PT.GetGeneratedPower(WirelessOutputNetwork)`, so whichever distance-loss model is active (vanilla `PowerLossOverDistance` curve or PowerTransmitterPlus's `MicrowaveEfficiency` formula) is automatically reflected. PowerGridPlus does not hardcode either model.
17. All power simulation values are Watts. There are no amps or volts in the simulation; the misleadingly named `Cable.MaxVoltage` is a Watts cap compared directly against accumulated Watts in `PowerTick.GetBreakableCables`.
18. Every segmenting device (Transformer, Battery, AreaPowerControl, PowerTransmitter, PowerReceiver, RocketPowerUmbilicalFemale, RocketPowerUmbilicalMale) is a level boundary in the §8.0 cascade. The list is exhaustive against the 0.2.6228.27061 decompile. PowerConnection is excluded per §5.0.1.
19. Transformer operational state is binary per tick: ON (working) or LOCKED-OUT (shed XOR overload). There is no "throttled" intermediate state. A transformer either gets its full required input or sheds; a transformer either delivers within `OutputMaximum` or overloads.
20. Shed and overload converge to a joint fixed point inside Phase 2 of one tick (§8.0). External observers never see the iteration; one tick produces one joint result.
21. Cycles burn only when the loop is powered (network `_actual > 0`); unpowered cycles persist silently until current flows (§4.4).
22. The cable-burn check is retained ONLY for direct generator overflow. Transformer or battery contributions causing a cable overflow trip the upstream segmenting devices into OVERLOAD instead of burning the cable (§5.7).
23. Battery discharge is elastically capped at `min(DischargeRateCap, PowerStored)` per battery (§7.3); proportional split is computed against effective caps, not the static rate cap. A high-cap battery is never throttled by a low-stored sibling.
24. A battery's discharge to its OUTPUT network is independent of any state on its INPUT network. The "kept-alive by local battery" failsafe is automatic from the dual-terminal model (§7.3.1).
25. Cycles are resolved by CYCLE_FAULT (§4.5), not by burning cables. PowerGridPlus's Phase 1.5b runs its OWN DFS over the segmenting-device graph (§4.2.5); it does NOT consume vanilla's `BreakableCables` / `BreakableFuses` lists as a cycle signal. PGP's DFS catches the gaps vanilla misses: wireless cycles (PT/PR are first-class graph nodes), multi-hop loops with no provider anchor, battery-only and APC-only cycles where every network's `Required` is 0, self-sustaining mutual-feed loops. Every segmenting device on a detected cycle enters CYCLE_FAULT for 60 seconds and the loop breaks immediately because every device in it contributes 0. Vanilla `PowerTick.CheckForRecursiveProviders` runs unsuppressed as belt-and-braces; in normal operation it never fires because PGP's DFS has already broken every cycle by faulting its members. If vanilla's destructive cable burn ever does fire, that signals a bug in PGP's DFS and vanilla acts as the safety net.
26. CYCLE_FAULT is the third lockout state (parallel to SHED and OVERLOAD). Visuals share the red flash with OVERLOAD; hover text differs.
27. Producer-isolation invariant (strict-literal): producers must only connect to other producers and `Transformer` instances (§8.5). Battery, AreaPowerControl, PowerTransmitter, PowerReceiver, RocketPowerUmbilicalMale, and RocketPowerUmbilicalFemale do NOT satisfy producer-isolation despite being segmenters. Violations trigger VARIABLE_VOLTAGE_FAULT on flash-capable producers (red flash + countdown hover) or the hover-only path on Solar / Wind / RTG (GetPassiveTooltip postfix appends the fault line). The producer-isolation walk treats every `Transformer` instance as a Transformer regardless of its fault state; a producer wired to a CYCLE_FAULTed transformer does NOT get VVF (§8.5). A producer-only network (producers + no other devices) is silent: no VVF, no warning. The vanilla "irreducible brownout" case no longer exists in valid configurations.
28. Phase 2 fixed-point iteration uses ONLY integer sort keys `(depth ASC, priority DESC, ReferenceId ASC)` for MP determinism. Floats never enter sort decisions. Optional magnitude tiebreaker uses `(int)Math.Floor(demand)` quantisation to integer Watts (§8.0.1).
29. Fault hover text includes a live countdown `{n}s` recomputed on each `Thing.GetContextualName` poll. The four templates live in §11.1 as the single source of truth.
30. Flash visuals attach to every segmenting device with an `InteractableType.OnOff` interactable AND every flash-capable producer (per the §11.4 table). Non-flash producers (SolarPanel, WindTurbineGenerator, RTG) use the hover-only path: a universal-base `Thing.GetPassiveTooltip` postfix appends the fault line + countdown to their tooltip. The cable-burn fallback (§11.6) is reserved for future producer classes that have neither flash nor a useful hover surface.
31. `PowerConnection` is vestigial dead code (§5.0.1). PowerGridPlus ignores it entirely: not segmenting, not a Transformer for producer-isolation, not fault-eligible, not in cycle detection. Vanilla itself never consults it (`IsPowerProvider = false`, no `is PowerConnection` checks).
32. Burned-cable per-instance reasons survive save/load via `BurnReasonSideCar` (§11.6). The side-car ZIP entry pattern is mod-removal safe: uninstalling PowerGridPlus leaves `world.xml` untouched and the orphan side-car entry is dropped on the next vanilla save.
33. SoftSupplyShareCache propagates the §7.3 elastic discharge cap into Phase 3 via Harmony postfixes on `Battery.GetGeneratedPower`, `AreaPowerControl.GetGeneratedPower`, `RocketPowerUmbilicalMale.GetGeneratedPower`, `RocketPowerUmbilicalFemale.GetGeneratedPower`. Without these postfixes the allocator's elastic decisions do not reach vanilla `_powerRatio` math, and rigid devices on undersupplied output nets partial-power. The postfixes are not optional.
34. Every segmenting device class (per §5.0) must enter the allocator's contributor list. Today only `Transformer` is in the list. POWERTODO Phase 5 / 6 expands this. Until expanded, any topology with an undersupplied non-Transformer segmenting device feeding a rigid load is a partial-power hole.
35. Vanilla `_powerRatio = Potential / Required` partial scaling is INTENTIONALLY RETAINED as a safety net. The spec's claim is that this branch becomes unreachable in correctly-classified configurations; if a future mod or game update introduces an unclassified device class, vanilla scaling gracefully degrades rather than corrupting state. Per-user directive: phantom power or corruption is worse than partial-power.
36. `Setting` keeps its vanilla read / write semantics for IC10 access (§5.3). PowerGridPlus does NOT redirect `Setting` writes from IC10. Only the in-world knob and the Labeller tool redirect writes to `Priority`. PowerGridPlus initialises `Setting = OutputMaximum` ONLY on new transformer construction; saved Setting values are preserved across save / load so IC10-throttled transformers retain their throttle.
37. All throughput formulas (§5.5, §5.6, §8.0, §8.4, §9) use `Setting` for the active throughput cap. `OutputMaximum` appears only as the prefab rating and the upper bound of Setting. An IC10 script that writes `Setting < OutputMaximum` reduces the transformer's effective_cap and can trigger SHED or OVERLOAD at the lower threshold.
38. The prior PGP repurpose of vanilla `LogicType.ImportQuantity` (29) and `ExportQuantity` (31) on Battery is REMOVED. Battery now exposes `MaxChargeSpeed` (6583), `MaxDischargeSpeed` (6584), `ChargeSpeed` (6585, actual this tick), `DischargeSpeed` (6586, actual this tick) instead. Breaking change for existing IC10 scripts that read ImportQuantity / ExportQuantity on Battery; documented in `<ChangeLog>`.
39. Skip-while-faulted optimisation (refined). A device in CYCLE_FAULT, VARIABLE_VOLTAGE_FAULT, OVERLOAD, or SHED is non-conducting for the entire 60 s lockout window (§10.2.1). The skip-while-faulted shortcut applies ONLY to the question "is the original loop still here?": when the next allocator pass asks whether to re-fire the same fault on the same participant, the participant's faulted-ness is enough to say "yes, this device is still locked out, do not re-validate the topology around it." For NEW cycle detection (Phase 1.5b's DFS) the walk treats faulted segmenters as if they were conducting: their input and output edges remain in the graph and a new cycle that runs through a faulted device is still detected. If a new cycle is found, only the previously non-faulted participants are newly registered for CYCLE_FAULT with a fresh 60 s timer; existing fault timers on devices already locked out are NOT extended, reset, or shortened.

    Worked example. T1, T2, T3 form a cycle and all three enter CYCLE_FAULT at t=0 with `_lockoutUntilTick = currentTick + 120`. At t=10 s the player rewires: the original T1-T2-T3 loop is broken AND a new T1-T4-T5 loop is formed (T1 sits at the corner of the rewire). Phase 1.5b's DFS walks the new graph; because the walk treats faulted T1 as conducting, the new T1-T4-T5 cycle is detected. T4 and T5 are newly registered with full 60 s timers (50 s of which remain "behind" T1's clock). T1 keeps its existing timer (50 s remaining). At t=60 s T1's original timer expires; at t=70 s T4's and T5's timers expire; on the next allocator pass after each expiry, the cycle is re-checked. As long as T1-T4-T5 is still closed, all three re-fault. The original T1-T2-T3 loop is gone, so T2 and T3 stay clear when their timers expire at t=60 s.

    Implementation summary across the four fault paths:
    - Cycle detection DFS (Phase 1.5b): faulted segmenters are walked through, not skipped. New cycles among any combination of faulted and non-faulted devices are detected; only the not-yet-faulted participants get new registry entries.
    - Producer-isolation (VARIABLE_VOLTAGE_FAULT, Phase 1.5c): producers already in VVF skip the topology re-check on the current tick. The lockout is timer-only, and the producer's contribution is already 0 via `GetGeneratedPower` postfix. After the timer expires, the post-timeout allocator pass re-walks the network; if the violation persists, VVF re-fires with a fresh 60 s.
    - Shed / overload allocator math (Phase 2): faulted devices contribute 0 supply and 0 cap, so they fall out of allocation naturally; no explicit short-circuit is needed. The Phase 2 iteration is free to walk through them.
40. Fault-state transience across save / load (§10.5). All four fault registries (`BrownoutRegistry`, `OverloadRegistry`, `CycleFaultRegistry`, `ProducerFaultRegistry`) clear on world load and recompute on the first post-load tick. The `_lockoutUntilTick` countdowns are in-memory only; never serialised. The first post-load tick applies the full 60 s lockout to any rediscovered violation with no grace period (§10.5). `BurnReasonSideCar` is the sole exception and records durable per-instance hover state on already-broken cable wreckage, not transient fault timers.
41. Phase 1.5b topology-change skip. PGP tracks `lastTopologyChangeTick` per `CableNetwork`. The Phase 1.5b cycle-detection DFS is skipped on a network whose `lastTopologyChangeTick < lastCheckedTick` (no structural change since the previous walk). Topology-change events that bump the counter: cable place / destroy / burn (Phase 1.5a wrong-tier burns count), segmenter place / destroy. The skip applies per network; a single network's change does not invalidate the cached result on disjoint networks. On a stable grid (the common case), Phase 1.5b is a near-zero-cost no-op every tick.
42. Settings are immutable during a game session. The StationeersLaunchPad in-game mod settings GUI is main-menu only; the game does not expose a runtime "open mod settings" panel. Every `Settings.*.Value` read during play returns the same value, so PGP code can treat settings as frozen for the duration of the session. This justifies lazy / no-cache patterns (Stationpedia footers in §15.1) and removes the need for a settings-change event bus.

## 18. Test plan summary

Per major behaviour, a probe scenario must exist:

- Atomic tick: AP probe (verifies architectural wiring).
- Overload: OP probe.
- Shed cascade with bottom-up propagation: new probe.
- Per-input-network priority comparison: new probe.
- Surplus distribution proportional: new probe.
- PT/PR as transformer: new probe.
- Soft-demand cap (no `_powerProvided` runaway): new probe with 60-tick state evolution.
- OFF-as-reset: new probe.
- Multiplayer transition broadcast: existing MP probe extended.
- Visual flash: existing FP probe.
- Hover text: existing OP P7 covers overload; add shed equivalent.
- Knob: existing KBP probe.
- Labeller: existing LP probe.
- Save/load: existing SLP probe.

All probes run via the `pgp-atomic-all` aggregator. Pass/fail counts logged per-probe; final tally compared against a baseline of 100% pass.

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
| 14 | PowerActual | 26 | vanilla on Battery/APC/WirelessPower; PGP-added on Transformer | R | Battery, APC, WirelessPower, Transformer (PGP) | `OutputNetwork.CurrentLoad` (or `_powerProvided` on Transformer per PGP) |
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
| 35 | Overloaded | 6580 | PGP | R | Transformer, PT | 1 = OVERLOAD lockout active (downstream demand exceeds OutputMaximum) |
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

19. **LogicType.Setting** (12, vanilla, read+write) - vanilla `_outputSetting` in `[0, OutputMaximum]`. **PGP override**: when `ShedSettingsSync.Effective`, reads return `OutputMaximum` and writes redirect to `Priority`. `TransformerPriorityLogicPatches.cs`.
20. **LogicType.Maximum** (23, vanilla, read-only) - `OutputMaximum`. `Transformer.GetLogicValue:403532`.
21. **LogicType.Ratio** (24, vanilla, read-only) - vanilla `Setting / OutputMaximum`. **PGP override**: returns 1.0 when shedding effective (Setting is hardcoded at OutputMaximum). `TransformerPriorityLogicPatches.cs`.
22. **LogicType.PowerPotential** (25, vanilla, read-only) - `InputNetwork.PotentialLoad`. Inherited.
23. **LogicType.PowerActual** (26, **PGP-added** read slot, read-only) - vanilla does NOT expose this on Transformer; PGP returns `_powerProvided` (downstream consumption accumulator). `TransformerLogicPatches.cs`. Gated by `EnableTransformerLogicAdditions`.
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
54. **LogicType.PowerActual** (26, vanilla, read-only) - `OutputNetwork.CurrentLoad`.
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
- **Overloaded** (6580, PGP, read-only): downstream demand exceeds OutputMaximum. Reset by 60s timer or OFF-toggle.
- **CycleFault** (6581, PGP, read-only): device is part of a closed loop. Reset by 60s timer or OFF-toggle. NEW.

All three exposed on Transformer and (for the PT/PR pair) on PowerTransmitter. Both Shedding and Overloaded and CycleFault read independently; multiple can be 1 simultaneously.
