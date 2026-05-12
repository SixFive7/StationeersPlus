---
title: PlacementOrientation
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-12
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Inventory.InventoryManager (placement loop, lines 1657-2797)
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Constructor
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.MultiConstructor
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Structure (SetStructureData, Rotate, save data, ~2185-2247)
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.Cable / Pipe / Piping
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.SmallGrid / SmallSingleGrid / Connection
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Util.SmartRotate (RotX/RotY/RotZ, ConnectionType, RotationsList)
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Networking.ConstructionCreationMessage
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Networking.CreateStructureInstance
  - workshop://544550/3310094883 ZoopMod.dll :: ZoopMod (SetStraightRotation / ApplyRotation / PositionSmallGridStructures)
related:
  - ../GameClasses/AllowedRotations.md
  - ../GameClasses/Structure.md
  - ../GameClasses/Constructor.md
  - ../GameClasses/InventoryManager.md
  - ../GameClasses/MultiMergeConstructor.md
  - ../GameClasses/CableNetwork.md
  - ../Protocols/ConstructionCreationMessage.md
  - ./ThirdPartyModIdentities.md
tags: [prefab, transforms, network]
---

# PlacementOrientation

End-to-end pipeline that turns player input (mouse position, R-key presses, surface raycast) into a placed `Structure` with a final `Quaternion` rotation. Documents every stage where rotation could be clamped, snapped, or rejected, and the multiplayer + save round-trip for the chosen rotation.

## Pipeline overview
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

```
Player holds Constructor / MultiConstructor / AuthoringTool
    | (every frame)
InventoryManager.NormalMode (right-click) or HandlePrimaryUse
    -> SetConstructorItemPlacement / SetMultiConstructorItemPlacement
    -> InventoryManager.PlacementMode (per-frame loop)
        - reads cursor raycast hit + surface normal
        - reads R / RotateLeft / RotateRight / RotateUp / RotateDown
        - mutates ConstructionCursor.ThingTransform.rotation directly
    -> InventoryManager.UpdatePlacement(structure)
        - derives CurrentFace from ConstructionCursor.ThingTransform.forward
        - applies AllowedRotations gate (see GameClasses/AllowedRotations.md, "Consumer 1")
        - snaps cursor world position to grid
    -> ConstructionCursor.CanConstruct() -> CanConstructInfo
        - checks grid collision and face blocking
        - DOES NOT consult AllowedRotations
    -> on left-click: Constructor.OnUsePrimary(targetLocation, targetRotation, ...)
        -> Constructor.Construct(localPosition, targetRotation, ...)
            -> new CreateStructureInstance(BuildStructure, localPosition, targetRotation, steamId)
            -> Constructor.SpawnConstruct(instance)
                if RunSimulation:
                    Thing.Create<Structure>(prefab, WorldPosition, WorldRotation, 0)
                    structure.SetStructureData(LocalRotation, ...)
                else (client without simulation):
                    new ConstructionCreationMessage(instance).SendToServer()
                    [server processes message, calls OnServer.Create + SetStructureData]
```

## Where rotation can be modified
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

| Stage | Code site | Quaternion treatment |
|---|---|---|
| R-key rotation | `InventoryManager.cs:2443-2479` | Three sibling blocks each gated on `(ConstructionCursor.RotationAxis & X) != None`. Y handles `RotateLeft`/`RotateRight` (yaw, ±90 around `Vector3.up`). X handles `RotateUp`/`RotateDown` (pitch, ±90 around `Vector3.right`, or ±180 when the cursor is `IMounted`). Z handles `RotateRollLeft`/`RotateRollRight` (roll, ±90 around `Vector3.forward`). Each `Rotate` call is `Space.World`. Direct world-space mutation of `ConstructionCursor.ThingTransform.rotation`. No clamping per axis. **A prefab whose serialized `RotationAxis = Y` cannot be pitched or rolled at the cursor stage at all** — the player can only yaw, regardless of `AllowedRotations`. |
| AllowedRotations gate | `InventoryManager.cs:1719-1739` | If the cursor's current face does not match the prefab's `AllowedRotations` mask, `CurrentFace` and `ConstructionCursor.ThingTransform.rotation` are rewritten to the nearest allowed surface. Auto-correct, not block. |
| `Structure.SnapTransform` | `Structure.cs:443-446` | `LocalGrid = base.GridController.WorldToLocalGrid(CenterPosition);` - position-only; rotation unchanged. |
| `Structure.Rotate` (post-placement R-key) | `Structure.cs:2185-2235` | When `AllowedRotations == Floor`, falls through to Y-axis only. Any other value permits multi-axis (`AngleAxis` around any vector). |
| `CreateStructureInstance` | `CreateStructureInstance.cs` ctor | Stores incoming `Quaternion targetRotation` in `LocalRotation`. `WorldRotation` is a getter returning `LocalRotation`. No clamping. |
| `Constructor.SpawnConstruct` | `Constructor.cs:32-44` | Passes `WorldRotation` to `Thing.Create<Structure>` and `LocalRotation` to `SetStructureData` verbatim. |
| `Structure.SetStructureData` | `Structure.cs:2240-2247` | `Direction = localRotation;` - stores the Quaternion as a `[ReadOnly]` public field. No clamping. |
| `Thing.Create<T>` | `Thing.cs:2320+` | `UnityEngine.Object.Instantiate(prefab, worldPosition, worldRotation)` - Unity-side application of the Quaternion to the new GameObject's transform. No game-side clamping. |

## CanConstruct does NOT consult AllowedRotations
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

Source: `Structure.cs:1230-1280`.

`Structure.CanConstruct()` walks every cell the structure would occupy via `GridBounds.GetLocalGrid(ThingTransformPosition, ThingTransformRotation)` and calls `CanConstructCell(cell, position)` for each. `CanConstructCell` checks:

- `StructureCollisionType == BlockGrid` returns invalid if any structure already occupies the cell.
- For each existing structure in the cell, branches on `StructureCollisionType`: `BlockGrid` -> reject, `BlockFace` -> reject if face position match within 0.01 m sq distance, otherwise allow.

It does NOT consult `AllowedRotations`. The orientation gate exists exclusively in the placement cursor (`UpdatePlacement`). Consequence: a Harmony patch that bypasses `UpdatePlacement` and feeds an arbitrary Quaternion to `Constructor.Construct` will succeed - `SetStructureData` accepts any Quaternion verbatim.

## Quaternion round-trip durability
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

| Channel | Encoding | Lossy? |
|---|---|---|
| Multiplayer placement (`ConstructionCreationMessage`) | `WriteQuaternion` / `ReadQuaternion` (4 floats, 16 bytes) | No. |
| Late-join replication (`Structure.SerializeOnJoin` / `DeserializeOnJoin`) | `WriteQuaternion(Direction)` then `ReadQuaternion()` into `Direction` | No. The `Direction` field carries the placement Quaternion across the join boundary. |
| Per-tick delta sync (`Thing.WriteTransform`) | `WriteVector3(RegisteredPosition); WriteQuaternion(RegisteredRotation);` | No (when the transform-update flag is set). |
| Save (`StructureSaveData.RegisteredWorldRotation`) | XML-serialized `Quaternion` (4 floats, full precision) | No. |
| Load (`Thing.DeserializeSave`) | Reads `RegisteredWorldRotation` and writes `ThingTransform.rotation = ...` | No. |

Source quotes:

`ConstructionCreationMessage.cs`:

```csharp
public override void Deserialize(RocketBinaryReader reader)
{
    PrefabHash = reader.ReadInt32();
    WorldPosition = reader.ReadVector3();
    WorldRotation = reader.ReadQuaternion();
    LocalRotation = reader.ReadQuaternion();
    OwnerClientId = reader.ReadUInt64();
    LocalGrid = reader.ReadGrid3();
    CustomColorIndex = reader.ReadInt32();
}
```

`Structure.cs:493-507` (SerializeOnJoin / DeserializeOnJoin):

```csharp
public override void SerializeOnJoin(RocketBinaryWriter writer)
{
    base.SerializeOnJoin(writer);
    writer.WriteByte((byte)CurrentBuildStateIndex);
    writer.WriteGrid3(RegisteredLocalGrid);
    writer.WriteQuaternion(Direction);
}

public override void DeserializeOnJoin(RocketBinaryReader reader)
{
    base.DeserializeOnJoin(reader);
    byte buildStateIndex = reader.ReadByte();
    RegisteredLocalGrid = reader.ReadGrid3();
    Direction = reader.ReadQuaternion();
    UpdateBuildStateAndVisualizer(buildStateIndex);
}
```

`StructureSaveData.cs`:

```csharp
[XmlInclude(typeof(ThingSaveData))]
public class StructureSaveData : ThingSaveData
{
    [XmlElement] public int CurrentBuildState;
    [XmlElement] public long MothershipReferenceId;
    [XmlElement] public bool HasSpawnedWreckage;
    [XmlElement] public Vector3 RegisteredWorldPosition;
    [XmlElement] public Quaternion RegisteredWorldRotation;
}
```

## Direction vs RegisteredRotation vs ThingTransform.rotation
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

Three sources of "the structure's rotation" exist on a `Structure`. They are not the same field but they are reconciled at well-known points.

| Source | Storage | Purpose |
|---|---|---|
| `ThingTransform.rotation` | Unity GameObject transform (real, live) | The visual / physics rotation. Read by everything that needs world-space orientation. |
| `Thing.RegisteredRotation` | Backing field on `Thing` | Snapshot of the rotation at the time of grid registration. Saved in `StructureSaveData.RegisteredWorldRotation`. |
| `Structure.Direction` | Public `[ReadOnly] Quaternion` field at `Structure.cs:163` | Mirrored network-sync field. Carried verbatim in `SerializeOnJoin` / `DeserializeOnJoin`. Reassigned from `ThingTransform.rotation` in `OnAssignedReference` and `RebuildGridState`. Set explicitly to the placement Quaternion in `SetStructureData`. |

Reconciliation points (verbatim):

`Structure.cs:511-515` (OnAssignedReference):
```csharp
public override void OnAssignedReference()
{
    base.OnAssignedReference();
    Direction = ThingTransform.rotation;
    RegisteredLocalGrid = new Grid3(base.ThingTransformPosition);
    ...
```

`Structure.cs:545-549` (RebuildGridState):
```csharp
public virtual void RebuildGridState()
{
    Direction = ThingTransform.rotation;
    RegisteredLocalGrid = new Grid3(base.ThingTransformPosition);
    base.ThingTransformPosition = RegisteredLocalGrid.ToVector3();
    base.RegisteredPosition = base.ThingTransformPosition;
    base.RegisteredRotation = base.ThingTransformRotation;
    ...
```

`Structure.cs:2240-2247` (SetStructureData):
```csharp
public void SetStructureData(Quaternion localRotation, ulong ownerClientId, Grid3 localGrid, int customColourIndex)
{
    Direction = localRotation;
    base.OwnerClientId = ownerClientId;
    RegisteredLocalGrid = localGrid;
    ...
}
```

`Structure.cs:680-687` (DeserializeSave - what is RESTORED from save):
```csharp
public override void DeserializeSave(ThingSaveData savedData)
{
    base.DeserializeSave(savedData);
    if (savedData is StructureSaveData structureSaveData)
    {
        CurrentBuildStateIndex = structureSaveData.CurrentBuildState;
        HasSpawnedWreckage = structureSaveData.HasSpawnedWreckage;
    }
    UpdateStateVisualizer();
}
```

`DeserializeSave` does NOT explicitly restore `Direction`. The `ThingTransform.rotation` is restored by `Thing.DeserializeSave` (the base call) from `RegisteredWorldRotation`. After that, when the structure is registered into the grid (`OnAssignedReference` or `RebuildGridState` fires later in the load pipeline), `Direction` is reassigned from `ThingTransform.rotation`. Therefore `Direction` ends up correctly aligned with the placement Quaternion at runtime IF the registration path runs.

Open question: whether `OnAssignedReference` / `RebuildGridState` fires in every load path (fresh save, late join, structure swapped between grids). See "Open questions" below.

## SmartRotate IndexOutOfRange under RotationAxis = All
<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

A prefab that implements `ISmartRotatable` (every `ElectricalInputOutput` subclass, including `PowerTransmitter` and `PowerReceiver`) reaches `Util.SmartRotate._GetNext` from `InventoryManager.PlacementMode` on every placement-mode frame: once for scroll-wheel cycling and once for the autoplace-on-enter branch. `_GetNext` calls `rotatable.GetOpenEndLocationPermutation(offset)` first thing, which calls `Util.Permutation.Permute(int[] array)` on the prefab's serialized `OpenEndsPermutation`. The permutation cycle indices are sourced from `SmartRotate.OrientationLookup[connectionType]`, sized to the connection geometry implied by the prefab's `RotationAxis` value at vanilla bake time.

When a mod flips `RotationAxis` from a single axis (e.g. `Y`) to `All` to enable multi-axis cursor R-key handling, the SmartRotate dispatcher selects a larger orientation set, but the prefab's `OpenEndsPermutation` array is still sized for the original single-axis configuration. `Permute` indexes off the end of the array and throws `IndexOutOfRangeException`, once per frame, for as long as the cursor is shown.

`Util/SmartRotate.cs:246-280` (relevant entry):

```csharp
private static void _GetNext(ISmartRotatable rotatable, bool isForward, Quaternion offset, Vector3 centerOfRotation)
{
    if (rotatable == null) return;
    ConnectionType connectionType = rotatable.GetConnectionType();
    int[] openEndLocationPermutation = rotatable.GetOpenEndLocationPermutation(offset);  // <- IndexOutOfRange here
    Dictionary<int[], int> dictionary = OrientationLookup[connectionType];
    RotationInformation[] array = RotationsList[connectionType];
    int index = dictionary[openEndLocationPermutation];
    ...
}
```

Mitigation strategies:

1. **Re-run `SmartRotate.AutomaticSetup`** on the affected prefab after flipping `AllowedRotations` and `RotationAxis`. The function is `public static`, has zero callers in the rest of the assembly (it is the editor-bake setup that vanilla never invokes at runtime), and its `(PlacementSnap.Grid, RotationAxis.{XY,ZX,ZY,All})` branch produces `int[6] {0,1,2,3,4,5} + ConnectionType.Exhaustive` — exactly the shape the (Grid, All) `GetOpenEndLocationPermutation` path expects. Vanilla SmartRotate then handles autoplace and scroll cycling without any Harmony patch. End state matches the C# field defaults declared on `ElectricalInputOutput.cs:28-30`. PowerTransmitterPlus uses this strategy. Three field writes total per prefab (`AllowedRotations`, `RotationAxis`, plus `AutomaticSetup` which sets `OpenEndsPermutation` and `ConnectionType` internally).

2. **Take over `_GetNext` with a Harmony Prefix** that, for the affected types, performs a hand-rolled rotation and returns `false`. Avoids the permutation array entirely. Pro: no need to understand the vanilla data shape. Con: bypasses vanilla's curated 24-orientation cycle for `Exhaustive`, so any future SmartRotate feature update does not reach the patched prefabs; the rotation rule is hand-rolled (e.g. 90 deg world-up yaw matching `RotateLeft`/`RotateRight`) and may diverge from vanilla semantics on non-floor poses. Drop in favour of strategy 1 unless `AutomaticSetup` is for some reason unsafe in the calling context.

3. **Skip `_GetNext` entirely.** Same Prefix returning `false` unconditionally for the affected types, with no replacement rotation. C-key smart rotate becomes a silent no-op for the dish. Simpler than strategy 2, but loses any muscle memory the player had for C-cycling on the prefab. Worse than strategy 1.

4. **Hand-write `OpenEndsPermutation` and `ConnectionType` directly** instead of calling `AutomaticSetup`. Equivalent end state to strategy 1, skips the `_AutomaticInitial3DSetup` + `_SetPermutation` lookup. Marginally fewer code paths exercised, but loses the safety net: if a future game version changes what `(Grid, All)` should produce, strategy 1 picks up the change automatically.

5. **Avoid changing `RotationAxis`** if a different gate is suitable. Not applicable for the wall/ceiling placement use case: the cursor R-key handler at `InventoryManager.cs:2443-2479` is gated by `RotationAxis` per axis, so floor-only prefabs that need pitch/roll input must lift `RotationAxis` to enable the keys.

## Empirical "floor-only" without code-level enforcement
<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

PowerTransmitter, PowerReceiver, and other devices that are floor-only in vanilla carry NO subclass override of `AllowedRotations` or `RotationAxis` in code. The C# default for both fields on `Structure` is `All`. The floor-only behavior comes from the prefabs' inspector values baked into `sharedassets0.assets` / `resources.assets` (Unity prefab serialization), which override the C# field initializers at runtime.

A patch that flips ONLY `AllowedRotations` to `All` is necessary but **not sufficient**. `AllowedRotations.All` removes the surface auto-correct in `UpdatePlacement` (the cursor stops snapping back to the floor face when the player aims at a wall), but the cursor R-key handler at `InventoryManager.cs:2443-2479` is gated independently by `RotationAxis` on a per-axis basis. With the dish prefab's baked `RotationAxis = Y`, the player can yaw the cursor with Q/E but cannot pitch or roll it onto a wall or ceiling face — the cursor remains upright and there is no input path to change that.

The minimum code-side patch to fully unlock non-floor placement on the dish prefab pair is therefore:

```csharp
prefab.AllowedRotations = AllowedRotations.All;
prefab.RotationAxis = RotationAxis.All;
```

Applied to both the SourcePrefab (so `Object.Instantiate` clones inherit the new values) and to any already-cloned `InventoryManager._constructionCursors` entry. Confirmed empirically on 2026-04-26: the SourcePrefab walk alone (`AllowedRotations.All` only) produced clean startup logs but the player still could not pitch the cursor onto a wall via the bound `RotateUp`/`RotateDown` keys; adding `RotationAxis = All` resolved it.

## Straight pipe/cable/chute roll is an unconstrained cosmetic degree of freedom
<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

Pipes, cables, and chutes live on the 0.5 m "small grid", not the 2 m structure grid: `SmallGrid.SmallGridSize = 0.5f`, `SmallGrid.SmallGridOffset = 0.25f` (declared at `Assembly-CSharp` `SmallGrid` class, ~`:293485-293487`; `HalfGridSize = 1f`, `GridSize = 2f` are the larger grids). A single-tile straight piece is a `SmallSingleGrid`: `Cable : SmallSingleGrid, IGridMergeable, ISmartRotatable, ...`; `Pipe : SmallSingleGrid, ISmartRotatable, ...`; `Piping : Pipe, IGridMergeable, ISmartRotatable`. Each has a `public bool IsStraight` field that is `true` on the straight prefabs and `false` on the corner/tee/cross variants. The cell a `SmallSingleGrid` occupies is its `position` cell, which is **rotation-invariant** (`GridBounds.GetLocalSmallGrid(position, rotation)` for a single origin-cell piece returns that one cell regardless of `rotation`).

`Cable.ConnectionType = SmartRotate.ConnectionType.Exhaustive` (the enum value `Exhaustive = 8`; `Straight = 2`, `FlatStraight = 1025`, `FlatExhaustive = 1028`). `SmartRotate.RotationsList[Exhaustive]` is a 24-entry list and `NumberOfUniqueOrientationsOf[Exhaustive] = 24` — "Exhaustive" means *treat the prefab as having no rotational symmetry, cycle all 24 cube orientations*. `Cable.OpenEndsPermutation = new int[6] { 0, 1, 2, 3, 4, 5 }` (the per-cube-face label bookkeeping; identical defaults appear on every `ISmartRotatable`, e.g. `RocketScanner`). Consequence: for a "straight along axis A" cable, **4 of those 24 orientations are equivalent in connectivity** — the 0/90/180/270 deg rolls about A — but distinct in mesh appearance. The build cursor and `SmartRotate.GetNext`/`_GetNext` (the `QuantityModifier` smart-place cycle) only maximise the *open-end count* (`ConnectedCount()`), which is roll-blind for a straight piece, so among the rolls that tie they land on whichever comes first in the cycle and **never normalise the roll**. The plain R-key roll (`InventoryManager` `RotateRollLeft`/`RotateRollRight` -> `transform.Rotate(Vector3.forward, ±90, Space.World)`) rotates the cursor freely and is gated by `RotationAxis`, not by `ConnectionType`, so the player can park the cursor at any of the four rolls and it persists across placements (see the R-key row in "Where rotation can be modified" and the `CurrentRotation` carry-over in `UpdatePlacement`).

For a *straight* piece, connectivity / network membership / collision are roll-blind. `Connection.SetGrids()` derives the two end cells from `Transform.position` and `Transform.position + Transform.forward * 0.5f`; a straight piece's two open-end child transforms sit on the run axis with `forward` pointing along it, so rolling about that axis moves neither end. `SmallGrid.IsConnected(Connection)` matches facing-grid against the other piece's open-end local grid plus a `Connection.ConnectionType` bitmask overlap (grid + bitmask only). `SmallGrid.Connected()`, `Cable.WillJoinNetwork()`, `SmallGrid.FindConnectingEnd()` (position cell + `forward` direction), `Cable._IsCollision()` (only `CableType` mismatch / `BlockMergeWithOtherCables`), and `SmallGrid.CanConstruct()` (iterates the rotation-invariant occupied-cell set) are all roll-invariant. Save/load carries only `CableNetworkId` for the cable. **Corner/tee/cross pieces are different** — their open ends are not on a single axis, so "roll" there moves the ends and changes connectivity; only `IsStraight == true` pieces have a free roll.

Why pipes and chutes do not visibly glitch but heavy / super-heavy cables do: a straight pipe / chute mesh is rotationally symmetric about its run axis (a plain cylinder / box), so its roll is invisible; the heavy and super-heavy cable mesh carries a red band running along its length that breaks that symmetry, so the roll shows as a misaligned band between adjacent pieces. There is **no `CableMesh` / `CableSkin` / dynamic-mesh-variant component on `Cable`** (unlike the pipe mesh-variant system) — `Cable.GetThingRenderers()` returns the static prefab `Renderers`, `Cable` has no `UpdateMesh`/`RefreshMesh`/`OnFinishedLoad` override, and `SmallGrid.RebuildGridState()` only refreshes the open-end grids. The visible cable mesh follows `transform.rotation` 1:1; change it at runtime and the mesh follows the same frame, no refresh call. (Pipes still carry the same roll in the *saved* `WorldRotation` — a part-zoop / part-manual pipe run is just as "mixed" in the data — it just is not visible.)

`WorldRotation` (and `StructureSaveData.RegisteredWorldRotation`) is applied verbatim on load (see "Quaternion round-trip durability") and re-serialized verbatim — `Thing.DeserializeSave` does `ThingTransform.rotation = ...RegisteredWorldRotation` with no rounding or re-derivation; `Structure.DeserializeSave` / `Cable.DeserializeSave` add only build-state / network re-attach. So a baked-in roll persists exactly, and a roll changed at runtime before saving is saved and reloaded exactly.

`ZoopMod` (Workshop 3310094883, see `ThirdPartyModIdentities.md`) is the main third-party source of mixed rolls. `ZoopMod.SetStraightRotation(Structure, ZoopDirection)` hard-codes each zooped straight piece's rotation from the drag axis: for a cable/pipe a Z run -> `SmartRotate.RotZ.Rotation` (`Quaternion.Euler(0,0,90)`), an X or Y run -> `SmartRotate.RotX.Rotation` (`Euler(90,0,0)`) (chutes swap which axis maps to which `Rot*`); it then writes the quaternion straight onto the preview piece's `transform.rotation` and replays it verbatim into `InventoryManager._usePrimaryRotation` -> `OnServer.UseMultiConstructor`. It ignores the build cursor's current roll (it only honours the cursor roll on the rare path where the kit has no Corner variant, which cables/pipes/chutes are not). So a zoop-built Z cable is always at roll +90 deg while a fresh click-built one starts at `Quaternion.identity` (roll 0 deg) — a guaranteed 90 deg mismatch at the seam between a zooped run and a manually-placed cable, and a part-zoop / part-manual run (or one where R was pressed between manual clicks) ends up mostly one roll with a few odd pieces. `BlueprintMod` (Workshop 3672138641) copies each Thing's exact `transform.rotation` and re-emits it multiplied by a 90-deg-snapped paste yaw — it neither introduces nor fixes the mismatch, but a paste of an already-mixed source reproduces it (rigidly re-oriented; a 90/180/270 yaw maps the four-roll family onto itself).

Practical consequence for a mod: forcing a `Cable.IsStraight == true` piece to a canonical roll for its run axis (e.g. pick one of the four rolls per run axis and rewrite the transform) is safe and purely cosmetic — set `ThingTransform.rotation` (and update the registered rotation, e.g. via `Thing.MoveToWorld(position, rotation, ...)` which takes the handed rotation verbatim) and the mesh follows that frame; no network rebuild, collision re-check, or merge-system involvement; the new rotation persists in the save and replicates to clients through the normal transform sync; never touch corner/tee/cross pieces. Flipping `Cable.ConnectionType` from `Exhaustive` to `Straight` (3 orientations) on the prefabs would additionally give the *build cursor / smart-place cycle* a canonical roll like pipe straights, but does not constrain ZoopMod's direct `transform.rotation` write or the plain R-key roll, and does not fix anything already baked into a save; an on-load normalisation sweep is the only thing that fixes existing worlds.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

- 2026-04-25: page created from a six-agent decompile pass investigating ceiling/wall placement of PowerTransmitter/PowerReceiver. All sections sourced from direct reads of `Structure.cs`, `InventoryManager.cs`, `Constructor.cs`, `MultiConstructor.cs`, `CreateStructureInstance.cs`, `ConstructionCreationMessage.cs`, `StructureSaveData.cs`. No conflicts with prior central pages.
- 2026-04-26: refined "Where rotation can be modified" R-key row and "Empirical floor-only" section after empirical testing surfaced the `RotationAxis` per-axis gate at `InventoryManager.cs:2443-2479`. Source: direct decompile re-read after a deployed patch with only `AllowedRotations = All` failed to enable wall placement in-game. Additive: documents a previously-implicit gate; `RotationAxis` is acknowledged as a separate prefab inspector value that, alongside `AllowedRotations`, must be lifted for non-floor placement.
- 2026-04-26: added "SmartRotate IndexOutOfRange under RotationAxis = All" section after empirical Player.log inspection surfaced an `IndexOutOfRangeException` in `Util.Permutation.Permute` every placement-mode frame for `ElectricalInputOutput`-derived prefabs once their `RotationAxis` is flipped to `All`. Source: direct read of `Util/SmartRotate.cs:246+` (the private static `_GetNext`), `Util/Permutation.cs`, and `Util/ExtensionMethods.cs:1870-1920` after the in-game crash. Additive: documents a previously-uncharacterized side effect of the prefab-mutation strategy.
- 2026-04-26: refined the "Mitigation strategies" list after empirical testing showed the initial "skip `_GetNext`" approach silently broke the player's C-key smart-rotate workflow (the `QuantityModifier` autoplace + scroll-cycle path in `InventoryManager.PlacementMode`). Promoted the takeover-with-90-deg-yaw approach to strategy 1; demoted the bare-skip approach to strategy 2 with the silent-input-loss caveat. PowerTransmitterPlus now uses strategy 1.
- 2026-04-26: restructured "Mitigation strategies" again after re-reading `SmartRotate.AutomaticSetup` (`Util/SmartRotate.cs:705`) and `ElectricalInputOutput.cs:28-30`. Found that the vanilla setup function takes the same `(PlacementSnap, RotationAxis, AllowedRotations)` triple that drives `GetOpenEndLocationPermutation` and produces the matching `OpenEndsPermutation` size and `ConnectionType` for any combination. Calling it after the field flips is the cleanest fix: it eliminates the Harmony patch entirely and hands the cursor cycle back to vanilla SmartRotate. Promoted the AutomaticSetup approach to strategy 1; demoted the takeover and skip approaches to strategies 2 and 3; added a new strategy 4 for hand-written field assignment as a finer-grained alternative. PowerTransmitterPlus now uses strategy 1.
- 2026-05-12: added "Straight pipe/cable/chute roll is an unconstrained cosmetic degree of freedom" after a three-agent investigation into a reported super-heavy-cable visual glitch (collinear `StructureCableSuperHeavyStraight` pieces with mismatched roll, red band misaligned; ~42% of straight-cable runs in the reporter's save affected). Sources: direct reads of `Cable`/`Pipe`/`Piping`/`SmallSingleGrid`/`SmallGrid`/`Connection` in `Assembly-CSharp.decompiled.cs` (0.2.6228.27061), `Util.SmartRotate` (`RotX/RotY/RotZ`, `ConnectionType` enum, `RotationsList`), and a decompile of `ZoopMod.dll` (Workshop 3310094883) — `SetStraightRotation`/`ApplyRotation`/`PositionSmallGridStructures` — and `BlueprintMod.dll` (3672138641) paste path. Cross-checked against this page's existing "SmartRotate IndexOutOfRange" and "Quaternion round-trip durability" sections; additive, no conflict (the IndexOutOfRange section is about flipping `RotationAxis` to `All` on devices; this one is about the cosmetic roll on straight small-grid pieces).

## Open questions

- Whether `OnAssignedReference` or `RebuildGridState` is guaranteed to fire after `DeserializeSave` on every Structure load path. If neither fires, a non-default `Direction` would not be restored from save (the structure's `ThingTransform.rotation` would be correct, but `Direction` would be `Quaternion.identity` until something later reassigns it). Empirical confirmation needed via InspectorPlus snapshot of `(Structure.Direction, ThingTransform.rotation)` post-load.
- Whether any vanilla code reads `Direction` for gameplay logic (vs. just for re-serialization). If reads are limited to network-sync paths and `RebuildGridState`, the load-time gap is harmless; if any tick code reads `Direction`, the gap matters. Grep + read of every consumer needed.
- Where `Pipe` / `Piping` get their `ISmartRotatable.GetConnectionType()` value. Neither class declares an explicit `ConnectionType` field in the decompile (unlike `Cable`, which sets `Exhaustive`); the value may come from a prefab inspector field, a base, or `SmartRotate.AutomaticSetup` at bake time. The reporter's save shows ~17% of straight *pipe* runs are also roll-mixed in the saved `WorldRotation`, so whatever pipes use does not fully constrain the roll either. Decompile re-read of `Pipe`/`Piping`/`Structure` for the `ConnectionType` source needed before claiming "pipe straights use `ConnectionType.Straight`".
