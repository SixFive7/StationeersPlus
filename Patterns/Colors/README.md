# Patterns/Colors

Shared conventions and code for the vanilla Stationeers `ColorSwatch` index across every SixFive7 mod.

The single source of truth for which integer index maps to which built-in color swatch lives in [`ColorSwatchIndex.cs`](ColorSwatchIndex.cs) in this folder. A mod that refers to a vanilla color by index links that file via:

```xml
<Compile Include="..\..\..\Patterns\Colors\ColorSwatchIndex.cs" Link="Patterns\ColorSwatchIndex.cs" />
```

so the mod reads `StationeersPlus.Shared.ColorSwatchIndex.<Name>` instead of hard-coding a literal `0`-`11`.

## Why this exists

The game keeps its paint colors in `GameManager.CustomColors`, a list whose first 12 entries are the vanilla swatches. Code that paints a thing, reads a paint color, or cycles through colors refers to a swatch by its position in that list (an `int`). The mapping is stable for the vanilla set, but a bare `4` in mod code says nothing about which color it is, and a typo points at the wrong swatch with no compiler error.

Centralising the 12 indices as named constants makes mod code read in color names, gives one place to re-verify when the game updates, and documents the boundary between vanilla swatches (fixed, owned by the game) and mod-registered swatches (variable, install-dependent).

This is the color counterpart to [`../Logic/`](../Logic/), which does the same job for custom `LogicType` numbers.

## The vanilla swatches

Verified against `Assembly-CSharp` in game version 0.2.6228.27061. Full detail (materials, emissive behaviour, the decompiled sources) is in [`Research/GameClasses/ColorSwatch.md`](../../Research/GameClasses/ColorSwatch.md); this table is the index map only.

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

## Rules

1. The 12 entries above are the vanilla set. Do not add mod-registered swatches to `ColorSwatchIndex.cs`.
2. Never renumber an existing value. Savegames serialise a thing's paint color by index, IC10 scripts and the paint UI read it, and multiplayer sync depends on host and client agreeing on it. Renumbering repaints existing things and breaks scripts.
3. The constant names match the game's swatch names exactly (`ColorBlue` ... `ColorPurple`), so they trace one-to-one to `Research/GameClasses/ColorSwatch.md` and to the game.
4. Re-verify against `Research/GameClasses/ColorSwatch.md` when the game updates. If the game ever reorders or renames the vanilla swatches, update `ColorSwatchIndex.cs` and the Research page in the same change.

## Mod-registered swatches (index 12 and up)

Mods that add their own swatches append to `GameManager.CustomColors`, so their indices land at 12 and above and depend on load order and which mods are installed. They are not stable across installs and are therefore not listed here. Resolve a mod swatch by looking it up in `CustomColors` at runtime (by material or name), not by a hard-coded index. Vanilla swatches also carry both `Normal` and `Emissive` materials; mod-added swatches may leave `Emissive` null, so null-check when iterating the full list.

## Source / verification

- [`Research/GameClasses/ColorSwatch.md`](../../Research/GameClasses/ColorSwatch.md): the verified `ColorSwatch` writeup (index table, materials, decompiled `Assembly-CSharp` sources, emissive behaviour).
- [`Research/GameSystems/RenderingPipelineAndGlow.md`](../../Research/GameSystems/RenderingPipelineAndGlow.md): how the swatch materials feed the rendering/glow path.
