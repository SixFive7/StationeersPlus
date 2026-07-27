using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using System.Collections.Generic;
using UnityEngine;

namespace SprayPaintPlus
{
    internal static class SprayPaintHelpers
    {
        // Network flag for custom spray can color sync (bit 12 = GenericFlag2)
        internal const ushort PaintColorNetworkFlag = 0x1000;

        // Tracks selected color index per spray can ReferenceId.
        // Entries are removed when a spray can is destroyed (see CleanupPatches).
        internal static readonly Dictionary<long, int> SprayCanColors = new Dictionary<long, int>();

        // Tracks each player's full client-half preference mask, keyed by that player's
        // Human ReferenceId. Not just the two modifier keys any more: as of v1.11.0 this
        // carries every client-side setting the server has to merge before acting on a
        // paint (the eleven network-painting toggles, glow paint, unlimited uses)
        // alongside the two live modifier bits it has always carried.
        //
        // Bit positions live in SettingsMerge.PlayerPrefs and are the single source of
        // truth for the layout; do not restate them here. Bits 0 and 1 predate v1.11.0
        // and keep their meaning (single-item paint with the invert already applied, and
        // checkered pattern). Read through SettingsMerge.PlayerPrefs.Has, which treats an
        // absent player as permissive.
        //
        // Server receives remote players' masks via PaintModifierMessage; the local
        // player's own mask is also written here so host/single-player takes the same
        // lookup path as remote clients. Entries for remote players are removed on
        // disconnect (see CleanupPatches).
        internal static readonly Dictionary<long, ushort> PlayerModifiers = new Dictionary<long, ushort>();

        // How many SettingBlockedNotice messages each player has already been sent this
        // session, keyed by that player's Human ReferenceId and then by function name.
        //
        // The client caps what it DISPLAYS at three per function (WarningNotifier), which
        // does nothing about the traffic: the server detects a block once per stroke and
        // painting repeats several times a second, so a player working along a pipe run
        // on a server with pipes disabled produced a message per stroke, forever. This is
        // the same cap applied on the sending side, so the fourth detection costs nothing
        // on the wire. SettingBlockedNotice owns the counting; the rows live here because
        // this is where the server keeps its per-player state.
        //
        // Rows are removed per player on disconnect, next to PlayerModifiers (see
        // CleanupPatches), and the whole table is dropped when this machine leaves the
        // session (see LeaveGameResetPatch), which is what makes the cap per session on
        // both sides.
        internal static readonly Dictionary<long, Dictionary<string, int>> BlockedNoticeCounts =
            new Dictionary<long, Dictionary<string, int>>();

        // ReferenceId of the Human whose paint action is currently being processed.
        // Set by PaintAttackerTracker_Local/_Remote prefixes before the paint
        // reaches OnServer.SetCustomColor, read in NetworkPainterPatch.Prefix,
        // reset to -1 on tracker postfix / after use to prevent stale reads.
        internal static long CurrentPaintingHumanId = -1;

        // Cache: maps paint material to thumbnail sprite, built on first use.
        private static Dictionary<Material, Sprite> _thumbnailCache;

        public static int GetPaintColorIndex(Material paintMaterial)
        {
            var colors = GameManager.Instance?.CustomColors;
            if (colors == null)
                return 0;
            for (int i = 0; i < colors.Count; i++)
            {
                if (colors[i].Normal == paintMaterial)
                    return i;
            }
            SprayPaintPlusPlugin.Log.LogWarning(
                $"Unknown paint material '{paintMaterial?.name}', defaulting to color index 0");
            return 0;
        }

        public static Material GetPaintColor(int colorIndex)
        {
            var colors = GameManager.Instance?.CustomColors;
            if (colors == null || colors.Count == 0)
                return null;
            if (colorIndex < 0 || colorIndex >= colors.Count)
            {
                SprayPaintPlusPlugin.Log.LogWarning(
                    $"Color index {colorIndex} out of range [0, {colors.Count}), defaulting to 0");
                colorIndex = 0;
            }
            return colors[colorIndex].Normal;
        }

        public static int GetSprayCanColorIndex(SprayCan sprayCan)
        {
            if (SprayCanColors.TryGetValue(sprayCan.ReferenceId, out int index))
                return index;
            return GetPaintColorIndex(sprayCan.PaintMaterial);
        }

        /// <summary>
        /// Updates the spray can's visual appearance WITHOUT changing PrefabHash/PrefabName.
        /// </summary>
        public static void UpdateSprayCanVisual(SprayCan sprayCan, int colorIndex)
        {
            var paintMaterial = GetPaintColor(colorIndex);
            if (paintMaterial == null)
                return;

            sprayCan.PaintableMaterial = paintMaterial;
            sprayCan.PaintMaterial = paintMaterial;

            if (sprayCan.GetComponent<MeshRenderer>() is MeshRenderer mr)
                mr.sharedMaterial = paintMaterial;

            sprayCan.Thumbnail = GetThumbnailForMaterial(paintMaterial);
            SprayCanColors[sprayCan.ReferenceId] = colorIndex;
        }

        /// <summary>
        /// Server-side update: changes visual and sets the broadcast flag.
        /// Callers must already be on the server.
        /// </summary>
        public static void UpdateSprayCanServer(SprayCan sprayCan, int colorIndex)
        {
            UpdateSprayCanVisual(sprayCan, colorIndex);
            sprayCan.NetworkUpdateFlags |= PaintColorNetworkFlag;
        }

        private static Sprite GetThumbnailForMaterial(Material paintMaterial)
        {
            if (_thumbnailCache == null)
            {
                _thumbnailCache = new Dictionary<Material, Sprite>();
                foreach (Thing thing in Prefab.AllPrefabs)
                {
                    if (thing is SprayCan prefabCan && prefabCan.PaintMaterial != null)
                        _thumbnailCache[prefabCan.PaintMaterial] = prefabCan.Thumbnail;
                }
            }

            _thumbnailCache.TryGetValue(paintMaterial, out Sprite thumbnail);
            return thumbnail;
        }
    }
}
