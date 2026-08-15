---
title: Dedicated Server Settings
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6428.27798
verified_at: 2026-08-15
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Settings.SettingData (decompile lines 248232-248613)
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: CommandLine (decompile lines 94926-95177)
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: SaveCommand / QuitCommand / LoadGameCommand / LoadLatestCommand / NewGameCommand / SettingsCommand / SettingsPathCommand / ServerRunCommand / BanCommand
  - rocketstation_DedicatedServer_Data/Managed/Assembly-CSharp.dll :: CommandLine.Process / ConsoleWindow.Submit / ServerRunCommandMessage.Process / RichPresenceJoinRequested (decompile lines 97056, 97087, 97100, 101957, 215637, 273780)
  - rocketstation_DedicatedServer_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.ConsoleWindow._Init / CustomLogFile / Initialize / WaitForGameToBeReadyThenOverrideConsoleInput (decompile lines 215144, 215190, 215203-215231, 215503-215511)
  - rocketstation_DedicatedServer_Data/Managed/Assembly-CSharp.dll :: UI.ImGuiUi.RocketSystemConsole (decompile lines 107246-107460, ConsoleInputThread at 107330)
  - rocketstation_DedicatedServer_Data/Managed/Assembly-CSharp-firstpass.dll :: MoonSharp PlatformAccessor.IO_GetStandardStream
  - rocketstation_DedicatedServer_Data/StreamingAssets/Worlds/*/*.xml :: GameData/WorldSettings/World[@Id]
related:
  - GameSystems/NetworkRoles.md
  - Patterns/ServerAuthoritativeSimulation.md
  - Patterns/SinglePlayerNetworkRole.md
  - Patterns/GameLoggingSinks.md
tags: [network, save-load, chat]
---

# Dedicated Server Settings

The Stationeers Dedicated Server (Steam app `600760`) shares its configuration surface with the regular client build. There is no server-only Settings class and no server-only command dispatcher. The dedicated build is the same Unity assembly with `Application.platform == WindowsServer` (or `-batchmode`) flipping a handful of conditional paths.

Three orthogonal configuration layers determine how the server runs:

1. **Unity built-in launch flags** (`-batchmode`, `-nographics`, `-logFile <path>`, `-screen-*`, etc.) are consumed by the Unity engine before any Stationeers code runs. They are not in this page; see Unity's command-line reference.
2. **Stationeers settings** (`Settings.SettingData`, persisted to `setting.xml`). 80+ XML-serialized fields covering both client UI/performance and server behaviour. Set via `-settings <Field> <Value>` on the launch line, written into `setting.xml` by the runtime, or hand-edited in the file.
3. **Stationeers commands** (`CommandLine` dispatcher, 70+ entries). Same dictionary serves both launch flags (with `-` prefix, multiple per launch) and runtime console input (no prefix, one per line). Each command exposes `HelpText`, `Arguments`, and `IsLaunchCmd`.

## Architecture
<!-- verified: 0.2.6428.27798 @ 2026-08-15 -->

The single dispatcher is `CommandLine.Process(string[], bool onLaunch = false)` (decompile line 95111). When `onLaunch` is true (called from `[RuntimeInitializeOnLoadMethod] CommandLine.ProcessOnLaunch`), every `-name` token starts a new command and subsequent non-dash tokens accumulate as that command's arguments. When `onLaunch` is false (the runtime path), only the first token may be `-name`-prefixed; later tokens are arguments to the first command. Same dispatch table either way.

The runtime path is **not** the process's standard input, and calling it "the stdin path" (as this section did until 2026-08-15) predicts the wrong thing: see "The console channel" below for what actually feeds it and for the two independent reasons a write to a headless server's redirected stdin changes nothing.

An unknown command name is silently dropped on the launch path and reported on the runtime path, which is why an unrecognised Unity or third-party flag on the launch line produces no error:

```csharp
if (!_commandsMap.TryGetValue(text2, out var value))
{
    if (!_isLaunchCommands)
    {
        ConsoleWindow.PrintError(ConsoleStrings.Error.CommandUnknown.AsString(text), suppressStacktrace: true);
    }
}
```

A command whose `RequiresGameManagerIsInitialized` is true and that arrives before `GameManager` is up is queued in `_postLaunchCommands` rather than run or dropped. The queue is drained by `CommandLine.ExecutePostLaunchCommands()` (line 97167), called once from `GameManager`'s startup chain at line 198538, immediately after `DifficultySetting.SetCurrent()` and a few statements before `IsInitialized = true`. That is how `-load` and `-new` (both `RequiresGameManagerIsInitialized => true`) run at all.

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

## The console channel: what actually feeds the runtime dispatcher
<!-- verified: 0.2.6428.27798 @ 2026-08-15 -->

The dedicated server **does** have a console reader, and it **does** feed `CommandLine.Process`. What it does not do is read the process's standard input **stream**. It reads the Win32 **console input buffer**, one keystroke at a time, through `System.Console.ReadKey()`. A pipe attached to the child's stdin is a different object and never reaches that buffer, so a wrapper that writes a line into a headless server's redirected stdin gets a successful write and no effect. On top of that, the reader is not even constructed when `-logFile` is on the launch line, which is the usual way a headless server is run.

Two independent reasons, either one sufficient. Both are below, with the evidence.

### The four routes into the dispatcher

`CommandLine.Process(string)` (decompile line 97087) is the single-string entry point that prefixes a dash and forwards to `Process(string[], onLaunch: false)`:

```csharp
public static void Process(string input)
{
    if (!string.IsNullOrEmpty(input))
    {
        string[] array = CmdLineParser.SplitCommandLine(input).ToArray();
        if (!array[0].StartsWith('-'))
        {
            array[0] = "-" + array[0];
        }
        Process(array);
    }
}
```

The token `CommandLine.Process` occurs exactly four times in the assembly. **Three are calls and the fourth is a method-group subscription**, which is why a grep shaped like `CommandLine.Process(` finds only three and misses the one that matters here:

| Line | Reference | What sends it |
|---|---|---|
| 101957 | `ServerRunCommandMessage.Process` calls it | A connected client's `serverrun`, gated on `Secret == Settings.CurrentData.ServerAuthSecret` and on the sender being in `NetworkBase.Clients` |
| 215509 | `_systemConsoleInput.OnInputReceived += CommandLine.Process;` | **The dedicated server's own system console.** A line assembled from `Console.ReadKey()` keystrokes on a background thread |
| 215637 | `ConsoleWindow.Submit()` calls it | The in-game console, in-process |
| 273780 | `RichPresenceJoinRequested(Friend, string args)` calls it | Steam rich-presence join arguments |

Plus `CommandLine.ProcessOnLaunch()` (line 97056), a `[RuntimeInitializeOnLoadMethod]` that calls `Process(CommandLineArgs, onLaunch: true)` on the launch line, with deferred entries drained later by `ExecutePostLaunchCommands()`.

### Reason 1: the reader is keystrokes, not a stream

The console lives in `UI.ImGuiUi.RocketSystemConsole` (line 107246), held by `Assets.Scripts.ConsoleWindow._systemConsoleInput` (line 215144). Its input thread, verbatim (line 107330):

```csharp
private void ConsoleInputThread()
{
    _inputString.Append(_inputPrefix);
    while (_keepAlive)
    {
        if (!System.Console.CursorVisible || !System.Console.KeyAvailable)
        {
            continue;
        }
        ConsoleKeyInfo consoleKeyInfo = System.Console.ReadKey();
        switch (consoleKeyInfo.Key)
        {
        case ConsoleKey.Enter:
            OnEnter();
            continue;
        case ConsoleKey.Backspace:
            OnBackspace();
            continue;
        case ConsoleKey.Escape:
            OnEscape();
            continue;
        }
        if (consoleKeyInfo.KeyChar != 0)
        {
            _inputString.Append(consoleKeyInfo.KeyChar);
        }
        RedrawInputLine();
    }
}
```

`Console.KeyAvailable` and `Console.ReadKey()` go to `PeekConsoleInput` / `ReadConsoleInput` on the standard **input handle**. When that handle is an anonymous pipe (`ProcessStartInfo.RedirectStandardInput = true`), those console APIs do not read the pipe: they fail or report nothing available, and the loop spins without ever consuming a byte. Nothing else in the assembly touches stdin. Across all 442,274 decompiled lines of `Assembly-CSharp.dll` there are zero occurrences of `Console.ReadLine`, `Console.In`, `Console.OpenStandardInput`, `StandardInput` or `ReadLineAsync`; the single `.ReadLine(` is `stringReader.ReadLine()` at line 117950, reading an in-memory string. `Assembly-CSharp-firstpass.dll` carries exactly one stdin reference, MoonSharp's Lua platform accessor, which is not a console:

```csharp
public override Stream IO_GetStandardStream(StandardFileType type)
{
    return type switch
    {
        StandardFileType.StdIn => Console.OpenStandardInput(),
        StandardFileType.StdOut => Console.OpenStandardOutput(),
        StandardFileType.StdErr => Console.OpenStandardError(),
        _ => throw new ArgumentException("type"),
    };
}
```

`Enter` is what turns the accumulated keystrokes into a command: `OnEnter()` strips the `"> "` prefix, trims, and enqueues the line under `lock (_inputQueue)`. A `Tick()` loop drains that queue **on the Unity thread** and raises `OnInputReceived`, which is what makes console commands safe to run against game state:

```csharp
private async Task Tick()
{
    while (_keepAlive)
    {
        if (Thread.CurrentThread == _unityThread)
        {
            lock (_inputQueue)
            {
                while (_inputQueue.Count > 0)
                {
                    OnInputReceived?.Invoke(_inputQueue.Dequeue());
                }
            }
        }
        await Task.Delay(100);
    }
}
```

### Reason 2: `-logFile` removes the console entirely

`ConsoleWindow` gates both the construction and the wiring on the absence of `-logFile` (line 215190):

```csharp
private static bool CustomLogFile => CommandLineArgs?.Contains("-logFile") ?? false;
```

Construction, in `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)] ConsoleWindow._Init` (line 215203):

```csharp
if (GameManager.IsBatchMode)
{
    CommandLineArgs = Environment.GetCommandLineArgs().Skip(1).ToList();
    string text = string.Join(" ", CommandLineArgs);
    string gameVersion = GameManager.GetGameVersion();
    if (!CustomLogFile)
    {
        _systemConsoleInput = new RocketSystemConsole("Stationeers - " + gameVersion + " " + text);
    }
}
```

Wiring, from `ConsoleWindow.Initialize()` (called at line 198456 in `GameManager`'s startup chain) via `WaitForGameToBeReadyThenOverrideConsoleInput()` (line 215503):

```csharp
private static async UniTaskVoid WaitForGameToBeReadyThenOverrideConsoleInput()
{
    await UniTask.WaitUntil(() => GameManager.IsInitialized);
    await UniTask.Delay(1000);
    if (!CustomLogFile && _systemConsoleInput != null)
    {
        _systemConsoleInput.OnInputReceived += CommandLine.Process;
        _systemConsoleInput.Ready("Stationeers - " + GameManager.GetGameVersion());
    }
}
```

`Ready()` is also the only place `_inputThread.Start()` is called (line 107307), so without it the input thread never runs at all. With `-logFile` present there is no `RocketSystemConsole`, no input thread, no handler, and `ConsoleWindow.Print` routes through `UnityEngine.Debug.Log` into the file instead. `GameManager.IsBatchMode` is true for the dedicated build regardless of `-batchmode`, because `SetMatchMode` (line 197713) ORs `Application.isBatchMode` with `RuntimePlatform.LinuxServer` / `WindowsServer`. `RocketSystemConsole`'s constructor throws `"Don't use this outside of dedicated server builds"` on any other platform, so this console exists only on the two server builds.

### Measured live

Game 0.2.6428.27798, 2026-08-15, dedicated build, two arms.

**Arm 1, the normal rig configuration** (`-batchmode -nographics -logFile <path>`, in world, stdin redirected to a pipe owned by a wrapper, in-process control-plane plugin loaded). Two commands written to stdin and flushed, then the identical two run in-process:

| Channel | Command | Result |
|---|---|---|
| stdin pipe | `version` | **zero** bytes appended to the log, no output line |
| stdin pipe | `settings ServerMaxPlayers 7` | `setting.xml` unchanged at `4` |
| in-process `ConsoleWindow.Submit` | `version` | printed and logged `version: 0.2.6428.27798` |
| in-process `ConsoleWindow.Submit` | `settings ServerMaxPlayers 7` | logged `Changed setting 'ServerMaxPlayers' from '4' to '7'`; `setting.xml` now `7` |
| launch line | `-settings ServerMaxPlayers 4` | logged `Changed setting 'ServerMaxPlayers' from '10' to '4'` at boot |

An earlier run of the same shape, recorded 2026-08-15: a stdin `quit` left the server running 90 s later with zero log bytes appended, while an in-process `ConsoleWindow.Submit("quit")` exited it in 2.55 s, and a `Submit("help")` between the two returned 452 console lines, so the server was healthy throughout and the stdin line was ignored rather than lost to a wedged process.

**Arm 2, the isolating arm: same build, stdin still a redirected pipe, but `-logFile` REMOVED** (`-batchmode -nographics -noclear`, no world, stdout captured). This is the configuration in which the console genuinely exists, and it is the arm that separates the two reasons. The captured stdout proves the console came all the way up:

```
08:47:45: Changed setting 'ServerMaxPlayers' from '10' to '4'
08:48:22: game manager initialized
***Stationeers - 0.2.6428.27798***
Ready
```

`***Stationeers - <version>***` and `Ready` are printed only by `RocketSystemConsole.Ready()`, which runs only after `OnInputReceived += CommandLine.Process`, and whose next statement after printing `Ready` is `_inputThread.Start()`. So the reader was constructed, the handler was attached and the input thread was started. Then, at t+180 s and t+200 s, `settings ServerMaxPlayers 9` and `version` were written to the process's stdin and flushed:

| Witness | Before | After |
|---|---|---|
| `setting.xml` `ServerMaxPlayers` | `4` | `4` (unchanged, checked at t+240 s and again at kill) |
| stdout | no `version:` line | no `version:` line, no `Changed setting` line |

**Removing `-logFile` does not make stdin work.** Reason 1 stands on its own; reason 2 is an additional, earlier cut-off.

**Consequence for tooling.** A wrapper that owns a headless server's stdin can write to it successfully and change nothing: the write succeeds because the pipe is real, and the console API on the other end is not reading pipes. The channels that do reach a running headless server's command dispatcher are:

1. **An in-process caller of `ConsoleWindow.Submit`** (a BepInEx plugin). Direct, synchronous, and returns the console output. This is what `TestRig`'s `POST /console/exec` uses.
2. **`serverrun <command>` from a connected client** whose `ServerAuthSecret` matches the server's. The closest thing to RCON the game ships. Established from code (`ServerRunCommandMessage.Process` at :101957 ends in `CommandLine.Process(Command)`), not measured in the 2026-08-15 runs, which used no game client.
3. **Real keystrokes typed at an attached console**, when the server is started without `-logFile` in a terminal a human is sitting at. This is the vanilla operator experience and the reason the console exists; it is not drivable by writing to a pipe, and reaching it programmatically would mean synthesising key events into the console input buffer (`WriteConsoleInput`), which nothing in this repository does.

And, before the server is running, the launch line itself (`ProcessOnLaunch`, plus `ExecutePostLaunchCommands` for anything needing `GameManager`). That is the channel `-load`, `-new`, `-settings` and `-settingspath` travel on, and it is fully reliable; the arm-1 table above shows it working in the same run in which stdin did nothing.

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

The dispatch dictionary as registered in `CommandLine`'s static constructor (decompile lines 94944-95038). Names with multiple keys (aliases) are grouped. Where a command exposes `IsLaunchCmd = true`, it can be used as a `-name` flag at launch; otherwise it is runtime-only, meaning the system console, `serverrun`, or an in-process `ConsoleWindow.Submit` (see "The console channel" above).

**The table below was read at 0.2.6228.27061 and the dictionary has moved since.** Re-read verbatim at 0.2.6428.27798 on 2026-08-15, the delta is ten keys; everything else in the dictionary is unchanged. Only the keys and classes were compared, so the `IsLaunchCmd` and HelpText columns still carry their 2026-04-28 reading and the section stamp reflects that.

| Change | Key | Class |
|---|---|---|
| added | `announce` | `AnnounceCommand` |
| added | `roomevaluator` | `RoomEvaluatorCommand` |
| added | `pylonlog` | `PylonLogCommand` |
| added | `deletenear` | `DeleteNearCommand` |
| added | `voxelfillnear` | `VoxelFillNearCommand` |
| added | `proxy` | `ProxyCommand` |
| added | `organs` | `OrgansCommand` |
| added | `thumbnail` | `ThumbnailStudioCommand` |
| removed | `clients` | `ClientsCommand` (the class is gone from the assembly entirely) |
| removed | `networkdebug` | `NetworkDebugWindowCommand` (the class is gone from the assembly entirely) |

`clients` is the one with operational consequences: tooling that reads a connected-player count by issuing a `clients` console command has nothing to issue as of 0.2.6428.27798. `status` (`StatusCommand`) is still registered.

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

Source-side defaults: world = `"Moon"`, difficulty = `"Normal"`, startcondition = `"Default"`. Validates each against `WorldSetting.Find` / `DifficultySetting.Find` / `DataCollection.Get<StartConditionData>`. Calls `World.StartNewWorld(worldId)` then prints `Started new game in world <worldName>`.

Critical caveat: the source's `"Moon"` default is stale. The runtime's actual valid world ids (verified 2026-04-28 by passing `-new Moon` and reading the error message) are:

- `Lunar` (this is the moon)
- `Mars2`
- `Europa3`
- `MimasHerschel`
- `Venus`
- `Vulcan2`
- `Vulcan` (marked Deprecated)

`WorldSetting.Find("Moon")` returns null because the world ids live in JSON / data files that were updated past the source's hardcoded default; `WorldSetting.Find` looks them up by id from the data store. So `-new Moon` (or no world arg, which falls back to "Moon") fails with `No such world name: Moon. Valid worlds: Europa3, Lunar, Mars2, MimasHerschel, Venus, Vulcan (Deprecated), Vulcan2`. Always pass an explicit valid id.

#### Where the valid ids live on disk, and how to read them without launching
<!-- verified: 0.2.6428.27798 @ 2026-08-15 -->

The data store behind `WorldSetting.Find` is loaded from `StreamingAssets/Worlds/`: one folder per world, holding one or more `<Name>.xml`, each a `GameData` document whose `WorldSettings` element contains one or more `<World Id="...">`. **The accepted id is that `Id` attribute, and it is not the folder name in four of the nine folders** on 0.2.6428.27798:

| Folder | File | `World Id` |
|---|---|---|
| `Europa` | `Europa.xml` | `Europa3` |
| `Lunar` | `Lunar.xml` | `Lunar` |
| `Mars2` | `Mars2.xml` | `Mars2` |
| `Mimas` | `MimasHerschel.xml` | `MimasHerschel` |
| `Venus` | `Venus.xml` | `Venus` |
| `Vulcan` | `Vulcan.xml` | `Vulcan` (`Hidden="true" Deprecated="true"`) |
| `Vulcan` | `VulcanV2.xml` | `Vulcan2` |
| `Tutorial1` ... `Tutorial6` | various | `Tutorial1`, `Airlock`, `FurnaceBasics`, `Manufacturing` |

Scanning folder names would therefore refuse `Europa3` and `MimasHerschel` and accept `Europa` and `Mimas`, all four wrongly, and would miss `Vulcan2` entirely because two worlds share the `Vulcan` folder in two files.

**Tutorial worlds are excluded from the `-new` set, and the discriminator is `<IsTutorial Value="true" />` inside the `World` element**, not the folder name and not the id. Parsing every `World Id` and dropping the ones carrying that marker reproduces exactly the seven the server prints, which is the check that the rule is the right one.

The strings themselves live in `Assembly-CSharp`: `No such world name: ` and `. Valid worlds: ` are two halves of one composite format, alongside `Tried to start a new game but no world id was provided.`. The three lines a successful `-new` prints around them (`Creating new world '<X>' with difficulty ...`, `WorldSetting: <X> StartCondition: ...`, `World <X> created`, `Started new game in world <X>.`) are NOT in any assembly under `Managed/`, so they come from a mod or from composed output; do not key a detector on them.

**Operational consequence.** The set is on disk before launch, so an invalid `-new` is answerable without starting anything, and it costs a ninety-second boot to discover otherwise. Worse, the server does not exit on a rejected world name: it logs the line once and then runs indefinitely with no world at all, `GameState` `None` and `phase` `menu`, answering its control plane normally throughout. Nothing else about the process distinguishes that state from a world that is still loading. `TestRig/testrig.exe` validates `--new` against this catalogue before launching and scans for the rejection line while waiting for readiness.

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

This is the line `TestRig/testrig.exe save --target server --save-name <X>` polls for in `data/server.log` to confirm completion.

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

If you launch `rocketstation_DedicatedServer.exe -batchmode -nographics -new Lunar` with no other flags and no pre-existing `setting.xml`, you get:

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

## Notes for the TestRig launcher (dedicated-server half)
<!-- verified: 0.2.6428.27798 @ 2026-08-15 -->

The flag set `TestRig/testrig.exe start --target server` applies:

```
-batchmode -nographics
-settingspath  <DedicatedServer>/data/setting.xml
-logFile       <DedicatedServer>/data/server.log
-settings SavePath           <DedicatedServer>/data
-settings GamePort           28016
-settings UpdatePort         28015
-settings LocalIpAddress     127.0.0.1
-settings AutoSave           true
-settings AutoPauseServer    false
-settings UPNPEnabled        false
-settings ServerName         "Local Test"
-settings ServerMaxPlayers   4
-settings ServerAuthSecret   x
-load <SaveName> <Map>   OR   -new <Map>
```

Verified against the source:

- `LocalIpAddress 127.0.0.1` pins RakNet to the loopback interface. Without it RakNet binds whichever interface comes up first, which on a developer machine with an active LAN is the LAN IP (`10.20.30.200` or similar) -- direct connections to `127.0.0.1:28016` then fail because no listener is bound there. Loopback binding makes the agent-driven test loop deterministic. To intentionally expose the server to the LAN, override or remove this line.
- `UPNPEnabled false` is correct for a loopback-only test rig; the default `true` would advertise via UPnP. Redundant with the loopback bind but kept for documentation.
- No `ServerPassword`. The server is loopback-only, so unauthenticated connections from outside the machine are impossible at the network layer; a connection password adds no protection in that topology and just gets in the way of agent-driven test loops. Earlier launcher revisions hardcoded `ServerPassword x`; that was removed once the LocalIpAddress pin landed.
- `ServerAuthSecret x` is kept; matching value on the client unlocks `serverrun` for in-game admin commands without writing to the server's stdin (see ServerRunCommand at decompile line 98588).
- `-logFile <path>` is load-bearing in two directions and worth stating explicitly. It gives the run a scrapable log, because `ConsoleWindow.Print` then routes through `UnityEngine.Debug.Log` into that file. It also **removes the system console outright**, since `ConsoleWindow.CustomLogFile` gates the `RocketSystemConsole` construction and its handler wiring. Dropping the flag would not buy a usable stdin channel (measured: see "The console channel", arm 2), it would only cost the log. Commands go through the in-process control plane; the flag stays.
- `ServerMaxPlayers 4` is below the default of 10. No clamping is visible at the SettingData level; the source-of-truth for upper bound (1-30 in public docs) was not located this pass.
- `AutoPauseServer false` keeps world simulation running with no clients connected (atmospheric simulation, growth, decay, autosave timer). Default is `true`. Required for tests that depend on game-time progression between client sessions.
- `AutoSave true` matches the default; passing it explicitly is documentation, not a state change.
- `GamePort 28016` / `UpdatePort 28015` are offset by +1000 from the Stationeers client defaults (`27016` / `27015`) so the dedicated server runs alongside a hosting client on the same machine without RakNet's port-binding fallback.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

- 2026-04-28: page created from a fresh decompile of `Assembly-CSharp.dll` at game version `0.2.6228.27061` (ilspycmd output at `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs`). All Settings field defaults are verbatim from `Settings.SettingData` (decompile lines 248236-248577). Command dispatch dictionary verbatim from `CommandLine` static constructor (lines 94942-95038). HelpText / Arguments / IsLaunchCmd values for the lifecycle commands (Save, Quit, LoadGame, LoadLatest, NewGame, Settings, SettingsPath, ServerRun, Ban) are verbatim from each command's class declaration.
- 2026-04-28: launcher relocated from `tools/dedicated-server.ps1` to `TestRig/DedicatedServer/dedicated-server.ps1`. Flag set updated: `StartLocalHost true` removed (confirmed unnecessary on the dedicated build's load path), `ServerAuthSecret x` added (enables `serverrun` from a connected client). Path references and the launcher-notes section updated to match. Game-internals claims unchanged.
- 2026-05-18: launcher flag set updated to pin `LocalIpAddress 127.0.0.1` and drop `ServerPassword x`. Confirmed via runtime test (host wrapper PID 25800, server UDP bind read via `Get-NetUDPEndpoint`): without LocalIpAddress, RakNet bound UDP 28016 to the LAN IP `10.20.30.200` and refused loopback connections; with LocalIpAddress = 127.0.0.1, the bind moves to loopback and Direct Connect from a same-machine client succeeds. Removed ServerPassword because the loopback bind makes external auth at the network layer impossible to bypass, so a password adds friction without security. Kept `ServerAuthSecret x` (it gates `serverrun`, not connection). Also flipped `AutoPauseServer` to `false` in the documented flag block to match the launcher, which has been carrying that flag for tests that need simulation between client sessions.
- 2026-08-13: the two rig launchers were replaced by one, `TestRig/testrig.ps1`, with positional verbs and `-Target all|server|clients|<instance>`. Command references on this page updated (`-Save -Name <X>` is now `save -Target server -SaveName <X>`; the flag-set section is now "Notes for the TestRig launcher (dedicated-server half)"). The flag set itself was re-read against the launcher's server library and is unchanged, verbatim, including `LocalIpAddress 127.0.0.1`, `AutoPauseServer false` and `ServerAuthSecret x`. No game-internals claim was changed and none was re-verified against the game, so no section stamp moved.
- 2026-04-28: corrected world-id list in the `-new` deep-dive. NewGameCommand source declares `"Moon"` as the default but `WorldSetting.Find("Moon")` returns null at runtime; valid ids verified by runtime probe are `Lunar, Mars2, Europa3, MimasHerschel, Venus, Vulcan2, Vulcan (Deprecated)`. Defaults summary updated to use `-new Lunar` as the example.
- 2026-08-14: the rig's PowerShell launcher was replaced by one AOT-compiled binary, `TestRig/testrig.exe`, whose options are double-dash. The two command references on this page follow (`testrig.ps1 save -Target server -SaveName <X>` is now `testrig.exe save --target server --save-name <X>`; `testrig.ps1 start -Target server` is now `testrig.exe start --target server`). The flag set itself was re-read against the binary's server half and is unchanged, verbatim, including `LocalIpAddress 127.0.0.1`, `AutoPauseServer false` and `ServerAuthSecret x`. No game-internals claim was changed and none was re-verified against the game, so no section stamp moved.

- 2026-08-15: added "Nothing reads standard input: what actually feeds the runtime dispatcher" from a fresh decompile of the DEDICATED SERVER build at `0.2.6428.27798` (`.work/decomp/0.2.6428.27798/Assembly-CSharp.DedicatedServer.decompiled.cs`, 442,274 lines, plus an `Assembly-CSharp-firstpass.dll` pass). Findings: zero occurrences of `Console.ReadLine` / `Console.In` / `Console.OpenStandardInput` / `StandardInput` / `ReadLineAsync` in `Assembly-CSharp`; the only stdin reference in `firstpass` is MoonSharp's `PlatformAccessor.IO_GetStandardStream`; `CommandLine.Process(string)` (97087) has exactly three callers, `ServerRunMessage.Process` (101957), `ConsoleWindow.Submit` (215637) and `RichPresenceJoinRequested` (273780), with `ProcessOnLaunch` (97056) the fourth route into the dispatcher. Corroborated by a live run on a headless server in world: a stdin `quit` left it running 90 s later with zero log bytes appended, an in-process `ConsoleWindow.Submit("quit")` exited it in 2.55 s, and a `Submit("help")` between them returned 452 lines. Additive; the Architecture section's older "(stdin path)" label is contradicted, is flagged inline and in Open Questions, and is left in place because changing stamped content requires the `Research/WORKFLOW.md` Rule 3 fresh validator, which was not run.

- 2026-08-15: conflict on "does the dedicated server read stdin, and what feeds the runtime dispatcher", resolved by a fresh validator (`Research/WORKFLOW.md` Rule 3) working from its own decompile of `TestRig/DedicatedServer/install/rocketstation_DedicatedServer_Data/Managed/Assembly-CSharp.dll` (0.2.6428.27798, SHA-256 `4A925BE2...B66A4E39`) and its own live runs. **Previous claim** (Architecture, stamped 0.2.6228.27061): the `onLaunch: false` branch is "the stdin path". **Competing claim** (the additive 2026-08-15 section): nothing reads standard input, `CommandLine.Process(string)` has exactly three callers, none a console reader. **Verdict: neither is right, and the observable outcome the additive section reported is correct while its mechanism is not.** Three corrections. (1) The census missed a fourth reference: `_systemConsoleInput.OnInputReceived += CommandLine.Process;` at line 215509 is a method-group subscription, not a call, so a grep shaped `CommandLine.Process(` cannot see it. The dedicated server therefore DOES have a console reader wired to the dispatcher. (2) That reader is `UI.ImGuiUi.RocketSystemConsole.ConsoleInputThread` (107330), which uses `System.Console.KeyAvailable` and `System.Console.ReadKey()` against the Win32 console input buffer. It is not a stdin stream read, which is why the "zero occurrences of `Console.ReadLine` / `Console.In` / `Console.OpenStandardInput` / `StandardInput` / `ReadLineAsync`" grep is true and yet incomplete: the grep list omitted `ReadKey` / `KeyAvailable`, both of which the assembly does contain, exactly once each, in that thread. (3) A second, earlier cut-off was missing entirely: `ConsoleWindow.CustomLogFile => CommandLineArgs?.Contains("-logFile")` (215190) gates both the `new RocketSystemConsole(...)` at 215211 and the handler wiring at 215507, so under the rig's own launch line (which carries `-logFile`) the reader is never constructed and the input thread never starts. **Result:** the Architecture section's "(stdin path)" label is corrected to "the runtime path" and restamped; the additive section is renamed "The console channel: what actually feeds the runtime dispatcher", its census table corrected to four references with 215509 named, and `ServerRunMessage` corrected to `ServerRunCommandMessage`; the two mechanisms are documented with verbatim code; the "there is no third" channel claim is corrected to three runtime channels (in-process `ConsoleWindow.Submit`, `serverrun`, and real keystrokes at an attached console) plus the launch line. **New live evidence**, both arms on 0.2.6428.27798 this date: with `-logFile` and in world, `version` and `settings ServerMaxPlayers 7` written to the wrapper-owned stdin pipe produced a zero-byte log delta and left `setting.xml` at `4`, while the identical two commands through an in-process `ConsoleWindow.Submit` printed `version: 0.2.6428.27798` and moved `setting.xml` to `7`; and in the isolating arm, `-logFile` REMOVED so the console genuinely exists (stdout carries `***Stationeers - 0.2.6428.27798***` then `Ready`, printed only by `RocketSystemConsole.Ready()`, whose next statement is `_inputThread.Start()`), `settings ServerMaxPlayers 9` written to the same kind of stdin pipe still left `ServerMaxPlayers` at `4` and printed nothing. Removing `-logFile` does not make stdin work. The Open Questions entry that held this conflict is removed.
- 2026-08-15: `CommandLine`'s dispatch dictionary re-read verbatim at 0.2.6428.27798 while resolving the above. Ten keys differ from the 0.2.6228.27061 table on this page: `announce`, `roomevaluator`, `pylonlog`, `deletenear`, `voxelfillnear`, `proxy`, `organs` and `thumbnail` added; `clients` (`ClientsCommand`) and `networkdebug` (`NetworkDebugWindowCommand`) removed, both classes gone from the assembly entirely. Recorded as a delta table under the command table rather than by rewriting 90 rows, because only the key and class columns were compared and the `IsLaunchCmd` / HelpText columns were not re-read; that section's stamp stays at 0.2.6228.27061 for the same reason. Also corrected the Open Questions note on `IsLaunchCmd`: the launch path DOES enforce it (`if (_isLaunchCommands && !value.IsLaunchCmd)` prints "Can not use command ... as a launch command"), the runtime path does not.
- 2026-08-15: the ten-key command delta above independently re-read from the DEDICATED SERVER build's own `Assembly-CSharp.dll` (`rocketstation_DedicatedServer_Data/Managed`, 0.2.6428.27798) rather than the client's, while correcting a rig document that still cited `clients`. Same result key for key: `clients` and `networkdebug` absent from the dispatch dictionary, the eight additions present, `status` and `serverrun` still registered. The two builds share one command table, so the delta holds for both.

## Open questions
<!-- verified: 0.2.6428.27798 @ 2026-08-15 -->

- `ServerMaxPlayers` upper bound. The SettingData declares `int ServerMaxPlayers = 10` without obvious clamping. Public docs claim 1-30. Enforcement may live in UI input validation, the network join handler, or a server-side reject path; not verified this pass.
- The "(mixed)" `IsLaunchCmd` entries in the command table reflect commands whose class declarations were not read in detail this pass. The dispatcher gates the LAUNCH path on `IsLaunchCmd` (`if (_isLaunchCommands && !value.IsLaunchCmd)` prints "Can not use command ... as a launch command") but does not gate the runtime path on it at all, so on the runtime path the flag is advisory. Which of the "(mixed)" entries declare it true is still unverified.
- `StartLocalHost` consumer path. Hypothesised to be the client's main-menu "host on launch" path, not consumed by the dedicated build's `-load` / `-new` flow. Verifying would require following `Settings.CurrentData.StartLocalHost` references.
- The full HelpText / Arguments for the bulk of the (non-lifecycle) commands listed in the command table were not transcribed. Each marked `(not read this pass)` or `(debug)` is a candidate for follow-up if the launcher needs to invoke any of them.
