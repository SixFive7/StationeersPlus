---
title: Wall
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/SprayPaintPlus/RESEARCH.md:185-187
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Wall
related:
  - ./Structure.md
tags: [prefab]
---

# Wall

Vanilla game class for wall structures. Inherits from `LargeStructure`. Wall-painting flow must honor inheritance ordering when dispatching.

## Wall extends LargeStructure inheritance ordering
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0029f.

`Wall` extends `LargeStructure`. The wall branch in `PaintNetwork` must come first. If walls-painting is disabled for a wall target, the method returns early rather than falling through to the large-structure grid flood.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0029f. No conflicts.

## Open questions

None at creation.
