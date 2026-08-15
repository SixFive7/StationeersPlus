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

namespace TestRig
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
                // THESE THREE ARE RAW, AND THEY READ BACKWARDS FOR A LISTEN HOST. Assert on
                // 'role' and 'hosting' above, never on these. A listen host is
                // NetworkRole.Server, so it answers isClient=false and isServer=true even though
                // it has a player character and is by every other measure a client; the dedicated
                // server answers identically and is one boolean apart (GameManager.IsBatchMode).
                // IsActive/IsClient/IsServer are three views of one enum field, not three
                // independent booleans: Assembly-CSharp NetworkManager, IsActive => NetworkRole !=
                // None, IsClient => NetworkRole == Client, IsServer => NetworkRole == Server. They
                // are reported because a raw reading is sometimes what a diagnosis needs, not
                // because a caller should branch on them. See Role().
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
            // Only present when the roster read threw, and it exists because an empty array is
            // indistinguishable from "nobody is connected". That ambiguity is what made a real
            // join look like a failed one for a whole playtest session, so a roster that could
            // not be read now says so beside itself instead of answering [] and looking fine.
            if (RosterError != null) o.Str("connectedClientsError", RosterError);

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

            // ---- host --------------------------------------------------------
            //
            // One plugin now loads into a game client and into the dedicated server, and the two
            // do not have the same capabilities. This block says which process answered, how that
            // was decided, and which main-thread marshal is in force. Without it a caller reading
            // player:{present:false} cannot tell "at the main menu" from "this host has no player
            // character at all", and the refusals in HostGuard would be the only clue.
            o.Raw("host", HostProfile.Json());

            // ---- scenarios ---------------------------------------------------
            o.Str("scenariosArmed", Scenarios.ScenarioHost.Effective);
            o.Str("scenariosArmedSource", Scenarios.ScenarioHost.Source);
            o.Int("simTicksSeen", Scenarios.Dispatcher.TicksSeen);

            // ---- driver ------------------------------------------------------
            o.Raw("driver", new Json.Obj()
                .Int("pumpFrames", MainThreadPump.FramesSeen)
                .Int("pumpItems", MainThreadPump.ItemsRun)
                .Str("lastPump", MainThreadPump.LastPumpSource)
                // NOT a pump. True once the ElectricityTick postfix has fired, which is the only
                // honest signal that the simulation is actually ticking: measured, a dedicated
                // server started with -new and force-unpause off ran ZERO ticks for 287 s.
                .Bit("simTickHookFired", MainThreadPump.FallbackPumpUsed)
                // The pump strategy, named. mainThreadDrain executes queued work from the
                // GameManager.Update postfix (both builds, thread 1, ~24 Hz, pause-independent);
                // unityMainThreadDispatcher is the backstop through the game's own marshal. The
                // ElectricityTick postfix never drains: its thread is never 1.
                .Str("pumpStrategy", MainThreadPump.StrategyName)
                .Bit("pumpMarshalAvailable", MainThreadPump.MarshalAvailable)
                .Bit("pumpDrainReady", MainThreadPump.DrainReady)
                .Bit("pumpGameMarshalReady", MainThreadPump.GameMarshalReady)
                .Str("pumpHooks", MainThreadPump.HookReport())
                .Str("pumpNote", MainThreadPump.StrategyNote)
                .Int("mainThreadDrains", MainThreadPump.MainThreadDrains)
                // The boot-window pump. GameManager.Update runs at 0.11-0.16/s until
                // GameState.Running, so during the 80-90 s boot these fields are the whole reason
                // the control plane answers at all.
                //
                // pumpHostCreatedAtFrame is NOT the same number on the two halves, and expecting 0
                // everywhere was wrong: the object is created at the first sceneLoaded callback,
                // which is 282 ms and frame 0 on a client but over a thousand frames in on the
                // dedicated server, where there is no splash or menu scene and the first load is
                // the mod-content load. It is a VARIABLE, not a constant: 1834 in the instrumented
                // run and 1635 in the first real one on the same game build, because what varies is
                // how much work precedes the mod-content load. Never assert on it. A -1 means no
                // scene has loaded yet. bootLoopDrains is what covers the stretch before it: on a
                // client it retires after a handful of frames, and on the dedicated server 0 there
                // while pumpHostCreatedAtFrame is large means the boot window went unpumped, which
                // is what made every Main(...) route 504 for the whole first stretch of a headless
                // boot.
                .Int("hostUpdateDrains", MainThreadPump.HostUpdateDrains)
                .Int("pumpHostCreatedAtFrame", MainThreadPump.PumpHostCreatedAtFrame)
                .Int("pumpBootLoopDrains", MainThreadPump.BootLoopDrains)
                .Str("pumpBootLoopState", MainThreadPump.BootLoopState)
                .Int("scenesLoaded", MainThreadPump.ScenesLoaded)
                // Climbs once per simulation tick and is NOT an error: it is the counter proving
                // the off-main-thread drain guard is in force.
                .Int("offThreadPumpsRefused", MainThreadPump.OffThreadPumpsRefused)
                .Int("pumpObjectCreations", MainThreadPump.InstanceCreations)
                .Int("pluginDestroyCount", Plugin.DestroyCount)
                .Bit("serverRunning", Plugin.Server != null && Plugin.Server.Running)
                .Int("serverRequests", Plugin.Server == null ? 0 : Plugin.Server.Requests)
                .Str("serverLastAcceptError", Plugin.Server == null ? null : Plugin.Server.LastAcceptError)
                // Callers that hung up before reading their answer. Not an error, and the
                // reason the log carries one prose line about it rather than N stack traces.
                .Int("serverClientDisconnects", Plugin.Server == null ? 0 : Plugin.Server.ClientDisconnects)
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

        /// <summary>Set by <see cref="ConnectedClientsJson"/> when the roster could not be read.</summary>
        internal static string RosterError;

        /// <summary>
        ///     The server-side roster, which is what makes "did the second instance actually arrive"
        ///     assertable from the host without asking the joiner. Empty on anything that is not a
        ///     server, because the roster is the server's answer.
        ///
        ///     <para>
        ///     TWO SOURCES, AND THE HOST IS ONLY IN THE SECOND. This read used to be
        ///     <c>NetworkBase.Clients</c> alone, and a listen host is never in that list: the sole
        ///     writer is <c>NetworkBase.AddClient</c>, called only from
        ///     <c>NetworkServer.VerifyConnection</c>, so the list holds JOINERS. The host's own
        ///     record is built separately by <c>NetworkServer.PopulateHostClient</c> and parked on
        ///     <c>NetworkManager.HostClient</c>. The game unions the two everywhere it presents a
        ///     roster: <c>NetworkManager.LogClientRosterToConsole</c> walks <c>Clients</c> and then
        ///     appends <c>HostClient</c>, and <c>NetworkManager.SerialisePlayerList</c> writes
        ///     <c>HostClient</c> first under exactly the guard used below
        ///     (<c>Client.Find(HostClient.ClientId) == null</c>) and then every entry in
        ///     <c>Clients</c>. Verified against 0.2.6428.27798.
        ///     </para>
        ///
        ///     <para>
        ///     The union also makes the roster reconcile with <c>playersInGame</c>, which is
        ///     <c>NetworkManager.TotalPlayersInGame => Clients.Count + (IsBatchMode ? 0 : 1)</c>. A
        ///     listen host adds itself, a dedicated server does not, and this method matches by
        ///     skipping a <c>HostClient</c> whose <c>ClientId</c> is 0. That is the game's own rule
        ///     for "not a real player", applied in <c>Client.DeserialiseClient</c>, and 0 is what a
        ///     dedicated server has, because <c>PlayerCookie</c> is not loaded in batch mode.
        ///     </para>
        ///
        ///     <para>
        ///     ClientId travels as a string, matching <c>/instance</c>, because a JSON number goes
        ///     through double on the reading side and silently loses precision above 2^53. A
        ///     truncated ClientId is exactly the failure these ids exist to detect.
        ///     </para>
        /// </summary>
        internal static string ConnectedClientsJson()
        {
            var rows = new List<string>();
            RosterError = null;
            try
            {
                if (!NetworkManager.IsServer) return "[]";

                Assets.Scripts.Client host = null;
                try { host = NetworkManager.HostClient; } catch { }

                var clients = NetworkBase.Clients;

                if (host != null && host.ClientId != 0 && !IsInClientList(clients, host.ClientId))
                    rows.Add(ClientRow(host));

                if (clients != null)
                {
                    // Indexed, not foreach. A joiner is added and removed from this list by the
                    // network layer, and a List<T> enumerator throws "collection was modified" if
                    // that lands mid-read. The old foreach was inside the catch below, so such a
                    // throw returned [] and read as "nobody is connected": the single most
                    // expensive wrong answer this endpoint can give. Indexing cannot throw that.
                    for (int i = 0; i < clients.Count; i++)
                    {
                        Assets.Scripts.Client client = null;
                        try { client = clients[i]; } catch { break; }
                        if (client == null) continue;
                        rows.Add(ClientRow(client));
                    }
                }
            }
            catch (Exception ex)
            {
                RosterError = "the server-side roster could not be read, so this array is empty for " +
                              "a reason that is NOT 'nobody is connected': " + ex.Message;
                return "[]";
            }
            return "[" + string.Join(",", rows.ToArray()) + "]";
        }

        private static bool IsInClientList(List<Assets.Scripts.Client> clients, ulong clientId)
        {
            if (clients == null) return false;
            for (int i = 0; i < clients.Count; i++)
            {
                Assets.Scripts.Client client = null;
                try { client = clients[i]; } catch { return false; }
                if (client != null && client.ClientId == clientId) return true;
            }
            return false;
        }

        /// <summary>
        ///     One roster row.
        ///
        ///     <para>
        ///     <c>connectionId</c> is the reason this method exists rather than being inlined.
        ///     <c>Client.connectionId</c> is a <c>long</c> holding a RakNet connection id, and the
        ///     values are enormous: 189151461494586169 and 1044835390751713754 in one measured
        ///     join. Emitted as a raw JSON number it does not fit the launcher's <c>int?</c>, so
        ///     System.Text.Json threw on the WHOLE /status payload, the launcher's reader returned
        ///     null, and its roster poll concluded the joiner had never arrived. That is what
        ///     produced three attempts of "roster did not grow (0 then 0)" against a host whose own
        ///     console showed the client verified, served and ready.
        ///     </para>
        ///
        ///     <para>
        ///     So the number is emitted only when it round-trips Int32, and the exact value always
        ///     rides beside it as a string. The proper fix is for the launcher's
        ///     <c>ConnectedClient.ConnectionId</c> to become a string, the same way
        ///     <c>clientId</c> already is and for the same reason; until it does, an id past
        ///     2^31 reads null here rather than taking the response down with it.
        ///     </para>
        /// </summary>
        private static string ClientRow(Assets.Scripts.Client client)
        {
            var row = new Json.Obj();
            try { row.Str("clientId", client.ClientId.ToString(CultureInfo.InvariantCulture)); } catch { }
            try { row.Str("username", client.name); } catch { }
            try { row.Str("state", client.state.ToString()); } catch { }
            try { row.Bit("isHost", client.IsHost); } catch { }
            try
            {
                long connectionId = client.connectionId;
                if (connectionId >= int.MinValue && connectionId <= int.MaxValue)
                    row.Int("connectionId", connectionId);
                else row.Raw("connectionId", "null");
                row.Str("connectionIdString", connectionId.ToString(CultureInfo.InvariantCulture));
            }
            catch { }
            return row.ToString();
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
