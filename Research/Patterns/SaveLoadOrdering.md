---
title: Save-load ordering: defer restore to OnFinishedLoad
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/EquipmentPlus/RESEARCH.md:250-251 (F0122, primary)
  - Plans/EquipmentPlus/EquipmentPlus/ActiveSlotPersistence.cs:11-28 (F0333)
  - Plans/EquipmentPlus/EquipmentPlus/AdvancedTabletPatches.cs:103-108 (F0373)
  - Plans/EquipmentPlus/EquipmentPlus/ActiveSlotPersistence.cs:95-101 (F0374)
related:
  - ../GameSystems/SaveDataRegistration.md
  - ./HarmonyInheritedMethods.md
tags: [save-load, harmony]
---

# Save-load ordering: defer restore to OnFinishedLoad

When a parent Thing's saved state references child Things that have not yet been placed into its slots, `DeserializeSave` cannot restore that state immediately. The child-placement pass fires `OnChildEnterInventory` per child, which can clobber values set earlier. The pattern: stash the saved value in a dictionary during `DeserializeSave`, then apply it from `OnFinishedLoad`, which runs after every child has been placed.

## Problem
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

F0122 (Plans/EquipmentPlus/RESEARCH.md:250-251, primary):

> Vanilla restores children (chips/cartridges) one at a time AFTER the parent's `DeserializeSave`. Each child's `MoveToSlot` fires `OnChildEnterInventory`, which for lenses unconditionally sets `Sensor = child`. The last chip to enter wins, overwriting any value set during deserialization. Active-slot restoration must wait for `OnFinishedLoad`, which fires after all children are placed.

F0333 (code comment, `ActiveSlotPersistence.cs:11-28`) extends to `AdvancedTablet`:

```text
    /// <summary>
    /// Vanilla's load sequence places every child of a Thing (sensor chips
    /// inside lenses, cartridges inside a tablet) one at a time AFTER the
    /// parent's DeserializeSave returns. Each MoveToSlot fires the parent's
    /// OnChildEnterInventory, which for SensorLenses unconditionally sets
    /// <c>Sensor = child</c>. As a result, the last chip to re-enter wins
    /// and any value we set from saved data is immediately overwritten.
    ///
    /// AdvancedTablet has an analogous problem: <c>Mode</c> isn't persisted
    /// at all, so it resets to 0 (first cartridge) on every load.
    ///
    /// This class runs the saved active-slot restore from <c>OnFinishedLoad</c>
    /// -- a Thing-lifecycle hook that fires after child slots are populated.
    /// Save-time capture is done by <c>SensorLensesSerializeSavePatch</c> and
    /// <c>AdvancedTabletSerializeSavePatch</c>; at deserialize time the
    /// saved value is stashed in a dictionary here; <c>OnFinishedLoad</c>
    /// reads the dictionary and applies.
    /// </summary>
```

## Solution / recipe
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Three-step pattern:

1. **Save-time capture.** `Postfix SerializeSave` to write the reference-id (or index, or whatever identifies the active child) into the saved blob.
2. **Deserialize stash.** `Postfix DeserializeSave` to read the saved value and store it in a dictionary keyed by the parent's `ReferenceId`. Do NOT attempt to apply it here; the children are not in their slots yet.
3. **OnFinishedLoad apply.** Hook `Thing.OnFinishedLoad` (by whichever Harmony mechanism suits; inherited methods need `TargetMethod()` per `./HarmonyInheritedMethods.md`). Look up the dictionary entry, find the matching child by reference-id, and set the active-slot field.

F0373 (Plans/EquipmentPlus/EquipmentPlus/AdvancedTabletPatches.cs:103-108) is the specific AdvancedTablet case:

> Mode cannot be restored yet -- CartridgeSlots is rebuilt by our RefreshSlots later, and vanilla hasn't placed cartridges into the slots yet either. Stash the saved reference id and apply it in OnFinishedLoad.

F0374 (Plans/EquipmentPlus/EquipmentPlus/ActiveSlotPersistence.cs:95-101) documents the apply step:

> Find the CartridgeSlots index whose occupant matches the saved cartridge reference id, and set Mode to that index. `Thing.set_Mode` propagates via `Interactable.set_State`; we also call the private `GetCartridge()` to force the display to reflect the new active cartridge immediately.

Skeleton:

```csharp
// Stash during deserialize:
[HarmonyPostfix]
static void DeserializePostfix(Thing __instance, /* saved data */ data)
{
    _savedActiveIds[__instance.ReferenceId] = data.ActiveChildRefId;
}

// Apply during OnFinishedLoad:
[HarmonyPostfix]
static void OnFinishedLoadPostfix(Thing __instance)
{
    if (!_savedActiveIds.TryGetValue(__instance.ReferenceId, out var savedId)) return;
    _savedActiveIds.Remove(__instance.ReferenceId);

    // Find child by reference id among populated slots, then set active-slot field.
}
```

## Cited verifications
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- F0122: primary rule (OnChildEnterInventory clobbers; wait for OnFinishedLoad).
- F0333: generalized to both SensorLenses and AdvancedTablet.
- F0373: AdvancedTablet-specific case where `Mode` is not persisted and also depends on rebuilt dynamic slots.
- F0374: apply-side implementation including `Thing.set_Mode` via `Interactable.set_State` and the `GetCartridge()` refresh.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; F0122 primary, three additional sources corroborating across two device classes.

## Open questions

None at creation.
