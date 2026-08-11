using System;
using System.Collections.Generic;
using System.Globalization;
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
// Aliased rather than imported. More than one loaded assembly carries a type called Settings, and
// resolving this one by bare name picked the wrong one once already (see RESEARCH.md, "Three traps
// on the way"). An alias pins it.
using Settings = Assets.Scripts.Serialization.Settings;

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

            // The epoch first, because it is what makes everything under it comparable to another
            // reading. Two readings whose epoch.session differs straddle a world or network
            // transition and are not describing the same situation, however alike they look.
            o.Raw("epoch", Epoch.Json());

            // Which instance this is, next, so a snapshot or a log line is attributable without
            // cross-referencing a port table. Two instances used to produce indistinguishable
            // /status blobs apart from the identity fields.
            o.Str("instanceName", string.IsNullOrEmpty(InstanceManifest.Name) ? "(unnamed)" : InstanceManifest.Name);
            o.Int("port", Plugin.EffectivePort);
            o.Raw("instance", InstanceManifest.DescribeJson());

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
            // 'role' first, deliberately. It is the one field downstream code should read, and it
            // exists so nothing has to re-derive the answer from the raw flags below. See Role().
            o.Str("role", Role());
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

            o.Bit("hosting", Hosting());
            o.Int("hostPort", HostPort());
            o.Raw("connectedClients", ConnectedClientsJson());

            // ---- save hygiene -------------------------------------------------
            // Four fields that together answer "where does this instance write, and will it host
            // again next boot". The last one is the important one and it is read from DISK, not
            // from the in-memory flag: a settings write persists the whole SettingData, so an
            // instance can be carrying StartLocalHost=true into its next launch while nothing in
            // this session shows it. See SettingsPath().
            string settingsPath = SettingsPath();
            string saveRoot = Router.EffectiveSaveRoot();
            o.Str("settingsPath", settingsPath);
            o.Str("savePathResolved", saveRoot);
            o.Bit("saveRootIsolated", Router.IsIsolatedSaveRoot(saveRoot));
            o.Raw("startLocalHostPersisted", TriState(PersistedStartLocalHost(settingsPath)));
            try { o.Bit("startLocalHostInMemory", Settings.CurrentData.StartLocalHost); } catch { }

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

            // appFocused used to be Application.isFocused, which reported true on two background
            // instances at the same time and so misled a caller deciding whether an input test was
            // worth running. It is now the window manager's answer, and the full block distinguishes
            // "background on the developer's desktop" from "on a desktop of my own", which
            // foregroundPid=0 alone could not. Reading the foreground window and the input desktop
            // activates nothing (see NativeWindow.cs).
            o.Raw("foreground", NativeWindow.DescribeJson());
            bool hasForeground;
            int foregroundPid;
            if (NativeWindow.TryIsForeground(out hasForeground, out foregroundPid))
                o.Bit("appFocused", hasForeground);
            else o.Raw("appFocused", "null");
            try { o.Bit("unityIsFocused", Application.isFocused); } catch { }

            // The one thing that decides whether synthetic input will do anything.
            try
            {
                o.Bit("gameplayInputGateOpen", GameplayGate.GateOpen);
                o.Str("gameplayInputShutReason", GameplayGate.ShutReason());
            }
            catch { }

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
                .Int("consoleDropped", ConsoleTap.Dropped)
                .Int("consoleTruncated", ConsoleTap.Truncated)
                .Raw("consoleTee", ConsoleTap.LimitsJson())
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

        /// <summary>
        ///     What this process IS: <c>menu</c>, <c>singlePlayer</c>, <c>joinedClient</c>,
        ///     <c>listenHost</c> or <c>dedicated</c>.
        ///
        ///     THE ONE PLACE THIS IS COMPUTED. Every consumer reads <c>/status.role</c> and nothing
        ///     re-derives it, because the raw flags read backwards for the case that matters most.
        ///     A listen host is <c>NetworkRole.Server</c> and therefore reports
        ///     <c>IsClient == false</c>, which is the opposite of the intuition that a hosting
        ///     player is "a client that is also a server". <c>IsActive</c>, <c>IsServer</c> and
        ///     <c>IsClient</c> are three views of one enum field, not three independent booleans.
        ///
        ///     The dedicated server and a listen host are the same <c>NetworkRole.Server</c> and are
        ///     one boolean apart: <c>GameManager.IsBatchMode</c>. See
        ///     <c>Research/GameSystems/ListenHost.md</c>.
        /// </summary>
        internal static string Role()
        {
            bool isClient, isServer;
            try
            {
                isClient = NetworkManager.IsClient;
                isServer = NetworkManager.IsServer;
            }
            catch { return "unknown"; }

            if (isServer)
            {
                bool batch = false;
                try { batch = GameManager.IsBatchMode; } catch { }
                return batch ? "dedicated" : "listenHost";
            }
            if (isClient) return "joinedClient";

            // NetworkRole.None. In a world that means single player; anywhere else it means this
            // process is not in a world at all.
            string state = "unknown";
            try { state = GameManager.GameState.ToString(); } catch { }
            return (state == "Running" || state == "Paused") ? "singlePlayer" : "menu";
        }

        /// <summary>
        ///     <c>NetworkServer.IsHosting</c>, the reliable post-condition for a host attempt.
        ///     <c>NetworkServer.Host()</c> no-ops from the main menu and gives up quietly after
        ///     three failed binds, so "the call returned" proves nothing.
        /// </summary>
        internal static bool Hosting()
        {
            try { return NetworkServer.IsHosting; } catch { return false; }
        }

        /// <summary>The port the RakNet listener is bound to, or 0 when not hosting.</summary>
        internal static int HostPort()
        {
            try { return Hosting() ? NetworkServer.HostPort : 0; } catch { return 0; }
        }

        /// <summary>
        ///     The <c>setting.xml</c> this process would write, which the launcher points at the
        ///     instance's own state folder with <c>-settingspath</c>. Reported so a reader can go
        ///     and look at the file that carries a persisted host flag.
        /// </summary>
        internal static string SettingsPath()
        {
            try { return Settings.SettingData.Path; } catch { return null; }
        }

        /// <summary>
        ///     Whether <c>StartLocalHost</c> is TRUE ON DISK, so this instance would host again on
        ///     its next launch. Null when the file is absent or does not carry the element.
        ///
        ///     Read from the file rather than from <c>Settings.CurrentData</c> on purpose, because
        ///     the two can disagree and only the file survives a restart. <c>/host</c> writes the
        ///     in-memory field and never saves, but three things still flush the whole
        ///     <c>SettingData</c> to disk behind it: any <c>settings &lt;name&gt; &lt;value&gt;</c>
        ///     console command (<c>SettingsCommand.OnValueChanged</c> calls
        ///     <c>Settings.SaveSettings()</c>), closing the in-game settings panel, and
        ///     <c>Settings.ValidateSavePath()</c> returning true at boot when the save path is not
        ///     writable. Any of those turns "this instance hosted once" into "this instance hosts
        ///     every time", and a joiner that silently came up as a host is a test that is
        ///     confidently wrong. Nothing inside <c>/host</c> can prevent them, so this reports them.
        ///
        ///     Parsed with a string scan rather than an XML reader: the file is written by
        ///     <c>XmlSerializer</c>, the element is a plain boolean, and a malformed file must
        ///     degrade to "unknown" rather than throw inside the one endpoint a harness always polls.
        /// </summary>
        internal static bool? PersistedStartLocalHost(string settingsPath)
        {
            try
            {
                if (string.IsNullOrEmpty(settingsPath) || !System.IO.File.Exists(settingsPath)) return null;
                string text = System.IO.File.ReadAllText(settingsPath);
                int at = text.IndexOf("<StartLocalHost>", StringComparison.OrdinalIgnoreCase);
                if (at < 0) return null;
                int from = at + "<StartLocalHost>".Length;
                int to = text.IndexOf('<', from);
                if (to < 0) return null;
                string value = text.Substring(from, to - from).Trim();
                if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)) return false;
                return null;
            }
            catch { return null; }
        }

        private static string TriState(bool? value)
            => value.HasValue ? (value.Value ? "true" : "false") : "null";

        /// <summary>
        ///     The server-side roster, which is what makes "did the second instance actually arrive"
        ///     assertable from the host without asking the joiner. Empty on anything that is not a
        ///     server: <c>NetworkBase.Clients</c> is a static list that a joined client never fills.
        ///
        ///     ClientId travels as a string, matching <c>/instance</c>, because a JSON number goes
        ///     through double on the reading side and silently loses precision above 2^53. A
        ///     truncated ClientId is exactly the failure these ids exist to detect.
        /// </summary>
        internal static string ConnectedClientsJson()
        {
            var rows = new List<string>();
            try
            {
                if (!NetworkManager.IsServer) return "[]";
                var clients = NetworkBase.Clients;
                if (clients == null) return "[]";
                foreach (var client in clients)
                {
                    if (client == null) continue;
                    var row = new Json.Obj();
                    try { row.Str("clientId", client.ClientId.ToString(CultureInfo.InvariantCulture)); } catch { }
                    try { row.Str("username", client.name); } catch { }
                    try { row.Str("state", client.state.ToString()); } catch { }
                    try { row.Bit("isHost", client.IsHost); } catch { }
                    try { row.Int("connectionId", client.connectionId); } catch { }
                    rows.Add(row.ToString());
                }
            }
            catch { return "[]"; }
            return "[" + string.Join(",", rows.ToArray()) + "]";
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
                    var row = new Json.Obj()
                        .Int("referenceId", thing.ReferenceId)
                        .Str("prefabName", thing.PrefabName)
                        .Str("type", thing.GetType().Name)
                        .Flt("distance", d)
                        .Vec("position", p)
                        .Bit("paintable", SafePaintable(thing))
                        .Int("customColorIndex", SafeColorIndex(thing));
                    // Whether it is loose or held, so a scan distinguishes an item on the ground
                    // from one in somebody's hand without a second request per row. The full answer,
                    // including whose hand and whether this process is the authority, is
                    // GET /thing?refId=<id>.
                    AppendParent(row, thing);
                    entries.Add(row.ToString());
                });
            }
            catch (Exception ex) { o.Str("scanError", ex.Message); }

            o.Bit("ok", true).Raw("epoch", Epoch.Json());
            o.Int("scanned", scanned).Int("count", entries.Count);
            o.Raw("things", "[" + string.Join(",", entries.ToArray()) + "]");
            return o.ToString();
        }

        /// <summary>
        ///     The one-line "is this in a slot, and whose" for a scan row. <c>ParentSlot</c> lives on
        ///     <c>DynamicThing</c>, so a Structure reports <c>inSlot:false</c> because it cannot be
        ///     in one, which is a different fact from an item lying on the floor.
        /// </summary>
        private static void AppendParent(Json.Obj row, Thing thing)
        {
            var dynamicThing = thing as DynamicThing;
            if (dynamicThing == null) { row.Bit("inSlot", false); return; }
            Slot slot = null;
            try { slot = dynamicThing.ParentSlot; } catch { }
            if (slot == null) { row.Bit("inSlot", false); return; }
            row.Bit("inSlot", true);
            try { row.Str("slotKey", slot.StringKey); } catch { }
            try { row.Int("parentId", slot.Parent == null ? 0 : slot.Parent.ReferenceId); } catch { }
            try { row.Str("parentName", slot.Parent == null ? null : slot.Parent.DisplayName); } catch { }
        }
    }
}
