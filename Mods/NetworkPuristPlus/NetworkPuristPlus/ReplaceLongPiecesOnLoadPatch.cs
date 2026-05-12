using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetworkPuristPlus
{
    // After a save finishes loading, rebuild every already-placed long straight pipe/cable/chute
    // from the equivalent run of single-tile pieces.
    //
    // World.OnLoadingFinished runs at the end of XmlSaveLoad.LoadWorld: every Thing is loaded,
    // networks are linked, Thing.OnFinishedLoad has fired, GameManager.GameState is Running. Host /
    // single-player only -- on a client GameManager.RunSimulation is false; the host's replacements
    // arrive through the normal world sync, and Constructor.SpawnConstruct on a client does nothing
    // anyway. The long-variant prefabs themselves stay registered (Prefab.AllPrefabs) so that an old
    // save containing a long piece still deserializes; we delete the instances here, after the fact.
    //
    // For each long piece: compute its N small-grid cells, snapshot rotation / owner / paint, destroy
    // it (OnServer.Destroy), then SpawnConstruct the single-tile base piece at each of those cells.
    // For a CABLE base the singles are spawned at the canonical roll for the run axis (CableRoll.Canonical)
    // so a rebuilt cable run is born aligned; for pipes/chutes the long piece's own rotation is kept.
    // Placed while GameState == Running, each base piece's OnRegistered merges it with its neighbours,
    // restitching the network -- so the run stays structurally connected.
    //
    // KNOWN LIMITATION (TODO): the gas inside a rebuilt PIPE run is NOT preserved -- the rebuilt
    // single-tile pipes start empty, re-pressurise them. OnServer.Destroy is deferred to end-of-frame
    // and the resulting Pipe.OnDestroy -> NetworkAtmosphereEvent.DivideNetworkAtmosphere dance loses
    // the network's gas when its pipes are replaced this way; a capture-then-Atmosphere.Add re-inject
    // after a frame delay was tried and the gas still ended up at zero (likely overwritten by a late
    // atmospherics event), so v1.0 simply doesn't claim to preserve it. Structure, layout, rotation,
    // colour and network connectivity are preserved; chute items in transit through a destroyed
    // segment and pipe gas are not.
    //
    // Every piece this rebuilds is logged via NetworkPuristPlusPlugin.PlayerLog -- BepInEx log,
    // Player.log, and the in-game `~` console.
    [HarmonyPatch(typeof(World), nameof(World.OnLoadingFinished))]
    internal static class ReplaceLongPiecesOnLoadPatch
    {
        private static void Postfix()
        {
            if (!Settings.MasterEnabled) return;                     // master toggle off -> mod is inert
            if (!GameManager.RunSimulation) return;                  // host / single-player only
            if (LongVariantRegistry.LongToBase.Count == 0) return;

            // Snapshot the placed-structure pool first -- we mutate it (destroy + create) while iterating.
            var targets = new List<Structure>();
            try
            {
                GridController.AllStructuresPool.ForEach(s =>
                {
                    if (s != null && !s.IsBeingDestroyed && LongVariantRegistry.LongToBase.ContainsKey(s.PrefabHash))
                        targets.Add(s);
                });
            }
            catch (Exception e)
            {
                NetworkPuristPlusPlugin.PlayerError($"could not scan placed structures: {e}");
                return;
            }
            if (targets.Count == 0)
            {
                NetworkPuristPlusPlugin.PlayerLog("no long pieces in this world; it is already clean, nothing to rebuild. All good.");
                return;
            }

            NetworkPuristPlusPlugin.PlayerLog($"found {targets.Count} long piece(s) to rebuild from single-tile pieces...");
            int rebuilt = 0, segments = 0, failed = 0;
            foreach (Structure longPiece in targets)
            {
                if (longPiece == null || longPiece.IsBeingDestroyed) continue;
                if (!LongVariantRegistry.LongToBase.TryGetValue(longPiece.PrefabHash, out Structure basePrefab) || basePrefab == null) continue;

                try
                {
                    var cells = longPiece.GridBounds?.GetLocalSmallGrid(longPiece.ThingTransformPosition, longPiece.ThingTransformRotation) as Grid3[];
                    if (cells == null || cells.Length == 0)
                    {
                        NetworkPuristPlusPlugin.PlayerWarn($"{SafeName(longPiece)} (ref {longPiece.ReferenceId}): could not determine footprint cells; leaving it in place.");
                        failed++;
                        continue;
                    }

                    Quaternion rotation = longPiece.ThingTransformRotation;
                    Quaternion spawnRotation = (basePrefab is Cable bc && bc.IsStraight) ? CableRoll.Canonical(rotation) : rotation;
                    ulong owner = longPiece.OwnerClientId;
                    int colorIndex = PaintedColorIndex(longPiece);
                    Vector3 p = longPiece.ThingTransformPosition;
                    NetworkPuristPlusPlugin.PlayerLog($"  rebuilding {longPiece.PrefabName} (ref {longPiece.ReferenceId}) at ({p.x:0.#}, {p.y:0.#}, {p.z:0.#}) -> {cells.Length} x {basePrefab.PrefabName}{(colorIndex >= 0 ? $" (color {colorIndex})" : "")}");

                    OnServer.Destroy(longPiece);
                    foreach (Grid3 cell in cells)
                    {
                        Constructor.SpawnConstruct(new CreateStructureInstance(basePrefab, cell, spawnRotation, owner, colorIndex));
                        segments++;
                    }
                    rebuilt++;
                }
                catch (Exception e)
                {
                    failed++;
                    NetworkPuristPlusPlugin.PlayerError($"failed to rebuild {SafeName(longPiece)} (ref {(longPiece != null ? longPiece.ReferenceId : 0)}): {e}");
                }
            }

            NetworkPuristPlusPlugin.PlayerLog($"done: rebuilt {rebuilt} long piece(s) as {segments} single-tile segment(s){(failed > 0 ? $" ({failed} failed -- see warnings/errors above)" : "")}. Rebuilt pipe runs start empty -- re-pressurise them.");
        }

        // The painted swatch index, or -1 ("keep the base prefab's default colour") if the structure
        // carries no paint override. A Thing's CustomColor is its PaintableMaterial swatch by default;
        // it's a real override only when CustomColor.Normal differs from PaintableMaterial.
        private static int PaintedColorIndex(Structure s)
        {
            try
            {
                if (s.CustomColor != null && s.PaintableMaterial != null && s.CustomColor.Normal != s.PaintableMaterial)
                    return s.CustomColor.Index;
            }
            catch { }
            return -1;
        }

        private static string SafeName(Structure s)
        {
            try { return s != null ? s.PrefabName : "(null)"; }
            catch { return "(unknown)"; }
        }
    }
}
