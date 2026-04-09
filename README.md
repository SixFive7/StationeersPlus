# Spray Paint Plus [StationeersLaunchPad]

A combined Stationeers mod that brings together **color cycling**, **network painting**, and **infinite spray paint** into a single mod that now also fully supports multiplayer.

> **WARNING:** This is a StationeersLaunchPad mod. It requires [BepInEx](https://docs.bepinex.dev/) and [StationeersLaunchPad](https://github.com/StationeersLaunchPad/StationeersLaunchPad) to be installed.

This mod builds on the excellent work of **Elmotrix** ([Color Cycler](https://github.com/Elmotrix/ColorCyclerMod), [Network Painter](https://github.com/Elmotrix/NetworkPainter)) and **Aspct** ([Infinite Spray Paint](https://steamcommunity.com/sharedfiles/filedetails/?id=3576112002)), whose original mods inspired this project. The multiplayer networking code in Color Cycler was contributed by **SubHobo** (bls220). Spray Paint Plus combines their ideas and fixes the multiplayer issues that affected clients in those mods.

## Features

### Color Cycling
Scroll your mouse wheel while holding a spray can to cycle through all available paint colors. No more carrying twelve cans in a backpack.

### Network Painting
Spray-paint a pipe, cable, or chute and the entire connected network gets painted at once.
- **Hold Shift** to paint just a single item (or swap this default — see Settings)
- **Hold Ctrl** for a checkered/alternating paint pattern
- Works on: pipe networks (including hydroponic trays and passive vents as separate paint groups), cable networks, chute networks

### Infinite Spray Paint
All spray cans have unlimited uses and produce no pollution. Both are configurable.

### Full Multiplayer Support
All features work correctly for every player — host and clients alike. Late-joining players see the correct spray can colors immediately.

### Settings

All features are configurable via the mod settings panel.

**Client settings** (personal preference, each player sets independently):

| Setting | Default | Description |
|---|---|---|
| Invert Color Scroll Direction | Off | Reverse the scroll wheel direction |
| Paint Single Item By Default | Off | Swap Shift behavior — single paint by default, hold Shift for network paint |

**Server settings** (the server's value controls gameplay for everyone):

| Setting | Default | Description |
|---|---|---|
| Unlimited Spray Paint Uses | On | Infinite spray cans |
| Suppress Spray Paint Pollution | On | No pollutant gas when spraying |
| Enable Network Painting | On | Paint entire networks at once |
| Network Paint Pipes | On | Include pipes in network painting |
| Network Paint Cables | On | Include cables in network painting |
| Network Paint Chutes | On | Include chutes in network painting |

## Compatibility

**Requires:** BepInEx + StationeersLaunchPad

**Incompatible with** (detected at startup — the mod refuses to load if either is found):
- [Color Cycler](https://steamcommunity.com/sharedfiles/filedetails/?id=3163662298) by Elmotrix
- [Network Painter](https://steamcommunity.com/sharedfiles/filedetails/?id=2876605527) by Elmotrix

**Redundant** (not detected, but pointless to run alongside this mod — disable to avoid confusion):
- [Infinite Spray Paint](https://steamcommunity.com/sharedfiles/filedetails/?id=3576112002) by Aspct
- [Infinite Paint Mod](https://steamcommunity.com/sharedfiles/filedetails/?id=1761980496) by Dingo

The detection hooks into `Prefab.OnPrefabsLoaded` to guarantee all mod assemblies are loaded before checking, regardless of machine speed or mod count. If a conflict is found, no patches are applied and an error repeats every 5 seconds.

**All players** on a server must have Spray Paint Plus installed. LaunchPadBooster enforces matching mod versions during the connection handshake automatically.

## Installation

1. Copy `SprayPaintPlus.dll` and the `About/` folder into your Stationeers local mods directory
2. Disable the original Color Cycler, Network Painter, and Infinite Spray Paint mods
3. Restart the game

## How It Works

This section documents the technical approach for modders and contributors.

### Color Cycling

**Harmony Prefix** on `InventoryManager.NormalMode` detects scroll input while holding a SprayCan. The mod updates visual properties locally for immediate feedback, then sends a `SprayCanColorMessage` (a `ModNetworkMessage<T>` registered via LaunchPadBooster) to the server.

**What we do differently:** The original Color Cycler called `SetPrefab()` and swapped `PrefabHash`/`PrefabName` at runtime — changing the item's identity over the network. This is fragile because the game's replication system can overwrite it. Spray Paint Plus **never touches prefab identity**. It only updates rendering properties (`PaintableMaterial`, `PaintMaterial`, mesh material, and thumbnail) and tracks the selected color in a `Dictionary<long, int>` keyed by `ReferenceId`, which is immune to network sync overwrites.

### Network Messaging

The original mods piggybacked on the vanilla `ThingColorMessage` with sentinel values to distinguish color changes from modifier key state. Spray Paint Plus uses **two dedicated `ModNetworkMessage<T>` types** registered via LaunchPadBooster:

- `SprayCanColorMessage` — carries spray can ReferenceId + color index, validated at the trust boundary
- `PaintModifierMessage` — carries a single byte of modifier flags (shift/ctrl intent)

Each message has its own `Serialize`, `Deserialize`, and `Process` methods. No piggybacking, no sentinels, no interference with vanilla message processing.

### Modifier Key Sync

The original Network Painter read `KeyManager.GetButton()` on the server — which can't see client keyboards. Spray Paint Plus captures modifier keys **on the client**, applies the `PaintSingleItemByDefault` inversion locally (via boolean inequality), and sends the effective intent ("I want single paint" / "I want network paint") to the server via `PaintModifierMessage`. The server reads remote clients' intent from a per-player dictionary, and reads `KeyManager` directly for the local host player.

### Network Painting

**Harmony Prefix** on `OnServer.SetCustomColor` iterates the connected pipe/cable/chute network and paints all members. Key hardening:
- **Reentrancy guard** — a `static bool _painting` flag with try/finally prevents O(n^2) recursion when `SetCustomColor` re-enters the Prefix
- **Null checks** on all network references (PipeNetwork, CableNetwork, ChuteNetwork)
- **`.ToList()` snapshots** on all iterations to prevent collection-modified-during-enumeration
- **Per-item try/catch** via `PaintSafe()` so one destroyed item doesn't abort the whole network
- **Skips the original thing** (`ReferenceEquals` check) — vanilla paints the target after the Prefix returns
- **`CheckeredCheck`** casts `Mathf.Round()` to `int` before modulo to avoid float imprecision

### State Sync

**Harmony Postfixes** on `Consumable.BuildUpdate`/`ProcessUpdate` using network flag `0x1000` (GenericFlag2) handle server-to-client replication. **Postfixes** on `Consumable.SerializeOnJoin`/`DeserializeOnJoin` handle late-joining clients — a feature none of the original mods had.

### Infinite Paint & Pollution

**Harmony Prefix** on `SprayCan.OnUseItem` with a server-only guard (`!NetworkManager.IsServer` returns early). Reads `UnlimitedSprayPaintUses` and `SuppressSprayPaintPollution` configs. The four combinations are independent: infinite paint controls consumption, pollution control is separate.

### Conflict Detection

StationeersLaunchPad bypasses BepInEx's Chainloader, so `[BepInIncompatibility]` attributes alone don't work. The mod subscribes to `Prefab.OnPrefabsLoaded` (which fires after SLP finishes loading all mods), then scans `AppDomain.CurrentDomain.GetAssemblies()` for `ColorCycler.dll` and `NetworkPainter.dll`. If found, no patches are applied and an error repeats every 5 seconds.

### Cleanup

- `Thing.OnDestroy` Postfix removes spray can entries from the color tracking dictionary
- `NetworkServer.ClientDisconnected` Postfix removes departed players' modifier state

## Credits

Spray Paint Plus would not exist without the modders who came before:

- **Elmotrix** — Created [Color Cycler](https://github.com/Elmotrix/ColorCyclerMod) and [Network Painter](https://github.com/Elmotrix/NetworkPainter), the original spray paint enhancement mods for Stationeers. The core ideas of scroll-to-cycle and paint-entire-networks are theirs.
- **SubHobo** (bls220) — Contributed the initial multiplayer networking code to Color Cycler via [PR #1](https://github.com/Elmotrix/ColorCyclerMod/pull/1), including the `ThingColorMessage` approach and the `BuildUpdate`/`ProcessUpdate` serialization that this mod builds on.
- **Aspct** — Created [Infinite Spray Paint](https://steamcommunity.com/sharedfiles/filedetails/?id=3576112002), the original clean infinite paint mod for Stationeers. Spray Paint Plus reimplements the feature via Harmony patching.
- **Dingo (DingoPD)** — Created the original [Infinite Paint Mod](https://steamcommunity.com/sharedfiles/filedetails/?id=1761980496), the first infinite spray paint mod for Stationeers.
