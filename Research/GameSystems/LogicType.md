---
title: LogicType
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-25
sources:
  - Mods/PowerTransmitterPlus/RESEARCH.md:664-670
  - Mods/PowerTransmitterPlus/RESEARCH.md:398-425
  - Plans/StationpediaPlus/PLAN.md:225-236
  - Plans/StationpediaPlus/PLAN.md:240-255
  - Plans/StationpediaPlus/PLAN.md:229-236
  - Plans/StationpediaPlus/PLAN.md:240-254
  - Plans/StationpediaPlus/PLAN.md:3483-3506
  - Mods/PowerTransmitterPlus/PowerTransmitterPlus/LogicableInitializePatch.cs:55-71
  - Mods/PowerTransmitterPlus/PowerTransmitterPlus/EnumNamePatches.cs:9-16
related:
  - ./IC10SyntaxHighlighting.md
  - ./StationpediaPageRendering.md
  - ../GameClasses/ProgrammableChip.md
  - ../GameClasses/WirelessPower.md
  - ../GameClasses/PowerTransmitter.md
tags: [logic, ic10, ui]
---

# LogicType

`LogicType` is a `ushort` enum with 350 vanilla values (0-349) that names every logic readable and writable in the game. Extending it at runtime requires writing into four independent registries, all snapshotted from `Enum.GetValues(typeof(LogicType))` at class load. Missing any registry produces silent breakage in at least one UI surface (tablet, ConfigCartridge, motherboard dropdown, or in-game screen syntax highlighting).

## Four separate LogicType registries
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

The game stores LogicType names/values in four independent locations, each populated from `Enum.GetValues`/`GetNames` at class load. All four must be extended for full coverage:

1. `Logicable.LogicTypes` / `LogicTypeNames`: drives `NextLogicType` cycling in the tablet.
2. `EnumCollections.LogicTypes`: drives `ConfigCartridge` (tablet cartridge UI).
3. `ScreenDropdownBase.LogicTypes` / `LogicTypeNames`: drives motherboard condition/action dropdown menus.
4. `ProgrammableChip.InternalEnums` entries `ScriptEnum<LogicType>` and `BasicEnum<LogicType>`: drive **syntax highlighting** on all in-game screens (computers, laptops, wall-mounted screens). If not extended, custom LogicType names receive no `<color>` tag and inherit the screen's default red text color, appearing "invalid" even though they compile and execute correctly. See `./IC10SyntaxHighlighting.md`.

## LogicType enum value inventory (F0040)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`LogicType` enum (`Assets.Scripts.Objects.Motherboards.LogicType`): `ushort`, vanilla values 0-349 with one true gap at **159**. Examples: `Power = 1`, `On = 28`, `Charge = 11`, `Horizontal = 20`, `Vertical = 21`, `PowerPotential = 25`, `PowerActual = 26`, `Error = 4`, `PrefabHash = 84`, `ReferenceId = 217`, `PositionX/Y/Z = 76/77/78`.

LogicType is `[ushort]` so values up to 65535 are runtime-legal. The IC10 / MIPS parser resolves name tokens against `ProgrammableChip.AllConstants[].Literal` via `OrdinalIgnoreCase` string comparison, so out-of-enum values WORK as long as we register their names in that array.

`WirelessPower.CanLogicRead` returns true for: Charge, Horizontal, Vertical, PowerPotential, PowerActual, PositionX/Y/Z (plus Device-base ones: Power, On, Error, Mode, RequiredPower, PrefabHash, ReferenceId). `PowerTransmitter` and `PowerReceiver` inherit without override.

`WirelessPower.GetLogicValue` returns:
- `Horizontal` → `Horizontal × 360.0` (degrees)
- `Vertical` → `Vertical × 180.0`
- `HorizontalRatio` → `Horizontal` (0..1)
- `VerticalRatio` → `Vertical`
- `Charge` → `AvailablePower` = `InputNetwork.PotentialLoad`
- `PowerPotential` → `base.PotentialLoad`
- `PowerActual` → `base.CurrentLoad` = `OutputNetwork.CurrentLoad` ← **delivered watts**
- `PositionX/Y/Z` → `RayTransform.position`

The game keeps **three separate arrays of logic types**:
1. `Logicable.LogicTypes` / `LogicTypeNames`: used by `Logicable.NextLogicType` cycling.
2. `Assets.Scripts.EnumCollections.LogicTypes`: the `EnumCollection<LogicType, ushort>` consumed by `ConfigCartridge` (configuration tablet cartridge).
3. `Assets.Scripts.UI.Motherboard.ScreenDropdownBase.LogicTypes` / `LogicTypeNames`: IC housing on-screen dropdowns.

All three are populated from `Enum.GetValues(typeof(LogicType))` at class load. Extending only `Logicable`'s pair is not enough: the configuration tablet is driven by `EnumCollections.LogicTypes`. `ScreenDropdownBase` drives the motherboard condition/action dropdown UI. This mod extends all three.

`EnumCollection<T1, T2>` lives in `Assets.Scripts`, NOT `Assets.Scripts.Util`. `ProgrammableChip` lives in `Assets.Scripts.Objects.Electrical`, NOT `Motherboards`; `ProgrammableChip.Constant` is a nested `public readonly struct`.

The MIPS compiler resolves name tokens against `ProgrammableChip.AllConstants` directly: verified in-game for both `l r0 d0 MicrowaveSourceDraw` and `lbn ... MicrowaveSourceDraw 0`. The `Hash` field on `Constant` is for the `#hash` MIPS directive, not pure name lookups.

## Atmospheric gas-ratio LogicType members
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

Every combustion-relevant gas (fuels and oxidisers) has a chamber-scoped `Ratio*` LogicType member, plus per-network `Input` / `Input2` / `Output` / `Output2` variants. Verbatim from `Assets/Scripts/Objects/Motherboards/LogicType.cs` (decompile of `Assembly-CSharp.dll`, source order, with assigned integer values):

```csharp
RatioOxygen = 14,
RatioCarbonDioxide = 15,
RatioNitrogen = 16,
RatioPollutant = 17,
RatioMethane = 18,
RatioWater = 19,
Ratio = 24,
RatioNitrousOxide = 83,
RatioOxygenInput = 108,
RatioCarbonDioxideInput = 109,
RatioNitrogenInput = 110,
RatioPollutantInput = 111,
RatioMethaneInput = 112,
RatioWaterInput = 113,
RatioNitrousOxideInput = 114,
RatioOxygenInput2 = 118,
RatioCarbonDioxideInput2 = 119,
RatioNitrogenInput2 = 120,
RatioPollutantInput2 = 121,
RatioMethaneInput2 = 122,
RatioWaterInput2 = 123,
RatioNitrousOxideInput2 = 124,
RatioOxygenOutput = 128,
RatioCarbonDioxideOutput = 129,
RatioNitrogenOutput = 130,
RatioPollutantOutput = 131,
RatioMethaneOutput = 132,
RatioWaterOutput = 133,
RatioNitrousOxideOutput = 134,
RatioOxygenOutput2 = 138,
RatioCarbonDioxideOutput2 = 139,
RatioNitrogenOutput2 = 140,
RatioPollutantOutput2 = 141,
RatioMethaneOutput2 = 142,
RatioWaterOutput2 = 143,
RatioNitrousOxideOutput2 = 144,
RatioLiquidNitrogen = 177,
RatioLiquidNitrogenInput = 178,
RatioLiquidNitrogenInput2 = 179,
RatioLiquidNitrogenOutput = 180,
RatioLiquidNitrogenOutput2 = 181,
RatioLiquidOxygen = 183,
RatioLiquidOxygenInput = 184,
RatioLiquidOxygenInput2 = 185,
RatioLiquidOxygenOutput = 186,
RatioLiquidOxygenOutput2 = 187,
RatioLiquidMethane = 188,
RatioLiquidMethaneInput = 189,
RatioLiquidMethaneInput2 = 190,
RatioLiquidMethaneOutput = 191,
RatioLiquidMethaneOutput2 = 192,
RatioSteam = 193,
RatioSteamInput = 194,
RatioSteamInput2 = 195,
RatioSteamOutput = 196,
RatioSteamOutput2 = 197,
RatioLiquidCarbonDioxide = 199,
RatioLiquidCarbonDioxideInput = 200,
RatioLiquidCarbonDioxideInput2 = 201,
RatioLiquidCarbonDioxideOutput = 202,
RatioLiquidCarbonDioxideOutput2 = 203,
RatioLiquidPollutant = 204,
RatioLiquidPollutantInput = 205,
RatioLiquidPollutantInput2 = 206,
RatioLiquidPollutantOutput = 207,
RatioLiquidPollutantOutput2 = 208,
RatioLiquidNitrousOxide = 209,
RatioLiquidNitrousOxideInput = 210,
RatioLiquidNitrousOxideInput2 = 211,
RatioLiquidNitrousOxideOutput = 212,
RatioLiquidNitrousOxideOutput2 = 213,
RatioHydrogen = 252,
RatioLiquidHydrogen = 253,
RatioPollutedWater = 254,
RatioHydrazine = 283,
RatioLiquidHydrazine = 284,
RatioLiquidAlcohol = 285,
RatioHelium = 286,
RatioLiquidSodiumChloride = 287,
RatioSilanol = 288,
RatioLiquidSilanol = 289,
RatioHydrochloricAcid = 290,
RatioLiquidHydrochloricAcid = 291,
RatioOzone = 292,
RatioLiquidOzone = 293,
RatioHydrogenInput = 294,
RatioHydrogenInput2 = 295,
RatioHydrogenOutput = 296,
RatioHydrogenOutput2 = 297,
RatioLiquidHydrogenInput = 298,
RatioLiquidHydrogenInput2 = 299,
RatioLiquidHydrogenOutput = 300,
RatioLiquidHydrogenOutput2 = 301,
RatioPollutedWaterInput = 302,
RatioPollutedWaterInput2 = 303,
RatioPollutedWaterOutput = 304,
RatioPollutedWaterOutput2 = 305,
RatioHydrazineInput = 306,
RatioHydrazineInput2 = 307,
RatioHydrazineOutput = 308,
RatioHydrazineOutput2 = 309,
RatioLiquidHydrazineInput = 310,
RatioLiquidHydrazineInput2 = 311,
RatioLiquidHydrazineOutput = 312,
RatioLiquidHydrazineOutput2 = 313,
RatioLiquidAlcoholInput = 314,
RatioLiquidAlcoholInput2 = 315,
RatioLiquidAlcoholOutput = 316,
RatioLiquidAlcoholOutput2 = 317,
RatioHeliumInput = 318,
RatioHeliumInput2 = 319,
RatioHeliumOutput = 320,
RatioHeliumOutput2 = 321,
RatioLiquidSodiumChlorideInput = 322,
RatioLiquidSodiumChlorideInput2 = 323,
RatioLiquidSodiumChlorideOutput = 324,
RatioLiquidSodiumChlorideOutput2 = 325,
RatioSilanolInput = 326,
RatioSilanolInput2 = 327,
RatioSilanolOutput = 328,
RatioSilanolOutput2 = 329,
RatioLiquidSilanolInput = 330,
RatioLiquidSilanolInput2 = 331,
RatioLiquidSilanolOutput = 332,
RatioLiquidSilanolOutput2 = 333,
RatioHydrochloricAcidInput = 334,
RatioHydrochloricAcidInput2 = 335,
RatioHydrochloricAcidOutput = 336,
RatioHydrochloricAcidOutput2 = 337,
RatioLiquidHydrochloricAcidInput = 338,
RatioLiquidHydrochloricAcidInput2 = 339,
RatioLiquidHydrochloricAcidOutput = 340,
RatioLiquidHydrochloricAcidOutput2 = 341,
RatioOzoneInput = 342,
RatioOzoneInput2 = 343,
RatioOzoneOutput = 344,
RatioOzoneOutput2 = 345,
RatioLiquidOzoneInput = 346,
RatioLiquidOzoneInput2 = 347,
RatioLiquidOzoneOutput = 348,
RatioLiquidOzoneOutput2 = 349,
```

The bare `Ratio = 24` member is the generic ratio readout (used by some non-atmosphere devices for proportional state), unrelated to gas content.

Notably absent: there is no `RatioVolatiles` member in the current enum. Hydrogen is exposed as `RatioHydrogen = 252`. Older Stationeers IC10 references that mention `RatioVolatiles` are from a previous enum revision. Use `RatioHydrogen` in scripts written against this game version.

For atmosphere-readable devices (those with `HasReadableAtmosphere => true`, e.g. `CombustionDeepMiner`), the bare-name members (no `Input` / `Output` suffix) read the **chamber's internal atmosphere**, not any pipe network. The `Input` / `Input2` / `Output` / `Output2` variants read the corresponding pipe networks on devices that expose them.

### Gas / fuel relevance for combustion

Cross-reference with `Mole.Enthalpy` and `EnthalpyMultiplier` (verbatim tables in `../GameClasses/CombustionDeepMiner.md`):

| Role | Gas | Chamber LogicType | Enthalpy / Multiplier |
|---|---|---|---|
| Fuel | Hydrogen | `RatioHydrogen` | 306,000 J/mol |
| Fuel | LiquidHydrogen | `RatioLiquidHydrogen` | 306,000 J/mol |
| Fuel | Methane | `RatioMethane` | 286,000 J/mol |
| Fuel | LiquidMethane | `RatioLiquidMethane` | 286,000 J/mol |
| Fuel | Hydrazine | `RatioHydrazine` | 306,000 J/mol |
| Fuel | LiquidHydrazine | `RatioLiquidHydrazine` | 306,000 J/mol |
| Fuel | LiquidAlcohol | `RatioLiquidAlcohol` | 566,000 J/mol |
| Oxidiser | Oxygen | `RatioOxygen` | ×1.0 |
| Oxidiser | LiquidOxygen | `RatioLiquidOxygen` | ×1.0 |
| Oxidiser | NitrousOxide | `RatioNitrousOxide` | ×2.0 |
| Oxidiser | LiquidNitrousOxide | `RatioLiquidNitrousOxide` | ×2.0 |
| Oxidiser | Ozone | `RatioOzone` | ×2.0 |
| Oxidiser | LiquidOzone | `RatioLiquidOzone` | ×2.0 |

This is the full set of IC10-readable gases that contribute to `Atmosphere.CombustionEnergy` accumulation.

## Reserved bands
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`LogicType` values are `ushort`; any 0-65535 value is legal at runtime once injected into `EnumCollections.LogicTypes`. Reserved bands:

| Band | Owner | Notes |
|---|---|---|
| 0-349 | Vanilla game | Compiled-in `LogicType` enum members |
| 1000-1830 | Stationeers Logic Extended (ThunderDuck) | Reserved by Stationeers Logic Extended; avoid |
| 6571-6599 | **PowerTransmitterPlus** | PowerTransmitterPlus reserved band |

Future SixFive7 mods adding LogicTypes should reserve their own bands clear of vanilla, Stationeers Logic Extended, and PowerTransmitterPlus.

## Three LogicType arrays extension mechanism
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Stationeers stores LogicType lists in three separate places: `Logicable.LogicTypes`/`LogicTypeNames` (NextLogicType cycling, tablet display), `EnumCollections.LogicTypes` (ConfigCartridge tablet UI), `ScreenDropdownBase.LogicTypes` (IC housing on-screen dropdowns). All extended by `LogicableInitializePatch` postfix, including Values/ValuesAsInts/Names/PaddedNames/`<Length>k__BackingField`. `LogicTypeNamesRedirects` is a binary-search index rebuilt by the same patch (best-effort). Must also patch `Enum.GetName` and `EnumCollection<LogicType, ushort>.GetName`/`GetNameFromValue` postfixes via `EnumNamePatches`.

| Array | Used by | PowerTransmitterPlus extension mechanism |
|---|---|---|
| `Logicable.LogicTypes` / `Logicable.LogicTypeNames` | `NextLogicType` cycling, tablet display | `LogicableInitializePatch` postfix |
| `Assets.Scripts.EnumCollections.LogicTypes` | `ConfigCartridge` (configuration tablet UI) | Same patch; extends Values, ValuesAsInts, Names, PaddedNames, `<Length>k__BackingField` |
| `Assets.Scripts.UI.Motherboard.ScreenDropdownBase.LogicTypes` | IC housing on-screen dropdowns | Same patch (best-effort) |

`LogicTypeNamesRedirects` is a binary-search index rebuilt by the same patch (best-effort; tolerates absence).

Additionally the mod must patch `Enum.GetName(Type, object)` and `EnumCollection<LogicType, ushort>.GetName` / `GetNameFromValue` postfixes so reflection-based name lookups find our custom values, done by `EnumNamePatches`.

### LogicableInitializePatch three-extension-points comment
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

From `LogicableInitializePatch.cs`:

```
// Some game versions maintain a binary-search redirect array
// (LogicTypeNamesRedirects). Best-effort rebuild if present;
// otherwise the tablet will fall back to linear scans.
TryRebuildRedirects(newNames);

// ConfigCartridge (and other tablet UI paths) iterate
// EnumCollections.LogicTypes instead of Logicable.LogicTypes.
// That collection wraps Enum.GetValues, so our custom values
// are invisible to the tablet dropdown unless we also extend
// its Values / ValuesAsInts / Names / PaddedNames / Length.
ExtendEnumCollection(additions);

// The in-game screen preview (code rendered on the
// computer/laptop when NOT in the editor) validates tokens
// against ScreenDropdownBase.LogicTypes / LogicTypeNames.
// Without this, custom names draw red as "invalid".
ExtendScreenDropdownBase(additions);
```

### EnumNamePatches class header
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

From `EnumNamePatches.cs`:

```
// The configuration tablet and various UI paths look up the display name
// for a given LogicType value via Enum.GetName(...) and the game's own
// EnumCollection<LogicType, ushort>. Both return null for our 6571+ values
// because the underlying enum has no metadata for them. These postfixes
// substitute our names from the registry when the lookup would otherwise
// come up empty.
//
// Pattern lifted from Stationeers Logic Extended (ThunderDuck).
```

## Why custom LogicTypes don't appear without patches
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Early investigation established that vanilla Stationpedia pages for stock devices (like the Microwave Power Transmitter) have a frozen `LogicInsert` list built once at game startup. Even though `Logicable.LogicTypes` can be extended at runtime, the page's `LogicInsert` is populated inside `AddLogicTypeInfo` which iterates `EnumCollections.LogicTypes.Values`, a collection constructed from `Enum.GetValues(typeof(LogicType))` at `EnumCollection<LogicType,ushort>` construction time. Our runtime-cast `ushort → LogicType` values are NOT in the compiled enum, so `Enum.GetValues` doesn't return them.

This is why we (1) extend `EnumCollections.LogicTypes` via `LogicableInitializePatch`, (2) patch `Enum.GetName` and `EnumCollection<LogicType, ushort>.GetName` / `GetNameFromValue` so reflection-based name lookups find our values, (3) postfix `CanLogicRead`/`CanLogicWrite` on `PowerTransmitter`/`PowerReceiver` to return true for our types, and (4) inject into `ProgrammableChip.AllConstants` for IC10 name resolution.

Given all four of those, `AddLogicTypeInfo` naturally discovers and emits native rows for our custom LogicTypes. This is why the LogicInsert fallback (Decision 11) is NOT needed under normal operation.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; F0054 is the primary source per MigrationMap §5.1. Additional sources: F0040, F0219a, F0219b, F0244, F0245, F0247 (primary for "why custom LogicTypes don't appear without patches"; F0219v is a duplicate extraction that merges here), F0305, F0316.
- 2026-04-25: added "Atmospheric gas-ratio LogicType members" section. Verbatim list of all 130+ `Ratio*` enum members (chamber + per-network variants) decompiled from `Assets/Scripts/Objects/Motherboards/LogicType.cs` at v0.2.6228.27061. Cross-references combustion-relevant gases against `Mole.Enthalpy` and `EnthalpyMultiplier` from `../GameClasses/CombustionDeepMiner.md`. Verified via `ilspycmd` against `E:/Steam/steamapps/common/Stationeers/rocketstation_Data/Managed/Assembly-CSharp.dll`. Confirms `RatioMethane`, `RatioHydrazine`, `RatioLiquidAlcohol`, `RatioOzone`, `RatioLiquidOzone`, `RatioLiquidHydrazine` all exist and are IC10-readable; no `RatioVolatiles` member in the current enum (Hydrogen exposed as `RatioHydrogen = 252`).

## Open questions

None at creation.
