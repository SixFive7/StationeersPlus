---
title: LaunchPadBooster Networking
type: Protocols
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-23
sources:
  - Mods/PowerTransmitterPlus/RESEARCH.md:676-692
  - Mods/SprayPaintPlus/RESEARCH.md:211-213
  - Mods/SprayPaintPlus/RESEARCH.md:149-151
  - Mods/PowerTransmitterPlus/PowerTransmitterPlus/DistanceConfigSync.cs:66-72
  - BepInEx/plugins/StationeersLaunchPad/LaunchPadBooster.dll :: LaunchPadBooster.Networking.IJoinValidator
  - BepInEx/plugins/StationeersLaunchPad/LaunchPadBooster.dll :: LaunchPadBooster.Networking.ModNetworking
  - BepInEx/plugins/StationeersLaunchPad/LaunchPadBooster.dll :: LaunchPadBooster.Networking.ConnectionState
related:
  - ../GameSystems/NetworkRoles.md
  - ../Patterns/SinglePlayerNetworkRole.md
  - ./PowerTransmitterPlusNetworking.md
  - ./SprayPaintPlusNetworking.md
  - ./EquipmentPlusNetworking.md
tags: [network, launchpad]
---

# LaunchPadBooster Networking

LaunchPadBooster's networking layer (`LaunchPadBooster.Networking`) exposes the mod-to-mod wire-format primitives that every StationeersPlus mod uses to send custom messages between host and clients. It wraps vanilla Stationeers networking with automatic compression, multi-packet splitting, and a mod-version handshake.

## Primitives
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`LaunchPadBooster.Networking.IModNetworking` exposes:
- `bool Required { get; set; }`: mod-version handshake rejects clients without matching install.
- `void RegisterMessage<T>() where T : INetworkMessage, new()`.

There are NO public connect/disconnect events; Harmony-patch `NetworkManager.PlayerConnected` as the documented workaround.

`Mod.SetMultiplayerRequired()` exists but is `[Obsolete(error: true)]`. Use `MOD.Networking.Required = true` instead.

`INetworkMessage` extension methods (in `LaunchPadBooster.Networking.ModNetworkingExtensions`):
- `void SendToHost()`: client -> server
- `void SendToClient(Client client)`: server -> specific client
- `void SendDirect(long connectionId, ConnectionMethod method)`: low-level
- `void SendAll(long excludeConnectionId)`: server -> all clients. Pass `0L` for "no exclusion"; there is no zero-arg overload.

`INetworkMessage.Process(long hostId)`: `hostId` is the connection ID of the peer who sent the message. On a client receiving a host broadcast, `hostId` = host's connection ID.

## Handshake / version
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`MOD.Networking.Required = true` tells LaunchPadBooster to reject connections from clients that do not have the mod, or have a different version. This ensures all players run the same wire format.

## Networking V2 benefits over vanilla piggybacking
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Moved from piggybacking on `ThingColorMessage` to LaunchPadBooster's dedicated message channels. V2 provides automatic compression, multi-packet splitting, and a version handshake. The handshake rejects mismatched mod versions, preventing wire-format desync.

## No public PlayerConnected event
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Host code that needs to push initial state to a newly joined client must Harmony-postfix `NetworkManager.PlayerConnected`. From `DistanceConfigSync.cs`:

```
// Hook the existing game event for "a client just finished connecting" so we
// can push the current k to them. LaunchPadBooster has no public event for
// this. The documented pattern (per LaunchPadBooster authors) is to Harmony-postfix
// NetworkManager.PlayerConnected. We re-broadcast to everyone on each
// connect rather than chase the new client's connectionId; the cost is one
// tiny float message per existing client per join, which is negligible.
```

## IJoinValidator: custom per-mod join validation
<!-- verified: 0.2.6228.27061 @ 2026-04-23 -->

LaunchPadBooster exposes a second, per-mod handshake hook beyond the `Required` version gate. A mod implements `IJoinValidator` and assigns it to its own `ModNetworking.JoinValidator`. Both sides then serialize and read a custom payload during the connection-verify exchange; rejection closes the connection with a readable error.

The interface (`LaunchPadBooster.Networking.IJoinValidator`):

```csharp
public interface IJoinValidator
{
    void SerializeJoinValidate(RocketBinaryWriter writer);

    bool ProcessJoinValidate(RocketBinaryReader reader, out string error);
}
```

The per-mod-networking hookup point (`LaunchPadBooster.Networking.IModNetworking`, `ModNetworking`):

```csharp
public IJoinValidator JoinValidator { get; set; }
```

### Invocation flow

`IJoinValidator.SerializeJoinValidate` and `ProcessJoinValidate` are invoked strictly inside the Stationeers network connect / verify exchange. The invocation sites are Harmony patches on the vanilla `VerifyPlayer` and `VerifyPlayerRequest` messages, applied by LaunchPadBooster in `ModNetworking`:

```csharp
[HarmonyPatch(typeof(VerifyPlayerRequest), "Serialize")]
[HarmonyPostfix]
internal static void WriteJoinHeaderServer(RocketBinaryWriter writer)
{
    WriteJoinValidateHeader(writer);
}

[HarmonyPatch(typeof(VerifyPlayer), "Serialize")]
[HarmonyPostfix]
internal static void WriteJoinHeaderClient(RocketBinaryWriter writer)
{
    WriteJoinValidateHeader(writer);
}

[HarmonyPatch(typeof(VerifyPlayerRequest), "Deserialize")]
[HarmonyPostfix]
internal static void ReadJoinHeaderClient(RocketBinaryReader reader)
{
    ReadJoinValidateHeader(GetHostId(), reader);
}

[HarmonyPatch(typeof(VerifyPlayer), "Deserialize")]
[HarmonyPostfix]
internal static void ReadJoinHeaderServer(VerifyPlayer __instance, RocketBinaryReader reader)
{
    ReadJoinValidateHeader(__instance.OwnerConnectionId, reader);
}
```

`VerifyPlayerRequest` is the server-to-client prompt and `VerifyPlayer` is the client-to-server response during a remote join. Both are only exchanged when a remote client negotiates with a host; they do not fire in single-player, and they do not fire on the host loading its own world.

The write side calls `PrepareJoinValidateData` (`ModNetworking.cs` approximately lines 693-755), which iterates every `ModNetworking.Instances` entry and calls each installed validator's `SerializeJoinValidate` into a shared `JoinValidateCustomWriter`, framing each mod's bytes with an offset / length entry keyed by mod hash:

```csharp
internal static JoinValidateData PrepareJoinValidateData(ConnectionState connection)
{
    ...
    foreach (ModNetworking instance in Instances)
    {
        IJoinValidator joinValidator = instance.JoinValidator;
        if (joinValidator != null)
        {
            Mod mod2 = instance.mod;
            int position = joinValidateCustomWriter.Position;
            ...
            try
            {
                joinValidator.SerializeJoinValidate(joinValidateCustomWriter);
            }
            catch (Exception innerException)
            {
                string message = "Error serializing custom join validate for " + mod2.ID.Name + " " + mod2.ID.Version;
                connection.CloseWithError(message);
                throw new Exception(message, innerException);
            }
            list.Add(new JoinValidateModCustomData.Entry
            {
                ModHash = mod2.Hash,
                Offset = position,
                Length = joinValidateCustomWriter.Position - position
            });
        }
    }
    ...
}
```

The read side is `ConnectionState.DoJoinValidateModCustom` (`ConnectionState.cs` approximately lines 441-539). For each entry in the incoming payload, it looks up the matching local `ModNetworking` by mod hash, slices the frame, and calls the local validator:

```csharp
private bool DoJoinValidateModCustom(JoinValidateModCustomData custom)
{
    ...
    for (int i = 0; i < array.Length; i++)
    {
        JoinValidateModCustomData.Entry entry = array[i];
        hashSet.Add(entry.ModHash);
        if (ModNetworking.InstancesByHash.TryGetValue(entry.ModHash, out var value))
        {
            IJoinValidator joinValidator = value.JoinValidator;
            if (joinValidator != null)
            {
                ...
                RocketBinaryReader val = new RocketBinaryReader((Stream)new MemoryStream(array2, 0, entry.Length, writable: false));
                try
                {
                    try
                    {
                        if (!joinValidator.ProcessJoinValidate(val, out var error))
                        {
                            list3.Add((value.Hash, error));
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError((object)("Error running join validator for " + value.ModID));
                        Debug.LogException(ex);
                        list4.Add(value.Hash);
                    }
                    ...
                }
                ...
            }
        }
        ...
    }
    foreach (ModNetworking instance in ModNetworking.Instances)
    {
        if (instance.JoinValidator != null && !hashSet.Contains(instance.Hash))
        {
            list2.Add(instance.Hash);
        }
    }
    ...
}
```

Missing-validator lists `list` (remote did not ship this mod's frame) and `list2` (local has a validator the remote did not send) both cause connection failure, labelled as "server missing" / "client missing" via `NetworkManager.IsServer` (see `ConnectionState.cs` approximately lines 510-538).

### When the validator fires

- **Remote client joining a host**: fires on both sides. Host sends its `SerializeJoinValidate` bytes inside `VerifyPlayerRequest`; client reads them via `ProcessJoinValidate`. Client sends back its own bytes inside `VerifyPlayer`; host reads them via `ProcessJoinValidate`. Either side returning `false` causes `CloseWithError` with a concatenated reason list.
- **Host starting or loading its own world**: does NOT fire. The `VerifyPlayer` / `VerifyPlayerRequest` exchange is purely the remote-join handshake; there is no analogous self-verify for the host's own session start.
- **Pure single-player**: does NOT fire. Same reason as the host-own-world case. Single-player never issues the verify messages that the LaunchPadBooster postfixes piggyback on.

Practical consequence: `IJoinValidator` cannot be used to enforce a host-side restart-required check against the host's own loaded world. It only protects against remote-client mismatches. Host-side self-consistency must be solved by other means (for example, `RequireRestart` advisory UI in the settings panel, a scene-load check, or accepting a silent mismatch with documented behavior).

### Error surface

`ConnectionState.CloseWithError` is called with a multi-line string composed of each failing mod's `ProcessJoinValidate` error. The string includes `"server"` / `"client"` framing derived from `NetworkManager.IsServer` at the point of check, so the displayed message tells the player which side is missing or rejecting the value.

### Related: `IJoinPrefixSerializer` and `IVersionValidator`

`ModNetworking` also exposes `JoinPrefixSerializer` and `VersionValidator` hooks for the same general exchange, with different semantics. Not in scope on this page; see `IModNetworking` for the full surface.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-23 -->

- 2026-04-20: page created from the Research migration. Primary source F0055 (PowerTransmitterPlus RESEARCH.md:676-692). Additional sources cited: F0025 (SprayPaintPlus RESEARCH.md:211-213), F0029c (SprayPaintPlus RESEARCH.md:149-151), F0312 (PowerTransmitterPlus/DistanceConfigSync.cs:66-72).
- 2026-04-23: added "IJoinValidator: custom per-mod join validation" section. Verified against `LaunchPadBooster.dll` in game version 0.2.6228.27061 by decompilation of `IJoinValidator`, `IModNetworking`, `ModNetworking.PrepareJoinValidateData`, `ModNetworking` `VerifyPlayer` / `VerifyPlayerRequest` Harmony postfixes, and `ConnectionState.DoJoinValidateModCustom`. Finding: validator fires only on remote client-join exchanges; does NOT fire for host-own-world load or single-player.

## Open questions

None at creation.
