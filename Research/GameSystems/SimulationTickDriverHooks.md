---
title: Simulation tick driver hooks
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-02
sources:
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: line 254905 (ElectricityManager.ElectricityTick), 417811 (AtmosphericsManager : ThreadedManager), 187543 (GameManager.RecordGameTick), 189381 (GameManager.StartGameTick), 189076 (GameManager.GameTickPaused)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: line 205154 (GameManager.Update), 203880 (GameManager.Managers), 204387 (GameManager.GameTick), 204363 (StartGameTick), 203823 (DefaultTickSpeedMs), 60520 (WorldManager.StartWorld), 60886 (WorldManager.SetGamePause), 272091 (ElectricityManager.ElectricityTick)
  - DedicatedServer/install/rocketstation_DedicatedServer_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.GameManager.StartGame + DelayedStartupPause (decompiled to .work/decomp/0.2.6403.27689/Assembly-CSharp.DedicatedServer.GameManager.decompiled.cs lines 902-959)
related:
  - ../GameClasses/PowerTick.md
  - ../GameClasses/GameManager.md
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

## Verification history

- 2026-05-26: page created. Sourced from a RuntimeProbe refactor that pulled the same hook out of PgpVerifyHelper and generalised it. Decompile cross-references at the line numbers above were re-confirmed against `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` during the same session. The crash stack quoted in "Threading constraint on the postfix" is the 2026-05-25 live repro recorded in `Research/Patterns/ThingEnumerationOffMainThread.md`.
- 2026-07-02: added "GameManager.Update manager loop: no per-manager exception isolation" and "GameTick loop, pause parking, and SetGamePause call sites", both verified line-by-line against `.work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs` during the dedicated-server boot investigation (a broken mod prefix at the KeyManager stage threw per-frame and starved every downstream manager). The `loaded 41 systems successfully` count is from that server's 2026-07-02 server.log. Pre-existing sections keep their 0.2.6228.27061 stamps and line numbers pending the version-migration pass; no contradiction between them and the new sections was found (the GameTick ThreadPool switch and the ElectricityTick RunSimulation guard reconfirm at 0.2.6403.27689 with new line numbers 204418 and 272091).
- 2026-07-02 (later, headless-tick investigation): added "Dedicated-server assembly only: DelayedStartupPause re-pauses 5 s after StartGame" plus the cross-reference bullet at the end of the call-site inventory. Source: ilspycmd decompile of the server binary (`.work/decomp/0.2.6403.27689/Assembly-CSharp.DedicatedServer.GameManager.decompiled.cs` lines 902-959) after a live InspectorPlus stack trace on `WorldManager.SetGamePause(true)` named `Assets.Scripts.GameManager.DelayedStartupPause` as the silent re-pauser on a fresh `-new Lunar` boot (exactly 8 ticks ran between the StartGame-postfix unpause and the re-pause). Confirmed additive against the existing inventory: the method is absent from the client decompile, so no prior claim was contradicted. Also live-verified the two countermeasures now in `Mods/InspectorPlus/InspectorPlus/HeadlessUnpausePatch.cs`: with the skip prefix plus watchdog active on the full 56-mod set, the same boot shape produced no re-pause, `GameTickCount` advanced continuously, and ScenarioRunner's 10-tick scenario fired.

## Open questions

- Exact method signature for `AtmosphericsManager`'s per-tick driver. The class inherits from `ThreadedManager`; identifying the override at the class top-of-body would let RuntimeProbe register an atmospheric-tick postfix without trial and error. Low priority; ElectricityTick is sufficient for current scenarios.
