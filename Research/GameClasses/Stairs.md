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

## IsStairs, the Cell.Stairs slot, and the Entry/Exit run model
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

There is no separate `Stairwell` class in `Assembly-CSharp` (the string does not appear anywhere in the assembly); a "stairwell" is a prefab built on the `Stairs` class. The game's canonical "is this a stairs" test is `Structure.IsStairs` (decompile lines 295378-295384):

```csharp
public bool IsStairs
{
    get
    {
        if (!(GetType() == typeof(Stairs)))
            return GetType().IsSubclassOf(typeof(Stairs));
        return true;
    }
}
```

So `IsStairs` (equivalently a C# `is Stairs` test) matches `Stairs` and any subclass, covering every stairs/stairwell prefab in one check.

On registration, `Cell.Add` routes a `Stairs` into the dedicated `Cell.Stairs` slot (decompile lines 272258-272261):

```csharp
Stairs stairs = structure as Stairs;
if ((bool)stairs)
    Stairs = stairs;
```

NPC pathing walks a connected stair run through the `Entry` / `Exit` grid points, not raw cell adjacency: `GetStairExitPoint` / `GetStairEntryPoint` (decompile lines 272840-272905) recurse from a stair's `Exit` to the stair whose `Entry` matches (`cell.Stairs.Entry == startGrid`), and vice versa, chaining the run floor by floor. Because a stair climbs up AND forward, consecutive stairs are diagonally offset, so the `Entry`/`Exit` chain (not orthogonal cell adjacency) is the reliable way to enumerate a run.

## Flood / traversal implication
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

Because `Stairs : Structure` is not a `LargeStructure`, a flood keyed on `thing is LargeStructure` (the SprayPaintPlus large-structure grid flood) does not catch stairs; a sprayed stairs falls through every paint branch and only the single piece is recolored. Stairs live on the large grid (`Cell` / `GetCell`, the `Cell.Stairs` slot), not the 0.5 m small grid, so they are cheaper to reach than ladders. But a plain 6-neighbor orthogonal BFS will NOT connect a multi-floor run, because stacked stairs are diagonally offset (see the Entry/Exit model above). To walk a run, hop along the `Entry`/`Exit` chain: from a stair, take the `Cell.Stairs` occupying the cell at its `Exit` and the cell at its `Entry`, recursing. An orthogonal-neighbor scan can be layered on top to catch side-by-side stairs of the same prefab. Filter by `is Stairs` (base type) to include any stairwell subclass.

## Verification history

- 2026-06-19: page created while scoping ladder and stairs flood-painting for Spray Paint Plus. Verbatim from `Assets.Scripts.Objects.Stairs` (class shape, `CanConstruct`, `OnRegistered`, `CanConstructCell`), game version 0.2.6228.27061. Additive; new page. The hierarchy fact `Stairs : Structure` is also noted in `Structure.md`.
- 2026-06-19: added "IsStairs, the Cell.Stairs slot, and the Entry/Exit run model" section (`Structure.IsStairs` verbatim, `Cell.Add` stairs routing, the `GetStairExitPoint`/`GetStairEntryPoint` pathing chain) and corrected the "Flood / traversal implication" section: stacked stairs are diagonally offset and connect via `Entry`/`Exit`, so a plain orthogonal BFS does not walk a run. Confirmed no `Stairwell` class exists in `Assembly-CSharp`. Sources: `Structure.IsStairs`, `Cell.Add`, `Assets.Scripts.GridSystem` stair-pathing helpers, game version 0.2.6228.27061.

## Open questions

- Whether stacked multi-floor stairs sit in orthogonally-adjacent large cells so the 6-neighbor BFS connects them, and the correct checker parity for a stair run (the large-grid `CheckeredCheckGrid` versus an elevator-style sort-and-alternate), is not confirmed from static reading. Resolve with an InspectorPlus probe before relying on either.
