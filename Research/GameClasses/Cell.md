---
title: Cell
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/SprayPaintPlus/RESEARCH.md:108-110
  - Mods/SprayPaintPlus/SprayPaintPlus/NetworkPainterPatch.cs:298-303
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Cell
related:
  - ./Room.md
  - ./Grid3.md
tags: [terrain]
---

# Cell

Vanilla game class representing a single voxel cell in the world grid. Used by `Room.Grids` and the room-neighbor scan.

## NeighborCells 26-cell and filter to 6
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0028.

`Cell.NeighborCells` returns all 26 surrounding cells (corners and diagonals included). The mod filters to 6 orthogonal neighbors by checking that exactly one axis of the `Grid3` difference is nonzero.

### 6-neighbor filter comment (F0353)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source comment from `NetworkPainterPatch.cs:298-303`:

```
Cell.NeighborCells contains all 26 surrounding cells (includeCorners:true in the Cell ctor). We want 6-orthogonal only: exactly one grid axis differs between the two cells.
```

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0028, F0353. No conflicts.

## Open questions

None at creation.
