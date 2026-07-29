using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Inventory;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using HarmonyLib;
using UnityEngine;
using GameManager = Assets.Scripts.GameManager;
using NetworkClient = Assets.Scripts.NetworkClient;

namespace ClientDriver
{
    /// <summary>
    /// Reads live client state into JSON. Everything here runs on the main thread and
    /// is defensive: at the main menu almost every game singleton is null, and a
    /// state endpoint that throws there is useless precisely when you most want to
    /// poll it.
    /// </summary>
    internal static class StateReporter
    {
        internal static string Status()
        {
            var o = new Json.Obj();
            o.Bit("ok", true);
            o.Str("plugin", Plugin.PluginName + " " + Plugin.PluginVersion);
            o.Int("frame", Time.frameCount);
            o.Dbl("realtime", Time.realtimeSinceStartup);

            // ---- session -----------------------------------------------------
            string gameState = "unknown";
            try { gameState = GameManager.GameState.ToString(); } catch { }
            o.Str("gameState", gameState);
            o.Str("phase", Phase(gameState));

            try { o.Bit("gameInitialized", GameManager.IsInitialized); } catch { o.Raw("gameInitialized", "null"); }
            try { o.Bit("batchMode", GameManager.IsBatchMode); } catch { }
            try { o.Bit("runSimulation", GameManager.RunSimulation); } catch { }
            try { o.Str("gameVersion", GameManager.GetGameVersion()); } catch { }

            // ---- network -----------------------------------------------------
            try
            {
                o.Str("networkRole", NetworkManager.NetworkRole.ToString());
                o.Str("networkState", NetworkManager.NetworkState.ToString());
                o.Bit("isClient", NetworkManager.IsClient);
                o.Bit("isServer", NetworkManager.IsServer);
                o.Bit("isActive", NetworkManager.IsActive);
                o.Int("localClientId", (long)NetworkManager.LocalClientId);
                o.Str("username", NetworkManager.Username);
                o.Int("playersInGame", NetworkManager.TotalPlayersInGame);
            }
            catch (Exception ex) { o.Str("networkError", ex.Message); }

            try
            {
                o.Str("serverAddress", NetworkClient.Address);
                o.Str("serverPort", NetworkClient.Port);
                o.Str("connectionMethod", NetworkClient.ConnectionMethod.ToString());
            }
            catch { }

            // ---- world -------------------------------------------------------
            try
            {
                o.Str("worldName", WorldManager.CurrentWorldName);
                o.Str("worldId", WorldManager.CurrentWorldId);
                o.Bit("worldPaused", WorldManager.IsGamePaused);
                o.Bit("worldInitialized", WorldManager.IsInitialized);
            }
            catch { }

            // Plugin count doubles as a "did StationeersLaunchPad finish loading
            // mods" signal. It sits at 2 (this plugin plus StationeersLaunchPad
            // itself) when the loader has failed, which happens on a transient Steam
            // Workshop query error and parks the client on the LaunchPad error
            // screen forever. Worth having in the one endpoint a harness always polls.
            try { o.Int("loadedPluginCount", ConfigAccess.AllPluginGuids().Count); } catch { }
            try { o.Bit("consoleOpen", ConsoleWindow.IsOpen); } catch { }
            try { o.Bit("cursorVisible", Cursor.visible); } catch { }
            try { o.Bit("appFocused", Application.isFocused); } catch { }

            // ---- player ------------------------------------------------------
            o.Raw("player", PlayerJson());

            // ---- driver ------------------------------------------------------
            o.Raw("driver", new Json.Obj()
                .Int("pumpFrames", MainThreadPump.FramesSeen)
                .Int("pumpItems", MainThreadPump.ItemsRun)
                .Str("lastPump", MainThreadPump.LastPumpSource)
                .Bit("fallbackPumpUsed", MainThreadPump.FallbackPumpUsed)
                .Int("pumpObjectCreations", MainThreadPump.InstanceCreations)
                .Int("pluginDestroyCount", Plugin.DestroyCount)
                .Bit("serverRunning", Plugin.Server != null && Plugin.Server.Running)
                .Int("serverRequests", Plugin.Server == null ? 0 : Plugin.Server.Requests)
                .Str("serverLastAcceptError", Plugin.Server == null ? null : Plugin.Server.LastAcceptError)
                .Bit("consoleTapPatched", ConsoleTap.ConsolePatchApplied)
                .Bit("bepInExTapAttached", ConsoleTap.BepInExListenerAttached)
                .Bit("inputEnabled", VirtualInput.Enabled)
                .Str("heldKeys", VirtualInput.DescribeHeld())
                .Int("keyOverrides", VirtualInput.KeyOverrides)
                .Int("scrollOverrides", VirtualInput.ScrollOverrides)
                .Int("consoleNextSeq", ConsoleTap.NextSeq)
                .ToString());

            return o.ToString();
        }

        private static string Phase(string gameState)
        {
            switch (gameState)
            {
                case "None": return "menu";
                case "Joining": return "joining";
                case "Loading": return "loading";
                case "Waiting": return "waiting";
                case "Paused": return "paused";
                case "Running": return "inWorld";
                default: return "unknown";
            }
        }

        internal static string PlayerJson()
        {
            var o = new Json.Obj();
            Human human = null;
            try { human = Human.LocalHuman; } catch { }

            if (human == null)
            {
                o.Bit("present", false);
                return o.ToString();
            }

            o.Bit("present", true);
            try { o.Int("referenceId", human.ReferenceId); } catch { }
            try { o.Str("displayName", human.DisplayName); } catch { }
            try { o.Vec("position", human.ThingTransformPosition); } catch { }
            try
            {
                var rot = human.ThingTransformRotation.eulerAngles;
                o.Vec("rotationEuler", rot);
            }
            catch { }
            try { o.Bit("dead", human.IsDead); } catch { }

            try
            {
                var cam = CameraController.Instance;
                if (cam != null)
                {
                    o.Flt("lookPitch", cam.RotationX);
                    o.Flt("lookYaw", cam.RotationY);
                }
                o.Vec("cameraPosition", CameraController.CameraPosition);
                o.Vec("cameraOrigin", CameraController.CameraOrigin);
                o.Bit("thirdPerson", CameraController.IsThirdPerson);
            }
            catch { }

            // hands
            try
            {
                var im = InventoryManager.Instance;
                var activeSlot = InventoryManager.ActiveHandSlot;
                o.Raw("activeHand", DescribeSlot(activeSlot));
                Slot inactive = null;
                if (im != null && im.InactiveHand != null) inactive = im.InactiveHand.Slot;
                o.Raw("inactiveHand", DescribeSlot(inactive));
                if (im != null && im.ActiveHand != null) o.Int("activeHandSlotId", im.ActiveHand.SlotId);
            }
            catch (Exception ex) { o.Str("handsError", ex.Message); }

            // cursor target
            try
            {
                var target = CursorManager.CursorThing;
                if (target == null) o.Raw("cursorTarget", "null");
                else
                {
                    o.Raw("cursorTarget", new Json.Obj()
                        .Int("referenceId", target.ReferenceId)
                        .Str("prefabName", target.PrefabName)
                        .Str("displayName", SafeDisplayName(target))
                        .Str("type", target.GetType().Name)
                        .Bit("paintable", SafePaintable(target))
                        .Int("customColorIndex", SafeColorIndex(target))
                        .Vec("position", target.ThingTransformPosition)
                        .ToString());
                }
            }
            catch (Exception ex) { o.Str("cursorError", ex.Message); }

            return o.ToString();
        }

        private static string SafeDisplayName(Thing t)
        {
            try { return t.DisplayName; } catch { return null; }
        }

        private static bool SafePaintable(Thing t)
        {
            try { return t.IsPaintable; } catch { return false; }
        }

        private static int SafeColorIndex(Thing t)
        {
            try
            {
                var swatch = t.CustomColor;
                return swatch == null ? -1 : swatch.Index;
            }
            catch { return -1; }
        }

        internal static string DescribeSlot(Slot slot)
        {
            if (slot == null) return "null";
            DynamicThing occupant = null;
            try { occupant = slot.Get(); } catch { }
            if (occupant == null)
                return new Json.Obj().Bit("empty", true).ToString();

            var o = new Json.Obj();
            o.Bit("empty", false);
            try { o.Int("referenceId", occupant.ReferenceId); } catch { }
            try { o.Str("prefabName", occupant.PrefabName); } catch { }
            try { o.Str("displayName", occupant.DisplayName); } catch { }
            o.Str("type", occupant.GetType().Name);

            var can = occupant as SprayCan;
            if (can != null)
            {
                o.Bit("isSprayCan", true);
                o.Int("paintColorIndex", PaintColorIndex(can.PaintMaterial));
                o.Str("paintMaterial", can.PaintMaterial == null ? null : can.PaintMaterial.name);
                o.Str("paintColorName", ColorSwatchName(PaintColorIndex(can.PaintMaterial)));
            }
            var gun = occupant as SprayGun;
            if (gun != null)
            {
                o.Bit("isSprayGun", true);
                try
                {
                    var inner = gun.SprayCan;
                    o.Int("loadedCanColorIndex", inner == null ? -1 : PaintColorIndex(inner.PaintMaterial));
                }
                catch { }
            }
            try { o.Int("quantity", occupant is Stackable st ? st.Quantity : 1); } catch { }
            return o.ToString();
        }

        /// <summary>
        /// Resolves a paint Material back to its ColorSwatch index the same way the
        /// game does. GameManager.GetColorIndex walks GameManager.Instance.CustomColors
        /// comparing against each swatch's Normal material.
        /// </summary>
        internal static int PaintColorIndex(Material material)
        {
            if (material == null) return -1;
            try { return GameManager.GetColorIndex(material); } catch { return -1; }
        }

        internal static string ColorSwatchName(int index)
        {
            try
            {
                var gm = GameManager.Instance;
                if (gm == null || gm.CustomColors == null) return null;
                if (index < 0 || index >= gm.CustomColors.Count) return null;
                var swatch = gm.CustomColors[index];
                return swatch == null ? null : swatch.Name;
            }
            catch { return null; }
        }

        // ---- colour catalogue -----------------------------------------------

        internal static string Colors()
        {
            var o = new Json.Obj();
            o.Bit("ok", true);
            var entries = new List<string>();
            try
            {
                var gm = GameManager.Instance;
                if (gm != null && gm.CustomColors != null)
                {
                    for (int i = 0; i < gm.CustomColors.Count; i++)
                    {
                        var s = gm.CustomColors[i];
                        entries.Add(new Json.Obj()
                            .Int("index", i)
                            .Int("swatchIndex", s == null ? -1 : s.Index)
                            .Str("name", s == null ? null : s.Name)
                            .Str("normalMaterial", (s == null || s.Normal == null) ? null : s.Normal.name)
                            .ToString());
                    }
                }
            }
            catch (Exception ex) { o.Str("error", ex.Message); }
            o.Raw("colors", "[" + string.Join(",", entries.ToArray()) + "]");
            o.Int("count", entries.Count);
            return o.ToString();
        }

        // ---- loaded plugins --------------------------------------------------

        /// <summary>
        /// Lists every plugin found by assembly scan, not just the ones BepInEx's
        /// Chainloader knows about. On this client the Chainloader only ever holds
        /// the two plugins under BepInEx/plugins/; every Workshop mod arrives
        /// through StationeersLaunchPad and is invisible to it.
        /// </summary>
        internal static string Plugins()
        {
            var o = new Json.Obj();
            o.Bit("ok", true);
            var entries = new List<string>();
            var chainloaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var kv in BepInEx.Bootstrap.Chainloader.PluginInfos) chainloaded.Add(kv.Key);
            }
            catch { }

            try
            {
                foreach (var row in ConfigAccess.AllPluginGuids())
                {
                    var parts = row.Split('\t');
                    if (parts.Length < 5) continue;
                    entries.Add(new Json.Obj()
                        .Str("guid", parts[0])
                        .Str("name", parts[1])
                        .Str("version", parts[2])
                        .Str("type", parts[3])
                        .Str("assembly", parts[4])
                        .Bit("chainloaded", chainloaded.Contains(parts[0]))
                        .ToString());
                }
            }
            catch (Exception ex) { o.Str("error", ex.Message); }

            o.Raw("plugins", "[" + string.Join(",", entries.ToArray()) + "]");
            o.Int("count", entries.Count);
            o.Int("chainloadedCount", chainloaded.Count);
            return o.ToString();
        }

        // ---- nearby things ---------------------------------------------------

        internal static string Nearby(float radius, string typeFilter, int limit)
        {
            var o = new Json.Obj();
            var human = Human.LocalHuman;
            if (human == null) return o.Bit("ok", false).Str("error", "no local player").ToString();

            Vector3 origin = human.ThingTransformPosition;
            var entries = new List<string>();
            int scanned = 0;

            // OcclusionManager.AllThings is a ConcurrentDensePool<Thing>, whose
            // enumerator is a ref struct and so cannot be used in a foreach that
            // captures anything. ForEach(Action<T>) is the supported traversal.
            try
            {
                OcclusionManager.AllThings.ForEach(thing =>
                {
                    scanned++;
                    if (thing == null) return;
                    if (limit > 0 && entries.Count >= limit) return;
                    Vector3 p;
                    try { p = thing.ThingTransformPosition; } catch { return; }
                    float d = Vector3.Distance(origin, p);
                    if (d > radius) return;
                    if (!string.IsNullOrEmpty(typeFilter) &&
                        thing.GetType().Name.IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) < 0 &&
                        (thing.PrefabName ?? "").IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) < 0) return;
                    entries.Add(new Json.Obj()
                        .Int("referenceId", thing.ReferenceId)
                        .Str("prefabName", thing.PrefabName)
                        .Str("type", thing.GetType().Name)
                        .Flt("distance", d)
                        .Vec("position", p)
                        .Bit("paintable", SafePaintable(thing))
                        .Int("customColorIndex", SafeColorIndex(thing))
                        .ToString());
                });
            }
            catch (Exception ex) { o.Str("scanError", ex.Message); }

            o.Bit("ok", true).Int("scanned", scanned).Int("count", entries.Count);
            o.Raw("things", "[" + string.Join(",", entries.ToArray()) + "]");
            return o.ToString();
        }
    }
}
