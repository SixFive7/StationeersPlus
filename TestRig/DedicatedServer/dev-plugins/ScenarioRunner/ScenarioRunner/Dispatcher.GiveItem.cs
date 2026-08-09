using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Util;
using BepInEx;
using DLC;

namespace ScenarioRunner
{
    // Scenario: give-item
    //
    // Puts a named prefab straight into a connected player's hand slot, from the
    // SERVER, using the authority the dedicated server already has.
    //
    // Why this exists: a client driven through ClientDriver cannot pick an item up.
    // /spawn/hand refuses without simulation authority (correctly: OnServer.Create
    // into a slot is gated on GameManager.RunSimulation, which is false on a remote
    // client). The console `thing spawn` command round-trips through the server and
    // drops the item on the ground, but the pickup then needs CursorManager.
    // CursorThing, which never resolves in a driven session no matter where the
    // synthetic mouse is put, and the window cannot be focused (nor should it be:
    // ClientDriver exists so driving needs no focus). Forcing the cursor instead is
    // what produced the permanent GameManager.Update wedge documented in
    // TEST-RESULTS.md T1a.
    //
    // Going through the server sidesteps every part of that. The server owns the
    // simulation, OnServer.Create<T>(Thing prefab, Slot slot) is the game's own
    // create-into-a-slot path (it is what Human.CreateEmptyHuman uses to dress a
    // fresh character), and MoveToSlotOrWorld inside it replicates the occupancy to
    // the owning client the same way any other server-side slot change does. The
    // client's cursor is not in the loop at all.
    //
    // Deliberately general rather than a spray-can shortcut: paint tests want cans
    // in several colours, a Spray Paint Gun and structures to paint at them, and the
    // next test after that will want something else entirely.
    //
    // Same passive-poller shape as config-set, and for the same reason: it sits
    // armed for a whole session and does nothing until an agent drops a file in,
    // which is what a mid-session test needs. Its request folder is separate from
    // config-set's so the two pollers never eat each other's files, and both can be
    // armed at once via a comma-separated Scenario string (see Dispatcher.Tick).
    //
    //   TestRig/DedicatedServer/install/BepInEx/scenariorunner/give/<anything>.txt
    //
    // File format, one request per file, blank lines and '#' comments ignored:
    //
    //   prefab=ItemSprayCanRed     # required, Prefab.AllPrefabs name
    //   player=SixFive7            # optional: display name, or a numeric Steam id,
    //                              #   or human=<Human ReferenceId>. Omit entirely
    //                              #   when exactly one player is connected.
    //   hand=either                # optional: left | right | either (default either)
    //   quantity=5                 # optional, stackables only, best effort
    //   replace=true               # optional: drop whatever is already in the hand
    //   mode=list                  # optional: report players and hands, spawn nothing
    //
    // The request file is deleted once processed, whether it succeeded or failed, so
    // a malformed request cannot be reapplied every tick for the rest of the session.
    //
    // Threading: the sim-tick pump runs on the UniTask ThreadPool worker, and every
    // call below touches Unity objects, so the whole spawn is marshalled onto the
    // Unity main thread through UnityMainThreadDispatcher exactly as
    // pgp-mixedwire-fixture does for cable spawning. Only the file I/O and the
    // parse happen on the worker.

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
                                  "simulation tick. Drop a file with prefab= (plus optional player= / " +
                                  "human= / hand= / quantity= / replace= / mode=list) to put an item " +
                                  "into a connected player's hand from the server.");
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
                // Half-written file: the writer still has it open. Leave it and pick
                // it up next tick rather than acting on a truncated request.
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

            kv.TryGetValue("prefab", out string prefabName);
            kv.TryGetValue("player", out string player);
            kv.TryGetValue("human", out string humanRaw);
            kv.TryGetValue("hand", out string hand);
            kv.TryGetValue("quantity", out string quantityRaw);
            kv.TryGetValue("replace", out string replaceRaw);
            kv.TryGetValue("mode", out string mode);

            bool list = string.Equals(mode, "list", StringComparison.OrdinalIgnoreCase);
            bool replace = string.Equals(replaceRaw, "true", StringComparison.OrdinalIgnoreCase);

            if (!list && string.IsNullOrEmpty(prefabName))
            {
                _log?.LogError($"[ScenarioRunner] {GIVE_TAG} | '{name}' FAIL | need a prefab= line " +
                               "(or mode=list to just report who is connected)");
                return true;
            }

            int quantity = 0;
            if (!string.IsNullOrEmpty(quantityRaw) && !int.TryParse(quantityRaw, out quantity))
            {
                _log?.LogError($"[ScenarioRunner] {GIVE_TAG} | '{name}' FAIL | " +
                               $"quantity '{quantityRaw}' is not an integer");
                return true;
            }

            long humanId = 0L;
            if (!string.IsNullOrEmpty(humanRaw) && !long.TryParse(humanRaw, out humanId))
            {
                _log?.LogError($"[ScenarioRunner] {GIVE_TAG} | '{name}' FAIL | " +
                               $"human '{humanRaw}' is not a ReferenceId");
                return true;
            }

            string wantHand = string.IsNullOrEmpty(hand) ? "either" : hand.Trim().ToLowerInvariant();
            if (wantHand != "left" && wantHand != "right" && wantHand != "either")
            {
                _log?.LogError($"[ScenarioRunner] {GIVE_TAG} | '{name}' FAIL | " +
                               $"hand '{hand}' must be left, right or either");
                return true;
            }

            if (!UnityMainThreadDispatcher.Exists())
            {
                _log?.LogError($"[ScenarioRunner] {GIVE_TAG} | '{name}' FAIL | " +
                               "UnityMainThreadDispatcher does not exist, cannot reach the main thread");
                return true;
            }

            // Everything from here touches Unity objects and the Thing graph, so it
            // runs on the main thread. Fire and forget: the outcome goes to the log,
            // and holding the request file open across a frame boundary to wait for
            // it would only add a way for the poller to stall.
            UnityMainThreadDispatcher.Instance().Enqueue(() =>
            {
                try
                {
                    Give_OnMainThread(name, prefabName, player, humanId, wantHand, quantity, replace, list);
                }
                catch (Exception e)
                {
                    _log?.LogError($"[ScenarioRunner] {GIVE_TAG} | '{name}' threw on the main thread: {e}");
                }
            });

            return true;
        }

        // Main thread only.
        private static void Give_OnMainThread(string name, string prefabName, string player,
                                              long humanId, string wantHand, int quantity,
                                              bool replace, bool list)
        {
            if (!GameManager.RunSimulation)
            {
                _log?.LogError($"[ScenarioRunner] {GIVE_TAG} | '{name}' FAIL | " +
                               "GameManager.RunSimulation is false; this is not the simulation owner");
                return;
            }

            var humans = Human.AllHumans;
            if (list)
            {
                Give_ReportHumans(name, humans);
                return;
            }

            Human target = Give_ResolveHuman(name, humans, player, humanId);
            if (target == null) return;

            var prefab = Prefab.Find(prefabName) as DynamicThing;
            if (prefab == null)
            {
                _log?.LogError($"[ScenarioRunner] {GIVE_TAG} | '{name}' FAIL | " +
                               $"no DynamicThing prefab named '{prefabName}'. Names are case sensitive " +
                               "and come from Prefab.AllPrefabs (e.g. ItemSprayCanRed, ItemSprayGun). " +
                               "A structure prefab will not match: only a DynamicThing can go in a slot.");
                return;
            }

            // Same gate the vanilla `thing spawn` console command applies. On a
            // dedicated server this is not "does the server own the DLC" but
            // SharedDLCManager's union over connected clients, so refusing here
            // means no connected player is entitled to the item either.
            try
            {
                if (!SharedDLCManager.CheckSharedAccess(prefab.DLCType))
                {
                    _log?.LogError($"[ScenarioRunner] {GIVE_TAG} | '{name}' FAIL | " +
                                   $"'{prefabName}' needs DLC {prefab.DLCType} and " +
                                   $"SharedDLCManager.CheckSharedAccess says no (SharedDLC=" +
                                   $"{SharedDLCManager.SharedDLC}). No connected client owns it.");
                    return;
                }
            }
            catch (Exception e)
            {
                _log?.LogWarning($"[ScenarioRunner] {GIVE_TAG} | '{name}' | DLC check threw " +
                                 $"({e.Message}); continuing.");
            }

            // Thing.Create only queues a new Thing for clients when at least one is
            // connected. With nobody connected the item exists server-side and is
            // never announced, which looks like a silent success.
            if (NetworkBase.Clients.Count == 0)
                _log?.LogWarning($"[ScenarioRunner] {GIVE_TAG} | '{name}' | no clients connected, so " +
                                 "the new Thing will not be announced to anyone this tick.");

            Slot slot = Give_PickHand(name, target, wantHand, replace);
            if (slot == null) return;

            // The game's own create-into-a-slot path. It creates the Thing at the
            // origin, marks it Indestructable for the duration of the move so a stray
            // world-collision destroy cannot eat it in flight, then MoveToSlotOrWorld
            // seats it. Being an OnServer call it replicates to the owning client.
            DynamicThing created;
            try
            {
                created = OnServer.Create<DynamicThing>(prefab, slot);
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] {GIVE_TAG} | '{name}' FAIL | " +
                               $"OnServer.Create('{prefabName}') threw: {e.Message}");
                return;
            }

            if (created == null)
            {
                _log?.LogError($"[ScenarioRunner] {GIVE_TAG} | '{name}' FAIL | " +
                               $"OnServer.Create('{prefabName}') returned null. The prefab exists but " +
                               "is not a DynamicThing, so it cannot go in a slot.");
                return;
            }

            string quantityNote = quantity > 0 ? Give_TrySetQuantity(created, quantity) : "";

            // Read the slot back rather than trusting the return: a create that lands
            // in the world instead of the slot (MoveToSlotOrWorld's fallback) still
            // returns the Thing, and reporting that as success is exactly the kind of
            // false green that wasted the last two runs.
            var seated = slot.Get();
            bool ok = seated != null && seated.ReferenceId == created.ReferenceId;

            if (ok)
                _log?.LogInfo($"[ScenarioRunner] {GIVE_TAG} | '{name}' GIVE OK | " +
                              $"'{prefabName}' (ref {created.ReferenceId}) -> {target.DisplayName} " +
                              $"(human {target.ReferenceId}, client {target.OwnerClientId}) " +
                              $"{Give_HandName(target, slot)} hand{quantityNote}");
            else
                _log?.LogError($"[ScenarioRunner] {GIVE_TAG} | '{name}' GIVE FAILED | " +
                               $"'{prefabName}' (ref {created.ReferenceId}) was created but the " +
                               $"{Give_HandName(target, slot)} hand holds " +
                               $"{(seated == null ? "nothing" : seated.DisplayName + " (ref " + seated.ReferenceId + ")")}. " +
                               "MoveToSlotOrWorld fell back to the world; the item is on the ground.");
        }

        private static void Give_ReportHumans(string name, List<Human> humans)
        {
            if (humans == null || humans.Count == 0)
            {
                _log?.LogInfo($"[ScenarioRunner] {GIVE_TAG} | '{name}' LIST | no Humans in the world");
                return;
            }

            foreach (var h in humans)
            {
                if (h == null) continue;
                _log?.LogInfo($"[ScenarioRunner] {GIVE_TAG} | '{name}' LIST | " +
                              $"'{h.DisplayName}' human={h.ReferenceId} client={h.OwnerClientId} " +
                              $"left={Give_Describe(h.LeftHandSlot)} right={Give_Describe(h.RightHandSlot)}");
            }
        }

        private static string Give_Describe(Slot slot)
        {
            if (slot == null) return "<no slot>";
            var occupant = slot.Get();
            return occupant == null ? "empty" : occupant.DisplayName + "(" + occupant.ReferenceId + ")";
        }

        private static string Give_HandName(Human human, Slot slot)
            => ReferenceEquals(slot, human.LeftHandSlot) ? "left" : "right";

        private static Human Give_ResolveHuman(string name, List<Human> humans, string player, long humanId)
        {
            if (humans == null || humans.Count == 0)
            {
                _log?.LogError($"[ScenarioRunner] {GIVE_TAG} | '{name}' FAIL | no Humans in the world; " +
                               "is a client connected and finished loading?");
                return null;
            }

            if (humanId != 0L)
            {
                var byRef = Thing.Find(humanId) as Human;
                if (byRef == null)
                    _log?.LogError($"[ScenarioRunner] {GIVE_TAG} | '{name}' FAIL | " +
                                   $"no Human with ReferenceId {humanId}. {Give_KnownHumans(humans)}");
                return byRef;
            }

            if (!string.IsNullOrEmpty(player))
            {
                // A bare number is a Steam id. Human.Find has a ulong overload keyed
                // on OwnerClientId and a string overload keyed on DisplayName, and
                // picking the wrong one silently returns null or somebody else.
                if (ulong.TryParse(player, out ulong clientId))
                {
                    var byClient = Human.Find(clientId);
                    if (byClient == null)
                        _log?.LogError($"[ScenarioRunner] {GIVE_TAG} | '{name}' FAIL | " +
                                       $"no Human owned by client {clientId}. {Give_KnownHumans(humans)}");
                    return byClient;
                }

                var byName = Human.Find(player);
                if (byName == null)
                    _log?.LogError($"[ScenarioRunner] {GIVE_TAG} | '{name}' FAIL | " +
                                   $"no Human named '{player}'. {Give_KnownHumans(humans)}");
                return byName;
            }

            // Nobody named. Unambiguous only when there is exactly one Human, which is
            // the normal single-tester case; anything else has to be spelled out,
            // because guessing here hands the can to the wrong character and the test
            // then fails for a reason that looks like a mod bug.
            Human only = null;
            int count = 0;
            foreach (var h in humans)
            {
                if (h == null) continue;
                count++;
                only = h;
            }

            if (count == 1) return only;

            _log?.LogError($"[ScenarioRunner] {GIVE_TAG} | '{name}' FAIL | {count} Humans in the world, " +
                           $"so player= or human= is required. {Give_KnownHumans(humans)}");
            return null;
        }

        private static string Give_KnownHumans(List<Human> humans)
        {
            var parts = new List<string>();
            foreach (var h in humans)
            {
                if (h == null) continue;
                parts.Add($"'{h.DisplayName}' human={h.ReferenceId} client={h.OwnerClientId}");
            }
            return parts.Count == 0 ? "No Humans present." : "Present: " + string.Join(" | ", parts.ToArray());
        }

        private static Slot Give_PickHand(string name, Human human, string wantHand, bool replace)
        {
            Slot left = human.LeftHandSlot;
            Slot right = human.RightHandSlot;

            if (left == null && right == null)
            {
                _log?.LogError($"[ScenarioRunner] {GIVE_TAG} | '{name}' FAIL | " +
                               $"'{human.DisplayName}' has no hand slots");
                return null;
            }

            Slot first = wantHand == "right" ? right : left;
            Slot second = wantHand == "either" ? (ReferenceEquals(first, left) ? right : left) : null;

            if (first != null && first.Get() == null) return first;
            if (second != null && second.Get() == null) return second;

            if (!replace)
            {
                _log?.LogError($"[ScenarioRunner] {GIVE_TAG} | '{name}' FAIL | " +
                               $"'{human.DisplayName}' hands are full " +
                               $"(left={Give_Describe(left)} right={Give_Describe(right)}). " +
                               "Add replace=true to drop what is in the way.");
                return null;
            }

            Slot victim = first ?? second;
            var occupant = victim.Get();
            if (occupant != null)
            {
                try
                {
                    // Drop rather than destroy: the developer's own tablet has been in
                    // that hand before now, and a test tool that silently deletes an
                    // item is a worse tool than one that puts it on the floor.
                    OnServer.MoveToWorld(occupant);
                    _log?.LogInfo($"[ScenarioRunner] {GIVE_TAG} | '{name}' | dropped " +
                                  $"{occupant.DisplayName} (ref {occupant.ReferenceId}) to clear the hand");
                }
                catch (Exception e)
                {
                    _log?.LogError($"[ScenarioRunner] {GIVE_TAG} | '{name}' FAIL | " +
                                   $"could not drop {occupant.DisplayName}: {e.Message}");
                    return null;
                }
            }

            if (victim.Get() != null)
            {
                _log?.LogError($"[ScenarioRunner] {GIVE_TAG} | '{name}' FAIL | " +
                               $"hand still holds {Give_Describe(victim)} after the drop");
                return null;
            }

            return victim;
        }

        /// <summary>
        /// Stackable.SetQuantity is the canonical server-side setter: it clamps to
        /// MaxQuantity and its Quantity setter raises NetworkUpdateFlags |= 1024, so
        /// the count replicates in the same state tick with no message of our own.
        /// It is what the vanilla `thing spawn` console command uses.
        ///
        /// Anything that is not a Stackable carries its fullness on a type-specific
        /// member instead (BatteryCell.PowerStored, Ingot.Quantity, CreditCard.
        /// Currency), so the fallback reflects for a settable int Quantity and says
        /// so plainly when there is not one. A wrong quantity never fails a paint
        /// test, and throwing here would waste an item that is already in the hand.
        /// </summary>
        private static string Give_TrySetQuantity(DynamicThing thing, int quantity)
        {
            try
            {
                if (thing is Stackable stackable)
                {
                    stackable.SetQuantity(quantity);
                    return stackable.Quantity == quantity
                        ? $", quantity={stackable.Quantity}"
                        : $", quantity={stackable.Quantity} (clamped from {quantity}, " +
                          $"MaxQuantity={stackable.MaxQuantity})";
                }

                var prop = thing.GetType().GetProperty("Quantity",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null && prop.CanWrite && prop.PropertyType == typeof(int))
                {
                    prop.SetValue(thing, quantity);
                    return $", quantity={prop.GetValue(thing)}";
                }

                return $", quantity NOT applied ({thing.GetType().Name} is not a Stackable and has " +
                       "no writable int Quantity)";
            }
            catch (Exception e)
            {
                return $", quantity NOT applied ({e.Message})";
            }
        }
    }
}
