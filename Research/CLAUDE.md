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

## Conflict-resolution prompt template

Spawn a fresh sub-agent when a new finding contradicts verified content already on a central page. The calling agent copies the template below, fills in the three placeholders, and sends it as the sub-agent's entire task prompt. The fresh agent must not receive the calling agent's reasoning, conversation history, or any framing about previous research.

Template:

```
Task: independently answer the question below by reading the cited sources directly. Do NOT defer to either existing claim. Return a short verdict plus a verbatim source quote that backs the verdict.

Question: <bare question framed without bias. State the fact at issue. Do not reveal which of the two claims is currently on the page or which is newly proposed.>

Source A (currently on the central page):
  Claim: <verbatim claim text from the page>
  Provenance: <DLL path plus method name, or original RESEARCH.md path with line range>

Source B (newly proposed):
  Claim: <verbatim claim text from the new finding>
  Provenance: <DLL path plus method name, or original RESEARCH.md path with line range>

Instructions:
1. Read both provenances directly. Do not rely on the claim text.
2. If the sources are decompiled code, read the method bodies in full including any inherited members.
3. Reach an independent conclusion. If neither A nor B matches what the sources actually say, return the correct answer.
4. Return exactly: "A is correct" / "B is correct" / "Neither, correct answer is <X>" plus one verbatim source quote (with provenance) backing the verdict.
```

Worked examples (for illustration only; do not copy into the spawn prompt):

Example 1: Harmony inherited-method `__instance` typing. This is the most-cited pattern in the repo, appearing across six findings from four mods, and the exact scenario that would produce future conflicts as mods grow.

```
Question: when patching a method that a subclass inherits from a base class, what concrete type should [HarmonyPostfix] use for __instance in Stationeers version 0.2.6228.27061?

Source A (currently on the central page):
  Claim: "Declare __instance as the derived class (e.g. SensorLenses __instance). Harmony resolves the subclass method and the cast is safe."
  Provenance: Research/Patterns/HarmonyInheritedMethodTrap.md section "Instance typing" citing Plans/EquipmentPlus/EquipmentPlus/SensorLensesSyncPatches.cs:17-24

Source B (newly proposed):
  Claim: "When TargetMethod() returns an inherited MethodInfo, Harmony patches the base-class method and the patch fires for every subclass instance. Declare __instance as Thing and filter with `is`; typing it as the derived class emits a castclass that throws InvalidCastException for other Thing subclasses."
  Provenance: Plans/EquipmentPlus/RESEARCH.md:239-240 and rocketstation_Data/Managed/0Harmony.dll :: HarmonyLib.PatchFunctions

Instructions: <as above>
```

Example 2: LogicType registry count. Represents the class of conflict where a new reading of the same game code adds a registry that earlier passes missed.

```
Question: how many independent LogicType registries must a mod extend to avoid mis-rendered names on every in-game UI surface in Stationeers version 0.2.6228.27061?

Source A (currently on the central page):
  Claim: "Three registries: Logicable.LogicTypes, EnumCollections.LogicTypes, and ScreenDropdownBase.LogicTypes. Extending these three covers tablet cycling, cartridge UI, and motherboard dropdowns."
  Provenance: Research/GameSystems/LogicType.md section "Registries" citing Mods/PowerTransmitterPlus/RESEARCH.md:398-425

Source B (newly proposed):
  Claim: "Four registries. The fourth is ProgrammableChip.InternalEnums entries ScriptEnum<LogicType> and BasicEnum<LogicType>, which drives syntax highlighting on in-game screens. Without it, custom LogicType names inherit the default red 'invalid' color even though they compile and execute."
  Provenance: Mods/PowerTransmitterPlus/RESEARCH.md:664-670 and rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.ProgrammableChip.InternalEnums

Instructions: <as above>
```

After the fresh agent returns, the calling agent applies the verdict to the page and appends a Verification History entry in this form:

```
## Verification history

- <YYYY-MM-DD>: conflict on "<short subject>". Previous claim: <A summary>. New finding: <B summary>. Fresh validator verdict: <verdict>. Result: <what changed on the page>.
```

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
