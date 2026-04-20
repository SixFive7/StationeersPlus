---
title: CameraFilterPack
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/LLM/RESEARCH.md:527-541
  - Plans/LLM/RESEARCH.md:500-502
related:
  - ../GameClasses/CameraController.md
  - ../GameClasses/Entity.md
  - ../Workflows/CameraEffectsRuntime.md
tags: [ui, entity]
---

# CameraFilterPack

Stationeers ships with the third-party CameraFilterPack library embedded as a set of shader-based Unity MonoBehaviours. 274 effects are available; each is added to the main camera with `AddComponent<>()`, parameters are set on the component, and the effect runs automatically. This page catalogs the library and the best-fit effects for gameplay use such as gas exposure, unconsciousness, EMP, and vision changes.

## Library overview
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

The game ships 274 shader-based camera effects as MonoBehaviour components (CameraFilterPack third-party library). All follow the same pattern: `AddComponent<>()` to the main camera, set parameters, enable/disable.

## Best distortion effects for gameplay use
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

| Effect class | Parameters | Use case |
|---|---|---|
| `CameraFilterPack_FX_Drunk` | `Fade`, `Distortion`, `Speed`, `Wavy` | Gas exposure, disorientation |
| `CameraFilterPack_Distortion_Dream` | `Distortion` (1-10) | Dream sequence, vision |
| `CameraFilterPack_FX_EarthQuake` | `Speed`, `X`, `Y` | Seismic event, explosion aftermath |
| `CameraFilterPack_Blur_GaussianBlur` | `Size` (1-16) | Unconsciousness without stun, transition |
| `CameraFilterPack_Noise_TV` | `Fade` (0-1) | Signal interference, EMP |
| `CameraFilterPack_TV_VHS` | `Cryptage`, `Parasite` | Corrupted feed |
| `CameraFilterPack_FX_Glitch1` | `Glitch` (0-1) | Digital glitch, power surge |
| `CameraFilterPack_TV_BrokenGlass` | `Broken_Small/Medium/High/Big` | Visor crack, decompression |
| `CameraFilterPack_Vision_Tunnel` | `Value`, `Value2`, `Intensity` | Tunnel vision, focus |
| `CameraFilterPack_Distortion_Heat` | `Distortion` (1-100) | Heat shimmer |
| `CameraFilterPack_Color_GrayScale` | `_Fade` (0-1) | Desaturation, fading |
| `CameraFilterPack_AAA_Blood_Hit` | `Hit_Full`, `Hit_Left/Right/Up/Down` | Damage flash |

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0092 and F0095r.

## Open questions

None at creation.
