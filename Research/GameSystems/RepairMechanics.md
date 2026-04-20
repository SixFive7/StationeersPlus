---
title: RepairMechanics
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/RepairPrototype/plan.md:185-207
related:
  - ./DamageState.md
  - ../Workflows/HealAllDamagedThings.md
tags: [damage]
---

# RepairMechanics

Vanilla repair paths: duct tape, Welding Torch / Arc Welder build-state revert, what cannot be repaired, and the resource-cost-to-time-cost ratio.

## Vanilla repair mechanics
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Duct tape fixes suits/solar/portable/canisters; left-click-hold for normal, right-click for suits. Cost: tape consumed, scales with damage. Crafting: Standard 1g Iron (Fabricator) or 2g Iron (Tool Manufacturer); Mk II 2g Iron + 1g Electrum. Suit caveat: repairs rupture, does NOT restore durability. Build-state repair: walls, frames, pipes, cables revert to earlier build states; reapply original materials via Welding Torch (66% CH4/34% O2 canister) or Arc Welder (battery). Unrepairable: AIMEe, Rover Mk I, Wreckage (remove with Angle Grinder, rebuild). Resources consumed are trivial (grams of iron); real cost is player time.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0229b (Plans/RepairPrototype/plan.md:185-207).

## Open questions

None at creation.
