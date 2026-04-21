using System.Collections.Generic;
using Assets.Scripts.Objects;

namespace SprayPaintPlus
{
    // Mode of the current glow-paint event. Set by `SprayGunGlowPatch`
    // around its OnServer.SetCustomColor call; read by the per-Thing
    // SetCustomColor prefix/postfix to decide whether to touch glow state
    // and in which direction.
    //
    // Idle: no gun paint event in progress. Covers bare-can paints, UI
    // color picker, save-load restore, color-sync receive, etc. These paths
    // never touch the IsGlowing flag.
    internal enum GlowApplyMode
    {
        Idle,
        AddGlow,
        RemoveGlow,
    }

    internal static class GlowPaintHelpers
    {
        // Thing.NetworkUpdateFlags bit 13 (0x2000, GenericFlag3). Free per
        // Research/GameSystems/NetworkUpdateFlags.md.
        internal const ushort GlowNetworkFlag = 0x2000;

        // Active mode for the current gun-paint event. Default Idle means
        // "no gun paint running right now." Set in SprayGunGlowPatch, read
        // by Thing.SetCustomColor patches. Main-thread only; no locks.
        internal static GlowApplyMode CurrentMode = GlowApplyMode.Idle;

        // Reentrancy guard for our own re-invocation of SetCustomColor with
        // emissive: true. Without it, the inner call would fire our postfix
        // again and recurse indefinitely.
        internal static bool Reapplying;

        // Per-Thing glow state, keyed by Thing.ReferenceId. Mutated only by
        // the gun's Thing.SetCustomColor postfix (AddGlow sets true,
        // RemoveGlow sets false). Persisted via GlowThingSaveData, synced via
        // GlowNetworkFlag bit. Cleared on Thing.OnDestroy.
        internal static readonly Dictionary<long, bool> GlowingThingIds =
            new Dictionary<long, bool>();

        internal static bool IsGlowing(Thing thing)
        {
            if (thing == null) return false;
            return GlowingThingIds.TryGetValue(thing.ReferenceId, out bool v) && v;
        }

        internal static void SetGlow(Thing thing, bool glowing)
        {
            if (thing == null) return;
            if (glowing)
                GlowingThingIds[thing.ReferenceId] = true;
            else
                GlowingThingIds.Remove(thing.ReferenceId);
        }

        // Re-invokes SetCustomColor behind the reentrancy guard. Safe to call
        // from any patch (glow postfix, network sync, save-load hook) to
        // force a material swap matching the requested emissive state.
        internal static void ReapplyEmissive(Thing thing, bool emissive)
        {
            if (thing == null || thing.CustomColor == null) return;
            Reapplying = true;
            try
            {
                thing.SetCustomColor(thing.CustomColor.Index, emissive);
            }
            finally
            {
                Reapplying = false;
            }
        }
    }
}
