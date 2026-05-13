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
        internal static ConfigEntry<float> MaxBatteryChargeRate;
        internal static ConfigEntry<float> MaxBatteryDischargeRate;
        internal static ConfigEntry<float> BatteryChargeEfficiency;
        internal static ConfigEntry<bool> EnableBatteryLogicAdditions;

        // --- Server - Transformers ---
        internal static ConfigEntry<bool> EnableTransformerExploitMitigation;
        internal static ConfigEntry<bool> EnableTransformerLogicAdditions;

        // --- Server - Area Power Control ---
        internal static ConfigEntry<bool> EnableAreaPowerControlFix;

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
                Desc("(Server-authoritative) When true, stationary batteries are charge- and discharge-rate limited (a " +
                     "battery cannot dump or absorb its full capacity in one tick).", 10));

            MaxBatteryChargeRate = config.Bind("Server - Batteries", "Max Battery Charge Rate", 0.002f,
                Desc("(Server-authoritative) Maximum stationary-battery charge rate per tick, as a fraction of the " +
                     "battery's maximum stored energy. Only used when Enable Battery Limits is on.", 20));

            MaxBatteryDischargeRate = config.Bind("Server - Batteries", "Max Battery Discharge Rate", 0.007f,
                Desc("(Server-authoritative) Maximum stationary-battery discharge rate per tick, as a fraction of the " +
                     "battery's maximum stored energy. Only used when Enable Battery Limits is on.", 30));

            BatteryChargeEfficiency = config.Bind("Server - Batteries", "Battery Charge Efficiency", 1.0f,
                Desc("(Server-authoritative) Fraction of incoming power a stationary battery actually stores. 1.0 is " +
                     "lossless; lower it to lose energy to charging inefficiency. (Trickle charges below 500 W are stored " +
                     "in full regardless, to avoid a battery never topping off.)", 40));

            EnableBatteryLogicAdditions = config.Bind("Server - Batteries", "Enable Battery Logic Additions", true,
                Desc("(Server-authoritative) When true, stationary batteries expose their max charge rate (Import Quantity) " +
                     "and max discharge rate (Export Quantity) as logic values.", 50));

            // --- Server - Transformers ---
            EnableTransformerExploitMitigation = config.Bind("Server - Transformers", "Enable Transformer Exploit Mitigation", true,
                Desc("(Server-authoritative) When true, transformers no longer leak free power and charge their own " +
                     "quiescent draw to the upstream network.", 10));

            EnableTransformerLogicAdditions = config.Bind("Server - Transformers", "Enable Transformer Logic Additions", true,
                Desc("(Server-authoritative) When true, transformers expose their current throughput as the Power Actual " +
                     "logic value.", 20));

            // --- Server - Area Power Control ---
            EnableAreaPowerControlFix = config.Bind("Server - Area Power Control", "Enable APC Power Fix", true,
                Desc("(Server-authoritative) When true, Area Power Controllers no longer leak a small amount of power and " +
                     "no longer slowly drain their battery when nothing is connected downstream.", 10));
        }
    }
}
