---
title: world.xml
type: Protocols
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-14
sources:
  - Plans/SaveFixPrototype/plan.md:160-178
  - Plans/SaveFixPrototype/plan.md:217-220
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: XmlSaveLoad.XmlReaderSettings (line 267981), XmlSaveLoad.ManagerStart ExtraTypes build (268106), XmlSaveLoad.LoadWorld (268507), XmlSaveLoad.Load (268463), XmlSerialization.Deserialize (268982), Serializers (234110), SaveHelper.Save (264972), DynamicThingSaveData (298176), DynamicThing.InitialiseSaveData / DeserializeSave / MoveToParent (299544, 299570, 299593)
  - DedicatedServer/data/saves/Luna/Luna.save :: world.xml (game-written, 61514025 bytes, inspected 2026-07-02)
  - DedicatedServer/data/saves/Luna_mixedwire/Luna_mixedwire.save :: world.xml (game-written, 65195989 bytes, censused 2026-07-14)
related:
  - ./SaveFileStructure.md
  - ./AtmosphereSaveData.md
  - ./TerrainChunkChecksums.md
  - ../GameSystems/UnregisteredSaveDataBehavior.md
  - ../GameSystems/SaveZipExtension.md
  - ../Patterns/SaveLoadOrdering.md
tags: [save-format, save-edit, save-load]
---

# world.xml

`world.xml` is the primary save payload: every Thing, atmosphere, network, and player lives here. This page documents individual subsections as they are investigated.

## Rooms (grid coordinates)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Rooms are sealed pressurized volumes. The game tracks every airtight cell. Sealed tunnels connecting rooms are rooms themselves.

```xml
<Rooms>
  <Room>
    <RoomId>42</RoomId>
    <Grids>
      <Grid><x>-12930</x><y>2050</y><z>-6930</z></Grid>
      <!-- one entry per sealed cell -->
    </Grids>
  </Room>
</Rooms>
```

**Grid coordinate scale:** stored at 10x. Divide by 10 for world coords. Grid `(-12930, 2050, -6930)` = world `(-1293, 205, -693)`.

**Cell size:** each cell is a 2x2x2 voxel block. Cells are spaced 2 apart on each axis.

## DifficultySetting
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

`<DifficultySetting Id="Normal" />` near the top of world.xml (a direct child of root `<WorldData>`, serialized via `XmlSaveLoad.WorldData.DifficultySetting`, a `SerializedId` whose `Id` is an `[XmlAttribute]`). Values include `Easy`, `Normal`, `Stationeer`, and `Creative`. The game mode (Survival vs Creative) is NOT a separate field; it is derived from this difficulty on load. Setting `Id="Creative"` enables creative mode and persists across reloads. See [../GameSystems/CreativeModeAndDifficulty.md](../GameSystems/CreativeModeAndDifficulty.md) for the full mechanism, the stock difficulty presets, and the `Creative` preset's field values.

## Celestial (sun position / world time)
<!-- verified: 0.2.6228.27061 @ 2026-06-25 -->

The `<Celestial>` element is a direct child of root `<WorldData>` and holds the orbital/sun clock. It serializes `OrbitSimulationSaveData` (see [../GameClasses/OrbitalSimulation.md](../GameClasses/OrbitalSimulation.md) for the full mechanism). Two attribute-valued `DoubleReference` children, both in seconds:

```xml
<Celestial>
  <AccumulatedTime Value="407548.122450768" />
  <SimulationTime Value="122597.43671865921" />
</Celestial>
```

- `SimulationTime/@Value` is authoritative. On load, `OrbitSimulationSaveData.Deserialize` calls `OrbitalSimulation.SetSimulationTime(SimulationTime.Value)`, which rebuilds the sun direction `WorldSunVector` from this clock. Editing it moves the sun. Setting it does NOT freeze the sun (the sim resumes ticking on load); a freeze needs `TimeScale = 0` at runtime, e.g. the `orbital timescale 0` console command.
- `AccumulatedTime/@Value` is total real time; used as the fallback time source and to restore `TotalRealTimeSeconds`.
- If `<Celestial>` is absent, the loader falls back to `SetRealTime(DaysPast * siderealDaySeconds)`.

Sibling clock fields (also direct children of `<WorldData>`, mirrored in `world_meta.xml`):

- `<DaysPast>` (uint): in-world day counter; the `<Celestial>`-absent fallback time source.
- `<DateTime>` (.NET `DateTime.Ticks`, 100ns units): calendar/HUD clock display only. NOT read for sun position. The time-of-day component decodes to the wall-clock time shown in-game.

There is no explicit sun-angle or day-length element in world.xml; the sun is derived at runtime from the `<Celestial>` clock and the world setting's day length.

## Invalid numeric character references: the game tolerates them, strict parsers do not
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

A game-written world.xml can contain numeric character references to characters that XML 1.0 forbids. Observed in a real game-written save (`DedicatedServer/data/saves/Luna/Luna.save`, world.xml 61514025 bytes), exactly one occurrence, on a sign:

```xml
<ThingSaveData xsi:type="StructureSaveData">
  <ReferenceId>614437</ReferenceId>
  <PrefabName>StructureSign1x1</PrefabName>
  <CustomName>&lt;color=yellow&gt;HIGH &#xB;VOLTAGE&lt;/color&gt;...</CustomName>
```

The `&#xB;` is the raw five bytes `&#xB;` in the file (a character reference to U+000B, vertical tab), not a literal control byte; the `...` shown above stands for three literal U+200B zero-width-space characters that also sit in the stored string before `</CustomName>`. The file bytes are valid UTF-8; the DOCUMENT is invalid XML 1.0, because U+000B is outside the XML `Char` production (`#x9 | #xA | #xD | [#x20-#xD7FF] | [#xE000-#xFFFD] | [#x10000-#x10FFFF]`).

Both sides of the game's pipeline let this through (0.2.6403.27689 decompile):

- Write: world.xml is produced by `Serializers.WorldData.Serialize(zipOutputStream, worldData)` (`SaveHelper.Save`, line 265016), i.e. `XmlSerializer.Serialize` straight onto the ZIP stream. That path uses the legacy non-validating `XmlTextWriter`, which happily escapes a control character in a string field as a numeric character reference instead of rejecting it. No `XmlWriterSettings` exists anywhere in the assembly (grep: zero hits).
- Read: `XmlSerialization.Deserialize(XmlSerializer, StreamReader, string)` (line 268982) wraps the reader explicitly:

  ```csharp
  using XmlReader xmlReader = XmlReader.Create(streamReader, XmlSaveLoad.XmlReaderSettings);
  return xmlSerializer.Deserialize(xmlReader);
  ```

  with (line 267981):

  ```csharp
  public static readonly XmlReaderSettings XmlReaderSettings = new XmlReaderSettings
  {
      CheckCharacters = false
  };
  ```

  `CheckCharacters = false` is the tolerance switch: the reference expands to a real `\x0B` in the deserialized string and the load succeeds.

Consequences for offline tooling:

- Strict XML parsers reject the WHOLE document on such a reference (Python's expat raises `xml.parsers.expat.ExpatError: reference to invalid character number`; default-settings .NET `XmlReader` and libxml2 likewise). One decorated sign breaks naive parse-and-reserialize tooling for a 61 MB save.
- Byte-level surgery is therefore the safer editing mode: keep untouched regions byte-identical and never re-serialize the document. When a real XML parse is needed (censusing, validation), pre-process a COPY of the bytes with a character-reference shim that replaces XML-invalid `&#...;` references before parsing, and never write the shimmed bytes back. The 2026-07-02 save-surgery session used exactly this pattern (regex `&#(x[0-9a-fA-F]+|\d+);`, validity check per the XML `Char` ranges above, replacement only in the in-memory parse copy).
- Any string field a player can type into (sign `CustomName` observed; labeller-editable names generally) can carry such characters, so tooling must assume they can appear anywhere in world.xml, not just on signs.

Re-observed 2026-07-14 in a second game-written save (the Luna_mixedwire world, world.xml 65195989 bytes): again exactly one XML-invalid reference, again `&#xB;`, again on a sign's `CustomName`. The shim-before-parse pattern parsed the file cleanly.

## ThingSaveData registry: unknown xsi:type is fatal, unknown PrefabName is a per-Thing skip
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

Two superficially similar "the save references something that is not installed anymore" cases have opposite blast radii. Verified against the 0.2.6403.27689 decompile:

**The serializer's known-type set is built from loaded prefabs.** `XmlSaveLoad.ManagerStart` (line 268106) builds `ExtraTypes` by walking every loaded prefab and collecting its save-data and mod-data types, then appending a fixed vanilla list:

```csharp
List<Type> extraTypes = new List<Type>();
foreach (Thing sourcePrefab in WorldManager.Instance.SourcePrefabs)
{
    if (!(sourcePrefab == null))
    {
        ThingSaveData thingSaveData = sourcePrefab.SerializeSave();
        Type type = thingSaveData.GetType();
        if (!extraTypes.Contains(thingSaveData.GetType()))
        {
            extraTypes.Add(type);
        }
        object modXmlType = sourcePrefab.GetModXmlType();
        ...
    }
}
AddExtraTypes(ref extraTypes);      // fixed vanilla list, line 268037
extraTypes.Add(typeof(JetPackModData));
ExtraTypes = extraTypes.ToArray();
```

and every save-file serializer is constructed with it (`Serializers`, line 234110): `_worldData = new XmlSerializer(typeof(XmlSaveLoad.WorldData), XmlSaveLoad.ExtraTypes);` (line 234162). So a MOD's custom `ThingSaveData` subclass is known to the serializer only while the mod's prefabs are loaded. Remove the mod and the type silently drops out of the registry.

**Unknown `xsi:type` fails the whole document.** `XmlSerializer.Deserialize` throws on the first `<ThingSaveData xsi:type="...">` whose type is not registered; the observed message for a removed mod is `The specified type was not recognized: name='ConsoleBoardSaveData'` (ModularConsoleMod removed after the 0.2.6403 update; ScriptedScreens's types hit the same wall). The catch in `XmlSerialization.Deserialize` (line 268994) logs `"An error occurred while deserializing a file!: " + path + ...` and returns null, and `XmlSaveLoad.LoadWorld` (line 268522) then aborts the entire load:

```csharp
object obj = XmlSerialization.Deserialize(Serializers.WorldData, fullName);
if (!(obj is WorldData worldData))
{
    UpdateLoadingScreen(display: false);
    throw new NullReferenceException("Failed to load the world.xml: " + fullName);
}
```

No per-Thing recovery is possible because the type failure happens inside the one-shot whole-document deserialize, before any Thing exists. This is the same mechanism documented at 0.2.6228 in [../GameSystems/UnregisteredSaveDataBehavior.md](../GameSystems/UnregisteredSaveDataBehavior.md); re-confirmed unchanged at 0.2.6403.27689 (only the per-Thing loader's name changed, see below).

**Unknown `PrefabName` inside a recognized save-data type skips just that Thing.** After the document deserializes, `LoadWorld` loops the Things (line 268635) and discards null results: `_ = Load(thingSaveData) == null;` (line 268669). `XmlSaveLoad.Load` (line 268463, the 0.2.6403 rename of `LoadThing`, see [../Patterns/SaveLoadOrdering.md](../Patterns/SaveLoadOrdering.md)) returns null gracefully when the prefab is gone:

```csharp
Thing thing = Prefab.Find(thingData.PrefabName);
if (thing == null)
{
    UnityEngine.Debug.LogWarning("Can't spawn " + thingData.PrefabName);
    return null;
}
```

**Practical rule for mod removal.** Removing a mod that registered its own `ThingSaveData` subclasses (via saved instances of them in world.xml) hard-breaks the save: nothing loads. Removing a mod whose Things saved as vanilla save-data types (plain `ThingSaveData` / `StructureSaveData` / `DynamicThingSaveData` with a custom `PrefabName`) soft-degrades: those Things vanish with a `Can't spawn <name>` warning and everything else loads. Recovery from the hard case without reinstalling the mod means removing the offending `<ThingSaveData xsi:type="...">` elements from world.xml offline (byte-level surgery per the section above; mind the dangling `ParentReferenceId` and other references to the removed `ReferenceId`s, next section).

## Containment is stored child-side: ParentReferenceId / ParentSlotId
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

Slot containment (what is inside what) is persisted on the CONTAINED Thing, not on the container. `DynamicThingSaveData : ThingSaveData` (decompile line 298176) carries:

```csharp
[XmlElement]
public long ParentReferenceId;

[XmlElement]
public int ParentSlotId;
```

(plus `Dragged`, `DragOffset`, `Velocity`, `AngularVelocity`, `HealthCurrent`). Plain `ThingSaveData` (structures) has no parent fields; only dynamic things can sit in slots.

Write side (`DynamicThing.InitialiseSaveData`, line 299544): `0` is the explicit no-parent sentinel for both fields:

```csharp
dynamicThingSaveData.ParentSlotId = (IsChild ? ParentSlot.SlotIndex : 0);
dynamicThingSaveData.ParentReferenceId = (IsChild ? ParentSlot.Parent.ReferenceId : 0);
```

Read side (`DynamicThing.DeserializeSave`, line 299570 -> `MoveToParent`, line 299593): the child resolves its own parent by reference id and inserts itself:

```csharp
ParentReferenceId = dynamicThingSaveData.ParentReferenceId;
MoveToParent(dynamicThingSaveData.ParentReferenceId, dynamicThingSaveData.Dragged, dynamicThingSaveData.ParentSlotId, dynamicThingSaveData.DragOffset);
```

```csharp
private void MoveToParent(long parentReferenceId, bool dragged, int parentSlotId, Vector3 dragOffset)
{
    if (parentReferenceId == 0L)
    {
        return;
    }
    Thing thing = Referencable.Find<Thing>(parentReferenceId);
    if ((object)thing != null)
    {
        if (!dragged)
        {
            try
            {
                MoveToSlot(thing.Slots[parentSlotId], thing);
                return;
            }
            catch (ArgumentOutOfRangeException)
            {
                ConsoleWindow.PrintError($"Slot {parentSlotId} does not exist on thing {thing.DisplayName}({thing.ReferenceId}) so cannot move {DisplayName}({base.ReferenceId}) to slot.");
                return;
            }
            ...
        }
        DragInSlot(thing.Slots[parentSlotId], dragOffset);
    }
    else
    {
        MoveToParentWhenReady(parentReferenceId, dragged, parentSlotId, dragOffset).Forget();
    }
}
```

Notes:

- `MoveToParentWhenReady` (line 299628) is an async retry for the "parent not deserialized yet" ordering case, so save-file element order does not have to put parents before children. It polls `Referencable.Find<Thing>` every frame with a 10-second unscaled-time budget (`float timeToWait = 10f;`); on timeout it prints `" not initialized ParentId : " + parentReferenceId` to the console and gives up, leaving the child at its own deserialized world position.
- `LoadWorld`'s `loadWithoutChars` purge path leans on the child-side link too (line 268663): a `DynamicThingSaveData { ParentReferenceId: not 0L }` whose parent was purged is purged with it.
- Observed distribution in the Luna save (world.xml, 2026-07-02): 1059 adjacent `ParentReferenceId`/`ParentSlotId` pairs total; 149 with `ParentReferenceId` 0, and every one of those 149 also has `ParentSlotId` 0; zero pairs with `ParentReferenceId` 0 and a nonzero `ParentSlotId`. So `0`/`0` is both the code's write-side sentinel and the empirical on-disk shape.
- Save-surgery consequence: when removing Things from world.xml offline, scan the remaining document for the removed `ReferenceId`s. A surviving child whose `<ParentReferenceId>` points at a removed Thing should have that field reset to `0` (the sentinel), leaving `ParentSlotId` as-is to match the observed no-parent shape. At runtime a dangling parent reference is not fatal (the `Referencable.Find` miss just schedules `MoveToParentWhenReady`, which never completes), but the child then floats in limbo instead of dropping cleanly to the world.

## Writer attribution and Thing serialization (Luna_mixedwire census)
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

Facts established 2026-07-14 by an offline census of a dedicated-server world (`DedicatedServer/data/saves/Luna_mixedwire/`, world.xml 65195989 bytes; Python `zipfile` + `xml.etree.iterparse` after the character-reference shim from the invalid-references section above). Census scale: 34650 `ThingSaveData` elements, 15134 placed cables (7422 heavy, 4183 normal, 3515 super-heavy, 14 fuses), 287 cable networks, max ReferenceId 673617. The world had suffered a mixed-tier cable print the previous day (a blueprint mod bypassed occupancy checks; see [../Patterns/BlueprintModPlacement.md](../Patterns/BlueprintModPlacement.md)), which is the incident several of these facts were learned diagnosing.

**ZIP member count attributes the writer.** A dedicated-server-written `.save` carries only the 3 standard ZIP members (`world_meta.xml`, `world.xml`, `terrain.dat`); a client-written `.save` carries 5 (adds `preview.png` and `screenshot.png`; the PNG previews are skipped in batch mode). Mod sidecar XML entries (see [../GameSystems/SaveZipExtension.md](../GameSystems/SaveZipExtension.md)) ride the ZIP in addition on both kinds. Observed: the client-written main save carried both PNGs plus four mod sidecars; every server autosave beside it carried the same members minus the two PNGs. Presence or absence of the PNG pair dates and attributes every save file in a folder.

**`CableSaveSaveData.CableNetworkId` is runtime state, not authoritative topology.** Placed cables serialize as `CableSaveSaveData` with a `CableNetworkId` tail, but the id records the runtime network object at save time and can be fragmented inconsistently with physical contact. The loader re-merges by contact: `Cable.DeserializeSave` joins the cable to its serialized id's network, then `OnRegistered`-time merges reconcile fragments that physically touch. Observed: after an occupancy-bypassing print plus two cable burns, a physically continuous same-tier collinear straight row serialized as four different network ids (4 adjacent collinear same-tier pairs with differing ids; zero such pairs anywhere else among the save's 15134 placed cables), the fragmentation persisted unchanged across four server autosaves written about four hours later, and loading re-fuses the fragments through the surviving contact cells. Mixed-tier membership is still a reliable incident signal (observed: one network of 518 super-heavy plus 99 normal members, one of 3 plus 4), but tools must not treat `CableNetworkId` as connectivity ground truth.

**Only live cables use `CableSaveSaveData`.** Cable fuses (`StructureCableFuse500k`; all 14 in the census) serialize as plain `StructureSaveData` with NO `CableNetworkId` element. Burnt cable remains are distinct `*Burnt` prefabs (`StructureCableStraightBurnt`, `StructureCableSuperHeavyStraightBurnt`, super-heavy junction and corner Burnt variants observed), also without a network id. Cable coils in inventories are `ItemCableCoil*` dynamic things. Fuse-over-cable is a vanilla-legal co-location: every observed fuse shared its small-grid cell with a live `StructureCableSuperHeavyStraight`, so save-audit tooling must whitelist that stack.

**Network ids and ReferenceIds share one global counter.** Cable network ids are allocated from the same counter as Thing `ReferenceId`s, so a network id doubles as a creation-time marker relative to Thing refs: in a save whose max ReferenceId was 673617, network ids at and above 671720 dated those network objects' creation to the final session before the save.

**`TransformerSaveData` adds only `OutputSetting`.** A transformer's input/output cable connections are never serialized; the game re-resolves them from grid contact at load, so offline tooling must derive connections geometrically. For `StructureTransformer` (the large transformer) the two cable connection cells sit at forward multiplied by +1.0 and forward multiplied by -0.5 from `WorldPosition` (dominant pattern over all 82 instances in the census, 60+ of 82 matching; the save cannot tell which connector is the Input role and which is the Output role, only where cables attach). Observed `OutputSetting=50000` on maxed units (50 kW, the large transformer cap).

**`OwnerSteamId` provenance (observed-in-save evidence).** Three value classes appeared in this save: a real SteamID64 (76561197970372584) on hand-placed Things; `513` on a machine-placed cable run attributed to the server-host identity (513 is not a valid SteamID64); `0` on Things spawned programmatically via the `Thing.Create` default (every blueprint-printed Thing in this save carried `OwnerSteamId 0`). These attributions are empirical observations from one save, not decompile-verified semantics.

**Cable positions serialize jitter-free; wall-family structures legally share a cell.** Cables live on the small grid (pitch 0.5 world units; the large grid is 2.0). Every one of the 15134 placed cables serialized `WorldPosition` as exact multiples of 0.5 on all three axes with zero float jitter, and `RegisteredWorldPosition` matched `WorldPosition` exactly in every checked case, so exact-equality cell keying is safe for cables in offline tooling (in this save every cable Thing was a single-cell piece, so cell identity equals exact `WorldPosition` equality). Position alone is NOT a uniqueness key across structure types: wall-family structures legally share one position with different rotations (122 same-prefab-same-cell pairs observed, e.g. two `StructureWallSmallPanelsOpen` on one cell; every checked pair differed in orientation). Positions serialize as exact floats (`WorldPosition` plus `RegisteredWorldPosition`) and rotations as a quaternion plus a redundant `eulerAngles` block, on `<ThingSaveData xsi:type="...">` children of `<AllThings>` under root `<WorldData>`.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-06-25 -->

- 2026-04-20: page created from the Research migration. Sources: F0236 (Rooms grid coordinate scale + cell size), F0250 (DifficultySetting enum location).
- 2026-06-25: added the Celestial section (sun position / world time fields) from a decompile read of `OrbitSimulationSaveData` / `WorldData` (game version 0.2.6228.27061) and confirmation against a real Luna save's world.xml. Documents `SimulationTime`, `AccumulatedTime`, `DaysPast`, `DateTime`.
- 2026-06-25: expanded the DifficultySetting section to note it is a `SerializedId` child of `<WorldData>`, that the live `difficultySettings.xml` ships a `Creative` value, and that the game mode (Survival/Creative) is derived from this element rather than stored separately. Cross-linked the new `../GameSystems/CreativeModeAndDifficulty.md`. Verified against a real Luna save's world.xml (`<DifficultySetting Id="Normal" />`) and the `XmlSaveLoad.WorldData` decompile.
- 2026-07-02: added three sections from the save-surgery session that stripped removed-mod ThingSaveData out of the Luna save after the 0.2.6403 update: "Invalid numeric character references" (game-written `&#xB;` observed at ReferenceId 614437, `XmlSaveLoad.XmlReaderSettings.CheckCharacters = false` quoted from decompile line 267981), "ThingSaveData registry" (ExtraTypes built from loaded prefabs in `ManagerStart` line 268106; fatal whole-document failure at `LoadWorld` line 268522 vs graceful `Can't spawn` skip in `Load` line 268463; re-confirms `../GameSystems/UnregisteredSaveDataBehavior.md` at 0.2.6403.27689), and "Containment is stored child-side" (`DynamicThingSaveData.ParentReferenceId`/`ParentSlotId` lines 298179-298182, write sentinel at 299549-299550, `MoveToParent` at 299593 with the 10 s `MoveToParentWhenReady` fallback at 299628; Luna census: 1059 pairs, 149 no-parent, all 0/0). Every decompile quote verified against `.work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs`; save evidence from a byte-level inspection of the Luna world.xml the same day.
- 2026-07-14: added the "Writer attribution and Thing serialization (Luna_mixedwire census)" section from the Luna_mixedwire forensics pass (offline Python zipfile + iterparse census of the dedicated-server world that suffered the 2026-07-13 mixed-tier blueprint-print incident; 34650 ThingSaveData elements, 15134 placed cables, 287 cable networks, world.xml 65195989 bytes). Facts added: PNG-member writer attribution, CableNetworkId as non-authoritative runtime state re-merged by contact on load, fuse and burnt-cable serialization shapes, the shared ReferenceId/network-id counter, TransformerSaveData's OutputSetting-only payload with geometric connector derivation, OwnerSteamId provenance values (real SteamID64 / 513 / 0), and jitter-free 0.5-multiple cable positions vs wall-family shared-cell rotations. Also re-observed the invalid `&#xB;` character reference in this second save (again exactly one, on a sign) and restamped that section. Companion page: [../Patterns/BlueprintModPlacement.md](../Patterns/BlueprintModPlacement.md).

## Open questions

None at creation.
