---
title: Structure
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-29
sources:
  - Mods/SprayPaintPlus/RESEARCH.md:177-179
  - Mods/SprayPaintPlus/SprayPaintPlus/NetworkPainterPatch.cs:320-328
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Structure
related:
  - ./Thing.md
  - ./Wall.md
  - ./OnServer.md
  - ../GameSystems/DamageState.md
  - ../GameSystems/Explosions.md
tags: [prefab, damage]
---

# Structure

Vanilla game class for player-built, fixed-position game objects. Subclass of `Thing`. Covers walls, frames, pipes, cables, and devices.

## NotImplementedException on batched structures
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0029e.

Some structures use `structureRenderMode != Standard` and share a combined mesh. `SetCustomColor` throws `NotImplementedException` on these. `PaintSafe` catches the exception per-item so one unpaintable structure does not abort the rest of the network.

### PaintSafe catch comment (F0322)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source comment from `NetworkPainterPatch.cs:320-328`:

```
/// <summary>
/// Individual SetCustomColor calls can throw. Most notably,
/// Structure.SetCustomColor throws NotImplementedException on any
/// structure whose structureRenderMode != Standard (batched-render
/// structures share a combined mesh and can't be recolored per
/// instance). A destroyed-mid-paint item can also trip a null deref.
/// Without the catch, one unpaintable or stale item would abort
/// painting the rest of the network.
/// </summary>
```

## IsBroken property
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Source: `$(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Structure` and `Assets.Scripts.Objects.Thing`.

`Structure` has a public `IsBroken` property, overriding the base on `Thing`.

Base (`Thing.IsBroken`):

```csharp
public virtual bool IsBroken
{
    get
    {
        if (DamageState != null)
            return DamageState.Total >= DamageState.MaxDamage;
        return false;
    }
}
```

Override (`Structure.IsBroken`):

```csharp
public override bool IsBroken
{
    get
    {
        if (!base.IsBroken)
            return CurrentBuildStateIndex < 0;
        return true;
    }
}
```

A `Structure` is `IsBroken` when either:

- Its `DamageState.Total >= DamageState.MaxDamage` (fully damage-destroyed), OR
- Its `CurrentBuildStateIndex < 0` (deconstructed past the first build stage, which is how the game models a wreckage / half-torn-down state).

Read-only property; no setter. Use it verbatim as `thing.IsBroken` to detect "is this structure currently wreckage / destroyed." For detecting structures that have broken build states in their prefab definition (not the runtime state), use `Structure.HasBrokenBuildStates` (getter tests `BrokenBuildStates?.Count > 0`).

## Build-state model and the destruction path
<!-- verified: 0.2.6228.27061 @ 2026-04-29 -->

`Structure : Thing`. Subclass tree of interest: `Structure -> LargeStructure -> Wall` / `Frame` (and `Geyser`, `StructureFuselage`, `LaunchMount`); `Structure -> SmallGrid` (attached devices / small wall-mounted machines), `Cladding`, `Stairs`, `RoverFrame`. The player-facing "structure frame" is `Frame : LargeStructure`, a trivial subclass; there is no class literally named `StructureFrame`. (`MultiConstructor` is the *kit item* you build frames from, not the structure.)

Construction is genuinely stage-by-stage. A `Structure` carries `List<BuildState> BuildStates`, `List<BrokenBuildState> BrokenBuildStates`, `int CurrentBuildStateIndex` (setter clamps, fires `OnBuildState`, sets `NetworkUpdateFlags |= 64`), `BuildState CurrentBuildState`, `bool IsStructureCompleted`, `bool HasBrokenMesh => BrokenBuildStates?.Count > 0`, plus grid registration (`LocalGrid`, `BlockingGrids`). `Structure.AttackWith` with the matching tool moves `CurrentBuildStateIndex` up (construct) or down (deconstruct) one step; deconstructing build state 0 is what removes the object (via `BuildStates[0].Tool.Deconstruct(eventInstance)` then `OnServer.Destroy`). A `Structure` can also be removed in one shot by `Thing.Delete` / `OnServer.Destroy` (the engine path), bypassing the build-stage walk and the tool/ingot requirements.

### When DamageState maxes out
<!-- verified: 0.2.6228.27061 @ 2026-04-29 -->

When `DamageState.Total >= MaxDamage`, `ThingDamageState.OnDamageUpdated()` schedules `ThingDamageState.Destroy()`:

```csharp
private async UniTask Destroy()
{
    await UniTask.SwitchToMainThread();
    await UniTask.DelayFrame(1);
    Structure asStructure = Parent.AsStructure;
    if ((bool)asStructure)
    {
        if (!_isDestroyed && asStructure.IsBroken && asStructure.HasBrokenMesh)
        {
            asStructure.UpdateBuildStateAndVisualizer(asStructure.GetBrokenState(), _particlesOnDestroy);
            asStructure.OnStructureBroken();
            HealAll();                       // reset HP so the wreck persists
            _isDestroyed = true;
            return;                          // NOT removed -- left as a wreck/damaged shell
        }
        EffectManager.CreateDeconstructionEffect(asStructure, _particlesOnDestroy);
        if ((bool)Parent) Parent.OnDamageDestroyed();
    }
    if ((bool)Parent) Parent.OnDamageDestroyed();
}
```

**Pitfall**: a structure that has a broken-mesh build state (most walls and frames do; check `HasBrokenMesh` / `HasBrokenBuildStates`) does NOT despawn when damage maxes out -- it converts to its wreck visual and stays alive in an `IsBroken` state with HP reset. A mod that wants *guaranteed* removal should call `OnServer.Destroy(thing)` or `Thing.Delete(...)` directly rather than cranking damage. See `./Thing.md` and `./OnServer.md`.

### OnDamageDestroyed and StructureDestroyed
<!-- verified: 0.2.6228.27061 @ 2026-04-29 -->

`Structure.OnDamageDestroyed()` is the "fully demolish, drop wreckage, despawn" path -- it skips the broken-mesh conversion:

```csharp
public override void OnDamageDestroyed()
{
    base.OnDamageDestroyed();                       // Thing.OnDamageDestroyed: IsBurning = false
    if (GameManager.RunSimulation && !base.Indestructable)
    {
        ConstructionEventInstance eventInstance = new ConstructionEventInstance { Parent = this, Position = ..., Rotation = ..., SteamId = OwnerClientId, OtherHandSlot = null };
        StructureDestroyed(eventInstance, destroyedFromDamage: true);
        if (this is IWreckage wreckage) wreckage.SpawnWreckage();
        OnServer.Destroy(this);
    }
}
```

`StructureDestroyed(eventInstance, destroyedFromDamage)` unregisters from the grid / atmospheres, handles `WorldParticleEffect` grid bookkeeping; if `destroyedFromDamage && BrokenBuildStates.Count > 0` it marks the broken build state, otherwise it runs `BuildStates[0].Tool.Deconstruct(eventInstance)` and moves slot occupants to the world; then for each `AttachedDevices` entry it deconstructs through all build states and `OnServer.Destroy`s it. `IWreckage.SpawnWreckage()` is what drops the broken-frame / wreckage debris item; not every structure implements `IWreckage` (`Frame` and walls generally do). `OnServer.Destroy(this)` is the actual removal. `Indestructable` is checked here (the damage path); `Thing.Delete` / `OnServer.Destroy` themselves do not check it. All server-authoritative; destruction replicates via the normal `DestroyEvent` / construction-event networking.

## Verification history

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0029e, F0322. No conflicts.
- 2026-04-21: added "IsBroken property" section from direct decompile of `Assets.Scripts.Objects.Structure` and `Assets.Scripts.Objects.Thing`. Additive only; no existing content changed. Game version 0.2.6228.27061.
- 2026-04-29: added "Build-state model and the destruction path" section (with "When DamageState maxes out" and "OnDamageDestroyed and StructureDestroyed" subsections) from a research pass on the explosion / structure-destruction system. Additive; no existing content changed. Sources: `Assets.Scripts.Objects.Structure` (`BuildStates`, `BrokenBuildStates`, `CurrentBuildStateIndex`, `HasBrokenMesh`, `AttackWith`, `OnDamageDestroyed`, `StructureDestroyed`, `OnDestroy`), `ThingDamageState.OnDamageUpdated` / `Destroy`, `IWreckage` (all in `Assembly-CSharp`, game version 0.2.6228.27061).

## Open questions

None.
