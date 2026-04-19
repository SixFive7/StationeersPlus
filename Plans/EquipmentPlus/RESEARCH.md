# Equipment Plus: Internals

## Architecture overview

Equipment Plus is a BepInEx plugin loaded via StationeersLaunchPad (SLP). It uses Harmony to patch game classes at runtime. The mod targets two items: `SensorLenses` and `AdvancedTablet`. It also patches `ConfigCartridge` for the configuration cartridge enhancements.

All patches are applied in `Plugin.OnAllModsLoaded`, which fires from `Prefab.OnPrefabsLoaded` (after SLP finishes loading every mod). This timing lets the conflict detector scan all loaded assemblies before committing to patch.

The plugin registers two custom save-data types (`SensorLensesSaveData`, `EquipmentPlusTabletSaveData`) and one custom network message (`SetActiveSensorMessage`) through SLP's `Mod` API. Save types are also injected directly into `XmlSaveLoad.ExtraTypes` at startup to handle the case where the serializer was already cached before our plugin loaded.

## File walkthrough

### Plugin.cs
Entry point. `BepInPlugin`, `BepInDependency`, `BepInIncompatibility` attributes. `Awake` hooks `Prefab.OnPrefabsLoaded`. `OnAllModsLoaded` does:
1. Conflict detection (assembly name scan for Better Advanced Tablet, ImprovedConfiguration, Slot Configuration Cartridge).
2. Network setup (`Mod.Networking.Required = true`, registers `SetActiveSensorMessage`).
3. Save-data type registration (both via SLP's `Mod.AddSaveDataType` and directly into `XmlSaveLoad.ExtraTypes`).
4. `Harmony.PatchAll()`.
5. `SlotTypeIconPatch.RegisterMissingSensorIcon()`.

`RegisterSaveDataTypeLate` uses reflection to append to `XmlSaveLoad.ExtraTypes` and null out `Serializers._worldData` so the XmlSerializer is rebuilt on next access. Both steps are needed because SLP's `AddSaveDataType` only injects via a Prefix on `XmlSaveLoad.AddExtraTypes`, which may have already executed before our plugin loaded.

### PrefabPatch.cs
Mutates the `ItemSensorLenses` and `ItemAdvancedTablet` prefabs before any instances spawn. Adds 100 `Blocked` slots to each. Runs once, guarded by `_hasRun`. Triggered from three Harmony patches: `World.StartNewWorld`, `XmlSaveLoad.LoadWorld`, `NetworkClient.ProcessJoinData`. These cover new-game, load-game, and client-join paths.

### DynamicSlots.cs
Core slot management. Stateless except for two dictionaries:
- `_vanillaSlotCount`: records each prefab's original slot count so the mod never touches vanilla slots.
- `_templateSprite`: caches the editor-baked `SlotTypeIcon` sprite from the first vanilla slot of each type, used as fallback when `Slot.GetSlotTypeSprite` returns null at runtime.

`AddBlockedSlots(prefab, count, activeType)` clones presentation properties from an existing slot of the target type, adds `count` Blocked slots, and unlocks the first one.

`RefreshSlots(thing, activeType)` enforces the invariant: exactly one empty unlocked slot of `activeType` among the mod-added slots. Two passes:
1. Unlock any Blocked slot that holds an occupant (save-load recovery).
2. Count unlocked-empty slots. Grow (unlock Blocked) if count < 1. Shrink (re-block) if count > 1.

After any state change, `SyncTabletCartridgeSlots` rebuilds `AdvancedTablet.CartridgeSlots` (vanilla caches this list at Awake and never updates it). `RebuildInventoryWindowFor` forces `InventoryWindow.HandleOccupantChange` on any visible window showing this item, because the UI only creates `SlotDisplayButton` widgets for slots where `IsInteractable == true` at `SetSlots` time.

`RefreshSlotsNoRebuild` variant skips the UI rebuild for call sites already inside an InventoryWindow rebuild (prevents recursion).

### SensorLensesPatches.cs
Hooks `SensorLenses.OnChildEnterInventory` and `OnChildExitInventory` Postfix to call `DynamicSlots.RefreshSlots`.

Save/load patches:
- `SensorLensesSerializeSavePatch`: Postfix on `SensorLenses.SerializeSave` (inherited from Thing). Replaces the vanilla `DynamicThingSaveData` result with a `SensorLensesSaveData` that includes `ActiveSensorReferenceId`. Uses field-by-field reflection copy.
- `SensorLensesSaveLoadPatch`: Postfix on `SensorLenses.DeserializeSave`. Stashes the saved active-chip id into `ActiveSlotPersistence.PendingActiveSensor` for later application. Cannot set `lenses.Sensor` here because vanilla's `OnChildEnterInventory` fires for each chip as it's restored and overwrites `Sensor` with the last chip to enter.

Both patches use `TargetMethod()` with `Type.GetMethod` to resolve inherited methods (Harmony's `AccessTools.DeclaredMethod` only finds methods declared on the exact type). `__instance` is typed as `Thing` to avoid `InvalidCastException` when the patched base method fires for non-SensorLenses instances.

### AdvancedTabletPatches.cs
Mirrors `SensorLensesPatches.cs` for the tablet. Same `OnChildEnter/ExitInventory` Postfix pattern.

Save/load: `AdvancedTabletSerializeSavePatch` replaces vanilla's save data with `EquipmentPlusTabletSaveData` containing `ActiveCartridgeReferenceId`. `TabletSaveLoadPatch` stashes the saved id for `ActiveSlotPersistence`.

AdvancedTablet declares its own `SerializeSave`/`DeserializeSave`, so plain `[HarmonyPatch(typeof, nameof)]` works here (no `TargetMethod` needed).

### ActiveSlotPersistence.cs
Two static dictionaries (`PendingActiveSensor`, `PendingActiveCartridge`) keyed by `Thing.ReferenceId`. Populated at deserialize time, consumed at `OnFinishedLoad` time, then cleared.

`SensorLensesOnFinishedLoadPatch` and `AdvancedTabletOnFinishedLoadPatch`: Postfix on `OnFinishedLoad` (inherited from DynamicThing). By the time `OnFinishedLoad` fires, all child items have been restored to their slots, so it is safe to set `Sensor` or `Mode`. For the tablet, `GetCartridge()` is invoked via reflection to refresh the display.

### ClickCyclePatch.cs
Prefix on `InventoryManager.HandlePrimaryUse`. Fires on left mouse down + Ctrl held.

For lenses: `TryCycleSensor` builds a list of all chips in slots, finds the current one, advances to the next position. The cycle includes an "off" position (Sensor=null) that powers the lenses off. `ApplySensorChange` does an optimistic local write, then either sends `SetActiveSensorMessage` to host (if remote client) or sets `NetworkUpdateFlags |= 0x4000` (if server/SP).

For tablet: `TryCycleTablet` increments `Mode` modulo cartridge count, then calls `GetCartridge()` via reflection. `Mode` propagates through vanilla's `Interactable.State` network machinery, so no custom message is needed.

For worn lenses (empty hand + Ctrl+click): cycles the lenses in `GlassesSlot`. No OnOff gate because the cycle itself toggles power.

Returns `false` to suppress the rest of `HandlePrimaryUse` when a cycle fires.

### SensorLensesSyncPatches.cs
Custom `NetworkUpdateFlag` 0x4000 (unused by vanilla's Thing/DynamicThing/Item hierarchy which goes up to 0x0800).

Four Harmony patches on inherited methods of `SensorLenses`:
- `BuildUpdate` Postfix: when flag 0x4000 is set, writes `Sensor.ReferenceId` (or 0) to the binary stream.
- `ProcessUpdate` Postfix: reads the reference id and sets `lenses.Sensor`.
- `SerializeOnJoin` Postfix: appends `Sensor.ReferenceId` to the join payload.
- `DeserializeOnJoin` Postfix: reads it back.

All use `TargetMethod()` and type `__instance` as `Thing` (see note in SensorLensesPatches.cs).

### SetActiveSensorMessage.cs
`INetworkMessage` (SLP). Fields: `LensesReferenceId`, `SensorReferenceId`, `PowerOn`.

`Process(clientId)` runs on the server. Validates that the lenses exist, the sensor chip exists and is in one of the lenses' slots, then applies `Sensor` and `OnOff` and sets flag 0x4000 for broadcast.

### ConfigCartridgePatches.cs
Reimplements ImprovedConfiguration with multiplayer support.

`ConfigCartridgeState`: per-cartridge selected line index dictionary, highlight color constants, line separator string, regex for stripping TMP color markup.

`ConfigCartridgeScrollPatch`: Prefix on `Cartridge.OnScroll`. Must return `bool` and return `false` to suppress vanilla's free-scroll (which otherwise runs in parallel and desyncs the viewport from the selected line). After clamping the selected index, drives the viewport explicitly with `_scrollPanel.SetScrollPosition((float)selected / (lineCount - 1))` (reflected, `_scrollPanel` lives on the `Cartridge` base class). Sign convention: `(scrollDelta.y > 0f) ? -1 : 1` so wheel-up decreases the index.

`ConfigCartridgeScreenPatch`: Postfix on `ConfigCartridge.OnScreenUpdate`. Highlights the selected line with yellow TMP markup. On left-click (without Ctrl):
- If the line is a slot-logic line (tracked by `ConfigCartridgeSlotDisplayPatch.SlotLines`), routes to slot read/write.
- Otherwise parses `LogicType` from the line text ("TypeName ... value" format).
- Writable values: opens `InputWindow.ShowInputPanel`, on submit calls `WriteLogicValue`.
- Read-only values: copies to clipboard via `GUIUtility.systemCopyBuffer`.

`WriteLogicValue`: on server, calls `Device.SetLogicValue` directly. On client, sends `SetLogicFromClient` to server (vanilla network message).

`WriteLogicSlotValue`: on server, calls `Device.SetLogicValue(LogicSlotType, slotIndex, value)` directly. On client, logs a warning. Vanilla has no `SetLogicSlotFromClient` message, so remote-client slot writes are not possible without a custom message.

### ConfigCartridgeSlotDisplay.cs
Postfix on `ConfigCartridge.ReadLogicText`. Appends per-slot logic values to the output text. Format matches the original Slot Configuration Cartridge mod: yellow slot headers, grey for writable values, green for read-only. Uses `Monitor.Enter/Exit` on the scanned device for thread safety (same pattern as the original mod).

Maintains `SlotLines` dictionary mapping absolute line indices to `SlotLineInfo` structs so `ConfigCartridgeScreenPatch` can route clicks to the slot write path.

### WindowOpenRefreshPatch.cs
Postfix on `InventoryWindow.ToggleVisibility`. When the window becomes visible, calls `DynamicSlots.RefreshSlotsNoRebuild` then `HandleOccupantChange` via reflection. Fixes stale widget state when slots changed while the window was closed (e.g. items inserted via another mod or hotkey while the inventory panel was hidden).

### SlotTypeIconPatch.cs
Called once from `Plugin.OnAllModsLoaded`. Vanilla's `Slot.PopulateSlotTypeSprites` loads sprites from `Resources/UI/SlotTypes` but has no `sloticon-sensorprocessingunit` asset, so SPU slots render without a hint icon. This patch injects into `Slot._slotTypeLookup` directly: first tries any sprite with "sensor" in its name, then falls back to the Cartridge icon.

Cannot use a Harmony Postfix on `PopulateSlotTypeSprites` because that method runs at `InventoryManager.ManagerAwake`, which fires before any BepInEx plugin patches are installed.

### SensorLensesSaveData.cs
`public class SensorLensesSaveData : DynamicThingSaveData` with one extra field: `ActiveSensorReferenceId`.

### AdvancedTabletSaveData.cs
`public class EquipmentPlusTabletSaveData : VanillaTabletSaveData` (aliased from `Assets.Scripts.Objects.Items.AdvancedTabletSaveData`) with one extra field: `ActiveCartridgeReferenceId`. Named differently from vanilla to avoid XML type-name collision in `XmlSaveLoad.ExtraTypes`.

### ReflectionUtils.cs
Single helper method `TryGetField<T>` for accessing private fields by name. Used to reach `ConfigCartridge._displayTextMesh`.

### Properties/AssemblyInfo.cs
Standard assembly metadata.

## Patch catalog

| Patch class | Target | Method | Type | Purpose |
|---|---|---|---|---|
| StartNewWorldPatch | World | StartNewWorld | Prefix | Trigger prefab mutation |
| LoadWorldPatch | XmlSaveLoad | LoadWorld | Prefix | Trigger prefab mutation |
| ProcessJoinDataPatch | NetworkClient | ProcessJoinData | Prefix | Trigger prefab mutation |
| SensorLensesSlotOnInsert | SensorLenses | OnChildEnterInventory | Postfix | RefreshSlots on chip insert |
| SensorLensesSlotOnRemove | SensorLenses | OnChildExitInventory | Postfix | RefreshSlots on chip remove |
| TabletSlotOnInsert | AdvancedTablet (inherited) | OnChildEnterInventory | Postfix | RefreshSlots on cartridge insert |
| TabletSlotOnRemove | AdvancedTablet (inherited) | OnChildExitInventory | Postfix | RefreshSlots on cartridge remove |
| ClickCyclePatch | InventoryManager | HandlePrimaryUse | Prefix | Ctrl+click cycling |
| SensorLensesSerializeSavePatch | SensorLenses (inherited) | SerializeSave | Postfix | Inject ActiveSensorReferenceId |
| SensorLensesSaveLoadPatch | SensorLenses (inherited) | DeserializeSave | Postfix | Stash saved active chip id |
| AdvancedTabletSerializeSavePatch | AdvancedTablet | SerializeSave | Postfix | Inject ActiveCartridgeReferenceId |
| TabletSaveLoadPatch | AdvancedTablet | DeserializeSave | Postfix | Stash saved active cartridge id |
| SensorLensesOnFinishedLoadPatch | SensorLenses (inherited) | OnFinishedLoad | Postfix | Restore active sensor after children loaded |
| AdvancedTabletOnFinishedLoadPatch | AdvancedTablet (inherited) | OnFinishedLoad | Postfix | Restore active cartridge after children loaded |
| SensorLensesBuildUpdatePatch | SensorLenses (inherited) | BuildUpdate | Postfix | Write active sensor to delta stream |
| SensorLensesProcessUpdatePatch | SensorLenses (inherited) | ProcessUpdate | Postfix | Read active sensor from delta stream |
| SensorLensesSerializeOnJoinPatch | SensorLenses (inherited) | SerializeOnJoin | Postfix | Write active sensor to join payload |
| SensorLensesDeserializeOnJoinPatch | SensorLenses (inherited) | DeserializeOnJoin | Postfix | Read active sensor from join payload |
| ConfigCartridgeScrollPatch | Cartridge | OnScroll | Prefix | Scroll-select line |
| ConfigCartridgeScreenPatch | ConfigCartridge | OnScreenUpdate | Postfix | Highlight line, handle click |
| ConfigCartridgeSlotDisplayPatch | ConfigCartridge | ReadLogicText | Postfix | Append slot logic values |
| ConfigCartridgeDestroyPatch | Cartridge | OnDestroy | Prefix | Clean up selected-index entry |
| ConfigCartridgeSlotDisplayDestroyPatch | Cartridge | OnDestroy | Prefix | Clean up slot-line registry |
| WindowOpenRefreshPatch | InventoryWindow | ToggleVisibility | Postfix | Rebuild widgets on window open |

## Decompiled game internals

### SensorLenses
- Inherits from Item (via DynamicThing).
- `Sensor` property: the currently-active `SensorProcessingUnit`. Set in `OnChildEnterInventory` to whatever chip enters. Not networked (no delta update, no join payload, no save).
- `OnOff`: power toggle. Networked through `Interactable.State`.
- Vanilla ships with 2 SPU slots. Only one chip is expected; the `Sensor` property has no cycling logic.

### AdvancedTablet
- Inherits from Item (via DynamicThing).
- `CartridgeSlots`: `List<Slot>` cached at Awake by scanning `Slots` for `Type == Cartridge`. Never rebuilt after Awake.
- `Mode`: index into `CartridgeSlots`. Propagated via `Interactable.State`. Not saved.
- `Cartridge`: property returning the occupant of `CartridgeSlots[Mode]`.
- `GetCartridge()`: private method that refreshes the cartridge display. Called by vanilla's Next/Prev InteractWith but not by `Mode` setter.
- Vanilla ships with Battery, ProgrammableChip, and 2 Cartridge slots.

### NetworkUpdateFlags
16-bit bitmask. Values through 0x0800 are used by Thing/DynamicThing/Item for standard state (position, rotation, damage, color, access, etc.). We use 0x4000 for active-sensor sync. `BuildUpdate` and `ProcessUpdate` are called by the network layer; each flag bit causes the corresponding data block to be written/read.

### XmlSaveLoad.ExtraTypes
Static `Type[]` field. Appended via `XmlSaveLoad.AddExtraTypes` at game startup. Used by the XmlSerializer to recognize subclasses in save files. If a type isn't registered, deserialization silently drops the extra fields.

`Serializers._worldData` caches the constructed XmlSerializer. Nulling it forces reconstruction on next access with the updated type list.

### SetLogicFromClient
Vanilla network message. Fields: `LogicId` (device ReferenceId), `LogicType`, `Value`. Server-side handler calls `Device.SetLogicValue`. No equivalent exists for `(LogicSlotType, slotIndex)` writes.

### Cartridge / ConfigCartridge screen plumbing
- `Cartridge._scrollPanel` (protected, serialized) is the only scroll state container. No `_scrollOffset`, `_firstLine`, or `_topIndex` exists on Cartridge or ConfigCartridge. Scroll position is a single normalized `float` in `[0,1]` inside `ScrollPanel._scrollPosition` / `_scrollActualPosition`.
- `Cartridge.OnScroll(Vector2 scrollDelta)` is a one-line forwarder: `if (_scrollPanel && scrollDelta != Vector2.zero) _scrollPanel.OnScroll(scrollDelta);`. That's the ONLY scroll behavior vanilla exposes.
- `ScrollPanel.OnScroll` flips the sign (`y *= -1f`), multiplies by `_effectiveSensitivity`, and adds to `_scrollPosition`. Smooth pixel-based scroll, not line-based. No concept of a selected line in vanilla.
- `ScrollPanel.SetScrollPosition(float)` snaps `_scrollPosition = _scrollActualPosition = clamp01(position)` and calls `RefreshPosition()`. Instantaneous, no lerp.
- `ConfigCartridge.OnScreenUpdate` unconditionally writes `_displayTextMesh.text = _outputText` every call, then sets `_scrollPanel.SetContentHeight(_displayTextMesh.preferredHeight)`. Any mod-side edit of the mesh text is wiped every frame and must be re-applied in a postfix.
- `Cartridge.UpdateEachFrame` calls `OnScreenUpdate` every frame for every cartridge in the tablet (not just the currently-displayed one), gated by `!IsOccluded && OnOff && Powered && (InPlayerHand || InUpdateRange)`. The `tablet.Cartridge == __instance` guard in click handling is load-bearing because the postfix fires for all cartridges.
- `ConfigCartridge._outputText` is built in `ReadLogicText` (called from `OnMainTick`, slower cadence than `OnScreenUpdate`). Format: line 0 is `ReferenceId ... $HEXID`, then one line per readable `LogicType` emitted as `{Name} ... <color=grey|green>{value}</color>`. Writable uses grey, read-only uses green, `ReferenceId` uses `#20B2AA`. Separator is a literal 5-character `" ... "` (space, three dots, space).
- `ConfigCartridge.ScannedDevice` is computed live from `CursorManager.CursorThing as Device` with an `IsMasterAuthority` gate. The cursor must still be pointing at the device at the instant of the click, or `ScannedDevice` is null and the click path bails.

### Upstream ImprovedConfiguration reference behavior
ImprovedConfiguration (Workshop 3651839114, `com.doggo.improved_configuration`, SP-only) is the baseline EquipmentPlus reimplements. Verbatim behavior:

- `Cartridge.OnScroll` is patched as a **Prefix that returns `bool`**. Returns `false` to suppress vanilla's free-scroll when a `ConfigCartridge` is the target. Does sign: `int num2 = (scrollDelta.y > 0f) ? -1 : 1` (wheel-up decreases index / moves selection up the list).
- After updating the selected index, the prefix explicitly calls `_scrollPanel.SetScrollPosition((float)selected / (count - 1))` (reflected, `_scrollPanel` lives on `Cartridge`). This drives the viewport so the selected line stays roughly in view.
- `ConfigCartridge.OnScreenUpdate` postfix re-applies `SetScrollPosition` every frame (same formula), re-wraps `lines[selected]` in `<color=#FFD561FF>...</color>` yellow, and handles the click.
- Click detection uses Unity's `Input.GetMouseButtonDown(0)` — NOT the game's `KeyManager`. No modifier gate in the upstream (plain left-click is the only click it responds to).
- Click validation chain: `tablet != null`, `tablet.ParentSlot != null`, `tablet.ParentSlot == InventoryManager.ActiveHandSlot`, `tablet.Cartridge == __instance`, `device = __instance.ScannedDevice != null`.
- Line parsing: strips `<color=...>` and `</color>` tags with regex `<color=[^>]*>|</color>`, splits on `" ... "`, takes `parts[0].Trim()` as the `LogicType` name, `Enum.TryParse<LogicType>`. No fallback for non-numeric values (`double.TryParse` only).
- Writable: `InputWindow.ShowInputPanel(typeName, currentValue.ToString(), Action<string,string> callback, 600)` with `maxLength = 600`. Callback signature requires TWO string parameters; the second is unused by upstream.
- Read-only: direct `GUIUtility.systemCopyBuffer = value.ToString()`. No `TextEditor`, no `OS` clipboard API.
- Selected-index dictionary is `static Dictionary<ConfigCartridge, int>`, keyed by instance (not `ReferenceId`). Cleaned up in a `Cartridge.OnDestroy` prefix. Not persisted to save files; resets to 0 on load.
- Write path calls `Device.SetLogicValue(LogicType, double)` directly on the client. No network routing. On a remote MP client the write is applied locally and overwritten on next sync — upstream is effectively host-only. EquipmentPlus adds `SetLogicFromClient` routing to fix this.

### InventoryManager.HandlePrimaryUse
Called every frame from input processing. Sequence for a held item:
1. If `CursorManager.CursorThing` is targetable and the held item supports `AttackWith` on it, call `AttackWith` (item-on-thing interaction).
2. Otherwise call `UseItemOnSelf(item)`, which early-returns when `item.AllowSelfUse == false`.
3. For `AllowSelfUse == true` items, `UseItemOnSelf` dispatches to `item.OnUsePrimary()`.

`AdvancedTablet.AllowSelfUse == false`, so patching `Item.OnUsePrimary` never fires for a plain tablet. `InventoryManager.HandlePrimaryUse` is the correct hook for tablet-level click intercepts because it runs regardless of `AllowSelfUse`. This is why the pre-refactor `CartridgeCyclePatch` on `Item.OnUsePrimary` never worked in practice, and why the current `ClickCyclePatch` targets `InventoryManager.HandlePrimaryUse`.

`KeyManager.GetMouseDown("Primary")` is a thin forwarder to `Input.GetKeyDown(KeyMap.PrimaryAction)` — frame-stable, idempotent across multiple reads within a single Update tick. Multiple patches can read it in the same frame without interference.

## Multiplayer protocol

### Active sensor sync (custom)
- Client cycles sensor: optimistic local write, then sends `SetActiveSensorMessage` to host.
- Server receives: validates lenses exist and chip is in a slot, applies `Sensor` and `OnOff`, sets flag 0x4000.
- Next `BuildUpdate`: flag 0x4000 causes `Sensor.ReferenceId` to be written to the delta stream. All clients read it in `ProcessUpdate`.
- Late-join: `SerializeOnJoin` appends `Sensor.ReferenceId`; `DeserializeOnJoin` reads it.

### Active cartridge sync (vanilla)
- `Mode` propagates through `Interactable.State` (vanilla networking). No custom message needed.
- `GetCartridge()` must be called manually after setting `Mode` because vanilla's setter does not call it.

### Config cartridge writes (vanilla message, mod routing)
- `WriteLogicValue` on client sends `SetLogicFromClient` to server. Server applies authoritatively.
- `WriteLogicSlotValue` on server calls `Device.SetLogicValue(LogicSlotType, slotIndex, value)` directly. On client, logs a warning (no vanilla message for slot writes).

## Pitfalls

### Harmony __instance typing on inherited methods
When `TargetMethod()` returns an inherited `MethodInfo`, Harmony patches the base class method and the patch fires for every subclass instance. Declaring `__instance` as the concrete type (e.g. `SensorLenses`) causes Harmony to emit a `castclass` instruction that throws `InvalidCastException` for any other Thing subclass. Always use `Thing __instance` and filter with `is`.

### CartridgeSlots cache staleness
`AdvancedTablet.CartridgeSlots` is built once at Awake. Runtime slot-type changes (Blocked to Cartridge or vice versa) do not update the cache. Vanilla's `GetCartridge`, `Next`, `Prev`, and `InteractWith` all iterate the cache. Must rebuild it explicitly after every slot state change (`DynamicSlots.SyncTabletCartridgeSlots`).

### InventoryWindow widget staleness
`InventoryWindow.SetSlots` only instantiates `SlotDisplayButton` widgets for slots where `IsInteractable == true` at the time it runs. Later changes to `IsInteractable` do not create or remove widgets. Must force `HandleOccupantChange` after any slot state change for the widget list to match.

When the window is closed (GameObject inactive), `FindObjectsOfType<InventoryWindow>()` skips it, so rebuilds during close are silently lost. `WindowOpenRefreshPatch` catches the re-open and forces a rebuild.

### Save-load ordering
Vanilla restores children (chips/cartridges) one at a time AFTER the parent's `DeserializeSave`. Each child's `MoveToSlot` fires `OnChildEnterInventory`, which for lenses unconditionally sets `Sensor = child`. The last chip to enter wins, overwriting any value set during deserialization. Active-slot restoration must wait for `OnFinishedLoad`, which fires after all children are placed.

### XmlSaveLoad.ExtraTypes timing
SLP's `Mod.AddSaveDataType` injects via a Prefix on `XmlSaveLoad.AddExtraTypes`. If that method already ran before our plugin loaded, the prefix never fires for our types. Direct injection into the `ExtraTypes` field plus nulling `Serializers._worldData` covers this race.

### SlotTypeIcon for SensorProcessingUnit
Vanilla has no `sloticon-sensorprocessingunit` sprite in `Resources/UI/SlotTypes`. `Slot.PopulateSlotTypeSprites` runs at `InventoryManager.ManagerAwake` (before plugins load), so a Harmony Postfix on it never fires. The icon must be injected directly into `Slot._slotTypeLookup` at plugin load time.

### Cartridge.OnScroll prefix must return bool and suppress vanilla
If the scroll prefix is a `void` method (not returning `bool`), vanilla's `_scrollPanel.OnScroll(scrollDelta)` still runs after the prefix, so the viewport pans smoothly based on pixel sensitivity while the mod's selected-line index also advances. The two states desync: the highlighted line drifts away from what's visible. The upstream mod returns `bool` from the prefix and returns `false` after advancing the selected index, blocking vanilla's free-scroll entirely. The mod then drives the viewport explicitly via `_scrollPanel.SetScrollPosition((float)selected / (count - 1))`.

### Scroll sign convention must match upstream
Upstream uses `int delta = (scrollDelta.y > 0f) ? -1 : 1` (wheel-up decreases index, matching the intuitive "wheel up moves selection up the list"). Inverting this puts selection on the opposite of the user's wheel direction and is the dominant visible symptom when paired with the "vanilla scroll still runs" bug.

### Ctrl modifier parity between click-cycle and click-edit
`ClickCyclePatch` checks both `KeyCode.LeftControl` and `KeyCode.RightControl` via `KeyManager.GetButton`. A click-edit path that only checks `Input.GetKey(KeyCode.LeftControl)` will treat `RightCtrl+click` as a plain click and fire the input window alongside the cycle. Both paths must check the same key set and (preferably) use the same input layer so remapped keybindings stay consistent.

### `_displayTextMesh.text` is overwritten every frame
`ConfigCartridge.OnScreenUpdate` unconditionally assigns `_displayTextMesh.text = _outputText` at the top of the method. Any mod-side mutation of the mesh text (e.g. wrapping a line in highlight markup) must be re-applied in a postfix every frame. Text-equality guard (`if (textMesh.text != newText) textMesh.text = newText;`) is worth keeping to avoid redundant TMP mesh rebuilds.

### `ReadLogicText` slot-line index math
`ConfigCartridgeSlotDisplay.Postfix` reads `____outputText` BEFORE appending (so `baseLineIndex = ____outputText.Split('\n').Length` counts vanilla lines only), then increments `appendedLines` for each `\n`-prefixed line written. The slot-header occupies `baseLineIndex + appendedLines` at the moment of its append (header index is NOT stored); the first slot-logic line occupies the NEXT index and IS stored in `SlotLines[cc][index]`. Because the postfix appends to `____outputText` directly and `OnScreenUpdate` then writes that same text to the mesh, `textMesh.text.Split('\n')` at click time indexes the same lines. The highlight wrap does not insert or remove `\n`, so indices stay aligned across the scroll-selection / click path.

## Design decisions

### Why 100 extra blocked slots instead of a configurable count
Blocked slots cost almost nothing (no UI widget, no network traffic, no save-data entry). Capping at 100 avoids any config file and eliminates the server/client mismatch problem that Better Advanced Tablet had. No player will realistically insert 100 chips.

### Why Ctrl+click instead of plain click
Plain click on held items fires vanilla's `AttackWith` path and collides with other mods that hook clicks (ImprovedConfiguration used plain click for Config Cartridge interaction). Requiring Ctrl makes the mod's cycling orthogonal to every other click handler.

### Why no custom message for slot writes
Vanilla's `SetLogicFromClient` covers `LogicType` writes. No equivalent exists for `(LogicSlotType, slotIndex)` writes. Implementing a custom message for this would require registering it through SLP, handling server-side validation for slot-write permissions, and testing interactions with every device type that exposes writable slot logic. The original Slot Configuration Cartridge mod was single-player only; accepting the same limitation for remote clients (while supporting server/listen-server hosts) is a pragmatic tradeoff. A custom message can be added later if demand warrants it.

### Why EquipmentPlusTabletSaveData inherits from vanilla AdvancedTabletSaveData
Vanilla's `AdvancedTablet.DeserializeSave` does an `isinst` check for its own `AdvancedTabletSaveData`. By inheriting from it, our subclass passes that check and any fields vanilla adds in future updates are preserved automatically. The class is named differently to avoid XML type-name collision when both are registered in `ExtraTypes`.
