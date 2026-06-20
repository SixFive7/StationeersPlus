# Spray Paint Plus TODO

This file tracks open issues only. Entries are plain bullets, not `- [ ]` checkboxes; when an item is done, remove it rather than ticking it off. Completed work lives in git history.

Implemented changes still awaiting an in-game or dedicated-server test do not belong here; record those in `PLAYTEST.md` (same folder).

## Post-release follow-up

- One release cycle after v1.6.0 has propagated: remove `GlowThingSaveData.cs` and the back-compat `ThingDeserializeSaveGlowPatch`. This strands any users who skipped v1.6.0 entirely when loading a v1.5.x save, so keep the back-compat path for at least one minor version.

## Paintability expansion

- After the v1.10.0 steel-frame paintability feature ships: scan the entire game for structures the base game leaves unpaintable (`PaintableMaterial == null`) and extend the `Make More Structures Paintable` toggle (`Client - Paintability`) to cover them. v1.10.0 only makes the two unpaintable Steel Frame shapes (Corner, Side) paintable. Get the authoritative list from the ScenarioRunner `paintable-prefab-dump` scenario run on a base-game-only dedicated server: the mod-loaded run during v1.10.0 work reported ~39 unpaintable `Structure` prefabs, but that count includes Workshop mods, so the vanilla subset must be re-derived. Confirm each candidate renders in `Standard` mode (Batched structures throw in `Structure.SetCustomColor` and cannot be recolored per-instance).

