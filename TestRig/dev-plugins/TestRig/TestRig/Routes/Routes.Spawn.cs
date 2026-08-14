using System;
using System.Collections;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Inventory;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using UnityEngine;
using GameManager = Assets.Scripts.GameManager;

namespace TestRig
{
    /// <summary>
    ///     Spawning routes.
    ///
    ///     Three routes because authority differs. A pure client cannot instantiate into a slot, so
    ///     the hand route needs simulation authority; the world route forwards through the server;
    ///     and the structure route goes through <c>Constructor.SpawnConstruct</c>, which is
    ///     client-safe and sends a message instead of instantiating locally.
    ///
    ///     For putting an item into a connected player's hand, prefer the server-side give-item
    ///     scenario in ScenarioRunner: it hands the item over from the simulation owner and never
    ///     involves the client's cursor or authority at all.
    /// </summary>
    internal static partial class Router
    {
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
                            "Use the ScenarioRunner give-item scenario on the server, or /console/exec with " +
                            "'thing spawn <prefab>', which round-trips through the server.");

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

            // The console path is client-safe: SpawnDynamicThingMaxStack forwards to the server
            // when this process is not the simulation owner.
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
        ///     Places a built Structure on the world grid without the build UI, through
        ///     <c>Constructor.SpawnConstruct</c>. That call is client-safe: on a pure client it
        ///     sends a <c>ConstructionCreationMessage</c> instead of instantiating locally, and
        ///     returns null, which is not a failure.
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
                o.Str("note", "SpawnConstruct returned null: this is a client, so the placement went to " +
                              "the server as a ConstructionCreationMessage. Poll /nearby to confirm it landed.");
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
    }
}
