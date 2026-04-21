# Research workflow

Single source of truth for three workflow triggers that sit at the boundary between mod work and the central `Research/` knowledge base.

Out of scope here: frontmatter schema, section-stamp format, Verification History conventions, Unsorted protocol, canonical tag vocabulary, InspectorPlus citation convention. Those are structural rules for pages under `Research/` and live in `Research/CLAUDE.md`. This file covers only the trigger-based rules (when to act, not how to format).

This file is referenced by:

- `research-hook-decompile.ps1` (fires on reads of `rocketstation_Data/Managed/**`).
- A one-line pointer in repo-root `CLAUDE.md`.

Read it in full the first time the reminder surfaces in a session; re-read when starting research or mod work after a break.

## Rule 1: read the mod's RESEARCH.md before touching any mod

Before doing any work on a mod (code changes, debugging, feature additions, refactors, content edits, build changes), read that mod's `RESEARCH.md` in full if it exists. The mod's `RESEARCH.md` carries mod-specific architecture, design decisions, and a pointer list into central `Research/` pages. That pointer list is the index into the wider knowledge base: each entry names a central page and the one-line reason this mod cares. Follow pointers selectively to the central pages relevant to your task.

Rules:

- The very first read for any mod task is that mod's `RESEARCH.md`. Paths are `Mods/<ModName>/RESEARCH.md` for released mods and `Plans/<ModName>/RESEARCH.md` for work-in-progress. Do this before grepping source, before opening `.cs` files, before planning.
- For each pointer in the mod's `RESEARCH.md`, decide whether to follow it. If you choose not to follow a pointer, justify the skip in one sentence (forces a deliberate decision rather than a silent omission).
- For tasks that span multiple mods, repeat the process for each affected mod.
- If a pointer is broken, or the mod clearly depends on a topic that has no central page yet, flag it and proceed. Do not block.
- After completing work that changes architecture, patch behavior, or invalidates the mod's own `RESEARCH.md`, update it. Game-internals changes go to central `Research/` per Rule 2, not to mod-local files.
- This rule applies to sub-agents too. When delegating a mod task, instruct the sub-agent to read the mod's `RESEARCH.md` first and to follow its pointers as needed.

## Rule 2: curate decompiled-code findings into Research/ on every touch

When you read decompiled game code from `$(StationeersPath)\rocketstation_Data\Managed\` (the hook will remind you) or produce a finding about game internals (class behavior, method signature, side-effect, multiplayer message format, save-format detail, Harmony pitfall, transform math, any similar fact), you MUST create or update the matching page under `Research/` in the same response. Do not postpone. Do not park the finding in a commit message or a code comment. Do not write it into a mod's `RESEARCH.md` when the fact is not mod-specific.

Central pages live under `Research/<category>/<page>.md`. The category list, per-category scope, and routing rules live in `Research/CLAUDE.md`. Read it before creating a new page.

The hook injects the current game version on every `Research/` read and write; use that value for the frontmatter `created_in` and `verified_in` fields and for every section-level stamp. Do not guess the version.

Requirements on every touch:

- If the page does not yet exist, create it with full YAML frontmatter (see `Research/CLAUDE.md` for the schema), source citations pointing at the DLL path and any originating `RESEARCH.md` line range, and section-level `<!-- verified: <version> @ <YYYY-MM-DD> -->` stamps after each H2 (and after H3 where the finer granularity matters).
- If the page exists and a section's content changes or is re-confirmed, update that section's stamp to the current game version and date. Cosmetic edits (typos, formatting) do not restamp.
- If the finding does not fit any of the five established categories, put it in `Research/Unsorted/<descriptive-name>.md` with full lossless content AND append an entry to the root `TODO.md` in this format: `- [ ] Research/Unsorted: classify Research/Unsorted/<filename>.md (originally from <source>)`. The Unsorted protocol and TODO format are spelled out in `Research/CLAUDE.md`.
- The lossless principle governs every central page: verbatim code excerpts, formulas, hex layouts, and exact field / method names carry forward untouched. No summarization that drops detail. When two existing sources overlap, preserve both until explicitly reconciled.

## Rule 3: fresh-validator protocol when a new finding contradicts an existing page

Decompilation and reverse-engineering are expensive. Any non-obvious finding must land in central `Research/`, not in commit messages, conversation scratch, mod-local files, or per-session planning docs.

Validation protocol:

- Sub-agents doing research or debugging must report every new lesson, not just their final answer. The coordinating conversation is responsible for collecting those lessons and routing them into `Research/`.
- Adding new content to a central page does not require a second agent. Additive content cannot contradict existing content.
- Changing or removing existing verified content on a central page DOES require a fresh validator. Before persisting a lesson that contradicts what is already on the page, spawn a fresh sub-agent with no exposure to your reasoning, conversation history, or framing. Give it the raw question and the two conflicting source extracts (decompiled code, DLL path, other mods' assemblies, original `RESEARCH.md` line ranges). Instruct it not to defer to either existing claim. Its verdict is binding. The prompt template for spawning the validator is below.
- Record every conflict and its resolution in the target page's "Verification History" section (append-only): date, what was contradicted, fresh-agent verdict, resulting change. Genuinely unresolved cases (fresh agent could not determine ground truth) go to the page's "Open Questions" section and escalate to the user.
- Speculation and "probably" claims do not go into central pages. Only verified, sourced findings land there. Unverified hypotheses stay in conversation or in a `Plans/<Mod>/` stub, never in `Research/`.
- This rule applies to sub-agents too. When a sub-agent produces a finding that contradicts an existing page, the calling agent spawns the fresh validator before persisting.

### Conflict-resolution prompt template

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
