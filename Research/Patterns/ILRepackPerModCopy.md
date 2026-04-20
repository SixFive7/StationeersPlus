---
title: ILRepack: each mod carries its own embedded-library copy
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/StationpediaPlus/PLAN.md:3558-3571 (F0219ac, primary)
  - Plans/StationpediaPlus/PLAN.md:3538-3547 (F0219aa)
related:
  - ./BestEffortIntegration.md
tags: [harmony, packaging]
---

# ILRepack: each mod carries its own embedded-library copy

When a shared library is ILRepack'd into each consuming mod's assembly, every mod loads its OWN copy with OWN static state and OWN Harmony patches. Harmony handles multiple postfixes on the same target method by running them in sequence; each mod sees only its own registrations. This constrains how a shared library can coordinate across consumers, but produces predictable, additive behavior with no mod-ordering surprises.

## Problem
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

F0219ac (Plans/StationpediaPlus/PLAN.md:3558-3571, primary):

> Under ILRepack, each mod's embedded library copy installs its OWN Harmony patches on the same game methods. Harmony handles multiple postfixes on one target by running them in sequence. Each mod's copy only knows about its own registrations (static state is per-copy). No shared registry of all mods' specs. Multiple mods extending the same device page each get their own `<ModName>Details` GameObject. Multiple mods hiding reference pages each maintain their own `HiddenKeys` HashSet, filter runs via multiple postfixes that each remove their own keys, additive effect. Harmony instance IDs derived from consuming mod's plugin GUID so two mods' patches don't clash.

## Solution / recipe
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Design implications for a shared library that ships ILRepack'd into consumers:

1. **Static state is per-copy.** A `static Dictionary<>` inside the library holds one instance per consuming mod. Do not design APIs that rely on "every consumer shares this state."
2. **Harmony patches are per-copy and run in sequence.** A postfix from mod A and a postfix from mod B on the same method both execute, in a deterministic sequence. Each postfix sees the result of the previous.
3. **Harmony instance IDs must be unique.** Derive the Harmony ID from the consuming mod's plugin GUID (`new Harmony(consumerGuid)`), not a library-owned constant. Two copies of the library using the same Harmony ID would clash.
4. **No shared registry.** The library cannot expose "give me all registrations from every mod using this library." Each copy only sees its own. For cross-mod coordination, use a separate IPC channel (BepInEx event, LaunchPadBooster message, filesystem dropbox).

### Structural enforcement over convention

F0219aa (Plans/StationpediaPlus/PLAN.md:3538-3547) adds a generalizable consequence for API design:

> User explicitly rejected CLAUDE.md conventions / code-review discipline for SPA tooltip integration. "CLAUDE.md rules are not deterministic (humans forget; reviews miss). Compiled code is deterministic." Chosen enforcement: `LogicTypePageBuilder.Register(spec)` invokes `SpaBridge.TryEnrichLogicTooltips` automatically for every device in `spec.RelatedDeviceKeys`. Mod authors cannot accidentally skip SPA tooltip enrichment.

The generalizable nugget: CLAUDE.md rules are per-repo, so they cannot enforce behavior inside a consuming mod's code. ILRepack-distributed libraries live inside each consumer's compiled binary; the library's API shape is what enforces correctness. Structural enforcement (build the right thing by construction) beats convention-based enforcement (tell authors to follow a rule) when the authors are in different repos the library cannot see.

## Cited verifications
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- F0219ac: primary source with full pattern statement (per-copy state, per-copy patches, Harmony instance IDs, additive effect).
- F0219aa: generalizable consequence for library API design.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; F0219ac primary, F0219aa providing the structural-enforcement corollary.

## Open questions

None at creation.
