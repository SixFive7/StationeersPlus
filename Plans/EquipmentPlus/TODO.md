# Equipment Plus: TODO

Working notes for the ConfigCartridge click + scroll fixes. Verification is pending; once the bugs are confirmed gone, strip the diagnostic logging and close out this file.

## In flight

- **Scroll (S1)**: fixed in `ConfigCartridgePatches.cs`. `ConfigCartridgeScrollPatch.Prefix` is now `bool`, returns `false` on ConfigCartridge to suppress vanilla's free-scroll, has the correct sign (`scrollDelta.y > 0 ? -1 : +1`), and drives `_scrollPanel.SetScrollPosition((float)selected / (count - 1))`. `ConfigCartridgeScreenPatch.Postfix` re-applies the same formula every frame so the viewport keeps tracking selection after cartridge swaps and text-size changes.
- **Click (C1 + C2 + C3)**: source handler reads correctly on paper. Built DLL at `bin/Release/EquipmentPlus.dll` is 43008 bytes as of 2026-04-16 19:40. Has NOT been deployed yet (user was mid-session). Fallback diagnostic logging is armed via `ConfigCartridgeState.ClickTrace = true` and logs one line per click fire plus a labeled bail at each early-return gate. Ctrl parity is aligned via `ConfigCartridgeState.AnyCtrlHeld()` using `KeyManager.GetButton` for both LeftCtrl and RightCtrl, matching `ClickCyclePatch`.

## Pending work

- [ ] Deploy `bin/Release/EquipmentPlus.dll` to the local Stationeers mods deploy folder (see `DEV.md` / `DEV.md.template` for the exact path on your setup) after confirming the build. Update `About.xml` alongside if it changed (it has not in this round).
- [ ] Verify scroll behavior in-game (see test regime below).
- [ ] Verify click behavior in-game. If logs show the handler never runs, the root cause is environmental (load order, conflict detector, deploy path). If logs show a specific gate bailing, that's the real bug and needs a targeted follow-up.
- [ ] Once click is confirmed working, flip `ConfigCartridgeState.ClickTrace` to `false` and rebuild. The extra log lines are noisy on a long cartridge scan.
- [ ] Consider S2 (viewport-aware scroll that only snaps `SetScrollPosition` when the selected line would leave the visible area) if S1's instant snap feels abrupt. S2 requires measuring `_displayTextMesh.preferredHeight` vs the `ScrollPanel` viewport height via reflection.

## Planned features

Scope items identified from a gap analysis vs other tablet, lens, and headlamp mods (Better Advanced Tablet, ImprovedConfiguration, Slot Configuration Cartridge, Inventory Tweaks, Fixing The Controls, Better Headlamp). Implement after the in-flight click+scroll fix is verified and shipped. Implement A first (blocks B), then B and C in either order, then D.

### A. Add Better Headlamp to the incompatibility list

- [x] Append `("betterheadlampmod", "Better Headlamp")` to `ConflictingMods` in `Plugin.cs`. The assembly-name scan is the authoritative path (StationeersLaunchPad bypasses `[BepInIncompatibility]`); follow the Slot Configuration Cartridge pattern of assembly-only entry, no attribute. Reason: EquipmentPlus absorbs Better Headlamp's beam-adjust feature under a different binding (Ctrl+Shift+scroll); leaving it loaded would double-handle scroll input on equipped helmets.

### B. Rebind cycling from click to scroll, integrate Better Headlamp beam control

Replaces Ctrl+left-click on cartridges and worn lenses with a unified scroll-modifier scheme. Plain left-click on a cartridge line keeps its current role (writable line opens `InputWindow`, read-only line copies to clipboard); only the cycling moves off click.

Input bindings:

| Input | Behavior |
|---|---|
| Plain left-click on cartridge line | Unchanged. Writable opens `InputWindow`; read-only copies to clipboard. |
| Ctrl + scroll | Cycle tablet cartridges. Up = next in insertion order; down = previous. Down past first turns the tablet OFF. Up from OFF returns to the last-active cartridge and turns the tablet ON; the selection is preserved across OFF / ON, not reset to cartridge 1. No wrap on either end. Empty slots skipped. Active hand must hold the tablet. |
| Shift + scroll | Cycle the active sensor chip inside the worn lens (the chip slots inside the GlassesSlot lens, analogous to cartridges in the tablet). Up = next; down = previous. Down past first turns the lens OFF. Up from OFF returns to the last-active chip and turns the lens ON; the selection is preserved across OFF / ON, not reset to chip 1. No wrap on either end. Empty slots skipped. A lens must be worn in GlassesSlot. |
| Ctrl + Shift + scroll | Adjust helmet beam (Better Headlamp logic). Up tightens (smaller spot angle, brighter, longer range); down widens. Continuous, no discrete positions, no OFF in the cycle. Helmet on/off remains independent of beam width. See item C for default and persistence. |

Tasks:

- [ ] Drop the existing chip-in-held-lens cycle entirely. Held-lens Ctrl+click no longer cycles chips. Worn-lens chip cycling moves to Shift+scroll per the table above.
- [ ] Implement scroll capture (model after Better Headlamp's `InventoryManager.NormalMode` prefix) with modifier-key disambiguation between Ctrl, Shift, and Ctrl+Shift.
- [ ] Confirm the existing `Cartridge.OnScroll` patch (current line-select scroll on the cartridge UI) does not double-fire when Ctrl or Shift is held. Ctrl/Shift/Ctrl+Shift+scroll suppress the line-select scroll; plain scroll still selects lines.
- [ ] **Suppress native inventory scroll** (vanilla hotbar / inventory selection on scroll wheel) whenever Ctrl, Shift, or Ctrl+Shift is held. Plain scroll continues to drive the vanilla inventory selection unchanged. Better Headlamp does NOT do this and it bleeds inventory selection into beam-adjust input, which is annoying; our scroll-capture prefix must consume the event so the vanilla inventory handler does not see it. Implementation: return false from the scroll-capture prefix on a modifier-active match (or otherwise zero out the scroll delta before vanilla reads it).
- [ ] Active-hand and worn-slot scope checks: Ctrl+scroll fires only with tablet in active hand; Shift+scroll fires only with a lens worn in GlassesSlot. Mirror the existing click-handler scope gates.
- [ ] **State preservation across OFF / ON.** Scrolling down past the first cartridge / chip turns the device OFF without erasing the last-active selection. Scrolling up from OFF re-engages at that preserved position, not at index 1. Reuse EquipmentPlus's existing persistent-active-cartridge (`Interactable.State`-based, canonical feature 16) and persistent-active-sensor-chip (canonical feature 14) state. The OFF state is the same selection state with `OnOff` cleared; do not introduce a parallel "remembered position" field. Reasoning: this matches save/load behavior, which already preserves selection across the heaviest possible state break, and is also the least-code path because EquipmentPlus already tracks the active state.
- [ ] Update README and `About.xml` Description / InGameDescription to document the new bindings (per `Mods/Template/LAYOUT.md`).

### C. Helmet beam: most-wide default, per-player saved setting

- [ ] Default beam is most-wide on first observation: spot angle = MaxAngle (90 degrees), intensity = MinIntensity, range = MinRange (Better Headlamp's config values at the wide end). Vanilla starts wider than narrowest; we want the explicit wide preset.
- [ ] Persistence scope is per-character per-save (NOT a BepInEx machine config). Saved value lives on the Human / player save data, persists across save/load, applies across every helmet that player equips.
- [ ] **Before implementing C, read [PowerTransmitterPlus](../../Mods/PowerTransmitterPlus/) and [SprayPaintPlus](../../Mods/SprayPaintPlus/) to study their save-data extension pattern.** Likely uses LaunchPadBooster's `MOD.AddSaveDataType<>()` plus direct `XmlSaveLoad.ExtraTypes` injection and `Serializers._worldData` invalidation, mirroring what `Plugin.cs` already does for `EquipmentPlusTabletSaveData` and `SensorLensesSaveData`. The mod-removal-safe extension pattern is mandatory: uninstalling EquipmentPlus must not break saves with embedded helmet-beam state.
- [ ] Apply the saved value to the active helmet light every frame (Better Headlamp-style postfix in `Human.LateUpdate` or equivalent).
- [ ] Ctrl+Shift+scroll updates the saved per-player value, then the per-frame apply path picks it up. Use Better Headlamp's existing config keys (Step, MinAngle, MaxAngle, MinIntensity, MaxIntensity, MinRange, MaxRange, AutoBrightness) ported into EquipmentPlus's BepInEx config under a new `Client - Helmet Beam` section (per the section-naming rule in root `CLAUDE.md`).
- [ ] No-op conditions for Ctrl+Shift+scroll. In all three cases, do nothing and do not update the saved value:
  - No helmet equipped on the player.
  - Helmet light is off.
  - Helmet has no power.

### D. Future: Ctrl + scroll-up auto-equips a tablet when none in hand

- [ ] Defer until A, B, C are landed. If the player Ctrl+scrolls up while the active hand is empty (or holds something other than a tablet), find the first tablet in inventory and equip it to the active hand before applying the cartridge cycle. Analogous to Fixing The Controls' Tablet hotkey, but on the Ctrl+scroll surface introduced in B.

### E. Multiplayer slot-logic write (replace today's host-only stub)

Today, `WriteLogicSlotValue` applies on host / single-player and logs a warning on remote clients (per the in-flight section's test regime note 5). Slot Configuration Cartridge had no multiplayer path either; remote-client writes silently desynced. End state: any client clicks a writable slot logic line, the value lands on every client.

- [ ] **Decompilation research first, 100% coverage before code.** Before writing any custom network message, decompile and read every game path that touches slot-logic writes. Cover at minimum: `Device`, `Logicable`, `LogicableExtensions`, `Slot`, `LogicSlotType`, `LogicableSlot`, anything matching `*FromClient` or `*Slot*Set*` patterns. Goal: confirm or refute the existence of a vanilla `SetLogicSlotFromClient` (or equivalent client-to-server slot-write path). If it exists, route through it. Implementation collapses to a one-line change. If it doesn't exist, only then build a custom message. Record the decompilation findings in `RESEARCH.md` (per the curation rule in root `CLAUDE.md` and `Research/WORKFLOW.md`) so future contributors and audits can verify the conclusion.
- [ ] **If a vanilla path exists**, route `WriteLogicSlotValue` on remote clients through it instead of the warning branch. Done.
- [ ] **If no vanilla path exists**, register a new LaunchPadBooster message modeled on the existing `SetActiveSensorMessage` (`Plugin.cs:73`). Message carries: device `ReferenceId` (long), slot index (int), `LogicSlotType` (enum), value (double).
- [ ] **Authority model: option A (any client can request, server validates).** Server receives, re-checks `CanLogicSlotWrite` for the (slot, LogicSlotType) pair, applies via the slot's existing logic-set entry point. Game state-sync handles propagation back to all clients on the next tick. Matches the game's existing `SetLogicFromClient` convention so slot writes feel identical to non-slot writes.
- [ ] **Remove the existing host-only warning entirely.** No "slot writes are host-only" log line, no greyed-out InputWindow on remote clients, no caveats in any user-visible text. The mod does not ship until this works for every client; warnings about partial functionality are dead code by release time.
- [ ] Update test regime note 5 above to drop the host-only caveat and describe the unified multiplayer-working behavior. Update README and About.xml to describe the slot-write feature unconditionally.

### F. Slot logic value precision: match base-game display convention

`ConfigCartridgeSlotDisplay.cs:155` currently does `Math.Round(rawValue, 2).ToString()`, displaying slot logic values to 2 decimals. Slot Configuration Cartridge used 3. Neither of those is necessarily the right answer; the right answer is to mirror however the base game itself formats logic values everywhere it displays one (logic readers, Stationpedia entries, tooltips, the non-slot ConfigCartridge output, item tooltips). Players see consistent precision regardless of which surface is showing the value.

- [ ] **Decompilation research first, 100% coverage before changing the format.** Identify every base-game site that formats a `LogicType` or `LogicSlotType` numeric value for display. Cover at minimum: `LogicableExtensions`, `LogicReader`, `Stationpedia`, `ConfigCartridge.ReadLogicText` (the non-slot path), `Cartridge` subclasses that print values, tooltip generators, and any custom `ToString()` overrides on `Logicable` / `Device` / readout widgets. Determine the format spec actually used: `Math.Round(value, N)`, `.ToString("F2")` / `"F3"` / `"G"`, culture handling (invariant vs. current), trailing-zero behavior, exponent suppression. The expected outcome is a single canonical format spec used across the game; if the base game itself is inconsistent, document the inconsistency in `RESEARCH.md` and pick the format used by `ConfigCartridge.ReadLogicText` for non-slot values, since visual continuity inside one cartridge surface matters more than cross-surface uniformity.
- [ ] Apply the discovered format to `ConfigCartridgeSlotDisplay.cs:155`. One-line change once the format is known.
- [ ] Curate the finding to a page under `Research/` per `Research/WORKFLOW.md` Rule 2. Stamp `verified_in: 0.2.6228.27061` (current game version at the time of writing this TODO; bump to whatever is current at implementation time).

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
