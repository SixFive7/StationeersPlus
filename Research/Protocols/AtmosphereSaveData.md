---
title: AtmosphereSaveData (pipe networks)
type: Protocols
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/SaveFixPrototype/plan.md:212-214
related:
  - ./WorldXml.md
  - ./SaveFileStructure.md
tags: [save-format, save-edit]
---

# AtmosphereSaveData (pipe networks)

Per-pipe-network gas mix stored inside world.xml. Each entry is keyed by `<NetworkReferenceId>`; every gas species is an individual float XML element. Mods editing atmospheres offline can zero out unwanted species; relative to a dominant gas, small changes have negligible thermal impact because energy scales with total moles.

## Gas species list
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Each pipe network's gas mix is an `<AtmosphereSaveData>` in world.xml keyed by `<NetworkReferenceId>`. All gas species are individual float XML elements (Oxygen, Nitrogen, CarbonDioxide, Volatiles, Chlorine, Water, PollutedWater, NitrousOxide, LiquidNitrogen, LiquidOxygen, LiquidVolatiles, Steam, LiquidCarbonDioxide, LiquidPollutant, LiquidNitrousOxide, Hydrogen, LiquidHydrogen, Hydrazine, LiquidHydrazine, LiquidAlcohol, Helium, LiquidSodiumChloride, Silanol, LiquidSilanol, HydrochloricAcid, LiquidHydrochloricAcid, Ozone, LiquidOzone). Set unwanted species to 0. Energy scales with total moles; small changes relative to dominant gas have negligible thermal impact.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration. Source: F0238 (Pipe network AtmosphereSaveData gas species list).

## Open questions

None at creation.
