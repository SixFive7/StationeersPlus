---
title: Atmospheric thing tick dispatch (Thing.OnAtmosphericTick)
type: GameSystems
created_in: 0.2.6403.27689
verified_in: 0.2.6403.27689
verified_at: 2026-07-06
sources:
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: Assets.Scripts.Atmospherics.AtmosphericsManager (class line 439550; MAX_ATMOS_THINGS line 439559; AtmosphericThings line 439561; ThingAtmosphereTickAction line 439615; ThingAtmosphereTick line 439955; Register line 440030; Deregister line 440039); GameTick dispatch line 204458; Thing.OnAtmosphericTick line 321543; Device.OnAtmosphericTick line 371821
  - Mods/ForceFieldDoorModFix/RESEARCH.md (the interception documented here is used there)
related:
  - ../Patterns/StaleModReferenceJitCrash.md
  - ../Unsorted/Api-removals-0.2.6403.md
  - ./SimulationTickDriverHooks.md
  - ./PowerTickThreading.md
  - ./DevicePowerDraw.md
tags: [harmony, threading]
---

# Atmospheric thing tick dispatch (Thing.OnAtmosphericTick)

How the game calls `Thing.OnAtmosphericTick()` on every registered thing each simulation tick, where the single dispatch site is, and how to intercept or filter it for one thing type. This is the mechanism a stale `OnAtmosphericTick` override crashes from (see [Api-removals-0.2.6403](../Unsorted/Api-removals-0.2.6403.md) and [StaleModReferenceJitCrash](../Patterns/StaleModReferenceJitCrash.md)), and the hook point Force Field Door Mod Fix uses to take over a broken door's tick.

## The registration pool
<!-- verified: 0.2.6403.27689 @ 2026-07-06 -->

`Assets.Scripts.Atmospherics.AtmosphericsManager : ThreadedManager` (class line 439550) holds the set of things that receive atmospheric ticks in a dense pool:

```csharp
private const int MAX_ATMOS_THINGS = 32768;                                                                    // line 439559
public static readonly DensePool<Thing> AtmosphericThings = new DensePool<Thing>("AtmosphericThings", 32768);  // line 439561
```

A thing enters the pool only through `Register` and leaves through `Deregister`:

```csharp
public void Register(Thing thing)          // line 440030
{
    Structure structure = thing as Structure;
    if (!(structure != null) || !structure.IsCursor)
    {
        AtmosphericThings.Add(thing);
    }
}
public void Deregister(Thing thing)        // line 440039
{
    AtmosphericThings.Remove(thing);
}
```

`Register` skips cursor-preview structures (a `Structure` whose `IsCursor` is true). A `Thing` is in `AtmosphericThings` only because something called `AtmosphericsManager.Instance.Register(this)` on it. A subclass that overrides `OnAtmosphericTick` but never registers is never ticked; a mod door that wants per-tick atmospheric logic registers itself in its own lifecycle (for example in `OnRegistered`).

## The single dispatch site
<!-- verified: 0.2.6403.27689 @ 2026-07-06 -->

Per-thing dispatch goes through one static delegate and one driver method:

```csharp
private static readonly Action<Thing> ThingAtmosphereTickAction = delegate(Thing thing)   // line 439615
{
    if ((object)thing != null && !thing.IsBeingDestroyed)
    {
        thing.OnAtmosphericTick();     // line 439619
    }
};

public static void ThingAtmosphereTick()   // line 439955
{
    AtmosphericThings.ForEach(ThingAtmosphereTickAction);   // line 439957
}
```

`thing.OnAtmosphericTick()` at line 439619 is the only external dispatch of the no-argument `Thing.OnAtmosphericTick()` virtual in the assembly; a whole-file grep of `OnAtmosphericTick` shows every other occurrence is either an `override` definition or a `base.OnAtmosphericTick()` chain call inside an override. `ThingAtmosphereTickAction` is the compiler-generated lambda (`AtmosphericsManager+<>c.<.cctor>b__NNN_M`, the number drifts between builds) that appears as the caller frame in a stale-override crash stack, which is why such a crash names only vanilla frames.

`ThingAtmosphereTick()` is called once per simulation tick from `GameTick` (line 204458), between `AtmosphericsController.World.RunInternalReactionsJobs()` (line 204454) and `AtmosphericsManager.LifeTicksTick()` (line 204460) / `AtmosphericsManager.AtmosphericsNetworksTick()` (line 204462). The `GameTick` body runs on a thread-pool thread, not the Unity main thread (see [PowerTickThreading](./PowerTickThreading.md) and [SimulationTickDriverHooks](./SimulationTickDriverHooks.md)), so `OnAtmosphericTick` overrides, and anything hooked here, run off the main thread.

## The override chain
<!-- verified: 0.2.6403.27689 @ 2026-07-06 -->

`Thing.OnAtmosphericTick()` is an empty root virtual (line 321543: `public virtual void OnAtmosphericTick() { }`). The base that does the real work for powered devices is `Device.OnAtmosphericTick()` (line 371821), which injects heat into the local atmosphere when the device is on, powered, cabled, has a non-zero `EnergyToHeatRatio`, and is above Armstrong pressure. `Airlock` and `Door` do not override it, so a door mod that overrides `OnAtmosphericTick` and calls `base.OnAtmosphericTick()` binds `base` to `Device.OnAtmosphericTick`.

## Intercepting a specific thing's atmospheric tick
<!-- verified: 0.2.6403.27689 @ 2026-07-06 -->

Three levers, all on vanilla members, so none of them trips a stale reference that lives inside a broken mod's own override:

- **Filter at dispatch.** Resolve the delegate through the named static field and Harmony-prefix its `Method`: `AccessTools.Field(typeof(AtmosphericsManager), "ThingAtmosphereTickAction")`, read the delegate, patch `del.Method`. A prefix `bool (Thing __0)` that returns `false` for the target type skips `thing.OnAtmosphericTick()` for that thing while leaving every other thing's dispatch intact. Resolve via the field, never by the mangled `b__NNN` name.
- **Block admission.** Harmony-prefix `AtmosphericsManager.Register(Thing)` (line 440030) to return `false` for the target type, so it never enters the pool. Sweep already-registered instances with the pool's `RemoveWhere` predicate or `AtmosphericsManager.Instance.Deregister(thing)`.
- **Drive it yourself.** After blocking, Harmony-postfix `AtmosphericsManager.ThingAtmosphereTick()` (line 439955) and iterate your own list at the same 1:1 per-tick cadence.

Force Field Door Mod Fix uses the first lever to replace a broken `forcefielddoormod.ForceFieldDoor.OnAtmosphericTick` with a corrected reimplementation without ever letting the broken method be called (and therefore JIT-compiled, which is what throws). See `Mods/ForceFieldDoorModFix/RESEARCH.md`.

## Verification history

- 2026-07-06: page created at game 0.2.6403.27689 during the Force Field Door Mod Fix build. All line numbers and code excerpts read directly from `.work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs`. The "single dispatch site" claim confirmed by a whole-file grep of `OnAtmosphericTick` (only line 439619 is an external call of the no-argument virtual; all others are definitions or `base.` chain calls). Behaviour confirmed live on the dedicated server (Luna save, 15 force field doors): a stale override crashes from the `ThingAtmosphereTickAction` frame on every tick, and a Harmony prefix on that delegate suppresses the call cleanly while the simulation keeps ticking.

## Open questions

- None.
