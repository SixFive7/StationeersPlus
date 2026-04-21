# Maintenance Bureau Plus: v1 Plan

Work-in-progress mod under `Plans/`. Collapses three earlier prototypes (LLM, RepairPrototype, SaveFixPrototype) into a single design. v1 ships the LLM-gated approval event that silently repairs every damaged thing in the save. The bureau's services beyond repair stay mysterious by design; future versions expand the catalogue. Terrain reclamation is archived under `Plans/TerrainReclamation/` for v2.

## 1. One-paragraph summary

Players type normal chat. A bureau officer, a local LLM persona, reads every public chat line. To get anything done, players must work through bureaucratic hoops: apologies, form corrections, ego massages, stamp requests, whatever the active officer demands. After between 5 and 15 conversational turns (the two configurable settings; the LLM judges how many based on how well the player plays along), the officer emits a structured approval tag. The mod immediately blacks out all connected players by maxing their stun channel, silently repairs every damaged built thing in the save, generates a closing message from the officer that signs off and announces the officer is leaving the desk (retirement, lunch, reassignment, anything lore-fitting), then spawns each player a landing capsule at their current location and teleports them into it. Right after the teleport the mod writes stun to 80 once so the game's natural stun decay wakes players during the descent. After that the mod is hands off; the game finishes the capsule sequence on its own. The next qualifying chat line opens a fresh cycle with a new officer.

## 2. v1 scope

**In:**

- Single global bureau presence. One active officer at a time. One conversation at a time.
- No trigger prefix. The LLM reads every public chat line and decides whether to engage.
- Officer persona rotation: new officer on each new request cycle.
- 100 curated personas in an embedded markdown file at `MaintenanceBureauPlus/Resources/Personas.md`. Each persona carries a name, tic, voice, backstory, department, and a one-line reviewer summary.
- The LLM either picks a persona from the pool or composes a variation. Past persona visits are tracked as super-summaries so the LLM can pick something the group has not seen.
- Conversational friction between `MinTurns` (default 5) and `MaxTurns` (default 15); the LLM chooses the count based on player posture. These are the only two settings exposed to the user.
- Approval signal from the LLM to the mod via a structured tag in the reply (`[APPROVED]`, `[CONTINUE]`, `[REFUSED]`) parsed and stripped before broadcast.
- Global knock-out of all connected players via `OrganBrain.DamageState.Stun` set to 100.
- Global repair sweep on `Thing.AllThings` during the blackout, limited to the category and channel rules in Section 5.
- Telemetry to the LLM: corpse count and wreckage inventory (counts only above 10 items) included in the closing-message context.
- Closing message signals the current officer is leaving. This is a hard instruction in the closing-message system prompt.
- Per-player `LanderCapsule` spawn at each player's current position, player moved into the capsule seat, game handles the descent.
- One-time stun write of 80 immediately after the capsule teleport so natural decay wakes each player during descent without further mod intervention.
- Super-summary memory of previously-visited personas persisted across sessions on the server.
- Server-authoritative. LLM and sweep run on the server. No client install required unless implementation reveals a blocking requirement.

**Out for v1 (moved to Plans/ subfolder, see Section 12):**

- Terrain reclamation. Entire SaveFixPrototype body and Python tool live under `Plans/TerrainReclamation/`.
- Placed bureau device. The bureau is ambient.
- Per-player state, conversation memory beyond the current cycle, per-player officer.
- Material costs. Cost is entirely conversational friction.
- Partial or severity-gated repair. v1 repairs all damage in scope to zero.
- Private chat handling. Private messages are ignored entirely.
- Any mention of repair in the user-facing tagline or short blurbs. Mystery is a design goal: users should discover the bureau's capabilities in-game. The bureau's actual function stays opaque in the tagline and the Workshop short description, then becomes obvious the first time it fires.

## 3. Conversation mechanics

### 3.1. Ambient listening

A Harmony postfix on `ChatMessage.Process` (pattern already in `Plans/LLMArchive/LLM/ChatPatch.cs`) filters for:

- `NetworkManager.IsServer` guard.
- `ChatChannel` equals the global channel (not private, not squad). Private chats are skipped without inference.
- `HumanId != -1` loop guard. Every bureau reply goes out with `HumanId = -1`, so any inbound message with `HumanId = -1` is the bureau's own voice echoing back.
- `Engine != null && Engine.IsLoaded`.

Every qualifying line is forwarded to the engine with the current conversation state attached.

### 3.2. Conversation state

Single global state held server-side. Not per-player. Fields:

- `OfficerPersona` (name, department, tic, voice, backstory) selected at cycle start.
- `TurnCount` incremented per qualifying line.
- `MinTurns`, `MaxTurns` config-bound, read at cycle start.
- `TranscriptTail` rolling last N turns.
- `PersonaMemory` super-summaries of previously-visited personas (see Section 6.3).
- `IsActive` flag; when false the next qualifying line begins a new cycle with a fresh officer.

The LLM sees: the current transcript tail, the officer persona block, a running counter `TurnCount / MaxTurns`, the persona memory super-summary list, and an instruction in the system prompt to decide between continuing, approving, or refusing this turn.

### 3.3. Approval signal

The LLM emits one of three tokens at the end of its reply:

- `[CONTINUE]` the officer stays in session; reply is posted to global chat, `TurnCount++`.
- `[APPROVED]` the officer approves; the mod strips the tag, posts the reply, kicks off the approval event (Section 4). This is also the developer debug hook: posting a crafted message in chat containing `[APPROVED]` on a debug build triggers the event.
- `[REFUSED]` the officer rejects the request outright, cycle ends, the next qualifying line opens a fresh cycle.

Tag parsing is case-insensitive exact-token match (bracketed), not substring-in-word. Free-form text may surround the tag. The tag is stripped before broadcast.

The LLM's posture judgment is soft: the system prompt tells it to approve earlier when the player engages, later when the player is angry or non-compliant, and to respect `MinTurns` / `MaxTurns` as hard bounds.

### 3.4. Output sizing

Bureau replies target roughly five paragraphs of mid-density lore per turn, sized for reading not typing. Player replies are never gated on length or spelling. The system prompt tells the officer to treat short, misspelled, or terse player messages as legitimate engagement, not as anger.

## 4. Approval event sequence

On `[APPROVED]` the server runs this sequence. All phases server-authoritative. See `Research/Workflows/KnockPlayerUnconscious.md` and `Research/Workflows/TriggerLanderCapsule.md` for the exact game-API recipes.

1. **Lock.** Set `IsActive = false`; block new bureau replies until the event completes.
2. **Broadcast the approval message.** The LLM's reply (with the `[APPROVED]` tag stripped) goes out on global chat under the officer's name. Players see the officer agreeing to the request.
3. **Full blackout.** For every connected `Human`, set `OrganBrain.DamageState.Stun = 100`. Players go unconscious immediately; their view fades to black. This happens before any state mutation so players never see the repair sweep or the capsule spawn.
4. **Repair sweep.** Iterate `Thing.AllThings` per Section 5. Single frame. Zero only incident-damage channels on the inclusion set; leave everything else untouched.
5. **Telemetry collection.** Walk `Thing.AllThings` once more (or as part of the same pass) to count corpses (`DynamicBodyBag`) and broken structures (`IsBroken == true`). If broken count is 10 or fewer, record each wreckage type + approximate location. Otherwise record the count only.
6. **Closing message.** Single-turn LLM call with the active officer persona, the telemetry data, and a hard system-prompt instruction: deliver the post-repair in-lore wrap-up AND signal that the officer is leaving the desk (retired, lunch, reassigned, transferred, recalled, whatever fits the persona). The player must end the message believing the next conversation will be with someone else. Broadcast the reply.
7. **Capsule spawn per player.** For each `Human`: capture position and rotation, `OnServer.Create<LanderCapsule>(prefab, pos, rot)`, `OnServer.MoveToSlot(human, capsule.Slots[1])`, `OnServer.Interact(capsule.InteractMode, 1)`.
8. **One-time wake-during-descent write.** Immediately after a player is moved into their capsule's seat, set `OrganBrain.DamageState.Stun = 80` once. The game's natural stun decay (3 per life tick) will reduce this over the 13.5 s descent so the player wakes up groggy as the capsule lands. The mod does not touch stun again.
9. **Hands off.** The game completes the capsule descent on its own. No mod patches or timers monitor the rest.
10. **Reset.** Clear `ConversationState`. Push the retired officer's super-summary onto the persona memory store. Next qualifying chat line opens a fresh cycle with a newly-selected officer.

Bots and livestock are untouched throughout.

Rationale for the new order (stun 100 before sweep, then capsule spawn, then one-time stun 80):

- Stunning to 100 first guarantees players never witness the sweep or the capsule's sudden appearance. The blackout covers everything.
- Doing the sweep while players are fully unconscious means they wake up to a healed world with no inconsistent intermediate state.
- Writing stun to 80 exactly once after the teleport lets the vanilla stun decay handle wake timing. No mod ticker, no stun fiddling, no rolling back.
- If natural decay is too slow to wake the player during the 13.5 s descent, the stun setting of 80 is the tuning knob; lower it in code if playtesting shows players still groggy after the capsule opens.

## 5. Repair sweep

Iterate `Thing.AllThings`. For each thing, apply the rules in this exact order.

### 5.1. Exclusion filters (skip thing entirely)

- `thing is Human` (players).
- `thing is Organ` (organs inside any entity).
- `thing is AIMEe` or other bot / livestock entity types.
- `thing is DynamicBodyBag` (corpses; counted separately for telemetry).
- `thing is Plant` and any `GrowthStage` item.
- `thing is Egg` / embryo types.
- `thing` is a broken wreckage structure (`IsBroken == true`; counted separately for telemetry).

### 5.2. Inclusion set

Everything else that carries a `DamageState`:

- `LargeStructure` (walls, floors, doors, airlocks, frames).
- `SmallGrid` (pipes, cables, chutes themselves).
- `Device` (machinery, sensors, fabricators, hydroponics trays, storage containers).
- `Item` at any location (inventory, ground, slots inside containers, fabricator queues, equipped on a living player).
- `DynamicThing` vehicles (landers, rovers, bikes). Vehicles the vanilla game marks unrepairable (Rover Mk I, rockets) repair anyway under bureau override.

### 5.3. Channels in scope

Only incident-damage channels are written. Gameplay-state channels are left untouched.

| Channel | Structure / device / vehicle (`ThingDamageState`) | Item / worn equipment (`OrganicDamageState`) | Written to 0? |
|---|---|---|---|
| Brute | yes | yes | yes |
| Burn | yes | yes | yes |
| Toxic | n/a | yes | yes |
| Radiation | n/a | yes | yes |
| Stun | n/a | yes | **NO** |
| Oxygen | n/a | yes | **NO** |
| Hydration | n/a | yes | **NO** |
| Nutrition | n/a | yes | **NO** |
| Stamina | n/a | yes | **NO** |

Pipe / chute / tank contents, atmosphere, charge, stack counts, IC program data, and any property other than the listed damage channels are never touched.

### 5.4. Scrap items

`Item` instances representing deconstructed scrap fall into the general Item inclusion set. Zeroing their damage channels is effectively a no-op for gameplay. No special case required.

## 6. Officer persona

### 6.1. The 100 curated pool

One hundred personas live in `MaintenanceBureauPlus/Resources/Personas.md` as an embedded resource in the DLL. The file is also committed at `Plans/MaintenanceBureauPlus/Personas.md` for human review; the deploy copy is the source of truth for runtime, the plan copy is the review mirror, and both are edited together.

Each persona carries:

- **Name.** Unique across the 100.
- **Department.** Which slice of the bureau this officer belongs to (Structural Integrity, Requisitions, Forms Compliance, Grievances, etc.).
- **Tic.** The one defining verbal or behavioral quirk. Never repeated across personas.
- **Voice.** A tone descriptor (clerical, wounded, deranged, pedantic, etc.).
- **Backstory.** Two to three sentences of specific history. At least one grounded detail (year count, dismissed promotion, transfer reason) the LLM can latch on to.
- **Summary.** A single sentence at the top of each entry for human reviewers to scan the index.

`Personas.md` structure is section-per-persona with a numeric heading (`## 1. Officer Voss`, `## 2. Officer Huang`, ...) and bullet fields. The mod parses this format at runtime via simple regex; deviations break the parser.

The 20 archetype categories used to produce variety (5 personas each): pedantic form-obsessives, wounded middle managers, former field inspectors, true believers, cynics, idealistic newcomers, near-retirement rumblers, specialist obsessives, provincial office types, central office snobs, deranged eccentrics, ex-military desk workers, academic citers, conspiracy theorists, obsequious terror, union reps, corporate consultants, poetic interpreters, over-eager interns, phoning-it-in temps.

### 6.2. LLM authority over persona selection and adaptation

When a fresh cycle starts, the mod passes the LLM:

- The full 100-persona pool.
- The super-summary memory list of previously-visited personas.
- An instruction: "pick a persona from the pool the group has not seen recently, or compose a variation that blends traits from two pool entries. Then speak as that officer for the rest of the cycle."

The LLM is free to pick a listed persona verbatim, pick one and riff on it, or blend two. Whatever it picks gets locked into `ConversationState.OfficerPersona` for the duration of the cycle. The LLM's choice is recorded in the super-summary memory when the cycle ends.

### 6.3. Persona memory store

After each cycle completes (approval or refusal), the mod:

- Asks the LLM for a one-sentence super-summary of the officer it just played. Format: "Officer <Name>, <Department>: <distinctive detail from the cycle that makes them recognizable next time>".
- Appends the super-summary to a persistent JSON file at `BepInEx/plugins/MaintenanceBureauPlus/state/persona_memory.json` on the server.
- Caps the list at some reasonable limit (initial value: 200 entries; older entries trimmed from the front when the cap is exceeded).

The file persists across server restarts. Fresh saves start with an empty memory; the mod does not prune by save association in v1 (all saves on the server share one memory store). This may be revisited in v1.x if players ask.

## 7. Architecture

Reuses the LLM prototype's thread and chat-broadcast model essentially as-is. The LLM prototype's source under `Plans/LLMArchive/LLM/` is the starting scaffold; v1 implementation begins by copying that source into a new `MaintenanceBureauPlus/` source tree at the mod root, renaming identifiers, and adding the repair subsystem.

### 7.1. Threads

- **Main (Unity):** patch application, chat broadcast, approval-event orchestration, `Thing.AllThings` sweep.
- **Model loader:** one-shot background thread started at `OnAllModsLoaded`.
- **Inference:** long-lived background thread draining a `ConcurrentQueue<InferenceRequest>`.
- **Response drain:** `Plugin.Update()` pulls from `ConcurrentQueue<string>` and posts to chat on main thread.

No change from the LLM prototype except that the inference request now carries the full conversation state bundle rather than a bare player message.

### 7.2. Server / client split

Target: server-only plugin, no client install required, matching the LLM prototype's footprint.

The knockout path (`OrganBrain.DamageState.Stun`) and capsule spawn (`OnServer.Create<LanderCapsule>` + `OnServer.MoveToSlot` + `OnServer.Interact`) are server-authoritative; clients see state changes through the normal sync path. The LLM prototype's `HumanId = -1` chat broadcast pattern (via `NetworkServer.SendToClients` on `NetworkChannel.GeneralTraffic`) handles all outgoing messages without client-side code.

If during implementation a client-visible effect turns out to require client-side code, switch to a client-required deployment and set the StationeersLaunchPad `ClientRequired` flag accordingly. Default assumption: server-only until proven otherwise.

### 7.3. Dependencies

- BepInEx 5.4.21+
- StationeersLaunchPad + LaunchPadBooster
- HarmonyX (via BepInEx)
- LLamaSharp + LLamaSharp.Backend.Cpu (NuGet, copied into deploy)
- Native `llama.dll` (from the CPU backend runtimes folder)
- Qwen2.5-1.5B-Instruct Q4_K_M GGUF model, ~1.1 GB, shipped separately (gitignored). Filename hardcoded to `qwen2.5-1.5b-instruct-q4_k_m.gguf`.

The LLM prototype's `.csproj` + `redist/` layout handles all of this correctly already; carry it forward verbatim into the new source tree.

## 8. Settings

Only two settings are exposed. Every other value is hardcoded in code.

| Order | Key | Default | Description |
|---|---|---|---|
| 10 | Minimum Turns | 5 | Lower bound on conversational hoops per request cycle. |
| 20 | Maximum Turns | 15 | Upper bound on conversational hoops per request cycle. |

Settings live under a single `Server - Bureau` section per the monorepo settings-grouping rule.

Hardcoded values (intentionally not exposed; changing them requires a code edit and a new release):

- Model file name: `qwen2.5-1.5b-instruct-q4_k_m.gguf`.
- Bot name per message: the currently-active officer's name (no fixed bot name).
- Loop guard: `HumanId == -1` (bureau messages are sent with no human entity).
- System prompt preamble: baked into a const string in code; officer-specific block is appended from the persona record.
- Max tokens per reply: 384.
- Closing-message max tokens: 512.
- Temperature: 0.8.
- Inference threads: 4.
- Context size: 4096.
- Persona memory cap: 200 entries.
- Stun at blackout: 100.
- Stun one-time wake write: 80.

## 9. Folder layout (current state)

```
Plans/MaintenanceBureauPlus/
  plan.md                             this document
  README.md                           draft of the future released README
  TODO.md                             v1.x / v2 feature list
  CLAUDE.md                           mod-local rules
  RESEARCH.md                         central Research pointers
  Personas.md                         100 curated personas (human-reviewable, mirrors the embedded-resource copy)
  MaintenanceBureauPlus.sln           solution (after scaffolding)
  MaintenanceBureauPlus/              mod source tree (after scaffolding)
    MaintenanceBureauPlus.csproj
    Plugin.cs, LlmEngine.cs, ChatPatch.cs
    ConversationState.cs, OfficerPersona.cs
    PersonaRegistry.cs, PersonaMemoryStore.cs
    ApprovalTagParser.cs, ApprovalEvent.cs
    RepairSweep.cs, TelemetryCollector.cs
    Resources/
      Personas.md                     embedded resource; mirror of Plans/MaintenanceBureauPlus/Personas.md
    About/
      About.xml, Preview.png, thumb.png (placeholders pending real art)
    Properties/AssemblyInfo.cs
    redist/                           MSVC runtime DLLs for LLamaSharp native deps
    models/                           developer-local gguf placement (gitignored)
  Plans/
    TerrainReclamation/               deferred v2 work (ex-SaveFixPrototype)
    RepairArchive/                    original BCSI design for reference
    LLMArchive/                       prototype source preserved
```

## 10. Research pointers

Read in this order when starting implementation. Also see `RESEARCH.md` for the full list.

- [../../Research/Workflows/KnockPlayerUnconscious.md](../../Research/Workflows/KnockPlayerUnconscious.md) stun-on-brain mechanism, exact method calls, natural decay rate.
- [../../Research/Workflows/TriggerLanderCapsule.md](../../Research/Workflows/TriggerLanderCapsule.md) three-call capsule spawn recipe, descent timings.
- [../../Research/GameClasses/LanderCapsule.md](../../Research/GameClasses/LanderCapsule.md) class fields, descent constants, lifecycle.
- [../../Research/GameSystems/RepairMechanics.md](../../Research/GameSystems/RepairMechanics.md) vanilla repair paths, unrepairable list, the "cost is player time, not resources" framing.
- [../../Research/GameSystems/DamageState.md](../../Research/GameSystems/DamageState.md) damage-channel inventory per type, field semantics, save-edit safety.
- [../../Research/GameClasses/ChatMessage.md](../../Research/GameClasses/ChatMessage.md) + [../../Research/GameSystems/ChatBroadcast.md](../../Research/GameSystems/ChatBroadcast.md) public chat message shape and broadcast flow.
- [../../Research/GameClasses/Thing.md](../../Research/GameClasses/Thing.md) + [../../Research/Patterns/PooledSpanEnumeration.md](../../Research/Patterns/PooledSpanEnumeration.md) class hierarchy and safe iteration of `AllThings`.
- [../../Research/Patterns/MainThreadDispatcher.md](../../Research/Patterns/MainThreadDispatcher.md) the `ConcurrentQueue` + `Update()` drain pattern already used by the LLM prototype.
- [../../Research/Patterns/AsyncEnumerator472.md](../../Research/Patterns/AsyncEnumerator472.md) the LLamaSharp streaming workaround for .NET Framework 4.7.2.
- [../../Research/Workflows/TTSDeadEnd.md](../../Research/Workflows/TTSDeadEnd.md) why the bureau is text-only.

## 11. Future work

Tracked in `TODO.md`. Highlights:

- **v1.1:** sliding-window or summarization strategy for long conversations.
- **v1.x:** gate approval on every connected player having their helmet closed.
- **v1.x:** partial battery drain as an additional request cost.
- **v1.5:** optional trigger prefix as a config knob (off by default).
- **v2:** terrain reclamation per `Plans/TerrainReclamation/`.
- **vNext:** additional bureau services beyond repair, each gated behind its own conversational loop, each preserving the opaque-until-discovered framing.

## 12. Deferred work

### 12.1. Terrain reclamation (v2)

Full body lives in `Plans/TerrainReclamation/`. Key points carried forward into v2 scope:

- Phase 1 Python tool (`terrain_reset.py`) edits `.save` files offline.
- Phase 2 C# mod at runtime calls `VoxelTerrain.Octree.SetDensity()` per voxel.
- Six open questions block Phase 2 implementation; see `Plans/TerrainReclamation/RESEARCH.md` Section 7.
- Durable game-internals already migrated to central pages.

### 12.2. Original BCSI design (reference)

`Plans/RepairArchive/plan.md` carries the original 43 KB design doc. Appendix B (Inspector personality samples) seeded part of the 100-persona pool. Appendix A (prefab cloning template) is reference for a potential future placed bureau device.

## 13. Implementation order

Rough sequence for v1. Each step ends at a testable checkpoint.

1. **Scaffold.** Copy `Plans/MaintenanceBureauPlus/Plans/LLMArchive/LLM/` into `Plans/MaintenanceBureauPlus/MaintenanceBureauPlus/`. Rename `.sln`, `.csproj`, folder, namespace, Plugin class, ModID. Drop all config settings except `MinTurns` and `MaxTurns`. Hardcode the rest. Switch loop guard from `DisplayName` match to `HumanId == -1`. Build; confirm `MaintenanceBureauPlus.dll`.
2. **Ambient listening.** Change `ChatPatch` from prefix-match to read-all. Add global-channel guard. Verify every public chat line logs on a two-player test.
3. **Conversation state.** Add `ConversationState`, transcript tail, turn counter. Drive the LLM with the state-bundle prompt structure.
4. **Persona pool.** Embed `Personas.md` as a resource. Write `PersonaRegistry` that parses the markdown at startup. On cycle start, pass the pool + memory to the LLM and let it pick.
5. **Approval tag.** Parse `[CONTINUE]` / `[APPROVED]` / `[REFUSED]`. On `[APPROVED]`, log a dummy event (no repair yet) for isolation.
6. **Blackout + sweep.** Implement `OrganBrain.DamageState.Stun = 100` on all players, then the `Thing.AllThings` iteration with the exclusion filter and incident-channel writes. Verify with InspectorPlus snapshots.
7. **Telemetry + closing message.** Add corpse / wreckage census, feed into a closing-message LLM prompt, require agent-retirement signal in the system prompt for that call. Broadcast the reply.
8. **Capsule spawn + one-time stun 80.** Per-player capsule spawn, teleport, one-time `Stun = 80` write. Confirm kit survives, wake during descent works.
9. **Persona memory.** Persist the officer's super-summary to `persona_memory.json` after every cycle end. Reload on startup.
10. **About.xml, tagline sync, build surface.** Finalize user-facing text for Release.

InspectorPlus-backed verification on each state-changing step per the repo's testing rules.

## 14. Risks and unknowns

- **Conversation context overflow.** 15 turns of ~5-paragraph replies plus a 100-persona pool summary plus the memory list will push the 4096-token context. Park to v1.1; may need summarization or a context bump.
- **Officer consistency with a 1.5B model.** Voice drift across 15 turns is likely. Mitigation: include the persona block verbatim in every prompt.
- **Multiple players talking over each other.** The bureau sees every line. Chatter may confuse the 1.5B model.
- **Capsule spawn in unusual locations.** Capsule descends from 100 m above the spawn point; may clip in dense geometry. Document as a known limitation.
- **Sweep cost.** `Thing.AllThings` can hold thousands on a mature save. Single-frame write of a few fields per thing should be cheap, but worth measuring.
- **Tag leakage.** If `[APPROVED]` slips out mid-continuation, the mod fires early. Strict case-insensitive exact-token match, test against adversarial inputs.
- **Closing-message LLM hang.** If the closing-message inference stalls or errors, the approval event is stuck waiting. Need a timeout + fallback (generic officer-retires stock sentence).
- **Persona memory file growth.** Capped at 200 entries but even then the file has to be read at startup. Small text is fine; worth a thought if the cap ever grows.

## 15. Naming

- Display name: `Maintenance Bureau Plus`
- Code name: `MaintenanceBureauPlus`
- ModID: `net.sixfive7.maintenancebureauplus`
- Plugin GUID: same as ModID
- Tagline (for the three synced surfaces): "A server-side bureau listens to every public chat message on your save. What the bureau does and who answers is for you to find out."

Tagline is deliberately opaque. No mention of repair, of inspection, of anything specific. Future expansions add services under the same bureau umbrella; the tagline carries forward without needing updates.
