# Equipment Plus: TODO

This file tracks open issues only. When an item is done, remove it rather than marking it done. Completed work lives in git history.

Implemented changes still awaiting an in-game or dedicated-server test do not belong here; record those in `PLAYTEST.md` (same folder).

## BUG: AdvancedTablet logic-value writes are silently dropped for non-ISetable devices on a dedicated server

Confirmed root cause (static analysis, 100% certain, 2026-05-21). Ready for implementation.

**Symptom.** A remote client on a dedicated server uses the AdvancedTablet (ConfigCartridge click-to-write) to write a device-level LogicType value, and nothing happens: the value reads back 0, the device does not react. Originally hit with PowerTransmitterPlus `MicrowaveAutoAimTarget` on a dish, but the bug is general (see Scope). Works fine in single-player and when the host does it.

**Root cause.** `ConfigCartridgePatches.WriteLogicValue` (`ConfigCartridgePatches.cs:456-473`) does this:

- Host branch (`NetworkManager.IsServer`): `device.SetLogicValue(logicType, value)` directly. Works -- the device is resolved as a concrete `Device`/`ILogicable` and the call lands.
- Client branch (`NetworkManager.IsClient`): sends the vanilla `SetLogicFromClient` message.

The vanilla `SetLogicFromClient.Process` (Assembly-CSharp decompile line 258660-258682) applies the write ONLY if the target resolves as `ISetable`:

```csharp
if (!(Thing.Find<Thing>(LogicId) is ISetable setable))
    WaitUntilFound(hostId, Process, Process, instanceId, 10f, "LogicMirror"); // retry then give up
else if (setable.CanLogicWrite(LogicType))
    setable.SetLogicValue(LogicType, Value);
```

`ISetable` is implemented by only a subset of devices (Transformer, Waypoint, LogicUnitBase, Stacker, AdvancedFurnace, SettableAtmosDevice, VendingMachineRefrigerated, AdvancedSuit, SuitBase). It is NOT implemented by `WirelessPower` / `PowerTransmitter` / `PowerReceiver` or their base `ElectricalInputOutput`, nor by plain `Device`. So for a non-`ISetable` target the cast fails, the write is diverted into `WaitUntilFound` (a 10s mirror-resolution retry intended for unresolved LogicMirror targets), and `SetLogicValue` is NEVER called. Hence zero effect, value reads back 0. The host branch avoids this because it calls `SetLogicValue` directly through `ILogicable`, never touching the `ISetable` gate.

Full game-internals writeup: `Research/Protocols/LogicValueWriteMessages.md` ("SetLogicFromClient (id 37): the handheld / settable write, gated on ISetable"). Note the value is a full-precision `double` on this path -- float quantization is NOT involved (that is a different, RocketMotherboard-panel-only path).

**Scope.** This is NOT specific to PowerTransmitterPlus or auto-aim. ANY device-level LogicType write via the AdvancedTablet from a remote client fails when the target device is `ILogicable` but not `ISetable` (dishes, and likely batteries, solar panels, and most plain `Device`s). The per-slot write path (`WriteLogicSlotValue` -> `SetLogicSlotFromClientMessage`, the Case 9 path) is unaffected because it already uses a custom server-resolving message, not the vanilla `ISetable`-gated one.

**Fix (recommended).** Mirror the existing slot pattern: replace the client branch's vanilla `SetLogicFromClient` with a custom client->server message whose server-side `Process` resolves the target as `ILogicable` (which every `Device` is) and applies the write the same way the host branch does.

- [ ] Add `SetLogicFromClientMessage` (device-level), parallel to the existing `SetLogicSlotFromClientMessage`. Fields: `long DeviceId; LogicType LogicType; double Value;`. Register it in `Plugin.cs` alongside the slot message.
- [ ] `Process` server-side: `var d = Thing.Find<ILogicable>(DeviceId); if (d != null && d.CanLogicWrite(LogicType)) d.SetLogicValue(LogicType, Value);`. Keep the `CanLogicWrite` re-check as server-side authority (a remote client should not be able to write a non-writable LogicType). This mirrors the host branch but adds the safety gate the host branch currently relies on the client-side UI for.
- [ ] Change `ConfigCartridgePatches.WriteLogicValue` client branch to send this new message instead of vanilla `SetLogicFromClient`.
- [ ] Leave the host branch (`device.SetLogicValue(...)` direct) as-is; it already works.

Alternative considered: route through the vanilla `SetLogicValueMessage` (id 104), whose `Process` resolves `Thing.Find<ILogicable>` with no `ISetable` gate. Workable (its value encoding defaults to full-precision `double` for a custom LogicType), but it couples to vanilla's per-type encoding table and is normally the RocketMotherboard panel's message; the custom-message approach above is more robust and matches the slot pattern already in the mod.

**Verification.** On a dedicated server with a remote client: write a device-level LogicType to a non-`ISetable` device via the AdvancedTablet (e.g. PowerTransmitterPlus `MicrowaveAutoAimTarget` on a dish, with PowerTransmitterPlus installed) and confirm the value lands and reads back. Cross-check that a write to an `ISetable` device (e.g. a Transformer) still works. Host/single-player must remain unaffected. PowerTransmitterPlus's IC10 control test already proved the dish's server-side `SetLogicValue` works, so a successful tablet write end-to-end confirms the fix.

## Resume after compact (2026-04-27 evening session)

User requested four items in one batch then paused for compaction. Pick up here:

### A. Helmet power gating (PROMOTED FROM "in-game check" — implement now)

User wants explicit power check before scroll turns the helmet light on. Implementation, not just verification.

- File: `Mods/EquipmentPlus/EquipmentPlus/HelmetBeamPatches.cs`, function `HandleScroll(Human, int)`.
- Current behavior: only checks `helmet.HasOnOffState`, then `helmet.OnOff = true`. Trusts vanilla to refuse on unpowered.
- Plan:
  1. Add a `HelmetHasPower(DynamicThing helmet) -> bool` helper. Iterate `helmet.Slots`, find a battery slot (vanilla helmets have `Slot.Class.Battery` — verify via decompile of `ItemHelmet*` prefab class hierarchy), get the occupant cast as a power source (likely `BatteryCell` / `BatteryCellLarge` in `Assets.Scripts.Objects.Items`; their public `PowerStored` field is what matters), return `> 0`.
  2. Gate the OFF→ON path in `HandleScroll` (around line 104 `if (!helmet.OnOff)` block): if `!HelmetHasPower(helmet)`, bail with `ConsoleWindow.PrintError("[EquipmentPlus] Helmet has no power.")` and a scroll-trace log line. Do NOT also gate the brightness-adjust path — if it's already on, it's powered.
- Research need before coding: confirm helmet battery slot class enum and the power-source type. Decompile is at `C:/Users/jori/Downloads/tmp-eq7-research/dump/Assets/Scripts/Objects/Items/` — grep for `Helmet.cs`, `HelmetBase.cs`, `BatteryCell.cs`, `Slot.cs`/`Class` enum.
- Verification after impl: equip helmet with no battery → scroll → expect console error and no `OnOff=true`. Drain a battery to 0 → scroll → same. Power back up → scroll → light turns on.

### B. Suppress vanilla NormalMode hotbar/inventory scroll when ANY modifier is held

User reports: while holding a modifier and scrolling, vanilla inventory selection cycles in addition to the modifier-scroll dispatch. Need to consume the scroll event when a modifier is held.

- File: `Mods/EquipmentPlus/EquipmentPlus/ScrollDispatchPatches.cs`, class `ScrollDispatchPatch`.
- Current: `public static void Prefix(InventoryManager __instance)` — implicit return true, vanilla NormalMode runs after our prefix and processes the same scroll for hotbar advance.
- Fix: change signature to `public static bool Prefix(InventoryManager __instance)`. Return:
  - `false` (suppress vanilla) when ANY of `ctrl || leftShift || rightShift` is held — including unbound combos like Ctrl+RightShift; the player is clearly not asking for plain hotbar advance.
  - `true` (let vanilla run) when no modifier is held = plain scroll → vanilla hotbar.
- Edge case to verify after fix: RightShift+scroll alone should still drive vanilla camera zoom. The camera handler is `CameraController.CacheCameraPosition`, NOT NormalMode, so suppressing NormalMode does NOT affect camera zoom. Confirm in-game.
- Risk: NormalMode likely also handles non-scroll inputs (the method is named generically). Read the surrounding decompile (`Assets.Scripts.Inventory.InventoryManager.NormalMode`) to make sure returning `false` only suppresses the scroll-related logic and doesn't break other things. If NormalMode does more than scroll, the suppression must be conditional on `scroll != 0f` — which we already check at the top of our prefix, so the bail is safe before any non-scroll work would have run. Verify by reading NormalMode in the decompile.

### C. Swap Config Cartridge slot-line colors (Item F resolved)

User decision: read-only = grey, writable = green (the current scheme is reversed: writable = grey, read-only = green).

- File: `Mods/EquipmentPlus/EquipmentPlus/ConfigCartridgeSlotDisplay.cs`, lines 69-70:
  ```csharp
  private const string ColorWritable  = "<color=grey>";   // -> "<color=green>"
  private const string ColorReadOnly  = "<color=green>";  // -> "<color=grey>"
  ```
  Trivial swap.
- **Also fix the wording in user-facing docs** (this is the gotcha — easy to miss):
  - `Mods/EquipmentPlus/README.md`, line ~42 area: "Writable slots render in grey, read-only slots in green" → "Writable slots render in green, read-only slots in grey".
  - `Mods/EquipmentPlus/EquipmentPlus/About/About.xml` `<Description>`: same wording. Search for "writable slots in grey".
  - `<InGameDescription>`: same — "(grey = writable, green = read-only)" → "(green = writable, grey = read-only)".
  - `<ChangeLog>` v1.0.0 entry: "writable in grey, read-only in green" → swap.
- Verify after edit: Description size cap still under 8000.
- **Open question to flag for user, NOT decided**: do the *non-slot* (main vanilla) LogicType lines on the cartridge also use writable=grey/read-only=green coloring? That comes from vanilla `ConfigCartridge.ReadLogicText` or inherited ImprovedConfiguration behavior — NOT from our slot-display patch. If yes, the swap looks inconsistent (slot lines green=writable, vanilla lines grey=writable). Need to check the vanilla rendering. If it's a problem, two options: (a) leave the inconsistency and document, (b) Harmony-patch the vanilla LogicType-line color too. Recommend checking after the slot-line swap is shipped and observing.

### D. S2 explanation (delivered to user; kept here for compaction survival)

**S2 — Viewport-aware scroll-position snap.** Today, `ConfigCartridgeScrollPatch.Prefix` (in `ConfigCartridgePatches.cs`) calls `scrollPanel.SetScrollPosition(pos)` on every scroll tick, where `pos = (float)current / (lineCount - 1)` — a normalized 0..1 position in the panel. That's an unconditional viewport snap: every wheel tick recomputes the scroll position and jumps the panel to make the highlight match. If the viewport already had the highlight visible somewhere on screen (say, line 5 of 30, with lines 0-15 currently rendered), the snap still fires and the panel re-anchors so the highlight is at proportional position 5/29 — usually meaning the panel jumps slightly. It's correct, just abrupt: any scroll that doesn't move the highlight far visibly jolts the page. Reading a long cartridge feels less smooth than vanilla's natural scroll. The smarter behavior is "only snap when the highlight would otherwise leave the visible window" — track the panel's current scroll range, check if the new selection's pixel position falls inside it, and call `SetScrollPosition` only when the answer is no.

### E. Implement S2 (PROMOTED FROM DEFERRED — user added to active fix list)

User wants the viewport-aware snap implemented now alongside A/B/C.

- File: `Mods/EquipmentPlus/EquipmentPlus/ConfigCartridgePatches.cs`. Two call sites for `scrollPanel.SetScrollPosition(pos)`:
  - `ConfigCartridgeScrollPatch.Prefix` (around line 116-121): on the wheel-tick path.
  - `ConfigCartridgeScreenPatch.Postfix` (around line 166-171): on the per-frame text-rebuild path. This one is more aggressive — re-snaps EVERY frame so the highlight tracks through `_needTopScroll` and other vanilla resets. Be careful not to break that recovery behavior.
- Plan:
  1. **Decompile-find** the visible-range API on `Assets.Scripts.UI.CustomScrollPanel.ScrollPanel`. Need a way to ask "what normalized range is currently visible?" so we can answer "is `selPos` inside it?". Likely candidates to look for: `NormalizedScrollPosition`, `Viewport`, `ContentRect`, `ScrollRect.normalizedPosition`, or a getter that returns the same value `SetScrollPosition` writes to. If `ScrollPanel` wraps a Unity `ScrollRect` internally, the answer is `scrollRect.verticalNormalizedPosition` plus `viewport.rect.height / content.rect.height` for the visible fraction. Decompile dumps available at `C:/Users/jori/Downloads/tmp-eq7-research/dump/Assets/Scripts/UI/CustomScrollPanel/`.
  2. **Helper**: `IsSelectionVisible(ScrollPanel panel, float selPos) -> bool`. Returns true if `selPos` is between the current top and bottom of the visible window (with maybe a small margin so the highlight isn't right at the edge).
  3. **Wheel-tick site**: replace unconditional `SetScrollPosition(pos)` with `if (!IsSelectionVisible(panel, pos)) panel.SetScrollPosition(NearestEdgePosition(panel, pos));` — where `NearestEdgePosition` snaps just enough to bring the highlight to the nearest visible edge instead of dead-center.
  4. **Per-frame postfix site**: this one's trickier. The current frame-by-frame snap is what defeats vanilla's `_needTopScroll` reset. If we make it conditional, vanilla's reset can drift the panel away from the highlight and our conditional snap might never fire. **Safer change**: keep the per-frame snap unconditional but ONLY when the panel was just reset (`_needTopScroll` flag was set this frame, or scrollPosition has moved compared to last frame). Otherwise leave alone. This requires tracking the previous-frame scroll position. Or: only do the per-frame snap when the highlight is visibly off-screen, accepting that one frame of drift is OK before we re-snap.
  5. **Test**: scroll a long cartridge (LogicMemory, complex device) from top to bottom slowly — should feel smooth, no jolt per tick when highlight is visible. Scroll fast — should still keep highlight visible. Switch to a different cartridge mid-scroll (vanilla `_needTopScroll` triggers) — highlight should still snap into view next frame.
- Risk: misjudging the visible range causes either (a) highlight scrolls off-screen unnoticed, or (b) the snap stops working entirely. Both are user-visible regressions worse than the current "abrupt but correct" behavior. Add a fallback: if the visible-range query fails or returns garbage, fall back to the unconditional snap.

### Order of operations after compact

1. Read source files at new Mods/ paths (Read tool needs a fresh read in a new session). Files: `HelmetBeamPatches.cs`, `ScrollDispatchPatches.cs`, `ConfigCartridgePatches.cs`, `ConfigCartridgeSlotDisplay.cs`. Already read (still in context, but compaction will drop them).
2. Item C first (smallest, low-risk): swap two consts in `ConfigCartridgeSlotDisplay.cs` + wording fixes in README/About.xml (writable=green now, read-only=grey). Build + verify size caps + redeploy.
3. Item B: decompile-check `InventoryManager.NormalMode` (does it do non-scroll work that our prefix would skip if we returned false?), then change Prefix signature to `bool` and add the suppression on any modifier. Build + redeploy.
4. Item A: decompile-find helmet battery slot class + power-source field, write `HelmetHasPower` helper, gate the OFF→ON path in `HandleScroll`. Build + redeploy.
5. Item E (S2): decompile-check `ScrollPanel` for visible-range API, write `IsSelectionVisible` helper, make the wheel-tick snap conditional, decide on the per-frame postfix policy (keep unconditional with movement detection vs make conditional with fallback). Build + redeploy.
6. Re-test A/B/C/E in single-player. The multiplayer test list (Case 1-Case 11) is in `PLAYTEST.md`.

Bundling note: A/B/C/E are independent edits across four files. C and B are pure source edits, no decompile needed. A and E both need a quick decompile pass first. Recommended: do C (5 min), then B (15 min), then A (30 min including decompile), then E (45 min including decompile + the trickier per-frame snap question). One build + deploy at the end. Keep diagnostic logging in for these edits — they'll help diagnose any regression. The diagnostic-strip pass stays a separate later todo.

## Before next release

- [ ] **Strip diagnostic logging.** One sweep covering `ConfigCartridgeState.ClickTrace` (`ConfigCartridgePatches.cs:52`), `ScrollDispatchState.ScrollTrace` (`ScrollDispatchPatches.cs`), per-state-transition `LogInfo` lines in `CycleTablet` / `CycleLens` / `HelmetBeamPatches`, the `[EquipmentPlus.rebind]` step trace, and the scroll-dispatch no-op spam. Then build + redeploy.

## Deferred (not blocking release)

(Both prior deferred items — F. color revisit and S2. viewport-aware snap — are now active in the post-compact resume plan above as items C and E.)
