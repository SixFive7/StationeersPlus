---
title: SaveDataRegistration
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/EquipmentPlus/RESEARCH.md:253-254
  - Plans/EquipmentPlus/EquipmentPlus/Plugin.cs:74-79
related:
  - ../Patterns/SaveLoadOrdering.md
  - ../Protocols/LaunchPadBoosterNetworking.md
tags: [save-load, launchpad]
---

# SaveDataRegistration

How a mod registers its custom save-data types with the game's `XmlSaveLoad.ExtraTypes` list, and why a dual-registration (LaunchPadBooster `AddSaveDataType` plus direct `ExtraTypes` injection) is required to tolerate the load-order race.

## XmlSaveLoad.ExtraTypes timing race
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

StationeersLaunchPad's `Mod.AddSaveDataType` injects via a Prefix on `XmlSaveLoad.AddExtraTypes`. If that method already ran before our plugin loaded, the prefix never fires for our types. Direct injection into the `ExtraTypes` field plus nulling `Serializers._worldData` covers this race.

## Dual save-data-type registration
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

From `Plugin.cs`:

```
// Register our save-data subclasses via LaunchPadBooster so
// future XmlSaveLoad.AddExtraTypes callers pick them up via
// LaunchPadBooster's prefix, AND inject directly into XmlSaveLoad.ExtraTypes
// + invalidate the cached XmlSerializer. See comment on
// RegisterSaveDataTypeLate for why both are needed.
```

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0123 and F0346.

## Open questions

None at creation.
