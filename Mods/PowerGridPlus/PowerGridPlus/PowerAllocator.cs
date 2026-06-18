using System;
using System.Collections.Generic;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using LaunchPadBooster.Networking;
using Objects.Rockets;
using UnityEngine;

namespace PowerGridPlus
{
    /// <summary>
    ///     ALLOCATE of the atomic tick: the joint shed / overload / elastic-supply / surplus allocator
    ///     (POWER.md §8.0 / §7.3 / §9). Replaces the former transformer-only TransformerAllocator with
    ///     a model that covers every segmenting device class (§8.0.0.1): Transformer, AreaPowerControl,
    ///     linked PowerTransmitter/PowerReceiver pairs (modelled as one contributor anchored on the PT,
    ///     §6.2), Battery, and the rocket power umbilicals (the latter three as elastic suppliers / soft
    ///     demanders rather than pull-through contributors).
    ///
    ///     Per tick:
    ///       1. Gather: per-network rigid demand + generator supply; the contributor / elastic / soft
    ///          rosters from SegmentingDeviceRegistry.
    ///       2. Order: topological (Kahn) order over conducting contributor edges, so every network is
    ///          processed after all the networks that feed it. Cycle members are already CYCLE_FAULTed by
    ///          PROTECT (cycle detection) and conduct 0, so the live graph is a DAG.
    ///       3. Fixed-point loop (max 2N+4 rounds, §8.0). OVERLOAD is grow-only / sticky (cleared once at
    ///          loop entry, then committed for the tick); only SHED is re-decided each round. Each round:
    ///          a. backward desire (leaf -> source): each network's residual need (rigid + deeper pulls
    ///             - generators - elastic availability) splits greedily over its suppliers in
    ///             (priority DESC, ReferenceId ASC) order, capped per-device at effective cap;
    ///          b. structural overload, evaluated BEFORE shed (§8.4 hit-max: a contributor at its
    ///             Setting-like cap with unmet downstream rigid demand), so a structurally-overloaded
    ///             device surfaces as OVERLOAD, not shed (the CYCLE > VVF > OVERLOAD > SHED precedence);
    ///          c. forward supply + re-decided shed per input network: when consumer claims exceed the
    ///             input budget, victims shed in (priority ASC, claim DESC, ReferenceId ASC) order (§8.3);
    ///             step-up transformers never shed (§5.2); a dead input (no supply at all) defers instead
    ///             of cycling 60-second lockouts;
    ///          d. supply overload after the forward pass: the elastic analog (a battery / APC cell /
    ///             umbilical delivering its full effective discharge with rigid demand still unmet) and the
    ///             §5.7 cable-overflow rule (flow above the weakest cable's cap with generators alone under
    ///             it trips every supplier of that network instead of burning the cable).
    ///       4. Elastic shares (§7.3): per output network, batteries cover only the rigid shortfall
    ///          left after generators + transformer inflow; proportional split against effective caps
    ///          (min(rate cap, stored)); written to SoftSupplyShareCache for the GetGeneratedPower
    ///          postfixes.
    ///       5. Surplus walk (§9): soft charge requests aggregate bottom-up through non-shed
    ///          contributors (capped by remaining contributor headroom), surplus allocates top-down
    ///          pure-proportionally, single pass; written to SoftDemandShareCache for the GetUsedPower
    ///          postfixes.
    ///
    ///     Determinism (§8.0.1): every ordering is integer-keyed -- (depth, priority, ReferenceId),
    ///     with float claims quantised to whole Watts where they enter a sort. Networks iterate in
    ///     (depth, ReferenceId) order, never dictionary order.
    ///
    ///     Threading: runs on the UniTask power worker; managed memory only.
    /// </summary>
    internal static class PowerAllocator
    {
        private const float Eps = 0.01f;
        private const int UnreachableDepth = int.MaxValue / 2;

        // Networks in shallow-first (depth ASC, ReferenceId ASC) order, recomputed by the DEPTH phase
        // every tick. AtomicElectricityTickPatch ENFORCE iterates this so each network is
        // recomputed AFTER its upstream input network's PotentialLoad has been refreshed this tick,
        // eliminating the one-tick supply-propagation lag that made multi-stage transformer chains
        // oscillate power on/off under variable load. Read-only outside this class.
        internal static List<CableNetwork> ShallowFirstNetworks = new List<CableNetwork>();

        private enum SegKind { Transformer, PtPair, Apc }

        // A pull-through contributor: draws from InNet to serve OutNet (Transformer, APC, PT/PR pair).
        private sealed class Seg
        {
            public long RefId;
            public SegKind Kind;
            public CableNetwork InNet;
            public CableNetwork OutNet;
            public float CapSetting;       // §8.4 hit-max threshold: Transformer.Setting / PT pair live cap / +inf for APC
            public float CableCap;         // min(input cable cap, output cable cap)
            public float EffCap;           // min(CapSetting, cable caps) - own quiescent draw(s)
            public float UsedPower;
            public float InputDrawFactor;  // input-side draw per unit Throughput (PT-pair distance overhead m, §8.4.2); 0 treated as 1
            public bool InputLimited;      // PT pair only (§8.4 / P6): deliverable == input PotentialLoad, so the SOURCE (not the link's own rated cap) binds; a downstream shortfall is SHED, not OVERLOAD
            public int Priority;
            public int Depth;
            public bool StepUp;            // never sheds (§5.2)
            public bool Locked;            // cycle-faulted or in a prior-tick shed/overload window: conducts 0, not re-decided
            // per-round:
            public bool Shed;
            public bool Overloaded;
            public float Throughput;       // committed passthrough this round
            public float Pull;             // Throughput + UsedPower, the demand presented on InNet
            // Backward desire pass scratch: what this contributor WANTS to pass assuming its input can
            // supply it (capped at EffCap), and the matching input-side draw.
            public float DesiredThroughput;
            public float DesiredPull;
            // surplus walk:
            public float PropagatedReq;
            public float GrantThrough;
        }

        // An elastic supplier: discharges a store onto OutNet only to fill rigid shortfall (§7.3).
        private sealed class Elastic
        {
            public long RefId;
            public CableNetwork OutNet;
            public float EffDischarge;     // min(rate cap, cable cap, stored)
            public bool Locked;
            public bool Overloaded;        // per-round (elastic hit-max analog)
            public float Share;            // final delivered share
        }

        // A soft demander: charges a store from InNet out of surplus only (§7.4/§9).
        private sealed class Soft
        {
            public long RefId;
            public CableNetwork InNet;
            public float Request;          // min(charge rate cap, cable cap, headroom)
            public float Share;
        }

        private sealed class Net
        {
            public CableNetwork Network;
            public long Id;
            public float RigidDemand;
            public float GenSupply;
            public int Depth = UnreachableDepth;
            public readonly List<Seg> Suppliers = new List<Seg>();
            public readonly List<Seg> Consumers = new List<Seg>();
            public readonly List<Elastic> Elastics = new List<Elastic>();
            public readonly List<Soft> Softs = new List<Soft>();
            // per-round:
            public float Unmet;
            public float PullsGranted;
            public float InflowCommitted;
            public float ElasticDelivered;
            // Backward desire pass scratch: total power demanded on this network assuming nothing is
            // shed (rigid + every non-locked, non-overloaded consumer's desired pull).
            public float DesiredDemand;
            // surplus walk:
            public float SoftReqTotal;
            public float IncomingGrant;
        }

        internal static void RunAtomic(int currentTick)
        {
            bool shedOn = ShedSettingsSync.Effective;
            bool overloadOn = OverloadSettingsSync.Effective;

            // ----------------------------------------------------------------
            // 1. GATHER.
            // ----------------------------------------------------------------
            var nets = new Dictionary<long, Net>();
            var netList = new List<Net>();

            Net GetNet(CableNetwork network)
            {
                if (network == null) return null;
                if (nets.TryGetValue(network.ReferenceId, out var existing)) return existing;
                var n = new Net { Network = network, Id = network.ReferenceId };
                nets[network.ReferenceId] = n;
                netList.Add(n);
                return n;
            }

            // Per-network rigid demand + generator supply. Segmenters are skipped here (they are
            // modelled structurally below); everything else generating is a rigid generator and
            // everything else drawing is a rigid consumer. VVF-faulted producers already read 0 via
            // ProducerFaultEnforcementPatches, so faulted solar contributes no supply here.
            CableNetwork.AllCableNetworks.ForEach(network =>
            {
                if (network == null) return;
                List<Device> snapshot;
                lock (network.PowerDeviceList)
                {
                    if (network.PowerDeviceList.Count == 0) return;
                    snapshot = new List<Device>(network.PowerDeviceList);
                }
                var n = GetNet(network);
                for (int i = 0; i < snapshot.Count; i++)
                {
                    var device = snapshot[i];
                    if (device == null) continue;
                    if (device is ElectricalInputOutput eio && SegmentingDeviceRegistry.IsSegmenter(eio)) continue;
                    try
                    {
                        float used = device.GetUsedPower(network);
                        if (used > 0f) n.RigidDemand += used;
                        float generated = device.GetGeneratedPower(network);
                        if (generated > 0f) n.GenSupply += generated;
                    }
                    catch { }
                }
            });

            // Contributor / elastic / soft rosters from the deterministic segmenter enumeration.
            var segs = new List<Seg>();
            var elastics = new List<Elastic>();
            var softs = new List<Soft>();

            bool IsPowerLocked(long refId)
            {
                return CycleFaultRegistry.IsCycleFaulted(refId, currentTick)
                    || BrownoutRegistry.IsLockedOut(refId, currentTick)
                    || OverloadRegistry.IsLockedOut(refId, currentTick);
            }

            var segmenters = SegmentingDeviceRegistry.EnumerateSorted();
            for (int i = 0; i < segmenters.Count; i++)
            {
                var eio = segmenters[i];
                if (eio == null || !eio.OnOff) continue;

                switch (eio)
                {
                    case Transformer t:
                    {
                        if (t.Error == 1 || t.InputNetwork == null || t.OutputNetwork == null) break;
                        if (t.InputNetwork.ReferenceId == t.OutputNetwork.ReferenceId) break;
                        var inCable = t.InputConnection?.GetCable();
                        var outCable = t.OutputConnection?.GetCable();
                        float capSetting = (float)t.Setting;
                        float cableCap = Mathf.Min(CableMax.For(inCable), CableMax.For(outCable));
                        float effCap = Mathf.Min(capSetting, cableCap) - t.UsedPower;
                        if (effCap < 0f) effCap = 0f;
                        segs.Add(new Seg
                        {
                            RefId = t.ReferenceId,
                            Kind = SegKind.Transformer,
                            InNet = t.InputNetwork,
                            OutNet = t.OutputNetwork,
                            CapSetting = capSetting,
                            CableCap = cableCap,
                            EffCap = effCap,
                            UsedPower = t.UsedPower,
                            Priority = PriorityStore.GetPriority(t.ReferenceId),
                            StepUp = IsStepUp(t.InputNetwork, t.OutputNetwork),
                            Locked = IsPowerLocked(t.ReferenceId),
                        });
                        break;
                    }
                    case PowerTransmitter pt:
                    {
                        var pr = pt.LinkedReceiver;
                        if (pr == null || !pr.OnOff || pt.InputNetwork == null || pr.OutputNetwork == null) break;
                        if (pt.InputNetwork.ReferenceId == pr.OutputNetwork.ReferenceId) break;
                        // liveCap is the link's DELIVERABLE cap on the wireless/output side, already
                        // reflecting the active distance model (POWER.md §6.3). The PT/PR pair is the
                        // one supplier class not run through DeviceOutputSanitizer, so guard a
                        // non-finite read here before it can poison the allocator's network sums.
                        float liveCap = 0f;
                        try { liveCap = pt.GetGeneratedPower(pt.OutputNetwork); } catch { }
                        if (float.IsNaN(liveCap) || float.IsInfinity(liveCap) || liveCap < 0f) liveCap = 0f;
                        // Source-draw multiplier (POWER.md §6.3 / §8.4.2). PowerTransmitterPlus does NOT
                        // derate delivered power for distance; it inflates the INPUT-side draw to
                        // delivered * m (m = 1 + k * distance_km). With vanilla, or no PowerTransmitterPlus,
                        // m = 1 (delivered == drawn). The input cable carries delivered * m and the input
                        // network is billed delivered * m, so both the input-cable bound and the shed
                        // demand on the source network must use m, or a long link silently brown-outs its
                        // source network (the no-partial-power invariant fails on the input side).
                        float m = PowerTransmitterPlusInterop.SourceDrawMultiplier(pt);
                        var inCable = pt.InputConnection?.GetCable();
                        var outCable = pr.OutputConnection?.GetCable();
                        // Output cable carries the delivered throughput; input cable carries throughput * m.
                        float cableCap = Mathf.Min(CableMax.For(inCable) / m, CableMax.For(outCable));
                        float effCap = Mathf.Min(liveCap, cableCap) - pt.UsedPower - pr.UsedPower;
                        if (effCap < 0f) effCap = 0f;
                        // P6 / §8.4: distinguish a link-rated-cap bottleneck (OVERLOAD) from an
                        // input-supply bottleneck (SHED). liveCap = min(rated cap, InputNetwork.PotentialLoad)
                        // [minus a vanilla distance loss]; when it equals the input network's PotentialLoad
                        // the deliverable is held down by the SOURCE, not the link's own rating, so a
                        // downstream shortfall is "insufficient upstream supply" (SHED), not "the link cannot
                        // carry the demand" (OVERLOAD). PowerTransmitterPlus applies no output-side loss, so
                        // liveCap == potential exactly when input-limited; a long vanilla link whose loss pulls
                        // liveCap below potential reads as link-limited, which is truthful (the loss is the link's).
                        bool inputLimited = liveCap >= pt.InputNetwork.PotentialLoad - Eps;
                        segs.Add(new Seg
                        {
                            RefId = pt.ReferenceId,
                            Kind = SegKind.PtPair,
                            InNet = pt.InputNetwork,
                            OutNet = pr.OutputNetwork,
                            CapSetting = liveCap,
                            CableCap = cableCap,
                            EffCap = effCap,
                            UsedPower = pt.UsedPower + pr.UsedPower,
                            InputDrawFactor = m,
                            InputLimited = inputLimited,
                            Priority = PriorityStore.GetPriority(pt.ReferenceId),
                            StepUp = IsStepUp(pt.InputNetwork, pr.OutputNetwork),
                            Locked = IsPowerLocked(pt.ReferenceId) || CycleFaultRegistry.IsCycleFaulted(pr.ReferenceId, currentTick),
                        });
                        break;
                    }
                    case PowerReceiver _:
                        break;   // handled via its linked PT
                    case AreaPowerControl apc:
                    {
                        if (apc.Error == 1 || apc.InputNetwork == null || apc.OutputNetwork == null) break;
                        if (apc.InputNetwork.ReferenceId == apc.OutputNetwork.ReferenceId) break;
                        var inCable = apc.InputConnection?.GetCable();
                        var outCable = apc.OutputConnection?.GetCable();
                        float cableCap = Mathf.Min(CableMax.For(inCable), CableMax.For(outCable));
                        float effCap = cableCap - apc.UsedPower;
                        if (effCap < 0f) effCap = 0f;
                        bool locked = IsPowerLocked(apc.ReferenceId);
                        segs.Add(new Seg
                        {
                            RefId = apc.ReferenceId,
                            Kind = SegKind.Apc,
                            InNet = apc.InputNetwork,
                            OutNet = apc.OutputNetwork,
                            CapSetting = float.MaxValue,   // no vanilla throughput rating; §8.4 hit-max does not apply
                            CableCap = cableCap,
                            EffCap = effCap,
                            UsedPower = apc.UsedPower,
                            Priority = PriorityStore.GetPriority(apc.ReferenceId),
                            StepUp = false,                // APC is never tier-classified step-up
                            Locked = locked,
                        });
                        var cell = apc.Battery;
                        if (cell != null)
                        {
                            if (cell.PowerStored > 0f)
                            {
                                elastics.Add(new Elastic
                                {
                                    RefId = apc.ReferenceId,
                                    OutNet = apc.OutputNetwork,
                                    EffDischarge = Mathf.Min(
                                        Mathf.Min(ApcDischargeRateRegistry.GetDischargeRate(apc.ReferenceId), CableMax.For(outCable)),
                                        cell.PowerStored),
                                    Locked = locked,
                                });
                            }
                            if (!cell.IsCharged)
                            {
                                float req = Mathf.Min(Patches.AreaPowerControlPatches.ComputeChargeCap(apc), cell.PowerDelta);
                                if (req > 0f)
                                    softs.Add(new Soft { RefId = apc.ReferenceId, InNet = apc.InputNetwork, Request = req });
                            }
                        }
                        break;
                    }
                    case Battery battery:
                    {
                        if (battery.Error == 1) break;
                        if (battery.InputNetwork != null && battery.OutputNetwork != null
                            && battery.InputNetwork.ReferenceId == battery.OutputNetwork.ReferenceId) break;   // short-circuit gate
                        bool locked = IsPowerLocked(battery.ReferenceId);
                        if (battery.OutputNetwork != null && battery.PowerStored > 0f)
                        {
                            float rateCap = Settings.EnableBatteryLimits.Value
                                ? Patches.StationaryBatteryPatches.EffectiveDischargeCap(battery)
                                : float.MaxValue;
                            elastics.Add(new Elastic
                            {
                                RefId = battery.ReferenceId,
                                OutNet = battery.OutputNetwork,
                                EffDischarge = Mathf.Min(rateCap, battery.PowerStored),
                                Locked = locked,
                            });
                        }
                        if (battery.InputNetwork != null)
                        {
                            float headroom = battery.PowerMaximum - battery.PowerStored;
                            float rateCap = Settings.EnableBatteryLimits.Value
                                ? Patches.StationaryBatteryPatches.EffectiveChargeCap(battery)
                                : float.MaxValue;
                            float req = Mathf.Min(rateCap, headroom);
                            if (req > 0f)
                                softs.Add(new Soft { RefId = battery.ReferenceId, InNet = battery.InputNetwork, Request = req });
                        }
                        break;
                    }
                    case RocketPowerUmbilicalMale male:
                        AddUmbilical(male, male.PowerStored, male.PowerMaximum, elastics, softs, currentTick);
                        break;
                    case RocketPowerUmbilicalFemale female:
                        AddUmbilical(female, female.PowerStored, female.PowerMaximum, elastics, softs, currentTick);
                        break;
                }
            }

            // Register rosters onto their networks (creating Net entries for cableless nets too).
            foreach (var seg in segs)
            {
                var inNet = GetNet(seg.InNet);
                var outNet = GetNet(seg.OutNet);
                if (inNet == null || outNet == null) continue;
                inNet.Consumers.Add(seg);
                outNet.Suppliers.Add(seg);
            }
            foreach (var e in elastics) GetNet(e.OutNet)?.Elastics.Add(e);
            foreach (var s in softs) GetNet(s.InNet)?.Softs.Add(s);

            // ----------------------------------------------------------------
            // 2. ORDER. TRUE topological order over the live contributor edges (every network after ALL
            //    the networks that feed it, diamonds included), so the backward/forward sweep sees fresh
            //    upstream supply at every depth with no residual lag.
            // ----------------------------------------------------------------
            List<Net> topo = BuildTopoOrder(netList, nets);    // assigns Net.Depth = topo index
            foreach (var seg in segs)
                seg.Depth = nets[seg.InNet.ReferenceId].Depth;
            List<Net> netsShallowFirst = topo;                 // source -> leaf
            List<Net> netsDeepFirst = new List<Net>(topo);
            netsDeepFirst.Reverse();                           // leaf -> source

            // Publish the shallow-first network order for ENFORCE (AtomicElectricityTickPatch).
            // Iterating ENFORCE upstream-first (topological order) is what eliminates the one-tick
            // transformer supply lag.
            var shallowOrder = new List<CableNetwork>(netsShallowFirst.Count);
            for (int i = 0; i < netsShallowFirst.Count; i++) shallowOrder.Add(netsShallowFirst[i].Network);
            ShallowFirstNetworks = shallowOrder;
            foreach (var n in netList)
            {
                n.Suppliers.Sort(SupplierOrder);
                n.Consumers.Sort(SupplierOrder);
            }

            // ----------------------------------------------------------------
            // 3. DECIDE: shed / overload fixed point. Backward/forward sweep iterated to a fixed point.
            //    Overload is evaluated BEFORE shed each round and is GROW-ONLY (sticky: cleared only at
            //    loop entry, then reset only by the 60 s timeout or a player turn-off); ONLY SHED is
            //    re-decided each round against the settled state. So an unnecessary shed cannot freeze for
            //    60 s once another device's overload frees the budget (the 2c case), and a transformer that
            //    structurally cannot serve its downstream surfaces as OVERLOAD, not shed. Throughputs are
            //    exact (no headroom). Convergence is bounded (2N+4 rounds);
            //    the per-net field values it leaves (Unmet / InflowCommitted / PullsGranted /
            //    ElasticDelivered / Throughput) feed the shared dead-input / commit / elastic-share /
            //    surplus tail below.
            // ----------------------------------------------------------------
            RunAllocationLoop(topo, netsDeepFirst, segs, elastics, netList, shedOn, overloadOn);

            // Dead-input cue (POWER.md §8.3): a contributor whose input network has NO effective supply
            // (no generators, no upstream inflow, no live battery -- the same totalAvail the shed pass
            // uses) idles instead of shedding. Flag those that are actively trying to pass power
            // downstream for a steady "no upstream supply" hover. NOT a lockout: no 60 s timer, no flash,
            // instant recovery when the input is powered. Rebuilt from the converged state every tick.
            DeadInputRegistry.BeginServerPass();
            foreach (var n in netList)
            {
                if (n.Consumers.Count == 0) continue;
                if (n.GenSupply + n.InflowCommitted + AvailableElastic(n) > Eps) continue;   // has supply -> not dead
                // The forward sweep clamps Throughput by actual upstream supply (zero on a dead net), so
                // fall back to the desired throughput here. A contributor actively trying to pass power on
                // an unsupplied input is the dead-input cue (no lockout, instant recovery when the input
                // is powered).
                foreach (var c in n.Consumers)
                    if (IsActive(c) && (c.Throughput > Eps || c.DesiredThroughput > Eps))
                        DeadInputRegistry.MarkDeadInput(c.RefId);
            }

            // ----------------------------------------------------------------
            // Commit new lockouts. Option C: a network with a cable burn in flight (a tier burn queued
            // this tick, or a §5.7 generator-overflow burn issued last tick whose split has not landed)
            // is about to re-partition, so its merged-topology supply/demand math is not the topology
            // the lockout would apply to. Defer committing NEW shed / overload lockouts for such a
            // network until the split lands (SplitPendingRegistry clears it). Existing lockouts
            // (seg.Locked) still enforce; the network still distributes power via vanilla ENFORCE.
            // ----------------------------------------------------------------
            foreach (var seg in segs)
            {
                if (seg.Locked) continue;   // prior-tick lockout carries; timer untouched
                if (SegNetPending(seg)) continue;
                if (seg.Shed) BrownoutRegistry.NoteShed(seg.RefId, currentTick);
                if (seg.Overloaded) OverloadRegistry.NoteOverload(seg.RefId, currentTick);
            }
            // Elastic overload commit: NETWORK-LEVEL RETRY, then reset (POWER.md §8.4.1). A net with a
            // newly overloaded elastic is a candidate. Its "overload cohort" is every elastic on the net
            // overloaded this tick OR already overload-locked -- all of them ON, because OffAsResetSweep
            // clears the lockouts of switched-off devices every tick. Before locking the cohort, RETRY at
            // the network level: if the cohort's COMBINED discharge would cover the net's residual demand,
            // the situation has recovered (load dropped, a device was toggled back on, or supply was added),
            // so clear the cohort's overload locks and let them rejoin next tick -- no timer reset. Only if
            // the retry still leaves demand unmet do we (re)stamp ONE shared fresh expiry across the cohort,
            // which both arms the 60 s lockout and keeps the cohort phase-synced, so an individually-too-
            // weak-but-jointly-sufficient bank always re-arms together rather than taking turns failing.
            // Self-resolving: after a commit the net is either all-recovered or all-locked, so neither
            // branch re-triggers next tick. Split-pending nets defer (Option C). Transformers keep their
            // per-device §8.4 timer (the culprit transformer is genuinely device-specific; this is a
            // network property).
            var elasticOverloadNets = new HashSet<long>();
            foreach (var e in elastics)
            {
                if (!e.Overloaded || e.OutNet == null) continue;
                if (SplitPendingRegistry.IsPending(e.OutNet.ReferenceId)) continue;
                elasticOverloadNets.Add(e.OutNet.ReferenceId);
            }
            foreach (long netRef in elasticOverloadNets)
            {
                if (!nets.TryGetValue(netRef, out var n)) continue;
                float cohortDischarge = 0f;
                foreach (var e in elastics)
                    if (e.OutNet != null && e.OutNet.ReferenceId == netRef
                        && (e.Overloaded || OverloadRegistry.IsOverloaded(e.RefId, currentTick)))
                        cohortDischarge += e.EffDischarge;
                bool recover = cohortDischarge >= n.Unmet - Eps;   // cohort can jointly cover the load -> retry succeeds
                foreach (var e in elastics)
                {
                    if (e.OutNet == null || e.OutNet.ReferenceId != netRef) continue;
                    if (!(e.Overloaded || OverloadRegistry.IsOverloaded(e.RefId, currentTick))) continue;
                    if (recover) OverloadRegistry.ClearLockout(e.RefId);          // recovered: no reset, rejoin next tick
                    else OverloadRegistry.NoteOverload(e.RefId, currentTick);     // still short: arm + phase-sync
                }
            }

            // ----------------------------------------------------------------
            // 4. ELASTIC SHARES (§7.3) -> SoftSupplyShareCache.
            // ----------------------------------------------------------------
            foreach (var n in netList)
            {
                float shortfall = n.RigidDemand + n.PullsGranted - n.GenSupply - n.InflowCommitted;
                if (shortfall < 0f) shortfall = 0f;
                float effTotal = 0f;
                foreach (var e in n.Elastics)
                    if (!e.Locked && !e.Overloaded) effTotal += e.EffDischarge;
                foreach (var e in n.Elastics)
                {
                    if (e.Locked || e.Overloaded) { e.Share = 0f; continue; }
                    if (shortfall <= 0f || effTotal <= 0f) { e.Share = 0f; continue; }
                    e.Share = shortfall >= effTotal
                        ? e.EffDischarge
                        : e.EffDischarge * (shortfall / effTotal);
                }
            }

            // ----------------------------------------------------------------
            // 5. SURPLUS WALK (§9) -> SoftDemandShareCache.
            // ----------------------------------------------------------------
            // Requests aggregate bottom-up (deepest nets first): a network's total request is its own
            // soft demand plus what flows up through each non-shed consumer, capped by that
            // contributor's remaining headroom.
            foreach (var n in netList)
            {
                n.SoftReqTotal = 0f;
                n.IncomingGrant = 0f;
                foreach (var s in n.Softs) { s.Share = 0f; n.SoftReqTotal += s.Request; }
            }
            foreach (var seg in segs) { seg.PropagatedReq = 0f; seg.GrantThrough = 0f; }
            foreach (var n in netsDeepFirst)
            {
                foreach (var seg in n.Consumers)   // seg.InNet == n, seg.OutNet deeper
                {
                    if (!IsActive(seg)) continue;
                    var outNet = nets[seg.OutNet.ReferenceId];
                    float headroom = seg.EffCap - seg.Throughput;
                    if (headroom <= 0f) continue;
                    seg.PropagatedReq = Mathf.Min(outNet.SoftReqTotal, headroom);
                    n.SoftReqTotal += seg.PropagatedReq;
                }
            }
            // Grants flow top-down (shallowest first), pure-proportional (§9.4), capped by the weakest
            // cable's remaining headroom on the granting network.
            foreach (var n in netsShallowFirst)
            {
                float localGive = n.GenSupply + n.InflowCommitted - (n.RigidDemand - n.Unmet) - n.PullsGranted;
                if (localGive < 0f) localGive = 0f;
                float avail = localGive + n.IncomingGrant;
                if (avail <= 0f || n.SoftReqTotal <= 0f) continue;

                float cableHeadroom = CableMax.WeakestCapOnNetwork(n.Network) - ((n.RigidDemand - n.Unmet) + n.PullsGranted);
                if (cableHeadroom < 0f) cableHeadroom = 0f;
                if (avail > cableHeadroom) avail = cableHeadroom;

                float granted = Mathf.Min(avail, n.SoftReqTotal);
                if (granted <= 0f) continue;
                float ratio = granted / n.SoftReqTotal;

                foreach (var s in n.Softs)
                    s.Share = s.Request * ratio;
                foreach (var seg in n.Consumers)
                {
                    if (seg.PropagatedReq <= 0f || !IsActive(seg)) continue;
                    seg.GrantThrough = seg.PropagatedReq * ratio;
                    nets[seg.OutNet.ReferenceId].IncomingGrant += seg.GrantThrough;
                }
            }

            // ----------------------------------------------------------------
            // Write the share caches (ENFORCE's postfixes read these).
            // ----------------------------------------------------------------
            foreach (var e in elastics)
            {
                // For APCs the GetGeneratedPower surface bundles passthrough + cell (vanilla
                // AvailablePower), so the cap must include the committed passthrough and any soft
                // grant flowing through; the matching Seg is found below.
                SoftSupplyShareCache.SetShare(e.RefId, e.Share);
            }
            foreach (var seg in segs)
            {
                if (seg.Kind != SegKind.Apc) continue;
                // The APC's bundled supply (passthrough + grant-through + cell) goes to
                // SoftSupplyShareCache because vanilla GetGeneratedPower bundles them. But DischargeSpeed
                // must mean the CELL rate, consistent with battery / umbilical (P9), so stamp the
                // cell-only share separately first, before the bundled entry overwrites it.
                float cellShare = 0f;
                foreach (var e in elastics)
                    if (e.RefId == seg.RefId) { cellShare = e.Share; break; }
                ApcCellDischargeCache.SetShare(seg.RefId, cellShare);
                SoftSupplyShareCache.SetShare(seg.RefId, seg.Throughput + seg.GrantThrough + cellShare);
            }
            foreach (var s in softs)
                SoftDemandShareCache.SetShare(s.RefId, s.Share);

            // Publish each TRANSFORMER's exact converged throughput so its GetGeneratedPower /
            // GetUsedPower report the real in-tick flow (no headroom). Output side = seg.Throughput;
            // input side = seg.Pull (throughput + the transformer's own quiescent draw). Inactive
            // transformers (shed / overloaded / cycle-faulted) carry Throughput = Pull = 0, so they
            // report 0 both ways. APC / battery / umbilical already report their exact shares through
            // SoftSupplyShareCache (driven by the same supply-accurate seg.Throughput).
            foreach (var seg in segs)
            {
                if (seg.Kind == SegKind.Transformer)
                    TransformerSupplyCache.Set(seg.RefId, seg.Throughput, seg.Pull);
                else if (seg.Kind == SegKind.PtPair)
                    // The wireless PT/PR pair bills its input draw via the PowerTransmitter on InNet.
                    // seg.Pull = Throughput * distance-multiplier + the pair's quiescent -- the exact
                    // input draw. PowerTransmitterDrawPatches.GetUsedPower reports this instead of the
                    // lagging _powerProvided (an inactive pair caches Pull = 0).
                    TransformerSupplyCache.Set(seg.RefId, seg.Throughput, seg.Pull);
                else if (seg.Kind == SegKind.Apc)
                    // Fresh passthrough draw the APC bills on its INPUT network: committed rigid
                    // passthrough + soft-charge grant flowing through. Replaces the lagging
                    // _powerProvided in AreaPowerControlPatches.GetUsedPower so the APC's input draw
                    // is current (input == output, no one-tick lag -> no input-network undershoot).
                    ApcPassthroughCache.Set(seg.RefId, seg.Throughput + seg.GrantThrough);
            }
        }

        // Option C gate: true when a cable burn is in flight on either side of this contributor's span,
        // so the allocator must not commit a durable lockout against a topology that is about to split.
        private static bool SegNetPending(Seg seg)
            => (seg.InNet != null && SplitPendingRegistry.IsPending(seg.InNet.ReferenceId))
               || (seg.OutNet != null && SplitPendingRegistry.IsPending(seg.OutNet.ReferenceId));

        // =====================================================================
        // ALLOCATOR: topological backward-demand / forward-supply
        // sweep, iterated to a fixed point with RE-DECIDABLE shed + overload.
        // Computes each contributor's exact in-tick throughput (no headroom) and
        // avoids the 60-second freeze of a shed that another device's overload
        // would have relieved (the 2c case).
        // =====================================================================

        // A contributor is eligible to DESIRE power (backward pass) when it is not locked and not
        // overloaded. Shed is deliberately IGNORED here: the forward pass re-decides shedding every
        // round, so a previously-shed contributor must still present its desired pull to be reconsidered.
        private static bool DesireActive(Seg s) => !s.Locked && !s.Overloaded;

        // Kahn topological order over the live contributor edges InNet -> OutNet. Cycle-faulted segs are
        // Locked and excluded, so the live graph is a forest/DAG; every network lands AFTER all the
        // networks that feed it (diamonds correct). Ready nodes are popped in ReferenceId order for
        // determinism. Net.Depth is set to the topo index. A residual cycle (should not occur after
        // PROTECT cycle detection / removal) is appended in ReferenceId order with a warning.
        private static List<Net> BuildTopoOrder(List<Net> netList, Dictionary<long, Net> nets)
        {
            var indeg = new Dictionary<long, int>(netList.Count);
            foreach (var n in netList)
            {
                int d = 0;
                foreach (var s in n.Suppliers) if (!s.Locked) d++;
                indeg[n.Id] = d;
            }
            var queue = new List<Net>();
            foreach (var n in netList) if (indeg[n.Id] == 0) queue.Add(n);
            queue.Sort((a, b) => a.Id.CompareTo(b.Id));

            var topo = new List<Net>(netList.Count);
            int qi = 0;
            while (qi < queue.Count)
            {
                var n = queue[qi++];
                topo.Add(n);
                foreach (var c in n.Consumers)        // edges out of n: n -> c.OutNet
                {
                    if (c.Locked) continue;
                    var m = nets[c.OutNet.ReferenceId];
                    if (--indeg[m.Id] != 0) continue;
                    // insert m into the unprocessed tail [qi..end], keeping that tail sorted by Id, so we
                    // always pop the smallest-Id ready network next (deterministic order).
                    int ins = queue.Count;
                    for (int k = qi; k < queue.Count; k++)
                        if (queue[k].Id > m.Id) { ins = k; break; }
                    queue.Insert(ins, m);
                }
            }

            if (topo.Count < netList.Count)
            {
                var seen = new HashSet<long>();
                foreach (var n in topo) seen.Add(n.Id);
                var leftover = new List<Net>();
                foreach (var n in netList) if (!seen.Contains(n.Id)) leftover.Add(n);
                leftover.Sort((a, b) => a.Id.CompareTo(b.Id));
                UnityEngine.Debug.LogWarning(
                    $"[PowerGridPlus] Allocator topo: {leftover.Count} network(s) in a residual cycle after cycle-fault removal; appended in ReferenceId order.");
                topo.AddRange(leftover);
            }
            for (int i = 0; i < topo.Count; i++) topo[i].Depth = i;
            return topo;
        }

        // The iterated fixed point. Each round: backward desire sweep, forward supply sweep (which
        // RE-DECIDES shedding against the settled upstream supply), then the overload pass (which
        // RE-DECIDES overload against the post-shed demand). Forward-before-overload means a shed that
        // relieves an over-demanded network is honoured the same round, killing the shed<->overload
        // 2-cycle. Converges when the (shed, overload) sets stop changing. Bounded by 2N+4 rounds; a
        // detected 2-cycle resolves to the safe union of the two states, then settles throughputs.
        private static void RunAllocationLoop(List<Net> topo, List<Net> topoRev, List<Seg> segs,
            List<Elastic> elastics, List<Net> netList, bool shedOn, bool overloadOn)
        {
            // Clean slate once per tick. Within the loop SHED is re-decided every round (ForwardSupplyAndShed
            // clears it); OVERLOAD only ever GROWS (sticky: committed on detection, reset only by the 60 s
            // timeout or a player turn-off), so it is cleared here once and never inside a round.
            foreach (var seg in segs) { seg.Shed = false; seg.Overloaded = false; }
            foreach (var e in elastics) e.Overloaded = false;

            int maxRounds = 2 * segs.Count + 4;
            HashSet<long> prevShed = null, prevOver = null, prevEl = null;
            HashSet<long> prev2Shed = null, prev2Over = null, prev2El = null;
            bool converged = false;

            for (int round = 0; round < maxRounds; round++)
            {
                BackwardDesirePass(topoRev);
                // Overload is evaluated BEFORE shed and is grow-only (precedence CYCLE > VVF > OVERLOAD >
                // SHED, POWER.md decision 3). Only SHED is re-decidable within a tick: a transformer that
                // structurally cannot serve its downstream is diagnosed as OVERLOAD here, before the shed
                // pass could mislabel it as input-starved. The structural rule is desire-based (pre-shed);
                // the supply rules (elastic / cable) need the forward pass's Unmet, so they run after it.
                DetectStructuralOverload(netList, segs, overloadOn);
                ForwardSupplyAndShed(topo, segs, shedOn, settleOnly: false);
                DetectSupplyOverload(netList, elastics, overloadOn);

                var curShed = CollectFlagged(segs, shed: true);
                var curOver = CollectFlagged(segs, shed: false);
                var curEl = CollectFlaggedElastic(elastics);

                if (prevShed != null && curShed.SetEquals(prevShed)
                    && curOver.SetEquals(prevOver) && curEl.SetEquals(prevEl))
                {
                    converged = true;
                    break;
                }
                if (prev2Shed != null && curShed.SetEquals(prev2Shed)
                    && curOver.SetEquals(prev2Over) && curEl.SetEquals(prev2El))
                {
                    // 2-cycle between two states: OR the intermediate state's flags in (safe superset,
                    // never under-protective), then settle throughputs without re-deciding.
                    foreach (var seg in segs)
                    {
                        if (prevShed.Contains(seg.RefId)) seg.Shed = true;
                        if (prevOver.Contains(seg.RefId)) seg.Overloaded = true;
                    }
                    foreach (var e in elastics) if (prevEl.Contains(e.RefId)) e.Overloaded = true;
                    BackwardDesirePass(topoRev);
                    ForwardSupplyAndShed(topo, segs, shedOn, settleOnly: true);
                    converged = true;
                    break;
                }
                prev2Shed = prevShed; prev2Over = prevOver; prev2El = prevEl;
                prevShed = curShed; prevOver = curOver; prevEl = curEl;
            }

            if (!converged)
                UnityEngine.Debug.LogWarning(
                    $"[PowerGridPlus] Allocator did not converge in {maxRounds} rounds; using last settled state (internally consistent, safe).");
        }

        // Backward demand sweep (leaf -> source). Each contributor's DesiredThroughput = its share of its
        // output network's demand (priority tier DESC, proportional by EffCap within a tier, capped at
        // EffCap), and DesiredPull = the matching input-side draw (throughput * distance factor + quiescent).
        // Generators are subtracted first; elastic storage is the documented last resort and is not modelled
        // here (it absorbs the per-net shortfall in the forward sweep / elastic-share pass, matching the
        // gen -> transformer -> battery supply order).
        private static void BackwardDesirePass(List<Net> topoRev)
        {
            foreach (var n in topoRev)
            {
                float pulls = 0f;
                foreach (var c in n.Consumers)
                {
                    c.DesiredPull = (DesireActive(c) && c.DesiredThroughput > 0f)
                        ? c.DesiredThroughput * Mathf.Max(c.InputDrawFactor, 1f) + c.UsedPower
                        : 0f;
                    pulls += c.DesiredPull;
                }
                n.DesiredDemand = n.RigidDemand + pulls;

                float residual = n.DesiredDemand - n.GenSupply;
                if (residual < 0f) residual = 0f;

                var supList = n.Suppliers;
                int si = 0;
                while (si < supList.Count && residual > Eps)
                {
                    int tierPriority = supList[si].Priority;
                    int blockEnd = si;
                    float tierCap = 0f;
                    while (blockEnd < supList.Count && supList[blockEnd].Priority == tierPriority)
                    {
                        if (DesireActive(supList[blockEnd])) tierCap += supList[blockEnd].EffCap;
                        else supList[blockEnd].DesiredThroughput = 0f;
                        blockEnd++;
                    }
                    float tierGive = tierCap > Eps ? Mathf.Min(tierCap, residual) : 0f;
                    for (int j = si; j < blockEnd; j++)
                    {
                        var seg = supList[j];
                        if (!DesireActive(seg)) continue;
                        seg.DesiredThroughput = tierCap > Eps ? tierGive * (seg.EffCap / tierCap) : 0f;
                    }
                    residual -= tierGive;
                    si = blockEnd;
                }
                for (; si < supList.Count; si++)
                    supList[si].DesiredThroughput = 0f;
            }
        }

        // Forward supply sweep (source -> leaf). For each network in topo order (so every supplier's
        // actual throughput is already finalized), compute the supply actually reaching it and distribute
        // to its consumers highest-priority-first. When deciding (settleOnly == false) shedding is RE-
        // DECIDED here: if the active consumers' desired pulls exceed the budget the network can pass, the
        // lowest-priority victims shed (whole, never partial) until the rest fit; a network with no supply
        // at all (avail <= Eps) sheds nothing (dead-input idle). settleOnly == true keeps the current
        // shed/overload flags and only recomputes throughputs + per-net fields (used to settle a 2-cycle).
        private static void ForwardSupplyAndShed(List<Net> topo, List<Seg> segs, bool shedOn, bool settleOnly)
        {
            foreach (var seg in segs)
            {
                seg.Throughput = 0f;
                seg.Pull = 0f;
                if (!settleOnly) seg.Shed = false;
            }

            foreach (var n in topo)
            {
                float firmIn = n.GenSupply;
                foreach (var s in n.Suppliers)
                    if (IsActive(s)) firmIn += s.Throughput;
                float elasticCap = AvailableElastic(n);
                float avail = firmIn + elasticCap;
                n.InflowCommitted = firmIn - n.GenSupply;

                float budget = avail - n.RigidDemand;
                if (budget < 0f) budget = 0f;

                if (shedOn && !settleOnly && avail > Eps)
                {
                    float claims = 0f;
                    foreach (var c in n.Consumers)
                        if (!c.Locked && !c.Overloaded && !c.Shed) claims += c.DesiredPull;
                    while (claims > budget + Eps)
                    {
                        Seg victim = null;
                        foreach (var c in n.Consumers)
                        {
                            if (c.Locked || c.Overloaded || c.Shed || c.StepUp || c.DesiredPull <= Eps) continue;
                            if (victim == null || ShedVictimOrder(c, victim) < 0) victim = c;
                        }
                        if (victim == null) break;   // only step-up / non-sheddable left: accept
                        victim.Shed = true;
                        claims -= victim.DesiredPull;
                    }
                }

                float remaining = budget;
                float survivorPull = 0f;
                foreach (var c in n.Consumers)
                {
                    if (!IsActive(c)) { c.Throughput = 0f; c.Pull = 0f; continue; }
                    float want = c.DesiredPull;
                    float grant = want < remaining ? want : remaining;
                    if (grant < 0f) grant = 0f;
                    float m = Mathf.Max(c.InputDrawFactor, 1f);
                    float outThr = (grant - c.UsedPower) / m;
                    c.Throughput = outThr > 0f ? outThr : 0f;
                    c.Pull = grant;
                    remaining -= grant;
                    survivorPull += grant;
                }
                n.PullsGranted = survivorPull;

                float rigidServed = avail < n.RigidDemand ? avail : n.RigidDemand;
                float elasticDelivered = (rigidServed + survivorPull) - firmIn;
                if (elasticDelivered < 0f) elasticDelivered = 0f;
                if (elasticDelivered > elasticCap) elasticDelivered = elasticCap;
                n.ElasticDelivered = elasticDelivered;

                float activeWant = n.RigidDemand;
                foreach (var c in n.Consumers) if (IsActive(c)) activeWant += c.DesiredPull;
                float unmet = activeWant - avail;
                n.Unmet = unmet > 0f ? unmet : 0f;
            }
        }

        // Overload pass (re-decidable). Three rules, recomputed fresh from the settled state each round:
        //   1. Per-network capacity hit-max (§8.4): a network whose post-shed demand exceeds gen + elastic
        //      + suppliers-at-their-caps overloads its SETTING-limited suppliers (downstream demand truly
        //      exceeds what the transformers can deliver). Suppliers are counted at EffCap even when already
        //      overloaded, so the condition keeps re-detecting them (an overloaded supplier contributes 0,
        //      which would otherwise make the network look relieved and oscillate). Cable-limited suppliers
        //      (CapSetting > CableCap) and APCs (no rating) are excluded -- cable overflow is rule 3.
        //   2. Elastic hit-max: a network still short after gen + inflow + full elastic discharge trips its
        //      live elastics, so a battery-fed subnet goes dark cleanly instead of partial-powering.
        //   3. §5.7 cable overflow: flow above the weakest cable cap with generators alone under it trips
        //      every supplier + elastic on the network (transformer/battery overflow does not burn cable).
        // Structural overload (rule 1): a network whose demand exceeds gen + elastic + its Setting-limited
        // suppliers' caps overloads those suppliers. Runs BEFORE the shed pass so a transformer that
        // structurally cannot serve its downstream is diagnosed as OVERLOAD (the higher-precedence fault)
        // instead of getting shed first and mislabeled input-starved. GROW-ONLY: never clears within a tick,
        // so the overload commits even if a same-tick shed in its subnetwork would have removed the condition
        // (desired: overload is the structural signal the player must act on). Desire-based, no forward
        // dependency, so it is safe to run before the forward pass.
        private static void DetectStructuralOverload(List<Net> netList, List<Seg> segs, bool overloadOn)
        {
            if (!overloadOn) return;
            foreach (var n in netList)
            {
                float demand = n.RigidDemand;
                foreach (var c in n.Consumers)
                    if (IsActive(c)) demand += c.DesiredPull;
                float cap = n.GenSupply + AvailableElastic(n);
                foreach (var s in n.Suppliers)
                    if (!s.Locked && !s.Shed) cap += s.EffCap;
                if (demand <= cap + Eps) continue;
                foreach (var s in n.Suppliers)
                {
                    if (s.Locked || s.Shed || s.Overloaded) continue;
                    if (s.CapSetting >= float.MaxValue) continue;   // APC: no throughput rating to hit
                    if (s.CapSetting > s.CableCap) continue;        // cable-limited (rule 3), not Setting-limited
                    s.Overloaded = true;                            // includes input-limited PT pairs (taken offline)
                }
            }
        }

        // Supply overload (rules 2 and 3): elastic hit-max and the §5.7 cable overflow. Both read the forward
        // pass's Unmet / PullsGranted, so they run AFTER ForwardSupplyAndShed. GROW-ONLY, like the structural
        // pass: never cleared within a tick.
        private static void DetectSupplyOverload(List<Net> netList, List<Elastic> elastics, bool overloadOn)
        {
            if (!overloadOn) return;

            foreach (var n in netList)   // rule 2: elastic hit-max
            {
                if (n.Unmet <= Eps) continue;
                foreach (var e in n.Elastics)
                    if (!e.Locked && !e.Overloaded) e.Overloaded = true;
            }

            foreach (var n in netList)   // rule 3: §5.7 cable overflow
            {
                float flow = (n.RigidDemand - n.Unmet) + n.PullsGranted;
                if (flow <= Eps) continue;
                float cableCap = CableMax.WeakestCapOnNetwork(n.Network);
                if (flow <= cableCap || n.GenSupply > cableCap) continue;
                foreach (var s in n.Suppliers)
                {
                    if (s.Locked || s.Shed || s.Overloaded) continue;
                    s.Overloaded = true;
                }
                foreach (var e in n.Elastics)
                    if (!e.Locked && !e.Overloaded) e.Overloaded = true;
            }
        }

        private static HashSet<long> CollectFlagged(List<Seg> segs, bool shed)
        {
            var set = new HashSet<long>();
            foreach (var seg in segs)
                if (shed ? seg.Shed : seg.Overloaded) set.Add(seg.RefId);
            return set;
        }

        private static HashSet<long> CollectFlaggedElastic(List<Elastic> elastics)
        {
            var set = new HashSet<long>();
            foreach (var e in elastics) if (e.Overloaded) set.Add(e.RefId);
            return set;
        }

        private static float AvailableElastic(Net n)
        {
            float total = 0f;
            foreach (var e in n.Elastics)
                if (!e.Locked && !e.Overloaded) total += e.EffDischarge;
            return total;
        }

        private static bool IsActive(Seg seg) => !seg.Locked && !seg.Shed && !seg.Overloaded;

        // (priority DESC, ReferenceId ASC): integer-only, MP-deterministic (§8.0.1).
        private static int SupplierOrder(Seg a, Seg b)
        {
            if (a.Priority != b.Priority) return b.Priority.CompareTo(a.Priority);
            return a.RefId.CompareTo(b.RefId);
        }

        // Shed victim selection (§8.3): (priority ASC, claim DESC quantised to whole Watts,
        // ReferenceId ASC). Returns < 0 when a should shed before b.
        private static int ShedVictimOrder(Seg a, Seg b)
        {
            if (a.Priority != b.Priority) return a.Priority.CompareTo(b.Priority);
            int aw = (int)Math.Floor(a.Pull);
            int bw = (int)Math.Floor(b.Pull);
            if (aw != bw) return bw.CompareTo(aw);
            return a.RefId.CompareTo(b.RefId);
        }

        private static bool IsStepUp(CableNetwork inNet, CableNetwork outNet)
        {
            var inTier = VoltageTierEnforcer.GetTierInfo(inNet).Tier;
            var outTier = VoltageTierEnforcer.GetTierInfo(outNet).Tier;
            if (!inTier.HasValue || !outTier.HasValue) return false;
            return TierRank(inTier.Value) < TierRank(outTier.Value);
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

        private static void AddUmbilical(ElectricalInputOutput umbilical, float stored, float maximum,
            List<Elastic> elastics, List<Soft> softs, int currentTick)
        {
            if (umbilical.Error == 1) return;
            if (umbilical.InputNetwork != null && umbilical.OutputNetwork != null
                && umbilical.InputNetwork.ReferenceId == umbilical.OutputNetwork.ReferenceId) return;
            bool limits = Settings.EnableRocketUmbilicalLimits.Value;
            float chargeRate = limits ? Settings.RocketUmbilicalChargeRate.Value : maximum;
            float dischargeRate = limits ? Settings.RocketUmbilicalDischargeRate.Value : maximum;
            bool locked = CycleFaultRegistry.IsCycleFaulted(umbilical.ReferenceId, currentTick)
                          || BrownoutRegistry.IsLockedOut(umbilical.ReferenceId, currentTick)
                          || OverloadRegistry.IsLockedOut(umbilical.ReferenceId, currentTick);
            if (umbilical.OutputNetwork != null && stored > 0f)
            {
                var outCable = umbilical.OutputConnection?.GetCable();
                elastics.Add(new Elastic
                {
                    RefId = umbilical.ReferenceId,
                    OutNet = umbilical.OutputNetwork,
                    EffDischarge = Mathf.Min(Mathf.Min(dischargeRate, CableMax.For(outCable)), stored),
                    Locked = locked,
                });
            }
            if (umbilical.InputNetwork != null)
            {
                float headroom = maximum - stored;
                if (headroom > 0f)
                {
                    var inCable = umbilical.InputConnection?.GetCable();
                    softs.Add(new Soft
                    {
                        RefId = umbilical.ReferenceId,
                        InNet = umbilical.InputNetwork,
                        Request = Mathf.Min(Mathf.Min(chargeRate, CableMax.For(inCable)), headroom),
                    });
                }
            }
        }

        // -------------------------------------------------------------------
        // Per-tick full fault-registry sync (host -> clients), POWER.md §13:
        // each non-empty registry broadcasts its full (refId, remainingTicks)
        // snapshot every tick; an empty registry sends exactly one empty
        // snapshot on the non-empty -> empty transition (so OFF-as-reset
        // clears client mirrors immediately), then goes silent.
        // -------------------------------------------------------------------

        private static bool _shedWasNonEmpty;
        private static bool _overloadWasNonEmpty;
        private static bool _cycleWasNonEmpty;
        private static bool _vvfWasNonEmpty;
        private static bool _deadInputWasNonEmpty;

        internal static void SyncFaultSnapshots(int currentTick)
        {
            if (!Assets.Scripts.Networking.NetworkManager.IsActive) return;
            if (!Assets.Scripts.Networking.NetworkManager.IsServer) return;

            SendPlain(FaultRegistrySnapshotMessage.KindShed,
                BrownoutRegistry.SnapshotRemaining(currentTick), ref _shedWasNonEmpty);
            SendPlain(FaultRegistrySnapshotMessage.KindOverload,
                OverloadRegistry.SnapshotRemaining(currentTick), ref _overloadWasNonEmpty);
            SendPlain(FaultRegistrySnapshotMessage.KindCycleFault,
                CycleFaultRegistry.SnapshotRemaining(currentTick), ref _cycleWasNonEmpty);
            SendPlain(FaultRegistrySnapshotMessage.KindDeadInput,
                DeadInputRegistry.SnapshotRemaining(), ref _deadInputWasNonEmpty);

            var vvf = new List<KeyValuePair<long, int>>();
            var violators = new List<string>();
            foreach (var (refId, remaining, names) in VariableVoltageFaultRegistry.SnapshotRemaining(currentTick))
            {
                vvf.Add(new KeyValuePair<long, int>(refId, remaining));
                violators.Add(names);
            }
            if (vvf.Count > 0 || _vvfWasNonEmpty)
            {
                new FaultRegistrySnapshotMessage
                {
                    Kind = FaultRegistrySnapshotMessage.KindVariableVoltage,
                    Entries = vvf,
                    Violators = violators,
                }.SendAll(0L);
            }
            _vvfWasNonEmpty = vvf.Count > 0;
        }

        private static void SendPlain(byte kind, IEnumerable<KeyValuePair<long, int>> snapshot, ref bool wasNonEmpty)
        {
            var entries = new List<KeyValuePair<long, int>>();
            foreach (var pair in snapshot) entries.Add(pair);
            if (entries.Count > 0 || wasNonEmpty)
                new FaultRegistrySnapshotMessage { Kind = kind, Entries = entries }.SendAll(0L);
            wasNonEmpty = entries.Count > 0;
        }
    }
}
