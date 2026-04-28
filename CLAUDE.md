# StationeersPlus Mods

Shared conventions for all Stationeers mods under this monorepo. `Mods/<ModName>/` holds released mods; `Plans/<ModName>/` holds work-in-progress; `tools/` holds repo-wide utility scripts.

## Repository layout

- `Mods/` contains released mods. Each subdirectory is a self-contained mod (own README, RESEARCH, source project, and `About/`).
- `Plans/` contains mods that are in progress and prototypes not yet released. They follow the same shape as released mods but are not tagged or published. Plans/ mods may carry design documents (`PLAN.md`, `plan.md`, `NOTES.md`) that are not permitted in released mods; these documents consolidate into `RESEARCH.md` or are deleted when the mod graduates to `Mods/`.
- `tools/` contains repository-wide utility scripts.
- `Mods/Template/` is the seed scaffold for creating new mods.
- `.work/` at the monorepo root is the gitignored scratch directory. All temp, prototype, and throwaway files written during a work session live here. See "Workflow: scratch and working files in .work/" below.
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

## Content: mod README and About.xml layout

Layout rules for mod `README.md` files and `About.xml` files live in `Mods/Template/LAYOUT.md`. That file is the single source of truth and covers:

- README / Description / InGameDescription content sync
- Canonical one-sentence tagline, mirrored across GitHub, Workshop, and in-game
- `<ChangeLog>` plain-text format, reverse-chronological order, 8000-character cap
- `<About>` element order and XML-escape safety (`<WorkshopHandle>` numeric rule, etc.)
- Per-element size caps (`<Description>` 8000, `<ChangeLog>` 8000)
- "Reporting Issues" section placement in README and Workshop Description
- `<InGameDescription>` `<line-height=40%>` compact wrap
- Preview image 16:9 dimensions and three-file layout (`Preview.source.png`, `About/Preview.png`, `About/thumb.png`)

A `PostToolUse` hook (`.claude/hooks/mod-content-hook.ps1`, wired in `.claude/settings.json`) fires when Claude touches any `Mods/<Mod>/README.md`, any `Plans/<Plan>/README.md`, or any `**/About/About.xml`. The hook injects a reminder pointing at `LAYOUT.md`. Read `LAYOUT.md` before editing any of those files; apply every rule there on top of the naming, abbreviation, settings-grouping, and style rules in this file.

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

## Release workflow

Release commit rules (one mod per commit, exactly Plugin.cs + About.xml touched, annotated tag `mods/<ModName>/v<X.Y.Z>`, commit-message format, no tags for `Plans/`) live in `Mods/Template/RELEASE.md`. A `PostToolUse` hook (`.claude/hooks/release-hook.ps1`) fires on `Edit|Write` of any mod's `Plugin.cs` and injects a reminder pointing there. Read `RELEASE.md` when the reminder surfaces and you are actually cutting a release; ignore the reminder on unrelated plugin-code edits.

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

## Workflow: Research protocols

Three workflow rules govern the boundary between mod work and the central `Research/` knowledge base: read the mod's `RESEARCH.md` before touching any mod, curate decompiled-code findings into `Research/<category>/` on every touch, and apply the fresh-validator protocol when a new finding conflicts with existing verified content on a page. Full rules and the conflict-resolution prompt template live in `Research/WORKFLOW.md`. The decompile hook (`.claude/hooks/research-hook-decompile.ps1`) points at it; read `WORKFLOW.md` when the reminder surfaces or when starting mod work. Structural rules for pages under `Research/` (frontmatter schema, section stamps, Verification History conventions, Unsorted protocol, tag vocabulary) live in `Research/CLAUDE.md` and auto-load when you touch a `Research/` file.

## Workflow: research commits are autonomous, code commits are not

Additions or revisions inside `Research/` (the central knowledge base at the repo root) should be committed without asking, in logical groups, as soon as the research is done and validated per `Research/WORKFLOW.md` and `Research/CLAUDE.md`. This is a deliberate exception to the default "never commit unless explicitly asked" rule, because research findings are durable knowledge that loses value sitting in a working tree: other agents and the user lose access, and branch switches or stash discards can erase it.

Code and other non-research changes are not covered. The user's deliverable in any given session is whatever they asked for outside `Research/`, and the user decides commit boundaries and timing for it. Never commit anything outside `Research/` autonomously, even when it sits in the same working tree as a research commit.

Rules for an autonomous research commit:

- Every staged path is under the central `Research/` directory. All other paths are off-limits to autonomous commits.
- Content satisfies the curation rules in `Research/WORKFLOW.md` (validated, fresh-validator protocol applied where existing verified content is overwritten) and the structural rules in `Research/CLAUDE.md` (frontmatter, section stamps, Verification History, tag vocabulary).
- The validation bar is the same as the section-stamp bar: do not commit a page or section unless you would, at this moment, stamp it as verified. If you would hesitate to stamp it, the commit is premature.
- One coherent unit per commit: a single page, or a closely related set produced together. Not one omnibus commit at session end.
- Stage by explicit paths. Never `git add -A` or `git add .`. Verify with `git status` and `git diff --cached` before each commit that no path outside `Research/` has slipped in.
- Commit message prefix: `Research: <topic>`. Example: `Research: Patterns/StationeersLaunchPadSettingsGrouping initial writeup`.
- Announce each commit in user-facing text (the path(s) committed and the resulting SHA). Autonomous commits are never silent.
- Local only. Do not push.

Session-end flush: before ending a turn that produced validated `Research/` additions, commit them per the rules above. Do not leave validated research uncommitted across the end of a turn. If a finding is not yet ready (incomplete validation, unresolved conflict against existing verified content), say so explicitly in the user-facing wrap-up.

Scope and delegation:

- This rule covers the central `Research/` directory only; `RESEARCH.md` files elsewhere are not central research.
- Sub-agents do not commit. When a sub-agent produces additions under `Research/`, the main agent is responsible for reviewing, packaging into logical groups, and committing them.

A `PreToolUse` hook (`.claude/hooks/research-commit-hook.ps1`, wired in `.claude/settings.json`) matches `git commit` invocations whose message starts with `Research:` and blocks the commit if any staged path is outside `Research/`. The hook turns the "explicit paths only" rule from convention into enforcement.

## Workflow: scratch and working files in .work/

All scratch, temp, prototype, and throwaway files written during a work session live under `.work/` at the monorepo root. The directory is gitignored (see the `.work/` entry in `.gitignore`) so nothing inside is committed. Clustering scratch in one place keeps the repo root tidy, prevents stray artifacts from leaking into commits, and gives a single place to clean out at the end of a session.

**This rule supersedes any user-global temporary-directory convention when working inside this repository.** Other instructions (for example, a personal `~/.claude/CLAUDE.md` that names a different scratch location such as a Downloads folder) do not apply inside this repo's working tree. Use `.work/` here, full stop. The user-global rule applies only when working outside this repository.

Required practice:

- Create `.work/` if it does not exist (`mkdir -p .work/`). It is not tracked, so a fresh clone will not have it.
- Anything an AI assistant or a developer writes that is not part of the released codebase lands inside `.work/`. Examples: throwaway IL dumps, decompile snippets pulled out of `Research/` for ad-hoc inspection, exploratory `.cs` files used to test a hypothesis, request and response captures, scratch scripts, ad-hoc notes that are not destined for `RESEARCH.md` or `TODO.md`.
- Do not write `.tmp_*.cs`, `scratch.txt`, `notes.md`, or other loose temp files at the repo root, inside any `Mods/<Mod>/` subtree, or inside any `Plans/<Plan>/` subtree. If a temp file needs to evoke the source it relates to, name it descriptively inside `.work/` (`.work/PowerTransmitterPlus_pc_dump.cs`, `.work/CombustionDeepMiner_il.txt`).
- `.work/` is not a substitute for the curation rules. Findings about game internals still belong in `Research/<category>/`; durable mod knowledge still belongs in the mod's `RESEARCH.md`. Use `.work/` only for material that is genuinely throwaway.
- Clean up `.work/` at the end of a session, or at least at the end of a coherent task. The directory should not become a graveyard of stale scratch from months ago.

The existing `*.tmp`, `*.bak`, `*.orig` patterns in `.gitignore` continue to catch stray throwaway files anywhere in the tree as a safety net, but `.work/` is the intended home, not a fallback. If you find yourself reaching for `*.tmp` at the repo root, route it through `.work/` instead.

### Decompilation artifacts: .work/decomp/<game-version>/

Any `.cs` file produced by decompiling a Stationeers DLL or any other binary lives at exactly:

`.work/decomp/<game-version>/<source-assembly-name>.decompiled.cs`

Three rules apply, all firm:

1. **Path layout**: the first segment after `decomp/` is the game version string at the time of decompilation (for example, `0.2.5095.21641`, sourced from `version.txt` in the Stationeers install). The next segment is the file name. Encoding the version in the path makes staleness obvious: when the game updates, the old folder is visibly out of date and an agent reading from it knows to re-decompile rather than treat the contents as current.
2. **Suffix**: the file MUST end in `.decompiled.cs`. The `.decompiled.` infix distinguishes derived artifacts from authored source and lets the research-curation hook (`.claude/hooks/research-hook-decompile.ps1`) detect decompiled-content reads anywhere in the tree as a safety net. A bare `.cs` extension is not acceptable for decompiled output.
3. **One version at a time**: when a session detects the game has updated, delete the old `.work/decomp/<old-version>/` folder before producing new decompiles. Do not let multiple version folders accumulate. Past decompiles can always be regenerated from the new DLLs.

Examples (correct):

- `.work/decomp/0.2.5095.21641/Assembly-CSharp.decompiled.cs`
- `.work/decomp/0.2.5095.21641/UnityEngine.UI.decompiled.cs`

Examples (forbidden):

- `C:\Users\jori\Downloads\tmp-dedserv\decomp\Assembly-CSharp.cs` (wrong location, missing version, missing suffix)
- `.work/decomp/Assembly-CSharp.decompiled.cs` (missing version subfolder)
- `.work/decomp/0.2.5095.21641/Assembly-CSharp.cs` (missing `.decompiled.` infix)
- `Mods/SomeMod/Assembly-CSharp.decompiled.cs` (outside `.work/decomp/`)

The decompile-content curation hook fires on Read or Glob against any file under `.work/decomp/` (canonical-path matcher) and against any file ending in `.decompiled.cs` anywhere in the tree (suffix-safety matcher). It also fires on Bash commands that invoke a decompiler (`ilspycmd`, `ICSharpCode.Decompiler`) and on Bash commands whose command text contains a decompile path or suffix (`.work/decomp/`, `*.decompiled.cs`, `rocketstation_Data/Managed/`). The Bash path-substring matchers catch inspection via `cat`, `grep`, `rg`, `head`, `tail`, `xxd`, `strings`, etc. without enumerating each tool. All matchers inject the same Rule 2 reminder pointing at `Research/WORKFLOW.md`. Grep is intentionally not hooked: per the Claude Code source (`GrepTool.preparePermissionMatcher`), Grep `if` rules match against the search regex argument, not the path or glob, so a path-shaped `if` rule on Grep can never fire for normal usage. Reading derived `.cs` content from anywhere else (a Downloads folder, an ad-hoc workspace, any path outside `.work/decomp/`) bypasses curation and is forbidden.

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
