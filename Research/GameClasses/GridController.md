---
title: GridController
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-19
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.GridController
related:
  - ./Cell.md
  - ./SmallCell.md
  - ./Room.md
  - ./RoomController.md
  - ./Grid3.md
  - ./Structure.md
  - ../GameSystems/PlacementOrientation.md
tags: [terrain]
---

# GridController

Global registry for the 2 m world grid: the `Cell` objects, every placed `Structure`, and a face-keyed structure lookup. The live instance is the static field `GridController.World`. Class is `Assets.Scripts.GridController : IDisposable`. Any "what is at cell X / on face F / next to here" query reads from this; structure painting, room scans, and atmospherics all go through it.

## Singletons and core collections
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

```csharp
public static GridController World;

public RoomController RoomController;

public ConcurrentDictionary<Grid3, Cell> GridCells = new ConcurrentDictionary<Grid3, Cell>();

public Dictionary<Grid3, SmallCell> SmallGridCells = new Dictionary<Grid3, SmallCell>();

public readonly Dictionary<Grid3, HashSet<Structure>> FaceLookup = new Dictionary<Grid3, HashSet<Structure>>();

private const int MAX_STRUCTURES = 65536;

public static readonly DensePool<Structure> AllStructuresPool = new DensePool<Structure>("AllStructuresPool", 65536);

private const int MAX_SERVER_TICK_STRUCTURES = 32768;
```

`GridCells` is the 2 m large-grid cell map; `SmallGridCells` is the separate 0.5 m small-grid map (pipes, cables, chutes). `AllStructuresPool` is the canonical pool of every registered `Structure`. `FaceLookup` is the face registry (see below).

## GetCell: cell lookup, O(1), no allocation
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

```csharp
public Cell GetCell(Grid3 localGrid)
{
    localGrid = new WorldGrid(localGrid).Value;
    GridCells.TryGetValue(localGrid, out var value);
    return value;
}

public Cell GetCell(WorldGrid localGrid)
{
    return GetCell(localGrid.Value);
}

public Cell GetCell(Vector3 worldPosition)
{
    Grid3 localGrid = WorldToLocalGrid(worldPosition, SmallGrid.SmallGridSize, SmallGrid.SmallGridOffset);
    ...
}
```

A pure `ConcurrentDictionary.TryGetValue`. Returns `null` for an empty cell; it does NOT create a cell on miss. Reads are cheap and trigger no recomputation. `GridCells` is a `ConcurrentDictionary` so the lookup itself is thread-safe, but the returned `Cell`'s `List` members (`AllStructures`, `NeighborCells`) are main-thread-mutated; enumerate them on the main thread.

## GetStructure: cell-resolved structure (large grid only)
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

```csharp
public Structure GetStructure(Grid3 localGrid)
{
    Cell cell = GetCell(localGrid);
    if (cell == null)
    {
        ...
    }
    ...
}
```

Resolves the cell, then the structure registered at that grid key via the cell's `Structural` dictionary (see `./Cell.md`). This is the LARGE 2 m grid only. `Structural` holds large-grid structures, so `GetStructure` does NOT return small-grid occupants (pipes, cables, chutes, small devices, rails, ladders). For those, use `GetSmallCell` + `SmallCell.Get<T>` (next section).

## GetSmallCell: small-grid lookup
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

The 0.5 m small grid lives in the separate `SmallGridCells` map and is reached through `GetSmallCell`, not `GetCell` / `GetStructure`:

```csharp
public SmallCell GetSmallCell(Vector3 worldPosition)
{
    Grid3 localGrid = WorldToLocalGrid(worldPosition, SmallGrid.SmallGridSize, SmallGrid.SmallGridOffset);
    return GetSmallCell(localGrid);
}

public SmallCell GetSmallCell(Grid3 localGrid)
{
    SmallGridCells.TryGetValue(localGrid, out var value);
    return value;
}
```

`SmallGrid.SmallGridSize` is `0.5` and `SmallGridOffset` is `0.25`. The canonical "what small-grid thing is at this position" call is the static `SmallCell.Get<T>(Vector3 worldPosition)` (and its `Grid3` overload), which fetches the `SmallCell` and returns the first slot matching `T` (`Chute` / `Pipe` / `Device` / `Cable` / `Rail` / `Other`). For example `SmallCell.Get<Ladder>(worldPos)` returns a ladder (a `SmallGrid` in the `Other` slot, including a `LadderEnd` since it is a `Ladder` subclass). See `./SmallCell.md`.

## FaceLookup: the face registry
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

A structure can mount on a cell face; one 2 m cell holds up to six face structures plus a body. `FaceLookup` maps each face-resolved `Grid3` (drawn from a structure's `BlockingGrids`) to the set of structures occupying it. This is the precise primitive for "what structures sit on this exact face plane."

```csharp
public readonly Dictionary<Grid3, HashSet<Structure>> FaceLookup = new Dictionary<Grid3, HashSet<Structure>>();

public HashSet<Structure> GetFaceStructures(Grid3 localFacePosition)
{
    FaceLookup.TryGetValue(localFacePosition, out var value);
    return value ?? EmptyStructureHashset;
}

public Structure GetFaceStructure(Vector3 worldPosition, Vector3 direction)
{
    return GetFaceStructure(WorldToLocalGrid(worldPosition), direction);
}

public Structure GetFaceStructure(Grid3 localGrid, Vector3 direction)
{
    Cell cell = GetCell(localGrid);
    ...
}
```

`GetFaceStructures` returns the live `HashSet` for a face key (or a shared empty set). The map is populated on registration:

```csharp
private void AddFaceReference(Structure structure)
{
    Grid3[] blockingGrids = structure.BlockingGrids;
    foreach (Grid3 key in blockingGrids)
    {
        if (!FaceLookup.ContainsKey(key))
        {
            FaceLookup.Add(key, new HashSet<Structure> { structure });
        }
        else
        {
            FaceLookup[key].Add(structure);
        }
    }
}
```

`RemoveFaceReference` mirrors this, removing the structure and pruning a bucket once its count hits zero.

## Neighbor enumeration
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

```csharp
public List<Grid3> GetGridNeighbours(Grid3 localGrid, bool horizontalOnly = false, bool includeCorners = false)

public void GetGridNeighbours(Grid3 localGrid, ref List<Grid3> result, bool horizontalOnly = false, bool includeCorners = false)

public void GetNeighborCells(Grid3 localGrid, ref List<Cell> cells, bool includeCorners = false)

public void GetNeighborCells(Vector3 worldPosition, ref List<Cell> cells, bool includeCorners = false)
```

`includeCorners: false` yields the 6 axis-aligned neighbors; `true` yields all 26 (corners and diagonals). `horizontalOnly` restricts to the planar four. `GetNeighborCells` resolves each neighbor grid through `GetCell`, so empty neighbors come back absent. This is the canonical 6-vs-26 control; compare `Cell.NeighborCells`, which is the precomputed all-26 list (see `./Cell.md`).

## Structure registration
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

`Register(Structure)` wires a structure into all three indices:

```csharp
AddGridStructure(structure);
AddFaceReference(structure);
UpdateAirState(structure);
```

`AddGridStructure` walks every cell the structure occupies, computed from its grid bounds:

```csharp
private void AddGridStructure(Structure structure)
{
    Cell cell = null;
    if (structure.PlacementType == PlacementSnap.Grid)
    {
        Grid3[] array = (Grid3[])structure.GridBounds.GetLocalGrid(structure.ThingTransformPosition, structure.ThingTransformRotation);
        foreach (Grid3 grid in array)
        {
            cell = GetCell(grid);
            ...
        }
    }
    ...
}
```

`Deregister` plus `RemoveFaceReference` undo it. `AllStructuresPool.Add(structure)` / `.Remove(structure)` keep the canonical pool current.

## Blocked-cell tests
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

```csharp
public bool IsBlockedGrid(Vector3 registeredPosition)
{
    Structure structure = GetStructure(registeredPosition);
    if ((bool)structure && structure.StructureCollisionType == CollisionType.BlockGrid && !structure.CanGravityPass)
    {
        ...
    }
    ...
}

public bool IsBlockedAirGrid(Grid3 localGrid)
{
    Structure structure = GetStructure(localGrid);
    if ((bool)structure && structure.StructureCollisionType == CollisionType.BlockGrid && !structure.CanAirPass)
    {
        ...
    }
    ...
}
```

"Blocked" keys off `Structure.StructureCollisionType == CollisionType.BlockGrid` plus the per-structure `CanGravityPass` / `CanAirPass` flags. Face-mounted structures use a different collision type (see the `./Cladding.md` open question on the exact value).

## Verification history

- 2026-05-22: page created from a room/grid/atmospherics decompile pass (feasibility study on networking composite cladding for SprayPaintPlus). All sections verified against `Assets.Scripts.GridController` in `Assembly-CSharp`, game version 0.2.6228.27061. New page; no conflicts.
- 2026-06-19: clarified that `GetStructure` resolves the LARGE 2 m grid only (`Cell.Structural`) and does not return small-grid occupants; added the "GetSmallCell: small-grid lookup" section (`GetSmallCell` overloads, `SmallCell.Get<T>`) while scoping ladder flood-painting for Spray Paint Plus. Additive plus one clarification; related link to `./SmallCell.md`. Source: `Assets.Scripts.GridController` (`GetSmallCell`) and `Assets.Scripts.GridSystem.SmallCell` (`Get<T>`), game version 0.2.6228.27061.

## Open questions

None at creation.
