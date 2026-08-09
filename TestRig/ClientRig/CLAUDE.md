# ClientRig

The client half of the rig: it provisions and drives N isolated Stationeers **game clients** on one machine. `client-rig.ps1` is the launcher; the `ClientDriver` BepInEx plugin is the control plane inside each instance.

This file exists because of one asymmetry. The server half's manual is named `CLAUDE.md` and auto-loads; this half's manual is `README.md` and does not. So this file carries only what an agent must know before touching anything here, and points at the two documents that carry the rest:

- **`README.md`**, next to this file, is the operating manual: setup, the launcher actions, the endpoint catalogue, the end-to-end hosting workflow, and the gotchas.
- **`RESEARCH.md`**, next to this file, is the durable why: the cursor gate, identity, the separate desktop, the hosting internals, and the measurements behind each.
- **`TestRig/CLAUDE.md`**, one level up, carries the rules shared with the dedicated server and auto-loads alongside this file: the one session lock covering both halves, what the rig touches outside its own folder, the save tiers, the `dev-plugins/` layout, and the launcher-flag conventions.

Nothing below is repeated from those files at length. Where they disagree with this one, they are the detail and this is the summary.

## Read these before running anything

- **Every mutating action needs the rig session lock, and one lock covers both halves.** Gated: `-Provision`, `-Start`, `-Stop`, `-Save`, `-Remove`, `-Broadcast`, `-Call`. Free: `-Status`, `-List`, `-Logs`, `-Snapshot`, and `-Wait` (which needs no lock but refreshes one you already hold). Acquire with `-Lock -Purpose "<reason>"`, then pass `-As <id>` on every gated command, on either launcher. Rules: `TestRig/session.lock.template`.
- **A NEW lock wipes each instance's leftovers, so your test starts clean.** Gone: `setting.xml` (it carries `StartLocalHost`), `data/<instance>/userdata/saves/`, the Unity logs, `imgui.ini`, a stale `game.pid`, the instance's `BepInEx/config` (re-copied from the source install, with `SavePathOverride` re-applied straight after, because the copy wipes it), `LogOutput.log*`, `BepInEx/cache/`, and the InspectorPlus `requests/` and `snapshots/`. Kept: `rig.json`, `instance.json`, `provision.stamp`, `userdata/mods/` (staleness is reported; the fix is `-Provision -Force`) and the hard links. It is refused while any instance is live, never fires on re-asserting a lock you already hold, and `-Lock -KeepState` opts out loudly. **It resets between SESSIONS: two unrelated tests under one lock get no reset between them, so release and re-take the lock when the subject changes.** Detail: `TestRig/CLAUDE.md`, "State hygiene".
- **This half can host a world.** `-Provision -Role host` plus `POST /host` turns an instance into a listen host that other instances join over loopback RakNet. A listen host runs the simulation AND plays a character, which the dedicated server cannot do, so any test that needs a host who plays lives here. It also means an instance now writes real worlds, which is why the save rules below are stricter than they were.
- **Ordering runs the opposite way at each end.** The host must be IN ITS WORLD before any joiner connects, because `/connect` has nothing to reach until then. At teardown the host goes LAST: joiners disconnect first, then whoever holds the world saves, then the host quits. `-Stop` performs that ordering itself and refuses to take a host down while something attached to it is not part of the same teardown.
- **Stop the instances before releasing the lock.** A running instance keeps the lock live with no timer to save you, so leaving one up holds the whole rig, dedicated server included. `-Unlock` refuses outright while a listen host is live.
- **A provision hard-links about 1,050 files out of the developer's real install.** That install is read-only, always. Anything the game or a mod writes to is a real copy; never make one a link.
- **The instances root is recorded at provision time, so `-InstancesRoot` is typed once.** Hard links cannot cross volumes, so the trees normally sit on the game install's drive rather than under `instances/` here; `-Provision` writes the resolved root into the registry entry and every later action (including the state reset) reads it back. Typing `-InstancesRoot` again overrides it and moves the tree. An instance provisioned before the root was recorded still works: it falls back to `-InstancesRoot`, then `$env:STATIONEERS_CLIENTRIG_ROOT`, then `instances/`, and prints one line naming `-Provision -Force` as the fix.
- **`-Call` and `-Broadcast` derive their HTTP timeout from the request.** The endpoint's own `timeoutMs` plus a margin, floored at 120 s and at 300 s for `/host`, `/connect`, `/save`, `/load`, `/newworld` and `/waitfor`. `-CallTimeoutSeconds N` overrides it; do not reach for `-TimeoutSeconds` or `-WaitSeconds`, which mean other things on both launchers.
- **Never focus, raise or activate a game window.** Instances run on a Win32 desktop that is created and never switched to. No `SetForegroundWindow`, no `AttachThreadInput`, no `ShowWindow`, no `SetWindowPos`, no `SwitchDesktop`. The read-only foreground queries in `Window/NativeWindow.cs` are the only exception and the only place `System.Runtime.InteropServices` belongs in the plugin.
- **`-Remove` deletes the instance's save root** at `data/<instance>/userdata/` along with its tree. On a host that root IS the world every joiner was playing in.
- **`POST /savepath force=true` reaches the developer's tier-1 save folder.** The refusal that stops it is plugin code, not a rule an agent reads first. Never pass `force=true` unless the user asked for exactly that.
- **`POST /host requireIsolatedSavePath` defaults to true and stays true.** It refuses to create a world when the instance's save root is inside the developer's user-data folder. Passing false writes a driven session's world into the developer's own saves.
- **This folder is gitignored deny-all with a named allowlist**: this file, `README.md`, `RESEARCH.md`, `client-rig.ps1`, and source under `dev-plugins/`. Everything else, `data/` and `instances/` included, is local-only. Do not bypass it with `git add -f`.

## Where the code is

```
client-rig.ps1                    the launcher: provision, desktop, lifecycle, save, host-aware teardown, fan-out
dev-plugins/ClientDriver/         the control plane inside each instance (never ships to the Workshop)
data/<instance>/                  manifest, provision stamp, setting.xml, save root, logs, PID file (gitignored)
data/rig.json                     the registry: one entry per instance, including the instances root its tree was built in
instances/<instance>/             the hard-linked game tree, normally on the install's volume instead (gitignored)
```

`client-rig.ps1` with no action prints the whole command surface, including the hosting sequence. `GET /help` on a running instance prints the endpoint catalogue.
