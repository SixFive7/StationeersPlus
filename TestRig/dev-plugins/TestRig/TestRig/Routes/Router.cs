using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Endpoints = TestRig.Contracts.Endpoints;

namespace TestRig
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
    ///
    ///     <para>
    ///     Every path below is a constant from <c>TestRig.Contracts.Endpoints</c>, not a string
    ///     literal. Those are <c>const</c>, so the compiler inlines them and this assembly carries
    ///     no runtime dependency on the contracts assembly, but a rename on the launcher side is
    ///     still a compile error here. That is the whole reason the shared assembly exists: the
    ///     fakery audit found a fake answering <c>/dlc</c> with a shape the real checks never read,
    ///     and 399 assertions stayed green through it.
    ///     </para>
    /// </summary>
    internal static partial class Router
    {
        private const int DefaultTimeoutMs = 20000;

        internal static HttpResponse Handle(HttpRequest req)
        {
            string path = (req.Path ?? "/").TrimEnd('/');
            if (path.Length == 0) path = "/";
            path = path.ToLowerInvariant();

            IDictionary body = Json.ParseObject(req.Body);
            // Query parameters are a convenience alias for body fields, so every endpoint can also
            // be driven from a plain browser or curl GET. A query parameter is percent-decoded by
            // the HTTP layer and never passes through the JSON string reader, which makes it the
            // reliable way to send a Windows path.
            foreach (var kv in req.QueryParams)
                if (!body.Contains(kv.Key)) body[kv.Key] = kv.Value;

            // One plugin, two hosts. An endpoint the dedicated server cannot serve is refused here
            // rather than allowed to return an empty object that reads like a real answer.
            var refusal = HostGuard.Check(path, body);
            if (refusal != null) return refusal;

            switch (path)
            {
                case Endpoints.Root:
                case Endpoints.Help: return HttpResponse.Json(Help());
                case Endpoints.Ping: return Ping();

                // ---- instance identity -------------------------------------
                case Endpoints.Instance: return Main(() => HttpResponse.Json(InstanceRoute(body)));
                case Endpoints.Identity: return Main(() => IdentityRoute(body));

                // ---- state -------------------------------------------------
                case Endpoints.Status: return Main(() => HttpResponse.Json(StateReporter.Status()));
                case Endpoints.Player: return Main(() => HttpResponse.Json(new Json.Obj()
                    .Bit("ok", true)
                    .Raw("epoch", Epoch.Json())
                    .Raw("player", StateReporter.PlayerJson())
                    .ToString()));
                case Endpoints.Colors: return Main(() => HttpResponse.Json(StateReporter.Colors()));
                case Endpoints.Plugins: return Main(() => HttpResponse.Json(StateReporter.Plugins()));
                case Endpoints.Nearby: return Main(() => HttpResponse.Json(StateReporter.Nearby(
                    Json.GetFloat(body, "radius", 10f),
                    Json.GetStr(body, "filter"),
                    Json.GetInt(body, "limit", 100))));

                // ---- console -----------------------------------------------
                case Endpoints.ConsoleLog: return ConsoleLog(body);
                case Endpoints.ConsoleClear: ConsoleTap.Clear(); return Ok();
                case Endpoints.ConsoleBuffer: return Main(() => HttpResponse.Json(
                    ConsoleTap.ReadGameBuffer(Json.GetInt(body, "limit", 200), Json.GetStr(body, "contains"))));
                case Endpoints.ConsoleExec: return ConsoleExec(body);
                case Endpoints.ConsolePrint: return ConsolePrint(body);
                case Endpoints.ConsoleCommands: return Main(() => HttpResponse.Json(ConsoleCommands(
                    Json.GetStr(body, "contains"))));

                // ---- session -----------------------------------------------
                // /connect, /host, /load, /newworld and /save are NOT wrapped in Main(...): each
                // one waits on the game for far longer than the 20 s main-thread budget, so they
                // hop to the main thread per step and poll in between. Wrapping one of them would
                // answer 504 while the work was still going fine.
                case Endpoints.Connect: return Connect(body);
                case Endpoints.Host: return HostWorld(body);
                case Endpoints.Disconnect: return Disconnect(body);
                case Endpoints.Quit: return Quit(body);
                case Endpoints.Saves: return Main(() => HttpResponse.Json(Saves()));
                case Endpoints.Save: return SaveWorld(body);
                case Endpoints.SavePath: return Main(() => SavePath(body));
                case Endpoints.Load: return LoadSave(body);
                case Endpoints.NewWorld: return NewWorld(body);
                case Endpoints.WaitFor: return WaitFor(body);

                // ---- input -------------------------------------------------
                case Endpoints.InputKey: return InputKey(body);
                case Endpoints.InputScroll: return InputScroll(body);
                case Endpoints.InputMouse: return InputMouse(body);
                case Endpoints.InputReleaseAll: return Main(() => { VirtualInput.ReleaseAll(); return OkJson(); });
                case Endpoints.InputClear: return Main(() => { VirtualInput.ClearAll(); return OkJson(); });
                case Endpoints.InputKeyMap: return HttpResponse.Json(new Json.Obj()
                    .Bit("ok", true).StrArray("actions", VirtualInput.ListKeyMapActions()).ToString());
                case Endpoints.InputEnable:
                    VirtualInput.Enabled = Json.GetBool(body, "enabled", true);
                    return HttpResponse.Json(new Json.Obj().Bit("ok", true).Bit("enabled", VirtualInput.Enabled).ToString());
                case Endpoints.InputMousePosition: return InputMousePosition(body);
                case Endpoints.DiagInput: return Main(() => HttpResponse.Json(InputDiagnostics()));
                case Endpoints.DiagJoin: return Main(() => HttpResponse.Json(JoinDiagnostics()));

                // ---- player actions -----------------------------------------
                case Endpoints.PlayerTeleport: return Main(() => Teleport(body));
                case Endpoints.PlayerLook: return Main(() => Look(body));
                case Endpoints.PlayerUse: return Main(() => UseOnTarget(body));
                case Endpoints.PlayerSwapHands: return Main(() => SwapHands());

                // ---- inventory -----------------------------------------------
                // /inventory/move and /inventory/arm are NOT wrapped in Main(...): on a client the
                // move is a message to the server and the slot only fills when the server's next
                // state delta arrives, so both hop to the main thread per step and poll in between.
                case Endpoints.Inventory: return Main(() => InventoryList(body));
                case Endpoints.InventoryMove: return InventoryMove(body);
                case Endpoints.InventoryGive: return InventoryGive(body);
                case Endpoints.InventoryArm: return InventoryArm(body);

                // ---- spawning ------------------------------------------------
                case Endpoints.SpawnHand: return Main(() => SpawnIntoHand(body));
                case Endpoints.SpawnWorld: return Main(() => SpawnIntoWorld(body));
                case Endpoints.SpawnStructure: return Main(() => SpawnStructure(body));
                case Endpoints.Prefabs: return Main(() => HttpResponse.Json(Prefabs(
                    Json.GetStr(body, "contains"), Json.GetInt(body, "limit", 200), Json.GetStr(body, "type"))));

                // ---- StationeersLaunchPad settings panel ---------------------
                case Endpoints.ModSettingsList: return Main(() => HttpResponse.Json(ModSettingsPanel.List()));
                case Endpoints.ModSettings: return Main(() => HttpResponse.Json(ModSettingsPanel.Show(
                    Json.GetStr(body, "mod"), Json.GetBool(body, "show", true))));

                // ---- modal dialogs -------------------------------------------
                case Endpoints.Modal: return Main(() => HttpResponse.Json(Modal.Describe()));
                case Endpoints.ModalClick: return Main(() => HttpResponse.Json(
                    Modal.Click(Json.GetInt(body, "button", 1))));

                // ---- cursor --------------------------------------------------
                case Endpoints.CursorForce: return Main(() => ForceCursor(body));

                // ---- screenshot ----------------------------------------------
                case Endpoints.Screenshot: return TakeScreenshot(body);

                // ---- config --------------------------------------------------
                case Endpoints.Config: return Main(() => HttpResponse.Json(ConfigAccess.Dump(
                    Json.GetStr(body, "guid"), Json.GetStr(body, "filter"))));

                // save defaults TRUE, on both hosts.
                //
                // The two merged plugins disagreed: the client's /config/set defaulted save=true,
                // the server's config-set request-file poller defaulted save=false. One default had
                // to win and true is the safer one. A write that is not persisted disappears on the
                // next reload, which produces a test that passed once and cannot be reproduced, and
                // the failure is silent because the in-memory value was correct while the test ran.
                // It also matches what a human editing the same entry through the StationeersLaunchPad
                // settings panel gets. Both config trees are tier-3 rig state that the session reset
                // restores, so persisting costs nothing that is not already undone at the boundary.
                case Endpoints.ConfigSet: return Main(() => HttpResponse.Json(ConfigAccess.Set(
                    Json.GetStr(body, "guid"),
                    Json.GetStr(body, "section"),
                    Json.GetStr(body, "key"),
                    Json.GetStr(body, "value"),
                    Json.GetBool(body, "save", true))));
                case Endpoints.ConfigReload: return Main(() => HttpResponse.Json(ConfigAccess.Reload(
                    Json.GetStr(body, "guid"))));
                case Endpoints.Reflect: return Main(() => HttpResponse.Json(ConfigAccess.ReadStatic(
                    Json.GetStr(body, "type"), Json.GetStr(body, "member"),
                    Json.GetBool(body, "expand", false),
                    Math.Max(1, Math.Min(500, Json.GetInt(body, "expandLimit", 25))),
                    Json.GetStr(body, "key"))));
                case Endpoints.ReflectMembers: return Main(() => HttpResponse.Json(ConfigAccess.DumpMembers(
                    Json.GetStr(body, "type"))));

                // ---- instance reflection: any member of any Thing --------------
                case Endpoints.Thing: return Main(() => ThingRoute(body));
                case Endpoints.ThingMembers: return Main(() => ThingMembersRoute(body));
                case Endpoints.ReflectInstance: return Main(() => ReflectInstanceRoute(body));

                // ---- per-process DLC entitlement, REMOVAL ONLY ------------------
                // There is deliberately no route here whose name could add. See Routes.Dlc.cs.
                case Endpoints.Dlc: return Main(() => HttpResponse.Json(DlcEntitlement.Describe()));
                case Endpoints.DlcRemove: return Main(() => DlcRemoveRoute(body));
                case Endpoints.DlcRestore: return Main(() => DlcRestoreRoute(body));

                // ---- scenarios ---------------------------------------------------
                // New in the merge, and the reason the ScenarioRunner half stops needing a log
                // grep. NOT wrapped in Main(...): a scenario is measured in simulation ticks, not
                // in frames, and the run route waits on Dispatcher.TicksSeen.
                //
                // These four paths are not yet in TestRig.Contracts.Endpoints, which another agent
                // owns. Until they are, they are the only string literals in this table.
                case ScenariosPath: return ScenariosRoute();
                case ScenarioRunPath: return ScenarioRun(body);
                case ScenarioArmPath: return ScenarioArm(body);
                case ScenarioDisarmPath: return ScenarioDisarm(body);

                default:
                    return HttpResponse.Error("unknown endpoint '" + req.Path + "'. GET /help lists them all.", 404);
            }
        }

        internal const string ScenariosPath = "/scenarios";
        internal const string ScenarioRunPath = "/scenario/run";
        internal const string ScenarioArmPath = "/scenario/arm";
        internal const string ScenarioDisarmPath = "/scenario/disarm";

        // ---- shared helpers -------------------------------------------------

        private static HttpResponse Main(Func<HttpResponse> work, int timeoutMs = DefaultTimeoutMs)
            => MainThreadPump.RunSync(work, timeoutMs);

        /// <summary>
        ///     Calls a route from inside the process, as if a request had arrived.
        ///
        ///     <para>
        ///     This is what lets the two request-file pollers stop carrying their own copy of an
        ///     operation the router already implements. <c>give-item</c> and
        ///     <c>/inventory/give</c> were the same operation written twice, as were
        ///     <c>config-set</c> and <c>/config/set</c>, and the pair disagreed on the default for
        ///     <c>save</c>. One implementation with two front doors is the fix; this is the second
        ///     front door.
        ///     </para>
        ///
        ///     <para>
        ///     Parameters go in as query parameters rather than as a JSON body on purpose: the
        ///     router merges them into the same dictionary, and a query parameter never passes
        ///     through the JSON string reader, so a Windows path or a value containing a backslash
        ///     survives intact. That is the same reason <c>/savepath</c> tells callers to use the
        ///     query string.
        ///     </para>
        ///
        ///     <para>
        ///     ASYNCHRONOUS, and that is load bearing. The pollers run from the
        ///     <c>ElectricityManager.ElectricityTick</c> postfix, and most routes wrap their work
        ///     in <c>Main(...)</c>, which blocks the calling thread until the Unity main thread
        ///     runs it. Blocking the simulation tick worker on the main thread risks a deadlock if
        ///     the main thread is itself awaiting that tick. Handing the call to a pool thread
        ///     keeps the tick returning immediately and preserves the fire-and-forget shape both
        ///     pollers already had.
        ///     </para>
        /// </summary>
        internal static void InvokeAsync(string path, IDictionary<string, string> parameters,
                                         Action<int, string> onResult)
        {
            var req = new HttpRequest { Method = "POST", Path = path };
            if (parameters != null)
                foreach (var kv in parameters)
                    if (kv.Value != null) req.QueryParams[kv.Key] = kv.Value;

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                int status = 500;
                string text = null;
                try
                {
                    var response = Handle(req);
                    if (response != null)
                    {
                        status = response.Status;
                        text = System.Text.Encoding.UTF8.GetString(response.Body ?? new byte[0]);
                    }
                }
                catch (Exception ex)
                {
                    text = "{\"ok\":false,\"error\":" + Json.Escape(ex.ToString()) + "}";
                }

                try { onResult?.Invoke(status, text); }
                catch (Exception ex) { Plugin.Log?.LogError("internal invoke callback threw: " + ex); }
            });
        }

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
                // Named here as well as on /status, because /ping is what a harness calls when
                // everything else is timing out, and "which pump is this host using and is it
                // ready" is the first question at that moment.
                .Str("host", HostProfile.Name)
                .Str("pumpStrategy", MainThreadPump.StrategyName)
                .Bit("pumpReady", MainThreadPump.MarshalAvailable)
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
