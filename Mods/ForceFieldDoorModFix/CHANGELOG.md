# Changelog

Full version history for Force Field Door Mod Fix. The newest entry also appears in `About.xml` `<ChangeLog>` and as the latest note on the Steam Workshop Change Notes tab. Newest version on top.

## v1.0.1: ForceFieldDoorMod fixed upstream, this mod is no longer needed
- The original ForceFieldDoorMod was updated by its author on 6 July 2026 and now works on current Stationeers builds on its own.
- This compatibility fix is no longer required. You can safely unsubscribe; it is also safe to leave it enabled, since it detects the upstream fix and does nothing.
- Description-only update to inform subscribers. No functional change.

## v1.0.0: Initial release
- Keeps ForceFieldDoorMod (Steam Workshop 3328065049) working on Stationeers 0.2.6403 and later, where the removed one-argument `GridController.CanContainAtmos(WorldGrid)` call in its force field door crashed the simulation every atmospheric tick.
- Takes over the force field door's atmospheric tick and runs a corrected version using the current two-argument `CanContainAtmos` overload, preserving the door's variable power draw.
- Never edits ForceFieldDoorMod on disk. Does nothing if the mod is absent or has already been updated.
