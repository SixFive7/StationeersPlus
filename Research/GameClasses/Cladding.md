---
title: Cladding
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-22
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Cladding
related:
  - ./Structure.md
  - ./Wall.md
  - ./Grid3.md
  - ./GridController.md
  - ../GameSystems/PlacementOrientation.md
  - ./MultiMergeConstructor.md
tags: [prefab, transforms]
---

# Cladding

Face-mounted panel structure (composite cladding, iron / glass cladding, and similar surfacing). `Cladding : Structure, ISmartRotatable` in `Assets.Scripts.Objects`. It is a 2 m large-grid `Structure`, a sibling of `Wall` / `Frame` under `Structure`, NOT a `SmallGrid`. It carries no connection or network model, so painting or grouping cladding has to be derived from the grid, not from a network object.

## Full class
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

```csharp
public class Cladding : Structure, ISmartRotatable
{
    [Header("ISmartRotation")]
    public SmartRotate.ConnectionType ConnectionType = SmartRotate.ConnectionType.Exhaustive;

    public int[] OpenEndsPermutation = new int[6] { 0, 1, 2, 3, 4, 5 };

    public override Vector3 CenterPosition => base.ThingTransformPosition + ThingTransform.rotation * Bounds.center;

    protected override float GetRenderMaxDistanceSquared()
    {
        return Mathf.Pow(100f * OcclusionManager.RenderDistanceMultiplier, 2f);
    }

    protected override float GetShadowMaxDistanceSquared()
    {
        return Mathf.Pow(60f * Settings.CurrentData.ThingShadowDistanceMultiplier, 2f);
    }

    public override Grid3 GetLocalGrid()
    {
        return base.GridController.WorldToLocalGrid(CenterPosition, GridSize, GridOffset);
    }

    public override string GetStationpediaCategory()
    {
        return Localization.GetInterface(StationpediaCategoryStrings.WallFloorCategory);
    }

    public SmartRotate.ConnectionType GetConnectionType()
    {
        return ConnectionType;
    }

    public void SetOpenEndsPermutation(int[] permutation)
    {
        OpenEndsPermutation = (int[])permutation.Clone();
    }

    public void SetConnectionType(SmartRotate.ConnectionType connectionType)
    {
        ConnectionType = connectionType;
    }

    public int[] GetOpenEndsPermutation()
    {
        return (int[])OpenEndsPermutation.Clone();
    }

    public List<Connection> GetOpenEnds()
    {
        return new List<Connection>(0);
    }

    public int ConnectedCount()
    {
        return 0;
    }

    public int GetOpenEndsCount()
    {
        return 0;
    }

    public float GetGridSize()
    {
        return 2f;
    }
}
```

## No network, no open ends
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

`GetOpenEnds()` returns an empty list; `ConnectedCount()` and `GetOpenEndsCount()` both return `0`. There is no `CladdingNetwork`, no `PipeNetwork` / `CableNetwork` / `ChuteNetwork` analogue. Unlike pipes, cables, chutes, rails, and elevators (which expose a server-maintained member list), connected cladding has no engine-maintained set. Any "which cladding pieces go together" question is spatial and must be answered by walking the grid (`GridController.GetFaceStructures`, or `GetCell` + `Cell.AllStructures` + `Cell.NeighborCells`) or by room membership (`RoomController.GetRoom`). See `./GridController.md` and `./RoomController.md`.

The `ISmartRotatable` members (`ConnectionType`, `OpenEndsPermutation`, `GetConnectionType`, and the setters) drive placement auto-rotation only; they are not a runtime connectivity graph.

## Grid registration and the face model
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

`GetGridSize()` returns `2f` (the large grid, same pitch as walls). Cladding mounts on a cell face, and its registration key is offset toward that face by the bounds center:

```csharp
public override Vector3 CenterPosition => base.ThingTransformPosition + ThingTransform.rotation * Bounds.center;

public override Grid3 GetLocalGrid()
{
    return base.GridController.WorldToLocalGrid(CenterPosition, GridSize, GridOffset);
}
```

`Bounds.center` is offset toward the mounted face, so two cladding panels on opposite faces of one wall resolve to distinct grid keys (their bounds centers fall on opposite sides of the cell boundary). The panel registers into `Cell.Structural` (face-keyed) and `GridController.FaceLookup` through the normal `Structure` registration path, so one 2 m cell can hold up to six cladding faces. `GetStationpediaCategory()` returns the Wall/Floor category, confirming cladding is treated as wall-family surfacing.

## Verification history

- 2026-05-22: page created from a feasibility study on networking composite cladding for SprayPaintPlus. The full class is verbatim from `Assets.Scripts.Objects.Cladding` in `Assembly-CSharp`, game version 0.2.6228.27061. New page; no conflicts.

## Open questions

- **Mod-added cladding class identity.** A research pass decompiling the More Cladding Mod (Steam Workshop 3140312559) reported its cladding type as `morecladdingmod.StructureCladding : Cladding`, a subclass of vanilla `Cladding`. If correct, an `is Cladding` test detects both vanilla and that mod's cladding, while a `GetType()`-equality test (as the SprayPaintPlus large-structure flood uses) would split them into separate groups. Not re-verified against the mod DLL on this pass; confirm by decompiling `MoreCladdingMod.dll` before relying on it for detection.
- **Face collision type.** Cladding's prefab-baked `StructureCollisionType` (expected to be a face type, which is what places each face as a distinct `Structural` / `FaceLookup` key) is not visible in the C# decompile; it lives in the Unity prefab asset. Confirm at runtime via InspectorPlus. Request: types=[Cladding], fields=[StructureCollisionType, PlacementType, BlockingGrids, GridPosition].
- **Per-instance paintability.** Whether `Cladding.SetCustomColor` succeeds or throws `NotImplementedException` (batched-render structures share a combined mesh; see `./Structure.md`) is untested. If cladding is batched-render, both single-piece and any future network painting silently no-op. Verify by painting one panel with a vanilla spray can.
