using System.Collections.Generic;
using BepInEx.Configuration;

namespace PowerGridPlus
{
    /// <summary>
    ///     Central holder for every <see cref="ConfigEntry{T}"/> the mod binds. All settings are
    ///     server-authoritative: in multiplayer the host's values apply for everyone.
    ///
    ///     Always on, with no toggle: per-prefab battery rate limits, transformer priority +
    ///     shedding, transformer overload protection, rocket umbilical rate limits and the four
    ///     soft-power logic values, the allocator conservation check, mod-owned consumer Powered
    ///     (vanilla on/off coupling from the tick snapshot; the IC10 Power logic value serves net
    ///     liveness instead), the plain-consumer delivery shim, and logic passthrough for every
    ///     bridge kind (batteries, transformers, APCs, power transmitters, umbilicals).
    ///     Passthrough is controlled per device via its LogicPassthroughMode logic value; the
    ///     per-kind Passthrough Default settings below seed devices whose mode has never been set.
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

        // --- Server - Compatibility ---
        // The delivery shim's built-in five classes are always on (no toggle). This list
        // extends the shim for modded devices whose gameplay effect runs inside ReceivePower
        // (the load-time census log names candidates).
        internal static ConfigEntry<string> ExtraDeliveryDevices;

        // --- Server - Batteries ---
        // Rate limits are always enforced: each per-prefab cap applies, plus a per-device
        // cable-headroom cap so a single battery cannot exceed the rating of its own cable.
        internal static ConfigEntry<float> StationBatteryChargeRate;
        internal static ConfigEntry<float> StationBatteryDischargeRate;
        internal static ConfigEntry<float> LargeBatteryChargeRate;
        internal static ConfigEntry<float> LargeBatteryDischargeRate;
        internal static ConfigEntry<float> NuclearBatteryChargeRate;
        internal static ConfigEntry<float> NuclearBatteryDischargeRate;
        internal static ConfigEntry<float> RocketBatteryMediumChargeRate;
        internal static ConfigEntry<float> RocketBatteryMediumDischargeRate;
        internal static ConfigEntry<float> RocketBatterySmallChargeRate;
        internal static ConfigEntry<float> RocketBatterySmallDischargeRate;
        internal static ConfigEntry<float> BatteryChargeEfficiency;
        internal static ConfigEntry<bool> EnableBatteryLogicAdditions;
        internal static ConfigEntry<bool> BatteryPassthroughDefault;

        // --- Server - Transformers ---
        // The transformer free-power exploit mitigation (fresh-pull billing), the Priority +
        // Shedding system, and overload protection are always on (no toggles).
        internal static ConfigEntry<bool> EnableTransformerLogicAdditions;
        internal static ConfigEntry<bool> SmallTransformerPassthroughDefault;
        internal static ConfigEntry<bool> OtherTransformerPassthroughDefault;

        // --- Server - Area Power Control ---
        // The APC power-leak / idle-drain / cable-cap fix is always on (no toggle).
        internal static ConfigEntry<float> ApcBatteryChargeRate;
        internal static ConfigEntry<float> ApcBatteryDischargeRate;
        internal static ConfigEntry<bool> ApcPassthroughDefault;

        // --- Server - Power Transmitters ---
        internal static ConfigEntry<bool> PowerTransmitterPassthroughDefault;

        // --- Server - Rocket Umbilical ---
        // Umbilical rate limits and the four soft-power logic values are always on (no toggle).
        internal static ConfigEntry<int> RocketUmbilicalChargeRate;
        internal static ConfigEntry<int> RocketUmbilicalDischargeRate;
        internal static ConfigEntry<bool> UmbilicalPassthroughDefault;

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
            // cable's cap (Core/WriteBack + CableBurnWindow). Only the per-tier caps are configurable.
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

            // --- Server - Compatibility ---
            ExtraDeliveryDevices = config.Bind("Server - Compatibility", "Extra Delivery Devices", "",
                Desc("(Server-authoritative) Comma-separated list of extra device prefab names whose ReceivePower " +
                     "should be called with their granted power each tick, on top of the built-in set (Omni Power " +
                     "Transmitter, Suit Storage, Battery Cell Charger, Powered Bench, Wall Light Battery). Use this " +
                     "for modded devices whose gameplay effect (charging something, forwarding power) runs inside " +
                     "ReceivePower and that the load-time census log names as candidates. Names are matched against " +
                     "the device's PrefabName.", 10));
            ExtraDeliveryDevices.SettingChanged += (_, __) => DeliveryEffectClassifier.RefreshConfig();

            // --- Server - Batteries ---
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
                Desc("(Server-authoritative) Maximum charge wattage for the Nuclear Battery (StationBatteryNuclear, " +
                     "from the third-party MorePowerMod). No effect if MorePowerMod is not installed.", 60));

            NuclearBatteryDischargeRate = config.Bind("Server - Batteries", "Nuclear Battery Discharge Rate", 50000f,
                Desc("(Server-authoritative) Maximum discharge wattage for the Nuclear Battery (StationBatteryNuclear, " +
                     "from the third-party MorePowerMod). No effect if MorePowerMod is not installed.", 70));

            RocketBatteryMediumChargeRate = config.Bind("Server - Batteries", "Rocket Battery (Medium) Charge Rate", 5000f,
                Desc("(Server-authoritative) Maximum charge wattage for the rocket Battery (Medium) (StructureBatteryMedium). " +
                     "Per device, not per network. Capped further by the input cable's MaxVoltage. The rocket batteries are " +
                     "treated exactly like stationary batteries (heavy cable, logic passthrough); this gives them the same " +
                     "per-prefab rate cap.", 72));

            RocketBatteryMediumDischargeRate = config.Bind("Server - Batteries", "Rocket Battery (Medium) Discharge Rate", 10000f,
                Desc("(Server-authoritative) Maximum discharge wattage for the rocket Battery (Medium) (StructureBatteryMedium). " +
                     "Per device, not per network. Capped further by the output cable's MaxVoltage.", 74));

            RocketBatterySmallChargeRate = config.Bind("Server - Batteries", "Auxiliary Rocket Battery Charge Rate", 2500f,
                Desc("(Server-authoritative) Maximum charge wattage for the Auxiliary Rocket Battery (StructureBatterySmall). " +
                     "Per device, not per network. Capped further by the input cable's MaxVoltage.", 76));

            RocketBatterySmallDischargeRate = config.Bind("Server - Batteries", "Auxiliary Rocket Battery Discharge Rate", 5000f,
                Desc("(Server-authoritative) Maximum discharge wattage for the Auxiliary Rocket Battery (StructureBatterySmall). " +
                     "Per device, not per network. Capped further by the output cable's MaxVoltage.", 78));

            BatteryChargeEfficiency = config.Bind("Server - Batteries", "Battery Charge Efficiency", 1.5f,
                Desc("(Server-authoritative) Grid energy a stationary battery draws per unit of energy it stores. 1.0 is " +
                     "lossless; the default 1.5 means a battery stores two thirds of what it draws and the rest is lost " +
                     "(a future update turns the loss into heat). Values below 1.0 are treated as 1.0: a battery never " +
                     "stores more than it draws. Post-loss trickle charges below 500 W are stored in full regardless, so " +
                     "a battery can always top off.", 80));

            EnableBatteryLogicAdditions = config.Bind("Server - Batteries", "Enable Battery Logic Additions", true,
                Desc("(Server-authoritative) When true, stationary batteries expose four read-only logic values, in " +
                     "watts: Max Charge Speed and Max Discharge Speed (the configured per-prefab rate caps, bounded by " +
                     "the connected cable's tier cap) and Charge Speed and Discharge Speed (the actual per-tick rates " +
                     "the power allocator granted this tick). When false, none of the four values is readable.", 90));

            BatteryPassthroughDefault = config.Bind("Server - Batteries", "Battery Passthrough Default", true,
                Desc("(Server-authoritative) The logic-passthrough mode a stationary battery starts with: applies to a " +
                     "newly built battery and to any battery whose mode has never been set (for example an existing save " +
                     "running this mod for the first time). On = logic-transparent (devices on either cable side are " +
                     "visible across), off = vanilla-opaque. A battery's own LogicPassthroughMode logic value (writable " +
                     "via IC10 or a logic writer, persisted with the save) overrides this once set. Passthrough support " +
                     "itself is always on; there is no master kill-switch.", 100));

            // --- Server - Transformers ---
            EnableTransformerLogicAdditions = config.Bind("Server - Transformers", "Enable Transformer Logic Additions", true,
                Desc("(Server-authoritative) When true, transformers expose their current throughput as the Power Actual " +
                     "logic value.", 20));

            SmallTransformerPassthroughDefault = config.Bind("Server - Transformers", "Small Transformer Passthrough Default", true,
                Desc("(Server-authoritative) The logic-passthrough mode a small transformer (StructureTransformerSmall, " +
                     "StructureTransformerSmallReversed, StructureRocketTransformerSmall) starts with: applies to a newly " +
                     "built small transformer and to any small transformer whose mode has never been set (for example an " +
                     "existing save running this mod for the first time). On = logic-transparent (devices on either side " +
                     "are visible across), off = vanilla-opaque. A transformer's own LogicPassthroughMode logic value " +
                     "(writable via IC10 or a logic writer, persisted with the save) overrides this once set. Passthrough " +
                     "support itself is always on; there is no master kill-switch.", 30));

            OtherTransformerPassthroughDefault = config.Bind("Server - Transformers", "Other Transformer Passthrough Default", false,
                Desc("(Server-authoritative) The logic-passthrough mode every transformer variant other than the three " +
                     "small-transformer prefabs starts with: applies to a newly built transformer and to any transformer " +
                     "whose mode has never been set (for example an existing save running this mod for the first time). " +
                     "On = logic-transparent (devices on either side are visible across), off = vanilla-opaque. A " +
                     "transformer's own LogicPassthroughMode logic value (writable via IC10 or a logic writer, persisted " +
                     "with the save) overrides this once set. Passthrough support itself is always on; there is no master " +
                     "kill-switch.", 35));

            // --- Server - Area Power Control ---
            ApcBatteryChargeRate = config.Bind("Server - Area Power Control", "APC Battery Charge Rate", 1000f,
                Desc("(Server-authoritative) Maximum wattage an APC pulls from upstream to charge its internal cell. " +
                     "Per device, not per network. Capped further by the input cable's remaining headroom after the " +
                     "APC's own downstream pass-through is subtracted, so a single APC can never blow its own cable " +
                     "just by charging on top of what it is already passing through. Vanilla default is 1000.", 15));

            ApcBatteryDischargeRate = config.Bind("Server - Area Power Control", "APC Battery Discharge Rate", 1000f,
                Desc("(Server-authoritative) Maximum wattage the APC's inserted battery cell can discharge per tick to the " +
                     "output network. Per device, not per network. Capped further by the output cable's MaxVoltage. The " +
                     "elastic-supply allocator discharges the cell only to fill the output network's shortfall, never more.", 17));

            ApcPassthroughDefault = config.Bind("Server - Area Power Control", "APC Passthrough Default", true,
                Desc("(Server-authoritative) The logic-passthrough mode an Area Power Control starts with: applies to a " +
                     "newly built APC and to any APC whose mode has never been set (for example an existing save running " +
                     "this mod for the first time). On = logic-transparent (devices on either cable side are visible " +
                     "across, and the APC's own logic ports are visible from both), off = vanilla-opaque, where the APC " +
                     "breaks the logic network the same way it breaks the power network. The APC's mode is currently " +
                     "seeded from this setting only (the APC does not yet expose a writable LogicPassthroughMode logic " +
                     "port); it still persists with the save once set by the mod. Passthrough support itself is always " +
                     "on; there is no master kill-switch. Power is unaffected either way: the APC's downstream side " +
                     "always meters and gates power normally.", 20));

            // --- Server - Power Transmitters ---
            PowerTransmitterPassthroughDefault = config.Bind("Server - Power Transmitters", "Power Transmitter Passthrough Default", true,
                Desc("(Server-authoritative) The logic-passthrough mode a wireless power dish starts with, covering both " +
                     "ends of a link (PowerTransmitter and PowerReceiver): applies to a newly built dish and to any dish " +
                     "whose mode has never been set (for example an existing save running this mod for the first time). " +
                     "On = logic-transparent (a reader wired to the transmitter's cable network sees devices on the " +
                     "receiver's network, and vice versa), off = vanilla-opaque. Bridging requires the pair to be linked; " +
                     "an unlinked dish has nothing to bridge to. A dish's own LogicPassthroughMode logic value (writable " +
                     "via IC10 or a logic writer, persisted with the save) overrides this once set. Passthrough support " +
                     "itself is always on; there is no master kill-switch.", 10));

            // --- Server - Rocket Umbilical ---
            RocketUmbilicalChargeRate = config.Bind("Server - Rocket Umbilical", "Rocket Umbilical Charge Rate", 10000,
                Desc("(Server-authoritative) Maximum Watts the rocket umbilical pulls from upstream per tick to charge its " +
                     "internal cell. Capped further by the input cable's MaxVoltage. Default 10000 matches the vanilla " +
                     "umbilical cell PowerMaximum.", 20));

            RocketUmbilicalDischargeRate = config.Bind("Server - Rocket Umbilical", "Rocket Umbilical Discharge Rate", 10000,
                Desc("(Server-authoritative) Maximum Watts the rocket umbilical discharges per tick to the output network. " +
                     "Capped further by the output cable's MaxVoltage. Default 10000 matches the vanilla umbilical cell " +
                     "PowerMaximum.", 30));

            UmbilicalPassthroughDefault = config.Bind("Server - Rocket Umbilical", "Umbilical Passthrough Default", true,
                Desc("(Server-authoritative) The logic-passthrough mode a rocket power umbilical half starts with, " +
                     "covering both halves (Male and Socket): applies to a newly built umbilical and to any umbilical " +
                     "whose mode has never been set (for example an existing save running this mod for the first time). " +
                     "On = logic-transparent (a docked pair carries logic as if the two halves were one wire: a reader on " +
                     "the rocket-internal grid sees devices on the external grid and vice versa), off = vanilla-opaque. " +
                     "Bridging requires the pair to be docked; an undocked umbilical bridges nothing. A half's own " +
                     "LogicPassthroughMode logic value (writable via IC10 or a logic writer, persisted with the save; " +
                     "writing one half mirrors the value to its docked partner) overrides this once set. Passthrough " +
                     "support itself is always on; there is no master kill-switch.", 40));

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
            EmergencyLightPrefabs.SettingChanged += (_, __) => Patches.EmergencyLightSupport.RefreshConfig();
        }
    }
}
