---
title: PlayerConnected message timing vs world-load on joining clients
type: Protocols
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-26
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Networking.NetworkManager.PlayerConnected
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.NetworkServer.ClientConnected
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.NetworkServer.VerifyConnection
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.NetworkBase.AddClient
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.NetworkClient.ProcessJoinData
  - $(StationeersPath)\BepInEx\plugins\StationeersLaunchPad\LaunchPadBooster.dll :: LaunchPadBooster.Networking.ModNetworking.SendMessageAll
  - $(StationeersPath)\BepInEx\plugins\StationeersLaunchPad\LaunchPadBooster.dll :: LaunchPadBooster.Networking.ModNetworking.WriteJoinSuffix
  - $(StationeersPath)\BepInEx\plugins\StationeersLaunchPad\LaunchPadBooster.dll :: LaunchPadBooster.Networking.ConnectionState.ReceiveMessage
related:
  - ./LaunchPadBoosterNetworking.md
  - ../GameSystems/NetworkRoles.md
tags: [network, launchpad]
---

# PlayerConnected message timing vs world-load on joining clients

Verbatim source-backed answer to "is it safe to broadcast an `INetworkMessage` from a Harmony postfix on `NetworkManager.PlayerConnected` and have a joining client resolve `Thing.Find(referenceId)` in its `Process()` callback?"

## Summary
<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

**No.** A broadcast from `NetworkManager.PlayerConnected` postfix on the host does not reach the joining client at all, because at that moment the joiner is not yet in `NetworkBase.Clients`. Even if it did, the LaunchPadBooster receive path would drop it because the connection is not yet `ConnectionStatus.Ready`.

The correct hook for "deliver per-Thing mod state to a joining client at world-snapshot time" is `LaunchPadBooster.Networking.IJoinSuffixSerializer`. Its `DeserializeJoinSuffix` runs after `ProcessThings` has populated the client's Thing registry, so `Thing.Find` resolves.

## Server-side join sequence
<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

`NetworkManager.PlayerConnected` is a `private static` method called from four sites in `NetworkManager.cs`: Steam P2P server (line 271), Steam P2P client (line 695), RocketNet `NewIncomingConnection` (line 731), RocketNet `ConnectionRequestAccepted` (line 737). All fire at TCP-accept time, before any verify handshake.

```csharp
// NetworkManager.cs:760
private static void PlayerConnected(long connectionId, ConnectionMethod connectionMethod)
{
    switch (NetworkRole)
    {
    case NetworkRole.Server:
        NetworkServer.ClientConnected(connectionId, connectionMethod);
        break;
    case NetworkRole.Client:
        NetworkClient.Connected(connectionId, connectionMethod);
        break;
    }
}
```

On the server side this dispatches to `NetworkServer.ClientConnected`, which only sends `VerifyPlayerRequest`. It does NOT add the client to any list:

```csharp
// NetworkServer.cs:191
public static void ClientConnected(long connectionId, ConnectionMethod connectionMethod)
{
    NetworkMessages.VerifyPlayerRequest message = new NetworkMessages.VerifyPlayerRequest
    {
        ClientConnectionID = connectionId,
        ClientConnectionMethod = connectionMethod,
        PasswordRequired = !string.IsNullOrEmpty(Settings.CurrentData.ServerPassword)
    };
    NetworkManager.SendNetworkMessageDirect(connectionId, connectionMethod, NetworkChannel.GeneralTraffic, message);
}
```

`NetworkBase.AddClient(client)` happens only later, inside `NetworkServer.VerifyConnection`, after the verify-player round-trip succeeds:

```csharp
// NetworkServer.cs:232
public static void VerifyConnection(long hostId, NetworkMessages.VerifyPlayer msg)
{
    Client client = new Client(hostId, msg.OwnerConnectionId, msg.ClientId, msg.Name, msg.ClientConnectionMethod);
    // ... blacklist / password / version checks ...
    NetworkBase.AddClient(client);   // ← only now in NetworkBase.Clients
    Achievements.AssessWelcomeAboard();
    JoiningClients.Enqueue(client);
    client.SetState(ClientState.Queued);
    if (!_processJoinQueueTaskRunning)
    {
        _processJoinQueueTaskRunning = true;
        ProcessJoinQueue().Forget();
    }
}
```

LaunchPadBooster transpiles `VerifyPlayer.Process` to redirect this `VerifyConnection` call through its own join-validate handshake (`ModNetworking.cs:227-237`). The handshake adds another round-trip; only after the LPB validate exchange completes does `ConnectionState` call `NetworkServer.VerifyConnection(GetHostId(), VerifyPlayer)`:

```csharp
// LaunchPadBooster.Networking.ConnectionState.cs:330-342
if (DoJoinValidateModList(joinValidateData.ModList) && DoJoinValidateModCustom(joinValidateData.ModCustom))
{
    Status = ConnectionStatus.Ready;
    if (NetworkManager.IsClient)
    {
        // client side: send our validate data back to server
        SendJoinValidateData();
    }
    else
    {
        // server side: complete the join
        NetworkServer.VerifyConnection(ModNetworking.GetHostId(), VerifyPlayer);
    }
}
```

Then `ProcessJoinQueue` packages the world snapshot (`PackageJoinData` calls `WorldManager.SerializeOnJoin`, `Thing.SerializeOnJoin` for every Thing, etc.) and ships it to the joiner via `NetworkChannel.PlayerJoin` fragments.

## Why `SendAll` from PlayerConnected misses the joiner
<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

`INetworkMessage.SendAll(0L)` resolves to `LaunchPadBooster.Networking.ModNetworkingExtensions` -> `ModNetworking.SendMessageAll`, which iterates `NetworkBase.Clients`:

```csharp
// LaunchPadBooster.Networking.ModNetworking.cs:913
internal unsafe static void SendMessageAll(INetworkMessage message, long excludeConnectionId)
{
    TypeID typeID = messageRegistry.TypeIDFor(message.GetType());
    sendAllList.Clear();
    for (int i = 0; i < NetworkBase.Clients.Count; i++)
    {
        Client val = NetworkBase.Clients[i];
        if ((int)val.state != 5 && val.connectionId != excludeConnectionId)
        {
            sendAllList.Add(GetConnection(val.connectionId));
        }
    }
    if (sendAllList.Count == 0) return;
    // ... encode + send ...
}
```

`NetworkBase.Clients` is populated only by `NetworkBase.AddClient`:

```csharp
// NetworkBase.cs:38
public static void AddClient(Client client)
{
    Clients.Add(client);
    OnClientAdded();
}
```

And `AddClient` is called only from `NetworkServer.VerifyConnection`, which fires after the verify-handshake round-trip, well after `PlayerConnected`. So at the moment a Harmony postfix on `NetworkManager.PlayerConnected` runs and calls `SendAll(0L)`, the joining client is NOT in `NetworkBase.Clients`. The broadcast goes only to peers that connected earlier; the new joiner is not among them.

Even if a message did reach the joiner before its connection was validated, the LPB receive path would drop it:

```csharp
// LaunchPadBooster.Networking.ConnectionState.cs:603-621
internal void ReceiveMessage()
{
    MessageHeader header;
    ArraySegment<byte> arraySegment = ReadIncoming<MessageHeader>(out header);
    TypeID typeID = header.TypeID;
    if (Status != ConnectionStatus.Ready)
    {
        Debug.LogWarning((object)$"unexpected {typeID} message on connection {ConnectionID} when status is {Status}");
        return;
    }
    // ... dispatch to message.Process(ConnectionID) ...
}
```

`ConnectionStatus.Ready` is set at the end of `ReceiveJoinValidateData` (`ConnectionState.cs:332`), which runs during the LPB validate handshake. That happens after both sides exchange validate data, which is after `PlayerConnected` on the host but before world snapshot transmission begins.

## The correct hook: IJoinSuffixSerializer
<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

LaunchPadBooster exposes `IJoinSuffixSerializer` as the documented hook for embedding mod state in the world-snapshot transmission:

```csharp
// LaunchPadBooster.Networking.IJoinSuffixSerializer.cs
public interface IJoinSuffixSerializer
{
    void SerializeJoinSuffix(RocketBinaryWriter writer);
    void DeserializeJoinSuffix(RocketBinaryReader reader);
}
```

It is wired through `IModNetworking.JoinSuffixSerializer { get; set; }`. Mods set `MOD.Networking.JoinSuffixSerializer = ...` in `OnAllModsLoaded` (alongside `JoinValidator`).

Host side: LPB Harmony-prefix `NetworkServer.PackageJoinData` then calls `WriteJoinSuffix(_joinWriter)` to write each registered mod's section, framed by `SectionedWriter` per-mod hash:

```csharp
// LaunchPadBooster.Networking.ModNetworking.cs:794
internal static void WriteJoinSuffix(RocketBinaryWriter writer)
{
    SectionedWriter sectionedWriter = new SectionedWriter(writer);
    foreach (ModNetworking instance in Instances)
    {
        IJoinSuffixSerializer joinSuffixSerializer = instance.JoinSuffixSerializer;
        if (joinSuffixSerializer != null)
        {
            sectionedWriter.StartSection(instance.Hash);
            joinSuffixSerializer.SerializeJoinSuffix(writer);
            sectionedWriter.FinishSection();
        }
    }
    sectionedWriter.Finish();
}
```

Client side: LPB transpiles `NetworkClient.ProcessJoinData` to replace the `AtmosphericsManager.DeserializeOnJoin(reader)` call with `ReadJoinSuffix(reader)`. The replacement reads each mod's sectioned data, then internally calls `AtmosphericsManager.DeserializeOnJoin` to preserve vanilla.

The position in `ProcessJoinData` matters: the suffix runs after `ProcessThings`, so all Things are already deserialized and `Thing.Find` will resolve. Verbatim sequence:

```csharp
// NetworkClient.cs:372
private static async UniTask ProcessJoinData()
{
    // ...
    GameManager.DeserializeGameTime(reader);
    GameManager.DeserializeTerrainSeed(reader);
    // ... LPB ReadJoinPrefix injected here ...
    WorldManager.DeserializeOnJoin(reader);
    await VoxelTerrain.LoadTerrain(...);
    // ... Vein, OrbitalSimulation, TerraForming, VoxelTerrain ...
    await StructureNetwork.DeserializeOnJoin(reader)...
    await CableNetwork.DeserializeOnJoin(reader)...
    // ... TraderContact, SpaceMap, Rocket, WorldLog, WorldObjectiveState ...
    await ProcessThings(reader)...                        // ← every Thing deserialized
    await RocketLog.DeserializeOnJoin(reader)...
    RoomController.DeserializeOnJoin(reader);
    await AtmosphericsManager.DeserializeOnJoin(reader)... // ← LPB transpile replaces this with ReadJoinSuffix; mod suffix sections then vanilla atmospherics
    StructureNetwork.StructureNetworksOnFinishedLoad();
    // ... GameManager.OnReadyToPlay, UpdateThingsOnGameStart (calls OnFinishedLoad on all Things) ...
    UpdateHandshakeState(HandshakeType.ClientReady);
}
```

Net: `IJoinSuffixSerializer.DeserializeJoinSuffix` runs after `ProcessThings` and before `Thing.OnFinishedLoad`. `Thing.Find(referenceId)` resolves at this point.

`IJoinPrefixSerializer` is the symmetric option that runs BEFORE `GameManager.DeserializeGameTime` (LPB transpile inserts it there). Use prefix when the mod data must precede the world snapshot; use suffix when the mod data must follow it (i.e. when Things must already exist for resolution).

## Practical implications for the StationeersPlus monorepo
<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

Two existing PowerTransmitterPlus sync flows broadcast on `NetworkManager.PlayerConnected` postfix:

- `DistanceConfigSync.BroadcastIfHost()` -> `DistanceConfigMessage.SendAll(0L)` (since v1.0.0)
- `BeamVisualConfigSync.BroadcastIfHost()` -> `BeamVisualConfigMessage.SendAll(0L)` (since v1.1.0)

By the source evidence above, neither broadcast reaches the joining client. Existing clients receive a redundant rebroadcast of values they already have; the new joiner falls back to its local BepInEx config values until the host changes a setting later (which triggers a `SettingChanged` rebroadcast that then catches the joiner because they're now in `NetworkBase.Clients`).

For PowerTransmitterPlus this is mostly invisible because the values are display-only on clients (the simulation runs server-authoritatively with the host's values). For the v1.6.0 auto-aim cache delivery to a joining client, the same pattern would be a real defect: the joining client's `MicrowaveAutoAimTarget` reads would return 0 until someone re-writes the LogicType. The fix is to switch that path to `IJoinSuffixSerializer`.

## Verification history

- 2026-04-26: page created. Originally fabricated by a sub-agent (Explore subagent that wrote a research page via Bash redirection without any actual decompile reads, citing only mod-source files in this repo as "evidence"). Replaced wholesale with verbatim decompile excerpts from `NetworkManager.cs`, `NetworkServer.cs`, `NetworkBase.cs`, `NetworkClient.cs`, and LaunchPadBooster's `ModNetworking.cs` and `ConnectionState.cs`. Game version 0.2.6228.27061. Decompile produced via `ilspycmd -p Assembly-CSharp.dll` and `ilspycmd -p LaunchPadBooster.dll`.

## Open questions

None at creation.
