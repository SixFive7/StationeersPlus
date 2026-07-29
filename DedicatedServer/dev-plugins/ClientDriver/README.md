# ClientDriver

Developer tooling. An in-process control plane for the Stationeers **game client**, exposed as a loopback HTTP API so an agent can read the in-game console, connect to a server, inspect state, inject input, spawn things, take screenshots, and read and write mod config with nobody at the keyboard.

It is the client-side counterpart to `ScenarioRunner` (which probes the dedicated server) and `InspectorPlus` (which dumps scene state to JSON on request). Where those two answer "what is the simulation doing", ClientDriver answers "make the client do a thing, then tell me what happened".

Not a player-facing mod. It lives here next to the dedi launcher, ships as a single DLL, and never gets a Workshop handle.

## Why an in-process HTTP server

Faking OS input into a Unity game needs the window focused, and Unity ignores synthetic events when it is in the background. Everything here instead calls the game's own methods or patches `UnityEngine.Input` from inside the process, so it is deterministic and works while the window sits unfocused behind a terminal. Requests are synchronous: engine work is marshalled onto the Unity main thread and the HTTP response waits for it.

The transport is a raw `TcpListener` speaking minimal HTTP/1.1, not `HttpListener`. `HttpListener` on the Microsoft CLR goes through http.sys and needs a URL ACL reservation or elevation; the socket has no such dependency and behaves the same everywhere.

## The driver never touches window focus

**No code here may focus, raise, or activate the game window.** No `SetForegroundWindow`, no `AttachThreadInput`, no `ShowWindow`, no `user32.dll` P/Invoke of any kind. The plugin contains none today and must not grow any: `System.Runtime.InteropServices` is not imported anywhere in this project, and that is the invariant to preserve.

This is not a style preference. Working unfocused is the entire reason the in-process design was chosen over synthetic OS input, so reaching for focus abandons the guarantee the tool exists to provide. It also does not work: a run on 2026-07-28 tried plain `SetForegroundWindow` and then an `AttachThreadInput` variant, both lost to Windows' foreground lock, and one of them interrupted the developer, who was using the machine at the time.

If something appears to need focus, the answer is to find the in-process method that does the job, or to report the capability as blocked. The item-pickup problem that prompted those attempts was ultimately solved from the other side entirely, by the `give-item` scenario in ScenarioRunner, which hands the item over from the server and never involves the client at all.

## Install

This plugin deploys to the **client**, not the dedicated server, so `dedicated-server.ps1 -DeployMods` does not apply. Install it by hand:

```powershell
dotnet build DedicatedServer/dev-plugins/ClientDriver/ClientDriver.sln -c Release
# copy the DLL into the client's BepInEx plugin folder ($(StationeersPath) from Directory.Build.props)
New-Item -ItemType Directory -Force -Path "<StationeersInstall>\BepInEx\plugins\ClientDriver"
Copy-Item DedicatedServer\dev-plugins\ClientDriver\ClientDriver\bin\Release\ClientDriver.dll `
          "<StationeersInstall>\BepInEx\plugins\ClientDriver\" -Force
```

`BepInEx/plugins/` is loaded by the BepInEx Chainloader directly, before StationeersLaunchPad runs. Do not also place it under the StationeersLaunchPad mod folder: two loaders means `Awake` twice and every Harmony patch registered twice.

Uninstall is deleting that folder plus `BepInEx/config/net.clientdriver.cfg`.

## Run

Launch the client any way you like (`steam://rungameid/544550`, or `rocketstation.exe` with Steam running). The control plane comes up during BepInEx chainload, well before the main menu.

```powershell
Invoke-RestMethod http://127.0.0.1:27700/ping
Invoke-RestMethod http://127.0.0.1:27700/status
Invoke-RestMethod http://127.0.0.1:27700/help
```

Readiness has three distinct stages and they matter:

| Signal | Meaning |
|---|---|
| `/ping` answers | BepInEx loaded the plugin. The game is still booting. |
| `/status.loadedPluginCount > 10` | StationeersLaunchPad has finished loading Workshop mods. |
| `/status.gameInitialized == true` and `phase == "menu"` | The splash screen is gone and the main menu is actually up. |

Wait for the third before doing anything that touches the menu or the ImGui overlay. `loadedPluginCount` alone is not enough; the splash screen is still drawing at that point and it suppresses the in-game ImGui windows.

## Configuration

`BepInEx/config/net.clientdriver.cfg`, section `Client - Control Plane`:

| Key | Default | What it does |
|---|---|---|
| `Port` | `27700` | TCP port, bound to `127.0.0.1` only. Clear of Steam (27000-27050), the Stationeers client (27015/27016) and this repo's dedicated server (28015/28016). |
| `Enabled` | `true` | Master switch. When false the plugin loads, patches nothing, and opens no socket. |
| `Allow Input Injection` | `true` | When false the Unity input patches still load but every query falls through to real hardware, so the driver can never fight the developer's keyboard. |
| `Patch Unity Input` | `true` | When false the `UnityEngine.Input` patches are never applied at all, so `/input/*` stops working. Diagnostic only: it is how you rule this plugin out when another mod misbehaves somewhere on the input path. |

## Endpoints

Every body field can also be passed as a query parameter, so anything is reachable from a browser or plain `curl`. `GET /help` returns this list at runtime.

### State

| Endpoint | Notes |
|---|---|
| `GET /ping` | Liveness plus frame counter. Never touches the main thread, so it answers even if the game is wedged. |
| `GET /status` | Everything: game state, network role, world, player, driver counters. |
| `GET /player` | Player block only. |
| `GET /colors` | `GameManager.CustomColors` catalogue with swatch indices. |
| `GET /plugins` | Every plugin found by assembly scan, with its assembly path. |
| `GET /nearby?radius=&filter=&limit=` | Things around the player. |

### Console

| Endpoint | Notes |
|---|---|
| `GET /console/log?since=&limit=&contains=&source=` | Sequence-numbered tee of the in-game console and the BepInEx log. Poll with `since=<nextSeq>` for an incremental stream. `source=console` or `source=bepinex` to split them. |
| `POST /console/clear` | Empty the tee. |
| `GET /console/buffer?limit=&contains=` | The game's own 1024-line console ring, newest first. Covers lines printed before this plugin loaded and the block/table printers that bypass `Print`. |
| `POST /console/exec` | `{command, waitFrames, waitMs}`. Runs a console command and returns the lines it produced. |
| `POST /console/print` | `{text, level=action\|error\|info}`. Writes a marker line, handy for bracketing a test. |
| `GET /console/commands?contains=` | Registered console command names. |

### Session

| Endpoint | Notes |
|---|---|
| `POST /connect` | `{address, port, wait, timeoutMs, suppressTimeout}`. Direct Connect. |
| `POST /disconnect` | `{wait, timeoutMs}`. Leave to the main menu. |
| `POST /quit` | `{hard}`. `Application.Quit()`, or `GameManager.QuitGame()` (a `Process.Kill`) when `hard`. |
| `GET /saves` | Local save list. |
| `POST /load` | `{save, wait, timeoutMs}`. Load a save by name. |
| `POST /newworld` | `{world, difficulty, start, wait, timeoutMs}`. World ids are `Lunar`, `Mars2`, `Europa3`, `MimasHerschel`, `Venus`, `Vulcan2`. Not `Moon`. |
| `POST /waitfor` | `{phase=menu\|joining\|loading\|inWorld, timeoutMs}`. |
| `GET/POST /savepath` | `{path}` redirects the user-data root so a driven session writes its worlds somewhere other than the developer's real save folder. GET reads the current value. |

### Input

| Endpoint | Notes |
|---|---|
| `POST /input/key` | `{key, mode=tap\|down\|up, frames, wait}`. `key` is a `KeyCode` name (`LeftShift`, `F3`, `Mouse0`) or a `KeyMap` action name (`PrimaryAction`, `SwapHands`, `ToggleConsole`), resolved against the live binding rather than a hardcoded default. |
| `POST /input/scroll` | `{notches, frames=1, repeat, gapFrames, wait}`. |
| `POST /input/mouse` | `{button, mode, frames}`. Alias for `Mouse0`/`Mouse1`. |
| `POST /input/releaseall` | End every held key. |
| `POST /input/clear` | Drop all synthetic input state. |
| `GET /input/keymap` | Every `KeyMap` action and its current binding. |
| `POST /input/enable` | `{enabled}`. Master switch for injection. |
| `POST /input/mouseposition` | `{x, y}` or `{clear:true}`. |

### Player

| Endpoint | Notes |
|---|---|
| `POST /player/teleport` | `{position:[x,y,z]}`, `{x,y,z}` or `{offset:[dx,dy,dz]}`. |
| `POST /player/look` | `{yaw, pitch}` or `{at:[x,y,z]}`. |
| `POST /player/use` | `{targetId}` or `{cursor:true}`. Uses the held item on a target by reference id, no aiming required. |
| `POST /player/swaphands` | Swap active and inactive hand. |

### Spawning

| Endpoint | Notes |
|---|---|
| `POST /spawn/hand` | `{prefab}`. Straight into the active hand. Needs simulation authority, so host or single player. |
| `POST /spawn/world` | `{prefab, position\|offset\|distance, viaServer}`. On a client it routes through `OnServer.SpawnDynamicThingMaxStack`, which forwards to the server. |
| `POST /spawn/structure` | `{prefab, position\|offset\|distance, yaw, colorIndex}`. Goes through `Constructor.SpawnConstruct`, which is client-safe. |
| `GET /prefabs?contains=&type=&limit=` | Prefab catalogue. |

### UI

| Endpoint | Notes |
|---|---|
| `GET /modsettings/list` | Every mod StationeersLaunchPad loaded, with `Name` and `Id`. |
| `POST /modsettings` | `{mod, show}`. Forces that mod's LaunchPad settings panel on screen so `/screenshot` can read it. See the gotcha below. |
| `GET /modal` | Is a confirmation dialog showing, and what does it say. |
| `POST /modal/click` | `{button=1\|2\|3}`. Dismisses it and runs that button's callback. |
| `POST /cursor/force` | `{targetId}` or `{clear:true}`. Pins what the cursor reports, target and collider together. Refuses a target it cannot find a collider for; see the gotcha below. `clear` also resets the game's own cursor fields, so it recovers a stale cursor rather than only dropping the pin. |
| `GET /screenshot?path=&supersize=&maxWidth=&inline=` | PNG of the full backbuffer, UI included. |

### Config and reflection

| Endpoint | Notes |
|---|---|
| `GET /config?guid=&filter=` | Every `ConfigEntry` of a loaded plugin. |
| `POST /config/set` | `{guid, section, key, value, save}`. Writes the live `ConfigEntry`, which takes effect immediately with no restart. |
| `POST /config/reload` | `{guid}`. Re-read the `.cfg` from disk. |
| `GET /reflect?type=&member=` | Read any static field or property by full type name. Unwraps a `ConfigEntry<T>` to its value. |
| `GET /reflect/members?type=` | Every static member of a type with its runtime value type. The diagnostic of last resort. |

## Gotchas

Everything below was hit for real on 0.2.6403.27689 with StationeersLaunchPad 0.5.0.

**The BepInEx plugin component is destroyed during boot.** `OnDestroy` fires on the ClientDriver `MonoBehaviour` about a minute into startup while the process keeps running, and `Chainloader.PluginInfos[...].Instance` is null for every plugin thereafter. The first build stopped its listener from `OnDestroy` and the control plane silently died a minute after launch. The server is therefore owned by a static, is never torn down from `OnDestroy`, and a watchdog thread re-binds if the socket ever goes away. `/status.driver.pluginDestroyCount` reports it. Do not put anything load-bearing in the plugin component's lifecycle.

**The main-thread pump cannot live on our own GameObject alone.** The pump `GameObject` is destroyed and recreated during boot too (`/status.driver.pumpObjectCreations` is normally 2). The primary pump is a postfix on `ImGuiManager.LateUpdate`, which runs every frame from the splash screen onwards and belongs to the game. `MonoBehaviour.Update` and an `ElectricityManager.ElectricityTick` postfix are secondary and tertiary.

**StationeersLaunchPad mods are invisible to `Chainloader.PluginInfos`.** It only ever lists what BepInEx loaded out of `BepInEx/plugins/`, which is this plugin plus StationeersLaunchPad. Every Workshop mod arrives another way. `/config` and `/plugins` therefore resolve plugins by scanning loaded assemblies for `[BepInPlugin]` and reaching the `ConfigFile` through any static `ConfigEntry` on the plugin type, which works whether or not the component is still alive.

**A failed Steam Workshop query parks the client forever.** When StationeersLaunchPad's `FetchWorkshopPage` throws (a transient Steamworks `NullReferenceException`), it prints "Mods failed to load. Game may not function properly" and sits on its own ImGui screen with a Start Game button, never reaching the menu. `/status.loadedPluginCount` stays at 2. Detect it and relaunch; it clears on retry. `/screenshot` is how this was diagnosed in the first place.

**The join has a 10 second timer that a modded server cannot beat.** `NetworkClient.OnJoinStart` arms a timer whose only job is to give up and pop a modal. The handshake reaches the server (`A connection is incoming` in the server log) and then the client cancels itself mid-transfer. `/connect` calls `NetworkClient.StopConnectionTimer()` immediately after `JoinClientFromMenu` (`suppressTimeout`, on by default) and uses its own timeout instead. If a dialog appears anyway, `/connect` reads it, clicks OK, and reports the text under `dialog`.

**`NetworkClient` is not findable for the first minute.** `FindObjectOfType` only sees active components. `/connect` falls back to `Resources.FindObjectsOfTypeAll` and waits for the object to appear rather than failing outright.

**One scroll frame is one notch.** Wheel consumers act once per frame, so `frames=2` moves a spray can two colours, not one. `frames` defaults to 1 for this reason. `repeat` with the default `gapFrames` is the way to travel several steps.

**`/modsettings` needs the real main menu, not just loaded mods.** The panel draws from a prefix on `OrbitalSimulation.Draw`, and `ImGuiManager.RenderOverlay` skips that entirely while the splash screen is up. Wait for `gameInitialized == true`.

**`ConfirmationPanel.IsVisible` lies during boot.** It is just `gameObject.activeInHierarchy`, which is true for a window early in startup with an empty data stack behind it. `/modal` reports `visible` only when there is actual dialog data, and exposes the raw flag as `panelActive`.

**Screenshots are big.** The backbuffer here is 3840x2160, which encodes to about 6 MB of PNG. `maxWidth` defaults to 1920 and GPU-downscales before encoding; pass `maxWidth=0` for the full thing.

**A forced cursor without a collider kills the client, permanently.** The cursor is a tuple, not one field: `CursorManager.SetCursorTarget` always writes `FoundThing` and `Instance.CursorTargetCollider` together, and the collider is null on a raycast miss and null while the console is open. Pinning only `FoundThing`, which the first version of `/cursor/force` did, produces `{FoundThing = X, CursorTargetCollider = null}`, a pair the game itself can never produce. `PlantAnalyserCartridge.GetScannedPlant` then calls `Thing.GetSlot(null)`, whose dictionary is eagerly constructed and so throws on every Thing. That exception aborts `GameManager.Update` before the manager loop reaches `CursorManager.ManagerUpdate`, the only caller of `SetCursorTarget`, so the stale target survives and throws again next frame, forever. `NetworkManager.ManagerUpdate` is in that same loop, so a wedged client also stops processing network packets. Measured at 100 exceptions per 6 seconds; only leaving the world recovered it. `/cursor/force` now pins the collider and `FoundTerrain` alongside the target and refuses a target with no reachable collider, and `clear` writes the game's three fields directly so it recovers a stale cursor rather than only dropping the pin. Full state inventory in `Research/GameSystems/CursorManager.md`.

**Prefer the server-side `give-item` scenario to anything cursor-shaped.** ScenarioRunner puts a prefab into a connected player's hand with the client's cursor out of the loop entirely. `/cursor/force` is for the cases where the code under test genuinely reads `CursorManager.CursorThing`, such as the spray-can eyedropper.

**The console tee merges two streams.** `GET /console/log` returns both the game console and the BepInEx log, and a mod line that goes to both appears twice, so a naive count doubles. Pass `source=console` when counting what a player would actually see.

## Keeping a driven session out of the real save folder

`POST /savepath {"path": "..."}` points `Settings.CurrentData.SavePath` at a scratch directory. Every save the game writes resolves through `StationSaveUtils.GetSavePath()` on each call, so worlds created after the redirect land there instead of the developer's `Documents\My Games\Stationeers\saves`. The change is in memory; the game persists settings on a clean exit, so put it back at the end of a session or exit hard.

## Layout

```
ClientDriver.sln
ClientDriver/
  Plugin.cs             plugin entry, patch application, server lifecycle, watchdog, frame pumps
  Router.cs             HTTP path to engine work
  HttpServer.cs         TcpListener HTTP/1.1
  MainThreadPump.cs     background thread to Unity main thread, synchronously
  ConsoleTap.cs         Harmony tee of ConsoleWindow.Print plus a BepInEx log listener
  InputDriver.cs        synthetic keyboard, mouse and wheel at the UnityEngine.Input layer
  StateReporter.cs      live state to JSON
  Screenshot.cs         backbuffer capture and downscale
  ConfigAccess.cs       live ConfigEntry read and write, plugin discovery, reflection
  ModSettingsPanel.cs   forces a StationeersLaunchPad mod settings panel on screen
  Modal.cs              reads and dismisses confirmation dialogs
  Json.cs               minimal JSON reader and writer
  About/About.xml
```
