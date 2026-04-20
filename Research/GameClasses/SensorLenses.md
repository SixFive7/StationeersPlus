---
title: SensorLenses
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/EquipmentPlus/RESEARCH.md:162-166
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: SensorLenses
related:
  - ./Thing.md
  - ../Patterns/HarmonyInheritedMethods.md
tags: [equipment, slots]
---

# SensorLenses

Vanilla game class representing the sensor-lens visor equipment item. Inherits from `Item` (via `DynamicThing`). Vanilla ships with 2 Sensor Processing Unit (SPU) slots but only one active `Sensor` property.

## Fields
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0113.

- Inherits from Item (via DynamicThing).
- `Sensor` property: the currently-active `SensorProcessingUnit`. Set in `OnChildEnterInventory` to whatever chip enters. Not networked (no delta update, no join payload, no save).
- `OnOff`: power toggle. Networked through `Interactable.State`.
- Vanilla ships with 2 SPU slots. Only one chip is expected; the `Sensor` property has no cycling logic.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0113. No conflicts.

## Open questions

None at creation.
