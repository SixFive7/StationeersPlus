---
title: Single-player NetworkRole trap
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/SprayPaintPlus/RESEARCH.md:189-191 (F0029g, primary)
  - Mods/SprayPaintPlus/SprayPaintPlus/SprayCanUsePatch.cs:18-23 (F0324)
related:
  - ../GameSystems/NetworkRoles.md
  - ./ServerAuthoritativeSimulation.md
tags: [network]
---

# Single-player NetworkRole trap

Single-player runs with `NetworkRole.None`: all three of `NetworkManager.IsActive`, `NetworkManager.IsServer`, and `NetworkManager.IsClient` are false. Guards written as "skip remote clients" using `!IsServer` accidentally also skip single-player, breaking mod features for solo players.

## Problem
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

F0029g (Mods/SprayPaintPlus/RESEARCH.md:189-191):

> Single-player runs with `NetworkRole.None`: `IsActive`, `IsServer`, and `IsClient` are all false. Guards that check `!IsServer` to skip remote clients will also skip single-player. The correct remote-client check is `IsActive && !IsServer`.

A naive guard of the form

```csharp
if (!NetworkManager.IsServer) return;  // "skip if we're not the authority"
```

means "skip if we are not the server." In multiplayer, server = host = authoritative. In single-player, the game is ALSO the authority, but `IsServer` is false. The guard skips legitimate work.

Conversely a guard "skip remote clients only" written as

```csharp
if (!NetworkManager.IsServer) return;  // INTENDED: "skip remote clients"
```

also traps single-player because single-player doesn't match `IsServer = true` either.

## Solution / recipe
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

The authoritative `NetworkRole` flag matrix lives on [../GameSystems/NetworkRoles.md](../GameSystems/NetworkRoles.md#role-flag-matrix). Consult it for the exact `IsActive` / `IsServer` / `IsClient` values across single-player, multiplayer host, multiplayer client, and dedicated server modes.

The correct filter for "remote client only" is `IsActive && !IsServer`. The correct filter for "authoritative (host or single-player)" is `IsServer || !IsActive`.

F0324 (code comment, `SprayCanUsePatch.cs:18-23`):

```text
            // Skip only on multiplayer remote clients. Their authoritative
            // quantity is broadcast by the server, so running this locally
            // would briefly show paint consumed before the sync corrects it.
            // Single-player has NetworkRole.None (IsActive=false, IsServer=false),
            // which the earlier `!IsServer` guard conflated with remote clients
            // and accidentally disabled infinite spray in solo play.
```

Rule of thumb when writing any network-aware guard:

1. Name the intent: "remote clients only," "server-authoritative work," "any connected node."
2. Map the intent through the `NetworkRole` matrix including single-player.
3. If a plain `!IsServer` or `!IsClient` handles single-player incorrectly, add `IsActive` to the conjunction.

## Cited verifications
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- F0029g: primary rule statement and the correct `IsActive && !IsServer` form.
- F0324: patch code comment documenting the concrete regression (infinite-spray broken in solo) that motivated the fix.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; F0029g primary, F0324 confirming with a regression narrative.
- 2026-04-20: removed derivative NetworkRole matrix that had drifted (said MP host IsClient=false; authoritative F0017 says true). Replaced with link to GameSystems/NetworkRoles.md per Phase 6 Pass B flag.

## Open questions

None at creation.
