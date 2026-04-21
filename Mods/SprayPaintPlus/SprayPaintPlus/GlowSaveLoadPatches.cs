using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Assets.Scripts.Objects;
using Assets.Scripts.Serialization;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using JetBrains.Annotations;

namespace SprayPaintPlus
{
    // Save/load persistence for per-Thing glow state.
    //
    // v1.6.0+ uses a side-car file `sprayplus-glow.xml` inside the save ZIP,
    // written in the SaveHelper.Save postfix and read in the
    // XmlSaveLoad.LoadWorld postfix. This keeps world.xml 100% vanilla:
    // removing SprayPaintPlus from an existing save never breaks the load.
    // See Research/GameSystems/SaveZipExtension.md and
    // Research/GameSystems/UnregisteredSaveDataBehavior.md for the mechanism
    // and failure mode this avoids.
    //
    // The back-compat deserialize postfix below continues to read any
    // `xsi:type="GlowThingSaveData"` entries left behind by v1.4.x-v1.5.x
    // saves. On the next save, the side-car writer owns persistence and
    // world.xml is rewritten without the xsi:type attribute, so the old
    // entries migrate away naturally. GlowThingSaveData itself remains
    // registered in XmlSaveLoad.ExtraTypes (see Plugin.cs) so old saves do
    // not fail the load while the mod is still installed.

    // Side-car write path. Prefix captures a main-thread snapshot of glowing
    // ReferenceIds; the Postfix wraps the returned UniTask<SaveResult> with
    // a continuation that writes the side-car after the archive has been
    // sealed and moved to its final location. SaveHelper.Save's body runs on
    // a ThreadPool worker (await UniTask.SwitchToThreadPool), but we
    // snapshot on the main thread to avoid racing gameplay mutations of
    // GlowPaintHelpers.GlowingThingIds.
    //
    // Targets the PRIVATE Save(DirectoryInfo, string, bool, CancellationToken)
    // worker, not the public Save(string, CancellationToken) entry. Every
    // save path (New, Save, QuickSave, AutoSave, SaveAs) funnels through the
    // private worker via SaveGame -> DoSave/DoNewSave/RollingSave/DoSaveAs.
    // The name "Save" is overloaded, so the argument-type array below is
    // required to disambiguate (HarmonyX otherwise raises
    // AmbiguousMatchException and no patches in this assembly apply).
    [HarmonyPatch(typeof(SaveHelper), "Save",
        new[] { typeof(DirectoryInfo), typeof(string), typeof(bool), typeof(CancellationToken) })]
    public class SaveHelperSaveSideCarPatch
    {
        [UsedImplicitly]
        public static void Prefix()
        {
            GlowSideCar.PendingSaveSnapshot = GlowSideCar.SnapshotGlowingIds();
        }

        [UsedImplicitly]
        public static void Postfix(
            DirectoryInfo saveDirectory,
            string saveFileName,
            ref UniTask<SaveResult> __result)
        {
            var originalTask = __result;
            var snapshot = GlowSideCar.PendingSaveSnapshot;
            GlowSideCar.PendingSaveSnapshot = null;

            if (saveDirectory == null || string.IsNullOrEmpty(saveFileName) || snapshot == null)
                return;

            var path = Path.Combine(saveDirectory.FullName, saveFileName);
            __result = WriteSideCarAfterSave(originalTask, path, snapshot);
        }

        private static async UniTask<SaveResult> WriteSideCarAfterSave(
            UniTask<SaveResult> saveTask, string path, List<long> snapshot)
        {
            var result = await saveTask;
            if (!result.Success) return result;

            try
            {
                GlowSideCar.WriteSideCar(path, snapshot);
            }
            catch (Exception e)
            {
                SprayPaintPlusPlugin.Log.LogWarning(
                    $"Glow side-car write failed for {path}: {e.Message}");
            }
            return result;
        }
    }

    // Side-car read path. Fires after XmlSaveLoad.LoadWorld has finished
    // deserializing world.xml but before GameManager.UpdateThingsOnGameStart
    // invokes Thing.OnFinishedLoad on every Thing. Populates
    // GlowSideCar.LoadedGlowIds for the per-Thing postfix below.
    //
    // LoadHelper.ExtractToTemp iterates every ZIP entry (known and unknown)
    // into a temp directory before LoadWorld runs; CurrentWorldSave.World is
    // a FileInfo pointing at <tempDir>/world.xml. Our sprayplus-glow.xml
    // entry is therefore a loose file in the same temp directory, and we
    // read it directly instead of re-opening the now-closed save ZIP.
    [HarmonyPatch(typeof(XmlSaveLoad), nameof(XmlSaveLoad.LoadWorld))]
    public class XmlSaveLoadLoadWorldSideCarPatch
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
                GlowSideCar.LoadedGlowIds = GlowSideCar.ReadSideCarFromDir(tempDir);
            }
            catch (Exception e)
            {
                SprayPaintPlusPlugin.Log.LogWarning(
                    $"Glow side-car read failed: {e.Message}");
                GlowSideCar.LoadedGlowIds = null;
            }
        }
    }

    // Per-Thing glow re-apply. Runs after DeserializeSave, atmosphere load,
    // and device init; safe for Thing.SetCustomColor per
    // Research/GameSystems/SaveZipExtension.md "Thing.OnFinishedLoad timing
    // and caller." Consumes the set populated by the LoadWorld postfix.
    [HarmonyPatch(typeof(Thing), nameof(Thing.OnFinishedLoad))]
    public class ThingOnFinishedLoadGlowPatch
    {
        [UsedImplicitly]
        public static void Postfix(Thing __instance)
        {
            var cache = GlowSideCar.LoadedGlowIds;
            if (cache == null || __instance == null) return;
            if (!cache.Contains(__instance.ReferenceId)) return;

            GlowPaintHelpers.SetGlow(__instance, true);
            GlowPaintHelpers.ReapplyEmissive(__instance, true);
        }
    }

    // Back-compat: v1.4.x-v1.5.x saves contain
    // `<ThingSaveData xsi:type="GlowThingSaveData">...` entries. Deserialize
    // those, re-apply glow. Plugin.cs keeps GlowThingSaveData in ExtraTypes
    // so the XmlSerializer accepts the old entries; once the user re-saves,
    // the side-car writer owns persistence and world.xml is written back
    // clean.
    [HarmonyPatch(typeof(Thing), nameof(Thing.DeserializeSave))]
    public class ThingDeserializeSaveGlowPatch
    {
        [UsedImplicitly]
        public static void Postfix(Thing __instance, ThingSaveData saveData)
        {
            if (__instance == null) return;
            if (!(saveData is GlowThingSaveData sd)) return;
            if (!sd.IsGlowing) return;

            GlowPaintHelpers.SetGlow(__instance, true);
            GlowPaintHelpers.ReapplyEmissive(__instance, true);
        }
    }
}
