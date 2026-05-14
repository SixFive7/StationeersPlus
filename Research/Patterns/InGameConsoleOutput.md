---
title: InGameConsoleOutput
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-12
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.ConsoleWindow
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs:206094-206957 (ConsoleWindow), :184063 (enclosing namespace Assets.Scripts), :248232 (Assets.Scripts.Settings)
  - Mods/EquipmentPlus/EquipmentPlus/HelmetBeamPatches.cs:123, ScrollDispatchPatches.cs:271 (direct ConsoleWindow.PrintError reference, using Assets.Scripts;)
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

**Key gotcha:** `UnityEngine.Debug.Log(...)` writes to `Player.log` but does **not** appear in the in-game `~` console (Stationeers' `ConsoleWindow` does not subscribe to `Application.logMessageReceived`). And BepInEx `Logger.LogInfo(...)` goes to `LogOutput.log` (and `Player.log` via the StationeersLaunchPad mirror) but also not the in-game console. So a mod whose only output is `Logger.LogInfo` / `Debug.Log` will look "silent" to a player who checks the `~` console. To show up there, call `ConsoleWindow.Print*`.

## ConsoleWindow API
<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

`public static class Assets.Scripts.ConsoleWindow` (`Assembly-CSharp.dll`). The namespace is **`Assets.Scripts`** -- the class sits inside the `namespace Assets.Scripts { ... }` block (around `Assembly-CSharp.decompiled.cs:206094`; the enclosing namespace opens at ~184063 and the next sibling namespace `Assets.Scripts.Weather` does not open until ~207612). The game's own code refers to it everywhere as bare `ConsoleWindow.PrintAction(...)` / `ConsoleWindow.PrintError(...)` after `using Assets.Scripts;`. There is exactly one `ConsoleWindow` type in `Assembly-CSharp` and none in any other shipped DLL. Print methods:

```csharp
public static void Print(string output, ConsoleColor color = ConsoleColor.White, bool clearLine = false, bool aged = true, bool unformatted = false);
public static void PrintAction(string output, bool aged = false);                  // the "an action happened" style line (Yellow)
public static void PrintError(string output, bool suppressStacktrace = false);     // Red, plus a gray Environment.StackTrace line unless suppressed
// plus several Print(GameString, ...) overloads for localised strings, and AsyncPrintError(string, bool)
```

`PrintAction` is what the game itself uses for "X happened" notices (input recording started, server paused, an id was reassigned, etc.). `PrintError` for errors. `aged: false` keeps the line from fading out quickly.

The buffer accepts lines even before the console UI exists (there is an internal `PrematureLog` struct that queues early calls), but at *very* early startup -- e.g. a `Prefab.OnPrefabsLoaded` handler, which fires before the main menu -- it is safest to wrap the call in try/catch and rely on the `Debug.Log` / `Logger.Log` channels as the fallback.

## Recommended mod pattern
<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

For a message the player should actually see, emit it on all three channels (greppable in the log files, visible in the `~` console). A direct compile-time reference is fine for any mod that already depends on `Assembly-CSharp` (`ConsoleWindow` is a vanilla game class). Reference it via a `using` alias rather than a blanket `using Assets.Scripts;` -- `Assets.Scripts` is a large namespace that contains common short names (`Settings : UserInterfaceBase` at ~`Assembly-CSharp.decompiled.cs:248232`, `Localization`, etc.), so a bare `using Assets.Scripts;` collides with any same-named type in the caller's own namespace (a mod with its own `Settings` class gets `CS0104 'Settings' is an ambiguous reference`). The alias dodges that:

```csharp
using ConsoleWindow = Assets.Scripts.ConsoleWindow;   // top of the file, with the other usings

internal static void PlayerLog(string msg) {
    Plugin.Log?.LogInfo(msg);                          // -> LogOutput.log (+ Player.log via StationeersLaunchPad mirror)
    UnityEngine.Debug.Log($"[ModName] {msg}");          // -> Player.log
    try { ConsoleWindow.PrintAction($"[ModName] {msg}", aged: false); } catch { }   // -> in-game ~ console
}
internal static void PlayerError(string msg) {
    Plugin.Log?.LogError(msg);
    UnityEngine.Debug.LogError($"[ModName] {msg}");
    try { ConsoleWindow.PrintError($"[ModName] {msg}", suppressStacktrace: true); } catch { }
}
```

`EquipmentPlus` references `ConsoleWindow.PrintError(...)` exactly this way -- `using Assets.Scripts;` plus a bare `ConsoleWindow.PrintError(...)` (it has no `Settings` class of its own, so the blanket `using` is safe there): `Mods/EquipmentPlus/EquipmentPlus/HelmetBeamPatches.cs:123`, `ScrollDispatchPatches.cs:271`. `MaintenanceBureauPlus` (`Plans/`) reflects but already uses the correct `asm.GetType("Assets.Scripts.ConsoleWindow")` name.

Reflection variant (only if you want zero compile-time dependency on `Assembly-CSharp` and graceful degradation if the class ever moves):

```csharp
static MethodInfo _printAction, _printError; static bool _resolved;
static void ResolveConsole() {
    if (_resolved) return; _resolved = true;
    try {
        var t = AccessTools.TypeByName("Assets.Scripts.ConsoleWindow")
             ?? AccessTools.TypeByName("ConsoleWindow")
             ?? AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                  .FirstOrDefault(x => x.Name == "ConsoleWindow" && x.IsClass && x.IsAbstract && x.IsSealed);
        if (t == null) return;
        _printAction = AccessTools.Method(t, "PrintAction", new[] { typeof(string), typeof(bool) }) ?? AccessTools.Method(t, "PrintAction", new[] { typeof(string) });
        _printError  = AccessTools.Method(t, "PrintError",  new[] { typeof(string), typeof(bool) }) ?? AccessTools.Method(t, "PrintError",  new[] { typeof(string) });
    } catch { }
}
```

Do not include `"Util.Commands.ConsoleWindow"` in the resolver -- `Util.Commands` is a real namespace (the `say` / console-command classes live there) but `ConsoleWindow` is not in it; the lookup always fails and HarmonyX logs a `Could not find type named Util.Commands.ConsoleWindow` warning at startup.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

- 2026-05-11: page created after a NetworkPuristPlus user reported "I see nothing in the player log" despite the mod's `Logger.LogInfo` lines being present in both `LogOutput.log` and `Player.log` -- they were looking at the in-game `~` console, which neither `Logger.Log*` nor `Debug.Log` reaches. `ConsoleWindow.PrintAction` / `PrintError` signatures lifted from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` (around line 206094, 206824-206957). Namespace of `ConsoleWindow` could not be pinned from the dump at the time (the `awk` "last `namespace` before the class" heuristic said `Assets.Scripts`; a brace-depth tracker said global -- unreliable because decompiled `$"..."` interpolation throws off naive `{ }` counting; a direct `using Util.Commands;` failed to compile) -- the page recommended the reflection-based resolver and listed the namespace as an open question.
- 2026-05-12: namespace resolved to `Assets.Scripts`. Confirmed two ways: (1) in `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` the `public static class ConsoleWindow` at ~206094 sits inside the `namespace Assets.Scripts` block opened at ~184063 with no intervening `namespace` declaration before the next sibling `namespace Assets.Scripts.Weather` at ~207612; (2) `EquipmentPlus` compiles cleanly with `using Assets.Scripts;` + a bare `ConsoleWindow.PrintError(...)` (`Mods/EquipmentPlus/EquipmentPlus/HelmetBeamPatches.cs:123`, `ScrollDispatchPatches.cs:271`; built `EquipmentPlus.dll` present). The earlier "a direct `using` failed to compile" was specifically `using Assets.Scripts;` colliding with a same-named `Settings` class in the caller (`NetworkPuristPlus.Settings` vs `Assets.Scripts.Settings`), not a `ConsoleWindow`-name problem -- the fix is a `using ConsoleWindow = Assets.Scripts.ConsoleWindow;` alias. `ConsoleWindow API` and `Recommended mod pattern` sections rewritten accordingly; `Util.Commands.ConsoleWindow` removed from the suggested reflection chain (it never matched). No fresh-validator pass needed: the contradicted claim ("namespace unknown / a `using` fails") is overturned by a strictly stronger source (the decompiled `namespace` block plus a compiling counter-example mod), with no ambiguity to resolve.

## Open questions

None.
