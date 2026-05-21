# Mod layout: README.md, CHANGELOG.md, and About.xml rules

Canonical reference for README.md, CHANGELOG.md, and About.xml layout conventions across every mod in this monorepo. When editing any `README.md`, `CHANGELOG.md`, or `About.xml` under `Mods/<ModName>/` or `Plans/<ModName>/`, apply every rule below.

A PostToolUse hook fires on Read / Edit / Write of `README.md` and `About.xml` and injects a reminder that points here. (CHANGELOG.md is reached through the release flow in `Mods/Template/RELEASE.md` and through this file.) This file is the source of truth; repo-root `CLAUDE.md` no longer duplicates its contents.

## Scope

Covered here:

- README.md structure and tone
- CHANGELOG.md full version history (per mod)
- About.xml `<Description>` (Workshop BBCode)
- About.xml `<InGameDescription>` (Unity TMP rich text)
- About.xml `<ChangeLog>` (plain text, latest version only)
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

## Changelog: full history in CHANGELOG.md, latest version in About.xml

The changelog lives in two files with different jobs, kept in sync on every release.

- **`CHANGELOG.md` at the mod's folder root** is the full version history: every released version, reverse-chronological, newest at the top. The in-repo source of truth for the complete history. Markdown, rendered on GitHub. Every release prepends a new entry; nothing already shipped is removed.
- **`About.xml` `<ChangeLog>`** is the current version's changes only (what changed since the previous published version). Plain text. Steam attaches it to this one update as the per-update change note and keeps every earlier update's note on the Workshop Change Notes tab on its own, so Steam preserves the per-version history without the mod resubmitting it. Every release replaces this body; it never accumulates. Carrying the whole history here is what eventually drives a long-lived mod past the 8000-character cap.

This split is a hard rule: every mod, every release, every time. The new version goes to the top of `CHANGELOG.md` and replaces the `<ChangeLog>` body in `About.xml`.

### Changelog entry format (both files)

Every entry, in `CHANGELOG.md` and in the `About.xml` `<ChangeLog>`, uses the same shape so the two never drift:

- A heading line naming the version: `v<X.Y.Z>: one-line summary`, sentence case, no trailing period. In `CHANGELOG.md` it is an H2 (`## v<X.Y.Z>: ...`); in `About.xml` it is the same line with no `##`.
- One or more `- ` bullets beneath it. Each bullet is a complete statement that ends with a period.
- Plain ASCII punctuation. No em or en dashes, and no `--` standing in for one; use commas, colons, semicolons, parentheses, or periods (the repo-wide style rule applies here too).
- Optional, used consistently within an entry: a leading category label on a bullet (`NEW:`, `FIX:`, `CHANGE:`) and a trailing `REQUIRES: ...` bullet for version or dependency requirements.
- Encoding is the only thing that differs between the files. `About.xml` is plain text (no Markdown, no BBCode) and XML-escapes any literal `<` or `>` as `&lt;` / `&gt;`. `CHANGELOG.md` is Markdown and wraps tag-like tokens such as `` `<WorkshopHandle>` `` in backticks so they render.

The newest `CHANGELOG.md` entry and the current `About.xml` `<ChangeLog>` body are the same text: same heading words, same bullets, same wording. Only the `## ` prefix, the backticks-vs-escaping, and Markdown-vs-plain-text differ. Write the entry once at release time and place it in both.

Canonical example, the same release in each file.

`CHANGELOG.md`:

```
## v1.4.2: Fix the paint bucket deprecated-property warning
- Replaced the deprecated Color.gamma read with the linear-space accessor; no visible change.
- FIX: Painting a chute no longer logs a null-reference warning on dedicated servers.
- REQUIRES: All players on a server must run 1.4.2 (matching-version handshake).
```

`About.xml` `<ChangeLog>`:

```
v1.4.2: Fix the paint bucket deprecated-property warning
- Replaced the deprecated Color.gamma read with the linear-space accessor; no visible change.
- FIX: Painting a chute no longer logs a null-reference warning on dedicated servers.
- REQUIRES: All players on a server must run 1.4.2 (matching-version handshake).
```

### About.xml `<ChangeLog>` (latest version only)

- Plain text only. Never use BBCode inside `<ChangeLog>`, even though the surrounding `<Description>` element uses BBCode heavily. Workshop renders the change note as plain text: BBCode tags (`[h2]`, `[list][*]`, `[b]`) appear as literal characters.
- Exactly one version block: the version being released and the changes since the previous version. No older entries beneath it. Replace the body every release; never append.
- The submitted change note is capped at 8000 characters. With one version per submission this is no longer a practical worry, but the cap still exists; the size-caps section below has the exact source.

The `<ChangeLog>` body is exactly one version block in the shared entry format above: the heading line with no `##`, then the period-terminated bullets, and nothing beneath it. No `#`, no `[h3]`, no BBCode; line breaks render as line breaks on Workshop.

### CHANGELOG.md (full history)

- One `CHANGELOG.md` per mod at the mod's folder root: `Mods/<ModName>/CHANGELOG.md` for released mods, `Plans/<ModName>/CHANGELOG.md` for work-in-progress. Required for every mod.
- Full history, reverse-chronological, newest at the top. Every released version has an entry. Shipped entries are not rewritten (fix a typo, yes; rewrite the history, no).
- Markdown. Each version is an H2 heading `## v<X.Y.Z>: one-line summary` followed by dash bullets. The newest entry's bullets are the same text as the current `About.xml` `<ChangeLog>` body; only the `##` heading prefix differs.
- On every release, prepend the new version's entry to the top, in the same commit that bumps `<Version>` and rewrites the `About.xml` `<ChangeLog>` (see `Mods/Template/RELEASE.md` Rule 2).

`CHANGELOG.md` shape (each version entry in the shared format above, newest first):

```
# Changelog

Full version history for {{Mod Display Name}}. The newest entry also appears in `About.xml` `<ChangeLog>` and as the latest note on the Steam Workshop Change Notes tab.

## v1.4.2: One-line summary of this version
- A change in this release, written as a complete statement ending with a period.

## v1.4.1: Previous version's summary
- An earlier change, same format.
```

### Version bump and README link

- `About.xml` also carries `<Version>`. On every release, whenever `PluginVersion` in the plugin's main class changes, bump `<Version>`, rewrite the `About.xml` `<ChangeLog>` to the new version's changes, and prepend the new entry to `CHANGELOG.md`.
- `README.md` carries a `## Changelog` section that links to `CHANGELOG.md` (the full in-repo history) and to the Workshop change-notes URL. It does not reproduce the history inline.

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

The size caps below are enforced by StationeersLaunchPad's `Steam.ValidateForWorkshop(ModInfo)` method when you publish a local mod through the StationeersLaunchPad UI. Over-limit content blocks the publish: the validator returns an error, an alert shows the message, and the publish button is disabled. Nothing is truncated, and the base game applies no length cap when it parses About.xml. The numbers match Steam's own Workshop limits, so the validator pre-empts a Steam-side rejection. `<InGameDescription>` is the exception: it has no code-level cap; its guidance number is a visual overflow in the in-game settings panel. Full source detail and the all-element table are in `Research/GameSystems/ModMetadataLimits.md`.

Caps per element:

- `<Name>`: 128 characters. Publish blocked above 128.
- `<Description>`: 8000 characters including BBCode markup. Count the UTF-8 content inside the element, not the wrapping `<Description>...</Description>` tags. Publish blocked above 8000.
- `<ChangeLog>`: 8000 characters (also stated in the changelog section above). Publish blocked above 8000; with the latest-version-only rule this is rarely approached.
- `About/thumb.png`: 1 MB (1048576 bytes), and the file must exist. Publish blocked otherwise.
- `<InGameDescription>`: approximately 1450 characters of CDATA content (markup included) before the body overflows the visible window of the StationeersLaunchPad in-game settings panel. This is a visual overflow, not a hard cap: there is no length check in code. Calibrated empirically against PowerTransmitterPlus: 1545 chars / 15 source lines overshot by one visible line; 1428 chars / 14 source lines fit. The effective ceiling depends on TMP rendering, panel dimensions, and per-bullet wrapping (long bullets that wrap to two visual lines burn the budget twice as fast as short ones), so treat as a guidance number not a hard byte limit. Verify in-game after every edit that adds or grows a bullet.
- `<ModID>`, `<Author>`, `<Version>`, `<Tags>`: no size cap. (`<WorkshopHandle>` must still be a valid numeric ulong; see the XML-safety section above.)

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
