---
title: SayCommand
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/LLM/RESEARCH.md:49-66
  - Plans/LLM/LLM/ChatPatch.cs:78-82
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Util.Commands.SayCommand
related:
  - ./ChatMessage.md
  - ./Human.md
tags: [chat, network]
---

# SayCommand

Vanilla game class at `Util.Commands.SayCommand`. Implements the `say` console command and is the canonical demonstration of the server-side chat-send pattern.

## Server-side send pattern
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0072.

**Namespace:** `Util.Commands`

The `say` console command demonstrates the server-side send pattern:

```csharp
ChatMessage chatMessage = new ChatMessage
{
    ChatText = input,
    DisplayName = GameManager.IsBatchMode ? "Server" : Human.LocalHuman.DisplayName,
    HumanId = GameManager.IsBatchMode ? -1 : Human.LocalHuman.ReferenceId
};
// On dedicated server (IsServer && IsBatchMode):
chatMessage.PrintToConsole();
NetworkServer.SendToClients(chatMessage, NetworkChannel.GeneralTraffic, -1L);
```

Key detail: on a dedicated server (`GameManager.IsBatchMode`), there is no `Human.LocalHuman`. The code uses `HumanId = -1` and `DisplayName = "Server"`. The bot follows this same pattern.

### SendBotMessage class comment (F0379)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source comment from `ChatPatch.cs:78-82`:

```
Sends a chat message as the bot. Must run on the main thread. Follows the same pattern as SayCommand.Say() for server/batch mode: construct a ChatMessage, print to server console, broadcast to clients.
```

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0072, F0379. No conflicts.

## Open questions

None at creation.
