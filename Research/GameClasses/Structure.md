---
title: Structure
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/SprayPaintPlus/RESEARCH.md:177-179
  - Mods/SprayPaintPlus/SprayPaintPlus/NetworkPainterPatch.cs:320-328
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Structure
related:
  - ./Thing.md
  - ./Wall.md
tags: [prefab]
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

## Verification history

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0029e, F0322. No conflicts.
- 2026-04-21: added "IsBroken property" section from direct decompile of `Assets.Scripts.Objects.Structure` and `Assets.Scripts.Objects.Thing`. Additive only; no existing content changed. Game version 0.2.6228.27061.

## Open questions

None.
