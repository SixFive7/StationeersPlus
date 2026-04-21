---
title: SprayGun
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-21
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Items.SprayGun
related:
  - ./SprayCan.md
  - ./ISprayer.md
  - ./OnServer.md
tags: [prefab, equipment]
---

# SprayGun

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Vanilla game class at `Assets.Scripts.Objects.Items.SprayGun`. A `Tool` that paints targets by delegating to a loaded `SprayCan`. Like `SprayCan`, it implements `ISprayer` and reaches `OnServer.SetCustomColor` through the shared `ISprayer.DoSpray` static helper.

## Declaration

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

```
namespace Assets.Scripts.Objects.Items
{
    public class SprayGun : Tool, ISprayer, IUsedAmount, IUsed
    {
    }
}
```

`Tool` is the base. The three additional interfaces (`ISprayer`, `IUsedAmount`, `IUsed`) are the same set `SprayCan` implements, so either class is interchangeable as the `sprayer` argument to `ISprayer.DoSpray`.

## OnUseItem

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

`OnUseItem(float quantity, Thing useOnThing)` at decompile line 334806-334812. Scales `quantity` by the gun's efficiency, then delegates to `SprayCan.OnUseItem` on the loaded can. Does not call `OnServer.SetCustomColor` directly; paint application runs through `ISprayer.DoSpray` (see `ISprayer.md`).

## Contrast with SprayCan

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

| Aspect | `SprayCan` | `SprayGun` |
|---|---|---|
| Base class | `Consumable` | `Tool` |
| Ammo model | Self-contained charges | Loaded `SprayCan` supplies charges |
| `OnUseItem` path | Direct paint via `ISprayer.DoSpray` | Scale quantity, delegate to loaded `SprayCan.OnUseItem` |
| `ISprayer` | Yes | Yes |

Both reach `OnServer.SetCustomColor(thing, colorSwatch.Index)` via `ISprayer.DoSpray` line 334711. By the time execution is inside `SetCustomColor`, the tool identity is no longer on the argument list; distinguishing the two requires hooking upstream at `DoSpray`.

## Operability gate (cursor validity)

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

`SprayGun.IsOperable` (virtual property, overridden from `Thing.IsOperable`):

```
public override bool IsOperable
{
    get
    {
        if (!IsEmpty) return OnOff;
        return false;
    }
}
```

Reads the loaded-can presence (`IsEmpty`) and the gun's on/off state. An empty gun reports non-operable, which paints the targeting cursor red and blocks use.

The targeting cursor's red / green color is driven downstream by `Thing.AttackWith(Attack attack, bool doAction)` at decompile line 302593. The relevant branch:

```
if (IsPaintable && sourceItem is ISprayer sprayer && !HasColorState)
{
    return ISprayer.DoSpray(this, sprayer, doAction);
}
```

Returning `null` from `AttackWith` triggers the red "invalid action" cursor at decompile line 269962 in the UI layer. Paths that make the gun return `null` include a failed `IsOperable` on the source item (`IsEmpty` branch on the gun) and any other failed predicate upstream.

Mods that want the gun to work ammo-less need to override `IsOperable` to ignore `IsEmpty` (e.g. return just `OnOff`); the rest of the `AttackWith` predicate passes naturally for paintable targets.

## OnOff state, toggle, label

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Right-click on a held gun flips its on/off state. Inheritance: `Thing.OnOff` is the virtual bool property at decompile line 299160 that all Things share; `SprayGun` does not override it. Reads the animator's `OnOffState` integer (or the transient `InteractOnOff.State` when no animator is present).

Right-click dispatch: `HumanHandsBehaviour.ToggleActiveHandTool` at decompile line 271917 calls `slot.Occupant.Interact(InteractableType.OnOff, state)`. The resulting `Thing.InteractWith(Interactable, Interaction, bool)` body at decompile line 302420 handles the `InteractableType.OnOff` case:

```
case InteractableType.OnOff:
    ...
    OnServer.Interact(interactable, (!OnOff) ? 1 : 0);
    return delayedActionInstance.Succeed();
```

`OnServer.Interact` is the server-authoritative path and broadcasts the new state to all clients via the `Interactable.State` field + animator parameter. The state persists across save/load because `Interactable.State` is part of the Thing's normal save data.

Contextual label ("On" / "Off" hint) comes from `Thing.GetContextualName(Interactable)` at decompile line 300637:

```
case InteractableType.OnOff:
    if (!OnOff) return ActionStrings.On;
    return ActionStrings.Off;
```

HUD display path: `Interactable.ContextualName` property at decompile line 286007 (`=> Parent.GetContextualName(this)`), polled from `InventoryManager.NormalModeThing()` around line 269971. Mods can postfix `Thing.GetContextualName` and filter by `__instance is SprayGun && interactable.Action == InteractableType.OnOff` to relabel per-class without affecting other on/off-togglable items.

`SprayPaintPlus` v1.4.0 uses this path to rebrand the gun's on/off as "Add Glow" / "Remove Glow": the gun's on/off animation becomes the feature's mode-toggle UX, and firing reads `__instance.OnOff` to decide add vs. remove glow.

## Verification history

- 2026-04-21: page created. Decompile findings sourced from Assembly-CSharp.dll line 334806-334812 for `OnUseItem`; declaration and interface list from the class header.
- 2026-04-21: added "Operability gate (cursor validity)" documenting `SprayGun.IsOperable` (returns `!IsEmpty && OnOff`) and its role in painting the cursor red on an empty gun via `Thing.AttackWith` (decompile line 302593) returning null; noted the red-cursor UI handler at line 269962. Added "OnOff state, toggle, label" documenting the inherited `Thing.OnOff` property (line 299160), the right-click dispatch via `HumanHandsBehaviour.ToggleActiveHandTool` (line 271917) and `Thing.InteractWith` (line 302420), the server-synced `Interactable.State` path, and the label path via `Thing.GetContextualName` (line 300637) and `Interactable.ContextualName` (line 286007). Additive only; no prior claim changed.

## Open questions

- (none)
