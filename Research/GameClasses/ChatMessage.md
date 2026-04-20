---
title: ChatMessage
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/LLM/RESEARCH.md:17-39
  - Plans/LLM/RESEARCH.md:68-74
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Networking.ChatMessage
related:
  - ./ChatCanvas.md
  - ./SayCommand.md
  - ./ChatStatusMessage.md
  - ./Human.md
  - ../GameSystems/ChatBroadcast.md
  - ../Protocols/GameMessageFactory.md
tags: [chat, network]
---

# ChatMessage

Vanilla game class at `Assets.Scripts.Networking.ChatMessage`. The wire-format message carrying in-world chat. Indexed at 80 in the `MessageFactory`.

## Fields and Process logic
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0071.

**Namespace:** `Assets.Scripts.Networking`
**Base class:** `ProcessedMessage<ChatMessage>` (which extends `MessageBase<T>`)
**MessageFactory index:** 80

Fields:
- `long HumanId` - the sender's `Thing.ReferenceId`. Set to `-1` for server/batch messages (no associated human entity)
- `string DisplayName` - the sender's display name. "Server" for batch mode, player name otherwise
- `string ChatText` - the message body

Serialization order (must match exactly): `HumanId`, `DisplayName`, `ChatText`.

`Process()` logic:
```
PrintToConsole()                    // always: writes "DisplayName: ChatText" to console in cyan
if (NetworkManager.IsServer)
    NetworkServer.SendToClients()   // server broadcasts to all connected clients
Thing.Find<Human>(HumanId)          // look up sender entity
if (human exists && not local player)
    human.SetChatText(ChatText)     // show chat bubble above their head
```

When `HumanId` is `-1`, `Thing.Find<Human>(-1)` returns null, so no chat bubble appears. The message still shows in every player's console/chat log. This is the correct behavior for a bot: text in chat, no floating bubble.

## Client-side Process exemption
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0095b.

`ChatCanvas` is attached to each `Human` entity and manages the floating chat bubble UI. `ChatWindow` handles the typewriter-style text animation. In `NetworkBase.DeserializeReceivedData()`, most message types log an error if processed on a non-server peer. `ChatMessage` is explicitly exempted from this check, meaning clients process it normally (print to console + show bubble). This is standard for broadcast messages.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0071, F0095b. No conflicts.

## Open questions

None at creation.
