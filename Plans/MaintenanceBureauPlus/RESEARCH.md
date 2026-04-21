# Maintenance Bureau Plus: Research Reference

Work-in-progress mod under `Plans/`. Collapses three earlier prototypes (LLM, RepairPrototype, SaveFixPrototype) into a single design that ships an LLM-gated global repair event for v1 and terrain reclamation for v2. The durable game-internals facts this mod leans on all live on central `Research/` pages; this file is the pointer index plus mod-local observations. First-time readers: read `plan.md` first, then walk the pointers in Section 3 by task (chat listening, knockout, capsule, repair sweep, officer persona).

## 1. Status

In-progress under `Plans/MaintenanceBureauPlus/`. Not tagged or published. No source project exists yet; v1 implementation begins by copying the LLM prototype's source out of `Plans/MaintenanceBureauPlus/Plans/LLMArchive/LLM/` and renaming identifiers per `TODO.md` step 1.

## 2. Architecture summary

Four subsystems:

- **Ambient chat listener.** Harmony postfix on `ChatMessage.Process`, server-only, global-channel-only. Every qualifying public chat line is forwarded to the inference thread with the current `ConversationState` bundle.
- **LLM engine.** LLamaSharp CPU inference on a dedicated background thread, single FIFO queue, no conversation memory at the executor level (memory is reconstructed from server-side state each turn). Reply drains back to the main thread via a `ConcurrentQueue<string>` and `Plugin.Update()`.
- **Approval orchestrator.** On `[APPROVED]` reply: broadcast the approval text, full-blackout stun all players to 100, run the repair sweep, collect corpse / wreckage telemetry, generate the closing message (with agent-retirement signal), spawn a `LanderCapsule` per player at their current position, teleport each into their capsule, write stun once to 80, then hand off to the game's natural stun decay and the capsule's own descent sequence.
- **Repair sweep.** Single-frame iteration of `Thing.AllThings` with the exclusion filter and the incident-channel-only damage write from `plan.md` Section 5.

All four subsystems are server-authoritative. No client install is required unless a client-visible effect (camera overlay, audio cue) proves necessary during implementation.

## 3. Central Research pages this mod depends on

Grouped by subsystem. Walk the group that matches the work you are about to do.

### 3.1. Chat listening and broadcast (ambient listener + closing message)

- [../../Research/GameClasses/ChatMessage.md](../../Research/GameClasses/ChatMessage.md) ChatMessage fields, serialization order, `Process()` logic, `HumanId = -1` semantics, client-side-Process exemption.
- [../../Research/GameSystems/ChatBroadcast.md](../../Research/GameSystems/ChatBroadcast.md) the four-step client-server chat flow the postfix rides on.
- [../../Research/GameClasses/SayCommand.md](../../Research/GameClasses/SayCommand.md) server-side send pattern (`HumanId = -1`, `DisplayName = "Server"`) mirrored by the bureau.
- [../../Research/GameClasses/NetworkChannel.md](../../Research/GameClasses/NetworkChannel.md) `GeneralTraffic` is the channel the bureau broadcasts on.
- [../../Research/GameClasses/ChatCanvas.md](../../Research/GameClasses/ChatCanvas.md) per-`Human` chat bubble UI; explains why `HumanId = -1` produces no bubble.

### 3.2. Knockout and capsule event

- [../../Research/Workflows/KnockPlayerUnconscious.md](../../Research/Workflows/KnockPlayerUnconscious.md) stun-on-brain mechanism, thresholds, natural decay rate, exact API calls.
- [../../Research/Workflows/TriggerLanderCapsule.md](../../Research/Workflows/TriggerLanderCapsule.md) three-call capsule spawn recipe, descent timings, time-skip window size.
- [../../Research/GameClasses/LanderCapsule.md](../../Research/GameClasses/LanderCapsule.md) class fields, descent constants (`KinematicStartHeight`, `DURATION`, `ENGINE_START_TIME`), lifecycle.
- [../../Research/GameSystems/RespawnFlow.md](../../Research/GameSystems/RespawnFlow.md) full respawn rebuilds a new `Human`; relevant background for separating "lander drop" from "respawn."
- [../../Research/GameSystems/StunStateMachine.md](../../Research/GameSystems/StunStateMachine.md) stun-channel-on-brain model, thresholds, sleeper mechanics, sources-of-stun table.
- [../../Research/GameClasses/ILifeSuspender.md](../../Research/GameClasses/ILifeSuspender.md) sleeper / cryo / bed suspension semantics; adjacent reference for any future "extended blackout" variant.

### 3.3. Repair sweep

- [../../Research/GameSystems/DamageState.md](../../Research/GameSystems/DamageState.md) damage-channel inventory per type (`ThingDamageState` vs `OrganicDamageState` vs `EntityDamageState`), field semantics, save-edit safety rules, the per-thing-type damage distribution from the RepairPrototype reference save.
- [../../Research/GameSystems/RepairMechanics.md](../../Research/GameSystems/RepairMechanics.md) vanilla repair paths, unrepairable-item list (Rover Mk I, rockets; bureau overrides), the "cost is player time, not resources" framing.
- [../../Research/GameClasses/Thing.md](../../Research/GameClasses/Thing.md) class-hierarchy diagram (Thing / DynamicThing / Item / Entity / Structure / LargeStructure / SmallGrid / Device) used by the exclusion filter.
- [../../Research/Patterns/PooledSpanEnumeration.md](../../Research/Patterns/PooledSpanEnumeration.md) safe iteration patterns for `AllThings` / `AllStructures` / `AllDevices`, including the `Device.AllDevices` duplicates trap.
- [../../Research/Workflows/HealAllDamagedThings.md](../../Research/Workflows/HealAllDamagedThings.md) server-only bulk repair walkthrough; closest existing recipe to the bureau's sweep.

### 3.4. Threading and async (LLamaSharp integration)

- [../../Research/Patterns/MainThreadDispatcher.md](../../Research/Patterns/MainThreadDispatcher.md) the `ConcurrentQueue` + `Update()` drain pattern, plus the "capture config on main before dispatch" rule.
- [../../Research/Patterns/AsyncEnumerator472.md](../../Research/Patterns/AsyncEnumerator472.md) `InferAsync` drains `IAsyncEnumerator<string>` by hand because .NET Framework 4.7.2 has no `await foreach`.
- [../../Research/Patterns/ServerAuthoritativeSimulation.md](../../Research/Patterns/ServerAuthoritativeSimulation.md) server / client split, `!GameManager.IsServer` guard pattern, ship-to-both-sides rule.

### 3.5. Mod project + plugin shape

- [../../Research/Workflows/ModProjectSetup.md](../../Research/Workflows/ModProjectSetup.md) framework stack, `Directory.Build.props` inheritance, `$(StationeersPath)` externalization, `BaseUnityPlugin` scaffold, `Config.Bind` signature.
- [../../Research/Patterns/HarmonyPatchTypes.md](../../Research/Patterns/HarmonyPatchTypes.md) Prefix / Postfix / Transpiler / Reverse Patch taxonomy, `__result` / `__instance` / `____privateField` conventions.
- [../../Research/Patterns/StationeersModdingGotchas.md](../../Research/Patterns/StationeersModdingGotchas.md) `AllDevices` duplicates, `AllAtmospheres` nulls, power NaN guards, background-thread `Random` trap, dedicated-server atmosphere null.

### 3.6. Why the mod ships text-only

- [../../Research/Workflows/TTSDeadEnd.md](../../Research/Workflows/TTSDeadEnd.md) every viable TTS path has a disqualifying blocker (Mono COM interop failure, GPL licensing, ONNX/Mono crashes). Text-only is a deliberate decision.

### 3.7. Adjacent background (not strictly required for v1)

- [../../Research/GameSystems/CameraFilterPack.md](../../Research/GameSystems/CameraFilterPack.md) camera distortion effects available for potential future "groggy descent" visual polish.
- [../../Research/GameClasses/CameraController.md](../../Research/GameClasses/CameraController.md) existing CameraFilterPack components on the main camera.
- [../../Research/GameClasses/Entity.md](../../Research/GameClasses/Entity.md) `OnCameraUpdate` post-processing hooks.
- [../../Research/Workflows/CameraEffectsRuntime.md](../../Research/Workflows/CameraEffectsRuntime.md) adding / removing camera filter components at runtime.
- [../../Research/Workflows/TimeSkipWorldManipulation.md](../../Research/Workflows/TimeSkipWorldManipulation.md) world-state APIs (sun, weather, hunger, pipes) explored during earlier time-skip design.
- [../../Research/Patterns/PrefabCloning.md](../../Research/Patterns/PrefabCloning.md) Mirrored Devices recipe; background for any future placed-bureau-device feature (explicitly not in v1).
- [../../Research/Patterns/CustomLogicValueInjection.md](../../Research/Patterns/CustomLogicValueInjection.md) Re-Volt pattern for adding logic channels to a cloned device; same "future device" thread.
- [../../Research/GameSystems/WorldStateAPIs.md](../../Research/GameSystems/WorldStateAPIs.md) atmospheres, pipe networks, cable networks, logic-channel quartet, game-state guards; covers more than this mod needs but anchors the "leave contents alone" invariant.
- [../../Research/Patterns/PeriodicProcessing.md](../../Research/Patterns/PeriodicProcessing.md) three periodic-processing options; relevant if the bureau ever grows a background ticker.

### 3.8. v2 terrain reclamation

Deferred; pointers repeated here so v2 readers do not need to walk into the archive.

- [../../Research/GameSystems/TerrainOctree.md](../../Research/GameSystems/TerrainOctree.md) dual-octree system, MaxDepth / world size, voxel / terrain class inventory.
- [../../Research/Protocols/SaveFileStructure.md](../../Research/Protocols/SaveFileStructure.md) `.save` ZIP archive layout.
- [../../Research/Protocols/TerrainDat.md](../../Research/Protocols/TerrainDat.md) terrain.dat binary format.
- [../../Research/Protocols/TerrainChunkChecksums.md](../../Research/Protocols/TerrainChunkChecksums.md) rolling XOR-multiply checksum algorithm.
- [../../Research/Protocols/WorldXml.md](../../Research/Protocols/WorldXml.md) `<Rooms>` / `<Grids>` schema, 10x grid coord scale, `<DifficultySetting>` enum.
- [../../Research/Protocols/AtmosphereSaveData.md](../../Research/Protocols/AtmosphereSaveData.md) pipe-network gas-species list.
- [../../Research/Workflows/ResetTerrainOffline.md](../../Research/Workflows/ResetTerrainOffline.md) `terrain_reset.py` algorithm, CLI, design decisions.
- [../../Research/Workflows/ResetTerrainLive.md](../../Research/Workflows/ResetTerrainLive.md) live-MP runtime reset recipe.

## 4. Harmony patches catalog (planned)

No patches exist yet. Planned:

| Patch class | Target method | Type | Effect |
|---|---|---|---|
| `ChatPatch` | `ChatMessage.Process(long hostId)` | Postfix | Server-only, global-channel-only. Every qualifying line feeds the conversation state and kicks off inference. Loop guard skips `DisplayName == BotName`. Reply is drained on main thread and broadcast via `HumanId = -1` + `NetworkServer.SendToClients(..., NetworkChannel.GeneralTraffic, -1L)`. This is the LLM prototype's patch with the trigger-prefix gate removed and a channel filter added. |

Future patches may be needed during implementation (e.g. patching `LanderCapsule.WaitThenOpen` to extend the blackout window if the sweep ever runs longer than the 13.5 s descent). They get recorded here as they land.

## 5. Design decisions

### 5.1. Applied

- **Conversation is the cost.** No materials, no cooldowns, no device. Friction is entirely social, which means the mod needs no economy balancing and leaves existing progression untouched. Rationale: the RepairPrototype design considered material costs at length and concluded the real cost of vanilla repair is player time; the bureau charges in the same currency but makes it fun.
- **Global shared bureau, no per-player state.** Multiplayer griefing risk is accepted as a feature. Watching another player wrestle the bureau is part of the loop. Rationale: the user stated explicitly that crowd heckling is the point.
- **No trigger prefix in v1.** The bureau reads all public chat. Rationale: the officer's engagement judgment is part of the flavor; gating on a prefix removes the surprise where the bureau chimes in on a completely unrelated conversation.
- **One officer at a time, new officer per cycle.** Persona consistency across a single 5-15 turn cycle is achievable with a 1.5B model. Persona consistency across back-to-back cycles is not interesting; a fresh officer per cycle is the comedy beat.
- **100 curated personas, LLM-selected per cycle.** Committed as `Personas.md` (human-reviewable) and embedded in the DLL as a resource. On cycle start the LLM is handed the full pool plus the super-summary memory of past officers, and picks one (verbatim or as a blend). Rationale: a 1.5B model given a single persona prompt produces consistent-enough voice for 15 turns; giving it a menu lets the bureau feel unpredictable without losing coherence per cycle.
- **Persona super-summary memory persisted to disk.** After each cycle, the LLM writes one sentence summarizing the retired officer. Summaries accumulate in `BepInEx/plugins/MaintenanceBureauPlus/state/persona_memory.json` (capped at 200 entries). The pool query for the next cycle includes these so the LLM can pick someone new. Rationale: over a long-running server, this keeps the bureau feeling diverse without player intervention.
- **Stun-100-before-sweep ordering.** Players are blacked out before the sweep runs so they never see inconsistent intermediate state (half-repaired world, suddenly-spawned capsules). The capsule spawn comes after the sweep and closing message. A one-time stun write of 80 immediately after the teleport lets the game's natural stun decay wake the player during descent, avoiding the need for any mod-side wake timer. Rationale: the repo testing principle prefers leaning on vanilla mechanics over mod-driven state machines.
- **Closing message signals officer retirement.** A hard system-prompt requirement for the closing-message LLM call. Every cycle ends with the officer leaving the desk (retired, lunch, reassigned, transferred). Rationale: players must know the next conversation is with a different officer; otherwise the persona rotation is invisible.
- **Two settings only: MinTurns and MaxTurns.** Every other value (model filename, temperature, thread count, context size, etc.) is hardcoded. Rationale: the configurable surface was a source of setup friction without producing meaningful gameplay variation; locking the mod's operating parameters down keeps the experience consistent across servers.
- **Approval via structured tag `[APPROVED]` in the LLM's reply.** Tag parsing is simple, debug-friendly (operators can fire the event by crafting a message), and already in pattern for the LLM prototype's chat output. Alternative considered: function calling via LLamaSharp's grammar-constrained output. Deferred because grammar support in LLamaSharp's .NET 4.7.2 surface is fiddly; tags cover the v1 need.
- **Knockout via stun-on-brain, capsule spawn via the three-call recipe.** The research on both paths is already verified. No Harmony patches required for either. Avoids the full respawn pipeline entirely.
- **Incident-damage channels only; gameplay-state channels untouched.** Writing stun, oxygen, hydration, nutrition, or stamina to zero would alter player-facing simulation in ways that feel arbitrary. The mod's job is to undo incidents, not to tune gameplay.
- **Bureau override on vanilla-unrepairable items (Rover Mk I, rockets).** Consistent with the lore: the bureau has special classification authority. Mechanically straightforward (the sweep ignores the `IRepairable == null` hint and zeroes damage channels directly).
- **Corpses and wreckage are counted, not touched.** Corpses tamper reads ghoulish; broken structures reflect player decisions (someone chose to deconstruct). Telemetry lets the LLM reference them in lore without the mod changing them.
- **Server-only target deployment.** The LLM prototype already runs server-only. The knockout and capsule APIs are server-authoritative. No client install is required unless implementation reveals a client-visible effect that needs mod-side code.

### 5.2. Rejected or deferred to v1.x / v1.5

- **Placed bureau device.** Rejected for v1; the bureau is ambient. Prefab-cloning template in `Plans/RepairArchive/plan.md` Appendix A stays as reference if a device is ever added.
- **Per-player request cap / quotas.** Deferred to v1.5 as an optional config (v1.5 list).
- **Global cooldown between approvals.** Deferred to v1.x.
- **Material costs for approval.** Deferred to v1.x in the form of optional battery drain; full ingredient cost is rejected.
- **Conversation summarization for long transcripts.** Deferred to v1.1; v1 raises context size to 4096 as a stopgap.
- **Officer memory across cycles.** Deferred indefinitely; every cycle is a clean slate.
- **Client-side TTS / radio filter.** Rejected; see `TTSDeadEnd.md`.

## 6. Pitfalls and dead ends

Most of these carry forward from the LLM prototype, which is still the best reference implementation for the chat and inference plumbing.

- **`System.Speech.Synthesis` does not load under Unity Mono.** Any client-side TTS experiment must avoid touching `System.Speech` at all. Full analysis on `TTSDeadEnd.md`.
- **`Task` vs `Thread` for inference.** Use a dedicated `Thread`, not a `Task`. `Task` rides Unity's `SynchronizationContext` and can schedule unexpectedly. `Thread` with `IsBackground = true`, `Priority = BelowNormal`, and a named identity is cleaner.
- **Capture config on main before dispatch.** `BepInEx.ConfigEntry<T>` reads are fine from any thread in practice but not a documented guarantee. Read config values on the main thread into locals, pass the locals to the background thread.
- **`StartsWith` case sensitivity.** The LLM prototype uses `StringComparison.OrdinalIgnoreCase`. The ambient listener does not do prefix matching in v1, but any v1.5 optional-prefix code should preserve the same comparison.
- **Bot loop protection.** The postfix filters messages where `DisplayName` equals the configured bot name. If a future change lets the bureau post under a different name (e.g. individual officer names in chat), re-apply the guard, or replies re-enter the patch and loop.
- **Bot messages with `HumanId = -1`.** The server always sees the broadcast (no filter) and rebroadcasts it. Clients see a chat message with no bubble. Any future attempt to attach the bureau to a physical console entity needs to reconsider bubble visibility and the loop-guard identity.
- **Sweep on `Thing.AllThings`: duplicates and nulls.** `Device.AllDevices` is known to carry duplicates. Use a `HashSet<Thing>` dedupe pass before iterating, or iterate `Thing.AllThings` (which does not have this issue). `AllAtmospheres` and some lists carry nulls; null-guard defensively.
- **Tag false positives.** A player typing "I would like this approved" must not fire the event. Tag parsing is case-insensitive but requires the exact bracketed token. Adversarial test entries live in `TODO.md`.
- **Capsule overlap in dense geometry.** The capsule descends from 100 m above the spawn point. If a player is inside a sealed room with a low ceiling, the descent will clip. Document this as a known limitation; playtest on a typical mid-game base.
- **LLM drift on a 1.5B model.** Officer voice may drift across 15 turns. Include the persona block verbatim in every prompt, not just at cycle start.

## 7. Mod-local observations

Empty for now; no implementation to observe. Fill this section as v1 implementation accumulates mod-specific facts (tested turn-count behavior, real-world sweep timings on varied save sizes, observed officer persona stability).

## 8. Archive pointers

Source material preserved under `Plans/MaintenanceBureauPlus/Plans/`:

- `LLMArchive/` prototype that seeds v1 implementation. `README.md`, `RESEARCH.md`, `TODO.md`, `CLAUDE.md`, plus full source under `LLM/` (Plugin.cs, LlmEngine.cs, ChatPatch.cs, `.csproj`, `About/`, `redist/`, gitignored `models/`). When v1 implementation starts, copy this source tree to `Plans/MaintenanceBureauPlus/MaintenanceBureauPlus/` and rename identifiers.
- `RepairArchive/plan.md` 43 KB design document for the original Bureau of Colonial Structural Integrity mod. Superseded by `plan.md` at v1 scope, but three parts stay live: Appendix B (Inspector personality samples, seeds the curated officer archetype list), Appendix A (prefab-cloning template for a potential future placed bureau device), and Section 8.X observations already lifted to central pages.
- `TerrainReclamation/` full SaveFixPrototype body (plan.md, RESEARCH.md, terrain_reset.py). Held for v2. Six open questions block Phase 2 implementation; see `TerrainReclamation/RESEARCH.md` Section 7.

## 9. Open questions

Specific to this mod's own implementation. Central `Research/` open questions live on their respective pages.

- **Context management specifics.** v1 plan parks this to v1.1. When that work starts, decide between: sliding window (drop oldest turns), summarization (model compresses older turns into a one-paragraph running summary), or context-size increase (bump to 8192 and accept the RAM cost).
- **Client-visible effects feasibility.** v1 targets server-only. Open question whether a subtle client-side visual during the blackout (screen fade, static overlay) is worth the client-install requirement. Revisit after the server-only MVP is stable.
- **Optimal approval-tag emission frequency.** Soft question. Does the model reliably emit `[APPROVED]` at the right moment given the system prompt's `MinTurns` / `MaxTurns` guidance, or does it drift toward early or late? Needs playtesting.
- **Persona stability vs. mode choice.** Curated archetypes give predictable voices but limited variety. Generated-per-cycle gives variety but may produce duds. Open question whether the default mode should flip after enough playtesting.
- **Multi-player chatter noise.** With two or more players talking, every line feeds into the same transcript. At what point does chatter confuse the 1.5B model enough to break the bureau's character? Mitigations: filter to lines that address the bureau (requires intent detection the 1.5B may not manage), or accept it as part of the comedy.

## 10. Verification history

Pages in central `Research/` carry their own Verification History sections per `Research/CLAUDE.md`. This mod-local file does not carry one; it indexes rather than verifies.
