# InspectorPlus

InspectorPlus is a local-only BepInEx plugin that dumps live game state to JSON on demand while Stationeers is running. It exists as debugging infrastructure for every other mod in this repository: drop a filtered request JSON into a watched folder, and the plugin writes a snapshot of the matching scene objects to disk. Because the plugin is consulted constantly by other mods' development workflows, the usage guide lives centrally; this page documents what the plugin itself is made of and why.

## Architecture

Four cooperating pieces:

- `Plugin.cs`: BepInEx entry point. Starts a `FileSystemWatcher` on `BepInEx/inspector/requests/` and registers a key-press handler for F8. On request-file creation, parses the JSON and schedules a snapshot on the main thread.
- `SnapshotRequest.cs`: the JSON request schema. Deserialised from request files dropped into the watched folder. Fields: `Types` (type-name filter), `Fields` (per-type field/property filter), `MaxDepth` (recursion cap). Parsed by a minimal hand-rolled parser, not Newtonsoft; see Design decisions.
- `ObjectWalker.cs`: the reflection-based traversal that produces the snapshot. Walks fields, properties, and Unity `Transform` positions; respects a configurable recursion depth; detects cycles to avoid infinite walks. Uses reflection against each candidate object and emits a nested JSON blob.
- `MainThreadDispatcher.cs`: queues work produced off the main thread (file-watcher callbacks fire on a thread-pool thread) onto the Unity main thread, which is the only thread from which scene queries are safe. Implementation is a simple `ConcurrentQueue<Action>` pumped from `Update()` on a persistent `GameObject`.

### Snapshot output format

Snapshots land in `BepInEx/inspector/snapshots/` as `snapshot_<yyyyMMdd_HHmmss_fff>.json`. Top level is an object keyed by type name; each value is a list of instances with their sampled fields/properties. Unity `Transform` and `Vector3` values are stringified as `(x, y, z)` for readability. The usage recipe (request lifecycle, F8 dump, file cleanup) is covered centrally; see [../../Research/Workflows/InspectorPlusUsage.md](../../Research/Workflows/InspectorPlusUsage.md).

### ObjectWalker filtering rules

Two rules the walker enforces to keep output sane:

- **Do not recurse into Unity objects.** When a field's value derives from `UnityEngine.Object`, emit only its name and type rather than recursing into its internals. Recursing into Unity-owned graphs produces multi-megabyte output and often loops.
- **Skip a hard-coded list of Unity internal members** that either produce noise or trigger infinite loops: `runInEditMode`, `useGUILayout`, `hideFlags`, `tag`, `rigidbody`, `rigidbody2D`, `camera`, `light`, `animation`, `constantForce`, `renderer`, `audio`, `networkView`, `collider`, `collider2D`, `hingeJoint`, `particleSystem`.

## Design decisions

- **Request JSON instead of a chat command.** Requests are machine-written by tooling that is diffing snapshots. A chat command would require the human to retype each request by hand and would limit the request size to a single chat line.
- **Fire-and-forget (delete request after processing)** instead of a long-lived request folder. Each snapshot is a single, traceable event. No stale requests linger across sessions.
- **Per-type filter.** A full-scene dump is cheap to produce but noisy to read. Most debugging sessions want a specific device class or a specific prefab instance, not the whole scene graph.
- **Hand-rolled minimal JSON parser in `SnapshotRequest.cs`.** The plugin cannot depend on Newtonsoft.Json being available in the game's assembly load context, and the request schema is small enough (three top-level fields) that a hand-rolled parser is cheaper than dragging in a dependency.

## Harmony patches catalog

InspectorPlus installs no Harmony patches. The plugin is a pure BepInEx plugin that runs alongside the game and never rewrites game methods.

## Relevant central pages

- [../../Research/Patterns/FileSystemWatcherMainThread.md](../../Research/Patterns/FileSystemWatcherMainThread.md) - `FileSystemWatcher.Created` fires on a thread-pool thread and can fire while the writer still holds the file open; the plugin's main-thread bridge and `FileShare.ReadWrite` retry loop both come from this rule.
- [../../Research/Patterns/MainThreadDispatcher.md](../../Research/Patterns/MainThreadDispatcher.md) - The `ConcurrentQueue<Action>` drain pattern behind `MainThreadDispatcher.cs`.
- [../../Research/Patterns/UnityFakeNull.md](../../Research/Patterns/UnityFakeNull.md) - `obj == null` returns true even when the managed wrapper is still alive; `ObjectWalker` uses the `!obj` check before dereferencing Unity-derived fields.
- [../../Research/Workflows/InspectorPlusUsage.md](../../Research/Workflows/InspectorPlusUsage.md) - How other mods' development workflows use this plugin: request schema, snapshot lifecycle, F8 dump, cleanup rules.
- [../../Research/Workflows/ModProjectSetup.md](../../Research/Workflows/ModProjectSetup.md) - BepInEx plugin scaffold this mod is built on.

## Pitfalls / dead ends

None recorded. The three original pitfall notes (FileSystemWatcher thread boundary, Unity fake-null via reflection, file-share race on freshly created request files) were generalizable rules that apply to any mod doing the same kind of work, so they were lifted to the central Patterns pages listed above.
