using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using System;
using UnityEngine;

namespace NetworkPuristPlus
{
    // Block 2 (canonical-at-birth): canonicalise a freshly built straight cable's roll BEFORE the cable is
    // created, by rewriting the targetRotation argument of the Construct entry methods. Set before
    // Thing.Create, the canonical rotation is snapshotted into RegisteredRotation during registration and
    // shipped to clients verbatim by the new-Thing replication (the client renders RegisteredRotation). So the
    // cable is born canonical AND renders canonical on every multiplayer client. Runtime-validated on a
    // dedicated server (a remote client built normally, with the rotate key, with ZoopMod, and via the
    // merge-with-tool, and every result came out canonical).
    //
    // WHY MultiConstructor.Construct(7-arg) is the hook:
    //   - Thing.Create<T> is generic; HarmonyX cannot patch it.
    //   - Constructor.SpawnConstruct is tiny and Mono inlines it, so a prefix there never fires (confirmed:
    //     0 prefix entries across 76 SpawnConstruct calls during an on-load rebuild).
    //   - Structure.SetStructureData writes only the network-sync-only Direction, not the visible rotation.
    //   - A Cable.OnRegistered postfix re-rolls AFTER creation and does not replicate to a freshly built cable
    //     on a multiplayer client.
    // The 7-arg MultiConstructor.Construct(localPosition, targetRotation, optionIndex, offhand, authoring,
    // steamId, quantity) is the convergence point that actually builds the CreateStructureInstance from
    // Constructables[optionIndex]. For the cable coil (a MultiMergeConstructor) BOTH branches reach it: the
    // plain-build branch via base.Construct, and the merge-with-tool branch via base.Construct(7-arg) carrying
    // the merge-recomputed rotation. So canonicalising targetRotation here covers plain placement, ZoopMod, and
    // merges. It is large enough that Mono does not inline it, so the prefix fires (the lesson from the inlined
    // SpawnConstruct). It runs host-side for host builds and for the remote-client message handler
    // (CreateStructureMessage.Process re-runs Construct on the host). See
    // Research/GameSystems/PlacementOrientation.md ("Build-time structure-creation chokepoints").
    //
    // Coverage by design: a BlueprintMod paste uses OnServer.Create<Thing> directly (bypasses Construct), so a
    // pasted cable aligns on the next world load via the on-load sweep (NormalizeCableRollOnLoadPatch), the
    // universal backstop that also realigns already-placed cables. Long variants are stripped from the kits, so
    // optionIndex never selects one. A merge result that is a corner/tee (not a straight) is left untouched by
    // the IsStraight gate, as it must be.

    // The cable coil and every other build kit: MultiConstructor.Construct(7-arg) -- the overload that builds
    // the instance from Constructables[optionIndex] with the final rotation.
    [HarmonyPatch(typeof(MultiConstructor), nameof(MultiConstructor.Construct),
        new[] { typeof(Grid3), typeof(Quaternion), typeof(int), typeof(Item), typeof(bool), typeof(ulong), typeof(int) })]
    internal static class RewriteCableRollOnMultiConstruct
    {
        private static bool _loggedActive;
        private static void Prefix(MultiConstructor __instance, ref Quaternion targetRotation, int optionIndex)
        {
            if (!Settings.CableAlignmentEnabled) return;
            var options = __instance?.Constructables;
            if (options == null || optionIndex < 0 || optionIndex >= options.Count) return;
            if (!(options[optionIndex] is Cable cable) || !cable.IsStraight) return;
            if (LongVariantRegistry.LongToBase.ContainsKey(cable.PrefabHash)) return;  // long variant -> handled elsewhere
            CableRollOnConstruct.Apply(ref targetRotation, ref _loggedActive, "kit");
        }
    }

    // A single-piece Constructor: Constructor.Construct(4-arg), building BuildStructure.
    [HarmonyPatch(typeof(Constructor), nameof(Constructor.Construct),
        new[] { typeof(Grid3), typeof(Quaternion), typeof(bool), typeof(ulong) })]
    internal static class RewriteCableRollOnSingleConstruct
    {
        private static bool _loggedActive;
        private static void Prefix(Constructor __instance, ref Quaternion targetRotation)
        {
            if (!Settings.CableAlignmentEnabled) return;
            if (!(__instance?.BuildStructure is Cable cable) || !cable.IsStraight) return;
            if (LongVariantRegistry.LongToBase.ContainsKey(cable.PrefabHash)) return;
            CableRollOnConstruct.Apply(ref targetRotation, ref _loggedActive, "single");
        }
    }

    internal static class CableRollOnConstruct
    {
        internal static void Apply(ref Quaternion targetRotation, ref bool loggedActive, string via)
        {
            try
            {
                targetRotation = CableRoll.Canonical(targetRotation);
                if (!loggedActive)
                {
                    loggedActive = true;
                    NetworkPuristPlusPlugin.Log?.LogInfo($"cable-roll-on-construct active ({via}): freshly built straight cables are canonicalised before creation.");
                }
            }
            catch (Exception e)
            {
                NetworkPuristPlusPlugin.PlayerWarn($"could not canonicalise a built cable's roll: {e}");
            }
        }
    }
}
