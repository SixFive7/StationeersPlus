using System.Collections;
using System.Collections.Generic;

namespace TestRig
{
    /// <summary>
    ///     Refuses an endpoint the current host cannot serve, before the route runs.
    ///
    ///     <para>
    ///     The alternative, which is what merging the two plugins naively would give, is a
    ///     null-reference or an empty object: <c>/player</c> on the dedicated server would answer
    ///     <c>{present:false}</c>, which is indistinguishable from a client standing at the main
    ///     menu, and a harness would keep polling for a character that cannot ever exist. A bare
    ///     404 is no better, because the path DOES exist, just not here.
    ///     </para>
    ///
    ///     <para>
    ///     So every refusal carries the launcher's three-part teaching shape: what the verb
    ///     needs, why this host cannot provide it, and a command that does work. The rig's own
    ///     refusal suite fails a refusal that lacks any of the three, and the same standard
    ///     applies here.
    ///     </para>
    ///
    ///     <para>
    ///     Scope is deliberately narrow. Only endpoints that CANNOT work headless are listed.
    ///     Several that look client-shaped are portable and are left alone: <c>/waitfor</c> polls
    ///     GameState, <c>/colors</c> reads inspector data on the GameManager prefab which is
    ///     present on both, and <c>/load</c> and <c>/newworld</c> drive console commands the
    ///     server has too. Refusing those would narrow the tool for no reason.
    ///     </para>
    /// </summary>
    internal static class HostGuard
    {
        private sealed class Rule
        {
            internal string Needs;
            internal string Because;
            internal string Instead;
        }

        /// <summary>
        ///     Endpoints refused unconditionally on the dedicated server. Keyed by the router's
        ///     already-normalised path.
        /// </summary>
        private static readonly Dictionary<string, Rule> DedicatedRefusals =
            new Dictionary<string, Rule>
            {
                // ---- needs a player character -----------------------------------
                { Contracts.Endpoints.Player, PlayerRule("read the local player's position, hands and cursor target") },
                { Contracts.Endpoints.PlayerTeleport, PlayerRule("move the local player") },
                { Contracts.Endpoints.PlayerLook, PlayerRule("aim the local player's camera") },
                { Contracts.Endpoints.PlayerUse, PlayerRule("run the local player's use action on a target") },
                { Contracts.Endpoints.PlayerSwapHands, PlayerRule("swap the local player's hands") },
                {
                    Contracts.Endpoints.SpawnHand, new Rule
                    {
                        Needs = "a local player character with an active hand slot",
                        Because = "the dedicated server has no player character at all: Human.LocalHuman is null " +
                                  "in this process and no amount of waiting changes that",
                        Instead = "POST /inventory/give?prefab=<name>&player=<display name or clientId>, which " +
                                  "creates directly into a connected player's slot using the authority this " +
                                  "process already has",
                    }
                },
                {
                    Contracts.Endpoints.InventoryArm, new Rule
                    {
                        Needs = "a local player character to put the item into",
                        Because = "the dedicated server has no player character; /inventory/arm spawns into the " +
                                  "LOCAL player's hand",
                        Instead = "POST /inventory/give?prefab=<name>&player=<display name or clientId>&slot=either",
                    }
                },
                {
                    Contracts.Endpoints.InventoryMove, new Rule
                    {
                        Needs = "a local player character whose slots the move is expressed against",
                        Because = "the dedicated server has no player character, so 'activeHand' and every other " +
                                  "slot spec resolves against nothing",
                        Instead = "POST /inventory?player=<name> to read a connected player's slots, then " +
                                  "POST /inventory/give to place an item directly",
                    }
                },

                // ---- needs the client input stack --------------------------------
                { Contracts.Endpoints.InputKey, InputRule("deliver a synthetic key to the game") },
                { Contracts.Endpoints.InputScroll, InputRule("deliver synthetic scroll notches to the game") },
                { Contracts.Endpoints.InputMouse, InputRule("deliver a synthetic mouse button to the game") },
                { Contracts.Endpoints.InputMousePosition, InputRule("place the synthetic mouse cursor") },
                { Contracts.Endpoints.InputReleaseAll, InputRule("release held synthetic keys") },
                { Contracts.Endpoints.InputClear, InputRule("clear synthetic input overrides") },
                { Contracts.Endpoints.InputEnable, InputRule("toggle synthetic input injection") },
                { Contracts.Endpoints.DiagInput, InputRule("report the per-frame input chain") },

                // ---- needs client-side UI ----------------------------------------
                {
                    Contracts.Endpoints.CursorForce, new Rule
                    {
                        Needs = "CursorManager, the client-side cursor target resolver",
                        Because = "CursorManager.Instance is null on the dedicated server: it is client-local UI " +
                                  "and is never instantiated headless",
                        Instead = "POST /player/use with an explicit targetId from a client instance, or " +
                                  "POST /inventory/give here to place the item without a cursor at all",
                    }
                },
                { Contracts.Endpoints.Modal, ModalRule() },
                { Contracts.Endpoints.ModalClick, ModalRule() },
                { Contracts.Endpoints.ModSettings, ModSettingsRule() },
                { Contracts.Endpoints.ModSettingsList, ModSettingsRule() },
                {
                    Contracts.Endpoints.Screenshot, new Rule
                    {
                        Needs = "a rendered backbuffer",
                        Because = "the dedicated server runs -batchmode -nographics and never renders a frame, so " +
                                  "ScreenCapture.CaptureScreenshotAsTexture has nothing to read and the request " +
                                  "would sit out its whole timeout",
                        Instead = "take the screenshot on a client instance: POST /screenshot against that " +
                                  "instance's port",
                    }
                },

                // ---- needs a client network role ----------------------------------
                {
                    Contracts.Endpoints.Connect, new Rule
                    {
                        Needs = "a NetworkClient component in the scene",
                        Because = "the dedicated server is the thing clients connect TO. It has no NetworkClient " +
                                  "and cannot join anything",
                        Instead = "POST /connect against a client instance, pointing it at this server's game port",
                    }
                },
                {
                    Contracts.Endpoints.Disconnect, new Rule
                    {
                        Needs = "a client-side connection to tear down",
                        Because = "the dedicated server holds connections rather than making one, so there is " +
                                  "nothing here to disconnect",
                        Instead = "POST /disconnect against the joined client instance, or stop the server with " +
                                  "testrig stop -Target server",
                    }
                },
                {
                    Contracts.Endpoints.DiagJoin, new Rule
                    {
                        Needs = "a client-side join attempt to trace",
                        Because = "the join trace patches NetworkClient.JoinClientFromMenu and NetworkManager." +
                                  "StartClient, neither of which a server ever calls; the patches are not even " +
                                  "installed on this host",
                        Instead = "GET /diag/join against the client instance that is trying to join",
                    }
                },
                {
                    Contracts.Endpoints.Host, new Rule
                    {
                        Needs = "a process at the main menu that can promote itself to a listen host",
                        Because = "the dedicated server IS the host. It is started with a world by the launcher " +
                                  "and has no menu to host from",
                        Instead = "testrig start -Target server -New <Map>, or POST /host against a client " +
                                  "instance created with -Role host if the test needs a host who also plays",
                    }
                },
                {
                    Contracts.Endpoints.Identity, new Rule
                    {
                        Needs = "a PlayerCookie to read or rewrite",
                        Because = "NetworkManager.Cookie is null in batch mode: the dedicated server loads no " +
                                  "cookie because it presents no player identity",
                        Instead = "GET /identity against a client instance; identity only matters where a " +
                                  "ClientId is presented to a server",
                    }
                },
            };

        private static Rule PlayerRule(string what)
        {
            return new Rule
            {
                Needs = "a local player character, to " + what,
                Because = "the dedicated server has no player character: Human.LocalHuman is null in this " +
                          "process by design, not by timing",
                Instead = "run the same verb against a client instance, or use POST /inventory?player=<name> " +
                          "and POST /inventory/give here, which address a CONNECTED player by name",
            };
        }

        private static Rule InputRule(string what)
        {
            return new Rule
            {
                Needs = "the client input stack, to " + what,
                Because = "nothing polls UnityEngine.Input on the dedicated server, so the input patches are " +
                          "not applied here at all and a delivered key would be consumed by nobody",
                Instead = "drive input against a client instance, or use the endpoint that performs the action " +
                          "directly (/player/use, /inventory/move, /console/exec) instead of synthesising the " +
                          "keystroke that would have triggered it",
            };
        }

        private static Rule ModalRule()
        {
            return new Rule
            {
                Needs = "the client's confirmation panel",
                Because = "modal dialogs are client-side UI; the dedicated server draws none and its " +
                          "ImGui overlay never renders",
                Instead = "GET /modal against the client instance that is showing the dialog",
            };
        }

        private static Rule ModSettingsRule()
        {
            return new Rule
            {
                Needs = "the StationeersLaunchPad settings panel, which draws from OrbitalSimulation.Draw",
                Because = "ImGuiManager.RenderOverlay never calls Draw on the dedicated server, so the panel " +
                          "cannot be shown and listing it would describe something invisible",
                Instead = "GET /config?guid=<mod guid> and POST /config/set, which read and write the same " +
                          "BepInEx entries the panel edits and work on both hosts",
            };
        }

        /// <summary>
        ///     Returns a refusal, or null to let the route run.
        /// </summary>
        internal static HttpResponse Check(string path, IDictionary body)
        {
            if (!HostProfile.IsDedicatedServer) return null;

            Rule rule;
            if (DedicatedRefusals.TryGetValue(path, out rule)) return Refuse(path, rule);

            // Conditional cases. These endpoints work headless with an explicit target and only
            // fall over when they would default to the local player, so refusing the whole path
            // would remove something that works.
            if (path == Contracts.Endpoints.Inventory && !HasAnyPlayerSelector(body))
                return Refuse(path, new Rule
                {
                    Needs = "a player to list, and with no selector it defaults to the local player",
                    Because = "the dedicated server has no local player character",
                    Instead = "POST /inventory?player=<display name>, or ?clientId=<id>, or ?humanId=<ReferenceId>. " +
                              "GET /status lists connectedClients with both ids",
                });

            // No HasLocalPlayer() probe here: this whole method has already returned for anything
            // that is not the dedicated server, and there the answer is false by construction.
            // Probing would mean a Unity read from the HTTP accept thread for no new information.
            if ((path == Contracts.Endpoints.SpawnWorld || path == Contracts.Endpoints.SpawnStructure) &&
                !Json.Has(body, "position"))
                return Refuse(path, new Rule
                {
                    Needs = "a spawn position, and with none given it is derived from the local player's " +
                            "position and facing",
                    Because = "the dedicated server has no local player character to derive it from",
                    Instead = "pass position=x,y,z explicitly. GET /nearby from a client instance, or " +
                              "GET /thing?refId=<id>, will give you a coordinate to anchor on",
                });

            return null;
        }

        private static bool HasAnyPlayerSelector(IDictionary body)
        {
            return Json.Has(body, "player") || Json.Has(body, "clientId") || Json.Has(body, "humanId");
        }

        private static HttpResponse Refuse(string path, Rule rule)
        {
            // 409, not 404 and not 500. The path exists and the request was understood; this host
            // declined it. That is exactly what 409 means everywhere else in this API.
            return HttpResponse.Json(new Json.Obj()
                .Bit("ok", false)
                .Str("error", path + " needs " + rule.Needs + ", and " + rule.Because + ". Instead: " + rule.Instead + ".")
                .Str("host", HostProfile.Name)
                .Str("endpoint", path)
                .Str("needs", rule.Needs)
                .Str("because", rule.Because)
                .Str("instead", rule.Instead)
                .ToString(), 409);
        }
    }
}
