---
title: Custom save-data type must inherit vanilla so isinst still matches
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/EquipmentPlus/RESEARCH.md:285-286 (F0126)
related:
  - ../GameSystems/SaveDataRegistration.md
tags: [save-load]
---

# Custom save-data type must inherit vanilla so isinst still matches

When extending a Thing's save-data payload by registering a custom type, inherit the vanilla save-data class. Vanilla's `DeserializeSave` often does an `isinst` check against its own type; a sibling class (no inheritance) fails the check and vanilla skips its own fields entirely. Inherit, and vanilla's check passes while the custom subclass adds fields the mod cares about.

## Problem
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

F0126 (Plans/EquipmentPlus/RESEARCH.md:285-286):

> Vanilla's `AdvancedTablet.DeserializeSave` does an `isinst` check for its own `AdvancedTabletSaveData`. By inheriting from it, our subclass passes that check and any fields vanilla adds in future updates are preserved automatically. The class is named differently to avoid XML type-name collision when both are registered in `ExtraTypes`.

Two failure modes if the custom type does not inherit:

1. Vanilla's `isinst AdvancedTabletSaveData` returns false for a sibling class; vanilla's own field restoration is skipped. The mod's fields load; the vanilla fields silently reset to defaults.
2. If the game version adds new fields to `AdvancedTabletSaveData`, a sibling class will not carry them forward across saves. Inheritance auto-inherits new fields.

## Solution / recipe
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

```csharp
[Serializable]
[XmlType("MyModTabletSaveData")]  // different XML name avoids ExtraTypes collision
public class MyModTabletSaveData : AdvancedTabletSaveData
{
    public long SavedActiveCartridgeRefId;
    // additional mod fields
}
```

Registering both `AdvancedTabletSaveData` (vanilla, already present) and `MyModTabletSaveData` (mod) in `XmlSaveLoad.ExtraTypes` requires the XML type names to differ. The C# class name and XML `[XmlType]` attribute can disagree; the XML name is what `ExtraTypes` indexes on. Keeping the class name distinct (`MyModTabletSaveData`) and the `[XmlType]` distinct prevents the collision.

The deserialize path:

1. Game reads the saved `<AdvancedTabletSaveData>` XML element.
2. Polymorphic deserialization finds the registered subclass that matches.
3. `DeserializeSave` receives an instance whose runtime type is `MyModTabletSaveData`.
4. Vanilla's `isinst AdvancedTabletSaveData` returns true (subclass satisfies the check); vanilla restores its fields.
5. The mod's postfix reads the additional fields off the subclass.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; single source (F0126).

## Open questions

None at creation.
