---
title: ClientDisconnected cleanup Prefix
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/SprayPaintPlus/RESEARCH.md:193-195 (F0029h, primary)
  - Mods/SprayPaintPlus/RESEARCH.md:87-90 (F0014)
  - Mods/SprayPaintPlus/SprayPaintPlus/CleanupPatches.cs:24-30 (F0326)
related:
  - ../GameSystems/NetworkRoles.md
tags: [network, harmony]
---

# ClientDisconnected cleanup Prefix

Patches that clean up per-client state when a player disconnects must run as a Harmony Prefix on `NetworkServer.ClientDisconnected`. A Postfix cannot read the `Client` record because vanilla's own `RemoveClient` call destroys it before returning.

## Problem
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

F0029h (Mods/SprayPaintPlus/RESEARCH.md:193-195, primary):

> `NetworkServer.ClientDisconnected` calls `NetworkBase.RemoveClient` before returning. The `Client` record is gone by the time a Postfix runs, so the cleanup patch must be a Prefix.

Any code that looks up the disconnecting player's `Human` / `ReferenceId` / other identity from the `Client` must run BEFORE vanilla removes the record. After the vanilla call, the record is unreachable and the cleanup patch cannot find the identity to clean up.

## Solution / recipe
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Write the cleanup as a `[HarmonyPrefix]` on `NetworkServer.ClientDisconnected`. Read the `Client` record, resolve the `Human.ReferenceId`, and remove the per-player entries before returning. The Prefix runs, then vanilla's `RemoveClient` destroys the record, then control returns to the caller.

F0014 (Mods/SprayPaintPlus/RESEARCH.md:87-90):

> - `ClientDisconnectCleanupPatch` (Prefix on `NetworkServer.ClientDisconnected`): Removes the disconnecting player's entry from `PlayerModifiers`. Must be a Prefix because vanilla's `RemoveClient` destroys the `Client` record before returning, making it unreachable in a Postfix.

F0326 (code comment, `CleanupPatches.cs:24-30`):

```text
    /// <summary>
    /// Cleans up PlayerModifiers dictionary when a client disconnects.
    /// Runs as a Prefix because NetworkServer.ClientDisconnected calls
    /// NetworkBase.RemoveClient before returning, making the Client record
    /// unreachable to a Postfix. We look up the disconnecting client's
    /// registered Human and remove the modifiers entry keyed by its ReferenceId.
    /// </summary>
```

Skeleton:

```csharp
[HarmonyPatch(typeof(NetworkServer), nameof(NetworkServer.ClientDisconnected))]
internal static class ClientDisconnectCleanupPatch
{
    [HarmonyPrefix]
    public static void Prefix(Client client)
    {
        var human = /* resolve Human from client */;
        if (human != null)
            PlayerModifiers.Remove(human.ReferenceId);
    }
}
```

A Postfix that takes `Client client` still compiles; it just sees a record whose links have already been severed by `RemoveClient`, so lookups that depend on those links fail silently.

## Cited verifications
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- F0029h: primary statement of the ordering ("RemoveClient destroys the record before returning").
- F0014: SprayPaintPlus's patch catalog entry stating the Prefix rule with the concrete cleanup target (`PlayerModifiers`).
- F0326: code comment on the deployed patch reiterating the rationale.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; F0029h primary, with F0014 and F0326 corroborating.

## Open questions

None at creation.
