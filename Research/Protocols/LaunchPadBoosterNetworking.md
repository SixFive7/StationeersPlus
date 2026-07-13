---
title: LaunchPadBooster Networking
type: Protocols
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-14
sources:
  - Mods/PowerTransmitterPlus/RESEARCH.md:676-692
  - Mods/SprayPaintPlus/RESEARCH.md:211-213
  - Mods/SprayPaintPlus/RESEARCH.md:149-151
  - Mods/PowerTransmitterPlus/PowerTransmitterPlus/DistanceConfigSync.cs:66-72
  - BepInEx/plugins/StationeersLaunchPad/LaunchPadBooster.dll :: LaunchPadBooster.Networking.IJoinValidator
  - BepInEx/plugins/StationeersLaunchPad/LaunchPadBooster.dll :: LaunchPadBooster.Networking.ModNetworking
  - BepInEx/plugins/StationeersLaunchPad/LaunchPadBooster.dll :: LaunchPadBooster.Networking.ConnectionState
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 274266-274519 (RocketBinaryReader, complete), 274520-274839 (RocketBinaryWriter, complete), 272552 (namespace Assets.Scripts.Networking)
related:
  - ../GameSystems/NetworkRoles.md
  - ../Patterns/SinglePlayerNetworkRole.md
  - ../Patterns/BinaryStreamSafety.md
  - ../Patterns/Float16Quantization.md
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

## IJoinSuffixSerializer: join-time state snapshot to a joining client
<!-- verified: 0.2.6228.27061 @ 2026-05-28 -->

`IJoinSuffixSerializer` is the hook for shipping a block of per-mod state to a joining client as part of the world snapshot. It is distinct from `IJoinValidator` (a pass/fail handshake) and from live `INetworkMessage` broadcasts (which only reach already-connected clients). A plugin implements it and assigns `MOD.Networking.JoinSuffixSerializer = this`.

```csharp
public interface IJoinSuffixSerializer
{
    void SerializeJoinSuffix(RocketBinaryWriter writer);
    void DeserializeJoinSuffix(RocketBinaryReader reader);
}
```

Invocation (verified from `Mods/PowerTransmitterPlus/PowerTransmitterPlus/Plugin.cs:260-348`, `DistanceConfigSync.cs:9-23`, and `./PlayerConnectedThingFindTiming.md`):

- Host: `SerializeJoinSuffix` runs inside `NetworkServer.PackageJoinData` (LaunchPadBooster injects it into the join writer), appended to the world snapshot sent to the joiner.
- Client: `DeserializeJoinSuffix` runs inside `NetworkClient.ProcessJoinData`, at the position of the original `AtmosphericsManager.DeserializeOnJoin` call, which is AFTER `ProcessThings`. So every Thing is already deserialized and `Thing.Find` resolves device ids inside the deserializer.
- Fires only on a remote client join. Does NOT fire when the host loads its own world, nor in single-player (same gating as `IJoinValidator`).
- LaunchPadBooster length-prefixes each mod's section (SectionedWriter / Reader), so a schema change in one mod does not desync neighbours. Field write order in `SerializeJoinSuffix` MUST match read order in `DeserializeJoinSuffix`.

Why this and not a `NetworkManager.PlayerConnected` broadcast: that hook fires BEFORE the joiner is added to `NetworkBase.Clients`, so an `INetworkMessage.SendAll` from a `PlayerConnected` postfix reaches existing clients but never the new joiner. PowerTransmitterPlus used the `PlayerConnected` rebroadcast through v1.6.x and removed it in v1.7.0 in favour of `IJoinSuffixSerializer` for exactly this reason. The established split: live `SendAll` for runtime changes to already-connected clients, `IJoinSuffixSerializer` for the initial snapshot to a joiner.

## RocketBinaryWriter / RocketBinaryReader: the vanilla primitive surface
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

Every serializer hook on this page (`IJoinValidator.SerializeJoinValidate` / `ProcessJoinValidate`, `IJoinSuffixSerializer.SerializeJoinSuffix` / `DeserializeJoinSuffix`, and LaunchPadBooster message serialization generally) hands the mod a `RocketBinaryWriter` or `RocketBinaryReader`. These are VANILLA game types in `Assets.Scripts.Networking` (Assembly-CSharp; namespace declaration at 0.2.6403.27689 decompile line 272552), not LaunchPadBooster types:

- `public class RocketBinaryReader : RocketBinaryCore` (274266, spans 274266-274519): wraps a `Stream`; every multi-byte read goes through `ReadExactly` (274284), which throws `EndOfStreamException` on a short read. Reader methods are `virtual` (subclass hook `PostRead`, 274275).
- `public class RocketBinaryWriter : RocketBinaryCore` (274520, spans 274520-274839): writes into an `ArrayPool<byte>`-rented buffer (constructor takes `bufferSize`, 274549) guarded by `Ensure(count)` (274560-274566, throws `OverflowException("Buffer too small for message.")`). Writer methods are plain instance methods (hook `PreWrite`, 274556).

Float payloads round-trip through `WriteSingle` / `ReadSingle` as 4-byte little-endian IEEE 754 singles, verbatim:

```csharp
public void WriteSingle(float value)                     // RocketBinaryWriter, line 274644
{
    Ensure(4);
    int value2 = BitConverter.SingleToInt32Bits(value);
    BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(Position, 4), value2);
    Position += 4;
}

public virtual float ReadSingle()                        // RocketBinaryReader, line 274380
{
    Span<byte> span = stackalloc byte[4];
    ReadExactly(span);
    return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(span));
}
```

Complete primitive inventory at 0.2.6403.27689 (writer line / reader line): Boolean 274574/274300, Byte 274580/274305, SByte 274586/274315, Bytes 274592/274325, Int32 274602/274338, UInt32 274609/274345, Int16 274616/274352, UInt16 274623/274359, Int64 274630/274366, UInt64 274637/274373, Single 274644/274380, Double 274652/274392, FloatHalf 274715/274387 (half precision via `Mathf.FloatToHalf` on write and `RocketMath.HalfToFloat(ReadUInt16())` on read; rounding consequences on [Float16Quantization](../Patterns/Float16Quantization.md)), Quaternion 274660/274399 and QuaternionHalf 274668/274404 (the read side renormalizes: `.normalized`), Colour 274676/274409, AnimationCurve 274684/274414, Vector3 274701/274435 and Vector3Half 274720/274445, Vector3d 274708/274440 (doubles), WorldGrid 274727/274450 (stored as `Int16` of value/10), Grid3 274734/274455, String 274741/274460 (`Int32` byte-length prefix + UTF8; writer emits length -1 for null, reader returns `string.Empty` for -1 or 0 and throws `InvalidDataException` on other negatives), NetworkUpdateType 274763/274504, MessageType 274568/274279 (a `MessageFactory` type index byte), Ascii 274799/274509. Writer-side positioning extras: `Position` / `Length` properties (274530/274547), `Seek(int, SeekOrigin)` 274768, `SeekZero` 274784, `SeekEnd` 274789, `Seek(long, SeekOrigin)` 274794, plus buffer lifecycle (`ReturnBuffer` 274808, `Close` 274817, `Dispose` 274823, `Reset` 274829).

Mod consequence: a custom payload carrying floats writes `writer.WriteSingle(x)` on one side and reads `x = reader.ReadSingle()` on the other for a full 32-bit round trip; reach for the FloatHalf pair only when half-precision rounding is acceptable. Write order must equal read order field for field, and no try-catch goes around the calls ([BinaryStreamSafety](../Patterns/BinaryStreamSafety.md)): a swallowed exception leaves the stream position misaligned for every subsequent field.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-05-28 -->

- 2026-04-20: page created from the Research migration. Primary source F0055 (PowerTransmitterPlus RESEARCH.md:676-692). Additional sources cited: F0025 (SprayPaintPlus RESEARCH.md:211-213), F0029c (SprayPaintPlus RESEARCH.md:149-151), F0312 (PowerTransmitterPlus/DistanceConfigSync.cs:66-72).
- 2026-04-23: added "IJoinValidator: custom per-mod join validation" section. Verified against `LaunchPadBooster.dll` in game version 0.2.6228.27061 by decompilation of `IJoinValidator`, `IModNetworking`, `ModNetworking.PrepareJoinValidateData`, `ModNetworking` `VerifyPlayer` / `VerifyPlayerRequest` Harmony postfixes, and `ConnectionState.DoJoinValidateModCustom`. Finding: validator fires only on remote client-join exchanges; does NOT fire for host-own-world load or single-player.
- 2026-05-28: added "IJoinSuffixSerializer: join-time state snapshot to a joining client". Verified from PowerTransmitterPlus `Plugin.cs:260-348` (implements `IJoinSuffixSerializer`; `SerializeJoinSuffix` / `DeserializeJoinSuffix` ship the auto-aim cache + seven config values) and `DistanceConfigSync.cs:9-23`, cross-referenced with `./PlayerConnectedThingFindTiming.md`. Documents the host `PackageJoinData` / client `ProcessJoinData`-after-`ProcessThings` invocation points, the remote-join-only gating, and why it replaced the v1.6.x `PlayerConnected` rebroadcast (which fires before the joiner is in `NetworkBase.Clients`). Additive (the page previously documented only `IJoinValidator`, with `IJoinPrefixSerializer` noted out of scope); no existing claim contradicted, so no fresh validator.
- 2026-07-14: added "RocketBinaryWriter / RocketBinaryReader: the vanilla primitive surface" (game version 0.2.6403.27689), from the PowerGridPlus fault-hover session's float-payload check. Both classes read in full from the 0.2.6403.27689 decompile: `RocketBinaryReader : RocketBinaryCore` 274266-274519 (Stream-backed, `ReadExactly` 274284, virtual methods, `PostRead` hook 274275) and `RocketBinaryWriter : RocketBinaryCore` 274520-274839 (ArrayPool buffer, `Ensure` 274560-274566, `PreWrite` hook 274556); namespace `Assets.Scripts.Networking` confirmed at 272552. `WriteSingle` (274644-274650) and `ReadSingle` (274380-274385) quoted verbatim: 4-byte little-endian IEEE 754 via `BitConverter.SingleToInt32Bits` / `Int32BitsToSingle`, confirming full-precision float payloads for custom network messages (the FloatHalf pair at 274715 / 274387 remains the only half-precision path). Full primitive inventory recorded with paired line references. Additive; no existing claim contradicted (the page previously used these types in verbatim LaunchPadBooster excerpts without documenting the game-side surface). Top-level `verified_in` bumped to 0.2.6403.27689 for this section; earlier sections keep their 0.2.6228.27061 stamps and were not re-read this pass.

## Open questions

None at creation.
