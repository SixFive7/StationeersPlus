---
title: Localization
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/StationpediaPlus/PLAN.md:929-939
  - Plans/StationpediaPlus/PLAN.md:1009-1030
  - Plans/EquipmentPlus/EquipmentPlus/DynamicSlots.cs:79-91
  - Plans/RepairPrototype/plan.md:583-610 (F0229p)
related:
  - ../GameClasses/StationpediaPage.md
  - ./StationpediaPageRendering.md
  - ../Patterns/PrefabCloning.md
tags: [ui, stationpedia, prefab, harmony]
---

# Localization

How the game resolves display strings for prefabs, LogicTypes, and slots: the `Localization.LanguageFolder.LoadAll` path, the `english.xml` / `english_help.xml` resources, the legacy `ThingTemplate` placeholder that is now dead code, the vanilla transmitter/receiver body text (same string for both), and the `StringKey` / `StringHash` pair that drives `Slot.DisplayName`.

## ThingTemplate placeholder is legacy dead code
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`english_help.xml` defines a `ThingTemplate` entry with `{0}..{3}` placeholders, but current `PopulateThingPages` (game.cs:231964) does NOT apply `string.Format` with this template. It sets `page.Description` directly from language XML's `<RecordThing>/<Description>`. The placeholders are legacy. Mod authors should not attempt to template against `ThingTemplate`; it is a no-op.

## Vanilla transmitter/receiver body text (identical for both prefabs)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`english.xml` contains identical body text for both the Microwave Power Transmitter and the Microwave Power Receiver (vanilla oversight; both prefabs share the transmitter description). The last sentence ("attrition over longer ranges, so the unit requires more power over greater distances") is the vanilla description of behavior that PowerTransmitterPlus REPLACES with the explicit distance-cost formula.

## Slot StringKey / StringHash drive DisplayName
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

From `DynamicSlots.cs`:

```
StringKey/StringHash drive Slot.DisplayName via the Localization table.
Without them the slot label is empty. Cloning from the template gives our
dynamic slots the same label vanilla uses on the first slot of this type
(e.g. "Sensor Processing Unit" for lenses, "Cartridge" for the tablet).
```

## Cloned-prefab localization
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

When a mod clones a vanilla prefab (see the `PrefabCloning.md` pattern for the full recipe), the clone's display name is registered by a Harmony Prefix on `Localization.LanguageFolder.LoadAll` rather than by shipping a custom language XML file. The prefix looks up the original's record in the already-loaded `LanguagePages[0].Things` and appends a derived `RecordThing` for the clone.

F0229p (Plans/RepairPrototype/plan.md:583-610):

> Localization registration via `[HarmonyPatch(typeof(Localization.LanguageFolder), nameof(Localization.LanguageFolder.LoadAll))]` Prefix: finds original name in `__instance.LanguagePages[0].Things`, then adds `new Localization.RecordThing { Key = mirrorDef.mirrorName, Value = originalName + " (Mirrored)", ThingDescription = mirrorDef.mirrorDescription }`. No XML needed. Crafting recipes FREE: `GameObject.Instantiate()` copies entire `BuildStates` chain. Clone inherits exact same recipe as original. No XML, no recipe code needed.

This recipe is part of the broader prefab-cloning pattern; see [../Patterns/PrefabCloning.md](../Patterns/PrefabCloning.md).

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0219i, F0219l, and F0375.
- 2026-04-20: added Cloned-prefab localization subsection (F0229p) per Phase 6 Pass A split-coverage fix.

## Open questions

None at creation.
