---
title: Simulation tick driver hooks
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6428.27798
verified_at: 2026-08-14
sources:
  - TestRig/DedicatedServer/install/rocketstation_DedicatedServer_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.UI.ImGuiManager, Assets.Scripts.Util.UnityMainThreadDispatcher, Assets.Scripts.Util.ManagerBase, Assets.Scripts.Util.Singleton`1 (Mono.Cecil metadata read, 0.2.6428.27798)
  - $(StationeersPath)/rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.UI.ImGuiManager (client-side comparison, Mono.Cecil metadata read, 0.2.6428.27798)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: line 254905 (ElectricityManager.ElectricityTick), 417811 (AtmosphericsManager : ThreadedManager), 187543 (GameManager.RecordGameTick), 189381 (GameManager.StartGameTick), 189076 (GameManager.GameTickPaused)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: line 205154 (GameManager.Update), 203880 (GameManager.Managers), 204387 (GameManager.GameTick), 204363 (StartGameTick), 203823 (DefaultTickSpeedMs), 60520 (WorldManager.StartWorld), 60886 (WorldManager.SetGamePause), 272091 (ElectricityManager.ElectricityTick)
  - TestRig/DedicatedServer/install/rocketstation_DedicatedServer_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.GameManager.StartGame + DelayedStartupPause (decompiled to .work/decomp/0.2.6403.27689/Assembly-CSharp.DedicatedServer.GameManager.decompiled.cs lines 902-959)
  - .work/decomp/0.2.6428.27798/GameManager.DedicatedServer.decompiled.cs :: lines 656-683 (LateUpdate, FixedUpdate), 716-859 (GameTick, SwitchToThreadPool at 747, HandleMainThreadEvents at 754, ElectricityTick at 795, SwitchToMainThread at 828), 904-961 (StartGame, DelayedStartupPause), 1495-1560 (Update)
  - .work/decomp/0.2.6428.27798/WorldManager.DedicatedServer.decompiled.cs :: lines 1424-1444 (UpdateFrameLimiter), 1886-1910 (SetGamePause)
related:
  - ../GameClasses/PowerTick.md
  - ../GameClasses/GameManager.md
  - ../Patterns/ThingEnumerationOffMainThread.md
tags: [power, threading, harmony]
---

# Simulation tick driver hooks

How to drive a diagnostic plugin from the game's per-tick simulation chain. Background for `TestRig/DedicatedServer/dev-plugins/ScenarioRunner/` and `Mods/InspectorPlus/`.

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
<!-- verified: 0.2.6428.27798 @ 2026-08-14 -->

On a headless dedicated server:

- A `MonoBehaviour.Update` poll written the natural Unity way, on a GameObject a `BepInEx/plugins/` chainloader plugin creates in its `Awake`, goes silent. **The cause is that the object is dead, not that the loop stalled.** The plugin component and everything it creates in `Awake` are destroyed at `Time.frameCount == 0`, before the first scene loads, having received zero `Update` calls; `DontDestroyOnLoad` does not protect them because `SceneManager.sceneCount == 0` at that moment. The player loop itself runs at ~25 Hz for the life of the process. Full measurement, the recreate-on-`sceneLoaded` fix, and the reason StationeersLaunchPad mods are immune: `../Patterns/MainThreadDispatcher.md`, "Headless dedicated server: the player loop is healthy, the plugin's GameObject is dead".
- `MainThreadDispatcher` patterns based on a `DontDestroyOnLoad` MonoBehaviour inherit exactly that, and only that. A dispatcher object recreated after boot ticks at full frame rate, paused world or not. The earlier wording here, "the dispatcher's PollLoop coroutine never advances past its first yield", was repo lore and is wrong: a coroutine on a live object advances normally.
- A `FileSystemWatcher` callback fires on a ThreadPool thread, so any Unity API call from it crashes. Routing through the dispatcher only helps if the dispatcher is alive.
- The GameTick-driven subsystem Tick methods fire on every simulation cycle whenever `RunSimulation` is true. A Harmony postfix on `ElectricityManager.ElectricityTick` is the simplest pump **for work that must observe the simulation**, at 2 Hz and on a ThreadPool worker. It is the wrong pump for anything that must answer while the world is parked, which is the default state of a dedicated server with nobody connected, and the wrong pump for anything that must run on the main thread.

`Mods/InspectorPlus/InspectorPlus/RequestPollOnTickPatch.cs` already uses this pattern for its request poller; `TestRig/DedicatedServer/dev-plugins/ScenarioRunner/ScenarioRunner/SimTickPump.cs` follows the same convention so the two cohabit cleanly.

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

## GameManager.Update manager loop: no per-manager exception isolation
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

`GameManager : Singleton<GameManager>` (0.2.6403.27689 decompile line 203733) holds the per-frame manager list as a plain instance field (line 203880):

```csharp
public List<ManagerBase> Managers = new List<ManagerBase>();
```

A live dedicated server at 0.2.6403.27689 reports 41 entries at boot (`loaded 41 systems successfully` in server.log; the format string is quoted below). `KeyManager` is one of them (`public class KeyManager : ManagerBase`, line 43646, with its own `ManagerUpdate` override at line 43736).

`GameManager.Update()` (line 205154) ends with the per-frame manager loop, and that loop has NO try/catch (lines 205213-205219):

```csharp
			foreach (ManagerBase manager2 in Managers)
			{
				manager2.ManagerUpdate();
			}
			Assets.Scripts.Objects.BatchRenderer.RenderAll();
			WindTurbineGenerator.UpdateWind();
		}
```

Two placement facts about `Update()`:

- The `ManagerUpdate` foreach sits OUTSIDE the `if (!WorldManager.IsGamePaused)` block that wraps the rest of the method body (lines 205160-205212), so managers get their `ManagerUpdate` every frame even while the game is paused.
- A second, throttled loop `foreach (ManagerBase manager in Managers) { manager.SlowUpdate(); }` (lines 205193-205196) runs inside the pause gate and inside the 100 ms throttle block. It is equally unguarded.

Contrast with startup: `GameManager.Start()` (line 205043) wraps BOTH the `ManagerAwake` loop (lines 205051-205063) and the `ManagerStart` loop (lines 205064-205076) in a per-manager try/catch that logs and counts failures:

```csharp
			foreach (ManagerBase manager in Managers)
			{
				try
				{
					manager.ManagerAwake();
				}
				catch (System.Exception ex)
				{
					UnityEngine.Debug.LogException(ex);
					ConsoleWindow.PrintError("error in awake with '" + manager.GetType().Name + "' " + ex.Message);
					num++;
				}
			}
```

(then `loaded {Managers.Count} systems successfully` or `loaded {Managers.Count} systems with {num} exceptions`, lines 205077-205084).

Consequence: an exception escaping any `ManagerUpdate` override propagates out of `GameManager.Update`. Unity logs it and aborts the rest of the method for that frame, which means every manager AFTER the throwing one in `Managers` list order is skipped, plus `BatchRenderer.RenderAll()` and `WindTurbineGenerator.UpdateWind()`. If the throw repeats each frame (the typical broken-Harmony-prefix case), the tail of the manager list is starved permanently while the process keeps running. Observed live during the 2026-07-02 dedicated-server boot investigation: a broken mod's prefix on the `KeyManager` stage threw every frame and every manager after `KeyManager` never ran, with no crash and no obvious log signal beyond the repeating exception.

Harmony implication: a prefix or postfix on any `ManagerBase.ManagerUpdate` override (or on anything those overrides call synchronously) inherits zero isolation. Wrap mod-side bodies in try/catch; a throwing patch does not just break its own mod, it silently disables every downstream manager. Note the contrast with the tick side: the `GameTick` worker body wraps its simulation phases in a try/catch per tick (see the next section), so the same mistake inside a tick-phase patch is logged and survived, while the same mistake inside a manager-update patch starves the manager list.

## GameTick loop, pause parking, and SetGamePause call sites
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

Line numbers in this section are from `.work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs`.

**World start path.** `World.Initialize(worldName, newWorld, loadingScreenMessage)` (static class `World`, line 324881; method at 325012) sets `GameManager.GameState = GameState.Joining` (line 325019). Both world entry points then call `WorldManager.StartWorld()`: `World.NewAsync` (line 324921, new world) at line 324956 and `World.OnLoadingFinished` (line 324961, save load) at line 324964. The client join path does the same dance: `ClientPreJoin` (line 213107) sets `GameState = GameState.Joining` (line 213109) and the join-package completion calls `WorldManager.StartWorld()` (line 213083). `WorldManager.StartWorld` (line 60520) starts the five manager singletons (`RoomManager`, `ElectricityManager`, `AtmosphericsManager`, `OcclusionManager`, `LightManager`), then:

```csharp
		GameManager.SetTickSpeed();
		GameManager.StartGameTick();
		WorldSetting.StartWorld();
```

`StartGameTick` (line 204363) resets `GameTickCount`, creates the cancellation source, and fires the loop: `GameTick(_cancelGameTickTask.Token).Forget();`.

**The tick loop and its pause parking.** `GameTick` (line 204387) is an `async UniTask` running for the lifetime of the world:

```csharp
	private static async UniTask GameTick(CancellationToken cancellationToken = default(CancellationToken))
	{
		Stopwatch gameTickStopwatch = new Stopwatch();
		gameTickStopwatch.Start();
		while (!cancellationToken.IsCancellationRequested && GameState != GameState.None)
		{
			LastTickTimeSeconds = (float)gameTickStopwatch.ElapsedMilliseconds / 1000f;
			while (WorldManager.IsGamePaused || GameTickPaused)
			{
				if (_gameTickPauseScheduled)
				{
					lock (GameTickPauseLock)
					{
						GameTickPaused = true;
					}
				}
				await UniTask.Delay(GameTickSpeedMs, DelayType.UnscaledDeltaTime, PlayerLoopTiming.Update, cancellationToken);
				if (cancellationToken.IsCancellationRequested)
				{
					return;
				}
			}
```

The inner `while (WorldManager.IsGamePaused || GameTickPaused)` (line 204394) is the park: while either flag is up, the loop just re-polls every `GameTickSpeedMs` and no simulation phase runs. After the park, the body switches to the ThreadPool (`await UniTask.SwitchToThreadPool();`, line 204418) and runs the simulation phases inside a `try { ... } catch (System.Exception exception) { Profiler.EndThreadProfiling(); UnityEngine.Debug.LogException(exception); }` (lines 204421-204496), returns to the main thread (line 204499), and finally paces itself: `while (gameTickStopwatch.Elapsed.Milliseconds < GameTickSpeedMs) { await UniTask.Delay(1, ...); }` then `GameTickCount++` (lines 204512-204520).

**Tick speed is 500 ms.** `private static readonly int DefaultTickSpeedMs = 500;` (line 203823), `public static int GameTickSpeedMs => DefaultTickSpeedMs;` (line 204007), `public static float GameTickSpeedSeconds => (float)GameTickSpeedMs / 1000f;` (line 204009). So the game tick is 2 Hz.

**RunSimulation gates the sim phases.** `public static bool RunSimulation => !Assets.Scripts.Networking.NetworkManager.IsClient;` (line 203945). Inside the `GameTick` body the simulation phases are wrapped in `if (RunSimulation)` blocks (lines 204410, 204423, 204448, 204482, 204500); the electricity tick call site is line 204466 inside one of them:

```csharp
					Assets.Scripts.Objects.Item.AllDecayingItems.ForEach(ItemDecayServerAction);
					ImGuiProfiler.Update("GameTick", "ItemDecayServerAction");
					ElectricityManager.ElectricityTick();
```

`ElectricityManager.ElectricityTick()` (line 272091) is additionally self-guarded:

```csharp
	public static void ElectricityTick()
	{
		if (!GameManager.RunSimulation)
		{
			return;
		}
```

So on a paused world NOTHING in the tick body runs (parked upstream), and on a client `RunSimulation == false` skips the sim phases even when the tick loop spins.

**GameTickPaused plumbing.** `GameTickPaused` (line 204022) is a lock-guarded static property. `PauseGameTick()` (line 204370) only schedules (`_gameTickPauseScheduled = true`); the flag is actually raised at the two loop checkpoints (lines 204396-204402 inside the park, lines 204505-204511 after the tick body), so a scheduled pause takes effect on a tick boundary. `UnpauseGameTick()` (line 204378) clears both. `StopGameTick()` (line 204356) cancels the loop, unpauses, and calls `AtmosphericsManager.ClearAll()`.

**SetGamePause is silent.** `WorldManager.SetGamePause(bool)` (line 60886) performs no logging of any kind:

```csharp
	public static void SetGamePause(bool pauseGame)
	{
		if (IsGamePaused != pauseGame)
		{
			IsGamePaused = pauseGame;
			if (pauseGame)
			{
				KeyManager.SetInputState("WorldManager", KeyInputState.Paused);
			}
			else
			{
				KeyManager.RemoveInputState("WorldManager");
			}
			Time.timeScale = (pauseGame ? 0f : 1f);
			RoomManager.Instance.IsPaused = pauseGame;
			OcclusionManager.Instance.IsPaused = pauseGame;
			ElectricityManager.Instance.IsPaused = pauseGame;
			AtmosphericsManager.Instance.IsPaused = pauseGame;
			LightManager.Instance.IsPaused = pauseGame;
			if (!GameManager.IsBatchMode)
			{
				AudioManager.UpdateVolume(SettingType.SoundVolume);
			}
			WorldManager.OnPaused?.Invoke(pauseGame);
		}
	}
```

Diagnostic consequence for headless servers: when something pauses the game via `SetGamePause` directly, the server log shows NO pause line; the only tell is that tick-driven activity stops. The log lines players associate with pausing belong to specific call sites, not to the pause itself.

**SetGamePause(true) call-site inventory (0.2.6403.27689).** The headless-relevant writers:

- `WorldManager.EnablePause(bool showPrompt = true)` (line 60874). Batch mode suppresses the confirmation prompt but NOT the pause:

  ```csharp
  	public void EnablePause(bool showPrompt = true)
  	{
  		if (GameManager.RunSimulation)
  		{
  			if (!GameManager.IsBatchMode && showPrompt)
  			{
  				PromptPanel.Instance.ShowPrompt(PromptPauseStrings.Title, PromptPauseStrings.PauseBody, PromptPauseStrings.ResumeButton, ResumePlay, isEscapable: false, hideCancelButton: true);
  			}
  			SetGamePause(pauseGame: true);
  		}
  	}
  ```

  No in-assembly caller exists; it is invoked through serialized UnityEvents (UI wiring), so grep-for-callers comes up empty by design.
- `Stationpedia.PauseGameToggle(bool value)` (line 247054, class `Stationpedia : ResizableWindow, IModal` at 246715): `if (!Assets.Scripts.Networking.NetworkManager.IsClient && NetworkBase.Clients.Count == 0 && !InventoryManager.Instance.InGameMenuOpen) { WorldManager.SetGamePause(value); }`. Also UnityEvent-wired (the window's pause checkbox; no in-assembly caller). Note the guard is true on a dedicated server with zero connected clients, so a programmatically driven Stationpedia pause toggle parks the whole tick loop silently. `Stationpedia.SetVisible` itself (line 249478) does not pause; on hide it calls `WorldManager.OnPanelClose()` (line 60866), which resumes only if neither Stationpedia, `InputSourceCode`, nor `InGameMenu` is still open.
- `InputSourceCode.PauseGameToggle(bool pauseGame)` (line 240335, class at 240201): same pattern minus the `InGameMenuOpen` check.
- `NetworkBase.PauseEvent(bool pause)` (line 39310, class `NetworkBase : ManagerBase` at 39197): the multiplayer pause relay and the ONLY pause path that logs (`ConsoleWindow.Print(pause ? "Game is Paused" : "Game is resumed");`) before `SendToClients(new NetworkMessages.UpdatePauseMessage ...)` and `WorldManager.SetGamePause(pause)`.
- `NetworkBase.AutoSaveOnLastClientLeave` (line 39256): dedicated-server auto-pause. Logs `"No clients connected. Will save and pause in 10 seconds."`, waits 10 s, autosaves, logs `"Server Paused"`, then `WorldManager.SetGamePause(pauseGame: true);` (line 39266).
- The `pause` console command (line 100385, `CommandScope.InGame | CommandScope.HostOrSinglePlayer`): `WorldManager.SetGamePause(result);` (line 100400), returns "Game paused." / "Game unpaused.".
- Load/join paths pause while streaming: `XmlSaveLoad.LoadWorld` (line 268509), `World.NewAsync` (line 324923), `PauseEventJoiningClient` (line 213160).
- The DEDICATED SERVER assembly adds one more writer that the client assembly does not have: `GameManager.DelayedStartupPause` (next section). The inventory above was compiled from the client decompile; any headless-pause audit must also read the server binary.

## Dedicated-server assembly only: DelayedStartupPause re-pauses 5 s after StartGame
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

The dedicated server ships its own `Assembly-CSharp.dll` (`rocketstation_DedicatedServer_Data/Managed/`), and its `GameManager` is NOT identical to the client build. At 0.2.6403.27689 the server build's `GameManager.StartGame()` is the same method as the client's (quoted from `.work/decomp/0.2.6403.27689/Assembly-CSharp.DedicatedServer.GameManager.decompiled.cs` lines 902-950) except for one extra final statement:

```csharp
		DelayedStartupPause().Forget();
	}

	private static async UniTaskVoid DelayedStartupPause()
	{
		await UniTask.Delay(5000, DelayType.UnscaledDeltaTime);
		if (NetworkBase.Clients.Count <= 0)
		{
			WorldManager.SetGamePause(pauseGame: true);
		}
	}
```

Neither `DelayedStartupPause` nor the call exists anywhere in the client assembly (grep of the full client decompile returns nothing). Facts that follow:

- **This is the mechanism that leaves a freshly started dedicated server paused with no client connected.** It is unconditional apart from the client count: it does not check `AutoPauseServer` (the `NetworkBase.AutoSaveOnLastClientLeave` path above is the only auto-pause that honors that setting), it does not log, and `SetGamePause` itself is silent, so the world stops ticking ~5 s after `StartGame` with no line in any log.
- **It defeats one-shot force-unpause patches by design.** `StartGame` is `async UniTask`; a Harmony postfix on it fires when the stub returns the task (at the first suspension, `await NetworkServer.Host()`), so any unpause applied in a `StartGame` postfix lands BEFORE the 5-second delay elapses and gets overwritten. Observed live on 2026-07-02: InspectorPlus's one-shot unpause ran, exactly 8 game ticks executed (~4 s at 2 Hz), then a stack-traced `SetGamePause(true)` arrived from `Assets.Scripts.GameManager.DelayedStartupPause()` via `Cysharp.Threading.Tasks.UniTask+DelayIgnoreTimeScalePromise.MoveNext()`, and the tick loop parked again.
- **Countermeasures (both implemented in `Mods/InspectorPlus/InspectorPlus/HeadlessUnpausePatch.cs`, opt-in, batch-mode only):** (1) a guarded Harmony prefix that skips `DelayedStartupPause` outright; the target only exists in the server assembly, so the patch class uses `Prepare()` returning false on the client build to avoid a PatchAll failure, and skipping the stub of an `async UniTaskVoid` method is safe because the caller's `.Forget()` on the default struct is a no-op; (2) a 5-second UniTask watchdog loop that logs `GameState / IsGamePaused / GameTickPaused / RunSimulation / GameTickCount / Clients.Count` and re-unpauses when parked with zero clients (skipping while `SaveHelper.IsSaving`), which also catches any OTHER silent pauser from the inventory above. The UniTask player loop (`PlayerLoopTiming.Update`) demonstrably runs on the headless server even while the tick loop is parked; the park loop itself awaits `UniTask.Delay` there.
- A note for probes: because the first ~8 ticks DO run between `StartGame` and the delayed pause, an InspectorPlus request dropped before world load can be consumed during that early window even on an otherwise-parked server. A consumed early probe is NOT proof the sim stayed running; re-probe after the 5-second mark.

## Dedicated-server assembly only: ImGuiManager ships as a stub with no LateUpdate
<!-- verified: 0.2.6428.27798 @ 2026-08-14 -->

`Assets.Scripts.UI.ImGuiManager` exists in both builds by name, but the dedicated server's copy is a stub with no behaviour. Metadata read with Mono.Cecil over both `Assembly-CSharp.dll` files at 0.2.6428.27798:

| | Client (`rocketstation_Data/Managed`) | Dedicated server (`rocketstation_DedicatedServer_Data/Managed`) |
|---|---|---|
| Base type | `UnityEngine.MonoBehaviour` | `Assets.Scripts.Util.Singleton<ImGuiManager>` |
| Methods | 19 | **1** (`.ctor()` only) |
| Fields | 17 | **0** |

The client's 19 methods are `.cctor, .ctor, Awake, CreateRenderTexture, ImGuiPointerFor, InitializeImGui, LateUpdate, OnDestroy, OnDisable, OnEnable, PrepareCommandBuffer, PrepareImGuiFrame, RandomLoadingTexture, RenderCommandBufferToCamera, RenderComputerScreens, RenderImGuiTo, RenderOverlay, SetBlockUguiClicks, ShutdownImGui`. The server declares none of them, and none is inherited: the chain is `Singleton<T>` -> `ManagerBase` -> `MonoBehaviour`, and neither `Singleton<T>` (`.cctor, .ctor, Create, get_Instance, get_IsQuitting, OnApplicationQuit, OnDestroy`) nor `ManagerBase` (`.ctor, get_ProfilerTag, ManagerAwake, ManagerStart, ManagerUpdate, SlowUpdate`) declares `LateUpdate`.

Consequences, all confirmed live on a `-batchmode -nographics` server at 0.2.6428.27798:

- **`ImGuiManager.LateUpdate` cannot be Harmony-patched on the dedicated server: the method does not exist.** `AccessTools.Method(type, "LateUpdate")` returns null after walking base types. A `Prepare()` that resolves the target reflectively therefore returns false and the patch is silently skipped; a patch class that assumes the target exists throws inside `PatchAll` and takes every later patch in the same call down with it.
- **Zero `ImGuiManager` instances exist in the scene.** `UnityEngine.Object.FindObjectsOfType(typeof(ImGuiManager))` returned 0 at every sample across three runs spanning boot, world generation, `GameState.Running` and 190+ s of steady state. So even a patch on a method the stub did declare would never fire.
- `RG.ImGui.dll` being present in the server's `BepInEx/plugins/StationeersLaunchPad/` is not evidence that the game's ImGui overlay runs headless. The overlay class is gutted in the server assembly regardless of what the binding library ships.

This is the same client/server assembly divergence as `DelayedStartupPause` below: the two `Assembly-CSharp.dll` files are different builds, and any hook chosen on the client must be re-checked against the server binary before it is assumed to exist.

## Headless dedicated server: the Unity player loop runs at ~24 Hz whether or not the world is paused
<!-- verified: 0.2.6428.27798 @ 2026-08-14 -->

Pausing the world stops `GameTick`. It does not stop Unity's player loop, and it does not stop `GameManager.Update` or the `ManagerBase.ManagerUpdate` fan-out that `Update` drives. The two clocks are independent, and on a headless server they run at very different rates.

Measured with a Harmony-postfix counter plugin on a `-batchmode -nographics` server, `-new Lunar`, no client ever connected, sampling every 5 s. Counts are calls per 5-second report over the steady-state window after `GameState` reached `Running`:

| Counter | World paused (`Force Unpause Without Client` = false) | World running (setting = true) |
|---|---|---|
| `GameManager.Update` | 116-122 (~24 Hz) | 117-120 (~24 Hz) |
| `UnityMainThreadDispatcher.ManagerUpdate` | 116-122 (~24 Hz) | 117-120 (~24 Hz) |
| `MonoBehaviour.Update` on a plugin object **recreated after boot** | 116-122 (~24 Hz) | 117-120 (~24 Hz) |
| `MonoBehaviour.LateUpdate`, same object | 116-122 (~24 Hz) | 117-120 (~24 Hz) |
| `MonoBehaviour.FixedUpdate`, same object | 247-251 (~50 Hz) | 249-252 (~50 Hz) |
| Coroutine (`WaitForSecondsRealtime(1)`), same object | 4-5 (~1 Hz) | 4-5 (~1 Hz) |
| Any of the above on the object the plugin created **in `Awake`** | **0, always** | **0, always** |
| `ElectricityManager.ElectricityTick` | **0** | 9-10 (~2 Hz) |
| `GameManager.GameTickCount` | **0, for the whole 287 s run** | rising, 27 -> 332 |

The "recreated after boot" qualifier on the plugin-owned rows is load-bearing, and the 2026-08-14 measurement that produced this table did not carry it. The GameObject a `BepInEx/plugins/` chainloader plugin creates in `Awake` is destroyed at frame 0 and receives zero callbacks of any kind for the life of the process; only a replacement created once a scene exists ticks. Reading these rows as "a plugin's MonoBehaviour ticks headless" is the trap. Mechanism, numbers and fix: `../Patterns/MainThreadDispatcher.md`.

The paused run held `WorldManager.IsGamePaused == true` and `GameTickCount == 0` for its entire life, so `ElectricityTick` never fired even once, while `GameManager.Update` accumulated 5,063 calls over the same period. Frame rate held at ~24 fps in both runs (`Time.frameCount` 1695 -> 6384 across 195 s paused).

Practical consequences for a headless plugin:

- **`ElectricityTick` is a simulation-liveness signal, not a general pump.** It is the correct hook for anything that must observe simulation state per tick, and it is useless for anything that must answer while the world is parked, which is the default state of a dedicated server with no client (see `DelayedStartupPause` below).
- **`GameManager.Update` fires at ~12x the simulation tick rate** and keeps firing when the simulation does not. It is the driver behind every `ManagerUpdate`, including `UnityMainThreadDispatcher`'s.
- **`ElectricityTick`'s postfix thread is a rotating ThreadPool worker, never the Unity main thread.** Across one run the managed thread id observed in the postfix was 20, 25, 42, 50, 9, 58, 44, 45, 57 on successive samples, while the Unity main thread was id 1 throughout. This reconfirms "Threading constraint on the postfix" above at 0.2.6428.27798.
- **The player loop does stall hard during world generation.** Between the plugin loading and `GameState` leaving `None`, `Time.frameCount` froze (1437 for 30 s in one run, 1936 for 20 s in another) and `GameManager.Update` advanced only 3-4 times in 15 s. Work marshalled to the main thread during that window waits: measured single-item latencies of 4238 ms and 4650 ms for an action enqueued just before world load, against 4-37 ms once the world was up. A main-thread marshal with a fixed timeout must budget for seconds, not milliseconds, if it can be called during world load.

Verified with a throwaway BepInEx counter plugin (`Assets.Scripts.GameManager.Update`, `Assets.Scripts.Util.UnityMainThreadDispatcher.ManagerUpdate`, `Assets.Scripts.Networks.ElectricityManager.ElectricityTick`, `WorldManager.SetGamePause` postfixes plus a plugin-created `DontDestroyOnLoad` MonoBehaviour), three runs on 2026-08-14 against `TestRig/DedicatedServer/`.

## A "paused" headless server usually still has Time.timeScale at 1
<!-- verified: 0.2.6428.27798 @ 2026-08-14 -->

`WorldManager.SetGamePause(bool)` assigns `Time.timeScale = (pauseGame ? 0f : 1f)`, but only inside `if (IsGamePaused != pauseGame)`. On a fresh headless boot that guard never opens, so the world ends up flagged paused with `timeScale` still 1:

1. The load path pauses. `WorldManager.IsGamePaused` is already `true` while `GameState` is `Joining`.
2. `GameManager.StartGame()` assigns `Time.timeScale = 1f;` **directly**, not through `SetGamePause`, so the flag stays `true` and the scale goes back to 1.
3. `DelayedStartupPause` fires 5 s later and calls `SetGamePause(true)`. `IsGamePaused` is already `true`, so the whole body is skipped and `timeScale` is never dropped.

Measured on a stock `-batchmode -nographics` server (`-new Lunar`, no client, InspectorPlus `Force Unpause Without Client` off), 85 s steady-state window: `gameState=Running`, `isGamePaused=True`, `timeScale=1.0`, `GameTickCount` flat at 0.

Three regimes, all measured on the same run, 55 s to 85 s each, sampling every 5 s:

| | `IsGamePaused` | `Time.timeScale` | `Update` | `LateUpdate` | `FixedUpdate` | `GameTickCount` |
|---|---|---|---|---|---|---|
| Natural headless steady state | true | 1 | 24.85 /s | 24.85 /s | 49.89 /s | **0** |
| Explicitly unpaused | false | 1 | 24.99 /s | 24.99 /s | 50.02 /s | 1.91 /s |
| `SetGamePause(true)` actual transition | true | 0 | 24.94 /s | 24.94 /s | **0** | **0** |

Consequences for a headless hook:

- **Pause state changes nothing about `Update` or `LateUpdate`,** at either level. Their rate is identical to five significant figures across all three regimes.
- **`FixedUpdate` is gated on `timeScale`, not on `IsGamePaused`.** It keeps running at 50 Hz on a normally "paused" headless server, and stops dead the moment something drives a real `false` to `true` transition through `SetGamePause`. A `FixedUpdate`-based pump is therefore usually fine headless and occasionally not, with no log line either way. Verified twice: `GameManager.FixedUpdate` froze at 7534 for 55 s in one run and a plugin-owned object's `FixedUpdate` froze at 7838 for 39 s in another, both while `Update` continued at 25 Hz.
- **`GameTickCount == 0` is the reliable simulation-liveness flag,** and it reads 0 in both paused regimes.

## Patch timing on the dedicated server: patching a static method at plugin Awake can poison its type
<!-- verified: 0.2.6428.27798 @ 2026-08-14 -->

Harmony has to resolve and prepare the declaring type to patch a static method, which runs that type's static constructor. At `BepInEx/plugins/` chainloader `Awake` time that is `Time.frameCount == 0` with no scene loaded and a null graphics device, and a type initializer that is not safe there fails permanently: the CLR caches a failed type initializer forever and rethrows `TypeInitializationException` on every subsequent access.

Observed on `Assets.Scripts.Objects.BatchRenderer`. Patching `BatchRenderer.RenderAll` from a plugin `Awake` threw `HarmonyException: IL Compile Error (unknown location)` and left the type poisoned:

```
NullReferenceException: Object reference not set to an instance of an object
  at Assets.Scripts.Objects.BatchRenderer..cctor ()
Rethrow as TypeInitializationException: The type initializer for 'Assets.Scripts.Objects.BatchRenderer' threw an exception.
  at (wrapper dynamic-method) Assets.Scripts.GameManager.DMD<Assets.Scripts.GameManager::Update>(Assets.Scripts.GameManager)
```

`GameManager.Update` calls `BatchRenderer.RenderAll()` unconditionally near its end, so the throw then repeated every frame: 4,276 occurrences in one 192 s run, `WindTurbineGenerator.UpdateWind()` (the statement after it) never ran once, and every Harmony **postfix** on `GameManager.Update` was skipped, because a postfix does not run when the original method throws. A baseline run of the same server without the patch has zero `BatchRenderer` lines in its log, so this is caused by the patch timing, not by `-nographics` on its own.

The same patch applied from a main-thread context after `GameState` reached `Running` succeeded, and `RenderAll` then counted 24.85-25.06 calls per second like any other per-frame method. So:

- Patch instance methods on `GameManager` at `Awake` if you need an early pump; that is measured safe.
- **Defer static-method patches until a scene is up.** The first `SceneManager.sceneLoaded` callback, or `GameState.Running`, both work.
- A Harmony postfix is not a reliable counter of "was this method called". It counts "did this method return normally". When a postfix count freezes while the frame counter advances, look for an exception in the tail of the patched body before concluding the method stopped being called.

## Verification history

- 2026-05-26: page created. Sourced from a RuntimeProbe refactor that pulled the same hook out of PgpVerifyHelper and generalised it. Decompile cross-references at the line numbers above were re-confirmed against `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` during the same session. The crash stack quoted in "Threading constraint on the postfix" is the 2026-05-25 live repro recorded in `Research/Patterns/ThingEnumerationOffMainThread.md`.
- 2026-07-02: added "GameManager.Update manager loop: no per-manager exception isolation" and "GameTick loop, pause parking, and SetGamePause call sites", both verified line-by-line against `.work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs` during the dedicated-server boot investigation (a broken mod prefix at the KeyManager stage threw per-frame and starved every downstream manager). The `loaded 41 systems successfully` count is from that server's 2026-07-02 server.log. Pre-existing sections keep their 0.2.6228.27061 stamps and line numbers pending the version-migration pass; no contradiction between them and the new sections was found (the GameTick ThreadPool switch and the ElectricityTick RunSimulation guard reconfirm at 0.2.6403.27689 with new line numbers 204418 and 272091).
- 2026-07-02 (later, headless-tick investigation): added "Dedicated-server assembly only: DelayedStartupPause re-pauses 5 s after StartGame" plus the cross-reference bullet at the end of the call-site inventory. Source: ilspycmd decompile of the server binary (`.work/decomp/0.2.6403.27689/Assembly-CSharp.DedicatedServer.GameManager.decompiled.cs` lines 902-959) after a live InspectorPlus stack trace on `WorldManager.SetGamePause(true)` named `Assets.Scripts.GameManager.DelayedStartupPause` as the silent re-pauser on a fresh `-new Lunar` boot (exactly 8 ticks ran between the StartGame-postfix unpause and the re-pause). Confirmed additive against the existing inventory: the method is absent from the client decompile, so no prior claim was contradicted. Also live-verified the two countermeasures now in `Mods/InspectorPlus/InspectorPlus/HeadlessUnpausePatch.cs`: with the skip prefix plus watchdog active on the full 56-mod set, the same boot shape produced no re-pause, `GameTickCount` advanced continuously, and ScenarioRunner's 10-tick scenario fired.

- 2026-08-14 (pump measurement for the TestRig plugin merge): added "Dedicated-server assembly only: ImGuiManager ships as a stub with no LateUpdate" and "Headless dedicated server: the Unity player loop runs at ~24 Hz whether or not the world is paused". Method and thread evidence from three instrumented `-batchmode -nographics` runs on `TestRig/DedicatedServer/` at 0.2.6428.27798 (one with `Force Unpause Without Client` off, two with it on), plus a Mono.Cecil metadata read of both `Assembly-CSharp.dll` builds. The ImGuiManager section is purely additive; nothing on this page previously claimed the type had a working `LateUpdate` server-side. The player-loop section **contradicts the "MonoBehaviour.Update does not reliably fire after world load" and "the dispatcher's PollLoop coroutine never advances past its first yield" bullets under "Why hook ElectricityTick for diagnostic plugins"** (stamped 0.2.6228.27061), which were carried from repo lore rather than measured. Those bullets are left standing pending the fresh-validator protocol in `WORKFLOW.md` Rule 3; the conflict and the probable reconciliation are recorded under Open questions.

- 2026-08-14 (Rule 3 fresh validator, conflict resolved). Conflict on "does a plugin's `MonoBehaviour.Update` fire on a headless dedicated server after world load". Previous claim: it does not fire, and a `DontDestroyOnLoad` dispatcher's poll coroutine never advances past its first yield. New finding: it fires at ~24 Hz. Fresh validator verdict: **neither as stated. The player loop is healthy; the plugin's GameObject is destroyed at `Time.frameCount == 0`.** Evidence, four instrumented `-batchmode -nographics` runs at 0.2.6428.27798 (`-new Lunar`, no client, 5 s sampling): inside `BaseUnityPlugin.Awake` the process reports `frame=0`, `sceneCount=0`, `activeScene=''`, with the plugin already on `BepInEx_Manager` in scene `DontDestroyOnLoad`; 135-219 ms later, still at frame 0, `OnDisable` and `OnDestroy` fire for the plugin component and for both objects it created (one `DontDestroyOnLoad`, one not), the unnamed bootstrap scene unloads, and only then does `Splash` load; `Start` is never reached and `Update`, `LateUpdate` and `FixedUpdate` stay at 0 for the whole process, across runs of 192 s, 252 s and 312 s. An equivalent object recreated from the first `SceneManager.sceneLoaded` callback reached `Update` count 5867 at `Time.frameCount` 5867, missing no frame. Corroborated in-process by `ClientDriver`'s own "plugin component destroyed (count=1)" log line, and explained by BepInEx 5.4.23.5 starting its chainloader from `UnityEngine.Application`'s static constructor, before any scene exists. StationeersLaunchPad mods are immune because `LoadedMod.LoadEntrypoints()` builds its own per-mod `DontDestroyOnLoad` GameObject at mod-load time; four LaunchPad mod plugin components were measured ticking at full frame rate. Result: the two lore bullets under "Why hook ElectricityTick for diagnostic plugins" rewritten and that section restamped to 0.2.6428.27798; the plugin-owned rows in the ~24 Hz table qualified with "recreated after boot", because as written they invited exactly the wrong reading; full mechanism and the fix recorded on `../Patterns/MainThreadDispatcher.md`.

- 2026-08-14 (same validator pass, additive). Added "A 'paused' headless server usually still has Time.timeScale at 1" and "Patch timing on the dedicated server: patching a static method at plugin Awake can poison its type". The first refines, without contradicting, the existing quote of `SetGamePause` under "GameTick loop, pause parking, and SetGamePause call sites": the assignment `Time.timeScale = (pauseGame ? 0f : 1f)` is real but sits behind `if (IsGamePaused != pauseGame)`, and on a fresh headless boot the flag is already `true` when `DelayedStartupPause` calls `SetGamePause(true)`, so the scale is never dropped and `FixedUpdate` keeps running at 50 Hz on a "paused" server. Measured across three regimes on one run, 55 to 85 s each. The second section records a self-inflicted failure from this pass that is worth not repeating: patching `Assets.Scripts.Objects.BatchRenderer.RenderAll` from plugin `Awake` ran its static constructor at frame 0 under a null graphics device, it threw, .NET cached the failure permanently, and `GameManager.Update` then threw at that call site 4,276 times in 192 s with every postfix on `Update` skipped. A baseline run of the same server without the patch has zero `BatchRenderer` lines; the same patch applied after `GameState.Running` worked and counted normally.

## Open questions

- Exact method signature for `AtmosphericsManager`'s per-tick driver. The class inherits from `ThreadedManager`; identifying the override at the class top-of-body would let RuntimeProbe register an atmospheric-tick postfix without trial and error. Low priority; ElectricityTick is sufficient for current scenarios.
- Not measured: whether the frame-0 destruction of `BepInEx_Manager` also occurs on the game client build. Every run in the 2026-08-14 validator pass was `-batchmode -nographics` on the dedicated server. The mechanism is not obviously server-specific, but the client case is unverified; the same entry sits on `../Patterns/MainThreadDispatcher.md`.
