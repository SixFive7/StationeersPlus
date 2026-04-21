# Mod layout: README.md and About.xml rules

Canonical reference for README.md and About.xml layout conventions across every mod in this monorepo. When editing any `Mods/<ModName>/README.md`, any `Plans/<ModName>/README.md`, or any `About.xml` under those folders, apply every rule below.

A PostToolUse hook fires on Read / Edit / Write against these file paths and injects a reminder that points here. This file is the source of truth; repo-root `CLAUDE.md` no longer duplicates its contents.

## Scope

Covered here:

- README.md structure and tone
- About.xml `<Description>` (Workshop BBCode)
- About.xml `<InGameDescription>` (Unity TMP rich text)
- About.xml `<ChangeLog>` (plain text)
- About.xml element order and XML-escape rules
- About.xml per-element size caps
- Reporting Issues section placement
- InGameDescription line-height compact layout
- Preview image dimensions and file locations

Not covered here (stays in repo-root `CLAUDE.md`):

- Mod naming conventions (folder names, `.csproj`, code identifiers)
- Abbreviation ban (mods and dependencies)
- Settings-panel grouping and ordering (`Config.Bind` sections in `.cs`)
- No developer-specific paths
- Licensing, release workflow, repo-wide style

## Keep README and About in sync

User-facing content lives in three mirrored places, each with its own markup. When features or settings copy change, update all three.

- `README.md` is the source of truth. Markdown, rendered on GitHub. Full-length.
- `About.xml` `<Description>` is the Steam Workshop mirror. BBCode (`[h1]`, `[b]`, `[list][*]`, `[url=...]`). Compressed from the README, not a literal translation, but the same sections and wording where they fit.
- `About.xml` `<InGameDescription>` is the in-game mod-settings panel. Unity rich text (`<b>`, `<size>`, `<color>`). Tighter still, feature-list style.

## Taglines in sync across three surfaces

Every mod has one canonical tagline: a single sentence describing the mod in its current feature-complete state. The same sentence appears verbatim, adjusted only for the surface's markup, in three places:

- GitHub repo description (plain text, shown under the repo name on github.com).
- `About.xml` `<Description>` opening line (BBCode, rendered on the Steam Workshop listing page beneath the `[h1]` title).
- `About.xml` `<InGameDescription>` subtitle (Unity rich text, rendered in the in-game mod settings panel beneath the sized/coloured title line).

When the feature set changes, update all three. Short-form abbreviations and marketing-style rewrites that drift between surfaces are not allowed; the tagline is one sentence, identical content, three encodings.

## Changelog lives in About.xml only

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

## About.xml structure and XML safety

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

## About.xml element size caps

Steam Workshop enforces per-element size limits on `About.xml`. Exceeding a cap causes Steam to truncate the value mid-BBCode on the Workshop listing page and in the in-game mod browser, which visibly breaks the rendered layout even though the underlying BBCode is valid.

Caps per element:

- `<Description>`: 8000 characters including BBCode markup. Count the UTF-8 content inside the element, not the wrapping `<Description>...</Description>` tags.
- `<ChangeLog>`: 8000 characters (also stated in "Changelog lives in About.xml only").
- `<InGameDescription>`: no hard cap observed, but keep it short enough to read in the tight in-game settings panel.

Verify `<Description>` content size on every edit that touches it:

```bash
awk '/<Description>/{flag=1; sub(/.*<Description>/,"")} /<\/Description>/{sub(/<\/Description>.*/,""); print; flag=0; exit} flag' About.xml | wc -c
```

Target at most 7900 characters to leave room for future additions. Cap violations are invisible in a source diff; they only manifest as a broken Workshop listing, so the check must be run proactively.

When `About.xml` size constraints and `README.md` clarity conflict, `README.md` is the long-form source of truth; `<Description>` is the compressed Workshop mirror per the "Keep README and About in sync" rule above. Trim prose in the Description first (explanatory paragraphs, long parentheticals, redundant qualifiers) before cutting sections or setting entries players need.

## Reporting Issues section

Every mod's `README.md` and `About.xml` `<Description>` must include a "Reporting Issues" section directing users to the monorepo's GitHub issues page. Steam Workshop comment notifications are unreliable, so bug reports left as Workshop comments often go unseen. Point users at GitHub instead.

- `README.md`: `## Reporting Issues` section with a markdown link to https://github.com/SixFive7/StationeersPlus/issues.
- `About.xml` `<Description>`: `[h2]Reporting Issues[/h2]` section with a BBCode `[url=...]` link to the same URL.
- Issue titles should start with the mod's display name so triage is easy (e.g. `[SprayPaintPlus] ...`).
- `<InGameDescription>` does not need this section (space is tight and the panel is not where players file bugs).

## InGameDescription compact line spacing

StationeersLaunchPad renders `<InGameDescription>` through TextMeshPro with a default line-height that leaves visible gaps between every line. The in-game mod panel is a short scrollable area, and any description longer than a tagline gets pushed past the visible window because of the wasted vertical space. Wrap the body of `<InGameDescription>` (everything after the title line) in a `<line-height=X%>...</line-height>` tag to compress the prose-line advance.

Canonical shape:

```xml
<InGameDescription><![CDATA[<size="30"><color=#ffa500>Mod Name</color></size>
<line-height=40%>Tagline sentence here.
<b>Features:</b>
- Bullet one
- Bullet two
<b>Settings:</b> ...
<b>Requires:</b> ...
<b>Credits:</b> ...
</line-height>
]]></InGameDescription>
```

Rules:

- The `<size>` + `<color>` title line stays OUTSIDE the `<line-height>` wrap so it keeps the default taller line-height and separates visually from the body.
- Tested working value is `<line-height=40%>`. Values above roughly 55 percent leave enough air that the tag stops earning its place.
- `<line-height>` only affects prose text lines (the tagline, `<b>...</b>` headers, trailing sentences). It does NOT affect bullet-to-bullet spacing. Live testing in Stationeers 0.2.6228.27061 varied the value from 0 to 100 percent and also tried a nested `<line-height>` wrap around the bullet list itself; none of it changed the visual gap between `- bullet` lines. Accept the default bullet density. Do not add nested `<line-height>` wraps around bullets trying to force compression; they have no effect and add noise to the source.
- Do not use blank lines anywhere inside `<InGameDescription>`. Section headers marked with `<b>` already carry enough visual weight; a blank line on top of any spacing reintroduces the airy layout this rule exists to prevent.
- `<line-height>` is a Unity TMP tag. It has no effect on the Workshop `<Description>` (which is BBCode); the tag would render as literal text if it appeared there. Scope: in-game panel only.
- Applies to every mod that ships an `<InGameDescription>`, including `Mods/Template/` as the canonical scaffold. Released mods not yet wrapped pick up the rule on their next release cycle.

## Preview image dimensions

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
