---
title: GameLoggingSinks
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-27
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.ConsoleWindow, GameManager, RocketSystemConsole, LogCommand, Defines.Paths
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs:99289-99340 (LogCommand), :221811-223400 (ConsoleWindow), :109870-110066 (RocketSystemConsole), :228560 (Defines.Paths.LocalData)
  - $(StationeersPath)\rocketstation_Data\Managed\0Harmony.dll :: FileLog, FileWriter
  - .work/decomp/0.2.6403.27689/0Harmony.decompiled.cs:8529-8673 (FileLog), :9332-9355 (FileWriter)
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
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

| Log sink | File path | Condition | Content | Writer |
|---|---|---|---|---|
| **Player.log** | %USERPROFILE%\AppData\LocalLow\Rocketwerkz\rocketstation\Player.log | Always active | UnityEngine.Debug.Log* and BepInEx mod logs (via LaunchPad mirror) | Unity player runtime (built-in) |
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
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

- **Path:** %USERPROFILE%\AppData\LocalLow\Rocketwerkz\rocketstation\Player.log
- **Rotation:** Auto-rotated to Player-prev.log on each launch
- **Content:** UnityEngine.Debug.Log/LogWarning/LogError output
- **Additional:** BepInEx mod logs mirrored via StationeersLaunchPad
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

### 3. Player-prev.log
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->
- **Path:** Same directory as Player.log
- **Content:** Previous session's Player.log (Unity automatic rotation)

### 4. Console buffer export (PlayerLog_<timestamp>.log)
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->
- **Path:** %USERPROFILE%\My Games\Stationeers\ (see Defines.Paths.LocalData)
- **Invocation:** In-game console command: log or log <customname>
- **Content:** Entire 1024-line in-game console buffer with timestamps
- **Implementation:** LogCommand class (Assembly-CSharp:99289), dispatching `LogToFile(args).Forget()` at :99305 into `private static async UniTaskVoid LogToFile(string[] lineSplit)` at :99320
- **Default naming:** PlayerLog_YYYY-MM-DD_HH-mm-ss.log
- **Custom naming:** Spaces replaced with underscores, .log suffix added
- **Related:** log clear command deletes all *.log files in LocalData folder

### 5. Harmony FileLog
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

### 6. Harmony FileWriter
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->
- **Path:** CWD\HarmonyLog.txt (overridable via FileWriter.FileWriterPath property, 0Harmony:9332)
- **Activation:** Only if FileWriter.Enabled = true (not default)
- **Content:** Harmony patch runtime messages (Logger.LogEventArgs events)
- **Implementation:** HarmonyLib.FileWriter class (0Harmony:9332-9355)
- **Operation:** Creates StreamWriter via File.Create on enable (line 9340); subscribes to Logger.MessageReceived
- **Format:** [LogChannel] Message (line 9352)

### 7. Dedicated server console (RocketSystemConsole)
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

### 8. Custom log file (-logFile flag)
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

- 2026-06-24: Page created from exhaustive search of Assembly-CSharp.decompiled.cs and 0Harmony.decompiled.cs against 0.2.6228.27061. Enumerated all StreamWriter, File.AppendText, File.Create, Application.logMessageReceived handlers, and console classes. Harmony logs verified from FileLog and FileWriter classes. Dedicated server logging verified from RocketSystemConsole and batch-mode initialization paths.
- 2026-07-27: re-verified and restamped against 0.2.6403.27689 during a repo-wide console-output audit; all Assembly-CSharp line citations updated (they were from the 0.2.6228.27061 decompile, whose folder no longer exists), Harmony citations re-checked and the FileLog class line corrected from 8534 to 8529. Two corrections and one addition. **Correction 1, the Player.log writer.** The summary table credited `Application.logMessageReceivedThreaded` as the writer of Player.log. It is not a writer at all; it is Unity's notification event, and Unity's player runtime writes Player.log internally regardless of subscribers. The row now names the Unity runtime. **Correction 2, what the subscription is for.** Old detail item 1 listed "Subscriber: Application.logMessageReceivedThreaded (ConsoleWindow._Init line 206182)" under Player.log, which conflated a consumer with a sink. That subscription is now its own section (detail item 2) describing what it actually does: pull `LogType.Error` and `LogType.Exception` INTO the in-game console buffer. No fresh validator was needed for either, because this page's claim that the subscription exists was the correct half of a conflict with `InGameConsoleOutput.md` and was upheld; see that page's 2026-07-27 Verification History entry for the binding verdict, which cites this page's original line numbers as the evidence that the subscription predates this game version. **Addition:** an in-game console buffer row in the summary table, the `-logFile` interaction with the log bridge, the `<color=` drop on the non-`-logFile` batch path, and the note that `PrintBlock` / `PrintSegmentedBlock` produce no dedicated-server output.

## Open questions

- **By what mechanism do BepInEx mod log lines reach `Player.log`?** The summary table and detail item 1 both credit a StationeersLaunchPad mirror, and that claim is verified only in the sense that the lines are observed in `Player.log`; the mechanism was never traced. It is now in tension with the 2026-07-29 finding in detail item 2, which established that `StationeersLaunchPad.decompiled.cs` has no `BepInEx.Logging` reference, no `ManualLogSource.LogEvent` subscription, and no `ILogListener`. Those two cannot both be right as stated, so one of them is describing the wrong actor. The likely resolution is that the route is BepInEx's own diskless-console or `UnityLogListener` plumbing rather than anything StationeersLaunchPad does, which would make "via LaunchPad mirror" a misattribution rather than a wrong observation. This does not affect the console-double-print rule either way: that rule depends only on the verified negative (no BepInEx to `UnityEngine.Debug` bridge in StationeersLaunchPad), which was established directly. Resolving it needs a pass over `BepInEx.Core` / `BepInEx.Preloader`, which no page here has decompiled yet. Left as an open question rather than silently overwritten, because correcting a verified claim requires the Rule 3 fresh-validator protocol and the evidence in hand is one-sided.
