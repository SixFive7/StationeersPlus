using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using System;

namespace NetworkPuristPlus
{
    // Block 2 (just-in-time): the moment a freshly built straight cable registers on the grid, re-roll it
    // to the canonical orientation. This catches every new placement -- a plain click build, an R-rotated
    // build, a ZoopMod drag, a BlueprintMod paste, and the single-tile replacements the long-piece rebuild
    // and RewriteLongVariantOnConstructPatch spawn -- because they all reach Cable.OnRegistered. The mods
    // keep working unchanged; they just produce a canonical cable. (Practical consequence: the R-key roll on
    // a cable becomes preview-only -- the placed piece always snaps to the canonical roll. For a network
    // "purist" that is the point.)
    //
    // Cable.OnRegistered runs its network-merge body only when GameState != Loading && RunSimulation, so we
    // use the same gate here: during save deserialisation (GameState == Loading) we do nothing and let the
    // on-load sweep (NormalizeCableRollOnLoadPatch) handle the loaded cables; World.OnLoadingFinished -- and
    // therefore the long-piece rebuild that runs in its postfix -- fires after GameState has flipped to
    // Running, so rebuilt single-tile cables registered there DO pass through this postfix.
    //
    // No per-cable log here (it would spam on every normal load, where every cable registers); the on-load
    // sweep does the summary logging.
    //
    // CableRuptured derives from SmallGrid, not Cable, so burnt/ruptured cables do not hit this patch; they
    // are picked up by the on-load sweep, which iterates every Cable instance.
    [HarmonyPatch(typeof(Cable), nameof(Cable.OnRegistered))]
    internal static class NormalizeCableRollOnRegisterPatch
    {
        private static void Postfix(Cable __instance)
        {
            if (!Settings.CableAlignmentEnabled) return;             // master or cable-alignment toggle off
            if (!GameManager.RunSimulation || GameManager.GameState == GameState.Loading) return;
            try
            {
                CableRoll.Normalise(__instance);
            }
            catch (Exception e)
            {
                NetworkPuristPlusPlugin.PlayerWarn($"could not align a just-built cable: {e}");
            }
        }
    }
}
