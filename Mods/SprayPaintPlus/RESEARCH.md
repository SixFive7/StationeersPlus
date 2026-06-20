# Spray Paint Plus: Research Reference

Spray Paint Plus is a BepInEx plugin that combines Color Cycler, Network Painter, and Infinite Spray Paint into one server-authoritative mod. Clients send input events (scroll, modifier keys) through LaunchPadBooster messages; the server applies paint and broadcasts results through the vanilla `Consumable` network update path with a piggybacked color index. First-time readers: plugin wiring, conflict detection, and the file walkthrough live in Section 1; the six patch classes are catalogued in Section 3; the wire format and two sync flows are Section 4; decompiled game internals (Cell, Room, Grid3, SprayCan, OnServer.SetCustomColor, NetworkUpdateFlags, etc.) live on the central pages pointed to from Section 5.

## 1. Architecture

Mod identity:

| Field | Value |
|---|---|
| Display Name | Spray Paint Plus |
| Code Name | SprayPaintPlus |
| Dependencies | BepInEx, StationeersLaunchPad, LaunchPadBooster (networking v2) |

The mod is server-authoritative. Clients send input events (color scroll, modifier keys) to the server. The server applies paint and broadcasts results through the game's normal network update system.

### 1.1. Plugin wiring

`Plugin.Awake()` binds config, runs conflict detection, registers LaunchPadBooster messages (`SprayCanColorMessage`, `PaintModifierMessage`), then applies Harmony patches. `StationeersLaunchPad` provides `Prefab.OnPrefabsLoaded` for deferred initialization; `LaunchPadBooster` provides `INetworkMessage`, channel-based message transport, automatic compression, and the version-matching handshake.

**Conflict detection.** The mod replaces Color Cycler and Network Painter. It cannot coexist with them because they patch the same methods. `BepInIncompatibility` attributes cover load-time detection, but StationeersLaunchPad loads mods progressively, so those assemblies may not exist when `Awake()` runs. A second check runs on `Prefab.OnPrefabsLoaded`, scanning `AppDomain.CurrentDomain.GetAssemblies()` for the conflicting assembly names. If found, the mod logs a fatal error and starts a coroutine that repeats the warning every 5 seconds. No Harmony patches are applied. See [../../Research/Patterns/ConflictDetection.md](../../Research/Patterns/ConflictDetection.md) for the general assembly-scan-on-prefab-load pattern.

### 1.2. Threading model

All patches run on the main Unity thread. No ThreadPool work, no Unity-API-from-background-thread bridging. Input polling in `ColorCyclerPatch` runs inside `InventoryManager.NormalMode`, which is already main-thread. Paint flood-fills in `NetworkPainterPatch` run synchronously inside `OnServer.SetCustomColor`.

### 1.3. Server / client roles

Server runs paint logic. Clients send input and receive visual updates through the vanilla update path. Single-player runs as `NetworkRole.None` (all role flags false); the `SprayCanUsePatch` guard uses `IsActive && !IsServer` so it catches remote clients without also catching single-player. See [../../Research/GameSystems/NetworkRoles.md](../../Research/GameSystems/NetworkRoles.md) for the full role-flag matrix and [../../Research/Patterns/SinglePlayerNetworkRole.md](../../Research/Patterns/SinglePlayerNetworkRole.md) for the trap.

### 1.4. File walkthrough

| File | Purpose |
|---|---|
| `Plugin.cs` | Entry point. Binds config, runs conflict detection, registers LaunchPadBooster messages, applies Harmony patches. |
| `SprayPaintHelpers.cs` | Shared state and utility methods. Color index dictionary (`SprayCanColors`), modifier state dictionary (`PlayerModifiers`), current-painter tracking (`CurrentPaintingHumanId`), color lookup/apply helpers, thumbnail cache. |
| `ColorCyclerPatch.cs` | Patches `InventoryManager.NormalMode`. Detects scroll input while holding a spray can, cycles color index, sends `SprayCanColorMessage` to the server. Also polls modifier keys and sends `PaintModifierMessage` when state changes. |
| `NetworkPainterPatch.cs` | Patches `OnServer.SetCustomColor`. When a paint action fires, reads the painter's modifier state and either paints a single item or floods the connected network/room/grid. Contains `PaintAttackerTracker_Local` and `PaintAttackerTracker_Remote` patches that capture the painting player's Human ReferenceId before the paint reaches `SetCustomColor`. |
| `SprayCanUsePatch.cs` | Patches `SprayCan.OnUseItem`. Implements infinite paint (sets quantity to 0) and pollution suppression (skips vanilla's gas emission). |
| `ConsumableSyncPatch.cs` | Patches `Consumable.BuildUpdate`, `ProcessUpdate`, `SerializeOnJoin`, `DeserializeOnJoin`. Appends the spray can's color index to the game's binary network stream so color syncs to all clients and late joiners. |
| `SprayCanColorMessage.cs` | LaunchPadBooster `INetworkMessage`. Client-to-server: "I scrolled to color X on spray can Y." Server validates the color index and applies it. |
| `PaintModifierMessage.cs` | LaunchPadBooster `INetworkMessage`. Client-to-server: "My modifier key state is now X." Carries the player's Human ReferenceId so the server can key the lookup correctly. |
| `CleanupPatches.cs` | Patches `Thing.OnDestroy` and `NetworkServer.ClientDisconnected`. Removes stale entries from `SprayCanColors` and `PlayerModifiers` dictionaries. |

`SprayPaintHelpers` carries three concurrent dictionaries: `SprayCanColors` maps spray can ReferenceId to color index (server-side authoritative, mirrored to clients via the sync postfixes); `PlayerModifiers` maps Human ReferenceId to a modifier byte; `CurrentPaintingHumanId` is a one-slot static filled by the tracker patches right before `SetCustomColor` runs.

## 2. Design decisions

### 2.1. Applied

- **Combined mod vs. separate mods.** The three original mods (Color Cycler, Network Painter, Infinite Spray Paint) each patched overlapping methods and had no coordination. Running them together caused double-patching, ordering issues, and broken multiplayer. Combining them into one mod eliminates patch conflicts and allows shared state (e.g., modifier tracking feeds into network painting).
- **Server-authoritative paint.** All paint logic runs on the server. Clients send input (scroll, modifiers) and the server decides what gets painted. This prevents desync and means the server's config toggles are the single source of truth.
- **LaunchPadBooster Networking V2.** Moved from piggybacking on `ThingColorMessage` to LaunchPadBooster's dedicated message channels. V2 provides automatic compression, multi-packet splitting, and a version handshake. The handshake rejects mismatched mod versions, preventing wire-format desync. See [../../Research/Protocols/LaunchPadBoosterNetworking.md](../../Research/Protocols/LaunchPadBoosterNetworking.md).
- **Human ReferenceId for player identification.** The original mods used the LaunchPadBooster connection id to track which player pressed which modifier keys. But `AttackWithMessage` on the server does not carry the LaunchPadBooster connection id; it carries `AttackParentId`, which is the Human ReferenceId. Keying `PlayerModifiers` by Human ReferenceId matches the identifier available at paint time. See [../../Research/GameClasses/Human.md](../../Research/GameClasses/Human.md).
- **GenericFlag2 (bit 12) for color sync.** Bit 12 of `NetworkUpdateFlags` was chosen because it is unused by `Consumable`'s vanilla serialization. Setting this flag triggers a network update that includes the spray can's data, and the postfix patches append the color index to that data. See [../../Research/GameSystems/NetworkUpdateFlags.md](../../Research/GameSystems/NetworkUpdateFlags.md).

## 3. Harmony patches catalog

### 3.1. ColorCyclerPatch (Prefix on `InventoryManager.NormalMode`)

Runs every frame. Checks if the active hand holds a `SprayCan`. If scroll input is nonzero, computes the next color index (wrapping), updates the can's visual locally, and sends a `SprayCanColorMessage` to the server. Also checks modifier key state each frame and sends `PaintModifierMessage` on change. When running on the host, modifier state is also mirrored directly into the server-side `PlayerModifiers` dictionary so the local player skips the network round-trip.

The color index wraps with simple `+1` / `-1` and bounds check. `InvertColorScrollDirection` flips the scroll direction. `PaintSingleItemByDefault` XORs with Shift before encoding bit 0 of the modifier byte.

**Depends on:** [../../Research/GameClasses/InventoryManager.md](../../Research/GameClasses/InventoryManager.md), [../../Research/GameClasses/SprayCan.md](../../Research/GameClasses/SprayCan.md).

### 3.2. NetworkPainterPatch (Prefix on `OnServer.SetCustomColor`)

Core network/room/grid paint logic. Only runs on the server (or in single-player, which is also "server" from the game's perspective after the `IsActive` guard).

**Reentrancy guard.** A static `_painting` bool prevents recursive invocation. Each `PaintSafe` call invokes `item.SetCustomColor`, which re-enters this prefix. Without the guard, one paint would cascade into an infinite loop.

**Skip the original target.** Inside the flood loop, if an item is `ReferenceEquals` to the original paint target, the patch skips it; vanilla `SetCustomColor` is going to paint that one itself.

**Modifier lookup.** Reads from `SprayPaintHelpers.PlayerModifiers` keyed by the Human ReferenceId captured by the tracker patches. Bit 0 = single-item mode (skip network paint). Bit 1 = checkered pattern.

**Paint branches** (checked in order, first match wins):

1. **Pipes**: `HydroponicTray`, `PassiveVent`, and `Pipe` each get their own sub-branch. Trays and passive vents are subtypes of Pipe but paint only within their own type. The pipe branch excludes trays and passive vents.
2. **Cables**: Floods `CableNetwork.CableList`.
3. **Chutes**: Floods `ChuteNetwork.StructureList`.
4. **Rails**: Dispatches off `INetworkedRoboticArm` (the interface that exposes `RoboticArmNetwork`) and enumerates `RoboticArmNetwork.RailList`. One traversal covers every member of the assembly: rail pieces, junctions, bypass, and docks. No grid walk needed; the network object is maintained server-side and rebuilt on topology change.
5. **Elevators**: Dispatches off `ElevatorShaft` (covers `ElevatorLevel` via inheritance) and walks `ElevatorShaftNetwork.Shafts` through the `PaintElevatorNetwork` helper. The carriage (`ElevatorShaftNetwork.Carrage`, a `DynamicThing`) is deliberately excluded: it is a separate movable object painted on its own, and the branch only matches shaft/level seeds, so painting the carriage directly falls through to vanilla single-paint and never floods the shafts. The checker cannot reuse either existing helper: `CheckeredCheck` (world-position parity at 0.5-unit resolution) never flips between segments stacked several whole world units apart, so it degrades to a full flood; `CheckeredCheckGrid` silently fails when segments are an even number of cells apart. Instead the helper sorts segments by `GridPosition.y` and alternates by vertical index, keyed off the seed's own segment so the seed level always paints.
6. **Ladders**: Dispatches off `Ladder` (covers `LadderEnd` caps via inheritance). Ladders are `SmallGrid` structures on the 0.5 m small grid with no network object, so the flood walks the small grid by KEY rather than world position: from the seed's registered cell (`origin.SmallCell.SmallGrid`) it steps the key vertically (one small cell is 5 Grid3 units; one 2 m rung pitch is 4 cells) and reads `GetSmallCell(key).Other as Ladder`. Only the rung directly above or below in the same column and same `Forward` connects; the scan skips the seed's own multi-cell footprint and stops at the first empty cell past it, so a missing rung splits the column. Checker is `PaintRunByHeight` (vertical index parity).
7. **Stairs** (angled flights: `Stairs` with non-zero `Entry`/`Exit`): gathers same-prefab, same-`Forward` flights from a small cube around the seed and keeps only those in a valid run relationship via `StairsConnect`: widening (directly to the side, same level) or lengthening (one run-step along the facing axis with exactly one level of rise, in the direction `Exit - Entry` ascends). Crossing flights and pieces two levels up over one run-step are rejected. Checker is a true 2D checkerboard across the staircase plane (`StairCheckerSameColour`: parity of width-steps + level-steps).
8. **Stairwells** (`Stairs` with zero `Entry`/`Exit`, the eight passthrough / door variants): a plain cell flood (`PaintStairwellRun`) over every spatially adjacent stairwell, any type, any orientation, expanding through all 26 `Cell.NeighborCells`; an empty cell stops the flood so a gap separates blocks. Checker is the 3D `CheckeredCheckGrid`.
9. **Walls**: Floods by `Room` membership. Scans `room.Grids` plus one orthogonal-neighbor expansion layer (walls sit on room boundaries, not inside). Filters by `PrefabHash` equality (so visual wall variants like Wall, Wall Flat, Wall Arched stay separate; same logic separates Floor visual variants since `Floor : Wall` flows through this branch) and same `GetRoom()` result.
10. **Large structures**: BFS flood-fill on the world grid using 6-neighbor (cardinal) adjacency. `Cell.NeighborCells` returns all 26 neighbors (including diagonals); `IsOrthogonalNeighbor` filters to axis-aligned only by checking that exactly one axis of the `Grid3` difference is nonzero. Filters by `GetType()` equality, not `PrefabHash`, so visual variants of `Frame` (frames, web frames, girders, etc.) flood together as one group.

Ladders, stairs, and stairwells all dispatch after Elevators and before Walls. `Ladder` is a `SmallGrid`; `Stairs` is a plain `Structure` (neither a `Wall` nor a `LargeStructure`), so order against the Wall / Large-structure branches does not matter, but the small-grid ladder walk and the two `Stairs` floods are genuinely different code paths. The angled-flight stair flood and the stairwell flood are split purely on whether `Entry`/`Exit` are set (`IsStairwell`).

**Wall branch must precede Large Structure** because `Wall` derives from `LargeStructure`. A wall with walls-painting disabled returns early and does not fall through to the grid flood.

**`PaintSafe` exception handling.** `PaintSafe` catches `NotImplementedException` per-item so one unpaintable batched-mesh structure does not abort the rest of the network.

**Depends on:** [../../Research/GameClasses/OnServer.md](../../Research/GameClasses/OnServer.md), [../../Research/GameClasses/Cell.md](../../Research/GameClasses/Cell.md), [../../Research/GameClasses/SmallCell.md](../../Research/GameClasses/SmallCell.md), [../../Research/GameClasses/Room.md](../../Research/GameClasses/Room.md), [../../Research/GameClasses/Grid3.md](../../Research/GameClasses/Grid3.md), [../../Research/GameClasses/Wall.md](../../Research/GameClasses/Wall.md), [../../Research/GameClasses/Structure.md](../../Research/GameClasses/Structure.md), [../../Research/GameClasses/Stairs.md](../../Research/GameClasses/Stairs.md), [../../Research/GameClasses/RoboticArmRail.md](../../Research/GameClasses/RoboticArmRail.md), [../../Research/GameClasses/Elevator.md](../../Research/GameClasses/Elevator.md).

### 3.3. SprayCanUsePatch (Prefix on `SprayCan.OnUseItem`)

Four-combination matrix of `infinite` x `suppressPollution`:

| infinite | suppress | Behavior |
|---|---|---|
| true | true | Set quantity to 0, skip vanilla entirely. No consumption, no gas. |
| true | false | Set quantity to 0, let vanilla run. No consumption, gas still emits. |
| false | true | Leave quantity alone, skip vanilla, subtract quantity manually. Normal consumption, no gas. |
| false | false | Let vanilla run unmodified. |

Guard: `if (NetworkManager.IsActive && !NetworkManager.IsServer) return true`. This skips only multiplayer remote clients. Single-player has `NetworkRole.None` where both `IsActive` and `IsServer` are false, so the condition is false and the patch runs.

**Depends on:** [../../Research/GameClasses/SprayCan.md](../../Research/GameClasses/SprayCan.md), [../../Research/GameSystems/NetworkRoles.md](../../Research/GameSystems/NetworkRoles.md), [../../Research/Patterns/SinglePlayerNetworkRole.md](../../Research/Patterns/SinglePlayerNetworkRole.md).

### 3.4. ConsumableSyncPatch (Postfixes on `Consumable` serialization)

Appends one `Int32` (the color index) after the vanilla `Consumable` data in both the per-tick update stream (`BuildUpdate` / `ProcessUpdate`) and the join snapshot (`SerializeOnJoin` / `DeserializeOnJoin`). Uses `SprayPaintHelpers.PaintColorNetworkFlag` (bit 12, `GenericFlag2`) to gate the per-tick write / read.

No try-catch wraps these calls. A local try-catch here is actively dangerous: if the read throws, the `RocketBinaryReader` is already past some bytes and in an unknown position; swallowing the exception leaves every subsequent field for that object (and potentially the whole update packet) misaligned.

**Depends on:** [../../Research/GameSystems/NetworkUpdateFlags.md](../../Research/GameSystems/NetworkUpdateFlags.md), [../../Research/Patterns/BinaryStreamSafety.md](../../Research/Patterns/BinaryStreamSafety.md).

### 3.5. PaintAttackerTracker (Prefix / Postfix on `OnServer.AttackWith` and `AttackWithMessage.Process`)

Two patches capture the painting player's identity before the paint reaches `SetCustomColor`:

- **`PaintAttackerTracker_Local`** (`OnServer.AttackWith`): `attackParent` is the player's Human. Prefix stores its `ReferenceId` in `CurrentPaintingHumanId`. Postfix resets to -1.
- **`PaintAttackerTracker_Remote`** (`AttackWithMessage.Process`): the authoritative id is `AttackParentId` from the message body (the Human ReferenceId). The `hostId` parameter (LaunchPadBooster connection id) is unreliable on the server, so the mod ignores it and reads from the message body instead.

Both postfixes reset `CurrentPaintingHumanId` to -1. The `NetworkPainterPatch.Prefix` also resets it after reading, as a guard against stale values if an earlier tracker postfix was skipped due to an exception.

**Depends on:** [../../Research/GameClasses/OnServer.md](../../Research/GameClasses/OnServer.md), [../../Research/GameClasses/Human.md](../../Research/GameClasses/Human.md), [../../Research/Protocols/GameMessageFactory.md](../../Research/Protocols/GameMessageFactory.md).

### 3.6. CleanupPatches

- `ThingDestroyCleanupPatch` (Postfix on `Thing.OnDestroy`): Removes destroyed spray cans from `SprayCanColors`.
- `ClientDisconnectCleanupPatch` (Prefix on `NetworkServer.ClientDisconnected`): Removes the disconnecting player's entry from `PlayerModifiers`. Must be a Prefix because vanilla's `RemoveClient` destroys the `Client` record before returning, making it unreachable in a Postfix.

**Depends on:** [../../Research/Patterns/ClientDisconnectedPrefix.md](../../Research/Patterns/ClientDisconnectedPrefix.md).

### 3.7. Glow Paint (v1.4.0)

The Spray Paint Gun is a self-contained glow applicator. Firing at a painted target preserves its color and adds a glow halo. The gun does not accept spray cans; it has no ammo requirement. A plain spray can removes glow by painting the target with the normal material.

**Gun pipeline** (`GlowPaintPatches.cs`):

- `SprayGunIsOperablePatch` (Prefix on the `SprayGun.IsOperable` getter): forces the result to the gun's `OnOff` state, dropping vanilla's `IsEmpty` gate. The ammo-less gun would otherwise read as inoperable (red targeting cursor) with no can loaded.
- `ThingAttackWithGunPatch` (Prefix on `Thing.AttackWith`): the core glow path. When the attack source is a `SprayGun` and the target is already painted, it sets `GlowPaintHelpers.CurrentMode` to `AddGlow` or `RemoveGlow` from the gun's `OnOff`, then enters via `OnServer.SetCustomColor(target, target.CustomColor.Index)` (server-gated by `GameManager.RunSimulation`) so the per-Thing color/glow postfixes and `NetworkPainterPatch` flood / single / checkered logic still run. A click whose mode matches the target's current glow state fails with a descriptive tooltip; non-gun attacks (cans, authoring tools, anything else) return `true` and fall through to vanilla. Patches `Thing.AttackWith` rather than `ISprayer.DoSpray` because Harmony cannot patch a static interface method, and `AttackWith` is the sole caller. The loaded can (if any) is ignored on the glow path; no ammo consumption.
- `SprayGunContextualNamePatch` (Postfix on `Thing.GetContextualName`): relabels the gun's on/off context action to "Add Glow" / "Remove Glow" (filtered to `SprayGun` + `InteractableType.OnOff`).

**Color preservation** (`GlowPaintPatches.cs`):

- `ThingSetCustomColorGunPreservePrefix` (Prefix on `Thing.SetCustomColor(int, bool)`): when `GlowPaintHelpers.CurrentMode` is `AddGlow` / `RemoveGlow` and the target already has a `CustomColor`, rewrites the incoming `index` to the target's existing color index. Net effect: the gun never changes a Thing's color. Works per-Thing during flood-fill (each flooded neighbor preserves its own color).

**Glow state and material re-application** (`GlowPaintPatches.cs`):

- `ThingSetCustomColorGlowPatch` (Postfix on `Thing.SetCustomColor(int, bool)`): when `CurrentMode` is `AddGlow` it sets the target's `IsGlowing` true, `RemoveGlow` sets it false, both raising `GlowPaintHelpers.GlowNetworkFlag` (bit 13) so the state syncs. Regardless of mode, if `IsGlowing` is true and the call was non-emissive, it re-invokes `SetCustomColor(index, true)` behind the `Reapplying` reentrancy guard to swap in the emissive material. Server-safe without an explicit `IsServer` gate: `CurrentMode` is set only inside `ThingAttackWithGunPatch`'s `RunSimulation`-gated branch, so it stays `Idle` on clients; the emissive re-skin is keyed on the host-authoritative `GlowingThingIds`. This one hook covers gun paint, color-sync receives, save-load restore, and flood-fill paint from `NetworkPainterPatch`. Audited in [../../Research/Patterns/MultiplayerStateMutation.md](../../Research/Patterns/MultiplayerStateMutation.md).
- `ThingDestroyGlowCleanupPatch` (Postfix on `Thing.OnDestroy`): removes the Thing from `GlowPaintHelpers.GlowingThingIds`. Sibling of `ThingDestroyCleanupPatch` (section 3.6); Harmony allows multiple patches on the same method.

**Gun slot block** (`GlowPaintPatches.cs`):

- `SprayGunSlotHiderPatch` (Postfix on `SprayGun.Awake`, reached via `TargetMethod` because `Awake` is inherited from a base; see [../../Research/Patterns/HarmonyInheritedMethodTrap.md](../../Research/Patterns/HarmonyInheritedMethodTrap.md)): when glow paint is enabled and the gun's can slot (slot 0) is empty, flips the slot's `Type` to `Slot.Class.Blocked`, sets `IsInteractable` false, and swaps the slot icon. This hides the slot and blocks the UI insertion paths (`Slot.AllowMove` / `Slot.CanInsert` reject a can once the slot `Type` no longer matches the can's `SlotType`). It is NOT a server-authoritative `CanEnter` block: vanilla `Thing.CanEnter` does not consult `Slot.Class` for ordinary items, so a direct `OnServer.MoveToSlot` is not rejected. In practice every UI path a player can drive is blocked; the unguarded data-layer path is only reachable by a crafted network message or another mod calling `MoveToSlot` directly, and the glow gun ignores slot contents anyway. The stronger `CanEnter` + `AllowMove` technique in [../../Research/Patterns/SlotInsertionBlock.md](../../Research/Patterns/SlotInsertionBlock.md) is documented but not used here. Gated by `EnableGlowPaint`; when off, the patch no-ops and the gun accepts cans as before.

Existing saves with a can loaded: a vanilla spray gun normally holds a can, so the common case of adding the mod to an existing save presents an occupied slot at `Awake`. `SprayGunSlotHiderPatch` then bails (the occupied-slot guard) and leaves the slot visible and interactable, so the can stays in the gun, the gun still works as a glow applicator (the can is never consumed), and the player can remove the can manually. Once the can is removed and the world reloaded, the now-empty slot is blocked and hidden like any other glow gun. There is no auto-eject; this edge case is accepted by design (see [../../Research/Patterns/SlotInsertionBlock.md](../../Research/Patterns/SlotInsertionBlock.md) "Legacy-state handling").

**Modifier polling extension** (`ColorCyclerPatch.cs`):

The existing `ColorCyclerPatch` polls Shift / Ctrl modifier state only when the active hand holds a `SprayCan`. Extended in v1.4.0 to also poll when the hand holds a `SprayGun`, so Shift (single) and Ctrl (checkered) work for gun-paint. Color-cycling via scroll remains can-only; the gun has no color state.

**Multiplayer sync** (`ThingGlowSyncPatches.cs`): Postfixes on `Thing.BuildUpdate` / `ProcessUpdate` / `SerializeOnJoin` / `DeserializeOnJoin`. Piggybacks on bit 13 (`GenericFlag3`, `0x2000`) of `Thing.NetworkUpdateFlags` for per-tick updates; `SerializeOnJoin` / `DeserializeOnJoin` unconditionally write/read one byte for late joiners. No try-catch per [../../Research/Patterns/BinaryStreamSafety.md](../../Research/Patterns/BinaryStreamSafety.md).

**Save/load persistence** (`GlowSaveLoadPatches.cs`, `GlowSideCar.cs`, `GlowThingSaveData.cs`): v1.6.0+ persists the set of glowing `ReferenceId`s in a side-car file `sprayplus-glow.xml` inside the save ZIP, alongside the vanilla `world.xml`. `world.xml` stays 100% vanilla; removing the mod from an existing save no longer fails the load. See `../../Research/GameSystems/SaveZipExtension.md` for the ZIP read/write asymmetry (read preserves unknown entries, write rebuilds from scratch via `ZipOutputStream`) and `../../Research/GameSystems/UnregisteredSaveDataBehavior.md` for the failure mode this design avoids.

Write path: `SaveHelperSaveSideCarPatch` Prefix snapshots `GlowPaintHelpers.GlowingThingIds` on the main thread, then the Postfix wraps the returned `UniTask<SaveResult>` with a continuation that writes the side-car after the archive has been sealed and moved to its final location. The snapshot avoids the ThreadPool race; the wrapper handles the async Harmony-patch trap (see `../../Research/Patterns/AsyncHarmonyTrap.md`).

Read path: `XmlSaveLoadLoadWorldSideCarPatch` reads the side-car into `GlowSideCar.LoadedGlowIds` after `world.xml` deserialization completes. `ThingOnFinishedLoadGlowPatch` consumes the set in each Thing's `OnFinishedLoad` postfix, which runs after `DeserializeSave`, child placement, atmosphere load, and device init are all complete (see `../../Research/GameSystems/SaveZipExtension.md` "Thing.OnFinishedLoad timing and caller").

Back-compat: `GlowThingSaveData : ThingSaveData` is preserved for one release cycle. Old saves (v1.4.x-v1.5.x) contain `<ThingSaveData xsi:type="GlowThingSaveData">` entries; `Plugin.cs` keeps the type registered via `MOD.AddSaveDataType<GlowThingSaveData>()` plus direct `XmlSaveLoad.ExtraTypes` injection (see `RegisterSaveDataTypeLate`) so the vanilla `XmlSerializer` accepts them. `ThingDeserializeSaveGlowPatch` re-applies glow from the old entries on load. On the next save, the side-car writer owns persistence and `ThingSerializeSaveGlowPatch` is gone, so `world.xml` is rewritten without the custom `xsi:type` and the save migrates to the clean format.

**Config**: server toggle `EnableGlowPaint` (default On). When off, `SprayGunIsOperablePatch` and `ThingAttackWithGunPatch` return early (vanilla gun behavior restored, including can-as-ammo use), `SprayGunSlotHiderPatch` no-ops (the can slot stays visible and usable), and the `Thing.SetCustomColor` glow postfix returns early (no glow state touched).

**Depends on:** [../../Research/GameClasses/ISprayer.md](../../Research/GameClasses/ISprayer.md), [../../Research/GameClasses/SprayGun.md](../../Research/GameClasses/SprayGun.md), [../../Research/GameClasses/ColorSwatch.md](../../Research/GameClasses/ColorSwatch.md), [../../Research/GameClasses/ThingRenderer.md](../../Research/GameClasses/ThingRenderer.md), [../../Research/GameSystems/RenderingPipelineAndGlow.md](../../Research/GameSystems/RenderingPipelineAndGlow.md), [../../Research/GameSystems/NetworkUpdateFlags.md](../../Research/GameSystems/NetworkUpdateFlags.md), [../../Research/GameSystems/SaveDataRegistration.md](../../Research/GameSystems/SaveDataRegistration.md), [../../Research/Patterns/SaveDataIsinstInheritance.md](../../Research/Patterns/SaveDataIsinstInheritance.md), [../../Research/Patterns/SlotInsertionBlock.md](../../Research/Patterns/SlotInsertionBlock.md), [../../Research/Patterns/BinaryStreamSafety.md](../../Research/Patterns/BinaryStreamSafety.md).

## 4. Multiplayer and sync

### 4.1. Messages

Two LaunchPadBooster `INetworkMessage` types, both client-to-server:

1. **SprayCanColorMessage**: `{ SprayCanId: long, ColorIndex: int }`. Sent when a client scrolls to change color. Server validates `ColorIndex` range, finds the SprayCan by ReferenceId, and applies the color. The update broadcasts to all clients via the normal `Consumable` network update path.
2. **PaintModifierMessage**: `{ Modifiers: byte, PlayerHumanId: long }`. Sent when modifier key state changes. Server stores in `PlayerModifiers[PlayerHumanId]`. Read during `NetworkPainterPatch.Prefix` to decide single / network / checkered mode.

See [../../Research/Protocols/SprayPaintPlusNetworking.md](../../Research/Protocols/SprayPaintPlusNetworking.md) for the full schema and the version handshake (`Networking.Required = true`) that enforces mod-version matching across all connected players.

### 4.2. Sync flow for color changes

1. Client detects scroll in `ColorCyclerPatch.Prefix`.
2. Client updates the spray can's visual locally (immediate feedback).
3. Client sends `SprayCanColorMessage` to the server.
4. Server validates, applies color via `UpdateSprayCanServer`, which sets the visual and raises `NetworkUpdateFlags`.
5. Next tick, `Consumable.BuildUpdate` fires; the `ConsumableBuildUpdatePatch` postfix writes the color index into the stream.
6. All clients receive the update; `ConsumableProcessUpdatePatch` reads the color index and applies it visually.

The color sync piggybacks on bit 12 (`GenericFlag2`) of `Thing.NetworkUpdateFlags`. See [../../Research/GameSystems/NetworkUpdateFlags.md](../../Research/GameSystems/NetworkUpdateFlags.md).

### 4.3. Sync flow for painting

1. Client attacks a structure with a spray can (vanilla input).
2. Vanilla sends `AttackWithMessage` to the server.
3. Server-side tracker prefix captures the Human ReferenceId.
4. Vanilla calls `OnServer.SetCustomColor` for the targeted item.
5. `NetworkPainterPatch.Prefix` intercepts, looks up modifiers for the painter, and paints the network / room / grid.
6. Each painted item's `SetCustomColor` sets its own `NetworkUpdateFlags`, broadcasting the color change to all clients through vanilla's update tick.

## 5. Relevant central pages

### 5.1. GameClasses

- [../../Research/GameClasses/Cell.md](../../Research/GameClasses/Cell.md) - `NeighborCells` returns all 26 surrounding cells; we filter to 6 orthogonal for checkered painting and room-wall propagation.
- [../../Research/GameClasses/GameManager.md](../../Research/GameClasses/GameManager.md) - `GameManager.Instance.CustomColors` list; each entry's index is the canonical color identifier the mod uses everywhere.
- [../../Research/GameClasses/Grid3.md](../../Research/GameClasses/Grid3.md) - `GridSize` scale (10 units per world unit, 2 world units per grid cell) and the parity math behind our checkered-painting option.
- [../../Research/GameClasses/Human.md](../../Research/GameClasses/Human.md) - Human `ReferenceId` is the player-identification key our modifier dictionary and tracker patches use.
- [../../Research/GameClasses/InventoryManager.md](../../Research/GameClasses/InventoryManager.md) - `NormalMode` is the per-frame hook for input polling in `ColorCyclerPatch`.
- [../../Research/GameClasses/OnServer.md](../../Research/GameClasses/OnServer.md) - `SetCustomColor` and `AttackWith` are the two server-side entry points our paint and tracker patches hook.
- [../../Research/GameClasses/Room.md](../../Research/GameClasses/Room.md) - `Room.Grids` lists interior cells; walls sit one layer outside, which is why our wall-painting expands a neighbor layer.
- [../../Research/GameClasses/RoboticArmRail.md](../../Research/GameClasses/RoboticArmRail.md) - `IRoboticArmRail.RoboticArmNetwork.RailList` holds every rail + junction + bypass + dock on one assembly; the rail paint branch walks that single list.
- [../../Research/GameClasses/SprayCan.md](../../Research/GameClasses/SprayCan.md) - `PaintMaterial`, `Thumbnail`, `Quantity`, and one-prefab-per-color model that our color swap and infinite-paint logic target.
- [../../Research/GameClasses/Structure.md](../../Research/GameClasses/Structure.md) - Batched structures (`structureRenderMode != Standard`) throw `NotImplementedException` from `SetCustomColor`; `PaintSafe` relies on this contract.
- [../../Research/GameClasses/Wall.md](../../Research/GameClasses/Wall.md) - `Wall` extends `LargeStructure`; our paint branches must check Wall first for correct dispatch.

### 5.2. GameSystems

- [../../Research/GameSystems/NetworkRoles.md](../../Research/GameSystems/NetworkRoles.md) - `NetworkManager` role-flag matrix; basis for our `IsActive && !IsServer` remote-client check.
- [../../Research/GameSystems/NetworkUpdateFlags.md](../../Research/GameSystems/NetworkUpdateFlags.md) - Bitmask semantics and the free bit 12 (`GenericFlag2`) we piggyback for spray can color sync.
- [../../Research/GameSystems/SaveZipExtension.md](../../Research/GameSystems/SaveZipExtension.md) - Read-safe / write-unsafe asymmetry of the save ZIP; Harmony interception pattern for `SaveHelper.Save`; `Thing.OnFinishedLoad` timing. Drives our v1.6.0 side-car persistence for glow.
- [../../Research/GameSystems/UnregisteredSaveDataBehavior.md](../../Research/GameSystems/UnregisteredSaveDataBehavior.md) - The failure mode our v1.6.0 migration avoids: a save containing `<ThingSaveData xsi:type="GlowThingSaveData">` without the mod installed fails the entire `WorldData` deserialize.
- [../../Research/GameSystems/SaveDataRegistration.md](../../Research/GameSystems/SaveDataRegistration.md) - Dual registration of `GlowThingSaveData` in `XmlSaveLoad.ExtraTypes` (via `MOD.AddSaveDataType` plus direct injection) required for back-compat reading of pre-v1.6.0 saves while the mod is still installed.

### 5.3. Patterns

- [../../Research/Patterns/BinaryStreamSafety.md](../../Research/Patterns/BinaryStreamSafety.md) - Why `ConsumableSyncPatch` deliberately has no try-catch around the binary read / write.
- [../../Research/Patterns/ClientDisconnectedPrefix.md](../../Research/Patterns/ClientDisconnectedPrefix.md) - `NetworkServer.ClientDisconnected` destroys the `Client` before returning; cleanup patches must be Prefixes.
- [../../Research/Patterns/ConflictDetection.md](../../Research/Patterns/ConflictDetection.md) - `Prefab.OnPrefabsLoaded` assembly-scan pattern used when `BepInIncompatibility` is insufficient under progressive mod loading.
- [../../Research/Patterns/SinglePlayerNetworkRole.md](../../Research/Patterns/SinglePlayerNetworkRole.md) - `NetworkRole.None` trap and the correct guard shape our `SprayCanUsePatch` uses.

### 5.4. Protocols

- [../../Research/Protocols/GameMessageFactory.md](../../Research/Protocols/GameMessageFactory.md) - `AttackWithMessage.hostId` is unreliable server-side; `AttackParentId` in the message body is authoritative (used by `PaintAttackerTracker_Remote`).
- [../../Research/Protocols/LaunchPadBoosterNetworking.md](../../Research/Protocols/LaunchPadBoosterNetworking.md) - V2 message channels, compression, multi-packet splitting, and the `Networking.Required = true` handshake our two messages rely on.
- [../../Research/Protocols/SprayPaintPlusNetworking.md](../../Research/Protocols/SprayPaintPlusNetworking.md) - Our two custom messages (`SprayCanColorMessage`, `PaintModifierMessage`) with schema, flow, and handshake details.

## 6. Pitfalls / dead ends

### Reentrancy in SetCustomColor

`PaintSafe` calls `item.SetCustomColor`, which re-enters the `NetworkPainterPatch` prefix. The `_painting` static bool prevents infinite recursion. Without it, painting one pipe would try to paint the whole network for every pipe in the network.

### Wall vs. LargeStructure inheritance ordering

`Wall` extends `LargeStructure`. The wall branch in `PaintNetwork` must come first. If walls-painting is disabled for a wall target, the method returns early rather than falling through to the large-structure grid flood.

### Grid3 parity trap for checkered painting

`Grid3` scales world coordinates by 10. Walls and large structures snap to a 2-world-unit cell grid, so every grid-aligned structure's `GridPosition` is a multiple of 20 Grid3 units. Naive `(x+y+z) % 2` parity is the same for every structure. The checkered check works on the delta between two positions divided by cell size, which gives the cell-index distance. Parity of that distance is the checker answer. See [../../Research/GameClasses/Grid3.md](../../Research/GameClasses/Grid3.md).

### Ladders live on the small grid, walk by key not world position

Ladders (and `LadderEnd`) are `SmallGrid`, registered in `SmallCell.Other` on the 0.5 m small grid, not in `Cell.AllStructures`. `GridController.GetStructure` (the large grid) never returns them; only `GetSmallCell` / `SmallCell.Get<T>` do. A first attempt probed `SmallCell.Get<Ladder>(CenterPosition + axis*step)` and found nothing: `SmallGrid.CenterPosition` adds `Forward * 0.2`, and the registered key anchors off `Position` (live capture: `key = (Position.x*10, Position.y*10 + 5, Position.z*10)`), so the world probe snapped into the wrong cell. The fix is to step the seed's own registered key (`origin.SmallCell.SmallGrid`) in 5-unit increments and `GetSmallCell(key).Other`. A ladder also occupies several cells, so the walk must skip cells that resolve back to the seed (`ReferenceEquals`) before reaching the next rung. See [../../Research/GameClasses/SmallCell.md](../../Research/GameClasses/SmallCell.md).

### Stairwells are the Stairs class with zero Entry/Exit

All eight stairwell variants (`StructureStairwellFrontPassthrough`, `BackPassthrough`, `NoDoors`, `FrontLeft`, `FrontRight`, `BackLeft`, `BackRight`, and the front/back passthrough) are the `Stairs` C# class, not a separate type. They are vertical pass / door pieces with no climb, so the game leaves `Entry` and `Exit` at `(0,0,0)`; angled flights set them. `IsStairwell` keys off that, routing stairwells to the plain adjacency flood and angled flights to the widening / lengthening flood. Confirmed via InspectorPlus across all eight variants.

### Grid3.ToVector3 converts grid to world; do not divide twice

`Grid3.ToVector3()` returns world metres (it divides by `Grid3.one`), not the raw integer components. The stair lengthening check needs "one level of rise" in world units; computing it as `(Exit - Entry).ToVector3().y / Grid3.one.y` divides by ten twice (0.2 instead of 2.0) and rejected every real lengthening link. Take the rise straight from the integer ports instead: `(Exit.y - Entry.y) / Grid3.one.y`.

### Stair lengthening must be exactly one level, not just the right sign

Adjacent stair flights step `(0, +2 m, +4 m)`: one level up per run-step. Checking only that the run direction and level direction share a sign also accepts `(0, +4 m, +4 m)` (two levels up over one run-step), which is a different flight hovering above, not the next step. The lengthening test therefore requires the level delta to equal exactly one flight's rise.
