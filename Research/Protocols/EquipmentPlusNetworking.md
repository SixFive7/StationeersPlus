---
title: EquipmentPlus Networking
type: Protocols
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/EquipmentPlus/RESEARCH.md:73-82
  - Plans/EquipmentPlus/RESEARCH.md:221-227
  - Plans/EquipmentPlus/EquipmentPlus/SetActiveSensorMessage.cs:13-18
related:
  - ./LaunchPadBoosterNetworking.md
  - ./GameMessageFactory.md
  - ../GameSystems/NetworkUpdateFlags.md
  - ../GameClasses/SensorLenses.md
  - ../Patterns/HarmonyInheritedMethods.md
tags: [network, launchpad]
---

# EquipmentPlus Networking

EquipmentPlus extends SensorLenses with an active-sensor reference that must sync across the multiplayer boundary. Rather than shipping a full custom wire-format, the mod piggybacks on vanilla's `NetworkUpdateFlags` delta stream by claiming an unused flag bit (0x4000) and riding the existing `BuildUpdate` / `ProcessUpdate` / `SerializeOnJoin` / `DeserializeOnJoin` plumbing. One small custom LaunchPadBooster message (`SetActiveSensorMessage`) triggers the host-side write.

## Custom flag reservation
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Custom `NetworkUpdateFlag` 0x4000 (unused by vanilla's Thing/DynamicThing/Item hierarchy which goes up to 0x0800).

Four Harmony patches on inherited methods of `SensorLenses`:
- `BuildUpdate` Postfix: when flag 0x4000 is set, writes `Sensor.ReferenceId` (or 0) to the binary stream.
- `ProcessUpdate` Postfix: reads the reference id and sets `lenses.Sensor`.
- `SerializeOnJoin` Postfix: appends `Sensor.ReferenceId` to the join payload.
- `DeserializeOnJoin` Postfix: reads it back.

All use `TargetMethod()` and type `__instance` as `Thing` (see note in SensorLensesPatches.cs).

## Active sensor sync flow
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- Client cycles sensor: optimistic local write, then sends `SetActiveSensorMessage` to host.
- Server receives: validates lenses exist and chip is in a slot, applies `Sensor` and `OnOff`, sets flag 0x4000.
- Next `BuildUpdate`: flag 0x4000 causes `Sensor.ReferenceId` to be written to the delta stream. All clients read it in `ProcessUpdate`.
- Late-join: `SerializeOnJoin` appends `Sensor.ReferenceId`; `DeserializeOnJoin` reads it.

## SetActiveSensorMessage PowerOn semantics
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

When the cycle lands on a chip, PowerOn=true (lenses become powered if they weren't). When the cycle lands on the "off" slot, PowerOn=false so the lenses stop draining power. The server applies this authoritatively via Thing.set_OnOff, which goes through the networked Interactable state machinery.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration. Sources: F0107 (flag 0x4000 + 4 patches), F0118 (active sensor sync flow), F0369 (SetActiveSensorMessage PowerOn semantics).

## Open questions

None at creation.
