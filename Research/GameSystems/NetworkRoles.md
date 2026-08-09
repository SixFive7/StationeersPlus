---
title: NetworkRoles
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-08-09
sources:
  - Mods/SprayPaintPlus/RESEARCH.md:128-137
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs (NetworkManager, NetworkRole, NetworkServer, GameManager)
related:
  - ../Patterns/SinglePlayerNetworkRole.md
  - ./ListenHost.md
tags: [network]
---

# NetworkRoles

How `NetworkManager.IsActive`, `IsServer`, and `IsClient` combine across single-player, multiplayer host, multiplayer client, and dedicated server modes, and why the `IsActive && !IsServer` guard is the correct remote-client check.

## Role flag matrix
<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

| Scenario | IsActive | IsServer | IsClient |
|---|---|---|---|
| Single-player | false | false | false |
| Multiplayer host (listen host) | true | true | **false** |
| Multiplayer client | true | false | true |
| Dedicated server | true | true | false |

The `IsActive && !IsServer` guard correctly identifies remote clients without catching single-player.

**A listen host and a dedicated server are indistinguishable by these three flags.** Both reach `NetworkManager.StartServer` through the same `GameManager.StartGame` branch (`if (Settings.CurrentData.StartLocalHost || IsBatchMode)`, Assembly-CSharp.decompiled.cs:204603), so the two rows cannot differ. To tell them apart, read `GameManager.IsBatchMode`, or `NetworkManager.LocalClientId` (0 on a dedicated server, because `Cookie` is only loaded when not in batch mode, :273983). See `ListenHost.md`.

## The three flags are one enum, not three booleans
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

```csharp
public static NetworkRole NetworkRole = NetworkRole.None;   // :272668

public static bool IsActive => NetworkRole != NetworkRole.None;   // :272706-272710
public static bool IsClient => NetworkRole == NetworkRole.Client;
public static bool IsServer => NetworkRole == NetworkRole.Server;
```

There is no `Host` value, and `IsServer` and `IsClient` are mutually exclusive by construction. **No process can ever report both.** Beyond the representation, `CanBecome` (:273434-273451) returns `NetworkRole == NetworkRole.None` for both the `Server` and `Client` cases, so a process that is already one cannot become the other without first resetting to `None` (:272777).

Every write to `NetworkRole` across every decompiled assembly (Assembly-CSharp, StationeersLaunchPad, LaunchPadBooster, BlueprintMod) lives inside `NetworkManager`, five in total: `= None` at :272777 (network reset), `= Server` at :273120 (`ProcessP2PSessionRequest`) and :273274 (`StartServer`), `= Client` at :273402 and :273423 (the two `StartClient` overloads). No third-party assembly writes it.

`NetworkManager.HostClient` and the `Client` record with `IsHost = true` created by `NetworkServer.PopulateHostClient()` (:213672) describe the host player in the roster. They are not role flags and do not affect `IsClient`.

## Verification history
<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0017 (Mods/SprayPaintPlus/RESEARCH.md:128-137).
- 2026-08-09: conflict on "IsClient on a multiplayer host". Previous claim: a multiplayer host reports `IsActive/IsServer/IsClient` as true/true/true. New finding: a listen host reports true/true/false, because the three properties are mutually exclusive views of one `NetworkRole` enum that has no `Host` value. Fresh validator verdict: the new finding is correct, quoting the enum declaration at Assembly-CSharp.decompiled.cs:272571-272576 and the property definitions at :272706-272710. Result: the host row's `IsClient` column corrected to false; a new section added recording the single-enum representation, the five write sites, and `CanBecome`'s refusal; note added that a listen host and a dedicated server cannot be distinguished by these flags; page restamped to 0.2.6403.27689.

## Open questions

None.
