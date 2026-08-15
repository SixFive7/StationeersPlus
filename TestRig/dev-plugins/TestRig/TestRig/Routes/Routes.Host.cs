using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Assets.Scripts.Serialization;
using HarmonyLib;
using ConsoleWindow = Assets.Scripts.ConsoleWindow;
using GameManager = Assets.Scripts.GameManager;

namespace TestRig
{
    /// <summary>
    ///     The two things a driven instance could not do until now: host its own multiplayer
    ///     session, and persist the world it is holding.
    ///
    ///     Both are shaped the same way and for the same reason. Each asks the game to start
    ///     something asynchronous, then WAITS for evidence that the thing actually happened, and
    ///     each refuses to answer 200 on anything weaker than that evidence. "The call returned" is
    ///     not evidence in either case: <c>NetworkServer.Host()</c> no-ops from the main menu and
    ///     gives up quietly after three failed binds, and the save command hands off to a UniTask
    ///     that reports only through the console.
    /// </summary>
    internal static partial class Router
    {
        // ---- host -----------------------------------------------------------

        /// <summary>
        ///     Turns this instance into a listen host: one process that runs the simulation, accepts
        ///     remote clients, and plays a character. Modelled on <c>/connect</c> rather than on
        ///     <c>/newworld</c>, because it is the other endpoint that changes this process's
        ///     network role, so it carries the same identity refusal, the same per-step main-thread
        ///     hop (never one long <c>Main(...)</c> against the 20 s default), and the same embedded
        ///     <c>/status</c>.
        ///
        ///     The order is the whole trick. <c>Settings.CurrentData.StartLocalHost</c> is read by
        ///     <c>GameManager.StartGame()</c> at world entry and by nothing else afterwards, so the
        ///     settings block has to land BEFORE the load or the create. Setting it on a world that
        ///     is already up does nothing.
        ///
        ///     Those settings are written as DIRECT FIELD ASSIGNMENTS, deliberately, not through
        ///     the <c>settings &lt;name&gt; &lt;value&gt;</c> console command that <c>/load</c> and
        ///     <c>/newworld</c> use for their own work. The console route looks equivalent and is a
        ///     trap: <c>SettingsCommand.OnValueChanged</c> calls <c>Settings.SaveSettings()</c>,
        ///     which serialises the entire <c>SettingData</c> to <c>setting.xml</c>, so one call
        ///     would persist <c>StartLocalHost=true</c> and the next boot of this instance would
        ///     come up hosting while a test believed it had a plain joiner. A direct write stays in
        ///     memory and dies with the process. <c>/status.startLocalHostPersisted</c> reports the
        ///     on-disk value so that distinction is visible rather than assumed.
        ///
        ///     See <c>Research/GameSystems/ListenHost.md</c> for the boot chain and for why each
        ///     field below has the value it has.
        /// </summary>
        private static HttpResponse HostWorld(IDictionary body)
        {
            // Refused, not ignored. See the gate below for why the parameter is gone; answering
            // rather than silently reinterpreting is the same rule /savepath and /dlc/remove follow.
            if (Json.Has(body, "requireIsolatedSavePath"))
                return HttpResponse.Error(
                    "'requireIsolatedSavePath' is not a parameter of this endpoint any more, and nothing " +
                    "was done. Setting it false used to let this instance create a world inside the " +
                    "developer's own save tree. That is now refused unconditionally. Remove the parameter; " +
                    "if the refusal fires, GET /savepath reports where this instance would write and " +
                    "POST /savepath moves it.", 400);

            string save = Json.GetStr(body, "save");
            string world = Json.GetStr(body, "world");
            bool haveSave = !string.IsNullOrEmpty(save);
            bool haveWorld = !string.IsNullOrEmpty(world);
            if (haveSave == haveWorld)
                return HttpResponse.Error(
                    "pass exactly one of 'save' (host an existing save) or 'world' (create a new " +
                    "world and host it). " + WorldIdHint, 400);

            string difficulty = Json.GetStr(body, "difficulty", "Normal");
            string start = Json.GetStr(body, "start", "Default");
            int port = Json.GetInt(body, "port", Plugin.EffectiveGamePort);
            string serverName = Json.GetStr(body, "serverName",
                string.IsNullOrEmpty(InstanceManifest.Name) ? "TestRig" : InstanceManifest.Name);
            string password = Json.GetStr(body, "password", "");
            int maxPlayers = Json.GetInt(body, "maxPlayers", 4);
            // Which local interface the RakNet listener binds. Loopback is the right default for a
            // rig on one machine, but it is NOT always reachable: on a host with Hyper-V virtual
            // adapters the joining side's wildcard-bound socket has no reason to select loopback,
            // and the join times out with a listener that netstat shows as perfectly healthy. Pass
            // the machine's real LAN address (the same value the developer's own client uses in its
            // hidden LocalIpAddress setting) when that happens. The joiner must be given the SAME
            // address via /connect localIpAddress, or the two bind different interfaces.
            string localIp = Json.GetStr(body, "localIpAddress", "127.0.0.1");
            bool wait = Json.GetBool(body, "wait", true);
            int timeoutMs = Json.GetInt(body, "timeoutMs", 300000);
            bool allowDuplicateIdentity = Json.GetBool(body, "allowDuplicateIdentity", false);

            // A world created by the console 'new' command has an EMPTY CurrentStationName, and
            // that is the name every later save resolves through: the bare 'save' command, this
            // plugin's POST /save with no name, and the game's own autosave. So a created world
            // cannot be saved by anything at all until a first NAMED save assigns one, and the
            // launcher's ordered teardown discovers that at the worst possible moment, when the
            // world it is about to quit on top of is the thing it was trying to keep. Measured on
            // every host check: "no 'name' was given and the current station name could not be
            // read" and then "Save not confirmed; --force, quitting anyway".
            //
            // So this endpoint names what it creates. Default is the world id, which is the only
            // thing a caller that passed nothing has told us about the world. Pass stationName to
            // choose, or an empty string to deliberately leave it unnamed and accept that nothing
            // can save it. A world LOADED from a save already has its name and is never touched.
            string stationName = Json.Has(body, "stationName")
                ? Json.GetStr(body, "stationName", "")
                : world;

            if (port < 1 || port > 65535)
                return HttpResponse.Error(
                    "port " + port.ToString(CultureInfo.InvariantCulture) + " is out of range; pass 1-65535", 400);
            if (maxPlayers < 1)
                return HttpResponse.Error("maxPlayers must be at least 1", 400);

            // The host's ClientId exists before any joiner's, so a collision here is worse than a
            // collision on a join: a joiner that shares the host's id takes over the HOST's body.
            var clash = IdentityConflictRefusal(allowDuplicateIdentity, "host");
            if (clash != null) return clash;

            string saveRoot = null;
            bool isolated = false;
            long beforeSeq = ConsoleTap.NextSeq;

            var pre = Main(() =>
            {
                string state = GameManager.GameState.ToString();
                if (state != "None")
                    return Fail("cannot host from gameState=" + state + ". This endpoint loads or " +
                                "creates the world itself and has to start from the main menu, " +
                                "because StartLocalHost is only read at world entry. POST /disconnect " +
                                "first.");

                string role = StateReporter.Role();
                if (role != "menu")
                    return Fail("cannot host: this process already reports role=" + role +
                                " at the main menu, so its NetworkRole is not None and a clean host " +
                                "is not possible. The known cause is an inbound Steam P2P session " +
                                "request promoting an idle process to server; that is what setting " +
                                "UseSteamP2P false prevents. Restart the instance.");

                // THE TIER-1 GATE. Unconditional, and fails closed: an unresolvable save root or
                // an unresolvable real folder is NOT isolated.
                //
                // requireIsolatedSavePath=false used to override this. The parameter is gone. Its
                // own error message had to end with "never pass it", which is the tell that a
                // parameter should not have existed: this endpoint creates a world, the world is
                // written wherever the save root points, and there is no test worth running that
                // needs that world inside the developer's own save tree.
                saveRoot = EffectiveSaveRoot();
                isolated = IsIsolatedSaveRoot(saveRoot);
                if (!isolated)
                    return HttpResponse.Error(
                        "refusing to host: this instance would write its world to '" +
                        (saveRoot ?? "(unresolved)") + "', which is inside the developer's real " +
                        "user-data folder '" + (RealUserDataPath() ?? "(unresolved)") + "'. That " +
                        "folder is off limits to the rig. Re-provision the instance so " +
                        "StationeersLaunchPad's SavePathOverride points at its own save root, or " +
                        "POST /savepath first and check GET /savepath afterwards. There is no " +
                        "override: requireIsolatedSavePath was removed so this cannot be argued " +
                        "past by an agent that has not read the rules.", 409);

                var data = Settings.CurrentData;
                if (data == null) return Fail("Settings.CurrentData is null");

                // Direct field writes, never the console 'settings' command. See the method remarks.
                data.StartLocalHost = true;
                // Without this, GetIPv4Address() filters the 127.x range out and binds the LAN
                // address, so nothing is listening on loopback and a Direct Connect to 127.0.0.1
                // finds nothing at all. Overridable because loopback is not universally reachable;
                // see the localIpAddress remarks where it is parsed.
                data.LocalIpAddress = localIp;
                // String-typed in the game; Convert.ToUInt16 reads it in NetworkServer.Host.
                data.GamePort = port.ToString(CultureInfo.InvariantCulture);
                data.ServerName = serverName;
                data.ServerPassword = password ?? "";
                data.ServerMaxPlayers = maxPlayers;
                // No master-server registration and no UPnP discovery round for a loopback session.
                data.ServerVisible = false;
                data.UPNPEnabled = false;
                // Not needed for RakNet, and it disarms ProcessP2PSessionRequest, which can promote
                // an idle process to NetworkRole.Server on an inbound request. CanBecome then
                // refuses StartClient and that instance can never join anything again.
                data.UseSteamP2P = false;

                if (haveWorld) ConsoleWindow.Submit("new " + world + " " + difficulty + " " + start);
                else ConsoleWindow.Submit("load \"" + save + "\"");

                return HttpResponse.Json(new Json.Obj()
                    .Bit("ok", true)
                    .Str("role", "listenHost")
                    .Bit("waiting", wait)
                    .Int("hostPort", port)
                    .Str("serverName", serverName)
                    .Bit("hasPassword", !string.IsNullOrEmpty(password))
                    .Str("world", haveWorld ? world : null)
                    .Str("save", haveSave ? save : null)
                    .Str("savePath", saveRoot)
                    .Str("saveRoot", isolated ? "instance" : "default")
                    .ToString());
            });
            if (pre.Status != 200) return pre;

            if (!wait)
                return HttpResponse.Json(new Json.Obj()
                    .Bit("ok", true)
                    .Str("role", "listenHost")
                    .Bit("waiting", false)
                    .Bit("hosting", false)
                    .Int("hostPort", port)
                    .Str("serverName", serverName)
                    .Bit("hasPassword", !string.IsNullOrEmpty(password))
                    .Str("world", haveWorld ? world : null)
                    .Str("save", haveSave ? save : null)
                    .Str("savePath", saveRoot)
                    .Str("saveRoot", isolated ? "instance" : "default")
                    .Str("note", "wait=false, so nothing was asserted. 'hosting' is the value at the " +
                                 "moment of the request, not a result. Poll /status.hosting.")
                    .ToString());

            // Stage one: the world has to come up at all.
            var poll = PollForRunning(timeoutMs, watchModal: true, failAtMenu: false);
            if (poll.Result != "running")
            {
                var o = new Json.Obj()
                    .Bit("ok", false)
                    .Str("result", poll.Result == "failed" ? "failed" : "timeout")
                    .Str("world", haveWorld ? world : null)
                    .Str("save", haveSave ? save : null)
                    .Str("error", "the world never reached GameState.Running, so hosting was never " +
                                  "attempted. GameManager.StartGame is the only thing that calls " +
                                  "NetworkServer.Host.");
                if (poll.ModalJson != null) o.Raw("dialog", poll.ModalJson);
                if (haveWorld) o.Str("hint", WorldIdHint);
                o.Raw("consoleTail", ConsoleTailArray(beforeSeq, 25));
                AttachStatus(o);
                return HttpResponse.Json(o.ToString(), 409);
            }

            // Stage two: the post-condition. Running does not imply hosting. StartGame awaits
            // NetworkServer.Host(), which retries a failed bind three times at one second apart and
            // then returns quietly, so allow for that before calling it a failure.
            string hosted = PollUntil(15000, () => MainThreadPump.RunValue(
                () => (StateReporter.Hosting() && StateReporter.Role() == "listenHost") ? "yes" : null, 5000));

            if (hosted == null)
            {
                var o = new Json.Obj()
                    .Bit("ok", false)
                    .Str("result", "notHosting")
                    .Bit("hosting", SafeMain(() => StateReporter.Hosting(), false))
                    .Str("role", SafeMain(() => StateReporter.Role(), "unknown"))
                    .Int("requestedPort", port)
                    .Str("world", haveWorld ? world : null)
                    .Str("save", haveSave ? save : null)
                    .Str("error", "the world is up but NetworkServer.IsHosting is false, so hosting " +
                                  "silently did not happen. The usual cause is the port: " +
                                  "NetworkManager.StartServer is retried three times a second apart " +
                                  "and then gives up with nothing but a console line. Check the " +
                                  "console tail below, and check that no other process holds UDP " +
                                  "port " + port.ToString(CultureInfo.InvariantCulture) + ".");
                o.Raw("consoleTail", ConsoleTailArray(beforeSeq, 25));
                AttachStatus(o);
                return HttpResponse.Json(o.ToString(), 409);
            }

            // Only for a world this endpoint CREATED, and only once it is up and hosting: the
            // save command is scoped HostOrSinglePlayer and refuses anything but Running or
            // Paused, so there is no earlier point at which this could work.
            string stationNameError = null;
            bool stationNameAssigned = false;
            if (haveWorld && !string.IsNullOrEmpty(stationName))
            {
                stationNameAssigned = AssignStationName(stationName, 180000, out stationNameError);
            }

            var ok = new Json.Obj()
                .Bit("ok", true)
                .Str("role", "listenHost")
                .Bit("hosting", true)
                .Int("hostPort", SafeMain(() => StateReporter.HostPort(), 0))
                .Str("serverName", serverName)
                .Bit("hasPassword", !string.IsNullOrEmpty(password))
                .Str("world", haveWorld ? world : null)
                .Str("save", haveSave ? save : null)
                .Str("stationName", haveWorld ? stationName : null)
                .Bit("stationNameAssigned", stationNameAssigned)
                .Str("savePath", saveRoot)
                .Str("saveRoot", isolated ? "instance" : "default")
                .Str("localClientId", SafeMain(
                    () => Assets.Scripts.Networking.NetworkManager.LocalClientId
                        .ToString(CultureInfo.InvariantCulture), null))
                .Str("username", SafeMain(
                    () => Assets.Scripts.Networking.NetworkManager.Username, null))
                .Int("playersInGame", SafeMain(
                    () => Assets.Scripts.Networking.NetworkManager.TotalPlayersInGame, 0))
                .Raw("connectedClients", SafeMain(
                    () => StateReporter.ConnectedClientsJson(), "[]"))
                .Str("joinWith", "POST /connect {\"address\":\"127.0.0.1\",\"port\":" +
                                 port.ToString(CultureInfo.InvariantCulture) + "} from another instance");

            // Still a 200: the world is up and hosting, which is what this endpoint asserts. But
            // an unnamed world cannot be saved by anything, so a caller that reads only the status
            // code must not be left to discover that at teardown.
            if (haveWorld && !stationNameAssigned)
            {
                ok.Str("warning", string.IsNullOrEmpty(stationName)
                    ? "hosting, but stationName was explicitly empty so this world has no station " +
                      "name. Nothing can save it: the bare save command, POST /save with no name " +
                      "and the game's own autosave all resolve through CurrentStationName. POST " +
                      "/save {\"name\":\"<X>\"} assigns one."
                    : "hosting, but the first named save that would have assigned the station name " +
                      "did not confirm (" + (stationNameError ?? "no detail") + "). Until one does, " +
                      "nothing can save this world and the launcher's ordered teardown will refuse " +
                      "to quit on top of it. Retry with POST /save {\"name\":\"" + stationName + "\"}.");
            }

            AttachStatus(ok);
            return HttpResponse.Json(ok.ToString());
        }

        /// <summary>
        ///     Gives a freshly created world its station name, by performing the first named save.
        /// </summary>
        /// <remarks>
        ///     There is no setter for <c>XmlSaveLoad.Instance.CurrentStationName</c> worth reaching
        ///     for: the game assigns it as a side effect of a named save, and going around that
        ///     would name the world without writing it, which is a worse state than either end.
        ///     So this is exactly what <c>POST /save</c> does, minus the reporting: submit
        ///     <c>save "&lt;name&gt;"</c> and wait for the game's own console confirmation.
        ///
        ///     Failure is never fatal to the caller. Hosting is what <c>/host</c> asserts and it
        ///     has already succeeded by the time this runs; an unnamed world is a warning on a 200.
        /// </remarks>
        private static bool AssignStationName(string name, int timeoutMs, out string detail)
        {
            detail = null;

            foreach (char c in name)
            {
                if (c == '"' || c < ' ' || c == '\u007f')
                {
                    detail = "the name contains a quote or a control character, which breaks the " +
                             "console command it is submitted through";
                    return false;
                }
            }

            string sanitized = SanitizeSaveName(name);
            long beforeSeq = ConsoleTap.NextSeq;

            var submit = Main(() =>
            {
                ConsoleWindow.Submit("save \"" + name + "\"");
                return OkJson();
            });
            if (submit.Status != 200)
            {
                detail = "the save command could not be submitted (status " +
                         submit.Status.ToString(CultureInfo.InvariantCulture) + ")";
                return false;
            }

            string failureLine = null;
            string outcome = PollUntil(timeoutMs, () =>
            {
                foreach (var line in ConsoleTap.Snapshot(beforeSeq, 500, null, "console"))
                {
                    string text = line.Text ?? "";
                    if (IsSaveFailureLine(text)) { failureLine = text; return "failed"; }
                    if (IsSaveConfirmedLine(text, name, sanitized)) return "console";
                }
                return null;
            });

            if (outcome == "console")
            {
                // Read back rather than assumed: the console line proves a save happened, and this
                // proves the thing the save was FOR, which is the name every later save resolves.
                string assigned = SafeMain(CurrentStationName, null);
                if (!string.IsNullOrEmpty(assigned)) return true;

                detail = "the save confirmed but CurrentStationName is still empty, so a later save " +
                         "with no name will still have nothing to save under";
                return false;
            }

            detail = outcome == "failed"
                ? "the game reported the save failed: " + failureLine
                : "the save did not confirm within " +
                  (timeoutMs / 1000).ToString(CultureInfo.InvariantCulture) + "s";
            return false;
        }

        // ---- save -----------------------------------------------------------

        /// <summary>
        ///     Persists the world this instance is holding, and waits for evidence that it landed.
        ///
        ///     The contract mirrors the launcher's <c>save</c> verb on the server half: request the save, wait for
        ///     confirmation, and on a timeout WARN rather than claim success. A fire-and-forget call
        ///     that answered 200 would be worse than having no endpoint, because a test would then
        ///     tear the rig down believing a world it never wrote is on disk.
        ///
        ///     The evidence is the console, corroborated by the file:
        ///
        ///     <list type="bullet">
        ///       <item><c>Starting Save for &lt;name&gt;</c>, printed by <c>SaveHelper.SaveGame</c>
        ///             once the request is accepted. Not success, but it separates "the save never
        ///             started" from "the save started and is still running", which is the
        ///             difference between a broken call and a big world.</item>
        ///       <item><c>Saved &lt;name&gt;</c> (existing save) or <c>Created new save</c> (first
        ///             save under that name), printed by <c>SaveCommand</c> only after the
        ///             <c>SaveResult</c> comes back successful. THIS is the confirmation.</item>
        ///       <item>A failure line. Every failure path returns a <c>SaveResult</c> whose message
        ///             is printed through <c>ConsoleWindow.PrintError</c>, so a failed save answers
        ///             immediately instead of burning the whole timeout.</item>
        ///       <item>The head <c>.save</c> file's size and write stamp, read afterwards and
        ///             reported. Used as the primary signal ONLY when the console tap is not
        ///             patched, because the file's write time moves while the zip is still being
        ///             written and would otherwise confirm mid-write.</item>
        ///     </list>
        ///
        ///     Status codes follow the same rule as <c>/input/*</c>: a caller that does nothing
        ///     special cannot receive a success for something that did not happen. Confirmed is 200;
        ///     unconfirmed is 409 carrying <c>requested:true</c> and a <c>warning</c>, so a launcher
        ///     can tell "asked for, not confirmed" (warn) from "refused outright" (fail).
        /// </summary>
        private static HttpResponse SaveWorld(IDictionary body)
        {
            string requestedName = Json.GetStr(body, "name");
            bool wait = Json.GetBool(body, "wait", true);
            int timeoutMs = Json.GetInt(body, "timeoutMs", 180000);

            if (requestedName != null)
            {
                foreach (char c in requestedName)
                {
                    if (c == '\"' || c < ' ' || c == '\u007f')
                        return HttpResponse.Error(
                            "the save name contains a quote or a control character. It is submitted " +
                            "to the game's console as save \"<name>\", which such a character breaks. " +
                            "Send it as a query parameter (POST /save?name=My%20Station) or pick a " +
                            "plainer name. Nothing was changed.", 400);
                }
            }

            string nameToSend = null;
            string sanitized = null;
            string saveRoot = null;

            var pre = Main(() =>
            {
                string state = GameManager.GameState.ToString();
                if (state != "Running" && state != "Paused")
                    return Fail("cannot save from gameState=" + state + "; the game's own save " +
                                "command refuses anything but Running or Paused and prints nothing " +
                                "this endpoint could wait on.");

                string role = StateReporter.Role();
                if (role == "joinedClient")
                    return Fail("cannot save from a joined client: the save command is scoped " +
                                "HostOrSinglePlayer, so it refuses here. Save on the host instead, " +
                                "which is the instance whose /status.role is listenHost.");

                nameToSend = string.IsNullOrEmpty(requestedName) ? CurrentStationName() : requestedName;
                if (string.IsNullOrEmpty(nameToSend))
                    return HttpResponse.Error(
                        "no 'name' was given and the current station name could not be read, so " +
                        "there is nothing to save under. Pass a name.", 400);

                sanitized = SanitizeSaveName(nameToSend);
                saveRoot = EffectiveSaveRoot();
                return OkJson();
            });
            if (pre.Status != 200) return pre;

            string savesDir = string.IsNullOrEmpty(saveRoot) ? null : Path.Combine(saveRoot, "saves");
            long beforeSeq = ConsoleTap.NextSeq;
            DateTime submittedUtc = DateTime.UtcNow;

            var submit = Main(() =>
            {
                ConsoleWindow.Submit("save \"" + nameToSend + "\"");
                return OkJson();
            });
            if (submit.Status != 200) return submit;

            if (!wait)
                return HttpResponse.Json(new Json.Obj()
                    .Bit("ok", true)
                    .Bit("requested", true)
                    .Bit("confirmed", false)
                    .Str("name", nameToSend)
                    .Str("resolvedName", sanitized)
                    .Str("note", "wait=false, so nothing was confirmed. The save runs asynchronously " +
                                 "and may still fail. Poll GET /console/log?contains=Saved to confirm.")
                    .ToString());

            string startedLine = null;
            string confirmLine = null;
            string failureLine = null;

            string outcome = PollUntil(timeoutMs, () =>
            {
                foreach (var line in ConsoleTap.Snapshot(beforeSeq, 500, null, "console"))
                {
                    string text = line.Text ?? "";
                    if (IsSaveFailureLine(text)) { failureLine = text; return "failed"; }
                    if (startedLine == null && IsSaveStartedLine(text)) startedLine = text;
                    if (IsSaveConfirmedLine(text, nameToSend, sanitized)) { confirmLine = text; return "console"; }
                }

                // Only when the console tee is not in place. The file's write stamp moves while the
                // zip is still streaming, so on its own it can confirm a save that is half written.
                if (!ConsoleTap.ConsolePatchApplied)
                {
                    var stamp = HeadSaveWriteTimeUtc(savesDir, nameToSend, sanitized);
                    if (stamp.HasValue && stamp.Value > submittedUtc) return "file";
                }
                return null;
            });

            string resolvedFile = HeadSaveFile(savesDir, nameToSend, sanitized);
            long sizeBytes = 0;
            string writtenUtc = null;
            try
            {
                if (resolvedFile != null)
                {
                    var info = new FileInfo(resolvedFile);
                    sizeBytes = info.Length;
                    writtenUtc = info.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture);
                }
            }
            catch { }

            var o2 = new Json.Obj()
                .Bit("ok", outcome == "console" || outcome == "file")
                .Bit("requested", true)
                .Bit("confirmed", outcome == "console" || outcome == "file")
                .Str("result", outcome ?? "timeout")
                .Str("name", nameToSend)
                .Str("resolvedName", sanitized)
                .Str("saveRoot", saveRoot)
                .Str("savePath", resolvedFile)
                .Int("sizeBytes", sizeBytes)
                .Str("lastWriteUtc", writtenUtc)
                .Str("confirmedBy", outcome == "console" ? "console" : (outcome == "file" ? "file" : null))
                .Str("startedLine", startedLine)
                .Str("confirmLine", confirmLine)
                .Str("errorLine", failureLine);

            if (outcome == "failed")
            {
                o2.Str("error", "the game reported the save failed: " + failureLine);
                o2.Raw("consoleTail", ConsoleTailArray(beforeSeq, 25));
                return HttpResponse.Json(o2.ToString(), 409);
            }
            if (outcome == null)
            {
                o2.Str("warning",
                    startedLine == null
                        ? "the save was requested and the game never printed that it started, so it " +
                          "may not have run at all. Treat the world as UNSAVED."
                        : "the save started and did not report completion within the timeout. A large " +
                          "world can outlast it, so it may still be running: re-check " +
                          "/console/log?contains=Saved before assuming failure. Until it confirms, " +
                          "treat the world as UNSAVED.");
                o2.Raw("consoleTail", ConsoleTailArray(beforeSeq, 25));
                return HttpResponse.Json(o2.ToString(), 409);
            }
            if (outcome == "file")
                o2.Str("warning", "confirmed from the file's write stamp rather than the console, " +
                                  "because the console tap is not patched in this process. That is " +
                                  "weaker evidence: the stamp moves while the zip is still being " +
                                  "written. Verify the size looks right.");

            return HttpResponse.Json(o2.ToString());
        }

        // ---- shared helpers --------------------------------------------------

        /// <summary>
        ///     Attaches the full <c>/status</c> block, the same way <c>/connect</c> does, so one
        ///     request answers both "did it work" and "what is this instance now".
        /// </summary>
        private static void AttachStatus(Json.Obj o)
        {
            try { o.Raw("status", MainThreadPump.RunValue(() => StateReporter.Status(), 5000)); }
            catch { }
        }

        /// <summary>
        ///     Reads one value on the main thread, falling back rather than throwing. Used only when
        ///     building a response that has already decided its own outcome: a wedged pump must not
        ///     turn a precise 409 into an opaque 500 that says nothing about what went wrong.
        /// </summary>
        private static T SafeMain<T>(Func<T> work, T fallback)
        {
            try { return MainThreadPump.RunValue(work, 5000); }
            catch { return fallback; }
        }

        /// <summary>
        ///     The last few console lines this endpoint's own command produced. Console only, not
        ///     the BepInEx side: what matters after a silent failure is what the GAME said, and the
        ///     mod-load chatter would bury it.
        /// </summary>
        private static string ConsoleTailArray(long sinceSeq, int limit)
        {
            var parts = new List<string>();
            try
            {
                foreach (var line in ConsoleTap.Snapshot(sinceSeq, limit, null, "console"))
                    parts.Add(Json.Escape(line.Text));
            }
            catch { }
            return "[" + string.Join(",", parts.ToArray()) + "]";
        }

        private static bool IsSaveStartedLine(string text)
        {
            // SaveHelper.SaveGame: ConsoleWindow.Print($"Starting {saveMethod} for {stationName}")
            return text.StartsWith("Starting Save for ", StringComparison.Ordinal)
                || text.StartsWith("Starting NewSave for ", StringComparison.Ordinal);
        }

        private static bool IsSaveConfirmedLine(string text, string requested, string sanitized)
        {
            // SaveCommand.NewSaveTask, for a name with no directory yet.
            if (string.Equals(text, "Created new save", StringComparison.Ordinal)) return true;
            // SaveCommand.SaveTask: ConsoleWindow.Print("Saved " + stationName)
            if (!text.StartsWith("Saved ", StringComparison.Ordinal)) return false;
            string named = text.Substring("Saved ".Length).Trim();
            return string.Equals(named, requested, StringComparison.OrdinalIgnoreCase)
                || string.Equals(named, sanitized, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSaveFailureLine(string text)
        {
            return text.IndexOf("Save Failed", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("Failed to write save file", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("Cannot save game in GameState", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        ///     <c>SaveHelper.SanitizeSaveName</c>'s rule, restated rather than called so a rename in
        ///     the game degrades to a slightly wrong reported name rather than to a plugin that will
        ///     not load. The game applies it only on the NEW-save path, so both spellings are worth
        ///     probing when resolving the file that was written.
        /// </summary>
        private static string SanitizeSaveName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var chars = name.ToCharArray();
            const string forbidden = "?:*<>|\\/\"";
            for (int i = 0; i < chars.Length; i++)
                if (forbidden.IndexOf(chars[i]) >= 0) chars[i] = '_';
            return new string(chars);
        }

        /// <summary>
        ///     <c>XmlSaveLoad.Instance.CurrentStationName</c>, the name the game's own bare
        ///     <c>save</c> command would use. Read reflectively so a rename degrades to "pass a
        ///     name" rather than to a plugin that will not load. Main thread only.
        /// </summary>
        private static string CurrentStationName()
        {
            try
            {
                var type = AccessTools.TypeByName("Assets.Scripts.Serialization.XmlSaveLoad")
                           ?? AccessTools.TypeByName("XmlSaveLoad");
                if (type == null) return null;

                object instance = StaticMemberValue(type, "Instance");
                if (instance == null) return null;

                var prop = AccessTools.Property(type, "CurrentStationName");
                if (prop != null) return prop.GetValue(instance, null) as string;
                var field = AccessTools.Field(type, "CurrentStationName");
                return field == null ? null : field.GetValue(instance) as string;
            }
            catch { return null; }
        }

        /// <summary>
        ///     Reads a static member by name, walking base types. <c>AccessTools.Property</c> and
        ///     <c>AccessTools.Field</c> do not flatten the hierarchy for statics, and singletons in
        ///     this game hang their <c>Instance</c> off a base class.
        /// </summary>
        private static object StaticMemberValue(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                try
                {
                    var prop = AccessTools.DeclaredProperty(t, name);
                    if (prop != null && prop.GetGetMethod(true) != null && prop.GetGetMethod(true).IsStatic)
                        return prop.GetValue(null, null);
                    var field = AccessTools.DeclaredField(t, name);
                    if (field != null && field.IsStatic) return field.GetValue(null);
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        ///     The head <c>.save</c> file inside <c>&lt;saveRoot&gt;/saves/&lt;name&gt;/</c>, which
        ///     is the file <c>SaveHelper.DoSave</c> overwrites. Both the requested and the sanitized
        ///     spelling are probed, because the game only sanitizes when it creates the directory.
        ///     Returns null when nothing is there yet, which is the normal state before a first save.
        /// </summary>
        private static string HeadSaveFile(string savesDir, string requested, string sanitized)
        {
            if (string.IsNullOrEmpty(savesDir)) return null;
            foreach (string candidate in new[] { requested, sanitized })
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                try
                {
                    string dir = Path.Combine(savesDir, candidate);
                    if (!Directory.Exists(dir)) continue;
                    var files = Directory.GetFiles(dir, "*.save");
                    if (files != null && files.Length > 0) return files[0];
                }
                catch { }
            }
            return null;
        }

        private static DateTime? HeadSaveWriteTimeUtc(string savesDir, string requested, string sanitized)
        {
            string file = HeadSaveFile(savesDir, requested, sanitized);
            if (file == null) return null;
            try { return File.GetLastWriteTimeUtc(file); }
            catch { return null; }
        }
    }
}
