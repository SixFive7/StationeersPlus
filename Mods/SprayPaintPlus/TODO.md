# SprayPaintPlus TODO

## v1.6.0 verification (before tagging)

- [ ] Save a new v1.6.0 world with 3+ glowing Things. Expand the save ZIP and confirm: `world.xml` contains NO `xsi:type="GlowThingSaveData"` attributes; `sprayplus-glow.xml` is present with the expected ReferenceIds.
- [ ] Round-trip: load that save; InspectorPlus snapshot with `types=[Thing]`, `fields=[ReferenceId, CustomColor]` after load; confirm the 3 glowing Things show the emissive material in-game.
- [ ] Removal test: rename `BepInEx/plugins/SprayPaintPlus/` to `SprayPaintPlus.disabled/`, load the save from step 1; confirm the save loads (no "Failed to load the world.xml" error), all Things present without glow. Restore the folder after.
- [ ] Back-compat: load a pre-v1.6.0 save (with `GlowThingSaveData` entries in world.xml); confirm glow restored. Save it. Expand the new ZIP and confirm world.xml no longer contains `xsi:type="GlowThingSaveData"` and the side-car carries the IDs.
- [ ] Auto-save: let an auto-save fire with glowing Things present; repeat the ZIP-content checks on the auto-saved file.
- [ ] Multiplayer host: run a multiplayer session with glowing Things, save on the host, confirm side-car written. `GameManager.AutoSaveNow` guards on `!NetworkManager.IsClient` so client saves are already excluded.
- [ ] Empty set: save a world with zero glowing Things; confirm no `sprayplus-glow.xml` entry exists (or an empty-IDs entry is fine; both are acceptable).

## Post-release follow-up

- [ ] One release cycle after v1.6.0 has propagated: remove `GlowThingSaveData.cs` and the back-compat `ThingDeserializeSaveGlowPatch`. This strands any users who skipped v1.6.0 entirely when loading a v1.5.x save, so keep the back-compat path for at least one minor version.

