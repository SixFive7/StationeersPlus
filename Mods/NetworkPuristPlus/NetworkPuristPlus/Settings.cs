using BepInEx.Configuration;
using System.Collections.Generic;

namespace NetworkPuristPlus
{
    // The five long-piece families. A long variant's family is decided from its single-tile base
    // name (regex group 1: "Structure<word>Straight" with the digit run and any "Burnt" suffix removed).
    // The classification gates everything downstream: LongVariantRegistry.Build() only records a long
    // variant in LongToBase if its family's toggle is on, and the strip / hide / rebuild / build-time
    // rewrite all key off LongToBase, so a disabled family is left exactly as vanilla ships it.
    internal enum LongPieceFamily
    {
        GasPipe,        // StructurePipeStraight       (NOT insulated, NOT liquid)
        LiquidPipe,     // StructurePipeLiquidStraight
        InsulatedPipe,  // StructureInsulatedPipeStraight, StructureInsulatedPipeLiquidStraight
        Chute,          // StructureChuteStraight
        SuperHeavyCable,// StructureCableSuperHeavyStraight (and ...Burnt)
        Unknown         // anything else that matched the name regex (a future mod's long variant)
    }

    // Server-authoritative settings for Network Purist Plus. All entries live in "Server - *" sections
    // and carry an Order tag (repo convention). The host's values are the ones that take effect (the
    // world rebuild + the prefab-time strip/hide are host-authoritative); a joining client whose values
    // differ from the host's is rejected at join time by the JoinValidator in Plugin.cs (the prefab-time
    // effects run at OnPrefabsLoaded, before any join, so each machine first applies its own config --
    // the validator turns a mismatch into a clean rejection rather than a silent desync of the build-kit
    // option lists). Document: README "all players must run the same settings, like the same version".
    internal static class Settings
    {
        // Master toggle. When off the mod does NOTHING: LongVariantRegistry.Build() short-circuits (so no
        // Constructables strip, no HideInStationpedia, no AutomaticSetup), and every Harmony patch / pass
        // early-returns. The patches are still applied (cheap, and PatchAll runs unconditionally) -- they
        // just check Enabled first.
        internal static ConfigEntry<bool> Enabled;

        // Per-family toggles for the long-piece removal. Each gates, for that family: whether its long
        // variants are stripped from build kits, hidden from the Stationpedia, rebuilt on load, and
        // rewritten at build time. (Implementation: LongVariantRegistry.Build() only adds a long variant
        // to LongToBase if its family's toggle is on; everything downstream keys off LongToBase. The
        // family-base AutomaticSetup -- the merge-with-tool fix -- also runs only for the families whose
        // long variants are stripped, i.e. iff that family's toggle is on.)
        internal static ConfigEntry<bool> RemoveLongGasPipes;
        internal static ConfigEntry<bool> RemoveLongLiquidPipes;
        internal static ConfigEntry<bool> RemoveLongInsulatedPipes;   // covers insulated gas AND insulated liquid
        internal static ConfigEntry<bool> RemoveLongChutes;
        internal static ConfigEntry<bool> RemoveLongSuperHeavyCables;

        // Cable-alignment toggle. When off: skip the on-load cable sweep, the Cable.OnRegistered re-roll,
        // and the AutomaticSetup of cable prefabs that have no long variant (StructureCableStraight /
        // StructureCableStraightH -- the cursor half of the alignment feature for those tiers). The
        // family-base AutomaticSetup for stripped pipe/chute/super-heavy-cable bases is the merge-with-tool
        // fix and stays tied to the per-family toggles, NOT this one.
        internal static ConfigEntry<bool> AlignStraightCables;

        // Bind every entry. Call from Plugin.Awake before anything reads a value.
        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind(
                "Server - Pieces", "Enable Network Purist Plus", true,
                new ConfigDescription(
                    "(Server-authoritative) Master switch. When off, the mod does nothing: long-piece variants stay in the build kits and the Stationpedia, no long run is rebuilt on load, no cable is realigned, and the build cursor is left untouched -- as if the mod were not installed. All players on a server must have the same value for this; a joining client whose value differs from the host's is rejected with a clear message.",
                    null,
                    new KeyValuePair<string, int>("Order", 10)));

            RemoveLongGasPipes = config.Bind(
                "Server - Pieces", "Remove Long Gas Pipes", true,
                new ConfigDescription(
                    "(Server-authoritative) Remove the long (3 / 5 / 10 segment) StructurePipeStraight variants: strip them from the pipe kit, hide them from the Stationpedia, rebuild placed long gas-pipe runs from single tiles on load, and rewrite a long gas pipe placed mid-game into single tiles. No effect when the master toggle is off. All players on a server must have the same value; a mismatch rejects the joining client.",
                    null,
                    new KeyValuePair<string, int>("Order", 20)));

            RemoveLongLiquidPipes = config.Bind(
                "Server - Pieces", "Remove Long Liquid Pipes", true,
                new ConfigDescription(
                    "(Server-authoritative) Remove the long (3 / 5 / 10 segment) StructurePipeLiquidStraight variants: strip them from the liquid-pipe kit, hide them from the Stationpedia, rebuild placed long liquid-pipe runs from single tiles on load, and rewrite a long liquid pipe placed mid-game into single tiles. No effect when the master toggle is off. All players on a server must have the same value; a mismatch rejects the joining client.",
                    null,
                    new KeyValuePair<string, int>("Order", 30)));

            RemoveLongInsulatedPipes = config.Bind(
                "Server - Pieces", "Remove Long Insulated Pipes", true,
                new ConfigDescription(
                    "(Server-authoritative) Remove the long (3 / 5 / 10 segment) insulated-pipe variants (StructureInsulatedPipeStraight and StructureInsulatedPipeLiquidStraight -- both insulated gas and insulated liquid): strip them from the insulated-pipe kits, hide them from the Stationpedia, rebuild placed long runs from single tiles on load, and rewrite one placed mid-game into single tiles. No effect when the master toggle is off. All players on a server must have the same value; a mismatch rejects the joining client.",
                    null,
                    new KeyValuePair<string, int>("Order", 40)));

            RemoveLongChutes = config.Bind(
                "Server - Pieces", "Remove Long Chutes", true,
                new ConfigDescription(
                    "(Server-authoritative) Remove the long (3 / 5 / 10 segment) StructureChuteStraight variants: strip them from the chute kit, hide them from the Stationpedia, rebuild placed long chute runs from single tiles on load (an item in transit inside a destroyed segment is lost), and rewrite a long chute placed mid-game into single tiles. No effect when the master toggle is off. All players on a server must have the same value; a mismatch rejects the joining client.",
                    null,
                    new KeyValuePair<string, int>("Order", 50)));

            RemoveLongSuperHeavyCables = config.Bind(
                "Server - Pieces", "Remove Long Super-Heavy Cables", true,
                new ConfigDescription(
                    "(Server-authoritative) Remove the long (3 / 5 / 10 segment) StructureCableSuperHeavyStraight variants (the only cable tier that has long variants; includes the burnt damage-state siblings): strip them from the super-heavy cable kit, hide them from the Stationpedia, rebuild placed long super-heavy cable runs from single tiles on load, and rewrite one placed mid-game into single tiles. No effect when the master toggle is off. All players on a server must have the same value; a mismatch rejects the joining client.",
                    null,
                    new KeyValuePair<string, int>("Order", 60)));

            AlignStraightCables = config.Bind(
                "Server - Cables", "Align Straight Cables", true,
                new ConfigDescription(
                    "(Server-authoritative) Re-roll every straight cable (all tiers) to one consistent orientation per run axis: existing runs when a save loads, freshly built ones the instant they register, and the build cursor for the straight-cable tiers that have no long variant. Cosmetic only -- connectivity, networks and colour are untouched. The cable rotate key becomes preview-only. When off, none of that happens (a cable's roll is left wherever it was placed). The merge-with-tool fix for the stripped pipe / chute / super-heavy-cable kits is separate and is governed by the per-family toggles, not this one. No effect when the master toggle is off. All players on a server must have the same value; a mismatch rejects the joining client.",
                    null,
                    new KeyValuePair<string, int>("Order", 10)));
        }

        // Classify a long-variant family from the base prefab name (regex group 1 + group 3, e.g.
        // "StructurePipeStraight", "StructureInsulatedPipeLiquidStraight", "StructureCableSuperHeavyStraightBurnt").
        // The order of the checks matters: the more-specific "InsulatedPipe..." / "PipeLiquid..." / "SuperHeavy..."
        // strings must be tested before the plain "Pipe..." / "Cable..." fallbacks.
        internal static LongPieceFamily ClassifyFamily(string baseName)
        {
            if (string.IsNullOrEmpty(baseName)) return LongPieceFamily.Unknown;
            // Insulated pipes (gas + liquid) -- before the plain-pipe check.
            if (baseName.IndexOf("InsulatedPipe", System.StringComparison.Ordinal) >= 0) return LongPieceFamily.InsulatedPipe;
            // Liquid pipe -- before the plain-pipe check.
            if (baseName.IndexOf("PipeLiquid", System.StringComparison.Ordinal) >= 0) return LongPieceFamily.LiquidPipe;
            // Chute.
            if (baseName.IndexOf("Chute", System.StringComparison.Ordinal) >= 0) return LongPieceFamily.Chute;
            // Super-heavy cable -- before the plain-cable check (the only cable tier with long variants).
            if (baseName.IndexOf("CableSuperHeavy", System.StringComparison.Ordinal) >= 0) return LongPieceFamily.SuperHeavyCable;
            // Plain gas pipe (not insulated, not liquid -- handled above).
            if (baseName.IndexOf("Pipe", System.StringComparison.Ordinal) >= 0) return LongPieceFamily.GasPipe;
            return LongPieceFamily.Unknown;
        }

        // True if the given long-piece family should be processed (its long variants stripped/hidden/rebuilt).
        // Unknown families (a future mod's long variant that matched the name pattern but none of the five
        // family substrings) are processed iff the master is on -- there is no per-family toggle for them,
        // and the safe default is the v1.0/v1.1 behaviour (strip it). A disabled per-family toggle leaves
        // that family exactly as vanilla ships it.
        internal static bool FamilyEnabled(LongPieceFamily family)
        {
            switch (family)
            {
                case LongPieceFamily.GasPipe:         return RemoveLongGasPipes?.Value ?? true;
                case LongPieceFamily.LiquidPipe:      return RemoveLongLiquidPipes?.Value ?? true;
                case LongPieceFamily.InsulatedPipe:   return RemoveLongInsulatedPipes?.Value ?? true;
                case LongPieceFamily.Chute:           return RemoveLongChutes?.Value ?? true;
                case LongPieceFamily.SuperHeavyCable: return RemoveLongSuperHeavyCables?.Value ?? true;
                default:                              return true;   // Unknown: process (master gate already passed)
            }
        }

        // Convenience accessors with safe fallbacks for the (rare) window before Bind() runs.
        internal static bool MasterEnabled => Enabled?.Value ?? true;
        internal static bool CableAlignmentEnabled => (Enabled?.Value ?? true) && (AlignStraightCables?.Value ?? true);
    }
}
