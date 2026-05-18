---
title: Mod LogicType Registration Analysis
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-18
sources:
  - .work/decomp/0.2.6228.27061/Dan_s_Colonists_3639645231__DansColonists.decompiled.cs:3714-3863
  - .work/decomp/0.2.6228.27061/FPGA_3457324551__FPGAMod.decompiled.cs:317
  - .work/decomp/0.2.6228.27061/FPGA_3457324551__FPGAMod.decompiled.cs:1890-1937
  - .work/decomp/0.2.6228.27061/KeypadMod_3478434324__KeypadMod.decompiled.cs:34-447
  - .work/decomp/0.2.6228.27061/ImprovedLabeller_3656548039__ImprovedLabeller.decompiled.cs:355-595
  - .work/decomp/0.2.6228.27061/StationeersLogicExtended.decompiled.cs:3924-4153
  - .work/decomp/0.2.6228.27061/IC10_Inspector_Debugger_3508602436__IC10Inspector.decompiled.cs:1268
  - .work/decomp/0.2.6228.27061/IC10_Inspector_Debugger_3508602436__IC10Inspector.decompiled.cs:2208-2212
  - E:/Steam/steamapps/workshop/content/544550/3625190467/Data/SLE_LogicTypes.json
  - E:/Steam/steamapps/workshop/content/544550/3625190467/Documentation/CustomLogicTypes.md
related:
  - ../GameSystems/LogicType.md
tags: [logic, harmony]
---

# Mod LogicType Registration Analysis

Analysis of decompiled Stationeers Workshop mods to determine whether each registers new LogicType integers or only consumes vanilla LogicType values (0-349). Initial pass covered four deep-dive targets; the 2026-05-18 extension added findings from a 124-mod scan (top 90 most-subscribed Workshop mods plus 34 additional developer-subscribed mods).

The per-name catalogue of every integer registered by every mod listed here lives in the monorepo's own `Patterns/Logic/README.md` (not under `Research/` because it is also the SixFive7 mods' own assignment table). Treat that file as the cross-reference for any specific integer value cited here.

## Methodology
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

Each mod was analyzed for:

1. **Registration mechanisms** used: `ProgrammableChip.AllConstants` reflection-append, `ProgrammableChip.InternalEnums` patching (`ScriptEnum<LogicType>` / `BasicEnum<LogicType>`), `Logicable.LogicTypes` dict, `EnumCollections.LogicTypes` modification, `ScreenDropdownBase.LogicTypes` patches.
2. **Custom LogicType constants** defined as `private const LogicType FOO = (LogicType)N` where N >= 350. A constant alone is necessary but not sufficient: the integer is only a true registration if it lands in at least one of the five registries above. A numeric cast that never reaches a registry is a "soft" reservation (the value is still serialized into save data via the per-Thing logic store, but it is invisible to IC10 chips and other mods).
3. **CanLogicRead / CanLogicWrite / GetLogicValue / SetLogicValue** overrides that handle custom values.
4. **OnLoaded / Awake / ModBehaviour entry points** where registration would occur.

For the broader 124-mod scan, a binary string scan of every mod DLL was used to filter the candidate set before decompilation. The scan covered both ASCII (for type / method names in the `#Strings` metadata heap) and UTF-16 LE (for string literals in the `#US` heap), because `GetField("AllConstants", ...)` calls store the field name as a UTF-16 string literal in the user-strings heap, not in the ASCII type-names heap. An ASCII-only filter misses every reflection-based registrar.

DLLs that hit any of the markers `AllConstants`, `InternalEnums`, `EnumCollections` plus `LogicType`, or `ScriptEnum<LogicType>` were decompiled with `ilspycmd 10.0.0.8330` and re-grepped for actual mutation sites.

## Registration mechanisms (canonical signatures)
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

The five mechanisms by which a mod can extend the vanilla `LogicType` enum at runtime. A registrar uses at least one; mods using none, but consuming `(LogicType)<int>` casts, are consumers regardless of how many devices they introduce.

1. **`ProgrammableChip.AllConstants` reflection-append**. Read the existing `ProgrammableChip.Constant[]` via reflection, allocate a larger array, copy + append `new ProgrammableChip.Constant(name, description, (double)value, true)` entries, then `field.SetValue(null, merged)`. This registers the integer with the IC10 compiler (so `s d0 MyName ...` resolves to the integer).

2. **`ProgrammableChip.InternalEnums`** entries (`ScriptEnum<LogicType>` and `BasicEnum<LogicType>`) are private snapshots of `Enum.GetValues` / `Enum.GetNames` taken at construction. Their backing fields `_types` and `_names` are extended via reflection. This drives in-game-screen syntax highlighting for custom names; without it, custom names render in the default invalid-token red.

3. **`Logicable.LogicTypes` dict** patched per-Thing. Wires per-instance Logicable behaviour.

4. **`EnumCollections.LogicTypes`** modification via `ModUtils.PatchEnumCollection` or equivalent. Resizes `Values`, `ValuesAsInts`, `Names`, `PaddedNames` arrays. Required for tablet-cycling UI and motherboard dropdowns.

5. **`ScreenDropdownBase.LogicTypes`** patches. Required for cartridge / screen dropdown lists.

The marker `EnumCollections.<something>` alone is insufficient to identify a LogicType registrar: the same helper utility commonly patches sibling enums in the same family (`SlotClasses`, `Class`, etc.). Confirm the generic type argument is `LogicType` (or the target collection field is `LogicTypes`).

## Findings

### 1. Dan's Colonists (rank 31)
<!-- verified: 0.2.6228.27061 @ 2026-05-14 -->

**Verdict:** CONSUMER

**Mechanism:** None -- uses only vanilla game logic

**Evidence:**
- Entry point: `Main : BaseUnityPlugin` (BepInEx, line 3715)
- No OnLoaded override; only Awake() (empty, line 3725)
- No Harmony patches, no enum registration code
- LogicType uses: only reading device values via vanilla calls; no custom LogicType constants defined
- File: `Dan_s_Colonists_3639645231__DansColonists.decompiled.cs`, lines 3714-3863

**Notes:** Game logic is driven by colonist AI tasks and NPC state machines. No need for custom LogicTypes.

---

### 2. FPGA Mod (rank 35)
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

**Verdict:** CONSUMER for `LogicType`. REGISTRAR for the adjacent `Class` enum (registers `Class 105 = "FPGAChip"` in `EnumCollections.SlotClasses`).

**Mechanism (Class registration):** `ModUtils.PatchEnumCollection<Class, ushort>(EnumCollections.SlotClasses, (Class)105, "FPGAChip")`. The generic argument is `<Class, ushort>` and the target collection is `EnumCollections.SlotClasses`, not `EnumCollections.LogicTypes`.

**Mechanism (LogicType):** None. No call to `ModUtils.PatchEnumCollection` with `LogicType` as a generic argument or with `EnumCollections.LogicTypes` as a target. No references to `ProgrammableChip.AllConstants`, `ProgrammableChip.InternalEnums`, `Logicable.LogicTypes`, or `ScreenDropdownBase.LogicTypes`. No `Register(new LogicTypeInfo(...))` calls. No `private const LogicType` definitions.

**Evidence:**
- Entry point: `FPGAMod : MonoBehaviour` with `OnLoaded(List<GameObject> prefabs)` (line 1890)
- Harmony patches initialized via `PatchAll()` (line 1893)
- Only `PatchEnumCollection` call site in the DLL (verbatim, line 317):
  ```
  ModUtils.PatchEnumCollection<Class, ushort>(EnumCollections.SlotClasses, (Class)105, "FPGAChip");
  ```
- Generic helper definition (line 1913): `public static void PatchEnumCollection<T1, T2>(EnumCollection<T1, T2> collection, T1 val, string name)` -- calls `Array.Resize()` on `collection.Values`, `collection.ValuesAsInts`, `collection.Names`, and `collection.PaddedNames`, then uses reflection `SetValue()` on backing fields (lines 1934-1936). Helper is generic and reusable; the only invocation in this DLL is the Class one above.

**Notes:** Earlier analysis (2026-05-14) misread the generic PatchEnumCollection helper as evidence of a LogicType registration. A fresh independent validator on 2026-05-18 enumerated every call site of the helper and every LogicType registration marker in the FPGA DLL; the verdict was binding (B is correct). See "Verification history" below. FPGA mods that read `LogicType.Setting` (12) on a placed FPGA chip do so against vanilla; the Class 105 registration is what makes the FPGA item itself placeable, not a LogicType extension.

---

### 3. Keypad Mod (rank 60)
<!-- verified: 0.2.6228.27061 @ 2026-05-14 -->

**Verdict:** CONSUMER

**Mechanism:** None -- overrides only vanilla LogicTypes

**LogicTypes used:**
- `(LogicType)3` = Output (vanilla)
- `(LogicType)12` = Setting (vanilla)

**Evidence:**
- Entry point: `KeypadMod : ModBehaviour` (line 34)
- `OnLoaded(ContentHandler contentHandler)` uses only Harmony `PatchAll()` (line 40)
- Custom Keypad class overrides logic methods (lines 401-447):
  - `CanLogicRead(LogicType type)`: special-case for `(int)type == 12` (Setting), else delegates (line 406)
  - `CanLogicWrite(LogicType type)`: special-case for `(int)type == 12`, else delegates (line 418)
  - `GetLogicValue(LogicType type)`: special-case for `(int)type == 12`, else delegates (line 430)
  - `SetLogicValue(LogicType type, double value)`: sets value if `(int)type == 12` (line 443)
- Pulse output triggered via `SetLogicValue((LogicType)3, ...)` (lines 111, 130)
- No custom LogicType constants; all values are hardcoded vanilla enums

**Notes:** Keypad is a simple input device that reads its Setting slot and pulses Output. No new enum values required.

---

### 4. Improved Labeller (rank 61)
<!-- verified: 0.2.6228.27061 @ 2026-05-14 -->

**Verdict:** CONSUMER

**Mechanism:** None -- utility mod for vanilla labellers

**LogicTypes used:** `(LogicType)0` = None (vanilla, all instances in file)

**Evidence:**
- Entry point: `ImprovedLabeller : BaseUnityPlugin` (BepInEx, line 580)
- No OnLoaded override; only `Awake()` with Harmony `PatchAll()` (line 595)
- Harmony patches modify device selection UI for labellers, not LogicType behavior
- Device-setting code always resets LogicType to None:
  - Line 355: `instance.LogicType = (LogicType)0;` (LogicReader)
  - Line 362: `((LogicWriterBase)instance).LogicType = (LogicType)0;` (LogicWriter)
  - Line 375: `((LogicWriterBase)instance).LogicType = (LogicType)0;` (LogicWriterSwitch)
  - Line 382: `((LogicWriterBase)instance).LogicType = (LogicType)0;` (LogicBatchWriter)
  - Line 395: `instance.LogicType = (LogicType)0;` (LogicBatchReader)
- No custom LogicType overrides; all patches are UI/interaction flow

**Notes:** UI enhancement for device selection. No game-logic LogicType changes.

---

### 5. Stationeers Logic Extended (Workshop 3625190467) -- the canonical large registrar
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

**Verdict:** REGISTRAR (the largest known third-party `LogicType` registrar in the Stationeers ecosystem).

**Mechanism:** `ProgrammableChip.AllConstants` reflection-append, driven by an internal `LogicTypeRegistry` class. The mod's `OnLoaded` calls `LogicTypeRegistry.Initialize()` (line 89), which executes ~230 `Register(new LogicTypeInfo(name, displayName, description, value, dataType, access, category))` calls. The registered values are subsequently appended to `ProgrammableChip.AllConstants` via the standard reflection pattern (the same one PowerTransmitterPlus uses). The mod also patches `Logicable.LogicTypes` (line 267) and reads back its own registry in the various game-side Harmony postfixes (`GetCustomLogicTypeName`, etc., lines 176-251) to keep tablet UI and IC10 compiler names consistent.

**Integers added:** 230 entries in the range **1000-1830**, sparse, grouped by 35 device categories. Per-name catalogue verbatim in `Patterns/Logic/README.md` under "Known third-party reservations". Range summary by device category:

| Range | Device | Count |
|---|---|---|
| 1000-1004 | ContactSelection (SatelliteDish) | 5 |
| 1010-1020 | ContactProperties (SatelliteDish) | 11 |
| 1030-1032 | DishState (SatelliteDish) | 3 |
| 1100-1103 | Centrifuge | 4 |
| 1110-1117 | RealtimeData (DaylightSensor) | 8 |
| 1120-1123 | WindTurbine | 4 |
| 1130 | SolidFuelGenerator | 1 |
| 1140-1141 | WeatherStation | 2 |
| 1150-1151 | DeepMiner | 2 |
| 1160-1169 | HydroponicsDevice | 10 |
| 1180-1189 | Harvester | 10 |
| 1200-1205 | GasFuelGenerator | 6 |
| 1210-1215 | Battery | 6 |
| 1220-1228 | SolarPanel | 9 |
| 1230-1233 | H2Combustor | 4 |
| 1240-1243 | Electrolyzer | 4 |
| 1250-1292 | PipeAnalyzer | 31 |
| 1300-1311 | Furnace | 12 |
| 1320-1323 | AdvancedFurnace | 4 |
| 1330-1333 | ArcFurnace | 4 |
| 1400-1416 | Filtration | 13 |
| 1500-1503 | AirConditioner | 4 |
| 1520-1523 | WallCooler | 4 |
| 1530-1534 | WallHeater | 5 |
| 1540-1542 | ActiveVent | 3 |
| 1600-1604 | StateChangeDevice | 5 |
| 1610-1615 | Fabricator | 6 |
| 1620-1627 | AdvancedComposter | 8 |
| 1700-1707 | RobotMining | 8 |
| 1720-1726 | Quarry | 7 |
| 1740-1745 | HorizontalQuarry | 6 |
| 1760-1764 | Recycler | 5 |
| 1780-1785 | StirlingEngine | 6 |
| 1800-1803 | RocketMiner | 4 |
| 1820-1824 | LandingPad | 5 |
| 1830 | APC | 1 |

The whole band 1000-1830 is reserved by the mod author by device category. Many gaps (e.g. 1005-1009, 1021-1029, 1033-1099) are intentional headroom for future entries in the same category and should be treated as reserved, not free.

**Evidence:**
- Entry point: `Plugin.Awake` at decompile line 86 prints `"[SLE] Stationeers Logic Extended v1.0.0 loading..."`, then calls `LogicTypeRegistry.Initialize()` (line 89).
- Registration loop (decompile lines 3924-4153): each entry is a verbatim `Register(new LogicTypeInfo("Name", "DisplayName", "Description", VALUE, "datatype", "access", "category"))` call. The full 230-call list was extracted into `Patterns/Logic/README.md` by a regex over the decompile.
- AllConstants append at the standard reflection pattern (lines 145-160): allocates a `List<Constant>`, iterates `LogicTypeRegistry.All`, constructs `new Constant(name, description, (double)value, true)` per entry, then writes back via reflection.
- Mod-shipped source-of-truth: the Workshop download contains `Data/SLE_LogicTypes.json` (24 sample entries) and `Documentation/CustomLogicTypes.md` (63 entries in markdown tables). Neither shipped file is complete; the DLL is authoritative.
- Workshop popularity: 378 subscribers, 2046 unique visitors, 48 favorites at 2026-05-18. Not in the all-time top 90 Stationeers Workshop mods by subscription.

**Notes:** Earlier monorepo conventions (root `CLAUDE.md`, prior version of `Patterns/Logic/README.md`) listed the SLE reservation as "1000-1830 ThunderDuck" without per-name detail. Both the upper bound (1830) and the author attribution are correct.

---

### 6. PowerTransmitterPlus (Workshop 3707677512) -- this monorepo's own registrar
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

**Verdict:** REGISTRAR (in-repo reference implementation of the canonical pattern).

**Mechanism:** `ProgrammableChip.AllConstants` reflection-append plus `ProgrammableChip.InternalEnums` extension for syntax highlighting. Source lives in this repository at `Mods/PowerTransmitterPlus/PowerTransmitterPlus/Ic10ConstantsPatcher.cs` (not decompiled; read directly).

**Integers added:** 6 entries in **6571-6576** (MicrowaveSourceDraw, MicrowaveDestinationDraw, MicrowaveTransmissionLoss, MicrowaveEfficiency, MicrowaveAutoAimTarget, MicrowaveLinkedPartner). Catalogued in `Patterns/Logic/LogicTypeNumbers.cs` and tabled in `Patterns/Logic/README.md`. The sibling mod PowerGridPlus adds LogicPassthroughMode at 6577 via the same shared catalogue.

**Evidence:**
- `Patterns/Logic/LogicTypeNumbers.cs` (verbatim, this repo): `public const ushort MicrowaveSourceDraw = 6571;` through `MicrowaveLinkedPartner = 6576` plus `LogicPassthroughMode = 6577`.
- `Mods/PowerTransmitterPlus/PowerTransmitterPlus/Ic10ConstantsPatcher.cs`: reflection-append pattern, `existing.CopyTo(merged, 0); ...; field.SetValue(null, merged);`. Also extends `_types` / `_names` of every `ScriptEnum<LogicType>` and `BasicEnum<LogicType>` entry in `ProgrammableChip.InternalEnums`.
- Binary scan of the built `PowerTransmitterPlus.dll` (UTF-16-aware) shows `AllConstants`, `InternalEnums`, `EnumCollections`, `LogicType`, and `ScriptEnum` string literals in the user-strings heap; an ASCII-only filter sees only the type-name literals (`LogicType` from member names) and misses the registration markers.

**Notes:** Same pattern Stationeers Logic Extended uses, applied to a single mod with a small reservation band. Workshop popularity: 169 subscribers, 669 unique visitors, 27 favorites at 2026-05-18.

---

### 7. IC10 Inspector/Debugger (Workshop 3508602436) -- the numeric-cast-only "soft" pattern
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

**Verdict:** CONSUMER for the strict definition (does not register), but **uses non-vanilla LogicType integers** via direct numeric casts that bypass every registry. Documented here because the values still land in save-game state, which makes them a soft reservation.

**Mechanism:** Private `const LogicType` declarations cast from `int` literals, accessed through the standard `Logicable` / `ISetable` interfaces. No call to `ProgrammableChip.AllConstants`, `ProgrammableChip.InternalEnums`, `EnumCollections.LogicTypes`, or any other registry. The integers are invisible to IC10 chip compilation, to tablet cycling, to the in-game enum dropdown, and to mods that iterate the registries.

**Integers used:**

| Value | Name (in mod source) | Use |
|---|---|---|
| 500 | LOGIC_TYPE / SETDEVICE_LOGIC_TYPE | Primary debugger logic slot, also reused for "set device by reference id" |
| 501 | SETSELDEVICE_LOGIC_TYPE | Selected-device command channel |
| 502 | COMMAND_LOGIC_TYPE | Generic command channel |

**Evidence:**
- Decompile line 1268: `private const LogicType LOGIC_TYPE = (LogicType)500;`
- Decompile lines 2208-2212: `private const LogicType SETDEVICE_LOGIC_TYPE = (LogicType)500; private const LogicType SETSELDEVICE_LOGIC_TYPE = (LogicType)501; private const LogicType COMMAND_LOGIC_TYPE = (LogicType)502;`
- Used in `LogicType = (LogicType)500` Setable assignments (lines 1391, 2405, 2437, 2469, 2803) and switch statements that branch on the three constants.

**Notes:** A future mod that registers 500/501/502 properly into the registries would not collide at the registry level (IC10 Inspector/Debugger never claimed those slots there), but would collide at the save-game level on installations where IC10 Inspector/Debugger has been active. Treat 500-502 as reserved when picking new SixFive7 values. Workshop popularity: 1529 subscribers, 4258 unique visitors, 127 favorites at 2026-05-18; in the all-time top 90 most-subscribed Stationeers Workshop mods.

---

## Summary Table
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

| Mod | Workshop ID | Rank | LogicType verdict | Mechanism | Integer range | Notes |
|---|---|---|---|---|---|---|
| Dan's Colonists | 3639645231 | 31 | CONSUMER | None | -- | NPC colonist AI; vanilla logic only |
| FPGA | 3457324551 | 35 | CONSUMER (Class 105 REGISTRAR) | `EnumCollections.SlotClasses` patching for Class enum, no LogicType code | -- (Class 105 = "FPGAChip") | Earlier mis-classified as LogicType registrar; corrected 2026-05-18 by fresh validator |
| Keypad | 3478434324 | 60 | CONSUMER | None | -- | Input device; uses (LogicType)3 and (LogicType)12 |
| Improved Labeller | 3656548039 | 61 | CONSUMER | None | -- | UI utility; all resets to (LogicType)0 |
| Stationeers Logic Extended | 3625190467 | (not top 90) | REGISTRAR | `AllConstants` reflection-append + `Logicable.LogicTypes` | 1000-1830 (230 names, 35 device categories) | Largest third-party LogicType registrar in the ecosystem |
| PowerTransmitterPlus | 3707677512 | (not top 90) | REGISTRAR | `AllConstants` reflection-append + `InternalEnums` extension | 6571-6576 (6 names) | This monorepo's own mod; reference implementation |
| IC10 Inspector/Debugger | 3508602436 | 28 | CONSUMER (soft 500-502) | Private const numeric casts, no registry | 500-502 (3 slots, NOT registered) | Save-game-level reservation; invisible to all registries |

---

## Decompilation Sources
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

All decompiled from game version **0.2.6228.27061**, present under `.work/decomp/0.2.6228.27061/` when regenerated:

- `Dan_s_Colonists_3639645231__DansColonists.decompiled.cs`
- `FPGA_3457324551__FPGAMod.decompiled.cs`
- `KeypadMod_3478434324__KeypadMod.decompiled.cs`
- `ImprovedLabeller_3656548039__ImprovedLabeller.decompiled.cs`
- `StationeersLogicExtended.decompiled.cs`
- `IC10_Inspector_Debugger_3508602436__IC10Inspector.decompiled.cs`

For PowerTransmitterPlus, the in-repo C# source under `Mods/PowerTransmitterPlus/` is the primary reference; the built DLL was binary-scanned to confirm registration markers are present in both ASCII and UTF-16 LE heaps. SLE additionally ships `Data/SLE_LogicTypes.json` (incomplete, 24 entries) and `Documentation/CustomLogicTypes.md` (incomplete, 63 entries); both are partial views of the 230 entries the DLL registers.

## Verification history

- 2026-05-14: Initial analysis of four mods (Dan's Colonists, FPGA, Keypad, Improved Labeller) against game version 0.2.6228.27061. Decompiles read from `.work/decomp/0.2.6228.27061/`. Per-finding section stamps applied.
- 2026-05-18: Frontmatter migrated to Research/CLAUDE.md schema (replaced `status`+`category` with `type: Patterns`, added `verified_at` and `sources[]`, normalised tags to canonical vocabulary). Page relocated from `Research/` root to `Research/Patterns/` so it nests under the Patterns category in nav. Body developer-specific paths stripped per repo-root style rule. No factual content changed.
- 2026-05-18: Conflict on "does FPGA register a new LogicType integer". Previous claim: FPGA was REGISTRAR via EnumCollections patching, integers unknown. New finding from a 124-mod scan: FPGA's only `ModUtils.PatchEnumCollection` call site passes `<Class, ushort>(EnumCollections.SlotClasses, (Class)105, "FPGAChip")`, not LogicType; no other LogicType registration markers present. Fresh validator verdict (game version 0.2.6228.27061): **B is correct, FPGA is a CONSUMER for LogicType and a REGISTRAR for the adjacent Class enum**. Result: FPGA section rewritten with the corrected verdict and the verbatim `Class 105 = "FPGAChip"` registration evidence. Open question about FPGA's "exact LogicType integer values" closed (no such values exist).
- 2026-05-18: Page extended additively (no contradictions of existing verified content) with three new findings from the same 124-mod scan: Stationeers Logic Extended (230 registrations, 1000-1830), PowerTransmitterPlus (6 registrations, 6571-6576, in-repo reference implementation), and IC10 Inspector/Debugger (numeric-cast-only soft reservation at 500-502). A new "Registration mechanisms" section was added near the top, drawn from `Research/WORKFLOW.md` Rule 3 Example 2 and verified against the SLE and PowerTransmitterPlus implementations.

## Open questions

- No outstanding LogicType-registration unknowns in the seven mods listed above. The 124-mod scan (top 90 most-subscribed Workshop mods plus 34 additional developer-subscribed mods) found 2 confirmed `LogicType` registrars (SLE, PowerTransmitterPlus), 1 numeric-cast-only mod (IC10 Inspector/Debugger), and 1 adjacent-enum registrar (FPGA's Class 105). Mods outside that 124-mod pool are not covered by the scan; periodic re-scan (or directed search for `AllConstants` / `InternalEnums` / `EnumCollections.LogicTypes` / `ScriptEnum<LogicType>` markers in newly-released mods) would extend the coverage.
