# Spray Paint Plus: Internals

## Architecture

Spray Paint Plus is a BepInEx plugin for Stationeers, loaded via StationeersLaunchPad. It uses Harmony patches to intercept game methods and LaunchPadBooster for multiplayer message transport.

The mod is server-authoritative. Clients send input events (color scroll, modifier keys) to the server. The server applies paint and broadcasts results through the game's normal network update system.

### Dependencies

- **BepInEx**: Plugin loader and configuration framework.
- **StationeersLaunchPad**: Mod manager that loads BepInEx plugins into Stationeers. Provides `Prefab.OnPrefabsLoaded` for deferred initialization.
- **LaunchPadBooster**: Networking layer on top of StationeersLaunchPad. Provides `INetworkMessage`, channel-based message transport, automatic compression, and a version-matching handshake (`Networking.Required = true`).

### Conflict detection

The mod replaces Color Cycler and Network Painter. It cannot coexist with them because they patch the same methods. `BepInIncompatibility` attributes cover load-time detection, but StationeersLaunchPad loads mods progressively, so those assemblies may not exist when `Awake()` runs. A second check runs on `Prefab.OnPrefabsLoaded`, scanning `AppDomain.CurrentDomain.GetAssemblies()` for the conflicting assembly names. If found, the mod logs a fatal error and starts a coroutine that repeats the warning every 5 seconds. No Harmony patches are applied.

## File walkthrough

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

## Patch catalog

### ColorCyclerPatch (Prefix on `InventoryManager.NormalMode`)

Runs every frame. Checks if the active hand holds a `SprayCan`. If scroll input is nonzero, computes the next color index (wrapping), updates the can's visual locally, and sends a `SprayCanColorMessage` to the server. Also checks modifier key state each frame and sends `PaintModifierMessage` on change.

The color index wraps with simple `+1`/`-1` and bounds check. `InvertColorScrollDirection` flips the scroll direction. `PaintSingleItemByDefault` XORs with Shift before encoding bit 0 of the modifier byte.

### NetworkPainterPatch (Prefix on `OnServer.SetCustomColor`)

Core network/room/grid paint logic. Only runs on the server (or in single-player, which is also "server" from the game's perspective after the `IsActive` guard fix).

**Reentrancy guard**: A static `_painting` bool prevents recursive invocation. Each `PaintSafe` call invokes `item.SetCustomColor`, which re-enters this prefix. Without the guard, one paint would cascade into an infinite loop.

**Modifier lookup**: Reads from `SprayPaintHelpers.PlayerModifiers` keyed by the Human ReferenceId captured by the tracker patches. Bit 0 = single-item mode (skip network paint). Bit 1 = checkered pattern.

**Paint branches** (checked in order, first match wins):

1. **Pipes**: `HydroponicTray`, `PassiveVent`, and `Pipe` each get their own sub-branch. Trays and passive vents are subtypes of Pipe but paint only within their own type. The pipe branch excludes trays and passive vents.
2. **Cables**: Floods `CableNetwork.CableList`.
3. **Chutes**: Floods `ChuteNetwork.StructureList`.
4. **Walls**: Floods by `Room` membership. Scans `room.Grids` plus one orthogonal-neighbor expansion layer (walls sit on room boundaries, not inside). Filters to exact type match and same `GetRoom()` result.
5. **Large structures**: BFS flood-fill on the world grid using 6-neighbor (cardinal) adjacency. `Cell.NeighborCells` returns all 26 neighbors (including diagonals); `IsOrthogonalNeighbor` filters to axis-aligned only by checking that exactly one axis differs.

Wall branch must precede Large Structure because `Wall` derives from `LargeStructure`. A wall with walls-painting disabled returns early and does not fall through to the grid flood.

### SprayCanUsePatch (Prefix on `SprayCan.OnUseItem`)

Four-combination matrix of `infinite` x `suppressPollution`:

| infinite | suppress | Behavior |
|---|---|---|
| true | true | Set quantity to 0, skip vanilla entirely. No consumption, no gas. |
| true | false | Set quantity to 0, let vanilla run. No consumption, gas still emits. |
| false | true | Leave quantity alone, skip vanilla, subtract quantity manually. Normal consumption, no gas. |
| false | false | Let vanilla run unmodified. |

Guard: `if (NetworkManager.IsActive && !NetworkManager.IsServer) return true`. This skips only multiplayer remote clients. Single-player has `NetworkRole.None` where both `IsActive` and `IsServer` are false, so the condition is false and the patch runs.

### ConsumableSyncPatch (Postfixes on `Consumable` serialization)

Appends one `Int32` (the color index) after the vanilla `Consumable` data in both the per-tick update stream (`BuildUpdate`/`ProcessUpdate`) and the join snapshot (`SerializeOnJoin`/`DeserializeOnJoin`). Uses `SprayPaintHelpers.PaintColorNetworkFlag` (bit 12, `GenericFlag2`) to gate the per-tick write/read.

No try-catch wraps these calls. If the read/write throws, catching it would leave the binary stream at the wrong position, corrupting all subsequent data for that object. Letting it propagate is the safer choice.

### PaintAttackerTracker (Prefix/Postfix on `OnServer.AttackWith` and `AttackWithMessage.Process`)

Two patches capture the painting player's identity before the paint reaches `SetCustomColor`:

- **Local** (`OnServer.AttackWith`): `attackParent` is the player's Human. Store its `ReferenceId`.
- **Remote** (`AttackWithMessage.Process`): `AttackParentId` from the message body is the Human ReferenceId. The `hostId` parameter (LaunchPadBooster connection id) is unreliable on the server.

Both postfixes reset `CurrentPaintingHumanId` to -1. The `NetworkPainterPatch.Prefix` also resets it after reading, as a guard against stale values if an earlier tracker postfix was skipped due to an exception.

### CleanupPatches

- `ThingDestroyCleanupPatch` (Postfix on `Thing.OnDestroy`): Removes destroyed spray cans from `SprayCanColors`.
- `ClientDisconnectCleanupPatch` (Prefix on `NetworkServer.ClientDisconnected`): Removes the disconnecting player's entry from `PlayerModifiers`. Must be a Prefix because vanilla's `RemoveClient` destroys the `Client` record before returning, making it unreachable in a Postfix.

## Game internals the mod hooks into

### SprayCan

- `PaintMaterial` / `PaintableMaterial`: The `Material` representing the can's current color. The game has one `SprayCan` prefab per color.
- `Thumbnail`: The `Sprite` shown in the inventory slot. Tied to the prefab, so switching color requires updating it manually.
- `Quantity`: Decremented on each use. Setting it to 0 before vanilla runs effectively makes the can infinite.

### CustomColors

`GameManager.Instance.CustomColors` is a `List<CustomColor>` where each entry has a `.Normal` material. The index into this list is the canonical color identifier used throughout the mod.

### Grid3

World coordinates scaled by `Grid3.one` (10 units per world unit). Structures snap to a grid defined by their `GridSize` (2 world units for walls and large structures by default). One cell spans `GridSize * Grid3.one.x` Grid3 units (20 by default). Every grid-aligned structure's `GridPosition` is a multiple of this cell size plus a fixed offset.

### Cell and NeighborCells

`Cell.NeighborCells` returns all 26 surrounding cells (corners and diagonals included). The mod filters to 6 orthogonal neighbors by checking that exactly one axis of the `Grid3` difference is nonzero.

### Room

`RoomController.World.GetRoom(gridPosition)` returns the `Room` a cell belongs to. `room.Grids` lists the room's interior cells. Walls sit on the boundary (one layer outside `room.Grids`), which is why the wall-painting code expands one neighbor layer outward.

### NetworkUpdateFlags

`Thing.NetworkUpdateFlags` is a bitmask. Setting a bit causes the game's next network tick to include that object in the update broadcast. The mod uses bit 12 (`0x1000`, `GenericFlag2`) for spray can color updates. This piggybacks on the existing `Consumable.BuildUpdate`/`ProcessUpdate` serialization.

### OnServer.SetCustomColor

Called when a player paints something. The mod's prefix intercepts this to flood-fill connected items before (or instead of) the vanilla single-item paint.

### OnServer.AttackWith / AttackWithMessage

`OnServer.AttackWith(attackParent, ...)` is the local path (host or single-player). `AttackWithMessage.Process(hostId)` is the remote client path. Both eventually reach `OnServer.SetCustomColor` if the attack involves a spray can. The mod's tracker patches extract the player identity from these calls.

### NetworkManager role flags

| Scenario | IsActive | IsServer | IsClient |
|---|---|---|---|
| Single-player | false | false | false |
| Multiplayer host | true | true | true |
| Multiplayer client | true | false | true |
| Dedicated server | true | true | false |

The `IsActive && !IsServer` guard correctly identifies remote clients without catching single-player.

## Multiplayer protocol

### Messages

Two LaunchPadBooster `INetworkMessage` types, both client-to-server:

1. **SprayCanColorMessage**: `{ SprayCanId: long, ColorIndex: int }`. Sent when a client scrolls to change color. Server validates ColorIndex range, finds the SprayCan by ReferenceId, and applies the color. The update broadcasts to all clients via the normal `Consumable` network update path.

2. **PaintModifierMessage**: `{ Modifiers: byte, PlayerHumanId: long }`. Sent when modifier key state changes. Server stores in `PlayerModifiers[PlayerHumanId]`. Read during `NetworkPainterPatch.Prefix` to decide single/network/checkered mode.

### Version handshake

`MOD.Networking.Required = true` tells LaunchPadBooster to reject connections from clients that do not have the mod, or have a different version. This ensures all players run the same wire format.

### Sync flow for color changes

1. Client detects scroll in `ColorCyclerPatch.Prefix`.
2. Client updates the spray can's visual locally (immediate feedback).
3. Client sends `SprayCanColorMessage` to the server.
4. Server validates, applies color via `UpdateSprayCanServer`, which sets the visual and raises `NetworkUpdateFlags`.
5. Next tick, `Consumable.BuildUpdate` fires; the `ConsumableBuildUpdatePatch` postfix writes the color index into the stream.
6. All clients receive the update; `ConsumableProcessUpdatePatch` reads the color index and applies it visually.

### Sync flow for painting

1. Client attacks a structure with a spray can (vanilla input).
2. Vanilla sends `AttackWithMessage` to the server.
3. Server-side tracker prefix captures the Human ReferenceId.
4. Vanilla calls `OnServer.SetCustomColor` for the targeted item.
5. `NetworkPainterPatch.Prefix` intercepts, looks up modifiers for the painter, and paints the network/room/grid.
6. Each painted item's `SetCustomColor` sets its own `NetworkUpdateFlags`, broadcasting the color change to all clients through vanilla's update tick.

## Pitfalls

### Reentrancy in SetCustomColor

`PaintSafe` calls `item.SetCustomColor`, which re-enters the `NetworkPainterPatch` prefix. The `_painting` static bool prevents infinite recursion. Without it, painting one pipe would try to paint the whole network for every pipe in the network.

### NotImplementedException on batched structures

Some structures use `structureRenderMode != Standard` and share a combined mesh. `SetCustomColor` throws `NotImplementedException` on these. `PaintSafe` catches the exception per-item so one unpaintable structure does not abort the rest of the network.

### Grid3 parity trap

`Grid3` scales world coordinates by 10. Walls and large structures snap to a 2-world-unit cell grid, so every grid-aligned structure's `GridPosition` is a multiple of 20 Grid3 units. Naive `(x+y+z) % 2` parity is the same for every structure. The checkered check works on the delta between two positions divided by cell size, which gives the cell-index distance. Parity of that distance is the checker answer.

### Wall vs. LargeStructure inheritance

`Wall` extends `LargeStructure`. The wall branch in `PaintNetwork` must come first. If walls-painting is disabled for a wall target, the method returns early rather than falling through to the large-structure grid flood.

### Single-player NetworkRole

Single-player runs with `NetworkRole.None`: `IsActive`, `IsServer`, and `IsClient` are all false. Guards that check `!IsServer` to skip remote clients will also skip single-player. The correct remote-client check is `IsActive && !IsServer`.

### ClientDisconnected cleanup timing

`NetworkServer.ClientDisconnected` calls `NetworkBase.RemoveClient` before returning. The `Client` record is gone by the time a Postfix runs, so the cleanup patch must be a Prefix.

### ConsumableSyncPatch exception handling

No try-catch wraps the binary read/write. If one side writes an Int32 and the other side's read fails mid-stream, catching the exception would leave the `RocketBinaryReader` at the wrong position. Every subsequent field for that object (and potentially the entire update packet) would be misaligned. Letting the exception propagate allows the game's connection-reset logic to recover cleanly.

## Design decisions

### Combined mod vs. separate mods

The three original mods (Color Cycler, Network Painter, Infinite Spray Paint) each patched overlapping methods and had no coordination. Running them together caused double-patching, ordering issues, and broken multiplayer. Combining them into one mod eliminates patch conflicts and allows shared state (e.g., modifier tracking feeds into network painting).

### Server-authoritative paint

All paint logic runs on the server. Clients send input (scroll, modifiers) and the server decides what gets painted. This prevents desync and means the server's config toggles are the single source of truth.

### LaunchPadBooster Networking V2

Moved from piggybacking on `ThingColorMessage` to LaunchPadBooster's dedicated message channels. V2 provides automatic compression, multi-packet splitting, and a version handshake. The handshake rejects mismatched mod versions, preventing wire-format desync.

### Human ReferenceId for player identification

The original mods used the LaunchPadBooster connection id to track which player pressed which modifier keys. But `AttackWithMessage` on the server does not carry the LaunchPadBooster connection id; it carries `AttackParentId`, which is the Human ReferenceId. Keying `PlayerModifiers` by Human ReferenceId matches the identifier available at paint time.

### GenericFlag2 for color sync

Bit 12 of `NetworkUpdateFlags` (`GenericFlag2`) was chosen because it is unused by `Consumable`'s vanilla serialization. Setting this flag triggers a network update that includes the spray can's data, and the postfix patches append the color index to that data.
