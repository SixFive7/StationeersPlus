---
title: Device
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-13
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.Device
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 345177-345193 (ArcFurnace GetUsedPower/ReceivePower, _powerUsedDuringTick impulse + reset), 350705 (Device.GetUsedPower base), 344687 (Setting/Setting2 device GetUsedPower) -- per-device draw-state fields (_powerProvided one-tick lag, _powerUsedDuringTick impulse)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 349588-351055 (Device class header, fields, properties, FindPowerCable, InitializeDataConnection, OnRegistered, OnNeighborPlaced, OnNeighborRemoved, CanConstruct), 253820-253850 (CableNetwork.AddDevice -> ConnectedCableNetworks.Add)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 297636-299221 (Thing.OnOff/Powered/PoweredValue/Error and backing fields), 302678-302695 (Thing.CacheStates sets every Has*State flag from the Interactables list), 349675 (Device.IsOperable), 373803 (ElectricalInputOutput.IsOperable), 327392 (IPowered), 386861-386894 (PowerReceiver.LinkedPowerTransmitter)
  - Plans/PowerGridPlus/PLAN.md, Mods/PowerGridPlus/RESEARCH.md
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 317927-317957 (Thing.Error property), 319497-319503 (Thing.SetIntegerSafe), Error write-path census (257 InteractError interact sites; CheckError bodies 424944-424957 / 391986-391999 / 427072; direct writes 389414/389424/374288/432580/42534/42544)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 370335 (Device class), 370460-370462 (DataCables/PowerCables arrays), 371501-371534 (power virtuals), 371568-371615 (FindDataCable/FindPowerCable/InitializeDataConnection), 371617-371638 (CanConstruct), 371640-371698 (SetPower/AssessPower/OnInteractableUpdated)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 304441-304460 (Interactable.State setter funnel), 319509-319522 (Thing.OnInteractableStateChanged/OnInteractableUpdated), 371700-371707 (Device.RefreshAnimState), 33633-33647 (InfoScreenComponent), 146456-146661 (SwitchMode/SwitchColorState/SwitchOnOff), 375983 (DeviceInputOutputImportExportCircuit tooltip gate), 371164-371181 (GetLogicValue incomplete-structure early-out + On/Power arms), 371028/371041 (CanLogicRead Power = HasPowerState)
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
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

```csharp
public class Device : SmallGrid, ILogicable, IReferencable, IEvaluable, IConnected,
                      ISlotWriteable, IWreckage, IPowered, IDensePoolable      // line 370335
{
    [Header("Device")]
    [Tooltip("How much power (in Watts) does the device used while turned on.")]
    public float UsedPower = 10f;

    [ReadOnly] public List<ChuteNetwork> ConnectedChuteNetworks = new List<ChuteNetwork>();
    [ReadOnly] public List<PipeNetwork>  ConnectedPipeNetworks  = new List<PipeNetwork>();
    [ReadOnly] public List<CableNetwork> ConnectedCableNetworks = new List<CableNetwork>();
    [ReadOnly] public List<Cable>        AttachedCables         = new List<Cable>();

    public Cable DataCable  { get; set; }
    public Cable PowerCable { get; private set; }

    public Cable[] DataCables  { get; private set; } = Array.Empty<Cable>();   // line 370460
    public Cable[] PowerCables { get; private set; } = Array.Empty<Cable>();   // line 370462

    public CableNetwork DataCableNetwork  => DataCable  == null ? null : DataCable.CableNetwork;
    public CableNetwork PowerCableNetwork => PowerCable == null ? null : PowerCable.CableNetwork;

    public Connection DataConnection => OpenEnds.Find(c =>
        c.ConnectionType == NetworkType.Data || c.ConnectionType == NetworkType.PowerAndData);

    public virtual bool IsPowerProvider     => false;
    public virtual bool IsPowerInputOutput  => false;
}
```

Version change at 0.2.6403.27689: `DataCables` / `PowerCables` are now `Cable[]` auto-properties defaulting to `Array.Empty<Cable>()` (previously `List<Cable>`), rebuilt wholesale by `FindDataCable` / `FindPowerCable`; the singular `DataCable` / `PowerCable` are set to the first array element.

Subclasses of note: `Transformer : ElectricalInputOutput : Device` (two ports, `Input` / `Output` `ConnectionRole`), `PowerTransmitter : Device`, `Battery : Device`, `SolarPanel : Device : IPowerGenerator`, every furnace / fabricator / pipe valve / chute conveyor. `DeviceCableMounted : Device` is the in-cable family (`CableFuse`, `CableAnalyser`) -- it sits **inside** a cable's grid cell rather than adjacent to one, but otherwise behaves like a `Device`.

## PowerCable resolution: every assignment goes through FindPowerCable
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

`Device.PowerCable` is a property with a public getter and a private setter, so it can only be assigned from inside `Device` itself. There is exactly one place that assigns it: `Device.FindPowerCable()` (line 371588). At 0.2.6403.27689 the body is Span-based (the old `ConnectedCables(NetworkType)` API is REMOVED from the game; see [CursorAdjacencyLookup](../Patterns/CursorAdjacencyLookup.md)):

```csharp
private void FindPowerCable()
{
    Span<SmallCellRef> span = stackalloc SmallCellRef[32];
    int count = 0;
    FillConnected<Cable>(NetworkType.Power, span, ref count);
    PowerCables = new Cable[count];
    if (count == 0)
    {
        PowerCable = null;
        AssessPower(null, OnOff);
        return;
    }
    Span<SmallCellRef> span2 = span;
    Span<SmallCellRef> span3 = span2.Slice(0, count);
    for (int i = 0; i < span3.Length; i++)
    {
        PowerCables[i] = span3[i].Get<Cable>();
    }
    PowerCable = PowerCables[0];
}
```

So `PowerCable` is *always* the first hit of `FillConnected<Cable>(NetworkType.Power, ...)` (`SmallGrid.FillConnected<T>(NetworkType, Span<SmallCellRef>, ref int)`, line 312896), which walks the device's `OpenEnds` in declaration order, projects each end's transform position through `GridController.WorldToLocalGrid`, fetches the `SmallCell` at that grid coordinate, and records any adjacent occupant whose own `IsConnected(openEnd)` agrees, as a lazily-resolved `SmallCellRef`. Same pipeline as before, allocation-free. See [CursorAdjacencyLookup](../Patterns/CursorAdjacencyLookup.md) for the migrated API surface and the cursor-time story.

There is **no direct `PowerCable = ...` write from a cable-side trigger, a `CableNetwork.Merge` hook, or anywhere else in `Assembly-CSharp`.** `Cable.OnRegistered(Cell)` (line 392523) only runs `CableNetwork.Merge(CableNetwork.ConnectedNetworks(this)).Add(this)`; it does not push itself into adjacent devices. The wiring is always pulled from the device side.

The decomp grep confirms it at 0.2.6403.27689: every `PowerCable = ` assignment resolves to lines 371596 (`PowerCable = null;`) and 371606 (`PowerCable = PowerCables[0];`), both inside `FindPowerCable`. No other write site exists.

## InitializeDataConnection: the wrapper that finds both cables and refreshes network membership
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

```csharp
public void InitializeDataConnection()                                          // line 371609
{
    FindDataCable();
    FindPowerCable();
    DataCableNetwork ?.DirtyPowerAndDataDeviceLists();
    PowerCableNetwork?.DirtyPowerAndDataDeviceLists();
}
```

`FindDataCable` (line 371568; note it is PUBLIC while `FindPowerCable` is private) is the data-cable twin of `FindPowerCable`, again filling from `FillConnected<Cable>(NetworkType.Data, ...)` into the `DataCables` array and taking the first result as `DataCable` (null when the array is empty).

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
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

`Device.CanConstruct()` (line 371617) runs on the **cursor ghost** every frame. At 0.2.6403.27689 it is `FillConnected`-based:

```csharp
public override CanConstructInfo CanConstruct()
{
    Span<SmallCellRef> buf = stackalloc SmallCellRef[32];
    int count = 0;
    FillConnected<Device>(buf, ref count);
    for (int num = count - 1; num >= 0; num--)
    {
        if (buf[num].TryGet<INetworkedPipe>(out var found) && !found.ProhibitConnection(this))
        {
            count--;
        }
    }
    if (count > 0 && buf[0].TryGet<Device>(out var found2))
    {
        return CanConstructInfo.InvalidPlacement(GameStrings.PlacementBlockedByAdjacentDevice.AsString(found2.DisplayName));
    }
    if (count > 0)
    {
        return CanConstructInfo.InvalidPlacement(GameStrings.PlacementBlockedByUnknownDevice.DisplayString);
    }
    return base.CanConstruct();
}
```

This is verbatim evidence that the `FillConnected` adjacency pipeline (`openEnd.Transform.position` -> `WorldToLocalGrid` -> `GetSmallCell` -> occupant `IsConnected(openEnd)`) **already works on the cursor ghost at preview time**; it reads the live transform, not the cached `Connection.LocalGrid`. A mod postfix on `Device.CanConstruct` that needs the would-attach cable cannot call the removed `ConnectedCables(NetworkType.Power)` and (on net472) cannot bind the `Span`-typed `FillConnected` either; use the `Connection.GetLocalGrid()` + `SmallCell.Get<Cable>(grid, openEnd)` pattern documented on [CursorAdjacencyLookup](../Patterns/CursorAdjacencyLookup.md).

Caveats:

- `PowerCable` itself is **not** set on the cursor ghost (no `FindPowerCable` runs during preview).
- `ConnectedCableNetworks` is **not** populated on the cursor ghost either; that list is only appended by `CableNetwork.AddDevice`, which only runs on the registered device.
- `Connection.GetCable()` / `GetDevice()` and the other occupant getters return null on a cursor ghost's own OpenEnds (`GetSmallGridOccupant` bails on `!_isInitialized`, and `SetGrids` never initializes cursor-parented connections); see [CursorAdjacencyLookup](../Patterns/CursorAdjacencyLookup.md).
- The cursor ghost has `IsCursor == true`, `tag == "Cursor"`, `IgnoreSave == true`, no colliders, no children Renderers swapped for ghost materials, and is reused across frames (one per prefab in `_constructionCursors`). It is *not* a fresh instance per placement.

## Power virtuals: where PowerCable gates a device
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

The default power virtuals only deliver power when the calling network matches the device's own `PowerCable.CableNetwork`:

```csharp
public virtual float GetGeneratedPower(CableNetwork cableNetwork)   // line 371501
{
    if (PowerCable == null || PowerCable.CableNetwork != cableNetwork)
        return -1f;
    return 0f;
}

public virtual float GetUsedPower(CableNetwork cableNetwork)        // line 371510
{
    if (PowerCable == null || PowerCable.CableNetwork != cableNetwork)
        return -1f;
    if (!OnOff || !base.IsStructureCompleted)
        return 0f;
    return UsedPower;
}

public virtual bool AllowSetPower(CableNetwork cableNetwork)        // line 371531
{
    return PowerCableNetwork == cableNetwork;
}
```

A device whose `PowerCable` is null (no adjacent power cable) silently returns `-1f` from `GetUsedPower` / `GetGeneratedPower`, which `PowerTick` reads as "not on this network". Many subclasses override these three methods; the `PowerCable == null || PowerCable.CableNetwork != cableNetwork` guard is replicated almost verbatim across vanilla power devices (0.2.6228.27061 line census: 165086, 169955, 344689, 345179, 350698, 350707, 359403, 371227, 374356, 375228, 387488, 397078, 398880, 401392; e.g. `PowerTransmitterOmni.GetUsedPower` at 0.2.6403 line 408692).

### Mid-tick admission: AssessPower books DuringTickLoad between power ticks
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

`Device.AssessPower(CableNetwork, bool)` (protected virtual, lines 371654-371685) is the between-tick admission check that decides whether a device switched on mid-tick lights up immediately or waits for the next power tick:

- It books the device's `GetUsedPower(cableNetwork)` into `CableNetwork.DuringTickLoad` against the network's `EstimatedRemainingLoad => PotentialLoad - CurrentLoad - DuringTickLoad` (CableNetwork line 270676).
- Within budget (`usedPower <= EstimatedRemainingLoad`): adds the full draw to `DuringTickLoad` and flips `Powered` on immediately via `SetPower` (371640-371646, `OnServer.Interact(InteractPowered, ...)` gated on `GameManager.RunSimulation`).
- Over budget: books `Min(usedPower, EstimatedRemainingLoad)` into `DuringTickLoad` (saturating the remaining headroom) and powers the device off.
- `cableNetwork == null || !isOn` powers off and books nothing.

`DuringTickLoad` is zeroed at the top of each `CableNetwork.OnPowerTick` (line 270834), so the bookings only bridge the gap until the next real tick recomputes everything. The vanilla trigger is `Device.OnInteractableUpdated` (371692-371695): an `OnOff` interactable change while `GameState.Running && RunSimulation && HasPowerState` calls `AssessPower(PowerCable?.CableNetwork, state == 1)`. `FindPowerCable` also calls `AssessPower(null, OnOff)` when the device loses its last power cable (371597), powering it off immediately.

### Two per-device draw-state fields make GetUsedPower reflect prior-tick state
<!-- verified: 0.2.6403.27689 @ 2026-07-06 -->

Two private float fields cause a device's `GetUsedPower` to encode work or throughput from the PREVIOUS tick rather than the live tick. Both matter to any mod that ticks a network more than once per game tick (it will read the same field in every pass, see "single-pass vs multi-pass" below).

**`_powerProvided` (one-tick lag).** Exactly FOUR classes carry a `private float _powerProvided` (0.2.6403.27689 whole-decompile census): `AreaPowerControl` (line 390592), `PowerReceiver` (408071), `PowerTransmitter` (408287), and `Transformer` (424621). It is the accumulator of the power drawn THROUGH the device by its downstream consumers: added to in the device's `UsePower` and decremented in `ReceivePower` as input power flows in (see [PowerTick](./PowerTick.md): `ApplyState` -> `ConsumePower` -> `Device.ReceivePower`). The device's input-side draw is reported FROM this accumulator:
- `Transformer.GetUsedPower(InputNetwork)` = `min(Setting + UsedPower, _powerProvided)` (line 424791).
- `AreaPowerControl.GetUsedPower(InputNetwork)` = `Max(_powerProvided, UsedPower)` (+ a cell-charge term) (391028-391044).
- `PowerTransmitter.GetUsedPower(InputNetwork)` = `min(MaxPowerTransmission, _powerProvided)` (408469); its `_powerProvided` accrues from the WirelessNetwork output (`UsePower` gated on `cableNetwork == WirelessOutputNetwork`, 408426) and decrements on input-cable `ReceivePower` (408442).
- `PowerReceiver.GetUsedPower(InputNetwork)` = `min(PowerTransmitter.MaxPowerTransmission + UsedPower, _powerProvided)` (408229); the receiver's `InputNetwork` is the WIRELESS network (assigned from `LinkedPowerTransmitter.OutputNetwork`), so this bills the wireless side, not a cable. `PowerReceiver.GetGeneratedPower(OutputNetwork)` = `WirelessInputNetwork.PotentialLoad` (408242), and its `_powerProvided` accrues from its OutputNetwork draws (`UsePower`, 408188).

`_powerProvided` is NEVER zeroed anywhere in the game: the whole-decompile census finds exactly the four declarations above and only `+=` / `-=` / read sites (APC 391004-391037, RX 408188 / 408202 / 408229, TX 408428 / 408442 / 408469, Transformer 424761 / 424769 / 424791); no plain `_powerProvided = ...` assignment exists. Residual or negative debt therefore persists across OnOff toggles, error states, and link changes for the life of the object; nothing but the object's destruction resets the ledger.

`_powerProvided` is also NOT serialized (0.2.6403.27689 census), so "the life of the object" is bounded by the session. No save record carries it: `TransformerSaveData` (424593-424597) holds only `OutputSetting`; `WirelessPowerSaveData` (426765-426778) holds only the four dish-rotation doubles (`Horizontal` / `Vertical` / `TargetHorizontal` / `TargetVertical`); `PowerTransmitterSaveData` (408264-408268) adds only `OutputNetworkReferenceId`; `PowerReceiverSaveData` (408062-408064) adds nothing; `AreaPowerControl` declares no save-data class and no serialization override anywhere in its class body (390555-391146). The same whole-decompile reference census doubles as the wire proof: no `SerializeOnJoin` / `DeserializeOnJoin` / `BuildUpdate` / `ProcessUpdate` member reads or writes the field. The only runtime write dispatch is inside `PowerTick.ApplyState` on the power worker: `PowerProvider.ApplyPower` (271690-271696) -> `UsePower`, and `PowerTick.ConsumePower` (271820-271840) -> `ReceivePower` (see [PowerTick](./PowerTick.md)); the rocket umbilical's direct `PartnerUmbilical.ReceivePower(null, ...)` crossing (158139 / 158624) dispatches only onto `RocketPowerUmbilical` halves, never onto the four ledger classes. Consequence: the ledger is per-session runtime accumulation. It restarts at the C# default 0 whenever the object is recreated, which includes save load and client join; a pre-save debt or credit does not survive into the loaded world, and a late-joining client starts every ledger at 0 regardless of the host's value. Scope: verified at 0.2.6403.27689; older game versions unverified.

Because `_powerProvided` is filled during `ApplyState` (which runs AFTER `CalculateState` has already summed `GetUsedPower`), the value a `CalculateState` reads is last tick's downstream consumption: **the input-side draw a pass-through device bills lags its output-side delivery by exactly one tick.** This is the mechanism behind the "transformer free-power / one-tick-lag" exploit family that Power Grid Plus replaces with a fresh allocator-computed throughput. `Battery` and `RocketPowerUmbilical` do NOT carry `_powerProvided` -- they are terminal storage (a `PowerStored` cell), so their charge/discharge draw is a function of live cell state, not a lagged pass-through accumulator.

**`_powerUsedDuringTick` (per-tick impulse, on processing machines: ArcFurnace, Centrifuge, Recycler, Fabricators, filtration machines, robotics chargers, etc.).** This is the IMPULSE energy a machine consumes doing work in a tick (a recipe's `Energy`, `EnergyPerSmelt`, atmosphere-proportional energy, etc.). The machine's `GetUsedPower` adds it on top of the base `UsedPower`: e.g. `ArcFurnace.GetUsedPower` returns `UsedPower + _powerUsedDuringTick` (0.2.6228 line 345177; the same `UsedPower + _powerUsedDuringTick` return / `= 0f`-in-`ReceivePower` reset pattern is confirmed still present across the 0.2.6403.27689 decompile, e.g. lines 365558 / 365564 with `+= _currentRecipe.Energy` at 365606). It is set during the machine's processing (e.g. `_powerUsedDuringTick += _currentRecipe.Energy`, `+= EnergyPerSmelt`) and RESET to 0 in `ReceivePower`. Because the reset happens once, inside `ApplyState`, the field holds the SAME value across every `CalculateState` read within a single game tick. So `GetUsedPower` is **idempotent within a tick** (an observe pass and an enforce pass in the same tick read the same impulse), even though the value changes tick-to-tick as the machine starts and finishes work.

Single-pass vs multi-pass implication: vanilla runs `Initialise -> CalculateState -> ApplyState` once, so it reads each field once and resets it once. A mod whose tick reads `GetUsedPower` in more than one pass (e.g. an OBSERVE pass and an ENFORCE pass) gets the SAME `_powerUsedDuringTick` in both (reset only happens in the single `ApplyState`), but if it tries to compute supply from one device's draw and feed it to another device, the `_powerProvided` lag means the pass-through input draw it observes is one tick stale relative to the live downstream demand. Matching freshly-computed supply against `_powerProvided`-reported demand mixes a live figure with a one-tick-old figure.

## Operational-state surface: OnOff, Powered, PoweredValue, Error, IsOperable
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

The "is this device switched on, and does it actually have power right now" question is answered by a set of animator-state-backed properties declared on `Thing` (the universal base), refined by `IsOperable` overrides further down the hierarchy. None of these live on `Device` itself; they are inherited.

### Error is animator display state; writers are event-driven (0.2.6403 write-path census)
<!-- verified: 0.2.6403.27689 @ 2026-07-07 -->

`Thing.Error` at 0.2.6403.27689 (property 317927-317957, verbatim):

```csharp
public virtual int Error
{
    get
    {
        if (HasErrorState && !HasBaseAnimator)
        {
            return InteractError.State;
        }
        if (ThreadedManager.IsThread || _frameErrorUpdated == Time.frameCount)
        {
            return _error;
        }
        _frameErrorUpdated = Time.frameCount;
        _error = (((bool)BaseAnimator && HasErrorState) ? BaseAnimator.GetInteger(Interactable.ErrorState) : 0);
        return _error;
    }
    set
    {
        if (HasErrorState)
        {
            if ((bool)BaseAnimator)
            {
                SetIntegerSafe(Interactable.ErrorState, value);
            }
            else
            {
                InteractError.State = value;
            }
            _error = value;
        }
    }
}
```

So `Error` is DISPLAY state: it is stored in the animator integer `Interactable.ErrorState` (or the `InteractError.State` slot when the prefab has no base animator), read through a per-frame cache (`_frameErrorUpdated` / `_error`; worker threads always get the cached value), and the setter is local-only (it touches the animator and the cache, sets no network flag, and sends nothing). The animator write guard every `Thing` state setter funnels through is `SetIntegerSafe` (319497-319503, verbatim):

```csharp
private void SetIntegerSafe(int stateId, int value)
{
    if ((object)BaseAnimator != null && BaseAnimator.HasParameter(stateId))
    {
        BaseAnimator.SetInteger(stateId, value);
    }
}
```

An animator without the named integer parameter silently swallows the write (no exception, no visible state). All the Thing state-property setters (Button/Error/Lock/Mode/Activate/Import/Export/OnOff/Powered/Open/Color/Access, call sites 317850-318339), the bulk animator-state refresh (319343-319399), and the generic interactable path (319513) go through it.

Write-path census over the whole 0.2.6403.27689 decompile (a text census of the two mechanisms that can change `Error`: `OnServer.Interact(*.InteractError, ...)` calls, which route through the `Interactable.State` funnel and replicate, and direct `Error = <expr>` property writes, which are local display only):

- 257 `OnServer.Interact(*.InteractError, ...)` call sites. The dominant shape is a per-class private `CheckError()` that compares `IsOperable` against the current `Error` and flips it 0/1 under `GameManager.RunSimulation`; `Transformer` (424944-424957) and `Battery` (391986-391999) carry byte-identical bodies of this shape, and `WirelessPower` has a protected variant (427072) that the dish pair invokes on link changes (408096, 408320).
- A handful of direct `Error = <expr>` property writes: `RocketMiner` mining-head presence checks (389414 / 389424), `DeviceInputOutput` reset to 0 (374288), `Appliance` reset to 0 (432580), and a scanning-head device's checks (42534 / 42544; declaring class not resolved this pass).
- Every writer found is event-driven: cable-network attach/detach, interactable updates, link changes, head-presence checks, state-machine transitions. For the power-bridge classes specifically, the only writer is their `CheckError`, and its callers are topology or toggle events (Transformer: `OnAddCableNetwork` / `OnRemoveCableNetwork` / next-frame recheck from `OnInteractableUpdated`, 424959-424984; Battery: `OnAddCableNetwork` / `OnRemoveCableNetwork`, 392013-392023, plus the next-frame-deferred `WaitCheckState` recheck, 392038-392050). Nothing calls `CheckError` from the power tick, so overload or shortfall during steady-state operation never raises `Error = 1` on these classes. A partial-power forensics pass (the PowerGridPlus occasion for this census) must not expect vanilla `Error` to flag brownouts; `Error == 1` on a bridge means a topology/operability fault was detected at an event boundary, nothing else.

### Thing-level state properties (declared on `Thing`, line 297636)
<!-- verified: 0.2.6403.27689 @ 2026-07-13 -->

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

Re-read verbatim at 0.2.6403.27689 (2026-07-13): the excerpt above is byte-identical apart from line numbers. New refs: `Thing` class 316720, `OnOff` 318249-318280, `Powered => PoweredValue >= 1` 318282, `PoweredValue` 318284-318315 (`Error` at 317927-317957 is quoted in the Error subsection above). The same getter shape (`HasXxxState && !HasBaseAnimator -> InteractXxx.State`; `ThreadedManager.IsThread || _frameXxxUpdated == Time.frameCount -> cached field`; else read the animator and refresh the cache) also covers `Exporting2` (318223-318247) and `IsOpen` (318317-318348). `ThreadedManager.IsThread` is `Thread.CurrentThread.ManagedThreadId != GameManager.MainThreadId` (217769; `GameManager.IsThread` at 203949 is the same predicate). The `CacheStates` excerpt below keeps its 0.2.6228 line ref (not re-read this pass).

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

### Visual-state plumbing: the animator integer IS the storage, and each visual surface keys off one property
<!-- verified: 0.2.6403.27689 @ 2026-07-13 -->

Three consequences of the getter shape above, verified end to end at 0.2.6403.27689.

**1. There is no separate render flag.** For a Thing WITH a `BaseAnimator`, the Unity animator integer parameters (`Interactable.PoweredState` / `Interactable.OnOffState`) are the storage for `PoweredValue` / `OnOff`: the getter reads `BaseAnimator.GetInteger(Interactable.PoweredState)` (318296) and the setter writes back through `SetIntegerSafe` (318306). For an animator-less Thing the `Interactable` slot is the storage (`return InteractPowered.State;`, 318290). Visual state, logical flag, and the IC10 values share that one storage: `Device.GetLogicValue` returns `LogicType.On => OnOff ? 1 : 0` (371178-371179) and `LogicType.Power => Powered ? 1 : 0` (371180-371181). Two gates sit in front of those arms: `GetLogicValue` returns `0.0` for ANY logic type while `!IsStructureCompleted` (the pre-switch early-out at the top of the method body, 371164 region), and `CanLogicRead(LogicType.Power)` returns `HasPowerState` (371028/371041), so a prefab without a Powered interactable never exposes the Power slot at all. The `Thing.OnOff` getter has the matching guard: it returns `false` when `!HasOnOffState` before consulting the animator or interactable storage (318249 region).

**2. Every interactable write, local or replicated, lands in the animator and refreshes the visuals.** The funnel is the `Interactable.State` setter (304441-304460): it assigns `_state` (304449), notifies the parent Thing (304450), and then triggers the visual refresh (304458):

```csharp
public virtual void OnInteractableStateChanged(Interactable interactable, int newState, int oldState)   // Thing, 319509-319515
{
    if ((bool)BaseAnimator)
    {
        SetIntegerSafe(interactable.PropertyId, newState);
    }
}

public virtual void OnInteractableUpdated(Interactable interactable)   // Thing, 319517-319522
{
    CacheAnimatorInteractableVariable(interactable.Action);
    RefreshAnimState(GameManager.GameState != GameState.Running);
    this.OnInteractable?.Invoke();
}
```

**3. Which property each visual surface reads.** Info screens are Powered-ONLY. `Device.RefreshAnimState` (371700-371707) forwards to the optional info-screen component (`protected InfoScreenComponent _infoScreen;`, 370366):

```csharp
protected override void RefreshAnimState(bool skipAnimation = false)   // Device, 371700-371707
{
    base.RefreshAnimState(skipAnimation);
    if (_infoScreen != null)
    {
        _infoScreen.RefreshState(this);
    }
}
```

```csharp
public class InfoScreenComponent : GameBase   // 33633-33647
{
    [SerializeField]
    private MaterialChanger _materialChanger;

    [SerializeField]
    private Collider infoTrigger;

    public Collider InfoTrigger => infoTrigger;

    public void RefreshState(Device parent)
    {
        _materialChanger.ChangeState(parent.Powered ? Defines.Animator.OnPowered : Defines.Animator.NotPowered);
    }
}
```

No `OnOff` term: an info screen lights whenever `Powered == 1`, whatever the switch says. The same Powered-only keying gates the IC-housing info-panel tooltip: `DeviceInputOutputImportExportCircuit.GetPassiveTooltip` shows the operation text only when `hitCollider == _infoScreen.InfoTrigger && Powered` (375983).

Switch levers are OnOff-first. `SwitchOnOff.RefreshColorState` (146597-146629, on the `DevicePart` visual-component family, driven from `RefreshState` at 146575-146582):

```csharp
protected virtual void RefreshColorState(bool skipAnim)   // SwitchOnOff, 146597-146629
{
    SwitchColorState switchColorState = SwitchColorState.Off;
    if (parentThing.OnOff)
    {
        switchColorState = ((parentThing.Powered || !parentThing.HasPowerState) ? SwitchColorState.OnPowered : SwitchColorState.On);
    }
    if ((parentThing.Error != 0 && parentThing.Powered) || (parentThing.Error != 0 && !parentThing.HasPowerState))
    {
        switchColorState = SwitchColorState.Error;
    }
    if (switchColorState != _currentColorState)
    {
        _currentColorState = switchColorState;
        CancelErrorAnimation();
        switch (_currentColorState)
        {
        case SwitchColorState.Off:
            switchRenderer.material = off;
            break;
        case SwitchColorState.On:
            switchRenderer.material = on;
            break;
        case SwitchColorState.OnPowered:
            switchRenderer.material = onPowered;
            break;
        case SwitchColorState.Error:
            _errorStateCancellationTokenSource = new CancellationTokenSource();
            ErrorAnimation(_errorStateCancellationTokenSource.Token).Forget();
            break;
        }
    }
}
```

Off wins unconditionally: `OnOff == false` selects the `off` material regardless of `Powered`. On and Powered selects `onPowered`; on and unpowered selects `on` (the `!parentThing.HasPowerState` term promotes things with no Powered interactable straight to `onPowered`, since their `Powered` reads false permanently per the `CacheStates` subsection above). The Error state blinks `error[0]` / `error[1]` every 250 ms (`ErrorAnimation`, 146641-146661). The base class has no `OffPowered` case; `SwitchMode` (146456-146502) adds the fourth material `offPowered` (field 146459) with this decision ladder (146480):

```csharp
SwitchColorState switchColorState = ((parentThing.GetInteractable(State).State != 1) ? ((!parentThing.Powered) ? SwitchColorState.Off : SwitchColorState.OffPowered) : (parentThing.Powered ? SwitchColorState.OnPowered : SwitchColorState.On));
```

The enum is the game's designed four-plus-state switch visual model:

```csharp
public enum SwitchColorState   // 146504-146512
{
    Undefined,
    Off,
    On,
    OnPowered,
    Error,
    OffPowered
}
```

**Why the two keyings never visibly disagree in vanilla.** Toggling OnOff off fires `Device.OnInteractableUpdated` -> `AssessPower(net, isOn)` (371687-371698), and `AssessPower` un-powers immediately (the same body the "Mid-tick admission" subsection above describes; verbatim, 371654-371685):

```csharp
protected virtual void AssessPower(CableNetwork cableNetwork, bool isOn)   // Device, 371654-371685
{
    if (cableNetwork == null || !isOn)
    {
        if (Powered)
        {
            SetPower(cableNetwork, hasPower: false);
        }
        return;
    }
    float usedPower = GetUsedPower(cableNetwork);
    if (usedPower <= 0f)
    {
        return;
    }
    if (usedPower > cableNetwork.EstimatedRemainingLoad)
    {
        cableNetwork.DuringTickLoad += Mathf.Min(usedPower, cableNetwork.EstimatedRemainingLoad);
        if (Powered)
        {
            SetPower(cableNetwork, hasPower: false);
        }
    }
    else
    {
        cableNetwork.DuringTickLoad += usedPower;
        if (!Powered)
        {
            SetPower(cableNetwork, hasPower: true);
        }
    }
}
```

Independently, `PowerTick.ApplyState` un-powers zero-demand and unfed devices every tick (OFF edge 271936-271938 inside the device loop 271916-271940; see [PowerTick](./PowerTick.md)). So in vanilla `OnOff = 0` forces `Powered = 0` within the frame, and the Powered-only surfaces (info screens, the 375983 tooltip gate) never light on a switched-off device.

**Mod context.** A mod that holds `Powered = 1` while `OnOff = 0` exposes every Powered-only-keyed surface: info screens stay lit, `offPowered`-material switches glow, and 375983-style Powered gates stay open. And because the animator integer IS the storage (point 1), there is no hidden render flag to patch: re-keying a surface means patching the visual component itself (`InfoScreenComponent.RefreshState`, `SwitchOnOff.RefreshColorState`) or changing the stored value, nothing in between.

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

## Vanilla Powered writer census (every write path, 0.2.6403.27689)
<!-- verified: 0.2.6403.27689 @ 2026-07-09 -->

Whole-decompile census of everything that writes a Device's Powered flag (produced for the PowerGridPlus Powered-ownership redesign; three independent agents concur, key sites re-read directly). The write funnel: `Device.SetPowerFromThread` (NON-virtual, `public async UniTaskVoid`, 371648-371652, marshals to the main thread) -> virtual `Device.SetPower` (371640-371646: `if (Powered != hasPower && GameManager.RunSimulation) OnServer.Interact(InteractPowered, hasPower ? 1 : 0)`) -> the replicated interactable. `OnServer.Interact` itself (39690-39703) returns unless `GameManager.RunSimulation` and queues to the main thread when called from a worker, so every write below is host-only and replicates to clients as normal interactable state. `SetPower` overrides: `Bench` (325448-325463, propagates `OnOff && Powered` into every docked Appliance) and `ElevatorLevel` (395278-395285, ORs `ShaftNetwork.IsAnyOtherPowered` into the value).

- **The tick writer.** `PowerTick.ApplyState`: ON edge 271929-271933 (consumed with power met, or a provider with ratio above 0; NOT AllowSetPower-gated), OFF edge 271936-271938 (the game's ONLY AllowSetPower-gated write). Both call `SetPowerFromThread`, its only vanilla callers.
- **Mid-tick admission.** `Device.AssessPower` (protected virtual, 371654-371685): both edges from the `EstimatedRemainingLoad` heuristic, on the MAIN thread. Triggers: every OnOff interactable change (`OnInteractableUpdated` 371692-371694), `FindPowerCable` finding zero power cables (`AssessPower(null, OnOff)`, 371597), `OnRemoveCableNetwork` (371738-371745). Overrides that REPLACE the admission: `LandingPadTankConnector` 176535, `LandingPadPump` 185583, `LandingPadTaxiThreshold` 185861 (all `SetPower(net, LandingPadCenter.OnOff)`, re-fired from their own `OnPowerTick`), `Bench` 325465, `UnPoweredDoor` 327581 (`SetPower(net, true)` unconditionally), `WallLightBattery` 327950 (delegates to `CheckPowerState`).
- **Event-driven un-power families.** `ElectricalInputOutput.CheckPower` (394945-394951: OFF when `InputNetwork == null && Powered`; fired from network add/remove 394986-395006); `AreaPowerControl.CheckPower` (390983-390989: OFF on `NoPower`, fired from OnOff interaction and battery-slot events); `Battery.CheckPower` (391963-391969: BOTH edges every host device tick, syncing `InteractPowered` to the computed charge-state getter, which is why writing a Battery's interactable never sticks).
- **Per-device-tick self-asserters.** `WallLightBattery.CheckPowerState` (328028-328038: `Powered = OnOff && HasPower(cell)`, re-fired from AssessPower / ReceivePower / OnPowerTick / OnInteractableUpdated / slot events 327950-328056); `PowerGeneratorPipe.OnAtmosphericTick` (396677-396714: both edges from combustion state) and `StirlingEngine.OnAtmosphericTick` (424014-424079), the two classes whose `AllowSetPower` returns false (see [PowerTick](./PowerTick.md)); the elevator family (`ElevatorShaftNetwork.ShaftPowerUpdated` 395914-395927 copies the aggregate onto cable-less shafts; `ElevatorCarrage.PhysicsUpdate` 203364-203373 re-syncs the carriage every physics frame); `SpawnPointAtmospherics` additionally self-toggles OnOff from `PowerTick.Potential` (422696-422702).
- **Out-of-scope self-owners** (never on a cable network's PowerDeviceList): the Bench appliance cascade (Appliance writes at 300-306, 432574-432694, plus the `BenchPowerStateChanged` overrides), self-gauging vehicles (`Rover` 226302-226331, `RobotMining` 308792-308817: `PoweredValue` is a 0-5 battery GAUGE, not a boolean), `Shuttle` (310264-310270, direct `InteractPowered.State =` on both peers), and the portable / wearable `CheckPowerState` family (Suit, Helmet, PowerTool, SensorLenses, the `Dynamic*` portables, Defibrillator, GasMask, LanderCapsule).
- **The forwarding producer.** `PowerConnector.ConnectedDynamicGenerator` (public field, 408007) is set/cleared on dock/undock (408023-408048); `PowerConnector.GetGeneratedPower` (408014) forwards the docked `DynamicGenerator.PowerGenerated` (gated on the generator's OnOff/Powered, 297398-297408) to ANY asking network. `DynamicComposter` (296224) is a `DraggableThing` sibling of `DynamicGenerator` (297342), so it can never dock as a generator: its threaded accumulator is unreachable by the power solve.

Consequence for a mod taking ownership of Powered: blocking the ApplyState OFF edge at the `SetPowerFromThread` funnel silences the only tick-driven un-power for every class at once (including third-party `AllowSetPower` overrides), but the event-driven writers above survive, and the per-device-tick self-asserters get the last word each tick (the device tick runs after any ENFORCE-tail write), so those classes must be exempted rather than driven.

## Verification history

- 2026-07-13 (third pass): extended the visual-state plumbing subsection's IC10 paragraph with the two front gates, re-read from the 0.2.6403.27689 decompile during the PowerGridPlus Power-read-override build: `GetLogicValue` returns `0.0` for any logic type while `!IsStructureCompleted` (pre-switch early-out, 371164 region), `CanLogicRead(LogicType.Power)` returns `HasPowerState` (371028/371041), and `Thing.OnOff` returns `false` when `!HasOnOffState` before consulting storage (318249 region). Additive; no prior content contradicted.
- 2026-07-13 (later): added the "Visual-state plumbing" subsection to the operational-state section (game version 0.2.6403.27689), all bodies re-read from the decompile this pass. The chain: animator integers are the storage for `PoweredValue` / `OnOff` (getter animator read 318296, animator-less `InteractPowered.State` 318290, setter 318306; the IC10 `LogicType.On` / `LogicType.Power` arms read the same properties, 371178-371181); the `Interactable.State` setter funnel (304441-304460) drives `Thing.OnInteractableStateChanged` (319509-319515, stamps the animator via `SetIntegerSafe`) and `Thing.OnInteractableUpdated` -> `RefreshAnimState` (319517-319522), both quoted verbatim; `Device.RefreshAnimState` (371700-371707) forwards to `InfoScreenComponent.RefreshState` (33633-33647, keyed on `parent.Powered` ONLY, no OnOff term; same keying as the `DeviceInputOutputImportExportCircuit.GetPassiveTooltip` gate at 375983); `SwitchOnOff.RefreshColorState` quoted verbatim in full (146597-146629, OnOff-first with the `!HasPowerState` promotion, Error blink 146641-146661, no OffPowered case in the base); `SwitchMode` adds `offPowered` (146456-146502, ladder 146480); `SwitchColorState` enum verbatim (146504-146512); `AssessPower` quoted verbatim (371654-371685, immediate `SetPower(false)` when `!isOn`), triggered from `Device.OnInteractableUpdated` (371687-371698), plus the per-tick `PowerTick.ApplyState` OFF edge (271936-271938), which together force `Powered = 0` within the frame of an OnOff-off in vanilla. Occasion: a mod holding `Powered = 1` while `OnOff = 0` lights every Powered-only surface; recorded the re-keying constraint (animator-is-storage means no separate render flag exists). Additive; no prior content contradicted.
- 2026-07-13: fresh-validation pass at 0.2.6403.27689 (decompile-claim audit). (a) Restamped "Thing-level state properties": `OnOff` / `Powered` / `PoweredValue` re-read verbatim at 318249-318315 (byte-identical to the 0.2.6228 excerpt; a worker thread returns the cached `_onOff` / `_powered` without touching the animator), `Exporting2` (318223-318247) and `IsOpen` (318317-318348) confirmed to share the shape, `ThreadedManager.IsThread` located at 217769; the `CacheStates` excerpt keeps its 0.2.6228 ref, marked inline. (b) Re-confirmed the Powered-writer census's tick-writer claim by whole-decompile grep: `SetPowerFromThread` is declared once (`Device`, 371648, `await UniTask.SwitchToMainThread(); SetPower(cableNetwork, hasPower);`) and called exactly twice, both in `PowerTick.ApplyState` (271933 ON edge, 271938 OFF edge); no other vanilla system marshals `Powered` writes through it. No content contradicted. The destroyed-parent drop semantics of the `OnServer.Interact` funnel these writes land in are now on [OnServer](./OnServer.md).
- 2026-07-09 (later): added the "Vanilla Powered writer census" section from the PowerGridPlus Powered-ownership redesign investigation (game version 0.2.6403.27689): the SetPowerFromThread -> SetPower -> OnServer.Interact funnel with its RunSimulation gate, the ApplyState ON/OFF edges as SetPowerFromThread's only vanilla callers, the AssessPower admission (triggers + the six overrides), the CheckPower / CheckPowerState event families, the per-device-tick self-asserters, the out-of-scope self-owner list, and the PowerConnector -> DynamicGenerator forwarding chain with the DynamicComposter sibling dead-end. Additive; the existing operational-state sections stand.
- 2026-07-09: re-confirmed the operational-state surface (`OnOff` / `Powered` / `PoweredValue` / `InteractOnOff` / `InteractPowered`) and the base `Device.GetUsedPower` OnOff gate verbatim against the 0.2.6403.27689 decompile; no content change (the section already carries `GetUsedPower` returning 0 when `!OnOff || !base.IsStructureCompleted` at 371510-371521, `Powered => PoweredValue >= 1` read off `InteractPowered.State` at 318282, and "Powered set by the power tick"). Incremental fact recorded this pass: the IC10 logic-type mapping. `LogicType.On` is the writable `OnOff` switch (`SetLogicValue(LogicType.On, ...)` gates on `OnOff`, 153191-153205); `LogicType.Power` is the read-only energized flag; `PowerTick.ApplyState -> SetPowerFromThread -> InteractPowered` is the sole writer of `Powered`. So `On` and `Power` are two distinct networked states (two interactables: `Thing.HasState` maps `OnOffState -> InteractOnOff` and `PoweredState -> InteractPowered`, 320106-320134), not one, and a switched-off device reads `Power == 0` only indirectly (OnOff false makes `GetUsedPower` return 0, so `ApplyState` un-powers it the next tick). Occasion: explaining the OnOff-vs-Powered distinction behind a PowerGridPlus fabricator reboot (a `Powered` transition; the switch never moved).
- 2026-07-07: added "Error is animator display state; writers are event-driven" subsection to the operational-state section (game version 0.2.6403.27689). Verbatim `Thing.Error` property (317927-317957: animator-integer display state with per-frame cache, worker threads read the cache, setter local-only via `SetIntegerSafe` / `InteractError.State`) and `Thing.SetIntegerSafe` (319497-319503: writes only when the animator has the named parameter). Whole-decompile write-path census: 257 `OnServer.Interact(*.InteractError, ...)` sites (dominant shape: per-class `CheckError()` flipping 0/1 on `IsOperable` under `RunSimulation`; Transformer 424944-424957 and Battery 391986-391999 byte-identical, WirelessPower protected variant 427072 called at 408096 / 408320) plus the direct `Error =` writes (RocketMiner 389414/389424, DeviceInputOutput 374288, Appliance 432580, unresolved scanner 42534/42544). All writers event-driven; no power-tick caller for the bridge classes. Occasion: PowerGridPlus partial-power forensics floated the claim "no vanilla Error writer for Transformer"; the census rejects the literal claim (CheckError exists and writes) while confirming the operative half (no steady-state/per-tick writer, so brownouts never raise Error). Additive here; the corresponding Transformer.md section was re-verified the same pass.
- 2026-07-06: added the non-serialization census to the `_powerProvided` half of "Two per-device draw-state fields" (game version 0.2.6403.27689). Before writing, checked every central page mentioning `_powerProvided` for a claim that the ledger persists into saves: none exists (this page's "for the life of the object" wording was the closest and already implies reset on recreation), so the addition is additive scoping and no fresh validator was required. Evidence, all re-read from the 0.2.6403.27689 decompile this pass: whole-decompile `_powerProvided` census re-run (four declarations, only the known `+=` / `-=` / read sites); `TransformerSaveData` 424593-424597 (only `OutputSetting`); `WirelessPowerSaveData` 426765-426778 (only H/V + slew targets); `PowerTransmitterSaveData` 408264-408268 (adds only `OutputNetworkReferenceId`); `PowerReceiverSaveData` 408062-408064 (empty); `AreaPowerControl` class body 390555-391146 contains no `SerializeSave` / `InitialiseSaveData` / `DeserializeSave` / `SerializeOnJoin` / `DeserializeOnJoin` / `BuildUpdate` / `ProcessUpdate` member (next type `AtmosphericSeat` at 391147); caller census for `.UsePower(` / `.ReceivePower(` confirms `PowerProvider.ApplyPower` (271690-271696) and `PowerTick.ConsumePower` (271820-271840) are the only dispatch sites that can reach the four ledger classes (the umbilical crossing at 158139 / 158624 targets `RocketPowerUmbilical` partners; the single-argument `appliance.ReceivePower(float)` at 287 / 325489 is a different overload on the appliance hierarchy). Restamped the subsection.
- 2026-07-02: re-verification and update pass against the 0.2.6403.27689 decompile after the game update from 0.2.6228.27061. (a) NEW fact in the draw-state subsection: `_powerProvided` is never zeroed anywhere in the game; whole-decompile census finds exactly four declarations (AreaPowerControl 390592, PowerReceiver 408071, PowerTransmitter 408287, Transformer 424621) and only `+=` / `-=` / read sites, no plain assignment, so residual or negative debt persists for the life of the object. Also corrected the APC input-draw formula in that subsection from `UsedPower + _powerProvided` to the actual `Max(_powerProvided, UsedPower)` + charge term (verified at 391028-391044; the old wording was imprecise even at 0.2.6228, see [AreaPowerControl](./AreaPowerControl.md)). (b) NEW "Mid-tick admission: AssessPower" subsection (`AssessPower` 371654-371685 books into `CableNetwork.DuringTickLoad` against `EstimatedRemainingLoad => PotentialLoad - CurrentLoad - DuringTickLoad` at 270676, flips `Powered` via `SetPower` 371640-371646, over-budget books `Min(usedPower, EstimatedRemainingLoad)` and powers off; `DuringTickLoad` zeroed each `OnPowerTick` at 270834; triggered from `OnInteractableUpdated` 371692-371695 and `FindPowerCable` 371597). (c) API-migration supersessions: `SmallGrid.ConnectedCables` / `ConnectedDevices` were removed from the game; `FindPowerCable` (371588) / `FindDataCable` (371568, now public) / `CanConstruct` (371617) are `FillConnected`-based and `DataCables` / `PowerCables` are `Cable[]` auto-properties (370460 / 370462, previously `List<Cable>`); replaced the three superseded verbatim excerpts with the 0.2.6403 bodies and re-ran the single-write-site census for `PowerCable` (only 371596 / 371606, both inside `FindPowerCable`). Updated class decl (370335), power virtuals (371501-371534), and per-class draw refs to the new decompile. Sections "When InitializeDataConnection runs" and the Thing-level operational-state sections were NOT re-read this pass and keep their 0.2.6228.27061 stamps.
- 2026-06-18: added "Two per-device draw-state fields make GetUsedPower reflect prior-tick state" subsection. Additive; no existing content changed. Driving question: a Power Grid Plus rewrite that reports fresh allocator-computed transformer supply matched against pass-through input draws produced a one-tick mismatch (a battery main feeding two Area Power Controllers flickered: `ENFORCE Required(t) == allocator-demand(t-1)`). Root-caused to `_powerProvided` (the downstream-consumption accumulator carried by exactly four classes -- Transformer, AreaPowerControl, PowerTransmitter, PowerReceiver -- filled in `ApplyState` after `CalculateState` already summed `GetUsedPower`, so the input-side draw lags the output-side delivery by one tick; Battery and RocketPowerUmbilical are terminal storage and do NOT carry it) vs `_powerUsedDuringTick` (the processing-machine impulse, reset once in `ReceivePower`, hence idempotent across multiple `CalculateState` reads within a tick). Decompile evidence (0.2.6228.27061): `ArcFurnace.GetUsedPower` = `UsedPower + _powerUsedDuringTick` (line 345177), reset at 345193; `_powerUsedDuringTick +=` setters at 164536/168673/169933/278095/278103/345226/359308/359382/361442/363841 etc. (recipe / smelt / atmosphere / charge energies); `_powerProvided` mutation via `ApplyState` -> `ConsumePower` -> `Device.ReceivePower` documented on [PowerTick](./PowerTick.md). Live confirmation via a ScenarioRunner consumer dump on the dedicated server: each Area Power Controller's `GetUsedPower` equalled `UsedPower + _powerProvided` (the stale accumulator) while transformers on the same net reported a fresh figure.
- 2026-06-14: conflict on "what populates the `Has*State` flags (`HasOnOffState` / `HasPowerState` / `HasErrorState`)". Previous claim (added 2026-05-22): set at prefab-init "from the animator's parameter list". New finding: assigned by `Thing.CacheStates()` (line 302678) from the `Interactables` list, one `Interactables.Exists(i => i.Action == InteractableType.X)` test per flag. Fresh validator verdict: new finding correct; the `[ReadOnly]` field attribute had been misread as evidence of an animator source, but it is only a Unity Inspector decorator, and `CacheStates` is the sole write site (no animator-sourced setter exists). The animator is the downstream consumer (the getters read it gated on the flag), never the source. Result: corrected the "Thing-level state properties" subsection with the verbatim `CacheStates` excerpt and the buttonless-device consequence (`OnOff == false` permanently when no `Action == OnOff` interactable exists); restamped that subsection to 2026-06-14 and bumped top-level `verified_at`. Driving question: whether PowerGridPlus's OFF-as-reset sweep (which clears fault lockouts on any device reporting `OnOff == false`) misfires on buttonless producers.
- 2026-05-22: added "Operational-state surface: OnOff, Powered, PoweredValue, Error, IsOperable" section. Additive; no existing content changed. Driving question: how does a mod (PowerTransmitterPlus beam fix) know a dish device is switched on and actually powered right now. Findings sourced from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs`: `Thing.OnOff` (line 299160), `Thing.Powered`/`PoweredValue` (lines 299193/299195), `Thing.Error` (line 298838), backing fields and `Has*State` flags (lines 298075-298154), `Device.IsOperable` property (line 349675), `ElectricalInputOutput.IsOperable` override (line 373803), the `OnServer.Interact(base.InteractPowered, ...)` power-tick write pattern (lines 277774-334419 passim), `IPowered` interface (line 327392, declares only `void OnPowerTick()`), and `PowerReceiver.LinkedPowerTransmitter` setter driving `Mode` (line 386885). The `IsOperable()`-method-vs-`IsOperable`-property naming collision (DraggableThing family at lines 277715/279310/289260 vs Device property) noted to prevent a future mis-patch. Cross-checked against `Mods/PowerTransmitterPlus/PowerTransmitterPlus/LogicReadoutPatches.cs:44` which already uses `!t.OnOff || t.Error == 1`.
- 2026-05-13: page created. Sourced from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` lines 349588-351055 (Device class header, fields, properties, FindPowerCable, InitializeDataConnection, OnRegistered, OnNeighborPlaced, OnNeighborRemoved, CanConstruct, GetGeneratedPower/GetUsedPower/AllowSetPower) and 253820-253850 (CableNetwork.AddDevice). Cross-checked against the Power Grid Plus phase 3 research dive (Mods/PowerGridPlus/RESEARCH.md). Single-write-site finding for `PowerCable` confirmed by exhaustive grep against `set_PowerCable` and `PowerCable = ` in the decomp (only matches: lines 350786, 350790 in `FindPowerCable`).

## Open questions

- Whether `InitializeDataConnection` runs during `Constructor.SpawnConstruct` on a server build vs only later via the `OnRegistered` chain. The decomp suggests `Thing.Create` -> `OnRegistered(cell)` chain is the only path, but the exact `Thing.Create` flow is not fully traced here.
