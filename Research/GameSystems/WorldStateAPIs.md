---
title: WorldStateAPIs
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/RepairPrototype/plan.md:398-421
related:
  - ../GameClasses/Thing.md
  - ../GameClasses/Device.md
  - ./DamageState.md
  - ./LogicType.md
tags: [logic, network, terrain]
---

# WorldStateAPIs

Quick-reference index of public world-state APIs: atmospheres (`AtmosphericsManager.AllAtmospheres`), pipe and cable networks, logic value read/write on `Device`, reusable vanilla LogicType values for generic fields (On, Power, Mode, etc.), and the game-state guards that mods use to gate server-only work.

## Atmosphere, pipe, cable, logic API quick reference
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`AtmosphericsManager.AllAtmospheres` static collection (may contain nulls). `atmosphere.Temperature` Kelvin, `PressureGassesAndLiquidsInPa`, `.Room` (has `RoomId`), `thing.WorldAtmosphere`. `CableNetwork.AllCableNetworks`, `PipeNetwork.AllPipeNetworks`, `network.DeviceList`, `.CableList`, `.FuseList`. Logic: `device.GetLogicValue(LogicType)` / `SetLogicValue(LogicType, double)`, `CanLogicRead/CanLogicWrite`. Existing LogicType values reusable: `On`, `Mode`, `Setting`, `Power`, `PowerActual`, `Ratio`, `Quantity`, `Temperature`, `Pressure`, `Error`, `Lock`. Game state guards: `!GameManager.IsServer`, `WorldManager.IsPaused`, `WorldManager.Instance.GameMode != GameMode.Survival`.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0229f (Plans/RepairPrototype/plan.md:398-421).

## Open questions

None at creation.
