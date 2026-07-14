using Assets.Scripts.Objects;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    // The on/off BUTTON tooltip's share of the fault / info hover (the casing hover is covered by
    // FaultHoverPatches; the block itself comes from FaultHover, the single source of truth both
    // surfaces and the flash colour share). Split of duties while a block is active:
    //
    // ACTION box (the small mouse-icon box): pure vanilla pass-through. This postfix does not
    // touch tooltip.Action, so the InteractWith preview's plain action word survives ("Off" on a
    // running device, "On" on a stopped one: the word is the action a click performs) and
    // SetUpToolTip wraps it in the interaction color as always (254319; green for an allowed
    // instant click, red while the click is disabled, ladder at 254416-254427).
    //
    // TITLE box (the separate side name box): everything else, the device name over the block:
    //
    //     Main Transformer Discharger              (DisplayName, the name box's own default styling)
    //     On - Cable overloaded fault: 35.23s      (block line 1, from FaultHover)
    //     Pushing 12.1 kW onto a 5.0 kW wire       (block diagnostics)
    //
    // Mechanism (content approach, no UI surgery): Tooltip.SetValuesForInteractable (decompile
    // 254408) rebuilds the PassiveTooltip struct from the InteractWith preview on EVERY hover poll
    // on both display routes (InputMouse.Idle 239718, InventoryManager.NormalModeThing 287927);
    // this postfix mutates only that frame's struct copy and runs after vanilla's optional
    // SwitchTitleForTooltip title write (254429-254432), so the rebuilt Title wins for the poll.
    // Restore path: automatic. The moment the fault clears or the cursor moves to any other device
    // the gate stops matching, the next poll's freshly rebuilt struct is not touched, and the
    // vanilla layout is back. No RectTransform, renderer, or other persistent UI state is ever
    // written.
    //
    // Title render path: SetUpToolTip copies Title verbatim with no color wrap (the 254319 wrap is
    // Action-only) and the Title setter writes the TextMeshProUGUI directly
    // (TooltipTitle.text = _title, 254119), so the markup renders exactly as authored: the
    // <align=left> tag at position 0 left-aligns all lines (no jitter as the countdown narrows),
    // the block keeps its explicit colors, and the un-tagged DisplayName line keeps the name
    // box's vanilla styling. TitleRenderer.SetVisible(_hasTitle) (254351) keeps the box visible
    // (the Title is never empty here). TextMeshPro renders the "\n" breaks as lines.
    //
    // The three composition tags below make the block render pixel-identical to the same block on
    // the casing tooltip's Extended row. Their values are HARDCODED from a live extraction (user
    // directive 2026-07-14: constants over hover-order-dependent runtime reads), pulled from the
    // running game via InspectorPlus with a casing fault hover and then a button fault hover on
    // screen (game 0.2.6403.27689). The tooltip rows' serialized metrics, the TextMeshPro advance
    // math the derivation uses, and the re-extraction recipe live in
    // Research/Patterns/TextMeshProLineMetrics.md:
    //
    //   TooltipTitle    (GameObject ItemTitle):    fontSize 24, lineSpacing 30, paragraphSpacing 0
    //   TooltipExtended (GameObject InfoExtended): fontSize 18, lineSpacing 30, paragraphSpacing 50
    //   both rows: enableAutoSizing false, font_english / RBBook SDF Material, fontStyle Normal,
    //   fontWeight Regular, characterSpacing 10, equal rect lossyScale. Equal point size therefore
    //   means equal on-screen glyphs.
    //
    //   SizeOpenTag         <size=18>: the Extended row's literal point size. Sits AFTER the first
    //     "\n" so the DisplayName line keeps the name box's native 24-point size, and closes
    //     before </align>.
    //   BlockAdvanceOpenTag <line-height=25.2>: the block-internal line advance. The Extended
    //     row's natural advance is 32.4 (face term 18.0 for font_english at 18 points, plus the
    //     spacing term (30 + 50) * 18 * 0.01 = 14.4); a driven break in the Title row re-adds the
    //     Title's own spacing term (30 + 0) * 24 * 0.01 = 7.2 at render time, so the tag carries
    //     32.4 - 7.2 = 25.2. Re-arms right after the first "\n" so every block-internal break
    //     lands at exactly the casing advance.
    //   FirstGapOpenTag     <line-height=21.4>: the name-to-block gap. On the casing the name and
    //     the block live in two separate rows whose distance is serialized prefab layout, measured
    //     live at 28.6 Title-local units first-baseline-to-first-baseline (one-shot runtime
    //     capture, 2026-07-14); minus the same 7.2 Title spacing term gives 21.4. Governs only the
    //     advance into block line 1 (the tag sits at the END of the DisplayName line, before the
    //     first "\n"; the advance into a line is computed at the break that starts it).
    //   CharacterSpacingOpenTag <cspace=-0.6>: cancels the per-character tracking surplus. The
    //     character-spacing em scale, like the line-height em scale, uses the COMPONENT font size
    //     and is computed once per render pass (Unity.TextMeshPro decompile 2912: num3 =
    //     m_fontSize * 0.01), so both rows' characterSpacing of 10 advances 10 * 0.24 = 2.4 units
    //     per character inside the 24-point Title row but 10 * 0.18 = 1.8 inside the 18-point
    //     Extended row; the size tag does not touch it. The surplus 0.6 per character made the
    //     button block track visibly wider than the casing block (in-game screenshots 2026-07-14,
    //     fourth round). A plain-number <cspace> is added RAW per character, unscaled (tag parse
    //     27273, advance 3812), so -0.6 cancels the surplus exactly; </cspace> rolls the trailing
    //     character's share back off the line width (27287-27293). The font asset's
    //     normalSpacingOffset rides the same component scale and would shift the cancel by
    //     nso * 0.06; assumed 0 (the TextMeshPro default), confirmed empirically 2026-07-14:
    //     with the -0.6 cancel the two blocks render pixel-identical in-game, which a nonzero
    //     offset would have prevented.
    //
    // All three values are TextMeshPro text-space units, so they hold across resolutions and UI
    // scales (canvas scaling multiplies the whole text uniformly). They only break if the game
    // re-tunes the tooltip prefab (row sizes, spacings, or layout distance); re-extract per the
    // research page then. No close tag is needed for line-height: m_lineHeight resets to unset at
    // the start of every GenerateTextMesh pass (Unity.TextMeshPro decompile 6224), so nothing
    // leaks to other tooltips.
    [HarmonyPatch(typeof(Assets.Scripts.UI.Tooltip), "SetValuesForInteractable")]
    public static class FaultButtonTooltipPatches
    {
        private const string SizeOpenTag = "<size=18>";
        private const string BlockAdvanceOpenTag = "<line-height=25.2>";
        private const string FirstGapOpenTag = "<line-height=21.4>";
        private const string CharacterSpacingOpenTag = "<cspace=-0.6>";

        [HarmonyPostfix]
        public static void SetValuesForInteractable_Postfix(ref PassiveTooltip tooltip, Thing CursorThing, Interactable interactable)
        {
            if (CursorThing == null || interactable == null) return;
            if (interactable.Action != InteractableType.OnOff) return;
            long refId = FaultHover.ResolveFaultRefId(CursorThing);
            int tick = ElectricityTickCounter.CurrentTick;
            if (!FaultHover.TryGetMergedBlock(refId, tick, CursorThing, out var block, out _)) return;
            // tooltip.Action stays untouched: the plain vanilla word passes through.
            tooltip.Title = "<align=left>" + CursorThing.DisplayName + FirstGapOpenTag + "\n"
                + SizeOpenTag + BlockAdvanceOpenTag + CharacterSpacingOpenTag + block
                + "</cspace></size></align>";
        }
    }
}
