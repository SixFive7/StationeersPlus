---
title: Rendering Pipeline and Glow Implementation
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-21
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.CameraController
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.CameraEffectCollection
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Thing
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.ColorSwatch
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.CustomColorMapping
  - Mods/PowerTransmitterPlus/PowerTransmitterPlus/BeamManager.cs
  - Mods/SprayPaintPlus/TODO.md
related:
  - ../Protocols/ThingColorMessage.md
  - ../Patterns/UnityMaterialPerInstance.md
  - ../GameClasses/Thing.md
  - ../GameClasses/ColorSwatch.md
  - ../GameClasses/CameraController.md
  - ../GameClasses/ThingRenderer.md
tags: [unity]
---

# Rendering Pipeline and Glow Implementation

## A. Render pipeline identification

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Stationeers uses Unity's built-in render pipeline on Unity 2021.2 LTS (same toolchain as `../Patterns/AsyncEnumerator472.md`).

Key evidence:

1. Bloom post-processing: the third-party UltimateBloom asset is attached via `CameraEffectCollection.Bloom`. The `CameraController.SetBloom(bool)` API toggles it. Enabled by default on vanilla gameplay cameras.
2. No URP / HDRP: only Legacy Shaders and built-in-pipeline shader references appear in the decompile.
3. Camera post-processing uses the old `UnityStandardAssets.ImageEffects` family, not the newer Post-Processing V2 stack.

UltimateBloom is what turns bright emissive pixels into a visible halo. Without bloom, an emissive material renders self-lit but produces no glow halo. Any glow feature for painted objects depends on the camera's bloom pass being on. The runtime read accessor is `CameraController.Instance.CameraEffects[0].Bloom`; check `.enabled` to confirm the component is active. See `../GameClasses/CameraController.md` section "Singleton and effect collections" for the full accessor chain.

## B. Shaders and emission on paintable objects

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Every `ColorSwatch` in `GameManager.CustomColors` can ship up to two materials per color (see `../GameClasses/ColorSwatch.md`):

- `ColorSwatch.Normal` - standard diffuse, always set on live swatches.
- `ColorSwatch.Emissive` - pre-baked emissive material, OPTIONAL: some swatches ship with `Emissive == null` and cannot be rendered in emissive mode via the material swap.

The game's shader system respects the `_EmissionColor` property. From the `Thing` decompile:

```
public static readonly int EMISSION_COLOR = Shader.PropertyToID("_EmissionColor");

EmissionColor = Color.white * (emissive ? 1f : 0f);
foreach (ThingRenderer renderer in Renderers)
{
    renderer.SetShaderVectorProperty(EMISSION_COLOR, EmissionColor);
}
```

On every `SetCustomColor` call, the game drives the renderer with two independent signals: the material-swap (via `CustomColorMapping.SetEmissive(material)` on colors that have an `Emissive` variant) and the shader property write (`_EmissionColor` on every `ThingRenderer`). The property write happens unconditionally regardless of whether the swatch has an `Emissive` material.

Mod code that wants to read the post-call renderer state goes through `ThingRenderer.Materials` / `sharedMaterials` / `GetMaterial()` on each entry in `Thing.Renderers`; see `../GameClasses/ThingRenderer.md` for the accessor shapes and null-safety caveats.

## C. How existing glowing things work in-game

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Vanilla uses the `ColorSwatch.Emissive` + `_EmissionColor` approach for ChemLights and RoadFlares only (see `../GameClasses/ColorSwatch.md` section "Vanilla callers of SetCustomColor with emissive: true"). When a flare is toggled on, `OnInteractableUpdated` calls `SetCustomColor(index, emissive: true)`; the renderer picks up the Emissive material (if present) and gets `_EmissionColor = Color.white`. UltimateBloom renders the halo.

No per-object point lights are used for this effect. The halo is entirely shader-driven.

Painted structures (walls, pipes, cables, frames) never receive `emissive: true` in vanilla. The API supports it but no caller invokes it; that is the gap `SprayPaintPlus` would fill.

## D. Mod prior art: PowerTransmitterPlus beam visuals

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

`PowerTransmitterPlus` renders its power beam with a `LineRenderer` using `Legacy Shaders/Particles/Additive`:

```
var shader = Shader.Find("Legacy Shaders/Particles/Additive")
           ?? Shader.Find("Particles/Additive")
           ?? Shader.Find("Sprites/Default")
           ?? Shader.Find("Hidden/Internal-Colored");
```

Additive shading produces bloom-friendly brightness, so the beam appears to glow under UltimateBloom. Beam color is set via `startColor` / `endColor`; no `_EmissionColor` manipulation is used. This is a proof point that runtime-created additive materials work under the game's rendering and bloom into a visible halo.

## E. Material-per-instance pattern

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

For per-instance material mutation (glow intensity per-Thing, custom emission color per-paint), follow `../Patterns/UnityMaterialPerInstance.md`:

1. Read `renderer.material` once to trigger the clone; assignment to subsequent renderers on the same GameObject can share.
2. Cache the cloned `Material` reference (e.g. keyed by `Thing.ReferenceId`).
3. Mutate via `SetColor("_EmissionColor", ...)`, `EnableKeyword("_EMISSION")`, etc.
4. `Destroy(material)` in `OnDestroy` or the clone leaks.

## F. The practical decision: how to implement glow

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Three viable techniques, in order of preference:

1. **Call vanilla `Thing.SetCustomColor(index, emissive: true)`**. Cleanest; uses the existing API; swaps to `Emissive` material and sets `_EmissionColor`. Limitations: the flag is transient and re-enters on every color change / sync / load, so a mod must re-apply after every vanilla `SetCustomColor` call. Swatches whose `Emissive` is null produce no material swap (only the `_EmissionColor` write), which may or may not glow depending on whether the `Normal` material's shader honors the property.

2. **Per-instance material clone + direct `_EmissionColor` write**. Bypass `SetCustomColor` entirely on the glow side: fetch `renderer.material` once, call `SetColor("_EmissionColor", CustomColor.Color * intensity)`. Gives full intensity control, works for swatches that have null `Emissive` as long as their `Normal` shader declares `_EmissionColor`. Costs one cloned material per glowing Thing; cleanup per `../Patterns/UnityMaterialPerInstance.md`.

3. **Shader swap to additive (fallback)**. If neither (1) nor (2) produces visible glow on some Things (batched structures, custom-shader children), mirror PowerTransmitterPlus's approach: create a new `Material` with `Shader.Find("Legacy Shaders/Particles/Additive")` and swap. Breaks normal diffuse lighting in lit areas, so reserve for cases where the Thing is only visible when glow matters.

What will NOT work:

- Encoding glow as a high bit in the color index. `GameManager.IsValidColor` clamps; see `../Protocols/ThingColorMessage.md`.
- Attaching per-object `Light` components. TODO-flagged as unacceptable for network-painting paint counts.
- Enabling the `_EMISSION` shader keyword without also ensuring the material variant exposes the keyword. On Unity Standard, `EnableKeyword("_EMISSION")` + `SetColor("_EmissionColor", c)` both are required; on a custom shader the keyword may be redundant or not present at all. Probe first, don't assume.

## G. Persistence and multiplayer gaps

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Vanilla's `emissive` parameter is transient (see `../GameClasses/ColorSwatch.md`). A mod feature that wants glow to survive save/load and multiplayer must:

1. Store an `IsGlowing` flag per Thing (custom `ThingSaveData` subclass; see `../Patterns/SaveDataIsinstInheritance.md` and `SaveDataRegistration.md`).
2. Sync the flag via a free `Thing.NetworkUpdateFlags` bit + postfixes on `Thing.BuildUpdate` / `ProcessUpdate` / `SerializeOnJoin` / `DeserializeOnJoin` (pattern per `Mods/SprayPaintPlus/RESEARCH.md` section 3.4, applied one level up on `Thing`).
3. Re-apply the emissive effect after every `SetCustomColor` postfix for Things whose flag is set, since the vanilla sync and load paths clear it.

The re-apply hook is what makes the feature work: any path that mutates color (paint, message receive, save load) passes `emissive: false` and clobbers the glow, so a postfix must read the glow flag and re-apply.

## H. Visibility proof recipe

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

To verify glow is visible in-game, use the probe plugin at `Plans/GlowPaintProbe/`:

1. Apply emissive to a painted pipe via direct `SetCustomColor(index, emissive: true)` or via per-instance `_EmissionColor` write.
2. Place the pipe in a dark room (no external lights).
3. A visible halo around the pipe confirms the bloom path is live.
4. In a lit room, the glow is a subtle brightness lift from the bloom pass.

If step 3 produces no halo, check:

- Is `CameraController.SetBloom` enabled? Confirm via the probe plugin's bloom log line (the runtime read accessor is documented in `../GameClasses/CameraController.md`).
- Does `CustomColor.Emissive` exist for the swatch? Confirm via the probe plugin's swatch enumeration (which logs `emissive=yes|no` per entry).
- Does the pipe's mesh have a `ThingRenderer` that was enumerated by `SetCustomColor`? `Thing.Renderers` is a public list; iterate and log via the probe's F9 handler (see `../GameClasses/ThingRenderer.md`).

## Verification history

- 2026-04-21: page created from decompiled Assembly-CSharp.dll and PowerTransmitterPlus source.
- 2026-04-21: corrected section C. Original claim "Every color has both Normal and Emissive materials" was not accurate; vanilla code contains `if (CustomColor.Emissive == null)` null-checks, so `Emissive` is optional per swatch. Page now links to `../GameClasses/ColorSwatch.md` which documents the class fully, and section F distinguishes the two glow paths (material swap vs. property write) based on whether `Emissive` is present. Also restamped section formatting to the `<!-- verified: ... -->` HTML-comment form required by `Research/CLAUDE.md`.
- 2026-04-21: added runtime read accessor chain for bloom to section A (`CameraController.Instance.CameraEffects[0].Bloom` with `.enabled` check) and cross-references to the new `../GameClasses/CameraController.md` and `../GameClasses/ThingRenderer.md` pages. Section H updated to reference the `Plans/GlowPaintProbe/` probe plugin now that InspectorPlus is off-limits. Additive; no prior claim changed.
- 2026-04-21: resolved the "fraction of swatches with non-null `Emissive`" open question via GlowPaintProbe plugin logs. All 12 shipping swatches in v0.2.6228.27061 carry non-null `Normal` and `Emissive` materials. Full inventory (names + presence) is documented in `../GameClasses/ColorSwatch.md` section "Vanilla swatch inventory (v0.2.6228.27061)". Approach F.1 is therefore viable for every vanilla paint color; approach F.2 only matters for mod-added swatches that leave `Emissive` null.
- 2026-04-21: approach F.1 visually and programmatically confirmed via GlowPaintProbe. Calling `Thing.SetCustomColor(index, emissive: true)` on a painted `Piping` instance in a dark room produces a visible bloom halo; `Plans/GlowPaintProbe/` log lines captured the runtime material swap from `TextureArrayColorSwatch` (shader `Custom/StandardTextureArray`) to `ColorPurpleEmissive` (shader `StandardInstanced`) with `_EmissionColor=(1.051, 0, 2.290, 1)` and `_EMISSION=on`. Reverts cleanly on `emissive: false`. Full details in `../GameClasses/ColorSwatch.md` section "Vanilla swatch material naming (runtime)". F.1 is green-lit for the SprayPaintPlus glow-paint implementation.

## Open questions

- Does setting `_EmissionColor` alone on the shared `Normal` material (approach F.2 without the material swap) produce visible glow? The Normal shader is `Custom/StandardTextureArray` (a custom shader); whether it honors the property without the `_EMISSION` keyword is unknown. The Normal material is shared across all painted Things, so this path would need a per-instance material clone (see `../Patterns/UnityMaterialPerInstance.md`). Relevant only for mod-added swatches whose `Emissive` is null, since every vanilla swatch ships both.
- Where is `UltimateBloom` actually attached at runtime? GlowPaintProbe observed `CameraController.Instance.CameraEffects.Count == 0` throughout gameplay, yet bloom halo is visibly active on emissive materials. The decompile-derived accessor `CameraController.Instance.CameraEffects[0].Bloom` returns empty; see `../GameClasses/CameraController.md` section "Runtime attachment (unresolved)". Not blocking for F.1 (bloom is working), but worth identifying before shipping a feature that needs to validate "bloom is on" from code.
