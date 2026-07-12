---
title: OnServer
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-13
sources:
  - Mods/SprayPaintPlus/RESEARCH.md:120-126
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: OnServer
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 39504 (class), 39664-39723 (Interact overloads), 304374 + 304777-304785 (Interactable.QueuedInteractions + DoQueuedInteractions), 205154-205170 (GameManager.Update drain gate)
related:
  - ./Human.md
  - ./Thing.md
  - ./Structure.md
  - ./Device.md
  - ../GameSystems/Explosions.md
  - ../GameSystems/PowerTickThreading.md
tags: [network, threading]
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

## Interact: RunSimulation gate, worker-thread queueing, and the destroyed-parent silent no-op
<!-- verified: 0.2.6403.27689 @ 2026-07-13 -->

`OnServer` is a static class (decompile 39504 at 0.2.6403.27689). The `Interact` family is the funnel every replicated interactable-state write goes through (`OnOff`, `Powered`, `Error`, `Open`, `Mode`, ...; the whole-game `Powered` writer census is on [Device](./Device.md)). Verbatim (39664-39723):

```csharp
public static void Interact(Thing thing, InteractableType interactableType, int state, bool skipAnimation = false)   // 39664
{
    if ((object)thing == null)
    {
        return;
    }
    Interactable interactable = null;
    foreach (Interactable interactable2 in thing.Interactables)
    {
        if (interactable2.Action == interactableType)
        {
            interactable = interactable2;
            break;
        }
    }
    if (interactable != null)
    {
        Interact(interactable, state, skipAnimation);
    }
}

public static void Interact(InteractionInstance interactionInstance)   // 39685
{
    Interact(interactionInstance.Thing, interactionInstance.Action, interactionInstance.State, interactionInstance.SkipAnimation);
}

public static void Interact(Interactable interactable, int state, bool skipAnimation = false)   // 39690
{
    if (!GameManager.RunSimulation || interactable == null)
    {
        return;
    }
    if (ThreadedManager.IsThread)
    {
        lock (Interactable.QueuedInteractions)
        {
            Interactable.QueuedInteractions.Enqueue(new InteractionInstance(interactable.Parent, interactable.Action, state, skipAnimation));
            return;
        }
    }
    if (GameManager.GameState != GameState.Running || (object)interactable.Parent == null)
    {
        return;
    }
    if (skipAnimation && interactable.State != state)
    {
        interactable.Interact(state);
    }
    else if ((bool)interactable.Parent && interactable.Parent.isActiveAndEnabled)
    {
        if (interactable.Parent.AllowInteraction)
        {
            interactable.Interact(state, skipAnimation);
        }
        else
        {
            interactable.InteractWhenReady(state, skipAnimation);
        }
    }
}
```

Gate by gate:

- **Clients: the whole funnel is inert.** `!GameManager.RunSimulation` returns first (`RunSimulation => !NetworkManager.IsClient`, 203945), consistent with the server-authoritative framing in the page intro.
- **Worker threads: deferred at least one main-thread frame via a queue.** When called with `ThreadedManager.IsThread` true (any thread but the Unity main thread, 217769), the call enqueues an `InteractionInstance` capturing `interactable.Parent` plus action/state/skipAnimation, and returns. The queue is `Interactable.QueuedInteractions` (`static Queue<InteractionInstance>`, 304374); `Interactable.DoQueuedInteractions` (304777-304785) drains it under the same lock, calling `OnServer.Interact(instance)` per entry, which re-resolves the interactable off the captured Thing through the 39664 overload. The single drain site is `GameManager.Update`, gated on `RunSimulation && !WorldManager.IsGamePaused` (205154-205170). This queue is one of vanilla's two marshaling mechanisms; the other is the per-method `await UniTask.SwitchToMainThread()` wrappers (`Device.SetPowerFromThread` 371648-371652, `Thing.DestroyFromThread`, `Transformer.SetKnobFromThread`), which land on the main thread first and then enter this method with `IsThread` false.
- **Destroyed or missing parent: silent no-op.** On the main-thread path, a reference-null `Parent` returns at 39704-39707 (as does any `GameState != Running`, so interactions during load and teardown windows are dropped). A Unity-DESTROYED parent is reference-non-null but fails the alive check `(bool)interactable.Parent` at 39712 (Unity fake-null), and an inactive one fails `isActiveAndEnabled`; either way the state write is silently discarded. Note the asymmetry: the `skipAnimation && interactable.State != state` branch (39708-39711) runs BEFORE the alive check and is guarded only by the reference-null test, so a skip-animation interact can still reach `interactable.Interact(state)` against a fake-null parent.

Consequence for marshaled `Powered` writes: `PowerTick.ApplyState` decides power flips on the game-tick worker and marshals them via `Device.SetPowerFromThread -> await UniTask.SwitchToMainThread() -> SetPower -> OnServer.Interact(InteractPowered, 0|1)` (call sites 271933 / 271938; bodies 371648-371652 and 371640-371646; `skipAnimation` defaults to false). If the device is deconstructed between the worker-thread decision and the main-thread continuation, the write lands in the destroyed-parent no-op above and is dropped; a late `Powered` write cannot act on a dead device. Queued interactions from other worker-thread callers get the same protection one frame later: the queue holds the Thing reference across the frame, and the 39664 overload plus these gates drop it safely if the Thing died meanwhile.

## Verification history
<!-- verified: 0.2.6403.27689 @ 2026-07-13 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0029b. No conflicts.
- 2026-04-29: added "Destroy(Thing) -- the canonical low-level removal" section from a direct decompile of `OnServer.Destroy` and `Thing.OnDestroy` / `Structure.OnDestroy` (game version 0.2.6228.27061). Additive only; no existing content changed.
- 2026-07-13: added "Interact: RunSimulation gate, worker-thread queueing, and the destroyed-parent silent no-op" (game version 0.2.6403.27689) from a fresh-validation pass on a decompile-claim audit. Verbatim `Interact` overloads (39664-39723) with the gate ladder: client early-out via `RunSimulation` (203945); worker-thread enqueue into `Interactable.QueuedInteractions` (304374) under lock, drained once per frame by `Interactable.DoQueuedInteractions` (304777-304785) from `GameManager.Update` (205154-205170); `GameState` / reference-null return (39704-39707); Unity-alive check `(bool)Parent && isActiveAndEnabled` (39712) guarding only the animated branch. Recorded the marshaled-`Powered`-write consequence (`SetPowerFromThread` continuations dropped silently when the device died in between). Additive; no existing content changed. Cross-links the Powered writer census on [Device](./Device.md).

## Open questions

None at creation.
