---
title: OnServer
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-29
sources:
  - Mods/SprayPaintPlus/RESEARCH.md:120-126
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: OnServer
related:
  - ./Human.md
  - ./Thing.md
  - ./Structure.md
  - ../GameSystems/Explosions.md
tags: [network]
---

# OnServer

Vanilla static facade for server-side mutation entry points. Callers funnel gameplay actions (paint, damage, attack) through its methods so the server remains the authoritative simulator.

## SetCustomColor and AttackWith paths
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0029b.

`OnServer.SetCustomColor(...)` is called when a player paints something. `OnServer.AttackWith(attackParent, ...)` is the local path (host or single-player). `AttackWithMessage.Process(hostId)` is the remote client path. Both eventually reach `OnServer.SetCustomColor` if the attack involves a spray can.

## Destroy(Thing) -- the canonical low-level removal
<!-- verified: 0.2.6228.27061 @ 2026-04-29 -->

```csharp
public static void Destroy(Thing thing)
{
    if (!GameManager.RunSimulation && GameManager.GameState == GameState.Running)
        ConsoleWindow.PrintError("OnServer.Destroy called on client for " + (thing ? thing.DisplayName : "unknown"));
    if ((bool)thing && (bool)thing.GameObject)
    {
        thing.GameObject.DestroyGameObject();   // Unity Destroy on the GameObject
        thing.BeingDestroyed = true;
    }
}
```

`DestroyGameObject()` destroys the Unity GameObject, which fires `Thing.OnDestroy()` (and `Structure.OnDestroy()` for structures). `OnServer.Destroy` itself does no item-dropping and no networking; `Thing.OnDestroy()` does the cleanup: sets `BeingDestroyed`, stops audio, queues a `DestroyEvent` for clients when `NetworkManager.IsServer && NetworkServer.HasClients()`, deregisters from every manager (`ElectricityManager`, `AtmosphericsManager`, `OcclusionManager`, `LightManager`, transmitters, etc.), `ReleaseThing()` / `OnReleaseReagents()`, removes colliders from `Thing._colliderLookup`, and handles slot occupants -- with the default `Thing.DestroyChildrenOnDead == true`, children are destroyed with the parent (`DestroyChildren(transform)`); when set false, occupants are moved to the world via `OnServer.MoveToWorld`. `Structure.OnDestroy()` additionally does `GridController.Deregister(this)`, removes from `GridController.AllServerTickStructures`, and cleans up the batched renderer.

Server-authoritative: it logs an error if called on a client while the game is running. Safe for a mod to call directly on any Thing (including `Structure`, `Frame`, `Wall`, `SmallGrid`, machines) **on the server side only** -- it bypasses the build-stage walk and the broken-mesh wreckage early-return, so structures with a broken-mesh build state actually disappear rather than convert to wreckage. There is no type gate and no `Indestructable` check inside `Destroy` itself (the `Indestructable` check lives in `Structure.OnDamageDestroyed`, the *damage* path, not here). A threaded caller should use `Thing.DestroyFromThread()` (it does `await UniTask.SwitchToMainThread()` then `OnServer.Destroy`). Related: `Thing.Delete(Thing)` (see `./Thing.md`) is the higher-level "a tool deleted me" path that recursively `Delete`s slot occupants and, on a pure client, sends a `DestroyThingRequest` to the server instead of calling `Destroy`; `Structure.OnDamageDestroyed()` (see `./Structure.md`) is the "damage maxed out" path that adds wreckage spawning and the construction-event broadcast before ending in `OnServer.Destroy`.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-29 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0029b. No conflicts.
- 2026-04-29: added "Destroy(Thing) -- the canonical low-level removal" section from a direct decompile of `OnServer.Destroy` and `Thing.OnDestroy` / `Structure.OnDestroy` (game version 0.2.6228.27061). Additive only; no existing content changed.

## Open questions

None at creation.
