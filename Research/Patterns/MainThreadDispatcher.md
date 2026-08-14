---
title: MainThreadDispatcher
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6428.27798
verified_at: 2026-08-14
sources:
  - TestRig/DedicatedServer/install/rocketstation_DedicatedServer_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Util.UnityMainThreadDispatcher, Assets.Scripts.Util.ManagerBase, Assets.Scripts.GameManager.Update (Mono.Cecil metadata read plus live Harmony instrumentation, 0.2.6428.27798)
  - TestRig/DedicatedServer/install/BepInEx/plugins/StationeersLaunchPad/StationeersLaunchPad.dll :: StationeersLaunchPad.LoadedMod.LoadEntrypoints, StationeersLaunchPad.Entrypoints.BepInExEntrypoint.Instantiate (ilspycmd decompile, 0.2.6428.27798)
  - TestRig/DedicatedServer/install/rocketstation_DedicatedServer_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.GameManager.StartGame, GameManager.GameTick, WorldManager.SetGamePause, WorldManager.UpdateFrameLimiter (.work/decomp/0.2.6428.27798/GameManager.DedicatedServer.decompiled.cs lines 716-859, 904-961, 1495-1560; WorldManager.DedicatedServer.decompiled.cs lines 1424-1444, 1886-1910)
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
<!-- verified: 0.2.6428.27798 @ 2026-08-14 -->

A `MonoBehaviour` on a `DontDestroyOnLoad` GameObject owns a `ConcurrentQueue<Action>` and drains it from its own `Update()`. Other code enqueues `Action` closures from any thread; the closure body executes on the main thread approximately one frame later.

Minimal shape (from F0032):

> `MainThreadDispatcher` is a `MonoBehaviour` on a `DontDestroyOnLoad` GameObject. It maintains a `ConcurrentQueue<Action>` drained in `Update()`. Every Harmony postfix that touches Unity API enqueues onto this dispatcher. Closure runs on main thread one frame later. ~1 frame latency, fully safe.

Key invariants:

- `DontDestroyOnLoad` keeps the dispatcher alive across scene loads (the main menu unloads the world scene, but the dispatcher must still drain queued actions). **One exception, and it is not rare:** `DontDestroyOnLoad` does not protect an object created before any scene is loaded, which is exactly where a `BepInEx/plugins/` chainloader plugin's `Awake` runs. Such an object is destroyed at `Time.frameCount == 0` and never receives a single `Update`. Creating the dispatcher directly in `Plugin.Awake` is therefore correct for a StationeersLaunchPad mod and broken for a chainloader plugin; see "Headless dedicated server: the player loop is healthy, the plugin's GameObject is dead" below for the measurement and the recreate-on-`sceneLoaded` fix.
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

## Headless dedicated server: the player loop is healthy, the plugin's GameObject is dead
<!-- verified: 0.2.6428.27798 @ 2026-08-14 -->

This section replaces "Headless dedicated server: Update does not pump after world load" (stamped 0.2.6228.27061), which was carried from repo lore rather than measured. The lore, quoted from `TestRig/MANUAL.md`, "The dedicated server half":

> on a headless dedicated server `MonoBehaviour.Update` does not fire after world load, and the top-level `GameManager.GameTick` is an async UniTask state machine that switches to a ThreadPool worker

The second clause is correct (see `../GameSystems/SimulationTickDriverHooks.md`). The first clause describes a real symptom and attributes it to the wrong cause, which is the dangerous combination: it predicts the wrong fix. Unity's player loop on a headless dedicated server never stops, before or after world load, paused or unpaused. What dies is the GameObject a BepInEx plugin creates.

### The BepInEx chainloader runs before any scene exists

BepInEx 5.4.23.5's preloader patches `UnityEngine.CoreModule` and starts the chainloader from `UnityEngine.Application`'s static constructor, which is earlier than the first scene load. Measured inside `BaseUnityPlugin.Awake` on the dedicated server, identical across four runs:

```
plugin gameObject: name=BepInEx_Manager  scene='DontDestroyOnLoad' (handle=-12, isLoaded=True)
                   hideFlags=None  activeInHierarchy=True  componentEnabled=True
unity at Awake:    frame=0  timeScale=1  targetFrameRate=-1  isBatchMode=True
                   activeScene=''  sceneCount=0  fixedDeltaTime=0.02
```

`sceneCount == 0` is the whole story. `DontDestroyOnLoad` appears to succeed (the object reports scene `DontDestroyOnLoad`, handle -12) but does not protect anything created at that moment. Roughly 130 to 215 ms later, still at `Time.frameCount == 0`, the unnamed bootstrap scene is torn down and takes the manager object with it. Event order in one run, from a probe that logged each callback:

```
    4 ms  plugin: Awake
    9 ms  created DDOL GameObject 'ValidatorProbe_DDOL'  scene='DontDestroyOnLoad' handle=-12
   10 ms  created plain GameObject 'ValidatorProbe_SceneLocal'  scene='' handle=0
  215 ms  plugin: OnDisable
  215 ms  plugin: OnDestroy      frame=0
  218 ms  sceneobj: OnDestroy    frame=0
  219 ms  ddol: OnDestroy        frame=0
  219 ms  sceneUnloaded: ''      frame=0
  282 ms  sceneLoaded: 'Splash'  frame=0
```

`Start()` is never reached. `Update`, `LateUpdate` and `FixedUpdate` fire **zero** times, on the plugin component and on both GameObjects it created, and they stay at zero for the life of the process (verified over runs of 192 s, 262 s and 312 s). `DontDestroyOnLoad` changes nothing: the DDOL object and the plain one die in the same instant, 1 ms apart.

`ClientDriver` already logs this from inside the same process, which is independent corroboration:

```
[Warning:ClientDriver] plugin component destroyed (count=1); control plane deliberately left running
```

### What survives the destruction

The component dies; static state does not. All of these kept working for the whole process:

- Harmony patches applied in `Awake`.
- `SceneManager.sceneLoaded` / `sceneUnloaded` / `activeSceneChanged` subscriptions registered in `Awake` (they fired at 282 ms for `Splash` and at 40246 ms for `Base`, long after `OnDestroy`).
- A `System.Threading.Thread` started in `Awake`.

### The fix: recreate the object from the first sceneLoaded callback

A DDOL GameObject created from the FIRST `SceneManager.sceneLoaded` callback survives indefinitely and misses nothing. Measured: created at 282 ms during the `Splash` load, still alive at the end of a 252 s run, with `Update` called 5867 times and `LateUpdate` 5867 times at `Time.frameCount == 5867`. Every frame the process ran, it got, including every frame of world generation.

Recreating later works too and costs exactly the frames skipped: an object created at the `Base` scene load (frame 1925) reached 3943 `Update` calls at frame 5867; one created after `GameState.Running` accumulated 6047 over 242 s. Neither was ever destroyed again. So the recipe for a plugin-owned dispatcher on a headless server is:

```csharp
void Awake()
{
    // The component and anything it creates here are destroyed at frame 0.
    // Subscribe from here anyway; the subscription outlives the component.
    SceneManager.sceneLoaded += (scene, mode) => EnsureDispatcher();
}

static void EnsureDispatcher()
{
    if (_dispatcher != null) return;               // Unity fake-null: catches destruction
    var go = new GameObject("MyMod_MainThreadDispatcher");
    UnityEngine.Object.DontDestroyOnLoad(go);
    _dispatcher = go.AddComponent<MainThreadDispatcher>();
}
```

The `_dispatcher != null` check must be the Unity comparison, not `ReferenceEquals`: after the boot-time destruction the managed reference is still non-null while the Unity object is gone. See `./UnityFakeNull.md`.

### StationeersLaunchPad mods are not affected

A mod loaded by StationeersLaunchPad from `data/mods/` never sees this. `StationeersLaunchPad.LoadedMod.LoadEntrypoints()` creates a fresh GameObject named after the mod, marks it `DontDestroyOnLoad`, and only then adds the plugin component:

```csharp
GameObject val = new GameObject { name = Info.Name };
Object.DontDestroyOnLoad((Object)(object)val);
foreach (ModEntrypoint entrypoint in Entrypoints)
    entrypoint.Instantiate(val);        // BepInExEntrypoint: parent.AddComponent(Type)
```

That runs during mod loading, long after scenes exist, so the object is durable. Measured live on the dedicated server, all three alive and enabled in scene `DontDestroyOnLoad` with `Update` firing at full frame rate:

| Mod component | Owning GameObject | `Update` rate |
|---|---|---|
| `InspectorPlus.InspectorPlusPlugin` | `Inspector Plus` | 24.85-25.06 /s |
| `InspectorPlus.MainThreadDispatcher` | `InspectorPlus_MainThreadDispatcher` | 24.85-25.06 /s |
| `BlueprintMod.BlueprintMod` | `BlueprintMod` | 24.85-25.06 /s |
| `FixingTheControls.Plugin` | (LaunchPad per-mod object) | 24.85-25.06 /s |

**This is the dividing line the old section was missing.** The mod-local `Update`-drained dispatcher recipe at the top of this page is sound for a StationeersLaunchPad mod on any target including a headless dedicated server. It is broken only for a plugin loaded by the BepInEx chainloader out of `BepInEx/plugins/`, and only because of when that plugin's `Awake` runs.

### Correction: do not drain a main-thread dispatcher from ElectricityTick

The replaced section recommended, as recovery pattern 2, a Harmony postfix on `ElectricityManager.ElectricityTick` calling the dispatcher's drain, and stated that "`ElectricityTick` is on the Unity main thread". **That is backwards and it inverts the purpose of this page.** In `GameManager.GameTick` the call sits between `await UniTask.SwitchToThreadPool()` and `await UniTask.SwitchToMainThread(cancellationToken)`:

```
747:  await UniTask.SwitchToThreadPool();
754:      AtmosphericsController.HandleMainThreadEvents();
795:      ElectricityManager.ElectricityTick();
828:  await UniTask.SwitchToMainThread(cancellationToken);
```

(`.work/decomp/0.2.6428.27798/GameManager.DedicatedServer.decompiled.cs`.) Measured with a postfix counter on `AtmosphericsController.HandleMainThreadEvents`, which sits at the same nesting level: 115 calls, 115 of them off the Unity main thread, observed managed thread id 40 while the main thread was id 1. `../GameSystems/SimulationTickDriverHooks.md` reports the same for `ElectricityTick` itself with thread ids 20, 25, 42, 50, 9, 58, 44, 45, 57. Draining a main-thread marshalling queue from there executes every queued Unity call on a ThreadPool worker, which is the exact hard native crash this page exists to prevent.

The correct headless pumps, all measured on the Unity main thread (id 1, zero off-thread hits):

| Pump | Rate | Runs while world is paused |
|---|---|---|
| Own `MonoBehaviour.Update` on a recreated DDOL object | 24.85-25.06 /s | yes |
| Own `MonoBehaviour.LateUpdate` | 24.85-25.06 /s | yes |
| Own `MonoBehaviour.FixedUpdate` | 49.89-50.11 /s | yes, unless `Time.timeScale` is 0 |
| Postfix on `Assets.Scripts.GameManager.Update` | 24.85-25.06 /s | yes |
| Postfix on `Assets.Scripts.GameManager.LateUpdate` | 24.85-25.06 /s | yes |
| `UnityMainThreadDispatcher.Enqueue` (game's own, drained from `ManagerUpdate`) | 24.85-25.06 /s | yes |
| Postfix on a `GameTick` sim phase (`ElectricityTick` and friends) | 1.91-1.93 /s | **no, and it is a ThreadPool worker** |

`Application.targetFrameRate` is pinned to 25 in batch mode by `WorldManager.UpdateFrameLimiter()`, which is where the ~25 Hz ceiling comes from; `Time.fixedDeltaTime` is the stock 0.02, hence 50 Hz.

One caveat on `GameManager.Update` and `GameManager.LateUpdate` as pumps: they are not available during boot. Before the world is up they fired 0.11 to 0.16 times per second while `Time.frameCount` advanced at 25 Hz, and only reached full rate once `GameState` became `Running`. An own-`Update` object recreated at the first `sceneLoaded` has no such gap.

## Measured: the game's dispatcher executes enqueued work while a headless world is paused
<!-- verified: 0.2.6428.27798 @ 2026-08-14 -->

The section above reasons about the game's `UnityMainThreadDispatcher` from its decompile. This section records what it actually does on a `-batchmode -nographics` dedicated server, measured on 2026-08-14 at 0.2.6428.27798 over three instrumented runs of `TestRig/DedicatedServer/` (`-new Lunar`, no client ever connected).

**It drains from `ManagerUpdate`, not from a Unity `Update` message.** `UnityMainThreadDispatcher : ManagerBase` declares exactly twelve methods and `Update` is not among them: `.cctor, .ctor, ActionWrapper, ClearAll, Enqueue(IEnumerator), Enqueue(ChunkThread), Enqueue(Action), Exists, Instance, ManagerAwake, ManagerUpdate, OnDestroy`. `Assets.Scripts.GameManager.Update` is the only caller of `ManagerBase.ManagerUpdate` in the assembly (every other hit is a manager's own override). Patching `UnityMainThreadDispatcher.Update` therefore resolves nothing; patch `ManagerUpdate`. The two builds are byte-identical in shape here: the server's `UnityMainThreadDispatcher` has the same 12 methods and 3 fields as the client's, unlike `ImGuiManager` (see `../GameSystems/SimulationTickDriverHooks.md`).

**It runs while the world is paused, at full frame rate.** With `Force Unpause Without Client` off, the world reached `GameState.Running`, `WorldManager.IsGamePaused` went true, and `GameManager.GameTickCount` stayed at **0 for the entire 287-second run** with `ElectricityManager.ElectricityTick` never firing once. Over that same window `ManagerUpdate` was called 4,699 times (116-122 per 5-second sample, ~24 Hz), and 48 of 49 actions enqueued from a plain background thread executed, on managed thread id 1 (the Unity main thread captured in `Awake`), with observed latencies of 4-37 ms. The unpaused run (`Force Unpause Without Client` on, `GameTickCount` rising 27 -> 332) produced the same drain rate and the same latencies.

So for a headless plugin that needs to touch Unity from an HTTP handler or any other background thread, `UnityMainThreadDispatcher.Instance().Enqueue(...)` is a working marshal that does **not** depend on the simulation running. That is the opposite of `ElectricityTick`-based pumps, which stop dead when the world parks, and the parked state is the default for a dedicated server with nobody connected.

Three caveats the measurement also produced:

- **`Exists()` is false for the first ~35 s of boot.** The dispatcher is created by `ManagerAwake`, so early-load enqueues have nowhere to go. Across the runs `Exists()` returned false on the first 6-7 five-second samples and true from then on. Guard with `Exists()`, never call `Instance()` blind; it throws rather than returning null.
- **Enqueued work stalls for seconds during world generation.** While `GameState` was `None` and terrain was generating, `Time.frameCount` froze (1437 for ~30 s in one run, 1936 for ~20 s in another) and queued actions sat undrained: single-item latencies of 4238 ms and 4650 ms were measured for actions enqueued just before world load, versus 4-37 ms once the world was up. A marshal with a fixed timeout must budget seconds if it can be called during load.
- **Queue depth stays at 0-1 in steady state**, read from the static `UnityMainThreadDispatcher.ExecutionQueue`. Nothing accumulates.

Verified with a throwaway BepInEx plugin holding Harmony postfixes on `Assets.Scripts.Util.UnityMainThreadDispatcher.ManagerUpdate`, `Assets.Scripts.GameManager.Update`, `Assets.Scripts.Networks.ElectricityManager.ElectricityTick` and `WorldManager.SetGamePause`, plus a background `System.Threading.Thread` enqueueing a timestamped marker every 5 s.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-05-28 -->

- 2026-08-13: the two TestRig launchers were replaced by one, `TestRig/testrig.ps1`, with positional verbs and `-Target`, and the rig's per-half documents were consolidated into `TestRig/CLAUDE.md`, `TestRig/MANUAL.md` and `TestRig/RESEARCH.md`. Pointers and command spellings on this page follow. No game-internals claim changed and none was re-verified, so no section stamp moved.
- 2026-04-20: page created from the Research migration; implementation verbatim from F0308, with underlying-cause detail from F0032 and the main-thread-capture addendum from F0350.
- 2026-05-28: added "The game's UnityMainThreadDispatcher, and why mods roll their own". Read `UnityMainThreadDispatcher : ManagerBase` (decompile line 219184): `Instance()` throws when the MainThreadExecutor manager is absent; `Enqueue(Action)` wraps in an `ActionWrapper` coroutine and drains under an `if (action.Target != null)` guard that drops target-less delegates; the execution queue is shared with engine `ChunkThread` tasks. Documents the game-vs-mod-local trade-off. Additive (the page previously covered only the mod-local recipe); no existing claim contradicted, so no fresh validator.
- 2026-05-28: added "Headless dedicated server: Update does not pump after world load". Documents the constraint already captured in `TestRig/DedicatedServer/CLAUDE.md` (the dedicated-server doc points at `Research/Patterns/ThingEnumerationOffMainThread.md` for the `GameTick` worker-thread half but is the only place the no-`Update` half lives). Cites ScenarioRunner's `ElectricityManager.ElectricityTick` postfix as the in-repo precedent for the sim-tick pump workaround. Surfaced while running the Power Grid Plus passthrough-refresh dedi playtest on a copy of `APC-Luna.save`: the mod's cascade-refresh queue is enqueued from `PassthroughModeStore.RestoreFromSideCar` and the cascade engine, but the dedi has no rendering motherboard consumer to notify, so the stalled-queue case is invisible from snapshots; the constraint is documented as repo lore, not independently re-verified this session. Additive; no existing claim contradicted; no fresh validator.

- 2026-08-14: added "Measured: the game's dispatcher executes enqueued work while a headless world is paused" while measuring pump options for the TestRig plugin merge. Three instrumented `-batchmode -nographics` runs at 0.2.6428.27798 plus a Mono.Cecil metadata read of both `Assembly-CSharp.dll` builds. Additive with respect to the two decompile-derived sections above, and it corrects one detail in "The game's UnityMainThreadDispatcher, and why mods roll their own" without contradicting it: that section's prose already says the drain is tied to `ManagerUpdate`, and the new section makes explicit that `Update` is not a member of the type at all, so a Harmony patch aimed at `Update` resolves nothing. **It does conflict with "Headless dedicated server: Update does not pump after world load"**, which is left standing pending the Rule 3 fresh validator; see Open questions.

- 2026-08-14 (Rule 3 fresh validator, conflict resolved). Conflict on "does a plugin's `MonoBehaviour.Update` fire on a headless dedicated server after world load". Previous claim (stamped 0.2.6228.27061, from `TestRig/MANUAL.md` lore): an `Update`-drained queue stalls server-side once the world has loaded, so add a sim-tick pump. New finding (2026-08-14 measurement): a plugin-created `DontDestroyOnLoad` MonoBehaviour ticks at ~24 Hz after world load. Fresh validator verdict: **neither claim as stated; both describe the same fact from opposite ends.** The player loop is healthy and never stops; the plugin's component and everything it creates in `Awake` are destroyed at `Time.frameCount == 0`, before the first scene loads, and receive zero `Update` calls ever. Independent evidence, four instrumented `-batchmode -nographics` runs on `TestRig/DedicatedServer/` at 0.2.6428.27798 (`-new Lunar`, no client ever connected, 5 s sampling, steady-state windows of 55 s to 312 s): plugin component and both `Awake`-created GameObjects (one `DontDestroyOnLoad`, one not) logged `Awake` at 2-10 ms and `OnDestroy` at 135-219 ms, always at frame 0, `Start` never reached, `Update`/`LateUpdate`/`FixedUpdate` counters flat at 0 for the whole process; at `Awake` time `SceneManager.sceneCount == 0` and `activeScene.name == ""`, which is why `DontDestroyOnLoad` does not bind; an object recreated from the first `SceneManager.sceneLoaded` callback (282 ms, still frame 0) reached `Update` count 2262 at `Time.frameCount` 2262, missing no frame. Corroborated in-process by `ClientDriver`'s own log line "plugin component destroyed (count=1)". Result: "Headless dedicated server: Update does not pump after world load" replaced by "Headless dedicated server: the player loop is healthy, the plugin's GameObject is dead", restamped 0.2.6428.27798; the `DontDestroyOnLoad` invariant under "Solution / recipe" gained its exception and was restamped; the StationeersLaunchPad-versus-chainloader dividing line added from `StationeersLaunchPad.LoadedMod.LoadEntrypoints` plus live measurement of four LaunchPad mod plugin components ticking at full rate.

- 2026-08-14 (same validator pass, second correction). The replaced section's recovery pattern 2 stated "`ElectricityTick` is on the Unity main thread; other ticks can be ThreadPool workers" and recommended draining a main-thread dispatcher from an `ElectricityTick` postfix. That is inverted, and it is the most dangerous statement either page carried, because following it executes every queued Unity call on a ThreadPool worker, which is the hard native crash this page exists to prevent. `GameManager.GameTick` calls `ElectricityManager.ElectricityTick()` at line 795, between `await UniTask.SwitchToThreadPool()` (line 747) and `await UniTask.SwitchToMainThread(cancellationToken)` (line 828) in `.work/decomp/0.2.6428.27798/GameManager.DedicatedServer.decompiled.cs`. Measured with a postfix counter on `AtmosphericsController.HandleMainThreadEvents` (line 754, same nesting level): 115 calls, 115 off the Unity main thread, observed thread id 40 against main thread id 1. `../GameSystems/SimulationTickDriverHooks.md` "Threading constraint on the postfix" already said the same thing about `ElectricityTick` itself, so this page was the outlier. Result: the claim is corrected in place under "Correction: do not drain a main-thread dispatcher from ElectricityTick", with a measured table of pumps that are on the main thread.

## Open questions

- Not measured this pass: whether the boot-time destruction of `BepInEx_Manager` also happens on the game client build. The mechanism (chainloader in `Application`'s static constructor, `sceneCount == 0` at plugin `Awake`) is not server-specific and BepInEx is configured identically, but every run in the 2026-08-14 validator pass was `-batchmode -nographics` on the dedicated server, so the client case is unverified. Any mod that hangs a dispatcher off a `BepInEx/plugins/` chainloader plugin and works on a client today is evidence against a naive "it happens everywhere" reading; check before generalising.
