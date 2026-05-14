# StationeersPlus

Mods for the game Stationeers, developed by SixFive7.

## Released mods

| Mod | Description | Source | Workshop |
|---|---|---|---|
| Spray Paint Plus | Combines color cycling, network painting, and infinite spray paint into one multiplayer-safe mod. | [GitHub](Mods/SprayPaintPlus) | [Steam](https://steamcommunity.com/sharedfiles/filedetails/?id=3702940349) |
| Power Transmitter Plus | Adds a visible beam, scrolling pulses, a configurable distance cost, and new logic readouts to the Microwave Power Transmitter. | [GitHub](Mods/PowerTransmitterPlus) | [Steam](https://steamcommunity.com/sharedfiles/filedetails/?id=3707677512) |

## Directory structure

- `Mods/` contains released mods. Each subdirectory is a standalone mod that ships to the Steam Workshop, with its own README, RESEARCH notes, source project, and `About/` folder for Workshop metadata. Each mod's version history is captured in `About/About.xml` `<ChangeLog>`.
- `Plans/` contains mods in progress and early-stage prototypes that are not yet released. Subdirectories here follow the same shape as released mods once they mature, but are not tagged or published.
- `Patterns/` contains shared conventions, documentation, and code that more than one mod needs to agree on. Currently holds `Patterns/Logic/` (centralised `LogicType` numbering catalogue + a shared `LogicTypeNumbers.cs` linked into every mod that registers a custom `LogicType`). Future shared patterns land here as separate subfolders.
- `Research/` contains the central knowledge base for Stationeers game internals (decompiled-class notes, system behaviours, protocols, workflows). Curated per `Research/WORKFLOW.md`; see `Research/CLAUDE.md` for the structural rules.
- `tools/` contains repository-wide utility scripts that serve more than one mod.
- `DedicatedServer/` holds a self-contained Stationeers Dedicated Server install used for multiplayer testing. Everything inside is gitignored except `DedicatedServer/CLAUDE.md`, the operating manual.
- `Mods/Template/` is the seed scaffold for creating new mods. Copy it, rename to the new mod's name, and edit.

Local build configuration is documented in `CLAUDE.md` (shared conventions) and `DEV.md.template` (developer-specific paths; copy to `DEV.md` and fill in).

## Reporting Issues

Please open an issue at https://github.com/SixFive7/StationeersPlus/issues. Include the mod name in the title so it can be triaged.

## License

Apache License 2.0. See [`LICENSE`](LICENSE).
