---
title: NetworkChannel
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/LLM/RESEARCH.md:77-86
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: NetworkChannel
related:
  - ./ChatMessage.md
tags: [network]
---

# NetworkChannel

Vanilla game enum used by the networking layer to tag outgoing messages with delivery semantics. Chat messages ride `GeneralTraffic` (reliable).

## Enum values
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0073.

```
GeneralTraffic = 134    (reliable, used for chat)
PlayerJoin = 135
StateTick = 136
Unreliable = 137
SteamP2P* = 138-140
```

Chat uses `GeneralTraffic` (reliable delivery).

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0073. No conflicts.

## Open questions

None at creation.
