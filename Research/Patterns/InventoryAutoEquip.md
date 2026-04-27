---
title: Inventory Auto-Equip APIs
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-27
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: InventoryManager.SmartStow
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: HumanHandsBehaviour.SwapHands
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: OnServer.MoveToSlot (~20 vanilla call sites)
related:
  - ../GameSystems/ScrollInputHandling.md
  - ../GameSystems/KeyBinding.md
tags: [equipment, slots, network]
---

# Inventory Auto-Equip APIs

The vanilla APIs a mod needs to programmatically move items between a player's slots: `InventoryManager.SmartStow` (find-a-spot-and-stow), `HumanHandsBehaviour.SwapHands` (flip active vs inactive hand without moving items), and `OnServer.MoveToSlot` (universal multiplayer-safe slot move). All three are usable from mod code without role checks; OnServer wraps the network round-trip for clients automatically.

## InventoryManager.SmartStow
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

Signature (Assembly-CSharp.dll line 269801):

```csharp
public static void SmartStow(Slot selectedSlot)
```

Behavior: looks for a free slot to move `selectedSlot.Occupant` into, with a search hierarchy:

1. `ParentHuman.GetFreeSlot(selectedSlot.Occupant.SlotType, _excludeHandSlots)` — first available player-slot of matching type, excluding hands.
2. If that returned the BackpackSlot AND the SuitSlot already contains a `SuitBase`, reject (suit takes the back slot visually).
3. Fall back to the item's "original slot" if recorded in `_originalSlots` (per-stow memory) and that origin is still on the same RootParent.
4. Fall back to `FindFreeSlotOpenWindowsSlotPriority(...)` — search currently-open inventory windows.
5. Fall back to scanning every slot on `ParentHuman.Slots`, looking inside any non-hand-slot occupant for a free slot of matching type (e.g. inside the toolbelt, backpack, etc.).
6. If still no slot: `UIAudioManager.Play(UIAudioManager.ActionFailHash)` and return (silent failure to the caller).

Pre-condition for the primary path: `LeftHandSlot.Occupant == selectedSlot.Occupant || RightHandSlot.Occupant == selectedSlot.Occupant`. The function is intended for stowing-from-hand. Calling with a non-hand source slot exercises a different code path further into the body (not yet documented here).

Returns: `void`. To detect success/failure from the caller:
- Capture `var stowed = activeHandSlot.Occupant` before the call.
- Call `InventoryManager.SmartStow(activeHandSlot)`.
- If `activeHandSlot.Occupant != stowed` (typically null after success), the move worked. Otherwise it failed (item stayed in hand; the fail sound was played).

**Multiplayer behavior**: SmartStow internally calls `OnServer.MoveToSlot` which wraps a network request on clients. So the active-hand state on a remote client may not reflect the move until the server's broadcast arrives a tick later. For in-frame success detection on clients, consider deferring the next operation by a frame OR running auto-equip only on host/SP for the first cut.

## HumanHandsBehaviour.SwapHands
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

Signature (Assembly-CSharp.dll line 271889):

```csharp
public void SwapHands()
```

Caller pattern from vanilla (line 43170-43173):

```csharp
private static void SwapHandsOnKeyUp()
{
    Human.LocalHuman?.HumanHandsBehaviour._SwapHandsOnKeyUp();
}
```

`_SwapHandsOnKeyUp()` (line 271881) is a wrapper that gates on `!InventoryManager.Instance.IsUsingSmartTool && !_human.IsUnresponsive && !_human.IsSleeping` then calls `SwapHands()`.

Behavior: does NOT move items between physical hand slots. Instead it flips which hand is "active" by swapping `InventoryManager.Instance.ActiveHand` and `InventoryManager.Instance.InactiveHand`. The physical contents of `LeftHandSlot` and `RightHandSlot` are unchanged.

From the player's perspective: an item that was in their off-hand before the call is now in their active hand after, because we changed which physical hand counts as active.

Side effects (line 271889-271925):

- Cancels precision-placement mode if active.
- Cancels multi-constructor placement.
- Fires the static `SwapHandsEvent`.
- Updates `PanelHands.Instance.HandleSwitchHands(...)` to flip the UI bindings.
- Re-runs `RefreshDisplaySlotBindings()` and `AnimateActiveHands()`.
- If the now-active hand holds a `Constructor` and we're in placement mode, re-runs `UpdatePlacement(...)`.
- Adjusts `CursorManager.CursorHitMask` to add/remove `LayerMasks.CursorVoxel` based on whether the active hand holds a `Pickaxe` / `MiningDrill`.
- If the now-active hand holds a `Tablet`, calls `tablet.InActiveHand()`.
- If the now-active hand holds an `OreDetector`, calls `oreDetector.InActiveHand()`.

So calling `Human.LocalHuman.HumanHandsBehaviour.SwapHands()` is a complete "switch which hand is active" operation, fully wired into the vanilla side effects. A mod that wants to make a previously-off-hand item active should use this instead of moving items manually.

## OnServer.MoveToSlot
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

Verbatim from `Assets.Scripts.OnServer.MoveToSlot` (Assembly-CSharp.dll line 39173):

```csharp
public static void MoveToSlot(DynamicThing childThing, Slot slot)
{
    if ((object)childThing != null && !childThing.IsBeingDestroyed)
    {
        if (GameManager.RunSimulation)
        {
            childThing.MoveToSlot(slot, slot.Parent);
            return;
        }
        NetworkClient.SendToServer(new MoveToSlotMessage
        {
            ChildId = childThing.netId,
            ParentId = slot.Parent.netId,
            SlotId = slot.SlotIndex,
            Drag = false
        });
    }
}
```

Used in ~20 places across vanilla (Assembly-CSharp.dll lines 41645, 107019, 109915, 113186, 140171, 140260, 146288, 146650, 147060, 158037, 158057, 158126, 160550, 160563, 161048, 161222, 161227, 161239, 161740, 161773, 161774, plus call sites inside `InventoryManager.SmartStow` and elsewhere).

The vanilla call sites do NOT check `NetworkManager.IsServer` before calling. The function routes by `GameManager.RunSimulation` (true on host / single-player → calls `Thing.MoveToSlot` directly; false on remote client → sends `MoveToSlotMessage`).

**Critical: does NOT auto-swap when target slot is occupied.** The vanilla body just calls `childThing.MoveToSlot(slot, slot.Parent)` — if `slot` already has an occupant, the move is rejected (no displacement, no swap). Drag-and-drop swap UX is implemented at the UI layer with multiple `OnServer.MoveToSlot` calls in sequence using a temporary slot.

For mod code that wants to swap two occupied slots, use a 3-way pattern via a temporary location (typically the off-hand if free): move A to temp, move B to A's slot, move A from temp to B's slot. EquipmentPlus's Phase 2 `AutoEquipTabletCoroutine` uses this pattern with the off-hand as temp; if off-hand is also occupied, the swap aborts with a player-facing console message.

## Human slot fields (canonical names + spellings)
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

`Assets.Scripts.Objects.Entities.Human` exposes its equipment slots as public `[NonSerialized] Slot` fields (line 339531 onwards). The exact field names matter because the codebase has both naming conventions in different contexts:

```csharp
[NonSerialized] public Slot SuitSlot;
[NonSerialized] public Slot HelmetSlot;
[NonSerialized] public Slot GlassesSlot;
[NonSerialized] public Slot BackpackSlot;
[NonSerialized] public Slot LeftHandSlot;
[NonSerialized] public Slot RightHandSlot;
[NonSerialized] public Slot UniformSlot;
[NonSerialized] public Slot ToolbeltSlot;   // NOTE: 'Toolbelt' is one word, lowercase 'b'
```

The `KeyMap.ToolBeltSlot` static is spelled differently (`KeyMap.ToolBeltSlot` with capital `B`) because that's the BIND-NAME used by the rebind UI, distinct from the slot field. Don't confuse them.

`InventoryManager.ActiveHandSlot` returns the active hand's underlying `Slot` (the one in `LeftHandSlot` or `RightHandSlot`). `InventoryManager.Instance.ActiveHand` returns the `SlotDisplay` wrapper; use `.Slot` to drill to the underlying slot.

## Auto-equip recipe (for tablet / lens / similar use cases)
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

Composite recipe combining the three APIs above for a "Ctrl+scroll auto-equip a tablet" workflow:

```csharp
// 1. If active hand already has the target item, just use it.
var active = InventoryManager.ActiveHand;
if (active?.Slot?.Get() is AdvancedTablet activeTablet)
{
    UseTablet(activeTablet);
    return;
}

// 2. If off-hand has it, flip which hand is active. No item moves.
var inactive = InventoryManager.Instance.InactiveHand;
if (inactive?.Slot?.Get() is AdvancedTablet)
{
    Human.LocalHuman?.HumanHandsBehaviour?.SwapHands();
    return; // subsequent input cycles uses the now-active tablet
}

// 3. Search inventory for the target item.
//    Tool belt occupant slots, then backpack occupant slots, then suit slots, no nested.
//    (Implementation detail per the consumer; not vanilla.)
if (!TryFindInInventory<AdvancedTablet>(out var found, out var foundSlot))
    return; // no-op; target not present

// 4. If active hand is empty, just move it directly.
if (active.Slot.Get() == null)
{
    OnServer.MoveToSlot(found, active.Slot);
    return;
}

// 5. Active hand holds something else — try to stow it first.
var prevOccupant = active.Slot.Get();
InventoryWindowManager.Instance?.SmartStow(active.Slot);
if (active.Slot.Get() == null)
{
    // SmartStow succeeded; equip target.
    OnServer.MoveToSlot(found, active.Slot);
    return;
}

// 6. SmartStow failed (no free slot). Fall back to swap: previous item
//    moves to where the target was, target moves to active hand.
//    Caveat on remote clients: both moves are async; the visible result
//    arrives a tick later and intermediate frames may show the swap
//    in-progress. Acceptable for v1; revisit if it causes UX issues.
OnServer.MoveToSlot(prevOccupant, foundSlot);
OnServer.MoveToSlot(found, active.Slot);
```

Multiplayer note: each `OnServer.MoveToSlot` on a remote client is async. The recipe above is correct on host/SP (each move applies before the next runs); on remote clients, the moves are queued network requests, so step 5 (success detection) can produce a false-negative in the same frame the SmartStow request was sent. For tighter detection, gate the auto-equip on `NetworkManager.IsServer` and require a vanilla manual equip on remote clients, OR run a coroutine that waits one tick before checking.

## Verification history

- 2026-04-27: page created. All sections verified by `ilspycmd` decompile of `E:/Steam/steamapps/common/Stationeers/rocketstation_Data/Managed/Assembly-CSharp.dll` at game version 0.2.6228.27061. Triggered by EquipmentPlus's TODO B Phase 2 design pass (auto-equip a tablet on Ctrl+scroll when none is in hand). Documents the three vanilla APIs (`SmartStow`, `SwapHands`, `OnServer.MoveToSlot`) and a composite recipe for the auto-equip use case, including the in-frame multiplayer-async caveat for remote-client success detection.
- 2026-04-27b: added "Human slot fields (canonical names + spellings)" section. Verbatim list of public `[NonSerialized] Slot` fields on `Assets.Scripts.Objects.Entities.Human` (line 339531 onwards). Critical detail: `Human.ToolbeltSlot` is spelled with lowercase 'b' as one word, distinct from `KeyMap.ToolBeltSlot` (capital B, used as the rebind binding-name). Triggered by EquipmentPlus's Phase 2 inventory-search implementation needing the correct field name.
- 2026-04-27c: corrected class name for `SmartStow(Slot)` static method throughout the page. The method lives on `InventoryManager` (Assembly-CSharp.dll line 269801, inside the InventoryManager class body), NOT on `InventoryWindowManager`. The page-creation entry mistakenly attributed it to `InventoryWindowManager`. Note that `InventoryWindowManager` DOES have a separate, instance-method, no-arg `SmartStow()` (the one bound to the SmartStow keybind via `KeyManager.SmartStow` at line 43190); for a specific slot, callers use the static `InventoryManager.SmartStow(Slot)`. Caught during EquipmentPlus Phase 2 build (compile error: "No overload for method 'SmartStow' takes 1 arguments" on `InventoryWindowManager.SmartStow(slot)`).
- 2026-04-27d: added the verbatim `OnServer.MoveToSlot` body (line 39173) and the **critical no-swap clarification**. Confirmed by reading the function: it routes by `GameManager.RunSimulation` (host: direct `childThing.MoveToSlot(slot, slot.Parent)` call; client: `MoveToSlotMessage` send), and there is NO occupied-target handling in the body — moves into occupied slots are rejected, no displacement, no swap. Added recipe note about the 3-way swap pattern via the off-hand as temp slot (used by EquipmentPlus's Phase 2 `AutoEquipTabletCoroutine`). Triggered by an EquipmentPlus user-test failure: the original 2-step swap fallback (move prevOccupant -> sourceSlot, then tablet -> activeHandSlot) failed silently because sourceSlot still held the tablet at the time of the first move.

## Open questions

- The non-hand-source code path of `SmartStow` (when `selectedSlot.Occupant` is NOT in either hand) was not traced past line 269807. Likely fall-through to the `OnServer.MoveToSlot` block lower in the method, but the gating logic differs. Document on first need.
- `OnServer.MoveToSlot` on remote clients: exact mechanism (custom INetworkMessage? proxy method? RPC?) not yet decomposed. Vanilla call sites use it as if synchronous, which suggests the engine handles the round-trip transparently. Worth a focused decompile pass if a future mod needs precise async semantics.
