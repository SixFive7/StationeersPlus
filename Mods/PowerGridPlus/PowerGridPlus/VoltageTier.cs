using System.Collections.Generic;
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
    ///     legal bridge between tiers is a transformer (whose input and output sit on separate networks
    ///     anyway, so no enforcement is needed there). All electrical generators belong on heavy cable.
    ///
    ///     Enforcement has two halves:
    ///       * a reactive backstop -- when a network ends up holding more than one tier, the lowest-tier
    ///         cable at the junction is burned, which splits the network back into single-tier pieces; and
    ///       * a build-time placement rejection (best effort) so the player sees the error before placing.
    ///
    ///     This v1 is deliberately coarse: it does not yet distinguish the small / medium / large
    ///     transformer prefabs by which tier pair they may bridge, and the "burn the cable closest to the
    ///     junction" heuristic only special-cases the cable that triggered the merge. Both are tracked in
    ///     the mod TODO.
    /// </summary>
    internal static class VoltageTier
    {
        /// <summary>True if this device is an electrical generator that, under NEW-3, belongs on heavy cable.</summary>
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
