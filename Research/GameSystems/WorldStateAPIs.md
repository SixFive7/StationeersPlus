---
title: WorldStateAPIs
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-14
sources:
  - Plans/RepairPrototype/plan.md:398-421
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 308886-308891 (LogicType.PositionX/Y/Z)
related:
  - ../GameClasses/Thing.md
  - ../GameClasses/Device.md
  - ../GameClasses/Grid3.md
  - ./DamageState.md
  - ./LogicType.md
tags: [logic, network, terrain]
---

# WorldStateAPIs

Quick-reference index of public world-state APIs: atmospheres (`AtmosphericsManager.AllAtmospheres`), pipe and cable networks, logic value read/write on `Device`, reusable vanilla LogicType values for generic fields (On, Power, Mode, etc.), and the game-state guards that mods use to gate server-only work.

## Atmosphere, pipe, cable, logic API quick reference
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`AtmosphericsManager.AllAtmospheres` static collection (may contain nulls). `atmosphere.Temperature` Kelvin, `PressureGassesAndLiquidsInPa`, `.Room` (has `RoomId`), `thing.WorldAtmosphere`. `CableNetwork.AllCableNetworks`, `PipeNetwork.AllPipeNetworks`, `network.DeviceList`, `.CableList`, `.FuseList`. Logic: `device.GetLogicValue(LogicType)` / `SetLogicValue(LogicType, double)`, `CanLogicRead/CanLogicWrite`. Existing LogicType values reusable: `On`, `Mode`, `Setting`, `Power`, `PowerActual`, `Ratio`, `Quantity`, `Temperature`, `Pressure`, `Error`, `Lock`. Game state guards: `!GameManager.IsServer`, `WorldManager.IsPaused`, `WorldManager.Instance.GameMode != GameMode.Survival`.

## Player-facing position convention: LogicType.PositionX/Y/Z in world meters
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

A device answering `LogicType.PositionX/Y/Z` returns `base.Position.x` / `.y` / `.z` (verbatim `case LogicType.PositionX: return base.Position.x;` at 0.2.6403.27689 decompile 308886-308891), i.e. raw world meters. IC10 players, autopilot scripts, and community tooling therefore treat "coordinates" as world meters; a mod that prints a position to players should format world meters (e.g. `$"({pos.x:F1}, {pos.y:F1}, {pos.z:F1})"`), not `Grid3` decimeter integers. No vanilla position-to-text formatter exists. Full coordinate-system math (decimeter `Grid3`, small-grid cell keys, snap formulas) is on [Grid3](../GameClasses/Grid3.md).

## Verification history
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0229f (Plans/RepairPrototype/plan.md:398-421).
- 2026-07-14: added the "Player-facing position convention" section (`LogicType.PositionX/Y/Z` returns `base.Position` components in world meters, decompile 308886-308891 at 0.2.6403.27689; no vanilla position-to-text formatter) from the mixed-tier cable network guard research pass. Additive; the quick-reference section keeps its 0.2.6228.27061 stamp (not re-read this pass). Cross-linked [Grid3](../GameClasses/Grid3.md).

## Open questions

None at creation.
