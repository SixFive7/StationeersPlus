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
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Networking.CreateStructureMessage
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Networking.CreateStructureInstance
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.OnServer (Create<T> overloads, UseMultiConstructor)
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Thing (Create<T>, DeserializeNew, OnRegistered, OnAssignedReference)
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.WorldManager / Assets.Scripts.Serialization.XmlSaveLoad (LoadThing, LoadWorld)
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Util.Referencable (RegisterNew, RegisterAs)
  - workshop://544550/3310094883 ZoopMod.dll :: ZoopMod (SetStraightRotation / ApplyRotation / PositionSmallGridStructures / ZoopBuildExecutor.BuildAll)
  - workshop://544550/3672138641 BlueprintMod.dll :: BlueprintMod (CreateSingleEntry / paste path)
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
<!-- updated: 0.2.6228.27061 @ 2026-05-12 — corrected the earlier "Pipe/Piping don't declare a ConnectionType field" claim (they do; same as Cable); added the SmartRotate-data details, AutomaticSetup-has-no-callers, the cursor-default-is-identity finding, and the pointer to connectiontype-findings.md -->


Pipes, cables, and chutes live on the 0.5 m "small grid", not the 2 m structure grid: `SmallGrid.SmallGridSize = 0.5f`, `SmallGrid.SmallGridOffset = 0.25f` (declared at `Assembly-CSharp` `SmallGrid` class, ~`:293485-293487`; `HalfGridSize = 1f`, `GridSize = 2f` are the larger grids). A single-tile straight piece is a `SmallSingleGrid`: `Cable : SmallSingleGrid, IGridMergeable, ISmartRotatable, ...`; `Pipe : SmallSingleGrid, ISmartRotatable, ...`; `Piping : Pipe, IGridMergeable, ISmartRotatable`. Each has a `public bool IsStraight` field that is `true` on the straight prefabs and `false` on the corner/tee/cross variants. The cell a `SmallSingleGrid` occupies is its `position` cell, which is **rotation-invariant** (`GridBounds.GetLocalSmallGrid(position, rotation)` for a single origin-cell piece returns that one cell regardless of `rotation`).

`Cable.ConnectionType = SmartRotate.ConnectionType.Exhaustive` (the enum value `Exhaustive = 8`; `Straight = 2`, `FlatStraight = 1025`, `FlatExhaustive = 1028`, `StraightAsymmetric = 3000`). `SmartRotate.RotationsList[Exhaustive]` is a 24-entry list and `NumberOfUniqueOrientationsOf[Exhaustive] = 24` — "Exhaustive" means *treat the prefab as having no rotational symmetry, cycle all 24 cube orientations*. `Cable.OpenEndsPermutation = new int[6] { 0, 1, 2, 3, 4, 5 }` (the per-cube-face label bookkeeping; identical defaults appear on every `ISmartRotatable`, e.g. `RocketScanner`). `Pipe`, `Piping : Pipe`, and `Chute` carry the **same** declarations: `Pipe` declares its own `[Header("ISmartRotation")] public SmartRotate.ConnectionType ConnectionType = SmartRotate.ConnectionType.Exhaustive;` + `public int[] OpenEndsPermutation = new int[6] { 0, 1, 2, 3, 4, 5 };`, `Chute` the same, and `Piping` inherits `Pipe`'s. `SmallGrid` (the shared base) does **not** declare `ConnectionType` or implement `ISmartRotatable`; it provides only `GetGridSize() => GridSize` (= 2 m, the *large*-grid unit, used by `GetFaceDir` in the auto-setup). `SmartRotate.AutomaticSetup` (the editor-bake helper that would derive a smaller `ConnectionType` like `Straight` from the prefab's open-end geometry) **has zero callers anywhere in `Assembly-CSharp`** — it is not invoked at runtime, so each class's C# default (`Exhaustive`) stands unless the Unity prefab serialization overrides it. **What the *straight* pipe/cable/chute prefabs actually serialize for `ConnectionType` cannot be confirmed from the decompile** (the prefab data in `sharedassets0.assets` / `resources.assets` overrides the C# field initializer and is not visible there); the reporter's ~17 % roll-mixed straight-pipe runs are consistent with EITHER `Straight` or `Exhaustive` on the prefab (ZoopMod's direct `transform.rotation` write and the plain R-key roll mix rolls regardless of `ConnectionType`). What `AutomaticSetup` *would* produce for a straight cable/pipe is `ConnectionType.Straight` + `OpenEndsPermutation = {1,0,0,0,0,1}` (both open ends same `NetworkType`, on one axis → matches `Straight`'s seed key); `RotationsList[Straight] = [RotX, RotY, RotZ]`, `NumberOfUniqueOrientationsOf[Straight] = 3` — "run along X / Y / Z, one canonical roll each". Consequence with the actual `Exhaustive` default: for a "straight along axis A" cable, **4 of those 24 orientations are equivalent in connectivity** — the 0/90/180/270 deg rolls about A — but distinct in mesh appearance. The build cursor and `SmartRotate.GetNext`/`_GetNext` (the `QuantityModifier` smart-place cycle) only maximise the *open-end count* (`ConnectedCount()`), which is roll-blind for a straight piece, so among the rolls that tie they land on whichever comes first in the cycle and **never normalise the roll**. The plain R-key roll (`CursorManager.PlacementMode` `RotateRollLeft`/`RotateRollRight` -> `ConstructionCursor.ThingTransform.Rotate(Vector3.forward, ±90, Space.World)`) rotates the cursor freely and is gated by `(RotationAxis & Z)`, not by `ConnectionType`, so the player can park the cursor at any of the four rolls and it persists across placements (see the R-key row in "Where rotation can be modified" and the `CurrentRotation` carry-over in `UpdatePlacement`). A fresh-picked cable cursor starts at `Quaternion.identity` (the cursor object is `Instantiate(prefab, Vector3.zero, Quaternion.identity)` once at startup; `CursorManager.CurrentRotation` defaults to `identity`), so a Z-running straight cable placed by a fresh click with no R presses is at roll 0° — *not* ZoopMod's `Euler(0,0,90)`, which is the source of the 90°-per-seam mismatch between zooped and click-built cable. The full SmartRotate-data dump (`ConnectionType` enum values, `RotX/RotY/RotZ` quaternions, `RotationsList`/`NumberOfUniqueOrientationsOf` per type, `AutomaticSetup` / `_AutomaticInitial3DSetup` / `_SetPermutation` behaviour, the `_GetNext` `KeyNotFoundException` trap when `ConnectionType` is flipped without fixing `OpenEndsPermutation`, and the merge-path `ConnectionType` reads in `MultiMergeConstructor.Construct`) is in `.work/2026-05-12-cable-rotation/notes/connectiontype-findings.md` (game version 0.2.6228.27061).

For a *straight* piece, connectivity / network membership / collision are roll-blind. `Connection.SetGrids()` derives the two end cells from `Transform.position` and `Transform.position + Transform.forward * 0.5f`; a straight piece's two open-end child transforms sit on the run axis with `forward` pointing along it, so rolling about that axis moves neither end. `SmallGrid.IsConnected(Connection)` matches facing-grid against the other piece's open-end local grid plus a `Connection.ConnectionType` bitmask overlap (grid + bitmask only). `SmallGrid.Connected()`, `Cable.WillJoinNetwork()`, `SmallGrid.FindConnectingEnd()` (position cell + `forward` direction), `Cable._IsCollision()` (only `CableType` mismatch / `BlockMergeWithOtherCables`), and `SmallGrid.CanConstruct()` (iterates the rotation-invariant occupied-cell set) are all roll-invariant. Save/load carries only `CableNetworkId` for the cable. **Corner/tee/cross pieces are different** — their open ends are not on a single axis, so "roll" there moves the ends and changes connectivity; only `IsStraight == true` pieces have a free roll.

Why pipes and chutes do not visibly glitch but heavy / super-heavy cables do: a straight pipe / chute mesh is rotationally symmetric about its run axis (a plain cylinder / box), so its roll is invisible; the heavy and super-heavy cable mesh carries a red band running along its length that breaks that symmetry, so the roll shows as a misaligned band between adjacent pieces. There is **no `CableMesh` / `CableSkin` / dynamic-mesh-variant component on `Cable`** (unlike the pipe mesh-variant system) — `Cable.GetThingRenderers()` returns the static prefab `Renderers`, `Cable` has no `UpdateMesh`/`RefreshMesh`/`OnFinishedLoad` override, and `SmallGrid.RebuildGridState()` only refreshes the open-end grids. The visible cable mesh follows `transform.rotation` 1:1; change it at runtime and the mesh follows the same frame, no refresh call. (Pipes still carry the same roll in the *saved* `WorldRotation` — a part-zoop / part-manual pipe run is just as "mixed" in the data — it just is not visible.)

`WorldRotation` (and `StructureSaveData.RegisteredWorldRotation`) is applied verbatim on load (see "Quaternion round-trip durability") and re-serialized verbatim — `Thing.DeserializeSave` does `ThingTransform.rotation = ...RegisteredWorldRotation` with no rounding or re-derivation; `Structure.DeserializeSave` / `Cable.DeserializeSave` add only build-state / network re-attach. So a baked-in roll persists exactly, and a roll changed at runtime before saving is saved and reloaded exactly.

`ZoopMod` (Workshop 3310094883, see `ThirdPartyModIdentities.md`) is the main third-party source of mixed rolls. `ZoopMod.SetStraightRotation(Structure, ZoopDirection)` hard-codes each zooped straight piece's rotation from the drag axis: for a cable/pipe a Z run -> `SmartRotate.RotZ.Rotation` (`Quaternion.Euler(0,0,90)`), an X or Y run -> `SmartRotate.RotX.Rotation` (`Euler(90,0,0)`) (chutes swap which axis maps to which `Rot*`); it then writes the quaternion straight onto the preview piece's `transform.rotation` and replays it verbatim into `InventoryManager._usePrimaryRotation` -> `OnServer.UseMultiConstructor`. It ignores the build cursor's current roll (it only honours the cursor roll on the rare path where the kit has no Corner variant, which cables/pipes/chutes are not). So a zoop-built Z cable is always at roll +90 deg while a fresh click-built one starts at `Quaternion.identity` (roll 0 deg) — a guaranteed 90 deg mismatch at the seam between a zooped run and a manually-placed cable, and a part-zoop / part-manual run (or one where R was pressed between manual clicks) ends up mostly one roll with a few odd pieces. `BlueprintMod` (Workshop 3672138641) copies each Thing's exact `transform.rotation` and re-emits it multiplied by a 90-deg-snapped paste yaw — it neither introduces nor fixes the mismatch, but a paste of an already-mixed source reproduces it (rigidly re-oriented; a 90/180/270 yaw maps the four-roll family onto itself).

Practical consequence for a mod: forcing a `Cable.IsStraight == true` piece to a canonical roll for its run axis (e.g. pick one of the four rolls per run axis and rewrite the transform) is safe and purely cosmetic — set `ThingTransform.rotation`, set `RegisteredRotation` (the save and join-package both read this, not the live transform — see "Changing a placed Structure's rotation at runtime" below) and `Direction`, and on the server set `NetworkUpdateFlags |= 1` for the per-tick client sync; the mesh follows that frame; no network rebuild, collision re-check, or merge-system involvement (do NOT re-register the cable — that re-runs `Cable.OnRegistered` → `CableNetwork.Merge`); the new rotation persists in the save and replicates to clients; never touch corner/tee/cross pieces. (`MoveToWorld` is not available on `Cable` — it is the `DynamicThing` inventory-eject op — and would not help anyway; see the next section.) Flipping `Cable.ConnectionType` from `Exhaustive` to `Straight` (3 orientations) on the prefabs would additionally give the *build cursor / smart-place cycle* a canonical roll like pipe straights, but does not constrain ZoopMod's direct `transform.rotation` write or the plain R-key roll, and does not fix anything already baked into a save; an on-load normalisation sweep is the only thing that fixes existing worlds.

## Changing a placed Structure's rotation at runtime (save + multiplayer sync)
<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

A mod that wants to re-orient an already-placed `Structure` at runtime (for example, normalising a straight cable's cosmetic roll, see the previous section) must update three fields and, on the server, set one network bit. `MoveToWorld` is **not** the tool for this: every `MoveToWorld(Vector3, Quaternion, ...)` and `MoveToWorld(float)` overload is declared on `DynamicThing` (`Assembly-CSharp` `DynamicThing.MoveToWorld` ~`:282518` / `:282626`) or `DynamicThing` subclasses (`Entity` `:284117`, `RobotMining` `:290690`, `Flashlight` `:325142`, `BodyArmor` `:406599`, `Clothing` `:406637`, `Suit` `:407405`, `SuitBase` `:408724`); `Thing` itself declares no `MoveToWorld`, and `Structure`/`SmallGrid`/`Cable` do not either. It is the "eject a held/parented Thing into the world" operation — `DynamicThing.MoveToWorld(Vector3, Quaternion, ...)` opens with `if (ParentSlot == null) return true;`, so for any non-slotted Thing (every placed `Structure`) it is a no-op; for slotted items it sets `ThingTransform.rotation`/`Position`/`ThingTransformPosition` then does inventory-exit bookkeeping (slot empty, `OnChildExitInventory`, physics on, `RigidBody`, `WorldGrid`/`GridWatchers`, `LodManager`) and replicates via the separate `MoveToWorldMessage` / `OnServer.MoveToWorld` round-trip (`:259313` / `:39397`), not the per-tick transform delta — it never touches `RegisteredPosition`/`RegisteredRotation`/`Direction`/`RegisteredLocalGrid`, never calls `OnRegistered`/`OnDeregistered` on the structure grid, and never sets a transform `NetworkUpdateFlags` bit. There is also no engine-side "resync this structure's transform" helper (`SnapTransform` at `Thing.cs:303179` / `Structure.cs:295544` snaps the transform locally; it does not flag a network update).

Field map (where each "rotation" lives and what reads it):

| Field | Storage | Written by | Read by (that matters here) |
|---|---|---|---|
| `ThingTransform.rotation` | Unity transform | anyone; the `ThingTransformRotation` / `ThingTransformLocalRotation` setters (`Thing.cs:298426` / `:298439`) mirror it to `Rotation` but not to `RegisteredRotation`/`Direction`/`NetworkUpdateFlags` | the visible mesh (a `Cable`'s mesh is a static child renderer following the transform 1:1); `Thing.BuildUpdateTransform` reads the `Rotation` mirror |
| `Thing.RegisteredRotation` (`{get;set;}` auto-prop, `Thing.cs:298412`) | backing field | `GridController.World.Register(Structure)` (`:191480`: `structure.RegisteredRotation = structure.ThingTransformRotation;`); `Structure.RebuildGridState` (`:295652`) | **the save**: `Structure.InitialiseSaveData` (`:295797`) `structureSaveData.RegisteredWorldRotation = base.RegisteredRotation;`. **The join-package transform**: `Structure.WriteTransform` (`:295588-295591`) `writer.WriteVector3(base.RegisteredPosition); writer.WriteQuaternion(base.RegisteredRotation);` (called from `Thing.SerializeOnJoin` `:303038`), which a joining client applies via `Thing.DeserializeNew` (`:303168`) `Create<Thing>(...)` + `SnapTransform(transformPosition, transformRotation)` (`:303176`/`:303179`) |
| `Structure.Direction` (`public Quaternion Direction;`, `Structure.cs:295264`; the `[ReadOnly]` is an inspector attribute, the field is mutable) | public field | `Structure.OnAssignedReference` (`:295614`: `Direction = ThingTransform.rotation;`); `Structure.RebuildGridState` (`:295648`); `Structure.SetStructureData` (`:297342`: `Direction = localRotation;`); `Structure.DeserializeOnJoin` (`:295607`: `Direction = reader.ReadQuaternion();`) | `Structure.SerializeOnJoin` (`:295599`: `writer.WriteQuaternion(Direction);`) — join-package write only. **No tick/gameplay code reads it** (see "No gameplay code reads Structure.Direction" below). |
| `Structure.RegisteredLocalGrid` (`public Grid3`, `Structure.cs:295248`) | public field | `OnAssignedReference` (`:295615`), `RebuildGridState` (`:295649`), `SetStructureData` (`:297344`), `World.Register`/`AddSmallGridStructure` (`:191478` snaps position from it) | the registered cell. Rotation-invariant for a single-origin-cell `SmallSingleGrid` — a roll-only change does not move it. |
| `Thing.NetworkUpdateFlags` (`public ushort {get;set;}`, `Thing.cs:299281`) | backing field | many sites set various bits; **bit `1` (transform) is never set when a `Structure`/`Cable` transform changes** | per-tick delta sync (below) |

Per-tick delta-sync path (how an already-connected client learns of a server-side transform change): `Thing.SerializeDeltaState` (`:303187`) iterates `OcclusionManager.AllThings`, and for each with `NetworkUpdateFlags != 0` writes `thing.BuildUpdate(writer, networkUpdateType)` then zeroes the flags (`:303197-303202`). `Thing.BuildUpdate` (`:303247`): `if (IsNetworkUpdateRequired(1u, networkUpdateType)) BuildUpdateTransform(writer);`. `Thing.BuildUpdateTransform` (`:303381-303384`): `writer.WriteVector3(Position); writer.WriteQuaternion(Rotation);` — the `Position`/`Rotation` mirror fields (the `ThingTransformRotation` setter keeps `Rotation` synced). Neither `Structure` nor `Cable` overrides `BuildUpdateTransform`/`ProcessUpdateTransform`, so the base versions apply (`Structure.WriteTransform` at `:295588` is a different method, used only by `SerializeOnJoin`). Client side: `Thing.DeserializeDeltaState` (`:303209`) → `thing.ProcessUpdate` → `Thing.ProcessUpdateTransform` (`:303387-303394`): `Vector3 v = reader.ReadVector3(); Quaternion rotation = reader.ReadQuaternion(); if (!HasAuthority) ThingTransform.SetPositionAndRotation(v, rotation);`. The client's `RegisteredRotation`/`Direction` are not updated by the delta path, but clients never save or re-serialize per-tick, so that is harmless. **The transform bit must be set explicitly** — grep of `NetworkUpdateFlags |= 1` (and `... = 1`) across `Assembly-CSharp` hits only `Slot.Take` for `DynamicThing` slot occupants (~`Slot.cs:292640`/`:292644`), a `CursorManager`-area site (`:56523`), vehicle/rocket classes (`:141801`, `:202841`), and unrelated structs with their own byte `NetworkUpdateFlags` (`:397615`, `:414907`, `:414933`); nothing in the `Structure`/`Cable`/transform-setter path.

Recommended minimal recipe (server-authoritative; single-player counts as host so `IsServer` is true there too — the flag set is harmless with no clients):

```csharp
// q = the target quaternion
thing.ThingTransform.rotation = q;            // visible mesh follows this frame
thing.RegisteredRotation     = q;             // save (StructureSaveData.RegisteredWorldRotation) + join-package transform (Structure.WriteTransform/SnapTransform)
thing.Direction              = q;             // keep the join-package Direction field consistent (harmless if not strictly needed)
if (NetworkManager.IsServer)
    thing.NetworkUpdateFlags |= 1;            // per-tick transform delta to already-connected clients
// rotation-only change: do NOT touch RegisteredPosition / RegisteredLocalGrid (rotation-invariant for a single-cell SmallSingleGrid)
// do NOT call OnDeregistered/OnRegistered (re-register re-runs subclass OnRegistered logic, e.g. Cable.OnRegistered -> CableNetwork.Merge, a network rebuild)
```

(Using `thing.ThingTransformRotation = q` instead of the raw `ThingTransform.rotation = q` additionally sets the `Rotation` mirror; either is fine since `WriteTransform`/`BuildUpdateTransform` re-read from the live transform anyway.)

Option comparison for "cosmetic re-orient, must save + sync": **(a)** bare `ThingTransform.rotation = q` — insufficient; the save writes the old `RegisteredRotation`, clients are never told, the host reload snaps it back. **(a')** `ThingTransformRotation = q` setter — still insufficient; the save reads `RegisteredRotation`, not the `Rotation` mirror. **(b)** transform + `RegisteredRotation` + `Direction` + (server) `NetworkUpdateFlags |= 1` — the recipe above; nothing missing, no re-register, no network logic. **(c)** `MoveToWorld` — N/A, not on `Structure`/`Cable`. **(d)** transform write then `Structure.RebuildGridState()` (`Structure.cs:295646`) — re-snapshots `Direction`, `RegisteredLocalGrid`, `RegisteredPosition`, `RegisteredRotation`, `BlockingGrids` and (via `SmallGrid.RebuildGridState`, `:293590`) re-runs `openEnd?.SetGrids()` per open end; **does not** set `NetworkUpdateFlags |= 1` and does **not** run network logic / `OnRegistered`, so you still need the sync-flag write and you have done strictly more work than (b); it is the "call one engine method instead of hand-setting fields" fallback (and the path the rocket grid system uses after the grid moves: `RocketGridController.RebuildAllGridState` ~`:176824`). **(e)** destroy + `Constructor.SpawnConstruct` with the new rotation — heaviest, known-good; `SpawnConstruct` (`Constructor.cs:276940`) does `Thing.Create<Structure>(prefab, WorldPosition, WorldRotation, 0L)` (new `ReferenceId`, full Thing replication via `NewToSend`→`DeserializeNew`) then `SetStructureData(...)`; `Thing.Create` → `OnAssignedReference` → `World.Register` → for a `Cable`, `Cable.OnRegistered` at `GameState.Running` runs `CableNetwork.Merge(...)` (network rebuild), and destroying the old one runs `Cable.OnDestroy` → `CableNetwork.Remove` + `CableNetwork.RebuildCableNetworkServer` per connected cable (another rebuild). Two network rebuilds for a transform tweak; avoid unless (b)/(d) genuinely cannot be used.

For a `Cable` specifically: re-registering would re-run `Cable.OnRegistered` (`Cable.cs:371477`) which, when `GameManager.GameState != GameState.Loading && GameManager.RunSimulation`, calls `CableNetwork.Merge(CableNetwork.ConnectedNetworks(this))` — a full network rebuild. The recipe (b) never deregisters or re-registers, so the `CableNetwork` is untouched. (And for a straight cable a roll about the run axis is connectivity-blind regardless — see the previous section.)

## Build-time structure-creation chokepoints (and the load/build discriminator)
<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

For a *build-time* JIT fix — rewrite a structure's rotation before it is created, or replace a long multi-tile straight variant (`StructureCableSuperHeavyStraight3/5/10`, `StructurePipeStraight3/5/10`, …) with N single-tile placements as it is built — the relevant question is which method every host-side fresh-creation path passes through, and how to distinguish a fresh build/spawn from a save-deserialize or join-replication. **Every host-side path that creates a placed `Structure` (or any `Thing`) funnels through `Thing.Create<T>(Thing prefab, Vector3 worldPosition, Quaternion worldRotation, long referenceId = 0L)` (`Thing.cs:299915`, the `UnityEngine.Object.Instantiate` site).** The discriminator that separates "freshly built / spawned" from "loaded / replicated on join" is **`referenceId == 0L`** — every fresh-creation path passes `0L`; `WorldManager.LoadThing` passes `thingData.ReferenceId` and `Thing.DeserializeNew` (join) passes the replicated id, both non-zero. (`GameManager.GameState == GameState.Loading` is equivalent for excluding the host save loop specifically; `GameManager.RunSimulation` excludes joining/remote clients.)

Path table (verified by direct reads of `Assembly-CSharp.dll` :: `Constructor.SpawnConstruct`/`Construct`/`OnUsePrimary` `:276921-276953`, `MultiConstructor.Construct` `:288267-288292`, `MultiMergeConstructor.Construct` `:288338`, `OnServer.UseMultiConstructor` `:40078` / `OnServer.Create<T>` `:39479-39533`, `Thing.Create<T>` `:299915` / `Thing.DeserializeNew` `:303168`, `WorldManager.LoadThing` `:251312` / `XmlSaveLoad.LoadWorld` `:251347`, `ConstructionCreationMessage.Process` `:258325` / `CreateStructureMessage.Process` `:258220`, `Referencable.RegisterNew`/`RegisterAs` `:37571`/`:37610`, `InventoryManager.UsePrimaryComplete` `:271013`, `GameManager.RunSimulation` `:188999`, and decompiles of `ZoopMod.dll` Workshop 3310094883 / `BlueprintMod.dll` Workshop 3672138641 — full line-index in `.work/2026-05-12-cable-rotation/notes/chokepoint-findings.md`):

| Origin | Host-side call chain | `referenceId` | `GameState` | `RunSimulation` |
|---|---|---|---|---|
| Vanilla manual build (host) | `Constructor.OnUsePrimary` → `Constructor.Construct` → **`Constructor.SpawnConstruct(CreateStructureInstance)`** → `Thing.Create<Structure>(prefab, WorldPos, WorldRot, 0L)` then `Structure.SetStructureData(LocalRotation, owner, localGrid, colour)` | `0L` | `Running` | `true` |
| Vanilla manual build (remote client → server) | `ConstructionCreationMessage.Process` → `OnServer.Create<Structure>(PrefabHash, WorldPos, WorldRot)` → `Thing.Create<Structure>(prefab, …, 0L)` then `SetStructureData(LocalRotation, …)`. Does **not** go through `Constructor.SpawnConstruct`. | `0L` | `Running` | `true` |
| Vanilla multi-kit build (host) | `OnServer.UseMultiConstructor` → `MultiConstructor.Construct` (or `MultiMergeConstructor.Construct`, which `OnServer.Destroy(old)` + `base.Construct`) → **`Constructor.SpawnConstruct(CreateStructureInstance)`** → `Thing.Create<Structure>(…, 0L)` | `0L` | `Running` | `true` |
| Vanilla multi-kit build (remote client → server) | `CreateStructureMessage.Process` → re-runs `constructor.Construct` / `multiConstructor.Construct` host-side → **`Constructor.SpawnConstruct`**. Does **not** go through `OnServer.Create`. | `0L` | `Running` | `true` |
| ZoopMod drag commit (host) | preview-piece transforms replayed via reflection into `InventoryManager._usePrimaryPosition`/`_usePrimaryRotation` → `InventoryManager.UsePrimaryComplete()` → `OnServer.UseMultiConstructor(…, _usePrimaryRotation, …)` → `multiConstructor.Construct` → **`Constructor.SpawnConstruct`** → `Thing.Create<Structure>(…, 0L)`. (Hard-coded per-axis rotation: `Euler(0,0,90)` Z run, `Euler(90,0,0)` X/Y; chutes swap which `SmartRotate.Rot*` maps to which axis.) | `0L` | `Running` | `true` |
| ZoopMod drag commit (remote client → server) | `InventoryManager.UsePrimaryComplete()` → `NetworkClient.SendToServer(new CreateStructureMessage { Rotation = _usePrimaryRotation, … })` → `CreateStructureMessage.Process` → `multiConstructor.Construct` → **`Constructor.SpawnConstruct`**. ZoopMod uses `CreateStructureMessage`, not `ConstructionCreationMessage`. | `0L` | `Running` | `true` |
| BlueprintMod paste (host) | `BlueprintMod.CreateSingleEntry(entry, worldPos, worldRot)` → `OnServer.Create<Thing>(entry.PrefabName, worldPos, worldRot)` → `Thing.Create<T>(prefab, …, 0L)` then `UpdateBuildStateAndVisualizer(entry.BuildState, 0)`. `worldRot` used verbatim. Does **not** go through `Constructor.SpawnConstruct`. | `0L` | `Running` | `true` |
| `Cable.Break()` (rupture) | `OnServer.Destroy(this)` + `Constructor.SpawnConstruct(new CreateStructureInstance(RupturedPrefab, this))` → `Thing.Create<Structure>(…, 0L)` | `0L` | `Running` | `true` |
| Save deserialize (host) | `XmlSaveLoad.LoadWorld` (`GameState = Loading` at start, stays `Loading` for the whole per-thing loop) → `WorldManager.LoadThing(thingData)` → `Thing.Create<Thing>(prefab, RegisteredWorldPosition, RegisteredWorldRotation, thingData.ReferenceId)` then `DeserializeSave(thingData)`. Does **not** go through `Constructor.SpawnConstruct` or `OnServer.Create`. | non-zero | `Loading` | `true` |
| Client join replication | `Thing.DeserializeNew(reader)` (during the join handshake, before `GameState` flips to `Running`) → `Thing.Create<Thing>(prefabHash, pos, rot, referenceId)` then `DeserializeOnJoin` + `SnapTransform`. | non-zero | `Joining` | `false` (client) |
| NetworkPuristPlus on-load long-piece rebuild | `World.OnLoadingFinished` postfix (runs at the *end* of `LoadWorld`, *after* `OnReadyToPlay` has flipped `GameState` to `Running`) → `OnServer.Destroy(longPiece)` + `Constructor.SpawnConstruct(new CreateStructureInstance(basePrefab, cell, rot, owner, colour))` per cell → `Thing.Create<Structure>(…, 0L)` | `0L` | `Running` | `true` |

What this means for the patch shape:

- **Rewrite a rotation before the piece exists** (e.g. canonicalise a straight cable's roll at build time): a Harmony **prefix on `Constructor.SpawnConstruct(CreateStructureInstance instance)`** mutating `instance.LocalRotation` (public settable field; `WorldRotation => LocalRotation`) is clean — the rewritten value reaches `Object.Instantiate`, so `RegisteredRotation`/`RegisteredWorldRotation` are snapshotted from it during the in-`Thing.Create` registration, and `SetStructureData(instance.LocalRotation, …)` then sets `Direction` from it too; nothing to patch up afterwards (contrast a *postfix*-on-`OnRegistered` approach, which has to repair `RegisteredRotation`/`Direction` itself — see "Changing a placed Structure's rotation at runtime" above). This catches vanilla manual (host), vanilla multi-kit (host + remote-client via `CreateStructureMessage`), and ZoopMod (host + remote-client). It misses BlueprintMod paste and the remote-client-manual `ConstructionCreationMessage` path. To cover *every* fresh-creation path, a **prefix on `Thing.Create<T>(Thing prefab, ref Quaternion worldRotation, long referenceId)`** gated on `referenceId == 0L && !thing.IsCursor && prefab is Cable c && c.IsStraight` is the single chokepoint (and `referenceId != 0L` cleanly excludes save-load and join). (Equivalently a prefix on `OnServer.Create<T>(Thing prefab, ref Quaternion rotation)`, the funnel for the `(pos, rot)` overloads, covers BlueprintMod + remote-manual + `ThingSpawnMessage` + gameplay item spawns but not the `SpawnConstruct` paths.)
- **Replace a long multi-tile straight variant with N single tiles at build time**: a **prefix on `Constructor.SpawnConstruct(CreateStructureInstance instance)`** that, when `instance.Prefab` is a long variant, loops `Constructor.SpawnConstruct(new CreateStructureInstance(basePrefab, cell, rot, instance.OwnerClientId, instance.CustomColor))` once per small-grid cell and returns `false`. `Thing.Create<T>` is too low for this (no GridController/cell context, and returning `false` from it would leave `SpawnConstruct` calling `SetStructureData` on a null return). Coverage: host manual + host multi-kit + ZoopMod (host + remote-client, since `CreateStructureMessage.Process` re-enters `SpawnConstruct`). Misses BlueprintMod paste and remote-client-manual — but with the long variants stripped from every kit's `Constructables` list plus a join validator, those paths cannot produce a long variant, and an on-load sweep is the backstop. The `Constructables[0]`-is-the-single-tile-base convention that ZoopMod relies on is unaffected.
- All of {host manual, host multi-kit, ZoopMod, BlueprintMod paste, NetworkPuristPlus's on-load rebuild, `Cable.Break`} also reach `Cable.OnRegistered(Cell)`, so a **postfix there** (gated `GameManager.RunSimulation && GameManager.GameState != GameState.Loading && cable.IsStraight`) is the minimal roll-fix hook that fires for all of them and not for save-deserialize (`Cable.OnRegistered` itself early-returns when `GameState == Loading`) or joining clients — subject to the `RegisteredRotation`-already-snapshotted gotcha covered in the runtime-rotation section. Re-rolling a straight cable does not need a network re-stitch (connectivity is roll-blind) and `Cable.OnRegistered`'s merge body has already run when the postfix fires, so it does not fight the merge.

The construction messages carry the rotation losslessly (`ConstructionCreationMessage.WorldRotation`/`LocalRotation` and `CreateStructureMessage.Rotation` are 16-byte `ReadQuaternion`/`WriteQuaternion`, all public settable; `CreateStructureInstance.LocalRotation` is a public settable field). And NetworkPuristPlus's `World.OnLoadingFinished` postfix — which `Constructor.SpawnConstruct`s the single-tile replacements — runs *after* `GameManager.OnReadyToPlay()` has flipped `GameState` to `Running`, so a build-time hook gated on `GameState != Loading` (or `referenceId == 0L`) does process those replacements (re-rolling them to canonical, which is desired — NetworkPuristPlus's rebuild inherits the long piece's roll, it does not canonicalise it).

## Structure.Direction is reliably correct after every load path
<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

On every load path the `Structure`'s transform is set to the saved/serialized rotation by `UnityEngine.Object.Instantiate` *before* the structure is registered, and `Structure.OnAssignedReference` (which does `Direction = ThingTransform.rotation`) runs during that registration — so `Direction` ends up correct on every path, without needing `RebuildGridState`.

Fresh single-player save load (`WorldManager.LoadWorld` → `WorldManager.LoadThing`, `:251312`): `LoadThing` reads `worldRotation = structureSaveData.RegisteredWorldRotation` (`:251329`), calls `Thing.Create<Thing>(thing, worldPosition, worldRotation, thingData.ReferenceId)` (`:251331`), then `thing2.DeserializeSave(thingData)` (`:251337`). `Thing.Create<T>(Thing, Vector3, Quaternion, long)` (`:299915`): `UnityEngine.Object.Instantiate(thing, worldPosition, worldRotation)` (transform = saved rotation from frame 1), then `Referencable.RegisterAs(thing2, referenceId)` (`Referencable.cs:37610`) which ends with `thing.OnAssignedReference()` (`:37644`). `Structure.OnAssignedReference` (`:295611`): `Direction = ThingTransform.rotation;` (= the saved rotation) and `GridController.World.Register(this)`; `World.Register(Structure)` (`:191476`) sets `structure.RegisteredRotation = structure.ThingTransformRotation;` (`:191480`) — so `RegisteredRotation` is also restored here — then `AddSmallGridStructure(smallGrid)` (`:191868`) which ends with `smallGridObject.OnRegistered(null)` → `Cable.OnRegistered(null)` which skips `CableNetwork.Merge` during a load (`GameState == GameState.Loading`). Then back in `LoadThing`, `Structure.DeserializeSave` (`:295778`) → `Thing.DeserializeSave` (`:302201`) re-applies the same saved rotation (`ThingTransform.rotation = structureSaveData.RegisteredWorldRotation`, `:302207`; it does not touch `RegisteredRotation`/`Direction`, but `OnAssignedReference`/`World.Register` already set both) + build-state; `Cable.DeserializeSave` (`Cable.cs:371381`) re-attaches the cable to its saved `CableNetwork` by id. Net order: transform set by `Instantiate` → `OnAssignedReference` sets `Direction` and `RegisteredRotation` → `DeserializeSave` re-applies the same transform rotation. `Direction` = saved `RegisteredWorldRotation`.

Dedicated-server load: identical — the server runs the same `WorldManager.LoadWorld` → `LoadThing` path at `GameState.Loading`; being `RunSimulation == true` changes nothing about `Direction`.

Late-join replication: server `Thing.SerializeAllOnJoin` (`:303411`) → `NetworkServer.Serialize(writer, thing, ...)` → `Thing.SerializeOnJoin` (`:303031`) writes `PrefabHash` + `WriteTransform(writer)` (for `Structure`: `RegisteredPosition` + `RegisteredRotation`, `:295588`) + name/colour, then `Structure.SerializeOnJoin` (`:295594`) writes build state + `RegisteredLocalGrid` + `WriteQuaternion(Direction)`, then `Cable.SerializeOnJoin` (`:371399`) writes `CableNetworkId`. Joining client: `Thing.DeserializeNew` (`:303168`): `Create<Thing>(prefabHash, transformPosition, quaternion2, referenceId)` (so `RegisterAs` → `Structure.OnAssignedReference` → `Direction = ThingTransform.rotation` = the `WriteTransform` rotation = `RegisteredRotation`; `World.Register` → `RegisteredRotation = ThingTransformRotation`), then `DeserializeOnJoin(reader)` → `Thing.DeserializeOnJoin` (name/colour) then `Structure.DeserializeOnJoin` (`:295602`): `RegisteredLocalGrid = reader.ReadGrid3(); Direction = reader.ReadQuaternion();` (explicit re-set) then `Cable.DeserializeOnJoin` (`:371405`: reads `CableNetworkId`; skips `CableNetwork.Merge` because `GameState == GameState.Joining` & `RunSimulation == false` on a non-host client); then `SnapTransform(transformPosition, transformRotation)` (`:303179`). So a late-joining client sets `Direction` correctly via `OnAssignedReference` and again explicitly via `DeserializeOnJoin`; the visible mesh comes from `SnapTransform`'s `transformRotation` = the `WriteTransform` rotation = host's `RegisteredRotation` (which is why a runtime roll change must update `RegisteredRotation`, not just the transform).

`RebuildGridState` does **not** run on a normal load — it is the rocket-grid-moved re-snapshot path (`RocketGridController.RebuildAllGridState` ~`:176824` calls `structure.RebuildGridState()` for every structure on the grid) — and it is not needed for `Direction` correctness on load.

## No gameplay code reads Structure.Direction
<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

`Structure.Direction` (the `public Quaternion Direction;` field at `Structure.cs:295264`) is referenced at exactly five sites, all on `Structure` itself, none in a tick/gameplay path: `SerializeOnJoin` (`:295599`, join-package write), `DeserializeOnJoin` (`:295607`, join-package read), `OnAssignedReference` (`:295614`, re-derive from transform on register), `RebuildGridState` (`:295648`, re-derive from transform on grid re-snapshot), `SetStructureData` (`:297342`, set to the placement quaternion at construction). A whole-`Assembly-CSharp` grep for `.Direction` reads turns up only `Atmosphere.Direction` (a `Vector3` wind vector), `Connection.Direction`, `VeinDirectionData.Direction`, `PoweredVent`/`AdvancedComposter` `VentDirection`, light `LightInfo.Direction`, etc. — nothing reads `Structure.Direction` (or `Cable.Direction` / `Cladding.Direction` / `SmallGrid.Direction`). The cable-network code (`Connection.SetGrids`, `SmallGrid.IsConnected`/`Connected`/`FindConnectingEnd`, `Cable.WillJoinNetwork`/`_IsCollision`/`OnRegistered`/`OnDestroy`, `SmallGrid.CanConstruct`) derives everything from open-end child transforms (`Transform.position` / `Transform.forward`) and `Connection.ConnectionType` bitmasks — never from `Structure.Direction`. Classification: `Structure.Direction` is **network-sync-only** (it carries the placement quaternion verbatim across the late-join boundary; convenience-mirrored on register / rebuild / construct). A briefly-stale `Direction` is harmless for gameplay; it would only show if a late-join happened in the stale window (a late-joiner would see the stale roll until the next full state), which the runtime-rotation recipe above closes by setting `Direction` alongside the transform.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

- 2026-04-25: page created from a six-agent decompile pass investigating ceiling/wall placement of PowerTransmitter/PowerReceiver. All sections sourced from direct reads of `Structure.cs`, `InventoryManager.cs`, `Constructor.cs`, `MultiConstructor.cs`, `CreateStructureInstance.cs`, `ConstructionCreationMessage.cs`, `StructureSaveData.cs`. No conflicts with prior central pages.
- 2026-04-26: refined "Where rotation can be modified" R-key row and "Empirical floor-only" section after empirical testing surfaced the `RotationAxis` per-axis gate at `InventoryManager.cs:2443-2479`. Source: direct decompile re-read after a deployed patch with only `AllowedRotations = All` failed to enable wall placement in-game. Additive: documents a previously-implicit gate; `RotationAxis` is acknowledged as a separate prefab inspector value that, alongside `AllowedRotations`, must be lifted for non-floor placement.
- 2026-04-26: added "SmartRotate IndexOutOfRange under RotationAxis = All" section after empirical Player.log inspection surfaced an `IndexOutOfRangeException` in `Util.Permutation.Permute` every placement-mode frame for `ElectricalInputOutput`-derived prefabs once their `RotationAxis` is flipped to `All`. Source: direct read of `Util/SmartRotate.cs:246+` (the private static `_GetNext`), `Util/Permutation.cs`, and `Util/ExtensionMethods.cs:1870-1920` after the in-game crash. Additive: documents a previously-uncharacterized side effect of the prefab-mutation strategy.
- 2026-04-26: refined the "Mitigation strategies" list after empirical testing showed the initial "skip `_GetNext`" approach silently broke the player's C-key smart-rotate workflow (the `QuantityModifier` autoplace + scroll-cycle path in `InventoryManager.PlacementMode`). Promoted the takeover-with-90-deg-yaw approach to strategy 1; demoted the bare-skip approach to strategy 2 with the silent-input-loss caveat. PowerTransmitterPlus now uses strategy 1.
- 2026-04-26: restructured "Mitigation strategies" again after re-reading `SmartRotate.AutomaticSetup` (`Util/SmartRotate.cs:705`) and `ElectricalInputOutput.cs:28-30`. Found that the vanilla setup function takes the same `(PlacementSnap, RotationAxis, AllowedRotations)` triple that drives `GetOpenEndLocationPermutation` and produces the matching `OpenEndsPermutation` size and `ConnectionType` for any combination. Calling it after the field flips is the cleanest fix: it eliminates the Harmony patch entirely and hands the cursor cycle back to vanilla SmartRotate. Promoted the AutomaticSetup approach to strategy 1; demoted the takeover and skip approaches to strategies 2 and 3; added a new strategy 4 for hand-written field assignment as a finer-grained alternative. PowerTransmitterPlus now uses strategy 1.
- 2026-05-12: added "Straight pipe/cable/chute roll is an unconstrained cosmetic degree of freedom" after a three-agent investigation into a reported super-heavy-cable visual glitch (collinear `StructureCableSuperHeavyStraight` pieces with mismatched roll, red band misaligned; ~42% of straight-cable runs in the reporter's save affected). Sources: direct reads of `Cable`/`Pipe`/`Piping`/`SmallSingleGrid`/`SmallGrid`/`Connection` in `Assembly-CSharp.decompiled.cs` (0.2.6228.27061), `Util.SmartRotate` (`RotX/RotY/RotZ`, `ConnectionType` enum, `RotationsList`), and a decompile of `ZoopMod.dll` (Workshop 3310094883) — `SetStraightRotation`/`ApplyRotation`/`PositionSmallGridStructures` — and `BlueprintMod.dll` (3672138641) paste path. Cross-checked against this page's existing "SmartRotate IndexOutOfRange" and "Quaternion round-trip durability" sections; additive, no conflict (the IndexOutOfRange section is about flipping `RotationAxis` to `All` on devices; this one is about the cosmetic roll on straight small-grid pieces).
- 2026-05-12: corrected and expanded the "Straight pipe/cable/chute roll" section after a follow-up decompile pass on `SmartRotate.AutomaticSetup` / `_AutomaticInitial3DSetup` / `_SetPermutation`, `RotationsList`/`NumberOfUniqueOrientationsOf`, `MultiMergeConstructor.Construct`, `GetOpenEndLocationPermutation`, `_GetNext`/`_BestMatchCount`, `Permutation.Permute`, `CursorManager.PlacementMode`/`UpdatePlacement`/`HandleStructurePrefab`, and `Structure.Rotate`. **Correction:** the earlier text said "neither `Pipe` nor `Piping` declares an explicit `ConnectionType` field in the decompile" — that is wrong: `Pipe` and `Chute` each declare their own `[Header("ISmartRotation")] public SmartRotate.ConnectionType ConnectionType = SmartRotate.ConnectionType.Exhaustive;` (identical to `Cable`'s), and `Piping : Pipe` inherits it. Verified by a fresh validator pass over those classes; no other section affected. Findings: `AutomaticSetup` has zero callers (editor-bake only), so the C# `Exhaustive` default stands unless the prefab overrides it; the actual prefab `ConnectionType` for *straight* variants is undetermined from the decompile (flagged in Open Questions); `AutomaticSetup` *would* produce `ConnectionType.Straight` + `OpenEndsPermutation = {1,0,0,0,0,1}` for a straight piece (`RotationsList[Straight] = [RotX, RotY, RotZ]`, 3 orientations); a fresh-picked cable cursor starts at `Quaternion.identity` (= roll 0°), which does not equal ZoopMod's forced `Euler(0,0,90)` for a Z run; flipping `Cable.ConnectionType` to `Straight` *without* also setting `OpenEndsPermutation = {1,0,0,0,0,1}` makes `SmartRotate._GetNext` throw `KeyNotFoundException` every cursor frame; the merge math (`MultiMergeConstructor.Construct`, `GetOpenEndLocationPermutation`) keys off the `OpenEndsPermutation` *array* and `GetConnectionType()`, not just one of them, so a `ConnectionType` flip alone leaves the merge math unchanged but cross-prefab consistency in the cable kit's `Constructables` matters. Full dump in `.work/2026-05-12-cable-rotation/notes/connectiontype-findings.md`. Resolves Open Question "Where `Pipe` / `Piping` get their `ISmartRotatable.GetConnectionType()` value" (answer: a per-class Unity-serialized public field, C# default `Exhaustive`, no base, no `AutomaticSetup`); a residual open item remains about the *prefab-serialized* value for straight variants.
- 2026-05-12: added "Changing a placed Structure's rotation at runtime (save + multiplayer sync)", "Structure.Direction is reliably correct after every load path", and "No gameplay code reads Structure.Direction" after a decompile pass investigating runtime re-rolling of a placed straight `Cable`. Sources: direct reads of `Assembly-CSharp.decompiled.cs` (0.2.6228.27061) — `DynamicThing.MoveToWorld` (`:282518`/`:282626`) and the `MoveToWorld` overrides on `Entity`/`RobotMining`/`Flashlight`/`BodyArmor`/`Clothing`/`Suit`/`SuitBase` (confirming `Thing`/`Structure`/`SmallGrid`/`Cable` declare no `MoveToWorld`); `Thing` transform-mirror setters (`:298397-298463`), `Thing.NetworkUpdateFlags`/`SerializeDeltaState`/`BuildUpdate`/`BuildUpdateTransform`/`ProcessUpdateTransform` (`:299281`, `:303187-303394`) plus a whole-file grep of `NetworkUpdateFlags |= 1` sites; `Structure.WriteTransform`/`SerializeOnJoin`/`DeserializeOnJoin`/`OnAssignedReference`/`RebuildGridState`/`DeserializeSave`/`InitialiseSaveData`/`SetStructureData`/`OnRegistered`/`OnDeregistered` (`:295569-297360`, `:296914`, `:297021`); `Structure.Direction` field (`:295264`) and a whole-file grep of `.Direction` reads (only `Atmosphere.Direction` etc.); `SmallGrid.RebuildGridState`/`OnRegistered`/`OnDeregistered` (`:293590`, `:293739`, `:293793`); `Cable.OnRegistered`/`OnDestroy`/`DeserializeSave`/`SerializeOnJoin`/`DeserializeOnJoin` (`:371381-371559`); `WorldManager.LoadThing`/`LoadWorld` (`:251312`, `:251347`); `Thing.Create<T>` (`:299915`), `Referencable.RegisterNew`/`RegisterAs` (`:37571`, `:37610`); `GridController.World.Register(Structure)`/`AddSmallGridStructure` (`:191476`, `:191868`); `Constructor.SpawnConstruct` (`:276940`); `RocketGridController.RebuildAllGridState` (`:176824`). Resolves the first two Open Questions (`OnAssignedReference` fires during `Thing.Create` → `RegisterAs`, *before* `DeserializeSave`, with the transform already at the saved/serialized rotation, so `Direction` is correct on every load path; and no tick/gameplay code reads `Structure.Direction` — it is a network-sync-only field). Additive; cross-checked against the existing "Quaternion round-trip durability", "Direction vs RegisteredRotation vs ThingTransform.rotation", and "Straight pipe/cable/chute roll" sections — corrects the stray `Thing.MoveToWorld` mention in the latter (no such method; it is `DynamicThing.MoveToWorld`, which is the inventory-eject op and a no-op for a placed `Structure`). Full dump in `.work/2026-05-12-cable-rotation/notes/mutation-findings.md`.
- 2026-05-12: added "Build-time structure-creation chokepoints (and the load/build discriminator)" after a decompile pass tracing where placed `Structure`s are created host-side, for a build-time JIT fix (rewrite a straight cable's roll, or replace a long multi-tile straight variant with N single tiles, as it is built — covering vanilla manual/multi-kit builds and ZoopMod / BlueprintMod). Sources: direct reads of `Assembly-CSharp.decompiled.cs` (0.2.6228.27061) — `Constructor.OnUsePrimary`/`Construct`/`SpawnConstruct` (`:276921-276953`), `CreateStructureInstance` (`:263337`), `MultiConstructor.Construct` (`:288267-288292`)/`MultiMergeConstructor.Construct` (`:288338`), `OnServer.UseMultiConstructor` (`:40078`) and the `OnServer.Create<T>` overloads (`:39479-39533`), `InventoryManager.UsePrimaryComplete` (`:271013`), `ConstructionCreationMessage.Process` (`:258325`) / `CreateStructureMessage.Process` (`:258220`), `Thing.Create<T>(Thing,Vector3,Quaternion,long)` (`:299915`) / `Thing.DeserializeNew` (`:303168`), `Referencable.RegisterNew`/`RegisterAs` (`:37571`/`:37610`), `WorldManager.LoadThing` (`:251312`) / `XmlSaveLoad.LoadWorld` (`:251347`, with `GameManager.OnReadyToPlay()` / `GameState = Running` at `:251635` / `:198160` and `World.OnLoadingFinished` at `:251637`), `GameManager.RunSimulation` (`:188999`) / `GameState` enum (`:272369`), `Cable.OnRegistered` (`:371477`) / `Cable.Break` (`:371424`); plus decompiles of `ZoopMod.dll` (Workshop 3310094883) — `ZoopBuildExecutor.BuildAll` replaying preview transforms into `InventoryManager._usePrimaryRotation` → `UsePrimaryComplete` → `OnServer.UseMultiConstructor` / `CreateStructureMessage` — and `BlueprintMod.dll` (Workshop 3672138641) — `CreateSingleEntry` → `OnServer.Create<Thing>(prefabName, worldPos, worldRot)`. Findings: every host-side fresh-creation path funnels through `Thing.Create<T>(…, long referenceId)` and passes `referenceId == 0L` (save-load and join replication pass non-zero), so `referenceId == 0L` is the build-vs-load discriminator (equivalently `GameState == GameState.Loading` for the host save loop, `RunSimulation` for joining clients); `Constructor.SpawnConstruct(CreateStructureInstance)` catches vanilla manual (host) + vanilla multi-kit (host + remote-client via `CreateStructureMessage`, which re-runs `Construct` host-side) + ZoopMod (host + remote-client) but not BlueprintMod paste or the remote-client-manual `ConstructionCreationMessage` path (which uses `OnServer.Create<Structure>` directly); for full coverage a `Thing.Create<T>` prefix (gated `referenceId == 0L`) is the single chokepoint; `OnRegistered`'s `RegisteredRotation` snapshot happens *inside* `Thing.Create` before `SetStructureData`, so a rotation rewrite done in a `SpawnConstruct`/`Thing.Create` prefix is consistent without after-fixup whereas a `Cable.OnRegistered` postfix must repair `RegisteredRotation`/`Direction` (as in the runtime-rotation recipe above); NetworkPuristPlus's `World.OnLoadingFinished` postfix `SpawnConstruct`s its single-tile replacements *after* `GameState` flips to `Running`, so a `GameState != Loading` / `referenceId == 0L` build hook does process them. Additive; cross-checked against "Changing a placed Structure's rotation at runtime", "Structure.Direction is reliably correct after every load path", "Quaternion round-trip durability", and "Pipeline overview" — no conflict (those cover the placement cursor and the runtime-mutation/load mechanics; this section enumerates the build-time creation chokepoints and which Harmony hook catches which path). Full path-by-path analysis, candidate-hook comparison table, and line-index in `.work/2026-05-12-cable-rotation/notes/chokepoint-findings.md`.

## Open questions

- What the *straight* pipe / cable / chute prefabs actually serialize for `ConnectionType`. The decompile only shows the C# field initializer (`Exhaustive`); the Unity prefab serialization (`sharedassets0.assets` / `resources.assets`) can override it and is not visible there. The reporter's ~17% roll-mixed straight-*pipe* runs are consistent with either `Straight` or `Exhaustive` on the prefab (ZoopMod's direct `transform.rotation` write and the plain R-key roll mix rolls regardless). Resolve by dumping the prefab from the asset bundle, or via an InspectorPlus snapshot of `pipe.ConnectionType` (request: `types=[Pipe]` or `[Cable]` or `[Chute]`, `fields=[ConnectionType, OpenEndsPermutation, IsStraight]`) on a placed straight piece in-game.
