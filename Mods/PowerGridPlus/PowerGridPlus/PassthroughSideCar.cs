using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Xml.Serialization;

namespace PowerGridPlus
{
    // Side-car file persistence for per-Transformer LogicPassthroughMode. Mirrors
    // PowerTransmitterPlus's AutoAimSideCar pattern: a separate XML entry inside
    // the save ZIP that vanilla load skips silently when the mod is uninstalled.
    internal static class PassthroughSideCar
    {
        internal const string SideCarEntryName = "pwrgridplus-passthrough.xml";

        internal static PassthroughSideCarData PendingSaveSnapshot;
        internal static Dictionary<long, int> LoadedModes;

        internal static PassthroughSideCarData Snapshot()
        {
            var data = new PassthroughSideCarData();
            foreach (var pair in PassthroughModeStore.SnapshotEntries())
            {
                data.Entries.Add(new PassthroughEntry
                {
                    ReferenceId = pair.Key,
                    Mode = pair.Value,
                });
            }
            return data;
        }

        internal static void WriteSideCar(string zipPath, PassthroughSideCarData data)
        {
            if (data == null || data.Entries == null || data.Entries.Count == 0)
            {
                RemoveSideCar(zipPath);
                return;
            }

            byte[] xmlBytes;
            using (var ms = new MemoryStream())
            {
                var serializer = new XmlSerializer(typeof(PassthroughSideCarData));
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

        internal static Dictionary<long, int> ReadSideCarFromDir(string tempDirPath)
        {
            if (string.IsNullOrEmpty(tempDirPath)) return null;
            var path = Path.Combine(tempDirPath, SideCarEntryName);
            if (!File.Exists(path)) return null;

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var serializer = new XmlSerializer(typeof(PassthroughSideCarData));
                var data = serializer.Deserialize(fs) as PassthroughSideCarData;
                if (data?.Entries == null) return new Dictionary<long, int>();
                var dict = new Dictionary<long, int>(data.Entries.Count);
                foreach (var entry in data.Entries)
                {
                    if (entry.ReferenceId == 0L) continue;
                    dict[entry.ReferenceId] = entry.Mode != 0 ? 1 : 0;
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
                Plugin.Log?.LogWarning($"Failed to remove empty passthrough side-car at {zipPath}: {e.Message}");
            }
        }
    }

    [Serializable]
    [XmlRoot("PassthroughSideCar")]
    public class PassthroughSideCarData
    {
        [XmlArray("Entries")]
        [XmlArrayItem("Entry")]
        public List<PassthroughEntry> Entries { get; set; } = new List<PassthroughEntry>();
    }

    [Serializable]
    public class PassthroughEntry
    {
        public long ReferenceId;
        public int Mode;
    }
}
