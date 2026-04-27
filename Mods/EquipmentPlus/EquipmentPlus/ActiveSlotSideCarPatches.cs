using Assets.Scripts.Serialization;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using JetBrains.Annotations;
using System;
using System.IO;
using System.Threading;

namespace EquipmentPlus
{
    // Save/load Harmony patches for the active-slot side-car. Mirrors
    // PowerTransmitterPlus.AutoAimSaveLoadPatches; see that file plus
    // Research/GameSystems/SaveZipExtension.md for the canonical mechanism
    // (private SaveHelper.Save worker, async-task wrap, LoadHelper.ExtractToTemp,
    // OnFinishedLoad consumption).

    [HarmonyPatch(typeof(SaveHelper), "Save",
        new[] { typeof(DirectoryInfo), typeof(string), typeof(bool), typeof(CancellationToken) })]
    public class SaveHelperSaveActiveSlotPatch
    {
        [UsedImplicitly]
        public static void Prefix()
        {
            // Snapshot on the main thread; the async body switches to the
            // ThreadPool on its first await.
            ActiveSlotSideCar.PendingSaveSnapshot = ActiveSlotSideCar.Snapshot();
        }

        [UsedImplicitly]
        public static void Postfix(
            DirectoryInfo saveDirectory,
            string saveFileName,
            ref UniTask<SaveResult> __result)
        {
            var originalTask = __result;
            var snapshot = ActiveSlotSideCar.PendingSaveSnapshot;
            ActiveSlotSideCar.PendingSaveSnapshot = null;

            if (saveDirectory == null || string.IsNullOrEmpty(saveFileName) || snapshot == null)
                return;

            var path = Path.Combine(saveDirectory.FullName, saveFileName);
            __result = WriteSideCarAfterSave(originalTask, path, snapshot);
        }

        private static async UniTask<SaveResult> WriteSideCarAfterSave(
            UniTask<SaveResult> saveTask, string path, ActiveSlotSideCarData snapshot)
        {
            var result = await saveTask;
            if (!result.Success) return result;
            try
            {
                ActiveSlotSideCar.WriteSideCar(path, snapshot);
            }
            catch (Exception e)
            {
                EquipmentPlusPlugin.Log?.LogWarning(
                    $"Active-slot side-car write failed for {path}: {e.Message}");
            }
            return result;
        }
    }

    [HarmonyPatch(typeof(XmlSaveLoad), nameof(XmlSaveLoad.LoadWorld))]
    public class XmlSaveLoadLoadWorldActiveSlotPatch
    {
        [UsedImplicitly]
        public static void Postfix()
        {
            try
            {
                var save = XmlSaveLoad.Instance?.CurrentWorldSave;
                var worldPath = save?.World?.FullName;
                var tempDir = string.IsNullOrEmpty(worldPath)
                    ? null
                    : Path.GetDirectoryName(worldPath);
                var (sensors, cartridges) = ActiveSlotSideCar.ReadSideCarFromDir(tempDir);
                ActiveSlotSideCar.LoadedActiveSensors = sensors;
                ActiveSlotSideCar.LoadedActiveCartridges = cartridges;

                // Bridge into the existing per-Thing OnFinishedLoad consumer so
                // the same restore code path runs whether the entries came from
                // the old xsi:type save data (no longer written) or from the
                // side-car. ActiveSlotPersistence drains these dicts in its
                // OnFinishedLoad postfixes.
                if (sensors != null)
                {
                    foreach (var pair in sensors)
                        ActiveSlotPersistence.PendingActiveSensor[pair.Key] = pair.Value;
                }
                if (cartridges != null)
                {
                    foreach (var pair in cartridges)
                        ActiveSlotPersistence.PendingActiveCartridge[pair.Key] = pair.Value;
                }
            }
            catch (Exception e)
            {
                EquipmentPlusPlugin.Log?.LogWarning(
                    $"Active-slot side-car read failed: {e.Message}");
                ActiveSlotSideCar.LoadedActiveSensors = null;
                ActiveSlotSideCar.LoadedActiveCartridges = null;
            }
        }
    }
}
