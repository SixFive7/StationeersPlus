---
title: StationAutoSave
type: GameSystems
created_in: 0.2.6403.27689
verified_in: 0.2.6403.27689
verified_at: 2026-07-02
sources:
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: line 267571 (StationAutoSave), 267592 (ResetAutoSave), 267612 (AutoSaveNow), 264928 (SaveHelper.DoAutoSave), 264943 (SaveHelper.PrepareToSave)
related:
  - ./SimulationTickDriverHooks.md
  - ../Workflows/StationeersLaunchPadDedicatedServer.md
tags: [save-load, threading]
---

# StationAutoSave

How the autosave timer works, what gates a fire, and why a fresh `-new` dedicated-server world logs `Save Failed: Folder name is empty.` every interval instead of autosaving.

## Timer architecture: wall-clock, not game time
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

`StationAutoSave` (static class, 0.2.6403.27689 client decompile line 267571) schedules with a `System.Timers.Timer`, so the countdown runs on WALL-CLOCK time. `Time.timeScale = 0` (a paused world, or a tool freezing the sun) does not slow or stop the schedule; pause is only consulted at fire time (next section).

```csharp
	public static class StationAutoSave
	{
		private static readonly System.Timers.Timer _autoSaveTimer = new System.Timers.Timer();
```

```csharp
		private static async void TimerElapsed(object sender, ElapsedEventArgs e)
		{
			await UniTask.SwitchToMainThread();
			AutoSaveNow();
		}
```

`Timer.Elapsed` fires on a ThreadPool thread; the handler marshals to the main thread through the UniTask player loop, which runs on a headless server even while the game tick is parked (see `SimulationTickDriverHooks.md`).

`ResetAutoSave()` (line 267592) stops the timer and, when `Settings.CurrentData.AutoSave && !GameManager.IsTutorial && !GameManager.IsNewTutorial`, restarts it with `Interval = Settings.CurrentData.SaveInterval * 1000`. In batch mode it prints the `Auto save stopped` / `Auto save started (<interval>)` console lines. Every call restarts the countdown from zero; it is called from `GameManager.StartGame`, `World.NewAsync`, and the load paths, so back-to-back `stopped`/`started` pairs at world start are normal.

## Gates at fire time
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

```csharp
		private static void AutoSaveNow()
		{
			if (!GameManager.IsTutorial && !GameManager.IsNewTutorial && !WorldManager.IsGamePaused && GameManager.GameState == GameState.Running && !Assets.Scripts.Networking.NetworkManager.IsClient)
			{
				AutoSaveTask().Forget();
			}
		}
```

A PAUSED world silently skips the autosave (no log line at all): the timer keeps running, the fire is discarded. Diagnostic consequence: on a headless server, ANY autosave-attempt line at the interval mark (success or failure) is positive evidence the world was unpaused and `Running` at that moment.

`AutoSaveTask` awaits `SaveHelper.AutoSave(XmlSaveLoad.Instance.CurrentStationName, ...)` and prints the `SaveResult` message on failure via `ConsoleWindow.PrintError`.

## Fresh -new worlds cannot autosave: empty station name
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

On a dedicated server started with `-new <Map>`, `XmlSaveLoad.Instance.CurrentStationName` is empty until a first named save assigns one. The autosave then fails name validation before any save work starts, and the server logs, every `SaveInterval` seconds:

```
15:08:24: Save Failed: Folder name is empty.
```

(verbatim from a 2026-07-02 dedicated-server run at 0.2.6403.27689; world created 15:03:24, `SaveInterval` 300, failure logged exactly at +300 s). The failure happens before `SaveHelper.PrepareToSave`, so `PauseGameTick` is never called and the simulation is not disturbed: an InspectorPlus `PauseGameTick` tracer recorded zero calls across the interval.

Consequences:

- A `-new` world produces NO real autosaves until it gets a name. The stdin console `save "<name>"` path would assign one, but stdin console commands are a no-op on the batch-mode dedicated server (observed at 0.2.6228.27061 and re-confirmed at 0.2.6403.27689: `save` queued via the launcher control file produced no save folder, no log response). Starting with `-load <SaveName>` instead gives the world a name from the start and autosaves work normally.
- When a test plan uses "an AutoSave line" as the world-is-ticking readiness marker (`DedicatedServer/CLAUDE.md`, "First-autosave grep"), on a `-new` world watch for the `Save Failed: Folder name is empty.` line instead; it is emitted at the same cadence and implies the same unpaused-and-running state.

## Verification history

- 2026-07-02: page created during the headless-tick investigation. Timer/gate code quoted verbatim from the 0.2.6403.27689 client decompile (lines 267571-267633); the empty-folder failure and its exact +interval timing observed live on the dedicated server the same day. The "stdin save no-op" cross-check is the launcher `-Save` command timing out with no `Saved` line and no folder appearing under `data/saves/`.

## Open questions

- Which code path assigns `CurrentStationName` on the dedicated server besides `-load` (for example, whether the first client-driven manual save or an admin `serverrun save` sets it). Not needed for current test flows; `-load` covers them.
