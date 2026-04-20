---
title: ChatCanvas
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/LLM/RESEARCH.md:68-74
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: ChatCanvas
related:
  - ./ChatMessage.md
  - ./Human.md
tags: [chat, ui]
---

# ChatCanvas

Vanilla game class attached to each `Human` entity. Hosts the floating chat bubble UI rendered above player heads.

## Chat bubble UI
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0095b.

`ChatCanvas` is attached to each `Human` entity and manages the floating chat bubble UI. `ChatWindow` handles the typewriter-style text animation. In `NetworkBase.DeserializeReceivedData()`, most message types log an error if processed on a non-server peer. `ChatMessage` is explicitly exempted from this check, meaning clients process it normally (print to console + show bubble). This is standard for broadcast messages.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0095b. No conflicts.

## Open questions

None at creation.
