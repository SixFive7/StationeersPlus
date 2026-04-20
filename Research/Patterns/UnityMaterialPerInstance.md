---
title: Unity material: per-instance clone via renderer.material
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/PowerTransmitterPlus/RESEARCH.md:607-609 (F0064, primary)
  - Mods/PowerTransmitterPlus/PowerTransmitterPlus/BeamPulseTrain.cs:22-30 (F0357)
  - Mods/PowerTransmitterPlus/PowerTransmitterPlus/BeamPulseTrain.cs:65-69 (F0358)
related: []
tags: [unity]
---

# Unity material: per-instance clone via renderer.material

Unity's `Renderer.material` getter clones the shared material on first access and caches the clone on the renderer. Reading the getter ONCE yields a per-instance material the mod can mutate without affecting other renderers. The mod MUST `Destroy(instanceMaterial)` in `OnDestroy` or the clone leaks.

## Problem
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

F0064 (Mods/PowerTransmitterPlus/RESEARCH.md:607-609, primary):

> `renderer.material` getter clones the shared material on first access and caches the clone on the renderer (subsequent reads return the same clone). `renderer.sharedMaterial` gives the original. Use `_lr.material` once in `Awake` to get a per-instance copy, store the reference. Must `Destroy(_instanceMaterial)` in `OnDestroy` to avoid a leak.

Without the `Destroy` call, each renderer that ever read `.material` leaves one `Material` instance uncollected per scene/session. For mods that spawn many renderers per placed structure (beam visualizers, overlays), this is a visible memory leak over long sessions.

Using `sharedMaterial` skips the clone but mutations apply to every renderer sharing that material; not what a per-instance tint/scroll effect wants.

## Solution / recipe
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

```csharp
// In Awake or early setup, once:
_instanceMaterial = _lineRenderer.material;  // getter triggers the clone

// Mutate freely:
_instanceMaterial.SetFloat("_ScrollSpeed", 2.0f);
_instanceMaterial.SetColor("_Tint", color);

// In OnDestroy:
if (_instanceMaterial != null)
{
    UnityEngine.Object.Destroy(_instanceMaterial);
}
```

Rules:

- Read `.material` exactly once per renderer lifetime and cache the reference. Re-reading is safe (the cached clone is returned) but stylistically hides the intent.
- `Destroy` goes in `OnDestroy`, not `OnDisable`. The clone's lifetime matches the renderer's.
- When the renderer itself is destroyed (whole GameObject), Unity handles cleanup via its own `OnDestroy`; the explicit `Destroy(material)` is still the safer pattern because component destruction order isn't guaranteed across Unity versions.

### Stretch-mode UV scaling observation

F0357 (code comment, `BeamPulseTrain.cs:22-30`):

> Stretch mode UV goes 0..1 across the line.

When driving a scrolling texture on a `LineRenderer` with stretch-mode UV, the V (or U) coordinate spans 0..1 end-to-end regardless of physical line length. Animate `material.mainTextureOffset` (or a shader `_ScrollSpeed` property) in world time, not in terms of line length, to get a consistent scroll rate across lines of different lengths.

### OnDestroy cleanup code

F0358 (code comment, `BeamPulseTrain.cs:65-69`) is the concrete `Destroy` call in the deployed patch, confirming the leak-avoidance rule from F0064.

## Cited verifications
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- F0064: primary rule statement on `.material` vs `.sharedMaterial` and the leak-avoidance `Destroy`.
- F0357: per-instance material used for stretch-mode UV scrolling in `BeamPulseTrain`.
- F0358: OnDestroy cleanup code in `BeamPulseTrain` confirming the pattern.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; F0064 primary, F0357 and F0358 confirming.

## Open questions

None at creation.
