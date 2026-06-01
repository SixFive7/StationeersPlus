---
title: LogicType
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-01
sources:
  - Mods/PowerTransmitterPlus/RESEARCH.md:664-670
  - Mods/PowerTransmitterPlus/RESEARCH.md:398-425
  - Plans/StationpediaPlus/PLAN.md:225-236
  - Plans/StationpediaPlus/PLAN.md:240-255
  - Plans/StationpediaPlus/PLAN.md:229-236
  - Plans/StationpediaPlus/PLAN.md:240-254
  - Plans/StationpediaPlus/PLAN.md:3483-3506
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.EnumCollection`2
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.EnumCollections
related:
  - ../Patterns/LogicTypeRegistration.md
  - ./IC10SyntaxHighlighting.md
  - ./ScreenDropdownBase.md
  - ./StationpediaPageRendering.md
  - ../GameClasses/ProgrammableChip.md
  - ../GameClasses/WirelessPower.md
  - ../GameClasses/PowerTransmitter.md
tags: [logic, ic10, ui]
---

# LogicType

`LogicType` is a `ushort` enum with 350 vanilla values (0-349) that names every logic readable and writable in the game. This page is the enum-value reference: vanilla values, the gas-ratio member catalogue, and the public surface of `EnumCollection<TEnum, TInt>` (the generic wrapper that backs `EnumCollections.LogicTypes` and its siblings).

For the runtime registration mechanism (how a mod adds custom values: the five reflection-extension sites, the three name-lookup fallback patches, the module split, and the ScreenDropdownBase Awake-survival behavior), see [../Patterns/LogicTypeRegistration.md](../Patterns/LogicTypeRegistration.md). For the canonical SixFive7 value-assignment catalogue and known third-party reservations to avoid, see [`Patterns/Logic/README.md`](../../Patterns/Logic/README.md) at the repo root. For the "who registers what" survey of Workshop mods, see [../Patterns/ModLogicTypeRegistration.md](../Patterns/ModLogicTypeRegistration.md).

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

The game stores LogicType identity and metadata across five independent collections that a mod must extend to make a custom value visible end-to-end (`Logicable.LogicTypes` for `NextLogicType` cycling, `EnumCollections.LogicTypes` for the ConfigCartridge UI, `ScreenDropdownBase.LogicTypes` for motherboard dropdowns, `ProgrammableChip.AllConstants` for the IC10 compiler, and `ProgrammableChip.InternalEnums` for screen syntax highlighting). The full extension mechanism, the module split that fires each site at the right time, and the three name-lookup fallback patches live on [../Patterns/LogicTypeRegistration.md](../Patterns/LogicTypeRegistration.md).

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
<!-- verified: 0.2.6228.27061 @ 2026-06-01 -->

`LogicType` values are `ushort`; any 0-65535 value is legal at runtime once injected into `EnumCollections.LogicTypes`. The full assignment catalogue (SixFive7 mods plus known third-party reservations from a 124-mod Workshop scan: vanilla 0-349, Stationeers Logic Extended 1000-1830, IC10 Inspector/Debugger 500-502 soft, the SixFive7 6571+ allocations) lives in [`Patterns/Logic/README.md`](../../Patterns/Logic/README.md). Cross-check that file before reserving a new band; do not duplicate the table here.

## EnumCollection<TEnum, TInt> public API
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

`Assets.Scripts.EnumCollection<T1, T2>` is the generic wrapper that backs every entry in the static `Assets.Scripts.EnumCollections` class (`LogicTypes`, `LogicSlotTypes`, `DeviceModes`, etc.). Verbatim public surface from the decompile of `Assets.Scripts.EnumCollection`2`:

```csharp
public class EnumCollection<T1, T2> : IEnumCollection
    where T1 : Enum, IConvertible, new()
    where T2 : IConvertible, IEquatable<T2>
{
    public T1[]  Values;
    public T2[]  ValuesAsInts;
    public readonly string[] Names;
    public readonly string[] PaddedNames;
    public readonly string   LongestName;
    public T1 this[int index] => Values[index];
    public int Length { get; }

    public static implicit operator string[](EnumCollection<T1, T2> c);
    public static implicit operator T2[]   (EnumCollection<T1, T2> c);

    public EnumCollection(bool toProper = true);

    public virtual string GetNameFromIndex(int index, bool padded = false);
    public T1[]   GetValues();
    public string GetName(T1 value, bool padded = false);
    public string GetEnumTypeName();
    public string GetNameFromValue(int value, bool padded = false);
    public int    GetIndexFromValue(T1 value);     // throws IndexOutOfRangeException on miss
    public int    GetIntFromIndex(int i);
    public string PrintAll(string begin = "", string end = "");
    public T1     Get(string name);                 // case-insensitive name -> value lookup
}
```

Lookup directions and miss behavior:

| Direction         | Method                              | Miss behavior                          |
|---|---|---|
| name -> enum      | `Get(string name)`                  | Returns `default(T1)` (NOT exception)  |
| enum -> name      | `GetName(T1 value, bool padded)`    | Returns `string.Empty`                 |
| int -> name       | `GetNameFromValue(int, bool)`       | Returns `string.Empty`                 |
| index -> name     | `GetNameFromIndex(int, bool)`       | Throws (array bounds)                  |
| enum -> index     | `GetIndexFromValue(T1)`             | Throws `IndexOutOfRangeException`      |
| index -> int      | `GetIntFromIndex(int)`              | Throws (array bounds)                  |
| index -> enum     | indexer `this[int]`                 | Throws (array bounds)                  |

`Get(string)` body, verbatim:

```csharp
public T1 Get(string name)
{
    for (int i = 0; i < Length; i++)
    {
        if (Names[i].Equals(name, StringComparison.InvariantCultureIgnoreCase))
        {
            return Values[i];
        }
    }
    return default(T1);
}
```

Caller pitfall: `Get` returning `default(T1)` is indistinguishable from a real hit on whichever enum member maps to the zero value. For unambiguous not-found detection, scan `Names` directly with `Array.FindIndex(coll.Names, n => string.Equals(n, name, StringComparison.InvariantCultureIgnoreCase))` and check for `-1`, then index `coll.Values[i]`.

The constructor populates `Values`, `ValuesAsInts`, `Names`, and `PaddedNames` from `Enum.GetValues(typeof(T1))` / `Enum.GetNames(typeof(T1))` exactly once at static class load, so custom enum values added later by `(T1)cast` are invisible to `Get` / `GetName` / `GetNameFromValue` until those arrays are resized via reflection. `Values` and `ValuesAsInts` are not `readonly`; `Names` and `PaddedNames` are `readonly` references whose array contents must be replaced via reflection (the `valuesField.SetValue(collection, newValues)` pattern in [`LogicableInitializePatch.ExtendEnumCollection`](../../Mods/PowerTransmitterPlus/PowerTransmitterPlus/LogicableInitializePatch.cs)).

## Why custom LogicTypes don't appear without patches
<!-- verified: 0.2.6228.27061 @ 2026-06-01 -->

Early investigation established that vanilla Stationpedia pages for stock devices (like the Microwave Power Transmitter) have a frozen `LogicInsert` list built once at game startup. Even though `Logicable.LogicTypes` can be extended at runtime, the page's `LogicInsert` is populated inside `AddLogicTypeInfo` which iterates `EnumCollections.LogicTypes.Values`, a collection constructed from `Enum.GetValues(typeof(LogicType))` at `EnumCollection<LogicType,ushort>` construction time. Our runtime-cast `ushort -> LogicType` values are NOT in the compiled enum, so `Enum.GetValues` does not return them. The same construction-time snapshot underlies the other four extension sites; the registration recipe extends all five to make `AddLogicTypeInfo` naturally discover and emit native rows for custom LogicTypes (this is why the LogicInsert fallback once labelled "Decision 11" is NOT needed under normal operation).

The full enumeration of the five extension sites, the three name-lookup fallback patches, and the module split that ties them together is on [../Patterns/LogicTypeRegistration.md](../Patterns/LogicTypeRegistration.md). Per-device `CanLogicRead` / `CanLogicWrite` / `GetLogicValue` / `SetLogicValue` overrides (which decide whether a particular device responds to the registered LogicType) are an orthogonal concern; see [../Patterns/CustomLogicValueInjection.md](../Patterns/CustomLogicValueInjection.md).

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-06-01 -->

- 2026-04-20: page created from the Research migration; F0054 is the primary source per MigrationMap §5.1. Additional sources: F0040, F0219a, F0219b, F0244, F0245, F0247 (primary for "why custom LogicTypes don't appear without patches"; F0219v is a duplicate extraction that merges here), F0305, F0316.
- 2026-04-25: added "Atmospheric gas-ratio LogicType members" section. Verbatim list of all 130+ `Ratio*` enum members (chamber + per-network variants) decompiled from `Assets/Scripts/Objects/Motherboards/LogicType.cs` at v0.2.6228.27061. Cross-references combustion-relevant gases against `Mole.Enthalpy` and `EnthalpyMultiplier` from `../GameClasses/CombustionDeepMiner.md`. Verified via `ilspycmd` against the local Stationeers install. Confirms `RatioMethane`, `RatioHydrazine`, `RatioLiquidAlcohol`, `RatioOzone`, `RatioLiquidOzone`, `RatioLiquidHydrazine` all exist and are IC10-readable; no `RatioVolatiles` member in the current enum (Hydrogen exposed as `RatioHydrogen = 252`).
- 2026-04-27: added "EnumCollection<TEnum, TInt> public API" section. Verbatim public surface (fields, indexer, constructor, all methods) of `Assets.Scripts.EnumCollection`2` decompiled at v0.2.6228.27061 via `ilspycmd` against the local Stationeers install. Documents the `Get(string name)` case-insensitive name-to-value lookup (returns `default(T1)` on miss, NOT exception), the value-to-name `GetName` / `GetNameFromValue` (return `string.Empty` on miss), the indexer / `GetIndexFromValue` / `GetIntFromIndex` (throw on miss), the implicit operators to `string[]` and `T2[]`, and the constructor's one-shot `Enum.GetValues` / `Enum.GetNames` snapshot that explains why custom enum values added via `(T1)cast` are invisible until the arrays are resized via reflection. No conflicts with prior page content; section is purely additive.
- 2026-06-01: split the registration mechanism out of this page into a dedicated [../Patterns/LogicTypeRegistration.md](../Patterns/LogicTypeRegistration.md). Removed sections "Four separate LogicType registries", "Three LogicType arrays extension mechanism", "LogicableInitializePatch three-extension-points comment", and "EnumNamePatches class header" (verbatim content preserved on the new page, lossless). Replaced the "Reserved bands" table with a one-line pointer to [`Patterns/Logic/README.md`](../../Patterns/Logic/README.md) (the canonical assignment catalogue; the table on this page was stale: it showed `6571-6599 PowerTransmitterPlus` instead of the actual 6571-6576 usage and did not list PowerGridPlus's 6577 reservation). Trimmed the in-section "three separate arrays" listing in "LogicType enum value inventory" to a one-paragraph pointer at the new mechanism page. Corrected drift relative to the 2026-05-28 fresh-validator resolution on [./ScreenDropdownBase.md](./ScreenDropdownBase.md) by removing the prior page's references to ScreenDropdownBase as "IC housing on-screen dropdowns" (it actually backs `LogicMotherboard` condition / action dropdowns on Big Screens, Wall Screens, and Consoles) and the implicit claim that reflection-extension of `ScreenDropdownBase.LogicTypes` is fragile against `Awake` (the appended tail survives every Awake). Page now serves only as the enum-value reference. The "five extension sites" framing on the new page consolidates the prior "four registries" vs "three arrays" inconsistency on this page; site 4 (`ProgrammableChip.AllConstants`) was previously mentioned only in "Why custom LogicTypes don't appear without patches" and not counted in either registry tally. Implementation verified by direct reads of both in-repo registrar mods (`Mods/PowerTransmitterPlus/`, `Mods/PowerGridPlus/`).

## Open questions

None at creation.
