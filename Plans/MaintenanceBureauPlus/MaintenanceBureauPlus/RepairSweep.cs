using System;
using System.Linq;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Objects;

namespace MaintenanceBureauPlus
{
    // Walks Thing.AllThings and zeroes incident-damage channels on the inclusion set.
    // See plan.md Section 5 for the exclusion filter and channel table.
    // Main thread, server-side only (caller must guard).
    public static class RepairSweep
    {
        // Incident channels we zero. Gameplay channels (Stun, Oxygen, Hydration,
        // Nutrition, Stamina) are intentionally left untouched.
        private static readonly string[] IncidentChannels = { "Brute", "Burn", "Toxic", "Radiation" };

        public static int Run()
        {
            int repaired = 0;
            int skipped = 0;
            try
            {
                var all = OcclusionManager.AllThings;
                if (all == null) return 0;

                foreach (var thing in all.ToList())
                {
                    if (thing == null) continue;
                    if (ShouldSkip(thing))
                    {
                        skipped++;
                        continue;
                    }
                    if (ZeroIncidentChannels(thing)) repaired++;
                }
            }
            catch (Exception ex)
            {
                MaintenanceBureauPlusPlugin.Log.LogError("RepairSweep failed: " + ex.Message);
            }
            MaintenanceBureauPlusPlugin.Log.LogInfo("[RepairSweep] repaired=" + repaired + " skipped=" + skipped);
            return repaired;
        }

        private static bool ShouldSkip(Thing thing)
        {
            var typeName = thing.GetType().Name;

            // Players, organs, bots / AIs, livestock. Covered by type-name pattern
            // matching to stay robust against exact type namespace drift.
            if (typeName == "Human" || typeName.EndsWith("Human")) return true;
            if (typeName.StartsWith("Organ")) return true;
            if (typeName.Contains("AIMEe")) return true;
            if (typeName.Contains("Robot")) return true;
            if (typeName.Contains("Chicken") || typeName.Contains("Cow") ||
                typeName.Contains("Sheep") || typeName.Contains("Pig") ||
                typeName.Contains("Livestock") || typeName.Contains("Rabbit") ||
                typeName.Contains("Goat")) return true;

            // Corpses. Counted by TelemetryCollector; not repaired.
            if (typeName == "DynamicBodyBag" || typeName.Contains("BodyBag")) return true;

            // Plants and growth stages. Not touched.
            if (typeName.Contains("Plant") || typeName.Contains("GrowthStage") ||
                typeName.Contains("Seed")) return true;

            // Eggs and embryos.
            if (typeName.Contains("Egg") || typeName.Contains("Embryo")) return true;

            // Broken structures. Counted as wreckage; not repaired (would require
            // remove-and-rebuild, which overwrites player decisions).
            var structure = thing as Structure;
            if (structure != null && IsBroken(structure)) return true;

            return false;
        }

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

        // Zero each incident channel on the thing's DamageState.
        // Uses reflection so the code works against both ThingDamageState (Brute, Burn)
        // and OrganicDamageState (9 channels) without knowing the exact type.
        // A channel that doesn't exist on the target type silently does nothing.
        private static bool ZeroIncidentChannels(Thing thing)
        {
            bool touched = false;
            try
            {
                var damageStateProp = thing.GetType().GetProperty("DamageState") ??
                                      thing.GetType().GetProperty("DamageState",
                                          BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                if (damageStateProp == null) return false;

                var damageState = damageStateProp.GetValue(thing, null);
                if (damageState == null) return false;

                foreach (var channel in IncidentChannels)
                {
                    if (TryZeroChannel(damageState, channel)) touched = true;
                }
            }
            catch
            {
                // Ignore; not every Thing has a writable damage state.
            }
            return touched;
        }

        // Prefer writing to a simple public field/property named after the channel
        // (e.g. ds.Brute = 0f). If that fails, try calling a Damage(Set, 0f, channel)
        // method. If neither works on this type, move on.
        private static bool TryZeroChannel(object damageState, string channelName)
        {
            var type = damageState.GetType();

            var prop = type.GetProperty(channelName);
            if (prop != null && prop.CanWrite && prop.PropertyType == typeof(float))
            {
                try { prop.SetValue(damageState, 0f, null); return true; }
                catch { }
            }

            var field = type.GetField(channelName);
            if (field != null && field.FieldType == typeof(float))
            {
                try { field.SetValue(damageState, 0f); return true; }
                catch { }
            }

            return false;
        }
    }
}
