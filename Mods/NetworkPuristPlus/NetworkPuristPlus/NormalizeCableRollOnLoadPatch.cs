using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using System;
using System.Collections.Generic;

namespace NetworkPuristPlus
{
    // Block 1 (on load): after a save finishes loading, re-roll every already-placed straight cable to
    // the canonical orientation for its run axis, so an existing world's mixed-roll cable runs become
    // consistent. Cosmetic only -- see CableRoll for why a straight cable's roll is connectivity-blind.
    // Host / single-player only (RunSimulation); a client receives the corrected rotations through the
    // normal world sync.
    //
    // Runs as a second postfix on World.OnLoadingFinished alongside ReplaceLongPiecesOnLoadPatch (the
    // long-piece rebuild). The two are order-independent: if this sweep runs first, the long rebuild's
    // single-tile cable replacements are canonicalised when they register (NormalizeCableRollOnRegisterPatch);
    // if it runs second, it re-checks them and finds them already canonical (the rebuild spawns them at
    // CableRoll.Canonical -- see ReplaceLongPiecesOnLoadPatch). IsNormalisableStraight skips long cables
    // and IsBeingDestroyed (the long rebuild's deferred-destroy targets), so this never touches a long
    // run mid-conversion.
    [HarmonyPatch(typeof(World), nameof(World.OnLoadingFinished))]
    internal static class NormalizeCableRollOnLoadPatch
    {
        private static void Postfix()
        {
            if (!Settings.CableAlignmentEnabled) return;             // master or cable-alignment toggle off
            if (!GameManager.RunSimulation) return;

            var targets = new List<Cable>();
            try
            {
                GridController.AllStructuresPool.ForEach(s =>
                {
                    if (s is Cable c && CableRoll.IsNormalisableStraight(c))
                        targets.Add(c);
                });
            }
            catch (Exception e)
            {
                NetworkPuristPlusPlugin.PlayerError($"could not scan placed cables: {e}");
                return;
            }
            if (targets.Count == 0) return;

            int changed = 0, failed = 0;
            foreach (Cable c in targets)
            {
                try { if (CableRoll.Normalise(c)) changed++; }
                catch (Exception e) { failed++; NetworkPuristPlusPlugin.PlayerWarn($"could not align cable (ref {(c != null ? c.ReferenceId : 0)}): {e}"); }
            }

            if (changed > 0 || failed > 0)
                NetworkPuristPlusPlugin.PlayerLog($"aligned {changed} straight cable(s) to a consistent orientation{(failed > 0 ? $" ({failed} failed -- see warnings above)" : "")}.");
        }
    }
}
