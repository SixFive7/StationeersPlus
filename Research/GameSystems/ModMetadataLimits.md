---
title: Mod Metadata Limits
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-21
sources:
  - rocketstation_Data/Managed/StationeersLaunchPad.dll :: Steam.ValidateForWorkshop(ModInfo) (lines 5794-5829)
  - rocketstation_Data/Managed/StationeersLaunchPad.dll :: Steam.MOD_THUMBNAIL_SIZE_LIMIT const (line 5763)
  - rocketstation_Data/Managed/StationeersLaunchPad.dll :: mod-publish UI caller of ValidateForWorkshop (lines 3712-3720)
  - rocketstation_Data/Managed/StationeersLaunchPad.dll :: ModAboutEx (ChangeLog field 13054-13055; InGameDescription CDataString 13073-13085)
  - rocketstation_Data/Managed/StationeersLaunchPad.dll :: Steam.ToDirName (64-char derived directory name, lines 13020-13028)
  - rocketstation_Data/Managed/StationeersLaunchPad.dll :: WorkshopMenu Harmony prefixes (PublishMod / Workshop_PublishItemAsync, lines 3650-3674)
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: ModAbout (ModMetadata XML root, lines 38046-38087)
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: SteamTransport.Workshop_PublishItemAsync (lines 263945-263965)
related:
  - ./ModLoadSequence.md
  - ../Patterns/AboutXmlWorkshopHandleParse.md
tags: [launchpad]
---

# Mod Metadata Limits

Size and format limits that apply to a mod About.xml. Headline result: the size caps (Name 128, Description 8000, ChangeLog 8000, thumb.png 1 MB) are enforced by StationeersLaunchPad at publish time, in the static method `Steam.ValidateForWorkshop(ModInfo)`. They are not base-game parse limits, and Steam does not truncate anything at display time. Over-limit content blocks the publish: the publish button is disabled and an alert shows the error. The base game applies no length cap when it parses About.xml.

## All About.xml element limits
<!-- verified: 0.2.6228.27061 @ 2026-05-21 -->

| Element | Limit | Type | Source | Over-limit behavior |
|---|---|---|---|---|
| `<Name>` | 128 chars | Hard publish-validation cap | `Steam.ValidateForWorkshop`, line 5805 | Publish blocked, alert shown |
| `<Description>` | 8000 chars | Hard publish-validation cap | `Steam.ValidateForWorkshop`, line 5810 | Publish blocked, alert shown |
| `<ChangeLog>` | 8000 chars | Hard publish-validation cap | `Steam.ValidateForWorkshop`, line 5815 | Publish blocked, alert shown |
| `About/thumb.png` | 1 MB (1048576 bytes), must exist | Hard publish-validation cap | `Steam.ValidateForWorkshop` lines 5819-5827; const `MOD_THUMBNAIL_SIZE_LIMIT` line 5763 | Publish blocked, alert shown |
| `<InGameDescription>` | ~1450 chars (empirical) | UI overflow, no code cap | No length check anywhere; parsed via `ModAboutEx.InGameDescriptionCData` (lines 13073-13085); ~1450 calibrated empirically against PowerTransmitterPlus | Body overflows the visible settings-panel window (visual only; still publishes) |
| `<ModID>` | none on the element | none | `ValidateForWorkshop` does not check it. `Steam.ToDirName` truncates a *derived* directory name at 64 chars (lines 13020-13028), not the element value | none on the element |
| `<Author>` | none found | none | No cap in validator or parser | none |
| `<Version>` | none found | none | No cap in validator or parser | none |
| `<WorkshopHandle>` | must parse as a numeric ulong | XML parse constraint | `XmlConvert.ToUInt64` in the generated reader; empty or non-numeric renames the mod `[Invalid About.xml] <ModID>` at load. See [AboutXmlWorkshopHandleParse](../Patterns/AboutXmlWorkshopHandleParse.md) | Mod loads under broken label |
| `<Tags>` | none enforced by the game | none | `ValidateForWorkshop` does not check tag count or length; tags flow to Steam via `Editor.WithTag` | none game-side |

`ValidateForWorkshop` only validates `Local`-source mods (`mod.Source != ModSourceType.Local` returns `(true, string.Empty)` immediately at line 5796); Workshop and Repo copies are not re-validated. The four size caps match Steam's documented Workshop maxima (title ~128, description 8000, change note 8000: the standard `k_cchPublishedDocument*` constants), so the validator pre-empts a Steam-side rejection author-side. Those Steam constant values are external Steamworks SDK knowledge, not present in the decompile.

## Publish-validation method and how it gates the upload
<!-- verified: 0.2.6228.27061 @ 2026-05-21 -->

The caps live in the static method `Steam.ValidateForWorkshop(ModInfo mod)` in StationeersLaunchPad.dll (line 5794). It checks each field in turn and returns a `(bool ok, string error)` tuple; the first failure returns `false` with a user-facing message:

```csharp
public static (bool, string) ValidateForWorkshop(ModInfo mod)
{
    if (mod.Source != ModSourceType.Local) { return (true, string.Empty); }
    if (mod.About == null) { return (false, "Mod has invalid/no about data."); }
    string name = mod.About.Name;
    if (name != null && name.Length > 128)
        return (false, $"Mod name is larger than {128} characters, current size is {mod.About.Name?.Length} characters.");
    string description = mod.About.Description;
    if (description != null && description.Length > 8000)
        return (false, $"Mod description is larger than {8000} characters, current size is {mod.About.Description?.Length} characters.");
    string changeLog = mod.About.ChangeLog;
    if (changeLog != null && changeLog.Length > 8000)
        return (false, $"Mod changelog is larger than {8000} characters, current size is {mod.About.ChangeLog?.Length} characters.");
    if (!File.Exists(mod.ThumbnailPath)) { return (false, "Mod does not have a thumb.png in the About folder."); }
    FileInfo fileInfo = new FileInfo(mod.ThumbnailPath);
    if (fileInfo != null && fileInfo.Length > 1048576)
        return (false, $"Mod thumbnail size is larger than {1024} kilobytes, ...");
    return (true, string.Empty);
}
```

(StationeersLaunchPad.dll, `Steam.ValidateForWorkshop`, lines 5794-5829. `MOD_THUMBNAIL_SIZE_LIMIT = 1048576` is the named constant at line 5763.)

The caller in the mod-publish UI gates on the result (lines 3712-3720):

```csharp
if (modInfo.Source == ModSourceType.Local)
{
    var (flag, text) = Steam.ValidateForWorkshop(modInfo);
    if (!flag && !string.IsNullOrEmpty(text))
    {
        AlertPanel.Instance.ShowAlert(text, (AlertState)0, 0f);
        selectedModButtonRight.SetActive(false);
    }
}
```

A failed validation shows the error string in an alert panel and disables the publish-side button (`selectedModButtonRight`). So over-limit content blocks the upload until the author shortens the field. Nothing is truncated, and the cap counts `string.Length` (UTF-16 code units), i.e. characters, not UTF-8 bytes. The check applies only to local mods being published through StationeersLaunchPad; editing About.xml by hand and loading the mod locally never triggers it.

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

(StationeersLaunchPad.dll, ModAboutEx, lines 13054-13055.) `ModAboutEx` also carries `InGameDescription`, serialized through a `CDataString` property (`[XmlElement("InGameDescription", IsNullable = true)] CDataString InGameDescriptionCData`, lines 13073-13085); there is no length check on it.

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

The 8000 figure matches the documented Steamworks change-note maximum (`k_cchPublishedDocumentChangeDescriptionMax = 8000`; the description maximum `k_cchPublishedDocumentDescriptionMax` is also 8000). Because each publish submits a fresh change note and Steam keeps the per-update history on the Workshop Change Notes tab, a mod only needs the current version's notes in `<ChangeLog>`; the full history belongs in the per-mod `CHANGELOG.md` (see `Mods/Template/LAYOUT.md`).

## Past Steam Workshop change notes are immutable
<!-- verified: 0.2.6228.27061 @ 2026-05-21 -->

Once an update is submitted, its change note is a permanent entry on the Workshop item's Change Notes tab. There is no Steamworks API to edit or delete a specific historical change note (`SubmitItemUpdate` / `Editor.WithChangeLog` only sets the note for the new update being submitted), and the Steam Workshop creator web UI offers no way to edit past change-note text. Submitting an update with no content change but a new note adds a NEW dated entry rather than editing an old one. The item description, title, tags, and preview ARE freely editable at any time; the per-update change-note history is append-only. The only way to clear the change-note history is to delete and recreate the whole Workshop item, which loses the item ID, subscribers, ratings, and discussions. (Sourced from Steamworks ISteamUGC documentation and Steam community creator discussions, 2026-05-21; not a decompile finding.)

## Verification history

- 2026-05-21: Page created. Decompiled StationeersLaunchPad.dll and Assembly-CSharp.dll (v0.2.6228.27061) to trace the 8000-character ChangeLog cap. Confirmed the limit is a StationeersLaunchPad publish-validation check (rejection, not truncation), that the base-game `ModAbout` class carries no ChangeLog field, and that `<ChangeLog>` is routed to the Steam change note via the `Workshop_PublishItemAsync` Harmony prefix and the Facepunch.Steamworks `.WithChangeLog(...)` builder call.
- 2026-05-21: Corrected and expanded. The validation method is `Steam.ValidateForWorkshop(ModInfo)` at line 5794, NOT `PublishItemValidator` as the page originally stated (a grep for "PublishItemValidator" returns zero hits in StationeersLaunchPad.dll; the original name was an unverified paraphrase from the page's first draft). Read the method body and its caller (lines 3712-3720) directly: a failed validation shows an `AlertPanel` error and disables the publish button, confirming over-limit content blocks the upload. Expanded the page from ChangeLog/Description to a full table of all nine About.xml elements: Name 128 / Description 8000 / ChangeLog 8000 / thumb.png 1 MB are the only hard caps; InGameDescription is a UI overflow (~1450, no code check, parsed via `ModAboutEx.InGameDescriptionCData`); WorkshopHandle must be a numeric ulong; ModID/Author/Version/Tags have no validator cap (`ToDirName` truncates a derived directory name at 64 chars, not the ModID element). Added the immutability of past Steam change notes (web-sourced, not decompile).

## Open questions

None.
