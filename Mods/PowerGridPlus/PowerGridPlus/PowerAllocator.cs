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
    ///     Phase 2 of the atomic tick: the joint shed / overload / elastic-supply / surplus allocator
    ///     (POWER.md §8.0 / §7.3 / §9). Replaces the former transformer-only TransformerAllocator with
    ///     a model that covers every segmenting device class (§8.0.0.1): Transformer, AreaPowerControl,
    ///     linked PowerTransmitter/PowerReceiver pairs (modelled as one contributor anchored on the PT,
    ///     §6.2), Battery, and the rocket power umbilicals (the latter three as elastic suppliers / soft
    ///     demanders rather than pull-through contributors).
    ///
    ///     Per tick:
    ///       1. Gather: per-network rigid demand + generator supply; the contributor / elastic / soft
    ///          rosters from SegmentingDeviceRegistry.
    ///       2. Depth: BFS from source networks (generator- or storage-bearing) over conducting
    ///          contributor edges. Cycle members are already CYCLE_FAULTed by Phase 1.5b and conduct 0,
    ///          so the live graph is a DAG.
    ///       3. Fixed-point loop (grow-only SHED + OVERLOAD sets, max 2N+4 rounds, §8.0):
    ///          a. demand propagation deepest-first: each network's residual need (rigid + deeper pulls
    ///             - generators - elastic availability) splits greedily over its suppliers in
    ///             (priority DESC, ReferenceId ASC) order, capped per-device at effective cap;
    ///          b. shed evaluation per input network: when consumer claims exceed the input budget,
    ///             victims shed in (priority ASC, claim DESC, ReferenceId ASC) order (§8.3); step-up
    ///             transformers never shed (§5.2); a dead input (no supply at all) defers instead of
    ///             cycling 60-second lockouts;
    ///          c. overload evaluation per device (§8.4 hit-max: throughput at the device's Setting-like
    ///             cap with unmet downstream rigid demand), the elastic analog (a battery / APC cell /
    ///             umbilical delivering its full effective discharge with rigid demand still unmet),
    ///             and the §5.7 cable-overflow rule (flow above the weakest cable's cap with generators
    ///             alone under it trips every supplier of that network instead of burning the cable).
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
            // 2. DEPTH (BFS over conducting contributor edges).
            // ----------------------------------------------------------------
            var frontier = new List<Net>();
            foreach (var n in netList)
            {
                bool hasLiveElastic = false;
                foreach (var e in n.Elastics) if (!e.Locked) { hasLiveElastic = true; break; }
                if (n.GenSupply > 0f || hasLiveElastic || n.Suppliers.Count == 0)
                {
                    n.Depth = 0;
                    frontier.Add(n);
                }
            }
            frontier.Sort((a, b) => a.Id.CompareTo(b.Id));
            int depth = 0;
            while (frontier.Count > 0)
            {
                depth++;
                var next = new List<Net>();
                foreach (var n in frontier)
                {
                    foreach (var seg in n.Consumers)
                    {
                        if (seg.Locked) continue;
                        var outNet = nets[seg.OutNet.ReferenceId];
                        if (outNet.Depth > depth)
                        {
                            outNet.Depth = depth;
                            next.Add(outNet);
                        }
                    }
                }
                next.Sort((a, b) => a.Id.CompareTo(b.Id));
                frontier = next;
            }
            foreach (var seg in segs)
                seg.Depth = nets[seg.InNet.ReferenceId].Depth;

            // Deterministic walk orders.
            var netsDeepFirst = new List<Net>(netList);
            netsDeepFirst.Sort((a, b) =>
            {
                int c = b.Depth.CompareTo(a.Depth);
                return c != 0 ? c : a.Id.CompareTo(b.Id);
            });
            var netsShallowFirst = new List<Net>(netList);
            netsShallowFirst.Sort((a, b) =>
            {
                int c = a.Depth.CompareTo(b.Depth);
                return c != 0 ? c : a.Id.CompareTo(b.Id);
            });
            foreach (var n in netList)
            {
                n.Suppliers.Sort(SupplierOrder);
                n.Consumers.Sort(SupplierOrder);
            }

            // ----------------------------------------------------------------
            // 3. FIXED-POINT LOOP (§8.0). Sheds/overloads only grow.
            // ----------------------------------------------------------------
            int maxRounds = 2 * segs.Count + 4;
            bool changed = true;
            for (int round = 0; round < maxRounds && changed; round++)
            {
                changed = false;
                EvaluateDemand(netsDeepFirst, nets);

                if (shedOn)
                {
                    // Shed evaluation per input network (§8.3): budget = local supply available to
                    // consumers after local rigid demand; victims shed lowest-priority-first.
                    foreach (var n in netsShallowFirst)
                    {
                        if (n.Consumers.Count == 0) continue;
                        float totalAvail = n.GenSupply + n.InflowCommitted + AvailableElastic(n);
                        float budget = totalAvail - n.RigidDemand;
                        if (budget < 0f) budget = 0f;

                        float claims = 0f;
                        foreach (var c in n.Consumers)
                            if (IsActive(c)) claims += c.Pull;
                        if (claims <= budget + Eps) continue;

                        // Dead input: nothing supplies this network at all. Consumers idle instead of
                        // cycling 60-second sheds (pass-1 behaviour, user-approved).
                        if (totalAvail <= Eps) continue;

                        while (claims > budget + Eps)
                        {
                            Seg victim = null;
                            foreach (var c in n.Consumers)
                            {
                                if (!IsActive(c) || c.StepUp || c.Pull <= Eps) continue;
                                if (victim == null || ShedVictimOrder(c, victim) < 0) victim = c;
                            }
                            if (victim == null) break;   // nothing shed-eligible left: accept
                            victim.Shed = true;
                            changed = true;
                            claims -= victim.Pull;
                        }
                    }
                }

                if (overloadOn)
                {
                    // Per-device hit-max (§8.4): the device is delivering everything its Setting-like
                    // cap allows (EffCap when Setting, not the cable, is the binding constraint) while
                    // downstream rigid demand stays unmet. A cable-throttled device running below its
                    // Setting does NOT trip (§8.4's discriminator case), and a Setting = 0 device is
                    // the documented "disabled via IC10" state and never trips (Throughput stays 0).
                    foreach (var seg in segs)
                    {
                        if (!IsActive(seg) || seg.Overloaded) continue;
                        if (seg.CapSetting >= float.MaxValue) continue;            // APC: no rating to hit
                        if (seg.CapSetting > seg.CableCap) continue;               // cable-limited, not Setting-limited
                        var outNet = nets[seg.OutNet.ReferenceId];
                        if (outNet.Unmet <= Eps) continue;
                        if (seg.Throughput > 0f && seg.Throughput >= seg.EffCap - Eps)
                        {
                            // P6: a PT/PR pair held at its deliverable by its INPUT supply (InputLimited)
                            // is not at its own rated cap, the source is the bottleneck, so route the
                            // shortfall to SHED ("insufficient upstream supply") rather than OVERLOAD ("the
                            // link cannot carry the demand"). Both take the pair offline (no-partial-power);
                            // only the diagnosis and the registry differ. Every other seg overloads as before.
                            if (seg.InputLimited)
                                seg.Shed = true;
                            else
                                seg.Overloaded = true;
                            changed = true;
                        }
                    }
                    // Elastic hit-max analog: a storage device delivering its full effective discharge
                    // with rigid demand still unmet trips OVERLOAD, so the subnet goes dark cleanly
                    // instead of partial-powering (the no-partial-power invariant on battery-fed nets).
                    foreach (var n in netList)
                    {
                        if (n.Unmet <= Eps) continue;
                        foreach (var e in n.Elastics)
                        {
                            if (e.Locked || e.Overloaded) continue;
                            e.Overloaded = true;
                            changed = true;
                        }
                    }
                    // §5.7 cable overflow: flow above the weakest cable cap with generators alone under
                    // it trips every supplier (transformer-derived overflow does not burn cables).
                    foreach (var n in netList)
                    {
                        float flow = (n.RigidDemand - n.Unmet) + n.PullsGranted;
                        if (flow <= Eps) continue;
                        float cap = CableMax.WeakestCapOnNetwork(n.Network);
                        if (flow <= cap || n.GenSupply > cap) continue;
                        foreach (var seg in n.Suppliers)
                        {
                            if (!IsActive(seg) || seg.Overloaded) continue;
                            seg.Overloaded = true;
                            changed = true;
                        }
                        foreach (var e in n.Elastics)
                        {
                            if (e.Locked || e.Overloaded) continue;
                            e.Overloaded = true;
                            changed = true;
                        }
                    }
                }
            }

            // Final demand pass so shares reflect the converged state.
            EvaluateDemand(netsDeepFirst, nets);

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
                foreach (var c in n.Consumers)
                    if (IsActive(c) && c.Throughput > Eps)
                        DeadInputRegistry.MarkDeadInput(c.RefId);
            }

            // ----------------------------------------------------------------
            // Commit new lockouts. Option C: a network with a cable burn in flight (a tier burn queued
            // this tick, or a §5.7 generator-overflow burn issued last tick whose split has not landed)
            // is about to re-partition, so its merged-topology supply/demand math is not the topology
            // the lockout would apply to. Defer committing NEW shed / overload lockouts for such a
            // network until the split lands (SplitPendingRegistry clears it). Existing lockouts
            // (seg.Locked) still enforce; the network still distributes power via vanilla Phase 3.
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
            // Write the share caches (Phase 3's postfixes read these).
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
        }

        // Option C gate: true when a cable burn is in flight on either side of this contributor's span,
        // so the allocator must not commit a durable lockout against a topology that is about to split.
        private static bool SegNetPending(Seg seg)
            => (seg.InNet != null && SplitPendingRegistry.IsPending(seg.InNet.ReferenceId))
               || (seg.OutNet != null && SplitPendingRegistry.IsPending(seg.OutNet.ReferenceId));

        // Demand propagation, deepest-first (one pass per round; the conducting graph is a DAG because
        // cycle members are faulted). Computes per-net residual need, supplier throughputs, pulls, and
        // unmet remainder under the CURRENT shed/overload state.
        private static void EvaluateDemand(List<Net> netsDeepFirst, Dictionary<long, Net> nets)
        {
            foreach (var n in netsDeepFirst)
            {
                n.Unmet = 0f;
                n.PullsGranted = 0f;
                n.InflowCommitted = 0f;
                n.ElasticDelivered = 0f;
            }
            foreach (var n in netsDeepFirst)
            {
                // Need on this network: local rigid demand plus the pulls of consumers drawing from it
                // (their pulls were computed when their deeper output networks were processed).
                float pulls = 0f;
                foreach (var c in n.Consumers)
                {
                    // PT pairs draw Throughput * m on their INPUT network (distance overhead, §8.4.2);
                    // InputDrawFactor is m for a PT pair and 0 (treated as 1) for every other contributor.
                    c.Pull = (IsActive(c) && c.Throughput > 0f)
                        ? c.Throughput * Mathf.Max(c.InputDrawFactor, 1f) + c.UsedPower
                        : 0f;
                    pulls += c.Pull;
                }
                n.PullsGranted = pulls;
                float need = n.RigidDemand + pulls;

                // Supply order (§7.3 / the soft-power Stationpedia text): generators first, then
                // pull-through contributors (transformers / APCs / PT pairs) in (priority DESC,
                // ReferenceId ASC) order, and elastic storage strictly LAST -- a battery discharges
                // only the shortfall nothing else covers.
                float residual = need - n.GenSupply;
                if (residual < 0f) residual = 0f;

                // Split residual demand across parallel suppliers (P8 / POWER.md §8.3.2): greedy by
                // PRIORITY TIER (higher-priority tiers fill before lower ones -> primary/backup banks),
                // PROPORTIONAL by EffCap WITHIN a tier (equal-priority suppliers share their tier's load
                // in proportion to capacity). Suppliers are pre-sorted (priority DESC, RefId ASC), so a
                // priority tier is a contiguous block. Proportional-by-cap is self-bounding: each share is
                // tierGive * EffCap/tierCap <= EffCap, so no per-supplier clamp is needed, and the shares
                // sum to exactly tierGive. Ordering is integer-keyed (MP-deterministic, §8.0.1); the
                // within-tier division is float but bit-identical across peers on the shared runtime.
                var supList = n.Suppliers;
                int si = 0;
                while (si < supList.Count && residual > Eps)
                {
                    int tierPriority = supList[si].Priority;
                    int blockEnd = si;
                    float tierCap = 0f;
                    while (blockEnd < supList.Count && supList[blockEnd].Priority == tierPriority)
                    {
                        if (IsActive(supList[blockEnd])) tierCap += supList[blockEnd].EffCap;
                        else supList[blockEnd].Throughput = 0f;
                        blockEnd++;
                    }
                    float tierGive = tierCap > Eps ? Mathf.Min(tierCap, residual) : 0f;
                    for (int j = si; j < blockEnd; j++)
                    {
                        var seg = supList[j];
                        if (!IsActive(seg)) continue;
                        float share = tierCap > Eps ? tierGive * (seg.EffCap / tierCap) : 0f;
                        seg.Throughput = share;
                        n.InflowCommitted += share;
                    }
                    residual -= tierGive;
                    si = blockEnd;
                }
                // Lower-priority suppliers not reached (demand already met) deliver nothing.
                for (; si < supList.Count; si++)
                    supList[si].Throughput = 0f;

                float elasticUsed = Mathf.Min(residual, AvailableElastic(n));
                n.ElasticDelivered = elasticUsed;
                residual -= elasticUsed;
                n.Unmet = residual;
            }
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
