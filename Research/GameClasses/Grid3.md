---
title: Grid3
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-14
sources:
  - Mods/SprayPaintPlus/RESEARCH.md:104-106
  - Mods/SprayPaintPlus/RESEARCH.md:181-183
  - Mods/SprayPaintPlus/SprayPaintPlus/NetworkPainterPatch.cs:357-371
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Grid3
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 291547-291736 (Grid3 constructors / converters), 230409-230459 (ToGrid / ToGridPosition / GridCenter), 206416-206424 (WorldToLocalGrid / LocalToWorld), 312036-312038 / 292041-292043 (SmallGridSize / SmallGridOffset), 308886-308891 (LogicType.PositionX/Y/Z), 43997 / 44065-44066 (KeyMap F3/F4)
related:
  - ./Cell.md
  - ./Room.md
  - ./Structure.md
  - ./Wall.md
  - ./SmallCell.md
  - ../GameSystems/StructureRegistration.md
tags: [terrain, ui]
---

# Grid3

Vanilla game type representing the scaled integer-grid coordinate system used for structure placement and room math. World coordinates are scaled by 10 (`Grid3.one`): a `Grid3` stores integer DECIMETERS (tenths of a meter). Grid-aligned structures snap to a cell spacing derived from their `GridSize`.

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

## Integer decimeters: constructors, converters, snapping
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

`Grid3` stores integer DECIMETERS (tenths of a meter). Constructors and converters, verbatim at 0.2.6403.27689:

```csharp
public Grid3(Vector3 worldPosition)            // line 291547
{
    x = Round(worldPosition.x * 10f);
    y = Round(worldPosition.y * 10f);
    z = Round(worldPosition.z * 10f);
}

public Grid3(Vector3 worldPosition, float gridBy, float gridOffset)   // line 291590
{
    worldPosition = worldPosition.GridCenter(gridBy, gridOffset);
    x = Round(worldPosition.x * 10f);
    y = Round(worldPosition.y * 10f);
    z = Round(worldPosition.z * 10f);
}

public readonly Vector3 ToVector3()            // line 291736
{
    return new Vector3((float)x * 0.1f, (float)y * 0.1f, (float)z * 0.1f);
}

public override string ToString()              // line 291731
{
    return $"Grid3({x}, {y}, {z})";
}
```

Grid snapping, `GridCenter` extension (230451-230459):

```csharp
public static Vector3 GridCenter(this Vector3 worldPosition, float gridSquareSize = 2f, float offset = 0f)
{
    float num = gridSquareSize * 0.5f;
    float num2 = offset + num;
    worldPosition.x = Mathf.Round((worldPosition.x - num2) / gridSquareSize) * gridSquareSize + num2;
    worldPosition.y = Mathf.Round((worldPosition.y - num2) / gridSquareSize) * gridSquareSize + num2;
    worldPosition.z = Mathf.Round((worldPosition.z - num2) / gridSquareSize) * gridSquareSize + num2;
    return worldPosition;
}
```

`GridController.WorldToLocalGrid` (206416-206419) and `LocalToWorld` (206421-206424):

```csharp
public Grid3 WorldToLocalGrid(Vector3 worldPosition, float gridSize = 2f, float gridOffset = 0f)
{
    return worldPosition.ToGrid(gridSize, gridOffset);
}

public Vector3 LocalToWorld(Grid3 localGrid)
{
    return localGrid.ToVector3();
}
```

(`ToGrid` at 230409-230412 forwards to the `(Vector3, float, float)` Grid3 constructor; `ToGridPosition` at 230414-230417 is the unsnapped decimeter conversion.)

## Small-grid cell math: pitch 0.5 m with 0.25 m offset
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

Small-grid constants: `SmallGrid.SmallGridSize = 0.5f`, `SmallGrid.SmallGridOffset = 0.25f` (statics at 312036-312038; parallel consts at 292041-292043). The default (large) grid is 2 m with 0 offset.

- At registration, `GridController.Register` snaps: `structure.ThingTransformPosition = LocalToWorld(structure.RegisteredLocalGrid)` (206471), where `RegisteredLocalGrid = new Grid3(ThingTransformPosition)` was computed in `Structure.OnAssignedReference` (314186). So a placed cable's `ThingTransformPosition` sits exactly on the 0.1 m Grid3 lattice, and for a snapped placement it IS the small-cell center. See [StructureRegistration](../GameSystems/StructureRegistration.md).
- The cable's occupied cell key(s): `GridBounds.GetLocalSmallGrid(RegisteredPosition, RegisteredRotation)` -> `Grid3[]` (one entry for the 1-cell piece; 3/5/10 for the long pieces). Equivalently for a point query: `WorldToLocalGrid(worldPos, 0.5f, 0.25f)` = snap to the 0.5 m cell center (centers lie on multiples of 0.5 m because offset 0.25 + half-size 0.25 = 0.5), then store as decimeter ints, so cell-key Grid3 components are multiples of 5.
- Round trip for a player-facing message: `cable.ThingTransformPosition` (world meters) is the number to show; `cable.RegisteredLocalGrid.ToVector3()` gives the same value. `SmallCell.SmallGrid` (the cell's own `Grid3` key; see [SmallCell](./SmallCell.md)) `.ToVector3()` gives the cell center in meters.

## Player-facing coordinate convention: world meters
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

- The logic system exposes raw world meters: a device answering `LogicType.PositionX/Y/Z` returns `base.Position.x` / `.y` / `.z` (verbatim `case LogicType.PositionX: return base.Position.x;` at 308886-308891). IC10 players, autopilot scripts, and community tooling therefore treat "coordinates" as world meters. `Thing.Position` is the registered `ThingTransformPosition` (319761).
- There is no textual coordinate HUD for players in vanilla: F3 toggles the console (`KeyMap.ToggleConsole = KeyCode.F3`, 43997/44065), F4 toggles the game-info panel (`KeyMap.ToggleInfo = KeyCode.F4`, 44066; consumer at 287004-287007 `SetGameInfoState`, the tab menu, no coordinates). Beacons and trackers register with `WaypointManager` and draw HUD markers with distance, not coordinate text.
- The authoring tool shows "Reference ID: " in an `AttackWith` tooltip (315158); that is the only vanilla surface printing a `ReferenceId` for a placed cable, and it prints no coordinates.
- No player-facing world-position-to-text formatter exists in the decompile; `Grid3.ToString()` ("Grid3(x, y, z)", decimeters) and `PrintDebugInfo` are developer-facing. A mod should format world meters itself, e.g. `$"({pos.x:F1}, {pos.y:F1}, {pos.z:F1})"`, matching the `LogicType.Position` convention (and matching how this repo already reports positions, e.g. the [CableNetwork](./CableNetwork.md) open question quotes "world position (-1292.0, 206.0, -780.0)").
- Recommendation for a console message: world meters of the subject's `ThingTransformPosition` with one decimal, optionally plus the display name and tier names. Do not print `Grid3` raw integers (players cannot map decimeters to anything in-game).

## Verification history
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0016, F0021, F0321. No conflicts.
- 2026-07-14: extended with three sections from the mixed-tier cable network guard research pass against the 0.2.6403.27689 decompile: "Integer decimeters" (constructors 291547 / 291590, `ToVector3` 291736, `ToString` 291731, `GridCenter` 230451-230459, `WorldToLocalGrid` / `LocalToWorld` 206416-206424, `ToGrid` / `ToGridPosition` 230409-230417), "Small-grid cell math" (`SmallGridSize` 0.5 / `SmallGridOffset` 0.25 at 312036-312038 and 292041-292043; cell centers on 0.5 m multiples so cell-key components are multiples of 5; a placed cable's `ThingTransformPosition` IS its grid-snapped world position per `Register` 206471 + `OnAssignedReference` 314186), and "Player-facing coordinate convention" (`LogicType.PositionX/Y/Z` returns `base.Position` components in world meters, 308886-308891; F3 = console, F4 = info panel without coordinates, beacons draw distance only; the authoring-tool tooltip's "Reference ID: " at 315158 is the only vanilla `ReferenceId` surface for a placed cable; no vanilla position-to-text formatter exists). The new decimeter semantics confirm and sharpen the existing "scaled by 10" sections; no prior claim contradicted. The pre-existing sections keep their 0.2.6228.27061 stamps (not re-read this pass).

## Open questions

None at creation.
