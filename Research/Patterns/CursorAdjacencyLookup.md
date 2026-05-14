---
title: Cursor-time adjacency lookup (find neighbour cells from a placement ghost)
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-13
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.SmallGrid, Assets.Scripts.Objects.Connection, Assets.Scripts.Objects.SmallCell, Assets.Scripts.Inventory.InventoryManager
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 268641-268712 (InventoryManager.HandleStructurePrefab cursor instantiation), 274718-274830 (SmallCell), 292000-294500 (SmallGrid.ConnectedCables / ConnectedDevices / ConnectedPipes / IsConnectingToUmbilical / Connection), 293285-293400 (Connection.GetSmallGridOccupant / SetGrids / Initialize), 298489 (Thing.GridController => GridController.World), 350802-350821 (Device.CanConstruct using ConnectedDevices), 371598-371673 (Cable.CanConstruct / CanReplace using GridBounds + GetSmallCell)
related:
  - ../GameClasses/Cable.md
  - ../GameClasses/Cell.md
  - ../GameClasses/Grid3.md
  - ../GameClasses/Device.md
  - ../GameSystems/StructurePlacementValidation.md
tags: [prefab, ui]
---

# Cursor-time adjacency lookup

What a `CanConstruct` / `CanReplace` Harmony postfix can read out of the cursor ghost to decide whether a placement should be allowed, given the cells the ghost *would* occupy. Specifically: how to look up neighbour `Cable`s, `Device`s, `Pipe`s on the world grid without the ghost being registered.

## The cursor ghost is a fully-instantiated Structure with IsCursor = true
<!-- verified: 0.2.6228.27061 @ 2026-05-13 -->

`InventoryManager.HandleStructurePrefab(Thing prefab)` (line 268641) builds one cursor instance per prefab at startup:

```csharp
private void HandleStructurePrefab(Thing prefab)
{
    Structure structure = prefab as Structure;
    if (structure == null) return;
    structure.IsCursor = true;
    Structure.IsCursorCreating = true;
    Structure structure2 = UnityEngine.Object.Instantiate(structure, Vector3.zero, Quaternion.identity);
    structure2.CachePrefabBounds();
    structure.IsCursor = false;
    Structure.IsCursorCreating = false;
    structure2.tag = "Cursor";
    structure2.IgnoreSave = true;
    structure2.IsCursor = true;
    structure2.name = structure.name + "_cursor";
    structure2.transform.parent = _constructionCursorParent.transform;
    // ... destroys BaseAnimator, lights, colliders, TextMesh, RectTransform, swaps to wireframe ...
    SmallGrid component = structure2.GetComponent<SmallGrid>();
    if ((bool)component)
    {
        foreach (Assets.Scripts.Objects.Connection openEnd in component.OpenEnds)
        {
            // ... rebuild HelperRenderer on each open end ...
        }
    }
    _constructionCursors.Add(structure.name, structure2);
    structure2.gameObject.SetActive(value: false);
    ...
}
```

Three facts that matter for adjacency lookups:

1. The cursor is a real `Structure` Unity instance (not a stub) with intact `OpenEnds`, intact `GridBounds`, and the same C# subclass as the prefab. A solar-panel cursor is a `SolarPanel` instance; a cable cursor is a `Cable` instance.
2. There is **one cursor per prefab**, not one per placement. The instance is recycled across frames; its `transform.position` and `transform.rotation` are reassigned every frame by the placement-preview loop in `InventoryManager.Update`.
3. The cursor has `IsCursor == true`, `tag == "Cursor"`, `IgnoreSave == true`, and a "_cursor" suffix on its name. Renderers are wireframe-swapped; colliders, lights, animators, and text are destroyed. None of that affects grid math.

`base.CenterPosition` honours `IsCursor` and recomputes on the fly:

```csharp
public override Vector3 CenterPosition          // line 349661
{
    get
    {
        if (IsCursor)
            _centerPosition = base.ThingTransformPosition + ThingTransform.rotation * Bounds.center;
        return _centerPosition;
    }
}
```

So `CenterPosition`, `ThingTransform.position`, and `ThingTransform.rotation` are all current to the frame.

## GridController is always GridController.World on a Thing
<!-- verified: 0.2.6228.27061 @ 2026-05-13 -->

```csharp
public GridController GridController => GridController.World;   // line 298489 (on Thing)
```

`Thing.GridController` is a get-only property that hard-returns the global singleton `GridController.World`. The cursor ghost therefore always has a valid `GridController`. There is no "is this thing registered yet" branch -- the reference is permanent. (Each rocket has its own `GridController` for internals; `Thing.GridController` falls back to `GridController.World` and the rocket path is reached via `ParentGridController`. The cursor for a world placement is always against `GridController.World`.)

## The cell lookup pipeline: WorldToLocalGrid -> GetSmallCell -> SmallCell.{Cable|Device|Pipe|...}
<!-- verified: 0.2.6228.27061 @ 2026-05-13 -->

`GridController.GetSmallCell(Grid3 localPosition)` returns the `SmallCell` at a grid coordinate. `SmallCell` is a thin record:

```csharp
public class SmallCell                                          // line 274718
{
    public Grid3 SmallGrid;
    public Chute Chute;
    public Pipe Pipe;
    public Device Device;
    public Cable Cable;
    public SmallGrid Other;
    public ISmallGridOwner Owner;
    public IRoboticArmRail Rail;

    public static T Get<T>(Vector3 worldPosition) where T : IReferencable
    {
        return Get<T>(GridController.World.WorldToLocalGrid(
            worldPosition, SmallGrid.SmallGridSize, SmallGrid.SmallGridOffset));
    }
    public static T Get<T>(Grid3 localPosition) where T : IReferencable { ... }
}
```

There is also a typed helper on `GridController` for cables specifically:

```csharp
public Cable GetCable(Grid3 localPosition)                      // line 192276
{
    SmallCell smallCell = GetSmallCell(localPosition);
    if (smallCell != null && (bool)smallCell.Cable
        && smallCell.Cable.CableNetwork != null
        && smallCell.Cable.CableNetwork.IsNetworkValid())
        return smallCell.Cable;
    return null;
}
```

Both `SmallCell.Cable` (the raw cell occupant) and `GridController.GetCable(localGrid)` (the network-validated occupant) are usable. The raw `SmallCell.Cable` field carries a registered cable instance and exposes `cable.CableType` directly.

## ConnectedCables(NetworkType.Power): the canonical "find adjacent cables" implementation
<!-- verified: 0.2.6228.27061 @ 2026-05-13 -->

The same code that drives `Device.FindPowerCable` (line 350778) is used by every cable lookup in the game. `SmallGrid.ConnectedCables(NetworkType networkType)` (line 294319):

```csharp
public List<Cable> ConnectedCables(NetworkType networkType)
{
    List<Cable> list = new List<Cable>(4);
    foreach (Connection openEnd in OpenEnds)
    {
        if ((openEnd.ConnectionType & networkType) != NetworkType.None)
        {
            Grid3 localGrid = base.GridController.WorldToLocalGrid(
                openEnd.Transform.position, SmallGridSize, SmallGridOffset);
            SmallCell smallCell = base.GridController.GetSmallCell(localGrid);
            if (smallCell != null && smallCell.Cable != null
                && smallCell.Cable.CableNetwork != null
                && smallCell.Cable != this
                && smallCell.Cable.IsConnected(openEnd))
            {
                list.Add(smallCell.Cable);
            }
        }
    }
    return list;
}
```

Six things this depends on, each verified valid on a cursor ghost:

1. `OpenEnds` -- serialized on the prefab, intact on the cursor instance. The placement-preview loop wires up new `HelperRenderer` objects on each open end (line 268691) but does not mutate the underlying `Connection` list.
2. `openEnd.Transform.position` -- the connection's `Transform` is a child of the structure's transform, so its world position follows the cursor as it moves frame to frame.
3. `(openEnd.ConnectionType & networkType) != NetworkType.None` -- pure prefab data.
4. `base.GridController` -- `GridController.World` singleton, always valid.
5. `WorldToLocalGrid(worldPos, SmallGridSize, SmallGridOffset)` -- pure math against the world grid singleton.
6. `GetSmallCell(localGrid)` -- reads the world's registered cells. Cables already placed live here; the cursor itself is not in `SmallCell.Cable` because it is not registered. So `smallCell.Cable != this` is trivially true for a cursor (`this` is not in any cell).

This is the same lookup pattern used by:

- `SmallGrid.ConnectedDevices()` (line 294359) -- driven by `Device.CanConstruct` (line 350802-350821, which already runs on the cursor ghost and emits `PlacementBlockedByAdjacentDevice` when a same-cell device is found).
- `SmallGrid.ConnectedPipes()` / `ConnectedChutes()` / `ConnectedPipesAndDevices()` / `ConnectedRoboticArms()` (lines 294282 - 294390) -- same body shape, different SmallCell field per network type.
- `SmallGrid.IsConnectingToUmbilical(out IUmbilical found)` (line 294426) -- `Cable.CanConstruct` (line 371598) already calls this on the cursor ghost.

Every one of these is a `CanConstruct` consumer or feeds a `CanConstruct` consumer in vanilla, so cursor-time validity of the entire pipeline is established by working code, not by speculation.

## Connection.SetGrids: cursor open ends refresh per frame
<!-- verified: 0.2.6228.27061 @ 2026-05-13 -->

`Connection.GetSmallGridOccupant<T>` (line 293295) caches its `LocalGrid` lazily via `Initialize()` -> `SetGrids()`. The cache is invalidated for cursor ghosts:

```csharp
public bool Initialize()                                       // line 293367
{
    if (_isInitialized && !Parent.IsCursor)
        return true;
    if (ThreadedManager.IsThread)
        return _isInitialized;
    Validate();
    SetGrids();
    CacheTransformUp();
    return IsValid;
}

public void SetGrids()                                         // line 293383
{
    if (IsValid)
    {
        if (!Parent.IsCursor)
            _isInitialized = true;
        Vector3 position = Transform.position;
        LocalGrid  = Parent.GridController.WorldToLocalGrid(position,                                  SmallGrid.SmallGridSize, SmallGrid.SmallGridOffset);
        FacingGrid = Parent.GridController.WorldToLocalGrid(position + Transform.forward * SmallGrid.SmallGridSize,
                                                            SmallGrid.SmallGridSize, SmallGrid.SmallGridOffset);
    }
}
```

`Parent.IsCursor` guards both the cache short-circuit (`if (_isInitialized && !Parent.IsCursor) return true`) and the cache-set (`if (!Parent.IsCursor) _isInitialized = true`). The effect: a cursor `Connection` recomputes `LocalGrid` and `FacingGrid` every time `Initialize()` is hit, which happens lazily on the first `GetCable` / `GetDevice` / etc. call per frame. So `openEnd.LocalGrid` -- and by extension `Connection.GetCable()`, `GetDevice()`, `GetPipe()` -- is current to the cursor's current `transform.position` for the frame, not stale from a previous frame.

## Cable.CanReplace as worked example of cursor-time grid lookup
<!-- verified: 0.2.6228.27061 @ 2026-05-13 -->

`Cable.CanReplace` (line 371607) runs on the cursor ghost and explicitly queries `GetSmallCell` for the cells the ghost would occupy:

```csharp
public CanConstructInfo CanReplace(MultiConstructor constructor, Item inactiveHandItem)
{
    if (base.Indestructable)
        return CanConstructInfo.InvalidPlacement(GameStrings.CannotMergeIMergeable.AsString(ToTooltip()));
    MultiMergeConstructor multiMergeConstructor = constructor as MultiMergeConstructor;
    if (multiMergeConstructor == null)
        return CanConstructInfo.InvalidPlacement(GameStrings.CannotMergeIMergeable.AsString(ToTooltip()));

    Grid3[] array = (Grid3[])GridBounds.GetLocalSmallGrid(
        base.ThingTransformPosition, base.ThingTransformRotation);
    foreach (Grid3 localGrid in array)
    {
        SmallCell smallCell = base.GridController.GetSmallCell(localGrid);
        if (smallCell == null || !smallCell.Cable) continue;
        if (inactiveHandItem == null)
            return CanConstructInfo.InvalidPlacement(...);
        if (multiMergeConstructor.ToolExit.PrefabHash == inactiveHandItem.PrefabHash || ...)
        {
            if (WillMergeWhenPlaced()) Cable.OnMerge?.Invoke(this);
            if (smallCell.Cable.CableType != CableType)
                return CanConstructInfo.InvalidPlacement(
                    GameStrings.CannotMergeIMergeableOfDifferentType.AsString(smallCell.Cable.DisplayName));
            ...
        }
    }
    ...
}
```

Note the path used here: `GridBounds.GetLocalSmallGrid(ThingTransformPosition, ThingTransformRotation)` returns the array of `Grid3` cells the cursor *would* occupy. That + `GridController.GetSmallCell(localGrid)` gives the existing occupants of each of those cells. The very next line reads `smallCell.Cable.CableType` and rejects on tier mismatch. This is identical in shape to what a per-tier device-placement check needs.

For multi-cell devices (a transformer occupies more than one cell), this `GridBounds.GetLocalSmallGrid` enumeration is the right tool. For single-cell open-end-based devices (a light, a fabricator, a solar panel), `ConnectedCables(NetworkType.Power)` is the more targeted query because it follows the device's actual connection points, not its bounding cells.

## What is NOT set on a cursor ghost
<!-- verified: 0.2.6228.27061 @ 2026-05-13 -->

Confirmed not-populated state, in case a postfix is tempted to read it:

- `Device.PowerCable` and `Device.DataCable` -- only `FindPowerCable` / `FindDataCable` assign them, and those run in `InitializeDataConnection` which only fires from `Device.OnRegistered`, `Device.OnNeighborPlaced`, `Device.OnNeighborRemoved`, and `InitAllDevices`. None of those run on the cursor (line 350999 explicitly guards on `GameManager.GameState != GameState.Loading` and is only called from the registration chain).
- `Device.PowerCables` / `Device.DataCables` -- same backing as the singular `PowerCable` / `DataCable`, populated by the same `FindPowerCable`/`FindDataCable` calls.
- `Device.ConnectedCableNetworks` / `ConnectedPipeNetworks` / `ConnectedChuteNetworks` -- populated only by `CableNetwork.AddDevice` (line 253836) etc., which only run on registered devices.
- `Device.AttachedCables` -- same; only `CableNetwork.AddDevice` appends.
- `Cable.CableNetwork` -- the placement-preview ghost does not have a `CableNetwork` until `Cable.OnRegistered` runs.
- `Cable.CableNetworkId` -- ReferenceId, only assigned when the network is created.
- Cell-side `SmallCell` for the ghost's own cells -- the ghost is not in `SmallCell.Cable` / `SmallCell.Device` because it is not registered. Adjacency lookups read OTHER occupants of those cells, which is exactly what is wanted.

The transform-side state (position, rotation, `OpenEnds[i].Transform`, `LocalGrid` via `Connection.SetGrids`) is fully current.

## Recipe for a Device.CanConstruct postfix that needs the would-attach cable
<!-- verified: 0.2.6228.27061 @ 2026-05-13 -->

Skeleton, given the verifications above:

```csharp
[HarmonyPostfix, HarmonyPatch(typeof(Device), nameof(Device.CanConstruct))]
public static void Device_CanConstruct_Postfix(Device __instance, ref CanConstructInfo __result)
{
    if (!__result.CanConstruct) return;                  // don't override an earlier rejection
    // ConnectedCables(NetworkType.Power) is safe on the cursor ghost: OpenEnds + GridController +
    // WorldToLocalGrid + GetSmallCell are all valid; Connection.SetGrids refreshes LocalGrid per frame
    // for Parent.IsCursor (Research/Patterns/CursorAdjacencyLookup.md).
    List<Cable> candidates = __instance.ConnectedCables(NetworkType.Power);
    if (candidates == null || candidates.Count == 0)
        return;                                          // unwired placement -- not our concern
    Cable wouldAttachTo = candidates[0];                 // mirrors Device.FindPowerCable
    Cable.Type tier = wouldAttachTo.CableType;
    if (!ModPolicy.IsDeviceAllowedOnTier(__instance, tier))
    {
        __result = CanConstructInfo.InvalidPlacement(ModPolicy.FormatRejection(__instance, tier));
    }
}
```

Notes:

- `candidates[0]` mirrors `FindPowerCable`'s first-element pick, so the postfix sees the same cable the device would adopt as `PowerCable` once registered. For multi-port devices (`ElectricalInputOutput`) the device picks input vs output by `ConnectionRole` in `CheckConnections` after registration; for the tier check the *union of tiers across all candidates* is the right read, since a placement that connects to two tiers at once is still an illegal junction.
- A multi-cell device that touches several existing networks in one placement is correctly handled by iterating `candidates` and emitting a rejection if *any* candidate is on a forbidden tier.
- Empty `candidates` means the device is being placed somewhere with no adjacent cable. That is legal -- vanilla allows an unwired device.

## Verification history

- 2026-05-13: page created. Sourced from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` lines 268641-268712 (InventoryManager.HandleStructurePrefab cursor instantiation), 274718-274830 (SmallCell type + lookups), 293285-293400 (Connection.GetSmallGridOccupant + SetGrids), 294267-294390 (SmallGrid.ConnectedCables / ConnectedDevices / ConnectedPipes), 294426-294439 (IsConnectingToUmbilical), 298489 (Thing.GridController), 350778-350821 (Device.FindPowerCable + CanConstruct), 371598-371673 (Cable.CanConstruct / CanReplace). Cross-referenced against the existing Cable.md, Cell.md, Grid3.md pages and the prior phase 3 spike on StructurePlacementValidation.md. The postfix skeleton in the last section is unverified pseudocode (it has not been compiled or play-tested) but every API it calls is verified valid on the cursor ghost.

## Open questions

- Whether `__instance` on a `Cable.CanConstruct` postfix vs a `Device.CanConstruct` postfix is reliably the cursor ghost in all placement modes (multi-kit, multi-merge constructor merging, ZoopMod replays, the BlueprintMod paste path). For the standard placement-preview loop driven by `InventoryManager.HandleStructurePrefab` it is verified. BlueprintMod's `OnServer.Create<Thing>` path bypasses `CanConstruct` entirely (per the NetworkPuristPlus comment in `RewriteLongVariantOnConstructPatch.cs:18-19`), so blueprint-pasted placements need a separate hook -- not a cursor-time concern.
