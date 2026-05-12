---
title: Cable
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-12
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.Cable
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 293196-293256 (NetworkType / ConnectionRole / Connection), 371283-371673 (Cable)
  - Plans/PowerGridPlus/PLAN.md (PGP-3 research)
related:
  - ./PowerTick.md
  - ./CableNetwork.md
  - ./Transformer.md
  - ../GameSystems/StructurePlacementValidation.md
  - ../GameClasses/MultiMergeConstructor.md
tags: [power, prefab]
---

# Cable

Vanilla placed power-cable structure. `Assets.Scripts.Objects.Electrical.Cable`. Each placed cable segment is a `Cable` instance; a connected island of cables forms one `CableNetwork`. The coil item the player carries is a separate `MultiMergeConstructor`-style item; the placed structures it builds are `Cable` (the 1-cell piece and the `StructureCableSuperHeavyStraight3/5/10` long pieces, see [MultiMergeConstructor](./MultiMergeConstructor.md)).

## Class hierarchy and key fields
<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

```csharp
public class Cable : SmallSingleGrid, IGridMergeable, ISmartRotatable, IRocketInternals, IRocketComponent
{
    public enum Type
    {
        normal,
        heavy,
        superHeavy
    }

    [Header("Cables")]
    public float MaxVoltage = 5000f;

    [ReadOnly]
    public long CableNetworkId;

    public CableRuptured RupturedPrefab;

    public Type CableType;

    public bool IsStraight;

    public bool BlockMergeWithOtherCables;
    ...
    public CableNetwork CableNetwork
    {
        get => _cableNetwork;
        set
        {
            if (!object.Equals(_cableNetwork, value))
            {
                _cableNetwork = value;
                if (this.OnPowerNetworkChanged != null)
                    this.OnPowerNetworkChanged();
            }
        }
    }
}
```

Full hierarchy: `Thing -> Structure -> SmallGrid -> SmallSingleGrid -> Cable`. (`Device -> DeviceCableMounted` is the sibling branch holding cable-mounted devices: `CableFuse`, `CableAnalyser`.)

Notes:

- **Three cable tiers ship in the base game**: `Cable.Type { normal, heavy, superHeavy }` (line 371285). `normal` is the in-game "Cable" family (and the insulated variant); `heavy` is "Heavy Cable"; `superHeavy` is "Super Heavy Cable" (the long `StructureCableSuperHeavyStraight3/5/10` pieces and the 1-cell super-heavy piece). There is no "small vs medium" distinction in vanilla; `normal` covers everything below `heavy`.
- **`MaxVoltage` is the rupture threshold in watts, despite the name.** It is a per-prefab serialized field (`[Header("Cables")] public float MaxVoltage = 5000f;`, line 371295); `5000f` is only the C# default. The real `heavy` and `superHeavy` values live in the prefab/asset data, not in the decompile (verify via InspectorPlus: `types=[Cable], fields=[CableType, MaxVoltage, DisplayName, PrefabHash, BlockMergeWithOtherCables]` on a save with one of each cable, or extract the prefab). The Stationpedia uses it: a cable page's break text is `StringManager.Get(cable.MaxVoltage) + "W"`.
- **`CableType` is consulted only inside `Cable` itself**, for collision / merge decisions (see below). It does NOT participate in network membership: `IsConnected` / `ConnectedCables` / `CableNetwork.Merge` ignore `CableType`. So a `heavy` cable and a `normal` cable that are grid-adjacent will NOT visually merge into one mesh, but DO end up on the same `CableNetwork` and are electrically one network.

## Connection model: NetworkType / ConnectionRole / Connection
<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

A cable's `OpenEnds` are `Connection` objects. Power cables ship with `ConnectionType = NetworkType.Power` (or `PowerAndData = 6`). There is **no per-cable-tier `NetworkType`** -- the network plumbing is tier-agnostic; the tier lives only on `Cable.CableType` / `Cable.MaxVoltage`.

```csharp
[Flags]
public enum NetworkType
{
    None = 0,
    Pipe = 1,
    Power = 2,
    Data = 4,
    Chute = 8,
    Elevator = 0x10,
    PipeLiquid = 0x20,
    LandingPad = 0x40,
    LaunchPad = 0x80,
    RoboticArmRail = 0x100,
    PowerAndData = 6,
    All = int.MaxValue
}

public enum ConnectionRole
{
    None,
    Input,
    Input2,
    Output,
    Output2,
    Waste
}

[Serializable]
public class Connection
{
    public NetworkType ConnectionType = NetworkType.Pipe;   // power cables override to Power / PowerAndData
    public Transform Transform;
    public Collider Collider;
    public ConnectionRole ConnectionRole;       // Input / Output / None -- used by ElectricalInputOutput to split which cable is input vs output
    [ReadOnly] public Renderer HelperRenderer;
    public SmallGrid Parent;
    ...
}
```

Cable-to-cable and cable-to-device adjacency is decided by `SmallGrid.IsConnected(Connection otherEnd)`: an open end on `this` whose grid position faces `otherEnd` and whose `(openEnd.ConnectionType & otherEnd.ConnectionType) != NetworkType.None`. Pure grid-adjacency plus bitmask overlap; **no type check, no `CableType` check.** A `Device` picks its first power-typed adjacent cable as its `PowerCable` (`Device.FindPowerCable` -> `ConnectedCables(NetworkType.Power)[0]`). A transformer / transmitter (`ElectricalInputOutput`) distinguishes its input vs output cable by which serialized `Connection` object it is (`ConnectionRole.Input` vs `Output`), resolved in `CheckConnections()` -- see [Transformer](./Transformer.md).

## CableType collision / merge gating that already exists
<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

`CableType` is enforced in three places, all of which gate visual merging / grid occupancy, not electrical-network membership:

```csharp
// Cable._IsCollision -- two cables of different CableType "collide" (one cannot occupy the other's merge cell)
protected override bool _IsCollision(SmallGrid smallGrid)
{
    if (!(smallGrid is Cable cable))
        return base._IsCollision(smallGrid);
    if (cable.CableType != CableType)
        return true;
    if (!cable.BlockMergeWithOtherCables)
        return BlockMergeWithOtherCables;
    return true;
}

// Cable.CanReplace (the IGridMergeable angle-grinder-merge path) -- refuses to merge onto a different-tier cell
... in CanReplace(MultiConstructor constructor, Item inactiveHandItem):
    if (smallCell.Cable.CableType != CableType)
        return CanConstructInfo.InvalidPlacement(GameStrings.CannotMergeIMergeableOfDifferentType.AsString(smallCell.Cable.DisplayName));
    return CanConstructInfo.ValidPlacement;

// Cable.WillMergeWhenPlaced -- returns true only when the adjacent cell holds a cable of the same CableType
public bool WillMergeWhenPlaced()
{
    if (BlockMergeWithOtherCables) return false;
    ...
        if (smallCell.Cable.CableType == CableType)
            return !BlockMergeWithOtherCables;
        return false;
    ...
}
```

**Consequence for a "voltage tier" mod**: today heavy and normal cables can sit grid-adjacent (they just do not fuse meshes) and they DO join the same `CableNetwork` because `OnRegistered` calls `CableNetwork.Merge(CableNetwork.ConnectedNetworks(this))` and the merge path ignores `CableType`. To make tiers electrically distinct a mod must either (a) reject the placement at build time via a `Cable.CanConstruct` / `Device.CanConstruct` postfix (clean, player-visible message, client-side only -- see [StructurePlacementValidation](../GameSystems/StructurePlacementValidation.md)), or (b) sever the mixed network after the fact in an `OnRegistered` postfix / `OnFinishedLoad` pass (uglier, needed anyway for migrating saves that already have a mixed junction).

## Placement / registration: OnRegistered, CanConstruct, CanDeconstruct
<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

```csharp
public override void OnRegistered(Cell cell)
{
    if (GameManager.GameState != GameState.Loading && GameManager.RunSimulation)
    {
        CableNetwork cableNetwork = CableNetwork.Merge(CableNetwork.ConnectedNetworks(this));
        if (cableNetwork != null)
            cableNetwork.Add(this);
        else
            new CableNetwork(this);
    }
    base.OnRegistered(cell);
}

public override CanConstructInfo CanConstruct()
{
    if (IsConnectingToUmbilical(out var found))
        return CanConstructInfo.InvalidPlacement(GameStrings.PlacementIsNotUmbilicalConnector.AsString(found.AsThing.ToTooltip()));
    return base.CanConstruct();
}
```

`OnRegistered` does **no tier validation**. `CanConstruct` already overrides the base (umbilical check) -- this is the natural extension point for a build-time tier gate, since `Cable.CanConstruct` is consulted every frame by the placement preview (`InventoryManager` placement loop calls `ConstructionCursor.CanConstruct()` and gates the left-click on the result; see [StructurePlacementValidation](../GameSystems/StructurePlacementValidation.md)). `CanDeconstruct` refuses if `AttachedDevices` is non-empty.

`Cable.DeserializeSave` rejoins networks: `(Referencable.Find<CableNetwork>(id) ?? new CableNetwork(id)).Add(this)`. `Cable.DeserializeOnJoin` (multiplayer client join) re-merges via `CableNetwork.Merge(CableNetwork.ConnectedNetworks(this))`. Both with **no `CableType` check**, so a save (or a client join) that already has a heavy-to-normal junction silently rebuilds it as one network.

## Cable rupture: Break()
<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

```csharp
public void Break()
{
    if (ThreadedManager.IsThread)
    {
        UnityMainThreadDispatcher.Instance().Enqueue(WaitThenBreak());
        return;
    }
    if (CableNetwork.RequiredLoad > 0f)
        WorldManager.Spark(base.ThingTransformPosition, 20, base.GridController.RoomController.GetRoom(base.WorldGrid) != null);
    CreateStructureInstance instance = new CreateStructureInstance(RupturedPrefab, this);
    OnServer.Destroy(this);
    Constructor.SpawnConstruct(instance);
}
```

`Break()` self-marshals to the main thread when called from a worker (`ThreadedManager.IsThread`), spawns the `RupturedPrefab` (`CableRuptured`), destroys the cable on the server, and (re)spawns the ruptured structure. Reached from `PowerTick.BreakSingleCable` (over-current; see [PowerTick](./PowerTick.md)), `WeldingTorch.AttackWith` (player burns it deliberately), and `ElectricalInputOutput.OnSubmergeableTick` (submerged-in-liquid rupture).

The vanilla over-current check that decides whether a cable ruptures is `PowerTick.GetBreakableCables` (`cable.MaxVoltage < _actual`) -- documented on [PowerTick](./PowerTick.md). A mod that wants heavy cables to never burn must intercept that (or `BreakSingleCable`, or guard `Break()` for `CableType >= heavy`). Re-Volt replaces the whole `PowerTick` with its `RevoltTick : PowerTick` (its `CableNetworkPatches.Inject` postfixes the `CableNetwork` constructors and assigns `CableNetwork.PowerTick = new RevoltTick()`), and `RevoltTick.TestBurnCable` reads `cable.MaxVoltage` from a `SortedList<float, List<Cable>>` keyed by `MaxVoltage`; a mod-compat concern is that with both installed, a `PowerTick.GetBreakableCables` patch never fires.

## Verification history

- 2026-05-12: page created. Sourced from a PGP-3 research dive (planned mod "Power Grid Plus") into `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` lines 293196-293256 and 371283-371673; verbatim excerpts of `NetworkType`/`ConnectionRole`/`Connection`, `Cable` class header + `Type` enum + `MaxVoltage`, `Cable._IsCollision`/`CanReplace`/`WillMergeWhenPlaced`, `Cable.OnRegistered`/`CanConstruct`/`Break`. The `superHeavy` tier and the `StructureCableSuperHeavyStraight3/5/10` long pieces corroborate the existing [MultiMergeConstructor](./MultiMergeConstructor.md) page. Re-Volt mod source (`RevoltTick : PowerTick`) corroborates `MaxVoltage` as the burn threshold.

## Open questions

- Real `MaxVoltage` values for the `heavy` and `superHeavy` cable prefabs (prefab serialized data, not in the decompile). Need InspectorPlus or a prefab extract.
- Code names of the heavy / super-heavy / insulated coil items (`ItemCableCoilHeavy`? something for `superHeavy`? the insulated variant's prefab name) and how the insulated cable avoids burning (a separate prefab with a very high `MaxVoltage`, or a flag not visible in the decompile?). Verify against `Prefab.AllPrefabs` / the Stationpedia / the prefab list.
