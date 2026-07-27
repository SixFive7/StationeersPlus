using Assets.Scripts.Networking;
using BepInEx.Configuration;
using System;

namespace SprayPaintPlus
{
    /// <summary>
    /// Every paired setting resolves here. Nothing outside this file reads a paired
    /// ConfigEntry.Value directly.
    ///
    /// The model: each capability has a client half and a server half, and the function
    /// works only when both allow it. A client half governs what you can DO, never what
    /// you SEE, so none of these accessors may be consulted from a sync receive path.
    ///
    /// Where each half comes from:
    ///
    ///   client half  -> always our own local entry
    ///   server half  -> our own local entry when we are the authority (single-player,
    ///                   host, dedicated server), the host's synced value when we are a
    ///                   remote client
    ///
    /// Single-player is the trap this shape exists to defuse. Solo reports
    /// NetworkRole.None, so both IsActive and IsServer are false, which is the same
    /// shape as the v1.2.2 infinite-spray bug where a bare !IsServer guard conflated
    /// solo with a remote client. It is ambiguous a second way too: in solo BOTH halves
    /// exist locally, so "return the local value" has two possible answers. Resolving
    /// the server half through ServerHalf() and always ANDing both makes solo behave
    /// exactly like a one-player server: both halves apply, either one disables.
    ///
    /// A remote client that has not yet received the join payload falls back to its own
    /// server-half entry. Permissive but harmless: the server independently enforces its
    /// own value at the trust boundary regardless of what any client believes.
    /// </summary>
    internal static class SettingsMerge
    {
        // ---- Synced host values -------------------------------------------------
        // Null until the join suffix lands. Remote clients only; never written on the
        // authority. Cleared on disconnect so a later solo session cannot read a stale
        // host value. See SettingsConfigSync.
        internal static ColorCyclingMode? SyncedColorCycling;
        internal static bool? SyncedColorPicking;
        internal static bool? SyncedGlowPaint;
        internal static bool? SyncedUnlimitedUses;
        internal static bool? SyncedExtraPaintable;
        internal static bool? SyncedNetworkPainting;
        internal static bool? SyncedNetworkPaintPipes;
        internal static bool? SyncedNetworkPaintCables;
        internal static bool? SyncedNetworkPaintChutes;
        internal static bool? SyncedNetworkPaintWalls;
        internal static bool? SyncedNetworkPaintRails;
        internal static bool? SyncedNetworkPaintLargeStructures;
        internal static bool? SyncedNetworkPaintElevators;
        internal static bool? SyncedNetworkPaintLadders;
        internal static bool? SyncedNetworkPaintStairs;
        internal static bool? SyncedNetworkPaintStairwells;

        internal static void ClearSynced()
        {
            SyncedColorCycling = null;
            SyncedColorPicking = null;
            SyncedGlowPaint = null;
            SyncedUnlimitedUses = null;
            SyncedExtraPaintable = null;
            SyncedNetworkPainting = null;
            SyncedNetworkPaintPipes = null;
            SyncedNetworkPaintCables = null;
            SyncedNetworkPaintChutes = null;
            SyncedNetworkPaintWalls = null;
            SyncedNetworkPaintRails = null;
            SyncedNetworkPaintLargeStructures = null;
            SyncedNetworkPaintElevators = null;
            SyncedNetworkPaintLadders = null;
            SyncedNetworkPaintStairs = null;
            SyncedNetworkPaintStairwells = null;
        }

        /// <summary>
        /// True when this process is the authority for settings: single-player, a
        /// listen host, or a dedicated server. False only on a remote client.
        /// </summary>
        internal static bool IsAuthority => !NetworkManager.IsActive || NetworkManager.IsServer;

        private static bool ServerHalf(ConfigEntry<bool> local, bool? synced)
        {
            if (local == null) return true;
            if (IsAuthority) return local.Value;
            return synced ?? local.Value;
        }

        private static ColorCyclingMode ServerHalf(ConfigEntry<ColorCyclingMode> local, ColorCyclingMode? synced)
        {
            if (local == null) return ColorCyclingMode.AllColors;
            if (IsAuthority) return local.Value;
            return synced ?? local.Value;
        }

        // ---- Effective values, evaluated locally --------------------------------
        // These are what the local machine may do. On a remote client they merge the
        // synced host half; on the authority they merge the local server half.

        internal static ColorCyclingMode EffectiveColorCycling
        {
            get
            {
                var client = SprayPaintPlusPlugin.ClientColorCycling?.Value ?? ColorCyclingMode.AllColors;
                var server = ServerHalf(SprayPaintPlusPlugin.ServerColorCycling, SyncedColorCycling);
                // Stricter wins. Relies on the ladder ordering in ColorCyclingMode.
                return (ColorCyclingMode)Math.Min((int)client, (int)server);
            }
        }

        internal static bool EffectiveColorPicking =>
            (SprayPaintPlusPlugin.ClientColorPicking?.Value ?? true)
            && ServerHalf(SprayPaintPlusPlugin.ServerColorPicking, SyncedColorPicking)
            // The mode outranks the picking toggle: eyedropping IS changing the can's
            // color, so a can that cannot change color cannot be eyedropped onto either.
            && EffectiveColorCycling != ColorCyclingMode.CannotChange;

        internal static bool EffectiveGlowPaint =>
            (SprayPaintPlusPlugin.ClientGlowPaint?.Value ?? true)
            && ServerHalf(SprayPaintPlusPlugin.ServerGlowPaint, SyncedGlowPaint);

        internal static bool EffectiveExtraPaintable =>
            (SprayPaintPlusPlugin.ClientExtraPaintableStructures?.Value ?? true)
            && ServerHalf(SprayPaintPlusPlugin.ServerExtraPaintableStructures, SyncedExtraPaintable);

        // ---- Server-side per-player merge ---------------------------------------
        // Used by the paint path, which runs on the authority and must merge the ACTING
        // player's client half rather than the local machine's. The acting player's bits
        // arrive via PaintModifierMessage; the local player writes their own bits into
        // the same dictionary so host and single-player take an identical lookup path.

        internal static bool ServerAllows(ConfigEntry<bool> serverEntry, bool? synced, long humanId, int bit)
        {
            if (!ServerHalf(serverEntry, synced)) return false;
            return PlayerPrefs.Has(humanId, bit);
        }

        /// <summary>
        /// Bit positions inside the per-player preference mask carried by
        /// PaintModifierMessage and stored in SprayPaintHelpers.PlayerModifiers.
        /// Bits 0 and 1 predate v1.11.0 and keep their meaning.
        /// Appending a bit is safe; renumbering one is not.
        /// </summary>
        internal static class PlayerPrefs
        {
            internal const int SingleItem = 0;
            internal const int Checkered = 1;
            internal const int NetworkPainting = 2;
            internal const int Pipes = 3;
            internal const int Cables = 4;
            internal const int Chutes = 5;
            internal const int Walls = 6;
            internal const int Rails = 7;
            internal const int LargeStructures = 8;
            internal const int Elevators = 9;
            internal const int Ladders = 10;
            internal const int Stairs = 11;
            internal const int Stairwells = 12;
            internal const int GlowPaint = 13;
            internal const int UnlimitedUses = 14;

            internal static bool Has(long humanId, int bit)
            {
                // Absent means the player never reported preferences. Default to
                // allowing: the server's own setting has already been applied by the
                // caller, and an unreported client should not be silently restricted.
                if (!SprayPaintHelpers.PlayerModifiers.TryGetValue(humanId, out ushort mask))
                    return true;
                return (mask & (1 << bit)) != 0;
            }

            /// <summary>
            /// Packs this machine's own client-half preferences. Bits 0 and 1 are the
            /// live modifier keys and are supplied by the caller.
            /// </summary>
            internal static ushort PackLocal(bool singleItem, bool checkered)
            {
                ushort mask = 0;
                if (singleItem) mask |= 1 << SingleItem;
                if (checkered) mask |= 1 << Checkered;
                Set(ref mask, NetworkPainting, SprayPaintPlusPlugin.ClientNetworkPainting);
                Set(ref mask, Pipes, SprayPaintPlusPlugin.ClientNetworkPaintPipes);
                Set(ref mask, Cables, SprayPaintPlusPlugin.ClientNetworkPaintCables);
                Set(ref mask, Chutes, SprayPaintPlusPlugin.ClientNetworkPaintChutes);
                Set(ref mask, Walls, SprayPaintPlusPlugin.ClientNetworkPaintWalls);
                Set(ref mask, Rails, SprayPaintPlusPlugin.ClientNetworkPaintRails);
                Set(ref mask, LargeStructures, SprayPaintPlusPlugin.ClientNetworkPaintLargeStructures);
                Set(ref mask, Elevators, SprayPaintPlusPlugin.ClientNetworkPaintElevators);
                Set(ref mask, Ladders, SprayPaintPlusPlugin.ClientNetworkPaintLadders);
                Set(ref mask, Stairs, SprayPaintPlusPlugin.ClientNetworkPaintStairs);
                Set(ref mask, Stairwells, SprayPaintPlusPlugin.ClientNetworkPaintStairwells);
                Set(ref mask, GlowPaint, SprayPaintPlusPlugin.ClientGlowPaint);
                Set(ref mask, UnlimitedUses, SprayPaintPlusPlugin.ClientUnlimitedSprayPaintUses);
                return mask;
            }

            private static void Set(ref ushort mask, int bit, ConfigEntry<bool> entry)
            {
                if (entry?.Value ?? true) mask |= (ushort)(1 << bit);
            }
        }
    }
}
