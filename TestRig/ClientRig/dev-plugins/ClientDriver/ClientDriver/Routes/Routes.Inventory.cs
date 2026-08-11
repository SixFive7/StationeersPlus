using System;
using System.Collections;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Inventory;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using DLC;
using UnityEngine;
using GameManager = Assets.Scripts.GameManager;

namespace ClientDriver
{
    /// <summary>
    ///     Inventory routes: getting an item into a slot, and specifically into a REMOTE (joined)
    ///     client's hand, which was the one thing the rig could not do.
    ///
    ///     The mechanism, and why these are not just wrappers over <c>/spawn/hand</c>.
    ///
    ///     <c>OnServer.MoveToSlot(DynamicThing, Slot)</c> is two implementations behind one name.
    ///     With <c>GameManager.RunSimulation</c> it moves the Thing directly; without it, it sends a
    ///     <c>MoveToSlotMessage</c> to the server carrying (child netId, parent netId, slot index).
    ///     That message is what a real player's inventory drag produces: <c>Slot.PlayerMoveToSlot</c>,
    ///     the method every slot button in the inventory UI ends in, calls exactly this. So a pure
    ///     client CAN move a Thing into its own hand, server-authoritatively, with no cursor, no
    ///     aiming and no UI, by calling the same method the UI calls.
    ///
    ///     The server applies it through <c>Thing.MoveToSlot</c>, whose only gates are
    ///     <c>CanEnter</c> (slot type, own-hierarchy, CanPickup) and "destination is empty". There
    ///     is no proximity check and no ownership check, which is why an item lying on the ground a
    ///     metre away goes straight into the hand. The result comes back to every client on its own:
    ///     <c>Slot.Take</c> sets <c>NetworkUpdateFlags |= 1</c> server-side, and
    ///     <c>DynamicThing.BuildUpdateTransform</c> writes the parent reference id and slot index
    ///     into that delta, so the occupancy replicates with no extra message.
    ///
    ///     Hence three routes rather than one, in increasing order of authority needed:
    ///
    ///     <list type="bullet">
    ///       <item><c>/inventory/arm</c> is the whole job in one call, on the client that wants the
    ///       item. It spawns through the server, waits for the Thing to arrive, then moves it into a
    ///       hand and waits for the server to agree. Works on a joiner, a listen host and single
    ///       player.</item>
    ///       <item><c>/inventory/move</c> is the second half alone, for a Thing that already exists
    ///       (one the host handed over, or one lying on the ground).</item>
    ///       <item><c>/inventory/give</c> is the host-side route: with simulation authority,
    ///       <c>OnServer.Create&lt;DynamicThing&gt;(prefab, slot)</c> creates the item directly into
    ///       ANOTHER player's slot with no ground hop at all. This is the client-rig equivalent of
    ///       the dedicated server's ScenarioRunner give-item scenario.</item>
    ///     </list>
    ///
    ///     One thing the host cannot do: target a remote player's ACTIVE hand. The active hand is
    ///     <c>InventoryManager.Instance.ActiveHand</c>, a client-local UI object that is never
    ///     replicated, so on the host <c>InventoryManager.ActiveHandSlot</c> is the HOST's own hand.
    ///     <c>/inventory/give</c> therefore names left or right, and the response reports which one
    ///     it used so the caller can either swap hands on the joiner or address that hand directly.
    /// </summary>
    internal static partial class Router
    {
        /// <summary>
        ///     What one main-thread hop hands back to the polling code that follows it. Either
        ///     <see cref="Early"/> is set, meaning the hop already has the whole answer and the
        ///     route should return it, or the identifiers are set and the route goes on to wait.
        ///
        ///     Slots and Things are Unity objects and must not be touched off the main thread, so
        ///     nothing here is a live reference: a slot is carried as (parent reference id, slot
        ///     index) and re-resolved inside each later hop. That also makes the wait correct if the
        ///     game re-creates an object underneath us.
        /// </summary>
        private sealed class InventoryStep
        {
            public HttpResponse Early;
            public long ThingId;
            public string ThingPrefab;
            public long ParentId;
            public int SlotIndex = -1;
            public string SlotLabel;
            public string Route;
            public long HumanId;
            public int PrefabHash;
        }

        private static InventoryStep MainStep(Func<InventoryStep> work, int timeoutMs = DefaultTimeoutMs)
        {
            try { return MainThreadPump.RunValue(work, timeoutMs); }
            catch (TimeoutException) { return new InventoryStep { Early = MainThreadPump.TimeoutResponse(timeoutMs) }; }
            catch (Exception ex) { return new InventoryStep { Early = HttpResponse.Error(ex.ToString()) }; }
        }

        private static InventoryStep Stop(HttpResponse response) => new InventoryStep { Early = response };

        // ---- slot resolution ------------------------------------------------

        /// <summary>
        ///     Resolves a slot spec against a Thing. Accepted forms:
        ///     <c>activeHand</c> / <c>inactiveHand</c> (local player only), <c>leftHand</c>,
        ///     <c>rightHand</c>, <c>either</c> (first empty hand, else left), a slot's
        ///     <c>StringKey</c> ("Uniform", "Back", "Helmet", ...), or a bare slot index.
        ///     <paramref name="error"/> carries the reason on null.
        /// </summary>
        private static Slot ResolveSlot(Thing parent, string spec, bool isLocalPlayer, out string error)
        {
            error = null;
            if (parent == null) { error = "no Thing to resolve a slot against"; return null; }

            string s = (spec ?? "").Trim();
            if (s.Length == 0) s = "activeHand";
            string key = s.ToLowerInvariant().Replace("_", "").Replace("-", "").Replace(" ", "");

            var human = parent as Human;

            switch (key)
            {
                case "activehand":
                case "active":
                case "hand":
                    if (!isLocalPlayer)
                    {
                        error = "'" + s + "' is only meaningful on the client that owns the character. " +
                                "The active hand lives in InventoryManager, which is client-local and never " +
                                "replicated, so from here it would resolve to THIS process's own hand. " +
                                "Name leftHand, rightHand or either instead.";
                        return null;
                    }
                    var active = InventoryManager.ActiveHandSlot;
                    if (active == null) error = "no active hand slot (InventoryManager not initialised)";
                    return active;

                case "inactivehand":
                case "inactive":
                case "offhand":
                    if (!isLocalPlayer)
                    {
                        error = "'" + s + "' is only meaningful on the client that owns the character. " +
                                "Name leftHand, rightHand or either instead.";
                        return null;
                    }
                    var im = InventoryManager.Instance;
                    var inactive = im == null || im.InactiveHand == null ? null : im.InactiveHand.Slot;
                    if (inactive == null) error = "no inactive hand slot (InventoryManager not initialised)";
                    return inactive;

                case "lefthand":
                case "left":
                    if (human == null) { error = "leftHand needs a Human, not " + parent.GetType().Name; return null; }
                    if (human.LeftHandSlot == null) error = "that character has no left hand slot";
                    return human.LeftHandSlot;

                case "righthand":
                case "right":
                    if (human == null) { error = "rightHand needs a Human, not " + parent.GetType().Name; return null; }
                    if (human.RightHandSlot == null) error = "that character has no right hand slot";
                    return human.RightHandSlot;

                case "either":
                case "eitherhand":
                case "freehand":
                    if (human == null) { error = "either needs a Human, not " + parent.GetType().Name; return null; }
                    Slot l = human.LeftHandSlot, r = human.RightHandSlot;
                    if (l != null && SlotOccupant(l) == null) return l;
                    if (r != null && SlotOccupant(r) == null) return r;
                    if (l == null && r == null) { error = "that character has no hand slots"; return null; }
                    return l ?? r;   // both full: caller decides what to do about it
            }

            var slots = parent.Slots;
            if (slots == null || slots.Count == 0) { error = parent.DisplayName + " has no slots"; return null; }

            // Bare index, or "#3".
            string numeric = s.StartsWith("#") ? s.Substring(1) : s;
            int index;
            if (int.TryParse(numeric, out index))
            {
                if (index < 0 || index >= slots.Count)
                {
                    error = "slot index " + index + " is out of range (0.." + (slots.Count - 1) + ")";
                    return null;
                }
                return slots[index];
            }

            foreach (var slot in slots)
            {
                if (slot == null) continue;
                if (string.Equals(slot.StringKey, s, StringComparison.OrdinalIgnoreCase)) return slot;
            }

            error = "no slot named '" + s + "'. GET /inventory lists every slot with its key and index.";
            return null;
        }

        private static DynamicThing SlotOccupant(Slot slot)
        {
            if (slot == null) return null;
            try { return slot.Get(); } catch { return null; }
        }

        private static string SlotName(Slot slot)
        {
            if (slot == null) return null;
            try
            {
                if (!string.IsNullOrEmpty(slot.StringKey)) return slot.StringKey;
            }
            catch { }
            return "#" + SafeSlotIndex(slot);
        }

        private static int SafeSlotIndex(Slot slot)
        {
            try { return slot.SlotIndex; } catch { return -1; }
        }

        /// <summary>
        ///     Finds the character a route should act on. With no selector this is the local player.
        ///     <c>player</c> (display name or numeric ClientId) and <c>humanId</c> (a Human
        ///     ReferenceId) address anyone in the world, which only makes sense where the caller has
        ///     the whole roster, so the caller decides whether to require authority.
        /// </summary>
        private static Human ResolveHuman(IDictionary body, out bool isLocal, out string error)
        {
            error = null;
            isLocal = false;

            long humanId = Json.GetLong(body, "humanId", 0);
            string player = Json.GetStr(body, "player");
            if (string.IsNullOrEmpty(player)) player = Json.GetStr(body, "clientId");

            Human local = null;
            try { local = Human.LocalHuman; } catch { }

            if (humanId == 0 && string.IsNullOrEmpty(player))
            {
                if (local == null) { error = "no local player"; return null; }
                isLocal = true;
                return local;
            }

            Human found = null;
            if (humanId != 0)
            {
                found = Thing.Find(humanId) as Human;
                if (found == null) { error = "no Human with ReferenceId " + humanId + ". " + KnownHumans(); return null; }
            }
            else
            {
                // A bare number is a ClientId. Human.Find has a ulong overload keyed on
                // OwnerClientId and a string overload keyed on DisplayName; picking the wrong one
                // silently returns null or somebody else.
                ulong clientId;
                if (ulong.TryParse(player, out clientId))
                {
                    found = Human.Find(clientId);
                    if (found == null) { error = "no Human owned by client " + clientId + ". " + KnownHumans(); return null; }
                }
                else
                {
                    found = Human.Find(player);
                    if (found == null) { error = "no Human named '" + player + "'. " + KnownHumans(); return null; }
                }
            }

            isLocal = local != null && ReferenceEquals(found, local);
            return found;
        }

        private static string KnownHumans()
        {
            var parts = new List<string>();
            try
            {
                foreach (var h in Human.AllHumans)
                {
                    if (h == null) continue;
                    parts.Add("'" + SafeName(h) + "' human=" + h.ReferenceId + " client=" + h.OwnerClientId);
                }
            }
            catch (Exception ex) { return "Could not enumerate Humans: " + ex.Message; }
            return parts.Count == 0 ? "No Humans present." : "Present: " + string.Join(" | ", parts.ToArray());
        }

        private static string SafeName(Human h)
        {
            try { return h.DisplayName; } catch { return h.name; }
        }

        // ---- GET /inventory --------------------------------------------------

        /// <summary>
        ///     Every slot of a character, with the key and index the other routes accept. Without a
        ///     selector this is the local player; with <c>player</c> or <c>humanId</c> it is anyone
        ///     in the world, which on a joiner means anyone it has been told about.
        /// </summary>
        private static HttpResponse InventoryList(IDictionary body)
        {
            bool isLocal;
            string err;
            var human = ResolveHuman(body, out isLocal, out err);
            if (human == null) return Fail(err);

            var slots = new List<string>();
            Slot activeSlot = null;
            if (isLocal) { try { activeSlot = InventoryManager.ActiveHandSlot; } catch { } }

            try
            {
                var all = human.Slots;
                if (all != null)
                {
                    for (int i = 0; i < all.Count; i++)
                    {
                        var slot = all[i];
                        if (slot == null) continue;
                        var o = new Json.Obj()
                            .Int("index", SafeSlotIndex(slot))
                            .Str("key", SafeStringKey(slot))
                            .Str("type", SafeSlotType(slot))
                            .Bit("isHandSlot", SafeIsHandSlot(slot));
                        if (activeSlot != null) o.Bit("isActiveHand", ReferenceEquals(slot, activeSlot));
                        o.Raw("occupant", StateReporter.DescribeSlot(slot));
                        slots.Add(o.ToString());
                    }
                }
            }
            catch (Exception ex) { return HttpResponse.Error("slot walk failed: " + ex.Message); }

            var res = new Json.Obj()
                .Bit("ok", true)
                .Raw("epoch", Epoch.Json())
                .Str("instance", InstanceManifest.Name)
                .Str("player", SafeName(human))
                .Int("humanId", human.ReferenceId)
                .Int("clientId", (long)human.OwnerClientId)
                .Bit("isLocalPlayer", isLocal)
                .Bit("hasSimulationAuthority", SafeRunSimulation())
                .Raw("slots", "[" + string.Join(",", slots.ToArray()) + "]");
            if (!isLocal)
                res.Str("note", "activeHand and inactiveHand cannot be resolved for a character this " +
                                "process does not own: the active hand is client-local UI state and is " +
                                "never replicated. Name leftHand, rightHand or either.");
            return HttpResponse.Json(res.ToString());
        }

        private static string SafeStringKey(Slot slot)
        {
            try { return slot.StringKey; } catch { return null; }
        }

        private static string SafeSlotType(Slot slot)
        {
            try { return slot.Type.ToString(); } catch { return null; }
        }

        private static bool SafeIsHandSlot(Slot slot)
        {
            try { return slot.IsHandSlot; } catch { return false; }
        }

        private static bool SafeRunSimulation()
        {
            try { return GameManager.RunSimulation; } catch { return false; }
        }

        // ---- POST /inventory/move -------------------------------------------

        /// <summary>
        ///     Moves an existing Thing into a slot through <c>OnServer.MoveToSlot</c>, the same call
        ///     <c>Slot.PlayerMoveToSlot</c> makes for every inventory drag in the UI. On a client
        ///     that is a <c>MoveToSlotMessage</c> to the server, so the move is server-authoritative
        ///     and the caller has no authority requirement at all.
        ///
        ///     Waits for the destination slot to actually hold the Thing before answering, because
        ///     on a client the message is fire-and-forget and the occupancy only lands when the
        ///     server's next state delta arrives. "The call returned" is not evidence of anything.
        /// </summary>
        private static HttpResponse InventoryMove(IDictionary body)
        {
            int timeoutMs = Json.GetInt(body, "timeoutMs", 10000);
            bool wait = Json.GetBool(body, "wait", true);
            string toSpec = Json.GetStr(body, "to", "activeHand");
            string fromSpec = Json.GetStr(body, "from");
            long thingId = Json.GetLong(body, "thing", 0);
            if (thingId == 0) thingId = Json.GetLong(body, "thingId", 0);
            long intoThingId = Json.GetLong(body, "intoThing", 0);
            bool replace = Json.GetBool(body, "replace", false);

            // Resolve everything, then issue the move, in one main-thread hop: a Thing that moved
            // between the resolve and the call would be a race with the game's own simulation.
            var step = MainStep(() =>
            {
                Human local;
                try { local = Human.LocalHuman; } catch { local = null; }
                if (local == null) return Stop(Fail("no local player"));

                DynamicThing thing = null;

                if (thingId != 0)
                {
                    thing = Thing.Find(thingId) as DynamicThing;
                    if (thing == null)
                        return Stop(Fail("no DynamicThing with ReferenceId " + thingId +
                                         " (a Structure cannot go in a slot). GET /nearby to find one."));
                }
                else if (!string.IsNullOrEmpty(fromSpec))
                {
                    string e;
                    var sourceSlot = ResolveSlot(local, fromSpec, true, out e);
                    if (sourceSlot == null) return Stop(Fail("from: " + e));
                    thing = SlotOccupant(sourceSlot);
                    if (thing == null) return Stop(Fail("source slot '" + SlotName(sourceSlot) + "' is empty"));
                }
                else
                {
                    return Stop(HttpResponse.Error(
                        "pass 'thing' (a ReferenceId) or 'from' (a slot on this player)", 400));
                }

                Thing container = local;
                if (intoThingId != 0)
                {
                    container = Thing.Find(intoThingId);
                    if (container == null)
                        return Stop(Fail("no Thing with ReferenceId " + intoThingId + " to move into"));
                }

                string err;
                var dest = ResolveSlot(container, toSpec, ReferenceEquals(container, local), out err);
                if (dest == null) return Stop(Fail("to: " + err));

                var sitting = SlotOccupant(dest);
                if (sitting != null && sitting.ReferenceId == thing.ReferenceId)
                    return Stop(HttpResponse.Json(new Json.Obj()
                        .Bit("ok", true).Bit("alreadyThere", true)
                        .Int("thingId", thing.ReferenceId)
                        .Str("to", SlotName(dest))
                        .Raw("destination", StateReporter.DescribeSlot(dest))
                        .ToString()));

                if (sitting != null)
                {
                    if (!replace)
                        return Stop(Fail("destination slot '" + SlotName(dest) + "' already holds " +
                                         sitting.DisplayName + " (ref " + sitting.ReferenceId + "). " +
                                         "Thing.MoveToSlot refuses an occupied slot. Pass replace=true to drop it first."));
                    try { OnServer.MoveToWorld(sitting); }
                    catch (Exception ex)
                    {
                        return Stop(HttpResponse.Error("could not clear the destination slot: " + ex.Message));
                    }
                }

                try { OnServer.MoveToSlot(thing, dest); }
                catch (Exception ex) { return Stop(HttpResponse.Error("MoveToSlot failed: " + ex.Message)); }

                return new InventoryStep
                {
                    ThingId = thing.ReferenceId,
                    ThingPrefab = thing.PrefabName,
                    ParentId = dest.Parent == null ? 0 : dest.Parent.ReferenceId,
                    SlotIndex = SafeSlotIndex(dest),
                    SlotLabel = SlotName(dest),
                    Route = SafeRunSimulation()
                        ? "OnServer.MoveToSlot (authority, applied here)"
                        : "OnServer.MoveToSlot -> MoveToSlotMessage (sent to server)",
                };
            });

            if (step.Early != null) return step.Early;

            long movedId = step.ThingId;
            long destParent = step.ParentId;
            int destIndex = step.SlotIndex;

            string landed = null;
            if (wait)
                landed = PollUntil(timeoutMs, () => MainThreadPump.RunValue(
                    () => SlotHoldsThing(destParent, destIndex, movedId) ? "yes" : null, 5000));

            var seated = SafeMain(() => DescribeSlotAt(destParent, destIndex), "null");
            var o = new Json.Obj()
                .Bit("ok", !wait || landed != null)
                .Str("instance", InstanceManifest.Name)
                .Int("thingId", movedId)
                .Str("thingPrefab", step.ThingPrefab)
                .Str("to", step.SlotLabel)
                .Str("route", step.Route)
                .Bit("confirmed", landed != null)
                .Raw("destination", seated);
            if (!wait)
                return HttpResponse.Json(o.Str("note",
                    "wait=false, so this only means the call was issued. On a client the slot fills " +
                    "when the server's next state delta arrives; poll GET /inventory to confirm.").ToString());
            if (landed == null)
                o.Str("error", "the move was issued but the destination slot did not hold the Thing within " +
                               timeoutMs + " ms. On a client the server has to accept the MoveToSlotMessage " +
                               "and replicate the slot change back; a refusal is silent, so the likely causes " +
                               "are CanEnter (wrong slot type, or the item is not pickupable) or the slot " +
                               "having been filled in the meantime.");
            return landed != null ? HttpResponse.Json(o.ToString()) : HttpResponse.Error(o.ToString(), 409);
        }

        private static bool SlotHoldsThing(long parentId, int slotIndex, long thingId)
        {
            var slot = FindSlot(parentId, slotIndex);
            var occupant = SlotOccupant(slot);
            return occupant != null && occupant.ReferenceId == thingId;
        }

        private static Slot FindSlot(long parentId, int slotIndex)
        {
            if (parentId == 0 || slotIndex < 0) return null;
            var parent = Thing.Find(parentId);
            if (parent == null || parent.Slots == null || slotIndex >= parent.Slots.Count) return null;
            return parent.Slots[slotIndex];
        }

        private static string DescribeSlotAt(long parentId, int slotIndex)
        {
            return StateReporter.DescribeSlot(FindSlot(parentId, slotIndex));
        }

        // ---- POST /inventory/give -------------------------------------------

        /// <summary>
        ///     Creates a prefab straight into a named character's slot, including a REMOTE player's.
        ///     Needs simulation authority, so this is the listen host's (or single player's) route;
        ///     it is the client-rig twin of the dedicated server's ScenarioRunner give-item.
        ///
        ///     <c>OnServer.Create&lt;DynamicThing&gt;(prefab, slot)</c> is the game's own
        ///     create-into-a-slot path (it is what dresses a fresh character). It marks the new Thing
        ///     Indestructable for the duration of the move so a stray world collision cannot eat it
        ///     in flight, then <c>MoveToSlotOrWorld</c> seats it and the occupancy replicates to the
        ///     owning client like any other server-side slot change. No ground hop, no cursor.
        /// </summary>
        private static HttpResponse InventoryGive(IDictionary body)
        {
            string prefabName = Json.GetStr(body, "prefab");
            if (string.IsNullOrEmpty(prefabName)) return HttpResponse.Error("missing 'prefab'", 400);

            string slotSpec = Json.GetStr(body, "slot", "either");
            int quantity = Json.GetInt(body, "quantity", 0);
            bool replace = Json.GetBool(body, "replace", false);

            return Main(() =>
            {
                if (!SafeRunSimulation())
                    return Fail("creating into a slot needs simulation authority and this process does not " +
                                "have it. Run /inventory/give on the HOST, or run /inventory/arm on this " +
                                "client instead, which spawns through the server and then moves the item " +
                                "into a hand with a MoveToSlotMessage.");

                bool isLocal;
                string err;
                var human = ResolveHuman(body, out isLocal, out err);
                if (human == null) return Fail(err);

                var prefab = Prefab.Find(prefabName) as DynamicThing;
                if (prefab == null)
                    return Fail("no DynamicThing prefab named '" + prefabName + "'. Names are case sensitive; " +
                                "GET /prefabs?contains=... to search. A Structure prefab will not match, " +
                                "because only a DynamicThing can go in a slot.");

                // The same gate the vanilla `thing spawn` console command applies. In multiplayer
                // this is SharedDLCManager's union over connected clients, so a refusal here means
                // no connected player is entitled to the item either.
                try
                {
                    if (!SharedDLCManager.CheckSharedAccess(prefab.DLCType))
                        return Fail("'" + prefabName + "' needs DLC " + prefab.DLCType +
                                    " and SharedDLCManager.CheckSharedAccess says no. No connected client owns it.");
                }
                catch { /* the DLC check is advisory; a throw here must not block the give */ }

                var dest = ResolveSlot(human, slotSpec, isLocal, out err);
                if (dest == null) return Fail("slot: " + err);

                var sitting = SlotOccupant(dest);
                if (sitting != null)
                {
                    if (!replace)
                        return Fail("'" + SafeName(human) + "' slot '" + SlotName(dest) + "' already holds " +
                                    sitting.DisplayName + " (ref " + sitting.ReferenceId + "). " +
                                    "Pass replace=true to drop it, or name a different slot.");
                    // Drop rather than destroy. A test tool that silently deletes whatever was in
                    // the way is a worse tool than one that puts it on the floor.
                    try { OnServer.MoveToWorld(sitting); }
                    catch (Exception ex) { return HttpResponse.Error("could not clear the slot: " + ex.Message); }
                    if (SlotOccupant(dest) != null)
                        return Fail("slot still holds " + SlotName(dest) + " after the drop");
                }

                DynamicThing created;
                try { created = OnServer.Create<DynamicThing>(prefab, dest); }
                catch (Exception ex) { return HttpResponse.Error("OnServer.Create failed: " + ex.Message); }
                if (created == null)
                    return Fail("OnServer.Create('" + prefabName + "') returned null");

                string quantityNote = quantity > 0 ? TrySetQuantity(created, quantity) : null;

                // Read the slot back rather than trusting the return. MoveToSlotOrWorld falls back
                // to the world when the slot will not take the Thing and still returns it, so a
                // non-null result is not evidence the item is in the slot.
                var seated = SlotOccupant(dest);
                bool ok = seated != null && seated.ReferenceId == created.ReferenceId;

                var o = new Json.Obj()
                    .Bit("ok", ok)
                    .Str("instance", InstanceManifest.Name)
                    .Str("prefab", prefabName)
                    .Int("referenceId", created.ReferenceId)
                    .Str("player", SafeName(human))
                    .Int("humanId", human.ReferenceId)
                    .Int("clientId", (long)human.OwnerClientId)
                    .Bit("isLocalPlayer", isLocal)
                    .Str("slot", SlotName(dest))
                    .Int("slotIndex", SafeSlotIndex(dest))
                    .Raw("destination", StateReporter.DescribeSlot(dest));
                if (quantityNote != null) o.Str("quantity", quantityNote);
                if (!ok)
                    o.Str("error", "the Thing was created but the slot does not hold it: MoveToSlotOrWorld " +
                                   "fell back to the world, so the item is on the ground near the character.");
                if (ok && !isLocal)
                    o.Str("note", "the item is in that player's " + SlotName(dest) + ". Their ACTIVE hand is " +
                                  "client-local state this process cannot see, so if the test needs it held " +
                                  "actively, read GET /inventory on their instance or call /player/swaphands there.");
                if (ok && SafeClientCount() == 0)
                    o.Str("warning", "no clients are connected, so nothing was announced to anyone.");
                return ok ? HttpResponse.Json(o.ToString()) : HttpResponse.Error(o.ToString(), 409);
            });
        }

        private static int SafeClientCount()
        {
            try { return NetworkBase.Clients.Count; } catch { return -1; }
        }

        /// <summary>
        ///     <c>Stackable.SetQuantity</c> is the canonical server-side setter: it clamps to
        ///     MaxQuantity and its setter raises the network update flag, so the count replicates in
        ///     the same state tick. Anything that is not a Stackable carries its fullness on a
        ///     type-specific member instead, so the fallback reflects for a settable int Quantity and
        ///     says so plainly when there is not one. A wrong quantity never fails a paint test, and
        ///     throwing here would waste an item that is already in the slot.
        /// </summary>
        private static string TrySetQuantity(DynamicThing thing, int quantity)
        {
            try
            {
                var stackable = thing as Stackable;
                if (stackable != null)
                {
                    stackable.SetQuantity(quantity);
                    return stackable.Quantity == quantity
                        ? stackable.Quantity.ToString()
                        : stackable.Quantity + " (clamped from " + quantity + ", max " + stackable.MaxQuantity + ")";
                }

                var prop = thing.GetType().GetProperty("Quantity",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);
                if (prop != null && prop.CanWrite && prop.PropertyType == typeof(int))
                {
                    prop.SetValue(thing, quantity, null);
                    return prop.GetValue(thing, null).ToString();
                }

                return "not applied (" + thing.GetType().Name + " is not a Stackable and has no writable int Quantity)";
            }
            catch (Exception ex) { return "not applied (" + ex.Message + ")"; }
        }

        // ---- POST /inventory/arm --------------------------------------------

        /// <summary>
        ///     The whole job in one call, from the client that wants the item, whatever its role.
        ///
        ///     Three server-authoritative steps, each waited on rather than assumed:
        ///     <c>OnServer.SpawnDynamicThingMaxStack</c> (on a client a
        ///     <c>SpawnDynamicThingMaxStackMessage</c>; the server creates the Thing about a metre in
        ///     front of the character and announces it), then a wait for that Thing to arrive here,
        ///     then <c>OnServer.MoveToSlot</c> into a hand and a wait for the server to agree.
        ///
        ///     The item is identified by diffing: every ReferenceId of that prefab near the player is
        ///     recorded before the spawn, and the arrival is whichever matching Thing is new, has no
        ///     parent slot, and is nearest. That is a heuristic, and it is the one part of this route
        ///     that could in principle pick the wrong item; the response reports the id it chose and
        ///     how many candidates it saw, so a wrong pick is visible rather than silent. The
        ///     alternative, an id echoed back by the server, does not exist:
        ///     <c>SpawnDynamicThingMaxStackMessage</c> has no reply.
        ///
        ///     <c>replace=true</c> on a client emits a <c>MoveToWorldMessage</c> to empty the hand
        ///     and then a <c>MoveToSlotMessage</c> to fill it, and relies on the server processing
        ///     them in that order. That holds for an ordered channel and has not been measured, so
        ///     prefer starting from an empty hand; if the confirm times out with the old item still
        ///     held, that ordering is the first thing to suspect.
        /// </summary>
        private static HttpResponse InventoryArm(IDictionary body)
        {
            string prefabName = Json.GetStr(body, "prefab");
            if (string.IsNullOrEmpty(prefabName)) return HttpResponse.Error("missing 'prefab'", 400);

            string handSpec = Json.GetStr(body, "hand", "activeHand");
            int timeoutMs = Json.GetInt(body, "timeoutMs", 20000);
            float radius = Json.GetFloat(body, "searchRadius", 8f);
            bool replace = Json.GetBool(body, "replace", false);
            int quantity = Json.GetInt(body, "quantity", 0);

            // Step 0: validate the prefab and the target hand, and record what is already lying
            // around, before anything is created. Failing here costs nothing; failing after the
            // spawn leaves an orphan item on the ground.
            var before = new HashSet<long>();
            var pre = MainStep(() =>
            {
                Human local;
                try { local = Human.LocalHuman; } catch { local = null; }
                if (local == null) return Stop(Fail("no local player"));

                var prefab = Prefab.Find(prefabName) as DynamicThing;
                if (prefab == null)
                    return Stop(Fail("no DynamicThing prefab named '" + prefabName + "'. Names are case " +
                                     "sensitive; GET /prefabs?contains=... to search."));

                string err;
                var hand = ResolveSlot(local, handSpec, true, out err);
                if (hand == null) return Stop(Fail("hand: " + err));

                var sitting = SlotOccupant(hand);
                if (sitting != null && !replace)
                    return Stop(Fail("the " + SlotName(hand) + " already holds " + sitting.DisplayName +
                                     " (ref " + sitting.ReferenceId + "). Thing.MoveToSlot refuses an occupied " +
                                     "slot, so pass replace=true to drop it first, name the other hand, or " +
                                     "empty it with a console 'thing delete " + sitting.ReferenceId + "'."));

                CollectNearbyOfPrefab(local, prefab.PrefabHash, radius, before);

                return new InventoryStep
                {
                    HumanId = local.ReferenceId,
                    PrefabHash = prefab.PrefabHash,
                    ParentId = hand.Parent == null ? 0 : hand.Parent.ReferenceId,
                    SlotIndex = SafeSlotIndex(hand),
                    SlotLabel = SlotName(hand),
                };
            });
            if (pre.Early != null) return pre.Early;

            long humanId = pre.HumanId;
            int prefabHash = pre.PrefabHash;
            long handParentId = pre.ParentId;
            int handSlotIndex = pre.SlotIndex;
            string handName = pre.SlotLabel;

            // Step 1: ask the server to create it. On a client this is a message; on the host it
            // runs inline. Either way the item lands on the ground in front of the character, which
            // is the whole reason this route exists rather than being one call to /spawn/world.
            var spawn = Main(() =>
            {
                try { OnServer.SpawnDynamicThingMaxStack(humanId, prefabName); }
                catch (Exception ex) { return HttpResponse.Error("spawn failed: " + ex.Message); }
                return OkJson();
            });
            if (spawn.Status != 200) return spawn;

            // Step 2: wait for it to exist here. On a client that is a round trip through the
            // server plus the create announcement, so it is not instant.
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            long newId = 0;
            int candidates = 0;
            var found = PollUntil(timeoutMs, () => MainThreadPump.RunValue(() =>
            {
                long id;
                int seen;
                if (!FindSpawnedThing(humanId, prefabHash, radius, before, out id, out seen)) { candidates = seen; return null; }
                newId = id;
                candidates = seen;
                return "yes";
            }, 5000));

            if (found == null)
                return HttpResponse.Error(new Json.Obj()
                    .Bit("ok", false)
                    .Str("instance", InstanceManifest.Name)
                    .Str("prefab", prefabName)
                    .Str("stage", "spawn")
                    .Str("error", "the spawn was sent but no new '" + prefabName + "' appeared within " +
                                  timeoutMs + " ms, inside " + radius + " m of the character. Causes worth " +
                                  "checking: the prefab is tagged NotSpawnable, SharedDLCManager refused it " +
                                  "(both print to the SERVER's console, not this one), or the item landed " +
                                  "outside the search radius. Raise searchRadius, or check the host's " +
                                  "/console/log.")
                    .Int("preexisting", before.Count)
                    .ToString(), 409);

            // Step 3: move it into the hand. This is /inventory/move's mechanism, inlined so the
            // whole thing stays one HTTP call.
            var move = Main(() =>
            {
                var thing = Thing.Find(newId) as DynamicThing;
                if (thing == null) return Fail("the spawned Thing " + newId + " vanished before it could be moved");

                var hand = FindSlot(handParentId, handSlotIndex);
                if (hand == null) return Fail("the hand slot went away");

                var sitting = SlotOccupant(hand);
                if (sitting != null)
                {
                    try { OnServer.MoveToWorld(sitting); }
                    catch (Exception ex) { return HttpResponse.Error("could not clear the hand: " + ex.Message); }
                }

                if (quantity > 0) TrySetQuantity(thing, quantity);

                try { OnServer.MoveToSlot(thing, hand); }
                catch (Exception ex) { return HttpResponse.Error("MoveToSlot failed: " + ex.Message); }
                return OkJson();
            });
            if (move.Status != 200) return move;

            int remaining = (int)Math.Max(2000, (deadline - DateTime.UtcNow).TotalMilliseconds);
            var landed = PollUntil(remaining, () => MainThreadPump.RunValue(
                () => SlotHoldsThing(handParentId, handSlotIndex, newId) ? "yes" : null, 5000));

            var o = new Json.Obj()
                .Bit("ok", landed != null)
                .Str("instance", InstanceManifest.Name)
                .Str("prefab", prefabName)
                .Int("referenceId", newId)
                .Str("hand", handName)
                .Bit("confirmed", landed != null)
                .Int("candidatesSeen", candidates)
                .Str("route", SafeRunSimulation()
                    ? "OnServer.SpawnDynamicThingMaxStack + OnServer.MoveToSlot (authority, applied here)"
                    : "SpawnDynamicThingMaxStackMessage + MoveToSlotMessage (both to the server)")
                .Raw("activeHand", SafeMain(() => StateReporter.DescribeSlot(InventoryManager.ActiveHandSlot), "null"))
                .Raw("destination", SafeMain(() => DescribeSlotAt(handParentId, handSlotIndex), "null"));
            if (landed == null)
                o.Str("error", "the item was created (ref " + newId + ") but the hand did not hold it within " +
                               "the timeout, so it is on the ground near the character. Retry with " +
                               "/inventory/move thing=" + newId + " to=" + handName + ", which skips the spawn.");
            if (candidates > 1)
                o.Str("warning", "more than one new '" + prefabName + "' was in range, so the nearest was " +
                                 "taken. If that is the wrong one, clear the loose copies first.");
            return landed != null ? HttpResponse.Json(o.ToString()) : HttpResponse.Error(o.ToString(), 409);
        }

        private static void CollectNearbyOfPrefab(Human human, int prefabHash, float radius, HashSet<long> into)
        {
            Vector3 origin;
            try { origin = human.ThingTransformPosition; } catch { return; }
            try
            {
                // OcclusionManager.AllThings is a ConcurrentDensePool<Thing> whose enumerator is a
                // ref struct, so ForEach(Action<T>) is the supported traversal.
                OcclusionManager.AllThings.ForEach(thing =>
                {
                    if (thing == null) return;
                    if (thing.PrefabHash != prefabHash) return;
                    Vector3 p;
                    try { p = thing.ThingTransformPosition; } catch { return; }
                    if (Vector3.Distance(origin, p) > radius) return;
                    into.Add(thing.ReferenceId);
                });
            }
            catch { }
        }

        private static bool FindSpawnedThing(long humanId, int prefabHash, float radius,
                                             HashSet<long> before, out long id, out int candidates)
        {
            id = 0;
            candidates = 0;
            var human = Thing.Find(humanId) as Human;
            if (human == null) return false;

            Vector3 origin;
            try { origin = human.ThingTransformPosition; } catch { return false; }

            long best = 0;
            float bestDistance = float.MaxValue;
            int seen = 0;
            try
            {
                OcclusionManager.AllThings.ForEach(thing =>
                {
                    if (thing == null) return;
                    if (thing.PrefabHash != prefabHash) return;
                    if (before.Contains(thing.ReferenceId)) return;
                    var dynamicThing = thing as DynamicThing;
                    if (dynamicThing == null) return;
                    // Anything already in a slot is somebody else's; only a loose item is the one
                    // the server just dropped in front of us.
                    if (dynamicThing.ParentSlot != null) return;
                    Vector3 p;
                    try { p = thing.ThingTransformPosition; } catch { return; }
                    float d = Vector3.Distance(origin, p);
                    if (d > radius) return;
                    seen++;
                    if (d < bestDistance) { bestDistance = d; best = thing.ReferenceId; }
                });
            }
            catch { return false; }

            candidates = seen;
            if (best == 0) return false;
            id = best;
            return true;
        }
    }
}
