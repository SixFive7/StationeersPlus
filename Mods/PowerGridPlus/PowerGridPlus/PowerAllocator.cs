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
    ///     ALLOCATE of the atomic tick: the joint shed / overload / elastic-supply allocator
    ///     (POWER.md §8.0 / §7.3). Replaces the former transformer-only TransformerAllocator with
    ///     a model that covers every segmenting device class (§8.0.0.1): Transformer, AreaPowerControl,
    ///     linked PowerTransmitter/PowerReceiver pairs (modelled as one contributor anchored on the PT,
    ///     §6.2), Battery, and the rocket power umbilicals (the latter three as elastic suppliers / soft
    ///     demanders rather than pull-through contributors).
    ///
    ///     Two flow classes ride ONE demand vector through the same backward/forward sweep:
    ///       - RIGID: ordinary consumer demand plus the contributor pulls that serve it. Drives every
    ///         fault decision (shed, structural overload, supply overload).
    ///       - SOFT: storage charge (battery / APC cell / umbilical). Propagates leaf-to-source through
    ///         the same splitter as rigid but capped at each contributor's headroom left after rigid
    ///         (soft never displaces rigid capacity), and is granted forward out of the firm residual
    ///         only (generators + contributor inflow after rigid is served; elastic discharge never
    ///         funds charging). Unmet soft desire is silently clamped: never a shed, never an overload,
    ///         never a lockout, never a dead-input cue.
    ///
    ///     Per tick:
    ///       1. Gather: per-network rigid demand + generator supply; the contributor / elastic / soft
    ///          rosters from SegmentingDeviceRegistry. Each bridge device's PHYSICAL description
    ///          (flow kind, terminal networks, capacities, distance multiplier, quiescent draw) comes
    ///          from its ISegAdapter (SegAdapters.cs): the Routed adapters (Transformer / linked
    ///          wireless pair / APC) each yield one Seg, and the Buffered adapter (rocket umbilical)
    ///          yields its cell's soft charge request + elastic discharge capacity instead of a Seg.
    ///          GATHER attaches allocator POLICY on top of the description: priority, lockout state,
    ///          and the shed/overload bookkeeping.
    ///       2. Order: topological (Kahn) order over conducting contributor edges, so every network is
    ///          processed after all the networks that feed it. Cycle members are already CYCLE_FAULTed by
    ///          PROTECT (cycle detection) and conduct 0, so the live graph is a DAG.
    ///       3. Fixed-point loop (max 2N+4 rounds, §8.0). OVERLOAD is grow-only / sticky (cleared once at
    ///          loop entry, then committed for the tick); only SHED is re-decided each round. Each round:
    ///          a. backward desire (leaf -> source): each network's residual rigid need (rigid + deeper
    ///             pulls - generators - elastic availability) splits greedily over its suppliers in
    ///             (priority DESC, ReferenceId ASC) order, capped per-device at effective cap; then the
    ///             network's soft desire (local charge requests + deeper soft pulls) splits over the same
    ///             suppliers with the same priority-tier-first proportional splitter, capped per-device
    ///             at (EffCap - rigid desired throughput);
    ///          b. structural overload, evaluated BEFORE shed (§8.4 hit-max: a contributor at its
    ///             Setting-like cap with unmet downstream rigid demand -- RIGID ONLY), so a structurally-
    ///             overloaded device surfaces as OVERLOAD, not shed (CYCLE > VVF > OVERLOAD > SHED);
    ///          c. forward supply + re-decided shed per input network: when consumer RIGID claims exceed
    ///             the input budget, victims shed in (priority ASC, claim DESC, ReferenceId ASC) order
    ///             (§8.3); step-up transformers never shed (§5.2); a dead input (no supply at all) defers
    ///             instead of cycling 60-second lockouts. After the rigid grants, soft is granted from the
    ///             net's firm residual (plus soft inflow arriving through its suppliers), proportionally
    ///             over local charge requests and consumer soft pulls, capped by the weakest cable's
    ///             remaining headroom; a shed / locked / overloaded contributor gets zero soft;
    ///          d. supply overload after the forward pass (RIGID ONLY): the elastic analog (a battery /
    ///             APC cell / umbilical delivering its full effective discharge with rigid demand still
    ///             unmet) and the §5.7 cable-overflow rule (rigid flow above the weakest cable's cap with
    ///             generators alone under it trips every supplier of that network instead of burning the
    ///             cable).
    ///       4. Elastic shares (§7.3): per output network, batteries cover only the RIGID shortfall
    ///          left after generators + transformer inflow; proportional split against effective caps
    ///          (min(rate cap, stored)); written to SoftSupplyShareCache for the GetGeneratedPower
    ///          postfixes. Elastic stays net-local; it never propagates across contributors.
    ///       5. Publish: per-device charge shares to SoftDemandShareCache; per-contributor presentation
    ///          totals (TotalThrough = rigid + soft throughput, TotalPull = TotalThrough * max(m,1) +
    ///          quiescent) to TransformerSupplyCache for EVERY routed seg kind, plus the APC bundle
    ///          caches. The vanilla-facing advertise/bill patches serve these totals verbatim, so a
    ///          granted soft flow always has a carrier on both terminals of its segment.
    ///       6. Conservation check (ConservationChecker, config-gated): per net, granted inflow must
    ///          equal granted outflow within tolerance; per seg, TotalPull must equal
    ///          TotalThrough * max(m,1) + quiescent. Violations log throttled warnings.
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
            public int Priority;
            public int Depth;
            public bool StepUp;            // never sheds (§5.2)
            public bool Locked;            // cycle-faulted or in a prior-tick shed/overload window: conducts 0, not re-decided
            // per-round:
            public bool Shed;
            public bool Overloaded;
            public float Throughput;       // committed RIGID passthrough this round
            public float Pull;             // the rigid demand presented on InNet (Throughput * m + UsedPower when granted)
            // Backward desire pass scratch: what this contributor WANTS to pass assuming its input can
            // supply it (capped at EffCap), and the matching input-side draw.
            public float DesiredThroughput;
            public float DesiredPull;
            // Soft (storage-charge) flow, riding the same backward/forward sweep as rigid. The
            // contributor's quiescent draw is carried exactly once: on the rigid pull when the
            // contributor carries any rigid flow, else on the soft pull.
            public float SoftDesiredThroughput;   // backward: share of OutNet's soft desire (output-side Watts)
            public float SoftDesiredPull;         // backward: the matching input-side draw
            public float SoftThrough;             // forward: granted soft passthrough (output-side Watts)
            public float SoftPull;                // forward: granted input-side soft draw
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

        // A soft demander: charges a store from InNet out of the firm residual only (§7.4).
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
            // Static per tick: total local storage charge requests (sum of Softs' Request).
            public float SoftRequestLocal;
            // per-round:
            public float Unmet;
            public float PullsGranted;
            public float InflowCommitted;
            public float RigidServed;
            // Backward desire pass scratch: total power demanded on this network assuming nothing is
            // shed (rigid + every non-locked, non-overloaded consumer's desired pull), and the soft
            // analog (local charge requests + every active consumer's soft desired pull).
            public float DesiredDemand;
            public float SoftDesire;
            // Forward soft grants (for the conservation check): local charge granted + soft pulls granted.
            public float SoftGrantedLocal;
            public float SoftPullsGranted;
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

            // Attach allocator POLICY to an adapter's physical description (SegAdapters.cs):
            // priority, and the lockout gate over the device's own registries plus -- for a paired
            // bridge -- the partner half's cycle-fault state (either dish faulted locks the pair).
            Seg MakeSeg(SegKind kind, long refId, in SegSpec spec)
            {
                return new Seg
                {
                    RefId = refId,
                    Kind = kind,
                    InNet = spec.InNet,
                    OutNet = spec.OutNet,
                    CapSetting = spec.CapacitySetting,
                    CableCap = spec.RateLimit,
                    EffCap = spec.EffectiveCapacity,
                    UsedPower = spec.Quiescent,
                    InputDrawFactor = spec.Multiplier,
                    Priority = PriorityStore.GetPriority(refId),
                    StepUp = spec.StepUp,
                    Locked = IsPowerLocked(refId)
                        || (spec.PartnerRefId != 0L && CycleFaultRegistry.IsCycleFaulted(spec.PartnerRefId, currentTick)),
                };
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
                        if (!SegAdapters.Transformer.TryDescribe(t, out var spec)) break;
                        segs.Add(MakeSeg(SegKind.Transformer, t.ReferenceId, spec));
                        break;
                    }
                    case PowerTransmitter pt:
                    {
                        // One seg per LINKED pair, anchored on the transmitter. The link-rating and
                        // source-draw-multiplier physics (PowerTransmitterPlus ModApi tier, legacy
                        // reflection tier, or the vanilla curve) live in WirelessPairAdapter; the
                        // partner receiver's cycle-fault state locks the pair via MakeSeg.
                        if (!SegAdapters.WirelessPair.TryDescribe(pt, out var spec)) break;
                        segs.Add(MakeSeg(SegKind.PtPair, pt.ReferenceId, spec));
                        break;
                    }
                    case PowerReceiver _:
                        break;   // handled via its linked PT (the pair is anchored on the transmitter)
                    case AreaPowerControl apc:
                    {
                        if (!SegAdapters.Apc.TryDescribe(apc, out var spec)) break;
                        var seg = MakeSeg(SegKind.Apc, apc.ReferenceId, spec);
                        segs.Add(seg);
                        // The APC's internal cell is a storage half alongside the routed seg (not
                        // part of the adapter's description): discharge is elastic onto the output
                        // network, charge is a soft request on the input network. Same lock state
                        // as the seg (the APC's own registries; no partner).
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
                                        Mathf.Min(ApcDischargeRateRegistry.GetDischargeRate(apc.ReferenceId),
                                            CableMax.For(apc.OutputConnection?.GetCable())),
                                        cell.PowerStored),
                                    Locked = seg.Locked,
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
                    case RocketPowerUmbilical umbilical:
                    {
                        // Buffered contract (UmbilicalAdapter, covers both halves via the shared
                        // 0.2.6403 base class): no routed seg. The description enrolls the cell's
                        // charge side as a Soft demander and its discharge side as an Elastic
                        // supplier; the cell-to-cell crossing between the two halves is vanilla
                        // phase 2, outside the allocator (see the adapter's doc).
                        if (!SegAdapters.Umbilical.TryDescribe(umbilical, out var spec)) break;
                        bool locked = IsPowerLocked(umbilical.ReferenceId);
                        if (spec.HasElastic)
                        {
                            elastics.Add(new Elastic
                            {
                                RefId = umbilical.ReferenceId,
                                OutNet = spec.OutNet,
                                EffDischarge = spec.ElasticCapacity,
                                Locked = locked,
                            });
                        }
                        if (spec.HasSoft)
                            softs.Add(new Soft { RefId = umbilical.ReferenceId, InNet = spec.InNet, Request = spec.SoftRequest });
                        break;
                    }
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
            foreach (var s in softs)
            {
                var n = GetNet(s.InNet);
                if (n == null) continue;
                n.Softs.Add(s);
                n.SoftRequestLocal += s.Request;
            }

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
            //    the per-net field values it leaves (Unmet / InflowCommitted / PullsGranted / RigidServed /
            //    SoftGrantedLocal / SoftPullsGranted / Throughput / SoftThrough) feed the shared
            //    dead-input / commit / elastic-share / publish tail below.
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
            // 5. PUBLISH: write the share caches (ENFORCE's postfixes read these).
            // ----------------------------------------------------------------
            foreach (var e in elastics)
            {
                // For APCs the GetGeneratedPower surface bundles passthrough + cell (vanilla
                // AvailablePower), so the cap must include the total committed passthrough (rigid +
                // soft); the matching Seg is found below.
                SoftSupplyShareCache.SetShare(e.RefId, e.Share);
            }
            // Storage charge shares: the forward sweep's per-device soft grants. The soft-demand
            // GetUsedPower postfixes cap the reported charge demand to these, so each store charges
            // exactly what the allocator granted.
            foreach (var s in softs)
                SoftDemandShareCache.SetShare(s.RefId, s.Share);

            // Publish each routed contributor's exact converged PRESENTATION TOTALS so its
            // GetGeneratedPower / GetUsedPower report the real in-tick flow (no headroom), soft
            // included. Output side = TotalThrough (rigid Throughput + granted SoftThrough); input
            // side = TotalPull (rigid Pull + granted SoftPull == TotalThrough * max(m,1) + the
            // contributor's own quiescent draw whenever it conducts). Publishing totals for EVERY seg
            // kind is what guarantees a granted soft flow has a carrier on both terminals of its
            // segment: a battery charging behind a transformer or wireless pair sees the charge
            // advertised downstream AND billed upstream in the same tick. Inactive contributors
            // (shed / overloaded / cycle-faulted) carry all-zero totals, so they report 0 both ways.
            foreach (var seg in segs)
            {
                float totalThrough = seg.Throughput + seg.SoftThrough;
                float totalPull = seg.Pull + seg.SoftPull;
                if (seg.Kind == SegKind.Transformer || seg.Kind == SegKind.PtPair)
                {
                    // The transformer bills via TransformerExploitPatches; the wireless PT/PR pair
                    // bills its input draw via PowerTransmitterDrawPatches on the PT (seg.RefId) and
                    // advertises through the DeliveryGatePatch clamp. Both read this cache pair.
                    TransformerSupplyCache.Set(seg.RefId, totalThrough, totalPull);
                }
                else if (seg.Kind == SegKind.Apc)
                {
                    // Fresh passthrough draw the APC bills on its INPUT network: total committed
                    // passthrough (rigid + soft-charge flowing through). Replaces the lagging
                    // _powerProvided in AreaPowerControlPatches.GetUsedPower so the APC's input draw
                    // is current (input == output, no one-tick lag -> no input-network undershoot).
                    ApcPassthroughCache.Set(seg.RefId, totalThrough);
                    // The APC's bundled supply (total passthrough + cell) goes to SoftSupplyShareCache
                    // because vanilla GetGeneratedPower bundles them. But DischargeSpeed must mean the
                    // CELL rate, consistent with battery / umbilical (P9), so stamp the cell-only share
                    // separately first, before the bundled entry overwrites it.
                    float cellShare = 0f;
                    foreach (var e in elastics)
                        if (e.RefId == seg.RefId) { cellShare = e.Share; break; }
                    ApcCellDischargeCache.SetShare(seg.RefId, cellShare);
                    SoftSupplyShareCache.SetShare(seg.RefId, totalThrough + cellShare);
                    // Presentation totals for the APC too, so every routed seg kind publishes the same
                    // (TotalThrough, TotalPull) surface (nothing reads the APC entry yet; the APC
                    // patches bill quiescent + cell charge themselves on top of the passthrough cache).
                    TransformerSupplyCache.Set(seg.RefId, totalThrough, totalPull);
                }
            }

            // ----------------------------------------------------------------
            // 6. CONSERVATION CHECK (config-gated): audit the converged grants. Per net, granted
            //    inflow == granted outflow within tolerance; per seg, TotalPull == TotalThrough *
            //    max(m,1) + quiescent. A violation is a code bug in the allocator, never a player
            //    problem; warnings are throttled per net / per seg.
            // ----------------------------------------------------------------
            if (ConservationChecker.Enabled)
            {
                foreach (var n in netList)
                {
                    float softInflow = 0f;
                    foreach (var s in n.Suppliers)
                        if (IsActive(s)) softInflow += s.SoftThrough;
                    float elasticGranted = 0f;
                    foreach (var e in n.Elastics) elasticGranted += e.Share;
                    ConservationChecker.CheckNet(n.Id, currentTick, n.GenSupply, n.InflowCommitted,
                        softInflow, elasticGranted, n.RigidServed, n.PullsGranted, n.SoftPullsGranted,
                        n.SoftGrantedLocal);
                }
                foreach (var seg in segs)
                    ConservationChecker.CheckSeg(seg.RefId, seg.Kind.ToString(), currentTick,
                        seg.Throughput + seg.SoftThrough, seg.Pull + seg.SoftPull,
                        seg.InputDrawFactor, seg.UsedPower);
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

        // Backward demand sweep (leaf -> source), carrying BOTH flow classes in one walk. Each
        // contributor's DesiredThroughput = its share of its output network's rigid demand (priority
        // tier DESC, proportional by EffCap within a tier, capped at EffCap), and DesiredPull = the
        // matching input-side draw (throughput * distance factor + quiescent). Generators are
        // subtracted first; elastic storage is the documented last resort and is not modelled here (it
        // absorbs the per-net shortfall in the forward sweep / elastic-share pass, matching the
        // gen -> transformer -> battery supply order).
        //
        // The soft class follows: SoftDesiredThroughput = the contributor's share of its output
        // network's soft desire (local charge requests + deeper soft pulls), split with the SAME
        // priority-tier-first proportional splitter but capped per contributor at
        // (EffCap - DesiredThroughput), so soft never displaces rigid capacity. The proportional split
        // is what kills the old surplus walk's double-count: parallel contributors divide the
        // downstream request instead of each propagating it whole. The quiescent draw rides the rigid
        // pull when the contributor carries any rigid flow, else the soft pull -- exactly once.
        private static void BackwardDesirePass(List<Net> topoRev)
        {
            foreach (var n in topoRev)
            {
                float pulls = 0f;
                float softPulls = 0f;
                foreach (var c in n.Consumers)
                {
                    c.DesiredPull = (DesireActive(c) && c.DesiredThroughput > 0f)
                        ? c.DesiredThroughput * Mathf.Max(c.InputDrawFactor, 1f) + c.UsedPower
                        : 0f;
                    pulls += c.DesiredPull;
                    // Soft gates on IsActive, NOT DesireActive: rigid must keep a shed contributor's
                    // claim visible so the forward pass can re-decide the shed, but soft never drives
                    // a shed, so a shed contributor's charge desire must NOT size its suppliers --
                    // the delivered soft would strand on this net (billed upstream, consumed by
                    // nobody). An un-shed next round restores the desire one round later.
                    c.SoftDesiredPull = (IsActive(c) && c.SoftDesiredThroughput > 0f)
                        ? c.SoftDesiredThroughput * Mathf.Max(c.InputDrawFactor, 1f)
                          + (c.DesiredPull > 0f ? 0f : c.UsedPower)
                        : 0f;
                    softPulls += c.SoftDesiredPull;
                }
                n.DesiredDemand = n.RigidDemand + pulls;
                n.SoftDesire = n.SoftRequestLocal + softPulls;

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

                // Soft split over the same suppliers: same tier walk, but each contributor's capacity
                // is the headroom LEFT after its rigid desired throughput. Runs independently of the
                // rigid residual (a net can have zero rigid residual and still route charge).
                float softResidual = n.SoftDesire;
                si = 0;
                while (si < supList.Count && softResidual > Eps)
                {
                    int tierPriority = supList[si].Priority;
                    int blockEnd = si;
                    float tierCap = 0f;
                    while (blockEnd < supList.Count && supList[blockEnd].Priority == tierPriority)
                    {
                        var seg = supList[blockEnd];
                        if (DesireActive(seg))
                        {
                            float head = seg.EffCap - seg.DesiredThroughput;
                            if (head > 0f) tierCap += head;
                        }
                        else seg.SoftDesiredThroughput = 0f;
                        blockEnd++;
                    }
                    float tierGive = tierCap > Eps ? Mathf.Min(tierCap, softResidual) : 0f;
                    for (int j = si; j < blockEnd; j++)
                    {
                        var seg = supList[j];
                        if (!DesireActive(seg)) continue;
                        float head = seg.EffCap - seg.DesiredThroughput;
                        if (head < 0f) head = 0f;
                        seg.SoftDesiredThroughput = tierCap > Eps ? tierGive * (head / tierCap) : 0f;
                    }
                    softResidual -= tierGive;
                    si = blockEnd;
                }
                for (; si < supList.Count; si++)
                    supList[si].SoftDesiredThroughput = 0f;
            }
        }

        // Forward supply sweep (source -> leaf). For each network in topo order (so every supplier's
        // actual throughput is already finalized), compute the supply actually reaching it and distribute
        // to its consumers highest-priority-first. When deciding (settleOnly == false) shedding is RE-
        // DECIDED here: if the active consumers' desired RIGID pulls exceed the budget the network can
        // pass, the lowest-priority victims shed (whole, never partial) until the rest fit; a network with
        // no supply at all (avail <= Eps) sheds nothing (dead-input idle). settleOnly == true keeps the
        // current shed/overload flags and only recomputes throughputs + per-net fields (used to settle a
        // 2-cycle). After the rigid grants, SOFT (storage charge) is granted per net from the firm
        // residual: shed decisions, budgets, and Unmet never see the soft class.
        private static void ForwardSupplyAndShed(List<Net> topo, List<Seg> segs, bool shedOn, bool settleOnly)
        {
            foreach (var seg in segs)
            {
                seg.Throughput = 0f;
                seg.Pull = 0f;
                seg.SoftThrough = 0f;
                seg.SoftPull = 0f;
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
                n.RigidServed = avail < n.RigidDemand ? avail : n.RigidDemand;

                float activeWant = n.RigidDemand;
                foreach (var c in n.Consumers) if (IsActive(c)) activeWant += c.DesiredPull;
                float unmet = activeWant - avail;
                n.Unmet = unmet > 0f ? unmet : 0f;

                // ---- Soft grants (storage charge), funded by the FIRM residual only ----
                // Pool: local firm supply left after rigid loads and granted rigid pulls (never
                // elastic: a battery must not discharge to charge another store), plus the soft
                // inflow arriving through this net's suppliers (granted when their input nets were
                // processed earlier in topo order). Capped by the weakest cable's remaining headroom
                // so a charge grant cannot push a network past its tier rating. Granted
                // proportionally over local charge requests and active consumers' soft pulls; a
                // shed / locked / overloaded consumer gets zero soft. Note: if any rigid pull on
                // this net was only partially granted, the firm residual is necessarily zero, so a
                // soft grant here implies every rigid pull was granted whole.
                float softLocal = firmIn - n.RigidDemand - survivorPull;
                if (softLocal < 0f) softLocal = 0f;
                float softInflow = 0f;
                foreach (var s in n.Suppliers)
                    if (IsActive(s)) softInflow += s.SoftThrough;
                float softAvail = softLocal + softInflow;
                if (softAvail > 0f)
                {
                    float cableHeadroom = CableMax.WeakestCapOnNetwork(n.Network) - (n.RigidServed + survivorPull);
                    if (cableHeadroom < 0f) cableHeadroom = 0f;
                    if (softAvail > cableHeadroom) softAvail = cableHeadroom;
                }

                float softDemand = n.SoftRequestLocal;
                foreach (var c in n.Consumers)
                    if (IsActive(c) && c.SoftDesiredPull > 0f) softDemand += c.SoftDesiredPull;

                float softRatio = (softAvail > 0f && softDemand > Eps)
                    ? Mathf.Min(1f, softAvail / softDemand)
                    : 0f;

                float softGrantedLocal = 0f;
                foreach (var s in n.Softs)
                {
                    s.Share = s.Request * softRatio;
                    softGrantedLocal += s.Share;
                }
                float softPullsGranted = 0f;
                foreach (var c in n.Consumers)
                {
                    if (!IsActive(c) || c.SoftDesiredPull <= 0f) { c.SoftThrough = 0f; c.SoftPull = 0f; continue; }
                    float grant = c.SoftDesiredPull * softRatio;
                    float m = Mathf.Max(c.InputDrawFactor, 1f);
                    // Quiescent rides the soft pull only when the rigid side carries none (the
                    // backward pass made the same choice when it sized SoftDesiredPull).
                    float q = c.DesiredPull > 0f ? 0f : c.UsedPower;
                    float outThr = (grant - q) / m;
                    if (outThr < 0f) outThr = 0f;
                    float headroom = c.EffCap - c.Throughput;
                    if (headroom < 0f) headroom = 0f;
                    if (outThr > headroom) outThr = headroom;
                    c.SoftThrough = outThr;
                    c.SoftPull = outThr > 0f ? outThr * m + q : Mathf.Min(grant, q);
                    softPullsGranted += c.SoftPull;
                }
                n.SoftGrantedLocal = softGrantedLocal;
                n.SoftPullsGranted = softPullsGranted;
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
