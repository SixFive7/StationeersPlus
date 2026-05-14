using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Serialization;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using JetBrains.Annotations;

namespace PowerTransmitterPlus
{
    // Side-car save/load for the per-dish auto-aim target cache.
    //
    // All four patch classes are gated on AutoAimPatched: when the player has
    // disabled the auto-aim feature at boot, no LogicType 6575 is registered,
    // no cache is maintained, and the side-car is neither written nor read.
    //
    // Mechanism mirrors SprayPaintPlus v1.6.0 GlowSideCar; see
    // Research/GameSystems/SaveZipExtension.md for the canonical reference
    // (private SaveHelper.Save worker, ZipOutputStream rebuild, async-task
    // wrap pattern, LoadHelper.ExtractToTemp pre-extraction, Thing.OnFinishedLoad
    // timing relative to DeserializeSave and IRotatable.OnClientStart).

    // Save path. Prefix snapshots cache on the main thread (the async save
    // body switches to a ThreadPool worker on its first await). Postfix
    // wraps the returned UniTask<SaveResult> with a continuation that writes
    // the side-car after the destination .save file is sealed and the temp
    // file is gone.
    //
    // Targets the PRIVATE Save(DirectoryInfo, string, bool, CancellationToken)
    // worker; every save path (Save, NewSave, QuickSave, AutoSave, SaveAs)
    // funnels through it via SaveGame -> Do*Save dispatch. The argument-type
    // array is required to disambiguate from the public (string,
    // CancellationToken) overload (HarmonyX raises AmbiguousMatchException at
    // PatchAll time without it, which would fail the entire mod's patches).
    [HarmonyPatch(typeof(SaveHelper), "Save",
        new[] { typeof(DirectoryInfo), typeof(string), typeof(bool), typeof(CancellationToken) })]
    public class SaveHelperSaveAutoAimSideCarPatch
    {
        [UsedImplicitly]
        public static bool Prepare() => PowerTransmitterPlusPlugin.AutoAimPatched;

        [UsedImplicitly]
        public static void Prefix()
        {
            AutoAimSideCar.PendingSaveSnapshot = AutoAimSideCar.Snapshot();
        }

        [UsedImplicitly]
        public static void Postfix(
            DirectoryInfo saveDirectory,
            string saveFileName,
            ref UniTask<SaveResult> __result)
        {
            var originalTask = __result;
            var snapshot = AutoAimSideCar.PendingSaveSnapshot;
            AutoAimSideCar.PendingSaveSnapshot = null;

            if (saveDirectory == null || string.IsNullOrEmpty(saveFileName) || snapshot == null)
                return;

            var path = Path.Combine(saveDirectory.FullName, saveFileName);
            __result = WriteSideCarAfterSave(originalTask, path, snapshot);
        }

        private static async UniTask<SaveResult> WriteSideCarAfterSave(
            UniTask<SaveResult> saveTask, string path, AutoAimSideCarData snapshot)
        {
            var result = await saveTask;
            if (!result.Success) return result;
            try
            {
                AutoAimSideCar.WriteSideCar(path, snapshot);
            }
            catch (Exception e)
            {
                PowerTransmitterPlusPlugin.Log?.LogWarning(
                    $"Auto-aim side-car write failed for {path}: {e.Message}");
            }
            return result;
        }
    }

    // Load path. Fires after XmlSaveLoad.LoadWorld finishes deserializing
    // world.xml and BEFORE GameManager.UpdateThingsOnGameStartAction calls
    // Thing.OnFinishedLoad on every Thing. Populates AutoAimSideCar.LoadedTargets
    // for the per-Thing postfix below.
    //
    // LoadHelper.ExtractToTemp pre-extracts every ZIP entry (known and unknown)
    // to a temp directory before LoadWorld runs, so we read the side-car as a
    // loose file at <tempDir>/pwrxmplus-autoaim.xml; do NOT attempt to
    // re-open the (already-closed) save ZIP.
    [HarmonyPatch(typeof(XmlSaveLoad), nameof(XmlSaveLoad.LoadWorld))]
    public class XmlSaveLoadLoadWorldAutoAimSideCarPatch
    {
        [UsedImplicitly]
        public static bool Prepare() => PowerTransmitterPlusPlugin.AutoAimPatched;

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
                AutoAimSideCar.LoadedTargets = AutoAimSideCar.ReadSideCarFromDir(tempDir);
            }
            catch (Exception e)
            {
                PowerTransmitterPlusPlugin.Log?.LogWarning(
                    $"Auto-aim side-car read failed: {e.Message}");
                AutoAimSideCar.LoadedTargets = null;
            }
        }
    }

    // Per-Thing cache restore. Runs in Thing.OnFinishedLoad postfix, which
    // fires after Thing.DeserializeSave (where the RotatableBehaviour
    // TargetHorizontal/TargetVertical setters restore the saved aim and our
    // reset postfixes call ClearCache on a still-empty cache, a harmless
    // no-op) and after IRotatable.OnClientStart syncs Horizontal/Vertical to
    // those targets without going through the RotatableBehaviour setters
    // again. By this point the dish is at its final pose; populating the
    // cache here puts MicrowaveAutoAimTarget back to the value it had before
    // the save.
    //
    // Hosts and single-player both run this restore. Joining clients receive
    // the live cache via AutoAimSnapshotMessage on PlayerConnected instead.
    [HarmonyPatch(typeof(Thing), nameof(Thing.OnFinishedLoad))]
    public class ThingOnFinishedLoadAutoAimPatch
    {
        [UsedImplicitly]
        public static bool Prepare() => PowerTransmitterPlusPlugin.AutoAimPatched;

        [UsedImplicitly]
        public static void Postfix(Thing __instance)
        {
            var cache = AutoAimSideCar.LoadedTargets;
            if (cache == null || __instance == null) return;
            if (!(__instance is WirelessPower dish)) return;
            if (!cache.TryGetValue(dish.ReferenceId, out var targetId)) return;
            if (targetId == 0L) return;
            AutoAimState.RestoreCache(dish, targetId);
        }
    }

    // Post-load auto-aim re-solve pass. Fires after every Thing's
    // OnFinishedLoad has run (see Research/GameClasses/GameManager.md "Load
    // finalize chain"). At that point all dishes have their saved poses
    // restored and the auto-aim cache has been repopulated by the per-Thing
    // RestoreCache postfix above. We walk every cached entry and recompute
    // (H, V) under the current solver. The pass serves three purposes:
    //
    //   - Re-applies the current solver to every cached pair, so a save made
    //     under an older solver picks up improvements without player action.
    //   - Repairs aim after save-file edits that moved dish positions.
    //   - Prunes cache entries whose target ReferenceId no longer resolves to
    //     a Thing, as if the user had cleared MicrowaveAutoAimTarget on that
    //     dish. Dangling targets do not accumulate across loads.
    //
    // Hooks GameManager.UpdateThingsOnGameStart rather than the async StartGame
    // body to keep Postfix semantics unambiguous (UpdateThingsOnGameStart is
    // synchronous, void). The cleared/recomputed values flow to clients via
    // the existing per-tick AutoAimUpdateFlag (0x2000) and TargetH/V (256)
    // deltas, so this pass is host-side only.
    //
    // Joining clients receive authoritative cache values from
    // IJoinSuffixSerializer.DeserializeJoinSuffix and the host's already-
    // resolved H/V from the live tick stream; no client-side pass is needed.
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.UpdateThingsOnGameStart))]
    public class GameManagerUpdateThingsOnGameStartAutoAimResolvePatch
    {
        [UsedImplicitly]
        public static bool Prepare() => PowerTransmitterPlusPlugin.AutoAimPatched;

        [UsedImplicitly]
        public static void Postfix()
        {
            try
            {
                // Run only on the authoritative side: single-player, multiplayer host,
                // or dedicated server. Pure remote clients receive cache and
                // aim from the host via IJoinSuffixSerializer + tick deltas.
                // Per Research/GameSystems/NetworkRoles.md the remote-client
                // case is uniquely (IsActive && !IsServer); negating that
                // covers every other scenario including single-player (where
                // IsActive is false at this hook point because NetworkServer
                // .Host() has not run yet).
                if (Assets.Scripts.Networking.NetworkManager.IsActive
                    && !Assets.Scripts.Networking.NetworkManager.IsServer)
                {
                    return;
                }

                // Materialise the snapshot before mutating; ResolveCachedTarget
                // calls SetCache which mutates _tracked through ResolveCachedTarget's
                // SetCache(0) branch on stale targets.
                var entries = new List<KeyValuePair<long, long>>();
                foreach (var pair in AutoAimState.SnapshotEntries()) entries.Add(pair);

                int resolved = 0, cleared = 0;
                foreach (var entry in entries)
                {
                    var dish = Thing.Find(entry.Key) as WirelessPower;
                    if (dish == null) continue;
                    long before = AutoAimState.GetCachedTarget(dish);
                    AutoAimState.ResolveCachedTarget(dish, entry.Value);
                    long after = AutoAimState.GetCachedTarget(dish);
                    if (after == 0L && before != 0L) cleared++;
                    else if (after != 0L) resolved++;
                }

                if (entries.Count > 0)
                {
                    PowerTransmitterPlusPlugin.Log?.LogInfo(
                        $"Auto-aim post-load: {resolved} re-solved, {cleared} stale target(s) cleared (out of {entries.Count})");
                }
            }
            catch (Exception e)
            {
                PowerTransmitterPlusPlugin.Log?.LogWarning(
                    $"Auto-aim post-load pass failed: {e.Message}");
            }
        }
    }
}
