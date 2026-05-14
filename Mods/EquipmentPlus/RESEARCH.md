# Equipment Plus (Plans): Research Reference

Equipment Plus is an in-progress mod that extends `SensorLenses` and `AdvancedTablet` with 100 extra dynamic slots each, persists the active chip / cartridge across save-load and multiplayer, adds Ctrl+click cycling to both, and absorbs ImprovedConfiguration / Slot Configuration Cartridge into a single multiplayer-aware `ConfigCartridge` reimplementation. BepInEx + StationeersLaunchPad, server-authoritative with client-side UI and custom message routing. First-time readers: architecture and plugin wiring live in Section 1; the patch catalog is Section 3; game internals the mod depends on (SensorLenses / AdvancedTablet / Cartridge / InventoryManager / InventoryWindow class data, inherited-method patching, save-load ordering, save-data registration) live on the central pages pointed to from Section 5.

## Status

In-progress. Current scope:

- SensorLenses 100-slot expansion with persisted active sensor (custom flag 0x4000 + `SetActiveSensorMessage`).
- AdvancedTablet 100-slot expansion with persisted active cartridge (`Mode` rides vanilla `Interactable.State`).
- Ctrl+click cycling on both via `InventoryManager.HandlePrimaryUse`.
- ConfigCartridge reimplementation with multiplayer-safe `SetLogicFromClient` routing (absorbs ImprovedConfiguration).
- Slot Configuration Cartridge behavior absorbed into `ConfigCartridgeSlotDisplay`.
- `SlotTypeIcon` fallback for `SensorProcessingUnit` slots via direct `Slot._slotTypeLookup` injection.
- `WindowOpenRefreshPatch` for stale-widget recovery when the inventory window was closed during slot mutation.

Not yet graduated to `Mods/`. No Workshop handle.

## 1. Architecture

Mod identity:

| Field | Value |
|---|---|
| Display Name | Equipment Plus |
| Code Name | EquipmentPlus |
| Plugin GUID | net.sixfive7.equipmentplus |
| Workshop ID | (unreleased) |
| Dependencies | StationeersLaunchPad, LaunchPadBooster (networking + save-data) |

Targets two items, `SensorLenses` and `AdvancedTablet`, and the `ConfigCartridge` screen. All patches apply in `Plugin.OnAllModsLoaded`, which fires from `Prefab.OnPrefabsLoaded` after StationeersLaunchPad finishes loading every mod. This timing lets the conflict detector scan all loaded assemblies before committing to patch.

### 1.1. Plugin wiring

`Plugin.cs` startup sequence:

1. Conflict detection by assembly-name scan (Better Advanced Tablet, ImprovedConfiguration, Slot Configuration Cartridge). These are absorbed, not integrated, so loading alongside them is blocked.
2. Network setup: `Mod.Networking.Required = true`, register `SetActiveSensorMessage`.
3. Save-data type registration, dual path: LaunchPadBooster's `Mod.AddSaveDataType` (normal case) AND direct injection into `XmlSaveLoad.ExtraTypes` with `Serializers._worldData` nulled (race-safe case). The direct path covers a race where `XmlSaveLoad.AddExtraTypes` already executed before our plugin loaded. See the central `SaveDataRegistration.md` pointer in Section 5.
4. `Harmony.PatchAll()`.
5. `SlotTypeIconPatch.RegisterMissingSensorIcon()` (direct `Slot._slotTypeLookup` injection; see central `SlotTypeIconLookup.md`).

Two custom save-data types register: `SensorLensesSaveData` (adds `ActiveSensorReferenceId`) and `EquipmentPlusTabletSaveData` (adds `ActiveCartridgeReferenceId`, inherits vanilla `AdvancedTabletSaveData` so vanilla's `isinst` check in `DeserializeSave` still matches; see central `SaveDataIsinstInheritance.md`). One custom network message registers: `SetActiveSensorMessage`.

Conflicting mods (absorbed):

- **Better Advanced Tablet** — slot expansion. Absorbed; we use 100 fixed blocked slots instead of a configurable count to avoid the server/client mismatch Better Advanced Tablet hit.
- **ImprovedConfiguration** — ConfigCartridge scroll-select + click-edit. Absorbed; we add multiplayer routing via `SetLogicFromClient`.
- **Slot Configuration Cartridge** — per-slot logic readout on ConfigCartridge. Absorbed into `ConfigCartridgeSlotDisplay`.

### 1.2. Threading model

No off-main-thread work. All mutations run on Unity's main thread. `ConfigCartridgeSlotDisplay` uses `Monitor.Enter/Exit` around scanned-device reads to match the original Slot Configuration Cartridge mod's thread-safety pattern, even though in practice `ReadLogicText` is called on the main thread.

### 1.3. Server / client roles

Server-authoritative for slot state, active-sensor writes, active-cartridge writes, and logic-value writes. Client-side for UI rebuild, scroll-select highlight, and optimistic local writes before round-trip. Single-player runs under `NetworkRole.None`; the client-vs-server branches all use the same `IsServer` check that works in that role. See central `SinglePlayerNetworkRole.md`.

Per-feature role split:

- **Slot expansion** — prefab mutation runs in `StartNewWorld`, `LoadWorld`, and `ProcessJoinData` (server + client cover). `RefreshSlots` runs everywhere.
- **Active sensor** — cycle issues optimistic local write + `SetActiveSensorMessage` to host; host validates + applies + sets flag 0x4000 for broadcast.
- **Active cartridge** — rides vanilla `Interactable.State` sync of `Mode`; no custom message.
- **ConfigCartridge writes** — server calls `Device.SetLogicValue` directly; client sends `SetLogicFromClient` (vanilla). Slot writes are server-only, as vanilla has no `SetLogicSlotFromClient`.

### 1.4. File walkthrough

- **Plugin.cs** — BepInEx entry point, conflict detection, network + save-data registration, PatchAll, icon injection.
- **PrefabPatch.cs** — Adds 100 `Blocked` slots to `ItemSensorLenses` and `ItemAdvancedTablet` prefabs once, guarded by `_hasRun`. Triggered from three Harmony patches (`World.StartNewWorld`, `XmlSaveLoad.LoadWorld`, `NetworkClient.ProcessJoinData`) to cover new-game, load-game, and client-join paths.
- **DynamicSlots.cs** — Core slot management. Stateless except for two dictionaries: `_vanillaSlotCount` (records each prefab's original slot count so we never touch vanilla slots) and `_templateSprite` (caches editor-baked `SlotTypeIcon` sprites). `AddBlockedSlots(prefab, count, activeType)` clones presentation properties from an existing vanilla slot and adds `count` Blocked slots. `RefreshSlots(thing, activeType)` enforces the invariant "exactly one empty unlocked slot of `activeType` among mod-added slots": unlock blocked slots holding occupants (save-load recovery), then grow or shrink unlocked-empty count to 1. After any state change, `SyncTabletCartridgeSlots` rebuilds the `AdvancedTablet.CartridgeSlots` cache (vanilla builds this once at Awake and never updates it); `RebuildInventoryWindowFor` forces `HandleOccupantChange` on any visible inventory window showing the item. `RefreshSlotsNoRebuild` variant skips the UI rebuild for call sites already inside an InventoryWindow rebuild (prevents recursion).
- **SensorLensesPatches.cs** — `OnChildEnterInventory` / `OnChildExitInventory` Postfixes call `DynamicSlots.RefreshSlots`. `SerializeSave` / `DeserializeSave` Postfixes (on inherited methods resolved via `TargetMethod()` + `Type.GetMethod`) replace vanilla's `DynamicThingSaveData` with `SensorLensesSaveData` carrying `ActiveSensorReferenceId`, and stash the saved id for later restoration in `OnFinishedLoad`. `__instance` is typed as `Thing` across all inherited-method patches.
- **AdvancedTabletPatches.cs** — Mirror of SensorLensesPatches for the tablet. `SerializeSave` / `DeserializeSave` are declared directly on `AdvancedTablet`, so no `TargetMethod()` is needed for those two; the slot patches still use inherited-method targeting.
- **ActiveSlotPersistence.cs** — Two static dictionaries (`PendingActiveSensor`, `PendingActiveCartridge`) keyed by `Thing.ReferenceId`. Populated at deserialize time, consumed in `OnFinishedLoad`, then cleared. Restoration deferred to `OnFinishedLoad` because vanilla's child-restore path fires `OnChildEnterInventory` for each chip / cartridge as it's re-placed, which unconditionally overwrites `SensorLenses.Sensor` or `AdvancedTablet.Mode` — the last child to enter wins. Only after all children are placed is it safe to set the active one.
- **ClickCyclePatch.cs** — Prefix on `InventoryManager.HandlePrimaryUse` (not `Item.OnUsePrimary`; `AdvancedTablet.AllowSelfUse == false`, so `OnUsePrimary` never fires). Fires on left-mouse-down + Ctrl held. For lenses: `TryCycleSensor` cycles through chips-in-slots plus an "off" position (Sensor=null), optimistic local write, then `SetActiveSensorMessage` to host (remote client) or `NetworkUpdateFlags |= 0x4000` (server / single-player). For tablet: `TryCycleTablet` increments `Mode` mod cartridge count, calls private `GetCartridge()` via reflection to refresh the display. `Mode` propagates via vanilla `Interactable.State`. For worn lenses (empty hand + Ctrl+click in `GlassesSlot`): same cycle; no OnOff gate because the cycle itself toggles power. Returns `false` to suppress the rest of `HandlePrimaryUse`.
- **SensorLensesSyncPatches.cs** — Custom `NetworkUpdateFlag` 0x4000 (vanilla Thing/DynamicThing/Item use up to 0x0800). Four Postfix patches on inherited methods of `SensorLenses`: `BuildUpdate` (writes `Sensor.ReferenceId` or 0 when flag set), `ProcessUpdate` (reads and assigns), `SerializeOnJoin` (append to join payload), `DeserializeOnJoin` (read back).
- **SetActiveSensorMessage.cs** — `INetworkMessage`. Fields: `LensesReferenceId`, `SensorReferenceId`, `PowerOn`. `Process(clientId)` on server validates that the lenses exist, the chip exists and is in one of the lenses' slots, then applies `Sensor` and `OnOff` and sets flag 0x4000 for broadcast.
- **ConfigCartridgePatches.cs** — Reimplements ImprovedConfiguration with multiplayer. `ConfigCartridgeState` (per-cartridge selected-line dictionary, highlight-color constants, line-separator, TMP-markup-strip regex). `ConfigCartridgeScrollPatch` is a `bool`-returning Prefix on `Cartridge.OnScroll` that returns `false` to suppress vanilla's free-scroll; after clamping the selected index it drives the viewport via reflected `_scrollPanel.SetScrollPosition`. Sign convention `(scrollDelta.y > 0f) ? -1 : 1` matches upstream (wheel-up decreases index). `ConfigCartridgeScreenPatch` Postfix on `OnScreenUpdate` re-applies yellow-highlight markup every frame (because `_displayTextMesh.text` is re-assigned every frame) and handles left-click: slot-logic line routes to slot read/write; other lines parse `LogicType` and open `InputWindow.ShowInputPanel` (writable) or copy to clipboard (read-only). `WriteLogicValue` on server calls `Device.SetLogicValue` directly; on client sends `SetLogicFromClient` vanilla message. `WriteLogicSlotValue` on server calls `Device.SetLogicValue(LogicSlotType, slotIndex, value)` directly; on client logs a warning (no vanilla `SetLogicSlotFromClient` exists).
- **ConfigCartridgeSlotDisplay.cs** — Postfix on `ConfigCartridge.ReadLogicText` appending per-slot logic values. Format matches the original Slot Configuration Cartridge: yellow slot headers, grey writable, green read-only. Maintains `SlotLines` dictionary mapping absolute line indices to `SlotLineInfo` structs so the screen patch can route clicks to the slot-write path. Index math: read `____outputText.Split('\n').Length` BEFORE appending as `baseLineIndex`, increment `appendedLines` per `\n`-prefixed line, slot header at `base+appended` (not stored), first slot-logic line at the NEXT index (stored). Highlight wrap does not insert or remove `\n`, so click-time `textMesh.text.Split('\n')` indexing stays aligned.
- **WindowOpenRefreshPatch.cs** — Postfix on `InventoryWindow.ToggleVisibility`. When the window becomes visible, calls `RefreshSlotsNoRebuild` then `HandleOccupantChange` via reflection. Fixes stale widgets when slots changed while the window was closed.
- **SlotTypeIconPatch.cs** — Called once from `OnAllModsLoaded`. Injects a "sensor" sprite (any sprite containing "sensor" in its name, falling back to the Cartridge icon) directly into `Slot._slotTypeLookup` for `SensorProcessingUnit`. Cannot use a Postfix on `Slot.PopulateSlotTypeSprites` because that method runs at `InventoryManager.ManagerAwake`, before any BepInEx plugin patches install.
- **SensorLensesSaveData.cs** — `public class SensorLensesSaveData : DynamicThingSaveData` with `ActiveSensorReferenceId`.
- **AdvancedTabletSaveData.cs** — `public class EquipmentPlusTabletSaveData : VanillaTabletSaveData` (aliased from `Assets.Scripts.Objects.Items.AdvancedTabletSaveData`) with `ActiveCartridgeReferenceId`. Different class name to avoid XML type-name collision when both are registered in `ExtraTypes`; vanilla's `isinst` check still matches because of inheritance.
- **ReflectionUtils.cs** — Single helper `TryGetField<T>` used to reach `ConfigCartridge._displayTextMesh`.

## 2. Design decisions

### 2.1. Applied

- **100 fixed blocked slots instead of a configurable count**: blocked slots cost almost nothing (no UI widget, no network traffic, no save-data entry). Capping at 100 eliminates the config-file surface and the server/client mismatch that broke Better Advanced Tablet. No player will realistically insert 100 chips.
- **Ctrl+click instead of plain click for cycling**: plain click on held items fires vanilla's `AttackWith` path and collides with other mods that hook clicks (ImprovedConfiguration used plain click for ConfigCartridge). Requiring Ctrl makes our cycling orthogonal to every other click handler. `ClickCyclePatch` checks both `LeftControl` and `RightControl` via `KeyManager.GetButton`; the click-edit path in `ConfigCartridgeScreenPatch` checks the same key set so RightCtrl+click never fires both paths.
- **EquipmentPlusTabletSaveData inherits from vanilla `AdvancedTabletSaveData`**: vanilla's `DeserializeSave` does an `isinst` check for its own save-data type. Inheriting means our subclass still passes that check and any fields vanilla adds later are preserved automatically. The class is named differently to dodge XML type-name collision in `ExtraTypes`.
- **Dual save-data-type registration (LaunchPadBooster + direct)**: LaunchPadBooster's `AddSaveDataType` injects via a Prefix on `XmlSaveLoad.AddExtraTypes`. If that method already ran before our plugin loaded, the Prefix never fires. Direct append to `ExtraTypes` plus nulling `Serializers._worldData` covers the race.
- **Deferred active-slot restore via `OnFinishedLoad`**: vanilla restores children after the parent's `DeserializeSave`, and each child's `MoveToSlot` fires `OnChildEnterInventory`, which unconditionally overwrites `SensorLenses.Sensor` or `AdvancedTablet.Mode`. Stashing the saved id in `ActiveSlotPersistence` and consuming it in `OnFinishedLoad` (after all children are placed) is the only safe restore point.

### 2.2. Rejected or deferred

- **Custom network message for `(LogicSlotType, slotIndex)` writes**: rejected for initial scope. Vanilla has no equivalent of `SetLogicFromClient` for slot writes, and adding one requires LaunchPadBooster message registration, server-side slot-write permission handling, and compatibility testing against every device type exposing writable slot logic. Server / listen-server hosts still write fine; remote clients lose slot-write access. The original Slot Configuration Cartridge was single-player only, so accepting the same limitation for remote clients is the pragmatic tradeoff. Can be added later if demand warrants.
- **Configurable slot count for the expansion**: rejected. See Applied above.
- **Separate conflict-detection integration with absorbed mods**: rejected. Better Advanced Tablet / ImprovedConfiguration / Slot Configuration Cartridge are absorbed, not integrated; their assemblies are detected and load is blocked.

## 3. Harmony patches catalog

### 3.1. Prefab expansion

| Patch class | Target method | Type | Effect |
|---|---|---|---|
| `StartNewWorldPatch` | `World.StartNewWorld` | Prefix | Trigger `PrefabPatch` 100-slot mutation on new-game. |
| `LoadWorldPatch` | `XmlSaveLoad.LoadWorld` | Prefix | Trigger on load-game. |
| `ProcessJoinDataPatch` | `NetworkClient.ProcessJoinData` | Prefix | Trigger on client-join. |

All three route to a guarded one-shot `_hasRun` path.

### 3.2. Dynamic slot refresh

| Patch class | Target method | Type | Effect |
|---|---|---|---|
| `SensorLensesSlotOnInsert` | `SensorLenses.OnChildEnterInventory` | Postfix | `RefreshSlots` on chip insert. |
| `SensorLensesSlotOnRemove` | `SensorLenses.OnChildExitInventory` | Postfix | `RefreshSlots` on chip remove. |
| `TabletSlotOnInsert` | `AdvancedTablet.OnChildEnterInventory` (inherited) | Postfix | `RefreshSlots` on cartridge insert. |
| `TabletSlotOnRemove` | `AdvancedTablet.OnChildExitInventory` (inherited) | Postfix | `RefreshSlots` on cartridge remove. |
| `WindowOpenRefreshPatch` | `InventoryWindow.ToggleVisibility` | Postfix | Force `HandleOccupantChange` on open so stale widgets catch up. |

**Depends on:** [../../Research/Patterns/HarmonyInheritedMethods.md](../../Research/Patterns/HarmonyInheritedMethods.md) for the `TargetMethod()` + `Thing __instance` pattern used on the tablet slot patches and all sync/save patches below. [../../Research/GameClasses/AdvancedTablet.md](../../Research/GameClasses/AdvancedTablet.md) for the `CartridgeSlots` cache-staleness rebuild. [../../Research/GameClasses/InventoryWindow.md](../../Research/GameClasses/InventoryWindow.md) for the `SetSlots` widget-instantiation rule this patch works around.

### 3.3. Save / load

| Patch class | Target method | Type | Effect |
|---|---|---|---|
| `SensorLensesSerializeSavePatch` | `SensorLenses.SerializeSave` (inherited) | Postfix | Replace vanilla `DynamicThingSaveData` with `SensorLensesSaveData` carrying `ActiveSensorReferenceId`. |
| `SensorLensesSaveLoadPatch` | `SensorLenses.DeserializeSave` (inherited) | Postfix | Stash saved active-chip id into `ActiveSlotPersistence.PendingActiveSensor`. |
| `AdvancedTabletSerializeSavePatch` | `AdvancedTablet.SerializeSave` | Postfix | Replace with `EquipmentPlusTabletSaveData` carrying `ActiveCartridgeReferenceId`. |
| `TabletSaveLoadPatch` | `AdvancedTablet.DeserializeSave` | Postfix | Stash saved active-cartridge id. |
| `SensorLensesOnFinishedLoadPatch` | `SensorLenses.OnFinishedLoad` (inherited) | Postfix | Apply `Sensor = pending` after all children are placed. |
| `AdvancedTabletOnFinishedLoadPatch` | `AdvancedTablet.OnFinishedLoad` (inherited) | Postfix | Apply `Mode = pending` and reflect-invoke private `GetCartridge()` to refresh display. |

AdvancedTablet declares its own `SerializeSave`/`DeserializeSave`, so those two patches use plain `[HarmonyPatch(typeof, nameof)]` without `TargetMethod()`. The slot and `OnFinishedLoad` patches target inherited methods.

**Depends on:** [../../Research/Patterns/SaveLoadOrdering.md](../../Research/Patterns/SaveLoadOrdering.md) for why restoration must wait for `OnFinishedLoad`. [../../Research/Patterns/HarmonyInheritedMethods.md](../../Research/Patterns/HarmonyInheritedMethods.md) for inherited-method targeting. [../../Research/Patterns/SaveDataIsinstInheritance.md](../../Research/Patterns/SaveDataIsinstInheritance.md) for why `EquipmentPlusTabletSaveData` inherits vanilla. [../../Research/GameSystems/SaveDataRegistration.md](../../Research/GameSystems/SaveDataRegistration.md) for the `XmlSaveLoad.ExtraTypes` dual-registration.

### 3.4. Active-sensor sync

| Patch class | Target method | Type | Effect |
|---|---|---|---|
| `SensorLensesBuildUpdatePatch` | `SensorLenses.BuildUpdate` (inherited) | Postfix | When flag 0x4000 set, write `Sensor.ReferenceId` (or 0) to delta stream. |
| `SensorLensesProcessUpdatePatch` | `SensorLenses.ProcessUpdate` (inherited) | Postfix | Read reference id, assign `lenses.Sensor`. |
| `SensorLensesSerializeOnJoinPatch` | `SensorLenses.SerializeOnJoin` (inherited) | Postfix | Append `Sensor.ReferenceId` to join payload. |
| `SensorLensesDeserializeOnJoinPatch` | `SensorLenses.DeserializeOnJoin` (inherited) | Postfix | Read back on join. |

**Depends on:** [../../Research/Patterns/HarmonyInheritedMethods.md](../../Research/Patterns/HarmonyInheritedMethods.md). [../../Research/GameSystems/NetworkUpdateFlags.md](../../Research/GameSystems/NetworkUpdateFlags.md) for why 0x4000 is safe. [../../Research/Protocols/EquipmentPlusNetworking.md](../../Research/Protocols/EquipmentPlusNetworking.md) for the wire format.

### 3.5. Click cycling

| Patch class | Target method | Type | Effect |
|---|---|---|---|
| `ClickCyclePatch` | `InventoryManager.HandlePrimaryUse` | Prefix | On Ctrl+left-click, cycle sensor / cartridge / worn-lenses; return `false` to suppress remaining `HandlePrimaryUse`. |

Cycle orders:

- **Lenses**: iterate chips-in-slots plus a synthetic "off" position (Sensor=null). Advance to next.
- **Tablet**: `Mode = (Mode + 1) % cartridgeCount`, then reflect-invoke `GetCartridge()` to refresh the display (vanilla's `Mode` setter does not do this; `Next` / `Prev` / `InteractWith` do).
- **Worn lenses** (empty hand + Ctrl+click on `GlassesSlot`): cycle the equipped lenses. No OnOff gate; the cycle toggles power itself.

**Depends on:** [../../Research/GameClasses/InventoryManager.md](../../Research/GameClasses/InventoryManager.md) for why `HandlePrimaryUse` is the correct hook (`AdvancedTablet.AllowSelfUse == false` means `Item.OnUsePrimary` never fires for a plain tablet). [../../Research/GameClasses/AdvancedTablet.md](../../Research/GameClasses/AdvancedTablet.md) for the `Mode` / `GetCartridge()` coupling.

### 3.6. ConfigCartridge reimplementation

| Patch class | Target method | Type | Effect |
|---|---|---|---|
| `ConfigCartridgeScrollPatch` | `Cartridge.OnScroll` | Prefix (returns bool) | Advance selected-line index, drive viewport via `_scrollPanel.SetScrollPosition`, return `false` to suppress vanilla free-scroll. |
| `ConfigCartridgeScreenPatch` | `ConfigCartridge.OnScreenUpdate` | Postfix | Re-apply yellow-highlight markup to selected line, handle left-click (slot-line route, writable `InputWindow`, read-only clipboard copy). |
| `ConfigCartridgeSlotDisplayPatch` | `ConfigCartridge.ReadLogicText` | Postfix | Append per-slot logic values with yellow/grey/green color markup, register line indices in `SlotLines` for click routing. |
| `ConfigCartridgeDestroyPatch` | `Cartridge.OnDestroy` | Prefix | Clean up selected-index entry. |
| `ConfigCartridgeSlotDisplayDestroyPatch` | `Cartridge.OnDestroy` | Prefix | Clean up slot-line registry. |

Sign convention: `(scrollDelta.y > 0f) ? -1 : 1`. Wheel-up decreases index (selection moves up). Upstream ImprovedConfiguration uses the same convention; inverting it is the dominant visible symptom when paired with the "vanilla free-scroll still runs" bug.

Slot-line index math (`ConfigCartridgeSlotDisplay.Postfix`): read `____outputText.Split('\n').Length` BEFORE appending (= `baseLineIndex`, counts vanilla lines only), then increment `appendedLines` per `\n`-prefixed line written. Slot header occupies `baseLineIndex + appendedLines` at append time (header index NOT stored); first slot-logic line occupies the NEXT index and IS stored in `SlotLines[cc][index]`. Highlight wrap does not insert or remove `\n`, so click-time `textMesh.text.Split('\n')` indexing stays aligned.

Upstream ImprovedConfiguration reference (one-off borrow, not adopted wholesale):

- Click detection uses Unity's `Input.GetMouseButtonDown(0)`, not `KeyManager`. We require Ctrl via `KeyManager.GetButton` instead so cycling and click-edit don't collide.
- Click validation chain: `tablet != null`, `tablet.ParentSlot == InventoryManager.ActiveHandSlot`, `tablet.Cartridge == __instance`, `device = __instance.ScannedDevice != null`. We keep this chain.
- Line parsing: regex `<color=[^>]*>|</color>` strip, split on `" ... "`, `parts[0].Trim()` as `LogicType` name, `Enum.TryParse<LogicType>`. `double.TryParse` only on writable values. We keep this.
- Writable: `InputWindow.ShowInputPanel(typeName, currentValue.ToString(), Action<string,string> callback, 600)`. We keep this (with multiplayer routing added in callback).
- Read-only: direct `GUIUtility.systemCopyBuffer = value.ToString()`. We keep this.
- Selected-index dictionary: `static Dictionary<ConfigCartridge, int>`, keyed by instance, cleaned up in `Cartridge.OnDestroy` prefix. We keep this shape. Not persisted to save files; resets to 0 on load (acceptable).
- Upstream writes `Device.SetLogicValue(LogicType, double)` directly on the client. On a remote multiplayer client the write applies locally and is overwritten on next sync. Upstream is effectively host-only. We route via `SetLogicFromClient` to fix this.

**Depends on:** [../../Research/Patterns/HarmonyPrefixReturnBool.md](../../Research/Patterns/HarmonyPrefixReturnBool.md) for why `OnScroll` Prefix must return `bool` and suppress vanilla. [../../Research/GameClasses/Cartridge.md](../../Research/GameClasses/Cartridge.md) and [../../Research/GameClasses/ConfigCartridge.md](../../Research/GameClasses/ConfigCartridge.md) for vanilla screen plumbing (`_scrollPanel` on base, `_displayTextMesh.text` overwritten every frame, `_outputText` construction in `ReadLogicText`, `ScannedDevice` live from cursor). [../../Research/Protocols/GameMessageFactory.md](../../Research/Protocols/GameMessageFactory.md) for `SetLogicFromClient` client-to-server routing.

### 3.7. Slot-type icon

`SlotTypeIconPatch.RegisterMissingSensorIcon()` called once from `Plugin.OnAllModsLoaded`. Direct injection into `Slot._slotTypeLookup` for `SensorProcessingUnit`: first tries any sprite with "sensor" in its name, then falls back to the Cartridge icon.

**Depends on:** [../../Research/Patterns/SlotTypeIconLookup.md](../../Research/Patterns/SlotTypeIconLookup.md) for why a Postfix on `Slot.PopulateSlotTypeSprites` cannot work (runs at `InventoryManager.ManagerAwake`, before plugin load) and why direct `_slotTypeLookup` injection is the escape hatch.

## 4. Multiplayer and sync

### 4.1. Active-sensor sync (custom, flag 0x4000)

Remote client cycles: optimistic local write, then `SetActiveSensorMessage` (fields: `LensesReferenceId`, `SensorReferenceId`, `PowerOn`) to host. Server validates lenses exist, chip exists and is in a slot, applies `Sensor` and `OnOff`, sets `NetworkUpdateFlags |= 0x4000`. Next `BuildUpdate` writes `Sensor.ReferenceId` (or 0) to delta stream; all clients read it in `ProcessUpdate`. Late-join carries `Sensor.ReferenceId` in `SerializeOnJoin` / `DeserializeOnJoin`.

See [../../Research/Protocols/EquipmentPlusNetworking.md](../../Research/Protocols/EquipmentPlusNetworking.md) for the full wire schema.

### 4.2. Active-cartridge sync (vanilla `Interactable.State`)

`AdvancedTablet.Mode` is networked by vanilla as part of `Interactable.State`. No custom message. After setting `Mode`, we reflect-invoke private `GetCartridge()` because vanilla's `Mode` setter does not call it (only `Next` / `Prev` / `InteractWith` do).

### 4.3. ConfigCartridge logic writes (vanilla `SetLogicFromClient`)

- **`WriteLogicValue`** on server: `Device.SetLogicValue(LogicType, double)` directly. On client: sends `SetLogicFromClient` (vanilla message, fields: `LogicId` = device ReferenceId, `LogicType`, `Value`). Server-side handler applies authoritatively.
- **`WriteLogicSlotValue`** on server: `Device.SetLogicValue(LogicSlotType, slotIndex, value)` directly. On client: logs a warning. Vanilla has no `SetLogicSlotFromClient`; remote-client slot writes are unsupported in the current scope.

See [../../Research/Protocols/GameMessageFactory.md](../../Research/Protocols/GameMessageFactory.md) for the vanilla `SetLogicFromClient` definition.

## 5. Relevant central pages

### 5.1. GameClasses

- [../../Research/GameClasses/AdvancedTablet.md](../../Research/GameClasses/AdvancedTablet.md) - Tablet class hierarchy, `CartridgeSlots` cache staleness (rebuilt at Awake only, we resync after every slot state change), `Mode` via `Interactable.State`, private `GetCartridge()` for display refresh.
- [../../Research/GameClasses/Cartridge.md](../../Research/GameClasses/Cartridge.md) - `Cartridge._scrollPanel` (protected, serialized), `Cartridge.OnScroll` one-line forwarder to `_scrollPanel.OnScroll`, per-frame `UpdateEachFrame` that calls `OnScreenUpdate` for every cartridge (not just the displayed one).
- [../../Research/GameClasses/ConfigCartridge.md](../../Research/GameClasses/ConfigCartridge.md) - `OnScreenUpdate` overwrites `_displayTextMesh.text` every frame, `_outputText` built in `ReadLogicText`, `ScannedDevice` computed live from cursor.
- [../../Research/GameClasses/InventoryManager.md](../../Research/GameClasses/InventoryManager.md) - `HandlePrimaryUse` call sequence and why it is the right hook (bypasses `AllowSelfUse`), `KeyManager.GetMouseDown("Primary")` frame-stable semantics.
- [../../Research/GameClasses/InventoryWindow.md](../../Research/GameClasses/InventoryWindow.md) - `SetSlots` only instantiates widgets for `IsInteractable == true` slots; closed windows are invisible to `FindObjectsOfType`.
- [../../Research/GameClasses/SensorLenses.md](../../Research/GameClasses/SensorLenses.md) - Class hierarchy, `Sensor` property set in `OnChildEnterInventory` (not networked / not saved in vanilla), vanilla's two-slot assumption.
- [../../Research/GameClasses/Thing.md](../../Research/GameClasses/Thing.md) - Inherited `SerializeSave` / `DeserializeSave` / `BuildUpdate` / `ProcessUpdate` / `SerializeOnJoin` / `DeserializeOnJoin` / `OnFinishedLoad` members that we patch via `TargetMethod()` on `SensorLenses`.

### 5.2. GameSystems

- [../../Research/GameSystems/NetworkRoles.md](../../Research/GameSystems/NetworkRoles.md) - `NetworkRole.None` for single-player, server-vs-client branching patterns we use throughout.
- [../../Research/GameSystems/NetworkUpdateFlags.md](../../Research/GameSystems/NetworkUpdateFlags.md) - 16-bit bitmask; vanilla uses up to 0x0800 on Thing/DynamicThing/Item; we claim 0x4000 for active-sensor sync.
- [../../Research/GameSystems/SaveDataRegistration.md](../../Research/GameSystems/SaveDataRegistration.md) - `XmlSaveLoad.ExtraTypes` registration, `Serializers._worldData` cache invalidation, LaunchPadBooster vs direct timing race.

### 5.3. Patterns

- [../../Research/Patterns/HarmonyInheritedMethods.md](../../Research/Patterns/HarmonyInheritedMethods.md) - `TargetMethod()` + `Type.GetMethod` for inherited methods, `Thing __instance` typing to avoid `InvalidCastException` when base-class patch fires for non-SensorLenses Things.
- [../../Research/Patterns/HarmonyPrefixReturnBool.md](../../Research/Patterns/HarmonyPrefixReturnBool.md) - `Cartridge.OnScroll` prefix must return `bool` and return `false` to suppress vanilla free-scroll; otherwise highlight and viewport desync.
- [../../Research/Patterns/SaveDataIsinstInheritance.md](../../Research/Patterns/SaveDataIsinstInheritance.md) - Inherit vanilla save-data type so vanilla's `isinst` check still matches; rename to dodge XML type-name collision.
- [../../Research/Patterns/SaveLoadOrdering.md](../../Research/Patterns/SaveLoadOrdering.md) - Child restore fires `OnChildEnterInventory` for each child after parent's `DeserializeSave`; active-slot restoration must defer to `OnFinishedLoad`.
- [../../Research/Patterns/SinglePlayerNetworkRole.md](../../Research/Patterns/SinglePlayerNetworkRole.md) - Single-player runs `NetworkRole.None`; server-vs-client guards must tolerate this role.
- [../../Research/Patterns/SlotTypeIconLookup.md](../../Research/Patterns/SlotTypeIconLookup.md) - Direct injection into `Slot._slotTypeLookup` when a Postfix on `PopulateSlotTypeSprites` fires too late (method runs at `ManagerAwake`).

### 5.4. Protocols

- [../../Research/Protocols/EquipmentPlusNetworking.md](../../Research/Protocols/EquipmentPlusNetworking.md) - Our `SetActiveSensorMessage` fields, flag 0x4000 usage on `SensorLenses`, join-payload extension.
- [../../Research/Protocols/GameMessageFactory.md](../../Research/Protocols/GameMessageFactory.md) - Vanilla `SetLogicFromClient` (client-to-server `LogicType` writes); no vanilla `SetLogicSlotFromClient` for `(LogicSlotType, slotIndex)` writes.
- [../../Research/Protocols/LaunchPadBoosterNetworking.md](../../Research/Protocols/LaunchPadBoosterNetworking.md) - `Mod.Networking.Required` handshake and custom-message registration used for `SetActiveSensorMessage`.

## 6. Pitfalls and dead ends

Mod-local pitfalls that do not generalize (inherited-method typing, save-load ordering, `SlotTypeIconPatch` timing, scroll prefix return-`bool`, etc. are centralized; see Section 5 pointers).

### 6.1. `SlotTypeIcon` fallback is best-effort

The "sensor" substring search in `Slot._slotTypeLookup` depends on at least one sprite whose name contains "sensor" being loadable at plugin init. If no such sprite exists, we fall back to the Cartridge icon (always present). The real fix is a committed `sloticon-sensorprocessingunit` asset shipped with the mod; the runtime fallback is the stopgap.

### 6.2. `ConfigCartridgeSlotDisplay` line-index fragility

The index math in `ConfigCartridgeSlotDisplayPatch.Postfix` assumes the yellow highlight wrap applied by `ConfigCartridgeScreenPatch` does not insert or remove `\n`. If a future edit wraps a slot-header line with a markup that crosses a newline, indices will desync between the registry and `textMesh.text.Split('\n')` at click time. Sanity-check: every mutation of the wrap format should grep for `\n` insertion.

### 6.3. `Monitor.Enter/Exit` in `ConfigCartridgeSlotDisplay`

We hold a `Monitor.Enter/Exit` on the scanned device across slot-read operations to mirror the original Slot Configuration Cartridge's pattern. In practice `ReadLogicText` runs on the main thread and contention is unlikely; the cost is cheap and the pattern documents "this interacts with device state that other code might touch." If a future refactor moves `ReadLogicText` off the main thread, this lock becomes actually necessary.

### 6.4. Remote-client slot writes unsupported

No vanilla `SetLogicSlotFromClient` exists. `WriteLogicSlotValue` on a remote client logs a warning and does nothing. Host / listen-server / single-player work fine. This is a deliberate deferred decision (see Section 2.2); a custom message can be added if demand warrants.

### 6.5. `AllowSelfUse == false` on tablet makes `Item.OnUsePrimary` unreachable

A previous iteration targeted `Item.OnUsePrimary` and never fired for the tablet. `InventoryManager.HandlePrimaryUse` is the correct hook because it runs regardless of `AllowSelfUse`. Documented here so future refactors do not regress to the `OnUsePrimary` approach.

## 7. Open questions

- **Is 100 slots really enough?** No data says otherwise, and no player has asked for more. The cap avoids the config-mismatch class of bug entirely, but if a power-user workflow emerges that wants hundreds of chips, the cap becomes the bottleneck rather than vanilla.
- **Should active-cartridge get a custom message?** It currently rides vanilla `Interactable.State`. Works in all observed scenarios. An edge case that flushes state before a cycle message might drop the cycle; no repro yet.
- **Slot-write message feasibility for remote clients.** See Section 2.2 / 6.4. Needs LaunchPadBooster message registration and server-side permission checking across every device with writable slot logic. Open until a concrete remote-client complaint justifies the effort.
