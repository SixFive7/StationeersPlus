---
title: RotatableBehaviour
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/PowerTransmitterPlus/RESEARCH.md:735-736
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: RotatableBehaviour
related:
  - ./PowerTransmitter.md
  - ./WirelessPower.md
tags: [transforms, network]
---

# RotatableBehaviour

Vanilla game class providing the servo behavior that drives dish rotation on `WirelessPower` subclasses. Holds `TargetHorizontal` / `TargetVertical` setters and an existing networked delta-state that clients slew to.

## Servo delta-state
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0058.

`WirelessPower.SetLogicValue` is server-authoritative in vanilla. Our prefix runs on the server. The ensuing writes to `RotatableBehaviour.TargetHorizontal` / `TargetVertical` set `NetworkUpdateFlags |= 256`, which the existing delta-state serialization ships to clients. `WirelessPower.ProcessUpdate` reads the flag and writes those targets on the client; the client's local servo then slews the dish. No new `INetworkMessage`.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0058. No conflicts.

## Open questions

None at creation.
