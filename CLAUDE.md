# StationeersPlus Mods

Shared conventions for all Stationeers mods under this monorepo. `Mods/<ModName>/` holds released mods; `Plans/<ModName>/` holds work-in-progress; `tools/` holds repo-wide utility scripts.

## Repository layout

- `Mods/` contains released mods. Each subdirectory is a self-contained mod (own README, RESEARCH, source project, and `About/`).
- `Plans/` contains mods that are in progress and prototypes not yet released. They follow the same shape as released mods but are not tagged or published. Plans/ mods may carry design documents (`PLAN.md`, `plan.md`, `NOTES.md`) that are not permitted in released mods; these documents consolidate into `RESEARCH.md` or are deleted when the mod graduates to `Mods/`.
- `tools/` contains repository-wide utility scripts.
- `Mods/Template/` is the seed scaffold for creating new mods.
- Root files: `LICENSE` (Apache 2.0), `NOTICE`, `README.md`, `TODO.md` (cross-mod and repo-wide todos), `Directory.Build.props.template` (MSBuild inheritance, filled-in `Directory.Build.props` is gitignored), `DEV.md.template` (developer environment scaffold, filled-in `DEV.md` is gitignored), `.gitignore`, `.gitattributes`, `CLAUDE.md` (this file).

## Seed new mods from Mods/Template/

`Mods/Template/` is a copy-ready mod scaffold. When starting a new mod, copy `Mods/Template/` to `Mods/NewModName/`, rename `Template.sln` / `Template.csproj` / the inner `Template/` folder to `NewModName`, and fill in the `{{placeholders}}` in each file. The template carries the canonical README structure, `About.xml` section layout, `.csproj` boilerplate, and preview-image placeholder files with their required dimensions documented inline.

Every rule below applies to the Template as well; the Template is the reference implementation of these rules.

## Build: externalize the game install path

Every mod's `.csproj` must reference game and BepInEx assemblies via a `$(StationeersPath)` MSBuild property, never via hardcoded absolute paths. Steam install locations differ per developer (primary install, secondary library folders, different drive letters), so absolute paths in committed files break clones on any other machine.

Required setup:

- `Directory.Build.props` at the monorepo root defines `<StationeersPath>...</StationeersPath>`. **This file is gitignored** so each developer keeps their own path.
- `Directory.Build.props.template` at the monorepo root is committed with a placeholder default, so new contributors know what to copy and edit.
- The root `.gitignore` includes `Directory.Build.props`.
- `.csproj` HintPaths use `$(StationeersPath)\rocketstation_Data\Managed\...` (game DLLs such as `Assembly-CSharp`, `UnityEngine.*`) and `$(StationeersPath)\BepInEx\...` (BepInEx core and plugins such as LaunchPadBooster).
- The `EnsureStationeersPath` target, which fails the build with a clear message if `$(StationeersPath)` is unset or does not contain `rocketstation_Data\Managed\Assembly-CSharp.dll`, lives in `Directory.Build.props.template` (and therefore in the filled-in `Directory.Build.props`), not per-csproj. MSBuild's `Directory.Build.props` inheritance walks up the tree, so every mod's build picks up the check automatically.

## Content: mod naming

Every mod has two distinct name forms that are both canonical, used in different places:

- **Display name**, with spaces between words, capitalised per title case. Used wherever a player, reader, or Workshop visitor sees the name: `About.xml` `<Name>`, `README.md` H1 heading, `<InGameDescription>` title line, `<Description>` opening header, GitHub repo description, Workshop page title, chat messages.
- **Code name**, concatenated with no spaces, PascalCase. Used for anything machine-facing: folder names, `.sln` and `.csproj` file names, `RootNamespace` and `AssemblyName`, `ModID` (prefixed `net.`), C# namespaces, DLL file name.

Examples: `Spray Paint Plus` / `SprayPaintPlus`, `Power Transmitter Plus` / `PowerTransmitterPlus`, `Equipment Plus` / `EquipmentPlus`, `Inspector Plus` / `InspectorPlus`.

No bracketed suffix tags (no `[StationeersLaunchPad]`, no `[SLP]`, no `[Beta]` etc.) are appended to display names. The fact that a mod requires StationeersLaunchPad is communicated by the "Compatibility / Requires" section in `README.md` and `About.xml` `<Description>`, not by the title.

## Content: no abbreviations for mod or dependency names

Never shorten a mod or dependency name to an invented acronym. The only accepted short forms are the canonical display name (`Power Transmitter Plus`) and the canonical code name (`PowerTransmitterPlus`); both are already short enough. Invented acronyms like `PTP` (Power Transmitter Plus), `SPP` (Spray Paint Plus), `EP` (Equipment Plus), `IP` (Inspector Plus), `SP` (Stationpedia Plus) are never acceptable, and the same rule applies to dependencies: write `StationeersLaunchPad`, not `SLP`; `LaunchPadBooster`, not `LPB`; `Stationeers Logic Extended`, not `SLE`; `BepInEx`, not `BIE`.

Scope: every committed file (`README.md`, `RESEARCH.md`, `TODO.md`, `PLAN.md`, `NOTES.md`, `CLAUDE.md`, `About.xml`, `.cs`, `.csproj`, `.sln`, `.md` of any kind), every commit message, every PR description, every Workshop comment reply, and every chat response. No tables, no tags, no bracketed suffixes, no quick shorthand in parentheses.

The cost of typing the full name is paid once; the cost of an abbreviation propagates to every future reader (including AI assistants re-reading the repo weeks later) who has to decode it, and inconsistent shorthand drifts across files. Readers should never need a glossary.

Only exception: citing an abbreviation *as a negative example* explicitly calling it out as forbidden (for example, "section title `Power Transmitter Plus Details`, not `PTP Details`"). Such citations teach the anti-pattern and require the abbreviation to be shown precisely so the rule is unambiguous.

## Content: keep README and About in sync

User-facing content lives in three mirrored places, each with its own markup. When features or settings copy change, update all three.

- `README.md` is the source of truth. Markdown, rendered on GitHub. Full-length.
- `About.xml` `<Description>` is the Steam Workshop mirror. BBCode (`[h1]`, `[b]`, `[list][*]`, `[url=...]`). Compressed from the README, not a literal translation, but the same sections and wording where they fit.
- `About.xml` `<InGameDescription>` is the in-game mod-settings panel. Unity rich text (`<b>`, `<size>`, `<color>`). Tighter still, feature-list style.

## Content: taglines in sync across three surfaces

Every mod has one canonical tagline: a single sentence describing the mod in its current feature-complete state. The same sentence appears verbatim, adjusted only for the surface's markup, in three places:

- GitHub repo description (plain text, shown under the repo name on github.com).
- `About.xml` `<Description>` opening line (BBCode, rendered on the Steam Workshop listing page beneath the `[h1]` title).
- `About.xml` `<InGameDescription>` subtitle (Unity rich text, rendered in the in-game mod settings panel beneath the sized/coloured title line).

When the feature set changes, update all three. Short-form abbreviations and marketing-style rewrites that drift between surfaces are not allowed; the tagline is one sentence, identical content, three encodings.

## Content: changelog lives in About.xml only

Steam Workshop consumes `About.xml` `<ChangeLog>` directly and submits its text as the per-update change note attached to each Workshop publish. Workshop renders the change note as **plain text**: BBCode tags (`[h2]`, `[list][*]`, `[b]`) appear as literal characters and do not format. Steamworks enforces an 8000-character cap on the submitted change note.

Rules:

- `<ChangeLog>` content is plain text only. Never use BBCode inside `<ChangeLog>`, even though the surrounding `<Description>` element uses BBCode heavily.
- Keep the full version history inside `<ChangeLog>` in reverse-chronological order, newest at the top. Each Workshop publish submits the entire block, so the full history appears under that update's timestamp on the Workshop Change Notes tab.
- No separate `CHANGELOG.md` file at the mod's root. `About.xml` `<ChangeLog>` is the single source of truth. GitHub readers follow the `## Changelog` section in `README.md`, which links to the Workshop change-notes URL and the `About.xml` file.
- `About.xml` also carries `<Version>`. Bump both `<Version>` and `<ChangeLog>` whenever `PluginVersion` in the plugin's main class changes.

Entry format for each version in `<ChangeLog>`:

```
v1.2.4: One-line summary
- Detail bullet
- Another detail bullet

v1.2.3: Previous version's summary
- ...
```

Blank line between versions. Dashes for bullets. No `#`, no `[h3]`, no Markdown. Line breaks render as line breaks on Workshop.

## Content: About.xml structure and XML safety

Every `About.xml` uses the same element order, and every mod pays attention to the same two XML-parse pitfalls. StationeersLaunchPad deserializes `About.xml` with `XmlSerializer`; a single stray angle bracket inside a simple-text element renames the mod `[Invalid About.xml] <ModID>` in-game and the plugin loads under that broken label.

Canonical top-level element order (matches `Mods/Template/Template/About/About.xml`):

```
Name, ModID, Author, Version, Description, ChangeLog, WorkshopHandle, Tags, InGameDescription
```

Rules:

- Follow that order in every new and existing `About.xml`. Do not interleave `<Tags>` before `<ChangeLog>`, do not push `<WorkshopHandle>` to the end.
- `<WorkshopHandle>` is always present AND always numeric. Un-published mods (anything in `Plans/`, or a released mod before its first Workshop upload) use `<WorkshopHandle>0</WorkshopHandle>`. Fill in the real numeric handle once the Workshop item exists. An empty element (`<WorkshopHandle></WorkshopHandle>`) throws `XmlConvert.ToUInt64("")` inside StationeersLaunchPad's generated XmlSerializer reader and renames the mod `[Invalid About.xml] <ModID>` at load time, even though the BepInEx plugin itself still loads. See `Research/Patterns/AboutXmlWorkshopHandleParse.md` for the full stack trace and symptoms.
- `<Description>` and `<ChangeLog>` are simple-text XML elements. Any literal `<` or `>` inside their content must be escaped as `&lt;` and `&gt;`. This applies to phrases like "added `<WorkshopHandle>` to About.xml" in the changelog and filename patterns like `snapshot_<timestamp>.json` in the description. BBCode square brackets (`[h1]`, `[list][*]`, `[url=...]`) are safe; only angle brackets need escaping.
- `<InGameDescription>` is wrapped in `<![CDATA[...]]>` everywhere. Unity rich text (`<size>`, `<color>`, `<b>`) inside the CDATA block is raw, not escaped.
- When seeding a new mod from `Mods/Template/`, preserve the element order and the empty `<WorkshopHandle>` verbatim.

## Content: About.xml element size caps

Steam Workshop enforces per-element size limits on `About.xml`. Exceeding a cap causes Steam to truncate the value mid-BBCode on the Workshop listing page and in the in-game mod browser, which visibly breaks the rendered layout even though the underlying BBCode is valid.

Caps per element:

- `<Description>`: 8000 characters including BBCode markup. Count the UTF-8 content inside the element, not the wrapping `<Description>...</Description>` tags.
- `<ChangeLog>`: 8000 characters (also stated in "Content: changelog lives in About.xml only").
- `<InGameDescription>`: no hard cap observed, but keep it short enough to read in the tight in-game settings panel.

Verify `<Description>` content size on every edit that touches it:

```bash
awk '/<Description>/{flag=1; sub(/.*<Description>/,"")} /<\/Description>/{sub(/<\/Description>.*/,""); print; flag=0; exit} flag' About.xml | wc -c
```

Target at most 7900 characters to leave room for future additions. Cap violations are invisible in a source diff; they only manifest as a broken Workshop listing, so the check must be run proactively.

When `About.xml` size constraints and `README.md` clarity conflict, `README.md` is the long-form source of truth; `<Description>` is the compressed Workshop mirror per the "Content: keep README and About in sync" rule. Trim prose in the Description first (explanatory paragraphs, long parentheticals, redundant qualifiers) before cutting sections or setting entries players need.

## Content: Reporting Issues section

Every mod's `README.md` and `About.xml` `<Description>` must include a "Reporting Issues" section directing users to the monorepo's GitHub issues page. Steam Workshop comment notifications are unreliable, so bug reports left as Workshop comments often go unseen. Point users at GitHub instead.

- `README.md`: `## Reporting Issues` section with a markdown link to https://github.com/SixFive7/StationeersPlus/issues.
- `About.xml` `<Description>`: `[h2]Reporting Issues[/h2]` section with a BBCode `[url=...]` link to the same URL.
- Issue titles should start with the mod's display name so triage is easy (e.g. `[SprayPaintPlus] ...`).
- `<InGameDescription>` does not need this section (space is tight and the panel is not where players file bugs).

## Content: InGameDescription compact line spacing

StationeersLaunchPad renders `<InGameDescription>` through TextMeshPro with a default line-height that leaves visible gaps between every line. The in-game mod panel is a short scrollable area, and any description longer than a tagline gets pushed past the visible window because of the wasted vertical space. Wrap the body of `<InGameDescription>` (everything after the title line) in a `<line-height=X%>...</line-height>` tag so TMP uses a compact per-line advance.

Canonical shape:

```xml
<InGameDescription><![CDATA[<size="30"><color=#ffa500>Mod Name</color></size>
<line-height=45%>Tagline sentence here.
<b>Features:</b>
- Bullet one
- Bullet two
<b>Settings:</b> ...
<b>Requires:</b> ...
</line-height>
]]></InGameDescription>
```

Rules:

- The `<size>` + `<color>` title line stays OUTSIDE the `<line-height>` wrap so it keeps the default taller line-height and separates visually from the body.
- Tested starting value is `<line-height=45%>`. Acceptable range is 40 to 55 percent. Below 35 percent produces visible overlap; above 60 percent leaves enough gap that the tag stops earning its place.
- Do not use blank lines anywhere inside `<InGameDescription>`. Section headers marked with `<b>` already carry enough visual weight; a blank line on top of the default TMP spacing reintroduces the airy layout this rule exists to prevent.
- `<line-height>` is a Unity TMP tag. It has no effect on the Workshop `<Description>` (which is BBCode); the tag would render as literal text if it appeared there. Scope: in-game panel only.
- Applies to every mod that ships an `<InGameDescription>`, including `Mods/Template/` as the canonical scaffold. Released mods not yet wrapped pick up the rule on their next release cycle.

## Content: preview image dimensions

Steam Workshop displays preview images in a 16:9 frame (637x358 on the listing page). Images that deviate from 16:9 get letterboxed with black bars by Steam's `impolicy=Letterbox` renderer, which looks broken. Any `Preview.png` or `thumb.png` generated for a mod's `About/` folder must be exactly 16:9.

Rules for any contributor generating or regenerating preview art:

- Generate at **1280x720** (preferred) or **1920x1080**. Both are exact 16:9 and scale cleanly to Steam's display size.
- The `thumb.png` that ships in `About/` may be a smaller 16:9 size (e.g. 640x360) but must still be exact 16:9, never 640x367 or any other off-ratio size.
- When prompting an external image generator, state the aspect ratio as 16:9 explicitly in the prompt AND request the target pixel dimensions (e.g. "1792x1024" for OpenAI gpt-image-1 widescreen, which is 16:9). Verify the returned file with `python -c "from PIL import Image; print(Image.open('file.png').size)"` before committing.
- If the generator returns a non-16:9 image, re-crop or regenerate. Do not upload off-ratio images and rely on Steam's letterbox to fix them.
- The aspect check is mandatory in any workflow that produces a new preview image.

Three files must exist for every mod's preview image:

- `Preview.source.png` at the mod's folder root under `Mods/<ModName>/`: the original full-resolution source image (the file before any resizing). This is the archival copy for re-cropping or re-scaling.
- `About/Preview.png`: resized to **1280x720** (exact 16:9). This is the Steam Workshop listing image.
- `About/thumb.png`: resized to **640x360** (exact 16:9). This is the in-game mod browser thumbnail.

Both `Preview.png` and `thumb.png` are derived from `Preview.source.png`. When regenerating preview art, always save the source file first, then produce the two resized copies from it.

Canonical reference: `Mods/SprayPaintPlus/Preview.source.png` (source), `Mods/SprayPaintPlus/SprayPaintPlus/About/Preview.png` (1280x720), `Mods/SprayPaintPlus/SprayPaintPlus/About/thumb.png` (640x360). The three preview-image locations in `Mods/Template/` are populated with placeholder text files (`Preview.source.png.placeholder`, `About/Preview.png.placeholder`, `About/thumb.png.placeholder`) that document their required dimensions inline; replace each with the real PNG at the stated size.

## Content: mod settings panel grouping and ordering

Every `ConfigEntry` a mod binds via BepInEx `Config.Bind(section, key, default, description)` appears in the StationeersLaunchPad in-game mod settings panel under a collapsible header named after the `section` string. StationeersLaunchPad has no other grouping mechanism; see `Research/Patterns/StationeersLaunchPadSettingsGrouping.md` for the decompiled internals. To keep the panel consistent across every mod in this repo, follow the conventions below.

**Section naming.** Use the form `<Scope> - <Topic>`. The scope is one of:

- `Client` if the player's local value takes effect. Each player sets it independently, and it is never synced.
- `Server` if only the host's value matters in multiplayer. Clients' values are ignored or overridden. Single-player counts as host.

The topic names the functional group in title case, no abbreviations: `Visual`, `Pulse`, `Consumables`, `Network Painting`, etc. Always use the compound `Scope - Topic` form, even when a mod has only one topic inside a scope (`Client - Preferences`, not bare `Client`). Consistent structure across mods outweighs saving one dash on single-topic cases.

StationeersLaunchPad sorts group headers alphabetically with no author override, so `Client - *` groups cluster before `Server - *` groups naturally.

**Entry ordering within a group.** Attach an `("Order", int)` tag to the `ConfigDescription` so entries appear in a deliberate order:

```csharp
EnableNetworkPainting = Config.Bind(
    "Server - Network Painting", "Enable Network Painting", true,
    new ConfigDescription(
        "(Server-authoritative) ...",
        null,
        new KeyValuePair<string, int>("Order", 10)));
```

Use spacing of 10, 20, 30 to leave room for future insertions. Without the `Order` tag, StationeersLaunchPad falls back to alphabetical by key name; do not rely on alphabetical for anything players see.

**Logical-ordering guidance.** When placing entries inside a group, follow these priorities:

- Master toggles sit above their dependents. `Enable Network Painting` comes above the individual `Network Paint Pipes` / `Cables` / etc. toggles it gates.
- Related settings cluster. Utility networks (pipes, cables, chutes) come before structural networks (walls, rails, large structures) inside `Network Painting`.
- Primary tweaks come before secondary ones. `Beam Color` (what color) before `Beam Width` (how thick) before `Emission Intensity` (brightness multiplier).

**Description scope prefix.** The description string (fourth argument to `Config.Bind`, or the first argument to `ConfigDescription`) starts with `(Client-local)` or `(Server-authoritative)` matching the section scope. BepInEx writes the description as a comment in the generated `.cfg` file, so power users editing the file directly still see the scope at a glance.

**Section renames reset player values.** BepInEx treats the `(Section, Key)` pair as an entry's identity; changing the section string on an existing entry causes BepInEx to re-seed the entry with the default on next launch because the old stored value is orphaned. When a section rename is worth the UX gain despite the reset, note it in the mod's `<ChangeLog>` entry so players are not surprised. For minor cosmetic renames, prefer keeping the existing section.

## Content: no developer-specific paths

Committed files must not contain filesystem paths that tie the repository to a specific developer's machine layout. Committed files are public; any path leaked here exposes the author's username, directory habits, or drive partitioning to anyone who reads the repo.

Forbidden:

- User home directory paths with a named user: `C:\Users\<name>\...`, `/home/<name>/...`, `/Users/<name>/...`.
- Personal scratch or temp directories tied to a specific user: `C:\Users\<name>\AppData\Local\Temp\...`, `/tmp/<developer-tag>/...` when the name is developer-specific.
- Absolute paths to a developer's source tree outside this repo: `C:\Source\...`, `D:\Projects\...`.
- Editor / IDE install paths that pin a specific version on a specific drive (e.g. `C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe`).
- Game install paths with a specific drive letter hardcoded into source, `README.md`, `About.xml`, or `RESEARCH.md` (use `$(StationeersPath)` or describe as "the Stationeers install" instead).

Allowed:

- Repository-relative paths: `Mods/SprayPaintPlus/`, `../../LICENSE`, `tools/decode_il.ps1`.
- Paths in `DEV.md.template` that use angle-bracket placeholders: `<STATIONEERS_INSTALL>`, `<YOUR_NAME>`, `<USER_DOCUMENTS>`, `<MSBUILD_PATH>`, `<REPO_CLONE_PATH>`, `<YOUR_SOURCE_ROOT>`.
- MSBuild property references: `$(StationeersPath)\rocketstation_Data\...`.
- Environment-variable references: `%USERPROFILE%`, `%APPDATA%`, `~/`.
- Third-party URLs (Steam Workshop, GitHub, HuggingFace, etc.).

Scope: every committed file. `README.md`, `RESEARCH.md`, `TODO.md`, `PLAN.md`, `plan.md`, `CLAUDE.md`, `NOTES.md`, `About.xml`, `.csproj`, `.sln`, `.cs`, commit messages. The rule applies equally to `Mods/` and `Plans/`. Not covered: `DEV.md` (gitignored) and `DEV.md.template` (the template that documents the placeholders).

When a document needs to reference developer-specific context, use a placeholder from `DEV.md.template` or describe the target abstractly ("the local Stationeers deploy folder documented in `DEV.md`").

## Licensing: Apache 2.0

This monorepo is licensed under **Apache License 2.0**. Required files and content:

- `LICENSE` at the monorepo root contains the full, unmodified Apache 2.0 license text.
- `NOTICE` at the monorepo root names the copyright holder (SixFive7) and the project. Redistributors must preserve it per Apache 2.0 section 4.
- Each mod's `README.md` ends with a `## License` section stating the mod is Apache 2.0 and linking to the root `LICENSE` file.
- Each mod's `About.xml` `<Description>` ends with an `[h2]License[/h2]` section stating Apache 2.0 and linking to https://github.com/SixFive7/StationeersPlus/blob/master/LICENSE. The `<InGameDescription>` does not need this section (space is tight).

Apache 2.0 is chosen over MIT because section 4(b) requires redistributors to state significant changes they made, which discourages unmodified repackaging without attribution. Do not swap in a different license (MIT, GPL, CC, etc.) without explicit instruction.

## Release workflow: version bumps, tags, and commit hygiene

Every released mod in `Mods/` follows the same release workflow. `Plans/` mods do not tag releases.

**Rule 1: one mod per release commit.** A release commit bumps exactly one mod's version. Never bump two mods' `<Version>` fields in the same commit. Cross-mod refactors are fine but are separate from release commits and do not bump `<Version>`. Tags point at commits, not subtrees; a commit that ships two mods cannot be tagged twice without ambiguity about what "version" the tag represents.

**Rule 2: a release commit is exactly these edits, nothing else.**

- `Mods/<ModName>/<ModName>/<ModName>.cs` (or `Plugin.cs`): `PluginVersion` bump.
- `Mods/<ModName>/<ModName>/About/About.xml`: `<Version>` bump and new top-of-`<ChangeLog>` entry.

Every release commit touches those two files and nothing else. Feature work goes in prior commits.

**Rule 3: always tag a release commit.** After creating the release commit, tag it with an annotated tag in the format `mods/<ModName>/v<X.Y.Z>` and push the tag:

```
git tag -a mods/SprayPaintPlus/v1.4.2 -m "SprayPaintPlus 1.4.2"
git push origin mods/SprayPaintPlus/v1.4.2
```

The tag is the source of truth for "what shipped as 1.4.2." Workshop can be rolled back or updated; tags cannot.

**Rule 4: never move a pushed tag.** If a release was mis-tagged or needs a hotfix, ship a new patch version (1.4.3) rather than moving v1.4.2.

**Rule 5: release commit message format.** Release commits use this exact subject line, for grep-ability:

```
<ModName> v<X.Y.Z>: <short summary>
```

Example: `SprayPaintPlus v1.4.2: fix paint bucket deprecated-property warning`.

**Rule 6: tags only for Mods/, never Plans/.** `Plans/` mods are WIP. They don't get version tags until they graduate to `Mods/` with a real release. The first release of a promoted mod starts at v1.0.0.

## Style: no AI tells in committed text

Every file that lands in this repository, user-facing or not, must not contain the visual and stylistic tics that flag AI-generated writing. Committed files are the public face of the project; visible tells invite skepticism that overshadows the work. Covered scope: `README.md`, `RESEARCH.md`, `TODO.md`, `LICENSE`, `NOTICE`, `About.xml` in every element (including `<ChangeLog>`), Workshop comment replies, in-game strings, code comments, and commit messages.

Hard rules:

- No em dashes (`—`) or en dashes (`–`) as sentence punctuation. Use commas, colons, semicolons, parentheses, or periods. This applies even inside quoted examples, BBCode, and Unity rich text.
- No ellipsis character (`…`). Use three periods (`...`).
- No curly quotes (`"` `"` `'` `'`). Use straight quotes (`"` and `'`).

Soft rules:

- Skip throat-clearing ("it's worth noting", "in conclusion", "furthermore", "moreover").
- Skip LLM buzzwords ("delve", "leverage", "unlock", "seamless", "robust", "empower", "comprehensive") when a plain word fits.
- Don't bold random nouns for emphasis. Bold is for section labels and setting names.
- Prefer one short sentence over a three-part list of near-synonyms.
- Terse, matter-of-fact tone. Real modders write like changelogs, not marketing copy.

These rules do not apply to `DEV.md` (gitignored) or any conversational scratch space that stays out of the repo. `CLAUDE.md` is committed but documents internal development conventions rather than user-facing content; the hard rules still apply (no em dashes, no curly quotes, no ellipsis character) to avoid visible stylistic tells if a reader browses the repo, but the soft tone rules are relaxed for technical documentation.

## Documentation file layout

| File | Location | Audience | Contents |
|---|---|---|---|
| `README.md` | Each mod's folder root (committed) | End users (GitHub readers, Workshop subscribers) | Feature overview, installation, settings tables, issue reporting, credits, license. Source of truth for user-facing content, mirrored into `About.xml`. |
| `RESEARCH.md` | Each mod's folder root (committed) | Someone picking up the mod for the first time | Durable, project-scoped internals: architecture, file walkthroughs, patch catalog with formulas, decompiled game internals, multiplayer protocol, pitfalls, design decisions with rationale. No session state, no developer-specific paths. |
| `DEV.md` | Monorepo root, gitignored | The developer | Machine-specific paths (game install, deploy target, MSBuild location, log paths, sibling-mod source dirs), workflow recipes, tooling inventory, per-developer collaboration preferences. Shared across every mod. |
| `DEV.md.template` | Monorepo root, committed | A new contributor | Scaffold for `DEV.md`. Structure and section headers filled in; machine-specific values are placeholders. Copy to `DEV.md` and fill in. |

Required setup:

- `README.md` and `RESEARCH.md` are committed per mod and kept in sync with the source when features or internals change.
- `DEV.md` at the monorepo root is gitignored. `DEV.md.template` at the monorepo root is the committed scaffold a new contributor copies to create their own `DEV.md`.
- No other long-form knowledge files (`plan.md`, `NOTES.md`, session logs) should accumulate in committed form inside `Mods/`. Use `RESEARCH.md` for durable knowledge, `TODO.md` for pending work, and git history / conversation for everything else. `Plans/` mods may carry such files since the work-in-progress phase is where they have value; they consolidate into `RESEARCH.md` or are deleted when a mod graduates to `Mods/`.

## Workflow: read the relevant Research before touching any mod

Before doing any work on a mod (code changes, debugging, feature additions, refactors, content edits, build changes), read that mod's `RESEARCH.md` in full if it exists. The mod's `RESEARCH.md` carries mod-specific architecture, design decisions, and a pointer list into central `Research/` pages. That pointer list is the index into the wider knowledge base: each entry names a central page and the one-line reason this mod cares. Follow pointers selectively to the central pages relevant to your task.

Rules:

- The very first read for any mod task is that mod's `RESEARCH.md`. Paths are `Mods/<ModName>/RESEARCH.md` for released mods and `Plans/<ModName>/RESEARCH.md` for work-in-progress. Do this before grepping source, before opening `.cs` files, before planning.
- For each pointer in the mod's `RESEARCH.md`, decide whether to follow it. If you choose not to follow a pointer, justify the skip in one sentence (forces a deliberate decision rather than a silent omission).
- For tasks that span multiple mods, repeat the process for each affected mod.
- If a pointer is broken, or the mod clearly depends on a topic that has no central page yet, flag it and proceed. Do not block.
- After completing work that changes architecture, patch behavior, or invalidates the mod's own `RESEARCH.md`, update it. Game-internals changes go to central `Research/` per Rule 2, not to mod-local files.
- This rule applies to sub-agents too. When delegating a mod task, instruct the sub-agent to read the mod's `RESEARCH.md` first and to follow its pointers as needed.

## Workflow: Research curation is mandatory on every decompiled-code touch

When you read decompiled game code from `$(StationeersPath)\rocketstation_Data\Managed\` (the hook will remind you) or produce a finding about game internals (class behavior, method signature, side-effect, multiplayer message format, save-format detail, Harmony pitfall, transform math, any similar fact), you MUST create or update the matching page under `Research/` in the same response. Do not postpone. Do not park the finding in a commit message or a code comment. Do not write it into a mod's `RESEARCH.md` when the fact is not mod-specific.

Central pages live under `Research/<category>/<page>.md`. The category list, per-category scope, and routing rules live in `Research/CLAUDE.md`. Read it before creating a new page.

The hook injects the current game version on every `Research/` read and write; use that value for the frontmatter `created_in` and `verified_in` fields and for every section-level stamp. Do not guess the version.

Requirements on every touch:

- If the page does not yet exist, create it with full YAML frontmatter (see `Research/CLAUDE.md` for the schema), source citations pointing at the DLL path and any originating `RESEARCH.md` line range, and section-level `<!-- verified: <version> @ <YYYY-MM-DD> -->` stamps after each H2 (and after H3 where the finer granularity matters).
- If the page exists and a section's content changes or is re-confirmed, update that section's stamp to the current game version and date. Cosmetic edits (typos, formatting) do not restamp.
- If the finding does not fit any of the five established categories, put it in `Research/Unsorted/<descriptive-name>.md` with full lossless content AND append an entry to the root `TODO.md` in this format: `- [ ] Research/Unsorted: classify Research/Unsorted/<filename>.md (originally from <source>)`. The Unsorted protocol and TODO format are spelled out in `Research/CLAUDE.md`.
- The lossless principle governs every central page: verbatim code excerpts, formulas, hex layouts, and exact field / method names carry forward untouched. No summarization that drops detail. When two existing sources overlap, preserve both until explicitly reconciled.

## Workflow: validate new lessons independently, then persist to central Research

Decompilation and reverse-engineering are expensive. Any non-obvious finding must land in central `Research/`, not in commit messages, conversation scratch, mod-local files, or per-session planning docs.

Validation protocol:

- Sub-agents doing research or debugging must report every new lesson, not just their final answer. The coordinating conversation is responsible for collecting those lessons and routing them into `Research/`.
- Adding new content to a central page does not require a second agent. Additive content cannot contradict existing content.
- Changing or removing existing verified content on a central page DOES require a fresh validator. Before persisting a lesson that contradicts what is already on the page, spawn a fresh sub-agent with no exposure to your reasoning, conversation history, or framing. Give it the raw question and the two conflicting source extracts (decompiled code, DLL path, other mods' assemblies, original `RESEARCH.md` line ranges). Instruct it not to defer to either existing claim. Its verdict is binding. The prompt template, source-extract format, and verdict-application rules live in `Research/CLAUDE.md` under "Conflict-resolution prompt template."
- Record every conflict and its resolution in the target page's "Verification History" section (append-only): date, what was contradicted, fresh-agent verdict, resulting change. Genuinely unresolved cases (fresh agent could not determine ground truth) go to the page's "Open Questions" section and escalate to the user.
- Speculation and "probably" claims do not go into central pages. Only verified, sourced findings land there. Unverified hypotheses stay in conversation or in a `Plans/<Mod>/` stub, never in `Research/`.
- This rule applies to sub-agents too. When a sub-agent produces a finding that contradicts an existing page, the calling agent spawns the fresh validator before persisting.

## Style: write like a human, not like an AI was here

Committed files should read like a developer who cares about the project wrote them, not like they came out of an AI chat window. This is a style preference, not a strict prohibition: hiding every possible mention of AI is neither realistic nor the point. A reader browsing the repo should see craftsmanship, not filler.

What this means in practice:

- Prefer tight, specific language over meandering abstractions.
- Don't casually credit AI in commit messages, README intros, or release notes. Phrases like "generated by", "with the help of Claude", or "ultrathink" in committed text are the tell that makes a repo feel auto-written. Avoid them.
- Don't lean on the soft-vocabulary AI-writing tics ("delve", "leverage", "unlock", "seamless", "robust", "empower", "comprehensive") when a plain word fits. The hard/soft style rules in the earlier "no AI tells" section cover this in more detail.
- When documentation addresses "the person reading this next", use phrasing like "a developer", "a contributor", or simply drop the audience framing.

Legitimate exceptions where naming AI tools is fine:

- Technical references where the tool is the actual subject. A `.gitignore` comment explaining what `.claude/` is. A toolchain doc mentioning `gh`, `ilspycmd`, or `CLAUDE.md` by name. These are factual, not promotional.
- Mod content that is itself about AI or language models. The `LLM` mod under `Plans/LLM/` is a local language model integration; its README, `RESEARCH.md`, and `About.xml` describe the mod in its own domain language ("a local language model", "inference threads", "GGUF model"). That is the subject matter, not a stylistic tell.
- `CLAUDE.md` (this file) and `DEV.md` (gitignored) are exempt; they describe internal development workflow and may reference AI tools, sub-agents, or automation as needed.

Rule of thumb: if a human modder would naturally write the same sentence because the topic genuinely requires it, it is fine. If the sentence only exists because an AI likes that phrasing, rewrite it.

## Tool: InspectorPlus for live runtime state

`InspectorPlus` is a local-only BepInEx plugin in this repository that dumps live game state to JSON on demand while the game is running. Use it to read actual field and property values of scene objects at a specific moment instead of guessing or adding one-off logging.

Two triggers:

- Drop a request JSON into `<StationeersInstall>\BepInEx\inspector\requests\` specifying types and fields to inspect. The plugin writes a snapshot into `...\inspector\snapshots\snapshot_<timestamp>.json` and deletes the request. This is the programmatic path (drop the file, then read the result).
- The user can press F8 in-game to dump every MonoBehaviour to the same snapshots folder.

Full schema, output format, and operational details live in `DEV.md` under "InspectorPlus: live runtime state snapshots." Read that section before using the tool.

Use InspectorPlus when debugging a patch or hypothesis about runtime state, but not as a replacement for logging inside hot Harmony patches.

### Use InspectorPlus to minimize the user's manual testing load

The test loop without InspectorPlus (deploy, launch, load save, perform action, describe behavior to the developer, close game) is the most expensive part of this project. Every round-trip takes minutes. The developer's time is the bottleneck. Reduce what the developer has to do by leaning on InspectorPlus aggressively.

Default posture for any change that affects runtime behavior:

1. **Prepare request files before asking for a test.** The moment a test instruction is handed back, the corresponding `BepInEx\inspector\requests\*.json` files should already be on disk, named for the checkpoint they capture (e.g. `before_link.json`, `after_link.json`, `steady_state.json`). The developer drops each file into the requests folder at the right moment, not hand-typing JSON mid-test.

2. **Prefer snapshots over narrated observations.** If the question is "did field X update, what is the value of Y after the button press, is the partner reference non-null", do not ask the developer to eyeball it. Write a request file, trigger the action, read the snapshot. The developer's role collapses to "load save, do the thing, tell me when done."

3. **Capture before + after, not just after.** A single snapshot can't tell you whether a field changed. For any hypothesis of the form "patch X should update Y to Z", write a paired request (baseline + post-action), diff them after the action is triggered, and report the delta.

4. **Use precise `types` and `fields` filters.** A full-scene dump is cheap to run but noisy to read. If you know which type and which members matter, target them. Broad dumps are a fallback for when there is no hypothesis yet.

5. **Write snapshots to the log too, when it helps.** For events that are hard to catch at a single moment (transient states, inter-frame transitions), emit `Logger.LogInfo(...)` lines from inside patches rather than racing a snapshot. InspectorPlus captures steady state well; short-lived transitions are better logged.

6. **Never require JSON to be pasted back.** Snapshots land on a known filesystem path. Read them after the test is done. The developer should never be in the middle of the data pipeline.

7. **Plan the verification before the implementation.** When designing a patch, name the specific InspectorPlus queries that will prove the patch works. If you cannot describe what to snapshot to verify success, the patch is not well-specified yet.

8. **Clean up after yourself.** After a test session, delete the snapshot files read from `BepInEx\inspector\snapshots\` and any stray request files still sitting in `BepInEx\inspector\requests\`. Snapshots have timestamped filenames and no automatic rotation. Stray request files are worse: if the plugin failed to process one, it will still be picked up the next time the game launches. Leave both folders empty at the end of every session unless a specific snapshot is being preserved for later review.

The target: the developer loads a save, performs whatever minimum in-game actions the test requires, and reports "done." Everything else, observing state, diffing before/after, confirming the hypothesis, is off the snapshot files. Treat any test plan that puts the developer in charge of reading values or describing behavior as a failure of planning and rewrite it.
