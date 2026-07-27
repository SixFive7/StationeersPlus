---
title: Driving the game client programmatically
type: Workflows
created_in: 0.2.6403.27689
verified_in: 0.2.6403.27689
verified_at: 2026-07-27
sources:
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.Human.decompiled.cs
  - .work/decomp/0.2.6403.27689/StationeersLaunchPad.decompiled.cs
  - DedicatedServer/dev-plugins/ClientDriver/
related:
  - ../Workflows/InspectorPlusUsage.md
  - ../GameClasses/ColorSwatch.md
tags: [client, automation, input, networking, multiplayer, screenshots, bepinex, harmony, launchpad]
---

Everything a BepInEx plugin needs in order to drive the Stationeers **client** with nobody at the
keyboard: the seams for input, joining a server, spawning, screenshots and live mod config, plus
the boot-order and lifecycle traps that make a naive implementation fail silently. All of it was
exercised against a live client on 0.2.6403.27689; the implementation is
`DedicatedServer/dev-plugins/ClientDriver/`.

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
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

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
<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

**Transient Steam Workshop failure parks the client forever.** When
`StationeersLaunchPad.Steam.FetchWorkshopPage` throws (a `NullReferenceException` out of
`Steamworks.Helpers.TakeMemory` inside `ISteamUGC.GetQueryUGCPreviewURL`), StationeersLaunchPad
prints "Error occurred during initialisation. Mods will not be loaded.", shows its own ImGui screen
with a Start Game button, and never reaches the menu. Loaded plugin count stays at 2. It clears on
relaunch; an unattended loop needs to detect it and retry.

**`ConfirmationPanel.IsVisible` is a false positive during boot.** It is
`gameObject.activeInHierarchy`, which is true for a window early in startup with an empty
`_dataStack` behind it. Treat a dialog as showing only when the stack has data.

**`StationeersLua` and `ScriptedScreens` throw an exception every single frame.** Their
`ScriptedScreensScriptableUiSystem` static ctor pulls `McpMultiplayerDebugProxy`, which pulls
MessagePack, which fails with
`Could not load file or assembly 'System.Collections.Immutable, Version=8.0.0.0'`. Once the ctor
fails, every later access rethrows `TypeInitializationException`, and it rethrows from
`ScriptedScreensBehaviorPatch.KeyWrap_PollForInput_Prefix` -> `KeyMap.PollInputs` ->
`KeyManager.ManagerUpdate` -> `GameManager.Update`, i.e. every frame for the rest of the session,
at the main menu and in world alike. It floods the console ring hard enough to evict thousands of
lines. Disabling both mods removes the exception but does not by itself fix a stalled join.

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

- 2026-07-27: page created from the ClientDriver build-out. Every section exercised against a live
  client on 0.2.6403.27689 with StationeersLaunchPad 0.5.0 and 35 Workshop mods loaded: console
  capture, console command execution, synthetic keys (F3 toggling the console, observed through
  `ConsoleWindow.IsOpen`), mouse wheel driving SprayPaintPlus colour cycling, teleport, look,
  look-at, spawn into hand, structure placement, `AttackWith` painting a wall, backbuffer
  screenshots at the menu and in world, forced LaunchPad settings panel, and a live ConfigEntry
  write reflected in the mod's computed value.
- 2026-07-27: corrected the StationeersLua / ScriptedScreens entry. It was first written as "fires
  once multiplayer is entered", based on occurrence counts in `BepInEx/LogOutput.log`. That file
  collapses the repeats and is not a usable measure. Re-measured against the game's own console ring
  with the plugin's `UnityEngine.Input` patches disabled: the exception fires continuously with no
  join involved and with no input patching in the process.

## Open questions

- A Direct Connect to this repo's dedicated server reaches the server (`A connection is incoming`,
  `VerifyPlayer - Serialising connection method: RocketNet`) but never completes: the client stays
  at `GameState.Joining` / `NetworkState.WaitingForConnection` for at least 180 seconds with no
  further console output, on a client whose mod set is not identical to the server's. Whether this
  is a mod-list mismatch, a verify-handshake stall, or something specific to that server's state
  was not determined. The client side of the join was verified as far as the game itself controls.
- Whether the `System.Collections.Immutable` load failure in `StationeersLua` / `ScriptedScreens` is
  a packaging bug in those mods or a StationeersLaunchPad assembly-resolution gap. Both mods ship
  the DLL in their own Workshop folder and it is still not resolved at runtime.
