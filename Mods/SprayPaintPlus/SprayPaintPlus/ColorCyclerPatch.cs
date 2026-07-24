using Assets.Scripts;
using Assets.Scripts.Inventory;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
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

            // Modifier polling (Shift = single, Ctrl = checkered) applies to
            // both the can and the glow gun; either tool paints through
            // NetworkPainterPatch which reads PlayerModifiers. Color cycling
            // only applies to the can; the gun is color-neutral.
            bool isCan = slotItem is SprayCan;
            bool isGun = slotItem is SprayGun;
            if (!isCan && !isGun)
                return;

            SendModifierStateIfChanged();

            if (!isCan)
                return;
            var sprayCan = (SprayCan)slotItem;

            if (KeyManager.GetMouseDown("Secondary"))
            {
                HandleEyedropper(__instance, sprayCan);
                return;
            }

            float scroll = __instance.newScrollData;
            if (scroll == 0f)
                return;

            int colorCount = GameManager.Instance?.CustomColors?.Count ?? 0;
            if (colorCount == 0)
                return;

            int previous = SprayPaintHelpers.GetSprayCanColorIndex(sprayCan);

            bool forward = SprayPaintPlusPlugin.InvertColorScrollDirection.Value
                ? scroll < 0f
                : scroll > 0f;

            int current = NextColorInCycle(previous, colorCount, forward);

            // Nothing else is selectable (every other color is DLC-gated or filtered out),
            // so leave the can alone rather than sending a no-op to the server.
            if (current == previous)
                return;

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

        /// <summary>
        /// Steps one place in the scroll direction, skipping over any color that is not in
        /// this client's cycle: DLC colors the session is not entitled to, and metallic
        /// colors an owner has switched off. Skipping rather than stopping keeps the wheel
        /// feeling continuous; stopping on a gated color would read as a stuck scroll.
        ///
        /// Starting the walk from the current index (rather than filtering a candidate list)
        /// also handles a can whose CURRENT color is gated. A player can legitimately hold a
        /// real metallic can in a session that has since lost its entitlement, and they can
        /// still scroll off it; they just cannot scroll back on.
        ///
        /// The loop is bounded by colorCount, so it terminates even in the degenerate case
        /// where nothing at all is selectable, returning the index it started from.
        /// </summary>
        private static int NextColorInCycle(int from, int colorCount, bool forward)
        {
            int candidate = from;

            for (int step = 0; step < colorCount; step++)
            {
                candidate += forward ? 1 : -1;

                if (candidate >= colorCount)
                    candidate = 0;
                else if (candidate < 0)
                    candidate = colorCount - 1;

                if (DlcPaintGate.IsColorInCycle(candidate))
                    return candidate;
            }

            return from;
        }

        private static void SendModifierStateIfChanged()
        {
            Human localHuman = InventoryManager.ParentHuman;
            if (localHuman == null)
                return;

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

            // Always mirror into the server-side dictionary locally. Host and
            // single-player go through the same PlayerModifiers lookup path as
            // remote clients do on the server.
            SprayPaintHelpers.PlayerModifiers[localHuman.ReferenceId] = modifiers;

            if (NetworkManager.IsClient && !NetworkManager.IsServer)
            {
                new PaintModifierMessage
                {
                    Modifiers = modifiers,
                    PlayerHumanId = localHuman.ReferenceId,
                }.SendToHost();
            }
        }

        private static void HandleEyedropper(InventoryManager inv, SprayCan sprayCan)
        {
            bool shiftHeld = KeyManager.GetButton(KeyCode.LeftShift)
                          || KeyManager.GetButton(KeyCode.RightShift);
            if (shiftHeld)
                return;

            bool ctrlHeld = KeyManager.GetButton(KeyCode.LeftControl)
                         || KeyManager.GetButton(KeyCode.RightControl);

            Thing target = CursorManager.CursorThing;
            if (!target)
                return;
            if (!target.IsPaintable)
                return;

            int pickedIndex;
            if (ctrlHeld)
            {
                pickedIndex = GetAsBuiltColorIndex(target);
            }
            else
            {
                var swatch = target.CustomColor;
                if (swatch == null)
                    return;
                pickedIndex = swatch.Index;
            }

            if (pickedIndex < 0)
                return;

            // Entitlement only, deliberately not IsColorInCycle. A world can hold metallic
            // paint the session is not entitled to (painted by an owner, or loaded from a
            // save), and copying it onto a can would be the same bypass by another route.
            // The cycle preference is not consulted: an owner who hid metallics from the
            // wheel and then deliberately eyedroppers a metallic wall meant to do that.
            if (!DlcPaintGate.IsColorAllowed(pickedIndex))
                return;

            int current = SprayPaintHelpers.GetSprayCanColorIndex(sprayCan);
            if (pickedIndex == current)
                return;

            if (NetworkManager.IsServer)
                SprayPaintHelpers.UpdateSprayCanServer(sprayCan, pickedIndex);
            else
                SprayPaintHelpers.UpdateSprayCanVisual(sprayCan, pickedIndex);

            inv.ActiveHand.Slot.RefreshSlotDisplay();

            if (NetworkManager.IsClient && !NetworkManager.IsServer)
            {
                new SprayCanColorMessage
                {
                    SprayCanId = sprayCan.ReferenceId,
                    ColorIndex = pickedIndex,
                }.SendToHost();
            }
        }

        // Returns the color index the target would have immediately after its
        // normal build flow (kit -> Constructor.Construct -> SetStructureData).
        // For kit-built Structures, reads the kit's PaintableMaterial via
        // ElectronicReader's prefab-hash lookup. Items and non-kit placements
        // fall back to the target's own PaintableMaterial.
        private static int GetAsBuiltColorIndex(Thing target)
        {
            var kits = ElectronicReader.GetAllConstructors(target);
            if (kits != null && kits.Count > 0)
            {
                IConstructionKit preferred = null;
                foreach (var k in kits)
                {
                    if (k is Constructor)
                    {
                        preferred = k;
                        break;
                    }
                }
                if (preferred == null)
                    preferred = kits[0];

                if (preferred is Thing kitThing && kitThing.PaintableMaterial != null)
                    return GameManager.GetColorIndex(kitThing.PaintableMaterial);
            }

            if (target.PaintableMaterial != null)
                return GameManager.GetColorIndex(target.PaintableMaterial);
            return -1;
        }
    }
}
