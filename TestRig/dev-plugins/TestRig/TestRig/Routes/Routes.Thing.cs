using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts.Objects;

namespace TestRig
{
    /// <summary>
    ///     Reading state off a specific object, on a specific instance.
    ///
    ///     Three routes, in increasing order of "I do not know what I am looking for":
    ///
    ///     <list type="bullet">
    ///       <item><c>GET /thing</c> reads named members off one or several Things by reference id,
    ///       and carries a location block that answers where each one lives.</item>
    ///       <item><c>GET /reflect/instance</c> reads ONE member on one object, with the declaring
    ///       type pinnable. It is the instance twin of <c>/reflect</c>, which reads statics.</item>
    ///       <item><c>GET /thing/members</c> lists every instance member a type has. The diagnostic
    ///       of last resort, mirroring <c>/reflect/members</c>.</item>
    ///     </list>
    ///
    ///     The engine, and the reasons the answers are shaped the way they are, is in
    ///     <see cref="ThingReflect"/>. The three things worth knowing at this level:
    ///
    ///     A member that does not exist answers <c>ok:false</c> with the type chain it searched, not
    ///     an empty value. A value equal to the one on the Thing's own PREFAB is flagged
    ///     <c>matchesPrefab</c>, because such a value is indistinguishable from never having been
    ///     set on this instance (<c>Thing.EmissionColor</c> initialises to <c>Color.white</c>, so an
    ///     unpainted object reads as glowing and a live run drew the wrong conclusion from exactly
    ///     that). And every response carries the epoch, so the answer is attributable to the
    ///     instance that produced it and to the stretch of that instance's life it was valid in.
    /// </summary>
    internal static partial class Router
    {
        /// <summary>How many Things one request may ask about, so a response stays readable.</summary>
        private const int MaxThingsPerRequest = 50;

        // ---- GET /thing ------------------------------------------------------

        private static HttpResponse ThingRoute(IDictionary body)
        {
            var ids = ParseIds(body, "refId", "refIds", "id", "ids");
            if (ids.Count == 0)
                return HttpResponse.Error(
                    "pass 'refId' (a Thing ReferenceId) or 'refIds' (a comma-separated list). " +
                    "GET /nearby finds ids around the player; GET /inventory finds them in slots.", 400);
            if (ids.Count > MaxThingsPerRequest)
                return HttpResponse.Error(
                    "asked about " + ids.Count + " Things; the cap is " + MaxThingsPerRequest +
                    " per request so one response stays readable. Split the request.", 400);

            var fields = ParseList(body, "fields", "field");
            string pinnedName = Json.GetStr(body, "type");
            bool comparePrefab = Json.GetBool(body, "comparePrefab", true);
            bool expand = Json.GetBool(body, "expand", false);
            int expandLimit = Math.Max(1, Math.Min(500, Json.GetInt(body, "expandLimit", 25)));
            string key = Json.GetStr(body, "key");

            Type pinned = null;
            if (!string.IsNullOrEmpty(pinnedName))
            {
                pinned = ConfigAccess.ResolveType(pinnedName);
                if (pinned == null)
                    return HttpResponse.Error(
                        "type '" + pinnedName + "' is not loaded in this process. 'type' is optional and " +
                        "only pins WHICH declaring type a member is looked up on; drop it to search the " +
                        "Thing's own runtime type and every base type.", 400);
            }

            var rows = new List<string>();
            var missing = new List<string>();
            bool allOk = true;

            foreach (long id in ids)
            {
                var thing = ThingReflect.Find(id);
                var row = new Json.Obj();
                // Every row names its own instance. A snapshot that concatenates rows from several
                // instances is the normal way this gets read, and an unattributed value is what
                // produced a retracted conclusion once already.
                row.Str("instance", string.IsNullOrEmpty(InstanceManifest.Name) ? "(unnamed)" : InstanceManifest.Name);
                row.Str("requestedRefId", id.ToString(CultureInfo.InvariantCulture));

                if (thing == null)
                {
                    allOk = false;
                    missing.Add(id.ToString(CultureInfo.InvariantCulture));
                    row.Bit("found", false)
                       .Str("error", "no Thing with ReferenceId " + id.ToString(CultureInfo.InvariantCulture) +
                                     " exists on this instance. On a joined client that can also mean the " +
                                     "server has not replicated it here yet, which is a different fact from " +
                                     "it not existing: read the same id on the instance whose " +
                                     "/status.role is listenHost or dedicated.");
                    rows.Add(row.ToString());
                    continue;
                }

                row.Bit("found", true);
                ThingReflect.DescribeThing(row, thing);
                row.Raw("location", ThingReflect.LocationJson(thing));

                if (fields.Count > 0)
                {
                    Thing prefab = comparePrefab ? ThingReflect.PrefabOf(thing) : null;
                    var fieldRows = new List<string>();
                    foreach (string field in fields)
                    {
                        var fieldRow = ReadField(thing, prefab, field, pinned, expand, expandLimit, key);
                        if (fieldRow.Ok == false) allOk = false;
                        fieldRows.Add(fieldRow.Json);
                    }
                    row.Raw("fields", "[" + string.Join(",", fieldRows.ToArray()) + "]");
                    if (comparePrefab && prefab == null)
                        row.Str("prefabNote",
                            "the prefab for this Thing could not be resolved, so matchesPrefab is null on " +
                            "every field. A value that equals its prefab's is indistinguishable from never " +
                            "having been set, and without the prefab that cannot be checked.");
                }

                rows.Add(row.ToString());
            }

            var o = new Json.Obj()
                .Bit("ok", allOk)
                .Str("instance", string.IsNullOrEmpty(InstanceManifest.Name) ? "(unnamed)" : InstanceManifest.Name)
                .Raw("epoch", Epoch.Json())
                .Int("requested", ids.Count)
                .Int("found", ids.Count - missing.Count)
                .StrArray("missing", missing)
                .Raw("things", "[" + string.Join(",", rows.ToArray()) + "]");
            if (!allOk)
                o.Str("error", "at least one Thing or one requested member did not resolve. Read the " +
                               "per-row 'error' fields: a member that does not exist is reported as " +
                               "ok:false rather than as an empty value, on purpose.");
            return HttpResponse.Json(o.ToString(), allOk ? 200 : 409);
        }

        /// <summary>One field row plus whether it counted as a success.</summary>
        private sealed class FieldRow
        {
            internal bool Ok;
            internal string Json;
        }

        private static FieldRow ReadField(Thing thing, Thing prefab, string path, Type pinned,
                                          bool expand, int expandLimit, string key)
        {
            var o = new Json.Obj().Str("name", path);
            var read = ThingReflect.ReadPath(thing, path, pinned);

            if (!read.Ok)
            {
                o.Bit("ok", false).Str("error", read.Error);
                if (read.Member != null)
                {
                    o.Str("kind", read.Member.Kind);
                    o.Str("declaredBy", read.Member.DeclaringType == null ? null : read.Member.DeclaringType.FullName);
                }
                return new FieldRow { Ok = false, Json = o.ToString() };
            }

            o.Bit("ok", true);
            o.Str("kind", read.Member.Kind);
            o.Str("resolvedName", read.Member.Name);
            o.Str("declaredBy", read.Member.DeclaringType == null ? null : read.Member.DeclaringType.FullName);
            o.Str("declaredType", read.Member.DeclaredType == null ? null : read.Member.DeclaredType.FullName);
            ThingReflect.Describe(o, read.Value, expand, expandLimit, key);

            // The prefab comparison. See ThingReflect.PrefabOf for why this is not a nicety.
            if (prefab != null)
            {
                var prefabRead = ThingReflect.ReadPath(prefab, path, pinned);
                if (prefabRead.Ok)
                {
                    var prefabRow = new Json.Obj();
                    ThingReflect.Describe(prefabRow, prefabRead.Value, false, 0, null);
                    string prefabJson = prefabRow.ToString();
                    bool same = string.Equals(prefabJson, ThingReflect.RenderForCompare(read.Value),
                                              StringComparison.Ordinal);
                    o.Raw("prefabValue", prefabJson);
                    o.Bit("matchesPrefab", same);
                    if (same)
                        o.Str("matchesPrefabNote",
                            "this value is identical to the one on the untouched prefab, so it is " +
                            "indistinguishable from never having been set on this instance. Treat it as " +
                            "'no evidence' rather than as a reading.");
                }
                else
                {
                    o.Raw("matchesPrefab", "null");
                    o.Str("prefabError", prefabRead.Error);
                }
            }
            else
            {
                o.Raw("matchesPrefab", "null");
            }

            return new FieldRow { Ok = true, Json = o.ToString() };
        }

        // ---- GET /reflect/instance -------------------------------------------

        /// <summary>
        ///     Reads one instance member on the object with a given reference id, optionally pinned
        ///     to a declaring type.
        ///
        ///     The twin of <c>/reflect</c>, which reads statics by type name. The <c>type</c>
        ///     parameter is why this is a separate route rather than a flag on <c>/thing</c>: it
        ///     names WHICH type in the object's hierarchy to look the member up on, which is the
        ///     only way to reach a private base-class field that a derived type shadows with
        ///     <c>new</c>, and the only way to be sure which of two same-named members answered. A
        ///     <c>ConfigEntry&lt;T&gt;</c> is unwrapped to its value, exactly as <c>/reflect</c>
        ///     does, because the wrapper is never the thing anybody wanted.
        /// </summary>
        private static HttpResponse ReflectInstanceRoute(IDictionary body)
        {
            long refId = Json.GetLong(body, "refId", 0);
            if (refId == 0) refId = Json.GetLong(body, "id", 0);
            string member = Json.GetStr(body, "member");
            if (string.IsNullOrEmpty(member)) member = Json.GetStr(body, "path");
            string pinnedName = Json.GetStr(body, "type");
            bool expand = Json.GetBool(body, "expand", false);
            int expandLimit = Math.Max(1, Math.Min(500, Json.GetInt(body, "expandLimit", 25)));
            string key = Json.GetStr(body, "key");

            if (refId == 0) return HttpResponse.Error("missing 'refId' (a Thing ReferenceId)", 400);
            if (string.IsNullOrEmpty(member))
                return HttpResponse.Error("missing 'member' (a member name, or a dotted path such as " +
                                          "ParentSlot.Parent.ReferenceId)", 400);

            Type pinned = null;
            if (!string.IsNullOrEmpty(pinnedName))
            {
                pinned = ConfigAccess.ResolveType(pinnedName);
                if (pinned == null)
                    return HttpResponse.Error("type '" + pinnedName + "' is not loaded in this process", 400);
            }

            var thing = ThingReflect.Find(refId);
            if (thing == null)
                return Fail("no Thing with ReferenceId " + refId.ToString(CultureInfo.InvariantCulture) +
                            " on this instance (" + InstanceManifest.Name + "). GET /nearby to find one.");

            var read = ThingReflect.ReadPath(thing, member, pinned);
            var o = new Json.Obj()
                .Bit("ok", read.Ok)
                .Str("instance", string.IsNullOrEmpty(InstanceManifest.Name) ? "(unnamed)" : InstanceManifest.Name)
                .Raw("epoch", Epoch.Json())
                .Str("refId", refId.ToString(CultureInfo.InvariantCulture))
                .Str("member", member)
                .Str("pinnedType", pinned == null ? null : pinned.FullName);

            var identity = new Json.Obj();
            ThingReflect.DescribeThing(identity, thing);
            o.Raw("thing", identity.ToString());

            if (!read.Ok)
            {
                o.Str("error", read.Error);
                return HttpResponse.Json(o.ToString(), 409);
            }

            o.Str("kind", read.Member.Kind);
            o.Str("resolvedName", read.Member.Name);
            o.Str("declaredBy", read.Member.DeclaringType == null ? null : read.Member.DeclaringType.FullName);
            o.Str("declaredType", read.Member.DeclaredType == null ? null : read.Member.DeclaredType.FullName);

            object value = Unwrap(read.Value);
            ThingReflect.Describe(o, value, expand, expandLimit, key);
            if (!ReferenceEquals(value, read.Value)) o.Bit("unwrappedConfigEntry", true);
            return HttpResponse.Json(o.ToString());
        }

        /// <summary>
        ///     A <c>ConfigEntry&lt;T&gt;</c> is far more useful as its value than as a type name.
        ///     Same unwrap <c>/reflect</c> applies to statics.
        /// </summary>
        private static object Unwrap(object value)
        {
            try
            {
                if (value == null) return null;
                var t = value.GetType();
                if (!t.IsGenericType ||
                    t.GetGenericTypeDefinition() != typeof(BepInEx.Configuration.ConfigEntry<>)) return value;
                var prop = t.GetProperty("Value");
                return prop == null ? value : prop.GetValue(value, null);
            }
            catch { return value; }
        }

        // ---- GET /thing/members ----------------------------------------------

        /// <summary>
        ///     Every instance field and property of a Thing (or of a bare type), with its declaring
        ///     type and its current value. The diagnostic of last resort when a member lookup finds
        ///     the object but not the member somebody expected.
        ///
        ///     <c>values=false</c> is not paranoia. A property getter is arbitrary game code: it can
        ///     allocate, lazily construct, or throw. A throw is caught and reported per member, but a
        ///     side effect is not preventable, so a caller that only wants the SHAPE of a type asks
        ///     for names and types alone.
        /// </summary>
        private static HttpResponse ThingMembersRoute(IDictionary body)
        {
            long refId = Json.GetLong(body, "refId", 0);
            if (refId == 0) refId = Json.GetLong(body, "id", 0);
            string typeName = Json.GetStr(body, "type");
            string contains = Json.GetStr(body, "contains");
            int limit = Math.Max(1, Math.Min(2000, Json.GetInt(body, "limit", 400)));
            bool readValues = Json.GetBool(body, "values", true);

            Thing thing = null;
            Type type = null;

            if (refId != 0)
            {
                thing = ThingReflect.Find(refId);
                if (thing == null)
                    return Fail("no Thing with ReferenceId " + refId.ToString(CultureInfo.InvariantCulture) +
                                " on this instance (" + InstanceManifest.Name + ")");
                type = thing.GetType();
            }
            else if (!string.IsNullOrEmpty(typeName))
            {
                type = ConfigAccess.ResolveType(typeName);
                if (type == null)
                    return HttpResponse.Error("type '" + typeName + "' is not loaded in this process", 400);
                readValues = false;   // no instance to read from
            }
            else
            {
                return HttpResponse.Error(
                    "pass 'refId' (a Thing ReferenceId, which gives names, types AND values) or 'type' " +
                    "(a full type name, which gives names and types only)", 400);
            }

            var o = new Json.Obj()
                .Bit("ok", true)
                .Str("instance", string.IsNullOrEmpty(InstanceManifest.Name) ? "(unnamed)" : InstanceManifest.Name)
                .Raw("epoch", Epoch.Json())
                .Str("type", type.FullName)
                .Str("typeChain", ThingReflect.TypeChain(type))
                .Str("assembly", ConfigAccess.AsmName(type))
                .Bit("valuesRead", readValues);
            if (thing != null)
            {
                var identity = new Json.Obj();
                ThingReflect.DescribeThing(identity, thing);
                o.Raw("thing", identity.ToString());
            }
            if (readValues)
                o.Str("note", "every property getter listed here was INVOKED to produce its value. A " +
                              "getter that throws is reported in that member's 'error'; a getter with a " +
                              "side effect is not preventable. Pass values=false for names and types only.");

            string members = ThingReflect.MembersJson(type, thing, contains, limit, readValues);
            o.Raw("members", members);
            return HttpResponse.Json(o.ToString());
        }

        // ---- parsing helpers --------------------------------------------------

        /// <summary>
        ///     Reads a comma-separated list from any of several aliases, in order. Blank entries are
        ///     dropped rather than turned into a member named "".
        /// </summary>
        private static List<string> ParseList(IDictionary body, params string[] keys)
        {
            var result = new List<string>();
            foreach (string key in keys)
            {
                string raw = Json.GetStr(body, key);
                if (string.IsNullOrEmpty(raw)) continue;
                foreach (string part in raw.Split(','))
                {
                    string trimmed = part.Trim();
                    if (trimmed.Length > 0 && !result.Contains(trimmed)) result.Add(trimmed);
                }
            }
            return result;
        }

        private static List<long> ParseIds(IDictionary body, params string[] keys)
        {
            var result = new List<long>();
            foreach (string raw in ParseList(body, keys))
            {
                long id;
                if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) continue;
                if (id != 0 && !result.Contains(id)) result.Add(id);
            }
            return result;
        }
    }
}
