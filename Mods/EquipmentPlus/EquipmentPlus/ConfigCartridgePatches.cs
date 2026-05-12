using Assets.Scripts;
using Assets.Scripts.Inventory;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.UI;
using Assets.Scripts.UI.CustomScrollPanel;
using HarmonyLib;
using JetBrains.Annotations;
using LaunchPadBooster.Networking;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;

namespace EquipmentPlus
{
    /// <summary>
    /// Reimplements the functionality of the ImprovedConfiguration mod with
    /// multiplayer support and Ctrl-aware click handling.
    ///
    /// Features:
    ///   - Scroll wheel selects a line in the Config Cartridge display
    ///   - Plain left-click on the selected line:
    ///       - Writable value: opens input dialog, sends SetLogicFromClient to server
    ///       - Read-only value: copies to clipboard
    ///   - Left Ctrl+click is NOT handled here (it's for cycling cartridges)
    ///
    /// Multiplayer fix: uses the vanilla SetLogicFromClient message instead
    /// of calling SetLogicValue directly. The server receives the message,
    /// applies the change authoritatively, and replicates to all clients.
    /// </summary>
    internal static class ConfigCartridgeState
    {
        // Per-cartridge selected line index. Cleaned up on cartridge destruction.
        internal static readonly Dictionary<ConfigCartridge, int> SelectedIndex =
            new Dictionary<ConfigCartridge, int>();

        // Highlight color for the selected line (yellow/gold).
        internal const string HighlightOpen = "<color=#FFD561FF>";
        internal const string HighlightClose = "</color>";

        // Separator used by the vanilla display format: "LogicTypeName ... value"
        internal const string LineSeparator = " ... ";

        // Regex that strips TMP color markup from a line before parsing.
        internal static readonly Regex MarkRegex = new Regex(@"<color=[^>]*>|</color>");

        // Diagnostic: toggle to LogInfo at each early-return in the click
        // handler. Noisy; leave off in shipping builds. Flip to true when
        // diagnosing why a live click isn't reaching InputWindow / clipboard.
        internal const bool ClickTrace = true;

        // Either control key cycles cartridges and suppresses the edit click.
        // Uses the game's input layer (KeyManager) for parity with ScrollDispatchPatches
        // so a remapped Ctrl or an input-focused UI state behaves identically on
        // both paths; otherwise one would fire on physical keys while the other
        // respects rebinds, and RightCtrl-cycle would double-fire the input panel.
        internal static bool AnyCtrlHeld()
            => KeyManager.GetButton(KeyCode.LeftControl) || KeyManager.GetButton(KeyCode.RightControl);
    }

    /// <summary>
    /// Viewport-aware scroll-position snap for ScrollPanel.
    ///
    /// Vanilla SetScrollPosition unconditionally re-anchors the panel,
    /// causing a visible jolt every wheel-tick even when the highlighted
    /// line is already on screen. This helper inspects the panel's private
    /// scroll/viewport/content state and only snaps when the selection
    /// would otherwise be off screen, then snaps to the nearest edge
    /// instead of the proportional center for a less-abrupt result.
    ///
    /// Reflection fail-safe: if any private field read fails, we fall back
    /// to the unconditional snap (current behavior) so the selection stays
    /// in view at the cost of a jolt. ScrollPanel field names are pinned
    /// in Research/GameClasses/CustomScrollPanel.md.
    /// </summary>
    internal static class ScrollPanelVisibility
    {
        // Snap with viewport awareness. selFrac is the normalized position
        // of the selected line in [0..1]. If the selection is already visible
        // on screen, no-op. Otherwise snap so the selection sits at the
        // nearest viewport edge (top if scrolling up, bottom if down).
        internal static void EnsureSelectionVisible(ScrollPanel panel, float selFrac)
        {
            if (panel == null) return;
            selFrac = Mathf.Clamp01(selFrac);

            if (!TryGetVisibleRange(panel, out float topFrac, out float bottomFrac, out float viewportFrac))
            {
                // Reflection fail-safe: keep selection visible by snapping
                // unconditionally (previous behavior).
                panel.SetScrollPosition(selFrac);
                return;
            }

            if (selFrac >= topFrac && selFrac <= bottomFrac)
                return;

            // Off screen. Snap to nearest edge: selection at top of viewport
            // if it was above, at bottom if below. The mapping derives from
            // SetContentPosition: with _scrollActualPosition == p, the visible
            // window covers content fraction [p*(1-vf), p*(1-vf) + vf]. Solving
            // for "selection at top edge" gives p = selFrac / (1 - vf); for
            // "selection at bottom edge" gives p = (selFrac - vf) / (1 - vf).
            float denominator = 1f - viewportFrac;
            if (denominator <= 0f)
            {
                // Content fully fits; nothing to snap.
                return;
            }

            float target = selFrac < topFrac
                ? selFrac / denominator
                : (selFrac - viewportFrac) / denominator;
            panel.SetScrollPosition(Mathf.Clamp01(target));
        }

        // Reads ScrollPanel private fields and computes the currently-visible
        // normalized line range. Returns false if any field read fails.
        // viewportFrac out-param is the visible fraction of total content
        // (== 1 when content fits entirely; in [0..1) otherwise).
        private static bool TryGetVisibleRange(
            ScrollPanel panel, out float topFrac, out float bottomFrac, out float viewportFrac)
        {
            topFrac = 0f;
            bottomFrac = 1f;
            viewportFrac = 1f;

            if (!ReflectionUtils.TryGetField<float>(panel, "_scrollActualPosition", out float actualPos))
                return false;
            if (!ReflectionUtils.TryGetField<float>(panel, "_viewportHeight", out float viewportH))
                return false;
            if (!ReflectionUtils.TryGetField<float>(panel, "_contentHeight", out float contentH))
                return false;

            if (contentH <= 0f || viewportH <= 0f)
                return false;
            if (contentH <= viewportH)
            {
                // Everything fits; selection is always visible.
                topFrac = 0f;
                bottomFrac = 1f;
                viewportFrac = 1f;
                return true;
            }

            viewportFrac = viewportH / contentH;
            topFrac = Mathf.Clamp01(actualPos) * (1f - viewportFrac);
            bottomFrac = topFrac + viewportFrac;
            return true;
        }
    }

    /// <summary>
    /// Scroll wheel steps the selected-line index one position per notch and
    /// snaps the ScrollPanel viewport to keep the selected line in view.
    ///
    /// Returns false to suppress vanilla's `_scrollPanel.OnScroll` free-pan.
    /// Without this, vanilla's pixel-based smooth scroll runs in parallel to
    /// our index advance, desyncing the highlighted line from the viewport.
    ///
    /// Sign convention matches upstream ImprovedConfiguration: wheel-up
    /// (scrollDelta.y > 0) decreases the index (selection moves up the list).
    /// </summary>
    [HarmonyPatch(typeof(Cartridge), nameof(Cartridge.OnScroll))]
    public class ConfigCartridgeScrollPatch
    {
        [UsedImplicitly]
        public static bool Prefix(Cartridge __instance, Vector2 scrollDelta)
        {
            if (!(__instance is ConfigCartridge cc))
                return true;
            if (scrollDelta == Vector2.zero)
                return true;

            // If any modifier we care about (Ctrl, LeftShift, RightShift) is
            // held, ScrollDispatchPatch in InventoryManager.NormalMode handles
            // the scroll event (or it falls through to vanilla camera under
            // RightShift). Either way, suppress the cartridge line-select so
            // we don't double-fire. Plain scroll still selects lines normally.
            // ALT is NOT checked here: vanilla captures ALT for its own mode
            // toggle so it never reaches us as a scroll modifier anyway.
            if (KeyManager.GetButton(KeyCode.LeftControl)
                || KeyManager.GetButton(KeyCode.RightControl)
                || KeyManager.GetButton(KeyCode.LeftShift)
                || KeyManager.GetButton(KeyCode.RightShift))
            {
                return false;
            }

            if (!ReflectionUtils.TryGetField<TextMeshProUGUI>(cc, "_displayTextMesh", out var textMesh))
                return true;
            if (textMesh == null || string.IsNullOrEmpty(textMesh.text))
                return true;

            int lineCount = textMesh.text.Split('\n').Length;
            if (lineCount <= 0)
                return false;

            ConfigCartridgeState.SelectedIndex.TryGetValue(cc, out int current);
            current = Math.Min(Math.Max(current, 0), lineCount - 1);

            int delta = scrollDelta.y > 0f ? -1 : 1;
            current = Math.Min(Math.Max(current + delta, 0), lineCount - 1);
            ConfigCartridgeState.SelectedIndex[cc] = current;

            if (ReflectionUtils.TryGetField<ScrollPanel>(__instance, "_scrollPanel", out var scrollPanel)
                && scrollPanel != null)
            {
                float pos = lineCount > 1 ? (float)current / (lineCount - 1) : 0f;
                ScrollPanelVisibility.EnsureSelectionVisible(scrollPanel, pos);
            }

            return false;
        }
    }

    /// <summary>
    /// After the cartridge renders its screen, highlight the selected line,
    /// drive the ScrollPanel viewport to track the selection, and handle
    /// left-click (without Ctrl) on the selected line.
    /// </summary>
    [HarmonyPatch(typeof(ConfigCartridge), nameof(ConfigCartridge.OnScreenUpdate))]
    public class ConfigCartridgeScreenPatch
    {
        [UsedImplicitly]
        public static void Postfix(ConfigCartridge __instance)
        {
            if (__instance == null)
                return;

            if (!ReflectionUtils.TryGetField<TextMeshProUGUI>(__instance, "_displayTextMesh", out var textMesh))
                return;
            if (textMesh == null || string.IsNullOrEmpty(textMesh.text))
                return;

            var lines = new List<string>(textMesh.text.Split('\n'));
            if (lines.Count == 0)
                return;

            // Clamp selected index into valid range
            ConfigCartridgeState.SelectedIndex.TryGetValue(__instance, out int selected);
            selected = Math.Min(Math.Max(selected, 0), lines.Count - 1);
            ConfigCartridgeState.SelectedIndex[__instance] = selected;

            // Strip any existing highlight markup from the selected line, then re-add it
            string stripped = ConfigCartridgeState.MarkRegex.Replace(lines[selected], string.Empty);
            lines[selected] = ConfigCartridgeState.HighlightOpen + stripped + ConfigCartridgeState.HighlightClose;

            string newText = string.Join("\n", lines);
            if (textMesh.text != newText)
                textMesh.text = newText;

            // Drive the viewport to follow selection only when selection
            // would otherwise be off screen. This skips the snap on every
            // benign frame (selection already visible) so the panel does not
            // jolt continuously, but still re-anchors after a vanilla
            // _needTopScroll reset or any other path that drifts the panel
            // away from the highlight.
            if (ReflectionUtils.TryGetField<ScrollPanel>(__instance, "_scrollPanel", out var scrollPanel)
                && scrollPanel != null)
            {
                float pos = lines.Count > 1 ? (float)selected / (lines.Count - 1) : 0f;
                ScrollPanelVisibility.EnsureSelectionVisible(scrollPanel, pos);
            }

            // Only respond to left-click without Ctrl (Ctrl+click is reserved for cycling).
            if (!Input.GetMouseButtonDown(0))
                return;
            if (ConfigCartridgeState.AnyCtrlHeld())
            {
                if (ConfigCartridgeState.ClickTrace)
                    EquipmentPlusPlugin.Log.LogInfo("[EquipmentPlus.click] suppressed: Ctrl held");
                return;
            }

            if (ConfigCartridgeState.ClickTrace)
                EquipmentPlusPlugin.Log.LogInfo(
                    $"[EquipmentPlus.click] fired: selected={selected} lineCount={lines.Count}");

            // Must be in the player's active hand
            var tablet = __instance.Tablet;
            if (tablet == null || tablet.ParentSlot == null)
            {
                if (ConfigCartridgeState.ClickTrace)
                    EquipmentPlusPlugin.Log.LogInfo(
                        $"[EquipmentPlus.click] bail: tablet={(tablet == null ? "null" : "set")} parentSlot=null");
                return;
            }
            if (tablet.ParentSlot != InventoryManager.ActiveHandSlot)
            {
                if (ConfigCartridgeState.ClickTrace)
                {
                    var ahs = InventoryManager.ActiveHandSlot;
                    var tps = tablet.ParentSlot;
                    string ahsDesc = ahs == null ? "null"
                        : $"[{(ahs.Parent != null ? ahs.Parent.GetType().Name + "#" + ahs.Parent.ReferenceId : "null")}/{ahs.SlotIndex} occ={(ahs.Get() != null ? ahs.Get().GetType().Name : "(empty)")}]";
                    string tpsDesc = $"[{(tps.Parent != null ? tps.Parent.GetType().Name + "#" + tps.Parent.ReferenceId : "null")}/{tps.SlotIndex} occ={(tps.Get() != null ? tps.Get().GetType().Name : "(empty)")}]";
                    bool sameRef = object.ReferenceEquals(tps, ahs);
                    bool sameId = ahs != null && tps.Parent == ahs.Parent && tps.SlotIndex == ahs.SlotIndex;
                    EquipmentPlusPlugin.Log.LogInfo(
                        $"[EquipmentPlus.click] bail: tablet#{tablet.ReferenceId} not in ActiveHandSlot. tablet.ParentSlot={tpsDesc} ActiveHandSlot={ahsDesc} sameRef={sameRef} sameLogical={sameId}");
                }
                return;
            }
            if (tablet.Cartridge != __instance)
            {
                if (ConfigCartridgeState.ClickTrace)
                    EquipmentPlusPlugin.Log.LogInfo(
                        "[EquipmentPlus.click] bail: tablet.Cartridge != this cartridge (not the displayed one)");
                return;
            }

            var device = __instance.ScannedDevice;
            if (device == null)
            {
                if (ConfigCartridgeState.ClickTrace)
                    EquipmentPlusPlugin.Log.LogInfo(
                        "[EquipmentPlus.click] bail: ScannedDevice is null (cursor not on a device)");
                return;
            }

            // Check if the selected line is a slot-logic line appended by ConfigCartridgeSlotDisplayPatch.
            // If so, route to the slot write path instead of the LogicType path.
            if (ConfigCartridgeSlotDisplayPatch.SlotLines.TryGetValue(__instance, out var slotLineMap)
                && slotLineMap.TryGetValue(selected, out var slotInfo))
            {
                double currentSlotValue = device.GetLogicValue(slotInfo.LogicSlotType, slotInfo.SlotIndex);
                string currentSlotValueStr = currentSlotValue.ToString();
                string slotLabel = $"Slot {slotInfo.SlotIndex} / {slotInfo.LogicSlotType}";

                if (ConfigCartridgeState.ClickTrace)
                    EquipmentPlusPlugin.Log.LogInfo(
                        $"[EquipmentPlus.click] slot-line path: {slotLabel} writable={slotInfo.IsWritable} value={currentSlotValueStr}");

                if (slotInfo.IsWritable)
                {
                    var capturedDevice = device;
                    var capturedInfo   = slotInfo;
                    try
                    {
                        InputWindow.ShowInputPanel(slotLabel, currentSlotValueStr,
                            (string input, string _) =>
                            {
                                if (!double.TryParse(input, out double newValue))
                                    return;
                                WriteLogicSlotValue(capturedDevice, capturedInfo.LogicSlotType,
                                    capturedInfo.SlotIndex, newValue);
                            },
                            600);
                    }
                    catch (Exception e)
                    {
                        EquipmentPlusPlugin.Log.LogError(
                            $"Failed to open slot input panel for {slotLabel}: {e.Message}");
                    }
                }
                else
                {
                    try
                    {
                        GUIUtility.systemCopyBuffer = currentSlotValueStr ?? string.Empty;
                        EquipmentPlusPlugin.Log.LogInfo(
                            $"Copied slot value {slotLabel} to clipboard: {currentSlotValueStr}");
                    }
                    catch (Exception e)
                    {
                        EquipmentPlusPlugin.Log.LogError(
                            $"Failed to copy slot value {slotLabel}: {e.Message}");
                    }
                }
                return;
            }

            // Parse the LogicType from "TypeName ... value"
            var parts = stripped.Split(new[] { ConfigCartridgeState.LineSeparator }, StringSplitOptions.None);
            if (parts.Length == 0)
                return;

            string typeName = parts[0].Trim();

            // Look up the LogicType by name in EnumCollections.LogicTypes (the runtime
            // registry the cartridge display itself reads from). Enum.TryParse only sees
            // the compile-time vanilla members, so it bails on mod-added types like
            // PowerTransmitterPlus's MicrowaveAutoAimTarget that are runtime-cast from
            // ushort. Array.FindIndex returns -1 on miss; EnumCollection<,>.Get(string)
            // would return default(T1), which is indistinguishable from a real hit on the
            // zero-valued enum member. See Research/GameSystems/LogicType.md "EnumCollection
            // <TEnum, TInt> public API" for the lookup-direction table.
            var logicNames = EnumCollections.LogicTypes.Names;
            int logicIdx = Array.FindIndex(logicNames, n =>
                string.Equals(n, typeName, StringComparison.InvariantCultureIgnoreCase));
            if (logicIdx < 0)
            {
                if (ConfigCartridgeState.ClickTrace)
                    EquipmentPlusPlugin.Log.LogInfo(
                        $"[EquipmentPlus.click] bail: '{typeName}' is not a registered LogicType (likely header/ReferenceId-style line)");
                return;
            }
            LogicType logicType = EnumCollections.LogicTypes.Values[logicIdx];

            double currentValue = device.GetLogicValue(logicType);
            string currentValueStr = currentValue.ToString();

            if (ConfigCartridgeState.ClickTrace)
                EquipmentPlusPlugin.Log.LogInfo(
                    $"[EquipmentPlus.click] logic-type path: {logicType} writable={device.CanLogicWrite(logicType)} value={currentValueStr}");

            if (device.CanLogicWrite(logicType))
            {
                // Writable: show input dialog
                var capturedDevice = device;
                var capturedLogicType = logicType;
                try
                {
                    InputWindow.ShowInputPanel(typeName, currentValueStr,
                        (string input, string _) =>
                        {
                            if (!double.TryParse(input, out double newValue))
                                return;
                            WriteLogicValue(capturedDevice, capturedLogicType, newValue);
                        },
                        600);
                }
                catch (Exception e)
                {
                    EquipmentPlusPlugin.Log.LogError(
                        $"Failed to open input panel for {typeName}: {e.Message}");
                }
            }
            else
            {
                // Read-only: copy to clipboard
                try
                {
                    GUIUtility.systemCopyBuffer = currentValueStr ?? string.Empty;
                    EquipmentPlusPlugin.Log.LogInfo(
                        $"Copied {typeName} to clipboard: {currentValueStr}");
                }
                catch (Exception e)
                {
                    EquipmentPlusPlugin.Log.LogError(
                        $"Failed to copy {typeName} to clipboard: {e.Message}");
                }
            }
        }

        /// <summary>
        /// Multiplayer-safe logic value write.
        /// On the server: call SetLogicValue directly (we are authoritative).
        /// On a client: send SetLogicFromClient to the server, which applies
        /// the change authoritatively and replicates to all clients.
        /// </summary>
        private static void WriteLogicValue(
            Assets.Scripts.Objects.Pipes.Device device, LogicType logicType, double value)
        {
            if (NetworkManager.IsServer)
            {
                device.SetLogicValue(logicType, value);
            }
            else if (NetworkManager.IsClient)
            {
                var msg = new SetLogicFromClient
                {
                    LogicId = device.ReferenceId,
                    LogicType = logicType,
                    Value = value
                };
                NetworkClient.SendToServer(msg, NetworkChannel.GeneralTraffic);
            }
        }

        /// <summary>
        /// Multiplayer-safe logic slot value write.
        /// On the server: call Device.SetLogicValue directly (we are authoritative).
        /// On a client: send our custom SetLogicSlotFromClientMessage to the server,
        /// which validates and applies. Broadcast is implicit: SetLogicValue routes
        /// through OnServer.Interact on the slot occupant, vanilla state-sync
        /// replicates the occupant's interactable state, and the cartridge re-derives
        /// the slot value on next read.
        /// </summary>
        private static void WriteLogicSlotValue(
            Assets.Scripts.Objects.Pipes.Device device,
            LogicSlotType logicSlotType, int slotIndex, double value)
        {
            if (NetworkManager.IsServer)
            {
                device.SetLogicValue(logicSlotType, slotIndex, value);
            }
            else if (NetworkManager.IsClient)
            {
                new SetLogicSlotFromClientMessage
                {
                    DeviceId = device.ReferenceId,
                    SlotIndex = slotIndex,
                    LogicSlotTypeInt = (int)logicSlotType,
                    Value = value,
                }.SendToHost();
            }
        }
    }

    /// <summary>
    /// Clean up the selected-index dictionary entry when a cartridge is destroyed.
    /// </summary>
    [HarmonyPatch(typeof(Cartridge), nameof(Cartridge.OnDestroy))]
    public class ConfigCartridgeDestroyPatch
    {
        [UsedImplicitly]
        public static void Prefix(Cartridge __instance)
        {
            if (__instance is ConfigCartridge cc)
                ConfigCartridgeState.SelectedIndex.Remove(cc);
        }
    }
}
