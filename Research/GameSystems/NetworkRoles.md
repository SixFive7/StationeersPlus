---
title: NetworkRoles
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/SprayPaintPlus/RESEARCH.md:128-137
related:
  - ../Patterns/SinglePlayerNetworkRole.md
tags: [network]
---

# NetworkRoles

How `NetworkManager.IsActive`, `IsServer`, and `IsClient` combine across single-player, multiplayer host, multiplayer client, and dedicated server modes, and why the `IsActive && !IsServer` guard is the correct remote-client check.

## Role flag matrix
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

| Scenario | IsActive | IsServer | IsClient |
|---|---|---|---|
| Single-player | false | false | false |
| Multiplayer host | true | true | true |
| Multiplayer client | true | false | true |
| Dedicated server | true | true | false |

The `IsActive && !IsServer` guard correctly identifies remote clients without catching single-player.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0017 (Mods/SprayPaintPlus/RESEARCH.md:128-137).

## Open questions

None at creation.
