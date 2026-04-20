---
title: ModLoadSequence
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/StationpediaPlus/PLAN.md:259-272
  - Plans/StationpediaPlus/PLAN.md:259-269
related:
  - ./StationpediaPageRendering.md
  - ../Patterns/MainThreadDispatcher.md
tags: [launchpad, unity, threading]
---

# ModLoadSequence

When `Prefab.OnPrefabsLoaded` and `OnAllModsLoaded` fire relative to `Stationpedia.Regenerate`, and why Unity API calls from inside `OnAllModsLoaded` are safe without a main-thread dispatch.

## OnPrefabsLoaded / OnAllModsLoaded main-thread timing
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`Prefab.OnPrefabsLoaded` fires on the Unity main thread (runs synchronously inside game's main-thread loading sequence around game.cs:59080-59090, before `Stationpedia.Regenerate` at line 59090). `OnAllModsLoaded` is therefore main-thread; all Unity API calls from within it are safe without dispatching.

PowerTransmitterPlus has a `MainThreadDispatcher` singleton MonoBehaviour for enqueuing actions from ThreadPool-run PowerTick contexts to the main thread (used by the distance-cost multiplayer sync, not by Stationpedia integration). The StationpediaPlus library does not need this; all its work happens on main thread during `OnAllModsLoaded` and during the main-thread-driven `Regenerate` / `ChangeDisplay` paths.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; F0219c is the primary source per MigrationMap §5.1. F0246 is a duplicate extraction that merges here.

## Open questions

None at creation.
