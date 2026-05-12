using BepInEx.Configuration;
using System.Collections.Generic;

namespace NetworkPuristPlus
{
    // The six long-piece families. A long variant's family is decided from its single-tile base
    // name (regex group 1 + group 3: "Structure<word>Straight" with the digit run removed, any "Burnt"
    // suffix kept). The classification gates everything downstream: LongVariantRegistry.Build() only
    // records a long variant in LongToBase if its family's toggle is on, and the strip / hide / rebuild /
    // build-time rewrite all key off LongToBase, so a disabled family is left exactly as vanilla ships it.
    internal enum LongPieceFamily
    {
        GasPipe,             // StructurePipeStraight       (NOT insulated, NOT liquid -- the game just calls it "Pipe"; the setting name spells out "Gas Pipe" so it's not confused with the liquid pipe)
        LiquidPipe,          // StructurePipeLiquidStraight
        InsulatedPipe,       // StructureInsulatedPipeStraight       (insulated gas pipe -- the game just calls it "Insulated Pipe"; the setting name spells out "Insulated Gas Pipe")
        InsulatedLiquidPipe, // StructureInsulatedPipeLiquidStraight
        Chute,               // StructureChuteStraight
        SuperHeavyCable,     // StructureCableSuperHeavyStraight (and ...Burnt)
        Unknown              // anything else that matched the name regex (a future mod's long variant)
    }

    // Server-authoritative settings for Network Purist Plus. All entries live in "Server - *" sections,
    // carry an Order tag (repo convention), open with "(Server-authoritative)", and carry the
    // ("RequireRestart", true) tag -- every one of them gates Prefab.OnPrefabsLoaded-time work
    // (LongVariantRegistry.Build() reads each value once, before any patch runs and before any join), so
    // toggling one while the game is running does nothing until you relaunch. The host's values are the
    // ones that take effect (the world rebuild + the prefab-time strip/hide are host-authoritative); a
    // joining client whose values differ from the host's is rejected at join time by the JoinValidator in
    // Plugin.cs (the prefab-time effects run at OnPrefabsLoaded, before any join, so each machine first
    // applies its own config -- the validator turns a mismatch into a clean rejection rather than a silent
    // desync of the build-kit option lists). Document: README "all players must run the same settings, like
    // the same version, and a change needs a game restart".
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
        // long variants are stripped, i.e. iff that family's toggle is on.) The gas / liquid distinction
        // is spelled out in the setting names ("Gas Pipe", "Insulated Gas Pipe") even though the game's own
        // term for the basic pipe is just "Pipe" -- a player reading the panel can't otherwise tell the
        // gas toggle from the liquid one. Names: "Gas Pipe", "Liquid Pipe", "Insulated Gas Pipe",
        // "Insulated Liquid Pipe", "Chute", "Super-Heavy Cable".
        internal static ConfigEntry<bool> RemoveLongGasPipes;              // StructurePipeStraight*               (the basic pipe -- "Gas Pipe" for clarity vs. the liquid pipe)
        internal static ConfigEntry<bool> RemoveLongLiquidPipes;           // StructurePipeLiquidStraight*
        internal static ConfigEntry<bool> RemoveLongInsulatedGasPipes;     // StructureInsulatedPipeStraight*      (insulated gas pipe only)
        internal static ConfigEntry<bool> RemoveLongInsulatedLiquidPipes;  // StructureInsulatedPipeLiquidStraight*
        internal static ConfigEntry<bool> RemoveLongChutes;
        internal static ConfigEntry<bool> RemoveLongSuperHeavyCables;

        // Cable-alignment toggle. When off: skip the on-load cable sweep, the Cable.OnRegistered re-roll,
        // and the AutomaticSetup of cable prefabs that have no long variant (StructureCableStraight /
        // StructureCableStraightH -- the cursor half of the alignment feature for those tiers). The
        // family-base AutomaticSetup for stripped pipe/chute/super-heavy-cable bases is the merge-with-tool
        // fix and stays tied to the per-family toggles, NOT this one.
        internal static ConfigEntry<bool> AlignStraightCables;

        // ConfigDescription builder: every NetworkPuristPlus entry carries an Order tag and the
        // ("RequireRestart", true) tag (StationeersLaunchPad surfaces a restart indicator from it),
        // mirroring PowerTransmitterPlus / PowerGridPlus.
        private static ConfigDescription Desc(string text, int order) =>
            new ConfigDescription(text, null,
                new KeyValuePair<string, int>("Order", order),
                new KeyValuePair<string, bool>("RequireRestart", true));

        // The restart caveat appended to most descriptions (the prefab-load-time work runs once at startup).
        private const string RestartNote =
            " Requires a full Stationeers restart to take effect: the build-kit strip / Stationpedia hide / " +
            "build-cursor setup runs once at startup, so toggling this while the game is running does nothing " +
            "until you relaunch.";

        // Bind every entry. Call from Plugin.Awake before anything reads a value.
        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind(
                "Server - Pieces", "Enable Network Purist Plus", true,
                Desc(
                    "(Server-authoritative) Master switch. When off, the mod does nothing: long-piece variants stay in the build kits and the Stationpedia, no long run is rebuilt on load, no cable is realigned, and the build cursor is left untouched -- as if the mod were not installed. Requires a full Stationeers restart to take effect: the mod's startup work runs once at game launch, so toggling this on/off mid-session changes nothing until you relaunch. All players on a server must have the same value for this; a joining client whose value differs from the host's is rejected with a clear message.",
                    10));

            RemoveLongGasPipes = config.Bind(
                "Server - Pieces", "Remove Long Gas Pipes", true,
                Desc(
                    "(Server-authoritative) Remove the long (3 / 5 / 10 segment) StructurePipeStraight variants (the basic pipe -- the game just calls it \"Pipe\"; named \"Gas Pipe\" here so it's not confused with the liquid pipe below): strip them from the pipe kit, hide them from the Stationpedia, rebuild placed long pipe runs from single tiles on load, and rewrite a long pipe placed mid-game into single tiles. No effect when the master toggle is off." + RestartNote + " All players on a server must have the same value; a mismatch rejects the joining client.",
                    20));

            RemoveLongLiquidPipes = config.Bind(
                "Server - Pieces", "Remove Long Liquid Pipes", true,
                Desc(
                    "(Server-authoritative) Remove the long (3 / 5 / 10 segment) StructurePipeLiquidStraight variants: strip them from the liquid-pipe kit, hide them from the Stationpedia, rebuild placed long liquid-pipe runs from single tiles on load, and rewrite a long liquid pipe placed mid-game into single tiles. No effect when the master toggle is off." + RestartNote + " All players on a server must have the same value; a mismatch rejects the joining client.",
                    30));

            RemoveLongInsulatedGasPipes = config.Bind(
                "Server - Pieces", "Remove Long Insulated Gas Pipes", true,
                Desc(
                    "(Server-authoritative) Remove the long (3 / 5 / 10 segment) StructureInsulatedPipeStraight variants (the insulated gas pipe -- the game calls it \"Insulated Pipe\"; named \"Insulated Gas Pipe\" here, and the insulated liquid pipe has its own toggle below): strip them from the insulated-pipe kit, hide them from the Stationpedia, rebuild placed long runs from single tiles on load, and rewrite one placed mid-game into single tiles. No effect when the master toggle is off." + RestartNote + " All players on a server must have the same value; a mismatch rejects the joining client.",
                    40));

            RemoveLongInsulatedLiquidPipes = config.Bind(
                "Server - Pieces", "Remove Long Insulated Liquid Pipes", true,
                Desc(
                    "(Server-authoritative) Remove the long (3 / 5 / 10 segment) StructureInsulatedPipeLiquidStraight variants (the insulated liquid pipe): strip them from the insulated-liquid-pipe kit, hide them from the Stationpedia, rebuild placed long runs from single tiles on load, and rewrite one placed mid-game into single tiles. No effect when the master toggle is off." + RestartNote + " All players on a server must have the same value; a mismatch rejects the joining client.",
                    50));

            RemoveLongChutes = config.Bind(
                "Server - Pieces", "Remove Long Chutes", true,
                Desc(
                    "(Server-authoritative) Remove the long (3 / 5 / 10 segment) StructureChuteStraight variants: strip them from the chute kit, hide them from the Stationpedia, rebuild placed long chute runs from single tiles on load (an item in transit inside a destroyed segment is lost), and rewrite a long chute placed mid-game into single tiles. No effect when the master toggle is off." + RestartNote + " All players on a server must have the same value; a mismatch rejects the joining client.",
                    60));

            RemoveLongSuperHeavyCables = config.Bind(
                "Server - Pieces", "Remove Long Super-Heavy Cables", true,
                Desc(
                    "(Server-authoritative) Remove the long (3 / 5 / 10 segment) StructureCableSuperHeavyStraight variants (the only cable tier that has long variants; includes the burnt damage-state siblings): strip them from the super-heavy cable kit, hide them from the Stationpedia, rebuild placed long super-heavy cable runs from single tiles on load, and rewrite one placed mid-game into single tiles. No effect when the master toggle is off." + RestartNote + " All players on a server must have the same value; a mismatch rejects the joining client.",
                    70));

            AlignStraightCables = config.Bind(
                "Server - Cables", "Align Straight Cables", true,
                Desc(
                    "(Server-authoritative) Re-roll every straight cable (all tiers) to one consistent orientation per run axis: existing runs when a save loads, freshly built ones the instant they register, and the build cursor for the straight-cable tiers that have no long variant. Cosmetic only -- connectivity, networks and colour are untouched. (The coloured band can still jump where a straight meets a corner cable -- corner pieces have a fixed band orientation and are not re-rolled.) The cable rotate key becomes preview-only. When off, none of that happens (a cable's roll is left wherever it was placed). The merge-with-tool fix for the stripped pipe / chute / super-heavy-cable kits is separate and is governed by the per-family toggles, not this one. No effect when the master toggle is off. Requires a full Stationeers restart to take effect: the on-load realignment and the build-cursor connection-type setup run once at startup, so toggling this mid-session has no effect until you relaunch. All players on a server must have the same value; a mismatch rejects the joining client.",
                    10));
        }

        // Classify a long-variant family from the base prefab name (regex group 1 + group 3, e.g.
        // "StructurePipeStraight", "StructureInsulatedPipeLiquidStraight", "StructureCableSuperHeavyStraightBurnt").
        // The order of the checks matters: the more-specific names are tested before the broader fallbacks.
        // "InsulatedPipeLiquid" contains "InsulatedPipe", which contains "Pipe", and "PipeLiquid" contains
        // "Pipe", so: insulated-liquid first, then insulated-gas, then liquid, then plain pipe.
        internal static LongPieceFamily ClassifyFamily(string baseName)
        {
            if (string.IsNullOrEmpty(baseName)) return LongPieceFamily.Unknown;
            // Insulated liquid pipe -- before the plain "InsulatedPipe" check (it is a superstring) and before "PipeLiquid".
            if (baseName.IndexOf("InsulatedPipeLiquid", System.StringComparison.Ordinal) >= 0) return LongPieceFamily.InsulatedLiquidPipe;
            // Insulated gas pipe -- before the plain-pipe check.
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
        // Unknown families (a future mod's long variant that matched the name pattern but none of the family
        // substrings) are processed iff the master is on -- there is no per-family toggle for them, and the
        // safe default is the v1.0/v1.1 behaviour (strip it). A disabled per-family toggle leaves that
        // family exactly as vanilla ships it.
        internal static bool FamilyEnabled(LongPieceFamily family)
        {
            switch (family)
            {
                case LongPieceFamily.GasPipe:             return RemoveLongGasPipes?.Value ?? true;
                case LongPieceFamily.LiquidPipe:          return RemoveLongLiquidPipes?.Value ?? true;
                case LongPieceFamily.InsulatedPipe:       return RemoveLongInsulatedGasPipes?.Value ?? true;
                case LongPieceFamily.InsulatedLiquidPipe: return RemoveLongInsulatedLiquidPipes?.Value ?? true;
                case LongPieceFamily.Chute:               return RemoveLongChutes?.Value ?? true;
                case LongPieceFamily.SuperHeavyCable:     return RemoveLongSuperHeavyCables?.Value ?? true;
                default:                                  return true;   // Unknown: process (master gate already passed)
            }
        }

        // Convenience accessors with safe fallbacks for the (rare) window before Bind() runs.
        internal static bool MasterEnabled => Enabled?.Value ?? true;
        internal static bool CableAlignmentEnabled => (Enabled?.Value ?? true) && (AlignStraightCables?.Value ?? true);
    }
}
