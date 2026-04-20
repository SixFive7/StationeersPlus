---
title: StationpediaSearch
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/StationpediaPlus/PLAN.md:712-788
  - Plans/StationpediaPlus/PLAN.md:888-907
  - Plans/StationpediaPlus/PLAN.md:910-924
related:
  - ./StationpediaPageRendering.md
  - ../GameClasses/Stationpedia.md
  - ../GameClasses/StationpediaPage.md
tags: [stationpedia, ui]
---

# StationpediaSearch

The Stationpedia search pipeline: from the search field's `onValueChanged` callback through `DoSearch`'s async UniTask into `StationpediaPage.IsRegexMatch`. This page documents the trigger chain, the async state-machine split (and why a Postfix on `DoSearch` cannot filter results), and the `IsRegexMatch` single-caller fact that makes it the correct filter target.

## Search mechanism
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`Stationpedia.DoSearch(string hash, string pattern, CancellationTokenSource)` at game.cs:230584 is the ONLY path that iterates `StationpediaPages` for display (search results). Driven by `SearchField.onValueChanged` → `SearchBehaviour` → `StartSearchCountdown` → `ClearAndStartSearch` → `ForceSearch` → `DoSearch`.

`DoSearch` calls `StationpediaPage.IsRegexMatch(hash, pattern)` per page at game.cs:230602. This two-argument overload has exactly ONE caller in the entire game assembly (DoSearch). It returns bool indicating a match.

`StationpediaPage.IsRegexMatch(string text, string pattern)` body at game.cs:233709:

```csharp
public bool IsRegexMatch(string text, string pattern)
{
    if (text == PrefabHashString) return true;
    if (!Match(pattern, Title) && !Match(pattern, Key))
        return Match(pattern, Description);
    return true;
}
```

Truncates at 255 chars for regex matching.

Separate `HelpReference.IsRegexMatch(string pattern)` at game.cs:221825 is unrelated (IC10 help panel). Do NOT patch the one-arg overload.

Why we patch `IsRegexMatch` and not `DoSearch` directly: `DoSearch` is `private async UniTask`, meaning Harmony patches the state-machine kick-off method, the patch receives the `UniTask` return before iteration has run. A Postfix on `DoSearch` can't filter results because the results haven't been materialized at postfix time. `IsRegexMatch`, by contrast, is a regular synchronous method called from inside the async state machine's `MoveNext`, and Harmony-patched synchronous methods called from inside an async body ARE redirected correctly. This is why `IsRegexMatch` is the right lever. Full body of `DoSearch` (game.cs:230584-230614):

```csharp
private async UniTask DoSearch(string hash, string pattern, CancellationTokenSource cancelToken)
{
    if (string.IsNullOrEmpty(pattern) && string.IsNullOrEmpty(hash))
    {
        ClearPreviousSearch();
        NoResultsFromSearchText.SetActive(value: true);
        return;
    }
    NoResultsFromSearchText.SetActive(value: false);
    await UniTask.SwitchToThreadPool();
    int count = 0;
    int i = StationpediaPages.Count - 1;
    while (i >= 0 && count < searchResultsPerPage)
    {
        if (cancelToken.IsCancellationRequested) return;
        if (!string.IsNullOrEmpty(StationpediaPages[i].Title)
            && !StationpediaPages[i].Title.Equals("Search")
            && StationpediaPages[i].IsRegexMatch(hash, pattern))
        {
            StationpediaPage page = _linkIdLookup[StationpediaPages[i].Key];
            SPDAListItem insert = _SPDASearchInserts[count];
            MakePage(page, insert).Forget();
            await UniTask.Delay(20, DelayType.UnscaledDeltaTime);
            count++;
        }
        i--;
    }
    await UniTask.SwitchToMainThread();
    NoResultsFromSearchText.SetActive(count == 0);
}
```

Iterates `StationpediaPages` in reverse, up to `searchResultsPerPage` (100) results. Note it mutates pre-pooled `_SPDASearchInserts` in-flight rather than returning a list; patch targets that need to filter the final list would need to trace into `MakePage` or patch `_SPDASearchInserts` population, both hairier than patching `IsRegexMatch`.

## Search trigger chain (onValueChanged -> DoSearch)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Chain: `onValueChanged` -> `SearchBehaviour(text)` -> if non-empty, `SetPage("Search")` + `StartSearchCountdown` (400ms debounce) -> `WaitStartSearch` -> `ClearAndStartSearch(text)` -> `ClearPreviousSearch` + `ForceSearch` -> builds regex pattern -> `DoSearch(hash, pattern, cancelToken)` -> iterates `StationpediaPages` in reverse, calls `IsRegexMatch`, `MakePage` for each hit. Enter-key / submit triggers the same chain via `StartSearchNow` (zero-delay variant).

### DoSearch is async UniTask (cannot postfix directly)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Async methods that invoke Harmony-patched code cannot be filtered by patching the async method itself: the compiler's state-machine rewrite leaves only a kick-off stub under the original name, and a Postfix on that stub fires before the body runs. See [../Patterns/AsyncHarmonyTrap.md](../Patterns/AsyncHarmonyTrap.md) for the generalized pattern and fix. For Stationpedia search specifically: `DoSearch` (game.cs:230584) is `async UniTask` and mutates the pooled `_SPDASearchInserts` list in-flight, so there is no `__result` to edit. The mod patches `StationpediaPage.IsRegexMatch` instead, which is synchronous, returns `bool`, and has exactly one caller (DoSearch). A Postfix with `ref bool __result = false` on `IsRegexMatch` short-circuits the downstream `MakePage` call.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0208 (primary), F0219f, and F0219g.
- 2026-04-20: F0219g section replaced with pointer to Patterns/AsyncHarmonyTrap.md per Phase 6 Pass B recommendation.

## Open questions

None at creation.
