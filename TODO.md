# StationeersPlus TODO

Cross-mod and repo-wide tasks. Per-mod tasks live in each mod's own `TODO.md`.

## Preview art

- [ ] **EquipmentPlus `Preview.source.png`**: currently only a `.placeholder` text file at the repo root. The resized `About/Preview.png` (1280x720) and `About/thumb.png` (640x360) already exist, but there is no archival source file to re-crop or re-scale from. Regenerate a 1920x1080 (or higher) master from whatever produced the current preview, or commission a fresh one, and drop it in at `Plans/EquipmentPlus/Preview.source.png`.

- [ ] **Move canonical preview-image reference in `CLAUDE.md` to `Mods/Template/` once real art lands there**. The "Content: preview image dimensions" section currently points at `Mods/SprayPaintPlus/Preview.source.png` as the canonical example. When `Mods/Template/Preview.source.png` and `Mods/Template/Template/About/Preview.png` / `thumb.png` are populated with actual example images (not just the `.placeholder` text files), update `CLAUDE.md` so the Template serves as the reference, matching the pattern that new mods are seeded from `Mods/Template/`.

## Repo-wide audits

- [ ] **Full rules and hooks compliance sweep**: walk over every rule in `CLAUDE.md` (naming, no abbreviations, no developer-specific paths, settings-panel grouping and ordering, Apache 2.0 headers, documentation layout, AI-tells style rules) and every committed hook under `.claude/hooks/`, then audit the entire repository (`Mods/`, `Plans/`, `Research/`, `tools/`, root files) for violations. Report findings per file with the specific rule broken, then fix or list as follow-up todos.

- [ ] **Template vs SprayPaintPlus parity audit**: `Mods/SprayPaintPlus/` is the reference for README and About quality. Diff `Mods/Template/README.md` and `Mods/Template/Template/About/About.xml` against SprayPaintPlus to confirm the template captures the same structure, section ordering, tagline placement, ChangeLog format, Reporting Issues block, and license section. Then walk every other mod in `Mods/` and `Plans/` and bring their README and About.xml up to the same content and layout quality, flagging gaps in a per-mod checklist.

- [ ] **External dependency audit**: walk every mod's `.csproj` under `Mods/` and `Plans/` and list all assembly references outside the core game DLLs (`Assembly-CSharp`, `UnityEngine.*`) and BepInEx. For each external lib (LaunchPadBooster, StationeersLaunchPad shims, Stationeers Logic Extended, Harmony extras, etc.), check whether the mod actually uses any type or member from it, and whether the usage is load-bearing or could be inlined / dropped. Report per-mod findings with a keep/drop recommendation and rationale.

- [ ] **Project GUID consistency sweep**: a survey during MaintenanceBureauPlus scaffolding found inconsistencies across `.sln` / `.csproj` / `AssemblyInfo.cs` for the MSBuild / COM project GUIDs. Fix each mod so all three surfaces agree.
  - `Mods/PowerTransmitterPlus/PowerTransmitterPlus.sln` references `{E5F6A7B8-C9D0-4123-DEF0-456789012345}` but its `.csproj` and `AssemblyInfo.cs` carry `{D4E5F6A7-B8C9-4012-CDEF-345678901234}`. Solution won't find the project on a clean open.
  - `Mods/SprayPaintPlus/SprayPaintPlus.sln` references `{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}` but its `.csproj` and `AssemblyInfo.cs` carry `{F47AC10B-58CC-4372-A567-0E02B2C3D479}`. Worse: the stale `.sln` GUID collides with InspectorPlus's real GUID (`Mods/InspectorPlus/` uses the same value consistently across all three of its surfaces).
  - `Plans/EquipmentPlus/EquipmentPlus/Properties/AssemblyInfo.cs` uses `e5f6a7b8-c9d0-1234-efab-345678901234` while its `.sln` + `.csproj` agree on `{D4E5F6A7-B8C9-0123-DEFA-234567890123}`.
  - `Mods/Template/Template/Template.csproj` keeps `{00000000-...}` as the placeholder, which is fine as a sentinel but is the root cause of the drift above (no standard procedure regenerates matching GUIDs when seeding a new mod). Consider either a `tools/new-mod.ps1` helper that generates matching GUIDs across `.sln`, `.csproj`, and `AssemblyInfo.cs`, or a monorepo-wide migration to SDK-style csproj which eliminates the three-way sync requirement entirely (SDK-style csproj has no `<ProjectGuid>` element).

- [ ] **SDK-style vs classic csproj decision**: `MaintenanceBureauPlus/MaintenanceBureauPlus.csproj` is the only SDK-style (`<Project Sdk="Microsoft.NET.Sdk">`) project in the monorepo. Every other mod (`Mods/Template`, `Mods/InspectorPlus`, `Mods/PowerTransmitterPlus`, `Mods/SprayPaintPlus`, `Plans/EquipmentPlus`, and the archived `Plans/MaintenanceBureauPlus/Plans/LLMArchive/LLM`) is classic with manual `Microsoft.Common.props` / `Microsoft.CSharp.targets` imports. The archived LLM bolted NuGet onto classic via `<RestoreProjectStyle>PackageReference</RestoreProjectStyle>`. Decide whether to (a) migrate every other mod and `Mods/Template/` to SDK-style (cleaner, better `dotnet build` support, kills the GUID-sync problem above), or (b) rewrite MaintenanceBureauPlus in classic form to match the rest.

- [ ] **Plugin GUID naming convention**: reverse-DNS `BepInPlugin` IDs are inconsistent. Most mods use a bare `net.<modname>` form (`net.spraypaintplus`, `net.powertransmitterplus`, `net.inspectorplus`, `net.equipmentplus`). `MaintenanceBureauPlus` uses a vendor-scoped form: `net.sixfive7.maintenancebureauplus`. Pick one convention and rename the outlier(s). Note that renaming a published mod's `ModID` / `PluginGuid` breaks BepInEx config paths and mod-author references, so this is a pre-release cleanup; do it before any of these mods hit Workshop.

- [ ] **`Directory.Build.props` local-copy parity**: the committed `Directory.Build.props.template` at the repo root defines the `EnsureStationeersPath` target (errors the build early if `$(StationeersPath)` is unset or points at a location without `Assembly-CSharp.dll`). The root `CLAUDE.md` documents this target as present in the filled-in (gitignored) `Directory.Build.props` too. Local copies currently in the wild are missing it. Add a one-line check to the monorepo setup docs, or have `Mods/Template/` / a setup script re-emit the target into `Directory.Build.props` when it detects the target is missing.

## New mods

- [ ] **BatteryBackupLightPlus**: build an improved take on alliephante's "Battery Backup Light" (https://steamcommunity.com/sharedfiles/filedetails/?id=3569109044). The base mod makes the Wall Light Battery act as an emergency backup (battery kicks in when cable power is lost) with a `LogicType.Mode` toggle between `0` (battery backup) and `1` (manual override). Improvements to address:
  - Add a power connector to the light so it can take wired power directly, instead of only running off its internal battery when unpowered.
  - Provide custom Mode strings so `0` / `1` show readable labels (e.g. `Backup` / `Manual`) in the logic UI, which the original author called out as unresolved.
  - Review the current `<InGameDescription>` / Workshop description of the source mod for any other "I couldn't figure out" items and fold fixes into the feature list.
  - Follow the standard `StationeersPlus` conventions: seeded from `Mods/Template/`, display name `Battery Backup Light Plus`, code name `BatteryBackupLightPlus`, Apache 2.0, README/About/InGame tagline in sync, preview art at the three required sizes.
