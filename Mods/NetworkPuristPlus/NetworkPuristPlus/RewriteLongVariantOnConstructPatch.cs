using Assets.Scripts.GridSystem;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using System;
using UnityEngine;

namespace NetworkPuristPlus
{
    // Block 4 (build-time rewrite): when something tries to place a "long" multi-tile straight variant
    // (StructurePipeStraight3/5/10, StructureCableSuperHeavyStraight3/5/10, etc.), expand it into N
    // single-tile placements right there instead of letting the long piece exist until the next world
    // load. Constructor.SpawnConstruct is the convergence point for vanilla manual builds, vanilla
    // multi-kit builds (host and the remote-client multi-kit message handler, which re-runs Construct
    // host-side), ZoopMod (its commit replays into Construct -> SpawnConstruct), and Cable.Break's
    // rupture re-spawn. (A BlueprintMod paste uses OnServer.Create<Thing> directly, not SpawnConstruct,
    // so a blueprint-pasted long piece is the one case still caught only by the on-load rebuild --
    // ReplaceLongPiecesOnLoadPatch -- which is the universal backstop.)
    //
    // It is a rewrite, never a reject: the kits keep working, they just produce single tiles. When the
    // base is a straight cable, the single tiles are spawned at the canonical roll for the run axis
    // (CableRoll.Canonical) so they are born aligned; for pipes/chutes the long piece's own rotation is
    // kept. The inner SpawnConstruct calls fall through this prefix (the base prefab is not a long
    // variant), so there is no recursion. If the long prefab's footprint cannot be determined, the
    // original placement proceeds and the on-load rebuild handles it next time the world loads.
    [HarmonyPatch(typeof(Constructor), nameof(Constructor.SpawnConstruct))]
    internal static class RewriteLongVariantOnConstructPatch
    {
        private static bool Prefix(CreateStructureInstance instance, ref Structure __result)
        {
            if (!Settings.MasterEnabled) return true;               // master toggle off -> mod is inert (also: LongToBase is empty)
            if (instance?.Prefab == null) return true;
            if (!LongVariantRegistry.LongToBase.TryGetValue(instance.Prefab.PrefabHash, out Structure basePrefab) || basePrefab == null) return true;

            try
            {
                Grid3[] cells = instance.Prefab.GridBounds?.GetLocalSmallGrid(instance.WorldPosition, instance.WorldRotation) as Grid3[];
                if (cells == null || cells.Length < 2)
                {
                    NetworkPuristPlusPlugin.PlayerWarn($"could not expand {instance.Prefab.PrefabName} at build time; it will be rebuilt from single tiles on the next world load.");
                    return true;
                }

                Quaternion rot = (basePrefab is Cable bc && bc.IsStraight) ? CableRoll.Canonical(instance.LocalRotation) : instance.LocalRotation;
                Structure first = null;
                foreach (Grid3 cell in cells)
                {
                    Structure s = Constructor.SpawnConstruct(new CreateStructureInstance(basePrefab, cell, rot, instance.OwnerClientId, instance.CustomColor));
                    if (first == null) first = s;
                }

                NetworkPuristPlusPlugin.PlayerLog($"built {cells.Length} x {basePrefab.PrefabName} in place of {instance.Prefab.PrefabName} (long pieces are disabled).");
                __result = first;
                return false;
            }
            catch (Exception e)
            {
                NetworkPuristPlusPlugin.PlayerError($"failed to expand {instance.Prefab.PrefabName} into single tiles: {e}");
                return true;   // fall back to the original placement; the on-load rebuild will fix it
            }
        }
    }
}
