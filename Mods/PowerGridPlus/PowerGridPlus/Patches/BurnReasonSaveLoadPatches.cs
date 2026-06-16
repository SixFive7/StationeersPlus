using System;
using System.IO;
using System.Threading;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Serialization;
using Cysharp.Threading.Tasks;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    // Save / load plumbing for the per-wreckage burn-reason side-car (POWERTODO 0.3). Mirrors
    // PassthroughSaveLoadPatches; canonical pattern in Research/GameSystems/SaveZipExtension.md.

    // Save path. Prefix snapshots the in-memory store on the main thread (the async save body
    // switches to a ThreadPool worker on its first await). Postfix wraps the returned
    // UniTask<SaveResult> with a continuation that writes the side-car after the .save file seals.
    // The argument-type array disambiguates the private overload from the public
    // Save(string, CancellationToken) (HarmonyX would raise AmbiguousMatchException otherwise).
    [HarmonyPatch(typeof(SaveHelper), "Save",
        new[] { typeof(DirectoryInfo), typeof(string), typeof(bool), typeof(CancellationToken) })]
    public class SaveHelperSaveBurnReasonSideCarPatch
    {
        public static void Prefix()
        {
            BurnReasonSideCar.PendingSaveSnapshot = BurnReasonSideCar.Snapshot();
        }

        public static void Postfix(
            DirectoryInfo saveDirectory,
            string saveFileName,
            ref UniTask<SaveResult> __result)
        {
            var originalTask = __result;
            var snapshot = BurnReasonSideCar.PendingSaveSnapshot;
            BurnReasonSideCar.PendingSaveSnapshot = null;

            if (saveDirectory == null || string.IsNullOrEmpty(saveFileName) || snapshot == null)
                return;

            var path = Path.Combine(saveDirectory.FullName, saveFileName);
            __result = WriteSideCarAfterSave(originalTask, path, snapshot);
        }

        private static async UniTask<SaveResult> WriteSideCarAfterSave(
            UniTask<SaveResult> saveTask, string path, BurnReasonSideCarData snapshot)
        {
            var result = await saveTask;
            if (!result.Success) return result;
            try
            {
                BurnReasonSideCar.WriteSideCar(path, snapshot);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"Burn-reason side-car write failed for {path}: {e.Message}");
            }
            return result;
        }
    }

    // Load path. LoadHelper.ExtractToTemp pre-extracts every ZIP entry to a temp directory, so the
    // side-car reads as a loose file next to world.xml.
    [HarmonyPatch(typeof(XmlSaveLoad), nameof(XmlSaveLoad.LoadWorld))]
    public class XmlSaveLoadLoadWorldBurnReasonSideCarPatch
    {
        public static void Postfix()
        {
            try
            {
                var save = XmlSaveLoad.Instance?.CurrentWorldSave;
                var worldPath = save?.World?.FullName;
                var tempDir = string.IsNullOrEmpty(worldPath) ? null : Path.GetDirectoryName(worldPath);
                BurnReasonSideCar.LoadedReasons = BurnReasonSideCar.ReadSideCarFromDir(tempDir);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"Burn-reason side-car read failed: {e.Message}");
                BurnReasonSideCar.LoadedReasons = null;
            }
        }
    }

    // Per-Thing restore: re-attach the persisted reason to reloaded CableRuptured wreckage.
    [HarmonyPatch(typeof(Thing), nameof(Thing.OnFinishedLoad))]
    public class ThingOnFinishedLoadBurnReasonPatch
    {
        public static void Postfix(Thing __instance)
        {
            if (!(__instance is CableRuptured)) return;
            var cache = BurnReasonSideCar.LoadedReasons;
            if (cache == null) return;
            if (!cache.TryGetValue(__instance.ReferenceId, out var reason)) return;
            BurnReasonRegistry.RestoreFromSideCar(__instance, reason);
        }
    }
}
