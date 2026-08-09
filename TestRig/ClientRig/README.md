# Client Rig

Developer tooling. Provisions and drives N isolated Stationeers **game clients** on one machine, so an agent can run a real multiplayer test with nobody at the keyboard.

Two pieces:

- **`ClientDriver`**, a BepInEx plugin, is the control plane inside each instance. It exposes a loopback HTTP API for reading the in-game console, connecting to a server, inspecting state, injecting input, spawning, screenshots, and reading and writing mod config.
- **`client-rig.ps1`** is the launcher. It provisions instances (hard-linked from the real install), creates the isolated Win32 desktop, starts and stops them, and fans one command out across the rig.

It is the client-side counterpart to `ScenarioRunner` (which probes the dedicated server) and `InspectorPlus` (which dumps scene state to JSON on request). Where those two answer "what is the simulation doing", this answers "make these clients do a thing, then tell me what actually happened".

**Not a player-facing mod, and it must never ship.** It is a remote control plane for the game. `WorkshopHandle` is 0 and stays 0.

---

## Design note: where the boundary sits

The split between the plugin and the launcher is **process creation**.

The launcher owns everything outside a game process, and everything that has to keep working when a process is dead, wedged, or not yet born: laying down an instance tree, creating the desktop, starting, killing, PID files, and the fan-out. The plugin owns everything inside a process, which is everything that needs the Unity main thread or the game's own types: input, state, config, the cursor gate, identity, the window.

There is no third category, and the two halves never overlap. Two consequences fall out of that and are worth stating because they are what make the shape correct rather than merely tidy:

**The fan-out lives in the launcher, not in the game.** Every interesting multi-client test is "set up A, set up B, act as A, observe both", and the interesting failures are ordering failures. A coordinator that lives inside one instance cannot supervise a barrier that another instance has stopped participating in, because a wedged client cannot report that it is wedged. The launcher is outside all of them and can.

**The per-instance manifest is written by the launcher and read by the plugin.** One writer, one reader, one file. Configuration used to live in three unconnected places (`net.clientdriver.cfg` for the port and identity, `stationeers.launchpad.cfg` for the save path, the command line for the rest), nothing tied them together, and two running instances produced `/status` blobs that were indistinguishable apart from the identity fields. Now `/status` leads with `instanceName`, and `/instance` answers the whole question.

---

## Setup

### 1. Pick a location for the instance trees

An instance is a **hard-linked** copy of the real install, so it costs a few megabytes instead of seven gigabytes. Hard links cannot cross NTFS volumes, so the instance trees must be on the same drive as the game install. The repository often is not.

```powershell
# once per shell, or record it in DEV.md
$env:STATIONEERS_CLIENTRIG_ROOT = '<drive of the game install>\StationeersRig'
```

The launcher refuses with the exact command to fix it if this is wrong, rather than quietly making a 7 GB copy. Per-instance state (manifest, `setting.xml`, save root, logs, PID file) is ordinary files rather than links, so it stays under `data/` beside the script regardless.

### 2. Build the plugin

```powershell
dotnet build TestRig/ClientRig/ClientDriver.sln -c Release
```

Provisioning copies whatever is in `bin/Release` into the instance. After a plugin change, rebuild and re-provision with `-Force`.

### 3. Provision instances

```powershell
.\client-rig.ps1 -Provision -Instance client1
.\client-rig.ps1 -Provision -Instance client2
```

Port and identity default off the instance index, so two instances with no flags get 27701/27702 and ClientIds 900000000001/900000000002 with no collision. Override with `-Port`, `-ClientId`, `-Username`, `-Width`, `-Height`.

Provisioning refuses a duplicate ClientId or port up front. That is not fussiness: the server keys a player's body on ClientId, `Brain.RegisterBrain` overwrites silently, and two clients sharing an id resolve onto **one character** with nothing anywhere warning. A test that believes it has two players and has one produces results that look plausible and mean nothing.

What each instance gets:

| Thing | How |
|---|---|
| `rocketstation_Data`, `MonoBleedingEdge`, the engine binaries | NTFS hard links. Around 1,050 of them, near-zero new disk. |
| `doorstop_config.ini`, `Fixing The Controls modifiers.ini`, `app.info` | Real copies. A mod writes to them, and a hard link would reach back into the developer's install. |
| `BepInEx/` | Real copy, about 2.7 MB. Own config, plugins, cache, `LogOutput.log`, and its own `inspector/` request and snapshot folders. |
| `ClientDriver.dll` | Copied into `BepInEx/plugins/ClientDriver/`. |
| Local mods | Copied into the instance's own save root, with `modconfig.xml` repointed at the copy and StationeersLaunchPad's `SavePathOverride` set. Skip with `-SeedMods:$false`. |
| `imgui.ini`, `output_log.txt` | Not carried. Regenerated, and resolved against the working directory. |

The source install is read-only throughout. `-Remove` deletes only links and per-instance copies.

---

## Run

```powershell
.\client-rig.ps1 -Start -All             # on the isolated desktop; never takes your foreground
.\client-rig.ps1 -Wait  -All -Stage menu # barrier across the rig; roughly 100 s from cold
.\client-rig.ps1 -Status -All
```

Then drive them:

```powershell
.\client-rig.ps1 -Call -Instance client1 -Path /connect -Body '{"address":"127.0.0.1","port":28016}'
.\client-rig.ps1 -Wait -All -Stage inWorld -TimeoutSeconds 600
.\client-rig.ps1 -Snapshot -All -OutFile before.json
```

Or talk to one directly, which is often easier when exploring:

```powershell
Invoke-RestMethod http://127.0.0.1:27701/status
Invoke-RestMethod http://127.0.0.1:27701/help
```

Teardown:

```powershell
.\client-rig.ps1 -Stop -All
.\client-rig.ps1 -Remove -Instance client1
```

### Readiness has three distinct stages and they are not interchangeable

| `-Stage` | Means |
|---|---|
| `ping` | BepInEx loaded the plugin. The game is still booting. |
| `modsLoaded` | `loadedPluginCount > 10`: StationeersLaunchPad finished loading Workshop mods. |
| `menu` | `gameInitialized == true` and `phase == "menu"`. The splash screen is gone and the menu is actually up. |
| `inWorld` | `phase == "inWorld"`. |

Wait for `menu` before touching the menu or the ImGui overlay. `modsLoaded` alone is not enough: the splash screen is still drawing at that point and it suppresses the in-game ImGui windows.

### The launcher actions

| Action | Does |
|---|---|
| `-Provision -Instance <n> [-Force]` | Build or rebuild an instance tree, seed its mods, write its manifest. |
| `-Start -Instance <n>\|-All` | Launch on the isolated desktop. |
| `-Stop -Instance <n>\|-All` | Ask through `/quit`, then kill after `-TimeoutSeconds` (default 30). |
| `-Status [-Instance <n>]` | Process, port, identity, phase, foreground verdict, input gate, identity conflicts. |
| `-List` | The rig registry as a table. |
| `-Remove -Instance <n>` | Delete the tree. Refuses while it is running. |
| `-Wait -All -Stage <s>` | Barrier. Fails loudly, per instance, with what each one was actually doing. |
| `-Broadcast -All -Path <p> [-Body <json>]` | One request to every instance. Throws on a partial result. |
| `-Call -Instance <n> -Path <p> [-Body <json>]` | One request to one instance. |
| `-Snapshot -All [-OutFile <f>]` | `/status` from every instance in one document. |
| `-Logs -Instance <n> [-Tail N] [-Grep <re>]` | That instance's BepInEx log. |

`-Broadcast` throws when any instance failed, deliberately. A partial broadcast leaves the rig in mixed state, and "both clients agree on X except for this one difference" is the shape of nearly every paired check, so half-applying it silently is how a test comes out wrong.

---

## The rig never touches your foreground

**No code here may focus, raise, or activate a game window.** No `SetForegroundWindow`, no `AttachThreadInput`, no `ShowWindow`, no `SetWindowPos`, no `SwitchDesktop`, nothing that CHANGES window state.

The rule used to be stated as "no `user32` P/Invoke of any kind", which was a proxy. It is now stated as the real rule, because read-only exceptions earn their place: `NativeWindow.cs` imports `GetForegroundWindow`, `GetWindowThreadProcessId`, `GetThreadDesktop`, `OpenInputDesktop` and `GetUserObjectInformationW` so `/status` can be honest about where the window is. Reading which window holds the foreground activates nothing. That file is the only place `System.Runtime.InteropServices` may appear in the plugin, and only for observation. In the launcher, `CreateProcessW` and `CreateDesktopW` are the only imports, and `SwitchDesktop` is deliberately absent.

Working unfocused is the entire reason the in-process design was chosen over synthetic OS input, so reaching for focus abandons the guarantee the tool exists to provide. It also does not work: a run tried plain `SetForegroundWindow` and then an `AttachThreadInput` variant, both lost to Windows' foreground lock, and one of them interrupted the developer, who was using the machine at the time.

**The separate desktop is the mechanism, not an optimisation.** `SW_SHOWNOACTIVATE` alone loses 40 focus steals out of 40 samples; a separate desktop loses 0 out of 55. See `RESEARCH.md`.

---

## Configuration

The manifest at `data/<instance>/instance.json` is written by the launcher and is the source of truth. It **wins over** the BepInEx config for every value it carries, because it is rewritten on every provision and therefore describes this run, whereas a `.cfg` is sticky across sessions and a mod or an earlier run can persist a value into it behind your back. `/instance` reports `valueSources` so which one won is never a guess.

`BepInEx/config/net.clientdriver.cfg` still works, and is what a lone client with no manifest uses.

Section `Client - Control Plane`:

| Key | Default | What it does |
|---|---|---|
| `Port` | `27700` | TCP port, bound to `127.0.0.1` only. Clear of Steam (27000-27050), the Stationeers client (27015/27016) and this repo's dedicated server (28015/28016). |
| `Enabled` | `true` | Master switch. When false the plugin loads, patches nothing, and opens no socket. |
| `Allow Input Injection` | `true` | When false the Unity input patches still load but every query falls through to real hardware, so the driver can never fight the developer's keyboard. |
| `Patch Unity Input` | `true` | When false the `UnityEngine.Input` patches are never applied. Diagnostic only: it is how you rule this plugin out when another mod misbehaves on the input path. |

Section `Client - Console Tee`:

| Key | Default | What it does |
|---|---|---|
| `Max Lines Per Source` | `2000` | Ring capacity per source. Evictions are counted in `dropped`. |
| `Max Characters Per Line` | `4000` | Longer lines are truncated with a marker and counted in `truncated`. 0 disables. |
| `Max Characters Per Source` | `4194304` | Total budget per source. This is the cap that actually holds when lines are large. 0 disables. |

Section `Client - Identity`:

| Key | Default | What it does |
|---|---|---|
| `Client Id` | empty | Decimal ulong to present, replacing the cookie's. Every concurrent instance needs a different value. |
| `Username` | empty | Player name to present. |
| `Lock Cookie File` | `false` | Suppress `PlayerCookie.Save()` even with no override. An identity override already implies this. |

Section `Client - Window`:

| Key | Default | What it does |
|---|---|---|
| `Force Windowed` | `false` | Keeps the instance in a window of the configured size. Necessary because `-screen-fullscreen 0` does not survive boot; see `RESEARCH.md`. Never writes to the shared PlayerPrefs registry key. |
| `Window Width` | `800` | |
| `Window Height` | `600` | |

Section `Client - Gameplay Input`:

| Key | Default | What it does |
|---|---|---|
| `Force Gameplay Input` | `false` | Holds the cursor locked and hidden from a prefix on `InventoryManager.ManagerUpdate`, so per-frame gameplay input consumers keep running in an unfocused window. **Without this, `/input/*` is delivered and then discarded.** Off by default because it takes the mouse cursor away from a real player. Provisioned instances get it on. |
| `Force Gameplay Input Everywhere` | `false` | Assert the gate outside a loaded world too. By default the gate is scoped to `GameState.Running` and yields to confirmation dialogs, because holding the cursor hidden in a menu leaves nothing clickable. |

---

## Endpoints

Every body field can also be passed as a query parameter, so anything is reachable from a browser or plain `curl`. **A query parameter is the reliable way to send a Windows path**, because it is percent-decoded by the HTTP layer and never goes through the JSON string reader. `GET /help` returns this list at runtime.

### Instance and state

| Endpoint | Notes |
|---|---|
| `GET /ping` | Liveness plus frame counter. Never touches the main thread, so it answers even if the game is wedged. |
| `GET /instance` | Name, port, identity, manifest path, which source each value came from, sibling ports, and the duplicate-ClientId verdict. `rescan=true` forces a fresh peer probe. |
| `GET /status` | Everything: instance, game state, network role, world, player, foreground, input gate, driver counters. |
| `GET /player` | Player block only. |
| `GET /colors` | `GameManager.CustomColors` catalogue with swatch indices. |
| `GET /plugins` | Every plugin found by assembly scan, with its assembly path. |
| `GET /nearby?radius=&filter=&limit=` | Things around the player. |

### Console

| Endpoint | Notes |
|---|---|
| `GET /console/log?since=&limit=&contains=&source=` | Sequence-numbered tee of the in-game console and the BepInEx log, with `dropped`, `truncated`, `bufferedLines` and `bufferedChars`. Poll with `since=<nextSeq>`. `source=console` or `source=bepinex` to split them. |
| `POST /console/clear` | Empty the tee. |
| `GET /console/buffer?limit=&contains=` | The game's own 1024-line console ring, newest first. Covers lines printed before this plugin loaded and the block/table printers that bypass `Print`. |
| `POST /console/exec` | `{command, waitFrames, waitMs}`. Runs a console command and returns the lines it produced. |
| `POST /console/print` | `{text, level=action\|error\|info}`. A marker line, handy for bracketing a test. |
| `GET /console/commands?contains=` | Registered console command names. |

### Session

| Endpoint | Notes |
|---|---|
| `POST /connect` | `{address, port, wait, timeoutMs, suppressTimeout, allowDuplicateIdentity}`. Direct Connect. Refuses a join into a known ClientId clash. |
| `POST /disconnect` | `{wait, timeoutMs}`. Leave to the main menu. |
| `POST /quit` | `{hard}`. `Application.Quit()`, or `GameManager.QuitGame()` (a `Process.Kill`) when `hard`. |
| `GET /saves` | Local save list. |
| `POST /load` | `{save, wait, timeoutMs}`. Load a save by name. |
| `POST /newworld` | `{world, difficulty, start, wait, timeoutMs}`. World ids are `Lunar`, `Mars2`, `Europa3`, `MimasHerschel`, `Venus`, `Vulcan2`. Not `Moon`. |
| `POST /waitfor` | `{phase=menu\|joining\|loading\|inWorld, timeoutMs}`. |
| `GET/POST /savepath` | `{path, force}` redirects the user-data root. See the safety notes below. |
| `GET/POST /identity` | `{clientId, username}`. Live rewrite; the value only has to be right at the moment the handshake copies it. |

### Input

| Endpoint | Notes |
|---|---|
| `POST /input/key` | `{key, mode=tap\|down\|up, frames, wait, requireConsumed}`. `key` is a `KeyCode` name (`LeftShift`, `F3`, `Mouse0`) or a `KeyMap` action name (`PrimaryAction`, `SwapHands`, `ToggleConsole`), resolved against the live binding rather than a hardcoded default. |
| `POST /input/scroll` | `{notches, frames=1, repeat, gapFrames, wait, requireConsumed}`. |
| `POST /input/mouse` | `{button, mode, frames}`. Alias for `Mouse0`/`Mouse1`. |
| `POST /input/mouseposition` | `{x, y}` or `{clear:true}`. Reports whether the game read it. |
| `POST /input/releaseall` | End every held key. |
| `POST /input/clear` | Drop all synthetic input state. |
| `GET /input/keymap` | Every `KeyMap` action and its current binding. |
| `POST /input/enable` | `{enabled}`. Master switch for injection. |
| `GET /diag/input` | Why input did or did not land, in one request. |

**The input contract.** These endpoints answer with what the game did, not with what the driver did:

| Field | Means |
|---|---|
| `consumed` | The game read the synthetic value **and** the per-frame consumer was running. **This is the field to assert on.** |
| `delivered` | Something in the game read the value. `observed` breaks it down by `getKey` / `getKeyDown` / `getKeyUp`; `scrollReads` is the wheel equivalent. |
| `gate` | Whether the consumer was running at all: `open`, `shutReason`, `cursorVisible`, `consoleOpen`, and how many times each relevant link ran inside the window. |
| `settled` | Only ever meant "the frames we asked for elapsed". True even when nothing read the key. **Never assert on it.** |

`requireConsumed` defaults to **true**, so unconsumed input answers **409**, not 200. A caller that does nothing special cannot get a success for input that did not happen. Pass `requireConsumed=false` for genuinely fire-and-forget input, such as a key nothing polls at the current phase.

### Player

| Endpoint | Notes |
|---|---|
| `POST /player/teleport` | `{position:[x,y,z]}`, `{x,y,z}` or `{offset:[dx,dy,dz]}`. On a remote client the server snaps the body back within seconds; the response says so. |
| `POST /player/look` | `{yaw, pitch}` or `{at:[x,y,z]}`. |
| `POST /player/use` | `{targetId}` or `{cursor:true}`. Uses the held item on a target by reference id, no aiming required and no distance gate. |
| `POST /player/swaphands` | Swap active and inactive hand. |

### Spawning

| Endpoint | Notes |
|---|---|
| `POST /spawn/hand` | `{prefab}`. Straight into the active hand. Needs simulation authority, so host or single player. |
| `POST /spawn/world` | `{prefab, position\|offset\|distance, viaServer}`. On a client it routes through `OnServer.SpawnDynamicThingMaxStack`, which forwards to the server. |
| `POST /spawn/structure` | `{prefab, position\|offset\|distance, yaw, colorIndex}`. Goes through `Constructor.SpawnConstruct`, which is client-safe. |
| `GET /prefabs?contains=&type=&limit=` | Prefab catalogue. |

### UI, config, reflection

| Endpoint | Notes |
|---|---|
| `GET /modsettings/list` | Every mod StationeersLaunchPad loaded, with `Name` and `Id`. |
| `POST /modsettings` | `{mod, show}`. Forces that mod's settings panel on screen so `/screenshot` can read it. Needs the real main menu. |
| `GET /modal` | Is a confirmation dialog showing, and what does it say. |
| `POST /modal/click` | `{button=1\|2\|3}`. Dismisses it and runs that button's callback. |
| `POST /cursor/force` | `{targetId}` or `{clear:true}`. Pins what the cursor reports, target and collider together. Refuses a target it cannot find a collider for. |
| `GET /screenshot?path=&supersize=&maxWidth=&inline=` | PNG of the full backbuffer, UI included. |
| `GET /config?guid=&filter=` | Every `ConfigEntry` of a loaded plugin. |
| `POST /config/set` | `{guid, section, key, value, save}`. Writes the live `ConfigEntry`; takes effect immediately with no restart. |
| `POST /config/reload` | `{guid}`. Re-read the `.cfg` from disk. |
| `GET /reflect?type=&member=` | Read any static field or property by full type name. Unwraps a `ConfigEntry<T>`. |
| `GET /reflect/members?type=` | Every static member of a type with its runtime value type. The diagnostic of last resort. |

---

## Keeping a driven session out of the real save folder

`POST /savepath {"path": "..."}` points `Settings.CurrentData.SavePath` at a scratch directory. Every save resolves through `StationSaveUtils.GetSavePath()` on each call, so worlds created after the redirect land there. The change is in memory; the game persists settings on a clean exit, so put it back at the end or exit with `POST /quit {"hard":true}`.

Provisioned instances already have their own save root through StationeersLaunchPad's `SavePathOverride`, so this endpoint is for one-off redirects rather than routine rig use.

Three things it does that a plain setter would not, all because the failure mode here is not recoverable by retrying:

- It echoes both the path as received and the path as resolved, so you can verify what landed.
- It **refuses** a path containing a control character rather than using it. The JSON reader now preserves a backslash that is not part of a recognised escape, so a path like `"C:\Rig\Scratch"` round-trips correctly where it used to lose both backslashes. What that cannot fix is the escapes JSON genuinely defines: `\b`, `\f`, `\n`, `\r` and `\t` still decode, so `"C:\builds"` and `"C:\files"` cannot survive a request body intact. Send such a path as a query parameter, or double every backslash.
- It **refuses** a path inside the game's own default user-data folder unless you pass `force=true`, since redirecting away from that folder is the entire point.

---

## Gotchas

Everything below was hit for real on 0.2.6403.27689 with StationeersLaunchPad 0.5.0.

**`-settings SavePath` silently vandalises the developer's `modconfig.xml`.** StationeersLaunchPad scans `<SavePath>\mods\`, finds it empty, and rewrites the shared `modconfig.xml` (which lives at `StationSaveUtils.DefaultPath` and which `-settingspath` does not move) with every `<Local>` entry deleted. Five local mods were silently stripped from the developer's own config on a first boot, and nothing warned. The launcher never passes that flag; it uses StationeersLaunchPad's own `SavePathOverride`, which moves `DefaultPath` itself. Do not add it back.

**Without a unique `-logFile`, the developer's `Player-prev.log` is destroyed.** Two instances sharing it both start fine, which is the trap. The second starter wins the file, the first instance's log is discarded with no error, and `Player-prev.log` is zeroed by two rotations in one second. The launcher always passes a unique path.

**`-nographics` without `-batchmode` is refused by Unity** and leaves a modal Win32 error dialog holding a live process that never boots. There is no windowless-but-not-batchmode mode. The launcher never passes it.

**The BepInEx plugin component is destroyed during boot.** `OnDestroy` fires on the ClientDriver `MonoBehaviour` about a minute into startup while the process keeps running, and `Chainloader.PluginInfos[...].Instance` is null for every plugin thereafter. The first build stopped its listener from `OnDestroy` and the control plane silently died a minute after launch. The server is therefore owned by a static, is never torn down from `OnDestroy`, and a watchdog re-binds if the socket goes away. `/status.driver.pluginDestroyCount` reports it. Do not put anything load-bearing in the plugin component's lifecycle.

**The main-thread pump cannot live on our own GameObject alone.** The pump `GameObject` is destroyed and recreated during boot too (`/status.driver.pumpObjectCreations` is normally 2). The primary pump is a postfix on `ImGuiManager.LateUpdate`, which runs every frame from the splash screen onwards and belongs to the game.

**StationeersLaunchPad mods are invisible to `Chainloader.PluginInfos`.** It only lists what BepInEx loaded out of `BepInEx/plugins/`, which is this plugin plus StationeersLaunchPad. `/config` and `/plugins` therefore resolve plugins by scanning loaded assemblies for `[BepInPlugin]`.

**A failed Steam Workshop query parks the client forever.** When StationeersLaunchPad's `FetchWorkshopPage` throws (a transient Steamworks `NullReferenceException`), it prints "Mods failed to load" and sits on its own ImGui screen, never reaching the menu. `loadedPluginCount` stays at 2 with `gameInitialized` false. `-Wait` names this explicitly when it times out. Stop and start the instance; it clears on retry.

**The join has a 10 second timer that a modded server cannot beat.** `NetworkClient.OnJoinStart` arms a timer whose only job is to give up and pop a modal. The handshake reaches the server and then the client cancels itself mid-transfer. `/connect` calls `NetworkClient.StopConnectionTimer()` immediately after `JoinClientFromMenu` (`suppressTimeout`, on by default) and uses its own timeout. If a dialog appears anyway, `/connect` reads it, clicks OK, and reports the text.

**`/connect` often fails on the first attempt after a server restart** and succeeds on the second, because the client is still settling from the previous disconnect. The response says so. Retry two or three times with a gap.

**`NetworkClient` is not findable for the first minute.** `FindObjectOfType` only sees active components. `/connect` falls back to `Resources.FindObjectsOfTypeAll` and waits.

**One scroll frame is one notch.** Wheel consumers act once per frame, so `frames=2` moves a spray can two colours, not one. `frames` defaults to 1. `repeat` with the default `gapFrames` is the way to travel several steps.

**`ConfirmationPanel.IsVisible` lies during boot.** It is just `gameObject.activeInHierarchy`, true for a window early in startup with an empty data stack behind it. `/modal` reports `visible` only when there is actual dialog data, and exposes the raw flag as `panelActive`.

**Screenshots are big.** A 3840x2160 backbuffer encodes to about 6 MB of PNG. `maxWidth` defaults to 1920 and GPU-downscales before encoding; pass `maxWidth=0` for the full thing.

**A forced cursor without a collider kills the client, permanently.** The cursor is a tuple, not one field, and `{FoundThing = X, CursorTargetCollider = null}` is a pair the game itself can never produce. `Thing.GetSlot(null)` then throws every frame from inside `GameManager.Update`, before the loop reaches the only code that could rebuild the cursor, so it throws again next frame forever, and `NetworkManager.ManagerUpdate` in the same loop stops processing packets. Measured at 100 exceptions per 6 seconds; only leaving the world recovered it. `/cursor/force` pins the collider alongside the target and refuses a target with no reachable collider. Full inventory in `Research/GameSystems/CursorManager.md`.

**Prefer `/player/use` with a `targetId` to anything cursor-shaped.** `OnServer.AttackWith` with an explicit target has no distance or line-of-sight gate (a stroke landed from 15 m away), so aiming is never necessary. `/cursor/force` is only for code that genuinely reads `CursorManager.CursorThing`.

**The console tee merges two streams.** `GET /console/log` returns both the game console and the BepInEx log, and a mod line that goes to both appears twice, so a naive count doubles. Pass `source=console` when counting what a player would actually see.

**`persistentDataPath` cannot be separated.** Editing `app.info` does nothing: the player takes company and product from the serialized PlayerSettings inside `globalgamemanagers`. So `PlayerCookie-v2.xml`, `Player.log`, `Blueprints\` and the PlayerPrefs registry key are shared by every instance and by the developer's client. Identity is handled in code instead, and the cookie file is protected by suppressing `Save()`.

**Every instance shares one Steam session.** Convenient (DLC entitlements are pooled, so metallic paints work everywhere) but they are not independent Steam identities and cannot be on one machine. A test needing one DLC owner and one non-owner is out of reach here.

**RAM is the constraint, not disk.** About 5 GB per instance idle at the menu, about 10 GB in world. Two instances plus the dedicated server fit comfortably in 128 GB; four would be tight. Disk is 3.6 MB for the first instance and 9.7 MB for the second.

---

## Layout

The rig lives at `TestRig/ClientRig/`, a peer of `TestRig/DedicatedServer/`. It drives game clients, not the server.

```
client-rig.ps1            the launcher: provision, desktop, lifecycle, fan-out
README.md                 this file
RESEARCH.md               durable internals
ClientDriver.sln
ClientDriver/
  Plugin.cs               entry, config binding, manifest folding, patch application, server lifecycle, frame pumps
  Instance/
    InstanceManifest.cs   the per-instance manifest, and which source each value came from
    Identity.cs           ClientId and Username injection, PlayerCookie.Save suppression
    PeerProbe.cs          duplicate-ClientId detection across sibling control planes
  Transport/
    HttpServer.cs         TcpListener HTTP/1.1
    Json.cs               minimal JSON reader and writer
    MainThreadPump.cs     background thread to Unity main thread, synchronously
  Routes/
    Router.cs             the dispatch table and shared helpers
    Routes.Console.cs     tee, game ring, command submission
    Routes.Session.cs     connect, disconnect, saves, savepath, identity, instance
    Routes.Input.cs       the input read-back contract, and /diag/input
    Routes.Player.cs      teleport, look, use, swap hands
    Routes.Spawn.cs       hand, world, structure, prefab catalogue
    Routes.Ui.cs          cursor forcing, screenshots
    Help.cs               the runtime endpoint catalogue
  Input/
    VirtualInput.cs       synthetic keyboard, mouse and wheel at the UnityEngine.Input layer, plus the delivery record
    GameplayGate.cs       opens the Cursor.visible gate, scoped to a loaded world
    ChainProbe.cs         enter/exit counters on the per-frame input chain
  Window/
    WindowMode.cs         forces windowed mode by correcting Settings.CurrentData before the game applies it
    NativeWindow.cs       read-only foreground and desktop queries, the only user32 imports in the plugin
  Observe/
    StateReporter.cs      live state to JSON
    ConsoleTap.cs         bounded tee of ConsoleWindow.Print plus a BepInEx log listener
    ConfigAccess.cs       live ConfigEntry read and write, plugin discovery, reflection
    Screenshot.cs         backbuffer capture and downscale
    ModSettingsPanel.cs   forces a StationeersLaunchPad mod settings panel on screen
    Modal.cs              reads and dismisses confirmation dialogs
  About/About.xml
```

The C# namespace is flat (`ClientDriver`) regardless of folder. The folders are for a reader, not for the compiler, and a nested namespace would churn every file for no gain.

Gitignored, created on demand: `data/` (registry, manifests, per-instance settings, save roots, logs, PID files) and `instances/` (the hard-linked trees, which normally live on the game install's volume instead). `.gitignore` carries the two as `/TestRig/ClientRig/data/` and `/TestRig/ClientRig/instances/`.
