---
title: UniversalPage
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/StationpediaPlus/PLAN.md:424-432
  - Plans/StationpediaPlus/PLAN.md:940-978
  - Plans/StationpediaPlus/PLAN.md:857-872
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: UniversalPage
related:
  - ./Stationpedia.md
  - ./StationpediaPage.md
  - ../GameSystems/StationpediaPageRendering.md
tags: [stationpedia, ui]
---

# UniversalPage

Vanilla game class at `UniversalPage : UserInterfaceBase` (line 233792). Whole-page renderer that hosts the 19 `StationpediaCategory` fields driven by the `Populate*Inserts` methods.

## Category inventory (19 categories)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0211.

The full set of `StationpediaCategory` fields on `UniversalPage`, each with
the `Populate*` method that fills it and the `StationpediaPage` field
that method reads (from game.cs lines 233895-233932 and the Populate methods
at 234554-234574):

| UniversalPage field | Populate method | Source on page |
|---|---|---|
| `SlotContents` | `PopulateSlotInserts` | `page.SlotInserts` |
| `CostToPrintContents` | `PopulateHowToBuildInserts` | `page.HowToBuild` |
| `BuildStateContents` | `PopulateBuildStatesInserts` | `page.BuildStates` |
| `StructureVersionContents` | `PopulateStructureVersion` | `page.StructVersionInsert` |
| `LogicContents` | `PopulateLogicInserts` | `page.LogicInsert` |
| `LogicInstructions` | `PopulateLogicInstructions` | `page.LogicInstructions` (+ `page.HasMemory`) |
| `LogicSlotContents` | `PopulateLogicSlotInserts` | `page.LogicSlotInsert` |
| `ModeContents` | `PopulateModeInserts` | `page.ModeInsert` |
| `ConnectionContents` | `PopulateConnectionInserts` | `page.ConnectionInsert` |
| `LifeRequirements` | `PopulateLifeRequirements` | `page.LifeRequirements` |
| `FoundInOreContents` | `PopulateOreInserts` | `page.FoundInOre` |
| `FoundInGasContents` | `PopulateGasInserts` | `page.FoundInGas` |
| `FoundInFermentationContents` | `PopulateFermentationInserts` | `page.FoundInFermentation` |
| `ConstructedThingsContents` | `PopulateKitInserts` | `page.ConstructedByKits` (naming inverted in source) |
| `ProducedThingsContents` | `PopulateProducedThings` | `page.ProducedThingsInserts` |
| `ConstructedByKitsContents` | `PopulateConstructedThings` | `page.ConstructedThings` (also inverted) |
| `ResourcesUsed` | `PopulateUsedResources` | `page.ResourcesUsed` |
| `UsedIn` | `PopulateUsedIn` | `page.UsedIn` |
| `CombustionInfo` | `PopulateCombustionInfo` | `page.CombustionInserts` |

Note: the `ConstructedThingsContents` ↔ `ConstructedByKits` and
`ConstructedByKitsContents` ↔ `ConstructedThings` pairs are genuinely
inverted in the vanilla source (likely a historical naming mixup). If
future work needs to inject into either kit-related category, match the
mapping above precisely.

For StationpediaPlus's first consumer we only mutate `page.LogicInsert`
indirectly (via enum extension + CanLogicRead/Write postfixes; Decision 11A).
Other categories are untouched. The inventory above exists for future mods
that may need to inject slots, recipes, version history, etc.

## Sibling index clamp
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0219j.

`Transform.SetSiblingIndex(N)` clamps to `childCount - 1` if N exceeds available siblings. With SPA loaded, our index-21 section sits below SPA's index-20 OperationalDetailsCategory. Without SPA, our section sits last. Both cases render correctly.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0200, F0211, F0219j. No conflicts.

## Open questions

None at creation.
