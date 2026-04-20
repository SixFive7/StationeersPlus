---
title: SprayCan
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/SprayPaintPlus/RESEARCH.md:94-98
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Items.SprayCan
related: []
tags: [prefab]
---

# SprayCan

Vanilla game class representing a spray-paint can consumable. The game ships one `SprayCan` prefab per color.

## Fields
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0015.

- `PaintMaterial` / `PaintableMaterial`: The `Material` representing the can's current color. The game has one `SprayCan` prefab per color.
- `Thumbnail`: The `Sprite` shown in the inventory slot. Tied to the prefab, so switching color requires updating it manually.
- `Quantity`: Decremented on each use. Setting it to 0 before vanilla runs effectively makes the can infinite.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0015. No conflicts.

## Open questions

None at creation.
