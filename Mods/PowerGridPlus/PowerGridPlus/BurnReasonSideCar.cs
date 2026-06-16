using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Xml.Serialization;

namespace PowerGridPlus
{
    // Side-car file persistence for per-wreckage burn reasons (POWER.md §11.6 / POWERTODO 0.3).
    // Mirrors PassthroughSideCar: a separate XML entry inside the save ZIP that vanilla load skips
    // silently when the mod is uninstalled (world.xml is never touched; the orphan entry is dropped
    // on the next vanilla save). No schema version field, per the established side-car convention:
    // a future shape change rolls out as a clean read failure, handled by the try/catch below.
    internal static class BurnReasonSideCar
    {
        internal const string SideCarEntryName = "pwrgridplus-burnreason.xml";

        internal static BurnReasonSideCarData PendingSaveSnapshot;
        internal static Dictionary<long, string> LoadedReasons;

        internal static BurnReasonSideCarData Snapshot()
        {
            var data = new BurnReasonSideCarData();
            foreach (var pair in BurnReasonRegistry.SnapshotAttached())
            {
                data.Entries.Add(new BurnReasonEntry
                {
                    ReferenceId = pair.Key,
                    Reason = pair.Value,
                });
            }
            return data;
        }

        internal static void WriteSideCar(string zipPath, BurnReasonSideCarData data)
        {
            if (data == null || data.Entries == null || data.Entries.Count == 0)
            {
                RemoveSideCar(zipPath);
                return;
            }

            byte[] xmlBytes;
            using (var ms = new MemoryStream())
            {
                var serializer = new XmlSerializer(typeof(BurnReasonSideCarData));
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

        internal static Dictionary<long, string> ReadSideCarFromDir(string tempDirPath)
        {
            if (string.IsNullOrEmpty(tempDirPath)) return null;
            var path = Path.Combine(tempDirPath, SideCarEntryName);
            if (!File.Exists(path)) return null;

            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var serializer = new XmlSerializer(typeof(BurnReasonSideCarData));
                    var data = serializer.Deserialize(fs) as BurnReasonSideCarData;
                    if (data?.Entries == null) return new Dictionary<long, string>();
                    var dict = new Dictionary<long, string>(data.Entries.Count);
                    foreach (var entry in data.Entries)
                    {
                        if (entry.ReferenceId == 0L || string.IsNullOrEmpty(entry.Reason)) continue;
                        dict[entry.ReferenceId] = entry.Reason;
                    }
                    return dict;
                }
            }
            catch (Exception e)
            {
                // Parse failure degrades to "no side-car": the wreckage shows the plain vanilla
                // burned-cable hover with no Burned: line. Never throws into the load pipeline.
                Plugin.Log?.LogWarning($"BurnReasonSideCar parse failed: {e.Message}");
                return null;
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
                Plugin.Log?.LogWarning($"Failed to remove empty burn-reason side-car at {zipPath}: {e.Message}");
            }
        }
    }

    [Serializable]
    [XmlRoot("BurnReasonSideCar")]
    public class BurnReasonSideCarData
    {
        [XmlArray("Entries")]
        [XmlArrayItem("Entry")]
        public List<BurnReasonEntry> Entries { get; set; } = new List<BurnReasonEntry>();
    }

    [Serializable]
    public class BurnReasonEntry
    {
        public long ReferenceId;
        public string Reason;
    }
}
