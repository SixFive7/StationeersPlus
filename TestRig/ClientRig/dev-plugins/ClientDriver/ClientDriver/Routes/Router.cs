using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ClientDriver
{
    /// <summary>
    ///     Maps HTTP paths onto engine work.
    ///
    ///     This file is the dispatch table and the helpers every route shares; the routes themselves
    ///     live in the <c>Routes.*.cs</c> partials beside it, one file per domain. The split is not
    ///     cosmetic: the single-file version reached 1,500 lines and the dispatch table, which is the
    ///     only part a reader normally wants, was buried a third of the way down it. Keeping the
    ///     table alone in one file means the answer to "what does this thing expose" is one screen.
    ///
    ///     Runs on the HTTP accept thread. Anything touching Unity hops to the main thread through
    ///     <see cref="MainThreadPump"/>, and the HTTP response waits for it, so every endpoint is a
    ///     synchronous request/response.
    /// </summary>
    internal static partial class Router
    {
        private const int DefaultTimeoutMs = 20000;

        internal static HttpResponse Handle(HttpRequest req)
        {
            string path = (req.Path ?? "/").TrimEnd('/');
            if (path.Length == 0) path = "/";

            IDictionary body = Json.ParseObject(req.Body);
            // Query parameters are a convenience alias for body fields, so every endpoint can also
            // be driven from a plain browser or curl GET. A query parameter is percent-decoded by
            // the HTTP layer and never passes through the JSON string reader, which makes it the
            // reliable way to send a Windows path.
            foreach (var kv in req.QueryParams)
                if (!body.Contains(kv.Key)) body[kv.Key] = kv.Value;

            switch (path.ToLowerInvariant())
            {
                case "/":
                case "/help": return HttpResponse.Json(Help());
                case "/ping": return Ping();

                // ---- instance identity -------------------------------------
                case "/instance": return Main(() => HttpResponse.Json(InstanceRoute(body)));
                case "/identity": return Main(() => IdentityRoute(body));

                // ---- state -------------------------------------------------
                case "/status": return Main(() => HttpResponse.Json(StateReporter.Status()));
                case "/player": return Main(() => HttpResponse.Json(new Json.Obj()
                    .Bit("ok", true)
                    .Raw("epoch", Epoch.Json())
                    .Raw("player", StateReporter.PlayerJson())
                    .ToString()));
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
                // /connect, /host, /load, /newworld and /save are NOT wrapped in Main(...): each
                // one waits on the game for far longer than the 20 s main-thread budget, so they
                // hop to the main thread per step and poll in between. Wrapping one of them would
                // answer 504 while the work was still going fine.
                case "/connect": return Connect(body);
                case "/host": return HostWorld(body);
                case "/disconnect": return Disconnect(body);
                case "/quit": return Quit(body);
                case "/saves": return Main(() => HttpResponse.Json(Saves()));
                case "/save": return SaveWorld(body);
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
                case "/diag/input": return Main(() => HttpResponse.Json(InputDiagnostics()));
                case "/diag/join": return Main(() => HttpResponse.Json(JoinDiagnostics()));

                // ---- player actions -----------------------------------------
                case "/player/teleport": return Main(() => Teleport(body));
                case "/player/look": return Main(() => Look(body));
                case "/player/use": return Main(() => UseOnTarget(body));
                case "/player/swaphands": return Main(() => SwapHands());

                // ---- inventory -----------------------------------------------
                // /inventory/move and /inventory/arm are NOT wrapped in Main(...): on a client the
                // move is a message to the server and the slot only fills when the server's next
                // state delta arrives, so both hop to the main thread per step and poll in between.
                case "/inventory": return Main(() => InventoryList(body));
                case "/inventory/move": return InventoryMove(body);
                case "/inventory/give": return InventoryGive(body);
                case "/inventory/arm": return InventoryArm(body);

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
                    Json.GetStr(body, "type"), Json.GetStr(body, "member"),
                    Json.GetBool(body, "expand", false),
                    Math.Max(1, Math.Min(500, Json.GetInt(body, "expandLimit", 25))),
                    Json.GetStr(body, "key"))));
                case "/reflect/members": return Main(() => HttpResponse.Json(ConfigAccess.DumpMembers(
                    Json.GetStr(body, "type"))));

                // ---- instance reflection: any member of any Thing --------------
                case "/thing": return Main(() => ThingRoute(body));
                case "/thing/members": return Main(() => ThingMembersRoute(body));
                case "/reflect/instance": return Main(() => ReflectInstanceRoute(body));

                // ---- per-process DLC entitlement, REMOVAL ONLY ------------------
                // There is deliberately no route here whose name could add. See Routes.Dlc.cs.
                case "/dlc": return Main(() => HttpResponse.Json(DlcEntitlement.Describe()));
                case "/dlc/remove": return Main(() => DlcRemoveRoute(body));
                case "/dlc/restore": return Main(() => DlcRestoreRoute(body));

                default:
                    return HttpResponse.Error("unknown endpoint '" + req.Path + "'. GET /help lists them all.", 404);
            }
        }

        // ---- shared helpers -------------------------------------------------

        private static HttpResponse Main(Func<HttpResponse> work, int timeoutMs = DefaultTimeoutMs)
            => MainThreadPump.RunSync(work, timeoutMs);

        private static HttpResponse Ok() => HttpResponse.Json("{\"ok\":true}");
        private static HttpResponse OkJson() => HttpResponse.Json("{\"ok\":true}");
        private static HttpResponse Fail(string message) => HttpResponse.Error(message, 409);

        /// <summary>
        ///     Liveness. Never touches the main thread, so it answers even when the game is wedged,
        ///     which is exactly when a harness most needs to know the process is still there.
        /// </summary>
        private static HttpResponse Ping()
        {
            return HttpResponse.Json(new Json.Obj()
                .Bit("ok", true)
                .Str("plugin", Plugin.PluginName)
                .Str("version", Plugin.PluginVersion)
                .Str("instance", string.IsNullOrEmpty(InstanceManifest.Name) ? "(unnamed)" : InstanceManifest.Name)
                .Int("port", Plugin.EffectivePort)
                .Int("pumpFrames", MainThreadPump.FramesSeen)
                .Int("frame", MainThreadPump.FrameCount)
                .Bit("pumpAlive", MainThreadPump.Alive)
                // The epoch is a cache read, so it rides even here. epoch.sampledSecondsAgo is wall
                // clock: on a wedged client this endpoint still answers and the stamp says how long
                // ago the game last ran a frame, which is the fact a caller most needs at that
                // moment and the one /status cannot deliver because it would block.
                .Raw("epoch", Epoch.Json())
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

        /// <summary>
        ///     Polls <paramref name="probe"/> every 200 ms until it returns non-null or the timeout
        ///     lapses. Returns the probe's answer, or null on timeout.
        /// </summary>
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

        internal static string PhaseOf(string gameState)
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
    }
}
