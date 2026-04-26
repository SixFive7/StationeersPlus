using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Xml.Serialization;

namespace PowerTransmitterPlus
{
    // Side-car file persistence for the per-dish auto-aim target cache. Mirrors
    // the SprayPaintPlus v1.6.0 GlowSideCar pattern; see
    // Research/GameSystems/SaveZipExtension.md for the full mechanism (private
    // SaveHelper.Save worker, ZipOutputStream rebuild, LoadHelper.ExtractToTemp
    // pre-extraction, Thing.OnFinishedLoad timing relative to DeserializeSave
    // and IRotatable.OnClientStart).
    //
    // Writes pwrxmplus-autoaim.xml into the save ZIP after SaveHelper.Save has
    // sealed the archive and moved the temp file to its final location. Reads
    // it back in the XmlSaveLoad.LoadWorld postfix from the loose temp-dir
    // copy left by ExtractToTemp, and consumes the dictionary in the
    // Thing.OnFinishedLoad postfix per dish.
    //
    // Removal-safe: world.xml stays vanilla. If the mod is uninstalled, the
    // side-car entry is silently dropped on the next vanilla save (which
    // rebuilds the ZIP from scratch with only the five known entries) and the
    // vanilla load path never opens it. Unlike a custom ThingSaveData subclass,
    // there is no xsi:type taint that could break a save when the mod is gone.
    internal static class AutoAimSideCar
    {
        internal const string SideCarEntryName = "pwrxmplus-autoaim.xml";

        // Snapshot captured in the SaveHelper.Save prefix on the main thread
        // so the async save body's ThreadPool worker does not race gameplay
        // mutations of AutoAimState's cache.
        internal static AutoAimSideCarData PendingSaveSnapshot;

        // Populated in the XmlSaveLoad.LoadWorld postfix, consumed by each
        // Thing.OnFinishedLoad postfix that fires for a WirelessPower. Null
        // when the save has no side-car (mod was absent at save time).
        internal static Dictionary<long, long> LoadedTargets;

        internal static AutoAimSideCarData Snapshot()
        {
            var data = new AutoAimSideCarData();
            foreach (var pair in AutoAimState.SnapshotEntries())
            {
                data.Entries.Add(new AutoAimEntry
                {
                    DishReferenceId = pair.Key,
                    TargetReferenceId = pair.Value,
                });
            }
            return data;
        }

        internal static void WriteSideCar(string zipPath, AutoAimSideCarData data)
        {
            if (data == null || data.Entries == null || data.Entries.Count == 0)
            {
                RemoveSideCar(zipPath);
                return;
            }

            byte[] xmlBytes;
            using (var ms = new MemoryStream())
            {
                var serializer = new XmlSerializer(typeof(AutoAimSideCarData));
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
        // extracted every entry in the save ZIP (known and unknown alike) to a
        // temp directory; CurrentWorldSave.World.FullName points at
        // <tempDir>/world.xml. Our side-car entry is therefore at
        // <tempDir>/pwrxmplus-autoaim.xml as a loose file. Pass in the temp
        // dir path (Path.GetDirectoryName of World.FullName).
        internal static Dictionary<long, long> ReadSideCarFromDir(string tempDirPath)
        {
            if (string.IsNullOrEmpty(tempDirPath)) return null;
            var path = Path.Combine(tempDirPath, SideCarEntryName);
            if (!File.Exists(path)) return null;

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var serializer = new XmlSerializer(typeof(AutoAimSideCarData));
                var data = serializer.Deserialize(fs) as AutoAimSideCarData;
                if (data?.Entries == null) return new Dictionary<long, long>();
                var dict = new Dictionary<long, long>(data.Entries.Count);
                foreach (var entry in data.Entries)
                {
                    if (entry.DishReferenceId == 0L || entry.TargetReferenceId == 0L) continue;
                    dict[entry.DishReferenceId] = entry.TargetReferenceId;
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
                PowerTransmitterPlusPlugin.Log?.LogWarning(
                    $"Failed to remove empty auto-aim side-car at {zipPath}: {e.Message}");
            }
        }
    }

    // XML root for the side-car file. Public + parameterless ctor are
    // XmlSerializer requirements.
    [Serializable]
    [XmlRoot("AutoAimSideCar")]
    public class AutoAimSideCarData
    {
        [XmlArray("Entries")]
        [XmlArrayItem("Entry")]
        public List<AutoAimEntry> Entries { get; set; } = new List<AutoAimEntry>();
    }

    [Serializable]
    public class AutoAimEntry
    {
        public long DishReferenceId;
        public long TargetReferenceId;
    }
}
