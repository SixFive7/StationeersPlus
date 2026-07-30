using Assets.Scripts;
using Assets.Scripts.Inventory;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using HarmonyLib;
using JetBrains.Annotations;
using LaunchPadBooster.Networking;
using StationeersPlus.Shared;
using UnityEngine;

namespace SprayPaintPlus
{
    /// <summary>
    /// Detects mouse scroll while holding a spray can and cycles the color.
    /// Sends color change and the player's client-half preference mask to the server via
    /// LaunchPadBooster ModNetworkMessages.
    /// </summary>
    [HarmonyPatch(typeof(InventoryManager), "NormalMode")]
    public class ColorCyclerPatch
    {
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

            ColorCyclingMode mode = SettingsMerge.EffectiveColorCycling;
            if (mode == ColorCyclingMode.CannotChange)
            {
                // The wheel is dead: no color computed, nothing applied, nothing sent.
                // The can keeps whatever color it currently has, which is the point of
                // the mode. Warn only if OUR half would have allowed the scroll, so the
                // player who switched their own wheel off is not lectured about it.
                if (ClientAllowsColorCycling())
                    WarningNotifier.WarnBlocked(WarningNotifier.Functions.ColorCycling);
                return;
            }

            int previous = SprayPaintHelpers.GetSprayCanColorIndex(sprayCan);

            bool forward = SprayPaintPlusPlugin.InvertColorScrollDirection.Value
                ? scroll < 0f
                : scroll > 0f;

            int current = NextColorInCycle(previous, colorCount, forward, mode);

            // Nothing else is selectable: every other color is either DLC-gated or, under
            // WithinFamily, outside this can's family. Leave the can alone rather than
            // sending a no-op to the server.
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
        /// Steps one place in the scroll direction, skipping over any color the current
        /// mode may not land on. Skipping rather than stopping keeps the wheel feeling
        /// continuous; stopping on a gated color would read as a stuck scroll.
        ///
        /// Two filters, in this order and never the other way round:
        ///
        ///   1. Entitlement, via the DLC gate. Hard, checked first in every mode, and no
        ///      mode may loosen it. A session that does not own Metallic Paints cannot
        ///      reach those four colors by any route.
        ///   2. Paint family, only under ColorCyclingMode.WithinFamily. Judged against
        ///      `from`, the color the can currently carries, so a base can walks the base
        ///      colors and a metallic can walks the metallic ones.
        ///
        /// Starting the walk from the current index (rather than filtering a candidate list)
        /// also handles a can whose CURRENT color is gated. A player can legitimately hold a
        /// real metallic can in a session that has since lost its entitlement, and they can
        /// still scroll off it; they just cannot scroll back on. Under WithinFamily such a
        /// can is pinned to its own family, which is the metallic one, so the entitlement
        /// filter leaves it nowhere to go and the wheel is inert. That is correct: the
        /// alternative is letting the family rule launder an unentitled can into base
        /// colors it was never dispensed in.
        ///
        /// The loop is bounded by colorCount, so it terminates even in the degenerate case
        /// where nothing at all is selectable, returning the index it started from.
        /// </summary>
        private static int NextColorInCycle(int from, int colorCount, bool forward, ColorCyclingMode mode)
        {
            int candidate = from;

            for (int step = 0; step < colorCount; step++)
            {
                candidate += forward ? 1 : -1;

                if (candidate >= colorCount)
                    candidate = 0;
                else if (candidate < 0)
                    candidate = colorCount - 1;

                // Hard gate first, always.
                if (!DlcPaintGate.IsColorInCycle(candidate))
                    continue;

                if (mode == ColorCyclingMode.WithinFamily && !DlcPaintGate.SameFamily(from, candidate))
                    continue;

                return candidate;
            }

            return from;
        }

        /// <summary>
        /// Packs this player's client-half preferences and pushes them to the server when
        /// they change. The mask is no longer only the two live modifier keys: it carries
        /// every client-side setting the server has to merge before acting on this
        /// player's paint. Because PackLocal re-reads the config entries on every call, a
        /// setting the player flips mid-session is picked up here and resent without any
        /// separate change notification.
        /// </summary>
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

            // Bit 0 carries the single-item intent with the invert ALREADY applied, exactly
            // as it always has: the server reads the outcome, never the raw key, and knows
            // nothing about this client's inversion preference.
            bool singleItem = shiftHeld != invertShift;
            bool checkered = ctrlHeld;

            ushort prefs = SettingsMerge.PlayerPrefs.PackLocal(singleItem, checkered);

            // The dictionary is the record of what we last reported, so it doubles as the
            // dedupe, and the server keeps its own copy of the same row under the same key.
            // The two stay symmetric because neither side drops it: a rejoin into the same
            // world hands the player back the same Human ReferenceId (see
            // Research/GameSystems/PlayerIdentityAcrossRejoin.md), and the server no longer
            // prunes on disconnect precisely so that a client with nothing new to say does
            // not have to say it again. A settings change while away is still reported: the
            // mask is repacked from the live config entries on every call, so it differs
            // from the row here and the send goes out.
            if (SprayPaintHelpers.PlayerModifiers.TryGetValue(localHuman.ReferenceId, out ushort reported)
                && reported == prefs)
                return;

            // Always mirror into the server-side dictionary locally. Host and
            // single-player go through the same PlayerModifiers lookup path as
            // remote clients do on the server.
            SprayPaintHelpers.PlayerModifiers[localHuman.ReferenceId] = prefs;

            if (NetworkManager.IsClient && !NetworkManager.IsServer)
            {
                new PaintModifierMessage
                {
                    Modifiers = prefs,
                    PlayerHumanId = localHuman.ReferenceId,
                }.SendToHost();
            }
        }

        // ---- Blame attribution for the first-use warnings ------------------------
        // These read the CLIENT half of a paired setting on its own, which is the one
        // thing SettingsMerge does not expose (it only ever answers with both halves
        // merged, which is right for deciding behavior). Nothing here decides behavior:
        // the merged accessor has already done that and the answer was "no". All these
        // decide is whether the block is worth telling the player about, and a player who
        // switched the function off themselves should never be told the server did it.
        //
        // The test is "would our own half alone have allowed this". If yes, the only thing
        // left that could have blocked it is the server half. If no, the player's own
        // choice already produces this outcome and the server agreeing changes nothing
        // they would want to hear about.

        private static bool ClientAllowsColorCycling()
        {
            var client = SprayPaintPlusPlugin.ClientColorCycling?.Value ?? ColorCyclingMode.AllColors;
            return client != ColorCyclingMode.CannotChange;
        }

        private static bool ClientAllowsColorPicking()
        {
            // Mirrors SettingsMerge.EffectiveColorPicking with the server halves dropped:
            // the picking toggle AND the mode, because eyedropping is a color change and a
            // can that cannot change color cannot be eyedropped onto either.
            return (SprayPaintPlusPlugin.ClientColorPicking?.Value ?? true)
                && ClientAllowsColorCycling();
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

            // Entitlement only, deliberately not IsColorInCycle, and checked before any mod
            // setting. A world can hold metallic paint the session is not entitled to
            // (painted by an owner, or loaded from a save), and copying it onto a can would
            // be the same bypass by another route. The cycle preference is not consulted
            // for on/off purposes: a player who deliberately aims at a wall and right-clicks
            // it meant to copy that color, which is a different act from spinning a wheel
            // past it.
            if (!DlcPaintGate.IsColorAllowed(pickedIndex))
                return;

            int current = SprayPaintHelpers.GetSprayCanColorIndex(sprayCan);
            if (pickedIndex == current)
                return;

            // Everything above has established that a pick would genuinely have changed the
            // can's color, which is the point at which a block is worth reporting. Warning
            // on a right-click into empty air, at an unpaintable target, or onto the color
            // the can already carries would be noise about a function that changed nothing.
            if (!SettingsMerge.EffectiveColorPicking)
            {
                // EffectiveColorPicking already folds in the CannotChange rule, so there is
                // nothing extra to check for on/off purposes. Blame the server only.
                if (ClientAllowsColorPicking())
                    WarningNotifier.WarnBlocked(WarningNotifier.Functions.ColorPicking);
                return;
            }

            // The family rule is a restriction, not a disabled function, so it does not go
            // through WarnBlocked: it answers a deliberate action every single time rather
            // than reporting a background condition, and is therefore exempt from the
            // three-per-session cap.
            //
            // Throttle.Never keeps that intent exactly, and the input shape is what makes
            // it safe. This sits behind KeyManager.GetMouseDown("Secondary"), a press edge,
            // not a per-frame or per-scroll-notch path, and it is reached only after the
            // cursor resolved a paintable target carrying a color the session is entitled
            // to and different from the one on the can. Rapid clicking is the worst case
            // and it costs one line per click. A Cooldown would trade that for something
            // worse: the second right-click, aimed at a different object, would answer
            // with silence, which reads as the mod being broken rather than as a rule
            // being enforced. Never say nothing in reply to a deliberate action.
            if (SettingsMerge.EffectiveColorCycling == ColorCyclingMode.WithinFamily
                && !DlcPaintGate.SameFamily(current, pickedIndex))
            {
                string canFamily = DlcPaintGate.FamilyName(current);
                string pickedFamily = DlcPaintGate.FamilyName(pickedIndex);
                PlayerMessage.Info("eyedropper-cross-family", Throttle.Never,
                    $"Color cycling is limited to one paint family here: " +
                    $"a {canFamily} spray can cannot copy {pickedFamily} paint. " +
                    $"Print a {pickedFamily} can to use that color.");
                return;
            }

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
