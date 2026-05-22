---
title: RoomController
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-22
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.RoomController
related:
  - ./Room.md
  - ./Cell.md
  - ./GridController.md
  - ./Atmosphere.md
tags: [terrain]
---

# RoomController

Global registry of detected rooms and the cell -> room map. The live instance is the static field `RoomController.World`. Class is `Assets.Scripts.RoomController`. The room set is server-maintained precomputed state: the engine flood-detects enclosed volumes and keeps `RoomLookup` current, so a mod reading "which room is this cell in" gets an O(1) answer with no recomputation.

## Singletons and the cell -> room map
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

```csharp
public static RoomController World;

public List<Room> Rooms = new List<Room>();

public ConcurrentDictionary<Grid3, Room> RoomLookup = new ConcurrentDictionary<Grid3, Room>();

private static readonly int MAXIterations = 1200;
```

`Rooms` is every detected room; `RoomLookup` is the cell -> room index. `MAXIterations = 1200` caps the engine's room flood-detect pass, which runs on construction/destruction events, never on a lookup.

## GetRoom: cell -> room, O(1)
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

```csharp
public Room GetRoom(Grid3 grid)
{
    Room value = null;
    RoomLookup?.TryGetValue(grid, out value);
    return value;
}

public Room GetRoom(Vector3 worldPosition)
{
    return GetRoom(new WorldGrid(worldPosition));
}

public Room GetRoom(WorldGrid worldGrid)
{
    return GetRoom(worldGrid.Value);
}
```

A pure `ConcurrentDictionary.TryGetValue`; returns `null` for a cell that belongs to no room (open space, solid fill, unenclosed). No flood, no allocation, no recomputation. This is the cheapest connectivity signal in the grid system to read. The SprayPaintPlus wall-painting code reaches it through a helper, `GetRoomFor(Structure s) => RoomController.World?.GetRoom(s.GridPosition)`.

## Mutating the map
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

```csharp
public void AddOrUpdateRoomLookup(WorldGrid grid, Room newValue)
{
    RoomLookup[grid.Value] = newValue;
}

public void RemoveRoomLookup(WorldGrid grid)
{
    RoomLookup.TryRemove(grid.Value, out var _);
}
```

These are driven by the room-detection system and by `Room.AddGrid` / `Room.RemoveGrid` (see `./Room.md`). Mods read `GetRoom`; they do not write the map.

## Rooms are precomputed, persisted, and join-synced
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

Rooms survive save/load and are reconstructed on client join, confirming they are durable precomputed state rather than something derived on demand. The load/join deserialize path rebuilds each room by reading its grids and re-adding them:

```csharp
room.AddGrid(reader.ReadWorldGrid());
...
World.NextRoomId = reader.ReadInt64();
```

`RegisterRoomGridFromWorldSetting(long roomId, WorldGrid worldGrid)` is the world-setting equivalent: `Get(roomId)`, then `room.AddGrid(worldGrid)`.

## Verification history

- 2026-05-22: page created from a room/grid/atmospherics decompile pass (feasibility study on networking composite cladding for SprayPaintPlus). All sections verified against `Assets.Scripts.RoomController` in `Assembly-CSharp`, game version 0.2.6228.27061. New page; no conflicts.

## Open questions

None at creation.
