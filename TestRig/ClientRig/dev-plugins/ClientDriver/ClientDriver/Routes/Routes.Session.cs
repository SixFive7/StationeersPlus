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
        /// <summary>
        ///     Named here rather than inline because <c>/newworld</c> and <c>/host</c> both hand it
        ///     back, and it is the single most common way a world request fails.
        /// </summary>
        internal const string WorldIdHint =
            "world ids are Lunar, Mars2, Europa3, MimasHerschel, Venus, Vulcan2. " +
            "'Moon' is not one of them, despite the Lunar world being called Moon: Great Mare.";

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

            var clash = IdentityConflictRefusal(allowDuplicateIdentity, "join");
            if (clash != null) return clash;

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

                // Which local interface the JOINING side binds. Nothing used to set this, so the
                // client socket took whatever a wildcard bind selected. On a machine with Hyper-V
                // virtual adapters that is not loopback, and a join to a host bound on 127.0.0.1
                // times out while netstat shows a perfectly healthy listener on both ends. Set it to
                // the SAME address the host was given via /host localIpAddress.
                //
                // Direct field write, never the console 'settings' command: that command persists
                // the whole settings blob to this instance's setting.xml, and a sticky
                // LocalIpAddress is exactly the leftover that makes the NEXT test fail.
                string localIp = Json.GetStr(body, "localIpAddress", null);
                if (!string.IsNullOrEmpty(localIp))
                {
                    var sd = Settings.CurrentData;
                    if (sd != null) sd.LocalIpAddress = localIp;
                }

                // Start recording BEFORE the call, so t=0 is the moment the game was asked to
                // join. Everything this endpoint can observe afterwards has already been undone
                // by its own cleanup; see JoinTrace.
                JoinTrace.Arm(address + ":" + port.ToString(CultureInfo.InvariantCulture));

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

            var poll = PollForRunning(timeoutMs, watchModal: true, failAtMenu: true);
            string modalText = poll.ModalJson;
            string result = poll.Result == "running" ? "connected" : poll.Result;

            // Read the failing state BEFORE cleaning anything up. The ordering is the whole point.
            // Cancel() below reaches NetworkManager.EndConnection and ShutDownRaknet, which put
            // NetworkRole back to None and NetworkState back to Offline, and dispose the RakNet
            // peer with its UDP socket. Anything read afterwards, by this endpoint or by netstat
            // once the call has returned, describes the CLEANUP and not the failure. Two live runs
            // were spent chasing a joiner that "never opened a UDP socket" and was only ever this
            // teardown having already happened.
            string stateAtFailure = null;
            string peerAtFailure = null;
            string statusAtFailure = null;
            if (result != "connected")
            {
                stateAtFailure = SafeMain(() => JoinTrace.StateLine(), null);
                peerAtFailure = SafeMain(() => JoinTrace.ProbePeer().ToJson(), null);
                try { statusAtFailure = MainThreadPump.RunValue(() => StateReporter.Status(), 5000); }
                catch { }
            }

            // With the game's own timer suppressed a dead server leaves the client parked in Joining
            // forever, so clean up after our own timeout.
            if (result == null)
            {
                try { MainThreadPump.RunValue(() => { NetworkClient.Cancel(); return true; }, 5000); }
                catch { }
            }

            // After the cleanup, so the trace carries the teardown too: which method tore RakNet
            // down, at what millisecond, and from where.
            string trace = JoinTrace.DescribeJson();
            JoinTrace.Disarm();

            var o = new Json.Obj().Bit("ok", result == "connected")
                .Str("target", address + ":" + port)
                .Str("result", result ?? "timeout");
            if (modalText != null) o.Raw("dialog", modalText);
            if (result != "connected")
            {
                o.Str("stateAtFailure", stateAtFailure);
                o.Raw("peerAtFailure", peerAtFailure);
                o.Raw("statusAtFailure", statusAtFailure);
                o.Raw("joinTrace", trace);
            }
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
        ///     Refuses an action that would put this instance's ClientId on the wire while a sibling
        ///     is already claiming it. Returns the 409 to send, or null when the way is clear.
        ///
        ///     Enforced at exactly the two moments an id reaches a server, and nowhere else, because
        ///     those are the two moments the damage happens: the server keys a player's body on
        ///     ClientId, <c>Brain.RegisterBrain</c> overwrites silently, and the loser resolves onto
        ///     the winner's character with nothing anywhere warning. A test that believes it has two
        ///     players and has one produces results that look plausible and mean nothing. Hosting
        ///     matters more than joining here, not less: the host consumes a ClientId of its own and
        ///     it exists FIRST, so a joiner that collides with the host takes over the host's body.
        ///
        ///     <c>PeerProbe.Scan</c> is safe from any thread except the Unity main thread, and this
        ///     runs on the HTTP accept thread before any main-thread hop.
        /// </summary>
        private static HttpResponse IdentityConflictRefusal(bool allowDuplicateIdentity, string action)
        {
            if (allowDuplicateIdentity || InstanceManifest.PeerPorts.Count == 0) return null;

            PeerProbe.Scan();
            if (!PeerProbe.ConflictDetected) return null;

            return HttpResponse.Json(new Json.Obj()
                .Bit("ok", false)
                .Str("error", "refusing to " + action + ": " + PeerProbe.ConflictSummary)
                .Raw("peers", PeerProbe.DescribeJson())
                .Str("override", "pass allowDuplicateIdentity=true to " + action + " anyway")
                .ToString(), 409);
        }

        /// <summary>
        ///     The answer from <see cref="PollForRunning"/>. A class rather than an out parameter
        ///     because the poll body is a lambda and a lambda cannot assign one.
        /// </summary>
        private sealed class RunningPoll
        {
            /// <summary>"running", "failed", or null on timeout.</summary>
            internal string Result;

            /// <summary>The dialog that was found and dismissed, as <c>/modal</c> reports it.</summary>
            internal string ModalJson;
        }

        /// <summary>
        ///     Waits for the client to reach <c>GameState.Running</c>. One helper for every endpoint
        ///     that puts this process into a world: <c>/connect</c>, <c>/load</c>, <c>/newworld</c>
        ///     and <c>/host</c> all used to carry their own copy of this loop, and the copies had
        ///     drifted.
        ///
        ///     <paramref name="watchModal"/> handles the case that makes an unattended run hang: on
        ///     failure the game pops a ConfirmationPanel a human would have to click, and nothing
        ///     clears it on its own. Reading it and clicking OK turns a wedged client into a clean
        ///     "failed" that carries the dialog text.
        ///
        ///     <paramref name="failAtMenu"/> is for the joining case only. A join that falls back to
        ///     <c>GameState.None</c> has failed, whereas <c>/load</c>, <c>/newworld</c> and
        ///     <c>/host</c> all START at None, so the same test would trip instantly for them.
        /// </summary>
        private static RunningPoll PollForRunning(int timeoutMs, bool watchModal, bool failAtMenu)
        {
            var poll = new RunningPoll();
            poll.Result = PollUntil(timeoutMs, () =>
            {
                string state = MainThreadPump.RunValue(() => GameManager.GameState.ToString(), 5000);
                if (state == "Running") return "running";

                if (watchModal)
                {
                    string modal = MainThreadPump.RunValue(() => Modal.Describe(), 5000);
                    if (modal != null && modal.IndexOf("\"visible\":true", StringComparison.Ordinal) >= 0)
                    {
                        poll.ModalJson = modal;
                        MainThreadPump.RunValue(() => Modal.Click(1), 5000);
                        return "failed";
                    }
                }

                if (failAtMenu && state == "None") return "failed";
                return null;
            });
            return poll;
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

        /// <summary>
        ///     Why a join did or did not land. The join-path counterpart to <c>/diag/input</c>, and
        ///     the answer to a question <c>/status</c> structurally cannot answer.
        ///
        ///     <c>/status</c> reports what is true NOW. A failed join is defined by things that were
        ///     true and are not any more: <c>NetworkRole</c> was Client and is None again,
        ///     <c>NetworkState</c> was WaitingForConnection and is Offline, a RakNet peer existed
        ///     with a bound UDP socket and has been disposed. So this endpoint reports the RECORDING
        ///     that <see cref="JoinTrace"/> made while the attempt was live, plus a probe of the peer
        ///     as it stands.
        ///
        ///     Read it in this order:
        ///
        ///     <list type="number">
        ///       <item><c>joinTrace.patched</c>. False means the recorder never installed and every
        ///             other field below is worthless.</item>
        ///       <item>The <c>startClient.returned</c> event. <c>result=True</c> means the client DID
        ///             get a socket and DID start a connection attempt, so nothing about a missing
        ///             socket is the explanation. <c>result=False</c> means it refused, and the
        ///             console line beside it names which of the two RakNet calls failed.</item>
        ///       <item>The <c>state</c> events, which carry <c>peer=</c>. <c>active</c> is RakNet's
        ///             own "a socket is bound right now". A slot reading <c>IsConnecting</c> that
        ///             disappears about six seconds in is RakNet abandoning the attempt after its 12
        ///             sends at 500 ms, which the game handles nowhere: ReceiveEvents has no case for
        ///             ConnectionAttemptFailed, so it is dropped in silence.</item>
        ///       <item><c>clientConnected</c>. Present means the RakNet handshake completed and the
        ///             failure is above the transport, in the game's own join handshake.</item>
        ///       <item><c>shutDownRaknet</c> and <c>endConnection</c>, with their callers. These say
        ///             who ended it and when, which is the one thing no later inspection recovers.</item>
        ///     </list>
        /// </summary>
        private static string JoinDiagnostics()
        {
            var o = new Json.Obj().Bit("ok", true);
            o.Raw("epoch", Epoch.Json());
            o.Str("state", JoinTrace.StateLine());
            o.Raw("peer", JoinTrace.ProbePeer().ToJson());

            // The three settings that decide which join path runs and which interface it uses.
            // UseSteamP2P is here because it is the one that LOOKS relevant and is not: /connect
            // always sends "address:port", and JoinClientFromMenu only takes the Steam branch for a
            // single 17-character token, so a joiner left on the default true still joins over
            // RakNet. What it does still do is arm ProcessP2PSessionRequest, which can promote an
            // idle process to NetworkRole.Server and make every later join impossible.
            try
            {
                var data = Settings.CurrentData;
                if (data != null)
                {
                    o.Bit("useSteamP2P", data.UseSteamP2P);
                    o.Str("localIpAddress", data.LocalIpAddress);
                    o.Str("gamePort", data.GamePort);
                }
            }
            catch { }

            // CanBecome's only unconditional refusal. Everything else it checks is NetworkRole,
            // which the state line above already carries.
            try { o.Bit("isNewTutorial", GameManager.IsNewTutorial); } catch { }

            // Written at exactly one place in the whole assembly, on StartClient's success path
            // immediately before NetworkRole is set. Non-null here is therefore evidence that some
            // StartClient call in this process reached that line, and nothing clears it afterwards,
            // so it survives a teardown that resets everything else.
            try
            {
                o.Str("serverAddress", NetworkClient.Address);
                o.Str("serverPort", NetworkClient.Port);
                o.Str("connectionMethod", NetworkClient.ConnectionMethod.ToString());
            }
            catch { }

            o.Raw("joinTrace", JoinTrace.DescribeJson());
            o.Str("note", "peer.active is RakNet's own answer to 'is a UDP socket bound right now'. " +
                          "It is the value netstat cannot supply, because a blocking /connect only " +
                          "returns after its own cleanup has disposed the peer. serverAddress being " +
                          "set while networkRole reads None means StartClient succeeded and the " +
                          "teardown ran, not that the join never started.");
            return o.ToString();
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
        ///
        ///     The change is in memory only. Nothing writes settings on exit at 0.2.6403.27689:
        ///     <c>GameManager.QuitGame()</c> is Close, GameState = None, Process.Kill, and
        ///     <c>WorldManager.OnApplicationQuit</c> only cancels the auto-save timer. What DOES
        ///     persist it is any later <c>settings &lt;name&gt; &lt;value&gt;</c> console command,
        ///     because <c>SettingsCommand.OnValueChanged</c> calls <c>Settings.SaveSettings()</c>,
        ///     which serialises the WHOLE <c>SettingData</c>, this redirect included. Closing the
        ///     in-game settings panel does the same. So a hard exit is not the hygiene measure it
        ///     looks like, and a <c>/console/exec</c> of a settings command is not the harmless one.
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
        ///     3. Redirecting INTO the developer's REAL user-data folder is refused unless the
        ///        caller passes <c>force=true</c>, since that is the exact outcome the endpoint
        ///        exists to prevent. The comparand is <see cref="RealUserDataPath"/>, computed
        ///        without asking the game, for the reason spelled out on that method.
        /// </summary>
        private static HttpResponse SavePath(IDictionary body)
        {
            var data = Settings.CurrentData;
            if (data == null) return Fail("Settings.CurrentData is null");

            string current = data.SavePath;
            string realUserData = RealUserDataPath();
            string reportedDefault = ReportedDefaultPath();

            string wanted = Json.GetStr(body, "path");
            if (string.IsNullOrEmpty(wanted))
                return HttpResponse.Json(new Json.Obj()
                    .Bit("ok", true)
                    .Str("savePath", current)
                    .Str("realUserDataPath", realUserData)
                    .Str("reportedDefaultPath", reportedDefault)
                    .Bit("defaultPathRedirected",
                        !string.IsNullOrEmpty(reportedDefault) && !IsInside(reportedDefault, realUserData))
                    .Bit("insideRealUserData",
                        IsInside(string.IsNullOrEmpty(current) ? reportedDefault : current, realUserData))
                    .Str("note", "realUserDataPath is the tier-1 folder this endpoint refuses to write " +
                                 "into. reportedDefaultPath is StationSaveUtils.DefaultPath as this process " +
                                 "sees it, which StationeersLaunchPad moves when SavePathOverride is set; " +
                                 "it is reported for visibility and is NOT what the refusal compares against.")
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

            if (!Json.GetBool(body, "force", false))
            {
                // Fail closed. If the real folder cannot be computed there is no way to tell whether
                // this redirect lands in it, and guessing wrong writes a driven session's worlds into
                // the developer's own save tree.
                if (string.IsNullOrEmpty(realUserData))
                    return HttpResponse.Error(
                        "refusing to change the save path: the developer's real user-data folder could " +
                        "not be resolved, so the tier-1 check cannot run. Nothing was changed.", 409);

                if (IsInside(resolved, realUserData))
                    return HttpResponse.Error(
                        "refusing to point the save path at '" + resolved + "', which is inside the " +
                        "developer's real user-data folder '" + realUserData + "'. Redirecting a driven " +
                        "session AWAY from that folder is the entire purpose of this endpoint. Pass " +
                        "force=true if this is genuinely what you want. Nothing was changed.", 409);
            }

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
                .Str("realUserDataPath", realUserData)
                .Str("reportedDefaultPath", reportedDefault)
                .Str("note", "in memory only. Nothing writes settings on exit at this game version, " +
                             "so quitting does not persist this. What does persist it is any later " +
                             "'settings <name> <value>' console command or closing the in-game " +
                             "settings panel: both call Settings.SaveSettings(), which serialises the " +
                             "whole SettingData including this path. /status.settingsPath names the " +
                             "file that would be written.")
                .ToString());
        }

        /// <summary>
        ///     The developer's REAL user-data folder, computed here rather than read from the game.
        ///
        ///     This is the tier-1 folder every save-writing endpoint refuses to touch, and it is the
        ///     one value in this file that must not route through anything a mod can move.
        ///     <c>StationSaveUtils.DefaultPath</c> looks like the obvious source and is the wrong
        ///     one: StationeersLaunchPad prefixes that getter and returns its own
        ///     <c>SavePathOverride</c> instead, which on a provisioned instance is the instance's own
        ///     <c>data/&lt;instance&gt;/userdata</c>. Comparing against the patched value inverted
        ///     both answers. Pointing a running instance at the developer's real save folder was not
        ///     refused at all and needed no <c>force=true</c>, while a legitimate redirect inside the
        ///     instance's own save root WAS refused.
        ///
        ///     The formula is the game's own unpatched one (<c>StationSaveUtils.DefaultPath</c> for a
        ///     non-batch build) and the same one <c>Get-RigUserDataPath</c> in the launcher computes, so
        ///     the launcher and the plugin agree on which folder is off limits. Resolved from the
        ///     Windows shell folder rather than hardcoded, so it carries no developer-specific path.
        ///
        ///     Returns null only if the shell folder cannot be read. Callers treat null as "refuse",
        ///     never as "allow".
        /// </summary>
        internal static string RealUserDataPath()
        {
            try
            {
                string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (string.IsNullOrEmpty(documents)) return null;
                return Path.Combine(Path.Combine(documents, "My Games"), "Stationeers");
            }
            catch { return null; }
        }

        /// <summary>
        ///     <c>StationSaveUtils.DefaultPath</c> as this process sees it, read reflectively so a
        ///     rename degrades to "not reported" rather than to a plugin that will not load.
        ///
        ///     REPORTING ONLY. It is whatever StationeersLaunchPad's <c>SavePathOverride</c> prefix
        ///     decides, so it answers "where does this instance think the default is", which is a
        ///     useful thing to see next to the real folder and a useless thing to gate on. The gate
        ///     is <see cref="RealUserDataPath"/>.
        /// </summary>
        private static string ReportedDefaultPath()
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

        /// <summary>
        ///     Where this process will actually write a world right now, without the directory
        ///     creation <c>StationSaveUtils.GetSavePath()</c> performs as a side effect. Same rule
        ///     the game uses: the configured save path, falling back to the default when it is empty.
        /// </summary>
        internal static string EffectiveSaveRoot()
        {
            try
            {
                var data = Settings.CurrentData;
                string configured = data == null ? null : data.SavePath;
                return string.IsNullOrEmpty(configured) ? ReportedDefaultPath() : configured;
            }
            catch { return null; }
        }

        /// <summary>
        ///     True when the given save root is safely outside the developer's real user-data folder.
        ///     Fails closed: an unresolvable real folder, or an unresolvable root, is NOT isolated.
        /// </summary>
        internal static bool IsIsolatedSaveRoot(string root)
        {
            string real = RealUserDataPath();
            if (string.IsNullOrEmpty(real) || string.IsNullOrEmpty(root)) return false;
            return !IsInside(root, real);
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

            string result = PollForRunning(timeoutMs, watchModal: false, failAtMenu: false).Result;
            return HttpResponse.Json(new Json.Obj()
                .Bit("ok", result == "running").Str("save", name)
                .Str("result", result == "running" ? "loaded" : "timeout").ToString(),
                result == "running" ? 200 : 409);
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

            string result = PollForRunning(timeoutMs, watchModal: false, failAtMenu: false).Result;
            return HttpResponse.Json(new Json.Obj()
                .Bit("ok", result == "running").Str("world", world)
                .Str("result", result == "running" ? "loaded" : "timeout")
                .Str("note", result == "running" ? null : WorldIdHint)
                .ToString(),
                result == "running" ? 200 : 409);
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
