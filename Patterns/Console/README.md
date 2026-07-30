# Patterns/Console

`PlayerMessage.cs` is the single entry point for everything a mod says to a player or writes to a log. Link it into a mod and use nothing else.

```xml
<Compile Include="..\..\..\Patterns\Console\PlayerMessage.cs" Link="Patterns\PlayerMessage.cs" />
```

```csharp
using StationeersPlus.Shared;

// once, first thing in Awake
PlayerMessage.Init("Power Grid Plus", Logger);
```

The display name goes in with spaces, as a player reads it. The helper turns it into the bracketed prefix on every line, so call sites never write a prefix themselves.

## Why this exists

The rules for the in-game console are counter-intuitive, and this repo has shipped the same defects more than once because each mod re-derived them by hand. The evidence is on the record: two mods needed the identical double-print fix in two separate commits on the same day, the research page that taught the pattern had the bug in it, and Spray Paint Plus contained a file whose own header warned "never pair `Debug.LogError` with a `ConsoleWindow` call" while another file in the same mod did exactly that, forever, in front of 513 subscribers.

Writing the rules down demonstrably did not work. So they are encoded here instead, where a call site cannot get them wrong.

## The rules it encodes

- **`aged` is inverted from its name.** `aged: true` sets activeTime 0, so the line is NOT drawn on the closed-console overlay and appears only once F3 is opened. Plain `Print` defaults to `aged: true`, so anything meant to be seen without opening the console needs `aged: false`.
- **There is no `PrintWarning`.** Yellow is `PrintAction`. Info and warning therefore look identical in the console; the severity survives only in the log files. If a player needs to see the difference, say so in the words.
- **`PrintError` dumps a stack trace** as a second line unless passed `suppressStacktrace: true`. On an ordinary "you cannot do that" message that reads as a mod crash.
- **Never call `UnityEngine.Debug.LogError` or `LogException`.** `ConsoleWindow` subscribes to `Application.logMessageReceivedThreaded` and re-prints Error and Exception itself, lowercased, with an unsuppressible stack trace. Pairing it with a console call shows the player the same thing two or three times. This file calls no `Debug.*` method at all, which is what makes that whole class of bug unreachable.
- **The BepInEx log is the useful sink, not `Debug`.** One `ManualLogSource` call reaches BOTH `BepInEx\LogOutput.log` (via `DiskLogListener`) AND `Player.log` (via BepInEx's `UnityLogListener`, which writes through Unity's native log writer and so never re-enters the console bridge). `Debug.Log*` reaches only `Player.log`, because `[Logging.Disk] WriteUnityLog` defaults to false.
- **Plain text only.** The console renders through ImGui `TextUnformatted`, so TextMeshPro tags show as literal characters, and a dedicated server launched without `-logFile` silently drops any line containing `<color=`.
- **Main thread only.** `Print` shifts an unlocked 1024-entry static array while the draw loop reads it. Calls from a worker have their console leg dropped here rather than racing the renderer; the log legs still run, so nothing is lost from the files.

**Read that last point carefully: the helper DROPS, it does not DEFER.** It has no dispatcher and cannot queue anything. A call from a worker thread is silently absent from the player's console, and only the log files record it. So if a message originates off the main thread and the player is supposed to see it, **the caller still has to marshal**. `Mods/PowerGridPlus/PowerGridPlus/DeviceOutputSanitizer.cs` is the worked example: it reports from the power worker, so it keeps its `UnityMainThreadDispatcher` hop and calls the helper from inside the marshalled action. Deleting that hop on the assumption the helper handles it would remove the entire player-visible half of that feature while leaving the logs looking healthy. The helper's guard is a safety net against racing the renderer, not a substitute for marshalling.

Full background: [Research/Patterns/InGameConsoleOutput.md](../../Research/Patterns/InGameConsoleOutput.md), [Research/Patterns/GameLoggingSinks.md](../../Research/Patterns/GameLoggingSinks.md), [Research/GameClasses/ConsoleWindow.md](../../Research/GameClasses/ConsoleWindow.md).

## Every call needs a key and a throttle

There is no un-throttled overload and no default policy. That is deliberate friction: the console has no rate limiting of its own and every print costs a full 1024-entry array shift, so self-limiting is always the caller's job, and the only person who knows what "the same message" means is the caller.

```csharp
PlayerMessage.Info ("helmet-no-power",        Throttle.Cooldown(5f),      "Helmet has no power.");
PlayerMessage.Error($"broken-device-{refId}", Throttle.Once,              $"Broken device: \"{name}\"");
PlayerMessage.Warn ($"paint-blocked-{fn}",    Throttle.MaxTimes(3),       "...");
PlayerMessage.Info ("wreckage-sweep",         Throttle.CapWithSummary(6), "...");
PlayerMessage.Broadcast($"tier-burn-{netId}", Throttle.Never,             "burned normal cable ...");
```

**Choosing the key is the real decision.** Key on the message text and you get one line total. Key on a device reference id and you get one line per device. Key on a logical function name and you get one line per kind of blocked action. Getting this wrong is how a "print once" turns into either silence or a flood.

| Policy | Behaviour | Use it for |
|---|---|---|
| `Throttle.Never` | every time | A line answering one deliberate player action, or one a test asserts exactly-once. Not a bypass: it is a policy you have to type. |
| `Throttle.Once` | first occurrence per key | A specific subject that stays broken. Key on the subject so each one is named once. |
| `Throttle.Cooldown(sec)` | min gap per key | Input paths. One mouse-wheel flick is 10-20 notches and every notch can reach the same branch. |
| `Throttle.MaxTimes(n)` | hard cap per key | A blocked action the player keeps attempting. Say it enough to be understood, then stop nagging. |
| `Throttle.CapWithSummary(n)` | cap, then "N more not shown" | A loop over world content, where the count is the part a player can act on. |

`CapWithSummary` needs `PlayerMessage.FlushSummary(key)` when the sweep ends. `ResetSession()` also flushes anything still pending, so a forgotten flush delays the line to the next world boundary rather than losing it.

Call `PlayerMessage.ResetSession()` on world load and on rejoin, so a player entering a new world is told about that world's problems.

## Severity and where each line goes

| Call | F3 console | `LogOutput.log` + `Player.log` | Networked | Boot log |
|---|---|---|---|---|
| `Info` | yellow, overlay-visible | `LogInfo` | no | while at boot or the menu |
| `Warn` | yellow, overlay-visible | `LogWarning` | no | while at boot or the menu |
| `Error` | red, no stack trace | `LogError` | no | while at boot or the menu |
| `Error(..., Exception)` | type and message only | full exception | no | while at boot or the menu |
| `Broadcast` | via the chat channel | `LogInfo` | **yes** | while at boot or the menu |

The StationeersLaunchPad boot log is mirrored automatically while `GameManager.GameState` is `None`, which covers boot and the main menu, and is where its ImGui panel is actually drawn. It is resolved by reflection and silently does nothing when StationeersLaunchPad is absent or did not load the mod. A compile-time reference would turn "not installed" into a `TypeLoadException` at the JIT of whichever method mentions the type, so it is never referenced directly.

## Broadcast is not like the others

`Broadcast` is the only method that leaves the machine. It sends a vanilla `ChatMessage` with `HumanId = -1`, which prints locally and replicates to every client. Use it for events the whole server needs to know about: enforcement actions, things the server did to a player's base. A plain `ConsoleWindow` print on a server is invisible to clients.

It carries **no severity**, because `ChatMessage` has no colour field. Everything arrives looking the same, so put any urgency in the words. It is server-authoritative and main-thread only.

`PlayerMessage.LastBroadcast` is a test seam that ScenarioRunner reads by reflection, asserting exact substrings. Changing the wording of a broadcast breaks those assertions.

## Per-mod copy

This file is linked into each mod with `<Compile Include>`, not shared at runtime, so every consuming assembly gets its own copy of `StationeersPlus.Shared.PlayerMessage` with its own statics. Consequences:

- `Init` must be called once per mod.
- Throttle state is per-mod. It bounds what one mod contributes to the console, never the aggregate across mods. Six mods each honouring "3 per key" can still put 18 lines into the one shared 1024-line ring.
- A "print once" is once per mod that links the file, not once globally.

Genuine cross-mod coordination would need a separately distributed assembly and an inter-process channel, which is a different packaging decision. See [Research/Patterns/ILRepackPerModCopy.md](../../Research/Patterns/ILRepackPerModCopy.md).

## What deliberately stays in the mods

Not everything belongs here, and pulling more in would make the shared file worse:

- **Throttle policy keys.** The helper supplies the mechanism; the mod picks the key, because the key is domain knowledge.
- **Spray Paint Plus's `Functions` table and `LogEffectiveSettings`.** Those strings are simultaneously settings-panel entry names, throttle keys, and a network payload, and around 30 literals are asserted by a test fixture.
- **Anything with its own transport.** If a mod needs a popup, a chat identity, or a bespoke network message, that is its own concern.
