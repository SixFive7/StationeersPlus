using Assets.Scripts.Serialization;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using JetBrains.Annotations;
using System;
using System.IO;
using System.Threading;

namespace EquipmentPlus
{
    // Save/load Harmony patches for the helmet-beam side-car. Mirrors
    // ActiveSlotSideCarPatches; see PowerTransmitterPlus.AutoAimSaveLoadPatches
    // and Research/GameSystems/SaveZipExtension.md for the canonical mechanism.

    [HarmonyPatch(typeof(SaveHelper), "Save",
        new[] { typeof(DirectoryInfo), typeof(string), typeof(bool), typeof(CancellationToken) })]
    public class SaveHelperSaveBeamPatch
    {
        [UsedImplicitly]
        public static void Prefix()
        {
            HelmetBeamSideCar.PendingSaveSnapshot = HelmetBeamSideCar.Snapshot();
        }

        [UsedImplicitly]
        public static void Postfix(
            DirectoryInfo saveDirectory,
            string saveFileName,
            ref UniTask<SaveResult> __result)
        {
            var originalTask = __result;
            var snapshot = HelmetBeamSideCar.PendingSaveSnapshot;
            HelmetBeamSideCar.PendingSaveSnapshot = null;

            if (saveDirectory == null || string.IsNullOrEmpty(saveFileName) || snapshot == null)
                return;

            var path = Path.Combine(saveDirectory.FullName, saveFileName);
            __result = WriteSideCarAfterSave(originalTask, path, snapshot);
        }

        private static async UniTask<SaveResult> WriteSideCarAfterSave(
            UniTask<SaveResult> saveTask, string path, HelmetBeamSideCarData snapshot)
        {
            var result = await saveTask;
            if (!result.Success) return result;
            try
            {
                HelmetBeamSideCar.WriteSideCar(path, snapshot);
            }
            catch (Exception e)
            {
                EquipmentPlusPlugin.Log?.LogWarning(
                    $"Helmet-beam side-car write failed for {path}: {e.Message}");
            }
            return result;
        }
    }

    [HarmonyPatch(typeof(XmlSaveLoad), nameof(XmlSaveLoad.LoadWorld))]
    public class XmlSaveLoadLoadWorldBeamPatch
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
                var loaded = HelmetBeamSideCar.ReadSideCarFromDir(tempDir);
                HelmetBeamSideCar.LoadedBeamMap = loaded;

                // Apply directly into the in-memory state. ReferenceId values
                // survive save/load (vanilla restores Things with their saved
                // ReferenceId, see ReferencableSaveData), so populating
                // PerCharacter at load time means the next HelmetBeamApplyPatch
                // postfix on the local human's LateUpdate already has the
                // right entry to apply.
                if (loaded != null)
                {
                    foreach (var pair in loaded)
                        HelmetBeamState.PerCharacter[pair.Key] = pair.Value;
                }
            }
            catch (Exception e)
            {
                EquipmentPlusPlugin.Log?.LogWarning(
                    $"Helmet-beam side-car read failed: {e.Message}");
                HelmetBeamSideCar.LoadedBeamMap = null;
            }
        }
    }
}
