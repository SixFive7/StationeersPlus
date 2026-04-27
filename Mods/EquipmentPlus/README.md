# Equipment Plus

Adds dynamic multi-slot sensor lenses and advanced tablets to Stationeers, with multiplayer-safe configuration cartridge enhancements.

Full multiplayer compatibility. Safe to remove from existing savegames.

Slots appear as you fill them and disappear as you empty them. The UI always shows exactly one empty slot, never a wall of boxes.

> **WARNING:** This is a StationeersLaunchPad mod. It requires [BepInEx](https://docs.bepinex.dev/) and [StationeersLaunchPad](https://github.com/StationeersLaunchPad/StationeersLaunchPad) to be installed.

This mod builds on and replaces the work of **Serialtasted, spacebuilder2020, Erik(NL)** ([Better Advanced Tablet](https://steamcommunity.com/sharedfiles/filedetails/?id=3134483503)), **Doggo** ([ImprovedConfiguration](https://steamcommunity.com/sharedfiles/filedetails/?id=3651839114)), and **Otis B.** ([Slot Configuration Cartridge](https://steamcommunity.com/sharedfiles/filedetails/?id=3578912665)), whose original mods inspired this project. Equipment Plus combines their features, adds multiplayer support, persists the active sensor chip across save/load, and unifies the user experience.

## Features

### Dynamic Slot Growth: Sensor Lenses and Advanced Tablet
Insert a sensor chip or cartridge: a new empty slot appears. Remove one: the excess empty slot disappears. You always see exactly one empty slot inviting the next item, never a grid of empty boxes.

### Scroll-Modifier Bindings

Each modifier+scroll combination targets a different piece of equipment. Plain scroll keeps its vanilla "advance hotbar / inventory selection" behavior; modifier+scroll layers on top.

| Input | Behavior |
|---|---|
| Plain scroll | Vanilla. Advances the hotbar / inventory selection. |
| Ctrl + scroll | **Tablet.** Cycles cartridges on the held Advanced Tablet. If no tablet is in the active hand, EquipmentPlus auto-equips one from the off-hand, toolbelt, backpack, or suit (in that order). If the active hand holds an item, it is stowed first; if no slot accepts it, EquipmentPlus uses the off-hand as a temp slot to swap. If neither path works, an in-game console message explains the abort. |
| LeftShift + scroll | **Lens.** Cycles sensor chips on the worn `SensorLenses`. No auto-equip (lenses must already be worn). |
| Ctrl + LeftShift + scroll | **Helmet beam.** Tightens (wheel-up) or widens (wheel-down) the worn helmet's spotlight. If the light is off, the first scroll turns it on at the last beam value. Scrolling never turns the light off. Auto-brightness scales intensity and range with angle. Configurable via `Client - Helmet Beam` settings (step, min/max angle, min/max intensity, min/max range, auto-brightness). |
| RightShift + scroll | **Vanilla camera.** Zoom and first/third-person toggle. EquipmentPlus auto-rebinds this from the vanilla default (LeftShift) on first launch (see callout below). |

For tablets and lenses, wheel-down past the first occupied slot turns the device OFF without changing the active index, and wheel-up from OFF turns it back on at the preserved index (not reset to slot 0).

### Vanilla camera-zoom key auto-rebind

Stationeers binds first/third-person camera zoom to `KeyMap.ThirdPersonControl`, which defaults to LeftShift. This conflicts with the LeftShift+scroll lens binding. On first launch, EquipmentPlus rebinds `ThirdPersonControl` from LeftShift to RightShift and saves the change to your settings file. **Hold the RIGHT shift, not the left, for camera zoom.** If you have already customized this binding to anything other than LeftShift, EquipmentPlus leaves it alone.

### Multiplayer-Safe Configuration Cartridge
Scroll wheel selects a line in the Config Cartridge display. Left-click (without Ctrl):
- **Read-only value**: copied to the clipboard
- **Writable value**: opens an input dialog to edit the value. Changes are properly synchronized through the server so they take effect for all players, fixing a longstanding multiplayer limitation of ImprovedConfiguration.

### Built-in Logic Slot Display (absorbed from Slot Configuration Cartridge)
The Config Cartridge screen also lists per-slot logic values (`Slot 0 / Occupant`, etc.). Writable slots render in green, read-only slots in grey. Scroll-select and click to copy or edit just like the main logic values.

### Persistent Active Sensor Chip
The currently-active sensor chip survives save/load and late-joining clients. Cycling on a client is routed through the server so every player sees the same active chip. Without this fix, the active chip is reconstructed as "whichever chip landed in a slot last," which with multiple chips becomes non-deterministic.

### Full Multiplayer Support
All features work correctly for every player. Matching mod versions are enforced during the connection handshake. State that vanilla leaves unsynchronised (active sensor chip, config cartridge writes) is explicitly synchronised by this mod.

## Compatibility

**Requires:** BepInEx + StationeersLaunchPad

**Incompatible with** (detected at startup; this mod refuses to load if any are found):
- [Better Advanced Tablet](https://steamcommunity.com/sharedfiles/filedetails/?id=3134483503) by Serialtasted, spacebuilder2020, Erik(NL), *replaced*
- [ImprovedConfiguration](https://steamcommunity.com/sharedfiles/filedetails/?id=3651839114) by Doggo, *replaced with multiplayer support*
- [Slot Configuration Cartridge](https://steamcommunity.com/sharedfiles/filedetails/?id=3578912665) by Otis B., *absorbed*
- [Better Headlamp](https://steamcommunity.com/sharedfiles/filedetails/?id=3616788907), *replaced by the Ctrl+LeftShift+scroll helmet beam binding*

These mods touch the same prefabs or UI paths. Disable any that are installed before enabling Equipment Plus. The conflict check runs after StationeersLaunchPad finishes loading all mods, so conflicts are detected even when StationeersLaunchPad bypasses BepInEx's own incompatibility system.

**Explicitly verified compatible (decompiled DLL + Harmony-attribute audit + IL cross-reference scan):**
- [ScriptedScreens](https://steamcommunity.com/sharedfiles/filedetails/?id=3666779631): All 63 `[HarmonyPatch]` attributes target `ProgrammableChipMotherboard`, `Motherboard`, `Circuitboard`, `Computer`, `ProgrammableChip`, UI-raycast/input classes, `GameManager`, `KeyWrap`, `SaveHelper`, `Thing.InteractWith`, and `Item.OnPowerTick`. Zero overlap with our patch surface. IL scan: zero uses of `NetworkUpdateFlag 0x4000`.
- [Advanced Computing](https://steamcommunity.com/sharedfiles/filedetails/?id=3465059322) by tom_is_unlucky: All 9 patch classes audited. `XmlSaveLoadPatch.AddToSavePrefix` only returns `false` for `ChipStackExtender` with a non-matching parent (our `SensorLenses` always passes through to vanilla). `SaveDataPatch.AddExtraTypes` is a Prefix that adds its own save types to the list and returns void, so our `SensorLensesSaveData` still registers. `PrefabPatch.Prefix` iterates only Advanced Computing's own prefabs (never touches `ItemSensorLenses`/`ItemAdvancedTablet`). `AutoAdapterPatch.TargetMethods` dynamically targets methods marked with its own attributes, which only exist in its own assembly. `LoadInNetworksPrefix` only handles chip-stack level ordering. Cross-reference scan: the only `BuildUpdate`/`SerializeOnJoin`/etc. references are on `Structure` and `LogicUnitBase` (the mod's own device types), never on `Item` or `SensorLenses`. IL scan: zero uses of `NetworkUpdateFlag 0x4000`. Adds a `SensorProcessingUnit (Data Network)` chip that drops into the dynamic lens slots without special casing.
- [MesonScannerMod](https://steamcommunity.com/sharedfiles/filedetails/?id=3163662089): All 5 patches target `SPUMesonScanner.Render/AddToBatch/RenderMeshes/CleanUp` and `InventoryManager.NormalMode`. Zero overlap. One read of `SensorLenses.Sensor` in its `NormalMode` Prefix is a harmless "is the active chip a Meson Scanner?" type check; this is exactly the sort of call our network sync makes *more* correct in multiplayer, not less. IL scan: zero uses of `NetworkUpdateFlag 0x4000`.

**Compatible in general:** Any mod that adds new sensor chips (Meson, Celestial, mod-added scanners) or new advanced-tablet cartridges. The dynamic slot system is type-agnostic and grows to fit whatever the game gives it. Mods that add logic types or logic slot types also work: the Config Cartridge screen renders any new values the game exposes.

## What This Mod Improves

### Compared to Better Advanced Tablet

| | Better Advanced Tablet | Equipment Plus |
|---|---|---|
| Extra slot count | Configurable, up to 8 total | Dynamically grows as you fill slots |
| UI clutter | All configured slots always visible | Exactly one empty slot always visible |
| Configuration | Slot count must match across players | No config needed |
| Works on sensor lenses | No | Yes |

**Why dynamic slots:** Better Advanced Tablet requires each player to manually pick a slot count, and all players on a server must match. Equipment Plus has no slot count setting; the UI only shows what's actually used, so there's nothing to mismatch. Reducing usage on an existing save can't orphan items because the data is always preserved.

### Compared to ImprovedConfiguration

| | ImprovedConfiguration | Equipment Plus |
|---|---|---|
| Scroll to select a line | Yes | Yes |
| Copy read-only values | Yes | Yes |
| Edit writable values | Yes (single-player only) | **Yes, fully multiplayer-compatible** |
| Cycling input | Ctrl + click on the device | Ctrl + scroll (tablet), LeftShift + scroll (lens), no modifier needed in the cartridge UI |

**The multiplayer fix:** In ImprovedConfiguration, editing a writable value from a client sets it only on that client's local copy. The server never finds out, and the change is silently overwritten by the next state sync. Equipment Plus routes all edits through the server's authoritative path, so writes from any player are applied correctly and visible to everyone.

### Compared to Slot Configuration Cartridge

| | Slot Configuration Cartridge | Equipment Plus |
|---|---|---|
| Shows per-slot logic values on Config Cartridge | Yes | Yes |
| Works on the advanced tablet | Yes | Yes |
| Edit writable slot values | Yes (single-player) | **Yes, fully multiplayer-compatible** |
| Required as a separate install | Yes | No (built in) |

**Multiplayer slot writes:** Vanilla provides `SetLogicFromClient` for `LogicType` writes but has no equivalent for `(LogicSlotType, slotIndex)` writes, so Slot Configuration Cartridge's slot edits silently no-op'd on remote clients. Equipment Plus ships a custom `SetLogicSlotFromClient` message: any client clicks a writable slot logic line, the server validates and applies, vanilla state-sync replicates the result to everyone.

### Active sensor persistence and sync

Vanilla Stationeers does not network `SensorLenses.Sensor` (the currently-selected sensor chip) in any delta update, join handshake, or save file. This was invisible in vanilla because lenses only held one chip at a time. With dynamic slots and Ctrl+click cycling, the active chip can diverge between peers and is reset by save/load. Equipment Plus adds explicit synchronisation for:

- **Live cycling**: client-side cycles are forwarded to the server via a custom `SetActiveSensorMessage`, validated, and broadcast to all clients via a dedicated `NetworkUpdateFlag` (0x4000).
- **Late-join**: the active chip's `ReferenceId` is appended to the lenses' join payload.
- **Save/load**: the active sensor and active cartridge ids are written to a side-car file (`equipmentplus-active-slots.xml`) inside the save ZIP. World.xml stays vanilla, so uninstalling Equipment Plus does not break the save (see "Uninstalling cleanly" below). Per-character helmet beam preferences ride in their own side-car (`equipmentplus-beam.xml`) the same way.

## Installation

1. Copy `EquipmentPlus.dll` and the `About/` folder into your Stationeers local mods directory
2. Disable Better Advanced Tablet, ImprovedConfiguration, Slot Configuration Cartridge, and Better Headlamp if any are installed
3. Restart the game

## Uninstalling cleanly

The save format is designed to survive uninstalling Equipment Plus. World.xml stays vanilla, and the mod's own data lives in side-car files inside the save ZIP (`equipmentplus-active-slots.xml`, `equipmentplus-beam.xml`) that vanilla silently ignores on load. Loading a save without Equipment Plus does **not** corrupt the world.

One limitation is unavoidable. Equipment Plus extends the Sensor Lenses (2 vanilla slots → 102) and Advanced Tablet (4 vanilla slots → 104) at runtime. If you've placed cartridges in tablet slots 5+ or chips in lens slots 3+ and then uninstall, vanilla's load path cannot place those extras back into slots that don't exist; it prints a red console error per item ("Slot N does not exist on thing Advanced Tablet ..."), and the items go missing from the device. Re-installing Equipment Plus restores them: the slot index and parent reference are still in the save, only the runtime slot extension was missing.

To uninstall cleanly: move any extras (more than 4 cartridges per tablet, more than 2 chips per lens) into your inventory or a storage container before disabling Equipment Plus.

## Credits

Equipment Plus would not exist without the modders who came before:

- **silentdeth, Serialtasted, spacebuilder2020, Erik(NL)**: Created the [Better Advanced Tablet](https://steamcommunity.com/sharedfiles/filedetails/?id=3134483503) lineage. The idea of extending tablet slots is theirs.
- **Doggo**: Created [ImprovedConfiguration](https://steamcommunity.com/sharedfiles/filedetails/?id=3651839114), the first mod to make the Config Cartridge write logic values rather than just read them. The scroll-to-select-line, highlight-with-color, and clipboard-copy patterns are theirs.
- **Otis B.**: Created [Slot Configuration Cartridge](https://steamcommunity.com/sharedfiles/filedetails/?id=3578912665), which established the "append per-slot logic values to the Config Cartridge display" UI now built into this mod.

All three mods are excellent. This one exists to combine them, unify the UX, fix the multiplayer write path, and keep the active sensor chip consistent across save/load and peers.


## Reporting Issues

If you run into a bug or something behaves unexpectedly, please open an issue on [GitHub](https://github.com/SixFive7/StationeersPlus/issues). Please include the mod name in the title so reports can be triaged. Steam comment notifications don't always come through, so GitHub is the reliable way to make sure a report is seen.

## Changelog

Version history lives in [`EquipmentPlus/About/About.xml`](EquipmentPlus/About/About.xml) under `<ChangeLog>`. Once the mod is published to the Steam Workshop, entries will also appear on the Workshop Change Notes tab with every release.

## License

Apache License 2.0. See [LICENSE](../../LICENSE) for the full text and [NOTICE](../../NOTICE) for attribution.
