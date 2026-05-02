---
title: GameManager
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-02
sources:
  - Mods/SprayPaintPlus/RESEARCH.md:100-102
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: GameManager
related:
  - ./Thing.md
tags: [save-load, ui]
---

# GameManager

Vanilla top-level game-manager singleton. Holds global state referenced by many subsystems.

## CustomColors list
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0027.

`GameManager.Instance.CustomColors` is a `List<CustomColor>` where each entry has a `.Normal` material. The index into this list is the canonical color identifier used throughout the mod.

## Load finalize chain
<!-- verified: 0.2.6228.27061 @ 2026-05-02 -->

`GameManager.StartGame()` is the post-deserialize entry point that flips the game from "loading" to playable. Body (verbatim, in order):

```csharp
public static async UniTask StartGame()
{
    GameState = GameState.Running;
    GameTime = Time.time;
    HelperHintsTextController.InitializePanel();
    UpdateThingsOnGameStart();
    StructureNetwork.StructureNetworksOnFinishedLoad();
    Rocket.OnFinishedLoad();
    SpaceMap.CleanUpOnFinishedLoad();
    // ... InventoryManager parent-brain hookup, time scale, FoV, UI cleanup,
    //     NetworkServer.Host()/PopulateHostClient(), StationAutoSave reset,
    //     OrbitalSimulation.OnGameStarted(), Steam rich presence ...
}
```

`UpdateThingsOnGameStart()` iterates the global registry and dispatches per-Thing finalization:

```csharp
public static void UpdateThingsOnGameStart()
{
    OcclusionManager.AllThings.ForEach(UpdateThingsOnGameStartAction);
}

private static readonly Action<Thing> UpdateThingsOnGameStartAction = delegate(Thing thing)
{
    if ((object)thing == null) return;
    thing.OnFinishedLoad();
    foreach (Interactable interactable in thing.Interactables)
    {
        if (interactable.JoinInProgressSync && (bool)interactable.Animator)
        {
            interactable.SetState();
            thing.OnFinishedInteractionSync(interactable);
        }
    }
};
```

Implications for a mod that needs to run a one-shot pass after the world has fully deserialized:

- `GameState` is already `Running` at the moment `UpdateThingsOnGameStart()` is invoked. A guard `GameManager.GameState != GameState.Running` is therefore not sufficient to distinguish "during load finalize" from "in-game running".
- `Thing.OnFinishedLoad` has been called on every Thing by the time `UpdateThingsOnGameStart()` returns. A `Postfix` on `GameManager.StartGame` (or on `UpdateThingsOnGameStart` itself) is the earliest reliable hook for "every Thing's transforms, rotations, and side-car-restored state are settled". Hooking individual `Thing.OnFinishedLoad` postfixes only sees its own state; partner Things may not have had their `OnFinishedLoad` run yet.
- `StructureNetwork.StructureNetworksOnFinishedLoad`, `Rocket.OnFinishedLoad`, `SpaceMap.CleanUpOnFinishedLoad` run sequentially after the per-Thing pass. A mod whose post-load pass depends on cable networks being formed should run after `StructureNetworksOnFinishedLoad`; for transform-only work, hooking after `UpdateThingsOnGameStart` is enough.
- `StartGame` is `async UniTask`. A Harmony `Postfix` returning before the awaits will run synchronously after the body up to the first await. The first `await` is `NetworkServer.Host()` (gated by `Settings.CurrentData.StartLocalHost || IsBatchMode`); for single-player and host-as-server, the Postfix fires after every synchronous step including `UpdateThingsOnGameStart`. For pure-client joining a remote host, `StartGame` may not be called the same way; the join path uses `NetworkClient.ProcessJoinData` instead (see `Research/Protocols/PlayerConnectedThingFindTiming.md`).

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-05-02 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0027. No conflicts.
- 2026-05-02: added "Load finalize chain" section with the verbatim `StartGame` body, `UpdateThingsOnGameStart` static, and `UpdateThingsOnGameStartAction` delegate. Sourced from the 0.2.6228.27061 Assembly-CSharp decompile (lines 188908, 189593, 189647). Documents the order in which post-deserialize callbacks fire so a mod-side one-shot post-load pass can pick the right hook (Postfix on `GameManager.StartGame` for transform-settled state across all Things; per-Thing `OnFinishedLoad` is too early when partners are involved).

## Open questions

None at creation.
