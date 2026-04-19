using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.UI;
using HarmonyLib;
using JetBrains.Annotations;
using System.Reflection;

namespace EquipmentPlus
{
    /// <summary>
    /// InventoryWindow.ToggleVisibility only flips the canvas on/off; it does
    /// not re-run HandleOccupantChange or SetSlots. Widgets the user sees are
    /// whatever SetSlots built during the last Assign (when the lens/tablet
    /// first went into its parent slot) or the last time the parent slot's
    /// OnOccupantChange fired.
    ///
    /// While the window is closed, our OnChildEnterInventory Postfix does
    /// normalise dynamic-slot state, but its <c>RebuildInventoryWindowFor</c>
    /// uses <c>FindObjectsOfType&lt;InventoryWindow&gt;()</c> which skips
    /// inactive GameObjects, so a closed window never receives the rebuild.
    /// When the user later opens the window it shows the stale widget set:
    /// if FixingTheControls "Clear Hands" filled two chip slots while closed,
    /// the user sees one slot less than the number of chips present.
    ///
    /// Hook ToggleVisibility as a Postfix so that the moment the window
    /// becomes visible, we normalise slot state and force a full
    /// HandleOccupantChange to rebuild the widget list against the
    /// current slots. HandleOccupantChange is private; reached via
    /// AccessTools reflection.
    /// </summary>
    [HarmonyPatch(typeof(InventoryWindow), nameof(InventoryWindow.ToggleVisibility))]
    public class WindowOpenRefreshPatch
    {
        private static readonly MethodInfo _handleOccupantChange =
            AccessTools.Method(typeof(InventoryWindow), "HandleOccupantChange");

        [UsedImplicitly]
        public static void Postfix(InventoryWindow __instance)
        {
            if (!__instance.IsVisible) return;

            var parent = __instance.Parent;
            Slot.Class activeType;
            if (parent is SensorLenses)
                activeType = Slot.Class.SensorProcessingUnit;
            else if (parent is AdvancedTablet)
                activeType = Slot.Class.Cartridge;
            else
                return;

            DynamicSlots.RefreshSlotsNoRebuild(parent, activeType);
            _handleOccupantChange?.Invoke(__instance, null);
        }
    }
}
