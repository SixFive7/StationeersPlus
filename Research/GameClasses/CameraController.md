---
title: CameraController
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-27
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

## Camera zoom and first / third-person toggle (Shift+scroll handler)
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

`CameraController.CacheCameraPosition()` (Assembly-CSharp.dll big-file decompile line 185625) contains the vanilla mouse-wheel zoom + viewpoint-switch handler. This is the binding that owns plain Shift+scroll in vanilla Stationeers; any mod that wants to bind a Shift-bearing scroll combo must coexist with or suppress this handler.

Verbatim block from `CacheCameraPosition`, line 185660-185674:

```csharp
if (!Cursor.visible
    && !(InventoryManager.Instance.ActiveHand.Slot.Occupant as Tablet)
    && KeyManager.GetButton(KeyMap.ThirdPersonControl)
    && Settings.CurrentData.MouseWheelZoom
    && !EventSystem.current.IsPointerOverGameObject())
{
    if (Input.mouseScrollDelta.y > 0f)
    {
        ThirdPersonOrbitCam.zoomOffset.z = Mathf.Clamp(ThirdPersonOrbitCam.zoomOffset.z + 0.5f, -1f, 4f);
    }
    else if (Input.mouseScrollDelta.y < 0f)
    {
        ThirdPersonOrbitCam.zoomOffset.z = Mathf.Clamp(ThirdPersonOrbitCam.zoomOffset.z - 0.5f, -1f, 4f);
    }
    if (ThirdController.enabled != ThirdPersonOrbitCam.zoomOffset.z < 2f)
    {
        SetThirdPersonCamera(!ThirdController.enabled);
    }
}
```

Behavior:

- Wheel-up increases `ThirdPersonOrbitCam.zoomOffset.z` by 0.5; wheel-down decreases by 0.5. Clamped to `[-1, 4]`.
- The viewpoint flips when `zoomOffset.z` crosses the 2.0 threshold: above 2.0 the handler enables `ThirdController` (third-person orbit cam); below 2.0 it disables (first-person). The toggle and the distance adjustment are the same input event - one Shift+scroll notch can both shift the camera and flip viewpoints.

Five gates, all required:

| Gate | Meaning | Bypass implication |
|---|---|---|
| `!Cursor.visible` | Not in a menu / cursor-locked gameplay focus | Standard "in gameplay" check |
| `!(InventoryManager.Instance.ActiveHand.Slot.Occupant as Tablet)` | Active hand does NOT hold a `Tablet` | **Holding a tablet completely disables this handler.** Mods that bind Shift+scroll while a tablet is in the active hand do not need to suppress vanilla. |
| `KeyManager.GetButton(KeyMap.ThirdPersonControl)` | The remappable "ThirdPersonControl" key is held | Default is `KeyCode.LeftShift` (Assembly-CSharp.dll line 43360 + 43427). Players can rebind. |
| `Settings.CurrentData.MouseWheelZoom` | Player has the option enabled in graphics/control settings | Players can disable globally |
| `!EventSystem.current.IsPointerOverGameObject()` | Cursor is not over a Unity UI element | Standard UI-suppression check |

Modifier matching is **inclusive**: `KeyManager.GetButton(KeyCode.LeftShift)` returns true when LeftShift is held alone OR with any other modifier (Ctrl+Shift, Alt+Shift, Ctrl+Shift+Alt, etc.). The handler does not check for the absence of Ctrl or Alt. Consequence: any scroll combo that contains Shift fires this handler, not just bare Shift+scroll.

Implication for mods binding scroll combos:

- **Combos without Shift** (Ctrl+scroll, Alt+scroll, Ctrl+Alt+scroll): no conflict, handler does not fire.
- **Combos containing Shift** (Shift+scroll, Ctrl+Shift+scroll, Alt+Shift+scroll, etc.): handler fires alongside the mod's binding unless the mod either (a) ensures the active hand holds a `Tablet` at the time the scroll fires (vanilla-built-in bypass), (b) Harmony-prefixes `CameraController.CacheCameraPosition` and returns false on the modifier match, or (c) zeroes `Input.mouseScrollDelta` before the camera handler reads it (more invasive).

The `KeyMap.ThirdPersonControl` default-key assignments live in:
- `KeyMap.ThirdPersonControl = KeyCode.LeftShift;` (Assembly-CSharp.dll line 43360, default-keys initializer)
- `KeyMap._ThirdPersonControl.AssignKey(KeyCode.LeftShift);` (Assembly-CSharp.dll line 43427, parallel `_`-prefixed assignment used by the rebindable-key system)

`MouseWheelZoom` is a player-toggleable setting: `Settings.CurrentData.MouseWheelZoom`. Players who disable it skip the handler entirely regardless of modifier or active-hand state.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0091. No conflicts.
- 2026-04-21: added "Singleton and effect collections" section documenting `Instance` (line 39), `CameraEffects` (line 224), and `CameraEffectCollection.Bloom` (line 8), with the runtime read accessor chain for UltimateBloom. Additive; no prior claim changed. Top-level `verified_at` bumped to 2026-04-21.
- 2026-04-21: added "Runtime attachment (unresolved)" section noting that `CameraEffects` is observed empty at runtime during gameplay despite bloom being visibly active. Decompile-derived accessor is retained (per lossless principle) but flagged as not runtime-verified. Corresponding Open Question added.
- 2026-04-21: upgraded the section to "Runtime attachment (partially resolved)" after a fresh decompile pass. `SetBloom(bool)` at line 1146-1156 iterates the empty `CameraEffects` list so its runtime effect is a no-op; the ONLY caller is `UpdateVolumeLight`. `UltimateBloom` declares `[RequireComponent(typeof(Camera))]`, and `CameraController.MainCamera` is an inspector-assigned field populated in `ManagerAwake` (line 420). The component therefore lives directly on the `MainCamera` GameObject, with `CameraEffects` as a dead / never-populated legacy field. Three candidate runtime accessors documented; Open Question narrowed from "where IS bloom attached?" to "which of the three candidates resolves at runtime?" (decompile-derived but not yet probe-verified).
- 2026-04-27: added "Camera zoom and first / third-person toggle (Shift+scroll handler)" section. Verbatim block from `CameraController.CacheCameraPosition` (Assembly-CSharp.dll big-file decompile line 185625, handler block at line 185660-185674) decompiled at v0.2.6228.27061 via `ilspycmd` against `E:/Steam/steamapps/common/Stationeers/rocketstation_Data/Managed/Assembly-CSharp.dll`. Documents the five gates (`!Cursor.visible`, active-hand-not-Tablet, `KeyManager.GetButton(KeyMap.ThirdPersonControl)`, `Settings.CurrentData.MouseWheelZoom`, `!IsPointerOverGameObject`), the zoom math (`zoomOffset.z` ± 0.5 clamped `[-1, 4]`), the 2.0-threshold viewpoint flip, and the inclusive modifier match (Shift held alone OR with any other modifier). `KeyMap.ThirdPersonControl` default `KeyCode.LeftShift` confirmed at line 43360 + 43427. Triggered by an investigation into vanilla Shift+scroll bindings for EquipmentPlus's planned scroll-modifier rebinds (Plans/EquipmentPlus/TODO.md item B); answers the question "which combos containing Shift are safe?" with: none, unless the active hand is a Tablet or the player has `MouseWheelZoom` disabled. Additive section; no prior content changed. Top-level `verified_at` bumped to 2026-04-27.

## Open questions

- Which of the candidate accessors documented in "Runtime attachment (partially resolved)" actually returns the live `UltimateBloom` at runtime? Candidates are `CameraController.Instance.MainCamera.GetComponent<UltimateBloom>()`, `Camera.main.GetComponent<UltimateBloom>()`, and `CameraController.Instance.CurrentCamera.GetComponent<UltimateBloom>()`. The decompile evidence (the `[RequireComponent(typeof(Camera))]` attribute plus the empty `CameraEffects` field) points to the first; runtime verification via a probe plugin attaching `GetComponent<UltimateBloom>()` calls to a Harmony postfix would settle it.
