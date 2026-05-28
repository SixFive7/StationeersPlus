# Power Grid Plus Playtests

Implemented changes awaiting in-game confirmation. An agent records a playtest here, not in `TODO.md`, when it has changed code whose behavior can only be confirmed by running the game: single-player, a hosted multiplayer session, or the dedicated server under `DedicatedServer/`. This keeps `TODO.md` a list of work still to do, and gives one place to check whether a change already has a pending test before adding another.

Rules:
- Add an entry when code is implemented but its in-game behavior is unconfirmed. Write down everything a tester needs: what changed (commit if there is one), single-player vs multiplayer / dedicated-server, the save or world to set up, the exact in-game steps, what to watch (InspectorPlus request files, specific log lines, on-screen behavior, IC10 reads), and the expected result. Point at any staged `.work/<date>-<slug>/` request files or playbook.
- Check first. Before adding, scan the entries below so a change already covered by a pending test is not duplicated; extend the existing entry instead.
- Remove an entry when one of these happens: a run confirms it works; a run shows it broken (then add a fresh `TODO.md` item for the fix, or keep working on it now); or the player says the playtest is done. Entries are plain bullets, not checkboxes: like `TODO.md`, finished items are removed, not ticked off. Outcomes live in git history.

This mod has never been released (still 0.1.0). It compiles and loads cleanly on the dedicated server (all Harmony patches apply, no errors), but its runtime behavior is entirely unplaytested. The entries below must pass before the first release cut.

- **Power simulation baseline (single-player).** A world with batteries, transformers, APCs, and a multi-provider / multi-consumer network. Confirm power distributes, cable burns fire on overload and mixed-tier, voltage-tier enforcement works, burn-reason tooltips appear on wreckage.
  - Mode: single-player.

- **Deterministic merge sort, single-player.** Cut + place a cable in a 50+ cable network. No console errors; the surviving network is the lowest-`ReferenceId` of the two merged (the sort runs unconditionally, so single-player always picks the lower id).
  - Mode: single-player.

- **Deterministic merge sort, multiplayer (the load-bearing test).** Host + one client, two cable networks of different sizes. Client cuts to split, then places a cable bridging them. Both peers' tablet must show the SAME surviving `CableNetworkId`. Repeat with the host bridging. This is the direct verification that the class-B fix lands.
  - Mode: multiplayer, host + one client.
  - Refs: commit 14946c5; `Research/Patterns/MultiplayerStateMutation.md`.

- **Late-join client.** Join a save with cable networks; client reads the same `CableNetworkId` per cable as the host.
  - Mode: multiplayer, late-join.

- **Logic-passthrough device-list refresh + client sync (multiplayer).** The device-list refresh cascade, the per-device mode + settings client sync (`PassthroughModeMessage` / `PassthroughSettingsMessage` / `IJoinSuffixSerializer`), and the client-side dish-link back-reference mirror are implemented but unplaytested in real multiplayer. Host-side parts are dedi + InspectorPlus automatable (request files + playbook staged under `.work/2026-05-28-pgp-passthrough-refresh/`); the client-side parts need a second player.
  - Mode: multiplayer, host + one client (host-side halves automatable on the dedicated server).
  - (a) Flipping `LogicPassthroughMode` on a transformer / battery / dish refreshes the motherboard device dropdown on BOTH peers with no replug.
  - (b) A client tablet write (routed through the host) still applies and refreshes.
  - (c) Toggling an `Enable*LogicPassthrough` server setting live refreshes all motherboards and the client adopts the host value.
  - (d) A joining client sees the correct merged lists immediately (join suffix).
  - (e) A dish TX/RX link / unlink refreshes the dropdowns, and the RX-cable side sees the TX-cable side on the client (the back-ref mirror).

- **In-game behaviour test (NEW-1 + NEW-3 simulation half).** Remaining sub-checks for the three-tier voltage feature; all test already-implemented behavior (the NEW-2 multiplier sweep and the NEW-3 device-tier rule are already in `verified.md`).
  - NEW-1: an all-super-heavy-cable network at >>>50 kW throughput doesn't burn cables (off via config -> super-heavy starts burning at its rating). Carve-out at `EnableUnlimitedSuperHeavyCables=true` already verified via ScenarioRunner's `pgp-cable-burn-probe` (see `verified.md`: `TestBurnCable(10000, 10000)` returns null for every super-heavy network). The off-toggle half remains: invert the config, re-run `pgp-cable-burn-probe` (or add a `pgp-superheavy-carveout-probe` scenario that calls `TestBurnCable(2000000, 2000000)` against every network so burnChance=3 always fires), confirm super-heavy networks now return a Cable.
  - NEW-3 cable-tier rule: place a normal cable adjacent to an existing heavy-cable network containing a turned-on generator + load. The boundary normal cable burns on the next tick. Wreckage tooltip: "Burned: Wrong voltage -- normal cable was bridging into a different cable tier". Force the scenario via `tools/save-edit/` (drop a single normal cable adjacent to an active heavy network in a Luna copy), load it, confirm the burn log line + a fresh `CableRuptured` via InspectorPlus.
  - NEW-3 cursor reject (cable-on-cable): hold a normal-cable coil, hover next to an existing heavy cable -> red ghost + "Wrong voltage -- this cable's tier doesn't match the adjacent cable network. Use a transformer." Client UI; needs a developer with the game client (playbook at `.work/2026-05-22-pgp-verify-task2/playbook.md`).
  - NEW-3 cursor reject (cable-on-device): hold a normal-cable coil, hover next to an existing turret/RTG -> red ghost + "Wrong voltage -- [device] doesn't accept normal cable." Client UI; see playbook.
  - Device placement is never cursor-blocked: hold a solar-panel kit, hover over a normal cable -> green ghost; place; cable burns next tick. Client UI; see playbook.
  - Burn-reason tooltips: hover wreckages from each burn category (sustained overload, mixed-tier bridge, adjacent misplaced device) and confirm the "Burned: ..." text in the wreckage's extended hover tooltip. Client UI; see playbook. (Server-side `BurnReasonRegistry.Attach` path is exercised by the device-tier burn already in `verified.md`.)
  - Power-flow gate: a network with a misplaced device but ALL devices off (`OnOff = false`) destroys no cables; turning a source + a load on fires the burn on the next tick. Needs a constructed scenario via `tools/save-edit/`; not observable on the Luna load alone.
  - Loaded save with pre-existing tier mix: construct the scenario via `tools/save-edit/` (single normal cable bridging two heavy networks with a running generator + load) and re-load to verify the boundary burn (D11 retroactive enforcement only fires on a mixed-tier network with active power flow; a clean Luna load produces zero burns).
  - InspectorPlus request files staged at `.work/2026-05-22-pgp-verify-task2/requests/pgp-task2-*.json` (7 files); developer playbook for client-side parts at `.work/2026-05-22-pgp-verify-task2/playbook.md`.

- **`Cable.ConnectedDevices()` on the cursor ghost.** The Round-3 cable-on-device cursor reject calls `__instance.ConnectedDevices()` on a placement-preview cable. The Research page (`Research/Patterns/CursorAdjacencyLookup.md`) says this should work the same way `ConnectedCables(NetworkType.Power)` does on a cursor; verify in-game by hovering a normal cable next to a generator and seeing the red message. The headless half is done (code review at `.work/2026-05-22-pgp-verify-task4/code-review.md`, developer playbook at `.work/2026-05-22-pgp-verify-task4/playbook.md`, mod built Release + deployed to the dedicated server). Cursor logic is client-side, so the in-game red-message confirmation is developer-driven; follow the playbook.
  - Mode: client UI (single-player or any session with the game client).

- **Heavy-cable device-list draw sweep.** The high-draw machine whitelist in `VoltageTier.IsHighDrawMachine` (`CarbonSequester`, `FurnaceBase`, `ArcFurnace`, `Centrifuge`, `Recycler`, `IceCrusher`, `HydraulicPipeBender`, `DeepMiner`) was assembled from community consensus; only the first three are verified-by-decompile to draw >5 kW. Confirm which of the rest actually exceed 5 kW.
  - Mode: single-player, high-load save.
  - Observe: InspectorPlus sweep `types=[Device], fields=[DisplayName, UsedPower, MaxUsedPower, GetUsedPower(network)]`.
  - Follow-up (stays in `TODO.md`): based on the measured draws, drop or extend the whitelist in `VoltageTier.IsHighDrawMachine`.
