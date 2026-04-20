---
title: Cartridge
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/EquipmentPlus/RESEARCH.md:187-195
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Cartridge
related:
  - ./ConfigCartridge.md
  - ./AdvancedTablet.md
tags: [equipment, ic10]
---

# Cartridge

Vanilla game class representing a cartridge item slotted into an `AdvancedTablet`. Drives the per-frame screen update and the scroll behavior for the currently-displayed cartridge.

## Scroll plumbing
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0117.

- `Cartridge._scrollPanel` (protected, serialized) is the only scroll state container. No `_scrollOffset`, `_firstLine`, or `_topIndex` exists on Cartridge or ConfigCartridge. Scroll position is a single normalized `float` in `[0,1]` inside `ScrollPanel._scrollPosition` / `_scrollActualPosition`.
- `Cartridge.OnScroll(Vector2 scrollDelta)` is a one-line forwarder: `if (_scrollPanel && scrollDelta != Vector2.zero) _scrollPanel.OnScroll(scrollDelta);`. That's the ONLY scroll behavior vanilla exposes.
- `ScrollPanel.OnScroll` flips the sign (`y *= -1f`), multiplies by `_effectiveSensitivity`, and adds to `_scrollPosition`. Smooth pixel-based scroll, not line-based. No concept of a selected line in vanilla.
- `ScrollPanel.SetScrollPosition(float)` snaps `_scrollPosition = _scrollActualPosition = clamp01(position)` and calls `RefreshPosition()`. Instantaneous, no lerp.
- `Cartridge.UpdateEachFrame` calls `OnScreenUpdate` every frame for every cartridge in the tablet (not just the currently-displayed one), gated by `!IsOccluded && OnOff && Powered && (InPlayerHand || InUpdateRange)`. The `tablet.Cartridge == __instance` guard in click handling is load-bearing because the postfix fires for all cartridges.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0117 (Cartridge-side facts). No conflicts.

## Open questions

None at creation.
