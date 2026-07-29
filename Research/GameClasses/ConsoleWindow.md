---
title: ConsoleWindow
type: GameClasses
created_in: 0.2.6403.27689
verified_in: 0.2.6403.27689
verified_at: 2026-07-29
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.ConsoleWindow
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs:221811-223383 (ConsoleWindow), :221505-221600 (ConsoleLine), :203899 / :203949 / :205014 (GameManager), :110054-110066 (RocketSystemConsole), :43997 / :44065 / :44604 (KeyMap.ToggleConsole)
related:
  - ../Patterns/InGameConsoleOutput.md
  - ../Patterns/GameLoggingSinks.md
  - ../GameSystems/ChatBroadcast.md
tags: [ui, chat, threading]
---

# ConsoleWindow

`public static class Assets.Scripts.ConsoleWindow` (`Assembly-CSharp.dll`, declaration at line 221811). The in-game console: the panel `KeyMap.ToggleConsole` (F3 by default) opens, whose recent lines also render bottom-left while it is closed.

This page is the API and semantics reference. For guidance on which sink a mod should use and the traps that produce real bugs, see [InGameConsoleOutput](../Patterns/InGameConsoleOutput.md).

The class sits inside `namespace Assets.Scripts`, opened at line 195722; the next sibling namespace `Assets.Scripts.Weather` does not open until 223978. There is exactly one `ConsoleWindow` type in `Assembly-CSharp` and none in any other shipped DLL.

## Print methods
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

```csharp
public static void PrintError(System.Exception exception);                                  // 222926, async UniTaskVoid
public static void PrintError(string output, bool suppressStacktrace = false);              // 222936
public static void AsyncPrintError(string output, bool suppressStacktrace = false);         // 222945, async UniTaskVoid
public static void PrintAction(string output, bool aged = false);                           // 222958
public static void Print(string output, ConsoleColor color = ConsoleColor.White, bool clearLine = false, bool aged = true, bool unformatted = false);   // 222963
public static void PrintBlock(params (string text, ConsoleColor color)[] lines);            // 223030
public static void PrintSegmentedBlock(params (string text, ConsoleColor color)[][] lines); // 223072
public static void PrintTable(string[] headers, IReadOnlyList<(string text, uint color)[]> rows, int columnGap = 2);   // 223134
public static void PrintSegmentedBlockRaw(params (string text, uint color)[][] lines);      // 223203
public static void Print(GameString output);                                                // 223265
public static void Print(GameString output, uint color);                                    // 223270
public static void Print(GameString output, string value);                                  // 223275
public static void Print(GameString output, string value, uint color);                      // 223280
public static void Print(GameString output, string value1, string value2);                  // 223285
public static void Print(GameString output, string value1, string value2, uint color);      // 223290
public static void Print(GameString output, string value1, string value2, string value3, uint color);   // 223295
public static void DrawListables(IEnumerable<IListable> enumerable1, IEnumerable<IListable> enumerable2 = null);        // 223300
```

**There is no `PrintWarning`.** A grep for `PrintWarning` across the whole 13.4 MB decompile returns no matches, on this class or anywhere in the assembly. Yellow is `PrintAction`.

`PrintAction` (222958-222961), verbatim:

```csharp
public static void PrintAction(string output, bool aged = false)
{
	Print(output, ConsoleColor.Yellow, clearLine: false, aged);
}
```

`ConsoleColor.Yellow` maps to the `"!"` glyph in `GetLevelGlyph` (221727-221731).

`PrintError` (222936-222943), verbatim:

```csharp
public static void PrintError(string output, bool suppressStacktrace = false)
{
	Print(output, ConsoleColor.Red, clearLine: false, aged: false);
	if (!suppressStacktrace)
	{
		Print(Environment.StackTrace, ConsoleColor.Gray);
	}
}
```

The stack trace is a **second console line**, Gray, with `aged` left at its default `true` (so it is overlay-hidden and only visible with the console open). `Environment.StackTrace` is multi-line, and `ConsoleLine.Set` (221532-221545) splits on `\n` into `Continuations`, so it consumes **one** ring-buffer slot rather than N.

`AsyncPrintError` differs subtly: its stack-trace print omits `ConsoleColor.Gray`, so that line is White.

```csharp
public static async UniTaskVoid AsyncPrintError(string output, bool suppressStacktrace = false)
...
			Print(Environment.StackTrace);                                   // 222954
```

Non-obvious behaviour of the block helpers: `PrintBlock` / `PrintSegmentedBlock` / `PrintSegmentedBlockRaw` all hard-code activeTime `0f` (223056, 223102, 223233), so they are always overlay-hidden and offer no `aged` parameter, and each consumes exactly one ring-buffer slot regardless of line count. `PrintBlock` and `PrintSegmentedBlock` skip their whole body when `GameManager.IsBatchMode` (223036), so **they produce no dedicated-server output at all**; only the plain `Print` has a batch-mode sink. All six `GameString` overloads funnel into `Print(string, ConsoleColor)` and therefore inherit `aged: true`.

## `aged` is inverted from its name
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

`aged: true` means the line is NOT shown on the closed-console overlay. `aged: false` is what puts a line in front of a player who has not opened the console. Plain `Print` defaults to `aged: true`.

The setter (223011-223018), verbatim:

```csharp
		if (aged)
		{
			_consoleBuffer[0].Set(output, consoleColor, 0f);
		}
		else
		{
			_consoleBuffer[0].Set(output, consoleColor);
		}
```

`Set`'s default is `float activeTime = 5f` (221523), so `aged: false` gives activeTime 5 and `aged: true` gives activeTime 0.

The visibility gate, `ConsoleLine.Draw` (221570-221575), verbatim:

```csharp
	public void Draw(ref Vector2 inputSize, bool isShown, bool noFade = false)
	{
		if (string.IsNullOrEmpty(Text) || (!isShown && _activeTime <= 0f))
		{
			return;
		}
```

With the console closed, `isShown` is false, so a line with `_activeTime <= 0f` draws nothing. The closed-console overlay draw loop is at 222429-222435:

```csharp
		else if (num2 >= 0)
		{
			for (int num4 = (_useCustomWindowHeight ? Mathf.Clamp(_customWindowHeight, 0, num2) : num2); num4 >= 0; num4--)
			{
				_consoleBuffer[num4].Draw(ref _inputSize, _show, noFade);
			}
		}
```

Overlay lifetime is 5 seconds, decremented per frame at 221638-221641, with an alpha fade over the last second (221577-221582).

## The Unity log bridge: Debug.LogError reaches the player's console
<!-- verified: 0.2.6403.27689 @ 2026-07-29 -->

`ConsoleWindow` subscribes to `Application.logMessageReceivedThreaded` and re-prints error-level Unity log output into the console itself. Subscription in `_Init` (221924-221928) and teardown (222261-222264), verbatim:

```csharp
	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
	private static void _Init()
	{
		Application.logMessageReceivedThreaded += LogMessage;
		Application.quitting += Shutdown;
```

```csharp
	private static void Shutdown()
	{
		Application.logMessageReceivedThreaded -= LogMessage;
	}
```

Handler (222266-222282), verbatim:

```csharp
	private static void LogMessage(string logStr, string stacktrace, LogType type)
	{
		if (CustomLogFile)
		{
			return;
		}
		string text = EnumCollections.LogTypes.GetName(type).ToUpper();
		string text2 = logStr.ToLower();
		if (type == LogType.Error || type == LogType.Exception)
		{
			Print("[" + text + "] " + text2, ConsoleColor.Red, clearLine: false, aged: false);
			if (!string.IsNullOrEmpty(stacktrace))
			{
				Print(stacktrace);
			}
		}
	}
```

Consequences:

- Printed: `LogType.Error` and `LogType.Exception` only. So `Debug.LogError`, `Debug.LogException`, and Unity's auto-logged unhandled exceptions all appear in the player's console.
- Ignored: `LogType.Log` (`Debug.Log`), `LogType.Warning` (`Debug.LogWarning`), and `LogType.Assert` (`Debug.LogAssertion`).
- Only the message line is red. The stack trace goes through `Print(stacktrace)` with all defaults, so it is White and `aged: true` (overlay-hidden).
- The message is lowercased (`logStr.ToLower()`); the level tag is uppercased and bracketed. `EnumCollections.LogTypes` is built with `toProper: false` (203493), so `Names = Enum.GetNames(typeof(T1))` is left untransformed (203609, the `ToProper()` rewrite at 203618 sitting inside `if (toProper)`) and the tag is the raw enum name upper-cased: `[ERROR]` for `LogType.Error`, `[EXCEPTION]` for `LogType.Exception`. `GetName` is called with `padded` defaulting to `false`, so the space-padded `PaddedNames` array is not used; `padded: true` would have rendered `[ERROR    ]` against the 9-character `Exception`.
- The whole handler is a no-op when `CustomLogFile` is true (`CommandLineArgs?.Contains("-logFile") ?? false`, line 221915). `CommandLineArgs` is populated only inside the `IsBatchMode` branch of `_Init` (221929-221931), so on a normal client it is null and `CustomLogFile` is false. The guard exists to break a recursion loop, because `Print` under batch + `CustomLogFile` itself calls `UnityEngine.Debug.LogError/LogWarning/Log`.
- The non-threaded `Application.logMessageReceived` is **never** subscribed anywhere in the assembly. A whole-file grep for `logMessageReceived` returns exactly two hits, both `logMessageReceivedThreaded` (221927, 222263).

For a mod this means pairing `Debug.LogError` with a `ConsoleWindow` call for the same message shows it to the player twice.

## Batch mode, the `<color=` drop, and premature prints
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

`Print` head (222963-222992), verbatim:

```csharp
public static void Print(string output, ConsoleColor color = ConsoleColor.White, bool clearLine = false, bool aged = true, bool unformatted = false)
{
	if (GameManager.IsBatchMode)
	{
		output = $"{DateTime.Now:HH:mm:ss}: {output}";
		if (CustomLogFile)
		{
			switch (color)
			{
			case ConsoleColor.DarkRed:
			case ConsoleColor.DarkMagenta:
			case ConsoleColor.Red:
			case ConsoleColor.Magenta:
				UnityEngine.Debug.LogError(output);
				break;
			case ConsoleColor.DarkYellow:
			case ConsoleColor.Yellow:
				UnityEngine.Debug.LogWarning(output);
				break;
			default:
				UnityEngine.Debug.Log(output);
				break;
			}
		}
		else if (!output.Contains("<color="))
		{
			_systemConsoleInput?.PrintToConsole(output, color);
		}
		return;
	}
```

The `<color=` discard at 222987 is narrower than "on a dedicated server": it applies only when batch mode is on **and** `-logFile` is absent. With `-logFile`, the line is routed to `UnityEngine.Debug.Log/LogWarning/Log` by colour band with the markup intact and no filtering. `_systemConsoleInput` is also null-conditional, so a line can be dropped even without the `<color=` test if the system console was never created.

The `return` at 222991 means the batch path never touches `_consoleBuffer`, ImGui, or Unity UI, which is what makes `Print` safe to call on a headless dedicated server. `RocketSystemConsole.PrintToConsole` (110054) is plain `System.Console.WriteLine` with colour and has its own pre-ready queue (110056-110060).

Early prints queue and replay (222994-223006, 223021-223028), verbatim:

```csharp
	ConsoleLine[] consoleBuffer = _consoleBuffer;
	if (consoleBuffer == null || consoleBuffer.Length <= 0)
	{
		_prematureLogQueue.Enqueue(new PrematureLog
		{
			output = output,
			color = color,
			clearLine = clearLine,
			aged = aged,
			unformatted = unformatted
		});
		return;
	}
```

```csharp
private static void DrainPrematureLogQueue()
{
	while (_prematureLogQueue.Count > 0)
	{
		PrematureLog prematureLog = _prematureLogQueue.Dequeue();
		Print(prematureLog.output, prematureLog.color, prematureLog.clearLine, prematureLog.aged, prematureLog.unformatted);
	}
}
```

`_consoleBuffer` starts as `Array.Empty<ConsoleLine>()` (221843). The queue is drained from `Initialize()` (221951), unblocked in `GameManager.Awake` (205014-205019), where `ApplySettings()` must run before `Initialize()` because the former allocates the 1024-element array (222799-222811) and the latter only writes into it (221945-221948). Replayed lines carry the **drain** timestamp, not the original: `ConsoleLine.Set` stamps `DateTime.Now` at 221547. The queue is unbounded and unsynchronised.

**`clearLine` and `unformatted` are dead parameters on the non-batch path.** They are read only to populate the premature-log struct (223001, 223003) and are never consulted when the line is actually written (223007-223018).

Neither has anywhere to go even in principle. `ConsoleLine` carries no matching field (221507-221519) and the sink signature is `public void Set(string text, uint color, float activeTime = 5f, uint[] continuationColors = null)` (221523), which has no slot for either. The `unformatted: true` arguments in the draw path (221698, 221709) are hard-coded literals on the closed-overlay branch, not fed from `Print`. `clearLine` does not overwrite the previous line either: the shift loop at 223007-223010 is unconditional and `Set` always writes slot 0 after it, so there is no branch that could produce overwrite semantics. Two similarly named things are unrelated: `_clearConsoleInput` (221841, consumed at 222436-222440) clears the input textbox, and `RocketSystemConsole.ClearLine()` is called unconditionally once per output line inside `PrintToConsole` (110065), followed by `RedrawInputLine()` (110069) to repaint the terminal prompt.

Once the console UI is up, the premature branch is skipped outright, so on a live path the two parameters are not read at all, not even into the queue.

## Rendering: ImGui, not TextMeshPro
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

`DrawTextWrapped` (221704-221717), verbatim tail:

```csharp
		else
		{
			ImGui.PushStyleColor(ImGuiCol.Text, color);
			ImGui.TextUnformatted(text);
			ImGui.PopStyleColor();
		}
```

A second `TextUnformatted` call site handles segmented rows at 221693, and the closed-overlay path goes through `ImguiHelper.TextShadow(text, color, TextAlignment.Left, unformatted: true)` (221709). All three are unformatted, so TextMeshPro rich-text tags render as literal characters in both the open and closed states. Colour is per line, via the `ConsoleColor` argument only.

## Threading and cost
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

Every print shifts the ring buffer with no synchronisation (223007-223010), verbatim:

```csharp
		for (int num = _consoleBuffer.Length - 1 - 1; num >= 0; num--)
		{
			_consoleBuffer[num + 1].Apply(_consoleBuffer[num]);
		}
```

There is no `lock`, `Interlocked`, or `Monitor` anywhere in the class body (221811-223383), while the draw loop reads the same array at 222364-222367 / 222429-222435.

The game marshals to the main thread before printing from its async paths. `PrintError(System.Exception)` (222926-222934), verbatim:

```csharp
public static async UniTaskVoid PrintError(System.Exception exception)
{
	if (GameManager.IsThread)
	{
		await UniTask.SwitchToMainThread();
	}
	PrintError("Exception: " + exception.Message);
	Print(exception.StackTrace);
}
```

Same pattern in `AsyncPrintError` at 222947-222950. `GameManager.IsThread` is `public static bool IsThread => MainThreadId != Thread.CurrentThread.ManagedThreadId;` (203949).

Caveat worth knowing: the `LogMessage` handler above is wired to `logMessageReceivedThreaded`, which Unity raises on whichever thread logged, and it calls `Print` directly with no `IsThread` / `SwitchToMainThread` guard. So the game itself violates the main-thread convention on the log bridge. That is not a licence for a mod to print off-thread; it is a known race in the game.

Cost and capacity:

```
221885		private static int DEFAULT_CONSOLE_BUFFER_SIZE = 1024;
222799			ConsoleLine[] array = new ConsoleLine[1024];
```

`ApplySettings` (222797) hard-codes 1024 and does not read `DEFAULT_CONSOLE_BUFFER_SIZE`. Every `Print` runs the full 1023-iteration shift unconditionally: no throttle, no dedupe, no same-message collapse, no early-out. `ConsoleLine.Apply` (221791-221800) copies seven fields per element, so one print is roughly 1023 x 7 field copies. `PrintBlock` (223041), `PrintSegmentedBlock` (223083), and `PrintSegmentedBlockRaw` (223214) each run the shift once for the whole block, so they are the cheap way to emit N lines. **Rate limiting is entirely the caller's job.**

## Lifetime: the buffer is never reset on world load
<!-- verified: 0.2.6403.27689 @ 2026-07-29 -->

The ring buffer is process-lifetime state. Nothing clears it on world load, world unload, or a return to the main menu.

`ClearConsole()` (222815) has exactly one caller in the entire assembly, `ClearCommand.Execute` (96611-96623):

```csharp
public class ClearCommand : CommandBase
{
	public override string HelpText => "Clears all text from the console buffer.";
...
	public override string Execute(string[] args)
	{
		ConsoleWindow.ClearConsole();
		return null;
	}
}
```

That is the player-typed `clear` command. `ApplySettings()` and `Initialize()` share the single call site `GameManager.Awake` (205018-205019), and `Initialize()` early-outs on `if (!IsInitialised)` (221943), so a second `Awake` is a no-op. No `SceneManager`, `sceneLoaded`, `OnWorldLoad`, or `OnFinishedLoad` reference exists anywhere in the class body.

`ApplySettings` preserves content when it reallocates (222800-222809), so even a repeat call would not wipe the buffer:

```csharp
			if (i >= _consoleBuffer.Length)
			{
				array[i] = new ConsoleLine();
			}
			else
			{
				array[i] = _consoleBuffer[i];
			}
```

For a mod this means the 1024-line ring is the only bound on a long session, and lines from a previous world are still in scroll-back after loading a new one. It also means a mod's own per-session print budget does not reset just because the player loaded a different save; if a cap should reset at a world boundary, the mod has to do that itself.

## Process-local, not networked
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

Every write path in `Print` terminates in one of three local sinks: `UnityEngine.Debug.Log*` (222976 / 222980 / 222983), `_systemConsoleInput?.PrintToConsole` (222989, which is `System.Console.WriteLine` at 110066), or `_consoleBuffer[0].Set` (223013 / 223017). No `NetworkServer`, `NetworkManager.Send*`, `MessageBase`, or `SendToClient*` reference exists in the print path.

**A `ConsoleWindow.Print` on the server is invisible to clients.** The only networking references in the class are the `ConnectTo` / host commands (222731-222779), which are command implementations rather than output plumbing. The only "console message" type in the assembly is `ShowHideConsoleWindowMessage : EventBase` (191577), an internal event-bus message, not a network message.

To put a line on one specific client, a mod must send its own message and print locally in `Process()`. See [ChatBroadcast](../GameSystems/ChatBroadcast.md) for the replicating chat channel and the `NetworkServer.SendToClient<T>` unicast form.

## Other public members
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

| Line | Signature |
|---|---|
| 221865 | `public const ConsoleColor DEFAULT_COLOR = ConsoleColor.White;` |
| 221867 | `public static readonly uint DefaultColor` (RGBA 0.7/0.7/0.7/1) |
| 221877 | `public static int MaxLength = 128;` (recomputed each frame at 222643 from ImGui content width; not a truncation limit on `Print`) |
| 221879 | `public static ConsoleCommandScope[] AllCommandScopes` |
| 221881 | `public static string[] ConsoleCommandScopeStrings` |
| 221891 | `public static bool IsInitialised { get; private set; }` |
| 221893 | `public static ConsoleLine[] ConsoleBuffer => _consoleBuffer;` (direct mutable reference to the live ring buffer) |
| 221895 | `public static bool IsOpen => _show;` |
| 221897 | `public static int CommandBufferIndex { get; private set; }` |
| 221919 | `public static void FlashCopyToast()` |
| 221941 | `public static void Initialize()` |
| 222284 | `public static void UseCustomWindowHeight(bool useCustom, int height = 0)` |
| 222290 | `public static void Show()` |
| 222306 | `public static void Hide()` |
| 222325 | `public static void Draw(bool noFade = false)` |
| 222487 | `public static void Submit(string input)` |
| 222544 | `public static bool IsInvalidSyntax(string[] lineSplit, int requiredSize, string[] uses = null)` |
| 222751 | `public static void ConnectTo(string[] lineSplit)` |
| 222797 | `public static void ApplySettings()` (reallocates the 1024 buffer and calls `GC.Collect()`) |
| 222815 | `public static void ClearConsole()` |

## Opening the console
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

```
43997:		KeyMap.ToggleConsole = KeyCode.F3;
44065:		KeyMap._ToggleConsole.AssignKey(KeyCode.F3);
44604:	public static KeyCode ToggleConsole;
```

F3 is the **default**, not a fixed binding: it is registered as `AddKey("ToggleConsole", KeyMap.ToggleConsole, controlsGroup2)` (44149) and re-read from saved settings at 44266 / 44332. Consumed at 222449 via `Input.GetKeyDown(KeyMap.ToggleConsole)`. Documentation aimed at players should say "the console key (F3 by default)" rather than assuming F3.

## Verification history

- 2026-07-29: re-read while reviewing the console-output fix commits. Three additions and two corrections, all against the same 0.2.6403.27689 decompile. Added: the "Lifetime" section (the ring buffer is never reset on world load; `ClearConsole()` has exactly one caller, the player-typed `clear` command at 96611-96623, and `ApplySettings` preserves content when it reallocates); the derivation behind the log bridge's bracketed tag, which resolved a conflict against [InGameConsoleOutput](../Patterns/InGameConsoleOutput.md) under the Rule 3 protocol (that page had generalised the tag to `[ERROR]`; `LogType.Exception` renders `[EXCEPTION]`, and the conflict record lives there); and the evidence that `clearLine` and `unformatted` have nowhere to go even in principle, since `ConsoleLine` has no matching field and `Set`'s signature has no slot for either. Corrected: the class body ends at 223383, not 223400 (the next top-level type, `TerrainDebugHelper`, declares at 223384), in the frontmatter source range and in the "Threading and cost" negative-probe sentence. The negative probe itself was re-run over the corrected range and still finds no `lock`, `Interlocked`, `Monitor`, `volatile`, `[ThreadStatic]`, or concurrent collection, so that claim is re-confirmed rather than changed.
- 2026-07-27: page created during a repo-wide audit of mod console usage against the 0.2.6403.27689 decompile. Split out of [InGameConsoleOutput](../Patterns/InGameConsoleOutput.md), which had grown a partial and partly incorrect API section while being a mod-guidance page; that page now carries the guidance and this one carries the class reference. All sections verified directly against `.work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs` with verbatim excerpts. Findings that contradicted the older page (the `logMessageReceivedThreaded` subscription, the inverted `aged` parameter, `PrintError`'s default stack-trace dump, the absence of `PrintWarning`) were resolved under the Rule 3 fresh-validator protocol; the conflict record lives on the InGameConsoleOutput page.

## Open questions

None.
