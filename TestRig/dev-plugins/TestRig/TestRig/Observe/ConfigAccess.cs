using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;

namespace TestRig
{
    /// <summary>
    /// Reads and writes any loaded plugin's BepInEx config entries at runtime.
    ///
    /// StationeersLaunchPad's in-game settings panel cannot reach a mod's
    /// ConfigEntry values mid-session, and editing the .cfg on disk needs a restart
    /// to take effect. Going straight at the live <see cref="ConfigEntryBase"/>
    /// through <c>Chainloader.PluginInfos[guid].Instance.Config</c> changes the value
    /// the running code actually reads, which is the only thing that matters for a
    /// between-runs settings flip.
    ///
    /// Writing also calls <see cref="ConfigFile.Save"/> so the change survives a
    /// restart, unless the caller asks otherwise.
    /// </summary>
    internal static class ConfigAccess
    {
        /// <summary>
        /// Resolves a plugin GUID to its live <see cref="ConfigFile"/>.
        ///
        /// Two routes, in order, because on this client neither alone is enough
        /// (verified 0.2.6403.27689 / StationeersLaunchPad 0.5.0):
        ///
        /// 1. <c>Chainloader.PluginInfos</c>. Only ever lists the plugins BepInEx
        ///    itself loaded out of <c>BepInEx/plugins/</c>. Every Workshop mod comes
        ///    in through StationeersLaunchPad instead and is absent here. Worse, the
        ///    <c>Instance</c> of even the plugins that ARE listed is null shortly
        ///    after boot, because this game destroys the BepInEx manager component.
        ///
        /// 2. Assembly scan for the type carrying <c>[BepInPlugin(guid, ...)]</c>,
        ///    then any <c>ConfigEntryBase</c> reachable from its static members. A
        ///    ConfigEntry holds a reference to its owning ConfigFile, and both
        ///    outlive the MonoBehaviour, so this route works whether or not the
        ///    plugin component is still alive. It is the route that actually works
        ///    for a Workshop mod, which is to say for nearly everything under test.
        /// </summary>
        internal static ConfigFile FindConfig(string guid, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(guid)) { error = "missing 'guid'"; return null; }

            try
            {
                PluginInfo info;
                if (Chainloader.PluginInfos.TryGetValue(guid, out info) && info != null)
                {
                    var instance = info.Instance as BaseUnityPlugin;
                    if (instance != null) return instance.Config;
                }
            }
            catch { }

            // Every matching type, not just the first. The same mod assembly can be
            // present more than once in the AppDomain (a Workshop copy plus a local
            // deploy, for instance), and only the copy whose Awake actually ran has
            // populated static ConfigEntry fields. Taking the first match by name
            // silently picks the dead one about half the time.
            var candidates = FindPluginTypes(guid);
            if (candidates.Count == 0)
            {
                error = "no plugin with GUID '" + guid + "' found in any loaded assembly";
                return null;
            }

            foreach (var t in candidates)
            {
                var config = ConfigFromType(t);
                if (config != null) return config;
            }

            var names = new List<string>();
            foreach (var t in candidates) names.Add(t.FullName + " @ " + AsmName(t));
            error = "found " + candidates.Count + " type(s) for GUID '" + guid + "' (" +
                    string.Join(", ", names.ToArray()) +
                    ") but none had a live ConfigEntry, so the ConfigFile is unreachable. " +
                    "The plugin may not have bound its config yet, or it keeps its entries somewhere other than a static field.";
            return null;
        }

        internal static string AsmName(Type t)
        {
            try { return t.Assembly.GetName().Name + " [" + (string.IsNullOrEmpty(t.Assembly.Location) ? "in-memory" : t.Assembly.Location) + "]"; }
            catch { return "?"; }
        }

        internal static List<Type> FindPluginTypes(string guid)
        {
            var result = new List<Type>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException rtle) { types = rtle.Types; }
                catch { continue; }
                if (types == null) continue;

                foreach (var t in types)
                {
                    if (t == null) continue;
                    object[] attrs;
                    try { attrs = t.GetCustomAttributes(typeof(BepInPlugin), false); }
                    catch { continue; }
                    if (attrs == null || attrs.Length == 0) continue;
                    var bp = attrs[0] as BepInPlugin;
                    if (bp != null && string.Equals(bp.GUID, guid, StringComparison.OrdinalIgnoreCase)) result.Add(t);
                }
            }
            return result;
        }

        private static ConfigFile ConfigFromType(Type type)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;

            foreach (var f in type.GetFields(flags))
            {
                object v;
                try { v = f.GetValue(null); } catch { continue; }
                var entry = v as ConfigEntryBase;
                if (entry?.ConfigFile != null) return entry.ConfigFile;
                var cf = v as ConfigFile;
                if (cf != null) return cf;
            }

            foreach (var p in type.GetProperties(flags))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                object v;
                try { v = p.GetValue(null, null); } catch { continue; }
                var entry = v as ConfigEntryBase;
                if (entry?.ConfigFile != null) return entry.ConfigFile;
                var cf = v as ConfigFile;
                if (cf != null) return cf;
            }

            // Last resort: a live component of the plugin type still in the scene.
            try
            {
                var found = UnityEngine.Object.FindObjectOfType(type) as BaseUnityPlugin;
                if (found != null) return found.Config;
            }
            catch { }

            return null;
        }

        /// <summary>Every plugin GUID reachable by assembly scan, Chainloader or not.</summary>
        internal static List<string> AllPluginGuids()
        {
            var result = new List<string>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException rtle) { types = rtle.Types; }
                catch { continue; }
                if (types == null) continue;

                foreach (var t in types)
                {
                    if (t == null) continue;
                    object[] attrs;
                    try { attrs = t.GetCustomAttributes(typeof(BepInPlugin), false); }
                    catch { continue; }
                    if (attrs == null || attrs.Length == 0) continue;
                    var bp = attrs[0] as BepInPlugin;
                    if (bp == null) continue;
                    result.Add(bp.GUID + "\t" + bp.Name + "\t" + bp.Version + "\t" + t.FullName + "\t" + AsmName(t));
                }
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        internal static string Dump(string guid, string filter)
        {
            string error;
            var config = FindConfig(guid, out error);
            if (config == null)
                return new Json.Obj().Bit("ok", false).Str("error", error).ToString();

            var entries = new List<string>();
            foreach (var def in new List<ConfigDefinition>(config.Keys))
            {
                ConfigEntryBase entry;
                try { entry = config[def]; } catch { continue; }
                if (entry == null) continue;

                string label = def.Section + " / " + def.Key;
                if (!string.IsNullOrEmpty(filter) &&
                    label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                object boxed = null;
                try { boxed = entry.BoxedValue; } catch { }
                object def0 = null;
                try { def0 = entry.DefaultValue; } catch { }

                entries.Add(new Json.Obj()
                    .Str("section", def.Section)
                    .Str("key", def.Key)
                    .Str("type", entry.SettingType == null ? null : entry.SettingType.Name)
                    .Str("value", boxed == null ? null : boxed.ToString())
                    .Str("default", def0 == null ? null : def0.ToString())
                    .Str("description", entry.Description == null ? null : entry.Description.Description)
                    .ToString());
            }

            entries.Sort(StringComparer.OrdinalIgnoreCase);
            return new Json.Obj()
                .Bit("ok", true)
                .Str("guid", guid)
                .Str("configPath", SafeConfigPath(config))
                .Int("count", entries.Count)
                .Raw("entries", "[" + string.Join(",", entries.ToArray()) + "]")
                .ToString();
        }

        private static string SafeConfigPath(ConfigFile config)
        {
            try { return config.ConfigFilePath; } catch { return null; }
        }

        internal static string Set(string guid, string section, string key, string rawValue, bool save)
        {
            string error;
            var config = FindConfig(guid, out error);
            if (config == null)
                return new Json.Obj().Bit("ok", false).Str("error", error).ToString();

            ConfigEntryBase entry = null;
            ConfigDefinition matched = null;
            foreach (var def in new List<ConfigDefinition>(config.Keys))
            {
                bool sectionOk = string.IsNullOrEmpty(section) ||
                                 string.Equals(def.Section, section, StringComparison.OrdinalIgnoreCase);
                if (!sectionOk) continue;
                if (!string.Equals(def.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                matched = def;
                entry = config[def];
                break;
            }

            if (entry == null)
                return new Json.Obj().Bit("ok", false)
                    .Str("error", "no entry '" + (section ?? "*") + " / " + key + "' in " + guid)
                    .ToString();

            object oldValue = null;
            try { oldValue = entry.BoxedValue; } catch { }

            object converted;
            try
            {
                converted = TomlTypeConverter.ConvertToValue(rawValue, entry.SettingType);
            }
            catch (Exception ex)
            {
                return new Json.Obj().Bit("ok", false)
                    .Str("error", "could not convert '" + rawValue + "' to " + entry.SettingType.Name + ": " + ex.Message)
                    .ToString();
            }

            try { entry.BoxedValue = converted; }
            catch (Exception ex)
            {
                return new Json.Obj().Bit("ok", false).Str("error", "write failed: " + ex.Message).ToString();
            }

            if (save)
            {
                try { config.Save(); }
                catch (Exception ex) { Plugin.Log.LogWarning("config save failed: " + ex.Message); }
            }

            object newValue = null;
            try { newValue = entry.BoxedValue; } catch { }

            return new Json.Obj()
                .Bit("ok", true)
                .Str("guid", guid)
                .Str("section", matched.Section)
                .Str("key", matched.Key)
                .Str("oldValue", oldValue == null ? null : oldValue.ToString())
                .Str("newValue", newValue == null ? null : newValue.ToString())
                .Bit("savedToDisk", save)
                .ToString();
        }

        internal static string Reload(string guid)
        {
            string error;
            var config = FindConfig(guid, out error);
            if (config == null)
                return new Json.Obj().Bit("ok", false).Str("error", error).ToString();
            try
            {
                config.Reload();
                return new Json.Obj().Bit("ok", true).Str("guid", guid).ToString();
            }
            catch (Exception ex)
            {
                return new Json.Obj().Bit("ok", false).Str("error", ex.Message).ToString();
            }
        }

        /// <summary>
        /// Lists every static member of a type with its runtime value type. The
        /// diagnostic of last resort when a reflective lookup finds a type but not
        /// the member it expected.
        /// </summary>
        internal static string DumpMembers(string typeName)
        {
            var type = ResolveType(typeName);
            if (type == null)
                return new Json.Obj().Bit("ok", false).Str("error", "type not found: " + typeName).ToString();

            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
            var entries = new List<string>();

            foreach (var f in type.GetFields(flags))
            {
                object v = null;
                string err = null;
                try { v = f.GetValue(null); } catch (Exception ex) { err = ex.Message; }
                entries.Add(new Json.Obj()
                    .Str("kind", "field")
                    .Str("name", f.Name)
                    .Str("declaredType", f.FieldType.FullName)
                    .Str("runtimeType", v == null ? null : v.GetType().AssemblyQualifiedName)
                    .Bit("isConfigEntryBase", v is ConfigEntryBase)
                    .Str("value", v == null ? null : Truncate(v.ToString()))
                    .Str("error", err)
                    .ToString());
            }

            foreach (var p in type.GetProperties(flags))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                object v = null;
                string err = null;
                try { v = p.GetValue(null, null); } catch (Exception ex) { err = ex.Message; }
                entries.Add(new Json.Obj()
                    .Str("kind", "property")
                    .Str("name", p.Name)
                    .Str("declaredType", p.PropertyType.FullName)
                    .Str("runtimeType", v == null ? null : v.GetType().AssemblyQualifiedName)
                    .Bit("isConfigEntryBase", v is ConfigEntryBase)
                    .Str("value", v == null ? null : Truncate(v.ToString()))
                    .Str("error", err)
                    .ToString());
            }

            return new Json.Obj()
                .Bit("ok", true)
                .Str("type", type.FullName)
                .Str("assembly", AsmName(type))
                .Str("configEntryBaseAsm", typeof(ConfigEntryBase).Assembly.FullName)
                .Int("count", entries.Count)
                .Raw("members", "[" + string.Join(",", entries.ToArray()) + "]")
                .ToString();
        }

        private static string Truncate(string s) => s != null && s.Length > 200 ? s.Substring(0, 200) + "..." : s;

        internal static Type ResolveType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType(typeName, false, true); } catch { }
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>
        /// Reads a static field or property on any loaded type by full name, so a
        /// harness can assert on mod internals that are not ConfigEntry backed.
        ///
        /// Values are rendered through <see cref="ThingReflect.Describe"/>, the same
        /// renderer the instance routes use, which is what makes a collection more
        /// than its type name here: <c>expand=true</c> lists its entries and
        /// <c>key=&lt;k&gt;</c> answers "does this registry contain that key" without
        /// dumping it. A mod-side dictionary keyed by reference id used to come back as
        /// "Dictionary`2" and needed a bespoke script to inspect.
        ///
        /// The instance twin is <c>GET /reflect/instance</c>, which reads a member on a
        /// specific object rather than a static.
        /// </summary>
        internal static string ReadStatic(string typeName, string memberName,
                                          bool expand = false, int expandLimit = 25, string key = null)
        {
            Type type = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { type = asm.GetType(typeName, false, true); } catch { }
                if (type != null) break;
            }
            if (type == null)
                return new Json.Obj().Bit("ok", false).Str("error", "type not found: " + typeName).ToString();

            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;

            object value = null;
            string kind = null;
            try
            {
                var prop = type.GetProperty(memberName, flags);
                if (prop != null) { value = prop.GetValue(null, null); kind = "property"; }
                else
                {
                    var field = type.GetField(memberName, flags);
                    if (field != null) { value = field.GetValue(null); kind = "field"; }
                }
            }
            catch (Exception ex)
            {
                return new Json.Obj().Bit("ok", false).Str("error", ex.Message).ToString();
            }

            if (kind == null)
                return new Json.Obj().Bit("ok", false)
                    .Str("error", "no static member '" + memberName + "' on " + typeName).ToString();

            // A ConfigEntry<T> is far more useful unwrapped than as a type name.
            try
            {
                if (value != null)
                {
                    var vt = value.GetType();
                    if (vt.IsGenericType && vt.GetGenericTypeDefinition() == typeof(BepInEx.Configuration.ConfigEntry<>))
                    {
                        var valueProp = vt.GetProperty("Value");
                        if (valueProp != null) value = valueProp.GetValue(value, null);
                    }
                }
            }
            catch { }

            var o = new Json.Obj()
                .Bit("ok", true)
                .Raw("epoch", Epoch.Json())
                .Str("type", typeName)
                .Str("member", memberName)
                .Str("kind", kind);
            ThingReflect.Describe(o, value, expand, expandLimit, key);
            return o.ToString();
        }
    }
}
