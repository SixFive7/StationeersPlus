using Assets.Scripts;
using Assets.Scripts.Networking;
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

        // Tracks modifier key state per player connectionId.
        // Bit 0 = wants single-item paint, Bit 1 = wants checkered pattern.
        // Entries are removed when a player disconnects (see CleanupPatches).
        internal static readonly Dictionary<long, byte> PlayerModifiers = new Dictionary<long, byte>();

        // Tracks which hostId triggered the current OnServer.SetCustomColor call.
        // Set in PaintHostIdTracker.Prefix, read in NetworkPainterPatch.Prefix,
        // reset to -1 after use to prevent stale reads.
        internal static long CurrentPaintingHostId = -1;

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
        /// Server-side update: changes visual + flags for network broadcast.
        /// </summary>
        public static void UpdateSprayCanServer(SprayCan sprayCan, int colorIndex)
        {
            UpdateSprayCanVisual(sprayCan, colorIndex);
            if (NetworkManager.IsServer)
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
