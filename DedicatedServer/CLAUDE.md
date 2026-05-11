# DedicatedServer

This folder holds a self-contained Stationeers Dedicated Server install used to test the mods in this monorepo against multiplayer code paths. It is isolated from the developer's client install and from any client save folder.

The folder is gitignored except for this file. Anything that lands in `install/` or `data/` is local-only and never committed.

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
- `data/server.log`: Unity log written directly by the dedicated server (`-logFile <path>`).
- `data/setting.xml`: server settings, written by the dedicated server itself on first run; persisted across restarts.

When the server exits (clean quit, crash, or force-kill), the host wrapper's `finally` block removes `host.pid`, `server.pid`, and any stale control file. If the host wrapper itself is killed (force-kill, machine reboot), the server can be left orphaned; `-Status` detects that case and `-Stop` cleans it up.

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

**Important**: `-SyncMods` and `-DeployMods` deploy via different mechanisms. `-SyncMods` writes to `data/mods/<Source>_<DirName>/` (loaded by SLP's `LocalModSource`). `-DeployMods` writes to `install/BepInEx/plugins/<X>/<X>.dll` (loaded by BepInEx's Chainloader). Running both for the same mod can produce duplicate-load conflicts (see the StationeersLaunchPad duplicate-load fatal documented in `DEV.md.template`). When testing this repo's own mods against the user's full Workshop set, prefer `-SyncMods` alone (the user's modconfig already lists this repo's mods as Workshop or Local, so they get the user's chosen version) and skip `-DeployMods` unless you specifically want to override one of them with a freshly-built variant.

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
-settings AutoSave          true
-settings UPNPEnabled       false
-settings ServerName        "Local Test"
-settings ServerMaxPlayers  4
-settings ServerPassword    x
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

The developer launches the regular Stationeers client and uses Direct Connect to `127.0.0.1:28016` with password `x`. There is no `-connect` command-line flag on the client; this step is manual.

The default `28016` for the dedicated server's `GamePort` is offset by +1000 from the Stationeers client default `27016`, so the dedicated server runs alongside a hosting client on the same machine without RakNet's port-binding fallback (see `Research/Workflows/StationeersLaunchPadDedicatedServer.md` "Port-binding behaviour with a running client on the same machine"). To override, pass `-GamePort N -UpdatePort N` to `-Start`.

The password is hardcoded to `x` in the launcher's flag set so the server is never exposed unauthenticated, even if it accidentally binds a public interface. The same value is also set as `ServerAuthSecret`, which lets a connected client run admin commands on the server via the in-game `serverrun` command without writing to the server's stdin (see `Research/GameSystems/DedicatedServerSettings.md` for the full mechanic). To rotate either value, edit the corresponding `'-settings', '...', 'x'` line in `DedicatedServer/dedicated-server.ps1` and restart the server.

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
- Never commit anything in this folder other than this `CLAUDE.md`. The `.gitignore` rule (`/DedicatedServer/* + !/DedicatedServer/CLAUDE.md`) makes this automatic for `git add`, but `git add -f` would bypass it; do not bypass it.
- All InspectorPlus snapshot conventions apply on the server too. Drop request files in `install/BepInEx/inspector/requests/` and read `install/BepInEx/inspector/snapshots/`.

### Standard test loop (agent owns lifecycle)

1. Build the mod(s) under test via the developer's MSBuild flow (see `DEV.md`).
2. `DedicatedServer/dedicated-server.ps1 -DeployMods -Mod <X>` (or all mods, no `-Mod` flag).
3. `DedicatedServer/dedicated-server.ps1 -Start -New <Map>`, OR ask the developer for a save name and use `-Start -Load <save> -Map <Map>`. The launcher returns within ~5 s of the server registering its PID.
4. Wait until the server is ready: poll `DedicatedServer/dedicated-server.ps1 -Logs -Grep 'World loaded'` (or another readiness marker) before asking the developer to join. Timing varies by save size; budget 10-60 s.
5. Tell the developer: "Server is up at `127.0.0.1:27016`. Join with the regular client via Direct Connect when you are ready." That is the only manual step.
6. Run the test. Drop InspectorPlus request files into `install/BepInEx/inspector/requests/`, read snapshots out of `install/BepInEx/inspector/snapshots/`. Use `-SendCommand -Command 'status'` or `-Logs -Grep <pattern>` to check server-side state.
7. To preserve state for a follow-up session: `-Save -Name <SaveName>`. Confirmation comes back via the log.
8. Tear down: `-Stop` (no save needed, throws away the run) or `-Stop -SaveAs <SaveName>` (saves first, then quits cleanly).

### Sanity checks before declaring "ready for the developer"

- `-Status` shows both host wrapper and server alive.
- `-Logs -Grep 'Patches applied successfully'` returns one line per deployed mod.
- `-Logs -Grep '\[Error\]|\[Fatal\]'` is empty (or lists only known-benign warnings).

If any check fails, fix the underlying issue (most often a stale BepInEx mirror after a client update; re-run `-Bootstrap`) before handing the session to the developer.
