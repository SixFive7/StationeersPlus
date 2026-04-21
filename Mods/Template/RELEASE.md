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
