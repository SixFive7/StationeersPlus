---
title: StationeersLaunchPadSettingsGrouping
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-03
sources:
  - BepInEx/plugins/StationeersLaunchPad/StationeersLaunchPad.dll :: SortedConfigFile
  - BepInEx/plugins/StationeersLaunchPad/StationeersLaunchPad.dll :: SortedConfigCategory
  - BepInEx/plugins/StationeersLaunchPad/StationeersLaunchPad.dll :: ConfigPanel.DrawConfigFile
  - BepInEx/plugins/StationeersLaunchPad/StationeersLaunchPad.dll :: ConfigPanel.DrawConfigEntry
  - BepInEx/plugins/StationeersLaunchPad/StationeersLaunchPad.dll :: ConfigPanel.DrawConfigEditor
  - BepInEx/plugins/StationeersLaunchPad/StationeersLaunchPad.dll :: ConfigEntryWrapper
  - BepInEx/plugins/StationeersLaunchPad/StationeersLaunchPad.dll :: EssentialPatches.DrawInGameWindows, ConfigPanel.DrawSettingsWindow, ManualLoadWindow.DrawModConfigTab, LaunchPadPlugin.Run/StartGame Stage transitions, ConfigPanel.DrawBoolEntry / DrawConfigEntry<T>
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.UI.MainMenu.Awake/Start, InventoryManager.ButtonSettings, WorkshopMenu
related:
  - ../Patterns/ConflictDetection.md
tags: [launchpad, ui]
---

# StationeersLaunchPad Settings Grouping Mechanism

StationeersLaunchPad renders each loaded BepInEx mod's `ConfigEntry` values inside its in-game settings panel. The grouping mechanism is purely BepInEx-native: settings are grouped by the `ConfigDefinition.Section` string (the first argument passed to `Config.Bind`). There is no custom attribute, no StationeersLaunchPad-specific registration API, and no way to nest groups. One section string produces one collapsible header in the GUI.

## Data model

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Two StationeersLaunchPad classes carry the grouped structure:

`SortedConfigFile` (constructor, approximately line 445):

- Takes a BepInEx `ConfigFile`.
- Groups entries by `entry.Definition.Section` via LINQ `group by`.
- Creates one `SortedConfigCategory` per unique section string.
- Sorts the category list alphabetically by section name.

`SortedConfigCategory` (approximately lines 453-476):

- Holds every `ConfigEntryBase` belonging to one section.
- Wraps each entry in a `ConfigEntryWrapper`.
- Sorts entries first by the `Order` tag (if present) then alphabetically by key name.

## Grouping logic

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Verbatim excerpt from the `SortedConfigFile` constructor (approximately line 445):

```csharp
foreach (IGrouping<string, ConfigEntryBase> item in
    from entry in (IEnumerable<KeyValuePair<ConfigDefinition, ConfigEntryBase>>)configFile
    select entry.Value into entry
    group entry by entry.Definition.Section)
{
    list.Add(new SortedConfigCategory(configFile, item.Key, item));
}
list.Sort((a, b) => a.Category.CompareTo(b.Category));
```

Every `ConfigEntry` sharing the same `Section` string lands in the same group. The final `Sort` is alphabetical and cannot be overridden by the mod author.

## Rendering path

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

`ConfigPanel.DrawConfigFile` (approximately lines 7925-7950):

```csharp
public static bool DrawConfigFile(SortedConfigFile configFile, Func<string, bool> categoryFilter = null)
{
    ImGuiHelper.Text(configFile.FileName);
    ImGui.PushID(configFile.FileName);
    bool result = false;
    foreach (SortedConfigCategory category in configFile.Categories)
    {
        if ((categoryFilter != null && !categoryFilter(category.Category)) ||
            !ImGui.CollapsingHeader(category.Category, (ImGuiTreeNodeFlags)32))
        {
            continue;
        }
        ImGui.PushID(category.Category);
        foreach (ConfigEntryWrapper entry in category.Entries)
        {
            if (entry.Visible && DrawConfigEntry(entry))
            {
                result = true;
            }
        }
        ImGui.PopID();
    }
    ImGui.PopID();
    return result;
}
```

Each section renders as an ImGui `CollapsingHeader`. The header label is the section string verbatim.

## Supported per-entry tags

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

StationeersLaunchPad reads tags from `ConfigDescription.Tags` (fourth argument to `Config.Bind`):

| Tag key | Value type | Purpose |
|---|---|---|
| `Order` | `int` | Sort order within a group. Lower values come first; ties fall back to alphabetical by key. |
| `DisplayName` | `string` | Override the config key shown in the GUI. |
| `Format` | `string` | Custom formatting hint for the rendered value. |
| `RequireRestart` | `bool` | Mark entry as requiring a restart. |
| `Disabled` | `bool` | Render the entry but disable editing. |
| `Visible` | `bool` | Hide the entry from the GUI entirely. |
| `CustomDrawer` | `Func<ConfigEntryBase, bool>` | Replace the default widget with a custom ImGui drawer. |

None of these tags affect grouping. There is no tag that moves an entry into a different section, adds a sub-group, or nests categories.

## What StationeersLaunchPad does not support

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

- No custom attributes (no `[StationeersLaunchPadCategory]`, `[ModSettingGroup]`, or similar).
- No registration API for settings.
- No naming conventions beyond the section string (prefixes like `[Client]` in a key name are not recognized, they render verbatim).
- No multi-group entries. One `Config.Bind` call contributes to exactly one section.
- No author-controlled ordering of groups. Sort is alphabetical on the section string.
- No nesting. A section cannot contain a sub-section.

## Example: existing mods in this repo

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

`PowerTransmitterPlus/Plugin.cs` binds three sections, which the StationeersLaunchPad GUI renders as three collapsible headers sorted alphabetically:

```csharp
Config.Bind("Visual", "Beam Width", 0.1f, "...");
Config.Bind("Visual", "Beam Color", "000DFF", "...");
Config.Bind("Visual", "Emission Intensity", 10.0f, "...");
Config.Bind("Pulse", "Stripe Wavelength", 2.0f, "...");
Config.Bind("Pulse", "Scroll Speed", 25.0f, "...");
Config.Bind("Pulse", "Trough Brightness", 0.5f, "...");
Config.Bind("Distance", "Cost Factor (k)", 5f, "...");
```

Rendered order: Distance, Pulse, Visual.

`SprayPaintPlus/Plugin.cs` binds two sections:

```csharp
Config.Bind("Client", "Invert Color Scroll Direction", false, "...");
Config.Bind("Client", "Paint Single Item By Default", false, "...");
Config.Bind("Server", "Unlimited Spray Paint Uses", true, "...");
Config.Bind("Server", "Suppress Spray Paint Pollution", true, "...");
// additional Server-section entries
```

Rendered order: Client, Server.

## Implications for mod authors

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

- The section string is the only grouping knob. Pick it deliberately; it is what players see.
- If deterministic entry ordering within a group matters (for example, to keep a parent toggle above its dependent toggles), add the `Order` tag to each entry via a `new ConfigDescription(desc, null, new KeyValuePair<string, int>("Order", n))`.
- If deterministic group ordering matters (for example, to make a "Server" group appear before a "Client" group), there is no clean solution. Section strings are sorted alphabetically. Naming workarounds (`"1. Server"`, `"A. Server"`) are possible but visible to players.
- Scope labels (Client / Server / etc.) work well as section names because they partition the entry set by who controls the value, which is the most common question a player asks when looking at a settings screen.

## `RequireRestart` rendering behavior

<!-- verified: 0.2.6228.27061 @ 2026-04-23 -->

The `RequireRestart` tag is a cosmetic UI signal. It does not disable the entry, does not defer the config write, and does not affect `ConfigEntry<T>.Value` at all. The value is written to disk by BepInEx immediately when the user edits the widget, exactly like any other entry. The only effect is a per-mod editor banner that appears while the settings panel is open.

`ConfigEntryWrapper` (constructor, approximately lines 29-95) reads the tag from `ConfigDescription.Tags`:

```csharp
else if (obj3 is KeyValuePair<string, bool> keyValuePair2)
{
    switch (keyValuePair2.Key)
    {
    case "RequireRestart":
    {
        bool value2 = keyValuePair2.Value;
        RequireRestart = value2;
        break;
    }
    ...
    }
}
```

`ConfigPanel.DrawConfigEntry<T>` (approximately lines 208-275) tracks changes. `flag` is true when the user interacted with the widget this frame; `value` is the pre-draw `ConfigEntry<T>.Value`; `value2` is the post-draw value:

```csharp
if (wrapper.RequireRestart && flag)
{
    T value2 = val.Value;
    if (!Equals(value, value2))
    {
        if (requireRestartOriginalValues.TryGetValue(wrapper.Entry, out var value3))
        {
            if (Equals(value2, value3))
            {
                requireRestartOriginalValues.Remove(wrapper.Entry);
            }
        }
        else
        {
            requireRestartOriginalValues.Add(wrapper.Entry, value);
        }
    }
}
```

`requireRestartOriginalValues` is a `private static readonly Dictionary<ConfigEntryBase, object>` (approximately line 63). It persists for the process lifetime, not the panel lifetime.

`ConfigPanel.DrawConfigEditor` (approximately lines 154-161) renders one of two banners at the top of each mod's editor based on the dictionary's population:

```csharp
if (requireRestartOriginalValues.Count > 0)
{
    ImGuiHelper.TextColored("Changes in configuration require a restart to apply", new Color(0.863f, 0.078f, 0.235f));
}
else
{
    ImGuiHelper.TextDisabled("These configurations may require a restart to apply");
}
```

The crimson banner (RGB `0.863, 0.078, 0.235`) displays once any `RequireRestart: true` entry has been changed in this process session. If the user reverts the entry to its original value (`Equals(value2, value3)` on line 254), the dictionary entry is removed and the banner reverts to the greyed-out "may require a restart" text.

Implications:

- The banner is only visible while the settings panel is open. Closing the panel does not dismiss the dictionary tracking, but the visual cue is gone. A player who makes a change, closes the panel, and loads a save receives no further prompt.
- The tag does not gate, delay, or warn about loading a world. It only annotates the settings UI.
- "Original" means "value when the player first changed it in this process session," not "value at plugin boot." The two coincide only if nobody has touched the entry in the panel since process start.
- Mods that want runtime enforcement (reject joins, refuse save load, etc.) must implement their own mechanism; `RequireRestart` is advisory only.

## Mid-session mutability

<!-- verified: 0.2.6228.27061 @ 2026-06-03 -->

**Verdict.** StationeersLaunchPad's in-game pause-menu "Settings" button DOES open an ImGui configuration overlay mid-session, but the overlay only renders LaunchPad's own `Configs.Sorted` entries. **Per-mod `ConfigEntry` values bound by third-party mods are unreachable from any UI surface while a save is loaded.** Mod authors who wire `ConfigEntry.SettingChanged` handlers expecting the host to flip a toggle mid-session are wiring dead code; that event never fires from a UI source for per-mod entries during in-world play.

### The in-game settings overlay

`EssentialPatches` Harmony-prefixes `OrbitalSimulation.Draw` to overlay every ImGui window each render frame (`StationeersLaunchPad.dll` L3768-3797):

```csharp
[HarmonyPatch(typeof(OrbitalSimulation), "Draw")]
[HarmonyPrefix]
private static void DrawInGameWindows()
{
    DrawWorkshopMenuConfig();
    DrawSettingsMenuConfig();
    LogPanel.DrawStandaloneLogs();
}

private static void DrawWorkshopMenuConfig()
{
    if (((Behaviour)WorkshopMenu.Instance).isActiveAndEnabled)
    {
        ...
        ConfigPanel.DrawWorkshopConfig(LaunchPadConfig.MatchMod(...));
    }
}

private static void DrawSettingsMenuConfig()
{
    if (((Behaviour)Settings.Instance).isActiveAndEnabled)
    {
        ConfigPanel.DrawSettingsWindow();
    }
}
```

`DrawSettingsWindow` only ever renders LaunchPad's `Configs.Sorted` -- the LaunchPad-internal entries, never any mod's (`StationeersLaunchPad.dll` L7869-7888):

```csharp
public static void DrawSettingsWindow()
{
    ...
    ImGui.Begin("LaunchPad Configuration##menulpconfig", ...);
    DrawConfigFile(Configs.Sorted, (string category) => category != "Internal");
    ImGui.End();
}
```

`Settings.Instance` is the shared Unity panel used by both the main menu and the in-game pause menu. `Assets.Scripts.UI.MainMenu.Awake` fetches it from `AlertCanvas` (`Assembly-CSharp.dll` L226137) and `InventoryManager.ButtonSettings` activates the same GameObject from the pause menu (`Assembly-CSharp.dll` L268906-268910):

```csharp
public void ButtonSettings()
{
    SettingsPanel.SetActive(value: true);
    Settings.Instance.InitCurrentPage();
}
```

So `Settings.Instance.isActiveAndEnabled` flips true on a pause-menu Settings click, the ImGui overlay appears, and the host can freely toggle every LaunchPad-owned entry. None of these entries belong to third-party mods.

### Why per-mod entries are unreachable mid-session

Mod-bound `ConfigEntry`s are rendered by `ConfigPanel.DrawConfigEditor`, which is called from exactly two places, both startup-only:

1. **`ManualLoadWindow.DrawModConfigTab`** -- the pre-load wizard (`StationeersLaunchPad.dll` L9717-9729):

   ```csharp
   private static void DrawModConfigTab(LoadStage stage)
   {
       bool flag = stage <= LoadStage.Loading;
       ImGui.BeginDisabled(flag);
       bool flag2 = ImGui.BeginTabItem("Mod Configuration");
       ...
       if (flag2)
       {
           ConfigPanel.DrawConfigEditor(selectedMod, selectedInfo);
           ...
       }
   }
   ```

   `ManualLoadWindow.Draw` only runs while `AutoLoad` is off and `Stage` is in `Configuring..LoadedEntryPoints` (L3173-3188). Once `LaunchPadPlugin.StartGame` (L3342) sets `Stage = LoadStage.Running`, the wizard's render loop terminates and the window is never drawn again.

2. **`DrawWorkshopMenuConfig`** -- the main-menu Workshop sidebar:

   ```csharp
   if (((Behaviour)WorkshopMenu.Instance).isActiveAndEnabled) { ... }
   ```

   `WorkshopMenu` is a `MainMenuPage` registered to the main-menu `_pageManager`. The only `_workshopButton.onClick` listener calls `_pageManager.EnableMainMenuPage("WorkshopMods")` (`Assembly-CSharp.dll` L226162-226165), bound exclusively to the main-menu button. There is no in-game pause-menu wiring; the single mid-session reference (`CancelKeyActions`, L268995) is a defensive Escape-key handler in case the GameObject is somehow active. `WorkshopMenu.gameObject` lives under the main-menu canvas hierarchy, not on the in-game `GameMenuPanel`.

No `GameManager.GameState`, `World.Loaded`, or simulation-running check guards `DrawInGameWindows` -- the gating is purely on which Unity host panel (`Settings.Instance` vs `WorkshopMenu.Instance`) is currently active. The in-game pause-menu Settings click only activates `Settings.Instance`, which only renders `Configs.Sorted` (LaunchPad-only).

### Call chain on a value change (startup path)

For LaunchPad's own entries mid-session AND any per-mod entry edited at startup, the chain is identical:

1. ImGui widget detects user interaction. Example for `bool` (`StationeersLaunchPad.dll` L8178-8194):

   ```csharp
   public static bool DrawBoolEntry(ConfigEntry<bool> entry, ConfigEntryWrapper wrapper, bool fill)
   {
       bool value = entry.Value;
       ...
       if (ImGui.Checkbox(..., ref value))
       {
           entry.Value = value;
           return true;
       }
       return false;
   }
   ```

2. `ConfigEntry<T>.Value` setter (BepInEx core) calls `SetSerializedValue` then `OwnerMetadata.ConfigFile.OnSettingChanged(this)`, which (a) fires both `ConfigFile.SettingChanged` and `ConfigEntryBase.SettingChanged` events, and (b) writes the whole `ConfigFile` to disk synchronously when `ConfigFile.SaveOnConfigSet` is true (the BepInEx default).

3. `DrawConfigEntry<T>` (L7963-8030) only post-processes the `RequireRestart` cosmetic banner. It does not gate, defer, or suppress the write. Every keystroke through a LaunchPad-drawn widget therefore writes to .cfg AND fires `SettingChanged` synchronously.

LaunchPad itself subscribes once for its own bookkeeping (L14704):

```csharp
configFile.SettingChanged += delegate { ... };
```

so mod-bound entries DO emit `SettingChanged` when edited through LaunchPad's UI -- but only when LaunchPad actually draws the widget, which is only at startup for per-mod entries.

### Implications for mod design

**Do not** wire `ConfigEntry.SettingChanged` handlers expecting "the host flipped this toggle mid-session through LaunchPad." That event cannot fire from a UI source mid-session for any per-mod entry. The PowerGridPlus `PassthroughSettingsSync.HookHostBroadcast` pattern (subscribing to multiple `Settings.Enable*.SettingChanged` events to broadcast a sync message) is mostly dead code in practice -- the only thing that would trigger those handlers mid-session is another mod programmatically assigning to the entry, an IPC handler, a custom in-world UI, or `ConfigFile.Reload()`.

Correct patterns for host-authoritative settings:

- **Join-suffix snapshot** (already used by PassthroughSettingsSync via `Plugin.SerializeJoinSuffix`). Ship the current value to a joining client; this is the only moment values are guaranteed to be both stable and observed by the peer.
- **`RequireRestart = true` tag** on the `ConfigDescription`. Renders the crimson "Changes require a restart to apply" banner in the LaunchPad UI when the value changes. Does NOT gate writes -- purely advisory to the human.
- **Custom in-world UI**, if live tuning is genuinely required. The mod must build its own pause-menu or kit panel and update both `ConfigEntry<T>.Value` and broadcast itself.

`SettingChanged` can still legitimately fire mid-session from non-UI sources: another mod calling `Config.Bind(...).Value = x` programmatically, IPC, a network packet handler, or BepInEx's `ConfigFile.Reload()`. Subscribers should be defensive about those code paths -- but should not assume a host UI toggle is part of the design space.

## Verification history

- 2026-04-21: page created. Verified against `StationeersLaunchPad.dll` in game version 0.2.6228.27061 by decompilation of `SortedConfigFile`, `SortedConfigCategory`, and `ConfigPanel.DrawConfigFile`.
- 2026-04-23: added "RequireRestart rendering behavior" section. Verified against `StationeersLaunchPad.dll` in game version 0.2.6228.27061 by decompilation of `ConfigEntryWrapper`, `ConfigPanel.DrawConfigEntry`, and `ConfigPanel.DrawConfigEditor`. `sources:` frontmatter extended with the three new decompile targets.
- 2026-06-03: added "Mid-session mutability" section after spawning a fresh-validator sub-agent to verify whether StationeersLaunchPad exposes per-mod ConfigEntry values to the host while a save is loaded. Verdict: the in-game pause-menu Settings overlay only renders LaunchPad's own `Configs.Sorted` entries; per-mod entries are only reachable from the main-menu `WorkshopMenu` and the pre-load `ManualLoadWindow`. The PowerGridPlus `PassthroughSettingsSync.HookHostBroadcast` pattern (subscribing to per-mod `SettingChanged` for live host broadcast) was the trigger for this verification -- it cannot fire from a UI source in practice. Frontmatter `sources:` extended with `EssentialPatches`, `ConfigPanel.DrawSettingsWindow`, `ManualLoadWindow.DrawModConfigTab`, `LaunchPadPlugin.Run/StartGame`, `ConfigPanel.DrawBoolEntry`, plus `MainMenu`, `InventoryManager.ButtonSettings`, and `WorkshopMenu` from Assembly-CSharp.

## Open questions

None.
