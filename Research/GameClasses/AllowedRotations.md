---
title: AllowedRotations
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-25
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.AllowedRotations
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Structure (field at line 135, consumers at lines 1719-1739, 2199)
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Inventory.InventoryManager.UpdatePlacement (lines 1698-1750)
related:
  - ./Structure.md
  - ./InventoryManager.md
  - ./Constructor.md
  - ../GameSystems/PlacementOrientation.md
tags: [prefab, transforms]
---

# AllowedRotations

Vanilla `[Flags]` enum at `Assets.Scripts.Objects.AllowedRotations` controlling which surface classes a `Structure` may be placed against (floor, wall, ceiling). Consumed at the placement-cursor stage and at runtime rotation. Set per-prefab in the Unity inspector; no code overrides exist on built-in subclasses.

## Enum values
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

```csharp
[Flags]
public enum AllowedRotations
{
    None = 0,
    Wall = 1,
    Ceiling = 2,
    Floor = 4,
    Vertical = 6,    // Floor | Ceiling
    All = 7          // Floor | Ceiling | Wall
}
```

`Vertical` (6) means "the device stands vertical" - it accepts floor or upside-down ceiling, but not a wall. Despite the name, it is NOT "wall mount only."

## Field declaration
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

The field lives on `Structure` and is inspector-serialized on every prefab.

`Assets/Scripts/Objects/Structure.cs:135`:

```csharp
public AllowedRotations AllowedRotations = AllowedRotations.All;
```

The C# default initializer is `All`. Unity prefab serialization overrides this on a per-prefab basis (the inspector value baked into `sharedassets0.assets` / `resources.assets` is what runtime sees). A grep of the entire decompiled assembly finds NO subclass that re-declares the field nor any code site that assigns it; every per-prefab restriction is set via the Unity prefab inspector and travels in the asset bundle.

A getter exists but is non-virtual:

`Structure.cs:2182-2185`:

```csharp
public AllowedRotations GetAllowedRotations()
{
    return AllowedRotations;
}
```

Subclasses cannot override the getter without going through `new` shadowing (which Harmony cannot trivially intercept).

## Consumer 1: placement-cursor surface gate (InventoryManager.UpdatePlacement)
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

Source: `Assets/Scripts/Inventory/InventoryManager.cs` lines 1719-1739 (within `UpdatePlacement(Structure)`).

```csharp
CurrentFace = RocketGrid.FaceInt.FaceIntFromDir(RocketGrid.GetForwardDir(ConstructionCursor.ThingTransform.forward));
if ((ConstructionCursor.AllowedRotations & AllowedRotations.Wall) == 0 && RocketGrid.FaceInt.IsHorizontalFace(CurrentFace))
{
    if ((ConstructionCursor.AllowedRotations & AllowedRotations.Ceiling) != AllowedRotations.None)
    {
        CurrentFace = RocketGrid.FaceInt.Up;
        ConstructionCursor.ThingTransform.rotation = new Quaternion(1f / Mathf.Sqrt(2f), 0f, 0f, 1f / Mathf.Sqrt(2f));
    }
    else
    {
        CurrentFace = RocketGrid.FaceInt.Down;
        ConstructionCursor.ThingTransform.rotation = new Quaternion(-1f / Mathf.Sqrt(2f), 0f, 0f, 1f / Mathf.Sqrt(2f));
    }
}
else if ((ConstructionCursor.AllowedRotations & AllowedRotations.Ceiling) == 0 && CurrentFace == RocketGrid.FaceInt.Up)
{
    CurrentFace = RocketGrid.FaceInt.South;
    ConstructionCursor.ThingTransform.rotation = Quaternion.identity;
}
else if ((ConstructionCursor.AllowedRotations & AllowedRotations.Floor) == 0 && CurrentFace == RocketGrid.FaceInt.Down)
{
    CurrentFace = RocketGrid.FaceInt.South;
    ConstructionCursor.ThingTransform.rotation = Quaternion.identity;
}
```

The gate is **auto-correcting**, not validating: when the player aims at a surface whose face class is not in the prefab's allowed mask, `UpdatePlacement` rewrites `CurrentFace` and `ConstructionCursor.ThingTransform.rotation` to the nearest allowed surface. It does NOT block the click; it silently snaps to a permitted orientation.

Implication: `AllowedRotations` is the only switch this method consults. Setting it to `All` on the construction cursor at any time before `UpdatePlacement` reads it is sufficient to unlock wall + ceiling placement.

## Consumer 2: free-axis runtime rotation (Structure.Rotate)
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

Source: `Assets/Scripts/Objects/Structure.cs:2185-2235`.

```csharp
public void Rotate(Vector3 axis, float angle, Quaternion offset, Vector3 centerOfRotation)
{
    switch (PlacementType)
    {
    case PlacementSnap.Grid:
    case PlacementSnap.Face:
        switch (RotationAxis)
        {
        case RotationAxis.XY:
        case RotationAxis.ZX:
        case RotationAxis.ZY:
        case RotationAxis.All:
            if (AllowedRotations != AllowedRotations.Floor)
            {
                Quaternion quaternion3 = Quaternion.AngleAxis(angle, axis);
                (offset * quaternion3 * Quaternion.Inverse(offset)).ToAngleAxis(out angle, out axis);
                ThingTransform.RotateAround(centerOfRotation, axis, angle);
                break;
            }
            goto case RotationAxis.Y;
        ...
```

**Special case for exactly `AllowedRotations.Floor`**: when the prefab is floor-only AND `RotationAxis` permits multi-axis rotation, `Rotate` falls through to `case RotationAxis.Y`, restricting actual rotation to the Y axis only. Any other `AllowedRotations` value (`Wall`, `Ceiling`, `Vertical`, `All`, or any combination including `Floor` plus another flag) takes the multi-axis branch.

Implication: changing a prefab's `AllowedRotations` from the literal value `Floor` to anything else (e.g., `All`) immediately unlocks the multi-axis rotation branch in `Rotate`, which the placement cursor invokes during R-key handling.

## Vanilla examples (by class hierarchy)
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

By code analysis (no UnityPy verification yet of prefab-baked values, see Open Questions), the following classes are clearly intended to support non-floor placement, and their prefabs are expected to carry `AllowedRotations.All` or `AllowedRotations.Wall`:

- `WallLight` / `FlashingLight` / `Diode` / `WallLightBattery` (wall fixtures)
- `Door` / `RoboticArmDoor` (doorways with hinged geometry)
- `Locker`, `Seat`, `Bench`, `Shelf` (`ISmartRotatable` furniture pieces)
- Any class implementing `ISmartRotatable` whose `GetConnectionType()` returns a non-floor `SmartRotate.ConnectionType`

Classes that empirically place floor-only (per in-game observation) but carry no field override in code, hence are restricted via prefab inspector value:

- `PowerTransmitter` (`StructurePowerTransmitter` prefab) - presumed `AllowedRotations.Floor`
- `PowerReceiver` (`StructurePowerTransmitterReceiver` prefab) - presumed `AllowedRotations.Floor`

## Patching strategy for unlocking non-floor placement
<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

To enable wall and/or ceiling placement on a prefab whose value is currently `Floor`, mutating `AllowedRotations` ALONE is necessary but not sufficient. The cursor R-key handler at `InventoryManager.cs:2443-2479` is gated independently per axis by `Structure.RotationAxis` (default `RotationAxis.All` in code, but typically baked to `Y` in the prefab inspector for floor-only devices). With `RotationAxis = Y`, the player can yaw the cursor but cannot pitch or roll it onto a wall or ceiling face, regardless of `AllowedRotations`. Both fields must be lifted:

```csharp
prefab.AllowedRotations = AllowedRotations.All;
prefab.RotationAxis = RotationAxis.All;
```

Strategies (apply both fields, the same way, in the same place):

1. **Mutate the fields on the SourcePrefab once at load time.** Patch `Prefab.LoadAll` (Postfix) or `WorldManager.SourcePrefabs` registration: locate the prefab by name, assign both fields. The construction cursor is cloned from `SourcePrefabs` in `InventoryManager.SetupConstructionCursors`, so the new values flow to the cursor automatically.

2. **Or mutate the construction cursor only.** Patch `InventoryManager.SetupConstructionCursors` (Postfix): walk `_constructionCursors` dictionary, find the cursor for the prefab name, set both fields on the cursor. Leaves the placed Structure's runtime fields at the prefab's original baked values, but the cursor's values are what `UpdatePlacement` and the R-key handler read.

3. **Or mutate per-call.** Patch `InventoryManager.UpdatePlacement` (Prefix) and the cursor R-key handler with type-guarded field writes. Most invasive; not recommended.

Strategy 1 is the most idiomatic (matches the Mirrored Devices recipe in `Patterns/PrefabCloning.md`). It changes the values once per game session and persists for both placement and runtime `Rotate` consumers. PowerTransmitterPlus's `PlacementPatcher.cs` uses Strategy 1 plus a defensive Strategy-2 walk to handle the case where `SetupConstructionCursors` has already run by the time `OnAllModsLoaded` fires.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

- 2026-04-25: page created from a six-agent decompile pass investigating the placement-orientation gate for PowerTransmitter / PowerReceiver. Consumer 1 and Consumer 2 sites confirmed by direct read of decompiled `Structure.cs` and `InventoryManager.cs`. No conflicts with prior pages. Note: existing claim on `PowerTransmitter.md` ("Placement: floor-only, four cardinal rotations. `root.up = world.up` always.") is consistent with `AllowedRotations.Floor` baked into the prefab; the floor-only behavior is a per-prefab inspector setting, not a hardcoded code rule.
- 2026-04-26: refined "Patching strategy" to call out that `AllowedRotations` is one of two prefab inspector values that gate non-floor placement; `Structure.RotationAxis` (`InventoryManager.cs:2443-2479`) gates the cursor R-key handler per axis independently and must be lifted alongside `AllowedRotations` for the player to actually be able to pitch / roll the cursor onto a non-floor face. Discovered empirically: a deployed patch that flipped only `AllowedRotations` produced clean startup logs and removed the cursor face auto-correct, but the player could not pitch the cursor at all because RotateUp / RotateDown remained gated by `(RotationAxis & X) != None`. Additive update; no prior verified content was contradicted.

## Open questions

- The actual `AllowedRotations` value on the `StructurePowerTransmitter` and `StructurePowerTransmitterReceiver` prefabs is not yet directly verified via UnityPy. The empirical floor-only behavior implies `AllowedRotations.Floor`, but no extraction has confirmed this. Resolution: a future UnityPy pass (or runtime InspectorPlus snapshot of the SourcePrefabs entry) should record the literal value.
