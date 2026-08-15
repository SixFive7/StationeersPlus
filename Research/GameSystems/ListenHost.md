---
title: Listen Host
type: GameSystems
created_in: 0.2.6403.27689
verified_in: 0.2.6428.27798
verified_at: 2026-08-15
sources:
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs (GameManager, NetworkServer, NetworkManager, Settings, World, SettingsCommand)
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Networking.NetworkManager.TotalPlayersInGame
related:
  - ../GameClasses/Client.md
  - ./NetworkRoles.md
  - ./DirectConnect.md
  - ./DedicatedServerSettings.md
  - ../Workflows/StationeersLaunchPadDedicatedServer.md
tags: [network, launchpad]
---

# Listen Host

How an ordinary Stationeers client process becomes a multiplayer host: one process that runs the simulation, accepts remote clients, and plays a character at the same time. This is the mode a player gets from the in-game "Start Local Host" setting, and it is distinct from the headless dedicated server covered in `DedicatedServerSettings.md`.

The short version: a listen host is `NetworkRole.Server` with a player character. It is the dedicated server's code path with `IsBatchMode` false, one boolean apart.

## The transport is RakNet, not Steam

<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

A listen host binds a plain RakNet UDP socket at `Settings.CurrentData.LocalIpAddress : Settings.CurrentData.GamePort`. Steam P2P is an additional, optional path, never the transport. A loopback Direct Connect into a listen host therefore works exactly as it does against the dedicated server (see `DirectConnect.md`).

Two facts establish this, both in `NetworkManager.StartServer(ushort port)` (Assembly-CSharp.decompiled.cs:273235-273279):

```csharp
_hostSteamId = GameManager.GetSteamId();
string text = Settings.CurrentData.LocalIpAddress.Trim();
string text2;
if (!string.IsNullOrEmpty(text)) { text2 = text; ... }
else { text2 = GetIPv4Address(); ... }
SocketDescriptor socketDescriptor = new SocketDescriptor(text2, port);
...
StartupResult startupResult = Instance.rakNet.Startup((uint)MaxConnections, socketDescriptors, 2);
// fallback: retry with SocketDescriptor("", port) if the first Startup fails
...
Instance.rakNet.SetMaximumIncomingConnections((ushort)MaxConnections);
NetworkState = NetworkState.Online;
Time.timeScale = 1f;
NetworkRole = NetworkRole.Server;
NetworkServer.HostPort = port;
steamLobby.HostLobby(_hostSteamId, Settings.CurrentData.ServerMaxPlayers);
return true;
```

First, the bind is unconditional RakNet. Second, `steamLobby.HostLobby(...)` (:189801) is an `async Task<bool>` that `StartServer` calls **without awaiting** at :273277 and then unconditionally returns `true`. Any lobby failure lands in an unobserved Task and cannot affect the RakNet listener or the return value. `NetworkState`, `NetworkRole` and `HostPort` are all assigned before that line. A Steam lobby is therefore not required for a listen host to accept connections.

`MaxConnections => 64` (:272712). `steamLobby` is constructed in `NetworkManager.Init()` during `ManagerAwake` (:273104), so it is never null on a client build.

### `GetIPv4Address()` deliberately excludes loopback

<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

When `LocalIpAddress` is empty, the bind address comes from `GetIPv4Address()` (:273206-273233), which filters out the 127.x range:

```csharp
if (address.AddressFamily == AddressFamily.InterNetwork && addressBytes[0] != 127) { num = num2; }
```

So a host left on the default empty `LocalIpAddress` binds the machine's LAN address and nothing is listening on 127.0.0.1. This is the same reason the dedicated server launcher pins `-settings LocalIpAddress 127.0.0.1`.

## `NetworkRole` has three values and no `Host`

<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

```csharp
public enum NetworkRole
{
    None,
    Server,
    Client
}
```

(:272571-272576)

The three role properties are mutually exclusive views of that one field (:272706-272710):

```csharp
public static bool IsActive => NetworkRole != NetworkRole.None;
public static bool IsClient => NetworkRole == NetworkRole.Client;
public static bool IsServer => NetworkRole == NetworkRole.Server;
```

A listen host is `NetworkRole.Server` and nothing else. Its "client half" is not a network client; it is simply the local player character created by the ordinary world-load path. See `NetworkRoles.md` for the full four-mode matrix.

`LocalClientId => Cookie?.ClientId ?? 0` (:272742) is unchanged by hosting: the host reads the same `PlayerCookie` value a single-player session would. `RunSimulation => !NetworkManager.IsClient` (:203945), so a host runs the simulation.

### `CanBecome` refuses any transition out of `None`

<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

```csharp
private static bool CanBecome(NetworkRole role)
{
    if (GameManager.IsNewTutorial)
    {
        ConsoleWindow.Print("Cannot host sever in Tutorial");
        return false;
    }
    switch (role)
    {
    case NetworkRole.None:
        return true;
    case NetworkRole.Server:
    case NetworkRole.Client:
        return NetworkRole == NetworkRole.None;
    default:
        throw new ArgumentOutOfRangeException("role", role, null);
    }
}
```

(:273434-273451)

Once a process is `NetworkRole.Server`, `StartClient` returns false immediately (:273372-273375). That is not a blocker for hosting, because a listen host never calls `StartClient`; it matters only if code tries to make one process both host and joiner. `GameManager.IsNewTutorial` is the only unconditional refusal.

## The boot chain: `StartLocalHost` -> `StartGame` -> `Host` -> `StartServer`

<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

There is **no "host this world" button** on the load or new-world screens. The switch is a Settings-panel toggle, `SettingType.StartLocalHost`, persisted as `Settings.SettingData.StartLocalHost` (declared :265553, default `false`).

Toggle handler (:267292-267295):

```csharp
case SettingType.StartLocalHost:
    CurrentData.StartLocalHost = GameManager.IsBatchMode || GetToggle(settingType).isOn;
    NetworkServer.ApplyLocalHostSetting(CurrentData.StartLocalHost);
    break;
```

`NetworkServer.ApplyLocalHostSetting` (:214068-214074) means flipping the toggle inside a running world starts hosting immediately:

```csharp
public static void ApplyLocalHostSetting(bool currentDataStartLocalHost)
{
    if (currentDataStartLocalHost) { Host().Forget(); }
}
```

Both world-entry branches converge on `GameManager.StartGame()`:

- New world: `MainMenu.StartGame()` (:242742) -> `World.StartNewWorld(id)` (:324892) -> `World.NewAsync` (:324921) -> `WorldManager.StartWorld(); await GameManager.StartGame();` (:324956-324957)
- Load save: menu or the `load` console command -> `LoadHelper.LoadGame(path, stationName)` (:264504) -> `LoadGameTask` -> `LoadWorldTask` -> `World.OnLoadingFinished(worldData)` (:324961) -> `WorldManager.StartWorld(); GameManager.StartGame().Forget();` (:324964-324965)

`GameManager.StartGame()` (:204575) is the single hosting trigger (:204603-204611):

```csharp
if (Settings.CurrentData.StartLocalHost || IsBatchMode)
{
    World.PopulateEmptyId();
    await NetworkServer.Host();
}
if (RunSimulation)
{
    NetworkServer.PopulateHostClient();
}
```

`NetworkServer.Host()` (:213648-213670):

```csharp
public static async UniTask Host()
{
    if (IsHosting || GameManager.GameState == GameState.None || !GameManager.RunSimulation)
    {
        ConsoleWindow.Print($"Failed ToHost. IsHosting: {IsHosting} GameState: {GameManager.GameState} RunSimulation: {GameManager.RunSimulation}");
        return;
    }
    int attempts = 0;
    while (!Assets.Scripts.Networking.NetworkManager.StartServer(Convert.ToUInt16(Settings.CurrentData.GamePort)))
    {
        await UniTask.Delay(1000);
        attempts++;
        ConsoleWindow.PrintAction($"Start Server Failed. Attempt {attempts} of {3}.");
        if (attempts >= 3) { return; }
    }
    IsHosting = true;
    NetworkUpdate().Forget();
    CreateNewGameSession();
    _masterServerPingTimer.Start();
}
```

Two failure modes are silent-ish and both only print to the console: hosting from the main menu (`GameState == GameState.None` fails the guard at :213650), and a port that cannot be bound (three attempts at 1 s intervals, then a quiet return, :213661-213664). `NetworkServer.IsHosting` (:213587) is a public getter and is the reliable post-condition to assert on, alongside `NetworkManager.NetworkRole == NetworkRole.Server`.

## Settings that must be set before the world loads

<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

All of these live on `Settings.CurrentData` (`Settings.SettingData`), mutable at runtime.

| Field | Default | Set to | Why |
|---|---|---|---|
| `StartLocalHost` | `false` | `true` | The only thing that makes `StartGame()` host. Read at :204603. |
| `LocalIpAddress` | `""` | `"127.0.0.1"` | Otherwise `GetIPv4Address()` skips 127.x and binds the LAN IP, so loopback Direct Connect finds nothing listening. |
| `GamePort` | `"27016"` | a free port | The listen port. String-typed; `Convert.ToUInt16` at :213656. |
| `ServerMaxPlayers` | `10` | as needed | Passed to `HostLobby` and the session record. |
| `ServerPassword` | `""` | empty | Empty skips the password check entirely (:213794). |
| `ServerName` | `"Stationeers"` | anything | Cosmetic, plus the server browser. |
| `ServerVisible` | `false` | `false` | Gates master-server registration (:274032, :214110). |
| `UPNPEnabled` | `true` | `false` | Avoids a UPnP discovery round on a loopback-only session. |
| `UseSteamP2P` | `true` | `false` | Not needed for RakNet, and it disarms `ProcessP2PSessionRequest`. |

`UpdatePort` is **vestigial**. The only references in the whole assembly are the settings UI (:265570, :266131, :267160-267172). Nothing binds a socket with it. Only `GamePort` matters.

### `ProcessP2PSessionRequest` can promote an idle process to server

<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

`ProcessP2PSessionRequest` (:273110-273124) can flip a process sitting at the menu to `NetworkRole.Server` on an inbound Steam P2P request, with no local action. Because `CanBecome` then refuses `StartClient` (see above), a process this happens to can no longer join anything. Setting `UseSteamP2P` to false closes it.

## Dedicated server versus listen host

<!-- verified: 0.2.6428.27798 @ 2026-08-15 -->

Same assembly, same `StartGame()`, one boolean apart.

| | Dedicated | Listen host |
|---|---|---|
| Host trigger | `IsBatchMode` (:204603); `StartLocalHost` irrelevant | `Settings.CurrentData.StartLocalHost` |
| `IsBatchMode` | true (`Application.isBatchMode` or `RuntimePlatform.*Server`, :204290-204304) | false |
| Player character | none. `if (!GameManager.IsBatchMode) CreateCharacterAndTakeControl();` (:324935-324938, and :268779-268791 on the load path) | created |
| `PlayerCookie` / `LocalClientId` | `Cookie = ((!GameManager.IsBatchMode) ? PlayerCookie.Load() : null)` (:273983), so `LocalClientId == 0` | real cookie ClientId |
| `TotalPlayersInGame` | `Clients.Count + 0` (:272728) | `Clients.Count + 1` (see below) |
| Auto-pause with no clients | active if `AutoPauseServer` (:39241, :39249, both gated on `IsBatchMode`) | never auto-pauses |
| Session `ServerType` | `DedicatedWindows` / `DedicatedLinux` | `Hosted`, `Players = 1` (:274011-274027) |

Carries over unchanged: the RakNet bind (`StartServer`), the `LocalIpAddress` loopback pin, `GamePort`, `VerifyConnection`, the join queue and `PackageJoinData`, `ServerPassword` and version checks, `NetworkServer.NetworkUpdate`, `serverrun` / `ServerAuthSecret`, blacklist, `ban` and `kick`.

Does not carry over: `AutoPauseServer` (inert on a listen host, since both call sites are gated on `IsBatchMode`), the dedicated build's system console, and `LocalClientId == 0`.

The system console is worth naming precisely, because calling it "the stdin console" (as this page did until 2026-08-15) describes a channel that does not exist. It is `UI.ImGuiUi.RocketSystemConsole`, constructed only inside `ConsoleWindow._Init`'s `GameManager.IsBatchMode` branch and only when `-logFile` is absent, and its constructor throws `"Don't use this outside of dedicated server builds"` unless `Application.platform` is `LinuxServer` or `WindowsServer`. A listen host is an ordinary client build with `IsBatchMode` false, so neither condition is met and there is no such console to inherit. It never read the process's standard input on the dedicated build either: it reads keystrokes from the Win32 console input buffer through `System.Console.ReadKey()`. See [DedicatedServerSettings, "The console channel"](./DedicatedServerSettings.md). A listen host also renders and runs a real `GameManager.Update`, so anything that depends on the render loop applies to it as it would to any other client.

**The `+1` on `TotalPlayersInGame` is not a fudge: it is the host itself, which is never in `NetworkBase.Clients`.** That list holds joiners only, and a listen host's own `Client` lives on `NetworkManager.HostClient`. Anything that enumerates connected players has to union the two the way the game's own roster code does. Re-confirmed at 0.2.6428.27798, where the property reads `NetworkBase.Clients.Count + ((!GameManager.IsBatchMode) ? 1 : 0)`. Full mechanism, both union sites, and the client-side split: [Client, "The roster is two collections"](../GameClasses/Client.md).

## Nothing rejects a second client connecting from the same machine

<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

`VerifyConnection` (:213785-213813) checks exactly three things:

```csharp
if (Blacklist.Any(x => x.Id == msg.ClientId))                                   → reject
if (!string.IsNullOrEmpty(ServerPassword) && ServerPassword != msg.Password)    → reject
if (GameManager.GetGameVersion() != msg.Version)                                → reject
```

No address check, no same-machine check, no comparison against the host's own `LocalClientId`. Steam identity is not consulted at all.

Port collision between the host and a same-machine joiner is impossible: the joining side builds `new SocketDescriptor(AddressFamily.InterNetwork)` (:273378), which chains to `SocketDescriptor("", 0, family)` (Brutal.RakNet.decompiled.cs:409-432), meaning port 0, ephemeral, any interface. The `localPort` parameter of `StartClient(string, ushort, ushort)` is computed by `JoinClientFromMenu` as `port + 1` (:213147) and then never used. Only the host binds a fixed port.

Incoming connections are role-dispatched rather than transport-special (:273584-273587, :273615-273626): `NewIncomingConnection` -> `PlayerConnected(id, RocketNet)` -> `case NetworkRole.Server: NetworkServer.ClientConnected(...)`. The listen host runs the identical server-side accept path as the dedicated server.

What is still hazardous is the ClientId collision itself: `Brain.RegisterBrain` overwrites `PlayerBrains[id]` with no warning. A listen host consumes a ClientId of its own, so a rig that assigns ids per instance must count the host as one of them.

## Setting fields from the console

<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

`SettingsCommand : ClassManipulator<Settings.SettingData>` (:101942) resolves the field name case-insensitively and coerces via `Convert.ChangeType` (:96444, :96468), so `settings StartLocalHost true` and `settings GamePort 29016` both work, as does the multi-pair form `settings A 1 B 2` (:96393-96403). Each set fires `OnValueChanged()` (:101948-101953), which calls `NetworkManager.UpdateSessionData` and `Settings.SaveSettings()`, writing the process's `setting.xml`. Direct field writes to `Settings.CurrentData` work identically and skip that write.

`Assets.Scripts.ConsoleWindow.Submit(string)` (:222487-222491) is a direct pass to `CommandLine.Process`, so the whole settings surface is reachable from any code that can call one method on the Unity main thread.

## Teardown

<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

`NetworkServer.StopServer()` (:214052-214061) reaches `NetworkManager.EndConnection()` (:272762), which resets `NetworkState = Offline` and `NetworkRole = None`. That re-opens `CanBecome` for a later host or join in the same process. `GameManager.LeaveGame()` arrives at the same place.

## Verification history

<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

- 2026-08-15: the "does not carry over" list said "the `-batchmode` stdin console quirk". A fresh validator working the stdin question at 0.2.6428.27798 (`Research/WORKFLOW.md` Rule 3) established there is no stdin console: the dedicated build's console is `UI.ImGuiUi.RocketSystemConsole`, reading the Win32 console input buffer through `System.Console.ReadKey()`, gated on `GameManager.IsBatchMode && !CustomLogFile` and throwing outside the `LinuxServer` / `WindowsServer` platforms. The CONCLUSION on this page is upheld unchanged, a listen host does not inherit it, and both gates independently explain why; only the name was wrong, and the wrong name predicted a stdin channel that does not exist on either build. Result: the bullet renamed and a paragraph added giving the mechanism and the two gates. Evidence: [DedicatedServerSettings, "The console channel"](./DedicatedServerSettings.md), 2026-08-15 entry.
- 2026-08-15: "Dedicated server versus listen host" re-read against 0.2.6428.27798 and restamped. The `TotalPlayersInGame` row still holds: the property reads `NetworkBase.Clients.Count + ((!GameManager.IsBatchMode) ? 1 : 0)`. Added why the `+1` exists, which the row stated arithmetically without saying: the host's own `Client` is never in `NetworkBase.Clients` and lives on `NetworkManager.HostClient` instead, so anything enumerating players has to union the two. Mechanism documented on `GameClasses/Client.md`, linked from here. No other section on this page was re-read, so they keep their 0.2.6403.27689 stamps.
- 2026-08-09: page created. Full listen-host boot chain traced from `SettingType.StartLocalHost` through `GameManager.StartGame`, `NetworkServer.Host` and `NetworkManager.StartServer` against the 0.2.6403.27689 decompile. Established that the transport is RakNet rather than Steam, that a Steam lobby is fire-and-forget and not required, that `NetworkRole` has no `Host` value, and that `VerifyConnection` contains nothing that would reject a same-machine joiner.

## Open questions

- Two Facepunch.Steamworks processes under one Steam account, one of them hosting a lobby. `HostLobby` calls `SteamMatchmaking.CreateLobbyAsync()` and then has the host `Join()` its own lobby (:189801-189815). It is fire-and-forget so it cannot break `StartServer`, but whether it produces log noise or perturbs a second instance's Steam state is not visible in managed code.
- `GameManager.GetSteamId()` (:204096) behaviour while Steam is mid-reconnect. `_hostSteamId` is assigned from it before the bind (:273243), so a throw there would abort `StartServer` before `NetworkRole` is set.
- Whether promoting an already-running single-player world with `NetworkServer.Host()` alone is safe. That path skips `NetworkServer.PopulateHostClient()`, which normally follows in `StartGame` (:204610), and the consequence of the missing `NetworkManager.HostClient` record for a joining client is not established in code.
- `ServerMaxPlayers` enforcement. No clamp is visible on the listen path, matching the open question already recorded on `DedicatedServerSettings.md`.
