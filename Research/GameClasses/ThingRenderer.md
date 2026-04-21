---
title: ThingRenderer
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-21
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.ThingRenderer
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Thing.Renderers
related:
  - ./Thing.md
  - ./ColorSwatch.md
  - ../GameSystems/RenderingPipelineAndGlow.md
tags: [prefab, unity]
---

# ThingRenderer

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Vanilla game class at `Assets.Scripts.Objects.ThingRenderer`. Wrapper around a Unity `Renderer` that unifies material access across two drawing paths (`MeshRenderer` and `DrawData`, used for batched / instanced geometry). Each `Thing` carries a `List<ThingRenderer>` in its `Renderers` field; mod code that needs to read or mutate the rendered material for a Thing goes through this wrapper.

## Containing collection

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

`Thing.Renderers` at decompile Thing.cs line 534:

```
public List<ThingRenderer> Renderers = new List<ThingRenderer>();
```

Populated during `Thing` initialization from the prefab's renderers. May be empty for Things with no visible geometry. Mod code must guard with `thing.Renderers.Count > 0` before indexing.

## Material accessors

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

`ThingRenderer` exposes three accessors for the underlying material(s):

| Accessor | Line | Returns | Notes |
|---|---|---|---|
| `Materials { get; }` | ThingRenderer.cs:130 | `Material[]` | Clones on the `DrawData` path, returns the shared-material array on the `MeshRenderer` path. Use when the intent is "read or mutate per-instance". |
| `sharedMaterials { get; }` | ThingRenderer.cs:160 | `Material[]` | Always returns the shared-material array (or a clone on `DrawData`). Can be null if the parent was destroyed. |
| `GetMaterial()` | ThingRenderer.cs:247 | `Material` | Convenience getter returning `_drawData.materials[0]`. `DrawData`-specific. |

Callers that want to read the current emission color or shader name use `Materials[0]` (or `sharedMaterials[0]`) and then call Unity's `Material.GetColor("_EmissionColor")`, `.shader.name`, `.IsKeywordEnabled("_EMISSION")`, etc.

The underlying `UnityRenderer` field (ThingRenderer.cs:17) is private; the three accessors above are the supported entry points.

## Null safety

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

`Materials` and `sharedMaterials` can return null on the `DrawData` path if the parent Thing has been destroyed (ThingRenderer.cs:168-169). Guard:

```
if (thing.Renderers.Count > 0)
{
    var mat = thing.Renderers[0].Materials[0];
    if (mat != null) { ... }
}
```

## Related vanilla usage

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

`Thing.SetCustomColor(int index, bool emissive = false)` iterates every entry in `Renderers` to write the `_EmissionColor` / `DiffuseIndexPropertyID` / `SmoothnessIndexPropertyID` shader properties. See `./ColorSwatch.md` "Normal vs Emissive selection" for the full body. Mods that need to inspect the shader state post-`SetCustomColor` read the renderer list and the material on each entry.

## Verification history

- 2026-04-21: page created. Findings from decompile of Assembly-CSharp.dll (ThingRenderer.cs line 17 for the private UnityRenderer field, line 130 for `Materials`, line 160 for `sharedMaterials`, line 247 for `GetMaterial`; Thing.cs line 534 for `Thing.Renderers`).

## Open questions

- What triggers `ThingRenderer` to select the `DrawData` path over a plain `MeshRenderer`? Suspected: `structureRenderMode != Standard` (see `./Structure.md`), but unconfirmed.
- Whether repeated calls to `Materials` in the `MeshRenderer` path clone the material each time (Unity's `.material` accessor semantics) or whether `ThingRenderer` caches the clone internally. Needs a runtime probe.
