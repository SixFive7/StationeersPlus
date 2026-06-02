---
title: NetworkManager
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-03
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll::NetworkManager class
  - rocketstation_Data/Managed/Assembly-CSharp.dll::NetworkManager.StartServer
  - rocketstation_Data/Managed/Assembly-CSharp.dll::NetworkManager.GetIPv4Address
  - rocketstation_Data/Managed/Assembly-CSharp.dll::Settings.SettingData.LocalIpAddress
related:
  - GameClasses/NetworkServer.md
tags: [network]
---

# NetworkManager

Stationeers multiplayer networking manager for client-hosted sessions. Uses RakNet UDP sockets with fallback behavior and Steam P2P capability.

## NIC Selection and IP Binding
<!-- verified: 0.2.6228.27061 @ 2026-06-03 -->

When a client starts hosting a multiplayer game, `NetworkManager.StartServer(ushort port)` (line 255734) determines which local IP to bind:

1. **Manual override first**: If `Settings.CurrentData.LocalIpAddress` is non-empty, use it directly (line 255743-255748).
2. **Auto-detection fallback**: If not set, call `GetIPv4Address()` (line 255752).

The auto-detection method `GetIPv4Address()` (line 255705-255732) enumerates all RakNet-reported local IPs in *reverse order* (line 255712: `for (int num2 = (int)(numberOfAddresses - 1); num2 >= 0; num2--)`). It returns the **last** non-loopback IPv4 address found. This reverse enumeration is the root cause of incorrect NIC selection when a machine has both a physical NIC and virtual adapters (e.g., Hyper-V Default Switch): RakNet's adapter enumeration order may place the virtual adapter last, and the reverse loop makes it first to be returned.

Code excerpt (line 255705-255732):
```csharp
public static string GetIPv4Address()
{
    uint numberOfAddresses = Instance.rakNet.GetNumberOfAddresses();
    if (numberOfAddresses != 0)
    {
        ConsoleWindow.Print("Found Ipv4 addresses");
        int num = -1;
        for (int num2 = (int)(numberOfAddresses - 1); num2 >= 0; num2--)
        {
            string localIP = Instance.rakNet.GetLocalIP((uint)num2);
            ConsoleWindow.Print(localIP);
            if (IPAddress.TryParse(localIP, out var address))
            {
                byte[] addressBytes = address.GetAddressBytes();
                if (address.AddressFamily == AddressFamily.InterNetwork && addressBytes[0] != 127)
                {
                    num = num2;
                }
            }
        }
        if (num == -1)
        {
            throw new System.Exception("No network adapters with an IPv4 address in the system!");
        }
        return Instance.rakNet.GetLocalIP((uint)num);
    }
    throw new System.Exception("No network adapters with an IPv4 address in the system!");
}
```

The binding and socket creation happens at line 255755-255763:
```csharp
SocketDescriptor socketDescriptor = new SocketDescriptor(text2, port);
Span<SocketDescriptor> socketDescriptors = stackalloc SocketDescriptor[1] { socketDescriptor };
StartupResult startupResult = Instance.rakNet.Startup((uint)MaxConnections, socketDescriptors, 2);
if (startupResult != StartupResult.RaknetStarted)
{
    ConsoleWindow.PrintAction("Hosting failed. Attempting fallback behaviour");
    SocketDescriptor socketDescriptor2 = new SocketDescriptor("", port);
    Span<SocketDescriptor> socketDescriptors2 = stackalloc SocketDescriptor[1] { socketDescriptor2 };
    startupResult = Instance.rakNet.Startup((uint)MaxConnections, socketDescriptors2, 2);
}
```

## Settings.LocalIpAddress Configuration
<!-- verified: 0.2.6228.27061 @ 2026-06-03 -->

`Settings.SettingData` (line 248235) declares `LocalIpAddress` as an XML-serialized field (line 248482):
```csharp
[XmlElement]
public string LocalIpAddress = string.Empty;
```

This field is serialized to/from the player's settings XML file and can be edited by the user. It is not populated from command-line arguments; it is read from the persistent settings store only.

## Command-Line Argument Handling
<!-- verified: 0.2.6228.27061 @ 2026-06-03 -->

The game parses launch arguments in `CommandLine.ProcessOnLaunch()` (line 95067-95076) via `Environment.GetCommandLineArgs()` and a `CommandLineArgs` property (line 94938). The `TryGetArg` method (line 95079-95096) retrieves argument values.

Exhaustive scan of the decompiled assembly shows **no command-line flag that accepts or sets an IP address for multiplayer binding**. The only launch-time network-related flag found is `-nodiscord` / `--nodiscord` (line 177514) which disables Discord integration, not networking.

The game's command registry at lines 94942-95039 includes console commands (executed in-game) but none that parse `-bindaddress`, `-bind`, `-ip`, `-host`, `-localip`, or similar. The console command list (starting at line 94944) includes `upnp`, `network`, `netconfig`, and various server / client management commands, but none expose IP binding via console arguments either.

## Transport Type
<!-- verified: 0.2.6228.27061 @ 2026-06-03 -->

Multiplayer uses two parallel transports:

1. **RakNet UDP** (default): All game-state replication. Bound to the selected local IP per `GetIPv4Address()` or `LocalIpAddress` setting.
2. **Steam P2P** (optional, controlled by `Settings.CurrentData.UseSteamP2P`): Supplemental connections via `SteamNetworking` API (line 255605-255623, 255961-255963). Does not depend on local IP binding; routed through Steam's relay infrastructure.

The RakNet transport is the one that exhibits the NIC-selection bug. Steam P2P does not suffer from it because Valve's relay network does not depend on the host's local adapter enumeration order.

## Verification history

- 2026-06-03: initial page creation from Assembly-CSharp.decompiled.cs (version 0.2.6228.27061)

## Open questions

- Why does `GetIPv4Address()` iterate RakNet's address list in reverse? Is this intentional (oldest adapter preferred) or a bug? Original commit history not available in the decompiled artifact.
- What is RakNet's adapter enumeration order on Windows with Hyper-V virtual switches? Does it correlate with the order in `ipconfig` or the routing table?
