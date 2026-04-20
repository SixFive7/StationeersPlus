---
title: ThirdPartyModIdentities
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/StationpediaPlus/PLAN.md:74-80
  - Plans/RepairPrototype/plan.md:213-228
  - Mods/PowerTransmitterPlus/RESEARCH.md:748-760
related:
  - ../Patterns/BestEffortIntegration.md
  - ./LogicType.md
tags: [launchpad]
---

# ThirdPartyModIdentities

Reference table for third-party mod identity constants (Plugin GUID, Harmony ID, Workshop ID, Version) that our mods consult when using `[HarmonyAfter("...")]`, `Chainloader.PluginInfos.ContainsKey(...)`, or simple "is that other mod loaded" checks. Also a survey of the existing Stationeers damage-adjacent mod ecosystem.

## Stationpedia Ascended identity constants
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

SPA Plugin GUID `com.florpydorp.stationpediaascended`, Harmony ID `com.stationpediaascended.mod`, Secondary Harmony ID (script engine) `com.stationpediaascended.mod.scriptengine`, Workshop ID `3634225688`, Version `0.8.6` per `[BepInPlugin]` (About.xml shows stale 0.8.5). Use `Chainloader.PluginInfos.ContainsKey("com.florpydorp.stationpediaascended")` and `[HarmonyAfter("com.stationpediaascended.mod")]`.

## Stationeers Logic Extended reference pattern
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Author: ThunderDuck. Establishes the pattern for mod-authored custom LogicTypes. PowerTransmitterPlus adopts that pattern in full:
- Registry of `LogicTypeInfo` entries, hardcoded inline.
- Reflection injection into `ProgrammableChip.AllConstants`.
- Postfix on `Logicable.Initialize` to extend tablet UI arrays.
- Postfix on `Enum.GetName` and `EnumCollection<LogicType, ushort>.GetName / GetNameFromValue`.
- Per-device `CanLogicRead` postfix + `GetLogicValue` prefix.
- Postfix on `Stationpedia.PopulateLogicVariables`.

Stationeers Logic Extended has NO public extensibility API. Every mod that wants custom LogicTypes reimplements the registration pattern from scratch.

`Animator.StringToHash(name)` is the value stored in `Constant.Hash`, used for the `#hash` MIPS directive; pure name lookups don't require it.

## Existing Stationeers damage mod ecosystem
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

No auto-repair mod exists as of 2026-04; this is a gap. Damage-adjacent mods: XRepairsInOne (weapon/solar bug fixes), Configurable Storms (original + Kastuk fork), Re-Volt (cable damage overhaul), Perishable Items (food decay -> player damage), Incident: Godmode (removed from Workshop). Stationeers has no Steam achievements or save-integrity checks; no ironman mode. Save editing and mods are consequence-free.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0219ae, F0059 (identity-half of the finding; the pattern-half routes to `Research/Patterns/BestEffortIntegration.md`), and F0229c.

## Open questions

None at creation.
