---
title: Atmosphere
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-22
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Atmospherics.Atmosphere
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Atmospherics.AtmosphericsManager
related:
  - ./Room.md
  - ./RoomController.md
  - ./Cell.md
  - ./GridController.md
tags: [terrain]
---

# Atmosphere

The per-cell gas volume and its registry. `Atmosphere` (`Assets.Scripts.Atmospherics.Atmosphere : IReferencable, IEvaluable, IDensePoolable`) is one cell's gas state. `AtmosphericsManager` (`Assets.Scripts.Atmospherics.AtmosphericsManager : ThreadedManager`) holds the static cell -> atmosphere map. An `Atmosphere` carries a back-reference to the `Room` it belongs to.

## AtmosphericsManager: the cell -> atmosphere map
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

```csharp
private static readonly Dictionary<WorldGrid, Atmosphere> AllWorldAtmospheresLookUp = new Dictionary<WorldGrid, Atmosphere>(65535);

public static int WorldAtmospheresCount => AllWorldAtmospheresLookUp.Count;

public static Atmosphere Find(WorldGrid worldGrid)
{
    lock (AllWorldAtmospheresLookUp)
    {
        AllWorldAtmospheresLookUp.TryGetValue(worldGrid, out var value);
        return value;
    }
}
```

`Find` is a locked `TryGetValue`. The map is mutated under the same lock on register / unregister (`AllWorldAtmospheresLookUp.Add(atmosphere.WorldGrid, atmosphere)` / `.Remove(...)`). The lock matters because atmospherics runs threaded: the dictionary is touched from worker threads, so a main-thread reader must respect the same lock.

## Atmosphere fields
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

```csharp
public Room Room;

public GasMixture GasMixture = new GasMixture(new MoleQuantity(0.0));

public readonly List<Grid3> OpenNeighbors = new List<Grid3>();

public readonly List<Grid3> OpenNeighborsForMainThread = new List<Grid3>();

public WorldGrid WorldGrid => _wGrid;
```

`Room` is the bounding-room back-reference (set by `Room.AddGrid`, see below). `OpenNeighbors` is the connectivity graph the simulation flows gas across; `OpenNeighborsForMainThread` is the main-thread-safe copy, swapped via `SwapOpenNeighbors()`. `IsRoom` distinguishes a room-bounded atmosphere from open world (`if (!IsRoom && OpenNeighbors.Count == 0)`).

## Room <-> Atmosphere binding
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

`Room.AddGrid` binds a cell's atmosphere to the room:

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

So from a cell you can reach its room two ways: `RoomController.World.GetRoom(grid)` (the direct map) or `AtmosphericsManager.Find(grid).Room` (via the atmosphere). The first is cheaper and lock-free.

## Verification history

- 2026-05-22: page created from a room/grid/atmospherics decompile pass (feasibility study on networking composite cladding for SprayPaintPlus). Sections verified against `Assets.Scripts.Atmospherics.Atmosphere` and `Assets.Scripts.Atmospherics.AtmosphericsManager` in `Assembly-CSharp`, game version 0.2.6228.27061. New page; no conflicts.

## Open questions

None at creation.
