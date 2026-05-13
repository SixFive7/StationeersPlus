---
title: Device
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-13
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.Device
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 349588-351055 (Device class header, fields, properties, FindPowerCable, InitializeDataConnection, OnRegistered, OnNeighborPlaced, OnNeighborRemoved, CanConstruct), 253820-253850 (CableNetwork.AddDevice -> ConnectedCableNetworks.Add)
  - Plans/PowerGridPlus/PLAN.md, Mods/PowerGridPlus/RESEARCH.md
related:
  - ./Cable.md
  - ./CableNetwork.md
  - ../GameSystems/StructurePlacementValidation.md
  - ../Patterns/CursorAdjacencyLookup.md
tags: [power, prefab]
---

# Device

Vanilla powered-structure base class. `Assets.Scripts.Objects.Pipes.Device : SmallGrid, ILogicable, IReferencable, IEvaluable, IConnected, ISlotWriteable, IWreckage, IPowered, IDensePoolable` (line 349588). The base type of every grid-mounted structure that has at least one `Connection` and that participates in a `CableNetwork` / `PipeNetwork` / `ChuteNetwork`. Vanilla electrical machines (lights, fabricators, generators, transformers, batteries, APCs, etc.) all derive from `Device` (often through `ElectricalInputOutput` for two-port devices).

## Class header, key fields, public surface
<!-- verified: 0.2.6228.27061 @ 2026-05-13 -->

```csharp
public class Device : SmallGrid, ILogicable, IReferencable, IEvaluable, IConnected,
                      ISlotWriteable, IWreckage, IPowered, IDensePoolable
{
    [Header("Device")]
    [Tooltip("How much power (in Watts) does the device used while turned on.")]
    public float UsedPower = 10f;

    [ReadOnly] public List<ChuteNetwork> ConnectedChuteNetworks = new List<ChuteNetwork>();
    [ReadOnly] public List<PipeNetwork>  ConnectedPipeNetworks  = new List<PipeNetwork>();
    [ReadOnly] public List<CableNetwork> ConnectedCableNetworks = new List<CableNetwork>();
    [ReadOnly] public List<Cable>        AttachedCables         = new List<Cable>();

    public Cable DataCable  { get; set; }
    public Cable PowerCable { get; private set; }       // line 349659

    public List<Cable> DataCables  { get; private set; } = new List<Cable>();
    public List<Cable> PowerCables { get; private set; } = new List<Cable>();

    public CableNetwork DataCableNetwork  => DataCable  == null ? null : DataCable.CableNetwork;
    public CableNetwork PowerCableNetwork => PowerCable == null ? null : PowerCable.CableNetwork;

    public Connection DataConnection => OpenEnds.Find(c =>
        c.ConnectionType == NetworkType.Data || c.ConnectionType == NetworkType.PowerAndData);

    public virtual bool IsPowerProvider     => false;
    public virtual bool IsPowerInputOutput  => false;
}
```

Subclasses of note: `Transformer : ElectricalInputOutput : Device` (two ports, `Input` / `Output` `ConnectionRole`), `PowerTransmitter : Device`, `Battery : Device`, `SolarPanel : Device : IPowerGenerator`, every furnace / fabricator / pipe valve / chute conveyor. `DeviceCableMounted : Device` is the in-cable family (`CableFuse`, `CableAnalyser`) -- it sits **inside** a cable's grid cell rather than adjacent to one, but otherwise behaves like a `Device`.

## PowerCable resolution: every assignment goes through FindPowerCable
<!-- verified: 0.2.6228.27061 @ 2026-05-13 -->

`Device.PowerCable` is a property with a public getter and a private setter, so it can only be assigned from inside `Device` itself. There is exactly one place that assigns it: `Device.FindPowerCable()` (line 350778).

```csharp
private void FindPowerCable()
{
    PowerCables = new List<Cable>(ConnectedCables(NetworkType.Power));
    using (List<Cable>.Enumerator enumerator = PowerCables.GetEnumerator())
    {
        if (enumerator.MoveNext())
        {
            Cable current = enumerator.Current;
            PowerCable = current;
            return;
        }
    }
    PowerCable = null;
    AssessPower(null, OnOff);
}
```

So `PowerCable` is *always* the first element of `ConnectedCables(NetworkType.Power)`. `ConnectedCables(NetworkType networkType)` (line 294319 on `SmallGrid`) walks the device's `OpenEnds`, projects each end's transform position through `GridController.WorldToLocalGrid`, fetches the `SmallCell` at that grid coordinate, and returns any adjacent `Cable` whose own `IsConnected(openEnd)` agrees. See [CursorAdjacencyLookup](../Patterns/CursorAdjacencyLookup.md) for the verbatim implementation and the cursor-time validity of every step.

There is **no direct `PowerCable = ...` write from a cable-side trigger, a `CableNetwork.Merge` hook, or anywhere else in `Assembly-CSharp`.** `Cable.OnRegistered(Cell)` (line 371477) only runs `CableNetwork.Merge(CableNetwork.ConnectedNetworks(this)).Add(this)`; it does not push itself into adjacent devices. The wiring is always pulled from the device side.

The decomp grep confirms it: every reference to `set_PowerCable` resolves to lines 350786 (`PowerCable = current;`) and 350790 (`PowerCable = null;`), both inside `FindPowerCable`. No other write site exists.

## InitializeDataConnection: the wrapper that finds both cables and refreshes network membership
<!-- verified: 0.2.6228.27061 @ 2026-05-13 -->

```csharp
public void InitializeDataConnection()                                          // line 350794
{
    FindDataCable();
    FindPowerCable();
    DataCableNetwork ?.DirtyPowerAndDataDeviceLists();
    PowerCableNetwork?.DirtyPowerAndDataDeviceLists();
}
```

`FindDataCable` (line 350763) is the data-cable twin of `FindPowerCable`, again pulling from `ConnectedCables(NetworkType.Data)` and taking the first result.

`InitializeDataConnection` is the single entry point that refreshes `(Power|Data)Cable` after the world changes shape around the device.

## When InitializeDataConnection runs (i.e. when PowerCable becomes valid)
<!-- verified: 0.2.6228.27061 @ 2026-05-13 -->

Three call sites in vanilla, all on the live world (not on cursor ghosts):

```csharp
public override void OnRegistered(Cell cell)                                    // line 350999
{
    _centerPosition = base.ThingTransformPosition + ThingTransform.rotation * Bounds.center;
    base.OnRegistered(cell);
    LocalGrid = base.GridController.WorldToLocalGrid(_centerPosition);
    AtmosphericsManager.Instance.Register(this);
    AllDevices.Add(this);
    if (GameManager.GameState != GameState.Loading)
    {
        InitializeDataConnection();
        InitializeDevice();
    }
}

public override void OnNeighborPlaced(SmallGrid neighbor)                       // line 351038
{
    base.OnNeighborPlaced(neighbor);
    if (GameManager.GameState != GameState.Loading)
        InitializeDataConnection();
}

public override void OnNeighborRemoved(SmallGrid neighbor)                      // line 351047
{
    base.OnNeighborRemoved(neighbor);
    InitializeDataConnection();
}

public static readonly Action<Device> InitializeDeviceAction = delegate(Device device)   // line 349636
{
    if (!(device == null) && !device.IsBeingDestroyed)
    {
        device.InitializeDevice();
        device.InitializeDataConnection();
    }
};

public static void InitAllDevices()                                              // line 351053
{
    AllDevices.ForEach(InitializeDeviceAction);
}
```

So `PowerCable` is set / refreshed on:

- device registration (`OnRegistered`, when a placed device first lands in a non-loading world)
- neighbour placement / removal (`OnNeighborPlaced` / `OnNeighborRemoved`, when a cable is built next to the device or removed)
- bulk init (`InitAllDevices`, called after save load and after multiplayer client join, see `OnFinishedLoad` plumbing in `GameManager`)

`InitializeDevice` (line 350958) additionally registers the device into every adjacent cable's `CableNetwork` (`item.CableNetwork.AddDevice(item, this)`, line 350962), which in turn appends the network to `device.ConnectedCableNetworks` (line 253836 inside `CableNetwork.AddDevice`).

## OnRegistered (live world) vs CanConstruct (cursor preview)
<!-- verified: 0.2.6228.27061 @ 2026-05-13 -->

`Device.CanConstruct()` (line 350802) runs on the **cursor ghost** every frame:

```csharp
public override CanConstructInfo CanConstruct()
{
    List<Device> list = ConnectedDevices();
    for (int num = list.Count - 1; num >= 0; num--)
    {
        if (list[num] is INetworkedPipe networkedPipe && !networkedPipe.ProhibitConnection(this))
            list.RemoveAt(num);
    }
    if (list.Count > 0 && list[0] != null)
        return CanConstructInfo.InvalidPlacement(GameStrings.PlacementBlockedByAdjacentDevice.AsString(list[0].DisplayName));
    if (list.Count > 0)
        return CanConstructInfo.InvalidPlacement(GameStrings.PlacementBlockedByUnknownDevice.DisplayString);
    return base.CanConstruct();
}
```

This is verbatim evidence that the same `ConnectedDevices()` (and by symmetry, `ConnectedCables(NetworkType.Power)`) adjacency query that `FindPowerCable` uses **already works on the cursor ghost at preview time**: `ConnectedDevices` (line 294359) drives the same `WorldToLocalGrid` + `GetSmallCell` pipeline. So `__instance.ConnectedCables(NetworkType.Power)` is callable inside a `Device.CanConstruct` postfix and returns the cables the device would attach to if it were placed at the cursor position.

Caveats:

- `PowerCable` itself is **not** set on the cursor ghost (no `FindPowerCable` runs during preview). A patch wanting the cable must call `ConnectedCables(NetworkType.Power)` directly on `__instance`.
- `ConnectedCableNetworks` is **not** populated on the cursor ghost either; that list is only appended by `CableNetwork.AddDevice` (line 253836), which only runs on the registered device.
- The cursor ghost has `IsCursor == true`, `tag == "Cursor"`, `IgnoreSave == true`, no colliders, no children Renderers swapped for ghost materials, and is reused across frames (one per prefab in `_constructionCursors`). It is *not* a fresh instance per placement.

See [CursorAdjacencyLookup](../Patterns/CursorAdjacencyLookup.md) for why the adjacency pipeline survives the cursor reuse (`Connection.SetGrids` rebuilds `LocalGrid` from `Transform.position` every frame for `Parent.IsCursor`).

## Power virtuals: where PowerCable gates a device
<!-- verified: 0.2.6228.27061 @ 2026-05-13 -->

The default power virtuals only deliver power when the calling network matches the device's own `PowerCable.CableNetwork`:

```csharp
public virtual float GetGeneratedPower(CableNetwork cableNetwork)   // line 350696
{
    if (PowerCable == null || PowerCable.CableNetwork != cableNetwork)
        return -1f;
    return 0f;
}

public virtual float GetUsedPower(CableNetwork cableNetwork)        // line 350705
{
    if (PowerCable == null || PowerCable.CableNetwork != cableNetwork)
        return -1f;
    if (!OnOff || !base.IsStructureCompleted)
        return 0f;
    return UsedPower;
}

public virtual bool AllowSetPower(CableNetwork cableNetwork)        // line 350726
{
    return PowerCableNetwork == cableNetwork;
}
```

A device whose `PowerCable` is null (no adjacent power cable) silently returns `-1f` from `GetUsedPower` / `GetGeneratedPower`, which `PowerTick` reads as "not on this network". Many subclasses override these three methods; the `PowerCable == null || PowerCable.CableNetwork != cableNetwork` guard is replicated almost verbatim across vanilla power devices (lines 165086, 169955, 344689, 345179, 350698, 350707, 359403, 371227, 374356, 375228, 387488, 397078, 398880, 401392).

## Verification history

- 2026-05-13: page created. Sourced from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` lines 349588-351055 (Device class header, fields, properties, FindPowerCable, InitializeDataConnection, OnRegistered, OnNeighborPlaced, OnNeighborRemoved, CanConstruct, GetGeneratedPower/GetUsedPower/AllowSetPower) and 253820-253850 (CableNetwork.AddDevice). Cross-checked against the Power Grid Plus PGP-3 research dive (Mods/PowerGridPlus/RESEARCH.md). Single-write-site finding for `PowerCable` confirmed by exhaustive grep against `set_PowerCable` and `PowerCable = ` in the decomp (only matches: lines 350786, 350790 in `FindPowerCable`).

## Open questions

- Whether `InitializeDataConnection` runs during `Constructor.SpawnConstruct` on a server build vs only later via the `OnRegistered` chain. The decomp suggests `Thing.Create` -> `OnRegistered(cell)` chain is the only path, but the exact `Thing.Create` flow is not fully traced here.
