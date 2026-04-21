---
title: CameraController
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-21
sources:
  - Plans/LLM/RESEARCH.md:504-514
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: CameraController
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: CameraEffectCollection
related:
  - ../GameSystems/CameraFilterPack.md
  - ../GameSystems/RenderingPipelineAndGlow.md
tags: [ui]
---

# CameraController

Vanilla game class driving the main player camera. Holds the `CameraFilterPack` MonoBehaviour components for existing vision effects, and a list of `CameraEffectCollection` entries that carry post-process effects (bloom, etc.).

## Existing filter-pack components
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0091.

Effects already active on `CameraController.Instance`:

| Field | Type | Purpose |
|---|---|---|
| `NightVisionFX` | `CameraFilterPack_NightVisionFX` | Night vision goggles |
| `WaterVisionFX` | `CameraFilterPack_Light_Water` | Underwater blur/tint |
| `LavaVisionFX` | `CameraFilterPack_NightVisionFX` | Under-lava vision |
| `SensorLensesVisionFX` | `CameraFilterPack_TV_80` | Sensor lenses scan lines |
| `SolarStormDistortionFX` | `CameraFilterPack_FX_Drunk` | Solar storm wobble |
| `SolarStormChromaticAberration` | `VignetteAndChromaticAberration` | Solar storm chromatic aberration |
| `CameraVignette` | `VignetteAndChromaticAberration` | Stun vignette + blur |
| `CameraColorControl` | `CameraFilterPack_Color_BrightContrastSaturation` | Brightness/saturation/contrast |

## Singleton and effect collections

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

`CameraController` follows the standard singleton pattern used by the game's manager classes:

- `public static CameraController Instance;` at decompile CameraController.cs line 39.

The camera's post-process effects live on a per-camera list rather than as direct fields on `CameraController`:

- `public List<CameraEffectCollection> CameraEffects = new List<CameraEffectCollection>();` at CameraController.cs line 224.

`CameraEffectCollection` (decompile CameraEffectCollection.cs line 8) exposes:

- `public UltimateBloom Bloom;` - the third-party UltimateBloom MonoBehaviour that drives the halo on emissive pixels.

Runtime read accessor:

```
var bloom = CameraController.Instance?.CameraEffects?[0]?.Bloom;
var on = bloom != null && bloom.enabled;
```

Toggle API: `CameraController.SetBloom(bool)` flips the component's `.enabled` state. See `../GameSystems/RenderingPipelineAndGlow.md` for how bloom turns emissive pixels into visible glow.

Reachable after the gameplay camera is active. Safe from `Update()` post-save-load; callers running during early Awake should null-check both `Instance` and `CameraEffects?.Count > 0`.

## Runtime attachment (partially resolved)

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

The `CameraEffects` accessor chain above is based on decompile inspection. Runtime probing via the `Plans/GlowPaintProbe/` plugin on 2026-04-21 observed `CameraController.Instance` non-null but `CameraController.Instance.CameraEffects.Count == 0` throughout gameplay (1800 tick retries on a Harmony postfix of `InventoryManager.NormalMode`, spanning ~30 seconds of active play). In the same session, emissive-painted pipes produced a visible bloom halo, so `UltimateBloom` IS active in the scene - just not reachable via `CameraController.Instance.CameraEffects[0].Bloom`.

A follow-up decompile pass on 2026-04-21 established:

- `CameraController.SetBloom(bool)` at CameraController.cs line 1146-1156 is the ONLY mutator of `UltimateBloom.enabled` in the decompile. Its body iterates `Instance.CameraEffects` and assigns `.Bloom.enabled` on each entry. With `CameraEffects` empty at runtime, this method is a no-op - which matches the probe observation that bloom works without ever going through this path.
- `CameraController.UpdateVolumeLight` is the single caller of `SetBloom`, invoked when the player changes `Settings.CurrentData.VolumeLight` via the graphics-settings dropdown.
- `UltimateBloom` declares `[RequireComponent(typeof(Camera))]`, so the component must live on a GameObject that also carries a `Camera` component.
- `CameraController.MainCamera` is an inspector-assigned field populated during `CameraController.ManagerAwake` (decompile line 420).

The conclusion is that `UltimateBloom` is attached directly to the `MainCamera` GameObject, not proxied through `CameraEffects`. The `CameraEffects` list is a dead field at runtime (initialized to an empty list at line 224, never populated by any code path).

Candidate runtime accessors for "is bloom on":

1. `CameraController.Instance?.MainCamera?.GetComponent<UltimateBloom>()?.enabled` (most likely)
2. `Camera.main?.GetComponent<UltimateBloom>()?.enabled` (fallback)
3. `CameraController.Instance?.CurrentCamera?.GetComponent<UltimateBloom>()?.enabled` (using the `CurrentCamera` property at line 299)

These are decompile-derived and not yet runtime-verified. A follow-up probe attaching `GetComponent<UltimateBloom>()` against each candidate in a Harmony postfix on `InventoryManager.NormalMode` would resolve the remaining ambiguity in one game-launch cycle.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0091. No conflicts.
- 2026-04-21: added "Singleton and effect collections" section documenting `Instance` (line 39), `CameraEffects` (line 224), and `CameraEffectCollection.Bloom` (line 8), with the runtime read accessor chain for UltimateBloom. Additive; no prior claim changed. Top-level `verified_at` bumped to 2026-04-21.
- 2026-04-21: added "Runtime attachment (unresolved)" section noting that `CameraEffects` is observed empty at runtime during gameplay despite bloom being visibly active. Decompile-derived accessor is retained (per lossless principle) but flagged as not runtime-verified. Corresponding Open Question added.
- 2026-04-21: upgraded the section to "Runtime attachment (partially resolved)" after a fresh decompile pass. `SetBloom(bool)` at line 1146-1156 iterates the empty `CameraEffects` list so its runtime effect is a no-op; the ONLY caller is `UpdateVolumeLight`. `UltimateBloom` declares `[RequireComponent(typeof(Camera))]`, and `CameraController.MainCamera` is an inspector-assigned field populated in `ManagerAwake` (line 420). The component therefore lives directly on the `MainCamera` GameObject, with `CameraEffects` as a dead / never-populated legacy field. Three candidate runtime accessors documented; Open Question narrowed from "where IS bloom attached?" to "which of the three candidates resolves at runtime?" (decompile-derived but not yet probe-verified).

## Open questions

- Which of the candidate accessors documented in "Runtime attachment (partially resolved)" actually returns the live `UltimateBloom` at runtime? Candidates are `CameraController.Instance.MainCamera.GetComponent<UltimateBloom>()`, `Camera.main.GetComponent<UltimateBloom>()`, and `CameraController.Instance.CurrentCamera.GetComponent<UltimateBloom>()`. The decompile evidence (the `[RequireComponent(typeof(Camera))]` attribute plus the empty `CameraEffects` field) points to the first; runtime verification via a probe plugin attaching `GetComponent<UltimateBloom>()` calls to a Harmony postfix would settle it.
