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

## Two placement families: Panel vs Angled
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

Cladding ships in two distinct placement families that register through different registries. Both families share the base `Cladding` C# class, so `is Cladding` catches both, but the spatial walk to enumerate cladding pieces has to account for both placement modes.

| Family | Example prefabs | `PlacementType` | `StructureCollisionType` | `BlockingGrids` vs `GridPosition` | Engine registry |
|---|---|---|---|---|---|
| Face cladding (panels) | `StructureCompositeCladdingPanel`, More Cladding Mod's `StructureMoreCladdingAngledPanel` | `Face` | `BlockFace` | One `Grid3` offset by `±10` on one axis from `GridPosition` (the mounted-face direction) | `GridController.FaceLookup` |
| Body cladding (angled) | `StructureCompositeCladdingAngled`, `StructureCompositeCladdingAngledLong`, `StructureCompositeCladdingAngledCornerLong*`, `*Angled7Long`, `*Angled8Long` | `Grid` | `BlockGrid` | One `Grid3` equal to `GridPosition` and `RegisteredLocalGrid` | `Cell.Structural` (body slot) |

The `Bounds` shape differs too:

- Face cladding: `Bounds.center = (0, 0, 0.065)`, `Bounds.size = (2, 2, 0.13)`. A thin slab offset toward the mounted face. `CenterPosition = ThingTransformPosition + rotation * Bounds.center` places the registration key on the face.
- Body cladding: `Bounds.center = (0, 0.065, 0)`, `Bounds.size = (2, 2.13, 2)`. A full-cell volume with a small Y offset.

For a feature that wants to "find all connected cladding", the simplest unified walk is to iterate `Cell.AllStructures` (which contains everything at a cell regardless of registration mode) and filter `is Cladding`. Walking only `FaceLookup` would miss body angled pieces; walking only `Cell.Structural`'s body slot would miss face panels.

The face-keyed `BlockingGrids` value is the practical primitive for "which face does this panel mount on": for a face panel at `GridPosition = (-12930, 2070, -7390)` with `BlockingGrids = [(-12930, 2070, -7400)]`, the difference `(0, 0, -10)` identifies the `-Z` face of that 2 m cell.

## Runtime verification of paintability
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

Runtime InspectorPlus snapshots of placed cladding on a dedicated server loading the Luna save resolved the three open questions present at page creation:

- **`structureRenderMode: "Standard"`** for every sampled cladding instance (vanilla panel, vanilla angled, mod angled panel). So `Structure.SetCustomColor` does NOT throw `NotImplementedException` on cladding; the per-instance paint path works. The batched-render risk flagged on `./Structure.md` does not apply to cladding.
- **`IsPaintable: true`** and **`PaintableMaterial: (Material) ColorOrange`** for every instance, vanilla and mod.
- **`CustomColor` already populated**: every sampled cladding had `CustomColor.IsSet = true` with `Name = "ColorBlack", Index = 7`, i.e. prior paint applications had succeeded and persisted into the save. This is the strongest possible evidence the paint primitive works on cladding.

For the mod cladding specifically, runtime `_type` on a placed instance was `morecladdingmod.StructureCladding`, a sibling of vanilla `Cladding` by inheritance (not by identity). `FindObjectsOfType(typeof(Cladding))` is polymorphic over the subclass, so a `types: ["Cladding"]` snapshot returns both vanilla and mod instances mixed. An `is Cladding` test in code detects both; a `GetType() == typeof(Cladding)`-equality test (as the SprayPaintPlus large-structure flood currently uses for other categories) would split vanilla and mod into separate groups.

The More Cladding Mod (Steam Workshop 3140312559) decompile (`MoreCladdingMod.dll`, 9.5 KB) confirms the inheritance:

```csharp
namespace morecladdingmod
{
    public class StructureCladding : Cladding, IPatchOnLoad
    {
        public void PatchOnLoad()
        {
            ((Thing)(object)this).ApplyPaintableParent((ColorType)3);
            ((Thing)(object)this).ApplyBlueprintMaterials();
            ((Structure)(object)this).SetExitTool(PrefabPatcher.ExitTool);
        }
    }
    ...
    public static class PrefabExtensions
    {
        ...
        private static void ApplyPaintable(Thing thing, MeshRenderer renderer, ColorType color)
        {
            Material normal = Singleton<GameManager>.Instance.CustomColors[(int)color].Normal;
            Material[] sharedMaterials = ((Renderer)renderer).sharedMaterials;
            if (sharedMaterials.Length != 0)
            {
                sharedMaterials[0] = normal;
                ((Renderer)renderer).sharedMaterials = sharedMaterials;
                thing.PaintableMaterial = normal;
            }
        }
    }
}
```

The mod writes `thing.PaintableMaterial = normal` in `PatchOnLoad`, so its cladding is paintable by deliberate design, not accident.

Verified via InspectorPlus on 2026-05-22 in game version 0.2.6228.27061. Requests: types=[Cladding], maxMonoBehaviours=3-10, includePrivate=true, maxDepth=2-3, with field filters on the topology and paint-related members. Dedicated-server side, with `Force Unpause Without Client = true` in `BepInEx/config/net.inspectorplus.cfg` so requests process headless.

## Verification history

- 2026-05-22: page created from a feasibility study on networking composite cladding for SprayPaintPlus. The full class is verbatim from `Assets.Scripts.Objects.Cladding` in `Assembly-CSharp`, game version 0.2.6228.27061. New page; no conflicts.
- 2026-05-22: resolved all three open questions present at page creation. Mod-cladding class identity confirmed via decompiling `MoreCladdingMod.dll` (Steam Workshop 3140312559) to `morecladdingmod.StructureCladding : Cladding, IPatchOnLoad`; runtime `_type` on a placed instance matched. Face collision type and per-instance paintability confirmed via InspectorPlus snapshots on a headless dedicated server with the Luna save loaded: every sampled cladding has `structureRenderMode: Standard`, `IsPaintable: true`, `PaintableMaterial: ColorOrange`, and `CustomColor` already populated (proof prior paint applications succeeded and persisted into the save). Bonus finding: cladding ships in TWO placement families (face panels with `PlacementType: Face` / `StructureCollisionType: BlockFace`, registered in `FaceLookup`; body angled pieces with `PlacementType: Grid` / `StructureCollisionType: BlockGrid`, registered in `Cell.Structural`). Added "Two placement families: Panel vs Angled" and "Runtime verification of paintability" sections.

## Open questions

None remaining. The three open questions present at page creation (mod-cladding class identity, face collision type, per-instance paintability) were resolved on 2026-05-22 via the More Cladding Mod decompile and InspectorPlus snapshots of placed cladding on a dedicated server with the Luna save loaded. See the "Two placement families: Panel vs Angled" and "Runtime verification of paintability" sections above for the findings, and the verification-history entry for the same date.
