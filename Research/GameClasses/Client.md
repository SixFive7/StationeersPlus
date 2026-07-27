---
title: Client
type: GameClasses
created_in: 0.2.6403.27689
verified_in: 0.2.6403.27689
verified_at: 2026-07-27
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Client
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.FragmentHandler
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Thing.OwnerClientId
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Entities.Human
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Entities.Entity.GetClientEntity
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Util.Commands.CleanupPlayersCommand
related:
  - ../Patterns/ClientDisconnectedPrefix.md
  - ./Human.md
  - ./Entity.md
  - ./NetworkChannel.md
  - ../Patterns/UnityFakeNull.md
tags: [network, entity, unity]
---

# Client

Vanilla game class at `Assets.Scripts.Client` (line 212198), the server's per-connection record for one connected player. It is a plain managed class, not a `MonoBehaviour` and not a `Thing`.

The headline fact for mods: **`Client.RegisteredHuman` is not a reliable way to get from a connection to a player's character.** On a dedicated server it is null for the entire session for any player whose character came out of the save, which is the normal case for every returning player. Use `Thing.OwnerClientId` instead. The argument is in "RegisteredHuman is unreliable" below.

## Declaration and member list

<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

```csharp
public class Client : IComparable<Client>, IEquatable<Client>, IRocketReaderWriter
```

Namespace `Assets.Scripts`, not `Assets.Scripts.Networking`. Class body spans lines 212198-212508. Fields and properties, in declaration order:

| Line | Member |
|---|---|
| 212200 | `public string name;` |
| 212202 | `public string address;` |
| 212204 | `public int port;` |
| 212206 | `public ClientState state;` |
| 212208 | `public ClientUpdateFlag flags = ClientUpdateFlag.DaysLived;` |
| 212210 | `public float connectTime;` |
| 212212 | `public ulong ClientId;` |
| 212214 | `public bool IsHost;` |
| 212216 | `public float joinProgress;` |
| 212218 | `public int bytesSent;` |
| 212220 | `public int RoundTripTime;` |
| 212222 | `public long connectionId;` |
| 212224 | `public ushort DaysLived;` |
| 212226 | `public ConnectionMethod connectionMethod = ConnectionMethod.None;` |
| 212228 | `public HandshakeType handshake;` |
| 212230 | `public static int Count => NetworkBase.Clients.Count;` |
| 212232 | `public Human RegisteredHuman { get; private set; }` |

Methods, in declaration order: `GetMessageProgress` (212234), `SetState` (212239), the two constructors (212244, 212249), `CompareTo` (212258), `ToString` (212271), `ToStringOneLine` (212276), `ToStringNameAndId` (212281), `Find(long)` (212286), `Find(ulong)` (212302), `Ban` (212318), `Disconnect` (212323), `SetProgress` (212329), `Read` (212334), `Write` (212350), `DeserialiseClient` (212367), `Equals(Client)` (212417), `Equals(object)` (212434), `GetHashCode` (212451), `Register` (212456), `SerializeDeltaState` (212468), `DeserializeDeltaState` (212490).

**There is no `Client.Human`, no `ReferenceId`, no `HumanId`, no `CharacterId`, and no `OwnerClientId` member on `Client`.** `RegisteredHuman` is the only `Human` linkage the class has. A text scan of the whole class body for those names returns only the `RegisteredHuman` declaration, the `Register` signature, and the assignment inside `Register`.

The two id fields are different types and mean different things: `ClientId` is the `ulong` Steam id, `connectionId` is the `long` transport connection id.

## Client.Find: two overloads keyed on different ids

<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

Casting one id to the other type silently looks up the wrong thing, because both overloads exist and both compile.

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

public static Client Find(ulong clientId)
{
	if (Assets.Scripts.Networking.NetworkManager.HostClient != null && Assets.Scripts.Networking.NetworkManager.HostClient.ClientId == clientId)
	{
		return Assets.Scripts.Networking.NetworkManager.HostClient;
	}
	foreach (Client client in NetworkBase.Clients)
	{
		if (client.ClientId == clientId)
		{
			return client;
		}
	}
	return null;
}
```

Both check `NetworkManager.HostClient` first, then scan `NetworkBase.Clients`. Both return null when the instance is not in that list, which is what makes disconnect cleanup a Prefix-only job. See [ClientDisconnected cleanup Prefix](../Patterns/ClientDisconnectedPrefix.md).

## RegisteredHuman is unreliable

<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

`RegisteredHuman` is an auto-property with a private setter (line 212232):

```csharp
public Human RegisteredHuman { get; private set; }
```

The identifier appears **exactly three times in the whole assembly**: the declaration (212232), one assignment (212463), and one read (212827).

### The single assignment site

`Client.Register` (lines 212456-212466) is the only writer:

```csharp
public static void Register(Human thing, ulong ownerClientId)
{
	if (ownerClientId != 0L)
	{
		Client client = Find(ownerClientId);
		if (client != null)
		{
			client.RegisteredHuman = thing;
		}
	}
}
```

Note both guards. A `Human` whose owner client is not currently in `NetworkBase.Clients` registers nowhere, silently, with no log line.

### Its only caller is guarded by a change check

`Client.Register` is called from exactly one place: the `Thing.OwnerClientId` setter (lines 317651-317668, `Assets.Scripts.Objects.Thing`):

```csharp
public ulong OwnerClientId
{
	get
	{
		return _ownerClientId;
	}
	set
	{
		if (value != _ownerClientId)
		{
			_ownerClientId = value;
			if (this is Human thing)
			{
				Client.Register(thing, _ownerClientId);
			}
		}
	}
}
```

The backing field is `private ulong _ownerClientId;` (line 317035), and every occurrence of it in the entire assembly is inside this property (317035, 317655, 317659, 317661, 317664). No code path writes the field directly, so every `OwnerClientId` write does run the `Client.Register` hook. But the `if (value != _ownerClientId)` guard means **assigning the same value again is a no-op that never re-drives registration**.

### Consequence: null for the whole session on a dedicated server

`Thing.DeserializeSave` (line 321303) restores ownership through the public setter (line 321319):

```csharp
base.name = saveData.PrefabName;
CustomName = saveData.CustomName;
OwnerClientId = saveData.OwnerSteamId;
```

That is a property write, so it does reach `Client.Register`. But at world-load time no client is connected, `Find(ownerClientId)` returns null, and the registration is dropped with no log line. Later, when that same owner actually connects, their `Human` already carries the correct `OwnerClientId`, so the setter's change guard rejects the write and `Client.Register` is never retried.

Net effect: on a dedicated server, `Client.RegisteredHuman` stays null for the entire session for any player whose character came out of the save. That is the normal case for every returning player.

The path that does register is `Human.SetBasicsData` (line 361978):

```csharp
public void SetBasicsData(ulong clientId, string clientUserName)
{
	OnServer.PublishCustomName(this, clientUserName);
	base.OwnerClientId = clientId;
	base.name = "Character_" + clientUserName + "_" + clientId + "_" + clientUserName;
}
```

It has three call sites: `Human.DeserializeOnJoin` (line 362008, inside the method declared at 361998), `Human.CreateCharacter` (362118), and `Human.MoveBodyBagToSlot` (369583). `DeserializeOnJoin` is the joining client's side of the join handshake, not the server's, and `CreateCharacter` runs on character creation, which a returning player does not go through.

### It is also never cleared

There is no `RegisteredHuman = null` anywhere in the assembly, no `Unregister`, and nothing on the disconnect or despawn path touches it. `NetworkBase.RemoveClient` does not clear it, and neither does `Client.SetState` or `Client.Disconnect`. So the property can also be stale-but-non-null, and it can hold a Unity fake-null reference to a destroyed object.

The game's own read site accounts for that by testing `if ((bool)registeredHuman)` rather than `!= null`, which is the Unity implicit-bool destroyed-object check. See [UnityFakeNull](../Patterns/UnityFakeNull.md). Any mod that reads the property must do the same.

## The only read site is unreachable in this build

<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

The single read (line 212827) lives in `FragmentHandler.Receive(byte[] bytes, int size, bool isServer)` (`public static class FragmentHandler` at 212509, `Receive` declared at 212759), inside `case 2:` of the reassembly switch:

```csharp
_waiting = true;
int count = BitConverter.ToInt32(_buffer, 0);
RocketBinaryReader rocketBinaryReader = new RocketBinaryReader(new MemoryStream(LZ4Codec.Unwrap(_buffer), 0, count));
if (isServer)
{
	ulong num2 = rocketBinaryReader.ReadUInt64();
	Client client = Client.Find(num2);
	if (client == null)
	{
		ConsoleWindow.PrintError($"error '{num2}' not found when reading client thing state");
		break;
	}
	Human registeredHuman = client.RegisteredHuman;
	if ((bool)registeredHuman)
	{
		registeredHuman.ProcessUpdate(rocketBinaryReader, registeredHuman.NetworkUpdateFlags);
	}
	else
	{
		ConsoleWindow.PrintError($"error '{num2}' has no thing when reading client state");
	}
}
else
{
	ReadStateImmediate(rocketBinaryReader);
}
break;
```

The branch runs only for a packet arriving on `NetworkChannel.StateTick` while the local role is Server. `NetworkChannel.StateTick` appears exactly twice in the whole assembly:

```
212541:		private static NetworkChannel Channel = NetworkChannel.StateTick;
273553:				case NetworkChannel.StateTick:
```

Line 212541 is the static channel field that both of the class's sends use. Line 273553 is the sole dispatcher into `Receive`, and it is also what supplies `isServer`:

```csharp
case NetworkChannel.StateTick:
	FragmentHandler.Receive(Buffer, num2, NetworkRole == NetworkRole.Server);
	break;
```

The only two sends on that channel are `FragmentHandler.SendFragmentHeader` (declared 212694, sends at 212700) and `FragmentHandler.Send` (declared 212703, sends at 212732), and both go through `NetworkServer.SendToClientsDirect(..., Channel, excludeConnecting: true, -1L)`, that is, server to clients. **Nothing sends on `StateTick` toward the server**, so a server never receives a `StateTick` packet, `isServer` is never true inside `Receive`, and neither the `RegisteredHuman` read nor the `PrintError` at 212834 ever executes in this build.

Client-to-server state actually travels on `NetworkChannel.GeneralTraffic` as `ProcessedMessage` types, and never touches `RegisteredHuman`.

Practical reading: `RegisteredHuman` is written in one place that usually does not fire and read in one place that never fires. Treat it as vestigial.

## Reliable alternatives, keyed on Thing.OwnerClientId

<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

`Thing.OwnerClientId` is the value that does survive a reconnect, because it is serialized into the save as `OwnerSteamId` and restored on load. Three vanilla lookups build on it.

`Human.Find(ulong clientId)` (lines 362067-362077):

```csharp
public static Human Find(ulong clientId)
{
	foreach (Human allHuman in AllHumans)
	{
		if (allHuman.OwnerClientId == clientId)
		{
			return allHuman;
		}
	}
	return null;
}
```

`Entity.GetClientEntity(ulong clientId)` (lines 302434-302444) is the same shape over `Entity.AllEntities`:

```csharp
public static Entity GetClientEntity(ulong clientId)
{
	foreach (Entity allEntity in AllEntities)
	{
		if (allEntity.OwnerClientId == clientId)
		{
			return allEntity;
		}
	}
	return null;
}
```

The game's own disconnect-cleanup test, `CleanupDisconnected` (lines 96602-96609, in `Util.Commands.CleanupPlayersCommand` declared at 96526), takes a third route and goes through the brain:

```csharp
private void CleanupDisconnected(Human human, List<Human> toDestroy)
{
	Brain brain = human?.BrainSlot?.Occupant as Brain;
	if ((bool)brain && Client.Find(brain.ClientId) == null && !toDestroy.Contains(human))
	{
		toDestroy.Add(human);
	}
}
```

This is vanilla's own precedent for "who is disconnected": `Client.Find(brain.ClientId) == null`, keyed off `Brain.ClientId`, not off `Client.RegisteredHuman`.

### Trap: reject clientId == 0 before any of these lookups

`OwnerClientId` is 0 on every unowned `Thing` in the world, so `Human.Find(0)` returns an arbitrary unowned `Human` rather than nothing. A `Client` that has not completed its handshake still has `ClientId == 0`, so this is reachable from real code, not just from a bug.

```csharp
if (client.ClientId == 0UL)
    return;

Human owner = Human.Find(client.ClientId);
```

`Client.Register` already encodes the same guard as its first condition (`if (ownerClientId != 0L)`).

## Verification history

<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

- 2026-07-27: page created. All content verified against game version 0.2.6403.27689. Covers the full `Client` member list (class body 212198-212508), both `Find` overloads, and the finding that `Client.RegisteredHuman` is unreliable: three assembly-wide occurrences (212232 declaration, 212463 assignment, 212827 read), single writer `Client.Register` (212456-212466), single caller `Thing.OwnerClientId` setter (317651-317668) whose change guard prevents any retry, backing field `_ownerClientId` (317035) written nowhere else, and the `Thing.DeserializeSave` (321303, assignment at 321319) path that drops the registration at world-load time. Also records that the property is never cleared, that its only read site inside `FragmentHandler.Receive` (212759, read at 212827) is unreachable because both `NetworkChannel.StateTick` sends are server to clients, and the reliable `OwnerClientId`-keyed alternatives (`Human.Find` 362067, `Entity.GetClientEntity` 302434, `CleanupPlayersCommand.CleanupDisconnected` 96602) plus the `clientId == 0` trap. Additive page; no existing verified content was contradicted. Corrects two namespace assumptions carried in the source material: `Client`, `FragmentHandler`, and `NetworkServer` live in `Assets.Scripts` (block 195722-223977), while `NetworkBase` (39197) is in the global namespace and `NetworkChannel` (272554) is in `Assets.Scripts.Networking`.

## Open questions

- Whether `FragmentHandler`'s server-side `case 2:` branch is dead by design (a client-to-server state channel that was removed) or is waiting on a sender that ships later. Only the current build was read; no history was consulted. Either way the branch does not execute at 0.2.6403.27689, so the conclusion about `RegisteredHuman` holds.
