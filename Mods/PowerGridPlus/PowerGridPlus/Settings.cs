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
        internal static ConfigEntry<float> CableBurnFactor;
        internal static ConfigEntry<bool> EnableUnlimitedSuperHeavyCables;
        internal static ConfigEntry<bool> EnableRecursiveNetworkLimits;

        // --- Server - Cable Costs ---
        internal static ConfigEntry<float> SuperHeavyCableCostMultiplier;

        // --- Server - Voltage Tiers ---
        internal static ConfigEntry<bool> EnableVoltageTiers;
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

        // --- Server - Area Power Control ---
        internal static ConfigEntry<bool> EnableAreaPowerControlFix;
        internal static ConfigEntry<float> ApcBatteryChargeRate;
        internal static ConfigEntry<bool> EnableAreaPowerControlLogicPassthrough;

        // --- Server - Power Transmitters ---
        internal static ConfigEntry<bool> EnablePowerTransmitterLogicPassthrough;

        // --- Server - Emergency Lights ---
        internal static ConfigEntry<bool> EnableEmergencyLights;

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
            CableBurnFactor = config.Bind("Server - Cable Simulation", "Cable Burn Factor", 1.0f,
                Desc("(Server-authoritative) Scales how likely an overloaded cable is to burn out on a given tick. " +
                     "1.0 is the default Re-Volt-derived rate; raise it for harsher grids, lower it for forgiving ones; " +
                     "set it to 0.0 to disable gradual cable burnout entirely (fuses still blow).", 10));

            EnableUnlimitedSuperHeavyCables = config.Bind("Server - Cable Simulation", "Enable Unlimited Super-Heavy Cables", true,
                Desc("(Server-authoritative) When true, super-heavy cable never burns out regardless of load. It is the " +
                     "long-haul backbone. Normal and heavy cable keep their ratings. Turn this off for vanilla-style " +
                     "super-heavy cable that can still burn.", 20));

            EnableRecursiveNetworkLimits = config.Bind("Server - Cable Simulation", "Enable Recursive Network Limits", false,
                Desc("(Server-authoritative) When true, restores the vanilla check that force-burns cables when the power " +
                     "grid forms a loop through multiple transformers or batteries. Off by default; recursive and looped " +
                     "networks are allowed.", 30));

            // --- Server - Cable Costs ---
            SuperHeavyCableCostMultiplier = config.Bind("Server - Cable Costs", "Super-Heavy Cable Cost Multiplier", 2.0f,
                Desc("(Server-authoritative) Multiplies the ingredient cost of crafting a super-heavy cable coil. 2.0 " +
                     "doubles it; set to 1.0 for vanilla cost. Applied to the crafting recipe at load time; existing " +
                     "coils in the world are unaffected.", 10, requireRestart: true));

            // --- Server - Voltage Tiers ---
            EnableVoltageTiers = config.Bind("Server - Voltage Tiers", "Enable Voltage Tiers", true,
                Desc("(Server-authoritative) When true, the three cable tiers (normal, heavy, super-heavy) are treated as " +
                     "three separate transmission voltages. A cable network must be all one tier; joining two tiers burns " +
                     "the lower-tier cable at the junction and splits the network. Generators and stationary batteries " +
                     "belong on heavy cable, the high-draw machines may use heavy or normal, super-heavy is the long-haul " +
                     "backbone (cables and transformers only), and everything else belongs on normal cable -- a device on " +
                     "the wrong tier is rejected at build time and receives no power if it slips through. Transformers and " +
                     "Area Power Controllers are exempt: they bridge whatever they are wired to.", 10));

            ExtraHeavyCableDevices = config.Bind("Server - Voltage Tiers", "Extra Heavy-Cable Devices", "",
                Desc("(Server-authoritative) Comma-separated list of extra device prefab names that should be allowed on " +
                     "heavy cable, on top of the built-in high-draw machines (Carbon Sequester, Furnace, Advanced Furnace, " +
                     "Arc Furnace, Centrifuge, Recycler, Ice Crusher, Hydraulic Pipe Bender, Deep Miner). Use this for " +
                     "modded high-draw machines. Names are matched against the device's PrefabName. Example: " +
                     "StructureBigMachine,StructureAnotherMachine", 20));
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
        }
    }
}
