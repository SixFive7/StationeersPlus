---
title: Cursor-time adjacency lookup (find neighbour cells from a placement ghost)
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-02
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.SmallGrid, Assets.Scripts.Objects.Connection, Assets.Scripts.Objects.SmallCell, Assets.Scripts.Inventory.InventoryManager
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 268641-268712 (InventoryManager.HandleStructurePrefab cursor instantiation), 274718-274830 (SmallCell), 292000-294500 (SmallGrid.ConnectedCables / ConnectedDevices / ConnectedPipes / IsConnectingToUmbilical / Connection), 293285-293400 (Connection.GetSmallGridOccupant / SetGrids / Initialize), 298489 (Thing.GridController => GridController.World), 350802-350821 (Device.CanConstruct using ConnectedDevices), 371598-371673 (Cable.CanConstruct / CanReplace using GridBounds + GetSmallCell)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 311786-312023 (Connection incl. GetSmallGridOccupant/Initialize/SetGrids/GetLocalGrid), 312025 (SmallGrid class), 312730 (IsConnected), 312804-312942 (FillConnected overloads), 290539-290700 (SmallCellType + SmallCellRef), 293005-293079 (SmallCell.Get overloads), 371568-371638 (Device.FindDataCable/FindPowerCable/CanConstruct), 392634-392672 (Cable.CanReplace)
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
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

`GridController.GetSmallCell(Grid3 localPosition)` returns the `SmallCell` at a grid coordinate. `SmallCell` is a thin record (occupant fields `Chute` / `Pipe` / `Device` / `Cable` / `Other` / `Owner` / `Rail`) with four static lookup helpers at 0.2.6403.27689:

```csharp
public class SmallCell
{
    public Grid3 SmallGrid;
    public Chute Chute;
    public Pipe Pipe;
    public Device Device;
    public Cable Cable;
    public SmallGrid Other;
    public ISmallGridOwner Owner;
    public IRoboticArmRail Rail;

    public static T Get<T>(Vector3 worldPosition) where T : IReferencable      // line 293005
    public static T Get<T>(Grid3 localPosition) where T : IReferencable        // line 293010: first occupant slot that is T
    public static T Get<T>(Grid3 localPosition, Connection connection)         // line 293046: slot is T AND
        where T : ISmallGrid                                                   //   occupant.IsConnected(connection)
    public static T Get<T>(Vector3 worldPosition, Connection connection)       // line 293076
        where T : ISmallGrid
}
```

The two `Connection`-taking overloads are the cursor-relevant additions: they fold the `IsConnected(openEnd)` agreement check into the lookup, which is exactly the filter the removed `ConnectedCables` walk applied.

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

## FillConnected replaced ConnectedCables/ConnectedDevices at 0.2.6403 (API migration)
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

SUPERSEDED at 0.2.6403.27689: `SmallGrid.ConnectedCables()`, `ConnectedCables(NetworkType)`, `ConnectedDevices()`, and the static `FoundCables` buffer are REMOVED from the game (whole-decompile grep: zero hits). The replacement is a family of allocation-free Span fillers on `SmallGrid` (class at line 312025):

- `FillConnected<T>(Span<SmallCellRef> buf, ref int count) where T : ISmallGrid` (312804)
- `FillConnected(Span<SmallCellRef> buf, ref int count)` (312852, untyped)
- `FillConnected<T>(NetworkType networkType, Span<SmallCellRef> buf, ref int count) where T : SmallGrid` (312896, the `ConnectedCables(NetworkType)` successor)

All three walk `OpenEnds` in declaration order, project `openEnd.Transform.position` through `WorldToLocalGrid`, fetch the `SmallCell`, and append one `SmallCellRef` per occupant slot (checked Cable, Chute, Device, Pipe, Rail, Other in that fixed order) that matches `T`, is not `this`, and agrees via `IsConnected(openEnd)`. `SmallGrid.IsConnected(Connection)` is unchanged (312730). Game call sites migrated onto this API: `Device.FindDataCable` (371568), `Device.FindPowerCable` (371588), `Device.CanConstruct` (371617), `CableNetwork.Add` (271075), `CableNetwork.RebuildNetwork` (271151), `Cable.OnDestroy` (392573).

`SmallCellRef` (`Assets.Scripts.GridSystem`, readonly struct at 290601; `BUFFER_SIZE = 32` at 290603) stores only a `Grid3` plus a `SmallCellType` (enum at 290539: Pipe, Device, Cable, Chute, Rail, Other) and re-resolves lazily via `GetCell()` / `Get()` / `Get<T>()` / `TryGet<T>()` (290621-290700) against `GridController.World.GetSmallCell`. Refs can go stale (the occupant leaves the cell); a stale ref's `Get<T>()` returns null.

### Modding constraint: net472 mods cannot bind the Span signatures

Assembly-CSharp 0.2.6403's ``Span`1`` TypeRefs resolve to Unity Mono's mscorlib (no `System.Memory.dll` ships with the game), so a net472 mod compiling against the game DLLs cannot reference the `FillConnected` overloads (the compiler cannot unify the mod's `System.Memory` `Span<T>` with the game's mscorlib `Span<T>`). The practical mod-side equivalent of the old `ConnectedCables(NetworkType.Power)` query is the `SmallCell.Get<T>(Grid3, Connection)` family:

```csharp
// per open end of interest:
if ((openEnd.ConnectionType & NetworkType.Power) == NetworkType.None) continue;
Grid3 grid = openEnd.GetLocalGrid();                       // Connection.GetLocalGrid, line 312009:
                                                           // cached grid when initialized, live
                                                           // Transform fallback on cursor ghosts
Cable cable = SmallCell.Get<Cable>(grid, openEnd);         // line 293046: occupant slot is T AND
                                                           // occupant.IsConnected(openEnd)
```

`SmallCell.Get<T>(Grid3 localPosition, Connection connection)` (293046-293074) checks each occupant slot (Chute, Pipe, Device, Cable, Rail, Other order) for `is T` plus `IsConnected(connection)`; the sibling overloads are `Get<T>(Vector3)` (293005), `Get<T>(Grid3)` (293010, no connection check), and `Get<T>(Vector3, Connection)` (293076). Combined with `Connection.GetLocalGrid()` (312009: returns the cached `LocalGrid` when `_isInitialized`, else computes live from `Transform.position`, which is exactly what a cursor ghost needs), this reproduces the removed API with the same per-open-end semantics.

## Connection.SetGrids: cursor connections are never initialized; occupant getters return null on a cursor
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

`Connection` (class at 311786 at 0.2.6403.27689) caches its `LocalGrid` lazily via `Initialize()` -> `SetGrids()`:

```csharp
public bool Initialize()                                       // line 311918
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

public void SetGrids()                                         // line 311934
{
    if (IsValid)
    {
        if (!Parent.IsCursor)
            _isInitialized = true;
        Vector3 position = Transform.position;
        LocalGrid  = Parent.GridController.WorldToLocalGrid(position, SmallGrid.SmallGridSize, SmallGrid.SmallGridOffset);
        FacingGrid = Parent.GridController.WorldToLocalGrid(position + Transform.forward * SmallGrid.SmallGridSize,
                                                            SmallGrid.SmallGridSize, SmallGrid.SmallGridOffset);
    }
}
```

`Parent.IsCursor` guards both the cache short-circuit and the cache-set, so a cursor `Connection` recomputes `LocalGrid` / `FacingGrid` on every `Initialize()` and `_isInitialized` STAYS FALSE forever on a cursor-parented connection.

**Cursor caveat (supersedes the earlier claim that `Connection.GetCable()` etc. work on a cursor ghost):** the occupant getters all route through `private T GetSmallGridOccupant<T>(bool connected = true)` (line 311846), which after `Initialize()` bails with `null` when `!_isInitialized`:

```csharp
private T GetSmallGridOccupant<T>(bool connected = true) where T : SmallGrid   // line 311846
{
    Initialize();
    if (!_isInitialized || !Transform || !Parent || Parent.GridController == null)
        return null;
    T val = SmallCell.Get<T>(LocalGrid);
    ...
}
```

Since `SetGrids` never sets `_isInitialized` for a cursor parent, `GetSmallGridOccupant` ALWAYS returns null on a cursor ghost's own OpenEnds, and with it the whole getter family: `GetDevice` (311869), `GetChute` (311874), `GetPipe` (311879), `GetINetworkedPipe` (311884), `GetCable` (311890), `GetOther` (311895), `GetChuteOrDevice` (311900). On registered (non-cursor) structures these getters are the convenient occupant lookups (`ElectricalInputOutput.CheckConnections` uses `InputConnection.GetCable()`).

Cursor-time adjacency must therefore use the live-fallback accessors plus the connection-checked `SmallCell.Get`:

```csharp
public Grid3 GetLocalGrid()                                    // line 312009
{
    if (_isInitialized)
        return LocalGrid;
    return Transform.position.ToGrid(SmallGrid.SmallGridSize, SmallGrid.SmallGridOffset);
}
// GetFacingGrid() at 312000 has the same shape for the facing cell.
```

`openEnd.GetLocalGrid()` on a cursor connection computes from the live `Transform.position` (current to the frame), and `SmallCell.Get<Cable>(grid, openEnd)` (293046) then performs the occupant + `IsConnected(openEnd)` check without ever touching `_isInitialized`. That pair is the supported cursor-time lookup.

## Cable.CanReplace as worked example of cursor-time grid lookup
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

`Cable.CanReplace` (line 392634 at 0.2.6403.27689, shape unchanged) runs on the cursor ghost and explicitly queries `GetSmallCell` for the cells the ghost would occupy:

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
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

Confirmed not-populated state, in case a postfix is tempted to read it:

- `Device.PowerCable` and `Device.DataCable` -- only `FindPowerCable` / `FindDataCable` assign them, and those run in `InitializeDataConnection` (371609) which only fires from `Device.OnRegistered`, `Device.OnNeighborPlaced`, `Device.OnNeighborRemoved`, and `InitAllDevices`. None of those run on the cursor.
- `Device.PowerCables` / `Device.DataCables` -- `Cable[]` auto-properties at 0.2.6403 (370460 / 370462, default `Array.Empty<Cable>()`), rebuilt only by the same `FindPowerCable` / `FindDataCable` calls; empty arrays on a cursor.
- `Device.ConnectedCableNetworks` / `ConnectedPipeNetworks` / `ConnectedChuteNetworks` -- populated only by `CableNetwork.AddDevice` etc., which only run on registered devices.
- `Device.AttachedCables` -- same; only `CableNetwork.AddDevice` appends.
- `Cable.CableNetwork` -- the placement-preview ghost does not have a `CableNetwork` until `Cable.OnRegistered` runs.
- `Cable.CableNetworkId` -- ReferenceId, only assigned when the network is created.
- `Connection._isInitialized` on the ghost's own OpenEnds -- never set for a cursor parent, so `Connection.GetCable()` / `GetDevice()` / the other occupant getters return null on the ghost (see the SetGrids section above). Use `GetLocalGrid()` + `SmallCell.Get<T>(grid, openEnd)` instead.
- Cell-side `SmallCell` for the ghost's own cells -- the ghost is not in `SmallCell.Cable` / `SmallCell.Device` because it is not registered. Adjacency lookups read OTHER occupants of those cells, which is exactly what is wanted.

The transform-side state (position, rotation, `OpenEnds[i].Transform`, and `GetLocalGrid()`'s live fallback) is fully current.

## Recipe for a Device.CanConstruct postfix that needs the would-attach cable
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

Skeleton at 0.2.6403.27689 (the old `ConnectedCables(NetworkType.Power)` call is gone from the game, and the `FillConnected` Span signatures are not bindable from net472 mods; iterate `OpenEnds` and use `SmallCell.Get<Cable>(grid, openEnd)` instead):

```csharp
[HarmonyPostfix, HarmonyPatch(typeof(Device), nameof(Device.CanConstruct))]
public static void Device_CanConstruct_Postfix(Device __instance, ref CanConstructInfo __result)
{
    if (!__result.CanConstruct) return;                  // don't override an earlier rejection
    // Cursor-safe adjacency: openEnd.GetLocalGrid() computes from the live Transform.position on a
    // cursor ghost (Connection._isInitialized is never set for cursor parents), and
    // SmallCell.Get<Cable>(grid, openEnd) applies the occupant + IsConnected(openEnd) check the
    // removed ConnectedCables walk used. Do NOT use openEnd.GetCable(): the occupant getters
    // return null on cursor-parented connections.
    Cable first = null;
    foreach (Connection openEnd in __instance.OpenEnds)
    {
        if ((openEnd.ConnectionType & NetworkType.Power) == NetworkType.None)
            continue;
        Cable candidate = SmallCell.Get<Cable>(openEnd.GetLocalGrid(), openEnd);
        if (candidate == null || candidate == __instance as SmallGrid)
            continue;
        first = first ?? candidate;                       // first hit in OpenEnds order mirrors
                                                          // FindPowerCable's PowerCables[0] pick
        Cable.Type tier = candidate.CableType;
        if (!ModPolicy.IsDeviceAllowedOnTier(__instance, tier))
        {
            __result = CanConstructInfo.InvalidPlacement(ModPolicy.FormatRejection(__instance, tier));
            return;
        }
    }
    // first == null means an unwired placement -- legal in vanilla.
}
```

Notes:

- The first `OpenEnds`-order hit mirrors `FindPowerCable`'s `PowerCables[0]` pick, so the postfix sees the same cable the device would adopt as `PowerCable` once registered. For multi-port devices (`ElectricalInputOutput`) the device picks input vs output by `ConnectionRole` in `CheckConnections` after registration; for a tier check, evaluating EVERY candidate (as the loop above does) is the right read, since a placement that connects to two tiers at once is still an illegal junction.
- No candidates means the device is being placed somewhere with no adjacent cable. That is legal -- vanilla allows an unwired device.
- The skeleton is unverified pseudocode (not compiled or play-tested at 0.2.6403); every API it calls is decompile-verified as described above.

## Verification history

- 2026-07-02: grid-adjacency API migration pass against the 0.2.6403.27689 decompile after the game update from 0.2.6228.27061. Two supersessions: (a) `SmallGrid.ConnectedCables()` / `ConnectedCables(NetworkType)` / `ConnectedDevices()` and the static `FoundCables` buffer are REMOVED from the game; the "canonical find-adjacent-cables" section is rewritten around the replacement `FillConnected` Span fillers (312804 / 312852 / 312896 over `SmallCellRef`, 290601) plus the net472 modding constraint (Assembly-CSharp's Span TypeRefs resolve to Unity Mono's mscorlib, no System.Memory.dll ships, so mods cannot bind the FillConnected signatures) and the mod-side replacement pattern `Connection.GetLocalGrid()` (312009) + `SmallCell.Get<T>(Grid3, Connection)` (293046, overloads 293005 / 293010 / 293076). (b) The earlier claim that `Connection.GetCable()` / `GetDevice()` / `GetPipe()` are cursor-current is superseded: `GetSmallGridOccupant<T>` (311846) bails with null when `!_isInitialized`, and `SetGrids` (311934) never initializes cursor-parented connections, so ALL occupant getters return null on a cursor ghost's own OpenEnds; the cursor-safe pair is `GetLocalGrid()` (live-Transform fallback) + the connection-checked `SmallCell.Get`. Re-verified: `SmallGrid.IsConnected(Connection)` unchanged (312730), `Device.CanConstruct` still runs the same WorldToLocalGrid + GetSmallCell pipeline on the cursor (now via `FillConnected<Device>`, 371617-371638), `Cable.CanReplace` shape unchanged (392634), `Device.DataCables` / `PowerCables` are now `Cable[]` (370460 / 370462). Rewrote the recipe skeleton onto the new pattern. The InventoryManager cursor-instantiation and Thing.GridController sections were not re-read this pass and keep their 0.2.6228.27061 stamps.
- 2026-05-13: page created. Sourced from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` lines 268641-268712 (InventoryManager.HandleStructurePrefab cursor instantiation), 274718-274830 (SmallCell type + lookups), 293285-293400 (Connection.GetSmallGridOccupant + SetGrids), 294267-294390 (SmallGrid.ConnectedCables / ConnectedDevices / ConnectedPipes), 294426-294439 (IsConnectingToUmbilical), 298489 (Thing.GridController), 350778-350821 (Device.FindPowerCable + CanConstruct), 371598-371673 (Cable.CanConstruct / CanReplace). Cross-referenced against the existing Cable.md, Cell.md, Grid3.md pages and the prior phase 3 spike on StructurePlacementValidation.md. The postfix skeleton in the last section is unverified pseudocode (it has not been compiled or play-tested) but every API it calls is verified valid on the cursor ghost.

## Open questions

- Whether `__instance` on a `Cable.CanConstruct` postfix vs a `Device.CanConstruct` postfix is reliably the cursor ghost in all placement modes (multi-kit, multi-merge constructor merging, ZoopMod replays, the BlueprintMod paste path). For the standard placement-preview loop driven by `InventoryManager.HandleStructurePrefab` it is verified. BlueprintMod's `OnServer.Create<Thing>` path bypasses `CanConstruct` entirely (per the NetworkPuristPlus comment in `RewriteLongVariantOnConstructPatch.cs:18-19`), so blueprint-pasted placements need a separate hook -- not a cursor-time concern.
