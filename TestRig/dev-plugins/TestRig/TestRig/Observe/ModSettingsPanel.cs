using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace TestRig
{
    /// <summary>
    /// Forces StationeersLaunchPad's per-mod settings panel on screen so a
    /// screenshot can prove what it renders.
    ///
    /// Vanilla StationeersLaunchPad only draws that panel while the Workshop menu is
    /// open AND a mod row is selected in it (<c>LaunchPadPatches.DrawWorkshopMenuConfig</c>
    /// reads the private <c>WorkshopMenu._selectedModItem</c>). Driving a mouse
    /// through that list is exactly the kind of UI navigation this plugin exists to
    /// avoid, and none of it is reachable at all once a world is loaded.
    ///
    /// <c>ConfigPanel.DrawWorkshopConfig(ModInfo)</c> is public and opens its own
    /// ImGui window, so calling it directly renders the same panel with no menu
    /// state involved. It has to run inside the ImGui frame, which means the same
    /// hook StationeersLaunchPad uses: a prefix on <c>OrbitalSimulation.Draw</c>,
    /// called from <c>ImGuiManager.RenderOverlay</c> between the frame begin and end.
    ///
    /// Everything here is reflective and fails soft: without StationeersLaunchPad
    /// loaded the patch simply never applies.
    /// </summary>
    internal static class ModSettingsPanel
    {
        internal static volatile string WantedMod;
        internal static string LastError;
        internal static long DrawCount;

        private static MethodInfo _drawWorkshopConfig;
        private static object _cachedInfo;
        private static string _cachedFor;

        private static Type LaunchPadType(string name)
        {
            return AccessTools.TypeByName("StationeersLaunchPad." + name) ?? AccessTools.TypeByName(name);
        }

        /// <summary>Every mod StationeersLaunchPad has loaded, as name and id strings.</summary>
        internal static string List()
        {
            var rows = new List<string>();
            try
            {
                foreach (var info in EnumerateModInfos())
                {
                    rows.Add(new Json.Obj()
                        .Str("name", ReadString(info, "Name"))
                        .Str("id", ReadString(info, "Id"))
                        .Str("author", ReadString(info, "Author"))
                        .Str("version", ReadString(info, "Version"))
                        .ToString());
                }
            }
            catch (Exception ex)
            {
                return new Json.Obj().Bit("ok", false).Str("error", ex.Message).ToString();
            }
            return new Json.Obj().Bit("ok", true).Int("count", rows.Count)
                .Raw("mods", "[" + string.Join(",", rows.ToArray()) + "]").ToString();
        }

        private static IEnumerable<object> EnumerateModInfos()
        {
            var loaderType = LaunchPadType("ModLoader");
            if (loaderType == null) yield break;
            var loadedField = AccessTools.Field(loaderType, "LoadedMods")
                              ?? AccessTools.Field(loaderType, "loadedMods");
            var loaded = loadedField?.GetValue(null) as IEnumerable;
            if (loaded == null) yield break;

            foreach (var mod in loaded)
            {
                if (mod == null) continue;
                object info = null;
                try
                {
                    var infoMember = mod.GetType().GetField("Info", BindingFlags.Public | BindingFlags.Instance);
                    if (infoMember != null) info = infoMember.GetValue(mod);
                    else info = mod.GetType().GetProperty("Info", BindingFlags.Public | BindingFlags.Instance)?.GetValue(mod, null);
                }
                catch { }
                if (info != null) yield return info;
            }
        }

        private static string ReadString(object obj, string member)
        {
            if (obj == null) return null;
            try
            {
                var f = obj.GetType().GetField(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) return f.GetValue(obj)?.ToString();
                var p = obj.GetType().GetProperty(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(obj, null)?.ToString();
            }
            catch { }
            return null;
        }

        private static object ResolveModInfo(string wanted)
        {
            if (_cachedInfo != null && _cachedFor == wanted) return _cachedInfo;
            foreach (var info in EnumerateModInfos())
            {
                var name = ReadString(info, "Name");
                var id = ReadString(info, "Id");
                if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(id, wanted, StringComparison.OrdinalIgnoreCase) ||
                    (name != null && name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    _cachedFor = wanted;
                    _cachedInfo = info;
                    return info;
                }
            }
            return null;
        }

        internal static string Show(string modName, bool show)
        {
            if (!show)
            {
                WantedMod = null;
                _cachedInfo = null;
                _cachedFor = null;
                return new Json.Obj().Bit("ok", true).Bit("showing", false).ToString();
            }

            if (string.IsNullOrEmpty(modName))
                return new Json.Obj().Bit("ok", false).Str("error", "missing 'mod' (a StationeersLaunchPad mod Name or Id)").ToString();

            var info = ResolveModInfo(modName);
            if (info == null)
                return new Json.Obj().Bit("ok", false)
                    .Str("error", "no loaded mod matching '" + modName + "'. GET /modsettings/list for the names.").ToString();

            if (Resolve() == null)
                return new Json.Obj().Bit("ok", false)
                    .Str("error", "StationeersLaunchPad ConfigPanel.DrawWorkshopConfig not found").ToString();

            WantedMod = modName;
            LastError = null;
            return new Json.Obj().Bit("ok", true).Bit("showing", true)
                .Str("mod", ReadString(info, "Name")).Str("id", ReadString(info, "Id")).ToString();
        }

        internal static MethodInfo Resolve()
        {
            if (_drawWorkshopConfig != null) return _drawWorkshopConfig;
            var panelType = LaunchPadType("ConfigPanel");
            if (panelType == null) return null;
            _drawWorkshopConfig = AccessTools.Method(panelType, "DrawWorkshopConfig");
            return _drawWorkshopConfig;
        }

        /// <summary>Called from inside the ImGui frame. Must never throw.</summary>
        internal static void DrawIfWanted()
        {
            var wanted = WantedMod;
            if (string.IsNullOrEmpty(wanted)) return;
            try
            {
                var method = Resolve();
                if (method == null) return;
                var info = ResolveModInfo(wanted);
                if (info == null) return;
                method.Invoke(null, new[] { info });
                DrawCount++;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                WantedMod = null;   // one failure is enough; do not spam every frame
            }
        }
    }

    /// <summary>
    /// Same hook StationeersLaunchPad uses for its own in-game ImGui windows: a
    /// prefix on OrbitalSimulation.Draw, which ImGuiManager.RenderOverlay calls
    /// inside the ImGui frame.
    /// </summary>
    [HarmonyPatch]
    internal static class ModSettingsDrawPatch
    {
        private static MethodBase Resolve()
        {
            var type = AccessTools.TypeByName("OrbitalSimulation")
                       ?? AccessTools.TypeByName("Assets.Scripts.OrbitalSimulation");
            return type == null ? null : AccessTools.Method(type, "Draw");
        }

        internal static MethodBase TargetMethod() => Resolve();

        internal static bool Prepare() => Plugin.ClientOnlyPatches && Resolve() != null;

        internal static void Prefix()
        {
            ModSettingsPanel.DrawIfWanted();
        }
    }
}
