# TestRig manual

The operating reference for `TestRig/testrig.exe`. Read `TestRig/CLAUDE.md` first: it carries the lock, the save tiers and the safety rules, and it auto-loads. This file is the detail, and it is not auto-loaded; open it when you need a verb, a sequence, an endpoint or an option.

Durable internals and the reasoning behind the design are in `TestRig/RESEARCH.md`. The source tree has `TestRig/src/CLAUDE.md`, the in-game plugin has `TestRig/dev-plugins/TestRig/CLAUDE.md` and `README.md`, and the playtest harness has `TestRig/playtest/CLAUDE.md`.

## The binary

```
testrig <verb> [--target all|server|clients|<instance>[,<instance>]] [options]
```

`TestRig/testrig.exe` is one AOT-compiled binary built from `TestRig/src/`. Run it with no verb to print the whole surface: every verb, every option with its default, the lock rules, the exit codes and the resolved instances root. That surface is generated from the same tables the parser and the dispatcher read, so it cannot drift from what the binary accepts, and it is the fastest correct answer to "what is this flag called".

**It refuses to run when it is stale.** The binary embeds a SHA-256 digest of `TestRig/src/` and recomputes it at startup; a mismatch prints both digests and exits **7** having done nothing. Rebuild with `dotnet publish TestRig/src/TestRig.Cli/TestRig.Cli.csproj -c Release -r win-x64`, which installs the new binary at `TestRig/testrig.exe` on its own. The suite is `dotnet test TestRig/src/TestRig.slnx`.

**Option grammar.** Options are `--double-dash`, matched case-insensitively, dashes optional, and a unique prefix is accepted: `--targ`, `-Target` and `--target` are one option. Two boolean options are on by default and are turned off with an explicit negative form (`--no-force-gameplay-input`, `--no-seed-mods`). **An option a verb does not read is a usage error, not a silent no-op**, so `start --width 1024` fails and names `create`.

**The PowerShell rig is gone.** It was retained through the port as the parity reference, checked line by line against 1,560 enumerated behaviours, and deleted once the binary had driven a real multiplayer playtest end to end. Two defects it carried are worth remembering because the binary is built not to repeat them: its `lock` never printed the `TESTRIG-OWNER` line the harness required by regex, and a failed world enumeration was indistinguishable from a rig with no worlds. Git history has the files.

### Exit codes

| Code | Means |
|---|---|
| 0 | did what you asked |
| 1 | tried and failed, including "your machine is not set up" |
| 2 | the command itself was wrong: unknown verb, missing value, bad flag, a flag this verb does not read |
| 3 | refused, with a working alternative named |
| 4 | the lock is held by another session |
| 5 | no lock is held by you |
| 6 | the rig is busy, so the requested state change is unsafe |
| 7 | this binary does not match `TestRig/src/`; rebuild it |
| 8 | a playtest run in which nothing failed but something could not be measured |

4, 5 and 6 exist because the PowerShell rig exited 1 for contention, a lapsed reservation, an unlock by a non-owner and a genuinely broken rig alike, so the harness collapsed every non-zero exit into "inconclusive / rig-unavailable" and a refusal no retry could fix looked exactly like a rig that was momentarily busy. 8 is separate from 1 for the same reason one level up: a fail accuses the mod, an inconclusive says the rig never got far enough to have an opinion.

**A release that did not happen does not exit 0.** `unlock` against a rig with no lock at all exits **5**, and one whose lock has since been taken by somebody else exits **4**; both used to exit 0, which is the code a caller reads as "released", so an agent that mistyped its owner id or whose lock had been reclaimed under it was told its session ended cleanly. `stop --release` maps the same way. **`stop` without `--release` still exits 0 with no lock**, which is what keeps orphan cleanup always possible.

### Targets

`all` is both halves, `server` is the dedicated server, `clients` is every provisioned instance, and one or more instance names (comma separated, case-insensitive) is exactly those. An unknown name is an error naming what is provisioned, never a silent empty set.

**Twelve verbs default `--target` to `all`** so the natural spelling of an update hits both halves: `lock`, `unlock`, `refresh-lock`, `capture-baseline`, `reset`, `status`, `list`, `logs`, `update-game`, `update-mods`, `deploy`, `playtest`.

**Nine act on a specific running thing and will not guess**, so they require an explicit target: `snapshot`, `create`, `remove`, `start`, `stop`, `save`, `wait`, `call`, `send`.

Six of the twelve are rig-wide by construction and **refuse a narrowing target while accepting `--target all`**: the five lock-family verbs (`lock`, `unlock`, `refresh-lock`, `capture-baseline`, `reset`) and `playtest`. `--target all` on any of them is fine and is what you get by typing nothing; `--target server` or an instance name is refusal 14 through 19. The other six (`status`, `list`, `logs`, `update-game`, `update-mods`, `deploy`) genuinely narrow.

## Verbs

Twenty-two verbs you can type, plus `host-mode`, which is internal: the detached wrapper the server's `start` spawns for itself. It bypasses target resolution, the refusal matrix and the lock, because the `start` that spawned it already holds one.

| Verb | Lock | On `server` | On an instance / `clients` |
|---|---|---|---|
| `help` | free | prints the whole surface | same |
| `lock --purpose <s>` | takes | rig-wide, refuses a narrowing target | same |
| `refresh-lock --as <id>` | refreshes | rig-wide | same |
| `unlock --as <id> [--force] [--keep-state]` | releases | rig-wide; **restores the rig first** | same |
| `capture-baseline --as <id> [--force]` | needs | rig-wide | same |
| `reset --as <id> [--dry-run] [--keep-state] [--allow-bulk-world-delete]` | needs | rig-wide restore without ending the session | same |
| `status [--as <id>]` | free | wrapper and process pids, uptime, last log line, pending command, connected players, world count | per instance: process, classified role, ports, identity, tree and where its path came from, phase, live role, hosting, host port, connected clients, foreground verdict, input gate, identity conflicts |
| `list` | free | installed or not, install dir | the registry as a table, plus live role, hosting and client count |
| `logs [--tail N] [--grep <re>] [--unity]` | free | `data/server.log` | each instance's `BepInEx/LogOutput.log`, or with `--unity` the newest `data/<n>/logs/unity-<stamp>.log` |
| `snapshot [--out-file <f>]` | free | refused (it has no registry row to key one on; use `call --path /status`) | `/status` from every named instance in one document |
| `update-game` | needs | SteamCMD app 600760, then mirror the client's `BepInEx/` tree and overlay the StationeersLaunchPad server zip | re-link each instance tree from the developer's install (a `create --force` per instance) |
| `update-mods [--from-modconfig <p>]` | needs | mirror the developer's enabled mod set into `data/mods/`, bake `install/modconfig.xml` | re-seed each instance's `userdata/mods/` and its own `modconfig.xml` |
| `deploy <ModName> [--configuration]` | needs | released mods to `install/BepInEx/plugins/<X>/`, dev-plugins to `data/mods/Local_<X>/` | to `userdata/mods/Local_<X>/` with an `About/` mirror and a `<Local>` entry. Refuses a mod the instance is not provisioned to test; with no `--mod` it deploys that instance's own under-test set |
| `create --target <name>` | needs | refused | build or rebuild ONE instance tree. `--under-test <Mod>[,<Mod>]` records what it exists to test |
| `remove --target <name>` | needs | refused | delete the tree and the instance's save root |
| `start` | needs | must enter a world in the same call: `--load <SaveName> --map <Map>` or `--new <Map>`, and `--new` is validated against the install's world catalogue before anything launches | boots to the MENU and no further; entering a world is a separate `call` |
| `wait --stage <s>` | free (refreshes) | `ping`, `modsLoaded`, `inWorld` or `process`; `menu` refused | `ping`, `modsLoaded`, `menu`, `inWorld` |
| `save [--save-name <n>]` | needs | `--save-name` required, confirmed from the log | `--save-name` optional, confirmed by the plugin |
| `stop [--save-name] [--release]` | not gated | save first if named, `quit`, then kill after the grace period | host-aware ordered teardown |
| `call --path <p> [--body <json>]` | needs | one HTTP request to the server's own control plane on `127.0.0.1:27750` | one HTTP request to each named instance's control plane |
| `send --command '<text>'` | needs | one line into the server's stdin, fire and forget | refused |
| `playtest [--only <pattern>]` | not gated | rig-wide; the harness takes a lock PER CHECK | same |

`stop` is deliberately not lock-gated, so an orphan or an expired session can always be cleaned up with no ceremony and no `--as`. It refuses while another session's lock is **live**, and `--break-lock` on it is human-gated like everywhere else.

`stop --release` asks for the lock STATE before it releases, and that order is load bearing: the state check self-renews a busy session's expired lock and reports it as foreign, which is what stops an unrelated `stop --release` from freeing it. Do not reorder those two steps.

`playtest` is not lock-gated either, and that is also deliberate: the harness takes and releases the lock **once per check**, which is what buys a state reset per check, since the reset is between sessions by design and two checks under one lock would get none.

`create` has no `--width` or `--height` twin on `start`. A window size is an instance's own, recorded when it was provisioned; a `start` that could override it would make the size depend on which command last mentioned it. Typing either on `start` is a usage error naming `create`.

## Mods under test, and every other mod

An instance records the mods it exists to TEST, and that set decides where each of its mods
comes from:

```
testrig create --target hostie --role host --under-test SprayPaintPlus --as <id>
testrig deploy SprayPaintPlus --target hostie --as <id>
```

- A mod **in** the set is NOT seeded from the developer's folder and gets no modconfig entry
  from the seed. `deploy` provides `Local_<Mod>/`, and that is the only copy there is.
- A mod **outside** it is seeded exactly as before, at the developer's published state. That
  is deliberate and it is the reason the set is explicit rather than "every mod this
  repository builds": a rig is normally testing one mod, this repository carries work in
  progress for the others, and an unrelated half-finished mod changing the behaviour of the
  one under test is the failure the separation prevents.
- `deploy` **refuses** a mod the instance does not record, naming the command that records it.
  With no `--mod` at all it deploys that instance's own set rather than everything under
  `Mods/`.
- `create --force` **keeps** the set, exactly as it keeps the role, the ports and the identity.
- The playtest harness checks it before bring-up: a check whose mod is not in an instance's
  set is `inconclusive (mod-not-under-test-here)` before a single game process starts. The
  mod comes from the check's own source location and the set from the registry, so nothing has
  to be declared twice.
- An instance that records a mod and never deploys it has NO copy of it, which attestation
  reports as `under-test-not-deployed` rather than as the ordinary not-deployed case.

Why any of this exists: `create` used to seed the developer's `<Mod>/` folder and `deploy`
used to write `Local_<Mod>/` beside it. Both carry an `About.xml`, so StationeersLaunchPad
loads BOTH, Awake fires twice and every Harmony patch registers twice. A doubled
side-effecting patch produced delta 10000 instead of 5000 during a battery verification, and
no log line anywhere says so, because two plausible halves of one number look exactly like one
correct number.

## The refusals

Where a verb cannot mean the same thing on both halves, the binary refuses and explains, naming what was attempted, why this target cannot do it, a command that does work, and where the durable explanation lives. All five parts are mandatory in the type, so a refusal without a working alternative cannot be written. **Read the refusal rather than a table about it**: it arrives at the moment of the mistake and it is generated from the same data the suite checks.

A refusal prints plainly and exits **3**, so a caller can tell "this command does not apply" from "the rig is broken" and from "the lock is somebody else's".

There are **20 rows**, and the suite pins that number exactly and resolves every alternative against the verb table and the endpoint catalogue. Six things genuinely differ between the halves, and the "refused" cells in the verb table above are where they show up: entering a world at start, the stdin channel (`send`), save-confirmation evidence, N instances versus one install, creating or removing an instance versus installing a server, and a snapshot's per-instance row shape. On top of those, the five lock verbs and `playtest` refuse a narrowing target (rows 14-19), and an instance-shape flag refuses against `--target server` (row 20), which has one identity and no instances.

It was 22. **Both `call` rows are gone, and the reason they went is worth more than the rows were.** They rested on "the dedicated server has no HTTP control plane", which was a fact about the pre-merge rig; one plugin loads into both halves now and the server answers on `127.0.0.1:27750`. The refusal kept firing while the plane was up and replying, so the verb an agent types was teaching a rig that no longer exists. Two more rows survived with their reasons rewritten (`snapshot` on either target, which refuses because the server has no registry row rather than because it cannot answer), and `wait`'s row narrowed from three stages to one. A refusal whose reason has stopped being true is worse than no refusal, because it corrects a caller's model in the wrong direction; re-read every row whenever the shape of the rig changes.

`testrig send --target clients` prints one. The matrix is data in `TestRig/src/TestRig.Cli/Refusals/RefusalMatrix.cs`.

Two of the PowerShell rig's refusals pointed callers at `/console/run`, an endpoint that has never existed; the real one is `/console/exec`. Both are fixed here, and the suite now resolves every named alternative rather than merely checking that one is present.

## Options

Every option, its default, and which verbs read it. Anything typed against a verb that does not read it is exit 2.

### Global

| Option | Default | Means |
|---|---|---|
| `--json` | off | structured output instead of prose, on every verb. Nothing needs to scrape a sentence. |
| `--verbose` | off | detail lines that are otherwise suppressed. |

### The session lock

| Option | Default | Means |
|---|---|---|
| `--purpose <s>` | none | why you are taking the rig. Required by `lock`. Written for the human who is told it when another session holds the rig. |
| `--as <id>` | none | the owner id printed by `lock`. Required by every mutating verb. |
| `--break-lock` | off | take a LIVE lock off another session. Human-gated. Read by `lock`, `unlock` and `stop`. |
| `--force` | off | override a refusal inside your own session (`create --force` rebuilds an instance you own; `unlock --force` releases with a host still up). Never touches another session's lock. Read by `unlock`, `capture-baseline`, `create`, `remove`, `stop`. |
| `--ttl-minutes <n>` | 10 | liveness heartbeat. `lock` and `refresh-lock`. |
| `--idle-ceiling-minutes <n>` | 60 | absolute idle ceiling on the owner's own actions, busy rig or not. `lock` and `refresh-lock`. |
| `--keep-state` | off | skip the state restore, loudly. `lock`, `unlock`, `reset`, `stop`. |
| `--release` | off | `stop` only: release the lock once both halves are down. |
| `--dry-run` | off | `reset` only: print the plan and change nothing. |
| `--allow-bulk-world-delete` | off | `reset` only: delete more worlds in one restore than the ceiling of **five** allows. The refusal names every world at risk and changes nothing; wanting this flag is nearly always a wrong answer upstream. |

### Worlds

| Option | Default | Means |
|---|---|---|
| `--load <SaveName>` | none | the existing world the dedicated server starts into. Needs `--map`. `start` and `host-mode`. |
| `--map <Map>` | none | the map a server world uses. |
| `--new <Map>` | none | create a brand-new server world on this map. Mutually exclusive with `--load`. |
| `--save-name <n>` | none | the world name to write, for `save` and for `stop`. Required on the server, optional on a client. |

### Mods

| Option | Default | Means |
|---|---|---|
| `--mod <names>` | none | comma-separated mod names for `deploy`. Also positional: `testrig deploy SprayPaintPlus`. |
| `--configuration <Release\|Debug>` | Release | which build of a mod to deploy. |
| `--from-modconfig <path>` | none | an alternate `modconfig.xml` source for `update-mods --target server`, instead of the developer's own. Spelled to match the file it names, because the server half's refusal text names the flag. |

### Driving

| Option | Default | Means |
|---|---|---|
| `--command '<text>'` | none | one line for the dedicated server's stdin. Fire and forget. `send` only. |
| `--path <p>` | none | a control-plane path, for example `/status`. `call` only. |
| `--body <json>` | none | raw JSON request body for `call`. **Never parsed here**, so anything in it reaches the plugin verbatim. |
| `--call-timeout-seconds <n>` | 0 | how long ONE control-plane request may take. 0 derives it from the request's own `timeoutMs` plus 30 s, floored at 120 s and at 300 s for `/host`, `/connect`, `/save`, `/load`, `/newworld` and `/waitfor`, capped at an hour. |
| `--stage <s>` | menu | readiness stage for `wait`: `ping`, `modsLoaded`, `menu`, `inWorld`, `process`. |
| `--wait-seconds <n>` | 300 | how long a BLOCKING WAIT waits: the readiness barrier, and a save awaiting confirmation, on both halves. On `lock` it is the queue budget; on `stop` it bounds the save it may perform first. |
| `--timeout-seconds <n>` | 30 | process-teardown grace for `stop`. Never a save budget. |

`--wait-seconds` used to be 30 on the server and 300 on the client for the same flag with the same meaning, so a slow but successful save produced a false warning on one half only. 300 wins because a false warning is indistinguishable from a real one, and the whole contract of `save` is that it warns rather than claiming success. `--timeout-seconds` is kept separate for the mirror-image reason: the server's stop once fed the teardown grace into a save confirmation, so raising the kill timeout silently raised how long a save was given to land.

### Instance shape

All ten refuse against `--target server`, which has one identity and no instances. `--game-port` and `--update-port` are deliberately not in that set: both are also the dedicated server's own start-time flags, so they have something to bind to there.

| Option | Default | Means |
|---|---|---|
| `--role <client\|host>` | client | what an instance is FOR. A host runs the simulation and plays. `create`. |
| `--port <n>` | 0 | control-plane TCP port. 0 derives 27700 + index. `create`. |
| `--game-port <n>` | 0 | RakNet UDP port. 0 derives 27800 + index for an instance, 28016 for the server. `create`, `start`, `host-mode`. |
| `--update-port <n>` | 0 | dedicated-server update port. 0 means 28015. `start`, `host-mode`. |
| `--client-id <v>` | none | Steam-shaped client id for a new instance. Must be a non-zero number. `create`. |
| `--username <v>` | none | in-game name for a new instance. Defaults to the instance name. `create`. |
| `--width <n>` | 800 | instance window width. `create` only. |
| `--height <n>` | 600 | instance window height. `create` only. |
| `--force-gameplay-input` / `--no-force-gameplay-input` | on | keep gameplay input alive on an unfocused window. `create`. |
| `--seed-mods` / `--no-seed-mods` | on | seed a new instance's mods from the developer's set. `create`. |
| `--desktop <name>` | StationeersRig | the Win32 desktop instances run on. Created, never switched to. `create`, `remove`, `start`, `update-game`. |
| `--instances-root <path>` | none | where instance trees live. Overrides `STATIONEERS_CLIENTRIG_ROOT`. `create`. |

### Reading

| Option | Default | Means |
|---|---|---|
| `--tail <n>` | 50 | how many log lines to show. `logs`. |
| `--grep <re>` | none | regex filter over the log. Combines with `--tail`: filter first, then tail the matches. `logs`. |
| `--out-file <f>` | none | write `snapshot` output to this file instead of stdout. |
| `--unity` | off | `logs` on an instance: read the **pre-BepInEx Unity log** at `data/<instance>/logs/unity-<stamp>.log` (newest wins) instead of `BepInEx/LogOutput.log`. Every failure before BepInEx loads lands there, and no verb ever printed it, so a hard boot failure was invisible from the launcher. Plain `logs` names the file when one exists. |

### Playtests

| Option | Default | Means |
|---|---|---|
| `--only <pattern>` | `*` | wildcard over check NAMES, applied once over the set compiled into this binary. Selecting what runs is `--only`; it is never `--target`. |
| `--evidence-root <path>` | `TestRig/playtest/evidence` | where a run writes its bundle. |

## Working sequences

### First time, or after a fresh clone

```powershell
# Directory.Build.props <StationeersPath>, and STEAMCMD_PATH in the environment. See DEV.md.
# Instance trees are hard links, so they must be on the game install's volume:
$env:STATIONEERS_CLIENTRIG_ROOT = '<drive of the game install>\StationeersRig'

TestRig\testrig.exe lock --purpose "Rig bring-up"
# note the id from the TESTRIG-OWNER line
testrig update-game --as <id>                                 # both halves
testrig update-mods --as <id>                                 # both halves
testrig create --target host1   --as <id> --role host
testrig create --target client1 --as <id>
testrig unlock --as <id>
```

The control-plane plugin is deployed into an instance at `create` time, so a new plugin build needs `create --force` per instance, not a `deploy`.

### Host a world from a driven client, with a joiner

The host must be in its world before the joiner connects.

```powershell
testrig lock --purpose "Host-side glow check for SprayPaintPlus"

testrig start --target host1 --as <id>
testrig wait  --target host1 --stage menu
testrig call  --target host1 --as <id> --path /host --body '{"world":"Lunar"}'
#   200 only once NetworkServer.IsHosting is true. The body carries hostPort, the
#   resolved savePath, localClientId, the roster and a full /status.
testrig wait  --target host1 --stage inWorld --wait-seconds 600

testrig start --target client1 --as <id>
testrig wait  --target client1 --stage menu
testrig call  --target client1 --as <id> --path /connect --body '{"address":"127.0.0.1","port":27801}'
testrig wait  --target client1 --stage inWorld --wait-seconds 600

testrig status --as <id>
#   under host1: liveRole=listenHost hosting=True hostPort=27801 connectedClients=1

# ... drive the test ...

testrig save --target host1 --as <id> --save-name HostGlowCheck   # only if the next session needs it
testrig stop --target clients --as <id> --release
```

Hosting an existing save instead of creating a world is `--body '{"save":"HostGlowCheck"}'`. Exactly one of `save` or `world`. World ids are `Lunar`, `Mars2`, `Europa3`, `MimasHerschel`, `Venus`, `Vulcan2`; not `Moon`.

Two hosts at once works (each has its own game port by index), but nothing guarantees a joiner reaches the one you meant, so always name the port in `/connect` and confirm from the host's roster.

### A dedicated-server test

```powershell
testrig lock --purpose "Playtesting network paint for SprayPaintPlus"
# stage any save you want to survive NOW, before the first mutating command
testrig stop   --target server --as <id>                          # if anything is alive
dotnet build Mods/SprayPaintPlus/SprayPaintPlus.sln -c Release
testrig deploy SprayPaintPlus --target server --as <id>
testrig start  --target server --as <id> --load Luna --map Lunar   # or --new Lunar
testrig wait   --target server --stage inWorld --wait-seconds 600
# drive it: testrig send / testrig logs --grep / InspectorPlus request files,
# or join a driven client to 127.0.0.1:28016 with call --path /connect
testrig stop --target server --as <id> --save-name AfterRun --release
```

The developer can also join by hand: the regular client, Direct Connect to `127.0.0.1:28016`, no password. There is no `-connect` flag on the client, so that step is manual. If you then go idle waiting on them, say what the reservation window is and raise `--idle-ceiling-minutes` to cover it.

### Run a mod's playtest checks

```powershell
testrig playtest
testrig playtest --only "the join summary*"
testrig playtest --evidence-root .work/2026-08-14-spp-run
```

The harness takes and releases the lock itself, per check, and provisions nothing. Do not hand-roll the bring-up sequence when checks exist. See `TestRig/playtest/CLAUDE.md`.

### After a game update

```powershell
testrig lock --purpose "Rig update to <version>"
testrig status                       # names each half's version and what is stale
testrig update-game --as <id>        # both halves: SteamCMD here, a re-link there
testrig update-mods --as <id>        # both halves
testrig unlock --as <id>
```

`status` compares each half against the developer's install and prints `current` or `STALE (source is <version>)` with the fixing command. That report exists because the rig once reported staleness for client instances only, so an agent updated the clients and was told it was done. The version itself is read from `<data folder>\StreamingAssets\version.ini`, whose first line is `UPDATEVERSION=Update <x.y.z.w>`; the PowerShell launcher read a `version.txt` at the install root that has never existed in any Stationeers install, so every provision stamp recorded the Unity `FileVersion` instead and no game update could be detected at all.

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
- **The basename must match the `--load` argument.** `Luna_pgp_test/` holding `Luna.save` will not load; rename on copy.
- Source is a bare `Luna.save`: `mkdir $dest; cp $src $dest/<SaveName>.save`. Source is already a save-shaped folder: copy its contents in as they are. Source is `<Other>/<Other>.save`: copy in, then rename inside the destination.

If no save exists and a test asks for `--load`, the command fails; use `--new <Map>` instead. Never read or copy out of the developer's own save folder.

## Readiness

| `--stage` | Means |
|---|---|
| `ping` | the control plane answers at all. The game is still booting, and this never touches the Unity main thread. |
| `modsLoaded` | `loadedPluginCount >= 10`: StationeersLaunchPad finished loading Workshop mods. The comparison is inclusive, and it used to be exclusive against a constant named for a minimum, so an instance sitting at exactly 10 was reported not ready and then failed with a message naming a number it had reached. |
| `menu` | `gameInitialized == true` and `phase == "menu"`. The splash screen is gone and the menu is up. |
| `inWorld` | `phase == "inWorld"`. |
| `process` | **the dedicated server's process is up, and that is explicitly NOT readiness.** The pid registers long before the world is tickable, and the gap is dominated by save size. Use it when you want to know the wrapper started, never as a substitute for `inWorld`. |

`menu` is the one stage that refuses against the server, which never has one: it enters its world from the command line. `ping` and `modsLoaded` work on both halves, because the merged plugin loads into both and the server answers on `127.0.0.1:27750`.

Wait for `menu` before touching the menu or the ImGui overlay: at `modsLoaded` the splash screen is still drawing and it suppresses the in-game windows. Cold boot to `menu` is about 100 s.

`inWorld` is **not** a readiness stage for a host. A world can be up with hosting silently not happening. The host's post-condition is `/status.hosting == true` with `/status.role == "listenHost"`, which `POST /host` asserts before it answers 200.

On the server, `wait --stage inWorld` polls `/status` on `127.0.0.1:27750` and needs `phase == "inWorld"`. That is the process stating its own game state, and it replaced an inference that was measured wrong: the barrier used to drop a minimal InspectorPlus request and treat its deletion as proof. On 2026-08-15 a `--new Moon` the game had rejected produced a server with no world, running indefinitely, and the probe was consumed anyway, so the barrier reported "the world is loaded and the simulation is ticking" about a world that did not exist. Consumption is not evidence, and nothing writes a probe any more.

Two things end that wait early rather than at the deadline. A `No such world name:` line in the log is a hard failure carrying the game's own list of what it would have accepted, because the server prints it once and then runs forever with no world. And nothing answering on 27750 at all is reported as its own failure, naming `testrig deploy TestRig --target server --as <id>`, because without the plugin nothing there can prove a world is loaded. `status` reporting the pid alive is not readiness, which is what `--stage process` is honest about.

## Playtests

`testrig playtest` runs a mod's in-game checks with nobody at the keyboard and reports **pass**, **fail** or **inconclusive** per check. Full contract: `TestRig/playtest/CLAUDE.md`.

Three things about the verb itself:

- **Checks are C# compiled into this binary**, globbed from `Mods/<Mod>/playtests/**/*.cs` into `TestRig.Playtests`. An AOT binary cannot load managed assemblies at runtime, so a check cannot be a file discovered on disk and **adding one means a rebuild**. That is already true of every other change because of the source-hash rule, and it is one command. A binary with no checks compiled in says so rather than reporting an empty pass.
- **`--only` selects checks; `--target` never does.** A check declares the instances it needs and brings them up itself, so naming half the rig could not change what runs, only what the report claimed to cover. `playtest --target server` is refusal 19.
- **Exit 8 is its own code.** 0 is all-pass, 1 means a check read a value from the authority and found the wrong one, 8 means nothing failed but something could not be measured. A caller that cannot tell 1 from 8 will eventually treat one as the other.

Attestation derives from a check's own location through `[CallerFilePath]`: the compiler records where the check was written and the check cannot lie about it, which replaces three declared fields. The two remaining config counts are replaced by a content hash of the deployed DLL against the build, which is the question they were approximating; the PowerShell `Assert-BinaryUnderTest` compared file **length** despite documenting a content comparison, so a same-length different build attested cleanly.

## The dedicated server half

`TestRig/DedicatedServer/` holds `install/` (the SteamCMD-managed binaries plus the mirrored `BepInEx/` tree and the doorstop loader) and `data/` (`setting.xml`, `server.log`, `saves/`, `scripts/`, `mods/`, the pid files and the control file). State is split out of the install tree so binaries can be wiped and re-installed without losing worlds.

**Lifecycle.** `start` launches a detached wrapper (the binary re-invoking itself as `host-mode`) with no console window, so nothing claims focus. The wrapper owns the server process: it spawns it with redirected stdin, polls `data/control.cmd` every 250 ms and forwards each command. `data/host.pid` is the wrapper, `data/server.pid` is the game, `data/control.cmd` is a one-command queue written by atomic rename. On exit the wrapper removes all three. If the wrapper itself is killed the server can be orphaned; `status` detects that and `stop` cleans it up.

**The flag set `start` applies:**

```
-batchmode -nographics
-settingspath  <DedicatedServer>/data/setting.xml
-logFile       <DedicatedServer>/data/server.log
-settings SavePath          <DedicatedServer>/data
-settings GamePort          28016     (override with --game-port N)
-settings UpdatePort        28015     (override with --update-port N)
-settings LocalIpAddress    127.0.0.1
-settings AutoSave          true
-settings AutoPauseServer   false
-settings UPNPEnabled       false
-settings ServerName        "Local Test"
-settings ServerMaxPlayers  4
-settings ServerAuthSecret  x
-load <SaveName> <Map>      OR   -new <Map>
```

`LocalIpAddress 127.0.0.1` pins RakNet to loopback; without it RakNet binds the first interface that is up, which on a machine with a LAN is the LAN address, and Direct Connect to `127.0.0.1:28016` then fails. `AutoPauseServer false` keeps the simulation running with nobody connected. `ServerAuthSecret x` enables `serverrun` from a connected client. No `ServerPassword`: a loopback-only bind makes external connections impossible at the network layer. `-settings SavePath` is passed **here and never on a client**; that asymmetry is deliberate and the reason is under "The client half". Details and the source-verified reasoning: `Research/GameSystems/DedicatedServerSettings.md`.

**The world parks, and it never ticks once.** `GameManager.DelayedStartupPause` pauses a dedicated server's world about five seconds after start when no client is connected. Measured over 287 s on 0.2.6428.27798 with `-new Lunar` and force-unpause off: `GameTickCount` stayed at **0** for the whole run, `SetGamePause` fired twice, both before any tick, and `ElectricityManager.ElectricityTick` never fired at all. So a simulation-tick pump is dead on a parked server, "a few ticks then a pause" is not a detector, and anything that has to observe the simulation needs `AutoPauseServer false` plus a connected client, or InspectorPlus's `Force Unpause Without Client`. What is unaffected is the Unity main thread, which keeps running at about 24 Hz throughout, so a control plane inside the process answers normally while the world is parked.

**Deploy versus sync.** `deploy` writes this repository's built mods to `install/BepInEx/plugins/<X>/<X>.dll` (the BepInEx Chainloader path); a dev-plugin goes to `data/mods/Local_<X>/` instead (the StationeersLaunchPad path), because it needs an `About.xml`. `update-mods` mirrors the developer's whole enabled mod set into `data/mods/` and **wipes that folder**, so anything a deploy put there goes with it: sync first, deploy second. The same DLL in both load paths is fatal, not untidy: `Awake` fires twice and every Harmony patch registers twice, so `deploy` removes a stale copy from the other path.

**Stop before deploying.** The Mono runtime holds an exclusive lock on every loaded plugin DLL on Windows, so a deploy onto a running server fails with a sharing violation or leaves a half-written DLL the next start picks up as broken plugin bytes. Both `deploy` and `update-mods` refuse while the server or its wrapper is alive.

**Version coupling.** The server's `BepInEx/` tree, including StationeersLaunchPad and its siblings (LaunchPadBooster, StationeersMods.Interface, StationeersMods.Shared, NetworkBufferFix), must match the client's exactly, or the join handshake rejects clients with a version mismatch. `update-game` re-syncs it and overlays the StationeersLaunchPad server zip, which carries `RG.ImGui.dll` that the client install does not have.

**In-process probes.** `ScenarioRunner` (`TestRig/DedicatedServer/dev-plugins/ScenarioRunner/`) is the deployed probe plugin today. Use it when a snapshot is the wrong shape: state evolution across many ticks, or stimulating a method rather than reading one. It runs from a Harmony postfix on `ElectricityManager.ElectricityTick`, which is a **ThreadPool worker** and only fires while the simulation is running. Pick a scenario in `install/BepInEx/config/net.scenariorunner.cfg`, then grep `install/BepInEx/LogOutput.log` for `[ScenarioRunner]` (not `data/server.log`, which carries Unity output). Its catalogue and authoring guide: that folder's `README.md`.

Its replacement is built and not yet deployed: `TestRig/dev-plugins/TestRig/` merges `ScenarioRunner` and `ClientDriver` into one plugin that loads into both halves, so a scenario becomes invocable over HTTP (`GET /scenarios`, `POST /scenario/run|arm|disarm`) instead of armed through a config value and read out of a log. See "Dev-plugins" below.

**Offline save editing** is `tools/save-edit/`: read a save ZIP, mutate `world.xml`, write a new ZIP, with the game not running. Use it for persisted state (fields on existing Things, cloning a Thing to a position, adding or dropping network ids) and an in-process probe for anything that depends on a simulation tick or on adjacency-driven registration. Always work on a copy inside `data/saves/`.

**InspectorPlus** works here as everywhere else: requests into `install/BepInEx/inspector/requests/`, snapshots out of `install/BepInEx/inspector/snapshots/`. With no client connected the world is parked and requests are not processed, so set `Force Unpause Without Client` under `[Server - Headless]` in `install/BepInEx/config/net.inspectorplus.cfg`. That force-unpause is a one-shot in a `GameManager.StartGame` postfix and has been observed not to survive a world reload; if a dropped request is not consumed within seconds of a loaded world, the world has re-parked. See `Research/Workflows/InspectorPlusUsage.md`.

There is no clean verb. Wiping binaries is "delete `install/`, then `update-game --target server`"; wiping worlds by hand is the developer's call, and `remove` refuses against the server for exactly that reason.

## The client half

`TestRig/ClientRig/` holds `dev-plugins/ClientDriver/` (the control plane inside each instance), `data/rig.json` (the registry, one entry per instance), `data/<instance>/` (manifest, provision stamp, `setting.xml`, save root, logs, pid file) and `instances/<instance>/` (the hard-linked game tree, which normally lives on the install's volume instead).

**What `create` builds.** `rocketstation_Data`, `MonoBleedingEdge` and the engine binaries are NTFS hard links: 1,053 of them on game 0.2.6428.27798, sharing about 6.9 GB. That count is a function of the install's file list and moves with every game update, so treat "about a thousand" as the durable figure and read the create summary for the real one. `app.info`, `doorstop_config.ini`, `Fixing The Controls modifiers.ini` and the whole `BepInEx/` tree are real copies, because a mod writes to them and a hard link would reach back into the developer's install. The control-plane plugin lands in `BepInEx/plugins/`. Local mods are copied into the instance's own save root with `modconfig.xml` repointed and `SavePathOverride` set (`--no-seed-mods` skips the mod seed, never the redirect). `imgui.ini` and `output_log.txt` are not carried.

**Defaults by index**, so instances created with no flags never collide: control plane TCP 27700+index, game port UDP 27800+index, ClientId 900000000000+index. Override with `--port`, `--game-port`, `--client-id`, `--username`, `--width`, `--height`. `--role` is `client` or `host`.

**`create` refuses a duplicate ClientId, control port or game port.** Neither is fussiness. The server keys a player's body on ClientId and `Brain.RegisterBrain` overwrites silently, so two clients sharing one id resolve onto **one character** with nothing warning; a test that believes it has two players and has one produces plausible, meaningless results. And two RakNet sockets on one UDP port coexist rather than conflicting, so a colliding game port produces a joiner that reaches something with no error anywhere. 27015/27016 (the game client's own defaults) and 28015/28016 (this rig's server) are refused too.

**A rebuild (`create --force`) replaces the TREE only.** `data/<instance>/` survives, deliberately, so a staged save does not evaporate on a plugin rebuild: the save root, the logs, the pid file and the game-written `setting.xml` all stay, and only `userdata/mods/` is rewritten. `--role` and `--game-port` are kept unless typed again, so picking up a new plugin build never silently demotes a host or moves its port. A fresh lock is what clears `setting.xml`.

The registry entry is written **before** the save-path redirect is attempted, and that ordering is load bearing: the redirect throws for a host when `stationeers.launchpad.cfg` does not exist yet, and the PowerShell threw after building the tree and before writing the entry, leaving a tree with no registry entry and every remedy its own message named unreachable.

**The instances root is recorded at create time.** Hard links cannot cross volumes, so the trees normally sit on the game install's drive rather than under `ClientRig/instances/`. The resolved root goes into the registry entry and every later action, including the state reset, reads it back, so `--instances-root` is typed once. Typing it again moves the tree (the old one is left behind and the rig says so). An entry from before the field existed falls back to `--instances-root`, then `$env:STATIONEERS_CLIENTRIG_ROOT`, then `instances/`, and names `create --force` as the fix. `status` prints the resolved tree, whether it exists and which source it came from.

**`data/<instance>/provision.stamp`** records when the instance was built and out of what: the time, the role, both ports, the source install and its version, and the plugin DLL's build time. It is the only way to answer "is this instance stale" after a game update or a plugin rebuild.

**Teardown is classification first, action second.** `stop` classifies the whole rig before touching any of it, then disconnects joiners and confirms it, saves whoever holds a world and confirms it, quits hosts, and leaves unclassifiable instances last. It refuses to take a host down while something outside the teardown is attached to it, and refuses an instance whose control plane does not answer and therefore cannot be ruled out as a host (`--force` accepts the loss). After the process is gone it clears `StartLocalHost` from that instance's `setting.xml`. `start` throws over a running instance rather than skipping it.

**Hosting refusals you may hit:**

| Answer | Means |
|---|---|
| `409 cannot host from gameState=Running` | `/host` loads or creates the world itself and must start from the menu, because `StartLocalHost` is only read at world entry. `POST /disconnect` first. |
| `409 ... already reports role=<x> at the main menu` | this process's `NetworkRole` is not `None`, so a clean host is impossible. Known cause: an inbound Steam P2P request promoting an idle process to server. Restart the instance. |
| `409 save path not isolated` | the instance would write its world inside the developer's real user-data folder. Re-create it so `SavePathOverride` points at its own save root. |
| `409 duplicate ClientId` | a sibling claims this instance's id. The host's id exists first, so a colliding joiner takes over the host's body. |
| `409 the world is up but NetworkServer.IsHosting is false` | hosting silently did not happen, almost always the port. The response carries the console tail and the requested port. |

**Plugin configuration.** The manifest at `data/<instance>/instance.json` is written by the rig and **wins over** the plugin's `BepInEx/config/*.cfg` for every value it carries, because it is rewritten on every create and therefore describes this run, whereas a `.cfg` is sticky across sessions. `GET /instance` reports `valueSources` so the winner is never a guess.

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
| Hosting | `Role` | client | what the instance is FOR. Advisory to the plugin (`/host` works on any instance) but load bearing to the rig, which drives teardown ordering and its host refusals off it. The live answer is `/status.role`. |
| | `Game Port` | 27016 | the RakNet port `/host` binds when the request names none; `create` sets 27800+index |
| Gameplay Input | `Force Gameplay Input` | false | holds the cursor locked and hidden so per-frame input consumers keep running unfocused. **Without it, `/input/*` is delivered and then discarded.** Created instances get it on. |
| | `Force Gameplay Input Everywhere` | false | assert the gate outside a loaded world too |

## The control-plane endpoint catalogue

The in-process control plane is a loopback `TcpListener` speaking minimal HTTP/1.1. Every body field can also be a query parameter, so anything is reachable from a browser or `curl`. **A query parameter is the reliable way to send a Windows path**: it is percent-decoded by the HTTP layer and never goes through the JSON string reader. `GET /help` prints this list at runtime and is the authority.

Today the deployed plugin is `ClientDriver` (client instances only) with 64 endpoints. The merged `TestRig` plugin carries all 64 across, adds `/scenarios`, `/scenario/run`, `/scenario/arm` and `/scenario/disarm`, and loads into the dedicated server as well; there, an endpoint that needs something a headless process does not have refuses with **409** carrying `needs`, `because` and `instead` rather than a bare 404 or an empty object. The refused set is listed in `TestRig/dev-plugins/TestRig/README.md`.

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
| `POST /console/exec` | `{command, waitFrames, waitMs}`. Runs a console command and returns the lines it produced. **This is the endpoint**, not `/console/run`, which has never existed. |
| `POST /console/print` | `{text, level}`. A marker line for bracketing a test. |
| `GET /console/commands?contains=` | registered console command names. |

### Session

| Endpoint | Notes |
|---|---|
| `POST /connect` | `{address, port, wait, timeoutMs, suppressTimeout, allowDuplicateIdentity}`. Direct Connect. Refuses a join into a known ClientId clash. |
| `POST /host` | `{save\|world, difficulty, start, port, serverName, password, maxPlayers, wait, timeoutMs, allowDuplicateIdentity}`. Load or create the world AND serve it. Must start from the menu. Defaults: `port` = the manifest's game port, `maxPlayers` 4, `difficulty` Normal, `timeoutMs` 300000. 200 only once `IsHosting` is true. The save-root isolation requirement is unconditional and fails closed; the merged plugin removes the `requireIsolatedSavePath` parameter outright and answers 400 if it is passed. |
| `POST /disconnect` | `{wait, timeoutMs}`. Back to the main menu. |
| `POST /quit` | `{hard}`. `Application.Quit()`, or a `Process.Kill` when `hard`. |
| `GET /saves` | local save list. |
| `POST /save` | `{name, wait, timeoutMs}`. Host or single player only. **200 only on a confirmed save**; asked-for-but-unconfirmed is 409 with `requested:true` and a warning. `timeoutMs` defaults to 180000. |
| `POST /load` | `{save, wait, timeoutMs}`. |
| `POST /newworld` | `{world, difficulty, start, wait, timeoutMs}`. |
| `POST /waitfor` | `{phase=menu\|joining\|loading\|inWorld, timeoutMs}`. |
| `GET/POST /savepath` | `{path, force}`. Retargets a RUNNING client's user-data root. `force=true` reaches the developer's tier-1 folder; **never pass it**, and note `call --body` hands the body through unread. `GET` reports `realUserDataPath` and `reportedDefaultPath` side by side. The merged plugin removes `force` outright. |
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
| `POST /config/set` | `{guid, section, key, value, save}`. Writes the live entry, effective immediately. `save` defaults to **true**: an unpersisted write disappears on the next reload, producing a test that passed once and cannot be reproduced. Pass `save=false` for in-memory only. |
| `POST /config/reload` | `{guid}`. Re-read the `.cfg` from disk. |
| `GET /reflect?type=&member=&expand=&expandLimit=&key=` | any STATIC field or property by full type name. `key=<k>` answers "does this dictionary contain that key" without dumping it. |
| `GET /reflect/members?type=` | every static member of a type with its runtime value type. |
| **`GET /thing?refId=&refIds=&fields=&type=&comparePrefab=&expand=&expandLimit=&key=`** | **read any member of any Thing.** `fields` is a comma-separated list of instance fields or properties, public or private, on the runtime type or any base type; a dotted path walks (`ParentSlot.Parent.ReferenceId`) and `[n]` indexes. A member that does not exist answers `ok:false` naming the types searched, never an empty value. Each field carries `prefabValue` and `matchesPrefab`, and every row carries a `location` block (in a slot or on the ground, which slot, which hand, and whether THIS process is the authority). |
| **`GET /reflect/instance?refId=&member=&type=&expand=&key=`** | one instance member on one object, the instance twin of `/reflect`. `type` pins which declaring type the member is looked up on, which is the only way to reach a private base field a derived type shadows. |
| **`GET /thing/members?refId=&type=&contains=&limit=&values=`** | every instance member of a Thing or of a bare type, with declaring type and current value. Diagnostic of last resort. `values=false` skips invoking every getter. |
| **`GET /dlc`** | this process's DLC entitlement, the session pool, what has been removed, and the ordering a removal must be sequenced into. |
| **`POST /dlc/remove`** | `{dlc, scope=owned\|shared\|both}`. **Removal only:** the one write it performs clears bits out of the value already there, so no route, parameter or value can add entitlement, and a request carrying add/grant/set/give/own/unlock is refused rather than ignored. In memory, per process, never persisted. **Sequence it before world entry:** a joiner announces `DLCManager.GetOwnedDLC()` at the end of its join and a listen host re-seeds the pool at the end of the load, so a later removal is silently undone. |
| **`POST /dlc/restore`** | put back the baseline this process held before its first removal. Takes no arguments. |

The five bold rows are what make a per-Thing instance field, and a DLC owner versus non-owner test, reachable without an InspectorPlus round trip. Two of the Spray Paint Plus checks run on the DLC routes.

### Scenarios (merged plugin only)

| Endpoint | Notes |
|---|---|
| `GET /scenarios` | the whole catalogue with `armed`, `dispatched`, `bootOrdered`, `requiresAssembly` and `blocked` per id, plus `unknownArmed` for anything that reached the dispatcher unrecognised, `armedSource`, and `ticksSeen` with a warning naming `DelayedStartupPause` when it is 0. |
| `POST /scenario/run?id=&ticks=` | run one scenario for N **simulation ticks** (not frames) and return every `[ScenarioRunner]` line it produced plus a `pass` / `fail` / `inconclusive` verdict. The caller never names a log file. |
| `POST /scenario/arm`, `POST /scenario/disarm` | change the armed set, effective on the next simulation tick. `persist=true` (the default) also writes it outside `BepInEx/config`, so the state reset cannot silently disarm it. |

Roughly seven probes are genuinely load-ordered and must stay armed at boot, because no HTTP call can be timed against a world load. Everything else is a one-shot over settled state and should be invoked directly.

## The session lock

The active lock is `TestRig/session.lock` (gitignored). Do not hand-create it. Its keys, one per line:

| Field | Means |
|---|---|
| `owner` | the short id printed by `lock`; echo it back with `--as` |
| `purpose` | short human-readable reason |
| `acquired_at` | ISO 8601 UTC, when first acquired |
| `refreshed_at` | last heartbeat, drives `ttl_minutes`. The busy self-renew moves this and only this. |
| `active_at` | last OWNER action, drives the ceiling. Nothing but the owner's own commands move it. |
| `ttl_minutes` | heartbeat window (10) |
| `idle_ceiling_minutes` | absolute idle window after which the lock is reclaimable busy or not (60) |
| `host` | machine name, for diagnostics |

A lock written before `active_at` existed falls back to `acquired_at`, which is older than any owner action and so never makes a lock look fresher than it is. Every timer field fails **closed**: missing, unparseable or negative reads as expired or as past the ceiling.

**`lock` prints `TESTRIG-OWNER <id>` as its last line**, and only `lock` prints it. `status` deliberately does not, because a status report is frequently about somebody else's lock and a harness scraping the id out of one would take a lock it does not hold. The PowerShell equivalent never printed the line at all, because `New-RigLock` returned a bare string and the guarded write could never fire; the old harness required that exact line by regex, threw `inconclusive/rig-unavailable` without it, and then unlocked in its `finally` with the id it never received.

**The lock stores no process identity.** There is no "owner died" transition: a session spans many launcher processes, so the idle ceiling is the entire substitute.

**Acquisition is serialised across processes** by a named system mutex, so two agents that both find the rig free cannot both walk away believing they own it. Without it the loser would only find out on its next mutating command, minutes and one full instance build later.

**Liveness** is: the timer is fresh, OR the rig is busy, AND the idle ceiling has not been reached. Past the ceiling a lock is reclaimable whether or not the rig is busy, and the reclaim stops what is running on both halves. That is deliberate: before the ceiling existed, an agent that died holding one instance held the whole rig until a human authorized a break, which makes a hung agent a blocker rather than a delay. The price is real and stated plainly: a reclaim can stop instances belonging to a session that was merely very quiet. So the reclaim is loud, names the purpose it took the rig from and what was running, and `status` prints the countdown.

**Reclaiming past the ceiling is not a `--break-lock`.** A lock past its ceiling is dead by these rules, in the same way an expired one on an idle rig is dead. `--break-lock` takes a genuinely LIVE lock and stays human-gated. It also does not run the reclaim path, so breaking a live lock leaves the previous session's processes running.

**The busy reason names what is happening**, not a process count: how many instances are running, which one is HOSTING, and how many clients are connected to it. That text is what a human reads when deciding whether to authorize a break, and "2 client instance(s) running" cannot tell a live hosted test at minute 40 from two instances somebody forgot to stop. A pid file alone is not proof: the process image is checked, because Windows recycles process ids and these files outlive their processes on a force-kill or a reboot. An instance created before the manifest carried a role reports "role unknown" and still counts as busy.

**A queueing agent keeps the holder's lock alive**, because every poll runs the busy self-renew. That is a property of the design, not a bug, but it means `lock --wait-seconds N` against a busy rig extends the very lock it is waiting for.

**Waiting for a human.** You are about to go idle, so you will not be refreshing. Tell the user the reservation in plain terms, raise `--idle-ceiling-minutes` to cover the wait, and set a sensible `--ttl-minutes` join window. While a player is actually connected the lock stays live on its own. If nobody joins, the timer lapses and the rig frees, which is intended: an agent waiting on a sleeping user must not block the others.

## State hygiene

The restore runs at both ends of a session (see `TestRig/CLAUDE.md`) and on demand through `reset`.

| Half | Reset | Kept |
|---|---|---|
| Client, per instance | `data/<n>/setting.xml` (it carries `StartLocalHost`), the Unity logs, `imgui.ini`, a STALE `game.pid`, the instance's `BepInEx/config` (re-copied from the source install, then `SavePathOverride` re-applied), `LogOutput.log*`, `BepInEx/cache/`, `BepInEx/inspector/requests/` and `snapshots/`, loose files at the top of `userdata/saves/`, and any world under `userdata/saves/` THIS session created | `data/rig.json`, `instance.json`, `provision.stamp`, `userdata/mods/` and `modconfig.xml`, the deployed control plugin, the hard links, and **every instance world that predates the session** |
| Dedicated server | the probe plugin's armed `Scenario` value (blanked, the rest of the file untouched), `install/BepInEx/scenariorunner/requests/` and `give/`, `install/BepInEx/inspector/requests/` and `snapshots/`, `data/setting.xml`, stale `server.pid` / `host.pid` / `control.cmd`, and any `data/saves/` world THIS session created | every world that predates the session, `data/mods/`, `install/modconfig.xml`, the deployed plugin DLLs, every other `install/BepInEx/config/*.cfg` |

**Client-instance worlds are session-scoped, not wiped.** They used to be deleted wholesale, on the reasoning that a client has no worlds worth keeping. A listen host writes real worlds there, so a host's world was destroyed by the next restore while the identically-tiered server world beside it was protected. Both halves now follow the same rule: a world is deleted if and only if the session marker recorded a world set and this world is not in it.

**A failed enumeration is not an empty set.** A world scan that cannot read a root returns a failure status, and the marker omits the key entirely rather than writing `worlds=`, so the reader lands in the degraded path that keeps every world. In PowerShell the enumeration swallowed every error and returned empty, the marker wrote an empty list, the snapshot reader tested the key's *presence* rather than its value, and the planner's predicate was then true for every world it found: 25 real worlds and 185 MB queued for irreversible deletion with no warning at all.

**A world name that cannot round-trip fails the whole scan.** The marker has no escaping, and a directory named `" Luna"` is legal on NTFS; the PowerShell reader trimmed it on the way back, so the key matched nothing and the world was deleted. One exotic directory name now costs a session its world scoping, loudly, instead of costing somebody a world, silently.

**More than five world deletions in one restore refuses**, names every world at risk, and changes nothing. `reset --allow-bulk-world-delete` overrides it.

Three things are **reported** rather than touched, because deleting them would be worse than leaving them: a seeded mod older than its source (the fix is `update-mods` or `create --force`), the retained world count and total size, and any server config that changed since the last reset.

Two entries in the client list are load bearing and easy to misread. `setting.xml` carries `StartLocalHost`, and an instance that silently comes up hosting while a test believes it is a joiner is the worst failure available here. And the `BepInEx/config` re-copy **wipes `SavePathOverride`**, so the re-apply immediately after it is not tidiness: without it the next launch of that instance writes its worlds into the developer's tier-1 folder. Both the create path and the reset write that setting through one implementation; do not add a second.

**What "clean" means is captured, not hardcoded.** `capture-baseline --as <id>` declares the rig as it stands to be the definition of clean, writing `TestRig/baseline/` (gitignored). Three classes:

- **config**: every `*.cfg` under each instance's and the server's `BepInEx/config`, plus each `modconfig.xml`. Bytes are stored and copied back, so a value a session changed or a file it deleted goes back exactly. Kilobytes in total.
- **payload**: deployed plugins and seeded mods. Hashed and inventoried, **never stored and never restored**: rolling one back would silently undo a deliberate deploy, and "my fix is not in the game" is the quietest possible failure. A payload that moved makes the baseline stale instead.
- **world**: saves, recorded by name and size, informational only. Nothing ever reads them back. What happens to a world is decided by the session marker, never by this manifest.

**Stale is loud, never silent.** A baseline is stale when the game version moved, an instance appeared or disappeared, or a plugin or seeded mod was rebuilt. Every reset then warns, names the reason, and names `capture-baseline` as the fix. It never blocks the lock: an unclean rig must not become an unlockable one. Staleness changes nothing about what a reset does. With no baseline at all, config behaviour is what it was before baselines existed, and every reset says so.

**The reset surface is an allow-list.** It touches only the classes above and its own hardcoded targets, so a deliberate instance-scoped change anywhere else in a tree (a real, never-hard-linked assembly dropped into one instance's `rocketstation_Data\Managed\`, say) survives every restore and is never reported as drift. A deny-list would have the opposite default and would scrub exactly those changes.

**A failed reset is loud, names the instance, and throws.** You still hold the lock; unlock and take it again to retry. A failed or refused restore does not clear the dirty marker, so the next acquisition tries again.

**Shared per-user state is reported, never restored.** `PlayerCookie-v2.xml`, the PlayerPrefs key and `Blueprints\` cannot be isolated, and writing them back would itself be the write the save rules forbid. A cheap snapshot is taken at acquisition into `TestRig/session.state.json` (gitignored) and the delta is printed at release. It fixes nothing; it turns state that was invisible until a later test failed into a line at the session boundary.

## Dev-plugins

A dev-plugin is a BepInEx plugin that exists only to drive or observe the rig. It never ships to the Workshop, never graduates into `Mods/`, and only makes sense paired with this binary. `WorkshopHandle` is 0 and stays 0.

| Path | What it is | State |
|---|---|---|
| `TestRig/dev-plugins/TestRig/` | the merged plugin: one assembly, both halves, `ClientDriver`'s 64 endpoints plus the four scenario routes | built, **not deployed** |
| `TestRig/ClientRig/dev-plugins/ClientDriver/` | the control plane inside a game client | deployed today |
| `TestRig/DedicatedServer/dev-plugins/ScenarioRunner/` | in-process probes on the dedicated server | deployed today |

All three follow the same layout, so a fourth slots in with nothing to rename:

```
<dev-plugins root>/<Name>/
    <Name>.sln
    <Name>/
        <Name>.csproj
        About/About.xml
        ... source ...
```

Build with `dotnet build <path>/<Name>.sln -c Release`. A server-half plugin then goes out with `testrig deploy <Name> --target server --as <id>`; a client-half plugin is copied at instance-create time, so picking up a new build is `create --force` per instance rather than a deploy.

**Nothing deploys the merged plugin yet.** The deploy resolver does not know `TestRig/dev-plugins/`, and the client half still names `ClientDriver` explicitly. Do not delete either replaced tree until parity has been proven on real hardware. Details, the measured pump design and the endpoints it refuses on the dedicated server: `TestRig/dev-plugins/TestRig/README.md`.
