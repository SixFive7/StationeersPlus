# Equipment Plus: TODO

Working notes for active development. Strip all diagnostic logging in one pass before final release.

## Current status (2026-04-27)

- **TODO B Phase 1: SHIPPED.** Auto-rebind of `KeyMap.ThirdPersonControl` from LeftShift to RightShift works one-shot per launch via `Plugin.EnsureCameraKeyDoesNotConflict`. Tablet cycling (Ctrl+scroll), lens cycling (LeftShift+scroll), headlamp adjust (Ctrl+LeftShift+scroll), vanilla camera on RightShift+scroll all verified in-game. Direction-inversion fix verified. State preservation across OFF/ON works for both tablet and lens.
- **Phase 1 follow-up bugfix: SHIPPED.** `WindowOpenRefreshPatch` now re-registers visible slots after `HandleOccupantChange` (added 2026-04-27 to fix tablet/lens window slots being orphaned from the plain-scroll cycle).
- **Phase 1 verbose rebind logging: TRIMMED** (2026-04-27). `EnsureCameraKeyDoesNotConflict` now emits one outcome line per code path instead of ~20 step traces. ScrollTrace stays ON for Phase 2 iteration.
- **TODO B Phase 2: SHIPPED** (2026-04-27). Hand-switch, auto-equip from inventory (Toolbelt → Backpack → Suit), SmartStow-then-equip with deferred-check coroutine, and 3-way swap via off-hand temp slot all verified in-game in single-player. Failure path prints player-facing console message via `ConsoleWindow.PrintError`. Multiplayer testing deferred until friend is online.
- **TODO B Phase 3: SHIPPED** (2026-04-27). Documentation updated: `README.md` (scroll-modifier table + camera-key auto-rebind callout + comparison-table fix), `About.xml` `<Description>` (mirrored sections), `<InGameDescription>` (replaced cycling line with scroll-modifier bullets), `<Version>` bumped 1.0.0 → 1.1.0, `<ChangeLog>` v1.1.0 entry added, `Plugin.cs` `PluginVersion` bumped to match. Size caps verified: Description 4563/8000, InGameDescription 1259/1450, ChangeLog 2056/8000.
- **ClickCyclePatch dropped + light caching SHIPPED** (2026-04-27). `ClickCyclePatch.cs` deleted, csproj cleaned, comment references purged across 4 files. `HelmetBeamPatches.HelmetBeamApplyPatch.Postfix` no longer recomputes the helmet's controllable Light per frame; cache invalidates on helmet swap.

## Promoted items (next sessions, in priority order)

- Item 6 — **Per-character per-save persistence of helmet beam settings**. Pattern: mirror EquipmentPlus's existing `EquipmentPlusTabletSaveData` and `SensorLensesSaveData` extension via `MOD.AddSaveDataType<>()` + direct `XmlSaveLoad.ExtraTypes` injection (see `Plugin.cs.RegisterSaveDataTypeLate`). Mod-removal-safe extension is mandatory: uninstalling EquipmentPlus must not break saves with embedded beam state. Read `Mods/PowerTransmitterPlus` and `Mods/SprayPaintPlus` save-data extensions for prior art.
- Item 7 — **Multiplayer slot-logic write to replace today's host-only stub** in `ConfigCartridgePatches.WriteLogicSlotValue`. **Decompile-first**: confirm or refute the existence of a vanilla `SetLogicSlotFromClient` (or equivalent client-to-server slot-write path). If exists, route through it. If not, build a custom LaunchPadBooster message modeled on `SetActiveSensorMessage`. Authority model: server validates, applies, broadcasts; client sends request and receives state via vanilla state-sync. Remove the existing host-only warning entirely once the new path works.

## Phase 2 multiplayer test checklist (defer until friend online)

Run each in MP with two players. Confirm both observe consistent state and the local-action player gets correct UX.

| # | Setup | Action | Expected |
|---|---|---|---|
| MP1 | Both players have an AdvancedTablet in active hand | Player A: Ctrl+scroll | A's tablet cycles cartridges. B sees A's tablet update via vanilla state sync (cartridge change is networked through `Interactable.State`). |
| MP2 | Player A has tablet in off-hand | A: Ctrl+scroll | A's hands swap (LOCAL only, not networked — `SwapHands` only flips A's `InventoryManager.ActiveHand`/`InactiveHand`). B sees A's physical hand contents unchanged but no swap visualization. |
| MP3 | A has tablet in toolbelt, active hand empty | A: Ctrl+scroll | `OnServer.MoveToSlot(tablet, activeHandSlot)` sent as `MoveToSlotMessage`. Server applies, broadcasts. After ~1 frame yield, A sees tablet in active hand. B observes the move via the same broadcast. |
| MP4 | A has tablet in toolbelt, active hand has SmartStow-able item | A: Ctrl+scroll | `SmartStow` sends moves via `MoveToSlotMessage`. After A's coroutine yields one frame, A's stow check should pass (server roundtrip arrived). Tablet equip moves next. ⚠️ **Risk: high latency may make 1-frame yield insufficient on B's perspective; A might see false-negative on stow check and incorrectly try the swap fallback.** Watch the log. |
| MP5 | A has tablet in toolbelt, active hand item NOT SmartStow-able and NOT type-compatible with toolbelt slot, off-hand empty | A: Ctrl+scroll | 3-way swap via off-hand. After all moves complete, tablet in active hand, prevOccupant in toolbelt slot. B observes the three sequential moves. |
| MP6 | Same as MP5 but off-hand also occupied | A: Ctrl+scroll | Abort with console message "[EquipmentPlus] No room to swap..." printed in A's local F3 console only. B sees nothing. |
| MP7 | A has lens worn, B has different lens worn | A: LeftShift+scroll | A's lens cycles via `SetActiveSensorMessage` (sent to host). Lens state syncs via `SensorLensesSync.ActiveSensorFlag` broadcast. B sees A's lens change. |
| MP8 | A in active session, B disconnects mid-swap | A: Ctrl+scroll while B disconnects | A's coroutine continues; the local moves complete based on server response (or fail gracefully if A is the disconnecting one). No NREs in log. |

**Known acceptable race conditions**:
- Two players grabbing the same tablet at the same moment: server honors the first request, second sees its target slot empty and aborts.
- High-latency client may see a one-frame visual flicker during the 3-way swap (tablet briefly visible in off-hand). Acceptable.

**MP failure-mode analysis for the 1-frame yield**:
- The yield is `yield return null` which waits exactly one frame regardless of latency.
- On localhost / LAN: server roundtrip is well under 1 frame (~1-2ms vs 16.6ms frame). Yield is more than sufficient.
- On internet MP with 50-100ms latency: 1 frame at 60fps is 16.6ms. Server roundtrip might exceed the yield. Result: A's coroutine reads stale `activeHandSlot.Get()` (sees prevOccupant still there), incorrectly thinks SmartStow failed, falls through to swap fallback. Swap fallback might double-apply if the stow eventually succeeds.
- Mitigation if MP testing reveals issues: extend yield to multiple frames OR poll a state predicate (e.g., yield until `activeHandSlot.Get() != prevOccupant || N frames elapsed`). Defer to MP testing results before committing to either.

## In-game test checklist (next session)

- [ ] **Helmet power gating**: equip a helmet with a DEAD battery (no power). Try Alt+scroll wait actually it's now Ctrl+LeftShift+scroll. If the light "turns on" visually but the helmet is unpowered, vanilla probably reverts OnOff next frame, leaving the player confused. If so, add an explicit power check (scan helmet slots for `BatteryInstance` / `PowerCellSlotted` with `PowerStored > 0`).
- [ ] Verify ClickCyclePatch removal didn't break anything: tablet+lens scroll bindings still work; the BepInEx log has no "patch failed" or "method not found" lines from the removed Harmony attribute.

## Pending (cross-cutting)

- [ ] **Strip ALL diagnostic logging before release.** Sweep covers: `ConfigCartridgeState.ClickTrace` (`ConfigCartridgePatches.cs:52`), `ScrollDispatchState.ScrollTrace` (`ScrollDispatchPatches.cs`), per-state-transition logs in `CycleTablet`/`CycleLens`/`HelmetBeamPatches`, and any LogInfo added during Phase 2/3. Do once the mod is feature-complete and verified, not piecemeal.
- [ ] Consider S2 (viewport-aware scroll, only snap `SetScrollPosition` when selection would leave the visible area). Defer until a player reports S1's instant snap feels abrupt.

## Planned features

Scope items identified from a gap analysis vs other tablet, lens, and headlamp mods (Better Advanced Tablet, ImprovedConfiguration, Slot Configuration Cartridge, Inventory Tweaks, Fixing The Controls, Better Headlamp). Implement after the in-flight click+scroll fix is verified and shipped. Implement B and C in either order, then D.

### B. Bind scroll-modifier combos to equipment cycling, headlamp beam, and camera

Modifier-per-equipment mapping: Ctrl = tablet, Shift = lens, Alt = headlamp, Ctrl+Alt+Shift = vanilla camera zoom (remapped from bare Shift). Plain scroll keeps its vanilla "advance hotbar / inventory selection" behavior unchanged; modifier+scroll adds equipment-specific actions. Plain left-click on a cartridge line is unchanged (writable opens `InputWindow`, read-only copies to clipboard).

Vanilla Shift+scroll is bound to camera zoom + first/third-person toggle in `CameraController.CacheCameraPosition`. The handler matches Shift inclusively, so any combo containing Shift triggers camera zoom unless suppressed. We Harmony-prefix the camera handler to suppress when bare Shift is held alone (no Ctrl, no Alt) so our Shift+scroll lens cycle has the input. All other Shift-bearing combos (including the explicit Ctrl+Alt+Shift) fall through to vanilla camera zoom. See `Research/GameClasses/CameraController.md` and `Research/GameSystems/ScrollInputHandling.md`.

Input bindings:

| Input | Behavior |
|---|---|
| Plain scroll | Vanilla: advances hotbar / inventory selection. Unchanged. |
| Plain left-click on cartridge line | Vanilla within the cartridge UI. Unchanged. Writable opens `InputWindow`; read-only copies to clipboard. |
| Ctrl + scroll | **Tablet path.** If the active hand holds an `AdvancedTablet`, cycle cartridges. (Phase 2: hand-switch + auto-equip described below.) Otherwise no-op in Phase 1. |
| LeftShift + scroll | **Lens path.** A `SensorLenses` must be worn in `GlassesSlot` (no auto-equip; lenses don't have a sensible auto-equip story). Otherwise no-op. |
| Ctrl + LeftShift + scroll | **Headlamp path.** A helmet must be equipped on the player AND the helmet must have power. If both true and the light is off, the first scroll (either direction) turns it on at the preserved last beam value (or the most-wide default on first-ever turn-on). If the light is on, the scroll adjusts the beam. If no helmet or no power, no-op. Scrolling never turns the light off; the existing helmet-toggle binding is the only off path. |
| RightShift + scroll | **Vanilla camera zoom + first/third-person toggle.** EquipmentPlus auto-rebinds `KeyMap.ThirdPersonControl` from the vanilla default (LeftShift) to RightShift on mod load via `Plugin.EnsureCameraKeyDoesNotConflict`, so vanilla now fires on RightShift. The auto-rebind ONLY runs if the player still has the default LeftShift; any custom binding is left alone (per `Research/GameSystems/KeyBinding.md` four-step pattern). |
| Ctrl + RightShift + scroll, or any combo with both shifts | EquipmentPlus does NOT bind anything to these combos. Vanilla camera fires whenever RightShift is held (the rebound `KeyMap.ThirdPersonControl`). |
| ALT + anything | Vanilla captures ALT for its own mouse-input mode toggle, so ALT+scroll never reaches our prefix. We do not bind anything to ALT. |
| Any other modifier combo | No-op. |

#### Cycle rules (tablet cartridges, lens chips)

Apply identically to both paths. State is `(OnOff, ActiveIndex)` where `ActiveIndex` is the cartridge slot for tablet or the chip slot for lens.

- Wheel-up = advance to next occupied slot (skip empty); wheel-down = previous occupied slot.
- Down past the first occupied slot turns the device OFF without changing `ActiveIndex`. Up from OFF turns the device ON at the preserved `ActiveIndex` (NOT reset to slot 0). Reuse `EquipmentPlusTabletSaveData` / `SensorLensesSaveData` and the existing `ActiveSlotPersistence` machinery; do not introduce a parallel "remembered position" field.
- No wrap on either end (up past the last occupied slot is a no-op; down past OFF is a no-op).

#### Headlamp adjust rules

- Wheel-up = tighten (decrease spot angle toward `MinAngle`); wheel-down = widen (increase toward `MaxAngle`). Continuous, no discrete positions.
- Wheel-up at tightest = no-op (clamp). Wheel-down at widest = no-op (clamp; the light does NOT turn off via scroll).
- When `AutoBrightness` is on (default true), intensity and range scale with angle (tightest = brightest + longest range; widest = dimmest + shortest range). Math: `t = InverseLerp(MaxAngle, MinAngle, spotAngle)`, `intensity = Lerp(MinIntensity, MaxIntensity, t)`, `range = Lerp(MinRange, MaxRange, t)`. Lifted from Better Headlamp's `AdjustFocusExternal`.
- BepInEx config keys live under section `Client - Helmet Beam`: `Step` (degrees per tick, default 2.5), `MinAngle` (default 20), `MaxAngle` (default 90), `MinIntensity`, `MaxIntensity`, `MinRange`, `MaxRange`, `AutoBrightness`. Names and defaults mirror Better Headlamp.
- **Default beam on first turn-on for a character is most-wide**: `spotAngle = MaxAngle`, `intensity = MinIntensity`, `range = MinRange`. After that, scroll adjustments are remembered in-session per character.
- Per-character storage: a `Dictionary<long, BeamSettings>` keyed by `Human.ReferenceId`. Local-player input only (other peers don't drive scroll on this client). The dictionary is stable for the structure's eventual extension to per-character per-save persistence in C.

#### Camera-zoom suppression

- Single Harmony prefix (bool) on `CameraController.CacheCameraPosition`. Returns false ONLY when bare Shift is held alone (Shift true AND Ctrl false AND Alt false). Otherwise returns true (vanilla runs).
- The `KeyManager.GetButton(KeyMap.ThirdPersonControl)` gate inside vanilla still applies, so if the player has rebound `KeyMap.ThirdPersonControl` away from Shift, our suppression check (which uses raw `KeyCode.LeftShift` / `RightShift`) might over- or under-match. Acceptable for v1; document in README that rebinding `ThirdPersonControl` interacts with these mod bindings.

Tasks (Phase 1 - SHIPPED 2026-04-27, retained for context only): scroll capture prefix on `InventoryManager.NormalMode`, drop chip-in-held-lens click cycle, tablet/lens cycling with OFF/ON state preservation, headlamp adjust + `Human.LateUpdate` postfix, camera-zoom resolved via auto-rebind (NOT transpiler suppression — pivoted), `ConfigCartridgePatches` modifier early-return, BepInEx config bindings under `Client - Helmet Beam`. All verified in-game. NOTE: camera-zoom mechanism CHANGED from "transpiler-suppress vanilla under bare Shift" to "auto-rebind `KeyMap.ThirdPersonControl` to RightShift via four-step pattern in `Research/GameSystems/KeyBinding.md`". The transpiler approach was deleted (`CameraZoomSuppressPatches.cs` no longer exists).

Tasks (Phase 2 - tablet auto-equip and hand-switch, IN PROGRESS):

**Decision tree (in `DispatchTablet`)**:

1. Active hand has AdvancedTablet → cycle (Phase 1, unchanged).
2. Off-hand has AdvancedTablet → `Human.LocalHuman.HumanHandsBehaviour.SwapHands()`. Return — no cycle this scroll. Subsequent scrolls cycle.
3. Search inventory in order, no nested: `human.ToolbeltSlot` (note lowercase 'b') occupant's slots → `human.BackpackSlot` occupant's slots → `human.SuitSlot` occupant's slots. First AdvancedTablet found wins. **Slot field name gotcha**: `Human.ToolbeltSlot` is one word lowercase 'b'; `KeyMap.ToolBeltSlot` is capital B (per `Research/Patterns/InventoryAutoEquip.md` "Human slot fields").
4. If found AND active hand empty: `OnServer.MoveToSlot(tablet, activeHandSlot)`. Done.
5. If found AND active hand occupied: capture `prevOccupant`. Call `InventoryWindowManager.Instance?.SmartStow(activeHandSlot)`. **Defer one tick via coroutine** (multiplayer correctness — server-roundtrip on remote clients). After yield, check `activeHandSlot.Get() == null`. If true → `OnServer.MoveToSlot(tablet, activeHandSlot)`. Done.
6. Else (SmartStow failed, slot still occupied): try swap fallback. `OnServer.MoveToSlot(prevOccupant, tabletSourceSlot)`. **Defer one tick.** Check `tabletSourceSlot.Get() == prevOccupant`. If true (swap succeeded) → `OnServer.MoveToSlot(tablet, activeHandSlot)`. **If false (swap failed because the hand item doesn't fit in the tablet's source slot type): emit `ConsoleWindow.PrintError("[EquipmentPlus] Cannot clear active hand to equip tablet (current item doesn't fit anywhere); please move it manually.")` so the local player sees the failure in the F3 console.** No moves applied; player resolves manually.
7. No tablet found anywhere: silent no-op.

**Multiplayer**: all moves go through `OnServer.MoveToSlot` (universal entry point — see `Research/Patterns/InventoryAutoEquip.md`). Detection happens client-side after a one-frame yield (lets server broadcast arrive). Failure message via `ConsoleWindow.PrintError` is local-only (each client has their own console), so it appears for the player whose action triggered the failure.

**Coroutine entry point**: needs `EquipmentPlusPlugin.Instance` (a `MonoBehaviour` reference) for `StartCoroutine(...)`. Add `internal static EquipmentPlusPlugin Instance;` field to `Plugin.cs`, assign in `Awake()`.

**File changes**:

- `Plugin.cs`: add `Instance` static reference + assignment in `Awake()` (~3 LOC).
- `ScrollDispatchPatches.cs`: refactor `DispatchTablet` to call new `TryAutoEquipTablet()` helper before deciding cycle vs no-op. Add helper + `AutoEquipTabletCoroutine` IEnumerator + `FindTabletInInventory` helper (~120 LOC).
- (No new files.)

**Logging additions** (kept on for Phase 2 iteration; stripped in pre-release sweep):

- `[scroll] tablet auto-equip: hand-switch (off-hand has tablet)`
- `[scroll] tablet auto-equip: active hand empty, equipping <tablet> from <slot path>`
- `[scroll] tablet auto-equip: stowing <currentItem>, equipping <tablet>`
- `[scroll] tablet auto-equip: stow succeeded after yield, equipping`
- `[scroll] tablet auto-equip: stow failed, swapping <currentItem> -> <slot path>, <tablet> -> active hand`
- `[scroll] tablet auto-equip: swap succeeded after yield, equipping`
- `[scroll] tablet auto-equip: swap failed (current item doesn't fit), notifying player via ConsoleWindow.PrintError`
- `[scroll] tablet auto-equip: no tablet found anywhere`

**Tasks (all SHIPPED 2026-04-27)**:

- [x] `EquipmentPlusPlugin.Instance` static field + assignment in `Awake()`.
- [x] `TryAutoEquipTablet()` decision tree.
- [x] `AutoEquipTabletCoroutine(...)` with one-tick yields. Initial 2-step swap fallback was wrong (sourceSlot still occupied by tablet — `OnServer.MoveToSlot` does not auto-swap, see `Research/Patterns/InventoryAutoEquip.md`); upgraded to 3-way swap via off-hand as temp slot. Aborts cleanly if off-hand also occupied.
- [x] `FindTabletInInventory` helper scanning `ToolbeltSlot` -> `BackpackSlot` -> `SuitSlot` occupants.
- [x] `ConsoleWindow.PrintError("[EquipmentPlus] No room to swap. Manually stow or drop the item in your active hand to equip the tablet.")` on all abort paths.
- [x] Built and deployed; verified all single-player scenarios.
- [ ] **Multiplayer testing pending** (deferred until friend is online).

Tasks (Phase 3 - documentation):

- [ ] Update README and `About.xml` Description / InGameDescription to document the new bindings (per `Mods/Template/LAYOUT.md`). Include a "vanilla Shift+scroll camera zoom is now Ctrl+Alt+Shift+scroll" note prominently to head off support questions.

#### Project-setup gotcha (resolved 2026-04-27)

- [x] **`.csproj` uses old-style explicit `<Compile Include>` entries.** New `.cs` files do not auto-include; they must be listed in the `<ItemGroup>` block. Phase 1's first build silently produced a DLL missing the three new files (HelmetBeamPatches, ScrollDispatchPatches, CameraZoomSuppressPatches) because they were never compiled — the build succeeded because the files weren't referenced from any compiled file (Plugin.cs declared the static config fields used by HelmetBeamPatches but those references compile fine since they live on Plugin.cs itself). Symptom: deployed DLL grew only by the Plugin.cs config additions; runtime tests of the new dispatcher silently did nothing because the dispatcher type didn't exist in the assembly. Fix applied: added the three entries to `EquipmentPlus.csproj`. Future cleanup option: migrate to SDK-style csproj (auto-glob), but that's a bigger refactor; leave as a follow-up. Document this gotcha in DEV.md or the mod template if other mods in the monorepo are affected.

#### Phase 1 follow-ups (uncovered during implementation)

- [ ] **Helmet power gating is a proxy.** `HelmetBeamPatches.HandleScroll` checks `helmet.HasOnOffState` as a stand-in for "this helmet can be turned on", but does not explicitly verify battery power. We toggle `OnOff = true` and trust vanilla to refuse if the helmet is unpowered. Verify in-game what happens when scrolling a helmet with a dead battery: does vanilla immediately revert OnOff, or does the light stay on with no actual illumination? If the latter, add an explicit power check (likely a slot scan for a `BatteryInstance` / `PowerCellSlotted` with `PowerStored > 0`).
- [ ] **Light reference is recomputed every frame.** `HelmetBeamApplyPatch.Postfix` calls `TryGetActiveHelmet` -> `GetComponentsInChildren<Light>` for the local human's helmet every LateUpdate. Better Headlamp caches `_currentLight` keyed by `_currentHelmetInstanceId` and invalidates on swap. Tiny perf delta (only fires for one helmet) but the cleaner pattern is preferable. Cache the (helmetInstanceId -> Light) mapping if a profiler ever flags this.
- [ ] **Held-tablet and held-lens Ctrl+click cycles remain in `ClickCyclePatch`** as redundant fallbacks alongside the new scroll bindings (only the empty-hand worn-lens branch was dropped in Phase 1). Decide during/after Phase 2 whether to drop them entirely (cleanest), keep them as documented alternates, or rebind them to a different click chord. If we drop them, also revisit whether `ClickCyclePatch.cs` is needed at all.
- [ ] **Held-lens click cycle still nullifies `Sensor` on power-off.** `ClickCyclePatch.ApplySensorChange` ties power state to sensor presence (Sensor=null implies OnOff=false). The new scroll path uses `SetLensState` with independent parameters so OFF preserves Sensor. If we keep both code paths long-term, the click path's behavior is inconsistent with the scroll path's; align them or document the divergence.
- [ ] **README must call out the ThirdPersonControl auto-rebind.** First launch with EquipmentPlus and default vanilla controls auto-changes `KeyMap.ThirdPersonControl` from LeftShift to RightShift. Players who customized this binding are unaffected. Players who reset to defaults later will see the rebind re-fire on the next mod load. Document prominently so users don't think the mod broke their camera key.

#### Persistence (deferred to a future session)

- [ ] Per-character per-save persistence of helmet beam settings. Saved value lives on the Human / player save data, persists across save/load, applies across every helmet that player equips. Pattern: mirror EquipmentPlus's existing `EquipmentPlusTabletSaveData` and `SensorLensesSaveData` extension via LaunchPadBooster's `MOD.AddSaveDataType<>()` plus direct `XmlSaveLoad.ExtraTypes` injection (see `Plugin.cs` `RegisterSaveDataTypeLate`). The mod-removal-safe extension pattern is mandatory: uninstalling EquipmentPlus must not break saves with embedded beam state. Read [PowerTransmitterPlus](../../Mods/PowerTransmitterPlus/) and [SprayPaintPlus](../../Mods/SprayPaintPlus/) save-data extensions for prior art before implementing.

### C. (folded into B)

Section C's previous scope (helmet beam most-wide default, per-player saved setting, BepInEx config, no-op conditions, per-frame apply) has been folded into B's Phase 1. The remaining "per-character per-save persistence" item lives under B's "Persistence (deferred)" subsection.

### F. Cartridge color revisit

- [ ] Revisit the Config Cartridge line colors (writable = `<color=grey>`, read-only = `<color=green>`, selection highlight = `<color=#FFD561FF>`). Both the per-line value tags and the slot-header yellow are inherited verbatim from the absorbed mods (ImprovedConfiguration + Slot Configuration Cartridge). Decide whether to keep, retune for readability against the dark cartridge background, or theme to match the rest of the tablet UI. Concrete palette + rationale TBD.

### E. Multiplayer slot-logic write (replace today's host-only stub)

Today, `WriteLogicSlotValue` applies on host / single-player and logs a warning on remote clients (per the in-flight section's test regime note 5). Slot Configuration Cartridge had no multiplayer path either; remote-client writes silently desynced. End state: any client clicks a writable slot logic line, the value lands on every client.

- [ ] **Decompilation research first, 100% coverage before code.** Before writing any custom network message, decompile and read every game path that touches slot-logic writes. Cover at minimum: `Device`, `Logicable`, `LogicableExtensions`, `Slot`, `LogicSlotType`, `LogicableSlot`, anything matching `*FromClient` or `*Slot*Set*` patterns. Goal: confirm or refute the existence of a vanilla `SetLogicSlotFromClient` (or equivalent client-to-server slot-write path). If it exists, route through it. Implementation collapses to a one-line change. If it doesn't exist, only then build a custom message. Record the decompilation findings in `RESEARCH.md` (per the curation rule in root `CLAUDE.md` and `Research/WORKFLOW.md`) so future contributors and audits can verify the conclusion.
- [ ] **If a vanilla path exists**, route `WriteLogicSlotValue` on remote clients through it instead of the warning branch. Done.
- [ ] **If no vanilla path exists**, register a new LaunchPadBooster message modeled on the existing `SetActiveSensorMessage` (`Plugin.cs:73`). Message carries: device `ReferenceId` (long), slot index (int), `LogicSlotType` (enum), value (double).
- [ ] **Authority model: option A (any client can request, server validates).** Server receives, re-checks `CanLogicSlotWrite` for the (slot, LogicSlotType) pair, applies via the slot's existing logic-set entry point. Game state-sync handles propagation back to all clients on the next tick. Matches the game's existing `SetLogicFromClient` convention so slot writes feel identical to non-slot writes.
- [ ] **Remove the existing host-only warning entirely.** No "slot writes are host-only" log line, no greyed-out InputWindow on remote clients, no caveats in any user-visible text. The mod does not ship until this works for every client; warnings about partial functionality are dead code by release time.
- [ ] Update test regime note 5 above to drop the host-only caveat and describe the unified multiplayer-working behavior. Update README and About.xml to describe the slot-write feature unconditionally.

## Test regime

Run each test from an active in-game save with the built DLL deployed and the game restarted (StationeersLaunchPad autoloads the folder, but patches only bind at plugin Awake).

### Scroll tests

1. Equip Advanced Tablet, insert a Config Cartridge, point cursor at any logic-enabled device (e.g. a LogicReader / Daylight Sensor / Battery).
2. Expected baseline: cartridge screen shows `ReferenceId ... $HEXID` on line 0, then one line per readable `LogicType`. The first line should be highlighted in yellow.
3. Scroll wheel **up**: highlighted line moves UP (toward line 0). Viewport follows so the highlight stays visible.
4. Scroll wheel **down**: highlighted line moves DOWN (toward the last line). Viewport follows.
5. Long cartridge test: point at a device with enough logic types to overflow the viewport (e.g. a LogicMemory, Stacker, or a mod's complex device). Scroll from top to bottom: the highlight should stay in view the whole time, no drift.
6. Slot-line test (device with readable slots): scroll past the vanilla logic types into the appended slot-header / slot-logic section. Highlight should advance into the yellow `Slot N ... DisplayName` headers and the subsequent per-slot logic lines with correct colors (grey = writable, green = read-only).

### Click tests

All require: tablet in active hand, cartridge currently displayed (not a sibling cartridge in another slot), cursor on the scanned device at the instant of the click.

1. **Writable LogicType (grey)**: scroll to a grey line, plain left-click. Expected: `InputWindow` opens titled with the `LogicType` name, default value = current. Enter a new number, submit. Value should update on the device (visible in the cartridge display on next tick).
2. **Read-only LogicType (green)**: scroll to a green line, plain left-click. Expected: no window. Value is copied to clipboard. Paste somewhere (Windows + V, or another text field) to verify.
3. **ReferenceId (line 0)**: click. Expected: no-op. The `Enter.TryParse<LogicType>("ReferenceId", ...)` succeeds and routes to the read-only clipboard path, so this actually DOES copy `$HEXID` to clipboard. If the test is meant to confirm no window opens, that's the success criterion.
4. **Slot-header line (yellow)**: click. Expected: no-op. `Enum.TryParse` of `"Slot 0"` fails, click is silently ignored.
5. **Writable slot-logic line (grey, inside a slot section)**: click. Expected: `InputWindow` opens titled `Slot N / LogicSlotType`. Writes route through `WriteLogicSlotValue`: server applies directly, remote client logs a warning (slot writes are host-only until a custom network message is added).
6. **Read-only slot-logic line (green)**: click. Expected: clipboard copy of the numeric value.
7. **Ctrl held**: LeftCtrl+click and RightCtrl+click should NOT open the input window or copy. They should cycle to the next cartridge instead (via `ClickCyclePatch`). Confirm cycling works for both Ctrl keys (parity check).
8. **Tablet in off-hand**: switch tablet to the non-active hand, click. Expected: handler bails with `"bail: tablet not in ActiveHandSlot"` in the log.
9. **Cursor off device**: aim at empty air or a non-device prop, click. Expected: handler bails with `"bail: ScannedDevice is null"` in the log.

If any of 1-6 fails, the log (tail `E:/Steam/steamapps/common/Stationeers/BepInEx/LogOutput.log`) should contain an `[EquipmentPlus.click]` line from the bail branch. That line names the exact gate that failed.

## Notes to keep

- The Stationeers deploy folder layout is documented in `DEV.md.template` (section "Local deploy target"). If the deploy path has `Stationeers - Copy*` sibling folders, those are full-game backups and must never be read or written; treat only `mods/<ModName>/` as a valid target.
- `Plugin.OnAllModsLoaded` aborts `Harmony.PatchAll()` if any of `BetterAdvancedTablet`, `ImprovedConfiguration`, or `SlotConfigurationCartridge` is loaded. Users reporting "the new behavior doesn't happen" with any of those mods still enabled are expected. Log line to look for on abort: `CONFLICT: {mod} is loaded` followed by `EquipmentPlus NOT LOADED`.
- `Cartridge.UpdateEachFrame` fires `OnScreenUpdate` for every cartridge in the tablet, not just the displayed one. The `tablet.Cartridge != __instance` guard in the click handler is load-bearing; removing it causes clicks on device A to open an input window for whichever inactive cartridge was last scrolled.
- `ConfigCartridge.OnScreenUpdate` overwrites `_displayTextMesh.text = _outputText` every call. Any text-mesh edit must be re-applied in a postfix every frame. The text-equality guard `if (textMesh.text != newText) textMesh.text = newText;` avoids redundant TMP mesh rebuilds.
- `ConfigCartridge.ScannedDevice` is live from `CursorManager.CursorThing as Device` with an `IsMasterAuthority` gate. The cursor must still be on the device at the click frame, or the click path bails silently. Document in README if users report "nothing happens when I look away and click".
- `Item.OnUsePrimary` does NOT fire for `AdvancedTablet` because `AllowSelfUse == false`. Patching there is a dead end for tablet interactions. `InventoryManager.HandlePrimaryUse` is the correct hook.
- `KeyManager.GetMouseDown("Primary")` is frame-stable (`Input.GetKeyDown(KeyMap.PrimaryAction)` under the hood). Multiple patches can read it in the same frame without interference.
- `InputWindow.ShowInputPanel` signature is `(string title, string default, Action<string,string> callback, int maxLength)`; upstream uses `maxLength = 600` and so do we. The second string in the callback is unused.
