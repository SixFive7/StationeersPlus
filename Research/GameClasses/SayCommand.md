---
title: SayCommand
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-14
sources:
  - Plans/LLM/RESEARCH.md:49-66
  - Plans/LLM/LLM/ChatPatch.cs:78-82
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Util.Commands.SayCommand
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 101709-101766 (SayCommand)
related:
  - ./ChatMessage.md
  - ./Human.md
  - ../GameSystems/ChatBroadcast.md
tags: [chat, network]
---

# SayCommand

Vanilla game class at `Util.Commands.SayCommand`. Implements the `say` console command and is the canonical demonstration of the server-side chat-send pattern.

## Server-side send pattern
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

Source: F0072; re-verified against the 0.2.6403.27689 decompile (`SayCommand` at lines 101709-101766).

**Namespace:** `Util.Commands`

The `say` console command demonstrates the server-side send pattern. Verbatim core (`Say`, 0.2.6403.27689); a listen-server host now gets a `" (Host)"` suffix appended to its display name:

```csharp
private static void Say(string input)
{
    if (!string.IsNullOrEmpty(input))
    {
        ChatMessage chatMessage = new ChatMessage
        {
            ChatText = input,
            DisplayName = (GameManager.IsBatchMode ? "Server" : Human.LocalHuman.DisplayName),
            HumanId = (GameManager.IsBatchMode ? (-1) : Human.LocalHuman.ReferenceId)
        };
        if (Assets.Scripts.Networking.NetworkManager.IsServer && !GameManager.IsBatchMode)
        {
            chatMessage.DisplayName += " (Host)";
        }
        if ((bool)Human.LocalHuman && !input.Contains("hi") && !input.Contains("hello"))
        {
            input.Contains("wave");
        }
        if (Assets.Scripts.Networking.NetworkManager.IsClient)
        {
            NetworkClient.SendToServer(chatMessage);
        }
        else if (Assets.Scripts.Networking.NetworkManager.IsServer)
        {
            chatMessage.PrintToConsole();
            NetworkServer.SendToClients(chatMessage, NetworkChannel.GeneralTraffic, -1L);
        }
        else
        {
            ConsoleWindow.PrintError("Unable to send message.", suppressStacktrace: true);
        }
    }
}
```

Key details:

- On a dedicated server (`GameManager.IsBatchMode`), there is no `Human.LocalHuman`. The code uses `HumanId = -1` and `DisplayName = "Server"`. The bot follows this same pattern.
- The command itself is registered `CommandScope.MultiplayerOnly` (101717); the underlying APIs (`ChatMessage`, `PrintToConsole`, `SendToClients`) are NOT scope-gated and work from a mod in any mode. In single-player (no network session) both `IsServer` and `IsClient` are false, so vanilla `Say` hits the error branch; a mod should call `PrintToConsole()` alone (or just `ConsoleWindow.Print`) in that case.
- `SayCommand.HelpText` (101711): "Sends a chat message to all connected players. Multiplayer only; the host appears tagged as '(Host)'."
- The full broadcast recipe, `ChatMessage.Process` on the receiving client, and the console rendering constraints (plain text only) are on [ChatBroadcast](../GameSystems/ChatBroadcast.md).

### SendBotMessage class comment (F0379)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source comment from `ChatPatch.cs:78-82`:

```
Sends a chat message as the bot. Must run on the main thread. Follows the same pattern as SayCommand.Say() for server/batch mode: construct a ChatMessage, print to server console, broadcast to clients.
```

## Verification history
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0072, F0379. No conflicts.
- 2026-07-14: re-verified against the 0.2.6403.27689 decompile (`SayCommand` 101709-101766) during the mixed-tier cable network guard research pass. The F0072 pattern stands; replaced the paraphrased snippet with the full verbatim `Say` body. New at this version relative to the old excerpt: the `" (Host)"` display-name suffix for a non-batch listen-server host, the explicit `CommandScope.MultiplayerOnly` registration (101717), the `HelpText` string (101711), and the single-player note (both `IsServer` and `IsClient` false with no network session, so a mod calls `PrintToConsole()` alone). Cross-linked [ChatBroadcast](../GameSystems/ChatBroadcast.md) for the receiving-side `Process` and the console rendering constraints.

## Open questions

None at creation.
