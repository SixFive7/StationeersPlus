---
title: Sub-component grafting from a vanilla prefab
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/RepairPrototype/plan.md:778-792 (F0229j)
related:
  - ./PrefabCloning.md
tags: [prefab]
---

# Sub-component grafting from a vanilla prefab

When a mod needs a specific child component from a vanilla prefab (an on/off button, a collider, a sub-mesh) but does NOT want the entire prefab cloned, graft just the child GameObject into the mod's prefab with `GameObject.Instantiate(srcChild, targetTransform)`. Wire up any cross-references by hand after the instantiate.

## Problem
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

F0229j (Plans/RepairPrototype/plan.md:778-792):

> FPGA `FPGALogicHousing.cs` pattern: `public override void OnPrefabLoad()` uses `PrefabUtils.FindPrefab<Structure>("StructureCircuitHousing")` to find source, then `src.transform.Find("OnOffNoShadow")` to locate a child, `GameObject.Instantiate(srcOnOff, this.transform)` to graft the child into mod's prefab, then wires Collider + LogicOnOffButton references. Enables reusing specific vanilla sub-components without cloning the entire prefab.

Cloning the whole prefab (see `./PrefabCloning.md`) is appropriate when the mod wants the full device behavior. When the mod wants one of its sub-pieces (the button, the dish's mesh, the cable connector), grafting is lighter and keeps the mod's prefab shape under the mod's control.

## Solution / recipe
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

```csharp
public override void OnPrefabLoad()
{
    base.OnPrefabLoad();
    var src = PrefabUtils.FindPrefab<Structure>("StructureCircuitHousing");
    var srcOnOff = src.transform.Find("OnOffNoShadow");
    var graftedOnOff = GameObject.Instantiate(srcOnOff.gameObject, this.transform);
    // Wire cross-references by hand:
    this.OnOffCollider = graftedOnOff.GetComponent<Collider>();
    this.OnOffButton = graftedOnOff.GetComponent<LogicOnOffButton>();
}
```

Steps:

1. Locate the source prefab via `PrefabUtils.FindPrefab<T>("PrefabName")`.
2. Locate the specific child via `transform.Find("ChildName")`.
3. `Instantiate` the child as a new GameObject parented to the mod's prefab transform.
4. Wire any cross-references (the mod's own fields, components in the mod's prefab that reference the button).

### Hook timing

`OnPrefabLoad` runs during `Prefab.LoadAll`, the same phase as full prefab cloning. See `./PrefabCloning.md` for the Harmony patch entry point.

### When to prefer grafting over cloning

- The mod's device is a new design (not a variant of an existing device). Full cloning produces a variant; grafting builds a new thing.
- The mod author controls the rest of the device layout (meshes, colliders, scripts). Cloning conflicts with that control.
- Only a small sub-piece of the vanilla prefab is needed.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; single source (F0229j) generalized from FPGA's implementation.

## Open questions

None at creation.
