---
title: SlotInsertionBlock
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-21
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Thing.CanEnter
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.DynamicThing.MoveToSlot
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Inventory.Slot.AllowMove
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Inventory.Slot.TakesSpecificItem
related:
  - ../GameClasses/Thing.md
tags: [slots, harmony]
---

# SlotInsertionBlock

How to block a specific `(Thing, Slot)` combination from being inserted, via Harmony patches. Used by `SprayPaintPlus` v1.4.0 to prevent `SprayCan` from being loaded into the `SprayGun` once the glow-paint feature replaces the gun's ammo model.

## Insertion call chain

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Vanilla insertion flows through, in order:

1. `DynamicThing.MoveToSlot(Slot destinationSlot, Thing originThing, bool forced = false)` at decompile line 2443. The primary gate; returns `false` without inserting when `!CanEnter(destinationSlot)`.
2. `Thing.CanEnter(Slot destinationSlot)` at decompile line 3931 (virtual). Returns a `CanEnterResult` struct with pass / fail plus an optional message. Vanilla rejects on entity-class mismatch, circular hierarchy, unpickable item, self-entry, and prefab-hash mismatch.
3. `Slot.TakesSpecificItem(int prefabHash)` at decompile line 981. Returns `false` when the slot's `SpecificTypePrefabHash` is set and does not match the incoming item's hash. Called from `CanEnter`.
4. `Slot.AllowMove(DynamicThing thing, Slot destinationSlot)` at decompile line 659 (static). UI-level affordance gate; does NOT block insertion, only controls whether the "can insert here" feedback is rendered.

## Authoritative block (server-side)

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Postfix `Thing.CanEnter` and overwrite the returned `CanEnterResult` via `ref`:

```
[HarmonyPatch(typeof(Thing), nameof(Thing.CanEnter))]
public class BlockFooIntoBar
{
    public static void Postfix(Thing __instance, Slot destinationSlot, ref CanEnterResult __result)
    {
        if (__instance is Foo && destinationSlot?.Parent is Bar)
            __result = CanEnterResult.Fail("explanation");
    }
}
```

This fires on every insertion attempt on the authoritative side. Server-side `MoveToSlotMessage.Process` calls `MoveToSlot` which calls `CanEnter`; the server rejects and the move is not broadcast. A cheating client cannot force the insertion because the server does not accept the resulting `MoveToSlotMessage`.

`CanEnterResult.Fail(string reason)` is the factory for the reject path; the returned struct carries a human-readable reason that vanilla UI surfaces surface when hovering.

## UI affordance (client-side)

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Patch `Slot.AllowMove` as a prefix that returns `false` and short-circuits:

```
[HarmonyPatch(typeof(Slot), nameof(Slot.AllowMove))]
public class BlockFooIntoBarUI
{
    public static bool Prefix(DynamicThing thing, Slot destinationSlot, ref bool __result)
    {
        if (thing is Foo && destinationSlot?.Parent is Bar)
        {
            __result = false;
            return false;
        }
        return true;
    }
}
```

Without this, the client UI can briefly show the insert as succeeding before the server rejects and the item snaps back. With both the `CanEnter` postfix and the `AllowMove` prefix in place, the UI stays consistent with the rule.

## Legacy-state handling

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

`Thing.CanEnter` is NOT re-checked during save load. Items already parented in slots at save time load as-is regardless of the current predicate. When a mod introduces a new block against a previously-valid `(Thing, Slot)` combination (like SprayPaintPlus v1.4.0's can-in-gun block), saves from before the block load normally, and the previously-loaded contents become orphaned: they still function (the gun still fires), but they cannot be re-inserted after the player removes them.

To eject orphaned contents cleanly on load, postfix a post-load hook (`Thing.OnFinishedLoad` or equivalent) on the container type and move the child out via `OnServer.MoveToWorld`. This is optional polish; many mods ship without it and document the edge case instead.

## Verification history

- 2026-04-21: page created. Decompile findings sourced from Assembly-CSharp.dll (Thing.CanEnter at line 3931, DynamicThing.MoveToSlot at line 2443, Slot.AllowMove at line 659, Slot.TakesSpecificItem at line 981) surfaced by a sub-agent pass in support of SprayPaintPlus v1.4.0 (block SprayCan from SprayGun).

## Open questions

- `CanEnterResult` struct shape: the `Fail(string)` factory is documented here; the full field / method list (other factories, severity levels, etc.) is not. If a mod wants to read the reason or downgrade the severity of an existing fail, the shape needs pinning.
- `Thing.OnFinishedLoad` method signature (and whether it is virtual / protected / public) is not documented. SprayPaintPlus v1.4.0 defers the legacy-eject pass until this is pinned.
