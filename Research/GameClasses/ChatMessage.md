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

## Chat channel: there is none
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Source: `$(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Networking.ChatMessage`, `Util.Commands.SayCommand`.

`ChatMessage` has no channel / scope field. Its only serialized fields are `HumanId`, `DisplayName`, `ChatText`. Vanilla has no notion of a private chat, squad chat, or team channel in this message type.

The string `NetworkChannel` elsewhere in the codebase (enum `Assets.Scripts.Networking.NetworkChannel` with values `GeneralTraffic = 134, PlayerJoin, StateTick, Unreliable, SteamP2PConnectionRequest, SteamP2PConnectionAccepted, SteamP2PHeartbeat, NumChannels`) is the transport-layer channel, not a chat scope. `ChatMessage.Process` sends over `NetworkChannel.GeneralTraffic`, the same channel every other gameplay message uses.

There is no `ChatChannel` enum. The only other chat-related message type is `ChatStatusMessage` (HumanId + bool IsTyping), which drives the chat-bubble typing indicator.

Consequence for patch code: a `Harmony` postfix on `ChatMessage.Process` cannot filter "public chat only"; every chat line that goes through `ChatMessage.Process` IS the single chat stream. The only filters available are:

- `HumanId == -1` (server batch message, no associated human, no bubble).
- `HumanId == InventoryManager.Parent?.ReferenceId` (message authored by the local player) - use this to avoid self-responses.
- `ChatText` prefix / content matching for command-style filtering.

## Verification history

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0071, F0095b. No conflicts.
- 2026-04-21: added "Chat channel: there is none" section documenting the absence of a chat-channel field on `ChatMessage` and clarifying that `NetworkChannel` is a transport-layer enum, not a chat scope. Additive only; no existing content changed. Game version 0.2.6228.27061.

## Open questions

None.
