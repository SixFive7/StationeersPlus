---
title: InspectorPlus Usage
type: Workflows
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-02
sources:
  - Mods/InspectorPlus/RESEARCH.md:21-23
  - Mods/InspectorPlus/InspectorPlus/HeadlessUnpausePatch.cs
  - Mods/InspectorPlus/InspectorPlus/SnapshotRequest.cs:46-68 (Parse regexes)
related:
  - ../Patterns/UnityFakeNull.md
tags: [unity, threading, harmony]
---

# InspectorPlus Usage

How to capture live runtime state from a running Stationeers session using the InspectorPlus plugin. Reach for this recipe whenever you need to verify a hypothesis about field or property values at a specific moment instead of adding one-off logging.

## When to use
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- Verifying that a Harmony patch actually updates the field it claims to update (paired before / after snapshots).
- Inspecting the steady-state values of a device, network, or entity after the game has reached a stable condition.
- Confirming the shape of an unfamiliar type (which fields exist, which are non-null, what the `Transform.position` looks like).

Do not use InspectorPlus as a replacement for logging inside hot Harmony patches. The request / snapshot cycle captures steady state well; short-lived transitions are better logged with `Logger.LogInfo(...)` from inside the patch.

## Setup
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

InspectorPlus is a local-only BepInEx plugin. It runs two triggers:

1. Drop a request JSON into the watched request folder to produce a programmatic snapshot. The plugin processes the file, writes a snapshot, then deletes the request.
2. Press F8 in-game to dump every MonoBehaviour in the scene to a snapshot file.

## Headless dedicated server (no client)
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

On a headless dedicated server with no client connected the simulation is paused, and on a headless build neither `MonoBehaviour.Update()` nor coroutines fire. The request pump runs off the simulation tick (`ElectricityManager.ElectricityTick`), so while the world is paused a dropped request file is never processed and no snapshot is written.

To capture snapshots in that case without a player joining, enable the opt-in setting (default off) before starting the server, either in the in-game settings panel under `Server - Headless`, or by adding to `BepInEx/config/net.inspectorplus.cfg`:

```ini
[Server - Headless]
Force Unpause Without Client = true
```

With it on, InspectorPlus keeps the simulation running on a batch-mode server with no client connected, so the tick, and therefore request processing, runs. The setting is gated on `Application.isBatchMode` and never fires on a client or single-player session. Added in InspectorPlus v1.1.0; hardened on 2026-07-02 into four cooperating pieces in `HeadlessUnpausePatch.cs`:

- The original one-shot unpause in a `GameManager.StartGame` postfix. On its own this is NOT sufficient: the dedicated-server assembly's `StartGame` ends with `DelayedStartupPause().Forget()`, a server-build-only method that re-pauses the world 5 seconds later whenever `NetworkBase.Clients.Count <= 0`, ignoring `AutoPauseServer`. Because `StartGame` is `async UniTask`, the postfix fires at the method's first await, always before the delayed pause lands. Full writeup: `../GameSystems/SimulationTickDriverHooks.md` section "Dedicated-server assembly only: DelayedStartupPause re-pauses 5 s after StartGame".
- A guarded prefix that skips `GameManager.DelayedStartupPause` entirely (`Prepare()` disables the patch class on the client build, where the method does not exist).
- A 5-second UniTask watchdog loop that logs `GameState / IsGamePaused / GameTickPaused / RunSimulation / GameTickCount / Clients` to `LogOutput.log` and re-unpauses whenever the world is `Running`, parked, and clientless (it skips while `SaveHelper.IsSaving`). This also recovers from any other silent pauser (panel pause paths, console `pause`, third-party mods).
- Pause tracers: every actual `WorldManager.SetGamePause` transition plus every `PauseGameTick` / `UnpauseGameTick` call is logged with a stack trace, so the next silent pause names its caller in `LogOutput.log` (`[PauseTrace]` lines).

When a client is connected (a normal playtest) the server is already running and the toggle is unnecessary; on a client or single-player, `Update()` drives the pump directly.

Readiness caveat when using a probe request as the "world is ticking" signal: the first ~8 ticks run between `StartGame` and where `DelayedStartupPause` would land, so with an unhardened plugin a pre-dropped probe could be consumed once even though the sim parked seconds later. With the hardened plugin, confirm sustained ticking via the repeating `[TickWatchdog]` lines (rising `GameTickCount`) rather than a single consumed probe.

## Request shape
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

Requests are JSON with five fields. The parser (`SnapshotRequest.Parse`, `Mods/InspectorPlus/InspectorPlus/SnapshotRequest.cs:46-68`) is a hand-rolled set of `Regex.Match` calls with NO `IgnoreCase` option, so the JSON keys are case-sensitive and must be spelled exactly in lowercase camelCase. A key with any other casing (`Types`, `MaxDepth`, `IncludePrivate`, ...) is silently ignored and its field keeps the default, so a fully capitalized request degrades to an unfiltered full-scene dump without any error.

The exact key spellings:

- `"types"`: type-name filter (JSON string array). Narrows the walk to instances of the listed types. Default: empty = all known game types.
- `"fields"`: per-type field / property filter (JSON string array). Narrows the emitted members to the listed names. Default: empty = all public fields/properties.
- `"maxDepth"`: recursion cap (integer, default 3). Controls how deep the reflection walk descends through nested object graphs.
- `"includePrivate"`: when `true`, also walk non-public fields and properties (boolean, default `false`, public members only).
- `"maxMonoBehaviours"`: cap on how many top-level objects a snapshot serializes (integer, default 10000).

Minimal example request:

```json
{
  "types": ["Assets.Scripts.Objects.Electrical.PowerTransmitter"],
  "fields": ["_linkedReceiver", "_linkedReceiverDistance", "_powerProvided"],
  "maxDepth": 2,
  "includePrivate": true,
  "maxMonoBehaviours": 100
}
```

Narrow `types` and `fields` precisely. A full-scene dump is cheap to produce but noisy to read; targeted requests are the default.

## Interpreting output
<!-- verified: 0.2.6228.27061 @ 2026-05-21 -->

Top level is an object with `timestamp`, `frame`, `gameTime`, and an `objects` array. Each entry carries `_type`, `_name`, and, for components, `_gameObject`, `_active`, and `_position`, plus a `fields` object of the sampled fields and properties. Unity `Vector3` values and `Transform` positions are emitted as `[x, y, z]` arrays. A snapshot that hits a size cap gets a `_truncated` marker.

Use this structure directly:

- To confirm "did field X update," diff a baseline snapshot against a post-action snapshot.
- To confirm "the partner reference is non-null," find the entry by `_type` and read the target member under `fields`.
- To confirm "there are N instances of this type live in the scene," count the `objects` entries with that `_type`.

## Common requests
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Prepare request files in advance of any in-game test. The moment a test instruction is handed to the developer, the corresponding request files should already exist on disk named for the checkpoints they capture (for example, `before_link.json`, `after_link.json`, `steady_state.json`). The developer drops each file into the request folder at the right moment rather than typing JSON mid-test.

Prefer paired before + after requests over a single after-only snapshot when the question is "did field X change". A single snapshot cannot prove a delta.

## Pitfalls
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- `FileSystemWatcher.Created` fires on a thread-pool thread, not the Unity main thread. Any Unity API call from the watcher callback crashes. InspectorPlus routes work through `MainThreadDispatcher` for this reason; a request that hits Unity APIs from a non-main thread is a bug in the plugin, not a bug in the request.
- Unity fake-null: `obj == null` returns `true` even when the managed wrapper is still alive. InspectorPlus checks `UnityEngine.Object`-derived values via `!obj` before dereferencing.
- `FileSystemWatcher.Created` can fire while the writer still holds the file open. InspectorPlus opens the request file with `FileShare.ReadWrite` and retries for a short window on `IOException`.

## Cleanup
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Snapshots are evidence, not durable artifacts. After a session, delete the snapshot files read during debugging and any stray request files still sitting in the request folder. Snapshots have timestamped filenames with no automatic rotation. Stray request files are worse: if the plugin failed to process one, it will be picked up the next time the game launches.

Central pages cite the request pattern, not a snapshot file path. Another developer reading the page months later cannot open a snapshot that was deleted at the end of the original session; the request pattern is reproducible. Write citations in the form:

```
Verified via InspectorPlus on 2026-04-20 in game version 0.2.6228.27061. Request: types=[PowerTransmitter], fields=[_linkedReceiver, _linkedReceiverDistance, _powerProvided].
```

## Verification history
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0003 and the surrounding sections of `Mods/InspectorPlus/RESEARCH.md`.
- 2026-05-21: corrected the request-field list (added `IncludePrivate`, `MaxMonoBehaviours`) and the output-format description (an `objects` array with `[x, y, z]` values, not an object keyed by type name with `(x, y, z)`) to match the shipped `ObjectWalker`; added the Headless dedicated server section for the InspectorPlus v1.1.0 `Force Unpause Without Client` toggle. Verified via InspectorPlus on 2026-05-21 in game version 0.2.6228.27061. Request: maxMonoBehaviours=25, maxDepth=2 on a fresh dedicated-server world.
- 2026-07-02: rewrote the "Request shape" section to give the exact JSON key spellings. The previous revision listed the fields by their C# property names (`Types`, `Fields`, `MaxDepth`, `IncludePrivate`, `MaxMonoBehaviours`), but `SnapshotRequest.Parse` (`Mods/InspectorPlus/InspectorPlus/SnapshotRequest.cs:46-68`) matches `"types"`, `"fields"`, `"maxDepth"`, `"includePrivate"`, `"maxMonoBehaviours"` via `Regex` with no `IgnoreCase`, so capitalized keys are silently ignored and the request degrades to a full-scene dump. Added the one-line degradation warning and a minimal example request JSON. Source is the mod's own shipped code (game-version-independent); section restamped at the current game version 0.2.6403.27689 per convention.
- 2026-07-02 (later): updated "Headless dedicated server (no client)" for the hardened force-unpause. The 2026-05-21 revision's one-shot description matched the plugin then, but the one-shot is defeated at 0.2.6403.27689 by the server-assembly-only `GameManager.DelayedStartupPause` (5-second delayed `SetGamePause(true)` when clientless); an earlier note in `DedicatedServer/CLAUDE.md` had recorded the resulting flakiness empirically at 0.2.6228.27061 without a cause. Documented the cause, the DelayedStartupPause skip patch, the 5-second watchdog, and the `[PauseTrace]` stack tracers, all live-verified on a fresh `-new Lunar` dedicated-server boot on 2026-07-02 at 0.2.6403.27689 (three-boot evidence run: unhardened = exactly 8 ticks then parked; hardened = continuous ticking, ScenarioRunner 10-tick scenario fired, probes consumed).

## Open questions

None at creation.
