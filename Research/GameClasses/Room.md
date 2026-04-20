---
title: Room
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/SprayPaintPlus/RESEARCH.md:112-114
  - Mods/SprayPaintPlus/SprayPaintPlus/NetworkPainterPatch.cs:212-215
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Room
related:
  - ./Cell.md
  - ./Wall.md
tags: [terrain]
---

# Room

Vanilla game class representing an enclosed interior volume. Holds the list of interior cells forming the sealed space.

## Room.Grids and walls on boundary
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0029.

`RoomController.World.GetRoom(gridPosition)` returns the `Room` a cell belongs to. `room.Grids` lists the room's interior cells. Walls sit on the boundary (one layer outside `room.Grids`), which is why the wall-painting code expands one neighbor layer outward.

### Wall-scan expansion comment (F0351)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source comment from `NetworkPainterPatch.cs:212-215`:

```
The room's interior cells are in room.Grids; walls sit on the boundary, so expand one layer to cover both sides.
```

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0029, F0351. No conflicts.

## Open questions

None at creation.
