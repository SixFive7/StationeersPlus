# Force Field Door Mod Fix TODO

This file tracks open issues only. Entries are plain bullets, not `- [ ]` checkboxes; when an item is done, remove it rather than ticking it off. Completed work lives in git history.

Implemented changes still awaiting an in-game or dedicated-server test do not belong here; record those in `PLAYTEST.md` (same folder).

- Publish to the Steam Workshop and fill in the real `<WorkshopHandle>` in `About.xml` (currently `0`).
- Watch for a ForceFieldDoorMod update. Once its author ships a build that drops the stale `CanContainAtmos(WorldGrid)` call, this shim self-retires; at that point retire the mod (unpublish or mark deprecated) rather than leaving a dead shim on the Workshop.
- Consider generalising into a shared "stale mod reference" fix host if a second abandoned mod needs the same treatment. The current design is one bespoke reimplementation; a second entry would be a parallel class registered in `Plugin.Awake`.
