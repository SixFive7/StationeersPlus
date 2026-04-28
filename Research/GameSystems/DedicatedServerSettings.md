---
title: Dedicated Server Settings
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-28
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Settings.SettingData (decompile lines 248232-248613)
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: CommandLine (decompile lines 94926-95177)
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: SaveCommand / QuitCommand / LoadGameCommand / LoadLatestCommand / NewGameCommand / SettingsCommand / SettingsPathCommand / ServerRunCommand / BanCommand
related:
  - GameSystems/NetworkRoles.md
  - Patterns/ServerAuthoritativeSimulation.md
  - Patterns/SinglePlayerNetworkRole.md
tags: [network, save-load]
---

# Dedicated Server Settings

The Stationeers Dedicated Server (Steam app `600760`) shares its configuration surface with the regular client build. There is no server-only Settings class and no server-only command dispatcher. The dedicated build is the same Unity assembly with `Application.platform == WindowsServer` (or `-batchmode`) flipping a handful of conditional paths.

Three orthogonal configuration layers determine how the server runs:

1. **Unity built-in launch flags** (`-batchmode`, `-nographics`, `-logFile <path>`, `-screen-*`, etc.) are consumed by the Unity engine before any Stationeers code runs. They are not in this page; see Unity's command-line reference.
2. **Stationeers settings** (`Settings.SettingData`, persisted to `setting.xml`). 80+ XML-serialized fields covering both client UI/performance and server behaviour. Set via `-settings <Field> <Value>` on the launch line, written into `setting.xml` by the runtime, or hand-edited in the file.
3. **Stationeers commands** (`CommandLine` dispatcher, 70+ entries). Same dictionary serves both launch flags (with `-` prefix, multiple per launch) and runtime stdin (no prefix, one per line). Each command exposes `HelpText`, `Arguments`, and `IsLaunchCmd`.

## Architecture
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

The single dispatcher is `CommandLine.Process(string[], bool onLaunch = false)` (decompile line 95111). When `onLaunch` is true (called from `[RuntimeInitializeOnLoadMethod] CommandLine.ProcessOnLaunch`), every `-name` token starts a new command and subsequent non-dash tokens accumulate as that command's arguments. When `onLaunch` is false (stdin path), only the first token may be `-name`-prefixed; later tokens are arguments to the first command. Same dispatch table either way.

The dispatch dictionary is initialised in the static constructor of `CommandLine` (line 94942):

```csharp
public static class CommandLine
{
    private static readonly SortedDictionary<string, CommandBase> _commandsMap;
    ...
    static CommandLine()
    {
        _commandsMap = new SortedDictionary<string, CommandBase>
        {
            ["achievements"] = new AchievementsCommand(),
            ["help"] = new HelpCommand(),
            ...
            ["save"] = new SaveCommand()
        };
        ...
    }
}
```

Each registered command derives from `CommandBase` and overrides:

- `string HelpText` (one-line description for the `help` command)
- `string[] Arguments` (argument shape; used by `help` to format usage)
- `bool IsLaunchCmd` (advisory: whether the command makes sense on the launch line)
- `string Execute(string[] args)` (returns a string to print, or null)

Two helpers used inside Execute bodies are worth knowing about:

- `CommandBase.CannotAsClient(name)` returns true when the local process is a remote client, blocking commands that only make sense on the server (Save, Ban, Kick).
- `CommandBase.CannotInSinglePlayer(name)` returns true in single-player, blocking client/server-only commands like Ban.

## Settings (`setting.xml` keys)
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

The `setting.xml` file path is computed lazily by `Settings.SettingData.Path` (line 248582):

```csharp
public static string Path
{
    get
    {
        if (string.IsNullOrEmpty(_path))
        {
            _path = System.IO.Path.Combine(StationSaveUtils.GetSavePath(), "setting.xml");
        }
        return _path;
    }
    set { _path = value; }
}
```

So `setting.xml` defaults to `<SavePath>/setting.xml` where `SavePath` is itself a SettingData field. `-settingspath <file-path>` (the SettingsPathCommand at line 98695) overrides this explicitly; useful for forcing a server's `setting.xml` to a specific location independent of `SavePath`.

`-settings <Field> <Value>` is reflection-based via `ClassManipulator<Settings.SettingData>` (the SettingsCommand at line 98668). Every field in SettingData below is settable by name (case-insensitive). After each set, `Settings.SaveSettings()` writes the value back to `setting.xml` and `NetworkManager.UpdateSessionData(ObjectInstance)` propagates the change.

### Server-relevant fields

Drawn verbatim from `Settings.SettingData` (lines 248236-248577).

| Field | Type | Default | XmlElement | Notes |
|---|---|---|---|---|
| `ServerName` | string | `"Stationeers"` | yes | Display name in browser. |
| `StartLocalHost` | bool | `false` | yes | Client-side "host this world on launch from main menu" toggle. The dedicated build's `-load` / `-new` / `-loadlatest` start the network listener directly; this flag is not required for a dedicated launch. |
| `ServerVisible` | bool | `false` | yes | Publish to public server browser. Leave false for local-only. |
| `ServerPassword` | string | `""` | yes | Empty means open server. Clients must enter the same string in Direct Connect. |
| `AdminPassword` | string | `""` | yes | In-client admin commands. |
| `ServerAuthSecret` | string | `""` | NO `[XmlElement]` decoration but XmlSerializer persists public fields by default | Shared secret. Required for `serverrun`. Server prints `serverrun command can only be used if a ServerAuthSecret is set in setting.xml` when missing (line 98635). |
| `ServerMaxPlayers` | int | `10` | yes | No explicit clamping observed in the SettingData class. Public docs claim 1-30; enforcement may live in UI validation or join-time logic, not verified this pass. |
| `UpdatePort` | string | `"27015"` | yes | UDP. Note string-typed despite holding a port number. |
| `GamePort` | string | `"27016"` | yes | UDP. Note string-typed. |
| `UPNPEnabled` | bool | `true` | yes | Set false for local-only servers to avoid advertising via UPnP. |
| `UseSteamP2P` | bool | `true` | yes | Steam relay. |
| `DisconnectTimeout` | int | `10000` | yes | Milliseconds. |
| `NetworkDebugFrequency` | int | `500` | yes | Milliseconds. |
| `LocalIpAddress` | string | `""` | yes | Bind interface. Empty means default. |
| `AutoPauseServer` | bool | `true` | NO `[XmlElement]` decoration but XmlSerializer persists public fields by default | Pauses world simulation when no clients are connected. Relevant for tests that depend on game-time progression without a connected client. |
| `AutoSave` | bool | `true` | yes | Recurring autosave. |
| `SaveInterval` | int | `300` | yes | Seconds between autosaves. |
| `MaxAutoSaves` | int | `5` | yes | Rotation count. |
| `MaxQuickSaves` | int | `5` | yes | Rotation count. |
| `SavePath` | string | `""` | yes | Empty falls back to `StationSaveUtils.DefaultPath` (in batch mode: exe directory). Setting this to a directory makes saves, scripts, mods, and setting.xml itself live under it. |

### Client UI / performance fields (full list, verbatim)

These fields live in the same SettingData struct and are settable on a dedicated server, but the server's batch-mode rendering loop does not consume most of them. Listed for completeness and for hand-editing `setting.xml`.

| Field | Type | Default |
|---|---|---|
| `SettingsVersion` | string | `string.Empty` |
| `ShowFps` | bool | `false` |
| `ShowLatency` | bool | `false` |
| `HUDScale` | int | `50` |
| `TooltipOpacity` | float | `0.95` |
| `IngamePortrait` | bool | `true` |
| `ExtendedTooltips` | bool | `true` |
| `ChatFadeTimer` | float | `10` |
| `DayLength` | int | `20` |
| `LegacyInventory` | bool | `false` |
| `ShowSlotToolTips` | bool | `true` |
| `DeleteSkeletonOnDecay` | DeleteSkeletonOnDecay | enum default |
| `Monitor` | int | `1` |
| `ScreenWidth` | string | `"1920"` |
| `ScreenHeight` | string | `"1080"` |
| `RefreshRate` | int | `60` |
| `GraphicQuality` | string | `"Fantastic"` |
| `TextureQuality` | string | `"Very High"` |
| `FullScreen` | bool | `true` |
| `Vsync` | bool | `false` |
| `Shadows` | string | `"High"` |
| `DistantShadows` | bool | `false` |
| `ShadowResolution` | string | `"Very High"` |
| `ShadowDistance` | int | `100` |
| `LightShadowDistance` | int | `50` |
| `RoomControlTickSpeed` | int | `1` |
| `ShadowNearPlaneOffset` | float | `0.2` |
| `ShadowCascades` | int | `4` |
| `ShadowCascade2Split` | float | `1f / 3f` |
| `ShadowCascade4Split` | Vector3 | `(1f/15f, 0.2f, 7f/15f)` |
| `ThingShadowMode` | string | `"High"` |
| `ThingShadowDistanceMultiplier` | float | `2` |
| `RenderDistance` | string | `"High"` |
| `WorldOrigin` | bool | `false` |
| `Brightness` | int | `100` |
| `FieldOfView` | int | `70` |
| `ColorBlind` | string | `"None"` |
| `ParticleQuality` | string | `"High"` |
| `SoftParticles` | bool | `true` |
| `EnvironmentElements` | bool | `true` |
| `ExtendedTerrain` | bool | `true` |
| `VolumeLight` | string | `"Full"` |
| `PixelLightCount` | int | `8` |
| `MaxThingLights` | int | `256` |
| `Antialiasing` | string | `"FXAA"` |
| `FrameLock` | string | `"Off"` |
| `AtmosphericScattering` | bool | `true` |
| `AmbientOcclusion` | string | `"Ultra"` |
| `LensFlares` | bool | `true` |
| `DisableWaterVisualizer` | bool | `true` |
| `Clouds` | bool | `false` |
| `HelmetOverlay` | bool | `true` |
| `WeatherEventQuality` | string | `"Medium"` |
| `TerrainDetail` | string | `"Medium"` |
| `MinableDistance` | string | `"Medium"` |
| `TerrainDistance` | string | `"Medium"` |
| `MasterVolume` | int | `100` |
| `SoundVolume` | int | `100` |
| `VoiceNotificationVolume` | int | `90` |
| `MusicVolume` | int | `100` |
| `InterfaceVolume` | int | `100` |
| `VirtualVoices` | int | `512` |
| `RealVoices` | int | `32` |
| `UserSpeakerMode` | AudioSpeakerMode | `Stereo` |
| `LanguageCode` | LanguageCode | `EN` |
| `VoiceLanguageCode` | LanguageCode | `EN` |
| `Voice` | bool | `false` |
| `PopupChat` | bool | `true` |
| `CameraSensitivity` | int | `50` |
| `KeyList` | List<KeyItem> | empty |
| `InvertMouse` | bool | `false` |
| `InvertMouseWheelInventory` | bool | `false` |
| `MenuLite` | bool | `false` |
| `MouseWheelZoom` | bool | `true` |
| `FirstRun` | bool | `true` |
| `VoiceNotifications` | List<VoiceNotificationData> | empty |
| `CompletedTutorials` | List<long> | empty |
| `CompletedScenarios` | List<long> | empty |
| `DisplayHelperHints` | bool | `true` |
| `AutoExpandHelperHints` | bool | `true` |
| `VerticalMovementAxis` | ControllerData | default |
| `HorizontalMovementAxis` | ControllerData | default (no `[XmlElement]`) |
| `ForwardMovementAxis` | ControllerData | default (no `[XmlElement]`) |
| `VerticalLookAxis` | ControllerData | default (no `[XmlElement]`) |
| `HorizontalLookAxis` | ControllerData | default (no `[XmlElement]`) |
| `UseCustomWorkThreadsCount` | bool | `false` |
| `MinWorkerThreads` | int | `Environment.ProcessorCount` |
| `MinCompletionPortThreads` | int | `Environment.ProcessorCount` |
| `MaxWorkerThreads` | int | `(Environment.ProcessorCount + 2) * 10` |
| `MaxCompletionPortThreads` | int | `(Environment.ProcessorCount + 2) * 5` |
| `MaxConcurrentWorkers` | int | `Environment.ProcessorCount - 1` |
| `CoroutineTimeBudget` | float | `1` |
| `SmoothTerrain` | bool | `false` |
| `SmoothTerrainAngle` | float | `60` |
| `ConsoleBufferSize` | int | `1024` |
| `LegacyCpu` | bool | `false` |

## Command-line flags
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

The dispatch dictionary as registered in `CommandLine`'s static constructor (decompile lines 94944-95038). Names with multiple keys (aliases) are grouped. Where a command exposes `IsLaunchCmd = true`, it can be used as a `-name` flag at launch; otherwise it is runtime-only (stdin or `serverrun`).

| Name(s) | Class | IsLaunchCmd | HelpText (verbatim where present) |
|---|---|---|---|
| `achievements` | AchievementsCommand | (mixed) | (not read this pass) |
| `help` | HelpCommand | (read help) | "Lists all available commands and their descriptions" (typical) |
| `clear` | ClearCommand | false | clears the console buffer |
| `quit` | QuitCommand | (any) | "immediately quits the game without any prompts" |
| `exit`, `leave` | ExitCommand | (alias for quit) | (not read this pass) |
| `newgame`, `new` | NewGameCommand | true | "Starts a new game at specific world automatically from launch.Must provide world name as argument" |
| `joingame`, `join` | JoinCommand | (mixed) | (not read this pass) |
| `steam` | SteamCommand | (mixed) | (not read this pass) |
| `listnetworkdevices` | ListNetworkDevicesCommand | false | (not read this pass) |
| `testbytearray` | TestByteArrayCommand | false | (debug) |
| `rocketbinary` | RocketBinaryCommands | false | (debug) |
| `imgui` | ImGuiCommands | false | (debug) |
| `atmos` | AtmosphereCommands | false | (debug) |
| `structurenetwork` | StructureNetworkCommand | false | (debug) |
| `thing` | ThingCommand | false | (debug) |
| `keybindings` | KeyBindingCommands | false | (debug) |
| `reset` | RestartCommand | false | "Restarts the application" |
| `version` | VersionCommand | false | (prints game version) |
| `rocket` | RocketCommand | false | (debug) |
| `unstuck` | UnstuckCommand | false | (debug) |
| `spacemap`, `spacemapnode` | SpaceMapCommand / SpaceMapNodeCommand | false | "Various space map debug functions" |
| `logtoclipboard` | LogToClipboardCommand | false | (debug) |
| `camera` | CameraCommand | false | (debug) |
| `kick` | KickCommand | false | server-only; disconnects a client |
| `ban` | BanCommand | false | "Bans a client from the server (server only command)". Args: `<clientId>` or `refresh` |
| `upnp` | UpnpCommand | (mixed) | (not read this pass) |
| `network` | NetworkCommand | false | (debug) |
| `pause` | PauseCommand | false | (toggle pause) |
| `say` | SayCommand | false | (server-side chat broadcast) |
| `world` | PrintWorldSettingsCommand | false | (debug) |
| `log` | LogCommand | false | (log inspection) |
| `discord` | DiscordCommand | false | (debug) |
| `settings` | SettingsCommand | true | "Change the settings.xml. e.g settings servermaxplayers 5" |
| `netconfig` | NetConfigCommand | true | (settings-style for NetConfig) |
| `settingspath` | SettingsPathCommand | true | "Sets the default settings path to a new location. Launch command only. If none found default is used." |
| `regeneraterooms` | RegenerateRoomsCommand | false | "Regenerates all rooms for the world" |
| `storm` | StormCommand | false | (debug) |
| `debugthreads` | DebugThreadsCommand | false | (debug) |
| `status` | StatusCommand | false | (server status snapshot) |
| `masterserver` | MasterServerCommand | (mixed) | (not read this pass) |
| `deletelooseitems` | DeleteLooseItemsCommand | false | (debug) |
| `emote` | EmoteCommand | false | (debug) |
| `expression` | CustomFacialExpressionCommand | false | (debug) |
| `serverrun` | ServerRunCommand | false | "Sends a message to the server to perform server side commands". Client-only; signs with `Settings.CurrentData.ServerAuthSecret`. |
| `windowheight` | ConsoleWindowHeightCommand | false | (debug) |
| `cleanupplayers` | CleanupPlayersCommand | false | (debug) |
| `networkdebug` | NetworkDebugWindowCommand | false | (debug) |
| `difficulty` | DifficultySettingsCommand | false | (debug) |
| `addgas` | AddGas | false | (debug) |
| `legacycpu` | LegacyCpuCommand | (mixed) | "Enables Legacy Cpu mode. Recommended for users with cpus below the recommended spec" |
| `trader` | TraderCommand | false | (debug) |
| `localization` | LocalizationCommand | false | (debug) |
| `deleteoutofbounds` | DeleteOutOfBoundsObjectsCommand | false | (debug) |
| `printgasinfo` | PrintPhaseChangeInfoCommand | false | (debug) |
| `structure` | StructureCommand | false | (debug) |
| `plant` | PlantCommand | false | (debug) |
| `physics` | PhysicsCommand | false | (debug) |
| `power` | PowerCommand | false | (debug) |
| `orbit` | OrbitalCommand | false | (debug) |
| `celestial` | CelestialCommand | false | (debug) |
| `dlc` | DLCCommand | false | (debug) |
| `entity` | EntityCommand | false | (debug) |
| `setbatteries` | SetBatteriesCommand | false | (debug) |
| `systeminfo` | SystemInfoCommand | false | (debug) |
| `profiler` | ProfilerCommand | false | (debug) |
| `prefabs` | ValidateSourcePrefabsCommands | false | (debug) |
| `helperhints` | WorldObjectiveCommand | false | (debug) |
| `exportworld` | ExportWorldCommand | false | (debug) |
| `worldsetting` | WorldSettingWindowCommand | false | (debug) |
| `liquid` | LiquidCommands | false | (debug) |
| `vegetation` | VegetationCommand | false | (debug) |
| `minables` | MinableCommand | false | (debug) |
| `testoctree` | TestOctreeCommand | false | (debug) |
| `terraineditor` | TerrainEditorWindowCommand | false | (debug) |
| `region` | RegionCommand | false | "Terrain region debugging" |
| `file` | FileCommand | (mixed) | (file ops) |
| `map` | MiniMapWindowCommand | false | (debug) |
| `terrain` | TerrainCommands | false | (debug) |
| `geyser` | GeyserCommand | false | (debug) |
| `reloadterraintexture` | ReloadTerrainTextureCommand | false | "Reloads the terrain textures from streaming assets" |
| `teleport` | TeleportCommand | false | (debug) |
| `lod` | LodDebugWindowCommand | false | (debug) |
| `clients` | ClientsCommand | false | (lists connected clients) |
| `densepools` | DensePoolCommand | false | (debug) |
| `loworbitstation` | LowOrbitStationCommand | false | (debug) |
| `clientinfo` | SerializedClientInfoCommand | false | (debug) |
| `player` | PlayerCommand | false | (debug) |
| `loadgame`, `load` | LoadGameCommand | true | "Loads a saved world file. This can also be used to start a new game via launch command. e.g -load \"my game save\" moon" |
| `loadlatest` | LoadLatestCommand | true | "Loads the latest saved file, including auto saves" |
| `save` | SaveCommand | false | "Saves the current game to specified path". Args: `<filename>` or `delete (d / rm) <filename>` or `list (l)` |
| `test` | BasicCommand (added at static-init time) | false | "Testing all the colours of the rainbow" |

## Lifecycle commands deep-dive
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

### `-load <savename> [worldname]` (LoadGameCommand, line 96508)

```csharp
public override string HelpText =>
    "Loads a saved world file. This can also be used to start a new game " +
    "via launch command. e.g -load \"my game save\" moon";
public override string[] Arguments =>
    new string[3] { "list", "<filename>", "<filename> (optional)<worldname>" };
public override bool IsLaunchCmd => true;
public override bool RequiresGameManagerIsInitialized => true;
```

Behaviour: if `<savename>` resolves to a directory under SavePath/saves containing exactly one `.save` file, load it. Otherwise, if `<worldname>` is provided, treat `<savename>` as the new save's name and start a new world on `<worldname>` with the default difficulty (Normal) and default start condition. Subcommand `list` (or `l`) prints the list of saves.

### `-loadlatest [savename]` (LoadLatestCommand, line 96606)

Without args: scans all subdirectories of `<SavePath>/saves`, picks the file with the most recent LastWriteTime, loads it. With `<savename>`: scans only that named directory for the most recent file. Falls through to LoadGame's logic if "Latest save not found".

### `-new <world> [difficulty] [startcondition]` (NewGameCommand, line 97302)

```csharp
public override string HelpText =>
    "Starts a new game at specific world automatically from launch." +
    "Must provide world name as argument";
public override string[] Arguments =>
    new string[3] { "worldname", "difficulty", "startcondition" };
public override bool IsLaunchCmd => true;
```

Defaults: world = `"Moon"`, difficulty = `"Normal"`, startcondition = `"Default"`. Validates each against `WorldSetting.Find` / `DifficultySetting.Find` / `DataCollection.Get<StartConditionData>`. Calls `World.StartNewWorld(worldId)` then prints `Started new game in world <worldName>`.

### `save <name>` / `save delete <name>` / `save list` (SaveCommand, line 96400)

```csharp
public override string HelpText => "Saves the current game to specified path";
public override string[] Arguments =>
    new string[3] { "<filename>", "delete (d | rm) <filename>", "list (l)" };
public override bool IsLaunchCmd => false;
```

Behaviour: with no args, saves under `XmlSaveLoad.Instance.CurrentStationName`. With one positional arg that does not match `delete`/`d`/`rm`/`list`/`l`, saves under that name. Subcommands `delete`/`rm`/`d <filename>` remove a save directory recursively. `list`/`l` prints existing saves.

Confirmation log line on success (line 96453):

```csharp
ConsoleWindow.Print("Saved " + stationName);
```

This is the line `tools/dedicated-server.ps1 -Save -Name <X>` polls for in `data/server.log` to confirm completion.

Refuses to save when `GameState != Running && != Paused`. Refuses on remote clients via `CommandBase.CannotAsClient("save")`.

### `quit` / `exit` / `leave` (QuitCommand, line 98133)

```csharp
public override string Execute(string[] args)
{
    ConsoleWindow.PrintAction("exiting game");
    Application.Quit();
    return null;
}
```

No autosave. The `Application.quitting` event cancels in-flight autosaves rather than waiting for them. To preserve state, send `save "<name>"` and wait for the `Saved <name>` line before sending `quit`.

### `-settings <Field> <Value>` (SettingsCommand, line 98668)

```csharp
internal class SettingsCommand : ClassManipulator<Settings.SettingData>
{
    public override string HelpText => "Change the settings.xml. e.g settings servermaxplayers 5";
    protected override Settings.SettingData ObjectInstance => Settings.CurrentData;

    protected override void OnValueChanged()
    {
        EnsureExistence();
        Assets.Scripts.Networking.NetworkManager.UpdateSessionData(ObjectInstance);
        Settings.SaveSettings();
    }
}
```

Reflection-driven. Any field on SettingData is settable by name (the ClassManipulator base handles the lookup, set, type coercion). Each set persists `setting.xml` and notifies NetworkManager.

### `-settingspath <file-path>` (SettingsPathCommand, line 98695)

```csharp
public override string HelpText =>
    "Sets the default settings path to a new location. " +
    "Launch command only. If none found default is used.";
public override string[] Arguments => new string[1] { "<full-directory-path>" };
public override bool IsLaunchCmd => true;

public override string Execute(string[] args)
{
    if (args.Length == 1)
    {
        FileInfo fileInfo = new FileInfo(args[0]);
        Settings.SettingData.Path = fileInfo.FullName;
        ConsoleWindow.PrintAction("Set custom settings path: " + fileInfo.FullName);
        return null;
    }
    return "Invalid syntax";
}
```

Despite the help text saying "directory path", the implementation wraps the arg in `new FileInfo(args[0])` and assigns the full file path. So the argument is a file path (the `setting.xml` file itself), not a directory. Help text is misleading.

### `serverrun <commandline>` (ServerRunCommand, line 98588)

```csharp
public override string HelpText => "Sends a message to the server to perform server side commands";
public override string[] Arguments => new string[1] { "Command" };
public override bool IsLaunchCmd => false;

public override string Execute(string[] args)
{
    if (args.Length == 0) return "Invalid syntax";
    if (Assets.Scripts.Networking.NetworkManager.IsClient)
        SendMessageToServer(string.Join(" ", args));
    else
        ConsoleWindow.PrintError("Only clients can use this command");
    return null;
}

private static void SendMessageToServer(string command)
{
    NetworkClient.SendToServer(new ServerRunCommandMessage
    {
        ClientId = Assets.Scripts.Networking.NetworkManager.LocalClientId,
        Secret = Settings.CurrentData.ServerAuthSecret,
        Command = command
    });
}
```

Server-side handler (line 98631):

```csharp
public override void Process(long hostId)
{
    if (string.IsNullOrEmpty(Settings.CurrentData.ServerAuthSecret))
    {
        ConsoleWindow.PrintError("serverrun command can only be used if a ServerAuthSecret is set in setting.xml");
        return;
    }
    Client client = NetworkBase.Clients.Find(x => x.ClientId == ClientId);
    if (client == null) { ... ClientId not found error ... }
    else if (Secret != Settings.CurrentData.ServerAuthSecret)
    { ... mismatch error ... }
    else
    {
        ConsoleWindow.PrintAction("client '<name>' ran command '<cmd>'");
        CommandLine.Process(Command);
    }
}
```

So `serverrun` is the closest in-game equivalent to RCON. Both client and server need `ServerAuthSecret` set to the same string. The server then runs the wrapped command through `CommandLine.Process`, giving the client access to the entire command surface (save, kick, ban, status, etc.).

### `kick <clientId>` and `ban <clientId>` / `ban refresh`

`BanCommand` (line 94389) is server-only and refuses in single-player. Args: `<clientId>` (numeric ulong) or `refresh` to reload the blacklist file. KickCommand is similar but disconnects without persisting to the blacklist (not read this pass; mentioned for completeness).

## Defaults summary
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

If you launch `rocketstation_DedicatedServer.exe -batchmode -nographics -new Moon` with no other flags and no pre-existing `setting.xml`, you get:

- ServerName: `Stationeers`
- Open server (no `ServerPassword`)
- 10 max players
- Ports `27016` (Game) / `27015` (Update)
- UPNPEnabled: true
- UseSteamP2P: true
- AutoSave every 300s, retain 5 autosaves
- ServerVisible: false (not in public browser)
- ServerAuthSecret unset, so `serverrun` from clients is rejected
- AutoPauseServer: true (world pauses with no clients connected)
- SavePath: empty, falling back to `StationSaveUtils.DefaultPath`. In batch mode that resolves to the directory containing `rocketstation_DedicatedServer.exe`. Worlds, scripts, and mods all live under that root.
- `setting.xml` written to `<SavePath>/setting.xml` on first save, which (with empty SavePath) is the exe directory.

## Notes for tools/dedicated-server.ps1
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

The launcher's current `-Start` flag set:

```
-batchmode -nographics
-settingspath  <DedicatedServer>/data/setting.xml
-logFile       <DedicatedServer>/data/server.log
-settings SavePath           <DedicatedServer>/data
-settings StartLocalHost     true
-settings GamePort           27016
-settings UpdatePort         27015
-settings AutoSave           true
-settings UPNPEnabled        false
-settings ServerName         "Local Test"
-settings ServerMaxPlayers   4
-settings ServerPassword     x
-load <SaveName> <Map>   OR   -new <Map>
```

Verified against the source:

- `UPNPEnabled false` is correct for a local-only test rig; the default `true` would advertise via UPnP.
- `ServerPassword x` is correct; clients enter `x` in Direct Connect.
- `StartLocalHost true` is harmless but not required: `-load` and `-new` both run `IsLaunchCmd = true` paths that hand off to `World.StartNewWorld` / `LoadHelper.LoadGame`, which in turn start the network listener. This setting governs the client's main-menu "host on launch" flow, not the dedicated build's load path. Could be removed without behaviour change.
- `ServerMaxPlayers 4` is below the default of 10. No clamping is visible at the SettingData level; the source-of-truth for upper bound (1-30 in public docs) was not located this pass.
- `AutoSave true` matches the default; passing it explicitly is documentation, not a state change.
- `GamePort 27016` / `UpdatePort 27015` match the defaults; explicit for the same reason.

Recommendations the launcher could adopt without breaking the current model:

- Set `-settings AutoPauseServer false` for tests that need world-time progression even with no client connected (e.g. atmospheric simulation over time without a human in-world). The default is true.
- Set `-settings ServerAuthSecret <token>` (and document the same on the client) to enable richer agent control via `serverrun`. The agent could then run admin commands on the running server (kick, ban, settings, save, status) from a client without typing on the server's stdin.
- Consider removing `-settings StartLocalHost true` since it has no observed effect on the dedicated-server load path.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

- 2026-04-28: page created from a fresh decompile of `Assembly-CSharp.dll` at game version `0.2.6228.27061` (ilspycmd output at `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs`). All Settings field defaults are verbatim from `Settings.SettingData` (decompile lines 248236-248577). Command dispatch dictionary verbatim from `CommandLine` static constructor (lines 94942-95038). HelpText / Arguments / IsLaunchCmd values for the lifecycle commands (Save, Quit, LoadGame, LoadLatest, NewGame, Settings, SettingsPath, ServerRun, Ban) are verbatim from each command's class declaration.

## Open questions
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

- `ServerMaxPlayers` upper bound. The SettingData declares `int ServerMaxPlayers = 10` without obvious clamping. Public docs claim 1-30. Enforcement may live in UI input validation, the network join handler, or a server-side reject path; not verified this pass.
- The "(mixed)" `IsLaunchCmd` entries in the command table reflect commands whose class declarations were not read in detail this pass. The dispatcher does not visibly gate runtime stdin on `IsLaunchCmd`, so the flag may be advisory only; whether the launch path enforces it is unverified.
- `StartLocalHost` consumer path. Hypothesised to be the client's main-menu "host on launch" path, not consumed by the dedicated build's `-load` / `-new` flow. Verifying would require following `Settings.CurrentData.StartLocalHost` references.
- The full HelpText / Arguments for the bulk of the (non-lifecycle) commands listed in the command table were not transcribed. Each marked `(not read this pass)` or `(debug)` is a candidate for follow-up if the launcher needs to invoke any of them.
