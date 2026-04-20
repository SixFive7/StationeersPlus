---
title: StationpediaPage
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/StationpediaPlus/PLAN.md:424-432
  - Plans/StationpediaPlus/PLAN.md:437-460
  - Plans/StationpediaPlus/PLAN.md:680-706
  - Plans/StationpediaPlus/PLAN.md:920-924
  - Plans/StationpediaPlus/PLAN.md:980-1003
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: StationpediaPage
related:
  - ./Stationpedia.md
  - ./UniversalPage.md
  - ../GameSystems/StationpediaPageRendering.md
  - ../GameSystems/StationpediaSearch.md
tags: [stationpedia, ui]
---

# StationpediaPage

Vanilla game class at `StationpediaPage` (line 233507 in game decompile). Data model for one Stationpedia entry: the record holding `Key`, `Title`, `Text`, parsed `Description`, category fields, and the insert-list collections consumed by `UniversalPage`.

## Constructors and field inventory
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0201.

Constructors (game.cs:233666-233681):

```csharp
public StationpediaPage() { }
public StationpediaPage(string key, string title, string text)
    { Key = key; Title = title; Text = text; }
public StationpediaPage(string key, string title)
    { Key = key; Title = title; }
```

The `(key, title, text)` overload sets `Text`, NOT `Description`.

Key fields relevant to our implementation:

- `string Key`, unique ID, goes into `_linkIdLookup`
- `string Title`, display name, used in nav and search results
- `string Text`, raw markup source, consumed by `ParsePage`
- `string Description`, parsed body; this is what `ChangeDisplay` renders
- `int SortPriority`, ascending sort key
- `bool ImportantPage`, visual flag (not used by us)
- `SPDAEntryType DisplayFilter`, default Undefined; Guides/Lore add to category shortcuts
- `Sprite CustomSpriteToUse`, optional page thumbnail
- `List<StationLogicInsert> LogicInsert`, public mutable, eagerly initialized
- `List<StationLogicInsert> LogicSlotInsert`, `ModeInsert`, `ConnectionInsert`, same
- `List<StationCategory> CreatedCategories` on `UniversalPage` (not `page`), game-owned cleanup list

## ParsePage body and Description guard
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0207.

`StationpediaPage.ParsePage()` at game.cs:233683-233707:

```csharp
public void ParsePage()
{
    _parsed = Localization.ParseHelpText(Text);
    _parsed = _parsed.Replace('[', '<');
    _parsed = _parsed.Replace(']', '>');
    _parsed = _parsed.Replace("\t", string.Empty);
    _parsed = _parsed.TrimStart();
    foreach (string listOfAllListOfObject in Stationpedia.DataHandler.ListOfAllListOfObjects)
    {
        string text = "{LIST_OF_" + listOfAllListOfObject.ToUpper() + "}";
        if (Text.Contains(text))
            PageCustomCategories.Add(listOfAllListOfObject);
        if (Text.Contains(_worldHashes))
            _parsed = _parsed.Replace(_worldHashes, Localization.ParseHelpText(NewWorldMenu.WorldHashes));
        _parsed = _parsed.Replace(text, string.Empty);
    }
    if (string.IsNullOrEmpty(Description))
        Description = _parsed;
}
```

Critical guard at last line: `Description` is populated from `_parsed` only
if it was empty. Mutating `Text` after `ParsePage` has run does not update
`Description` unless `Description` is cleared or assigned directly.

`_parsed` is only set by `ParsePage`. The `Parsed` property (line 233664) is
a plain backing-field getter, no lazy parse.

## IsRegexMatch 255-char cutoff
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0219h.

`StationpediaPage.IsRegexMatch` (game.cs:233728) truncates at 255 chars when performing the regex match. Pages with Description longer than 255 chars will still match on Title and Key, but regex-body search is skipped. Irrelevant for our Ref pages (filtered out of search regardless) and acceptable for LogicType pages.

## ParsePage token expansion
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0219k (ParsePage-tokens portion).

`StationpediaPage.ParsePage` additionally handles `{LIST_OF_<CATEGORY>}` tokens and `_worldHashes` placeholder (magic token replaced with parsed `NewWorldMenu.WorldHashes`).

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0200, F0201, F0207, F0219h, F0219k. No conflicts.

## Open questions

None at creation.
