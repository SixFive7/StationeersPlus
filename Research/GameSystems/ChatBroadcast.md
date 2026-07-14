---
title: ChatBroadcast
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-14
sources:
  - Plans/LLM/RESEARCH.md:9-14
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 278421-278461 (ChatMessage), 278462-278492 (AnnounceMessage), 96082-96110 (AnnounceCommand), 222963-223019 (ConsoleWindow.Print), 221505-221544 (ConsoleLine), 43997 / 44065-44066 (KeyMap console/info bindings)
related:
  - ../GameClasses/ChatMessage.md
  - ../GameClasses/ChatCanvas.md
  - ../GameClasses/SayCommand.md
  - ../GameClasses/NetworkChannel.md
tags: [chat, network]
---

# ChatBroadcast

Stationeers chat is not a separate protocol; player chat messages flow through the game's native network message system as `ChatMessage` instances. The four-step flow from client input to every client's console and chat bubble is documented below, plus the server-side broadcast recipe for mods, the console rendering constraints (the F3 console is ImGui, not TextMeshPro), and the louder `AnnounceMessage` channel.

## Flow
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

Stationeers chat uses the game's native network message system, not a separate chat protocol. The flow:

1. Player types in chat UI, client constructs a `ChatMessage` and sends it to the server via `NetworkClient.SendToServer(chatMessage)`
2. Server receives the message, calls `ChatMessage.Process(hostId)`
3. `Process()` prints to the server console, then broadcasts to all clients via `NetworkServer.SendToClients(this, NetworkChannel.GeneralTraffic, -1L)`
4. Each client receives the broadcast, prints to their console, and shows a chat bubble above the sender's character model (only when `HumanId` resolves to a `Human` that is not the local player; see the verbatim below)

`ChatMessage` (0.2.6403.27689 decompile lines 278421-278461), verbatim, including `Process` as it runs on every receiving side:

```csharp
public class ChatMessage : ProcessedMessage<ChatMessage>
{
    public long HumanId { get; set; }

    public string DisplayName { get; set; }

    public string ChatText { get; set; }

    public override void Process(long hostId)
    {
        PrintToConsole();
        if (NetworkManager.IsServer)
        {
            NetworkServer.SendToClients(this, NetworkChannel.GeneralTraffic, -1L);
        }
        Human human = Thing.Find<Human>(HumanId);
        if ((bool)human && (bool)InventoryManager.Parent && InventoryManager.Parent.ReferenceId != HumanId)
        {
            human.SetChatText(ChatText);
        }
    }

    public override void Deserialize(RocketBinaryReader reader)
    {
        HumanId = reader.ReadInt64();
        DisplayName = reader.ReadString();
        ChatText = reader.ReadString();
    }

    public override void Serialize(RocketBinaryWriter writer)
    {
        writer.WriteInt64(HumanId);
        writer.WriteString(DisplayName);
        writer.WriteString(ChatText);
    }

    public void PrintToConsole()
    {
        ConsoleWindow.Print(DisplayName + ": " + ChatText, ConsoleColor.Cyan, clearLine: false, aged: false, unformatted: true);
    }
}
```

## Server-side broadcast recipe for mods
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

Works on a dedicated server with no local player; this is exactly the [SayCommand](../GameClasses/SayCommand.md) batch branch:

```csharp
ChatMessage chatMessage = new ChatMessage
{
    ChatText = ...,
    DisplayName = "Server",   // or a mod tag
    HumanId = -1
};
chatMessage.PrintToConsole();
NetworkServer.SendToClients(chatMessage, NetworkChannel.GeneralTraffic, -1L);
```

- Each receiving client's `Process` prints the line into its own console. `SendToClients(msg, NetworkChannel.GeneralTraffic, -1L)` = reliable channel, `-1L` = all clients.
- `HumanId = -1` suppresses the chat bubble: `Process` only routes to `human.SetChatText(ChatText)` when `HumanId` resolves to a `Human`, which is what a system line wants.
- Single-player: with no network session both `IsServer` and `IsClient` are false; call `PrintToConsole()` alone (or just `ConsoleWindow.Print`). The `say` command itself is `CommandScope.MultiplayerOnly` but the underlying APIs are not.

## Rendering constraints: the console is ImGui, not TextMeshPro
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

`ConsoleWindow.Print` (222963-223019), verbatim head:

```csharp
public static void Print(string output, ConsoleColor color = ConsoleColor.White, bool clearLine = false, bool aged = true, bool unformatted = false)
{
    if (GameManager.IsBatchMode)
    {
        output = $"{DateTime.Now:HH:mm:ss}: {output}";
        if (CustomLogFile)
        {
            switch (color)
            {
            case ConsoleColor.DarkRed:
            case ConsoleColor.DarkMagenta:
            case ConsoleColor.Red:
            case ConsoleColor.Magenta:
                UnityEngine.Debug.LogError(output);
                break;
            case ConsoleColor.DarkYellow:
            case ConsoleColor.Yellow:
                UnityEngine.Debug.LogWarning(output);
                break;
            default:
                UnityEngine.Debug.Log(output);
                break;
            }
        }
        else if (!output.Contains("<color="))
        {
            _systemConsoleInput?.PrintToConsole(output, color);
        }
        return;
    }
    uint consoleColor = GetConsoleColor(color);
    ConsoleLine[] consoleBuffer = _consoleBuffer;
    if (consoleBuffer == null || consoleBuffer.Length <= 0)
    {
        _prematureLogQueue.Enqueue(new PrematureLog { output = output, color = color, clearLine = clearLine, aged = aged, unformatted = unformatted });
        return;
    }
    for (int num = _consoleBuffer.Length - 1 - 1; num >= 0; num--)
    {
        _consoleBuffer[num + 1].Apply(_consoleBuffer[num]);
    }
    if (aged)
    {
        _consoleBuffer[0].Set(output, consoleColor, 0f);
    }
    else
    {
        _consoleBuffer[0].Set(output, consoleColor);
    }
}
```

`ConsoleLine` (221505 onward) stores `uint Color` and a plain `string Text` and renders through ImGui (`ImGui.ColorConvertFloat4ToU32` at 221521). Constraints that follow:

- TextMeshPro rich-text tags (`<color=...>` etc.) do NOT render in the in-game console; the console colors a whole line via the `ConsoleColor` argument only. Chat lines are always Cyan (`PrintToConsole`, 278459), with the sender name baked into the text as `DisplayName + ": " + ChatText`. (Contrast: the build-cursor tooltip IS a TextMeshPro surface, which is why the placement loop's `<color=red>` markup renders there; see [StructurePlacementValidation](./StructurePlacementValidation.md).)
- On a dedicated server (`IsBatchMode`) WITHOUT `CustomLogFile`, any line CONTAINING `<color=` is silently dropped from the system console (the `!output.Contains("<color=")` guard at 222987). With `CustomLogFile`, output is routed to `UnityEngine.Debug.Log/LogWarning/LogError` by color band instead. Batch-mode lines are timestamped `HH:mm:ss`. So embedding rich-text tags in `ChatText` would render as literal tags on clients and can vanish entirely from the server console log. Use plain text.
- Multi-line: `ConsoleLine.Set` splits on `\n` into continuations (221532-221544), so newlines are legal in a printed line.
- No length cap was found in `ChatMessage.Serialize` (plain `WriteString`) or `ConsoleWindow.Print` at this layer; the chat INPUT field may cap what a player can type, but a mod-constructed message is not length-gated by the send path. Keep messages one line and short anyway (console lines wrap poorly and the overlay ages out).
- The console is the surface players toggle with F3: `KeyMap.ToggleConsole = KeyCode.F3` (43997, default assignment repeated at 44065). New lines also appear transiently on the HUD overlay (the `aged` fade path; chat uses `aged: false` so chat lines persist until scrolled).

## AnnounceMessage: the louder channel
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

Alternative channel: `AnnounceMessage` (278462-278492) plus the `announce` command (`AnnounceCommand`, 96082-96110). The server sends `NetworkServer.SendToClients(new AnnounceMessage { AnnounceText = ... }, NetworkChannel.GeneralTraffic, -1L)` and calls `AnnounceMessage.Display(text)` locally. `Display` prints "Announcement: " + text in Yellow AND pops a modal `ConfirmationPanel` on non-batch clients. Louder than chat; suitable for rare, important notices, not per-placement spam.

```csharp
public static void Display(string text)
{
    ConsoleWindow.Print("Announcement: " + text, ConsoleColor.Yellow, clearLine: false, aged: false, unformatted: true);
    if (!GameManager.IsBatchMode && (bool)Singleton<ConfirmationPanel>.Instance)
    {
        Singleton<ConfirmationPanel>.Instance.ShowWithRawMessage("ServerAnnouncement", text, "ButtonOk");
    }
}
```

## Verification history
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0070 (Plans/LLM/RESEARCH.md:9-14).
- 2026-07-14: re-verified and extended against the 0.2.6403.27689 decompile during the mixed-tier cable network guard research pass. The four-step flow stands, with step 4 refined (the bubble requires `HumanId` to resolve to a `Human` other than the local player). Added: the full `ChatMessage` class verbatim including `Process` (278421-278461), the server-side broadcast recipe (the `SayCommand` batch branch: `DisplayName = "Server"`, `HumanId = -1` to suppress the bubble, `PrintToConsole()` + `SendToClients(..., NetworkChannel.GeneralTraffic, -1L)`), the "Rendering constraints" section (`ConsoleWindow.Print` verbatim head 222963-223019; `ConsoleLine` renders via ImGui `ColorConvertFloat4ToU32` at 221521 so TextMeshPro tags do not render; one `ConsoleColor` per line, chat = Cyan at 278459; dedicated-server `CustomLogFile` routing with `HH:mm:ss` timestamps vs the silent `<color=` drop at 222987; `\n` continuations 221532-221544; no length cap at this layer; F3 = `KeyMap.ToggleConsole` 43997/44065), and the `AnnounceMessage` / `announce` command channel (278462-278492 / 96082-96110; yellow console line plus a modal `ConfirmationPanel` on every non-batch client).

## Open questions

None at creation.
