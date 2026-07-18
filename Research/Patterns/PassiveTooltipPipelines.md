---
title: Passive tooltip pipelines
type: Patterns
created_in: 0.2.6403.27689
verified_in: 0.2.6403.27689
verified_at: 2026-07-18
sources:
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 287864-287975 (InventoryManager.NormalModeThing), 239679-239729 (InputMouse.Idle), 254322-254362 (Tooltip.HandleToolTipDisplay), 314388-314471 (Structure tooltip gates and GetPassiveTooltip), 319731-319734 (Thing.GetPassiveTooltip), 307089-307104 (PassiveTooltip(bool) constructor)
related:
  - ../GameClasses/Structure.md
  - ../GameClasses/Cable.md
  - ../GameClasses/ElectricalInputOutput.md
tags: [ui]
---

# Passive tooltip pipelines

Two independent code paths render a hover tooltip for a world object, and they gate on DIFFERENT fields of the same `PassiveTooltip` struct. The crosshair pipeline (normal first-person play) displays a passive tooltip only when `Title` is non-empty; the mouse-control pipeline (the ALT free-cursor mode) renders whenever any populated field exists, `Extended` alone included. A mod appending hover text must know which pipeline it is feeding. (The `PassiveTooltip` struct body, the `Device` -> `Structure` -> `Thing` dispatch chain, and the TextMeshPro render path are on [ElectricalInputOutput](../GameClasses/ElectricalInputOutput.md), "GetPassiveTooltip: body-hover tooltip resolution chain".)

## The law
<!-- verified: 0.2.6403.27689 @ 2026-07-18 -->

- An `Extended`-only tooltip renders under mouse control (ALT) but NEVER on a crosshair hover.
- A mod appending hover text to a passive tooltip must fill `Title` for crosshair visibility; `Extended` alone is invisible there.
- Crosshair hovers that resolve to a tool action bypass `GetPassiveTooltip` output entirely: the displayed tooltip is built from the `DelayedActionInstance`, so a `GetPassiveTooltip` patch never affects those hovers.
- An undamaged `Structure` with no ExtendedTooltips gate passing produces the all-empty base tooltip, which neither pipeline renders: crosshair fails the `Title` gate, mouse control has every `_has*` flag false.

## Crosshair pipeline: InventoryManager.NormalModeThing requires a non-empty Title
<!-- verified: 0.2.6403.27689 @ 2026-07-18 -->

`InventoryManager.NormalModeThing()` (declared at decompile 287864 at 0.2.6403.27689) is the per-frame crosshair-hover handler in normal play. It asks the hovered thing for its passive tooltip once (287869):

```csharp
PassiveTooltip cursorPassiveTooltip2 = ((CursorManager.CursorThing != null) ? CursorManager.CursorThing.GetPassiveTooltip(cursorTargetCollider) : default(PassiveTooltip));
```

When the hover carries a tool action (a `DelayedActionInstance` from `AttackWith` / `InteractWith` previews), the displayed tooltip is constructed from the action instance instead, bypassing `cursorPassiveTooltip2`. The two action-built sites (287917-287923 and 287947-287953), verbatim:

```csharp
if (delayedActionInstance3 != null)
{
    color = ((!delayedActionInstance3.IsDisabled) ? ((delayedActionInstance3.Duration > 0f) ? UnityEngine.Color.yellow : UnityEngine.Color.green) : UnityEngine.Color.red);
    PassiveTooltip cursorPassiveTooltip3 = new PassiveTooltip(delayedActionInstance3, string.Empty, CursorManager.CursorThing);
    cursorPassiveTooltip3.color = color;
    TooltipRef.HandleToolTipDisplay(cursorPassiveTooltip3);
}
```

```csharp
else if (delayedActionInstance != null)
{
    color = ((!delayedActionInstance.IsDisabled) ? ((delayedActionInstance.Duration > 0f) ? UnityEngine.Color.yellow : UnityEngine.Color.green) : UnityEngine.Color.red);
    PassiveTooltip cursorPassiveTooltip5 = new PassiveTooltip(delayedActionInstance, string.Empty, CursorManager.CursorThing);
    cursorPassiveTooltip5.color = color;
    TooltipRef.HandleToolTipDisplay(cursorPassiveTooltip5);
}
```

The `GetPassiveTooltip` result reaches the renderer through exactly one branch, and that branch is gated on a non-empty `Title` (287963-287967), verbatim:

```csharp
else if (!cursorPassiveTooltip2.Title.Equals(string.Empty))
{
    TooltipRef.HandleToolTipDisplay(cursorPassiveTooltip2);
    CursorManager.ClearLastSelectionId();
}
```

A passive tooltip whose `Title` is empty never reaches `HandleToolTipDisplay` on this pipeline, no matter how much `Extended` text it carries.

## Mouse-control pipeline: InputMouse.Idle hands over unconditionally
<!-- verified: 0.2.6403.27689 @ 2026-07-18 -->

`InputMouse` (`public class InputMouse : UserInterfaceBase`, 239369) drives the ALT free-cursor mode. Its `Idle()` handler (239679) sources the tooltip from the same producer (239691: `passiveTooltip = CursorThing.GetPassiveTooltip(hitInfo.collider);`) but hands it to the renderer with NO field gate at all (239716-239721), verbatim:

```csharp
if (interactable != null)
{
    Tooltip.SetValuesForInteractable(ref passiveTooltip, CursorThing, interactable);
}
passiveTooltip.FollowMouseMovement = true;
InventoryManager.Instance.TooltipRef.HandleToolTipDisplay(passiveTooltip);
```

The visibility decision happens inside `Tooltip.HandleToolTipDisplay` (`public class Tooltip : UserInterfaceBase`, 253966; method at 254322), where a non-empty `Extended` is sufficient on its own: the panel-visible flag `flag2` includes `_hasExtended`, and the extended block toggles on its own flag. Verbatim (254322-254351, trimmed after the per-block toggles):

```csharp
public void HandleToolTipDisplay(PassiveTooltip cursorPassiveTooltip)
{
    WantDraw = true;
    if ((bool)InventoryManager.ParentHuman && (bool)InventoryManager.Instance.ActiveHand.Slot.Occupant && InventoryManager.Instance.ActiveHand.Slot.Occupant is Tablet)
    {
        return;
    }
    SetUpToolTip(cursorPassiveTooltip.Action, cursorPassiveTooltip);
    bool flag = !InventoryManager.Instance.UIProgressionBar.IsVisible && (cursorPassiveTooltip.ShowRotate || cursorPassiveTooltip.ShowScroll || (cursorPassiveTooltip.ShowAction && _hasAction));
    bool flag2 = !InventoryManager.Instance.UIProgressionBar.IsVisible && (_hasAction || _hasConstruction || _hasDeconstruction || _hasPlacement || _hasState || _hasTitle || _hasRepair || _hasExtended);
    if (!flag2 && !flag)
    {
        UiComponentRenderer.SetVisible(isVisble: false);
        return;
    }
    if (!UiComponentRenderer.IsVisible)
    {
        UiComponentRenderer.SetVisible(isVisble: true);
    }
    FullToolTipHotKeys.SetVisible(flag);
    FullToolTipPanel.SetVisible(flag2);
    if (flag2)
    {
        InfoConstructionGameObject.SetVisible(_hasConstruction);
        InfoRepairGameObject?.SetVisible(_hasRepair);
        InfoDeconstructGameObject.SetVisible(_hasDeconstruction);
        InfoPlacementGameObject.SetVisible(_hasPlacement);
        StateRenderer.SetVisible(_hasState);
        ExtendedRenderer.SetVisible(_hasExtended);
        TitleRenderer.SetVisible(_hasTitle);
```

(`_hasExtended = !string.IsNullOrEmpty(_extended) && _extended.Length > 0;` is computed inside `SetUpToolTip` at 254314.) So under mouse control an `Extended`-only tooltip renders: the panel shows with only the extended block visible. The crosshair pipeline funnels through this same method, but only after its own `Title` gate upstream; the asymmetry lives in the callers, not here.

## Producer gates: Structure.GetPassiveTooltip returns the all-empty tooltip unless an ExtendedTooltips gate passes
<!-- verified: 0.2.6403.27689 @ 2026-07-18 -->

The base producer is `Thing.GetPassiveTooltip` (319731-319734), which returns a defaulted struct whose every string is empty. Verbatim, plus the head of the `PassiveTooltip(bool)` constructor (307089-307104; the body continues past the excerpt):

```csharp
public virtual PassiveTooltip GetPassiveTooltip(Collider hitCollider)
{
    return new PassiveTooltip(true);
}
```

```csharp
public PassiveTooltip(bool toDefault = true)
{
    Title = string.Empty;
    Action = string.Empty;
    State = string.Empty;
    Extended = string.Empty;
    RepairString = string.Empty;
    DeconstructString = string.Empty;
    ConstructString = string.Empty;
    PlacementString = string.Empty;
    ShowRotate = false;
    ShowScroll = false;
    ShowConstructionRotate = false;
    ShowAction = true;
    BuildStateIndexMessage = string.Empty;
    color = UnityEngine.Color.white;
```

`Structure.GetPassiveTooltip` (314440-314471) falls through to that empty base for every undamaged structure unless one of three gates passes. ALL three require the vanilla ExtendedTooltips game setting (`Settings.CurrentData.ExtendedTooltips`); the build and deconstruct gates additionally require an `IShowBuildStateTooltip` item in the active hand (the build gate waives the hand requirement in the upgrade-state case). Verbatim (gates 314388-314438, method 314440-314471):

```csharp
private bool ShowBuildTooltip()
{
    bool flag = IsUpgradeState();
    if (Settings.CurrentData.ExtendedTooltips && (flag || !IsStructureCompleted) && NextBuildState != null)
    {
        if (!flag)
        {
            Slot activeHandSlot = InventoryManager.ActiveHandSlot;
            if (activeHandSlot != null)
            {
                return activeHandSlot.Occupant is IShowBuildStateTooltip;
            }
            return false;
        }
        return true;
    }
    return false;
}

private bool ShowDeconstructTooltip()
{
    if (Settings.CurrentData.ExtendedTooltips && (bool)InventoryManager.Parent)
    {
        Slot activeHandSlot = InventoryManager.ActiveHandSlot;
        if (activeHandSlot != null)
        {
            return activeHandSlot.Occupant is IShowBuildStateTooltip;
        }
        return false;
    }
    return false;
}

private bool ShowRepairTooltip()
{
    if (Settings.CurrentData.ExtendedTooltips && RepairTools.IsValid())
    {
        return DamageState.Total > 0f;
    }
    return false;
}

private bool IsUpgradeState()
{
    ToolUseType? toolUseType = NextBuildState?.Tool?.ToolUseType;
    if (toolUseType.HasValue)
    {
        return toolUseType == ToolUseType.Upgrade;
    }
    return false;
}

public override PassiveTooltip GetPassiveTooltip(Collider hitCollider)
{
    bool flag = ShowBuildTooltip();
    bool flag2 = ShowDeconstructTooltip();
    bool flag3 = ShowRepairTooltip();
    if (DamageState.Total <= 0f && !flag && !flag2 && !flag3)
    {
        return base.GetPassiveTooltip(hitCollider);
    }
    PassiveTooltip passiveTooltip = new PassiveTooltip(true);
    passiveTooltip.Title = DisplayName;
    passiveTooltip.Extended = GetExtendedText().ToString();
    PassiveTooltip result = passiveTooltip;
    if (flag)
    {
        result.ConstructString = NextBuildState.Tool.GetToolsAsString();
    }
    if (IsBroken && BrokenBuildStates.Count > 0)
    {
        int index = Mathf.Clamp(Mathf.Abs(CurrentBuildStateIndex) - 1, 0, BrokenBuildStates.Count - 1);
        result.DeconstructString = BrokenBuildStates[index].BuildState.Tool.GetExitToolAsString();
    }
    else
    {
        result.DeconstructString = CurrentBuildState.Tool.GetExitToolAsString();
    }
    if (flag3)
    {
        result.RepairString = RepairTools.GetRepairsAsString();
    }
    return result;
}
```

Gate census:

| Gate | Requires |
|---|---|
| `ShowBuildTooltip` (314388-314405) | `ExtendedTooltips` AND (`IsUpgradeState()` OR not `IsStructureCompleted`) AND `NextBuildState != null`; when not upgrade-state, ALSO active-hand occupant `is IShowBuildStateTooltip` |
| `ShowDeconstructTooltip` (314407-314419) | `ExtendedTooltips` AND `InventoryManager.Parent` AND active-hand occupant `is IShowBuildStateTooltip` |
| `ShowRepairTooltip` (314421-314428) | `ExtendedTooltips` AND `RepairTools.IsValid()` AND `DamageState.Total > 0f` |

When `DamageState.Total <= 0f` and no gate passes, the return is the all-empty base tooltip: invisible on both pipelines. When any gate passes (or the structure is damaged), the struct gets `Title = DisplayName` and `Extended = GetExtendedText().ToString()` plus the construct / deconstruct / repair strings.

## Mod consequences
<!-- verified: 0.2.6403.27689 @ 2026-07-18 -->

- A `Structure.GetPassiveTooltip` postfix that only appends to `__result.Extended` is invisible to crosshair players whenever the vanilla body fell through to the empty base (undamaged structure, gates closed): `Title` stayed empty and `NormalModeThing`'s gate at 287963 drops the tooltip. Set `__result.Title` (typically `__instance.DisplayName`) in the same postfix.
- Concrete case: `CableRuptured` wreckage spawns undamaged (`DamageState.Total == 0`, see [Cable](../GameClasses/Cable.md), "The wreckage spawns undamaged"), so a burn-reason annotation on the wreckage must fill `Title` to show on a crosshair hover; `Extended`-only text appears solely in the ALT mouse-control hover.
- Tool-action hovers (crosshair with a tool in hand producing a `DelayedActionInstance`) never consult `GetPassiveTooltip` output; annotating those requires touching the action-instance path (287920 / 287950), not the passive-tooltip producer.

## Per-class Title filling: which device bodies render Extended-only text on a crosshair hover
<!-- verified: 0.2.6403.27689 @ 2026-07-18 -->

The crosshair Title gate makes the practical visibility of an appended Extended line depend on whether the hovered class's own `GetPassiveTooltip` override fills `Title`. Census at 0.2.6403.27689:

Title-FILLING bodies (Extended-only annotations DO render on a bare crosshair hover):

- `SolarPanel.GetPassiveTooltip` (421384-421397): when fully built, always sets `Title = DisplayName` plus `State = SolarInfo()`.
- `PowerGeneratorPipe.GetPassiveTooltip` (396655-396675): fills `Title = DisplayName` whenever the base chain left it empty.
- `StirlingEngine.GetPassiveTooltip` (424241-424269): same pattern.
- `AreaPowerControl.GetPassiveTooltip` (390800-390810): the null-collider body poll gets `Title = DisplayName` plus the charge readout in Extended.
- Any DAMAGED structure (the damage gate fills a repair tooltip) and any Input / Output port collider on the `ElectricalInputOutput` family (395008-395023 titles the port colliders).

Title-LESS bodies (Extended-only annotations render ONLY under ALT):

- The `ElectricalInputOutput` family body hover (Transformer, Battery, PowerTransmitter, PowerReceiver, both umbilical halves): only port colliders are titled.
- `PowerConnector` (408050-408059): only its input-port collider.
- Every class that falls through to the empty base tooltip (Thing 319731-319734), including undamaged wreckage.

## Verification history

- 2026-07-18: added the per-class Title-filling census (SolarPanel / PowerGeneratorPipe / StirlingEngine / AreaPowerControl fill Title on the body; the ElectricalInputOutput family and PowerConnector title only port colliders), read from the 0.2.6403.27689 decompile during the hover-surface matrix review.
- 2026-07-18: page created (game version 0.2.6403.27689). All quoted lines read directly from `.work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs`; the findings were produced and independently adversarially verified against the decompile this session. Crosshair pipeline: `InventoryManager.NormalModeThing` (287864) builds the hover tooltip from `GetPassiveTooltip` (287869) and displays it only when `Title` is non-empty (287963); tool-action hovers build `PassiveTooltip(DelayedActionInstance, ...)` directly (287920, 287950). Mouse-control pipeline: `InputMouse.Idle` (239679, tooltip sourced at 239691) hands the tooltip to `HandleToolTipDisplay` unconditionally (239720-239721); `Tooltip.HandleToolTipDisplay` (254322) treats a non-empty `Extended` as sufficient (`flag2` includes `_hasExtended` at 254331; `ExtendedRenderer.SetVisible(_hasExtended)` at 254350). Producer gates: `Structure.GetPassiveTooltip` (314440-314471) with the three ExtendedTooltips-gated helpers (314388-314428) and the all-empty `Thing` base (319731-319734; `PassiveTooltip(bool)` zeroes `Title` at 307091).

## Open questions

None.
