# Power Grid Plus

Reworks Stationeers wired power: an atomic power tick with priority-based transformer dispatch, automatic deprioritization and overload lockouts, loop and producer-isolation faults with on-device countdowns, elastic battery allocation, three cable tiers as separate transmission voltages, logic-transparent transformer / battery / dish bridges, and deterministic multiplayer cable-network ids.

Work in progress. Not on the Workshop yet. A pure-patch mod: no new craftable devices, no asset bundle. Parts of the simulation are derived from Sukasa's [Re-Volt](https://github.com/sukasa/revolt) (MIT). The full power-system design, invariants, and algorithm specifications live in the repo-root [POWER.md](../../POWER.md). Mod-local design notes and the decision log live in `RESEARCH.md`; pending work lives in `TODO.md`.

## What it does

The atomic power tick:

- Replaces the electricity tick outright: one snapshot of the whole grid (topology plus every device's demand and output, read exactly once per tick), the protection checks, one global allocation, then one write-back that applies the results. Decisions made in a tick apply in that same tick, so there is no one-tick lag and no flicker.
- The vanilla per-network power flow no longer runs at all. Every device's demand is sampled once, every delivery equals the allocator's grant exactly, and every joule is accounted for; the mid-tick races and partial-power artifacts of the vanilla flow are gone by construction, not patched around.
- A device is either fully powered or cleanly off. Vanilla's partial-power scaling (every machine browning out a little) has no equivalent in this model; a network that cannot carry its load goes dark as a unit with a visible fault instead.

Faults. A power device that cannot do its job enters a visible 60-second fault lockout instead of silently misbehaving. Five fault types, each with its own on-device feedback:

- **Deprioritized fault** (orange flash): a transformer (or other bridging device) cannot get its full draw from its input network. When siblings compete for a short input, the lowest Priority is deprioritized first. While deprioritized it contributes 0 to its output network, so the subnet goes dark cleanly instead of every device flickering. The hover shows the contest (`Needs 12.0 kW while 30.0 kW competes for 20.0 kW upstream`).
- **Device overloaded fault** (red flash): the draw on a network exceeds what its supplying device can deliver. The hover titles the actual device (`Transformer overloaded fault`, `Link overloaded fault` on a wireless dish pair, `Battery overloaded fault`, `APC overloaded fault`, `Umbilical overloaded fault`) and shows the live numbers (`Drawing 150.0 kW of 100.0 kW`), so you know what to upgrade or split. A storage device at its full effective discharge rate with demand still unmet takes the same fault under its own name.
- **Cable overloaded fault** (red flash): the devices could deliver the flow, but the network's weakest cable cannot carry it, so the suppliers go offline instead of burning the cable. The hover shows the flow against the wire's rating (`Pushing 80.0 kW onto a 5.0 kW wire`). A battery tripped this way takes the same visible lockout (it previously produced zero silently, with no cue).
- **Cycle fault** (red flash): the device is part of a closed power loop (`This device is part of a power loop`). Every bridging device on the loop faults and contributes 0, which dissolves the loop. No cable is burned for loops.
- **Current mismatch fault** (red flash): a power producer (solar, wind, generator, RTG, the small wall turbine, or a portable generator plugged into a power connector) shares a cable network with any device that is not another producer or a transformer; the hover explains the rule (`Generator DC cannot feed the AC grid without a transformer`) and names the offending devices, one per line, capped at three plus a count of the rest. The rule is strict: batteries and other bridging devices (APCs, wireless dishes, rocket umbilicals) count as violations too, and a transformer on the network does not exempt the rest; everything except producers and transformers must sit behind a transformer. Producers without a button (solar, wind, RTG) stop generating and explain themselves in their hover text. An unrecognised modded producer in the same situation falls back to burning the cable next to the violating device. Every producer with a logic port (so not the RTG or the bare Power Connector dock) exposes a read-only `CurrentMismatchFault` logic value, 1 while faulted, for IC10.
- Every faulted device shows one block on its casing hover and its on/off-button hover: the switch state and the fault title with a live countdown (for example `On - Deprioritized fault: 42.17s`), then calm grey diagnostics with the offending value in the fault red and the capacity value in blue; watts render whole below 1000 W and as kilowatts with one decimal above. Toggling the device off clears the fault instantly; toggling back on re-evaluates, and the fault re-fires if the cause is still there.
- Faults are transient: they clear on save load and recompute from the live topology on the first tick.
- Not every dark subnet is a fault: a contributor whose input network has no power source at all simply idles, with a steady grey `No upstream supply` hover (`The input network carries no power`; no flash, no countdown, no lockout). Power its input and it delivers again immediately. A transformer throttled below its rated throughput by an IC10 `Setting` write carries an amber `Throttled` note instead (`Limited to 3.2 kW of 50.0 kW by the IC10 Setting value`, `The dial sets priority instead of power`).
- A healthy but idle bridge stays powered. A charger transformer whose batteries are full, or an idle dish pair, reads Powered on (hover and IC10 alike) instead of vanilla's misleading "unpowered" state. Only faulted, switched-off, or dead-input bridges read unpowered, so the Powered flag means what a player thinks it means.
- A machine's own demand spike can never reboot it. The mod decides every device's Powered flag from its network's state (live, deprioritized, overloaded, or dead) plus the device's own switch, not from vanilla's per-device met-this-tick check, so a printer starting a job keeps running: either its network carries the load, or the whole subnet goes dark for 60 seconds as a unit. A switched-off device shows dark exactly as in the base game (info screen, button glow, and every other powered visual), and a toggle takes effect within one power tick (up to half a second). In IC10, `Power` on a plain device reads whether its network is energized: it returns 1 on a live network even while the device is switched off (the hover still shows it unpowered), so scripts get a stable grid signal; read `On` for the switch state.

Transformer priority and dispatch:

- **The vanilla Setting value is the throughput cap again.** IC10 reads and writes work exactly as in the base game, clamped 0..OutputMaximum. A freshly built transformer starts at Setting = OutputMaximum (full throughput); a saved world keeps its saved Settings. A transformer with Setting 0 deliberately delivers nothing (logged at load so a dark subnet is explainable).
- **The in-world knob sets Priority instead** (non-negative integer, no upper cap, default 100, step 10 per screwdriver click or 1 with Alt). The Labeller tool also sets Priority. Priority decides who is deprioritized first when an input network runs short; comparisons are local to each input network.
- **Priority also ranks dispatch, not just deprioritization.** When several transformers feed one output network, higher-Priority transformers carry the load first and lower-Priority ones act as backup, picking up only what the higher ones cannot. Transformers at the same Priority share the load in proportion to their capacity (a large and a small transformer split by size, not evenly).
- **Read-only logic values** `DeprioritizedFault`, `DeviceOverloadedFault`, `CableOverloadedFault`, and `CycleFault` return 1 while the matching lockout is active (`DeviceOverloadedFault` covers the capacity lockout, `CableOverloadedFault` the cable-overflow one). `Priority` is readable and writable from IC10.
- **Per-transformer Priority is saved with the world** (side-car XML inside the save ZIP) and synced in multiplayer.

Batteries, APCs, and rocket umbilicals (elastic storage):

- Storage devices charge only from surplus and discharge only to fill a shortfall. When generators and transformers cover the demand, batteries rest; when several batteries share a surplus, each charges a proportional share. This kills battery ping-pong and wasted round-trips.
- Charging works across the whole grid, not just locally: surplus is routed through transformers and linked wireless dish pairs to reach batteries behind them, using only the capacity left after every running machine is served. A charge request that cannot be met is simply trimmed; it never trips a deprioritization or an overload. Storage behind a high-Priority transformer charges before storage behind a low-Priority one.
- Because charge flow really crosses the grid, hover tooltips and IC10 network / throughput readings on transformers and dish pairs include it: a transformer that is only charging batteries shows that wattage as throughput instead of 0, and the numbers fall back to the idle draw once the batteries are full.
- Per-prefab charge and discharge rate caps (small / large / nuclear battery, the rocket batteries, APC cell, rocket umbilical), each also capped by the connected cable's tier rating.
- Four read-only logic values on every storage device: `MaxChargeSpeed` / `MaxDischargeSpeed` (the configured caps) and `ChargeSpeed` / `DischargeSpeed` (the actual allocated rate this tick). The earlier Import Quantity / Export Quantity exposure on batteries is removed.
- Area Power Controllers no longer leak power or drain their cell while idle, and respect cable caps on both sides.

Cable tiers and burns:

- **The three cable tiers are three separate transmission voltages.** A cable network must be all one tier; a wrong-tier junction burns the lower-tier cable once power flows. Transformers and Area Power Controllers are the bridges. Generators and stationary batteries belong on heavy cable; high-draw machines on heavy or normal; everything else on normal; super-heavy is the long-haul backbone (cables and transformers only).
- **Cable Watts caps are configurable per tier** (normal 5000, heavy 100000, super-heavy unlimited by default; 0 = unlimited) and are enforced at runtime only: the mod never modifies the cables in your save, so removing it restores vanilla ratings everywhere.
- **Only direct generator overflow burns cables.** When transformer or battery output pushes a cable past its rating, the upstream devices trip into the cable-overloaded fault instead; the cable survives and the fault tells you what to fix. A generator-overflow burn is deterministic: when a network's generator output, averaged over the last ten seconds, exceeds the weakest cable's rating, the cable nearest the top-producing generator burns. There is no random chance and no burn-factor setting.
- **Burned-cable tooltips tell you why** ("Burned: overloaded...", "Burned: wrong voltage..."), and the reason survives save / load.
- **Placement UX:** placing a wrong-tier cable next to an existing device or network shows a red ghost with a "Wrong voltage" tooltip. Placing a device on a wrong-tier cable is allowed; the cable burns when power flows.

Logic passthrough:

- **Bridging devices can be made logic-transparent.** A transformer, stationary battery, or Area Power Controller splits a power network into two sides, and normally an IC10 chip or logic reader on one side cannot see the devices on the other. Turn passthrough on and that wall disappears for logic: a reader sees through the bridge, and through a whole chain of bridges, with no extra data cable. A linked Microwave Power Transmitter and Receiver pair bridges the same way across its wireless link.
- **A writable `LogicPassthroughMode` logic value controls it per device** (1 = transparent, 0 = vanilla opaque), saved with the world. Passthrough support itself is always on; there is no master kill-switch. Six per-family Passthrough Default settings seed the mode of a device that was never explicitly set (newly built, or an existing save running the mod for the first time); a device's own stored mode wins once set. The APC has no logic port, so its mode comes from the config default only. Fault state never affects logic passthrough; power and logic are independent lanes.

Emergency lights:

- Wall Light (Battery) devices act as emergency backup lights: off while the grid powers them, on (from their internal cell) when grid power fails. A per-light Mode toggle opts a light out, and the prefab list is configurable for modded battery lights. If the third-party Battery Backup Light mod is installed, Power Grid Plus yields to it.

Charging and delivery devices:

- Devices whose job happens when power arrives work under the new tick exactly as before: the Omni Power Transmitter charges wireless batteries (with distance falloff), a Suit Storage recharges the suit stored in it, the Battery Cell Charger fills its slotted cells, the Powered Bench feeds its appliances, and the Wall Light Battery tops up its internal cell from grid surplus (which is also what tells the emergency-light logic the grid is up).
- Modded devices of the same kind can be enrolled with the Extra Delivery Devices setting; at world load the log names every third-party candidate it finds.

Diagnostics and self-checks:

- A conservation check audits the allocator every tick: per cable network, granted inflow must match granted outflow, and every transformer / dish pair / APC must bill its input exactly what it passes downstream plus its own draw. A violation logs a throttled warning with a per-component breakdown; it always indicates a mod bug worth reporting, never a problem with your base. Always on; there is no setting to disable it.
- Stale wireless transfer debts and credits saved into a world by earlier versions are zeroed on load, so a dish pair no longer starts with free transfer credit after loading an old save, and the vanilla billing ledger is kept in bounds every tick from then on.
- Unrecognised two-port power devices from other mods are listed once in the log at world load and left on vanilla behaviour, so a modded bridge degrades gracefully instead of silently misbehaving.

Multiplayer:

- Faults are decided on the host and streamed to clients as per-tick full snapshots, so client visuals (flash + countdown) always match the host within a tick and self-heal after packet loss. A client joining mid-fault sees the correct remaining countdown immediately.
- Allocation ordering uses integer-only sort keys, so peers walking the same grid always agree.

## Settings

All settings are server-authoritative: in multiplayer the host's values apply for everyone. Settings are read at world load; a mid-session change takes effect after a reload.

| Section | Setting | Default | Effect |
|---|---|---|---|
| Server - Cable Simulation | Normal Cable Max Watts | 5000 | Watts cap for normal cable. 0 = unlimited. Runtime-enforced, never written to the save. |
| Server - Cable Simulation | Heavy Cable Max Watts | 100000 | Watts cap for heavy cable (vanilla value). 0 = unlimited. |
| Server - Cable Simulation | Super Heavy Cable Max Watts | 0 | Watts cap for super-heavy cable. Default 0 = unlimited (the backbone never burns). |
| Server - Cable Costs | Super-Heavy Cable Cost Multiplier | 2.0 | Multiplies the super-heavy cable coil recipe cost. 1.0 = vanilla. Requires restart. |
| Server - Voltage Tiers | Extra Heavy-Cable Devices | (empty) | Comma-separated prefab names of extra devices allowed on heavy cable (for modded high-draw machines). |
| Server - Compatibility | Extra Delivery Devices | (empty) | Comma-separated prefab names of extra devices that receive their granted power through ReceivePower each tick, on top of the built-in five (Omni Power Transmitter, Suit Storage, Battery Cell Charger, Powered Bench, Wall Light Battery). For modded charging devices; the load log names candidates. |
| Server - Batteries | Station Battery Charge Rate | 5000 | Small battery charge cap (W). |
| Server - Batteries | Station Battery Discharge Rate | 10000 | Small battery discharge cap (W). |
| Server - Batteries | Large Station Battery Charge Rate | 25000 | Large battery charge cap (W). |
| Server - Batteries | Large Station Battery Discharge Rate | 50000 | Large battery discharge cap (W). |
| Server - Batteries | Nuclear Battery Charge Rate | 25000 | Nuclear battery charge cap (W, third-party MorePowerMod). |
| Server - Batteries | Nuclear Battery Discharge Rate | 50000 | Nuclear battery discharge cap (W). |
| Server - Batteries | Rocket Battery (Medium) Charge Rate | 5000 | Rocket Battery (Medium) charge cap (W). |
| Server - Batteries | Rocket Battery (Medium) Discharge Rate | 10000 | Rocket Battery (Medium) discharge cap (W). |
| Server - Batteries | Auxiliary Rocket Battery Charge Rate | 2500 | Auxiliary Rocket Battery charge cap (W). |
| Server - Batteries | Auxiliary Rocket Battery Discharge Rate | 5000 | Auxiliary Rocket Battery discharge cap (W). |
| Server - Batteries | Battery Charge Efficiency | 1.5 | Grid energy a battery draws per unit stored. 1.0 = lossless; the default 1.5 stores two thirds of the draw. Values below 1.0 count as 1.0; post-loss trickles under 500 W store in full. |
| Server - Batteries | Enable Battery Logic Additions | true | Expose the four soft-power logic values on batteries. |
| Server - Batteries | Battery Passthrough Default | true | Passthrough mode a battery starts with while its own mode was never set. |
| Server - Transformers | Enable Transformer Logic Additions | true | Expose transformer throughput as Power Actual. |
| Server - Transformers | Small Transformer Passthrough Default | true | Passthrough mode the three small transformer prefabs start with while their own mode was never set. |
| Server - Transformers | Other Transformer Passthrough Default | false | Passthrough mode every other transformer variant starts with while its own mode was never set. |
| Server - Area Power Control | APC Battery Charge Rate | 1000 | APC cell charge cap (W). |
| Server - Area Power Control | APC Battery Discharge Rate | 1000 | APC cell discharge cap (W). |
| Server - Area Power Control | APC Passthrough Default | true | Passthrough mode an APC starts with. The APC has no logic port, so the config default is its only seed. |
| Server - Power Transmitters | Power Transmitter Passthrough Default | true | Passthrough mode a linked dish pair starts with while its own mode was never set. |
| Server - Rocket Umbilical | Rocket Umbilical Charge Rate | 10000 | Umbilical charge cap (W). |
| Server - Rocket Umbilical | Rocket Umbilical Discharge Rate | 10000 | Umbilical discharge cap (W). |
| Server - Rocket Umbilical | Umbilical Passthrough Default | true | Passthrough mode an umbilical half starts with while its own mode was never set. |
| Server - Emergency Lights | Enable Wall Light Battery Emergency Mode | true | Battery wall lights act as emergency backup lights. |
| Server - Emergency Lights | Emergency Light Prefabs | StructureWallLightBattery | Comma-separated prefab names that get the emergency behaviour. |

Always-on behaviour with no toggle: voltage tiers, cycle faults, producer isolation (the current-mismatch rule), the deterministic cable-burn rule, per-prefab battery rate limits, transformer priority and deprioritization, device overload protection, rocket umbilical rate limits, the per-tick conservation audit, the transformer free-power exploit fix, the Area Power Control power fix, device Powered ownership (the network's state plus the device's own switch decide every device's Powered flag, so a demand spike cannot reboot a printer, and IC10 Power reads the network's state), the power delivery to charging devices (the built-in five above; the Extra Delivery Devices setting only extends the list), the powered presentation for idle healthy bridges, the wireless ledger cleanup, and logic-passthrough support (each device's LogicPassthroughMode stays player-controlled; the six Passthrough Default settings above seed devices whose mode was never set). These are the core of the redesigned grid.

## How it works

Think of the grid as rooms joined by doors. Each cable network is a room. Transformers, linked wireless dishes, and Area Power Controllers are the doors between rooms, and power flows through them from one network to the next. Generators put power into a room, machines and lights and IC10 housings take it out, and batteries store it.

Power is pulled, not pushed. Vanilla solves each network on its own and lets a short network borrow power a tick before its supplier actually delivers it, which is the cause of the flicker and the half-powered machines. Power Grid Plus instead looks at the whole grid at once, twice a second, and works backward from the machines that need power to the generators that make it:

1. **Look:** add up what every device wants and what every generator is making, on every network.
2. **Protect:** check for trouble first, power loops and cables carrying the wrong voltage tier.
3. **Share:** each door works out how much its far side needs and asks its near side for exactly that much, and the request flows back through the doors until it reaches the generators. Battery-charging requests ride the same flow, capped so they can never crowd out a running machine.
4. **Deliver:** push the agreed amounts forward, generators to machines, so nothing lags a tick behind.

When a network cannot cover everything asking for power, the lowest-Priority doors are switched fully off (deprioritized) until the rest can run at full strength, so whole rooms go dark rather than every machine browning out. When a transformer is already passing its full rated throughput and the rooms behind it still want more, it trips the device-overloaded fault, a breaker telling you that door needs a bigger transformer or the load split across more of them. When the transformers could carry the load but the wire between the rooms cannot, the suppliers trip the cable-overloaded fault instead of burning the cable. Batteries fill only the shortfall left after generators and transformers, and recharge only from leftover power, which reaches them through transformers and wireless links like any other flow, so they never ping-pong. A charge request that cannot be met is quietly trimmed; only real machines going short can trip a fault.

The result is a grid where a machine is either fully powered or cleanly off, supply always reaches a network before it is spent, and the protections act like breakers and priorities you can reason about instead of random brownouts.

Under the hood this is three layers: a per-tick snapshot reads the whole grid exactly once (topology and every device's demand and output); a global allocator solves every network at once (the piece vanilla cannot express, since the base game ticks each network in isolation); and a write-back applies the converged result (delivered energy, stored charge, the Powered flags, the network readouts). The vanilla per-network power flow is not called at all.

### The tick pipeline

The electricity tick is replaced by one driver (`AtomicElectricityTickPatch`) that runs the pipeline in order. Reading that file top to bottom is reading the whole flow.

- **Snapshot.** One pass over the grid builds the tick's model: network membership (read under the game's own locks), every device's power draw and output (sampled exactly once, so a device changing its mind mid-tick cannot tear the solve), storage levels, and each bridge device's physics. On the first tick after a world load, stale wireless billing ledgers from the save are zeroed and unrecognised modded bridge devices are inventoried first.
- **Protect.** Wrong-tier cable burns fire first, then cycle detection faults every device on a closed power loop, then producer isolation faults any producer sharing a network with anything but producers and transformers. Newly faulted producers are zeroed in the snapshot so the allocator solves the corrected grid in the same tick.
- **Allocate.** The global allocator decides every flow, which devices are deprioritized (lowest Priority first when an input runs short, leaf networks before mid-chain hops), which overload (demand still unmet at full output), and each network's live-or-dark verdict. Per-tick fault snapshots to clients are sent here.
- **Write back.** The results are applied in one pass: machines are billed exactly what the allocator granted, batteries and cells charge and discharge by exactly their shares, charging devices (omni transmitters, suit storages, cell chargers, benches, battery wall lights) receive their granted power, fuses blow when flow exceeds their rating, sustained generator overflow burns the cable at the top producer, the network readouts (the analyser and IC10 values) are filled, and every device's Powered flag is asserted from its network's verdict and its own switch.
- **Devices.** Every powered thing runs its per-device tick: battery charge state, generator fuel, and any other mod's device-tick patch.
- **Logic.** IC10 chips execute on the vanilla schedule.

### Where it lives in the source

- `AtomicElectricityTickPatch` is the pipeline driver and the single entry point.
- `Core/GridSnapshot` is the snapshot: topology plus the single boundary read, with `Core/DemandModel` handling the accumulator-driven machines (fabricators, furnaces) so their billed work is drained exactly once on the thread that owns it (`Core/MainThreadDebitQueue`).
- `PowerAllocator` is the solve: the three flow classes (rigid machine demand, storage charge, storage discharge), deprioritization, overload, and the integer-keyed ordering that keeps multiplayer peers in agreement. `SegAdapters` describes each bridge device class (transformer, linked dish pair, APC, rocket umbilical) to the allocator through one contract, and `PowerTransmitterPlusInterop` handles the PowerTransmitterPlus tiers and the billing handshake.
- `Core/WriteBack` applies the plan: energy settlement, the delivery to charging devices (`DeliveryEffectClassifier` picks them; `ReceivePowerOverrideCensus` names modded candidates at load), fuses, the generator-overflow burn, and the network readouts; `PoweredOwnership` and `PoweredPresentation` assert the Powered flags, and `PowerLogicReadPatches` serves the IC10 Power read from the network verdict.
- Five registries hold the transient fault lockouts (deprioritization, device overload, cable overload, cycle, current mismatch). They are cleared on save load and recomputed from live topology on the first tick.
- The detectors are separate from the registries: `CycleGraphBuilder` finds power loops, `CurrentMismatchFaultDetector` finds unprotected producers, and `VoltageTierEnforcer` with `CableBurnWindow` handles the two kinds of cable burn (wrong-tier and generator overflow).
- The remaining device patches are presentation: they serve the allocator's published totals to tooltips, hovers, and the logic values.
- `LedgerAdoption`, `ConservationChecker`, and `UnknownBridgeCensus` are the self-check layer: the wireless-ledger cleanup, the per-tick conservation audit, and the modded-bridge inventory.

Full algorithm specifications, invariants, and the decision log are in the repo-root [POWER.md](../../POWER.md) and the mod-local `RESEARCH.md`.

## Compatibility

**Requires:** [BepInEx](https://docs.bepinex.dev/) and [StationeersLaunchPad](https://github.com/StationeersLaunchPad/StationeersLaunchPad).

All players on a server must have Power Grid Plus installed; the version is checked during the connection handshake. Dedicated servers need the same BepInEx + StationeersLaunchPad + Power Grid Plus setup.

Known interaction: [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) itself swaps the power tick per network the same way this mod does, so running both is not supported and the mod refuses to load alongside it. MorePowerMod's nuclear battery is supported with its own rate-cap settings. PowerTransmitterPlus is fully compatible; linked dishes participate in dispatch as transformer-like devices using whichever distance-cost model is active. With PowerTransmitterPlus 1.9.0 or newer the two mods perform a billing handshake at load: Power Grid Plus takes over the wireless billing (PowerTransmitterPlus's own transfer-debt billing stands down) while the beam visuals, link handling, and capacity settings stay with PowerTransmitterPlus; the Workshop 1.8.0 build keeps working through the previous integration, and without PowerTransmitterPlus the vanilla wireless model applies. Custom two-port power devices from other mods are inventoried in the log at world load and left on vanilla behaviour.

## Reporting Issues

If you run into a bug or something behaves unexpectedly, please open an issue on [GitHub](https://github.com/SixFive7/StationeersPlus/issues). Please include the mod name in the title so reports can be triaged. Steam comment notifications don't always come through, so GitHub is the reliable way to make sure a report is seen.

## Credits

Built in part on the power-simulation work in [Re-Volt](https://github.com/sukasa/revolt) by Sukasa, used under the MIT License. The Re-Volt-derived portions retain their MIT notice; see the repository `NOTICE`. The emergency-light behaviour is inspired by alliephante's Battery Backup Light (MIT).

## License

Licensed under the [Apache License 2.0](https://github.com/SixFive7/StationeersPlus/blob/master/LICENSE). Attribution required; see the LICENSE and NOTICE files at the repository root. The portions derived from Re-Volt remain under their original MIT License (Copyright (c) 2025 Sukasa).
