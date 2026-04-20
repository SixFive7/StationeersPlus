---
title: InspectorPlus Usage
type: Workflows
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/InspectorPlus/RESEARCH.md:21-23
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

## Request shape
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Requests are JSON with three fields documented in `SnapshotRequest.cs`:

- `Types`: type-name filter. Narrows the walk to instances of the listed types.
- `Fields`: per-type field / property filter. Narrows the emitted members to the listed names.
- `MaxDepth`: recursion cap. Controls how deep the reflection walk descends through nested object graphs.

Narrow `Types` and `Fields` precisely. A full-scene dump is cheap to produce but noisy to read; targeted requests are the default.

## Interpreting output
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Top level is an object keyed by type name; each value is a list of instances with their sampled fields / properties. Unity `Transform` and `Vector3` values are stringified as `(x, y, z)` for readability.

Use this structure directly:

- To confirm "did field X update," diff a baseline snapshot against a post-action snapshot.
- To confirm "the partner reference is non-null," look up the type key and read the target field on the listed instances.
- To confirm "there are N instances of this type live in the scene," count entries in the type's list.

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

## Open questions

None at creation.
