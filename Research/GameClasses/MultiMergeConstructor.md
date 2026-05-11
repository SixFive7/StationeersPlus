---
title: MultiMergeConstructor
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-11
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.MultiMergeConstructor
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.Piping
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.Cable
  - $(StationeersPath)\rocketstation_Data\StreamingAssets\Language\english.xml
related:
  - ./MultiConstructor.md
  - ./Constructor.md
  - ../GameSystems/PlacementOrientation.md
tags: [prefab, network]
---

# MultiMergeConstructor

`MultiMergeConstructor : MultiConstructor` at `Assets.Scripts.Objects.MultiMergeConstructor`. The held-item kit form used by `ItemKitPipe` ("Kit (Pipe)"), `ItemKitChute` ("Kit (Basic Chutes)"), and the Super-Heavy cable kit. It is a regular `MultiConstructor` whose `Constructables` list includes both the 1-cell straight piece and the multi-cell "long" straight variants (3, 5, 10 segments), plus an extra `ToolExit` item that, when held in the off hand, lets the kit *merge over* an existing straight run instead of refusing placement.

This page exists because someone asked "what are the long pieces in Stationeers and where do they come from in the code." Short answer: there is no per-length C# class; the long variants are extra prefabs of the ordinary `Pipe` / `Piping` / `Cable` (and, for chutes, `Chute`) classes whose mesh/collider footprint spans N small-grid cells, registered into a `MultiMergeConstructor` kit's `Constructables` list.

## The "long" straight-piece prefab family
<!-- verified: 0.2.6228.27061 @ 2026-05-11 -->

From `StreamingAssets\Language\english.xml` (`<RecordThing>` keys = prefab names; values = display names). Each family has a 1-cell base piece and 3 / 5 / 10-segment long variants. "Burnt" variants exist for the cable family (electrocution damage state).

Gas pipes:
- `StructurePipeStraight` = "Pipe (Straight)" (1 cell) — note: this one *can* be merged and upgraded to insulated.
- `StructurePipeStraight3` = "Pipe (Straight 3)", `StructurePipeStraight5` = "Pipe (Straight 5)", `StructurePipeStraight10` = "Pipe (Straight 10)". Description: "Long variant of the straight pipe. These variants cannot be merged or upgraded with insulation."

Insulated gas pipes:
- `StructureInsulatedPipeStraight` = "Insulated Pipe (Straight)" (1 cell).
- `StructureInsulatedPipeStraight3` / `...5` / `...10` = "Insulated Pipe (Straight 3 / 5 / 10)".

Liquid pipes:
- `StructurePipeLiquidStraight` = "Liquid Pipe (Straight)" (1 cell).
- `StructurePipeLiquidStraight3` / `...5` / `...10` = "Liquid Pipe (Straight 3 / 5 / 10)".

Insulated liquid pipes:
- `StructureInsulatedPipeLiquidStraight` = "Insulated Liquid Pipe (Straight)" (1 cell).
- `StructureInsulatedPipeLiquidStraight3` / `...5` / `...10` = "Insulated Liquid Pipe (Straight 3 / 5 / 10)".

Chutes:
- `StructureChuteStraight` = "Chute (Straight)" (1 cell).
- `StructureChuteStraight3` / `...5` / `...10` = "Chute (Straight 3 / 5 / 10)".

Cables — only the Super-Heavy cable tier has long variants (confirmed by the NetworkUpgrader Workshop mod's own description: "Super Heavy Cables ... the only cable type with long variants available"):
- `StructureCableSuperHeavyStraight` = "Super-Heavy Cable (Straight)" (1 cell), `StructureCableSuperHeavyStraightBurnt`.
- `StructureCableSuperHeavyStraight3` / `...5` / `...10` = "Super-Heavy Cable (Straight 3 / 5 / 10)", each with a `...Burnt` sibling.
- (Ordinary `StructureCableStraight` and Heavy `StructureCableStraightH` have no long variants.)

Not part of this family but named "Long": `StructureConveyorStraightLong` / `...Short` / `StructureConveyorStraight` (the conveyor-belt cargo system, separate mechanic), and various `StructureCompositeCladdingAngled*Long*` and `StructureLightLong*` prefabs (cladding panels / light fixtures whose collider is wider, not multi-cell pipe-style runs). These are not built by `MultiMergeConstructor`.

There is no `StructurePipeStraight3` C# class, no `ChuteStraight` class, etc. `Chute : SmallSingleGrid, ISmartRotatable, IChute, ...` and `Cable : SmallSingleGrid, IGridMergeable, ...` and `Pipe : SmallSingleGrid, ...` / `Piping : Pipe, IGridMergeable, ISmartRotatable` are the only relevant classes; the long variants are instances of these.

## How a long piece declares its length
<!-- verified: 0.2.6228.27061 @ 2026-05-11 -->

A `Structure`'s occupied small-grid cells come from `GridBounds.GetLocalSmallGrid(position, rotation)` -> `Grid3[]`, ultimately computed in `Structure.GetLocalSmallGridBounds()` from the mesh/collider bounds scaled by `BoundsGridRatio` and the `BoundsGridAdd*` offsets (see `Structure.md`). A `StructurePipeStraight3` prefab simply has a collider/mesh ~3 small cells long, so `GetLocalSmallGrid` returns 3 `Grid3` entries; a `...10` returns 10. `Structure.ForceGridBounds : List<Grid3>` can override the computed set, but the per-length sizing is baked into the prefab asset, not into `Assembly-CSharp.dll` — the decompiled C# carries no length constant.

`Piping.CanReplace` and `Piping.WillMergeWhenPlaced` both iterate `(Grid3[])GridBounds.GetLocalSmallGrid(ThingTransformPosition, ThingTransformRotation)` and look up each cell's `SmallCell.Pipe`, which is how a long ghost detects the run of existing 1-cell pipes it would absorb.

## Class layout
<!-- verified: 0.2.6228.27061 @ 2026-05-11 -->

```csharp
public class MultiMergeConstructor : MultiConstructor
{
    public Item ToolExit;            // off-hand tool that enables "merge over an existing run"

    public override int ConstructingSoundHash         => string.IsNullOrEmpty(UsingSound)       ? 0 : Animator.StringToHash(UsingSound);
    public override int FinishedConstructingSoundHash => string.IsNullOrEmpty(UseCompleteSound) ? 0 : Animator.StringToHash(UseCompleteSound);

    public override void Construct(Grid3 localPosition, Quaternion targetRotation, int optionIndex, Item offhandItem, bool authoringMode, ulong steamId);
}
```

`ToolExit` (display string `MergeRequiresTool` = "Merging requires &lt;color=green&gt;{Tool}&lt;/color&gt; in other hand") is the item the player holds in the inactive hand to authorise a merge.

## Construct: the merge path
<!-- verified: 0.2.6228.27061 @ 2026-05-11 -->

```csharp
public override void Construct(Grid3 localPosition, Quaternion targetRotation, int optionIndex, Item offhandItem, bool authoringMode, ulong steamId)
{
    // No merge tool in off hand (and not authoring) -> behave like a plain MultiConstructor: place Constructables[optionIndex] as-is.
    if (!authoringMode && (offhandItem == null || (offhandItem.PrefabHash != ToolExit.PrefabHash
            && offhandItem.ReplacementOf != null && ToolExit.PrefabHash != offhandItem.ReplacementOf.PrefabHash)))
    {
        base.Construct(localPosition, targetRotation, optionIndex, offhandItem, authoringMode, steamId);
        return;
    }

    IGridMergeable gridMergeable = Constructables[optionIndex] as IGridMergeable;
    StructureFuselage structureFuselage = Constructables[optionIndex] as StructureFuselage;
    if (gridMergeable == null && structureFuselage == null) { base.Construct(...); return; }

    IGridMergeable gridMergeable2 = null; Structure thing = null;
    if (gridMergeable is Piping)
    {
        Piping piping = base.GridController.GetPipe(localPosition) as Piping;
        if (piping) piping.PipeNetwork?.Remove(piping);
        gridMergeable2 = piping; thing = piping;
    }
    else if (gridMergeable is Cable)
    {
        Cable cable = base.GridController.GetCable(localPosition);
        if (cable) cable.CableNetwork?.Remove(cable);
        gridMergeable2 = cable; thing = cable;
    }
    else if (structureFuselage)   // hull/fuselage panels share this kit form
    {
        StructureFuselage existing = base.GridController.GetStructure(localPosition) as StructureFuselage;
        existing?.StructureNetwork?.Remove(existing);
        if (existing) OnServer.Destroy(existing);
        base.Construct(localPosition, targetRotation, optionIndex, null, authoringMode, steamId, (!existing) ? 1 : 0);
        return;
    }
    if (gridMergeable2 == null) { base.Construct(...); return; }

    // Combine the open-end permutations of the new ghost and the existing piece, resolve the resulting
    // SmartRotate.ConnectionType (Elbow / Straight / Tee / Cross / ...), pick the Constructables entry whose
    // GetConnectionType() matches, compute the rotation that lines its open-ends up, destroy the old piece,
    // and place the chosen variant charging only the *extra* item cost (EntryQuantity delta).
    int[] permNew = gridMergeable.GetOpenEndLocationPermutation(Quaternion.identity, targetRotation);
    int[] permOld = gridMergeable2.GetOpenEndLocationPermutation(Quaternion.identity);
    int[] combined = new int[6];
    for (int i = 0; i < 6; i++) combined[i] = Math.Max(permNew[i], permOld[i]);
    // ... resolve connectionType via SmartRotate.OrientationLookup ...
    // num2 = first Constructables index whose GetConnectionType() == connectionType
    // index = first Constructables index whose GetConnectionType() == gridMergeable2.GetConnectionType()
    OnServer.Destroy(thing);
    int entryQuantity = Constructables[index].BuildStates[0].Tool.EntryQuantity;
    int extra = Constructables[num2].BuildStates[0].Tool.EntryQuantity - entryQuantity;
    base.Construct(localPosition, quaternion2, num2, null, authoringMode, steamId, extra);
}
```

Takeaways:

- Without `ToolExit` in the off hand, `MultiMergeConstructor` is just a `MultiConstructor`: you select the long variant from the kit (mouse-wheel option) and place it on empty cells like any other piece. So the long pieces are directly placeable from the kit if (and only if) they sit in that kit's serialized `Constructables` list.
- With `ToolExit` in the off hand and a ghost overlapping an existing pipe/cable, the kit *absorbs* the existing piece(s) into the larger variant, charging only the material difference. This is the "upgrade a straight run in place" affordance.
- Only `Piping`, `Cable`, and `StructureFuselage` have a merge branch here. `Chute` does not implement `IGridMergeable`, so the chute kit's long variants go through the plain `base.Construct` path (place-as-is); they are not merge-absorbed.

## IGridMergeable
<!-- verified: 0.2.6228.27061 @ 2026-05-11 -->

```csharp
public interface IGridMergeable : ISmartRotatable
{
    CanConstructInfo CanReplace(MultiConstructor constructor, Assets.Scripts.Objects.Item inactiveHandItem);
    bool WillMergeWhenPlaced();
}
```

Implemented by `Cable` (`Cable : SmallSingleGrid, IGridMergeable, ISmartRotatable, IRocketInternals, IRocketComponent`) and `Piping` (`Piping : Pipe, IGridMergeable, ISmartRotatable`). `Piping.Type` enum: `normal, Insulated, NormalLowVolume, InsulatedLowVolume, Duct`. `Piping.DontAllowMergingWithWrench` (bool) blocks merge for specific piping prefabs.

`Piping.CanReplace` rejects with `GameStrings.CannotMergeIMergeable` when the piece is `Indestructable` or the constructor is not a `MultiMergeConstructor`; rejects with `GameStrings.MergeRequiresTool` when no off-hand item / wrong off-hand item; rejects with `GameStrings.CannotMergeIMergeableOfDifferentType` when `PipeType` / `PipeContentType` differ; otherwise `CanConstructInfo.ValidPlacement`. `Cable.CanReplace` (around `Assembly-CSharp.dll` :: `Cable`) is the parallel implementation for cables. `ConstructionCursor` checks `rotatable is IGridMergeable gm && gm.WillMergeWhenPlaced()` while previewing placement.

## Implications for a "remove the long pieces" mod
<!-- verified: 0.2.6228.27061 @ 2026-05-11 -->

A mod that wants to make the 3/5/10 variants unavailable has these levers (none of them deletes the prefab type, just its reachability):

1. **Strip kit constructables** — at prefab-load, remove every `Structure` whose `PrefabName` matches `*Straight(3|5|10)` (and the Super-Heavy cable ones) from each `MultiMergeConstructor.Constructables` list (then let `MultiConstructor.OnPrefabLoad` clamp `LastSelectedIndex`). This hides them from the kit's mouse-wheel options. Mirror of the "insert variant" recipe in `Patterns/PrefabCloning.md`.
2. **Disable the merge tool** — null/replace `MultiMergeConstructor.ToolExit`, or patch `Piping.CanReplace` / `Cable.CanReplace` to always return `InvalidPlacement`. Removes the in-place upgrade affordance but not direct placement.
3. **Block at construction** — Harmony-prefix `MultiMergeConstructor.Construct` (and/or `MultiConstructor.Construct` / `Constructor.SpawnConstruct` / `CreateStructureInstance`) and reject when the chosen `Constructables[optionIndex]` (or `CreateStructureInstance.Prefab`) is a long variant. Catches every placement path including dev tools, at the cost of an unfiltered build menu.
4. **Unregister the prefab** — remove the long-variant prefab from `Prefab.AllPrefabs` / `Prefab._allPrefabs` after `Prefab.LoadAll`. Fragile: any cached reference (recipes, Stationpedia, an already-loaded save with one placed) still points at it.
5. **Strip from saves** — on save load, enumerate placed `Structure`s and `Thing.Delete` long variants. Destructive: breaks any current world that already merged its networks (including worlds touched by the NetworkUpgrader mod).

Existing-save concern: a save can already contain these prefabs (a player placed them, or the NetworkUpgrader Workshop mod's `upgrade <pipes|cables|chutes|all>` console command converted runs into them). Hiding from the kit does not affect already-placed instances; only lever 5 removes those, destructively.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-05-11 -->

- 2026-05-11: page created. `MultiMergeConstructor` class body, `IGridMergeable`, and `Piping.CanReplace` lifted verbatim from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` (lines ~288310-288459, ~215053, ~363925-363993). Prefab-name family enumerated from `StreamingAssets\Language\english.xml`. The NetworkUpgrader Workshop mod (id 3656955459) corroborates "Super-Heavy is the only cable tier with long variants" and that long pieces are 3/5/10 segments. No conflicts with existing pages.

## Open questions

- Whether the `*Straight3/5/10` prefabs are in the vanilla `ItemKitPipe` / `ItemKitChute` / Super-Heavy-cable-kit `Constructables` lists by default (so directly placeable from the kit's mouse-wheel), or only reachable via the `ToolExit` merge path and dev/admin spawn. The `Constructables` list is serialized in the Unity prefab and not visible in the decompiled C#; resolve with an in-game check or an InspectorPlus dump of `ItemKitPipe.Constructables` / `ItemKitChute.Constructables`.
- The exact `ToolExit` item for each kit (likely a wrench-class tool; `MergeRequiresTool` shows it as a coloured display name at runtime). Confirm by inspecting the kit prefabs in-game.
- How the chute kit exposes `StructureChuteStraight3/5/10` given `Chute` does not implement `IGridMergeable` — presumably as plain `Constructables` entries placed as-is, but unconfirmed.
