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
    // Save / load plumbing for the per-Transformer LogicPassthroughMode side-car.
    // Mirrors PowerTransmitterPlus's AutoAimSaveLoadPatches; see
    // Research/GameSystems/SaveZipExtension.md for the canonical pattern.

    // Save path. Prefix snapshots the in-memory store on the main thread (the
    // async save body switches to a ThreadPool worker on its first await).
    // Postfix wraps the returned UniTask<SaveResult> with a continuation that
    // writes the side-car after the destination .save file is sealed.
    [HarmonyPatch(typeof(SaveHelper), "Save",
        new[] { typeof(DirectoryInfo), typeof(string), typeof(bool), typeof(CancellationToken) })]
    public class SaveHelperSavePassthroughSideCarPatch
    {
        public static void Prefix()
        {
            PassthroughSideCar.PendingSaveSnapshot = PassthroughSideCar.Snapshot();
        }

        public static void Postfix(
            DirectoryInfo saveDirectory,
            string saveFileName,
            ref UniTask<SaveResult> __result)
        {
            var originalTask = __result;
            var snapshot = PassthroughSideCar.PendingSaveSnapshot;
            PassthroughSideCar.PendingSaveSnapshot = null;

            if (saveDirectory == null || string.IsNullOrEmpty(saveFileName) || snapshot == null)
                return;

            var path = Path.Combine(saveDirectory.FullName, saveFileName);
            __result = WriteSideCarAfterSave(originalTask, path, snapshot);
        }

        private static async UniTask<SaveResult> WriteSideCarAfterSave(
            UniTask<SaveResult> saveTask, string path, PassthroughSideCarData snapshot)
        {
            var result = await saveTask;
            if (!result.Success) return result;
            try
            {
                PassthroughSideCar.WriteSideCar(path, snapshot);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"Passthrough side-car write failed for {path}: {e.Message}");
            }
            return result;
        }
    }

    // Load path. Fires after XmlSaveLoad.LoadWorld deserializes world.xml and
    // before Thing.OnFinishedLoad runs on every Thing. LoadHelper.ExtractToTemp
    // pre-extracts every ZIP entry to a temp directory, so we read the side-car
    // as a loose file at <tempDir>/pwrgridplus-passthrough.xml.
    [HarmonyPatch(typeof(XmlSaveLoad), nameof(XmlSaveLoad.LoadWorld))]
    public class XmlSaveLoadLoadWorldPassthroughSideCarPatch
    {
        public static void Postfix()
        {
            try
            {
                var save = XmlSaveLoad.Instance?.CurrentWorldSave;
                var worldPath = save?.World?.FullName;
                var tempDir = string.IsNullOrEmpty(worldPath) ? null : Path.GetDirectoryName(worldPath);
                PassthroughSideCar.LoadedModes = PassthroughSideCar.ReadSideCarFromDir(tempDir);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"Passthrough side-car read failed: {e.Message}");
                PassthroughSideCar.LoadedModes = null;
            }
        }
    }

    // Per-Thing restore. Fires in Thing.OnFinishedLoad postfix. For each supported
    // bridging device (Transformer, Battery, AreaPowerControl, PowerTransmitter,
    // PowerReceiver) with a side-car entry, restore its saved mode and dirty the
    // affected networks so the cached data-device lists rebuild with the restored
    // value on next read.
    // Devices without a side-car entry (legacy saves, fresh placements) fall through
    // to PassthroughModeStore.GetDefaultMode via GetMode on next read.
    [HarmonyPatch(typeof(Thing), nameof(Thing.OnFinishedLoad))]
    public class ThingOnFinishedLoadPassthroughPatch
    {
        public static void Postfix(Thing __instance)
        {
            if (__instance == null) return;
            var cache = PassthroughSideCar.LoadedModes;
            if (cache == null) return;
            if (!cache.TryGetValue(__instance.ReferenceId, out var savedMode)) return;

            // Only restore for supported bridging types. Anything else with a stray sidecar
            // entry would be a legacy file; ignore safely.
            switch (__instance)
            {
                case Transformer transformer:
                    PassthroughModeStore.RestoreFromSideCar(transformer.ReferenceId, savedMode);
                    // See Research/GameClasses/CableNetwork.md "Field shape and accessor quirk".
                    transformer.InputNetwork?.DirtyPowerAndDataDeviceLists();
                    transformer.OutputNetwork?.DirtyPowerAndDataDeviceLists();
                    break;
                case Battery battery:
                    PassthroughModeStore.RestoreFromSideCar(battery.ReferenceId, savedMode);
                    battery.InputNetwork?.DirtyPowerAndDataDeviceLists();
                    battery.OutputNetwork?.DirtyPowerAndDataDeviceLists();
                    break;
                case AreaPowerControl apc:
                    PassthroughModeStore.RestoreFromSideCar(apc.ReferenceId, savedMode);
                    apc.InputNetwork?.DirtyPowerAndDataDeviceLists();
                    apc.OutputNetwork?.DirtyPowerAndDataDeviceLists();
                    break;
                case PowerTransmitter tx:
                    PassthroughModeStore.RestoreFromSideCar(tx.ReferenceId, savedMode);
                    tx.InputNetwork?.DirtyPowerAndDataDeviceLists();
                    tx.LinkedReceiver?.OutputNetwork?.DirtyPowerAndDataDeviceLists();
                    break;
                case PowerReceiver rx:
                    PassthroughModeStore.RestoreFromSideCar(rx.ReferenceId, savedMode);
                    rx.OutputNetwork?.DirtyPowerAndDataDeviceLists();
                    rx.LinkedPowerTransmitter?.InputNetwork?.DirtyPowerAndDataDeviceLists();
                    break;
            }
        }
    }
}
