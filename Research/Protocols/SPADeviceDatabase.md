---
title: Stationpedia Ascended DeviceDatabase / descriptions.json
type: Protocols
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/StationpediaPlus/PLAN.md:3673-3705
  - Plans/StationpediaPlus/PLAN.md:1096-1134
related:
  - ../GameSystems/StationpediaAscendedInternals.md
  - ../GameSystems/ThirdPartyModIdentities.md
tags: [save-format]
---

# Stationpedia Ascended DeviceDatabase / descriptions.json

Wire-format reference for Stationpedia Ascended's shipped JSON description database and its runtime `DeviceDatabase` dictionary. Mods that augment SPA tooltips or add new device pages need the exact schema to produce valid entries and to understand which lookup paths SPA consults at runtime.

## descriptions.json metrics
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Size: approximately 1.2 MB (shipped as both an embedded resource
`StationpediaAscended.descriptions.json` inside SPA's DLL and as a loose
file next to the DLL at
`<STEAM_ROOT>\steamapps\workshop\content\544550\3634225688\descriptions.json`).

Content breakdown:

- `devices`: 499 entries. One per modded or vanilla Thing that SPA documents.
- `guides`: 5 entries (Survival, Power, Airlock, AC, plus one general).
- `mechanics`: 2 entries (game-mechanic explainer pages).
- `genericDescriptions`: 250 entries (fallback LogicType / slot / version /
  memory descriptions used when a device-specific entry is missing).

Loading order (first hit wins):
1. Embedded resource `StationpediaAscended.descriptions.json` inside the DLL.
2. `<dll dir>/descriptions.json` (next to SPA's DLL).
3. `BepInEx/scripts/descriptions.json`.
4. `<My Games>/Stationeers/mods/StationpediaAscended/descriptions.json`.

Deserialized via Newtonsoft.Json into `DescriptionsRoot` which contains
`List<DeviceDescriptions> devices` etc. `GenericDescriptionsData` uses
`[JsonExtensionData]` so additional unknown keys are preserved in
`AdditionalData`.

Implication: SPA's shipped file already has entries for all three microwave
transmitter variants (`ThingStructurePowerTransmitter` at :24385,
`ThingStructurePowerTransmitterOmni` at :24482,
`ThingStructurePowerTransmitterReceiver` at :24524) with `logicDescriptions`
for every vanilla LogicType but empty `operationalDetails: []`. None of our
six custom LogicType names appear. SpaBridge's reflection into
`DeviceDatabase` adds entries to the existing `logicDescriptions` dicts on
these devices at runtime; it does not modify the shipped JSON file itself.

## DeviceDatabase and schema
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Public static dictionary on `StationpediaAscended.StationpediaAscendedMod`:

```csharp
public static Dictionary<string, DeviceDescriptions> DeviceDatabase { get; }
```

Populated synchronously by `LoadDescriptions` inside SPA's Awake. By the time
any BepInEx `OnAllModsLoaded` callback fires, the database is ready.

SPA entry schema:

```csharp
public class DeviceDescriptions
{
    public string deviceKey;
    public string displayName;
    public string pageDescription;
    public string pageDescriptionPrepend;
    public string pageDescriptionAppend;
    public string pageImage;
    public Dictionary<string, LogicDescription> logicDescriptions;
    public Dictionary<string, ModeDescription> modeDescriptions;
    public Dictionary<string, SlotDescription> slotDescriptions;
    public Dictionary<string, VersionDescription> versionDescriptions;
    public Dictionary<string, MemoryDescription> memoryDescriptions;
    public List<OperationalDetail> operationalDetails;
    public string operationalDetailsTitleColor;
    public string operationalDetailsBackgroundColor;
    public bool generateToc;
    public string tocTitle;
    public bool tocFlat;
}

public class LogicDescription
{
    public string dataType;     // "Boolean", "Float", "Integer", "ReferenceId", "String"
    public string range;        // "0-1", "0+", "any", "0 or id", etc.
    public string description;  // plain text only; no markup tokens
}
```

All lowercase-first field names. Plain fields, no properties, no constructors.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration. Primary source F0249 (SPA descriptions.json shipped metrics). Additional source F0213 (DeviceDatabase + DeviceDescriptions + LogicDescription schema). F0219x merges into F0249 per MigrationMap §5.1.

## Open questions

None at creation.
