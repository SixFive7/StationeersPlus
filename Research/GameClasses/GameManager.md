---
title: GameManager
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/SprayPaintPlus/RESEARCH.md:100-102
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: GameManager
related: []
tags: [ui]
---

# GameManager

Vanilla top-level game-manager singleton. Holds global state referenced by many subsystems.

## CustomColors list
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0027.

`GameManager.Instance.CustomColors` is a `List<CustomColor>` where each entry has a `.Normal` material. The index into this list is the canonical color identifier used throughout the mod.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0027. No conflicts.

## Open questions

None at creation.
