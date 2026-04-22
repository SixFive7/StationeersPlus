# Contributing to StationeersPlus

StationeersPlus is a monorepo of Stationeers mods (BepInEx + StationeersLaunchPad) plus a central `Research/` knowledge base of decompiled game internals. This document covers how to set up the repo, the Claude Code automation every contributor inherits, and the conventions to follow when adding to the codebase.

## Getting started

1. Clone the repo.
2. Copy `Directory.Build.props.template` to `Directory.Build.props` at the repo root. Set `<StationeersPath>` to your local Stationeers install (the folder containing `rocketstation_Data/Managed/`). The filled-in `Directory.Build.props` is gitignored so each contributor keeps their own path.
3. Copy `DEV.md.template` to `DEV.md` at the repo root and fill in the placeholders. `DEV.md` is gitignored; it holds machine-specific paths, deploy targets, and personal workflow notes.
4. Open any mod solution under `Mods/<ModName>/` or `Plans/<ModName>/` and run `dotnet build -c Release` to verify the build works end-to-end.

## Repository layout

| Path | What |
|---|---|
| `Mods/<ModName>/` | Released mods. Each holds source, `README.md`, `RESEARCH.md`, `About/`. |
| `Plans/<ModName>/` | Work-in-progress mods. Same shape but not tagged or published. |
| `Mods/Template/` | Canonical scaffold for new mods. Also hosts `LAYOUT.md` and `RELEASE.md`. |
| `Research/` | Central knowledge base about game internals, organized by category. |
| `tools/` | Repo-wide utility scripts (decompile helpers, save inspectors, etc.). |
| `.claude/` | Claude Code automation: hook scripts and `settings.json`. Committed. |
| `CLAUDE.md` | Repo-wide conventions loaded into every Claude session. |
| `LICENSE`, `NOTICE` | Apache 2.0. |

## Automation wiring

Work in this repo happens with help from Claude Code. Two mechanisms combine to keep Claude aware of local conventions: scoped `CLAUDE.md` files that auto-load when Claude enters a directory tree, and `PostToolUse` hooks that fire on specific file patterns and inject just-in-time reminders.

Both the hook scripts and `settings.json` are committed to origin (the repo `.gitignore` has `!.claude/settings.json` and `!.claude/hooks/` exceptions), so every contributor's Claude session gets identical behavior automatically.

### CLAUDE.md hierarchy

| File | Loaded when | Covers |
|---|---|---|
| `CLAUDE.md` (repo root) | Every session | Repo-wide conventions: mod naming, abbreviation ban, settings-panel grouping, developer-path ban, licensing, style, pointers to scoped docs. |
| `Research/CLAUDE.md` | Claude touches a file under `Research/` | Research page structure: frontmatter schema, section-level stamps, Verification History conventions, Unsorted protocol, canonical tag vocabulary, InspectorPlus citation convention. |
| `Plans/<Plan>/CLAUDE.md` | Claude touches a file in that plan's folder | Per-plan extensions. Example: `Plans/MaintenanceBureauPlus/CLAUDE.md` carries model-file rules for that plan. |

### Scoped docs (hook-triggered)

Rules that apply to specific classes of edits live in dedicated non-CLAUDE.md files. Hooks fire only on matching file paths and inject a short reminder pointing at the doc; Claude reads the doc on demand if it has not already this session. This keeps repo-root `CLAUDE.md` lean while still guaranteeing the rule is visible when it matters.

| Scoped doc | Hook script | Fires on | Content |
|---|---|---|---|
| `Mods/Template/LAYOUT.md` | `mod-content-hook.ps1` | Read / Edit / Write of `Mods/*/README.md`, `Plans/*/README.md`, `**/About/About.xml` | Mod README + About.xml layout: content sync, tagline rule, ChangeLog format and size cap, About.xml element order, XML-escape safety, per-element size caps, Reporting Issues section, `<line-height>` wrap, preview image dimensions. |
| `Research/WORKFLOW.md` | `research-hook-decompile.ps1` | Read / Grep / Glob under `**/rocketstation_Data/Managed/**` | The three research workflow triggers: read mod `RESEARCH.md` first, curate decompiled findings into `Research/<category>/`, fresh-validator protocol for conflicts. Includes the full conflict-resolution prompt template. |
| `Mods/Template/RELEASE.md` | `release-hook.ps1` | Edit / Write of `Mods/*/*/Plugin.cs` | Release commit rules: one mod per commit, exactly `Plugin.cs` + `About.xml` touched, tag format `mods/<ModName>/v<X.Y.Z>`, commit message format. |

Two additional hooks run inside `Research/` itself and inject context that is always useful when writing there:

| Hook script | Fires on | Injects |
|---|---|---|
| `research-hook-read.ps1` | Read under `**/Research/**` | Current game version (for `created_in`, `verified_in`, and section stamps). |
| `research-hook-write.ps1` | Edit / Write under `**/Research/**` | Write backstop reminder to match the stamp version and append to Verification History when content changes. |

Hook scripts live at `.claude/hooks/*.ps1`. Registration lives in `.claude/settings.json`. The version-helper `.claude/hooks/get-game-version.ps1` is shared by the research read / write / decompile hooks.

### How a session flows

When Claude reads, edits, or writes a file:

1. Every `PostToolUse` entry in `.claude/settings.json` whose `matcher` (tool name) AND `if` (file-path glob, using permission-rule syntax) both apply runs its hook script. Each hook writes a JSON payload injected back into the conversation as a `system-reminder`. Multiple hooks can fire on a single action and are independent; one failing does not stop the others.
2. Any scoped `CLAUDE.md` file whose directory contains the touched file is injected as additional context, above and beyond the always-loaded repo-root `CLAUDE.md`.
3. Claude sees the reminders inline with the tool result and acts on them immediately. For reminders that point at a scoped doc (`LAYOUT.md`, `WORKFLOW.md`, `RELEASE.md`), Claude reads the doc on demand the first time it is needed in the session.

### Adding a new rule-set

When a cluster of rules grows large enough that it no longer earns its place in repo-root `CLAUDE.md`:

1. Write the rule doc next to its canonical example (usually under `Mods/Template/` or `Research/`).
2. Add a PowerShell hook script under `.claude/hooks/` that injects a short reminder pointing at the doc.
3. Register a matcher entry in `.claude/settings.json` with a precise `if` glob so the hook fires only on the intended file patterns.
4. Leave a short pointer section in repo-root `CLAUDE.md` so contributors reading top-down still find the rule.

## Conventions

### Style

No em dashes or en dashes, no ellipsis character, no curly quotes. No LLM buzzwords (`delve`, `leverage`, `seamless`, `robust`, `empower`, `comprehensive` when a plain word fits). Terse matter-of-fact tone. Full rules in repo-root `CLAUDE.md` under the `Style:` sections.

### Commits

Small, focused commits with `<Topic>: <short summary>` subject lines (examples: `SprayPaintPlus: side-car glow persistence`, `Research/GameSystems: verify SaveZipExtension against live in-game test`). Release commits are separate from feature commits; their exact scope is in `Mods/Template/RELEASE.md`.

### Mod README and About.xml

Mirrored across three surfaces (GitHub `README.md`, Workshop `<Description>` BBCode, in-game `<InGameDescription>` TMP rich text). Full rules in `Mods/Template/LAYOUT.md`.

### Releasing

One mod per release commit; exactly `Plugin.cs` + `About.xml` touched; annotated tag `mods/<ModName>/v<X.Y.Z>`. Full rules in `Mods/Template/RELEASE.md`.

### Research

Before touching any mod, read `Mods/<ModName>/RESEARCH.md`. Every decompiled-code finding lands in `Research/<category>/` on the same turn. See `Research/WORKFLOW.md` for the three triggers and the conflict-resolution validator protocol.

### Branches and pull requests

Work on feature branches off `master`. Open a pull request when ready for review. Release commits can land directly on `master`. The repo has no CI on mods yet; builds are verified locally.

## Reporting issues

Open issues at https://github.com/SixFive7/StationeersPlus/issues. Title prefix the mod name (e.g. `[SprayPaintPlus] bug with glow paint on load`) so triage is easy. Steam Workshop comment notifications are unreliable, so GitHub is the correct channel.

## License

Apache 2.0. See `LICENSE` for full text, `NOTICE` for attribution. Contributions are accepted under the same license.
