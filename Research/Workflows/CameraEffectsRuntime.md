---
title: Camera Effects at Runtime
type: Workflows
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/LLM/RESEARCH.md:516-524
related:
  - TimeSkipWorldManipulation.md
  - ../GameClasses/Entity.md
tags: [ui, harmony, entity]
---

# Camera Effects at Runtime

Add or remove a `CameraFilterPack` shader effect on the main camera at runtime, and override the stun-driven effects that `Entity.OnCameraUpdate` resets every frame. Reach for this recipe when a mod wants to flash a distortion or color shift during a scripted event.

## When to use
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- A mod wants to add a shader-based camera effect (distortion, chromatic aberration, greyscale fade) that is not already baked into the game's effect pipeline.
- A mod wants to override `CameraVignette` or `CameraColorControl` regardless of the player's stun value.

The game ships 274 shader-based camera effects as `MonoBehaviour` components (CameraFilterPack third-party library). All follow the same pattern: `AddComponent<>()` to the main camera, set parameters, enable / disable.

## Prerequisites
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- Client-side code path (camera effects are per-client visuals, not server state).
- A reference to the main camera via `CameraController.Instance.MainCamera`.

## Steps
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Adding a new effect at runtime:

```csharp
var cam = CameraController.Instance.MainCamera;
var fx = cam.gameObject.AddComponent<CameraFilterPack_Distortion_Dream>();
fx.Distortion = 5f;
// Remove: fx.enabled = false; or Object.Destroy(fx);
```

## Overriding `CameraVignette` and `CameraColorControl`
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`Entity.OnCameraUpdate` runs every frame and resets `CameraVignette` and `CameraColorControl` based on actual stun value. To override these, use a Harmony postfix on `Entity.OnCameraUpdate`.

## Verification
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- Observe the shader effect in-game the frame after `AddComponent<>()`.
- For the override case, snapshot `CameraVignette` and `CameraColorControl` parameters after your postfix runs and confirm they hold the values the postfix set, rather than the stun-derived values vanilla writes.

## Pitfalls
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- Client-local effect. Do not rely on a camera effect being visible on other players unless you broadcast the trigger and each client applies its own effect.
- `CameraVignette` and `CameraColorControl` are vanilla-written every frame in `Entity.OnCameraUpdate`. A simple `AddComponent` will not hold a custom value across frames; a Harmony postfix on `OnCameraUpdate` is required to persist overrides.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0095s (`Plans/LLM/RESEARCH.md:516-524`).

## Open questions

None at creation.
