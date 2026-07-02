# InspectorPlus

InspectorPlus is a local-only BepInEx plugin that dumps live game state to JSON on demand while Stationeers is running. It exists as debugging infrastructure for every other mod in this repository: drop a filtered request JSON into a watched folder, and the plugin writes a snapshot of the matching scene objects to disk. Because the plugin is consulted constantly by other mods' development workflows, the usage guide lives centrally; this page documents what the plugin itself is made of and why.

## Architecture

Six cooperating pieces:

- `Plugin.cs`: BepInEx entry point. Starts a `FileSystemWatcher` on `BepInEx/inspector/requests/` and registers a key-press handler for F8. On request-file creation, parses the JSON and schedules a snapshot on the main thread.
- `SnapshotRequest.cs`: the JSON request schema. Deserialised from request files dropped into the watched folder. Fields: `Types` (type-name filter), `Fields` (per-type field/property filter), `MaxDepth` (recursion cap), `IncludePrivate` (include non-public members), `MaxMonoBehaviours` (top-level object cap). Parsed by a minimal hand-rolled parser, not Newtonsoft; see Design decisions.
- `ObjectWalker.cs`: the reflection-based traversal that produces the snapshot. Walks fields, properties, and Unity `Transform` positions; respects a configurable recursion depth; detects cycles to avoid infinite walks; enforces byte and nested-expansion caps and marks the result truncated when one is hit. Uses reflection against each candidate object and emits a nested JSON blob.
- `MainThreadDispatcher.cs`: queues work produced off the main thread (file-watcher callbacks fire on a thread-pool thread) onto the Unity main thread, which is the only thread from which scene queries are safe. Implementation is a `Queue<Action>` guarded by a `lock`, drained from `Update()` on a persistent `GameObject`.
- `RequestPollOnTickPatch.cs`: a Harmony postfix on `ElectricityManager.ElectricityTick` that pumps the request-file scan from the simulation tick. A headless dedicated server does not reliably drive `Update()` or coroutines, so this keeps request snapshots working server-side; on a client it is redundant with the `Update()` poll and the `FileSystemWatcher`.
- `HeadlessUnpausePatch.cs`: the opt-in headless keep-alive (the `Force Unpause Without Client` setting, default off; every piece additionally gated on `Application.isBatchMode`). Four cooperating pieces: (1) the original one-shot unpause postfix on `GameManager.StartGame`; (2) a guarded prefix that skips the dedicated-server-assembly-only `GameManager.DelayedStartupPause`, which would otherwise silently re-pause the world 5 seconds after StartGame whenever no client is connected and which defeats the one-shot (StartGame is async, so the postfix fires at its first await); (3) a 5-second UniTask watchdog loop that logs tick state and re-unpauses whenever the world is Running, parked, and clientless (skips while a save is in progress); (4) `[PauseTrace]` stack-trace tracers on `WorldManager.SetGamePause` transitions and `PauseGameTick` / `UnpauseGameTick`, so any future silent pauser names its caller in `LogOutput.log`. See `../../Research/GameSystems/SimulationTickDriverHooks.md` ("DelayedStartupPause" section) for the game-side mechanism.

### Snapshot output format

Snapshots land in `BepInEx/inspector/snapshots/` as `snapshot_<yyyyMMdd_HHmmss_fff>.json`. The top level is an object with `timestamp`, `frame`, `gameTime`, and an `objects` array; each entry carries `_type`, `_name`, and, for components, `_gameObject`, `_active`, and `_position`, plus a `fields` object of sampled fields and properties. Unity `Vector3` values and `Transform` positions are emitted as `[x, y, z]` arrays. A snapshot that hits a size cap gets a `_truncated` marker. The usage recipe (request lifecycle, F8 dump, file cleanup) is covered centrally; see [../../Research/Workflows/InspectorPlusUsage.md](../../Research/Workflows/InspectorPlusUsage.md).

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

InspectorPlus installs six Harmony patch classes:

- `RequestPollOnTickPatch`, a postfix on `ElectricityManager.ElectricityTick`, pumps the request-file scan on a headless dedicated server where `MonoBehaviour.Update()` and coroutines do not fire reliably. It scans and processes request files and never mutates game state.
- `HeadlessUnpausePatch`, a postfix on `GameManager.StartGame`, is opt-in (the `Force Unpause Without Client` setting, default off) and only acts under `Application.isBatchMode`. When both hold it unpauses the simulation so the tick, and therefore request processing, runs with no client connected. It also starts the `HeadlessTickWatchdog` loop (not a patch; a UniTask delay loop on the player loop, which keeps running headless even while the tick is parked).
- `DelayedStartupPauseSkipPatch`, a prefix on `GameManager.DelayedStartupPause`, same double gate. The target method exists only in the dedicated-server assembly (it re-pauses the world 5 seconds after StartGame when no client is connected, ignoring `AutoPauseServer`); `Prepare()` disables the patch class cleanly on the client build so `PatchAll` cannot fail there. Skipping the stub of an `async UniTaskVoid` is safe: the caller's `.Forget()` on the default struct is a no-op.
- `SetGamePauseTracePatch`, a prefix on `WorldManager.SetGamePause`, same double gate. Logs every actual pause-state transition with a stack trace (`[PauseTrace]` lines); `SetGamePause` is otherwise completely silent, which is what made the DelayedStartupPause re-pause invisible.
- `PauseGameTickTracePatch` / `UnpauseGameTickTracePatch`, prefixes on `GameManager.PauseGameTick` / `UnpauseGameTick`, same double gate, same stack-trace logging for the tick-level pause latch.

The unpause pieces are the one place the plugin changes game state, and only on an explicitly opted-in headless server; they never fire on a client or single-player. State capture itself is read-only reflection in `ObjectWalker`.

## Relevant central pages

- [../../Research/Patterns/FileSystemWatcherMainThread.md](../../Research/Patterns/FileSystemWatcherMainThread.md) - `FileSystemWatcher.Created` fires on a thread-pool thread and can fire while the writer still holds the file open; the plugin's main-thread bridge and `FileShare.ReadWrite` retry loop both come from this rule.
- [../../Research/Patterns/MainThreadDispatcher.md](../../Research/Patterns/MainThreadDispatcher.md) - The main-thread `Action` queue drain pattern behind `MainThreadDispatcher.cs`.
- [../../Research/Patterns/UnityFakeNull.md](../../Research/Patterns/UnityFakeNull.md) - `obj == null` returns true even when the managed wrapper is still alive; `ObjectWalker` uses the `!obj` check before dereferencing Unity-derived fields.
- [../../Research/Workflows/InspectorPlusUsage.md](../../Research/Workflows/InspectorPlusUsage.md) - How other mods' development workflows use this plugin: request schema, snapshot lifecycle, F8 dump, cleanup rules.
- [../../Research/Workflows/ModProjectSetup.md](../../Research/Workflows/ModProjectSetup.md) - BepInEx plugin scaffold this mod is built on.

## Pitfalls / dead ends

None recorded. The three original pitfall notes (FileSystemWatcher thread boundary, Unity fake-null via reflection, file-share race on freshly created request files) were generalizable rules that apply to any mod doing the same kind of work, so they were lifted to the central Patterns pages listed above.
