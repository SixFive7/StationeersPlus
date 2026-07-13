using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects.Pipes;
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
                case "StructurePowerUmbilicalMale":
                case "StructurePowerUmbilicalFemale":
                    return BuildRocketUmbilicalFooter();
                default:
                    var producers = ProducerPrefabNames();
                    if (producers != null && producers.Contains(prefabName))
                        return BuildProducerFooter();
                    return null;
            }
        }

        // Producer prefab names (solar, wind, RTG, the fuel generators, the small turbine, the portable-
        // generator dock), discovered once from the prefab registry via ProducerClassifier.IsProducer so the
        // footer auto-tracks the classifier list with no hardcoded name table. Not cached until the registry
        // has populated (an early call mid prefab-load returns null and retries on the next).
        private static HashSet<string> _producerPrefabNames;

        private static HashSet<string> ProducerPrefabNames()
        {
            if (_producerPrefabNames != null) return _producerPrefabNames;
            var prefabs = WorldManager.Instance?.SourcePrefabs;
            if (prefabs == null) return null;
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var thing in prefabs)
            {
                if (thing is Device d && !string.IsNullOrEmpty(thing.PrefabName)
                    && ProducerClassifier.IsProducer(d))
                    set.Add(thing.PrefabName);
            }
            if (set.Count == 0) return null;   // registry not ready yet; retry next call, do not cache empty
            _producerPrefabNames = set;
            return set;
        }

        private static string BuildProducerFooter()
        {
            return
                "\n\n{HEADER:POWER GRID PLUS}\n" +
                "Producer isolation: a power producer (solar, wind, RTG, the gas / coal / stirling generators, " +
                "the small turbine, or a portable generator on a power connector) may share a cable network ONLY " +
                "with other producers and transformers. Wired straight to a machine or any other consumer with no " +
                "transformer between them, it enters Variable Voltage Fault and stops generating until a transformer " +
                "is added. This includes the common early-game portable-generator-to-machines setup: route it " +
                "through a transformer.\n" +
                "The fault is reversible (clears the instant a transformer is added, or the device is toggled off " +
                "then on) and shows on the device: a red on/off button flash on the gas / coal / stirling " +
                "generators, hover text on the buttonless producers (solar, wind, RTG, the small turbine, the " +
                "connector). Always on; there is no toggle.";
        }

        // Soft-power system explanation, appended to every elastic-cell device footer
        // (POWERTODO 0.2.7.9). Single source string; a future soft-power device copies it.
        private const string SoftPowerParagraph =
            "\n{HEADER:SOFT POWER SYSTEM}\n" +
            "Charge and discharge rates are elastic. MaxChargeSpeed and MaxDischargeSpeed report the configured " +
            "upper caps; ChargeSpeed and DischargeSpeed report the ACTUAL rate this tick after Power Grid Plus " +
            "allocates the network's surplus.\n" +
            "When upstream supply has plenty of slack, ChargeSpeed approaches MaxChargeSpeed. When other storage " +
            "devices on the same input network compete for the same surplus, each receives a proportional share, " +
            "so ChargeSpeed is lower than MaxChargeSpeed by design.\n" +
            "Similarly, DischargeSpeed stays at 0 while downstream rigid demand is fully covered by generators or " +
            "upstream transformers (storage only discharges to fill a shortfall), and approaches MaxDischargeSpeed " +
            "when this device alone has to carry the downstream load.\n" +
            "This is intentional: the elastic system prevents producers and storage from cycling rapidly on / off " +
            "and avoids wasted round-trips through storage. Use ChargeSpeed / DischargeSpeed in IC10 scripts to " +
            "monitor live flow; use MaxChargeSpeed / MaxDischargeSpeed to read the configured caps.";

        private static string BuildRocketUmbilicalFooter()
        {
            return
                "\n\n{HEADER:POWER GRID PLUS}\n" +
                $"Internal cell: 10 kJ. Charge rate capped at {FormatWatts(Settings.RocketUmbilicalChargeRate.Value)} " +
                "(server config \"Rocket Umbilical Charge Rate\") and discharge at " +
                $"{FormatWatts(Settings.RocketUmbilicalDischargeRate.Value)} (\"Rocket Umbilical Discharge Rate\"), " +
                "each further capped by the respective cable tier.\n" +
                "Segmenting device: participates in the shed / overload / cycle-fault system like a battery. The Male " +
                "half flashes its button on a fault; the Female half has no button and reports faults via hover text only.\n" +
                "IC10: read-only MaxChargeSpeed / MaxDischargeSpeed (configured caps) and ChargeSpeed / DischargeSpeed " +
                "(live allocated rates) on both halves." +
                SoftPowerParagraph;
        }

        private static string BuildSuperHeavyCableFooter()
        {
            int cap = Settings.CableSuperHeavyMaxWatts.Value;
            if (cap <= 0)
            {
                return
                    "\n\n{HEADER:POWER GRID PLUS}\n" +
                    "Burn-immune: this cable does not burn out, regardless of throughput. It is the long-haul " +
                    "backbone of the grid. (Server config \"Super Heavy Cable Max Watts\" is 0 = unlimited.)\n" +
                    "Normal cable burns above " + Settings.CableNormalMaxWatts.Value + " W and heavy cable above " +
                    Settings.CableHeavyMaxWatts.Value + " W (0 = unlimited) from direct generator supply.";
            }

            return
                "\n\n{HEADER:POWER GRID PLUS}\n" +
                "Burn threshold: this cable burns out above " + cap + " W of sustained direct generator supply " +
                "(Server config \"Super Heavy Cable Max Watts\"). Normal cable burns above " +
                Settings.CableNormalMaxWatts.Value + " W and heavy cable above " + Settings.CableHeavyMaxWatts.Value + " W.";
        }

        private static string BuildTransformerFooter()
        {
            return
                "\n\n{HEADER:POWER GRID PLUS}\n" +
                "Throughput: governed by the vanilla Setting value (IC10 read / write, clamped 0..OutputMaximum). " +
                "A freshly built transformer starts at Setting = OutputMaximum (full rated throughput); an IC10 " +
                "script can lower Setting to throttle it dynamically. The in-world knob does NOT control " +
                "throughput; it sets the Priority value (non-negative integer, no upper cap, default 100, step 10 " +
                "per click or 1 with Alt). The Labeller tool also sets Priority.\n" +
                "Allocation: when multiple transformers pull from the same input cable network, supply is allocated " +
                "strictly by Priority among those siblings. The highest-priority transformer gets first dibs up to " +
                "its Setting; the leftover goes to the next priority, and so on. Priority comparisons are local to " +
                "each input network.\n" +
                "Shedding: a transformer that cannot get its share of the input network sheds for 60 seconds " +
                "(contributes 0 to the output network, flashes its on / off button orange, hover shows the cause " +
                "with a live countdown), then re-engages automatically. Lowest priority sheds first.\n" +
                "Overload: a transformer delivering at its Setting cap while downstream demand stays unmet enters " +
                "overload (red flash, hover countdown), contributes 0 for 60 seconds, then re-engages.\n" +
                "Cycle fault: a transformer that forms a closed power loop with other segmenting devices enters " +
                "cycle fault (red flash), contributes 0 for 60 seconds. No cable is burned for loops.\n" +
                "Toggling the device off clears any fault instantly (off-as-reset); toggling back on re-evaluates.\n" +
                "IC10: Setting / Maximum / Ratio are pure vanilla. Priority is read / write. Read-only Shedding / " +
                "Overloaded / CycleFault return 1 while the transformer is in the matching lockout, 0 otherwise.";
        }

        private static string BuildApcFooter()
        {
            return
                "\n\n{HEADER:POWER GRID PLUS}\n" +
                $"Charge rate: capped at {FormatWatts(Settings.ApcBatteryChargeRate.Value)} (server config \"APC Battery Charge Rate\"). " +
                "Further capped by the input cable's remaining tier headroom after this APC's own downstream pass-through is subtracted -- " +
                "so a single APC cannot blow its input cable just by charging on top of what it is already passing through.\n" +
                $"Discharge rate: the inserted cell discharges at most {FormatWatts(Settings.ApcBatteryDischargeRate.Value)} per tick " +
                "(server config \"APC Battery Discharge Rate\"), and only to fill the output network's shortfall, never more.\n" +
                "Output: capped at the output cable's tier rating. A single APC cannot supply more than its output cable physically carries.\n" +
                "Cable tier: input and output cables must be the same tier; mismatched cables burn at the junction when power flows.\n" +
                "Faults: participates in the shed / overload / cycle-fault system as a segmenting device (button flash + hover countdown).\n" +
                "IC10: read-only MaxChargeSpeed / MaxDischargeSpeed (configured caps) and ChargeSpeed / DischargeSpeed (live allocated rates).\n" +
                "Bug fix: the vanilla idle-leak is closed. Battery does not slowly drain when nothing is connected downstream." +
                SoftPowerParagraph;
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
                "Both caps are per-device, further bounded by the respective cable's tier rating. " +
                "Cable tier: belongs on heavy cable.\n" +
                "Faults: participates in the shed / overload / cycle-fault system as a segmenting device " +
                "(button flash + hover countdown; off-as-reset applies).\n" +
                "IC10: read-only MaxChargeSpeed / MaxDischargeSpeed (the caps above) and ChargeSpeed / " +
                "DischargeSpeed (live allocated rates). The previous Import Quantity / Export Quantity " +
                "exposure is REMOVED; scripts that read those must switch to the new logic values." +
                SoftPowerParagraph;
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
