# Inspector Plus Research

Durable, project-scoped internals for the runtime-state inspection plugin.

## Architecture

Three cooperating pieces:

- `Plugin.cs`: BepInEx entry point. Starts a `FileSystemWatcher` on `BepInEx/inspector/requests/` and registers a key-press handler for F8.
- `SnapshotRequest.cs`: the JSON request schema. Deserialised from request files dropped into the watched folder.
- `ObjectWalker.cs`: the reflection-based traversal that produces the snapshot. Walks fields, properties, and Unity `Transform` positions; respects a configurable recursion depth; detects cycles to avoid infinite walks.
- `MainThreadDispatcher.cs`: queues work produced off the main thread (file-watcher callbacks fire on a thread-pool thread) onto the Unity main thread, which is the only thread from which scene queries are safe.

## File walkthrough

- `Plugin.cs`: wires up the watcher and the F8 handler. On request-file creation, parses the JSON and schedules a snapshot on the main thread.
- `SnapshotRequest.cs`: `Types` (type-name filter), `Fields` (per-type field/property filter), `MaxDepth` (recursion cap).
- `ObjectWalker.cs`: the serialiser. Uses reflection against each candidate object and emits a nested JSON blob.
- `MainThreadDispatcher.cs`: simple `ConcurrentQueue<Action>` pumped from `Update()` on a persistent `GameObject`.

## Output format

Snapshots land in `BepInEx/inspector/snapshots/` as `snapshot_<yyyyMMdd_HHmmss_fff>.json`. Top level is an object keyed by type name; each value is a list of instances with their sampled fields/properties. Unity `Transform` and `Vector3` values are stringified as `(x, y, z)` for readability.

## Pitfalls

- The `FileSystemWatcher` fires on a thread-pool thread, not the Unity main thread. Any Unity API call from the watcher callback crashes. `MainThreadDispatcher` exists for exactly this reason.
- Reflection against Unity fake-null is a known trap (`obj == null` returns `true` even when the managed wrapper is still alive). The walker checks `UnityEngine.Object`-derived values via `!obj` before dereferencing.
- `FileSystemWatcher.Created` can fire while the writer still holds the file open. The plugin opens the request file with `FileShare.ReadWrite` and retries for a short window on `IOException`.

## Design decisions

- **Request JSON instead of a chat command**: requests are machine-written by tooling that is diffing snapshots. A chat command would require the human to retype each request by hand, and would limit the request size to a single chat line.
- **Fire-and-forget (delete request after processing)** instead of a long-lived request folder: each snapshot is a single, traceable event. No stale requests linger across sessions.
- **Per-type filter**: a full-scene dump is cheap to produce but noisy to read. Most debugging sessions want a specific device class or a specific prefab instance, not the whole scene graph.
