# Client Rig

Developer tooling. Provisions and drives N isolated Stationeers **game clients** on one machine, so an agent can run a real multiplayer test with nobody at the keyboard.

Two pieces:

- **`ClientDriver`**, a BepInEx plugin, is the control plane inside each instance. It exposes a loopback HTTP API for reading the in-game console, connecting to a server, hosting a session, saving the world, inspecting state, injecting input, spawning, screenshots, and reading and writing mod config.
- **`client-rig.ps1`** is the launcher. It provisions instances (hard-linked from the real install), creates the isolated Win32 desktop, starts and stops them in the right order, saves their worlds, and fans one command out across the rig.

One instance can **host**: `-Role host` plus `POST /host` makes it a listen host, a single process that runs the simulation, accepts joiners over loopback RakNet, and plays a character. The dedicated server cannot do the last part, so anything needing a host who plays lives here. See "Hosting a world" below, and `Research/GameSystems/ListenHost.md` for the game internals.

It is the client-side counterpart to `ScenarioRunner` (which probes the dedicated server) and `InspectorPlus` (which dumps scene state to JSON on request). Where those two answer "what is the simulation doing", this answers "make these clients do a thing, then tell me what actually happened".

**Not a player-facing mod, and it must never ship.** It is a remote control plane for the game. `WorkshopHandle` is 0 and stays 0.

**Read `CLAUDE.md` next to this file, and `TestRig/CLAUDE.md` above it.** The first is the short version of the rules below; the second carries what this rig shares with the dedicated server half (the one session lock covering both, what the rig touches outside its own folder, the save tiers, the `dev-plugins/` layout, the launcher-flag conventions). Every mutating action here needs the lock.

---

## Design note: where the boundary sits

The split between the plugin and the launcher is **process creation**.

The launcher owns everything outside a game process, and everything that has to keep working when a process is dead, wedged, or not yet born: laying down an instance tree, creating the desktop, starting, killing, PID files, and the fan-out. The plugin owns everything inside a process, which is everything that needs the Unity main thread or the game's own types: input, state, config, the cursor gate, identity, the window.

There is no third category, and the two halves never overlap. Two consequences fall out of that and are worth stating because they are what make the shape correct rather than merely tidy:

**The fan-out lives in the launcher, not in the game.** Every interesting multi-client test is "set up A, set up B, act as A, observe both", and the interesting failures are ordering failures. A coordinator that lives inside one instance cannot supervise a barrier that another instance has stopped participating in, because a wedged client cannot report that it is wedged. The launcher is outside all of them and can.

**The per-instance manifest is written by the launcher and read by the plugin.** One writer, one reader, one file. Configuration used to live in three unconnected places (`net.clientdriver.cfg` for the port and identity, `stationeers.launchpad.cfg` for the save path, the command line for the rest), nothing tied them together, and two running instances produced `/status` blobs that were indistinguishable apart from the identity fields. Now `/status` leads with `instanceName`, and `/instance` answers the whole question.

---

## Setup

### 0. Take the rig session lock

Every mutating action refuses without it. One lock covers this rig and the dedicated server, because the two share the developer's game install and per-Windows-user Unity state that nothing separates.

```powershell
.\client-rig.ps1 -Lock -Purpose "Two-client paint check for SprayPaintPlus"
# prints an owner id; pass -As <id> on every mutating command, on either launcher
```

Gated: `-Provision`, `-Start`, `-Stop`, `-Save`, `-Remove`, `-Broadcast`, `-Call`. Free: `-Status`, `-List`, `-Logs`, `-Snapshot`, `-Wait`. Release with `-Unlock -As <id>` or `-Stop -All -As <id> -Release`. Full rules: `TestRig/session.lock.template`.

Add `-WaitSeconds N` to queue for up to N seconds when another session holds the rig; the default of 0 keeps the immediate refusal. It is a queue, not a reservation, and promises no ordering fairness.

Three things worth knowing before you go idle. A running instance keeps the lock live with no timer to save you, so leaving instances up holds the whole rig including the dedicated server; always stop them before releasing. `-Unlock` refuses outright while a listen host is still live, because releasing hands the rig to an agent whose `-Stop -All` would end the world mid-test. And `-BreakLock` (not `-Force`) is what takes a lock off another session, and it is human-gated.

#### Taking the lock also cleans the rig

A **new** lock resets each provisioned instance, so your test starts on a known state instead of on the previous session's leftovers. Per instance it deletes `setting.xml` (it carries `StartLocalHost`, and an instance that silently comes up hosting when your test believes it is a joiner is the worst failure available here), everything under `data/<instance>/userdata/saves/`, the Unity logs, `imgui.ini`, a stale `game.pid`, the instance's `BepInEx/config` (re-copied from the source install, because `POST /config/set` defaults to `save:true` and every value a previous test flipped is otherwise sticky), `LogOutput.log*`, `BepInEx/cache/`, and the InspectorPlus `requests/` and `snapshots/` folders. An unconsumed request file matters more than it looks: it is picked up on the NEXT launch, so another session's request fires inside your run.

**`SavePathOverride` is re-applied immediately after the config re-copy, because the copy wipes it.** An instance without that setting writes its worlds into the developer's tier-1 save folder. The re-apply goes through `Set-RigSavePathOverride` in `TestRig/rig-reset.ps1`, the same function `-Provision` calls, so there is exactly one implementation of that write. Whether a failed write is fatal depends on whether the reset caused it: if the config was re-copied (so a working redirect was just wiped) the reset FAILS and names the instance, because the damage is ours. If no copy happened, which only occurs on an instance that has never been launched and therefore has no `stationeers.launchpad.cfg`, it warns loudly and the session still starts, since failing would make the lock unobtainable and `-Provision -Force` needs the lock.

Kept, deliberately: `data/rig.json` (deleting it loses every instance definition), `instance.json`, `provision.stamp`, `userdata/mods/` and `modconfig.xml`, the deployed `ClientDriver`, and the roughly 1,050 hard links. A seeded mod older than its source tree is **reported**, not deleted; the fix is `-Provision -Force`, which is the only thing that can re-seed it correctly.

- Refused while any instance is live. The lock is still granted and its id still printed, with a warning naming what is running: an unclean rig must not become an unlockable one.
- Never fires when you re-assert a lock you already hold, so changing your purpose or TTL mid-test cannot wipe your own run.
- `-Lock -KeepState` skips it and prints exactly what it skipped, for a save or a config value staged on purpose.
- **It resets between SESSIONS.** A session spans many start/stop cycles by design, so two unrelated tests run under one lock get no reset between them. Release the lock and take it again when the subject changes.
- `-Unlock` and `-Stop -Release` print what moved in the shared per-user Unity state (`PlayerCookie-v2.xml`, PlayerPrefs, `Blueprints\`). None of that can be isolated and none of it is ever restored, so the report is the whole mechanism.

### 1. Pick a location for the instance trees

An instance is a **hard-linked** copy of the real install, so it costs a few megabytes instead of seven gigabytes. Hard links cannot cross NTFS volumes, so the instance trees must be on the same drive as the game install. The repository often is not.

```powershell
# once per shell, or record it in DEV.md
$env:STATIONEERS_CLIENTRIG_ROOT = '<drive of the game install>\StationeersRig'
```

The launcher refuses with the exact command to fix it if this is wrong, rather than quietly making a 7 GB copy. Per-instance state (manifest, `setting.xml`, save root, logs, PID file) is ordinary files rather than links, so it stays under `data/` beside the script regardless.

### 2. Build the plugin

```powershell
dotnet build TestRig/ClientRig/dev-plugins/ClientDriver/ClientDriver.sln -c Release
```

Provisioning copies whatever is in `bin/Release` into the instance. After a plugin change, rebuild and re-provision with `-Force`. That `-Force` is the routine kind: it rebuilds an instance you already own and never touches the rig lock.

### 3. Provision instances

```powershell
.\client-rig.ps1 -Provision -As <id> -Instance client1
.\client-rig.ps1 -Provision -As <id> -Instance client2
.\client-rig.ps1 -Provision -As <id> -Instance host1 -Role host    # a listen host
```

Every per-instance value defaults off the instance index, so instances provisioned with no flags never collide: control plane on TCP 27700+index, game port on UDP 27800+index, ClientId 900000000000+index. Override with `-Port`, `-GamePort`, `-ClientId`, `-Username`, `-Width`, `-Height`. `-Role` is `client` (the default) or `host`; on a rebuild both `-Role` and `-GamePort` are kept unless typed again, so `-Provision -Force` to pick up a new plugin build never silently demotes a host or moves its port.

Provisioning refuses a duplicate ClientId, control-plane port, or game port up front. That is not fussiness in either case:

- **ClientId.** The server keys a player's body on it, `Brain.RegisterBrain` overwrites silently, and two clients sharing an id resolve onto **one character** with nothing anywhere warning. A test that believes it has two players and has one produces results that look plausible and mean nothing. A listen host consumes a ClientId of its own, and it exists first, so a joiner that collides takes over the host's body.
- **Game port.** Two RakNet sockets on one UDP port do not conflict; both bindings coexist and a datagram goes to whichever matches its destination address. Nothing errors, so the joiner reaches something and the test is wrong silently. Ports 27015/27016 (the game client's own defaults) and 28015/28016 (this repository's dedicated server) are refused for the same reason.

`data/<instance>/provision.stamp` records when the instance was built and out of what: the provision time, the role, both ports, the source install and its `version.txt`, and the plugin DLL's build time. It is the only way to answer "is this instance stale" after a game update or a plugin rebuild.

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
.\client-rig.ps1 -Start -As <id> -All     # on the isolated desktop; never takes your foreground
.\client-rig.ps1 -Wait  -All -Stage menu  # barrier across the rig; roughly 100 s from cold
.\client-rig.ps1 -Status -As <id> -All
```

Then drive them:

```powershell
.\client-rig.ps1 -Call -As <id> -Instance client1 -Path /connect -Body '{"address":"127.0.0.1","port":28016}'
.\client-rig.ps1 -Wait -All -Stage inWorld -WaitSeconds 600
.\client-rig.ps1 -Snapshot -All -OutFile before.json
```

Or talk to one directly, which is often easier when exploring:

```powershell
Invoke-RestMethod http://127.0.0.1:27701/status
Invoke-RestMethod http://127.0.0.1:27701/help
```

Teardown:

```powershell
.\client-rig.ps1 -Stop -As <id> -All -Release   # -Release also frees the rig lock
.\client-rig.ps1 -Remove -As <id> -Instance client1
```

`-Stop` is host-aware and orders the teardown itself; see "Hosting a world" below before stopping anything that holds a world.

### Readiness has three distinct stages and they are not interchangeable

| `-Stage` | Means |
|---|---|
| `ping` | BepInEx loaded the plugin. The game is still booting. |
| `modsLoaded` | `loadedPluginCount > 10`: StationeersLaunchPad finished loading Workshop mods. |
| `menu` | `gameInitialized == true` and `phase == "menu"`. The splash screen is gone and the menu is actually up. |
| `inWorld` | `phase == "inWorld"`. |

Wait for `menu` before touching the menu or the ImGui overlay. `modsLoaded` alone is not enough: the splash screen is still drawing at that point and it suppresses the in-game ImGui windows.

`inWorld` is **not** a readiness stage for a host. A world can be up with hosting silently not happening, because `NetworkServer.Host()` gives up quietly after three failed binds. The host's post-condition is `/status.hosting == true` with `/status.role == "listenHost"`, which `POST /host` asserts for you before it answers 200.

### The launcher actions

| Action | Lock | Does |
|---|---|---|
| `-Lock -Purpose <s> [-TtlMinutes N] [-WaitSeconds N]` | acquires | Take the rig session lock. Prints the owner id for `-As`. `-WaitSeconds` queues instead of failing at once. |
| `-RefreshLock -As <id>` | refreshes | Bump the timer while actively driving a test. |
| `-Unlock -As <id>` | releases | Give the rig back. Warns if instances are still running, and REFUSES while a listen host is live. |
| `-Provision -Instance <n> [-Role client\|host] [-GamePort N] [-Force]` | needs | Build or rebuild an instance tree, write its save redirect, seed its mods, write its manifest and provision stamp. |
| `-Start -Instance <n>\|-All` | needs | Launch on the isolated desktop, hosts first. Throws rather than skipping when an instance is already up. |
| `-Stop -Instance <n>\|-All [-Release]` | needs | Host-aware teardown: classify, refuse, disconnect joiners, save the world holder, `/quit`, kill after `-TimeoutSeconds` (default 30), then clear `StartLocalHost` from the stopped instance's `setting.xml`. |
| `-Save -Instance <n>\|-All [-Name <s>]` | needs | Write the world through `POST /save` and wait for the game's own confirmation. Warns rather than claiming success on a timeout. |
| `-Remove -Instance <n>` | needs | Delete the tree and the instance's save root. Refuses while it is running, and refuses to delete a host's world while a joiner is attached. |
| `-Broadcast -All -Path <p> [-Body <json>]` | needs | One request to every instance. Throws on a partial result. |
| `-Call -Instance <n> -Path <p> [-Body <json>]` | needs | One request to one instance. |
| `-Status [-Instance <n>]` | free | The rig lock, then per instance: process, classified role, both ports, identity, phase, live role, hosting, host port, connected clients by name and id, foreground verdict, input gate, identity conflicts. |
| `-List` | free | The rig registry as a table, plus live role, hosting and client count for the instances that are running. |
| `-Wait -All -Stage <s> [-WaitSeconds N]` | refreshes | Barrier, default 300 s. Fails loudly, per instance, with what each one was actually doing. |
| `-Snapshot -All [-OutFile <f>]` | free | `/status` from every instance in one document. A relative `-OutFile` is rooted at the rig folder (which is gitignored deny-all), not at the shell's working directory. |
| `-Logs -Instance <n> [-Tail N] [-Grep <re>]` | free | That instance's BepInEx log. |

`-Broadcast` and `-Call` are gated even though they read like queries, because they drive a live client: `/quit` ends one, `/host` puts one into a world it serves, and `/savepath` retargets where one writes its saves. `-Wait` needs no lock but refreshes one you already hold, because a barrier can legitimately outlast the TTL.

`-Broadcast` throws when any instance failed, deliberately. A partial broadcast leaves the rig in mixed state, and "both clients agree on X except for this one difference" is the shape of nearly every paired check, so half-applying it silently is how a test comes out wrong.

---

## Hosting a world

A driven instance can be a **listen host**: one process that runs the simulation, accepts joiners over loopback RakNet, and plays a character. That last part is what the dedicated server cannot do, so a test whose host has to hold an item, paint something, or apply their own client-half setting belongs here. The game internals are in `Research/GameSystems/ListenHost.md`; the short version is that a listen host is `NetworkRole.Server` exactly like the dedicated server and differs from it by `GameManager.IsBatchMode` alone.

### The order is the whole trick, and it runs opposite at each end

**Startup: the host must be IN ITS WORLD before any joiner connects.** `/connect` has nothing to reach until the host is hosting, and a join issued against a host that is still loading fails in a way that reads like a bad address.

**Teardown: the host goes LAST.** Joiners disconnect first and confirm it, then whoever holds the world saves and confirms it, then the host quits. `-Stop` does that ordering itself, so `-Stop -All` is safe; what is not safe is killing a host by hand while joiners are in it.

### End to end

```powershell
# 0. one lock for the whole session, on either launcher
.\client-rig.ps1 -Lock -Purpose "Host-side glow check for SprayPaintPlus"

# 1. two instances: one host, one joiner
.\client-rig.ps1 -Provision -As <id> -Instance host1   -Role host
.\client-rig.ps1 -Provision -As <id> -Instance client1 -Role client

# 2. the host, all the way into its world
.\client-rig.ps1 -Start -As <id> -Instance host1
.\client-rig.ps1 -Wait  -Instance host1 -Stage menu
.\client-rig.ps1 -Call  -As <id> -Instance host1 -Path /host -Body '{"world":"Lunar"}'
#    /host answers 200 only once NetworkServer.IsHosting is true. Its body carries
#    hostPort, the resolved savePath, localClientId, the client roster and a full /status.

# 3. only now the joiner, at the host's game port (-Status prints it)
.\client-rig.ps1 -Start -As <id> -Instance client1
.\client-rig.ps1 -Wait  -Instance client1 -Stage menu
.\client-rig.ps1 -Call  -As <id> -Instance client1 -Path /connect -Body '{"address":"127.0.0.1","port":27801}'
.\client-rig.ps1 -Wait  -Instance client1 -Stage inWorld -WaitSeconds 600

# 4. confirm from the HOST that the joiner actually arrived
.\client-rig.ps1 -Status -As <id> -All
#    under host1:
#      network:    liveRole=listenHost hosting=True hostPort=27801 connectedClients=1
#      client:     <username> (<clientId>)        one line per joiner

# ... run the test ...

# 5. persist the world if the next session needs it
.\client-rig.ps1 -Save -As <id> -Instance host1 -Name HostGlowCheck

# 6. teardown in the order above, then release
.\client-rig.ps1 -Stop -As <id> -All -Release
```

Hosting an existing save instead of creating a world is `-Body '{"save":"HostGlowCheck"}'`. Exactly one of `save` or `world` is allowed. World ids are `Lunar`, `Mars2`, `Europa3`, `MimasHerschel`, `Venus`, `Vulcan2`, not `Moon`.

### What to assert on, and what not to

- **`/status.role`** is the one computed answer to "what is this process": `menu`, `singlePlayer`, `joinedClient`, `listenHost`, `dedicated`. Read it; never re-derive from `isClient` / `isServer`. A listen host reports `isServer` true and `isClient` **false**, which is the opposite of the intuition that a hosting player is a client that also serves.
- **`/status.hosting`** is `NetworkServer.IsHosting`. "The call returned" proves nothing: `NetworkServer.Host()` no-ops from the main menu and gives up quietly after three failed binds.
- **`/status.connectedClients`** is the server-side roster, `{clientId, username, state, isHost, connectionId}` per row. It is what makes "did the second instance actually arrive" assertable from the host without asking the joiner. It is empty on anything that is not a server, and the host appears in its own roster, so subtract one when counting joiners (the launcher already does).
- **`/status.startLocalHostPersisted`** is read from the instance's `setting.xml` on disk, not from memory. See the gotcha below: an instance carrying `true` there comes up hosting on its next launch whether or not the test wants it to.

### Refusals you may hit, and what each means

| Answer | Means |
|---|---|
| `409 cannot host from gameState=Running` | `/host` loads or creates the world itself and has to start from the menu, because `StartLocalHost` is only read at world entry. `POST /disconnect` first. |
| `409 ... already reports role=<x> at the main menu` | This process's `NetworkRole` is not `None`, so a clean host is impossible. The known cause is an inbound Steam P2P request promoting an idle process to server. Restart the instance. |
| `409 save path not isolated` | The instance would write its world inside the developer's real user-data folder. Re-provision so `SavePathOverride` points at its own save root. The `requireIsolatedSavePath=false` escape writes a world into the developer's saves; never pass it. |
| `409 duplicate ClientId` | A sibling claims this instance's id. The host's id exists first, so a colliding joiner takes over the host's body. |
| `409 the world is up but NetworkServer.IsHosting is false` | Hosting silently did not happen, almost always the port. The response carries the console tail and the requested port. |
| `[Stop] ... is hosting and something ... is still attached` | A joiner outside this teardown is connected. Take it down too, or accept the loss with `-Force`. |
| `[Stop] ... cannot be classified` | A live instance whose control plane does not answer cannot be ruled out as a host, and cannot be asked to save. Wait for it to boot (about 100 s) or accept the loss with `-Force`. |

**Two hosts at once is possible but rarely what you want.** Each needs its own game port, which `-Provision` already guarantees by index. What it cannot guarantee is that a joiner reaches the one you meant, so name the port explicitly in `/connect` and confirm from the host's roster.

---

**Two flags mean exactly what they mean on `dedicated-server.ps1`, and one of them did not used to.** `-Force` is the routine override inside your own session (`-Provision -Force` rebuilds an instance you own); taking a lock off another session is `-BreakLock`, which is human-gated. They were the same flag with opposite risk across the two launchers, which is how a live test gets torn down by muscle memory. `-TimeoutSeconds` (default 30) is process-teardown grace for `-Stop` on both; the readiness barrier here is `-WaitSeconds` (default 300), which it did not used to be, so an older note reading `-Wait -TimeoutSeconds 600` means `-Wait -WaitSeconds 600`.

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

Section `Client - Hosting`:

| Key | Default | What it does |
|---|---|---|
| `Role` | `client` | What this instance is provisioned for, `client` or `host`. **Advisory:** it gates nothing, `POST /host` works on any instance, and the live answer is `/status.role`. It exists so a reader, and the launcher's teardown ordering, can tell what an instance was MEANT to be when its control plane is not answering. |
| `Game Port` | `27016` | The RakNet port `POST /host` binds when the request names none. 27016 is the game's own client default; the launcher provisions 27800 plus the instance index instead. Every concurrent host needs a distinct value, clear of the dedicated server (28015/28016) and of every other instance. |

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
| `GET /instance` | Name, port, provisioned role, game port, identity, manifest path, which source each value came from, sibling ports, and the duplicate-ClientId verdict. `rescan=true` forces a fresh peer probe. |
| `GET /status` | Everything: instance, game state, network role, hosting, world, player, foreground, input gate, save hygiene, driver counters. The fields that matter for a multiplayer test are below. |
| `GET /player` | Player block only. |
| `GET /colors` | `GameManager.CustomColors` catalogue with swatch indices. |
| `GET /plugins` | Every plugin found by assembly scan, with its assembly path. |
| `GET /nearby?radius=&filter=&limit=` | Things around the player. |

**The `/status` fields a multiplayer test reads.** The first four answer "what is this process and who is on it"; the last five answer "where does it write, and will it host again next boot".

| Field | Means |
|---|---|
| `role` | `menu \| singlePlayer \| joinedClient \| listenHost \| dedicated`, computed in one place. **Read this rather than `isClient` / `isServer`**, which are three views of one enum and read backwards for a listen host. |
| `hosting` | `NetworkServer.IsHosting`. The only honest post-condition for a host attempt. |
| `hostPort` | `NetworkServer.HostPort`, or 0 when not hosting. |
| `connectedClients` | Server-side roster: `{clientId, username, state, isHost, connectionId}`. Empty on anything that is not a server. The host is in its own roster. |
| `settingsPath` | The `setting.xml` this instance would write, which the launcher points at `data/<instance>/`. |
| `savePathResolved` | Where this process would write a world right now. |
| `saveRootIsolated` | Whether that root is safely outside the developer's real user-data folder. Fails closed: unresolvable is not isolated. |
| `startLocalHostPersisted` | `StartLocalHost` as it stands **on disk**, so `true` means this instance hosts again on its next launch. `null` when the file or the element is absent. |
| `startLocalHostInMemory` | The live value. It disagreeing with the persisted one is normal and is the point of reporting both. |

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
| `POST /host` | `{save\|world, difficulty, start, port, serverName, password, maxPlayers, wait, timeoutMs, allowDuplicateIdentity, requireIsolatedSavePath}`. Become a listen host: load or create the world **and** serve it on `127.0.0.1:<port>`. Must start from the menu. Defaults: `port` = the manifest's game port, `maxPlayers` 4, `difficulty` Normal, `timeoutMs` 300000, `requireIsolatedSavePath` **true**. 200 only once `NetworkServer.IsHosting` is true; see the refusal table under "Hosting a world". |
| `POST /disconnect` | `{wait, timeoutMs}`. Leave to the main menu. |
| `POST /quit` | `{hard}`. `Application.Quit()`, or `GameManager.QuitGame()` (a `Process.Kill`) when `hard`. |
| `GET /saves` | Local save list. |
| `POST /save` | `{name, wait, timeoutMs}`. Persist the world and wait for the game's own confirmation line. Omit `name` to save under the current station name. Host or single player only: the game's `save` command is scoped `HostOrSinglePlayer`. **200 only on a confirmed save**; a save that was asked for but not confirmed answers 409 with `requested:true` and a `warning`, so "accepted" can never be mistaken for "on disk". `timeoutMs` defaults to 180000; a big world can outlast it and still be running. |
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

Each provisioned instance gets its own save root at `data/<instance>/userdata/` through StationeersLaunchPad's `SavePathOverride`. That root is tier 3 under the repository save-tier rule (root `CLAUDE.md`, "Workflow: save file access tiers"): agent-managed, free to edit, and deleted by `-Remove` along with the tree. The developer's own save folder stays tier 1 and is never touched.

**This got sharper when an instance became able to host.** A joining client reads a world the server owns and writes none of its own; a host CREATES one. So the redirect is written on every provision, unconditionally and ahead of the mod seed, and a failure to write it is a hard throw for `-Role host` and a warning for `-Role client`. It used to sit at the end of the mod seed, behind that function's early return for a developer with no `modconfig.xml`, which meant an instance provisioned on such a machine (or with `-SeedMods:$false`) got no redirect at all and wrote into the developer's tier-1 folder behind a warning whose text only mentioned mods.

`POST /savepath {"path": "..."}` points `Settings.CurrentData.SavePath` at a scratch directory. Every save resolves through `StationSaveUtils.GetSavePath()` on each call, so worlds created after the redirect land there. The change is in memory; the game persists settings on a clean exit, so put it back at the end or exit with `POST /quit {"hard":true}`.

Because instances already have their own save root, this endpoint is for one-off redirects rather than routine rig use. **It retargets a client that is already running**, which is why `-Call` and `-Broadcast` are lock-gated.

If a provision could not write `SavePathOverride` (no `stationeers.launchpad.cfg` yet), a `-Role client` instance is provisioned with a warning and has NO separate save root, so it would write into the developer's user-data folder. Treat that warning as a stop: launch once to generate the config, then re-provision with `-Force`. A `-Role host` provision throws instead of warning, because a host creates a world and that world would be created inside the developer's saves.

The state reset that runs when a new lock is taken re-copies the instance's `BepInEx/config` from the source install, and **that copy wipes `SavePathOverride`**. The re-apply immediately after it is therefore load bearing, not tidiness: without it, the next launch of that instance writes its worlds into the developer's tier-1 folder. Both the provision and the reset write the setting through one function, `Set-RigSavePathOverride` in `TestRig/rig-reset.ps1`, precisely so the two cannot drift; `TestRig/rig-reset.tests.ps1` measures the wipe and then pins the re-apply.

`POST /host` applies the same rule at the other end of the chain: it refuses to start unless the resolved save root is outside the developer's real folder. `requireIsolatedSavePath=false` is the documented override and there is no correct reason to pass it.

Three things it does that a plain setter would not, all because the failure mode here is not recoverable by retrying:

- It echoes both the path as received and the path as resolved, so you can verify what landed.
- It **refuses** a path containing a control character rather than using it. The JSON reader now preserves a backslash that is not part of a recognised escape, so a path like `"C:\Rig\Scratch"` round-trips correctly where it used to lose both backslashes. What that cannot fix is the escapes JSON genuinely defines: `\b`, `\f`, `\n`, `\r` and `\t` still decode, so `"C:\builds"` and `"C:\files"` cannot survive a request body intact. Send such a path as a query parameter, or double every backslash.
- It **refuses** a path inside the developer's real user-data folder unless you pass `force=true`, since redirecting away from that folder is the entire point. That refusal is the only thing standing between this endpoint and the developer's tier-1 save folder, and it lives in plugin code rather than in any rule an agent reads first. **Never pass `force=true` here unless the user asked for exactly that.** It also fails closed: if the real folder cannot be resolved at all, the redirect is refused rather than allowed.

  The comparand is computed independently (`MyDocuments\My Games\Stationeers`, the same value the launcher computes) and deliberately does not go through `StationSaveUtils.DefaultPath`. That getter looks like the obvious source and is the wrong one: StationeersLaunchPad prefixes it to return `SavePathOverride`, so on a provisioned instance it names the instance's own folder. Comparing against it inverted both answers, refusing a legitimate redirect inside the instance's save root and allowing a redirect into the developer's real one with no `force=true`. `GET /savepath` reports `realUserDataPath` (the gate) and `reportedDefaultPath` (what this process thinks the default is) side by side so the distinction is visible rather than assumed.

---

## Gotchas

Everything below was hit for real on 0.2.6403.27689 with StationeersLaunchPad 0.5.0, except the hosting group, which is read off the game's own code and is marked as such.

**`StartLocalHost` can outlive the run that set it (from the code, not yet observed here).** `/host` writes `Settings.CurrentData.StartLocalHost` as a direct field assignment and never saves, deliberately: the `settings <name> <value>` console command would have been the obvious route and is a trap, because `SettingsCommand.OnValueChanged` calls `Settings.SaveSettings()`, which serialises the WHOLE `SettingData` to `setting.xml`. Closing the in-game settings panel does the same. Any of those turns "this instance hosted once" into "this instance hosts every launch", and a joiner that silently came up as a host is a test that is confidently wrong. Four defences now: the direct write, `/status.startLocalHostPersisted` reading the flag back off disk, `-Stop` clearing it from `setting.xml` after the process is gone, and the state reset deleting `setting.xml` outright when a new lock is taken. Note that `-Provision -Force` rebuilds the instance TREE and does NOT reset `data/<instance>/`, so `setting.xml` survives a rebuild; taking a fresh lock is what clears it.

**Two RakNet sockets on one UDP port do not conflict (from the code).** Both bindings coexist and a datagram is routed by destination address, so a colliding game port produces a joiner that connects to something and a test that is wrong with nothing logged anywhere. `-Provision` refuses a game port that collides with another instance, with 28015/28016, or with 27015/27016. Nothing outside the rig is checked.

**Hosting can fail silently (from the code).** `NetworkServer.Host()` returns early with only a console line when called from the main menu (`GameState == None` fails its guard), and after a failed bind it retries three times a second apart and then gives up quietly. Never treat "the call returned" as success; assert `hosting == true` and `role == "listenHost"`. `/host` does that for you and hands back the console tail when it does not happen.

**A host consumes a ClientId, and it exists first (from the code).** `NetworkManager.TotalPlayersInGame` on a host is `Clients.Count + 1`, the host appears in its own `connectedClients` roster, and a joiner that shares the host's id takes over the host's body via `Brain.RegisterBrain`'s silent overwrite. Count the host as one of the rig's identities.

**A joined client cannot save the world.** The game's `save` command is scoped `HostOrSinglePlayer`, so `/save` refuses on a joiner rather than pretending. Save on the instance whose `/status.role` is `listenHost`.

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

The plugin sits under `dev-plugins/<Name>/<Name>.sln` with its source in `dev-plugins/<Name>/<Name>/`, which is the same layout `ScenarioRunner` uses on the server half. Folder, solution, project, assembly and namespace therefore all read `ClientDriver`, and a second client-side dev-plugin slots in beside this one with nothing to rename. The rule is in `TestRig/CLAUDE.md`.

```
client-rig.ps1            the launcher: provision, desktop, lifecycle, save, host-aware teardown, fan-out
CLAUDE.md                 the short rules, which auto-load; points here
README.md                 this file
RESEARCH.md               durable internals
dev-plugins/
  ClientDriver/
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
        Routes.Session.cs     connect, disconnect, saves, savepath, identity, instance, the shared world-entry poll
        Routes.Host.cs        /host and /save: become a listen host, persist a world, and prove both
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

**This folder is gitignored deny-all**, like the dedicated server half. `.gitignore` carries `/TestRig/ClientRig/*` plus a named allowlist for `client-rig.ps1`, `CLAUDE.md`, `README.md`, `RESEARCH.md` and `dev-plugins/` (whose `bin/` and `obj/` are ignored again). Everything else is local-only: `data/` (registry, manifests, provision stamps, per-instance settings, save roots, logs, PID files) and `instances/` (the hard-linked trees, which normally live on the game install's volume instead), both created on demand. A host's worlds land under `data/<instance>/userdata/saves/`, so nothing a host writes is committable either.

Deny-all rather than a short ignore list, because routine actions drop artifacts straight into this folder: `-Snapshot -All -OutFile before.json` writes here, and `/screenshot?path=shot.png` resolves relative to the instance working directory. Under an ignore-a-few-things list every one of those was committable by accident.
