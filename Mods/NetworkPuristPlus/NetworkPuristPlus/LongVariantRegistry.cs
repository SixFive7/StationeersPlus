using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Util;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace NetworkPuristPlus
{
    // Identifies the "long straight" pipe/cable/chute variants and their single-tile base pieces.
    //
    // A long straight variant is a Structure whose prefab name is "<some registered straight piece>"
    // followed by a length-digit run and an optional "Burnt" suffix, e.g.
    //   StructurePipeStraight3 / 5 / 10
    //   StructureInsulatedPipeStraight3 / 5 / 10
    //   StructurePipeLiquidStraight3 / 5 / 10
    //   StructureInsulatedPipeLiquidStraight3 / 5 / 10
    //   StructureChuteStraight3 / 5 / 10
    //   StructureCableSuperHeavyStraight3 / 5 / 10  (and ...Straight3Burnt etc.)
    // The base piece is the same name with the digit run removed (keeping the optional "Burnt"):
    //   StructurePipeStraight10              -> StructurePipeStraight
    //   StructureCableSuperHeavyStraight10Burnt -> StructureCableSuperHeavyStraightBurnt
    //
    // Classification is by name + "the de-numbered name is itself a registered Structure". The single-tile
    // base ("...Straight" without digits) never matches the regex, so bases are never misclassified. Each
    // matched long variant is also assigned a LongPieceFamily (from its base name); a long variant is only
    // recorded here -- and therefore only stripped / hidden / rebuilt / build-time-rewritten -- when its
    // family's per-family settings toggle is on (and the master toggle is on). See Settings.cs.
    internal static class LongVariantRegistry
    {
        // long-variant PrefabHash -> the single-tile base Structure prefab it should be replaced with.
        internal static readonly Dictionary<int, Structure> LongToBase = new Dictionary<int, Structure>();

        // The long-variant prefab objects themselves (the ones we are removing -- keyed-in only when the
        // family toggle is on). Parallel to LongToBase's keys. Used by HideLongVariantsStationpediaPatch
        // to re-assert HideInStationpedia after a page-override pass; LongToBase only points the other way.
        internal static readonly List<Structure> LongVariants = new List<Structure>();

        // long-variant PrefabHash -> its segment count (3 / 5 / 10), from the trailing digit run. Diagnostic
        // only -- the build-time rewrite reads the actual footprint cells off GridBounds, not this.
        internal static readonly Dictionary<int, int> LongToCellCount = new Dictionary<int, int>();

        // How many build kits had long variants stripped from their option list. Diagnostic only.
        internal static int StrippedKitCount;

        // ^(Structure<word>Straight)(<digits>)(Burnt)?$  -- group 1 + group 3 == the base prefab name.
        private static readonly Regex LongNameRegex =
            new Regex(@"^(Structure[A-Za-z]+Straight)(\d+)(Burnt)?$", RegexOptions.CultureInvariant);

        internal static void Build()
        {
            LongToBase.Clear();
            LongVariants.Clear();
            LongToCellCount.Clear();
            StrippedKitCount = 0;
            var log = NetworkPuristPlusPlugin.Log;

            // Master toggle: when off, do nothing at all -- no Constructables strip, no HideInStationpedia,
            // no AutomaticSetup. Leave the collections empty so every downstream patch / pass is inert too.
            if (!Settings.MasterEnabled)
            {
                log.LogInfo("master toggle is off -- Network Purist Plus does nothing this session (no long-variant strip, no Stationpedia hide, no cable alignment, no build-cursor change).");
                return;
            }

            int prefabs = 0, structures = 0, nameMatches = 0, familyDisabled = 0;

            // Pass 1: classify long variants by name, map each to its single-tile base, hide from Stationpedia.
            // Skip a long variant whose family's per-family toggle is off (leave it exactly as vanilla ships it).
            foreach (Thing thing in Prefab.AllPrefabs)
            {
                prefabs++;
                if (!(thing is Structure structure)) continue;
                structures++;

                Match m = LongNameRegex.Match(structure.PrefabName ?? string.Empty);
                if (!m.Success) continue;
                nameMatches++;

                string baseName = m.Groups[1].Value + m.Groups[3].Value;
                if (!(Prefab.Find(baseName) is Structure basePrefab))
                {
                    log.LogWarning($"  candidate {structure.PrefabName}: base prefab '{baseName}' not found / not a Structure -> skipped");
                    continue;
                }

                LongPieceFamily family = Settings.ClassifyFamily(baseName);
                if (!Settings.FamilyEnabled(family))
                {
                    familyDisabled++;
                    log.LogInfo($"  long variant: {structure.PrefabName} (family {family}) -> family toggle is OFF, leaving it as vanilla");
                    continue;
                }

                LongToBase[structure.PrefabHash] = basePrefab;
                LongVariants.Add(structure);
                if (int.TryParse(m.Groups[2].Value, out int segments)) LongToCellCount[structure.PrefabHash] = segments;
                structure.HideInStationpedia = true;
                int cells = CellCount(structure);
                log.LogInfo($"  long variant: {structure.PrefabName} (family {family}, footprint {(cells > 0 ? cells + " cell(s)" : "n/a")}, name says {segments}) -> base {baseName}");
            }
            log.LogInfo($"scanned {prefabs} prefab(s), {structures} Structure(s); {nameMatches} matched the long-variant name pattern; {LongToBase.Count} classified" + (familyDisabled > 0 ? $", {familyDisabled} left alone (family toggle off)." : "."));

            // The single-tile straight pieces to give a SmartRotate "Straight" connection type (3 orientations,
            // one canonical roll per run axis) instead of the C# default "Exhaustive" (24 orientations, no
            // symmetry assumed):
            //   - every single-tile base whose long variants we are stripping (pipes, insulated pipes, liquid
            //     pipes, chutes, super-heavy cable -- "Straight"-typed in vanilla; without one in their kit's
            //     Constructables, MultiMergeConstructor.Construct's merge-with-tool path looks for a
            //     "Straight"-typed entry, finds none, and throws). This is the merge-with-tool fix; it is
            //     gated by the per-family toggles via LongToBase.Values.
            //   - if the cable-alignment toggle is on, every straight cable of every tier (so the build cursor
            //     cloned from the prefab uses the canonical roll, matching the cable-alignment feature). The
            //     stripped super-heavy-cable base is already in the first set, so when cable alignment is off
            //     but the super-heavy toggle is on, the merge-with-tool fix still runs on it; the normal/heavy
            //     cables (no long variant) only get the cursor change when cable alignment is on.
            var autoSetup = new HashSet<int>();
            foreach (Structure b in LongToBase.Values) autoSetup.Add(b.PrefabHash);
            if (Settings.CableAlignmentEnabled)
                foreach (Thing thing in Prefab.AllPrefabs)
                    if (thing is Cable cable && cable.IsStraight) autoSetup.Add(cable.PrefabHash);

            // Pass 2: remove the long variants from every build kit's option list (the mouse-wheel), and run
            // SmartRotate.AutomaticSetup on the kit's straight pipe/cable/chute options (so the merge-with-tool
            // path sees a "Straight"-typed entry even though the long variants are gone). Index 0 of a kit's
            // Constructables is always the single-tile base straight; ZoopMod relies on that (it matches
            // Constructables[0].PrefabName + digits against the rest of the list), so RemoveAll on the long
            // variants is safe and leaves index 0 untouched -- ZoopMod then falls back to placing only
            // single-tile pieces with no errors. MultiConstructor.OnPrefabLoad has already done its own
            // null-strip + LastSelectedIndex clamp by the time this runs. Only long variants whose family
            // toggle is on are in LongToBase, so a disabled family's kit is left fully intact.
            foreach (Thing thing in Prefab.AllPrefabs)
            {
                if (!(thing is MultiConstructor kit) || kit.Constructables == null) continue;

                int removed = kit.Constructables.RemoveAll(s => s != null && LongToBase.ContainsKey(s.PrefabHash));

                foreach (Structure option in kit.Constructables)
                    if (option != null && autoSetup.Contains(option.PrefabHash)) AutomaticSetupStraight(option, log);

                if (removed == 0) continue;
                if (kit.LastSelectedIndex >= kit.Constructables.Count)
                    kit.LastSelectedIndex = Math.Max(0, kit.Constructables.Count - 1);
                StrippedKitCount++;
                log.LogInfo($"  stripped {removed} long variant(s) from kit {kit.PrefabName} ({kit.Constructables.Count} option(s) left)");
            }

            // Pass 3: the same SmartRotate "Straight" setup applied to the registry prefab itself, so the build
            // cursor that is later cloned from the prefab (in InventoryManager) inherits it.
            int aligned = 0;
            foreach (Thing thing in Prefab.AllPrefabs)
                if (autoSetup.Contains(thing.PrefabHash) && AutomaticSetupStraight(thing, log))
                    aligned++;
            if (aligned > 0) log.LogInfo($"set the Straight connection type on {aligned} straight pipe/cable/chute prefab(s)");
        }

        // Run SmartRotate.AutomaticSetup on a straight pipe/cable/chute prefab: for a (Grid, All-axis) straight
        // piece this sets ConnectionType = Straight and OpenEndsPermutation to the matching axis triple, and
        // touches nothing else (it falls back to Exhaustive -- a no-op for a piece that is already Exhaustive --
        // if it cannot classify the geometry). Idempotent; safe to call on the same object more than once.
        // Returns true if `thing` is an ISmartRotatable and AutomaticSetup ran without throwing.
        private static bool AutomaticSetupStraight(Thing thing, BepInEx.Logging.ManualLogSource log)
        {
            if (!(thing is ISmartRotatable rotatable)) return false;
            try { SmartRotate.AutomaticSetup(rotatable); return true; }
            catch (Exception e) { log.LogWarning($"  SmartRotate.AutomaticSetup({thing.PrefabName}) failed: {e.Message}"); return false; }
        }

        // Number of small-grid cells the structure occupies, from its cached GridBounds. Returns 0
        // when the footprint has not been computed yet (a fresh prefab whose CachePrefabBounds() has
        // not run) or cannot be determined. Reliable (> 0) on a placed instance.
        internal static int CellCount(Structure s)
        {
            try
            {
                var cells = s.GridBounds?.GetLocalSmallGrid(Vector3.zero, Quaternion.identity) as Grid3[];
                return cells?.Length ?? 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
