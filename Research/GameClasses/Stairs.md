---
title: Stairs
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-19
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Stairs
related:
  - ./Structure.md
  - ./Cell.md
  - ./SmallCell.md
tags: [prefab]
---

# Stairs

Vanilla `Stairs : Structure, ISmartRotatable` (decompile line 307764). A third grid-registration category alongside `LargeStructure` and `SmallGrid`: a stairs is a plain large-grid `Structure` (NOT a `LargeStructure`, NOT a `SmallGrid`), tracked specially by the dedicated `Cell.Stairs` slot and the `Structure.IsStairs` flag. For the wider subclass tree see `Structure.md`.

## Class shape
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

Members (decompile lines 307764-307783):

```csharp
public Transform EntryPoint;
public Transform ExitPoint;
private Grid3 _entryPosition;
private Grid3 _exitPosition;
[Header("ISmartRotation")]
public SmartRotate.ConnectionType ConnectionType = SmartRotate.ConnectionType.Exhaustive;
public int[] OpenEndsPermutation = new int[6] { 0, 1, 2, 3, 4, 5 };
public bool RequiresFrame;
public Grid3 Entry => _entryPosition;
public Grid3 Exit => _exitPosition;
```

`Entry` / `Exit` are the climb endpoints. `ISmartRotatable` (the `ConnectionType` + `OpenEndsPermutation` pair) is shared with `Ladder`. `GetStationpediaCategory` returns the Safety category. `RequiresFrame` gates placement on a mounting structure behind the stairs (`CanConstruct`, lines 307785-307807).

## Large-grid registration
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

`OnRegistered(Cell)` registers a stairs against the large `Cell` grid (contrast `SmallGrid` and ladders, which register on the 0.5 m `SmallCell` small grid, see `SmallCell.md`):

```csharp
public override void OnRegistered(Cell cell)
{
    base.OnRegistered(cell);
    if ((bool)EntryPoint && (bool)ExitPoint)
    {
        _entryPosition = base.GridController.WorldToLocalGrid(EntryPoint.position);
        _exitPosition = base.GridController.WorldToLocalGrid(ExitPoint.position);
    }
}
```

A stairs is therefore present in `Cell.AllStructures` and occupies the dedicated `Cell.Stairs` slot (see `Cell.md`). `CanConstructCell` (lines 307828-307858) reads `allStructure.IsStairs` and per-cell `StructureCollisionType` values to block overlapping placement.

## Flood / traversal implication
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

Because `Stairs : Structure` is not a `LargeStructure`, a flood keyed on `thing is LargeStructure` (the SprayPaintPlus large-structure grid flood) does not catch stairs; a sprayed stairs falls through every paint branch and only the single piece is recolored. But stairs ARE on the large grid (`Cell.AllStructures` + `Cell.NeighborCells`), so a stairs flood can reuse the same large-cell BFS used for walls and large structures, filtered by `is Stairs` (base type) instead of exact `GetType()`. This is cheaper than ladders, which are small-grid (`SmallCell`) and need a separate small-grid `OpenEnds` walk.

## Verification history

- 2026-06-19: page created while scoping ladder and stairs flood-painting for Spray Paint Plus. Verbatim from `Assets.Scripts.Objects.Stairs` (class shape, `CanConstruct`, `OnRegistered`, `CanConstructCell`), game version 0.2.6228.27061. Additive; new page. The hierarchy fact `Stairs : Structure` is also noted in `Structure.md`.

## Open questions

- Whether stacked multi-floor stairs sit in orthogonally-adjacent large cells so the 6-neighbor BFS connects them, and the correct checker parity for a stair run (the large-grid `CheckeredCheckGrid` versus an elevator-style sort-and-alternate), is not confirmed from static reading. Resolve with an InspectorPlus probe before relying on either.
