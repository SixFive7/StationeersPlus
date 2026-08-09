---
title: Connection failures are silent
type: GameSystems
created_in: 0.2.6403.27689
verified_in: 0.2.6403.27689
verified_at: 2026-08-09
sources:
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs (NetworkManager.ReceiveEvents, NetworkClient, ConsoleWindow.ConnectTo)
  - .work/decomp/0.2.6403.27689/Brutal.RakNet.decompiled.cs (DefaultMessageIDTypes, Connect)
related:
  - ./DirectConnect.md
  - ./ListenHost.md
  - ./NetworkRoles.md
tags: [network]
---

# Connection failures are silent

When a RakNet connection attempt fails, the game learns nothing. `NetworkManager.ReceiveEvents` has no case and no `default:` arm for any of the six connection-failure message ids: the packet is received, deallocated, and dropped with no log line, no state change and no UI. The only thing that ever tells a player is a 10 second timer that pops a generic modal, and that timer is not armed on every join path.

This matters to anyone driving the game programmatically. A failed join looks exactly like a join that is still in progress.

## `ReceiveEvents` handles five ids and ignores the rest

<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

`ReceiveEvents` splits on `if (num >= 134)`. Ids at or above 134 go to the game's own `NetworkChannel` enum (`GeneralTraffic = 134`, :272554-272564, matching RakNet's `UserPacketEnum = 134` at Brutal.RakNet.decompiled.cs:684). Everything below goes to the RakNet switch, verbatim and complete (:273572-273599):

```csharp
else
{
    switch ((DefaultMessageIDTypes)num)
    {
    case DefaultMessageIDTypes.DisconnectionNotification:
        ConsoleWindow.Print("Client has disconnected.");
        PlayerDisconnected(num3);
        break;
    case DefaultMessageIDTypes.ConnectionLost:
        ConsoleWindow.Print("A client lost the connection.");
        PlayerDisconnected(num3);
        break;
    case DefaultMessageIDTypes.NewIncomingConnection:
        ConsoleWindow.Print("A connection is incoming.");
        PlayerConnected(num3, ConnectionMethod.RocketNet);
        break;
    case DefaultMessageIDTypes.ConnectionRequestAccepted:
        ConsoleWindow.Print("Our connection request has been accepted.");
        NetworkState = NetworkState.Online;
        _hostId = num3;
        PlayerConnected(num3, ConnectionMethod.RocketNet);
        break;
    case DefaultMessageIDTypes.NoFreeIncomingConnections:
        ConsoleWindow.Print("The server is full.");
        NetworkState = NetworkState.Offline;
        break;
    }
}
return true;
```

| Id | Enum name | Effect |
|---|---|---|
| 16 | `ConnectionRequestAccepted` | `NetworkState = Online`, `_hostId`, `PlayerConnected` |
| 19 | `NewIncomingConnection` | `PlayerConnected` (server side) |
| 20 | `NoFreeIncomingConnections` | log, `NetworkState = Offline` |
| 21 | `DisconnectionNotification` | log, `PlayerDisconnected` |
| 22 | `ConnectionLost` | log, `PlayerDisconnected` |

Unhandled, all six of them:

| Id | Enum name |
|---|---|
| 17 | `ConnectionAttemptFailed` |
| 18 | `AlreadyConnected` |
| 23 | `ConnectionBanned` |
| 24 | `InvalidPassword` |
| 25 | `IncompatibleProtocolVersion` |
| 26 | `IpRecentlyConnected` |

A whole-assembly grep for those six names plus `DefaultMessageIDTypes` returns only the six lines inside `ReceiveEvents` (:273574, :273576, :273580, :273584, :273588, :273594). None of the six failure names appears anywhere else in the assembly.

**There is no `default:` arm on the RakNet switch.** The contrast with the `NetworkChannel` switch immediately above it, which does have one (`default: throw new ArgumentOutOfRangeException();`), makes the omission look deliberate rather than accidental. So id 17 is received, copied into `Buffer`, `TimeSincePacketReceived` reset, deallocated, and then falls through to `return true;`. `ManagerUpdate`'s `while (ReceiveEvents()) { }` spins on to the next packet. The client stays `NetworkRole.Client` and `NetworkState.WaitingForConnection` indefinitely, and `EnsureRakNet()` keeps the peer alive because `NetworkState != Offline`.

## RakNet gives up at about 6 seconds

<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

`NetworkManager.StartClient` (:273391) passes no retry arguments:

```csharp
ConnectionAttemptResult connectionAttemptResult = Instance.rakNet.Connect(array, port, ReadOnlySpan<byte>.Empty, null);
```

so both defaults apply (Brutal.RakNet.decompiled.cs, `Connect`):

```csharp
uint sendConnectionAttemptCount = 12u, uint timeBetweenSendConnectionAttemptsMS = 500u, uint timeoutTime = 0u
```

12 attempts at 500 ms is about **6.0 seconds** before RakNet abandons the attempt and queues `ID_CONNECTION_ATTEMPT_FAILED`, which the game then ignores. `timeoutTime = 0` means "use the peer default" and governs an established connection, not the attempt; the game never calls `SetTimeoutTime`.

## The 10 second modal is the only player-visible signal

<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

`OnJoinStart` does not arm the timer itself. It calls `PauseEventJoiningClient()`, which does:

```csharp
private static readonly System.Timers.Timer _connectionTimer = new System.Timers.Timer(10000.0);
```

```csharp
private static async void ConnectionTimerOnElapsed(object sender, ElapsedEventArgs e)
{
    ConsoleWindow.PrintError("Connection could not be established");
    await UniTask.SwitchToMainThread();
    StopConnectionTimer();
    Singleton<ConfirmationPanel>.Instance.Show(MultiplayerCouldNotConnectKey, MultiplayerCheckAddressKey, "ButtonOk", Cancel);
}
```

It fires once (stopping itself despite `AutoReset = true`), prints one error, and shows a generic modal whose OK button calls `Cancel`, which calls `GameManager.LeaveGame()`. It is cancelled on success by `NetworkClient.Handshake` (:213022), `ReceiveJoinFragment` (:213062) and the `VerifyPlayer` path (:279397).

Net effect: **RakNet gives up at about 6 s, the game notices nothing, and the player finds out at 10 s** from a message that cannot distinguish refused, banned, wrong password, version mismatch or full.

## Three paths where even that signal is absent

<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

- **`ConsoleWindow.ConnectTo` (:222751) bypasses the timer entirely.** It calls `NetworkManager.StartClient` directly, with no `ClientPreJoin` and no `OnJoinStart`, so nothing arms `_connectionTimer`. A join driven through the console has no modal and no timeout of any kind: a failure there is completely silent. Only `JoinClientFromMenu` (:213126) and `JoinWithSteamP2P` (:213095) arm it. **Any tool that drives a join must use one of those two, or implement its own timeout.**
- **The early-failure path shows no modal.** If `Startup` fails or `Connect` returns anything but `ConnectionAttemptStarted`, `StartClient` returns false and `OnJoinFailed()` runs instead of `OnJoinStart()`, so the timer is never armed. `OnJoinFailed` is a `ConsoleWindow.PrintError` and nothing else. `ClientPreJoin()` has already set `GameManager.GameState = GameState.Joining` and nothing resets it, leaving the game in `Joining` on the main menu.
- **A suppressed timer removes the last signal.** A driver that calls `NetworkClient.StopConnectionTimer()` to survive a slow modded join (as this repo's `ClientDriver` does) disarms the only thing that would have reported the failure, and must supply its own.

## Ban, password and version rejection are application-level

<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

Ids 23 and 24 are effectively unreachable against a vanilla server: the game never calls `SetIncomingPassword`, `AddToBanList` or `IsBanned` (zero hits assembly-wide). Those rejections happen after a successful RakNet handshake, as a `NetworkMessages.Handshake` with `HandshakeType.Rejected` over `NetworkChannel.GeneralTraffic`, and that path does surface correctly:

```csharp
case HandshakeType.Rejected:
    Singleton<ConfirmationPanel>.Instance.Show(MultiplayerRejectedKey, handshake.Message, "ButtonOk", Cancel);
    Assets.Scripts.Networking.NetworkManager.EndConnection();
    break;
```

with server-side senders `HandleBanned`, `HandleIncorrectPassword` and `HandleIncorrectVersion` (:213747-213768). Id 25 `IncompatibleProtocolVersion` stays reachable if the RakNet wire protocol differs across builds, and it is silent.

## Verification history

<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

- 2026-08-09: page created. Written after a driven two-instance join failed six times with no diagnostic anywhere, which sent the investigation down three wrong paths before the cause of the SILENCE was understood. A fresh validator, given the bare question with no indication of the expected answer, independently enumerated the switch, confirmed the absence of a `default:` arm, confirmed the whole-assembly grep, and established the 12x500 ms retry defaults and the 10 second timer. It additionally found the `ConnectTo` and early-failure paths where even the modal is absent, and the application-level rejection path that does work.

## Open questions

- The exact moment RakNet raises `ID_CONNECTION_ATTEMPT_FAILED` relative to the twelfth datagram is inside the native `RakNetDLL` and not visible in the managed decompile. About 6 s is the calculated figure, not a measured one.
- Whether `IpRecentlyConnected` (26) can fire against a vanilla server. `SetLimitIPConnectionFrequency` is never called, so the behaviour is whatever the native library defaults to.
