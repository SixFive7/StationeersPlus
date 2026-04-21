---
title: ModDeduplication
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-21
sources:
  - BepInEx/plugins/StationeersLaunchPad/StationeersLaunchPad.dll :: StationeersLaunchPad.Metadata.ModSet.TryGetExisting
  - BepInEx/plugins/StationeersLaunchPad/StationeersLaunchPad.dll :: StationeersLaunchPad.Metadata.ModList.DisableDuplicates
  - BepInEx/plugins/StationeersLaunchPad/StationeersLaunchPad.dll :: StationeersLaunchPad.Configs
related:
  - ./ModLoadSequence.md
  - ../Patterns/AboutXmlWorkshopHandleParse.md
  - ./ThirdPartyModIdentities.md
tags: [launchpad, packaging]
---

# ModDeduplication

When the same mod is present in more than one source (Workshop subscription, local deploy under `Documents\My Games\Stationeers\mods\`, or a Repo source) and both copies are enabled in the in-game mod list, StationeersLaunchPad deduplicates them before handing the winning copy to BepInEx. Which copy wins depends on a priority table configurable via the mod config; the default is Repo (3) > Local (2) > Workshop (1). The losing copy has `Enabled = false` set on its `ModInfo` and is skipped at plugin-load time. This page documents the match key, the priority rule, the side effects on the mod-list UI, and the failure mode when the match key does not match.

## Match key: Workshop handle, then ModID, then reference
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

`StationeersLaunchPad.Metadata.ModSet.TryGetExisting(ModInfo mod, out ModInfo existing)` is the single routine that decides whether a newly-enumerated `ModInfo` duplicates an already-known `ModInfo`. The check runs in order:

```csharp
public bool TryGetExisting(ModInfo mod, out ModInfo existing)
{
    if (mod.WorkshopHandle > 1 && byWorkshopHandle.TryGetValue(mod.WorkshopHandle, out existing))
    {
        return true;
    }
    if (!string.IsNullOrEmpty(mod.ModID) && byModID.TryGetValue(mod.ModID, out existing))
    {
        return true;
    }
    return all.TryGetValue(mod, out existing);
}
```

Rules that follow from the code:

- **Workshop handle is checked first.** A local deploy whose `About.xml` carries the real numeric `<WorkshopHandle>` will be recognized as a duplicate of its Workshop-subscribed sibling even before the `ModID` pass runs. Setting `<WorkshopHandle>0</WorkshopHandle>` on un-published copies (per the repo-root `CLAUDE.md` "Content: About.xml structure and XML safety" rule) keeps the handle test from matching; this is correct behavior for `Plans/` mods that have no Workshop presence.
- **ModID is the dedup key that catches developer workflows.** A local dev copy and a Workshop-subscribed copy of the same mod share the `<ModID>` in `About.xml` (both built from the same source) and therefore dedup reliably on that key even if the local copy carries `<WorkshopHandle>0</WorkshopHandle>`.
- **Folder name is not a dedup key.** Two copies in different folders with different `ModID` values will both load, even if the folders are identically named.
- **Reference-equality fallback** (`all.TryGetValue`) is a hash-set contains-check against the same `ModInfo` instance; it never matches two distinct enumerations.

## Priority table and the winner
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

`StationeersLaunchPad.Configs` reads the `DedupeMode` config value at init and populates a four-slot priority array used by `DisableDuplicates`:

```csharp
int[] array = ((value == "KeepLocal") ? new int[4] { 1, 2, 1, 3 }
             : ((!(value == "KeepWorkshop")) ? new int[4] { 0, 2, 1, 3 }
                : new int[4] { 1, 1, 2, 3 }));
// indices: [0]=dedupe-enabled, [1]=Local priority, [2]=Workshop priority, [3]=Repo priority
```

Three modes:

| DedupeMode | Local | Workshop | Repo | Winner order |
|---|---|---|---|---|
| (default) | 2 | 1 | 3 | Repo > Local > Workshop |
| `KeepLocal` | 2 | 1 | 3 | Repo > Local > Workshop (same numbers; slot [0] flips from 0 to 1) |
| `KeepWorkshop` | 1 | 2 | 3 | Repo > Workshop > Local |

Higher number wins. A Workshop subscription of `SprayPaintPlus` and a local deploy of the same mod both enabled, with default config, resolves to the **local copy running and the Workshop copy disabled**.

## Disable step on the losing copy
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

`ModList.DisableDuplicates()` walks the mod list, calls `ModSet.TryGetExisting` for each enabled mod, and when a match is found, swaps in whichever `ModInfo` has the higher priority score. The excerpt:

```csharp
int num = source switch
{
    ModSourceType.Local => value,     // Local priority
    ModSourceType.Workshop => value2, // Workshop priority
    ModSourceType.Repo => value3,     // Repo priority
    _ => int.MinValue,
};
int num3 = num; // computed the same way for the new ModInfo

if (num3 > num2)
{
    ModInfo modInfo2 = modInfo;
    modInfo = existing;
    existing = modInfo2;
}

modSet.Remove(modInfo);
modSet.Add(existing);
modInfo.Enabled = false;
Logger.Global.LogWarning($"{item.Source} {item.Name} disabled in favor of {existing2.Source} {existing2.Name}");
```

Effects:

- The losing `ModInfo` has its `Enabled` field set to `false` in memory. The decision is not written back to `modconfig.xml`; it is recomputed at the next config load.
- A `LogWarning` is emitted to `BepInEx/LogOutput.log` of the form `Workshop SprayPaintPlus disabled in favor of Local SprayPaintPlus`. This line is the first-stop diagnostic for "which copy actually ran."
- The in-game mod-list UI still shows **both** entries. The losing copy appears with `Enabled = false` (greyed-out in the UI). The player sees two rows; only one plugin DLL is handed to BepInEx for load.
- `DisableDuplicates` is called from `LaunchPadConfig.StageConfiguring()` at startup and from `HandleChange()` whenever the player edits the mod list in the UI, so the decision refreshes on every mod-list change.

## Failure mode: ModID mismatch defeats dedup
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

The developer-facing footgun: if the Workshop build and the local dev build carry **different** `ModID` values (typo, renamed mid-development, Workshop copy still on an old ModID, local copy on a new one) AND neither carries the same non-zero `WorkshopHandle`, `TryGetExisting` returns false for both and both are left `Enabled = true`.

Two plugin DLLs are then passed to BepInEx. BepInEx has its own dedup layer keyed on `[BepInPlugin]` GUID:

- If both DLLs declare the **same** BepInEx GUID, BepInEx loads only the first one it enumerates (plugin-path enumeration order is filesystem-dependent and not guaranteed between runs).
- If the two DLLs declare **different** BepInEx GUIDs, both load. Harmony patches from both are installed. The result is typically a double-patch (Postfixes run twice, Prefixes both gate the original) and visible misbehavior.

Rule for developers working on a mod in this monorepo while also subscribed to its Workshop listing: keep the `<ModID>` in `About.xml` identical between the committed source and any unreleased local tweaks. The same holds for the BepInEx GUID constant in the plugin class. A mismatch on either key is the cause when "my local changes aren't taking" despite both mods appearing enabled in the UI.

## Diagnostic recipe
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

When a developer suspects the wrong copy ran:

1. Open `BepInEx/LogOutput.log`. Grep for `disabled in favor of`. Each matching line is a dedup decision with source labels (`Local`, `Workshop`, `Repo`) on both sides.
2. If no such line exists for the mod in question, dedup did not fire. Confirm the two copies share `<ModID>` in `About.xml`. If they do not, fix the source of the mismatch; do not rely on `DedupeMode` to paper over it.
3. For a live runtime check, InspectorPlus can dump `ModList.AllMods` (types=[ModInfo], fields=[Name, ModID, Source, Enabled, WorkshopHandle]); compare the expected single enabled entry against the snapshot.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

- 2026-04-21: page created from decompiled `StationeersLaunchPad.dll` (`ModSet.TryGetExisting`, `ModList.DisableDuplicates`, `Configs` priority init), in response to the question "if I'm subscribed on Workshop AND have a local deploy, what runs?" No prior central page on this topic.

## Open questions

None at creation.
