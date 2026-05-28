# Maintenance Bureau Plus Playtests

Implemented changes awaiting in-game confirmation. An agent records a playtest here, not in `TODO.md`, when it has changed code whose behavior can only be confirmed by running the game: single-player, a hosted multiplayer session, or the dedicated server under `DedicatedServer/`. This keeps `TODO.md` a list of work still to do, and gives one place to check whether a change already has a pending test before adding another.

Rules:
- Add an entry when code is implemented but its in-game behavior is unconfirmed. Write down everything a tester needs: what changed (commit if there is one), single-player vs multiplayer / dedicated-server, the save or world to set up, the exact in-game steps, what to watch (InspectorPlus request files, specific log lines, on-screen behavior, IC10 reads), and the expected result. Point at any staged `.work/<date>-<slug>/` request files or playbook.
- Check first. Before adding, scan the entries below so a change already covered by a pending test is not duplicated; extend the existing entry instead.
- Remove an entry when one of these happens: a run confirms it works; a run shows it broken (then add a fresh `TODO.md` item for the fix, or keep working on it now); or the player says the playtest is done. Entries are plain bullets, not checkboxes: like `TODO.md`, finished items are removed, not ticked off. Outcomes live in git history.

Note: this mod runs as a plain BepInEx plugin (not via StationeersLaunchPad). The deploy path is the local `BepInEx/plugins/MaintenanceBureauPlus/`, and the game must be closed to redeploy the DLL. Its log lines go to `LogOutput.log` at the BepInEx log path; the in-game F3 viewer does NOT show this mod's messages.

- **InteractiveExecutor inference-latency (commit `2f3ba73`).** The commit rewrites in-cycle LLM turns to use `InteractiveExecutor` for KV-cache reuse: turn 1 sends the full ~2 kB system block once and caches it; turns 2+ send only the ~100-char delta. It also caps Dispose shutdown delay at 500 ms and skips native disposal. Not yet playtested.
  - Mode: single-player / local hosted game (not a Workshop or dedicated-server path).
  - Steps: restart Stationeers, load a save, type 3-4 chat messages spaced a few seconds apart.
  - Observe in `LogOutput.log`:
    - Turn 1: `[LlmEngine] Inference start: mode=interactive promptChars=~2200` and an `Inference done: N ms`.
    - Turns 2+: `promptChars` under 200, and `Inference done` ms should drop significantly (ideally an order of magnitude).
    - If turn 2 `promptChars` is still ~2200, the interactive cache is not engaging.
    - Also confirm `[LlmEngine] Dispose starting.` → `Dispose done.` within a second when the game exits. If shutdown is still slow, report the timing.
  - On result: if latency is still too slow for the UX, the prompt-trimming / model-swap follow-up is the next `TODO.md` item ("If interactive latency is still too slow").
