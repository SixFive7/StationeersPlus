# Spray Paint Plus TODO

This file tracks open issues only. Entries are plain bullets, not `- [ ]` checkboxes; when an item is done, remove it rather than ticking it off. Completed work lives in git history.

Implemented changes still awaiting an in-game or dedicated-server test do not belong here; record those in `PLAYTEST.md` (same folder).

## Reply to the Workshop comments that prompted v1.11.0

- Two comments have been waiting since 24 and 25 July, and v1.11.0 exists because of them. Reply when it ships, not before, so the reply can point at a version people can actually install.
  - **streakymirror73** (25 July) asked for a config option to disable color cycling, "to maintain some balance and make it a little less OP". That is the `Can cannot change color` mode of the new Color Cycling setting: the wheel stops working and each color needs its own printed can. Worth saying that the server can enforce it for everyone, and that a player can also choose it for themselves.
  - **AlienXtream** (24 July) suggested separating the paints so base cans cycle base colors and a metallic can cycles the DLC ones. That is the `Cycles within paint family` mode, close to exactly as described. Their first suggestion, giving the spray gun an inventory of cans, was not taken: the gun is ammo-less by design for glow paint, so it cannot carry the colors, and the family mode reaches the same goal without that conflict. Say so plainly rather than ignoring the half that was declined.
- Both were reported through Steam comments, so reply there. Note that neither was a bug, so no GitHub issue exists to close.

## Release bookkeeping

- `Mods/Template/RELEASE.md` Rule 2 says a release commit touches exactly `Plugin.cs`, `About.xml` and `CHANGELOG.md`, with feature work in prior commits. Commit `b81d271c` already carried the 1.11.0 version bump alongside feature work, so by the time v1.11.0 ships there is no version left to bump and a conforming release commit cannot be constructed. Decide at release time between tagging the final feature commit directly, or bumping to 1.11.1 so a clean release commit exists. Neither is wrong; the point is to choose deliberately rather than discover it while tagging.

## Post-release follow-up

- One release cycle after v1.6.0 has propagated: remove `GlowThingSaveData.cs` and the back-compat `ThingDeserializeSaveGlowPatch`. This strands any users who skipped v1.6.0 entirely when loading a v1.5.x save, so keep the back-compat path for at least one minor version.

