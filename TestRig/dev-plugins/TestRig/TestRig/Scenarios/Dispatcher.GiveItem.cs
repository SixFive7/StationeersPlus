using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;

namespace TestRig.Scenarios
{
    // Scenario: give-item
    //
    // Puts a named prefab straight into a connected player's hand slot, from the process that
    // owns the simulation.
    //
    // Why this exists: a driven client cannot pick an item up. /spawn/hand refuses without
    // simulation authority (correctly: OnServer.Create into a slot is gated on
    // GameManager.RunSimulation, which is false on a remote client). The console `thing spawn`
    // command round-trips through the server and drops the item on the ground, but the pickup
    // then needs CursorManager.CursorThing, which never resolves in a driven session no matter
    // where the synthetic mouse is put, and the window cannot be focused (nor should it be).
    // Forcing the cursor instead is what produced the permanent GameManager.Update wedge.
    //
    // Going through the simulation owner sidesteps every part of that.
    // OnServer.Create<T>(Thing prefab, Slot slot) is the game's own create-into-a-slot path (it
    // is what Human.CreateEmptyHuman uses to dress a fresh character), and MoveToSlotOrWorld
    // inside it replicates the occupancy to the owning client the same way any other
    // server-side slot change does. The client's cursor is not in the loop at all.
    //
    //   <BepInEx root>/scenariorunner/give/<anything>.txt
    //
    // File format, one request per file, blank lines and '#' comments ignored:
    //
    //   prefab=ItemSprayCanRed     # required, Prefab.AllPrefabs name
    //   player=SixFive7            # optional: display name, or a numeric ClientId, or
    //                              #   human=<Human ReferenceId>. Omit entirely when exactly
    //                              #   one player is connected.
    //   hand=either                # optional: left | right | either (default either)
    //   quantity=5                 # optional, stackables only, best effort
    //   replace=true               # optional: drop whatever is already in the hand
    //   mode=list                  # optional: report connected players, spawn nothing
    //
    // The request file is deleted once processed, whether it succeeded or failed, so a
    // malformed request cannot be reapplied every tick for the rest of the session.
    //
    // ---- WHAT CHANGED IN THE MERGE ----
    //
    // This file used to carry a second, independent implementation of the same operation as
    // /inventory/give: resolve a Human, pick a hand, OnServer.Create<DynamicThing>, apply
    // SharedDLCManager.CheckSharedAccess, set the quantity through Stackable.SetQuantity with a
    // reflective int fallback, drop rather than destroy on replace, and read the slot back
    // rather than trusting the return. Both did all of that, separately, about 500 lines apart.
    //
    // There is one implementation now, the route, and this poller is a front door onto it. The
    // poller existed in the first place because the dedicated server had no HTTP control plane;
    // that is precisely what the merge fixes, so POST /inventory/give is the preferred route and
    // the folder is kept for the request-file workflow and for anything already scripted
    // against it.
    //
    // Threading: the sim-tick pump runs on the UniTask ThreadPool worker. Only the file I/O and
    // the parse happen here; Router.InvokeAsync hands the call to a pool thread and the route
    // marshals onto the Unity main thread itself, which on the dedicated server means
    // UnityMainThreadDispatcher, exactly as this file used to do by hand.

    internal static partial class Dispatcher
    {
        private const string GIVE_TAG = "give-item";

        private static string _giveDir;
        private static bool _giveAnnounced;

        private static void Scenario_GiveItem()
        {
            try
            {
                if (_giveDir == null)
                {
                    _giveDir = Path.Combine(Paths.BepInExRootPath, "scenariorunner", "give");
                    Directory.CreateDirectory(_giveDir);
                }

                if (!_giveAnnounced)
                {
                    _giveAnnounced = true;
                    _log?.LogInfo($"[ScenarioRunner] {GIVE_TAG} ARMED | polling '{_giveDir}' every " +
                                  "simulation tick. Drop a file with prefab= (and optional player= / hand= / " +
                                  "quantity= / replace= / mode=list) to create an item into a connected " +
                                  "player's hand. POST /inventory/give does the same thing without a file " +
                                  "and is the preferred route now that the control plane runs on both halves.");
                }

                string[] files;
                try { files = Directory.GetFiles(_giveDir, "*.*"); }
                catch (Exception e)
                {
                    _log?.LogError($"[ScenarioRunner] {GIVE_TAG} | cannot list '{_giveDir}': {e.Message}");
                    return;
                }

                if (files.Length == 0) return;
                Array.Sort(files, StringComparer.Ordinal);

                foreach (var file in files)
                {
                    string name = Path.GetFileName(file);
                    bool consume;
                    try
                    {
                        consume = Give_ProcessRequest(file, name);
                    }
                    catch (Exception e)
                    {
                        _log?.LogError($"[ScenarioRunner] {GIVE_TAG} | request '{name}' threw: {e}");
                        consume = true;
                    }

                    if (!consume) continue;

                    try { File.Delete(file); }
                    catch (Exception e)
                    {
                        _log?.LogError($"[ScenarioRunner] {GIVE_TAG} | could not delete '{name}': " +
                                       $"{e.Message} (it WILL be reprocessed next tick)");
                    }
                }
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] {GIVE_TAG} threw: {e}");
            }
        }

        /// <summary>
        /// Returns true when the request file should be deleted, false to retry next tick.
        /// </summary>
        private static bool Give_ProcessRequest(string path, string name)
        {
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (IOException)
            {
                _log?.LogInfo($"[ScenarioRunner] {GIVE_TAG} | '{name}' not readable yet, retrying next tick");
                return false;
            }

            var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                kv[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }

            string prefab, player, human, hand, quantity, replace, mode;
            kv.TryGetValue("prefab", out prefab);
            kv.TryGetValue("player", out player);
            kv.TryGetValue("human", out human);
            kv.TryGetValue("hand", out hand);
            kv.TryGetValue("quantity", out quantity);
            kv.TryGetValue("replace", out replace);
            kv.TryGetValue("mode", out mode);

            if (string.Equals(mode, "list", StringComparison.OrdinalIgnoreCase))
            {
                // NARROWED, deliberately. The old list mode walked every Human and printed its
                // hand slots from inside the plugin. /status already reports connectedClients with
                // both ids, and /inventory?player=<name> reports that player's slots including the
                // hands, so listing is two calls that a caller can also make directly. Reproducing
                // it here would be a third implementation of a read the control plane already has.
                Router.InvokeAsync(Contracts.Endpoints.Status, null, (status, bodyText) =>
                    _log?.LogInfo($"[ScenarioRunner] {GIVE_TAG} | '{name}' LIST | http={status} {bodyText} " +
                                  "| for a player's hands use POST /inventory?player=<display name>"));
                return true;
            }

            if (string.IsNullOrEmpty(prefab))
            {
                _log?.LogError($"[ScenarioRunner] {GIVE_TAG} | '{name}' FAIL | need a prefab= line");
                return true;
            }

            string wantHand = string.IsNullOrEmpty(hand) ? "either" : hand.Trim().ToLowerInvariant();
            if (wantHand != "left" && wantHand != "right" && wantHand != "either")
            {
                _log?.LogError($"[ScenarioRunner] {GIVE_TAG} | '{name}' FAIL | " +
                               $"hand '{hand}' must be left, right or either");
                return true;
            }

            var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "prefab", prefab },
                // The route's slot spec accepts left / right / either directly, so hand maps
                // straight onto it without a translation table.
                { "slot", wantHand },
            };
            if (!string.IsNullOrEmpty(player)) parameters["player"] = player;
            if (!string.IsNullOrEmpty(human)) parameters["humanId"] = human;
            if (!string.IsNullOrEmpty(quantity)) parameters["quantity"] = quantity;
            if (!string.IsNullOrEmpty(replace)) parameters["replace"] = replace;

            Router.InvokeAsync(Contracts.Endpoints.InventoryGive, parameters, (status, bodyText) =>
            {
                bool ok = status == Contracts.RigStatus.Ok &&
                          bodyText != null &&
                          bodyText.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0;
                if (ok)
                    _log?.LogInfo($"[ScenarioRunner] {GIVE_TAG} | '{name}' GIVE OK | {bodyText}");
                else
                    _log?.LogError($"[ScenarioRunner] {GIVE_TAG} | '{name}' GIVE FAILED | http={status} {bodyText}");
            });

            return true;
        }
    }
}
