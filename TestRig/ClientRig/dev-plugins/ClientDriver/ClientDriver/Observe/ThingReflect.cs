using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Assets.Scripts.Inventory;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using UnityEngine;

namespace ClientDriver
{
    /// <summary>
    ///     Reads any instance member of any Thing, on whichever instance is asked.
    ///
    ///     This closes the gap that made every client-side assertion expensive. <c>/reflect</c>
    ///     reads STATICS only, <c>/status</c> and <c>/nearby</c> expose fixed field sets, and
    ///     anything outside those needed an InspectorPlus request file plus a bespoke script: a live
    ///     run had to write one just to read <c>Thing.EmissionColor</c> off two pipes.
    ///
    ///     Three things here are not just "call GetValue", and each exists because of a way a
    ///     reflective read lies:
    ///
    ///     <list type="bullet">
    ///       <item><b>A missing member is not an empty value.</b> Every read reports
    ///       <c>ok</c> separately from <c>value</c>, and a name that does not resolve answers
    ///       <c>ok:false</c> with the type it searched and a pointer at <c>/thing/members</c>.
    ///       Returning "" for a member that does not exist reads as a measurement.</item>
    ///
    ///       <item><b>A value can equal its never-set value and mean nothing.</b>
    ///       <c>Thing.EmissionColor</c> initialises to <c>Color.white</c>, so an object that has
    ///       never been painted reads (1,1,1,1) and looks like it is glowing. The general answer is
    ///       to read the SAME member off the prefab, which is the untouched template, and report
    ///       <c>matchesPrefab</c>. A field that matches its prefab is indistinguishable from never
    ///       set, whatever it says, and that is true for every mod's fields as much as for the
    ///       game's.</item>
    ///
    ///       <item><b>A Unity object can be destroyed and still not be null.</b>
    ///       <c>UnityEngine.Object</c> overloads <c>==</c>, so a destroyed Material is a live C#
    ///       reference that the game treats as null. <c>isNull</c> uses the game's answer and
    ///       <c>destroyed</c> names the difference.</item>
    ///     </list>
    ///
    ///     Everything here must run on the Unity main thread: it touches Unity objects and it
    ///     invokes property getters, which are arbitrary game code.
    /// </summary>
    internal static class ThingReflect
    {
        /// <summary>
        ///     Instance members, declared on one type only. <c>FlattenHierarchy</c> applies to
        ///     statics, so a private field on a base class is invisible without walking the chain
        ///     ourselves. Every lookup below therefore walks <c>BaseType</c> explicitly.
        /// </summary>
        private const BindingFlags Declared =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        private const BindingFlags DeclaredLoose = Declared | BindingFlags.IgnoreCase;

        // ---- resolution ------------------------------------------------------

        internal static Thing Find(long referenceId)
        {
            if (referenceId == 0) return null;
            try { return Thing.Find(referenceId); } catch { return null; }
        }

        /// <summary>
        ///     A member resolved on some type in an object's hierarchy, plus how to read it.
        /// </summary>
        internal sealed class Member
        {
            internal string Kind;
            internal string Name;
            internal Type DeclaringType;
            internal Type DeclaredType;
            private FieldInfo _field;
            private PropertyInfo _property;

            internal object Read(object target)
            {
                if (_field != null) return _field.GetValue(target);
                return _property.GetValue(target, null);
            }

            internal static Member Of(FieldInfo f) => new Member
            {
                Kind = "field", Name = f.Name, DeclaringType = f.DeclaringType,
                DeclaredType = f.FieldType, _field = f,
            };

            internal static Member Of(PropertyInfo p) => new Member
            {
                Kind = "property", Name = p.Name, DeclaringType = p.DeclaringType,
                DeclaredType = p.PropertyType, _property = p,
            };
        }

        /// <summary>
        ///     Finds an instance member by name, walking the type's base chain. A property wins over
        ///     a field of the same name, matching <c>/reflect</c>; an exact-case match wins over a
        ///     case-insensitive one, so <c>quantity</c> still finds <c>Quantity</c> without letting
        ///     a near-miss shadow a real member.
        ///
        ///     <paramref name="pinnedType"/> narrows the search to one declaring type. That is what
        ///     makes a member reachable when a derived type shadows it with <c>new</c>, and it is
        ///     the reason the instance routes take a <c>type</c> parameter at all.
        /// </summary>
        internal static Member FindMember(Type start, string name, Type pinnedType)
        {
            if (start == null || string.IsNullOrEmpty(name)) return null;

            if (pinnedType != null)
            {
                var pinned = OnType(pinnedType, name, Declared) ?? OnType(pinnedType, name, DeclaredLoose);
                if (pinned != null) return pinned;
                // Fall through: a caller who pinned a base type of the runtime type still expects the
                // member, and pinning is a hint about WHERE to look rather than a refusal to look on.
            }

            for (var t = start; t != null; t = t.BaseType)
            {
                var exact = OnType(t, name, Declared);
                if (exact != null) return exact;
            }
            for (var t = start; t != null; t = t.BaseType)
            {
                var loose = OnType(t, name, DeclaredLoose);
                if (loose != null) return loose;
            }
            return null;
        }

        private static Member OnType(Type t, string name, BindingFlags flags)
        {
            try
            {
                var p = t.GetProperty(name, flags);
                if (p != null && p.CanRead && p.GetIndexParameters().Length == 0) return Member.Of(p);
            }
            catch { }
            try
            {
                var f = t.GetField(name, flags);
                if (f != null) return Member.Of(f);
            }
            catch { }
            return null;
        }

        /// <summary>
        ///     Every type this member lookup would search, for an error message that actually helps.
        /// </summary>
        internal static string TypeChain(Type start)
        {
            var parts = new List<string>();
            for (var t = start; t != null && parts.Count < 12; t = t.BaseType) parts.Add(t.Name);
            return string.Join(" -> ", parts.ToArray());
        }

        // ---- reading ---------------------------------------------------------

        /// <summary>
        ///     The outcome of reading one member or one dotted path.
        /// </summary>
        internal sealed class Read
        {
            internal bool Ok;
            internal string Error;
            internal Member Member;
            internal object Value;
            /// <summary>The object the final segment was read from, for a prefab comparison.</summary>
            internal object Owner;
        }

        /// <summary>
        ///     Reads a member path off an object. A path is one member name, or several joined with
        ///     dots (<c>ParentSlot.Parent.ReferenceId</c>), with an optional <c>[n]</c> index on any
        ///     segment whose value is a list.
        ///
        ///     A dotted path whose intermediate is null answers <c>ok:false</c> naming the segment
        ///     that was null, never a bare null value: "the field is null" and "something on the way
        ///     to it was null" are different measurements and a caller must not have to guess which
        ///     one it got.
        /// </summary>
        internal static Read ReadPath(object root, string path, Type pinnedType)
        {
            var result = new Read();
            if (root == null) { result.Error = "nothing to read from"; return result; }
            if (string.IsNullOrEmpty(path)) { result.Error = "empty member name"; return result; }

            object current = root;
            var segments = path.Split('.');
            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i].Trim();
                if (segment.Length == 0) { result.Error = "empty segment in path '" + path + "'"; return result; }

                int index = -1;
                int bracket = segment.IndexOf('[');
                if (bracket > 0 && segment.EndsWith("]", StringComparison.Ordinal))
                {
                    int.TryParse(segment.Substring(bracket + 1, segment.Length - bracket - 2),
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out index);
                    segment = segment.Substring(0, bracket);
                }

                if (IsNull(current))
                {
                    result.Error = "'" + string.Join(".", segments, 0, i) + "' is null, so '" + path +
                                   "' cannot be read";
                    return result;
                }

                var owner = current;
                var type = owner.GetType();
                // The pin only applies to the first segment: later segments are resolved against
                // whatever the previous one actually produced, which is the only type they can be on.
                var member = FindMember(type, segment, i == 0 ? pinnedType : null);
                if (member == null)
                {
                    result.Error = "no instance field or property named '" + segment + "' on " + type.Name +
                                   " (searched " + TypeChain(type) + "). " +
                                   "GET /thing/members lists every member this object has.";
                    return result;
                }

                object value;
                try { value = member.Read(owner); }
                catch (Exception ex)
                {
                    result.Member = member;
                    result.Error = "reading '" + segment + "' on " + type.Name + " threw: " + ex.Message;
                    return result;
                }

                if (index >= 0)
                {
                    var list = value as IList;
                    if (list == null)
                    {
                        result.Member = member;
                        result.Error = "'" + segment + "' is " + (value == null ? "null" : value.GetType().Name) +
                                       ", which is not an indexable list, so [" + index + "] cannot be applied";
                        return result;
                    }
                    if (index >= list.Count)
                    {
                        result.Member = member;
                        result.Error = "index " + index + " is out of range for '" + segment +
                                       "' (count " + list.Count + ")";
                        return result;
                    }
                    try { value = list[index]; }
                    catch (Exception ex)
                    {
                        result.Member = member;
                        result.Error = "indexing '" + segment + "[" + index + "]' threw: " + ex.Message;
                        return result;
                    }
                }

                result.Member = member;
                result.Owner = owner;
                current = value;
            }

            result.Value = current;
            result.Ok = true;
            return result;
        }

        // ---- rendering -------------------------------------------------------

        /// <summary>
        ///     Unity's answer to "is this null", not C#'s. <c>UnityEngine.Object</c> overloads
        ///     <c>==</c> so a destroyed object is a live reference the game treats as null; a
        ///     reflective read that only checks <c>value == null</c> reports a Material that no
        ///     longer exists as a perfectly good Material.
        /// </summary>
        internal static bool IsNull(object value)
        {
            if (value == null) return true;
            var uo = value as UnityEngine.Object;
            return uo != null && uo == null;
        }

        private static bool IsDestroyedUnityObject(object value)
        {
            if (value == null) return false;
            var uo = value as UnityEngine.Object;
            return !ReferenceEquals(uo, null) && uo == null;
        }

        /// <summary>
        ///     Writes a value into a response object as <c>value</c> (a string, always) plus
        ///     <c>valueJson</c> (a native JSON encoding where one is unambiguous) plus the type and
        ///     null bookkeeping.
        ///
        ///     Both spellings, because neither alone is enough. <c>Color.ToString()</c> rounds to
        ///     three decimals and formats in the current culture, so it is unusable as an assertion;
        ///     a raw number is unreadable in a log. <c>valueJson</c> is the one to compare against,
        ///     <c>value</c> is the one to read.
        /// </summary>
        internal static void Describe(Json.Obj o, object value, bool expand, int expandLimit, string key)
        {
            bool destroyed = IsDestroyedUnityObject(value);
            bool isNull = value == null || destroyed;
            o.Bit("isNull", isNull);
            if (destroyed)
                o.Bit("destroyed", true).Str("destroyedNote",
                    "this is a UnityEngine.Object that has been destroyed. It is not a C# null, but " +
                    "every == null test in the game says it is.");

            if (isNull)
            {
                o.Str("value", null);
                o.Raw("valueJson", "null");
                o.Str("valueType", value == null ? null : value.GetType().Name);
                return;
            }

            var type = value.GetType();
            o.Str("valueType", type.Name);

            if (value is bool b)
            {
                // "True"/"False", which is what ToString() has always produced here. /reflect used
                // to render every value through ToString and existing callers compare against that
                // spelling; valueJson carries the lowercase JSON form for anything parsing.
                o.Str("value", b ? "True" : "False").Raw("valueJson", Json.Bool(b));
                return;
            }
            if (type.IsEnum)
            {
                string name = value.ToString();
                long numeric = 0;
                try { numeric = Convert.ToInt64(value, CultureInfo.InvariantCulture); } catch { }
                o.Str("value", name).Raw("valueJson", numeric.ToString(CultureInfo.InvariantCulture))
                 .Str("enumName", name);
                return;
            }
            if (value is float f) { o.Str("value", Json.Num(f)).Raw("valueJson", Json.Num(f)); return; }
            if (value is double d) { o.Str("value", Json.Num(d)).Raw("valueJson", Json.Num(d)); return; }
            if (value is decimal dec)
            {
                string s = dec.ToString(CultureInfo.InvariantCulture);
                o.Str("value", s).Raw("valueJson", s);
                return;
            }
            if (IsIntegral(type))
            {
                string s = Convert.ToString(value, CultureInfo.InvariantCulture);
                // 'value' is the exact one. valueJson is a bare JSON number, and a reader that
                // parses through double (this plugin's own does, and so does JavaScript) loses
                // precision above 2^53. A ClientId is in that territory; compare the string.
                o.Str("value", s).Raw("valueJson", s);
                return;
            }
            if (value is string str)
            {
                o.Str("value", Truncate(str)).Raw("valueJson", Json.Escape(Truncate(str)));
                return;
            }
            if (value is Vector3 v3)
            {
                o.Str("value", Triple(v3.x, v3.y, v3.z))
                 .Raw("valueJson", "[" + Json.Num(v3.x) + "," + Json.Num(v3.y) + "," + Json.Num(v3.z) + "]");
                return;
            }
            if (value is Vector2 v2)
            {
                o.Str("value", Json.Num(v2.x) + "," + Json.Num(v2.y))
                 .Raw("valueJson", "[" + Json.Num(v2.x) + "," + Json.Num(v2.y) + "]");
                return;
            }
            if (value is Vector4 v4)
            {
                o.Str("value", Json.Num(v4.x) + "," + Json.Num(v4.y) + "," + Json.Num(v4.z) + "," + Json.Num(v4.w))
                 .Raw("valueJson", "[" + Json.Num(v4.x) + "," + Json.Num(v4.y) + "," + Json.Num(v4.z) + "," + Json.Num(v4.w) + "]");
                return;
            }
            if (value is Quaternion q)
            {
                o.Str("value", Json.Num(q.x) + "," + Json.Num(q.y) + "," + Json.Num(q.z) + "," + Json.Num(q.w))
                 .Raw("valueJson", "[" + Json.Num(q.x) + "," + Json.Num(q.y) + "," + Json.Num(q.z) + "," + Json.Num(q.w) + "]");
                return;
            }
            if (value is Color c)
            {
                // The exact reason this method exists. EmissionColor is a Color, and a paint
                // assertion turns on whether it is (1,1,1,1) or (0,0,0,0) exactly.
                o.Str("value", Json.Num(c.r) + "," + Json.Num(c.g) + "," + Json.Num(c.b) + "," + Json.Num(c.a))
                 .Raw("valueJson", "[" + Json.Num(c.r) + "," + Json.Num(c.g) + "," + Json.Num(c.b) + "," + Json.Num(c.a) + "]");
                return;
            }
            if (value is Color32 c32)
            {
                Color asColor = c32;
                o.Str("value", Json.Num(asColor.r) + "," + Json.Num(asColor.g) + "," + Json.Num(asColor.b) + "," + Json.Num(asColor.a))
                 .Raw("valueJson", "[" + c32.r + "," + c32.g + "," + c32.b + "," + c32.a + "]");
                return;
            }

            var thing = value as Thing;
            if (thing != null)
            {
                long refId = 0;
                string prefabName = null, displayName = null;
                try { refId = thing.ReferenceId; } catch { }
                try { prefabName = thing.PrefabName; } catch { }
                try { displayName = thing.DisplayName; } catch { }
                o.Str("value", (prefabName ?? type.Name) + " #" + refId.ToString(CultureInfo.InvariantCulture))
                 .Raw("valueJson", Json.Escape(refId.ToString(CultureInfo.InvariantCulture)))
                 .Str("referenceId", refId.ToString(CultureInfo.InvariantCulture))
                 .Str("prefabName", prefabName)
                 .Str("displayName", displayName)
                 .Str("chainWith", "GET /thing?refId=" + refId.ToString(CultureInfo.InvariantCulture));
                return;
            }

            var dict = value as IDictionary;
            if (dict != null)
            {
                // The case a static read could not answer at all: a mod-side registry keyed by
                // reference id came back as its type name, so "is Thing N in this dictionary" was
                // unanswerable without a bespoke script.
                o.Int("count", dict.Count);
                o.Str("value", type.Name + " count=" + dict.Count.ToString(CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(key)) DescribeKeyLookup(o, dict, key);
                if (expand) o.Raw("entries", DictEntries(dict, expandLimit));
                return;
            }

            var collection = value as ICollection;
            if (collection != null)
            {
                o.Int("count", collection.Count);
                o.Str("value", type.Name + " count=" + collection.Count.ToString(CultureInfo.InvariantCulture));
                if (expand) o.Raw("items", Items(collection, expandLimit));
                return;
            }

            string text;
            try { text = Truncate(value.ToString()); } catch (Exception ex) { text = "(ToString threw: " + ex.Message + ")"; }
            o.Str("value", text).Raw("valueJson", Json.Escape(text));
        }

        /// <summary>
        ///     "Does this registry contain Thing N", answered without dumping the registry. A key is
        ///     matched by its invariant string form, so a dictionary keyed by long, int, ulong or
        ///     string all answer the same request.
        /// </summary>
        private static void DescribeKeyLookup(Json.Obj o, IDictionary dict, string key)
        {
            bool found = false;
            object hit = null;
            try
            {
                foreach (DictionaryEntry entry in dict)
                {
                    string k;
                    try { k = Convert.ToString(entry.Key, CultureInfo.InvariantCulture); } catch { continue; }
                    if (!string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) continue;
                    found = true;
                    hit = entry.Value;
                    break;
                }
            }
            catch (Exception ex)
            {
                o.Str("keyLookupError", ex.Message);
                return;
            }

            o.Str("key", key).Bit("containsKey", found);
            if (!found) return;
            var inner = new Json.Obj();
            Describe(inner, hit, false, 0, null);
            o.Raw("keyValue", inner.ToString());
        }

        private static string DictEntries(IDictionary dict, int limit)
        {
            var rows = new List<string>();
            try
            {
                foreach (DictionaryEntry entry in dict)
                {
                    if (rows.Count >= limit) break;
                    var row = new Json.Obj();
                    try { row.Str("key", Truncate(Convert.ToString(entry.Key, CultureInfo.InvariantCulture))); }
                    catch { row.Str("key", null); }
                    Describe(row, entry.Value, false, 0, null);
                    rows.Add(row.ToString());
                }
            }
            catch { }
            return "[" + string.Join(",", rows.ToArray()) + "]";
        }

        private static string Items(ICollection collection, int limit)
        {
            var rows = new List<string>();
            try
            {
                foreach (var item in collection)
                {
                    if (rows.Count >= limit) break;
                    var row = new Json.Obj();
                    Describe(row, item, false, 0, null);
                    rows.Add(row.ToString());
                }
            }
            catch { }
            return "[" + string.Join(",", rows.ToArray()) + "]";
        }

        private static bool IsIntegral(Type t)
        {
            return t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort) ||
                   t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong) ||
                   t == typeof(char);
        }

        private static string Triple(float x, float y, float z)
            => Json.Num(x) + "," + Json.Num(y) + "," + Json.Num(z);

        internal static string Truncate(string s)
            => s != null && s.Length > 400 ? s.Substring(0, 400) + "..." : s;

        // ---- the prefab comparison -------------------------------------------

        /// <summary>
        ///     The same member read off the Thing's own prefab, which is the untouched template.
        ///
        ///     This is the general answer to a specific way a reflective read lies. A live run read
        ///     <c>Thing.EmissionColor</c> to decide whether an object was glowing, and every object
        ///     that had never been painted read (1,1,1,1) because the field's initialiser is
        ///     <c>Color.white</c>. The value was correct and the conclusion was wrong. Nothing about
        ///     the field itself says so, and no "is this the type default" test catches it, because
        ///     white is not the default for Color.
        ///
        ///     Comparing against the prefab does catch it, and catches the same class of mistake for
        ///     any field on any type, including a mod's: a value equal to the prefab's is
        ///     indistinguishable from never having been set on this instance.
        /// </summary>
        internal static Thing PrefabOf(Thing thing)
        {
            if (thing == null) return null;
            try
            {
                string name = thing.PrefabName;
                if (string.IsNullOrEmpty(name)) return null;
                var prefab = Prefab.Find(name);
                // A prefab compared against itself is not a measurement.
                return ReferenceEquals(prefab, thing) ? null : prefab;
            }
            catch { return null; }
        }

        /// <summary>
        ///     Compares a live value against the prefab's, by their rendered form. Rendering is what
        ///     the caller sees, so comparing renderings is what makes <c>matchesPrefab</c> mean the
        ///     same thing as "these two rows look identical".
        /// </summary>
        internal static string RenderForCompare(object value)
        {
            var o = new Json.Obj();
            Describe(o, value, false, 0, null);
            return o.ToString();
        }

        // ---- where a Thing lives ---------------------------------------------

        /// <summary>
        ///     Whether a Thing is in a slot, whose slot, and on which character, answered from THIS
        ///     process's own view of the world.
        ///
        ///     The field that makes it an assertion rather than an observation is
        ///     <c>authoritative</c>: <c>GameManager.RunSimulation</c> is true on a listen host, a
        ///     dedicated server and single player, and false on a joined client. A joiner claiming
        ///     an item is in its hand proves only that the joiner thinks so; the same answer read on
        ///     the authority is the server's own record, and that is what separates a replicated
        ///     change from a client-local one. A live run left exactly this question open, because
        ///     the host could see the Thing and had no way to report what it was parented to.
        ///
        ///     <c>ParentSlot</c> lives on <c>DynamicThing</c>, so a Structure reports
        ///     <c>inSlot:false</c> with a note rather than an empty block: a wall is not "on the
        ///     ground", it is a thing that cannot be in a slot at all.
        /// </summary>
        internal static string LocationJson(Thing thing)
        {
            var o = new Json.Obj();
            if (thing == null) return "null";

            bool authoritative = false;
            try { authoritative = Assets.Scripts.GameManager.RunSimulation; } catch { }
            o.Bit("authoritative", authoritative);

            var dynamicThing = thing as DynamicThing;
            if (dynamicThing == null)
            {
                o.Bit("inSlot", false).Bit("canBeInSlot", false)
                 .Str("whereIs", thing.GetType().Name + " cannot be in a slot; only a DynamicThing can");
                try { o.Vec("position", thing.ThingTransformPosition); } catch { }
                return o.ToString();
            }
            o.Bit("canBeInSlot", true);

            Slot slot = null;
            try { slot = dynamicThing.ParentSlot; } catch { }

            if (slot == null)
            {
                o.Bit("inSlot", false).Bit("onGround", true);
                try { o.Vec("position", thing.ThingTransformPosition); } catch { }
                string where = "loose in the world";
                try { where += " at " + Triple(thing.ThingTransformPosition.x, thing.ThingTransformPosition.y,
                                               thing.ThingTransformPosition.z); }
                catch { }
                o.Str("whereIs", where);
                return o.ToString();
            }

            o.Bit("inSlot", true).Bit("onGround", false);
            try { o.Int("slotIndex", slot.SlotIndex); } catch { }
            try { o.Str("slotKey", slot.StringKey); } catch { }
            try { o.Str("slotType", slot.Type.ToString()); } catch { }
            try { o.Bit("isHandSlot", slot.IsHandSlot); } catch { }

            Thing parent = null;
            try { parent = slot.Parent; } catch { }
            string parentName = null, handSide = null;
            long parentId = 0;

            if (parent != null)
            {
                try { parentId = parent.ReferenceId; } catch { }
                try { parentName = parent.DisplayName; } catch { }
                o.Int("parentId", parentId);
                o.Str("parentType", parent.GetType().Name);
                o.Str("parentName", parentName);
                try { o.Str("parentPrefab", parent.PrefabName); } catch { }

                var human = parent as Human;
                if (human != null)
                {
                    try { o.Str("parentClientId", human.OwnerClientId.ToString(CultureInfo.InvariantCulture)); } catch { }
                    try { if (ReferenceEquals(slot, human.LeftHandSlot)) handSide = "left"; } catch { }
                    try { if (ReferenceEquals(slot, human.RightHandSlot)) handSide = "right"; } catch { }
                    o.Str("handSide", handSide);
                    bool isLocal = false;
                    try { isLocal = ReferenceEquals(human, Human.LocalHuman); } catch { }
                    o.Bit("parentIsLocalPlayer", isLocal);
                    if (isLocal)
                    {
                        // Only meaningful for the character THIS process owns: the active hand is
                        // InventoryManager state, which is client-local and never replicated.
                        try { o.Bit("isActiveHand", ReferenceEquals(slot, InventoryManager.ActiveHandSlot)); }
                        catch { }
                    }
                    else
                    {
                        o.Str("activeHandNote",
                            "which of that character's hands is ACTIVE is client-local UI state and is " +
                            "never replicated, so this process cannot answer it. Read GET /inventory on " +
                            "their own instance.");
                    }
                }
            }

            // One level up and the root, so a can inside a gun inside a hand reads as such rather
            // than as "in a gun".
            AppendChain(o, dynamicThing);

            var summary = new System.Text.StringBuilder();
            summary.Append("in ");
            summary.Append(parentName ?? (parent == null ? "an unknown parent" : parent.GetType().Name));
            summary.Append("'s ");
            string slotLabel = null;
            try { slotLabel = slot.StringKey; } catch { }
            if (string.IsNullOrEmpty(slotLabel))
            {
                int idx = -1;
                try { idx = slot.SlotIndex; } catch { }
                slotLabel = "slot #" + idx.ToString(CultureInfo.InvariantCulture);
            }
            summary.Append(slotLabel).Append(" slot");
            if (handSide != null) summary.Append(" (").Append(handSide).Append(" hand)");
            if (parentId != 0) summary.Append(", parent ref ").Append(parentId.ToString(CultureInfo.InvariantCulture));
            summary.Append(authoritative ? ", as the SIMULATION AUTHORITY sees it"
                                         : ", as a NON-AUTHORITATIVE client sees it");
            o.Str("whereIs", summary.ToString());
            return o.ToString();
        }

        /// <summary>
        ///     Walks the parent-slot chain up to the root, capped so a corrupt cycle cannot spin.
        /// </summary>
        private static void AppendChain(Json.Obj o, DynamicThing thing)
        {
            var chain = new List<string>();
            Thing root = thing;
            var walker = thing;
            for (int depth = 0; depth < 8 && walker != null; depth++)
            {
                Slot slot = null;
                try { slot = walker.ParentSlot; } catch { }
                if (slot == null) break;
                Thing parent = null;
                try { parent = slot.Parent; } catch { }
                if (parent == null) break;

                long id = 0;
                string prefab = null;
                try { id = parent.ReferenceId; } catch { }
                try { prefab = parent.PrefabName; } catch { }
                chain.Add(new Json.Obj()
                    .Int("referenceId", id)
                    .Str("type", parent.GetType().Name)
                    .Str("prefabName", prefab)
                    .Str("slotKey", SafeSlotKey(slot))
                    .ToString());

                root = parent;
                walker = parent as DynamicThing;
            }

            o.Raw("chain", "[" + string.Join(",", chain.ToArray()) + "]");
            if (root != null && !ReferenceEquals(root, thing))
            {
                try { o.Int("rootId", root.ReferenceId); } catch { }
                o.Str("rootType", root.GetType().Name);
                try { o.Str("rootName", root.DisplayName); } catch { }
            }
        }

        private static string SafeSlotKey(Slot slot)
        {
            try { return slot.StringKey; } catch { return null; }
        }

        // ---- identity --------------------------------------------------------

        /// <summary>
        ///     The identity block every Thing row leads with, so a value is attributable to an object
        ///     rather than to a reference id somebody has to look up separately.
        /// </summary>
        internal static void DescribeThing(Json.Obj o, Thing thing)
        {
            try { o.Int("referenceId", thing.ReferenceId); } catch { }
            try { o.Str("prefabName", thing.PrefabName); } catch { }
            o.Str("type", thing.GetType().Name);
            o.Str("typeFullName", thing.GetType().FullName);
            o.Str("assembly", ConfigAccess.AsmName(thing.GetType()));
            try { o.Str("displayName", thing.DisplayName); } catch { }
            try { o.Vec("position", thing.ThingTransformPosition); } catch { }
            try { o.Bit("paintable", thing.IsPaintable); } catch { }
            try
            {
                var swatch = thing.CustomColor;
                o.Int("customColorIndex", swatch == null ? -1 : swatch.Index);
            }
            catch { }
        }

        // ---- member enumeration ----------------------------------------------

        /// <summary>
        ///     Every instance member of a type, with its declaring type and, optionally, its current
        ///     value. The diagnostic of last resort, mirroring <c>/reflect/members</c> for statics.
        ///
        ///     <paramref name="readValues"/> exists because reading is not free of consequences here
        ///     the way it is for a static dump: a property getter is arbitrary game code and can
        ///     allocate, lazily construct, or throw. Throwing is caught and reported per member;
        ///     side effects are not preventable, so a caller that only wants the shape of a type
        ///     passes false.
        /// </summary>
        internal static string MembersJson(Type type, object target, string contains, int limit, bool readValues)
        {
            var rows = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (var t = type; t != null; t = t.BaseType)
            {
                foreach (var p in Fields(t, contains))
                {
                    if (!seen.Add("f:" + p.Name)) continue;
                    if (rows.Count >= limit) break;
                    var row = new Json.Obj()
                        .Str("kind", "field")
                        .Str("name", p.Name)
                        .Str("declaredBy", t.Name)
                        .Str("declaredType", p.FieldType.FullName)
                        .Bit("public", p.IsPublic);
                    if (readValues && target != null)
                    {
                        object v = null;
                        string err = null;
                        try { v = p.GetValue(target); } catch (Exception ex) { err = ex.Message; }
                        if (err != null) row.Str("error", err);
                        else Describe(row, v, false, 0, null);
                    }
                    rows.Add(row.ToString());
                }

                foreach (var p in Properties(t, contains))
                {
                    if (!seen.Add("p:" + p.Name)) continue;
                    if (rows.Count >= limit) break;
                    var row = new Json.Obj()
                        .Str("kind", "property")
                        .Str("name", p.Name)
                        .Str("declaredBy", t.Name)
                        .Str("declaredType", p.PropertyType.FullName)
                        .Bit("canWrite", p.CanWrite);
                    if (readValues && target != null)
                    {
                        object v = null;
                        string err = null;
                        try { v = p.GetValue(target, null); } catch (Exception ex) { err = Unwrap(ex); }
                        if (err != null) row.Str("error", err);
                        else Describe(row, v, false, 0, null);
                    }
                    rows.Add(row.ToString());
                }

                if (rows.Count >= limit) break;
            }

            return "[" + string.Join(",", rows.ToArray()) + "]";
        }

        private static IEnumerable<FieldInfo> Fields(Type t, string contains)
        {
            FieldInfo[] all;
            try { all = t.GetFields(Declared); } catch { yield break; }
            foreach (var f in all)
            {
                if (!Matches(f.Name, contains)) continue;
                yield return f;
            }
        }

        private static IEnumerable<PropertyInfo> Properties(Type t, string contains)
        {
            PropertyInfo[] all;
            try { all = t.GetProperties(Declared); } catch { yield break; }
            foreach (var p in all)
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                if (!Matches(p.Name, contains)) continue;
                yield return p;
            }
        }

        private static bool Matches(string name, string contains)
            => string.IsNullOrEmpty(contains) ||
               name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        ///     A reflected getter that throws arrives wrapped, and the wrapper's message says
        ///     nothing. The inner one is the answer.
        /// </summary>
        private static string Unwrap(Exception ex)
        {
            var inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            return inner.GetType().Name + ": " + inner.Message;
        }
    }
}
