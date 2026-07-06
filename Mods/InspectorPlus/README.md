# Inspector Plus

![Inspector Plus](InspectorPlus/About/Preview.png)

Developer tool that dumps live Stationeers runtime state to JSON on demand for mod development.

Full multiplayer compatibility. Safe to remove from existing savegames.

> **WARNING:** This is a StationeersLaunchPad mod. It requires [BepInEx](https://docs.bepinex.dev/) and [StationeersLaunchPad](https://github.com/StationeersLaunchPad/StationeersLaunchPad) to be installed.

Not intended for end-user gameplay. Subscribe only if you are writing or debugging a Stationeers mod. The plugin reads field and property values from live scene objects and writes them to JSON snapshots on disk, so a developer can diff runtime state across frames or game events without adding one-off logging.

## Installation

[Subscribe on the Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3730349036), or install manually:

1. Copy `InspectorPlus.dll` and the `About/` folder into your Stationeers local mods directory
2. Restart the game

The `BepInEx/inspector/requests/` and `BepInEx/inspector/snapshots/` folders are created automatically on first load.

## Features

### On-Demand Snapshots via Request JSON
Drop a request JSON into `BepInEx/inspector/requests/` specifying types and fields to inspect. The plugin writes a snapshot into `BepInEx/inspector/snapshots/snapshot_<timestamp>.json` and deletes the request. This is the programmatic path: drop the file, get the result.

### F8 Full-Scene Dump
Press F8 in-game to dump every MonoBehaviour in the current scene to the same snapshots folder in one shot. Useful for the first-pass "what is in the scene right now" survey.

### Field, Property, and Unity Position Capture
Dumps private and public fields, computed properties, and Unity Transform positions. Walks nested objects to a configurable depth, with cycle detection.

### Headless Server Operation
Opt-in: the Force Unpause Without Client setting keeps a headless dedicated server simulating with no client connected, so automated tooling can drop request files and read snapshots without a player joining. The game schedules its own pause a few seconds after world start when no client is connected; while the setting is on that startup pause is skipped, and a watchdog re-asserts the unpause and logs tick state every few seconds. Never affects a client or single-player.

## Settings

Settings appear in the StationeersLaunchPad in-game mod settings panel.

| Section | Setting | Default | Effect |
|---|---|---|---|
| Client - Snapshots | Snapshot Key | F8 | Key that writes a full scene snapshot to `BepInEx/inspector/snapshots/`. |
| Server - Headless | Force Unpause Without Client | false | Headless dedicated servers only: keep the simulation running with no client connected so request-file snapshots can be captured by automated tooling. Skips the game's startup pause and re-asserts the unpause via a watchdog. |
| Server - Headless | Enable Pause Trace Logging | false | Dump pause and unpause call-site stack traces to the log to diagnose what re-paused a headless world. Diagnostic for mod developers; noisy around autosaves. |

## Compatibility

**Requires:** BepInEx + StationeersLaunchPad

**Single-player and multiplayer.** Works on any instance where it is installed. This is a developer tool; do not require it on a shared server.

## Reporting Issues

If you run into a bug or something behaves unexpectedly, please open an issue on [GitHub](https://github.com/SixFive7/StationeersPlus/issues). Please include the mod name in the title so reports can be triaged. Steam comment notifications don't always come through, so GitHub is the reliable way to make sure a report is seen.

## Changelog

Full version history is in [CHANGELOG.md](CHANGELOG.md). Each release also appears on the [Steam Workshop Change Notes tab](https://steamcommunity.com/sharedfiles/filedetails/changelog/3730349036).

## License

Apache License 2.0. See [LICENSE](../../LICENSE) for the full text and [NOTICE](../../NOTICE) for attribution.
