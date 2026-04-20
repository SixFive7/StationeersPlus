---
title: SlotTypeIcon lookup injection
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/EquipmentPlus/RESEARCH.md:114-117 (F0111, primary)
  - Plans/EquipmentPlus/EquipmentPlus/SlotTypeIconPatch.cs:9-27 (F0344)
  - Plans/EquipmentPlus/EquipmentPlus/DynamicSlots.cs:283-292 (F0376)
related:
  - ../GameClasses/InventoryManager.md
tags: [ui, slots]
---

# SlotTypeIcon lookup injection

Vanilla's `Slot.PopulateSlotTypeSprites` loads sprite assets from `Resources/UI/SlotTypes` whose filenames follow a `sloticon-<lowercase-enum-name>` convention. Missing assets (e.g. `sloticon-sensorprocessingunit`) produce empty slot hint icons. A Harmony Postfix on `PopulateSlotTypeSprites` arrives too late because the method runs at `InventoryManager.ManagerAwake`, before any BepInEx plugin has its patches installed. The fix populates `Slot._slotTypeLookup` directly during plugin load.

## Problem
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

F0111 (Plans/EquipmentPlus/RESEARCH.md:114-117, primary):

> Called once from `Plugin.OnAllModsLoaded`. Vanilla's `Slot.PopulateSlotTypeSprites` loads sprites from `Resources/UI/SlotTypes` but has no `sloticon-sensorprocessingunit` asset, so SPU slots render without a hint icon. This patch injects into `Slot._slotTypeLookup` directly: first tries any sprite with "sensor" in its name, then falls back to the Cartridge icon.
>
> Cannot use a Harmony Postfix on `PopulateSlotTypeSprites` because that method runs at `InventoryManager.ManagerAwake`, which fires before any BepInEx plugin patches are installed.

F0344 (code comment, `SlotTypeIconPatch.cs:9-27`) expands:

```text
    /// <summary>
    /// Vanilla's <c>Slot.PopulateSlotTypeSprites</c> (called once from
    /// <c>InventoryManager.ManagerAwake</c>) scans
    /// <c>Resources/UI/SlotTypes</c> for sprites named
    /// <c>sloticon-&lt;lowercase-enum-name&gt;</c> and registers them in
    /// <c>Slot._slotTypeLookup</c>. There's a <c>sloticon-cartridge</c>
    /// asset but no <c>sloticon-sensorprocessingunit</c>, so the runtime
    /// lookup returns null for SPU and empty sensor-chip slots render
    /// with no hint icon. Vanilla has this same bug on its own lens
    /// slots -- you just don't notice because vanilla ships 2 chips per
    /// lens and they're usually occupied.
    ///
    /// A Postfix on PopulateSlotTypeSprites doesn't help: that method
    /// runs at InventoryManager.ManagerAwake, which fires BEFORE our
    /// plugin patches are installed, so the Postfix never attaches to
    /// the one call that matters. Instead we populate the dictionary
    /// directly at plugin load. Vanilla's later Adds don't collide
    /// because PopulateSlotTypeSprites only adds keys it doesn't have.
    /// </summary>
```

## Solution / recipe
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

In `OnAllModsLoaded` (or equivalent post-game-startup hook), write to `Slot._slotTypeLookup` directly. Pick a fallback sprite by filename pattern when a targeted asset isn't present.

Shape:

```csharp
private static void InjectSlotTypeIcons()
{
    var lookup = /* reflect Slot._slotTypeLookup */;
    if (!lookup.ContainsKey(SlotClass.SensorProcessingUnit))
    {
        var sprite =
            FindSpriteContainingName("sensor")
            ?? FindSpriteByKey(SlotClass.Cartridge)
            ?? null;
        if (sprite != null) lookup[SlotClass.SensorProcessingUnit] = sprite;
    }
}
```

Vanilla's `PopulateSlotTypeSprites` uses `TryAdd` semantics (only adds keys it doesn't have), so the injection survives subsequent vanilla calls without collision.

### Unlocked-slot fallback

F0376 (Plans/EquipmentPlus/EquipmentPlus/DynamicSlots.cs:283-292) adds a runtime complement: when a dynamic slot's type is unlocked from `Blocked` to its real type, `Slot.Initialize` has already baked the Blocked sprite into the field. Re-derive the icon from the `_slotTypeLookup`, and if that lookup misses, fall back to the editor-baked template sprite captured during the initial `AddBlockedSlots` step.

> Slot.Initialize runs once at instance Awake and baked the Blocked-type sprite into SlotTypeIcon; re-derive it here so the runtime-unlocked slot shows the correct hint icon. If the runtime lookup misses this type, fall back to the editor-baked template sprite captured in AddBlockedSlots.

## Cited verifications
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- F0111: primary source stating the injection pattern and the Postfix-too-late observation.
- F0344: patch code comment reiterating the rule with explicit call-order detail (`InventoryManager.ManagerAwake`) and vanilla's `TryAdd` semantics.
- F0376: runtime unlock-fallback, which applies when the slot type changes after initial Init.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; F0111 primary, F0344 and F0376 confirming and extending.

## Open questions

None at creation.
