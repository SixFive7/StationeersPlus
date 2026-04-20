---
title: ChatStatusMessage
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/LLM/RESEARCH.md:42-47
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Networking.ChatStatusMessage
related:
  - ./ChatMessage.md
tags: [chat, network]
---

# ChatStatusMessage

Vanilla game class at `Assets.Scripts.Networking.ChatStatusMessage`. Indexed at 81 in the `MessageFactory`. Carries the "is typing" indicator.

## Typing indicator
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0095a.

`ChatStatusMessage` class (`Assets.Scripts.Networking`, MessageFactory index 81). Sent when a player starts/stops typing. Fields: `long HumanId`, `bool IsTyping`. Triggers the "..." typing indicator bubble above a player's head. Not relevant for the bot (we never show typing status).

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0095a. No conflicts.

## Open questions

None at creation.
