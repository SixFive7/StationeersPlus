---
title: ChatBroadcast
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/LLM/RESEARCH.md:9-14
related:
  - ../GameClasses/ChatMessage.md
  - ../GameClasses/ChatCanvas.md
  - ../GameClasses/SayCommand.md
  - ../GameClasses/NetworkChannel.md
tags: [chat, network]
---

# ChatBroadcast

Stationeers chat is not a separate protocol; player chat messages flow through the game's native network message system as `ChatMessage` instances. The four-step flow from client input to every client's console and chat bubble is documented below.

## Flow
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Stationeers chat uses the game's native network message system, not a separate chat protocol. The flow:

1. Player types in chat UI, client constructs a `ChatMessage` and sends it to the server via `NetworkClient.SendToServer(chatMessage)`
2. Server receives the message, calls `ChatMessage.Process(hostId)`
3. `Process()` prints to the server console, then broadcasts to all clients via `NetworkServer.SendToClients(this, NetworkChannel.GeneralTraffic, -1L)`
4. Each client receives the broadcast, prints to their console, and shows a chat bubble above the sender's character model

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0070 (Plans/LLM/RESEARCH.md:9-14).

## Open questions

None at creation.
