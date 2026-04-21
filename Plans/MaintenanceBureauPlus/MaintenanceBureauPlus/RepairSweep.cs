using System;
using System.Linq;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;

namespace MaintenanceBureauPlus
{
    // Walks OcclusionManager.AllThings and zeroes the four incident-damage
    // channels (Brute, Burn, Toxic, Radiation) on the inclusion set.
    //
    // Exclusion filter:
    //   Entity         covers Human, AIMEe, livestock (any living creature)
    //   DynamicBodyBag corpses (counted separately by TelemetryCollector)
    //   Organ*         organs inside any entity (name-match, concrete type varies)
    //   Plant / Egg    type-name match, concrete types vary
    //   Robot          defensive name-match in case Robot doesn't inherit Entity
    //   Structure.IsBroken wreckage (counted separately; repairing wreckage would
    //                  require remove-and-rebuild, which overwrites player intent)
    //
    // Gameplay-state channels (Stun, Oxygen, Hydration, Nutrition, Stamina) are
    // intentionally left alone. Main thread, server-only (caller guards).
    public static class RepairSweep
    {
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
            // Any living creature (Human, AIMEe, livestock that inherit from Entity).
            if (thing is Entity) return true;

            // Corpses. DynamicBodyBag does not inherit from Entity.
            if (thing is DynamicBodyBag) return true;

            // Wreckage: Structure.IsBroken is true if fully destroyed or deconstructed.
            if (thing is Structure s && s.IsBroken) return true;

            // Types whose concrete class varies but whose name tells us enough:
            var typeName = thing.GetType().Name;
            if (typeName.StartsWith("Organ")) return true;
            if (typeName.Contains("Plant") || typeName.Contains("GrowthStage") || typeName.Contains("Seed")) return true;
            if (typeName.Contains("Egg") || typeName.Contains("Embryo")) return true;
            if (typeName.Contains("Robot")) return true;  // defensive; some Robots may not be Entity

            return false;
        }

        // Zero the four incident channels via the canonical DamageState.Damage method.
        // Polymorphic dispatch handles ThingDamageState (Brute+Burn), OrganicDamageState
        // (all 9 channels, of which we only write 4), and EntityDamageState.
        // Calls for channels the concrete subclass doesn't implement are expected to
        // be no-ops. A catch-all wraps the block so any one channel's failure doesn't
        // derail the others.
        private static bool ZeroIncidentChannels(Thing thing)
        {
            var ds = thing.DamageState;
            if (ds == null) return false;
            try
            {
                ds.Damage(ChangeDamageType.Set, 0f, DamageUpdateType.Brute);
                ds.Damage(ChangeDamageType.Set, 0f, DamageUpdateType.Burn);
                ds.Damage(ChangeDamageType.Set, 0f, DamageUpdateType.Toxic);
                ds.Damage(ChangeDamageType.Set, 0f, DamageUpdateType.Radiation);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
