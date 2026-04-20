---
title: Server-authoritative simulation
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/PowerTransmitterPlus/RESEARCH.md:51-58 (F0033, primary)
  - Plans/RepairPrototype/plan.md:424-430 (F0224)
related:
  - ../GameSystems/NetworkRoles.md
  - ../GameSystems/PowerTickThreading.md
  - ./SinglePlayerNetworkRole.md
tags: [network]
---

# Server-authoritative simulation

Stationeers runs simulation on the server. Clients receive synced state and display it; no simulation runs client-side. Simulation-tweaking patches only meaningfully execute on the server; clients run the patches but the underlying vanilla work does not happen there. Server-authoritative mods that don't add new prefabs or assets can be server-only installs.

## Problem
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

F0033 (Mods/PowerTransmitterPlus/RESEARCH.md:51-58, primary):

> Stationeers is **server-authoritative for simulation**. Only the server runs the power tick; clients receive synced state. The `DistanceCostPatches` (4 patches) only meaningfully execute on the server. Clients run the patches but they are no-ops because power-tick code doesn't run on clients.
>
> Detection: `Assets.Scripts.Networking.NetworkManager.IsServer` (true on host or single-player) and `NetworkManager.IsActive` (true in multiplayer either side). Single-player has `IsActive = false`.
>
> Client-side display values for the readouts are computed from already-synced game state (`OutputNetwork.CurrentLoad`, `_linkedReceiverDistance`) plus the host's `k` value. The `k` value is pushed via `DistanceConfigMessage` on `PlayerConnected` and on every `SettingChanged` event.
>
> Auto-aim rides entirely on pre-existing infrastructure: `SetLogicValue` is server-authoritative; `TargetHorizontal` / `TargetVertical` writes set `NetworkUpdateFlags |= 256` which the existing delta-state serialization ships to clients.

F0224 (Plans/RepairPrototype/plan.md:424-430) restates the rule at a higher level:

> - **Server-authoritative** model. Game simulation runs on server, clients receive updates.
> - **Server-only mods** (gameplay patches): Only need to be on the server. PerishableItems explicitly states this.
> - **Both-sides mods** (custom prefabs/assets): Clients need the mod to recognize new PrefabHash values.
> - **Key guard:** `if (!GameManager.IsServer) return;` at the top of every patch
> - **Power system** runs on a **background thread** (not main thread). Use `System.Random` not `UnityEngine.Random`. Use `UniTask.SwitchToMainThread()` for visual updates.
> - BepInEx installs on dedicated servers the same way (alongside `rocketstation_DedicatedServer.exe`).

## Solution / recipe
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Two design axes to decide before writing multiplayer-aware patches.

### Axis 1: server-only install or both-sides install?

- **Server-only** (PerishableItems, most gameplay rebalances): patches apply to vanilla prefabs. Clients see correct behavior through the existing sync because the server's computation already dominates.
- **Both-sides** (custom prefabs, Mirrored Devices): clients need the mod so their save-load knows the custom `PrefabHash` values. Missing mod on a client means the thing fails to load with unknown `PrefabHash`.

### Axis 2: where does the patch body run?

The guard `if (!GameManager.IsServer) return;` goes at the top of every patch whose body does simulation-affecting work. Client runs of the patch early-out.

Two important exceptions:

- **Single-player has `IsServer = true`** (single-player is the authority). See `./SinglePlayerNetworkRole.md`.
- **Client-side display values** computed from already-synced state (not re-simulation) are fine to compute without the guard; they are presentational, not authoritative.

### Axis 3: sync the configuration inputs

Server-authoritative simulation that depends on a mod-owned setting (e.g. a configurable `k` coefficient) must push the setting to clients so client-side display values match. The push happens on `PlayerConnected` (new client joins) and on every setting change. See `../Protocols/LaunchPadBoosterNetworking.md`.

### Axis 4: the power tick is on a background thread

When a patch hooks a method reachable from `PowerTick.ApplyState`, any Unity API call from the patch body hard-crashes. Use `MainThreadDispatcher` to bridge. See `../GameSystems/PowerTickThreading.md` and `./MainThreadDispatcher.md`.

F0224 captures the related random-number rule: use `System.Random` (managed, thread-safe with the right constructor) instead of `UnityEngine.Random` (main-thread-only).

## Cited verifications
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- F0033: primary source grounding the rule in `PowerTransmitterPlus`'s `DistanceCostPatches` implementation, including the client-side-display branch and the auto-aim delta-state observation.
- F0224: RepairPrototype plan restates the rule across a broader set of subsystems (power, random, dedicated-server install).

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; F0033 primary, F0224 generalizing.

## Open questions

None at creation.
