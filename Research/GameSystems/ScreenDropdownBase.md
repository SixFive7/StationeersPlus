---
title: ScreenDropdownBase Dropdown Population
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-26
sources:
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs:239469-239517
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs:238727-238802
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs:238907-239103
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs:188524-188550
  - Research/GameSystems/LogicType.md
related:
  - ./LogicType.md
  - ./IC10SyntaxHighlighting.md
tags: [logic, ui]
---

# ScreenDropdownBase Dropdown Population

`ScreenDropdownBase` is the base class for the LogicType dropdowns owned by `LogicMotherboard` (`Assembly-CSharp.decompiled.cs:315578`). The LogicMotherboard is the visual "if device X meets condition Y, then write Z" editor that installs into Big Screens, Wall Screens, and Consoles. The IC Editor cartridge (`ProgrammableChipMotherboard`, `Assembly-CSharp.decompiled.cs:317577`) is a different motherboard and has no LogicType dropdowns at all - it carries only a `ButtonDropdown _dropdown` for selecting which connected device is in scope for the IC10 source. Any "LogicType dropdown on a Big Screen" the player sees comes from a LogicMotherboard cartridge, not the IC Editor.

The dropdowns are populated by two `ScreenDropdownBase` subclasses: `ScreenAction` (write side, instantiated from `LogicMotherboard.ActionPrefab`) and `ScreenCondition` (read side, instantiated from `LogicMotherboard.ConditionPrefab`). Both rebuild their option list dynamically every time `RefreshAll` or `OnDeviceChanged` fires, by iterating the static `ScreenDropdownBase.LogicTypes` array and filtering each entry through the currently-targeted `Parent.Device.CanLogicWrite(...)` (action side) or `Parent.Device.CanLogicRead(...)` (condition side).

## ScreenDropdownBase class structure
<!-- verified: 0.2.6228.27061 @ 2026-05-26 -->

Assets.Scripts.UI.Motherboard.ScreenDropdownBase is the base class for motherboard condition/action dropdown UI. It maintains three static fields:

public static List<Dropdown.OptionData> OptionData = new List<Dropdown.OptionData>();
public static string[] LogicTypeNames = Enum.GetNames(typeof(LogicType));
public static LogicType[] LogicTypes = new LogicType[LogicTypeNames.Length];
private static bool _isInitialized;

The LogicTypeNames and LogicTypes arrays are populated exactly once in Awake:

protected virtual void Awake()
{
    if (!_isInitialized)
    {
        Array values = Enum.GetValues(typeof(LogicType));
        for (int i = 0; i < values.Length; i++)
        {
            LogicTypes[i] = (LogicType)values.GetValue(i);
        }
    }
    Device.ReplaceRaycasters();
    Type.ReplaceRaycasters();
    Value.ReplaceRaycasters();
}

The _isInitialized flag prevents re-initialization, but it is never set to true in the game code. This means the population block theoretically runs every Awake, but the static arrays remain unchanged since they are not re-allocated.

## Dropdown population in ScreenAction and ScreenCondition
<!-- verified: 0.2.6228.27061 @ 2026-05-26 -->

ScreenAction and ScreenCondition subclasses use ScreenDropdownBase.LogicTypes to populate their Type dropdowns. ScreenAction.PopulateTypes iterates the frozen array and filters by device capability:

public void PopulateTypes()
{
    Type.ClearOptions();
    if (!Parent.Device) { /* ... */ return; }
    ScreenDropdownBase.OptionData.Clear();
    int num = -1;
    int num2 = 0;
    LogicType[] logicTypes = ScreenDropdownBase.LogicTypes;
    for (int i = 0; i < logicTypes.Length; i++)
    {
        LogicType logicType = logicTypes[i];
        if (Parent.Device.CanLogicWrite(logicType))
        {
            if (logicType == Parent.Type) num = num2;
            ScreenDropdownBase.OptionData.Add(new Dropdown.OptionData
            { text = logicType.ToString() });
            num2++;
        }
    }
    if (ScreenDropdownBase.OptionData.Count == 0) { /* ... */ return; }
    Type.interactable = true;
    Type.AddOptions(ScreenDropdownBase.OptionData);
}

Every time the dropdown opens or RefreshAll is called, PopulateTypes rebuilds the OptionData list by filtering ScreenDropdownBase.LogicTypes. The dropdown options are live each render cycle, but the underlying LogicTypes array is frozen at startup.

ScreenCondition.PopulateTypes follows the identical pattern, as does ScreenCondition.PopulateOperators which clears and rebuilds ScreenDropdownBase.OptionData for operator selection.

## Caching and snapshot behavior
<!-- verified: 0.2.6228.27061 @ 2026-05-26 -->

The dropdown options themselves are rebuilt dynamically every render, but the list of possible LogicType values comes from the frozen static array:

Component                              When built              When frozen        Mutation point
ScreenDropdownBase.LogicTypes array    First Awake() call     Never re-init      Enum.GetValues at Awake
ScreenDropdownBase.OptionData list     Every PopulateTypes()  Per-cycle           Filters LogicTypes by device
Dropdown UI options                    Every AddOptions()     Per-refresh         Unity Dropdown component

Custom LogicTypes registered at runtime appear in:
- EnumCollections.LogicTypes (extended via Harmony) - Tablet UI sees them
- Logicable.LogicTypes (extended via Harmony) - NextLogicType cycling sees them
- ScreenDropdownBase.LogicTypes (frozen at Awake) - Motherboard UI DOES NOT see them

## Why custom LogicTypes don't appear in motherboard dropdowns
<!-- verified: 0.2.6228.27061 @ 2026-05-26 -->

The three-registry architecture documented in LogicType.md requires extending ScreenDropdownBase.LogicTypes via the LogicableInitializePatch. However, this patch only works if it fires before any ScreenDropdownBase.Awake() is called. Since ScreenDropdownBase is instantiated when a Big Screen or Console spawns (typically during gameplay), and BepInEx plugin initialization runs in a separate phase, the race condition is common: the Awake fires before the Harmony patch extends the array.

Additionally, ScreenDropdownBase has no explicit rebuild mechanism. Unlike EnumCollections.LogicTypes (rebuilt via Harmony postfix) or Logicable.LogicTypes (iterable via reflection), ScreenDropdownBase.LogicTypes is a frozen array with no setter or refresh hook. Once populated at Awake, it cannot be extended without either:

1. Patching ScreenDropdownBase.Awake before any instance runs (risky; races with scene load)
2. Patching ScreenDropdownBase.PopulateTypes postfix to inject missing types into OptionData each render (workaround; not integrated into game design)

## Dropdown options filter by CanLogicWrite / CanLogicRead at refresh time
<!-- verified: 0.2.6228.27061 @ 2026-05-26 -->

Each `PopulateTypes` call rebuilds `ScreenDropdownBase.OptionData` from scratch via `ClearOptions` + `AddOptions`, so the dropdown UI is not statically cached. The filter, however, is the targeted device's `CanLogicWrite(LogicType)` (`ScreenAction`, `Assembly-CSharp.decompiled.cs:238771`) or `CanLogicRead(LogicType)` (`ScreenCondition`, `Assembly-CSharp.decompiled.cs:239082`). A LogicType only appears in the dropdown if all three conditions hold simultaneously:

1. The LogicType ushort is present in `ScreenDropdownBase.LogicTypes` at the moment of the call.
2. The currently-bound `Parent.Device` is the actual target device (not null, not a stale prefab reference).
3. `Parent.Device.CanLogicWrite(logicType)` (action) or `CanLogicRead(logicType)` (condition) returns true for that specific device at that moment.

`PopulateTypes` is triggered by `Initialize(parent, index)`, `RefreshAll()`, and `OnDeviceChanged()` (`Assembly-CSharp.decompiled.cs:238734, 238740, 238746`). There is NO event that re-fires `PopulateTypes` when the `CanLogicWrite/Read` capability set of an already-selected device changes at runtime. Consequence: if a mod patches `CanLogicWrite` to add a new writable LogicType to a device that is already selected as the dropdown's `Parent.Device`, the dropdown will not pick up the new entry until the player triggers a refresh (re-selecting the device from the device dropdown is the standard trigger).

`PopulateValue` (`Assembly-CSharp.decompiled.cs:238804`) uses a hard-coded `switch (Parent.Type)` over `LogicType.None, Power, Activate, Open, Error, Lock, Mode, Color`. Any other LogicType (including every custom mod-registered LogicType) falls into the `default` arm, which surfaces the value as a raw number plus an "Enter New Value" option. Custom boolean-shaped LogicTypes (mode flags, on/off toggles) therefore do not render as "Off / On" or "False / True" in this dropdown - they render numerically. There is no extension point in the switch.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-05-26 -->

- 2026-05-26: page created. Decompiled ScreenDropdownBase at v0.2.6228.27061 from Assembly-CSharp.decompiled.cs lines 239469-239517. Verified that LogicTypes array is built in Awake via Enum.GetValues, with _isInitialized flag never set to true (incomplete game code). Traced dropdown population in ScreenAction.PopulateTypes (lines 238755-238802) and ScreenCondition.PopulateTypes (lines 239075-239103), confirming both re-fetch ScreenDropdownBase.LogicTypes and filter dynamically. Verified consistency with LogicType.md three-registry pattern.
- 2026-05-26: intro corrected. Earlier draft attributed the LogicType dropdowns to "the IC Editor motherboard and other Big Screen/Console cartridges." Re-reading the decompile confirms the dropdowns belong to `LogicMotherboard` (`Assembly-CSharp.decompiled.cs:315578`), which owns `ConditionPrefab : ScreenCondition` and `ActionPrefab : ScreenAction`. The IC Editor cartridge is `ProgrammableChipMotherboard` (`Assembly-CSharp.decompiled.cs:317577`); its only dropdown is a `ButtonDropdown _dropdown` for device selection (line 317606), with no LogicType dropdown anywhere on it. Intro paragraph rewritten accordingly.
- 2026-05-26: added "Dropdown options filter by CanLogicWrite / CanLogicRead at refresh time" section documenting the per-call dynamic filter, the three trigger points for `PopulateTypes`, the absence of a re-fire on capability changes, and the `PopulateValue` switch's hard-coded LogicType set that forces every custom LogicType into the numeric default arm.

## Open questions

- Why is _isInitialized never set to true in the game code? Is this intentional or a bug?
- Can a Harmony postfix to ScreenDropdownBase.PopulateTypes safely inject custom LogicTypes into OptionData without affecting other screens?
- What is the exact load order: when does ScreenDropdownBase.Awake fire relative to BepInEx plugin patch application?
- Sections "Caching and snapshot behavior" and "Why custom LogicTypes don't appear in motherboard dropdowns" claim that the `Awake` re-init destroys reflection-based extensions of `ScreenDropdownBase.LogicTypes`. A direct re-read of the `Awake` body (`Assembly-CSharp.decompiled.cs:239491-239504`) shows the loop bound is `values.Length` where `values = Enum.GetValues(typeof(LogicType))` - i.e. the underlying enum's count, which is fixed at game build time and unaffected by reflection-based array replacement on the static field. Indices at and beyond `values.Length` are not touched. This suggests an extension that replaces `ScreenDropdownBase.LogicTypes` with a longer array survives subsequent `Awake` calls intact for the appended slots. Needs fresh-validator review per WORKFLOW.md Rule 3 before the contested sections are rewritten.
