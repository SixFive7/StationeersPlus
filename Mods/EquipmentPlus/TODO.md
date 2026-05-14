# Equipment Plus: TODO

Open work items only. Done items live in git history.

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
6. Re-test A/B/C/E in single-player. Multiplayer test list (Case 1-Case 11) still pending friend.

Bundling note: A/B/C/E are independent edits across four files. C and B are pure source edits, no decompile needed. A and E both need a quick decompile pass first. Recommended: do C (5 min), then B (15 min), then A (30 min including decompile), then E (45 min including decompile + the trickier per-frame snap question). One build + deploy at the end. Keep diagnostic logging in for these edits — they'll help diagnose any regression. The diagnostic-strip pass stays a separate later todo.

## Before next release

- [ ] **Strip diagnostic logging.** One sweep covering `ConfigCartridgeState.ClickTrace` (`ConfigCartridgePatches.cs:52`), `ScrollDispatchState.ScrollTrace` (`ScrollDispatchPatches.cs`), per-state-transition `LogInfo` lines in `CycleTablet` / `CycleLens` / `HelmetBeamPatches`, the `[EquipmentPlus.rebind]` step trace, and the scroll-dispatch no-op spam. Then build + redeploy.

## Pending in-game / multiplayer testing

- [ ] **Multiplayer test checklist (deferred until friend online).** Two-player coverage:
  - Case 1 — Both holding a tablet, A: Ctrl+scroll cycles A's cartridges; B sees A's tablet update via vanilla state-sync.
  - Case 2 — A has tablet in off-hand, A: Ctrl+scroll triggers `SwapHands` (A-local; B sees no swap visualization).
  - Case 3 — A has tablet in toolbelt, active hand empty, A: Ctrl+scroll equips via `OnServer.MoveToSlot`; B observes the move.
  - Case 4 — A has tablet in toolbelt, active hand has SmartStow-able item, A: Ctrl+scroll. ⚠️ Risk: 1-frame yield may be insufficient on internet-latency multiplayer and false-fail the stow check; watch the log for the false fallback into the swap path.
  - Case 5 — A has tablet in toolbelt, active hand item NOT SmartStow-able and NOT type-compatible with toolbelt slot, off-hand empty, A: Ctrl+scroll → 3-way swap via off-hand.
  - Case 6 — Same as Case 5 but off-hand also occupied → abort with local console message "[EquipmentPlus] No room to swap..."; B sees nothing.
  - Case 7 — A wears lens, B wears different lens, A: LeftShift+scroll → A's lens cycles; B sees A's chip change via `SensorLensesSync.ActiveSensorFlag`.
  - Case 8 — A scrolling while B disconnects mid-action; A's coroutine completes or fails gracefully, no NREs.
  - Case 9 (Item 7) — B (remote client) clicks a writable slot logic line on a host's device; the server applies via `SetLogicSlotFromClient` and replicates back to all clients.
  - Case 10 (Item 6) — B adjusts helmet beam; A sees B's beam visibly tighten/widen via `SetBeamSettingsMessage` rebroadcast.
  - Case 11 (Item 6) — B adjusts beam, host saves, B disconnects, host reloads, B reconnects → B's beam restored at saved angle (host log: "Restored N helmet-beam entries from host join").
  - Mitigation if Case 4 hits the latency issue: extend the yield to multiple frames OR poll a state predicate (`yield until activeHandSlot.Get() != prevOccupant || N frames elapsed`).

## Deferred (not blocking release)

(Both prior deferred items — F. color revisit and S2. viewport-aware snap — are now active in the post-compact resume plan above as items C and E.)
