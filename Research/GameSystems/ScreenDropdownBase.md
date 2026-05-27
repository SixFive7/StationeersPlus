---
title: ScreenDropdownBase Dropdown Population
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-28
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
<!-- verified: 0.2.6228.27061 @ 2026-05-28 -->

The dropdown options themselves are rebuilt dynamically every render. The list of possible LogicType values comes from the static `ScreenDropdownBase.LogicTypes` array, seeded from the vanilla enum at Awake but extendable by reflection in a way that survives Awake (see "Awake does not clobber a reflection-extended array" below):

Component                              When built             Re-init behavior                  Mutation point
ScreenDropdownBase.LogicTypes array    Static init + Awake    Awake rewrites indices 0..vanilla-1 only   Enum.GetValues at Awake; reflection-appended tail survives
ScreenDropdownBase.OptionData list     Every PopulateTypes()  Per-cycle                         Filters LogicTypes by device
Dropdown UI options                    Every AddOptions()     Per-refresh                       Unity Dropdown component

Custom LogicTypes registered at runtime appear in:
- EnumCollections.LogicTypes (extended via Harmony) - Tablet UI sees them
- Logicable.LogicTypes (extended via Harmony) - NextLogicType cycling sees them
- ScreenDropdownBase.LogicTypes (extended via reflection; the append survives Awake) - LogicMotherboard / screen dropdowns see them, gated by the per-device CanLogicWrite / CanLogicRead filter

## Awake does not clobber a reflection-extended array
<!-- verified: 0.2.6228.27061 @ 2026-05-28 -->

A mod extends ScreenDropdownBase.LogicTypes by replacing the static field with a longer array (vanilla entries copied into indices 0..vanilla-1, custom values appended after), as PowerGridPlus does in LogicableInitializePatch.ExtendScreenDropdownBase. The extension is durable across ScreenDropdownBase.Awake() and does NOT race with it:

- Awake's fill loop is bounded by `Enum.GetValues(typeof(LogicType)).Length` (the underlying enum's fixed member count, decided at game build time), NOT by `LogicTypes.Length`. It only writes indices 0..vanilla-1.
- Awake does not re-allocate LogicTypes; it writes into the existing array object. A reflection replacement installs the longer array, and subsequent Awake calls overwrite only the vanilla-index prefix, leaving the appended tail intact.
- `_isInitialized` is never set true, so the fill loop runs on every Awake, but the two points above make the re-run idempotent for the appended tail.

Consequence: patch ordering does not matter. The Logicable.Initialize postfix may run before or after any ScreenDropdownBase.Awake; the appended entries survive either way, and there is no rebuild hook to call. A custom LogicType present in the extended array still only SHOWS in a given LogicMotherboard dropdown when that motherboard's targeted device passes the CanLogicWrite / CanLogicRead filter (see the section above) - that filter, not array freshness, is the real gate on visibility.

## Dropdown options filter by CanLogicWrite / CanLogicRead at refresh time
<!-- verified: 0.2.6228.27061 @ 2026-05-26 -->

Each `PopulateTypes` call rebuilds `ScreenDropdownBase.OptionData` from scratch via `ClearOptions` + `AddOptions`, so the dropdown UI is not statically cached. The filter, however, is the targeted device's `CanLogicWrite(LogicType)` (`ScreenAction`, `Assembly-CSharp.decompiled.cs:238771`) or `CanLogicRead(LogicType)` (`ScreenCondition`, `Assembly-CSharp.decompiled.cs:239082`). A LogicType only appears in the dropdown if all three conditions hold simultaneously:

1. The LogicType ushort is present in `ScreenDropdownBase.LogicTypes` at the moment of the call.
2. The currently-bound `Parent.Device` is the actual target device (not null, not a stale prefab reference).
3. `Parent.Device.CanLogicWrite(logicType)` (action) or `CanLogicRead(logicType)` (condition) returns true for that specific device at that moment.

`PopulateTypes` is triggered by `Initialize(parent, index)`, `RefreshAll()`, and `OnDeviceChanged()` (`Assembly-CSharp.decompiled.cs:238734, 238740, 238746`). There is NO event that re-fires `PopulateTypes` when the `CanLogicWrite/Read` capability set of an already-selected device changes at runtime. Consequence: if a mod patches `CanLogicWrite` to add a new writable LogicType to a device that is already selected as the dropdown's `Parent.Device`, the dropdown will not pick up the new entry until the player triggers a refresh (re-selecting the device from the device dropdown is the standard trigger).

`PopulateValue` (`Assembly-CSharp.decompiled.cs:238804`) uses a hard-coded `switch (Parent.Type)` over `LogicType.None, Power, Activate, Open, Error, Lock, Mode, Color`. Any other LogicType (including every custom mod-registered LogicType) falls into the `default` arm, which surfaces the value as a raw number plus an "Enter New Value" option. Custom boolean-shaped LogicTypes (mode flags, on/off toggles) therefore do not render as "Off / On" or "False / True" in this dropdown - they render numerically. There is no extension point in the switch.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-05-28 -->

- 2026-05-26: page created. Decompiled ScreenDropdownBase at v0.2.6228.27061 from Assembly-CSharp.decompiled.cs lines 239469-239517. Verified that LogicTypes array is built in Awake via Enum.GetValues, with _isInitialized flag never set to true (incomplete game code). Traced dropdown population in ScreenAction.PopulateTypes (lines 238755-238802) and ScreenCondition.PopulateTypes (lines 239075-239103), confirming both re-fetch ScreenDropdownBase.LogicTypes and filter dynamically. Verified consistency with LogicType.md three-registry pattern.
- 2026-05-26: intro corrected. Earlier draft attributed the LogicType dropdowns to "the IC Editor motherboard and other Big Screen/Console cartridges." Re-reading the decompile confirms the dropdowns belong to `LogicMotherboard` (`Assembly-CSharp.decompiled.cs:315578`), which owns `ConditionPrefab : ScreenCondition` and `ActionPrefab : ScreenAction`. The IC Editor cartridge is `ProgrammableChipMotherboard` (`Assembly-CSharp.decompiled.cs:317577`); its only dropdown is a `ButtonDropdown _dropdown` for device selection (line 317606), with no LogicType dropdown anywhere on it. Intro paragraph rewritten accordingly.
- 2026-05-26: added "Dropdown options filter by CanLogicWrite / CanLogicRead at refresh time" section documenting the per-call dynamic filter, the three trigger points for `PopulateTypes`, the absence of a re-fire on capability changes, and the `PopulateValue` switch's hard-coded LogicType set that forces every custom LogicType into the numeric default arm.
- 2026-05-28: conflict on "does Awake clobber a reflection-extended ScreenDropdownBase.LogicTypes array". Previous claim (sections "Caching and snapshot behavior" / "Why custom LogicTypes don't appear in motherboard dropdowns"): the array is frozen at Awake, motherboard UI cannot see reflection-appended custom types, and the patch must win a race against Awake. New finding: Awake's loop is bounded by the fixed enum member count and does not re-allocate the array, so an appended tail survives. Fresh validator verdict: Survives (Awake does not clobber the appended tail); loop bound is `Enum.GetValues(typeof(LogicType)).Length`, no re-allocation, `_isInitialized` never set. Result: renamed "Why custom LogicTypes don't appear in motherboard dropdowns" to "Awake does not clobber a reflection-extended array" and rewrote it; corrected the "Caching and snapshot behavior" bullet and framing; removed the corresponding Open Question.

## Open questions

- Why is _isInitialized never set to true in the game code? Is this intentional or a bug?
- Can a Harmony postfix to ScreenDropdownBase.PopulateTypes safely inject custom LogicTypes into OptionData without affecting other screens?
- What is the exact load order: when does ScreenDropdownBase.Awake fire relative to BepInEx plugin patch application? (Per the 2026-05-28 conflict resolution this does not affect whether reflection-appended LogicTypes survive; it only affects the first frame they become visible.)
