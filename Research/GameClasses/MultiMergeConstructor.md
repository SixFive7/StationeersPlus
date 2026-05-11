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
  - ./CreateStructureInstance.md
  - ../GameSystems/PlacementOrientation.md
  - ../GameSystems/ConveyorBeltCutContent.md
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

That is the complete set of "merge into a longer straight run" prefabs (6 families x {1, 3, 5, 10}, plus `...Burnt` only for the cable family). `StreamingAssets\Language\english.xml` has no other `<Key>...Straight[0-9]+</Key>` keys; low-volume pipes and ducts have only `...CrossJunction3/4/5/6` (junction *outlet counts*, not multi-cell runs); no long walls / girders / beams / rails exist. Changelog corroboration: the WIP long straight insulated-*liquid*-pipe variants (3x / 5x / 10x) were an earlier beta experiment; Update v0.2.6085.26682 then changed the long super-heavy-cable and long pipe variants to the "straight asymmetric" smart-rotation type, and the new Super-Heavy cable (500 kW) shipped with 3 / 5 / 10 long variants for trunk lines.

Adjacent but a *separate* system, not a `MultiMergeConstructor`: the conveyor-belt prefabs `StructureConveyorStraight` / `StructureConveyorStraightLong` / `StructureConveyorStraightShort` / `StructureConveyorCorner` / `StructureConveyorRiser` and their kit `ItemKitConveyor` ("Kit (Conveyors)"). These are **cut content** — leftovers from the pre-chute "Flexi-belt" cargo system. The prefabs and string / Stationpedia entries still ship registered (so old saves and the prefab tables don't break), but no conveyor-specific C# class exists in `Assembly-CSharp`: `Prefab.Find<Item>("ItemKitConveyor")` is cast to plain `Item`, and an exhaustive `grep -i conveyor` of the assembly returns only `LoreFactions.Recurso` (the lore faction credited in the flavor text), a `"Conveyors"` audio-concurrency bucket, `CrateType.ConveyorSupplies` (whose spawn case adds 2x `ItemKitConveyor`), the vestigial `DynamicThing.OnConveyor` bool (read in the physics tick, never assigned `true` anywhere), and `DeviceImportExport.ImportConveyorPosition` / `DeviceImportExport2.ExportConveyorPosition2` (Transform fields sitting next to `Chute ImportChute` / `Chute ExportChute2` — chute drop-points, not belt code). The conveyor prefabs are spawnable via the creative-mode prefab spawner, but "building" one (right-mouse) errors because the construction wiring is gone. So `StructureConveyorStraightLong` is **not** a working `MultiConstructor`/`MultiMergeConstructor` kit option and is not merge-able — for a "remove the long pieces" mod it is a "hide a vestige" target (`HideInStationpedia`, drop from the creative spawn list), not the `Constructables`-strip + merge-reject + on-load-replace target the functional long pipe / chute / cable variants are. Full inventory: `Research/GameSystems/ConveyorBeltCutContent.md`. Also unrelated: `StructureCompositeCladdingAngled*Long*` and `StructureLightLong*` are cladding panels / light fixtures with a wider collider, not multi-cell pipe-style runs.

There is no `StructurePipeStraight3` C# class, no `ChuteStraight` class, etc. `Chute : SmallSingleGrid, ISmartRotatable, IChute, ...` and `Cable : SmallSingleGrid, IGridMergeable, ...` and `Pipe : SmallSingleGrid, ...` / `Piping : Pipe, IGridMergeable, ISmartRotatable` are the only relevant classes; the long variants are instances of these.

## How a long piece declares its length
<!-- verified: 0.2.6228.27061 @ 2026-05-11 -->

A `Structure`'s occupied small-grid cells come from `GridBounds.GetLocalSmallGrid(position, rotation)` -> `Grid3[]`, ultimately computed in `Structure.GetLocalSmallGridBounds()` from the mesh/collider bounds scaled by `BoundsGridRatio` and the `BoundsGridAdd*` offsets (see `Structure.md`). A `StructurePipeStraight3` prefab simply has a collider/mesh ~3 small cells long, so `GetLocalSmallGrid` returns 3 `Grid3` entries; a `...10` returns 10. (`Structure.ForceGridBounds : List<Grid3>` overrides only `GetLocalGridBounds`, the *large*-grid set, not the small-grid set; the per-length sizing of these pieces is the mesh bounds, baked into the prefab asset, not into `Assembly-CSharp.dll` — the decompiled C# carries no length constant.)

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
3. **Block at construction** — Harmony-prefix `MultiMergeConstructor.Construct` (and/or `MultiConstructor.Construct` / `Constructor.SpawnConstruct` / `CreateStructureInstance`) and reject when the chosen `Constructables[optionIndex]` (or `CreateStructureInstance.Prefab`) is a long variant. Catches every placement path including dev tools, at the cost of an unfiltered build menu. **Caveat: ZoopMod (id 3310094883) drives placement through `InventoryManager.UsePrimaryComplete` and has no per-piece error recovery, so a hard reject here produces half-built zoops. Don't use lever 3 as a reject if ZoopMod compatibility matters; use lever 1 instead.**
4. **Unregister the prefab** — remove the long-variant prefab from `Prefab.AllPrefabs` / `Prefab._allPrefabs` after `Prefab.LoadAll`. Fragile: any cached reference (recipes, Stationpedia, an already-loaded save with one placed) still points at it; a save containing a long variant fails to instantiate it on load. Keep the prefab registered if you also do lever 5.
5. **Strip from saves** — on save load (after structures are placed), enumerate placed `Structure`s and `OnServer.Destroy` / `Thing.Delete` long variants, or replace each with N x the 1-cell base piece (NetworkUpgrader in reverse: preserve cell positions, rotation, custom color, and gas/atmos contents). Plain delete splits the network at that cell and loses the segment's contents; replace keeps the network. Run server-side / single-player only.

Existing-save concern: a save can already contain these prefabs (a player placed them via the kit / `ToolExit` merge, the NetworkUpgrader Workshop mod's `upgrade <pipes|cables|chutes|all>` console command converted runs into them, or a ZoopMod drag packed them in). Hiding from the kit does not affect already-placed instances; only lever 5 removes those.

Menu surfaces a long variant can appear in, and which lever clears each:

- **In-world kit mouse-wheel / `ConstructionPanel`** — lever 1 (strip `Constructables`). `ConstructionPanel` (`Assembly-CSharp` line ~220492) holds `[ReadOnly] public MultiConstructor Parent` and `int BuildIndex`; `Assign`/`SelectUp`/`SelectDown` walk `BuildIndex` over `Parent.Constructables` only, and `MultiConstructor.Construct(... optionIndex ...)` does `new CreateStructureInstance(Constructables[optionIndex], ...)`. Nothing here consults `Prefab.AllPrefabs`, so removing the prefab from the registry does *not* clean the wheel; you must mutate `Constructables`. `MultiConstructor.OnPrefabLoad` (~288251) already removes `null` entries and clamps `LastSelectedIndex` — postfix it and `Constructables.RemoveAll(s => s != null && isLong(s))`, then re-clamp `LastSelectedIndex` if `Count` changed. No long variant is a single-piece `Constructor.BuildStructure`, so there is no second wheel source to clear.
- **Stationpedia (page list + category lists + search)** — set `Thing.HideInStationpedia = true` (`Assembly-CSharp` line ~297850) on each long-variant prefab. `Stationpedia.Regenerate()` (~231012) -> `PopulateThingPages()` (~231964) loops `foreach (Thing allPrefab in Prefab.AllPrefabs)` and `continue`s (no page created) when `allPrefab.HideInStationpedia || DataHandler.HiddenInPedia[name]`; the same guard gates the kit/category lists (~232470-232697) and search (`DoSearch` iterates `StationpediaPages`, which the hidden prefab never enters). Pages are not cached across runs and `Regenerate` re-runs on `Localization.OnLanguageChanged`, so set the flag on `Prefab.OnPrefabsLoaded` (it survives unless an XML page-override re-sets it inside `DataHandler.HandleThingPageOverrides` -> `thing.HideInStationpedia = item.HideInSPDA;` at ~47703; vanilla has no override for these, but a belt-and-suspenders postfix on `HandleThingPageOverrides` re-setting the flag is ~5 extra lines). This is *not* much work; no reason to defer it. See `Research/GameClasses/Stationpedia.md` / `StationpediaPage.md` / `Research/GameSystems/StationpediaPageRendering.md` / `StationpediaSearch.md`.
- **Fabricator recipe lists** — nothing to do. Recipe outputs are always `DynamicThing`/`Item` (`Autolathe`/`ElectronicsPrinter`/`HydraulicPipeBender` `RecipeComparable` are `Dictionary<DynamicThing, Recipe>`; `Microwave`/`ChemistryStation` are `Dictionary<Recipe, Item>`). A long variant is a `Structure` (`Pipe`/`Cable`/`Chute`), so it cannot be a recipe output. The `HydraulicPipeBender` produces the *kits* (`ItemKitPipe` etc.), which are not removed. Confirmed: no fabricator recipe emits a long-variant prefab.
- **Creative / admin spawn UI** — out of scope (it iterates `Prefab.AllPrefabs`; would need lever 4 or a population patch). The long-variant prefabs stay registered so old saves load, so they remain visible here.

ZoopMod interaction (verified from `github.com/Nivvdiy/ZoopModRecovered`, `Zoop/Placement/ZoopLongVariantRules.cs`): ZoopMod detects long variants by string-matching `Constructables[0].GetPrefabName() + <digits>` inside the active kit's `Constructables` list (no hard-coded prefab names or hashes), caches the result per base-piece object, then sets `ConstructionPanel.BuildIndex` and calls vanilla `InventoryManager.UsePrimaryComplete`. So lever 1 (strip the long variants from `Constructables`) makes ZoopMod's `FindLongVariants` return empty and it cleanly falls back to placing only the 1-cell base piece, with no ZoopMod-side errors. Two constraints: (a) keep `Constructables[0]` as the 1-cell base straight (ZoopMod assumes index 0); (b) strip before the first zoop preview of the session (a prefab-load / kit-init patch satisfies this), because ZoopMod caches `_longVariantsByBasePiece`. ZoopMod has no "disable long pieces" config to flip; BepInEx GUID `"ZoopMod"`. Related Workshop mods: NetworkUpgrader (id 3656955459, `upgrade pipes|cables|chutes|all` console command) and "No tool required for pipe/cable merging" (id 3571613419, Nikku, removes the `ToolExit` requirement) — both only ever produce the vanilla long prefabs, so lever 1 + lever 5 cover them; do *not* null `MultiMergeConstructor.ToolExit` (lever 2) if you care about the Nikku mod, and it is unnecessary anyway once the long variants are out of `Constructables`. QuietPipesMod (id 3402741739) adds `ItemKitQuietInsulatedPipe` / `ItemKitQuietInsulatedLiquidPipe`; no evidence it adds long quiet-pipe variants, but a footprint-based predicate (below) catches them if it does.

### Detecting a long-straight variant, and the implementation recipe

There is no single boolean meaning "I am a long straight variant" across the families. `Cable.BlockMergeWithOtherCables` (`Assembly-CSharp` line ~371306; this is `IsLongStraightVariant` renamed) and `Piping.DontAllowMergingWithWrench` (~363936) are set on the cable / pipe long variants respectively but are semantically "don't merge", are family-specific, and `Chute` has no such flag. The robust, family-agnostic predicate is footprint cell count:

```csharp
// On a registered prefab, GridBounds is populated by Structure.CachePrefabBounds() (~297131):
//   GridBounds = new GridBounds(this);  -> _gridsSmall = (Grid3[])structure.GetLocalSmallGridBounds();
static bool IsLongStraightVariant(Structure s)
{
    if (s is not (Pipe or Cable or Chute)) return false;          // the three small-grid-pipe families
    var cells = (Grid3[])s.GridBounds.GetLocalSmallGrid(Vector3.zero, Quaternion.identity);
    return cells != null && cells.Length > 1;                     // 1-cell base == 1; long variant == 3/5/10
}
```

(On the *prefab*, use `GridBounds.GetLocalSmallGrid`, not `Structure.BlockingGrids` — `BlockingGrids` is the post-placement registered set, a `Grid3[1]` placeholder on a fresh prefab. Note `Structure.ForceGridBounds` overrides only `GetLocalGridBounds` (the *large*-grid set), **not** `GetLocalSmallGridBounds`, which always computes from the mesh bounds — so the small-cell count check is unaffected by `ForceGridBounds`. Optionally corroborate with `(s as Cable)?.BlockMergeWithOtherCables == true || (s as Piping)?.DontAllowMergingWithWrench == true`.)

Recipe:

1. **`Prefab.OnPrefabsLoaded` handler** (or `Prefab.LoadAll` postfix): scan `Prefab.AllPrefabs`, build `HashSet<int> longHashes` of `PrefabHash` for everything matching `IsLongStraightVariant`; set `HideInStationpedia = true` on each. Leave them in `Prefab._allPrefabs` / `AllPrefabs`.
2. **Postfix `MultiConstructor.OnPrefabLoad`**: `Constructables.RemoveAll(s => s != null && longHashes.Contains(s.PrefabHash)); if (LastSelectedIndex >= Constructables.Count) LastSelectedIndex = Constructables.Count - 1;` (covers `MultiMergeConstructor` since it inherits `OnPrefabLoad`).
3. **Postfix `World.OnLoadingFinished`** (`Assets.Scripts.Objects.World`, static, `Assembly-CSharp` line ~305850 — `World.OnLoadingFinished(XmlSaveLoad.WorldData)`, called from the tail of `XmlSaveLoad.LoadWorld`; it in turn calls `GameManager.StartGame()` synchronously, which flips `GameState` to `Running` and runs `UpdateThingsOnGameStart` -> per-Thing `Thing.OnFinishedLoad`. Patch `World.OnLoadingFinished`, not `GameManager.StartGame` directly — the latter is `async UniTask`.) Guard `if (!GameManager.RunSimulation) return;` (host / single-player only — `Constructor.SpawnConstruct` on a client just sends a message and does nothing locally, and `OnServer.Destroy` is server-only). Then iterate a snapshot of `GridController.AllStructuresPool` (the pool is mutated by destroy/create) and for each long-variant instance either: (a) **delete** — `OnServer.Destroy(s)` (deregisters its grid cells, removes it from its `PipeNetwork`/`CableNetwork`/`ChuteNetwork`, splitting the run into disconnected ends and queuing the gas redistribution as a `NetworkAtmosphereEvent`, queues a `DestroyEvent` to clients) — simplest, but loses the segment's gas/contents and splits the network there; or (b) **replace** — compute the run's `Grid3[]` cells via `s.GridBounds.GetLocalSmallGrid(s.ThingTransformPosition, s.ThingTransformRotation)`, capture rotation/owner/paint, `OnServer.Destroy(s)`, then `Constructor.SpawnConstruct(new CreateStructureInstance(basePrefab, cell, rotation, owner, colorIdx))` for each cell. Because `GameState == Running` at this point, each new 1-cell piece's `OnRegistered` runs the network-merge branch (`Pipe`/`Cable`/`Chute.OnRegistered` does `StructureNetwork.Merge(ConnectedNetworks(this))` + `.Add(this)` only when `GameState != Loading`), so the replacements rejoin the surrounding network with no extra calls — gas and connectivity preserved (modulo a transient pressure swing while the long piece's volume is briefly out of the network; the gas-divide event is async and usually runs after the replacements are back). `SpawnConstruct` charges no item cost and replicates created structures to connected clients via the normal `Thing.Create` -> `NewToSend` path. The vanilla precedent for the destroy-then-`SpawnConstruct` swap is `Cable.Break()` (`new CreateStructureInstance(RupturedPrefab, this); OnServer.Destroy(this); Constructor.SpawnConstruct(instance);`). This is also the same iterate-and-`OnServer.Destroy` shape as `WorldManager.DeleteOutOfBoundsObjects` (~251634).
4. **Do not** add a hard reject in `MultiMergeConstructor.Construct` / `Constructor.SpawnConstruct` — step 2 already makes the wheel unable to select a long variant, and a mid-placement reject breaks ZoopMod.

`GridController.AllStructuresPool` (`Assets.Scripts.GridController`, `static readonly DensePool<Structure>("AllStructuresPool", 65536)`, line ~191276) is the canonical "all placed structures" collection (every `Structure` / `SmallGridObject` is added in `GridController.Register`, removed in `Deregister`); iterate with `ForEach`, snapshot before destroying. `OcclusionManager.AllThings` is the broader all-Thing pool if you need it.

Replace-on-load specifics worth knowing: the long-variant prefabs must stay registered in `Prefab.AllPrefabs` regardless of which lever you pick — `XmlSaveLoad.LoadThing` (`Assembly-CSharp` ~251312) does `Prefab.Find(thingData.PrefabName)` and silently drops the structure (`"Can't spawn ..."` warning) if the prefab is gone, so unregistering would lose every placed long piece before you can rebuild it. During the *load* itself (`GameState == Loading`) the network linkage is pre-established differently — `XmlSaveLoad.LoadInNetworks` pre-creates the network objects and each pipe/cable/chute's `DeserializeSave` re-adds itself to its saved network by id (`Pipe.DeserializeSave`: `(Referencable.Find<PipeNetwork>(id) ?? new PipeNetwork(id)).Add(this)`, and likewise `Cable`/`Chute`) — which is why the merge-in-`OnRegistered` path is skipped during load and fires only for the replacements created at `Running`. For burnt long cables, `StructureCableSuperHeavyStraight10Burnt` etc. are `CableRuptured` prefabs (`Cable.RupturedPrefab : CableRuptured`; `CableRuptured : SmallGrid` — electrically dead, no `CableNetwork`), base `StructureCableSuperHeavyStraightBurnt`; the name+footprint predicate classifies them without a type check and they need no network rejoin or colour carry. For chutes, an item physically in transit sits in `Chute.TransportSlot => Slots[0]`, and `Chute` does not override `Thing.DestroyChildrenOnDead` (default `true`), so `OnServer.Destroy(chute)` destroys the contained item — to avoid that, grab `chute.TransportSlot.Occupant` and `OnServer.MoveToWorld(...)` (or re-insert into a replacement) before destroying.

`Mods/NetworkPuristPlus/` in this repo implements the **replace** variant: `LongVariantRegistry` (the `OnPrefabsLoaded` scan, `HideInStationpedia`, the imperative `MultiConstructor.Constructables` strip) and `ReplaceLongPiecesOnLoadPatch` (the `World.OnLoadingFinished` postfix). See its `RESEARCH.md` for the per-file walkthrough and the accepted limitations.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-05-11 -->

- 2026-05-11: page created. `MultiMergeConstructor` class body, `IGridMergeable`, and `Piping.CanReplace` lifted verbatim from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` (lines ~288310-288459, ~215053, ~363925-363993). Prefab-name family enumerated from `StreamingAssets\Language\english.xml`. The NetworkUpgrader Workshop mod (id 3656955459) corroborates "Super-Heavy is the only cable tier with long variants" and that long pieces are 3/5/10 segments. No conflicts with existing pages.
- 2026-05-11: completeness re-check. `english.xml` `<Key>...Straight[0-9]+</Key>` sweep confirms exactly 6 families x {3,5,10} (+ `...Burnt` only for the cable family) and nothing else (low-volume pipes/ducts have only `...CrossJunction3/4/5/6` outlet-count junctions; no long walls/girders/beams/rails). In-game changelog (`StreamingAssets\version.ini` `UPDATENOTES`) cross-check: WIP long insulated-liquid-pipe 3x/5x/10x added in an earlier beta, then "long straight pipe variants" / "long straight super-heavy cable variants" / "long straight chute variants" moved to the new 'straight asymmetric' smart-rotate type, super-heavy-cable build requirements set for the 3x/5x/10x variants, and a `Cable.IsLongStraightVariant` flag renamed to `BlockMergeWithOtherCables`. Open question 1 resolved: the long variants ARE in the stock kit `Constructables` lists -- ZoopMod's long-piece feature (`Zoop/Placement/ZoopLongVariantRules.cs`) works on vanilla kits by string-matching `Constructables[0].GetPrefabName() + <digits>` and finds them, which it could not if they were absent. Added ZoopMod-interaction notes and the menu-surface map to the "Implications" section.
- 2026-05-11: removal-mechanics deep dive (decompile pass for a planned StationeersPlus "remove the long pieces" mod). Confirmed: (a) `MultiConstructor.Constructables` is serialized direct `Structure` refs, not `Prefab.AllPrefabs` lookups, so unregistering a prefab does not clean the kit wheel; `MultiConstructor.OnPrefabLoad` already null-strips + clamps `LastSelectedIndex`. (b) `Thing.HideInStationpedia` (~297850) makes `Stationpedia.PopulateThingPages` (~231964, loops `Prefab.AllPrefabs`) skip a prefab entirely (page list, category lists, search) — pages are not cross-run cached, `Regenerate` re-runs on language change, the flag is normally written only from XML page-overrides (`DataHandler.HandleThingPageOverrides` ~47703). (c) No fabricator recipe outputs a long variant (recipe outputs are `DynamicThing`/`Item`; long variants are `Structure`s) -- resolves the old open question. (d) Save-load lifecycle: `XmlSaveLoad.LoadWorld` (~251347) -> `LoadThing` (~251312, `Prefab.Find` resolves the prefab, so it must stay registered) -> networks linked -> `World.OnLoadingFinished` (~305850) -> `GameManager.StartGame` (~189593) -> `UpdateThingsOnGameStart` (`Thing.OnFinishedLoad` per Thing); the post-load sweep belongs in a postfix on `World.OnLoadingFinished` (static, `Assets.Scripts.Objects.World`; `GameManager.StartGame` is `async UniTask`, awkward to postfix), guarded by `GameManager.RunSimulation`. (e) `GridController.AllStructuresPool` (~191276, `Assets.Scripts.GridController`, `DensePool<Structure>`) is the canonical placed-structure collection; `OnServer.Destroy(structure)` is the correct removal (deregisters the grid cell, splits the run, queues client `DestroyEvent`); iterate a snapshot. (f) Robust long-variant predicate: `s is Pipe or Cable or Chute` && `s.GridBounds.GetLocalSmallGrid(Vector3.zero, Quaternion.identity).Length > 1` (on a prefab; not `BlockingGrids`), optionally corroborated by `Cable.BlockMergeWithOtherCables` / `Piping.DontAllowMergingWithWrench`. Added the "Detecting a long-straight variant, and the implementation recipe" subsection.
- 2026-05-11: replace-on-load mechanics + correction. The "remove the long pieces" mod was built as `Mods/NetworkPuristPlus/` using the **replace** variant (destroy each long piece, `Constructor.SpawnConstruct` the N x 1-cell base at its cells with the same rotation/owner/paint; the replacements rejoin the network because `OnRegistered`'s merge branch fires at `GameState == Running`). Added the replace recipe to the "implementation recipe" section. Findings folded in: `Pipe`/`Cable`/`Chute.OnRegistered` runs `StructureNetwork.Merge(ConnectedNetworks(this)) + .Add(this)` only when `GameState != Loading` (during load the network is pre-linked by `DeserializeSave` from the saved network id); `Cable.RupturedPrefab : CableRuptured` (`CableRuptured : SmallGrid`, no network) so the `...Burnt` long-cable variants are `CableRuptured` prefabs; `Chute.TransportSlot => Slots[0]` and `Chute` keeps `Thing.DestroyChildrenOnDead == true`, so `OnServer.Destroy` on a long chute destroys an in-transit item unless it is moved out first; `Cable.Break()` is the vanilla destroy-then-`SpawnConstruct` precedent. Correction: `Structure.ForceGridBounds` overrides only `GetLocalGridBounds` (large-grid), **not** `GetLocalSmallGridBounds` — the small-cell-count check is unaffected by it (the prior wording of this page implied otherwise; fixed in two places). `Constructor.md` and `CreateStructureInstance.md` were checked and are already accurate (the `SpawnConstruct` body there already includes the `0L` reference-id arg; both `CreateStructureInstance` ctors are present).
- 2026-05-11: conflict on "`StructureConveyorStraightLong` status". Previous claim: it is a distinct multi-cell buildable variant the player selects from the (plain `MultiConstructor`) `ItemKitConveyor` kit, with no merge-absorb. New finding: the `StructureConveyor*` prefabs and `ItemKitConveyor` are cut content from the pre-chute "Flexi-belt" era — prefabs and string entries still ship registered, but no conveyor-specific C# class exists in `Assembly-CSharp` (only leftovers: `LoreFactions.Recurso`, a "Conveyors" audio bucket, `CrateType.ConveyorSupplies`, the never-set `DynamicThing.OnConveyor` bool, the chute-related `DeviceImportExport.ImportConveyorPosition` transforms); spawnable via the creative spawner, errors on build. Fresh validator verdict: B is correct — the "buildable variant you select from the kit" wording was unsupported (the page cited only the `english.xml` name list for it; the DLL has no construction wiring referencing the conveyor prefabs and `OnConveyor` is dead code). Result: rewrote the conveyor paragraph in "The 'long' straight-piece prefab family"; created `Research/GameSystems/ConveyorBeltCutContent.md` (full inventory) and linked it from `related:`.
- 2026-05-11: `Pipe.OnDestroy` gas-transfer dance, and the standalone-network gas-loss it causes when rebuilding. `Pipe.OnDestroy` (`Assembly-CSharp` line ~363201): snapshots `ConnectedPipes()` (before removal), `PipeNetwork?.Remove(this)`, then if `RunSimulation` copies the *whole* network gas (`GasMixture gasMixture = GasMixtureHelper.Create(); gasMixture.Set(pipeNetwork.Atmosphere.GasMixture);`), calls `networkedPipe.PipeNetwork.RebuildNetworkServer(networkedPipe)` for each connected pipe (and collects their networks into `list2`), then queues `NetworkAtmosphereEvent.DivideNetworkAtmosphere(list2, gasMixture)` (or `..., pipeNetwork.Atmosphere)` when `pipeNetwork.Atmosphere.IsAwaitingEvent`). Consequence for a "destroy each long pipe + `Constructor.SpawnConstruct` the N×1-cell base" rebuild: `OnServer.Destroy` is deferred to end-of-frame, all of a network's long pipes get destroyed together, each `OnDestroy` queues its own `DivideNetworkAtmosphere`, and for a network that consisted *entirely* of long variants the gas does not make it to the rebuilt single-tile network — field-confirmed: NetworkPuristPlus rebuilding a standalone gas-edited `StructurePipeStraight 10/5/3` run (4 mol O2 + 16 mol N2 + 123 kJ in a 190 L network) left the rebuilt 18×`StructurePipeStraight` (correctly networked, correct 190 L `<Volume>`) with `Oxygen=0 Nitrogen=0 Energy=0`; the original network id is gone, the rebuilt pipes are in a brand-new network with an empty atmosphere. Mixed networks (long pipes alongside regular pipes/devices) keep the gas because there are surviving pipes for the divide. Fix used in `Mods/NetworkPuristPlus/` v1.0: capture each affected `PipeNetwork.Atmosphere.GasMixture` (via `GasMixtureHelper.Create().Set(...)`) and its `ReferenceId` *before* destroying anything from it; after a 3-frame `WaitForEndOfFrame`-equivalent settle (so the deferred destroys + queued atmospherics events have run), if a sample rebuilt pipe's `PipeNetwork.ReferenceId != original` (the original network died) `Atmosphere.Add(capturedGas)` into the rebuilt network. Cables/chutes carry no gas, so this only applies to `Pipe`-derived long variants. Relevant types/namespaces: `Pipe` `Assets.Scripts.Objects.Pipes`, `PipeNetwork : AtmosphericsNetwork` `Assets.Scripts.Networks`, `Atmosphere : IReferencable,...` `Assets.Scripts.Atmospherics` (NOT the unrelated static `Assets.Scripts.Networking.Atmosphere`), `GasMixture` (struct) / `GasMixtureHelper` `Assets.Scripts.Atmospherics`, `Atmosphere.Add(GasMixture)` line ~190674.
- 2026-05-11: correction to the "Detecting a long-straight variant" subsection — the footprint test does NOT work on a prefab. `Structure.GridBounds` is `new GridBounds()` (the empty parameterless ctor; `_gridsSmall` empty) until `Structure.CachePrefabBounds()` -> `GridBounds = new GridBounds(this)` runs, and `CachePrefabBounds()` is called from `Structure.SetPrefab(Thing prefab)` (when a Thing is *instantiated* from a prefab), not from `Prefab.RegisterExisting` / `Prefab.LoadAll`. So on a never-yet-instantiated prefab at `Prefab.OnPrefabsLoaded` time, `GridBounds.GetLocalSmallGrid(Vector3.zero, Quaternion.identity)` returns a *zero-length* `Grid3[]`, and `Structure.BlockingGrids` is a `Grid3[1]` placeholder — neither reveals the real N cells. Field-confirmed: NetworkPuristPlus v1.0's first build gated prefab-time classification on `footprint > 1 cell` and consequently classified ZERO long variants (every candidate skipped at the footprint check; log line `[Warning:NetworkPuristPlus] ... no long-variant prefabs found`). Corrected approach: at prefab-load time classify by NAME only — a `Structure` whose prefab name matches `^(Structure[A-Za-z]+Straight)(\d+)(Burnt)?$` AND whose de-numbered name (`group1 + group3`) is a registered `Structure` (optionally also `s is SmallGrid` and/or the `Cable.BlockMergeWithOtherCables` / `Piping.DontAllowMergingWithWrench` family flags); the `english.xml` `<Key>...Straight[0-9]+</Key>` sweep confirms nothing outside the six families matches, so name+base-exists is precise. The footprint IS reliable on a placed *instance* (it has been instantiated, so `CachePrefabBounds` has run) — that is where the on-load rebuild reads `GetLocalSmallGrid(ThingTransformPosition, ThingTransformRotation)` to get the run's cells. The "Detecting a long-straight variant, and the implementation recipe" code sample / parenthetical above still shows the prefab-footprint version and should be updated to the name-based predicate. (Confirmed by runtime behavior + decompile — `GridBounds = new GridBounds()` at `Assembly-CSharp` line ~295232, `CachePrefabBounds` at ~297131 called from `SetPrefab` at ~295701; not a fresh-validator pass.) NetworkPuristPlus v1.0 (rebuilt with the name-based predicate + per-rebuilt-piece `Logger.LogInfo`) is the reference implementation.

## Open questions

- The exact `ToolExit` item for each kit (likely a wrench-class tool; `MergeRequiresTool` shows it as a coloured display name at runtime). Confirm by inspecting the kit prefabs in-game.
- How the chute kit exposes `StructureChuteStraight3/5/10` given `Chute` does not implement `IGridMergeable` — presumably as plain `Constructables` entries placed as-is (the `MultiMergeConstructor.Construct` merge path only handles `Piping` / `Cable` / `StructureFuselage`, so chute long variants must go through `base.Construct`), but the kit's serialized `Constructables` membership is unconfirmed.
