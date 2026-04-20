---
title: HelpLinkHandler
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/StationpediaPlus/PLAN.md:424-432
  - Plans/StationpediaPlus/PLAN.md:835-852
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: HelpLinkHandler
related:
  - ./Stationpedia.md
  - ./StationpediaPage.md
tags: [stationpedia, ui]
---

# HelpLinkHandler

Vanilla game class at `HelpLinkHandler : UserInterfaceBase, IPointerClickHandler, ...` (line 221638). The TMP link-click handler used across Stationpedia surfaces. Its `LateUpdate` references `WorldManager.IsGamePaused`, tying the component to scene state.

## OnPointerClick + LateUpdate dependency
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0210.

`HelpLinkHandler.OnPointerClick` at game.cs:221692-221717:

```csharp
public void OnPointerClick(PointerEventData eventData)
{
    int num = TMP_TextUtilities.FindIntersectingLink(Parent, Input.mousePosition, _pCamera);
    if (num == -1) return;
    TMP_LinkInfo linkInfo = Parent.textInfo.linkInfo[num];
    string linkID = linkInfo.GetLinkID();
    if (linkID == "Clipboard") { GameManager.Clipboard = linkInfo.GetLinkText(); }
    else if (ForceOpen) Stationpedia.OpenAt(linkID);
    else Stationpedia.Instance.SetPage(linkID);
}
```

The class has `[RequireComponent(typeof(TextMeshProUGUI))]` and a public
`Parent` field. Its LateUpdate references `WorldManager.IsGamePaused` for
hover-color vertex updates, which ties it to game-scene state.

Our own `SixFive7LinkHandler` replicates the click-only behavior (line
221692 onwards) without the LateUpdate dependency. See §8.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0200, F0210. No conflicts.

## Open questions

None at creation.
