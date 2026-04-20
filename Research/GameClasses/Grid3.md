---
title: Grid3
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/SprayPaintPlus/RESEARCH.md:104-106
  - Mods/SprayPaintPlus/RESEARCH.md:181-183
  - Mods/SprayPaintPlus/SprayPaintPlus/NetworkPainterPatch.cs:357-371
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Grid3
related:
  - ./Cell.md
  - ./Room.md
  - ./Structure.md
  - ./Wall.md
tags: [terrain, ui]
---

# Grid3

Vanilla game type representing the scaled integer-grid coordinate system used for structure placement and room math. World coordinates are scaled by 10 (`Grid3.one`), and grid-aligned structures snap to a cell spacing derived from their `GridSize`.

## Scale and cell math
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0016.

World coordinates scaled by `Grid3.one` (10 units per world unit). Structures snap to a grid defined by their `GridSize` (2 world units for walls and large structures by default). One cell spans `GridSize * Grid3.one.x` Grid3 units (20 by default). Every grid-aligned structure's `GridPosition` is a multiple of this cell size plus a fixed offset.

## Parity trap for checkered painting
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0021.

`Grid3` scales world coordinates by 10. Walls and large structures snap to a 2-world-unit cell grid, so every grid-aligned structure's `GridPosition` is a multiple of 20 Grid3 units. Naive `(x+y+z) % 2` parity is the same for every structure. The checkered check works on the delta between two positions divided by cell size, which gives the cell-index distance. Parity of that distance is the checker answer.

### 3D checkerboard comment (F0321)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source comment from `NetworkPainterPatch.cs:357-371`:

```
/// <summary>
/// 3D checkerboard parity for grid-aligned structures. Two traps make
/// the naive `(x+y+z) & 1` on GridPosition useless here:
///   1. Grid3 scales world coords x Grid3.one (10), so one world unit
///      is ten Grid3 units.
///   2. Walls and large structures snap to a GridSize-wide cell grid
///      (default 2 world units). One cell therefore spans
///      GridSize * Grid3.one Grid3 units (20 by default), and every
///      structure's GridPosition is a multiple of 20 (+ a fixed
///      offset). Parity on raw coords is always the same value.
/// Working from the delta between the two positions sidesteps both
/// the scale and the grid offset: the delta is always an exact
/// multiple of cellSize, so integer division yields the cell-index
/// distance and its parity is the checker answer.
/// </summary>
```

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0016, F0021, F0321. No conflicts.

## Open questions

None at creation.
