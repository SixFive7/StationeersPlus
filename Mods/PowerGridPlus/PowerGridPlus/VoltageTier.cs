using System;
using System.Collections.Generic;
using System.Linq;
using Assets.Scripts;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Objects;

namespace PowerGridPlus
{
    /// <summary>
    ///     The three-tier transmission-voltage policy (NEW-3). The three cable tiers (normal, heavy,
    ///     super-heavy) are three separate voltages: a cable network must be single-tier, and the only
    ///     legal bridge between tiers is a transformer (or Area Power Controller) -- whose input and
    ///     output sit on separate networks anyway, so they need no enforcement.
    ///
    ///     Per-device tier rules (a single-tier network's tier T):
    ///       * Transformer / Area Power Controller / wireless power devices -- any T (exempt).
    ///       * Electrical generators -- T must be heavy.
    ///       * Stationary batteries -- T must be heavy.
    ///       * High-draw machines (Carbon Sequester, the furnaces, Centrifuge, Recycler, Ice Crusher,
    ///         Hydraulic Pipe Bender, Deep Miner, plus the Extra Heavy-Cable Devices config list) -- T
    ///         must be heavy or normal (not super-heavy).
    ///       * Everything else -- T must be normal.
    ///     A device on a tier it is not allowed on is rejected at build time (best effort) and produces /
    ///     draws no power at the simulation level.
    ///
    ///     Enforcement of the cable-tier rule has two halves: a reactive backstop (a network holding more
    ///     than one tier burns its lowest-tier boundary cable, splitting it -- this fires on cable
    ///     register and on power-tick rebuild, both cheap) and a best-effort build-time placement
    ///     rejection.
    /// </summary>
    internal static class VoltageTier
    {
        private static HashSet<string> _extraHeavyDevices;

        internal static void RefreshConfig()
        {
            var raw = Settings.ExtraHeavyCableDevices?.Value ?? string.Empty;
            _extraHeavyDevices = new HashSet<string>(
                raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()),
                StringComparer.OrdinalIgnoreCase);
        }

        private static HashSet<string> ExtraHeavyDevices
        {
            get
            {
                if (_extraHeavyDevices == null)
                    RefreshConfig();
                return _extraHeavyDevices;
            }
        }

        /// <summary>True if this device is an electrical generator that belongs on heavy cable.</summary>
        internal static bool IsGenerator(Device device)
        {
            if (device == null)
                return false;

            // Solar panels and (large) wind turbines implement IPowerGenerator; the fuel generators,
            // the RTG, the gas-pipe turbine and the Stirling engine do not, so they are named explicitly.
            return device is IPowerGenerator
                   || device is TurbineGenerator
                   || device is StirlingEngine
                   || device is PowerGeneratorSlot          // covers SolidFuelGenerator
                   || device is PowerGeneratorPipe          // covers GasFuelGenerator
                   || device is RadioscopicThermalGenerator;
        }

        /// <summary>True for transformers / APCs / wireless power devices -- these are exempt from tier restrictions.</summary>
        private static bool IsTierExempt(Device device)
        {
            return device is Transformer
                   || device is AreaPowerControl
                   || device is WirelessPower            // covers PowerReceiver and PowerTransmitter
                   || device is PowerTransmitterOmni;
        }

        private static bool IsStationaryBattery(Device device) => device is Battery;

        /// <summary>True for the high-draw machines that are allowed on heavy cable (in addition to normal).</summary>
        private static bool IsHighDrawMachine(Device device)
        {
            if (device == null)
                return false;

            if (device is CarbonSequester
                || device is FurnaceBase                 // covers Furnace and AdvancedFurnace
                || device is ArcFurnace
                || device is Centrifuge
                || device is Recycler
                || device is IceCrusher
                || device is HydraulicPipeBender
                || device is DeepMiner)                  // covers CombustionDeepMiner
                return true;

            var prefabName = device.PrefabName;
            return !string.IsNullOrEmpty(prefabName) && ExtraHeavyDevices.Contains(prefabName);
        }

        /// <summary>
        ///     Is <paramref name="device"/> allowed to be powered by a single-tier network of tier
        ///     <paramref name="tier"/>? (When the network is cableless -- e.g. a power transmitter
        ///     relay -- callers pass tier as null and should treat that as "allowed".)
        /// </summary>
        internal static bool IsAllowedOnTier(Device device, Cable.Type tier)
        {
            if (device == null)
                return true;
            if (IsTierExempt(device))
                return true;
            if (IsGenerator(device) || IsStationaryBattery(device))
                return tier == Cable.Type.heavy;
            if (IsHighDrawMachine(device))
                return tier == Cable.Type.heavy || tier == Cable.Type.normal;
            // Ordinary device: normal cable only.
            return tier == Cable.Type.normal;
        }

        /// <summary>Does the given network contain at least one cable, and is every cable in it heavy?</summary>
        internal static bool IsAllHeavyNetwork(CableNetwork network)
        {
            if (network == null)
                return false;

            bool sawCable = false;
            lock (network.CableList)
            {
                foreach (var cable in network.CableList)
                {
                    if (cable == null)
                        continue;
                    sawCable = true;
                    if (cable.CableType != Cable.Type.heavy)
                        return false;
                }
            }

            return sawCable;
        }

        /// <summary>
        ///     If <paramref name="network"/> holds more than one cable tier, burn one lowest-tier cable so
        ///     the network re-floods and splits. <paramref name="preferVictim"/>, when it is itself a
        ///     lowest-tier cable in the network, is burned in preference (this is the cable the player just
        ///     placed -- burning it gives the clearest feedback).
        /// </summary>
        internal static void ResolveMixedTierNetwork(CableNetwork network, Cable preferVictim = null)
        {
            if (network == null || !GameManager.RunSimulation)
                return;

            Cable victim = null;
            lock (network.CableList)
            {
                Cable.Type? firstType = null;
                bool mixed = false;
                float lowestRating = float.MaxValue;

                foreach (var cable in network.CableList)
                {
                    if (cable == null)
                        continue;

                    if (firstType == null)
                        firstType = cable.CableType;
                    else if (cable.CableType != firstType.Value)
                        mixed = true;

                    if (cable.MaxVoltage < lowestRating)
                        lowestRating = cable.MaxVoltage;
                }

                if (!mixed)
                    return;

                if (preferVictim != null && preferVictim.CableNetwork == network && preferVictim.MaxVoltage <= lowestRating)
                {
                    victim = preferVictim;
                }
                else
                {
                    foreach (var cable in network.CableList)
                    {
                        if (cable == null)
                            continue;
                        if (cable.MaxVoltage <= lowestRating)
                        {
                            victim = cable;
                            break;
                        }
                    }
                }
            }

            if (victim != null)
            {
                Plugin.Log?.LogInfo($"Voltage tiers: burning a {victim.CableType} cable to split a mixed-tier network ({network.ReferenceId}).");
                victim.Break();
            }
        }
    }
}
