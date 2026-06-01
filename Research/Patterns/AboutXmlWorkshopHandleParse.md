---
title: AboutXmlWorkshopHandleParse
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-01
sources:
  - "$(StationeersPath)/BepInEx/plugins/StationeersLaunchPad/StationeersLaunchPad.dll :: StationeersLaunchPad.Sources.ListMods (Sources/Local.cs line 34)"
  - "$(StationeersPath)/rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Serialization.XmlSerialization.Deserialize<T>(path, root)"
  - "Player.log exception capture 2026-04-21 (InspectorPlus 0.1.0 load)"
  - "Observed first-publish write-back of net.keypadmodfix (item 3737027789), 2026-06-01: deployed About.xml changed 0 -> 3737027789 after in-game publish; git source copy unchanged; downloaded item content at steamapps/workshop/content/544550/3737027789/About/About.xml still 0"
related:
  - ../../CLAUDE.md
  - ../GameSystems/ModLoadSequence.md
  - ../GameSystems/ModMetadataLimits.md
tags: [launchpad, dead-end]
---

# About.xml `<WorkshopHandle>` empty-element parse failure

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

StationeersLaunchPad deserializes each mod's `About.xml` with `XmlSerializer`. The generated reader (`XmlSerializationReaderModAboutEx.Read3_ModAboutEx`, and its sibling `XmlSerializationReaderModAbout.Read2_ModAbout` on a second load path) parses the `<WorkshopHandle>` element as `System.UInt64` by calling `System.Xml.XmlConvert.ToUInt64(String)` on its text content. `XmlConvert.ToUInt64("")` throws `FormatException`, which the serializer rethrows as `InvalidOperationException: There is an error in XML document (<line>, 4).`

## Symptom

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

In `Player.log`, the affected mod is renamed `[Invalid About.xml] <ModID>` in every subsequent StationeersLaunchPad log line for that mod. The BepInEx plugin machinery loads the mod's assembly and fires its `Awake` normally (BepInEx does not consult `About.xml`), so `LogOutput.log` will still show the plugin's own load banner. Only StationeersLaunchPad-surfaced identity (mod list UI, entrypoint headers) is broken.

Example from Player.log for InspectorPlus:

```
[[Invalid About.xml] InspectorPlus]: Loading Assembly .../InspectorPlus.dll
[[Invalid About.xml] InspectorPlus]: Loaded Assembly
...
[[Invalid About.xml] InspectorPlus]: Finding Entrypoints
[[Invalid About.xml] InspectorPlus]: Found 1 Entrypoints
[[Invalid About.xml] InspectorPlus]: - BepInEx Entry InspectorPlus.InspectorPlusPlugin
[[Invalid About.xml] InspectorPlus]: Loading Entrypoints
[Info   :InspectorPlus] InspectorPlus 0.1.0 loaded. Watching: E:\Steam\steamapps\common\Stationeers\BepInEx\inspector\requests
[[Invalid About.xml] InspectorPlus]: Loaded Entrypoints
```

## Verbatim exception

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

```
System.FormatException: Input string was not in a correct format.
  at System.Number.ThrowOverflowOrFormatException (System.Boolean overflow, System.String overflowResourceKey) ...
  at System.UInt64.Parse (System.String s) ...
  at System.Xml.XmlConvert.ToUInt64 (System.String s) [0x00007] in <cc6807978d504e8ea81a34b1b2ec871c>:0
  at Microsoft.Xml.Serialization.GeneratedAssembly.XmlSerializationReaderModAboutEx.Read3_ModAboutEx (System.Boolean isNullable, System.Boolean checkType) [0x00459] in <ad5c469f4ea54b4891a078ab616ff57c>:0
  at Microsoft.Xml.Serialization.GeneratedAssembly.XmlSerializationReaderModAboutEx.Read4_ModMetadata () [0x00050] in <ad5c469f4ea54b4891a078ab616ff57c>:0
  ...
Rethrow as InvalidOperationException: There is an error in XML document (48, 4).
  at System.Xml.Serialization.XmlSerializer.Deserialize (System.Xml.XmlReader xmlReader, ...)
  at System.Xml.Serialization.XmlSerializer.Deserialize (System.IO.TextReader textReader) ...
  at Assets.Scripts.Serialization.XmlSerialization.Deserialize[T] (System.String path, System.String root) ...
  at StationeersLaunchPad.Sources.<ListMods>d__2:MoveNext() (at /_/StationeersLaunchPad/Sources/Local.cs:34)
```

The `(48, 4)` coordinates are the line and column of the offending element (column 4 because the element is indented two spaces and `<` sits at column 3, making the start of the content column 4 in the XmlReader's error geometry).

## Correct element forms

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Observed in this repo:

| About.xml | Line | Content | Parses? |
|---|---|---|---|
| `Mods/PowerTransmitterPlus/.../About.xml` | 123 | `<WorkshopHandle>3707677512</WorkshopHandle>` | Yes |
| `Mods/SprayPaintPlus/.../About.xml` | 156 | `<WorkshopHandle>3702940349</WorkshopHandle>` | Yes |
| `Mods/Template/Template/About/About.xml` | 59 | `<WorkshopHandle></WorkshopHandle>` | No |
| `Mods/InspectorPlus/InspectorPlus/About/About.xml` | 47 | `<WorkshopHandle></WorkshopHandle>` | No |

Any numeric value parses; the element content must be present and numeric. Whether omitting the element entirely (no empty tag, no self-closing element) also parses is not verified in this pass; XmlSerializer typically tolerates missing optional elements, but the generated reader's exact behavior on a missing `<WorkshopHandle>` has not been confirmed against a run.

## Workarounds

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

For an unpublished mod (no Workshop ID yet), the two verified-safe options are:

- Put a literal `0`: `<WorkshopHandle>0</WorkshopHandle>`. Parses, and `0` is the correct "no handle yet" sentinel: the publish flow treats `PublishedFileId == 0` as "create a new Workshop item" (see [ModMetadataLimits](../GameSystems/ModMetadataLimits.md), the `Editor.NewCommunityFile` branch). At first publish the assigned id is written back into the DEPLOYED copy's `<WorkshopHandle>` (see "First publish writes the assigned id back into the deployed About.xml" below).
- Put a placeholder numeric ID that the mod will never publish against, then swap in the real ID at first Workshop upload.

For a published mod, always carry the real Workshop numeric ID.

## First publish writes the assigned id back into the deployed About.xml
<!-- verified: 0.2.6228.27061 @ 2026-06-01 -->

When a mod is published to the Steam Workshop for the FIRST time via the in-game StationeersLaunchPad publisher, the numeric published-file id Steam assigns to the newly created item is written back into the `<WorkshopHandle>` element of the About.xml inside the DEPLOYED mod folder that was published (the upload-source folder under `...\My Games\Stationeers\mods\<ModName>\About\About.xml`).

VERIFIED (directly observed, n=1, first-publish case only): the mod `net.keypadmodfix` (KeypadMod Fix) was deployed to the local mods folder with `<WorkshopHandle>0</WorkshopHandle>` (built from a git source that carried `0`). It was published once through the in-game publisher; Steam created a new item with id `3737027789`. Immediately after the publish, the deployed copy's About.xml read `<WorkshopHandle>3737027789</WorkshopHandle>`, while the separate git SOURCE copy still held `0`. The only actor that touched the deployed file between deploy and that change was the publish flow.

INFERRED (only plausible actor, not separately proven by reading the writer): the StationeersLaunchPad publish flow performed the write, as opposed to the base game or the Steam client. Not confirmed against the decompiled writer.

Consequence: only the DEPLOYED copy is updated. A separate source-of-truth copy (for example the mod's git source About.xml) is NOT touched by the publish and must be synced manually afterward, otherwise the next build redeploys with the stale `0` and the local deploy diverges from the source.

Note on the downloaded published item: the content Steam stores for the item (downloaded copy at `steamapps\workshop\content\544550\3737027789\About\About.xml`) still reads `<WorkshopHandle>0</WorkshopHandle>`, because Steam snapshots the upload-source folder before the write-back lands. The downloaded item therefore does NOT show the assigned id; the write-back is observable only in the local deploy folder.

## Relation to repo CLAUDE.md

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Repo-root `CLAUDE.md` section "Content: About.xml structure and XML safety" was corrected on 2026-04-21 to recommend `<WorkshopHandle>0</WorkshopHandle>` as the placeholder for unpublished mods rather than an empty element. `Mods/Template/` and `Mods/InspectorPlus/` were both carrying empty elements and were fixed in the same pass. Before the correction, any mod seeded from `Mods/Template/` would produce `[Invalid About.xml] <ModID>` at load until its Workshop handle was filled in.

## Why BepInEx still loads the plugin

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

BepInEx's `Chainloader` enumerates DLLs and inspects them for `[BepInPlugin]` attributes independently of StationeersLaunchPad's `About.xml` parse. The About.xml failure only corrupts LaunchPad's per-mod identity record; the `[BepInPlugin(guid, name, version)]` attributes on the plugin class drive the BepInEx-visible logger prefix (`[Info :InspectorPlus]`), which is why `LogOutput.log` looks healthy even when Player.log is showing the `[Invalid About.xml]` rename.

This is a diagnostic tell: if a plugin logs fine to `LogOutput.log` but its name is wrong on-screen or in LaunchPad UI surfaces, check Player.log for the `[Invalid About.xml]` prefix before assuming the plugin itself is broken.

## Verification history

- 2026-04-21: page created. Source: InspectorPlus failed-load diagnosis. Evidence: Player.log exception capture plus cross-reference of working vs failing `About.xml` content in this repo.
- 2026-04-21: "Relation to repo CLAUDE.md" section updated after CLAUDE.md was corrected to recommend `<WorkshopHandle>0</WorkshopHandle>`. Template and InspectorPlus `About.xml` files were updated in the same pass.
- 2026-06-01: Added "First publish writes the assigned id back into the deployed About.xml" and resolved part of the `0`-semantics open question. Pure addition; no prior verified claim contradicted, so no fresh-validator pass was needed (the page previously said only that `0` parses, with semantics explicitly unverified). Evidence: first-publish of `net.keypadmodfix` (item `3737027789`) on 2026-06-01, deployed About.xml observed changing `0` -> `3737027789` while the git source copy stayed `0`. Noted that the downloaded published item content still reads `0` (Steam snapshots the source before the write-back), so the corroborating download does not show the assigned id.

## Open questions

- Whether fully omitting the `<WorkshopHandle>` element (no tag at all) also throws, or whether the XmlSerializer's generated reader treats the field as optional and leaves it default when the element is absent. Not verified this pass.
- Which actor performs the first-publish write-back into the deployed About.xml. Observed that it happens; INFERRED to be the StationeersLaunchPad publish flow (only actor that touched the file), but not confirmed by reading the decompiled writer.
- Whether the write-back also fires on UPDATE publishes (`PublishedFileId` already non-zero), and whether it writes at all when the deployed handle already matches the assigned id. Only the first-publish, `0` -> id case was observed. Not tested.
- Whether `0` as a handle has any downstream semantic effect inside LaunchPad beyond "parses successfully" and "selects the create-new-item publish branch." The publish-branch effect is now known (see ModMetadataLimits); any other in-LaunchPad effect of `0` is still unverified.
