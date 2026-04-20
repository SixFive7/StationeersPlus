---
title: Human
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/SprayPaintPlus/RESEARCH.md:215-217
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Human
related:
  - ./Entity.md
  - ./ChatMessage.md
  - ./ILifeSuspender.md
  - ../GameSystems/StunStateMachine.md
tags: [entity]
---

# Human

Vanilla game class representing the player-controlled character entity. Lives under `Assets.Scripts.Objects.Entities` in the Entity hierarchy.

## Player identification
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0024.

The original mods used the LaunchPadBooster connection id to track which player pressed which modifier keys. But `AttackWithMessage` on the server does not carry the LaunchPadBooster connection id; it carries `AttackParentId`, which is the Human ReferenceId. Keying `PlayerModifiers` by Human ReferenceId matches the identifier available at paint time.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0024. No conflicts.

## Open questions

None at creation.
