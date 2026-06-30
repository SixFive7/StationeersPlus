---
title: InputHelpers.GetCameraForwardGrid (construction-cursor placement raycast)
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-30
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Util.InputHelpers.GetCameraForwardGrid
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 215261-215270 (GetCameraForwardGrid body), 270377 / 270926 (InventoryManager construction-cursor call sites)
  - StationeersCommunityMods/LibConstruct (LibConstructReloaded) PlacementBoardPatches.Construction.cs, commit 38ea9ed5 "Fix for latest Stationeers beta" (2026-06-14)
related:
  - ./CursorAdjacencyLookup.md
  - ../GameSystems/PlacementOrientation.md
  - ../GameClasses/Device.md
  - ../GameSystems/StructurePlacementValidation.md
tags: [prefab, ui, transforms]
---

# InputHelpers.GetCameraForwardGrid

The static helper that turns the player camera ray into the world-space point where the construction cursor ghost should sit. Any mod that drives or overrides the construction cursor (custom placement grids, board placement, snapping) calls this to reproduce the vanilla cursor position before applying its own offset, so its signature is a cross-version compatibility surface for construction mods.

## Signature and body (installed version)
<!-- verified: 0.2.6228.27061 @ 2026-06-30 -->

`Assets.Scripts.Util.InputHelpers.GetCameraForwardGrid` in the installed game (0.2.6228.27061) takes TWO arguments and returns the hit point (or a fallback point along the ray at max range):

```csharp
public static Vector3 GetCameraForwardGrid(float range, float offset)   // line 215261
{
    Ray cameraRay = GetCameraRay();
    float num = RocketGrid.GridLength * range + offset;
    if (!Physics.Raycast(cameraRay, out var hitInfo, num, CursorManager.Instance.CursorHitMask))
    {
        return cameraRay.GetPoint(num);
    }
    return hitInfo.point;
}
```

- `range` is scaled by `RocketGrid.GridLength` and added to `offset` to get the raycast distance `num`.
- The raycast uses `CursorManager.Instance.CursorHitMask`.
- On a miss it returns `cameraRay.GetPoint(num)` (a point in mid-air at distance `num`); on a hit it returns `hitInfo.point`.
- The `out RaycastHit hitInfo` is internal to the method: in this version it is NOT exposed to the caller.

## Vanilla call sites
<!-- verified: 0.2.6228.27061 @ 2026-06-30 -->

Both vanilla call sites are in `InventoryManager` construction-cursor code and use the two-argument form, passing a range that depends on the cursor grid size and the cursor's own offset:

```csharp
// line 270377
Vector3 cameraForwardGrid = InputHelpers.GetCameraForwardGrid(
    (ConstructionCursor.GridSize > 0.5f) ? 0.3f : 0.6f, ConstructionCursor.GetCursorOffset);

// line 270926
Vector3 vector = InputHelpers.GetCameraForwardGrid(
    (ConstructionCursor.GridSize > 0.5f) ? 0.3f : 0.6f, ConstructionCursor.GetCursorOffset)
    .GridCenter(ConstructionCursor.GridSize, ConstructionCursor.GridOffset);
```

A construction mod that recomputes the base cursor position copies this exact pattern. Example (LibConstruct / LibConstructReloaded `PlacementBoardPatches.Construction.cs`, which reproduces the vanilla base cursor placement before clearing the board association):

```csharp
InventoryManager.ConstructionCursor.ThingTransformPosition =
    InputHelpers.GetCameraForwardGrid(0.6f, InventoryManager.ConstructionCursor.GetCursorOffset);
```

## Cross-version note: a third out-parameter was added on a later build
<!-- verified: 0.2.6228.27061 @ 2026-06-30 -->

This signature is a known break point for construction mods. In the installed build 0.2.6228.27061 the method is two-arg `(float range, float offset)`. On a LATER Stationeers build (the beta that became the basis for the next stable update), the signature gained a third `out` parameter; the same LibConstruct call had to change from

```csharp
InputHelpers.GetCameraForwardGrid(0.6f, ConstructionCursor.GetCursorOffset)
```

to

```csharp
InputHelpers.GetCameraForwardGrid(0.6f, ConstructionCursor.GetCursorOffset, out var _)
```

in the LibConstructReloaded commit `38ea9ed5` "Fix for latest Stationeers beta" (2026-06-14). The newly returned value is presumed to be the `RaycastHit` (or the hit surface) that the two-arg version discarded internally, but the exact type/meaning on the newer build is not verified here (the installed DLL still has the two-arg form). Because C# resolves this by overload signature, a mod compiled against one form throws / fails to bind against the other: this is a hard compile-and-load-time break, not a behavioral drift. A mod that calls `GetCameraForwardGrid` cannot be simultaneously binary-compatible with both the two-arg and three-arg game builds without reflection or per-version conditional compilation.

## Verification history

- 2026-06-30: page created. Sourced from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` lines 215261-215270 (method body) and 270377 / 270926 (InventoryManager call sites). The cross-version out-parameter break was surfaced while investigating why the LibConstruct / Modular Consoles mods need a new build for the upcoming Stationeers stable update: LibConstructReloaded commit 38ea9ed5 (2026-06-14) changes the call from the two-arg form (matching the installed 0.2.6228.27061 DLL) to a three-arg `out var _` form for "the latest Stationeers beta." The installed DLL was not yet on the breaking build, so the three-arg signature itself is documented as observed-from-the-mod-diff, not from the installed decompile.

## Open questions

- The exact type and semantics of the third (`out`) parameter on the newer build (the beta that became the next stable update). Presumed to be the `RaycastHit` that the two-arg version computes internally and discards, but this is not confirmed against a decompile of the newer DLL. Re-decompile under the new game version when it is installed and restamp.
