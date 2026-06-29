---
title: Device
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-18
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.Device
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 345177-345193 (ArcFurnace GetUsedPower/ReceivePower, _powerUsedDuringTick impulse + reset), 350705 (Device.GetUsedPower base), 344687 (Setting/Setting2 device GetUsedPower) -- per-device draw-state fields (_powerProvided one-tick lag, _powerUsedDuringTick impulse)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 349588-351055 (Device class header, fields, properties, FindPowerCable, InitializeDataConnection, OnRegistered, OnNeighborPlaced, OnNeighborRemoved, CanConstruct), 253820-253850 (CableNetwork.AddDevice -> ConnectedCableNetworks.Add)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 297636-299221 (Thing.OnOff/Powered/PoweredValue/Error and backing fields), 302678-302695 (Thing.CacheStates sets every Has*State flag from the Interactables list), 349675 (Device.IsOperable), 373803 (ElectricalInputOutput.IsOperable), 327392 (IPowered), 386861-386894 (PowerReceiver.LinkedPowerTransmitter)
  - Plans/PowerGridPlus/PLAN.md, Mods/PowerGridPlus/RESEARCH.md
related:
  - ./Cable.md
  - ./CableNetwork.md
  - ./WirelessPower.md
  - ./ElectricalInputOutput.md
  - ./ElectricityManager.md
  - ./PowerTick.md
  - ../GameSystems/StructurePlacementValidation.md
  - ../Patterns/CursorAdjacencyLookup.md
tags: [power, prefab, network]
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

### Two per-device draw-state fields make GetUsedPower reflect prior-tick state
<!-- verified: 0.2.6228.27061 @ 2026-06-18 -->

Two private float fields cause a device's `GetUsedPower` to encode work or throughput from the PREVIOUS tick rather than the live tick. Both matter to any mod that ticks a network more than once per game tick (it will read the same field in every pass, see "single-pass vs multi-pass" below).

**`_powerProvided` (one-tick lag).** Exactly FOUR classes carry a `private float _powerProvided`: `Transformer` (line 403323), `AreaPowerControl` (369546), `PowerTransmitter` (387083), and `PowerReceiver` (386867). It is the accumulator of the power drawn THROUGH the device by its downstream consumers: added to in the device's `UsePower` and decremented in `ReceivePower` as input power flows in (see [PowerTick](./PowerTick.md): `ApplyState` -> `ConsumePower` -> `Device.ReceivePower`). The device's input-side draw is reported FROM this accumulator:
- `Transformer.GetUsedPower(InputNetwork)` = `min(Setting + UsedPower, _powerProvided)` (line 403493).
- `AreaPowerControl.GetUsedPower(InputNetwork)` = `UsedPower + _powerProvided` (+ a cell-charge term) (369991).
- `PowerTransmitter.GetUsedPower(InputNetwork)` = `min(MaxPowerTransmission, _powerProvided)` (387265); its `_powerProvided` accrues from the WirelessNetwork output (`UsePower` gated on `cableNetwork == WirelessOutputNetwork`, 387220) and decrements on input-cable `ReceivePower` (387228).
- `PowerReceiver.GetUsedPower(InputNetwork)` = `min(PowerTransmitter.MaxPowerTransmission + UsedPower, _powerProvided)` (387025), but a pure receiver's `InputNetwork` is null (it has `WirelessInputNetwork` + cable `OutputNetwork`), so the `cableNetwork != InputNetwork` guard returns 0 -- the receiver does not bill an input cable. `PowerReceiver.GetGeneratedPower(OutputNetwork)` = `WirelessInputNetwork.PotentialLoad` (387038), and its `_powerProvided` accrues from its OutputNetwork draws (386984).

Because `_powerProvided` is filled during `ApplyState` (which runs AFTER `CalculateState` has already summed `GetUsedPower`), the value a `CalculateState` reads is last tick's downstream consumption: **the input-side draw a pass-through device bills lags its output-side delivery by exactly one tick.** This is the mechanism behind the "transformer free-power / one-tick-lag" exploit family that Power Grid Plus replaces with a fresh allocator-computed throughput. `Battery` and `RocketPowerUmbilical` do NOT carry `_powerProvided` -- they are terminal storage (a `PowerStored` cell), so their charge/discharge draw is a function of live cell state, not a lagged pass-through accumulator.

**`_powerUsedDuringTick` (per-tick impulse, on processing machines: ArcFurnace, Centrifuge, Recycler, Fabricators, filtration machines, robotics chargers, etc.).** This is the IMPULSE energy a machine consumes doing work in a tick (a recipe's `Energy`, `EnergyPerSmelt`, atmosphere-proportional energy, etc.). The machine's `GetUsedPower` adds it on top of the base `UsedPower`: e.g. `ArcFurnace.GetUsedPower` (line 345177) returns `UsedPower + _powerUsedDuringTick`. It is set during the machine's processing (e.g. `_powerUsedDuringTick += _currentRecipe.Energy`, `+= EnergyPerSmelt`) and RESET to 0 in `ReceivePower` (line 345193 for ArcFurnace). Because the reset happens once, inside `ApplyState`, the field holds the SAME value across every `CalculateState` read within a single game tick. So `GetUsedPower` is **idempotent within a tick** (an observe pass and an enforce pass in the same tick read the same impulse), even though the value changes tick-to-tick as the machine starts and finishes work.

Single-pass vs multi-pass implication: vanilla runs `Initialise -> CalculateState -> ApplyState` once, so it reads each field once and resets it once. A mod whose tick reads `GetUsedPower` in more than one pass (e.g. an OBSERVE pass and an ENFORCE pass) gets the SAME `_powerUsedDuringTick` in both (reset only happens in the single `ApplyState`), but if it tries to compute supply from one device's draw and feed it to another device, the `_powerProvided` lag means the pass-through input draw it observes is one tick stale relative to the live downstream demand. Matching freshly-computed supply against `_powerProvided`-reported demand mixes a live figure with a one-tick-old figure.

## Operational-state surface: OnOff, Powered, PoweredValue, Error, IsOperable
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

The "is this device switched on, and does it actually have power right now" question is answered by a set of animator-state-backed properties declared on `Thing` (the universal base), refined by `IsOperable` overrides further down the hierarchy. None of these live on `Device` itself; they are inherited.

### Thing-level state properties (declared on `Thing`, line 297636)
<!-- verified: 0.2.6228.27061 @ 2026-06-14 -->

All four are `public virtual`, backed by a Unity `Animator` integer parameter, and cached per-frame. Source `Assets/Scripts/Objects/Thing.cs`:

```csharp
public virtual bool OnOff                                              // line 299160
{
    get
    {
        if (HasOnOffState && !HasBaseAnimator)
            return InteractOnOff.State == 1;
        if (ThreadedManager.IsThread || _frameOnOffUpdated == Time.frameCount)
            return _onOff;
        _onOff = (bool)BaseAnimator && HasOnOffState && BaseAnimator.GetInteger(Interactable.OnOffState) == 1;
        _frameOnOffUpdated = Time.frameCount;
        return _onOff;
    }
    set { /* writes Interactable.OnOffState on the animator + _onOff */ }
}

public virtual bool Powered => PoweredValue >= 1;                      // line 299193

public virtual int PoweredValue                                       // line 299195
{
    get
    {
        if (HasPowerState && !HasBaseAnimator)
            return InteractPowered.State;
        if (ThreadedManager.IsThread || _framePoweredUpdated == Time.frameCount)
            return _powered;
        _powered = ((bool)BaseAnimator && HasPowerState) ? BaseAnimator.GetInteger(Interactable.PoweredState) : 0;
        _framePoweredUpdated = Time.frameCount;
        return _powered;
    }
    set { /* writes Interactable.PoweredState on the animator + _powered */ }
}

public virtual int Error                                              // line 298838
{
    get
    {
        if (HasErrorState && !HasBaseAnimator)
            return InteractError.State;
        if (ThreadedManager.IsThread || _frameErrorUpdated == Time.frameCount)
            return _error;
        _frameErrorUpdated = Time.frameCount;
        _error = (((bool)BaseAnimator && HasErrorState) ? BaseAnimator.GetInteger(Interactable.ErrorState) : 0);
        return _error;
    }
    set { /* writes Interactable.ErrorState on the animator + _error */ }
}
```

Backing fields (also on `Thing`): `private bool _onOff;` (line 298075), `protected int _powered;` (line 298079), `private int _error;`, plus the per-frame guards `_frameOnOffUpdated` / `_framePoweredUpdated` / `_frameErrorUpdated`. The `Has*State` flags (`HasOnOffState`, `HasPowerState`, `HasErrorState`, declared line 298143-298154) are `[ReadOnly]` bools, but the `[ReadOnly]` attribute is only a Unity Inspector decorator and says nothing about the data source. They are assigned by `Thing.CacheStates()` (line 302678) from the **`Interactables` list**, one `Exists` test per flag against the matching `InteractableType`, NOT from the animator's parameter list:

```csharp
public void CacheStates(bool cacheInteractables = false)          // line 302678
{
    HasLockState  = Interactables.Exists(i => i.Action == InteractableType.Lock);
    HasErrorState = Interactables.Exists(i => i.Action == InteractableType.Error);
    HasPowerState = Interactables.Exists(i => i.Action == InteractableType.Powered);
    HasOnOffState = Interactables.Exists(i => i.Action == InteractableType.OnOff);
    // ... HasModeState / HasOpenState / HasActivateState / HasExport(2)State /
    //     HasImport(2)State / HasButton1-3State / HasColorState / HasAccessState, all same shape ...
}
```

This is the sole write site for each flag (exhaustive grep: one assignment each, all inside `CacheStates`; no animator-sourced setter exists anywhere in Assembly-CSharp). The animator is the DOWNSTREAM consumer, not the source: once a flag is set, the getters above read the animator only while the flag is true (e.g. `OnOff` evaluates `BaseAnimator.GetInteger(Interactable.OnOffState) == 1` gated on `HasOnOffState`, and falls through to `false` when the flag is false). Practical consequence: a device whose prefab carries no Interactable with `Action == InteractableType.OnOff` (no on/off toggle, e.g. solar panels, both wind turbines, the RTG, the bare power-connector dock) reports `OnOff == false` permanently; likewise a device with no `Powered` interactable reports `Powered == false` always. This matters for any logic that treats `OnOff == false` as "the player switched it off": for a buttonless device that is not an OFF gesture, it is the absence of an OFF concept, so such logic must additionally gate on `HasOnOffState` to tell the two apart.

Caching / threading semantics that matter for a mod reader:

- On the **main thread**, the getter reads the live `Animator.GetInteger(...)` once per frame and caches into the `_xxx` field (keyed by `Time.frameCount`). Subsequent same-frame reads return the cache.
- On a **background thread** (`ThreadedManager.IsThread`, e.g. inside `PowerTick.ApplyState` on the UniTask ThreadPool worker), the getter short-circuits to the cached `_onOff` / `_powered` / `_error` field WITHOUT touching the `Animator` (Unity API calls from a worker thread crash the player). So from a power-tick-adjacent patch these properties are safe to read and return last-frame's value. This is exactly why PowerTransmitterPlus reads `t.OnOff` / `t.Error` from its power-tick postfixes without a `MainThreadDispatcher` round-trip (`Mods/PowerTransmitterPlus/PowerTransmitterPlus/LogicReadoutPatches.cs:44`: `if (t == null || !t.OnOff || t.Error == 1) return 0f;`).

### Network sync: animator state, server-driven
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

`OnOff`, `PoweredValue`, and `Error` are **server-authoritative and synced to clients as part of the `Thing` animator-state replication**, not as bespoke `NetworkUpdateFlags` bits. The write path is always through `OnServer.Interact(Interactable, int)` on the host, which sets the `Interactable.State` and drives the corresponding animator integer; the animator-state delta then ships to clients. The power state specifically is set every power tick from `IPowered.OnPowerTick()` implementations via `OnServer.Interact(base.InteractPowered, 0|1)` (dozens of call sites; e.g. lines 277774/277781, 279291/279306, 308728/308732, 326535/326540). `OnOff` is toggled by player interaction or logic-write, again funnelled through `OnServer.Interact(base.InteractOnOff, ...)`.

Implication: a client reading `device.Powered` / `device.OnOff` / `device.Error` sees the host's value (synced), so these are valid to read on either side. A mod must NOT write them on a client; do gameplay writes on the server (`NetworkManager.IsServer`) and let the existing animator sync propagate.

### IsOperable: the closest thing to a single "on and powered" predicate (but it is NOT that)
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

`IsOperable` is a `protected` property (not the public combined predicate one might hope for). Two declarations matter on the dish hierarchy:

```csharp
// Device, line 349675
protected virtual bool IsOperable
{
    get
    {
        if (base.IsStructureCompleted) return !IsBroken;
        return false;
    }
}

// ElectricalInputOutput : Device, line 373803
protected override bool IsOperable
{
    get
    {
        if (OutputNetwork != null && InputNetwork == OutputNetwork)
            return false;        // input and output shorted to the same network
        return base.IsOperable;  // -> IsStructureCompleted && !IsBroken
    }
}
```

`IsOperable` does NOT consult `OnOff` or `Powered`. It means "structurally complete, not broken, and (for two-port devices) not self-shorted." `protected` visibility means a mod cannot read it directly without reflection. It feeds `Error`, not the other way around (see `WirelessPower.CheckError` below).

Beware a naming collision: a parallel `DraggableThing`-family hierarchy (`PortableAtmospherics`, `DynamicGenerator`, etc.) declares `IsOperable` as a **method** `public virtual bool IsOperable()` (lines 277715, 279310, 289260) returning `true` by default. That method is unrelated to the `Device` property and is not on the dish path.

### How to read "on and powered, right now" for a Device subclass
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

There is no single vanilla property meaning "switched on AND receiving power." The canonical runtime check is the explicit conjunction the game itself uses across power code:

```
isLiveAndWorking = device.OnOff && device.Powered && device.Error == 0;
```

- `OnOff` (public bool): the switch state. Player/logic controlled, server-set, synced.
- `Powered` (public bool, == `PoweredValue >= 1`): set by the power tick when the device's network actually delivered its demand this tick. Server-set, synced. This is the "not browned out" signal.
- `Error == 0` (public int, 0 = ok / 1 = error): excludes the misconfigured / self-shorted / broken case. Server-set from `IsOperable`, synced.

All three are public on `Thing`, so a mod reads them directly off any `Device` instance (including `PowerTransmitter` / `PowerReceiver`) on either client or server, including from a background power-tick thread (cached value). For the dish pair specifically, `PowerReceiver.LinkedPowerTransmitter`'s setter also drives `Mode` (1 = linked, 0 = unlinked) via `OnServer.Interact(base.InteractMode, ...)` (line 386885), so `receiver.Mode == 1` is the synced "is linked" signal distinct from "is powered."

## Verification history

- 2026-06-18: added "Two per-device draw-state fields make GetUsedPower reflect prior-tick state" subsection. Additive; no existing content changed. Driving question: a Power Grid Plus rewrite that reports fresh allocator-computed transformer supply matched against pass-through input draws produced a one-tick mismatch (a battery main feeding two Area Power Controllers flickered: `ENFORCE Required(t) == allocator-demand(t-1)`). Root-caused to `_powerProvided` (the downstream-consumption accumulator carried by exactly four classes -- Transformer, AreaPowerControl, PowerTransmitter, PowerReceiver -- filled in `ApplyState` after `CalculateState` already summed `GetUsedPower`, so the input-side draw lags the output-side delivery by one tick; Battery and RocketPowerUmbilical are terminal storage and do NOT carry it) vs `_powerUsedDuringTick` (the processing-machine impulse, reset once in `ReceivePower`, hence idempotent across multiple `CalculateState` reads within a tick). Decompile evidence (0.2.6228.27061): `ArcFurnace.GetUsedPower` = `UsedPower + _powerUsedDuringTick` (line 345177), reset at 345193; `_powerUsedDuringTick +=` setters at 164536/168673/169933/278095/278103/345226/359308/359382/361442/363841 etc. (recipe / smelt / atmosphere / charge energies); `_powerProvided` mutation via `ApplyState` -> `ConsumePower` -> `Device.ReceivePower` documented on [PowerTick](./PowerTick.md). Live confirmation via a ScenarioRunner consumer dump on the dedicated server: each Area Power Controller's `GetUsedPower` equalled `UsedPower + _powerProvided` (the stale accumulator) while transformers on the same net reported a fresh figure.
- 2026-06-14: conflict on "what populates the `Has*State` flags (`HasOnOffState` / `HasPowerState` / `HasErrorState`)". Previous claim (added 2026-05-22): set at prefab-init "from the animator's parameter list". New finding: assigned by `Thing.CacheStates()` (line 302678) from the `Interactables` list, one `Interactables.Exists(i => i.Action == InteractableType.X)` test per flag. Fresh validator verdict: new finding correct; the `[ReadOnly]` field attribute had been misread as evidence of an animator source, but it is only a Unity Inspector decorator, and `CacheStates` is the sole write site (no animator-sourced setter exists). The animator is the downstream consumer (the getters read it gated on the flag), never the source. Result: corrected the "Thing-level state properties" subsection with the verbatim `CacheStates` excerpt and the buttonless-device consequence (`OnOff == false` permanently when no `Action == OnOff` interactable exists); restamped that subsection to 2026-06-14 and bumped top-level `verified_at`. Driving question: whether PowerGridPlus's OFF-as-reset sweep (which clears fault lockouts on any device reporting `OnOff == false`) misfires on buttonless producers.
- 2026-05-22: added "Operational-state surface: OnOff, Powered, PoweredValue, Error, IsOperable" section. Additive; no existing content changed. Driving question: how does a mod (PowerTransmitterPlus beam fix) know a dish device is switched on and actually powered right now. Findings sourced from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs`: `Thing.OnOff` (line 299160), `Thing.Powered`/`PoweredValue` (lines 299193/299195), `Thing.Error` (line 298838), backing fields and `Has*State` flags (lines 298075-298154), `Device.IsOperable` property (line 349675), `ElectricalInputOutput.IsOperable` override (line 373803), the `OnServer.Interact(base.InteractPowered, ...)` power-tick write pattern (lines 277774-334419 passim), `IPowered` interface (line 327392, declares only `void OnPowerTick()`), and `PowerReceiver.LinkedPowerTransmitter` setter driving `Mode` (line 386885). The `IsOperable()`-method-vs-`IsOperable`-property naming collision (DraggableThing family at lines 277715/279310/289260 vs Device property) noted to prevent a future mis-patch. Cross-checked against `Mods/PowerTransmitterPlus/PowerTransmitterPlus/LogicReadoutPatches.cs:44` which already uses `!t.OnOff || t.Error == 1`.
- 2026-05-13: page created. Sourced from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` lines 349588-351055 (Device class header, fields, properties, FindPowerCable, InitializeDataConnection, OnRegistered, OnNeighborPlaced, OnNeighborRemoved, CanConstruct, GetGeneratedPower/GetUsedPower/AllowSetPower) and 253820-253850 (CableNetwork.AddDevice). Cross-checked against the Power Grid Plus phase 3 research dive (Mods/PowerGridPlus/RESEARCH.md). Single-write-site finding for `PowerCable` confirmed by exhaustive grep against `set_PowerCable` and `PowerCable = ` in the decomp (only matches: lines 350786, 350790 in `FindPowerCable`).

## Open questions

- Whether `InitializeDataConnection` runs during `Constructor.SpawnConstruct` on a server build vs only later via the `OnRegistered` chain. The decomp suggests `Thing.Create` -> `OnRegistered(cell)` chain is the only path, but the exact `Thing.Create` flow is not fully traced here.
