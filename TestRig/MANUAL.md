# TestRig manual

The operating reference for `TestRig/testrig.ps1`. Read `TestRig/CLAUDE.md` first: it carries the lock, the save tiers and the safety rules, and it auto-loads. This file is the detail, and it is not auto-loaded; open it when you need a verb, a sequence, an endpoint or a flag.

Durable internals and the reasoning behind the design are in `TestRig/RESEARCH.md`. The playtest harness has its own short file at `TestRig/playtest/CLAUDE.md`.

## The launcher

```
testrig.ps1 <verb> [-Target all|server|clients|<instance>[,<instance>]] [options]
```

Run it with no verb to print the whole surface, including the hosting sequence and the current instances root. `TestRig/lib/` holds the per-half function libraries it dot-sources (`common.ps1`, `server.ps1`, `client.ps1`); `rig-lock.ps1` and `rig-reset.ps1` sit beside it. None of them is an entry point.

**Targets.** `all` is both halves, `server` is the dedicated server, `clients` is every provisioned instance, and one or more instance names (comma separated) is exactly those. An unknown name is a throw naming what is provisioned, never a silent empty set.

**`-Target` defaults to `all` on every rig-wide verb** (`status`, `list`, `logs`, `update-game`, `update-mods`, `deploy`, `reset`, and the lock verbs), so updating the rig updates both halves. Verbs that act on a specific running thing (`start`, `stop`, `save`, `wait`, `call`, `send`, `snapshot`, `create`, `remove`) require an explicit target and will not guess.

## Verbs

| Verb | Lock | On `server` | On an instance / `clients` |
|---|---|---|---|
| `lock -Purpose <s>` | takes | rig-wide, never per half | same |
| `refresh-lock -As <id>` | refreshes | rig-wide | same |
| `unlock -As <id> [-Force] [-KeepState]` | releases | rig-wide; **restores the rig first** | same |
| `capture-baseline -As <id> [-Force]` | needs | rig-wide | same |
| `reset -As <id> [-DryRun] [-KeepState]` | needs | rig-wide restore without ending the session | same |
| `status [-As <id>]` | free | wrapper and process pids, uptime, last log line, pending command, connected players, world count | per instance: process, classified role, ports, identity, tree and where its path came from, phase, live role, hosting, host port, connected clients, foreground verdict, input gate, identity conflicts |
| `list` | free | installed or not, install dir | the registry as a table, plus live role, hosting and client count |
| `logs [-Tail N] [-Grep <re>]` | free | `data/server.log` | each instance's `BepInEx/LogOutput.log` |
| `snapshot [-OutFile <f>]` | free | refused | `/status` from every named instance in one document |
| `update-game` | needs | SteamCMD app 600760, then mirror the client's `BepInEx/` tree and overlay the StationeersLaunchPad server zip | re-link each instance tree from the developer's install (a `create -Force` per instance) |
| `update-mods [-FromModConfig <p>]` | needs | mirror the developer's enabled mod set into `data/mods/`, bake `install/modconfig.xml` | re-seed each instance's `userdata/mods/` and its own `modconfig.xml` |
| `deploy <ModName> [-Configuration]` | needs | released mods to `install/BepInEx/plugins/<X>/`, dev-plugins to `data/mods/Local_<X>/` | to `userdata/mods/Local_<X>/` with an `About/` mirror and a `<Local>` entry |
| `create -Target <name>` | needs | refused | build or rebuild ONE instance tree |
| `remove -Target <name>` | needs | refused | delete the tree and the instance's save root |
| `start` | needs | must enter a world in the same call: `-Load <SaveName> -Map <Map>` or `-New <Map>` | boots to the MENU and no further; entering a world is a separate `call` |
| `wait -Stage <s>` | free (refreshes) | `inWorld` (an InspectorPlus probe) or `process` | `ping`, `modsLoaded`, `menu`, `inWorld` |
| `save [-SaveName <n>]` | needs | `-SaveName` required, confirmed from the log | `-SaveName` optional, confirmed by the plugin |
| `stop [-SaveName] [-Release]` | not gated | save first if named, `quit`, then kill after the grace period | host-aware ordered teardown |
| `call -Path <p> [-Body <json>]` | needs | refused | one HTTP request to each named instance's control plane |
| `send -Command '<text>'` | needs | one line into the server's stdin, fire and forget | refused |

`stop` is deliberately not lock-gated, so an orphan or an expired session can always be cleaned up with no ceremony and no `-As`. It refuses while another session's lock is **live**, and `-BreakLock` on it is human-gated like everywhere else.

`stop -Release` asks for the lock STATE before it releases, and that order is load bearing: the state check self-renews a busy session's expired lock and reports it as foreign, which is what stops an unrelated `stop -Release` from freeing it. Do not reorder those two steps.

## The refusals

Where a verb cannot mean the same thing on both halves, the launcher refuses and explains, naming what the verb needs, why this target cannot provide it, and a command that works. **Read the refusal rather than a table about it**: it arrives at the moment of the mistake and it is generated from the same data the tests check. Seven things genuinely differ, and the "refused" cells in the verb table above are where they show up: entering a world at start, the control channel (`call` versus `send`), save-confirmation evidence, anything needing a player character, N instances versus one install, creating an instance versus installing a server, and where a mod loads from. A lock verb, `capture-baseline` and `reset` refuse a `-Target` at all, because all four are rig-wide by construction, and an instance-shape flag (`-Role`, `-Port`, `-ClientId`, `-Username`, `-Width`, `-Height`, `-Desktop`) refuses against `-Target server`, which has one identity and no instances.

A refusal prints plainly and exits **2**, so a caller can tell "this command does not apply" from "the rig is broken". The matrix is data in `TestRig/lib/common.ps1` (`Get-RigRefusalTable`), and `testrig.tests.ps1` fails the suite if any entry lacks an alternative. `testrig send -Target clients` prints one.

## Flags

| Flag | Default | Means |
|---|---|---|
| `-SaveName <n>` | none | the world name to write, for `save` and for `stop`. Required on the server, optional on a client. It used to be `-Name` on save, `-SaveAs` on the server's stop and `-Name` again on the client's stop. |
| `-WaitSeconds <n>` | 300 | how long a BLOCKING WAIT waits: the readiness barrier and a save confirmation, on both halves. On `lock` only, it is the queue budget and defaults to 0 (do not queue). |
| `-TimeoutSeconds <n>` | 30 | process-teardown grace for `stop`. Never a save budget. |
| `-CallTimeoutSeconds <n>` | 0 | how long ONE control-plane request may take. 0 derives it from the request's own `timeoutMs` plus 30 s, floored at 120 s and at 300 s for `/host`, `/connect`, `/save`, `/load`, `/newworld` and `/waitfor`, capped at an hour. |
| `-Force` | off | override a refusal inside your own session (`create -Force` rebuilds an instance you own; `unlock -Force` releases with a host still up). Never touches another session's lock. |
| `-BreakLock` | off | take a LIVE lock off another session. Human-gated. |
| `-KeepState` | off | on `lock`, `unlock` and `reset`: skip the restore, loudly. |
| `-DryRun` | off | on `reset`: print the plan and do nothing. |
| `-As <id>` | none | the owner id printed by `lock`. |

`-WaitSeconds` used to be 30 on the server and 300 on the client for the same flag and the same meaning, so a slow but successful save produced a false warning on one half only. 300 wins because a false warning is indistinguishable from a real one, and the whole contract of `save` is that it warns rather than claiming success.

## Working sequences

### First time, or after a fresh clone

```powershell
# Directory.Build.props <StationeersPath>, and STEAMCMD_PATH in the environment. See DEV.md.
# Instance trees are hard links, so they must be on the game install's volume:
$env:STATIONEERS_CLIENTRIG_ROOT = '<drive of the game install>\StationeersRig'

pwsh -NoProfile -File TestRig/testrig.ps1 lock -Purpose "Rig bring-up"
# note the id from the TESTRIG-OWNER line
testrig update-game -As <id>                                  # both halves
testrig update-mods -As <id>                                  # both halves
dotnet build TestRig/ClientRig/dev-plugins/ClientDriver/ClientDriver.sln -c Release
testrig create -Target host1   -As <id> -Role host
testrig create -Target client1 -As <id>
testrig unlock -As <id>
```

### Host a world from a driven client, with a joiner

The host must be in its world before the joiner connects.

```powershell
testrig lock -Purpose "Host-side glow check for SprayPaintPlus"

testrig start -Target host1 -As <id>
testrig wait  -Target host1 -Stage menu
testrig call  -Target host1 -As <id> -Path /host -Body '{"world":"Lunar"}'
#   200 only once NetworkServer.IsHosting is true. The body carries hostPort, the
#   resolved savePath, localClientId, the roster and a full /status.
testrig wait  -Target host1 -Stage inWorld -WaitSeconds 600

testrig start -Target client1 -As <id>
testrig wait  -Target client1 -Stage menu
testrig call  -Target client1 -As <id> -Path /connect -Body '{"address":"127.0.0.1","port":27801}'
testrig wait  -Target client1 -Stage inWorld -WaitSeconds 600

testrig status -As <id>
#   under host1: liveRole=listenHost hosting=True hostPort=27801 connectedClients=1

# ... drive the test ...

testrig save -Target host1 -As <id> -SaveName HostGlowCheck     # only if the next session needs it
testrig stop -Target clients -As <id> -Release
```

Hosting an existing save instead of creating a world is `-Body '{"save":"HostGlowCheck"}'`. Exactly one of `save` or `world`. World ids are `Lunar`, `Mars2`, `Europa3`, `MimasHerschel`, `Venus`, `Vulcan2`; not `Moon`.

Two hosts at once works (each has its own game port by index), but nothing guarantees a joiner reaches the one you meant, so always name the port in `/connect` and confirm from the host's roster.

### A dedicated-server test

```powershell
testrig lock -Purpose "Playtesting network paint for SprayPaintPlus"
# stage any save you want to survive NOW, before the first mutating command
testrig stop   -Target server -As <id>                          # if anything is alive
dotnet build Mods/SprayPaintPlus/SprayPaintPlus.sln -c Release
testrig deploy SprayPaintPlus -Target server -As <id>
testrig start  -Target server -As <id> -Load Luna -Map Lunar    # or -New Lunar
testrig wait   -Target server -Stage inWorld -WaitSeconds 600
# drive it: testrig send / testrig logs -Grep / InspectorPlus request files,
# or join a driven client to 127.0.0.1:28016 with call -Path /connect
testrig stop -Target server -As <id> -SaveName AfterRun -Release
```

The developer can also join by hand: the regular client, Direct Connect to `127.0.0.1:28016`, no password. There is no `-connect` flag on the client, so that step is manual. If you then go idle waiting on them, say what the reservation window is and raise `-IdleCeilingMinutes` to cover it.

### Run a mod's playtest checks

```powershell
pwsh -NoProfile -File TestRig/playtest/playtest.ps1 -Suite TestRig/playtest/checks/SprayPaintPlus
```

The harness takes and releases the lock itself, per check, and provisions nothing. Do not hand-roll the bring-up sequence when a suite exists. See `TestRig/playtest/CLAUDE.md`.

### After a game update

```powershell
testrig lock -Purpose "Rig update to <version>"
testrig status                       # names each half's version and what is stale
testrig update-game -As <id>         # both halves: SteamCMD here, a re-link there
testrig update-mods -As <id>         # both halves
testrig unlock -As <id>
```

`status` compares each half against the developer's install and prints `current` or `STALE (source is <version>)` with the fixing command. That report exists because the rig once reported staleness for client instances only, so an agent updated the clients and was told it was done.

### Stage a save under test (dedicated server)

The developer drops a source save somewhere outside the rig (tier 2, read-only). Copy it in **before the session's first mutating command**, then start.

```
TestRig/DedicatedServer/data/saves/<SaveName>/
    <SaveName>.save           the ZIP archive. Its basename MUST match the folder name.
    autosave/                 optional, ignored if absent
    manualsave/               optional
    quicksave/                optional
```

- **The `.save` file IS the ZIP.** Do not extract it. If the folder holds `world.xml`, `world_meta.xml`, `terrain.dat` and `preview.png`, the layout is wrong.
- **The basename must match the `-Load` argument.** `Luna_pgp_test/` holding `Luna.save` will not load; rename on copy.
- Source is a bare `Luna.save`: `mkdir $dest; cp $src $dest/<SaveName>.save`. Source is already a save-shaped folder: copy its contents in as they are. Source is `<Other>/<Other>.save`: copy in, then rename inside the destination.

If no save exists and a test asks for `-Load`, the command fails; use `-New <Map>` instead. Never read or copy out of the developer's own save folder.

## Readiness

| `-Stage` | Means |
|---|---|
| `ping` | BepInEx loaded the plugin. The game is still booting. |
| `modsLoaded` | `loadedPluginCount > 10`: StationeersLaunchPad finished loading Workshop mods. |
| `menu` | `gameInitialized == true` and `phase == "menu"`. The splash screen is gone and the menu is up. |
| `inWorld` | `phase == "inWorld"`. |

Wait for `menu` before touching the menu or the ImGui overlay: at `modsLoaded` the splash screen is still drawing and it suppresses the in-game windows. Cold boot to `menu` is about 100 s.

`inWorld` is **not** a readiness stage for a host. A world can be up with hosting silently not happening. The host's post-condition is `/status.hosting == true` with `/status.role == "listenHost"`, which `POST /host` asserts before it answers 200.

On the server, `wait -Stage inWorld` drops a minimal InspectorPlus request into `install/BepInEx/inspector/requests/` and polls for the plugin to delete it. The pump runs off `ElectricityManager.ElectricityTick`, so consumption means the world is loaded and ticking. It needs InspectorPlus deployed with `Force Unpause Without Client` set, or the simulation stays paused with nobody connected and no probe is ever consumed; the timeout message says so. `status` reporting the pid alive is not readiness.

## The dedicated server half

`TestRig/DedicatedServer/` holds `install/` (the SteamCMD-managed binaries plus the mirrored `BepInEx/` tree and the doorstop loader) and `data/` (`setting.xml`, `server.log`, `saves/`, `scripts/`, `mods/`, the pid files and the control file). State is split out of the install tree so binaries can be wiped and re-installed without losing worlds.

**Lifecycle.** `start` launches a hidden PowerShell wrapper through `ProcessStartInfo` with `CreateNoWindow`, so no console host is allocated and nothing claims focus. The wrapper owns the server process: it spawns it with redirected stdin, polls `data/control.cmd` every 250 ms and forwards each command. `data/host.pid` is the wrapper, `data/server.pid` is the game, `data/control.cmd` is a one-command queue written by atomic rename. On exit the wrapper's `finally` removes all three. If the wrapper itself is killed the server can be orphaned; `status` detects that and `stop` cleans it up.

**The flag set `start` applies:**

```
-batchmode -nographics
-settingspath  <DedicatedServer>/data/setting.xml
-logFile       <DedicatedServer>/data/server.log
-settings SavePath          <DedicatedServer>/data
-settings GamePort          28016     (override with -GamePort N)
-settings UpdatePort        28015     (override with -UpdatePort N)
-settings LocalIpAddress    127.0.0.1
-settings AutoSave          true
-settings AutoPauseServer   false
-settings UPNPEnabled       false
-settings ServerName        "Local Test"
-settings ServerMaxPlayers  4
-settings ServerAuthSecret  x
-load <SaveName> <Map>      OR   -new <Map>
```

`LocalIpAddress 127.0.0.1` pins RakNet to loopback; without it RakNet binds the first interface that is up, which on a machine with a LAN is the LAN address, and Direct Connect to `127.0.0.1:28016` then fails. `AutoPauseServer false` keeps the simulation running with nobody connected. `ServerAuthSecret x` enables `serverrun` from a connected client. No `ServerPassword`: a loopback-only bind makes external connections impossible at the network layer. Details and the source-verified reasoning: `Research/GameSystems/DedicatedServerSettings.md`.

**Deploy versus sync.** `deploy` writes this repository's built mods to `install/BepInEx/plugins/<X>/<X>.dll` (the BepInEx Chainloader path); a dev-plugin goes to `data/mods/Local_<X>/` instead (the StationeersLaunchPad path), because it needs an `About.xml`. `update-mods` mirrors the developer's whole enabled mod set into `data/mods/` and **wipes that folder**, so anything a deploy put there goes with it: sync first, deploy second. The same DLL in both load paths is fatal, not untidy: `Awake` fires twice and every Harmony patch registers twice, so `deploy` removes a stale copy from the other path.

**Stop before deploying.** The Mono runtime holds an exclusive lock on every loaded plugin DLL on Windows, so a deploy onto a running server fails with a sharing violation or leaves a half-written DLL the next start picks up as broken plugin bytes. Both `deploy` and `update-mods` refuse while the server or its wrapper is alive.

**Version coupling.** The server's `BepInEx/` tree, including StationeersLaunchPad and its siblings (LaunchPadBooster, StationeersMods.Interface, StationeersMods.Shared, NetworkBufferFix), must match the client's exactly, or the join handshake rejects clients with a version mismatch. `update-game` re-syncs it and overlays the StationeersLaunchPad server zip, which carries `RG.ImGui.dll` that the client install does not have.

**`ScenarioRunner`** is the in-server probe plugin, at `TestRig/DedicatedServer/dev-plugins/ScenarioRunner/`. Use it when a snapshot is the wrong shape: state evolution across many ticks, or stimulating a method rather than reading one. It runs from a Harmony postfix on `ElectricityManager.ElectricityTick` (on a headless server `MonoBehaviour.Update` does not fire after world load, and `GameManager.GameTick` runs on a ThreadPool worker where `FindObjectsOfType` crashes). Pick a scenario in `install/BepInEx/config/net.scenariorunner.cfg`, then grep `install/BepInEx/LogOutput.log` for `[ScenarioRunner]`. Its catalogue and authoring guide: that folder's `README.md`.

**Offline save editing** is `tools/save-edit/`: read a save ZIP, mutate `world.xml`, write a new ZIP, with the game not running. Use it for persisted state (fields on existing Things, cloning a Thing to a position, adding or dropping network ids) and `ScenarioRunner` for anything that depends on a simulation tick or on adjacency-driven registration. Always work on a copy inside `data/saves/`.

**InspectorPlus** works here as everywhere else: requests into `install/BepInEx/inspector/requests/`, snapshots out of `install/BepInEx/inspector/snapshots/`. With no client connected the simulation is paused and requests are not processed, so set `Force Unpause Without Client` under `[Server - Headless]` in `install/BepInEx/config/net.inspectorplus.cfg`. That force-unpause is a one-shot in a `GameManager.StartGame` postfix and has been observed not to survive a world reload; if a dropped request is not consumed within seconds of a loaded world, the simulation has re-paused. See `Research/Workflows/InspectorPlusUsage.md`.

There is no clean verb. Wiping binaries is "delete `install/`, then `update-game -Target server`"; wiping worlds by hand is the developer's call.

## The client half

`TestRig/ClientRig/` holds `dev-plugins/ClientDriver/` (the control plane inside each instance), `data/rig.json` (the registry, one entry per instance), `data/<instance>/` (manifest, provision stamp, `setting.xml`, save root, logs, pid file) and `instances/<instance>/` (the hard-linked game tree, which normally lives on the install's volume instead).

**What `create` builds.** `rocketstation_Data`, `MonoBleedingEdge` and the engine binaries are NTFS hard links (about 1,050 of them). `doorstop_config.ini`, `Fixing The Controls modifiers.ini`, `app.info` and the whole `BepInEx/` tree are real copies, because a mod writes to them and a hard link would reach back into the developer's install. `ClientDriver.dll` lands in `BepInEx/plugins/ClientDriver/`. Local mods are copied into the instance's own save root with `modconfig.xml` repointed and `SavePathOverride` set (`-SeedMods:$false` skips that). `imgui.ini` and `output_log.txt` are not carried.

**Defaults by index**, so instances created with no flags never collide: control plane TCP 27700+index, game port UDP 27800+index, ClientId 900000000000+index. Override with `-Port`, `-GamePort`, `-ClientId`, `-Username`, `-Width`, `-Height`. `-Role` is `client` or `host`.

**`create` refuses a duplicate ClientId, control port or game port.** Neither is fussiness. The server keys a player's body on ClientId and `Brain.RegisterBrain` overwrites silently, so two clients sharing one id resolve onto **one character** with nothing warning; a test that believes it has two players and has one produces plausible, meaningless results. And two RakNet sockets on one UDP port coexist rather than conflicting, so a colliding game port produces a joiner that reaches something with no error anywhere. 27015/27016 (the game client's own defaults) and 28015/28016 (this rig's server) are refused too.

**A rebuild (`create -Force`) replaces the TREE only.** `data/<instance>/` survives, deliberately, so a staged save does not evaporate on a plugin rebuild: the save root, the logs, the pid file and the game-written `setting.xml` all stay, and only `userdata/mods/` is rewritten. `-Role` and `-GamePort` are kept unless typed again, so picking up a new plugin build never silently demotes a host or moves its port. A fresh lock is what clears `setting.xml`.

**The instances root is recorded at create time.** Hard links cannot cross volumes, so the trees normally sit on the game install's drive rather than under `ClientRig/instances/`. The resolved root goes into the registry entry and every later action, including the state reset, reads it back, so `-InstancesRoot` is typed once. Typing it again moves the tree (the old one is left behind and the launcher says so). An entry from before the field existed falls back to `-InstancesRoot`, then `$env:STATIONEERS_CLIENTRIG_ROOT`, then `instances/`, and names `create -Force` as the fix. `status` prints the resolved tree, whether it exists and which source it came from.

**`data/<instance>/provision.stamp`** records when the instance was built and out of what: the time, the role, both ports, the source install and its version, and the plugin DLL's build time. It is the only way to answer "is this instance stale" after a game update or a plugin rebuild.

**Teardown is classification first, action second.** `stop` classifies the whole rig before touching any of it, then disconnects joiners and confirms it, saves whoever holds a world and confirms it, quits hosts, and leaves unclassifiable instances last. It refuses to take a host down while something outside the teardown is attached to it, and refuses an instance whose control plane does not answer and therefore cannot be ruled out as a host (`-Force` accepts the loss). After the process is gone it clears `StartLocalHost` from that instance's `setting.xml`. `start` throws over a running instance rather than skipping it.

**Hosting refusals you may hit:**

| Answer | Means |
|---|---|
| `409 cannot host from gameState=Running` | `/host` loads or creates the world itself and must start from the menu, because `StartLocalHost` is only read at world entry. `POST /disconnect` first. |
| `409 ... already reports role=<x> at the main menu` | this process's `NetworkRole` is not `None`, so a clean host is impossible. Known cause: an inbound Steam P2P request promoting an idle process to server. Restart the instance. |
| `409 save path not isolated` | the instance would write its world inside the developer's real user-data folder. Re-create it so `SavePathOverride` points at its own save root. |
| `409 duplicate ClientId` | a sibling claims this instance's id. The host's id exists first, so a colliding joiner takes over the host's body. |
| `409 the world is up but NetworkServer.IsHosting is false` | hosting silently did not happen, almost always the port. The response carries the console tail and the requested port. |

**ClientDriver configuration.** The manifest at `data/<instance>/instance.json` is written by the launcher and **wins over** `BepInEx/config/net.clientdriver.cfg` for every value it carries, because it is rewritten on every create and therefore describes this run, whereas a `.cfg` is sticky across sessions. `GET /instance` reports `valueSources` so the winner is never a guess.

| Section | Key | Default | What it does |
|---|---|---|---|
| Control Plane | `Port` | 27700 | TCP, bound to 127.0.0.1 only |
| | `Enabled` | true | master switch; false means no patches and no socket |
| | `Allow Input Injection` | true | false makes every query fall through to real hardware |
| | `Patch Unity Input` | true | diagnostic only: false rules this plugin out of an input problem |
| Console Tee | `Max Lines Per Source` | 2000 | ring capacity; evictions counted in `dropped` |
| | `Max Characters Per Line` | 4000 | longer lines truncated and counted |
| | `Max Characters Per Source` | 4194304 | the cap that actually holds when lines are large |
| Identity | `Client Id` / `Username` | empty | the identity to present; every concurrent instance needs a different id |
| | `Lock Cookie File` | false | suppress `PlayerCookie.Save()`; an identity override implies it |
| Window | `Force Windowed` | false | keeps the instance windowed; `-screen-fullscreen 0` does not survive boot |
| | `Window Width` / `Height` | 800 / 600 | |
| Hosting | `Role` | client | what the instance is FOR. Advisory to the plugin (`/host` works on any instance) but load bearing to the launcher, which drives teardown ordering and its host refusals off it. The live answer is `/status.role`. |
| | `Game Port` | 27016 | the RakNet port `/host` binds when the request names none; `create` sets 27800+index |
| Gameplay Input | `Force Gameplay Input` | false | holds the cursor locked and hidden so per-frame input consumers keep running unfocused. **Without it, `/input/*` is delivered and then discarded.** Created instances get it on. |
| | `Force Gameplay Input Everywhere` | false | assert the gate outside a loaded world too |

## The ClientDriver endpoint catalogue

Every body field can also be a query parameter, so anything is reachable from a browser or `curl`. **A query parameter is the reliable way to send a Windows path**: it is percent-decoded by the HTTP layer and never goes through the JSON string reader. `GET /help` prints this list at runtime and is the authority.

### Instance and state

| Endpoint | Notes |
|---|---|
| `GET /ping` | liveness plus frame counter. Never touches the main thread, so it answers even if the game is wedged. |
| `GET /instance` | name, port, role, game port, identity, manifest path, which source each value came from, sibling ports, duplicate-ClientId verdict. `rescan=true` forces a fresh peer probe. |
| `GET /status` | everything: instance, game state, network role, hosting, world, player, foreground, input gate, save hygiene, driver counters. |
| `GET /player` | the player block only. |
| `GET /colors` | `GameManager.CustomColors` with swatch indices. |
| `GET /plugins` | every plugin found by assembly scan, with its assembly path. |
| `GET /nearby?radius=&filter=&limit=` | Things around the player, with a fixed field set. |

The `/status` fields a multiplayer test reads:

| Field | Means |
|---|---|
| `role` | `menu \| singlePlayer \| joinedClient \| listenHost \| dedicated`, computed in one place. **Read this rather than `isClient` / `isServer`**, which are three views of one enum and read backwards for a listen host. |
| `hosting` | `NetworkServer.IsHosting`. The only honest post-condition for a host attempt. |
| `hostPort` | `NetworkServer.HostPort`, or 0. |
| `connectedClients` | server-side roster: `{clientId, username, state, isHost, connectionId}`. Empty on anything that is not a server. The host is in its own roster, so subtract one when counting joiners. |
| `settingsPath` | the `setting.xml` this instance would write. |
| `savePathResolved` | where this process would write a world right now. |
| `saveRootIsolated` | whether that root is outside the developer's real user-data folder. Fails closed. |
| `startLocalHostPersisted` | `StartLocalHost` as it stands ON DISK, so `true` means this instance hosts again on its next launch. |
| `startLocalHostInMemory` | the live value. Disagreeing with the persisted one is normal and is why both are reported. |

### Console

| Endpoint | Notes |
|---|---|
| `GET /console/log?since=&limit=&contains=&source=` | sequence-numbered tee of the in-game console and the BepInEx log, with `dropped`, `truncated`, `bufferedLines`, `bufferedChars`. Poll with `since=<nextSeq>`. `source=console\|bepinex` splits them. |
| `POST /console/clear` | empty the tee. |
| `GET /console/buffer?limit=&contains=` | the game's own 1024-line ring, newest first. Covers lines printed before the plugin loaded. |
| `POST /console/exec` | `{command, waitFrames, waitMs}`. Runs a console command and returns the lines it produced. |
| `POST /console/print` | `{text, level}`. A marker line for bracketing a test. |
| `GET /console/commands?contains=` | registered console command names. |

### Session

| Endpoint | Notes |
|---|---|
| `POST /connect` | `{address, port, wait, timeoutMs, suppressTimeout, allowDuplicateIdentity}`. Direct Connect. Refuses a join into a known ClientId clash. |
| `POST /host` | `{save\|world, difficulty, start, port, serverName, password, maxPlayers, wait, timeoutMs, allowDuplicateIdentity, requireIsolatedSavePath}`. Load or create the world AND serve it. Must start from the menu. Defaults: `port` = the manifest's game port, `maxPlayers` 4, `difficulty` Normal, `timeoutMs` 300000, `requireIsolatedSavePath` **true**. 200 only once `IsHosting` is true. |
| `POST /disconnect` | `{wait, timeoutMs}`. Back to the main menu. |
| `POST /quit` | `{hard}`. `Application.Quit()`, or a `Process.Kill` when `hard`. |
| `GET /saves` | local save list. |
| `POST /save` | `{name, wait, timeoutMs}`. Host or single player only. **200 only on a confirmed save**; asked-for-but-unconfirmed is 409 with `requested:true` and a warning. `timeoutMs` defaults to 180000. |
| `POST /load` | `{save, wait, timeoutMs}`. |
| `POST /newworld` | `{world, difficulty, start, wait, timeoutMs}`. |
| `POST /waitfor` | `{phase=menu\|joining\|loading\|inWorld, timeoutMs}`. |
| `GET/POST /savepath` | `{path, force}`. Retargets a RUNNING client's user-data root. `force=true` reaches the developer's tier-1 folder; never pass it. `GET` reports `realUserDataPath` and `reportedDefaultPath` side by side. |
| `GET/POST /identity` | `{clientId, username}`. Live rewrite; the value only has to be right when the handshake copies it. |
| `GET /diag/join` | why a join did or did not land: the recorded trace of the last `/connect`, including `StartClient`'s result and the RakNet detail. |

### Input

| Endpoint | Notes |
|---|---|
| `POST /input/key` | `{key, mode=tap\|down\|up, frames, wait, requireConsumed}`. `key` is a `KeyCode` name or a `KeyMap` action name, resolved against the live binding. |
| `POST /input/scroll` | `{notches, frames=1, repeat, gapFrames, wait, requireConsumed}`. |
| `POST /input/mouse` | `{button, mode, frames}`. |
| `POST /input/mouseposition` | `{x, y}` or `{clear:true}`. Reports whether the game read it. |
| `POST /input/releaseall`, `POST /input/clear` | end held keys, drop synthetic state. |
| `GET /input/keymap` | every `KeyMap` action and its binding. |
| `POST /input/enable` | `{enabled}`. |
| `GET /diag/input` | why input did or did not land, in one request. |

**The input contract.** `consumed` means the game read the synthetic value AND the per-frame consumer was running: **that is the field to assert on**. `delivered` means something read it. `gate` says whether the consumer ran at all. `settled` only ever meant "the frames we asked for elapsed" and must never be asserted on. `requireConsumed` defaults to **true**, so unconsumed input answers 409.

### Player, inventory, spawning

| Endpoint | Notes |
|---|---|
| `POST /player/teleport` | `{position}`, `{x,y,z}` or `{offset}`. On a remote client the server snaps the body back; the response says so. |
| `POST /player/look` | `{yaw, pitch}` or `{at}`. |
| `POST /player/use` | `{targetId}` or `{cursor:true}`. Uses the held item on a target by reference id, no aiming and no distance gate. |
| `POST /player/swaphands` | swap active and inactive hand. |
| `GET /inventory` | `?player=&humanId=`. Every slot with the `key` and `index` the routes below accept. `activeHand` resolves only for the character this process owns. |
| `POST /inventory/arm` | `{prefab, hand, quantity, replace, searchRadius, timeoutMs}`. **One call, any role, joiner included.** Spawns through the server, waits for the Thing, moves it into the hand, waits for the server to agree. 200 only when the hand holds it. |
| `POST /inventory/move` | `{thing\|from, to, intoThing, replace, wait, timeoutMs}`. `OnServer.MoveToSlot`, the same call every inventory drag makes. No authority needed. |
| `POST /inventory/give` | `{prefab, player\|clientId\|humanId, slot, quantity, replace}`. **Host only.** Cannot target a remote player's active hand. |
| `POST /spawn/hand` | `{prefab}`. Needs simulation authority, so host or single player. Use `/inventory/arm` on a joiner. |
| `POST /spawn/world` | `{prefab, position\|offset\|distance, viaServer}`. |
| `POST /spawn/structure` | `{prefab, position\|offset\|distance, yaw, colorIndex}`. Client-safe, through `Constructor.SpawnConstruct`. |
| `GET /prefabs?contains=&type=&limit=` | the prefab catalogue. |

### UI, config, reflection, Things, DLC

| Endpoint | Notes |
|---|---|
| `GET /modsettings/list`, `POST /modsettings` | list the mods StationeersLaunchPad loaded; force one's settings panel on screen so `/screenshot` can read it. Needs the real main menu. |
| `GET /modal`, `POST /modal/click` | is a confirmation dialog showing and what does it say; dismiss it and run that button's callback. |
| `POST /cursor/force` | `{targetId}` or `{clear:true}`. Pins target and collider together. Refuses a target with no reachable collider. Avoid it; prefer `/player/use`. |
| `GET /screenshot?path=&supersize=&maxWidth=&inline=` | PNG of the full backbuffer. `maxWidth` defaults to 1920. |
| `GET /config?guid=&filter=` | every `ConfigEntry` of a loaded plugin. |
| `POST /config/set` | `{guid, section, key, value, save}`. Writes the live entry, effective immediately. |
| `POST /config/reload` | `{guid}`. Re-read the `.cfg` from disk. |
| `GET /reflect?type=&member=&expand=&expandLimit=&key=` | any STATIC field or property by full type name. `key=<k>` answers "does this dictionary contain that key" without dumping it. |
| `GET /reflect/members?type=` | every static member of a type with its runtime value type. |
| **`GET /thing?refId=&refIds=&fields=&type=&comparePrefab=&expand=&expandLimit=&key=`** | **read any member of any Thing.** `fields` is a comma-separated list of instance fields or properties, public or private, on the runtime type or any base type; a dotted path walks (`ParentSlot.Parent.ReferenceId`) and `[n]` indexes. A member that does not exist answers `ok:false` naming the types searched, never an empty value. Each field carries `prefabValue` and `matchesPrefab`, and every row carries a `location` block (in a slot or on the ground, which slot, which hand, and whether THIS process is the authority). |
| **`GET /reflect/instance?refId=&member=&type=&expand=&key=`** | one instance member on one object, the instance twin of `/reflect`. `type` pins which declaring type the member is looked up on, which is the only way to reach a private base field a derived type shadows. |
| **`GET /thing/members?refId=&type=&contains=&limit=&values=`** | every instance member of a Thing or of a bare type, with declaring type and current value. Diagnostic of last resort. `values=false` skips invoking every getter. |
| **`GET /dlc`** | this process's DLC entitlement, the session pool, what has been removed, and the ordering a removal must be sequenced into. |
| **`POST /dlc/remove`** | `{dlc, scope=owned\|shared\|both}`. **Removal only:** the one write it performs clears bits out of the value already there, so no route, parameter or value can add entitlement, and a request carrying add/grant/set/give/own/unlock is refused rather than ignored. In memory, per process, never persisted. **Sequence it before world entry:** a joiner announces `DLCManager.GetOwnedDLC()` at the end of its join and a listen host re-seeds the pool at the end of the load, so a later removal is silently undone. |
| **`POST /dlc/restore`** | put back the baseline this process held before its first removal. Takes no arguments. |

The five bold rows are what make a per-Thing instance field, and a DLC owner versus non-owner test, reachable without an InspectorPlus round trip. Checks 07 and 08 under `playtest/checks/SprayPaintPlus/` run on the DLC routes.

## The session lock

The active lock is `TestRig/session.lock` (gitignored), written and maintained by `TestRig/rig-lock.ps1`. Do not hand-create it. Its keys, one per line:

| Field | Means |
|---|---|
| `owner` | the short id printed by `lock`; echo it back with `-As` |
| `purpose` | short human-readable reason |
| `acquired_at` | ISO 8601 UTC, when first acquired |
| `refreshed_at` | last heartbeat, drives `ttl_minutes`. The busy self-renew moves this and only this. |
| `active_at` | last OWNER action, drives the ceiling. Nothing but the owner's own commands move it. |
| `ttl_minutes` | heartbeat window (10) |
| `idle_ceiling_minutes` | absolute idle window after which the lock is reclaimable busy or not (60) |
| `host` | machine name, for diagnostics |

A lock written before `active_at` existed falls back to `acquired_at`, which is older than any owner action and so never makes a lock look fresher than it is. Every timer field fails **closed**: missing, unparseable or negative reads as expired or as past the ceiling.

**Acquisition is serialised across processes** by a named system mutex, so two agents that both find the rig free cannot both walk away believing they own it. Without it the loser would only find out on its next mutating command, minutes and one full instance build later.

**Liveness** is: the timer is fresh, OR the rig is busy, AND the idle ceiling has not been reached. Past the ceiling a lock is reclaimable whether or not the rig is busy, and the reclaim stops what is running on both halves. That is deliberate: before the ceiling existed, an agent that died holding one instance held the whole rig until a human authorized a break, which makes a hung agent a blocker rather than a delay. The price is real and stated plainly: a reclaim can stop instances belonging to a session that was merely very quiet. So the reclaim is loud, names the purpose it took the rig from and what was running, and `status` prints the countdown.

**Reclaiming past the ceiling is not a `-BreakLock`.** A lock past its ceiling is dead by these rules, in the same way an expired one on an idle rig is dead. `-BreakLock` takes a genuinely LIVE lock and stays human-gated.

**The busy reason names what is happening**, not a process count: how many instances are running, which one is HOSTING, and how many clients are connected to it. That text is what a human reads when deciding whether to authorize a break, and "2 client instance(s) running" cannot tell a live hosted test at minute 40 from two instances somebody forgot to stop. A pid file alone is not proof: the process image is checked, because Windows recycles process ids and these files outlive their processes on a force-kill or a reboot. An instance created before the manifest carried a role reports "role unknown" and still counts as busy.

**The purpose string** exists for the user, who is told it when another session holds the rig and has to decide whether to wait, take it, or leave it alone. Write it for that reader: "Playtesting network paint for SprayPaintPlus".

**Waiting for a human.** You are about to go idle, so you will not be refreshing. Tell the user the reservation in plain terms, raise `-IdleCeilingMinutes` to cover the wait, and set a sensible `-TtlMinutes` join window. While a player is actually connected the lock stays live on its own. If nobody joins, the timer lapses and the rig frees, which is intended: an agent waiting on a sleeping user must not block the others.

## State hygiene

The implementation is `TestRig/rig-reset.ps1`. It runs at both ends of a session (see `TestRig/CLAUDE.md`) and on demand through `reset`.

| Half | Reset | Kept |
|---|---|---|
| Client, per instance | `data/<n>/setting.xml` (it carries `StartLocalHost`), `data/<n>/userdata/saves/`, the Unity logs, `imgui.ini`, a STALE `game.pid`, the instance's `BepInEx/config` (re-copied from the source install, then `SavePathOverride` re-applied), `LogOutput.log*`, `BepInEx/cache/`, `BepInEx/inspector/requests/` and `snapshots/` | `data/rig.json`, `instance.json`, `provision.stamp`, `userdata/mods/` and `modconfig.xml`, the deployed `ClientDriver`, the hard links |
| Dedicated server | the ScenarioRunner `Scenario` value (blanked, the rest of the file untouched), `install/BepInEx/scenariorunner/requests/` and `give/`, `install/BepInEx/inspector/requests/` and `snapshots/`, `data/setting.xml`, stale `server.pid` / `host.pid` / `control.cmd`, and any `data/saves/` world THIS session created | every world that predates the session, `data/mods/`, `install/modconfig.xml`, the deployed plugin DLLs, every other `install/BepInEx/config/*.cfg` |

Three things are **reported** rather than touched, because deleting them would be worse than leaving them: a seeded mod older than its source (the fix is `update-mods` or `create -Force`), the retained world count and total size, and any server config that changed since the last reset.

Two entries in the client list are load bearing and easy to misread. `setting.xml` carries `StartLocalHost`, and an instance that silently comes up hosting while a test believes it is a joiner is the worst failure available here. And the `BepInEx/config` re-copy **wipes `SavePathOverride`**, so the re-apply immediately after it is not tidiness: without it the next launch of that instance writes its worlds into the developer's tier-1 folder. Both the create path and the reset write that setting through one function, `Set-RigSavePathOverride`; do not add a second copy anywhere.

**What "clean" means is captured, not hardcoded.** `capture-baseline -As <id>` declares the rig as it stands to be the definition of clean, writing `TestRig/baseline/` (gitignored). Three classes:

- **config**: every `*.cfg` under each instance's and the server's `BepInEx/config`, plus each `modconfig.xml`. Bytes are stored and copied back, so a value a session changed or a file it deleted goes back exactly. Kilobytes in total.
- **payload**: deployed plugins and seeded mods. Hashed and inventoried, **never stored and never restored**: rolling one back would silently undo a deliberate deploy, and "my fix is not in the game" is the quietest possible failure. A payload that moved makes the baseline stale instead.
- **world**: dedicated-server saves, recorded by name and size, informational only. Nothing ever reads them back. What happens to a world is decided by the session marker, never by this manifest.

**Stale is loud, never silent.** A baseline is stale when the game version moved, an instance appeared or disappeared, or a plugin or seeded mod was rebuilt. Every reset then warns, names the reason, and names `capture-baseline` as the fix. It never blocks the lock: an unclean rig must not become an unlockable one. Staleness changes nothing about what a reset does. With no baseline at all, config behaviour is what it was before baselines existed, and every reset says so.

**The reset surface is an allow-list.** It touches only the classes above and its own hardcoded targets, so a deliberate instance-scoped change anywhere else in a tree (a real, never-hard-linked assembly dropped into one instance's `rocketstation_Data\Managed\`, say) survives every restore and is never reported as drift. A deny-list would have the opposite default and would scrub exactly those changes.

**A failed reset is loud, names the instance, and throws.** You still hold the lock; unlock and take it again to retry. A failed or refused restore does not clear the dirty marker, so the next acquisition tries again.

**Shared per-user state is reported, never restored.** `PlayerCookie-v2.xml`, the PlayerPrefs key and `Blueprints\` cannot be isolated, and writing them back would itself be the write the save rules forbid. A cheap snapshot is taken at acquisition into `TestRig/session.state.json` (gitignored) and the delta is printed at release. It fixes nothing; it turns state that was invisible until a later test failed into a line at the session boundary.

## Dev-plugins

A dev-plugin is a BepInEx plugin that exists only to drive or observe the rig. It never ships to the Workshop, never graduates into `Mods/`, and only makes sense paired with the launcher. `WorkshopHandle` is 0 and stays 0. The layout is the same on both halves, so a second one slots in beside the first with nothing to rename:

```
TestRig/<Half>/dev-plugins/<Name>/
    <Name>.sln
    <Name>/
        <Name>.csproj
        About/About.xml
        ... source ...
```

Today that is `TestRig/DedicatedServer/dev-plugins/ScenarioRunner/` and `TestRig/ClientRig/dev-plugins/ClientDriver/`. Build with `dotnet build <path>/<Name>.sln -c Release`, then `testrig deploy <Name> -Target <half> -As <id>`; on a client instance, re-create it with `-Force` to pick up a new `ClientDriver` build, since the plugin is copied at create time.
