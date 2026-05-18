---
title: Mod LogicType Registration Analysis
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-14
sources:
  - .work/decomp/0.2.6228.27061/Dan_s_Colonists_3639645231__DansColonists.decompiled.cs:3714-3863
  - .work/decomp/0.2.6228.27061/FPGA_3457324551__FPGAMod.decompiled.cs:1890-1937
  - .work/decomp/0.2.6228.27061/KeypadMod_3478434324__KeypadMod.decompiled.cs:34-447
  - .work/decomp/0.2.6228.27061/ImprovedLabeller_3656548039__ImprovedLabeller.decompiled.cs:355-595
related:
  - ../GameSystems/LogicType.md
tags: [logic, harmony]
---

# Mod LogicType Registration Analysis

Analysis of four decompiled C# mods to determine whether each registers new LogicType integers or only consumes vanilla LogicType values (0–349).

## Methodology

Each mod was analyzed for:
1. **Registration mechanisms** used: ProgrammableChip.AllConstants reflection-append, InternalEnums patching, Logicable.LogicTypes dict, EnumCollections modification, ScreenDropdownBase patches
2. **Custom LogicType constants** defined as `private const LogicType FOO = (LogicType)N` where N >= 350
3. **CanLogicRead/CanLogicWrite/GetLogicValue/SetLogicValue** overrides that handle custom values
4. **OnLoaded/Awake/ModBehaviour entry points** where registration would occur

## Findings

### 1. Dan's Colonists (rank 31)

**Verdict:** CONSUMER

**Mechanism:** None -- uses only vanilla game logic

**Evidence:**
- Entry point: `Main : BaseUnityPlugin` (BepInEx, line 3715)
- No OnLoaded override; only Awake() (empty, line 3725)
- No Harmony patches, no enum registration code
- LogicType uses: only reading device values via vanilla calls; no custom LogicType constants defined
- File: `Dan_s_Colonists_3639645231__DansColonists.decompiled.cs`, lines 3714-3863

**Notes:** Game logic is driven by colonist AI tasks and NPC state machines. No need for custom LogicTypes.

<!-- verified: 0.2.6228.27061 @ 2026-05-14 -->

---

### 2. FPGA Mod (rank 35)

**Verdict:** REGISTRAR (via EnumCollections patching)

**Mechanism:** `ModUtils.PatchEnumCollection<T1, T2>()` reflection-based enum resizing

**Integers added:** Unknown -- likely custom FPGA control LogicTypes, exact values not determinable from decompiled code (computed at runtime or stored in config)

**Evidence:**
- Entry point: `FPGAMod : MonoBehaviour` with `OnLoaded(List<GameObject> prefabs)` (line 1890)
- Harmony patches initialized via `PatchAll()` (line 1893)
- Enum patching utility at lines 1913-1937:
  ```
  PatchEnumCollection<T1, T2>(EnumCollection<T1, T2> collection, T1 val, string name)
  ```
  Calls `Array.Resize()` on `collection.Values`, `collection.ValuesAsInts`, `collection.Names`, and `collection.PaddedNames`, then uses reflection `SetValue()` on backing fields (lines 1934-1936)
- No explicit call to `PatchEnumCollection` with `LogicType` visible in decompiled output (likely in undecompiled config/initialization path or called dynamically)
- No private `const LogicType` definitions found

**Notes:** Pattern matches EnumCollections modification mechanism. Exact registered values unknown without access to source or runtime inspection. The utility is generic and could patch any EnumCollection, not just LogicType.

<!-- verified: 0.2.6228.27061 @ 2026-05-14 -->

---

### 3. Keypad Mod (rank 60)

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

<!-- verified: 0.2.6228.27061 @ 2026-05-14 -->

---

### 4. Improved Labeller (rank 61)

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

<!-- verified: 0.2.6228.27061 @ 2026-05-14 -->

---

## Summary Table

| Mod | Rank | Verdict | Mechanism | Custom Values | Notes |
|-----|------|---------|-----------|---------------|-------|
| Dan's Colonists | 31 | CONSUMER | None | -- | NPC colonist AI; vanilla logic only |
| FPGA | 35 | REGISTRAR | EnumCollections patching | unknown | Likely FPGA control types; values runtime-determined |
| Keypad | 60 | CONSUMER | None | -- | Input device; uses (LogicType)3 & (LogicType)12 |
| Improved Labeller | 61 | CONSUMER | None | -- | UI utility; all resets to (LogicType)0 |

---

## Decompilation Sources

All decompiled from game version **0.2.6228.27061**, present under `.work/decomp/0.2.6228.27061/` when regenerated:

- `Dan_s_Colonists_3639645231__DansColonists.decompiled.cs`
- `FPGA_3457324551__FPGAMod.decompiled.cs`
- `KeypadMod_3478434324__KeypadMod.decompiled.cs`
- `ImprovedLabeller_3656548039__ImprovedLabeller.decompiled.cs`

## Verification history

- 2026-05-14: Initial analysis of four mods (Dan's Colonists, FPGA, Keypad, Improved Labeller) against game version 0.2.6228.27061. Decompiles read from `.work/decomp/0.2.6228.27061/`. Per-finding section stamps applied.
- 2026-05-18: Frontmatter migrated to Research/CLAUDE.md schema (replaced `status`+`category` with `type: Patterns`, added `verified_at` and `sources[]`, normalised tags to canonical vocabulary). Page relocated from `Research/` root to `Research/Patterns/` so it nests under the Patterns category in nav. Body developer-specific paths stripped per repo-root style rule. No factual content changed.

## Open questions

- FPGA mod's exact custom LogicType integer values are not determinable from the decompile. The `PatchEnumCollection` utility is generic and the call sites that target `LogicType` are not present in the decompiled output (likely runtime-computed from config or invoked dynamically). Runtime introspection of the patched `EnumCollection<LogicType>` via InspectorPlus (request: types=[LogicTypeCollection or wherever the patched collection lives], fields=[Values, Names, ValuesAsInts]) after FPGA mod loads would resolve the registered values.
