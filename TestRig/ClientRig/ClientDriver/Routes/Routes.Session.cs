using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Assets.Scripts.Serialization;
using HarmonyLib;
using UnityEngine;
using GameManager = Assets.Scripts.GameManager;
using NetworkClient = Assets.Scripts.NetworkClient;

namespace ClientDriver
{
    /// <summary>
    ///     Session routes: joining, leaving, loading, and the two settings a driven instance must
    ///     get right before it does anything else (where it saves, and who it says it is).
    /// </summary>
    internal static partial class Router
    {
        // ---- instance identity ---------------------------------------------

        /// <summary>
        ///     Who this instance is, in one request. This is what makes a snapshot, a screenshot or
        ///     a log line attributable without cross-referencing a port table, and it is what
        ///     <see cref="PeerProbe"/> calls on siblings to detect a duplicate ClientId.
        /// </summary>
        private static string InstanceRoute(IDictionary body)
        {
            if (Json.GetBool(body, "rescan", false)) PeerProbe.Scan();
            else PeerProbe.ScanAsync();

            return new Json.Obj()
                .Bit("ok", true)
                .Raw("instance", InstanceManifest.DescribeJson())
                .Raw("peers", PeerProbe.DescribeJson())
                .Str("effectiveClientId", Identity.EffectiveClientId.ToString(CultureInfo.InvariantCulture))
                .Str("effectiveUsername", Identity.EffectiveUsername)
                .ToString();
        }

        /// <summary>
        ///     Read or set this instance's presented player identity.
        ///
        ///     GET reports the live cookie plus what the override is doing. POST
        ///     <c>{clientId, username}</c> rewrites it in place, which matters because the value
        ///     only has to be correct at the moment the join handshake copies it into
        ///     <c>VerifyPlayerMessage</c>: an instance that booted with the wrong identity can be
        ///     corrected without a restart.
        /// </summary>
        private static HttpResponse IdentityRoute(IDictionary body)
        {
            string wantedId = Json.GetStr(body, "clientId");
            string wantedName = Json.GetStr(body, "username");

            if (!string.IsNullOrEmpty(wantedId))
            {
                ulong parsed;
                if (!ulong.TryParse(wantedId.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    return HttpResponse.Error("clientId '" + wantedId + "' is not a ulong", 400);
                if (parsed == 0)
                    return HttpResponse.Error("clientId 0 is the batch-mode sentinel; pick a non-zero id", 400);
                Identity.OverrideClientId = parsed;
            }
            if (!string.IsNullOrEmpty(wantedName)) Identity.OverrideUsername = wantedName;
            if (!string.IsNullOrEmpty(wantedId) || !string.IsNullOrEmpty(wantedName))
            {
                Identity.Apply();
                // The identity just changed, so any cached peer verdict is stale.
                PeerProbe.ScanAsync(0);
            }

            object cookie = Identity.CurrentCookie();
            return HttpResponse.Json(new Json.Obj()
                .Bit("ok", true)
                .Str("instanceName", InstanceManifest.Name)
                .Str("localClientId", Assets.Scripts.Networking.NetworkManager.LocalClientId.ToString(CultureInfo.InvariantCulture))
                .Str("username", Assets.Scripts.Networking.NetworkManager.Username)
                .Bit("cookiePresent", cookie != null)
                .Str("overrideClientId", Identity.OverrideClientId.ToString(CultureInfo.InvariantCulture))
                .Str("overrideUsername", Identity.OverrideUsername ?? "")
                .Bit("overrideApplied", Identity.Applied)
                .Int("applyCount", Identity.ApplyCount)
                .Int("suppressedCookieSaves", Identity.SuppressedSaves)
                .Bit("duplicateIdentity", PeerProbe.ConflictDetected)
                .Str("duplicateIdentityDetail", PeerProbe.ConflictSummary)
                .Str("lastError", Identity.LastError ?? "")
                .ToString());
        }

        // ---- connect / disconnect -------------------------------------------

        /// <summary>
        ///     Direct connect, the same call the Join menu's Direct Connect button makes.
        ///     <c>NetworkClient.JoinClientFromMenu("ip:port")</c> runs ClientPreJoin
        ///     (GameState -> Joining), parses the address, and calls
        ///     <c>NetworkManager.StartClient</c>. Calling StartClient directly would skip the menu
        ///     teardown and the connection timer, so it is not used here.
        /// </summary>
        private static HttpResponse Connect(IDictionary body)
        {
            string address = Json.GetStr(body, "address", "127.0.0.1");
            int port = Json.GetInt(body, "port", 28016);
            bool wait = Json.GetBool(body, "wait", true);
            int timeoutMs = Json.GetInt(body, "timeoutMs", 180000);
            bool suppressTimeout = Json.GetBool(body, "suppressTimeout", true);
            bool allowDuplicateIdentity = Json.GetBool(body, "allowDuplicateIdentity", false);

            // Duplicate identity is checked HERE and nowhere else, because the join is where the
            // damage happens: the server keys a player's body on ClientId, RegisterBrain overwrites
            // silently, and the second joiner takes over the first joiner's character with nothing
            // anywhere warning. A test that believes it has two players and has one produces
            // results that look plausible and mean nothing.
            if (!allowDuplicateIdentity && InstanceManifest.PeerPorts.Count > 0)
            {
                PeerProbe.Scan();
                if (PeerProbe.ConflictDetected)
                    return HttpResponse.Json(new Json.Obj()
                        .Bit("ok", false)
                        .Str("error", "refusing to join: " + PeerProbe.ConflictSummary)
                        .Raw("peers", PeerProbe.DescribeJson())
                        .Str("override", "pass allowDuplicateIdentity=true to join anyway")
                        .ToString(), 409);
            }

            // The NetworkClient component only becomes findable some way into boot, so wait for it
            // rather than failing the request outright.
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

                // NetworkClient.OnJoinStart, called inside JoinClientFromMenu, arms a 10 second
                // timer whose only job is to give up and pop a modal. Ten seconds is nowhere near
                // enough for a heavily modded dedicated server: the handshake reaches the server
                // ("A connection is incoming" in server.log) and then the client cancels itself
                // mid-transfer. Stop the timer and let this endpoint's own timeout be the authority.
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

            // On expiry the game pops a ConfirmationPanel that a human would have to click. Dismiss
            // it here so an unattended run gets a clean "failed" rather than a wedged client.
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

            // With the game's own timer suppressed a dead server leaves the client parked in Joining
            // forever, so clean up after our own timeout.
            if (result == null)
            {
                try { MainThreadPump.RunValue(() => { NetworkClient.Cancel(); return true; }, 5000); }
                catch { }
            }

            var o = new Json.Obj().Bit("ok", result == "connected")
                .Str("target", address + ":" + port)
                .Str("result", result ?? "timeout");
            if (modalText != null) o.Raw("dialog", modalText);
            // Observed twice: the first connect after a server restart returns 409 and the second
            // succeeds, because the client is still settling from the previous disconnect. Say so
            // rather than leaving a caller to rediscover it.
            if (result != "connected")
                o.Str("hint", "a first attempt after a server restart often fails while the client " +
                              "settles from the previous disconnect; retry two or three times with a gap");
            try
            {
                o.Raw("status", MainThreadPump.RunValue(() => StateReporter.Status(), 5000));
            }
            catch { }
            return HttpResponse.Json(o.ToString(), result == "connected" ? 200 : 409);
        }

        /// <summary>
        ///     <c>FindObjectOfType</c> only sees active, enabled components. The NetworkClient lives
        ///     on a DontDestroyOnLoad object that is not always active at the menu, so fall back to
        ///     the whole-object sweep, which includes inactive ones. Without the fallback a connect
        ///     issued during boot fails with a misleading "still booting".
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
            // Answer before the process dies, otherwise the caller sees a dropped socket rather
            // than a confirmation.
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

        // ---- saves ----------------------------------------------------------

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
        ///     Reads or redirects the user-data root. Every save the game writes lands in
        ///     <c>&lt;SavePath&gt;/saves</c>, resolved on each call to
        ///     <c>StationSaveUtils.GetSavePath()</c>, so pointing this at a scratch directory before
        ///     creating a world keeps a driven test session out of the developer's real save folder.
        ///     The change is in memory; the game persists settings on a clean exit, so put it back
        ///     at the end of a session or exit hard.
        ///
        ///     THIS ENDPOINT IS SAFETY CRITICAL and is written defensively on three counts, because
        ///     the failure mode is "a driven session writes worlds into the developer's real save
        ///     folder" and that is not recoverable by retrying.
        ///
        ///     1. The path is echoed back, both as received and as resolved to a full path, so a
        ///        caller can verify what actually landed instead of trusting that it round-tripped.
        ///     2. A path containing a control character is REFUSED rather than used. A backslash
        ///        path in a JSON body used to be silently mangled (see <c>Json.ParseStr</c>); the
        ///        reader now preserves undefined escapes, but <c>\b</c> and <c>\f</c> are escapes
        ///        JSON really does define, so <c>C:\builds</c> and <c>C:\files</c> still decode to
        ///        something with a control character in it. Refusing is the only honest answer,
        ///        with the two ways to send it correctly named in the error.
        ///     3. Redirecting INTO the game's own default user-data folder is refused unless the
        ///        caller passes <c>force=true</c>, since that is the exact outcome the endpoint
        ///        exists to prevent.
        /// </summary>
        private static HttpResponse SavePath(IDictionary body)
        {
            var data = Settings.CurrentData;
            if (data == null) return Fail("Settings.CurrentData is null");

            string current = data.SavePath;
            string wanted = Json.GetStr(body, "path");
            if (string.IsNullOrEmpty(wanted))
                return HttpResponse.Json(new Json.Obj()
                    .Bit("ok", true)
                    .Str("savePath", current)
                    .Str("defaultPath", DefaultUserDataPath())
                    .ToString());

            foreach (char c in wanted)
            {
                if (c >= ' ' && c != '\u007f') continue;
                return HttpResponse.Error(
                    "the path contains a control character (U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture) +
                    "), which means a backslash escape was decoded. JSON defines \\b and \\f, so " +
                    "\"C:\\builds\" and \"C:\\files\" do not survive a request body. Send the path as a " +
                    "query parameter (POST /savepath?path=C%3A%5Cbuilds%5Crig) or double every " +
                    "backslash in the body (\"C:\\\\builds\\\\rig\"). Nothing was changed.", 400);
            }

            string resolved;
            try { resolved = Path.GetFullPath(wanted); }
            catch (Exception ex) { return HttpResponse.Error("'" + wanted + "' is not a usable path: " + ex.Message, 400); }

            string defaultPath = DefaultUserDataPath();
            if (!Json.GetBool(body, "force", false) && IsInside(resolved, defaultPath))
                return HttpResponse.Error(
                    "refusing to point the save path at '" + resolved + "', which is inside the game's " +
                    "default user-data folder '" + defaultPath + "'. Redirecting a driven session AWAY " +
                    "from that folder is the entire purpose of this endpoint. Pass force=true if this " +
                    "is genuinely what you want. Nothing was changed.", 409);

            try
            {
                Directory.CreateDirectory(resolved);
                Directory.CreateDirectory(Path.Combine(resolved, "saves"));
                Directory.CreateDirectory(Path.Combine(resolved, "scripts"));
                Directory.CreateDirectory(Path.Combine(resolved, "mods"));
            }
            catch (Exception ex) { return HttpResponse.Error("could not create " + resolved + ": " + ex.Message); }

            data.SavePath = resolved;
            return HttpResponse.Json(new Json.Obj()
                .Bit("ok", true)
                .Str("previous", current)
                .Str("requestedPath", wanted)
                .Str("savePath", data.SavePath)
                .Str("defaultPath", defaultPath)
                .Str("note", "in memory only; the game persists settings on a clean exit, so restore " +
                             "it at the end of the session or exit with POST /quit {\"hard\":true}")
                .ToString());
        }

        /// <summary>
        ///     <c>StationSaveUtils.DefaultPath</c>, read reflectively so a rename degrades to "the
        ///     safety check cannot run" rather than to a plugin that will not load.
        /// </summary>
        private static string DefaultUserDataPath()
        {
            try
            {
                var t = AccessTools.TypeByName("Assets.Scripts.Serialization.StationSaveUtils")
                        ?? AccessTools.TypeByName("StationSaveUtils");
                if (t == null) return null;
                var p = AccessTools.Property(t, "DefaultPath");
                if (p != null) return p.GetValue(null, null) as string;
                var f = AccessTools.Field(t, "DefaultPath");
                return f == null ? null : f.GetValue(null) as string;
            }
            catch { return null; }
        }

        private static bool IsInside(string candidate, string root)
        {
            if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(root)) return false;
            try
            {
                string a = Path.GetFullPath(candidate).TrimEnd('\\', '/');
                string b = Path.GetFullPath(root).TrimEnd('\\', '/');
                if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
                return a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // ---- world lifecycle -------------------------------------------------

        private static HttpResponse LoadSave(IDictionary body)
        {
            string name = Json.GetStr(body, "save");
            if (string.IsNullOrEmpty(name)) return HttpResponse.Error("missing 'save'", 400);
            bool wait = Json.GetBool(body, "wait", true);
            int timeoutMs = Json.GetInt(body, "timeoutMs", 180000);

            var pre = Main(() =>
            {
                Assets.Scripts.ConsoleWindow.Submit("load \"" + name + "\"");
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
            string world = Json.GetStr(body, "world", "Lunar");
            string difficulty = Json.GetStr(body, "difficulty", "Normal");
            string start = Json.GetStr(body, "start", "Default");
            bool wait = Json.GetBool(body, "wait", true);
            int timeoutMs = Json.GetInt(body, "timeoutMs", 300000);

            var pre = Main(() =>
            {
                Assets.Scripts.ConsoleWindow.Submit("new " + world + " " + difficulty + " " + start);
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
                .Bit("ok", result == "loaded").Str("world", world).Str("result", result ?? "timeout")
                .Str("note", result == "loaded" ? null :
                    "world ids are Lunar, Mars2, Europa3, MimasHerschel, Venus, Vulcan2. " +
                    "'Moon' is not one of them, despite the Lunar world being called Moon: Great Mare.")
                .ToString(),
                result == "loaded" ? 200 : 409);
        }

        /// <summary>
        ///     Blocks until the client reaches a named phase, so a harness can say "wait until we
        ///     are in a world" without writing its own poll loop.
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
    }
}
