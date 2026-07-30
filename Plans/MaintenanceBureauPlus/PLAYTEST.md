# Maintenance Bureau Plus Playtests

Implemented changes awaiting in-game confirmation. An agent records a playtest here, not in `TODO.md`, when it has changed code whose behavior can only be confirmed by running the game: single-player, a hosted multiplayer session, or the dedicated server under `DedicatedServer/`. This keeps `TODO.md` a list of work still to do, and gives one place to check whether a change already has a pending test before adding another.

Rules:
- Add an entry when code is implemented but its in-game behavior is unconfirmed. Write down everything a tester needs: what changed (commit if there is one), single-player vs multiplayer / dedicated-server, the save or world to set up, the exact in-game steps, what to watch (InspectorPlus request files, specific log lines, on-screen behavior, IC10 reads), and the expected result. Point at any staged `.work/<date>-<slug>/` request files or playbook.
- Check first. Before adding, scan the entries below so a change already covered by a pending test is not duplicated; extend the existing entry instead.
- Remove an entry when one of these happens: a run confirms it works; a run shows it broken (then add a fresh `TODO.md` item for the fix, or keep working on it now); or the player says the playtest is done. Entries are plain bullets, not checkboxes: like `TODO.md`, finished items are removed, not ticked off. Outcomes live in git history.

Note: this mod runs as a plain BepInEx plugin (not via StationeersLaunchPad). The deploy path is the local `BepInEx/plugins/MaintenanceBureauPlus/`, and the game must be closed to redeploy the DLL. Its log lines go to `LogOutput.log` at the BepInEx log path.

Two different surfaces get called "F3" and this note used to conflate them. StationeersLaunchPad's own log viewer (`slp logs`) does NOT show this mod's messages, because the mod is not LaunchPad-loaded and so has no LaunchPad child `Logger`. The game's own in-game console, the panel `KeyMap.ToggleConsole` (F3 by default) opens, DOES show them today: `LaunchPadLog.cs` resolves `Assets.Scripts.ConsoleWindow.Print` by reflection and mirrors every BepInEx log entry into it, which works whether or not StationeersLaunchPad is present. That bridge is playtest-only and is slated for removal before v1.0.0 (see `TODO.md`), so expect this to stop being true once it goes.

- **Console mirror line prefix changed (commit `4133cb09`, 2026-07-27).** `LaunchPadLog.ForwardToStationeersConsole` prefixed every mirrored line with `"[MBP] "`, which is a named violation of the no-abbreviations rule in this mod's own `CLAUDE.md`. It now emits `"[MaintenanceBureauPlus] "`. Only the prefix string changed; the severity-to-colour mapping and the `aged: true` choice are untouched, and the rest of that pass was comment corrections (the file header now states the flooding rate, the cross-thread hazard, and the `aged` semantics accurately).
  - Mode: single-player / local hosted game. Requires the model file present so the mod actually starts logging.
  - Steps: launch the game with the mod deployed, open the console (F3 by default), and read the mirrored lines. Every one should read `[MaintenanceBureauPlus] ...`; no line should read `[MBP] ...`.
  - Also confirm the inner tags are unaffected, so a watchdog line reads `[MaintenanceBureauPlus] [Watchdog #N] ...`. Those inner tags (`[DIAG]`, `[Watchdog #N]`, `[ApprovalEvent]`, `[LlmEngine]`, `[Bureau]`) are unchanged by this pass and are part of the diagnostic-removal sweep tracked in `TODO.md`, not of this change.
  - Note while you are looking at it: the flooding this bridge causes is the reason `TODO.md` marks it for removal before v1.0.0. If the console is unusable for typing during this check, that is the documented behavior, not a new regression.

- **InteractiveExecutor inference-latency (commit `2f3ba73`).** The commit rewrites in-cycle LLM turns to use `InteractiveExecutor` for KV-cache reuse: turn 1 sends the full ~2 kB system block once and caches it; turns 2+ send only the ~100-char delta. It also caps Dispose shutdown delay at 500 ms and skips native disposal. Not yet playtested.
  - Mode: single-player / local hosted game (not a Workshop or dedicated-server path).
  - Steps: restart Stationeers, load a save, type 3-4 chat messages spaced a few seconds apart.
  - Observe in `LogOutput.log`:
    - Turn 1: `[LlmEngine] Inference start: mode=interactive promptChars=~2200` and an `Inference done: N ms`.
    - Turns 2+: `promptChars` under 200, and `Inference done` ms should drop significantly (ideally an order of magnitude).
    - If turn 2 `promptChars` is still ~2200, the interactive cache is not engaging.
    - Also confirm `[LlmEngine] Dispose starting.` → `Dispose done.` within a second when the game exits. If shutdown is still slow, report the timing.
  - On result: if latency is still too slow for the UX, the prompt-trimming / model-swap follow-up is the next `TODO.md` item ("If interactive latency is still too slow").
