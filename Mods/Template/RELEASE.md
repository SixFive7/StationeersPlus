# Mod release workflow

Rules for cutting a release in `Mods/<ModName>/`. `Plans/` mods do not tag releases.

This file is the single source of truth for release commits, version bumps, and tags. A PostToolUse hook on `Edit|Write(**/Mods/*/*/Plugin.cs)` injects a reminder pointing here. The hook over-fires on non-release Plugin.cs edits by design; the rules are short and worth re-reading when any plugin code changes.

## Rule 1: one mod per release commit

A release commit bumps exactly one mod's version. Never bump two mods' `<Version>` fields in the same commit. Cross-mod refactors are fine but are separate from release commits and do not bump `<Version>`. Tags point at commits, not subtrees; a commit that ships two mods cannot be tagged twice without ambiguity about what "version" the tag represents.

## Rule 2: a release commit is exactly these edits, nothing else

- `Mods/<ModName>/<ModName>/<ModName>.cs` (or `Plugin.cs`): `PluginVersion` bump.
- `Mods/<ModName>/<ModName>/About/About.xml`: `<Version>` bump and new top-of-`<ChangeLog>` entry.

Every release commit touches those two files and nothing else. Feature work goes in prior commits.

## Rule 3: always tag a release commit

After creating the release commit, tag it with an annotated tag in the format `mods/<ModName>/v<X.Y.Z>` and push the tag:

```
git tag -a mods/SprayPaintPlus/v1.4.2 -m "SprayPaintPlus 1.4.2"
git push origin mods/SprayPaintPlus/v1.4.2
```

The tag is the source of truth for "what shipped as 1.4.2." Workshop can be rolled back or updated; tags cannot.

## Rule 4: never move a pushed tag

If a release was mis-tagged or needs a hotfix, ship a new patch version (1.4.3) rather than moving v1.4.2.

## Rule 5: release commit message format

Release commits use this exact subject line, for grep-ability:

```
<ModName> v<X.Y.Z>: <short summary>
```

Example: `SprayPaintPlus v1.4.2: fix paint bucket deprecated-property warning`.

## Rule 6: tags only for Mods/, never Plans/

`Plans/` mods are WIP. They don't get version tags until they graduate to `Mods/` with a real release. The first release of a promoted mod starts at v1.0.0.

## Rule 7: after the Workshop upload, verify deployment and update metrics

The release commit and tag cover the source side. Uploading the new build to the Steam Workshop is a separate manual step (the in-game StationeersLaunchPad publish flow). Once that upload is done, finish the release with a verification and bookkeeping pass:

1. **Verify the Workshop item updated correctly.** Open the mod's Workshop page through the Playwright browser (see "steamcommunity.com lookups must go through Playwright" in the repo `CLAUDE.md`) and confirm:
   - "Updated" date is today and the changenote count went up by one.
   - The top `<ChangeLog>` entry on the page matches the new `v<X.Y.Z>` and its notes.
   - Description, preview image, and tags still render correctly (no broken BBCode, no missing image) per `Mods/Template/LAYOUT.md`.
   If anything is wrong, fix it and re-upload before considering the release done. Do not move the tag (Rule 4); a content-only re-upload does not need a new version, a code fix does.

2. **Update the metrics tracker for every published mod.** While you are already looking at the Workshop, append a fresh dated row to each mod's table in the repo-root `METRICS.md` (not just the mod you released). Follow the column instructions in `METRICS.md`'s "Updating" section. Doing every mod at once keeps the snapshot dates aligned so the trend lines stay comparable.

The `METRICS.md` change is an ordinary doc change, not part of the release commit (Rule 2): commit it separately, with the verification, after the release commit and tag.
