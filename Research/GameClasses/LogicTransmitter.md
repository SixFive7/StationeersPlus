---
title: LogicTransmitter
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-26
sources:
  - .work/decompile/configcartridge/Assets.Scripts.Objects.Electrical/LogicTransmitter.cs
  - .work/decompile/configcartridge/Assets.Scripts.Networking/SetLogicTransmitterMessage.cs
  - .work/decompile/configcartridge/Assets.Scripts.Objects.Electrical/Transmitters.cs
  - $(StationeersPath)/rocketstation_Data/StreamingAssets/Data/electronics.xml lines 712-727
  - $(StationeersPath)/rocketstation_Data/StreamingAssets/Language/english.xml lines 4657-4664
  - $(StationeersPath)/rocketstation_Data/StreamingAssets/Language/english_help.xml line 446
tags: [logic, network, stationpedia]
---

## Class Hierarchy
<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

LogicTransmitter extends: LogicInputBase → LogicUnitBase → SmallDevice → Device

Implements: ITransmissionReceiver, ITransmitable, ILogicable, IReferencable, IEvaluable

Device parent class provides: UsedPower field, IPowered contract, power/cable networking, OnOff/Mode/Powered state.
LogicUnitBase parent: cable network I/O, Setting property with ByteArraySync, logic dispatch.

## Mode Behaviour
<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

**Passive** (Mode == 0): Proxies to remote ITransmitable via _currentDevice field. All logic reads/writes pass through.

- Power gating: reads return 0.0 if !Powered; writes are no-ops if !Powered (lines 213-217, 298-301).
- Target is null-safe with ?. operator; reads return 0.0, writes no-op on null target.
- Link stored via ReferenceId in LogicTransmitterSaveData.CurrentConnectedId, persists across save/reload (line 153).

**Active** (Mode == 1): Transmitter is itself a logic device.

- Registers into Transmitters.AllTransmitters list (line 120).
- Exposes only LogicType.Setting for read/write; other LogicTypes delegated to parent.
- Self-registration used by screwdriver UI to build candidate list of linkable targets.

**Mode Toggle:** Via screwdriver on Mode interactable. Clears CurrentDevice and Setting to 0.0 on toggle (lines 403-404).

## Linking
<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

**Screwdriver interaction** (Button1 interactable):
- Calls Logicable.GetNextReadOrWritable<T>(this, CurrentDevice, Transmitters.AllTransmitters, interaction.AltKey) to enumerate candidates (line 423).
- Filters: must be ITransmitable, must be readable/writable, skips self.
- **No range check, no line-of-sight, no grid isolation.** Candidate list is all active transmitters + transmittable devices in AllTransmitters.
- Player selects via forward/backward cycling (AltKey toggles direction).
- Local mutation: CurrentDevice = nextReadOrWritable (line 440).
- Network sync: CurrentDevice property setter flags NetworkUpdateFlags 1024, which serializes the packed ID in BuildUpdate/ProcessUpdate (lines 99, 343-356).

**SetLogicTransmitterMessage:**
- Contract: carries LogicTransmitterId and DeviceId (longs).
- Server processing: finds both entities, casts DeviceId to ITransmitable, sets CurrentDevice (line 32).
- Timeout: waits 10 seconds if entities not found (line 24).
- **Not fired by screwdriver** (property mutation is direct). Purpose unclear from decompile; hypothetical IC10 pathway or remote message source.

**Late-join:** SerializeOnJoin() writes packed ID. DeserializeOnJoin() reads into _savedId. OnFinishedLoad() resolves via Thing.Find<ITransmitable>() (line 160).

## Power
<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

UsedPower: 10W (inherited Device default). Draws continuously while OnOff and Powered, regardless of mode or activity.

In Passive mode: power is required on transmitter to proxy reads/writes. Target power irrelevant.

No IdlePower override. No special PowerTick logic.

## LogicTypes
<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

Active mode: only LogicType.Setting readable/writable (lines 203-207, 275-279).

Passive mode: all LogicTypes forwarded to CurrentDevice, power-gated (lines 201-238, 271-301).

Inherited LogicTypes: Mode, On, Power (dispatched by LogicUnitBase).

**No runtime link target reprogramming via IC10.** Setting only controls the Setting value, not the target reference.

## Multiplayer
<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

NetworkUpdateFlags bit 1024: set on CurrentDevice change (line 99). Triggers BuildUpdate/ProcessUpdate serialization of packed ID.

SerializeOnJoin/DeserializeOnJoin: writes/reads packed CurrentDevice ID. Resolves on OnFinishedLoad() via Thing.Find<>().

Out-of-sync: SetLogicTransmitterMessage.Process() checks NetworkThing.IsValid(DeviceId). If invalid, waits; if timeout, link not set.

## Gotchas
<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

- **Null target:** Transmitter safely null-checks CurrentDevice; reads return 0.0, writes no-op.
- **Unpowered in Passive:** all reads return 0.0, all writes silently fail (strict power gate).
- **Transmitter → Transmitter links:** technically allowed (A proxies B which proxies C). No loop detection; infinite recursion possible if A → B → A.
- **Destroyed target:** reference becomes stale; undefined behavior on stale access (would need InspectorPlus to verify).
- **Save/load:** links persist (CurrentConnectedId saved/loaded), provided target ReferenceId unchanged.
- **Race in message processing:** two concurrent SetLogicTransmitterMessage calls could overwrite; order undefined.
- **Cable network orthogonal:** transmitter can be part of data cable network AND proxy a remote device simultaneously. Independent systems.

## Why Not Data Cables
<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

1. Sealed rooms: transmitter broadcasts from sealed interior; external proxy reads without cable penetration.
2. Multi-source display: each Active transmitter is independent; no channel contention unlike cable networks.
3. Cross-grid: no range limit (per decompile); bridges grids wirelessly.
4. Dynamic reassignment: screwdriver relink is quicker than rerouting cables.

Limitations: power required on transmitter only; no conditional logic; slower setup; dedicated link (no multiplexing).

## Build / Gameplay
<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

**Printer:** Electronics Printer (root element of the recipe file is `<ElectronicsPrinterRecipes>`).

**Print target:** `ItemKitLogicTransmitter`, in-game name "Kit (Logic Transmitter)". Deploys the structure `StructureLogicTransmitter`, in-game name "Logic Transmitter".

**Recipe** (electronics.xml lines 712-727, version 0.2.6228.27061):

| Field | Value |
|---|---|
| Time | 10 (seconds) |
| Energy | 1000 (Ws) |
| Gold | 2 |
| Copper | 1 |
| Electrum | 3 |
| Silicon | 5 |
| Iron / Carbon / Steel / Uranium / Hydrocarbon | 0 |

Electrum + Silicon + Gold pegs it as mid-tier. Compare to `ItemKitMusicMachines` directly above (Gold 2, Copper 2, no Electrum/Silicon) and to other Electronics Printer entries in the same file for tier framing.

**Stationpedia text:** No standalone description string. The kit's `<Description />` is empty in english.xml line 4664. The structure has no `<Description>` field at all in the same `RecordThing` block. The only related help text is the generic `ThingTransmissibleTemplate` (english_help.xml line 446): `Connects to {POS:300}{THING:StructureLogicTransmitter}`. This is the line another device's Stationpedia page renders to point at the linked transmitter.

**Tooltip text:** No entry in english_tooltips.xml. In-game tooltip falls back to the structure name.

**Footprint:** Inherits from SmallDevice. Not validated against the prefab directly here.

**Power draw (gameplay number):** 10 W continuous while on (UsedPower default from Device, not overridden in the class).

**Tier framing:** Gold + Electrum + Silicon = behind the Electronics Printer + an ingot-grade smelting/alloying chain. Not a starter item; sits in the same materials class as advanced logic devices (memory, math units).

## Verification History

- 2026-04-26: Initial deep research from decompile. All sections 0.2.6228.27061.
- 2026-04-26: Added Build / Gameplay section from `electronics.xml` (Electronics Printer recipe), `english.xml` (display names), `english_help.xml` (ThingTransmissibleTemplate). No conflict with existing sections (additive). Source list and tags updated to reflect new provenance.

## Open Questions

- Exact cleanup on target destruction: does target hold reference back to transmitter?
- SetLogicTransmitterMessage actual sender: screwdriver is direct property mutation; message origin unclear.
- Single-device list cycling behavior: does GetNextReadOrWritable loop on self if only one device exists?

