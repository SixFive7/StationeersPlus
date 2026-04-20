---
title: MainThreadDispatcher
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
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

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; implementation verbatim from F0308, with underlying-cause detail from F0032 and the main-thread-capture addendum from F0350.

## Open questions

None at creation.
