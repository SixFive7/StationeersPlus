using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networking;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using HarmonyLib;
using Objects;
using Objects.Rockets;
using UnityEngine;

namespace PowerGridPlus
{
    /// <summary>
    ///     The three-tier transmission-voltage policy. The three cable tiers (normal, heavy,
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
                case "StructureRocketTransformerSmall":
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
        internal static bool BurnTransformerBothCables(Transformer transformer)
        {
            if (transformer == null || !GameManager.RunSimulation) return false;
            string label = string.IsNullOrEmpty(transformer.DisplayName) ? transformer.PrefabName : transformer.DisplayName;

            bool burned = false;
            var inputCable = transformer.InputConnection?.GetCable();
            if (inputCable != null)
            {
                Plugin.Log?.LogInfo(
                    $"Voltage tiers: burning a {inputCable.CableType} cable adjacent to {label} (transformer bridging incompatible tiers).");
                BurnReasonRegistry.RegisterPending(inputCable,
                    $"Wrong voltage -- {label} was bridging incompatible cable tiers");
                inputCable.Break();
                burned = true;
            }
            var outputCable = transformer.OutputConnection?.GetCable();
            if (outputCable != null)
            {
                Plugin.Log?.LogInfo(
                    $"Voltage tiers: burning a {outputCable.CableType} cable adjacent to {label} (transformer bridging incompatible tiers).");
                BurnReasonRegistry.RegisterPending(outputCable,
                    $"Wrong voltage -- {label} was bridging incompatible cable tiers");
                outputCable.Break();
                burned = true;
            }
            return burned;
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
            int myRank = TierRank(cable.CableType);
            try
            {
                // Per Power-bit OpenEnd, the same per-cell adjacency lookup as the cursor-reject path
                // (0.2.6403 removed the list-returning ConnectedCables overloads). Runs on the power-tick
                // worker thread: Connection.GetLocalGrid() returns the cached grid there (placed cables
                // are initialized), and the try/catch keeps any uninitialized-connection Transform
                // fallback a degraded "no" instead of a crash, as before.
                foreach (var openEnd in cable.OpenEnds)
                {
                    if (openEnd == null || !ConnectionCarriesPower(openEnd)) continue;
                    var n = SmallCell.Get<Cable>(openEnd.GetLocalGrid(), openEnd);
                    if (n == null || ReferenceEquals(n, cable)) continue;
                    if (TierRank(n.CableType) > myRank) return true;
                }
            }
            catch
            {
                return false;
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

        /// <summary>
        ///     True for either half of the rocket power umbilical pair. Both halves are heavy-cable-only,
        ///     like a stationary battery: the umbilical is a high-throughput transfer link, not an ordinary
        ///     device. (Male = the dockable side; Female / FemaleSide = the rocket-internal socket.)
        /// </summary>
        private static bool IsRocketPowerUmbilical(Device device) =>
            device is RocketPowerUmbilicalMale || device is RocketPowerUmbilicalFemale;

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
        ///     True when <paramref name="connection"/> carries the Power bit (Power or PowerAndData).
        ///     The voltage-tier rules apply ONLY to power-bearing connections; an exclusive data-only
        ///     port (NetworkType.Data) is invisible to tiering and accepts any cable tier. This is the
        ///     single shared predicate behind the data-only-port carve-out on the cursor side.
        /// </summary>
        internal static bool ConnectionCarriesPower(Connection connection)
        {
            return connection != null && (connection.ConnectionType & NetworkType.Power) != NetworkType.None;
        }

        /// <summary>
        ///     True when <paramref name="device"/> reaches <paramref name="network"/> through at least one
        ///     Power-bit port (one of its power cables sits on that network). The per-device tier rule must
        ///     only judge a device on a network it genuinely powers through: a network the device touches
        ///     ONLY via its exclusive data-only port imposes no tier requirement and must never trigger a
        ///     burn. The game's <c>CableNetwork.PowerDeviceList</c> is already filtered this way (built from
        ///     <c>Device.PowerCables</c>), so this is the explicit, regression-proof restatement of the
        ///     invariant the reactive enforcer relies on -- it guarantees a data-only port can never be
        ///     tier-judged even if a future game/mod change leaks a data-only device into PowerDeviceList.
        /// </summary>
        internal static bool ReachesNetworkViaPowerPort(Device device, CableNetwork network)
        {
            if (device == null || network == null)
                return false;
            var cables = device.PowerCables;
            if (cables == null)
                return false;
            for (int i = 0; i < cables.Length; i++)
                if (cables[i] != null && cables[i].CableNetwork == network)
                    return true;
            return false;
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
            // Third-party Force Field Door mod (Workshop 3328065049): pressure-driven power profile
            // ranges from 100 W idle to 100 kW under heavy load, which spans every cable tier. Treat
            // it as tier-agnostic by name so we do not take a hard reference on the mod assembly.
            if (device.GetType().FullName == "forcefielddoormod.ForceFieldDoor")
                return true;
            if (IsGenerator(device) || IsStationaryBattery(device) || IsRocketPowerUmbilical(device))
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
        internal static bool ResolveMixedTierNetwork(CableNetwork network, Cable preferVictim = null)
        {
            if (network == null || !GameManager.RunSimulation)
                return false;

            Cable victim = null;
            Cable.Type highestType = Cable.Type.normal;
            bool mixed = false;
            List<(Cable keep, Cable remove)> stacks = null;
            lock (network.CableList)
            {
                Cable.Type? firstType = null;
                int highestRank = 0;

                foreach (var cable in network.CableList)
                {
                    if (cable == null)
                        continue;

                    if (firstType == null)
                        firstType = cable.CableType;
                    else if (cable.CableType != firstType.Value)
                        mixed = true;

                    int rank = TierRank(cable.CableType);
                    if (rank > highestRank)
                    {
                        highestRank = rank;
                        highestType = cable.CableType;
                    }

                    // Stacked-theft scan, from the victim's side: a member cable whose own cell seats a
                    // DIFFERENT cable was stacked over by a cursor-less print (the victim's SmallCell
                    // back-pointer survives the overwrite; Research/GameClasses/SmallCell.md). The
                    // seated thief may belong to another network entirely; the pair is repairable
                    // either way. Only the unseated side reports, so each pair lands exactly once.
                    // A NULL seat is the thief-destroyed-while-seated aftermath (the reference-equality
                    // cell clear emptied the slot): an orphaned ghost, repaired by a plain re-seat
                    // (remove == null in the pair). Multi-cell straight variants carry the back-pointer
                    // of their LAST cell only, a documented limitation.
                    if (cable.IsBeingDestroyed)
                        continue;
                    var seated = cable.SmallCell != null ? cable.SmallCell.Cable : null;
                    if (cable.SmallCell != null && seated == null)
                    {
                        (stacks ?? (stacks = new List<(Cable, Cable)>())).Add((cable, null));
                    }
                    else if (seated != null && !ReferenceEquals(seated, cable)
                        && !seated.IsBeingDestroyed && seated.CableType != cable.CableType)
                    {
                        var keep = TierRank(seated.CableType) > TierRank(cable.CableType) ? seated : cable;
                        var remove = ReferenceEquals(keep, seated) ? cable : seated;
                        (stacks ?? (stacks = new List<(Cable, Cable)>())).Add((keep, remove));
                    }
                }
            }

            // Pass 1: stacked-cell repair. A stacked pair must NOT be resolved by Break(): destroying
            // whichever cable sits in the cell slot nulls the slot and orphans the survivor into an
            // untargetable ghost. Instead: seat the higher-tier cable in the cell FIRST (the removal's
            // reference-equality clear then leaves it alone), and remove the lower-tier one cleanly,
            // no rupture wreckage on top of a live cable. Runs even when this network itself is not
            // mixed (the thief can sit on another net).
            if (stacks != null)
            {
                foreach (var (keep, remove) in stacks)
                {
                    if (remove == null) RepairOrphanSeat(keep);
                    else RepairStackedCell(keep, remove);
                }
                return true;   // count changes when the destroys land (and the orphan rebuild mints a
                               // new network id); the split-pending gate holds the network until then,
                               // and the next pass burns any residual mixing.
            }

            if (!mixed)
                return false;

            lock (network.CableList)
            {
                float lowestRating = float.MaxValue;
                foreach (var cable in network.CableList)
                {
                    if (cable != null && cable.MaxVoltage < lowestRating)
                        lowestRating = cable.MaxVoltage;
                }

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
                PlayerConsole.Broadcast(
                    $"Illegal mixed-tier cable at {Coords(victim)}: burned {TierWord(victim.CableType)} cable joining a {TierWord(highestType)} network");
                victim.Break();
                return true;
            }
            return false;
        }

        /// <summary>
        ///     Repair one stacked cell (two different-tier cables at the same grid position). Seat the
        ///     higher-tier cable in the cell slot first, then destroy the lower-tier one cleanly (no
        ///     rupture wreckage: wreckage would co-locate with the surviving live cable, the exact
        ///     corruption the Luna_mixedwire forensics found persisted). With the survivor seated, the
        ///     removal's reference-equality cell clear is a no-op, so no ghost is created.
        /// </summary>
        private static void RepairStackedCell(Cable keep, Cable remove)
        {
            if (keep == null || remove == null || remove.IsBeingDestroyed)
                return;
            var cell = remove.SmallCell ?? keep.SmallCell;
            if (cell != null && !ReferenceEquals(cell.Cable, keep))
            {
                cell.Add(keep);
                keep.SmallCell = cell;
            }
            Plugin.Log?.LogInfo(
                $"Voltage tiers: removing a {remove.CableType} cable stacked on a {keep.CableType} cable at {Coords(remove)} (mixed-tier repair).");
            PlayerConsole.Broadcast(
                $"Illegal mixed-tier cable at {Coords(remove)}: removed {TierWord(remove.CableType)} cable stacked on a {TierWord(keep.CableType)} cable");
            OnServer.Destroy(remove);
        }

        /// <summary>
        ///     Repair one orphaned ghost: a live cable whose own cell slot is EMPTY (its stacked thief
        ///     was destroyed while seated, so the reference-equality clear emptied the slot instead of
        ///     restoring the victim). Grid-invisible cables drop out of every rebuild flood and freeze
        ///     in a stale network forever. Repair: seat the cable back into the cell the game itself
        ///     maps for its CURRENT position, then rebuild its network so it merges home. The
        ///     GetSmallCell identity check makes rocket-carried cables in transit a natural skip:
        ///     their position maps to no world cell.
        /// </summary>
        private static void RepairOrphanSeat(Cable orphan)
        {
            if (orphan == null || orphan.IsBeingDestroyed)
                return;
            var liveCell = GridController.World.GetSmallCell(orphan.ThingTransformPosition);
            if (liveCell == null)
            {
                Plugin.Log?.LogWarning(
                    $"Voltage tiers: orphaned {orphan.CableType} cable at {Coords(orphan)} has no mapped grid cell; leaving it (rocket transit or pruned cell).");
                return;
            }
            if (liveCell.Cable != null)
                return;   // reoccupied meanwhile (or a same-tier stack, which is not this repair's case)
            liveCell.Add(orphan);
            orphan.SmallCell = liveCell;
            Plugin.Log?.LogInfo(
                $"Voltage tiers: re-seated an orphaned {orphan.CableType} cable at {Coords(orphan)} and rebuilding its network.");
            PlayerConsole.Broadcast(
                $"Repaired orphaned {TierWord(orphan.CableType)} cable at {Coords(orphan)}: restored to its grid cell");
            CableNetwork.RebuildCableNetworkServer(orphan);
        }

        /// <summary>Player-facing tier word for console lines (plain text, no enum casing).</summary>
        internal static string TierWord(Cable.Type tier)
        {
            switch (tier)
            {
                case Cable.Type.superHeavy: return "super heavy";
                case Cable.Type.heavy: return "heavy";
                default: return "normal";
            }
        }

        /// <summary>
        ///     Player-facing world-meter coordinates, one decimal, dot separator. World meters is the
        ///     only coordinate convention the game surfaces to players (the IC10 PositionX/Y/Z values;
        ///     see Research/GameClasses/Grid3.md); cables sit on jitter-free 0.5 m cell centers.
        /// </summary>
        internal static string Coords(Thing thing)
        {
            var p = thing.ThingTransformPosition;
            return string.Format(CultureInfo.InvariantCulture, "({0:0.0}, {1:0.0}, {2:0.0})", p.x, p.y, p.z);
        }

        /// <summary>
        ///     A two-port device (transformer / APC) has a wrong-tier cable directly on one of its ports
        ///     (either the transformer's input or output port, or the APC's lower-tier side when its two
        ///     sides differ). Burns that specific cable -- it sits directly at the offending port via
        ///     <see cref="Assets.Scripts.Objects.Pipes.Connection.GetCable(bool)"/>, so the boundary is
        ///     correct by construction.
        /// </summary>
        internal static bool BurnPortMismatchCable(Cable victim, Device portOwner)
        {
            if (victim == null || portOwner == null || !GameManager.RunSimulation)
                return false;
            string label = string.IsNullOrEmpty(portOwner.DisplayName) ? portOwner.PrefabName : portOwner.DisplayName;
            Plugin.Log?.LogInfo(
                $"Voltage tiers: burning a {victim.CableType} cable adjacent to {label} (port-tier mismatch).");
            BurnReasonRegistry.RegisterPending(victim,
                $"Wrong voltage -- the adjacent {label} doesn't accept {victim.CableType} cable on this port");
            victim.Break();
            return true;
        }

        /// <summary>
        ///     A device on this network is not allowed on its tier; burn the cable that connects the device
        ///     to this specific <paramref name="network"/> so the network shrinks away from the device.
        /// </summary>
        internal static bool BurnCableForMisplacedDevice(Device device, CableNetwork network)
        {
            if (device == null || network == null || !GameManager.RunSimulation)
                return false;

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
                return false;

            string label = string.IsNullOrEmpty(device.DisplayName) ? device.PrefabName : device.DisplayName;
            Plugin.Log?.LogInfo(
                $"Voltage tiers: burning a {victim.CableType} cable adjacent to misplaced {label} (network {network.ReferenceId}).");
            BurnReasonRegistry.RegisterPending(victim,
                $"Wrong voltage -- the adjacent {label} doesn't accept {victim.CableType} cable");
            victim.Break();
            return true;
        }
    }
}
