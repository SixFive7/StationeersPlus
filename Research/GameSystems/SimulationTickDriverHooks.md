---
title: Simulation tick driver hooks
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-26
sources:
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: line 254905 (ElectricityManager.ElectricityTick), 417811 (AtmosphericsManager : ThreadedManager), 187543 (GameManager.RecordGameTick), 189381 (GameManager.StartGameTick), 189076 (GameManager.GameTickPaused)
related:
  - ../GameClasses/PowerTick.md
  - ../Patterns/ThingEnumerationOffMainThread.md
tags: [power, threading, harmony]
---

# Simulation tick driver hooks

How to drive a diagnostic plugin from the game's per-tick simulation chain. Background for `DedicatedServer/dev-plugins/ScenarioRunner/` and `Mods/InspectorPlus/`.

## The chain
<!-- verified: 0.2.6228.27061 @ 2026-05-26 -->

```
GameManager.GameTick (async UniTask, switches to ThreadPool)
  -> AtmosphericsManager subsystem tick (cache + solver)
  -> ElectricityManager.ElectricityTick (static, public, line 254905)
  -> ... other subsystem ticks
```

`GameManager.GameTick` is the top-level driver but its body is an `async UniTask` state machine that switches to a ThreadPool worker; patching its `MoveNext` directly is awkward and the postfix runs at task completion which is too late. Each ThreadedManager subsystem exposes a public static `*Tick` method that the GameTick drives:

- `ElectricityManager.ElectricityTick()` (decompile line 254905). Static, public, signature `public static void ElectricityTick()`. Body guards on `GameManager.RunSimulation` and `try`/`catch`es exceptions. Walks `CableNetwork.AllCableNetworks` and `AllPoweredThings`.
- `AtmosphericsManager` extends `ThreadedManager` (decompile line 417811); the per-tick driver in that class drives atmospheric solver passes. The class exposes management methods (`Register`, `Deregister`, `HandleMainThreadRegistrations`, `CleanUpAllAtmospheresList`, `RunCacheAtmosphereDataJobs`) but the actual per-tick entry method is inherited from `ThreadedManager` and named per the manager's conventions. Use `ElectricityTick` as the primary diagnostic pump; reach for the atmospheric tick only when a scenario specifically needs to observe between atmospheric solver passes.

`GameManager.GameTickPaused` (line 189076) is the `static bool` that gates whether GameTick runs at all. `StartGameTick` / `StopGameTick` / `PauseGameTick` / `UnpauseGameTick` (lines 189381, 189374, 189388, 189396) toggle it. `RecordGameTick` (line 187543) is the per-tick counter increment.

## Why hook ElectricityTick for diagnostic plugins
<!-- verified: 0.2.6228.27061 @ 2026-05-26 -->

On a headless dedicated server:

- `MonoBehaviour.Update` does not reliably fire after world load. An Update-based poll (the natural Unity choice on a client) goes silent.
- `MainThreadDispatcher` patterns based on a DontDestroyOnLoad MonoBehaviour have the same problem; the dispatcher's PollLoop coroutine never advances past its first yield.
- A `FileSystemWatcher` callback fires on a ThreadPool thread, so any Unity API call from it crashes. Routing through the dispatcher only helps if the dispatcher is alive.
- The GameTick-driven subsystem Tick methods, in contrast, fire on every simulation cycle whenever `RunSimulation` is true. A Harmony postfix on `ElectricityManager.ElectricityTick` is the simplest reliable pump.

`Mods/InspectorPlus/InspectorPlus/RequestPollOnTickPatch.cs` already uses this pattern for its request poller; `DedicatedServer/dev-plugins/ScenarioRunner/ScenarioRunner/SimTickPump.cs` follows the same convention so the two cohabit cleanly.

## Threading constraint on the postfix
<!-- verified: 0.2.6228.27061 @ 2026-05-26 -->

The postfix body runs on the same thread as the patched method. ElectricityTick is called from `GameManager.GameTick`'s `await` continuation, which `Cysharp.Threading.Tasks.SwitchToThreadPoolAwaitable` switches onto a ThreadPool worker. Confirmed by the live crash stack:

```
0x... Cysharp.Threading.Tasks.SwitchToThreadPoolAwaitable/Awaiter:Callback (object)
0x... System.Threading.QueueUserWorkItemCallback:...ExecuteWorkItem ()
0x... System.Threading.ThreadPoolWorkQueue:Dispatch ()
0x... (Mono JIT Code) (wrapper managed-to-native) UnityEngine.Object:FindObjectsOfType (System.Type,bool)
```

Implications for what the postfix can read:

- Managed-memory access on game-internal types is safe (read `Battery.PowerStored`, `Transformer.UsedPower`, `CableNetwork.CurrentLoad`, etc).
- The game's own `ConcurrentDensePool<T>` collections (`OcclusionManager.AllThings`, `CableNetwork.AllCableNetworks`, `AtmosphericsManager.AllAtmospheres`) are safe to iterate off the main thread (they manage their own synchronisation).
- `UnityEngine.Object.FindObjectsOfType<T>()` is NOT safe; crashes the engine native side intermittently. Use the game's `ConcurrentDensePool` enumerations instead. Full writeup in `Research/Patterns/ThingEnumerationOffMainThread.md`.
- Any Unity-side mutation (`Instantiate`, `Destroy`, `gameObject.SetActive`, `transform.position` writes) must marshal to the main thread.

## Dedup across multiple pumps
<!-- verified: 0.2.6228.27061 @ 2026-05-26 -->

A diagnostic plugin that wants redundancy (the ElectricityTick was blocked, so the atmospheric tick pumps instead) can register postfixes on multiple subsystem ticks and dedupe by `UnityEngine.Time.frameCount` inside the dispatcher. `ScenarioRunner`'s `Dispatcher.OnSimTick()` records `_lastTickFrame = Time.frameCount` and bails on repeated calls from the same frame, so a second pump source only adds redundancy, never extra cost or scenario double-fires.

## Verification history

- 2026-05-26: page created. Sourced from a RuntimeProbe refactor that pulled the same hook out of PgpVerifyHelper and generalised it. Decompile cross-references at the line numbers above were re-confirmed against `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` during the same session. The crash stack quoted in "Threading constraint on the postfix" is the 2026-05-25 live repro recorded in `Research/Patterns/ThingEnumerationOffMainThread.md`.

## Open questions

- Exact method signature for `AtmosphericsManager`'s per-tick driver. The class inherits from `ThreadedManager`; identifying the override at the class top-of-body would let RuntimeProbe register an atmospheric-tick postfix without trial and error. Low priority; ElectricityTick is sufficient for current scenarios.
