---
title: StationpediaPageRendering
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/StationpediaPlus/PLAN.md:510-530
  - Plans/StationpediaPlus/PLAN.md:565-592
  - Plans/StationpediaPlus/PLAN.md:596-635
  - Plans/StationpediaPlus/PLAN.md:639-676
related:
  - ./StationpediaSearch.md
  - ./StationpediaMarkup.md
  - ./StationpediaAscendedInternals.md
  - ./LogicType.md
  - ../GameClasses/Stationpedia.md
  - ../GameClasses/StationpediaPage.md
  - ../GameClasses/UniversalPage.md
tags: [stationpedia, ui]
---

# StationpediaPageRendering

End-to-end lifecycle of a Stationpedia page render: the `Stationpedia.Regenerate` 15-step sequence that builds every page from prefab data at load time, the `UniversalPage.ChangeDisplay` 6-step flow that populates the on-screen UI when a page is opened, and the two `Populate*` methods that iterate `EnumCollections.LogicTypes` to emit per-LogicType rows.

## UniversalPage.ChangeDisplay flow (steps 1-6)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Method at game.cs:234485. Body order:

1. Destroy every GameObject in `CreatedCategories`, clear the list.
2. Set `PageHeaderImage.sprite`.
3. Call `ResetUniversalPageInserts()`, clears all built-in categories' children.
4. `CheckAndSetTextElement(PageDescription, page.Description)`, plain assignment; no re-parse.
5. Call every other `CheckAndSetTextElement` (thermal, power, chemistry, etc.).
6. Call each `Populate*Inserts` method synchronously (game.cs:234554-234574):
   PopulateLifeRequirements, PopulateCustomCategories, PopulateSlotInserts,
   PopulateStructureVersion, PopulateLogicInserts, PopulateLogicInstructions,
   PopulateLogicSlotInserts, PopulateModeInserts, PopulateConnectionInserts,
   PopulateOreInserts, PopulateGasInserts, PopulateFermentationInserts,
   PopulateConstructedThings, PopulateUsedResources, PopulateUsedIn,
   PopulateCombustionInfo, PopulateProducedThings, PopulateKitInserts,
   PopulateHowToBuildInserts, PopulateBuildStatesInserts, PopulatePhaseDiagram.

A `ChangeDisplay` Postfix runs AFTER step 6. Mutating `page.LogicInsert` at that point does not retroactively render rows; vanilla already iterated. Injecting a new `StationpediaCategory` under `page.Content` does work (we do this in `CategoryBuilder`).

## Regenerate lifecycle (15 steps)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`Stationpedia.Regenerate()` at game.cs:231012-231040. Body:

1. `Instance.PopulateLists()`
2. `foreach (StationpediaPage p in StationpediaPages) p.ParsePage()`
3. `Instance.PopulateThingPages()`, builds Thing pages
4. `Instance.PopulateLogicVariables()`, builds LogicType pages
5. `Instance.PopulateLogicSlotVariables()`
6. `Instance.PopulateReagents()`
7. `Instance.PopulateGenes()`
8. `Instance.PopulateTrading()`
9. `Instance.PopulateGases()`
10. `Instance.PopulateFactionLorePages()`
11. `Instance.UpdateLinkedPages()`
12. `Instance.SetPage(CurrentPageKey)`
13. `Instance.SortPages()`
14. `GC.Collect()`
15. First-call only: subscribes `Regenerate` to `Localization.OnLanguageChanged`.

Call sites (exactly two):

- `GameManager.LoadGameDataAsync` at game.cs:59090, AFTER `await Prefab.LoadAll()` completes.
- `Localization.OnLanguageChanged` event, every language change.

Mod Harmony patches installed in `OnAllModsLoaded` are active before the first `Regenerate` runs.

`Regenerate` does NOT clear `StationpediaPages` at the top. Register's replace semantics handle deduplication on re-runs.

## PopulateThingPages body + AddLogicTypeInfo
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`Stationpedia.PopulateThingPages` at game.cs:231964. Body iterates `Prefab.AllPrefabs` and for each creates:

```csharp
StationpediaPage page3 = new StationpediaPage(
    $"Thing{allPrefab.PrefabName}", allPrefab.DisplayName);
```

At game.cs:232041:

```csharp
page3.Description = Localization.ParseHelpText(
    Localization.GetThingDescription(allPrefab.PrefabName));
```

`GetThingDescription` reads the `<RecordThing>/<Description>` entry from the language XML keyed on `Animator.StringToHash(prefabName)`.

Logic rows come from `AddLogicTypeInfo(Thing prefab, ref StationpediaPage page)` at game.cs:231184:

```csharp
LogicType[] values = EnumCollections.LogicTypes.Values;
for (int i = 0; i < values.Length; i++)
{
    LogicType logicType = values[i];
    bool flag  = logicable.CanLogicRead(logicType);
    bool flag2 = logicable.CanLogicWrite(logicType);
    if (!flag2 && !flag) continue;
    // ...
    stationLogicInsert.LogicName = Localization.ParseHelpText(
        "{LOGICTYPE:" + logicType.ToString() + "}");
    page.LogicInsert.Add(stationLogicInsert);
}
```

Because `LogicableInitializePatch` extends `EnumCollections.LogicTypes.Values` to include our custom values before `Regenerate` fires, and because our `CanLogicRead`/`Write` postfixes return true for those values, vanilla naturally adds our custom rows. No LogicInsert fallback is needed (Decision 11A).

## PopulateLogicVariables body + template
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`Stationpedia.PopulateLogicVariables` at game.cs:232142-232193:

```csharp
private void PopulateLogicVariables()
{
    StationpediaPage page = GetPage("LogicTypePageTemplate");
    if (page == null) return;
    LogicType[] values = EnumCollections.LogicTypes.Values;
    for (int i = 0; i < values.Length; i++)
    {
        LogicType logicType = values[i];
        if (LogicBase.IsDeprecated(logicType)) continue;
        try
        {
            string logicDescription = LogicBase.GetLogicDescription(logicType);
            string text = string.Format(page.Parsed, logicDescription);
            string text2 = EnumCollections.LogicTypes.GetName(logicType);
            string title = "LogicSlotType." + text2;
            StationpediaPage stationpediaPage = new StationpediaPage(
                "LogicType" + text2, title, text);
            // ... SoundAlert sub-loop ...
            stationpediaPage.Title = "LogicType." + EnumCollections.LogicTypes.GetName(logicType);
            stationpediaPage.Description = text;
            stationpediaPage.CustomSpriteToUse = VariableImage;
            stationpediaPage.ParsePage();
            Register(stationpediaPage);
        }
        catch (System.Exception ex2) { ... }
    }
}
```

Template body source: `LogicTypePageTemplate.Text` is just `{0}` from `english_help.xml`. `string.Format({0}, logicDescription)` resolves to the vanilla one-liner description.

Our `LogicTypePageBuilder` postfix runs AFTER this, replacing each of our custom LogicType pages with an enriched version via `Register(page, false)`.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0202, F0204, F0205, and F0206.

## Open questions

None at creation.
