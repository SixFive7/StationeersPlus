using System.Collections.Generic;
using BepInEx.Configuration;

namespace PowerGridPlus
{
    /// <summary>
    ///     Central holder for every <see cref="ConfigEntry{T}"/> the mod binds. All settings are
    ///     server-authoritative: in multiplayer the host's values apply for everyone.
    /// </summary>
    internal static class Settings
    {
        // --- Server - Cable Simulation ---
        internal static ConfigEntry<int> CableNormalMaxWatts;
        internal static ConfigEntry<int> CableHeavyMaxWatts;
        internal static ConfigEntry<int> CableSuperHeavyMaxWatts;

        // --- Server - Cable Costs ---
        internal static ConfigEntry<float> SuperHeavyCableCostMultiplier;

        // --- Server - Voltage Tiers ---
        // Voltage tiers are always on (no toggle). This list extends the built-in
        // heavy-cable device allow-list for modded high-draw machines.
        internal static ConfigEntry<string> ExtraHeavyCableDevices;

        // --- Server - Batteries ---
        internal static ConfigEntry<bool> EnableBatteryLimits;
        internal static ConfigEntry<float> StationBatteryChargeRate;
        internal static ConfigEntry<float> StationBatteryDischargeRate;
        internal static ConfigEntry<float> LargeBatteryChargeRate;
        internal static ConfigEntry<float> LargeBatteryDischargeRate;
        internal static ConfigEntry<float> NuclearBatteryChargeRate;
        internal static ConfigEntry<float> NuclearBatteryDischargeRate;
        internal static ConfigEntry<float> BatteryChargeEfficiency;
        internal static ConfigEntry<bool> EnableBatteryLogicAdditions;
        internal static ConfigEntry<bool> EnableBatteryLogicPassthrough;

        // --- Server - Transformers ---
        internal static ConfigEntry<bool> EnableTransformerExploitMitigation;
        internal static ConfigEntry<bool> EnableTransformerLogicAdditions;
        internal static ConfigEntry<bool> EnableTransformerLogicPassthrough;
        internal static ConfigEntry<bool> EnableTransformerShedding;
        internal static ConfigEntry<bool> EnableTransformerOverloadProtection;

        // --- Server - Area Power Control ---
        internal static ConfigEntry<bool> EnableAreaPowerControlFix;
        internal static ConfigEntry<float> ApcBatteryChargeRate;
        internal static ConfigEntry<float> ApcBatteryDischargeRate;
        internal static ConfigEntry<bool> EnableAreaPowerControlLogicPassthrough;

        // --- Server - Power Transmitters ---
        internal static ConfigEntry<bool> EnablePowerTransmitterLogicPassthrough;

        // --- Server - Rocket Umbilical ---
        internal static ConfigEntry<bool> EnableRocketUmbilicalLimits;
        internal static ConfigEntry<int> RocketUmbilicalChargeRate;
        internal static ConfigEntry<int> RocketUmbilicalDischargeRate;

        // --- Server - Emergency Lights ---
        internal static ConfigEntry<bool> EnableEmergencyLights;
        internal static ConfigEntry<string> EmergencyLightPrefabs;

        private static ConfigDescription Desc(string text, int order, bool requireRestart = false)
        {
            if (requireRestart)
            {
                return new ConfigDescription(text, null,
                    new KeyValuePair<string, int>("Order", order),
                    new KeyValuePair<string, bool>("RequireRestart", true));
            }

            return new ConfigDescription(text, null, new KeyValuePair<string, int>("Order", order));
        }

        internal static void Bind(ConfigFile config)
        {
            // --- Server - Cable Simulation ---
            // Cable burnout itself is deterministic and hardcoded (no setting): a cable burns when the
            // 20-tick running average of direct generator power on its network exceeds the weakest
            // cable's cap (PowerTickPatches / CableBurnWindow). Only the per-tier caps are configurable.
            CableNormalMaxWatts = config.Bind("Server - Cable Simulation", "Normal Cable Max Watts", 5000,
                Desc("(Server-authoritative) Watts cap for normal cable. A cable carrying more than this from direct " +
                     "generator supply burns out; overflow caused by transformers or batteries trips those devices into " +
                     "overload instead. 0 = unlimited (never burns). Default 5000 matches vanilla. Enforced at runtime; " +
                     "cables in the save are never modified. A mid-session change takes effect after a world reload.", 20));

            CableHeavyMaxWatts = config.Bind("Server - Cable Simulation", "Heavy Cable Max Watts", 100000,
                Desc("(Server-authoritative) Watts cap for heavy cable. 0 = unlimited (never burns). Default 100000 " +
                     "matches vanilla. Enforced at runtime; cables in the save are never modified. A mid-session change " +
                     "takes effect after a world reload.", 30));

            CableSuperHeavyMaxWatts = config.Bind("Server - Cable Simulation", "Super Heavy Cable Max Watts", 0,
                Desc("(Server-authoritative) Watts cap for super-heavy cable. 0 = unlimited (default; the long-haul " +
                     "backbone never burns). Set a positive value to make super-heavy cable burn above that load. " +
                     "Enforced at runtime; cables in the save are never modified. A mid-session change takes effect " +
                     "after a world reload.", 40));

            // --- Server - Cable Costs ---
            SuperHeavyCableCostMultiplier = config.Bind("Server - Cable Costs", "Super-Heavy Cable Cost Multiplier", 2.0f,
                Desc("(Server-authoritative) Multiplies the ingredient cost of crafting a super-heavy cable coil. 2.0 " +
                     "doubles it; set to 1.0 for vanilla cost. Applied to the crafting recipe at load time; existing " +
                     "coils in the world are unaffected.", 10, requireRestart: true));

            // --- Server - Voltage Tiers ---
            // Voltage tiers are always enforced (no toggle). A cable network must be all one tier;
            // joining two tiers burns the lower-tier cable at the junction and splits the network.
            // Transformers and Area Power Controllers bridge whatever they are wired to.
            ExtraHeavyCableDevices = config.Bind("Server - Voltage Tiers", "Extra Heavy-Cable Devices", "",
                Desc("(Server-authoritative) Comma-separated list of extra device prefab names that should be allowed on " +
                     "heavy cable, on top of the built-in high-draw machines (Carbon Sequester, Furnace, Advanced Furnace, " +
                     "Arc Furnace, Centrifuge, Recycler, Ice Crusher, Hydraulic Pipe Bender, Deep Miner). Use this for " +
                     "modded high-draw machines. Names are matched against the device's PrefabName. Example: " +
                     "StructureBigMachine,StructureAnotherMachine", 10));
            ExtraHeavyCableDevices.SettingChanged += (_, __) => VoltageTier.RefreshConfig();

            // --- Server - Batteries ---
            EnableBatteryLimits = config.Bind("Server - Batteries", "Enable Battery Limits", true,
                Desc("(Server-authoritative) When true, stationary batteries are charge- and discharge-rate limited. " +
                     "Each per-prefab cap below applies, plus a per-device cable-headroom cap so a single battery " +
                     "cannot exceed the rating of the cable it is wired to. With this off, batteries behave vanilla " +
                     "(no per-tick rate cap; only the cable's own overload mechanism applies).", 10));

            StationBatteryChargeRate = config.Bind("Server - Batteries", "Station Battery Charge Rate", 5000f,
                Desc("(Server-authoritative) Maximum charge wattage for the small Station Battery (StructureBattery). " +
                     "Per device, not per network. Capped further by the input cable's MaxVoltage so a single battery " +
                     "cannot blow its own cable just by charging.", 20));

            StationBatteryDischargeRate = config.Bind("Server - Batteries", "Station Battery Discharge Rate", 10000f,
                Desc("(Server-authoritative) Maximum discharge wattage for the small Station Battery " +
                     "(StructureBattery). Per device, not per network. Capped further by the output cable's MaxVoltage.", 30));

            LargeBatteryChargeRate = config.Bind("Server - Batteries", "Large Station Battery Charge Rate", 25000f,
                Desc("(Server-authoritative) Maximum charge wattage for the Large Station Battery " +
                     "(StructureBatteryLarge). Per device. Capped further by the input cable's MaxVoltage.", 40));

            LargeBatteryDischargeRate = config.Bind("Server - Batteries", "Large Station Battery Discharge Rate", 50000f,
                Desc("(Server-authoritative) Maximum discharge wattage for the Large Station Battery " +
                     "(StructureBatteryLarge). Per device. Capped further by the output cable's MaxVoltage.", 50));

            NuclearBatteryChargeRate = config.Bind("Server - Batteries", "Nuclear Battery Charge Rate", 25000f,
                Desc("(Server-authoritative) Maximum charge wattage for the Nuclear Battery (StructureBatteryNuclear, " +
                     "from the third-party MorePowerMod). No effect if MorePowerMod is not installed.", 60));

            NuclearBatteryDischargeRate = config.Bind("Server - Batteries", "Nuclear Battery Discharge Rate", 50000f,
                Desc("(Server-authoritative) Maximum discharge wattage for the Nuclear Battery (StructureBatteryNuclear, " +
                     "from the third-party MorePowerMod). No effect if MorePowerMod is not installed.", 70));

            BatteryChargeEfficiency = config.Bind("Server - Batteries", "Battery Charge Efficiency", 1.0f,
                Desc("(Server-authoritative) Fraction of incoming power a stationary battery actually stores. 1.0 is " +
                     "lossless; lower it to lose energy to charging inefficiency. (Trickle charges below 500 W are stored " +
                     "in full regardless, to avoid a battery never topping off.)", 80));

            EnableBatteryLogicAdditions = config.Bind("Server - Batteries", "Enable Battery Logic Additions", true,
                Desc("(Server-authoritative) When true, stationary batteries expose their max charge rate (Import Quantity) " +
                     "and max discharge rate (Export Quantity) as logic values.", 90));

            EnableBatteryLogicPassthrough = config.Bind("Server - Batteries", "Enable Battery Logic Passthrough", true,
                Desc("(Server-authoritative) Master kill-switch for stationary-battery logic-passthrough. When true, batteries " +
                     "honour the per-device LogicPassthroughMode logic value (writable via IC10 or a logic writer): 1 makes the " +
                     "battery logic-transparent (devices on either cable side are visible across), 0 keeps vanilla logic-opaque " +
                     "behaviour. Every battery defaults to mode 1 (enabled); per-device mode is persisted across save / load. " +
                     "When this master is false, every battery behaves vanilla-opaque regardless of its per-device mode.", 100));

            // --- Server - Transformers ---
            EnableTransformerExploitMitigation = config.Bind("Server - Transformers", "Enable Transformer Exploit Mitigation", true,
                Desc("(Server-authoritative) When true, transformers no longer leak free power and charge their own " +
                     "quiescent draw to the upstream network.", 10));

            EnableTransformerLogicAdditions = config.Bind("Server - Transformers", "Enable Transformer Logic Additions", true,
                Desc("(Server-authoritative) When true, transformers expose their current throughput as the Power Actual " +
                     "logic value.", 20));

            EnableTransformerLogicPassthrough = config.Bind("Server - Transformers", "Enable Transformer Logic Passthrough", true,
                Desc("(Server-authoritative) Master kill-switch for transformer logic-passthrough. When true, transformers " +
                     "honour the per-device LogicPassthroughMode logic value (writable via IC10 or a logic writer): 1 makes " +
                     "the transformer logic-transparent (devices on either side are visible across), 0 keeps vanilla " +
                     "logic-opaque behaviour. The small transformer and its reversed variant default to mode 1; every other " +
                     "transformer defaults to mode 0. Per-device mode is persisted across save / load. When this master is " +
                     "false, every transformer behaves vanilla-opaque regardless of its per-device mode.", 30));

            EnableTransformerShedding = config.Bind("Server - Transformers", "Enable Transformer Shedding", true,
                Desc("(Server-authoritative) Master toggle for the transformer Priority + Shedding feature (upstream-side " +
                     "protection). When true, every transformer's throughput is hardcoded at its OutputMaximum rating, the " +
                     "in-world dial controls a new Priority value instead (non-negative int, default 100, step 1 per click or " +
                     "10 with Alt), and the input cable network's supply is allocated strictly by Priority: the highest-" +
                     "priority transformer gets first dibs; the leftover goes to the next priority. A transformer that cannot " +
                     "get its share of the input sheds for 60 seconds (flashes its on / off button orange, surfaces a hover " +
                     "error, contributes 0 to the output network), then re-engages automatically. Shed fires instantly on " +
                     "detection -- the atomic power-tick architecture decides with fresh in-tick data, so there is no need " +
                     "for a shortfall-tolerance counter. The IC10 LogicType.Setting read returns OutputMaximum (hardcoded); " +
                     "writes to Setting redirect to Priority for backward compatibility with existing scripts. A new read-" +
                     "only LogicType.Shedding returns 1 while a transformer is in shed lockout, 0 otherwise. When this " +
                     "master is false, transformers behave vanilla (Setting is a writable throughput cap, no shedding, no " +
                     "flashing).", 40));

            EnableTransformerOverloadProtection = config.Bind("Server - Transformers", "Enable Transformer Overload Protection", true,
                Desc("(Server-authoritative) Master toggle for downstream-side overload protection. When true, a transformer " +
                     "whose output cable network demands more than the transformer can deliver enters overload protection: " +
                     "it contributes 0 W to the output network for 60 seconds (flashes its on / off button orange, surfaces a " +
                     "hover error 'downstream demand exceeds this transformer's limit'), then re-engages automatically. For parallel " +
                     "transformers on the same output network, overload fires for all of them together when combined " +
                     "OutputMaximum cannot meet the network's RequiredLoad. Fires instantly on detection; no tolerance " +
                     "counter. The downstream sub-network goes dark cleanly instead of vanilla's partial-power-then-Powered=" +
                     "false random device failures. A new read-only LogicType.Overloaded returns 1 while a transformer is in " +
                     "overload lockout, 0 otherwise. When this master is false, vanilla's partial-power behaviour returns.", 50));

            // --- Server - Area Power Control ---
            EnableAreaPowerControlFix = config.Bind("Server - Area Power Control", "Enable APC Power Fix", true,
                Desc("(Server-authoritative) When true, Area Power Controllers (a) no longer leak a small amount of power " +
                     "and slowly drain their battery when nothing is connected downstream, (b) apply the cable-headroom " +
                     "cap on charge so a single APC cannot exceed its input cable's MaxVoltage by adding charge demand " +
                     "on top of its pass-through, and (c) apply the cable cap on output so a single APC cannot supply " +
                     "more than its output cable's MaxVoltage. With this off, the APC behaves vanilla (the original " +
                     "free-power exploit returns and the cable caps do not apply).", 10));

            ApcBatteryChargeRate = config.Bind("Server - Area Power Control", "APC Battery Charge Rate", 1000f,
                Desc("(Server-authoritative) Maximum wattage an APC pulls from upstream to charge its internal cell. " +
                     "Per device, not per network. Capped further by the input cable's remaining headroom after the " +
                     "APC's own downstream pass-through is subtracted, so a single APC can never blow its own cable " +
                     "just by charging on top of what it is already passing through. Vanilla default is 1000.", 15));

            ApcBatteryDischargeRate = config.Bind("Server - Area Power Control", "APC Battery Discharge Rate", 1000f,
                Desc("(Server-authoritative) Maximum wattage the APC's inserted battery cell can discharge per tick to the " +
                     "output network. Per device, not per network. Capped further by the output cable's MaxVoltage. The " +
                     "elastic-supply allocator discharges the cell only to fill the output network's shortfall, never more.", 17));

            EnableAreaPowerControlLogicPassthrough = config.Bind("Server - Area Power Control", "Enable APC Logic Passthrough", true,
                Desc("(Server-authoritative) Master kill-switch for Area Power Control logic-passthrough. When true, " +
                     "Area Power Controllers honour the per-device LogicPassthroughMode logic value (writable via IC10 " +
                     "or a logic writer): 1 makes the APC logic-transparent (devices on either cable side are visible " +
                     "across, and the APC's own logic ports are visible from both), 0 keeps vanilla logic-opaque " +
                     "behaviour where the APC breaks the logic network the same way it breaks the power network. Every " +
                     "APC defaults to mode 1 (enabled); per-device mode is persisted across save / load. When this master " +
                     "is false, every APC behaves vanilla-opaque regardless of its per-device mode. Power is unaffected " +
                     "either way: the APC's downstream side always meters and gates power normally.", 20));

            // --- Server - Power Transmitters ---
            EnablePowerTransmitterLogicPassthrough = config.Bind("Server - Power Transmitters", "Enable Power Transmitter Logic Passthrough", true,
                Desc("(Server-authoritative) Master kill-switch for power-transmitter logic-passthrough across a wireless link. " +
                     "When true, a linked TX/RX dish pair is logic-transparent: an IC10 or logic reader wired to the TX's cable " +
                     "network can see devices wired to the RX's cable network, and vice versa. Each dish honours its own " +
                     "LogicPassthroughMode logic value (writable via IC10 or a logic writer): 1 = transparent, 0 = opaque. " +
                     "Defaults to mode 1 for every transmitter and receiver. Bridging requires the pair to be linked (auto-aim " +
                     "or manual link); an unlinked dish has nothing to bridge to. Per-device mode is persisted across save / load.", 10));

            // --- Server - Rocket Umbilical ---
            EnableRocketUmbilicalLimits = config.Bind("Server - Rocket Umbilical", "Enable Rocket Umbilical Limits", true,
                Desc("(Server-authoritative) When true, the rocket power umbilical pair (Male / Female) is charge- and " +
                     "discharge-rate limited like a stationary battery, participates in the shed / overload / cycle-fault " +
                     "system as a segmenting device, and exposes the four soft-power logic values (Max/Charge/Discharge " +
                     "Speed). When false, the umbilical reverts to vanilla behaviour (transfers up to its internal cell " +
                     "PowerMaximum per tick) and the four logic values are not exposed.", 10));

            RocketUmbilicalChargeRate = config.Bind("Server - Rocket Umbilical", "Rocket Umbilical Charge Rate", 10000,
                Desc("(Server-authoritative) Maximum Watts the rocket umbilical pulls from upstream per tick to charge its " +
                     "internal cell. Capped further by the input cable's MaxVoltage. Default 10000 matches the vanilla " +
                     "umbilical cell PowerMaximum.", 20));

            RocketUmbilicalDischargeRate = config.Bind("Server - Rocket Umbilical", "Rocket Umbilical Discharge Rate", 10000,
                Desc("(Server-authoritative) Maximum Watts the rocket umbilical discharges per tick to the output network. " +
                     "Capped further by the output cable's MaxVoltage. Default 10000 matches the vanilla umbilical cell " +
                     "PowerMaximum.", 30));

            // --- Server - Emergency Lights ---
            EnableEmergencyLights = config.Bind("Server - Emergency Lights", "Enable Wall Light Battery Emergency Mode", true,
                Desc("(Server-authoritative) When true, Wall Light Battery devices behave as emergency backup lights: the lamp " +
                     "stays off while the cable grid powers it, and switches on (powered by its internal battery cell) when " +
                     "grid power is lost. Set a specific light's Mode to 1 to opt that light out (vanilla wall-light " +
                     "behaviour). When this master is off, Wall Light Batteries behave vanilla regardless of Mode. Equivalent " +
                     "to alliephante's Battery Backup Light mod, with the per-tick Powered re-assert preserved so the lit " +
                     "emergency light does not flicker on the host. If the third-party Battery Backup Light mod is also " +
                     "installed, these patches yield to it (and you keep the flicker); uninstall that mod to switch to the " +
                     "Power Grid Plus implementation.", 10));

            EmergencyLightPrefabs = config.Bind("Server - Emergency Lights", "Emergency Light Prefabs", "StructureWallLightBattery",
                Desc("(Server-authoritative) Comma-separated list of light prefab names that get the emergency-backup " +
                     "behaviour when the master toggle above is on. Default covers the vanilla Wall Light (Battery). Add " +
                     "modded battery-light prefab names to include them, or remove entries to restrict the set. Entries " +
                     "must be battery-backed wall lights (the WallLightBattery device class); names are matched against " +
                     "the device's PrefabName.", 20));
        }
    }
}
