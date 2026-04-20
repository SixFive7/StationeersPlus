---
title: RespawnFlow
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/LLM/RESEARCH.md:493-495
related:
  - ../GameClasses/Human.md
  - ../GameClasses/LanderCapsule.md
tags: [entity]
---

# RespawnFlow

The full Stationeers respawn path creates a brand-new `Human` rather than reviving the existing body. Important context for any mod that wants to manipulate "entering a capsule" (time-skip, knockout) without triggering respawn semantics.

## Full respawn flow creates new Human
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

The full respawn creates an entirely new `Human` via `Human.CreateCharacter()`. This resets all stats, creates new organs, empties inventory (old items go into a CardboardBox on the ground). The old body becomes a `DynamicBodyBag`. None of this happens when you just create a `LanderCapsule` and move a living player into it.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0095q (Plans/LLM/RESEARCH.md:493-495).

## Open questions

None at creation.
