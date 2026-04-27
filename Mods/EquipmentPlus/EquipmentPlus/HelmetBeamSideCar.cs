using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Xml.Serialization;

namespace EquipmentPlus
{
    // Side-car file persistence for the per-character helmet beam settings cache
    // (HelmetBeamState.PerCharacter, keyed by Human.ReferenceId). Mirrors the
    // SprayPaintPlus / PowerTransmitterPlus side-car pattern; see
    // Research/GameSystems/SaveZipExtension.md for the mechanism.
    //
    // Removal-safe: world.xml stays vanilla. If EquipmentPlus is uninstalled,
    // the side-car entry is silently dropped on the next vanilla save and the
    // vanilla load path never opens it.
    //
    // Multiplayer authority: only the host writes saves. Remote clients push
    // their per-character beam state to the host via SetBeamSettingsMessage so
    // the host's PerCharacter dict has the right value at save-snapshot time.
    // On join, the host pushes its full dict to the joining client via
    // EquipmentPlusPlugin's IJoinSuffixSerializer implementation so the client
    // sees their own preference applied immediately.
    internal static class HelmetBeamSideCar
    {
        internal const string SideCarEntryName = "equipmentplus-beam.xml";

        internal static HelmetBeamSideCarData PendingSaveSnapshot;

        // Populated by the LoadWorld postfix; consumed by the
        // ApplyAfterLoad helper on the next frame after Things are restored.
        // Null when the save has no side-car (mod absent at save time, or
        // first-ever save with the mod).
        internal static Dictionary<long, BeamSettings> LoadedBeamMap;

        internal static HelmetBeamSideCarData Snapshot()
        {
            var data = new HelmetBeamSideCarData();
            foreach (var pair in HelmetBeamState.PerCharacter)
            {
                data.Entries.Add(new BeamEntry
                {
                    HumanReferenceId = pair.Key,
                    SpotAngle = pair.Value.SpotAngle,
                    Intensity = pair.Value.Intensity,
                    Range = pair.Value.Range,
                });
            }
            return data;
        }

        internal static void WriteSideCar(string zipPath, HelmetBeamSideCarData data)
        {
            if (data == null || data.Entries == null || data.Entries.Count == 0)
            {
                RemoveSideCar(zipPath);
                return;
            }

            byte[] xmlBytes;
            using (var ms = new MemoryStream())
            {
                var serializer = new XmlSerializer(typeof(HelmetBeamSideCarData));
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

        internal static Dictionary<long, BeamSettings> ReadSideCarFromDir(string tempDirPath)
        {
            if (string.IsNullOrEmpty(tempDirPath)) return null;
            var path = Path.Combine(tempDirPath, SideCarEntryName);
            if (!File.Exists(path)) return null;

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var serializer = new XmlSerializer(typeof(HelmetBeamSideCarData));
                var data = serializer.Deserialize(fs) as HelmetBeamSideCarData;
                if (data?.Entries == null) return new Dictionary<long, BeamSettings>();
                var dict = new Dictionary<long, BeamSettings>(data.Entries.Count);
                foreach (var entry in data.Entries)
                {
                    if (entry.HumanReferenceId == 0L) continue;
                    dict[entry.HumanReferenceId] = new BeamSettings
                    {
                        SpotAngle = entry.SpotAngle,
                        Intensity = entry.Intensity,
                        Range = entry.Range,
                    };
                }
                return dict;
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
                    $"Failed to remove empty helmet-beam side-car at {zipPath}: {e.Message}");
            }
        }
    }

    [Serializable]
    [XmlRoot("EquipmentPlusBeamSideCar")]
    public class HelmetBeamSideCarData
    {
        [XmlArray("Entries")]
        [XmlArrayItem("Entry")]
        public List<BeamEntry> Entries { get; set; } = new List<BeamEntry>();
    }

    [Serializable]
    public class BeamEntry
    {
        public long HumanReferenceId;
        public float SpotAngle;
        public float Intensity;
        public float Range;
    }
}
