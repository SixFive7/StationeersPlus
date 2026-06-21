using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using BepInEx;
using UnityEngine;

namespace ScenarioRunner
{
    // Scenario: device-port-dump
    //
    // One-shot dump of every power/data-bearing prefab in Prefab.AllPrefabs, for building a
    // full "device -> ports" table across the loaded mod set. Per prefab it records:
    //   - PrefabName + PrefabHash, DisplayName, runtime type, isDevice
    //   - each OpenEnds connector: index, class (power/data/powerdata/other), raw NetworkType,
    //     ConnectionRole, and the connector Transform's GameObject name
    //   - source-mod attribution: which loaded mod registered the prefab, else "vanilla"
    //
    // Attribution joins Prefab.AllPrefabs to the per-loader source registries by PrefabHash
    // (LaunchPadBooster.Mod.AllMods and StationeersLaunchPad ModLoader.LoadedMods), read by
    // reflection so no extra assembly reference is needed. See
    // Research/GameSystems/PrefabSourceAttribution.md. Any hash claimed by neither registry is
    // vanilla. The per-mod prefab-name lists are emitted too, so the attribution is auditable.
    //
    // Output: <BepInEx>/device-port-dump.json plus a one-line [ScenarioRunner] summary.
    // Connector model (NetworkType flags Power=2/Data=4/PowerAndData=6, ConnectionRole) matches
    // the game's own Device.DataConnection classification; see Research/GameClasses/Connection.md.
    //
    // Threading: runs off the ElectricityTick worker like every scenario. Booster attribution
    // reads only managed fields (Thing.PrefabHash/PrefabName), worker-safe. DisplayName and the
    // SLP GetComponent path can touch Unity APIs, so both are wrapped per-item; a failure there
    // degrades to PrefabName / "vanilla" rather than aborting the dump.
    internal static partial class Dispatcher
    {
        private static bool _devicePortDumpFired;

        private sealed class DpdMod
        {
            public string Name;
            public string Loader;
            public readonly List<string> Prefabs = new List<string>();
        }

        private static void Scenario_DevicePortDump()
        {
            if (_devicePortDumpFired) return;
            _devicePortDumpFired = true;

            try
            {
                _log?.LogInfo("[ScenarioRunner] device-port-dump START");

                // 1) Build PrefabHash -> mod-name attribution from the loader registries.
                var hashToMod = new Dictionary<int, string>();
                var mods = new List<DpdMod>();
                DpdBuildAttribution(hashToMod, mods);
                _log?.LogInfo($"[ScenarioRunner] device-port-dump attribution: mods={mods.Count} attributedHashes={hashToMod.Count}");

                // 2) Walk the registry; emit every power/data-bearing SmallGrid.
                var sb = new StringBuilder(1 << 20);
                sb.Append("{\n");
                string ver = "";
                try { ver = Application.version; } catch { }
                sb.Append("  \"gameVersion\": ").Append(DpdJsonStr(ver)).Append(",\n");

                sb.Append("  \"mods\": [\n");
                for (int i = 0; i < mods.Count; i++)
                {
                    var m = mods[i];
                    sb.Append("    {\"name\": ").Append(DpdJsonStr(m.Name))
                      .Append(", \"loader\": ").Append(DpdJsonStr(m.Loader))
                      .Append(", \"prefabCount\": ").Append(m.Prefabs.Count)
                      .Append(", \"prefabs\": [");
                    for (int j = 0; j < m.Prefabs.Count; j++)
                    {
                        if (j > 0) sb.Append(", ");
                        sb.Append(DpdJsonStr(m.Prefabs[j]));
                    }
                    sb.Append("]}");
                    sb.Append(i < mods.Count - 1 ? ",\n" : "\n");
                }
                sb.Append("  ],\n");

                sb.Append("  \"devices\": [\n");
                int total = 0, emitted = 0, vanillaCount = 0, modCount = 0, displayFails = 0;
                bool firstDev = true;
                foreach (var prefab in Prefab.AllPrefabs)
                {
                    if (prefab == null) continue;
                    total++;
                    if (!(prefab is SmallGrid grid)) continue;
                    var ends = grid.OpenEnds;
                    if (ends == null || ends.Count == 0) continue;

                    bool relevant = prefab is ElectricalInputOutput;
                    for (int i = 0; i < ends.Count && !relevant; i++)
                    {
                        var c = ends[i];
                        if (c == null) continue;
                        var ct = c.ConnectionType;
                        if ((ct & NetworkType.Power) != NetworkType.None || (ct & NetworkType.Data) != NetworkType.None)
                            relevant = true;
                    }
                    if (!relevant) continue;

                    bool isMod = hashToMod.TryGetValue(prefab.PrefabHash, out string srcMod);
                    if (isMod) modCount++; else { srcMod = "vanilla"; vanillaCount++; }

                    string display = prefab.PrefabName;
                    try { var d = prefab.DisplayName; if (!string.IsNullOrEmpty(d)) display = d; else displayFails++; }
                    catch { displayFails++; }

                    if (!firstDev) sb.Append(",\n");
                    firstDev = false;

                    sb.Append("    {");
                    sb.Append("\"prefabName\": ").Append(DpdJsonStr(prefab.PrefabName));
                    sb.Append(", \"prefabHash\": ").Append(prefab.PrefabHash);
                    sb.Append(", \"displayName\": ").Append(DpdJsonStr(display));
                    sb.Append(", \"type\": ").Append(DpdJsonStr(prefab.GetType().Name));
                    sb.Append(", \"isDevice\": ").Append(prefab is Device ? "true" : "false");
                    sb.Append(", \"sourceMod\": ").Append(DpdJsonStr(srcMod));
                    sb.Append(", \"hasDataConnection\": ").Append(grid.HasDataConnection ? "true" : "false");
                    sb.Append(", \"ports\": [");
                    for (int i = 0; i < ends.Count; i++)
                    {
                        var c = ends[i];
                        if (i > 0) sb.Append(", ");
                        if (c == null) { sb.Append("{\"i\": ").Append(i).Append(", \"class\": \"null\"}"); continue; }
                        var ct = c.ConnectionType;
                        string pname = "";
                        try { if (c.Transform != null) pname = c.Transform.gameObject.name; } catch { }
                        sb.Append("{\"i\": ").Append(i)
                          .Append(", \"class\": ").Append(DpdJsonStr(DpdPortClass(ct)))
                          .Append(", \"type\": ").Append(DpdJsonStr(ct.ToString()))
                          .Append(", \"role\": ").Append(DpdJsonStr(c.ConnectionRole.ToString()))
                          .Append(", \"name\": ").Append(DpdJsonStr(pname))
                          .Append("}");
                    }
                    sb.Append("]}");
                    emitted++;
                }
                sb.Append("\n  ]\n}\n");

                string outPath = Path.Combine(Paths.BepInExRootPath, "device-port-dump.json");
                File.WriteAllText(outPath, sb.ToString());

                _log?.LogInfo(
                    $"[ScenarioRunner] device-port-dump END totalPrefabs={total} emitted={emitted} " +
                    $"vanilla={vanillaCount} mod={modCount} mods={mods.Count} displayFallbacks={displayFails} -> {outPath}");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] device-port-dump threw: {e}");
            }
        }

        // power / data / powerdata using the game's exact-equality classification (Device.DataConnection),
        // with bitmask "+" variants flagged for any connector that combines a power/data bit with others.
        private static string DpdPortClass(NetworkType ct)
        {
            if (ct == NetworkType.PowerAndData) return "powerdata";
            if (ct == NetworkType.Data) return "data";
            if (ct == NetworkType.Power) return "power";
            bool p = (ct & NetworkType.Power) != NetworkType.None;
            bool d = (ct & NetworkType.Data) != NetworkType.None;
            if (p && d) return "powerdata+";
            if (d) return "data+";
            if (p) return "power+";
            return "other";
        }

        private static void DpdBuildAttribution(Dictionary<int, string> hashToMod, List<DpdMod> mods)
        {
            // LaunchPadBooster.Mod.AllMods -> Mod.ID.Name + Mod.Prefabs (List<Thing>). Worker-safe.
            try
            {
                var modType = DpdFindType("LaunchPadBooster.Mod");
                var allMods = DpdGetStatic(modType, "AllMods") as IEnumerable;
                if (allMods != null)
                {
                    foreach (var mod in allMods)
                    {
                        if (mod == null) continue;
                        var entry = new DpdMod { Loader = "LaunchPadBooster", Name = DpdBoosterModName(mod) };
                        var prefabs = DpdGetInstance(mod, "Prefabs") as IEnumerable;
                        if (prefabs != null)
                            foreach (var t in prefabs)
                            {
                                if (!(t is Thing th)) continue;
                                hashToMod[th.PrefabHash] = entry.Name;
                                entry.Prefabs.Add(th.PrefabName);
                            }
                        mods.Add(entry);
                    }
                }
            }
            catch (Exception e) { _log?.LogWarning($"[ScenarioRunner] device-port-dump booster attribution failed: {e.GetBaseException().Message}"); }

            // StationeersLaunchPad ModLoader.LoadedMods -> LoadedMod.Prefabs (List<GameObject>). Best-effort.
            try
            {
                var loaderType = DpdFindType("StationeersLaunchPad.ModLoader")
                                 ?? DpdFindType("StationeersLaunchPad.Metadata.ModLoader")
                                 ?? DpdFindTypeBySimpleName("ModLoader", "StationeersLaunchPad");
                var loaded = DpdGetStatic(loaderType, "LoadedMods") as IEnumerable;
                if (loaded != null)
                {
                    bool loggedMembers = false;
                    foreach (var lm in loaded)
                    {
                        if (lm == null) continue;
                        if (!loggedMembers) { loggedMembers = true; DpdLogMembers(lm); }
                        var entry = new DpdMod { Loader = "StationeersLaunchPad", Name = DpdSlpModName(lm) };
                        var prefabs = DpdGetInstance(lm, "Prefabs") as IEnumerable;
                        if (prefabs != null)
                            foreach (var go in prefabs)
                            {
                                Thing th = go as Thing;
                                if (th == null && go is GameObject gObj)
                                {
                                    try { th = gObj.GetComponent<Thing>(); } catch { }
                                }
                                if (th == null) continue;
                                if (!hashToMod.ContainsKey(th.PrefabHash)) hashToMod[th.PrefabHash] = entry.Name;
                                entry.Prefabs.Add(th.PrefabName);
                            }
                        mods.Add(entry);
                    }
                }
            }
            catch (Exception e) { _log?.LogWarning($"[ScenarioRunner] device-port-dump SLP attribution failed: {e.GetBaseException().Message}"); }
        }

        private static string DpdBoosterModName(object mod)
        {
            try
            {
                var id = DpdGetInstance(mod, "ID");
                var n = DpdGetInstance(id, "Name") as string;
                if (!string.IsNullOrEmpty(n)) return n;
            }
            catch { }
            return "(booster-unnamed)";
        }

        // LoadedMod (StationeersLaunchPad ModLoader.LoadedMods) holds Prefabs but NOT the name; the name
        // lives on a separate ModInfo (ModInfo.Name => About.Name). Find the ModInfo-shaped member on the
        // LoadedMod by reflection, then read its Name. See StationeersLaunchPadModLoading.md.
        private static string DpdSlpModName(object lm)
        {
            // Direct members, in case a member surfaces the name straight off the LoadedMod.
            foreach (var n in new[] { "Name", "DirectoryName" })
            { try { var v = DpdGetInstance(lm, n) as string; if (!string.IsNullOrEmpty(v)) return v; } catch { } }

            var info = DpdFindModInfo(lm);
            if (info != null)
            {
                try { var n = DpdGetInstance(info, "Name") as string; if (!string.IsNullOrEmpty(n)) return n; } catch { }
                try { var about = DpdGetInstance(info, "About"); var an = about != null ? DpdGetInstance(about, "Name") as string : null; if (!string.IsNullOrEmpty(an)) return an; } catch { }
                try { var dn = DpdGetInstance(info, "DirectoryName") as string; if (!string.IsNullOrEmpty(dn)) return dn; } catch { }
            }
            return "(slp-unnamed)";
        }

        // Scan a LoadedMod's instance fields + properties for the ModInfo-shaped record (the metadata
        // object carrying the mod name). Strict-ish: type named *ModInfo*, or a record exposing both a
        // Name member and an About/DirectoryPath member.
        private static object DpdFindModInfo(object lm)
        {
            if (lm == null) return null;
            var t = lm.GetType();
            const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var f in t.GetFields(BF))
            { object v = null; try { v = f.GetValue(lm); } catch { } if (DpdLooksLikeModInfo(v)) return v; }
            foreach (var p in t.GetProperties(BF))
            { if (p.GetIndexParameters().Length > 0) continue; object v = null; try { v = p.GetValue(lm); } catch { } if (DpdLooksLikeModInfo(v)) return v; }
            return null;
        }

        private static bool DpdLooksLikeModInfo(object v)
        {
            if (v == null) return false;
            var t = v.GetType();
            if (t.Name.IndexOf("ModInfo", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            bool hasName = t.GetProperty("Name") != null || t.GetField("Name") != null;
            bool hasDir = t.GetProperty("DirectoryPath") != null || t.GetProperty("DirectoryName") != null || t.GetProperty("About") != null
                          || t.GetField("DirectoryPath") != null || t.GetField("About") != null;
            return hasName && hasDir;
        }

        // One-time diagnostic: log a LoadedMod's field + property names so the name member can be pinned
        // if the heuristic above ever misses on a future StationeersLaunchPad version.
        private static void DpdLogMembers(object lm)
        {
            try
            {
                var t = lm.GetType();
                const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var fields = string.Join(", ", Array.ConvertAll(t.GetFields(BF), f => f.Name + ":" + f.FieldType.Name));
                var props = string.Join(", ", Array.ConvertAll(t.GetProperties(BF), p => p.Name + ":" + p.PropertyType.Name));
                _log?.LogInfo($"[ScenarioRunner] device-port-dump LoadedMod type={t.FullName} | fields=[{fields}] | props=[{props}]");
            }
            catch { }
        }

        private static Type DpdFindType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            { try { var t = a.GetType(fullName); if (t != null) return t; } catch { } }
            return null;
        }

        private static Type DpdFindTypeBySimpleName(string simpleName, string assemblyNameContains)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assemblyNameContains != null && (a.FullName == null
                    || a.FullName.IndexOf(assemblyNameContains, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                Type[] types; try { types = a.GetTypes(); } catch { continue; }
                foreach (var t in types) if (t.Name == simpleName) return t;
            }
            return null;
        }

        private static object DpdGetStatic(Type t, string name)
        {
            if (t == null) return null;
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (p != null) return p.GetValue(null);
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            return f?.GetValue(null);
        }

        private static object DpdGetInstance(object obj, string name)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null) return p.GetValue(obj);
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return f?.GetValue(obj);
        }

        private static string DpdJsonStr(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
