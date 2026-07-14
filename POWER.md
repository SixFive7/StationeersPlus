# Power Grid Plus: Power System Specification

Authoritative spec for the post-refactor power simulation. Reads as a reference, not a tutorial. Defines invariants, algorithms, and behaviour rules in enough detail to implement against without consulting the conversation that produced it.

## 0. Resolved decisions (dated override blocks, newest first; these override any contradicting text below)

Decision numbers are global across the dated blocks (source comments cite them as "decision N" / "§0.N"), so a newer block continues the numbering of the older one instead of restarting at 1.

### Resolved decisions (2026-07-14): mixed-tier prevention, self-healing, and the load-time wreckage sweep

32. **Load-time wreckage sweep: burnt-cable wreckage stacked with live wiring is removed once per world load.** Burnt-cable wreckage (`CableRuptured : SmallGrid`, the *Burnt prefabs `Break()` leaves) sharing a small-grid cell with a LIVE cable of any tier or with OTHER wreckage is a corruption artifact, not burn feedback: pre-guard blueprint prints stamped cables into occupied cells and the pre-decision-31 enforcement `Break()`ed stacked cells, leaving wreckage on top of live wiring. One sweep per world LOAD (armed in FaultRegistryLoadPatches beside the other one-shots, consumed at the tick head, marshalled to the main thread; host-only, destroys replicate) removes every wreckage piece in such a cell and prints a plain-text console line per removal (capped at six lines plus a remainder line against spam); a LONE wreckage piece in its own cell stays, that is the game's normal "a cable burned here" cue. Wreckage is not a `Cable`, occupies no cable slot, and is invisible to the network model, so removal can never change electrical state. Matching keys on the anchor position in rounded decimeters (the same multi-cell anchor limitation as the stack repair). Verified on the Luna_mixedwire copy 2026-07-14: the two forensic wreckage-over-live cells removed with console lines at their exact coordinates, 31 lone pieces preserved, post-run autosave audits zero co-located wreckage. (implemented 2026-07-14) Extension: the same pass also collapses same-tier duplicate LIVE cables sharing one cell (hidden blueprint-print twins; the meshes overlap exactly, vanilla occupancy makes the shape unbuildable by hand, and a vanilla same-tier replace never persists both to disk): the grid-seated cable is kept (lowest ReferenceId when no member is seated), the ghosts are destroyed cleanly with a console line each under the same cap, and different-tier stacks stay the tier enforcer's job per decision 31. Closes the user-named gap where a hidden twin survived every load, invisible to the player, and resurrected through the orphan re-seat repair when its visible twin was later deconstructed or burned; with this, the idle-mixed activity gate (burn only when power touches it) is confirmed as the standing posture. (implemented 2026-07-15)

31. **Mixed-tier wiring gets prevention at registration and stack-aware self-healing (phase 2 of the mixed-wire plan).** Three pieces. (a) Registration guard (WiringGuardPatches): placement paths that never see the build cursor (blueprint printers, creative spawns, mod calls to OnServer.Create) are refused at registration time. A SmallCell.Add prefix captures a different-tier cell theft while the victim is still reachable (SmallCell holds ONE cable slot and Add is a plain overwrite), and a Cable.OnRegistered postfix refuses a newcomer whose merge made the network mixed-tier: stolen cells are re-seated to their victims first, the refused cable is refunded as its construction kit (BuildStates[0].Tool ToolEntry x EntryQuantity) at the spot, and a plain-text line goes to the player-visible console. Load and join are exempt (corrupt saves must load; healing is (c)'s job); same-tier overwrites stay untouched (vanilla merge-replace flows). (b) Detection gates split per kind: the Mixed kind is gated on ACTIVITY (any of the snapshot's RigidDemand / RequiredSum / PotentialSum positive) instead of the old min(Potential, Required) flow gate that deadlocked the 2026-07-13 incident (the pair's lockout re-zeroed published demand every cycle); device kinds keep the flow gate. The pre-burn main-thread re-detect checks only that the violation still exists and does NOT re-apply the activity gate (published fields read zero on device-less trunk fragments and silently stalled the heal cascade). The tier cache is event-evicted on every CableNetwork constructor/Add/Remove (the cable-count compare stays as a belt), and GetTierInfo also reports StackedTheft (a member cable whose cell seats a different-tier cable). (c) Stack-aware repair in ResolveMixedTierNetwork: stacked pairs are detected from the victim's surviving SmallCell back-pointer and repaired by seating the higher-tier cable FIRST and destroying the lower-tier one cleanly (no rupture wreckage; Break() on a stacked cell would null the slot and orphan the survivor into an untargetable ghost, the corruption the Luna_mixedwire forensics found persisted); only then does the boundary burn run. Every wrong-tier removal or burn prints "Illegal mixed-tier cable at (x, y, z): ..." to the console and log in world-meter coordinates (plain text: the console is ImGui and drops color-tagged lines on a dedicated server). Verified by the pgp-mixedwire-fixture (10 checks green 2026-07-14: tier refusal with kit, theft re-seat, cache coherence, gate matrix, stack repair, boundary burn) and by the Luna_mixedwire heal run repairing the incident's stacked cells at their forensic coordinates. Known limitation: multi-cell straight variants carry only their last cell's back-pointer, so mid-span overlaps of long pieces can escape stack detection. (implemented 2026-07-14)

### Resolved decisions (2026-07-14): the fault naming lock and the overload fault split

30. **Fault naming lock: five player-facing fault names, renamed IC10 values, and one merged hover template, final.** (a) Names. The five lockout faults are Deprioritized (the §8.3 upstream-undersupply lockout), Device overloaded (the §8.4 capacity kind; the title substitutes the hovered device's label, `Transformer / Link / Battery / APC / Umbilical overloaded fault`, retiring the storage `{device clause}` sentences), Cable overloaded (§5.7), Cycle (§4.2.5), and Current mismatch (the §8.5 producer-isolation fault, formerly named for voltage). (b) IC10. The five read-only values rename with numbers unchanged: `DeprioritizedFault` (6579), `DeviceOverloadedFault` (6580), `CycleFault` (6581, name unchanged), `CurrentMismatchFault` (6582), `CableOverloadedFault` (6587); Patterns/Logic catalogue two-step applied per rename. (c) Hover template (§11.1). One merged block on BOTH surfaces (casing tooltip and on/off button title box): line 1 `{On|Off} - {title}: {n}s` (switch word green `#00FF00` on / grey `#9aa0a6` off, title + countdown red `#ff2626`; switchless devices drop the prefix; the two info states, No upstream supply and Throttled, use grey / amber titles and no countdown), then calm-grey diagnostics with the offending value red and the capacity blue `#008AE6`. Exactly one block per hover: info states are suppressed while any fault is active (the throttle note no longer stacks under a fault), and the button's Action box stays the pure vanilla action word. `FaultHover.TryGetMergedBlock` is the single renderer; the legacy per-line renderer and its layout gate are deleted. (d) Deprioritized payload. `DeprioritizedRegistry` records (needsW, upstreamDemandW, upstreamSupplyW) per entry, rides the per-tick snapshots (`KindDeprioritized`) and the join suffix, and renders `Needs 12.0 kW while 30.0 kW competes for 20.0 kW upstream` (needs grey, upstream demand red, upstream supply blue). (e) Scrub. The retired terminology is gone everywhere: zero occurrences of the old fault vocabulary remain across code, fixtures, IC10, Stationpedia, and docs (`DeprioritizedRegistry`, `PowerAllocator.SelectDeprioritizationVictims`, `ForwardSupplyAndDeprioritize`, `Seg.Deprioritized`, `FaultFlashBehaviour`, `SwitchOnOffFaultPatches`, `FaultButtonTooltipPatches`, the `CurrentMismatchFault*` classes, and the `pgp-priority-deprioritization-*` / `pgp-deprioritization-*` scenario ids and Dispatcher partials). (implemented 2026-07-14)

29. **The single OVERLOADED fault splits into two user-named kinds, "Device overloaded" (capacity, titled per device) and "Cable overloaded" (cable overflow), each with live watt diagnostics in the hover.** Motivation: the mixed-wire Luna incident (a BlueprintMod print merged normal cable into a super-heavy mainline; the merged net's weakest tier collapsed the mainline pair's rate limit to 5 kW and the pair sat in an unexplained OVERLOAD with no number anywhere naming the 5 kW wire as the cause). (a) The split. The capacity family keeps `OverloadRegistry` and its read-only IC10 value (`DeviceOverloadedFault` since decision 30): the §8.4 structural hit-max rule (the draw on a network exceeds its suppliers' combined Setting-derived maximum) and the §8.4.1 elastic hit-max analog. The cable family is the §5.7 cable-overflow rule (the suppliers could deliver but the network's weakest cable cannot carry the flow) and moves to the new `CableOverloadRegistry` (parallel API, same 120-tick lockout, OFF-as-reset, hygiene sweep) with a new read-only IC10 value (`CableOverloadedFault`, 6587, assigned via the Patterns/Logic two-step). Precedence becomes CYCLE > CURRENT-MISMATCH > CABLE-OVERLOADED > DEVICE-OVERLOADED > DEPRIORITIZED (§11.5); both kinds keep the red flash. (b) Naming (user decision, re-locked with per-device titles by decision 30): every device titles the capacity fault with its own label (`Transformer / Link / Battery / APC / Umbilical overloaded fault`); the cable kind is `Cable overloaded fault` on every device it trips. (c) Diagnostics. Both overload kinds render the merged hover block (decision 30): the state-plus-title line, then a calm-grey `#9aa0a6` line with the offending value in the fault red `#ff2626`: `Drawing 150.0 kW of 100.0 kW` (capacity: the rigid draw that tripped the rule against the combined deliverable cap) and `Pushing 12.1 kW onto a 5.0 kW wire` (cable: the flow against the weakest-cable cap). Watts format whole below 1000 W, kilowatts with one decimal at or above, locale decimal separator like the countdown. Each registry entry records its (valueW, capW) pair at the detection site (grow-only overload means the first detector owns the entry; the elastic capacity cohort computes one shared pair at the commit), and the pair rides the per-tick fault snapshots and the join suffix (the fifth registry block, §13) so client hovers show the host's numbers. (d) The fault block also appears on a transformer's BODY tooltip while and only while it is faulted; wiring that surfaced a pre-existing multi-fire defect (Harmony detours `base.GetPassiveTooltip` calls exactly like virtual dispatch, so one hover poll ran the fault postfix at every patched level of the AreaPowerControl -> ElectricalInputOutput -> Device chain and the line rendered two or three times), closed with an outermost-only depth guard (prefix increments, finalizer decrements on every exit path). (e) Behavior change: a cable-overflow-tripped battery takes the real visible 60 s lockout (previously it silently produced zero with no cue); a cable-overflow elastic commits per device into `CableOverloadRegistry` and never joins the §8.4.1 network retry, because re-engaging a store cannot raise the cable's rating. (f) Scope: mixed-tier placement prevention and self-healing of illegal wiring are deliberately deferred (phase 2, mod TODO). Where older body text says unqualified OVERLOAD, it means the capacity kind unless it describes the §5.7 cable-overflow rule, which is now CABLE_OVERLOADED. (implemented 2026-07-14)

### Resolved decisions (2026-07-13): Powered coupling revert, IC10 Power override, and the delivery shim

28. **Powered follows the vanilla on/off coupling again, the IC10 `Power` read carries the grid signal instead, and five consumer classes get their `ReceivePower` delivery back.** Three coupled changes. (a) The coupling revert; motivation: the one-storage visual finding. Vanilla stores the Powered flag, the animator state, and the IC10 value in ONE place (the animator integer IS the storage), and `InfoScreenComponent.RefreshState` plus several panel gates key on Powered alone, so under the decision-19/27 decoupling a switched-off device kept a lit info screen, a green Powered-keyed button glow, and lit third-party Powered-keyed visuals (force fields); re-keying every visual to Powered AND OnOff was rejected (the fabricator button glow is animator-asset-keyed and not cleanly patchable). The ownership sweep's expectation becomes verdict-LIVE AND structure complete AND the snapshot row's OnOff, so every Powered-keyed visual is vanilla-correct by construction; the sweep remains the single Powered writer (vanilla `AssessPower` stays suppressed) and the OnOff term is the snapshot capture, never a live re-read, so a toggle edge lands when the NEXT tick's snapshot carries the new OnOff, up to half a second later. That latency is the deliberate race-free single-writer trade (decision 25): letting the vanilla toggle write through, or adding a live-OnOff freshness gate to the sweep, would be a second boundary read acting mid-tick. This amends decision 19's "powered but off" semantics and decision 27's "Powered is always decoupled" clause; the setting stays removed, the hardcoded behavior is now the vanilla coupling. (b) The IC10 `Power` override; motivation: scripts relied on the decoupled read's "my network is energized" meaning. A postfix on the base-declared `Device.GetLogicValue` (`Patches/PowerLogicReadPatches.cs`) serves `LogicType.Power` from the net-liveness verdict (1 iff the device's power cable network is verdict-LIVE) on exactly the plain devices the ownership sweep owns, so scripts keep reading the values they saw before this change; the non-simulating side, cable-less devices, incomplete structures, no-power-state devices, segmenters, PoweredOwnership-exempt classes, emergency-light prefabs, and quarantined devices keep the vanilla `Powered ? 1 : 0`. Read `On` for the switch, `Power` for the grid. A new mod-specific LogicType serving the verdict was the runner-up; overriding the existing name keeps existing scripts working unchanged. (c) The plain-consumer delivery shim; motivation: the five-class `ReceivePower` gap. The write-back retired vanilla ConsumePower, and a whole-decompile census found exactly five vanilla classes whose gameplay lives ONLY inside `ReceivePower(CableNetwork, float)`, all silently dead since the rebuild while the grid kept billing them: PowerTransmitterOmni (wireless-battery charge forwarding with distance falloff), SuitStorage (recharges the stored suit's batteries), BatteryCellCharger (charges the slotted cells), Bench (feeds its powered appliances), and WallLightBattery (recharges its cell from grid surplus and writes the grid-fed latch `WasPoweredByCableLastTick` the emergency-light feature reads). A WRITE-BACK step between storage settlement and the accumulator drains calls `device.ReceivePower(net, granted demand)` on verdict-LIVE nets only, values from the snapshot (grant equals demand by the all-or-nothing construction; a DEAD net delivers nothing, matching vanilla's empty-providers behavior), allow-listed by `DeliveryEffectClassifier` (`is`-type checks so subclasses inherit membership) plus the new `Server - Compatibility` / `Extra Delivery Devices` PrefabName list (default empty), each call under a per-device try/catch with a 600-tick warning throttle. A load-time census (`ReceivePowerOverrideCensus`) logs every third-party `ReceivePower` override as an enrollment candidate. Deliberately excluded: the fabricator family (its `ReceivePower` zeroes the main-thread-owned `_powerUsedDuringTick`; the decision-26 debit queue owns that field), and stores / segmenters (the write-back owns their ledgers). Calling the vanilla method beats reimplementing the five effects: version fidelity, virtual subclass dispatch, one surface instead of five clones. See §2.1 for the full vanilla-touchpoint ledger. (implemented 2026-07-13)

### Resolved decisions (2026-07-13): settings rework

27. **Settings rework: the remaining behavior masters are removed, passthrough defaults become per-kind settings, and `Battery Charge Efficiency` becomes a charge cost.** Removed outright, behavior unconditional: `Enable Battery Limits`, `Enable Transformer Deprioritization`, `Enable Transformer Overload Protection`, `Enable Rocket Umbilical Limits`, `Enable Conservation Check`, `Decouple Powered From On Off` (Powered is always decoupled from the device's own on/off switch; this amends decision 19's config sentence and decision 23's "only Decouple remains" note, and retires the `Server - Powered Presentation` and `Server - Diagnostics` groups), and the five per-family `Enable * Logic Passthrough` masters (passthrough support is always on; a device's own `LogicPassthroughMode` is the only runtime gate). Added: six per-kind `Passthrough Default` settings (Battery / Small Transformer / Other Transformer / APC / Power Transmitter / Umbilical; default true except Other Transformer false), each the mode a device starts with while its `LogicPassthroughMode` was never explicitly set (newly built, or an existing save running the mod for the first time); a stored per-device mode wins once set, and the APC (no logic port) is config-seeded only. Changed: `Battery Charge Efficiency` (default 1.5) is a cost multiplier, grid energy drawn per unit stored (`stored = delivered / value`; values below 1.0 treated as 1.0; post-loss trickles below 500 W stored in full); the loss is destroyed for now, a heat conversion is planned. Wire change: the join suffix carries the six passthrough defaults (`PassthroughDefaultsSync`) and no longer carries deprioritization / overload toggles; `PassthroughSettingsSync` / `ShedSettingsSync` / `OverloadSettingsSync` are deleted. (implemented 2026-07-13)

### Resolved decisions (2026-07-12): full-strict isolation, always-on hardcoding, the B + D1 redesign, and the race-free mandate

22. **Producer isolation is code-strict.** The §8.5 strict-literal rule is now implemented literally (POWER_DEVIATIONS.md P17): the violator set is presence-based (any device on a producer's net that is neither a producer nor a `Transformer`, idle consumers and every non-Transformer segmenter included), and a `Transformer` on the net is allowed but exempts nothing. The prior code behavior (transformer presence exempted the whole net; only drawing rigid consumers counted) was an undocumented divergence, not a spec change. (implemented 2026-07-12)
23. **Always-on: Powered ownership, transformer exploit mitigation, APC power fix.** The three master settings are removed; the behaviors are unconditional. This amends decision 19's config sentence: only `Decouple Powered From On Off` remains as a setting in the Powered Presentation group. (implemented 2026-07-12)
24. **Architecture redesign: Option B (own the data plane) + D1 (topology mirror), staged.** Approved shape in `.work/2026-07-12-architecture-redesign/OPTIONS.md`: stage 0 hotfixes (the PowerDeviceList root lock, arm-and-drain world-load clears, tier-cache concurrency), stage 1 the mod-owned topology mirror (event-patch maintained, tick-boundary applied; the solve stops iterating `AllCableNetworks` / `PowerDeviceList`), stage 2 the single boundary read feeding the allocator's table (the latch army demotes to compat shims), stage 3 the mod-owned write-back (vanilla `PowerTick.Initialise` / `CalculateState` / `ApplyState` are no longer called; the mod credits energy, sets Powered, and fills the net load fields; vanilla power methods remain only as compat shims answering from the table), stage 4 the unblocked solver policies (leaf-first deprioritization, table-fed producer isolation, network-analyser surfacing). (implemented 2026-07-12)
25. **Race-free mandate.** Everything the mod builds must be race-free by construction: single-writer thread ownership, phase sequencing, or a synchronization design whose every interleaving is proven to conserve. "A smaller race than vanilla" is not an acceptable end state for any mod-owned write. Pre-existing vanilla-internal races the mod does not participate in are out of scope, except where a mod mechanism closes one in passing at no cost. (implemented 2026-07-12)
26. **The `_powerUsedDuringTick` write-back drain: home-thread debits, the main-thread queue, and the pending-debit ledger; worker-side CAS rejected.** Each accumulator class's drain executes on the thread that owns its accrual writes (the synchronization census in `Research/GameSystems/PowerTickThreading.md`). The main-clock accumulate classes (SimpleFabricatorBase, Fabricator, ArcFurnace, IceCrusher) debit through ONE batched main-thread-marshaled queue per tick (vanilla's own `SetPowerFromThread` idiom), paired with a worker-owned pending-debit ledger: the boundary read serves `max(0, Volatile.Read(field) - pending)` until a volatile applied-sequence number covers the batch, which conserves exactly under every interleaving (worst case defers a partial tick of accrual to the next bill; never loses, never double-bills). Worker-owned and atmosphere-phase-sequenced classes debit synchronously at write-back with no ledger; assert-semantics classes (absolute re-assignment each cycle, Fermenter's `= 200f` / `= 0f` being the archetype) take no debit at all; IceCrusher's atmosphere-phase `+=` (decompile 380203) is redirected into the same queue so the class becomes single-writer, closing in passing a vanilla-internal accrual race between its two writer threads. The field must drain physically rather than be shadow-offset: three classes consume the raw value for physics (DroidSleeper robot charging 181239, SimpleFabricatorBase ignition energy 420313, SpawnPointAtmospherics delivery fold 422744) and a never-reset float exhausts its 24-bit mantissa within hours. A worker-side CAS debit was evaluated and REJECTED as violating decision 25: the main thread's `+=` compiles to a non-atomic load-add-store, and its blind store can land after a successful CAS, resurrecting already-billed joules as a silent double-bill. Post-redesign review item (recorded in OPTIONS.md): reassess whether the queue-plus-ledger bookkeeping earned its keep relative to its complexity once the redesign has settled. (implemented 2026-07-12)

### Resolved decisions (2026-07-09): consumer Powered ownership (net liveness)

19. **The mod owns every device's Powered flag; the verdict is per-net liveness, never per-device demand-met.** Root cause driving it: a fabricator's print-start demand spike (the main-thread `_powerUsedDuringTick` accumulator) let vanilla ApplyState un-power the device for one tick, cancelling the print; the supply solve was proven fully atomic (a six-agent investigation), so the tear is a demand-observation artifact no allocator can outrun. The fix removes demand from the Powered decision entirely. At the ALLOCATE tail the allocator publishes a per-net verdict (`NetLiveness`): LIVE iff `Unmet <= Eps` AND `GenSupply + InflowCommitted + AvailableElastic > Eps` (the same supply expression as the dead-input cue and the healthy-set gate); otherwise DEAD_UNMET (unfundable rigid demand; arms a 60 s hold against demand-collapse flapping) or DEAD_NOSUPPLY (no energized feed; re-arms the tick supply returns). An ENFORCE-tail sweep (`PoweredOwnership`) asserts both Powered edges per plain device from the verdict: LIVE nets present every structure-complete device powered; DEAD nets go dark AS A UNIT. Vanilla's false edge is blocked at the non-virtual `Device.SetPowerFromThread` funnel (catches every class, third-party `AllowSetPower` overrides included); the vanilla ON edge stays (it cannot fire on a DEAD net, see decision 20). Unclassified nets (fresh mid-tick splits) FREEZE (no write) with a 4-tick fail-safe to dark plus a one-shot log: a device the mod cannot classify stays depowered by design. (2026-07-12 note: the freeze/streak machinery is retired by construction under decision 24; the verdict map is computed from the same snapshot the sweep iterates, so classification is total and a verdict miss is a plain no-write fail-safe.) Exemptions keep their own owners: segmenters (the §10.6 healthy-set policy), battery wall lights (the emergency-light feature), the gas / Stirling generator family (vanilla `AllowSetPower => false`, self-owned), landing pads, always-powered doors, the elevator family, and, via a reflection rule, any class overriding the Powered getter below `Thing` (vanilla Battery is the archetype). A device whose actual state keeps contradicting the sweep's stable expectation for 10 consecutive ticks is quarantined back to full vanilla behavior with one log line, and a conformance auditor (PoweredSetConformance posture: exact counters, one aggregated warning per 600 ticks) reports any device found in a state the mod did not expect. Config: `Enable Device Powered Ownership` (master, default on) and `Decouple Powered From On Off` (default on: Powered means "my network is energized", so a switched-off device on a live net reads IC10 `Power` = 1 while drawing nothing; off restores the vanilla coupling). The IC10 semantic change is deliberate and documented in the changelog.
20. **Dead nets deliver literally nothing: the advertise is zeroed at the source.** Powered=false alone does not stop vanilla delivery; a ratio-scaled trickle into a dark subnet keeps resetting `_powerUsedDuringTick` debts at partial price (free power) and bills upstream for undelivered flow. So a DEAD verdict zeroes every supply surface on the net during ENFORCE: plain producers via the ProducerFaultEnforcementPatches postfix (the proven current-mismatch-zeroing mechanism, now also keyed on the net verdict, ENFORCE-phase only so GATHER re-reads real supply and recovery is never deadlocked), and every routed seg via the allocator publish tail itself (a seg whose OutNet is DEAD publishes all-zero `TotalThrough` / `TotalPull` and zero elastic / soft shares, so the cache-governed advertise AND the input bill collapse together; no energy is ever billed for undelivered flow). Zero advertise means an EMPTY `Providers` array at ENFORCE, `ConsumePower` never calls `ReceivePower`, accumulator debts freeze exactly and are paid in full on revival, and the power-ON branch cannot fire (the zero-Potential corollary, Research/GameClasses/PowerTick.md). This closes the three partial-delivery carve-outs the investigation surfaced (generation-short root nets, the §8.3 step-up partial grant, cable-limited segs trickling below demand): all three now classify DEAD_UNMET and go dark whole, superseding §8.0.0.2's reachable-partial-power reading and the §8.3 carve-out's delivered-partial behavior for consumer-bearing nets. The vanilla ratio path remains only as the unclassified-net safety net.
21. **The read-once discipline is now exhaustive: fourteen new tick-scoped latches plus a GATHER control snapshot.** A whole-decompile census (all 34 `GetUsedPower` + 15 `GetGeneratedPower` overrides classified by mutation site and thread) found the per-tick-variable set beyond the three existing latches: `ConsumerDemandLatchesExtended` latches Fabricator (a SIBLING of SimpleFabricatorBase, missed by the original latch), ArcFurnace, IceCrusher, Fermenter, Bench, SuitStorage, WallLightBattery, BatteryCellCharger, AdvancedFurnace, VolumePump, TurboVolumePump (its own override), SatelliteDish, and RocketMiner (first-own-network-read-wins, foreign sentinel paths never latched); `PowerConnectorOutputLatchPatches` closes the one producer gap (the docked portable-generator forwarding path, which has no network guard of its own). The 20 stable classes (accumulators written only in `OnAtmosphericTick` or `OnPowerTick`, both tick-stable under the atomic pipeline) deliberately carry no latch. Control state joins the same discipline: GATHER records every segmenter's (OnOff, Error) first-read-wins into `SegControlSnapshot`, enrollment gates on the snapshot, and every ENFORCE-phase gate in the segmenter patch files (transformer advertise / bill / ledger repayment, wireless bill, battery charge credit, APC quiescent + bill, umbilical quiescent) consults the snapshot instead of live fields, so a player toggle landing mid-tick takes effect next tick, coherently with the grant. Adapter reads at GATHER are by definition the snapshot point and stay as they are.

### Resolved decisions (2026-07-08): elastic-to-soft transfer + the auditor round

17. **Elastic leftover funds soft demand (the lowest funding rung; battery-to-battery transfer).** The former "elastic never funds charging" rule is replaced: after the per-net rigid settlement, each net's remaining eligible elastic discharge capacity (Σ per-elastic `EffDischarge - rigid share`, excluding Locked / Overloaded elastics) joins the net's soft pool BELOW the firm residual and the soft inflow in consumption order, so elastic is tapped only for the soft shortfall firm supply could not cover. One softRatio as before; recipients charge in the existing priority-tier order; the pool stays capped by the weakest-cable headroom (physics-capped, no further constraints); always on, no config flag. Each elastic's published share becomes `rigidShare + softTopUp` (topUp distributed full-or-proportional to leftover, total per elastic <= `EffDischarge`). Rigid keeps absolute priority by construction (the leftover is what rigid did not consume). No self-exclusion, no donor / recipient ordering, no hysteresis: batteries are directional, players place buffers deliberately, and energy loops require directed cable cycles which the PROTECT-phase cycle DFS already locks (§4; battery / APC / umbilical edges participate in that DFS, so a transfer loop is CYCLE_FAULTed before the allocator ever runs on it). Charge-efficiency losses remain plain losses for now (converting them to battery heat is an approved future feature, tracked in the mod TODO). Universal at the Soft class level: every store kind participates as recipient and every elastic surface donates; faults never fire for soft (§9.6) and the fixed-point safety argument is unchanged (the leftover is a pure function of each round's rigid settlement).
18. **The auditor round: six always-on self-checks.** (a) A discharge-delivery audit (the ChargeDeliveryAudit's second direction): argument-derived UsePower drain brackets on battery / umbilical stores compared at the ENFORCE tail against the granted elastic share (`rigidShare + softTopUp`), Served-gated, exact band, APC cell out of scope by design (vanilla deferred ledger settlement). (b) A tick-duration watchdog around the atomic tick + RunAtomic (threshold `min(max(8 * rolling median, 50 ms), 400 ms)`, and the warning gated on the allocator being the dominant cause of the overrun: its span's excess over its own rolling median must explain at least half the amount the tick ran over, so an environmental stall that slows a tick while the allocator ran its normal few ms stays silent; zero per-tick allocation). (c) Generator read-coherence extended by per-class decompile verdicts: the wind turbine gets the solar-style first-read latch (its output recomputes per call from per-frame main-thread wind state); gas / solid / Stirling / RTG / wall-turbine outputs are atmos-tick-written fields, stable within the electricity tick, no latch. (d) A Powered-set conformance assert at the ENFORCE tail (healthy roster members must read Powered == true past a one-tick marshal grace). (e) A registry hygiene sweep every 600 ticks pruning expired and destroyed-device fault entries. (f) A save/load one-shot self-check (fault registries empty, priority sidecar restored == loaded, ledger sweep ran). All follow the ledger-audit posture: exact counters, worst / latest captures where meaningful, one aggregated warning per 600 ticks, zero lines healthy, world-load clear (§8.8).

### Resolved decisions (2026-07-02): the unified-flow rearchitecture

Shipped in commits e9ef3c1f (Stage 1: unified flow classes + complete segment presentation), 2a00d674 (Stage 2b: segment adapter contract + PowerTransmitterPlus ModApi handshake), and 31017f38 (Stage 3: Powered presentation + ledger adoption + diagnostics). The affected body sections (§2, §5.0.2-§5.0.3, §6.3, §6.6, §7.2-§7.6, §8.0.3-§8.0.4, §8.2-§8.4.2, §8.8, §9, §10.6-§10.7, §12, §14.2, §16, §17, §18, §19) are rewritten or extended to match; where any remaining older text contradicts these decisions, the decisions win.

9. **One demand vector, two riding flow classes; the surplus walk is deleted.** Storage charge (battery, APC cell, umbilical cell) is the SOFT class riding the SAME `BackwardDesirePass` / `ForwardSupplyAndDeprioritize` sweep as rigid demand, as a (rigid, soft) demand vector: the same priority-tier-first, proportional-within-a-tier splitter, with each contributor's soft capacity capped at `EffCap - rigid desired throughput`, granted forward out of the firm residual only, quiescent draw carried exactly once (on the rigid pull when any rigid flows, else on the soft pull). The separate single-pass surplus walk, `GrantThrough`, `SoftReqTotal`, and `Net.ElasticDelivered` are deleted (§9).
10. **Faults are rigid-only.** Deprioritization, structural overload, supply overload, cable overflow, and the dead-input cue all evaluate the rigid component only. Unmet soft desire clamps silently: never a deprioritization, never an overload, never a lockout, never a hover cue (§9.6).
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
3. **Failure colour is distinct (resolves the §11 / open-questions contradiction).** DEPRIORITIZED = orange `#ffa500`; OVERLOAD / CYCLE_FAULT / CURRENT_MISMATCH_FAULT = red `#ff2626`. Highest-precedence active fault picks the colour: CYCLE > CURRENT-MISMATCH > CABLE-OVERLOADED > DEVICE-OVERLOADED > DEPRIORITIZED.
4. **Cycle detection is a DIRECTED-SCC walk (replaces §4.2.5's undirected bipartite DFS).** Nodes = cable networks; each segmenter contributes one directed edge InputNetwork -> OutputNetwork; a cycle = a strongly-connected component of size >= 2 (Tarjan). Edges gated on OnOff + both-networks-non-null + Input != Output; wireless PT/PR via the shared `WirelessNetwork` node. The undirected model false-positives on parallel same-direction transformers/batteries (normal redundancy); the directed model does not. Only powered SCCs fault (min(Potential,Required) > 0 on a member network).
5. **Producer-isolation (CURRENT_MISMATCH_FAULT) is ALWAYS-ON, fault+zero, with a cable-burn fallback for unknown producers.** Known producers (the classifier list) fault and stop generating (reversible; button-bearing ones flash red, solar/wind/RTG hover-only); no cable burn for known producers (resolves §1.6.5's internal contradiction). A producer-LIKE device NOT in the known list (new game version / modded) falls back to the original cable-burn handling so it is still caught. No enable/disable toggle.
6. **Fault visuals on every faultable device.** The flash + hover-countdown attach to every segmenter (Transformer, Battery, AreaPowerControl, PowerTransmitter, PowerReceiver, RocketPowerUmbilicalMale; RocketPowerUmbilicalFemale hover-only) and every button-bearing producer; solar/wind/RTG are hover-only.
7. **Emergency lights are a configurable prefab list.** `EnableEmergencyLights` applies to a configurable comma-separated list of light prefab names (default `StructureWallLightBattery`), not a single hardcoded prefab.
8. **One allocator, no toggle.** PowerGridPlus has exactly one power allocator (§8). An earlier development pass carried two DECIDE-phase strategies behind a runtime toggle (`EnableSweepAllocator`); the toggle, the alternate strategy, and the second code path are all removed. The surviving allocator is the topological-order, iterated fixed-point design described in §8. Throughout this spec "the allocator" means that single implementation; there is no longer a "Sweep" / "Legacy" distinction, no `SweepAllocatorSync`, and no allocator-selection setting. The per-feature master toggles `EnableTransformerDeprioritization` and `EnableTransformerOverloadProtection` remain (they enable or disable the deprioritization and overload passes), but they do not select between allocators.

## 1. Architectural overview

PowerGridPlus owns `ElectricityManager.ElectricityTick` via a Harmony prefix-return-false and owns the whole power data plane (decision §0.24, the B + D1 redesign). Vanilla's per-network `PowerTick` trio (`Initialise` / `CalculateState` / `ApplyState`) is NEVER called. The mod replaces vanilla's per-network iterate-then-flow with an atomic multi-phase pipeline that:

- Builds one per-tick grid snapshot: topology plus a single boundary read of every device's demand, output, and control state (SNAPSHOT).
- Detects structural faults (wrong-tier burn, cycle-fault, producer-isolation) against the snapshot, before allocation (PROTECT).
- Runs a global allocator that decides every deprioritization and every soft-demand share in a single deterministic pass (ALLOCATE).
- Applies the converged results itself: net load fields, protective burns, storage settlement, the plain-consumer delivery shim, and consumer accumulator drains (WRITE-BACK), then reconciles Powered flags, ledgers, and the self-audits (the tick tail).
- Runs vanilla's per-device post-tick and IC10 phases (DEVICE TICK and LOGIC TICK) as vanilla copies.

The mod's invariants:

- Voltage tiers are always on. Cables of different tiers are mutually incompatible; transformers and Area Power Controllers (APCs) bridge between them.
- Recursive (cycling) cable networks are always faulted. PowerGridPlus's PROTECT phase detects every powered cycle and trips every segmenting device in it into CYCLE_FAULT for 60 s. No cables burn from cycles. Vanilla's `CheckForRecursiveProviders` and its destructive cable burn never run at all (they lived inside the retired `PowerTick` trio); the mod's directed-SCC walk is the only cycle detection.
- Re-Volt is incompatible and refused at load.
- The tick is atomic: every deprioritization or soft-demand allocation that the allocator decides at tick N takes effect at tick N's downstream device flow. No one-tick latency. No oscillation.

## 2. The atomic electricity tick (phase names)

`AtomicElectricityTickPatch.Prefix` replaces vanilla `ElectricityTick`'s body. The phases below are the names used throughout this spec. Vanilla's per-network `PowerTick` trio (`Initialise` / `CalculateState` / `ApplyState`) is never called anywhere in the pipeline; the mod builds its own view of the grid (SNAPSHOT), solves against it (PROTECT / ALLOCATE), and applies the results itself (WRITE-BACK plus the tick tail).

**Naming note (OBSERVE / ENFORCE, historical).** Earlier revisions of this pipeline ran the vanilla `PowerTick` passes inside the atomic tick and named them SETUP / OBSERVE (the `Initialise + CalculateState` read) and ENFORCE (the re-read plus `ApplyState`). Those phases no longer exist: the boundary read of SNAPSHOT replaced OBSERVE, and WRITE-BACK plus the tick tail replaced ENFORCE. Method names of the tail (`ReconcileEnforceTail`, `SweepEnforceTail`, `SettleEnforceTail`, `RunEnforceTail`) keep the historical suffix; "the tick tail" in this spec means those calls.

0. **HOUSEKEEPING.** Advance the shared tick counter (`ElectricityTickCounter.Advance`). Then, before any grid read: the arm-and-drain load boundary (`LoadBoundary.DrainPending`: world-load clears armed by the load patch run HERE on the worker, never on the load path racing an in-flight tick), the accumulator-debit reconcile (`MainThreadDebitQueue.Reconcile`, decision §0.26), the one-shot unknown-bridge census (§8.8), the one-shot world-load ledger sweep plus the ledger tick-boundary audit (§10.7; a stale saved `_powerProvided` can never bill through the first tick), the save/load self-check (§8.8), the registry hygiene sweep (§8.8), and the emergency-light toggle drain (§8.0.6).

1. **SNAPSHOT.** `GridSnapshot.Build(currentTick)` (`Core/GridSnapshot.cs`): the per-tick grid table everything downstream consumes. Topology comes from `lock(DeviceList)` plus the same power-port membership predicate vanilla's list rebuild uses (`device.PowerCables[i].CableNetwork == net`); the `PowerDeviceList` lazy getter is never touched, so the build never races its unlocked in-place rebuild. Demand discipline: ONE `GetUsedPower` / `GetGeneratedPower` sample per device per tick, here and nowhere else; the accumulator consumer classes are reconstructed through `DemandModel` (decision §0.26) instead of sampled raw. Each net row also carries the vanilla-OBSERVE-equivalent `Required` / `Potential` sums (segmenter surfaces serve the previous tick's published totals, exactly what vanilla OBSERVE saw), the weakest cable cap, and the lowest-rated fuse; each segmenter row captures its control state (OnOff, Error) and both terminal networks once, so every downstream consumer sees the same instant.

2. **PROTECT.** Structural-fault detection against the snapshot, before allocation, so the allocator never sees a fault's inflated supply. In order: `OffAsResetSweep.Run` (clears every lockout on devices the player has switched off, §10.3, before the detectors re-evaluate), wrong-tier cable burn (`VoltageTierEnforcer.Run(snap)`, §3 / §4.3), cycle-fault detection (`CycleGraphBuilder.FindCycleFaultedSegmenters(snap)` -> `CycleFaultRegistry`, the directed-SCC walk of §4.2.5), then producer-isolation / CURRENT_MISMATCH_FAULT (`CurrentMismatchFaultDetector.Run(currentTick, snap)`, §8.5). If anything was newly cycle-faulted or current-mismatch-faulted this tick, the newly locked producers are zeroed IN the snapshot (`snap.ZeroFaultedProducers`): their supply leaves the table in place and the allocator solves the corrected grid with no second observation pass (the old OBSERVE / re-observe pair collapsed into this). Devices faulted on a PRIOR tick already read 0 at the boundary via the enforcement postfixes; newly locked segmenters are consumed by GATHER through the registries directly.

3. **ALLOCATE.** `PowerAllocator.RunAtomic(currentTick)`, the allocator (§8). GATHER consumes the snapshot rows (per-net rigid demand, generator supply, and the sorted segmenter roster; no vanilla topology or demand read happens here), builds the topological order, iterates the fixed-point loop, and decides every deprioritization, overload, elastic discharge share, and storage-charge (soft) grant. Outputs:
   - Set of segmenting devices freshly entering deprioritization lockout (written to `DeprioritizedRegistry`).
   - Set freshly entering overload lockout, with each entry's (valueW, capW) hover payload: capacity trips to `OverloadRegistry`, cable-overflow trips to `CableOverloadRegistry` (decision §0.29).
   - Per-elastic-supplier discharge share (written to `SoftSupplyShareCache`) and per-soft-demand-device charge share (written to `SoftDemandShareCache`).
   - Per-routed-contributor exact in-tick presentation totals, rigid + soft (`TransformerSupplyCache`, written for Transformer, wireless PT/PR pair, and APC alike, §8.0.4); per-APC fresh passthrough draw (`ApcPassthroughCache`) and cell-only discharge share (`ApcCellDischargeCache`).
   - The per-net liveness verdict (`NetLiveness`, decision §0.19: LIVE / DEAD_UNMET / DEAD_NOSUPPLY), computed from the converged state; every published surface on a DEAD net (shares, seg totals, audit grants, write-back credits and debits) is zeroed at the source (decision §0.20).
   - The presentation snapshots: the healthy-segmenter set plus enrolled-seg roster (`PoweredPresentation`, §10.6), the segmenter control snapshot (`SegControlSnapshot`, first-read-wins (OnOff, Error) from the boundary read, consumed by the presentation shims), and the per-net shortfall classification (`ShortfallDiagnostics`, §8.8), each swapped in by volatile reference.
   - The per-tick charge-grant and discharge-grant snapshots for the two delivery audits (§8.8), dead-net-zeroed like every published surface.
   - The write-back plan (`Core/WriteBack.Plan`): per-net `Required` / `Current` / `Potential` figures plus the store credit and debit lists the WRITE-BACK phase applies (§8.0.4 step 10).
   - Per-tick full fault-registry snapshots broadcast to clients (one `FaultRegistrySnapshotMessage` per kind: deprioritized / device-overload / cycle / current-mismatch / dead-input / cable-overload, §13), via `PowerAllocator.SyncFaultSnapshots`.

4. **WRITE-BACK.** `WriteBack.Run(currentTick, snap)` (`Core/WriteBack.cs`) applies the allocator's plan in one pass; everything vanilla `ApplyState` used to do is done here, from converged results, with exact conservation. (a) The net HUD / MP / logic fields: `RequiredLoad`, `CurrentLoad`, `PotentialLoad`, `ShortfallLoad`, plus the `DuringTickLoad` reset; these are MP-serialized to clients and read by the network-analyser cartridge, IC10 `PowerRequired` / `PowerActual` / `PowerPotential`, and the main thread's `AssessPower` headroom check. (b) Fuse protection: the lowest-rated fuse on a net blows when the delivered flow exceeds its rating (vanilla picked a RANDOM breakable fuse; lowest-rated is the deterministic, multiplayer-stable choice); `CableFuse.Break` self-marshals to the main thread. (c) The deterministic 20-tick generator-overflow cable burn (§5.7, ported verbatim from the retired vanilla-check replacement; split-pending gate unchanged). (d) Storage settlement: store credits and debits from the plan, credit == grant by construction, with the battery charge-efficiency factor and the sub-500 W trickle floor applied here (§7.3.0.1) and the delivery audits fed at the settlement site. (e) The plain-consumer delivery shim (decision §0.28): the classified delivery-effect rows (the five vanilla classes whose gameplay runs inside `ReceivePower`, plus the Extra Delivery Devices list) receive `device.ReceivePower(net, granted demand)` on verdict-LIVE nets only, values from the snapshot, per-device try/catch; a DEAD net delivers nothing, vanilla's empty-providers equivalence. (f) The consumer accumulator drains per decision §0.26: one batched main-thread-marshaled debit post for the main-clock classes (SimpleFabricatorBase, Fabricator, ArcFurnace, IceCrusher), synchronous worker-direct debits for the tick-sequenced classes; a net that is not verdict-LIVE drains nothing, so debts freeze exactly and are billed in full on revival.

5. **TAIL** (the tick tail, still on the power worker). `PoweredPresentation.ReconcileEnforceTail` asserts BOTH Powered edges on routed segmenters from the health verdict and is the only tick-driven writer of a segmenter's Powered flag (§10.6); `PoweredOwnership.SweepEnforceTail(currentTick, snap)` asserts both Powered edges on every plain device from the snapshot rows and the net-liveness verdict (decisions §0.19 / §0.28: expected = verdict-LIVE AND structure complete AND the row's OnOff); `LedgerAdoption.SettleEnforceTail` settles every owned segmenter's `_powerProvided` ledger (§10.7); then `PoweredSetConformance.RunEnforceTail` and the charge / discharge delivery audits run (§8.8). The DEVICE TICK and LOGIC TICK below read reconciled state (transformer `PowerActual` equals the tick's actual throughput).

6. **DEVICE TICK.** `ElectricityManager.AllPoweredThings.ForEach(p => p?.OnPowerTick())`. Vanilla copy. Every `IPowered.OnPowerTick` patch from other mods (BatteryLight, HaulerMod, ScriptedScreens) fires here as in vanilla.

7. **LOGIC TICK.** `CircuitHolders.Execute()`. Vanilla copy. IC10 chips tick.

Threading: vanilla schedules `ElectricityTick` on the UniTask ThreadPool worker. The atomic prefix inherits that thread. The only main-thread crossings are the self-marshaling vanilla calls (`SetPowerFromThread`, `Cable.Break`, `CableFuse.Break`) and the batched accumulator-debit post; everything from SNAPSHOT through the tail is managed memory only. §2.1 is the complete ledger of vanilla surfaces the pipeline still touches.

### 2.1 Vanilla touchpoints inside the mod-owned tick

The mod owns the data plane, but "no vanilla `PowerTick`" does not mean "no vanilla code". This is the COMPLETE ledger of every vanilla surface the atomic tick still runs, calls, or writes, with the motivation per row. Anything not listed here is mod-owned managed memory. Any change that adds a vanilla call or a vanilla-field write to the pipeline must add a row here first.

1. **Device tick phase** (`ElectricityManager.AllPoweredThings.ForEach(p => p?.OnPowerTick())`). Vanilla method, unchanged position, runs on the worker. Motivation: vanilla always ran the device tick right there on this thread; third-party `OnPowerTick` patches (BatteryLight, HaulerMod, ScriptedScreens) depend on the slot.
2. **IC10 phase** (`CircuitHolders.Execute()`). Vanilla method, unchanged position, worker. Motivation: same as above; chips must read the settled tick.
3. **Powered edges** (`Device.SetPowerFromThread`, plus the interactable marshaling it routes through). The decision is computed on the worker from the net verdicts and the snapshot rows (§10.6 presentation reconcile, decision §0.19/§0.28 ownership sweep); the write itself is the vanilla self-marshaling funnel and lands on the main thread. Motivation: the funnel is how vanilla itself crossed this boundary, and it feeds the normal interactable replication so clients mirror for free.
4. **Store settlement** (direct `PowerStored` writes on battery / APC cell / umbilical cell, plus the umbilical `LastPowerAdded` / `LastPowerRemoved` reflection setters). Motivation: these are the same writes vanilla `ApplyState` / `ReceivePower` / `UsePower` made on this same thread; values come from the plan (credit == grant), clamped to `[0, PowerMaximum]` by the vanilla property.
5. **Fuse and cable `Break()`** (`CableFuse.Break`, `Cable.Break`). Vanilla methods; each self-marshals to the main thread. Motivation: destruction must run vanilla's own path (wreckage spawn, network split, replication); the mod only decides WHICH fuse or cable, deterministically.
6. **Net load fields** (`RequiredLoad` / `CurrentLoad` / `PotentialLoad` / `ShortfallLoad`, plus the `DuringTickLoad` reset). Same vanilla fields, same tick position vanilla wrote them, worker. Motivation: they are the MP-serialized HUD / logic / analyser surface; every reader (client HUD, IC10 `PowerRequired` / `PowerActual` / `PowerPotential`, `AssessPower` headroom) keeps working unpatched.
7. **Consumer accumulator drains** (`_powerUsedDuringTick` debits). Worker-direct for the ownership-analyzed classes; ONE batched main-thread debit queue for the fabricator family (decision §0.26). Motivation: each class drains on the thread that owns its accrual writes, proven per class by the threading census.
8. **Emergency-light toggles** (`EmergencyLightToggleQueue`). OnOff decisions queue on the worker and the writes defer to the main thread. Motivation: the toggle drives interactables and visuals vanilla owns on the main thread; deferring keeps the tick free of foreign writers.
9. **The five-class `ReceivePower` delivery shim** (decision §0.28). Worker, inside WRITE-BACK between storage settlement and the accumulator drains; allow-listed (`DeliveryEffectClassifier`: PowerTransmitterOmni, SuitStorage, BatteryCellCharger, Bench, WallLightBattery, subclasses included, plus the Extra Delivery Devices config list) and per-device try/caught with a 600-tick warning throttle. Motivation: these five classes' gameplay exists ONLY in the consumer settle; a solver that never calls `ReceivePower` silences wireless charging, suit recharge, cell charging, bench appliances, and the wall-light grid-fed latch. Calling the vanilla method beats reimplementing the effects: version fidelity across game updates, virtual subclass dispatch for free, one surface instead of five clones. Excluded on purpose: the fabricator family (its `ReceivePower` zeroes the main-thread-owned accumulator; row 7's debit queue owns that field), and stores / segmenters (row 4 owns their ledgers; a segmenter row can never classify as delivery-effect, even by config).

Shared safety posture, stated once for all nine rows: every touchpoint runs on the same worker thread and at the same tick position vanilla used it (or defers through vanilla's own main-thread marshaling), consumes snapshot-derived values only (the single boundary read is preserved; no row re-reads live device state), is allow-listed and per-device try/caught where third-party code can run inside it, and never writes a main-thread-owned field from the worker.

## 3. Voltage tiers (always-on invariant)

Stationeers has three cable tiers: normal, heavy, super-heavy. PowerGridPlus enforces tier integrity:

- A cable network is uniform in tier. A cable of one tier connected to a network of another tier triggers the lower-tier cable to burn at the junction, splitting the network. Tier violations are detected two ways (§4.3): on the worker thread each tick (the PROTECT-phase wrong-tier scan over the grid snapshot, the backstop) and immediately on the main-thread topology-event recheck (network-construction and membership postfixes; the 0.2.6403 game update removed the old `OnNetworkChanged` event this path used to subscribe to), so a junction is caught the instant it is built or loaded. The burn itself always executes on the main thread, so the split lands synchronously, before the next tick conducts across the junction. A network with a burn in flight is gated by `SplitPendingRegistry` until its split lands (detected by cable-count change), which replaces the old fixed burn cooldown with a state signal.
- Devices are tier-restricted: generators and stationary batteries on heavy; high-draw machines (Carbon Sequester, Furnace, Advanced Furnace, Arc Furnace, Centrifuge, Recycler, Ice Crusher, Hydraulic Pipe Bender, Deep Miner) may use heavy or normal; normal-cable devices include lights, IC10, sensors, doors, hydroponics, etc.; super-heavy is cables and transformers only.
- Transformers and APCs are tier-exempt. They are the bridges.

This invariant has no toggle. The `EnableVoltageTiers` setting is removed; the always-on rules apply unconditionally.

## 4. Recursive networks (cycle-fault invariant)

PowerGridPlus replaces vanilla's "burn one cable in the cycle" response with a new fault mode: every segmenting device that participates in a detected powered cycle enters CYCLE_FAULT, a third lockout state parallel to DEPRIORITIZED and OVERLOAD. Detection runs in the PROTECT phase. The cycle dissolves immediately because every device in it contributes 0 to its output network for the 60-second lockout duration. No cables burn from cycles.

Rationale:
- Burning a cable is destructive (player loses material), often in a non-obvious spot, and breaks immersion if the loop happens to be unpowered or only briefly powered.
- A fault on every dual-terminal device in the loop is a clear, localised, reversible signal: each "device-in-loop" turns off (red flash, hover text), the loop is broken because each device contributes 0, and after 60 s the devices auto-retry. If the player has rewired correctly the fault clears; if not, the fault re-fires immediately.
- This unifies the response: the player learns "red flash on a dual-terminal device == this device is faulted; check hover for cause" (cause = overload OR cycle-fault).

### 4.1 What vanilla detects

> Reference only: vanilla's recursive check lives inside `PowerTick.CalculateState`, which the atomic pipeline never calls (§2). Nothing in this subsection runs under PowerGridPlus; it documents the behaviour the §4.2.5 DFS replaces and the gaps that motivated it.

The recursive check walks `ElectricalInputOutput.IsProviderToDevice` (a depth-first search through `InputNetwork.PowerTick.InputOutputDevices` chains), capped at 512 hops via `Device.MaxProviderRecursionIterations`. The check covers:

- Direct 2-node cycles: T1 input on N1 + output on N2, T2 input on N2 + output on N1.
- Arbitrary-length cycles through any combination of `ElectricalInputOutput` devices: transformers, APCs, stationary batteries.
- Cycles up to 512 distinct intermediate devices.

Detection action (vanilla): one cable on the cycle (or one fuse, if any exist on the anchor's network) is added to the breakable set per detected anchor, and vanilla `ApplyState` runs `BreakSingleCable` / `BreakSingleFuse` same-tick, picking one entry randomly. Under PowerGridPlus none of this executes.

### 4.2 What vanilla misses

Two known gaps:

1. **Wireless cycles.** `PowerTransmitter` and `PowerReceiver` extend `WirelessPower : Device`, NOT `ElectricalInputOutput`. They never appear in any network's `InputOutputDevices` list and do not override `IsProviderToDevice`. A cycle that closes through a transmitter-receiver wireless hop was invisible to vanilla detection. PowerGridPlus detects it itself: the directed cycle graph (§4.2.5) represents the wireless link through the shared WirelessNetwork node, so a loop closing over a dish pair is found by the same SCC walk as any cable loop. When found, every member enters CYCLE_FAULT non-destructively (§4.5); no cable is burned for a loop.

2. **`_networkTraversalRecord` reuse bug.** Vanilla's `CheckForRecursiveProviders` calls `_networkTraversalRecord.Clear()` ONCE at the start, then iterates anchors WITHOUT clearing between them. The visited set carries over between anchor iterations, so an anchor whose ReferenceId was added during an earlier anchor's walk gets pruned immediately on its own first call. Concrete failure: two anchors A1 and A2 on the same network, A2 in A1's walk path. A1's walk does not close a cycle and returns false. A2 starts its own walk, finds itself already in the visited set, returns false immediately, missing any A2-anchored cycle that runs through a chain disjoint from A1's. No fix is applied: the vanilla walk never executes under the atomic pipeline (§4.1 note), so the bug is documented here only as part of the case against relying on vanilla detection.

3. **Multi-hop loops with no provider anchor.** Vanilla `CheckForRecursiveProviders` walks outward only from a network's existing `Providers` list (`InputOutputDevices` entries that already report supplying power on the network). A cycle that closes through a chain where no participant currently reports as a provider is invisible: battery-only loops (every battery's `_powerProvided` is 0 because no downstream device has pulled yet), APC-only loops, self-sustaining mutual-feed loops where every network's `Required` is 0 because the loop is feeding itself. Vanilla anchors on the wrong vertex or finds no anchor at all; it never starts the walk.

4. **Single-anchor blind spots.** Even when vanilla finds an anchor, it walks from one anchor at a time and bails the moment it closes a single cycle. A topology with two disjoint cycles sharing a network anchors on one, breaks one cable, then the second cycle persists until the next tick re-detects it. Cumulative cost: cascading partial-tick burns where a single pass should have caught everything.

### 4.2.5 PowerGridPlus's own DFS (replaces vanilla-driven detection)

The PROTECT-phase cycle detector is the ONLY cycle detection in the pipeline: vanilla's `CheckForRecursiveProviders` lives inside the retired `CalculateState` and never runs (§4.1 note). Relying on it was never an option anyway; its walk has the structural gaps in §4.2 (wireless invisible, single-anchor blind spots, multi-hop loops with no provider anchor, battery-only / APC-only cycles with `Required = 0` everywhere). PowerGridPlus builds and walks its own graph from the tick's grid snapshot.

Algorithm:

1. **Build the graph from the snapshot.** Nodes are (a) every net row in the tick's grid snapshot and (b) every segmenting device in the snapshot's sorted segmenter roster (per §5.0 and §8.0.5, sorted by `ReferenceId ASC`); terminals come from the segmenter rows' captured `InputNetwork` / `OutputNetwork`, so the walk sees the same instant the boundary read billed under. Each segmenting device contributes its edge between its input and output networks (directed input -> output per decision §0.4's SCC model). Wireless PT-PR links contribute an additional network-to-network edge subject to the OnOff gate in §6.5.

2. **DFS from every segmenting device.** Walk from each segmenting device in sorted order. Track a per-walk visited set (cleared per starting device, never reused across walks); a back edge to a vertex already on the current DFS stack identifies a cycle.

3. **Mark cycle participants.** Every segmenting device on a cycle path is registered for CYCLE_FAULT (60s lockout). Devices that appear in multiple detected cycles get registered once with the longest applicable timer (effectively just the standard 60s, since all cycles fire at the same tick).

4. **Determinism.** Sorting `SegmentingDeviceRegistry.AllSegmentingDevices` by `ReferenceId ASC` before the DFS walk guarantees identical traversal order across MP peers without any float dependence; pair this with the §8.0.5 sort key for the allocator and the cycle detector becomes a free MP-deterministic operation.

5. **Vanilla detection retired.** `CheckForRecursiveProviders` never runs (it lived inside the retired `CalculateState`), `BreakableCables` / `BreakableFuses` are never populated, and `BreakSingleCable` / `BreakSingleFuse` are never invoked. There is no vanilla destructive fallback behind the DFS any more (§17.7 / §17.25): the DFS is the sole cycle signal, and a cycle it missed would conduct un-faulted until a code fix lands. That trade is deliberate; the price of the old backstop was running the whole vanilla trio.

This covers the gaps vanilla misses: wireless edges are first-class graph edges; multi-hop loops are walked end-to-end regardless of anchor presence; battery-only and APC-only cycles are walked because every battery and APC is a graph node regardless of its current `_powerProvided` value; self-sustaining mutual-feed loops are walked because the graph topology is purely structural and never consults `Required`.

### 4.3 Pre-allocator burn (PROTECT phase)

> 2026-06-13 (B+C rework): wrong-tier burns no longer attempt a same-tick network re-enumeration. The
> split from `Cable.Break()` is parented to `Cable.OnDestroy` and lands on the main thread at end of
> frame (see `Research/GameClasses/Cable.md`, "Network split on destruction"), so it is observed by the
> NEXT tick, not mid-tick. The cycle DFS therefore walks the pre-tier-split topology; this is
> harmless because a cycle through a soon-to-burn cable is real this tick and is dissolved by zeroing its
> members (§4) regardless of whether the cable is gone. The original post-burn re-enumeration step is
> removed. See POWER_DEVIATIONS.md P1 for the full rationale and the dedi verification.

The cycle fault fires BEFORE the allocator runs, so the allocator never sees a cycle's inflated supply and demand numbers. PROTECT sits between SNAPSHOT and ALLOCATE, and runs in this order:

1. **SNAPSHOT (prerequisite)**: `GridSnapshot.Build` has already captured topology, the single boundary read, and each segmenter's terminals (§2 step 1). Every detector below consumes the snapshot; nothing re-reads the live grid.
2. **Wrong-tier detection (worker thread)**: `VoltageTierEnforcer.Run(snap)` clears landed pendings (`SplitPendingRegistry.SweepLanded`), then detects any tier violation per the §3 invariant from the snapshot rows (no `Transform` access, so it is worker-safe). A detected violation on a non-pending network is reserved in `SplitPendingRegistry` and its burn is marshalled to the main thread, where the mixed-tier victim walk and `Cable.Break()` run synchronously; the split lands at end of frame. This is the per-tick backstop. The immediate path is the main-thread topology-event recheck (the network-construction and membership postfixes in `VoltageTierPatches`; the 0.2.6403 game update removed the old `OnNetworkChanged` event), which re-checks and burns the instant any membership mutation creates a junction -- before any tick conducts across it.
3. **Cycle detection and CYCLE_FAULT**: run the §4.2.5 walk over the snapshot's sorted segmenter roster plus the wireless edges from §6.5, identify segmenting devices on each cycle path, mark them for CYCLE_FAULT (§4.5). It walks the snapshot topology (any tier split queued by the wrong-tier scan lands next tick); a cycle through a doomed cable is dissolved by zeroing its members this tick regardless. The mod's walk is the only cycle signal; no vanilla breakable set exists any more (§4.6).
4. **Producer-isolation (CURRENT_MISMATCH_FAULT)**: the §8.5 walk runs right after cycle detection.
5. **In-snapshot zeroing (replaces the old re-observe pass)**: if any producer was newly current-mismatch-locked this tick, `snap.ZeroFaultedProducers` removes its supply from the table in place, so ALLOCATE solves the corrected grid with no second observation pass. Newly cycle-faulted segmenters need no zeroing: GATHER reads the fault registries directly and enrolls them `Locked` (conducting 0).
6. **ALLOCATE**: the allocator runs against the clean post-fault snapshot, and skips committing new deprioritization / overload lockouts on any network with a burn in flight (`SplitPendingRegistry.IsPending`), so no durable decision is made against a merged topology that is about to split (Option C).
7. **WRITE-BACK**: as in §2 step 4. It never drives cycle-burns (PROTECT handled cycles by faulting); its only destructive actions are the §5.7 generator-overflow burn and the protective lowest-rated fuse blow.

Wrong-tier detection before cycle detection is the locked order within PROTECT. Doing it the other way would let cycle detection walk through wrong-tier cables that are about to vanish, wasting the work and (worse) potentially marking devices that the wrong-tier burn would have isolated anyway.

### 4.4 Only powered loops burn

A cycle whose loop carries no current is invisible to the player and faulting a device there would break immersion. The PROTECT phase faults only when the loop is actually carrying power:

A detected cycle is "powered" when at least one member network's `min(Potential, Required) > 0`, evaluated on the grid snapshot's vanilla-OBSERVE-equivalent per-net sums (`CycleGraphBuilder.IsPowered`). Only powered cycles register CYCLE_FAULT; unpowered cycles are simply not acted on (they re-detect next tick automatically because the DFS re-walks every tick).

This means an unused cycle (e.g., a build-time mistake in an unpowered grid) sits silently until something downstream pulls power; the moment power flows, the loop faults.

### 4.5 Cycle-fault: how loops break

PowerGridPlus replaces vanilla's cable burn for cycles with a CYCLE_FAULT mode. When the PROTECT phase detects a powered cycle:

1. Identify every segmenting device whose `InputNetwork` or `OutputNetwork` participates in the loop. This is the full §5.0 segmenter list applied uniformly: `Transformer`, `Battery`, `AreaPowerControl`, `PowerTransmitter`, `PowerReceiver`, `RocketPowerUmbilicalMale`, `RocketPowerUmbilicalFemale`. PowerConnection is excluded per §5.0.1.
2. Mark all of them for CYCLE_FAULT, with no class-specific exemption. A Battery on a cycle gets CYCLE_FAULT (red flash, hover countdown), stops bridging power through its input + output terminals, and stops contributing to elastic supply on its output net for the full 60 s. An APC gets the same treatment. PT/PR pairs get it on both the PT and PR side. RocketPowerUmbilicalMale gets the visual flash; RocketPowerUmbilicalFemale uses the hover-only path per §5.0.2.
3. Each device enters the new `CycleFaultRegistry` with `_lockoutUntilTick = currentTick + 120` (60 seconds).
4. While in CYCLE_FAULT each device's contribution to power flow is 0 (same effect as DEPRIORITIZED / OVERLOAD). For elastic suppliers (Battery, APC, RocketPowerUmbilical*) this means `GetGeneratedPower` returns 0 via the SoftSupplyShareCache postfix path (§7.3.0.1); the device does not discharge into its output network while locked out, even if it has stored energy.
5. Because every segmenting device in the loop is now contributing 0, the loop is broken: the allocator sees clean state on the next iteration of §8.0 within the same tick. No cable is destroyed.
6. After 60 seconds the fault auto-clears. The next allocator pass re-detects: if the cycle still exists, CYCLE_FAULT re-fires. If the player has rewired correctly, devices re-engage.
7. The OFF-as-reset (§10.3) applies uniformly to every CYCLE_FAULTed segmenter that has a clickable OnOff button: a player toggling the device OFF then ON clears its lockout instantly, allowing manual retry. For RocketPowerUmbilicalFemale (no OnOff button) the only manual reset is to wait out the timer.

This unifies the "device is faulted, hover for cause" experience:
- Orange flash + the Deprioritized fault block ("{On|Off} - Deprioritized fault: {n}s" over the Needs diagnostics line) -> upstream undersupply.
- Red flash + the overload hover block ("{On|Off} - {Transformer|Link|Battery|APC|Umbilical} overloaded fault: {n}s" / "{On|Off} - Cable overloaded fault: {n}s", each over its watt diagnostics line) -> downstream overcommitment (§11.1).
- Red flash + "{On|Off} - Cycle fault: {n}s" over "This device is part of a power loop" -> topological loop.

CYCLE_FAULT uses the same red flash code path as OVERLOAD because both are "downstream / topology" faults the player needs to fix, not just upstream supply issues.

### 4.6 No cable burn from cycles

The vanilla `BreakableCables` / `BreakableFuses` lists are never populated: the code that filled them (`CheckForRecursiveProviders` inside `CalculateState`) and the code that consumed them (`ApplyState`'s `BreakSingleCable` / `BreakSingleFuse`) both live in the retired `PowerTick` trio and never run. Cycle detection is PGP's own walk over the segmenting-device graph (§4.2.5), the response is CYCLE_FAULT, and no cable or fuse is ever destroyed for a cycle. There is no vanilla destructive fallback behind the DFS (§17.7 / §17.25).

The cable-burn rule for non-cycle overloads (direct generator supply sustained above the weakest cable's cap) lives in the WRITE-BACK phase per §5.7. Transformer-side overflow does not burn cables either (it trips overloads). Fuse protection is also the write-back's: the lowest-rated fuse on a net blows when the delivered flow exceeds its rating (§5.7).

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

Idle hover behaviour on `RocketPowerUmbilicalFemale`: when the device is NOT in a fault state, PGP does NOT touch the hover text at all. The vanilla `GetPassiveTooltip` output (which may be blank or show vanilla pre-built status text) is what the player sees. The PGP postfix early-returns if `DeprioritizedRegistry`, `OverloadRegistry`, `CycleFaultRegistry`, and `CurrentMismatchFaultRegistry` all report "not locked" for this `ReferenceId`. PGP injects content only when a fault is active.

### 5.0.3 The segment adapter contract (ISegAdapter / SegSpec)

GATHER (§8.0.0.1) does not open-code each bridge class; it consults one `ISegAdapter` per modelled class (`SegAdapters.cs`). An adapter answers `Describes(device)` (pure type membership, state-independent) and `TryDescribe(device, out SegSpec)` (the device's per-tick PHYSICAL description: flow kind, terminal networks, capacities, distance multiplier `m`, quiescent draw, pair partner; false when the device presents no flow surface right now). GATHER attaches allocator POLICY on top of the description: priority, lockout state, deprioritization / overload bookkeeping. Two flow models:

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

Any `ElectricalInputOutput` subclass that no adapter describes and the segmenter roster does not know is an unknown bridge: it keeps its vanilla power methods unpatched, the snapshot's boundary read samples its `GetUsedPower` / `GetGeneratedPower` once per tick as plain rigid demand / generation on each side (the honest conservative fallback, §8.0.0.2), and the one-shot census reports it at world load (§8.8).

### 5.1 Direction classification

Every transformer is classified at runtime as `StepUp`, `StepDown`, or `SameTier`:

- Read the cable tier of `InputNetwork` (looking at the cable type predominant on the network).
- Read the cable tier of `OutputNetwork`.
- `Direction = inputTier < outputTier ? StepUp : (inputTier > outputTier ? StepDown : SameTier)`.

Cable tier ordering: normal (1) < heavy (2) < super-heavy (3). The classifier reads cable type directly off the network's `Cables` collection; it does not need the `EnableVoltageTiers` flag because tiers are always on.

Edge case: a network in transition (mid-burn) may briefly have mixed cables. The classifier uses the cable with the highest tier as authoritative for that tick; classification stabilises after the burn completes.

### 5.2 Direction governs deprioritization eligibility

- **Step-up transformers are never deprioritized.** They are pass-through points. Their `GetGeneratedPower` formula runs vanilla-faithfully, capped at `OutputMaximum` and `InputNetwork.Potential - _powerProvided`. The allocator's deprioritization pass skips them entirely.
- **Step-down and same-tier transformers are deprioritization-eligible.** They participate in the deprioritization cascade described in §7.

### 5.3 Throughput rating, Setting, Priority, and dial

PowerGridPlus separates three concepts that vanilla conflated:

- **`OutputMaximum`** (vanilla field, prefab-specific): the transformer's *rated* throughput. 5 kW small, 50 kW large, etc. Hard ceiling on what the transformer can ever do. Not directly writable.
- **`Setting`** (vanilla field, `[0, OutputMaximum]` range): the transformer's *active* throughput cap this tick. Vanilla treats Setting as the dial-controlled throughput. PowerGridPlus keeps `Setting` fully vanilla-writable from IC10 and reverts the previous PGP behaviour of redirecting `Setting` writes to `Priority`. IC10 scripts that read or write Setting work exactly as in vanilla.
- **`Priority`** (PGP-added store, non-negative integer): the dispatch priority used by the deprioritization cascade. Non-negative integer, no upper cap, default 100. Stored in `PriorityStore` keyed by `Thing.ReferenceId`. Persisted via `PrioritySideCar`. `Priority = 0` means the lowest priority value; the device still receives allocation if any supply remains after higher-priority devices are served. `Priority = 0` is NOT a sentinel for "disabled" or "skip" -- a player who wants to disable a transformer toggles its OnOff button or sets `Setting = 0` instead.

Init-time defaults (PGP-imposed):

- **On new transformer construction**: when a player places a transformer and the build completes, PowerGridPlus sets `Setting = OutputMaximum` so the freshly placed transformer runs at full rated throughput by default.
- **On world load**: PowerGridPlus does NOT overwrite `Setting` on existing transformers. A transformer that has an IC10-throttled `Setting < OutputMaximum` saved in the world keeps its saved value. Only freshly placed transformers get the OutputMaximum default.
- This matches the spec's "transformer runs at full rated throughput by default" assumption for new builds while preserving any deliberate IC10 throttle the player set in a previous session. In practice almost no player will ever touch `Setting`; the visible knob writes Priority. But legacy or sophisticated IC10 scripts that write `Setting` to dynamically throttle a transformer continue to work, and their saved state is honoured across save / load.

In-world writers redirect to Priority:

- The in-world **dial / knob** writes to `Priority`, not `Setting`. Button1 / Button2 step Priority by ±10 (no Alt) or ±1 (with Alt).
- The **Labeller** tool, when used on a transformer's dial, writes to `Priority`, not `Setting`. (The Labeller is the player-held tool that reads / writes numeric device values; PGP's existing label patch covers this.)
- Any other future in-world UI that writes the transformer's "knob value" should also be redirected to Priority. Add per case.

**Throttle hover warning (deviation P13).** A transformer whose `Setting` is below its `OutputMaximum` is running a custom throttle that, under PowerGridPlus, can only have come from IC10 (the dial writes Priority) or a legacy / vanilla save where the player dialed the vanilla Setting down. To keep that from being a mystery dark subnet, PGP surfaces it: a neutral info block (no flash, no countdown) appears on BOTH the transformer's case hover and its on/off button title box (`TransformerThrottleHover`, rendered through the single merged-block renderer `FaultHover.TryGetMergedBlock` and surfaced by `FaultHoverPatches` and `FaultButtonTooltipPatches`):

> `{On|Off} - Throttled` (amber `#d9a441` title, no countdown)
> `Limited to 3.2 kW of 50.0 kW by the IC10 Setting value` (live `FmtWatts` values; the word `Setting` in vanilla yellow `#FFFF00`)
> `The dial sets priority instead of power`

Fresh transformers default `Setting = OutputMaximum` (above), so the line never shows on a default build. It is suppressed while any fault block is active (one block per hover, §11.1), so a throttled transformer that also overloads shows only the fault; the block returns when the fault clears (and still explains the §8.4 overload after the fact). The saved `Setting` is still honoured (not migrated, §5.3); the warning just makes the IC10-or-rebuild fix discoverable instead of silently leaving a dark subnet. The colour is a muted amber (`#d9a441`) to read as "advanced use" without mimicking the deprioritized flash orange.

IC10 access:

- `LogicType.Setting` reads return the live `Transformer.Setting` value. Writes update `Transformer.Setting`, clamped to `[0, OutputMaximum]`. Pure vanilla. Default after init = `OutputMaximum`.
- `LogicType.Maximum` returns `OutputMaximum` (vanilla).
- `LogicType.Ratio` returns `Setting / OutputMaximum` (vanilla).
- `LogicType.Priority` reads and writes the `PriorityStore` entry. Read returns the int priority; write quantises to non-negative int.
- `LogicType.DeprioritizedFault` returns 1 during DEPRIORITIZED lockout, 0 otherwise (read-only).
- `LogicType.DeviceOverloadedFault` returns 1 during OVERLOAD lockout, 0 otherwise (read-only).
- `LogicType.CycleFault` returns 1 during CYCLE_FAULT lockout, 0 otherwise (read-only).

**Formulas in this document use `Setting` for the active throughput cap, NOT `OutputMaximum`.** Setting starts equal to OutputMaximum and can be reduced by IC10 to dynamically throttle a transformer. See §5.5 / §5.6 / §8.4 for explicit usage. OutputMaximum still appears in formulas only as the *upper bound* of what Setting can be (the prefab rating).

### 5.4 Per-input-network priority comparison

This is the rule that replaces the old global priority sort. Priorities are compared ONLY among transformers that share the same input cable network. A transformer at priority 100 on the main grid is not compared with a transformer at priority 100 on a mid-net even if both are at the same depth from the supply. This is what makes "minimize the blast radius" tractable: each input network's siblings compete only with each other.

### 5.5 Binary state and effective throughput formula

A transformer (or APC, or PT/PR pair) has a binary operating state per tick: ON or LOCKED-OUT (deprioritized OR overloaded). There is no "throttled" intermediate state. A transformer either gets its full needed draw from its input network or it is deprioritized entirely.

State semantics:

- **ON**: input network supplies the transformer's full required draw, output network demand is at or below `Setting`. Working normally; `actual_throughput = min(Setting, output_demand)`.
- **DEPRIORITIZED**: input network cannot supply the transformer's full required draw. Lockout for 60 seconds. Contributes 0 to its output network.
- **OVERLOAD**: input adequate; output demand exceeds `Setting`. Lockout for 60 seconds. Contributes 0 to its output network.

Both DEPRIORITIZED and OVERLOAD result in the transformer being "off" for downstream purposes (0 W contribution). The cause differs and so do the visuals and IC10 flags.

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

The generator-overflow burn runs in the WRITE-BACK phase (`Core/WriteBack.Run`, §2 step 4); no vanilla cable-burn check exists any more (the trio is retired). A cable burns only when GENERATOR-direct supply alone exceeds the cable cap, sustained over the window below. Transformer-derived overflow does NOT burn the cable; it trips the upstream transformers into CABLE_OVERLOADED (the allocator's §8.0.3 cable-overflow rule, decision §0.29).

Per snapshot net row N in WRITE-BACK:

1. `generator_supply_N` = the row's `GenSupply`: the boundary-read generation of every plain (non-segmenter) device on N (true generators: SolarPanel, CoalGenerator, RTG, WindTurbine, HydrogenBurner, etc.). Batteries discharging on N are NOT counted here; they self-cap and never push past the cable.
2. `actual_flow_N` = the delivered flow the write-back plan carries for N (the figure written to `CurrentLoad`).
3. Apply rules:
    - Observe `min(generator_supply_N, actual_flow_N)` into network N's 20-tick sliding window (`CableBurnWindow`) every tick. If the 20-tick running average exceeds the weakest cable's cap (the snapshot row's `WeakestCap`), BURN the cable at the output of the generator that produced the most over the window (deterministic; ties by lowest ReferenceId). A full window is required first (10 s grace), the window resets on a burn, and a network with a burn already in flight is skipped (`SplitPendingRegistry.IsPending`, the unchanged split-pending gate). `Cable.Break()` self-marshals to the main thread. This replaces the old per-tick `generator_supply_N > cap` instantaneous test (and the interim probability ramp) so a transient spike must be sustained, or countered by a dip within 10 s, to burn.
    - Cable overflow NOT caused by generators alone (the excess comes from upstream transformers / PT/PR pairs / batteries) is the allocator's business, decided in ALLOCATE before the write-back runs: it trips EVERY supplier feeding N (every transformer, APC, PT, battery, elastic whose `OutputNetwork == N`) into CABLE_OVERLOADED (§8.0.3 pass 4; `CableOverloadRegistry`, with the (flow, weakest-cable cap) hover payload). NO cable burn.
    - Else: no action.

The write-back also owns fuse protection on the same pass: the lowest-rated fuse on N (captured on the snapshot row) blows when the delivered flow exceeds its rating. Vanilla picked a RANDOM member of its breakable set; lowest-rated is the deterministic, multiplayer-stable choice, and it matches the physical intuition that the weakest fuse pops first. `CableFuse.Break` self-marshals.

The "generator + transformer" mixed case is uncommon but well-defined: if `generator_supply_N` alone is under the cap, but transformer contributions push the total over, the transformers take the trip; the cable survives because the generators alone wouldn't have killed it.

Batteries do not contribute to the cable-burn risk because their `GetGeneratedPower` is elastically capped at the rigid shortfall (§7.3); they only push as much as is being pulled from the downstream side, which is by construction at or below the network's demand and therefore at or below the cable's draw budget.

This rule means a player who lays out an under-sized cable carrying a heavy transformer's output sees the transformer trip the Cable overloaded fault, with the hover naming the flow against the wire's rating (`Pushing 12.1 kW onto a 5.0 kW wire`), rather than the cable burning silently in a random spot.

### 5.6 Cable max settings

Cable `MaxVoltage` per tier is read at runtime from the live `Cable` instance, not hardcoded. PowerGridPlus exposes three server-authoritative settings to override per tier (all values in Watts):

- `CableNormalMaxWatts`: default 5000 (vanilla normal value).
- `CableHeavyMaxWatts`: default 50000 (vanilla heavy value).
- `CableSuperHeavyMaxWatts`: default 0 (= unlimited, internally stored as `float.MaxValue`).

A configured value of `0` means unlimited and is normalised internally to `float.MaxValue`. The setting description says "0 = unlimited" so operators know the rule.

PowerGridPlus does NOT rewrite cable `MaxVoltage` (D2 non-mutating, deviation P14): `Cable.MaxVoltage` is a per-instance serialized field, so writing it would bake the configured caps into the save and survive mod removal. Instead every cap reader (battery / APC headroom, the allocator, the snapshot builder's per-net weakest cap, the write-back's generator-overflow burn rule) consults the `CableMax` helper, which reads the configured per-tier setting live at use-time; there is no vanilla burn check left to patch (the trio is retired), so the helper is the single source for every cap comparison. Removing the mod reverts cables to vanilla ratings with no save contamination. This subsumes the legacy `EnableUnlimitedSuperHeavyCables` setting (equivalent to `CableSuperHeavyMaxWatts = 0`); the legacy setting is removed.

All formulas in §5.5 and elsewhere are generic across tiers: the per-tier numeric values come from settings, not hardcoded constants.

## 6. Power transmitter model

Power transmitter / receiver pairs are first-class transformers at the allocator layer. The synthetic transformer is a MODEL the allocator builds in ALLOCATE by reading existing fields. The actual energy bookkeeping is the mod's own: the snapshot's boundary read samples the pair's patched surfaces once per tick, the allocator grants the pair's exact throughput and pull, and the write-back publishes the net fields; the vanilla `PowerTick` trio never runs, so `GetGeneratedPower` / `GetUsedPower` on the dishes survive as presentation shims (tooltips, IC10, PowerTransmitterPlus's own readers, the next tick's boundary read). PowerGridPlus's per-device touches on `PowerTransmitter` / `PowerReceiver` are the lockout postfix (returns 0 when the pair is locked out, §6.4), the fresh input-draw bill and the delivery gate (`PowerTransmitterDrawPatches`: the transmitter's shim serves the pair's exact in-tick `TotalPull` from `TransformerSupplyCache`, and a last-priority postfix clamps the advertised wireless delivery to the granted `TotalThrough`, §8.0.4), the tick-tail Powered reconcile that owns the pair's Powered flag (§10.6), and the tick-tail ledger settle (§10.7).

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
- `FaultFlashBehaviour` attached to the PT's on/off button. Deprioritization and overload visual + hover text apply to the PT's button.

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

PT/PR pairs are deprioritization-eligible (§5.2) when the pair's Direction classifies as StepDown or SameTier.

### 6.4 Deprioritization / overload enforcement

Deprioritization and overload lockouts on a PT/PR pair take the link offline: PT draws 0, PR generates 0, downstream goes dark for the 60-second lockout. Same OFF-as-reset behaviour as wired transformers, applied via the PT's on/off button.

Enforcement is via the same registries (`DeprioritizedRegistry`, `OverloadRegistry`, `CableOverloadRegistry`) keyed by PT.ReferenceId. PowerGridPlus's `GetGeneratedPower` postfix on `PowerTransmitter` checks the registries and returns 0 when locked out (this is one of the very few places PowerGridPlus needs to touch a PT/PR per-device method, and it is additive over PowerTransmitterPlus's existing patches: Harmony priority is set late so PowerTransmitterPlus computes its distance-loss number first, then the lockout postfix overrides to 0 if applicable).

### 6.5 Wireless edge gating (OnOff, bidirectional pair enumeration)

A wireless edge between a `PowerTransmitter` PT and a `PowerReceiver` PR exists in the PROTECT-phase cycle graph if and only if:

1. The pair is established: `PT.LinkedReceiver != null` AND `PR._linkedPowerTransmitter != null`. Both cross-references must be set; either being null means the pair is half-broken (a load-time edge case or a mid-pairing transient) and no edge is contributed.
2. BOTH ends are turned ON: `PT.OnOff == true` AND `PR.OnOff == true`. A player turning either dish OFF removes the edge from the graph; cycle walking treats the link as cut.

Fault state does NOT affect edge existence. Per the refined skip-while-faulted rule (§17.39), the PROTECT-phase DFS walks through faulted devices for NEW cycle detection, so faulted PT or PR continues to contribute its edge to the graph. This is consistent with the wired transformer case: a faulted Transformer's `InputConnection` and `OutputConnection` are still graph edges; only its 0-throughput contribution falls out of the allocator's math.

Pair enumeration: walking only `PowerTransmitter` instances and dereferencing `LinkedReceiver` would miss multi-PR fan-out from a single PT, since the PT-side list can only hold one canonical link at a time and the second PR's edge would be invisible. PGP walks BOTH the PowerTransmitter and PowerReceiver instance lists, building the edge set as a deduplicated `HashSet<(long PtRefId, long PrRefId)>` keyed by the pair's two ReferenceIds. The HashSet de-dupes when both walks encounter the same pair. This catches every linked pair regardless of which side carries the canonical reference.

### 6.6 PowerTransmitterPlus compatibility

PowerTransmitterPlus and PowerGridPlus coexist through a resolved-once interop bridge (`PowerTransmitterPlusInterop.cs`; probed at first use, everything cached, no per-tick reflection; PowerGridPlus never hard-references PowerTransmitterPlus, so it stays an optional dependency). The atomic Prefix-return-false on `ElectricityManager.ElectricityTick` replaces the whole vanilla tick: the per-device power methods are no longer called for delivery, but they stay live surfaces (the snapshot's once-per-tick boundary read, tooltips, IC10, and PowerTransmitterPlus's own reads all go through them), and PowerTransmitterPlus's auto-aim patches, link-visibility patches, save side-cars, and IC10 readouts survive unchanged under every tier. Three resolution tiers, degrading in order:

1. **ModApi tier** (PowerTransmitterPlus 1.9.0+, preferred). The public, versioned cross-mod surface `PowerTransmitterPlus.ModApi` (requires `Version >= 1`; members are only ever added, never renamed or removed). PowerGridPlus binds `EffectiveMaxCapacity()` (the configured delivery cap in Watts, 0 = unlimited), `TryGetLink(transmitter, out distanceMeters)` (false when unlinked, so a dropped link's stale cached distance never surfaces), `SourceDrawMultiplier(transmitter)` (the factor `m`), and, bound leniently, the `GetTransferDebt` / `SetTransferDebt` ledger accessors that the ledger adoption uses (§10.7). It then calls `ClaimBillingOwnership("net.powergridplus")` exactly once.
2. **Legacy tier** (the shipped Workshop 1.8.0 line). `DistanceCostShared.SourceDrawMultiplier` (public wrapper, added after the 1.8.0 tag) or the internal `GetMultiplier` (the identical computation; the shipped Workshop build has only this), plus `MaxCapacityConfigSync.GetEffectiveMaxCapacity` and the vanilla `_linkedReceiverDistance` field. No ownership handshake exists at this tier; PowerGridPlus's delivery gate + fresh-pull billing (§8.0.4) keep the ledgers bounded instead.
3. **Absent.** `m = 1` and the vanilla link model (`MaxPowerTransmission` minus the `PowerLossOverDistance` delivery loss, §6.3).

The tiers agree by construction (the ModApi forwards to the same internals the legacy tier binds), so switching tiers never changes the allocator's numbers for the same world state. Exactly one Info line states the resolved tier and, on the ModApi tier, the claim outcome. Without the multiplier the allocator would under-count a long link's source-network draw by a factor of `m` and the no-partial-power invariant would fail on the transmitter's input network (§8.4.2).

**The billing-ownership handshake.** While the `"net.powergridplus"` claim is held, PowerTransmitterPlus's native wireless debt billing stands down: its `UsePower` debt inflation, its `GetUsedPower` source-side cap lift, and its standalone debt ceiling all no-op, and PowerGridPlus's allocator is the single billing authority for wireless links. The capacity advertise (the link-rating definition PowerGridPlus's delivery gate clamps), the receiver drain-cap lift, the beam visuals, and the link handling stay active on the PowerTransmitterPlus side. Each PowerTransmitterPlus billing patch checks `ModApi.BillingOwner` per call, so a late claim is safe regardless of plugin load order. Re-claiming the same id is idempotent; a claim while a different owner holds it is rejected (PowerGridPlus then logs the rejection and relies on the delivery-gate / fresh-pull containment, exactly as at the legacy tier). `ReleaseBillingOwnership` restores native billing.

**PowerTransmitterPlus standalone rules (for reference).** These define the behaviour the handshake stands down and the always-on parts of the environment the allocator runs in (`DistanceCostPatches.cs`):

- **Bounded debt ceiling** (standalone only, no billing owner): the advertise prefix pauses delivery (advertises 0) while the transmitter's unpaid `_powerProvided` debt is at or above `ceiling = effectiveCap * max(m, 1) * 4`, where `effectiveCap` is the configured Max Transfer Capacity, or the vanilla `MaxPowerTransmission` (5000 W) when the cap is unlimited (0). Delivery resumes as the source pays the debt down (one warning per pause episode; the episode ends once the debt falls below half the ceiling). This bounds the native debt runaway on an insufficient source and keeps the lump bill after an OnOff cycle finite.
- **Receiver drain-cap lift** (ALWAYS active, including under the handshake): vanilla `PowerReceiver.GetUsedPower(wireless)` bills `min(MaxPowerTransmission + UsedPower, debt)`, so a link delivering above 5 kW would strand the excess as receiver debt the source is never billed for (free energy). The lift raises the bound to `min(cap + UsedPower, debt)` for a configured cap above 5000, and to the full debt when the cap is unlimited (0); it never lowers the vanilla result. Flows above 5 kW are therefore billable end to end.
- **Unlinked multiplier is exactly 1.0**: `ModApi.SourceDrawMultiplier` returns 1 for a null or unlinked transmitter (and `TryGetLink` returns false), so the stale cached `_linkedReceiverDistance` a dropped link leaves behind never inflates a bill. For a linked transmitter, `m = 1 + k * distance_m / 1000` (k host-synced, default 5; `k <= 0` gives `m = 1`; never below 1).

Beyond the interop, the PowerGridPlus-side touches on PT/PR per-device methods are the ones listed in the §6 intro and §6.4: the lockout zero, the fresh input-draw bill plus the delivery gate (§8.0.4), the tick-tail Powered reconcile (§10.6), and the tick-tail ledger settle (§10.7). None conflicts with anything PowerTransmitterPlus does under any tier.

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

Each device's `GetUsedPower(net)` declares a demand. The deprioritization pass cares about which demands are RIGID (must be satisfied or the device fails) and which are SOFT (can be fractionally allocated without harm).

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

Rigid and soft ride ONE demand vector through the same backward/forward sweep (§8.0.3): rigid alone drives every fault decision, soft is granted from the §9.2 funding ladder (firm residual, then soft inflow, then elastic leftover), and unmet soft clamps silently (§9).

Note: Power Transmitters are NEITHER rigid nor soft. They are transformers, treated by §6.

### 7.3 Battery and APC as elastic supply on the Output network

Generators (coal generator, solar panel, RTG, wind, hydrogen burner) are rigid supply: they produce their `GetGeneratedPower` value whether anything consumes it or not. Batteries and APC cells are elastic supply: PowerGridPlus's allocator discharges them first to fill the rigid shortfall on their Output network, and then donates whatever eligible discharge capacity the rigid settlement left over to the net's SOFT pool as its lowest funding rung (§9.2, the elastic-to-soft transfer that makes battery-to-battery charging possible). Rigid keeps absolute priority by construction: the donation is exactly the capacity rigid did not consume.

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
5. If `S_N >= effective_total`: every supplier delivers its full `effective_discharge_i`. Residue `S_N - effective_total` is rigid demand still unmet (which the joint allocator §8 then treats as triggering deprioritization/overload on suppliers).
6. If `S_N < effective_total`: proportional share, each supplier delivers `share_i = effective_discharge_i * (S_N / effective_total)`. By construction `share_i <= effective_discharge_i`, so no saturation occurs.
7. SOFT TOP-UP (§9.2): the net's converged elastic-funded soft quantum (`ElasticFundedSoft`, the granted soft the firm pool could not cover) is then distributed over the same eligible elastics full-or-proportional to each one's leftover (`effective_discharge_i - share_i`), and each elastic's published share becomes `rigidShare_i + softTopUp_i`, still at most `effective_discharge_i`. Steps 1-6 are unchanged by the top-up: rigid consumed first, the top-up only hands out what rigid left over.

Worked example: 2 batteries on the same output network, both with discharge rate 200 W. Battery A is full (stored >> 200). Battery B has only 19 stored. Shortfall 300 W on the network.

- effective_A = min(200, full) = 200.
- effective_B = min(200, 19) = 19.
- effective_total = 219.
- `S_N = 300 >= effective_total = 219`, so each delivers its full effective: A = 200, B = 19. Sum 219. Residue 81 W rigid still unmet. The allocator (§8) then considers the rigid supply ceiling on N to be `G_N + 219`, and if `D_N > G_N + 219` triggers deprioritization somewhere upstream.

Note we do NOT throttle Battery A to "equal share" (300/2 = 150). Battery A delivers its full capable amount; Battery B contributes whatever it can; the residue surfaces as unmet rigid demand to the allocator.

For deprioritization planning (§8.0 iteration) the network's rigid supply ceiling is `G_N + sum_of_effective_discharge_i` plus inbound flow from non-deprioritized upstream transformers. If rigid demand exceeds this ceiling, deprioritization cascade or overload triggers.

### 7.3.0.1 Critical implementation note: SoftSupplyShareCache

**The allocator's elastic decision must be visible on every surface a reader can consult, or the store's advertised supply and its settled drain disagree.** Two mechanisms carry the §7.3 per-battery `delivered_i`: the WRITE-BACK debits each store exactly its granted share (`Core/WriteBack`, credit == grant by construction; this is the actual energy movement, since vanilla `ApplyState` never runs), and a `SoftSupplyShareCache` (parallel to `SoftDemandShareCache` for charge demand) feeds Harmony postfixes that clamp the advertised discharge, so the boundary read, tooltips, IC10, and any third-party caller see the granted figure and never raw headroom:

```csharp
// Battery.GetGeneratedPower postfix
__result = Mathf.Min(__result, SoftSupplyShareCache.GetShare(__instance.ReferenceId));

// AreaPowerControl.GetGeneratedPower postfix (same shape)
// RocketPowerUmbilicalFemale.GetGeneratedPower postfix (same shape)
// RocketPowerUmbilicalMale.GetGeneratedPower postfix (same shape)
```

Cache lifecycle is a freshness-stamp model. The cache holds entries keyed by `ReferenceId` with payload `(long tickWritten, float share)`. ALLOCATE writes `(currentNetworkTick, share)` for every active supplier as the iteration converges. The `GetGeneratedPower` postfix reads the cache: if an entry exists AND `tickWritten >= currentNetworkTick - 1`, the postfix returns the cached share. Otherwise (entry older than one tick or absent), the postfix falls back to the vanilla formula (raw `PowerStored`). Self-cleaning: stale entries from cable breaks or supplier reassignment are naturally distrusted on read; no explicit invalidation step is needed. In-memory only, not serialised.

These postfixes are presentation shims, but they are not optional (§17.33). The next tick's boundary read samples exactly these surfaces, so an unclamped advertise would feed raw `PowerStored` back into the solve; a locked-out or dead-net supplier must read 0 everywhere; and delivery on the wire (the store's `PowerStored` movement) matches the advertise because the write-back settles the same granted share the cache published. Historical note: under the retired vanilla-trio pipeline this postfix was also what kept ENFORCE's `_powerRatio` at 1 (raw `PowerStored` below rigid demand produced ratio-scaled partial power on rigid devices); with the trio gone there is no ratio math left to protect, and the shim's job is read-surface consistency.

### 7.3.1 Local-battery failsafe

A battery's discharge to its OUTPUT network is fully independent of any state on its INPUT network. If an upstream transformer feeding the battery's input net is deprioritized, the battery cannot charge but its output side continues to discharge to the output network's rigid loads as long as it has stored energy.

Example:
```
N0 (generator)  ->  T1 (DEPRIORITIZED)  ->  N1 (rigid lights, plus battery B input)
                                   B output -> N2 (rigid lights)
```

When T1 is deprioritized: N1 has 0 inflow, N1's lights go dark (B's Input terminal is consumer-only and cannot back-feed N1). But N2's lights continue to run on B's discharge as long as B has stored energy. This is the "kept-alive by local battery" failsafe and is the natural consequence of the dual-terminal model (§7.1); no special-case code is required.

The symmetric case for APCs has the in-tick interlock caveat (§7.1): if the APC chose to charge from its input this tick, it cannot also discharge to its output this tick. Stationary batteries have no such interlock.

### 7.4 Battery as elastic demand on the Input network

A battery's `GetUsedPower(InputNetwork)` reports `PowerMaximum - PowerStored` (full headroom) in vanilla. PowerGridPlus treats the charge request as SOFT demand: GATHER enrolls each battery with `Request = min(EffectiveChargeCap, PowerMaximum - PowerStored)` (§7.6), the request propagates leaf-to-source through the same `BackwardDesirePass` as rigid demand, and the forward sweep grants it from the §9.2 funding ladder (firm residual, then soft inflow, then elastic leftover). The granted share lands in `SoftDemandShareCache`, and the `Battery.GetUsedPower` postfix min-clamps the reported charge demand to it, so each battery charges exactly its grant. A battery is simultaneously a soft demander on its Input network AND an elastic supplier on its Output network. Per §7.1 the two roles run independently in the same tick when the two networks are distinct.

### 7.5 Splitting APC demand

`AreaPowerControl.GetUsedPower(InputNetwork)` returns passthrough + internal-charge bundled in vanilla. PowerGridPlus splits the two STRUCTURALLY at GATHER time (§8.0.0.1):

- **Passthrough** is the APC's routed seg (§5.0.3): its share of the output network's demand rides the (rigid, soft) demand vector like any other contributor, so the passthrough portion is never summed into the input network's rigid demand directly, and it needs no subtraction heuristics.
- **Internal-charge** is a separate Soft request on the input network: `Request = min(ComputeChargeCap(apc), cell.PowerDelta)`, where `ComputeChargeCap` is the configured `ApcBatteryChargeRate` bounded by the input cable's remaining headroom after the APC's own passthrough (`AreaPowerControlPatches.ComputeChargeCap`) and `PowerDelta` is the inserted cell's remaining headroom.

The APC is the one `QuiescentAlwaysOn` contributor (§8.0.3 pass 1): vanilla bills its 10 W quiescent whenever `OnOff && OutputNetwork != null` regardless of throughput, so the allocator funds an idle APC's bare quiescent as a quiescent-only pull (`Pull = 10, Throughput = 0`) and the patch bills it under the same vanilla gate only while the published `TransformerSupplyCache` pull is positive: an idle healthy APC publishes `(0, quiescent)` and keeps billing it, an inactive (deprioritized / overloaded / cycle-locked) APC publishes all-zero totals and bills 0 like every other inactive contributor, and a roster-absent APC (cache miss) bills 0, a deliberate vanilla deviation matching the errored-transformer 0-bill. The cell-charge portion bills 0 when the APC has no fresh `SoftDemandShareCache` share this tick (roster-absent: errored, short-circuited, or output-less), closing the unfunded phantom-draw hole the (since-retired) partial-power sentinel exposed on 2026-07-07. Delivery is the write-back's: the cell is credited exactly the granted charge share and drained exactly the granted cell discharge share at the settlement site (`Core/WriteBack`, credit == grant by construction; the credit is recorded there for the charge-delivery audit), so no ReceivePower-stream bookkeeping exists any more (the old `DeliveryTickLedger` alignment prefix ran inside the retired vanilla delivery path). A fresh share with a cell that can take no charge marks the grant moot for the audit (`MarkChargeGateClosed`, the Mode-based `IsCharged` fill edge).

The `AreaPowerControl.GetUsedPower` prefix (a presentation shim feeding the boundary read, tooltips, and third-party callers) rebuilds the input-side bill from fresh allocator figures: the quiescent `UsedPower`, plus the fresh total passthrough from `ApcPassthroughCache` (rigid + soft-charge flow crossing the APC, §8.0.4), plus the charge portion `min(ComputeChargeCap, cell.PowerDelta, SoftDemandShareCache share)`. The internal-charge portion is storage-charge flow (§9); the passthrough portion is rigid.

### 7.6 Soft-demand request value

Each soft-demand store declares a per-tick `Request`, capped three ways (charge rate cap, cable cap, remaining headroom), computed at GATHER per class:

- Stationary battery: `Request = min(EffectiveChargeCap, PowerMaximum - PowerStored)`, where `EffectiveChargeCap = min(per-prefab configured charge rate, input cable tier cap)` (`StationaryBatteryPatches.EffectiveChargeCap`). The per-prefab rate caps are always on; there is no master toggle (decision §0.27).
- APC internal cell: `Request = min(ComputeChargeCap(apc), cell.PowerDelta)` (§7.5).
- Rocket umbilical cell: `Request = min(configured charge rate, input cable tier cap, PowerMaximum - PowerStored)` (`UmbilicalAdapter`, §5.0.3).

The request is a first-class SOFT demand on the store's input network: it aggregates into the per-round billable local sum (`BillableSoftRequestLocal`), rides the same backward/forward sweep as rigid demand (§8.0.3 / §9), and the granted share is published to `SoftDemandShareCache`, which the per-class `GetUsedPower` patches min-clamp against. Enrollment requires a BILLABLE owner: GATHER refuses a store whose owner sits in a fault lockout (the lockout enforcement zeroes the owner's bill on every published surface, so a granted share could never be billed or delivered: the 464386 finding, one full 120-tick dawn lockout of zero-credit grants), the per-round `SoftOwnerBillable` gate covers an owner that is deprioritized or overloads inside the deciding loop, and an inoperable battery (vanilla `IsOperable` false) is never enrolled. There is no separate request pipeline and no shared headroom helper; the request computation lives in GATHER and the adapters as above.

## 8. The allocator

PowerGridPlus has exactly one power allocator (decision §0.8). It runs in the ALLOCATE phase (`PowerAllocator.RunAtomic`) and decides every deprioritization, every overload, every elastic discharge share, and every storage-charge (soft) grant for the whole grid in one deterministic pass, against the fresh per-network demand and supply the SNAPSHOT phase's single boundary read populated (§2 step 1). There is no second strategy and no toggle: "the allocator" throughout this spec is the single implementation described here.

### 8.0 What the allocator does (overview)

`RunAtomic` runs in three stages, then a shared tail:

1. **GATHER** (§8.0.0.1): consume the tick's grid snapshot: the per-network rigid-demand and generator-supply numbers straight off the net rows, the contributor / elastic / soft rosters from the snapshot's sorted segmenter roster, and the first-read-wins segmenter control snapshot.
2. **ORDER** (§8.0.2): a true topological (Kahn) order over the live contributor edges, replacing the deleted minimum-depth BFS.
3. **DECIDE** (§8.0.3): an iterated fixed-point loop (`RunAllocationLoop`) that settles deprioritization and overload and computes each contributor's exact in-tick throughput.
4. **Shared tail** (§8.0.4): dead-input cue, lockout commit (with the elastic-overload network retry), elastic-share pass (§7.3), the per-net liveness verdict (decision §0.19), and the publish tail that writes the share caches, the per-contributor presentation totals for every routed seg kind, the audit grant snapshots, the Powered-presentation, control, and shortfall snapshots, the write-back plan, and runs the conservation check (§8.8). Storage-charge grants are decided inside DECIDE's forward sweep (§9); the tail only publishes them. Every published surface is zeroed for a DEAD net at the source (decision §0.20).

Deprioritization, overload, and cycle-fault interact non-trivially inside a single tick, which is why DECIDE iterates rather than running a single sweep:

- A deprioritized transformer contributes 0 to its output network. Its siblings on the same output network now bear more load and may individually exceed their `Setting` -> OVERLOAD.
- An overloaded transformer also contributes 0. Its input network is no longer drawn from. Its siblings on the same INPUT network now have more headroom.
- A cycle-faulted device contributes 0 on both sides; cycle-fault is decided in the PROTECT phase (§4.5), so cycle-faulted contributors enter the allocator already marked `Locked` and conduct 0. The live graph the allocator sees is therefore a DAG.
- A battery on an output network whose rigid demand exceeds upstream supply plus the battery's discharge contribution is the elastic-supply ceiling case (§7.3); the loop accounts for it.

The whole iteration happens inside ALLOCATE of one game tick. From outside, one tick produces one joint result; the atomic invariant (§17.1) holds.

### 8.0.1 Overload is grow-only / sticky; only deprioritization is re-decidable

This is the rule that makes the loop both correct and convergent.

- **OVERLOAD is grow-only (sticky).** The overload flags (`seg.Overloaded`, `e.Overloaded`) are cleared exactly once per tick, at loop entry in `RunAllocationLoop`, and NEVER inside a round. Once a round sets an overload it stays set for the rest of the tick, so an overload commits even if a same-tick deprioritization in its subnetwork would have removed the condition. This is intended: overload is the structural signal the player must fix (a transformer that genuinely cannot serve its downstream), not a transient artifact of the iteration order.
- **DEPRIORITIZED is re-decidable.** `ForwardSupplyAndDeprioritize` clears `seg.Deprioritized` for every contributor at the top of each round and recomputes deprioritization fresh against the settled state. So a deprioritization that another device's overload later frees the budget for is retracted within the same tick; an unnecessary deprioritization never freezes for 60 s.

Together these realize the fault precedence **CYCLE > CURRENT-MISMATCH > CABLE-OVERLOADED > DEVICE-OVERLOADED > DEPRIORITIZED** (decision §0.3 as amended by §0.29): structural overload is evaluated BEFORE the deprioritization pass each round (§8.0.3), so a transformer that structurally cannot serve its downstream is diagnosed OVERLOAD, the higher-precedence fault, rather than getting deprioritized first and mislabeled as input-starved.

**Resetting a sticky fault.** A committed overload (or cycle-fault, or current-mismatch) clears only two ways: the 60-second lockout timeout (§10.2), or a player turn-off. `OffAsResetSweep` (§10.3) clears every fault registry for a device the player switched off; a switched-off device also leaves the allocator's roster entirely (the GATHER step skips `!OnOff` segmenters), so turning it back on re-introduces it and the fault re-detects on the next pass if it still holds. There is no mid-tick auto-clear of a committed overload.

### 8.0.0.1 GATHER: rosters and per-network numbers

GATHER consumes the tick's `GridSnapshot` (§2 step 1): the net rows for the rigid numbers and the snapshot's sorted segmenter roster (`ReferenceId ASC`, §8.0.5) for the structural rosters. It performs no vanilla topology or demand read of its own; every number below was sampled exactly once at the boundary.

- **Per-network rigid demand + generator supply.** The snapshot builder classified every device row: for a device that is NOT a segmenter (segmenters are modelled structurally), its boundary-read demand adds to `RigidDemand` and its boundary-read generation adds to `GenSupply` (accumulator classes reconstructed via `DemandModel`, decision §0.26). Producers current-mismatch-faulted on a prior tick already read 0 at the boundary (their enforcement postfix); producers newly faulted THIS tick were zeroed in the snapshot by PROTECT (`ZeroFaultedProducers`, §2 step 2), so a silenced producer contributes no supply either way. One exception rides the snapshot build: a rocket umbilical half's own idle draw is funded as plain rigid demand on its input network under the per-half vanilla gates (`RocketUmbilicalPatches.QuiescentBill`: Male whenever ON, Female whenever wired and not errored; the Buffered adapter has no routed seg to carry a quiescent pull), so the bill is never a phantom no advertise covers. GATHER also records the first-read-wins control snapshot (`SegControlSnapshot`): each segmenter's (OnOff, Error) as captured on its snapshot row, consumed by enrollment, the quiescent bill, and the presentation shims, so a player toggle landing mid-tick takes effect next tick, coherently with the grant.
- **Contributors (`Seg`)**: `Transformer`, linked `PowerTransmitter` / `PowerReceiver` pairs (one `Seg` anchored on the PT, §6.2), and `AreaPowerControl`. Each carries the physical description its adapter produced (§5.0.3): its effective cap (`min(CapSetting, cable caps) - quiescent draw`, where `CapSetting` is `Transformer.Setting`, the pair's static link rating, or +inf for the APC), the input-draw factor `m`, plus the policy GATHER attaches: its `Priority`, its step-up flag (§5.2), and a `Locked` flag (true if cycle-faulted, in a prior-tick deprioritization / overload window, or paired with a cycle-faulted partner half: it conducts 0 and is not re-decided).
- **Elastic suppliers (`Elastic`)**: a `Battery`, `AreaPowerControl` cell, or `RocketPowerUmbilical*` with stored energy, discharging onto its output network to fill rigid shortfall first and donating its leftover to the net's soft pool (§9.2). Effective discharge = `min(rate cap, cable cap, stored)`.
- **Soft demanders (`Soft`)**: a `Battery`, APC cell, or umbilical charging from its input network out of the §9.2 funding ladder (firm residual, then soft inflow, then elastic leftover). Request = `min(charge rate cap, cable cap, headroom)` (§7.6).

The §5.0 segmenter list is exhaustive; an unhandled segmenting class falls to the unknown-bridge fallback (one honest boundary sample per side, summed as plain rigid demand / generation, §8.0.0.2) and is reported by the census, so the gap is visible and conservatively billed rather than silently mis-modelled. Battery / APC / umbilical are modelled as elastic suppliers and soft demanders rather than pull-through contributors; only Transformer, the PT/PR pair, and APC's passthrough are pull-through `Seg`s.

Each contributor class is classified by direction (§5.1), priority-sorted (§5.4), elastic-supply-capped (§7.3) where applicable, and deprioritization / overload-eligible per §8.0.3. Catching every segmenting class is what stops the undersupply cases the adversarial review flagged (a battery on an undersupplied output net; an APC with a depleting cell; a PT/PR pair feeding past its effective cap, especially with a PowerTransmitterPlus distance derate; a rocket umbilical with a rigid load on the rocket side) from entering the solve as inflated demand a supplier cannot actually fund. Every segmenting class also needs a `GetGeneratedPower` lockout postfix that returns 0 when in DEPRIORITIZED, either overload kind (DEVICE_OVERLOADED or CABLE_OVERLOADED), CYCLE_FAULT, or CURRENT_MISMATCH_FAULT, so a locked-out contributor reads 0 on every surface: the next tick's boundary read, tooltips, IC10, and any third-party caller (the write-back independently delivers nothing for it, since an inactive contributor publishes all-zero totals).

### 8.0.0.2 Unknown devices: one honest boundary sample, all-or-nothing liveness (no ratio fallback exists)

The vanilla `_powerRatio = Potential / Required` scaling path no longer exists in the pipeline: it lived inside `PowerTick.CalculateState` / `ApplyState`, which are never called (§2), and nothing in the mod reimplements it. There is no partial-power path of any kind, on any network, for any device class.

An earlier revision deliberately kept vanilla scaling reachable as a fallback for unclassified devices, on the rationale that graceful degradation beats phantom power when a third-party mod or a game update introduces a device class the roster does not know. That concern is answered structurally now, without a ratio path:

- **Unclassified plain devices and unknown bridges get one honest boundary sample.** The snapshot reads their unpatched `GetUsedPower` / `GetGeneratedPower` once per tick and sums the result as plain rigid demand / generation on each side. The demand enters the solve at face value and is either funded whole or not funded; nothing is scaled. Unknown bridges are additionally reported once per type at world load (the census, §8.8).
- **Liveness is all-or-nothing.** A network that cannot fund its rigid demand classifies DEAD_UNMET (decision §0.19) and goes dark as a unit: the write-back settles no energy there, consumer accumulators freeze exactly and bill in full on revival, and the Powered ownership sweep darkens the whole net. Whole-device honest darkness replaces the old ratio-scaled partial delivery in every case the fallback used to soften.
- **Phantom power is guarded by audits, not by scaling.** The conservation checker, the two delivery audits (plan-vs-settlement identity), and the ledger audit machine-check every granted watt (§8.8); a mis-modelled device surfaces as a counted violation instead of silently browning out a subnet.

The partial-power sentinel that monitored the old fallback's unreachability is deleted with the path it watched (§8.8); §17.35 and §17.45 record the replacement invariants.

### 8.0.2 ORDER: topological (Kahn) order over live contributor edges

`BuildTopoOrder` builds a true topological order over the live contributor edges `InNet -> OutNet`, replacing the deleted minimum-depth BFS. Why topological and not min-depth: a diamond (a network fed through two contributor chains of different length) takes the shorter depth under a min-depth scheme, so the longer feeder's supply is not yet finalized when that network is processed, and the residual a multi-stage chain leaves is exactly the one-tick supply-propagation lag (§8.0.6). A topological order lands every network AFTER all the networks that feed it, diamonds included, so the backward / forward sweep sees fresh upstream supply at every depth with no residual lag.

Mechanics:
- In-degree counts only non-`Locked` suppliers. Cycle-faulted segments are `Locked` and excluded, so the live graph is a forest / DAG.
- Ready networks (in-degree 0) are popped in `ReferenceId ASC` order, and the unprocessed tail is kept sorted by `ReferenceId` as networks become ready, so the pop order is deterministic across MP peers.
- `Net.Depth` is set to the topo index (used to align contributor depth and as the iteration order).
- A residual cycle (should not occur after PROTECT-phase cycle removal) is appended in `ReferenceId` order with a `LogWarning`.

The topo order is internal to the allocator: DECIDE's backward and forward passes iterate it, and the write-back applies the converged plan wholesale, so no downstream phase needs an ordered network walk any more. (The retired ENFORCE phase consumed a published `ShallowFirstNetworks` order so each network's vanilla `CalculateState` could run after its feeders' `PotentialLoad` refresh; with the trio gone, that publication is deleted.)

### 8.0.3 DECIDE: the iterated fixed-point loop

`RunAllocationLoop` iterates a backward / forward sweep to a fixed point. Flag lifecycle once per tick at loop entry: `seg.Deprioritized`, `seg.Overloaded`, and `e.Overloaded` are all cleared once here, together with the overload KIND bit (`CableOverloaded`) and the (valueW, capW) hover payload that travel with the flag (decision §0.29: `Overloaded` keeps meaning "offline this tick" for BOTH overload kinds, the solve reads only it; the kind bit routes the commit to `CableOverloadRegistry` instead of `OverloadRegistry`, and the payload is written by whichever detector first trips the device). Inside the loop, DEPRIORITIZED is re-decided every round and OVERLOAD only ever grows (§8.0.1).

Each round runs four passes in this fixed order:

1. **`BackwardDesirePass`** (leaf -> source, over the reversed topo order), carrying BOTH flow classes in one walk as a (rigid, soft) demand vector. RIGID: each contributor's `DesiredThroughput` is its share of its output network's residual rigid need (`RigidDemand + consumer pulls - GenSupply`, floored at 0; priority tier DESC, proportional by `EffCap` within a tier, capped at `EffCap`, §8.3.2); `DesiredPull` is the matching input-side draw (`throughput * max(InputDrawFactor, 1) + UsedPower`). An idle contributor whose class bills its quiescent whenever ON (`QuiescentAlwaysOn`, today only the APC, §7.5) presents a bare quiescent-only `DesiredPull = UsedPower`; every other idle contributor presents 0. SOFT: the network's soft desire (`BillableSoftRequestLocal + active consumers' SoftDesiredPull`) splits over the SAME suppliers with the same tier-first proportional splitter, but each contributor's soft capacity is its residual headroom `EffCap - DesiredThroughput`, so soft never displaces rigid capacity; `SoftDesiredPull = SoftDesiredThroughput * max(InputDrawFactor, 1)` plus the quiescent draw ONLY when the contributor carries no rigid pull (the quiescent is carried exactly once, on the rigid pull when any rigid flows, else on the soft pull; in the forward pass the soft pull carries only the remainder the GRANTED rigid pull left uncovered, `max(0, UsedPower - Pull)`, so the seg invariant stays exact even when a quiescent-bearing rigid pull is granted partially). The soft split runs independently of the rigid residual: a net with zero rigid residual still routes charge. Eligibility differs by class: rigid desires gate on `DesireActive(s) = !s.Locked && !s.Overloaded` (deprioritization is deliberately IGNORED so a previously-deprioritized contributor still presents its desired pull and can be reconsidered), while soft desires gate on the stricter `IsActive` (also not deprioritized): soft never drives a deprioritization decision, so a deprioritized contributor's charge desire must not size its suppliers, or the delivered soft would strand on its input net billed-but-unconsumed; an un-deprioritized next round restores the desire one round later. A LOCAL store's request follows the same rule through its OWNER (`SoftOwnerBillable`): GATHER refuses enrollment while the owner sits in a fault lockout, the per-round billable-desire sums exclude an owner that is deprioritized or overloads inside the loop (so no upstream soft inflow is ever sized for it), and the forward grant assigns such a store a zero share; the lockout enforcement zeroes an unbillable owner's bill on every published surface, so any grant here could never be billed or delivered (the 464386 finding, §8.8). Generators are subtracted first; elastic storage is the documented last resort (§7.3) and is not modelled in this pass (it absorbs the per-net shortfall in the forward sweep / elastic-share pass, matching the gen -> transformer -> battery supply order; its LEFTOVER likewise enters only the forward pass's soft pool, §9.2, so the backward pass needed no change for the elastic-to-soft rung: soft desires are demand-sized, and funding is a forward-pass concern).

2. **`DetectStructuralOverload`** (desire-based, evaluated BEFORE the deprioritization pass). A network whose post-deprioritization demand exceeds `GenSupply + AvailableElastic + sum of its non-deprioritized suppliers' EffCap` overloads its `Setting`-limited suppliers (input-limited PT pairs included, taken offline). Suppliers are summed at `EffCap` even when already overloaded, so the condition keeps re-detecting them rather than oscillating (an overloaded supplier contributes 0, which would otherwise make the network look relieved). Cable-limited suppliers (`CapSetting > CableCap`) and APCs (no rating) are excluded; cable overflow is rule 3 below. This is the DEVICE_OVERLOADED fault kind (decision §0.29); the rule stamps its hover payload from its own locals, (net rigid desire, combined deliverable cap), net-level numbers shared by every supplier it flags on the net. This pass is desire-based and has no forward dependency, so it is safe to run before the forward pass; running it before the deprioritization pass is what gives a structurally-overcommitted transformer the OVERLOAD diagnosis instead of a DEPRIORITIZED mislabel (§8.0.1, precedence).

3. **`ForwardSupplyAndDeprioritize`** (source -> leaf, over the topo order, so every supplier's `Throughput` is already final). For each network it computes the supply actually arriving (`firmIn = GenSupply + active suppliers' Throughput`, plus `AvailableElastic`) and `budget = avail - RigidDemand`. Deprioritization is RE-DECIDED against the RIGID claims only: `seg.Deprioritized` is cleared for every seg at the top of the pass (when not settle-only), then if the active consumers' desired rigid pulls exceed the budget victims are deprioritized whole (never partial) per the tier-major best-fit selection in `SelectDeprioritizationVictims` (§8.3) until the rest fit; step-up segments are never deprioritized (§5.2); a network with no supply at all (`avail <= Eps`) deprioritizes nothing (dead-input idle, §8.3.1). Survivors are then granted highest-priority-first, and each contributor's exact `Throughput`, `Pull`, and the per-net `InflowCommitted` / `PullsGranted` / `RigidServed` / `Unmet` are written. AFTER the rigid grants, SOFT (storage charge) is granted per network from the §9.2 funding ladder, consumed in order: (1) the local firm residual `firmIn - RigidDemand - granted rigid pulls` floored at 0, (2) the soft inflow arriving through the net's active suppliers (granted when THEIR input nets were processed earlier in topo order), and (3) the net's ELASTIC LEFTOVER `AvailableElastic - max(0, RigidDemand + granted rigid pulls - firmIn)` floored at 0 (the lowest rung: eligible discharge capacity the rigid settlement did not consume, tapped only for the soft shortfall firm supply could not cover). The whole pool is capped by the weakest cable's remaining headroom (`CableMax.WeakestCapOnNetwork - (RigidServed + granted rigid pulls)`). One ratio `softRatio = min(1, softAvail / softDemand)` scales every local charge request and every active consumer's soft pull on the net; a consumer's granted soft throughput is further capped at its remaining headroom `EffCap - Throughput`, and a deprioritized / locked / overloaded consumer gets zero soft. The per-net `SoftGrantedLocal` / `SoftPullsGranted` / `ElasticFundedSoft` (the granted soft the firm pool could not cover, measured against actual granted totals) and per-seg `SoftThrough` / `SoftPull` are written. Deprioritization decisions, budgets, and `Unmet` never see the soft class; unmet soft desire is silently clamped (§9.6), and the leftover is a pure function of the round's rigid settlement, so the new rung adds no oscillation mode to the fixed point.

4. **`DetectSupplyOverload`** (after the forward pass, because both rules read the forward pass's `Unmet` / `PullsGranted`). Two rules: the elastic hit-max (a network still `Unmet` after gen + inflow + full elastic discharge trips its live elastics, §8.4.1, so a storage-fed subnet goes dark cleanly; capacity family, `OverloadRegistry`, shared payload computed at the cohort commit), and the §5.7 cable overflow (flow above the weakest cable cap with generators alone under it trips every supplier + elastic on the network instead of burning the cable; the CABLE_OVERLOADED kind, decision §0.29: the rule stamps the kind bit plus the (flow, weakest-cable cap) payload here, and the commit routes to `CableOverloadRegistry`).

The **structural-overload-before-deprioritization-before-supply-overload** ordering is load-bearing: a deprioritization that relieves an over-demanded network is honoured in pass 3 of the SAME round, so pass 4 sees the reduced demand. Combined with grow-only overload (§8.0.1), this kills the deprioritization <-> overload 2-cycle (§8.0.7).

**Convergence.** After the four passes, the `(deprioritized, overload, elastic-overload)` RefId sets are collected. If they equal the previous round's sets, the loop has converged. If they equal the round-before-previous sets, the loop is in a 2-cycle between two states: the intermediate state's flags are OR-ed in (a safe superset, never under-protective), then one `BackwardDesirePass` + `ForwardSupplyAndDeprioritize(settleOnly: true)` settles throughputs without re-deciding, and the loop exits. The round cap is `2 * segs.Count + 4`; exhausting it logs a `LogWarning` and keeps the last settled state (internally consistent and safe, possibly not minimal). Only deprioritization can oscillate (overload is monotonic), so the 2-cycle guard plus the union fallback is sufficient.

**The stranded-inflow clawback.** The fixed point deliberately keeps deprioritized contributors visible to the backward desire pass (deprioritization is re-decided every round, so a deprioritized seg must keep presenting its claim to be reconsidered). The converged state can therefore carry inflow granted to fund a claim the same forward pass then deprioritized: committed and billed upstream, taken by nobody. After the loop, the lockout commits, the dead-input rebuild, and the shortfall census (all of which read the deciding state), any tick that deprioritized at least one contributor runs a clawback in `RunAtomic`: walking leaf to source, every network whose total committed inflow (rigid plus soft) exceeds its total consumption (served demand, granted pulls, charge, and soft pulls; soft counts because the soft stage funds charge from the rigid firm residual) takes the surplus back from its active suppliers in reverse grant order, shrinking each seg's published throughput and pull together (a fully clawed always-on-quiescent seg keeps its funded quiescent pull, `QuiescentAlwaysOn`, since vanilla bills that draw regardless of throughput) and landing the pull reduction on the supplier's input network before that network is visited. There is no re-split and no re-grant (a full settle re-pass was tried and re-granted the freed budget to other branches, reshaping real allocation on trip ticks), so every seg off the stranded chains keeps its deciding-pass numbers to the bit and the only real-world change is that nothing is billed upstream for power nobody consumes. No decision is re-opened.

### 8.0.4 The shared tail (commit, shares, publish, snapshots)

After DECIDE converges, `RunAtomic` runs a single shared tail against the converged per-net / per-seg fields:

1. **Dead-input cue** (§8.3.1): a contributor whose input network has no effective supply at all idles instead of being deprioritized; if it is actively trying to pass power downstream it is flagged in `DeadInputRegistry` for a steady "no upstream supply" hover (no lockout, instant recovery).
2. **Commit lockouts.** For each non-`Locked` contributor not on a split-pending network (Option C, §4.3 step ALLOCATE), a `Deprioritized` flag stamps `DeprioritizedRegistry`, and an `Overloaded` flag stamps `CableOverloadRegistry` when the `CableOverloaded` kind bit is set, else `OverloadRegistry` (decision §0.29); either way the entry carries the (valueW, capW) hover payload captured at the detection site. Prior-tick lockouts (already `Locked`) carry unchanged.
3. **Elastic-overload network retry, then reset** (§8.4.1): a network with a newly overloaded elastic is a candidate. Only the CAPACITY family (the elastic hit-max) forms cohorts here; a cable-overflow (`CableOverloaded`) elastic commits per device into `CableOverloadRegistry` with the (flow, weakest-cable cap) payload from its detection site and skips the retry, because re-engaging a store cannot raise the cable's rating (decision §0.29). Before locking a capacity cohort, the allocator retries at the network level: if the cohort's combined discharge would cover the residual demand the locks are cleared (recovered, no timer reset), otherwise one shared fresh expiry is stamped across the cohort (arms the 60 s lockout and keeps the cohort phase-synced), together with one shared hover payload computed at this commit (the net's total rigid want against the pool maximum with the cohort re-engaged, so every member shows one consistent pair). Transformers keep their per-device §8.4 timer; this retry is the elastic-specific, network-property branch.
4. **Elastic shares** (§7.3 + §9.2): per output network, each battery / APC cell / umbilical's share is its RIGID component (the residual rigid shortfall `RigidDemand + PullsGranted - GenSupply - InflowCommitted`, floored at 0, full-or-proportional against effective caps) plus its SOFT TOP-UP (its full-or-proportional-to-leftover slice of the net's converged `ElasticFundedSoft` quantum), totalling at most `EffDischarge` per elastic.
5. **Net liveness** (decision §0.19): the per-net LIVE / DEAD_UNMET / DEAD_NOSUPPLY verdict, computed from the converged post-clawback state BEFORE anything publishes, so a DEAD net's shares, totals, audit grants, and write-back entries can all be zeroed at the source (decision §0.20: dead nets deliver literally nothing). LIVE iff `Unmet <= Eps` AND `GenSupply + InflowCommitted + AvailableElastic > Eps`; DEAD_UNMET arms the 60 s anti-flap hold; the verdict map is published to `NetLiveness` for the ownership sweep and the accumulator-drain gate.
6. **Share caches**: the elastic shares -> `SoftSupplyShareCache` and the per-device storage-charge grants the forward sweep already computed (§8.0.3 pass 3 / §9) -> `SoftDemandShareCache`. There is no separate distribution pass; the former surplus walk is deleted. A store on a DEAD net publishes a zero share in both caches.
7. **Presentation totals for EVERY routed seg kind.** For each contributor the tail computes `TotalThrough = Throughput + SoftThrough` (output-side delivery, rigid + storage-charge flow) and `TotalPull = Pull + SoftPull` (input-side bill), which whenever the seg conducts equals `TotalThrough * max(m, 1) + quiescent` exactly, and writes `TransformerSupplyCache.Set(RefId, TotalThrough, TotalPull)` for `Transformer`, wireless PT/PR pair, AND `AreaPowerControl` alike. An inactive contributor (deprioritized / overloaded / cycle-faulted) caches all-zero totals; a seg whose OUTPUT net is verdict-DEAD caches all-zero totals too (all-or-nothing: no trickle into a dark subnet, no bill for undelivered flow). An idle always-on-quiescent APC caches `(0, quiescent)`. Publishing totals for every kind is what guarantees a granted soft flow has a carrier on BOTH terminals of its segment: a battery charging behind a transformer or wireless pair sees the charge advertised downstream AND billed upstream in the same tick (the pre-rearchitecture rigid-only cache writes were the deadlock that left chargers idling at 0 W forever with no fault anywhere). The APC additionally publishes:
   - `ApcPassthroughCache.Set(RefId, TotalThrough)`: the fresh input-side passthrough bill, rigid + soft-charge flow crossing the APC (there is no `GrantThrough` field any more; `SoftThrough` subsumed it).
   - `ApcCellDischargeCache.SetShare(RefId, cellShare)`: the cell-only elastic share, so `DischargeSpeed` means the cell rate consistently across storage classes (deviation P9).
   - `SoftSupplyShareCache.SetShare(RefId, TotalThrough + cellShare)`: the bundled supply cap, because vanilla `AreaPowerControl.GetGeneratedPower` bundles passthrough with the cell (`AvailablePower`).
8. **Audit grant snapshots** (§8.8): the per-store charge-grant snapshot (refId, granted watts, charge-side net, store kind, straight from the softs roster) and the per-store discharge-grant snapshot (battery / umbilical only; the APC cell is deliberately not published). Grants on DEAD nets are zeroed exactly like the caches and the write-back plan, so the audits expect what the settlement will actually do.
9. **Powered-presentation and control snapshots** (§10.6): the healthy-segmenter set (`healthy = IsActive && (conducting || idle on a supplied input with no unmet rigid demand)`; a pair publishes under both halves' ReferenceIds) plus the enrolled-seg roster carrying each seg's `TotalThrough` / `TotalPull` and its ledger-settle eligibility, swapped into `PoweredPresentation` by volatile reference like the share caches; the GATHER-time segmenter control snapshot is published to `SegControlSnapshot` for the presentation shims.
10. **The write-back plan** (`Core/WriteBack.Plan`, decision §0.24 stage 3): per-net `Required = RigidDemand + PullsGranted + SoftPullsGranted + SoftGrantedLocal`, `Current = RigidServed + PullsGranted + SoftPullsGranted + SoftGrantedLocal`, `Potential = GenSupply + InflowCommitted + granted elastic`, plus the store credit list (each soft's granted share) and the store debit list (each elastic's granted share), credits and debits zeroed on DEAD nets exactly like the published caches so all-or-nothing holds at the settlement layer too. The WRITE-BACK phase applies this plan verbatim (§2 step 4).
11. **Shortfall classification snapshot** (§8.8): every allocator net's end-of-tick RIGID state (Served / Dry / Throttled / Deadlock) into `ShortfallDiagnostics`, same volatile-swap publication.
12. **Conservation check** (§8.8, config-gated): per net, granted inflow must equal granted outflow within 0.5 W; per seg, `TotalPull == TotalThrough * max(m, 1) + quiescent`.

The presentation caches exist because the patched device surfaces remain live read surfaces even though vanilla no longer calls them for delivery: the next tick's boundary read, hover tooltips, IC10, and third-party callers (PowerTransmitterPlus included) all consult `GetUsedPower` / `GetGeneratedPower`, and they must see the allocator's exact granted figures rather than vanilla's formulas. (Historically the fresh caches also fixed the one-tick lag of billing from the `_powerProvided` accumulator, which vanilla filled during the previous tick's `ApplyState`; with the trio retired the ledger is not a billing channel at all, see §10.7.) Each routed class is served from the fresh cache, unconditionally (no allocator-selection gate; decision §0.8):
- The `TransformerExploitPatches` prefixes (presentation shims): `Transformer.GetGeneratedPower` serves the `TransformerSupplyCache` output (`TotalThrough`, exact delivery); `Transformer.GetUsedPower` serves the cached input draw (`TotalPull`) instead of `min(Setting + UsedPower, _powerProvided)`; an errored transformer serves 0. Conservation holds (input == output + conversion loss), so the free-power exploit stays closed on every surface that could re-open it.
- `AreaPowerControlPatches.GetUsedPowerPatch` (presentation shim) reports quiescent (vanilla gate `OnOff && OutputNetwork != null`, AND a positive published `TransformerSupplyCache` pull: inactive or roster-absent APCs report 0 quiescent) + the `ApcPassthroughCache` passthrough (input == output, no lag) + the cell-charge portion capped to its `SoftDemandShareCache` share; a missing share (roster-absent APC) reports 0 charge. The cell's actual credit and drain settle in the write-back at exactly the granted shares (§7.5); the old `ReceivePower` delivery-alignment prefix is retired with the vanilla trio.
- `RocketUmbilicalPatches` (presentation shims) report each half's vanilla quiescent (funded as plain rigid demand by the snapshot builder under the per-half gates, §8.0.0.1) plus the granted charge share; a missing share reports 0 instead of vanilla's full-headroom fallback. The cell is credited exactly its granted share by the write-back, so the vanilla free-charge quiescent leak cannot recur; the old `ReceivePower` quiescent-burn prefixes are retired with the trio.
- `PowerTransmitterDrawPatches.GetUsedPowerPatch` (presentation shim) serves the cached input draw (`TotalPull`) for the PT/PR pair (the transmitter carries the pair's input-cable figure; the receiver's `InputNetwork` is null so it already returns 0), and its last-priority `DeliveryGatePatch` postfix on `PowerTransmitter.GetGeneratedPower` clamps the advertised wireless delivery to the granted `TotalThrough`, so every reader sees the granted figure and a transfer debt cannot be seeded by an ungated advertise during a startup or deprioritization transient.

**Battery and Umbilical are terminal storage, not pass-through, and need no in-tick throughput cache.** Their discharge onto an output network is gated to the elastic-share pass (`SoftSupplyShareCache`, §7.3) and their charge draw to the storage-charge grant (`SoftDemandShareCache`, §9), both computed fresh in-tick from the same supply-accurate converged sweep, published for the read surfaces, and settled by the write-back at exactly the granted amounts. A PT pair's advertise is NOT left as headroom: the delivery gate clamps it to the granted `TotalThrough`, so both terminals of the pair carry the exact granted figures.

Both `TransformerSupplyCache` and `ApcPassthroughCache` are tick-stamped, in-memory, self-cleaning per-`ReferenceId` stores (same shape as `SoftSupplyShareCache`): a read older than one tick falls back to "no fresh value", so the shim reports 0 (or its vanilla formula) until the allocator roster includes the device again. The allocator writes in ALLOCATE; readers later in the same tick (the tail, tooltips, third-party calls) see the fresh totals; the NEXT tick's boundary read sees the previous tick's published totals, which is exactly the vanilla-OBSERVE-equivalent view the snapshot's per-net sums are built from, and those sums do not feed the allocator's own model.

### 8.0.5 MP-safe ordering: integer-only keys

Every ordering decision in the allocator uses integer keys only. Floats are excluded because IEEE 754 sums are order-sensitive and bit-exact identity across peers is fragile; a float on a sort decision would be the first float dependence in the ordering path. The keys:

- **Topological order**: `ReferenceId ASC` tiebreak on ready-node pops (§8.0.2).
- **Supplier dispatch order** (`SupplierOrder`): `(priority DESC, ReferenceId ASC)`. Higher-priority contributors dispatch first.
- **Deprioritization victim selection** (`SelectDeprioritizationVictims`, §8.3): tier-major best-fit-decreasing over `(priority ASC, claim DESC quantised to whole Watts, ReferenceId ASC)`. Tiers go priority ASC and a tier is exhausted before the next is touched; within the tier, the smallest quantised claim that covers the remaining deficit alone is deprioritized and ends selection (tie: lowest `ReferenceId`), else the largest claim is deprioritized (tie: lowest `ReferenceId`) and the deficit shrinks. Claims floor to whole Watts via `(int)Math.Floor`; the deficit rounds up past the Eps tolerance via `(int)Math.Ceiling(deficit - Eps)`, so the selected set always restores claims under budget in float terms (never an under-selection that would trip the §8.4 elastic hit-max) at the cost of at most one extra small victim on sub-Watt boundaries. Every comparison is integer / `ReferenceId` only, and the selector is a pure static function of (candidates, deficit), pinned by ScenarioRunner's `pgp-deprioritization-victim-fixture` synthetic cases (§8.3.3). It replaced the flat `(priority ASC, ReferenceId ASC)` walk, whose over-deprioritization cost §8.3.3 documents.

Quantising the claim to whole Watts erases any ulp-level float divergence between peers (Watts are O(10^3-10^5), well clear of float precision boundaries); the cast is at the comparator only, the underlying float drives physics. The within-tier proportional demand split (§8.3.2) is float, but is bit-identical across peers on the shared mono runtime, so it never makes a divergent ordering decision. `MergeDeterminismPatches` enforces deterministic network-id ordering on cable merges, so the keys stay stable across peers.

The same `ReferenceId ASC` enumeration (`SegmentingDeviceRegistry.EnumerateSorted`) is used everywhere PGP walks segmenting devices: the PROTECT-phase cycle DFS (§4.2.5), the PROTECT-phase producer-isolation walk (§8.5), and the allocator GATHER. One sort key across the whole tick; the gain is MP determinism with no float dependence anywhere in the ordering.

### 8.0.6 Exact-balance delivery and tick-scoped read coherence

Vanilla `PowerTick.CacheState` set `_isPowerMet = (Potential - Required) > 0f` (STRICT `>`), so a network whose `Potential` exactly equalled its `Required` read as NOT powered and its rigid loads went dark; since the allocator grants exact throughput with no headroom, a fully served network lands at `Potential == Required` and that strict test was a standing threat. It no longer exists to nudge: `CacheState` never runs, no `_isPowerMet` or `_powerRatio` is computed anywhere, and the boundary is native to the mod's own math. A net is served when the allocator's `Unmet <= Eps` (0.01 W), the liveness verdict and the Powered sweeps derive from that same expression, and the write-back publishes the exact granted fields, so exact balance IS the healthy steady state by construction. (The retired mechanism was an unconditional `CacheState` postfix that forced `_isPowerMet = true` and ratio 1 within Eps of balance; it is deleted with the trio.)

**Tick-scoped read coherence.** Every decision in the tick must see the same device outputs, or a mid-tick step tears the solve (the 2026-07-07 transition-dip finding). The single boundary read closes this class by construction: each device's demand and output is sampled exactly ONCE per tick, into the snapshot, and every consumer (PROTECT, GATHER, the write-back, the audits) reads the row instead of re-sampling, so a main-thread mutation between phases (solar `GenerationEfficiency` stepping per FixedUpdate frame, `WindStrength` rewritten every frame) lands in the NEXT tick's snapshot instead of tearing the current one. The solar and wind-turbine first-read latch patches, and the fourteen consumer demand latches plus the producer-connector latch of decision §0.21, are all retired: there is no repeat read left to latch. One queue survives from that era:

- **Emergency-light toggle queue** (`EmergencyLightToggleQueue`, drained by the atomic tick prefix in HOUSEKEEPING): the emergency-light decisions used to fire `OnServer.Interact` mid-tick, adding or removing lamp + cell-charge demand between phases. Decisions are queued (one pending state per light, last wins) and issued at the next tick boundary, making the flips tick-atomic. This is control-state coherence, not a read latch, which is why it outlives the latches.

### 8.0.7 Why this design (the two defects it closes)

Two concrete defects motivate the topological-order, re-decidable, exact-throughput design.

1. **One-tick supply-propagation lag (the multi-stage-chain oscillation).** On a multi-stage transformer chain under variable load, a min-depth order plus capacity-headroom advertising plus the lagging `_powerProvided` input draw let a downstream network's demand and its upstream supply drift one tick out of phase, so the chain flickers power on and off. The topological order (supply finalized before each network is read), exact throughput, the fresh per-class input-draw caches, and the `>=` boundary together close the loop within one tick: input equals output on every pass-through class, and a fully served chain stays powered at exact balance.

2. **The deprioritization <-> overload 2-cycle ("60-second freeze").** A grow-only-everything loop can commit a deprioritization AND an overload that are individually justified but jointly contradictory: device A is deprioritized because its input looks short, which relieves device B's network so B's overload should clear, but if the overload were committed and never retracted within the tick, the relief that would have made A's deprioritization unnecessary never registers, and both devices lock out for 60 seconds. The re-decidable deprioritization (cleared and recomputed every round) plus the structural-overload-before-deprioritization ordering settle A's deprioritization and B's relief in the same round, so the contradictory pair is never committed. (Overload itself stays grow-only / sticky, §8.0.1: a transformer that structurally cannot serve its downstream is a real fault and is meant to commit; only an unnecessary DEPRIORITIZED is retracted.)

### 8.5 Producer-isolation rule and CURRENT_MISMATCH_FAULT (strict-literal)

PowerGridPlus enforces a strict-literal rule: a producer's network may contain ONLY other producers and `Transformer` instances. Any other device on a producer's network is a violation. Explicitly: `Battery`, `AreaPowerControl`, `PowerTransmitter`, `PowerReceiver`, `RocketPowerUmbilicalMale`, and `RocketPowerUmbilicalFemale` do NOT satisfy producer-isolation, even though they are segmenting devices. Only `Transformer` does. Rigid consumers (lights, machines, IC10, etc.) and the other six segmenting classes are all violators.

What counts:
- **Producer**: SolarPanel (and Large / HeavyDouble variants), WindTurbineGenerator (and Large), PowerGeneratorPipe / GasFuelGenerator, PowerGeneratorSlot / SolidFuelGenerator (coal genny), StirlingEngine, RadioscopicThermalGenerator (RTG), TurbineGenerator (the small wall turbine), and PowerConnector (the portable-generator dock). Identified at runtime via `GetGeneratedPower(net) > 0` while the device is NEITHER an `ElectricalInputOutput` NOR a `WirelessPower`, OR by class-list lookup. (TurbineGenerator and PowerConnector were a §8.5 omission, added in deviation P11: both override `GetGeneratedPower`, so leaving them off would drop them to the unknown-producer cable-burn fallback per D6.) Note: `PowerConnection` is a DIFFERENT, vestigial dead-code class (§5.0.1) and is never a producer.
- **Transformer**: only `Transformer` instances (NOT Battery, NOT APC, NOT PT/PR, NOT RocketPowerUmbilical).
- **Violator**: any device on the producer's network that is neither a producer nor a `Transformer`. This includes rigid consumers AND every non-Transformer segmenting device class (Battery, AreaPowerControl, PowerTransmitter, PowerReceiver, RocketPowerUmbilicalMale, RocketPowerUmbilicalFemale).

**Active-source gate.** A producer is faulted only while it is an ACTIVE source (the form that actually feeds unregulated voltage), decided by `ProducerClassifier.IsActiveProducer`. A producer with an on/off button (gas / solid / stirling) counts while it is ON; a `PowerConnector` counts while its docked generator is delivering (the dock is a transparent proxy, it forwards the generator's power and has no source of its own, so an empty or switched-off dock is inert and never faults); a buttonless producer (solar / wind / wall turbine / RTG) always counts, since it cannot be switched off and produces whenever the environment allows. The connector reads the docked generator's raw `PowerGenerated`, not the connector's `GetGeneratedPower` (the current-mismatch enforcement postfix zeroes that, so gating on it would oscillate). This is the current-mismatch analog of the elastic-overload ON cohort (§8.4.1); an inactive producer is neither faulted nor counted toward the violation, so a producer that is off / not delivering is transparent to the rule exactly like a `Transformer` is.

Per-tick check, in the PROTECT phase right after the cycle-fault detector, over the grid snapshot's rows (the classification flags were computed once at the boundary read):

```
for each net row N in the grid snapshot:
    active_producers_on_N = [d for d in N.Rows if IsActiveProducer(d)]            # the cohort (active-source gate)
    transformers_on_N     = [d for d in N.Rows if d is Transformer]
    violators_on_N        = [d for d in N.Rows if not IsProducer(d) and not (d is Transformer)]
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

**Producer-only network (no transformer, no consumers).** A cable network that contains only producers (e.g. Solar wired to Solar with nothing else attached) is a valid harmless idle configuration. No producer-isolation violation fires; no current-mismatch fault; no hover warning; no cable burn. The producers run, generate their `GetGeneratedPower` value, and the power has nowhere to go (so vanilla `Required = 0` and the surplus dissipates), but that is harmless. The producer-isolation walk fires only when `producers_on_N AND violators_on_N` is non-empty; a producer-only or producer-empty network never crosses that threshold.

**Faulted transformer counts.** The producer-isolation walk treats every `Transformer` instance as a `Transformer` regardless of its current fault state. A Solar wired to a `Transformer` T1 that is currently in CYCLE_FAULT (or OVERLOAD, or DEPRIORITIZED) still satisfies the rule; the network has a producer and a Transformer, no violators, no current-mismatch fault. T1's fault is its own concern: its `GetGeneratedPower` is 0 for the lockout window, so the producer's power has nowhere to flow until T1 recovers, but the player is not double-punished with a current-mismatch fault on top of T1's existing red flash. The player traces the dead downstream back to T1, reads T1's hover ("Cycle fault" / "Transformer overloaded fault"), and fixes that. No special diagnostic on the producer.

Action choice (per producer):

- **Flash producers** (have on/off button): GasFuelGenerator, PowerGeneratorPipe, SolidFuelGenerator (coal), StirlingEngine. Get `ProducerFaultFlashBehaviour` attached (red flash, countdown hover on the button).
- **Hover-only producers** (no on/off button, but the player naturally hovers the device body to investigate why it is not producing): SolarPanel, WindTurbineGenerator, RadioscopicThermalGenerator (RTG). Get a `Thing.GetPassiveTooltip` postfix that appends the fault line + countdown to `__result.Extended`. No flash visual (nothing to flash); the player reads the fault from the hover.
- **Cable-burn producers** (no on/off button AND no useful hover): currently EMPTY after the hover-only path. The cable-burn fallback exists only for future producer classes that have neither flash nor a useful hover surface; none of the current vanilla producer set falls here. The existing `BurnReasonRegistry` + persistence (§11.6) remain ready if such a class appears.

When a producer enters CURRENT_MISMATCH_FAULT (flash or hover) it contributes 0 to its network for the lockout: the tick it is newly locked, PROTECT zeroes its row in the grid snapshot (`ZeroFaultedProducers`, so the fault takes effect the same tick with no second observation pass), and for every later tick of the window the `GetGeneratedPower` enforcement postfix (`ProducerFaultEnforcementPatches`, the surviving fault-enforcement shim) returns 0 at the boundary read and on every other surface while the registry has the device locked.

**Network-level commit (mirrors the elastic-overload retry, §8.4.1).** CURRENT_MISMATCH_FAULT is stored per-producer (`CurrentMismatchFaultRegistry`; each producer flashes / hovers / snapshots independently) but committed at the NETWORK level with a RETRY before any reset. A net is a commit candidate when an active producer NEWLY violates this tick, or a toggle requested a retry (`OffAsResetSweep` flags the net via `CurrentMismatchFaultDetector.RequestRetry` when it clears a producer's lock, the OFF-as-reset edge). On a candidate net:

- **Recover.** If it no longer violates (the foreign devices are gone, or there is no active producer left; adding a transformer does NOT recover a net, since a transformer exempts nothing), the whole producer cohort's locks are CLEARED.
- **Reset.** If it still violates, every active producer is stamped to ONE shared fresh expiry (`currentTick + LockoutDurationTicks`), which arms the 60 s lockout and keeps the cohort phase-synced.

A stable all-locked violating net is NOT a candidate, so its synced timer counts down rather than being re-stamped (this is what prevents a frozen countdown on a buttonless producer). The retry makes toggling any buttoned producer a network-level recovery attempt for every producer sharing its network: a buttonless solar / wind on the same net clears together with the buttoned generator the player toggled, and the cohort either resolves (wiring now fixed) or re-faults on a fresh synced timer ("clear, then resolve or immediately fault again"). Like overload there is no free auto-recovery mid-lockout: a fix with no interaction clears at the 60 s expiry; a toggle (the §10.3 OFF-as-reset) is the instant retry. The commit is self-resolving (after it the net is either all-recovered or all-locked, so neither branch re-triggers next tick).

The hover-only mechanism is verified viable:
- SolarPanel's vanilla `GetPassiveTooltip` (decompile L400076-L400089) already writes `passiveTooltip.State = SolarInfo()` showing generation rate, efficiency, and health. Appending the fault line to `__result.Extended` stacks naturally below.
- WindTurbineGenerator and RTG have no vanilla `GetPassiveTooltip` override (blank tooltip from `Thing.GetPassiveTooltip` base at L300658). The fault line is the only content the player sees, which is acceptable: the player who hovers wind / RTG investigating "why is this producing nothing" gets a direct, immediate answer.
- `WorldCursor.Idle()` (L223086-L223198) re-polls `GetPassiveTooltip(hitCollider)` every frame while the player hovers, so the `{n}s` countdown updates smoothly (typically 60+ FPS).

### 8.6 Why this replaces the vanilla partial-power fallback

Without the producer-isolation rule, vanilla scaled rigid devices proportionally when supply was short on a network with no upstream transformer to deprioritize (the "irreducible undersupply" case). With the rule, that configuration is itself a fault the player must address, eliminating the silent partial delivery. Trade-offs:

- Stricter: a player who built a 800 W solar panel directly feeding a 1500 W light cluster used to get dim lights; now they get a faulted solar panel (or burned cable) and a clear instruction to add a transformer.
- More structural: encourages the canonical hub-and-spoke power layout (producers -> heavy backbone -> transformers -> normal load networks).
- No silent partial delivery: every undersupply scenario produces a visible fault somewhere.

The remaining undersupply case, a legal configuration (producer connects only to segmenting devices, no direct rigid load) whose segmenting device's input network is still undersupplied, is handled by the upstream deprioritization cascade (§8.0) and, where nothing can fund the demand, by the per-net DEAD verdict darkening the subnet whole (decision §0.19). No proportional scaling exists anywhere in the pipeline (§8.0.0.2).

### 8.7 LogicType.CurrentMismatchFault = 6582

Read-only int LogicType, added by PowerGridPlus, next free slot after `CycleFault = 6581`. Reads 1 while the device is in CURRENT_MISMATCH_FAULT lockout, 0 otherwise. Server-derived, replicated to clients via the per-tick fault snapshot (`FaultRegistrySnapshotMessage` KindCurrentMismatch). Exposed on every classifier producer that has a DEVICE-SPECIFIC logic surface: solar, both wind turbines, the small turbine, and the gas / solid-fuel / stirling generators (wind / turbine expose `PowerGeneration`, solar its tracking logic, so each is logic-readable). NOT exposed on `PowerConnector` (a dynamic-generator dock) or `RadioscopicThermalGenerator` (RTG, a rocket-internal component): neither declares a logic surface, so the read would be unreachable (see `Research/GameSystems/PowerProducerLogicReadability.md`). Both can still ENTER CURRENT_MISMATCH_FAULT (detection); it is hover-only / zeroed-output on them. The exposure resolves each producer's ACTUAL runtime `CanLogicRead` / `GetLogicValue` via `AccessTools`, so it follows a future override (e.g. on GasFuelGenerator) instead of breaking silently; an instance filter keeps the read off non-producers (and off PowerConnector / RTG) when a resolved target is a shared base method (deviation P10).

### 8.1 Goal

Decide which contributors (transformers, PT/PR pairs, APCs) enter deprioritization or overload lockout this tick so that every surviving contributor's input network can satisfy its rigid demand AND every surviving contributor's output network demand is within its `Setting`. Minimise the count of subnets that go dark; satisfy higher-priority sibling subnets first; if siblings tie on priority, deprioritize the one with the larger presented pull first.

### 8.2 Per-network inputs the deprioritization decision reads

The deprioritization decision reads the GATHER numbers (§8.0.0.1) and the converged backward-pass demand:

1. **Rigid demand** per network: the sum of the snapshot's boundary-read demand for rigid (non-segmenter) devices; soft-demand stores are excluded (their charge requests ride the soft class of the same sweep, §9), and an APC's input draw is split structurally so only the passthrough portion is rigid (§7.5). Stored as `Net.RigidDemand`.
2. **Generator supply** per network: the sum of direct generators' `GetGeneratedPower` (`Net.GenSupply`). Elastic suppliers are tracked separately (`AvailableElastic`, §7.3) and added as the documented last resort.
3. **Presented pull** per contributor: its `DesiredPull` from the backward desire pass (§8.0.3), the input-side draw it would need to serve its share of its output network's demand. This is the claim the deprioritization decision weighs against the input network's budget.

There is no precomputed subtree-demand recursion; the backward desire pass over the topological order resolves each contributor's pull with downstream demand already folded in.

### 8.3 The deprioritization decision (per input network)

Deprioritization is decided in `ForwardSupplyAndDeprioritize` (§8.0.3), per input network, and re-decided every round (§8.0.1). For a network whose budget the active consumers' claims exceed:

1. The budget the network can pass to its consumers is `avail - RigidDemand` (floored at 0), where `avail = GenSupply + active suppliers' Throughput + AvailableElastic`.
2. If the sum of active consumers' claims (`DesiredPull`) exceeds the budget, `SelectDeprioritizationVictims` picks the victim set for the whole deficit in one pass (tier-major best-fit-decreasing, §8.0.5): lowest priority tier first, and within a tier the smallest quantised claim covering the remaining deficit alone (else the largest claim, repeatedly). Every victim is deprioritized whole (never partial).
3. Step-up contributors are never victims (§5.2); if only step-up / non-deprioritizeddable consumers remain, the budget is accepted as-is.
4. A network with NO supply at all (`avail <= Eps`) deprioritizes nothing: its contributors idle (the dead-input carveout, §8.3.1), so a permanently-unsupplied input does not cycle 60-second lockouts.

Per-input-network scope (§5.4): claims compete only among siblings on the same input network. A high-priority contributor on one network is never weighed against an equal-priority contributor on a different network.

A deprioritization committed at the shared tail (§8.0.4) writes the contributor's RefId into `DeprioritizedRegistry` with `_lockoutUntilTick = currentTick + 120` (60 seconds at 2 Hz). Within the tick the deprioritization is provisional and can be retracted (§8.0.1); the commit happens once, after the loop converges.

### 8.3.1 Dead-input carveout and the no-upstream-supply cue

The deprioritization decision (§8.3) deprioritizes the lowest-priority consumers when an input network's supply is short. One case is carved out: an input network with NO effective supply at all (`GenSupply + InflowCommitted + AvailableElastic <= 0`, the same `avail` the forward pass computes). Deprioritizing such a network's consumers would lock them out for 60 s and re-fire every expiry forever, since the condition is permanent until the input is powered. Instead, a contributor (transformer / APC / PT pair) on a dead input simply IDLES: it is not deprioritized, takes no lockout, does not flash, and recovers the instant its input is powered (no lockout to wait out). This is a deliberate carveout (POWER_DEVIATIONS P7).

So the player still gets a signal, a dead-input contributor that is actively trying to pass power downstream (its modelled `Throughput > 0`) shows the steady, neutral-grey info block `No upstream supply` over `The input network carries no power`, with no countdown. It is an INFO cue, not a fault: lowest hover precedence (any real fault on the same device wins), it never drives the flash, and it carries no 60 s timer. The set is recomputed every tick from the converged allocator state (`DeadInputRegistry`) and mirrored to clients via the per-tick fault snapshot (`KindDeadInput`), so the cue shows identically on every peer. Batteries and rocket umbilicals are pure suppliers, not pull-through consumers, so they do not receive the cue.

### 8.3.2 Parallel-supplier demand split (priority-tiered, proportional within a tier)

The deprioritization victim order (§8.3) and the §8.4 overload trigger settle which suppliers drop, but not how a MET demand divides across parallel suppliers (transformers / APCs / PT pairs) feeding one output network. The backward desire pass (§8.0.3) splits it greedy by PRIORITY TIER, proportional by capacity WITHIN a tier:

- **Across tiers (greedy by priority).** Higher-priority suppliers fill before lower ones. A high-priority bank carries the load to its combined cap; lower-priority banks engage only once it is maxed. This makes priority a primary/backup control, consistent with its deprioritization meaning (high priority delivers first and is deprioritized last).
- **Within a tier (proportional by EffCap).** Suppliers at the SAME priority each deliver `tierGive * EffCap_i / sum(EffCap)`, so equal-priority suppliers share the tier's load in proportion to capacity (a 50 kW and a 5 kW transformer at equal priority split ~10:1, not 50/50). The split is self-bounding (each share <= its own EffCap) and conserves the tier total.

Interaction with §8.4: a tier delivering its full combined cap has every member at `EffCap` (all overload-eligible if demand is still unmet); a tier delivering less than its cap has every member at the same sub-cap fraction (none overload). So within a tier, overload is all-or-nothing, and across tiers the fully-engaged higher tiers are the ones that can trip. Determinism: the tier ORDER is integer-keyed (priority DESC, RefId ASC, §8.0.5); the within-tier proportional division is float but bit-identical across peers on the shared mono runtime, so the §8.4 discriminator probes still behave identically on every peer.

The SAME splitter carries the soft class (§9): a network's soft desire divides over the same priority tiers with the same proportional rule, except each supplier's within-tier weight is its residual headroom `EffCap - DesiredThroughput` instead of `EffCap`, so soft never displaces rigid and a rigid-saturated supplier takes no charge flow. Because soft rides the same priority tiers, a high-priority contributor's downstream storage charges before a low-priority one's; this is a deliberate behaviour change from the deleted priority-blind surplus pass (§9).

### 8.4 Overload: per-transformer hit-max trigger

An overload trips a transformer that is delivering at its active `Setting` cap while the output network still has unmet rigid demand. This is the DEVICE_OVERLOADED fault kind (decision §0.29): the hover title is `Transformer overloaded fault` on a transformer and `Link overloaded fault` on the wireless pair, over the `Drawing` diagnostics line, and the entry's hover payload is the rule's own locals (the net's rigid desire against the suppliers' combined deliverable cap). The trigger is per-transformer, not per-network. Note: a player who reduced Setting below OutputMaximum via IC10 will see overload fire at the lower threshold; this is intentional (they explicitly asked the transformer to throttle below its rated max).

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

**Input-limited pairs (historical deviation P6, superseded by the static rating).** An earlier build sized the pair from the live deliverable (`min(rated cap, InputNetwork.PotentialLoad)`) and needed an input-limited carve-out (`Seg.InputLimited`) that routed source-bottleneck shortfalls to DEPRIORITIZED instead of OVERLOAD. The 2026-07-02 rearchitecture replaced the live read with the static link rating (§6.3), so the pair's cap no longer tracks the source potential and the carve-out is gone (`InputLimited` no longer exists). An undersupplied source now surfaces through the normal channels: the forward sweep deprioritizes the pair when its input network cannot fund its `delivered * m` pull (§8.3), or idles it without a lockout on a dead input (§8.3.1). The structural-overload rule treats the pair as Setting-limited (its `CapSetting <= CableCap` by construction, §6.3), so only downstream rigid demand genuinely exceeding the rating-bounded `EffCap` trips OVERLOAD; input-limited pairs caught by that rule are taken offline with it. Either way the pair goes offline whole (no-partial-power); the diagnosis matches the actual bottleneck.

The `OverloadRegistry` is keyed by `ReferenceId` (per-device, consistent with `DeprioritizedRegistry`). No NetworkId-keyed variant is needed; the elastic re-arm sync in §8.4.1 is commit-time phase alignment of per-device entries, not a separate network-keyed store.

### 8.4.1 Elastic supplier overload: network-level retry and synced re-arm

§8.4's hit-max trigger is per-transformer. The same capacity-OVERLOAD outcome extends to elastic suppliers (Battery, AreaPowerControl, RocketPowerUmbilical*) so storage-fed networks honour the no-partial-power invariant: when a network's COMBINED effective discharge from all its elastic suppliers (Σ `min(rate cap, cable cap, stored)`) still leaves rigid demand unmet, every elastic supplier on that network trips OVERLOAD (the elastic hit-max rule of `DetectSupplyOverload`, §8.0.3), contributes 0, and the subnet goes dark cleanly instead of partial-powering. The storage elastics title the fault with their own label (`Battery / APC / Umbilical overloaded fault`, decision §0.30) over the `Drawing` diagnostics line; the CABLE-bound case (the store's flow exceeding the weakest cable's rating) is the separate CABLE_OVERLOADED kind, committed per device into `CableOverloadRegistry` outside the retry below, which also makes a cable-overflow-tripped battery take a real visible 60 s lockout where it previously produced zero silently. Two batteries that individually fall short but together cover the load do NOT trip: the forward sweep sums their discharge (`AvailableElastic`) before the `Unmet` test, and only a genuinely unmeetable load trips them.

Unlike the per-transformer trigger, the elastic OVERLOAD is a NETWORK property ("this network's storage cannot meet its load"). `OverloadRegistry` stays per-device (keyed by `ReferenceId`; each device flashes / hovers / snapshots independently), but the commit is network-level with a RETRY before any reset. A net is a commit candidate when at least one of its elastics newly overloads this tick. Its overload cohort is every elastic on the net overloaded this tick or already overload-locked (all ON, since the §10.3 OFF-as-reset sweep clears the lockouts of switched-off devices every tick). The commit then:

- **Retry.** If the cohort's combined effective discharge would cover the net's residual demand, the situation has recovered (the load dropped, a device was toggled back on, or supply was added), so the cohort's overload locks are CLEARED and they rejoin next tick. No timer is reset.
- **Reset.** Only if the retry still leaves demand unmet are the cohort's per-device locks (re)stamped to ONE shared fresh expiry (`currentTick + LockoutDurationTicks`), which both arms the 60 s lockout and keeps the cohort phase-synced.

The shared expiry matters because the per-device timers can otherwise drift out of phase (one toggled off then on, or one locked first by an unrelated fault), after which the suppliers re-arm one at a time, each re-overloading alone because its siblings are still locked, leaving a network they could JOINTLY power dark forever. Syncing guarantees an individually-too-weak-but-jointly-sufficient bank always re-arms together. The retry makes a player toggle (or any supply change) an immediate network-level recovery attempt rather than a blanket timer reset: toggling one device on retries the whole on-cohort at once and recovers the net the same tick if they jointly suffice, and only eats a fresh 60 s if they genuinely cannot. The commit is self-resolving (after it the net is either all-recovered or all-locked, so neither branch re-triggers next tick); devices locked for CYCLE_FAULT / DEPRIORITIZED are not in the overload cohort; a split-pending network defers its commit (Option C). Transformers keep their per-device §8.4 timer (the culprit transformer is genuinely device-specific; this is a network property).

### 8.4.2 PT/PR pair input-side draw (distance overhead)

The §8.4 overload check is an OUTPUT-side test (the pair delivering its full deliverable with downstream still unmet). The pair's INPUT side needs separate accounting because the deliverable and the source draw are not equal under PowerTransmitterPlus: the source network is billed `delivered * m`, where `m = 1 + k * distance_km` is PowerTransmitterPlus's source-draw multiplier (§6.3; `m = 1` under vanilla or with PowerTransmitterPlus absent, where delivered == drawn).

The allocator therefore models a PT/PR pair's demand on its INPUT network as `throughput * m + quiescent`, not `throughput + quiescent`, and bounds the input cable at `input_cable_cap / m`. It reads `m` from PowerTransmitterPlus by reflection (`PowerTransmitterPlusInterop.SourceDrawMultiplier`, a soft dependency returning 1 when PowerTransmitterPlus is not loaded). Without this, a long link feeding off a constrained source network is under-counted by a factor of `m`: the allocator believes the input is covered, deprioritizes nothing, and grants the source network more outflow than its real supply funds, an over-commitment that starves the net's other consumers and violates the conservation identity the checker enforces (§8.8), the exact whole-or-nothing breach the design exists to prevent. With it, the source network's true `delivered * m` draw enters the deprioritization decision, so an unaffordable link is deprioritized cleanly (its input subnet goes dark, not partial) and the input cable is protected against the inflated current.

This is the PowerGridPlus-side half; PowerTransmitterPlus charging the source for distance is by design. The static link rating (the §8.4 threshold) is unchanged by this section: downstream rigid demand exceeding the pair's rating-bounded cap is the OVERLOAD trigger, while a source that cannot fund the pair's `delivered * m` pull deprioritizes it through the normal §8.3 budget math (the former live-cap input-limited carve-out, deviation P6, is superseded; see §8.4).

### 8.3.3 Deprioritization victim selection example (best-fit-decreasing)

Three sheddable siblings T_a, T_b, T_c on the same input network, all priority 100, ReferenceIds ascending in that order. Their presented pulls (`DesiredPull`, rounded to whole Watts) are 500, 1000, 2000 respectively. The input budget the network can pass is 2500 W, so the deficit is 1000 W.

`SelectDeprioritizationVictims` (tier-major best-fit-decreasing, §8.0.5) looks for the smallest single claim that covers the 1000 W deficit: T_b (1000) covers exactly, so T_b is deprioritized and selection ends. T_a and T_c survive; one device is deprioritized where the earlier flat (priority ASC, ReferenceId ASC) walk deprioritized T_a (500) and then T_b (1000), two victims where one sufficed.

When no single claim covers the deficit, the largest claim is deprioritized first and the rule re-applies to the remainder within the tier (claims 500 and 700 against a 1000 W deficit deprioritize 700 then 500); only when a tier is exhausted with deficit remaining does selection move to the next tier. The policy and both ReferenceId tie rules are pinned by ScenarioRunner's `pgp-deprioritization-victim-fixture` synthetic cases, which drive the pure selector directly with the exact numbers above.

### 8.8 Self-diagnostics: conservation check, shortfall classification, unknown-bridge census, delivery audits, and the auditor round

The diagnostic surfaces below watch the allocator and its settlement seams. None of them feeds back into any decision: allocation math, ordering, and cache contents are untouched by all of them. (The partial-power sentinel that used to head this roster is deleted: it monitored the vanilla `_powerRatio` path, and that path no longer exists, see §8.0.0.2 and the retirement note below.)

**Conservation check** (`ConservationChecker.cs`, always-on, no config entry; decision §0.27). Audits the converged grants at the end of ALLOCATE, so a violation is by definition a PowerGridPlus bug, never a player problem. Two invariants:

- **Per network: granted inflow == granted outflow within 0.5 W.** Outflow = rigid demand served + local storage charge granted + rigid pulls granted + soft pulls granted. Inflow = supplier rigid throughput + supplier soft throughput + granted elastic discharge + the generator power the grants imply, derived as the residual `outflow - non-generator inflow` clamped to `[0, GenSupply]` so both failure directions surface: a residual above `GenSupply` means power was granted out of nothing, a negative residual means a contributor was granted throughput nobody consumes (billed upstream, wasted). Unused generator capacity is curtailment, not a violation.
- **Per contributor seg: `TotalPull == TotalThrough * max(m, 1) + quiescent` whenever the seg conducts** (`TotalThrough > 0.01`). A non-conducting seg is checked one-sided (`TotalPull <= quiescent + tolerance`): a grant below the quiescent draw carries zero throughput and may bill any partial amount up to the quiescent. Any drift is a code bug (a double-billed quiescent, a lost distance factor, a class granted on one terminal only).

Warnings are throttled to once per network / per seg per 600 ticks (about 5 minutes at the 2 Hz power tick) and carry a per-component breakdown.

**Shortfall classification** (`ShortfallDiagnostics.cs`). ALLOCATE labels every allocator net's end-of-tick RIGID state and publishes one immutable snapshot per tick by volatile reference swap. The byte values are a cross-assembly contract with the ScenarioRunner census (renumbering breaks its buckets):

- **Served** (0): no unmet rigid demand.
- **Dry** (1): unmet, with every remaining feed genuinely exhausted: each active supplier is saturated or draws from an input net that retained no undelivered supply. Source-side shortage (dead-input chains, unaimed solar islands); honest darkness.
- **Throttled** (2): unmet, with some feed valve deliberately closed: a locked / deprioritized / overloaded supplier, a zero-effective-capacity supplier (a `Setting = 0` "firewall", or rate-limited to zero), or a locked / overloaded elastic on the net. Honest darkness (the player or a fault closed the valve).
- **Deadlock** (3): unmet while the allocator's own accounting says supply existed: an ACTIVE supplier had headroom above 0.5 W AND its input network retained undelivered supply. On a correct allocator this is impossible (an unmet net's suppliers either sit at their caps or drained their inputs); it is the invisible-deadlock regression shape and must be zero on a healthy build (invariant §17.44). Checked BEFORE the throttle rung so a genuine routing failure is never masked by an unrelated closed valve on the same net.

Diagnostics only: nothing in the mod reads it back; the `pgp-rearch-suite` census (§18) joins the snapshot via reflection. A net absent from the snapshot was outside allocator scope this tick.

**Unknown-bridge census** (`UnknownBridgeCensus.cs`). On the first atomic tick after a world load (armed at plugin load, re-armed on every world load), every scene device that is an `ElectricalInputOutput` subclass but neither in `SegmentingDeviceRegistry`'s known set nor described by any adapter (§5.0.3) is reported, one Info line per TYPE. Reporting is the ONLY handling: an unknown bridge keeps its vanilla power methods unpatched and the snapshot's boundary read samples it once per tick as plain rigid demand / generation on each side (the honest conservative fallback, §8.0.0.2). The census makes the gap visible instead of silent when a third-party mod ships its own two-port power device.

**Partial-power sentinel: retired (2026-07-12).** `PartialPowerSentinel.cs` read every network's settled `_powerRatio` at the tick tail and counted Served, ratio-deprivable networks below a 4-ulp sub-unity ceiling: it existed to prove the vanilla partial-power branch stayed unreachable while that branch still existed. With the `PowerTick` trio retired, nothing computes a `_powerRatio` and there is no branch to monitor; the sentinel is deleted along with its `pgp-partial-power-injection` positive control. Its contract survives in stronger form: the write-back publishes exact granted fields, the conservation checker audits the grants, the two delivery audits verify plan-vs-settlement identity per store, and the Powered-set conformance assert plus the ownership auditor catch a healthy device presenting dark. The two structural fixes the sentinel's soaks forced (the allocator-funded APC quiescent, §8.0.3, and the errored-transformer 0 bill) and the tick-scoped read coherence work (§8.0.6) all remain load-bearing.

**Charge-delivery audit** (`ChargeDeliveryAudit.cs`, always-on, no config entry; the same posture). The grant-vs-credit seam detector: the conservation checker audits the allocator's GRANTS and the ledger audit the ledger surfaces, but what actually LANDS in a store (a `PowerStored` credit) deserves its own check, because a settlement-side adjustment can under- or over-credit with every other invariant green (the APC quiescent-subtraction wrinkle that motivated it, back when delivery ran through vanilla `ReceivePower`). Since the redesign the audit verifies PLAN-VS-SETTLEMENT IDENTITY: ALLOCATE publishes a per-tick charge-grant snapshot (refId, granted watts, charge-side net, store kind, straight from the softs roster, with grants on DEAD nets zeroed exactly like every published surface, so the audit expects what the settlement will actually do), and the write-back records each store's credited watts AT THE SETTLEMENT SITE (`Core/WriteBack.ApplyCredit`), argument-derived, never as a `PowerStored` field diff (the store fields are float32, so a 230 MJ nuclear bank quantizes at 16 J per ulp and genuine trickle credits read as multiples of 16). Credit equals grant by construction on the current settlement path; the audit exists to catch a future settlement-path change that silently breaks the identity. The old Priority.First / Priority.Last observation brackets around `ReceivePower` are deleted with the vanilla delivery path they bracketed. The tick tail compares credited against granted per store, gated on the charge-side net classifying Served, with the battery band `[granted / BatteryChargeEfficiency, granted]` (the configured charge cost is legitimate, the credited fraction being the reciprocal of the cost multiplier; the write-back's sub-500 W trickle floor stores the full delivery and stays inside the band). Tolerance 0.5 W, the conservation-checker basis. A store whose charge gate legitimately closed between grant and settlement (the APC cell's Mode-based `IsCharged` fill edge) is marked moot for the tick (`MarkChargeGateClosed`) rather than tolerated numerically, and a store owned by an unbillable contributor is never granted at all (§8.0.3), so the fault-lockout class the audit first caught (store 464386: granted 22 to 1000 W for exactly one 120-tick dawn lockout, credited 0 every tick, every other detector silent) is impossible by construction. Counters are exact and never throttled (violation store-ticks, affected ticks, distinct stores, worst and latest captures with refId / granted / credited / tick); one aggregated warning per 600 ticks while new anomalies arrive; zero anomalies produce zero lines; cleared on world load. Positive control: the ScenarioRunner `pgp-rearch-suite` drives `IsViolation` with six synthetic fixture cases (`RSD P<n>` lines) and judges the live window through its `delivery=` verdict field (invariant §17.46).

**Discharge-delivery audit** (`DischargeDeliveryAudit.cs`, always-on, the same posture; decision §0.18a). The charge audit's second direction: what LEAVES a store must equal the granted elastic share (`rigidShare + softTopUp` since §9.2). ALLOCATE publishes a per-tick discharge-grant snapshot straight from the elastics roster (refId, granted watts, discharge-side net, store kind; zero-share entries included so an ungranted drain on a Served net is caught; grants on DEAD nets zeroed like every published surface), and the write-back records each battery / umbilical drain AT THE SETTLEMENT SITE (`Core/WriteBack.ApplyDebit`), argument-derived; drain equals grant by construction on the current path, and the audit exists to catch a future settlement-path change. The old Priority.First / Priority.Last brackets on `Battery.UsePower` and the umbilical halves' `UsePower` are deleted with the vanilla drain path they bracketed; the umbilical phase-2 crossing mutates `PowerStored` directly and enters neither settlement lane. Charge / discharge same-tick disambiguation is by settlement kind by construction: credits and debits are separate plan lists. The tick tail compares drained against granted per store, gated on the discharge-side net classifying Served (on an unmet net the elastics are overload-tripped to share 0 and the subnet goes dark whole); the band is exact both ways (discharge has no configured loss), tolerance 0.5 W. The APC cell is out of scope by design: the allocator publishes no APC-cell grant to this audit and the write-back drains the cell directly by its cell-only share (the vanilla deferred `min(PowerStored, _powerProvided)` repayment ran inside the retired ApplyState). Counters exact and never throttled (violation ticks, store-ticks, distinct stores, worst / latest captures); one aggregated warning per 600 ticks; zero anomalies produce zero lines; cleared on world load. Positive control: the `pgp-rearch-suite` drives `IsViolation` with four synthetic fixture cases (`RSD D<n>` lines) and folds the live window into its `audits=` verdict field (invariant §17.47).

**Tick-duration watchdog** (`TickDurationWatchdog.cs`, always-on, the same posture; decision §0.18b). Stopwatch spans around the whole atomic tick body and the `RunAtomic` call inside it. A tick counts as a violation only when BOTH conditions hold. (1) The whole tick crosses a derived duration threshold `min(max(8 * rolling median, 50 ms), 400 ms)`: the 8x multiple flags decisively pathological ticks on any save size, the 50 ms floor (10 percent of the 500 ms tick period) keeps sub-millisecond-median scheduling noise silent, and the 400 ms ceiling (80 percent of the period) trips unconditionally even when the median itself has degraded. (2) The allocator is the dominant cause: the `RunAtomic` span's EXCESS over its own 256-tick rolling median accounts for at least half the amount by which the tick overran its threshold (`IsAllocatorAttributable`). Condition 2 is what keeps the auditor honest about the "regression in the mod" claim its warning makes: crossing the total-duration threshold means the tick was slow, not that the power code was the reason, and on a host whose tick median is single-digit milliseconds an autosave serialization stall, a GC pause, or OS scheduling routinely pushes a tick past 8x median while the allocator ran its normal few milliseconds (the gate-14 4-hour soak produced exactly this: 150 ms ticks with the allocator at 4 to 6 ms, previously two false-positive warnings). The allocator carries its own rolling median alongside the tick median so the baseline self-calibrates to save size the same way the threshold does; a genuine allocator blow-up (an unconverged solve, a quadratic diagnostic) still trips even when the environment is also noisy, because the excess is measured against the allocator's own norm rather than the tick's. Both medians come from 256-tick rings recomputed every 64 ticks (ceiling-only during the 64-sample warm-up), both rings update after the comparison so a violating tick never softens the thresholds it was judged against, and the whole path allocates nothing per tick. Exact counters plus high-water captures for both spans; one aggregated warning per 600 ticks; cleared on world load (invariant §17.48). Positive control: the `pgp-rearch-suite` drives `ComputeThresholdMicros` (`TDW T1`-`T3`) and the `IsAllocatorAttributable` gate (`TDW T4`-`T7`: environmental-overrun suppression, allocator blow-up, below-median never fires, allocator-dominates-amid-noise) as pure functions.

**Powered-set conformance** (`PoweredSetConformance.cs`, always-on, the same posture; decision §0.18d). At the tick tail, right after the Powered reconcile, every roster member ALLOCATE published HEALTHY must read `Powered == true`; a device dark while healthy on two consecutive tick tails counts a violation (the one-tick grace absorbs the reconcile's main-thread marshal on a fresh rising edge, §10.6). Exact counters (violation ticks, device-ticks, distinct devices, latest capture); one aggregated warning per 600 ticks; cleared on world load (invariant §17.49).

**Registry hygiene sweep** (`RegistryHygiene.cs`; decision §0.18e). Every 600 ticks, the five fault registries drop entries whose lockout expired (the read path self-cleans, but an entry nobody queries again leaks) and entries whose device no longer exists. Worker-thread safety: `Thing.Find` is a plain managed dictionary lookup (the OffAsResetSweep precedent) and the destroyed verdict is a reference test (`is null`), never the Unity lifetime operator; ConcurrentDictionary enumeration with concurrent removal is safe by contract. Exact counters; one Info line per sweep that removed anything (the cadence is its own throttle). DeadInputRegistry is exempt (rebuilt every tick).

**Save/load self-check** (`SaveLoadSelfCheck.cs`; decision §0.18f). One-shot on the first atomic tick after a load, after the ledger sweep and before this tick's detectors: the five fault registries must be empty (transient-by-spec, §10.5), the priority sidecar's restored count must equal its loaded count, and the `_powerProvided` world-load sweep must have run. One Info line on pass; one warning naming every failed clause on mismatch (invariant §17.50).

**Generator read-coherence** is structural since the redesign: the single boundary read (§8.0.6) samples every generator once per tick, so a mid-tick output step (solar efficiency, wind strength) cannot tear a solve and the old solar / wind first-read latches are deleted. The two delivery audits above remain the tripwire if a coherence hole ever reopens (a mid-tick generator step on a donor or charge-side net would surface as an under-drain or under-credit on a Served net).

### 8.9 Status

The allocator described in §8.0 is the sole power path: there is one allocator, no toggle, and no alternate strategy (decision §0.8). In-game validation on the dedicated server is tracked separately (see `PLAYTEST.md`); this spec does not carry dated dedi results.

## 9. Storage-charge flow (the Soft class)

> Supersedes the former "surplus distribution pass" design of this section (2026-07-02, decision §0.9): the separate priority-blind surplus walk is deleted; storage charge is the SOFT flow class riding the same backward/forward sweep as rigid demand (§8.0.3).

### 9.1 Goal

Charge every storage cell (stationary battery, APC cell, rocket umbilical cell) out of supply that rigid demand does not need, across any number of intermediate contributors, without ever displacing rigid load, tripping a fault, or double-counting a request. There is no second accounting system: soft is the second component of the one (rigid, soft) demand vector the allocator sweeps.

### 9.2 Where charge power comes from

Soft is funded per network by a three-rung ladder, consumed strictly in order (decision §0.17 replaced the former "elastic never funds charging" rule):

1. **The firm residual**: `GenSupply + active suppliers' rigid Throughput - RigidDemand - granted rigid pulls`, floored at 0.
2. **The soft inflow** arriving through the net's active suppliers (charge flow granted upstream, crossing contributors).
3. **The elastic leftover** (the lowest rung): the net's eligible elastic discharge capacity minus what the rigid settlement consumed, `AvailableElastic - max(0, RigidDemand + granted rigid pulls - GenSupply - InflowCommitted)`, floored at 0, excluding Locked / Overloaded elastics. Elastic funds soft ONLY from this per-net leftover after the full rigid settlement; because it sits below the firm rungs in consumption order, it is tapped only for the soft shortfall firm supply could not cover. This is what makes battery-to-battery transfer possible: a donor's unused discharge capacity charges the net's (and, through the soft class, downstream nets') stores, with recipients funded in the existing priority-tier order and no further constraints beyond physics.

The pool is additionally capped by the weakest cable's remaining headroom on the network (`CableMax.WeakestCapOnNetwork - (RigidServed + granted rigid pulls)`), so a charge grant cannot push a network past its tier rating, and each transfer is bounded by `min(donor leftover discharge, recipient charge request, cable / bridge headroom)` with bridge multiplier overhead billed to the donor side as usual. A consequence of the funding rule survives the new rung: a soft grant on a network still implies every rigid pull there was granted whole (a partially granted rigid pull leaves both the firm residual and the elastic leftover at exactly zero, since the grant loop stopped only when the combined avail ran out).

**Cycle-fault dependency (the transfer's safety argument).** A sustained energy loop through the new rung would require a directed cable cycle (store A's output reaching store B's input and B's output reaching A's input), and the PROTECT-phase cycle DFS already locks exactly that topology (§4): battery, APC, and umbilical edges participate in the directed-SCC walk with direction input -> output, so a powered transfer loop is CYCLE_FAULTed and its members conduct 0 before ALLOCATE ever runs on it. Deliberately absent by design ruling: no self-exclusion, no donor / recipient ordering, no hysteresis, no rate caps beyond physics (batteries are directional and players place buffers where they want them; a downstream enabled buffer exists to be filled). Charge-efficiency losses on the recipient remain plain losses for now; converting them to battery heat is an approved future feature.

### 9.3 Request flow upstream (the backward half)

Every storage cell raises a `Request` (§7.6) on its input network, summed per round into the billable local sum (`BillableSoftRequestLocal`). The `BackwardDesirePass` (§8.0.3 pass 1) carries the soft class leaf-to-source in the SAME walk as rigid: `net.SoftDesire = BillableSoftRequestLocal + sum of active consumer segs' SoftDesiredPull`, split over the net's suppliers by the same priority-tier-first, proportional-within-a-tier splitter (§8.3.2), with each contributor capped at its residual headroom `EffCap - DesiredThroughput` (soft never displaces rigid capacity), and `SoftDesiredPull = SoftDesiredThroughput * max(m, 1)` plus the quiescent draw only when the contributor carries no rigid pull (the quiescent is carried exactly once). The proportional split is what kills the deleted surplus walk's double-count by construction: parallel contributors DIVIDE a downstream request instead of each propagating it whole. A locked / deprioritized / overloaded contributor propagates zero soft desire, and a LOCAL store's request counts only while its owner is billable (`SoftOwnerBillable`, §8.0.3): GATHER refuses registry-locked owners outright and the per-round sums drop an owner that is deprioritized or overloads inside the loop, so no grant, no share, and no upstream inflow ever exist for a store whose owner's bill the lockout enforcement zeroes.

### 9.4 Grant flow downstream (the forward half)

`ForwardSupplyAndDeprioritize` (§8.0.3 pass 3) grants soft per network AFTER the rigid grants, out of the §9.2 pool: one ratio `softRatio = min(1, softAvail / softDemand)` scales every local charge request and every active consumer's soft pull on the network, and a consumer's granted soft throughput is further capped at its remaining headroom `EffCap - Throughput`. Because networks are processed in topological order, the soft inflow a network receives through its suppliers was granted when THEIR input networks were processed, so a charge flow crosses any chain of contributors within the single tick, with no propagation lag and no iteration beyond the DECIDE loop it already rides.

### 9.5 Storage, enforcement, and presentation

The final per-device charge share is written to `SoftDemandShareCache[ReferenceId]`; the `Battery.GetUsedPower` postfix, the `AreaPowerControl.GetUsedPower` prefix (charge portion only, §7.5), and the rocket umbilical patches min-clamp the reported charge demand to it, so each store charges exactly its grant. The charge flow crossing each contributor is part of that contributor's published `TotalThrough` / `TotalPull` (§8.0.4), so the flow is PRESENTED: it has a carrier on both terminals of every segment it crosses, and network `PotentialLoad` / `RequiredLoad` mirrors, hover tooltips, and IC10 reads of transformer / wireless throughput include storage-charge flow, not just running machines. (The pre-rearchitecture design granted charge in a side walk that published only APC figures; a granted-but-unpresented charge flow behind a transformer or wireless pair was a permanent fault-free deadlock, chargers idling at 0 W forever.) The write-back then settles each store at exactly its granted share (credit == grant by construction) and publishes net fields that satisfy `Required <= Potential` on every served network, so nothing flickers and no scaling of any kind occurs.

### 9.6 Faults never fire for soft

Every fault decision (deprioritization §8.3, structural overload §8.0.3, elastic supply overload §8.4.1, cable overflow §5.7, and the dead-input cue §8.3.1) evaluates the RIGID component only. Excess soft desire is CLAMPED, never a fault: a 5 kW transformer asked for 50 kW of battery charge passes 5 kW of charge with no overload, no deprioritization, no lockout, and no hover cue; unmet charge simply waits for headroom. Charge never displaces rigid: the backward split caps soft at each contributor's post-rigid headroom (§9.3) and the forward grant funds it from the §9.2 ladder, whose every rung (firm residual, soft inflow, elastic leftover) is post-rigid by construction. Because soft rides the same priority tiers as rigid (§8.3.2), a high-priority contributor's downstream storage charges before a low-priority one's; this is a deliberate behaviour change from the deleted priority-blind surplus walk.

## 10. Protection state machine

### 10.0 Tick-rate constants are literal

All lockout-duration constants in the protection state machine are literal integer tick counts (e.g. `currentTick + 120` for a 60-second lock at 2 Hz). The literal `120` is NOT derived from a `TICK_RATE_HZ` variable, and there is no runtime tick-rate query. Assumes the vanilla electricity tick rate of 2 Hz. If a game update changes the electricity tick rate, every literal in §10.1, §10.2, §4.5, §8.3, §8.5, §11.2 must be reviewed and adjusted manually. The 60 second figure itself is a human constant, not a performance constant: it is sized to player movement and deduction speed (walk to the device, read the flash / hover / IC10 state), so a tick-rate change adjusts the literals to PRESERVE 60 seconds, never to re-tune the duration.

### 10.1 Deprioritization lockout

- Triggered by §8.3 cascade.
- Stored in `DeprioritizedRegistry._lockoutUntilTick[refId] = currentTick + 120` (120 ticks × 0.5 s = 60 seconds).
- `DeprioritizedRegistry.IsLockedOut(refId, tick)` returns true while `tick < until`.
- Auto-clears when timer expires; next allocator pass re-decides.
- Timer-only. Mid-cooldown topology fixes (player rewires upstream, raises a transformer's Setting, adds a generator) do NOT shorten the timer. The full 60 s runs to completion; the player either waits or uses the OFF-as-reset (§10.3) for an instant manual retry.
- No early release, deliberately. The 60 second lockout gives the player time to troubleshoot intermittent issues that would otherwise disappear before being noticed; an early-release mechanism would erase the evidence of transient faults. The timer-only rule above is not a simplification awaiting a fix; it is the point.
- The 60 second duration is player-movement and deduction-speed oriented: long enough to physically walk to the faulting device and read the flash / hover / IC10 state before it clears. It is not a tunable performance constant (§10.0).
- Consequence: the cold-start boot deprioritization observed on a masters-on load is the system working as designed. A deep chain whose upstream supply has not yet propagated is deprioritized instantly (the no-partial-power contract demands whole-device deprioritization on transiently wrong numbers too), and the lockout that follows is the diagnostic affordance, not a defect. No arming window or clean-tick hysteresis is planned; the former "cold-start deprioritization trap" TODO entry is resolved by this ruling.

### 10.2 Overload lockouts (both kinds)

- DEVICE_OVERLOADED (capacity): triggered by §8.4 / §8.4.1 detection, stored in `OverloadRegistry._lockoutUntilTick[refId] = currentTick + 120`.
- CABLE_OVERLOADED (cable overflow): triggered by the §5.7 / §8.0.3 pass-4 cable rule, stored in `CableOverloadRegistry` (decision §0.29), same 120-tick duration, parallel API (`IsLockedOut`, `IsCableOverloaded`, `NoteCableOverload`, `ClearLockout`, `ClearAll`).
- Both registries' entries carry the (valueW, capW) hover payload alongside the expiry (capacity: rigid draw vs combined deliverable cap; cable: flow vs weakest-cable cap), captured at the detection site and mirrored to clients (§13).
- Parallel API to deprioritization on the capacity side: `IsLockedOut`, `IsOverloaded`, `NoteOverload`, `ClearLockout`, `ClearAll`.
- Timer-only, same as deprioritization, for both kinds. Topology fixes during the lockout window (player rewires downstream, splits load across more transformers, replaces the weak cable) do NOT shorten the timer.

### 10.2.1 Timer-only invariant applies to all five faults

CYCLE_FAULT and CURRENT_MISMATCH_FAULT follow the same rule: the 60 s countdown runs to completion regardless of mid-window topology changes. The lockout window is not re-validated against current topology mid-flight; only the post-timeout allocator pass re-checks. Rationale: re-checking topology every tick during the lockout would let the player observe the fault flicker if their fix is marginal (e.g. cycle that briefly opens then re-closes). The timer-only model gives a clean 60 s "this is broken, fix it and either wait or toggle OFF/ON" signal.

The optimisation invariant in §17.39 (skip-while-faulted) is refined: a faulted device contributes 0 to power flow for the entire window (so allocator math naturally excludes it), but cycle DFS WALKS through it as if conducting when checking for NEW cycles formed by mid-window rewires. Only the original-loop re-check skips faulted participants; newly formed loops that route through a faulted device are still detected, with new participants getting fresh 60 s timers and existing fault timers untouched. Producer-isolation behaves the same way: a producer already in CURRENT_MISMATCH_FAULT skips its own topology re-check this tick, but a different producer newly violating because of mid-window rewire is still caught.

### 10.3 OFF-as-reset

When a player toggles a transformer (or PT) OFF during a flash:
- `SwitchOnOffFaultPatches.RefreshColorState_Prefix` detects `OnOff == false` with any fault active.
- Calls `ClearLockout(refId)` on all five registries (`DeprioritizedRegistry`, `OverloadRegistry`, `CableOverloadRegistry`, `CycleFaultRegistry`, `CurrentMismatchFaultRegistry`); the call clears both the host dicts and the local client mirrors.
- Returns true so vanilla updates the button material to off-state.
- The next per-tick `FaultRegistrySnapshotMessage` heartbeat (including the one empty packet on the non-empty-to-empty transition) propagates the cleared state to the other peers.

When the player toggles ON again, the next allocator pass re-evaluates. If conditions still warrant deprioritization/overload, the lockout re-fires instantly.

**Server-side authority: the OFF-as-reset sweep.** The `SwitchOnOffFaultPatches` path above is the client-side visual clear; it runs in the rendering path, which a headless dedicated server never executes. The authoritative clear is `OffAsResetSweep`, run every tick at the top of the PROTECT phase (before the detectors re-evaluate, §2). It walks the devices currently in any of the five lockouts and clears all of a device's lockouts when the player has switched that device OFF.

A device counts as switched-off only when it actually has an on/off control and that control is off: `HasOnOffState && !OnOff`. This distinction matters because a buttonless device (one whose prefab carries no `InteractableType.OnOff` interactable: solar panels, both wind turbines, the wall turbine, the RTG, the bare power-connector dock) reports `OnOff == false` permanently. That is the absence of an on/off concept, not an OFF gesture, so such devices are NOT swept. Sweeping them would clear and immediately re-note their CURRENT_MISMATCH_FAULT every tick, freezing the hover countdown at its full value instead of letting it count down on its own 60 s timer. Which producers carry an OnOff interactable is catalogued in `Research/GameSystems/PowerProducerOnOffState.md`: the three fuel generators (gas / solid / stirling) do, and are reset normally; the other six producers do not.

**Power Connector dock special-case.** The Power Connector is a buttonless dock that forwards a docked portable generator's power, so its own `OnOff` is permanently false while the real source is the docked `DynamicGenerator` (a `DraggableThing`, never itself on the cable network; the connector is the network producer). The sweep treats the connector as reset-eligible exactly when it is NOT delivering, `!ProducerClassifier.ConnectorIsDelivering`: no generator docked, or the docked generator's raw `PowerGenerated` is 0 (off or out of fuel). With a generator delivering and misconfigured the connector's current-mismatch fault counts down normally; the moment the player switches the generator off (or pulls it from the dock) the next tick clears the fault, matching the player's actual OFF gesture. The access (`PowerConnector.ConnectedDynamicGenerator?.PowerGenerated`) is public, no reflection, and the presence check is a plain reference test (the sweep runs on the power-tick worker, so it avoids the Unity `(bool)`/`==null` native operator). Reading the generator's raw `PowerGenerated` rather than the connector's enforcement-zeroed `GetGeneratedPower` keeps the signal stable while the connector is faulted (no oscillation). Clearing the connector's lock here also flags its network for the §8.5 network-level retry, so a buttoned producer toggled on the same net retries the whole cohort.

### 10.4 Visual restoration

`FaultFlashBehaviour` on deprioritization/overload exit:
1. Calls `RestoreBaseline()` to drop the orange material.
2. Calls `ForceVanillaRefresh()` which invokes `SwitchOnOff.RefreshColorState` via reflection. Vanilla picks the correct material from current `(OnOff, Powered, HasPowerState, Error)`. Avoids the green-button-while-off bug.

### 10.5 Save/load behaviour: fault states are transient

All five fault registries (`DeprioritizedRegistry`, `OverloadRegistry`, `CableOverloadRegistry`, `CycleFaultRegistry`, `CurrentMismatchFaultRegistry` for CURRENT_MISMATCH_FAULT) clear on save load and recompute on the first tick after load. None of the `_lockoutUntilTick` countdowns are serialised. Rationale: faults are auto-clearing diagnostics with a 60-second wall-clock semantic; if the underlying topology is still broken, the first post-load tick re-fires the fault, and if the player fixed it offline, the post-load state is correctly clean. Persisting a half-elapsed countdown across an arbitrary-length save gap has no useful meaning.

This is the unified rule across CYCLE_FAULT, CURRENT_MISMATCH_FAULT, CABLE_OVERLOADED, DEVICE_OVERLOADED, and DEPRIORITIZED: every fault timer is in-memory only.

**No grace period on save load.** The first post-load tick runs the full allocator pass. Any violation that is still present in the loaded topology re-fires its fault with the full 60 s `_lockoutUntilTick = currentTick + 120` lockout. There is no shorter "warm-up" timer, no grace tick, and no half-elapsed countdown carried over. A player who saved with a cycle, an overload, or a producer-isolation violation present loads into the same world, sees the full red countdown the moment the first electricity tick runs, and accepts that the save was made with a violation in place. The OFF-as-reset (§10.3) remains available immediately after load if the player wants to retry without waiting.

The `BurnReasonSideCar` (§11.6) is the only fault-related state that DOES persist across save / load. It records per-instance hover reasons on already-broken `CableRuptured` wreckage, which is durable game state, not a transient lockout countdown.

### 10.6 Powered presentation policy (healthy routed segmenters)

Vanilla `PowerTick.ApplyState` derived a device's Powered flag from what it consumed this tick, so a HEALTHY routed segmenter that idles at zero throughput (a charger transformer whose batteries are full, an idle dish pair) billed a fresh pull of 0 and vanilla flipped it to Powered=False: players read "device broken" on a device the allocator is routing normally. That diagnostic trap motivated this policy, and the writer conflict behind it is gone: with ApplyState retired, NOTHING else writes a routed segmenter's Powered flag any more. Policy: a routed segmenter the allocator considers healthy presents `Powered = True` even when idle (`PoweredPresentation.cs`).

**Healthy** (published per tick by the ALLOCATE tail, §8.0.4): enrolled in this tick's contributor roster, carrying no fault (not cycle-faulted, not deprioritized, not overloaded, this tick or from a prior-tick lockout), and either conducting (`TotalThrough > 0`) or idle on an input network that has effective supply and no unmet rigid demand. A wireless pair publishes health under BOTH halves' ReferenceIds so transmitter and receiver present the same verdict. An idle segmenter on a DARK input (a night-time solar feed) is deliberately NOT healthy: the tail un-powers it, matching the dead-input hover cue (§8.3.1). Faulted, dead-input, and switched-off segmenters therefore present dark; an unenrolled segmenter (empty roster: client peer, pre-first-tick) is simply not written.

Mechanics, one writer, both edges: the tick tail (`PoweredPresentation.ReconcileEnforceTail`, §2 step 5) asserts Powered from the health verdict on the anchor AND the wireless partner alike, healthy = true, unhealthy = false, via the vanilla self-marshaling `Device.SetPowerFromThread`. Edges fire only on an actual transition, so a steadily healthy (or steadily dark) segmenter causes zero per-tick traffic, and the rising edge lands with one frame of marshal lag (an idle healthy charger can read Powered=False for the single tick spanning the marshal, e.g. right after world load; the conformance assert's one-tick grace absorbs exactly this, §8.8). The old two-half mechanism (`AllowSetPower` postfixes blocking vanilla's False edge while the tail raised the True edge) is deleted: it existed to fence ApplyState out, and ApplyState never runs.

Server-authoritative by construction: the tick tail runs only inside the host's atomic tick, and `SetPower` routes through the normal interactable replication, so clients mirror the host's flag. On a client peer the published healthy set stays empty and nothing is asserted. Between world load and the first atomic tick the set is also empty, so nothing is written until the first solve publishes a verdict (the safe default).

### 10.7 Ledger adoption (`_powerProvided`)

The private per-class `_powerProvided` ledger on `Transformer` / `AreaPowerControl` / `PowerTransmitter` / `PowerReceiver` is vanilla's deferred billing handshake (`UsePower` charges it output-side, `GetUsedPower` bills it, `ReceivePower` repays it). The game never zeroes it; at 0.2.6403 it is not serialized into saves (no SaveData member carries it, per the 0.2.6403 write-site census), so a nonzero value is residue from an older-version save or an external writer. Since the B + D1 redesign NOTHING writes the ledger in-tick at all: the vanilla `UsePower` / `ReceivePower` traffic that fed it ran inside the retired `ApplyState`, and the mod's billing goes through the fresh caches (§8.0.4). But surfaces still READ the ledger (`LogicType.PowerActual` on the transformer, the receiver's relay debt that drives the wireless drain and beam visuals), and history shows what unowned residue does (under the pre-redesign fresh-pull billing a conducting transmitter drifted `-((m - 1) * TotalThrough + quiescent)` per tick, compounding into a stranded -176,226 W credit on one Luna transmitter, a dormant free-power source the moment vanilla billing ever read it again). PowerGridPlus owns billing for these devices, so it also owns their ledgers (`LedgerAdoption.cs`), both lanes host-only inside the atomic tick:

- **World-load sweep** (`RunSweepIfPending`, §2 step 0): on the first atomic tick after a load (armed at plugin load, re-armed on every world load), zero the ledger on every modelled segmenter, both signs, NaN included, BEFORE the first boundary read can serve a stale value through a vanilla `GetUsedPower` fallback. One Info line per zeroed device plus a summary.
- **Per-tick tail settle** (`SettleEnforceTail`, §2 step 5): after the write-back, each enrolled, settle-eligible segmenter's ledger is SET to its vanilla-equivalent standing value: `Transformer := TotalThrough` (one tick's output drain awaiting billing, which also makes the `LogicType.PowerActual` read equal the documented "current throughput", §12); `PowerTransmitter := 0` (its fresh bill was already paid in full this tick); `PowerReceiver := min(debt, TotalThrough)` (preserves the load-bearing relay cycle, whose lifted `GetUsedPower` drains the transmitter's advertise on the wireless network and drives both halves' beam visuals, shearing only the stuck overshoot above one tick's throughput; negative or non-finite standing values clamp to 0). Writes are skipped inside a 0.01 W tolerance. Negatives therefore never survive a tick; each is counted silently as the free-energy metric. The settle is unconditional for Transformer and the wireless halves (the old `EnableTransformerExploitMitigation` gate is removed with the setting, decision §0.23).
- **APC exempt from the per-tick settle** (swept at load only): under vanilla, `AreaPowerControl.UsePower` drained the internal cell against a POSITIVE ledger one tick after the cell covered an output shortfall, and its `GetUsedPower` takes `Max(ledger, quiescent)` so a negative never discounts a bill; settling it per tick would have handed the cell free energy. With the trio retired the write-back drains the cell directly by its granted cell-only share, the vanilla handshake never runs inside the tick, and the APC ledger is simply left alone between load sweeps.
- **Exact ledger audit** (always-on, no config entry): the settle pins every owned ledger to a known value at the tick tail and nothing legitimate writes `_powerProvided` between settles AT ALL (the vanilla write sites lived inside the retired ApplyState), so ownership violations are detected exactly, in three classes. (1) Tick-boundary identity: the settle records the post-settle value, and at the start of the next atomic tick the field must equal it EXACTLY; a mismatch is a foreign write. (2) Settle-tail identity: at the tail, the pre-settle field must equal the recorded boundary value within 0.01 W. (3) Non-finite backstop: a NaN / Infinity pre-settle value is its own class; the settle repairs it. The old Layer A+ per-mutation bracket wrappers around the six vanilla write sites (Transformer / PowerTransmitter / PowerReceiver x UsePower / ReceivePower) are deleted with those write sites' caller; there is nothing left to bracket. Counts are exact and never throttled; the tick boundary emits ONE aggregated warning per 600 ticks while new anomalies arrive, carrying per-class totals since load and the worst offender. Zero anomalies produce zero lines. There is no corrective clamp beyond the settle itself; the audit only reports. Tracking follows the settle set: the sweep clears it on world load, devices leaving the enrolled set drop out at the settle tail.

Field access: wireless halves route through the PowerTransmitterPlus ModApi debt accessors (`GetTransferDebt` / `SetTransferDebt`) when the ModApi tier resolved (§6.6), else through cached `FieldInfo` on the vanilla field (which exists with or without PowerTransmitterPlus); `Transformer` / `AreaPowerControl` use cached `FieldInfo` directly. The audit reads the injected field directly, which today is bitwise-identical to the ModApi route (`GetTransferDebt` is a plain field read); a future PowerTransmitterPlus build that transformed the value would surface as boundary anomalies. Any external reader of the field sees at most one tick's standing value per ledger.

## 11. Visual feedback

Five distinct failure modes (after the current-mismatch addition in §11.5 and the §0.29 overload split). Colour identifies the family of cause; the hover block identifies the specific cause: a merged state-plus-title line with a live countdown over a diagnostics line (live watts on the two overload kinds and the deprioritized kind; fixed sentences elsewhere). Wording locked by §0.30.

### 11.1 Colour and hover text inventory

| State | Flash | Hex | Title (line 1 of the block) | Diagnostics line(s) |
|---|---|---|---|---|
| DEPRIORITIZED | orange | `#ffa500` band | `Deprioritized fault` | `Needs 12.0 kW while 30.0 kW competes for 20.0 kW upstream` (needs grey, upstream demand red, upstream supply blue) |
| DEVICE_OVERLOADED | red | `#ff2626` | `Transformer overloaded fault` / `Link overloaded fault` / `Battery overloaded fault` / `APC overloaded fault` / `Umbilical overloaded fault` (per hovered device, see below) | `Drawing 150.0 kW of 100.0 kW` (drawn value red, cap blue) |
| CABLE_OVERLOADED | red | `#ff2626` | `Cable overloaded fault` | `Pushing 12.1 kW onto a 5.0 kW wire` (flow red, cap blue) |
| CYCLE_FAULT | red | `#ff2626` | `Cycle fault` | `This device is part of a power loop` |
| CURRENT_MISMATCH_FAULT | red | `#ff2626` | `Current mismatch fault` | violator device names one per line in red, capped at three plus a grey `and N more` line, then `Generator DC cannot feed the AC grid without a transformer` |
| No upstream supply (info) | no flash | grey title | `No upstream supply` | `The input network carries no power` |
| Throttled (info) | no flash | `#d9a441` title | `Throttled` | `Limited to 3.2 kW of 50.0 kW by the IC10 Setting value` (the word `Setting` in vanilla yellow `#FFFF00`), then `The dial sets priority instead of power` |

Every state renders ONE merged block, identical on the casing tooltip and the on/off button's title box. Line 1 is `{On|Off} - {title}: {n}s`: the switch word green `#00FF00` when on / grey `#9aa0a6` when off, the dash grey, the fault title and countdown red `#ff2626`; switchless devices (no `HasOnOffState`) drop the `{On|Off} - ` prefix; the two info states use their grey / amber titles and carry NO countdown. The diagnostics line(s) below are calm-grey `#9aa0a6` prose with the offending value in the fault red `#ff2626` and the capacity value in the informational blue `#008AE6`. Exactly one block per hover: lower-precedence faults are not stacked (§11.5) and the info states are suppressed while any fault is active (the throttle note no longer stacks under a fault). The button's Action box keeps the pure vanilla action word.

`{n}` is the remaining lockout countdown in seconds with two-decimal precision (e.g. `4.32s` or `4,32s` per locale). Recomputed on every `Thing.GetPassiveTooltip` poll (casing) and every `Tooltip.SetValuesForInteractable` rebuild (button title box). Ticks down continuously from 60.00 to 0.00.

The diagnostics line (decision §0.29, wording locked by §0.30) is the calm-grey `#9aa0a6` base with the offending value wrapped in the fault red `#ff2626`, so the number that broke the rule stands out against the calmer capacity figure: `Drawing 150.0 kW of 100.0 kW` (the rigid draw that tripped the capacity rule against the combined deliverable cap) and `Pushing 12.1 kW onto a 5.0 kW wire` (the flow against the weakest cable's rating). The deprioritized block reads its entry's `(needsW, upstreamDemandW, upstreamSupplyW)` payload: `Needs 12.0 kW while 30.0 kW competes for 20.0 kW upstream`. Watt values format as whole watts below 1000 W and as kilowatts with one decimal at or above (`FmtWatts`), locale decimal separator like the countdown. All payloads come from the registry entry, recorded at the detection site and mirrored to clients (§13); a zero-payload entry (none in practice) renders the title line alone.

The generic `Device` in the IC10 name `DeviceOverloadedFault` is substituted by the hovered device's label in the title, resolved by the C# type of the instance the player is pointing at: `Transformer` on `Transformer`, `Link` on either wireless half (a PowerReceiver aliases to its PowerTransmitter for fault state per §6.2, but the hovered instance selects the wording), `Battery` on stationary batteries and subclasses, `APC` on `AreaPowerControl`, `Umbilical` on both umbilical halves. This replaces the former storage `{device clause}` sentence set outright (decision §0.30).

Implementation: `FaultHover.TryGetMergedBlock` is the single renderer for all five faults plus the two info states; the legacy per-line renderer (`TryGetLine`) and its merged-layout gate (`UsesMergedLayout`) are deleted. The hovered instance is threaded in from both hover patches (`FaultHoverPatches` body tooltip, `FaultButtonTooltipPatches` button title box; the Action box is untouched vanilla). `FaultHoverPatches` runs an outermost-only depth guard (prefix + finalizer pair): several targets sit on one inheritance chain whose derived overrides call `base.GetPassiveTooltip`, Harmony detours base calls like virtual dispatch, and without the guard one hover poll appended the fault block at every patched level (the two-or-three-copies defect fixed with §0.29).

The violator names in the current-mismatch block name the devices on the producer's network that triggered the producer-isolation walk. Implementation: the PROTECT-phase producer-isolation walk records the violator class name(s) onto the producer's `CurrentMismatchFaultRegistry` entry as it marks the producer. The hover formatter renders them ONE PER LINE in red, in walk-order, capped at three lines plus a grey `and N more` line, above the fixed advisory `Generator DC cannot feed the AC grid without a transformer`. The class name is the C# class short name (`Battery`, `AreaPowerControl`, `PowerTransmitter`, etc.), not the localized prefab display name, so the message is unambiguous for cross-language players reading reports.

Named CURRENT_MISMATCH_FAULT throughout the spec (decision §0.30): it conveys the WHY (a generator's unregulated DC cannot feed the regulated AC grid; the transformer is the converter) to the player better than the prior producer-fault and voltage-based names.

The four red faults (both overload kinds, the cycle fault, and the current mismatch fault) share the colour because all four indicate a "structural / topology" problem the player must fix by rewiring or resizing. Orange (deprioritized) is reserved for the "your upstream supply is insufficient" case which the player addresses differently (add generators / batteries / reduce load).

Burned cables get their own per-instance hover text via the existing `BurnReasonRegistry` + `BurnReasonPatches` (see §11.6).

Player review and edit: the hover-text templates above are the single source of truth. Adjustments to wording go here; subsequent code reads from this spec.

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

The Stationeers UI re-polls `Thing.GetPassiveTooltip` and rebuilds the button tooltip via `Tooltip.SetValuesForInteractable` every frame through `WorldCursor.Idle()` (60+ FPS), so the centisecond display updates smoothly without any timer subscription.

Decimal separator: locale-driven. The countdown formats as `tickRemaining.ToString("0.00", CultureInfo.CurrentCulture)`. EU locales (nl-NL, de-DE, fr-FR, etc.) render as `4,32s`; US / UK locales render as `4.32s`. The display matches the player's system culture; no override is applied.

When a player toggles OFF (OFF-as-reset, §10.3) the registries clear immediately and the next hover poll shows the device's normal-state text (no fault prefix).

### 11.3 Pulse and renderer machinery

Pulse rate for all five faults: 2 Hz.

Renderer discovery (shared): walks the `InteractableType.OnOff` `Interactable`'s `Animator` GameObject subtree for `MeshRenderer`s. Falls back to name-substring match (`OnOff`, `Button`, `Switch`) if the precise path fails.

Material strategy (shared): per-renderer Material instance (clones the on-state material with `_EmissionMap` cleared so the tint shows through). Original sharedMaterial is restored on exit, then `RefreshColorState` is invoked to ensure correctness against current state.

Implementation note: the existing `FaultFlashBehaviour` already does this work on Transformers. PowerGridPlus generalises it (renames internal `_transformer` to `_device` of type `Thing`) so the same behaviour attaches to every segmenting device class.

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
2. CURRENT_MISMATCH_FAULT
3. CABLE_OVERLOADED
4. DEVICE_OVERLOADED
5. DEPRIORITIZED (lowest)

Only ONE fault appears in hover text per tick: the highest-precedence active fault, as a single fault block with its countdown (every state renders the merged block, the title line over its diagnostics, §11.1). Lower-precedence faults that are also active are NOT shown -- no stacking. The flash uses the highest-precedence fault's colour (CYCLE_FAULT red, CURRENT_MISMATCH_FAULT red, both overload kinds red, DEPRIORITIZED orange `#ffa500`).

IC10 readings (`DeprioritizedFault`, `DeviceOverloadedFault`, `CableOverloadedFault`, `CycleFault`, `CurrentMismatchFault`) all read independently of precedence; a device in two states simultaneously reads 1 on both relevant LogicTypes. Only the hover text and flash collapse to the highest-precedence fault.

### 11.6 Burned-cable hover text (per-instance wreckage reasons)

PowerGridPlus has `BurnReasonRegistry.cs` and `Patches/BurnReasonPatches.cs` that attach a per-instance reason string to each `CableRuptured` wreckage:

- `RegisterPending(cable, reason)` is called before `cable.Break()`.
- A Harmony Postfix on `CableRuptured.OnRegistered` consumes the pending reason via `LocalGrid` lookup and attaches it to the wreckage via `ConditionalWeakTable`.
- A Harmony Postfix is INTENDED to inject `<color=#ffa500>Burned:</color> {reason}` into the hover tooltip on `CableRuptured` instances.

**Bug, fixed in Phase 0.2 of POWERTODO**: the existing Postfix is attached to `Thing.GetPassiveTooltip`, but `Structure : Thing` overrides this method, and `CableRuptured : SmallGrid : Structure` virtual-dispatches the call to `Structure.GetPassiveTooltip`. The Postfix never fires for wreckage hovers in-game. Same virtual-dispatch trap the `FaultButtonTooltipPatches.cs` file header documents explicitly. The fix retargets the Postfix at `Structure.GetPassiveTooltip` and, if a secondary clobber by `Tooltip.SetValuesForInteractable` is observed, adds a parallel re-apply Postfix on that method too. See POWERTODO Phase 0.2 for the concrete patch.

The same trap applies to the planned `ProducerFaultHoverPatches` for SolarPanel / WindTurbineGenerator / RTG: each producer class's `GetPassiveTooltip` must be patched directly (Harmony's class-hierarchy walk handles classes that inherit the method without overriding it), not via the Thing base.

Existing burn-reason strings (already in code):
- `Overloaded -- sustained generator supply (~{avg} W over 10 s) exceeded this cable's rating ({cap} W)` (the write-back's 20-tick generator-overflow burn, §5.7)
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

- `LogicPassthroughMode = 6577`. Writable per-device. 0 = logic-opaque (vanilla), 1 = logic-transparent. Applies to transformers, batteries, APCs, and PT/PR pairs. Logic passthrough behavior is solely configured by this per-device value; while a device's mode was never explicitly set, it is seeded from the per-kind `Passthrough Default` setting (decision §0.27; the per-class server master toggles are removed). It is independent of OnOff state and independent of fault state: a CYCLE_FAULTed Transformer with `LogicPassthroughMode = 1` continues to bridge logic reads between its Input and Output networks for the entire 60 s lockout window. Power flow is 0 during the fault; logic flow is unaffected.
- `Priority = 6578`. Writable per-transformer. Non-negative integer, no upper cap, default 100. Quantises to non-negative int on write. `0` means lowest priority (still allocated if supply remains), NOT a "disabled" sentinel.
- `DeprioritizedFault = 6579`. Read-only. 1 = deprioritization lockout active.
- `DeviceOverloadedFault = 6580`. Read-only. 1 = capacity (DEVICE_OVERLOADED) lockout active. A lockout caused by the cable's rating reports via `CableOverloaded`, not here (decision §0.29).
- `CableOverloadedFault = 6587`. Read-only. 1 = CABLE_OVERLOADED lockout active (the device's cable network cannot carry the demanded flow; the suppliers go offline instead of burning the weakest cable). Wired on Transformer alongside the other fault slots; server-derived from `CableOverloadRegistry`, replicated via `FaultRegistrySnapshotMessage` (KindCableOverload).
- `CycleFault = 6581`. Read-only. 1 = cycle-fault lockout active.
- `CurrentMismatchFault = 6582`. Read-only. 1 = CURRENT_MISMATCH_FAULT lockout active (producers on a network with anything other than producers + transformers).
- `MaxChargeSpeed = 6583`. Read-only on every device with an elastic-supply / elastic-demand cell: Battery, AreaPowerControl, RocketPowerUmbilicalMale, RocketPowerUmbilicalFemale. Returns the configured per-prefab charge rate cap (W). Replaces the prior PGP repurpose of `ImportQuantity` on Battery.
- `MaxDischargeSpeed = 6584`. Same device set as MaxChargeSpeed. Returns the configured per-prefab discharge rate cap (W). Replaces the prior PGP repurpose of `ExportQuantity` on Battery.
- `ChargeSpeed = 6585`. Same device set. Returns the ACTUAL charge rate this tick (W), post-elastic-allocation. Reads from `SoftDemandShareCache[refId]`. Value is updated live as the allocator (ALLOCATE) computes each supplier's share; the field holds the most recent post-ALLOCATE value between ticks. IC10 reads in practice see the end-of-tick value because IC10 (the LOGIC TICK phase) doesn't run during the earlier phases of the atomic tick (SNAPSHOT through the tail).
- `DischargeSpeed = 6586`. Same device set. Returns the ACTUAL discharge rate this tick (W), post-elastic-allocation, i.e. the cell's discharge rate. For Battery / RocketPowerUmbilical* (pure elastic suppliers) this reads `SoftSupplyShareCache[refId]`, which already IS the cell rate. For an APC the `SoftSupplyShareCache` entry is the BUNDLED supply (passthrough + grant-through + cell) that feeds the bundled vanilla `GetGeneratedPower` surface, so the APC's `DischargeSpeed` reads the CELL-only share from a separate `ApcCellDischargeCache`, keeping the value consistent across every storage device (deviation P9). Same live-update / non-latched semantics as `ChargeSpeed`; IC10 reads see the end-of-tick value.

All four LogicTypes (6583..6586) appear on Battery, AreaPowerControl, RocketPowerUmbilicalMale, RocketPowerUmbilicalFemale. For APC the "cell" is the inserted `BatteryCell`; the rates apply to its charge / discharge. For RocketUmbilical the cell is the device's internal `PowerMaximum = 10000` store.

The prior PGP repurpose of vanilla `LogicType.ImportQuantity` (29) and `ExportQuantity` (31) on Battery is REMOVED, hard. The mod has not yet been deployed, so no backward-compatibility shim is required. Vanilla meaning reverts on Battery (which is nothing meaningful, since Battery has no import / export slots).

Vanilla `LogicType.Power` (1) changes meaning on swept plain devices (decision §0.28, `Patches/PowerLogicReadPatches.cs`): a postfix on the base-declared `Device.GetLogicValue` returns 1 iff the device's power cable network is verdict-LIVE (`NetLiveness`), 0 otherwise, independent of the device's own switch, so a switched-off device on a live net reads `Power` = 1 while its Powered flag (and hover) shows unpowered. This preserves the grid signal scripts read under the decoupled build; read `On` for the switch state. Everything outside the sweep's ownership keeps the vanilla `Powered ? 1 : 0`: the non-simulating side (a client's flag is synced from the host), cable-less devices, incomplete structures, no-power-state devices, segmenters (Transformer, Battery, AreaPowerControl, the wireless pair, the umbilical halves), PoweredOwnership-exempt classes, emergency-light prefabs, and quarantined devices. Between publishes the read serves the previous tick's verdict, the same cadence the flag updates on; there is no live freshness gate, per the single-boundary-read mandate.

`Setting` keeps its vanilla read/write semantics for IC10 access (clamped `[0, OutputMaximum]`). PowerGridPlus initialises `Setting = OutputMaximum` at world load. The in-world knob and Labeller tool redirect writes to `Priority`. See §5.3.

Transformer `PowerActual` (the PGP-added read, §19.3) reads the `_powerProvided` ledger AFTER the tick-tail settle (§10.7), so it reports the transformer's ACTUAL current throughput this tick, storage-charge flow included; before the 2026-07-02 rearchitecture the read reported the ledger's drifting billed plateau instead. More generally, every throughput reading on transformers and wireless pairs (device reads and the network `PowerPotential` / `PowerActual` mirrors) includes storage-charge flow, not just running machines (§9.5).

The `Ic10ConstantsPatcher` injects these names into the IC10 syntax highlighter at prefab-load time.

The full per-device-type LogicType inventory (vanilla and PowerGridPlus-added, with location references) lives at the end of this document in §19 "IC10 logic field inventory."

## 13. Multiplayer

- Deprioritization and overload decisions run on the host (the peer with `RunSimulation == true`).
- Fault state is broadcast to clients as ONE per-tick full-snapshot message, `FaultRegistrySnapshotMessage`, with a `Kind` byte selecting the registry (deprioritized = 0, device-overload = 1, cycle = 2, current-mismatch = 3, dead-input = 4, cable-overload = 5). This single message replaces the former per-transition diff messages (one per registry) outright (deviation P16); transitions are implicit in the snapshot deltas. Per-entry wire layout by kind: every kind carries `(int64 refId, int32 remainingTicks)`; deprioritized (0) appends `(single needsW, single upstreamDemandW, single upstreamSupplyW)`, the hover triple (§11.1, decision §0.30); the two overload kinds (1 and 5) append `(single valueW, single capW)`, the hover-diagnostics payload (§11.1, decision §0.29); current-mismatch appends the violator-names string.
- Clients mirror each registry against a monotonic wall clock (deviation P15): the snapshot carries each entry's REMAINING ticks, the client stores `expiry = MonotonicClock.NowMs + remaining * 500 ms` and reads "still faulted" as `expiry > now` (the overload mirrors store the payload pair alongside the expiry, so client hovers show the host's numbers). `ElectricityTickCounter` advances only on the simulating peer, so a client cannot use an absolute until-tick.
- Priority writes broadcast via `PriorityMessage` (separate from the fault snapshot).
- Join-suffix snapshot (`Plugin.SerializeJoinSuffix`) ships: per-device passthrough mode, the six per-kind passthrough defaults (`PassthroughDefaultsSync`; fixed order small transformer, other transformer, battery, APC, power transmitter, umbilical), per-transformer priority, and a `(ReferenceId, remainingTicks)` snapshot of the five COUNTDOWN registries in a fixed block order documented in `Plugin.cs`: (1) deprioritized plus its per-entry `(needsW, upstreamDemandW, upstreamSupplyW)` payload, (2) device-overload plus its per-entry `(valueW, capW)` payload, (3) cycle, (4) current-mismatch plus violator names, (5) cable-overload plus its payload, appended fifth so older block order is preserved. Deprioritization / overload toggles are no longer carried (the behaviors are always on, decision §0.27). The dead-input cue is intentionally NOT in the handshake (see "Mid-cooldown client join" below).

**Per-tick full registry sync (heartbeat).** `PowerAllocator.SyncFaultSnapshots` broadcasts a `FaultRegistrySnapshotMessage` for each of the SIX snapshot kinds every tick: the five lockout registries (`DeprioritizedRegistry`, `OverloadRegistry`, `CableOverloadRegistry`, `CycleFaultRegistry`, `CurrentMismatchFaultRegistry`) listing every currently-locked `ReferenceId` plus its REMAINING tick count (the current-mismatch snapshot also carries each producer's violator names, the two overload snapshots each entry's `(valueW, capW)` hover payload, and the deprioritized snapshot each entry's `(needsW, upstreamDemandW, upstreamSupplyW)` triple), and the `DeadInputRegistry` membership cue (deviation P7; the carried int is a keep-alive TTL, not a countdown). A kind's packet fires every tick while it is non-empty, PLUS exactly one EMPTY packet on the non-empty -> empty transition (the `_xxxWasNonEmpty` flags), so an OFF-as-reset, or the §8.5 network-retry recover, clears the client mirror immediately instead of waiting out the local expiry. Each tick is its own heartbeat: clients overwrite their mirror with the received list, so a lost or reordered packet self-heals on the next tick. There is no separate diff message; this snapshot is the authoritative and only fault-state channel (deviation P16). Because the current-mismatch network commit (§8.5) stamps a producer cohort to ONE shared expiry, every cohort member carries the same remaining-tick count in the snapshot, so clients render their countdowns ticking down together.

**Mid-cooldown client join.** The join handshake bundles the five COUNTDOWN registries (deprioritized, device-overload, cycle, current-mismatch, cable-overload; the current-mismatch entries carrying violator names, the two overload blocks their `(valueW, capW)` payloads, and the deprioritized block its `(needsW, upstreamDemandW, upstreamSupplyW)` triple) into the join-suffix payload, each entry as `(long ReferenceId, int remainingTicks)` plus the kind's extras, so a joining client lands mid-lockout with the correct countdown and hover numbers showing on every faulted device. Without this, a join during a 60 s lockout window would render the device as un-faulted on the client until the next per-tick snapshot arrived, and the countdown would restart at 60 instead of resuming from the actual remaining time. The dead-input cue is intentionally NOT bundled: it has no countdown and only a 2-tick keep-alive TTL, so the joining client picks it up on the first per-tick heartbeat (within one tick) rather than carrying durable join state.

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

**Compatibility:**

| Setting | Default | Order | Purpose |
|---|---|---|---|
| `ExtraDeliveryDevices` | (empty) | 10 | Section `Server - Compatibility`, key `Extra Delivery Devices`. Comma-separated extra device PrefabNames whose `ReceivePower` is called with their granted power each tick, on top of the delivery shim's built-in five (Omni Power Transmitter, Suit Storage, Battery Cell Charger, Powered Bench, Wall Light Battery; decision §0.28, §2.1 row 9). For modded devices whose gameplay effect (charging something, forwarding power) runs inside `ReceivePower`; the load-time `ReceivePowerOverrideCensus` log names candidates. |

**Batteries:**

| Setting | Default | Order | Purpose |
|---|---|---|---|
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
| `BatteryChargeEfficiency` | 1.5 | 80 | Charge cost: grid energy drawn per unit stored (`stored = delivered / value`; values below 1.0 treated as 1.0; a post-loss trickle under 500 W stores the full delivery). Default 1.5 stores two thirds of the draw; the loss is destroyed for now (heat conversion planned, decision §0.17). |
| `EnableBatteryLogicAdditions` | true | 90 | Expose the four soft-power reads (MaxChargeSpeed / MaxDischargeSpeed / ChargeSpeed / DischargeSpeed) |
| `BatteryPassthroughDefault` | true | 100 | Key `Battery Passthrough Default`. Seed `LogicPassthroughMode` for a battery whose mode was never explicitly set; a stored per-device mode wins once set (decision §0.27). The per-prefab rate caps above are always on (no master toggle). |

**Area Power Control:**

| Setting | Default | Order | Purpose |
|---|---|---|---|
| `ApcBatteryChargeRate` | 1000 | 15 | APC charge cap (W); mirrors vanilla `BatteryChargeRate` default. Read at compute-time by `AreaPowerControlPatches.ComputeChargeCap`; the vanilla `AreaPowerControl.BatteryChargeRate` field is NOT mutated (D2 non-mutating, deviation P14). The APC power fix itself is always on (decision §0.23). |
| `ApcBatteryDischargeRate` | 1000 | 17 | NEW. APC discharge cap (W). Vanilla has no parallel field; PowerGridPlus tracks per-APC via a static dictionary keyed by `ReferenceId`. Default mirrors charge for symmetry. |
| `ApcPassthroughDefault` | true | 20 | Key `APC Passthrough Default`. Seed `LogicPassthroughMode` for an APC. The APC exposes no writable mode logic port, so the config default is its only seed; the seeded mode still persists with the save (decision §0.27). |

**Transformers and protection:**

| Setting | Default | Order | Purpose |
|---|---|---|---|
| `EnableTransformerLogicAdditions` | true | 20 | Power Actual read |
| `SmallTransformerPassthroughDefault` | true | 30 | Key `Small Transformer Passthrough Default`. Seed `LogicPassthroughMode` for the three small transformer prefabs (StructureTransformerSmall, StructureTransformerSmallReversed, StructureRocketTransformerSmall) while never explicitly set (decision §0.27). |
| `OtherTransformerPassthroughDefault` | false | 35 | Key `Other Transformer Passthrough Default`. Seed mode for every other transformer variant while never explicitly set. |

Deprioritization, priority dispatch, and overload protection are always on; the `EnableTransformerDeprioritization` / `EnableTransformerOverloadProtection` masters are removed (decision §0.27).

**Rocket Umbilical:**

| Setting | Default | Order | Purpose |
|---|---|---|---|
| `RocketUmbilicalChargeRate` | 10000 | 20 | Per-pair charge rate cap (W). Default matches vanilla `PowerMaximum`. Applies to both Male and Female halves of every linked pair. |
| `RocketUmbilicalDischargeRate` | 10000 | 30 | Per-pair discharge rate cap (W). |
| `UmbilicalPassthroughDefault` | true | 40 | Key `Umbilical Passthrough Default`. Seed `LogicPassthroughMode` for an umbilical half (Male and Socket) while never explicitly set; a mode write to one half mirrors to the docked partner, persisted across save / load. An undocked umbilical bridges nothing (decision §0.27). |

The rate caps and the four soft-power LogicTypes (MaxChargeSpeed / MaxDischargeSpeed / ChargeSpeed / DischargeSpeed) on Male and Female are always on; the `EnableRocketUmbilicalLimits` master is removed (decision §0.27).

**Power transmitter:**

| Setting | Default | Order | Purpose |
|---|---|---|---|
| `PowerTransmitterPassthroughDefault` | true | 10 | Key `Power Transmitter Passthrough Default`. Seed `LogicPassthroughMode` for a wireless dish, covering both ends of a linked pair, while never explicitly set (decision §0.27). |

**Diagnostics / Powered Presentation (groups removed):** the per-tick conservation check (§8.8) is always on with no config entry; `Enable Conservation Check` and `Decouple Powered From On Off` are removed with their groups (decision §0.27). Powered itself follows the vanilla on/off coupling decided from the tick snapshot (decision §0.28 reverted the decoupling the removed setting used to control); the grid signal moved to the IC10 `Power` read on swept plain devices, which returns 1 iff the net is verdict-LIVE, so a switched-off device on a live net still reads `Power` = 1 while drawing nothing and showing dark.

**Emergency lights:**

| Setting | Default | Purpose |
|---|---|---|
| `EnableEmergencyLights` | true | Key `Enable Wall Light Battery Emergency Mode` (Order 10). Wall Light Battery devices behave as emergency backup lights: lamp off while grid-powered, on (from the internal cell) when grid power is lost. Per-light opt-out via Mode 1. |
| `EmergencyLightPrefabs` | StructureWallLightBattery | Key `Emergency Light Prefabs` (Order 20). Comma-separated light prefab names that get the emergency-backup behaviour while the master toggle is on; entries must be battery-backed wall lights (the `WallLightBattery` device class); matched against the device's `PrefabName`. |

### 14.3 Removed (with dead code)

- `EnableVoltageTiers` (now always-on).
- `EnableUnlimitedSuperHeavyCables` (subsumed by `CableSuperHeavyMaxWatts = 0`).
- `EnableTransformerExploitMitigation`, `EnableAreaPowerControlFix`, and `Enable Device Powered Ownership` (decision §0.23: the three behaviors are unconditional).
- `EnableBatteryLimits`, `EnableTransformerDeprioritization`, `EnableTransformerOverloadProtection`, `EnableRocketUmbilicalLimits`, `EnableConservationCheck`, and `DecouplePoweredFromOnOff` (decision §0.27: the behaviors became unconditional; the `Server - Diagnostics` and `Server - Powered Presentation` groups are gone). The decoupling behavior itself was then reverted by decision §0.28: Powered follows the vanilla coupling and the grid signal lives in the IC10 `Power` read instead; no setting returns.
- The five per-family `Enable * Logic Passthrough` masters (decision §0.27: passthrough support is always on; the six per-kind `Passthrough Default` settings seed never-set modes instead, and the per-device `LogicPassthroughMode` is the only runtime gate).

### 14.4 Settings rename rules

BepInEx keys `(Section, Key)` identify entries. Renaming either orphans the saved value (BepInEx resets to default on next load). The cable max settings and `ApcBatteryDischargeRate` are NEW (no rename). If any existing setting's Section or Key changes in this refactor, the About.xml `<ChangeLog>` carries a player-facing note per project CLAUDE.md guidance.

### 14.5 APC field handling at runtime

PowerGridPlus never mutates `AreaPowerControl.BatteryChargeRate` (decision §0.2 non-mutating, deviation P14). The charge cap is read at compute time: `AreaPowerControlPatches.ComputeChargeCap` reads `Settings.ApcBatteryChargeRate.Value` live and bounds it by the input cable's remaining headroom after the APC's own passthrough (§7.5); the APC power fix is unconditional (decision §0.23; the old master toggle is removed). A third-party mod reading the vanilla field therefore sees the unmodified vanilla value, not the configured cap. The discharge cap has no parallel vanilla field; PowerGridPlus tracks it in `ApcDischargeRateRegistry` (per-APC, session-only, defaulting to `Settings.ApcBatteryDischargeRate.Value` when no override exists). The registry is queried by the elastic-supply allocator (§7.3) and the `MaxDischargeSpeed` logic read; it does not need to back any vanilla code path because no vanilla code reads a discharge cap on APC.

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

- SNAPSHOT: 1x device-walk per network with exactly one `GetUsedPower` / `GetGeneratedPower` sample per device. ≈ 400 device-method calls; this is the only device-method walk in the tick (the old second ENFORCE walk is gone with the trio).
- ALLOCATE: O(N) Kahn topological order + the fixed-point loop (bounded 2N+4 rounds, typically 1-2; each round carries both flow classes, so there is no separate charge-distribution walk) + the publish tail, where N = contributor count. ≈ 1000 method calls, ≈ 100 µs.
- WRITE-BACK + tail: plan application (net field writes, settlement entries, accumulator drains) plus the Powered reconciles, ledger settle, and audits over the enrolled roster (§10.6 / §10.7 / §8.8). Field writes and list walks; the only device-method calls are the delivery shim's `ReceivePower` on the classified rows (§2.1 row 9; a handful of devices on a typical save).
- DEVICE TICK + LOGIC TICK: vanilla copy, no overhead.

Aggregate cost is well under 1 ms per tick at 2 Hz: rounding error against the broader simulation. The tick-duration watchdog (§8.8) alarms if the allocator ever stops being rounding error.

## 17. Invariants the implementation must preserve

1. Tick atomicity: every decision at tick N takes effect at tick N. No latency. No oscillation.
2. Priority comparisons are local to a single input network.
3. Deprioritization cascade is bottom-up; the smallest possible subnet is deprioritized first.
4. Storage charge (the Soft class) rides the same priority-tiered backward/forward sweep as rigid demand (§9): funded per network from the §9.2 ladder consumed in order (firm residual, then soft inflow, then the elastic leftover the rigid settlement did not consume; elastic funds soft ONLY from that per-net leftover, the lowest rung), split tier-first and proportional-to-residual-headroom within a tier, capped by cable headroom at every hop, and it never displaces rigid capacity and never triggers a fault.
5. Soft demand never causes rigid devices to flicker; soft devices scale with available headroom but never below 0.
6. Voltage tiers are enforced regardless of any toggle.
7. Cycles are faulted regardless of any toggle. PowerGridPlus's PROTECT-phase detection covers cycles through `ElectricalInputOutput` device chains AND cycles through PT/PR wireless links; both produce CYCLE_FAULT, not a cable burn. It is the only cycle detection in the pipeline; vanilla's recursive-provider detector never runs (§17.25).
8. OFF clears the lockout. ON re-evaluates.
9. A deprioritized transformer's downstream is dark for 60 s regardless of mid-window surplus availability.
10. PT/PR pairs behave identically to wired transformers at the allocator layer; their per-device methods continue to run under PowerTransmitterPlus's distance-loss math without coordination.
11. Stationary batteries are dual-terminal `ElectricalInputOutput` devices. Charge demand fires on the Input network; discharge supply fires on the Output network. Both flow simultaneously per tick on distinct networks with no interlock; net cell delta = received_on_input - delivered_on_output.
12. APCs are dual-terminal but their internal `_powerProvided` couples both sides per tick; charge and discharge of the inserted cell are in-tick exclusive.
13. A battery or APC with `InputNetwork == OutputNetwork` is short-circuited via vanilla `ElectricalInputOutput.IsOperable`. All four power methods return 0 / no-op. Hover shows "Device Short Circuited". PowerGridPlus relies on the vanilla gate and does not implement parallel detection.
14. A single transformer can never burn a cable by overdraw: its `effective_cap` is bounded by `min(OutputMaximum, InputCable.MaxVoltage, OutputCable.MaxVoltage)`. Even multiple transformers collectively pushing a network past the cable cap never burn it; they trip into CABLE_OVERLOADED instead (§5.7, decision §0.29). Only sustained direct generator overflow burns cable.
15. Cable caps per tier are server-authoritative via settings and NON-MUTATING (decision §0.2): `Cable.MaxVoltage` is never written. Every cap reader (battery / APC headroom, the allocator's effective-cap formula, the snapshot's per-net weakest cap, the write-back's generator-overflow burn rule) consults the `CableMax` helper, which reads the configured per-tier value live at use time; no vanilla burn check exists to disagree with it (the trio is retired). `0` means unlimited and is normalised internally to `float.MaxValue` (clamped to a finite 1e9 sentinel where it enters the allocator's capacity sums, §5.0.3).
16. The PT/PR pair's cap is a STATIC link rating computed per tick from whichever distance model is active (§6.3): PowerTransmitterPlus's configured Max Transfer Capacity (0 = unlimited, mapped to a finite 1e9 sentinel) with distance repriced as the source-draw multiplier `m`, or the vanilla `MaxPowerTransmission` minus the `PowerLossOverDistance` delivery loss when PowerTransmitterPlus is absent. It is never the live `InputNetwork.PotentialLoad` (the live read collapses to a cross-tick zero fixed point on transformer-fed sources). PowerGridPlus does not hardcode either model.
17. All power simulation values are Watts. There are no amps or volts in the simulation; the misleadingly named `Cable.MaxVoltage` is a Watts cap (vanilla compared it against accumulated Watts in `PowerTick.GetBreakableCables`; the mod's write-back compares the configured tier cap against the 20-tick generator-flow average, §5.7).
18. Every segmenting device (Transformer, Battery, AreaPowerControl, PowerTransmitter, PowerReceiver, RocketPowerUmbilicalFemale, RocketPowerUmbilicalMale) is a level boundary in the §8.0 cascade. The list is exhaustive against the 0.2.6228.27061 decompile. PowerConnection is excluded per §5.0.1.
19. Transformer operational state is binary per tick: ON (working) or LOCKED-OUT (deprioritized XOR overloaded). There is no "throttled" intermediate state. A transformer either gets its full required input or is deprioritized; a transformer either delivers within `OutputMaximum` or overloads.
20. Deprioritization and overload converge to a joint fixed point inside ALLOCATE of one tick (§8.0). External observers never see the iteration; one tick produces one joint result.
21. Cycles fault only when the loop is powered (`min(Potential, Required) > 0` on a member network, evaluated on the snapshot's per-net sums); unpowered cycles persist silently until current flows (§4.4).
22. Cable burning exists ONLY for direct generator overflow (the write-back's 20-tick rule, §5.7) and wrong-tier junctions (§4.3). Transformer or battery contributions causing a cable overflow trip the upstream segmenting devices into CABLE_OVERLOADED instead of burning the cable (§5.7, decision §0.29).
23. Battery discharge is elastically capped at `min(DischargeRateCap, PowerStored)` per battery (§7.3); proportional split is computed against effective caps, not the static rate cap. A high-cap battery is never throttled by a low-stored sibling.
24. A battery's discharge to its OUTPUT network is independent of any state on its INPUT network. The "kept-alive by local battery" failsafe is automatic from the dual-terminal model (§7.3.1).
25. Cycles are resolved by CYCLE_FAULT (§4.5), not by burning cables. PowerGridPlus's PROTECT phase runs its OWN walk over the segmenting-device graph built from the grid snapshot (§4.2.5), and that walk is the SOLE cycle signal: vanilla's `CheckForRecursiveProviders`, the `BreakableCables` / `BreakableFuses` lists, and `BreakSingleCable` / `BreakSingleFuse` all live inside the retired `PowerTick` trio and never run, so there is no vanilla destructive fallback (a cycle the walk missed would conduct un-faulted until a code fix lands). The walk catches the gaps vanilla missed: wireless cycles (PT/PR are first-class graph nodes), multi-hop loops with no provider anchor, battery-only and APC-only cycles where every network's `Required` is 0, self-sustaining mutual-feed loops. Every segmenting device on a detected cycle enters CYCLE_FAULT for 60 seconds and the loop breaks immediately because every device in it contributes 0.
26. CYCLE_FAULT is a lockout state parallel to DEPRIORITIZED and the two overload kinds. Visuals share the red flash with the overload kinds; hover text differs.
27. Producer-isolation invariant (strict-literal): producers must only connect to other producers and `Transformer` instances (§8.5). Battery, AreaPowerControl, PowerTransmitter, PowerReceiver, RocketPowerUmbilicalMale, and RocketPowerUmbilicalFemale do NOT satisfy producer-isolation despite being segmenters. Violations trigger CURRENT_MISMATCH_FAULT on flash-capable producers (red flash + countdown hover) or the hover-only path on Solar / Wind / RTG (GetPassiveTooltip postfix appends the fault line). The producer-isolation walk treats every `Transformer` instance as a Transformer regardless of its fault state; a producer wired to a CYCLE_FAULTed transformer does NOT get CURRENT_MISMATCH_FAULT (§8.5). A producer-only network (producers + no other devices) is silent: no current-mismatch fault, no warning. The vanilla "irreducible undersupply" case no longer exists in valid configurations.
28. The allocator uses ONLY integer keys for every ordering decision (the Kahn topological order's `ReferenceId` tiebreak, supplier dispatch `priority DESC / ReferenceId ASC`, deprioritization victim selection over `priority ASC / claim DESC / ReferenceId ASC`), for MP determinism. Floats never enter an ordering decision; the deprioritization-victim claim is quantised to integer Watts via `(int)Math.Floor` and the deprioritization deficit via `(int)Math.Ceiling(deficit - Eps)` (§8.0.5).
29. Fault hover text includes a live countdown `{n}s` recomputed on each hover poll (`Thing.GetPassiveTooltip`; `Tooltip.SetValuesForInteractable` for the button title box), plus a diagnostics line under every fault title. The templates live in §11.1 as the single source of truth.
30. Flash visuals attach to every segmenting device with an `InteractableType.OnOff` interactable AND every flash-capable producer (per the §11.4 table). Non-flash producers (SolarPanel, WindTurbineGenerator, RTG) use the hover-only path: a universal-base `Thing.GetPassiveTooltip` postfix appends the fault line + countdown to their tooltip. The cable-burn fallback (§11.6) is reserved for future producer classes that have neither flash nor a useful hover surface.
31. `PowerConnection` is vestigial dead code (§5.0.1). PowerGridPlus ignores it entirely: not segmenting, not a Transformer for producer-isolation, not fault-eligible, not in cycle detection. Vanilla itself never consults it (`IsPowerProvider = false`, no `is PowerConnection` checks).
32. Burned-cable per-instance reasons survive save/load via `BurnReasonSideCar` (§11.6). The side-car ZIP entry pattern is mod-removal safe: uninstalling PowerGridPlus leaves `world.xml` untouched and the orphan side-car entry is dropped on the next vanilla save.
33. SoftSupplyShareCache carries the full §7.3 elastic share (`rigidShare + softTopUp`, §9.2) onto every read surface via Harmony postfixes on `Battery.GetGeneratedPower`, `AreaPowerControl.GetGeneratedPower`, `RocketPowerUmbilicalMale.GetGeneratedPower`, `RocketPowerUmbilicalFemale.GetGeneratedPower`, and the write-back drains each store exactly that granted share (§7.3.0.1). Without the postfixes a store would advertise raw headroom to the next tick's boundary read, tooltips, IC10, and third-party callers, disagreeing with what actually moves; a locked-out or dead-net supplier must read 0 everywhere. The postfixes are not optional.
34. Every segmenting device class (per §5.0) enters the allocator's roster (§8.0.0.1): Transformer, APC, and the linked PT/PR pair as pull-through contributors; Battery, APC cell, and RocketPowerUmbilical* as elastic suppliers / soft demanders. An unclassified future class falls to the unknown-bridge fallback (one honest boundary sample per side, all-or-nothing net liveness, §8.0.0.2) and is reported by the census, so the gap is visible and conservatively billed instead of silently mis-modelled.
35. No partial power exists anywhere: the vanilla `_powerRatio = Potential / Required` scaling path is DELETED with the `PowerTick` trio (§8.0.0.2), and nothing reimplements it. Every device is served whole or dark whole; an unclassified device class gets one honest boundary sample and rides the same all-or-nothing liveness as everything else. The old concern behind the retained fallback (phantom power or corruption is worse than partial power) is answered by honest billing plus the machine-checked conservation and delivery audits, not by scaling.
36. `Setting` keeps its vanilla read / write semantics for IC10 access (§5.3). PowerGridPlus does NOT redirect `Setting` writes from IC10. Only the in-world knob and the Labeller tool redirect writes to `Priority`. PowerGridPlus initialises `Setting = OutputMaximum` ONLY on new transformer construction; saved Setting values are preserved across save / load so IC10-throttled transformers retain their throttle.
37. All throughput formulas (§5.5, §5.6, §8.0, §8.4, §9) use `Setting` for the active throughput cap. `OutputMaximum` appears only as the prefab rating and the upper bound of Setting. An IC10 script that writes `Setting < OutputMaximum` reduces the transformer's effective_cap and can trigger DEPRIORITIZED or OVERLOAD at the lower threshold.
38. The prior PGP repurpose of vanilla `LogicType.ImportQuantity` (29) and `ExportQuantity` (31) on Battery is REMOVED. Battery now exposes `MaxChargeSpeed` (6583), `MaxDischargeSpeed` (6584), `ChargeSpeed` (6585, actual this tick), `DischargeSpeed` (6586, actual this tick) instead. Breaking change for existing IC10 scripts that read ImportQuantity / ExportQuantity on Battery; documented in `<ChangeLog>`.
39. Skip-while-faulted optimisation (refined). A device in CYCLE_FAULT, CURRENT_MISMATCH_FAULT, CABLE_OVERLOADED, DEVICE_OVERLOADED, or DEPRIORITIZED is non-conducting for the entire 60 s lockout window (§10.2.1). The skip-while-faulted shortcut applies ONLY to the question "is the original loop still here?": when the next allocator pass asks whether to re-fire the same fault on the same participant, the participant's faulted-ness is enough to say "yes, this device is still locked out, do not re-validate the topology around it." For NEW cycle detection (the PROTECT-phase DFS) the walk treats faulted segmenters as if they were conducting: their input and output edges remain in the graph and a new cycle that runs through a faulted device is still detected. If a new cycle is found, only the previously non-faulted participants are newly registered for CYCLE_FAULT with a fresh 60 s timer; existing fault timers on devices already locked out are NOT extended, reset, or shortened.

    Worked example. T1, T2, T3 form a cycle and all three enter CYCLE_FAULT at t=0 with `_lockoutUntilTick = currentTick + 120`. At t=10 s the player rewires: the original T1-T2-T3 loop is broken AND a new T1-T4-T5 loop is formed (T1 sits at the corner of the rewire). The PROTECT-phase DFS walks the new graph; because the walk treats faulted T1 as conducting, the new T1-T4-T5 cycle is detected. T4 and T5 are newly registered with full 60 s timers (50 s of which remain "behind" T1's clock). T1 keeps its existing timer (50 s remaining). At t=60 s T1's original timer expires; at t=70 s T4's and T5's timers expire; on the next allocator pass after each expiry, the cycle is re-checked. As long as T1-T4-T5 is still closed, all three re-fault. The original T1-T2-T3 loop is gone, so T2 and T3 stay clear when their timers expire at t=60 s.

    Implementation summary across the five fault paths:
    - Cycle detection DFS (PROTECT phase): faulted segmenters are walked through, not skipped. New cycles among any combination of faulted and non-faulted devices are detected; only the not-yet-faulted participants get new registry entries.
    - Producer-isolation (CURRENT_MISMATCH_FAULT, PROTECT phase): producers already in CURRENT_MISMATCH_FAULT skip the topology re-check on the current tick. The lockout is timer-only, and the producer's contribution is already 0 via `GetGeneratedPower` postfix. After the timer expires, the post-timeout allocator pass re-walks the network; if the violation persists, current-mismatch re-fires with a fresh 60 s.
    - Deprioritization / overload allocator math (ALLOCATE): faulted devices contribute 0 supply and 0 cap, so they fall out of allocation naturally; no explicit short-circuit is needed. The fixed-point loop is free to walk through them.
40. Fault-state transience across save / load (§10.5). All five fault registries (`DeprioritizedRegistry`, `OverloadRegistry`, `CableOverloadRegistry`, `CycleFaultRegistry`, `CurrentMismatchFaultRegistry`) clear on world load and recompute on the first post-load tick. The `_lockoutUntilTick` countdowns are in-memory only; never serialised. The first post-load tick applies the full 60 s lockout to any rediscovered violation with no grace period (§10.5). `BurnReasonSideCar` is the sole exception and records durable per-instance hover state on already-broken cable wreckage, not transient fault timers.
41. Cycle-detection topology-change skip. PGP tracks `lastTopologyChangeTick` per `CableNetwork`. The PROTECT-phase cycle-detection DFS is skipped on a network whose `lastTopologyChangeTick < lastCheckedTick` (no structural change since the previous walk). Topology-change events that bump the counter: cable place / destroy / burn (wrong-tier burns count), segmenter place / destroy. The skip applies per network; a single network's change does not invalidate the cached result on disjoint networks. On a stable grid (the common case), the cycle detector is a near-zero-cost no-op every tick.
42. Settings are immutable during a game session. The StationeersLaunchPad in-game mod settings GUI is main-menu only; the game does not expose a runtime "open mod settings" panel. Every `Settings.*.Value` read during play returns the same value, so PGP code can treat settings as frozen for the duration of the session. This justifies lazy / no-cache patterns (Stationpedia footers in §15.1) and removes the need for a settings-change event bus.
43. Conservation is MACHINE-CHECKED every tick (§8.8). The always-on conservation checker (no config entry, decision §0.27) audits the allocator's own converged grants at the end of ALLOCATE: per network, granted inflow equals granted outflow within 0.5 W; per routed contributor, `TotalPull == TotalThrough * max(m, 1) + quiescent` whenever it conducts (one-sided against the quiescent when it does not). A violation is an allocator bug by definition and logs a throttled warning with a per-component breakdown; a healthy build logs zero violations.
44. The shortfall census reads ZERO Deadlock networks on a healthy build (§8.8): no network may end a tick with unmet rigid demand while an active supplier retains headroom above 0.5 W AND that supplier's input network retains undelivered supply. Deadlock > 0 is the invisible-deadlock regression signal (the shape of the pre-rearchitecture charge deadlock) and fails the `pgp-rearch-suite` census.
45. Retired (2026-07-12): the partial-power sentinel and the vanilla ratio path it monitored are both deleted (§8.0.0.2, §8.8). No network carries a `_powerRatio` any more, so the old "zero sub-unity ratios on Served nets" assertion is meaningless; the contract it defended (a Served net's members get exactly their grants) is now enforced by construction (the write-back publishes exact fields and settles credit == grant) and watched by the conservation checker (§17.43), the two delivery audits (§17.46 / §17.47), and the Powered-set conformance assert (§17.49).
46. The charge-delivery audit reads ZERO anomalies on a healthy build (§8.8): every store the allocator granted charge this tick is credited exactly that much (0.5 W tolerance; batteries within `[granted / BatteryChargeEfficiency, granted]`, the charge cost's reciprocal) on a Served charge-side network. The audit is a plan-vs-settlement identity check: grants are published dead-net-zeroed, credits are recorded at the write-back settlement site (credit == grant by construction), a store owned by an unbillable contributor is never granted (§8.0.3), and a fill-edge grant is marked moot rather than tolerated numerically.
47. The discharge-delivery audit reads ZERO anomalies on a healthy build (§8.8): every battery / umbilical elastic drains exactly its granted share (`rigidShare + softTopUp`, 0.5 W tolerance, exact both ways) on a Served discharge-side network. The APC cell is out of the audit's scope by design (no grant is published for it; the write-back drains it directly by its cell-only share). Charge and discharge audits can never confuse a same-tick charge + discharge: credits and debits are separate write-back plan lists recorded at separate settlement lanes.
48. The tick-duration watchdog reads ZERO violations on a healthy build (§8.8): a violation requires BOTH that an atomic tick exceed `min(max(8 * rolling median, 50 ms), 400 ms)` wall-clock AND that the allocator's excess over its own rolling median explain at least half the overrun. A violation is therefore a performance regression in the allocator itself (the tick owns a 500 ms period at 2 Hz), not an environmental stall and never a player problem.
49. The Powered-set conformance assert reads ZERO violations on a healthy build (§8.8): every segmenter published HEALTHY reads `Powered == true` at the tick tail past a one-tick marshal grace. A violation means the presentation reconcile (§10.6, the single tick-driven writer) diverged from the allocator's health verdict, or a foreign writer is fighting it.
50. The save/load self-check passes on every load (§8.8): fault registries empty (transient-by-spec, §10.5), priority sidecar restored == loaded, ledger sweep ran. The registry hygiene sweep bounds fault-registry memory to currently-faulted devices (expired and destroyed-device entries pruned every 600 ticks).

## 18. Test plan summary

Per major behaviour, a probe scenario must exist:

- Atomic tick: AP probe (verifies architectural wiring).
- Overload: OP probe.
- Deprioritization cascade with bottom-up propagation: new probe.
- Per-input-network priority comparison: new probe.
- Storage-charge flow (soft riding the sweep, charging across intermediate segments): covered by the `pgp-rearch-suite` charge leg below.
- PT/PR as transformer: new probe.
- Soft-demand cap (no `_powerProvided` runaway): new probe with 60-tick state evolution.
- OFF-as-reset: new probe.
- Multiplayer transition broadcast: existing MP probe extended.
- Visual flash: existing FP probe.
- Hover text: existing OP P7 covers overload; add a deprioritization equivalent.
- Knob: existing KBP probe.
- Labeller: existing LP probe.
- Save/load: existing SLP probe.

Three dedicated-server ScenarioRunner suites carry the plan (each implemented as a single phased scenario, one scenario per server run):

- **`pgp-rearch-suite`**: the unified-flow regression suite. Charge regression (a known nuclear-battery pair charging across a 7-link wireless farm; `PowerStored` trajectory must rise to full), the shortfall census over the `ShortfallDiagnostics` snapshot (§8.8; the Deadlock bucket is the regression signal and must be 0, invariant §17.44), the Powered-presentation check (idle healthy chargers read Powered=True, with a >= 3-consecutive-tick debounce for the one-frame marshal lag of the rising edge, §10.6), and the ledger check (no negative `_powerProvided` on any wireless half after the sweep, settle silent, §10.7), with the conservation checker expected at zero violations throughout (§17.43). Phase A also drives the charge-delivery audit's pure `IsViolation` predicate with six synthetic fixture cases (`RSD P<n>` lines: exact match, under-credit, over-credit, zero grant / zero credit, and the configured-efficiency band both ways) and baselines the live `ViolationStoreTicks` counter; the delivery verdict is PASS when every fixture is green and the counter never moved across the window (invariant §17.46). The auditor round rides the same suite: Phase A drives the discharge audit's pure predicate (`RSD D<n>` lines) and the watchdog's threshold formula plus allocator-attribution gate (`TDW T<n>` lines), baselines the discharge / watchdog / conformance live counters, Phase C reads them back one-shot together with the save/load self-check flags and RegistryHygiene reachability, and Phases A/B log a transfer-visibility line every 10 ticks (reference-battery ratios plus min/max/mean charge ratio across all rostered stores; observation only). Emits one `VERDICT charge=... shortfalls=... powered=... ledger=... delivery=... audits=...` line (the `audits=` field is the combined auditor-round verdict, PASS|FAIL only).
- **`ptp-standalone-suite`**: PowerTransmitterPlus WITHOUT PowerGridPlus loaded (the §6.6 standalone rules). Steady links conserve; transmitter debt stays bounded by the ceiling plus one burst bill (`debt(t) <= ceiling + delivered(t) * m`); flows above 5 kW are billed to the source (the receiver drain-cap lift, `flow5k` leg); an OnOff cycle leaves the debt bounded and resumes clean. Emits `VERDICT api=... pairs=... debt=... flow5k=... toggle=...`.
- **`pgp-atomic-all`**: the composite aggregator running every legacy probe family above (atomic tick AP, overload OP, knob KBP, flash FP, hover HP, labeller LP, multiplayer MP, save/load SLP). Pass/fail counts logged per probe; the final tally is compared against a baseline of 100% pass.

## 19. IC10 logic field inventory

### 19.0 Quick-reference table (one row per unique LogicType name)

For correction-by-number convenience. The "Where used" column lists the device classes that expose this LogicType under PowerGridPlus's enabled patches; meaning is summarised one-line.

| # | LogicType name | Value | Source | R/W | Where used | Meaning |
|---|---|---|---|---|---|---|
| 1 | Power | 1 | vanilla; PGP overrides the read on swept plain devices | R | every IPowered device | On plain devices the ownership sweep owns: 1 iff the device's power net is verdict-LIVE (decision §0.28), independent of the switch. Segmenters, batteries, APCs, exempt / quarantined / cable-less devices, and emergency lights keep vanilla `Powered ? 1 : 0`. |
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
| 14 | PowerActual | 26 | vanilla on Battery/APC/WirelessPower; PGP-added on Transformer | R | Battery, APC, WirelessPower, Transformer (PGP) | `OutputNetwork.CurrentLoad`. On Transformer, PGP reads the `_powerProvided` ledger AFTER the tick-tail settle (§10.7), so it equals the ACTUAL current throughput this tick, storage-charge flow included |
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
| 34 | DeprioritizedFault | 6579 | PGP | R | Transformer, PT | 1 = DEPRIORITIZED lockout active (insufficient upstream supply) |
| 35 | DeviceOverloadedFault | 6580 | PGP | R | Transformer, PT | 1 = capacity (DEVICE_OVERLOADED) lockout active (downstream demand exceeds the transformer's active `Setting` cap, or the wireless pair's static link rating, §8.4); a cable-rating lockout reads via CableOverloaded instead (§0.29) |
| 36 | CycleFault | 6581 | PGP | R | every segmenting device | 1 = CYCLE_FAULT lockout active (device is part of a closed loop) |
| 37 | CurrentMismatchFault | 6582 | PGP | R | every classifier producer WITH a logic surface (Solar, Wind, Large Wind, Gas, Solid-fuel/Coal, Stirling, small Turbine); NOT PowerConnector or RTG (no logic surface) | 1 = CURRENT_MISMATCH_FAULT lockout active (producer wired to anything other than producers or transformers) |
| 38 | MaxChargeSpeed | 6583 | PGP | R | Battery, APC, RocketPowerUmbilicalMale, RocketPowerUmbilicalFemale | configured per-prefab charge rate cap in W |
| 39 | MaxDischargeSpeed | 6584 | PGP | R | Battery, APC, RocketPowerUmbilicalMale, RocketPowerUmbilicalFemale | configured per-prefab discharge rate cap in W |
| 40 | ChargeSpeed | 6585 | PGP | R | Battery, APC, RocketPowerUmbilicalMale, RocketPowerUmbilicalFemale | actual charge rate this tick (W), post-elastic-allocation, from `SoftDemandShareCache` |
| 41 | DischargeSpeed | 6586 | PGP | R | Battery, APC, RocketPowerUmbilicalMale, RocketPowerUmbilicalFemale | actual CELL discharge rate this tick (W), post-elastic-allocation; from `SoftSupplyShareCache` (Battery / umbilical) or `ApcCellDischargeCache` (APC cell-only, since its supply cache is bundled) |
| 42 | CableOverloadedFault | 6587 | PGP | R | Transformer | 1 = CABLE_OVERLOADED lockout active (the cable network cannot carry the demanded flow; the suppliers go offline instead of burning the weakest cable, §5.7, §0.29); auto-clears after 60 s |

Plus, on the separate `LogicSlotType` enum (slot-keyed reads from a parent device, NOT direct `LogicType`):

| # | LogicSlotType name | Source | R/W | Where used | Meaning |
|---|---|---|---|---|---|
| 43 | Charge | vanilla | R | BatteryCell-bearing devices (APC slot, charger slot, etc.) | inserted cell's `PowerStored` |
| 44 | ChargeRatio | vanilla | R | same | inserted cell's `PowerRatio` |

### 19.1 Device base class (inherited surface)

Comprehensive list of every power-related `LogicType` exposed on every power device. Verified against the 0.2.6228.27061 decompile. Entries are numbered for reference.

### 19.1 Device base class (inherited surface)

Read or write gated per device by `HasXxxState` flags. Listed once.

1. **LogicType.ReferenceId** (217, vanilla, read-only) - `Thing.ReferenceId`. Always readable.
2. **LogicType.Color** (38, vanilla, read+write) - paint swatch index. Gated by `HasColorState`.
3. **LogicType.Activate** (9, vanilla, read+write) - interactable activate. Gated by `HasActivateState`.
4. **LogicType.Power** (1, vanilla, read-only) - vanilla `Powered ? 1 : 0`; on swept plain devices PGP serves the net-liveness verdict instead (§12, decision §0.28). Gated by `HasPowerState`.
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

19. **LogicType.Setting** (12, vanilla, read+write) - vanilla `_outputSetting` in `[0, OutputMaximum]`. PGP does NOT intercept Setting: IC10 reads return the live Setting and writes update it (the vanilla `[0, OutputMaximum]` clamp), exactly as in vanilla. Only the in-world knob and the Labeller redirect to `Priority` (§5.3). `TransformerPriorityLogicPatches.cs` wires Priority / DeprioritizedFault / DeviceOverloadedFault / CableOverloadedFault / CycleFault, not Setting.
20. **LogicType.Maximum** (23, vanilla, read-only) - `OutputMaximum`. `Transformer.GetLogicValue:403532`.
21. **LogicType.Ratio** (24, vanilla, read-only) - vanilla `Setting / OutputMaximum`. **PGP override**: returns 1.0 when deprioritization effective (Setting is hardcoded at OutputMaximum). `TransformerPriorityLogicPatches.cs`.
22. **LogicType.PowerPotential** (25, vanilla, read-only) - `InputNetwork.PotentialLoad`. Inherited.
23. **LogicType.PowerActual** (26, **PGP-added** read slot, read-only) - vanilla does NOT expose this on Transformer; PGP returns `_powerProvided`, which the tick-tail ledger settle (§10.7) sets to the allocator's `TotalThrough` every tick before the LOGIC phase runs, so the read equals the transformer's ACTUAL current throughput (rigid + storage-charge flow), not the drifting billed-ledger plateau the pre-settle ledger carried. `TransformerLogicPatches.cs`. Gated by `EnableTransformerLogicAdditions`.
24. **LogicType.LogicPassthroughMode** (6577, PGP-added, read+write) - per-Transformer override. Default 1 on small transformers, 0 elsewhere.
25. **LogicType.Priority** (6578, PGP-added, read+write) - dispatch priority int >= 0, default 100. Backing `PriorityStore`. Replicated via `PriorityMessage`.
26. **LogicType.DeprioritizedFault** (6579, PGP-added, read-only) - 1 when in deprioritization lockout. Server-derived from `DeprioritizedRegistry`. Replicated via `FaultRegistrySnapshotMessage` (KindDeprioritized).
27. **LogicType.DeviceOverloadedFault** (6580, PGP-added, read-only) - 1 when in capacity-overload lockout. From `OverloadRegistry`. Replicated via `FaultRegistrySnapshotMessage` (KindDeviceOverload). The cable-overflow lockout reads via **LogicType.CableOverloadedFault** (6587, same read pattern, from `CableOverloadRegistry`, KindCableOverload; decision §0.29), wired in the same patch class.
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
67. **LogicType.DeprioritizedFault** (6579, PGP-added, read-only on PT only) - 1 when pair is in deprioritization lockout.
68. **LogicType.DeviceOverloadedFault** (6580, PGP-added, read-only on PT only) - 1 when pair is in overload lockout.
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

- **DeprioritizedFault** (6579, PGP, read-only): upstream supply insufficient. Reset by 60s timer or OFF-toggle.
- **DeviceOverloadedFault** (6580, PGP, read-only): capacity lockout; downstream demand exceeds the transformer's active `Setting` cap (the static link rating on a PT/PR pair, §8.4). Reset by 60s timer or OFF-toggle.
- **CableOverloadedFault** (6587, PGP, read-only): cable-overflow lockout; the cable network cannot carry the demanded flow (§5.7, §0.29). Reset by 60s timer or OFF-toggle. NEW.
- **CycleFault** (6581, PGP, read-only): device is part of a closed loop. Reset by 60s timer or OFF-toggle.

All exposed on Transformer (the PT/PR pair reads DeprioritizedFault / DeviceOverloadedFault / CycleFault on PowerTransmitter). The fault reads are independent of each other and of hover precedence; multiple can be 1 simultaneously.
