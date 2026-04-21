# Research folder conventions

This file applies when reading or writing any file under `Research/`. It is loaded in addition to the repo-root `CLAUDE.md`; the rules below do not restate repo-wide conventions (naming, style, licensing). They cover the scoped rules needed to keep the central knowledge base correct.

## Scope

Any file under `Research/` (central pages, `Research/INDEX.md`, `Research/Unsorted/`). Mod-local `RESEARCH.md` files under `Mods/` and `Plans/` are not in scope.

## Lossless principle

Central pages preserve source content verbatim. Do not summarize, paraphrase, or silently deduplicate. Code excerpts, formulas, hex layouts, enum values, method signatures, and exact field names carry forward untouched. When two existing sources overlap, keep both with their provenance until a conflict-resolution pass reconciles them explicitly; never drop one to "clean up."

Prose around excerpts (section intros, transition sentences) may be tightened for clarity, but the factual payload is verbatim.

## Frontmatter schema

Every central page starts with YAML frontmatter:

```
---
title: <human-readable name, usually matches game class or pattern>
type: <category: GameClasses | GameSystems | Patterns | Protocols | Workflows | Unsorted>
created_in: <game version when page was created>
verified_in: <game version of most recent section verification>
verified_at: <YYYY-MM-DD of most recent verification>
sources:
  - <DLL path or original RESEARCH.md path with line range>
  - <additional sources as needed>
related:
  - <relative link to related central page>
tags: [<optional searchable terms from the canonical vocabulary>]
---
```

Pages in `Research/Unsorted/` still carry full frontmatter; `type: Unsorted`.

## Section-level stamps

Immediately after each H2 heading (and after H3 where finer granularity matters), place a stamp comment:

```
## <Section heading>
<!-- verified: <version> @ <YYYY-MM-DD> -->

<content>
```

The hook injects the current game version every time a file under `Research/` is read, written, or edited. Copy the injected version verbatim; do not guess.

## When to stamp, when not to

- Page creation: mandatory. All stamps get the current version + date.
- Section content changed or re-confirmed against the current game DLL: mandatory restamp of that section plus the top-level `verified_in` and `verified_at`.
- Cosmetic edits (typo, formatting, link fix, re-wording that does not alter the factual claim): no restamp.
- When the game version advances and a section is re-read and confirmed still correct: restamp with the new version and the current date. This is how stale-stamp staleness gets resolved.

## Conflict resolution

When a new finding contradicts verified content already on a central page, spawn a fresh sub-agent using the protocol and prompt template in `Research/WORKFLOW.md` ("Rule 3: fresh-validator protocol when a new finding contradicts an existing page"). The validator's verdict is binding. Record the resolution in the page's Verification History section (see conventions below).

## Verification History and Open Questions conventions

- Every page has a `## Verification history` section at the bottom. Entries are append-only. Dated entries record page creation, conflict resolutions, and re-verification passes ("2026-04-20: re-read against v0.2.6230; all sections still match"). Do not rewrite past entries.
- Every page has a `## Open questions` section after Verification History. Use it only for genuinely unresolved items (fresh validator could not determine ground truth, or a finding depends on future game behavior). Speculation and "probably" claims do not belong anywhere else on the page; Open Questions is where they live until resolved.

## Unsorted protocol

When a finding does not fit `GameClasses / GameSystems / Patterns / Protocols / Workflows`, create `Research/Unsorted/<descriptive-name>.md` with:

- Full YAML frontmatter, `type: Unsorted`.
- Full lossless content (same rules as any other central page).
- A `sources:` entry pointing at the original RESEARCH.md or DLL path.

Then append an entry to the root `TODO.md` in exactly this format:

```
- [ ] Research/Unsorted: classify Research/Unsorted/<filename>.md (originally from <source>)
```

The TODO entry stops the misfit from vanishing. A later pass either relocates the page to one of the five categories (and crosses off the TODO) or proposes a new category if the pattern of misfits justifies one.

## Canonical tag vocabulary

Tags are free-form but drawn from the canonical vocabulary below when a match exists. New tags are added here when they prove useful across multiple pages. A page can carry 1-4 tags.

Reference material:
- `power` - wireless power, wiring, batteries, solar, power tick
- `logic` - LogicType, ProgrammableChip, IC10, logic readable / writable
- `ic10` - IC10 assembly, instruction set, script internals
- `chat` - chat messages, console, tagline
- `network` - NetworkManager roles, INetworkMessage, MessageFactory, client / server flow
- `launchpad` - StationeersLaunchPad / LaunchPadBooster plumbing
- `damage` - DamageState, damage channels, repair, decay
- `entity` - Human, OrganBrain, life tick, vital stats
- `equipment` - visors, tablets, cartridges, slots
- `slots` - Thing slot system, parent / child containment
- `prefab` - prefab cloning, Mirrored Devices, SourcePrefabs
- `save-format` - on-disk binary layouts (save ZIP, terrain.dat)
- `save-edit` - offline save editing workflows
- `save-load` - runtime save / load ordering, OnFinishedLoad
- `terrain` - Voxel, VoxelOctree, ReadOnlyOctree, rooms
- `stationpedia` - StationpediaPage, UniversalPage, search, markup
- `spa` - Stationpedia Ascended integration
- `ui` - canvas, prefab-driven UI, ChangeDisplay

Patterns / techniques:
- `harmony` - Harmony / HarmonyX rules, patch ordering, attribute traps
- `threading` - main thread, ThreadPool, MainThreadDispatcher, FileSystemWatcher
- `unity` - Unity fake-null, component lifecycle, DontDestroyOnLoad
- `packaging` - ILRepack, embedded libraries, per-mod-copy static state
- `python` - Python tooling (terrain_reset.py, analyzers)
- `worldgen` - trade markers, landers, world setup
- `timeskip` - in-world time skip, fast-forward
- `llm` - local language model integration
- `dead-end` - investigations preserved as "do not repeat this"
- `transforms` - transform hierarchy, rotation math, aim geometry

Add new tags here when they repeat across two or more pages.

## InspectorPlus citation convention

Snapshots are evidence, not durable artifacts; the repo-root `CLAUDE.md` requires deleting them at session end. Central pages cite the request pattern, not the snapshot file path:

Good:

```
Verified via InspectorPlus on 2026-04-20 in game version 0.2.6228.27061. Request: types=[PowerTransmitter], fields=[_linkedReceiver, _linkedReceiverDistance, _powerProvided].
```

Bad:

```
See BepInEx/inspector/snapshots/snapshot_20260420_1703.json.
```

Another developer reading the page months later cannot open a snapshot file that was deleted at the end of the original session. The request pattern is reproducible: drop the same request JSON, get an equivalent snapshot.

## Common lookups

Reserved for future use. When a handful of pages become the most-hit entries across the knowledge base, a small shortcut table may live here so agents do not have to scan `INDEX.md` every time. Empty at migration.
