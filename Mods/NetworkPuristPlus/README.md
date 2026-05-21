# Network Purist Plus

![Network Purist Plus](NetworkPuristPlus/About/Preview.png)

Removes the long (3-, 5-, and 10-segment) straight pipe, chute, and super-heavy cable variants and aligns straight cables to one consistent orientation; existing worlds are converted when a save loads.

Full multiplayer compatibility. Safe to add to an existing savegame.

> **WARNING:** This is a StationeersLaunchPad mod. It requires [BepInEx](https://docs.bepinex.dev/) and [StationeersLaunchPad](https://github.com/StationeersLaunchPad/StationeersLaunchPad) to be installed.

## Installation

1. Copy `NetworkPuristPlus.dll` and the `About/` folder into your Stationeers local mods directory.
2. Restart the game.

## Features

### Long pieces removed everywhere

Stationeers ships "long" straight building pieces: 3-, 5-, and 10-segment variants you build (or merge existing runs into) instead of placing single tiles. This mod takes them out of the game's menus:

| Family | Long variants removed |
|---|---|
| Gas pipe | `StructurePipeStraight3` / `5` / `10` |
| Insulated gas pipe | `StructureInsulatedPipeStraight3` / `5` / `10` |
| Liquid pipe | `StructurePipeLiquidStraight3` / `5` / `10` |
| Insulated liquid pipe | `StructureInsulatedPipeLiquidStraight3` / `5` / `10` |
| Chute | `StructureChuteStraight3` / `5` / `10` |
| Super-heavy cable | `StructureCableSuperHeavyStraight3` / `5` / `10` (the only cable tier with long variants) |

They are stripped from the build-kit mouse wheel and hidden from the Stationpedia (page list and search). The merge-with-a-tool action can no longer produce a long piece either. You build pipes, chutes, and cables one tile at a time, the way it worked before the merge feature was added. The long-variant prefabs themselves stay registered so old saves still load (see below).

### Existing long runs rebuilt on load

When a save loads, every already-placed long pipe, chute, or cable run is rebuilt from the equivalent single-tile pieces at the same cells, with the same rotation and custom colour, then the long piece is removed. Networks stay connected, but rebuilt pipe runs start empty -- the gas inside a long pipe run is not preserved; re-pressurize them. This is a one-time conversion the first time you load a world after installing the mod (and again for any world a long piece sneaks into, for example from another mod). It runs on the host / single-player only; clients receive the rebuilt world through the normal sync.

A long piece that turns up some other way -- a blueprint paste, another mod's `upgrade` command, the creative menu -- is converted to single tiles on the next world load. The long variants are removed from the build menus, so there is no way to build one directly.

### Cable alignment

A straight cable is a tube with a coloured band along the side, but the game lets a straight cable be placed at any of four "rolls" about its run direction (it treats them as 24 box orientations with no symmetry, unlike straight pipes). So a cable placed one way next to a cable placed another way shows the band misaligned -- you see it most on super-heavy cables, and it crops up easily when zoop-built and hand-built cables meet, or after pressing the rotate key. (It is purely cosmetic: the game does not care about a straight cable's roll for connectivity, networks or anything else.)

This mod picks one orientation per run axis -- the same set the game uses for straight pipes -- and applies it:

- **On load:** every already-placed straight cable, all tiers, is realigned. Host / single-player only; clients get the corrected rotations through the normal sync.
- **As you build:** a freshly built straight cable is set to the canonical orientation before it is created, whatever the cursor showed, so in multiplayer it shows aligned on every player's screen right away. Building normally, rotating the cursor, [ZoopMod](https://steamcommunity.com/sharedfiles/filedetails/?id=3310094883) drag-builds, and the merge-with-tool action all produce an aligned cable as it is placed; a [BlueprintMod](https://steamcommunity.com/sharedfiles/filedetails/?id=3672138641) paste is realigned on the next world load instead. The rotate key on a cable becomes preview-only as a result.
- **Build cursor:** the single-tile straight pipe, cable and chute in each kit is given the "straight" connection type, so the cursor and the smart-rotate cycle behave consistently, and the merge-with-tool action keeps working now that the long straight variants are gone from the kits. (Without that last part, merging two collinear pipes or cables with the exit tool throws an error -- this also fixes that latent v1.0 issue.)

The chosen orientation per run axis is the one that mates with the most corner pieces, but it does **not** fully eliminate the band-seam at corner cables. Corner cables have a fixed band orientation that this mod does not touch (re-rolling a corner would change connectivity, not just looks -- a corner's open ends are off the run axis), and no single per-axis straight-cable orientation mates with every corner: a corner's band-exit face depends on the corner's own rotation, and a straight that runs between two corners with contradictory band faces can match at one end only. So a straight-to-straight seam is always flush, and a straight-to-corner seam is flush more often than before but not always. A full corner-seam fix (re-rolling each corner-adjacent straight to match its specific corner) is a larger change this mod does not attempt.

Cosmetic only -- connectivity, networks, colour, paint and everything else are untouched.

### Plays nicely with drag-build mods

**[ZoopMod](https://steamcommunity.com/sharedfiles/filedetails/?id=3310094883)** (drag-build): zooping a long stretch of pipe, chute, or cable just places single-tile pieces, already at the canonical roll for cables. ZoopMod discovers the "long" variants by scanning the build kit's option list (`MultiConstructor.Constructables`) for the base name plus a trailing digit; this mod has emptied the long variants out of that list at startup, so ZoopMod finds none and falls back to placing single-tile pieces -- no error, just single tiles. Each single-tile cable it places then passes through the same build-time alignment hook (the construction path ZoopMod replays into), so it lands at the canonical orientation. Net: zooping a long stretch produces a row of single-tile cables, already aligned.

**[BlueprintMod](https://steamcommunity.com/sharedfiles/filedetails/?id=3672138641)** (paste a saved template): if a template contains long pieces -- built before this mod, or copied from another world -- pasting it places those long pieces, but on the next world load this mod's on-load rebuild converts them to single tiles, exactly like any other long piece it finds. A blueprint paste creates structures via a path (`OnServer.Create`) that bypasses the build-time cable-alignment hook, so a pasted long piece, and the roll of a pasted straight cable, are both put right on the next world load by the on-load sweep, the universal backstop.

**Other mods that create wrong-length or wrong-orientation segments** (an "upgrade to a long run" mod, the in-game merge-with-a-tool action, a future build mod): this mod never *rejects* anything -- it *rewrites* (a long-variant placement becomes N single-tile placements on the next world load) and *re-rolls* (a freshly built straight cable snaps to the canonical orientation as it is placed). So the other mod still functions; its output just comes out as single-tile, consistently oriented pieces. Anything it missed is fixed up on the next world load. [NetworkUpgrader](https://steamcommunity.com/sharedfiles/filedetails/?id=3656955459)'s `upgrade` command and the in-game merge tool, in particular, can no longer leave a long piece behind.

### Settings

All settings are server-authoritative: the host's values are the ones that take effect, and every player on a server (and a dedicated server) must run the same values. A joining client whose settings differ from the host's is rejected at join time with a message saying which one to change, the same way a version mismatch is rejected (see Compatibility below). They live in the in-game mod settings panel under the `Server - Pieces` and `Server - Cables` headers. **All of them take effect on a game restart** -- the strip / hide / build-cursor work runs once at startup, so toggling one mid-session does nothing until you relaunch (the in-game panel marks them with a restart indicator).

| Setting | Section | Default | What it does |
|---|---|---|---|
| Enable Network Purist Plus | Server - Pieces | on | Master switch. When off, the mod does nothing: long-piece variants stay in the build kits and the Stationpedia, no long run is rebuilt on load, no cable is realigned, the build cursor is left untouched. |
| Remove Long Gas Pipes | Server - Pieces | on | Removes the long `StructurePipeStraight3` / `5` / `10` variants -- the basic pipe (the game just calls it "Pipe"; named "Gas Pipe" here so it's not confused with the liquid pipe): stripped from the kit, hidden from the Stationpedia, and rebuilt from single tiles on load. |
| Remove Long Liquid Pipes | Server - Pieces | on | Same, for the long `StructurePipeLiquidStraight3` / `5` / `10` variants. |
| Remove Long Insulated Gas Pipes | Server - Pieces | on | Same, for the long `StructureInsulatedPipeStraight3` / `5` / `10` variants -- the insulated **gas** pipe (the game calls it "Insulated Pipe"; named "Insulated Gas Pipe" here). The insulated liquid pipe has its own toggle below. |
| Remove Long Insulated Liquid Pipes | Server - Pieces | on | Same, for the long `StructureInsulatedPipeLiquidStraight3` / `5` / `10` variants -- the insulated liquid pipe. |
| Remove Long Chutes | Server - Pieces | on | Same, for the long `StructureChuteStraight3` / `5` / `10` variants. (An item in transit inside a destroyed segment is lost, as documented in Limitations.) |
| Remove Long Super-Heavy Cables | Server - Pieces | on | Same, for the long `StructureCableSuperHeavyStraight3` / `5` / `10` variants (the only cable tier with long variants; includes the burnt damage-state siblings). |
| Align Straight Cables | Server - Cables | on | Re-rolls every straight cable (all tiers) to one consistent orientation per run axis -- existing runs on load, new ones as built, and the build cursor for the straight-cable tiers that have no long variant. Cosmetic only; the cable rotate key becomes preview-only. (The band can still jump where a straight meets a corner cable -- corners aren't re-rolled; see Limitations.) When off, a cable's roll is left wherever it was placed. The merge-with-tool fix for the stripped pipe / chute / super-heavy-cable kits is governed by the per-family toggles above, not this one. |

The master toggle and the per-family toggles being server-authoritative is not just a multiplayer-fairness rule: stripping a variant from the build kits and hiding it from the Stationpedia happens at prefab-load time, which is before any client joins, so each machine first applies its own settings. The join check is what keeps everyone's build-kit option lists and Stationpedias in sync. Changing a setting takes effect on the next Stationeers restart (the prefab-time work runs once at boot); the world rebuild on load always uses the host's values.

## Limitations

- **Rebuilt pipe runs are emptied -- the gas inside a long pipe run is deleted when the run is rebuilt from single tiles.** This is by design, not a bug being chased: the game's atmospherics-event system loses a pipe network's gas when its pipes are replaced this way, there is no clean workaround, and the mod will not attempt to preserve it. Re-pressurize the new single-tile pipes. (Layout, rotation, colour and network connectivity are preserved; only the gas is lost.) It happens on the first load after installing the mod, and again for any long pipe that enters a world later -- via NetworkUpgrader, a blueprint paste, or the creative menu -- all converted on the next world load.
- **An item in transit inside a long chute is deleted** -- an item physically moving through a long chute segment at the moment it is rebuilt is lost, also by design. Items sitting in chute bins are unaffected.
- **Straight cables align to each other, but the coloured band can still jump where a straight meets a corner cable.** Corner cables have a fixed band orientation that this mod doesn't touch (re-rolling a corner would change connectivity, not just looks), and no single per-axis straight-cable orientation mates with every corner. The mod picks the orientation that mates with the most corners, so straight-to-corner seams line up more often than before -- but not always. (Straight-to-straight seams are always flush.)
- The rotate key no longer changes a placed straight cable's roll -- a built cable always snaps to the canonical orientation for its run axis (this is the cable-alignment feature; it affects the cable preview, not gameplay).

## Compatibility

**Requires:** BepInEx + StationeersLaunchPad

**Verified compatible with [ZoopMod](https://steamcommunity.com/sharedfiles/filedetails/?id=3310094883) and [BlueprintMod](https://steamcommunity.com/sharedfiles/filedetails/?id=3672138641), and compatible by design with other build mods:** this mod's Harmony patches sit on `World.OnLoadingFinished`, `MultiConstructor.Construct`, `Constructor.Construct` and `Stationpedia.DataHandler.HandleThingPageOverrides` (plus a `Prefab.OnPrefabsLoaded` event subscription) -- none of which ZoopMod or BlueprintMod patches -- so there is no patch conflict. More than that, the mod never *rejects* a placement: when another mod (or the in-game merge tool, or a future build mod) produces a wrong-length or wrong-orientation segment, this mod *rewrites* it into single tiles on the next world load and *re-rolls* a freshly built straight cable to the canonical orientation as it is placed. The other mod keeps working; its output simply comes out as single-tile, consistently oriented pieces, with the on-load rebuild as the backstop. See [Plays nicely with drag-build mods](#plays-nicely-with-drag-build-mods) above for the ZoopMod / BlueprintMod / general-case details.

**Network enforcement:** Network Purist Plus uses LaunchPadBooster to require that every player (and every dedicated server) on a multiplayer game runs the same version of the mod *and* the same settings. A client without the mod, with a different version, or with different settings (any of the per-family toggles, the master toggle, or the cable-alignment toggle) is rejected at join time with a clear message. This is necessary because the build-kit option lists and the Stationpedia are reshaped at prefab-load time, before any client joins, so a mismatch would desync what each player sees in their build wheel. Also fine alongside NetworkUpgrader and the "no tool required for pipe/cable merging" mods (none of which can create long pieces while this mod is active).

**Dedicated servers** need the same BepInEx + StationeersLaunchPad + NetworkPuristPlus setup installed server-side, with the same settings as the connecting players. The server's copy is the one that rebuilds the world on load and realigns cables.

## Reporting Issues

If you run into a bug or something behaves unexpectedly, please open an issue on [GitHub](https://github.com/SixFive7/StationeersPlus/issues). Please include "[Network Purist Plus]" in the title so reports can be triaged. Steam comment notifications don't always come through, so GitHub is the reliable way to make sure a report is seen.

## Changelog

Version history lives in [`NetworkPuristPlus/About/About.xml`](NetworkPuristPlus/About/About.xml) under `<ChangeLog>` and is published on the [Steam Workshop Change Notes tab](https://steamcommunity.com/sharedfiles/filedetails/changelog/3724874914) with every release.

## License

Apache License 2.0. See [LICENSE](../../LICENSE) for the full text and [NOTICE](../../NOTICE) for attribution.
