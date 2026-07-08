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
    // Save / load plumbing for the per-Transformer Priority side-car. Parallel
    // to PassthroughSaveLoadPatches: same pattern, separate side-car file.

    // Save path. Prefix snapshots the in-memory store on the main thread (the
    // async save body switches to a ThreadPool worker on its first await).
    // Postfix wraps the returned UniTask<SaveResult> with a continuation that
    // writes the side-car after the destination .save file is sealed.
    [HarmonyPatch(typeof(SaveHelper), "Save",
        new[] { typeof(DirectoryInfo), typeof(string), typeof(bool), typeof(CancellationToken) })]
    public class SaveHelperSavePrioritySideCarPatch
    {
        public static void Prefix()
        {
            PrioritySideCar.PendingSaveSnapshot = PrioritySideCar.Snapshot();
        }

        public static void Postfix(
            DirectoryInfo saveDirectory,
            string saveFileName,
            ref UniTask<SaveResult> __result)
        {
            var originalTask = __result;
            var snapshot = PrioritySideCar.PendingSaveSnapshot;
            PrioritySideCar.PendingSaveSnapshot = null;

            if (saveDirectory == null || string.IsNullOrEmpty(saveFileName) || snapshot == null)
                return;

            var path = Path.Combine(saveDirectory.FullName, saveFileName);
            __result = WriteSideCarAfterSave(originalTask, path, snapshot);
        }

        private static async UniTask<SaveResult> WriteSideCarAfterSave(
            UniTask<SaveResult> saveTask, string path, PrioritySideCarData snapshot)
        {
            var result = await saveTask;
            if (!result.Success) return result;
            try
            {
                PrioritySideCar.WriteSideCar(path, snapshot);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"Priority side-car write failed for {path}: {e.Message}");
            }
            return result;
        }
    }

    // Load path. Postfix XmlSaveLoad.LoadWorld extracts the pre-extracted loose
    // file at <tempDir>/pwrgridplus-priority.xml.
    [HarmonyPatch(typeof(XmlSaveLoad), nameof(XmlSaveLoad.LoadWorld))]
    public class XmlSaveLoadLoadWorldPrioritySideCarPatch
    {
        public static void Postfix()
        {
            try
            {
                var save = XmlSaveLoad.Instance?.CurrentWorldSave;
                var worldPath = save?.World?.FullName;
                var tempDir = string.IsNullOrEmpty(worldPath) ? null : Path.GetDirectoryName(worldPath);
                PrioritySideCar.LoadedPriorities = PrioritySideCar.ReadSideCarFromDir(tempDir);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"Priority side-car read failed: {e.Message}");
                PrioritySideCar.LoadedPriorities = null;
            }
        }
    }

    // Per-Thing restore. Fires in Thing.OnFinishedLoad postfix for transformers
    // that have a side-car entry. Transformers without an entry (legacy saves,
    // fresh placements) fall through to PriorityStore.DefaultPriority on next
    // read.
    [HarmonyPatch(typeof(Thing), nameof(Thing.OnFinishedLoad))]
    public class ThingOnFinishedLoadPriorityPatch
    {
        public static void Postfix(Thing __instance)
        {
            if (__instance == null) return;
            if (!(__instance is Transformer transformer)) return;
            var cache = PrioritySideCar.LoadedPriorities;
            if (cache == null) return;
            if (!cache.TryGetValue(transformer.ReferenceId, out var savedPriority)) return;

            PriorityStore.RestoreFromSideCar(transformer.ReferenceId, savedPriority);
            // Save/load self-check clause 2: every loaded sidecar entry must land in a restore.
            SaveLoadSelfCheck.NotePriorityRestored();
        }
    }
}
