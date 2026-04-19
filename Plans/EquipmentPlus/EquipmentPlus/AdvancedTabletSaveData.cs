using VanillaTabletSaveData = Assets.Scripts.Objects.Items.AdvancedTabletSaveData;

namespace EquipmentPlus
{
    /// <summary>
    /// Extends the vanilla AdvancedTabletSaveData with the reference id of the
    /// currently-active cartridge so the tablet's Mode survives save/load.
    /// Vanilla does not persist <c>tablet.Mode</c>; without this data every
    /// load resets the active cartridge to the first Cartridge slot.
    ///
    /// Named EquipmentPlusTabletSaveData (not AdvancedTabletSaveData) so the
    /// short XML type name does not collide with the vanilla class when both
    /// are registered in XmlSaveLoad.ExtraTypes. Inherits from vanilla so
    /// AdvancedTablet.DeserializeSave's <c>isinst</c> check still recognises
    /// our instance as a valid tablet save, and any fields added to vanilla
    /// in a future game update are preserved for free.
    /// </summary>
    public class EquipmentPlusTabletSaveData : VanillaTabletSaveData
    {
        public long ActiveCartridgeReferenceId;
    }
}
