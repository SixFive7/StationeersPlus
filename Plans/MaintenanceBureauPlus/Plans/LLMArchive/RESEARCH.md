# LLM (Plans)

LLM is a work-in-progress mod under `Plans/` that adds a server-side chat companion powered by a local language model. A Harmony postfix on `ChatMessage.Process` forwards player messages that match a trigger prefix to a background inference thread; responses drain back onto the Unity main thread and are broadcast through the vanilla chat system under a configurable bot name. First-time readers: architecture and threading sit in Section 1; the single Harmony patch is Section 3; the game internals the mod leans on (chat broadcast, ChatMessage fields, SayCommand pattern, NetworkChannel enum) live on the central pages listed in Section 5.

## Status

In-progress under `Plans/LLM/`. Not tagged or published. No Workshop handle yet. The plugin loads a GGUF model with LLamaSharp (CPU backend) and replies to chat messages prefixed with `@sat` (default). Text-only; client-side TTS was investigated and rejected (see Section 2.2 and the `TTSDeadEnd.md` central workflow).

## Architecture

Mod identity:

| Field | Value |
|---|---|
| Display Name | LLM |
| Code Name | LLM |
| Plugin GUID | net.llm |
| Workshop ID | (none; not yet published) |
| Dependencies | StationeersLaunchPad, LaunchPadBooster, LLamaSharp + LLamaSharp.Backend.Cpu |

Source files under `Plans/LLM/LLM/`: `Plugin.cs` (BepInEx plugin entry, config binding, background model loader, `Update()` pump), `LlmEngine.cs` (LLamaSharp wrapper, StatelessExecutor, prompt formatting), `ChatPatch.cs` (the Harmony postfix + main-thread drain + bot-message sender).

### Plugin wiring

`LlmPlugin.Awake()` binds config and subscribes to `Prefab.OnPrefabsLoaded`. In `OnAllModsLoaded()` the plugin resolves `BepInEx/plugins/LLM/Models/<ModelFileName>`, logs an error and returns cleanly if the file is missing, then spawns a dedicated background `Thread` (not `Task`) to load the GGUF model. Config values that the loader needs (`ContextSize`, `InferenceThreads`) are read on the main thread and captured into local variables before the thread starts, so the background thread never touches `ConfigEntry` from off-main. Once load completes, `_modelReady` flips and `Update()` applies the Harmony patches on the next frame.

The loader thread runs at `ThreadPriority.BelowNormal` so the game's physics, atmospherics, and networking keep priority even when inference is active.

### Threading model

Three threads matter:

- **Main (Unity)**: plugin startup, config read, Harmony patching, chat-message construction, `NetworkServer.SendToClients` calls, `Update()` pumping the response queue.
- **Model loader**: a one-shot `Thread` started in `OnAllModsLoaded` that performs the LLamaSharp model load, then exits.
- **Inference**: a long-lived `Thread` inside `LlmEngine` that pulls requests off a `ConcurrentQueue`, runs the StatelessExecutor, and pushes response strings back onto `ChatPatch.PendingResponses` (a `ConcurrentQueue<string>`).

Anything that touches Stationeers game API (`ChatMessage`, `NetworkServer`, `NetworkManager.IsServer`) runs on the main thread. The inference thread only ever touches LLamaSharp and the two queues. `ChatPatch.DrainResponses()` is the bridge: called from `Plugin.Update()` each frame, it dequeues ready responses and sends them through the game's chat system.

`LlmEngine.InferAsync` drains an `IAsyncEnumerator<string>` manually (no `await foreach`) because the mod targets .NET Framework 4.7.2, which has no built-in async-enumerator language support. See `../../Research/Patterns/AsyncEnumerator472.md`.

### Server / client roles

The mod is server-authoritative in a narrow sense: the `ChatPatch` postfix guards on `NetworkManager.IsServer` and only the server runs inference. Clients do not load the model, do not run the patch body, and never enqueue anything. The response comes back through the same chat-broadcast path the vanilla `SayCommand` uses when the server types a message, so clients render it identically to any other chat message.

Bot messages set `HumanId = -1` so no chat bubble appears above any character; the response shows in the chat console only, matching how "Server" messages work on dedicated servers.

### LLamaSharp integration

LLamaSharp provides C# bindings for llama.cpp, targets .NET Standard 2.0, and runs on the game's .NET Framework 4.7.2 Mono runtime. The `LLamaSharp.Backend.Cpu` NuGet package bundles the native `llama.dll` for CPU-only inference.

Inference uses `StatelessExecutor` (not `InteractiveExecutor`). Each request builds a fresh prompt from the system prompt + player message. No shared mutable state in the executor, no cross-request context-window arithmetic, no conversation memory.

Qwen2.5-Instruct prompt format (ChatML):

```
<|im_start|>system
{system prompt}<|im_end|>
<|im_start|>user
[{playerName}]: {message}<|im_end|>
<|im_start|>assistant
```

Anti-prompts: `<|im_end|>` and `\n\n` stop generation cleanly.

Resource budget (Qwen2.5-1.5B-Instruct Q4_K_M):

- Model file: ~1.1 GB on disk.
- RAM at runtime: ~2-3 GB resident.
- CPU: configurable thread count, `BelowNormal` priority.
- Inference speed on a modern server CPU: ~5-15 tokens/sec, 50-token response in 3-10 seconds.

### Deployment layout

```
BepInEx/plugins/LLM/
  LLM.dll
  LLamaSharp.dll
  llama.dll              (native, from LLamaSharp.Backend.Cpu runtimes/)
  Models/
    qwen2.5-1.5b-instruct-q4_k_m.gguf
  About/
    About.xml
```

The native `llama.dll` must sit next to `LLM.dll` or under a `runtimes/<rid>/` subfolder. LLamaSharp probes both; flattening it next to the managed DLL is simplest. Model files never ship with the mod (see `Plans/LLM/CLAUDE.md`); users download a GGUF themselves.

## Design decisions

### Applied

- **Trigger prefix, not respond-to-all**: the `@sat` default gives players explicit control. Responding to every line would flood the chat channel and burn CPU on irrelevant chatter. An empty prefix config falls back to respond-to-all for servers that want it.
- **No conversation memory (stateless)**: each request is independent. Rationale: a sliding context window adds state management, context-size arithmetic, and the 2048-token model fills fast under multi-turn chat. Per-player sessions with timeout compounds the state problem. The "unreliable satellite relay" lore reframes the limitation as a feature ("signal quality too poor for session persistence").
- **Bot messages use `HumanId = -1`**: no chat bubble appears above any entity. The bot is a satellite, not a character in the world. Matches how the vanilla "Server" label works on dedicated servers.
- **Inference thread at `ThreadPriority.BelowNormal`**: simulation tick keeps priority over inference. The user-visible cost is a few extra seconds of response latency, which fits the lore.
- **Single FIFO queue, one request at a time**: if three players message the bot simultaneously, requests queue and process sequentially. Concurrent inference would need multiple model contexts (multiplied RAM) for marginal gain. Sequential matches "shared satellite bandwidth."
- **Background `Thread`, not `Task`**: `Task` rides the ThreadPool, which Unity's `SynchronizationContext` can interact with in surprising ways. A dedicated `Thread` is simpler and gives us direct control over priority and naming.
- **Capture config values on the main thread before dispatching to the loader thread**: BepInEx `ConfigEntry<T>` is fine to read from any thread in practice, but reading it from Unity's main thread keeps the guarantee that any incidental Unity-side dependency stays on the correct thread. See `../../Research/Patterns/MainThreadDispatcher.md`.
- **Ship text-only**: the bot's responses appear as chat messages, identical to player chat. The SATCOM framing already fits a text-only medium; a radio-filter TTS add-on is a separate mod's problem if anyone ever wants one.

### Rejected or deferred

- **Client-side TTS**: every viable path (SAPI via System.Speech, C++ DLL bridge, Piper, Kokoro, Sherpa-ONNX, cloud TTS) has a disqualifying blocker (Mono COM interop failure, GPL licensing, ONNX/Mono crashes, runtime downloads, API key management). Full argument on `../../Research/Workflows/TTSDeadEnd.md`.
- **Per-player context windows**: deferred. Not worth the state-management complexity for a 1.5B model that barely handles single-turn.
- **Streaming token-by-token chat output**: deferred. Stationeers `ChatMessage` is one-shot; streaming would need the client to render partial messages, which is not how the vanilla chat UI works.

## Harmony patches catalog

### ChatPatch

| Patch class | Target method | Type | Effect |
|---|---|---|---|
| `ChatPatch` | `ChatMessage.Process(long hostId)` | Postfix | Server-only. If the incoming message matches the configured trigger prefix and is not from the bot itself, strip the prefix and enqueue onto the inference thread. When the response returns, it is pushed onto `PendingResponses`; `Plugin.Update()` drains that queue on the main thread and broadcasts each response via a new `ChatMessage` with `HumanId = -1` + `NetworkServer.SendToClients(..., NetworkChannel.GeneralTraffic, -1L)`. |

Why Postfix, not Prefix: let the game finish its normal broadcast first. The bot response arrives asynchronously, so there is no need to block or modify the original message flow.

Loop guard: the postfix skips messages whose `DisplayName` equals the configured `BotName`, which prevents an `@sat`-prefixed bot response from re-triggering the patch when it echoes through the same broadcast path.

**Depends on:** [../../Research/GameClasses/ChatMessage.md](../../Research/GameClasses/ChatMessage.md), [../../Research/GameSystems/ChatBroadcast.md](../../Research/GameSystems/ChatBroadcast.md), [../../Research/GameClasses/SayCommand.md](../../Research/GameClasses/SayCommand.md), [../../Research/GameClasses/NetworkChannel.md](../../Research/GameClasses/NetworkChannel.md), [../../Research/Patterns/MainThreadDispatcher.md](../../Research/Patterns/MainThreadDispatcher.md), [../../Research/Patterns/AsyncEnumerator472.md](../../Research/Patterns/AsyncEnumerator472.md).

## Relevant central pages

### GameClasses

- [../../Research/GameClasses/CameraController.md](../../Research/GameClasses/CameraController.md) - Existing CameraFilterPack components on the main camera; background for any future "radio static on response" visual polish.
- [../../Research/GameClasses/ChatCanvas.md](../../Research/GameClasses/ChatCanvas.md) - The per-`Human` chat bubble UI; explains why `HumanId = -1` produces no bubble.
- [../../Research/GameClasses/ChatMessage.md](../../Research/GameClasses/ChatMessage.md) - Fields, serialization order, `Process()` logic, `HumanId = -1` semantics, and the client-side-Process exemption that lets clients render server broadcasts.
- [../../Research/GameClasses/ChatStatusMessage.md](../../Research/GameClasses/ChatStatusMessage.md) - Typing-indicator message; not used by the bot but adjacent to the chat protocol.
- [../../Research/GameClasses/Entity.md](../../Research/GameClasses/Entity.md) - `OnCameraUpdate` post-processing hooks; background for any "groggy transmission" client effects.
- [../../Research/GameClasses/ILifeSuspender.md](../../Research/GameClasses/ILifeSuspender.md) - Reference for sleeper/cryo/bed suspension semantics cited in earlier time-skip exploration.
- [../../Research/GameClasses/LanderCapsule.md](../../Research/GameClasses/LanderCapsule.md) - Lander drop constants, descent sequence, LanderMode enum; background for the "time-skip cover" exploration that was investigated and dropped from the MVP.
- [../../Research/GameClasses/NetworkChannel.md](../../Research/GameClasses/NetworkChannel.md) - `GeneralTraffic` is the channel the bot's broadcast uses for reliable chat delivery.
- [../../Research/GameClasses/SayCommand.md](../../Research/GameClasses/SayCommand.md) - Server-side send pattern the bot mirrors for batch/server mode (`HumanId = -1`, `DisplayName = "Server"`).

### GameSystems

- [../../Research/GameSystems/CameraFilterPack.md](../../Research/GameSystems/CameraFilterPack.md) - Catalogue of distortion effects available for disorientation overlays.
- [../../Research/GameSystems/ChatBroadcast.md](../../Research/GameSystems/ChatBroadcast.md) - The four-step client-server chat flow the postfix rides on.
- [../../Research/GameSystems/DamageState.md](../../Research/GameSystems/DamageState.md) - Damage-channel hierarchy and `HealAll` semantics; background for the earlier repair / time-skip explorations.
- [../../Research/GameSystems/RespawnFlow.md](../../Research/GameSystems/RespawnFlow.md) - Full respawn rebuilds a new `Human`; relevant when separating "lander drop effect" from "respawn" in any future companion feature.
- [../../Research/GameSystems/StunStateMachine.md](../../Research/GameSystems/StunStateMachine.md) - Stun-channel-on-brain model, thresholds, sleeper mechanics, and the sources-of-stun table; background for time-skip cover scenarios that were investigated and dropped.

### Patterns

- [../../Research/Patterns/AsyncEnumerator472.md](../../Research/Patterns/AsyncEnumerator472.md) - `InferAsync` drains `IAsyncEnumerator<string>` by hand because .NET Framework 4.7.2 has no `await foreach`.
- [../../Research/Patterns/MainThreadDispatcher.md](../../Research/Patterns/MainThreadDispatcher.md) - The `ConcurrentQueue` + `Update()` drain pattern the mod uses to bridge inference results onto the main thread; also covers "capture config on main before dispatch."
- [../../Research/Patterns/PooledSpanEnumeration.md](../../Research/Patterns/PooledSpanEnumeration.md) - `OcclusionManager.AllThings` enumeration pattern; background for any future "mod-scoped bulk query" feature.

### Workflows

- [../../Research/Workflows/CameraEffectsRuntime.md](../../Research/Workflows/CameraEffectsRuntime.md) - Adding / removing camera filter components at runtime; background for future "signal interference" polish.
- [../../Research/Workflows/HealAllDamagedThings.md](../../Research/Workflows/HealAllDamagedThings.md) - Server-only bulk repair walkthrough; background for time-skip "everything got fixed while you were out" cover scenarios.
- [../../Research/Workflows/KnockPlayerUnconscious.md](../../Research/Workflows/KnockPlayerUnconscious.md) - How to drive the stun state machine from a mod; background for disorientation / groggy-response gimmicks.
- [../../Research/Workflows/TTSDeadEnd.md](../../Research/Workflows/TTSDeadEnd.md) - The full argument for why this mod ships text-only. Every TTS path had a disqualifying blocker.
- [../../Research/Workflows/TimeSkipWorldManipulation.md](../../Research/Workflows/TimeSkipWorldManipulation.md) - The table of world-state APIs (sun, weather, hunger, pipes, etc.) explored for a "satellite-relay cover" time-skip feature that is not in the current MVP.
- [../../Research/Workflows/TriggerLanderCapsule.md](../../Research/Workflows/TriggerLanderCapsule.md) - Three-call recipe for dropping a living player into a lander without the death/respawn pipeline; background for the same time-skip cover investigation.

## Pitfalls / dead ends

- **`System.Speech.Synthesis` does not load under Unity Mono.** Even referencing the type from a never-executed code path produces a `TypeLoadException` at assembly load. Any client-side TTS experiment must avoid touching `System.Speech` at all. Full analysis on `../../Research/Workflows/TTSDeadEnd.md`.
- **Task vs Thread for inference**: using `Task` pulls in Unity's `SynchronizationContext` behaviour. The mod deliberately uses a dedicated `Thread` to keep the scheduling model simple and to let `ThreadPriority.BelowNormal` bite.
- **`ConfigEntry<T>` access from non-Unity threads**: reading config values on the background loader thread works today but is not a documented guarantee. Capture them on the main thread into locals first. See the "capture on main" subsection of `../../Research/Patterns/MainThreadDispatcher.md`.
- **`StartsWith` case sensitivity for the trigger prefix**: the postfix uses `StringComparison.OrdinalIgnoreCase`. Changing this to a culture-sensitive comparison would tie trigger matching to the server's locale, which is never what a mod author wants.
- **Bot loop protection**: the postfix filters out messages whose `DisplayName` matches the configured bot name. Any future change that lets the bot post under a different name must re-apply this guard, or responses will re-enter the patch and loop.
- **Bot messages with `HumanId = -1`**: the server always sees the broadcast (no filter) and rebroadcasts it. Clients see a normal chat message with no bubble. Any future change that tries to attach the bot to a specific `Human` (e.g. a physical SATCOM console entity) needs to reconsider bubble visibility and the loop-guard identity.
