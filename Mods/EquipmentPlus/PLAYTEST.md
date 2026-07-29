# Equipment Plus Playtests

Implemented changes awaiting in-game confirmation. An agent records a playtest here, not in `TODO.md`, when it has changed code whose behavior can only be confirmed by running the game: single-player, a hosted multiplayer session, or the dedicated server under `DedicatedServer/`. This keeps `TODO.md` a list of work still to do, and gives one place to check whether a change already has a pending test before adding another.

Rules:
- Add an entry when code is implemented but its in-game behavior is unconfirmed. Write down everything a tester needs: what changed (commit if there is one), single-player vs multiplayer / dedicated-server, the save or world to set up, the exact in-game steps, what to watch (InspectorPlus request files, specific log lines, on-screen behavior, IC10 reads), and the expected result. Point at any staged `.work/<date>-<slug>/` request files or playbook.
- Check first. Before adding, scan the entries below so a change already covered by a pending test is not duplicated; extend the existing entry instead.
- Remove an entry when one of these happens: a run confirms it works; a run shows it broken (then add a fresh `TODO.md` item for the fix, or keep working on it now); or the player says the playtest is done. Entries are plain bullets, not checkboxes: like `TODO.md`, finished items are removed, not ticked off. Outcomes live in git history.

- **In-game console output rework (uncommitted, 2026-07-27).** Three changes to what EquipmentPlus puts in the console F3 opens. All three are single-player testable; no second player needed.
  - What changed. A new `PlayerNotice` helper (`PlayerNotice.cs`) replaces the two bare `ConsoleWindow.PrintError` calls. Both messages keep their exact wording but are now yellow (`PrintAction`) instead of red, carry no stack trace, pass `aged: false` so they show without opening the console, and are throttled to one identical message per 5 seconds. Separately, the mod-conflict banner in `Plugin.cs` (`RepeatWarning`) no longer loops `Debug.LogError` forever; it prints six times at 5-second intervals via `PrintError(suppressStacktrace: true)` and then stops.
  - Mode: single-player. Any world with a helmet and an advanced tablet.
  - Case A (helmet, previously a red stack-trace block per notch): equip a helmet with no battery or a fully drained one, light off, and spin the scroll wheel hard, 15+ notches in one flick. Expect ONE yellow line "[EquipmentPlus] Helmet has no power." and no stack trace, not one per notch. Wait 5 seconds, scroll again, expect a second line. Confirm the line is visible bottom-left WITHOUT pressing F3 (that is the `aged: false` behavior), then press F3 and confirm it is in the buffer too. Re-fit a charged battery and confirm the light still turns on.
  - Case B (tablet swap): fill the inventory and occupy both hands so the tablet auto-equip has nowhere to stow, then Ctrl+scroll repeatedly. Same expectation: one throttled yellow "[EquipmentPlus] No room to swap..." line, no stack trace, no per-notch repeat.
  - Case C (conflict banner, only if a conflicting mod is available): load with BetterAdvancedTablet or the improved_configuration mod enabled. Expect exactly six red "[EquipmentPlus] NOT LOADED! ..." lines about 5 seconds apart, the last one ending "(This warning will stop repeating; see the BepInEx log.)", and then silence for the rest of the session. Previously this repeated forever.
  - Watch for: any doubled line. A message appearing twice would mean something still pairs `Debug.LogError` with a ConsoleWindow call, which is the defect this pass removed.

- **Multiplayer test checklist (deferred until a second player is online).** Two-player coverage of the already-implemented scroll-dispatch, hand-swap, lens-cycle, slot-write, and helmet-beam sync paths. Single-player re-tests of items A/B/C/E stay in `TODO.md` because that code is not written yet; this checklist is the multiplayer-only backlog.
  - Mode: multiplayer, two players (A and B).
  - Case 1: Both holding a tablet, A does Ctrl+scroll: cycles A's cartridges; B sees A's tablet update via vanilla state-sync.
  - Case 2: A has tablet in off-hand, A does Ctrl+scroll: triggers `SwapHands` (A-local; B sees no swap visualization).
  - Case 3: A has tablet in toolbelt, active hand empty, A does Ctrl+scroll: equips via `OnServer.MoveToSlot`; B observes the move.
  - Case 4: A has tablet in toolbelt, active hand has SmartStow-able item, A does Ctrl+scroll. Risk: the 1-frame yield may be insufficient on internet-latency multiplayer and false-fail the stow check; watch the log for the false fallback into the swap path.
  - Case 5: A has tablet in toolbelt, active hand item NOT SmartStow-able and NOT type-compatible with toolbelt slot, off-hand empty, A does Ctrl+scroll: 3-way swap via off-hand.
  - Case 6: Same as Case 5 but off-hand also occupied: abort with local console message "[EquipmentPlus] No room to swap..."; B sees nothing. (Same wording as before, but the line is now yellow rather than red and is throttled; see the console-output entry above.)
  - Case 7: A wears lens, B wears different lens, A does LeftShift+scroll: A's lens cycles; B sees A's chip change via `SensorLensesSync.ActiveSensorFlag`.
  - Case 8: A scrolling while B disconnects mid-action; A's coroutine completes or fails gracefully, no NREs.
  - Case 9 (Item 7): B (remote client) clicks a writable slot logic line on a host's device; the server applies via `SetLogicSlotFromClient` and replicates back to all clients.
  - Case 10 (Item 6): B adjusts helmet beam; A sees B's beam visibly tighten/widen via `SetBeamSettingsMessage` rebroadcast.
  - Case 11 (Item 6): B adjusts beam, host saves, B disconnects, host reloads, B reconnects: B's beam restored at saved angle (host log: "Restored N helmet-beam entries from host join").
  - Mitigation if Case 4 hits the latency issue: extend the yield to multiple frames OR poll a state predicate (`yield until activeHandSlot.Get() != prevOccupant || N frames elapsed`).
