---
title: InspectorPlus Usage
type: Workflows
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-21
sources:
  - Mods/InspectorPlus/RESEARCH.md:21-23
  - Mods/InspectorPlus/InspectorPlus/HeadlessUnpausePatch.cs
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
<!-- verified: 0.2.6228.27061 @ 2026-05-21 -->

On a headless dedicated server with no client connected the simulation is paused, and on a headless build neither `MonoBehaviour.Update()` nor coroutines fire. The request pump runs off the simulation tick (`ElectricityManager.ElectricityTick`), so while the world is paused a dropped request file is never processed and no snapshot is written.

To capture snapshots in that case without a player joining, enable the opt-in setting (default off) before starting the server, either in the in-game settings panel under `Server - Headless`, or by adding to `BepInEx/config/net.inspectorplus.cfg`:

```ini
[Server - Headless]
Force Unpause Without Client = true
```

With it on, InspectorPlus unpauses the simulation after `StartGame` on a batch-mode server, so the tick, and therefore request processing, runs. The setting is gated on `Application.isBatchMode` and never fires on a client or single-player session. Added in InspectorPlus v1.1.0.

When a client is connected (a normal playtest) the server is already running and the toggle is unnecessary; on a client or single-player, `Update()` drives the pump directly.

## Request shape
<!-- verified: 0.2.6228.27061 @ 2026-05-21 -->

Requests are JSON with five fields documented in `SnapshotRequest.cs`:

- `Types`: type-name filter. Narrows the walk to instances of the listed types.
- `Fields`: per-type field / property filter. Narrows the emitted members to the listed names.
- `MaxDepth`: recursion cap (default 3). Controls how deep the reflection walk descends through nested object graphs.
- `IncludePrivate`: when true, also walk non-public fields and properties (default false, public members only).
- `MaxMonoBehaviours`: cap on how many top-level objects a snapshot serializes (default 10000).

Narrow `Types` and `Fields` precisely. A full-scene dump is cheap to produce but noisy to read; targeted requests are the default.

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
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0003 and the surrounding sections of `Mods/InspectorPlus/RESEARCH.md`.
- 2026-05-21: corrected the request-field list (added `IncludePrivate`, `MaxMonoBehaviours`) and the output-format description (an `objects` array with `[x, y, z]` values, not an object keyed by type name with `(x, y, z)`) to match the shipped `ObjectWalker`; added the Headless dedicated server section for the InspectorPlus v1.1.0 `Force Unpause Without Client` toggle. Verified via InspectorPlus on 2026-05-21 in game version 0.2.6228.27061. Request: maxMonoBehaviours=25, maxDepth=2 on a fresh dedicated-server world.

## Open questions

None at creation.
