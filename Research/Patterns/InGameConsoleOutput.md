---
title: InGameConsoleOutput
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-11
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: ConsoleWindow
related:
  - ../GameSystems/ChatBroadcast.md
tags: [ui]
---

# Printing to the in-game `~` console (and where mod log lines actually go)

There are three separate "log" surfaces in a BepInEx-modded Stationeers, and they do **not** all carry the same content:

| Surface | Fed by | A player typically looks here? |
|---|---|---|
| `BepInEx\LogOutput.log` (disk) | BepInEx `ManualLogSource.LogInfo/LogWarning/LogError` (i.e. `Logger.Log*` in a plugin). StationeersLaunchPad also mirrors mod log lines into `Player.log`. | Power users / when sending a log to a mod author. |
| Unity `Player.log` (`%USERPROFILE%\AppData\LocalLow\Rocketwerkz\rocketstation\Player.log`, rotated to `Player-prev.log` on each launch) | `UnityEngine.Debug.Log/LogWarning/LogError`, plus (via StationeersLaunchPad's mirror) BepInEx mod log lines. | Occasionally. |
| The in-game `~` console (`ConsoleWindow`) | **Only** `ConsoleWindow.Print* ` calls and console-command output. **Not** `UnityEngine.Debug.Log`, and **not** BepInEx `Logger.Log*`. | Yes -- this is the one a player sees while playing. |

**Key gotcha:** `UnityEngine.Debug.Log(...)` writes to `Player.log` but does **not** appear in the in-game `~` console (Stationeers' `ConsoleWindow` does not subscribe to `Application.logMessageReceived`). And BepInEx `Logger.LogInfo(...)` goes to `LogOutput.log` (and `Player.log` via the SLP mirror) but also not the in-game console. So a mod whose only output is `Logger.LogInfo` / `Debug.Log` will look "silent" to a player who checks the `~` console. To show up there, call `ConsoleWindow.Print*`.

## ConsoleWindow API

`public static class ConsoleWindow` (`Assembly-CSharp.dll`; resolve by reflection -- the namespace is ambiguous in the decompile dump and a direct `using` failed to compile, so treat the namespace as unstable). Print methods:

```csharp
public static void Print(string output, ConsoleColor color = ConsoleColor.White, bool clearLine = false, bool aged = true, bool unformatted = false);
public static void PrintAction(string output, bool aged = false);                  // the "an action happened" style line
public static void PrintError(string output, bool suppressStacktrace = false);
// plus several Print(GameString, ...) overloads for localised strings
```

`PrintAction` is what the game itself uses for "X happened" notices (input recording started, server paused, an id was reassigned, etc.). `PrintError` for errors. `aged: false` keeps the line from fading out quickly.

The buffer accepts lines even before the console UI exists (there is an internal `PrematureLog` struct that queues early calls), but at *very* early startup -- e.g. a `Prefab.OnPrefabsLoaded` handler, which fires before the main menu -- it is safest to wrap the call in try/catch and rely on the `Debug.Log` / `Logger.Log` channels as the fallback.

## Recommended mod pattern

For a message the player should actually see, emit it on all three channels (greppable in the log files, visible in the `~` console). Resolve `ConsoleWindow` by reflection so the build does not hard-depend on its namespace and degrades gracefully:

```csharp
static MethodInfo _printAction, _printError; static bool _resolved;
static void ResolveConsole() {
    if (_resolved) return; _resolved = true;
    try {
        var t = AccessTools.TypeByName("Util.Commands.ConsoleWindow")
             ?? AccessTools.TypeByName("Assets.Scripts.ConsoleWindow")
             ?? AccessTools.TypeByName("ConsoleWindow")
             ?? AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                  .FirstOrDefault(x => x.Name == "ConsoleWindow" && x.IsClass && x.IsAbstract && x.IsSealed);
        if (t == null) return;
        _printAction = AccessTools.Method(t, "PrintAction", new[] { typeof(string), typeof(bool) }) ?? AccessTools.Method(t, "PrintAction", new[] { typeof(string) });
        _printError  = AccessTools.Method(t, "PrintError",  new[] { typeof(string), typeof(bool) }) ?? AccessTools.Method(t, "PrintError",  new[] { typeof(string) });
    } catch { }
}
static void PlayerLog(string msg) {
    Plugin.Log?.LogInfo(msg);                          // -> LogOutput.log (+ Player.log via SLP mirror)
    UnityEngine.Debug.Log($"[ModName] {msg}");          // -> Player.log
    ResolveConsole();
    try { _printAction?.Invoke(null, _printAction.GetParameters().Length >= 2 ? new object[] { $"[ModName] {msg}", false } : new object[] { $"[ModName] {msg}" }); } catch { }   // -> in-game ~ console
}
```

`NetworkPuristPlus` (`Mods/NetworkPuristPlus/`) uses exactly this `PlayerLog` / `PlayerWarn` / `PlayerError` pattern.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-05-11 -->

- 2026-05-11: page created after a NetworkPuristPlus user reported "I see nothing in the player log" despite the mod's `Logger.LogInfo` lines being present in both `LogOutput.log` and `Player.log` -- they were looking at the in-game `~` console, which neither `Logger.Log*` nor `Debug.Log` reaches. `ConsoleWindow.PrintAction` / `PrintError` signatures lifted from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` (around line 206094, 206824-206957). Namespace of `ConsoleWindow` could not be pinned from the dump (the `awk` "last `namespace` before the class" heuristic said `Assets.Scripts`; a brace-depth tracker said global -- unreliable because decompiled `$"..."` interpolation throws off naive `{ }` counting; a direct `using Util.Commands;` failed to compile) -- hence the reflection-based resolver.

## Open questions

- The exact namespace of `ConsoleWindow` (the reflection resolver sidesteps it; a clean decompile pass with proper namespace tracking would settle it).
