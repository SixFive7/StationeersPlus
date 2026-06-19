---
title: SmallCell
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-19
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.GridSystem.SmallCell
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.SmallGrid
related:
  - ./Cell.md
  - ./GridController.md
  - ./Structure.md
  - ./Connection.md
  - ./Cable.md
tags: [terrain, prefab]
---

# SmallCell

Vanilla game class for one cell of the 0.5 m "small grid", the registration grid for `SmallGrid`-derived structures (pipes, cables, chutes, small devices, robotic-arm rails, ladders). Parallel to and separate from the large `Cell` grid (see `Cell.md`). Fully qualified `Assets.Scripts.GridSystem.SmallCell` (decompile line 274718), same `Assets.Scripts.GridSystem` namespace as `Cell` (namespace block spans lines 272148-274900).

## Small grid versus large Cell grid
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

Two parallel grids coexist. The large `Cell` (see `Cell.md`) carries `AllStructures`, the face-keyed `Structural` map, and a dedicated `Stairs Stairs` slot; walls and large structures are flooded by SprayPaintPlus through `Cell.AllStructures` + `Cell.NeighborCells`. The small grid is keyed at `SmallGrid.SmallGridSize = 0.5f` with `SmallGrid.SmallGridOffset = 0.25f`; each `SmallCell` holds at most one occupant per typed slot. World-to-local conversion for the small grid uses `GridController.World.WorldToLocalGrid(worldPosition, SmallGrid.SmallGridSize, SmallGrid.SmallGridOffset)` and lookup goes through `GridController.World.GetSmallCell(localPosition)`.

A `SmallGrid` structure registers in a `SmallCell`, not in `Cell.AllStructures`. That is why the mod floods pipes and cables through their dedicated `PipeNetwork` / `CableNetwork` objects, and reserves the large-cell BFS for walls and large structures only. A traversal over `SmallGrid` structures that have no network object (ladders) must therefore walk the small grid, not the large-cell `AllStructures` / `NeighborCells` lists.

## Slots
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

Field cluster on `SmallCell` (decompile lines 274720-274734):

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

One occupant per slot. `Other` is the generic `SmallGrid` slot: anything that is not a chute, pipe, device, cable, or rail lands here.

## Add dispatch
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

`SmallCell.Add(SmallGrid)` routes by concrete type (decompile lines 274831-274864):

```csharp
public void Add(SmallGrid smallGridObjectGrid)
{
    Cable cable = smallGridObjectGrid as Cable;
    if ((bool)cable) { Cable = cable; return; }
    Chute chute = smallGridObjectGrid as Chute;
    if ((bool)chute) { Chute = chute; return; }
    Pipe pipe = smallGridObjectGrid as Pipe;
    if ((bool)pipe) { Pipe = pipe; return; }
    Device device = smallGridObjectGrid as Device;
    if ((bool)device && device.SmallCollisionType != SmallGridBlock.Covers)
        Device = device;
    else if (smallGridObjectGrid is IRoboticArmRail rail)
        Rail = rail;
    else
        Other = smallGridObjectGrid;
}
```

`RemoveCellObjectReferences(SmallGrid)` (lines 274866-274898) clears whichever slot held the structure and nulls that structure's `SmallCell` back-reference.

## Lookups: Get&lt;T&gt; by position and by connection
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

The static `SmallCell.Get<T>(Grid3 localPosition)` (lines 274741-274775) fetches the `SmallCell` and returns the first slot (`Chute`, `Pipe`, `Device`, `Cable`, `Rail`, `Other`) that is a `T`. The `Get<T>(Grid3 localPosition, Connection connection)` overload (lines 274777-274805) additionally requires `result.IsConnected(connection)`, so it returns only the occupant that actually has an open end facing the queried connection. `Get<T>(Vector3 worldPosition, ...)` wraps the world-to-local conversion noted above.

This connection-filtered lookup is the primitive the small-grid network builders use to find the neighbor across each open end.

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

- 2026-06-19: page created from a small-grid registration decompile pass while scoping ladder flood-painting for Spray Paint Plus. Verbatim content from `Assets.Scripts.GridSystem.SmallCell` (slots, `Add`, `Get<T>`, `RemoveCellObjectReferences`) and `Assets.Scripts.Objects.SmallGrid` (`OpenEnds`, `SmallGridSize` / `SmallGridOffset`, `SmallCell`, `CenterPosition`, `Awake`, `RebuildGridState`), game version 0.2.6228.27061. Additive; new page, no existing content changed.
- 2026-06-19: added "Small-grid key scale, lookup conversion, and observed ladder registration" from a live InspectorPlus ladder-run capture plus the `GetSmallCell` / `Get<T>` conversion path (`new Grid3(worldPos)` = `Round(world * 10)`, no snapping; decompile 150910-150912, 158148-158150). Corrected the ladder-run consequence to key-stepping (verified working in-game) in place of the earlier unverified `OpenEnds`-walk suggestion. Resolved the prior open question on stacked-ladder small-cell pitch (2 m = 20 key units). Game version 0.2.6228.27061.

## Open questions

- Whether a `SmallGrid` structure is ALSO added to the large `Cell.AllStructures` (the `DualRegister` flag hints some small grids dual-register) or lives only in `SmallCell` is not confirmed here. The ladder-flood design does not depend on it (it walks the small grid regardless), but a definitive answer would let a reader reason about both grids.
- The exact size and direction of one ladder's multi-cell small-grid footprint (how many cells, anchored which way) is inferred from the flood's need to skip the seed's own cells, not read from the registration code directly. The 2 m stacked-ladder pitch (20 key units) and the `Position.y * 10 + 5` anchor are confirmed from live captures.
