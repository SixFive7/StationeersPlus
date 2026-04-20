---
title: AboutXmlWorkshopHandleParse
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-21
sources:
  - "$(StationeersPath)/BepInEx/plugins/StationeersLaunchPad/StationeersLaunchPad.dll :: StationeersLaunchPad.Sources.ListMods (Sources/Local.cs line 34)"
  - "$(StationeersPath)/rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Serialization.XmlSerialization.Deserialize<T>(path, root)"
  - "Player.log exception capture 2026-04-21 (InspectorPlus 0.1.0 load)"
related:
  - ../../CLAUDE.md
  - ../GameSystems/ModLoadSequence.md
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

- Put a literal `0`: `<WorkshopHandle>0</WorkshopHandle>`. Parses. Semantics unverified (whether LaunchPad treats `0` as "no handle" vs "handle zero"; at a minimum the parse succeeds and the mod loads under its correct name).
- Put a placeholder numeric ID that the mod will never publish against, then swap in the real ID at first Workshop upload.

For a published mod, always carry the real Workshop numeric ID.

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

## Open questions

- Whether fully omitting the `<WorkshopHandle>` element (no tag at all) also throws, or whether the XmlSerializer's generated reader treats the field as optional and leaves it default when the element is absent. Not verified this pass.
- Whether `0` as a handle has any downstream semantic effect inside LaunchPad beyond "parses successfully." Not verified.
