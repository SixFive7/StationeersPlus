---
title: Harmony Prefix must return bool to block the original
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/EquipmentPlus/RESEARCH.md:259-260 (F0124)
related: []
tags: [harmony]
---

# Harmony Prefix must return bool to block the original

A Harmony `[HarmonyPrefix]` method returning `void` cannot block the original method. If the prefix advances mod-owned state while the vanilla method also runs, the two states desync. The fix: declare the prefix as `bool`-returning, and `return false` when the mod wants vanilla skipped.

## Problem
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

F0124 (Plans/EquipmentPlus/RESEARCH.md:259-260):

> If the scroll prefix is a `void` method (not returning `bool`), vanilla's `_scrollPanel.OnScroll(scrollDelta)` still runs after the prefix, so the viewport pans smoothly based on pixel sensitivity while the mod's selected-line index also advances. The two states desync: the highlighted line drifts away from what's visible. The upstream mod returns `bool` from the prefix and returns `false` after advancing the selected index, blocking vanilla's free-scroll entirely. The mod then drives the viewport explicitly via `_scrollPanel.SetScrollPosition((float)selected / (count - 1))`.

The concrete symptom in the cartridge case: the highlighted line is at index 3 (based on the mod's counter) but the viewport shows lines 7-12 (based on vanilla's pixel scroll). The user sees no highlight.

## Solution / recipe
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

When the prefix's purpose is to replace vanilla behavior (not augment it), declare the method as `bool`-returning and `return false` to block:

```csharp
[HarmonyPrefix]
public static bool Prefix(Cartridge __instance, float scrollDelta)
{
    AdvanceSelectedLine(__instance, scrollDelta);
    return false;  // block vanilla's _scrollPanel.OnScroll
}
```

When the mod's work supplements vanilla (wants both to run), `void` is correct, or `bool` returning `true`.

When the mod blocks the vanilla method, the mod MUST also perform any visible side-effect the vanilla method would have produced. In F0124's case, that means driving the scroll position explicitly with `_scrollPanel.SetScrollPosition(...)`. Otherwise the UI looks frozen.

Rule of thumb: if a mod's state advances in units that do NOT match vanilla's state, block vanilla (return `false`) and drive both states from the mod.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; single source (F0124) with concrete desync narrative.

## Open questions

None at creation.
