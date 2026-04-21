---
title: ColorSwatch
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-21
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.ColorSwatch
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Thing.SetCustomColor
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Thing.LoadSimData
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.PowerTool.SelectColorSwatchMaterial
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.ChemLight.OnInteractableUpdated
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.RoadFlare.OnInteractableUpdated
related:
  - ./Thing.md
  - ./GameManager.md
  - ../Protocols/ThingColorMessage.md
  - ../GameSystems/RenderingPipelineAndGlow.md
tags: [prefab]
---

# ColorSwatch

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Vanilla game class at `Assets.Scripts.Objects.ColorSwatch`. Serializable container for one entry in `GameManager.CustomColors`. Holds two `Material` references (`Normal` and `Emissive`) plus light and localization metadata. `Thing.CustomColor` holds a reference to one of these; `ThingSaveData.CustomColorIndex` is the on-disk identifier of which swatch is active on a given Thing.

## Declaration and fields

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

`ColorSwatch` is a C# `class` (not a struct), serializable for Unity inspector editing, with no explicit base. Fields in declaration order:

| Member | Type | Notes |
|---|---|---|
| `Name` | `public string` | Inspector display name (ReadOnly). |
| `_index` | `private int = -1` | Backing cache for `Index`. |
| `Bit` | `public int` | Legacy field, currently unused. |
| `StringKey` | `public int` | Localization key (ReadOnly). |
| `Normal` | `public Material` | Standard (non-emissive) material. Always set on live swatches. |
| `Emissive` | `public Material` | Emissive material. MAY BE NULL: vanilla code contains `if (CustomColor.Emissive == null)` null-checks, so not every swatch ships an emissive variant. Colors without `Emissive` cannot be rendered in emissive mode via the material-swap path. |
| `Cutable` | `public Material` | Present on the inspector; code path that consumes it has not been traced. |
| `Light` | `public Color = Color.white` | Color applied to `LensFlare.color` on each `SetCustomColor` call. |
| `Color` | `public Color = Color.white` | Forced base color (ReadOnly). |

Properties:

- `public int Index { get; }` — lazy, calls `GameManager.GetColorIndex(this)` on first access and caches in `_index`.
- `public bool IsSet => Index != -1`.
- `public string DisplayName => Localization.GetName(this)`.

## ColorSwatch list

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

`GameManager.CustomColors` is a `public List<ColorSwatch>`, populated via the Unity inspector on the `GameManager` prefab (serialized field, not assembled at runtime). `GameManager.IsValidColor(int index)` checks `0 <= index < CustomColors.Count`. `Thing.SetCustomColor` silently drops invalid indices: the method returns early without setting the color and without logging.

Because `Emissive` is optional per swatch, any code path that wants to render a Thing in emissive mode must null-check `CustomColor.Emissive` before relying on the material swap; for swatches where `Emissive` is null, only the shader-property path (setting `_EmissionColor` on the `Normal` material) produces visual change.

## Normal vs Emissive selection

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

`Thing.SelectColorSwatchMaterial(bool emissive)` is overridable. `PowerTool.SelectColorSwatchMaterial` (decompile line 139053-139061) is the canonical body:

```
public override Material SelectColorSwatchMaterial(bool emissive)
{
    if (!emissive)
    {
        return CustomColor.Normal;
    }
    return CustomColor.Emissive;
}
```

`Thing.SetCustomColor(int index, bool emissive = false)` (decompile line 302860-302911) calls `SelectColorSwatchMaterial(emissive)` to choose the material, then for each `CustomColorMapping` entry in `_customMaterials`:

```
if (emissive)
    customMaterial.SetEmissive(material);
else
    customMaterial.SetColor(material, index);
```

After the per-mapping swap, the method writes the shader-level `_EmissionColor` vector on every `ThingRenderer`:

```
EmissionColor = Color.white * (emissive ? 1f : 0f);
foreach (ThingRenderer renderer in Renderers)
{
    renderer.SetShaderVectorProperty(EMISSION_COLOR, EmissionColor);
    renderer.SetShaderFloatProperty(DiffuseIndexPropertyID, DiffuseIndex);
    ...
}
```

`EMISSION_COLOR` is a cached `Shader.PropertyToID("_EmissionColor")` on `Thing`. So the renderer is driven by two independent signals on every color change: the material-swap (material asset changes) and the `_EmissionColor` shader property (value changes from `Color.black` to `Color.white`).

## Transience of the emissive flag

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

The `emissive` parameter is NOT stored on `Thing`. Each `SetCustomColor` call recomputes the material swap and the `_EmissionColor` write; there is no `IsEmissive` field on `Thing` or on `ThingSaveData`. Consequences:

- **Save/load**: `ThingSaveData` persists only `CustomColorIndex`. `Thing.LoadSimData` (decompile line 302262-302287) calls `SetCustomColor(saveData.CustomColorIndex)` with the default `emissive: false`. A flare that was glowing when the save was written loads non-glowing.
- **Network**: `ThingColorMessage` carries only `(long ThingId, int ColorIndex)`. `OnServer.SetCustomColor` (decompile line 39449-39463) calls `thing.SetCustomColor(colorIndex)` without passing an emissive value; `ThingColorMessage.Process` (line 259881-259883) does the same on the receiver. Emissive state is local-only; see `../Protocols/ThingColorMessage.md`.
- **Any re-entry reverts**: color change, color-message receive, save-load — each path passes `emissive: false` and reverts the renderer to the Normal material with `_EmissionColor = Color.black`.

Mods that want emissive/glow to persist and sync must store their own `IsGlowing` flag per Thing and re-apply emissive after every `SetCustomColor` entry.

## Vanilla callers of SetCustomColor with emissive: true

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Only two sites in vanilla pass `emissive: true`:

- `ChemLight.OnInteractableUpdated` at decompile line 322433. Called when a chemical light's state flips to On.
- `RoadFlare.OnInteractableUpdated` at decompile line 334170. Called when a flare's state flips to On.

No other vanilla code path passes `emissive: true`. Painted structures (walls, pipes, cables, frames) never receive it, which is why spray-painting a red pipe today never produces a glow: the API supports it but no caller invokes it.

Because both callers re-apply `emissive: true` on every state change (via `OnInteractableUpdated` firing on the On/Off toggle), the transience of the flag does not matter for flares in single-player. In multiplayer it still fails to sync: a flare turned on by the server does not glow for remote clients.

## Vanilla swatch inventory (v0.2.6228.27061)

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

`GameManager.CustomColors` ships with 12 entries in game version 0.2.6228.27061. All 12 carry both `Normal` and `Emissive` materials; none has `Emissive == null`.

| Index | Name |
|---|---|
| 0 | ColorBlue |
| 1 | ColorGray |
| 2 | ColorGreen |
| 3 | ColorOrange |
| 4 | ColorRed |
| 5 | ColorYellow |
| 6 | ColorWhite |
| 7 | ColorBlack |
| 8 | ColorBrown |
| 9 | ColorKhaki |
| 10 | ColorPink |
| 11 | ColorPurple |

Verified via the `Plans/GlowPaintProbe/` probe plugin's `OnPrefabsLoaded` enumeration on 2026-04-21 in game version 0.2.6228.27061. The per-entry breakdown (`name="..." normal=yes emissive=yes`) is recorded in the probe's startup log lines.

The `if (CustomColor.Emissive == null)` null-checks that appear in vanilla decompiled code (documented above in "Declaration and fields") remain defensive code: the possibility of a null `Emissive` is coded against, but no shipping vanilla swatch currently hits the null branch. Mod-added swatches may still populate `Emissive` as null, and any mod iterating `CustomColors` across installs should continue to null-check.

## Vanilla swatch material naming (runtime)

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Runtime observation via the `Plans/GlowPaintProbe/` plugin on 2026-04-21 captured the material and shader details for painted pipes (`Piping` and `InLineTank` classes receiving `SetCustomColor(index, emissive)`):

| Mode | Material name | Shader | `_EmissionColor` | `_EMISSION` keyword |
|---|---|---|---|---|
| Normal | `TextureArrayColorSwatch` (shared across all swatches) | `Custom/StandardTextureArray` | `(0, 0, 0, 0)` | off |
| Emissive | `Color<Name>Emissive` (per-color, e.g. `ColorPurpleEmissive`) | `StandardInstanced` | HDR pre-baked per color | on |

Findings:

- The `Normal` material is SHARED across all swatches (`TextureArrayColorSwatch`) and selects the color via texture-array indexing, consistent with the `Custom/StandardTextureArray` shader name.
- The `Emissive` material is PER-COLOR (one Material asset per swatch, named `Color<Name>Emissive`), not shared.
- The emissive shader is Unity's `StandardInstanced` (GPU-instanced Standard), which supports the `_EMISSION` keyword and `_EmissionColor` property.
- Emissive `_EmissionColor` values exceed `1.0` on individual components, encoding HDR brightness that UltimateBloom picks up into a visible halo. For `ColorPurple`: `(1.051, 0.000, 2.290, 1.000)`.
- `SetCustomColor(index, emissive: true)` drives two changes simultaneously: material asset swap (from shared Normal to per-color Emissive) AND `_EmissionColor` shader-property write. The `_EMISSION` shader keyword is enabled on the Emissive material.

Runtime evidence for a `Piping` instance with `CustomColor.Index = 11` (ColorPurple):

```
SetCustomColor(11, emissive: true)
  -> renderer[0] shader="StandardInstanced" material="ColorPurpleEmissive" _EmissionColor=RGBA(1.051, 0.000, 2.290, 1.000) _EMISSION=on

SetCustomColor(11, emissive: false)
  -> renderer[0] shader="Custom/StandardTextureArray" material="TextureArrayColorSwatch" _EmissionColor=RGBA(0.000, 0.000, 0.000, 0.000) _EMISSION=off
```

Verified visually: the pipe glows with a bloom halo in a dark room after F9; reverts to flat-colored pipe after F10. Confirmed via `Plans/GlowPaintProbe/` probe logs on 2026-04-21 in game version 0.2.6228.27061.

## Verification history

- 2026-04-21: page created. Decompile findings sourced from Assembly-CSharp.dll (ColorSwatch declaration, Thing.SetCustomColor body at line 302860-302911, PowerTool.SelectColorSwatchMaterial at line 139053-139061, Thing.LoadSimData at line 302262-302287, ChemLight.OnInteractableUpdated at line 322433, RoadFlare.OnInteractableUpdated at line 334170).
- 2026-04-21: added "Vanilla swatch inventory (v0.2.6228.27061)" section with the 12 shipping swatches and their Normal/Emissive presence, verified via the GlowPaintProbe plugin logs. Resolves the open question about how many swatches populate `Emissive`: the answer is all 12. Defensive null-check guidance retained for mod-added swatches.
- 2026-04-21: added "Vanilla swatch material naming (runtime)" section documenting the `TextureArrayColorSwatch` + `Custom/StandardTextureArray` normal pair and per-color `Color<Name>Emissive` + `StandardInstanced` emissive pair, with HDR `_EmissionColor` values and keyword state. Confirmed visible bloom halo on a dark-room pipe via the probe's F9 / F10 flow. Verified through `Plans/GlowPaintProbe/` plugin logs.

## Open questions

- `ColorSwatch.Cutable` Material: inspector-visible but the consumer has not been traced. Possibly a cut-out / cutscene variant.
- Does setting `_EmissionColor` alone on the shared `Normal` material (without swapping to the Emissive asset) produce visible glow? The Normal shader is `Custom/StandardTextureArray` (a custom game shader); whether it honors `_EmissionColor` with the `_EMISSION` keyword disabled is unknown. Because the Normal material is SHARED across all painted Things, the property-write path would require a per-instance material clone per `../Patterns/UnityMaterialPerInstance.md`. Relevant only for mod-added swatches whose `Emissive` is null, since every vanilla swatch has a pre-baked Emissive asset.
