---
title: LogicType
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
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

## Open questions

None at creation.
