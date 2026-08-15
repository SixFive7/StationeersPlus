# TestRig

`TestRig/` drives Stationeers for testing. **One binary, `TestRig/testrig.exe`. One session lock. Two halves plus a playtest harness, all of it one system.**

```
testrig <verb> [--target all|server|clients|<instance>[,<instance>]] [options]
```

The two halves are a headless dedicated server (`DedicatedServer/`) and N driven game clients (`ClientRig/`, each running the rig's in-process control plane). The `playtest` verb runs a mod's in-game checks against the client half with nobody at the keyboard.

**Run `TestRig/testrig.exe` with no verb to print the whole surface.** It is generated from the same verb and option tables the parser uses, so it cannot drift, and it is the fastest correct reference for a verb, a flag, an option's grammar or an exit code. Where a verb cannot mean the same thing on both halves the binary refuses, says why, and names a command that works. Read the refusal; it is the documentation for that case.

- Operating reference (every verb per target, the working sequences, the endpoint catalogue): `TestRig/MANUAL.md`.
- Durable internals and the reasons behind the design: `TestRig/RESEARCH.md`.
- The source tree and how to rebuild: `TestRig/src/CLAUDE.md`. The in-game plugin: `TestRig/dev-plugins/TestRig/CLAUDE.md`.
- The playtest harness: `TestRig/playtest/CLAUDE.md`.

**There is no PowerShell rig any more.** The launcher, its two per-half libraries, the lock and reset scripts, the playtest runner and the eight PowerShell checks were deleted once the binary had driven a real multiplayer playtest end to end. They are in git history if a behaviour ever needs looking up; nothing on disk runs them.

This file auto-loads for any path under `TestRig/`. It carries what prevents damage or a wasted session, and nothing else.

## The binary is committed, and it refuses to run when stale

`testrig.exe` is build output and it IS in git, so driving the rig never needs a build step. It embeds a SHA-256 digest of every source file under `TestRig/src/` and under every `Mods/<Mod>/playtests/` (the checks are compiled in too), recomputes that digest at startup, and on a mismatch prints both and **exits 7 having done nothing**. A refusal rather than a warning, because a stale on-disk artifact has already cost this project two whole sessions and both times the evidence scrolled past.

```
dotnet publish TestRig/src/TestRig.Cli/TestRig.Cli.csproj -c Release -r win-x64
```

That is the whole rebuild. It publishes AOT and installs the result at `TestRig/testrig.exe`. **Commit the binary in the same commit as the source change that produced it**, or the next agent inherits one that will not run. Expect exit 7 while you are actively editing; that is the guard working, not something to route around. The suite is `dotnet test TestRig/src/TestRig.slnx`, and it is offline: no game, no network, and it never touches the real `session.lock`.

## The session lock covers the whole rig

Every agent on this machine contends for one rig. The two halves share the developer's single game install and the per-Windows-user Unity state nothing separates (`PlayerCookie-v2.xml`, the `HKCU\Software\Rocketwerkz\rocketstation` PlayerPrefs key), which they also share with the developer's own client. That is why there is one lock at `TestRig/session.lock` and not two.

```
testrig lock --purpose "<what you are testing>"    # prints TESTRIG-OWNER <id> as its last line
testrig <verb> --as <id> ...                       # every mutating verb
testrig unlock --as <id>                           # releases AND restores the rig
```

The surface lists which verbs are gated and which are free, and a mutating verb without `--as` refuses by name, so neither needs repeating here. What the binary cannot enforce:

- **Hitting another session's live lock fails at once.** `lock --wait-seconds N` queues for up to N seconds instead. It is a queue, not a reservation, and promises no ordering fairness.
- **Two timers.** `--ttl-minutes` (10) is a liveness heartbeat: refresh about once a minute while driving a test, with `refresh-lock --as <id>` or any mutating verb. `--idle-ceiling-minutes` (60) is an absolute idle ceiling on the OWNER's own actions, and past it the lock is reclaimable **even on a busy rig**, killing what is running. A session that keeps working is never reclaimed. If you will legitimately be idle longer (waiting on a human), pass a longer `--idle-ceiling-minutes` at `lock` and tell the user.
- **Never poll-refresh to hold the rig for an absent human, and never spawn a background refresher.** Either one starves every other agent.
- **Busy** means a player is connected to the running dedicated server, or any client instance process is alive. A busy rig keeps its heartbeat alive by itself, below the ceiling only. An **untracked** game process (claimed by no pid file) is reported but is not busy: no rig action can stop it, so kill it by pid.
- **Stop instances before releasing.** A running instance holds the whole rig with no timer to save you. `unlock` refuses outright while a listen host is live; `--force` overrides that one refusal.
- **`--force` is not `--break-lock`.** `--force` overrides a refusal inside your own session and never touches a lock. `--break-lock` takes a live lock off another session and is **human-gated: only on the user's explicit say-so**.
- **After any idle gap, re-check ownership first:** `testrig status --as <id>`. If another session holds it, stop, tell the user what took it, and re-acquire only on their go-ahead.

Full rules, the lock file's fields, the queueing and hand-off cases: `TestRig/MANUAL.md`, "The session lock".

## The rig is restored at BOTH ends of a session

**Releasing the lock restores the rig, and acquiring one restores it again as a backstop.** Both, not either. The release is where the guarantee is earned: the session that made the mess pays for it while it still owns the rig and the rig is provably idle. The acquisition covers the one case a release cannot, a session that crashed, was killed, or lost the machine to a reboot. `TestRig/session.dirty` is written durably before a session's first mutating action, carries the OS boot identity as well as the pid, and is cleared only by a completed restore, so acquisition can tell.

Consequences an agent gets wrong:

- **Your own leftovers do not survive your release.** That includes any world your session created, on either half. `unlock --keep-state` is the way to hand a staged rig to a follow-up session; it leaves the dirty marker set so the next acquisition cleans up instead.
- **A world's lifetime is session-scoped, on BOTH halves.** A world under `DedicatedServer/data/saves/` or under a client instance's own `userdata/saves/` is kept if and only if it was in the world set `session.dirty` recorded before your first mutating command. **So stage a save you want to keep BEFORE that first command**, not after: staging is a plain file copy and writes no marker. Every degraded case keeps every world and says which case it was, and a failed enumeration is recorded as a failure rather than as an empty set, so it can never read as "this session created all of them".
- **A restore that would delete more than five worlds at once refuses**, names every world at risk, and changes nothing. `reset --allow-bulk-world-delete` is the override, and wanting it is nearly always a wrong answer upstream.
- **Three lifetimes, all explicit.** Until you release (the default, and it spans your own start/stop cycles); into the next session with `unlock --keep-state`; or permanently with `capture-baseline --as <id>`, which declares the rig as it stands to be the definition of clean. A baseline never protects a world; staging before the lock is what does.
- **It resets between SESSIONS only.** Two unrelated tests under one lock get no reset between them, so release and re-take the lock when the subject changes.
- **The restore is refused while the rig is in use**, and the three conditions are ORed: any live client instance, a live dedicated-server process, or an untracked game process. The lock is still granted, loudly, with what is running named. Re-asserting a lock you already hold never resets. `--keep-state` opts out at either end and prints what it skipped.
- **`testrig reset --as <id>` runs the restore without ending the session** (`--dry-run` to see the plan first). Restoring and releasing are different things.

`testrig status` prints `rig state: clean` or `DIRTY`, the idle countdown, how many worlds are protected, and each half's game version and mod staleness. Detail: `MANUAL.md`, "State hygiene".

## Two ways to host a world

"Host a world" does not mean the dedicated server any more. A client instance created with `--role host` and driven with `POST /host` is a **listen host**: one process that runs the simulation, accepts joiners over loopback RakNet, and plays a character.

Use the **dedicated server** when the test wants a server that is not a player (soak runs, in-process scenario probes, save-edit round trips); it has no player character at all. Use a **listen host** when the test needs a host who plays: a host holding an item, a host's own client-half setting, anything shaped "the host does X and the joiner sees Y".

**Both halves have an HTTP control plane**, because one plugin loads into both: instances on `127.0.0.1:27700 + index`, the dedicated server on `127.0.0.1:27750`. So `call --target server` works, and `wait --target server --stage inWorld` gets its answer from `/status.phase` rather than inferring one from an InspectorPlus request file being consumed, which was measured happening with no world loaded at all. `--new <Map>` is validated against the install's own world catalogue before anything launches, and a world name the game rejected ends a wait at once carrying the game's list of what it would have accepted; the server prints that once and then runs forever with no world.

**An instance records the mods it exists to TEST**, and that set is what keeps a mod from
being loaded twice. `create --target hostie --under-test SprayPaintPlus` means the seed does
not copy the developer's SprayPaintPlus and `deploy` provides the only copy; every OTHER mod
is still seeded at its published state, on purpose, because this repository carries work in
progress for those too. `deploy` refuses a mod the instance does not record, `create --force`
keeps the set, and the playtest harness refuses before bring-up when a check's mod is not in
it. Detail: `MANUAL.md`, "Mods under test, and every other mod".

Either way it is one rig and one lock. Ordering runs opposite at each end: the host must be IN ITS WORLD before any joiner connects, and at teardown joiners go first, the world holder saves, the host quits last. `stop` performs that ordering itself and refuses to end a host under an attached joiner. Assert on `/status.role` (`menu|singlePlayer|joinedClient|listenHost|dedicated`) and `/status.hosting`, never on `isClient`/`isServer`: a listen host is `NetworkRole.Server` and reports `isClient=false`.

Game internals: `Research/GameSystems/ListenHost.md`.

## Saves, and the folder that is never touched

Both rig save roots are **tier 3** under the repository save-tier rule (root `CLAUDE.md`): copy in, overwrite, rename, delete, hand-edit. They still belong to whoever holds the lock, and both are session-scoped as above.

- `TestRig/DedicatedServer/data/saves/` (the server's worlds).
- `TestRig/ClientRig/data/<instance>/userdata/` (each instance's own save root, through StationeersLaunchPad's `SavePathOverride`). A listen host writes real worlds under `saves/` there. `testrig remove` deletes that root along with the tree, which for a host is the world every joiner was in.

**The developer's own client save folder is tier 1 and off-limits unconditionally.** Three specific hazards can reach it, all on the client half:

- `POST /savepath` retargets a **running** client's save root. It refuses a path inside the developer's real user-data folder only while the caller omits `force=true`. That refusal is plugin code, not a rule an agent reads first, and `call --body` passes the body through unread. **Never pass `force=true`** unless the user asked for exactly that.
- `POST /host` refuses to create a world when the instance's save root is not isolated. The override is `requireIsolatedSavePath=false` and there is no correct reason to pass it.
- `SavePathOverride` is written on every `create`, ahead of the mod seed. A failure to write it **throws** for `--role host` and **warns** for `--role client`. Treat the warning as a stop: start the instance once to generate `stationeers.launchpad.cfg`, then `create --force`.

The merged in-game plugin removes both overrides outright, so passing either becomes a 400. It is built but **not deployed yet**, so until it is, the two hazards above are live.

## Never take the developer's foreground

**No code here may focus, raise, or activate a game window.** Eight Win32 APIs are forbidden outright, `SwitchDesktop` and `SetForegroundWindow` among them; both trees fail the build or the suite on any of them, and each guard is named in its own `CLAUDE.md`. Instances run on a Win32 desktop that is created and never switched to, and that is the mechanism the whole tool rests on: measured, 40 focus steals out of 40 samples without it, 0 out of 55 with it. Read-only foreground queries in the plugin's `Window/NativeWindow.cs` are the only exception.

## What the rig touches outside its own folder

- The developer's Stationeers install (`$(StationeersPath)`): **read-only, always.** A `create` hard-links about a thousand files out of it per instance (1,053 on 0.2.6428.27798; the count moves with every game update); `update-game --target server` mirrors its `BepInEx/` tree. A hard link shares the file data, so anything the game writes to is a real copy, never a link.
- `PlayerCookie-v2.xml`, the PlayerPrefs key, `Player.log` / `Player-prev.log` and `Blueprints\`: shared with the developer's own client and not separable. Reported at the session boundary, never restored.
- The developer's `modconfig.xml` and `mods/`: read-only sources for `update-mods`. `-settings SavePath` is never passed on a client, because StationeersLaunchPad then rewrites that shared file with every `<Local>` entry deleted.
- UDP game ports: 28016/28015 for the dedicated server, 27800 plus the instance index for a listen host. **Two RakNet sockets on one port do not conflict**: both bindings coexist and route by destination address, so a collision is a test that is confidently wrong with nothing logged anywhere. `create` refuses the known collisions; nothing outside the rig is checked.


## Committing

`TestRig/` is **gitignored deny-all with a named allowlist**, because routine actions drop artifacts straight into the folder (`snapshot --out-file before.json`, `/screenshot?path=`). Tracked: `testrig.exe` and its sources under `src/`, the four docs, `dev-plugins/` source under either half, and the retained PowerShell tree until it goes. Everything else stays local, `install/`, `data/`, `instances/`, `session.lock`, `session.dirty`, `session.state.json` and `baseline/` included. `git add -f` would bypass all of it; do not.
