---
title: /spda_dumpkeys Console Command
type: Workflows
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/StationpediaPlus/PLAN.md:1652-1683
related:
  - ../GameSystems/Stationpedia.md
  - ../GameSystems/ThirdPartyModIdentities.md
tags: [stationpedia, spa]
---

# /spda_dumpkeys Console Command

Use Stationpedia Ascended's built-in `/spda_dumpkeys` console command to list every registered Stationpedia page during implementation or debugging of a Stationpedia-adjacent mod. Reach for this recipe when you need to confirm a custom page is registered, verify hidden keys, or look for key collisions between mods.

## When to use
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- Confirming that a mod's custom reference pages are registered in `_linkIdLookup`.
- Confirming that a mod's hidden keys still appear in `StationpediaPages` (the search filter is at display time, not registration time).
- Checking for key collisions between a mod and any other mod that registers Stationpedia pages.

Stationpedia Ascended is a third-party mod; `/spda_dumpkeys` is available only when Stationpedia Ascended is installed. For debugging on a machine without Stationpedia Ascended, write an equivalent one-shot diagnostic in the mod being implemented, temporarily.

## Prerequisites
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- Stationpedia Ascended installed alongside the mod under test. See the Stationpedia Ascended identity constants on [ThirdPartyModIdentities.md](../GameSystems/ThirdPartyModIdentities.md).
- A running Stationeers session with the BepInEx console accessible.

## Steps
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Stationpedia Ascended registers a BepInEx console command `/spda_dumpkeys` (`StationpediaAscendedMod.cs:1652-1684`) that iterates `Stationpedia.StationpediaPages` and prints every page's Key, Title, and DisplayFilter to the console. It is not invoked automatically; run it from the BepInEx console during interactive testing.

## Interpreting output
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Each line of the dump names a page by:

- **Key**: the registry key used for cross-links and lookups.
- **Title**: the display title shown in the Stationpedia UI.
- **DisplayFilter**: the filter used to decide whether the entry appears in search results.

Use this to verify:

- The mod's reference pages appear in `_linkIdLookup`.
- Hidden keys appear in `StationpediaPages` even though the search UI filters them at display time.
- No two mods are attempting to register the same key.

## Pitfalls
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- Available only when Stationpedia Ascended is installed. On a non-Stationpedia-Ascended setup, the command is not registered and the BepInEx console reports it as unknown.
- The dump captures current registry state. A page registered late in load order or by a deferred postfix may not appear if `/spda_dumpkeys` runs before registration completes; run the command after the Stationpedia UI has been opened at least once.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0219s (`Plans/StationpediaPlus/PLAN.md:1652-1683`, source text at `PLAN.md:3034-3050`).

## Open questions

None at creation.
