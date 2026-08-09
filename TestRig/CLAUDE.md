# TestRig

`TestRig/` holds the two tools that drive Stationeers for testing. They are peers, and a multiplayer test uses one or both.

- **`TestRig/DedicatedServer/`** runs a headless dedicated server. Operating manual: `TestRig/DedicatedServer/CLAUDE.md`.
- **`TestRig/ClientRig/`** provisions and drives N isolated game clients through the `ClientDriver` BepInEx plugin. Read `TestRig/ClientRig/CLAUDE.md` first (it auto-loads and is short); operating manual: `TestRig/ClientRig/README.md`; durable internals: `TestRig/ClientRig/RESEARCH.md`.

**This file auto-loads for work in EITHER half, which is why the shared rules live here rather than in one half's manual.** A rule that only appears under `DedicatedServer/` is invisible to an agent working the client rig, and the reverse. Read the half-specific manual on top of this file, never instead of it.

## Two ways to host a world, and how to choose

"Host a world" used to mean the dedicated server. It does not any more: a client-rig instance provisioned with `-Role host` and driven through `POST /host` becomes a **listen host**, one process that runs the simulation, accepts joiners over loopback RakNet, and plays a character.

| | Dedicated server | Listen host (client rig) |
|---|---|---|
| Has a player character | no | **yes** |
| `NetworkRole` | `Server` | `Server` (they differ only by `GameManager.IsBatchMode`) |
| Reached at | `127.0.0.1:28016` | `127.0.0.1:<the instance's game port>`, 27800 plus the instance index by default |
| Driven by | `dedicated-server.ps1`, console commands through stdin, `ScenarioRunner` | `client-rig.ps1`, the `ClientDriver` HTTP control plane |
| Renders, runs `GameManager.Update` | no | yes |
| Auto-pause with nobody connected | supported through `AutoPauseServer`, which this rig's launcher pins false | never, at any setting: both call sites are gated on `IsBatchMode` |
| Costs | one process, low RAM | about 10 GB of RAM in world, like any other instance |

Pick the dedicated server when the test needs a server that is not a player: long soak runs, `ScenarioRunner` probes, save-edit round trips, anything where the host's own client half is noise. Pick a listen host when the test needs **a host who plays**: a host holding an item, a host's own client-half setting, anything where "the host does X and the joiner sees Y" is the assertion. Game internals for the mode: `Research/GameSystems/ListenHost.md`.

Both are still one rig and one lock. A listen host does not make the client half independent of the server half.

Both halves point real, running game processes at the developer's own machine. Read the safety sections below before running anything under `TestRig/`.

## What the rig touches outside its own folder

Neither half is as isolated as its folder makes it look. These are shared with the developer's own Stationeers client and with each other, and nothing in the game separates them:

| Shared artifact | Who touches it | Notes |
|---|---|---|
| The Stationeers client install (`$(StationeersPath)`) | Both | **Read-only, always.** The client rig hard-links about 1,050 files out of it per instance; the dedicated server mirrors `BepInEx/` out of it on `-Bootstrap`. A hard link shares the file data, so anything the game writes to must be a real copy, never a link. |
| `%USERPROFILE%\AppData\LocalLow\Rocketwerkz\rocketstation\PlayerCookie-v2.xml` | Both, plus the developer's client | Unity `persistentDataPath`, which cannot be separated: the player takes company and product from the serialized PlayerSettings inside `globalgamemanagers`, so editing `app.info` does nothing. `ClientDriver` protects it by suppressing `PlayerCookie.Save()`. Carries player ID and dismissed-popup flags. Not save data. |
| `HKCU\Software\Rocketwerkz\rocketstation` | Both, plus the developer's client | Unity PlayerPrefs. Not save data. |
| `Player.log` / `Player-prev.log`, `Blueprints\` | Both, plus the developer's client | Same `persistentDataPath` root. The client rig always passes a unique `-logFile` because two instances sharing the default silently destroy the developer's `Player-prev.log`. |
| `<USER_DOCUMENTS>\My Games\Stationeers\` (`modconfig.xml`, `mods/`) | The client rig, read-only source for `-Provision`; the dedicated server, read-only source for `-SyncMods` | Read-only in both directions. `-settings SavePath` is never passed on the client, because StationeersLaunchPad then rewrites the developer's shared `modconfig.xml` with every `<Local>` entry deleted. |
| **UDP game ports (RakNet)** | The dedicated server binds 28016 (28015 update); a client-rig listen host binds its own game port, 27800 plus the instance index by default | The machine's ports are one namespace and a listen host now takes one. Two RakNet sockets on one port do NOT conflict: both bindings coexist and a datagram goes to whichever matches its destination address, so a joiner reaches something and the test is confidently wrong with no error anywhere. `-Provision` refuses a game port that collides with another instance, with 28015/28016, or with the game client's own 27015/27016. Nothing outside the rig is checked, so a hand-picked port is the caller's problem. |
| TCP control-plane ports | The client rig, 27700 plus the instance index | Loopback only, one per instance, refused on collision at provision time. |
| The client save folder (`<USER_DOCUMENTS>\My Games\Stationeers\saves\`) | Nobody | Tier 1, off-limits unconditionally. See "Saves" below. |

That shared per-Windows-user state is why there is one lock rather than two.

## Session lock: one lock, both halves

**The rig is a shared single-instance resource and every mutating command needs the lock.** One lock at `TestRig/session.lock` covers `DedicatedServer/` and `ClientRig/` together.

**The single source of truth for the rules is `TestRig/session.lock.template`. Read it before driving either half.** The implementation is `TestRig/rig-lock.ps1`, dot-sourced by both launchers so the timer, ownership, break-lock gate and release semantics cannot drift between them. The essentials:

- **Acquire once, from either launcher, before any mutating command.** `-Lock -Purpose "<what you are testing>"` prints a short owner id; pass `-As <id>` on every mutating command afterwards, on **both** launchers. Acquisition is serialised across processes by a named system mutex, so two agents that both find the rig free cannot both walk away believing they own it.
- **A NEW lock also resets the rig's between-session state**, so you start on a clean rig rather than on the last session's leftovers. See "State hygiene" below; the short version is that it is refused while the rig is in use, never fires when you re-assert a lock you already hold, and `-KeepState` opts out loudly.
- **Mutating on the dedicated server:** `-Bootstrap`, `-DeployMods`, `-SyncMods`, `-Start`, `-Save`, `-SendCommand`, `-Stop`. Free: `-Status`, `-Logs`.
- **Mutating on the client rig:** `-Provision`, `-Start`, `-Stop`, `-Save`, `-Remove`, `-Broadcast`, `-Call`. Free: `-Status`, `-List`, `-Logs`, `-Snapshot`, `-Wait` (which refreshes a lock you already hold, since a barrier can outlast the TTL).
- **Hitting another session's live lock fails immediately by default.** `-Lock -Purpose "..." -WaitSeconds N` queues for up to N seconds instead, printing the holder's purpose while it waits. It is a queue, not a reservation: no ordering fairness is promised, and there is no unbounded wait.
- **It expires on a timer (default 10 min) so an idle agent frees the rig.** Refresh (`-RefreshLock -As <id>`, or any mutating command) about once a minute ONLY while actively driving a test. Never refresh to hold the rig for an absent human, and never spawn a background refresher.
- **A busy rig stays live regardless of the timer.** Busy means a player is connected to the running dedicated server, or at least one client-rig instance process is alive. The reported reason names what is actually happening (how many instances, which one is HOSTING, how many clients are connected to it) because that text is what a human reads when deciding whether to authorise a `-BreakLock`. A pid file alone is not proof: the launcher checks the process really is the game, so a recycled process id cannot pin the lock live forever.
- **An untracked game process is reported but is NOT busy.** An orphan from a killed launcher or a crashed test belongs to no pid file, so no launcher action can stop it; counting it as busy would pin the lock with no way out but `-BreakLock`. `-Status` names it and its pid; kill it with `Stop-Process -Id <pid> -Force`.
- **Release when done:** `-Unlock -As <id>`, or `-Stop -As <id> -Release`. A running client instance has no timer to save you: leaving instances up holds the WHOLE rig. Always `client-rig.ps1 -Stop -All -As <id>` before releasing.
- **Tear a listen host down before releasing.** `-Unlock` REFUSES while a client-rig host instance is live, because releasing hands the rig to the next agent while a world is up and connected, and that agent's `-Stop -All` ends the test with nothing left to say it happened. `dedicated-server.ps1 -Unlock -Force` overrides that one refusal (`client-rig.ps1` accepts `-Force` but does not currently forward it to the release, so stop the instances or unlock from the server half). `-Force` is not a lock breaker.
- **`-Stop -Release` asks for the lock STATE before it releases, and that order is load-bearing.** The state check self-renews a busy session's expired lock and reports it as foreign, which is what stops an unrelated `-Stop -Release` from freeing it. Do not reorder those two steps in either launcher.
- **On resume after any gap, re-check ownership first:** `-Status -As <id>` on either launcher.
- **Breaking another session's live lock is `-BreakLock`, and it is human-gated.** Never use it without the user's explicit say-so.

`TestRig/rig-lock.tests.ps1` exercises all of the above offline against a temp directory: the state machine, the timer's fail-closed cases, ownership, `-BreakLock`, the busy signal, the file format, the release ordering, and real concurrent processes racing for one lock. `TestRig/rig-reset.tests.ps1` does the same for the state reset described below. Run both after any change to `TestRig/rig-lock.ps1` or `TestRig/rig-reset.ps1`:

```powershell
pwsh -NoProfile -File TestRig/rig-lock.tests.ps1
pwsh -NoProfile -File TestRig/rig-reset.tests.ps1
```

## State hygiene: a new lock gets you a clean rig

**Taking a NEW lock RESETS the rig's between-session state.** The point is that a playtest cannot suddenly run badly because of garbage an unrelated playtest left behind, and that the guarantee holds by construction rather than by a rule somebody remembers. The lock is the only mandatory choke point that already exists and is already enforced in code, so an agent cannot get the rig without getting it clean and cannot route around it. Implementation: `TestRig/rig-reset.ps1`, dot-sourced by both launchers next to `rig-lock.ps1`. Full rules: `TestRig/session.lock.template`.

| Half | Reset | Kept |
|---|---|---|
| Client, per instance | `data/<n>/setting.xml` (it carries `StartLocalHost`), `data/<n>/userdata/saves/`, the Unity logs, `imgui.ini`, a STALE `game.pid`, the instance's `BepInEx/config` (re-copied from the source install, then `SavePathOverride` re-applied), `LogOutput.log*`, `BepInEx/cache/`, `BepInEx/inspector/requests/` and `snapshots/` | `data/rig.json`, `instance.json`, `provision.stamp`, `userdata/mods/` and `modconfig.xml`, the deployed `ClientDriver`, the hard links |
| Dedicated server | the ScenarioRunner `Scenario` value (blanked, the rest of the file untouched), `install/BepInEx/scenariorunner/requests/` and `give/`, `install/BepInEx/inspector/requests/` and `snapshots/`, `data/setting.xml`, stale `server.pid` / `host.pid` / `control.cmd` | `data/saves/`, `data/mods/`, `install/modconfig.xml`, the deployed plugin DLLs, every other `install/BepInEx/config/*.cfg` |

Three things it reports instead of touching, because deleting them would be worse than leaving them: a seeded mod older than its source tree (the fix is `-Provision -Force`, not a delete), the dedicated server's retained save count and total size (there is no retention policy anywhere), and any server config that changed since the last reset (the rig-owned versus mod-owned split is undecided).

Rules an agent needs:

- **It is refused while the rig is in use** (any live client instance, a live dedicated server process, or an untracked game process). The lock is still acquired and its owner id still printed, with a loud warning naming what is running. An unclean rig must not become an unlockable one.
- **Re-asserting a lock you already hold never resets.** Changing your purpose or TTL mid-test would otherwise wipe your own run.
- **`-Lock -KeepState` skips it**, and prints exactly what it skipped. That is the escape hatch for a save, a config value or a scenario staged on purpose.
- **The reset prints what it did, per instance.** A silent reset is indistinguishable from no reset when something later goes wrong.
- **It finds each instance's tree through the `instancesRoot` recorded in `ClientRig/data/rig.json`**, because the trees normally sit on the game install's volume rather than inside `TestRig/`. It used to assume one configured root, so on a rig built with `-InstancesRoot` it found no `BepInEx/` tree and quietly did only half its work (no config re-copy, no `SavePathOverride` re-apply) while reporting nothing worse than "no instance tree". An entry from before that field existed falls back to the configured root and the report now names which of the two it used and what was skipped.
- **It resets BETWEEN sessions only.** A session spans many start/stop cycles by design, so two unrelated tests run under ONE lock get no reset between them. Release the lock and take it again when the subject changes. Per-test hygiene would make the unit of hygiene smaller than the unit of ownership, which is a lock-model change and has not been done.

**The shared per-user state is reported, never restored.** `PlayerCookie-v2.xml`, the PlayerPrefs key and `Blueprints\` cannot be isolated, and writing them back would itself be the write the save rules forbid. A cheap snapshot is taken at acquisition into `TestRig/session.state.json` (gitignored), and `-Unlock` / `-Stop -Release` print the delta. It fixes nothing; it turns state that was invisible until a later test failed into a line at the session boundary.

`Set-RigSavePathOverride` lives in `rig-reset.ps1` and `client-rig.ps1 -Provision` calls it. That is deliberate: the config re-copy WIPES `SavePathOverride`, and an instance without it writes its worlds into the developer's tier-1 save folder, so provisioning and resetting write that one setting through a single implementation. Do not add a second copy anywhere.

### `-Force` and `-BreakLock` are different things, on both launchers

This pair used to be one flag with opposite risk on the two halves, which is exactly how a live test gets torn down by muscle memory. The rule now, on both launchers:

- **`-Force`** overrides a refusal inside your own session, and is routine. On the client rig, `-Provision -Force` rebuilds an instance you already own. It never touches the lock.
- **`-BreakLock`** takes a live lock off another session, and is human-gated.

`-Force` no longer breaks a lock anywhere. If a doc, script or habit still says `-Force` for lock-breaking, it is stale.

### `-TimeoutSeconds` and `-WaitSeconds` mean the same thing on both launchers

- **`-TimeoutSeconds`** (default 30) is process-teardown grace for `-Stop`: how long the thing gets to exit cleanly before it is killed.
- **`-WaitSeconds`** is how long a blocking wait waits. On the dedicated server that is `-Save` waiting for its log confirmation (default 30); on the client rig it is the `-Wait` readiness barrier (default 300).

The client rig used to overload `-TimeoutSeconds` for its barrier. It does not any more; `-Wait -TimeoutSeconds 600` in an old note means `-Wait -WaitSeconds 600`.

**`-CallTimeoutSeconds` is a third flag, client rig only, and it is separate on purpose.** It is how long one `-Call` or `-Broadcast` request may take, and it defaults to 0, meaning "derive it from the request": the endpoint's own `timeoutMs` plus a margin, floored at 120 s and at 300 s for the endpoints that block for minutes. A fixed transport timeout used to win over whatever the caller asked the endpoint for, so `-Call -Path /connect -Body '{"timeoutMs":300000}'` died client-side at 120 s and the plugin's answer, which is the only thing that explains a failed join or host attempt, was never read. Giving it either of the two names above would have made one flag mean two things, which is the mistake the barrier rename above already had to undo once.

## Saves

The repository-wide save-tier rule is in the root `CLAUDE.md` under "Workflow: save file access tiers". Both rig save roots are **tier 3, agent-managed**:

- `TestRig/DedicatedServer/data/saves/` (the server's world tree)
- `TestRig/ClientRig/data/<instance>/userdata/` (each client instance's own save root, pointed at by StationeersLaunchPad's `SavePathOverride`). A listen host writes real worlds into `saves/` underneath it; `client-rig.ps1 -Save` is how they get there, and it reports confirmed or warns, never both.

Copy in, overwrite, rename, delete, hand-edit. `client-rig.ps1 -Remove` deletes an instance's save root along with its tree, by design, which for a host is the world every joiner was in. Both roots are still covered by the lock: they belong to whoever holds it.

**The developer's client save folder stays tier 1, off-limits unconditionally**, and no rig operation may reach into it. Three specific hazards on the client half, all sharpened by an instance that can now create a world:

- `POST /savepath` retargets `Settings.CurrentData.SavePath` on a **running** client. It refuses a path inside the developer's real user-data folder, but only when the caller does not pass `force=true`. That refusal is plugin code, not a rule, so an agent that passes `force=true` walks straight past it. Do not pass `force=true` on `/savepath` unless the user has asked for exactly that. (The refusal used to compare against `StationSaveUtils.DefaultPath`, which StationeersLaunchPad has already moved to the instance's own folder, so on a provisioned instance it refused the safe redirect and allowed the dangerous one. It now computes the real folder itself, the same way the launcher does. `GET /savepath` reports both paths so the difference is visible.)
- `SavePathOverride` is written on every provision, unconditionally, ahead of the mod seed. If it cannot be written (no `stationeers.launchpad.cfg` yet), a `-Role host` provision **throws** and a `-Role client` provision warns. Treat that warning as a stop, not a note: launch the instance once to generate the config, then re-provision with `-Force`.
- `POST /host` refuses to create a world when the instance's save root is not isolated from the developer's folder. The escape is `requireIsolatedSavePath=false`, and its only correct use is none.

## Dev-plugins: one convention, both halves

A **dev-plugin** is a BepInEx plugin that exists only to drive or observe the rig. It never ships to the Workshop, never graduates into `Mods/`, and only makes sense paired with its launcher. `ScenarioRunner` probes the dedicated server from inside; `ClientDriver` is the control plane inside each game client.

Every dev-plugin, on either half, uses this layout:

```
TestRig/<Half>/dev-plugins/<Name>/
    <Name>.sln
    <Name>/
        <Name>.csproj
        About/About.xml
        ... source ...
```

so folder, solution, project, assembly and namespace all carry the same name, and a second dev-plugin on either half slots in beside the first with nothing to rename. Today that is:

- `TestRig/DedicatedServer/dev-plugins/ScenarioRunner/`
- `TestRig/ClientRig/dev-plugins/ClientDriver/`

`WorkshopHandle` is 0 in a dev-plugin's `About.xml` and stays 0.

## Gitignore model: deny-all with an allowlist, on both halves

Both halves are **deny-all with a named allowlist**, not "ignored apart from scripts and docs". The difference matters: under deny-all, a stray artifact written into the folder (a `-Snapshot -OutFile before.json`, a `/screenshot?path=` PNG, a scratch dump) is ignored by default and cannot be committed by accident. The allowlist is small and explicit:

| Half | Tracked | Everything else |
|---|---|---|
| `TestRig/DedicatedServer/` | `CLAUDE.md`, `dedicated-server.ps1`, `dev-plugins/` source | ignored, including `install/`, `data/` |
| `TestRig/ClientRig/` | `CLAUDE.md`, `README.md`, `RESEARCH.md`, `client-rig.ps1`, `dev-plugins/` source | ignored, including `instances/`, `data/` (a host's worlds land under `data/<instance>/userdata/saves/`, so nothing a host writes is committable) |
| `TestRig/` itself | `CLAUDE.md`, `rig-lock.ps1`, `rig-lock.tests.ps1`, `rig-reset.ps1`, `rig-reset.tests.ps1`, `session.lock.template` | the active `session.lock` and the `session.state.json` shared-state baseline are ignored |

`dev-plugins/**/bin/` and `dev-plugins/**/obj/` stay ignored under both. `git add -f` would bypass all of this; do not bypass it.

## Notes for agents

- This file auto-loads when you touch any path inside `TestRig/`. If your work touches only a launcher script and never reads inside the folder, read this file explicitly.
- Read the half's own manual too: `TestRig/DedicatedServer/CLAUDE.md` for the server, `TestRig/ClientRig/CLAUDE.md` then `README.md` (plus `RESEARCH.md`) for the client rig.
- Both launchers print their whole command surface when run with no action, and the client rig's includes the hosting sequence in the order that works. That is the fastest way to check a flag without opening a document.
- **Never focus, raise or activate a game window.** The client rig's whole value is that it works while the developer is using the machine, and a driven instance runs on a Win32 desktop that is created but never switched to. `SwitchDesktop` is deliberately not imported anywhere and must never be. The full rule, and the read-only exceptions that earn their place, is in `TestRig/ClientRig/README.md`.
- The source install is read-only from both halves. Nothing under `$(StationeersPath)` is ever written.
- Both halves read `<StationeersPath>` from `Directory.Build.props` at the repo root. The dedicated server also reads `STEAMCMD_PATH`, and the client rig also reads `STATIONEERS_CLIENTRIG_ROOT`, both from the environment (set per `DEV.md`). Neither launcher contains a developer-specific path.
