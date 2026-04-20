---
title: CameraController
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/LLM/RESEARCH.md:504-514
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: CameraController
related:
  - ../GameSystems/CameraFilterPack.md
tags: [ui]
---

# CameraController

Vanilla game class driving the main player camera. Holds the CameraFilterPack MonoBehaviour components for existing vision effects.

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

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0091. No conflicts.

## Open questions

None at creation.
