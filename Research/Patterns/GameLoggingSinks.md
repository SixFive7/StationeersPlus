---
title: GameLoggingSinks
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-29
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.ConsoleWindow, GameManager, RocketSystemConsole, LogCommand, Defines.Paths
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs:99289-99340 (LogCommand), :221811-223400 (ConsoleWindow), :109870-110066 (RocketSystemConsole), :228560 (Defines.Paths.LocalData)
  - $(StationeersPath)\rocketstation_Data\Managed\0Harmony.dll :: FileLog, FileWriter
  - .work/decomp/0.2.6403.27689/0Harmony.decompiled.cs:8529-8673 (FileLog), :9332-9355 (FileWriter)
  - $(StationeersPath)\BepInEx\core\BepInEx.dll :: BepInEx.Logging.UnityLogListener, UnityLogSource, DiskLogListener (type and string table inspection only, not decompiled)
  - $(StationeersPath)\rocketstation_Data\Managed\UnityEngine.CoreModule.dll :: UnityEngine.UnityLogWriter.WriteStringToUnityLog
  - $(StationeersPath)\BepInEx\config\BepInEx.cfg :: [Logging] UnityLogListening, LogConsoleToUnityLog; [Logging.Disk] WriteUnityLog; [Logging.Console] Enabled
related:
  - ./InGameConsoleOutput.md
  - ../GameClasses/ConsoleWindow.md
  - ../GameSystems/DedicatedServerSettings.md
tags: [logging, diagnostics, debugging]
---

# Stationeers game logging and diagnostic output sinks

The Stationeers game writes diagnostic and telemetry data to multiple log sinks. This page catalogs every known log file and output mechanism the game itself produces.

For which sink a MOD should write to, and the traps in the in-game console API, see [InGameConsoleOutput](./InGameConsoleOutput.md). For the `ConsoleWindow` class reference, see [ConsoleWindow](../GameClasses/ConsoleWindow.md).

## Summary table
<!-- verified: 0.2.6403.27689 @ 2026-07-29 -->

| Log sink | File path | Condition | Content | Writer |
|---|---|---|---|---|
| **Player.log** | %USERPROFILE%\AppData\LocalLow\Rocketwerkz\rocketstation\Player.log | Always active | UnityEngine.Debug.Log* and BepInEx plugin logs (via BepInEx `UnityLogListener`) | Unity player runtime (built-in) |
| **Player-prev.log** | Same folder | Auto-rotated | Previous session Player.log | Unity built-in rotation |
| **Console export** | %USERPROFILE%\My Games\Stationeers\PlayerLog_*.log | Player-initiated via log command | In-game console buffer dump | LogCommand.LogToFile |
| **Harmony debug log** | Desktop\harmony.log.txt OR HARMONY_LOG_FILE env var | Opt-in (Harmony.DEBUG=true) | Low-level patch tracing | HarmonyLib.FileLog |
| **Harmony FileWriter** | CWD\HarmonyLog.txt OR FileWriter.FileWriterPath | Opt-in (FileWriter.Enabled=true) | Harmony runtime messages | HarmonyLib.FileWriter |
| **Dedicated server console** | Console window (live, not persisted) | Batch mode without -logFile | ConsoleWindow output to system console | RocketSystemConsole |
| **Custom log file** | Specified via -logFile flag | Batch mode with -logFile | UnityEngine.Debug.Log* calls | UnityEngine.Debug |
| **In-game console buffer** | In memory (1024 lines) | Always active, non-batch | ConsoleWindow.Print* calls, command output, AND Debug.LogError / LogException re-printed by the log bridge | ConsoleWindow |

The last row is the one that most often surprises a mod author: the in-game console is a *consumer* of Unity's log stream, not only of explicit `Print` calls. See "The log bridge" below.

## Detailed findings

### 1. Player.log (primary)
<!-- verified: 0.2.6403.27689 @ 2026-07-29 -->

- **Path:** %USERPROFILE%\AppData\LocalLow\Rocketwerkz\rocketstation\Player.log
- **Rotation:** Auto-rotated to Player-prev.log on each launch
- **Content:** UnityEngine.Debug.Log/LogWarning/LogError output
- **Additional:** BepInEx plugin logs, written by BepInEx's own `UnityLogListener` (an `ILogListener` in `BepInEx.dll`, alongside `ConsoleLogListener` and `DiskLogListener`). It formats each event with `[{0,-7}:{1,10}] {2}` (level left-padded to 7, source right-padded to 10) and passes the result to Unity's internal `UnityEngine.UnityLogWriter.WriteStringToUnityLog`, a native binding exported by `UnityEngine.CoreModule.dll` (`Runtime/Export/Logging/UnityLogWriter.bindings.h`). That call writes the string straight into the player log; it does **not** route through `Debug.unityLogger`, so it raises no `logMessageReceivedThreaded` event, captures no stack trace, and is never re-printed into the in-game console at any severity. StationeersLaunchPad plays no part: BepInEx-format lines appear in `Player.log` before StationeersLaunchPad's assembly is loaded, and `StationeersLaunchPad.decompiled.cs` contains no reference to `BepInEx.Logging` at all.
- **Writer:** the Unity player runtime itself, internally. No game code writes this file.

### 2. The log bridge: ConsoleWindow consumes Unity's log stream
<!-- verified: 0.2.6403.27689 @ 2026-07-29 -->

`ConsoleWindow._Init` subscribes to `Application.logMessageReceivedThreaded` (Assembly-CSharp:221927) and its `LogMessage` handler (Assembly-CSharp:222266-222282) re-prints `LogType.Error` and `LogType.Exception` into the in-game console buffer, lowercased, prefixed `[ERROR]` / `[EXCEPTION]`, red, followed by the stack trace in the default colour.

This subscription is a **consumer** of Unity's log notification event. It does not write `Player.log`; Unity does that internally regardless of who subscribes. Getting this backwards is easy because both facts involve the same event name.

- Reaches the in-game console: `LogType.Error`, `LogType.Exception`.
- Does not: `LogType.Log`, `LogType.Warning`, `LogType.Assert`.
- Suppressed entirely when `CustomLogFile` is true (Assembly-CSharp:221915, `CommandLineArgs?.Contains("-logFile") ?? false`), which breaks a recursion loop: under batch + `CustomLogFile`, `ConsoleWindow.Print` itself calls `UnityEngine.Debug.LogError/LogWarning/Log`.
- The non-threaded `Application.logMessageReceived` is never subscribed anywhere in the assembly.

Consequence for mods: a `Debug.LogError` is player-visible, and pairing it with a `ConsoleWindow` call for the same text double-prints.

**BepInEx `Log.LogError` does not travel this route, so pairing it with a `ConsoleWindow` call is safe.** This is the fact the whole "log to the file, print to the console" pattern rests on, and it is worth stating as a verified negative rather than an assumption. `StationeersLaunchPad.decompiled.cs` contains no subscription to BepInEx `ManualLogSource.LogEvent`, no `ILogListener` implementation, and no reference to `BepInEx.Logging` at all. Its only re-emission into Unity's log stream is `Logger.LogUnityInternal` (`:2905`, a `Debug.LogFormat` call), reachable only from `Logger.Log(..., unity: true)`, which requires a mod to call StationeersLaunchPad's own `Logger` directly rather than its BepInEx `ManualLogSource`. So a plugin's `Log.LogError(...)` never becomes a `Debug.LogError`, never raises `logMessageReceivedThreaded`, and never reaches the in-game console. `Log.LogError(msg)` alongside `ConsoleWindow.PrintError(msg, suppressStacktrace: true)` yields exactly one console line and one log line, which is the intended shape.

The one in-repo counter-example is a mod that deliberately bridges the two itself: `Plans/MaintenanceBureauPlus/MaintenanceBureauPlus/LaunchPadLog.cs` hooks its own BepInEx `ManualLogSource` and forwards every entry into `ConsoleWindow.Print` by reflection. That is the mod's own wiring, not a StationeersLaunchPad behaviour.

### 3. The reverse bridge: Unity to BepInEx, and why it does not reach LogOutput.log
<!-- verified: 0.2.6403.27689 @ 2026-07-29 -->

Unity's log stream is also fed INTO the BepInEx logging system, but at default settings it does not reach `LogOutput.log`. `[Logging] UnityLogListening` (default true) installs `UnityLogSource`, which captures `Debug.Log*` output as BepInEx log events. `[Logging.Disk] WriteUnityLog` (default **false**, described in `BepInEx.cfg` as "Include unity log messages in log file output.") then makes `DiskLogListener` discard them. Captured, not written.

Verified against a live session by counting the same markers in both files:

| Marker | `Player.log` | `LogOutput.log` |
|---|---|---|
| `^\[Global\]:` (StationeersLaunchPad via `Debug.LogFormat`) | 22 | 0 |
| `Begin MonoManager` (Unity engine) | 1 | 0 |
| `UnloadTime` (Unity engine) | 2 | 0 |

**The practical consequence, and the reason this matters for mod code: the useful direction is BepInEx to Unity, not Unity to BepInEx.** One `Log.LogError(msg)` on a plugin's `ManualLogSource` lands in BOTH `LogOutput.log` (via `DiskLogListener`) and `Player.log` (via `UnityLogListener`), from a single call, at every severity, with no console side effect. One `Debug.LogError(msg)` lands only in `Player.log` and additionally double-prints into the in-game console. So a mod that wants its output readable afterwards should prefer its BepInEx `ManualLogSource` over `UnityEngine.Debug.*` for everything.

Two traps in the same area:

- `LogMessage` lowercases the message before printing it (`logStr.ToLower()`, `Assembly-CSharp:222273`), so a `Debug.LogError` shows the player mangled casing for mod names, device names, and IC10 tokens.
- `Debug.LogAssertion` and `Debug.Assert` are `[Conditional("UNITY_ASSERTIONS")]`, evaluated at the *caller's* compile time. A mod whose `.csproj` does not define `UNITY_ASSERTIONS` has the call removed by its own compiler and the message reaches nothing at all. Across the whole game assembly there are 49 `Debug.Log(`, 36 `Debug.LogWarning(`, 97 `Debug.LogError(` and 35 `Debug.LogException(` call sites and zero of either assertion form, which is the signature of that stripping.

### 4. Player-prev.log
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->
- **Path:** Same directory as Player.log
- **Content:** Previous session's Player.log (Unity automatic rotation)

### 5. Console buffer export (PlayerLog_<timestamp>.log)
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->
- **Path:** %USERPROFILE%\My Games\Stationeers\ (see Defines.Paths.LocalData)
- **Invocation:** In-game console command: log or log <customname>
- **Content:** Entire 1024-line in-game console buffer with timestamps
- **Implementation:** LogCommand class (Assembly-CSharp:99289), dispatching `LogToFile(args).Forget()` at :99305 into `private static async UniTaskVoid LogToFile(string[] lineSplit)` at :99320
- **Default naming:** PlayerLog_YYYY-MM-DD_HH-mm-ss.log
- **Custom naming:** Spaces replaced with underscores, .log suffix added
- **Related:** log clear command deletes all *.log files in LocalData folder

### 6. Harmony FileLog
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->
- **Path:** %USERPROFILE%\Desktop\harmony.log.txt
- **Override:** Environment variable HARMONY_LOG_FILE (checked at 0Harmony line 8550)
- **Activation:** Only if Harmony.DEBUG = true (developer-only)
- **Content:** Low-level transpiler and patch-application traces
- **Implementation:** HarmonyLib.FileLog class (0Harmony:8529-8673)
- **Methods:**
  - Log(string) - immediate disk write via File.AppendText (line 8650)
  - LogBuffered(string) - queued flush
  - Reset() - deletes harmony.log.txt from desktop
- **Thread safety:** Guarded by lock object fileLock

### 7. Harmony FileWriter
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->
- **Path:** CWD\HarmonyLog.txt (overridable via FileWriter.FileWriterPath property, 0Harmony:9332)
- **Activation:** Only if FileWriter.Enabled = true (not default)
- **Content:** Harmony patch runtime messages (Logger.LogEventArgs events)
- **Implementation:** HarmonyLib.FileWriter class (0Harmony:9332-9355)
- **Operation:** Creates StreamWriter via File.Create on enable (line 9340); subscribes to Logger.MessageReceived
- **Format:** [LogChannel] Message (line 9352)

### 8. Dedicated server console (RocketSystemConsole)
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->
- **Activation:** Batch mode (GameManager.IsBatchMode) without -logFile flag
- **Path:** System console window (live output, not persisted by default)
- **Content:** All ConsoleWindow.Print* calls + console command input/output
- **Implementation:** RocketSystemConsole class (Assembly-CSharp:109870); PrintToConsole at :110054, which is a plain System.Console.WriteLine at :110066, with its own pre-ready queue at :110056-110060
- **Platform support:** Windows and Linux server builds
- **Features:**
  - UTF-8 encoding
  - Input thread for console commands
  - Title bar with game version and command-line args
  - Input forwarded to CommandLine.Process via ConsoleWindow.Submit (Assembly-CSharp:222487)
- **Initialization:** ConsoleWindow._Init, inside the IsBatchMode branch and conditional on !CustomLogFile (Assembly-CSharp:221929-221937; the `new RocketSystemConsole(...)` is at :221936)
- **Important:** This is live console, not persisted to disk unless stdout redirected
- **Caveat:** `PrintBlock` and `PrintSegmentedBlock` skip their entire body when IsBatchMode (Assembly-CSharp:223036), so they produce NO dedicated-server output. Only the plain `Print` has a batch-mode sink.

### 9. Custom log file (-logFile flag)
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->
- **Activation:** Batch mode with -logFile <path> command-line flag
- **Detection:** ConsoleWindow.CustomLogFile property (Assembly-CSharp:221915): CommandLineArgs?.Contains("-logFile")
- **Path:** Specified by flag or Unity default output_log.txt
- **Content:** UnityEngine.Debug.Log* calls (routed from ConsoleWindow.Print)
- **Behavior:** Skips RocketSystemConsole creation (:221934-221936); also disables the log bridge in section 2
- **Routing:** ConsoleWindow.Print routes to Debug.LogError / LogWarning / Log by colour band (Assembly-CSharp:222968-222986). Without -logFile, the batch path instead writes to the system console and silently DROPS any line containing `<color=` (:222987).

## Additional command-line flags
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

| Flag | Scope | Effect |
|---|---|---|
| -logFile <path> | Batch mode | Routes output to log file instead of system console; also disables the ConsoleWindow log bridge |
| -noclear | Batch mode | Prevents console window clearing on startup |
| HARMONY_LOG_FILE (env var) | Global | Overrides default harmony.log.txt path |

## Key path constant
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

**Defines.Paths.LocalData** (Assembly-CSharp:228560), verbatim:

```csharp
			public static string LocalData = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + _baseFolder;
```

Resolves to %USERPROFILE%\My Games\Stationeers\.

## Not covered

- Crash dumps (.dmp files) - OS-level, not game-code initiated
- Steam telemetry - third-party, not visible in game code
- Mod logging - documented in InGameConsoleOutput.md
- Network/RakNet logs - no explicit log file exposed in decompiled code

## Verification history

- 2026-07-29: **conflict on "what puts BepInEx plugin log lines into `Player.log`".** Previous claim (summary table and detail item 1): "BepInEx mod logs mirrored via StationeersLaunchPad". New finding: the route is BepInEx's own `BepInEx.Logging.UnityLogListener`, which formats with `[{0,-7}:{1,10}] {2}` and calls Unity's native `UnityEngine.UnityLogWriter.WriteStringToUnityLog`; StationeersLaunchPad is not involved. Fresh validator verdict: **B is correct**, quoting the assembly-qualified string `UnityEngine.UnityLogWriter, UnityEngine.CoreModule` from the `#US` heap of `BepInEx\core\BepInEx.dll`, with `UnityLogListener`, `UnityLogWriter`, `WriteStringToUnityLog`, `WriteFromUnityLog` and the namespace `BepInEx.Logging` in the same DLL's `#Strings` heap, and both target methods present in `UnityEngine.CoreModule.dll`. The decisive evidence is ordering: in a live `Player.log`, `[Message:   BepInEx] BepInEx 5.4.23.5` is line 19 and plugin `ManualLogSource` output (`[Info   :ClientDriver]`) is at lines 39-43, while `[Info   :   BepInEx] Loading [StationeersLaunchPad 0.5.0]` is line 44 and StationeersLaunchPad's own first line is 52. A component not yet loaded cannot be mirroring. The two competing routes are both off at default (`[Logging.Console] Enabled = false`, `[Logging] LogConsoleToUnityLog = false`), which forces the conclusion. The original observation was always correct; only the actor was misattributed, so the console double-print rule in detail item 2 is unaffected and is in fact strengthened, since the native writer bypasses `Debug.unityLogger` entirely and therefore cannot re-enter the console bridge at any severity. Result: summary table row and detail item 1 corrected; new detail item 3 added on the reverse bridge, recording that `[Logging.Disk] WriteUnityLog` defaults to false and so keeps `Debug.Log*` out of `LogOutput.log`, plus the `LogMessage` lowercasing trap and the `UNITY_ASSERTIONS` stripping trap; downstream detail items renumbered. This closes the Open Question opened earlier the same day. BepInEx assemblies were NOT decompiled (repo rule); see the remaining Open Questions for what that leaves unread.
- 2026-06-24: Page created from exhaustive search of Assembly-CSharp.decompiled.cs and 0Harmony.decompiled.cs against 0.2.6228.27061. Enumerated all StreamWriter, File.AppendText, File.Create, Application.logMessageReceived handlers, and console classes. Harmony logs verified from FileLog and FileWriter classes. Dedicated server logging verified from RocketSystemConsole and batch-mode initialization paths.
- 2026-07-27: re-verified and restamped against 0.2.6403.27689 during a repo-wide console-output audit; all Assembly-CSharp line citations updated (they were from the 0.2.6228.27061 decompile, whose folder no longer exists), Harmony citations re-checked and the FileLog class line corrected from 8534 to 8529. Two corrections and one addition. **Correction 1, the Player.log writer.** The summary table credited `Application.logMessageReceivedThreaded` as the writer of Player.log. It is not a writer at all; it is Unity's notification event, and Unity's player runtime writes Player.log internally regardless of subscribers. The row now names the Unity runtime. **Correction 2, what the subscription is for.** Old detail item 1 listed "Subscriber: Application.logMessageReceivedThreaded (ConsoleWindow._Init line 206182)" under Player.log, which conflated a consumer with a sink. That subscription is now its own section (detail item 2) describing what it actually does: pull `LogType.Error` and `LogType.Exception` INTO the in-game console buffer. No fresh validator was needed for either, because this page's claim that the subscription exists was the correct half of a conflict with `InGameConsoleOutput.md` and was upheld; see that page's 2026-07-27 Verification History entry for the binding verdict, which cites this page's original line numbers as the evidence that the subscription predates this game version. **Addition:** an in-game console buffer row in the summary table, the `-logFile` interaction with the log bridge, the `<color=` drop on the non-`-logFile` batch path, and the note that `PrintBlock` / `PrintSegmentedBlock` produce no dedicated-server output.

## Open questions

- **Which `WriteStringToUnityLog` overload binds at runtime.** Both `WriteStringToUnityLog` and `WriteStringToUnityLogImpl` appear as name strings in `BepInEx.dll` and both methods exist in `UnityEngine.CoreModule.dll`, which implies a reflection fallback chain. BepInEx's own changelog carries `(37f0a9aa) [Soggy_Pancake] v5: Fix log writer errors for Unity 6 (#1264)`, corroborating that the fallback exists. Which one binds on Unity 2022.3.62f3 was not determined. This is a curiosity, not a blocker: the observable behaviour is identical either way.
- **`UnityLogListener.LogEvent`'s method body has not been read.** The repo forbids decompiling outside `.work/decomp/<game-version>/`, and no BepInEx assembly is decompiled there. The routing in detail item 1 is established from the type and string tables in `BepInEx.dll`, the config keys, the live log contents, and the elimination of every alternative, but not from IL. Treat the mechanism as verified by behaviour rather than by reading the method.
