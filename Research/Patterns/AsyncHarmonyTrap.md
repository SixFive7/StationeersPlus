---
title: Async Harmony trap
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/StationpediaPlus/PLAN.md:910-924 (F0219g, primary)
related:
  - ./HarmonyPatchTypes.md
  - ./HarmonyPrefixReturnBool.md
  - ../GameSystems/StationpediaSearch.md
tags: [harmony, threading]
---

# Async Harmony trap

Patching an `async` method with a Harmony Postfix does not intercept the method's work. The C# compiler rewrites `async` methods into a state machine; the outer method body Harmony sees is the kick-off stub that returns the awaitable immediately, before any of the asynchronous work has run. A Postfix on that stub fires when the awaitable is returned, not when the work completes, and has no access to any results produced inside the state machine. If the async method also mutates pooled state or writes UI widgets in-flight rather than returning a collection, there is no `__result` for a Postfix to filter either.

## Problem
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

An `async Task` or `async UniTask` method compiles to:

1. A stub method with the original signature that constructs a state machine, calls `MoveNext()` once, and returns the awaitable.
2. A generated state-machine type whose `MoveNext()` contains the real body split across continuations at each `await`.

Harmony's attribute path (`[HarmonyPatch(typeof(T), "Name")]`) resolves and patches the stub. The Postfix therefore:

- Fires when the awaitable is handed back, not when the async work completes.
- Cannot read values produced inside the state machine, because those are local to `MoveNext` frames and no `__result` exposes them.
- Cannot block or alter the work that runs after the first `await`.

This is not a Harmony bug: Harmony is patching exactly the method the attribute names. The trap is that the method the developer thinks they are patching (the full async body) is not the method the compiler actually exposes.

The trap compounds when the async method mutates pooled or UI state in-flight rather than returning a value. There is no `ref T __result` to edit; the side effects have already landed by the time any observer could see them.

## Solution / recipe
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Patch a synchronous method that the async body calls, not the async method itself. The compiler's state-machine rewrite only affects the outer method; any synchronous method invoked from inside `MoveNext` is a normal method. Harmony patches on those synchronous methods run as expected for every call, including calls made from inside async continuations.

Recipe:

1. Read the async method body. Identify the synchronous helper methods it calls per unit of work (per row, per page, per item).
2. Pick the helper whose signature exposes the value to filter or the side effect to intercept. A helper that returns `bool` and is called once per candidate is ideal: a Postfix with `ref bool __result` can short-circuit downstream work by returning `false`.
3. Patch that helper. The Prefix or Postfix runs synchronously inside the async method's continuation, with full access to `__instance` and arguments.

Verify the helper has few enough callers that patching it does not affect unrelated code paths. A single-caller helper is ideal; when the helper is shared, the Prefix must branch on call site (type of `__instance`, values of arguments) before acting.

### Concrete example

`Stationpedia.DoSearch(string hash, string pattern, CancellationTokenSource)` is `private async UniTask` and mutates a pooled `_SPDASearchInserts` list of UI widgets in-flight. It has no return list and no `ref` parameter a Postfix could filter. A Postfix on `DoSearch` therefore cannot remove results.

`StationpediaPage.IsRegexMatch(string hash, string pattern)` is synchronous, called once per page from inside `DoSearch`, returns `bool`, and has exactly one caller in the game. A Postfix with `ref bool __result` runs inside the async body's `MoveNext` continuation for every candidate page; returning `false` short-circuits the `MakePage` call downstream of the branch.

See [../GameSystems/StationpediaSearch.md](../GameSystems/StationpediaSearch.md) for the full trigger chain and why `IsRegexMatch` is the correct filter target for Stationpedia search.

## Cited verifications
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- F0219g (Plans/StationpediaPlus/PLAN.md:910-924): primary statement of the trap in the context of `Stationpedia.DoSearch` as an `async UniTask` at game.cs:230584, and the recipe of patching `StationpediaPage.IsRegexMatch` (single-caller, `bool`-returning, synchronous) instead.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created by lifting the generalized trap from GameSystems/StationpediaSearch.md (F0219g) per Phase 6 Pass B recommendation.

## Open questions

None at creation.
