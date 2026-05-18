using System.Collections.Generic;
using Assets.Scripts.Networks;
using HarmonyLib;
using JetBrains.Annotations;
using Networks;

namespace PowerGridPlus.Patches
{
    // Vanilla's CableNetwork.Merge(List<CableNetwork>) and StructureNetwork.Merge(List<StructureNetwork>)
    // both pick the survivor as list[0]. The two calls happen INDEPENDENTLY on server and client during
    // a wire-arriving structure's OnRegistered / DeserializeOnJoin pipeline, with no
    // RebuildCableNetworkEvent / RebuildStructureNetworkEvent analogue carrying the host's chosen
    // ReferenceId across (the split path has those events; the merge path does not). If the iteration
    // order of ConnectedCables / ConnectedNetworks differs between the two sides for any reason --
    // the cable's rotation differs and so OpenEnds enumerate adjacency in a different sequence, a
    // third-party mod perturbs the iteration, transient state at the moment of the call -- the two
    // sides pick different survivors. The losers stay alive on the OTHER side, so the divergence is
    // stable: server and client read different CableNetwork.ReferenceId for the same cable
    // indefinitely.
    //
    // Sorting the input list by ReferenceId ascending before vanilla picks list[0] makes the survivor
    // a pure function of the network identifiers, which are server-allocated and identical on every
    // peer for the same instance. The two sides converge on the same survivor regardless of any
    // ordering perturbation upstream.
    //
    // Coverage: this patch fixes a vanilla structural fragility that affects four network types:
    //   CableNetwork (and its subclass WirelessNetwork) via CableNetwork.Merge(List<CableNetwork>).
    //   RocketNetwork, RoboticArmNetwork, LandingPadNetwork via the inherited
    //     StructureNetwork.Merge(List<StructureNetwork>, out).
    // Both Merge overloads are static and non-virtual; the prefix fires for every caller.
    //
    // Safety of in-place sort: every call site passes ConnectedNetworks(thing) directly. Both
    // CableNetwork.ConnectedNetworks (decompile line 254113) and StructureNetwork.ConnectedNetworks
    // (line 177115) allocate a fresh List<> each invocation, so no caller depends on the input
    // ordering. Vanilla Merge only iterates the list, it does not modify it. No re-entry: instance
    // Merge(other) does not call back to the static Merge(List).
    //
    // Survivor change observed in the smoking-gun reproduction: server picked 386495 (high id, small
    // membership), client picked 386494 (low id, large membership). After the fix, both pick 386494.
    // Functionally identical merged network either way; only the identifier differs. Lowest-id wins
    // tends to preserve older, larger networks (older networks have lower ids), which usually matches
    // the implicit "preserve the big one" intuition behind vanilla's accidental list[0].
    [HarmonyPatch(typeof(CableNetwork), nameof(CableNetwork.Merge), new[] { typeof(List<CableNetwork>) })]
    public static class CableNetworkMergeDeterministicPatch
    {
        [HarmonyPrefix]
        [UsedImplicitly]
        public static void Prefix(List<CableNetwork> cableNetworks)
        {
            if (cableNetworks == null || cableNetworks.Count <= 1) return;
            cableNetworks.Sort(CompareByReferenceId);
        }

        private static int CompareByReferenceId(CableNetwork a, CableNetwork b)
            => (a?.ReferenceId ?? 0L).CompareTo(b?.ReferenceId ?? 0L);
    }

    [HarmonyPatch(typeof(StructureNetwork), nameof(StructureNetwork.Merge),
        new[] { typeof(List<StructureNetwork>), typeof(StructureNetwork) },
        new[] { ArgumentType.Normal, ArgumentType.Out })]
    public static class StructureNetworkMergeDeterministicPatch
    {
        [HarmonyPrefix]
        [UsedImplicitly]
        public static void Prefix(List<StructureNetwork> structureNetworks)
        {
            if (structureNetworks == null || structureNetworks.Count <= 1) return;
            structureNetworks.Sort(CompareByReferenceId);
        }

        private static int CompareByReferenceId(StructureNetwork a, StructureNetwork b)
            => (a?.ReferenceId ?? 0L).CompareTo(b?.ReferenceId ?? 0L);
    }
}
