using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;

namespace InspectorPlus
{
    /// <summary>
    /// Walks arbitrary object graphs via reflection and serializes them to JSON.
    /// Generic across any Unity game: the only types this file knows about are
    /// UnityEngine primitives (Object, Component, Vector2/3, Quaternion, Color)
    /// and plain reflection. No game-specific type names appear here.
    /// </summary>
    internal static class ObjectWalker
    {
        private const long DefaultMaxBytes = 50L * 1024 * 1024;
        private const int DefaultMaxTopLevel = 10000;
        private const int DefaultMaxNested = 100000;
        private const int MaxEnumerableItems = 50;

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => obj == null ? 0 : RuntimeHelpers.GetHashCode(obj);
        }

        private sealed class WalkContext
        {
            public StringBuilder Sb;
            public HashSet<object> Chain = new HashSet<object>(ReferenceComparer.Instance);
            public long MaxBytes = DefaultMaxBytes;
            public int MaxTopLevel = DefaultMaxTopLevel;
            public int MaxNested = DefaultMaxNested;
            public int NestedCount;
            public bool Truncated;
            public bool IncludePrivate;
            public List<string> FieldFilter;
            public int MaxDepth;

            public bool ShouldStop()
            {
                if (Truncated) return true;
                if (Sb.Length >= MaxBytes) { Truncated = true; return true; }
                return false;
            }
        }

        public static string Walk(SnapshotRequest request)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"timestamp\": \"").Append(DateTime.Now.ToString("o", CultureInfo.InvariantCulture)).Append("\",\n");
            sb.Append("  \"frame\": ").Append(Time.frameCount).Append(",\n");
            sb.Append("  \"gameTime\": ").Append(Time.time.ToString("F2", CultureInfo.InvariantCulture)).Append(",\n");

            var ctx = new WalkContext
            {
                Sb = sb,
                MaxTopLevel = (request != null && request.MaxMonoBehaviours > 0) ? request.MaxMonoBehaviours : DefaultMaxTopLevel,
                IncludePrivate = request?.IncludePrivate ?? false,
                FieldFilter = request?.Fields,
                MaxDepth = request?.MaxDepth ?? 3,
            };

            string error = null;
            sb.Append("  \"objects\": [");
            try
            {
                if (request == null || request.Types == null || request.Types.Count == 0)
                    WalkAllGameObjects(ctx);
                else
                    WalkRequestedTypes(ctx, request);
            }
            catch (Exception ex)
            {
                error = ex.ToString();
                InspectorPlusPlugin.Log.LogError($"Walk failed: {ex}");
            }
            sb.Append("\n  ]");

            if (ctx.Truncated)
                sb.Append(",\n  \"_truncated\": true");
            if (error != null)
                sb.Append(",\n  \"error\": \"").Append(EscapeJson(error)).Append("\"");

            sb.Append("\n}\n");
            return sb.ToString();
        }

        private static void WalkAllGameObjects(WalkContext ctx)
        {
            MonoBehaviour[] allObjects;
            try { allObjects = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(); }
            catch (Exception ex)
            {
                InspectorPlusPlugin.Log.LogError($"FindObjectsOfType<MonoBehaviour> failed: {ex.Message}");
                return;
            }
            if (allObjects == null) return;

            bool first = true;
            int emitted = 0;
            foreach (var obj in allObjects)
            {
                if (obj == null) continue;
                if (obj is UnityEngine.Object uo && !uo) continue;
                if (emitted >= ctx.MaxTopLevel) { ctx.Truncated = true; break; }
                if (ctx.ShouldStop()) break;

                if (!first) ctx.Sb.Append(",");
                first = false;
                ctx.Sb.Append("\n");
                SerializeTopLevelObject(ctx, obj, 4);
                emitted++;
            }
            if (!first) ctx.Sb.Append("\n");
        }

        private static void WalkRequestedTypes(WalkContext ctx, SnapshotRequest request)
        {
            bool first = true;
            int emitted = 0;
            foreach (var typeName in request.Types)
            {
                var type = FindType(typeName);
                if (type == null)
                {
                    InspectorPlusPlugin.Log.LogWarning($"Type not found: {typeName}");
                    continue;
                }

                var instances = ResolveInstances(type);
                if (instances == null) continue;

                foreach (var instance in instances)
                {
                    if (instance == null) continue;
                    if (instance is UnityEngine.Object uo && !uo) continue;
                    if (emitted >= ctx.MaxTopLevel) { ctx.Truncated = true; break; }
                    if (ctx.ShouldStop()) break;

                    if (!first) ctx.Sb.Append(",");
                    first = false;
                    ctx.Sb.Append("\n");
                    SerializeTopLevelObject(ctx, instance, 4);
                    emitted++;
                }
                if (ctx.Truncated || ctx.ShouldStop()) break;
            }
            if (!first) ctx.Sb.Append("\n");
        }

        // Produces instances of `type` for the walker to serialize.
        // Unity-derived types go through FindObjectsOfType; everything else
        // falls back to a reflection scan over live MonoBehaviours and static
        // members of loaded types. Never returns null when a secondary path
        // is tried; returns an empty collection if nothing matches.
        private static IEnumerable<object> ResolveInstances(Type type)
        {
            if (typeof(UnityEngine.Object).IsAssignableFrom(type))
            {
                UnityEngine.Object[] found = null;
                try { found = UnityEngine.Object.FindObjectsOfType(type); }
                catch (Exception ex)
                {
                    InspectorPlusPlugin.Log.LogWarning(
                        $"FindObjectsOfType({type.FullName}) threw {ex.GetType().Name}: {ex.Message}. Falling back to reflection scan.");
                }
                if (found == null)
                {
                    InspectorPlusPlugin.Log.LogWarning(
                        $"FindObjectsOfType({type.FullName}) returned null. Falling back to reflection scan.");
                    return FallbackLookup(type);
                }
                var list = new List<object>(found.Length);
                foreach (var o in found) list.Add(o);
                return list;
            }

            InspectorPlusPlugin.Log.LogWarning(
                $"Type {type.FullName} does not inherit from UnityEngine.Object; FindObjectsOfType cannot locate it. Scanning live MonoBehaviours and static members for matching instances.");
            return FallbackLookup(type);
        }

        // Reflection-based instance lookup for types that FindObjectsOfType can't reach.
        // Walks every live MonoBehaviour's instance fields and properties, and every loaded
        // type's static fields and properties, collecting runtime values assignable to `target`.
        // Deduplicates by reference identity.
        private static IEnumerable<object> FallbackLookup(Type target)
        {
            var results = new HashSet<object>(ReferenceComparer.Instance);
            var instanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var staticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

            MonoBehaviour[] mbs;
            try { mbs = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(); }
            catch { mbs = null; }
            if (mbs != null)
            {
                foreach (var mb in mbs)
                {
                    if (mb == null) continue;
                    if (mb is UnityEngine.Object uo && !uo) continue;
                    CollectMatchingMembers(mb, mb.GetType(), instanceFlags, target, results);
                }
            }

            Assembly[] asms;
            try { asms = AppDomain.CurrentDomain.GetAssemblies(); }
            catch { asms = new Assembly[0]; }

            foreach (var asm in asms)
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException rtle) { types = Array.FindAll(rtle.Types ?? new Type[0], t => t != null); }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t == null) continue;
                    CollectMatchingMembers(null, t, staticFlags, target, results);
                }
            }

            return results;
        }

        private static void CollectMatchingMembers(object owner, Type declaringType, BindingFlags flags, Type target, HashSet<object> results)
        {
            FieldInfo[] fields;
            try { fields = declaringType.GetFields(flags); } catch { fields = new FieldInfo[0]; }
            foreach (var f in fields)
            {
                if (!target.IsAssignableFrom(f.FieldType) && !f.FieldType.IsAssignableFrom(target)) continue;
                var v = SafeGetValue(() => f.GetValue(owner));
                if (v != null && target.IsInstanceOfType(v)) results.Add(v);
            }

            PropertyInfo[] props;
            try { props = declaringType.GetProperties(flags); } catch { props = new PropertyInfo[0]; }
            foreach (var p in props)
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                if (!target.IsAssignableFrom(p.PropertyType) && !p.PropertyType.IsAssignableFrom(target)) continue;
                var v = SafeGetValue(() => p.GetValue(owner));
                if (v != null && target.IsInstanceOfType(v)) results.Add(v);
            }
        }

        private static void SerializeTopLevelObject(WalkContext ctx, object obj, int indent)
        {
            var pad = new string(' ', indent);
            var type = obj.GetType();
            var sb = ctx.Sb;

            sb.Append(pad).Append("{\n");
            sb.Append(pad).Append("  \"_type\": \"").Append(EscapeJson(type.FullName)).Append("\",\n");
            sb.Append(pad).Append("  \"_name\": \"").Append(EscapeJson(SafeUnityName(obj))).Append("\",\n");
            if (obj is UnityEngine.Object uobj)
                sb.Append(pad).Append("  \"_instanceId\": ").Append(SafeInstanceId(uobj)).Append(",\n");
            if (obj is Component comp)
            {
                sb.Append(pad).Append("  \"_gameObject\": \"").Append(EscapeJson(SafeGameObjectName(comp))).Append("\",\n");
                sb.Append(pad).Append("  \"_active\": ").Append(SafeActive(comp)).Append(",\n");
                sb.Append(pad).Append("  \"_position\": ").Append(SafePosition(comp)).Append(",\n");
            }
            sb.Append(pad).Append("  \"fields\": {");

            ctx.Chain.Add(obj);
            bool firstMember = true;
            try
            {
                SerializeMembers(ctx, obj, type, ctx.MaxDepth - 1, indent + 4, applyFieldFilter: true, ref firstMember);
            }
            finally { ctx.Chain.Remove(obj); }

            if (!firstMember) sb.Append("\n").Append(pad).Append("  ");
            sb.Append("}\n");
            sb.Append(pad).Append("}");
        }

        // Writes out `obj`'s reflected fields and properties in comma-separated JSON.
        // If firstMember starts true, the first written member has no leading comma; otherwise
        // each written member begins with a comma. This lets SerializeTopLevelObject (caller has
        // just opened "fields": {) and the inline plain-object branch (caller just wrote "_type")
        // share the same implementation.
        private static void SerializeMembers(WalkContext ctx, object obj, Type type, int remainingDepth, int indent, bool applyFieldFilter, ref bool firstMember)
        {
            var pad = new string(' ', indent);
            var flags = BindingFlags.Public | BindingFlags.Instance;
            if (ctx.IncludePrivate) flags |= BindingFlags.NonPublic;

            FieldInfo[] fields;
            PropertyInfo[] props;
            try { fields = type.GetFields(flags); } catch { fields = new FieldInfo[0]; }
            try { props = type.GetProperties(flags); } catch { props = new PropertyInfo[0]; }

            foreach (var f in fields)
            {
                if (applyFieldFilter && ctx.FieldFilter != null && ctx.FieldFilter.Count > 0 && !ctx.FieldFilter.Contains(f.Name)) continue;
                if (ShouldSkipField(f.Name)) continue;
                if (ctx.ShouldStop()) return;

                var v = SafeGetValue(() => f.GetValue(obj));
                if (!firstMember) ctx.Sb.Append(",");
                firstMember = false;
                ctx.Sb.Append("\n").Append(pad).Append("\"").Append(EscapeJson(f.Name)).Append("\": ");
                SerializeValue(ctx, v, remainingDepth, indent);
            }

            foreach (var p in props)
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                if (applyFieldFilter && ctx.FieldFilter != null && ctx.FieldFilter.Count > 0 && !ctx.FieldFilter.Contains(p.Name)) continue;
                if (ShouldSkipField(p.Name)) continue;
                if (ctx.ShouldStop()) return;

                var v = SafeGetValue(() => p.GetValue(obj));
                if (!firstMember) ctx.Sb.Append(",");
                firstMember = false;
                ctx.Sb.Append("\n").Append(pad).Append("\"").Append(EscapeJson(p.Name)).Append("\": ");
                SerializeValue(ctx, v, remainingDepth, indent);
            }
        }

        private static void SerializeValue(WalkContext ctx, object value, int remainingDepth, int indent)
        {
            var sb = ctx.Sb;
            if (value == null) { sb.Append("null"); return; }

            // Unity fake-null: managed wrapper survives destruction. Guard before any dereference.
            if (value is UnityEngine.Object deadCheck && !deadCheck)
            {
                sb.Append("\"(destroyed)\"");
                return;
            }

            if (ctx.ShouldStop())
            {
                sb.Append("\"(truncated)\"");
                return;
            }

            try
            {
                var type = value.GetType();

                if (value is string s) { sb.Append("\"").Append(EscapeJson(s)).Append("\""); return; }
                if (value is bool bv) { sb.Append(bv ? "true" : "false"); return; }
                if (type.IsPrimitive || type == typeof(decimal))
                {
                    sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    return;
                }
                if (type.IsEnum) { sb.Append("\"").Append(EscapeJson(value.ToString())).Append("\""); return; }
                if (value is Vector3 v3) { sb.AppendFormat(CultureInfo.InvariantCulture, "[{0:F3}, {1:F3}, {2:F3}]", v3.x, v3.y, v3.z); return; }
                if (value is Vector2 v2) { sb.AppendFormat(CultureInfo.InvariantCulture, "[{0:F3}, {1:F3}]", v2.x, v2.y); return; }
                if (value is Quaternion q) { sb.AppendFormat(CultureInfo.InvariantCulture, "[{0:F3}, {1:F3}, {2:F3}, {3:F3}]", q.x, q.y, q.z, q.w); return; }
                if (value is Color c) { sb.AppendFormat(CultureInfo.InvariantCulture, "[{0:F3}, {1:F3}, {2:F3}, {3:F3}]", c.r, c.g, c.b, c.a); return; }

                if (value is UnityEngine.Object uobj)
                {
                    // Don't recurse into Unity objects; print a short descriptor.
                    sb.Append("\"(").Append(uobj.GetType().Name).Append(") ").Append(EscapeJson(SafeUnityName(uobj))).Append("\"");
                    return;
                }

                if (value is IEnumerable enumerable)
                {
                    if (remainingDepth <= 0) { sb.Append("\"[...]\""); return; }
                    if (!ctx.Chain.Add(value)) { sb.Append("\"(cycle)\""); return; }
                    try
                    {
                        sb.Append("[");
                        bool first = true;
                        int count = 0;
                        foreach (var item in enumerable)
                        {
                            if (count >= MaxEnumerableItems) { sb.Append(", \"...(truncated)\""); break; }
                            if (ctx.ShouldStop()) { sb.Append(", \"(truncated)\""); break; }
                            if (!first) sb.Append(", ");
                            first = false;
                            SerializeValue(ctx, item, remainingDepth - 1, indent);
                            count++;
                        }
                        sb.Append("]");
                    }
                    finally { ctx.Chain.Remove(value); }
                    return;
                }

                // Plain CLR object. Inline-expand if depth allows, else stringify the type name.
                if (remainingDepth <= 0)
                {
                    sb.Append("\"(").Append(EscapeJson(type.Name)).Append(")\"");
                    return;
                }
                if (!ctx.Chain.Add(value)) { sb.Append("\"(cycle)\""); return; }
                if (++ctx.NestedCount > ctx.MaxNested)
                {
                    ctx.Truncated = true;
                    ctx.Chain.Remove(value);
                    sb.Append("\"(truncated)\"");
                    return;
                }
                try
                {
                    var pad = new string(' ', indent);
                    sb.Append("{\n").Append(pad).Append("  \"_type\": \"").Append(EscapeJson(type.FullName)).Append("\"");
                    // _type is already present, so the first member needs a leading comma.
                    bool firstMember = false;
                    SerializeMembers(ctx, value, type, remainingDepth - 1, indent + 2, applyFieldFilter: false, ref firstMember);
                    sb.Append("\n").Append(pad).Append("}");
                }
                finally { ctx.Chain.Remove(value); }
            }
            catch (Exception ex)
            {
                sb.Append("\"<error: ").Append(EscapeJson(ex.GetType().Name)).Append(">\"");
            }
        }

        private static object SafeGetValue(Func<object> getter)
        {
            try { return getter(); }
            catch { return "<error reading value>"; }
        }

        private static string SafeUnityName(object obj)
        {
            try
            {
                if (obj is UnityEngine.Object uo) return uo ? uo.name : "(destroyed)";
                return obj?.ToString() ?? "";
            }
            catch { return "(error)"; }
        }

        private static long SafeInstanceId(UnityEngine.Object uobj)
        {
            try { return uobj.GetInstanceID(); }
            catch { return 0; }
        }

        private static string SafeGameObjectName(Component comp)
        {
            try
            {
                if (!comp) return "(destroyed)";
                var go = comp.gameObject;
                if (!go) return "(destroyed)";
                return go.name;
            }
            catch { return "(error)"; }
        }

        private static string SafeActive(Component comp)
        {
            try
            {
                if (!comp) return "false";
                var go = comp.gameObject;
                if (!go) return "false";
                return go.activeInHierarchy ? "true" : "false";
            }
            catch { return "false"; }
        }

        private static string SafePosition(Component comp)
        {
            try
            {
                if (!comp) return "null";
                var t = comp.transform;
                if (!t) return "null";
                var p = t.position;
                return string.Format(CultureInfo.InvariantCulture, "[{0:F2}, {1:F2}, {2:F2}]", p.x, p.y, p.z);
            }
            catch { return "null"; }
        }

        private static bool ShouldSkipField(string name)
        {
            // Skip Unity internal members that produce noise or infinite loops.
            return name == "runInEditMode"
                || name == "useGUILayout"
                || name == "hideFlags"
                || name == "tag"
                || name == "rigidbody"
                || name == "rigidbody2D"
                || name == "camera"
                || name == "light"
                || name == "animation"
                || name == "constantForce"
                || name == "renderer"
                || name == "audio"
                || name == "networkView"
                || name == "collider"
                || name == "collider2D"
                || name == "hingeJoint"
                || name == "particleSystem";
        }

        private static Type FindType(string typeName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = asm.GetType(typeName, false);
                if (type != null) return type;
            }
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException rtle) { types = Array.FindAll(rtle.Types ?? new Type[0], t => t != null); }
                catch { continue; }
                foreach (var type in types)
                {
                    if (type == null) continue;
                    if (type.Name == typeName || type.FullName?.EndsWith("." + typeName) == true)
                        return type;
                }
            }
            return null;
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\n", "\\n")
                    .Replace("\r", "\\r")
                    .Replace("\t", "\\t");
        }
    }
}
