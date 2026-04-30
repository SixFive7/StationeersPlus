---
title: Layers
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-30
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Layers
related:
  - ./Thing.md
  - ./PowerTransmitter.md
tags: [unity, transforms]
---

# Layers

Vanilla static class at `Assets.Scripts.Layers`. Caches the integer indices of the Unity physics / rendering layers the game references by code. The class only enumerates layers that game code resolves by name; many other layers exist in the project's `TagManager` (cursor, structure clicks, etc.) but are referenced by string at the call site, not via this static.

## Definition
<!-- verified: 0.2.6228.27061 @ 2026-04-30 -->

Source: F-this-turn. `Assembly-CSharp.dll`, line 303759 of the 0.2.6228.27061 decompile.

```csharp
public static class Layers
{
    public static LayerMask CharacterCreation = LayerMask.NameToLayer("CharacterCreation");
    public static LayerMask PlayerInvisible   = LayerMask.NameToLayer("PlayerInvisible");
    public static LayerMask Player            = LayerMask.NameToLayer("Player");
    public static LayerMask PlayerImmune      = LayerMask.NameToLayer("PlayerImmune");
    public static LayerMask PlayerRagdoll     = LayerMask.NameToLayer("PlayerRagdoll");
    public static LayerMask Default           = LayerMask.NameToLayer("Default");
    public static LayerMask IgnoreRaycast     = LayerMask.NameToLayer("Ignore Raycast");
    public static LayerMask ThumbnailCreation = LayerMask.NameToLayer("ThumbnailCreation");
    public static LayerMask Terrain           = LayerMask.NameToLayer("Terrain");
}
```

The static initializers run on first access; `LayerMask.NameToLayer` returns the integer layer index (0-31) given the string name. The field type is `LayerMask` but the value stored is the implicit integer layer index, not a bitmask. To build a raycast mask from one of these, use `1 << Layers.X`.

## What is NOT in this class
<!-- verified: 0.2.6228.27061 @ 2026-04-30 -->

Source: same.

There is no enumerated entry for "Structure", "Items", "Pipes", "Cables", "Atmospherics", "PowerReceiver", "Dish", or any other Stationeers content category. Search across the decompile for `LayerMask.NameToLayer("...")` usages outside this class confirms only a handful of additional names appear ("CursorVoxel", "TransparentFX"), each resolved at the call site.

Practical consequence: code that wants to filter raycasts to "only structures" or "only obstacles" cannot use a content-typed layer mask, because the colliders for placed structures, pipes, cables, walls, machines, and dishes all live on the same general physics layer (`Default`). Filtering by Thing type after the cast (e.g. via `Thing._colliderLookup`) is the supported pattern.

The two filter-friendly layers actually exposed are `Terrain` (voxel ground only) and the player-related layers; everything else collapses into `Default`. `IgnoreRaycast` is Unity's standard "skipped by Physics.Raycast" layer (also referred to as `Ignore Raycast` with the space).

## Usage in vanilla raycasts
<!-- verified: 0.2.6228.27061 @ 2026-04-30 -->

Source: same.

`PowerTransmitter.TryContactReceiver` (line 387288 of the 0.2.6228.27061 decompile) does NOT pass a layer mask:

```csharp
Physics.Raycast(RayTransform.transform.position, RayTransform.transform.TransformDirection(Vector3.forward), out var hitInfo, float.PositiveInfinity)
```

The 4-argument overload defaults to `Physics.DefaultRaycastLayers`, which is "everything except IgnoreRaycast". The link probe therefore hits any collider in the scene (terrain, walls, pipes, cables, other dishes' meshes, atmospherics colliders, the player, etc.) and relies on the post-hit type filter (`hit.collider -> Thing._colliderLookup -> is PowerReceiver -> hit.transform == rx.DishTarget`) to reject everything that is not the partner dish's small `DishTarget` collider.

This is fine for the narrow ray on a perfect aim, but any modification that broadens the cast volume (`SphereCast`, `CapsuleCast`, multi-ray) inherits the same lack of layer filtering and must do its filtering after the cast.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-30 -->

- 2026-04-30: page created. Curated during research into PowerTransmitterPlus link-probe alternatives. Source: direct read of the 0.2.6228.27061 `Assembly-CSharp.dll` decompile around line 303759 (the static class definition) and line 387297 (the vanilla `TryContactReceiver` raycast call site, no layer mask argument). No conflict against existing pages; cross-link added on `PowerTransmitter.md` is appropriate but not required for this page to stand alone.

## Open questions

None at creation.
