# ClientRig

Durable internals for the client rig: the ClientDriver plugin and the `client-rig.ps1` launcher. Everything here was verified at runtime on game 0.2.6403.27689, Unity 2022.3.62f3, StationeersLaunchPad 0.5.0, BepInEx 5.4.23.5.

`README.md` is the operating manual. This file is why the code is shaped the way it is.

## The plugin / launcher boundary

The dividing line is **process creation**.

The launcher owns everything outside a game process, and everything that must keep working when a process is dead, wedged, or not yet born. The plugin owns everything inside a process, which is everything that needs the Unity main thread or the game's own types. Nothing sits in both.

That boundary is not a filing convention; it falls out of two hard constraints.

**A coordinator cannot live inside the thing it coordinates.** Every multi-client test is "set up A, set up B, act as A, observe both", and the failures worth catching are ordering failures. An instance that has stopped participating in a barrier cannot report that it has stopped participating. The launcher is outside all of them, holds their PIDs, and can distinguish "not answering" from "answering, but not there yet". So `-Wait`, `-Broadcast` and `-Snapshot` are launcher actions, and the plugin has no notion of a sibling except the one narrow case below.

**The one exception proves the rule.** `PeerProbe` inside the plugin does talk to siblings, for exactly one purpose: detecting that another instance is claiming the same ClientId. That check has to be inside, because the moment it must fire is the join, and the join is initiated from inside. `/connect` refuses on a detected conflict; nothing else in the plugin cares about siblings.

**Configuration flows one way.** The launcher writes `data/<instance>/instance.json`; the plugin reads it at `Awake`. One writer, one reader. The manifest wins over the BepInEx config because the manifest is rewritten on every provision and therefore describes this run, whereas a `.cfg` is sticky across sessions: an instance was once observed booting with a setting left behind by the previous session, with nothing indicating it. `/instance` reports `valueSources` so the winner is never a guess.

---

## The cursor gate

This is the single most expensive thing in the rig's history: it cost a session, and it produced a confidently wrong acceptance-test result before it was found.

### The gate

`Assets.Scripts.Inventory.InventoryManager.ManagerUpdate` opens with:

```csharp
if (Cursor.visible || Parent.IsUnresponsive || ConsoleWindow.IsOpen)
{
    return;
}
CheckDisplaySlotInput();
CheckSeatedInput();
...
switch (CurrentMode) { case Mode.Normal: NormalMode(); break; ... }
```

Everything input-driven sits below that early return:

- **`CheckDisplaySlotInput` is the only writer of `InventoryManager.newScrollData` in the entire assembly.** One assignment, `newScrollData = Input.mouseScrollDelta.y / 10f`, and no reset to zero anywhere: the once-per-frame overwrite is the reset. Gate shut, and no wheel is sampled at all, so no wheel consumer can act.
- **`NormalMode` never runs**, and that is where mods hang their per-frame gameplay hooks. A driven client therefore never reports its own client-half state to a server, and the server falls back to its permissive default.

The same `Cursor.visible` term gates movement: `KeyManager.InputState != KeyInputState.Game || Cursor.visible || ...`.

### Why an unfocused window trips it

Unity releases the cursor lock when the application loses focus. `MouseModeController.SetState` tries to re-lock every frame and cannot take it back while the window is in the background, so `Cursor.visible` stays true for the whole session.

The dependency on foreground focus is entirely second-order, through the cursor, and entirely inside managed code. Nothing in the assembly checks focus directly: `Application.isFocused` has **zero** occurrences, `Application.runInBackground` appears once inside a diagnostic print, and the single `OnApplicationFocus` only restores cursor state on regaining focus. That is why the separate desktop turned out to be unnecessary for input, and why the fix is a single line of managed state.

### The measurement, not the inference

The diagnosis is a measurement because `ChainProbe` exists. From `client1` at the main menu, before it had ever entered a world:

```
gateAsserts             1685
cursorForcedHiddenCount 1685
```

Identical: the cursor was visible on every single frame, so on every single frame the gate would have been shut. Chain counters at the same moment:

```
GameManager.Update                       enter 1692  exit 1692
KeyManager.ManagerUpdate                 enter 1685  exit 1685
KeyMap.PollInputs                        enter 1685  exit 1685
InventoryManager.ManagerUpdate           enter 1685  exit 1685
InventoryManager.CheckDisplaySlotInput   (absent)
InventoryManager.NormalMode              (absent)
```

Balanced all the way down to `ManagerUpdate`, then nothing. **Balanced-then-absent is the shape of an early return; unbalanced would have been the shape of an exception.** That distinction is what ruled out the competing explanation, which was that a throwing Harmony patch was aborting `GameManager.Update`. In world with the gate forced open the two missing links appear and keep pace: 14,774 / 14,774 on one instance, 15,001 / 15,001 on the other.

### The fix, and its scope

A prefix on `InventoryManager.ManagerUpdate` asserts the cursor state a few instructions before the gate reads it, so nothing can intervene within the frame:

```csharp
if (Cursor.visible) Cursor.visible = false;
if (Cursor.lockState != CursorLockMode.Locked) Cursor.lockState = CursorLockMode.Locked;
ForceKeyInputStateGame();
```

No window focus, no OS input, no window-state call.

The first version asserted unconditionally on every `ManagerUpdate`, which is a blunt instrument: it held the cursor hidden at the main menu and during boot, where the cursor is the only way to interact with anything. `GameplayGate` now scopes the assertion to `GameState.Running` and yields while a confirmation dialog is up. In a world, hiding the cursor is what the game itself does, so the assertion is invisible; outside one it is a fight nobody wins. `Force Gameplay Input Everywhere` restores the old behaviour for a test that drives menus through synthetic input.

`GameState` is resolved reflectively and the answer is cached per frame, so a game update that moves the enum degrades to "the gate never asserts, and `shutReason` says why" rather than to a plugin that will not load.

### Two further gates that also read as "input did nothing"

- `ConsoleWindow.IsOpen` short-circuits **every** `KeyManager.GetButton*` call for any key other than `KeyMap.ToggleConsole`, as well as appearing in the `ManagerUpdate` gate.
- `KeyWrapBindings.KeyWrapOnEvent` filters every KeyWrap-bound action on `item.inputState.HasFlag(KeyManager.InputState)`. `KeyManager.InputState` is a public getter with a private setter, defaults to `KeyInputState.Game`, and is moved only through a push/pop dictionary. A panel that pushes a state and never pops it leaves every bound action inert, `SwapHands` among them, which is why a synthetic SwapHands can be delivered and still do nothing. `ForceKeyInputStateGame` writes the state back through the private setter for exactly this case.

---

## Input layering

### Why `UnityEngine.Input` and not `KeyManager`

Every `KeyManager` query bottoms out in the Unity layer, and a great deal of game code calls `Input.GetKey(KeyMap.X)` directly, bypassing `KeyManager` entirely. There are **139 direct `Input.*` call sites** in `Assembly-CSharp`. `KeyManager.GetButton` decompiles to a console guard plus the live Unity call:

```csharp
public static bool GetButton(KeyCode key)
{
    if (key != KeyMap.ToggleConsole && ConsoleWindow.IsOpen) { return false; }
    return Input.GetKey(key);
}
```

There is also no cached key state to sit under, which was checked rather than assumed:

- `InputSystem.KeyWrap.PollForInput` calls `Input.GetKeyDown` / `GetKey` / `GetKeyUp` directly and fires its C# events synchronously from inside that stack. Its `IsPressed` and `IsPressedThisFrame` properties are written there and **read by nothing in the assembly**: the only hits for the identifier are the declaration and the three assignments.
- `KeyMap.PollInputs` writes no state at all; it iterates a `HashSet<KeyWrap>` and calls `PollForInput` on each.
- There is no modern input package. No `UnityEngine.InputSystem` references anywhere, no `Unity.InputSystem.dll` in `rocketstation_Data\Managed\`, only `UnityEngine.InputLegacyModule.dll`.

So the Unity layer is the one true chokepoint, and patching it means "Shift is held" says the same thing to every consumer.

`KeyMap` is a static class of **mutable `public static KeyCode` fields**, rebindable at runtime, not an enum. `/input/key` resolves an action name against the live field rather than a hardcoded default.

### The frame-window model

Synthetic input is expressed as an absolute `Time.frameCount` window, never as a countdown ticked from `Update`. MonoBehaviour update order is undefined, so a countdown can expire before the frame's real consumer runs. A window opened on `Time.frameCount + 1` is visible for the whole of every frame in it regardless of ordering.

**One frame of wheel is one notch.** Consumers act once per frame, so a two-frame injection scrolls two notches. `frames` defaults to 1 for that reason, and `repeat` with a gap is how to travel several steps.

### The read-back, and why it is the contract

"The driver applied the override" and "the game read the override" are different claims, and only the second is worth anything. The old shape could only report the first, so `/input/key` answered `{"ok":true,"resolvedVia":"KeyMap.SwapHands","settled":true}` for a keypress that never happened.

`VirtualInput` records, per KeyCode, how many times a synthetic value was **handed back to a caller**, split by `GetKey` / `GetKeyDown` / `GetKeyUp`, plus a wheel counter and a mouse-position counter. The bookkeeping is written only at the moment a synthetic value is returned, which can only happen for a key the driver injected, so an untouched key costs nothing on the hot path.

`consumed = delivered && the gate was open` is the field to assert on. For the wheel the honest number is `gate.checkDisplaySlotInputRan`: if `CheckDisplaySlotInput` did not run, `newScrollData` was never written and no consumer could possibly have seen anything, regardless of what the driver injected.

`requireConsumed` defaults to **true**, so an unconsumed input answers 409. That is the defect turned into a default: a caller who does nothing special cannot receive a success for input that did not happen.

`ScrollDataBackstopPatch` is belt and braces: a postfix on `CheckDisplaySlotInput` assigns `newScrollData` directly, so if the `get_mouseScrollDelta` prefix ever fails to apply the value still lands in the field consumers actually read. Assignment rather than accumulation, so both paths working is harmless.

---

## Identity

### Stationeers does not get identity from Steam at join time

It reads `PlayerCookie-v2.xml` from `Application.persistentDataPath` and honours it verbatim whenever `Version == 2 && ClientId != 0`:

```csharp
// NetworkManager
public static PlayerCookie Cookie { get; private set; }
public static ulong  LocalClientId => Cookie?.ClientId ?? 0;
public static string Username      => Cookie?.Username ?? string.Empty;

// NetworkManager.Init(TransportType), from GameManager.Awake
Cookie = ((!GameManager.IsBatchMode) ? PlayerCookie.Load() : null);
```

Steam is consulted only on the **create** path (`CreateNewCookie`), not on the load path. `ClientId` and `Username` both have public setters, so no reflection is needed to change them.

The server's `VerifyConnection` checks exactly three things: blacklist by the same self-reported `ClientId`, password string equality, and exact game-version string equality. The client sends `ClientId = NetworkManager.LocalClientId` verbatim in `VerifyPlayerMessage`. Steam auth is dead code: the only `BeginAuthSession`-family call site in the assembly is `SteamTransport.Authenticate`, which has **zero callers**.

The familiar server log line is `"Client " + client.ToStringNameAndId() + " is ready"`, formatted as `{name} ({ClientId})` from the client-supplied name. Entirely self-reported.

### The injection point

A postfix on `NetworkManager.Init(TransportType)`. That is where the cookie is loaded, it is the earliest point at which the identity can be rewritten, and it is long before anything reads `LocalClientId`. If `Cookie` is null (the batch-mode case) the patch constructs one and writes it through the property's backing field, so the mechanism would also work headless.

Live rewrite through `POST /identity` works too, because the value only has to be correct at the instant the join handshake copies it into `VerifyPlayerMessage`. An instance that booted with the wrong identity can be corrected without a restart.

### Why `PlayerCookie.Save()` must be suppressed

`Application.persistentDataPath` is per-Windows-user and **cannot be separated**. Editing `rocketstation_Data\app.info` was tested directly and does nothing: the instance still reported `persistentDataPath = .../Rocketwerkz/rocketstation`, `companyName = Rocketwerkz` and `productName = rocketstation`, while `dataPath` correctly followed the instance directory, proving the process really was running out of the copy. The player takes company and product from the serialized PlayerSettings inside `globalgamemanagers`, not from `app.info`. No new `AppData\LocalLow` folder and no new registry key appeared.

So every instance shares the developer's real `PlayerCookie-v2.xml`. `Save()` writes the in-memory `ClientId` over it, and its triggers include dismissing the old-save popup, dismissing the major-update popup, and **opening the in-game menu with Esc while a world is running**. A prefix skips the original whenever an override is configured or `Lock Cookie File` is set. Skipping is safe because the cookie is only ever read at startup.

### Duplicate identity is silent and destructive

`NetworkBase.Clients` is a bare `List<Client>` with no dedupe. The damage is one level down:

```csharp
public static readonly Dictionary<ulong, Brain> PlayerBrains = new Dictionary<ulong, Brain>();
```

`RegisterBrain` does a silent `PlayerBrains[steamId] = this` overwrite, so **the second joiner resolves onto the first joiner's character**. `GameManager.ClientInfo.TryAdd` silently drops the second client's info. Nothing anywhere warns.

Hence three layers of guard: the launcher refuses to provision a duplicate ClientId or port; `PeerProbe` asks every sibling control plane who it is and reports a conflict in `/status` and `/instance`; and `/connect` refuses to join into a detected conflict unless `allowDuplicateIdentity=true`. The join is where the enforcement belongs, because that is where the damage happens.

ClientId 0 is refused everywhere: it is the batch-mode sentinel, and two batch-mode clients both claim it.

### It works

```
12:36:18: Client RigClientOne (900000000001) is ready
12:37:16: Client RigClientTwo (900000000002) is ready
```

Distinct non-zero ClientIds, distinct names, both in world simultaneously, with distinct `Human` reference ids at distinct positions and `playersInGame: 3`.

---

## Window size and fullscreen

### The launch flags lose

`-screen-fullscreen 0 -screen-width W -screen-height H` are honoured by the native player when it creates the window, and then thrown away by the game, twice, neither call guarded by `IsBatchMode`:

```csharp
// Settings.LoadSettings(), reached from WorldManager.ManagerAwake() ABOVE that method's own
// IsBatchMode block
Screen.SetResolution(result, result2, CurrentData.FullScreen, CurrentData.RefreshRate);

// Settings.ApplyVideoSettings(), the last statement of GameManager.Start(), which runs AFTER
// CommandLine.ExecutePostLaunchCommands()
Screen.SetResolution(int.Parse(CurrentData.ScreenWidth), int.Parse(CurrentData.ScreenHeight),
                     CurrentData.FullScreen, CurrentData.RefreshRate);
```

`CurrentData` comes from the instance's own `setting.xml`, where `<FullScreen>` defaults to **true**.

### Correct the source, do not fight the symptom

A prefix on `ApplyVideoSettings` and a postfix on `LoadSettings` rewrite `CurrentData.FullScreen / ScreenWidth / ScreenHeight` before the game reads them. The game's own `SetResolution` call then asks for a window. Measured on both instances, in world and at the menu:

```
{"forceWindowed":true,"configuredWidth":800,"configuredHeight":600,
 "screenWidth":800,"screenHeight":600,"screenFullScreen":false,
 "screenFullScreenMode":"Windowed","setResolutionCalls":0,"settingsRewrites":838}
```

`setResolutionCalls: 0` is the number that matters. The plugin never had to call `Screen.SetResolution` itself.

**Nothing writes to `HKCU\Software\Rocketwerkz\rocketstation`.** That key is Unity's PlayerPrefs store, written natively, and it is shared with the developer's own client. A registry diff across a full session showed only Unity's own per-run bookkeeping moving (window position, session count, session id); `Screenmanager Fullscreen mode`, `Resolution Width` and `Resolution Height` were untouched. The values that did move were deliberately **not** restored, because restoring is still a write to a key this tool must not write to.

Incidentally, the original diagnosis blamed that registry key for the fullscreen launch. It was wrong: measured at the start of the session the key already held Windowed and a windowed resolution. The cause was purely `Settings.CurrentData`.

### Three traps on the way

1. `ScreenWidth` and `ScreenHeight` are declared as **string**, not int. `LoadSettings` uses `int.TryParse` and tolerates garbage; `ApplyVideoSettings` uses a bare `int.Parse` and would throw inside `GameManager.Start()`, an `async void`. Write digits only.
2. The `Settings` class is in the **global namespace**, and other loaded assemblies carry their own `Settings`. Resolving it by name returned the wrong one on the first build, which is what a reported "Settings type not found" actually meant. Resolution is a scan for a type named `Settings` that has both a static `CurrentData` field and a static `LoadSettings` method; nothing else in the process has both.
3. `<Monitor>` is serialized and read by nothing.

---

## Why the separate Win32 desktop is required

### The no-activate flag loses, measured

Launching through `CreateProcess` with `STARTF_USESHOWWINDOW` and `SW_SHOWNOACTIVATE`, sampling the foreground every 3 seconds for two minutes:

```
[0] FOREGROUND STOLEN: rocketstation(41500)
...
[39] FOREGROUND STOLEN: rocketstation(41500)
focus-steal samples: 40 / 40 over ~120s
```

Foreground moved within 3 seconds of launch and never came back. The cause: **`wShowWindow` only governs the first `ShowWindow(SW_SHOWDEFAULT)`**, and Unity calls `ShowWindow` explicitly once its window exists, so the flag is ignored.

### The desktop wins, measured

`CreateDesktopW`, then `STARTUPINFO.lpDesktop` pointed at it. Same sampling, both instances running, through a full boot and an entire acceptance test:

```
[0]  fg=Code(48008) | c1 phase=menu init=False plugins=2  | c2 phase=menu init=False plugins=2
[10] fg=Code(48008) | c1 phase=menu init=False plugins=37 | c2 phase=menu init=False plugins=37
[30] fg=Code(48008) | c1 phase=menu init=True  plugins=37 | c2 phase=menu init=True  plugins=37
focus steals by rocketstation: 0 / 55
```

**40/40 before, 0/55 after.** The developer's foreground was their editor at every sample, and still was when the last action landed.

Note what this is not. `SwitchDesktop` is deliberately not imported and nothing switches to that desktop. The instances render, run, join and are driven over HTTP on a desktop that is never shown. The desktop object lives as long as a process runs on it and disappears on its own afterwards, so there is nothing to clean up and no handle to keep. It costs nothing measurable: same plugin count, same Steam entitlements, normal boot time, GPU rendering fine.

.NET's `ProcessStartInfo` cannot express `lpDesktop` or `wShowWindow` with `UseShellExecute = false`, which is the entire reason the launcher carries a `CreateProcessW` P/Invoke.

### The reporting consequence, and its fix

`GetForegroundWindow` returns NULL when the calling process is on a desktop that is not receiving input. The old report therefore showed `foregroundPid: 0` and could not tell "I am a background window on the developer's desktop" from "I am on a desktop of my own". Those deserve different responses: the first says another window is in front, the second says the isolation is working as designed.

`NativeWindow` now compares the process's own desktop name (`GetThreadDesktop` plus `GetUserObjectInformationW`) against the input desktop's name (`OpenInputDesktop`, opened read-only and closed immediately) and answers one of five verdicts: `foreground`, `background`, `otherDesktop`, `noForeground`, `unknown`. `foregroundPid` is reported as `null` rather than 0 when it is not knowable, because 0 previously read as a real answer meaning "nothing is focused anywhere".

The two fixes are independent. The gate fix is what makes input work and works whether or not the desktop is separate; the desktop is what keeps the developer's foreground. Both are wanted.

---

## Instance provisioning

### Hard links, and what must never be one

Read-only bulk is NTFS-hard-linked from the real install: `rocketstation_Data` (about 1,026 files), `MonoBleedingEdge` (20), and the engine binaries. An instance costs directory entries plus a real copy of BepInEx rather than about 7 GB.

Hard links share the file data, so **nothing the game or a mod writes to may be a link**. Real copies: `doorstop_config.ini`, `Fixing The Controls modifiers.ini`, `app.info`, and the whole `BepInEx/` tree. Not carried at all: `imgui.ini` and `output_log.txt`, which are regenerated and resolved against the working directory.

Hard links cannot cross volumes, so the instances root must be on the game install's drive. The launcher checks and refuses with the exact remediation rather than silently making a 7 GB copy. Since the repository is frequently on a different drive, the instances root is relocatable through `-InstancesRoot` or `STATIONEERS_CLIENTRIG_ROOT`; only the linked trees need to move, because per-instance state is ordinary files.

### One directory buys all the isolation that is achievable

The BepInEx root is always `<dir of rocketstation.exe>\BepInEx` and no environment variable relocates it. `BepInEx.dll` and `BepInEx.Preloader.dll` carry **zero `BEPINEX_*` env vars**, and `BepInEx.cfg` has no `[Paths]` section. `doorstop_config.ini` uses a target path relative to the game directory. So a separate install directory automatically yields a separate BepInEx config, plugin set, cache, `LogOutput.log`, and InspectorPlus request and snapshot folders, in one move.

### The two flags that matter, and the one that must never be used

`-settingspath <file>` gives each instance its own `setting.xml`. It takes a FILE path despite help text saying `<full-directory-path>`.

`-logFile <unique path>` is mandatory, for a subtler reason than it looks. Two instances without it both start fine; that is the trap. What happens is that the second starter wins `Player.log`, the first instance's log goes nowhere with no error and no warning, and `Player-prev.log` is left at **0 bytes** because instance one rotated the developer's real previous log into it and instance two rotated the already-moved log over it again. The developer's previous log is destroyed by two rotations in one second.

**`-settings SavePath` must never be used.** It moves the save tree but not `StationSaveUtils.DefaultPath`, so StationeersLaunchPad scans an empty `<SavePath>\mods\`, finds nothing, and rewrites the developer's **shared** `modconfig.xml` with every `<Local>` entry deleted. Observed on a first boot: five local mod entries silently removed, file 289 lines down to 274, and the instance then loaded 32 plugins instead of 37, exactly the five missing mods. Nothing warned.

The correct lever is StationeersLaunchPad's own `SavePathOverride` in `stationeers.launchpad.cfg`, which moves `DefaultPath` itself. With it set and `-settings SavePath` dropped: the developer's `modconfig.xml`, `setting.xml` and `modrepos.xml` were byte-identical before and after across four instance boots, each instance wrote its own `modconfig.xml`, and the plugin count went 32 to **37, matching the developer's own client exactly**. Because it moves `DefaultPath`, `<Local>` mod folders must exist under the instance's own save root, which is why provisioning copies them and repoints the paths.

`-nographics` without `-batchmode` is rejected by the Unity 2022.3.62f3 Windows player: it prints `-nographics requires -batchmode`, pops a modal Win32 error window, and holds a live process that never boots, never honours `-logFile`, never loads BepInEx and never opens a control plane. There is no windowless-but-not-batchmode mode.

### What is not separable

**`Application.persistentDataPath`.** Covered above. `PlayerCookie-v2.xml`, `Player.log`, `Blueprints\` and the PlayerPrefs registry key are shared by every instance and by the developer's client.

**The Steam session.** One Steam client, one account. Every instance reports the developer's entitlements, which is convenient because DLC works everywhere, but they are not independent Steam identities and cannot be on one machine. A test needing one DLC owner and one non-owner is out of reach. DLC is pooled by design anyway: `SharedDLCManager.AddSharedDLC` ORs each client's self-reported `DLCType` into a union that is never subtracted from except at world teardown.

### Measured cost

| Item | Measured |
|---|---|
| Disk, first instance | 3.6 MB actual consumption (2.7 MB BepInEx copy plus directory entries for about 1,051 hard links) |
| Disk, second instance | 9.7 MB (adds a 6.6 MB copy of the local mods) |
| Shared via hard links | about 7 GB per instance that costs nothing |
| Provision time | about 2.7 s for the link tree, about 5 s including the mod seed |
| RAM, idle at the menu | about 5.0 GB working set per instance |
| RAM, in world after 10 minutes | about 10.0 GB working set per instance |
| Boot to main menu | about 100 s solo, about 110 s with two booting at once |
| Join to in-world | 42 s and 49 s |

RAM is the constraint, not disk. Ten gigabytes per in-world instance is what limits how far this scales.

---

## The console tee bound

The tee once took a client to a **12.75 GB working set** with a frozen pump after ingesting over 500,000 lines, and a later run reported 654 dropped lines within five minutes of a fresh launch. With N instances the risk multiplies by N.

A line count alone is not a bound. The lines that arrive during a storm are stack traces, and a single one can be megabytes, so 8,000 unbounded strings is not a bounded amount of memory. `ConsoleTap` therefore caps on three axes: lines per source (ring capacity), characters per line (truncate with a marker), and total characters per source (evict oldest until under budget). The third is the one that actually holds when lines are large. All three are configurable and all three report: `dropped`, `truncated`, `bufferedLines` and `bufferedChars` ride on every `/console/log` response and on `/status`.

Eviction nulls the slot rather than merely decrementing a count, so the ring does not pin evicted strings.

Two rings, not one. The BepInEx side sees every `Debug.Log` every mod makes, which during mod load is thousands of lines in a couple of seconds; sharing one ring would evict exactly the lines a test cares about. The sequence counter stays global across both, so `since` polling still yields one ordered stream.

---

## Plugin lifecycle traps

**The BepInEx plugin component is destroyed during boot.** `OnDestroy` fires on the `BaseUnityPlugin` component about a minute into startup while the process keeps running, and `Chainloader.PluginInfos[guid].Instance` is null for every plugin afterwards, including StationeersLaunchPad's own. A `TcpListener` stopped there leaves a live process with nothing listening and no error anywhere. The control plane is therefore owned by a static, never torn down from `OnDestroy`, and re-bound by a watchdog thread. **`Application.quitting` is the only teardown signal that means the process is going away.**

**A `DontDestroyOnLoad` GameObject created by the plugin is also destroyed and must be recreated**, observed exactly twice per session. So the primary main-thread pump is a postfix on `ImGuiManager.LateUpdate`, which belongs to the game, runs every frame from the splash screen onwards, and does not depend on any object this plugin owns. `MonoBehaviour.Update` on the plugin's own object is secondary and an `ElectricityManager.ElectricityTick` postfix is tertiary.

**`ImGuiManager.RenderOverlay` skips `OrbitalSimulation.Draw` entirely while the splash or loading screen is up**, and StationeersLaunchPad hangs all its in-game ImGui windows off a prefix on that method. So `/modsettings` needs `gameInitialized == true`, not merely loaded mods.

**`ConfirmationPanel.IsVisible` is `gameObject.activeInHierarchy`** and reads true during boot with an empty data stack. A dialog counts as showing only when `_dataStack` has data.

---

## The cursor-force wedge

`/cursor/force` exists, is guarded, and should be avoided.

The cursor is a tuple, not one field. Vanilla always writes `FoundThing` and `CursorTargetCollider` together, and `{FoundThing = X, CursorTargetCollider = null}` is a pair the game itself can never produce. `PlantAnalyserCartridge.GetScannedPlant` walks straight into it: `Thing.GetSlot(null)` reaches `Dictionary.TryGetValue(null)` and throws on every Thing, because the dictionary is eagerly constructed.

That throw is unrecoverable. The cartridge runs from `GameManager.Update` and `CursorManager.ManagerUpdate`, the only caller of `SetCursorTarget`, runs later in the same method with no try/catch between them. The exception aborts the frame before the cursor can be rebuilt, the stale target survives, and it throws again next frame, forever. `NetworkManager.ManagerUpdate` is in the same loop, so a wedged client also stops processing network packets. Measured at 100 exceptions per 6 seconds; only leaving the world recovered it.

Two consequences are baked into the code. `/cursor/force` refuses a target it cannot find a collider for, preferring a collider that is actually a key in the target's slot lookup so `GetSlot` returns a real Slot rather than merely not throwing. And `clear` writes the game's three fields directly rather than only dropping the pin, so it recovers a client whose `SetCursorTarget` is no longer reachable; that still lands because the plugin's pump is not downstream of the aborted `GameManager.Update`.

`FoundTerrain` is pinned to `Invalid` deliberately: `CursorManager.GetCurrentVoxelWorld` hard-casts `CursorTargetCollider` to `BoxCollider` guarded only by `CursorTerrain.IsValid`, so a valid terrain paired with a non-box collider is a second way to throw out of the same loop.

**Prefer `/player/use` with a `targetId`.** `OnServer.AttackWith` with an explicit target has no distance or line-of-sight gate: a stroke landed on a cable 15 m from the actor's body. Nothing needs aiming, so nothing needs the cursor.

---

## Transport

The control plane is a raw `TcpListener` speaking minimal HTTP/1.1, not `HttpListener`. `HttpListener` on the Microsoft CLR goes through http.sys and needs a URL ACL reservation or elevation; under Unity's Mono the managed implementation has its own quirks with keep-alive and binary bodies. A socket plus a small parser has no such dependencies and behaves identically everywhere.

One request per connection, always answered with `Connection: close`, served inline on the accept thread. Requests are therefore strictly sequential, which is a feature: a harness that fires two engine mutations concurrently gets nondeterministic results.

The JSON reader is hand-rolled because the game ships no JSON library a BepInEx plugin can safely reference. It diverges from strict JSON in exactly one place: an **undefined** escape keeps both characters rather than dropping the backslash, so `"C:\Rig\Scratch"` in a hand-written body survives. Every escape JSON actually defines still decodes normally, so anything a real encoder produced round-trips unchanged. The escapes JSON does define (`\b`, `\f`, `\n`, `\r`, `\t`) still consume the backslash, which is why `/savepath` refuses a path containing a control character instead of using it.

`ClientId` travels through the manifest as a **string**, because the JSON number parser goes through `double` and silently loses precision above 2^53. A truncated ClientId is exactly the failure the field exists to prevent.

---

## Relevant central pages

- [Research/Workflows/DrivingTheGameClientProgrammatically.md](../../Research/Workflows/DrivingTheGameClientProgrammatically.md) - the curated version of the input, gate and window findings.
- [Research/GameSystems/CursorManager.md](../../Research/GameSystems/CursorManager.md) - the full cursor state inventory behind the wedge.
- [TestRig/DedicatedServer/CLAUDE.md](../DedicatedServer/CLAUDE.md) - the server side of any multiplayer test this rig drives.
- [TestRig/DedicatedServer/dev-plugins/ScenarioRunner/README.md](../DedicatedServer/dev-plugins/ScenarioRunner/README.md) - the server-side counterpart, including the give-item scenario that hands an item to a connected player without involving the client's cursor.

## Corrections owed elsewhere

- `TestRig/DedicatedServer/CLAUDE.md` (two places) and the repo-root `CLAUDE.md` name the PlayerPrefs key as `HKCU\Software\Rocketwerkz Limited\rocketstation`. That key **does not exist**. The real one is `HKCU\Software\Rocketwerkz\rocketstation`; `Rocketwerkz Limited` is only an `AssemblyCompany` string.
