using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using HarmonyLib;
using JetBrains.Annotations;
using System;
using System.Linq;
using UnityEngine;

namespace SprayPaintPlus
{
    /// <summary>
    /// Captures the hostId from ThingColorMessage.Process so we know which
    /// player triggered the current paint action.
    /// </summary>
    [HarmonyPatch(typeof(ThingColorMessage), nameof(ThingColorMessage.Process))]
    public class PaintHostIdTracker
    {
        [UsedImplicitly]
        public static void Prefix(ThingColorMessage __instance, long hostId)
        {
            SprayPaintHelpers.CurrentPaintingHostId = hostId;
        }
    }

    /// <summary>
    /// Prefix on OnServer.SetCustomColor — paints entire pipe/cable/chute networks.
    /// Reads modifier keys from per-player dictionary instead of KeyManager.
    /// </summary>
    [HarmonyPatch(typeof(OnServer), nameof(OnServer.SetCustomColor))]
    public class NetworkPainterPatch
    {
        private static bool _painting;

        [UsedImplicitly]
        public static void Prefix(Thing thing, int colorIndex)
        {
            if (_painting)
                return;

            if (!SprayPaintPlusPlugin.EnableNetworkPainting.Value)
                return;

            long hostId = SprayPaintHelpers.CurrentPaintingHostId;
            SprayPaintHelpers.CurrentPaintingHostId = -1;

            bool wantsSingle;
            bool ctrlHeld;

            if (hostId == -1 && NetworkManager.IsServer)
            {
                // Local host / single player — read keyboard directly.
                // The host's keyboard IS the local keyboard, no dictionary needed.
                bool shift = KeyManager.GetButton(KeyCode.LeftShift)
                          || KeyManager.GetButton(KeyCode.RightShift);
                bool ctrl = KeyManager.GetButton(KeyCode.LeftControl)
                         || KeyManager.GetButton(KeyCode.RightControl);
                bool invert = SprayPaintPlusPlugin.PaintSingleItemByDefault.Value;
                wantsSingle = shift != invert;
                ctrlHeld = ctrl;
            }
            else
            {
                // Remote client — read from per-player dictionary.
                SprayPaintHelpers.PlayerModifiers.TryGetValue(hostId, out byte modifiers);
                wantsSingle = (modifiers & 1) != 0;
                ctrlHeld = (modifiers & 2) != 0;
            }

            if (wantsSingle)
                return;

            _painting = true;
            try
            {
                PaintNetwork(thing, colorIndex, ctrlHeld);
            }
            finally
            {
                _painting = false;
            }
        }

        private static void PaintNetwork(Thing thing, int colorIndex, bool checkered)
        {
            if (SprayPaintPlusPlugin.NetworkPaintPipes.Value)
            {
                if (thing is HydroponicTray tray && tray.PipeNetwork?.StructureList != null)
                {
                    foreach (Pipe item in tray.PipeNetwork.StructureList.ToList())
                    {
                        // Fix #5b: Skip the original thing — vanilla paints it after the Prefix
                        if (ReferenceEquals(item, thing))
                            continue;
                        if (item is HydroponicTray && (!checkered || CheckeredCheck(thing, item)))
                            PaintSafe(item, colorIndex);
                    }
                    return;
                }

                if (thing is PassiveVent pv && pv.PipeNetwork?.StructureList != null)
                {
                    foreach (Pipe item in pv.PipeNetwork.StructureList.ToList())
                    {
                        if (ReferenceEquals(item, thing))
                            continue;
                        if (item is PassiveVent && (!checkered || CheckeredCheck(thing, item)))
                            PaintSafe(item, colorIndex);
                    }
                    return;
                }

                if (thing is Pipe pipe && pipe.PipeNetwork?.StructureList != null)
                {
                    foreach (Pipe item in pipe.PipeNetwork.StructureList.ToList())
                    {
                        if (ReferenceEquals(item, thing))
                            continue;
                        if (!(item is PassiveVent) && !(item is HydroponicTray)
                            && (!checkered || CheckeredCheck(thing, item)))
                            PaintSafe(item, colorIndex);
                    }
                    return;
                }
            }

            if (SprayPaintPlusPlugin.NetworkPaintCables.Value)
            {
                if (thing is Cable cable && cable.CableNetwork?.CableList != null)
                {
                    foreach (Cable item in cable.CableNetwork.CableList.ToList())
                    {
                        if (ReferenceEquals(item, thing))
                            continue;
                        if (!checkered || CheckeredCheck(thing, item))
                            PaintSafe(item, colorIndex);
                    }
                    return;
                }
            }

            if (SprayPaintPlusPlugin.NetworkPaintChutes.Value)
            {
                if (thing is Chute chute && chute.ChuteNetwork?.StructureList != null)
                {
                    foreach (Chute item in chute.ChuteNetwork.StructureList.ToList())
                    {
                        if (ReferenceEquals(item, thing))
                            continue;
                        if (!checkered || CheckeredCheck(thing, item))
                            PaintSafe(item, colorIndex);
                    }
                }
            }
        }

        /// <summary>
        /// Fix #8b: Wraps individual SetCustomColor calls so one destroyed item
        /// doesn't abort painting the rest of the network.
        /// </summary>
        private static void PaintSafe(Thing item, int colorIndex)
        {
            try
            {
                item.SetCustomColor(colorIndex);
            }
            catch (Exception e)
            {
                SprayPaintPlusPlugin.Log.LogWarning(
                    $"Failed to paint {item?.ReferenceId}: {e.Message}");
            }
        }

        /// <summary>
        /// 3D checkerboard pattern. Cast Mathf.Round to int before modulo
        /// to avoid float modulo imprecision.
        /// </summary>
        private static bool CheckeredCheck(Thing original, Thing target)
        {
            int one = ((int)Mathf.Round(Mathf.Abs(original.Position.x) * 2) % 2)
                   == ((int)Mathf.Round(Mathf.Abs(target.Position.x) * 2) % 2) ? 1 : 0;
            int two = ((int)Mathf.Round(Mathf.Abs(original.Position.y) * 2) % 2)
                   == ((int)Mathf.Round(Mathf.Abs(target.Position.y) * 2) % 2) ? 1 : 0;
            int three = ((int)Mathf.Round(Mathf.Abs(original.Position.z) * 2) % 2)
                     == ((int)Mathf.Round(Mathf.Abs(target.Position.z) * 2) % 2) ? 1 : 0;
            return (one + two + three) % 2 != 0;
        }
    }
}
