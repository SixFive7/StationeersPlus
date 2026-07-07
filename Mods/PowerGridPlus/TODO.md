# Power Grid Plus TODO

This file tracks open issues only. Entries are plain bullets, not `- [ ]` checkboxes; when an item is done, remove it rather than ticking it off. Completed work lives in git history.

Implemented changes still awaiting an in-game or dedicated-server test do not belong here; record those in `PLAYTEST.md` (same folder).

## Documentation

- **Refresh stale `Settings.cs` description strings.** The `Enable Transformer Shedding` and `Enable Battery Logic Additions` descriptions still describe pre-refactor behavior (the Setting redirect, ImportQuantity/ExportQuantity); the strings surface in the in-game settings panel and the generated `.cfg` comments. Text-only change, but it forces a rebuild, so batch it with the next code change.

## Allocator follow-ups

- **Partial-power sentinel finding: transient sub-1 ratios on served consumer nets (open defect, found 2026-07-07).** The first natural-sun 4-hour soak (12 Lunar day/night cycles) recorded ~54,000 violating net-ticks across 20 in-scope served networks (~19 percent of ticks affected). Two shapes: a recurring exactly-one-quiescent gap (Required 160 W vs Potential 150 W, ratio 0.9375, e.g. nets 597277 / 500237 / 502349, including a deterministic tick-66 boot event) suggesting a downstream segmenter's 10 W quiescent missing from an upstream advertise; and deeper dips (ratio 0.44 to 0.75) clustered at day/night transitions, suggesting a one-tick advertise lag against moving solar supply. Devices on those nets ARE ratio-scaled for those ticks (the no-partial-power contract is violated). Never observed under the frozen-noon sun, so all pre-sentinel soaks were blind to it. Root-cause forensics in progress; fix pending.

- **Batteries shed a percentage of (dis)charge as heat.** Both charge and discharge dump a configurable fraction of the throughput as heat into the surrounding atmosphere via the existing `EnergyToHeatRatio` hook (Battery inherits it from `ElectricalInputOutput`). Percentage TBD; default 0 keeps current behaviour. Applies to vanilla Battery, BatteryLarge, and the third-party Nuclear variant via the shared base class.
- **Network-wide battery charge balancing.** Every power tick, batteries on the same cable network redistribute stored charge so all sit at the same `PowerRatio` (an extra pass at the end of ALLOCATE walking each network's battery roster, averaging stored energy proportionally to capacity). Removes the "this one battery is empty while that other is full" failure mode players hit with mixed bank ages.
- **Absorb Omni Transmitter Settings.** Fold the [Omni Transmitter Settings](https://steamcommunity.com/sharedfiles/filedetails/?id=3643534844) mod's behaviour into Power Grid Plus: configurable max transmit range, distance-based charge-power falloff, and a minimum-power-per-battery floor for the vanilla Omni (Microwave) Transmitter (`PowerTransmitterOmni`, a plain single-network consumer per `Research/GameClasses/PowerTransmitterOmni.md`, not a bridge). Adds a `Server - Wireless` config section; still a pure patch. Check the source mod's license first; adapt with attribution or reimplement. Does not overlap Power Transmitter Plus (which handles the dish pair).

## Verification tasks

- **Cross-mod compatibility pass: unverified pairings remaining.** The 2026-05-22 compat pass (`Research/Unsorted/PowerGridPlusCrossModCompat.md`) live-tested MorePowerMod and PowerOverhaul against Power Grid Plus; both clean. Not yet live-testable (not subscribed): MoreCables, CableTypeSwitcher, EL Switche, Deadly Electricity, 3.6 Megawatt Battery, EGC, Super XL Wireless Battery (EGC Edit), BuffWirelessBatteries, Jigawatt Battery, RRI - Boost Da Powa, Better Power Mod, Custom Power DeepMiner, Super Structures Mod, 2x Solar Power, Realistic Solar Constants. The compat page records the code-analytical assessment and per-mod action; re-run live tests as each is subscribed. The unknown-bridge census now surfaces any third-party two-port power device at load, which is the first thing to check in each pairing.
- **Heavy-cable device whitelist: drop or extend after the draw sweep.** The hardcoded high-draw machine list in `VoltageTier.IsHighDrawMachine` (CarbonSequester, FurnaceBase, ArcFurnace, Centrifuge, Recycler, IceCrusher, HydraulicPipeBender, DeepMiner) was assembled from community consensus; only the first three are verified-by-decompile to draw over 5 kW. The InspectorPlus draw sweep is queued in `PLAYTEST.md`; adjust the whitelist when it reports.

## Pointers

- `RESEARCH.md` -- architecture (section 3 covers the atomic tick, the unified flow classes, the adapter contract, the interop handshake, and the Stage 3 policies), file walkthrough, patch catalog, decision log.
- Repo-root `POWER.md` -- the power-system algorithm spec, revised 2026-07-03 for the unified-flow rearchitecture.
- `Research/GameClasses/Cable.md`, `PowerTick.md`, `Transformer.md`, `AreaPowerControl.md`, `Battery.md`, `RocketPowerUmbilical.md`, `PowerTransmitterOmni.md`; `Research/GameSystems/StructurePlacementValidation.md`, `DevicePowerDraw.md`, `RecipeDataLoading.md`, `PowerTickThreading.md` -- the curated game internals this mod relies on.
- `Mods/PowerTransmitterPlus/` -- the sibling mod that owns the wireless dish pair (billing model, ModApi, beam visuals). Wireless-dish changes go there, not here.
- `.work/revolt-source/` -- read-only clone of Re-Volt (the upstream the device fixes derive from).
