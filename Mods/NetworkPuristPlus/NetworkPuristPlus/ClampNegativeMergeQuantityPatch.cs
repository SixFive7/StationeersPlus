using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using HarmonyLib;
using UnityEngine;

namespace NetworkPuristPlus
{
    // Crash guard for the ZoopMod + base-game "merge onto a long straight variant" interaction.
    //
    // When MultiMergeConstructor.Construct merges a new piece onto an existing one, it charges the
    // material difference: quantity = EntryQuantity(merged result) - EntryQuantity(existing piece),
    // passed as the last argument of the 7-arg MultiConstructor.Construct, which feeds it to
    // Stackable.OnUseItem. If the existing piece is one of the "long" straight variants
    // (StraightAsymmetric, cost 3/4/8) and the merged result is a cheaper single-tile straight (cost 1),
    // that difference is negative (e.g. 1 - 3 = -2). Stackable.OnUseItem then calls List.RemoveRange with
    // a negative count and throws ArgumentOutOfRangeException, which aborts the build.
    //
    // The game normally never reaches this: the placement cursor's CanReplace / _IsCollision validation
    // forbids merging onto a long variant (they carry DontAllowMergingWithWrench /
    // BlockMergeWithOtherCables). ZoopMod places through InventoryManager.UsePrimaryComplete, which skips
    // that validation, so a drag-build can drive the forbidden merge and hit the crash. Reported upstream
    // at github.com/Nivvdiy/ZoopModRecovered (issue 22); this is the local safety net.
    //
    // The guard clamps a negative quantity to 0 before the original runs, so OnUseItem never sees a
    // negative and never calls RemoveRange with a negative count. A normal build always passes a positive
    // quantity (the piece cost), so the clamp only ever fires on the would-crash merge and leaves ordinary
    // construction untouched. It runs whenever the mod is enabled, independent of the long-piece family
    // toggles, so it protects a player who has those turned off (long pieces still present is exactly the
    // case that crashes). Construction is host-authoritative, so this runs where the merge executes; a pure
    // client never reaches this path. The clamped merge consumes nothing from the kit on that one piece
    // (the few items the game would otherwise have refunded for the cheaper result are not refunded), which
    // is harmless next to the crash it replaces.
    //
    // Same target method as RewriteCableRollOnMultiConstruct (the cable-roll prefix). Harmony runs both
    // prefixes; one rewrites targetRotation, this one clamps quantity, and they do not interact.
    [HarmonyPatch(typeof(MultiConstructor), nameof(MultiConstructor.Construct),
        new[] { typeof(Grid3), typeof(Quaternion), typeof(int), typeof(Item), typeof(bool), typeof(ulong), typeof(int) })]
    internal static class ClampNegativeMergeQuantityPatch
    {
        private static int _clampCount;

        private static void Prefix(ref int quantity)
        {
            if (!Settings.MasterEnabled) return;
            if (quantity >= 0) return;

            int original = quantity;
            quantity = 0;

            _clampCount++;
            if (_clampCount == 1)
                NetworkPuristPlusPlugin.PlayerLog(
                    $"prevented a construction crash: a merge tried to consume a negative item quantity ({original}); clamped to 0 and the build continued. " +
                    "This is the ZoopMod + base-game long-variant merge interaction (reported at github.com/Nivvdiy/ZoopModRecovered issue 22).");
            else
                NetworkPuristPlusPlugin.Log?.LogInfo($"clamped a negative construct quantity ({original}) to 0 (occurrence {_clampCount}).");
        }
    }
}
