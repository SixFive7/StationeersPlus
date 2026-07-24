# Patterns/Colors

Shared conventions and code for the game-owned Stationeers `ColorSwatch` indices across every SixFive7 mod.

The single source of truth for which integer index maps to which built-in color swatch lives in [`ColorSwatchIndex.cs`](ColorSwatchIndex.cs) in this folder. A mod that refers to a game color by index links that file via:

```xml
<Compile Include="..\..\..\Patterns\Colors\ColorSwatchIndex.cs" Link="Patterns\ColorSwatchIndex.cs" />
```

so the mod reads `StationeersPlus.Shared.ColorSwatchIndex.<Name>` instead of hard-coding a literal `0`-`15`.

## Why this exists

The game keeps its paint colors in `GameManager.CustomColors`, a list whose first 16 entries are game content (12 vanilla plus 4 from the Metallic Paints DLC). Code that paints a thing, reads a paint color, or cycles through colors refers to a swatch by its position in that list (an `int`). The mapping is stable for the game's own set, but a bare `4` in mod code says nothing about which color it is, and a typo points at the wrong swatch with no compiler error.

Centralising the 16 indices as named constants makes mod code read in color names, gives one place to re-verify when the game updates, and documents the boundary between game swatches (fixed, owned by the game) and mod-registered swatches (variable, install-dependent).

One thing this file deliberately does not do is answer "may the player use this color". The four DLC indices exist on every install, owned or not. Naming an index and being allowed to paint with it are separate questions; see rule 2 below.

This is the color counterpart to [`../Logic/`](../Logic/), which does the same job for custom `LogicType` numbers.

## The vanilla swatches

Verified against `Assembly-CSharp` in game version 0.2.6228.27061, re-confirmed by runtime enumeration in 0.2.6403.27689. Full detail (materials, emissive behaviour, the decompiled sources) is in [`Research/GameClasses/ColorSwatch.md`](../../Research/GameClasses/ColorSwatch.md); this table is the index map only.

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

## The Metallic Paints DLC swatches (indices 12-15)

The Metallic Paints DLC adds four more swatches. They are game content, not mod content, and they occupy fixed indices directly after the vanilla twelve:

| Index | Name | Dispensing prefab |
|---|---|---|
| 12 | ColorObsidian | ItemSprayCanMetallicObsidian |
| 13 | ColorSilver | ItemSprayCanMetallicSilver |
| 14 | ColorBronze | ItemSprayCanMetallicBronze |
| 15 | ColorGold | ItemSprayCanMetallicGold |

Three things to know before touching these:

- **They are present whether or not the player owns the DLC.** The swatches are in `GameManager.CustomColors` and the can prefabs are in `Prefab.AllPrefabs` on every install. Only *acquiring the can* is gated, by `Thing.DLCType` on the prefab. So the presence of a swatch says nothing about whether the player may use it.
- **Never gate on the index, and never gate on `ColorSwatch.PaintOnly`.** All four carry `PaintOnly = true` today, but that flag means "spray-paint-only, hidden from logic dropdowns", not "requires DLC"; the overlap is a coincidence the game does not promise to keep. Resolve entitlement through the dispensing prefab's `DLCType` and check it with `SharedDLCManager.CheckSharedAccess`, which is what vanilla itself does. See [`Research/GameSystems/DLCGating.md`](../../Research/GameSystems/DLCGating.md).
- **The names do not follow the prefabs.** The swatch is `ColorObsidian`, the prefab is `ItemSprayCanMetallicObsidian`: the swatch names drop the `Metallic` prefix. The swatch order also matches neither alphabetical order nor the recipe order in `paints.xml`. Do not derive one from the other by string manipulation. (The vanilla set has the same hazard: prefab `ItemSprayCanGrey` against swatch `ColorGray`.)

These four are in [`ColorSwatchIndex.cs`](ColorSwatchIndex.cs) as `ColorObsidian`, `ColorSilver`, `ColorBronze`, and `ColorGold`. They are named there because the indices are game-owned and stable, the same reason the vanilla twelve are. A named constant is for reading and writing an index unambiguously; it is not permission to use the color. Entitlement is a separate question and always goes through the dispensing prefab.

## Rules

1. The 16 game-owned entries (12 vanilla plus 4 from the Metallic Paints DLC) are the set `ColorSwatchIndex.cs` covers. Do not add mod-registered swatches to it; their indices are install-dependent.
2. A constant names an index; it does not grant access. Never treat an index being in range, or `ColorSwatch.PaintOnly` being true, as an entitlement check. Resolve entitlement through the dispensing spray can prefab's `Thing.DLCType` and test it with `SharedDLCManager.CheckSharedAccess`.
3. Never renumber an existing value. Savegames serialise a thing's paint color by index, IC10 scripts and the paint UI read it, and multiplayer sync depends on host and client agreeing on it. Renumbering repaints existing things and breaks scripts.
4. The constant names match the game's swatch names exactly (`ColorBlue` ... `ColorGold`), so they trace one-to-one to `Research/GameClasses/ColorSwatch.md` and to the game. Note this means the DLC constants carry no `Metallic` prefix even though their prefab names do.
5. Re-verify against `Research/GameClasses/ColorSwatch.md` when the game updates. If the game ever reorders or renames a swatch, or ships more paint, update `ColorSwatchIndex.cs` and the Research page in the same change.

## Mod-registered swatches (index 16 and up)

Mods that add their own swatches append to `GameManager.CustomColors`, so their indices land after whatever game content is already in the list. That boundary has MOVED: it was index 12 before the Metallic Paints DLC and is index 16 after it, and it will move again if the game ships more paint. Never hard-code the first mod-registered slot, and never assume "index >= 12 means a mod put it there."

Mod swatch indices depend on load order and on which mods are installed, so they are not stable across installs and are not listed here. Resolve a mod swatch by looking it up in `CustomColors` at runtime (by material or name), not by a hard-coded index. Game swatches all carry both `Normal` and `Emissive` materials; mod-added swatches may leave `Emissive` null, so null-check when iterating the full list.

Runtime enumeration in 0.2.6403.27689 with roughly 60 Workshop mods loaded found `CustomColors.Count == 16`, meaning none of those mods registers a swatch. Mod-added swatches are rare in practice, which is exactly why code that assumes "everything past the vanilla twelve is a mod swatch" went unnoticed until the DLC landed.

## Source / verification

- [`Research/GameClasses/ColorSwatch.md`](../../Research/GameClasses/ColorSwatch.md): the verified `ColorSwatch` writeup (index table, materials, decompiled `Assembly-CSharp` sources, emissive behaviour, the `PaintOnly` flag, and the runtime-confirmed metallic swatch table).
- [`Research/GameSystems/DLCGating.md`](../../Research/GameSystems/DLCGating.md): how the game gates DLC content, why there is no entitlement check on the paint path, and the correct way for a mod to resolve a color index to a `DLCType`.
- [`Research/GameClasses/SprayCan.md`](../../Research/GameClasses/SprayCan.md): the one-prefab-per-color model and the sixteen can prefabs that carry the color-to-DLC mapping.
- [`Research/GameSystems/RenderingPipelineAndGlow.md`](../../Research/GameSystems/RenderingPipelineAndGlow.md): how the swatch materials feed the rendering/glow path.
