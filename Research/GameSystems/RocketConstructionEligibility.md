---
title: RocketConstructionEligibility
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-21
sources:
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: Objects.Rockets.RocketInternalCellType enum (L147734-147746), IRocketComponent (L141462-141470), IRocketInternals (L140566-140587)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: SmallGrid.CanConstruct (L294093-L294146, gate at L294100), StructureFuselage.CanConstruct (L150901-L150971)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: Prefab.RegisterExisting (L303904, classifier at L304008), Stationpedia.AddRocketInfo (L231722-231740)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: RocketGrid.IsCollision (L176959-176974)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: per-class IRocketInternals implementers (Battery L370616, Transformer L403300, Pipe L362671, Cable L371283, Chute L346123, Tank L366938, etc.)
related:
  - ./StructurePlacementValidation.md
  - ./PlacementOrientation.md
  - ../GameClasses/RocketPowerUmbilical.md
  - ../GameClasses/Constructor.md
  - ../GameClasses/Structure.md
tags: [prefab, transforms]
---

# RocketConstructionEligibility

How the game decides whether a structure / device belongs to the "rocket family" and how that gates where it may be built (inside a rocket fuselage vs on a station vs anywhere). The decision is NOT made by prefab name. It is made by two interface implementations plus two members the game reads off each prefab:

- `IRocketComponent` (marker interface) and `IRocketInternals : IRocketComponent` (carries the build-rule data).
- `IRocketInternals.InternalCellType` (a `[Flags] RocketInternalCellType`) and `IRocketInternals.StrictlyInternal` (a `bool`).

Two distinct questions get answered from these:

1. **"Is this thing constructable in a rocket at all?"** (the rocket-family membership / Stationpedia "Constructable In Rockets: True" flag). Read from `IRocketComponent` + `InternalCellType != None`.
2. **"Can this thing ONLY be built inside a rocket (never on the station / surface)?"** (the placement gate that rejects outside-rocket placement). Read from `StrictlyInternal`.

These are independent: a `Pipe` is rocket-family (question 1 = yes) but NOT strictly internal (question 2 = no, it builds anywhere). A `RocketScanner` is both.

## The membership classifier: IRocketComponent + InternalCellType != None
<!-- verified: 0.2.6228.27061 @ 2026-06-21 -->

The single canonical expression the game uses to decide "this prefab is rocket-family / constructable in rockets" appears verbatim in two places, both running once at prefab-load time:

`Prefab.RegisterExisting(Thing prefab)` at L303904, gate at L304008 (only reached inside `if (prefab is Structure item3)`):

```csharp
if (prefab is IRocketComponent rocketComponent && !(rocketComponent is IRocketInternals { InternalCellType: RocketInternalCellType.None }))
{
    IRocketComponent.AllRocketPrefabs.Add(rocketComponent);
}
```

`Stationpedia.AddRocketInfo(Thing prefab, ref StationpediaPage page)` at L231722:

```csharp
private void AddRocketInfo(Thing prefab, ref StationpediaPage page)
{
    if ((prefab is IRocketComponent rocketComponent && !(rocketComponent is IRocketInternals { InternalCellType: RocketInternalCellType.None })) ? true : false)
    {
        page.PlaceableInRocket = GameStrings.ConstructableInRocketsTrue;
        if (prefab is IRocketMassContributor rocketMassContributor)
        {
            page.RocketMass = rocketMassContributor.MassContribution.ToStringRounded() + "kg";
        }
        if (prefab is IRocketEngine rocketEngine) { /* engine stats */ }
    }
}
```

Read the boolean exactly: a prefab is rocket-family TRUE when it implements `IRocketComponent` AND it is not the special case of being an `IRocketInternals` whose `InternalCellType` is `None`. Decomposed:

- Implements `IRocketComponent` but NOT `IRocketInternals` (a bare marker, e.g. the umbilical Male halves, `StructureFuselage`): the negated `is IRocketInternals { ... }` pattern is false, so `!(false)` is true. **Rocket-family TRUE.**
- Implements `IRocketInternals` with `InternalCellType != None`: `is IRocketInternals { InternalCellType: None }` is false, `!(false)` is true. **Rocket-family TRUE.**
- Implements `IRocketInternals` with `InternalCellType == None`: `is IRocketInternals { InternalCellType: None }` is true, `!(true)` is false. **Rocket-family FALSE** (even though it implements the rocket interface).
- Implements neither interface (an ordinary station device, e.g. `WallLight`): the first clause `prefab is IRocketComponent` is false. **Rocket-family FALSE.**

`IRocketComponent.AllRocketPrefabs` (a `static List<IRocketComponent>` on the interface, L141464-141468) is the in-memory list of every rocket-family Structure prefab, populated by the L304008 gate during prefab load. The Stationpedia flag drives the in-game "Constructable In Rockets" panel row (`StationpediaPage.PlaceableInRocket`, displayed by `SetRocketPanelValues` L62814 / `Show` L62809; the panel row shows only when the string is non-empty). Note there is no `ConstructableInRocketsFalse` string: the panel row is simply hidden for non-rocket-family prefabs (`!string.IsNullOrEmpty(page.PlaceableInRocket)`).

## The placement gate: StrictlyInternal -> "Cannot place outside of a rocket"
<!-- verified: 0.2.6228.27061 @ 2026-06-21 -->

Membership (above) does not stop you placing a rocket-family device on the station. The thing that forbids outside-rocket placement is `StrictlyInternal`, checked in `SmallGrid.CanConstruct()`. `SmallGrid : Structure` (L293474) is the base placement class every small-grid device, pipe, cable, chute inherits, so this gate covers essentially all rocket-internal devices.

`SmallGrid.CanConstruct()` L294093, gate at L294100:

```csharp
public override CanConstructInfo CanConstruct()
{
    Grid3[] array = (Grid3[])GridBounds.GetLocalSmallGrid(base.ThingTransformPosition, base.ThingTransformRotation);
    for (int i = 0; i < array.Length; i++)
    {
        Grid3 localGrid = array[i];
        SmallCell smallCell = base.GridController.GetSmallCell(localGrid);
        if (this is IRocketInternals { StrictlyInternal: not false } && !(smallCell?.Owner is RocketNetwork))
        {
            return CanConstructInfo.InvalidPlacement(GameStrings.CannotPlaceOutsideRocket);
        }
        if (smallCell?.Owner == null && this is IRocketInternals)
        {
            Grid3 localGrid2 = new Grid3(localGrid.x, (float)localGrid.y + SmallGridSize * 10f, localGrid.z);
            Grid3 localGrid3 = new Grid3(localGrid.x, (float)localGrid.y - SmallGridSize * 10f, localGrid.z);
            SmallCell smallCell2 = base.GridController.GetSmallCell(localGrid2);
            SmallCell smallCell3 = base.GridController.GetSmallCell(localGrid3);
            if (smallCell2?.Owner != null || smallCell3?.Owner != null)
            {
                return CanConstructInfo.InvalidPlacement(GameStrings.PlacementConnectingNeedsFuselage.DisplayString);
            }
        }
        // ... per-cell collision checks against Cable/Device/Pipe/Chute/Rail/Other ...
    }
    // ...
}
```

The first branch (L294100) is THE rocket-interior gate: a `StrictlyInternal` rocket device can only be placed where the small cell's owner is a `RocketNetwork`. `{ StrictlyInternal: not false }` is the C# property-pattern for `StrictlyInternal == true`. `GameStrings.CannotPlaceOutsideRocket` (L265489) = `"Cannot place outside of a rocket"`.

The second branch (L294104) handles any `IRocketInternals` (strict or not) placed in an empty cell that is vertically adjacent to a rocket grid: it must connect through a fuselage. `GameStrings.PlacementConnectingNeedsFuselage` (L266159) = `"Placement that connects to a <color=green>Rocket</color> needs to be inside a <color=green>Fuselage</color> or via <color=green>Umbilical</color>"`.

This is a client-side build-cursor gate (per [StructurePlacementValidation](./StructurePlacementValidation.md), `Constructor.SpawnConstruct` on the server does NOT re-run `CanConstruct`).

### The fuselage side of the same coin
<!-- verified: 0.2.6228.27061 @ 2026-06-21 -->

`StructureFuselage.CanConstruct()` (L150901, class `StructureFuselage : LargeStructure, ..., IRocketComponent, IRocketMassContributor` L150722) validates the reverse direction: placing a fuselage over already-present rocket internals. It walks the fuselage's `InternalCellOffsets` and for each occupied small cell requires the occupant to be an `IRocketInternals` whose `InternalCellType` flag overlaps the offset's `CellType` (otherwise "grid blocked"):

```csharp
if ((bool)smallCell.Device)
{
    if (!(smallCell.Device is IRocketInternals rocketInternals))
        return CanConstructInfo.InvalidPlacement(GameStrings.GridBlockedByStructure.AsString(smallCell.Device.DisplayName));
    if ((rocketInternals.InternalCellType & internalCellOffset.CellType) == 0)
        return CanConstructInfo.InvalidPlacement(GameStrings.GridBlockedByStructure.AsString(smallCell.Device.DisplayName));
}
```

The same flag-mask test (`(occupant.InternalCellType & cellType) == 0` -> blocked) drives the runtime grid-collision check `RocketGrid.IsCollision(SmallGrid gridObject, Grid3 grid)` (L176959):

```csharp
public bool IsCollision(SmallGrid gridObject, Grid3 grid)
{
    if (!_smallGridsOccupied.TryGetValue(grid, out var value)) return false;
    if (!(gridObject is IRocketInternals rocketInternals)) return true;
    if (rocketInternals.InternalCellType == RocketInternalCellType.None) return true;
    return (value.CellType & rocketInternals.InternalCellType) != rocketInternals.InternalCellType;
}
```

So `InternalCellType` is a layering mask: it lets several rocket-internal kinds (e.g. a pipe and a cable) share one fuselage cell when their flags differ, while a non-rocket-internal (or an `InternalCellType.None` one) always collides.

## RocketInternalCellType: the enum
<!-- verified: 0.2.6228.27061 @ 2026-06-21 -->

`[Flags] RocketInternalCellType` (L147734-147746), in the `Objects.Rockets` namespace:

```csharp
[Flags]
public enum RocketInternalCellType
{
    None = 0,
    Pipes = 1,
    Cables = 2,
    Devices = 4,
    Chutes = 8,
    Umbilical = 0x10,   // 16
    Engine = 0x20,      // 32
    CargoBay = 0x40,    // 64
    CableConnector = 0x80  // 128
}
```

It is a bitfield (the layering mask above), but in practice each `IRocketInternals` implementer reports a single value (e.g. `Devices`, `Umbilical`, `Engine`, `Chutes`). `None` is the sentinel that excludes a prefab from rocket-family membership and from grid layering. Supporting helper types: `RocketInternalCellTypeRange { Vector2 Range; RocketInternalCellType CellType; }` (L147715), `RocketInternalCellOffset { Vector3 Offset; RocketInternalCellType CellType; }` (L147722, what `StructureFuselage.InternalCellOffsets` is a list of), `RocketOccupiedCell { RocketInternalCellType CellType; bool Overlapping; }` (L147728).

## The two interfaces
<!-- verified: 0.2.6228.27061 @ 2026-06-21 -->

`IRocketComponent` (L141462) is a bare marker with one static member, the prefab list:

```csharp
public interface IRocketComponent
{
    static List<IRocketComponent> AllRocketPrefabs;   // L141464
    static IRocketComponent() { AllRocketPrefabs = new List<IRocketComponent>(); }
}
```

`IRocketInternals : IRocketComponent` (L140566) carries the build-rule data plus rocket-network plumbing:

```csharp
public interface IRocketInternals : IRocketComponent
{
    RocketInternalCellType InternalCellType { get; }   // L140568
    bool StrictlyInternal { get; }                      // L140570
    RocketNetwork RocketNetwork { get; set; }
    Vector3 Position { get; set; }
    Vector3 ThingTransformPosition { get; }
    Transform Transform { get; }
    List<Assets.Scripts.Objects.Connection> AccessOpenEnds { get; }
    WorldGrid WorldGrid { get; set; }
    void OnLaunch(bool immediate = false);
    void OnLanded(bool immediate = false);
}
```

`IRocketEngine : IRocketInternals, IRocketComponent` (L140588) adds thrust stats; `IRocketMassContributor` (L140562) adds `MassContribution`. These are how `AddRocketInfo` decides whether to also show engine / mass rows.

## Serialized vs code: where InternalCellType and StrictlyInternal come from
<!-- verified: 0.2.6228.27061 @ 2026-06-21 -->

This is mixed across classes, which is the decisive fact for any per-prefab dump: **you cannot statically derive the values for every prefab from the C# alone; some classes read them from Unity-serialized prefab fields.** The property accessors are uniform (always `IRocketInternals.InternalCellType` / `.StrictlyInternal`), but the backing differs:

- **Hardcoded in C#** (most rocket-specific classes): e.g. `RocketScanner.InternalCellType => RocketInternalCellType.Devices` (L41894) and `StrictlyInternal => true` (L41896). These ARE statically derivable.
- **Serialized prefab field** (`[SerializeField]` private/protected): the property returns a backing field whose value lives in the prefab asset, NOT in the DLL. Confirmed cases:
  - `Battery` (L370616): `InternalCellType => _rocketInternalCellType` (L370659, field `[SerializeField] private RocketInternalCellType _rocketInternalCellType` L370633-370634) AND `StrictlyInternal => _strictlyInternal` (L370661, field `[SerializeField] private bool _strictlyInternal` L370636-370637). BOTH serialized.
  - `Transformer` (L403300): same pattern, `InternalCellType => _rocketInternalCellType` (L403353, field L403327-403328) AND `StrictlyInternal => _strictlyInternal` (L403355, field L403330-403331). BOTH serialized.
  - `Pipe` (L362671): `InternalCellType => _rocketInternalCellType` (L362766, field L362695-362696, serialized); `StrictlyInternal => false` (L362768, hardcoded).
  - `Tank` (L366938): `InternalCellType => _rocketInternalCellType` (L366943, field L366940-366941, serialized); `StrictlyInternal => InternalCellType != RocketInternalCellType.None` (L366945, an EXPRESSION over the serialized field).
  - `Cable` (L371283): `InternalCellType => _rocketInternalCellType` (L371318, field L371308-371309, serialized); `StrictlyInternal => false` (L371320, hardcoded).
  - `DirectHeatExchanger` (L356081): `InternalCellType => rocketInternalCellType` (L356131, field `[SerializeField] protected RocketInternalCellType rocketInternalCellType` L356086-356087, serialized); `StrictlyInternal => false` (L356133, hardcoded).

Consequence: classes like `Battery`, `Transformer`, `Pipe`, `Tank`, `Cable`, `DirectHeatExchanger` are ONE C# class serving multiple prefabs (`StructureBattery`, `StructureBatterySmall`, `StructureBatteryMedium` are all the `Battery` class). The decompile cannot tell the variants apart; their rocket-eligibility is entirely a function of the serialized `_rocketInternalCellType` / `_strictlyInternal` values on each distinct prefab. To know them you MUST read a loaded prefab at runtime (see Runtime read recipe). Do not assume "Battery is rocket-family" or "Battery is strictly internal" from the class; it is per-prefab.

## Runtime read recipe (per prefab from Prefab.AllPrefabs)
<!-- verified: 0.2.6228.27061 @ 2026-06-21 -->

Given a `Thing thing` from `Prefab.AllPrefabs`, the eligibility data is read through the public interfaces, no reflection needed (`InternalCellType` and `StrictlyInternal` are public interface members):

```csharp
// 1. Rocket-family membership (the exact game rule, mirrors L304008 / L231724):
bool isRocketFamily =
    thing is IRocketComponent rc &&
    !(rc is IRocketInternals { InternalCellType: RocketInternalCellType.None });

// 2. The two data members (null when the thing is not IRocketInternals):
var internals = thing as IRocketInternals;          // null if it only implements IRocketComponent, or neither
RocketInternalCellType? cellType = internals?.InternalCellType;
bool? strictlyInternal = internals?.StrictlyInternal;

// 3. Refinements:
bool isBareMarker  = thing is IRocketComponent && !(thing is IRocketInternals); // umbilical Male halves, Fuselage
bool rocketOnly    = strictlyInternal == true;      // "can ONLY be built inside a rocket"
bool buildsAnywhere = isRocketFamily && strictlyInternal == false;             // rocket-usable but station-buildable too
bool isEngine      = thing is IRocketEngine;
float? rocketMass  = (thing as IRocketMassContributor)?.MassContribution;
```

Notes:
- The interfaces are in the `Assets.Scripts.Objects.Rockets` namespace (`IRocketComponent`, `IRocketInternals`, `IRocketEngine`, `IRocketMassContributor`, `RocketInternalCellType`); `IRocketMassContributor` is at the rockets-namespace level too.
- `thing as IRocketInternals` returns non-null for ~41 classes (see table below). Reading `.InternalCellType` / `.StrictlyInternal` triggers the serialized-or-hardcoded getter transparently; the loaded prefab carries the correct serialized values.
- The membership classifier (`isRocketFamily`) is exactly what populates `IRocketComponent.AllRocketPrefabs`. If you only need the membership list and the game is past prefab-load, you can also read `IRocketComponent.AllRocketPrefabs` directly instead of re-scanning `AllPrefabs`. Note that list is filtered to `Structure` prefabs only (the L304008 gate sits inside `if (prefab is Structure)`), whereas a full `AllPrefabs` scan would also surface any non-Structure `IRocketComponent` (none exist among rocket parts today; all are Structures).

## Outcome categories (map each to the data)
<!-- verified: 0.2.6228.27061 @ 2026-06-21 -->

Distinct outcomes the data encodes, and the value combination that produces each:

| Outcome | `IRocketComponent`? | `IRocketInternals`? | `InternalCellType` | `StrictlyInternal` | Membership (Q1) | Outside-rocket build blocked? (Q2) |
|---|---|---|---|---|---|---|
| Ordinary station thing | no | no | n/a | n/a | FALSE | no (normal placement rules) |
| Rocket-only interior device | yes | yes | non-`None` (usually `Devices`/`Umbilical`/`Engine`) | `true` | TRUE | YES (`CannotPlaceOutsideRocket`) |
| Rocket-usable, station-buildable | yes | yes | non-`None` (`Pipes`/`Cables`/`Chutes`/`Devices`) | `false` | TRUE | no (but fuselage-adjacency rule still applies) |
| Bare rocket marker (umbilical Male, Fuselage) | yes | no | n/a | n/a | TRUE | no (no `StrictlyInternal` to gate on) |
| Rocket-interface but disabled | yes | yes | `None` | (Tank: `false` by its expression) | FALSE | no |

The single best boolean for "is this part of the rocket family" is the membership classifier (Q1): `thing is IRocketComponent rc && !(rc is IRocketInternals { InternalCellType: RocketInternalCellType.None })`. That is the game's own definition (it is literally the `AllRocketPrefabs` filter and the Stationpedia "Constructable In Rockets" flag).

One boolean is enough for "rocket family yes/no". But if the goal is to distinguish the *kinds* of rocket relationship (the user-visible distinction between "rocket-only" parts and "works in rockets but also on station"), you need a second axis, `StrictlyInternal`, and optionally `InternalCellType` for the cell-kind (`Engine` vs `Umbilical` vs `Devices` vs `Pipes`/`Cables`/`Chutes`). So: one boolean for membership; add `StrictlyInternal` to separate rocket-only from rocket-capable; add `InternalCellType` for the fine category.

## Per-class IRocketInternals table (decompile-derived)
<!-- verified: 0.2.6228.27061 @ 2026-06-21 -->

Every class implementing `IRocketInternals` (directly or via `IRocketEngine`), with the source of each value. "code" = hardcoded enum/bool literal in C# (statically derivable); "SERIALIZED" = `[SerializeField]` backing field, prefab-asset data (must read at runtime). Line numbers are the getter sites.

| Class | Base | InternalCellType | Src | StrictlyInternal | Src |
|---|---|---|---|---|---|
| `RocketPayloadBay` (L41668) | DeviceInputOutput | `Devices` (L41682) | code | `true` (L41684) | code |
| `RocketScanner` (L41855) | Device | `Devices` (L41894) | code | `true` (L41896) | code |
| `CrewModuleChair` (L140216) | Device | `Devices` (L140231) | code | `true` (L140233) | code |
| `CrewModuleScreen` (L140317) | Device | `Devices` (L140326) | code | `true` (L140328) | code |
| `RocketAvionicsDevice` (L144527) | Device | `Devices` (L144591) | code | `true` (L144593) | code |
| `RocketCelestialTracker` (L145772) | Device | `Devices` (L145826) | code | `true` (L145828) | code |
| `RocketChuteUmbilicalFemale` (L146220) | ChuteDevice | `Umbilical` (L146242) | code | `true` (L146244) | code |
| `RocketGasUmbilicalFemale` (L147196) | DeviceInput | `Umbilical` (L147218) | code | `true` (L147220) | code |
| `RocketPowerUmbilicalFemale` (L147895) | ElectricalInputOutput | `Umbilical` (L148027) | code | `true` (L148029) | code |
| `CondensationValve` (L163881) | DeviceInputOutput | `Devices` (L163886) | code | `false` (L163888) | code |
| `PassiveLiquidDrain` (L164659) | DevicePipeMounted | `Devices` (L164661) | code | `false` (L164663) | code |
| `VolumePump` (L164947) | SettableAtmosDevice | `Devices` (L164979) | code | `false` (L164981) | code |
| `Chute` (L346123) | SmallSingleGrid | `Chutes` (L346151) | code | `false` (L346153) | code |
| `DirectHeatExchanger` (L356081) | HeatExchangerBase | `rocketInternalCellType` (L356131) | SERIALIZED (L356087) | `false` (L356133) | code |
| `Mixer` (L361678) | SettableAtmosDevice | `Devices` (L361684) | code | `false` (L361686) | code |
| `OneWayValve` (L362246) | DeviceInputOutput | `Devices` (L362248) | code | `false` (L362250) | code |
| `OneWayValveLever` (L362271) | DeviceInputOutput | `Devices` (L362276) | code | `false` (L362278) | code |
| `Pipe` (L362671) | SmallSingleGrid | `_rocketInternalCellType` (L362766) | SERIALIZED (L362696) | `false` (L362768) | code |
| `PipeAnalysizer` (L363452) | DevicePipeMounted | `Devices` (L363456) | code | `false` (L363458) | code |
| `PipeGasMeter` (L363725) | DevicePipeMounted | `Devices` (L363760) | code | `false` (L363762) | code |
| `PipeHeater` (L363815) | DevicePipeMounted | `Devices` (L363823) | code | `false` (L363825) | code |
| `PressureRegulator` (L364623) | SettableAtmosDevice | `Devices` (L364632) | code | `false` (L364634) | code |
| `RocketDataDownLink` (L364757) | RocketDataLink | `Devices` (L364765) | code | `true` (L364767) | code |
| `RocketEngineBase` (L365437) | DeviceInput | `Engine` (L365657) | code | `false` (L365539) | code |
| `RocketFiltrationMachine` (L366129) | FiltrationMachine | `Devices` | code | `true` (L366135) | code |
| `RocketGasCollector` (L366163) | DeviceMixAtmosphere | `Devices` | code | `true` (L366175) | code |
| `Tank` (L366938) | DeviceInternal | `_rocketInternalCellType` (L366943) | SERIALIZED (L366941) | `InternalCellType != None` (L366945) | expr over serialized |
| `Valve` (L367113) | DeviceAtmospherics | `Devices` (L367120) | code | `false` (L367122) | code |
| `VolumeRegulator` (L367232) | SettableAtmosDevice | `Devices` (L367243) | code | `false` (L367245) | code |
| `RocketMiner` (L368225) | DeviceImportExport | `Devices` (L368314) | code | `true` (L368316) | code |
| `Battery` (L370616) | ElectricalInputOutput | `_rocketInternalCellType` (L370659) | SERIALIZED (L370634) | `_strictlyInternal` (L370661) | SERIALIZED (L370637) |
| `Cable` (L371283) | SmallSingleGrid | `_rocketInternalCellType` (L371318) | SERIALIZED (L371309) | `false` (L371320) | code |
| `CableAnalyser` (L371674) | DeviceCableMounted | `Devices` (L371714) | code | `false` (L371716) | code |
| `RadioscopicThermalGenerator` (L395566) | Electrical | `Devices` (L395574) | code | `false` (L395576) | code |
| `RocketChuteStorage` (L396250) | DeviceImportExport | `Devices` (L396261) | code | `true` (L396263) | code |
| `RocketCircuitHousing` (L396458) | CircuitHousing | `Devices` (L396460) | code | `true` (L396462) | code |
| `Transformer` (L403300) | ElectricalInputOutput | `_rocketInternalCellType` (L403353) | SERIALIZED (L403328) | `_strictlyInternal` (L403355) | SERIALIZED (L403331) |

(~38 implementers verified; the agent pass counted 41 including a couple whose getter lines fell just outside the sampled lines. The structural point stands: ~6 classes read serialized fields, the rest hardcode.)

`IRocketComponent`-only (NOT `IRocketInternals`, so always rocket-family TRUE via the bare-marker path, and never gated by `StrictlyInternal`):

| Class | Base | Interfaces |
|---|---|---|
| `CrewModule` (L140073) | Fuselage | `IUmbilical, IRocketComponent` |
| `RocketChuteUmbilicalMale` (L146383) | ChuteDevice | `IUmbilical, IRocketComponent` |
| `RocketCrewUmbilical` (L146817) | Device | `IUmbilical, IRocketComponent, ISmartRotatable` |
| `RocketGasUmbilicalMale` (L147341) | DeviceInput | `IUmbilical, IRocketComponent` |
| `RocketPowerUmbilicalMale` (L148269) | ElectricalInputOutput | `IUmbilical, IRocketComponent` |
| `StructureFuselage` (L150722) | LargeStructure | `INetworkedRocketPart, ..., IRocketComponent, IRocketMassContributor` |

## Sanity check (used to confirm the rule, not to derive it)
<!-- verified: 0.2.6228.27061 @ 2026-06-21 -->

Classifying the user-named devices with the membership rule + `StrictlyInternal`:

- `StructureRocketTransformerSmall`: no class by that name exists. It is the `Transformer` class (L403300) as a distinct prefab. `InternalCellType` and `StrictlyInternal` are BOTH serialized (`_rocketInternalCellType` L403328, `_strictlyInternal` L403331), so whether this specific prefab is rocket-family / rocket-only is determined by its serialized values, which the decompile cannot read. Confirm at runtime. (The "RocketTransformer" player name plus its rocket role strongly implies `_strictlyInternal = true` and `_rocketInternalCellType = Devices`, but that is a prefab-data claim, not a code claim.)
- `RocketPowerUmbilicalFemale` (L147895): `IRocketInternals`, `InternalCellType => Umbilical` (L148027), `StrictlyInternal => true` (L148029). Membership TRUE; rocket-only TRUE. Correct (the socket lives inside the rocket).
- `RocketPowerUmbilicalMale` (L148269): `IRocketComponent` only (NOT `IRocketInternals`). Membership TRUE via the bare-marker path; not gated by `StrictlyInternal` (it is the external/dockable half, intentionally buildable on the station gantry). Correct, and a good example of why name alone is insufficient: Male and Female differ in interface set.
- `StructureBattery` / `StructureBatterySmall` / `StructureBatteryMedium`: all the SAME `Battery` class (L370616), differing only by serialized `_rocketInternalCellType` (L370634) and `_strictlyInternal` (L370637). The CODE cannot say which battery prefab is rocket-family or rocket-only; that is purely prefab-asset data. This directly answers the user's flag: do NOT assume `StructureBattery` is non-rocket and `StructureBatteryMedium`/`Small` are rocket from the class. Read the serialized values per prefab at runtime. (The expectation: the rocket-intended battery prefabs carry a non-`None` `_rocketInternalCellType`, and `StructureBattery` the plain wall battery carries `None` -> not rocket-family; but confirm in a `Prefab.AllPrefabs` dump.)
- `StructureWallLight`: the `WallLight` class implements `IAirlockDevice, ISmartRotatable, ILight` only, neither `IRocketComponent` nor `IRocketInternals`. Membership FALSE. Correct (ordinary station light).
- Rocket-named but NOT rocket-family: none found. Every `class *Rocket*` that is a buildable Structure implements `IRocketComponent` (or a derived interface). Non-structure rocket-named types (save-data, effects, UI) are not Structures and never reach the classifier.
- NOT rocket-named but IS rocket-family (the load-bearing "don't classify by name" evidence): `Pipe`, `Cable`, `Chute`, `Tank`, `Valve`, `Mixer`, `Battery`, `Transformer`, `VolumePump`, `OneWayValve`, `PressureRegulator`, `PipeHeater`, `PipeGasMeter`, `PipeAnalysizer`, `CableAnalyser`, `CondensationValve`, `PassiveLiquidDrain`, `VolumeRegulator`, `DirectHeatExchanger`, `RadioscopicThermalGenerator` all implement `IRocketInternals` with non-`None` cell types and are therefore rocket-family (most with `StrictlyInternal => false`, i.e. buildable on station too). Their names carry no "Rocket".

## Verification history

- 2026-06-21: serialized per-prefab values confirmed live via a `Prefab.AllPrefabs` runtime dump (ScenarioRunner `device-port-dump` on the dedicated server, full mod set, game 0.2.6228.27061), reading `InternalCellType` / `StrictlyInternal` through the `IRocketInternals` interface type (explicit-impl-safe). Scope: the 378 power/data-bearing prefabs. Confirmed values for the serialized classes: **Battery** — `StructureBattery` and `StructureBatteryLarge` = `None` (not rocket family); `StructureBatteryMedium` and `StructureBatterySmall` = `InternalCellType=Devices`, `StrictlyInternal=true` (rocket-only). **Transformer** — only `StructureRocketTransformerSmall` = `Devices` + `StrictlyInternal=true`; the other five (`StructureTransformer`, `StructureTransformerMedium` (+Reversed), `StructureTransformerSmall`, +reversed) = `None`. **Cable** — all 29 are rocket family, `StrictlyInternal=false`: 27 = `Cables`, and `StructureCrewModuleCableConnectorA`/`B` = `CableConnector`. Also confirmed: `AreaPowerControl` (`StructureAreaPowerControl`/`Reversed`) implements neither interface (NOT rocket family); the `Power` umbilical `Female`/`FemaleSide` = `Umbilical` + `StrictlyInternal=true`, while the `Male` power/chute/crew/gas/liquid umbilical halves are bare `IRocketComponent` markers (family, not strict, no `InternalCellType`); engines (`StructureGovernedGasEngine`, `StructurePressureFedGas/LiquidEngine` (+Heavy), `StructurePumpedLiquidEngine`) = `Engine`, not strict. Totals among power/data devices: 92 rocket family, 24 rocket-only. The same-`Battery`-class split (Medium/Small rocket-only vs standard/Large not) is the load-bearing proof that this data is per-prefab serialized and cannot be derived from the C# class.
- 2026-06-21: page created. Sourced from a direct read of `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` (game 0.2.6228.27061) plus one Explore sub-agent pass for the full per-class table. Established: the membership classifier `prefab is IRocketComponent rc && !(rc is IRocketInternals { InternalCellType: None })` (verbatim at L304008 in `Prefab.RegisterExisting` and L231724 in `Stationpedia.AddRocketInfo`); the outside-rocket placement gate `this is IRocketInternals { StrictlyInternal: not false } && !(smallCell?.Owner is RocketNetwork)` in `SmallGrid.CanConstruct` L294100 (`GameStrings.CannotPlaceOutsideRocket` L265489); the `[Flags] RocketInternalCellType` enum (L147734); both interfaces (L140566 / L141462); the fuselage-side `StructureFuselage.CanConstruct` flag-mask (L150901) and `RocketGrid.IsCollision` (L176959); and the serialized-vs-code split (Battery / Transformer fully serialized `_rocketInternalCellType` + `_strictlyInternal`; Pipe / Tank / Cable / DirectHeatExchanger serialized cell-type only; most rocket classes hardcode both). No conflict with existing pages; additive. The `RocketPowerUmbilical` GameClasses page's `StrictlyInternal => true` / `InternalCellType => Umbilical` for the Female (L148027-148029) is consistent with this page.

## Open questions

- Serialized per-prefab values: **RESOLVED for `Battery`, `Transformer`, and `Cable`** via the 2026-06-21 runtime dump (see Verification history). **Still pending live values: `Pipe`, `Tank`, `DirectHeatExchanger`** and any other serialized-cell-type prefabs that are not power/data devices (the dump that confirmed the others was scoped to the 378 power/data-bearing prefabs, so these non-power/data classes were not captured; a full `Prefab.AllPrefabs` dump reading `(thing as IRocketInternals)?.InternalCellType` / `.StrictlyInternal` would close them). The code-derived table above remains complete for everything hardcoded.
