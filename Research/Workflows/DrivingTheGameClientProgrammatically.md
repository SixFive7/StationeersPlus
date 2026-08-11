---
title: Driving the game client programmatically
type: Workflows
created_in: 0.2.6403.27689
verified_in: 0.2.6403.27689
verified_at: 2026-08-11
sources:
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.Human.decompiled.cs
  - .work/decomp/0.2.6403.27689/StationeersLaunchPad.decompiled.cs
  - TestRig/ClientRig/
related:
  - ../Workflows/InspectorPlusUsage.md
  - ../GameClasses/ColorSwatch.md
tags: [client, automation, input, networking, multiplayer, screenshots, bepinex, harmony, launchpad]
---

Everything a BepInEx plugin needs in order to drive the Stationeers **client** with nobody at the
keyboard: the seams for input, joining a server, spawning, screenshots and live mod config, plus
the boot-order and lifecycle traps that make a naive implementation fail silently. All of it was
exercised against a live client on 0.2.6403.27689; the implementation is the ClientDriver plugin
and its launcher under `TestRig/ClientRig/`.

## Plugin lifecycle: the BepInEx component is destroyed during boot
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

`OnDestroy` fires on a `BaseUnityPlugin` component roughly a minute into startup while the process
keeps running. After that point `BepInEx.Bootstrap.Chainloader.PluginInfos[guid].Instance` is null
for every plugin, including StationeersLaunchPad itself.

Consequences:

- Anything torn down from `OnDestroy` dies a minute after launch. A `TcpListener` stopped there
  leaves the process alive with nothing listening and no error anywhere.
- `Application.quitting` is the only teardown signal that actually means the process is going away.
- Long-lived state belongs to a static, not to the component.

A `DontDestroyOnLoad` GameObject created by the plugin is also destroyed and must be recreated;
observed exactly twice per session in ClientDriver's counter.

## Per-frame main-thread hooks that survive everything
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

`Assets.Scripts.UI.ImGuiManager.LateUpdate()` (public, `Assembly-CSharp` line 238964) runs every
frame from the splash screen onwards and belongs to the game, so a Harmony postfix on it is a
main-thread pump that is immune to the lifecycle problem above. Its body:

```csharp
public void LateUpdate()
{
    if (renderTexture.width != Screen.width || renderTexture.height != Screen.height)
    {
        CreateRenderTexture();
    }
    RenderOverlay();
}
```

`RenderOverlay()` (line 238972) is where the in-game ImGui frame lives:

```csharp
private void RenderOverlay()
{
    PrepareImGuiFrame();
    ConsoleWindow.Draw();
    if (SplashBehaviour.IsActive)              { SplashBehaviour.Draw(); }
    else if (ImGuiLoadingScreen.IsShowing)     { ImGuiLoadingScreen.DrawStandardLoading(); }
    else
    {
        OrbitalSimulation.Draw();
        CinematicCamera.DrawOverlay();
        ...
    }
}
```

Two things follow. First, `ConsoleWindow.Draw()` is outside the branch, so the console renders at
every phase. Second, `OrbitalSimulation.Draw()` is skipped entirely while the splash screen or the
loading screen is up, and StationeersLaunchPad hangs all of its in-game ImGui windows off a prefix
on that method. Any plugin drawing ImGui through the same seam is invisible until the real main
menu is reached.

Fallback pumps, in the order ClientDriver uses them: `ImGuiManager.LateUpdate` postfix (primary),
`MonoBehaviour.Update` on an own GameObject (secondary), `ElectricityManager.ElectricityTick`
postfix (tertiary, the one InspectorPlus and ScenarioRunner rely on headless).

## Console capture
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

Every `Assets.Scripts.ConsoleWindow` print overload funnels into one method:

```csharp
public static void Print(string output, ConsoleColor color = ConsoleColor.White,
                         bool clearLine = false, bool aged = true, bool unformatted = false)
```

`PrintError(string, bool)` calls it with `ConsoleColor.Red` then prints `Environment.StackTrace` in
Gray. `PrintAction(string, bool)` calls it with `ConsoleColor.Yellow`. All seven `GameString`
overloads format and delegate. A single Harmony postfix on the five-argument `Print` therefore
captures the lot.

Not captured by that postfix: `PrintBlock`, `PrintSegmentedBlock`, `PrintSegmentedBlockRaw` and
`PrintTable`, which write straight into the ring. For those, and for anything printed before the
plugin loaded, read the game's own ring directly: `ConsoleWindow.ConsoleBuffer` is a public
`ConsoleLine[]` of `DEFAULT_CONSOLE_BUFFER_SIZE` = 1024 entries, index 0 newest.
`ConsoleLine` exposes `Color` (uint), `Time`, `Text` and `Continuations` (the tail of a multi-line
message, split on `\n`).

`ConsoleWindow.Submit(string)` is public static and is the console's own input path: it prints the
echo then calls `CommandLine.Process`. Capturing from a sequence number taken before the call gets
the echo plus all output. `Util.Commands.CommandLine.AddCommand(string, CommandBase)` is public,
so a plugin can register its own console commands.

Note the namespace trap: `ConsoleWindow` is in the bare `Assets.Scripts` namespace and clashes with
`Assets.Scripts.Settings`. Use `using ConsoleWindow = Assets.Scripts.ConsoleWindow;`.

BepInEx-side capture is a separate stream. Registering an `ILogListener` on
`BepInEx.Logging.Logger.Listeners` catches every plugin's log and, because `UnityLogListening` is on
in `BepInEx.cfg`, every `Debug.Log` in the process as well. During mod load that is thousands of
lines in a couple of seconds, so it needs its own ring buffer or it evicts the console lines a test
actually cares about.

## Input injection
<!-- verified: 0.2.6403.27689 @ 2026-07-30 -->

Patch `UnityEngine.Input`, not `KeyManager`. Every `KeyManager` query bottoms out in the Unity layer:

```csharp
public static bool GetButton(KeyCode key)
{
    if (key != KeyMap.ToggleConsole && ConsoleWindow.IsOpen) { return false; }
    return Input.GetKey(key);
}
public static bool GetMouse(string key)
{
    if (key.Equals("Primary")) { return GetButton(KeyMap.PrimaryAction); }
    return GetButton(KeyMap.SecondaryAction);
}
```

and a large amount of game code calls `Input.GetKey(KeyMap.X)` directly, bypassing `KeyManager`.
Patching `Input.GetKey` / `GetKeyDown` / `GetKeyUp` / `GetMouseButton*` / `get_mouseScrollDelta` /
`get_mousePosition` covers both. All of those are managed wrappers over externs on Unity
2022.3.62, so Harmony patches them without complaint (confirmed applied at runtime).

`KeyMap` is a static class of mutable `public static KeyCode` fields, not an enum, and the values
are rebindable at runtime from settings. Resolve an action name against the live field rather than
hardcoding a default. `KeyMap.PrimaryAction` defaults to `KeyCode.Mouse0`, `SecondaryAction` to
`Mouse1`, `ToggleConsole` to `F3`, `SwapHands` to `E`. `KeyMap.Teleport` is vestigial: it is bound
to `ToggleNightVision` and saved under the settings key `"NightVision"`.

**Express synthetic input as an absolute `Time.frameCount` window, never as a countdown ticked from
`Update`.** MonoBehaviour update order is undefined, so a countdown can expire before the frame's
real consumer runs. Open the window on `Time.frameCount + 1` and it is visible for the whole of
every frame in it regardless of ordering.

Mouse wheel specifics. `InventoryManager.CheckDisplaySlotInput()` caches
`newScrollData = Input.mouseScrollDelta.y / 10f` at the top of every frame, and that public field is
what most consumers read, including SprayPaintPlus's `ColorCyclerPatch`. `PrecisionPlacementMode`
zoom and a few UI paths read `Input.mouseScrollDelta` directly instead. Patching the Unity property
covers everything; a postfix on `CheckDisplaySlotInput` that assigns `newScrollData` is a useful
backstop.

**One frame of wheel is one notch.** Consumers act once per frame, so a two-frame injection scrolls
a spray can two colours. Verified in world: `frames=1` steps the colour index by exactly 1 per call.

Re-confirmed on 0.2.6403.27689: there is no cached key state anywhere for the patch to sit under.
`InputSystem.KeyWrap.PollForInput` (line 191636) calls `Input.GetKeyDown` / `GetKey` / `GetKeyUp`
directly and fires its C# events synchronously from inside that call stack; its `IsPressed` and
`IsPressedThisFrame` properties are written there and read by nothing in the assembly.
`KeyMap.PollInputs` (44823) only fans out over a `HashSet<KeyWrap>`. `KeyManager.GetButton`,
`GetButtonDown` and `GetButtonUp` (44446-44471) are a `ConsoleWindow.IsOpen` short-circuit plus the
live Unity call. There are 139 direct `Input.*` call sites in `Assembly-CSharp`, so the Unity layer
really is the one chokepoint. The modern input package is absent: no `UnityEngine.InputSystem`
references, and no `Unity.InputSystem.dll` in `rocketstation_Data\Managed\`, only
`UnityEngine.InputLegacyModule.dll`.

## The gameplay input gate: `Cursor.visible` kills per-frame input
<!-- verified: 0.2.6403.27689 @ 2026-07-30 -->

Patching `UnityEngine.Input` delivers the value. It does not guarantee anything consumes it. The
consumer side has a gate that closes on its own in a background window, and when it is shut the
symptom is indistinguishable from injection not working: synthetic keys and wheel do nothing, while
direct method calls on the same objects work perfectly.

`Assets.Scripts.Inventory.InventoryManager.ManagerUpdate` (287152), abbreviated to the branch that
matters:

```csharp
if (Cursor.visible || Parent.IsUnresponsive || ConsoleWindow.IsOpen)
{
    return;
}
CheckDisplaySlotInput();
CheckSeatedInput();
...
if (Parent.State == EntityState.Alive && IsAllowedToLook() && IsParentSafe() && !Stationpedia.IsOpenAndLocked)
{
    switch (CurrentMode)
    {
    case Mode.Normal:
        NormalMode();
        break;
    case Mode.Placement:
        PlacementMode();
        break;
    case Mode.PrecisionPlacement:
        PrecisionPlacementMode();
        break;
```

Everything below that early return stops:

- **`CheckDisplaySlotInput` (287367) is the only writer of `InventoryManager.newScrollData` in the
  whole assembly.** A regex search for `newScrollData\s*=` returns exactly one hit, line 287369,
  `newScrollData = Input.mouseScrollDelta.y / 10f`. There is no reset to zero anywhere; the
  once-per-frame overwrite is the reset. So when the gate is shut the wheel is never sampled and the
  field keeps its last value indefinitely.
- **`NormalMode` (288000)** never runs, which takes with it every mod that hangs a per-frame patch
  there. SprayPaintPlus's `ColorCyclerPatch` is a prefix on `NormalMode`, and that is where it packs
  and sends its client-half preference mask, so a driven client silently never reports its own
  settings to the server.

Note that `NormalMode` does not read `newScrollData` itself; the placement-rotation reads at 288400,
288404, 288470, 288474, 288618 and 288622 are all inside `PlacementMode` (288320).
`PrecisionPlacementMode` (288256) bypasses the field and reads `Input.mouseScrollDelta.y` raw.

The same term gates movement, at 211510:

```csharp
if (KeyManager.InputState != KeyInputState.Game || Cursor.visible || !IsGround || _jumping || !IsInputAscend)
```

**Why an unfocused window ends up here.** Unity releases the cursor lock when the application loses
focus. `MouseModeController.Check` (201335) tries to re-lock every frame and `SetState` (201363)
re-issues `CursorManager.SetCursor(locked)` whenever the actual state does not match, but it cannot
take the lock back while the window is in the background, so `Cursor.visible` stays true for as long
as the window is not foreground.

Nothing in the managed code checks application focus directly. `Application.isFocused` has zero
occurrences in `Assembly-CSharp`. `Application.runInBackground` appears once, at 102476, inside a
diagnostic print. The single `OnApplicationFocus` (201248, on `CursorManager`) only restores cursor
state when focus is regained. The focus dependency is entirely this second-order one through the
cursor.

**Working around it in-process.** Assert the cursor state from a Harmony prefix on
`InventoryManager.ManagerUpdate` itself, so the write lands a few instructions before the gate reads
it and nothing can intervene:

```csharp
if (Cursor.visible) Cursor.visible = false;
if (Cursor.lockState != CursorLockMode.Locked) Cursor.lockState = CursorLockMode.Locked;
```

This needs no window focus and no OS input, so it keeps the never-focus property that makes the
in-process design worth having. It is only correct for a client nobody is sitting at: it takes the
mouse cursor away from a real player. ClientDriver gates it behind
`Client - Gameplay Input / Force Gameplay Input`, default off.

Two further gates worth knowing, both of which report as "input did nothing":

- `ConsoleWindow.IsOpen` short-circuits **every** `KeyManager.GetButton*` call (44448, 44457, 44466)
  for any key other than `KeyMap.ToggleConsole`, as well as appearing in the `ManagerUpdate` gate.
- `KeyWrapBindings.KeyWrapOnEvent` (191728) filters every KeyWrap-bound action on
  `item.inputState == KeyInputState.All || item.inputState.HasFlag(KeyManager.InputState)`.
  `KeyManager.InputState` (43706) is a public getter with a private setter, defaults to
  `KeyInputState.Game`, and is moved only by `SetInputState` / `RemoveInputState` (43864, 43870)
  through a `Dictionary<string, KeyInputState>` stack. A panel that pushes a state and never pops it
  leaves every bound action inert. `KeyMap._SwapHands.Bind(InputPhase.Down, SwapHandsOnKeyUp,
  KeyInputState.Game)` at 43771 is one of those, which is why a synthetic SwapHands can be delivered
  and still do nothing.

`enum KeyInputState : byte { All = 0, Game = 2, Paused = 4, Typing = 8, Cinematic = 0x10 }` (43638).

**Diagnosing it.** Do not infer this from behaviour. Prefix and postfix each link of the chain and
compare enter counts across the input window: `GameManager.Update` (which runs the manager loop at
`GameManager.decompiled.cs:1540-1543`), `KeyManager.ManagerUpdate` (43736), `KeyMap.PollInputs`,
`InventoryManager.ManagerUpdate`, `InventoryManager.CheckDisplaySlotInput`,
`InventoryManager.NormalMode`. A link whose enter count stops advancing is not being reached; a link
whose enter count outruns its exit count is throwing. ClientDriver exposes this as `GET /diag/input`.

## Window size and fullscreen come from the game, not the command line
<!-- verified: 0.2.6403.27689 @ 2026-07-30 -->

`-screen-width`, `-screen-height` and `-screen-fullscreen` are consumed by the Unity player when it
creates the window, and the game then overrides them twice with its own saved preference. Both call
sites are unguarded, so this happens in batch mode too.

```csharp
// Settings.LoadSettings(), line 266641, reached from WorldManager.ManagerAwake() line 59685,
// which sits ABOVE that method's `if (!GameManager.IsBatchMode)` block
if (int.TryParse(CurrentData.ScreenWidth, out var result) && int.TryParse(CurrentData.ScreenHeight, out var result2))
{
    Screen.SetResolution(result, result2, CurrentData.FullScreen, CurrentData.RefreshRate);
}

// Settings.ApplyVideoSettings(), line 266686, the last statement of GameManager.Start() at 205102,
// which runs AFTER CommandLine.ExecutePostLaunchCommands()
Screen.SetResolution(int.Parse(CurrentData.ScreenWidth), int.Parse(CurrentData.ScreenHeight),
                     CurrentData.FullScreen, CurrentData.RefreshRate);
```

The fields are `[XmlElement]` members of `SettingData` (265393-265424), so they are exactly the
element names in `setting.xml`:

```csharp
[XmlElement] public int    Monitor       = 1;
[XmlElement] public string ScreenWidth   = "1920";
[XmlElement] public string ScreenHeight  = "1080";
[XmlElement] public int    RefreshRate   = 60;
[XmlElement] public bool   FullScreen    = true;
[XmlElement] public bool   Vsync;
```

Consequences for a driven instance:

- `<FullScreen>` defaults to **true**, so an instance whose `setting.xml` was never edited comes up
  fullscreen no matter what the launch line said. `-settingspath` gives each instance its own file,
  so setting `<FullScreen>false</FullScreen>` there is per-instance and costs nothing.
- `ScreenWidth` and `ScreenHeight` are **strings**. `LoadSettings` uses `int.TryParse` and tolerates
  garbage; `ApplyVideoSettings` uses a bare `int.Parse` and will throw inside `GameManager.Start()`,
  an `async void`, if the value is not numeric.
- `Screen.fullScreenMode` and the `FullScreenMode` enum have zero occurrences; the game only ever
  uses the legacy `bool fullscreen` overload.
- `<Monitor>` is serialized and read by nothing. `CurrentData.Monitor` has no consumers.
- The game never writes Unity's own PlayerPrefs screen keys: the string `Screenmanager` has zero
  occurrences in the assembly, and the nine `PlayerPrefs` call sites are all server-browser filter
  and sort state (`FILTER_VERSION`, `FILTER_PASSWORD`, `FILTER_EMPTY`, `JoinSortBy`) plus
  `ChatSize`. The values under `HKCU\Software\Rocketwerkz\rocketstation` are Unity's, written
  natively.

The in-process fix is to correct `Settings.CurrentData` before the game reads it, rather than to call
`Screen.SetResolution` afterwards and be overwritten. A prefix on `ApplyVideoSettings` that rewrites
the three fields makes the game's own call ask for a window.

Resolving the `Settings` type by name is a trap: it is in the **global namespace**, and other loaded
assemblies carry their own `Settings`, so `AccessTools.TypeByName("Settings")` can return the wrong
one. Scan for a type named `Settings` that has both a static `CurrentData` field and a static
`LoadSettings` method.

## Direct Connect
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

One public instance method does the whole join:

```csharp
// Assets.Scripts.NetworkClient
public void JoinClientFromMenu(string ipPort)
```

It runs `ClientPreJoin()` (sets `GameManager.GameState = GameState.Joining`), splits the address,
and calls `NetworkManager.StartClient(address, port, localPort)`, then `OnJoinStart()`. Calling
`StartClient` directly skips the menu teardown and the loading screen, so use the menu method.

Getting the instance is the first trap: `FindObjectOfType<NetworkClient>()` only sees active,
enabled components and returns null for roughly the first minute of a session. Fall back to
`Resources.FindObjectsOfTypeAll<NetworkClient>()`, which includes inactive objects, and poll until
it appears.

The second trap is the join timer. `OnJoinStart` arms a 10 second `AutoReset` timer whose elapsed
handler does nothing but give up:

```csharp
private static async void ConnectionTimerOnElapsed(object sender, ElapsedEventArgs e)
{
    ConsoleWindow.PrintError("Connection could not be established");
    await UniTask.SwitchToMainThread();
    StopConnectionTimer();
    Singleton<ConfirmationPanel>.Instance.Show(MultiplayerCouldNotConnectKey, MultiplayerCheckAddressKey, "ButtonOk", Cancel);
}
```

Ten seconds is not enough for a heavily modded dedicated server. Observed: the server logs
`A connection is incoming` and `VerifyPlayer - Serialising connection method: RocketNet`, then the
client cancels itself twenty seconds later. `NetworkClient.StopConnectionTimer()` is public static;
call it immediately after `JoinClientFromMenu` and impose your own timeout instead.

Two dead ends worth knowing. `-join <address:port>` does exist as a launch command
(`JoinCommand`, keys `join` and `joingame`), but unlike `LoadGameCommand` and `NewGameCommand` it
does not override `RequiresGameManagerIsInitialized`, so it runs at
`RuntimeInitializeOnLoadMethod` time and prints "network client not initialised yet" if the
component is not up. And a password-protected server prompts through
`PasswordWindow.PromptPassword`, with no client-side field to pre-fill.

Leaving: `GameManager.LeaveGame()` (public static) tears the session down;
`NetworkClient.Cancel()` aborts a pending join and calls `LeaveGame` itself. Both return before
the menu is actually back, so poll `GameManager.GameState == GameState.None`.

## State accessors
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

| What | Accessor | Notes |
|---|---|---|
| Session phase | `GameManager.GameState` | `enum GameState : byte { None, Joining, Waiting, Running, Loading, Paused }`. Public static get AND set. |
| Client vs host | `NetworkManager.NetworkRole`, `.NetworkState`, `.IsClient`, `.IsServer` | public static fields/properties |
| Simulation owner | `GameManager.RunSimulation` | `=> !NetworkManager.IsClient` |
| World | `WorldManager.CurrentWorldName`, `.CurrentWorldId`, `.IsGamePaused` | `WorldManager` is in the global namespace |
| Local player | `Human.LocalHuman` | one-line forwarder to `InventoryManager.ParentHuman`; null at the menu |
| Position | `Thing.ThingTransformPosition` | `Thing.Position` is a cached copy, up to one FixedUpdate stale |
| Look | `CameraController.Instance.RotationX` (pitch, positive is up) and `.RotationY` (yaw) | |
| Hands | `InventoryManager.ActiveHandSlot`, `InventoryManager.Instance.ActiveHand/.InactiveHand` | `Slot.Get()` returns the occupant; `Slot.Occupant` is `[Obsolete]` |
| Cursor target | `CursorManager.CursorThing` | rebuilt every frame by `CursorManager.SetCursorTarget()` |

`WorldManager.IsLoaded`, `NetworkManager.Instance.LocalClient` and `GameManager.Instance.Player` do
not exist. There is no `CharacterController` anywhere in the assembly; movement is pure `Rigidbody`.

## Teleport, look, and using an item
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

`Human.ForceSetPosition(Vector3)` is gated on `GameManager.RunSimulation`, which is false on a
multiplayer client, so it is a silent no-op there. The game's own `teleport` console command
sidesteps it and writes `InventoryManager.Parent.Transform.position` raw. The reliable sequence,
which is `ForceSetPosition` minus the gate:

```csharp
human.ThingTransformPosition = target;   // writes the transform and the Position cache
human.Transform.position = target;
rb.MovePosition(target);
if (!rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
human.ResetInterpolation();              // public, on DynamicThing
```

Verified in single player: position moved exactly as requested and stuck.

Look direction: `CameraController.UnitTest_SetRotation(float InRotationX, float InRotationY)` is
public and sets pitch then yaw. `SetMouseLook()` adds mouse delta to both every `LateUpdate`, so a
one-shot write holds only while the mouse is still, which is the normal case for a driven client.
To aim at a world point:

```csharp
Vector3 dir = (worldPoint - CameraController.CameraOrigin).normalized;
float yaw   = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
float pitch = Mathf.Asin(dir.y) * Mathf.Rad2Deg;   // positive is up, matching RotationX
CameraController.Instance.UnitTest_SetRotation(pitch, yaw);
```

Verified: a target 10 m along +Z gives yaw 0 pitch 0, 10 m along +X gives yaw 90, and 10 m forward
and 10 m down gives pitch -45.

Using the held item on a specific target, without aiming, goes through:

```csharp
// global namespace
public static void OnServer.AttackWith(Thing attackParent, byte activeHandSlotId, byte offHandSlotId,
                                       long targetId, Vector3 attackPosition, float completedRatio,
                                       bool isDestroy, bool isCopy)
```

It predicts locally with `doAction: GameManager.RunSimulation` and sends an `AttackWithMessage` when
`NetworkManager.IsClient`, so it is correct on both a host and a client. It constructs the `Attack`
with `targetCollider: null`, so an override that dereferences `attack.TargetCollider` would throw;
the spray-paint path does not. Verified end to end in single player: a spray can in the active hand
plus `AttackWith(targetId: <wall>)` changed the wall's `CustomColor.Index` to the can's colour.

## Spawning
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

Prefab lookup is by hash, with string overloads that wrap `Animator.StringToHash`:
`Prefab.Find(string)`, `Prefab.Find<T>(string)`, `Prefab.TryFind(...)`, and the full list in
`Prefab.AllPrefabs`. `Prefab.Find` returns null on a miss; `TryFind<T>` throws internally and
`Find<T>` swallows it and returns default, so prefer `Find(name)` plus a null check.

| Goal | Call | Authority |
|---|---|---|
| Item straight into a slot | `OnServer.Create<T>(Thing prefab, Slot slot)` | needs `RunSimulation`, so host or single player |
| Item on the ground near a player | `OnServer.SpawnDynamicThingMaxStack(long parentId, string prefabName)` | client-safe, forwards to the server |
| Item at an arbitrary position | `OnServer.Create<T>(Thing prefab, Vector3 pos, Quaternion rot)` | server side |
| Built structure on the grid | `Constructor.SpawnConstruct(CreateStructureInstance)` | client-safe, sends `ConstructionCreationMessage` |

`CreateStructureInstance(Structure prefab, Grid3 localGrid, Quaternion worldRotation, ulong ownerClientId, int colorIndex = -1)`;
get the grid coordinate with `GridController.World.WorldToLocal(worldPosition)`. Verified: a
`StructureCompositeWall` placed 3 m in front of the player landed on the grid, fully built and
paintable, and reported a sensible `CustomColor.Index`.

Spray can prefabs are `ItemSprayCan<Color>` for the 12 base colours and
`ItemSprayCanMetallic<Colour>` for the four DLC ones; 16 in total, all present in `Prefab.AllPrefabs`.
The colour lives on the can as a `Material` (`SprayCan.PaintMaterial`), not an index;
`GameManager.GetColorIndex(Material)` maps it back to a swatch index.

## Screenshots
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

Only `ScreenCapture.CaptureScreenshotAsTexture()` after `yield return new WaitForEndOfFrame()`
includes overlay UI. The game's own `GameManager.CreateScreenShot(int, int, Camera)` and
`GameManager.SaveScreenShot(...)` render a Camera into a RenderTexture, which is right for a world
thumbnail but silently omits every uGUI canvas and every ImGui panel. Anything that needs to prove
a panel rendered readable text must go through the backbuffer.

Cost at 3840x2160: about 6 MB of PNG per shot, roughly 500 ms end to end. A `Graphics.Blit` through
a smaller `RenderTexture` before `EncodeToPNG` cuts that by an order of magnitude and stays sharp
enough to read UI text.

## Mod config at runtime
<!-- verified: 0.2.6403.27689 @ 2026-07-27, StationeersLaunchPad 0.5.0 -->

**`Chainloader.PluginInfos` only ever lists what BepInEx loaded out of `BepInEx/plugins/`.** On a
normal client that is StationeersLaunchPad and nothing else; every Workshop mod arrives through
StationeersLaunchPad and is invisible to it. Combined with the destroyed-component problem above,
`Chainloader.PluginInfos[guid].Instance.Config` is useless for reaching a mod's settings.

What works: scan `AppDomain.CurrentDomain.GetAssemblies()` for a type carrying
`[BepInPlugin(guid, ...)]`, then walk its static members for any `ConfigEntryBase` and read
`entry.ConfigFile`. A `ConfigEntry` holds a reference to its owning `ConfigFile`, and both outlive
the MonoBehaviour. From the `ConfigFile` the whole `Keys` collection is reachable, and
`ConfigEntryBase.BoxedValue` is settable with a value produced by
`TomlTypeConverter.ConvertToValue(string, entry.SettingType)`.

Verified end to end: writing `net.spraypaintplus` / `Client - Color Cycling` / `Color Cycling` from
`AllColors` to `WithinFamily` changed the value `SprayPaintPlus.SettingsMerge.EffectiveColorCycling`
computes, immediately, with no restart.

Scanning by assembly also exposes duplicates: two loaded copies of the same mod assembly both carry
the attribute, and only the copy whose `Awake` ran has populated statics. Try every match rather
than the first.

## The StationeersLaunchPad settings panel
<!-- verified: 0.2.6403.27689 @ 2026-07-27, StationeersLaunchPad 0.5.0 -->

`StationeersLaunchPad.ConfigPanel.DrawWorkshopConfig(ModInfo)` is public static and opens its own
ImGui window. Vanilla only calls it while the Workshop menu is open and a mod row is selected:

```csharp
private static void DrawWorkshopMenuConfig()
{
    if (((Behaviour)WorkshopMenu.Instance).isActiveAndEnabled)
    {
        ... workshopMenuSelectedField.GetValue(WorkshopMenu.Instance) ...
        ConfigPanel.DrawWorkshopConfig(LaunchPadConfig.MatchMod(...));
    }
}
```

Calling `DrawWorkshopConfig` directly from a prefix on `OrbitalSimulation.Draw` renders the same
panel with no menu state involved, which makes the panel screenshot-testable without driving a
mouse through a list. The `ModInfo` to pass is `LoadedMod.Info` from `ModLoader.LoadedMods`.

Because the seam is `OrbitalSimulation.Draw`, this only works once the real main menu is up; see
the `RenderOverlay` branch above.

## Failure modes seen on a live client
<!-- verified: 0.2.6403.27689 @ 2026-08-11 -->

**Transient Steam Workshop failure parks the client forever.** When
`StationeersLaunchPad.Steam.FetchWorkshopPage` throws (a `NullReferenceException` out of
`Steamworks.Helpers.TakeMemory` inside `ISteamUGC.GetQueryUGCPreviewURL`), StationeersLaunchPad
prints "Error occurred during initialisation. Mods will not be loaded.", shows its own ImGui screen
with a Start Game button, and never reaches the menu. Loaded plugin count stays at 2. It clears on
relaunch; an unattended loop needs to detect it and retry.

**`ConfirmationPanel.IsVisible` is a false positive during boot.** It is
`gameObject.activeInHierarchy`, which is true for a window early in startup with an empty
`_dataStack` behind it. Treat a dialog as showing only when the stack has data.

**`StationeersLua` and `ScriptedScreens` throw an exception every single frame.** The
`ScriptedScreensScriptableUiSystem` static ctor reaches MessagePack directly, in one hop, with no
intermediary type: its last statement is
`MpOptions = MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance).WithCompression(...)`,
and from there `ContractlessStandardResolver` -> `StandardResolverHelper` ->
`ImmutableCollectionResolver` pulls `System.Collections.Immutable, Version=8.0.0.0`, which fails with
`Could not load file or assembly 'System.Collections.Immutable, Version=8.0.0.0'`. Once the ctor
fails, every later access rethrows `TypeInitializationException`, and it rethrows from
`ScriptedScreensBehaviorPatch.KeyWrap_PollForInput_Prefix` -> `KeyMap.PollInputs` ->
`KeyManager.ManagerUpdate` -> `GameManager.Update`, i.e. every frame for the rest of the session,
at the main menu and in world alike. It floods the console ring hard enough to evict thousands of
lines. Disabling both mods removes the exception but does not by itself fix a stalled join.
`StationeersLua` has a second, separate per-frame site that reaches the same poisoned library
(`McpServerTickPatch.GameManager_Update_Postfix` -> `McpMultiplayerDebugProxy.ShouldProxyRequestsLocally()`);
it is not part of the `ScriptedScreens` chain. Mechanism, blast radius and the resolution gap:
[Patterns/ModDependencyAssemblyResolution.md](../Patterns/ModDependencyAssemblyResolution.md).

**It is observationally log spam, not a functional break.** Field evidence from the developer:
they host a session with both mods enabled and a second player joins normally, with the exception
firing throughout. That settles it, and it is worth recording because the mechanism invites a
stronger conclusion than the evidence supports. `GameManager.Update` does iterate its managers
with no try/catch, and `NetworkManager.ManagerUpdate` is the client's only network receive pump,
so a throwing manager ordered before it WOULD stop all packet processing. But `Managers` is a
`public List<ManagerBase>` populated by Unity serialization, not by code, so **the order is not in
the decompile at all** and the "throws before the pump" step was never established. Do not assert
it without evidence. Copying `System.Collections.Immutable.dll` into `BepInEx/core/` was tried once
and a stalled join succeeded afterwards, but that is a single correlated observation and the
transient StationeersLaunchPad Workshop hang documented above is an equally good explanation for
the same pair of events.

Attribution was checked properly rather than assumed, because a driving plugin patching
`UnityEngine.Input` is an obvious suspect for a fault on the input path. Counting occurrences in
`BepInEx/LogOutput.log` is misleading: BepInEx collapses the repeats there, so the file shows single
digits while the game console is taking thousands. Measured against the game's own console instead,
with the `UnityEngine.Input` patches never applied at all, the exception still fires at the same
rate. It is not caused by patching `Input`.

## Keeping a driven session out of the developer's save folder
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

`StationSaveUtils.GetSavePath()` resolves `Settings.CurrentData.SavePath` on every call and creates
`saves`, `scripts` and `mods` beneath it:

```csharp
public static string GetSavePath()
{
    if (string.IsNullOrEmpty(Settings.CurrentData.SavePath)) { Settings.CurrentData.SavePath = DefaultPath; }
    string savePath = Settings.CurrentData.SavePath;
    string text = Path.Combine(savePath, "saves");
    ...
}
```

Assigning `Settings.CurrentData.SavePath` at runtime before creating a world therefore redirects
every write to a scratch directory. The change is in memory; the game persists settings on a clean
exit, so restore it or exit hard. Verified: a world created after the redirect wrote only into the
scratch tree, and the real `saves` folder kept its original timestamp.

The world ids accepted by the `new` console command are `Europa3`, `Lunar`, `Mars2`,
`MimasHerschel`, `Venus`, `Vulcan (Deprecated)`, `Vulcan2`. `Moon` is not one of them, despite the
Lunar world's display name being "Moon: Great Mare".

## Verification history

- 2026-08-09: corrected the framing of the `System.Collections.Immutable` exception. It is
  observationally log spam: the developer hosts with both mods enabled and a second player joins
  normally while it fires. The mechanism invites a stronger conclusion, and the missing step is
  that `GameManager.Managers` is Unity-serialized rather than built in code, so the manager order
  is not in the decompile and "throws before the network pump" was never established. The
  single correlated observation that a stalled join succeeded after adding the assembly to
  `BepInEx/core/` is not evidence of cause; the transient StationeersLaunchPad Workshop hang
  explains the same pair of events. The assembly has been removed from the developer's install.
- 2026-07-27: page created from the ClientDriver build-out. Every section exercised against a live
  client on 0.2.6403.27689 with StationeersLaunchPad 0.5.0 and 35 Workshop mods loaded: console
  capture, console command execution, synthetic keys (F3 toggling the console, observed through
  `ConsoleWindow.IsOpen`), mouse wheel driving SprayPaintPlus colour cycling, teleport, look,
  look-at, spawn into hand, structure placement, `AttackWith` painting a wall, backbuffer
  screenshots at the menu and in world, forced LaunchPad settings panel, and a live ConfigEntry
  write reflected in the mod's computed value.
- 2026-07-30: added "The gameplay input gate" and "Window size and fullscreen come from the game, not
  the command line". Both are additive; nothing previously on the page was contradicted. The "Input
  injection" section was re-read against the current decompile and re-confirmed (there is no cached
  key state to sit under; `KeyWrap.PollForInput` calls `UnityEngine.Input` live), so it was
  restamped rather than changed. The gate section supplies the missing precondition for the
  already-recorded "frames=1 steps the colour index by exactly 1 per call" result: it holds only
  while `InventoryManager.ManagerUpdate` gets past its `Cursor.visible` early return.
- 2026-07-27: corrected the StationeersLua / ScriptedScreens entry. It was first written as "fires
  once multiplayer is entered", based on occurrence counts in `BepInEx/LogOutput.log`. That file
  collapses the repeats and is not a usable measure. Re-measured against the game's own console ring
  with the plugin's `UnityEngine.Input` patches disabled: the exception fires continuously with no
  join involved and with no input patching in the process.
- 2026-08-11: conflict on "how the `ScriptedScreens` static ctor reaches MessagePack". Previous
  claim: the ctor pulls `McpMultiplayerDebugProxy`, which pulls MessagePack. New finding: the ctor
  reaches MessagePack directly in one hop, and `McpMultiplayerDebugProxy` is a `StationeersLua` type
  that `ScriptedScreens.dll` does not reference at all. Fresh validator verdict: the new finding is
  correct, established from the assembly TypeDef and TypeRef tables via `PEReader` / `MetadataReader`
  rather than a text search, and corroborated against the `.cctor` IL. The name appears in neither
  table of `ScriptedScreens.dll`; it is a TypeDef in `StationeersLua.dll`
  (`StationeersLua.McpServer.McpMultiplayerDebugProxy`). `ScriptedScreens.dll` references only three
  `StationeersLua` types (`LuaMcpRegistry`, `McpServer.McpEditorContext`, `McpServer.McpServerConfig`),
  none of them touched by the ctor. Result: the chain was corrected in place and the separate
  `StationeersLua` per-frame site recorded alongside it. The rest of the entry, including the
  `KeyWrap_PollForInput_Prefix` rethrow path, was confirmed unchanged.

## Open questions

- A Direct Connect to this repo's dedicated server reaches the server (`A connection is incoming`,
  `VerifyPlayer - Serialising connection method: RocketNet`) but never completes: the client stays
  at `GameState.Joining` / `NetworkState.WaitingForConnection` for at least 180 seconds with no
  further console output, on a client whose mod set is not identical to the server's. Whether this
  is a mod-list mismatch, a verify-handshake stall, or something specific to that server's state
  was not determined. The client side of the join was verified as far as the game itself controls.
- Why the `System.Collections.Immutable` load fails when the correct assembly is both shipped and
  loaded. It is neither a packaging bug nor a StationeersLaunchPad gap, which is what this entry
  used to ask: both mods ship the exactly-correct 8.0.0.0 assembly and StationeersLaunchPad does
  load it, so the loaded copy is present in the domain when the reference fails to bind. Whether
  Mono suppresses the managed `AssemblyResolve` event on the field-type-loading path, or caches the
  negative reference per image, is unresolved and needs a runtime probe rather than more
  decompiling. Detail:
  [Patterns/ModDependencyAssemblyResolution.md](../Patterns/ModDependencyAssemblyResolution.md).
