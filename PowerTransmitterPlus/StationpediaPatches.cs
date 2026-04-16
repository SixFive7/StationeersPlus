using HarmonyLib;
using JetBrains.Annotations;
using System;

namespace PowerTransmitterPlus
{
    // Best-effort Stationpedia integration. We don't hard-fail if the game
    // version refactored these methods. Just log and skip; the readouts
    // still work without documentation entries.
    //
    // Pattern lifted from Stationeers Logic Extended (ThunderDuck).
    internal static class StationpediaPatches
    {
        // Called from the patched methods below. Idempotent per-page.
        internal static void RegisterCustomLogicTypePages()
        {
            try
            {
                var pediaType = AccessTools.TypeByName("Assets.Scripts.UI.Stationpedia")
                    ?? AccessTools.TypeByName("Stationpedia");
                if (pediaType == null) return;

                var pageType = AccessTools.TypeByName("Assets.Scripts.UI.StationpediaPage")
                    ?? AccessTools.TypeByName("StationpediaPage");
                if (pageType == null) return;

                var register = AccessTools.Method(pediaType, "Register",
                    new Type[] { pageType, typeof(bool) });
                if (register == null) return;

                foreach (var t in LogicTypeRegistry.All)
                {
                    var page = Activator.CreateInstance(pageType,
                        "LogicType" + t.Name, t.Name, t.Description);
                    register.Invoke(null, new object[] { page, false });
                }
            }
            catch (Exception e)
            {
                PowerTransmitterPlusPlugin.Log?.LogDebug(
                    $"Stationpedia page registration skipped: {e.Message}");
            }
        }
    }

    // Postfix the vanilla Stationpedia logic-variable population so our entries
    // are added when the in-game wiki rebuilds its logic listings.
    [HarmonyPatch]
    public static class StationpediaPopulateLogicVariablesPatch
    {
        [UsedImplicitly]
        public static System.Reflection.MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Assets.Scripts.UI.Stationpedia")
                ?? AccessTools.TypeByName("Stationpedia");
            return t == null ? null : AccessTools.Method(t, "PopulateLogicVariables");
        }

        [UsedImplicitly]
        public static bool Prepare() => TargetMethod() != null;

        [UsedImplicitly]
        public static void Postfix() => StationpediaPatches.RegisterCustomLogicTypePages();
    }
}
