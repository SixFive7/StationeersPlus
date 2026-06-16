# Power Grid Plus

Reworks Stationeers wired power: an atomic power tick with priority-based transformer dispatch, automatic shedding and overload lockouts, loop and producer-isolation faults with on-device countdowns, elastic battery allocation, three cable tiers as separate transmission voltages, logic-transparent transformer / battery / dish bridges, and deterministic multiplayer cable-network ids.

Work in progress. Not on the Workshop yet. A pure-patch mod: no new craftable devices, no asset bundle. Parts of the simulation are derived from Sukasa's [Re-Volt](https://github.com/sukasa/revolt) (MIT). The full power-system design, invariants, and algorithm specifications live in the repo-root [POWER.md](../../POWER.md). Mod-local design notes and the decision log live in `RESEARCH.md`; pending work lives in `TODO.md`.

## What it does

The atomic power tick:

- Replaces the outer electricity tick with a five-phase flow: observe every network, decide every fault and allocation globally, then run the vanilla per-network power flow with those decisions already in effect. Decisions made in a tick apply in that same tick, so there is no one-tick lag and no flicker.
- The inner per-network simulation is the vanilla PowerTick, unmodified. Power Grid Plus changes behaviour through device-level patches and the global allocator, not by replacing the simulation core.
- Vanilla partial-power scaling is kept as a safety net: if a configuration slips past every Power Grid Plus rule (a future game update, an unclassified modded device), the grid degrades the vanilla way instead of misbehaving.

Faults. A power device that cannot do its job enters a visible 60-second fault lockout instead of silently misbehaving. Four fault types, each with its own on-device feedback:

- **Shedding** (orange flash): a transformer (or other bridging device) cannot get its full draw from its input network. When siblings compete for a short input, the lowest Priority sheds first. While shed it contributes 0 to its output network, so the subnet goes dark cleanly instead of every device flickering.
- **Overloaded** (red flash): a device is delivering everything its Setting allows while downstream demand stays unmet, or a battery is discharging at its full effective rate with demand still unmet. The bottleneck device is the one that trips, so you know what to upgrade or split.
- **Cycle Fault** (red flash): the device is part of a closed power loop. Every bridging device on the loop faults and contributes 0, which dissolves the loop. No cable is burned for loops.
- **Variable Voltage Fault** (red flash): a power producer (solar, wind, generator, RTG, the small wall turbine, or a portable generator plugged into a power connector) is wired to consumers without a transformer in between. Producers may only share a network with other producers and transformers; everything else must sit behind a transformer. Producers without a button (solar, wind, RTG) stop generating and explain themselves in their hover text. An unrecognised modded producer in the same situation falls back to burning the cable next to the violating consumer. Every producer with a logic port (so not the RTG or the bare Power Connector dock) exposes a read-only `VariableVoltageFault` logic value, 1 while faulted, for IC10.
- Every faulted device shows the cause and a live countdown in its hover text (for example `(Shedding: Insufficient upstream supply! 42.17s)`). Toggling the device off clears the fault instantly; toggling back on re-evaluates, and the fault re-fires if the cause is still there.
- Faults are transient: they clear on save load and recompute from the live topology on the first tick.
- Not every dark subnet is a fault: a contributor whose input network has no power source at all simply idles, with a steady grey `(No upstream supply)` hover (no flash, no countdown, no lockout). Power its input and it delivers again immediately.

Transformer priority and dispatch:

- **The vanilla Setting value is the throughput cap again.** IC10 reads and writes work exactly as in the base game, clamped 0..OutputMaximum. A freshly built transformer starts at Setting = OutputMaximum (full throughput); a saved world keeps its saved Settings. A transformer with Setting 0 deliberately delivers nothing (logged at load so a dark subnet is explainable).
- **The in-world knob sets Priority instead** (non-negative integer, no upper cap, default 100, step 10 per screwdriver click or 1 with Alt). The Labeller tool also sets Priority. Priority decides who sheds first when an input network runs short; comparisons are local to each input network.
- **Priority also ranks dispatch, not just shedding.** When several transformers feed one output network, higher-Priority transformers carry the load first and lower-Priority ones act as backup, picking up only what the higher ones cannot. Transformers at the same Priority share the load in proportion to their capacity (a large and a small transformer split by size, not evenly).
- **Read-only logic values** `Shedding`, `Overloaded`, and `CycleFault` return 1 while the matching lockout is active. `Priority` is readable and writable from IC10.
- **Per-transformer Priority is saved with the world** (side-car XML inside the save ZIP) and synced in multiplayer.

Batteries, APCs, and rocket umbilicals (elastic storage):

- Storage devices charge only from surplus and discharge only to fill a shortfall. When generators and transformers cover the demand, batteries rest; when several batteries share a surplus, each charges a proportional share. This kills battery ping-pong and wasted round-trips.
- Per-prefab charge and discharge rate caps (small / large / nuclear battery, APC cell, rocket umbilical), each also capped by the connected cable's tier rating.
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

Multiplayer:

- Faults are decided on the host and streamed to clients as per-tick full snapshots, so client visuals (flash + countdown) always match the host within a tick and self-heal after packet loss. A client joining mid-fault sees the correct remaining countdown immediately.
- Allocation ordering uses integer-only sort keys, so peers walking the same grid always agree.

## Settings

All settings are server-authoritative: in multiplayer the host's values apply for everyone. Settings are read at world load; a mid-session change takes effect after a reload.

| Section | Setting | Default | Effect |
|---|---|---|---|
| Cable Simulation | Normal Cable Max Watts | 5000 | Watts cap for normal cable. 0 = unlimited. Runtime-enforced, never written to the save. |
| Cable Simulation | Heavy Cable Max Watts | 100000 | Watts cap for heavy cable (vanilla value). 0 = unlimited. |
| Cable Simulation | Super Heavy Cable Max Watts | 0 | Watts cap for super-heavy cable. Default 0 = unlimited (the backbone never burns). |
| Cable Costs | Super-Heavy Cable Cost Multiplier | 2.0 | Multiplies the super-heavy cable coil recipe cost. 1.0 = vanilla. |
| Voltage Tiers | Extra Heavy-Cable Devices | (empty) | Comma-separated prefab names of extra devices allowed on heavy cable (for modded high-draw machines). |
| Batteries | Enable Battery Limits | true | Charge/discharge-rate limit stationary batteries. |
| Batteries | Station Battery Charge Rate | 5000 | Small battery charge cap (W). |
| Batteries | Station Battery Discharge Rate | 10000 | Small battery discharge cap (W). |
| Batteries | Large Station Battery Charge Rate | 25000 | Large battery charge cap (W). |
| Batteries | Large Station Battery Discharge Rate | 50000 | Large battery discharge cap (W). |
| Batteries | Nuclear Battery Charge Rate | 25000 | Nuclear battery charge cap (W, third-party MorePowerMod). |
| Batteries | Nuclear Battery Discharge Rate | 50000 | Nuclear battery discharge cap (W). |
| Batteries | Battery Charge Efficiency | 1.0 | Fraction of incoming power stored. |
| Batteries | Enable Battery Logic Additions | true | Expose the four soft-power logic values on batteries. |
| Batteries | Enable Battery Logic Passthrough | true | Master toggle for battery logic passthrough. |
| Transformers | Enable Transformer Exploit Mitigation | true | Close the transformer free-power exploit. |
| Transformers | Enable Transformer Logic Additions | true | Expose transformer throughput as Power Actual. |
| Transformers | Enable Transformer Logic Passthrough | true | Master toggle for transformer logic passthrough. |
| Transformers | Enable Transformer Shedding | true | Priority dispatch and shed lockouts. Off restores vanilla input-side behaviour. |
| Transformers | Enable Transformer Overload Protection | true | Overload lockouts (including the cable-overflow trip). Off restores vanilla partial power. |
| Area Power Control | Enable APC Power Fix | true | Stop the APC power leak and idle battery drain; apply cable caps. |
| Area Power Control | APC Battery Charge Rate | 1000 | APC cell charge cap (W). |
| Area Power Control | APC Battery Discharge Rate | 1000 | APC cell discharge cap (W). |
| Area Power Control | Enable APC Logic Passthrough | true | APCs are logic-transparent. |
| Power Transmitters | Enable Power Transmitter Logic Passthrough | true | Master toggle for transmitter / receiver logic passthrough. |
| Rocket Umbilical | Enable Rocket Umbilical Limits | true | Rate caps + the four soft-power logic values on the umbilical pair. |
| Rocket Umbilical | Rocket Umbilical Charge Rate | 10000 | Umbilical charge cap (W). |
| Rocket Umbilical | Rocket Umbilical Discharge Rate | 10000 | Umbilical discharge cap (W). |
| Emergency Lights | Enable Wall Light Battery Emergency Mode | true | Battery wall lights act as emergency backup lights. |
| Emergency Lights | Emergency Light Prefabs | StructureWallLightBattery | Comma-separated prefab names that get the emergency behaviour. |

Always-on behaviour with no toggle: voltage tiers, cycle faults, and producer isolation (the Variable Voltage Fault rule). These are the core of the redesigned grid.

## How it works

The power simulation is built in three layers, and the layering is the fastest way to read the source. Nothing here rewrites the vanilla power math.

1. **The vanilla per-network simulation runs unmodified.** Each cable network still ticks through the base game's `PowerTick`: observe demand and supply, then distribute power and burn anything over its rating. Power Grid Plus never replaces that core.
2. **Device-level patches change what each device reports.** A transformer, battery, Area Power Controller, or umbilical that is shed, overloaded, or faulted reports 0 power for that tick through a small patch on its power methods. The vanilla simulation then sees a grid that already reflects every decision without knowing the decisions were made elsewhere.
3. **A global allocator runs once per tick, across every network at once.** This is the piece vanilla cannot express: the base game ticks each network independently, so no per-network code can weigh a transformer against its siblings or balance demand across the whole grid. The allocator sits between observing every network and enforcing the result.

### The five-phase tick

The outer electricity tick is replaced by one driver (`AtomicElectricityTickPatch`) that runs five phases in order. Reading that file top to bottom is reading the whole flow; each phase hands off to one registry, detector, or patch.

- **Phase 1, observe.** Initialise and calculate every network from current state, populating each network's required and potential power from this tick's device readings. Burn candidates are cleared so the tick starts clean.
- **Phase 1.5, faults.** Wrong-tier cable burns fire first, then cycle detection faults every device on a closed power loop, then producer isolation faults any generator wired to consumers without a transformer. If anything is newly faulted, the networks are re-observed so the next phase sees the corrected grid.
- **Phase 2, decide.** The global allocator reads every network's required and potential power, decides which devices shed (lowest Priority first when an input runs short) and which overload (demand still unmet at full output), and records the lockouts. Per-tick fault snapshots to clients are sent here.
- **Phase 3, enforce.** Initialise, calculate, and apply every network again. The second calculation reads the fresh lockout flags through the device patches, so locked-out devices contribute 0 and vanilla distributes power and burns overloaded cables with every decision already in effect. Decisions made this tick take effect this tick: no one-tick lag, no flicker.
- **Phase 4, devices.** Every powered thing runs its per-device tick: battery charge state, generator fuel, and any other mod's device-tick patch.
- **Phase 5, logic.** IC10 chips execute on the vanilla schedule.

### Where it lives in the source

- `AtomicElectricityTickPatch` is the five-phase driver and the single entry point.
- `PowerAllocator` is Phase 2: shedding, overload, and the integer-keyed ordering that keeps multiplayer peers in agreement.
- Four registries hold the transient fault lockouts (shedding, overload, cycle, variable voltage). They are cleared on save load and recomputed from live topology on the first tick.
- The detectors are separate from the registries: `CycleGraphBuilder` finds power loops, `VariableVoltageFaultDetector` finds unprotected producers, and `VoltageTierEnforcer` with `CableBurnWindow` handles the two kinds of cable burn (wrong-tier and generator overflow).
- The device patches (battery, transformer, Area Power Control, rocket umbilical) are where a lockout becomes a 0-power reading, and where elastic storage and the soft-power logic values live.

Full algorithm specifications, invariants, and the decision log are in the repo-root [POWER.md](../../POWER.md) and the mod-local `RESEARCH.md`.

## Compatibility

**Requires:** [BepInEx](https://docs.bepinex.dev/) and [StationeersLaunchPad](https://github.com/StationeersLaunchPad/StationeersLaunchPad).

All players on a server must have Power Grid Plus installed; the version is checked during the connection handshake. Dedicated servers need the same BepInEx + StationeersLaunchPad + Power Grid Plus setup.

Known interaction: [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) itself swaps the power tick per network the same way this mod does, so running both is not supported and the mod refuses to load alongside it. MorePowerMod's nuclear battery is supported with its own rate-cap settings. PowerTransmitterPlus is fully compatible; linked dishes participate in dispatch as transformer-like devices using whichever distance-loss model is active.

## Reporting Issues

If you run into a bug or something behaves unexpectedly, please open an issue on [GitHub](https://github.com/SixFive7/StationeersPlus/issues). Please include the mod name in the title so reports can be triaged. Steam comment notifications don't always come through, so GitHub is the reliable way to make sure a report is seen.

## Credits

Built in part on the power-simulation work in [Re-Volt](https://github.com/sukasa/revolt) by Sukasa, used under the MIT License. The Re-Volt-derived portions retain their MIT notice; see the repository `NOTICE`. The emergency-light behaviour is inspired by alliephante's Battery Backup Light (MIT).

## License

Licensed under the [Apache License 2.0](https://github.com/SixFive7/StationeersPlus/blob/master/LICENSE). Attribution required; see the LICENSE and NOTICE files at the repository root. The portions derived from Re-Volt remain under their original MIT License (Copyright (c) 2025 Sukasa).
