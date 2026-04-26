# {{Mod Display Name}}

![{{Mod Display Name}}]({{ModCodeName}}/About/Preview.png)

{{Tagline: one sentence describing the mod in its current feature-complete state. Identical wording (allowing for markup differences) to the GitHub repo description, the About.xml <Description> opening line, and the <InGameDescription> subtitle.}}

Full multiplayer compatibility. Safe to remove from existing savegames.

> **WARNING:** This is a StationeersLaunchPad mod. It requires [BepInEx](https://docs.bepinex.dev/) and [StationeersLaunchPad](https://github.com/StationeersLaunchPad/StationeersLaunchPad) to be installed.

{{Optional credits paragraph: when the mod builds on prior work, name the predecessors and what they contributed, with Steam Workshop links. Delete this paragraph entirely if the mod is original.}}

## Installation

1. Copy `{{ModCodeName}}.dll` and the `About/` folder into your Stationeers local mods directory
2. Restart the game

## Features

### {{Feature 1 Title}}
{{One or two sentences describing what this feature does and why a player would want it. Keep the tone matter-of-fact.}}

### {{Feature 2 Title}}
{{Repeat for each feature. Use tables, bullet lists, or code blocks as needed. See SprayPaintPlus and PowerTransmitterPlus READMEs for examples of settings tables, IC10 code samples, and distance-cost tables.}}

### Settings

All features are configurable via the mod settings panel.

**Client settings** (personal preference, each player sets independently):

| Setting | Default | Description |
|---|---|---|
| {{Setting name}} | {{Default}} | {{Description}} |

**Server settings** (the server's value controls gameplay for everyone):

| Setting | Default | Description |
|---|---|---|
| {{Setting name}} | {{Default}} | {{Description}} |

## Compatibility

**Requires:** BepInEx + StationeersLaunchPad

**Incompatible with** {{optional; delete the block if there are no known incompatibilities}} (detected at startup; the mod refuses to load if any are found):
- [{{Mod name}}]({{Workshop URL}}) by {{Author}}

**Redundant** {{optional; delete if not applicable}} (not detected, but pointless to run alongside this mod; disable to avoid confusion):
- [{{Mod name}}]({{Workshop URL}}) by {{Author}}

**All players** on a server must have {{Mod Display Name}} installed. Matching mod versions are enforced during the connection handshake automatically.

**Dedicated servers** need the same BepInEx + StationeersLaunchPad + {{ModCodeName}} setup installed server-side.

## Reporting Issues

If you run into a bug or something behaves unexpectedly, please open an issue on [GitHub](https://github.com/SixFive7/StationeersPlus/issues). Please include the mod name in the title so reports can be triaged. Steam comment notifications don't always come through, so GitHub is the reliable way to make sure a report is seen.

## Changelog

Version history lives in [`{{ModCodeName}}/About/About.xml`]({{ModCodeName}}/About/About.xml) under `<ChangeLog>` and is published on the [Steam Workshop Change Notes tab](https://steamcommunity.com/sharedfiles/filedetails/changelog/{{WorkshopHandle}}) with every release.

## Credits

{{Optional; when building on others' work.}}

- **{{Author}}**: Created [{{Mod name}}]({{URL}}). {{What they contributed.}}

## License

Apache License 2.0. See [LICENSE](../../LICENSE) for the full text and [NOTICE](../../NOTICE) for attribution.
