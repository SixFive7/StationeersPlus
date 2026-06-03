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

        /// <summary>
        ///     Returns the "Power Grid Plus rules" footer to append to a device's Stationpedia
        ///     description, or null if PGP doesn't override this device. Localization.GetThingDescription
        ///     postfix calls this for every prefab name; unknown prefabs return null and pass through.
        ///     Numbers reflect the current Settings values where applicable, so a host who retunes the
        ///     server-authoritative caps sees the live numbers in the Stationpedia.
        /// </summary>
        internal static string GetDescriptionFooter(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return null;

            switch (prefabName)
            {
                case "StructureTransformer":
                case "StructureTransformerSmall":
                case "StructureTransformerSmallReversed":
                case "StructureTransformerLarge":
                    return BuildTransformerFooter();
                case "StructureAreaPowerControl":
                case "StructureAreaPowerControlReversed":
                    return BuildApcFooter();
                case "StructureBattery":
                    return BuildBatteryFooter(
                        "small Station Battery",
                        3_600_000f,
                        Settings.StationBatteryChargeRate.Value,
                        Settings.StationBatteryDischargeRate.Value,
                        chargeConfigName: "Station Battery Charge Rate",
                        dischargeConfigName: "Station Battery Discharge Rate");
                case "StructureBatteryLarge":
                    return BuildBatteryFooter(
                        "Large Station Battery",
                        9_000_001f,
                        Settings.LargeBatteryChargeRate.Value,
                        Settings.LargeBatteryDischargeRate.Value,
                        chargeConfigName: "Large Station Battery Charge Rate",
                        dischargeConfigName: "Large Station Battery Discharge Rate");
                case "StationBatteryNuclear":
                    // Gated implicitly: this prefab only exists if the third-party MorePowerMod is
                    // loaded. If absent, Localization.GetThingDescription never sees this prefabName
                    // and the footer never appends. MorePowerMod's prefab uses "StationBatteryNuclear"
                    // (Station-, not Structure-); confirmed via InspectorPlus baseline.
                    return BuildBatteryFooter(
                        "Nuclear Battery (from MorePowerMod)",
                        230_400_000f,
                        Settings.NuclearBatteryChargeRate.Value,
                        Settings.NuclearBatteryDischargeRate.Value,
                        chargeConfigName: "Nuclear Battery Charge Rate",
                        dischargeConfigName: "Nuclear Battery Discharge Rate");
                case "ItemCableCoilSuperHeavy":
                    return BuildSuperHeavyCableFooter();
                default:
                    return null;
            }
        }

        private static string BuildSuperHeavyCableFooter()
        {
            if (Settings.EnableUnlimitedSuperHeavyCables.Value)
            {
                return
                    "\n\n{HEADER:POWER GRID PLUS}\n" +
                    "Burn-immune: this cable does not burn out, regardless of throughput. It is the long-haul " +
                    "backbone of the grid. (Server config \"Enable Unlimited Super-Heavy Cables\" is on.)";
            }

            return
                "\n\n{HEADER:POWER GRID PLUS}\n" +
                "Burn behaviour: vanilla. This cable can still burn out under sustained overload. " +
                "(Server config \"Enable Unlimited Super-Heavy Cables\" is off.)";
        }

        private static string BuildTransformerFooter()
        {
            if (!Settings.EnableTransformerShedding.Value)
            {
                return
                    "\n\n{HEADER:POWER GRID PLUS}\n" +
                    "Behaviour: vanilla. The knob controls throughput; Setting / Maximum / Ratio logic " +
                    "values behave as in the base game. (Server config \"Enable Transformer Shedding\" is off.)";
            }

            return
                "\n\n{HEADER:POWER GRID PLUS}\n" +
                "Throughput: hardcoded at the transformer's OutputMaximum rating. The in-world knob no longer " +
                "controls throughput; it sets a new Priority value (non-negative integer, default 100, step 10 per " +
                "click or 1 with Alt).\n" +
                "Allocation: when multiple transformers pull from the same input cable network, supply is allocated " +
                "strictly by Priority. The highest-priority transformer gets first dibs up to its OutputMaximum; " +
                "the leftover goes to the next priority, and so on.\n" +
                "Shedding: a transformer that cannot get its full OutputMaximum from the input network sheds for 10 " +
                "seconds (contributes 0 to the output network, flashes its on / off button orange, surfaces a hover " +
                "error), then re-engages automatically. A 2-tick shortfall tolerance prevents single-tick demand " +
                "spikes from tripping a 10-second lockout.\n" +
                "IC10: LogicType.Setting reads return OutputMaximum (the fixed throughput); writes to Setting redirect " +
                "to Priority so legacy scripts that wrote to Setting now write to Priority transparently. A new " +
                "read-only LogicType.Shedding returns 1 while the transformer is in its lockout window, 0 otherwise.";
        }

        private static string BuildApcFooter()
        {
            float rate = Settings.ApcBatteryChargeRate.Value;
            return
                "\n\n{HEADER:POWER GRID PLUS}\n" +
                $"Charge rate: capped at {FormatWatts(rate)} (server config \"APC Battery Charge Rate\"). " +
                "Further capped by the input cable's remaining MaxVoltage after this APC's own downstream pass-through is subtracted -- " +
                "so a single APC cannot blow its input cable just by charging on top of what it is already passing through.\n" +
                "Output: capped at the output cable's MaxVoltage. A single APC cannot supply more than its output cable physically carries (normal cable = 5 kW, heavy cable = 100 kW, super-heavy cable = effectively unlimited).\n" +
                "Cable tier: input and output cables must be the same tier; mismatched cables burn at the junction when power flows.\n" +
                "Bug fix: the vanilla idle-leak is closed. Battery does not slowly drain when nothing is connected downstream.";
        }

        private static string BuildBatteryFooter(string name, float capacityJ, float chargeW, float dischargeW,
            string chargeConfigName, string dischargeConfigName)
        {
            // Wall-clock times: J/tick at 0.5 s tick -> divide capacity by (rateW * 2).
            string chargeTime = chargeW > 0f ? FormatSeconds(capacityJ / (chargeW * 2f)) : "n/a";
            string dischargeTime = dischargeW > 0f ? FormatSeconds(capacityJ / (dischargeW * 2f)) : "n/a";

            return
                "\n\n{HEADER:POWER GRID PLUS}\n" +
                $"Capacity: {FormatJoules(capacityJ)} ({name}).\n" +
                $"Charge rate: capped at {FormatWatts(chargeW)} (server config \"{chargeConfigName}\"). " +
                $"Full charge takes about {chargeTime} of wall-clock time at the cap.\n" +
                $"Discharge rate: capped at {FormatWatts(dischargeW)} (server config \"{dischargeConfigName}\"). " +
                $"Full discharge takes about {dischargeTime} of wall-clock time at the cap.\n" +
                "Both caps are per-device, further bounded by the respective cable's MaxVoltage. " +
                "Cable tier: belongs on heavy cable.";
        }

        private static string FormatWatts(float w)
        {
            if (w >= 1000f) return $"{w / 1000f:0.##} kW";
            return $"{w:0} W";
        }

        private static string FormatJoules(float j)
        {
            if (j >= 1_000_000f) return $"{j / 1_000_000f:0.##} MJ";
            if (j >= 1000f) return $"{j / 1000f:0.##} kJ";
            return $"{j:0} J";
        }

        private static string FormatSeconds(float s)
        {
            if (s < 60f) return $"{s:0} seconds";
            if (s < 3600f) return $"{s / 60f:0.#} minutes";
            int h = (int)(s / 3600f);
            int m = (int)((s - h * 3600f) / 60f);
            return $"{h} h {m} min";
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

    /// <summary>
    ///     Postfix Localization.GetThingDescription so the APC and the three stationary battery
    ///     prefabs (Station, Large, Nuclear-via-MorePowerMod) carry a "Power Grid Plus" footer
    ///     summarising the rate caps and cable rules. Vanilla descriptions are preserved verbatim;
    ///     we only append. Per the Localization postfix dispatch, this affects everywhere the
    ///     description surfaces (Stationpedia, tooltips, build-state hover).
    /// </summary>
    [HarmonyPatch]
    public static class LocalizationGetThingDescriptionPatch
    {
        public static System.Reflection.MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Assets.Scripts.Localization")
                ?? AccessTools.TypeByName("Localization");
            if (t == null) return null;
            return AccessTools.Method(t, "GetThingDescription", new[] { typeof(string) });
        }

        public static bool Prepare() => TargetMethod() != null;

        public static void Postfix(string thingPrefabName, ref string __result)
        {
            try
            {
                string footer = StationpediaPatches.GetDescriptionFooter(thingPrefabName);
                if (string.IsNullOrEmpty(footer)) return;
                __result = (__result ?? string.Empty) + footer;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogDebug($"Stationpedia footer append skipped for {thingPrefabName}: {e.Message}");
            }
        }
    }
}
