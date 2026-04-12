using Assets.Scripts;
using Assets.Scripts.Inventory;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects.Items;
using HarmonyLib;
using JetBrains.Annotations;
using LaunchPadBooster.Networking;
using UnityEngine;

namespace SprayPaintPlus
{
    /// <summary>
    /// Detects mouse scroll while holding a spray can and cycles the color.
    /// Sends color change and modifier key state to the server via
    /// LaunchPadBooster ModNetworkMessages.
    /// </summary>
    [HarmonyPatch(typeof(InventoryManager), "NormalMode")]
    public class ColorCyclerPatch
    {
        private static byte _lastSentModifiers = 0xFF; // force initial send

        [UsedImplicitly]
        public static void Prefix(InventoryManager __instance)
        {
            var slotItem = __instance.ActiveHand?.Slot?.Get();
            if (slotItem == null)
                return;

            if (!(slotItem is SprayCan sprayCan))
                return;

            SendModifierStateIfChanged();

            float scroll = __instance.newScrollData;
            if (scroll == 0f)
                return;

            int colorCount = GameManager.Instance?.CustomColors?.Count ?? 0;
            if (colorCount == 0)
                return;

            int current = SprayPaintHelpers.GetSprayCanColorIndex(sprayCan);

            bool forward = SprayPaintPlusPlugin.InvertColorScrollDirection.Value
                ? scroll < 0f
                : scroll > 0f;

            current += forward ? 1 : -1;

            if (current >= colorCount)
                current = 0;
            else if (current < 0)
                current = colorCount - 1;

            if (NetworkManager.IsServer)
            {
                SprayPaintHelpers.UpdateSprayCanServer(sprayCan, current);
            }
            else
            {
                SprayPaintHelpers.UpdateSprayCanVisual(sprayCan, current);
            }

            __instance.ActiveHand.Slot.RefreshSlotDisplay();

            if (NetworkManager.IsClient && !NetworkManager.IsServer)
            {
                new SprayCanColorMessage
                {
                    SprayCanId = sprayCan.ReferenceId,
                    ColorIndex = current,
                }.SendToHost();
            }
        }

        private static void SendModifierStateIfChanged()
        {
            bool shiftHeld = KeyManager.GetButton(KeyCode.LeftShift)
                          || KeyManager.GetButton(KeyCode.RightShift);
            bool ctrlHeld = KeyManager.GetButton(KeyCode.LeftControl)
                         || KeyManager.GetButton(KeyCode.RightControl);
            bool invertShift = SprayPaintPlusPlugin.PaintSingleItemByDefault.Value;

            byte modifiers = 0;
            if (shiftHeld != invertShift)
                modifiers |= 1;
            if (ctrlHeld)
                modifiers |= 2;

            if (modifiers == _lastSentModifiers)
                return;

            _lastSentModifiers = modifiers;

            // Only remote clients need to send modifier state to the server.
            // The host reads its own keyboard directly in NetworkPainterPatch.
            if (NetworkManager.IsClient && !NetworkManager.IsServer)
            {
                new PaintModifierMessage
                {
                    Modifiers = modifiers,
                }.SendToHost();
            }
        }
    }
}
