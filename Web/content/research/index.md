---
title: Research
---

# Research

The Research section is a structured knowledge base of Stationeers' internals. Every entry traces a specific game class, system, pattern, protocol, or workflow back to its source in the decompiled `Assembly-CSharp.dll`, the BepInEx mod loader, or a mod's own research notes.

## What you'll find here

Five categories, each answering a different kind of question:

- **Game Classes** — what a specific game type does, what fields it carries, how it interacts with other types. Class-by-class reference. Useful when you have a class name and need to know what it represents and what touches it.
- **Game Systems** — how a subsystem behaves end to end: power, terrain, chat, save format, chemistry, damage. Useful when you're trying to reason about a feature, not a single type.
- **Patterns** — recurring techniques and gotchas that apply across mods. Harmony quirks, Unity null-equality traps, threading rules, ILRepack packaging conventions. Useful when you're writing a patch and want to know the right shape for the problem.
- **Protocols** — wire formats, save formats, file layouts. The on-disk and on-network shape of game data, with offsets and field sizes. Useful when you're reading or writing binary data the game produced.
- **Workflows** — multi-step implementation guides for non-trivial mod features. Useful when you have a feature to build and want a tested recipe.

## How to read these pages

Every page carries metadata at the top:

- **Verified in** — the exact game version the page was last confirmed against. If the game has updated since, treat the page as a starting point and re-check the cited DLL line numbers before trusting it.
- **Sources** — the decompile path, the mod-local research file, the DLL line range, or the InspectorPlus snapshot request that the claim is sourced from. You can re-derive every claim by following the citation.
- **Related** — other pages on this site that share scope. Cross-references between, say, a class page and the system that uses it.

Sections within a page carry their own freshness stamps, so a partial re-verification can be reflected without restamping the whole page.

## Reproducing a claim

Pages don't paraphrase. Code excerpts are verbatim from the decompile; formulas, hex layouts, enum values, and method signatures are preserved as-is. If a claim looks surprising, the cited DLL line is the place to confirm or refute it.

This is the same content the team behind StationeersPlus uses every day to build mods against the game. Nothing private has been held back; the public site and the private working tree share one source.
