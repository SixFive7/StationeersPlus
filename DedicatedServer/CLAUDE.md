# DedicatedServer

This folder holds a self-contained Stationeers Dedicated Server install used to test the mods in this monorepo against multiplayer code paths. It is isolated from the developer's client install and from any client save folder.

The folder is gitignored except for this file, the launcher `dedicated-server.ps1`, and `session.lock.template`. Anything that lands in `install/` or `data/` is local-only and never committed.

## Layout

- `install/` (gitignored): the SteamCMD-managed binary install (`rocketstation_DedicatedServer.exe` plus `rocketstation_DedicatedServer_Data/`), the BepInEx loader (`winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`), and the `BepInEx/` tree mirrored from the developer's client install (BepInEx core plus StationeersLaunchPad and its sibling plugins).
- `data/` (gitignored): the dedicated server's `setting.xml`, the streaming log, the `saves/` folder, the `scripts/` folder, and the engine-managed `mods/` folder. State is split out of the install tree so binaries can be wiped and re-bootstrapped without losing test worlds.

## Saves: tier-3 free-to-edit, plus the client folder remains off-limits

`data/saves/` is the dedicated server's working save tree and is **tier-3 free-to-edit** under the repository-wide save-tier rule (root `CLAUDE.md` "Workflow: save file access tiers"). Agents may copy save trees in, overwrite existing folders, rename, delete, hand-edit. The whole reason this folder exists is autonomous test driving.

The **client save folder** (path in `DEV.md`) remains tier-1 off-limits unconditionally. NEVER reach into it to seed or harvest saves; the developer copies any client save out to a tier-2 location (typically a Downloads scratch path) before agents may read from it. Tier-2 sources are read-only: copy out from them into `data/saves/`, never write back.

Restoring a save under test, end-to-end:

1. Developer drops the source save tree under, for example, `C:\Users\jori\Downloads\<SaveName>\`.
2. Agent copies that folder into `DedicatedServer/data/saves/<SaveName>/`, overwriting the existing destination if any.
3. Agent runs `-Start -Load <SaveName> -Map <Map>`.

If the developer has not placed a save anywhere and a test asks for `-Start -Load`, that command will fail. Either ask the developer to provide the save, or use `-Start -New <Map>` to start a fresh world (the dedicated server creates that file inside `data/saves/` itself).

## Version coupling with the client

The server's `BepInEx/` tree, including StationeersLaunchPad and its sibling plugins (LaunchPadBooster, StationeersMods.Interface, StationeersMods.Shared, NetworkBufferFix), must match the client's exactly. The bootstrap step copies these from the path documented in `DEV.md` and exposed to MSBuild as `$(StationeersPath)`. Whenever the developer updates the client, run `-Bootstrap` again to re-sync. The launcher does NOT auto-detect drift; if versions diverge the StationeersLaunchPad join handshake rejects clients with a version-mismatch error, which is the cue to re-bootstrap.

## Session lock (acquire before anything)

One dedicated-server install is shared by every agent on this machine, and a test is many start/stop cycles. "Is a server process running" is therefore not enough to tell whether a session is in progress: between a `-Stop` and the next `-Start` nothing runs, yet the session is still active. The session lock marks "a session owns the dedi" across those gaps so a second agent does not stomp the saves, the deployed mods, or the world.

**The single source of truth for the lock rules is `DedicatedServer/session.lock.template`. Read it before driving the server.** The active lock is `DedicatedServer/session.lock` (gitignored), managed by the launcher. The essentials:

- **Acquire once, before any mutating command:** `dedicated-server.ps1 -Lock -Purpose "Playtesting <what> for <mod>"`. It prints a short owner id; pass `-As <id>` on every mutating command after that (`-Bootstrap`, `-DeployMods`, `-SyncMods`, `-Start`, `-Save`, `-SendCommand`, `-Stop`). Read-only `-Status` / `-Logs` never need it.
- **The purpose is for the user.** Keep it short and generic. When you hit a lock you do not own, do not proceed: show its purpose to the user and let the user decide. Never use `-Force` on your own; it is human-gated.
- **It expires on a timer (default 10 min) so an idle agent frees the dedi.** Refresh (`-RefreshLock -As <id>`, or any mutating command) about once a minute ONLY while actively driving a test. Never refresh just to hold the server for an absent human, and never spawn a background refresher; either one starves other agents.
- **A connected player keeps the lock live regardless of the timer.** An active playtest is protected; a server left idle with nobody connected frees up when the timer lapses.
- **Waiting for the developer to join:** tell them the reservation window in plain terms, set `-TtlMinutes` to a sensible join window, then go idle. Do not poll-refresh.
- **On resume after any gap, re-check ownership first:** `dedicated-server.ps1 -Status -As <id>`. If another session holds it, stop, tell the user what took it (the reported purpose), and re-acquire only on their go-ahead.
- **Release when done:** `-Unlock -As <id>`, or `-Stop -As <id> -Release`.

## Operations

The launcher lives next to this file at `DedicatedServer/dedicated-server.ps1`. It reads:

- `<StationeersPath>` from `Directory.Build.props` at the repo root (the same property the mod builds use).
- `STEAMCMD_PATH` from the environment (set per `DEV.md`).

Anything either of these resolves to is the only externally-rooted path the launcher touches.

The agent runs the lifecycle end-to-end. The developer never types commands at the server console. `-Start` returns immediately; `-Stop`, `-Save`, `-SendCommand`, `-Status`, and `-Logs` coordinate with the running server through PID files and a control file under `data/`.

### Lifecycle architecture

`-Start` launches a hidden PowerShell host wrapper via `Start-Process`. The wrapper owns the dedicated server process: it spawns it with redirected stdin, polls `data/control.cmd` every 250 ms, and forwards each command into the server's stdin. The launcher returns as soon as the server has registered its PID. State files under `data/`:

- `data/host.pid`: PID of the host wrapper.
- `data/server.pid`: PID of `rocketstation_DedicatedServer.exe`.
- `data/control.cmd`: command queue. The agent writes via atomic rename; the wrapper reads and deletes. Only one command at a time can be pending.
- `data/server.log`: Unity log written directly by the dedicated server (`-logFile <path>`). Player-aware lock liveness reads the connected-player count from here by counting client connect (`Client <name> (<id>) is ready`) and disconnect (`Client disconnected: ...`) events. The log truncates per launch, so the whole file is the current run. The `clients` / `status` console commands write to the in-game console, not this log, so they cannot be scraped.
- `data/setting.xml`: server settings, written by the dedicated server itself on first run; persisted across restarts.

The session lock lives at `session.lock` in the folder root, not under `data/`, so it survives a `data/` wipe; see "Session lock" above.

When the server exits (clean quit, crash, or force-kill), the host wrapper's `finally` block removes `host.pid`, `server.pid`, and any stale control file. The session lock is deliberately NOT removed on server exit: it spans the whole session across stop/start cycles and is released only by `-Unlock` / `-Stop -Release`, or when its timer lapses with no player connected. If the host wrapper itself is killed (force-kill, machine reboot), the server can be left orphaned; `-Status` detects that case and `-Stop` cleans it up.

### Bootstrap (one-time, or after a client update)

```
DedicatedServer/dedicated-server.ps1 -Bootstrap
```

1. Validate `Directory.Build.props` `<StationeersPath>` and `$env:STEAMCMD_PATH`.
2. Run SteamCMD with `+force_install_dir` pointed at `DedicatedServer/install/`, install Steam app `600760` (`Stationeers Dedicated Server`) anonymously.
3. Mirror the client's `BepInEx/` tree into the server install. The destination `BepInEx/` is wiped first so a plugin removed on the client is removed on the server too. Also copies `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, and `changelog.txt` from the client install root when present.
4. Report the mirrored BepInEx version.

### Sync mods from the client (for tests against the user's full mod set)

```
DedicatedServer/dedicated-server.ps1 -SyncMods [-FromModConfig <path>]
```

Mirrors every enabled mod from the user's `modconfig.xml` (default: `%USERPROFILE%\Documents\My Games\Stationeers\modconfig.xml`) onto the server. For each enabled `<Workshop>` or `<Local>` entry:

- Source path comes from the entry's `<Path Value>` (already absolute on the user's machine).
- Destination name follows StationeersLaunchPad's Export Mod Package convention: `Workshop_<PublishedFileId>` for Workshop entries, `Local_<DirName>` for Local entries.
- Recursively copy the source folder's contents into `DedicatedServer/data/mods/<DestinationName>/`.

Then writes a baked `DedicatedServer/install/modconfig.xml` with one `<Local Enabled="true"><Path Value="<DestinationName>" /></Local>` per copied mod, plus `<Core Enabled="true">`. This replicates Export Mod Package without driving the client UI.

The source `modconfig.xml` and the user's mod folders are read-only. Per the repo-wide save-touch policy, ask before re-running `-SyncMods` if mods may have been deliberately staged or hand-edited on the server.

The local mods directory is `DedicatedServer/data/mods/` (under SavePath), NOT `DedicatedServer/install/mods/`. The full mechanism is documented in `Research/Workflows/StationeersLaunchPadDedicatedServer.md`.

### Deploy mods built from this repo

```
DedicatedServer/dedicated-server.ps1 -DeployMods [-Mod <name>] [-Configuration Release|Debug]
```

Copies built mod DLLs from `Mods/<X>/<X>/bin/<Configuration>/<X>.dll` into `DedicatedServer/install/BepInEx/plugins/<X>/<X>.dll`. Without `-Mod`, every mod under `Mods/` (except `Template`) is deployed. Configuration defaults to `Release`.

The launcher does not build for you. Build first via the developer's normal MSBuild flow (see `DEV.md`), then run `-DeployMods`.

This step is per-invocation and explicit so the agent driving the test always controls which mods at which build configuration are present on the server. Whenever code changes, re-run `-DeployMods` before `-Start`.

**Stop the server first.** `-DeployMods` (and `-SyncMods`) refuses to run if the dedi or its host wrapper is alive: on Windows the Mono runtime holds an exclusive file lock on every loaded plugin DLL, so overwriting `install/BepInEx/plugins/<X>/<X>.dll` mid-flight fails with a sharing violation or, worse, leaves a half-written DLL the next `-Start` picks up as broken plugin bytes. The correct order is always: `-Lock` (once per session), then `-Stop` (if anything is alive), then build, then `-DeployMods`, then `-Start`. The launcher enforces the alive-server check; the doc rule is here so the agent does not need to discover it by hitting the error.

**Important**: `-SyncMods` and `-DeployMods` deploy via different mechanisms. `-SyncMods` writes to `data/mods/<Source>_<DirName>/` (loaded by StationeersLaunchPad's `LocalModSource`). `-DeployMods` writes to `install/BepInEx/plugins/<X>/<X>.dll` (loaded by BepInEx's Chainloader). Running both for the same mod can produce duplicate-load conflicts (see the StationeersLaunchPad duplicate-load fatal documented in `DEV.md`). When testing this repo's own mods against the user's full Workshop set, prefer `-SyncMods` alone (the user's modconfig already lists this repo's mods as Workshop or Local, so they get the user's chosen version) and skip `-DeployMods` unless you specifically want to override one of them with a freshly-built variant.

### Start

```
DedicatedServer/dedicated-server.ps1 -Start -Load <SaveName> -Map <Map>
DedicatedServer/dedicated-server.ps1 -Start -New <Map>
```

Validates args, refuses if a server or host wrapper is already alive (use `-Stop` first), spawns the hidden host wrapper, then waits up to 20 s for the server to register `data/server.pid`. Returns once the PID is alive. Flag set the host wrapper applies to `rocketstation_DedicatedServer.exe`:

```
-batchmode
-nographics
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
-load <SaveName> <Map>      OR    -new <Map>
```

If `-Load <SaveName>` references a save that does not exist under `data/saves/`, the call fails. Either copy a tier-2 source save into `data/saves/<SaveName>/` first, or use `-Start -New <Map>` for a fresh world.

### Status

```
DedicatedServer/dedicated-server.ps1 -Status
```

Reports:

- whether the host wrapper is alive (PID),
- whether the server is alive (PID, uptime),
- the last line of `data/server.log`,
- any pending command in `data/control.cmd`.

Warns when the server is alive but the host wrapper is gone (orphan).

### Logs

```
DedicatedServer/dedicated-server.ps1 -Logs [-Tail <N>] [-Grep <regex>]
```

Tails the last N lines of `data/server.log` (default 50), or filters the whole log by regex. The dedicated server writes here directly via `-logFile`, so this works whether the server is running or already stopped.

### Save (with confirmation)

```
DedicatedServer/dedicated-server.ps1 -Save -Name <SaveName> [-WaitSeconds <N>]
```

Queues `save "<SaveName>"` on the server's stdin (via the control file) and waits up to `WaitSeconds` (default 30) for a `Saved...<SaveName>` line in the log. Reports confirmed or warns on timeout. The dedicated server does NOT autosave on quit, so explicit `-Save` is the only way to persist state before `-Stop`.

### Send a raw command

```
DedicatedServer/dedicated-server.ps1 -SendCommand -Command '<text>'
```

Forwards an arbitrary string to the server's stdin. Use for commands the launcher does not have a dedicated wrapper for (`status`, `serverrun`, etc.). Fire-and-forget; does not wait for any log response.

### Stop

```
DedicatedServer/dedicated-server.ps1 -Stop [-SaveAs <SaveName>] [-TimeoutSeconds <N>]
```

If `-SaveAs` is given, queues `save "<SaveName>"` and waits up to `TimeoutSeconds` (default 30) for confirmation in the log before sending `quit`. Then waits for the server process to exit. Force-kills the server and host wrapper if either is still alive after the timeout. Cleans up all PID files and the control file.

If nothing is running, the call is a no-op (cleans stale state files defensively).

## Joining as a client

The developer launches the regular Stationeers client and uses Direct Connect to `127.0.0.1:28016`. No password is required. There is no `-connect` command-line flag on the client; this step is manual.

The launcher pins `LocalIpAddress 127.0.0.1` so the dedicated server binds RakNet to the loopback interface only. Without that pin RakNet picks the first up interface, which on a developer machine with a LAN connection is typically the LAN IP (e.g. `10.20.30.200`); Direct Connect to `127.0.0.1:28016` then fails because nothing is listening there. See `Research/Workflows/StationeersLaunchPadDedicatedServer.md` "Port-binding behaviour with a running client on the same machine" for the underlying behaviour. To expose the server to other machines on the LAN intentionally, override the bind by editing the `LocalIpAddress` line in `DedicatedServer/dedicated-server.ps1`.

The default `28016` for the dedicated server's `GamePort` is offset by +1000 from the Stationeers client default `27016`, so the dedicated server runs alongside a hosting client on the same machine without RakNet's port-binding fallback. To override, pass `-GamePort N -UpdatePort N` to `-Start`.

No `ServerPassword` is set. The server is loopback-only by design (LocalIpAddress 127.0.0.1), so unauthenticated connections from outside the machine are impossible at the network layer; a connection password adds no protection in that topology and just gets in the way of agent-driven and developer test loops. `ServerAuthSecret` is still set to `x` so a connected client can run admin commands on the server via the in-game `serverrun` command (see `Research/GameSystems/DedicatedServerSettings.md`).

## Files outside this folder touched by operations

- The client install (path in `DEV.md`, `$(StationeersPath)`): read-only mirror source for `BepInEx/` and the doorstop loader files during `-Bootstrap`. Never written.
- `Mods/<ModName>/<ModName>/bin/<Configuration>/<ModName>.dll`: read-only source for `-DeployMods`.
- The SteamCMD executable (path in `DEV.md`, `STEAMCMD_PATH`): invoked during `-Bootstrap`. SteamCMD writes to its own state directories; nothing here.
- The Unity per-user persistent data folder for `Rocketwerkz/rocketstation` (path in `DEV.md`): `PlayerCookie-v2.xml` is read and may be written by the dedicated server itself. Shared with the client. Carries player ID and dismissed-popup flags. Not save data.
- The `HKCU\Software\Rocketwerkz Limited\rocketstation` registry key: Unity PlayerPrefs, shared with the client. Not save data.
- The client's save folder (path in `DEV.md`): NEVER read or written.

## Manual cleanup

There is no `-Clean` action. Cleaning is the developer's call:

- To wipe binaries and start a fresh bootstrap, delete `install/` and re-run `-Bootstrap`. Saves and settings under `data/` survive.
- To wipe state (settings, log, scripts), delete `data/setting.xml`, `data/server.log`, `data/scripts/`. `data/saves/` is also free to delete or rebuild as part of a test cleanup; per the tier-3 rule above, the dedicated-server save tree is agent-managed.
- To wipe everything including saves, the developer does that manually with explorer or rm. An agent must not.

## Notes for agents

- This file auto-loads when you touch any path inside `DedicatedServer/`. If your work involves only `DedicatedServer/dedicated-server.ps1` and never reads or writes inside this folder, read this file explicitly.
- Never commit anything in this folder other than this `CLAUDE.md`, the launcher `dedicated-server.ps1`, `session.lock.template`, and source code under `dev-plugins/`. The `.gitignore` rule (`/DedicatedServer/*` plus `!` exceptions for those four targets) makes this automatic for `git add`, but `git add -f` would bypass it; do not bypass it. The active `session.lock`, the entire `install/` tree, the entire `data/` tree, and the `dev-plugins/<X>/<X>/bin/` and `obj/` build outputs all stay gitignored.
- All InspectorPlus snapshot conventions apply on the server too. Drop request files in `install/BepInEx/inspector/requests/` and read `install/BepInEx/inspector/snapshots/`. With no client connected the server simulation is paused and request files are not processed; for autonomous snapshots, enable InspectorPlus's `Force Unpause Without Client` setting (off by default) under `[Server - Headless]` in `install/BepInEx/config/net.inspectorplus.cfg`. See `Research/Workflows/InspectorPlusUsage.md`.

## Manipulating world state without a client (Path B + Path D)

A connected client is the high-friction way to set up a verification scenario. Two lower-friction paths exist; both are headless-driven so an agent can run them end-to-end without asking the developer to play. Pick the path that fits the operation; they compose freely.

### Path D: offline save-zip edit (`tools/save-edit/`)

Use Path D for changes to PERSISTED world state: adding or modifying Things, retargeting `CableNetworkId` references, toggling `OnOff` on a device that will be loaded later. The tool reads a save ZIP, mutates `world.xml`, writes a new ZIP. Game is not running. No lock needed for the edit itself; the lock is only needed when starting the server with the resulting save.

```
python tools/save-edit/stationeers_save.py list   <save.zip> [--prefab P] [--type StructureSaveData] [--limit N]
python tools/save-edit/stationeers_save.py show   <save.zip> --ref <ReferenceId>
python tools/save-edit/stationeers_save.py set    <save.zip> <out.zip> --ref <ReferenceId> --field <xpath> --value <V>
python tools/save-edit/stationeers_save.py clone  <save.zip> <out.zip> --ref <template-ref> --pos X,Y,Z [--rot QX,QY,QZ,QW]
python tools/save-edit/stationeers_save.py add-network  <save.zip> <out.zip> --id <NetworkId>
python tools/save-edit/stationeers_save.py drop-network <save.zip> <out.zip> --id <NetworkId>
```

Always work on a COPY in `data/saves/`. The original Luna save is the developer's; agents may copy it to `data/saves/Luna_pgp_task1/` (or similar) and edit freely there. Full rules: `tools/save-edit/README.md` and `Research/Protocols/SaveFileStructure.md` / `Research/Protocols/WorldXml.md`.

What Path D does well:
- Set OnOff, Setting, Mode, CurrentBuildState, DamageState fields on existing Things.
- Clone an existing Thing to a new world position (the type-specific tail of the XML is preserved verbatim; only `ReferenceId` and position change).
- Add or drop CableNetworkIds from the top-level list.

What Path D does badly (use Path B instead):
- Wiring fresh Things into a coherent CableNetwork. Adjacency-based registration is decided by `Cable.OnRegistered`, not by the XML. Hand-positioning cells correctly for adjacency is error-prone.
- Anything that depends on a specific simulation tick (e.g. "snap state after the third simulation tick"). Use RuntimeProbe for that.

### Path B: in-game scenario plugin (`dev-plugins/RuntimeProbe/`)

Use Path B for changes to LIVE simulation state and for RUNTIME OBSERVATION at a known simulation tick. `RuntimeProbe` is a developer BepInEx plugin that lives at `DedicatedServer/dev-plugins/RuntimeProbe/`, next to this CLAUDE.md and the launcher. It loads via StationeersLaunchPad, runs a scenario picked by a config string, and logs structured `[RuntimeProbe] ...` lines to `install/BepInEx/LogOutput.log`. An agent greps the log instead of staging InspectorPlus request files.

It is intentionally NOT in `Mods/` or `Plans/`: it never ships to the Workshop, never graduates to a release mod, and only makes sense paired with the dedi launcher. `dev-plugins/` is the home for this category. New dev-plugins follow the same shape (`<Name>/<Name>.sln` + `<Name>/<Name>/` source folder + `About/`) and slot in next to `RuntimeProbe/`.

Build and deploy:

```
dotnet build DedicatedServer/dev-plugins/RuntimeProbe/RuntimeProbe.sln -c Release
DedicatedServer/dedicated-server.ps1 -DeployMods -As <id> -Mod RuntimeProbe -Configuration Release
# The launcher's -Mod search order is Mods/<name>/, then Plans/<name>/, then
# DedicatedServer/dev-plugins/<name>/. -DeployMods writes the DLL to
# install/BepInEx/plugins/<Mod>/ as usual.
```

`-DeployMods` only writes to `install/BepInEx/plugins/`. RuntimeProbe ALSO needs to be loaded by StationeersLaunchPad, which scans `data/mods/Local_*` per `install/modconfig.xml`. For a first-time deploy on a fresh dedi install, mirror the source folder once:

```
# One-time: mirror About + the built DLL to data/mods/ so StationeersLaunchPad picks it up.
mkdir -p DedicatedServer/data/mods/Local_RuntimeProbe
cp -r DedicatedServer/dev-plugins/RuntimeProbe/RuntimeProbe/About DedicatedServer/data/mods/Local_RuntimeProbe/
cp DedicatedServer/dev-plugins/RuntimeProbe/RuntimeProbe/bin/Release/RuntimeProbe.dll DedicatedServer/data/mods/Local_RuntimeProbe/

# Then add an entry to install/modconfig.xml before </ModConfig>:
#   <Local Enabled="true">
#     <Path Value="C:\<repo>\DedicatedServer\data\mods\Local_RuntimeProbe" />
#   </Local>
```

Critical: with the same DLL in BOTH `install/BepInEx/plugins/RuntimeProbe/` AND `data/mods/Local_RuntimeProbe/`, BepInEx Chainloader and StationeersLaunchPad each load it, the plugin's `Awake` fires twice, every Harmony prefix is registered twice, and side-effecting patches double. Pick ONE path. The convention on this dedi is `data/mods/Local_<X>/` via StationeersLaunchPad; the `install/BepInEx/plugins/RuntimeProbe/` copy that `-DeployMods` writes should be deleted after the first deploy. (A future launcher revision will pick the right path automatically.)

Configure the scenario:

```
# Edit install/BepInEx/config/net.runtimeprobe.cfg:
#   Scenario = inventory                       (general)
#   Scenario = battery-charge-snapshot         (general; needs no mod)
#   Scenario = pgp-transformer-conservation    (requires PowerGridPlus loaded)
#   Scenario = pgp-battery-efficiency-probe    (requires PowerGridPlus loaded)
#   Scenario = pgp-apc-idle-probe              (requires PowerGridPlus loaded)
#   Scenario = pgp-cable-burn-probe            (requires PowerGridPlus loaded)
DedicatedServer/dedicated-server.ps1 -Start -As <id> -Load <save> -Map <Map>
grep "\[RuntimeProbe\]" DedicatedServer/install/BepInEx/LogOutput.log
```

Mod-specific scenarios are prefixed (e.g. `pgp-` for PowerGridPlus); they no-op with a warning if the named mod is absent. Add new scenarios by editing `DedicatedServer/dev-plugins/RuntimeProbe/RuntimeProbe/ScenarioRunner.cs`, adding a `case` to the switch and a `Scenario_*` method, then rebuilding. Full scenario catalogue and authoring guide in `DedicatedServer/dev-plugins/RuntimeProbe/README.md`.

Why a plugin and why this pump source: on a headless dedicated server `MonoBehaviour.Update` does not fire after world load, and the top-level `GameManager.GameTick` is an async UniTask state machine that switches to a ThreadPool worker (which crashes `UnityEngine.Object.FindObjectsOfType` calls; see `Research/Patterns/ThingEnumerationOffMainThread.md`). RuntimeProbe drives off a Harmony postfix on `ElectricityManager.ElectricityTick`, the same pump InspectorPlus uses, so scenario code runs once per simulation tick when `GameManager.RunSimulation` is true. The scenario dispatcher deduplicates by `Time.frameCount`, so adding a second pump (e.g. an atmospheric tick) is safe: a second Harmony patch class targets that method and calls `ScenarioRunner.OnSimTick()` from its postfix; the dispatcher fires the scenario only once per simulation frame regardless of how many pumps registered.

### Composing the two

Most verification flows are best as Path D + Path B together:
1. Path D copies the developer's save, sets the world to a known state (turn off unrelated providers, set Setting on a transformer to a known value).
2. Path B observes the simulation under that state, dumping the relevant fields to the log on a known tick offset.

Save edit always finishes before `-Start`. Scenario logs always come from after `-Start` plus `Delay Ticks` ticks. Agents should reach for save-edit first (cheap and reversible), then RuntimeProbe for the runtime read.

### Standard test loop (agent owns lifecycle)

1. Acquire the session lock: `DedicatedServer/dedicated-server.ps1 -Lock -Purpose "Playtesting <what> for <mod>"`. Note the printed owner id and pass `-As <id>` on every mutating command below. Rules: `session.lock.template`.
2. If a previous run is still alive (`-Status` shows host or server PID up), `-Stop -As <id>` first. Required before any rebuild + redeploy: the Mono runtime holds an exclusive file lock on every loaded plugin DLL, so `-DeployMods` on a running server fails or corrupts the DLL in place. The launcher enforces this check, but the test loop should never hit it.
3. Build the mod(s) under test via the developer's MSBuild flow (see `DEV.md`).
4. `DedicatedServer/dedicated-server.ps1 -DeployMods -As <id> -Mod <X>` (or all mods, no `-Mod` flag).
5. `DedicatedServer/dedicated-server.ps1 -Start -As <id> -New <Map>`, OR ask the developer for a save name and use `-Start -As <id> -Load <save> -Map <Map>`. The launcher returns within ~5 s of the server registering its PID.
6. Wait until the server is ready: poll `DedicatedServer/dedicated-server.ps1 -Logs -Grep 'World loaded'` (or another readiness marker) before asking the developer to join. Timing varies by save size; budget 10-60 s.
7. Tell the developer: "Server is up at `127.0.0.1:28016`, no password. Join with the regular client via Direct Connect when you are ready." If you then go idle waiting on them, state the reservation window (`-TtlMinutes`) and that a connected player holds the lock open. That is the only manual step.
8. Run the test. While actively driving it, refresh the lock about once a minute (any mutating command refreshes it; otherwise `-RefreshLock -As <id>`). Drop InspectorPlus request files into `install/BepInEx/inspector/requests/`, read snapshots out of `install/BepInEx/inspector/snapshots/`. Use `-SendCommand -As <id> -Command 'status'` or `-Logs -Grep <pattern>` to check server-side state.
9. If you resume after a gap, re-check ownership first: `-Status -As <id>`. If another session now holds the lock, stop and tell the user what took it.
10. To preserve state for a follow-up session: `-Save -As <id> -Name <SaveName>`. Confirmation comes back via the log.
11. Tear down and release: `-Stop -As <id> -Release` (no save, throws away the run) or `-Stop -As <id> -SaveAs <SaveName> -Release` (saves first, then quits cleanly). Omit `-Release` only if you are keeping the lock for an immediate follow-up.

### Sanity checks before declaring "ready for the developer"

- `-Status -As <id>` shows both host wrapper and server alive, and the lock is YOURS.
- `-Logs -Grep 'Patches applied successfully'` returns one line per deployed mod.
- `-Logs -Grep '\[Error\]|\[Fatal\]'` is empty (or lists only known-benign warnings).

If any check fails, fix the underlying issue (most often a stale BepInEx mirror after a client update; re-run `-Bootstrap`) before handing the session to the developer.
