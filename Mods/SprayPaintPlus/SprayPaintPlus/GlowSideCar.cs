using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Xml.Serialization;

namespace SprayPaintPlus
{
    // Side-car file persistence for glow state. See
    // Research/GameSystems/SaveZipExtension.md for the ZIP read/write
    // asymmetry and the Harmony interception pattern.
    //
    // Writes sprayplus-glow.xml into the save ZIP after SaveHelper.Save has
    // sealed the archive and moved the temp file to its final location. Reads
    // it back in the XmlSaveLoad.LoadWorld postfix, and consumes the set in
    // the Thing.OnFinishedLoad postfix per Thing.
    //
    // Removal-safe: if the mod is absent, the entry is silently ignored on
    // load. On the next vanilla save, SaveHelper.Save rebuilds the ZIP from
    // scratch via ZipOutputStream with only the five known entries, so the
    // orphan side-car is dropped. Unlike GlowThingSaveData, this does not
    // taint world.xml with an unregistered xsi:type.

    internal static class GlowSideCar
    {
        internal const string SideCarEntryName = "sprayplus-glow.xml";

        // Snapshot of glowing ReferenceIds captured in the SaveHelper.Save
        // prefix. The async save body resumes on a ThreadPool worker, so a
        // main-thread snapshot removes the race with gameplay mutations of
        // GlowPaintHelpers.GlowingThingIds during the save.
        internal static List<long> PendingSaveSnapshot;

        // Populated in the XmlSaveLoad.LoadWorld postfix, consumed by each
        // Thing.OnFinishedLoad postfix. Null when the save has no side-car.
        internal static HashSet<long> LoadedGlowIds;

        internal static List<long> SnapshotGlowingIds()
        {
            var source = GlowPaintHelpers.GlowingThingIds;
            var ids = new List<long>(source.Count);
            foreach (var kv in source)
            {
                if (kv.Value) ids.Add(kv.Key);
            }
            return ids;
        }

        internal static void WriteSideCar(string zipPath, List<long> glowingIds)
        {
            if (glowingIds == null || glowingIds.Count == 0)
            {
                RemoveSideCar(zipPath);
                return;
            }

            byte[] xmlBytes;
            using (var ms = new MemoryStream())
            {
                var data = new GlowSideCarData { Ids = glowingIds };
                var serializer = new XmlSerializer(typeof(GlowSideCarData));
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

        // Read path. At load time, LoadHelper.ExtractToTemp has already
        // extracted every entry in the save ZIP (known and unknown alike) to
        // a temp directory; CurrentWorldSave.World.FullName is <tempDir>/world.xml.
        // Our side-car entry is therefore at <tempDir>/sprayplus-glow.xml as
        // a loose file, and we never need to re-open the closed save ZIP.
        // Pass in the temp-dir path (Path.GetDirectoryName of World.FullName).
        internal static HashSet<long> ReadSideCarFromDir(string tempDirPath)
        {
            if (string.IsNullOrEmpty(tempDirPath)) return null;
            var path = Path.Combine(tempDirPath, SideCarEntryName);
            if (!File.Exists(path)) return null;

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var serializer = new XmlSerializer(typeof(GlowSideCarData));
                var data = serializer.Deserialize(fs) as GlowSideCarData;
                return data?.Ids == null
                    ? new HashSet<long>()
                    : new HashSet<long>(data.Ids);
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
                SprayPaintPlusPlugin.Log?.LogWarning(
                    $"Failed to remove empty glow side-car at {zipPath}: {e.Message}");
            }
        }
    }

    // XML root for the side-car file. Public + parameterless ctor are
    // XmlSerializer requirements.
    [Serializable]
    [XmlRoot("GlowSideCar")]
    public class GlowSideCarData
    {
        [XmlArray("Ids")]
        [XmlArrayItem("Id")]
        public List<long> Ids { get; set; }
    }
}
