---
title: Direct-Connect Server Join Flow
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-14
sources:
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs (ServerListManager, NetworkClient, NetworkManager)
related: []
tags: [multiplayer, networking, ui-menu, harmony-patch-sites]
---

## Direct-Connect UI Layer

<!-- verified: 0.2.6228.27061 @ 2026-05-14 -->

**Class:** `ServerListManager` (line 46830)
**File:** Assembly-CSharp.decompiled.cs

The UI component owns the direct-connect input field and button. Key methods:

- **OnDirectConnect()** (line 47137): Button callback. Trims `_addressInputField.text` and passes to `NetworkClient.JoinClientFromMenu()`.
- **OnInputValueChanged()** (line 46913): Called on every keystroke via `_addressInputField.onValueChanged` listener. Enables/disables the "Connect" button based on `IsValidIpAddressAndPort()`.
- **IsValidIpAddressAndPort()** (line 46918): Returns `ValidIpPattern.Match(address).Success`.

Field: `_addressInputField` is a TMP_InputField (line 46836).

## IP Address Validation (Hostname Blocker)

<!-- verified: 0.2.6228.27061 @ 2026-05-14 -->

**Static Regex:** `ValidIpPattern` (line 46878)

```csharp
private static readonly Regex ValidIpPattern = new Regex("^(?:[0-9]{1,3}\.){3}[0-9]{1,3}:[0-9]{1,5}\s*$");
```

Pattern matches only IPv4 dotted-quad format with colon-delimited port: `###.###.###.###:port`. No hostname support. This regex blocks any domain name input—the button remains grayed out until the user types an IP.

**This is the choke point for the hostname problem.** Any Harmony patch enabling hostnames must either:
1. Patch `IsValidIpAddressAndPort()` to accept hostnames, or
2. Replace `ValidIpPattern` with a less restrictive regex, or
3. Patch the button-enabling logic upstream in `OnInputValueChanged()`.

## Connection Initiation

<!-- verified: 0.2.6228.27061 @ 2026-05-14 -->

**Method:** `NetworkClient.JoinClientFromMenu()` (line 198051)

Flow:
1. Splits input on ':' to extract address and port.
2. Special case: if length == 1 and the string is 17 chars, treats it as a SteamId and calls `JoinWithSteamP2P()`.
3. If length != 2, shows error dialog.
4. Parses port as ushort (line 198071).
5. Calls `Assets.Scripts.Networking.NetworkManager.StartClient(address, port, localPort)` (line 198073).

**Method:** `NetworkManager.StartClient(string address, ushort port, ushort localPort)` (line 255869)

- Uses RakNet directly: calls `Instance.rakNet.Connect(array, port, ReadOnlySpan<byte>.Empty, null)` (line 255889).
- Converts the address string to a null-terminated UTF-8 byte array (lines 255885–255888) and passes it to RakNet.
- On success, stores address and port in `NetworkClient.Address` and `NetworkClient.Port` static fields (lines 255898–255899) and logs `"Connected to {address} on port {port}"`.

Whether RakNet performs DNS resolution on a hostname-shaped byte array is **not visible in the C# decompile** — `RakPeerInterface.Connect` is a managed wrapper over native RakNet code. The C# layer does not pre-resolve. See "Open questions" below.

### DNS helper already in the game

**Method:** `NetworkManager.ResolveIpAddress(string address)` (line 255859, immediately above `StartClient`)

```csharp
public static string ResolveIpAddress(string address)
{
    IPAddress[] hostAddresses = Dns.GetHostAddresses(address);
    if (hostAddresses.Length == 0)
    {
        return string.Empty;
    }
    return hostAddresses[0].ToString();
}
```

This static helper resolves a hostname to a dotted-quad string (or `""` on failure). It is defined but **not called from `StartClient`**. A Harmony patch that needs to feed an IP to the game can call this helper directly instead of bundling its own `System.Net.Dns` invocation.

**Harmony patch site #1:** `NetworkManager.StartClient()` (prefix) — call `ResolveIpAddress(address)` when `address` is not dotted-quad, substitute the result. Defensive against unknown RakNet DNS behavior.
**Harmony patch site #2:** `NetworkClient.JoinClientFromMenu()` (prefix) — alternative interception site; can preprocess the input string before the colon split / port parse.

## Server Persistence (Last Server Forgotten)

<!-- verified: 0.2.6228.27061 @ 2026-05-14 -->

**Finding:** No PlayerPrefs entry for the last direct-connect address exists in `ServerListManager` or `NetworkClient`.

PlayerPrefs used in ServerListManager (line 46899–46901, 46927–46935, 46942–46962):
- `FILTER_VERSION`, `FILTER_PASSWORD`, `FILTER_EMPTY` (filter toggles)
- `JoinSortBy` (sort preference)

None of these store the address field. The `_addressInputField` text is not persisted anywhere. On app quit, it is lost.

**Harmony patch site #3:** `ServerListManager.OnDisable()` or application quit hook to save `_addressInputField.text` to PlayerPrefs (e.g., key `LAST_DIRECT_CONNECT_ADDRESS`).

**Harmony patch site #4:** `ServerListManager.Awake()` or `Start()` to restore the saved address and repopulate `_addressInputField.text`.

## Port Handling

<!-- verified: 0.2.6228.27061 @ 2026-05-14 -->

Port is extracted from the input string during parsing in `JoinClientFromMenu()` (line 198071):
```csharp
ushort num = ushort.Parse(array[1]);
ushort localPort = (ushort)(num + 1);
```

The local port is automatically set to one higher than the server port. Both are passed to `StartClient()`. No separate port input field exists in the UI; port is baked into the "address:port" string format required by the regex.

## Verification history

- 2026-05-14: Initial findings. Decompiled v0.2.6228.27061. Located ServerListManager, ValidIpPattern regex (IPv4-only), JoinClientFromMenu parsing, RakNet integration point, no persistence on address field.
- 2026-05-14: Added `NetworkManager.ResolveIpAddress` (line 255859) as a ready-made DNS helper the game already ships. Walked back the unverified claim that `RakPeerInterface.Connect` resolves hostnames internally; the C# decompile does not reveal native RakNet behavior, so a patch should not assume it.

## Open questions

- Does the native `RakPeerInterface.Connect` resolve hostnames internally, or does it require a numeric IP in the UTF-8 byte array? Not visible in the C# decompile. Resolved cheaply at implementation time by either (a) pre-resolving with `NetworkManager.ResolveIpAddress` defensively or (b) testing with a hostname and inspecting RakNet's failure mode.
- What does RakNet do if a hostname fails to resolve? Likely returns `ConnectionAttemptResult` != `ConnectionAttemptStarted`, which `StartClient` already surfaces via `ConsoleWindow.PrintAction(...)` (line 255892) and `NetworkClient.OnJoinFailed()`. Verify when implementing.
