# StationeersPlus TODO

Cross-mod and repo-wide tasks. Per-mod tasks live in each mod's own `TODO.md`.

## Preview art

- [ ] **EquipmentPlus `Preview.source.png`**: currently only a `.placeholder` text file at the repo root. The resized `About/Preview.png` (1280x720) and `About/thumb.png` (640x360) already exist, but there is no archival source file to re-crop or re-scale from. Regenerate a 1920x1080 (or higher) master from whatever produced the current preview, or commission a fresh one, and drop it in at `Plans/EquipmentPlus/Preview.source.png`.

- [ ] **Move canonical preview-image reference in `CLAUDE.md` to `Mods/Template/` once real art lands there**. The "Content: preview image dimensions" section currently points at `Mods/SprayPaintPlus/Preview.source.png` as the canonical example. When `Mods/Template/Preview.source.png` and `Mods/Template/Template/About/Preview.png` / `thumb.png` are populated with actual example images (not just the `.placeholder` text files), update `CLAUDE.md` so the Template serves as the reference, matching the pattern that new mods are seeded from `Mods/Template/`.

## New mods

- [ ] **BatteryBackupLightPlus**: build an improved take on alliephante's "Battery Backup Light" (https://steamcommunity.com/sharedfiles/filedetails/?id=3569109044). The base mod makes the Wall Light Battery act as an emergency backup (battery kicks in when cable power is lost) with a `LogicType.Mode` toggle between `0` (battery backup) and `1` (manual override). Improvements to address:
  - Add a power connector to the light so it can take wired power directly, instead of only running off its internal battery when unpowered.
  - Provide custom Mode strings so `0` / `1` show readable labels (e.g. `Backup` / `Manual`) in the logic UI, which the original author called out as unresolved.
  - Review the current `<InGameDescription>` / Workshop description of the source mod for any other "I couldn't figure out" items and fold fixes into the feature list.
  - Follow the standard `StationeersPlus` conventions: seeded from `Mods/Template/`, display name `Battery Backup Light Plus`, code name `BatteryBackupLightPlus`, Apache 2.0, README/About/InGame tagline in sync, preview art at the three required sizes.

## Repository infrastructure

- [ ] **Enable Git LFS for the repository** so `Plans/LLM/` source (2+ GB, LLaMA.cpp native runtimes plus model weights) can be committed. Currently `Plans/LLM/` is gitignored entirely; only a stub README lives there.

- [ ] **Review all `.ps1` scripts** committed to the repository (currently `tools/decode_il.ps1`) plus any `.ps1` scripts outside the repo that relate to the mods, for correctness, usefulness, and potential consolidation into `tools/`.

- [ ] **Update deploy scripts** that referenced paths under the previous per-mod-repo layout so they now point at `Mods/<ModName>/` under this monorepo. Deploy commands in `DEV.md.template` already use the new layout; any external scripts (wrapper batch files, shell aliases) need review.

- [ ] **Archive the old standalone GitHub repos** (`SixFive7/PowerTransmitterPlus`, `SixFive7/SprayPaintPlus`) once this monorepo is verified stable. Flip them to read-only on GitHub and replace each README with a pointer to the monorepo so outbound links and forks still lead somewhere useful.

- [ ] **Audit every `RESEARCH.md` for stale filesystem paths**. After the subtree import, imported mods' `RESEARCH.md` files may reference absolute paths under the pre-migration layout (for example `C:\Source\SixFive7\StationeersPlus\<ModName>\...`) or relative paths rooted at the old per-mod repo. Rewrite these to monorepo-relative paths (`Mods/<ModName>/...`) or to placeholders documented in `DEV.md.template`. Likely candidates: the subtree-imported `Mods/PowerTransmitterPlus/RESEARCH.md` and `Mods/SprayPaintPlus/RESEARCH.md`, plus any `Plans/` mod whose RESEARCH predates the move.
