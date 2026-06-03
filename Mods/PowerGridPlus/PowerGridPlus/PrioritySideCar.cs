using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Xml.Serialization;

namespace PowerGridPlus
{
    // Side-car file persistence for per-Transformer Priority. Separate from the
    // PassthroughSideCar (`pwrgridplus-passthrough.xml`) so each feature's failure
    // mode stays independent and the XML schemas do not get mixed. Same shape as
    // PassthroughSideCar, parallel implementation.
    //
    // The file `pwrgridplus-priority.xml` is added as a ZIP entry inside the save
    // file. Vanilla load skips unknown ZIP entries silently, so installing /
    // uninstalling PGP is forward / backward compatible.
    internal static class PrioritySideCar
    {
        internal const string SideCarEntryName = "pwrgridplus-priority.xml";

        internal static PrioritySideCarData PendingSaveSnapshot;
        internal static Dictionary<long, int> LoadedPriorities;

        internal static PrioritySideCarData Snapshot()
        {
            var data = new PrioritySideCarData();
            foreach (var pair in PriorityStore.SnapshotEntries())
            {
                data.Entries.Add(new PriorityEntry
                {
                    ReferenceId = pair.Key,
                    Priority = pair.Value,
                });
            }
            return data;
        }

        internal static void WriteSideCar(string zipPath, PrioritySideCarData data)
        {
            if (data == null || data.Entries == null || data.Entries.Count == 0)
            {
                RemoveSideCar(zipPath);
                return;
            }

            byte[] xmlBytes;
            using (var ms = new MemoryStream())
            {
                var serializer = new XmlSerializer(typeof(PrioritySideCarData));
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
                var serializer = new XmlSerializer(typeof(PrioritySideCarData));
                var data = serializer.Deserialize(fs) as PrioritySideCarData;
                if (data?.Entries == null) return new Dictionary<long, int>();
                var dict = new Dictionary<long, int>(data.Entries.Count);
                foreach (var entry in data.Entries)
                {
                    if (entry.ReferenceId == 0L) continue;
                    dict[entry.ReferenceId] = entry.Priority < 0 ? 0 : entry.Priority;
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
                Plugin.Log?.LogWarning($"Failed to remove empty priority side-car at {zipPath}: {e.Message}");
            }
        }
    }

    [Serializable]
    [XmlRoot("PrioritySideCar")]
    public class PrioritySideCarData
    {
        [XmlArray("Entries")]
        [XmlArrayItem("Entry")]
        public List<PriorityEntry> Entries { get; set; } = new List<PriorityEntry>();
    }

    [Serializable]
    public class PriorityEntry
    {
        public long ReferenceId;
        public int Priority;
    }
}
