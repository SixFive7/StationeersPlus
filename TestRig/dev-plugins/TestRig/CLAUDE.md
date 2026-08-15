# TestRig plugin

The in-process half of the rig: **one BepInEx plugin that loads into both the game client and the dedicated server**, replacing `ClientDriver` and `ScenarioRunner`. Assumes `TestRig/CLAUDE.md` has been read. Full detail is in `README.md` next to this file.

```
dotnet build TestRig/dev-plugins/TestRig/TestRig.sln -c Release
```

**This is what both halves run.** `create` and `deploy` resolve it by name and sweep both predecessors out of both load paths, so `ClientRig/dev-plugins/ClientDriver/` and `DedicatedServer/dev-plugins/ScenarioRunner/` still build and are still deployable by name, but nothing resolves to them by default. A new name goes at the FRONT of `ControlPlugins.Names` in `TestRig/src/`, or it will not be swept: that set is about names, and leaving `ScenarioRunner` out of it is what let the dedicated server run two scenario dispatchers at once.

## Eight things to know before editing

**1. There is one host detector. Do not add a second.** `HostProfile` decides which process this is, at load, from `GameManager.IsBatchMode` with the command line as the provisional answer. Every other file asks; nothing re-derives. Read `/status.host` to see it, alongside the pump state.

**2. The drain has four hooks and needs all four. Do not remove one as redundant.** Measured on 0.2.6428.27798, and independently re-measured under the research conflict protocol:

- **The pump host's own `MonoBehaviour.Update` covers BOOT**, at ~25 Hz from the first scene load. `GameManager.Update` cannot: it runs at **0.11-0.16 per second until `GameState.Running`**, so relying on it alone leaves the control plane nearly frozen for the whole 80-90 s boot, which is exactly when a caller polls for readiness.
- **A UniTask player-loop loop covers everything BEFORE the first scene load**, which on the dedicated server is frames 0 to somewhere around 1600-1850 (measured 1834 and 1635; it is a variable, not a constant). It retires the moment the pump host exists, so on a client it runs for a handful of frames. Do not delete it as redundant with the pump host: headless it is the only thing running at all in that window, and `/status.driver.pumpBootLoopDrains` is how a run proves it. See point 3.
- **`Assets.Scripts.GameManager.Update` is the steady-state primary, on both builds.** Thread 1, ~24 Hz, unaffected by pause (287 s with no client and force-unpause off, tick count at 0 throughout).
- **`ImGuiManager.LateUpdate` is client-only, and not because it is never called, but because the method is absent from the dedicated server assembly** (1 method and 0 fields there against 19 and 17 on the client). Do not move anything back onto that hook alone. `PerFrameTicks` exists because `Epoch`, `JoinTrace` and `WindowMode` used to ride it and were therefore dead headless.

The game's `UnityMainThreadDispatcher` is the backstop and also works while paused, but it drains from `ManagerUpdate`, whose sole caller is `GameManager.Update`, so it can never be earlier. And **`ElectricityManager.ElectricityTick` is not a pump and must never become one**: its postfix thread was measured off the main thread 115 times out of 115 (id 40 against main id 1) and rotated across nine ids in the earlier runs. `MainThreadPump.Drain` refuses to run work off the captured main thread; that check is load bearing, executing Unity work on that worker crashes the engine native side intermittently, and it is what caught a `Research/Patterns/MainThreadDispatcher.md` recovery pattern that recommended exactly that mistake. `ElectricityTick` is the simulation-liveness signal and the scenario pump, nothing else.

**3. The pump host is created at the FIRST `SceneManager.sceneLoaded` callback. Never in `Awake`.** A plugin's own `Update` never fires at all otherwise: the component and everything it creates in `Awake` are destroyed **135-219 ms later at `Time.frameCount == 0`**, before the first scene loads, with `Start()` never reached and zero `Update` calls received. `DontDestroyOnLoad` does not save it: the call appears to succeed (scene `DontDestroyOnLoad`, handle -12) but does not bind, because no scene is loaded yet. Created at the first `sceneLoaded` it survives indefinitely and misses nothing; created at the later Base scene load it appears at frame 1925 and loses everything before it. Static state is what survives a component's death, which is why the subscription itself is registered in `Awake` and why the listener is owned by a static.

**And that first callback is frame 0 on a client but over a thousand frames in on the dedicated server.** Headless there is no splash or menu scene, so the first scene load is the mod-content load; the server's own log says `pump host created at frame 1834 (scene load 1)`, with no line at all between it and "ready on http://127.0.0.1:27750/". **That frame index is a sample, not a constant: 1834 in the instrumented run and 1635 in the first real one on the same build.** Never assert on it. Nothing covered that gap before: the `GameManager.Update` postfix calls the same idempotent creator, and the host was still created from scene load 1, which proves that postfix fired zero times; `UnityMainThreadDispatcher` drains from `ManagerUpdate`, whose sole caller is that postfix; `ImGuiManager.LateUpdate` is absent from the server assembly. Every `Main(...)` route therefore 504'd for the whole window. **Do not expect `pumpHostCreatedAtFrame` to be 0 on the server, and do not "fix" it by creating the host earlier** (see the destruction above). The UniTask loop in point 2 is what covers it, because `PlayerLoopHelper.Init` is a `RuntimeInitializeOnLoadMethod(AfterAssembliesLoaded)` and so is installed before any scene and before this plugin's `Awake`, while nothing in the game assembly is running yet.

**Do not use `FixedUpdate` for anything the control plane depends on.** It is gated on `Time.timeScale`, not `IsGamePaused`, and the two disagree headless: `GameManager.StartGame()` sets `Time.timeScale = 1f` while `IsGamePaused` is already true, so `DelayedStartupPause`'s `SetGamePause(true)` hits the `if (IsGamePaused != pauseGame)` guard and never drops the scale. A real `SetGamePause(true)` transition stops `FixedUpdate` dead. No log line either way.

**4. Scenario bodies run on the tick thread, deliberately.** ~85 of them were written against that contract. `SimTickPatch` records liveness and then calls `Dispatcher.OnSimTick()` on the same thread. Do not marshal scenario dispatch onto the main thread to "fix" it; that changes what every one of them measures.

**5. A dedicated server with no client attached runs ZERO simulation ticks**, not "a few then a pause". Measured: 287 s at tick 0, `SetGamePause` twice, both before any tick. Never write a detector that waits for a first tick and then a park. The control plane is unaffected, because the main thread keeps running throughout.

**6. The tier-1 save guards have no overrides and must not regain any.** `/savepath` refuses a path inside the developer's user-data folder, `/host` refuses a non-isolated save root, both unconditionally and both fail closed. `force` and `requireIsolatedSavePath` were removed and passing either is a 400. These endpoints are reachable by raw curl, so a documented rule is not a guard.

**7. No code here may focus, raise or activate a window.** The build enforces it: `TestRig.csproj` has a `ForbidFocusStealingImports` target that fails on `SwitchDesktop`, `SetForegroundWindow`, `ShowWindow`, `SetWindowPos`, `AttachThreadInput`, `BringWindowToTop`, `SetActiveWindow` or `SetThreadDesktop` in any non-comment line. `Window/NativeWindow.cs` is the only file with a `DllImport` and all of its imports are read-only queries.

**8. The server-side roster has TWO sources, and an id on the wire is a string for a reason.** `NetworkBase.Clients` holds joiners only: its sole writer is `NetworkBase.AddClient`, called only from `NetworkServer.VerifyConnection`. A listen host's own record lives on `NetworkManager.HostClient`, built by `NetworkServer.PopulateHostClient`, and the game unions the two everywhere it presents a roster (`LogClientRosterToConsole`, `SerialisePlayerList`). Reading only the list is what made a host with a joiner in world report an empty roster. A `HostClient` whose `ClientId` is 0 is skipped, which is the game's own rule in `Client.DeserialiseClient` and is what a dedicated server has; that also makes the roster length equal `playersInGame` on both halves.

The id rule is not cosmetic. `Client.connectionId` is a `long` RakNet id in the 10^17 range, and emitting it as a raw JSON number made `System.Text.Json` throw on the **whole** `/status` payload on the launcher side, where the reader returns null on a parse failure. One oversized field made the entire endpoint unreadable, and the harness concluded the joiner had never arrived. **Never emit a game id as a bare JSON number without checking what the launcher's Contracts record types it as.**

## Layout

| Path | What |
|---|---|
| `Plugin.cs` | bootstrap, config, patch application, the three pump patch classes and `PerFrameTicks` |
| `Host/HostProfile.cs` | which process this is, and its capabilities |
| `Host/HostGuard.cs` | refusals for endpoints the dedicated server cannot serve |
| `Transport/` | the `TcpListener`, the JSON writer and tolerant reader, the pump |
| `Routes/` | `Router.cs` is the dispatch table; `Routes.*.cs` are the routes, one file per domain |
| `Observe/`, `Instance/`, `Input/`, `Window/` | carried across from `ClientDriver`, unchanged except where noted in `README.md` |
| `Scenarios/` | carried across from `ScenarioRunner`; `ScenarioHost.cs` and `Dispatcher.Control.cs` are new |

## Adding an endpoint

1. Add the path constant to `TestRig.Contracts.Endpoints` (in `TestRig/src/`, which another agent owns) and use it in `Router.Handle`. The contracts reference is compile-time only, so paths are `const` and cost nothing at runtime; a rename there must break this build.
2. Add a line to `Routes/Help.cs`. A route that is not in `/help` is a route nobody finds.
3. Decide whether the dedicated server can serve it. If not, add a `HostGuard` rule with all three parts: what the verb needs, why this host cannot provide it, and a command that works. A refusal missing any of the three is not a refusal, it is a dead end.
4. Wrap in `Main(...)` only if the body touches Unity and finishes inside 20 seconds. Anything that waits on the game (a join, a load, a save, a scenario) polls instead and answers 504 or 409 on its own terms.

## Adding a scenario

1. Add the case to `Dispatcher.TickOne` and the body as a `Dispatcher.*.cs` partial, exactly as before.
2. **Add the id to `ScenarioHost.Catalogue`** with its tick budget, required mod assembly, and whether it is load-ordered. An id missing from the catalogue cannot be armed or run over HTTP and shows up on `/scenarios` as `unknownArmed` after its first tick.
3. Mark `bootOrdered: true` only if it genuinely cannot be started by an HTTP call: it has to be running before or during a world load. Everything else is invocable and should be.
