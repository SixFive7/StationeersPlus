using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using UnityEngine;

namespace ScenarioRunner
{
    // Scenario: spp-color-swatch-probe
    //
    // One-shot. Answers three questions that cannot be read out of the decompile because
    // GameManager.CustomColors is serialized inspector data on the GameManager prefab:
    //
    //   1. How many swatches ship, in what order, and which carry PaintOnly = true.
    //   2. Whether each swatch's Normal Material is a DISTINCT asset or whether several
    //      swatches (specifically the four Metallic Paints ones) share one Material.
    //   3. The colour-index -> DLCType map, recovered the only way the game exposes it:
    //      via the SprayCan prefab whose PaintMaterial is that swatch's Normal.
    //
    // Question 2 is the load-bearing one. Spray Paint Plus resolves a colour by walking
    // CustomColors and comparing Materials by reference (SprayPaintHelpers.GetPaintColorIndex
    // does `colors[i].Normal == paintMaterial`). If the metallic swatches share a single
    // Material, that comparison collapses them and a PaintMaterial-keyed colour-to-DLC map
    // cannot tell them apart, which would rule out the prefab-derived entitlement gate.
    //
    // Threading: this fires from the ElectricityTick UniTask worker, NOT the Unity main
    // thread (Research/Patterns/ThingEnumerationOffMainThread.md). Everything read here is
    // managed state only:
    //   * ColorSwatch is a plain serializable C# class, so Name / PaintOnly are managed
    //     field reads.
    //   * Material identity is compared with object.ReferenceEquals and printed via
    //     RuntimeHelpers.GetHashCode, both pure managed. Material.name and GetInstanceID()
    //     are deliberately NOT touched: they marshal into native Unity and can hard-crash
    //     the process off the main thread rather than throwing.
    //   * Thing.PrefabName is a managed string field and Thing.DLCType a serialized enum
    //     field, both already read from this pump by paintable-prefab-dump.

    internal static partial class Dispatcher
    {
        private static bool _sppColorSwatchProbeFired;

        private static void Scenario_SppColorSwatchProbe()
        {
            if (_sppColorSwatchProbeFired) return;
            _sppColorSwatchProbeFired = true;

            try
            {
                _log?.LogInfo("[ScenarioRunner] spp-color-swatch-probe START");

                var gm = GameManager.Instance;
                if (gm == null)
                {
                    _log?.LogWarning("[ScenarioRunner] spp-color-swatch | GameManager.Instance is null, aborting");
                    return;
                }

                List<ColorSwatch> colors = gm.CustomColors;
                if (colors == null)
                {
                    _log?.LogWarning("[ScenarioRunner] spp-color-swatch | CustomColors is null, aborting");
                    return;
                }

                _log?.LogInfo($"[ScenarioRunner] spp-color-swatch | CustomColors.Count={colors.Count}");

                // --- 1 + 2: swatch inventory and Normal-material distinctness ---
                for (int i = 0; i < colors.Count; i++)
                {
                    var sw = colors[i];
                    if (sw == null)
                    {
                        _log?.LogInfo($"[ScenarioRunner] spp-color-swatch | idx={i} <null swatch>");
                        continue;
                    }

                    string name = sw.Name ?? "<null>";
                    bool paintOnly = sw.PaintOnly;
                    bool normalNull = object.ReferenceEquals(sw.Normal, null);
                    bool emissiveNull = object.ReferenceEquals(sw.Emissive, null);
                    int normalId = normalNull ? 0 : RuntimeHelpers.GetHashCode(sw.Normal);

                    // Reference-identity scan against every earlier swatch. "shared=-1" means
                    // this swatch's Normal is its own asset; anything else is the index it
                    // shares a Material with.
                    int sharedWith = -1;
                    if (!normalNull)
                    {
                        for (int j = 0; j < i; j++)
                        {
                            var prev = colors[j];
                            if (prev == null) continue;
                            if (object.ReferenceEquals(prev.Normal, sw.Normal)) { sharedWith = j; break; }
                        }
                    }

                    _log?.LogInfo(
                        $"[ScenarioRunner] spp-color-swatch | idx={i} name={name} paintOnly={paintOnly} " +
                        $"normalNull={normalNull} emissiveNull={emissiveNull} normalObjId={normalId} sharedWithIdx={sharedWith}");
                }

                // --- 3: colour index -> DLCType, via the SprayCan prefabs ---
                var indexToDlc = new Dictionary<int, string>();
                int canCount = 0;

                foreach (var p in Prefab.AllPrefabs)
                {
                    if (p == null) continue;
                    if (!(p is SprayCan can)) continue;
                    canCount++;

                    string prefabName = can.PrefabName ?? "<null>";
                    string dlc = ExtractDlcTag(can);
                    bool pmNull = object.ReferenceEquals(can.PaintMaterial, null);
                    int pmId = pmNull ? 0 : RuntimeHelpers.GetHashCode(can.PaintMaterial);

                    // Resolve by reference identity, exactly as SprayPaintHelpers does.
                    int resolvedIdx = -1;
                    int matchCount = 0;
                    if (!pmNull)
                    {
                        for (int i = 0; i < colors.Count; i++)
                        {
                            var sw = colors[i];
                            if (sw == null) continue;
                            if (object.ReferenceEquals(sw.Normal, can.PaintMaterial))
                            {
                                if (resolvedIdx < 0) resolvedIdx = i;
                                matchCount++;
                            }
                        }
                    }

                    if (resolvedIdx >= 0)
                    {
                        if (indexToDlc.TryGetValue(resolvedIdx, out string existing) && existing != dlc)
                        {
                            _log?.LogWarning(
                                $"[ScenarioRunner] spp-color-swatch | CONFLICT idx={resolvedIdx} already mapped to {existing}, {prefabName} claims {dlc}");
                        }
                        indexToDlc[resolvedIdx] = dlc;
                    }

                    _log?.LogInfo(
                        $"[ScenarioRunner] spp-color-swatch can | {prefabName} dlc={dlc} paintMaterialNull={pmNull} " +
                        $"paintMaterialObjId={pmId} resolvedColorIdx={resolvedIdx} swatchMatches={matchCount}");
                }

                _log?.LogInfo($"[ScenarioRunner] spp-color-swatch | SprayCan prefabs found={canCount}");

                // --- Final map, one line per colour index ---
                for (int i = 0; i < colors.Count; i++)
                {
                    string name = colors[i]?.Name ?? "<null>";
                    string dlc = indexToDlc.TryGetValue(i, out string d) ? d : "NO-CAN";
                    _log?.LogInfo($"[ScenarioRunner] spp-color-swatch MAP | idx={i} name={name} dlc={dlc}");
                }

                _log?.LogInfo("[ScenarioRunner] spp-color-swatch-probe END");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] spp-color-swatch-probe threw: {e}");
            }
        }
    }
}
