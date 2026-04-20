---
title: GameMessageFactory (vanilla network messages)
type: Protocols
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/EquipmentPlus/RESEARCH.md:184-185
  - Plans/EquipmentPlus/RESEARCH.md:229-235
  - Plans/EquipmentPlus/EquipmentPlus/ConfigCartridgePatches.cs:342-350
  - Plans/EquipmentPlus/EquipmentPlus/ConfigCartridgePatches.cs:316-322
  - Mods/SprayPaintPlus/SprayPaintPlus/NetworkPainterPatch.cs:38-44
related:
  - ../GameClasses/AdvancedTablet.md
  - ../GameClasses/InventoryManager.md
  - ../GameClasses/OnServer.md
  - ../GameClasses/Human.md
  - ./LaunchPadBoosterNetworking.md
tags: [network]
---

# GameMessageFactory (vanilla network messages)

Reference page for vanilla Stationeers network messages that mods ride directly rather than defining custom LaunchPadBooster messages. Covers the `SetLogicFromClient` routing rules, the `Interactable.State` -> `Mode` sync channel, and the reliability caveat around `hostId` on server-side message dispatch.

## SetLogicFromClient (vanilla)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`SetLogicFromClient` vanilla network message. Fields: `LogicId` (device ReferenceId), `LogicType`, `Value`. Server-side handler calls `Device.SetLogicValue`. No equivalent exists for `(LogicSlotType, slotIndex)` writes.

## WriteLogicValue multiplayer-safe routing
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Multiplayer-safe logic value write. On the server: call SetLogicValue directly (we are authoritative). On a client: send SetLogicFromClient to the server, which applies the change authoritatively and replicates to all clients.

## WriteLogicSlotValue limitation
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

```
/// <summary>
/// Multiplayer-safe logic slot value write.
///
/// Vanilla has SetLogicFromClient for LogicType writes, but NO equivalent network
/// message exists for (LogicSlotType, slotIndex) writes.  Therefore:
///   - Server: call Device.SetLogicValue(LogicSlotType, slotId, value) directly.
///   - Client: log a warning and skip.  This is a known limitation; a proper
///             fix would require a custom NetworkMessage or a mod-specific packet.
/// </summary>
```

## Active cartridge sync via vanilla Mode (Interactable.State)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`Mode` propagates through `Interactable.State` (vanilla networking). No custom message needed. `GetCartridge()` must be called manually after setting `Mode` because vanilla's setter does not call it. `WriteLogicValue` on client sends `SetLogicFromClient` to server. `WriteLogicSlotValue` on server calls directly; on client logs a warning.

## AttackWithMessage hostId unreliability
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Captures the painting player's Human ReferenceId when the server processes a remote client's AttackWithMessage. The hostId parameter from vanilla message dispatch is unreliable on the server (NetworkManager._hostId is only maintained client-side); AttackParentId in the message body is the authoritative source identifier.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration. Primary sources: F0126b (SetLogicFromClient vanilla message), F0126c (active cartridge sync via vanilla Mode + SetLogicFromClient routing). Additional sources: F0331 (WriteLogicSlotValue multiplayer limitation comment), F0377 (WriteLogicValue multiplayer-safe routing), F0386 (PaintAttackerTracker_Remote hostId unreliability).

## Open questions

None at creation.
