# TestRig

`TestRig/` holds the two tools that drive Stationeers for testing. They are peers, and a full multiplayer test uses both: the dedicated server hosts the world, the client rig joins it.

- **`TestRig/DedicatedServer/`** runs a headless dedicated server. Operating manual: `TestRig/DedicatedServer/CLAUDE.md`.
- **`TestRig/ClientRig/`** provisions and drives N isolated game clients through the `ClientDriver` BepInEx plugin. Operating manual: `TestRig/ClientRig/README.md`; durable internals: `TestRig/ClientRig/RESEARCH.md`.

**This file auto-loads for work in EITHER half, which is why the shared rules live here rather than in one half's manual.** A rule that only appears under `DedicatedServer/` is invisible to an agent working the client rig, and the reverse. Read the half-specific manual on top of this file, never instead of it.

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
| The client save folder (`<USER_DOCUMENTS>\My Games\Stationeers\saves\`) | Nobody | Tier 1, off-limits unconditionally. See "Saves" below. |

That shared per-Windows-user state is why there is one lock rather than two.

## Session lock: one lock, both halves

**The rig is a shared single-instance resource and every mutating command needs the lock.** One lock at `TestRig/session.lock` covers `DedicatedServer/` and `ClientRig/` together.

**The single source of truth for the rules is `TestRig/session.lock.template`. Read it before driving either half.** The implementation is `TestRig/rig-lock.ps1`, dot-sourced by both launchers so the timer, ownership, break-lock gate and release semantics cannot drift between them. The essentials:

- **Acquire once, from either launcher, before any mutating command.** `-Lock -Purpose "<what you are testing>"` prints a short owner id; pass `-As <id>` on every mutating command afterwards, on **both** launchers.
- **Mutating on the dedicated server:** `-Bootstrap`, `-DeployMods`, `-SyncMods`, `-Start`, `-Save`, `-SendCommand`, `-Stop`. Free: `-Status`, `-Logs`.
- **Mutating on the client rig:** `-Provision`, `-Start`, `-Stop`, `-Remove`, `-Broadcast`, `-Call`. Free: `-Status`, `-List`, `-Logs`, `-Snapshot`, `-Wait` (which refreshes a lock you already hold, since a barrier can outlast the TTL).
- **It expires on a timer (default 10 min) so an idle agent frees the rig.** Refresh (`-RefreshLock -As <id>`, or any mutating command) about once a minute ONLY while actively driving a test. Never refresh to hold the rig for an absent human, and never spawn a background refresher.
- **A busy rig stays live regardless of the timer.** Busy means a player is connected to the running dedicated server, or at least one client-rig instance process is alive.
- **Release when done:** `-Unlock -As <id>`, or `-Stop -As <id> -Release`. A running client instance has no timer to save you: leaving instances up holds the WHOLE rig. Always `client-rig.ps1 -Stop -All -As <id>` before releasing.
- **On resume after any gap, re-check ownership first:** `-Status -As <id>` on either launcher.
- **Breaking another session's live lock is `-BreakLock`, and it is human-gated.** Never use it without the user's explicit say-so.

### `-Force` and `-BreakLock` are different things, on both launchers

This pair used to be one flag with opposite risk on the two halves, which is exactly how a live test gets torn down by muscle memory. The rule now, on both launchers:

- **`-Force`** overrides a refusal inside your own session, and is routine. On the client rig, `-Provision -Force` rebuilds an instance you already own. It never touches the lock.
- **`-BreakLock`** takes a live lock off another session, and is human-gated.

`-Force` no longer breaks a lock anywhere. If a doc, script or habit still says `-Force` for lock-breaking, it is stale.

### `-TimeoutSeconds` and `-WaitSeconds` mean the same thing on both launchers

- **`-TimeoutSeconds`** (default 30) is process-teardown grace for `-Stop`: how long the thing gets to exit cleanly before it is killed.
- **`-WaitSeconds`** is how long a blocking wait waits. On the dedicated server that is `-Save` waiting for its log confirmation (default 30); on the client rig it is the `-Wait` readiness barrier (default 300).

The client rig used to overload `-TimeoutSeconds` for its barrier. It does not any more; `-Wait -TimeoutSeconds 600` in an old note means `-Wait -WaitSeconds 600`.

## Saves

The repository-wide save-tier rule is in the root `CLAUDE.md` under "Workflow: save file access tiers". Both rig save roots are **tier 3, agent-managed**:

- `TestRig/DedicatedServer/data/saves/` (the server's world tree)
- `TestRig/ClientRig/data/<instance>/userdata/` (each client instance's own save root, pointed at by StationeersLaunchPad's `SavePathOverride`)

Copy in, overwrite, rename, delete, hand-edit. `client-rig.ps1 -Remove` deletes an instance's save root along with its tree, by design. Both are still covered by the lock: they belong to whoever holds it.

**The developer's client save folder stays tier 1, off-limits unconditionally**, and no rig operation may reach into it. Two specific hazards on the client half:

- `POST /savepath` retargets `Settings.CurrentData.SavePath` on a **running** client. It refuses a path inside the game's own default user-data folder, but only when the caller does not pass `force=true`. That refusal is plugin code, not a rule, so an agent that passes `force=true` walks straight past it. Do not pass `force=true` on `/savepath` unless the user has asked for exactly that.
- A provisioned instance gets its own save root at provision time. An instance provisioned with `-SeedMods:$false` on a machine where `stationeers.launchpad.cfg` did not yet exist gets a warning instead of a `SavePathOverride`, and would share the developer's user-data folder. The launcher warns; treat that warning as a stop, not a note.

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
| `TestRig/ClientRig/` | `README.md`, `RESEARCH.md`, `client-rig.ps1`, `dev-plugins/` source | ignored, including `instances/`, `data/` |
| `TestRig/` itself | `CLAUDE.md`, `rig-lock.ps1`, `session.lock.template` | the active `session.lock` is ignored |

`dev-plugins/**/bin/` and `dev-plugins/**/obj/` stay ignored under both. `git add -f` would bypass all of this; do not bypass it.

## Notes for agents

- This file auto-loads when you touch any path inside `TestRig/`. If your work touches only a launcher script and never reads inside the folder, read this file explicitly.
- Read the half's own manual too: `TestRig/DedicatedServer/CLAUDE.md` for the server, `TestRig/ClientRig/README.md` (plus `RESEARCH.md`) for the client rig.
- **Never focus, raise or activate a game window.** The client rig's whole value is that it works while the developer is using the machine, and a driven instance runs on a Win32 desktop that is created but never switched to. `SwitchDesktop` is deliberately not imported anywhere and must never be. The full rule, and the read-only exceptions that earn their place, is in `TestRig/ClientRig/README.md`.
- The source install is read-only from both halves. Nothing under `$(StationeersPath)` is ever written.
- Both halves read `<StationeersPath>` from `Directory.Build.props` at the repo root. The dedicated server also reads `STEAMCMD_PATH`, and the client rig also reads `STATIONEERS_CLIENTRIG_ROOT`, both from the environment (set per `DEV.md`). Neither launcher contains a developer-specific path.
