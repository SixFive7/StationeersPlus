# Maintenance Bureau Plus - TODO

Tracks the work for v1, then the deferred v1.x / v1.5 / v2 list. Conventions match the monorepo: `- [ ]` open, `- [x]` done.

---

## Session handoff (2026-04-22)

> A fresh agent picking up this work should read this entire section, then the rest of TODO.md, then [plan.md](plan.md), then [RESEARCH.md](RESEARCH.md). The rest of the sections below this handoff describe the project's full planned scope; this handoff describes the current implementation state and exactly where to pick up.

### Where we are

v1 source is compiled, deployed, and the plugin loads successfully in-game. The `ping` → `pong` hotpath works end-to-end, proving chat intercept + main-thread broadcast. The first real LLM turn currently takes minutes on a CPU-only 1.5B model because of prompt-ingestion cost; the most recent commit (`2f3ba73`) rewrites this to use `InteractiveExecutor` for KV-cache reuse across turns. **That commit has not been playtested yet** — that is the next step.

### Deployment layout (important, non-obvious)

The mod is **not** deployed through StationeersLaunchPad. It lives at `E:\Steam\steamapps\common\Stationeers\BepInEx\plugins\MaintenanceBureauPlus\` as a plain BepInEx plugin. The `modconfig.xml` Local entry for it is `Enabled="false"`. This is because LaunchPad's mod scanner aborts the entire mod load on the first native DLL under the deploy folder; LLamaSharp ships native libraries. Full write-up: [../../Research/Workflows/LaunchPadNativeDllTrap.md](../../Research/Workflows/LaunchPadNativeDllTrap.md).

Side effect: the bureau is not listed in LaunchPad's in-game mod list and F3 log-viewer does not show MBP messages. All logs go to `E:\Steam\steamapps\common\Stationeers\BepInEx\LogOutput.log` and the Unity player log at `/c/Users/jori/AppData/LocalLow/Rocketwerkz/rocketstation/Player.log`.

### Build + deploy cheat sheet

```bash
# Build (from repo root)
dotnet build "Plans/MaintenanceBureauPlus/MaintenanceBureauPlus.sln" -c Release 2>&1 | tail -5

# Deploy (requires game closed — DLL is locked while game runs)
cp "Plans/MaintenanceBureauPlus/MaintenanceBureauPlus/bin/Release/MaintenanceBureauPlus.dll" \
   "E:/Steam/steamapps/common/Stationeers/BepInEx/plugins/MaintenanceBureauPlus/MaintenanceBureauPlus.dll"

# Check the logs
grep -nE "MaintenanceBureauPlus\]|ChatPatch|Inference|Bureau\]" \
   "E:/Steam/steamapps/common/Stationeers/BepInEx/LogOutput.log"
```

### What works (confirmed via playtest)

- Plugin loads at BepInEx Chainloader phase. `Preloaded native lib` (x6), `Force-loaded Microsoft.Bcl.AsyncInterfaces`, `PersonaRegistry loaded 100 personas`, `PersonaMemoryStore loaded 0 entries`, `Harmony patches applied`, `MBP_MainThreadTicker GameObject created` all log in that order.
- Model load completes (~30 s on local hardware for the 1.1 GB GGUF).
- `ChatPatch.Postfix` fires on every public chat message (host + clients) because the Harmony target is `ChatMessage.PrintToConsole`, not `Process` (which never runs locally on the host).
- `[DEBUG-APPROVE]` in a chat message fires `ApprovalEvent.Start()`.
- `ping` in chat replies with `pong` from `Bureau PingBot` via `BroadcastAsOfficer`, proving chat-in + chat-out.
- `MainThreadQueue.Drain()` runs from the top of `ChatPatch.Postfix` (emergency drain), since Unity's `Update()` does not fire on our MonoBehaviour. This is why inter-turn LLM responses can land on main thread at all.

### What's confirmed broken / not working yet

- **Unity `Update()` never fires on our plugin.** Tried `BaseUnityPlugin.Update`, `Start`, `FixedUpdate`, `LateUpdate`, and a freshly-attached `MainThreadTicker` MonoBehaviour on a `DontDestroyOnLoad` GameObject. None of them tick. Watchdog background thread fires exactly once (5 s after Awake) then silently stops. Root cause not identified. Workaround: drain `MainThreadQueue` from inside `ChatPatch.Postfix` (runs on main thread). Every incoming chat message drains any pending callbacks. This is a latency band-aid; LLM replies to player message N broadcast when player sends message N+1.
- **First LLM turn takes minutes.** Pre-`2f3ba73` design sent a ~2 kB system block per turn and re-tokenized it every time on the 1.5B CPU model. `2f3ba73` switches to `InteractiveExecutor` so only turn 1 pays the full ingest cost; turn 2+ send only the delta. Not playtested.

### What each fix committed so far solved (in rough order)

Read these commits in order if you need to understand why the code is shaped the way it is:

| Commit | Purpose |
|---|---|
| `694809f` | Folder restructure: three prototypes collapsed into `Plans/MaintenanceBureauPlus/`. |
| `0cecc0a` | 100 curated officer personas in `Personas.md` (human-review) + mirrored to `Resources/Personas.md` (embedded at runtime). |
| `d1b60e6` | Scaffolded source tree from the LLM prototype. |
| `88e1c08` | SDK-style csproj (was classic); discovered `Assets.Scripts.OcclusionManager.AllThings` is the actual iteration target, not a phantom `Thing.AllThings`. |
| `dc90d5d` | Decompile-verified game APIs for the repair event. Added to `Research/GameClasses/{Human,LanderCapsule,Structure,ChatMessage}.md` and `Research/Workflows/{KnockPlayerUnconscious,TriggerLanderCapsule}.md`. |
| `c716fca` | Reflection shims resolved to concrete APIs, JsonUtility replaces Newtonsoft, `StunBlackout = 1000`. |
| `b88859b` | Documented the LaunchPad native-DLL scan trap in `Research/Workflows/LaunchPadNativeDllTrap.md`. |
| `134781a` | Preload LLamaSharp native libs via `kernel32!LoadLibrary` so `DllImport("llama")` resolves — Mono's default probe doesn't search `BepInEx/plugins/<mod>/runtimes/win-x64/native/`. |
| `7e82e8f` | Harmony patches apply in `Awake` (not in a deferred `Update` branch that never fired). |
| `3d5f903` | Switched Harmony target from `ChatMessage.Process` to `ChatMessage.PrintToConsole`; `Microsoft.Bcl.AsyncInterfaces` force-loaded in Awake before LLamaSharp touches `IAsyncEnumerable<T>`. Reflection-based async-enumerator drain lands. Emergency `MainThreadQueue.Drain()` at top of `Postfix`. |
| `d4c96e6` | Fixed the reflection lookup to handle explicit interface implementations — LLamaSharp's compiler-generated state machine exposes `GetAsyncEnumerator` / `MoveNextAsync` / `DisposeAsync` as explicit impls with qualified names. |
| `3af06d3` | Added `[LlmEngine] Inference start:` / `Inference done:` logging and the `ping` → `pong` hotpath to isolate LLM from broadcast flow. |
| `ff05052` | Replaced 14 kB LLM-driven persona-selection prompt with a local `PickRandomUnseenPersona()` that biases away from officers named in `PersonaMemoryStore` super-summaries. |
| `2f3ba73` | **Most recent.** InteractiveExecutor migration for in-cycle turns. Turn 1 sends the full ~2 kB system block once and caches it; turns 2+ send ~100 char deltas. Also fixed Dispose to cap shutdown delay at 500 ms and skip native disposal. |

### Gotchas that tripped us up (so a fresh agent doesn't repeat them)

1. **Native DLLs in the mod folder break LaunchPad.** See the `LaunchPadNativeDllTrap` Research page. We sidestepped by deploying as a BepInEx plugin. The Workshop-ready fix (TODO below) is to ship natives as `.dll.bin` inside the mod folder and extract to a user-data dir on first launch.
2. **Mono's `DllImport` doesn't search the mod folder.** Preload native libs with `kernel32!LoadLibrary` by absolute path in `Plugin.Awake()`.
3. **`Microsoft.Bcl.AsyncInterfaces.dll` must be explicitly `Assembly.LoadFrom`'d** before anything touches `IAsyncEnumerable<T>`, or Mono's JIT resolves the interface to a built-in version that lacks `MoveNextAsync()`.
4. **LLamaSharp's async-enumerator exposes `MoveNextAsync` / `Current` / `DisposeAsync` as explicit interface impls** on a compiler-generated state machine. `GetMethod("MoveNextAsync", Public | Instance)` returns null. Use `GetMethods(Public | NonPublic | Instance)` and match by suffix. See `LlmEngine.FindInstanceMethod`.
5. **Harmony target for chat must be `ChatMessage.PrintToConsole`**, not `ChatMessage.Process`. The host's own outgoing chat does not call `Process` locally; it calls `PrintToConsole` then `NetworkServer.SendToClients`. Both host-send and client-receive paths hit `PrintToConsole`.
6. **`Update()` does not fire on BepInEx plugins on Stationeers** (at least in our testing). Don't design any logic around Update ticking. Drain main-thread queues from Harmony postfixes on methods that DO fire.
7. **LLamaSharp's `StatelessExecutor` re-tokenizes the full prompt every call.** For multi-turn conversations, `InteractiveExecutor` keeps the KV cache.
8. **`Engine.Dispose()` on shutdown must not wait long** for the worker thread — native llama code can ignore cancellation for minutes. Cap Join at 500 ms and do not call native `Dispose` on the model/context; the OS reclaims on process exit.
9. **Stun damage write**: `human.DamageState.Damage(ChangeDamageType.Set, value, DamageUpdateType.Stun)`. `EntityDamageState` auto-forwards Stun writes to the brain organ; no need to reach `human.OrganBrain.DamageState` directly.
10. **LanderCapsule spawn recipe**: `Prefab.Find<LanderCapsule>("LanderCapsule")` — the overload REQUIRES the string argument; the older research note with a parameterless `Find<T>()` was wrong and is corrected in `Research/Workflows/TriggerLanderCapsule.md`.

### Immediate next steps (pick up here)

1. **Playtest commit `2f3ba73` (InteractiveExecutor).** Restart Stationeers, load save, type 3-4 chat messages spaced a few seconds apart. Check `LogOutput.log` for:
   - Turn 1: `[LlmEngine] Inference start: mode=interactive promptChars=~2200` and an `Inference done: N ms`.
   - Turns 2+: `promptChars` under 200, `Inference done` ms should drop significantly (ideally order of magnitude).
   - If turn 2 `promptChars` is still ~2200, the interactive cache isn't engaging.
   - Also confirm `[LlmEngine] Dispose starting.` → `Dispose done.` within a second when the game exits. If shutdown is still slow, report the timing.
2. **If interactive latency is still too slow** for the UX: options listed under "What could shrink the prompt further" in the conversation on 2026-04-22 — trim `GlobalBureauPreamble` from 955 chars, trim `ApprovalTagRules` from 592 chars, or switch model to a smaller quant. Pick after seeing the turn-2 timings.
3. **Native-DLL extraction for Workshop deploy.** See the "Playtest gate" first bullet below; this blocks publishing.
4. **`PickRandomUnseenPersona` code review.** Six points queued below.
5. **Remove the `[DEBUG-APPROVE]` hook** before any public release.
6. **(Optional, non-blocking) Investigate why `Update()` doesn't fire.** Currently worked around by postfix-drain, which is sufficient for functionality but adds latency (replies land when next message arrives). Not urgent.

### Key files (for orientation)

| Path | Purpose |
|---|---|
| [`Plans/MaintenanceBureauPlus/plan.md`](plan.md) | v1 design. Scope, settings, approval event, repair sweep, persona mechanics. |
| [`Plans/MaintenanceBureauPlus/RESEARCH.md`](RESEARCH.md) | Pointers into central `Research/` pages by subsystem. |
| [`Plans/MaintenanceBureauPlus/Personas.md`](Personas.md) | 100 curated officer personas (human-reviewable copy). |
| `MaintenanceBureauPlus/MaintenanceBureauPlus/Plugin.cs` | Entry point: Awake preload, PersonaRegistry, PersonaMemoryStore, MainThreadQueue, watchdog. |
| `MaintenanceBureauPlus/MaintenanceBureauPlus/LlmEngine.cs` | LLamaSharp wrapper. Stateless + Interactive APIs, worker thread, reflection-based async drain, Dispose hang cap. |
| `MaintenanceBureauPlus/MaintenanceBureauPlus/ChatPatch.cs` | Harmony postfix on `ChatMessage.PrintToConsole`. Conversation state machine, emergency main-thread drain, ping hotpath, cycle start/end wiring, persona local-picker. |
| `MaintenanceBureauPlus/MaintenanceBureauPlus/ApprovalEvent.cs` | Post-approval sequence: stun, sweep, closing message, capsule spawn. |
| `MaintenanceBureauPlus/MaintenanceBureauPlus/SystemPrompts.cs` | Prompt templates: preamble, approval rules, closing, super-summary. |
| `MaintenanceBureauPlus/MaintenanceBureauPlus/Resources/Personas.md` | Embedded copy of Personas.md loaded at startup via PersonaRegistry. |
| `E:/Steam/steamapps/common/Stationeers/BepInEx/plugins/MaintenanceBureauPlus/` | Deploy location. |
| `E:/Steam/steamapps/common/Stationeers/BepInEx/LogOutput.log` | BepInEx plugin log. |
| `/c/Users/jori/AppData/LocalLow/Rocketwerkz/rocketstation/Player.log` | Unity runtime log; stack traces and exceptions land here. |

### Research pages relevant to this mod

- [`Research/Workflows/LaunchPadNativeDllTrap.md`](../../Research/Workflows/LaunchPadNativeDllTrap.md) — why we deploy as a BepInEx plugin.
- [`Research/Workflows/KnockPlayerUnconscious.md`](../../Research/Workflows/KnockPlayerUnconscious.md) — stun-on-brain mechanism for the approval event.
- [`Research/Workflows/TriggerLanderCapsule.md`](../../Research/Workflows/TriggerLanderCapsule.md) — three-call capsule spawn recipe.
- [`Research/GameClasses/Human.md`](../../Research/GameClasses/Human.md) — `Human.AllHumans` enumeration.
- [`Research/GameClasses/LanderCapsule.md`](../../Research/GameClasses/LanderCapsule.md) — Slots, InteractMode, descent constants.
- [`Research/GameClasses/Structure.md`](../../Research/GameClasses/Structure.md) — `IsBroken` property for the repair-sweep exclusion filter.
- [`Research/GameClasses/ChatMessage.md`](../../Research/GameClasses/ChatMessage.md) — chat-channel-doesn't-exist finding and the loop guard (`HumanId == -1`).
- [`Research/Patterns/AsyncEnumerator472.md`](../../Research/Patterns/AsyncEnumerator472.md) — manual async-enumerator drain pattern (since superseded by the reflection-based one in `LlmEngine`, but the page still explains why it's needed).

### Reverting

- Pure BepInEx deploy: nothing in the repo points at `BepInEx/plugins/MaintenanceBureauPlus/`; to revert, delete that folder and re-enable the modconfig Local entry. The repo tree is untouched by the deploy.
- Local `Directory.Build.props` has the `EnsureStationeersPath` target we added. Gitignored. Delete to revert.

---

## v1: first shippable release

Ordered to match Section 13 of `plan.md`.

### Scaffolding

- [ ] Create `Plans/MaintenanceBureauPlus/MaintenanceBureauPlus/` source tree by copying `Plans/LLMArchive/LLM/`. Rename:
  - [ ] `LLM.sln` -> `MaintenanceBureauPlus.sln`
  - [ ] inner `LLM/` -> `MaintenanceBureauPlus/`
  - [ ] `LLM.csproj` -> `MaintenanceBureauPlus.csproj`; update `RootNamespace`, `AssemblyName`
  - [ ] namespace `LLM` -> `MaintenanceBureauPlus` across all `.cs` files
  - [ ] class `LlmPlugin` -> `MaintenanceBureauPlusPlugin`; constants `PluginGuid`, `PluginName`
  - [ ] `About/About.xml`: `<Name>`, `<ModID>`, `<Description>` (mysterious tagline), `<InGameDescription>`, `<ChangeLog>` reset, `<WorkshopHandle>0</WorkshopHandle>`
- [ ] Drop all config settings except `Minimum Turns` (default 5) and `Maximum Turns` (default 15).
- [ ] Hardcode model filename, bot name (per-officer, dynamic), max tokens (384), temperature (0.8), inference threads (4), context size (4096), loop guard (`HumanId == -1`).
- [ ] Confirm Release build produces `MaintenanceBureauPlus.dll` + all native deps + `About/` + `Resources/Personas.md` embedded.
- [ ] Deploy locally; confirm StationeersLaunchPad loads with no errors.

### Ambient chat listening

- [ ] Change `ChatPatch.Postfix` from trigger-prefix match to read-all.
- [ ] Add channel guard: only act on public / global chat.
- [ ] Loop guard: skip messages where `HumanId == -1`.
- [ ] Log every qualifying line at Debug level.

### Conversation state

- [ ] Add `ConversationState` (server-side, global): officer persona, turn count, transcript tail, active flag, persona memory reference.
- [ ] Prompt shape: global bureau preamble + officer block + turn-state line + transcript tail + player's new message + approval-tag instruction set.
- [ ] Confirm the model carries persona across 3 consecutive turns.

### Persona pool

- [ ] Embed `Personas.md` as a resource in `MaintenanceBureauPlus.csproj`.
- [ ] Mirror `Plans/MaintenanceBureauPlus/Personas.md` into `Plans/MaintenanceBureauPlus/MaintenanceBureauPlus/Resources/Personas.md`. Treat both as the same file; commit updates to both simultaneously.
- [ ] Write `PersonaRegistry`: parse the markdown at startup. Regex on `## N. Officer <Name>` headings; bullet fields parsed line-by-line.
- [ ] On cycle start: pass the 100-persona pool + persona memory super-summaries + an instruction ("pick one the group has not seen recently, or blend two") to the LLM. The LLM returns its choice and the cycle locks it.
- [ ] Record the LLM's chosen persona in `ConversationState.OfficerPersona`.

### Persona memory store

- [ ] Write `PersonaMemoryStore`: persistent JSON file at `BepInEx/plugins/MaintenanceBureauPlus/state/persona_memory.json`.
- [ ] Super-summary format: one sentence per past officer ("Officer X, Department Y: defining detail from last cycle").
- [ ] Cap at 200 entries; trim oldest when exceeded.
- [ ] Load on plugin startup; append-and-save after every cycle end.
- [ ] Confirm survival across server restarts.

### Approval tag parsing

- [ ] Parse `[CONTINUE]`, `[APPROVED]`, `[REFUSED]` (case-insensitive, exact bracketed token, not substring-in-word).
- [ ] Strip the tag from the broadcast text.
- [ ] Default to `[CONTINUE]` if no tag present; log at Info.
- [ ] Adversarial test: player types "please approve"; confirm the mod does not trigger.
- [ ] Debug hook: any chat message containing literal `[APPROVED]` fires the event in a debug build.

### Officer persona lifecycle

- [ ] Cycle start flow: fresh persona chosen, transcript empty, turn counter zero.
- [ ] During cycle: every player chat appends to transcript; LLM replies with a tag; mod acts on tag.
- [ ] Cycle end flow (approval or refusal): LLM asked for one-sentence super-summary of the persona just played; summary pushed onto `PersonaMemoryStore`; state reset.

### Approval event (new order)

- [ ] Lock state (block new replies).
- [ ] Broadcast the approval message (tag stripped).
- [ ] Full blackout: for every connected `Human`, set `OrganBrain.DamageState.Stun = 100`.
- [ ] Repair sweep per `plan.md` Section 5.
- [ ] Telemetry: corpse count, wreckage count (+ detail if <=10).
- [ ] Closing message: single-turn LLM call with telemetry + instruction to sign off AND signal the officer is leaving (retirement, lunch, reassignment, etc.). Broadcast.
- [ ] Capsule spawn per player: `OnServer.Create<LanderCapsule>`, `OnServer.MoveToSlot`, `OnServer.Interact`.
- [ ] Immediately after the teleport: one-time `Stun = 80`.
- [ ] Hands off: no further stun writes, no position writes, no timers.
- [ ] Reset `ConversationState`. Push retired persona summary onto the memory store.

### Repair sweep

- [ ] Iterate `Thing.AllThings` with the exclusion filter from `plan.md` Section 5.1.
- [ ] For the inclusion set, zero only incident channels per Section 5.3 (Brute, Burn, Toxic, Radiation).
- [ ] Keep gameplay-state channels untouched (Stun, Oxygen, Hydration, Nutrition, Stamina).
- [ ] Keep contents untouched (gas mixes, pipe / chute / tank volumes, charge, stack counts, IC program data).
- [ ] Override the vanilla "unrepairable" flag for Rover Mk I and rockets.
- [ ] Verify with InspectorPlus: pre-event and post-event snapshots show incident channels transitioning to 0 on covered things, untouched on excluded things.

### Telemetry

- [ ] Corpse count during the sweep.
- [ ] Wreckage inventory: list if count <= 10, count only if >10.
- [ ] Feed both into the closing-message prompt context.
- [ ] Confirm the closing message references the numbers.

### Closing message

- [ ] Separate single-shot LLM prompt after the sweep completes.
- [ ] System prompt for this call: officer persona + telemetry context + "deliver the in-lore post-repair explanation AND sign off in a way that indicates you personally are no longer on duty. The next request will be handled by another officer."
- [ ] Timeout fallback: if the LLM does not return in N seconds (initial guess: 10), broadcast a generic officer-retires sentence so the cycle does not stall.
- [ ] After broadcast, proceed to capsule spawn.

### About.xml + build surface

- [ ] Generate real preview art.
- [ ] Fill `<Description>` in BBCode, mirrored from README.md. Stay under 7900 characters.
- [ ] Fill `<InGameDescription>` in Unity rich text, compressed.
- [ ] Fill `<ChangeLog>` with v0.1.0 entry.
- [ ] Confirm tagline verbatim on GitHub repo description, `<Description>` opener, `<InGameDescription>` subtitle.
- [ ] Decide `PluginVersion`. v0.1.0 for test builds; 1.0.0 for first Workshop publish.

## v1.1

- [ ] Sliding-window transcript summarization for long conversations.
- [ ] Measure token count of each prompt build; add a debug log flag.
- [ ] Tune `MinTurns` / `MaxTurns` defaults against real play.
- [ ] Tune temperature and closing-message timeout against observed behavior.
- [ ] Review persona memory cap of 200; raise or lower based on how long servers actually run.

## v1.x (discretionary polish)

- [ ] Helmet-closed gate before firing approval.
- [ ] Partial battery drain as a per-approval cost.
- [ ] Admin cancel command for in-progress conversations.
- [ ] Global cooldown between approvals.
- [ ] Refusal recovery: prior refusal context threaded into the next officer's opening.
- [ ] Broken-structure census expansion (per-type counts above 10 items).
- [ ] Per-save persona memory scoping (v1 shares one memory across all saves on the server).
- [ ] InspectorPlus pre-baked request templates (`before_approval.json`, `after_approval.json`) for developer testing.

## v1.5

- [ ] Optional trigger prefix as a config knob. Default off.
- [ ] Per-player request cap per in-game day.
- [ ] Bureau "office hours" config.

## v2: terrain reclamation

See `Plans/TerrainReclamation/plan.md` for the full design.

- [ ] Resolve the six open questions in `Plans/TerrainReclamation/RESEARCH.md` Section 7.
- [ ] Build `ReclamationEvent` state machine (Scan / Warn / Execute / Complete).
- [ ] Build `RoomProtectionMap` from `Room` grid cells + margin at runtime.
- [ ] Build `VoxelResetter` (base-density read + `SetDensity` write per voxel).
- [ ] Build `EffectsController`.
- [ ] Build `TriggerSystem`.
- [ ] Wire terrain reclamation into the bureau's approval taxonomy as a separate department with its own officers.

## vNext (beyond v2)

- [ ] Additional bureau services beyond repair and terrain. Each gated behind its own conversational loop. Each preserving the opaque-until-discovered framing: the tagline does not mention specifics.

## Playtest gate

Before v1 release:

- [ ] **Native-DLL extraction on first launch** (blocks Workshop-compatible deploy). StationeersLaunchPad's recursive `*.dll` glob aborts the entire mod load on any native library in the deploy (see [../../Research/Workflows/LaunchPadNativeDllTrap.md](../../Research/Workflows/LaunchPadNativeDllTrap.md)). For the current local playtest the entire deploy lives under `BepInEx/plugins/MaintenanceBureauPlus/` to sidestep the trap, but that path does not ship to Workshop subscribers. The Workshop-compatible layout:
  - Change the `CopyNativeLibraries` MSBuild target to emit `natives/**/*.dll.bin` inside the mod's `bin/Release/` instead of `runtimes/win-x64/native/**/*.dll`. Any non-`.dll` extension works; LaunchPad's `Directory.GetFiles(..., "*.dll", SearchOption.AllDirectories)` scan ignores it entirely.
  - Preserve the existing nested directory structure (`avx/`, `avx2/`, `avx512/` subfolders of `natives/` for the instruction-set variants).
  - Ship the VC++ redist DLLs the same way: `natives/msvcp140.dll.bin`, `natives/vcruntime140.dll.bin`, `natives/vcruntime140_1.dll.bin`.
  - Add extract-on-launch code to `Plugin.Awake()` (runs on the Unity main thread before patches apply):
    1. Determine `nativeSrc = Path.Combine(Paths.PluginPath, "MaintenanceBureauPlus", "natives")`.
    2. Determine `nativeDst = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Stationeers", "MaintenanceBureauPlus", "natives")`. This path is Workshop-update stable and outside LaunchPad's scan.
    3. Walk `nativeSrc` recursively. For each `*.dll.bin`, compute the mirrored target under `nativeDst` stripping the `.bin` suffix. If the target exists with the same file length, skip; otherwise `Directory.CreateDirectory` on the parent and `File.Copy` with `overwrite: true`.
    4. P/Invoke `kernel32.SetDllDirectory(nativeDst)` so LLamaSharp's first `LoadLibrary("llama.dll")` finds the extracted natives and its transitive dependencies (`ggml.dll`, VC++ runtime).
    5. Log the extraction result: `extracted N new, skipped M unchanged`.
  - Test matrix: first-time install (all files extract), subsequent launches (zero extracts), Workshop update that ships a new GGUF / native version (new files extract, old remain).
  - Once this is in place, restore the mod-folder deploy target (`mods/MaintenanceBureauPlus/`), delete the `BepInEx/plugins/MaintenanceBureauPlus/` staging copy, and re-enable the `modconfig.xml` Local entry to test the full path before Workshop publish.
- [ ] Remove the `[DEBUG-APPROVE]` chat hook in `ChatPatch.Postfix`. It currently fires `ApprovalEvent.Start()` immediately on any chat message containing the literal token, in both Debug and Release builds. Kept unconditional during playtest so testers can trigger the event without waiting for the LLM. The hook is a 6-line block just after the loop guard; delete it, log nothing.

- [ ] **Remove the playtest log bridges in `LaunchPadLog.cs`.** During playtest the file forwards every BepInEx log entry to (1) `StationeersLaunchPad.Logger` for the `slp logs` viewer and (2) `Assets.Scripts.ConsoleWindow.Print` for the in-game F3 console. The ConsoleWindow branch in particular floods F3 with `[Watchdog #N]` and per-turn `[DIAG]` lines at a rate that makes typing other commands miserable for a Workshop subscriber. Either delete `LaunchPadLog.cs` entirely (and unwire `LaunchPadLog.Hook(Log)` and `LaunchPadLog.TryUpgradeToModLogger()` in `Plugin.cs`), or strip the ConsoleWindow branch and gate the LaunchPad branch on Warning+ only. Also drop the per-turn `[DIAG]` logging at every site in `ChatPatch.cs` and the `[Watchdog #N]` line cadence in `Plugin.StartWatchdog`.

- [ ] **Code review `ChatPatch.PickRandomUnseenPersona`.** Written fast during a playtest session to replace a 14 kB LLM persona-selection prompt (commit ff05052). Points to verify:
  - Parses officer names out of `PersonaMemoryStore` summary strings by scanning for the prefix `"Officer "` and a trailing comma, per the super-summary format defined in `SystemPrompts.SuperSummaryTemplate`. If the LLM ever emits a summary that doesn't follow that exact shape, the parser silently skips it and that officer stays in the "unseen" candidate pool. Decide: tighten the summary format contract, tighten the parser, or persist the picked persona's index alongside the summary so the parser doesn't have to regex-extract names.
  - Uses `new Random()` per call. Seeded from `Environment.TickCount` so consecutive calls within a few ms return the same first value. In practice our cycles are seconds apart so it does not bite, but prefer a static `ThreadLocal<Random>` or cached instance for hygiene.
  - Falls back to picking from `all` (including seen officers) when the unseen list is empty. That's correct after 100 cycles but worth documenting: the bureau never fully runs out of officers, it just starts recycling once every officer has been visited.
  - `PersonaMemoryStore.Summaries` is returned as `IReadOnlyList<string>` so the `foreach` is safe to iterate, but `Append` mutates the underlying list. No concurrent mutation path exists today (both reads and writes are main-thread via `MainThreadQueue`), but if anything off-main ever starts writing memory the iteration becomes unsafe. Add a lock or snapshot-then-iterate if that becomes possible.
  - `StringComparer.OrdinalIgnoreCase` for the seen-name set: correct for Latin names in the curated pool. If later personas add non-ASCII or accented characters, reconsider (but OrdinalIgnoreCase is still probably fine; culture-aware comparison would be wrong).
  - Doesn't validate that persona fields are non-null before returning; `FallbackRandomPersona()` does but `PickRandomUnseenPersona` doesn't re-check after pool lookup. If `Personas.md` parsing ever produces an entry with a null field, a downstream `NullReferenceException` will hide the root cause. Defensive null-check before returning.

## Cross-cutting

- [ ] Keep `plan.md`, `README.md`, `About.xml` in sync per the monorepo's content rule.
- [ ] Keep `Plans/MaintenanceBureauPlus/Personas.md` and `Plans/MaintenanceBureauPlus/MaintenanceBureauPlus/Resources/Personas.md` in sync. Any edit to personas touches both files.
- [ ] Review persona #11 (Vilhelm Orr), #14 (Lothar Schein), #28 (Rosamund Fickle), #31 (Barnabas Ulmen), #71 (Horatio Pinn), #74 (Emrys Dobbler), #91 (Pascal Umber-Vogel) for tone fit. Sub-agent flagged these as borderline against the "not magical, not supernatural, not too whimsical" constraint.
- [ ] Update `RESEARCH.md` (mod-local) when architecture or patch behavior changes. Game-internals go to central `Research/`, not here.
- [ ] Before every publish, verify `<Description>` under 7900 characters.
- [x] Replace reflection shims with concrete game-API calls. Decompilation against 0.2.6228.27061 confirmed `Human.AllHumans`, `human.DamageState.Damage(ChangeDamageType.Set, v, DamageUpdateType.Stun)`, `Prefab.Find<LanderCapsule>("LanderCapsule")` (string required, no parameterless overload), `OnServer` in the global namespace, `Structure.IsBroken` property, and that `ChatMessage` has no channel field. All shims removed from `ApprovalEvent.cs`, `RepairSweep.cs`, `TelemetryCollector.cs`.
- [x] Swap `Newtonsoft.Json` for `UnityEngine.JsonUtility`. `OfficerPersona` converted to `[Serializable]` public fields; `PersonaMemoryStore` uses a private `MemoryFile` carrier class because JsonUtility cannot serialize a top-level `List<T>`. Matches the `Mods/InspectorPlus` convention of avoiding Newtonsoft.
