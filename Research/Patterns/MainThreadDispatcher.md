---
title: MainThreadDispatcher
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-28
sources:
  - Mods/PowerTransmitterPlus/PowerTransmitterPlus/MainThreadDispatcher.cs:7-15 (F0308)
  - Plans/LLM/LLM/Plugin.cs:67-68 (F0350)
  - Mods/PowerTransmitterPlus/RESEARCH.md:43-49 (F0032, underlying cause)
related:
  - ../GameSystems/PowerTickThreading.md
  - ./FileSystemWatcherMainThread.md
  - ./UnityFakeNull.md
tags: [threading, unity]
---

# MainThreadDispatcher

`ConcurrentQueue<Action>` drained in `Update()` on a `DontDestroyOnLoad` MonoBehaviour. Required whenever a callback can fire off the Unity main thread and the callback needs to touch a Unity API. Appears verbatim as a helper class in multiple mods in this repo.

## Problem
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Unity's P/Invoke-backed APIs are not thread-safe and hard-crash the native player when called from any thread other than the Unity main thread. Stationeers drives several callback paths off the main thread:

- `PowerTick.ApplyState` runs on a UniTask ThreadPool worker. Any postfix on `ReceivePower` / `UsePower` / `GetGeneratedPower` / `GetUsedPower` / `VisualizerIntensity` setter inherits that thread. See `../GameSystems/PowerTickThreading.md`.
- `FileSystemWatcher` events fire on a .NET thread-pool thread. See `./FileSystemWatcherMainThread.md`.
- `IAsyncEnumerable` / background-thread inference (LLM) produces callbacks off the main thread.

F0308 (code comment, `MainThreadDispatcher.cs:7-15`):

```text
    // Stationeers drives power-tick code (PowerTick.ApplyState -> ReceivePower ->
    // VisualizerIntensity setter) on a ThreadPool worker via UniTask's
    // SwitchToThreadPoolAwaitable. Our Harmony postfixes inherit that thread,
    // so any call to a Unity API (new GameObject, Shader.Find, Transform.position,
    // LineRenderer.SetPosition) hard-crashes the native Unity player.
    //
    // This dispatcher parks a queue on a DontDestroyOnLoad GameObject, drained
    // in Update() on the main thread. Patches enqueue closures from any thread,
    // the closure body runs safely on the main thread one frame later.
```

## Solution / recipe
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

A `MonoBehaviour` on a `DontDestroyOnLoad` GameObject owns a `ConcurrentQueue<Action>` and drains it from its own `Update()`. Other code enqueues `Action` closures from any thread; the closure body executes on the main thread approximately one frame later.

Minimal shape (from F0032):

> `MainThreadDispatcher` is a `MonoBehaviour` on a `DontDestroyOnLoad` GameObject. It maintains a `ConcurrentQueue<Action>` drained in `Update()`. Every Harmony postfix that touches Unity API enqueues onto this dispatcher. Closure runs on main thread one frame later. ~1 frame latency, fully safe.

Key invariants:

- `DontDestroyOnLoad` keeps the dispatcher alive across scene loads (the main menu unloads the world scene, but the dispatcher must still drain queued actions).
- The queue MUST be `ConcurrentQueue<T>` (or another lock-free structure). Plain `Queue<T>` races with enqueues from other threads.
- `Update()` can drain a fixed batch per frame or the entire queue; draining everything is safe if producers are bounded.
- Field reads/writes (managed memory, no Unity P/Invoke) are safe from background threads. Only the Unity API calls need to be dispatched.

### Capture config values on main thread before dispatch

F0350 (code comment, `Plans/LLM/LLM/Plugin.cs:67-68`):

```text
            // Capture config values now (main thread) so the background thread
            // doesn't touch BepInEx ConfigEntry from a non-Unity thread.
```

When a background worker needs values from a Unity-main-thread-owned source (BepInEx `ConfigEntry`, Unity components, scene objects), capture them into plain locals on the main thread before starting the background work. Dispatching the capture back through `MainThreadDispatcher` defeats the purpose if the background worker is waiting on the value.

## Cited verifications
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- F0032 (Mods/PowerTransmitterPlus/RESEARCH.md:43-49): crash mechanism (UniTask ThreadPool worker, hard native crash on Unity API from non-main thread) and the field-reads-are-safe caveat. Full content on `../GameSystems/PowerTickThreading.md`.
- F0308 (MainThreadDispatcher.cs class header): implementation recipe (`ConcurrentQueue<Action>` + `DontDestroyOnLoad` + drain in `Update`).
- F0350 (LLM Plugin.cs: main-thread capture): pair the dispatcher with up-front value capture for background workers that consume main-thread state.

## The game's UnityMainThreadDispatcher, and why mods roll their own
<!-- verified: 0.2.6228.27061 @ 2026-05-28 -->

Stationeers ships its own main-thread dispatcher, `UnityMainThreadDispatcher : ManagerBase` (decompile line 219184). It is usable in principle, but three properties make a self-owned mod-local dispatcher the safer default for a BepInEx mod.

1. `Instance()` throws when the dispatcher object is absent:

```csharp
public static UnityMainThreadDispatcher Instance()
{
    if (!Exists())
        throw new System.Exception("UnityMainThreadDispatcher could not find the UnityMainThreadDispatcher object. Please ensure you have added the MainThreadExecutor Prefab to your scene.");
    return _instance;
}
```

`_instance` is assigned in `ManagerAwake` (the `ManagerBase` lifecycle), so the dispatcher exists only once the `MainThreadExecutor` manager has spawned in the current scene. A plugin that enqueues from early load, the main menu, or any scene where the manager is not present gets an exception, not a no-op. A mod-local dispatcher created in `Plugin.Awake` is available the moment the plugin is alive.

2. `Enqueue(Action)` does not run the action directly. It wraps it in a coroutine started during `ManagerUpdate`:

```csharp
public void Enqueue(Action action) => Enqueue(ActionWrapper(action));
private IEnumerator ActionWrapper(Action a) { a(); yield return null; }
// ManagerUpdate drains ExecutionQueue under: if (action.Target != null) action();
```

Two consequences: the action runs via `StartCoroutine` (extra indirection; runs only while the dispatcher MonoBehaviour is active and `ManagerUpdate` pumps), and the `if (action.Target != null)` guard silently DROPS any queued delegate whose `Target` is null. In practice the queued item is a closure (non-null `Target`) so it runs, but a target-less delegate vanishes without error.

3. The execution queue is shared with the engine's own usage (it also carries `ChunkThread` tasks via a second `TaskQueue`), and its pump is tied to the game's manager-update loop rather than the mod's control.

Trade-off summary:

| | Game `UnityMainThreadDispatcher` | Mod-local dispatcher |
|---|---|---|
| Availability | Throws if the MainThreadExecutor manager is not in the scene; must guard with `Exists()` and have a fallback | Guaranteed from `Plugin.Awake`; defensive no-op before Init |
| Drain | `StartCoroutine(ActionWrapper)`; `Target != null` guard drops target-less delegates | Direct `action()` in `Update()`; no Target gotcha |
| Coupling | Game manager-update loop + scene/prefab presence | Mod-owned lifecycle, `DontDestroyOnLoad` |
| Queue | Shared with engine (also `ChunkThread` tasks) | Isolated to the mod |
| Cost | No extra GameObject | One extra GameObject + Update tick (negligible); ~30 lines duplicated per mod |

Net: the game's dispatcher is usable if you guard `UnityMainThreadDispatcher.Exists()` and accept the coroutine / Target semantics, but the mod-local pattern above is what every dispatching mod in this repo uses, because it removes the throw-if-absent failure mode and the Target-drop gotcha for ~30 lines. When more than one mod needs it, consider promoting the helper to `Patterns/` shared code rather than copying it per mod.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-05-28 -->

- 2026-04-20: page created from the Research migration; implementation verbatim from F0308, with underlying-cause detail from F0032 and the main-thread-capture addendum from F0350.
- 2026-05-28: added "The game's UnityMainThreadDispatcher, and why mods roll their own". Read `UnityMainThreadDispatcher : ManagerBase` (decompile line 219184): `Instance()` throws when the MainThreadExecutor manager is absent; `Enqueue(Action)` wraps in an `ActionWrapper` coroutine and drains under an `if (action.Target != null)` guard that drops target-less delegates; the execution queue is shared with engine `ChunkThread` tasks. Documents the game-vs-mod-local trade-off. Additive (the page previously covered only the mod-local recipe); no existing claim contradicted, so no fresh validator.

## Open questions

None at creation.
