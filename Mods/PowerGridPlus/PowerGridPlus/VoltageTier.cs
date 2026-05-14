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
    ///     super-heavy) are three separate voltages: a cable network must be single-tier.
    ///
    ///     Per-device tier rules (a single-tier network's tier T):
    ///       * Electrical generators -- T must be heavy.
    ///       * Stationary batteries -- T must be heavy.
    ///       * High-draw machines (Carbon Sequester, the furnaces, Centrifuge, Recycler, Ice Crusher,
    ///         Hydraulic Pipe Bender, Deep Miner, plus the Extra Heavy-Cable Devices config list) -- T
    ///         must be heavy or normal (not super-heavy).
    ///       * Area Power Control and wireless power devices -- any T, but the two cable ports (if both
    ///         carry cable) must be on the same tier. If they differ, the lower-tier port's adjacent
    ///         cable burns when power flows.
    ///       * Transformer (per-variant) -- explicit input / output tier requirement:
    ///           - Small (StructureTransformerSmall, StructureTransformerSmallReversed): input = heavy,
    ///             output = normal.
    ///           - Medium (StructureTransformerMedium, StructureTransformerMedium(Reversed),
    ///             StructureTransformerMediumReversed): input = heavy, output = heavy.
    ///           - Large (StructureTransformer): input = superHeavy, output = heavy.
    ///         If a port has the wrong-tier cable, that adjacent cable burns when power flows.
    ///       * Everything else -- T must be normal.
    ///     A device on a tier it is not allowed on burns the adjacent cable when power flows. Placing
    ///     the device itself is never blocked at the cursor; placing a wrong-tier cable next to an
    ///     existing device IS blocked at the cursor.
    ///
    ///     Enforcement has two halves: a reactive backstop (the power tick burns the offending cable
    ///     when power flows; an idle network destroys nothing) and a best-effort build-time cursor
    ///     rejection for cables placed adjacent to wrong-tier devices.
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

        /// <summary>
        ///     True for devices that are exempt from the single-tier per-device rule because they have
        ///     their own per-port rules (transformer, APC) or only sit on one cable side (wireless power).
        ///     The per-port rules are enforced by <see cref="FindMismatchedTransformerCable"/> and
        ///     <see cref="FindMismatchedApcCable"/>, called from the power tick.
        /// </summary>
        private static bool IsTierExempt(Device device)
        {
            return device is Transformer
                   || device is AreaPowerControl
                   || device is WirelessPower            // covers PowerReceiver and PowerTransmitter
                   || device is PowerTransmitterOmni;
        }

        /// <summary>
        ///     Per-variant transformer port-tier requirements. Returns (inputTier, outputTier) or null
        ///     when the prefab is not a known transformer variant (e.g. modded transformers).
        /// </summary>
        internal static (Cable.Type Input, Cable.Type Output)? GetTransformerTierMap(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return null;
            switch (prefabName)
            {
                case "StructureTransformerSmall":
                case "StructureTransformerSmallReversed":
                    return (Cable.Type.heavy, Cable.Type.normal);
                case "StructureTransformerMedium":
                case "StructureTransformerMedium(Reversed)":
                case "StructureTransformerMediumReversed":
                    return (Cable.Type.heavy, Cable.Type.heavy);
                case "StructureTransformer":
                    return (Cable.Type.superHeavy, Cable.Type.heavy);
                default:
                    return null;
            }
        }

        /// <summary>
        ///     For a transformer with a known per-variant tier map: returns the wrong-tier cable
        ///     adjacent to a port, or null if both ports are correct (or no map / no cables).
        ///     The cable returned is the one to burn -- it sits directly at the offending port via
        ///     <see cref="Assets.Scripts.Objects.Pipes.Connection.GetCable(bool)"/>.
        /// </summary>
        internal static Cable FindMismatchedTransformerCable(Transformer transformer)
        {
            if (transformer == null) return null;
            var map = GetTransformerTierMap(transformer.PrefabName);
            if (!map.HasValue) return null;

            var inputCable = transformer.InputConnection?.GetCable();
            if (inputCable != null && inputCable.CableType != map.Value.Input)
                return inputCable;

            var outputCable = transformer.OutputConnection?.GetCable();
            if (outputCable != null && outputCable.CableType != map.Value.Output)
                return outputCable;

            return null;
        }

        /// <summary>
        ///     For an APC: both cable ports must be on the same tier. Returns the lower-tier port's
        ///     adjacent cable when they differ, or null if matching (or either port is uncabled).
        /// </summary>
        internal static Cable FindMismatchedApcCable(AreaPowerControl apc)
        {
            if (apc == null) return null;
            var inputCable = apc.InputConnection?.GetCable();
            var outputCable = apc.OutputConnection?.GetCable();
            if (inputCable == null || outputCable == null) return null;
            if (inputCable.CableType == outputCable.CableType) return null;
            return TierRank(inputCable.CableType) < TierRank(outputCable.CableType) ? inputCable : outputCable;
        }

        /// <summary>
        ///     True when this cable has at least one grid-adjacent cable of a higher tier (used to
        ///     identify boundary cables in a mixed-tier network). Cheap: a few neighbours per cable.
        /// </summary>
        private static bool HasHigherTierNeighbour(Cable cable)
        {
            if (cable == null) return false;
            List<Cable> neighbours;
            try
            {
                neighbours = cable.ConnectedCables(NetworkType.Power);
            }
            catch
            {
                return false;
            }
            if (neighbours == null) return false;
            int myRank = TierRank(cable.CableType);
            foreach (var n in neighbours)
            {
                if (n == null) continue;
                if (TierRank(n.CableType) > myRank) return true;
            }
            return false;
        }

        private static int TierRank(Cable.Type t)
        {
            switch (t)
            {
                case Cable.Type.normal: return 1;
                case Cable.Type.heavy: return 2;
                case Cable.Type.superHeavy: return 3;
                default: return 0;
            }
        }

        /// <summary>
        ///     Returns the uniform <see cref="Cable.Type"/> of a network, or null if the network is mixed
        ///     or empty. Used by the cursor-reject path to read an APC's existing-side tier without
        ///     having to figure out which Connection the cursor cable would attach to.
        /// </summary>
        internal static Cable.Type? GetUniformTier(CableNetwork network)
        {
            if (network == null) return null;
            Cable.Type? type = null;
            lock (network.CableList)
            {
                foreach (var cable in network.CableList)
                {
                    if (cable == null) continue;
                    if (!type.HasValue) type = cable.CableType;
                    else if (type.Value != cable.CableType) return null;
                }
            }
            return type;
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
                    // First pass: pick a lowest-tier cable that is grid-adjacent to a higher-tier cable
                    // (the actual boundary -- "burn next to the higher tier cable, not somewhere else").
                    foreach (var cable in network.CableList)
                    {
                        if (cable == null) continue;
                        if (cable.MaxVoltage > lowestRating) continue;
                        if (HasHigherTierNeighbour(cable))
                        {
                            victim = cable;
                            break;
                        }
                    }
                    // Fallback: any lowest-tier cable (only reached when the grid-adjacency check yields
                    // nothing -- typically a torn network mid-rebuild).
                    if (victim == null)
                    {
                        foreach (var cable in network.CableList)
                        {
                            if (cable == null) continue;
                            if (cable.MaxVoltage <= lowestRating)
                            {
                                victim = cable;
                                break;
                            }
                        }
                    }
                }
            }

            if (victim != null)
            {
                Plugin.Log?.LogInfo($"Voltage tiers: burning a {victim.CableType} cable to split a mixed-tier network ({network.ReferenceId}).");
                BurnReasonRegistry.RegisterPending(victim,
                    $"Wrong voltage -- {victim.CableType} cable was bridging into a different cable tier");
                victim.Break();
            }
        }

        /// <summary>
        ///     A two-port device (transformer / APC) has a wrong-tier cable directly on one of its ports
        ///     (either the transformer's input or output port, or the APC's lower-tier side when its two
        ///     sides differ). Burns that specific cable -- it sits directly at the offending port via
        ///     <see cref="Assets.Scripts.Objects.Pipes.Connection.GetCable(bool)"/>, so the boundary is
        ///     correct by construction.
        /// </summary>
        internal static void BurnPortMismatchCable(Cable victim, Device portOwner)
        {
            if (victim == null || portOwner == null || !GameManager.RunSimulation)
                return;
            string label = string.IsNullOrEmpty(portOwner.DisplayName) ? portOwner.PrefabName : portOwner.DisplayName;
            Plugin.Log?.LogInfo(
                $"Voltage tiers: burning a {victim.CableType} cable adjacent to {label} (port-tier mismatch).");
            BurnReasonRegistry.RegisterPending(victim,
                $"Wrong voltage -- the adjacent {label} doesn't accept {victim.CableType} cable on this port");
            victim.Break();
        }

        /// <summary>
        ///     A device on this network is not allowed on its tier; burn the cable that connects the device
        ///     to this specific <paramref name="network"/> so the network shrinks away from the device.
        /// </summary>
        internal static void BurnCableForMisplacedDevice(Device device, CableNetwork network)
        {
            if (device == null || network == null || !GameManager.RunSimulation)
                return;

            Cable victim = null;
            // device.PowerCable is the primary; check it first.
            if (device.PowerCable != null && device.PowerCable.CableNetwork == network)
                victim = device.PowerCable;
            else if (device.PowerCables != null)
            {
                foreach (var c in device.PowerCables)
                {
                    if (c != null && c.CableNetwork == network)
                    {
                        victim = c;
                        break;
                    }
                }
            }

            if (victim == null)
                return;

            string label = string.IsNullOrEmpty(device.DisplayName) ? device.PrefabName : device.DisplayName;
            Plugin.Log?.LogInfo(
                $"Voltage tiers: burning a {victim.CableType} cable adjacent to misplaced {label} (network {network.ReferenceId}).");
            BurnReasonRegistry.RegisterPending(victim,
                $"Wrong voltage -- the adjacent {label} doesn't accept {victim.CableType} cable");
            victim.Break();
        }
    }
}
