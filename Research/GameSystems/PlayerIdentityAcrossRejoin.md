---
title: Player identity across a disconnect and rejoin
type: GameSystems
created_in: 0.2.6403.27689
verified_in: 0.2.6403.27689
verified_at: 2026-07-27
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Referencable (NextReferenceId, NextReferenceIdLock, Referencables, FailedToRegister, RegisterNew, RegisterAs, AssignNewIdToDuplicates, ClearReferences, FindAndSetNextReferenceId)
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: NetworkServer.ClientDisconnected
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: NetworkBase.RemoveClient
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: NetworkClient (ProcessJoinData, RequestCharacterAsync, TakeControl)
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Brain (PlayerBrains, IsOnline, GetValidatedBrain, RegisterBrain, DeserializeOnJoin, DeserializeSave, RelinquishControl, OnLifeTick)
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: OnServer.RelinquishBrain
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Human (CreateCharacter, Respawn, RespawnFromNoParent)
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Networking.RespawnMessage.Process
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Networking.RelinquishControlMessage.Process
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: World (Initialize, HandlePlayerControl, CreateCharacterAndTakeControl)
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: BrainSaveData.ClientSteamId
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Thing.Create
related:
  - ../Patterns/ClientDisconnectedPrefix.md
  - ../GameClasses/Human.md
  - ../GameClasses/Client.md
  - ./RespawnFlow.md
  - ../Patterns/SaveLoadOrdering.md
tags: [network, entity, save-load]
---

# Player identity across a disconnect and rejoin

A player's `Human.ReferenceId` is stable across a disconnect and rejoin into the same running server world. The `Human` is never destroyed on disconnect and the rejoining client re-possesses the same object. Four specific cases produce a new id.

Line numbers in this page refer to the 0.2.6403.27689 decompile of `Assembly-CSharp.dll`.

## How ReferenceId is assigned
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

`Referencable` (37867) holds the allocator. Ids are sequential, server-authoritative, and persisted in the save. Not random, not a hash, not derived from the Steam id.

```csharp
public static class Referencable
{
	public static long NextReferenceId = 1L;

	public static object NextReferenceIdLock = new object();

	public static readonly List<IReferencable> ReferencablesChanged = new List<IReferencable>();

	public static readonly Dictionary<long, IReferencable> Referencables = new Dictionary<long, IReferencable>();

	public static List<IReferencable> FailedToRegister = new List<IReferencable>();

	public const long INVALID = 0L;
```

`RegisterNew` (37900) is the fresh-spawn path. It throws if called on a client (37902-37910):

```csharp
	public static bool RegisterNew(IReferencable iReferencable)
	{
		if (Assets.Scripts.Networking.NetworkManager.IsClient)
		{
			string text = "Error: Clients should not be assigning a new Reference";
			if (iReferencable != null)
			{
				text = text + ". Client is trying to assign a new reference for " + iReferencable.DisplayName;
			}
			throw new System.Exception(text);
		}
```

and then hands out the next number under the lock (37921-37930):

```csharp
		lock (NextReferenceIdLock)
		{
			if (Referencables.ContainsKey(NextReferenceId))
			{
				ConsoleWindow.PrintError($"error trying to register '{iReferencable.DisplayName}' with referenceId of '{NextReferenceId}' as it is already Assigned to: '{Referencables[NextReferenceId].DisplayName}'");
				return false;
			}
			iReferencable.ReferenceId = NextReferenceId;
			NextReferenceId++;
		}
```

`RegisterAs` (37940) is the adopt-an-existing-id path used by save load and by client join:

```csharp
	public static bool RegisterAs(IReferencable thing, long referenceId, bool force = false)
```

```csharp
			lock (NextReferenceIdLock)
			{
				thing.ReferenceId = referenceId;
				referenceId++;
				NextReferenceId = ((referenceId > NextReferenceId) ? referenceId : NextReferenceId);
			}
```

Load re-adopts the saved id: `Thing.Create<Thing>(prefab, pos, rot, thingData.ReferenceId)` reaches the `referenceId != 0` branch inside `Thing.Create<T>` (319024-319027):

```csharp
			else
			{
				flag = Referencable.RegisterAs(thing2, referenceId);
			}
```

The `referenceId` parameter of `Thing.Create<T>` is the discriminator throughout: `0L` means "server-side fresh spawn, allocate via `RegisterNew`", nonzero means "recreate a known thing under an existing id via `RegisterAs`". See [StructureRegistration](./StructureRegistration.md) for the full `Thing.Create<T>` body.

## Disconnect leaves the Human in the world
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

`NetworkServer.ClientDisconnected` (213891) is the entire server-side teardown:

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

and `NetworkBase.RemoveClient` (39227) is:

```csharp
	public static void RemoveClient(Client client)
	{
		Clients.Remove(client);
		OnClientRemoved();
	}
```

No `Thing` destruction, no `Referencable` deregistration, no `Brain.PlayerBrains` removal. The body stays in the world as an offline entity.

`Brain.IsOnline` (340989) is defined purely as connection presence:

```csharp
		public bool IsOnline => Client.Find(ClientId) != null;
```

Offline bodies keep metabolising at a reduced rate. `Brain.OnLifeTick` (341225-341238) gates the rate on `DifficultySetting.Current.OfflineMetabolism`:

```csharp
		public override void OnLifeTick()
		{
			base.OnLifeTick();
			if (!ParentEntity)
			{
				return;
			}
			bool flag = ParentEntity is Npc;
			float num = ((flag || IsOnline) ? 0.2f : (0.2f * Mathf.Clamp(DifficultySetting.Current.OfflineMetabolism, 0.1f, 1f)));
			if (!flag && ((object)ParentHuman == null || ParentHuman.IsSleeping))
			{
				num *= 0.5f;
			}
			float num2 = ((flag || IsOnline) ? 3f : (3f * Mathf.Clamp(DifficultySetting.Current.OfflineMetabolism, 0.1f, 1f)));
```

The only code that unbinds a brain from a client id is `OnServer.RelinquishBrain` (40470) and `Brain.RelinquishControl` (341127):

```csharp
	public static void RelinquishBrain(Brain playerBrain)
	{
		Brain.PlayerBrains.Remove(playerBrain.ClientId);
		playerBrain.ClientControl = false;
		playerBrain.ClientId = 0uL;
	}
```

```csharp
		public void RelinquishControl()
		{
			LocalControl = false;
			ParentEntity?.ReleaseControl();
			PlayerBrains.Remove(ClientId);
			if (Assets.Scripts.Networking.NetworkManager.IsClient)
			{
				NetworkClient.ReturnCharacter();
			}
			InventoryManager.ParentBrain = null;
			InventoryManager.Parent = null;
			ClientId = 0uL;
		}
```

Those are reached only from the respawn paths `Human.Respawn()` (361921) and `Human.RespawnFromNoParent()` (361956), and from `RelinquishControlMessage` (278569). Nothing on the disconnect path touches them.

## Rejoin re-possesses the same object
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

`NetworkClient.ProcessJoinData` (213188) ends with a character request (213229-213230):

```csharp
				await ImGuiLoadingScreen.SetState(GameStrings.LoadingScreenRequestingCharacter.DisplayString);
				await RequestCharacterAsync(isRespawn: false, cancellationToken);
```

`RequestCharacterAsync` (213286) is the decision point. A new `Human` is requested only when there is no surviving brain for this Steam id, or when the caller explicitly asks for a respawn:

```csharp
		public static async UniTask RequestCharacterAsync(bool isRespawn, CancellationToken token)
		{
			ConsoleWindow.Print("[NetworkClient] Sending respawn message and waiting to take control");
			ulong localClientId = Assets.Scripts.Networking.NetworkManager.LocalClientId;
			PlayerCosmetics cosmetics = PlayerCosmetics.Load(Singleton<GameManager>.Instance.CustomCosmeticsSlot);
			Brain.GetValidatedBrain(localClientId, out var playerBrain);
			if ((object)playerBrain == null || isRespawn)
			{
				SendToServer(new RespawnMessage(localClientId, cosmetics));
			}
			await TakeControl(token);
		}
```

`Brain.GetValidatedBrain` (341304) is a Steam-id lookup into `PlayerBrains`, with a decay check:

```csharp
		public static void GetValidatedBrain(ulong steamId, out Brain playerBrain)
		{
			PlayerBrains.TryGetValue(steamId, out playerBrain);
			if ((object)playerBrain != null && (object)playerBrain.ParentHuman != null && playerBrain.ParentHuman.State == EntityState.Decay)
			{
				playerBrain.ClientId = 0uL;
				if (GameManager.RunSimulation)
				{
					OnServer.Destroy(playerBrain);
				}
				playerBrain = null;
			}
		}
```

`PlayerBrains` (340944) is a plain static dictionary keyed by Steam id:

```csharp
		public static readonly Dictionary<ulong, Brain> PlayerBrains = new Dictionary<ulong, Brain>();
```

It is rebuilt on the joining client by `Brain.DeserializeOnJoin` (341216), which ends in `RegisterBrain(ClientId)`:

```csharp
		public override void DeserializeOnJoin(RocketBinaryReader reader)
		{
			base.DeserializeOnJoin(reader);
			ClientId = reader.ReadUInt64();
			SteamName = reader.ReadString();
			ClientControl = reader.ReadBoolean();
			RegisterBrain(ClientId);
		}
```

```csharp
		public void RegisterBrain(ulong steamId)
		{
			ClientId = steamId;
			PlayerBrains[steamId] = this;
		}
```

`TakeControl` (213308) then possesses the existing body and echoes its id back (213321, 213327-213329):

```csharp
					playerBrain.TakeControl(setPhysics: false);
```

```csharp
					takeControlMessage.HumanId = playerBrain.ParentHuman.ReferenceId;
					takeControlMessage.ClientId = localClientId;
					takeControlMessage.SendToServer();
```

No `Human.CreateCharacter`, no `OnServer.Create<Human>`, no `RegisterNew`. Same `Human`, same `ReferenceId`.

## The four cases that do produce a new ReferenceId
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

| Case | Trigger | Mechanism | Result |
|---|---|---|---|
| Body fully decayed while offline | `ParentHuman.State == EntityState.Decay` at rejoin | `GetValidatedBrain` (341304) nulls `ClientId`, destroys the brain, returns `null`, so `RequestCharacterAsync` takes the `RespawnMessage` branch | New `Human` via `Human.CreateCharacter` (362098) and `OnServer.Create<Human>` (362115) with `referenceId == 0`, which reaches `RegisterNew` and a new sequential id |
| Player died and chose respawn | `Human.Respawn()` (361921) or `Human.RespawnFromNoParent()` (361956) | Both call `NetworkClient.RequestCharacterAsync(isRespawn: true, ...)` (361935 / 361964), forcing the `RespawnMessage` branch regardless of the brain lookup | New `Human`, new id. See [RespawnFlow](./RespawnFlow.md) |
| Character relinquished | `RelinquishControlMessage` (278569) | `OnServer.RelinquishBrain` (40470) does `Brain.PlayerBrains.Remove(playerBrain.ClientId)` and `playerBrain.ClientId = 0uL` | The next join finds no brain for that Steam id, so `GetValidatedBrain` yields `null` and a new `Human` is created |
| A different Steam account | Any join under a different Steam id | `PlayerBrains` is keyed by Steam id | No entry, so a new `Human` |

The creation site (362098, 362115):

```csharp
		public static Human CreateCharacter(ulong clientId, string steamName, PlayerCosmetics cosmetics = null, bool isRespawn = false, StartLocationData startLocation = null, ISpawnPoint spawnPoint = null)
```

```csharp
			Human human = OnServer.Create<Human>(Prefab.Character, safePositionInRadius, rotation);
```

`CreateCharacter` does reuse spawn continuity, but that is start-location data, not the `ReferenceId`. `RespawnMessage.Process` (278542-278546) looks up the previous client info to decide the spawn point:

```csharp
			SerializedClientInfo clientInfo = GameManager.GetClientInfo(HumanId);
			bool isRespawn = clientInfo != null;
			StartLocationData startLocation = ((clientInfo != null) ? DataCollection.Get<StartLocationData>(clientInfo.StartLocationHash) : null);
			ISpawnPoint spawnPoint = ((clientInfo != null) ? Referencable.Find<ISpawnPoint>(clientInfo.SpawnPointReference) : null);
			Human.CreateCharacter(HumanId, client?.name ?? "Unknown", Cosmetics, isRespawn, startLocation, spawnPoint);
```

and `CreateCharacter` writes the record back at 362145:

```csharp
			GameManager.AddClientInfo(clientId, new SerializedClientInfo(clientId, startLocation.IdHash, spawnPoint?.ReferenceId ?? 0));
```

## ReferenceId remapping on load
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

Normal load preserves ids exactly. Remapping happens only on collision or failure. `RegisterAs` pushes onto `FailedToRegister` in three cases (37949-37966): the id is 0, the thing already holds an id, or the id is already taken.

```csharp
			if (referenceId == 0L)
			{
				ConsoleWindow.PrintError("error trying to assign " + thing.DisplayName + " a null reference id");
				FailedToRegister.Add(thing);
				return false;
			}
			if (!force && thing.ReferenceId != 0L)
			{
				ConsoleWindow.PrintError($"error trying to register '{thing.DisplayName}' with id '{referenceId}' as it already has been registered with '{thing.ReferenceId}' ");
				FailedToRegister.Add(thing);
				return false;
			}
			if (Referencables.ContainsKey(referenceId))
			{
				ConsoleWindow.PrintError($"Couldn't register '{thing.DisplayName}' with id '{referenceId}' as there is already an entry for this id. It is used by: {Referencables[referenceId].DisplayName}");
				FailedToRegister.Add(thing);
				return false;
			}
```

World load then calls `Referencable.AssignNewIdToDuplicates()` (268759-268761):

```csharp
			SpawnDataHelper.LoadSave(worldData);
			Referencable.AssignNewIdToDuplicates();
			await UniTask.NextFrame();
```

```csharp
	public static void AssignNewIdToDuplicates()
	{
		foreach (IReferencable item in FailedToRegister)
		{
			RegisterNew(item);
			ConsoleWindow.PrintAction(item.DisplayName + "' has been assigned a new Id: " + StringManager.Get(item.ReferenceId));
		}
		FailedToRegister.Clear();
	}
```

`Referencable.FindAndSetNextReferenceId(worldData)` (38060) scans the saved collections and parks the allocator past the highest saved id (38091-38094):

```csharp
	public static void FindAndSetNextReferenceId(XmlSaveLoad.WorldData worldData)
```

```csharp
		lock (NextReferenceIdLock)
		{
			NextReferenceId = highest + 1;
		}
```

`Referencable.ClearReferences()` (38043) resets the allocator to 1:

```csharp
	public static void ClearReferences()
	{
		lock (Referencables)
		{
			Referencables.Clear();
		}
		lock (NextReferenceIdLock)
		{
			NextReferenceId = 1L;
		}
	}
```

It is reached via `Thing.ClearAll()` in `World.Initialize` (325012-325019), which is why ids restart at 1 for a fresh world:

```csharp
	private static void Initialize(string worldName, bool newWorld, string loadingScreenMessage)
	{
		if (!GameManager.IsBatchMode)
		{
			XmlSaveLoad.UpdateLoadingScreen(loadingScreenMessage, 0f).Forget();
		}
		Thing.ClearAll();
		GameManager.GameState = GameState.Joining;
```

## The three deployment scenarios
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

**Dedicated server that keeps running: stable.** The world is never torn down, `Referencables` is never cleared, the `Human` is never destroyed, and `PlayerBrains[steamId]` survives the disconnect. A player who drops and rejoins gets the same `Human` with the same `ReferenceId`.

**Host / listen server: stable for remote clients.** A remote client disconnecting and rejoining behaves exactly like the dedicated case. The host leaving shuts the world down, which is the next case.

**Save reloaded from the menu: still stable for the same Steam account.** Ids are re-adopted by `RegisterAs`, and brain ownership round-trips through the save. `BrainSaveData` (340934-340941) carries the Steam id:

```csharp
public class BrainSaveData : DynamicThingSaveData
{
	[XmlElement]
	public ulong ClientSteamId;

	[XmlElement]
	public PlayerCosmetics identity;
}
```

`Brain.DeserializeSave` (341153) restores the mapping:

```csharp
		public override void DeserializeSave(ThingSaveData savedData)
		{
			base.DeserializeSave(savedData);
			if (savedData is BrainSaveData brainSaveData)
			{
				ClientId = brainSaveData.ClientSteamId;
			}
			if (ClientId != 0)
			{
				PlayerBrains[ClientId] = this;
			}
		}
```

`World.HandlePlayerControl()` (324973) then matches the local Steam id against `PlayerBrains` and takes control of the existing body:

```csharp
	public static DynamicThing HandlePlayerControl()
	{
		foreach (KeyValuePair<ulong, Brain> playerBrain in Brain.PlayerBrains)
		{
			if (playerBrain.Key != Assets.Scripts.Networking.NetworkManager.LocalClientId)
			{
				continue;
			}
			Brain value = playerBrain.Value;
			if ((bool)value)
			{
				if ((bool)(value.ParentSlot.Parent as Entity))
				{
					value.TakeControl();
				}
				else
				{
					value.TakeControlInBodyBag();
				}
				return value.ParentSlot.Parent.AsDynamicThing;
			}
		}
		return null;
	}
```

Only if that returns null does the loader fall through to `World.CreateCharacterAndTakeControl()` and a new `Human`. The call site (268783-268791):

```csharp
			else if ((bool)World.HandlePlayerControl())
			{
				await LodManager.InitialiseLodsOnLoad();
			}
			else
			{
				LodManager.EnqueueRequesterToUpdate(World.CreateCharacterAndTakeControl());
				await LodManager.InitialiseLodsOnLoad();
			}
```

and the definition (324998):

```csharp
	public static Human CreateCharacterAndTakeControl()
	{
		ulong localClientId = Assets.Scripts.Networking.NetworkManager.LocalClientId;
		string username = Assets.Scripts.Networking.NetworkManager.Username;
		PlayerCosmetics cosmetics = PlayerCosmetics.Load(Singleton<GameManager>.Instance.CustomCosmeticsSlot) ?? new PlayerCosmetics();
		SerializedClientInfo value;
		bool flag = GameManager.ClientInfo.TryGetValue(localClientId, out value);
		StartLocationData startLocation = (flag ? DataCollection.Get<StartLocationData>(value.StartLocationHash) : null);
		ISpawnPoint spawnPoint = (flag ? Referencable.Find<ISpawnPoint>(value.SpawnPointReference) : null);
		Human human = Human.CreateCharacter(localClientId, username, cosmetics, flag, startLocation, spawnPoint);
		human.OrganBrain.TakeControl(setPhysics: false);
		return human;
	}
```

The caveat on this third scenario is `AssignNewIdToDuplicates`: a save carrying duplicate or zero ids can have arbitrary Things renumbered on load. That is a corrupted-save path, not normal operation.

## Why this matters for mod state
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

Mod state keyed by `Human.ReferenceId` on a server survives a player's reconnect. That cuts both ways.

- Per-player rows do not self-expire when a player leaves. They must be pruned explicitly, which is what a Prefix on `NetworkServer.ClientDisconnected` is for. See [ClientDisconnectedPrefix](../Patterns/ClientDisconnectedPrefix.md).
- A client-side cache keyed by the local `Human`'s `ReferenceId` is still valid after a rejoin. Any "resend only when the value changed" dedupe built on it stays silent across the reconnect, because the key and the last-sent value both still match. If a rejoin must force a resend, clear the cache on the join path rather than relying on the id to change.

## Verification history
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

- 2026-07-27: page created against the 0.2.6403.27689 decompile of `Assembly-CSharp.dll`. All excerpts read directly from the decompile and confirmed at the cited line numbers by an independent pass.
- 2026-07-27: while writing this page, the then-current skeleton and mechanism sentence on [ClientDisconnectedPrefix](../Patterns/ClientDisconnectedPrefix.md) were found to contradict the verified signature `NetworkServer.ClientDisconnected(long connectionId)`. A fresh validator was spawned per `Research/WORKFLOW.md` Rule 3 and returned "B is correct": the method takes a single `long connectionId`, `NetworkBase.RemoveClient` only removes the instance from the static `Clients` list and mutates nothing on the `Client` itself, and a Harmony patch parameter named `client` fails to bind at patch time. A parallel pass in the same session reached the identical verdict and rewrote that page; its own Verification History carries the full resolution. Nothing on this page was changed by the conflict, and the disconnect excerpts here match the ones on the rewritten page.

## Open questions

- Whether an in-world confirmation run matches the source reading has not been done. The whole page is source-derived; a dedicated-server session that records a player's `Human.ReferenceId`, disconnects, rejoins, and re-reads it would close the loop.
