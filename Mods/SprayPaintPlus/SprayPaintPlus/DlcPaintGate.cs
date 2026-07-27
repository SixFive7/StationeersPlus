using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using DLC;
using System.Collections.Generic;
using UnityEngine;

namespace SprayPaintPlus
{
    /// <summary>
    /// Entitlement gate for paint colors.
    ///
    /// The game gates DLC paint at the point you OBTAIN a spray can (creative spawn and
    /// the fabricator both call SharedDLCManager.CheckSharedAccess on the can prefab's
    /// DLCType). There is no check anywhere on the paint-application path: not in
    /// Thing.SetCustomColor, not in OnServer.SetCustomColor, not in ISprayer.DoSpray,
    /// and ColorSwatch itself carries no DLCType. GameManager.CustomColors is a plain
    /// ungated list holding every swatch on every install, owned or not.
    ///
    /// That is coherent for vanilla, where holding the can is the only way to reach the
    /// color. It is not coherent for this mod, which reaches a color by index: the color
    /// scroll and the eyedropper both recolor the can already in your hand and so never
    /// pass through the gate. Before 1.10.1 that let a player reach the four Metallic
    /// Paints colors without the DLC.
    ///
    /// This class rebuilds the missing link. A color's DLC requirement lives on the
    /// SprayCan prefab that dispenses it, so walking Prefab.AllPrefabs and matching each
    /// can's PaintMaterial back to a swatch recovers a colorIndex -> DLCType map. The
    /// check itself then delegates to SharedDLCManager.CheckSharedAccess, the same call
    /// vanilla uses, which gets shared-DLC semantics for free: if any player in the
    /// session owns Metallic Paints, everyone in that session may use those colors.
    ///
    /// Verified at runtime in game 0.2.6403.27689: 16 swatches, all with distinct Normal
    /// materials, and 16 SprayCan prefabs resolving one-to-one onto them. Indices 0-11 are
    /// DLCType.None; 12-15 (ColorObsidian, ColorSilver, ColorBronze, ColorGold) are
    /// DLCType.MetallicPaints. See Research/GameSystems/DLCGating.md.
    ///
    /// The same map is also the source of the paint-family grouping that
    /// ColorCyclingMode.WithinFamily restricts cycling to (SameFamily / FamilyOf /
    /// FamilyName). That is a separate concern from entitlement and is deliberately not
    /// wired into IsColorAllowed: the family rule restricts which colors a can may reach,
    /// while entitlement decides which colors exist for this session at all. Entitlement
    /// is always checked first and no mode may loosen it.
    /// </summary>
    internal static class DlcPaintGate
    {
        // colorIndex -> DLCType required to obtain the can that dispenses that color.
        // Only populated for colors that have a dispensing can; see IsColorAllowed for
        // what a missing entry means.
        private static readonly Dictionary<int, DLCType> ColorDlc = new Dictionary<int, DLCType>();

        // Set only by a build that actually found spray can prefabs. A build attempted
        // before the prefab registry or GameManager is ready leaves this false so the
        // next call retries. Latching it on failure would leave ColorDlc permanently
        // empty, which reads as "nothing is gated" and would silently restore the bug.
        private static bool _built;

        /// <summary>
        /// Builds the color-to-DLC map. Safe to call more than once; a failed attempt is
        /// not cached. Call from Prefab.OnPrefabsLoaded for an early log line, and rely on
        /// the lazy retry in the accessors for the case where that fires too early.
        /// </summary>
        internal static void Build()
        {
            var colors = GameManager.Instance?.CustomColors;
            if (colors == null || colors.Count == 0)
                return;

            var map = new Dictionary<int, DLCType>();
            int cans = 0;
            int gated = 0;

            foreach (Thing thing in Prefab.AllPrefabs)
            {
                if (!(thing is SprayCan can))
                    continue;
                Material paint = can.PaintMaterial;
                if (paint == null)
                    continue;

                cans++;

                int index = IndexOfSwatchMaterial(colors, paint);
                if (index < 0)
                {
                    SprayPaintPlusPlugin.Log.LogWarning(
                        $"Spray can '{can.PrefabName}' has a paint material that matches no color swatch; " +
                        "its color cannot be entitlement-checked and will be treated as unrestricted.");
                    continue;
                }

                map[index] = can.DLCType;
                if (can.DLCType != DLCType.None)
                    gated++;
            }

            if (cans == 0)
            {
                // Not necessarily broken: this fires when the build runs before the prefab
                // registry is populated. Left unbuilt so the next call retries.
                return;
            }

            ColorDlc.Clear();
            foreach (var kvp in map)
                ColorDlc[kvp.Key] = kvp.Value;
            _built = true;

            SprayPaintPlusPlugin.Log.LogInfo(
                $"DLC paint map built: {colors.Count} color swatches, {cans} spray can prefabs, " +
                $"{gated} DLC-gated color(s).");

            if (gated == 0)
                SprayPaintPlusPlugin.Log.LogInfo(
                    "No DLC-gated paint colors in this install; every color is unrestricted.");
        }

        /// <summary>
        /// True when this session may use the color at all. This is the hard gate: it
        /// ignores player preference and is the only check the server trusts.
        /// </summary>
        internal static bool IsColorAllowed(int colorIndex)
        {
            EnsureBuilt();

            // No dispensing can means no entitlement to check. Mod-registered swatches sit
            // past the game's own content and have no DLC concept at all, so refusing them
            // would break other mods' colors: a worse bug than the one this gate closes.
            if (!ColorDlc.TryGetValue(colorIndex, out DLCType required))
                return true;

            if (required == DLCType.None)
                return true;

            // The same call vanilla's spawn and fabricator gates use. Reads the session-wide
            // pool, not just local ownership, so a session where any player owns the DLC
            // keeps working for everyone in it.
            return SharedDLCManager.CheckSharedAccess(required);
        }

        /// <summary>
        /// True when the color belongs in this client's scroll cycle. Client-local only.
        /// The server must never consult this, because one player's cycle preference says
        /// nothing about what another player is allowed to do.
        ///
        /// As of v1.11.0 this adds nothing on top of the entitlement gate. The one
        /// client-local filter it used to carry was the "Enable Metallic Paints" toggle,
        /// which is gone: the "Cycles within paint family" mode replaces it. That rule is
        /// relative to the color already on the can, so a single-index predicate cannot
        /// express it and it lives in ColorCyclerPatch.NextColorInCycle via SameFamily
        /// instead. The method stays because it names a real distinction that the trust
        /// boundary in SprayCanColorMessage depends on, and because a future client-local
        /// filter belongs here rather than inside IsColorAllowed.
        /// </summary>
        internal static bool IsColorInCycle(int colorIndex)
        {
            return IsColorAllowed(colorIndex);
        }

        /// <summary>
        /// True when two colors belong to the same paint family, which is the boundary
        /// ColorCyclingMode.WithinFamily may not cross.
        /// </summary>
        internal static bool SameFamily(int indexA, int indexB)
        {
            return FamilyOf(indexA) == FamilyOf(indexB);
        }

        /// <summary>
        /// The DLC that owns a color's family, reusing the colorIndex -> DLCType map that
        /// the entitlement gate already builds. Base colors are DLCType.None; the four
        /// Metallic Paints colors are DLCType.MetallicPaints.
        ///
        /// A color absent from the map joins the base family. That is a decided policy, not
        /// a fallback: a swatch with no dispensing can is typically another mod's custom
        /// color, and it has no DLC concept at all. Grouping it with the base colors keeps
        /// those mods working, and it means such a can lands in the largest family rather
        /// than a family of one, so WithinFamily never strands it with nowhere to cycle.
        /// This matches IsColorAllowed, which treats the same absence as unrestricted.
        /// </summary>
        internal static DLCType FamilyOf(int colorIndex)
        {
            EnsureBuilt();
            if (!ColorDlc.TryGetValue(colorIndex, out DLCType required))
                return DLCType.None;
            return required;
        }

        /// <summary>
        /// Short human-readable family name for the console message the eyedropper prints
        /// when a pick would cross a family boundary. Any DLC beyond the one that exists
        /// today falls back to its enum name: blunt, but it will not silently mislabel a
        /// future paint DLC as metallic.
        /// </summary>
        internal static string FamilyName(int colorIndex)
        {
            DLCType family = FamilyOf(colorIndex);
            if (family == DLCType.None)
                return "standard";
            if ((family & DLCType.MetallicPaints) != 0)
                return "metallic";
            return family.ToString();
        }

        private static void EnsureBuilt()
        {
            if (_built)
                return;
            Build();
        }

        private static int IndexOfSwatchMaterial(List<ColorSwatch> colors, Material paintMaterial)
        {
            for (int i = 0; i < colors.Count; i++)
            {
                ColorSwatch swatch = colors[i];
                if (swatch == null)
                    continue;
                if (ReferenceEquals(swatch.Normal, paintMaterial))
                    return i;
            }
            return -1;
        }
    }
}
