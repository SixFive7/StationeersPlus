using System;
using HarmonyLib;

namespace PowerGridPlus
{
    // Best-effort Stationpedia integration. We don't hard-fail if a future game
    // version refactors these methods; just log and skip. The LogicPassthroughMode
    // slot still works without its wiki page, only the in-game documentation entry
    // would be missing.
    //
    // Pattern lifted from Stationeers Logic Extended (ThunderDuck); shared with
    // PowerTransmitterPlus's StationpediaPatches.cs.
    internal static class StationpediaPatches
    {
        // Called from the patched method below. Idempotent per page (Register replaces by key).
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
                    // Key must match the per-device hyperlink target ("LogicType" + name),
                    // which vanilla emits from logicType.ToString() via Enum.GetName.
                    var page = Activator.CreateInstance(pageType,
                        "LogicType" + t.Name, t.Name, t.Description);
                    register.Invoke(null, new object[] { page, false });
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogDebug($"Stationpedia page registration skipped: {e.Message}");
            }
        }
    }

    // Postfix the vanilla Stationpedia logic-variable population so our custom
    // LogicType pages are registered when the in-game wiki rebuilds its logic listings.
    [HarmonyPatch]
    public static class StationpediaPopulateLogicVariablesPatch
    {
        public static System.Reflection.MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Assets.Scripts.UI.Stationpedia")
                ?? AccessTools.TypeByName("Stationpedia");
            return t == null ? null : AccessTools.Method(t, "PopulateLogicVariables");
        }

        public static bool Prepare() => TargetMethod() != null;

        public static void Postfix() => StationpediaPatches.RegisterCustomLogicTypePages();
    }
}
