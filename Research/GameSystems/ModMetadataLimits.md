---
title: Mod Metadata Limits
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-21
sources:
  - rocketstation_Data/Managed/StationeersLaunchPad.dll :: PublishItemValidator (validate method, lines 5795-5829)
  - rocketstation_Data/Managed/StationeersLaunchPad.dll :: ModAboutEx (ChangeLog field, lines 13054-13055)
  - rocketstation_Data/Managed/StationeersLaunchPad.dll :: WorkshopMenu Harmony prefixes (PublishMod / Workshop_PublishItemAsync, lines 3650-3674)
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: ModAbout (ModMetadata XML root, lines 38046-38087)
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: SteamTransport.Workshop_PublishItemAsync (lines 263945-263965)
related:
  - ./ModLoadSequence.md
tags: [launchpad]
---

# Mod Metadata Limits

Character limits applied to About.xml mod metadata. Headline result: the 8000-character cap on `<ChangeLog>` (and `<Description>`) is a StationeersLaunchPad publish-time validation, not a base-game limit and not a limit Steam imposes at parse time.

## ChangeLog and Description 8000-character cap (StationeersLaunchPad validation)
<!-- verified: 0.2.6228.27061 @ 2026-05-21 -->

The cap originates in StationeersLaunchPad's `PublishItemValidator`, which runs when a local mod is published through the StationeersLaunchPad publish UI. The validator checks each field in turn and returns a `(bool, string)` tuple; the first failure stops the publish and surfaces the error string:

```csharp
string name = mod.About.Name;
if (name != null && name.Length > 128)
{
    return (false, $"Mod name is larger than {128} characters, current size is {mod.About.Name?.Length} characters.");
}
string description = mod.About.Description;
if (description != null && description.Length > 8000)
{
    return (false, $"Mod description is larger than {8000} characters, current size is {mod.About.Description?.Length} characters.");
}
string changeLog = mod.About.ChangeLog;
if (changeLog != null && changeLog.Length > 8000)
{
    return (false, $"Mod changelog is larger than {8000} characters, current size is {mod.About.ChangeLog?.Length} characters.");
}
```

(StationeersLaunchPad.dll, PublishItemValidator, lines 5804-5818.)

Enforcement behavior:

- Over-limit content is rejected, not truncated. The validator returns `false` with the error string and the publish operation aborts. The author must shorten the field and retry.
- The check is `string.Length`, i.e. UTF-16 code-unit count, so it counts characters, not UTF-8 bytes.
- The same validator also caps `Name` at 128 characters and the About-folder `thumb.png` at 1 MB (1048576 bytes; lines 5819-5827).

The cap applies only at publish time through StationeersLaunchPad. Editing About.xml by hand and loading the mod locally never triggers it.

## ChangeLog is a StationeersLaunchPad-only element
<!-- verified: 0.2.6228.27061 @ 2026-05-21 -->

The base game has no concept of a changelog in mod metadata. Its `ModAbout` class (the `[XmlRoot("ModMetadata")]` type the game deserializes About.xml into) declares only Name, Author, Version, Description, WorkshopHandle, Tags, and an `[XmlIgnore]` IsValid flag. There is no ChangeLog field, and `ModAbout.Load` does no length validation:

```csharp
[XmlRoot("ModMetadata")]
public class ModAbout
{
    public const string ROOT_NAME = "ModMetadata";
    [XmlElement] public string Name;
    [XmlElement] public string Author;
    [XmlElement] public string Version;
    [XmlElement] public string Description;
    [XmlElement] public ulong WorkshopHandle;
    [XmlArray("Tags")][XmlArrayItem("Tag")] public List<string> Tags;
    [XmlIgnore] public bool IsValid = true;

    public static ModAbout Load(string xmlFile)
    {
        // ... locates About/About.xml relative to the gamedata folder ...
        return XmlSerialization.Deserialize<ModAbout>(path);
    }
}
```

(Assembly-CSharp.dll, ModAbout, lines 38046-38087.)

`<ChangeLog>` is added by StationeersLaunchPad's own `ModAboutEx` class, which extends the metadata schema with the extra element:

```csharp
[XmlElement("ChangeLog", IsNullable = true)]
public string ChangeLog;
```

(StationeersLaunchPad.dll, ModAboutEx, lines 13054-13055.)

Because ChangeLog lives only in `ModAboutEx`, the in-game mod browser and mod-details panels (which read the base `ModAbout`) never see it and never apply a length to it.

## ChangeLog maps to the Steam change note at publish
<!-- verified: 0.2.6228.27061 @ 2026-05-21 -->

When a mod is published, StationeersLaunchPad routes the About.xml `<ChangeLog>` text into Steam's per-update change note (the changelog Steam shows for each update), not the item description. Two Harmony prefixes in StationeersLaunchPad carry it across:

```csharp
[HarmonyPatch(typeof(WorkshopMenu), "PublishMod")]
[HarmonyPrefix]
private static void PublishMod(WorkshopModListItem ____selectedModItem)
{
    ModData val = ((____selectedModItem != null) ? ____selectedModItem.Data : null);
    if (val != null)
    {
        ModAboutEx modAboutEx = XmlSerialization.Deserialize<ModAboutEx>(val.AboutXmlPath, "ModMetadata");
        if (modAboutEx != null)
        {
            SavedChangeLog = modAboutEx.ChangeLog;
            SavedPath = PathReference.op_Implicit(val.DirectoryPath);
        }
    }
}

[HarmonyPatch(typeof(SteamTransport), "Workshop_PublishItemAsync")]
[HarmonyPrefix]
private static void Workshop_PublishItemAsync(WorkShopItemDetail detail)
{
    if (detail != null && detail.Path == SavedPath)
    {
        detail.ChangeNote = SavedChangeLog;
    }
}
```

(StationeersLaunchPad.dll, lines 3650-3674.)

The base game then submits the item with Facepunch.Steamworks (the `Steamworks.Ugc.Editor` fluent builder). `detail.Description` becomes the Workshop item description and `detail.ChangeNote` becomes the change note:

```csharp
Editor seed = ((detail.PublishedFileId == 0L) ? Editor.NewCommunityFile : new Editor(detail.PublishedFileId))
    .WithTitle(detail.Title)
    .WithDescription(detail.Description)
    .WithPreviewFile(detail.PreviewPath)
    .WithContent(detail.Path)
    .WithChangeLog(detail.ChangeNote)
    .WithTag(GetTagFromType(detail.Type))
    .WithPublicVisibility();
// ...
PublishResult result = await seed.SubmitAsync(new WorkshopProgress(isDeleting: false));
```

(Assembly-CSharp.dll, SteamTransport.Workshop_PublishItemAsync, lines 263945-263965.)

The 8000 figure matches the documented Steamworks change-note maximum (`k_cchPublishedDocumentChangeDescriptionMax = 8000`; the description maximum `k_cchPublishedDocumentDescriptionMax` is also 8000). Enforcing 8000 author-side pre-empts a Steam-side rejection at submit, but the limit a mod author actually hits first is the StationeersLaunchPad validator, not Steam itself.

## Summary
<!-- verified: 0.2.6228.27061 @ 2026-05-21 -->

| Aspect | Finding |
|--------|---------|
| Where the cap lives | StationeersLaunchPad `PublishItemValidator`, not the base game and not Steam at parse time |
| Capped fields | `<ChangeLog>` 8000, `<Description>` 8000, `Name` 128, `thumb.png` 1 MB |
| Unit | `string.Length` (UTF-16 code units), i.e. characters, not UTF-8 bytes |
| When it applies | Only when publishing through the StationeersLaunchPad UI |
| Over-limit behavior | Publish rejected with an error message; no truncation |
| In-game display | Unaffected; the base-game `ModAbout` class has no ChangeLog field |
| Steam mapping | `<ChangeLog>` to the Steam change note, `<Description>` to the item description |

## Verification history

- 2026-05-21: Page created. Decompiled StationeersLaunchPad.dll and Assembly-CSharp.dll (v0.2.6228.27061) to trace the 8000-character ChangeLog cap. Confirmed the limit is a StationeersLaunchPad `PublishItemValidator` check (rejection, not truncation), that the base-game `ModAbout` class carries no ChangeLog field, and that `<ChangeLog>` is routed to the Steam change note via the `Workshop_PublishItemAsync` Harmony prefix and the Facepunch.Steamworks `.WithChangeLog(...)` builder call.

## Open questions

None.
