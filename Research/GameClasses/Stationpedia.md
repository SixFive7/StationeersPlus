---
title: Stationpedia
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/StationpediaPlus/PLAN.md:424-432
  - Plans/StationpediaPlus/PLAN.md:534-562
  - Plans/StationpediaPlus/PLAN.md:1296-1312
  - Plans/StationpediaPlus/PLAN.md:857-871
  - Plans/StationpediaPlus/PLAN.md:873-886
  - Plans/StationpediaPlus/PLAN.md:980-1003
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.UI.Stationpedia
related:
  - ./StationpediaPage.md
  - ./UniversalPage.md
  - ./HelpLinkHandler.md
  - ../GameSystems/StationpediaPageRendering.md
  - ../GameSystems/StationpediaSearch.md
tags: [stationpedia, ui]
---

# Stationpedia

Vanilla game class at `Assets.Scripts.UI.Stationpedia`. Singleton controller of the in-game Stationpedia window. Exposes `Register`, `SetPage`, `OnPageChanged`, and owns the row prefab inventory used by page rendering.

## Core classes and line references
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0200.

| Class | Line | Purpose |
|---|---|---|
| `Stationpedia : ResizableWindow, IModal` | 230120 | Singleton controller of the pedia window |
| `StationpediaPage` | 233507 | Data model for one page |
| `StationpediaCategory : UserInterfaceBase` | 233199 | UI for one collapsible category |
| `UniversalPage : UserInterfaceBase` | 233792 | Whole-page renderer |
| `SPDALogic : UserInterfaceBase` | 233092 | One logic-row prefab (two TMP fields) |
| `StationLogicInsert` | 233362 | Data model for one logic row |
| `HelpLinkHandler : UserInterfaceBase, IPointerClickHandler, ...` | 221638 | Vanilla TMP link-click handler (uses `WorldManager.IsGamePaused` in LateUpdate) |
| `SPDAEntryType` (enum) | 233007-233017 | Members: Undefined, Guides, Lore, Maximum |

## Register semantics and fallback modes
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0203.

`Stationpedia.Register(StationpediaPage page, bool fallback = false)` at
game.cs:230948-230969:

```csharp
public static void Register(StationpediaPage page, bool fallback = false)
{
    _linkIdLookup.TryGetValue(page.Key, out var value);
    if (!fallback || value == null)
    {
        if (value != null)
        {
            _linkIdLookup.Remove(value.Key);
            StationpediaPages.Remove(value);
        }
        if (page.DisplayFilter == SPDAEntryType.Guides) GuidesPages.Add(page.Key);
        else if (page.DisplayFilter == SPDAEntryType.Lore) LorePages.Add(page.Key);
        StationpediaPages.Add(page);
        _linkIdLookup.Add(page.Key, page);
    }
}
```

- `fallback:false` (default): always replaces existing entry.
- `fallback:true`: inserts only if key is missing.

Both `StationpediaPages` (public list) and `_linkIdLookup` (private dict)
are kept consistent on replace. The page object reference is shared; mutation
via one is visible via the other.

## Row prefab inventory
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0217.

Row prefabs visible as public fields on `Stationpedia.Instance`
(game.cs:230122-230150):

```csharp
public SPDAListItem ListInsertPrefab;
public SPDAListItem ListSearchPrefab;
public SPDACombustionItem CombustionItemPrefab;
public StationpediaCategory CategoryPrefab;          // what we clone
public SPDASlot SlotInsertPrefab;
public SPDAManufacturer ManufactureInsertPrefab;
public SPDAVersion MachineTierInsertPrefab;
public SPDALogic LogicInsertPrefab;                  // logic row prefab
public SPDAGeneric GenericPrefab;
public SPDAFoundIn FoundInInsertPrefab;
public SPDAFoundIn FermentationInsertPrefab;
public SPDAGeneric InfoBoxPrefab;
public SPDALifeRequirement LifeRequirementPrefab;
public SPDAHomePageCategory HomePageButtonPrefab;
```

Our helper uses `CategoryPrefab` (for cloning collapsible sections). The
`LogicInsertPrefab` would be used if we ever needed to inject SPDALogic
rows manually (Decision 11A says we don't).

## OnPageChanged public event
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0219d.

`Stationpedia.OnPageChanged` at game.cs:230434 is a `public static event Event` fired from `SetPage` (line 230781) whenever navigation changes. Stable, public, no reflection needed. Available as an alternative to Harmony patching for mods that want to react to page navigation without intercepting the render.

## Register leak on re-register
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0219e.

`Register` appends to `GuidesPages` or `LorePages` based on `page.DisplayFilter` (lines 230958-230965) but NEVER removes stale entries. If a mod re-registers a page with a different DisplayFilter, the old entry leaks. Not relevant to StationpediaPlus because reference pages use `SPDAEntryType.Undefined` and LogicType pages are re-registered with same filter.

## Page navigation back-stack
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0219k (back-stack portion).

`Stationpedia` maintains a private `_pageHistory` list for the back-button navigation state. Our `SixFive7LinkHandler` routes clicks to `SetPage`, so cross-page navigation participates normally in the back stack.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0200, F0203, F0217, F0219d, F0219e, F0219k. No conflicts.

## Open questions

None at creation.
