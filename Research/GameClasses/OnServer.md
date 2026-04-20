---
title: OnServer
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/SprayPaintPlus/RESEARCH.md:120-126
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: OnServer
related:
  - ./Human.md
tags: [network]
---

# OnServer

Vanilla static facade for server-side mutation entry points. Callers funnel gameplay actions (paint, damage, attack) through its methods so the server remains the authoritative simulator.

## SetCustomColor and AttackWith paths
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0029b.

`OnServer.SetCustomColor(...)` is called when a player paints something. `OnServer.AttackWith(attackParent, ...)` is the local path (host or single-player). `AttackWithMessage.Process(hostId)` is the remote client path. Both eventually reach `OnServer.SetCustomColor` if the attack involves a spray can.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0029b. No conflicts.

## Open questions

None at creation.
