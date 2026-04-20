---
title: StationpediaAscendedInternals
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/StationpediaPlus/PLAN.md:1050-1078
  - Plans/StationpediaPlus/PLAN.md:1082-1094
  - Plans/StationpediaPlus/PLAN.md:1141-1151
  - Plans/StationpediaPlus/PLAN.md:1099-1104
  - Plans/StationpediaPlus/PLAN.md:1154-1168
  - Plans/StationpediaPlus/PLAN.md:1180-1188
  - Plans/StationpediaPlus/PLAN.md:1190-1196
  - Plans/StationpediaPlus/PLAN.md:1198-1208
  - Plans/StationpediaPlus/PLAN.md:1210-1222
  - Plans/StationpediaPlus/PLAN.md:1224-1266
  - Plans/StationpediaPlus/PLAN.md:1268-1290
  - Plans/StationpediaPlus/PLAN.md:3646-3670
related:
  - ./StationpediaPageRendering.md
  - ./StationpediaSearch.md
  - ./ThirdPartyModIdentities.md
  - ../Protocols/SPADeviceDatabase.md
  - ../Patterns/BestEffortIntegration.md
tags: [stationpedia, spa, ui]
---

# StationpediaAscendedInternals

Reverse-engineered internals of the third-party Stationpedia Ascended (SPA) mod, as they pertain to an integrating mod: SPA's Harmony patch list, what SPA deliberately does NOT patch, the `ChangeDisplay` postfix flow, microwave-device coverage, the tooltip resolution chain, the `SearchPatches` state and hide-policy, SPA's own link-handler inventory, and the 100ms tooltip polling coroutine.

## SPA Harmony patches full list + SPA does NOT patch
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

All imperative `_harmony.Patch(...)`, default `Priority.Normal`, no priority attributes anywhere in SPA's codebase.

| Target | Kind |
|---|---|
| `UniversalPage.ChangeDisplay` | Postfix |
| `UniversalPage.PopulateLogicSlotInserts` | Postfix (cosmetic slot compaction) |
| `Stationpedia.OnDrag` | Prefix |
| `Stationpedia.OnBeginDrag` | Prefix |
| `Stationpedia.ClearPreviousSearch` | Postfix (UI state only, not filtering) |
| `Stationpedia.SetPage` | Prefix (custom key navigation) |
| `Stationpedia.SetPageGuides` | Postfix |
| `Stationpedia.SetPageLore` | Prefix + Postfix |
| `KeyManager.SetupKeyBindings` | Postfix |
| `KeyManager.GetButtonDown` | Prefix |

**SPA does NOT patch:**
- `Stationpedia.PopulateLogicVariables`
- `Stationpedia.PopulateThingPages`
- `Stationpedia.Register`
- `StationpediaPage.IsRegexMatch` (our primary search-filter target)
- `Stationpedia.DoSearch`
- `Stationpedia.PopulateGuideLoreContents`
- `page.LogicInsert`, `PopulateLogicInserts`

So our three primary hooks and one optional secondary hook all land on SPA-untouched methods except `UniversalPage.ChangeDisplay`, where we coordinate via `[HarmonyAfter]`.

## SPA ChangeDisplay postfix flow
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

SPA's ChangeDisplay_Postfix at HarmonyPatches.cs:111: 1) Null-guards. 2) Destroys prior SPA-owned children by name: `GuideSectionsContent`, `SurvivalManualContent`, `OperationalDetailsCategory`. 3) Dispatches special pages (SurvivalManual, custom JSON guides, etc.). 4) `DeviceDatabase.TryGetValue(pageKey, out descriptionEntry)`; early return on miss. 5) Applies `pageDescription` / `Prepend` / `Append` if set. 6) Creates `OperationalDetailsCategory` at sibling index 20 if entry has non-empty `operationalDetails`.

## SPA microwave coverage
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

From shipped `descriptions.json`: `ThingStructurePowerTransmitter` (line 24385), empty `operationalDetails: []`; `logicDescriptions` covers vanilla LogicTypes only. `ThingStructurePowerTransmitterReceiver` (line 24524), same. `ThingStructurePowerTransmitterOmni` (line 24482), same. None of PowerTransmitterPlus's six custom LogicType names appear in SPA's JSON. Without `SpaBridge` enrichment, SPA users hovering custom rows see "No detailed description available yet."

### SPA LoadDescriptions synchronous readiness
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

SPA `DeviceDatabase` is populated synchronously by `LoadDescriptions` inside SPA's Awake. By the time any BepInEx `OnAllModsLoaded` callback fires, the database is ready.

## SPA tooltip flow + resolution chain
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

A 100ms polling coroutine watches `Stationpedia.CurrentPageKey`. On page change, iterates every `SPDALogic` component under `LogicContents.Contents` and attaches `SPDALogicTooltip`. Resolution chain:

1. `DeviceDatabase[deviceKey].logicDescriptions[cleanName]`
2. `GenericDescriptions.logic[cleanName]`
3. `GenericDescriptionsData.AdditionalData[cleanName]`
4. Placeholder: "No detailed description available yet."

`CleanName` strips `<[^>]+>` tags and trims. No case normalization. So our `LogicName` column stripping leaves the bare enum name as the lookup key.

Our `TextElementFactory` produces raw TMP elements (not `SPDALogic`), so SPA's coroutine ignores our custom content in extension sections. That's the intended behavior.

### SPA AdditionalData tooltip fallback (JsonExtensionData)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Tooltip resolution chain step 3 is `GenericDescriptionsData.AdditionalData[cleanName]`, a `[JsonExtensionData]` catch-all dictionary that Newtonsoft.Json uses for unknown JSON properties deserialized into `GenericDescriptionsData`. SPA uses it as a dynamic bucket for community-authored descriptions not captured by the typed schema. Not relevant to SpaBridge flow (writes to the typed `logicDescriptions` dict, tier 1).

### SPA GenericDescriptions fallback property
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`StationpediaAscendedMod.GenericDescriptions` is a public static `GenericDescriptionsData` property populated from `descriptions.json`'s `genericDescriptions` entry. Used as fallback tier 2 in tooltip resolution. SpaBridge does NOT write to it; it targets per-device `logicDescriptions` only.

### SPA destroys prior children by name (distinct-name convention)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

SPA's `ChangeDisplay_Postfix` destroys any pre-existing children of `UniversalPage.Content` named `OperationalDetailsCategory`, `GuideSectionsContent`, or `SurvivalManualContent` before re-creating its own. Our `<ModName>Details` GameObjects are NOT in that destroy list, so SPA's postfix leaves them alone. If we named ours `OperationalDetailsCategory`, SPA would destroy it on every navigation.

### SPA row-iteration prefab filter (SPDALogic only)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

SPA's `AddTooltipsToCategory` specifically filters for `SPDALogic` component instances under `LogicContents.Contents`. Our `TextElementFactory.Create` produces raw `TextMeshProUGUI` GameObjects (not `SPDALogic`), so SPA's coroutine skips them. SPA only decorates vanilla-populated rows, which includes our custom LogicType rows (those ARE `SPDALogic` instances produced by vanilla `PopulateLogicInserts`). Net effect: SPA tooltips appear on our native custom rows (after SpaBridge), SPA ignores our extension section's text elements.

## SPA SearchPatches caches + ShouldHideFromSearch policy
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

SPA's `SearchPatches` class (at `StationpediaAscended.Patches\SearchPatches.cs`) owns the following static state:

- `_pageTitleIndex` / `_pageWordIndex`, inverted indexes from words/titles to `StationpediaPage` references. Built once by `BuildPageIndexes` on first need, rebuilt on explicit invalidation.
- `_hideFromSearchCache`, memoized bool per page computed by `ShouldHideFromSearch`.
- `_lastSearchText`, `_lastResultCount`, used to skip redundant reorganizations when the visible result count hasn't changed.

Key methods:

- `BuildPageIndexes`, scans `Stationpedia.StationpediaPages`, skips pages failing `ShouldHideFromSearch`, populates the two indexes.
- `ShouldHideFromSearch(StationpediaPage page)`, SPA's own hide policy. Based on name patterns like `Ruptured`, `Burnt`, `Wreckage`. Returns `true` (hide) for pages matching those patterns. This is SPA's opinion; our mod's hidden keys are NOT in this set.
- `ReorganizeSearchResults`, runs after vanilla `DoSearch` populates its pool; adds SPA category headers to the search UI, may inject missing matches from its own indexes via `FindMissingMatches` / `InjectMissingResults`.
- `ClearPreviousSearch_Postfix`, patches vanilla `Stationpedia.ClearPreviousSearch` to take care of SPA-owned category headers and to lazily register listeners on `SearchField` / build indexes.

Risk surface for our hidden pages: if SPA's title/word indexes contain our `PowerTransmitterPlus_*` keys AND SPA's `FindMissingMatches` decides a query matches them, SPA could inject them as "missing results" even after our `IsRegexMatch` postfix returned false. Mitigated in practice because SPA builds its indexes via `BuildPageIndexes` which skips pages failing `ShouldHideFromSearch`, our pages DO pass `ShouldHideFromSearch` (SPA doesn't know about our mod), so they ARE in SPA's indexes. Whether SPA re-injects them depends on whether the typed query's regex happens to match our page titles or keys. For `PowerTransmitterPlus_...` keys that's unlikely (players rarely type the full prefix), but flagged as open item O4 (§17) for runtime test verification.

Escape hatch if observed: soft-reflective postfix on SPA's `ShouldHideFromSearch` to return true when the page's key is in our `HiddenKeys` set. Implement only if runtime test T5 shows re-injection.

## SPA UI-side click handlers + reason to ship our own
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

SPA ships two of its own `IPointerClickHandler` MonoBehaviours in `StationpediaAscended.UI\`:

- `TocLinkHandler`, attached to dynamic TMP elements SPA creates inside its operational-detail sections. Handles `toc_*` link clicks (its own Table-of-Contents anchor system) AND falls through to `Stationpedia.Instance.SetPage(linkID, true)` for standard links. Includes hover-color feedback via vertex colors.
- `CategoryHeaderHandler`, attached to SPA's search-result category headers for click-to-expand behavior on the Search page.

We do NOT reuse `TocLinkHandler` even though our use case is similar because:
- Reuse would require reflection attach of an SPA type, which creates a soft dependency on SPA's assembly (against our architecture, §2).
- SPA's handler understands `toc_*` links we don't emit (harmless but unnecessary code path).

Our `SixFive7LinkHandler` (§8.5) is the click-only equivalent without SPA dependency. It lacks hover-color feedback, compensated by the mandatory click-phrasing authoring rule (§11.3).

## SPA tooltip coroutine timing
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`MonitorStationpediaCoroutine` (SPA `StationpediaAscendedMod.cs`) polls `Stationpedia.CurrentPageKey` at a 100ms interval. On a page-change detection, it schedules `AddTooltipsAfterDelay` via a 2-frame delay (Unity coroutine `yield return null` twice) to let vanilla `UniversalPage.ChangeDisplay` finish populating `LogicContents.Contents` before tooltip attachment iterates the rendered children.

Tooltip flow per detected page change:
1. Wait 2 frames.
2. Call `AddTooltipsToCategory(universalPageRef.LogicContents, pageKey, "Logic")`.
3. Iterate transforms under `LogicContents.Contents`; for each, get its `SPDALogic` component; if non-null and no existing `SPDALogicTooltip`, attach a new `SPDALogicTooltip` MonoBehaviour seeded with `pageKey`, `component.InfoValue.text` (the rendered row name; tag-stripped by `CleanLogicTypeName`), and `"Logic"` as the category name.

Implication: our custom LogicType rows (which render as `SPDALogic` prefab instances inside the vanilla Logic Variables category) are automatically decorated by SPA's coroutine regardless of whether SpaBridge ran. SpaBridge supplies the tooltip TEXT content via `DeviceDatabase` enrichment; without SpaBridge, SPA's tooltip shows the "No detailed description available yet." placeholder. Decision 16 structural enforcement ensures SpaBridge runs every time.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0212, F0214, F0215, F0216, F0219m, F0219n, F0219af, F0219o, F0219p, F0219q, F0219r, and F0248 (primary for tooltip-timing; F0219w merges per MigrationMap §5.1).

## Open questions

None at creation.
