using HarmonyLib;
using StationeersPlus.Shared;
using System;

namespace NetworkPuristPlus
{
    // Belt-and-suspenders Stationpedia hide. SPDADataHandler.HandleThingPageOverrides() (called from
    // Stationpedia.PopulateLists, just before the per-prefab page build) walks the loaded XML
    // ThingOverrideData and sets Thing.HideInStationpedia = item.HideInSPDA on each named prefab -- so a
    // page-override with HideInSPDA = false would CLEAR the flag we set in LongVariantRegistry.Build().
    // Vanilla ships no such override for the long pipe / chute / cable variants, but a future mod could.
    // This postfix re-asserts HideInStationpedia = true (and the parallel SPDADataHandler.HiddenInPedia
    // dictionary entry, which PopulateLists also consults) on every long variant we are removing, after
    // HandleThingPageOverrides has run, so the Stationpedia page-build that follows still skips them.
    //
    // SPDADataHandler is a global-namespace type (Stationpedia.DataHandler is a static field of it); the
    // method is a parameterless public instance method. We need the SPDADataHandler instance both to scope
    // the patch correctly and to write HiddenInPedia, so the postfix takes __instance.
    //
    // Gated on the master toggle. When a long variant's family toggle is off it is never in LongToBase /
    // LongVariants in the first place, so this only touches the families that are actually being removed.
    // If LongVariantRegistry found nothing (game version changed, regex no longer matches), the loop is
    // empty and the postfix is a no-op.
    [HarmonyPatch(typeof(SPDADataHandler), nameof(SPDADataHandler.HandleThingPageOverrides))]
    internal static class HideLongVariantsStationpediaPatch
    {
        private static void Postfix(SPDADataHandler __instance)
        {
            if (!Settings.MasterEnabled) return;
            if (LongVariantRegistry.LongVariants.Count == 0) return;

            int reHidden = 0;
            foreach (var longVariant in LongVariantRegistry.LongVariants)
            {
                if (longVariant == null) continue;
                try
                {
                    if (!longVariant.HideInStationpedia)
                    {
                        longVariant.HideInStationpedia = true;
                        reHidden++;
                    }
                    if (__instance?.HiddenInPedia != null) __instance.HiddenInPedia[longVariant.PrefabName] = true;
                }
                catch (Exception e)
                {
                    // Capped, because this line sits inside a loop over every long variant (two dozen on a
                    // vanilla install, more with a mod that adds long pieces) on a postfix that is NOT
                    // one-shot: Stationpedia.PopulateLists re-runs on a language change and on every
                    // Stationpedia regeneration. Uncapped it was one console line per prefab per rebuild.
                    // CapWithSummary rather than Once-per-prefab because the failure is systemic when it
                    // happens at all: three named prefabs are enough to identify what broke, and the count
                    // of the rest is the only extra thing a player could act on. FlushSummary emits that
                    // count below, once the loop is done.
                    PlayerMessage.Warn("stationpedia-rehide-failed", Throttle.CapWithSummary(3),
                        $"could not re-hide {SafeName(longVariant)} from the Stationpedia: {e.Message}");
                }
            }
            PlayerMessage.FlushSummary("stationpedia-rehide-failed");

            // The sweep summary: at most one line per Stationpedia rebuild, and only when a page override
            // actually cleared the flag (vanilla ships none, so normally never). Throttle.Never, same as
            // the other sweep summaries -- it is already once per pass and reports a real state change.
            if (reHidden > 0)
                PlayerMessage.Info("stationpedia-rehide", Throttle.Never, $"re-hid {reHidden} long-variant prefab(s) from the Stationpedia after a page-override pass cleared the flag.");
        }

        private static string SafeName(Assets.Scripts.Objects.Structure s)
        {
            try { return s != null ? s.PrefabName : "(null)"; }
            catch { return "(unknown)"; }
        }
    }
}
