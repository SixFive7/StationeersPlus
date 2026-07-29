---
title: ModLoadSequence
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-29
sources:
  - Plans/StationpediaPlus/PLAN.md:259-272
  - Plans/StationpediaPlus/PLAN.md:259-269
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs:203945 (GameManager.RunSimulation), :290777 (GameState enum), :204577 / :213235 (GameState.Running), :213543 (NetworkServer), :278421 (ChatMessage), :322926 / :322971 (Prefab.LoadAll), :59725 (LoadGameDataAsync), :60874-60899 (WorldManager.SetGamePause / EnablePause), :204588 (GameManager.StartGame), :39504 (OnServer), :43646 (KeyManager)
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
- The fix for a banner that must be seen is to wait for a world before the first print. Use `GameManager.GameState`, NOT `GameManager.RunSimulation`; see the two paragraphs below for why the obvious-looking one is wrong. Spinning on `while (GameManager.GameState == GameState.None) yield return null;` is the shape.

Related timing fact for any coroutine anchored here: `WaitForSeconds` is timeScale-scaled, and the assembly's only `Time.timeScale = 0f` assignment is `WorldManager.SetGamePause(bool)` at `:60899`, reachable through `EnablePause` (`:60874-60884`) which is itself gated on `GameManager.RunSimulation`. `GameManager.StartGame()` restores it to `1f` at `:204588`. So a coroutine running at the main menu is never stalled by a pause, but one that has waited for `RunSimulation` can be, and should use `WaitForSecondsRealtime` if its cadence matters.

**`GameManager.GameState` is the usable phase signal**, and it is public. The enum is `GameState : byte { None, Joining, Waiting, Running, Loading, Paused }` (`:290777`). It sits at `None` through boot and whenever no game is running, moves to `Loading` / `Joining` as a world comes up (`:268516`, `:213109`), and reaches `Running` at `:204577` and `:213235`. Returning to the main menu sets it back to `None` (`:60544`, `:213007`, `:290834`). So `GameManager.GameState == GameState.None` is a reliable "no game is running, we are at boot or the menu" test, which is what a mod wants for deciding whether a startup surface is still worth writing to.

**`GameManager.RunSimulation` is NOT that signal, despite reading like it.** It is `public static bool RunSimulation => !Assets.Scripts.Networking.NetworkManager.IsClient;` (`:203945`), i.e. purely "am I not a multiplayer client". It is **true at the main menu**, true during boot, and true in single-player before any world exists. It is the correct gate for "host-only work" and the wrong gate for "a game is running"; several mods in this repo use it correctly for the former, and it would silently misbehave if used for the latter.

Namespace placements confirmed while tracing the above, all easy to get wrong because the names suggest a different home. `KeyManager` (`:43646`) and `OnServer` (`:39504`) are declared in the **global** namespace, before the first `namespace` block, so neither needs a `using` at all. Of the types a console or chat helper needs: `ConsoleWindow` is in `Assets.Scripts` (`:221811`), `NetworkServer` is also in `Assets.Scripts` (`:213543`) rather than `Assets.Scripts.Networking`, while `ChatMessage` (`:278421`) and `NetworkChannel` ARE in `Assets.Scripts.Networking`, and `GameState` (`:290777`) is in `Assets.Scripts.GridSystem` rather than alongside the `GameManager` that exposes it. The split is not guessable; verify before writing a `using` alias.

## Verification history
<!-- verified: 0.2.6403.27689 @ 2026-07-29 -->

- 2026-07-29 (second pass): extended the same section with the phase signal a mod should actually use. `GameManager.GameState` is public, its enum is `None / Joining / Waiting / Running / Loading / Paused`, and `None` covers boot and the main menu. Recorded the trap that `GameManager.RunSimulation` is only `!NetworkManager.IsClient` and is therefore true at the main menu, so it answers "am I the host" and not "is a game running". Also recorded the non-guessable namespace placements for the types a console or chat helper needs (`NetworkServer` in `Assets.Scripts`, `ChatMessage` and `NetworkChannel` in `Assets.Scripts.Networking`, `GameState` in `Assets.Scripts.GridSystem`), all three found by compiler error while writing `Patterns/Console/PlayerMessage.cs`.
- 2026-07-29: added the "OnPrefabsLoaded fires at boot, not at world load" section against 0.2.6403.27689. Purely additive; the existing main-thread claim is unaffected and was not re-verified against the new decompile, so its 0.2.6228.27061 stamp stands. Found while reviewing a mod-conflict banner that paced itself with `WaitForSeconds` from this event and therefore played out entirely at the main menu. Also records the single `Time.timeScale = 0f` assignment site and its `RunSimulation` gate, and the fact that `KeyManager` and `OnServer` live in the global namespace rather than `Assets.Scripts`.
- 2026-04-20: page created from the Research migration; F0219c is the primary source per MigrationMap §5.1. F0246 is a duplicate extraction that merges here.

## Open questions

None at creation.
