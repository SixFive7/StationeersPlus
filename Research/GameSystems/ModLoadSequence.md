---
title: ModLoadSequence
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-29
sources:
  - Plans/StationpediaPlus/PLAN.md:259-272
  - Plans/StationpediaPlus/PLAN.md:259-269
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs:322926 / :322971 (Prefab.LoadAll), :59725 (LoadGameDataAsync), :60874-60899 (WorldManager.SetGamePause / EnablePause), :204588 (GameManager.StartGame), :39504 (OnServer), :43646 (KeyManager)
related:
  - ./StationpediaPageRendering.md
  - ../Patterns/MainThreadDispatcher.md
  - ../GameClasses/ConsoleWindow.md
  - ../Patterns/InGameConsoleOutput.md
tags: [launchpad, unity, threading]
---

# ModLoadSequence

When `Prefab.OnPrefabsLoaded` and `OnAllModsLoaded` fire relative to `Stationpedia.Regenerate`, and why Unity API calls from inside `OnAllModsLoaded` are safe without a main-thread dispatch.

## OnPrefabsLoaded / OnAllModsLoaded main-thread timing
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`Prefab.OnPrefabsLoaded` fires on the Unity main thread (runs synchronously inside game's main-thread loading sequence around game.cs:59080-59090, before `Stationpedia.Regenerate` at line 59090). `OnAllModsLoaded` is therefore main-thread; all Unity API calls from within it are safe without dispatching.

PowerTransmitterPlus has a `MainThreadDispatcher` singleton MonoBehaviour for enqueuing actions from ThreadPool-run PowerTick contexts to the main thread (used by the distance-cost multiplayer sync, not by Stationpedia integration). The StationpediaPlus library does not need this; all its work happens on main thread during `OnAllModsLoaded` and during the main-thread-driven `Regenerate` / `ChangeDisplay` paths.

## OnPrefabsLoaded fires at boot, not at world load
<!-- verified: 0.2.6403.27689 @ 2026-07-29 -->

This is the trap that matters for anything a mod wants a player to *see*. `Prefab.OnPrefabsLoaded` is invoked from `Prefab.LoadAll()` (`Assembly-CSharp.decompiled.cs:322971`, guarded by `if (PrefabsGameObject != null)` at `:322926`), which is awaited from `LoadGameDataAsync()` at `:59725`. That runs during process startup with `MainMenuCanvas.enabled = false` and the ImGui loading screen up. It completes **before the main menu appears**, and long before any world exists.

So "prefabs loaded" is not "the player is in a game". A mod that anchors player-facing output to this event is writing to a screen nobody is reading:

- Anything printed to `ConsoleWindow` here has aged off the closed-console overlay (5 seconds of `activeTime`, see [ConsoleWindow](../GameClasses/ConsoleWindow.md)) long before the player finishes picking a save. It survives only in F3 scrollback.
- A coroutine started here that paces itself with `WaitForSeconds` runs its whole schedule at the main menu. `Mods/EquipmentPlus/EquipmentPlus/Plugin.cs` `RepeatWarning` is the worked example: a six-line, 25-second mod-conflict banner that is fully spent before the player loads a world.
- `GameManager.RunSimulation` is the gate that actually means "a world is running". Spinning on `while (!GameManager.RunSimulation) yield return null;` before the first print is the fix for a banner that must be seen.

Related timing fact for any coroutine anchored here: `WaitForSeconds` is timeScale-scaled, and the assembly's only `Time.timeScale = 0f` assignment is `WorldManager.SetGamePause(bool)` at `:60899`, reachable through `EnablePause` (`:60874-60884`) which is itself gated on `GameManager.RunSimulation`. `GameManager.StartGame()` restores it to `1f` at `:204588`. So a coroutine running at the main menu is never stalled by a pause, but one that has waited for `RunSimulation` can be, and should use `WaitForSecondsRealtime` if its cadence matters.

Two namespace facts confirmed while tracing the above, both easy to get wrong because the names look like they belong to `Assets.Scripts`: `KeyManager` (`:43646`) and `OnServer` (`:39504`) are declared in the **global** namespace, before the first `namespace` block. Neither needs a `using` at all.

## Verification history
<!-- verified: 0.2.6403.27689 @ 2026-07-29 -->

- 2026-07-29: added the "OnPrefabsLoaded fires at boot, not at world load" section against 0.2.6403.27689. Purely additive; the existing main-thread claim is unaffected and was not re-verified against the new decompile, so its 0.2.6228.27061 stamp stands. Found while reviewing a mod-conflict banner that paced itself with `WaitForSeconds` from this event and therefore played out entirely at the main menu. Also records the single `Time.timeScale = 0f` assignment site and its `RunSimulation` gate, and the fact that `KeyManager` and `OnServer` live in the global namespace rather than `Assets.Scripts`.
- 2026-04-20: page created from the Research migration; F0219c is the primary source per MigrationMap §5.1. F0246 is a duplicate extraction that merges here.

## Open questions

None at creation.
