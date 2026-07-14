---
title: Interactable
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-07
sources:
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 285691-285832 (InteractableType enum), 285833-286051 (Interactable class header, fields, State getter/setter), 286328-286361 (Interact / WaitThenInteract / InteractWhenReady)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 39664-39723 (OnServer.Interact overloads incl. worker enqueue), 205154-205170 (GameManager.Update drain gate), 304374 (QueuedInteractions), 304777-304785 (DoQueuedInteractions), 304799 (InteractionInstance struct)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 39322-39360 (OnServer.Interact), 198342-198353 (NetworkClient.Interact)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 277765-277782 (Battery OnAtmosphericTick power-tick gate), 279285-279308 (combustion power-tick gate), 299160-299191 (Thing.OnOff getter/setter), 300436-300449 (Thing.OnInteractableStateChanged / OnInteractableUpdated)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 302253-302261 (DeserializeSave JoinInProgressSync loop), 303112-303129 (SetInteractableStateOnJoin / DeserializeInteractableOnJoin), 303291-303379 (BuildInteractableUpdate / ProcessUpdate / ProcessInteractableUpdate)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 350870-350881 (Device.OnInteractableUpdated), 386946-386964 (PowerReceiver.OnInteractableUpdated), 387163-387181 (PowerTransmitter.OnInteractableUpdated), 386861/387065/405441/373755 (class headers for PowerReceiver/PowerTransmitter/WirelessPower/ElectricalInputOutput hierarchy)
  - Investigated for the PowerTransmitterPlus beam on/off fix (hooking dish switch state changes on all peers).
related:
  - ./Device.md
  - ./Thing.md
  - ./WirelessPower.md
  - ../GameSystems/LightSources.md
  - ../GameSystems/NetworkUpdateFlags.md
  - ../Protocols/GameMessageFactory.md
tags: [network, prefab]
---

# Interactable

`Assets.Scripts.Inventory.Interactable : SlotDisplayBase` (line 285833) is the per-`Thing` state slot that vanilla uses for every interactable axis of a device: the on/off switch, the powered indicator, the error indicator, the mode/lock/import/export states, every button, and every inventory slot. A `Thing` holds a `List<Interactable> Interactables`; each entry has an `InteractableType Action` (which axis it represents) and an `int State` (its current value). Writing `Interactable.State` is the single funnel through which a state change becomes visible: it raises `Thing.OnInteractableStateChanged` and `Thing.OnInteractableUpdated`, and on the server marks the thing dirty for network replication.

This page documents how a state change propagates and fires `Thing.OnInteractableUpdated` on all peers (server, single-player host, and remote clients), which was the load-bearing question for a PowerTransmitterPlus beam fix that needs to react to dish on/off switch changes exactly once on every peer and never per power tick.

## InteractableType enum and the discriminator fields
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

`InteractableType` (line 285691) names every axis. The values relevant to power devices:

```csharp
public enum InteractableType
{
    Open,
    Slot1,
    // ... Slot2 .. Slot30 ...
    Button1,
    Button2,
    Button3,
    Button4,
    Button5,
    OnOff,
    Mode,
    Lock,
    Import,
    Export,
    Activate,
    Powered,
    Error,
    Export2,
    Color,
    Access,
    // ... Slot31 .. Slot109, Button6 .. Button17, Import2 ...
}
```

The discriminator for "which interactable changed" is the public field `Interactable.Action` (type `InteractableType`, line 285902):

```csharp
public InteractableType Action;
```

`Interactable.State` (`int`) carries the new value. Do NOT confuse the discriminator with `Interactable.OnOffState` (line 285866):

```csharp
public static int OnOffState = Animator.StringToHash("OnOff");
```

`OnOffState` is a static Unity `Animator` parameter-name hash (`Animator.StringToHash("OnOff")`), used to read/write the animator integer parameter. It is not the per-interactable discriminator. Each axis has its own hash constant on `Interactable` (`OnState`, `OffState`, `PoweredState`, `ErrorState`, `ModeState`, `LockState`, etc.); these are all animator parameter hashes, not `InteractableType` values.

## The State setter: fires OnInteractableUpdated unconditionally (no new != old guard)
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

`Interactable.State` (line 286009) is the central write path. The setter (lines 286027-286050):

```csharp
public int State
{
    get
    {
        if (JoinInProgressSync)
        {
            if (_hasAnimator && (bool)Animator)
            {
                if (!Animator.isInitialized)
                {
                    return _state;
                }
                return Animator.GetInteger(PropertyId);
            }
            return ParentInteractable?.State ?? _state;
        }
        return 0;
    }
    set
    {
        if (!Animator && ParentInteractable != null)
        {
            ParentInteractable.State = value;
            return;
        }
        int state = _state;
        _state = value;
        Parent.OnInteractableStateChanged(this, _state, state);
        if (Settings.SoundOn && AssociatedAudioEvents.Count > 0)
        {
            lock (_scheduledSoundEvents)
            {
                _scheduledSoundEvents.Enqueue(this);
            }
        }
        Parent.OnInteractableUpdated(this);
        if (Assets.Scripts.Networking.NetworkManager.IsServer)
        {
            Parent.NetworkUpdateFlags |= 2;
            IsDirty = true;
        }
    }
}
```

Load-bearing facts:

- There is **NO `new != old` guard** in the setter. It captures `old = _state`, assigns `_state = value`, then unconditionally calls `Parent.OnInteractableStateChanged(this, _state, old)` and `Parent.OnInteractableUpdated(this)`. Writing the same value the interactable already holds still raises both callbacks. The old value is passed to `OnInteractableStateChanged` (which drives the animator integer via `SetIntegerSafe`) but is not used to short-circuit anything.
- A switch sound is enqueued when `Settings.SoundOn && AssociatedAudioEvents.Count > 0` (lines 286037-286043), again with no value-change check.
- On the **server only** (`NetworkManager.IsServer`, lines 286045-286049), the setter sets `Parent.NetworkUpdateFlags |= 2` and `IsDirty = true`. Bit `2` (`0x2`) is the interactable-update flag (see [NetworkUpdateFlags](../GameSystems/NetworkUpdateFlags.md)); `IsDirty` marks this specific interactable for inclusion in the next delta. On a client (`IsServer == false`) this block is skipped, so applying a server-sent state on a client does NOT re-raise flag 2 (no echo back to the server).
- The early redirect (lines 286029-286033): if this interactable has no `Animator` of its own but has a `ParentInteractable`, the write is forwarded to the parent and the local body is skipped. This is the slot-hierarchy case (child slots delegating to a parent interactable) and is not the path power-state writes take.

Because there is no value-change guard, the thing that prevents per-tick churn is the **caller**, not the setter (see "Caller-side change gating" below).

## Interact / InteractWhenReady: how a state is normally written
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

`Interactable.Interact(int state, bool skipAnimation = true)` (line 286328) is the normal entry that writes `State` and then finalises:

```csharp
public void Interact(int state, bool skipAnimation = true)
{
    State = state;
    if (skipAnimation)
    {
        SetState();
    }
    Parent.OnFinishedInteractionSync(this);
}
```

So `Interact` does `State = state;` (going through the setter above, hence raising `OnInteractableUpdated`), optionally calls `SetState()` to push the value straight to the animator without playing the transition animation, then `Parent.OnFinishedInteractionSync(this)` (which re-caches animator interactable variables).

`InteractWhenReady(int state, bool skipAnimation)` (line 286358) is the deferred variant for when the parent is not yet ready to interact:

```csharp
private async UniTaskVoid WaitThenInteract(int state, bool skipAnimation)
{
    int frame = 0;
    CancellationToken cancelToken = Parent.GetCancellationTokenOnDestroy();
    while (!Parent.AllowInteraction && frame < 60)
    {
        await UniTask.NextFrame(cancelToken);
        frame++;
    }
    if (!cancelToken.IsCancellationRequested)
    {
        State = state;
        if (skipAnimation)
        {
            SetState();
        }
        Parent.OnFinishedInteractionSync(this);
    }
}

public void InteractWhenReady(int state, bool skipAnimation)
{
    WaitThenInteract(state, skipAnimation).Forget();
}
```

It waits up to 60 frames for `Parent.AllowInteraction`, then does the same `State = state;` -> `SetState()` -> `OnFinishedInteractionSync` sequence. Either way, the `State` setter (and therefore `OnInteractableUpdated`) runs.

## OnServer.Interact (host only) vs NetworkClient.Interact (request only)
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

Gameplay code does not call `interactable.Interact` directly; it calls the static `OnServer.Interact`, which is gated to the simulation authority. `OnServer.Interact(Interactable, int, bool)` (line 39327):

```csharp
public static void Interact(Interactable interactable, int state, bool skipAnimation = false)
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

Facts:

- `GameManager.RunSimulation` gate (line 39329): this method does nothing unless the local peer owns the simulation (host or single-player). On a remote client `RunSimulation` is false, so `OnServer.Interact` returns immediately. State changes are server-driven.
- Background-thread safety (lines 39333-39340): if called from a worker thread (e.g. inside a power tick on the UniTask ThreadPool), it enqueues an `InteractionInstance` onto `Interactable.QueuedInteractions` instead of touching the animator. The queue is drained on the main thread by `Interactable.DoQueuedInteractions()` (line 286363), which calls `OnServer.Interact(InteractionInstance)` per item.
- The **`skipAnimation && interactable.State != state` short-circuit (line 39345)** is the ONLY value-change check on this path, and it applies only to the `skipAnimation` branch: when skipping animation and the value already matches, the call collapses to nothing (no `Interact`, no setter, no callback). The else branch (the animated path, lines 39349-39358) is unconditional and will write even an unchanged value.
- Note that even when the `skipAnimation` branch fires, it calls `interactable.Interact(state)` with the default `skipAnimation = true`.

The client-side counterpart only asks the server to do the work. `NetworkClient.Interact(Interactable, int)` (line 198342):

```csharp
public static void Interact(Interactable interactable, int state)
{
    if (Assets.Scripts.Networking.NetworkManager.IsClient)
    {
        SendToServer(new RequestInteractionToServer
        {
            InteractThingId = interactable.Parent.ReferenceId,
            InteractionId = interactable.InteractableId,
            NewState = state
        });
    }
}
```

It sends a `RequestInteractionToServer` message and does not write `State` locally. The server applies the change via its own `OnServer.Interact`, then the result ships back through the interactable-update delta (next section).

### Queue drain timing: GameManager.Update on the main thread, one frame later, not the issuing tick
<!-- verified: 0.2.6403.27689 @ 2026-07-07 -->

Re-verified at 0.2.6403.27689: the `OnServer.Interact(Interactable, int, bool)` body excerpted above is verbatim-unchanged (new refs 39690-39723; the worker-thread enqueue branch is 39696-39703). The queue side, verbatim:

```csharp
public static Queue<InteractionInstance> QueuedInteractions = new Queue<InteractionInstance>();   // Interactable, L304374

public static void DoQueuedInteractions()          // Interactable, L304777-304785
{
    lock (QueuedInteractions)
    {
        while (QueuedInteractions.Count > 0)
        {
            OnServer.Interact(QueuedInteractions.Dequeue());
        }
    }
}

public readonly struct InteractionInstance(Thing thing, InteractableType action, int state, bool skipAnimation)   // L304799
```

The drain driver is `GameManager.Update()` on the Unity main thread (L205154-205170), gated three ways before the queue is touched:

```csharp
public void Update()
{
    if (!IsInitialized)
    {
        return;
    }
    if (!WorldManager.IsGamePaused)
    {
        // ...
        if (RunSimulation)
        {
            Interactable.DoQueuedInteractions();   // L205169
        }
        // ...
    }
}
```

Mechanics:

- The queued item stores `interactable.Parent` (the Thing) plus `interactable.Action` (the `InteractableType`), NOT the `Interactable` reference. The drain re-enters `OnServer.Interact(InteractionInstance)` (39685-39688) -> `Interact(Thing, InteractableType, int, bool)` (39664-39683), which re-resolves the interactable by scanning `thing.Interactables` for the first entry whose `Action` matches, then re-enters the main-thread path above (re-checking `RunSimulation` and `GameState == Running`).
- A worker-thread call enqueues under the `RunSimulation` gate only; the `GameState != Running` drop and the `AllowInteraction` / `InteractWhenReady` branching happen at drain time.
- The drain is skipped while the game is paused, before `GameManager.IsInitialized`, or when `RunSimulation` is false, so queued items survive a pause and land on unpause.

Consequence: an `OnServer.Interact` issued from a worker thread (a power-tick or atmos-tick patch) lands within one main-thread FRAME, NOT within the issuing tick. Anything that must be tick-atomic (state visible before the next power tick reads it, or a batch applied all-or-nothing at a tick boundary) cannot ride this queue; the mod needs its own queue drained at a tick boundary it controls. PowerGridPlus's emergency-light toggle queue is the in-repo example: it collects worker-side toggle decisions and applies them itself at the tick edge instead of letting each land a frame apart mid-tick.

## Caller-side change gating: why power ticks do not churn the callback
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

Because the `State` setter has no value guard and the animated `OnServer.Interact` branch is unconditional, repeated power ticks would re-raise `OnInteractableUpdated` every tick if callers wrote blindly. They do not: power-tick code wraps each `OnServer.Interact(base.InteractPowered, X)` in an `if (Powered)` / `if (!Powered)` guard so the call is made only on an actual transition.

Battery example, `Battery.OnAtmosphericTick` (lines 277770-277782):

```csharp
if (!OnOff || !BatteryCell || BatteryCell.IsEmpty)
{
    if (Powered)
    {
        OnServer.Interact(base.InteractPowered, 0);
    }
    HeatingEnergyLastTick = heatingEnergyLastTick;
    return;
}
if (!Powered)
{
    OnServer.Interact(base.InteractPowered, 1);
}
```

Combustion example (lines 279289-279307):

```csharp
if (!Powered)
{
    OnServer.Interact(base.InteractPowered, 1);
}
// ...
else if (Powered)
{
    OnServer.Interact(base.InteractPowered, 0);
}
```

In both, the `OnServer.Interact` call is reached only when the desired state differs from the current `Powered` reading. A tick that does not change the powered state makes no call at all, so the setter never runs and `OnInteractableUpdated` is not raised. This is the mechanism that keeps the `Powered` interactable from firing the callback every tick. Do not assume the setter or `OnServer.Interact` deduplicates for you on the animated path; the caller's `if` is the gate.

## Thing.OnInteractableUpdated: the base virtual every peer runs
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

The setter calls `Parent.OnInteractableUpdated(this)`. The base implementation on `Thing` (line 300444):

```csharp
public virtual void OnInteractableUpdated(Interactable interactable)
{
    CacheAnimatorInteractableVariable(interactable.Action);
    RefreshAnimState(GameManager.GameState != GameState.Running);
    this.OnInteractable?.Invoke();
}
```

It caches the animator variable for `interactable.Action`, refreshes the animator state, and invokes the `OnInteractable` event (an `Action` other code can subscribe to). The sibling `Thing.OnInteractableStateChanged` (line 300436), also called from the setter just before this, only writes the animator integer:

```csharp
public virtual void OnInteractableStateChanged(Interactable interactable, int newState, int oldState)
{
    if ((bool)BaseAnimator)
    {
        SetIntegerSafe(interactable.PropertyId, newState);
    }
}
```

Inside `OnInteractableUpdated`, the only thing telling you *which* interactable changed is `interactable.Action`; the new value is `interactable.State`. A subclass override (or a Harmony postfix) keys off `interactable.Action == InteractableType.<X>` to react to one specific axis.

## Override chain: a Thing postfix catches every subclass
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

`OnInteractableUpdated` is overridden down the power-device hierarchy, and every override calls `base.OnInteractableUpdated(interactable)` first. So a single Harmony postfix on `Thing.OnInteractableUpdated` runs for every subclass, after each subclass's own logic.

Hierarchy (each `: ` confirmed in the decompile):

`PowerReceiver` (line 386861) / `PowerTransmitter` (line 387065) `: WirelessPower` (line 405441) `: ElectricalInputOutput` (line 373755) `: Device : ... : Thing`.

Resolution order for a `PowerTransmitter` / `PowerReceiver` instance (most-derived first; each calls `base` first, so the actual execution order of the bodies is base-to-derived):

1. `Thing.OnInteractableUpdated` (line 300444) - base, shown above.
2. `Device.OnInteractableUpdated` (line 350870):

```csharp
public override void OnInteractableUpdated(Interactable interactable)
{
    base.OnInteractableUpdated(interactable);
    if (GameManager.GameState == GameState.Running)
    {
        if (GameManager.RunSimulation && interactable.Action == InteractableType.OnOff && HasPowerState)
        {
            AssessPower(PowerCable ? PowerCable.CableNetwork : null, interactable.State == 1);
        }
        _ = IsOperable;
    }
}
```

3. `PowerReceiver.OnInteractableUpdated` (line 386946) or `PowerTransmitter.OnInteractableUpdated` (line 387163):

```csharp
// PowerReceiver, line 386946
public override void OnInteractableUpdated(Interactable interactable)
{
    base.OnInteractableUpdated(interactable);
    if (interactable.Action == InteractableType.OnOff)
    {
        if (OnOff)
        {
            RequestRetarget();
        }
        else
        {
            base.VisualizerIntensity = 0f;
        }
    }
    if (interactable.Action == InteractableType.Powered && !Powered)
    {
        base.VisualizerIntensity = 0f;
    }
}

// PowerTransmitter, line 387163
public override void OnInteractableUpdated(Interactable interactable)
{
    base.OnInteractableUpdated(interactable);
    if (interactable.Action == InteractableType.OnOff)
    {
        if (OnOff)
        {
            TryContactReceiver();
        }
        else
        {
            base.VisualizerIntensity = 0f;
        }
    }
    if (interactable.Action == InteractableType.Powered && !Powered)
    {
        base.VisualizerIntensity = 0f;
    }
}
```

`WirelessPower` (line 405441) and `ElectricalInputOutput` (line 373755) do **not** override `OnInteractableUpdated`; the chain passes straight through them. Both dish overrides filter on `interactable.Action`: on an `OnOff` change they retarget when switched on (`RequestRetarget` / `TryContactReceiver`) and zero the beam visualizer when switched off, and they zero the visualizer on a `Powered` change that left the device unpowered. This is the exact vanilla pattern a beam-on/off mod mirrors.

## Client apply path: how OnInteractableUpdated fires on a remote client
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

On the server, the `State` setter set `NetworkUpdateFlags |= 2` and `IsDirty = true`. The server serializes only the dirty interactables and clears the flag. `Thing.BuildInteractableUpdate` (line 303291):

```csharp
private void BuildInteractableUpdate(RocketBinaryWriter writer, ushort networkUpdateType)
{
    if (!IsNetworkUpdateRequired(2u, networkUpdateType))
    {
        return;
    }
    Network.WriteIndex<byte>(writer, out var count, out var bufferIndex);
    for (byte b = 0; b < Interactables.Count; b++)
    {
        Interactable interactable = Interactables[b];
        if (interactable.IsDirty)
        {
            writer.WriteByte(b);
            WriteInteractableState(writer, interactable);
            interactable.IsDirty = false;
            if (count >= byte.MaxValue)
            {
                throw new System.Exception($"InteractableCount exceeds: {byte.MaxValue}");
            }
            count++;
        }
    }
    Network.WriteIndex(writer, count, bufferIndex);
}
```

Only interactables with `IsDirty == true` are written; each is cleared (`interactable.IsDirty = false`, line 303305) as it ships.

On the client, `Thing.ProcessUpdate` (line 303329) calls `ProcessInteractableUpdate` (line 303366):

```csharp
private void ProcessInteractableUpdate(RocketBinaryReader reader, ushort networkUpdateType)
{
    if (IsNetworkUpdateRequired(2u, networkUpdateType))
    {
        byte b = reader.ReadByte();
        for (int i = 0; i < b; i++)
        {
            byte index = reader.ReadByte();
            Interactable interactable = Interactables[index];
            int state = ReadInteractableState(reader, interactable);
            interactable.Interact(state, skipAnimation: false);
        }
    }
}
```

For each dirty interactable the server sent, the client calls `interactable.Interact(state, skipAnimation: false)`. That goes through `Interact` -> the `State` setter -> `Parent.OnInteractableUpdated(this)`. So `OnInteractableUpdated` fires on the remote client too. Because `NetworkManager.IsServer` is false on the client, the setter's `NetworkUpdateFlags |= 2` block is skipped, so the client does not echo the change back. `skipAnimation: false` takes the animated branch, which also means the client does NOT inherit any value-change short-circuit (that short-circuit lives only on the `skipAnimation == true` branch of `OnServer.Interact`, which the client never reaches for this path).

Join paths also raise the callback so a freshly joined or freshly loaded client lands in the correct state:

- `Thing.DeserializeSave` (lines 302253-302261) iterates `Interactables` and, for each with `JoinInProgressSync`, calls `OnInteractableUpdated(interactable)` directly:

```csharp
foreach (Interactable interactable2 in Interactables)
{
    if (interactable2.JoinInProgressSync)
    {
        OnInteractableUpdated(interactable2);
        interactable2.SetState();
        OnFinishedInteractionSync(interactable2);
    }
}
```

- `Thing.DeserializeInteractableOnJoin` (line 303118) reads each interactable and routes through `SetInteractableStateOnJoin` (line 303112), whose `interactable.State = state;` runs the setter (and thus `OnInteractableUpdated`):

```csharp
protected virtual void SetInteractableStateOnJoin(Interactable interactable, int state)
{
    interactable.State = state;
    interactable.SetState();
}

private void DeserializeInteractableOnJoin(RocketBinaryReader reader)
{
    Network.ReadIndex<byte>(reader, out var value);
    for (int i = 0; i < value; i++)
    {
        byte index = reader.ReadByte();
        Interactable interactable = Interactables[index];
        int state = ReadInteractableState(reader, interactable);
        SetInteractableStateOnJoin(interactable, state);
        OnFinishedInteractionSync(interactable);
    }
}
```

## Practical guidance: react to a specific state change once on all peers
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

To run logic exactly once whenever a specific interactable axis changes, on every peer (server, single-player host, AND remote clients), and never per power tick:

**Postfix `Thing.OnInteractableUpdated(Interactable)` and filter on `interactable.Action == InteractableType.<X>`.**

Why this is the right hook:

- It runs on the server/single-player path (the `State` setter calls it directly when `OnServer.Interact` applies a change) and on remote clients (the `ProcessInteractableUpdate` / join paths call `interactable.Interact(...)` -> setter -> callback). One postfix covers all peers.
- It runs once per actual change, because the caller-side `if (Powered)` / `if (!Powered)` (and the player/logic toggle path) only writes on a transition. It does NOT fire every tick.
- The override chain means a postfix on the base `Thing` method catches `PowerReceiver`, `PowerTransmitter`, `Device`, and every other subclass (each calls `base` first).

The `Action` filter is **mandatory**. `Powered`, `Error`, `Mode`, every button, and every slot all enter the same callback (each only on its own change). Without `if (interactable.Action == InteractableType.OnOff)` (or whichever axis you care about), a postfix watching for the switch will also fire on power-state and error-state changes. Read the new value from `interactable.State` (e.g. `interactable.State == 1` for "switched on").

Alternative hooks and why they are worse:

- **`Thing.set_OnOff`** (line 299176): the `OnOff` property setter does not route through the interactable callback at all. It writes the animator integer or `InteractOnOff.State` directly:

```csharp
set
{
    if (HasOnOffState)
    {
        if ((bool)BaseAnimator)
        {
            SetIntegerSafe(Interactable.OnOffState, value ? 1 : 0);
        }
        else
        {
            InteractOnOff.State = (value ? 1 : 0);
        }
        _onOff = value;
    }
}
```

When `HasBaseAnimator` is true it calls `SetIntegerSafe` on the animator and never touches `InteractOnOff.State`, so the `Interactable.State` setter (and `OnInteractableUpdated`) is bypassed. This property is not the real toggle path the game uses for player/logic switching (that path is `OnServer.Interact(base.InteractOnOff, ...)` -> `Interact` -> `State`). Patching `set_OnOff` is animator-dependent and misses the canonical path. Note `InteractOnOff.State = ...` in the no-animator branch DOES go through the setter, so behavior differs by prefab; do not rely on it.

- **`OnServer.Interact` filtered to `InteractableType.OnOff`**: this fires only where `GameManager.RunSimulation` is true, i.e. on the host / single-player. It never runs on a remote client (the client only sends a request; the server applies it). A mod that needs the visual or gameplay reaction on remote clients (e.g. a beam visualizer) would miss them entirely. Use this only for server-authoritative gameplay writes, not for client-visible reactions.

## Interaction readonly struct (the value passed into InteractWith)
<!-- verified: 0.2.6228.27061 @ 2026-06-03 -->

Distinct from `Interactable` itself: `Interaction` is a primary-constructed `readonly struct` (line 286395 in `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs`) that carries the per-click context into every `Thing.InteractWith(Interactable, Interaction, bool doAction)` call. Verbatim shape:

```csharp
public readonly struct Interaction(Thing sourceThing, Slot sourceSlot, Thing destinationThing, bool altKey)
{
    public Thing SourceThing { get; } = sourceThing;
    public Slot SourceSlot { get; } = sourceSlot;
    public Thing DestinationThing { get; } = destinationThing;
    public bool AltKey { get; } = altKey;
}
```

`AltKey` is the modifier toggle a click captured from input (e.g. holding Alt during a screwdriver button click), and is the only field most patched `InteractWith` overrides read. `Transformer.InteractWith` does `interaction.AltKey ? 1 : 10` for the priority step in PowerGridPlus's reskin.

Adjacent in the same lines (286385-286394) is the unrelated `InteractionInstance` struct, the `Queue<InteractionInstance>` payload type used by `Interactable.QueuedInteractions` (line 285960):

```csharp
public readonly struct InteractionInstance(Thing thing, InteractableType action, int state, bool skipAnimation)
{
    public readonly InteractableType Action = action;
    public readonly int State = state;
    public readonly bool SkipAnimation = skipAnimation;
    public readonly Thing Thing = thing;
}
```

Same file, similar name, different role: `Interaction` is a per-click value, `InteractionInstance` is an enqueued state-write request. Do not confuse them in a Harmony patch signature.

Practical use: to drive `Transformer.InteractWith` headlessly from a probe (e.g. ScenarioRunner verifying knob increment behaviour without a connected player), pass `new Interaction(null, null, transformer, altKey)` plus a real `Interactable` lifted from `transformer.Interactables` matching the desired `Action` (Button1 / Button2). All four `Interaction` fields are nullable references except `AltKey`; passing nulls for `SourceThing` / `SourceSlot` / `DestinationThing` is safe because `Transformer.InteractWith` only reads `AltKey`.

The base `Thing.InteractWith(Interactable, Interaction, bool)` body that consumes this struct (slot delegation, the Open / OnOff / Lock switch, and the `doAction: false` hover-preview contract) is quoted in full on [Thing](./Thing.md).

## Verification history

- 2026-07-07: added "Queue drain timing" subsection under the OnServer.Interact section (game version 0.2.6403.27689). Re-verified the `OnServer.Interact(Interactable, int, bool)` excerpt verbatim-unchanged at the new refs (39690-39723; worker enqueue 39696-39703) and read the full drain chain directly: `QueuedInteractions` declaration (304374), `DoQueuedInteractions` (304777-304785), `InteractionInstance` readonly struct (304799), the re-entry overloads `Interact(InteractionInstance)` (39685-39688) -> `Interact(Thing, InteractableType, int, bool)` (39664-39683, re-resolves the Interactable by `Action` scan), and the drain driver `GameManager.Update` (205154-205170) gated on `IsInitialized && !WorldManager.IsGamePaused && RunSimulation`. New durable consequence: worker-issued interactions land within one main-thread frame, not within the issuing tick, so tick-atomic state changes need a mod-owned queue drained at a tick boundary (PowerGridPlus emergency-light toggle queue). Occasion: PowerGridPlus partial-power forensics. Additive; the existing 0.2.6228 description of the enqueue/drain pair (which did not name the drain driver) was confirmed, not changed. Bumped frontmatter verified_in / verified_at.
- 2026-06-03: added `Interaction` readonly struct documentation (line 286395), distinguishing it from `InteractableType` enum (the discriminator), `Interactable` class (the per-Thing state slot), and `InteractionInstance` struct (the queued state-write request, line 286385). Finding produced while building ScenarioRunner headless probes for PowerGridPlus knob behaviour; needed the exact constructor signature to synthesize an `Interaction` for `Transformer.InteractWith`.
- 2026-05-22: page created. Sourced from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs`. Driving question: how does a state change (specifically a dish on/off switch) propagate and fire `Thing.OnInteractableUpdated` on every peer, so a PowerTransmitterPlus beam fix can hook it once per change and never per tick. Verbatim extracts: `InteractableType` enum (lines 285691-285832), `Interactable` fields `Action` (285902) and `OnOffState` hash (285866), the `State` getter/setter (286009-286051, setter body 286027-286050, server flag block 286045-286049, no `new != old` guard confirmed), `Interact` (286328-286336) / `InteractWhenReady` (286358-286361), `OnServer.Interact` (39327-39360, `RunSimulation` gate at 39329, `skipAnimation && State != state` short-circuit at 39345), `NetworkClient.Interact` (198342-198353, request-only), the battery (277770-277782) and combustion (279289-279307) caller-side power-tick gates, base `Thing.OnInteractableUpdated` (300444-300449) and `OnInteractableStateChanged` (300436-300442), the override chain `Device.OnInteractableUpdated` (350870-350881) / `PowerReceiver` (386946-386964) / `PowerTransmitter` (387163-387181) with `WirelessPower` and `ElectricalInputOutput` confirmed not overriding, hierarchy headers (`PowerReceiver` 386861, `PowerTransmitter` 387065, `WirelessPower` 405441, `ElectricalInputOutput` 373755), the client apply path `BuildInteractableUpdate` (303291-303314, clears `IsDirty` at 303305) / `ProcessUpdate` (303329-303364) / `ProcessInteractableUpdate` (303366-303379, `Interact(state, skipAnimation: false)` at 303376), the join paths `DeserializeSave` loop (302253-302261) and `DeserializeInteractableOnJoin` (303118-303129) / `SetInteractableStateOnJoin` (303112-303116), and `Thing.set_OnOff` (299176-299190) for the rejected-alternative note. Cross-checked the hierarchy and override claims against the existing `Device.md` and `WirelessPower.md` pages; consistent (both already note `OnServer.Interact(base.InteractPowered, ...)` as the power-state write pattern).

## Open questions

None.
