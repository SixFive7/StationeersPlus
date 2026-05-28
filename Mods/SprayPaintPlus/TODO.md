# Spray Paint Plus TODO

This file tracks open issues only. Entries are plain bullets, not `- [ ]` checkboxes; when an item is done, remove it rather than ticking it off. Completed work lives in git history.

Implemented changes still awaiting an in-game or dedicated-server test do not belong here; record those in `PLAYTEST.md` (same folder).

## Post-release follow-up

- One release cycle after v1.6.0 has propagated: remove `GlowThingSaveData.cs` and the back-compat `ThingDeserializeSaveGlowPatch`. This strands any users who skipped v1.6.0 entirely when loading a v1.5.x save, so keep the back-compat path for at least one minor version.

