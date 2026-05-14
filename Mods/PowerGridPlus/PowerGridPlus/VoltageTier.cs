using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using HarmonyLib;
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
    ///       * Area Power Control -- any T, but both cable ports must be on the same tier. If they
    ///         differ, the lower-tier port's adjacent cable burns when power flows on the relevant
    ///         network. Wireless power devices (PowerTransmitter, PowerReceiver, PowerTransmitterOmni)
    ///         only have one cable-wired port in practice; the rule is vacuous on them.
    ///       * Transformer -- per variant, the two cable ports must hold a canonical UNORDERED pair of
    ///         tiers. Direction is interchangeable, so a Small can be wired heavy-in/normal-out OR
    ///         normal-in/heavy-out (the latter is the "step-up" / generation-side wiring).
    ///           - Small (StructureTransformerSmall, StructureTransformerSmallReversed): {heavy, normal}.
    ///           - Medium (StructureTransformerMedium, StructureTransformerMedium(Reversed),
    ///             StructureTransformerMediumReversed): {heavy, heavy}.
    ///           - Large (StructureTransformer): {superHeavy, heavy}.
    ///         The reactive burn for a transformer-tier violation fires ONLY when the transformer is
    ///         turned on AND actively bridging power (Transformer._powerProvided > 0 -- both sides
    ///         have flow through the transformer). When triggered, BOTH adjacent cables burn. The
    ///         transformer itself is never destroyed (it's expensive to build); the cables are the
    ///         signal. A violated-but-off or violated-but-half-powered transformer sits harmlessly
    ///         until the player flips it on with both sides drawing.
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
        ///     True iff the transformer's two cable ports together violate its per-variant unordered
        ///     pair-of-tiers rule. The pair is direction-agnostic: a Small accepts heavy/normal OR
        ///     normal/heavy. A Medium (symmetric pair) accepts only heavy/heavy. A Large accepts
        ///     superHeavy/heavy OR heavy/superHeavy. Violation if (a) any wired port has a tier outside
        ///     the pair set, or (b) on an asymmetric variant, both wired ports share the same tier
        ///     (meaning the pair is incomplete -- the other tier of the pair is missing).
        ///     Returns null when there's no violation (or when only one side is wired and that side's
        ///     tier is in the pair; the other side could still be wired correctly later).
        /// </summary>
        internal static bool IsTransformerTierViolated(Transformer transformer)
        {
            if (transformer == null) return false;
            var map = GetTransformerTierMap(transformer.PrefabName);
            if (!map.HasValue) return false;

            var inputCable = transformer.InputConnection?.GetCable();
            var outputCable = transformer.OutputConnection?.GetCable();

            var tierA = map.Value.Input;
            var tierB = map.Value.Output;
            bool symmetric = tierA == tierB;

            if (symmetric)
            {
                // Medium: both ports must be the canonical tier.
                if (inputCable != null && inputCable.CableType != tierA) return true;
                if (outputCable != null && outputCable.CableType != tierA) return true;
                return false;
            }

            // Asymmetric (Small, Large): each individually-wired cable must be in {tierA, tierB},
            // and when both are wired they must be different (otherwise the pair has duplicates and
            // is missing the other tier).
            if (inputCable != null && inputCable.CableType != tierA && inputCable.CableType != tierB)
                return true;
            if (outputCable != null && outputCable.CableType != tierA && outputCable.CableType != tierB)
                return true;
            if (inputCable != null && outputCable != null && inputCable.CableType == outputCable.CableType)
                return true;
            return false;
        }

        // Reflection accessor for Transformer._powerProvided. Set by Transformer.ReceivePower
        // during the previous tick's ApplyState_New; positive means the transformer was bridging
        // real power flow last tick. One-tick latency vs the "first tick power flows" framing is
        // acceptable -- the player won't notice and after the burn fires the cables are gone.
        private static readonly FieldInfo _transformerPowerProvidedField =
            AccessTools.Field(typeof(Transformer), "_powerProvided");

        /// <summary>
        ///     True when this transformer is actively bridging power: turned on AND its previous
        ///     tick's throughput was positive. This is the "both sides have real flow through the
        ///     transformer" gate for the burn-both-cables rule.
        /// </summary>
        internal static bool IsTransformerActivelyConducting(Transformer transformer)
        {
            if (transformer == null || _transformerPowerProvidedField == null) return false;
            if (!transformer.OnOff) return false;
            object raw = _transformerPowerProvidedField.GetValue(transformer);
            return raw is float v && v > 0f;
        }

        /// <summary>
        ///     The transformer is violating its tier-pair rule AND is actively bridging power. Burn
        ///     BOTH adjacent cables (input port AND output port) so the bridge splits on both sides.
        ///     The transformer itself is never destroyed -- only the cables, which are the player
        ///     signal. Idempotent: calling Break on an already-destroyed cable is a safe no-op via
        ///     Unity's "destroyed object compares equal to null" overload.
        /// </summary>
        internal static void BurnTransformerBothCables(Transformer transformer)
        {
            if (transformer == null || !GameManager.RunSimulation) return;
            string label = string.IsNullOrEmpty(transformer.DisplayName) ? transformer.PrefabName : transformer.DisplayName;

            var inputCable = transformer.InputConnection?.GetCable();
            if (inputCable != null)
            {
                Plugin.Log?.LogInfo(
                    $"Voltage tiers: burning a {inputCable.CableType} cable adjacent to {label} (transformer bridging incompatible tiers).");
                BurnReasonRegistry.RegisterPending(inputCable,
                    $"Wrong voltage -- {label} was bridging incompatible cable tiers");
                inputCable.Break();
            }
            var outputCable = transformer.OutputConnection?.GetCable();
            if (outputCable != null)
            {
                Plugin.Log?.LogInfo(
                    $"Voltage tiers: burning a {outputCable.CableType} cable adjacent to {label} (transformer bridging incompatible tiers).");
                BurnReasonRegistry.RegisterPending(outputCable,
                    $"Wrong voltage -- {label} was bridging incompatible cable tiers");
                outputCable.Break();
            }
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
