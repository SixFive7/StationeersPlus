# TestRig plugin

The in-process half of the test rig. **One BepInEx plugin, loaded into both halves**: the game client and the headless dedicated server.

It replaces two plugins that did the same job in one process each:

| Replaced | What it was | Where it lived |
|---|---|---|
| `ClientDriver` | the loopback HTTP control plane inside a game client | `TestRig/ClientRig/dev-plugins/ClientDriver/` |
| `ScenarioRunner` | in-process probes on the dedicated server | `TestRig/DedicatedServer/dev-plugins/ScenarioRunner/` |

**Both source trees still exist and still build**, so a rig that has not built this one is not stranded, but nothing resolves to them by default any more: `create` and `deploy` resolve this plugin by name and sweep both predecessors out of both load paths. See "Wiring, and what the two replaced trees are still for" at the bottom.

- Assembly / ModID: `TestRig` / `net.sixfive7.testrig`. `WorkshopHandle` is 0 and stays 0.
- Target: `net472`, the game's Mono runtime.
- Build: `dotnet build TestRig/dev-plugins/TestRig/TestRig.sln -c Release`

## What is in it

| | Count |
|---|---|
| HTTP endpoints carried across from `ClientDriver` | 64 |
| HTTP endpoints added by the merge | 4 (`/scenarios`, `/scenario/run`, `/scenario/arm`, `/scenario/disarm`) |
| Scenario ids in the catalogue | 78 |
| Harmony patch classes | 21 (`ClientDriver`'s 21, minus its duplicate simulation-tick patch which merged with `ScenarioRunner`'s one, plus the new `GameManager.Update` primary pump) |

## The split exists because of the pump, not because of the listener

Nothing in the listener touches Unity, the game, or a graphics device. `Transport/HttpServer.cs` is a raw `TcpListener` on `127.0.0.1`, hand-rolled HTTP/1.1, one background accept thread, strictly sequential by design, owned by a static rather than by the MonoBehaviour (the game destroys that component 135-219 ms into boot, at frame 0, while the process keeps running), re-bound by a watchdog thread every 5 seconds. It is `System.Net.Sockets` plus `System.Text` on a runtime both halves already run, so it was never the obstacle. It is carried across intact.

The obstacle was what runs the work the listener accepts.

**The problem.** `ClientDriver.MainThreadPump.Drain` executed queued work on whichever thread called the pump. In a game client the hooks that fire run on the Unity main thread, so that was harmless. On the dedicated server the pump ClientDriver fell back to is a postfix on `ElectricityManager.ElectricityTick`, and that runs on a UniTask ThreadPool worker: measured across three runs its thread id rotated through 20, 25, 42, 50, 9, 58, 44, 45 and 57, and was **never 1**. Every `Main(...)`-wrapped route, which is most of them, would have executed its Unity-touching body off the main thread, where `UnityEngine.Object.FindObjectsOfType` crashes the engine native side intermittently. That is the reason every scenario body iterates `OcclusionManager.AllThings` instead.

**The fix, in two parts.**

1. **`Drain` executes nothing unless it is on the captured Unity main thread.** A thread-identity check, not a host check, so it also closes the same hole on a client whose hooks have stopped. A pump firing on a worker only advances counters, and the count of refused drains is reported (`/status.driver.offThreadPumpsRefused`). On the dedicated server it climbs once per simulation tick and is not an error: it is the counter that proves the guard is in force.

2. **How a queued item reaches the main thread is a selectable strategy**, reported everywhere. Both hosts get the same composite, `mainThreadDrain+unityMainThreadDispatcher`, tried in that order:

   | Route | Mechanism | Available when |
   |---|---|---|
   | `mainThreadDrain` (primary) | queue, drained from three main-thread hooks (below) | a drain has actually run on the main thread |
   | `unityMainThreadDispatcher` (backstop) | the game's own `Assets.Scripts.Util.UnityMainThreadDispatcher` | `Exists()`, checked before every submit |

   The order is not arbitrary. The marshal drains from `ManagerUpdate`, and `GameManager.Update` is the sole caller of `ManagerBase.ManagerUpdate` in the assembly, so the drain is available whenever the marshal is and potentially earlier. The marshal stays because it is the path that was independently measured executing under a paused world, and because a second route to thread 1 costs nothing.

   `UnityMainThreadDispatcher` is a game type, not one this plugin owns, so it survives the plugin component being destroyed. It is resolved reflectively, so a game update that renames it degrades to a reported-unavailable route and a 504 that names it, rather than a plugin that will not load.

3. **The drain has four hooks, and it needs all four**, because no single one covers both boot and steady state:

   | Hook | Covers | Rate | Both builds? |
   |---|---|---|---|
   | UniTask player-loop boot loop | everything before the first scene load, which headless is **frames 0 to ~1600-1850** | per frame, then retires | yes |
   | pump host `MonoBehaviour.Update` | **boot**, from the first scene load: frame 0 on a client, ~1600-1850 on the server | ~25 Hz throughout | yes |
   | `Assets.Scripts.GameManager.Update` postfix | steady state | ~24 Hz once `GameState.Running`, but **0.11-0.16/s before it** | yes |
   | `ImGuiManager.LateUpdate` postfix | the client splash window | per frame | client only (absent from the server assembly) |

   `GameManager.Update` alone would leave the control plane effectively frozen for the whole 80-90 s boot, which is exactly the window a caller spends polling for readiness. The pump host covers it on a client. It does not on the dedicated server, because the first `sceneLoaded` there is over a thousand frames in, and the boot loop is what covers that; see "The pump host" below.

The choice is logged at load and readable on `/ping` (`host`, `pumpStrategy`, `pumpReady`), `/instance` and `/status` (`host` block plus `driver.pumpStrategy`, `driver.pumpDrainReady`, `driver.pumpGameMarshalReady`, `driver.pumpHooks`, `driver.pumpNote`, `driver.mainThreadDrains`, `driver.hostUpdateDrains`, `driver.pumpHostCreatedAtFrame`, `driver.pumpBootLoopDrains`, `driver.pumpBootLoopState`, `driver.scenesLoaded`). Every 504 body names the strategy, both routes' readiness, and which hooks resolved.

**Scenario dispatch was deliberately NOT moved to the main thread.** Roughly 85 scenario bodies were written against the "runs on the simulation-tick worker" contract. Quietly marshalling them would change what they measure. `ElectricityTick` is kept as the **simulation-liveness signal** and the scenario pump, and it never drains.

### The pump host, and why it is created at the first scene load

A plugin's own `MonoBehaviour.Update` **never fires at all** on the dedicated server. Not rarely: zero times, in every measured run. The component and everything it creates in `Awake` are destroyed **135-219 ms later, at `Time.frameCount == 0`, before the first scene loads**, and `Start()` is never reached. `DontDestroyOnLoad` does not save it: the call appears to succeed (scene name `DontDestroyOnLoad`, handle -12) but does not bind, because no scene is loaded at that moment, so a `DontDestroyOnLoad` object and a plain one beside it die 1 ms apart.

That is the real mechanism behind the repo lore that "`Update` does not reliably fire after world load". The loop does not stall; the object is destroyed before it ever ticks. Only a **replacement** object ticks, which is why every earlier account looked inconsistent.

Static state is what survives, and that is what this plugin relies on: the listener is owned by a static, the Harmony patches persist, background threads persist, and so does a `SceneManager.sceneLoaded` subscription registered in `Awake`. So the pump host is created from **the first `sceneLoaded` callback**, at 282 ms and still at frame 0. Measured, it then survives indefinitely and misses nothing: Update 5867 at `Time.frameCount` 5867, no gap. Recreating at the later Base scene load instead puts the object at frame 1925 and loses everything before it. The handler stays subscribed so a later scene load re-creates the host if it ever dies again, and the two main-thread postfixes call the same idempotent creator as a backstop. Nothing creates it in `Awake`.

### The first scene load is frame 0 on a client and over a thousand frames in on the dedicated server

That difference was assumed away and is now measured. The server's own log line says it: `pump host created at frame 1834 (scene load 1)`. Headless there is no splash and no menu scene, so the first scene load is the mod-content load, and those frames pass before it with **no log output at all** between "ready on http://127.0.0.1:27750/" and the pump-host line.

**The number is a sample, not a constant.** 1834 in that instrumented run and **1635** in the first real one, on the same game build, because what varies is how much work happens before the mod-content load and that depends on the mod set. Nothing here keys off the value: the boot loop covers whatever the window turns out to be. `pumpHostCreatedAtFrame` is reported so a run can state what it got, and it is not an assertion target.

Nothing else was covering that window either, and the plugin's own code proves it: the `GameManager.Update` postfix calls the same idempotent `EnsurePumpHost()`, and the host was still logged as created from "scene load 1", so that postfix had fired **zero** times before it. `UnityMainThreadDispatcher` cannot help, because it drains from `ManagerUpdate` whose sole caller is that same `GameManager.Update`, and `ImGuiManager.LateUpdate` does not exist in the server assembly. So every `Main(...)`-wrapped route queued work that nothing would run and answered 504 after its 20 s budget, for the whole first thousand-odd frames of every headless boot.

Nothing in the launcher noticed, which is why this went unmeasured: the server half's `wait` uses process liveness and an InspectorPlus request-file probe rather than the HTTP plane, and `call --target server` refuses outright today. The cost was paid only by an agent hand-driving `127.0.0.1:27750` during a boot.

**The fix is a UniTask player-loop drain**, running from load until the pump host exists and then retiring. UniTask is the only mechanism alive that early headless, because nothing in the game assembly is running yet and there is therefore no Harmony hook to take: `Cysharp.Threading.Tasks.PlayerLoopHelper.Init` carries `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]`, so its player-loop subsystems are installed before any scene and before this plugin's `Awake`, and they are engine state rather than a scene object, which puts them in the same surviving category as the Harmony patches and the `sceneLoaded` subscription. Both installs ship `UniTask.dll`.

It is not privileged: it calls the same `Drain` as every other hook, and that still refuses to execute anything off the captured main thread, so a timing that resumed elsewhere would show up as `offThreadPumpsRefused` climbing rather than as Unity work on a worker. `/status.driver` reports `pumpBootLoopDrains` and `pumpBootLoopState` so the next real run can confirm or refute it, and a failure to start leaves behaviour exactly as it was.

### Do not build on `FixedUpdate`

`Update` and `LateUpdate` are unaffected by pause: 24.85-25.06 per second in every regime. `FixedUpdate` is not, and it does not track the flag you would expect. It is gated on `Time.timeScale`, not on `IsGamePaused`, and on a headless server the two disagree: `GameManager.StartGame()` assigns `Time.timeScale = 1f` while `IsGamePaused` is already true, so `DelayedStartupPause`'s `SetGamePause(true)` hits the `if (IsGamePaused != pauseGame)` guard and never drops the scale. A nominally paused server therefore still runs `FixedUpdate`, while a real `SetGamePause(true)` transition stops it dead. Neither emits a log line. Nothing the control plane depends on uses it.

## The two experiments: measured, not open

Both were run against the real dedicated server, game version 0.2.6428.27798, three runs of 190 s+ each, then re-measured by an independent fresh validator under the repo's research conflict protocol across four instrumented runs of 192-312 s. The two agree on every rate. Recorded here as results.

### 1. `ImGuiManager.LateUpdate` does not exist on the dedicated server

Not "present but never called": **the class is gutted in the server assembly**. Mono.Cecil metadata gives the client build 19 methods and 17 fields (`LateUpdate`, `RenderOverlay`, `Awake`, `InitializeImGui`, and more) against 1 method (`.ctor`) and 0 fields on the server build. The base chain `Singleton<T>` to `ManagerBase` to `MonoBehaviour` declares no `LateUpdate` either. Live, `AccessTools.Method(ImGuiManager, "LateUpdate")` returned null in all three runs and `FindObjectsOfType(ImGuiManager)` returned 0 instances at every sample.

**What changed because of it.** Everything riding that one postfix was dead headless: the drain, and with it `Epoch.Tick`, `JoinTrace.Tick`, `WindowMode.Tick` and the pump-object recreation. `GameManager.Update` is now the primary hook on both builds and carries all of them; the ImGui postfix stays as a client-only supplementary drain for the splash window before GameManager is up, and its `Prepare()` resolves to false headless on its own with no host check needed. The per-frame block is deduped by `Time.frameCount`, so a client running both hooks samples once per frame rather than twice. `Epoch` is therefore sampled on the dedicated server, which it would not have been: `epoch.stale` would have been permanently true there and every epoch block would have carried no information.

### 2. `UnityMainThreadDispatcher.Enqueue` does execute while the world is paused

The marshal choice was correct and the H.5 blocker is genuinely resolved. With `Force Unpause Without Client = false`, no client, 287 s: `IsGamePaused` true throughout, `GameTickCount` 0 for the entire run, `ElectricityTick` never fired once, yet `ManagerUpdate` ran at about 24 Hz (4,699 calls) and enqueued items executed **on thread 1** with 4-37 ms latency. 51 of 52 accepted items executed; none threw.

Two corrections to the mechanism, both encoded:

- **It has no `Update`.** Its twelve methods are `.cctor`, `.ctor`, `ActionWrapper`, `ClearAll`, `Enqueue(IEnumerator)`, `Enqueue(ChunkThread)`, `Enqueue(Action)`, `Exists`, `Instance`, `ManagerAwake`, `ManagerUpdate`, `OnDestroy`. It drains from `ManagerUpdate`, whose sole caller is `GameManager.Update`. Nothing here patches `ManagerUpdate`; patching `GameManager.Update` gets the same tick and is earlier.
- **`Exists()` is checked before every submit, not once.** It returned false for the first 35 seconds or so of boot, and `Instance()` **throws** in that window rather than returning null.

Latency is budgeted in seconds, not milliseconds, for anything that can land during world generation: single-item latency there was 4238 ms and 4650 ms against 4-37 ms once up. The default `Main(...)` budget is 20 s, which covers it, and the 504 body says so.

### 3. `MonoBehaviour.Update` fires only on a recreated object, and `GameManager.Update` is unusable during boot

Reconciled by the fresh validator, and it reverses part of the first account. **A plugin's own `Update` does not fire on the dedicated server at all**: not once, ever, across all runs. The natural reading is zero. What fires is a **replacement** object created after the first scene load. Mechanism and figures are in "The pump host" above: destroyed at 135-219 ms, always at `Time.frameCount == 0`, before the first scene loads, `Start()` never reached, `DontDestroyOnLoad` silently failing to bind. The earlier "0.3-0.8 s" figure was wrong and has been corrected everywhere it appeared.

The finding that changed this plugin's design: **`GameManager.Update` runs at 0.11-0.16 per second until `GameState.Running`**, while frames advance at 25 Hz throughout. Making it the sole primary, as the previous revision did, would have left the control plane nearly frozen for the entire boot. Fixed by adding the first-`sceneLoaded` pump host above, which was measured clean.

Also reconciled: **`FixedUpdate` is gated on `Time.timeScale`, not `IsGamePaused`**, and the two disagree on a headless server. See "Do not build on `FixedUpdate`" above.

### 4. The tick count at startup, and a corrected Research page

With force-unpause off and `-new Lunar`, the world ran **zero** ticks, not the eight some notes describe. `GameTickCount` stayed 0 for the full 287 s and `SetGamePause` fired twice, both before any tick. "A few ticks then park" is not a detector and is not used as one anywhere here. The `/scenarios` warning and the `/scenario/run` zero-tick error both state the measured behaviour, and both point out that the control plane is unaffected because the main thread keeps running at about 24 Hz while the world is parked.

The validator also corrected `Research/Patterns/MainThreadDispatcher.md`, whose recovery pattern 2 claimed `ElectricityTick` runs on the Unity main thread and recommended draining a main-thread marshalling queue from it. Measured: 115 calls, 115 of them off the main thread, on id 40 against main id 1. This plugin follows the corrected page; the thread-identity guard on `Drain` is precisely what catches that class of mistake, and it stays.

## Feature detection, and refusals that teach

`HostProfile` decides which process this is, once, at load. The discriminator is `GameManager.IsBatchMode`, which has to be the same one `StateReporter.Role()` uses to tell `dedicated` from `listenHost`, because both are `NetworkRole.Server`. At `Awake` the game's statics may not be populated, so the first answer comes from the command line (`-batchmode`) and is replaced by the game's own answer as soon as that is conclusive. `/status.host.settled` says which you got.

`HostGuard` refuses an endpoint the dedicated server cannot serve, before the route runs, with the launcher's three-part shape: **what the verb needs, why this host cannot provide it, and a command that works**. Status 409, and the body carries `needs`, `because` and `instead` as separate fields as well as in the joined `error` string. Never a bare 404 (the path does exist, just not here) and never an empty object that reads like a real answer.

Refused on the dedicated server:

- Needs a player character: `/player`, `/player/teleport`, `/player/look`, `/player/use`, `/player/swaphands`, `/spawn/hand`, `/inventory/arm`, `/inventory/move`.
- Needs the client input stack: `/input/key`, `/input/scroll`, `/input/mouse`, `/input/mouseposition`, `/input/releaseall`, `/input/clear`, `/input/enable`, `/diag/input`.
- Needs client-side UI: `/cursor/force`, `/modal`, `/modal/click`, `/modsettings`, `/modsettings/list`, `/screenshot`.
- Needs a client network role: `/connect`, `/disconnect`, `/diag/join`, `/host`, `/identity`.
- Conditional: `/inventory` only when no `player` / `clientId` / `humanId` selector is given; `/spawn/world` and `/spawn/structure` only when no `position` is given and there is no local player to derive one from.

Deliberately **not** refused, because they work: `/waitfor` polls GameState, `/colors` reads inspector data present on both, `/load` and `/newworld` drive console commands the server has, `/input/keymap` is a static read.

## The tier-1 save guards: both overrides removed

`/savepath?force=true` and `/host?requireIsolatedSavePath=false` are gone. Not defaulted differently, not warned about: removed.

The reason is that neither could ever have been a guard. Both endpoints are reachable by raw curl from anything on the machine, so the launcher cannot police them, and what stood in for a guard was a sentence in three separate documents saying "never pass this" plus, in one case, an error message that had to end with the words "never pass it". A rule that only works if the caller has already read it is not a guard, and the failure it protects against, a driven session writing worlds into the developer's own save folder, is not recoverable by retrying.

- **`/savepath`**: the tier-1 refusal is unconditional and fails closed (an unresolvable real user-data folder refuses rather than allows). The comparand is still computed locally from the Windows shell folder, never from `StationSaveUtils.DefaultPath`, which StationeersLaunchPad patches to return its own `SavePathOverride` and which inverted both answers when it was trusted.
- **`/host`**: the isolation requirement is unconditional and fails closed the same way.
- **Passing either removed parameter is a 400 that names the removal**, and nothing is changed or done. Silently ignoring a parameter the caller believed in is how a caller ends up trusting a result it should not. This is the same shape `/dlc/remove` already used for its nine grant-shaped fields.

## A world this plugin creates gets a station name

`POST /host` with a `world` id runs the console `new` command, and a world created that way has an **empty `XmlSaveLoad.CurrentStationName`**. Every save resolves through that name: the bare console `save`, `POST /save` with no name, and the game's own autosave. So a created world could not be saved by anything, and the launcher's ordered teardown found out at the one moment it cannot act on it, refusing to quit on top of an unsaved world and then losing it. It reproduced on every host check.

So `/host` names what it creates, once the world is up and hosting, which is the earliest point the save command is not refused (it is scoped `HostOrSinglePlayer` and takes only `Running` or `Paused`). The name is a first NAMED save, and `CurrentStationName` is read back afterwards to prove it took, because a save confirming is not the same claim as the name being assigned.

- `stationName` defaults to the world id. An empty string opts out deliberately, and the response says what that costs.
- A world **loaded** from a save already has its name and is never touched.
- Failure is a `warning` on a 200, never a refusal: hosting is what this endpoint asserts and it has already succeeded. `stationNameAssigned` is the field to read.
- There is no setter worth reaching for. The game assigns the name as a side effect of a named save, and going around it would name a world without writing it, which is worse than either end.

The dedicated server half has always had the same problem and only warned about it, in the launcher: `--new <Map>` prints that autosaves will fail with "Save Failed: Folder name is empty." until a first named save. That warning stands; the server's world is created through stdin, not through this endpoint.

## Scenarios: armed at boot AND callable over HTTP

Both paths exist. Roughly seven probes are genuinely load-ordered and no HTTP call can be timed against a world load: `sun-noon` (the light freeze has to be in force before anything measures light), `pgp-fresh-device-trace` (the construction events are the point), `pgp-umbilical-saveload-set` and `pgp-priority-deprioritization-probe` (both write before a save), and the multi-tick state machines `pgp-rearch-suite`, `ptp-standalone-suite`, `pgp-chain-fixture`, `pgp-mixedwire-fixture`, `pgp-2cycle-freeze`, `pgp-deprioritization-multilevel`. Those stay armed at boot. Everything else is a one-shot over settled state and is invoked directly.

`POST /scenario/run?id=<id>&ticks=<n>` runs one scenario for N **simulation ticks** (not frames: on the dedicated server the two clocks do not correspond, and `OnSimTick` even dedupes by `Time.frameCount`) and returns every `[ScenarioRunner]` line it produced, plus a `pass` / `fail` / `inconclusive` verdict read from the markers the bodies actually use. No log file is named by the caller.

### How the four arming traps are closed

The old mechanism (a config string plus a log grep) failed four times for four unrelated reasons. Each is addressed by construction, not by a warning:

| Trap | What happened | What stops it now |
|---|---|---|
| **The rig's state reset blanks the config value at session boundaries.** | An agent armed a probe, took the lock, started the server, and found it silently disarmed. The reset does this deliberately and correctly: a scenario left armed injects itself into an unrelated test's log. | The armed set is no longer in `BepInEx/config`. It lives in `<BepInEx root>/testrig/scenarios.armed`, which neither the config blanking nor the per-instance config re-copy touches. The config entry survives as a fallback, and if the two disagree the conflict is **reported** on `/scenarios` rather than silently resolved. `armedSource` names the winner. |
| **Arming required a restart, and a restart ends the session under test.** | The scenario string was read once at `OnPrefabsLoaded` and never re-read. | `Dispatcher.Tick` already re-read the field every tick; `POST /scenario/arm` now writes it, so a change takes effect on the next simulation tick. `persist=true` (the default) also writes the file so it survives the next boot, which is what the load-ordered probes need. |
| **A typo, a missing mod assembly, or an unreached settle counter produced one log line and then silence forever.** | All three are indistinguishable from "the probe ran and found nothing". | `GET /scenarios` reports the whole catalogue with `armed`, `dispatched`, `bootOrdered`, `requiresAssembly` and `blocked` per id, plus `unknownArmed` for anything that reached the switch unrecognised. A run that emits nothing returns a `note` naming exactly those three causes. |
| **The grep targeted the wrong file** (`data/server.log` carries Unity output; `[ScenarioRunner]` lines land in `install/BepInEx/LogOutput.log`). | Returns nothing, which again looks like a disarmed probe. | `/scenario/run` returns the lines in the response body. The caller never picks a file. |

A fifth cause is not fixable here and is reported instead: **if the simulation never ticks, nothing can fire.** `GameManager.DelayedStartupPause` parks a dedicated server's world five seconds after start with no client connected. `/scenarios` carries `ticksSeen`, and a `warning` naming that mechanism when it is 0.

The `[ScenarioRunner]` log prefix is kept unchanged, so every existing grep, analysis script and archived transcript still matches.

## Double-load refusal

The same DLL in both `install/BepInEx/plugins/` and `data/mods/Local_TestRig/` makes the BepInEx Chainloader and StationeersLaunchPad each load it: `Awake` fires twice, every Harmony patch registers twice, and side-effecting patches double. That trap produced `delta=10000` instead of `5000` during a battery-efficiency verification, and a log grep cannot see it because the output reads as entirely plausible. `ScenarioRunner` had one patch; this assembly has 20 patch classes, so doubling it would be considerably worse.

`Plugin.ClaimSingleLoad` records the load in an AppDomain slot and refuses the second `Awake` with a loud error naming both paths. The duplicate patches nothing and opens no socket.

## Deduplicated: one implementation, two front doors

`ScenarioRunner` and `ClientDriver` each carried an implementation of the same two operations.

- **`give-item` / `/inventory/give`.** Both resolved a Human, picked a hand, called `OnServer.Create<DynamicThing>(prefab, slot)`, applied `SharedDLCManager.CheckSharedAccess`, set quantity through `Stackable.SetQuantity` with a reflective fallback, dropped rather than destroyed on replace, and read the slot back. The route survives as the implementation; the request-file poller is now a front door that parses the file and calls it. The poller only existed because the dedicated server had no HTTP control plane, which is exactly what the merge fixes, so `POST /inventory/give` is the preferred route.
- **`config-set` / `/config/set`.** Same operation, and the two had drifted on a default: the server's poller defaulted `save=false`, the client's route defaulted `save=true`. **`save=true` wins, on both.** A write that is not persisted disappears on the next reload, producing a test that passed once and cannot be reproduced, and the failure is silent because the in-memory value was correct for the whole run. It also matches what a human editing the same entry through the StationeersLaunchPad panel gets. The old argument for `false` (persisting leaks test state into the next start) is real but already handled: both config trees are tier-3 rig state that the session reset restores. Pass `save=false` explicitly for the in-memory-only behaviour.

The poller's `mode=list` is **narrowed**: it now reports `/status.connectedClients` and points at `/inventory?player=<name>` for hands, rather than carrying a third implementation of a read the control plane already has.

## The roster: two sources, and the host is only in one

`/status.connectedClients` is the server-side answer to "did the joiner actually arrive", and the first real end-to-end run showed it empty on a listen host with a joiner demonstrably in world. Two separate faults, both here:

**1. The host is never in `NetworkBase.Clients`.** That list has exactly one writer, `NetworkBase.AddClient`, called only from `NetworkServer.VerifyConnection`, so it holds JOINERS. A listen host's own record is built by `NetworkServer.PopulateHostClient` and parked on `NetworkManager.HostClient` instead. The game unions the two everywhere it shows a roster: `NetworkManager.LogClientRosterToConsole` walks `Clients` then appends `HostClient`, and `NetworkManager.SerialisePlayerList` writes `HostClient` first under the guard `Client.Find(HostClient.ClientId) == null` and then every entry in `Clients`. This plugin now does the same, and skips a `HostClient` whose `ClientId` is 0, which is the game's own "not a real player" rule from `Client.DeserialiseClient` and is what a dedicated server has. The union makes the roster reconcile with `playersInGame`, which is `Clients.Count + (IsBatchMode ? 0 : 1)`.

**2. `connectionId` took the whole response down.** `Client.connectionId` is a `long` RakNet connection id, and the values are enormous: 189151461494586169 and 1044835390751713754 in one measured join. Emitted as a raw JSON number it does not fit the launcher's `ConnectedClient.ConnectionId`, which is `int?`, so `System.Text.Json` threw on the **whole** `/status` payload, `RigWire.Deserialize` returned null by design, and the launcher's roster poll concluded nothing had arrived. That is what produced three attempts of `roster did not grow (0 then 0)` against a host whose own console showed the client verified, served, ready and holding the session. The number is now emitted only when it round-trips `Int32`, with the exact value beside it as `connectionIdString`.

The proper fix for the second one is on the launcher side: `ConnectedClient.ConnectionId` should be a `string`, exactly as `clientId` already is and for the reason its own doc comment gives. That belongs to whoever owns `TestRig/src/`, and until it lands the plugin degrades one field rather than the whole endpoint.

Two smaller things went with them. The roster loop is indexed rather than `foreach`, because a `List<T>` enumerator throws "collection was modified" if a joiner is added or removed mid-read and the old `catch` turned that into `[]`, which reads as "nobody is connected". And when the read does fail, `/status` now carries `connectedClientsError` beside the empty array, so the two cases stop looking alike.

## Fixed on the way through

- **`FallbackPumpPatch` resolved the wrong namespace.** It tried `Assets.Scripts.Atmospherics.ElectricityManager` first. That namespace does not exist; the real type is `Assets.Scripts.Networks.ElectricityManager`, which `ScenarioRunner` compiled against directly. The wrong candidate only ever resolved through the bare-name fallback, which would have picked any type called `ElectricityManager` in any loaded assembly. `SimTickPatch` now tries the correct namespace first and keeps the bare name as the last resort.
- **Two Harmony patches on one method** (`ClientDriver`'s fallback pump and `ScenarioRunner`'s sim-tick pump) became one.
- **`harmony.PatchAll()` scope.** `ScenarioRunner` used the parameterless overload; the merged call names the assembly explicitly.
- **`AssemblyVersion`** comes from `Plugin.PluginVersion` rather than a hardcoded literal, which is how `ScenarioRunner`'s assembly version drifted from the version it logged.
- **Client-only patches are gated off headless** with `Plugin.ClientOnlyPatches` in their `Prepare()`: the input layer, the cursor gate, the window asserts, the cookie suppressor, the chain probe and the join trace. Every applied patch is one more thing that can throw inside `PatchAll` and take the ones after it down with it.

## The Contracts reference

`TestRig.Contracts` is referenced **compile-time only**: `Private=false`, `ExcludeAssets=runtime`, `SetTargetFramework=netstandard2.0`. Every path in the dispatch table and every status code comparison is a `const` from that assembly, and the compiler inlines a const at the use site, so renaming `Endpoints.ConsoleExec` breaks this build while the shipped output stays exactly one DLL. That matters because the netstandard2.0 face of Contracts carries a `PackageReference` on `System.Text.Json`, which drags six more assemblies behind it, and loading those into Unity's Mono buys nothing: this plugin writes its JSON by hand through `Transport/Json.cs` and always has.

Verify with `ls TestRig/dev-plugins/TestRig/TestRig/bin/Release/`: `TestRig.dll` and `About/`, nothing else.

**Gap to close:** the four scenario paths are in `TestRig.Contracts.Endpoints` now (`Scenarios`, `ScenarioRun`, `ScenarioArm`, `ScenarioDisarm`), but this dispatch table still carries its own literals (`Router.ScenariosPath` and friends). Switching those four cases to the Contracts consts is the one remaining place a rename there would not break this build.

## Ports

One assembly loads into both halves, and two processes cannot bind one TCP port. The config default therefore differs by host: **27700** in a game client (instances are handed 27700, 27701, ... by their manifests) and **27750** in the dedicated server, above the whole client band and below the game's own 28015/28016. Both are clear of Steam (27000-27050) and the Stationeers client (27015/27016). A manifest still overrides the config.

## The focus constraint

No code here may focus, raise or activate a window. `Window/NativeWindow.cs` remains the only file with a `DllImport`, and all seven imports are read-only queries or a handle release: `GetForegroundWindow`, `GetWindowThreadProcessId`, `GetCurrentThreadId`, `GetThreadDesktop`, `OpenInputDesktop` (with `DESKTOP_READOBJECTS`, the read-only right), `CloseDesktop`, `GetUserObjectInformationW`. `SwitchDesktop`, `SetForegroundWindow`, `ShowWindow`, `SetWindowPos`, `AttachThreadInput`, `BringWindowToTop`, `SetActiveWindow` and `SetThreadDesktop` are absent.

That is enforced by the build, not asserted in a comment. `TestRig.csproj` carries a `ForbidFocusStealingImports` target that scans every compiled source line, skipping comments (the names have to be writable in prose, which is what `NativeWindow.cs`'s header does), and fails the build on any hit. Verified by adding an `extern SwitchDesktop` to a scratch file: the build failed with the named error, and passed again once removed.

`TestRig/src/` has the same rule as an xUnit test over its own tree. That test does not reach this directory; this target is why it does not have to.

## Wiring, and what the two replaced trees are still for

- **The launcher deploys it.** The PowerShell launcher resolved a deploy target under `Mods/`, then `Plans/`, then `DedicatedServer/dev-plugins/` and `ClientRig/dev-plugins/`, and `TestRig/dev-plugins/` was in none of them; its client half additionally hardcoded `ClientDriver.sln` / `ClientDriver.dll`. `testrig.exe` searches `TestRig/dev-plugins/` ahead of both per-half folders and resolves the control plugin by name rather than hardcoding one. Both client instances and the dedicated server carry `TestRig.dll`.
- **Deploying it sweeps both predecessors, from both load paths.** `ControlPlugins.Names` is the set, and it is about NAMES rather than about which folder a source tree sits in. That distinction is the whole of the bug it had: `ScenarioRunner` lives under the dedicated server's own `dev-plugins/`, so every derived "is this the control plane" test answered false for it and nothing swept it. Measured 2026-08-15, the server was running `BepInEx/plugins/ScenarioRunner/` beside `data/mods/Local_TestRig/`: two scenario dispatchers and two sim-tick patches. The plugin's own duplicate refusal cannot catch that, because it recognises a second copy of ITSELF by GUID and a predecessor carries a different one.
- **The two replaced trees are kept, not wired.** `ClientRig/dev-plugins/ClientDriver/` and `DedicatedServer/dev-plugins/ScenarioRunner/` still build and are still deployable BY NAME, so a rig that has not built this one is not stranded, and deploying either sweeps this one in turn. Nothing resolves to them by default any more.
- **The rig's state reset keys off the old config file names** (`net.clientdriver.cfg`, `net.scenariorunner.cfg`) and blanks `net.scenariorunner.cfg`'s `Scenario` value. The merged plugin writes `net.sixfive7.testrig.cfg`. The reset's scenario-blanking rule becomes unnecessary rather than wrong: the armed set has moved out of the config file precisely so that rule cannot disarm a session, but the reset should be told about the new file name so it restores config the same way it does today.
- **Both halves have now been run.** All eight Spray Paint Plus checks ran against two instances carrying `TestRig.dll`, host plus joiner. The dedicated server was then started with it for the first time on 2026-08-15: `deploy TestRig --target server` swept `ScenarioRunner`, the server came up on a new Lunar world, reached `inWorld` through its own control plane, and `call --target server` answered `/status` and `/scenarios`, with exactly one load measured in its own log. That run is also where the pump-host frame turned out to be variable (above) and where the accept loop's stack-trace noise on an early client disconnect was found.
