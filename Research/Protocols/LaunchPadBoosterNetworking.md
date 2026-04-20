---
title: LaunchPadBooster Networking
type: Protocols
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/PowerTransmitterPlus/RESEARCH.md:676-692
  - Mods/SprayPaintPlus/RESEARCH.md:211-213
  - Mods/SprayPaintPlus/RESEARCH.md:149-151
  - Mods/PowerTransmitterPlus/PowerTransmitterPlus/DistanceConfigSync.cs:66-72
related:
  - ../GameSystems/NetworkRoles.md
  - ../Patterns/SinglePlayerNetworkRole.md
  - ./PowerTransmitterPlusNetworking.md
  - ./SprayPaintPlusNetworking.md
  - ./EquipmentPlusNetworking.md
tags: [network, launchpad]
---

# LaunchPadBooster Networking

LaunchPadBooster's networking layer (`LaunchPadBooster.Networking`) exposes the mod-to-mod wire-format primitives that every StationeersPlus mod uses to send custom messages between host and clients. It wraps vanilla Stationeers networking with automatic compression, multi-packet splitting, and a mod-version handshake.

## Primitives
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`LaunchPadBooster.Networking.IModNetworking` exposes:
- `bool Required { get; set; }`: mod-version handshake rejects clients without matching install.
- `void RegisterMessage<T>() where T : INetworkMessage, new()`.

There are NO public connect/disconnect events; Harmony-patch `NetworkManager.PlayerConnected` as the documented workaround.

`Mod.SetMultiplayerRequired()` exists but is `[Obsolete(error: true)]`. Use `MOD.Networking.Required = true` instead.

`INetworkMessage` extension methods (in `LaunchPadBooster.Networking.ModNetworkingExtensions`):
- `void SendToHost()`: client -> server
- `void SendToClient(Client client)`: server -> specific client
- `void SendDirect(long connectionId, ConnectionMethod method)`: low-level
- `void SendAll(long excludeConnectionId)`: server -> all clients. Pass `0L` for "no exclusion"; there is no zero-arg overload.

`INetworkMessage.Process(long hostId)`: `hostId` is the connection ID of the peer who sent the message. On a client receiving a host broadcast, `hostId` = host's connection ID.

## Handshake / version
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`MOD.Networking.Required = true` tells LaunchPadBooster to reject connections from clients that do not have the mod, or have a different version. This ensures all players run the same wire format.

## Networking V2 benefits over vanilla piggybacking
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Moved from piggybacking on `ThingColorMessage` to LaunchPadBooster's dedicated message channels. V2 provides automatic compression, multi-packet splitting, and a version handshake. The handshake rejects mismatched mod versions, preventing wire-format desync.

## No public PlayerConnected event
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Host code that needs to push initial state to a newly joined client must Harmony-postfix `NetworkManager.PlayerConnected`. From `DistanceConfigSync.cs`:

```
// Hook the existing game event for "a client just finished connecting" so we
// can push the current k to them. LaunchPadBooster has no public event for
// this. The documented pattern (per LaunchPadBooster authors) is to Harmony-postfix
// NetworkManager.PlayerConnected. We re-broadcast to everyone on each
// connect rather than chase the new client's connectionId; the cost is one
// tiny float message per existing client per join, which is negligible.
```

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration. Primary source F0055 (PowerTransmitterPlus RESEARCH.md:676-692). Additional sources cited: F0025 (SprayPaintPlus RESEARCH.md:211-213), F0029c (SprayPaintPlus RESEARCH.md:149-151), F0312 (PowerTransmitterPlus/DistanceConfigSync.cs:66-72).

## Open questions

None at creation.
