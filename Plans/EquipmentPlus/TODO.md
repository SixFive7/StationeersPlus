# Equipment Plus: TODO

Working notes for the ConfigCartridge click + scroll fixes. Verification is pending; once the bugs are confirmed gone, strip the diagnostic logging and close out this file.

## In flight

- **Scroll (S1)**: fixed in `ConfigCartridgePatches.cs`. `ConfigCartridgeScrollPatch.Prefix` is now `bool`, returns `false` on ConfigCartridge to suppress vanilla's free-scroll, has the correct sign (`scrollDelta.y > 0 ? -1 : +1`), and drives `_scrollPanel.SetScrollPosition((float)selected / (count - 1))`. `ConfigCartridgeScreenPatch.Postfix` re-applies the same formula every frame so the viewport keeps tracking selection after cartridge swaps and text-size changes.
- **Click (C1 + C2 + C3)**: source handler reads correctly on paper. Built DLL at `bin/Release/EquipmentPlus.dll` is 43008 bytes as of 2026-04-16 19:40. Has NOT been deployed yet (user was mid-session). Fallback diagnostic logging is armed via `ConfigCartridgeState.ClickTrace = true` and logs one line per click fire plus a labeled bail at each early-return gate. Ctrl parity is aligned via `ConfigCartridgeState.AnyCtrlHeld()` using `KeyManager.GetButton` for both LeftCtrl and RightCtrl, matching `ClickCyclePatch`.

## Pending work

- [ ] Deploy `bin/Release/EquipmentPlus.dll` to `C:\Users\jori\Documents\My Games\Stationeers\mods\EquipmentPlus\` after the user gives the go-ahead. Update `About.xml` alongside if it changed (it has not in this round).
- [ ] Verify scroll behavior in-game (see test regime below).
- [ ] Verify click behavior in-game. If logs show the handler never runs, the root cause is environmental (load order, conflict detector, deploy path). If logs show a specific gate bailing, that's the real bug and needs a targeted follow-up.
- [ ] Once click is confirmed working, flip `ConfigCartridgeState.ClickTrace` to `false` and rebuild. The extra log lines are noisy on a long cartridge scan.
- [ ] Consider S2 (viewport-aware scroll that only snaps `SetScrollPosition` when the selected line would leave the visible area) if S1's instant snap feels abrupt. S2 requires measuring `_displayTextMesh.preferredHeight` vs the `ScrollPanel` viewport height via reflection.

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

If any of 1-6 fails, the log (tail `E:/Steam/steamapps/common/Stationeers/BepInEx/LogOutput.log`) should contain an `[EP.click]` line from the bail branch. That line names the exact gate that failed.

## Notes to keep

- The user's active Stationeers deploy path is `C:\Users\jori\Documents\My Games\Stationeers\mods\<ModName>\`. The sibling `Stationeers - Copy*` folders under `My Games` are full-game backups and must never be read or written.
- `Plugin.OnAllModsLoaded` aborts `Harmony.PatchAll()` if any of `BetterAdvancedTablet`, `ImprovedConfiguration`, or `SlotConfigurationCartridge` is loaded. Users reporting "the new behavior doesn't happen" with any of those mods still enabled are expected. Log line to look for on abort: `CONFLICT: {mod} is loaded` followed by `EquipmentPlus NOT LOADED`.
- `Cartridge.UpdateEachFrame` fires `OnScreenUpdate` for every cartridge in the tablet, not just the displayed one. The `tablet.Cartridge != __instance` guard in the click handler is load-bearing; removing it causes clicks on device A to open an input window for whichever inactive cartridge was last scrolled.
- `ConfigCartridge.OnScreenUpdate` overwrites `_displayTextMesh.text = _outputText` every call. Any text-mesh edit must be re-applied in a postfix every frame. The text-equality guard `if (textMesh.text != newText) textMesh.text = newText;` avoids redundant TMP mesh rebuilds.
- `ConfigCartridge.ScannedDevice` is live from `CursorManager.CursorThing as Device` with an `IsMasterAuthority` gate. The cursor must still be on the device at the click frame, or the click path bails silently. Document in README if users report "nothing happens when I look away and click".
- `Item.OnUsePrimary` does NOT fire for `AdvancedTablet` because `AllowSelfUse == false`. Patching there is a dead end for tablet interactions. `InventoryManager.HandlePrimaryUse` is the correct hook.
- `KeyManager.GetMouseDown("Primary")` is frame-stable (`Input.GetKeyDown(KeyMap.PrimaryAction)` under the hood). Multiple patches can read it in the same frame without interference.
- `InputWindow.ShowInputPanel` signature is `(string title, string default, Action<string,string> callback, int maxLength)`; upstream uses `maxLength = 600` and so do we. The second string in the callback is unused.
