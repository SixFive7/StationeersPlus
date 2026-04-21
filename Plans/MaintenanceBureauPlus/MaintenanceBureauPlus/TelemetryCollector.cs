using System;
using System.Collections.Generic;
using System.Text;
using Assets.Scripts.Objects;
using UnityEngine;

namespace MaintenanceBureauPlus
{
    public class Telemetry
    {
        public int CorpseCount;
        public int WreckageCount;
        public List<string> WreckageDetails = new List<string>();

        public string RenderWreckageBrief()
        {
            const int detailThreshold = 10;
            if (WreckageCount == 0) return "none";
            if (WreckageCount > detailThreshold) return WreckageCount + " items";
            if (WreckageDetails.Count == 0) return WreckageCount + " items";
            var sb = new StringBuilder();
            for (int i = 0; i < WreckageDetails.Count; i++)
            {
                if (i > 0) sb.Append("; ");
                sb.Append(WreckageDetails[i]);
            }
            return sb.ToString();
        }
    }

    public static class TelemetryCollector
    {
        // Walks Thing.AllThings and counts corpses and broken structures.
        // Main thread only.
        public static Telemetry Collect()
        {
            var t = new Telemetry();
            try
            {
                var all = Thing.AllThings;
                if (all == null) return t;
                foreach (var thing in all)
                {
                    if (thing == null) continue;

                    // Corpses. Exact type is DynamicBodyBag; check by name as a
                    // defensive fallback in case the namespace differs.
                    var typeName = thing.GetType().Name;
                    if (typeName == "DynamicBodyBag" || typeName.Contains("BodyBag"))
                    {
                        t.CorpseCount++;
                        continue;
                    }

                    // Broken structures. `Structure.IsBroken` is the vanilla flag
                    // per Research/GameSystems/RepairMechanics.md.
                    var structure = thing as Structure;
                    if (structure != null && IsBroken(structure))
                    {
                        t.WreckageCount++;
                        if (t.WreckageDetails.Count < 10)
                        {
                            var pos = thing.ThingTransform != null ? thing.ThingTransform.position : Vector3.zero;
                            t.WreckageDetails.Add(
                                thing.GetType().Name +
                                " at (" + Mathf.RoundToInt(pos.x) + "," + Mathf.RoundToInt(pos.y) + "," + Mathf.RoundToInt(pos.z) + ")");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MaintenanceBureauPlusPlugin.Log.LogError("TelemetryCollector failed: " + ex.Message);
            }
            return t;
        }

        // Reflection-based property access so we degrade gracefully if the
        // exact field name differs in a given game version.
        private static bool IsBroken(Structure s)
        {
            try
            {
                var prop = s.GetType().GetProperty("IsBroken");
                if (prop != null)
                {
                    var v = prop.GetValue(s, null);
                    return v is bool && (bool)v;
                }
            }
            catch { }
            return false;
        }
    }
}
