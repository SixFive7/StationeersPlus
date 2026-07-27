---
title: ClientDisconnected cleanup Prefix
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-27
sources:
  - Mods/SprayPaintPlus/RESEARCH.md:193-195 (F0029h, primary)
  - Mods/SprayPaintPlus/RESEARCH.md:87-90 (F0014)
  - Mods/SprayPaintPlus/SprayPaintPlus/CleanupPatches.cs:24-30 (F0326, original wording)
  - Mods/SprayPaintPlus/SprayPaintPlus/CleanupPatches.cs:25-47 (F0326, corrected wording)
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: NetworkBase.RemoveClient
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.NetworkServer.ClientDisconnected
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Client
related:
  - ../GameClasses/Client.md
  - ../GameSystems/NetworkRoles.md
  - ../GameSystems/PlayerIdentityAcrossRejoin.md
tags: [network, harmony]
---

# ClientDisconnected cleanup Prefix

Patches that clean up per-client state when a player disconnects must run as a Harmony Prefix on `NetworkServer.ClientDisconnected`. The reason is lookup, not lifetime. Vanilla's own `NetworkBase.RemoveClient` call takes the `Client` out of the static `NetworkBase.Clients` list before returning, and that list is what `Client.Find` scans, so the patch's `connectionId` argument stops resolving to anything the moment vanilla's body runs.

## Problem

<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

The patched method is `Assets.Scripts.NetworkServer.ClientDisconnected` (line 213891):

```csharp
public static void ClientDisconnected(long connectionId)
{
	Client client = Client.Find(connectionId);
	if (client != null)
	{
		client.SetState(ClientState.Disconnected);
		ConsoleWindow.Print("Client disconnected: " + client.ToStringOneLine());
		NetworkBase.RemoveClient(client);
	}
}
```

Its only argument is `connectionId`. Everything the method knows about the player it recovers through `Client.Find(connectionId)`.

`NetworkBase.RemoveClient` (line 39227, global namespace, not `Assets.Scripts.Networking`) and the `OnClientRemoved` hook it calls (line 39247):

```csharp
public static void RemoveClient(Client client)
{
	Clients.Remove(client);
	OnClientRemoved();
}

private static void OnClientRemoved()
{
	if (GameManager.IsBatchMode && Settings.CurrentData.AutoPauseServer && Clients.Count <= 0)
	{
		_lastClientLeaveCancellation.CancelAndInitialize();
		AutoSaveOnLastClientLeave(_lastClientLeaveCancellation.Token).Forget();
	}
}
```

The two are not adjacent in the decompile; `ClearClientsList` (39233) and `OnClientAdded` (39239) sit between them.

`RemoveClient` clears no field, destroys no `Human`, and severs no reference. It removes the instance from one static list and runs a batch-mode autosave hook. `Client.SetState` (line 212239) is equally inert:

```csharp
public void SetState(ClientState clientState)
{
	state = clientState;
}
```

So after vanilla's body has run, the `Client` instance is fully intact and every field on it still reads correctly. What stops working is the static lookup. `Client.Find(long connectionId)` (line 212286) scans `NetworkBase.Clients`:

```csharp
public static Client Find(long connectionId)
{
	if (Assets.Scripts.Networking.NetworkManager.HostClient != null && Assets.Scripts.Networking.NetworkManager.HostClient.connectionId == connectionId)
	{
		return Assets.Scripts.Networking.NetworkManager.HostClient;
	}
	foreach (Client client in NetworkBase.Clients)
	{
		if (client.connectionId == connectionId)
		{
			return client;
		}
	}
	return null;
}
```

Once the instance is out of `Clients` the scan finds nothing and `Find` returns null. A bare Postfix holding only `connectionId` therefore has no route back to the player. That, and not the destruction of any record, is why the cleanup has to be a Prefix.

Two details fall out of the same code:

- The `NetworkManager.HostClient` short-circuit runs ahead of the list scan, so the host's own record keeps resolving through `Find` even after `RemoveClient` has taken it out of `Clients`. The Prefix rule is about remote clients.
- `Client.Disconnect()` (line 212323) also calls `NetworkServer.ClientDisconnected(connectionId)`, so the patched method is the single choke point for both the transport-driven and the explicit disconnect paths.

## Solution / recipe

<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

Write the cleanup as a `[HarmonyPrefix]` on `NetworkServer.ClientDisconnected`. Resolve the `Client` while `Find` still works, resolve the player identity from it, and remove the per-player entries before returning. The Prefix runs, then vanilla's body sets the state, prints, and drops the instance from `Clients`, then control returns to the caller.

```csharp
[HarmonyPatch(typeof(NetworkServer), nameof(NetworkServer.ClientDisconnected))]
internal static class ClientDisconnectCleanupPatch
{
    [HarmonyPrefix]
    public static void Prefix(long connectionId)
    {
        Client client = Client.Find(connectionId);
        if (client == null)
            return;

        if (client.ClientId == 0UL)
            return;

        Human owner = Human.Find(client.ClientId);
        if (owner != null)
            PlayerModifiers.Remove(owner.ReferenceId);
    }
}
```

Three things this skeleton encodes:

- **Resolve the player through `Thing.OwnerClientId`, not through `Client.RegisteredHuman`.** `RegisteredHuman` is null for the whole session for any character that came out of a save, which is the normal case for every returning player on a dedicated server. The full argument and the reliable alternatives are on [Client](../GameClasses/Client.md).
- **Reject `clientId == 0` before calling `Human.Find`.** `OwnerClientId` is 0 on every unowned `Thing` in the world, so `Human.Find(0)` returns an arbitrary unowned `Human`. A `Client` that has not completed its handshake still has `ClientId == 0`.
- **Do not let the Prefix throw.** The deployed patch wraps its whole body, on the reasoning that an exception out of a prefix propagates instead of running the original, which would stop `NetworkBase.RemoveClient` from ever running and leave a dead client in the server's list, breaking disconnects for the base game and every other mod on the server.

A Prefix plus Postfix pair is also viable when the cleanup genuinely needs to run after vanilla: stash the `Client` reference from the Prefix in Harmony `__state` and act on it in the Postfix. Because `RemoveClient` mutates nothing on the instance, a reference captured before the call reads exactly the same afterwards. What cannot work is a Postfix that tries to re-derive the `Client` from `connectionId` on its own.

## Cited verifications

<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

The three findings below are the original provenance of this page. All three reach the correct conclusion (the cleanup must be a Prefix), but all three state the mechanism as "`RemoveClient` destroys the record". That wording is imprecise: nothing is destroyed. Read them for the rule, not for the mechanism.

F0029h (Mods/SprayPaintPlus/RESEARCH.md:193-195, primary):

> `NetworkServer.ClientDisconnected` calls `NetworkBase.RemoveClient` before returning. The `Client` record is gone by the time a Postfix runs, so the cleanup patch must be a Prefix.

F0014 (Mods/SprayPaintPlus/RESEARCH.md:87-90):

> - `ClientDisconnectCleanupPatch` (Prefix on `NetworkServer.ClientDisconnected`): Removes the disconnecting player's entry from `PlayerModifiers`. Must be a Prefix because vanilla's `RemoveClient` destroys the `Client` record before returning, making it unreachable in a Postfix.

F0326 (code comment, `CleanupPatches.cs:24-30`), original wording:

```text
    /// <summary>
    /// Cleans up PlayerModifiers dictionary when a client disconnects.
    /// Runs as a Prefix because NetworkServer.ClientDisconnected calls
    /// NetworkBase.RemoveClient before returning, making the Client record
    /// unreachable to a Postfix. We look up the disconnecting client's
    /// registered Human and remove the modifiers entry keyed by its ReferenceId.
    /// </summary>
```

The deployed source has since been corrected in place. `CleanupPatches.cs:29-34` now reads:

```text
    /// Runs as a Prefix because NetworkServer.ClientDisconnected calls
    /// NetworkBase.RemoveClient before returning. RemoveClient does not clear any
    /// field on the Client, but it does take it out of NetworkBase.Clients, which
    /// is the list Client.Find scans, so connectionId stops resolving to anything
    /// the moment vanilla's body runs. A Postfix would have no way back to the
    /// disconnecting player.
```

Summary of what each finding is good for:

- F0029h: primary statement of the ordering. Correct that the Prefix is required; the "record is gone" phrasing is wrong.
- F0014: SprayPaintPlus's patch catalog entry stating the Prefix rule with the concrete cleanup target (`PlayerModifiers`). Same imprecise mechanism, and it still describes `PlayerModifiers` as keyed off the registered `Human`, which the deployed patch no longer relies on for the notice budget.
- F0326: code comment on the deployed patch. Now carries the corrected mechanism.

## Verification history

<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

- 2026-04-20: page created from the Research migration; F0029h primary, with F0014 and F0326 corroborating.
- 2026-07-27: conflict on "what RemoveClient does to the Client record". Previous claim: RemoveClient destroys the Client record and severs its links. New finding: RemoveClient only removes the instance from NetworkBase.Clients and calls OnClientRemoved, leaving every field intact; only Client.Find stops resolving. Fresh validator verdict: B is correct. Result: rewrote the page intro, the "Problem" section, and the "Solution / recipe" section against verbatim 0.2.6403.27689 bodies for `NetworkServer.ClientDisconnected`, `NetworkBase.RemoveClient`, `OnClientRemoved`, `Client.SetState`, and `Client.Find(long)`; the practical conclusion (Prefix required, because `Client.Find(connectionId)` is the only route from the argument back to the player) is unchanged. Added the `NetworkManager.HostClient` short-circuit caveat, the `Client.Disconnect()` second entry point, and the Prefix-plus-`__state`-Postfix variant that the corrected mechanism makes viable. Kept the F0029h / F0014 / F0326 blocks verbatim under a note that their "destroys the record" wording is imprecise, and recorded F0326's corrected in-source wording alongside the original. Replaced the recipe's `Client.RegisteredHuman` lookup with `Human.Find(client.ClientId)` plus a zero-id guard, and linked the new [Client](../GameClasses/Client.md) page. Also corrected the namespace of `NetworkBase` (global, not `Assets.Scripts.Networking`) and of `NetworkServer` / `Client` (`Assets.Scripts`).

## Open questions

None.
