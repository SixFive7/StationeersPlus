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

## Content: Reporting Issues section

Every mod's `README.md` and `About.xml` `<Description>` must include a "Reporting Issues" section directing users to the monorepo's GitHub issues page. Steam Workshop comment notifications are unreliable, so bug reports left as Workshop comments often go unseen. Point users at GitHub instead.

- `README.md`: `## Reporting Issues` section with a markdown link to https://github.com/SixFive7/StationeersPlus/issues.
- `About.xml` `<Description>`: `[h2]Reporting Issues[/h2]` section with a BBCode `[url=...]` link to the same URL.
- Issue titles should start with the mod's display name so triage is easy (e.g. `[SprayPaintPlus] ...`).
- `<InGameDescription>` does not need this section (space is tight and the panel is not where players file bugs).

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

## Workflow: read RESEARCH.md before touching any mod

Before doing any work on a mod (code changes, debugging, feature additions, refactors, content edits, build changes), read that mod's `RESEARCH.md` in full if it exists. It documents architecture, patch formulas, decompiled game internals, multiplayer protocol details, known pitfalls, and the rationale behind past design decisions. Skipping it routinely costs hours of rediscovery and produces patches that conflict with constraints already documented there.

Rules:

- The very first read for any mod task is that mod's `RESEARCH.md`. Paths are `Mods/<ModName>/RESEARCH.md` for released mods and `Plans/<ModName>/RESEARCH.md` for work-in-progress. Do this before grepping the source, before opening `.cs` files, before planning.
- If `RESEARCH.md` is missing, note it and proceed; do not block.
- For tasks that span multiple mods, read each affected mod's `RESEARCH.md`.
- After completing work that changes architecture, patch behavior, game-internals understanding, or invalidates anything in `RESEARCH.md`, update `RESEARCH.md` so the next reader gets accurate information.
- This rule applies to sub-agents too. When delegating a mod task, instruct the sub-agent to read the mod's `RESEARCH.md` first.

## Workflow: validate new lessons independently, then persist to RESEARCH.md

Decompilation, reverse-engineering, and deep-dive investigations are expensive (minutes of tool work, often tens of thousands of tokens). Any non-obvious finding surfaced during such work, game internals, IL patterns, UI event plumbing, multiplayer quirks, save-load ordering, Harmony pitfalls, etc., must be captured in the relevant mod's `RESEARCH.md` so the next reader does not repeat the work.

Rules:

- Sub-agents doing research or debugging must report every new lesson learned (not just their final answer). The coordinating conversation is responsible for collecting these lessons.
- Before writing a new lesson into `RESEARCH.md`, validate it with a second, independent sub-agent that has no exposure to the first agent's conclusions. The second agent gets the raw question and sources (decompiled code, game DLLs, other mods' assemblies) and must reach the same conclusion on its own. If the two agents disagree, reconcile with a third pass or by reading the source directly before committing anything to `RESEARCH.md`.
- Once validated, append or update the relevant section of the mod's `RESEARCH.md` (architecture, patch catalog, decompiled game internals, pitfalls, or design decisions, whichever fits). Do this even when the current task did not require the lesson to be documented, if the lesson is durable.
- Do not park durable knowledge in commit messages, conversation scratch, or per-session planning files. Those are ephemeral; `RESEARCH.md` is the single source of truth for each mod.
- Speculation, unverified hypotheses, and "probably" claims do not go into `RESEARCH.md`. Only verified, sourced findings land there.

## Style: no mention of AI or automation inside committed files

Files committed to this repository must not reference AI, agents, automation, large language models, or similar. This includes all committed markdown (`README.md`, `RESEARCH.md`, `TODO.md`, `LICENSE`, `NOTICE`), `About.xml` in all elements, source code comments, commit messages, and PR descriptions.

Reasoning:

- Committed files are the project's public face. Visible AI involvement invites skepticism about quality and authenticity that overshadows the project's actual work.
- The repository history is a durable record; hints of AI involvement cannot be scrubbed later without rewriting history.
- Word targets to avoid: `agent`, `AI`, `LLM`, `Claude`, `ChatGPT`, `language model`, `assistant`, `generated by`, `prompt`, `ultrathink`, `with the help of`.
- When documentation needs to address "the person reading this next", use phrasing like "a developer", "a contributor", "someone working on this", or simply drop the audience framing.

This rule does NOT apply to `DEV.md` (gitignored) or any conversational scratch space that stays out of the repo. `CLAUDE.md` is committed but is an internal development-conventions document; it may reference sub-agents and automation as part of its own workflow guidance.

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
