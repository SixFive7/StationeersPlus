---
title: Key Binding
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-27b
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: KeyManager
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: KeyMap
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: KeyItem
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: KeyWrap
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: ControlsAssignment
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: InputKeyWindow
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Settings.SettingData
related:
  - ../GameClasses/CameraController.md
  - ./ScrollInputHandling.md
tags: [ui]
---

# Key Binding

Stationeers' rebindable input system stores per-action key assignments by name in a player-local XML settings file, with full preservation of left/right modifier-key distinction at every layer (Unity input, in-memory storage, on-disk persistence, and the rebind UI). Mods that want to programmatically rebind a key (or detect a specific left/right modifier) work entirely within Unity's `KeyCode` vocabulary, with no abstraction layer in the way.

## Layer summary
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

| Layer | API surface | Distinguishes Left/Right? |
|---|---|---|
| Unity input | `Input.GetKey(KeyCode)` / `Input.GetKeyDown(KeyCode)` | Yes — `KeyCode.LeftShift` (304) and `KeyCode.RightShift` (303) are separate enum members; same for Control and Alt |
| KeyManager wrapper | `KeyManager.GetButton(KeyCode key)` (Assembly-CSharp.dll line 43799-43806) | Yes — passes through to `Input.GetKey` after one console-window guard |
| In-memory rebindable map | `KeyMap.ThirdPersonControl`, `KeyMap.MoveAllOfType`, etc. — public static `KeyCode` fields | Yes — each holds one specific KeyCode |
| KeyMap mirror (`_`-prefixed `KeyWrap` objects) | `KeyMap._ThirdPersonControl.AssignKey(KeyCode)` | Yes |
| Per-setting metadata | `KeyItem.Key` (`[XmlElement] public KeyCode Key`, line 42944) | Yes — `KeyCode` field, serialized as enum name |
| On-disk persistence | `<savePath>/setting.xml` written by `Settings.SettingData.Save()` via `XmlSerialization` | Yes — `KeyCode` serializes as enum **name** ("LeftShift" / "RightShift"); round-trips losslessly |
| Rebind UI capture | `InputKeyWindow.Update()` iterates `Enum.GetValues(typeof(KeyCode))` and picks the first one for which `Input.GetKeyDown(keyCode)` returns true | Yes — captures exactly what the player presses |

## KeyManager.GetButton verbatim
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

From `KeyManager` (line 43799):

```csharp
public static bool GetButton(KeyCode key)
{
    if (key != KeyMap.ToggleConsole && ConsoleWindow.IsOpen)
    {
        return false;
    }
    return Input.GetKey(key);
}
```

`GetButtonDown` (line 43781) and `GetButtonUp` (line 43790) follow the identical pattern, calling `Input.GetKeyDown` / `Input.GetKeyUp` respectively. The console-open guard suppresses all input checks except the console-toggle key.

## KeyMap structure
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

`KeyMap` (top-level class, no namespace) holds two parallel sets of fields per binding:

- `public static KeyCode <Name>` — the raw KeyCode for direct comparison.
- `public static readonly KeyWrap _<Name>` — a `KeyWrap` object carrying the same key plus event-binding hooks (`Bind(InputPhase, Action, KeyInputState)`).

Both are kept in sync. Defaults are assigned in a method that runs at startup (line 43358 onwards) like:

```csharp
KeyMap.ToggleLight = KeyCode.L;
KeyMap.MouseControl = KeyCode.LeftAlt;
KeyMap.ThirdPersonControl = KeyCode.LeftShift;
KeyMap.HideAllWindows = KeyCode.BackQuote;
// ...
KeyMap._ToggleLight.AssignKey(KeyCode.L);
KeyMap._MouseControl.AssignKey(KeyCode.LeftAlt);
KeyMap._ThirdPersonControl.AssignKey(KeyCode.LeftShift);
// ...
```

When loading from settings (line 43624 onwards):

```csharp
KeyMap.ThirdPersonControl = GetKey("ThirdPersonControl");
KeyMap._ThirdPersonControl.AssignKey(GetKey("ThirdPersonControl"));
```

`GetKey(string name)` (line 43528) reads from `KeyItemLookup`:

```csharp
public static KeyCode GetKey(string _name)
{
    KeyItemLookup.TryGetValue(_name, out var value);
    return value?.Key ?? KeyCode.None;
}
```

## KeyItem (the per-binding metadata record)
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

From `KeyItem` (line 42938):

```csharp
public class KeyItem
{
    [XmlElement]
    public string Name;

    [XmlElement]
    public KeyCode Key;

    [XmlIgnore]
    public KeyCode DefaultKey;

    [XmlIgnore]
    public int KeyHash;

    [XmlIgnore]
    public ControlsAssignment Display;

    [XmlIgnore]
    public int Index;

    [XmlIgnore]
    public bool Hidden;

    public event Action OnChanged;

    public KeyItem(string name, KeyCode key, bool isHidden = false)
    {
        Name = name;
        Key = key;
        DefaultKey = key;
        Hidden = isHidden;
    }

    public void Changed() => this.OnChanged?.Invoke();
}
```

`KeyItem.Key` is the live binding (mutable). `DefaultKey` is the original assignment (used by "reset to default" UI). The `[XmlElement] KeyCode Key` field is the only thing that gets serialized; XmlSerializer writes enum values as their name strings, preserving Left/Right distinction.

## Persistence: setting.xml
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

`KeyManager.AllKeys` is the master `List<KeyItem>`. `Settings.SaveSettings()` (line 249472) bundles it into the live settings record:

```csharp
public static void SaveSettings()
{
    CurrentData.SettingsVersion = GameManager.GetGameVersion();
    // ... controller axes ...
    CurrentData.KeyList = KeyManager.AllKeys;
    // ... voice notifications, audio mode ...
    CurrentData.Save();
}
```

The save path resolves to `<save folder>/setting.xml` (Settings.SettingData.Path getter, line 248582):

```csharp
[XmlIgnore]
public static string Path
{
    get
    {
        if (string.IsNullOrEmpty(_path))
        {
            _path = System.IO.Path.Combine(StationSaveUtils.GetSavePath(), "setting.xml");
        }
        return _path;
    }
}

public void Save()
{
    this.SaveXml(Path);
}
```

`KeyList` is serialized as a sequence of `KeyItem` elements, each with `<Name>...</Name>` and `<Key>...</Key>`. Example:

```xml
<KeyItem>
  <Name>ThirdPersonControl</Name>
  <Key>LeftShift</Key>
</KeyItem>
```

(or `<Key>RightShift</Key>` if the player rebound it).

## Rebind UI capture path
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

`InputKeyWindow.Update()` (line 222669) is the per-frame poll that captures the next-pressed key once a rebind has been initiated:

```csharp
public static Array KeyCodes = Enum.GetValues(typeof(KeyCode));

private void Update()
{
    if (!InputControlBase.IsRecording)
    {
        return;
    }
    foreach (KeyCode keyCode in KeyCodes)
    {
        if (Input.GetKeyDown(keyCode))
        {
            _currentKey = keyCode;
            Instance.AssignmentText.text = Localization.GetKeyName(_currentKey).ToUpper();
            InputControlBase.IsRecording = false;
            AssignmentAnimator.SetBool("Input", value: false);
            break;
        }
    }
}
```

`Enum.GetValues(typeof(KeyCode))` enumerates ALL Unity `KeyCode` members including `LeftShift` (304), `RightShift` (303), `LeftAlt`, `RightAlt`, `LeftControl`, `RightControl` as **separate entries**. The first one for which `Input.GetKeyDown(keyCode)` returns true wins.

Subtlety: enum iteration order matches numeric order. `RightShift` (303) appears before `LeftShift` (304); same for Control (Right=305, Left=306) and Alt (Right=307, Left=308). If the player holds BOTH shifts down before pressing for rebind, RightShift gets captured first because the loop breaks on the first match. In normal use (player presses one specific key), the captured value matches the physical key pressed.

On submit, `InputKeyWindow.ButtonInputSubmit()` (line 222688) calls `_currentAssignment.Assign(_currentKey)`:

```csharp
public void Assign(KeyCode key, bool refresh = true)
{
    Name.text = KeyItem.Name.ToProper();
    Assignment.text = Localization.GetKeyName(key);
    KeyItem.Key = key;
    Register(KeyItem);
    if (refresh)
    {
        KeyManager.LoadKeyboardSetting();
    }
    KeyItem.Changed();
}
```

`KeyManager.LoadKeyboardSetting()` (line 43571) re-reads every binding from `KeyItemLookup` into the corresponding `KeyMap.<Name>` field plus the `_<Name>.AssignKey(...)` mirror. Then the next `KeyManager.GetButton(KeyMap.<Name>)` call sees the new value.

## Programmatic rebind from a mod
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

To change a key binding from mod code (e.g. to resolve a default-binding conflict):

```csharp
if (KeyManager.KeyItemLookup.TryGetValue("ThirdPersonControl", out var keyItem) && keyItem != null)
{
    if (keyItem.Key == KeyCode.LeftShift)
    {
        keyItem.Key = KeyCode.RightShift;
        KeyMap.ThirdPersonControl = KeyCode.RightShift;
        KeyMap._ThirdPersonControl.AssignKey(KeyCode.RightShift);
        keyItem.Changed();      // fires OnChanged listeners
        Settings.SaveSettings();// persists the updated KeyList to setting.xml
    }
}
```

All four updates are required for a clean rebind:

- `keyItem.Key` — for next `LoadKeyboardSetting` re-sync and for any code that reads via `KeyItemLookup`.
- `KeyMap.<Name>` — for any code that reads the raw KeyCode field directly (e.g. vanilla camera handler reads `KeyManager.GetButton(KeyMap.ThirdPersonControl)`).
- `KeyMap._<Name>.AssignKey(KeyCode)` — for the `KeyWrap`-driven event bindings (`Bind(InputPhase, Action, ...)`).
- `Settings.SaveSettings()` — to persist; without it the change is in-memory-only and reverts on next launch.

`keyItem.Changed()` is optional but recommended — it fires the `OnChanged` event so any UI listening to the binding (e.g. the controls panel showing the assigned key) refreshes.

`Settings.SaveSettings()` writes the entire `setting.xml`, not just the changed key. It is inexpensive and safe to call after any in-memory settings change.

## MouseInspect: a vanilla LeftShift binding that does NOT conflict with bare-Shift mods
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

`KeyMap.MouseInspect` defaults to `KeyCode.LeftShift` (Assembly-CSharp.dll line 43370):

```csharp
KeyMap.MouseInspect = KeyCode.LeftShift;
```

Despite sharing the LeftShift default with `KeyMap.ThirdPersonControl`, MouseInspect is in `KeyManager.NeverConflict` (line 43046):

```csharp
public static List<string> NeverConflict = new List<string> { "FovRefresh", "MouseInspect" };
```

This explicitly exempts MouseInspect from the in-game key-conflict warning UI even when its key collides with another binding's key. The exemption exists because MouseInspect is structured as a SECONDARY modifier that requires a primary mode to be active first — its three usage sites are all gated by being in a different game state than `Mode.Normal`:

| Site | Class.Method | Gate | Effect |
|---|---|---|---|
| Click suppression | `SlotDisplay.PrimaryAction` (line 229295), `SlotDisplay.SecondaryAction` (line 229314) | `InputMouse.IsMouseControl` (default Alt held — `Cursor.visible == true`) | Suppresses click and smart-swap actions while in mouse-control mode (so the player can hover over slots without accidentally clicking) |
| Placement-rotation reverse | `InventoryManager.PlacementMode` (line 270450) | `CurrentMode == Mode.Placement` (player holding a constructor) | Reverses scroll-rotation: scroll-up rotates "previous" instead of "next" |
| Precision-placement | `InventoryManager.PrecisionPlacementMode` (line 270658) | `CurrentMode == Mode.PrecisionPlacement` | Similar reverse-rotation modifier in precision mode |

Implication for scroll-modifier mods that bind LeftShift in `Mode.Normal` (e.g. EquipmentPlus's lens cycle): no conflict. When MouseInspect's first usage condition is true (`InputMouse.IsMouseControl`, i.e. Alt held), the cursor is visible — and `InventoryManager.ManagerUpdate` early-returns before calling either `CheckDisplaySlotInput` or `NormalMode` (line 269276 — see `./ScrollInputHandling.md` "Cursor-visible gate"). When MouseInspect's other usages fire, `CurrentMode` is `Mode.Placement` or `Mode.PrecisionPlacement`, so the dispatch switch routes to those mode handlers and skips `NormalMode`. A mod that prefixes `InventoryManager.NormalMode` is therefore never running while MouseInspect could fire.

The shared LeftShift default is annoying-looking in the controls UI but operationally harmless. Mods with a bare-LeftShift scroll binding can ignore it.

## Implications for scroll-modifier mods
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

For mods that bind scroll combos involving the Shift modifier:

- `CameraController.CacheCameraPosition` (see [`../GameClasses/CameraController.md`](../GameClasses/CameraController.md)) gates its zoom block on `KeyManager.GetButton(KeyMap.ThirdPersonControl)`, default `KeyCode.LeftShift`.
- A mod using `KeyManager.GetButton(KeyCode.LeftShift)` (or `Input.GetKey(KeyCode.LeftShift)`) for a scroll modifier would conflict with the vanilla camera zoom on default keybinds — both fire on bare LeftShift+scroll.
- A mod can sidestep this WITHOUT touching the vanilla camera handler by:
  1. Programmatically rebinding `KeyMap.ThirdPersonControl` from `LeftShift` to `RightShift` (above pattern), only when it currently equals the default LeftShift (respect player customization).
  2. Detecting `KeyCode.LeftShift` specifically in the mod's own scroll handler.
- After the rebind, vanilla camera fires only on RightShift+scroll; LeftShift+scroll is free for the mod.

This avoids a Harmony patch on the camera handler while preserving vanilla camera-zoom functionality on a different physical key. Trade-off: the player's first-launch experience shows the camera key moved unexpectedly; the mod must surface this (in-game chat message or README) to avoid surprise.

## Verification history

- 2026-04-27: page created. All sections verified by `ilspycmd` decompile of `E:/Steam/steamapps/common/Stationeers/rocketstation_Data/Managed/Assembly-CSharp.dll` at game version 0.2.6228.27061. Triggered by EquipmentPlus's TODO B modifier-redesign discussion (resolving the "ALT is reserved by the game, can we use Left/Right shift distinction instead?" question). Documents the full input pipeline from Unity's `Input.GetKey` through `KeyManager.GetButton`, `KeyMap.<Name>` raw fields, `KeyItem.Key` XML-serialized records, on-disk `setting.xml` persistence, and the `InputKeyWindow.Update` rebind capture loop. Confirms Left/Right modifier distinction is preserved at every layer with concrete verbatim code excerpts. Also documents the four-step programmatic-rebind pattern for mods that want to resolve binding conflicts without patching vanilla.
- 2026-04-27b: added "MouseInspect: a vanilla LeftShift binding that does NOT conflict with bare-Shift mods" section. Triggered by an EquipmentPlus user question about the apparent shared-LeftShift conflict between `KeyMap.MouseInspect` (default LeftShift) and EquipmentPlus's lens-cycle binding (also LeftShift+scroll). Verified via decompile that MouseInspect is in `KeyManager.NeverConflict` (line 43046) and that all three of its usage sites are gated by game state different from `Mode.Normal` (mouse-control mode active = `Cursor.visible`; or `CurrentMode == Mode.Placement / PrecisionPlacement`). Mods that prefix `InventoryManager.NormalMode` are never running concurrently with MouseInspect. Additive section; no prior content changed. Top-level `verified_at` advanced to 2026-04-27b to indicate a same-day update.

## Open questions

None at creation.
