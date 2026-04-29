---
title: Occlusion (distance-based renderer culling)
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-29
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.OcclusionManager
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Thing (SetOcclusion / IsOccluded / OnStartRender / OnStopRender / GetRenderMaxDistanceSquared)
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.DynamicThing (PhysicsOnRender / SetColliders)
related:
  - ../GameClasses/Thing.md
  - ../GameClasses/PowerTransmitter.md
tags: [unity, transforms]
---

# Occlusion (distance-based renderer culling)

Stationeers culls Things visually based on distance from the player camera. The system is purely a **renderer-visibility** mechanism for static Things; physics colliders remain active at any distance for `Structure`-derived classes. Only `DynamicThing` instances additionally have their colliders toggled, and that is keyed off the same render-state transition.

The distinction matters whenever a piece of code relies on physics raycasts hitting distant objects. A `Physics.Raycast` from the player toward a far-away `PowerReceiver` will register the hit even though that receiver's mesh is no longer drawn, because the receiver is a `Structure` and `Structure`'s `OnStopRender` does not touch its colliders.

## Core flow
<!-- verified: 0.2.6228.27061 @ 2026-04-29 -->

`OcclusionManager.CheckAllOcclusion` (Assembly-CSharp `OcclusionManager`):

```csharp
public static void CheckAllOcclusion()
{
    GridController.AllStructuresPool.ForEach(SetOcclusionAction);
    AllDynamicThings.ForEach(SetOcclusionAction);
}
```

Both static structures (in `GridController.AllStructuresPool`) and dynamic things (in `AllDynamicThings`) are iterated; `Entity` instances are short-circuited inside `SetOcclusion` itself.

`Thing.SetOcclusion` (base):

```csharp
public virtual void SetOcclusion()
{
    if (_renderChangeScheduled || IsBeingDestroyed || (object)InventoryManager.Parent == null) return;
    float renderMaxDistanceSquared = GetRenderMaxDistanceSquared();
    CurrentCameraDistanceSquared = RootParent.Position.DistanceSquared(InventoryManager.WorldPosition);
    bool flag = CurrentCameraDistanceSquared < renderMaxDistanceSquared;
    if (flag) CheckShadowOverride();
    if (this is Entity) return;
    if (!flag)
    {
        if (!IsOccluded && !IsBeingDestroyed)
        {
            _renderChangeScheduled = true;
            RenderChange(setRenderer: false, this).Forget();
        }
    }
    else if (IsOccluded && !IsBeingDestroyed)
    {
        _renderChangeScheduled = true;
        RenderChange(setRenderer: true, this).Forget();
    }
}
```

`Thing.IsOccluded` (virtual property setter):

```csharp
public virtual bool IsOccluded
{
    get => _isOccluded;
    set
    {
        _isOccluded = value;
        if (!_isOccluded) OnStartRender();
        else OnStopRender();
    }
}
```

`Thing.OnStopRender` (base — used by every `Structure` subclass that does not override):

```csharp
public virtual void OnStopRender()
{
    if (this is IBatchRendered batchRendered) BatchRenderer.Remove(batchRendered);
    else
    {
        foreach (ThingRenderer renderer in Renderers)
            if (renderer != null && renderer.HasRenderer())
                renderer.Visible = false;
    }
    foreach (ThingLight light in Lights) light.Refresh();
}
```

Note what is **absent**: no `Collider.enabled = false`, no `gameObject.SetActive(false)`, no removal from physics scene. The Things' `Collider` components stay enabled and continue to participate in raycasts.

## Render distance
<!-- verified: 0.2.6228.27061 @ 2026-04-29 -->

`Thing.GetRenderMaxDistanceSquared` (base):

```csharp
protected virtual float GetRenderMaxDistanceSquared()
{
    return Mathf.Pow(100f * OcclusionManager.RenderDistanceMultiplier, 2f);
}
```

Default base distance is `100 m` along each axis squared. Subclasses override this with values found in vanilla code at 15 m, 20 m, 25 m, 30 m, 40 m, 60 m, 100 m, and 200 m. The multiplier is taken from the user's video setting:

| `Settings.CurrentData.RenderDistance` | `RenderDistanceMultiplier` |
|---|---|
| `Lowest` | `0.8` |
| `Low` | `1.0` |
| `Default` (fallback) | `1.0` |
| `Medium` | `1.5` |
| `High` | `2.0` |
| `Extreme` | `2.5` |

`OcclusionManager.UpdateRenderDistanceMultiplier` reads the setting via `Enum.TryParse<RenderDistance>`; an unrecognised string falls through the `_ => 1f` default.

So a `Thing` whose subclass does not override `GetRenderMaxDistanceSquared` has its renderer hidden when the camera is more than `100 × multiplier` metres from `RootParent.Position`. At `Extreme` that is 250 m; at `Lowest` that is 80 m.

## Colliders are the exception, not the rule
<!-- verified: 0.2.6228.27061 @ 2026-04-29 -->

The only class that disables colliders on render-state transition is `DynamicThing`. Its overrides in Assembly-CSharp:

```csharp
public override void OnStartRender()
{
    base.OnStartRender();
    PhysicsOnRender(true);
}

public override void OnStopRender()
{
    base.OnStopRender();
    PhysicsOnRender(false);
}

public void PhysicsOnRender(bool isRendered)
{
    if (isRendered) SetPhysics(on: true);
    else SetPhysics(on: false);
}

public void SetColliders(bool on, bool always = false)
{
    foreach (Collider collider in _colliders)
        if (always || !collider.gameObject.CompareTag("CollidersAlwaysVisible"))
            collider.enabled = on;
    foreach (Collider trigger in _triggers) ...
}
```

`SetColliders` is declared only on `DynamicThing` (no other `public void SetColliders(bool on, bool always = false)` definition exists in Assembly-CSharp). Static `Structure` subclasses inherit `OnStopRender` directly from `Thing` (or override it for their own render-only side-effects, like a console screen toggling the screen mesh on / off) and never reach `SetColliders` through the occlusion path.

Implication: a raycast against a `PowerReceiver.DishTarget`, a `Wall`, an `Appliance` mounted on a frame, or any other static structure will hit at any distance the player's chunk is loaded at, regardless of the local render-distance setting. The visible mesh disappears at the configured threshold; the collider does not.

## Verification history

- 2026-04-29: page created during a diagnostic for `PowerTransmitterPlus`'s long-distance auto-aim. The hypothesis was that distant `PowerReceiver` colliders might be deactivated by an occlusion / streaming mechanism, causing `PowerTransmitter.TryContactReceiver`'s `Physics.Raycast` to return no hit. Reading `OcclusionManager.CheckAllOcclusion`, `Thing.SetOcclusion`, `Thing.OnStopRender`, and `DynamicThing.PhysicsOnRender` / `SetColliders` showed the hypothesis was wrong: only `DynamicThing` toggles colliders on render-state transitions, and `PowerReceiver`'s inheritance chain (`Structure → SmallGrid → Device → ElectricalInputOutput → WirelessPower → PowerReceiver`) never enters that path. Verified by direct decompile read and by `grep` confirming `SetColliders` is declared only on `DynamicThing`.

## Open questions

- Do interior chunks ever fully unload (deactivate `gameObject` rather than toggle renderer / collider) at very long range? `gameObject.SetActive(false)` calls on Things were not explicitly searched for in this pass; the occlusion path verified above does not call it, but other systems (chunk streaming, save / load, scene unload) might. A long-distance test that places a `PowerReceiver` deliberately far from spawn and observes whether `Thing._colliderLookup.TryGetValue(hit.collider, ...)` succeeds when the player is close to the transmitter would confirm this end-to-end.
