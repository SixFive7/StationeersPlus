---
title: Entity
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/LLM/RESEARCH.md:262-270
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Entities.Entity
related:
  - ./Human.md
  - ./ILifeSuspender.md
  - ../GameSystems/StunStateMachine.md
tags: [entity, damage]
---

# Entity

Vanilla game class at `Assets.Scripts.Objects.Entities.Entity`. Base class for living in-world actors (Humans and animals). Drives state transitions between `Alive` and `Unconscious` and renders stun post-processing on the local camera.

## OnCameraUpdate stun post-processing
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0079.

`Entity.OnCameraUpdate()` applies post-processing based on stun level:
- Vignette intensity: 0 to 0.5 over stun 0-80
- Vignette blur: 0 to 1 over stun 0-50
- Color saturation: 1 to 0 over stun 0-100
- Brightness: 0.98 to 0 over stun 0-100

The screen progressively darkens and desaturates as stun increases. At 100, the screen is black.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0079. No conflicts.

## Open questions

None at creation.
