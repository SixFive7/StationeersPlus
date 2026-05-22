---
title: Room
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-22
sources:
  - Mods/SprayPaintPlus/RESEARCH.md:112-114
  - Mods/SprayPaintPlus/SprayPaintPlus/NetworkPainterPatch.cs:212-215
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.GridSystem.Room
related:
  - ./Cell.md
  - ./Wall.md
  - ./RoomController.md
  - ./GridController.md
  - ./Atmosphere.md
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

## Room class and fields
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

`Room : IEvaluable` in `Assets.Scripts.GridSystem`. Field cluster:

```csharp
public static List<Room> AllRooms = new List<Room>();

public List<WorldGrid> Grids = new List<WorldGrid>();

public long RoomId;

[XmlIgnore]
public bool IsDeletionCandidate;

public bool WillSave = true;

public GasMixture FrozenContents = GasMixtureHelper.Create();

[XmlIgnore]
public List<DynamicThing> DynamicContents = new List<DynamicThing>();

[XmlIgnore]
public RoomType RoomType;

public GasMixture GasMixture;
```

`Grids` is the list of interior cells (the enclosed volume, not the boundary). `AllRooms` is the global list of every room. `RoomId` is the stable identifier persisted in saves. `GasMixture` is the room's current gas state; `RoomType` is the classified room category.

## Grid membership: AddGrid / RemoveGrid
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

```csharp
public bool AddGrid(WorldGrid grid)
{
    if (Grids.Contains(grid))
    {
        return false;
    }
    Grids.Add(grid);
    RoomController.World.AddOrUpdateRoomLookup(grid, this);
    Atmosphere atmosphereLocal = GridController.World.AtmosphericsController.GetAtmosphereLocal(grid);
    if (atmosphereLocal != null)
    {
        atmosphereLocal.Room = this;
    }
    RoomManager.EvaluateRoomTypeRules(this);
    return true;
}
```

`AddGrid` is the single place that keeps three structures in sync: the room's own `Grids` list, the `RoomController.RoomLookup` cell -> room map, and the cell `Atmosphere.Room` back-reference. `RemoveGrid(WorldGrid grid)` reverses it. See `./RoomController.md` and `./Atmosphere.md`.

## CacheRoomData: gas/data refresh
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

`CacheRoomData()` recomputes the room's cached gas snapshot; `RunCacheRoomDataJobs()` is the static driver that runs the `CacheRoomData` job on the atmospherics worker (`AtmosphericsWorker.Execute(AtmosphericsWorker.Job.CacheRoomData)`). This refresh is independent of the `RoomController.GetRoom` cell-lookup path; reading room membership does not trigger it.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0029, F0351. No conflicts.
- 2026-05-22: added "Room class and fields", "Grid membership: AddGrid / RemoveGrid", and "CacheRoomData: gas/data refresh" sections from a room/grid/atmospherics decompile pass. Additive; existing sections unchanged. Source: `Assets.Scripts.GridSystem.Room` in `Assembly-CSharp`, game version 0.2.6228.27061. Added related links to RoomController, GridController, Atmosphere.

## Open questions

None at creation.
