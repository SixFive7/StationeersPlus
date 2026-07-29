using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Inventory;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Serialization;
using HarmonyLib;
using TerrainSystem;
using UnityEngine;
using ConsoleWindow = Assets.Scripts.ConsoleWindow;
using GameManager = Assets.Scripts.GameManager;
using NetworkClient = Assets.Scripts.NetworkClient;

namespace ClientDriver
{
    /// <summary>
    /// Maps HTTP paths onto engine work. Runs on the HTTP accept thread; anything
    /// touching Unity hops to the main thread through <see cref="MainThreadPump"/>.
    /// </summary>
    internal static class Router
    {
        private const int DefaultTimeoutMs = 20000;

        internal static HttpResponse Handle(HttpRequest req)
        {
            string path = (req.Path ?? "/").TrimEnd('/');
            if (path.Length == 0) path = "/";

            IDictionary body = Json.ParseObject(req.Body);
            // Query parameters are a convenience alias for body fields, so every
            // endpoint can also be driven from a plain browser or curl GET.
            foreach (var kv in req.QueryParams)
                if (!body.Contains(kv.Key)) body[kv.Key] = kv.Value;

            switch (path.ToLowerInvariant())
            {
                case "/":
                case "/help": return HttpResponse.Json(Help());
                case "/ping": return Ping();

                // ---- state -------------------------------------------------
                case "/status": return Main(() => HttpResponse.Json(StateReporter.Status()));
                case "/player": return Main(() => HttpResponse.Json(
                    "{\"ok\":true,\"player\":" + StateReporter.PlayerJson() + "}"));
                case "/colors": return Main(() => HttpResponse.Json(StateReporter.Colors()));
                case "/plugins": return Main(() => HttpResponse.Json(StateReporter.Plugins()));
                case "/nearby": return Main(() => HttpResponse.Json(StateReporter.Nearby(
                    Json.GetFloat(body, "radius", 10f),
                    Json.GetStr(body, "filter"),
                    Json.GetInt(body, "limit", 100))));

                // ---- console -----------------------------------------------
                case "/console/log": return ConsoleLog(body);
                case "/console/clear": ConsoleTap.Clear(); return Ok();
                case "/console/buffer": return Main(() => HttpResponse.Json(
                    ConsoleTap.ReadGameBuffer(Json.GetInt(body, "limit", 200), Json.GetStr(body, "contains"))));
                case "/console/exec": return ConsoleExec(body);
                case "/console/print": return ConsolePrint(body);
                case "/console/commands": return Main(() => HttpResponse.Json(ConsoleCommands(
                    Json.GetStr(body, "contains"))));

                // ---- session -----------------------------------------------
                case "/connect": return Connect(body);
                case "/disconnect": return Disconnect(body);
                case "/quit": return Quit(body);
                case "/saves": return Main(() => HttpResponse.Json(Saves()));
                case "/savepath": return Main(() => SavePath(body));
                case "/load": return LoadSave(body);
                case "/newworld": return NewWorld(body);
                case "/waitfor": return WaitFor(body);

                // ---- input -------------------------------------------------
                case "/input/key": return InputKey(body);
                case "/input/scroll": return InputScroll(body);
                case "/input/mouse": return InputMouse(body);
                case "/input/releaseall": return Main(() => { VirtualInput.ReleaseAll(); return OkJson(); });
                case "/input/clear": return Main(() => { VirtualInput.ClearAll(); return OkJson(); });
                case "/input/keymap": return HttpResponse.Json(new Json.Obj()
                    .Bit("ok", true).StrArray("actions", VirtualInput.ListKeyMapActions()).ToString());
                case "/input/enable":
                    VirtualInput.Enabled = Json.GetBool(body, "enabled", true);
                    return HttpResponse.Json(new Json.Obj().Bit("ok", true).Bit("enabled", VirtualInput.Enabled).ToString());
                case "/input/mouseposition": return InputMousePosition(body);

                // ---- player actions -----------------------------------------
                case "/player/teleport": return Main(() => Teleport(body));
                case "/player/look": return Main(() => Look(body));
                case "/player/use": return Main(() => UseOnTarget(body));
                case "/player/swaphands": return Main(() => SwapHands());

                // ---- spawning ------------------------------------------------
                case "/spawn/hand": return Main(() => SpawnIntoHand(body));
                case "/spawn/world": return Main(() => SpawnIntoWorld(body));
                case "/spawn/structure": return Main(() => SpawnStructure(body));
                case "/prefabs": return Main(() => HttpResponse.Json(Prefabs(
                    Json.GetStr(body, "contains"), Json.GetInt(body, "limit", 200), Json.GetStr(body, "type"))));

                // ---- StationeersLaunchPad settings panel ---------------------
                case "/modsettings/list": return Main(() => HttpResponse.Json(ModSettingsPanel.List()));
                case "/modsettings": return Main(() => HttpResponse.Json(ModSettingsPanel.Show(
                    Json.GetStr(body, "mod"), Json.GetBool(body, "show", true))));

                // ---- modal dialogs -------------------------------------------
                case "/modal": return Main(() => HttpResponse.Json(Modal.Describe()));
                case "/modal/click": return Main(() => HttpResponse.Json(
                    Modal.Click(Json.GetInt(body, "button", 1))));

                // ---- cursor --------------------------------------------------
                case "/cursor/force": return Main(() => ForceCursor(body));

                // ---- screenshot ----------------------------------------------
                case "/screenshot": return TakeScreenshot(body);

                // ---- config --------------------------------------------------
                case "/config": return Main(() => HttpResponse.Json(ConfigAccess.Dump(
                    Json.GetStr(body, "guid"), Json.GetStr(body, "filter"))));
                case "/config/set": return Main(() => HttpResponse.Json(ConfigAccess.Set(
                    Json.GetStr(body, "guid"),
                    Json.GetStr(body, "section"),
                    Json.GetStr(body, "key"),
                    Json.GetStr(body, "value"),
                    Json.GetBool(body, "save", true))));
                case "/config/reload": return Main(() => HttpResponse.Json(ConfigAccess.Reload(
                    Json.GetStr(body, "guid"))));
                case "/reflect": return Main(() => HttpResponse.Json(ConfigAccess.ReadStatic(
                    Json.GetStr(body, "type"), Json.GetStr(body, "member"))));
                case "/reflect/members": return Main(() => HttpResponse.Json(ConfigAccess.DumpMembers(
                    Json.GetStr(body, "type"))));

                default:
                    return HttpResponse.Error("unknown endpoint '" + req.Path + "'. GET /help lists them all.", 404);
            }
        }

        // ---- helpers -------------------------------------------------------

        private static HttpResponse Main(Func<HttpResponse> work, int timeoutMs = DefaultTimeoutMs)
            => MainThreadPump.RunSync(work, timeoutMs);

        private static HttpResponse Ok() => HttpResponse.Json("{\"ok\":true}");
        private static HttpResponse OkJson() => HttpResponse.Json("{\"ok\":true}");
        private static HttpResponse Fail(string message) => HttpResponse.Error(message, 409);

        private static HttpResponse Ping()
        {
            return HttpResponse.Json(new Json.Obj()
                .Bit("ok", true)
                .Str("plugin", Plugin.PluginName)
                .Str("version", Plugin.PluginVersion)
                .Int("pumpFrames", MainThreadPump.FramesSeen)
                .Int("frame", MainThreadPump.FrameCount)
                .Bit("pumpAlive", MainThreadPump.Alive)
                .ToString());
        }

        private static Vector3 ReadVector(IDictionary body, string key, Vector3 fallback)
        {
            if (!Json.Has(body, key)) return fallback;
            var raw = body[key];
            var list = raw as List<object>;
            if (list != null && list.Count >= 3)
            {
                return new Vector3(ToF(list[0]), ToF(list[1]), ToF(list[2]));
            }
            var s = raw as string;
            if (!string.IsNullOrEmpty(s))
            {
                var parts = s.Split(',');
                if (parts.Length >= 3)
                {
                    return new Vector3(ParseF(parts[0]), ParseF(parts[1]), ParseF(parts[2]));
                }
            }
            return fallback;
        }

        private static float ToF(object o)
        {
            if (o is double d) return (float)d;
            if (o is string s) return ParseF(s);
            return 0f;
        }

        private static float ParseF(string s)
        {
            float f;
            float.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out f);
            return f;
        }

        // ---- console -------------------------------------------------------

        private static HttpResponse ConsoleLog(IDictionary body)
        {
            long since = Json.GetLong(body, "since", 0);
            int limit = Json.GetInt(body, "limit", 200);
            string contains = Json.GetStr(body, "contains");
            string source = Json.GetStr(body, "source");
            var lines = ConsoleTap.Snapshot(since, limit, contains, source);
            return HttpResponse.Json(ConsoleTap.ToJson(lines, ConsoleTap.NextSeq));
        }

        private static HttpResponse ConsolePrint(IDictionary body)
        {
            string text = Json.GetStr(body, "text", "");
            string level = (Json.GetStr(body, "level", "action") ?? "action").ToLowerInvariant();
            return Main(() =>
            {
                switch (level)
                {
                    case "error": ConsoleWindow.PrintError(text, true); break;
                    case "info": ConsoleWindow.Print(text); break;
                    default: ConsoleWindow.PrintAction(text); break;
                }
                return OkJson();
            });
        }

        /// <summary>
        /// Submits a line to the in-game console and returns every console line the
        /// command produced. ConsoleWindow.Submit prints the echo then hands off to
        /// CommandLine.Process, so capturing from the sequence number taken before
        /// the call gets the echo plus all output.
        /// </summary>
        private static HttpResponse ConsoleExec(IDictionary body)
        {
            string command = Json.GetStr(body, "command");
            if (string.IsNullOrEmpty(command)) return HttpResponse.Error("missing 'command'", 400);
            int waitFrames = Json.GetInt(body, "waitFrames", 2);
            int waitMs = Json.GetInt(body, "waitMs", 0);

            long before = ConsoleTap.NextSeq;
            int endFrame = 0;
            var scheduled = Main(() =>
            {
                ConsoleWindow.Submit(command);
                endFrame = Time.frameCount + Math.Max(0, waitFrames);
                return OkJson();
            });
            if (scheduled.Status != 200) return scheduled;

            if (waitFrames > 0) MainThreadPump.WaitForFrame(endFrame + 1, 10000);
            if (waitMs > 0) System.Threading.Thread.Sleep(waitMs);

            var lines = ConsoleTap.Snapshot(before, 500, null, null);
            var payload = ConsoleTap.ToJson(lines, ConsoleTap.NextSeq);
            // Splice the command in after the payload's own leading {"ok":true,
            const string head = "{\"ok\":true,";
            var spliced = payload.StartsWith(head, StringComparison.Ordinal)
                ? head + "\"command\":" + Json.Escape(command) + "," + payload.Substring(head.Length)
                : payload;
            return HttpResponse.Json(spliced);
        }

        private static string ConsoleCommands(string contains)
        {
            var names = new List<string>();
            try
            {
                var mapProp = AccessTools.Property(typeof(global::Util.Commands.CommandLine), "CommandsMap");
                var map = mapProp?.GetValue(null, null) as IEnumerable;
                if (map != null)
                {
                    foreach (var entry in map)
                    {
                        var keyProp = entry.GetType().GetProperty("Key");
                        var valProp = entry.GetType().GetProperty("Value");
                        var key = keyProp?.GetValue(entry, null) as string;
                        var val = valProp?.GetValue(entry, null);
                        if (key == null) continue;
                        if (!string.IsNullOrEmpty(contains) &&
                            key.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        names.Add(key + " (" + (val == null ? "?" : val.GetType().Name) + ")");
                    }
                }
            }
            catch (Exception ex)
            {
                return new Json.Obj().Bit("ok", false).Str("error", ex.Message).ToString();
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return new Json.Obj().Bit("ok", true).Int("count", names.Count)
                .StrArray("commands", names).ToString();
        }

        // ---- session -------------------------------------------------------

        /// <summary>
        /// Direct connect, the same call the Join menu's Direct Connect button makes.
        /// <c>NetworkClient.JoinClientFromMenu("ip:port")</c> runs ClientPreJoin
        /// (GameState -> Joining), parses the address, and calls
        /// <c>NetworkManager.StartClient</c>. Calling StartClient directly would skip
        /// the menu teardown and the connection timer, so it is not used here.
        /// </summary>
        private static HttpResponse Connect(IDictionary body)
        {
            string address = Json.GetStr(body, "address", "127.0.0.1");
            int port = Json.GetInt(body, "port", 28016);
            bool wait = Json.GetBool(body, "wait", true);
            int timeoutMs = Json.GetInt(body, "timeoutMs", 180000);
            bool suppressTimeout = Json.GetBool(body, "suppressTimeout", true);

            // The NetworkClient component only becomes findable some way into boot,
            // so wait for it rather than failing the request outright.
            var appeared = PollUntil(Math.Min(timeoutMs, 120000), () =>
                MainThreadPump.RunValue(() => FindNetworkClient() != null ? "yes" : null, 5000));
            if (appeared == null)
                return Fail("no NetworkClient in the scene after waiting; the game is still booting or the mod load stalled");

            var pre = Main(() =>
            {
                string state = GameManager.GameState.ToString();
                if (state != "None")
                    return Fail("cannot connect from gameState=" + state + "; disconnect to the main menu first");

                var client = FindNetworkClient();
                if (client == null)
                    return Fail("no NetworkClient in the scene");

                client.JoinClientFromMenu(address + ":" + port.ToString(CultureInfo.InvariantCulture));

                // NetworkClient.OnJoinStart, called inside JoinClientFromMenu, arms a
                // 10 second timer whose only job is to give up and pop a modal. Ten
                // seconds is nowhere near enough for a heavily modded dedicated
                // server: the handshake reaches the server ("A connection is
                // incoming" in server.log) and then the client cancels itself
                // mid-transfer. Stop the timer and let this endpoint's own timeout
                // be the authority.
                if (suppressTimeout)
                {
                    try { NetworkClient.StopConnectionTimer(); }
                    catch (Exception ex) { Plugin.Log.LogWarning("could not stop the join timer: " + ex.Message); }
                }

                return HttpResponse.Json(new Json.Obj().Bit("ok", true)
                    .Str("target", address + ":" + port).Bit("waiting", wait)
                    .Bit("timerSuppressed", suppressTimeout).ToString());
            });
            if (pre.Status != 200) return pre;
            if (!wait) return pre;

            // The join has a 10 second timer of its own; on expiry it pops a
            // ConfirmationPanel that a human would have to click. Dismiss it here so
            // an unattended run gets a clean "failed" rather than a wedged client.
            string modalText = null;
            var result = PollUntil(timeoutMs, () =>
            {
                string state = MainThreadPump.RunValue(() => GameManager.GameState.ToString(), 5000);
                if (state == "Running") return "connected";

                string modal = MainThreadPump.RunValue(() => Modal.Describe(), 5000);
                if (modal != null && modal.IndexOf("\"visible\":true", StringComparison.Ordinal) >= 0)
                {
                    modalText = modal;
                    MainThreadPump.RunValue(() => Modal.Click(1), 5000);
                    return "failed";
                }

                if (state == "None") return "failed";
                return null;
            });

            // With the game's own timer suppressed a dead server leaves the client
            // parked in Joining forever, so clean up after our own timeout.
            if (result == null)
            {
                try { MainThreadPump.RunValue(() => { NetworkClient.Cancel(); return true; }, 5000); }
                catch { }
            }

            var o = new Json.Obj().Bit("ok", result == "connected")
                .Str("target", address + ":" + port)
                .Str("result", result ?? "timeout");
            if (modalText != null) o.Raw("dialog", modalText);
            try
            {
                o.Raw("status", MainThreadPump.RunValue(() => StateReporter.Status(), 5000));
            }
            catch { }
            return HttpResponse.Json(o.ToString(), result == "connected" ? 200 : 409);
        }

        /// <summary>
        /// FindObjectOfType only sees active, enabled components. The NetworkClient
        /// lives on a DontDestroyOnLoad object that is not always active at the
        /// menu, so fall back to the whole-object sweep, which includes inactive
        /// ones. Without the fallback a connect issued during boot fails with a
        /// misleading "still booting".
        /// </summary>
        internal static NetworkClient FindNetworkClient()
        {
            var client = UnityEngine.Object.FindObjectOfType<NetworkClient>();
            if (client != null) return client;
            var all = Resources.FindObjectsOfTypeAll<NetworkClient>();
            if (all != null && all.Length > 0) return all[0];
            return null;
        }

        private static HttpResponse Disconnect(IDictionary body)
        {
            bool wait = Json.GetBool(body, "wait", true);
            int timeoutMs = Json.GetInt(body, "timeoutMs", 30000);

            var pre = Main(() =>
            {
                try { NetworkClient.Cancel(); } catch { }
                GameManager.LeaveGame();
                return OkJson();
            });
            if (pre.Status != 200) return pre;
            if (!wait) return pre;

            var result = PollUntil(timeoutMs, () =>
            {
                string state = MainThreadPump.RunValue(() => GameManager.GameState.ToString(), 5000);
                return state == "None" ? "menu" : null;
            });

            return HttpResponse.Json(new Json.Obj()
                .Bit("ok", result == "menu")
                .Str("result", result ?? "timeout")
                .ToString(), result == "menu" ? 200 : 409);
        }

        private static HttpResponse Quit(IDictionary body)
        {
            bool hard = Json.GetBool(body, "hard", false);
            // Answer before the process dies, otherwise the caller sees a dropped
            // socket rather than a confirmation.
            MainThreadPump.Post(() =>
            {
                try
                {
                    if (hard) GameManager.QuitGame();
                    else Application.Quit();
                }
                catch (Exception ex) { Plugin.Log.LogError("quit failed: " + ex); }
            });
            return HttpResponse.Json(new Json.Obj().Bit("ok", true).Bit("hard", hard).ToString());
        }

        private static string Saves()
        {
            var entries = new List<string>();
            try
            {
                var saves = LoadHelper.GetLocalSaves();
                if (saves != null)
                {
                    foreach (var s in saves)
                    {
                        var o = new Json.Obj();
                        foreach (var f in s.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                        {
                            object v = null;
                            try { v = f.GetValue(s); } catch { }
                            o.Str(f.Name, v == null ? null : v.ToString());
                        }
                        foreach (var p in s.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        {
                            if (p.GetIndexParameters().Length > 0) continue;
                            object v = null;
                            try { v = p.GetValue(s, null); } catch { }
                            o.Str(p.Name, v == null ? null : v.ToString());
                        }
                        entries.Add(o.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                return new Json.Obj().Bit("ok", false).Str("error", ex.Message).ToString();
            }
            return new Json.Obj().Bit("ok", true).Int("count", entries.Count)
                .Raw("saves", "[" + string.Join(",", entries.ToArray()) + "]").ToString();
        }

        /// <summary>
        /// Reads or redirects the user-data root. Every save the game writes lands in
        /// <c>&lt;SavePath&gt;/saves</c>, resolved on each call to
        /// <c>StationSaveUtils.GetSavePath()</c>, so pointing this at a scratch
        /// directory before creating a world keeps a driven test session out of the
        /// developer's real save folder entirely. The change is in memory; the game
        /// persists it to setting.xml on exit, so put it back when the session ends.
        /// </summary>
        private static HttpResponse SavePath(IDictionary body)
        {
            var data = Assets.Scripts.Serialization.Settings.CurrentData;
            if (data == null) return Fail("Settings.CurrentData is null");

            string current = data.SavePath;
            string wanted = Json.GetStr(body, "path");
            if (string.IsNullOrEmpty(wanted))
                return HttpResponse.Json(new Json.Obj().Bit("ok", true).Str("savePath", current).ToString());

            try
            {
                Directory.CreateDirectory(wanted);
                Directory.CreateDirectory(Path.Combine(wanted, "saves"));
                Directory.CreateDirectory(Path.Combine(wanted, "scripts"));
                Directory.CreateDirectory(Path.Combine(wanted, "mods"));
            }
            catch (Exception ex) { return HttpResponse.Error("could not create " + wanted + ": " + ex.Message); }

            data.SavePath = wanted;
            return HttpResponse.Json(new Json.Obj()
                .Bit("ok", true).Str("previous", current).Str("savePath", data.SavePath).ToString());
        }

        private static HttpResponse LoadSave(IDictionary body)
        {
            string name = Json.GetStr(body, "save");
            if (string.IsNullOrEmpty(name)) return HttpResponse.Error("missing 'save'", 400);
            bool wait = Json.GetBool(body, "wait", true);
            int timeoutMs = Json.GetInt(body, "timeoutMs", 180000);

            var pre = Main(() =>
            {
                ConsoleWindow.Submit("load \"" + name + "\"");
                return OkJson();
            });
            if (pre.Status != 200) return pre;
            if (!wait) return pre;

            var result = PollUntil(timeoutMs, () =>
            {
                string state = MainThreadPump.RunValue(() => GameManager.GameState.ToString(), 5000);
                return state == "Running" ? "loaded" : null;
            });
            return HttpResponse.Json(new Json.Obj()
                .Bit("ok", result == "loaded").Str("save", name).Str("result", result ?? "timeout").ToString(),
                result == "loaded" ? 200 : 409);
        }

        private static HttpResponse NewWorld(IDictionary body)
        {
            string world = Json.GetStr(body, "world", "Moon");
            string difficulty = Json.GetStr(body, "difficulty", "Normal");
            string start = Json.GetStr(body, "start", "Default");
            bool wait = Json.GetBool(body, "wait", true);
            int timeoutMs = Json.GetInt(body, "timeoutMs", 300000);

            var pre = Main(() =>
            {
                ConsoleWindow.Submit("new " + world + " " + difficulty + " " + start);
                return OkJson();
            });
            if (pre.Status != 200) return pre;
            if (!wait) return pre;

            var result = PollUntil(timeoutMs, () =>
            {
                string state = MainThreadPump.RunValue(() => GameManager.GameState.ToString(), 5000);
                return state == "Running" ? "loaded" : null;
            });
            return HttpResponse.Json(new Json.Obj()
                .Bit("ok", result == "loaded").Str("world", world).Str("result", result ?? "timeout").ToString(),
                result == "loaded" ? 200 : 409);
        }

        /// <summary>
        /// Blocks until the client reaches a named phase, so a harness can say
        /// "wait until we are in a world" without polling /status in a loop.
        /// </summary>
        private static HttpResponse WaitFor(IDictionary body)
        {
            string wanted = Json.GetStr(body, "phase", "inWorld");
            int timeoutMs = Json.GetInt(body, "timeoutMs", 120000);
            var result = PollUntil(timeoutMs, () =>
            {
                string state = MainThreadPump.RunValue(() => GameManager.GameState.ToString(), 5000);
                string phase = PhaseOf(state);
                return (string.Equals(phase, wanted, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(state, wanted, StringComparison.OrdinalIgnoreCase)) ? phase : null;
            });
            return HttpResponse.Json(new Json.Obj()
                .Bit("ok", result != null).Str("wanted", wanted).Str("result", result ?? "timeout").ToString(),
                result != null ? 200 : 409);
        }

        private static string PhaseOf(string gameState)
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

        private static string PollUntil(int timeoutMs, Func<string> probe)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var r = probe();
                    if (r != null) return r;
                }
                catch { }
                System.Threading.Thread.Sleep(200);
            }
            return null;
        }

        // ---- input ---------------------------------------------------------

        private static HttpResponse InputKey(IDictionary body)
        {
            string keyName = Json.GetStr(body, "key");
            if (string.IsNullOrEmpty(keyName)) return HttpResponse.Error("missing 'key'", 400);

            KeyCode key;
            string how;
            if (!VirtualInput.TryResolveKey(keyName, out key, out how))
                return HttpResponse.Error("unknown key '" + keyName + "'. GET /input/keymap lists the action names.", 400);

            string mode = (Json.GetStr(body, "mode", "tap") ?? "tap").ToLowerInvariant();
            int frames = Json.GetInt(body, "frames", 3);
            bool wait = Json.GetBool(body, "wait", true);

            int endFrame = 0;
            var scheduled = Main(() =>
            {
                switch (mode)
                {
                    case "down": endFrame = VirtualInput.HoldKey(key); break;
                    case "up": endFrame = VirtualInput.ReleaseKey(key); break;
                    default: endFrame = VirtualInput.PressKey(key, frames); break;
                }
                return OkJson();
            });
            if (scheduled.Status != 200) return scheduled;

            // A held key never ends, so waiting on it would hang; wait only for the
            // press to become visible.
            int target = mode == "down" ? endFrame + 1 : endFrame + 2;
            bool settled = !wait || MainThreadPump.WaitForFrame(target, 15000);

            return HttpResponse.Json(new Json.Obj()
                .Bit("ok", true).Str("key", key.ToString()).Str("resolvedVia", how)
                .Str("mode", mode).Int("frames", frames).Bit("settled", settled).ToString());
        }

        private static HttpResponse InputScroll(IDictionary body)
        {
            float notches = Json.GetFloat(body, "notches", 1f);
            // One frame by default, and that matters. Consumers act on the wheel once
            // per frame, so a two-frame window is two notches: SprayPaintPlus's colour
            // cycler advances two swatches per request instead of one. Verified in
            // world on 2026-07-27.
            int frames = Json.GetInt(body, "frames", 1);
            int repeat = Math.Max(1, Json.GetInt(body, "repeat", 1));
            bool wait = Json.GetBool(body, "wait", true);
            int gapFrames = Math.Max(1, Json.GetInt(body, "gapFrames", 3));

            for (int i = 0; i < repeat; i++)
            {
                int endFrame = 0;
                var scheduled = Main(() => { endFrame = VirtualInput.Scroll(notches, frames); return OkJson(); });
                if (scheduled.Status != 200) return scheduled;
                if (wait && !MainThreadPump.WaitForFrame(endFrame + gapFrames, 15000))
                    return HttpResponse.Error("scroll " + (i + 1) + "/" + repeat + " did not settle", 504);
            }

            // Leave no residual wheel state behind: a stale window would keep
            // cycling colours on the next frame the game happens to poll.
            Main(() => { VirtualInput.ClearScroll(); return OkJson(); });

            return HttpResponse.Json(new Json.Obj()
                .Bit("ok", true).Flt("notches", notches).Int("frames", frames).Int("repeat", repeat).ToString());
        }

        private static HttpResponse InputMouse(IDictionary body)
        {
            int button = Json.GetInt(body, "button", 0);
            var alias = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                { "key", (KeyCode.Mouse0 + button).ToString() },
                { "mode", Json.GetStr(body, "mode", "tap") },
                { "frames", (double)Json.GetInt(body, "frames", 3) },
                { "wait", Json.GetBool(body, "wait", true) },
            };
            return InputKey(alias);
        }

        private static HttpResponse InputMousePosition(IDictionary body)
        {
            bool clear = Json.GetBool(body, "clear", false);
            float x = Json.GetFloat(body, "x", 0f);
            float y = Json.GetFloat(body, "y", 0f);
            return Main(() =>
            {
                VirtualInput.SetMousePosition(clear ? (Vector3?)null : new Vector3(x, y, 0f));
                return HttpResponse.Json(new Json.Obj().Bit("ok", true).Bit("cleared", clear)
                    .Flt("x", x).Flt("y", y).ToString());
            });
        }

        // ---- player actions -------------------------------------------------

        /// <summary>
        /// Teleports the local player. Mirrors Human.ForceSetPosition but without its
        /// GameManager.RunSimulation gate, which is false on a multiplayer client and
        /// would make the call a silent no-op there. A client is authoritative over
        /// its own Human's transform (DynamicThing.HasAuthority is true for it, so
        /// UpdateNetworkPosition never overwrites it), so the write sticks and
        /// propagates through the normal owner update.
        /// </summary>
        private static HttpResponse Teleport(IDictionary body)
        {
            var human = Human.LocalHuman;
            if (human == null) return Fail("no local player");

            Vector3 current = human.ThingTransformPosition;
            Vector3 target = current;

            if (Json.Has(body, "position")) target = ReadVector(body, "position", current);
            else if (Json.Has(body, "x") || Json.Has(body, "y") || Json.Has(body, "z"))
                target = new Vector3(
                    Json.GetFloat(body, "x", current.x),
                    Json.GetFloat(body, "y", current.y),
                    Json.GetFloat(body, "z", current.z));
            if (Json.Has(body, "offset")) target = target + ReadVector(body, "offset", Vector3.zero);

            try
            {
                human.ThingTransformPosition = target;
                if (human.Transform != null) human.Transform.position = target;
                var rb = human.ActiveRigidbody;
                if (rb != null)
                {
                    rb.MovePosition(target);
                    if (!rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
                }
                human.ResetInterpolation();
            }
            catch (Exception ex) { return HttpResponse.Error("teleport failed: " + ex.Message); }

            return HttpResponse.Json(new Json.Obj()
                .Bit("ok", true).Vec("from", current).Vec("to", human.ThingTransformPosition).ToString());
        }

        /// <summary>
        /// Sets the look direction. CameraController.RotationX is pitch with positive
        /// meaning up, RotationY is yaw. SetMouseLook adds mouse delta to both every
        /// LateUpdate, so a one-shot write holds only while the mouse is still: fine
        /// for an unattended client, and exactly what UnitTest_SetRotation is for.
        /// </summary>
        private static HttpResponse Look(IDictionary body)
        {
            var cam = CameraController.Instance;
            if (cam == null) return Fail("no CameraController (not in a world)");

            float yaw = cam.RotationY;
            float pitch = cam.RotationX;

            if (Json.Has(body, "at"))
            {
                var human = Human.LocalHuman;
                if (human == null) return Fail("no local player");
                Vector3 at = ReadVector(body, "at", Vector3.zero);
                Vector3 origin = CameraController.CameraOrigin;
                Vector3 dir = (at - origin).normalized;
                yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
                pitch = Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;
            }
            else
            {
                yaw = Json.GetFloat(body, "yaw", yaw);
                pitch = Json.GetFloat(body, "pitch", pitch);
            }

            pitch = Mathf.Clamp(pitch, -89f, 89f);

            try { cam.UnitTest_SetRotation(pitch, yaw); }
            catch (Exception ex) { return HttpResponse.Error("look failed: " + ex.Message); }

            return HttpResponse.Json(new Json.Obj()
                .Bit("ok", true).Flt("yaw", cam.RotationY).Flt("pitch", cam.RotationX).ToString());
        }

        /// <summary>
        /// Uses the item in the active hand on a target Thing, through
        /// OnServer.AttackWith. That is the same entry the game itself takes when the
        /// held item declares AttackWithEvent.Server: predict locally, then send an
        /// AttackWithMessage. It takes a reference id rather than requiring the
        /// cursor to be pointed at anything.
        /// </summary>
        private static HttpResponse UseOnTarget(IDictionary body)
        {
            var human = Human.LocalHuman;
            if (human == null) return Fail("no local player");

            var im = InventoryManager.Instance;
            if (im == null || im.ActiveHand == null || im.InactiveHand == null) return Fail("hands not initialised");

            Thing target = null;
            long targetId = Json.GetLong(body, "targetId", 0);
            if (targetId != 0) target = Thing.Find(targetId);
            else if (Json.GetBool(body, "cursor", true)) target = CursorManager.CursorThing;

            if (target == null) return Fail("no target: pass targetId, or aim the cursor at something first");

            float ratio = Json.GetFloat(body, "completedRatio", 1f);
            bool isDestroy = Json.GetBool(body, "destroy", false);
            bool isCopy = Json.GetBool(body, "copy", false);
            Vector3 point = Json.Has(body, "point")
                ? ReadVector(body, "point", target.ThingTransformPosition)
                : target.ThingTransformPosition;

            var held = InventoryManager.ActiveHandSlot?.Get();

            try
            {
                OnServer.AttackWith(
                    InventoryManager.Parent,
                    (byte)im.ActiveHand.SlotId,
                    (byte)im.InactiveHand.SlotId,
                    target.ReferenceId,
                    point,
                    ratio,
                    isDestroy,
                    isCopy);
            }
            catch (Exception ex)
            {
                return HttpResponse.Error("AttackWith failed: " + ex.Message);
            }

            return HttpResponse.Json(new Json.Obj()
                .Bit("ok", true)
                .Int("targetId", target.ReferenceId)
                .Str("targetPrefab", target.PrefabName)
                .Str("heldItem", held == null ? null : held.PrefabName)
                .Vec("point", point)
                .ToString());
        }

        private static HttpResponse SwapHands()
        {
            var human = Human.LocalHuman;
            if (human == null) return Fail("no local player");
            try
            {
                var behaviour = human.HumanHandsBehaviour;
                if (behaviour == null) return Fail("no HumanHandsBehaviour");
                var method = AccessTools.Method(behaviour.GetType(), "SwapHands");
                if (method == null) return Fail("SwapHands not found on " + behaviour.GetType().Name);
                method.Invoke(behaviour, null);
            }
            catch (Exception ex) { return HttpResponse.Error("swap hands failed: " + ex.Message); }
            return Main(() => HttpResponse.Json(
                "{\"ok\":true,\"player\":" + StateReporter.PlayerJson() + "}"));
        }

        // ---- spawning -------------------------------------------------------

        private static HttpResponse SpawnIntoHand(IDictionary body)
        {
            string prefabName = Json.GetStr(body, "prefab");
            if (string.IsNullOrEmpty(prefabName)) return HttpResponse.Error("missing 'prefab'", 400);

            var human = Human.LocalHuman;
            if (human == null) return Fail("no local player");
            var slot = InventoryManager.ActiveHandSlot;
            if (slot == null) return Fail("no active hand slot");

            var prefab = Prefab.Find(prefabName);
            if (prefab == null)
                return Fail("no prefab named '" + prefabName + "'. GET /prefabs?contains=... to search.");

            if (!GameManager.RunSimulation)
                return Fail("spawning into a slot needs server authority; this client is not the simulation owner. " +
                            "Use /console/exec with 'thing spawn <prefab>' instead, which round-trips through the server.");

            DynamicThing created;
            try { created = OnServer.Create<DynamicThing>(prefab, slot); }
            catch (Exception ex) { return HttpResponse.Error("spawn failed: " + ex.Message); }

            try { slot.RefreshSlotDisplay(); } catch { }

            return HttpResponse.Json(new Json.Obj()
                .Bit("ok", created != null)
                .Str("prefab", prefabName)
                .Int("referenceId", created == null ? 0 : created.ReferenceId)
                .Raw("activeHand", StateReporter.DescribeSlot(slot))
                .ToString());
        }

        private static HttpResponse SpawnIntoWorld(IDictionary body)
        {
            string prefabName = Json.GetStr(body, "prefab");
            if (string.IsNullOrEmpty(prefabName)) return HttpResponse.Error("missing 'prefab'", 400);

            var human = Human.LocalHuman;
            if (human == null) return Fail("no local player");

            // The console path is client-safe: SpawnDynamicThingMaxStack forwards to
            // the server when this process is not the simulation owner.
            if (Json.GetBool(body, "viaServer", !GameManager.RunSimulation))
            {
                try { OnServer.SpawnDynamicThingMaxStack(human.ReferenceId, prefabName); }
                catch (Exception ex) { return HttpResponse.Error("spawn failed: " + ex.Message); }
                return HttpResponse.Json(new Json.Obj()
                    .Bit("ok", true).Str("prefab", prefabName).Str("route", "SpawnDynamicThingMaxStack").ToString());
            }

            var prefab = Prefab.Find(prefabName);
            if (prefab == null) return Fail("no prefab named '" + prefabName + "'");

            Vector3 pos = Json.Has(body, "position")
                ? ReadVector(body, "position", human.ThingTransformPosition)
                : human.ThingTransformPosition + human.EntityForward * Json.GetFloat(body, "distance", 1.5f);
            pos += ReadVector(body, "offset", Vector3.zero);

            Thing created;
            try { created = OnServer.Create<Thing>(prefab, pos, Quaternion.identity); }
            catch (Exception ex) { return HttpResponse.Error("spawn failed: " + ex.Message); }

            return HttpResponse.Json(new Json.Obj()
                .Bit("ok", created != null).Str("prefab", prefabName)
                .Int("referenceId", created == null ? 0 : created.ReferenceId)
                .Vec("position", pos).Str("route", "OnServer.Create").ToString());
        }

        /// <summary>
        /// Places a built Structure on the world grid without the build UI, through
        /// Constructor.SpawnConstruct. That call is client-safe: on a pure client it
        /// sends a ConstructionCreationMessage instead of instantiating locally.
        /// </summary>
        private static HttpResponse SpawnStructure(IDictionary body)
        {
            string prefabName = Json.GetStr(body, "prefab");
            if (string.IsNullOrEmpty(prefabName)) return HttpResponse.Error("missing 'prefab'", 400);

            var human = Human.LocalHuman;
            if (human == null) return Fail("no local player");

            var prefab = Prefab.Find<Structure>(prefabName);
            if (prefab == null) return Fail("no Structure prefab named '" + prefabName + "'");

            Vector3 pos = Json.Has(body, "position")
                ? ReadVector(body, "position", human.ThingTransformPosition)
                : human.ThingTransformPosition + human.EntityForward * Json.GetFloat(body, "distance", 3f);
            pos += ReadVector(body, "offset", Vector3.zero);

            float yaw = Json.GetFloat(body, "yaw", 0f);
            int colorIndex = Json.GetInt(body, "colorIndex", -1);

            Structure placed;
            try
            {
                var grid = GridController.World.WorldToLocal(pos);
                var instance = new CreateStructureInstance(
                    prefab, grid, Quaternion.Euler(0f, yaw, 0f), NetworkManager.LocalClientId, colorIndex);
                placed = Constructor.SpawnConstruct(instance);
            }
            catch (Exception ex)
            {
                return HttpResponse.Error("SpawnConstruct failed: " + ex.Message);
            }

            var o = new Json.Obj()
                .Bit("ok", true).Str("prefab", prefabName).Vec("requestedPosition", pos)
                .Flt("yaw", yaw).Int("colorIndex", colorIndex);
            if (placed == null)
                o.Str("note", "SpawnConstruct returned null: this is a client, so the placement went to the server as a ConstructionCreationMessage. Poll /nearby to confirm it landed.");
            else
                o.Int("referenceId", placed.ReferenceId).Vec("position", placed.ThingTransformPosition);
            return HttpResponse.Json(o.ToString());
        }

        private static string Prefabs(string contains, int limit, string typeFilter)
        {
            var names = new List<string>();
            int scanned = 0;
            try
            {
                foreach (var p in Prefab.AllPrefabs)
                {
                    scanned++;
                    if (p == null) continue;
                    string name = p.PrefabName ?? "";
                    string typeName = p.GetType().Name;
                    if (!string.IsNullOrEmpty(contains) &&
                        name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!string.IsNullOrEmpty(typeFilter) &&
                        typeName.IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    names.Add(name + " [" + typeName + "]");
                    if (limit > 0 && names.Count >= limit) break;
                }
            }
            catch (Exception ex)
            {
                return new Json.Obj().Bit("ok", false).Str("error", ex.Message).ToString();
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return new Json.Obj().Bit("ok", true).Int("scanned", scanned).Int("count", names.Count)
                .StrArray("prefabs", names).ToString();
        }

        // ---- cursor ---------------------------------------------------------

        /// <summary>
        /// Pins the cursor target. CursorManager.SetCursorTarget rebuilds
        /// FoundThing from a raycast every frame, so a forced target only survives
        /// via <see cref="CursorForcePatch"/>, which re-applies it in a postfix.
        ///
        /// A forced target is only safe when it carries a collider. The cursor
        /// state is a tuple, not a single field: vanilla always writes FoundThing
        /// and CursorTargetCollider together, and several consumers read the second
        /// with no null guard. Thing.GetSlot(Collider) is the worst of them, because
        /// its dictionary is eagerly allocated so a null key reaches
        /// Dictionary.TryGetValue and throws every time rather than on some Things.
        /// The full failure and the state inventory are in
        /// Research/GameSystems/CursorManager.md.
        ///
        /// So this refuses a target it cannot give a collider to, rather than
        /// accepting it and wedging the client. See <see cref="CursorForcePatch"/>
        /// for why a wedge cannot be undone by clearing.
        /// </summary>
        private static HttpResponse ForceCursor(IDictionary body)
        {
            if (Json.GetBool(body, "clear", false))
            {
                bool wasWedged = CursorForcePatch.Release();
                return HttpResponse.Json(new Json.Obj()
                    .Bit("ok", true).Bit("cleared", true)
                    .Bit("stateReset", wasWedged)
                    .Str("note", "FoundThing, CursorTargetCollider and FoundTerrain were written " +
                                 "directly; clearing the pin alone does not recover a stale cursor")
                    .ToString());
            }

            long id = Json.GetLong(body, "targetId", 0);
            if (id == 0) return HttpResponse.Error("missing 'targetId' (or pass clear=true)", 400);
            var thing = Thing.Find(id);
            if (thing == null) return Fail("no Thing with reference id " + id);

            var collider = CursorForcePatch.FindCollider(thing);
            if (collider == null)
                return Fail("Thing " + id + " (" + thing.PrefabName + ") exposes no collider, so a forced " +
                            "cursor on it would leave CursorTargetCollider null. That is the state that " +
                            "wedges GameManager.Update permanently (see Research/GameSystems/CursorManager.md). " +
                            "Refusing. Prefer the server-side give-item scenario over cursor forcing.");

            CursorForcePatch.Apply(thing, collider);
            return HttpResponse.Json(new Json.Obj()
                .Bit("ok", true).Int("targetId", id).Str("prefabName", thing.PrefabName)
                .Str("collider", collider.name)
                .Str("colliderType", collider.GetType().Name)
                .Bit("isSlotCollider", CursorForcePatch.IsSlotCollider(thing, collider))
                .ToString());
        }

        // ---- screenshot -----------------------------------------------------

        private static HttpResponse TakeScreenshot(IDictionary body)
        {
            int superSize = Math.Max(1, Json.GetInt(body, "supersize", 1));
            int maxWidth = Json.GetInt(body, "maxWidth", 1920);
            int timeoutMs = Json.GetInt(body, "timeoutMs", 30000);
            string path = Json.GetStr(body, "path");
            bool inline = Json.GetBool(body, "inline", string.IsNullOrEmpty(path));

            string error;
            int w, h;
            var png = Screenshot.CapturePng(superSize, maxWidth, timeoutMs, out error, out w, out h);
            if (png == null) return HttpResponse.Error(error ?? "screenshot produced no bytes");

            string written = null;
            if (!string.IsNullOrEmpty(path))
            {
                try { written = Screenshot.WriteToDisk(png, path); }
                catch (Exception ex) { return HttpResponse.Error("wrote no file: " + ex.Message); }
            }

            if (inline) return HttpResponse.Png(png);

            return HttpResponse.Json(new Json.Obj()
                .Bit("ok", true).Str("path", written).Int("bytes", png.Length)
                .Int("width", w).Int("height", h).ToString());
        }

        // ---- help -----------------------------------------------------------

        private static string Help()
        {
            var endpoints = new List<string>
            {
                "GET  /ping                       liveness plus frame counter, never touches the main thread",
                "GET  /status                     full client state: gameState, network role, world, player, driver counters",
                "GET  /player                     player block only",
                "GET  /colors                     GameManager.CustomColors catalogue with swatch indices",
                "GET  /plugins                    every loaded BepInEx plugin with GUID and version",
                "GET  /nearby?radius=&filter=&limit=   Things around the player",
                "",
                "GET  /console/log?since=&limit=&contains=&source=   tee'd console + BepInEx lines, sequence numbered",
                "POST /console/clear              empty the tee ring",
                "GET  /console/buffer?limit=&contains=   the game's own 1024-line console ring, newest first",
                "POST /console/exec               {command, waitFrames, waitMs} run a console command, return its output",
                "POST /console/print              {text, level=action|error|info} write a marker line",
                "GET  /console/commands?contains= registered console command names",
                "",
                "POST /connect                    {address, port, wait, timeoutMs} Direct Connect",
                "POST /disconnect                 {wait, timeoutMs} leave to the main menu",
                "POST /quit                       {hard} exit the process",
                "GET  /saves                      local save list",
                "POST /savepath                   {path} redirect the user-data root so driven worlds stay out of the real save folder; GET reads it",
                "POST /load                       {save, wait, timeoutMs} load a save by name",
                "POST /newworld                   {world, difficulty, start, wait, timeoutMs}",
                "POST /waitfor                    {phase=menu|joining|loading|inWorld, timeoutMs}",
                "",
                "POST /input/key                  {key, mode=tap|down|up, frames, wait} KeyCode or KeyMap action name",
                "POST /input/scroll               {notches, frames=1, repeat, gapFrames, wait} mouse wheel; one frame is one notch, so frames>1 multiplies the effect",
                "POST /input/mouse                {button, mode, frames} alias for Mouse0/Mouse1",
                "POST /input/releaseall           end every held key",
                "POST /input/clear                drop all synthetic input state",
                "GET  /input/keymap               every KeyMap action and its current binding",
                "POST /input/enable               {enabled} master switch for input injection",
                "POST /input/mouseposition        {x, y} or {clear:true}",
                "",
                "POST /player/teleport            {position:[x,y,z]} or {x,y,z} or {offset:[dx,dy,dz]}",
                "POST /player/look                {yaw, pitch} or {at:[x,y,z]}",
                "POST /player/use                 {targetId} or {cursor:true} use the held item on a target",
                "POST /player/swaphands           swap active and inactive hand",
                "",
                "POST /spawn/hand                 {prefab} put a prefab in the active hand (host or single player)",
                "POST /spawn/world                {prefab, position|offset|distance, viaServer} drop a prefab nearby",
                "POST /spawn/structure            {prefab, position|offset|distance, yaw, colorIndex} place a Structure",
                "GET  /prefabs?contains=&type=&limit=   prefab catalogue",
                "",
                "GET  /modsettings/list           every mod StationeersLaunchPad loaded, with Name and Id",
                "POST /modsettings                {mod, show} force that mod's LaunchPad settings panel on screen so /screenshot can read it",
                "",
                "GET  /modal                      is a confirmation dialog showing, and what does it say",
                "POST /modal/click                {button=1|2|3} dismiss it and run that button's callback",
                "",
                "POST /cursor/force               {targetId} pins target+collider together (refuses a target with no collider); {clear:true} resets the cursor",
                "",
                "GET  /screenshot?path=&supersize=&maxWidth=&inline=   PNG of the full backbuffer, UI included (maxWidth defaults to 1920, 0 disables downscale)",
                "",
                "GET  /config?guid=&filter=       every ConfigEntry of a loaded plugin",
                "POST /config/set                 {guid, section, key, value, save} write a live ConfigEntry",
                "POST /config/reload              {guid} re-read the .cfg from disk",
                "GET  /reflect?type=&member=      read any static field or property by full type name",
            };

            return new Json.Obj()
                .Bit("ok", true)
                .Str("plugin", Plugin.PluginName + " " + Plugin.PluginVersion)
                .Str("note", "Every body field can also be passed as a query parameter. All engine work runs on the Unity main thread.")
                .StrArray("endpoints", endpoints)
                .ToString();
        }
    }

    /// <summary>
    /// Re-applies a forced cursor target after the game's own raycast has run.
    /// CursorManager.SetCursorTarget overwrites FoundThing every frame from
    /// ManagerUpdate, so nothing short of a postfix can hold a pin.
    ///
    /// The cursor is a tuple and this pins all of it. An earlier version wrote only
    /// FoundThing and left CursorTargetCollider at whatever the raycast had just
    /// produced, which is null on a miss and null whenever the console is open. The
    /// pair {FoundThing = X, CursorTargetCollider = null} is a state the game itself
    /// can never produce, and PlantAnalyserCartridge.GetScannedPlant walks straight
    /// into it: Thing.GetSlot(null) hits Dictionary.TryGetValue(null) and throws.
    ///
    /// That throw is unrecoverable, which is the part worth understanding before
    /// touching this class. The cartridge runs from GameManager.Update line ~1498
    /// (OcclusionManager.UpdatingThings.ForEach) and CursorManager.ManagerUpdate,
    /// the only caller of SetCursorTarget, runs from line ~1540 of the same method,
    /// with no try/catch between them. So the exception aborts the frame before the
    /// cursor can be rebuilt, the stale FoundThing survives, and it throws again
    /// next frame, forever. NetworkManager.ManagerUpdate is in the same loop, so a
    /// wedged client also stops processing network packets entirely.
    ///
    /// Two consequences shape the code below:
    ///
    ///   1. Never pin without a collider. FindCollider prefers a collider that is
    ///      actually a key in the target's _slotLookup, so GetSlot returns a real
    ///      Slot instead of merely not throwing. Router refuses the request when
    ///      nothing can be found.
    ///   2. Clearing has to write the game's fields itself. Setting Forced = null
    ///      only stops the postfix re-applying; it cannot help when the reason the
    ///      cursor is stale is that SetCursorTarget is no longer reachable. Release
    ///      therefore assigns FoundThing, CursorTargetCollider and FoundTerrain
    ///      directly. That still lands while wedged because ClientDriver's own pump
    ///      is a separate MonoBehaviour plus an ImGuiManager.LateUpdate postfix,
    ///      neither of which is downstream of the aborted GameManager.Update.
    ///
    /// FoundTerrain is pinned to Invalid deliberately: CursorManager.
    /// GetCurrentVoxelWorld hard-casts CursorTargetCollider to BoxCollider and is
    /// guarded only by CursorTerrain.IsValid, so a valid terrain paired with a
    /// non-box collider is a second way to throw out of the same loop.
    ///
    /// Prefer not to need any of this. The server-side give-item scenario in
    /// ScenarioRunner puts an item in a player's hand without involving the cursor.
    /// </summary>
    [HarmonyPatch]
    internal static class CursorForcePatch
    {
        internal static Thing Forced;
        private static Collider _forcedCollider;

        internal static MethodBase TargetMethod() => AccessTools.Method(typeof(CursorManager), "SetCursorTarget");

        internal static bool Prepare() => TargetMethod() != null;

        internal static void Postfix(CursorManager __instance)
        {
            var forced = Forced;
            if (forced == null) return;
            try
            {
                var collider = _forcedCollider;

                // The target can be destroyed while pinned (painting a thing that
                // then gets deconstructed, an item consumed). Unpin rather than
                // hold a dead reference, and let the vanilla state stand.
                if (collider == null || !forced)
                {
                    Release();
                    return;
                }

                __instance.FoundThing = forced;
                __instance.CursorTargetCollider = collider;
                __instance.FoundTerrain = CursorTerrain.Invalid;
            }
            catch
            {
                // A throw here would land inside the same GameManager.Update loop
                // the whole class exists to keep alive, so give up the pin instead.
                Release();
            }
        }

        /// <summary>
        /// Pins a target and its collider together. Both or neither.
        /// </summary>
        internal static void Apply(Thing thing, Collider collider)
        {
            _forcedCollider = collider;
            Forced = thing;

            // Write once immediately so a caller that never reaches another
            // SetCursorTarget still sees a consistent tuple.
            var instance = CursorManager.Instance;
            if (instance == null) return;
            try
            {
                instance.FoundThing = thing;
                instance.CursorTargetCollider = collider;
                instance.FoundTerrain = CursorTerrain.Invalid;
            }
            catch { }
        }

        /// <summary>
        /// Drops the pin and puts the game's own cursor fields back to the empty
        /// state, so this recovers a client whose SetCursorTarget is unreachable.
        /// Returns true when there was something to reset.
        /// </summary>
        internal static bool Release()
        {
            bool had = Forced != null;
            Forced = null;
            _forcedCollider = null;

            var instance = CursorManager.Instance;
            if (instance == null) return had;
            try
            {
                if (instance.FoundThing != null) had = true;
                instance.FoundThing = null;
                instance.CursorTargetCollider = null;
                instance.FoundTerrain = CursorTerrain.Invalid;
            }
            catch { }
            return had;
        }

        /// <summary>
        /// Best collider to report for a Thing, most faithful first.
        ///
        /// A Slot collider is the only kind that is a key in the target's
        /// _slotLookup, so it is the only one that makes GetSlot(collider) return
        /// something rather than merely not throw. Everything after it is a
        /// structurally valid stand-in.
        /// </summary>
        internal static Collider FindCollider(Thing thing)
        {
            if (thing == null) return null;
            try
            {
                if (thing.Slots != null)
                    foreach (var slot in thing.Slots)
                        if (slot != null && slot.Collider != null && slot.IsInteractable)
                            return slot.Collider;

                var fromList = First(thing._selfColliders) ?? First(thing._staticColliders) ?? First(thing._dynamicColliders);
                if (fromList != null) return fromList;

                return thing.GetComponentInChildren<Collider>();
            }
            catch { return null; }
        }

        internal static bool IsSlotCollider(Thing thing, Collider collider)
        {
            try
            {
                if (thing?.Slots == null || collider == null) return false;
                foreach (var slot in thing.Slots)
                    if (slot != null && ReferenceEquals(slot.Collider, collider)) return true;
            }
            catch { }
            return false;
        }

        private static Collider First(List<Collider> list)
        {
            if (list == null) return null;
            foreach (var c in list)
                if (c != null) return c;
            return null;
        }
    }
}
