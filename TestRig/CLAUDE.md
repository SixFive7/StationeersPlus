# TestRig

`TestRig/` drives Stationeers for testing. **One launcher, `TestRig/testrig.ps1`. One session lock. Two halves plus a playtest harness, all of it one system.**

```
testrig.ps1 <verb> [-Target all|server|clients|<instance>[,<instance>]] [options]
```

The two halves are a headless dedicated server (`DedicatedServer/`) and N driven game clients (`ClientRig/`, each running the `ClientDriver` plugin). `playtest/` runs a mod's in-game checks against the client half with nobody at the keyboard. There are no per-half launchers and no per-half manuals; `dedicated-server.ps1` and `client-rig.ps1` no longer exist, and any note naming them is stale.

**Run `pwsh -NoProfile -File TestRig/testrig.ps1` with no verb to print the whole surface.** It is the fastest correct reference for a verb or a flag. Where a verb cannot mean the same thing on both halves the launcher refuses, says why, and names a command that works. Read the refusal; it is the documentation for that case.

- Operating reference (every verb per target, the working sequences, the endpoint catalogue): `TestRig/MANUAL.md`.
- Durable internals and the reasons behind the design: `TestRig/RESEARCH.md`.
- The playtest harness: `TestRig/playtest/CLAUDE.md`.

This file auto-loads for any path under `TestRig/`. It carries what prevents damage or a wasted session, and nothing else.

## The session lock covers the whole rig

Every agent on this machine contends for one rig. The two halves share the developer's single game install and the per-Windows-user Unity state nothing separates (`PlayerCookie-v2.xml`, the `HKCU\Software\Rocketwerkz\rocketstation` PlayerPrefs key), which they also share with the developer's own client. That is why there is one lock at `TestRig/session.lock` and not two.

```powershell
testrig lock -Purpose "<what you are testing>"     # prints TESTRIG-OWNER <id> as its last line
testrig <verb> -As <id> ...                        # every mutating command
testrig unlock -As <id>                            # releases AND restores the rig
```

- **Gated:** `create`, `remove`, `start`, `save`, `call`, `send`, `deploy`, `update-game`, `update-mods`, `capture-baseline`, `reset`. **Free:** `status`, `list`, `logs`, `snapshot`. `wait` needs no lock but refreshes one you hold, because a barrier can outlast the TTL. `stop` needs no lock either, so an orphan or a dead session can always be cleaned up, but it refuses while another session's lock is live.
- **Hitting another session's live lock fails at once.** `lock -WaitSeconds N` queues for up to N seconds instead. It is a queue, not a reservation, and promises no ordering fairness.
- **Two timers.** `-TtlMinutes` (10) is a liveness heartbeat: refresh about once a minute while actively driving a test, with `refresh-lock -As <id>` or any mutating command. `-IdleCeilingMinutes` (60) is an absolute idle ceiling on the OWNER's own actions, and past it the lock is reclaimable **even on a busy rig**, killing what is running. A session that keeps working is never reclaimed. If you will legitimately be idle longer (waiting on a human), pass a longer `-IdleCeilingMinutes` at `lock` and tell the user.
- **Never poll-refresh to hold the rig for an absent human, and never spawn a background refresher.** Either one starves every other agent.
- **Busy** means a player is connected to the running dedicated server, or any client instance process is alive. A busy rig keeps its heartbeat alive by itself, below the ceiling only. An **untracked** game process (claimed by no pid file) is reported but is not busy: no launcher action can stop it, so kill it by pid.
- **Stop instances before releasing.** A running instance holds the whole rig with no timer to save you. `unlock` refuses outright while a listen host is live; `unlock -Force` overrides that one refusal, from this launcher, which is the only launcher.
- **`-Force` is not `-BreakLock`.** `-Force` overrides a refusal inside your own session and never touches a lock. `-BreakLock` takes a live lock off another session and is **human-gated: only on the user's explicit say-so**.
- **After any idle gap, re-check ownership first:** `testrig status -As <id>`. If another session holds it, stop, tell the user what took it, and re-acquire only on their go-ahead.

Full rules, the lock file's fields and the queueing and hand-off cases: `TestRig/MANUAL.md`, "The session lock".

## The rig is restored at BOTH ends of a session

**Releasing the lock restores the rig, and acquiring one restores it again as a backstop.** Both, not either. The release is where the guarantee is earned: the session that made the mess pays for it while it still owns the rig and the rig is provably idle. The acquisition covers the one case a release cannot, a session that crashed, was killed, or lost the machine to a reboot. `TestRig/session.dirty` is written durably before a session's first mutating action, carries the OS boot identity as well as the pid, and is cleared only by a completed restore, so acquisition can tell.

Consequences an agent gets wrong:

- **Your own leftovers do not survive your release.** That includes any world your session created. `unlock -KeepState` is the way to hand a staged rig to a follow-up session; it leaves the dirty marker set so the next acquisition cleans up instead.
- **A world's lifetime is session-scoped.** A dedicated-server world under `DedicatedServer/data/saves/` is kept if and only if it was in the world set `session.dirty` recorded before your first mutating command. **So stage a save you want to keep BEFORE that first command**, not after: staging is a plain file copy and writes no marker. Every degraded case keeps every world and says which case it was.
- **Three lifetimes, all explicit.** Until you release (the default, and it spans your own start/stop cycles); into the next session with `unlock -KeepState`; or permanently with `capture-baseline -As <id>`, which declares the rig as it stands to be the definition of clean. A baseline never protects a world; staging before the lock is what does.
- **It resets between SESSIONS only.** Two unrelated tests under one lock get no reset between them, so release and re-take the lock when the subject changes.
- **The restore is refused while the rig is in use**, and the three conditions are ORed: any live client instance, a live dedicated-server process, or an untracked game process. The lock is still granted, loudly, with what is running named. Re-asserting a lock you already hold never resets. `-KeepState` opts out at either end and prints what it skipped.
- **`testrig reset -As <id>` runs the restore without ending the session** (`-DryRun` to see the plan first). Restoring and releasing are different things.

`testrig status` prints `rig state: clean` or `DIRTY`, the idle countdown, how many worlds are protected, and each half's game version and mod staleness. Detail: `MANUAL.md`, "State hygiene".

## Two ways to host a world

"Host a world" does not mean the dedicated server any more. A client instance created with `-Role host` and driven with `POST /host` is a **listen host**: one process that runs the simulation, accepts joiners over loopback RakNet, and plays a character.

- **Dedicated server** when the test wants a server that is not a player: soak runs, `ScenarioRunner` probes, save-edit round trips. It has no player character at all.
- **Listen host** when the test needs a host who plays: a host holding an item, a host's own client-half setting, anything shaped "the host does X and the joiner sees Y".

Either way it is one rig and one lock. Ordering runs opposite at each end: the host must be IN ITS WORLD before any joiner connects, and at teardown joiners go first, the world holder saves, the host quits last. `stop` performs that ordering itself and refuses to end a host under an attached joiner. Assert on `/status.role` (`menu|singlePlayer|joinedClient|listenHost|dedicated`) and `/status.hosting`, never on `isClient`/`isServer`: a listen host is `NetworkRole.Server` and reports `isClient=false`.

Game internals: `Research/GameSystems/ListenHost.md`.

## Saves, and the folder that is never touched

Both rig save roots are **tier 3** under the repository save-tier rule (root `CLAUDE.md`): copy in, overwrite, rename, delete, hand-edit. They still belong to whoever holds the lock.

- `TestRig/DedicatedServer/data/saves/` (the server's worlds; session-scoped, see above).
- `TestRig/ClientRig/data/<instance>/userdata/` (each instance's own save root, through StationeersLaunchPad's `SavePathOverride`). A listen host writes real worlds under `saves/` there. `testrig remove` deletes that root along with the tree, which for a host is the world every joiner was in.

**The developer's own client save folder is tier 1 and off-limits unconditionally.** Three specific hazards can reach it, all on the client half:

- `POST /savepath` retargets a **running** client's save root. It refuses a path inside the developer's real user-data folder only while the caller omits `force=true`. That refusal is plugin code, not a rule an agent reads first. **Never pass `force=true`** unless the user asked for exactly that.
- `POST /host` refuses to create a world when the instance's save root is not isolated. The override is `requireIsolatedSavePath=false` and there is no correct reason to pass it.
- `SavePathOverride` is written on every `create`, ahead of the mod seed. A failure to write it **throws** for `-Role host` and **warns** for `-Role client`. Treat the warning as a stop: start the instance once to generate `stationeers.launchpad.cfg`, then `create -Force`.

## Never take the developer's foreground

**No code here may focus, raise, or activate a game window.** No `SetForegroundWindow`, no `AttachThreadInput`, no `ShowWindow`, no `SetWindowPos`, no `SwitchDesktop`. Instances run on a Win32 desktop that is created and never switched to; that is the mechanism the whole tool rests on (measured: 40 focus steals out of 40 samples without it, 0 out of 55 with it). Read-only foreground queries in `ClientDriver`'s `Window/NativeWindow.cs` are the only exception and the only place `System.Runtime.InteropServices` belongs in the plugin.

## What the rig touches outside its own folder

- The developer's Stationeers install (`$(StationeersPath)`): **read-only, always.** A `create` hard-links about 1,050 files out of it per instance; `update-game -Target server` mirrors its `BepInEx/` tree. A hard link shares the file data, so anything the game writes to is a real copy, never a link.
- `PlayerCookie-v2.xml`, the PlayerPrefs key, `Player.log` / `Player-prev.log` and `Blueprints\`: shared with the developer's own client and not separable. Reported at the session boundary, never restored.
- The developer's `modconfig.xml` and `mods/`: read-only sources for `update-mods`. `-settings SavePath` is never passed on a client, because StationeersLaunchPad then rewrites that shared file with every `<Local>` entry deleted.
- UDP game ports: 28016/28015 for the dedicated server, 27800 plus the instance index for a listen host. **Two RakNet sockets on one port do not conflict**: both bindings coexist and route by destination address, so a collision is a test that is confidently wrong with nothing logged anywhere. `create` refuses the known collisions; nothing outside the rig is checked.

## Committing, and the tests

`TestRig/` is **gitignored deny-all with a named allowlist**, because routine actions drop artifacts straight into the folder (`snapshot -OutFile before.json`, `/screenshot?path=`). Tracked: `testrig.ps1`, `testrig.tests.ps1`, `lib/`, `rig-lock.ps1`, `rig-reset.ps1`, their test suites, `CLAUDE.md`, `MANUAL.md`, `RESEARCH.md`, `playtest/` (its source, checks and `CLAUDE.md`) and `dev-plugins/` source under either half. Everything else, `install/`, `data/`, `instances/`, `session.lock`, `session.dirty`, `session.state.json` and `baseline/` included, stays local. `git add -f` would bypass all of it; do not.

Run these after any change to the launcher, the lock, the reset or the harness:

```powershell
pwsh -NoProfile -File TestRig/testrig.tests.ps1
pwsh -NoProfile -File TestRig/rig-lock.tests.ps1
pwsh -NoProfile -File TestRig/rig-reset.tests.ps1
pwsh -NoProfile -File TestRig/playtest/playtest-lib.tests.ps1
```

All four are offline: no game, no instances, no network, and they never touch the real `session.lock`.
