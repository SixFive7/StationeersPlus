---
title: SmallCell
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-14
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.GridSystem.SmallCell
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.SmallGrid
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 292987-293168 (SmallCell), 207128-207138 (GridController.GetSmallCell), 206820-206840 / 206947+ (DetatchSmallGridStructure / RemoveSmallGridStructure), 206870-206919 (AddSmallGridStructure caller context)
related:
  - ./Cell.md
  - ./GridController.md
  - ./Structure.md
  - ./Connection.md
  - ./Cable.md
  - ../GameSystems/StructureRegistration.md
tags: [terrain, prefab, power]
---

# SmallCell

Vanilla game class for one cell of the 0.5 m "small grid", the registration grid for `SmallGrid`-derived structures (pipes, cables, chutes, small devices, robotic-arm rails, ladders). Parallel to and separate from the large `Cell` grid (see `Cell.md`). Fully qualified `Assets.Scripts.GridSystem.SmallCell` (decompile line 274718), same `Assets.Scripts.GridSystem` namespace as `Cell` (namespace block spans lines 272148-274900).

## Small grid versus large Cell grid
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

Two parallel grids coexist. The large `Cell` (see `Cell.md`) carries `AllStructures`, the face-keyed `Structural` map, and a dedicated `Stairs Stairs` slot; walls and large structures are flooded by SprayPaintPlus through `Cell.AllStructures` + `Cell.NeighborCells`. The small grid is keyed at `SmallGrid.SmallGridSize = 0.5f` with `SmallGrid.SmallGridOffset = 0.25f`; each `SmallCell` holds at most one occupant per typed slot. World-to-local conversion for the small grid uses `GridController.World.WorldToLocalGrid(worldPosition, SmallGrid.SmallGridSize, SmallGrid.SmallGridOffset)` and lookup goes through `GridController.World.GetSmallCell(localPosition)`.

A `SmallGrid` structure registers in a `SmallCell`, not in `Cell.AllStructures`. That is why the mod floods pipes and cables through their dedicated `PipeNetwork` / `CableNetwork` objects, and reserves the large-cell BFS for walls and large structures only. A traversal over `SmallGrid` structures that have no network object (ladders) must therefore walk the small grid, not the large-cell `AllStructures` / `NeighborCells` lists.

## Slots
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

Field cluster on `SmallCell` (0.2.6403.27689 decompile: class 292987-293168, fields 292989-293003; verbatim-identical to the 0.2.6228.27061 excerpt at old lines 274720-274734):

```csharp
public Grid3 SmallGrid;
public Chute Chute;
public Pipe Pipe;
public Device Device;
public Cable Cable;
public SmallGrid Other;
public ISmallGridOwner Owner;
public IRoboticArmRail Rail;
```

One occupant per slot. `Other` is the generic `SmallGrid` slot: anything that is not a chute, pipe, device, cable, or rail lands here. There is exactly one `Cable` slot per cell: the grid model has no concept of two cables in one cell (see "Stacked pairs" below for what happens when a validation-bypassing spawn forces it).

## Add dispatch: plain overwrite, last writer wins
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

`SmallCell.Add(SmallGrid)` routes by concrete type (0.2.6403.27689 decompile lines 293100-293133; the Cable slot occupant write is 293100-293106), verbatim:

```csharp
public void Add(SmallGrid smallGridObjectGrid)
{
    Cable cable = smallGridObjectGrid as Cable;
    if ((bool)cable)
    {
        Cable = cable;
        return;
    }
    Chute chute = smallGridObjectGrid as Chute;
    if ((bool)chute)
    {
        Chute = chute;
        return;
    }
    Pipe pipe = smallGridObjectGrid as Pipe;
    if ((bool)pipe)
    {
        Pipe = pipe;
        return;
    }
    Device device = smallGridObjectGrid as Device;
    if ((bool)device && device.SmallCollisionType != SmallGridBlock.Covers)
    {
        Device = device;
    }
    else if (smallGridObjectGrid is IRoboticArmRail rail)
    {
        Rail = rail;
    }
    else
    {
        Other = smallGridObjectGrid;
    }
}
```

Every slot assignment is a plain overwrite, LAST WRITER WINS: registering a second occupant of the same kind replaces the reference with no error, no check, and no eviction of the old occupant's `SmallCell` back-pointer. The caller is `GridController.AddSmallGridStructure` (206870-206919), which writes the occupant and the `SmallGrid.SmallCell` back-pointer BEFORE invoking `OnRegistered(null)`; there is no occupancy check anywhere in the registration path (`_IsCollision` is cursor-side only). See [StructureRegistration](../GameSystems/StructureRegistration.md) for the full chain.

`RemoveCellObjectReferences(SmallGrid)` clears whichever slot held the structure and nulls that structure's `SmallCell` back-reference; see the dedicated section below for the reference-equality nuance.

## Lookups: Get&lt;T&gt; by position and by connection
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

The static `SmallCell.Get<T>(Grid3 localPosition)` (0.2.6403.27689 decompile lines 293005-293079) fetches the `SmallCell` and probes the slots in fixed order `Chute`, `Pipe`, `Device`, `Cable`, `Rail`, `Other`, returning the first slot whose occupant is a `T`. The `Get<T>(Grid3 localPosition, Connection connection)` overload additionally requires `IsConnected(connection)`, so it returns only the occupant that actually has an open end facing the queried connection (this is the overload `FillConnected` uses via `SmallCellRef`). `Get<T>(Vector3 worldPosition, ...)` wraps the world-to-local conversion noted above.

This connection-filtered lookup is the primitive the small-grid network builders use to find the neighbor across each open end.

`GridController.GetSmallCell` (207128-207138), both overloads verbatim:

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

Because every slot is a plain overwrite (see "Add dispatch" above), all of these lookups return the NEWEST occupant of a slot. For a cable query, `SmallCell.Get<Cable>` and `GetSmallCell(...).Cable` both return whatever single `Cable` reference the cell currently holds, which after an overwrite is the most recently registered one.

## RemoveCellObjectReferences: clears only on reference equality
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

`SmallCell.RemoveCellObjectReferences` (293135-293167), verbatim. A slot is cleared only if the departing structure IS the current slot value; a structure whose slot was overwritten by a later occupant no-ops here:

```csharp
public void RemoveCellObjectReferences(SmallGrid smallGrid)
{
    if (smallGrid == Device)
    {
        Device.SmallCell = null;
        Device = null;
    }
    if (smallGrid == Chute)
    {
        Chute.SmallCell = null;
        Chute = null;
    }
    if (smallGrid == Pipe)
    {
        Pipe.SmallCell = null;
        Pipe = null;
    }
    if (smallGrid == Cable)
    {
        Cable.SmallCell = null;
        Cable = null;
    }
    if (smallGrid == (SmallGrid)Rail)
    {
        Rail.SmallCell = null;
        Rail = null;
    }
    if (smallGrid == Other)
    {
        Other.SmallCell = null;
        Other = null;
    }
}
```

`GridController.RemoveSmallGridStructure` (206947+) inlines the same reference-equality pattern per slot; `DetatchSmallGridStructure` (206820-206840, the rocket-move path) calls `RemoveCellObjectReferences` and then prunes the cell from `SmallGridCells` when `!smallCell.IsValid()`.

## Stacked pairs: two cables in one cell
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

Note the scope: this is about two CABLES. `CableFuse : DeviceCableMounted` (decompile 392800) is NOT a `Cable`, so a fuse mounted over a cable occupies the Device slot, never the Cable slot; fuse-over-cable co-location is vanilla-legal and untouched by cable-slot overwrite semantics (observed legitimately 14 times in the Luna_mixedwire census).

Mechanically possible only via programmatic spawn (or a validation-bypassing caller such as ZoopMod's `UsePrimaryComplete` reflection): registration overwrites `smallCell.Cable` with the second cable. Consequences:

- Lookup: every grid query (`SmallCell.Get<Cable>`, `GetSmallCell(...).Cable`, `FillConnected`) returns the SECOND (most recently registered) cable. The first cable becomes invisible to grid queries while remaining alive: still in `AllStructuresPool`, still in its `CableNetwork.CableList` (so it still conducts and can still burn), still rendered, still in `Referencables`.
- Network topology: the two stacked cables do NOT merge with each other through the shared cell (`FillConnected` walks cells FACED BY OpenEnds, not the own cell), but both independently connect to the same neighbors, so they typically end up on the same network anyway via shared neighbors; each remains individually in `CableList`.
- Deconstruction order hazard: removing the SECOND cable clears `smallCell.Cable` to null (reference-equal), even though the first cable still physically occupies the cell; if the cell then has no other occupants it is pruned from `SmallGridCells`. The FIRST cable is left holding a `SmallCell` property that points at a detached (or emptied) cell record: it can no longer be found by any grid lookup, cannot be hovered or deconstructed by grid-based targeting, and its neighbors' `FillConnected` no longer see it, so a later `RebuildNetwork` BFS silently drops it from every network while `Cable.OnDestroy` for OTHER cables never re-links it. Removing the FIRST cable first is harmless to the second (the reference-equality checks no-op).
- `CableNetwork.Remove`'s unguarded `cable.SmallCell.Device` dereference (271102, see [CableNetwork](./CableNetwork.md)) stays non-null in both removal orders (the property is a stale but non-null reference), so no NullReferenceException arises from stacking alone.
- Net: the game neither crashes nor repairs; it silently misbehaves (ghost conductor, un-targetable structure, asymmetric removal). A guard mod must therefore refuse or destroy at registration time rather than rely on any vanilla self-healing.

## Multi-cell pieces: the SmallCell back-pointer is the LAST cell
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

For a multi-cell piece such as `StructureCableSuperHeavyStraight10`, `GridController.AddSmallGridStructure` assigns `smallGridObject.SmallCell = smallCell` inside its per-cell loop, so the back-pointer ends up pointing at the LAST cell iterated. `SmallCell` on `SmallGrid` is a single reference, not a list; code that treats it as "the cell of this structure" is only exact for 1-cell pieces. The full set of occupied cell keys comes from `GridBounds.GetLocalSmallGrid(RegisteredPosition, RegisteredRotation)` (one entry for the 1-cell piece; 3/5/10 for the long pieces). This is consistent with the multi-cell-footprint caveat in the ladder-flood section below.

## SmallGrid registration model
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

`SmallGrid : Structure, ISmallGrid, ITooltip, IReferencable, IEvaluable` (decompile line 293474). Members that drive small-grid connectivity:

```csharp
public List<Connection> OpenEnds = new List<Connection>();   // the connection faces
public static float SmallGridSize = 0.5f;
public static float SmallGridOffset = 0.25f;
public SmallCell SmallCell { get; set; }                      // back-reference to its cell
public bool DualRegister;
public override Vector3 CenterPosition => base.Position + Forward * 0.2f;
```

`Awake()` validates each `OpenEnds` entry and flags `HasDataConnection` for data/power-and-data ends. `RebuildGridState()` calls `SetGrids()` on every open end. Connectivity between two small-grid structures is expressed through matching `OpenEnds` connections resolved via `SmallCell.Get<T>(localPosition, connection)`. Pipe and cable networks layer a cached `Network` object on top of this; a bare `SmallGrid` such as a ladder has open ends but no network object, so its "connected run" exists only as a chain of `OpenEnds` adjacencies, not a precomputed list.

## Ladders occupy the Other slot
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

`Ladder : SmallGrid, ISmartRotatable` (decompile line 287263) and `LadderEnd : Ladder` (line 295026). Neither is a chute, pipe, device, cable, or rail, so `SmallCell.Add` routes both to `Other` (the final `else`, lines 274860-274863), and `SmallCell.Get<Ladder>` retrieves them from `Other`. For the Ladder / SmallGrid / LargeStructure hierarchy split (a ladder is a `SmallGrid`, a ladder platform is a `LargeStructure`), see `Structure.md`.

Consequence for anything that wants to walk a connected ladder run (for example a flood-paint over ladders): use the small grid. Seed from the ladder's own registered key (`origin.SmallCell.SmallGrid`) and step it in whole small cells (5 Grid3 units) along each axis, reading `GetSmallCell(key).Other as Ladder` (which matches `LadderEnd` caps, `LadderEnd : Ladder`), rather than the large-cell `NeighborCells` / `AllStructures` BFS used for walls and large structures. See the next section for why stepping the key beats probing world positions, and for the multi-cell-footprint caveat.

## Small-grid key scale, lookup conversion, and observed ladder registration
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

`SmallCell` keys are world metres scaled by `Grid3.one` (10), so one 0.5 m small cell is 5 Grid3 units and two structures stacked 2 m apart differ by 20 in the key. Verified live (InspectorPlus, a vertical ladder run): three stacked ladders at world y 283 / 281 / 279 carry `SmallCell.SmallGrid` y-keys 2835 / 2815 / 2795 (uniform minus-20 per 2 m).

`GetSmallCell(Vector3)` and `SmallCell.Get<T>(Vector3)` convert the probe position with `new Grid3(worldPosition)`, that is `Round(worldPosition * 10)` with NO `GridCenter` snapping (decompile lines 150910-150912 and 158148-158150), in contrast to `GetCell`, which normalizes through `WorldGrid`. A lookup therefore only succeeds when the probe rounds to the structure's exact registered key; probing a position even a fraction of a cell off (for example a `SmallGrid.CenterPosition`, which adds `Forward * 0.2`) snaps into a neighbouring cell and misses. Stepping a known registered key (`origin.SmallCell.SmallGrid`) in 5-unit increments is robust; deriving the key from a world position is fragile.

Observed for ladders (InspectorPlus): the registered `SmallCell.SmallGrid` anchors half a small cell above `Position` in the vertical axis (`key.y == Position.y * 10 + 5`; `key.x` / `key.z` track `Position`, not the `CenterPosition.z` that carries the 0.2 m forward offset). A ladder also occupies more than one small cell along its run axis, so a key-stepping traversal first re-encounters the seed's own cells and must skip past them (`ReferenceEquals(found, seed)`) before it reaches the next distinct ladder, and must scan a few cells (not just one) to bridge the 2 m pitch. SprayPaintPlus's ladder flood relies on all three facts.

## Verification history

- 2026-07-14 (second pass, same day): added the CableFuse scope note to "Stacked pairs" (`CableFuse : DeviceCableMounted`, decompile 392800: fuses take the Device slot, so fuse-over-cable stacks are vanilla-legal). Driving work: the mixed-tier registration guard needed to know whether fuses require a whitelist (they do not).
- 2026-06-19: page created from a small-grid registration decompile pass while scoping ladder flood-painting for Spray Paint Plus. Verbatim content from `Assets.Scripts.GridSystem.SmallCell` (slots, `Add`, `Get<T>`, `RemoveCellObjectReferences`) and `Assets.Scripts.Objects.SmallGrid` (`OpenEnds`, `SmallGridSize` / `SmallGridOffset`, `SmallCell`, `CenterPosition`, `Awake`, `RebuildGridState`), game version 0.2.6228.27061. Additive; new page, no existing content changed.
- 2026-06-19: added "Small-grid key scale, lookup conversion, and observed ladder registration" from a live InspectorPlus ladder-run capture plus the `GetSmallCell` / `Get<T>` conversion path (`new Grid3(worldPos)` = `Round(world * 10)`, no snapping; decompile 150910-150912, 158148-158150). Corrected the ladder-run consequence to key-stepping (verified working in-game) in place of the earlier unverified `OpenEnds`-walk suggestion. Resolved the prior open question on stacked-ladder small-cell pitch (2 m = 20 key units). Game version 0.2.6228.27061.
- 2026-07-14: re-verified and extended against the 0.2.6403.27689 decompile during the mixed-tier cable network guard research pass. Re-verified verbatim-identical with new line refs: field cluster (292989-293003), `Add` dispatch (293100-293133), `Get<T>` internals (293005-293079), `RemoveCellObjectReferences` (293135-293167). Extended: `Add` overwrite semantics made explicit (plain overwrite, last writer wins; caller `AddSmallGridStructure` 206870-206919 writes cell occupant + back-pointer before `OnRegistered(null)`; no occupancy check in the registration path), `GridController.GetSmallCell` verbatim (207128-207138), `RemoveCellObjectReferences` promoted to its own section with the full verbatim and the reference-equality-only clearing nuance (`RemoveSmallGridStructure` 206947+ inlines the same pattern; `DetatchSmallGridStructure` 206820-206840 prunes the cell when `!IsValid()`), new "Stacked pairs: two cables in one cell" section (grid-invisible older cable, no self-merge through the shared cell, deconstruction order hazard, no NRE from stacking alone, silent misbehavior overall), new "Multi-cell pieces" section (`SmallGrid.SmallCell` back-pointer ends as the LAST occupied cell for pieces like `StructureCableSuperHeavyStraight10`). Resolved the DualRegister open question (removed): `GridController.Register` (206469-206486) shows a `SmallGrid` structure always goes through `AddSmallGridStructure`, and ONLY when `DualRegister` is true does it fall through to `AddGridStructure` + `AddFaceReference` + `UpdateAirState` (the large-grid registration); non-DualRegister small grids live only in `SmallCell`. Full chain on [StructureRegistration](../GameSystems/StructureRegistration.md). No prior claim contradicted; the 0.2.6228 line refs in the sections above were superseded by 0.2.6403 refs where the section was re-verified.

## Open questions

- The exact size and direction of one ladder's multi-cell small-grid footprint (how many cells, anchored which way) is inferred from the flood's need to skip the seed's own cells, not read from the registration code directly. The 2 m stacked-ladder pitch (20 key units) and the `Position.y * 10 + 5` anchor are confirmed from live captures.
