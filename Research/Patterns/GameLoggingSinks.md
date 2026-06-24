---
title: GameLoggingSinks
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-24
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.ConsoleWindow, GameManager, Settings, LogCommand
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs:96920-97033 (LogCommand), :206094-206957 (ConsoleWindow), :212181-212186 (Defines.Paths)
  - $(StationeersPath)\rocketstation_Data\Managed\0Harmony.dll :: FileLog, FileWriter
  - .work/decomp/0.2.6228.27061/0Harmony.decompiled.cs:8534-8673 (FileLog), :9332-9355 (FileWriter)
related:
  - ../Patterns/InGameConsoleOutput.md
  - ../GameSystems/DedicatedServerSettings.md
tags: [logging, diagnostics, debugging]
---

# Stationeers game logging and diagnostic output sinks

The Stationeers game writes diagnostic and telemetry data to multiple log sinks. This page catalogs every known log file and output mechanism the game itself produces.

## Summary table

| Log sink | File path | Condition | Content | Writer |
|---|---|---|---|---|
| **Player.log** | %USERPROFILE%\AppData\LocalLow\Rocketwerkz\rocketstation\Player.log | Always active | UnityEngine.Debug.Log* and BepInEx mod logs (via LaunchPad mirror) | Application.logMessageReceivedThreaded |
| **Player-prev.log** | Same folder | Auto-rotated | Previous session Player.log | Unity built-in rotation |
| **Console export** | %USERPROFILE%\My Games\Stationeers\PlayerLog_*.log | Player-initiated via log command | In-game console buffer dump | LogCommand.LogToFile |
| **Harmony debug log** | Desktop\harmony.log.txt OR HARMONY_LOG_FILE env var | Opt-in (Harmony.DEBUG=true) | Low-level patch tracing | HarmonyLib.FileLog |
| **Harmony FileWriter** | CWD\HarmonyLog.txt OR FileWriter.FileWriterPath | Opt-in (FileWriter.Enabled=true) | Harmony runtime messages | HarmonyLib.FileWriter |
| **Dedicated server console** | Console window (live, not persisted) | Batch mode without -logFile | ConsoleWindow output to system console | RocketSystemConsole |
| **Custom log file** | Specified via -logFile flag | Batch mode with -logFile | UnityEngine.Debug.Log* calls | UnityEngine.Debug |

## Detailed findings

### 1. Player.log (primary)
- **Path:** %USERPROFILE%\AppData\LocalLow\Rocketwerkz\rocketstation\Player.log
- **Rotation:** Auto-rotated to Player-prev.log on each launch
- **Content:** UnityEngine.Debug.Log/LogWarning/LogError output
- **Additional:** BepInEx mod logs mirrored via StationeersLaunchPad
- **Subscriber:** Application.logMessageReceivedThreaded (ConsoleWindow._Init line 206182)

### 2. Player-prev.log
- **Path:** Same directory as Player.log
- **Content:** Previous session's Player.log (Unity automatic rotation)

### 3. Console buffer export (PlayerLog_<timestamp>.log)
- **Path:** %USERPROFILE%\My Games\Stationeers\ (see Defines.Paths.LocalData)
- **Invocation:** In-game console command: log or log <customname>
- **Content:** Entire 1024-line in-game console buffer with timestamps
- **Implementation:** LogCommand class (Assembly-CSharp:96920-97033)
- **Default naming:** PlayerLog_YYYY-MM-DD_HH-mm-ss.log
- **Custom naming:** Spaces replaced with underscores, .log suffix added
- **Related:** log clear command deletes all *.log files in LocalData folder

### 4. Harmony FileLog
- **Path:** %USERPROFILE%\Desktop\harmony.log.txt
- **Override:** Environment variable HARMONY_LOG_FILE (checked at 0Harmony line 8550)
- **Activation:** Only if Harmony.DEBUG = true (developer-only)
- **Content:** Low-level transpiler and patch-application traces
- **Implementation:** HarmonyLib.FileLog class (0Harmony:8534-8673)
- **Methods:**
  - Log(string) - immediate disk write via File.AppendText (line 8650)
  - LogBuffered(string) - queued flush
  - Reset() - deletes harmony.log.txt from desktop
- **Thread safety:** Guarded by lock object fileLock

### 5. Harmony FileWriter
- **Path:** CWD\HarmonyLog.txt (overridable via FileWriter.FileWriterPath property)
- **Activation:** Only if FileWriter.Enabled = true (not default)
- **Content:** Harmony patch runtime messages (Logger.LogEventArgs events)
- **Implementation:** HarmonyLib.FileWriter class (0Harmony:9314-9355)
- **Operation:** Creates StreamWriter via File.Create on enable; subscribes to Logger.MessageReceived
- **Format:** [LogChannel] Message (line 9352)

### 6. Dedicated server console (RocketSystemConsole)
- **Activation:** Batch mode (GameManager.IsBatchMode) without -logFile flag
- **Path:** System console window (live output, not persisted by default)
- **Content:** All ConsoleWindow.Print* calls + console command input/output
- **Implementation:** RocketSystemConsole class (Assembly-CSharp:105597-105642)
- **Platform support:** Windows and Linux server builds (line 105627)
- **Features:**
  - UTF-8 encoding (line 105636)
  - Input thread for console commands (line 105639)
  - Title bar with game version and command-line args
  - Input forwarded to CommandLine.Process (line 206219)
- **Initialization:** ConsoleWindow._Init line 206191 (conditional on IsBatchMode && !CustomLogFile)
- **Important:** This is live console, not persisted to disk unless stdout redirected

### 7. Custom log file (-logFile flag)
- **Activation:** Batch mode with -logFile <path> command-line flag
- **Detection:** ConsoleWindow.CustomLogFile property (line 206177): CommandLineArgs?.Contains("-logFile")
- **Path:** Specified by flag or Unity default output_log.txt
- **Content:** UnityEngine.Debug.Log* calls (routed from ConsoleWindow.Print)
- **Behavior:** Skips RocketSystemConsole creation (line 206189)
- **Routing:** ConsoleWindow.Print routes to Debug.Log/LogWarning/LogError (lines 206856-206873)

## Additional command-line flags

| Flag | Scope | Effect |
|---|---|---|
| -logFile <path> | Batch mode | Routes output to log file instead of system console |
| -noclear | Batch mode | Prevents console window clearing on startup |
| HARMONY_LOG_FILE (env var) | Global | Overrides default harmony.log.txt path |

## Key path constant

**Defines.Paths.LocalData** (Assembly-CSharp:212185):
%USERPROFILE%\My Games\Stationeers\ (derived from Environment.SpecialFolder.MyDocuments + "\My Games\Stationeers\\")

## Not covered

- Crash dumps (.dmp files) - OS-level, not game-code initiated
- Steam telemetry - third-party, not visible in game code
- Mod logging - documented in InGameConsoleOutput.md
- Network/RakNet logs - no explicit log file exposed in decompiled code

## Verification history

2026-06-24: Page created from exhaustive search of Assembly-CSharp.decompiled.cs and 0Harmony.decompiled.cs. Enumerated all StreamWriter, File.AppendText, File.Create, Application.logMessageReceived handlers, and console classes. Harmony logs verified from FileLog (line 8534) and FileWriter (line 9332) classes. Dedicated server logging verified from RocketSystemConsole and batch-mode initialization paths.

