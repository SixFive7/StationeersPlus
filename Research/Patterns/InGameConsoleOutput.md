---
title: InGameConsoleOutput
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-29
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.ConsoleWindow
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs:221811-223400 (ConsoleWindow), :195722 (enclosing namespace Assets.Scripts), :265338 (Assets.Scripts.Serialization.Settings)
  - Mods/NetworkPuristPlus/NetworkPuristPlus/Plugin.cs (the PlayerLog / PlayerWarn / PlayerError helpers)
  - Mods/PowerGridPlus/PowerGridPlus/DeviceOutputSanitizer.cs (dedupe + main-thread marshal reference implementation)
related:
  - ../GameClasses/ConsoleWindow.md
  - ./GameLoggingSinks.md
  - ../GameSystems/ChatBroadcast.md
tags: [ui, threading]
---

# Printing to the in-game console (and where mod log lines actually go)

How a mod puts a message in front of a player, and the traps that make it go wrong. The API reference for the class itself, with verbatim decompiled bodies, is [ConsoleWindow](../GameClasses/ConsoleWindow.md).

There are three separate "log" surfaces in a BepInEx-modded Stationeers, and they do **not** carry the same content:

| Surface | Fed by | A player typically looks here? |
|---|---|---|
| `BepInEx\LogOutput.log` (disk) | BepInEx `ManualLogSource.LogInfo/LogWarning/LogError` (i.e. `Logger.Log*` in a plugin). StationeersLaunchPad also mirrors mod log lines into `Player.log`. | Power users / when sending a log to a mod author. |
| Unity `Player.log` (`%USERPROFILE%\AppData\LocalLow\Rocketwerkz\rocketstation\Player.log`, rotated to `Player-prev.log` on each launch) | `UnityEngine.Debug.Log/LogWarning/LogError`, plus (via StationeersLaunchPad's mirror) BepInEx mod log lines. | Occasionally. |
| The in-game console (`ConsoleWindow`, opened with `KeyMap.ToggleConsole`, F3 by default) | `ConsoleWindow.Print*` calls, console-command output, **and** `UnityEngine.Debug.LogError` / `LogException` via the log bridge below. **Not** `Debug.Log`, **not** `Debug.LogWarning`, **not** BepInEx `Logger.Log*`. | Yes, this is the one a player sees while playing. |

**Key gotcha:** `Debug.Log(...)` and `Logger.LogInfo(...)` write to their log files but do not appear in the in-game console, so a mod whose only output is those will look silent to a player who checks the console. To show up there, call `ConsoleWindow.Print*`.

**The matching trap in the other direction:** `Debug.LogError` and `Debug.LogException` DO appear in the in-game console. `ConsoleWindow` subscribes to `Application.logMessageReceivedThreaded` (`Assembly-CSharp.decompiled.cs:221927`) and its handler (`:222266-222282`) re-prints `LogType.Error` and `LogType.Exception` itself, lowercased, in red, prefixed with the uppercased level name in brackets (`[ERROR]` or `[EXCEPTION]`), followed by the stack trace. So calling `Debug.LogError` **and** a `ConsoleWindow` method for the same message shows it to the player twice. Pick one.

## The five traps
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

Each of these produced a real defect in this repo, found in the 2026-07-27 audit.

1. **`aged` is inverted from its name.** `aged: true` sets the line's activeTime to 0, so it is NOT drawn on the closed-console overlay and appears only once the console is opened. `aged: false` is what puts a line in front of a player who has not opened the console. Plain `Print` defaults to `aged: true`; `PrintAction` and `PrintError` already pass `false`.

2. **`PrintError` dumps a stack trace by default.** Unless it is passed `suppressStacktrace: true`, it prints a full `Environment.StackTrace` as a second grey line. On an ordinary "you cannot do that" message this reads to a player as a mod crash. Always pass `suppressStacktrace: true` for anything that is not a genuine unexpected failure, and prefer the BepInEx log for the diagnostic detail.

3. **There is no `PrintWarning`.** The three levels available are `Print` (any `ConsoleColor`), `PrintAction` (Yellow), and `PrintError` (Red). A warning belongs in `PrintAction`, not `PrintError`.

4. **Main thread only.** Every print shifts an unlocked 1024-entry static array while the draw loop reads it. Marshal first from any worker, `UniTask`, `FileSystemWatcher`, or network-message thread. The game's own async print paths check `GameManager.IsThread` and `await UniTask.SwitchToMainThread()` before printing.

5. **No rate limiting exists, and every print is O(1024).** The console has no throttle, no dedupe, and no same-message collapse; each print runs the full ring-buffer shift. Self-limiting is entirely the caller's job. This matters most on per-tick, per-frame, and per-input-notch paths: one scroll-wheel flick is 10-20 notches.

Two more constraints that shape message text:

- **No rich text.** The console renders through ImGui `TextUnformatted`, so TextMeshPro tags appear as literal characters. Worse, on a dedicated server without `-logFile`, any line containing `<color=` is silently discarded (`:222987`). Keep console text plain, and be careful with interpolated values that a player controls (a renameable `DisplayName`, chat text) or that a language model produced.
- **Process-local, not networked.** A `Print` on the server appears only on the server. To reach clients a mod must send its own message and print locally in `Process()`; see [ChatBroadcast](../GameSystems/ChatBroadcast.md) for the replicating chat channel and the `NetworkServer.SendToClient<T>` unicast form.

Safe to call on every machine role including a headless dedicated server (the batch-mode branch writes stdout or the log and touches no UI) and before world load (early lines queue in `_prematureLogQueue` and replay at init).

## Recommended mod pattern
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

Reference `ConsoleWindow` through a `using` alias rather than a blanket `using Assets.Scripts;`. The alias imports one name instead of the 145 top-level types in that namespace and states the fully-qualified target at the top of the file, so a namespace move at the next game update produces a precise error.

```csharp
using ConsoleWindow = Assets.Scripts.ConsoleWindow;   // top of the file, with the other usings

// Informational. Yellow, visible without opening the console, no stack trace.
internal static void PlayerLog(string msg) {
    Plugin.Log?.LogInfo(msg);                          // -> LogOutput.log (+ Player.log via StationeersLaunchPad mirror)
    UnityEngine.Debug.Log($"[ModName] {msg}");         // -> Player.log. LogType.Log is NOT re-printed, so no duplicate.
    try { ConsoleWindow.PrintAction($"[ModName] {msg}", aged: false); } catch { }
}

// A warning is not an error. There is no PrintWarning; yellow is PrintAction.
internal static void PlayerWarn(string msg) {
    Plugin.Log?.LogWarning(msg);
    UnityEngine.Debug.LogWarning($"[ModName] {msg}");  // LogType.Warning is NOT re-printed either.
    try { ConsoleWindow.PrintAction($"[ModName] {msg}", aged: false); } catch { }
}

// NOTE the absence of Debug.LogError. It would be re-printed by the console's own
// logMessageReceivedThreaded handler, so the player would see the line twice: once as this
// controlled PrintError, and again lowercased with an unavoidable stack trace. Nothing is lost --
// Log.LogError already reaches Player.log through the StationeersLaunchPad mirror.
internal static void PlayerError(string msg) {
    Plugin.Log?.LogError(msg);
    try { ConsoleWindow.PrintError($"[ModName] {msg}", suppressStacktrace: true); } catch { }
}

// Exception overload: full detail to the file log, type and message only to the player.
// Interpolating a bare {e} into a console line dumps a multi-line managed stack trace complete
// with compiler-generated frame names like <Postfix>b__0.
internal static void PlayerError(string msg, System.Exception e) {
    Plugin.Log?.LogError($"{msg}: {e}");
    try { ConsoleWindow.PrintError($"[ModName] {msg}: {e.GetType().Name}: {e.Message}", suppressStacktrace: true); } catch { }
}
```

The `try`/`catch` is for calls that can fire from `Prefab.OnPrefabsLoaded`, before the console UI exists. `ConsoleWindow` has an internal premature-log queue so it should be fine, but the catch costs nothing.

**Self-limiting on repeatable paths.** The console will not do it for you. Three patterns in use in this repo, in rough order of preference:

- **Once per subject per session.** `Mods/PowerGridPlus/PowerGridPlus/DeviceOutputSanitizer.cs` keeps a `ConcurrentDictionary<long, byte>` keyed by reference id and calls `TryAdd` before printing, so each broken device is named exactly once and the set is cleared on world load. It also marshals through `UnityMainThreadDispatcher` because the power tick runs on a worker, and degrades to the file log when no dispatcher exists. This is the reference implementation.
- **Hard cap with an overflow summary.** `Mods/PowerGridPlus/PowerGridPlus/WreckageCleanup.cs` prints at most `AnnounceCap = 6` lines and then one summary line for the remainder.
- **First occurrence only, rest to the file log.** `Mods/NetworkPuristPlus/NetworkPuristPlus/ClampNegativeMergeQuantityPatch.cs` and the one-shot guards in `CableRollOnConstruct`.

For a loop over world content (every cable, every structure), prefer a count plus a summary line over one line per item. `Mods/EquipmentPlus/EquipmentPlus/PlayerNotice.cs` shows the input-path variant: a per-message cooldown, because the alternative is one line per scroll notch.

**Message prefix.** Use a bracketed mod tag so a player can tell whose message it is. Prefixes are inconsistent across this repo today (`[EquipmentPlus]`, `[NetworkPuristPlus]`, `[Power Grid Plus]`, `[PowerGridPlus]`, and PowerGridPlus's `PlayerConsole` messages carry none at all). The repo convention for player-facing text is the display name, so `[Power Grid Plus]` is the correct form of that pair; the code name is for machine-facing identifiers. Whichever a mod picks, it should use one form everywhere.

Reflection variant, only if you want zero compile-time dependency on `Assembly-CSharp` and graceful degradation if the class ever moves:

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

Do not include `"Util.Commands.ConsoleWindow"` in the resolver. `Util.Commands` is a real namespace (the `say` / console-command classes live there) but `ConsoleWindow` is not in it; the lookup always fails and HarmonyX logs a `Could not find type named Util.Commands.ConsoleWindow` warning at startup.

## The namespace question, and the CS0104 story
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

`ConsoleWindow` is in `Assets.Scripts` (class at `:221811`, inside the `namespace Assets.Scripts` block opened at `:195722`, next sibling namespace `Assets.Scripts.Weather` at `:223978`). A blanket `using Assets.Scripts;` plus a bare `ConsoleWindow.PrintError(...)` compiles.

This page previously claimed that a blanket `using Assets.Scripts;` risks `CS0104 'Settings' is an ambiguous reference` for a mod with its own `Settings` class. **That claim was wrong and has been removed.** There is exactly one type named `Settings` in the entire reference closure of these mod projects, and it is `Assets.Scripts.Serialization.Settings` (`public class Settings : UserInterfaceBase`, `:265338`, inside `namespace Assets.Scripts.Serialization` opened at `:264409`). `Assets.Scripts` contains no `Settings` at all, so there is only ever one candidate and CS0104 cannot fire. Independently, even with two candidates C# resolves a name in the enclosing namespace before consulting that namespace's using-directives, so a mod's own `Settings` would win regardless. Six files in this repo already combine `using Assets.Scripts;` with an unqualified reference to their own `Settings` and are present in shipped DLLs.

The alias recommendation above still stands, on the honest grounds (one imported name instead of 145, and a precise error if the type moves) rather than on the CS0104 grounds.

Where real ambiguity exposure in this codebase actually comes from: `Assembly-CSharp` ships parallel namespace pairs, both `Objects` and `Assets.Scripts.Objects`, both `Networks` and `Assets.Scripts.Networks`, both `Networking` and `Assets.Scripts.Networking`, both `Util` and `Assets.Scripts.Util`, both `UI` and `Assets.Scripts.UI`, both `Weather` and `Assets.Scripts.Weather`. `Mods/PowerGridPlus/PowerGridPlus/VoltageTier.cs` already imports `Assets.Scripts.Objects`, `Objects`, and `Objects.Rockets` together.

## Verification history

- 2026-07-29: **conflict on "what bracketed prefix does the log bridge put in front of a re-printed message".** Previous claim (line 33): the handler re-prints `LogType.Error` and `LogType.Exception` "lowercased, prefixed `[ERROR]`". New finding: the prefix is `EnumCollections.LogTypes.GetName(type).ToUpper()`, and that collection is constructed with `toProper: false`, so the names stay as raw `Enum.GetNames` output and `LogType.Exception` renders `[EXCEPTION]`, not `[ERROR]`. Fresh validator verdict: **B is correct**, quoting `LogMessage` at `:222266-222282` (`string text = EnumCollections.LogTypes.GetName(type).ToUpper();`) and the `EnumCollection<T1,T2>` constructor at `:203609-203619`, where `Names = Enum.GetNames(typeof(T1))` is only rewritten by `ToProper()` inside `if (toProper)`. The validator confirmed the field is `static readonly` and never reassigned (`:203493` and `:222272` are its only references), that no post-construction write to `Names` exists, that `GetNameFromIndex` is `virtual` but has no subclass anywhere in the assembly, and that `padded` defaults to `false` so the space-padded `PaddedNames` is not in play (`padded: true` would have produced `[ERROR    ]`). Everything else in the old sentence (lowercasing, red, the `Error || Exception` gate, the trailing stack trace) was accurate. Result: the "matching trap in the other direction" paragraph now names both tags. The parallel bullet on [ConsoleWindow](../GameClasses/ConsoleWindow.md) said only "e.g. `[ERROR] `", which was hedged rather than wrong; it was expanded with the same derivation so the two pages cannot drift apart again. Found while reviewing the console-output fix commits, which had copied the `[ERROR]` generalisation out of this page and into a mod code comment.
- 2026-07-27: **conflict on "does ConsoleWindow subscribe to Unity's log callback".** Previous claim (line 26): "Stationeers' `ConsoleWindow` does not subscribe to `Application.logMessageReceived`", sourced to `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs:206094-206957` (a decompile folder no longer on disk). New finding: it subscribes to `Application.logMessageReceivedThreaded` and re-prints `LogType.Error` and `LogType.Exception`. Fresh validator verdict: **B is correct**, quoting `_Init` at `:221924-221928` and the `LogMessage` handler at `:222266-222282`; the non-threaded event is never subscribed anywhere in the assembly, which is why the old claim was literally true about `logMessageReceived` while being substantively wrong about the outcome. The validator also determined the claim was **wrong when written rather than changed between versions** (high confidence): `GameLoggingSinks.md:41`, written 2026-06-24 against the same 0.2.6228.27061 decompile, already documented the subscription at `ConsoleWindow._Init line 206182`, and its several independent line citations reconstruct the old `_Init` with internal offsets (+7, +9) that match today's exactly, so the old `_Init` was structurally identical. Result: the summary table row and the "Key gotcha" paragraph rewritten to state that `Debug.LogError` / `LogException` DO reach the console; the recommended `PlayerError` helper no longer pairs `Debug.LogError` with `PrintError`. That pairing was the direct cause of a double-print defect in `Mods/NetworkPuristPlus/NetworkPuristPlus/Plugin.cs`, which this page had taught.
- 2026-07-27: **conflict on "does a blanket `using Assets.Scripts;` risk CS0104 against a mod's own `Settings`".** Previous claim (lines 47 and 90): yes, because `Assets.Scripts` contains `Settings : UserInterfaceBase`. New finding: no such type exists in `Assets.Scripts`; it is `Assets.Scripts.Serialization.Settings`, and enclosing-namespace resolution would win anyway. Fresh validator verdict: **B is correct**, quoting `namespace Assets.Scripts.Serialization` at `:264409` with `public class Settings : UserInterfaceBase` at `:265338`; exactly one `Settings` exists in the whole reference closure of both mod projects, and CS0104 requires two candidates. The validator noted the old page refutes itself: line 90 places `Assets.Scripts` as closed by ~207612 in the old dump while the frontmatter placed `Settings` at 248232, some 40,000 lines later. The originally observed compile failure was almost certainly `CS0246` from the `using Util.Commands;` attempt recorded in the same history entry, not `CS0104`. Result: the CS0104 rationale removed from the "Recommended mod pattern" section and replaced by a dedicated section stating the correct facts; the alias recommendation kept on other grounds. The frontmatter source entry for `Settings` was corrected from `Assets.Scripts.Settings` to `Assets.Scripts.Serialization.Settings`.
- 2026-07-27: re-verified and restamped against 0.2.6403.27689. All cited line numbers from the 0.2.6228.27061 era had drifted and no longer resolved (the old decompile folder is gone). The API detail that had accumulated on this page moved to the new [ConsoleWindow](../GameClasses/ConsoleWindow.md) game-class page, with this page keeping the mod-facing guidance. Added, all newly verified: the inverted `aged` semantics, `PrintError`'s default stack-trace dump, the absence of `PrintWarning`, the main-thread-only constraint, the absence of any rate limiting, the ImGui / no-rich-text constraint and the dedicated-server `<color=` drop, and the process-local (non-networked) nature of the console. Added the self-limiting patterns section citing the three in-repo implementations.
- 2026-05-11: page created after a NetworkPuristPlus user reported "I see nothing in the player log" despite the mod's `Logger.LogInfo` lines being present in both `LogOutput.log` and `Player.log` -- they were looking at the in-game console, which neither `Logger.Log*` nor `Debug.Log` reaches. `ConsoleWindow.PrintAction` / `PrintError` signatures lifted from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` (around line 206094, 206824-206957). Namespace of `ConsoleWindow` could not be pinned from the dump at the time (the `awk` "last `namespace` before the class" heuristic said `Assets.Scripts`; a brace-depth tracker said global -- unreliable because decompiled `$"..."` interpolation throws off naive `{ }` counting; a direct `using Util.Commands;` failed to compile) -- the page recommended the reflection-based resolver and listed the namespace as an open question.
- 2026-05-12: namespace resolved to `Assets.Scripts`. Confirmed two ways: (1) in `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` the `public static class ConsoleWindow` at ~206094 sits inside the `namespace Assets.Scripts` block opened at ~184063 with no intervening `namespace` declaration before the next sibling `namespace Assets.Scripts.Weather` at ~207612; (2) `EquipmentPlus` compiles cleanly with `using Assets.Scripts;` + a bare `ConsoleWindow.PrintError(...)` (`Mods/EquipmentPlus/EquipmentPlus/HelmetBeamPatches.cs:123`, `ScrollDispatchPatches.cs:271`; built `EquipmentPlus.dll` present). The earlier "a direct `using` failed to compile" was specifically `using Assets.Scripts;` colliding with a same-named `Settings` class in the caller (`NetworkPuristPlus.Settings` vs `Assets.Scripts.Settings`), not a `ConsoleWindow`-name problem -- the fix is a `using ConsoleWindow = Assets.Scripts.ConsoleWindow;` alias. `ConsoleWindow API` and `Recommended mod pattern` sections rewritten accordingly; `Util.Commands.ConsoleWindow` removed from the suggested reflection chain (it never matched). No fresh-validator pass needed: the contradicted claim ("namespace unknown / a `using` fails") is overturned by a strictly stronger source (the decompiled `namespace` block plus a compiling counter-example mod), with no ambiguity to resolve. (The `Settings`-collision half of this entry was itself overturned on 2026-07-27; see above.)

## Open questions

None.
