using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Xml.Serialization;

namespace EquipmentPlus
{
    // Side-car file persistence for SensorLenses' active sensor and AdvancedTablet's
    // active cartridge. Mirrors the SprayPaintPlus v1.6.0 GlowSideCar /
    // PowerTransmitterPlus AutoAimSideCar pattern; see
    // Research/GameSystems/SaveZipExtension.md for the mechanism.
    //
    // Removal-safe: world.xml stays vanilla, no xsi:type taint. If EquipmentPlus
    // is uninstalled, the side-car entry is silently dropped on the next vanilla
    // save (which rebuilds the ZIP from scratch with only the five known entries)
    // and the vanilla load path never opens it. Replaces the previous
    // SensorLensesSaveData / EquipmentPlusTabletSaveData xsi:type subclasses
    // which would have failed XmlSerializer.Deserialize on mod removal.
    internal static class ActiveSlotSideCar
    {
        internal const string SideCarEntryName = "equipmentplus-active-slots.xml";

        // Snapshot captured on the main thread in SaveHelper.Save Prefix so the
        // async save body's ThreadPool worker does not race a slot reassignment
        // mid-serialize.
        internal static ActiveSlotSideCarData PendingSaveSnapshot;

        // Populated by the LoadWorld postfix. Drained by ActiveSlotPersistence's
        // OnFinishedLoad postfixes per Thing. Null when the save has no side-car
        // (mod was absent at save time, or first-ever save with the mod).
        internal static Dictionary<long, long> LoadedActiveSensors;
        internal static Dictionary<long, long> LoadedActiveCartridges;

        internal static ActiveSlotSideCarData Snapshot()
        {
            var data = new ActiveSlotSideCarData();
            // Walk every Thing in the scene via DensePool.ForEach. AllThings
            // is the full set the save serializer iterates, so capturing here
            // matches exactly what's about to be saved.
            OcclusionManager.AllThings.ForEach(thing =>
            {
                if (thing == null) return;
                if (thing is SensorLenses lenses)
                {
                    long activeId = lenses.Sensor != null ? lenses.Sensor.ReferenceId : 0L;
                    if (activeId != 0L)
                    {
                        data.ActiveSensors.Add(new ActiveSlotEntry
                        {
                            ThingReferenceId = lenses.ReferenceId,
                            ActiveReferenceId = activeId,
                        });
                    }
                }
                else if (thing is AdvancedTablet tablet)
                {
                    long activeId = tablet.Cartridge != null ? tablet.Cartridge.ReferenceId : 0L;
                    if (activeId != 0L)
                    {
                        data.ActiveCartridges.Add(new ActiveSlotEntry
                        {
                            ThingReferenceId = tablet.ReferenceId,
                            ActiveReferenceId = activeId,
                        });
                    }
                }
            });
            return data;
        }

        internal static void WriteSideCar(string zipPath, ActiveSlotSideCarData data)
        {
            if (data == null
                || ((data.ActiveSensors == null || data.ActiveSensors.Count == 0)
                    && (data.ActiveCartridges == null || data.ActiveCartridges.Count == 0)))
            {
                RemoveSideCar(zipPath);
                return;
            }

            byte[] xmlBytes;
            using (var ms = new MemoryStream())
            {
                var serializer = new XmlSerializer(typeof(ActiveSlotSideCarData));
                serializer.Serialize(ms, data);
                xmlBytes = ms.ToArray();
            }

            using (var fs = new FileStream(zipPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Update))
            {
                archive.GetEntry(SideCarEntryName)?.Delete();
                var entry = archive.CreateEntry(SideCarEntryName, CompressionLevel.Optimal);
                using (var es = entry.Open())
                {
                    es.Write(xmlBytes, 0, xmlBytes.Length);
                }
            }
        }

        internal static (Dictionary<long, long> sensors, Dictionary<long, long> cartridges)
            ReadSideCarFromDir(string tempDirPath)
        {
            if (string.IsNullOrEmpty(tempDirPath))
                return (null, null);
            var path = Path.Combine(tempDirPath, SideCarEntryName);
            if (!File.Exists(path))
                return (null, null);

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var serializer = new XmlSerializer(typeof(ActiveSlotSideCarData));
                var data = serializer.Deserialize(fs) as ActiveSlotSideCarData;
                if (data == null)
                    return (new Dictionary<long, long>(), new Dictionary<long, long>());

                var sensors = new Dictionary<long, long>(data.ActiveSensors?.Count ?? 0);
                if (data.ActiveSensors != null)
                {
                    foreach (var e in data.ActiveSensors)
                    {
                        if (e.ThingReferenceId == 0L) continue;
                        sensors[e.ThingReferenceId] = e.ActiveReferenceId;
                    }
                }

                var cartridges = new Dictionary<long, long>(data.ActiveCartridges?.Count ?? 0);
                if (data.ActiveCartridges != null)
                {
                    foreach (var e in data.ActiveCartridges)
                    {
                        if (e.ThingReferenceId == 0L) continue;
                        cartridges[e.ThingReferenceId] = e.ActiveReferenceId;
                    }
                }
                return (sensors, cartridges);
            }
        }

        private static void RemoveSideCar(string zipPath)
        {
            if (string.IsNullOrEmpty(zipPath) || !File.Exists(zipPath)) return;
            try
            {
                using (var fs = new FileStream(zipPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                using (var archive = new ZipArchive(fs, ZipArchiveMode.Update))
                {
                    archive.GetEntry(SideCarEntryName)?.Delete();
                }
            }
            catch (Exception e)
            {
                EquipmentPlusPlugin.Log?.LogWarning(
                    $"Failed to remove empty active-slot side-car at {zipPath}: {e.Message}");
            }
        }
    }

    [Serializable]
    [XmlRoot("EquipmentPlusActiveSlots")]
    public class ActiveSlotSideCarData
    {
        [XmlArray("ActiveSensors")]
        [XmlArrayItem("Entry")]
        public List<ActiveSlotEntry> ActiveSensors { get; set; } = new List<ActiveSlotEntry>();

        [XmlArray("ActiveCartridges")]
        [XmlArrayItem("Entry")]
        public List<ActiveSlotEntry> ActiveCartridges { get; set; } = new List<ActiveSlotEntry>();
    }

    [Serializable]
    public class ActiveSlotEntry
    {
        public long ThingReferenceId;
        public long ActiveReferenceId;
    }
}
