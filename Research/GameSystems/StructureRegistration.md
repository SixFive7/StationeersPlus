---
title: StructureRegistration
type: GameSystems
created_in: 0.2.6403.27689
verified_in: 0.2.6403.27689
verified_at: 2026-07-14
sources:
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 318983-319038 (Thing.Create), 37900-37978 (Referencable.RegisterNew / RegisterAs), 314182-314205 (Structure.OnAssignedReference), 206469-206486 (GridController.Register), 206870-206919 (AddSmallGridStructure), 312289-312351 (SmallGrid.OnRegistered), 319758-319773 (Thing.OnRegistered), 317372 / 322270-322287 (NewToSend / Thing.DeserializeNew / SnapTransform), 206783-206840 (AttachSmallGridStructure / DetatchSmallGridStructure)
related:
  - ../GameClasses/Cable.md
  - ../GameClasses/SmallCell.md
  - ../GameClasses/Structure.md
tags: [prefab, network]
---

# StructureRegistration

The generic spawn-to-grid chain every placed structure runs through, from `Thing.Create<T>` to the `OnRegistered` tails. Cursor builds, programmatic spawns, save load, and multiplayer client spawns all share this chain; only the gates inside individual `OnRegistered` overrides differ per path. All line numbers below refer to `.work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs`. The small-grid cell record the chain writes into is documented on [SmallCell](../GameClasses/SmallCell.md); the cable-specific merge that runs inside this chain is on [Cable](../GameClasses/Cable.md).

## The full chain
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

`Constructor.SpawnConstruct(CreateStructureInstance)` (documented on [Constructor](../GameClasses/Constructor.md); the simulation side calls `Thing.Create<Structure>` + `SetStructureData`) -> `Thing.Create<T>` -> `Referencable.RegisterNew` -> `Structure.OnAssignedReference` -> `GridController.World.Register(this)` -> `AddSmallGridStructure` (grid write) -> `<subclass>.OnRegistered(null)` -> `SmallGrid.OnRegistered` -> `Structure.OnRegistered` -> `Thing.OnRegistered`.

`Thing.Create<T>(Thing prefab, Vector3 worldPosition, Quaternion worldRotation, long referenceId = 0L)`, verbatim (318983-319038). The `referenceId` parameter is the fresh-spawn vs load/join discriminator: `0L` means "server-side fresh spawn, allocate a new id via `RegisterNew` and broadcast via `NewToSend`"; nonzero means "recreate a known thing under an existing id via `RegisterAs`" (client spawn from the wire, or load paths that carry a saved id):

```csharp
public static T Create<T>(Thing prefab, Vector3 worldPosition, Quaternion worldRotation, long referenceId = 0L)
{
    if (prefab == null)
    {
        throw new NullReferenceException("Parameter prefab is null");
    }
    if (!(prefab is T))
    {
        throw new InvalidCastException($"{prefab.GetType()} can not be cast to type {typeof(T)}");
    }
    Thing thing = Prefab.Find(prefab.PrefabHash);
    Thing thing2 = UnityEngine.Object.Instantiate(thing, worldPosition, worldRotation);
    thing2.name = thing.name;
    thing2.PrefabName = thing.name;
    thing2.SetPrefab(thing);
    thing2.OnStartRender();
    if (!thing2.IsCursor)
    {
        bool flag = false;
        if (referenceId == 0L)
        {
            flag = Referencable.RegisterNew(thing2);
            if (flag)
            {
                if (GameManager.RunSimulation)
                {
                    thing2.InitInternalAtmosphere();
                }
                if (Assets.Scripts.Networking.NetworkManager.IsServer && NetworkBase.Clients.Count > 0)
                {
                    if (thing2 is LanderCapsule)
                    {
                        NewToSend.AddToFront(thing2);
                    }
                    else
                    {
                        NewToSend.Add(thing2);
                    }
                }
            }
        }
        else
        {
            flag = Referencable.RegisterAs(thing2, referenceId);
        }
        if (flag)
        {
            OcclusionManager.Register(thing2);
        }
    }
    if (thing2 is T)
    {
        return (T)(object)((thing2 is T) ? thing2 : null);
    }
    throw new NullReferenceException();
}
```

Notes on the body:

- The entire registration chain runs synchronously inside `Thing.Create` (`RegisterNew` / `RegisterAs` call `OnAssignedReference`, which calls `Register`, which calls `AddSmallGridStructure`, which calls `OnRegistered`). By the time `Create` returns, the thing is fully registered and on the grid.
- Fresh server spawns are appended to the `SyncList<Thing> NewToSend` (declared at 317372) when clients are connected; that is the spawn broadcast (see "Client replication" below). `LanderCapsule` is special-cased to the front of the list.
- `IsCursor` things (placement-preview ghosts) skip registration entirely.

## Referencable.RegisterNew vs RegisterAs
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

`Referencable.RegisterNew` (37900-37938), verbatim. It THROWS on clients, assigns the id under a lock, and calls `OnAssignedReference` synchronously:

```csharp
public static bool RegisterNew(IReferencable iReferencable)
{
    if (Assets.Scripts.Networking.NetworkManager.IsClient)
    {
        string text = "Error: Clients should not be assigning a new Reference";
        if (iReferencable != null)
        {
            text = text + ". Client is trying to assign a new reference for " + iReferencable.DisplayName;
        }
        throw new System.Exception(text);
    }
    if (iReferencable == null)
    {
        ConsoleWindow.PrintError("Can't register a null IReferencable");
        return false;
    }
    if (iReferencable.ReferenceId != 0L)
    {
        ConsoleWindow.PrintError($"error trying to register '{iReferencable.DisplayName}' as it already is with '{iReferencable.ReferenceId}'");
        return false;
    }
    lock (NextReferenceIdLock)
    {
        if (Referencables.ContainsKey(NextReferenceId))
        {
            ConsoleWindow.PrintError($"error trying to register '{iReferencable.DisplayName}' with referenceId of '{NextReferenceId}' as it is already Assigned to: '{Referencables[NextReferenceId].DisplayName}'");
            return false;
        }
        iReferencable.ReferenceId = NextReferenceId;
        NextReferenceId++;
    }
    lock (Referencables)
    {
        Referencables.Add(iReferencable.ReferenceId, iReferencable);
    }
    iReferencable.OnAssignedReference();
    DeferredMessageQueue.NotifyRegistered(iReferencable.ReferenceId);
    return true;
}
```

`RegisterAs(IReferencable thing, long referenceId, bool force = false)` (37940-37978) is the `referenceId != 0` variant used by clients recreating server things (and load paths carrying a saved id); it also calls `thing.OnAssignedReference()` (at 37974), so the rest of the chain below is identical on both entry paths.

## Structure.OnAssignedReference and GridController.Register
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

`Structure.OnAssignedReference` (314182-314205), verbatim. This is where the transform position is snapped to the `Grid3` lattice (see [Grid3](../GameClasses/Grid3.md)) and `RegisteredLocalGrid` / `BlockingGrids` are computed:

```csharp
public override void OnAssignedReference()
{
    base.OnAssignedReference();
    Direction = ThingTransform.rotation;
    RegisteredLocalGrid = new Grid3(base.ThingTransformPosition);
    base.ThingTransformPosition = RegisteredLocalGrid.ToVector3();
    if (blockingGrids == null || blockingGrids.Length == 0)
    {
        BlockingGrids = new Grid3[1]
        {
            new Grid3(base.ThingTransformPosition)
        };
    }
    else
    {
        BlockingGrids = new Grid3[blockingGrids.Length];
        for (int i = 0; i < blockingGrids.Length; i++)
        {
            Vector3 worldPosition = blockingGrids[i].RotateAround(Vector3.zero, ThingTransform.rotation) + base.transform.position;
            BlockingGrids[i] = new Grid3(worldPosition);
        }
    }
    GridController.World.Register(this);
}
```

`GridController.Register(Structure)` (206469-206486), verbatim. Small-grid structures branch to `AddSmallGridStructure`; only `DualRegister` small grids fall through to the large-grid registration as well:

```csharp
public void Register(Structure structure)
{
    structure.ThingTransformPosition = LocalToWorld(structure.RegisteredLocalGrid);
    structure.RegisteredPosition = structure.ThingTransformPosition;
    structure.RegisteredRotation = structure.ThingTransformRotation;
    SmallGrid smallGrid = structure as SmallGrid;
    if ((bool)smallGrid)
    {
        AddSmallGridStructure(smallGrid);
        if (!smallGrid.DualRegister)
        {
            return;
        }
    }
    AddGridStructure(structure);
    AddFaceReference(structure);
    UpdateAirState(structure);
}
```

## AddSmallGridStructure: grid writes happen BEFORE OnRegistered
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

`GridController.AddSmallGridStructure(SmallGrid)` (206870-206919+), verbatim. The cell writes (`smallCell.Add(smallGridObject)`, 293100-293106 in `SmallCell.Add`) and the `SmallCell` back-pointer assignment happen at 206876-206887, BEFORE `OnRegistered(null)` is invoked at 206899. `OnRegistered` receives a NULL `Cell` for non-DualRegister small-grid structures such as cables:

```csharp
private void AddSmallGridStructure(SmallGrid smallGridObject)
{
    List<SmallCell> list = new List<SmallCell>();
    Grid3[] array = (Grid3[])smallGridObject.GridBounds.GetLocalSmallGrid(smallGridObject.RegisteredPosition, smallGridObject.RegisteredRotation);
    foreach (Grid3 grid in array)
    {
        SmallCell smallCell = GetSmallCell(grid);
        if (smallCell == null)
        {
            smallCell = new SmallCell(grid, this);
            smallCell.Add(smallGridObject);
            SmallGridCells.Add(grid, smallCell);
        }
        else
        {
            smallCell.Add(smallGridObject);
        }
        smallGridObject.SmallCell = smallCell;
        list.Add(smallCell);
    }
    AllStructuresPool.Add(smallGridObject);
    if (smallGridObject.IsUpdateOnServerTick)
    {
        AllServerTickStructures.Add(smallGridObject);
    }
    foreach (Assets.Scripts.Objects.Connection openEnd in smallGridObject.OpenEnds)
    {
        openEnd.CacheTransformUp();
    }
    smallGridObject.OnRegistered(null);
    if (list.Count >= 6)
    {
        foreach (SmallCell item in list)
        {
            if (!(item.Device == null) && !item.Device.IsDoor)
            {
                Cell cell = GetCell(item.Device.LocalGrid);
                if (cell == null || !cell.Stairs)
                {
                    GridPathfinder.DeviceBlockedList.Add(item.Device.LocalGrid);
                }
            }
        }
    }
    foreach (SmallCell item2 in list)
    {
        if (item2.Pipe != null)
        {
            item2.Pipe.OnGridPlaced(smallGridObject);
        }
        ...
```

(The body continues with the per-cell `OnGridPlaced` fan-out for the other occupant slots.)

Ordering facts that matter for hooks:

- The cell occupant writes and the `SmallCell` back-pointer land BEFORE `OnRegistered(null)`. Any `OnRegistered` prefix or postfix already sees the new structure in its own cell(s). This was validated 2026-07-14 against a prior contrary claim; see the Verification history on [CableNetwork](../GameClasses/CableNetwork.md).
- For a multi-cell piece (for example `StructureCableSuperHeavyStraight10`), `smallGridObject.SmallCell` ends up pointing at the LAST cell iterated; `SmallCell` on `SmallGrid` is a single reference, not a list. See [SmallCell](../GameClasses/SmallCell.md).
- `OpenEnds` get `CacheTransformUp()` (206895-206898) before `OnRegistered`, but `Connection.Initialize()` has NOT yet run at that point (it runs inside `SmallGrid.OnRegistered`, 312292-312295).

## The OnRegistered tails: what state is valid when
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

`SmallGrid.OnRegistered` (312289-312351), verbatim. This is the neighbor notification pass; the `OnGridAdjacentPlaced` fan-out is a candidate observation point for mods that want to react to a new neighbor:

```csharp
public override void OnRegistered(Cell cell)
{
    base.OnRegistered(cell);
    foreach (Connection openEnd in OpenEnds)
    {
        openEnd.Initialize();
    }
    if (GameManager.GameState == GameState.Loading || GameManager.GameState == GameState.None)
    {
        return;
    }
    if (base.WorldGrid == WorldGrid.INVALID)
    {
        throw new NullReferenceException("grid for " + DisplayName + " is invalid when registering");
    }
    Span<SmallCellRef> span = stackalloc SmallCellRef[32];
    int count = 0;
    FillConnected(span, ref count);
    Span<SmallCellRef> span2 = span;
    Span<SmallCellRef> span3 = span2.Slice(0, count);
    for (int i = 0; i < span3.Length; i++)
    {
        SmallCellRef smallCellRef = span3[i];
        smallCellRef.Get().OnNeighborPlaced(this);
    }
    Span<Grid3> span4 = stackalloc Grid3[6];
    int count2 = 0;
    base.GridController.PopulateSmallGridNeighbours(base.Position, span4, ref count2);
    Span<Grid3> span5 = span4;
    Span<Grid3> span6 = span5.Slice(0, count2);
    for (int i = 0; i < span6.Length; i++)
    {
        Grid3 localGrid = span6[i];
        SmallCell smallCell = base.GridController.GetSmallCell(localGrid);
        if (smallCell != null)
        {
            if (smallCell.Pipe != null)
            {
                smallCell.Pipe.OnGridAdjacentPlaced(this);
            }
            if (smallCell.Cable != null)
            {
                smallCell.Cable.OnGridAdjacentPlaced(this);
            }
            if (smallCell.Device != null)
            {
                smallCell.Device.OnGridAdjacentPlaced(this);
            }
            if (smallCell.Chute != null)
            {
                smallCell.Chute.OnGridAdjacentPlaced(this);
            }
            if (smallCell.Other != null)
            {
                smallCell.Other.OnGridAdjacentPlaced(this);
            }
            if (smallCell.Rail != null)
            {
                ((SmallGrid)smallCell.Rail).OnGridAdjacentPlaced(this);
            }
        }
    }
}
```

`Thing.OnRegistered` (319758-319773), verbatim. This is where `Cell`, `Position`, and `WorldGrid` are finalized; for a small-grid structure `cell` is null:

```csharp
public virtual void OnRegistered(Cell cell)
{
    Cell = cell;
    Position = ThingTransformPosition;
    Rotation = ThingTransformRotation;
    RegisteredPosition = Position;
    RegisteredRotation = Rotation;
    WorldGrid = new WorldGrid(this);
    RenderChange(setRenderer: true, this).Forget();
    RefreshAnimState(skipAnimation: true);
    if (RoomManager.ContributingPrefabs.Contains(PrefabHash))
    {
        RoomManager.AddRoomContributor(WorldGrid, this);
    }
    _nameHash = Animator.StringToHash(DisplayName);
}
```

The intermediate `Structure.OnRegistered` (315513-315560) writes no `SmallCell` state either; nothing in the whole `OnRegistered` chain (SmallGrid 312289-312351, Structure 315513-315560, Thing 319758-319773) touches the cell record. The cell writes belong exclusively to `AddSmallGridStructure` (above).

State validity for hook code running inside a subclass `OnRegistered` body BEFORE its `base.OnRegistered(cell)` call (the `Cable.OnRegistered` merge is the canonical example, see [Cable](../GameClasses/Cable.md)):

- The Transform exists and is positioned (Instantiate at world position; `Register` re-snapped `ThingTransformPosition` at 206471).
- `RegisteredLocalGrid`, `RegisteredPosition`, `RegisteredRotation` are set (`OnAssignedReference` 314186, `Register` 206471-206473).
- The structure IS in its SmallCell(s), and its `SmallCell` back-pointer is set (206876-206887).
- OpenEnds have `CacheTransformUp()` done (206895-206898) but `Connection.Initialize()` has NOT yet run (that happens in `SmallGrid.OnRegistered` at 312292-312295).
- `Thing.Cell`, `Thing.Position`, `Thing.WorldGrid` are NOT yet set by `Thing.OnRegistered` (`WorldGrid` is INVALID until 319765). Any hook logic before `base.OnRegistered` must use `ThingTransformPosition` / `RegisteredLocalGrid`, not `Position` / `WorldGrid`.

## There is NO occupancy check at registration time
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

The entire register path (`Register` 206469 -> `AddSmallGridStructure` 206870) contains no `CanConstruct`, no `_IsCollision`, no occupancy test of any kind. `_IsCollision` is consulted ONLY from cursor-side code. Whole-file census of `_IsCollision(` call sites at 0.2.6403.27689: lines 312579-312609 (the wall-mount validity check `CanWallMount` region), 312693-312713 (`SmallGrid.CanConstruct`), plus the two overrides (384925, believed to be `Piping`; 392588, `Cable`). None is in the registration path. `Constructor.SpawnConstruct` on the simulation side likewise performs no re-validation (already documented on [StructurePlacementValidation](./StructurePlacementValidation.md), re-confirmed at 0.2.6403.27689 by the absence of any check in the Create -> Register chain).

Consequence: a programmatic spawn (or any validation-bypassing caller) that places a structure into an occupied cell is silently accepted; `SmallCell.Add` is a plain overwrite of the slot. The failure modes of the resulting stacked pair are documented on [SmallCell](../GameClasses/SmallCell.md), "Stacked pairs".

## Rocket-move path: Attach/Detatch do not re-run OnRegistered
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

`GridController.AttachSmallGridStructure` / `DetatchSmallGridStructure` (206783-206840) are the rocket-move variants of Add/Remove (used when a rocket carrying small-grid structures lands or moves between grid controllers). `Attach` does NOT call `OnRegistered`, so an `OnRegistered` hook does not fire when a rocket carrying cables lands or moves. `DetatchSmallGridStructure` (206820-206840) calls `SmallCell.RemoveCellObjectReferences` and then prunes the cell from `SmallGridCells` when `!smallCell.IsValid()` (see [SmallCell](../GameClasses/SmallCell.md)).

## Client replication: NewToSend spawn broadcast and DeserializeNew
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

A fresh server-side spawn reaches remote clients via the `SyncList<Thing> NewToSend` (declared 317372, appended inside `Thing.Create` above). The client-side factory is `Thing.DeserializeNew` (322270-322279), verbatim:

```csharp
public static void DeserializeNew(RocketBinaryReader reader)
{
    Network.ReadPackedId(reader, out var referenceId);
    int prefabHash = reader.ReadInt32();
    Vector3 transformPosition = reader.ReadVector3();
    Quaternion quaternion2 = reader.ReadQuaternion();
    Thing thing = Create<Thing>(prefabHash, transformPosition, quaternion2, referenceId);
    thing.DeserializeOnJoin(reader);
    thing.SnapTransform(transformPosition, quaternion2);
}
```

So on a remote client, a newly placed structure arrives as: `Thing.DeserializeNew` -> `Thing.Create` (which takes the `RegisterAs` branch with the server's id; `OnAssignedReference` -> `Register` -> `AddSmallGridStructure` -> `OnRegistered`, where server-only gates such as `GameManager.RunSimulation` skip simulation-side work) -> `thing.DeserializeOnJoin(reader)` (per-type wire payload) -> `thing.SnapTransform(...)`.

`Thing.SnapTransform` (322281-322287) re-snaps the position and recomputes `WorldGrid` after the client-side spawn, so the client's final registered position matches the wire values even if `DeserializeOnJoin` moved anything.

Worked example (cable): the client's `Cable.OnRegistered` merge body is SKIPPED (`GameManager.RunSimulation` is false on a client), and the client's local network re-merge happens afterwards in `Cable.DeserializeOnJoin` (GameState is Running at runtime delivery, so the `!Joining` branch re-merges locally). This matches the "merge is not authoritative" analysis on [CableNetwork](../GameClasses/CableNetwork.md).

## Threading
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

The whole chain is main-thread, always: `Thing.Create` calls `UnityEngine.Object.Instantiate`, which forces the main thread, and everything downstream (`RegisterNew` / `RegisterAs` -> `OnAssignedReference` -> `Register` -> `AddSmallGridStructure` -> `OnRegistered`) runs synchronously inside it. Higher-level flows that enter the chain, all main-thread:

- Cursor build: `Constructor.SpawnConstruct` on the simulation side.
- Programmatic spawn: any `Thing.Create` / `OnServer.Create`.
- Save load: things created during Loading (`GameManager.GameState == GameState.Loading`; per-type `OnRegistered` gates such as the cable merge skip themselves during Loading).
- Multiplayer client spawn: `Thing.DeserializeNew` -> `RegisterAs`.
- `Cable.Break()` wreckage spawn: `Constructor.SpawnConstruct(instance)` inside `Break`, main thread because `Break` self-marshals from the worker (see [Cable](../GameClasses/Cable.md)).

## Verification history

- 2026-07-14: page created. Sourced from the mixed-tier cable network guard research pass against `.work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs`: `Thing.Create` (318983-319038), `Referencable.RegisterNew` / `RegisterAs` (37900-37978), `Structure.OnAssignedReference` (314182-314205), `GridController.Register` (206469-206486), `AddSmallGridStructure` (206870-206919+), `SmallGrid.OnRegistered` (312289-312351), `Thing.OnRegistered` (319758-319773), `NewToSend` (317372), `Thing.DeserializeNew` (322270-322279), `Thing.SnapTransform` (322281-322287), `AttachSmallGridStructure` / `DetatchSmallGridStructure` (206783-206840), `_IsCollision` call-site census (312579-312609, 312693-312713, overrides 384925 / 392588). The "cell writes precede OnRegistered" ordering was confirmed by a fresh-validator run resolving a conflicting claim on [CableNetwork](../GameClasses/CableNetwork.md) (see that page's Verification history, 2026-07-14).

## Open questions

- The `_IsCollision` override at decompile line 384925 is believed to be `Piping` but was not positively identified during the census (the census keyed on call sites, not declarations). Confirm the declaring type on the next pass through the piping family.
