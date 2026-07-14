---
title: Cable
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-14
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.Cable
  - $(StationeersPath)\rocketstation_Data\resources.assets :: Cable prefab MaxVoltage / CableType (read 2026-05-22 via UnityPy + generated type tree)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 293196-293256 (NetworkType / ConnectionRole / Connection), 371283-371673 (Cable)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 392329-392700 (Cable), 311698 (NetworkType), 311786-312023 (Connection), 271203-271220 (RebuildCableNetworkServer overloads), 392848-392881 (CableRuptured), 314440-314471 (Structure.GetPassiveTooltip), 319731-319734 (Thing.GetPassiveTooltip), 392420-392462 (SerializeSave / DeserializeSave / SerializeOnJoin / DeserializeOnJoin), 288665-288726 (InventoryManager cursor gate), 271842-271915 (PowerTick burn selection), 39914-39926 (OnServer.Destroy)
  - Plans/PowerGridPlus/PLAN.md (phase 3 research)
related:
  - ./PowerTick.md
  - ./CableNetwork.md
  - ./Transformer.md
  - ./ElectricalInputOutput.md
  - ./SmallCell.md
  - ../GameSystems/StructurePlacementValidation.md
  - ../GameSystems/StructureRegistration.md
  - ../GameClasses/MultiMergeConstructor.md
tags: [power, prefab]
---

# Cable

Vanilla placed power-cable structure. `Assets.Scripts.Objects.Electrical.Cable`. Each placed cable segment is a `Cable` instance; a connected island of cables forms one `CableNetwork`. The coil item the player carries is a separate `MultiMergeConstructor`-style item; the placed structures it builds are `Cable` (the 1-cell piece and the `StructureCableSuperHeavyStraight3/5/10` long pieces, see [MultiMergeConstructor](./MultiMergeConstructor.md)).

## Class hierarchy and key fields
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

Line refs at 0.2.6403.27689: class 392329, `Type` enum 392331-392336, `MaxVoltage` 392341, `CableNetworkId` 392344, `CableType` 392348, `CableNetwork` property 392370-392387, `OnPowerNetworkChanged` instance event 392389.

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

- **Three cable tiers ship in the base game**: `Cable.Type { normal, heavy, superHeavy }` (line 392331). `normal` is the in-game "Cable" family (and the insulated variant); `heavy` is "Heavy Cable"; `superHeavy` is "Super Heavy Cable" (the long `StructureCableSuperHeavyStraight3/5/10` pieces and the 1-cell super-heavy piece). There is no "small vs medium" distinction in vanilla; `normal` covers everything below `heavy`.
- **`MaxVoltage` is the rupture threshold in watts, despite the name.** It is a per-prefab serialized field (`[Header("Cables")] public float MaxVoltage = 5000f;`, line 392341); `5000f` is only the C# default. Per-tier values, read from `$(StationeersPath)\rocketstation_Data\resources.assets` via UnityPy with a generated type tree (type trees are stripped from this build's serialized files): `normal = 5000 W`, `heavy = 100000 W` (20x normal), `superHeavy = 500000 W` (100x normal). Every cable prefab in a tier carries the same `MaxVoltage`; all 29 cable prefabs ship in `resources.assets` (10 normal, 8 heavy, 11 superHeavy). The Stationpedia uses it: a cable page's break text is `StringManager.Get(cable.MaxVoltage) + "W"`.
- **`CableType` is consulted only inside `Cable` itself**, for collision / merge decisions (see below). It does NOT participate in network membership: `IsConnected` / `ConnectedCables` / `CableNetwork.Merge` ignore `CableType`. So a `heavy` cable and a `normal` cable that are grid-adjacent will NOT visually merge into one mesh, but DO end up on the same `CableNetwork` and are electrically one network.

## Connection model: NetworkType / ConnectionRole / Connection
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

A cable's `OpenEnds` are `Connection` objects (`NetworkType` enum at decompile 311698, `Connection` class at 311786 at 0.2.6403.27689). Power cables ship with `ConnectionType = NetworkType.Power` (or `PowerAndData = 6`). There is **no per-cable-tier `NetworkType`** -- the network plumbing is tier-agnostic; the tier lives only on `Cable.CableType` / `Cable.MaxVoltage`.

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

Cable-to-cable and cable-to-device adjacency is decided by `SmallGrid.IsConnected(Connection otherEnd)` (unchanged at 0.2.6403.27689, line 312730): an open end on `this` whose grid position faces `otherEnd` and whose `(openEnd.ConnectionType & otherEnd.ConnectionType) != NetworkType.None`. Pure grid-adjacency plus bitmask overlap; **no type check, no `CableType` check.** A `Device` picks its first power-typed adjacent cable as its `PowerCable` (`Device.FindPowerCable` -> first `FillConnected<Cable>(NetworkType.Power, ...)` hit; the old `ConnectedCables(NetworkType)` API was REMOVED at 0.2.6403, see [CursorAdjacencyLookup](../Patterns/CursorAdjacencyLookup.md) for the migrated API surface and the mod-side binding constraint on the `Span` signatures). A transformer / transmitter (`ElectricalInputOutput`) distinguishes its input vs output cable by which serialized `Connection` object it is (`ConnectionRole.Input` vs `Output`), resolved in `CheckConnections()` -- see [Transformer](./Transformer.md).

## CableType collision / merge gating that already exists
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

`CableType` is enforced in three places, all of which gate visual merging / grid occupancy, not electrical-network membership. Full verbatim bodies at 0.2.6403.27689:

`Cable._IsCollision` (392588-392603): a cable of a DIFFERENT tier in the cell is a collision; a SAME-tier cable passes (unless a `BlockMergeWithOtherCables` flag intervenes):

```csharp
protected override bool _IsCollision(SmallGrid smallGrid)
{
    if (!(smallGrid is Cable cable))
    {
        return base._IsCollision(smallGrid);
    }
    if (cable.CableType != CableType)
    {
        return true;
    }
    if (!cable.BlockMergeWithOtherCables)
    {
        return BlockMergeWithOtherCables;
    }
    return true;
}
```

`Cable.CanReplace` (392634-392672), the `IGridMergeable` second cursor arm. It demands a `MultiMergeConstructor` cursor AND the constructor's `ToolExit` tool in the off hand; without the tool it fails with `MergeRequiresTool`. When the tool check passes it fires the static `Cable.OnMerge` event (via `WillMergeWhenPlaced()`) and then applies the tier check:

```csharp
public CanConstructInfo CanReplace(MultiConstructor constructor, Item inactiveHandItem)
{
    if (base.Indestructable)
    {
        return CanConstructInfo.InvalidPlacement(GameStrings.CannotMergeIMergeable.AsString(ToTooltip()));
    }
    MultiMergeConstructor multiMergeConstructor = constructor as MultiMergeConstructor;
    if (multiMergeConstructor == null)
    {
        return CanConstructInfo.InvalidPlacement(GameStrings.CannotMergeIMergeable.AsString(ToTooltip()));
    }
    Grid3[] array = (Grid3[])GridBounds.GetLocalSmallGrid(base.ThingTransformPosition, base.ThingTransformRotation);
    foreach (Grid3 localGrid in array)
    {
        SmallCell smallCell = base.GridController.GetSmallCell(localGrid);
        if (smallCell == null || !smallCell.Cable)
        {
            continue;
        }
        if (inactiveHandItem == null)
        {
            return CanConstructInfo.InvalidPlacement(GameStrings.MergeRequiresTool.AsString(multiMergeConstructor.ToolExit.DisplayName));
        }
        if (multiMergeConstructor.ToolExit.PrefabHash == inactiveHandItem.PrefabHash || (inactiveHandItem.ReplacementOf != null && multiMergeConstructor.ToolExit.PrefabHash == inactiveHandItem.ReplacementOf.PrefabHash))
        {
            if (WillMergeWhenPlaced())
            {
                Cable.OnMerge?.Invoke(this);
            }
            if (smallCell.Cable.CableType != CableType)
            {
                return CanConstructInfo.InvalidPlacement(GameStrings.CannotMergeIMergeableOfDifferentType.AsString(smallCell.Cable.DisplayName));
            }
            return CanConstructInfo.ValidPlacement;
        }
        return CanConstructInfo.InvalidPlacement(GameStrings.MergeRequiresTool.AsString(multiMergeConstructor.ToolExit.DisplayName));
    }
    return CanConstructInfo.ValidPlacement;
}
```

`Cable.WillMergeWhenPlaced` (392674-392694): returns true only when the occupied cell holds a cable of the same `CableType` and neither side blocks merging:

```csharp
public bool WillMergeWhenPlaced()
{
    if (BlockMergeWithOtherCables)
    {
        return false;
    }
    Grid3[] array = (Grid3[])GridBounds.GetLocalSmallGrid(base.ThingTransformPosition, base.ThingTransformRotation);
    foreach (Grid3 localGrid in array)
    {
        SmallCell smallCell = base.GridController.GetSmallCell(localGrid);
        if (smallCell != null && smallCell.Cable != null)
        {
            if (smallCell.Cable.CableType == CableType)
            {
                return !BlockMergeWithOtherCables;
            }
            return false;
        }
    }
    return false;
}
```

Notes:

- `Cable.OnMerge` is `public static event OnMergeEvent OnMerge` (declared 392391, raised at 392659-392662). It is a CURSOR-SIDE signal: it fires during preview validation (every frame the merge preview is valid), not on actual merge execution. Do not treat it as "a merge happened".
- The cursor gate that consumes these is the per-frame loop in `InventoryManager` (288665-288726): `flag2 = CanConstruct().CanConstruct && (cursor is IGridMergeable ? CanReplace(...).CanConstruct : true)` gates the primary click and `UsePrimaryComplete()`. See [StructurePlacementValidation](../GameSystems/StructurePlacementValidation.md) for the verbatim.
- Same-tier stacking consequence: for a cable ghost over an existing SAME-tier cable (neither `BlockMergeWithOtherCables` flag set), `_IsCollision` returns false, so `SmallGrid.CanConstruct` PASSES; the click is blocked only by the `CanReplace` arm (which demands the merge tool). A programmatic caller that honors `CanConstruct` but skips `CanReplace` can therefore stack same-tier cables into one cell; see [SmallCell](./SmallCell.md), "Stacked pairs", for the resulting silent misbehavior. For a DIFFERENT-tier cable, `_IsCollision` returns true and `CanConstruct` fails with `PlacementBlockedBySmallGrid`.

**Consequence for a "voltage tier" mod**: today heavy and normal cables can sit grid-adjacent (they just do not fuse meshes) and they DO join the same `CableNetwork` because `OnRegistered` calls `CableNetwork.Merge(CableNetwork.ConnectedNetworks(this))` and the merge path ignores `CableType`. To make tiers electrically distinct a mod must either (a) reject the placement at build time via a `Cable.CanConstruct` / `Device.CanConstruct` postfix (clean, player-visible message, client-side only -- see [StructurePlacementValidation](../GameSystems/StructurePlacementValidation.md)), or (b) sever the mixed network after the fact in an `OnRegistered` postfix / `OnFinishedLoad` pass (uglier, needed anyway for migrating saves that already have a mixed junction). The interception-point analysis for option (b) is in "Registration-time guard" below.

## Placement / registration: OnRegistered, CanConstruct, CanDeconstruct
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

`Cable.OnRegistered` (392523-392538), verbatim; the merge is gated on `GameState != Loading && RunSimulation`:

```csharp
public override void OnRegistered(Cell cell)
{
    if (GameManager.GameState != GameState.Loading && GameManager.RunSimulation)
    {
        CableNetwork cableNetwork = CableNetwork.Merge(CableNetwork.ConnectedNetworks(this));
        if (cableNetwork != null)
        {
            cableNetwork.Add(this);
        }
        else
        {
            new CableNetwork(this);
        }
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

`OnRegistered` does **no tier validation**. `CanConstruct` (392625-392632) already overrides the base (umbilical check) -- this is the natural extension point for a build-time tier gate, since `Cable.CanConstruct` is consulted every frame by the placement preview (`InventoryManager` placement loop calls `ConstructionCursor.CanConstruct()` and gates the left-click on the result; see [StructurePlacementValidation](../GameSystems/StructurePlacementValidation.md)). `CanDeconstruct` (392393-392405) refuses if `AttachedDevices` is non-empty.

`OnRegistered` is reached from `GridController.AddSmallGridStructure` (206899) at the tail of the generic spawn-to-grid chain (`Thing.Create` -> `Referencable.RegisterNew` / `RegisterAs` -> `Structure.OnAssignedReference` -> `GridController.Register` -> `AddSmallGridStructure`; full chain on [StructureRegistration](../GameSystems/StructureRegistration.md)). The `cell` argument is null for cables (non-DualRegister small grids). Object-state validity at the moment `Cable.OnRegistered` runs (before its `base.OnRegistered(cell)` call):

- The Transform exists and is positioned (Instantiate at world position; `Register` re-snapped `ThingTransformPosition` at 206471).
- `RegisteredLocalGrid`, `RegisteredPosition`, `RegisteredRotation` are set (`OnAssignedReference` 314186, `Register` 206471-206473).
- The cable IS in its SmallCell(s); the `SmallCell` back-pointer is set (206876-206887). The cell writes happen in `AddSmallGridStructure` BEFORE `OnRegistered`; nothing in the `OnRegistered` chain writes SmallCell state (validated 2026-07-14, see the Verification history on [CableNetwork](./CableNetwork.md)).
- OpenEnds have `CacheTransformUp()` done (206895-206898) but `Connection.Initialize()` has NOT yet run (that happens in `SmallGrid.OnRegistered` at 312292-312295, i.e. after the merge).
- `Thing.Cell`, `Thing.Position`, `Thing.WorldGrid` are NOT yet set by `Thing.OnRegistered` (`WorldGrid` is INVALID until 319765). Any hook logic before `base.OnRegistered` must use `ThingTransformPosition` / `RegisteredLocalGrid`, not `Position` / `WorldGrid`.
- `CableNetwork` property: null on fresh placement (the merge assigns it via `Add`); non-null on the save-load path (`DeserializeSave` already added it) but the merge is skipped during Loading anyway.

### Save load and multiplayer join paths
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

`Cable.SerializeSave` writes `CableSaveSaveData.CableNetworkId` (392420-392443). `Cable.DeserializeSave` (392427-392434), verbatim (join-by-id, `?? new CableNetwork(id)`):

```csharp
public override void DeserializeSave(ThingSaveData savedData)
{
    base.DeserializeSave(savedData);
    if (savedData is CableSaveSaveData { CableNetworkId: var cableNetworkId })
    {
        (Referencable.Find<CableNetwork>(cableNetworkId) ?? new CableNetwork(cableNetworkId)).Add(this);
    }
}
```

`Cable.SerializeOnJoin` / `DeserializeOnJoin` (392445-392462), verbatim (the client re-merges locally when NOT in the initial Joining handshake):

```csharp
public override void SerializeOnJoin(RocketBinaryWriter writer)
{
    base.SerializeOnJoin(writer);
    writer.WriteInt64(CableNetworkId);
}

public override void DeserializeOnJoin(RocketBinaryReader reader)
{
    base.DeserializeOnJoin(reader);
    CableNetworkId = reader.ReadInt64();
    CableNetwork cableNetwork = Referencable.Find<CableNetwork>(CableNetworkId);
    CableNetwork cableNetwork2 = cableNetwork;
    if (GameManager.GameState != GameState.Joining)
    {
        cableNetwork2 = CableNetwork.Merge(CableNetwork.ConnectedNetworks(this)) ?? cableNetwork;
    }
    cableNetwork2.Add(this);
}
```

Both paths have **no `CableType` check**, so a save (or a client join) that already has a heavy-to-normal junction silently rebuilds it as one network. On a remote client, a newly placed cable arrives as: `Thing.DeserializeNew` -> `Thing.Create` (`RegisterAs` with the server id; `OnAssignedReference` -> `Register` -> `AddSmallGridStructure` -> `Cable.OnRegistered`, whose merge body is SKIPPED because `GameManager.RunSimulation` is false on a client) -> `Cable.DeserializeOnJoin` (GameState is Running, so the client re-merges locally). This matches the "merge is not authoritative" analysis on [CableNetwork](./CableNetwork.md).

### Which thread calls what
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

- `Cable.OnRegistered`: main thread, always. Callers: `GridController.AddSmallGridStructure` (206899) via `Register` (206469) via `Structure.OnAssignedReference` (314204) via `Referencable.RegisterNew` / `RegisterAs` via `Thing.Create` (`UnityEngine.Object.Instantiate` forces main thread). Higher-level flows: cursor build (`Constructor.SpawnConstruct` on the simulation side), programmatic spawn (any `Thing.Create` / `OnServer.Create`), save load (Things created during Loading; merge skipped by the `GameState != Loading` gate), multiplayer client spawn (`Thing.DeserializeNew` -> `RegisterAs`; merge skipped by the `RunSimulation` gate, client merge happens in `DeserializeOnJoin` instead), `Break()` wreckage spawn (`Constructor.SpawnConstruct(instance)` inside `Break`, main thread because `Break` self-marshals).
- `Cable.DeserializeSave`: main thread, during world load (XmlSaveLoad), after the Thing exists. `GameManager.GameState == GameState.Loading` at that time.
- `Cable.DeserializeOnJoin`: main thread, two flows: initial join bulk (GameState Joining: accept the server network id verbatim; the static `CableNetwork.DeserializeOnJoin` at 271295-271318 pre-creates every network by id) and runtime new-thing delivery (GameState Running: local re-merge).
- The power tick reads network membership from the UniTask ThreadPool worker, but only via a per-tick snapshot under lock; see [CableNetwork](./CableNetwork.md), "Membership mutators".

## Registration-time guard: interception points and destroy-from-hook mechanics
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

Candidate hooks for a mod that wants to refuse (or observe) a wrong-tier cable at registration time, with what happens if the mod destroys/deconstructs the cable from inside each:

- **`Cable.OnRegistered` postfix (RECOMMENDED refusal point).** At postfix time the merge is done and `__instance.CableNetwork` is the (possibly mixed) merged network; the cable is fully registered; neighbors have been notified. Calling `OnServer.Destroy(__instance)` here does NOT synchronously mutate anything: it calls `thing.GameObject.DestroyGameObject()` (39914-39926) and Unity defers the actual `Cable.OnDestroy` to end of frame. `Cable.OnDestroy` (392565-392586) then snapshots neighbors, `CableNetwork?.Remove(this)`, `base.OnDestroy()` (grid deregistration and `DestroyToSend` replication), then per-neighbor `RebuildCableNetworkServer`, which re-partitions the merged network back into per-tier islands. No collection is being iterated at postfix time, so no collection-modification hazard; the only transient is that the mixed network exists for the remainder of the frame and can be snapshotted by one power tick (the tick tolerates dying members; a demand-gated burn mod may want to skip networks containing a `BeingDestroyed` cable for that tick). Spawning the refund kit inside the same postfix is safe: `OnServer.Create<Item>` runs `Thing.Create` on the main thread and replicates via `NewToSend`.
- **`Cable.OnRegistered` prefix (refuse before the merge).** The cable IS already in its SmallCell (the cell writes precede `OnRegistered`, see above), so tier inspection of neighbors is possible via `GridController.GetSmallCell` over adjacent grids or `FillConnected<Cable>` (main thread, safe). Skipping the vanilla body (prefix returns false) also skips `base.OnRegistered(cell)`, leaving `Thing.Cell` / `Position` / `WorldGrid` unset and skipping neighbor notifications while the object is still in the grid cells and `AllStructuresPool`; the object then needs an immediate `OnServer.Destroy` and its `OnDestroy` -> `CableNetwork?.Remove` is a no-op (network never assigned) and grid deregistration still runs. Workable but leaves a one-frame half-initialized object; the postfix route produces strictly more vanilla-shaped state transitions.
- **`CableNetwork.Add(Cable)` prefix (refuse membership).** HAZARDOUS. `Merge(CableNetwork oldNetwork)` iterates `oldNetwork.CableList` in reverse and relies on `Add` moving each cable out; it then unconditionally runs `oldNetwork.CableList.Clear()` and `oldNetwork.RefreshNetwork()`. If a prefix skips `Add` for a cable, that cable keeps `CableNetwork` pointing at `oldNetwork`, which is then cleared and (if emptied) deregistered: the cable ends up holding a dangling network reference with a stale `CableNetworkId`, invisible to `AllCableNetworks`. Also note `CableNetwork.Remove` (reached from `Add` when the cable was on another network) dereferences `cable.SmallCell.Device` WITHOUT a null-conditional (271102): calling `Remove` on a cable that is not (or no longer) in a SmallCell throws NullReferenceException. Do not refuse here.
- **`CableNetwork.Merge(List<CableNetwork>)` prefix/postfix.** Fires only when a cable bridges 2+ existing networks (list.Count > 1 for a real merge); does NOT fire for a lone cable extending one network (`Add` without `Merge` doing work) nor a first cable (`new CableNetwork(cable)`). Fine for observing "a merge happened" but incomplete as a mixed-tier gate: a wrong-tier cable extending a single same-network run never produces a multi-network merge.
- **`Cable.OnPowerNetworkChanged` instance event (cheap observation signal).** Raised by the `CableNetwork` property setter (392370-392387, event declared 392389) on EVERY reassignment, so it fires whenever a cable is added to or removed from a network, including merge-driven moves. A mod can subscribe per instance to observe mixed-tier network formation without patching anything.
- **`SmallGrid.OnGridAdjacentPlaced` / `OnNeighborPlaced` overrides or patches.** Fire on existing neighbors when a new small-grid object registers (312309-312350). Observation-only candidates; they run before the new thing's `Thing.OnRegistered` completes.
- **Cursor-side gate: `Cable.CanConstruct` postfix** (client-preview only; see [StructurePlacementValidation](../GameSystems/StructurePlacementValidation.md)). Blocks the click with a red tooltip message but does not protect against programmatic spawns, ZoopMod-style `UsePrimaryComplete` bypass, save-load, or remote-client construction messages; a server-side registration guard remains necessary.
- **Load path.** A `DeserializeSave` postfix could flag mixed junctions but destroying things mid-load is risky (Loading-state gates differ, clients get things via join instead); prefer a one-shot pass after load (e.g. `OnFinishedLoad`-adjacent, or a first-tick scan) if save migration is wanted. The existing note above ("sever the mixed network after the fact in an `OnRegistered` postfix / `OnFinishedLoad` pass") stands.

Multiplayer note for any registration-time destroy: the placement already replicated (the new cable rides `Thing.NewToSend`), so the refusing server also replicates the removal automatically via `Thing.DestroyToSend` (`Thing.OnDestroy` 320984-320999 adds `DestroyEvent.Create(this)` when `IsServer && HasClients`), and the per-neighbor splits ride `RebuildCableNetworkEvent`. The client will have run its local `DeserializeOnJoin` merge before the destroy arrives; the destroy plus rebuild events then converge it. Ordering across the per-tick packet is the same SyncList stream already documented on [CableNetwork](./CableNetwork.md).

Kit identity for a refuse-and-refund: the coil item a cable refunds is serialized prefab data, `BuildStates[0].Tool.ToolEntry` with quantity `EntryQuantity` (read them from the live instance rather than hardcoding prefab names; that automatically matches variant cost). The full deconstruct-to-kit path (`Structure.AttackWith` tool branch, `StructureDestroyed`, `ToolUse.SpawnItem`) is on [Structure](./Structure.md).

## Cable rupture: Break()
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

(`Break()` verbatim unchanged at 0.2.6403.27689, lines 392470-392484; `AttackWith` welding-torch path 392486-392521.)

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

0.2.6403.27689 line refs for the vanilla burn-selection chain: `PowerTick.CacheState` (271842-271855) computes `_actual = Mathf.Min(Potential, Required)`; `GetBreakableCables` (271869-271879) flags `cable.MaxVoltage < _actual`; `BreakSingleCable` (271892-271901) picks one and calls `cable.Break()`; `ApplyState` order (271903-271915) is fuses first, then cables, then devices. This is the demand-gated burn pattern for a mod to mirror for a wrong-tier burn (it runs on the worker thread; `Break()` self-marshals; Power Grid Plus's `VoltageTierEnforcer` already follows it).

## Network split on destruction: OnDestroy -> RebuildCableNetworkServer
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

`Break()` does **not** recompute network topology. It spawns the wreckage and destroys the GameObject (`OnServer.Destroy(this)` -> `UnityEngine.Object.Destroy`, which Unity defers to end-of-frame); it never touches `CableList` / `DeviceList` membership. The actual network re-partition is parented to the Unity destroy callback `Cable.OnDestroy()` (decompile lines 392565-392586 at 0.2.6403.27689; the old `ConnectedCables()`-based body is superseded by this Span-based one):

```csharp
public override void OnDestroy()
{
    if (Singleton<GameManager>.IsQuitting || IsCursor || GameManager.GameState == GameState.None)
    {
        return;
    }
    Span<SmallCellRef> span = stackalloc SmallCellRef[32];
    int count = 0;
    FillConnected<Cable>(span, ref count);                   // snapshot former neighbours BEFORE removal
    CableNetwork cableNetwork = CableNetwork;
    CableNetwork?.Remove(this);                              // drop this cable from its network
    base.OnDestroy();
    if (GameManager.RunSimulation && cableNetwork != null)
    {
        Span<SmallCellRef> span2 = span;
        Span<SmallCellRef> span3 = span2.Slice(0, count);
        for (int i = 0; i < span3.Length; i++)
        {
            CableNetwork.RebuildCableNetworkServer(span3[i]);   // flood-fill each former neighbour into a fresh network
        }
    }
}
```

The neighbour snapshot is now a stackalloc `SmallCellRef` span (no shared static buffer; the old `FoundCables` list is gone from the game entirely), and the per-neighbour call goes through the new `RebuildCableNetworkServer(SmallCellRef)` overload (271203-271206), which lazily re-resolves the ref (`cableRef.Get<Cable>()`) before delegating to `RebuildCableNetworkServer(Cable)` (271208-271220). So the full destroy-to-split chain is `Break()` -> `OnServer.Destroy` -> (Unity end-of-frame) `Object.Destroy` -> `Cable.OnDestroy()` -> per-neighbour `CableNetwork.RebuildCableNetworkServer` (the BFS documented on [CableNetwork](./CableNetwork.md), "Split is authoritative; merge is not"). This `OnDestroy` is the **only** server-side caller of `RebuildCableNetworkServer`; the parallel `RebuildCableNetworkClient` (271222-271234) is the wire-applied variant driven by `RebuildCableNetworkEvent` (272397).

**Timing -- the split is NOT lazy, but it is deferred.** The re-partition runs synchronously inside `OnDestroy`, but `OnDestroy` only fires when Unity tears the GameObject down at end-of-frame, one or more frames after `Break()` was called. When `Break()` is reached from the power tick (worker thread, `ThreadedManager.IsThread` true), `Break()` first marshals itself to the main thread via `UnityMainThreadDispatcher` (so even the `Object.Destroy` is a frame later), and only then does the destroy + `OnDestroy` split land. Net effect for a mod that burns a cable during the power tick: the post-split topology is observable no earlier than the NEXT tick's network enumeration, never the same tick. This is the mechanism behind Power Grid Plus's per-network burn cooldown (`VoltageTierEnforcer`, 4 ticks) which bridges the marshal-to-split latency so the same junction is not burned twice before the split takes effect.

**Separability of the recompute from the GameObject teardown.** The membership mutation itself (`CableNetwork.Remove` / `RebuildCableNetworkServer` / `RebuildNetwork` / `Add` / `RemoveDevice`) is pure managed list/dict/HashSet work under `lock`, with no `UnityEngine.Object` lifecycle call (see [CableNetwork](./CableNetwork.md)). The ONLY main-thread coupling inside the recompute is the `FillConnected` walk -- run once at the top of `OnDestroy` and again per dequeued cable inside `RebuildNetwork`'s BFS (271147-271201) -- reading `openEnd.Transform.position` (a Unity `Transform` getter, main-thread-only; FillConnected body at 312808 / 312856 / 312904). At 0.2.6403 the buffers are per-call `stackalloc Span<SmallCellRef>`, so the shared-static-buffer hazard of the removed `ConnectedCables()` / `FoundCables` pair is gone; the live `Transform.position` read is the whole remaining coupling. The same coordinate is cached in `Connection.LocalGrid` (populated by `SetGrids()` from the same `Transform.position`), and the game's own off-thread idiom uses the cached `LocalGrid` (`Connection.Initialize()` early-returns the cached `_isInitialized` value without touching the Transform when `ThreadedManager.IsThread`, 311918-311932). So a worker-thread synchronous split is blocked ONLY by that one live `Transform.position` read, not by any GameObject-lifecycle dependency. A mod that wanted the split to land in-tick would have to (a) traverse connectivity via cached `Connection.GetLocalGrid()` + `SmallCell.Get<Cable>(grid, openEnd)` into a local list rather than call vanilla `FillConnected` / `RebuildCableNetworkServer` directly, (b) suppress the vanilla `OnDestroy` rebuild that still fires at end-of-frame to avoid a double-split, and (c) replicate `RebuildCableNetworkEvent` to clients itself (vanilla's server-allocated network ids would otherwise be the only authoritative split, and re-implementing it forfeits that). The teardown and the recompute are cleanly separable in principle; the cost is re-implementing the connectivity walk and the MP replication, not just calling one vanilla method.

## Wreckage: CableRuptured
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

The structure spawned by `Break()` is `Cable.RupturedPrefab`, typed as `CableRuptured` (decompile line 392346: `public CableRuptured RupturedPrefab;`). The class itself (full body, 392848-392881, verbatim-unchanged from the 0.2.6228.27061 excerpt):

```csharp
public class CableRuptured : SmallGrid                                      // line 392848
{
    public static List<CableRuptured> AllCableRuptured = new List<CableRuptured>();
    public static readonly int CableSparkHash = Animator.StringToHash("CableSpark");
    private static readonly int NUMBER_OF_SPARKS = 100;

    public override void Awake()
    {
        base.Awake();
        if (GameManager.GameState == GameState.Running && !IsCursor && !GameManager.IsBatchMode)
        {
            ParticleSystem.EmitParams emitParams = new ParticleSystem.EmitParams { position = base.ThingTransformPosition };
            WorldManager.Instance.Sparker.Emit(emitParams, NUMBER_OF_SPARKS);
            Singleton<AudioManager>.Instance.PlayAudioClipsData(CableSparkHash, base.ThingTransformPosition);
        }
    }

    public override void OnRegistered(Cell cell)
    {
        base.OnRegistered(cell);
        AllCableRuptured.Add(this);
    }

    public override void OnDeregistered()
    {
        base.OnDeregistered();
        AllCableRuptured.Remove(this);
    }
}
```

`CableRuptured` is in `Assets.Scripts.Objects.Electrical` (namespace block at 389692, same as `Cable`). It is a `SmallGrid`, not a `Cable` -- it does NOT participate in a `CableNetwork` and does NOT carry over the original cable's `CableType` / `MaxVoltage`. It exists purely as a one-cell visual+audio wreckage marker that the player has to weld with a welding torch (`AttackWith` on the base) to clear.

Implication for the spawn-sequence inside `Break()`: `Constructor.SpawnConstruct(instance)` is called synchronously after `OnServer.Destroy(this)`. Inside `SpawnConstruct` -> `Thing.Create` -> `OnRegistered(cell)`, the new wreckage is registered at the cable's old cell BEFORE `Break()` returns. So any postfix on `Cable.Break()` or `CableRuptured.OnRegistered` runs with the wreckage already in `AllCableRuptured` and queryable via `GridController.GetSmallCell(cell).Other` (the wreckage occupies a cell's `Other` slot because it is `SmallGrid` not `Cable`/`Device`/`Pipe`/`Chute`/`Rail`).

`CableRuptured` does **not override `GetPassiveTooltip`** (nothing in its body, 392848-392881), but it does NOT inherit the base `Thing.GetPassiveTooltip`: the chain is `CableRuptured : SmallGrid` (392848) -> `SmallGrid : Structure` (312025, declares no `GetPassiveTooltip` anywhere; whole-decompile census has no occurrence between 309684 and 314439) -> `Structure : Thing` (313704), and `Structure` DOES override it. A wreckage body hover therefore dispatches to `Structure.GetPassiveTooltip` (314440), the damage / build-state tooltip:

```csharp
public override PassiveTooltip GetPassiveTooltip(Collider hitCollider)      // line 314440
{
    bool flag = ShowBuildTooltip();
    bool flag2 = ShowDeconstructTooltip();
    bool flag3 = ShowRepairTooltip();
    if (DamageState.Total <= 0f && !flag && !flag2 && !flag3)
    {
        return base.GetPassiveTooltip(hitCollider);       // the ONLY route to Thing's base
    }
    PassiveTooltip passiveTooltip = new PassiveTooltip(true);
    passiveTooltip.Title = DisplayName;
    passiveTooltip.Extended = GetExtendedText().ToString();
    ...                                                   // ConstructString / DeconstructString / RepairString fill, 314453-314470
}
```

The all-empty base `Thing.GetPassiveTooltip` (319731-319734, `return new PassiveTooltip(true);`) runs only through that fall-through branch. A mod that wants to annotate the wreckage's hover tooltip with a reason therefore patches `Structure.GetPassiveTooltip` with a postfix filtered to `__instance is CableRuptured` and mutates `__result.Extended` (or `__result.State` for prominence); a postfix on `Thing.GetPassiveTooltip` never fires for any hover where `Structure` answers without calling base (any damage, or any build / deconstruct / repair tooltip applying). Power Grid Plus ships this pattern (`[HarmonyPatch(typeof(Structure), nameof(Structure.GetPassiveTooltip))]` in `Mods/PowerGridPlus/PowerGridPlus/Patches/BurnReasonPatches.cs`) to differentiate cable-burn reasons (overload vs. tier-mismatch vs. device-tier-mismatch). `PassiveTooltip` is a mutable struct (307045) with public string fields `Title`, `Action`, `State`, `Extended`, `RepairString`, `DeconstructString`, `ConstructString`, `PlacementString`, `BuildStateIndexMessage` (+ a few booleans for UI hints); `GetExtendedText()` simply returns `Extended`. The full body-hover dispatch chain (`Device` -> `Structure` -> `Thing`), the electrical-family override census, the struct body, and the TextMeshPro render path live on [ElectricalInputOutput](./ElectricalInputOutput.md), "GetPassiveTooltip: body-hover tooltip resolution chain". Patch at exactly one level of the chain: derived overrides call `base.GetPassiveTooltip` and Harmony detours base calls too, so multi-level patches multi-fire (see [HarmonyBaseCallDetourMultiFire](../Patterns/HarmonyBaseCallDetourMultiFire.md)).

To attach a per-wreckage reason from the caller side (where it is known), the caller records the dying cable's cell in a `ConcurrentDictionary<Grid3, string>` (the PowerTick runs on worker threads, so plain `Dictionary` is not safe), and a postfix on `CableRuptured.OnRegistered(Cell cell)` reads + removes the entry using `__instance.LocalGrid` and stores the reason on the wreckage in a `ConditionalWeakTable<Thing, string>` sidecar.

## Verification history

- 2026-07-14 (mixed-tier guard pass): extended three sections and added one, from the mixed-tier cable network guard research pass against the 0.2.6403.27689 decompile. **CableType collision / merge gating**: replaced the partial `CanReplace` / `WillMergeWhenPlaced` excerpts with the full verbatim bodies at new refs (`_IsCollision` 392588-392603, `CanReplace` 392634-392672, `WillMergeWhenPlaced` 392674-392694); added the `CanReplace` second-cursor-arm mechanics (requires `MultiMergeConstructor` + `ToolExit` in the off hand, `MergeRequiresTool` otherwise), the static `Cable.OnMerge` event (declared 392391, raised 392659-392662, cursor-side preview signal only), the `InventoryManager` gate lines (288665-288726), and the same-tier stacking consequence (same-tier cable-over-cable passes `CanConstruct`; only the `CanReplace` arm blocks the click; programmatic callers skipping `CanReplace` can stack). **Placement / registration**: added the object-state-validity list at `Cable.OnRegistered` time (the cable IS already in its SmallCell, cell writes precede `OnRegistered` per the fresh-validator ruling recorded on [CableNetwork](./CableNetwork.md); `Position` / `WorldGrid` / `Cell` not yet set; `Connection.Initialize` not yet run), the verbatim `DeserializeSave` / `SerializeOnJoin` / `DeserializeOnJoin` bodies (392420-392462), the remote-client spawn flow, and the thread census (all registration and membership mutation main-thread). **Cable rupture**: added 0.2.6403 line refs for the vanilla burn-selection chain (`CacheState` 271842-271855 `_actual = min(Potential, Required)`, `GetBreakableCables` 271869-271879, `BreakSingleCable` 271892-271901, `ApplyState` order fuses/cables/devices 271903-271915). **NEW section "Registration-time guard: interception points and destroy-from-hook mechanics"**: the hook-point catalogue (OnRegistered postfix recommended; prefix workable but half-initialized; `CableNetwork.Add` prefix hazardous because `Merge` unconditionally `Clear()`s + `RefreshNetwork()`s the old network leaving a skipped cable with a dangling reference, and `Remove`'s unguarded `cable.SmallCell.Device` deref at 271102 can NRE; `Merge(List)` fires only for 2+ network bridges; `OnPowerNetworkChanged` instance event as a patch-free observation signal; adjacent-placed observation hooks; cursor gate limits; load-path caution), the multiplayer auto-replication note (`NewToSend` / `DestroyToSend` 320984-320999 / `RebuildCableNetworkEvent`), and the kit-identity line (`BuildStates[0].Tool.ToolEntry` x `EntryQuantity`, read from the live instance). No prior claim on this page contradicted.
- 2026-07-14: conflict on "CableRuptured hover-tooltip dispatch target", resolved via the Rule 3 fresh-validator protocol. Previous claim (Wreckage section, stamped 0.2.6228.27061): the wreckage inherits "the base `Thing.GetPassiveTooltip`" (old line 300658) and a mod annotating the hover patches `Thing.GetPassiveTooltip` filtered to `CableRuptured`. New finding (surfaced during the [ElectricalInputOutput](./ElectricalInputOutput.md) tooltip-chain pass, 2026-07-14): a `SmallGrid` subclass without its own override dispatches to `Structure.GetPassiveTooltip`. Fresh validator verdict: the new finding is correct at 0.2.6403.27689. Decisive lines in `.work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs`: `public class CableRuptured : SmallGrid` (392848) declares no `GetPassiveTooltip` in its full body (392848-392881); `public class SmallGrid : Structure, ...` (312025) declares none (whole-file `GetPassiveTooltip` census has zero occurrences between 309684 and 314439); `public class Structure : Thing` (313704) declares the override at 314440, whose only route to `Thing.GetPassiveTooltip` (319731-319734) is the fall-through `if (DamageState.Total <= 0f && !flag && !flag2 && !flag3) return base.GetPassiveTooltip(hitCollider);` (314445-314448). Whether the claim was already wrong at 0.2.6228.27061 or the game changed could not be determined: `.work/decomp/0.2.6228.27061/` no longer exists (one-version-at-a-time decomp cache), so the ruling covers the current version only. Result: rewrote the Wreckage section's tooltip paragraph around `Structure.GetPassiveTooltip` (verbatim head quoted), retargeted the mod recommendation to a `Structure.GetPassiveTooltip` postfix (matching the shipped `Mods/PowerGridPlus/PowerGridPlus/Patches/BurnReasonPatches.cs`, which patches `typeof(Structure)`), refreshed stale line refs (`RupturedPrefab` 392346, class header 392848, namespace block 389692, `PassiveTooltip` struct 307045), cross-linked [ElectricalInputOutput](./ElectricalInputOutput.md), and restamped the section. Re-verified in the same pass: the `CableRuptured` class body is verbatim-unchanged from the 0.2.6228.27061 excerpt (now 392848-392881); `RupturedPrefab` typing unchanged (392346); `Constructor.SpawnConstruct(CreateStructureInstance)` still present (295228) and `SmallCell.Other` still the blocking slot placement checks consult (148460-148474, 161157-161162), supporting the carried-over spawn-sequence paragraph.
- 2026-07-02: grid-adjacency API migration pass against the 0.2.6403.27689 decompile after the game update from 0.2.6228.27061. SUPERSEDED: `SmallGrid.ConnectedCables()` / `ConnectedCables(NetworkType)` and the static `FoundCables` buffer are REMOVED from the game (whole-decompile grep: zero hits); replaced by allocation-free Span fillers on `SmallGrid` (`FillConnected<T>(Span<SmallCellRef>, ref int)` 312804, `FillConnected(Span<SmallCellRef>, ref int)` 312852, `FillConnected<T>(NetworkType, Span<SmallCellRef>, ref int)` 312896) over the new `readonly struct SmallCellRef` (290601, BUFFER_SIZE = 32). Replaced the `Cable.OnDestroy` verbatim excerpt with the new Span-based body (392565-392586, per-neighbour `RebuildCableNetworkServer(SmallCellRef)` overload 271203) and reworked the separability discussion (shared-buffer hazard gone; the `openEnd.Transform.position` main-thread coupling remains inside FillConnected). Re-verified unchanged with new refs: class/fields (392329-392389), `_IsCollision` / `CanReplace` / `WillMergeWhenPlaced` shapes (392588-392694), `OnRegistered` (392523-392538), `CanConstruct` (392625-392632), `Break()` (392470-392484), `DeserializeSave` / `DeserializeOnJoin` rejoin paths (392427-392434 / 392451-392462), `SmallGrid.IsConnected(Connection)` (312730), `NetworkType` enum (311698), `Connection` class (311786). The wreckage section (`CableRuptured`) was not re-read this pass and keeps its 0.2.6228.27061 stamp. Mod-side note: the `FillConnected` Span signatures are not bindable from net472 mods (see [CursorAdjacencyLookup](../Patterns/CursorAdjacencyLookup.md) for the constraint and the replacement pattern).
- 2026-05-12: page created. Sourced from a phase 3 research dive (planned mod "Power Grid Plus") into `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` lines 293196-293256 and 371283-371673; verbatim excerpts of `NetworkType`/`ConnectionRole`/`Connection`, `Cable` class header + `Type` enum + `MaxVoltage`, `Cable._IsCollision`/`CanReplace`/`WillMergeWhenPlaced`, `Cable.OnRegistered`/`CanConstruct`/`Break`. The `superHeavy` tier and the `StructureCableSuperHeavyStraight3/5/10` long pieces corroborate the existing [MultiMergeConstructor](./MultiMergeConstructor.md) page. Re-Volt mod source (`RevoltTick : PowerTick`) corroborates `MaxVoltage` as the burn threshold.
- 2026-05-13: added the **Wreckage: CableRuptured** section. Sourced from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` lines 371300 (`Cable.RupturedPrefab : CableRuptured`), 371821-371851 (full `CableRuptured` class body), 288582-288610+ (`PassiveTooltip` struct), 300658 (`Thing.GetPassiveTooltip` base signature). Resolves one open question (the super-heavy coil prefab name is `ItemCableCoilSuperHeavy`, found in `$(StationeersPath)\rocketstation_Data\StreamingAssets\Data\electronics.xml` while wiring NEW-2's cost overlay -- entry already shipped in `Mods/PowerGridPlus/PowerGridPlus/GameData/cable-recipes.xml`). Sourced from a Power Grid Plus burn-reason-tooltip research pass.
- 2026-05-22: filled in the per-tier `MaxVoltage` values in the **Class hierarchy and key fields** section: `normal = 5000 W`, `heavy = 100000 W`, `superHeavy = 500000 W`. Extracted from `$(StationeersPath)\rocketstation_Data\resources.assets` via UnityPy. Type trees are stripped from this build's serialized files, so a Mono `TypeTreeGenerator` (UnityPy's, requires the `TypeTreeGeneratorAPI` native extension) was attached to the UnityPy environment over `$(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll` plus the other 144 DLLs in `Managed/`, and `obj.read_typetree()` resolved each Cable MonoBehaviour's per-script tree on demand. 29 cable prefabs scanned (10 normal, 8 heavy, 11 superHeavy); all live in `resources.assets`; `MaxVoltage` is constant within each tier; `normal = 5000` matches the C# field default in the decompile as a sanity check. Resolves the matching Open Question (removed). Restamped the **Class hierarchy and key fields** section.
- 2026-06-13: added the **Network split on destruction: OnDestroy -> RebuildCableNetworkServer** section. Sourced from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` line 371541-371559 (`Cable.OnDestroy` verbatim) plus the existing `CableNetwork` BFS excerpts. Fills the gap in the **Cable rupture: Break()** section, which described the GameObject teardown but never traced where the network re-partition happens. Records that `Cable.OnDestroy` (not `Break()`) is the sole server-side caller of `RebuildCableNetworkServer`, that the split is synchronous-inside-OnDestroy but deferred to Unity end-of-frame (so a power-tick burn's split lands no earlier than the next tick -- the mechanism behind Power Grid Plus's per-network burn cooldown), and that the topology recompute is separable from the GameObject teardown (pure-managed except the one `Transform.position` read in `ConnectedCables` / `RebuildNetwork`, for which the game itself uses the cached `Connection.LocalGrid` off-thread). Produced while evaluating whether Power Grid Plus could make a wrong-tier burn's network split land in the same tick.
- 2026-05-26: resolved the cable-coil-prefab-names open question. `$(StationeersPath)\rocketstation_Data\StreamingAssets\Data\electronics.xml` declares exactly three cable-coil printer recipes by `<PrefabName>`: `ItemCableCoil` (normal), `ItemCableCoilHeavy` (heavy), `ItemCableCoilSuperHeavy` (super-heavy). No separate `ItemCableCoilInsulated` exists, in the recipe XML or the decompile (`grep -nE "InsulatedCable|CableInsulated|StructureCable.*Insulated"` against `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` returns no matches). The "insulated cable" the player can build is a structure-side variant of the normal `Cable` family (same `Cable.Type.normal`, same `MaxVoltage = 5000 W`, no separate code path), crafted from the same `ItemCableCoil`; there is no "insulated coil" item or a special burn-immunity flag to discover. `ItemCableCoilHeavy` independently corroborated against developer save state: `<PrefabName>ItemCableCoilHeavy</PrefabName>` entries appear in every captured Luna `world.xml` (e.g. `.work/2026-05-15-luna-bug-inspect/Luna/world.xml`).

## Open questions
