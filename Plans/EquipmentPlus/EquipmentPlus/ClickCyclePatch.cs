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

namespace EquipmentPlus
{
    /// <summary>
    /// Ctrl+left-click while holding a SensorLenses or AdvancedTablet cycles
    /// to the next loaded chip / cartridge.
    ///
    /// We patch <c>InventoryManager.HandlePrimaryUse</c> because neither
    /// vanilla prefab sets <c>AllowSelfUse = true</c>, so clicks never reach
    /// <c>Item.OnUsePrimary</c> via <c>UseItemOnSelf</c>. HandlePrimaryUse is
    /// the per-frame click handler and runs on the clicking peer regardless
    /// of the item's AttackWithEvent. Detecting modifier + click there fires
    /// identically in single-player, on the host, and on a remote client --
    /// no server-side dispatch concerns, no modifier-state mirroring needed.
    ///
    /// When the cycle fires we return <c>false</c> to suppress the rest of
    /// HandlePrimaryUse; vanilla would otherwise try an AttackWith on the
    /// cursor target with our item, which for the tablet/lenses does nothing
    /// useful anyway but we don't want the side effect.
    /// </summary>
    [HarmonyPatch(typeof(InventoryManager), "HandlePrimaryUse")]
    public class ClickCyclePatch
    {
        [UsedImplicitly]
        public static bool Prefix(InventoryManager __instance)
        {
            if (__instance == null) return true;

            // Only act on the frame the primary mouse button is pressed.
            if (!KeyManager.GetMouseDown("Primary")) return true;

            // Require Ctrl. Checked via the game's input layer (not Unity's
            // Input.GetKey) so remapped keys and input focus behave right.
            if (!KeyManager.GetButton(KeyCode.LeftControl)
                && !KeyManager.GetButton(KeyCode.RightControl))
                return true;

            var held = __instance.ActiveHand?.Slot?.Get();

            if (held is SensorLenses handLenses)
            {
                // No OnOff gate -- cycle doubles as power toggle.
                return !TryCycleSensor(handLenses);
            }

            if (held is AdvancedTablet tablet)
            {
                if (!tablet.OnOff) return true;
                return !TryCycleTablet(tablet);
            }

            // Empty hand: cycle the lenses the player is currently wearing.
            // Ctrl+click while wearing lenses is the only way to trigger the
            // cycle without removing them (which hides the overlay). Plain
            // click with empty hand stays vanilla (drag-pickup); only the
            // Ctrl-modified click flows to our cycle.
            //
            // No OnOff gate here: the cycle IS the on/off toggle -- landing
            // on the "off" cycle slot turns the lenses off, landing on any
            // chip turns them on. Otherwise cycling to off would power the
            // lenses off and immediately block the next Ctrl+click to power
            // them back on.
            if (held == null)
            {
                var human = InventoryManager.ParentHuman;
                if (human != null && human.GlassesSlot?.Get() is SensorLenses worn)
                {
                    return !TryCycleSensor(worn);
                }
            }

            return true;
        }

        // Cycle order: chip[0] -> chip[1] -> ... -> chip[N-1] -> off -> chip[0].
        // "Off" is a Sensor=null state that keeps the lenses powered but
        // hides the chip-specific overlay. This lets the player flip the
        // HUD off without having to toggle the lenses' power.
        private static bool TryCycleSensor(SensorLenses lenses)
        {
            var chips = new System.Collections.Generic.List<SensorProcessingUnit>();
            int currentIndex = -1;
            for (int i = 0; i < lenses.Slots.Count; i++)
            {
                if (lenses.Slots[i].Get() is SensorProcessingUnit s)
                {
                    if (s == lenses.Sensor) currentIndex = chips.Count;
                    chips.Add(s);
                }
            }
            if (chips.Count == 0) return false;

            // Extended cycle has chips.Count + 1 positions; position
            // chips.Count is the "off" slot (Sensor=null). If the current
            // Sensor isn't one of the chips (i.e. already null, or a stale
            // reference), treat current position as the off slot.
            int extPos = currentIndex < 0 ? chips.Count : currentIndex;
            int nextPos = (extPos + 1) % (chips.Count + 1);
            SensorProcessingUnit next = (nextPos == chips.Count) ? null : chips[nextPos];

            ApplySensorChange(lenses, next);
            return true;
        }

        // `sensor` may be null -- that's the "off" slot in the cycle, which
        // also powers the lenses off to stop the power drain.
        private static void ApplySensorChange(SensorLenses lenses, SensorProcessingUnit sensor)
        {
            bool powerOn = sensor != null;

            // Optimistic local update for responsive rendering on every role.
            // On remote clients the server's broadcast will overwrite these
            // with the same values shortly; on host/SP this IS the write.
            lenses.Sensor = sensor;
            if (lenses.OnOff != powerOn)
                lenses.OnOff = powerOn;

            // Remote client: defer authoritative apply to the server.
            // `IsActive && !IsServer` matches only a remote MP client --
            // single-player's NetworkRole.None falls through to the host path.
            if (NetworkManager.IsActive && !NetworkManager.IsServer)
            {
                new SetActiveSensorMessage
                {
                    LensesReferenceId = lenses.ReferenceId,
                    SensorReferenceId = sensor != null ? sensor.ReferenceId : 0L,
                    PowerOn = powerOn
                }.SendToHost();
                return;
            }

            // Host: flag for BuildUpdate broadcast to remote clients.
            // SP: no peers, no flag needed.
            if (NetworkManager.IsServer)
                lenses.NetworkUpdateFlags |= SensorLensesSync.ActiveSensorFlag;
        }

        private static bool TryCycleTablet(AdvancedTablet tablet)
        {
            int count = tablet.CartridgeSlots.Count;
            if (count < 2) return false;

            int cur = tablet.Mode;
            if (cur < 0 || cur >= count) cur = 0;
            int next = (cur + 1) % count;
            if (next == cur) return false;

            tablet.Mode = next;

            // Thing.set_Mode propagates via the networked Interactable state,
            // but it does NOT call GetCartridge() -- which is the method that
            // refreshes the displayed cartridge screen. Vanilla's Next-button
            // InteractWith path calls both; so do we. GetCartridge is private,
            // hence reflection via AccessTools.
            var m = AccessTools.Method(typeof(AdvancedTablet), "GetCartridge");
            m?.Invoke(tablet, null);

            return true;
        }
    }
}
