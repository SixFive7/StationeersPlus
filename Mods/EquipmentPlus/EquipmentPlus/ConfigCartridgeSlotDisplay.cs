using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using HarmonyLib;
using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace EquipmentPlus
{
    /// <summary>
    /// Identifies a slot-logic line parsed from the Config Cartridge display text.
    /// Stored temporarily during a click event so ConfigCartridgeScreenPatch can
    /// dispatch to WriteLogicSlotValue instead of WriteLogicValue.
    /// </summary>
    internal struct SlotLineInfo
    {
        /// <summary>Zero-based slot index on the scanned device.</summary>
        internal int SlotIndex;
        /// <summary>Which logic type within that slot.</summary>
        internal LogicSlotType LogicSlotType;
        /// <summary>Whether this logic-slot type is writable.</summary>
        internal bool IsWritable;
    }

    /// <summary>
    /// Replicates Slot Configuration Cartridge (Workshop 3578912665) as an absorbed feature.
    ///
    /// Patches ConfigCartridge.ReadLogicText (Postfix) to append per-slot logic values to the
    /// output text produced by the vanilla method.
    ///
    /// Output format per slot (matches the original mod exactly):
    ///
    ///   \n&lt;color=yellow&gt;Slot {slotIndex} ... {slot.DisplayName}&lt;/color&gt;
    ///   \n{logicTypeName} ... &lt;color=grey&gt;{value}&lt;/color&gt;   (writable)
    ///   \n{logicTypeName} ... &lt;color=green&gt;{value}&lt;/color&gt;  (read-only)
    ///
    /// Thread safety: the original mod acquired a Monitor lock on ____scannedDevice before
    /// iterating.  We do the same.
    ///
    /// Click-to-edit integration: slot lines are tagged in ConfigCartridgeState.SlotLines so
    /// ConfigCartridgeScreenPatch can route clicks to WriteLogicSlotValue.
    ///
    /// Multiplayer write safety:
    ///   Vanilla has SetLogicFromClient for LogicType writes.  No equivalent message exists for
    ///   (LogicSlotType, slotIndex) writes.  On server we call Device.SetLogicValue directly
    ///   (authoritative).  On a client we silently skip the write and log a warning; this is
    ///   the same limitation the original read-only mod accepted implicitly (it never added
    ///   write support at all).  A future work item could implement a custom NetworkMessage.
    /// </summary>
    [HarmonyPatch(typeof(ConfigCartridge), "ReadLogicText")]
    public class ConfigCartridgeSlotDisplayPatch
    {
        // -----------------------------------------------------------------------
        // Format strings: identical to the original mod's US[121], US[203], etc.
        // -----------------------------------------------------------------------

        /// <summary>Slot header block: blank separator line + yellow "Slot {0} ... {1}" header,
        /// matching the original Slot Configuration Cartridge spacing (\n\n prefix).</summary>
        private const string SlotHeaderFmt = "\n\n<color=yellow>Slot {0} ... {1}</color>";

        /// <summary>Logic-type line: newline + "{0} ... {1}{2}{3}" where
        /// {0}=type name, {1}=color-open tag, {2}=value string, {3}="&lt;/color&gt;".</summary>
        private const string SlotLogicLineFmt = "\n{0} ... {1}{2}{3}";

        private const string ColorWritable  = "<color=grey>";
        private const string ColorReadOnly  = "<color=green>";
        private const string ColorClose     = "</color>";

        // -----------------------------------------------------------------------
        // Slot line registry: populated here, consumed by ConfigCartridgeScreenPatch
        // -----------------------------------------------------------------------

        /// <summary>
        /// Maps each display line index (0-based within the *full* output text split by '\n')
        /// to its SlotLineInfo.  Rebuilt every time ReadLogicText runs.
        /// Keyed by ConfigCartridge instance so multi-cartridge sessions stay isolated.
        /// </summary>
        internal static readonly Dictionary<ConfigCartridge, Dictionary<int, SlotLineInfo>> SlotLines =
            new Dictionary<ConfigCartridge, Dictionary<int, SlotLineInfo>>();

        [UsedImplicitly]
        public static void Postfix(ConfigCartridge __instance, ref string ____outputText, ref Device ____scannedDevice)
        {
            if (__instance == null || ____scannedDevice == null)
                return;

            // Rebuild the slot-line registry for this cartridge.
            if (!SlotLines.TryGetValue(__instance, out var lineMap))
            {
                lineMap = new Dictionary<int, SlotLineInfo>();
                SlotLines[__instance] = lineMap;
            }
            else
            {
                lineMap.Clear();
            }

            // Work out how many lines are already in the output so we can track the
            // absolute line index for any slot lines we append.
            // (Existing text ends without a trailing newline; the first thing we append
            //  starts with '\n', so the first new line index = existing line count.)
            int baseLineIndex = string.IsNullOrEmpty(____outputText)
                ? 0
                : ____outputText.Split('\n').Length;

            // Thread-safety: mirror the Monitor.Enter/Exit the original mod uses.
            bool lockTaken = false;
            try
            {
                Monitor.Enter(____scannedDevice, ref lockTaken);

                var sb = new StringBuilder();
                var slots = ____scannedDevice.Slots;                    // Thing.Slots : List<Slot>
                // LogicSlotTypes and LogicSlotTypeStrings are public static arrays on Logicable.
                // They are the game's master list of all possible slot logic types.
                var logicSlotTypes   = Logicable.LogicSlotTypes;        // LogicSlotType[] (static)
                var logicSlotStrings = Logicable.LogicSlotTypeStrings;  // List<string>    (static)

                if (slots == null || logicSlotTypes == null || logicSlotStrings == null)
                    return;

                int appendedLines = 0; // lines we've added so far (incremented per \n)

                for (int slotIdx = 0; slotIdx < slots.Count; slotIdx++)
                {
                    var slot = slots[slotIdx];
                    if (slot == null)
                        continue;

                    // Header: \n\n<color=yellow>Slot N ... SlotDisplayName</color>
                    sb.AppendFormat(SlotHeaderFmt, slotIdx, slot.DisplayName);
                    // The format starts with two newlines: the first creates an empty separator
                    // line above the slot block (matching the original Slot Configuration Cartridge
                    // visual spacing), the second begins the header line. Both occupy their own
                    // line indices, but neither is a clickable logic-value line, so we don't register
                    // them. Bump appendedLines by 2 to keep subsequent logic-line indices aligned.
                    appendedLines += 2;

                    // Logic types for this slot
                    for (int typeIdx = 0; typeIdx < logicSlotTypes.Length; typeIdx++)
                    {
                        var lst = logicSlotTypes[typeIdx];
                        if (!____scannedDevice.CanLogicRead(lst, slotIdx))
                            continue;

                        string typeName = (logicSlotStrings.Count > typeIdx)
                            ? logicSlotStrings[typeIdx]
                            : lst.ToString();

                        bool writable = ____scannedDevice.CanLogicWrite(lst, slotIdx);
                        string colorOpen = writable ? ColorWritable : ColorReadOnly;

                        double rawValue = ____scannedDevice.GetLogicValue(lst, slotIdx);
                        string valueStr = Math.Round(rawValue, 3, MidpointRounding.AwayFromZero).ToString();

                        // \n{typeName} ... {colorOpen}{value}</color>
                        sb.AppendFormat(SlotLogicLineFmt, typeName, colorOpen, valueStr, ColorClose);

                        int absoluteLineIndex = baseLineIndex + appendedLines;
                        lineMap[absoluteLineIndex] = new SlotLineInfo
                        {
                            SlotIndex    = slotIdx,
                            LogicSlotType = lst,
                            IsWritable   = writable
                        };
                        appendedLines++;
                    }
                }

                if (sb.Length > 0)
                    ____outputText += sb.ToString();
            }
            catch (Exception e)
            {
                EquipmentPlusPlugin.Log.LogError(
                    $"ConfigCartridgeSlotDisplay: error building slot text: {e.Message}");
            }
            finally
            {
                if (lockTaken)
                    Monitor.Exit(____scannedDevice);
            }
        }
    }

    /// <summary>
    /// Cleanup: remove the slot-line registry entry when a cartridge is destroyed,
    /// keeping the dictionary from growing unbounded.
    /// This cooperates with ConfigCartridgeDestroyPatch in ConfigCartridgePatches.cs
    /// Both run as Prefix on Cartridge.OnDestroy; order is undefined but both clean up.
    /// </summary>
    [HarmonyPatch(typeof(Cartridge), nameof(Cartridge.OnDestroy))]
    public class ConfigCartridgeSlotDisplayDestroyPatch
    {
        [UsedImplicitly]
        public static void Prefix(Cartridge __instance)
        {
            if (__instance is ConfigCartridge cc)
                ConfigCartridgeSlotDisplayPatch.SlotLines.Remove(cc);
        }
    }
}
