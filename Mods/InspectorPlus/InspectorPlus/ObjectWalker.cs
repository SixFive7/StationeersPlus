using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace InspectorPlus
{
    /// <summary>
    /// Walks Unity objects via reflection and serializes their state to JSON.
    /// </summary>
    internal static class ObjectWalker
    {
        public static string Walk(SnapshotRequest request)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");

            var timestamp = DateTime.Now.ToString("o");
            sb.AppendLine($"  \"timestamp\": \"{timestamp}\",");
            sb.AppendLine($"  \"frame\": {Time.frameCount},");
            sb.AppendLine($"  \"gameTime\": {Time.time:F2},");

            if (request == null || request.Types.Count == 0)
            {
                WalkAllGameObjects(sb, request?.MaxDepth ?? 3, request?.IncludePrivate ?? false, request?.Fields);
            }
            else
            {
                WalkRequestedTypes(sb, request);
            }

            sb.AppendLine("}");
            return sb.ToString();
        }

        private static void WalkAllGameObjects(StringBuilder sb, int maxDepth, bool includePrivate, List<string> fields)
        {
            sb.AppendLine("  \"objects\": [");
            var allObjects = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
            bool first = true;

            // Group by type name for readability.
            var byType = new Dictionary<string, List<MonoBehaviour>>();
            foreach (var obj in allObjects)
            {
                var typeName = obj.GetType().FullName;
                if (!byType.ContainsKey(typeName))
                    byType[typeName] = new List<MonoBehaviour>();
                byType[typeName].Add(obj);
            }

            foreach (var kv in byType)
            {
                foreach (var obj in kv.Value)
                {
                    if (!first) sb.AppendLine(",");
                    first = false;
                    SerializeObject(sb, obj, maxDepth, includePrivate, fields, 4);
                }
            }

            sb.AppendLine();
            sb.AppendLine("  ]");
        }

        private static void WalkRequestedTypes(StringBuilder sb, SnapshotRequest request)
        {
            sb.AppendLine("  \"objects\": [");
            bool first = true;

            foreach (var typeName in request.Types)
            {
                var type = FindType(typeName);
                if (type == null)
                {
                    InspectorPlusPlugin.Log.LogWarning($"Type not found: {typeName}");
                    continue;
                }

                var instances = UnityEngine.Object.FindObjectsOfType(type);
                foreach (var instance in instances)
                {
                    if (!first) sb.AppendLine(",");
                    first = false;
                    SerializeObject(sb, instance, request.MaxDepth, request.IncludePrivate, request.Fields, 4);
                }
            }

            sb.AppendLine();
            sb.AppendLine("  ]");
        }

        private static void SerializeObject(StringBuilder sb, UnityEngine.Object obj, int maxDepth, bool includePrivate, List<string> fieldFilter, int indent)
        {
            var pad = new string(' ', indent);
            var type = obj.GetType();

            sb.AppendLine($"{pad}{{");
            sb.AppendLine($"{pad}  \"_type\": \"{EscapeJson(type.FullName)}\",");
            sb.AppendLine($"{pad}  \"_name\": \"{EscapeJson(obj.name)}\",");
            sb.AppendLine($"{pad}  \"_instanceId\": {obj.GetInstanceID()},");

            if (obj is Component comp)
            {
                var go = comp.gameObject;
                sb.AppendLine($"{pad}  \"_gameObject\": \"{EscapeJson(go.name)}\",");
                sb.AppendLine($"{pad}  \"_active\": {(go.activeInHierarchy ? "true" : "false")},");
                var pos = go.transform.position;
                sb.AppendLine($"{pad}  \"_position\": [{pos.x:F2}, {pos.y:F2}, {pos.z:F2}],");
            }

            var flags = BindingFlags.Public | BindingFlags.Instance;
            if (includePrivate)
                flags |= BindingFlags.NonPublic;

            var fields = type.GetFields(flags);
            var properties = type.GetProperties(flags);
            bool firstMember = true;

            sb.Append($"{pad}  \"fields\": {{");

            foreach (var field in fields)
            {
                if (fieldFilter != null && fieldFilter.Count > 0 && !fieldFilter.Contains(field.Name))
                    continue;
                if (ShouldSkipField(field.Name))
                    continue;

                var value = SafeGetValue(() => field.GetValue(obj));
                if (!firstMember) sb.Append(",");
                firstMember = false;
                sb.AppendLine();
                sb.Append($"{pad}    \"{EscapeJson(field.Name)}\": ");
                SerializeValue(sb, value, maxDepth - 1, indent + 4);
            }

            foreach (var prop in properties)
            {
                if (fieldFilter != null && fieldFilter.Count > 0 && !fieldFilter.Contains(prop.Name))
                    continue;
                if (!prop.CanRead || prop.GetIndexParameters().Length > 0)
                    continue;
                if (ShouldSkipField(prop.Name))
                    continue;

                var value = SafeGetValue(() => prop.GetValue(obj));
                if (!firstMember) sb.Append(",");
                firstMember = false;
                sb.AppendLine();
                sb.Append($"{pad}    \"{EscapeJson(prop.Name)}\": ");
                SerializeValue(sb, value, maxDepth - 1, indent + 4);
            }

            sb.AppendLine();
            sb.AppendLine($"{pad}  }}");
            sb.Append($"{pad}}}");
        }

        private static void SerializeValue(StringBuilder sb, object value, int remainingDepth, int indent)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            var type = value.GetType();

            if (value is string s)
            {
                sb.Append($"\"{EscapeJson(s)}\"");
            }
            else if (value is bool b)
            {
                sb.Append(b ? "true" : "false");
            }
            else if (type.IsPrimitive || type == typeof(decimal))
            {
                sb.Append(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (type.IsEnum)
            {
                sb.Append($"\"{value}\"");
            }
            else if (value is Vector3 v3)
            {
                sb.Append($"[{v3.x:F3}, {v3.y:F3}, {v3.z:F3}]");
            }
            else if (value is Vector2 v2)
            {
                sb.Append($"[{v2.x:F3}, {v2.y:F3}]");
            }
            else if (value is Quaternion q)
            {
                sb.Append($"[{q.x:F3}, {q.y:F3}, {q.z:F3}, {q.w:F3}]");
            }
            else if (value is Color c)
            {
                sb.Append($"[{c.r:F3}, {c.g:F3}, {c.b:F3}, {c.a:F3}]");
            }
            else if (value is UnityEngine.Object uobj)
            {
                // Don't recurse into Unity objects, just show name + type.
                sb.Append($"\"({uobj.GetType().Name}) {EscapeJson(uobj.name)}\"");
            }
            else if (value is IEnumerable enumerable && !(value is string))
            {
                if (remainingDepth <= 0)
                {
                    sb.Append("\"[...]\"");
                    return;
                }
                sb.Append("[");
                bool first = true;
                int count = 0;
                foreach (var item in enumerable)
                {
                    if (count >= 50)
                    {
                        sb.Append(", \"...(truncated)\"");
                        break;
                    }
                    if (!first) sb.Append(", ");
                    first = false;
                    SerializeValue(sb, item, remainingDepth - 1, indent);
                    count++;
                }
                sb.Append("]");
            }
            else if (remainingDepth > 0 && !type.IsPrimitive)
            {
                sb.Append($"\"({type.Name})\"");
            }
            else
            {
                sb.Append($"\"{EscapeJson(value.ToString())}\"");
            }
        }

        private static object SafeGetValue(Func<object> getter)
        {
            try
            {
                return getter();
            }
            catch
            {
                return "<error reading value>";
            }
        }

        private static bool ShouldSkipField(string name)
        {
            // Skip Unity internal fields that produce noise or infinite loops.
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
            // Try exact match across all loaded assemblies.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = asm.GetType(typeName, false);
                if (type != null) return type;
            }

            // Try partial match (just the class name without namespace).
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var type in asm.GetTypes())
                {
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
