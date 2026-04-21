---
title: PowerTransmitterPlus Networking
type: Protocols
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-21
sources:
  - Mods/PowerTransmitterPlus/RESEARCH.md:696-711
  - Mods/PowerTransmitterPlus/RESEARCH.md:715-732
  - Mods/PowerTransmitterPlus/PowerTransmitterPlus/DistanceConfigSync.cs:8-18
  - Mods/PowerTransmitterPlus/PowerTransmitterPlus/DistanceConfigMessage.cs:6-11
  - Mods/PowerTransmitterPlus/PowerTransmitterPlus/BeamVisualConfigSync.cs:6-10
related:
  - ./LaunchPadBoosterNetworking.md
  - ../GameSystems/NetworkRoles.md
  - ../Patterns/SinglePlayerNetworkRole.md
tags: [network, launchpad]
---

# PowerTransmitterPlus Networking

PowerTransmitterPlus ships two server-authoritative config sync protocols on top of LaunchPadBooster. The host pushes its authoritative config values (`DistanceCostFactor k`, beam visual settings) to all clients on every join and every `SettingChanged` event; clients use the synced values for display-only math while the simulation runs server-side.

## Distance-cost k sync
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

```
Host:
  On DistanceCostFactor.SettingChanged     -> DistanceConfigSync.BroadcastIfHost()
  On NetworkManager.PlayerConnected (postfix) -> BroadcastIfHost()
  BroadcastIfHost(): if IsServer, new DistanceConfigMessage{K=k}.SendAll(0L)

Client:
  DistanceConfigMessage.Process(hostId):
    if !IsServer, DistanceConfigSync.OnHostConfigReceived(K)
  OnHostConfigReceived(k): _syncedHostK = k

Effective k decision:
  !NetworkManager.IsActive  -> local (single-player)
  IsServer                  -> local (host)
  else (client)             -> _syncedHostK ?? local
```

### DistanceConfigSync rationale

```
// Server-authoritative distance-cost (k) sync.
//
// - Local config value is the source of truth ON THE HOST (and in single-player).
// - On a multiplayer client, the host's value is pushed via DistanceConfigMessage
//   on connect and on every subsequent SettingChanged event.
// - GetEffectiveK() returns the right value for the current side: host config
//   on host or single-player; synced (or local fallback) on client.
//
// The simulation patches (UsePower / GetUsedPower / ReceivePower / GetGeneratedPower)
// run only on the server, so the gameplay number is always the host's. Clients
// need this only so their tablet/IC10 readouts compute matching display values.
```

### DistanceConfigMessage purpose

```
// Server -> client message: pushes the host's authoritative DistanceCostFactor (k)
// value so client tablets/IC10 readouts compute the same MicrowaveSourceDraw and
// MicrowaveTransmissionLoss values that the server is simulating.
//
// Process() runs on the receiving side. On a client receiving from the host,
// hostId == NetworkManager._hostId (the host's connection ID).
```

## Visual config sync
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

```
Host:
  On BeamWidth/BeamColorHex/EmissionIntensity/
     StripeWavelength/ScrollSpeed/
     StripeTroughBrightness.SettingChanged -> BeamVisualConfigSync.BroadcastIfHost()
  On NetworkManager.PlayerConnected (postfix)   -> BroadcastIfHost()
  BroadcastIfHost(): if IsServer, new BeamVisualConfigMessage{...}.SendAll(0L)

Client:
  BeamVisualConfigMessage.Process(hostId):
    if !IsServer, BeamVisualConfigSync.OnHostConfigReceived(msg)
  OnHostConfigReceived(msg):
    store all values, set _received = true
    call BeamManager.InvalidateAllBeams() to force beam recreation
    (InvalidateAllBeams also destroys the cached stripe texture so the
     next beam rebuilds it from the new trough brightness value)

Effective value decision (per GetEffective* method):
  _received AND IsActive AND !IsServer -> synced value from host
  else                                 -> local config
```

### BeamVisualConfigSync rationale

Server-authoritative visual config sync. The host's beam visual settings always override client-local config in multiplayer. Mirrors the DistanceConfigSync pattern: host pushes values on connect and on every visual config change; clients store them and return them from GetEffective* methods.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

- 2026-04-20: page created from the Research migration. Primary sources: F0056 (distance-cost k protocol) and F0057 (visual config protocol). Additional sources cited: F0311 (DistanceConfigSync.cs class header), F0317 (DistanceConfigMessage.cs class header), F0366 (BeamVisualConfigSync.cs class header).
- 2026-04-21: visual config sync extended to carry `StripeTroughBrightness`. `BeamVisualConfigMessage` now serializes six fields (sixth float appended after `ScrollSpeed`). `BeamManager.InvalidateAllBeams` additionally destroys the cached `StripeTexture` so the next beam rebuilds it from the synced value.

## Open questions

None at creation.
