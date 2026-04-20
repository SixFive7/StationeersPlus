---
title: SprayPaintPlus Networking
type: Protocols
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/SprayPaintPlus/RESEARCH.md:141-147
  - Mods/SprayPaintPlus/RESEARCH.md:153-160
  - Mods/SprayPaintPlus/RESEARCH.md:162-169
related:
  - ./LaunchPadBoosterNetworking.md
  - ../GameSystems/NetworkUpdateFlags.md
  - ../GameClasses/Consumable.md
  - ../GameClasses/OnServer.md
  - ../GameClasses/Human.md
tags: [network, launchpad]
---

# SprayPaintPlus Networking

SprayPaintPlus ships two custom LaunchPadBooster `INetworkMessage` types (client-to-server) to carry per-can color state and per-player modifier state, plus two server-authoritative sync flows that layer on top of vanilla `Consumable` update ticks and `AttackWithMessage` paths.

## Message types
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Two LaunchPadBooster `INetworkMessage` types, both client-to-server:

1. **SprayCanColorMessage**: `{ SprayCanId: long, ColorIndex: int }`. Sent when a client scrolls to change color. Server validates ColorIndex range, finds the SprayCan by ReferenceId, and applies the color. The update broadcasts to all clients via the normal `Consumable` network update path.

2. **PaintModifierMessage**: `{ Modifiers: byte, PlayerHumanId: long }`. Sent when modifier key state changes. Server stores in `PlayerModifiers[PlayerHumanId]`. Read during `NetworkPainterPatch.Prefix` to decide single/network/checkered mode.

## Sync flow for color changes
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

1. Client detects scroll in `ColorCyclerPatch.Prefix`.
2. Client updates the spray can's visual locally (immediate feedback).
3. Client sends `SprayCanColorMessage` to the server.
4. Server validates, applies color via `UpdateSprayCanServer`, which sets the visual and raises `NetworkUpdateFlags`.
5. Next tick, `Consumable.BuildUpdate` fires; the `ConsumableBuildUpdatePatch` postfix writes the color index into the stream.
6. All clients receive the update; `ConsumableProcessUpdatePatch` reads the color index and applies it visually.

## Sync flow for painting
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

1. Client attacks a structure with a spray can (vanilla input).
2. Vanilla sends `AttackWithMessage` to the server.
3. Server-side tracker prefix captures the Human ReferenceId.
4. Vanilla calls `OnServer.SetCustomColor` for the targeted item.
5. `NetworkPainterPatch.Prefix` intercepts, looks up modifiers for the painter, and paints the network/room/grid.
6. Each painted item's `SetCustomColor` sets its own `NetworkUpdateFlags`, broadcasting the color change to all clients through vanilla's update tick.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration. Sources: F0018 (message types), F0019 (color-change sync flow), F0020 (painting sync flow).

## Open questions

None at creation.
