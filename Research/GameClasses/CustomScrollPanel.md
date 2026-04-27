---
title: CustomScrollPanel.ScrollPanel
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-27
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.UI.CustomScrollPanel.ScrollPanel
related:
  - ../GameSystems/ScrollInputHandling.md
tags: [ui]
---

# CustomScrollPanel.ScrollPanel

In-house scroll-view component used by ConfigCartridge and other rich-text panels. Not a Unity `ScrollRect`; a hand-rolled MonoBehaviour with a custom lerp toward a normalized scroll target.

## Public API
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

```csharp
public void SetContentHeight(float height);     // resizes the content rect
public void OnScroll(Vector2 scrollDelta);      // accumulates wheel delta into _scrollPosition
public void SetScrollPosition(float position);  // hard-snaps both target and actual
```

`SetScrollPosition` clamps `position` to [0..1] and writes both `_scrollPosition` (target) AND `_scrollActualPosition` (current), then calls `RefreshPosition()` to re-anchor the content and handle. There is no observable lerp on a `SetScrollPosition` call: the snap is immediate.

`OnScroll` writes only `_scrollPosition`; the visible scroll then lerps toward it inside `LateUpdate` at `Time.deltaTime * _scrollSpeed * _sensitivity`.

## Private state worth knowing
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

```csharp
[SerializeField] private float _scrollPosition;        // [0..1] target the user scrolled to
private float _scrollActualPosition;                    // [0..1] current (lerps toward _scrollPosition each LateUpdate)
private float _viewportHeight;                          // cached _viewportTransform.rect.size.y
private float _contentHeight;                           // cached _contentTransform.sizeDelta.y
private float _handleHeight;
private float _effectiveSensitivity;
[SerializeField] private bool _pinContentToBottom;
```

`_viewportHeight` and `_contentHeight` are populated by `RefreshSize()`, which runs on `SetContentHeight` calls only. Both are zero until the first `SetContentHeight` call lands. Reflection-based reads must accept that early-frame reads can return zero; treat that as "no scroll needed yet."

## Visibility math
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

`SetContentPosition()` (line 128) sets the content rect's anchored Y to:

```csharp
y = (_scrollActualPosition - (pinContentToBottom ? 1 : 0)) * (_contentHeight - _viewportHeight)
```

For the typical case (`_pinContentToBottom == false`):

- `_scrollActualPosition == 0` → top of content visible.
- `_scrollActualPosition == 1` → bottom of content visible.

The visible window covers the content's normalized Y range:

```
viewportFraction = _viewportHeight / _contentHeight                  // < 1 when content overflows
topFraction      = _scrollActualPosition * (1 - viewportFraction)
bottomFraction   = topFraction + viewportFraction
```

A point at normalized position `p` (where 0 = top of content, 1 = bottom) is visible iff `topFraction <= p <= bottomFraction`. When `_contentHeight <= _viewportHeight`, everything fits and any point is trivially visible.

To snap a target `selFrac` to the **nearest visible edge** (rather than dead center):

```
denominator = 1 - viewportFraction
if (selFrac < topFraction)    target = selFrac / denominator         // selection lands at viewport top
else if (selFrac > bottomFraction) target = (selFrac - viewportFraction) / denominator  // selection lands at viewport bottom
```

These derive from solving `topFraction == selFrac` and `bottomFraction == selFrac` for `_scrollActualPosition`.

## Reflection-based external read pattern
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

A mod that wants to ask "is content position p currently visible?" must read the three private fields by reflection:

```csharp
ReflectionUtils.TryGetField<float>(panel, "_scrollActualPosition", out var actualPos);
ReflectionUtils.TryGetField<float>(panel, "_viewportHeight",       out var viewportH);
ReflectionUtils.TryGetField<float>(panel, "_contentHeight",        out var contentH);
```

Fail-safe pattern: if any read returns false (or returns zero on an uninitialized panel), fall back to an unconditional `SetScrollPosition(selFrac)` to keep the selection in view at the cost of a jolt. Visibility-aware snap is purely a UX optimization; correctness must not depend on the reflection succeeding.

## Verification history

- 2026-04-27: page created. Verbatim findings from `ilspycmd` decompile of `E:/Steam/steamapps/common/Stationeers/rocketstation_Data/Managed/Assembly-CSharp.dll` at game version 0.2.6228.27061. Triggered by EquipmentPlus item E (viewport-aware snap on the ConfigCartridge scroll path); the mod previously called `SetScrollPosition` unconditionally on every wheel-tick and every postframe rebuild, which jolts the panel even when the selection is already on screen. Documents the `_scrollActualPosition / _viewportHeight / _contentHeight` field shape, the linear visibility math derived from `SetContentPosition`, and the nearest-edge snap formula.

## Open questions

None at creation.
