---
title: Cell
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-22
sources:
  - Mods/SprayPaintPlus/RESEARCH.md:108-110
  - Mods/SprayPaintPlus/SprayPaintPlus/NetworkPainterPatch.cs:298-303
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.GridSystem.Cell
related:
  - ./Room.md
  - ./Grid3.md
  - ./GridController.md
  - ./Structure.md
tags: [terrain]
---

# Cell

Vanilla game class representing a single voxel cell in the world grid. Used by `Room.Grids` and the room-neighbor scan.

Fully qualified type: `Assets.Scripts.GridSystem.Cell` (decompile line 272151). Namespace differs from most game-object types under `Assets.Scripts.Objects`; mods referencing `Cell` in a Harmony patch signature must `using Assets.Scripts.GridSystem;`.

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

## Cell fields
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

Field cluster on `Cell`:

```csharp
public static readonly ConcurrentDictionary<Cell, byte> AllCells = new ConcurrentDictionary<Cell, byte>();

public GridController GridController;

public readonly WorldGrid WorldGrid;

public Vector3 Position;

public Dictionary<Grid3, Structure> Structural = new Dictionary<Grid3, Structure>();

public List<Structure> AllStructures = new List<Structure>();

public List<Cell> NeighborCells = new List<Cell>();

public Stairs Stairs;

public bool HasLight;
```

`Structural` is the face-keyed structure map (one `Grid3` key per occupied face, up to six faces plus a body per cell). `AllStructures` is the flat list of everything touching the cell, which the SprayPaintPlus wall and large-structure floods iterate. `NeighborCells` is the precomputed all-26 neighbor list (see the section above). `AllCells` is the global set of every cell.

### IsBlocked / IsBlockedLight
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

```csharp
public bool IsBlocked
{
    get
    {
        Structure structural = GetStructural(Position);
        ...
    }
}

public bool IsBlockedLight
{
    get
    {
        Structure structural = GetStructural(Position);
        ...
    }
}
```

Both resolve the structure at the cell's own `Position` via `GetStructural` and test its collision / pass flags.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0028, F0353. No conflicts.
- 2026-05-15: verified namespace `Assets.Scripts.GridSystem.Cell` (decompile line 272151); added pre-page note for mod authors patching method signatures that take `Cell`.
- 2026-05-22: added "Cell fields" section (with "IsBlocked / IsBlockedLight" subsection) from a room/grid/atmospherics decompile pass. Additive; existing sections unchanged. Source: `Assets.Scripts.GridSystem.Cell` in `Assembly-CSharp`, game version 0.2.6228.27061. Added related links to GridController, Structure.

## Open questions

None at creation.
