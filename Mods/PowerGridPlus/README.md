# Power Grid Plus

Reworks Stationeers wired power: an atomic power tick with priority-based transformer dispatch, automatic shedding and overload lockouts, loop and producer-isolation faults with on-device countdowns, elastic battery allocation, three cable tiers as separate transmission voltages, logic-transparent transformer / battery / dish bridges, and deterministic multiplayer cable-network ids.

Work in progress. Not on the Workshop yet. A pure-patch mod: no new craftable devices, no asset bundle. Parts of the simulation are derived from Sukasa's [Re-Volt](https://github.com/sukasa/revolt) (MIT). The full power-system design, invariants, and algorithm specifications live in the repo-root [POWER.md](../../POWER.md). Mod-local design notes and the decision log live in `RESEARCH.md`; pending work lives in `TODO.md`.

## What it does

The atomic power tick:

- Replaces the electricity tick outright: one snapshot of the whole grid (topology plus every device's demand and output, read exactly once per tick), the protection checks, one global allocation, then one write-back that applies the results. Decisions made in a tick apply in that same tick, so there is no one-tick lag and no flicker.
- The vanilla per-network power flow no longer runs at all. Every device's demand is sampled once, every delivery equals the allocator's grant exactly, and every joule is accounted for; the mid-tick races and partial-power artifacts of the vanilla flow are gone by construction, not patched around.
- A device is either fully powered or cleanly off. Vanilla's partial-power scaling (every machine browning out a little) has no equivalent in this model; a network that cannot carry its load goes dark as a unit with a visible fault instead.

Faults. A power device that cannot do its job enters a visible 60-second fault lockout instead of silently misbehaving. Four fault types, each with its own on-device feedback:

- **Shedding** (orange flash): a transformer (or other bridging device) cannot get its full draw from its input network. When siblings compete for a short input, the lowest Priority sheds first. While shed it contributes 0 to its output network, so the subnet goes dark cleanly instead of every device flickering.
- **Overloaded** (red flash): a device is delivering everything its Setting allows while downstream demand stays unmet, or a battery is discharging at its full effective rate with demand still unmet. The bottleneck device is the one that trips, so you know what to upgrade or split.
- **Cycle Fault** (red flash): the device is part of a closed power loop. Every bridging device on the loop faults and contributes 0, which dissolves the loop. No cable is burned for loops.
- **Variable Voltage Fault** (red flash): a power producer (solar, wind, generator, RTG, the small wall turbine, or a portable generator plugged into a power connector) shares a cable network with any device that is not another producer or a transformer. The rule is strict: batteries and other bridging devices (APCs, wireless dishes, rocket umbilicals) count as violations too, and a transformer on the network does not exempt the rest; everything except producers and transformers must sit behind a transformer. Producers without a button (solar, wind, RTG) stop generating and explain themselves in their hover text. An unrecognised modded producer in the same situation falls back to burning the cable next to the violating device. Every producer with a logic port (so not the RTG or the bare Power Connector dock) exposes a read-only `VariableVoltageFault` logic value, 1 while faulted, for IC10.
- Every faulted device shows the cause and a live countdown in its hover text (for example `(Shedding: Insufficient upstream supply! 42.17s)`). Toggling the device off clears the fault instantly; toggling back on re-evaluates, and the fault re-fires if the cause is still there.
- Faults are transient: they clear on save load and recompute from the live topology on the first tick.
- Not every dark subnet is a fault: a contributor whose input network has no power source at all simply idles, with a steady grey `(No upstream supply)` hover (no flash, no countdown, no lockout). Power its input and it delivers again immediately.
- A healthy but idle bridge stays powered. A charger transformer whose batteries are full, or an idle dish pair, reads Powered on (hover and IC10 alike) instead of vanilla's misleading "unpowered" state. Only faulted, switched-off, or dead-input bridges read unpowered, so the Powered flag means what a player thinks it means.
- A machine's own demand spike can never reboot it. The mod decides every device's Powered flag from its network's state (live, shed, overloaded, or dead), not from vanilla's per-device met-this-tick check, so a printer starting a job keeps running: either its network carries the load, or the whole subnet goes dark for 60 seconds as a unit. Powered and the on/off switch are independent as well: a switched-off device on a live network reads `Power` = 1 in IC10 (powered but off) and draws nothing; read `On` for the switch state, or turn off the Decouple Powered From On Off setting to restore the vanilla coupling.

Transformer priority and dispatch:

- **The vanilla Setting value is the throughput cap again.** IC10 reads and writes work exactly as in the base game, clamped 0..OutputMaximum. A freshly built transformer starts at Setting = OutputMaximum (full throughput); a saved world keeps its saved Settings. A transformer with Setting 0 deliberately delivers nothing (logged at load so a dark subnet is explainable).
- **The in-world knob sets Priority instead** (non-negative integer, no upper cap, default 100, step 10 per screwdriver click or 1 with Alt). The Labeller tool also sets Priority. Priority decides who sheds first when an input network runs short; comparisons are local to each input network.
- **Priority also ranks dispatch, not just shedding.** When several transformers feed one output network, higher-Priority transformers carry the load first and lower-Priority ones act as backup, picking up only what the higher ones cannot. Transformers at the same Priority share the load in proportion to their capacity (a large and a small transformer split by size, not evenly).
- **Read-only logic values** `Shedding`, `Overloaded`, and `CycleFault` return 1 while the matching lockout is active. `Priority` is readable and writable from IC10.
- **Per-transformer Priority is saved with the world** (side-car XML inside the save ZIP) and synced in multiplayer.

Batteries, APCs, and rocket umbilicals (elastic storage):

- Storage devices charge only from surplus and discharge only to fill a shortfall. When generators and transformers cover the demand, batteries rest; when several batteries share a surplus, each charges a proportional share. This kills battery ping-pong and wasted round-trips.
- Charging works across the whole grid, not just locally: surplus is routed through transformers and linked wireless dish pairs to reach batteries behind them, using only the capacity left after every running machine is served. A charge request that cannot be met is simply trimmed; it never trips a shed or an overload. Storage behind a high-Priority transformer charges before storage behind a low-Priority one.
- Because charge flow really crosses the grid, hover tooltips and IC10 network / throughput readings on transformers and dish pairs include it: a transformer that is only charging batteries shows that wattage as throughput instead of 0, and the numbers fall back to the idle draw once the batteries are full.
- Per-prefab charge and discharge rate caps (small / large / nuclear battery, the rocket batteries, APC cell, rocket umbilical), each also capped by the connected cable's tier rating.
- Four read-only logic values on every storage device: `MaxChargeSpeed` / `MaxDischargeSpeed` (the configured caps) and `ChargeSpeed` / `DischargeSpeed` (the actual allocated rate this tick). The earlier Import Quantity / Export Quantity exposure on batteries is removed.
- Area Power Controllers no longer leak power or drain their cell while idle, and respect cable caps on both sides.

Cable tiers and burns:

- **The three cable tiers are three separate transmission voltages.** A cable network must be all one tier; a wrong-tier junction burns the lower-tier cable once power flows. Transformers and Area Power Controllers are the bridges. Generators and stationary batteries belong on heavy cable; high-draw machines on heavy or normal; everything else on normal; super-heavy is the long-haul backbone (cables and transformers only).
- **Cable Watts caps are configurable per tier** (normal 5000, heavy 100000, super-heavy unlimited by default; 0 = unlimited) and are enforced at runtime only: the mod never modifies the cables in your save, so removing it restores vanilla ratings everywhere.
- **Only direct generator overflow burns cables.** When transformer or battery output pushes a cable past its rating, the upstream devices trip into Overloaded instead; the cable survives and the fault tells you what to fix. A generator-overflow burn is deterministic: when a network's generator output, averaged over the last ten seconds, exceeds the weakest cable's rating, the cable nearest the top-producing generator burns. There is no random chance and no burn-factor setting.
- **Burned-cable tooltips tell you why** ("Burned: overloaded...", "Burned: wrong voltage..."), and the reason survives save / load.
- **Placement UX:** placing a wrong-tier cable next to an existing device or network shows a red ghost with a "Wrong voltage" tooltip. Placing a device on a wrong-tier cable is allowed; the cable burns when power flows.

Logic passthrough:

- **Bridging devices can be made logic-transparent.** A transformer, stationary battery, or Area Power Controller splits a power network into two sides, and normally an IC10 chip or logic reader on one side cannot see the devices on the other. Turn passthrough on and that wall disappears for logic: a reader sees through the bridge, and through a whole chain of bridges, with no extra data cable. A linked Microwave Power Transmitter and Receiver pair bridges the same way across its wireless link.
- **A writable `LogicPassthroughMode` logic value controls it per device** (1 = transparent, 0 = vanilla opaque), with per-family server master toggles. The per-device setting is saved with the world. Fault state never affects logic passthrough; power and logic are independent lanes.

Emergency lights:

- Wall Light (Battery) devices act as emergency backup lights: off while the grid powers them, on (from their internal cell) when grid power fails. A per-light Mode toggle opts a light out, and the prefab list is configurable for modded battery lights. If the third-party Battery Backup Light mod is installed, Power Grid Plus yields to it.

Diagnostics and self-checks:

- A conservation check audits the allocator every tick: per cable network, granted inflow must match granted outflow, and every transformer / dish pair / APC must bill its input exactly what it passes downstream plus its own draw. A violation logs a throttled warning with a per-component breakdown; it always indicates a mod bug worth reporting, never a problem with your base. Can be switched off under `Server - Diagnostics`.
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
| Server - Batteries | Enable Battery Limits | true | Charge/discharge-rate limit stationary batteries. |
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
| Server - Batteries | Battery Charge Efficiency | 1.0 | Fraction of incoming power stored. |
| Server - Batteries | Enable Battery Logic Additions | true | Expose the four soft-power logic values on batteries. |
| Server - Batteries | Enable Battery Logic Passthrough | true | Master toggle for battery logic passthrough. |
| Server - Transformers | Enable Transformer Logic Additions | true | Expose transformer throughput as Power Actual. |
| Server - Transformers | Enable Transformer Logic Passthrough | true | Master toggle for transformer logic passthrough. |
| Server - Transformers | Enable Transformer Shedding | true | Priority dispatch and shed lockouts. Off restores vanilla input-side behaviour. |
| Server - Transformers | Enable Transformer Overload Protection | true | Overload lockouts (including the cable-overflow trip). Off restores vanilla partial power. |
| Server - Area Power Control | APC Battery Charge Rate | 1000 | APC cell charge cap (W). |
| Server - Area Power Control | APC Battery Discharge Rate | 1000 | APC cell discharge cap (W). |
| Server - Area Power Control | Enable APC Logic Passthrough | true | APCs are logic-transparent. |
| Server - Power Transmitters | Enable Power Transmitter Logic Passthrough | true | Master toggle for transmitter / receiver logic passthrough. |
| Server - Powered Presentation | Decouple Powered From On Off | true | Powered means "network energized", independent of the on/off switch: a switched-off device on a live network reads Power=1. Off restores the vanilla coupling. |
| Server - Rocket Umbilical | Enable Rocket Umbilical Limits | true | Rate caps + the four soft-power logic values on the umbilical pair. |
| Server - Rocket Umbilical | Rocket Umbilical Charge Rate | 10000 | Umbilical charge cap (W). |
| Server - Rocket Umbilical | Rocket Umbilical Discharge Rate | 10000 | Umbilical discharge cap (W). |
| Server - Rocket Umbilical | Enable Umbilical Logic Passthrough | true | Master toggle for docked umbilical-pair logic passthrough. |
| Server - Diagnostics | Enable Conservation Check | true | Per-tick allocator self-audit; a violation logs a throttled warning and means a mod bug, not a base problem. Costs a few microseconds per tick. |
| Server - Emergency Lights | Enable Wall Light Battery Emergency Mode | true | Battery wall lights act as emergency backup lights. |
| Server - Emergency Lights | Emergency Light Prefabs | StructureWallLightBattery | Comma-separated prefab names that get the emergency behaviour. |

Always-on behaviour with no toggle: voltage tiers, cycle faults, producer isolation (the Variable Voltage Fault rule), the deterministic cable-burn rule, the transformer free-power exploit fix, the Area Power Control power fix, device Powered ownership (the network's state decides every device's Powered flag, so a demand spike cannot reboot a printer), the powered presentation for idle healthy bridges, and the wireless ledger cleanup. These are the core of the redesigned grid.

## How it works

Think of the grid as rooms joined by doors. Each cable network is a room. Transformers, linked wireless dishes, and Area Power Controllers are the doors between rooms, and power flows through them from one network to the next. Generators put power into a room, machines and lights and IC10 housings take it out, and batteries store it.

Power is pulled, not pushed. Vanilla solves each network on its own and lets a short network borrow power a tick before its supplier actually delivers it, which is the cause of the flicker and the half-powered machines. Power Grid Plus instead looks at the whole grid at once, twice a second, and works backward from the machines that need power to the generators that make it:

1. **Look:** add up what every device wants and what every generator is making, on every network.
2. **Protect:** check for trouble first, power loops and cables carrying the wrong voltage tier.
3. **Share:** each door works out how much its far side needs and asks its near side for exactly that much, and the request flows back through the doors until it reaches the generators. Battery-charging requests ride the same flow, capped so they can never crowd out a running machine.
4. **Deliver:** push the agreed amounts forward, generators to machines, so nothing lags a tick behind.

When a network cannot cover everything asking for power, the lowest-Priority doors are switched fully off (shed) until the rest can run at full strength, so whole rooms go dark rather than every machine browning out. When a transformer is already passing its full rated throughput and the rooms behind it still want more, it trips Overloaded, a breaker telling you that door needs a bigger transformer or the load split across more of them. Batteries fill only the shortfall left after generators and transformers, and recharge only from leftover power, which reaches them through transformers and wireless links like any other flow, so they never ping-pong. A charge request that cannot be met is quietly trimmed; only real machines going short can trip a fault.

The result is a grid where a machine is either fully powered or cleanly off, supply always reaches a network before it is spent, and the protections act like breakers and priorities you can reason about instead of random brownouts.

Under the hood this is three layers: a per-tick snapshot reads the whole grid exactly once (topology and every device's demand and output); a global allocator solves every network at once (the piece vanilla cannot express, since the base game ticks each network in isolation); and a write-back applies the converged result (delivered energy, stored charge, the Powered flags, the network readouts). The vanilla per-network power flow is not called at all.

### The tick pipeline

The electricity tick is replaced by one driver (`AtomicElectricityTickPatch`) that runs the pipeline in order. Reading that file top to bottom is reading the whole flow.

- **Snapshot.** One pass over the grid builds the tick's model: network membership (read under the game's own locks), every device's power draw and output (sampled exactly once, so a device changing its mind mid-tick cannot tear the solve), storage levels, and each bridge device's physics. On the first tick after a world load, stale wireless billing ledgers from the save are zeroed and unrecognised modded bridge devices are inventoried first.
- **Protect.** Wrong-tier cable burns fire first, then cycle detection faults every device on a closed power loop, then producer isolation faults any producer sharing a network with anything but producers and transformers. Newly faulted producers are zeroed in the snapshot so the allocator solves the corrected grid in the same tick.
- **Allocate.** The global allocator decides every flow, which devices shed (lowest Priority first when an input runs short, leaf networks before mid-chain hops), which overload (demand still unmet at full output), and each network's live-or-dark verdict. Per-tick fault snapshots to clients are sent here.
- **Write back.** The results are applied in one pass: machines are billed exactly what the allocator granted, batteries and cells charge and discharge by exactly their shares, fuses blow when flow exceeds their rating, sustained generator overflow burns the cable at the top producer, the network readouts (the analyser and IC10 values) are filled, and every device's Powered flag is asserted from its network's verdict.
- **Devices.** Every powered thing runs its per-device tick: battery charge state, generator fuel, and any other mod's device-tick patch.
- **Logic.** IC10 chips execute on the vanilla schedule.

### Where it lives in the source

- `AtomicElectricityTickPatch` is the pipeline driver and the single entry point.
- `Core/GridSnapshot` is the snapshot: topology plus the single boundary read, with `Core/DemandModel` handling the accumulator-driven machines (fabricators, furnaces) so their billed work is drained exactly once on the thread that owns it (`Core/MainThreadDebitQueue`).
- `PowerAllocator` is the solve: the three flow classes (rigid machine demand, storage charge, storage discharge), shedding, overload, and the integer-keyed ordering that keeps multiplayer peers in agreement. `SegAdapters` describes each bridge device class (transformer, linked dish pair, APC, rocket umbilical) to the allocator through one contract, and `PowerTransmitterPlusInterop` handles the PowerTransmitterPlus tiers and the billing handshake.
- `Core/WriteBack` applies the plan: energy settlement, fuses, the generator-overflow burn, and the network readouts; `PoweredOwnership` and `PoweredPresentation` assert the Powered flags.
- Four registries hold the transient fault lockouts (shedding, overload, cycle, variable voltage). They are cleared on save load and recomputed from live topology on the first tick.
- The detectors are separate from the registries: `CycleGraphBuilder` finds power loops, `VariableVoltageFaultDetector` finds unprotected producers, and `VoltageTierEnforcer` with `CableBurnWindow` handles the two kinds of cable burn (wrong-tier and generator overflow).
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
