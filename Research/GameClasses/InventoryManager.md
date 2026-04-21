---
title: InventoryManager
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-22
sources:
  - Plans/EquipmentPlus/RESEARCH.md:211-219
  - Plans/EquipmentPlus/EquipmentPlus/ClickCyclePatch.cs:14-30
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Inventory.InventoryManager
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Item.OnUseSecondary
related:
  - ./AdvancedTablet.md
  - ./SensorLenses.md
  - ./CursorManager.md
  - ./Thing.md
tags: [equipment, slots]
---

# InventoryManager

Vanilla game class routing per-frame primary-use clicks against held items. The correct hook point for intercepting click-based item interactions, because neither `SensorLenses` nor `AdvancedTablet` set `AllowSelfUse = true`.

## HandlePrimaryUse sequence
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0116.

Called every frame from input processing. Sequence for a held item:
1. If `CursorManager.CursorThing` is targetable and the held item supports `AttackWith` on it, call `AttackWith` (item-on-thing interaction).
2. Otherwise call `UseItemOnSelf(item)`, which early-returns when `item.AllowSelfUse == false`.
3. For `AllowSelfUse == true` items, `UseItemOnSelf` dispatches to `item.OnUsePrimary()`.

`AdvancedTablet.AllowSelfUse == false`, so patching `Item.OnUsePrimary` never fires for a plain tablet. `InventoryManager.HandlePrimaryUse` is the correct hook for tablet-level click intercepts because it runs regardless of `AllowSelfUse`. This is why the pre-refactor `CartridgeCyclePatch` on `Item.OnUsePrimary` never worked in practice, and why the current `ClickCyclePatch` targets `InventoryManager.HandlePrimaryUse`.

`KeyManager.GetMouseDown("Primary")` is a thin forwarder to `Input.GetKeyDown(KeyMap.PrimaryAction)`, frame-stable, idempotent across multiple reads within a single Update tick. Multiple patches can read it in the same frame without interference.

### ClickCyclePatch class header (F0334)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source comment from `ClickCyclePatch.cs:14-30`:

```
/// <summary>
/// Ctrl+left-click while holding a SensorLenses or AdvancedTablet cycles
/// to the next loaded chip / cartridge.
///
/// We patch <c>InventoryManager.HandlePrimaryUse</c> because neither
/// vanilla prefab sets <c>AllowSelfUse = true</c>, so clicks never reach
/// <c>Item.OnUsePrimary</c> via <c>UseItemOnSelf</c>. HandlePrimaryUse is
/// the per-frame click handler and runs on the clicking peer regardless
/// of the item's AttackWithEvent. Detecting modifier + click there fires
/// identically in single-player, on the host, and on a remote client --
/// no server-side dispatch concerns, no modifier-state mirroring needed.
///
/// When the cycle fires we return <c>false</c> to suppress the rest of
/// HandlePrimaryUse; vanilla would otherwise try an AttackWith on the
/// cursor target with our item, which for the tablet/lenses does nothing
/// useful anyway but we don't want the side effect.
/// </summary>
```

## Secondary-use (right-click) routing

<!-- verified: 0.2.6228.27061 @ 2026-04-22 -->

`InventoryManager.NormalMode` handles the secondary-action (right-click) path inline, NOT via a dedicated `HandleSecondaryUse` method (unlike the primary path which factors out into `HandlePrimaryUse`). Decompile line ~2159 (`Assets.Scripts.Inventory.InventoryManager.NormalMode`):

```csharp
if ((bool)ActiveHand.Slot.Occupant && KeyManager.GetMouseDown("Secondary"))
{
    DynamicThing occupant2 = ActiveHand.Slot.Occupant;
    if (!(occupant2 is Constructor constructorItemPlacement))
    {
        if (!(occupant2 is MultiConstructor multiConstructorItemPlacement))
        {
            if (!(occupant2 is AuthoringTool))
            {
                if (occupant2 is Item item)
                {
                    Thing.DelayedActionInstance delayedActionInstance = item.OnUseSecondary();
                    if (delayedActionInstance != null && delayedActionInstance.Duration > 0f)
                    {
                        ActionCoroutine = StartCoroutine(WaitUntilDone(...));
                    }
                    else if (GameManager.RunSimulation)
                    {
                        OnServer.UseItemSecondary(Parent, ActiveHand.SlotId, LastCompletedRatio);
                    }
                    else
                    {
                        NetworkClient.UseItemSecondary(Parent, ActiveHand.SlotId, LastCompletedRatio);
                    }
                }
            }
            else { /* AuthoringTool placement path */ }
        }
        else { SetMultiConstructorItemPlacement(multiConstructorItemPlacement); }
    }
    else { /* Constructor placement path */ }
}
```

Notable differences from the primary path:

- No `CursorManager.CursorThing` branch: secondary-use does NOT route through `AttackWith` on the cursor target. `OnUseSecondary()` is invoked directly on the held item with no target argument, and no `AllowSelfUse` gate exists for secondary-use.
- `Item.OnUseSecondary(bool doAction = false, float actionCompletedRatio = 1f)` (decompile line ~399 of `Assets.Scripts.Objects.Item`) returns `null` by default. Any item that wants to react to right-click must override it; otherwise the whole `if (occupant2 is Item item) { ... }` block exits with no action dispatched.
- `SprayCan : Consumable, ISprayer, IUsedAmount, IUsed` does NOT override `OnUseSecondary`. Vanilla right-click on a held spray can is a no-op.
- `Constructor`, `MultiConstructor`, and `AuthoringTool` are intercepted BEFORE the `Item` branch and get placement-mode behavior instead of `OnUseSecondary`.
- `KeyManager.GetMouseDown("Secondary")` is the frame-stable secondary-click read, analogous to `GetMouseDown("Primary")` in `HandlePrimaryUse`.

Hook choices for a right-click intercept on a specific item type:

1. Patch `Item.OnUseSecondary` with a filter like `if (!(__instance is SprayCan can)) return true;`. The virtual returns `null`, so a postfix that sets `__result` to a non-null `DelayedActionInstance` also flows through the `duration > 0f ? coroutine : OnServer.UseItemSecondary / NetworkClient.UseItemSecondary` branch; a prefix that returns `false` after setting `__result = null` suppresses that dispatch entirely. This is the clean choice when the intercept is per-item-type and doesn't need to inspect the cursor target before the check order described above.
2. Patch `InventoryManager.NormalMode` (same target the existing `ColorCyclerPatch` uses) to inspect `KeyManager.GetMouseDown("Secondary")` before the vanilla block fires. This is the correct choice when the intercept needs `CursorManager.CursorThing` (secondary routing itself does not supply a target), needs to run regardless of item type, or needs to suppress the vanilla secondary-use dispatch for types that already override `OnUseSecondary`.

For the spray-can eyedropper, patching at the `InventoryManager.NormalMode` layer mirrors the existing `ColorCyclerPatch` and keeps all client-side spray-can input polling in one place. Reading `CursorManager.CursorThing` at that layer gives the looked-at Thing client-side with no server round-trip, consistent with the attack-resolve path (`OnServer.AttackWith` receives a resolved `Thing`, but the client already knows the target via `CursorThing` in `HandlePrimaryUse`; the secondary path does not auto-resolve a target, so a client-side read of `CursorThing` is both the only and the correct source).

### Parallel secondary-action dispatch via KeyManager

<!-- verified: 0.2.6228.27061 @ 2026-04-22 -->

Right-click has a SECOND dispatch edge independent of `NormalMode`. `KeyManager.cs` line 141: `KeyMap._SecondaryAction.Bind(InputPhase.Up, ToggleActiveHandTool, KeyInputState.Game)`. The mouse-UP edge triggers `HumanHandsBehaviour.ToggleActiveHandTool` (also bound at line 140 to `KeyMap._ToggleHandPower`).

```
public void ToggleActiveHandTool()
{
    if (!_human.IsUnresponsive && !_human.IsSleeping && !InputMouse.IsMouseControl && !InputWindowBase.IsInputWindow)
    {
        Slot slot = ActiveHand.Slot;
        if ((bool)slot.Occupant && slot.Occupant.CheckTogglePower() && slot.Occupant.ShouldToggleOn())
        {
            int state = ((!slot.Occupant.OnOff) ? 1 : 0);
            slot.Occupant.Interact(InteractableType.OnOff, state);
            PanelHands.Instance.HandlePowerOnSwitch(slot.Display.SlotDisplayButton.IsLeftHand);
        }
    }
}
```

`DynamicThing.CheckTogglePower()` (line 1085) defaults to `return CanTogglePower;`. `Tool.CheckTogglePower()` (line 50) overrides with `return base.CanTogglePower;`. `Consumable` has no override. `SprayCan : Consumable` therefore resolves to the `DynamicThing` default (returns `CanTogglePower`); on the can prefab `CanTogglePower` is false, so `ToggleActiveHandTool` early-returns at the `CheckTogglePower()` gate without calling `Interact`. The mouse-up edge is therefore a no-op for a bare can in vanilla. The gun sets the prefab flag true, which is why its right-click toggle fires (see `SprayGun.md` "OnOff state, toggle, label").

Timing: `NormalMode` fires on `GetMouseDown` (edge-triggered) at line 2159; the `KeyManager` binding fires on `InputPhase.Up` (edge-triggered on release). Both edges are reachable in a single right-click gesture; they do not conflict because they fire at different phases, and both are no-ops for a bare can in vanilla. A mod that intercepts `NormalMode`'s mouse-down edge for the can does not interact with the `ToggleActiveHandTool` mouse-up edge.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-22 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0116, F0334. No conflicts.
- 2026-04-22: added "Secondary-use (right-click) routing" section. Additive only; no existing content changed. Source: decompile of `Assets.Scripts.Inventory.InventoryManager.NormalMode` (line ~2159) and `Assets.Scripts.Objects.Item.OnUseSecondary` (line ~399) in game version 0.2.6228.27061. Confirmed `SprayCan` does not override `OnUseSecondary` by decompiling `Assets.Scripts.Objects.Items.SprayCan` (no `OnUseSecondary` / `AllowSelfUse` members declared).
- 2026-04-22: added "Parallel secondary-action dispatch via KeyManager" subsection documenting the second right-click edge via `KeyManager.cs:141` (`KeyMap._SecondaryAction.Bind(InputPhase.Up, ToggleActiveHandTool)`) and the `CheckTogglePower` / `ShouldToggleOn` gates (`DynamicThing.cs:1085`, `Tool.cs:50`, `DynamicThing.cs:1179`) that make the mouse-up edge a no-op for a bare `SprayCan` because `Consumable` inherits the `CanTogglePower == false` default. Additive.

## Open questions

None at creation.
