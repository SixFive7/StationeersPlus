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
    ///     ALLOCATE of the atomic tick: the joint deprioritization / overload / elastic-supply allocator
    ///     (POWER.md §8.0 / §7.3). Replaces the former transformer-only TransformerAllocator with
    ///     a model that covers every segmenting device class (§8.0.0.1): Transformer, AreaPowerControl,
    ///     linked PowerTransmitter/PowerReceiver pairs (modelled as one contributor anchored on the PT,
    ///     §6.2), Battery, and the rocket power umbilicals (the latter three as elastic suppliers / soft
    ///     demanders rather than pull-through contributors).
    ///
    ///     Two flow classes ride ONE demand vector through the same backward/forward sweep:
    ///       - RIGID: ordinary consumer demand plus the contributor pulls that serve it. Drives every
    ///         fault decision (deprioritization, structural overload, supply overload).
    ///       - SOFT: storage charge (battery / APC cell / umbilical). Propagates leaf-to-source through
    ///         the same splitter as rigid but capped at each contributor's headroom left after rigid
    ///         (soft never displaces rigid capacity), and is granted forward per net from a three-rung
    ///         funding ladder consumed in order (POWER.md 9.2): the firm residual (generators +
    ///         contributor inflow after rigid is served), the soft inflow arriving through suppliers,
    ///         and LAST the net's elastic leftover (eligible discharge capacity the rigid settlement
    ///         did not consume), which is what makes battery-to-battery transfer possible. Unmet soft
    ///         desire is silently clamped: never a deprioritization, never an overload, never a lockout, never a
    ///         dead-input cue.
    ///
    ///     Per tick:
    ///       1. Gather: per-network rigid demand + generator supply; the contributor / elastic / soft
    ///          rosters from SegmentingDeviceRegistry. Each bridge device's PHYSICAL description
    ///          (flow kind, terminal networks, capacities, distance multiplier, quiescent draw) comes
    ///          from its ISegAdapter (SegAdapters.cs): the Routed adapters (Transformer / linked
    ///          wireless pair / APC) each yield one Seg, and the Buffered adapter (rocket umbilical)
    ///          yields its cell's soft charge request + elastic discharge capacity instead of a Seg.
    ///          GATHER attaches allocator POLICY on top of the description: priority, lockout state,
    ///          and the deprioritization/overload bookkeeping.
    ///       2. Order: topological (Kahn) order over conducting contributor edges, so every network is
    ///          processed after all the networks that feed it. Cycle members are already CYCLE_FAULTed by
    ///          PROTECT (cycle detection) and conduct 0, so the live graph is a DAG.
    ///       3. Fixed-point loop (max 2N+4 rounds, §8.0). OVERLOAD is grow-only / sticky (cleared once at
    ///          loop entry, then committed for the tick); only DEPRIORITIZED is re-decided each round. Each round:
    ///          a. backward desire (leaf -> source): each network's residual rigid need (rigid + deeper
    ///             pulls - generators - elastic availability) splits greedily over its suppliers in
    ///             (priority DESC, ReferenceId ASC) order, capped per-device at effective cap; then the
    ///             network's soft desire (local charge requests + deeper soft pulls) splits over the same
    ///             suppliers with the same priority-tier-first proportional splitter, capped per-device
    ///             at (EffCap - rigid desired throughput);
    ///          b. structural overload, evaluated BEFORE deprioritization (§8.4 hit-max: a contributor at its
    ///             Setting-like cap with unmet downstream rigid demand -- RIGID ONLY), so a structurally-
    ///             overloaded device surfaces as OVERLOAD, not deprioritized (CYCLE > CURRENT-MISMATCH > CABLE-OVERLOADED >
    ///             DEVICE-OVERLOADED > DEPRIORITIZED);
    ///          c. forward supply + re-decided deprioritization per input network: when consumer RIGID claims exceed
    ///             the input budget, victims are deprioritized WHOLE by tier-major best-fit-decreasing selection
    ///             (SelectDeprioritizationVictims: lowest priority tier first; within a tier the smallest single
    ///             claim that covers the remaining deficit, else largest-first; ties by ReferenceId);
    ///             step-up transformers are never deprioritized (§5.2); a dead input (no supply at all) defers
    ///             instead of cycling 60-second lockouts. After the rigid grants, soft is granted from the
    ///             net's firm residual (plus soft inflow arriving through its suppliers), proportionally
    ///             over local charge requests and consumer soft pulls, capped by the weakest cable's
    ///             remaining headroom; a deprioritized / locked / overloaded contributor gets zero soft, and a
    ///             store owned by an unbillable contributor raises no charge desire and gets no grant
    ///             (SoftOwnerBillable: the lockout enforcement zeroes the owner's vanilla bill, so a
    ///             grant could never be billed or delivered; the 464386 finding);
    ///          d. supply overload after the forward pass (RIGID ONLY): the elastic analog (a battery /
    ///             APC cell / umbilical delivering its full effective discharge with rigid demand still
    ///             unmet) and the §5.7 cable-overflow rule (rigid flow above the weakest cable's cap with
    ///             generators alone under it trips every supplier of that network instead of burning the
    ///             cable).
    ///          After the loop, the lockout commits, the dead-input rebuild, and the shortfall
    ///          census (all of which read the deciding state), a STRANDED-INFLOW CLAWBACK runs on
    ///          ticks that deprioritized anything. The deciding rounds keep deprioritized contributors'
    ///          desires visible so deprioritization stays re-decidable, which can leave inflow committed
    ///          for a consumer the same forward pass deprioritized (billed upstream, consumed by nobody). The
    ///          clawback walks leaf to source and, on every network whose total committed inflow
    ///          (rigid plus soft) exceeds its total consumption (served demand, granted pulls,
    ///          charge, soft pulls; soft counts because the soft stage funds charge from the
    ///          rigid firm residual), takes exactly that surplus back from the network's active
    ///          suppliers' rigid throughput in reverse grant order, propagating the pull
    ///          reduction upstream in the same walk. No re-split and no re-grant: every seg off the stranded
    ///          chains keeps its deciding-pass numbers to the bit, so the elastic shares, the
    ///          publish tail, and the conservation check see balanced flow while real-world
    ///          allocation changes by the stranded component only. No decision is re-opened.
    ///       4. Elastic shares (§7.3 + §9.2): per output network, each elastic's share is its RIGID
    ///          component (the rigid shortfall left after generators + transformer inflow,
    ///          full-or-proportional against effective caps, min(rate cap, stored)) plus its SOFT
    ///          TOP-UP (its slice of the net's elastic-funded soft quantum, full-or-proportional to
    ///          leftover); written to SoftSupplyShareCache for the GetGeneratedPower postfixes.
    ///          The elastic CAPACITY stays net-local (it never propagates across contributors);
    ///          the soft flow it funds rides the normal soft class and can cross contributors.
    ///       5. Publish: per-device charge shares to SoftDemandShareCache; per-contributor presentation
    ///          totals (TotalThrough = rigid + soft throughput, TotalPull = TotalThrough * max(m,1) +
    ///          quiescent) to TransformerSupplyCache for EVERY routed seg kind, plus the APC bundle
    ///          caches. The vanilla-facing advertise/bill patches serve these totals verbatim, so a
    ///          granted soft flow always has a carrier on both terminals of its segment. Also swaps
    ///          in the Stage 3 presentation snapshots (PoweredPresentation): the healthy-segmenter
    ///          set the AllowSetPower postfixes read, and the enrolled-seg roster the ENFORCE tail
    ///          uses for the Powered reconcile and the _powerProvided ledger settle. Also publishes
    ///          the per-net shortfall classification snapshot (ShortfallDiagnostics: Served / Dry /
    ///          Throttled / Deadlock) the regression census joins against; diagnostics only, read
    ///          from the converged fields, never fed back into any decision.
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
            public bool QuiescentAlwaysOn; // APC: vanilla bills the quiescent whenever ON, so an idle seg still presents (and is granted) a quiescent-only pull
            public float InputDrawFactor;  // input-side draw per unit Throughput (PT-pair distance overhead m, §8.4.2); 0 treated as 1
            public int Priority;
            public int Depth;
            public bool StepUp;            // never deprioritized (§5.2)
            public bool Locked;            // cycle-faulted or in a prior-tick deprioritization/overload window: conducts 0, not re-decided
            // Presentation identity (Stage 3): the enumerated bridge device, its pair partner
            // (linked receiver, 0/null when none), consumed by the Powered-presentation and
            // ledger-settle publish tail. References live for this tick's publish only.
            public long PartnerRefId;
            public ElectricalInputOutput AnchorDevice;
            public ElectricalInputOutput PartnerDevice;
            // The Net model this seg delivers onto, resolved when rosters are registered. Consumed
            // by the leaf-first victim protection (a seg whose output net still feeds ACTIVE child
            // segs is a hop and never a deprioritization victim; POWER.md §0 decision 24 stage 4).
            public Net OutNetModel;
            // per-round:
            public bool Deprioritized;
            // Deprioritization hover payload (the block FaultHover renders, POWER.md §11.1),
            // captured at the victim-mark site inside the deciding forward sweep:
            // the victim's own rigid pull, the input net's total rigid want that round, and the
            // supply that net could raise. Not cleared with the flag: a 2-cycle union re-mark
            // reuses the values from the round that last marked the seg.
            public float DeprioritizedNeedsW;
            public float DeprioritizedUpstreamDemandW;
            public float DeprioritizedUpstreamSupplyW;
            // Decision fields captured at the same victim-mark site: the remaining upstream deficit
            // at the instant this seg was shed (ShortfallW), which of the three DeprioritizeReason
            // cases applied, and the seg's own priority value. Sticky with the triple above (not
            // cleared with the flag), so a 2-cycle union re-mark reuses the last-marked values.
            public float DeprioritizedShortfallW;
            public DeprioritizeReason DeprioritizedReason;
            public int DeprioritizedVictimPriority;
            public bool Overloaded;
            // Overload KIND bit + hover payload. Overloaded keeps meaning "offline this tick" for
            // BOTH overload kinds (the solve reads only Overloaded); CableOverloaded routes the
            // publish to CableOverloadRegistry (5.7 cable overflow) instead of OverloadRegistry
            // (8.4 capacity hit-max). The payload pair is captured at the detection site that first
            // trips the seg (overload is grow-only, so the first detector owns the entry): rule 1
            // writes (net rigid desire, combined deliverable cap); rule 3 writes (flow, weakest
            // cable cap).
            public bool CableOverloaded;
            public float OverloadValueW;
            public float OverloadCapW;
            // The internal-storage (AvailableElastic) component of OverloadCapW for the rule-1
            // capacity fault; a consumer derives the upstream part as OverloadCapW - OverloadStorageW.
            // Written at the DetectStructuralOverload site alongside the pair, reset with them.
            public float OverloadStorageW;
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

        // An elastic supplier: discharges a store onto OutNet to fill rigid shortfall first (§7.3),
        // then donates its per-net leftover to the net's soft pool (§9.2, the lowest funding rung).
        private sealed class Elastic
        {
            public long RefId;
            public CableNetwork OutNet;
            public float EffDischarge;     // min(rate cap, cable cap, stored)
            public bool Locked;
            public bool Overloaded;        // per-round (elastic hit-max analog); covers both overload kinds for the solve
            // Overload KIND bit + hover payload, same contract as Seg: CableOverloaded routes the
            // commit to CableOverloadRegistry with the (flow, weakest cable cap) pair captured at
            // the rule 3 site; a capacity (rule 2) elastic gets its shared payload at the cohort
            // commit instead, so these fields stay zero for it.
            public bool CableOverloaded;
            public float OverloadValueW;
            public float OverloadCapW;
            public float Share;            // final delivered share (rigid share + soft top-up)
            public byte Kind;              // store kind (ChargeDeliveryAudit.Kind*) for the discharge-delivery audit
            public ElectricalInputOutput Owner;   // the store device, consumed by the write-back plan
        }

        // A soft demander: charges a store from InNet out of the firm residual only (§7.4).
        private sealed class Soft
        {
            public long RefId;
            public CableNetwork InNet;
            public float Request;          // min(charge rate cap, cable cap, headroom)
            public float Share;
            public byte Kind;              // ChargeDeliveryAudit store kind (battery / APC cell / umbilical)
            // Billability links (the 464386 finding): CycleFaultEnforcementPatches zeroes a
            // locked / deprioritized / overloaded owner's vanilla bill at Priority.Last, so a share
            // granted to a store whose owner cannot bill can never be billed or delivered and
            // strands as granted-but-uncredited. GATHER never enrolls a store whose owner is
            // registry-locked; these references let the per-round SoftOwnerBillable gate cover
            // the owner that is deprioritized / overloads INSIDE the deciding loop.
            public Seg OwnerSeg;           // the APC's routed seg (null for battery / umbilical stores)
            public Elastic OwnerElastic;   // the store's own discharge half this tick (null when absent)
            public ElectricalInputOutput Owner;   // the store device, consumed by the write-back plan
        }

        private sealed class Net
        {
            public CableNetwork Network;
            public long Id;
            public float RigidDemand;
            public float GenSupply;
            // GATHER saw at least one non-segmenter power device on this net. Combined with the
            // per-tick Softs roster it decides the ratio-contract scope (published to
            // ShortfallDiagnostics alongside the classification snapshot).
            public bool HasNonSegmenterDevice;
            public float WeakestCap = float.MaxValue;   // snapshot-time weakest-cable cap (tier rating)
            public int Depth = UnreachableDepth;
            public readonly List<Seg> Suppliers = new List<Seg>();
            public readonly List<Seg> Consumers = new List<Seg>();
            public readonly List<Elastic> Elastics = new List<Elastic>();
            public readonly List<Soft> Softs = new List<Soft>();
            // The local charge desire is recomputed per round from billable owners only
            // (BillableSoftRequestLocal); no static per-tick sum is kept, so a store whose owner
            // is deprioritized / overloads inside the deciding loop stops sizing upstream soft inflow.
            // per-round:
            public float Unmet;
            public float PullsGranted;
            public float InflowCommitted;
            public float RigidServed;
            // Backward desire pass scratch: total power demanded on this network assuming nothing is
            // deprioritized (rigid + every non-locked, non-overloaded consumer's desired pull), and the soft
            // analog (local charge requests + every active consumer's soft desired pull).
            public float DesiredDemand;
            public float SoftDesire;
            // Forward soft grants (for the conservation check): local charge granted + soft pulls granted.
            public float SoftGrantedLocal;
            public float SoftPullsGranted;
            // The elastic-funded soft quantum (§9.2): the part of this net's granted soft flow the
            // firm pool (local firm residual + soft inflow) could not cover, funded from the net's
            // elastic leftover. Written by the forward pass each round; consumed by the elastic
            // share pass (soft top-up sizing) after the loop converges.
            public float ElasticFundedSoft;
        }

        internal static void RunAtomic(int currentTick)
        {
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

            // GATHER-time control-state snapshot (SegControlSnapshot): the FIRST touch of a
            // segmenter this tick pins its (OnOff, Error) for the whole tick. The umbilical
            // quiescent bill in the gather below, the enrollment gate in the segmenter
            // enumeration, and every ENFORCE-phase gate in the segmenter patch files read this
            // one coherent value, so a player toggle landing mid-tick cannot tear the solve; it
            // lands next tick.
            var controlSnapshot = new Dictionary<long, SegControlSnapshot.Entry>();

            // Per-network rigid demand + generator supply, consumed from the tick's GridSnapshot
            // (the single boundary read; POWER.md §0 decision 24). The umbilical quiescent bill and
            // the demand-model reconstruction already happened in the snapshot builder; CURRENT-MISMATCH-locked
            // producers were zeroed in place after PROTECT (ZeroFaultedProducers), so faulted solar
            // contributes no supply here. This method performs no vanilla topology or demand reads.
            var gridSnap = Core.GridSnapshot.Current;
            if (gridSnap == null) return;
            for (int ni = 0; ni < gridSnap.Nets.Count; ni++)
            {
                var nr = gridSnap.Nets[ni];
                var n = GetNet(nr.Network);
                if (n == null) continue;
                n.RigidDemand = nr.RigidDemand;
                n.GenSupply = nr.GenSupply;
                n.HasNonSegmenterDevice = nr.HasNonSegmenterDevice;
                n.WeakestCap = nr.WeakestCap;
                for (int ri = 0; ri < nr.Rows.Count; ri++)
                {
                    var row = nr.Rows[ri];
                    if (!row.IsSegmenter) continue;
                    // First-read-wins control snapshot: the boundary read is the first (and only)
                    // touch, so enrollment, the quiescent bill, and every published gate agree.
                    if (!controlSnapshot.ContainsKey(row.RefId))
                    {
                        controlSnapshot[row.RefId] = new SegControlSnapshot.Entry
                        {
                            OnOff = row.OnOff,
                            Error = row.Error,
                        };
                    }
                }
            }

            // Contributor / elastic / soft rosters from the deterministic segmenter enumeration.
            var segs = new List<Seg>();
            var elastics = new List<Elastic>();
            var softs = new List<Soft>();

            bool IsPowerLocked(long refId)
            {
                return CycleFaultRegistry.IsCycleFaulted(refId, currentTick)
                    || DeprioritizedRegistry.IsLockedOut(refId, currentTick)
                    || OverloadRegistry.IsLockedOut(refId, currentTick)
                    || CableOverloadRegistry.IsLockedOut(refId, currentTick);
            }

            // Attach allocator POLICY to an adapter's physical description (SegAdapters.cs):
            // priority, and the lockout gate over the device's own registries plus -- for a paired
            // bridge -- the partner half's cycle-fault state (either dish faulted locks the pair).
            // The anchor / partner device references ride along for the Stage 3 publish tail
            // (Powered presentation + ledger settle); they are consumed within this tick.
            Seg MakeSeg(SegKind kind, long refId, in SegSpec spec,
                ElectricalInputOutput anchor, ElectricalInputOutput partner = null)
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
                    QuiescentAlwaysOn = spec.QuiescentAlwaysOn,
                    InputDrawFactor = spec.Multiplier,
                    Priority = PriorityStore.GetPriority(refId),
                    StepUp = spec.StepUp,
                    Locked = IsPowerLocked(refId)
                        || (spec.PartnerRefId != 0L && CycleFaultRegistry.IsCycleFaulted(spec.PartnerRefId, currentTick)),
                    PartnerRefId = spec.PartnerRefId,
                    AnchorDevice = anchor,
                    PartnerDevice = partner,
                };
            }

            var segmenters = gridSnap.SegmentersSorted;
            for (int i = 0; i < segmenters.Count; i++)
            {
                var eio = segmenters[i];
                if (eio == null) continue;
                // Enrollment gates on the tick's control snapshot (first-read-wins; recorded in
                // the rigid gather above, backfilled here for a segmenter the gather never saw),
                // so enrollment, the quiescent bill, and the ENFORCE gates all agree.
                if (!controlSnapshot.TryGetValue(eio.ReferenceId, out var snap))
                {
                    snap = new SegControlSnapshot.Entry { OnOff = eio.OnOff, Error = eio.Error };
                    controlSnapshot[eio.ReferenceId] = snap;
                }
                // The rocket umbilical Female is OnOff-BLIND in vanilla (her GetUsedPower has no
                // OnOff gate, the pair's switch is the Male; the QuiescentBill Female branch encodes
                // the same), so she enrolls regardless of the flag. Dropping her here left a docked
                // rocket's internal net with zero modeled supply: the net judged DEAD_NOSUPPLY and
                // the rocket batteries' charge shares were zeroed at publish (found live 2026-07-14,
                // StructureBatteryMedium 703648 never charging off Female 614953 with a full cell).
                if (!snap.OnOff && !(eio is RocketPowerUmbilicalFemale)) continue;

                switch (eio)
                {
                    case Transformer t:
                    {
                        if (!SegAdapters.Transformer.TryDescribe(t, out var spec)) break;
                        segs.Add(MakeSeg(SegKind.Transformer, t.ReferenceId, spec, t));
                        break;
                    }
                    case PowerTransmitter pt:
                    {
                        // One seg per LINKED pair, anchored on the transmitter. The link-rating and
                        // source-draw-multiplier physics (PowerTransmitterPlus ModApi tier, legacy
                        // reflection tier, or the vanilla curve) live in WirelessPairAdapter; the
                        // partner receiver's cycle-fault state locks the pair via MakeSeg.
                        if (!SegAdapters.WirelessPair.TryDescribe(pt, out var spec)) break;
                        segs.Add(MakeSeg(SegKind.PtPair, pt.ReferenceId, spec, pt, pt.LinkedReceiver));
                        break;
                    }
                    case PowerReceiver _:
                        break;   // handled via its linked PT (the pair is anchored on the transmitter)
                    case AreaPowerControl apc:
                    {
                        if (!SegAdapters.Apc.TryDescribe(apc, out var spec)) break;
                        var seg = MakeSeg(SegKind.Apc, apc.ReferenceId, spec, apc);
                        segs.Add(seg);
                        // The APC's internal cell is a storage half alongside the routed seg (not
                        // part of the adapter's description): discharge is elastic onto the output
                        // network, charge is a soft request on the input network. Same lock state
                        // as the seg (the APC's own registries; no partner).
                        var cell = apc.Battery;
                        if (cell != null)
                        {
                            Elastic cellElastic = null;
                            if (cell.PowerStored > 0f)
                            {
                                elastics.Add(cellElastic = new Elastic
                                {
                                    RefId = apc.ReferenceId,
                                    OutNet = apc.OutputNetwork,
                                    EffDischarge = Mathf.Min(
                                        Mathf.Min(Settings.ApcBatteryDischargeRate.Value,
                                            CableMax.For(apc.OutputConnection?.GetCable())),
                                        cell.PowerStored),
                                    Locked = seg.Locked,
                                    Kind = ChargeDeliveryAudit.KindApcCell,
                                    Owner = apc,
                                });
                            }
                            // A registry-locked APC's vanilla bill is zeroed at ENFORCE
                            // (CycleFaultEnforcementPatches, Priority.Last), so its cell must not
                            // raise a charge request: a granted share could never be billed or
                            // delivered and would strand as granted-but-uncredited for the whole
                            // lockout window (the 464386 finding: exactly 120 zero-credit ticks
                            // under a dawn lockout). Same-tick deprioritization / overload decided inside the
                            // loop is covered by the SoftOwnerBillable gate via the owner links.
                            if (!cell.IsCharged && !seg.Locked)
                            {
                                float req = Mathf.Min(Patches.AreaPowerControlPatches.ComputeChargeCap(apc), cell.PowerDelta);
                                if (req > 0f)
                                    softs.Add(new Soft
                                    {
                                        RefId = apc.ReferenceId,
                                        InNet = apc.InputNetwork,
                                        Request = req,
                                        Kind = ChargeDeliveryAudit.KindApcCell,
                                        OwnerSeg = seg,
                                        OwnerElastic = cellElastic,
                                        Owner = apc,
                                    });
                            }
                        }
                        break;
                    }
                    case Battery battery:
                    {
                        if (battery.Error == 1) break;
                        // Vanilla gates every battery power surface on IsOperable (an incomplete
                        // or broken battery bills, advertises, and credits nothing), so an
                        // inoperable battery is not enrolled either: a charge share granted to a
                        // store vanilla will never bill for would strand as granted-but-
                        // undeliverable and the charge-delivery audit would rightly flag it.
                        if (!Patches.StationaryBatteryPatches.GetIsOperable(battery)) break;
                        if (battery.InputNetwork != null && battery.OutputNetwork != null
                            && battery.InputNetwork.ReferenceId == battery.OutputNetwork.ReferenceId) break;   // short-circuit gate
                        bool locked = IsPowerLocked(battery.ReferenceId);
                        Elastic ownElastic = null;
                        if (battery.OutputNetwork != null && battery.PowerStored > 0f)
                        {
                            float rateCap = Patches.StationaryBatteryPatches.EffectiveDischargeCap(battery);
                            elastics.Add(ownElastic = new Elastic
                            {
                                RefId = battery.ReferenceId,
                                OutNet = battery.OutputNetwork,
                                EffDischarge = Mathf.Min(rateCap, battery.PowerStored),
                                Locked = locked,
                                Kind = ChargeDeliveryAudit.KindBattery,
                                Owner = battery,
                            });
                        }
                        // A registry-locked battery's vanilla bill is zeroed at ENFORCE
                        // (CycleFaultEnforcementPatches), so it raises no charge request while
                        // locked: a granted share would strand as granted-but-uncredited (the
                        // 464386 shape on a battery). The same-tick elastic-overload trip is
                        // covered by the OwnerElastic link and the SoftOwnerBillable gate.
                        if (battery.InputNetwork != null && !locked)
                        {
                            float headroom = battery.PowerMaximum - battery.PowerStored;
                            float rateCap = Patches.StationaryBatteryPatches.EffectiveChargeCap(battery);
                            float req = Mathf.Min(rateCap, headroom);
                            if (req > 0f)
                                softs.Add(new Soft
                                {
                                    RefId = battery.ReferenceId,
                                    InNet = battery.InputNetwork,
                                    Request = req,
                                    Kind = ChargeDeliveryAudit.KindBattery,
                                    OwnerElastic = ownElastic,
                                    Owner = battery,
                                });
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
                        Elastic umbElastic = null;
                        if (spec.HasElastic)
                        {
                            elastics.Add(umbElastic = new Elastic
                            {
                                RefId = umbilical.ReferenceId,
                                OutNet = spec.OutNet,
                                EffDischarge = spec.ElasticCapacity,
                                Locked = locked,
                                Kind = ChargeDeliveryAudit.KindUmbilical,
                                Owner = umbilical,
                            });
                        }
                        // Same billability rule as the APC cell and the battery: a registry-
                        // locked half's bill is zeroed at ENFORCE, so it raises no charge
                        // request; the same-tick elastic trip rides the OwnerElastic link.
                        if (spec.HasSoft && !locked)
                            softs.Add(new Soft
                            {
                                RefId = umbilical.ReferenceId,
                                InNet = spec.InNet,
                                Request = spec.SoftRequest,
                                Kind = ChargeDeliveryAudit.KindUmbilical,
                                OwnerElastic = umbElastic,
                                Owner = umbilical,
                            });
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
                seg.OutNetModel = outNet;
            }
            foreach (var e in elastics) GetNet(e.OutNet)?.Elastics.Add(e);
            foreach (var s in softs)
                GetNet(s.InNet)?.Softs.Add(s);

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

            foreach (var n in netList)
            {
                n.Suppliers.Sort(SupplierOrder);
                n.Consumers.Sort(SupplierOrder);
            }

            // ----------------------------------------------------------------
            // 3. DECIDE: deprioritization / overload fixed point. Backward/forward sweep iterated to a fixed point.
            //    Overload is evaluated BEFORE deprioritization each round and is GROW-ONLY (sticky: cleared only at
            //    loop entry, then reset only by the 60 s timeout or a player turn-off); ONLY DEPRIORITIZED is
            //    re-decided each round against the settled state. So an unnecessary deprioritization cannot freeze for
            //    60 s once another device's overload frees the budget (the 2c case), and a transformer that
            //    structurally cannot serve its downstream surfaces as OVERLOAD, not deprioritized. Throughputs are
            //    exact (no headroom). Convergence is bounded (2N+4 rounds);
            //    the per-net field values it leaves (Unmet / InflowCommitted / PullsGranted / RigidServed /
            //    SoftGrantedLocal / SoftPullsGranted / Throughput / SoftThrough) feed the dead-input
            //    cue, the lockout commits, and the shortfall census below; the stranded-inflow
            //    clawback then removes deprioritization-orphaned surplus before the elastic-share / publish /
            //    conservation tail reads them.
            // ----------------------------------------------------------------
            RunAllocationLoop(topo, netsDeepFirst, segs, elastics, netList);

            // Dead-input cue (POWER.md §8.3): a contributor whose input network has NO effective supply
            // (no generators, no upstream inflow, no live battery -- the same totalAvail the deprioritization
            // pass uses) idles instead of being deprioritized. Flag those that are actively trying to pass power
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
            // the lockout would apply to. Defer committing NEW deprioritization / overload lockouts for such a
            // network until the split lands (SplitPendingRegistry clears it). Existing lockouts
            // (seg.Locked) still enforce; the network still distributes power via vanilla ENFORCE.
            // ----------------------------------------------------------------
            foreach (var seg in segs)
            {
                if (seg.Locked) continue;   // prior-tick lockout carries; timer untouched
                if (SegNetPending(seg)) continue;
                if (seg.Deprioritized) DeprioritizedRegistry.NoteDeprioritized(seg.RefId, currentTick,
                    seg.DeprioritizedNeedsW, seg.DeprioritizedUpstreamDemandW, seg.DeprioritizedUpstreamSupplyW,
                    seg.DeprioritizedShortfallW, seg.DeprioritizedReason, seg.DeprioritizedVictimPriority);
                if (seg.Overloaded)
                {
                    // Kind routing: the cable-overflow trip (rule 3) and the capacity trip (rule 1)
                    // publish into separate registries so the hover, the flash resolution, and the
                    // IC10 slots can name the specific cause. The payload pair captured at the
                    // detection site rides into the entry.
                    if (seg.CableOverloaded)
                        CableOverloadRegistry.NoteCableOverload(seg.RefId, currentTick,
                            seg.OverloadValueW, seg.OverloadCapW);
                    else
                        OverloadRegistry.NoteOverload(seg.RefId, currentTick,
                            seg.OverloadValueW, seg.OverloadCapW, seg.OverloadStorageW);
                }
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
            //
            // KIND SPLIT: only the capacity family (rule 2, elastic hit-max) forms cohorts here. A
            // cable-overflow (rule 3) elastic commits per device into CableOverloadRegistry with the
            // (flow, weakest cable cap) payload from its detection site; the network retry is
            // meaningless for it because re-engaging the store cannot raise the cable's rating.
            var elasticOverloadNets = new HashSet<long>();
            foreach (var e in elastics)
            {
                if (!e.Overloaded || e.OutNet == null) continue;
                if (SplitPendingRegistry.IsPending(e.OutNet.ReferenceId)) continue;
                if (e.CableOverloaded)
                {
                    CableOverloadRegistry.NoteCableOverload(e.RefId, currentTick,
                        e.OverloadValueW, e.OverloadCapW);
                    continue;
                }
                elasticOverloadNets.Add(e.OutNet.ReferenceId);
            }
            bool InCapacityCohort(Elastic e, long netRef)
                => e.OutNet != null && e.OutNet.ReferenceId == netRef
                   && ((e.Overloaded && !e.CableOverloaded)
                       || OverloadRegistry.IsOverloaded(e.RefId, currentTick));
            foreach (long netRef in elasticOverloadNets)
            {
                if (!nets.TryGetValue(netRef, out var n)) continue;
                float cohortDischarge = 0f;
                foreach (var e in elastics)
                    if (InCapacityCohort(e, netRef))
                        cohortDischarge += e.EffDischarge;
                bool recover = cohortDischarge >= n.Unmet - Eps;   // cohort can jointly cover the load -> retry succeeds
                // Shared hover payload for a still-short cohort: the net's total rigid want against
                // the pool max with the cohort re-engaged. Computed at this commit (the deciding
                // site for the elastic capacity fault) so every cohort member shows one consistent
                // pair; AvailableElastic excludes the cohort itself (locked or flagged), so adding
                // cohortDischarge does not double-count.
                float availElastic = AvailableElastic(n);
                float liveSupply = n.GenSupply + n.InflowCommitted + availElastic;
                float cohortValueW = liveSupply + n.Unmet;
                float cohortCapW = liveSupply + cohortDischarge;
                // The internal-storage slice of the cohort cap: the non-cohort elastic already in
                // liveSupply plus the cohort's own discharge re-engaged by the retry. A consumer
                // reads upstream as cohortCapW - cohortStorageW.
                float cohortStorageW = availElastic + cohortDischarge;
                foreach (var e in elastics)
                {
                    if (!InCapacityCohort(e, netRef)) continue;
                    if (recover) OverloadRegistry.ClearLockout(e.RefId);          // recovered: no reset, rejoin next tick
                    else OverloadRegistry.NoteOverload(e.RefId, currentTick,      // still short: arm + phase-sync
                        cohortValueW, cohortCapW, cohortStorageW);
                }
            }

            // Shortfall classification snapshot (diagnostics): label every allocator net's
            // end-of-tick RIGID state for the regression census (ShortfallDiagnostics: Served /
            // Dry / Throttled / Deadlock, ClassifyNetShortfall below). Pure read-over of the
            // DECIDING per-net / per-seg fields, deliberately taken BEFORE the stranded-inflow
            // clawback below, so the census describes exactly the state the deprioritization / overload
            // decisions were taken against (and stays tick-for-tick comparable across builds).
            // Swapped by volatile reference exactly like the Powered-presentation snapshots; a
            // net absent from the map was outside allocator scope this tick (the census reads
            // absence as off-scope).
            var shortfallClasses = new Dictionary<long, byte>(netList.Count);
            // Ratio-contract scope (POWER.md §8.8), published for the ScenarioRunner census: a
            // net is ratio-deprivable iff a delivery shortfall could shrink something the
            // allocator granted: a plain (non-segmenter) power device's delivery and Powered
            // state, or a charging store's delivered energy. A net whose power members are all
            // routed segmenters is inert (bills and advertises cache-governed; Powered
            // reconciled at the ENFORCE tail); notably every wireless carrier net, whose
            // vanilla mirrors are structurally asymmetric under the billing handshake
            // (unclamped receiver drain vs delivery-gated advertise), so ratio < 1 is its
            // normal conducting state, not a contract breach.
            var ratioScope = new HashSet<long>();
            foreach (var n in netList)
            {
                shortfallClasses[n.Id] = ClassifyNetShortfall(n, nets);
                if (n.HasNonSegmenterDevice || n.Softs.Count > 0)
                    ratioScope.Add(n.Id);
            }
            ShortfallDiagnostics.Publish(shortfallClasses);
            ShortfallDiagnostics.PublishRatioScope(ratioScope);

            // ----------------------------------------------------------------
            // STRANDED-INFLOW CLAWBACK. The deciding rounds keep deprioritized contributors' desires
            // visible (DesireActive ignores Deprioritized) so deprioritization stays re-decidable. The converged
            // state can therefore carry inflow committed for a consumer the same forward pass
            // deprioritized: billed upstream, consumed by nobody (the net-487688 conservation bug). Undo
            // exactly that surplus and nothing else: walk leaf -> source; on every network whose
            // total committed inflow exceeds its total consumption, take the surplus back from
            // its active suppliers in reverse grant order (the tail of the sequential grant loop
            // is what funded the deprioritized claims), shrinking each seg's published throughput and
            // pull consistently; the pull reduction lands on the supplier's input network before
            // that network is visited, so the surplus propagates upstream in one pass. No
            // re-split and no re-grant (a full settle re-pass was tried and re-granted the freed
            // budget to other branches, changing real allocation on trip ticks): every seg off
            // the stranded chains keeps its deciding-pass numbers to the bit. Decisions are
            // untouched: flags, registries, the dead-input cue, and the shortfall census above
            // all read the deciding state; the elastic shares, publish tail, and conservation
            // check below read the clawed state.
            //
            // The strand is measured over the FULL pool the conservation checker audits, never
            // the rigid slice alone: the deciding soft stage funds charge grants out of the
            // rigid firm residual (softLocal = firmIn - RigidDemand - survivorPull), so rigid
            // inflow left over after rigid consumption is NOT stranded when soft grants consumed
            // it (a rigid-only strand formula clawed exactly that funded residual on a chargers'
            // input net and pushed the soft ledger negative by the clawed amount, the net-625036
            // finding). Soft fields are still never clawed: at any exit this gate sees, soft
            // outflow covers soft inflow (the grant loop distributes min(softDemand, softAvail)
            // and the inflow is desire-sized), so the surplus is always coverable by rigid
            // throughput alone. Generator power is drawn on demand (never strands) and elastic
            // shares are sized later from the final shortfall (zero on any surplus net), so
            // neither enters the strand.
            // ----------------------------------------------------------------
            bool anyDeprioritized = false;
            foreach (var seg in segs)
                if (seg.Deprioritized) { anyDeprioritized = true; break; }
            if (anyDeprioritized)
            {
                foreach (var n in netsDeepFirst)
                {
                    float softIn = 0f;
                    foreach (var s in n.Suppliers)
                        if (IsActive(s)) softIn += s.SoftThrough;
                    float strand = n.InflowCommitted + softIn
                                   - n.RigidServed - n.PullsGranted
                                   - n.SoftGrantedLocal - n.SoftPullsGranted;
                    if (strand <= Eps) continue;
                    for (int i = n.Suppliers.Count - 1; i >= 0 && strand > Eps; i--)
                    {
                        var s = n.Suppliers[i];
                        if (!IsActive(s) || s.Throughput <= 0f) continue;
                        float take = s.Throughput < strand ? s.Throughput : strand;
                        float newThr = s.Throughput - take;
                        float oldPull = s.Pull;
                        float m = Mathf.Max(s.InputDrawFactor, 1f);
                        float newPull;
                        if (newThr > Eps)
                        {
                            newPull = newThr * m + s.UsedPower;
                        }
                        else
                        {
                            take = s.Throughput;   // absorb the sub-Eps remainder exactly
                            newThr = 0f;
                            // The quiescent stays on the rigid pull while the seg still carries
                            // soft flow (its soft pull deliberately carries no quiescent when the
                            // rigid side did) or when the seg is always-on-quiescent (APC: vanilla
                            // bills the idle draw whenever ON, so the clawback must not strip the
                            // funded quiescent); any other fully idle seg bills nothing, matching
                            // the idle desire model and the checker's one-sided not-conducting case.
                            newPull = s.SoftThrough > Eps || s.QuiescentAlwaysOn ? s.UsedPower : 0f;
                        }
                        s.Throughput = newThr;
                        s.Pull = newPull;
                        n.InflowCommitted -= take;
                        strand -= take;
                        if (s.InNet != null && nets.TryGetValue(s.InNet.ReferenceId, out var up))
                            up.PullsGranted -= oldPull - newPull;
                    }
                }
            }

            // ----------------------------------------------------------------
            // 4. ELASTIC SHARES (§7.3 + §9.2) -> SoftSupplyShareCache. Two components per
            //    elastic: the RIGID share (the residual rigid shortfall, full-or-proportional
            //    against effective discharge caps, exactly as before) plus the SOFT TOP-UP
            //    (the net's elastic-funded soft quantum from the converged forward pass,
            //    distributed full-or-proportional to each elastic's leftover). Total per
            //    elastic never exceeds EffDischarge by construction.
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

                // Soft top-up (§9.2): the forward pass funded ElasticFundedSoft watts of this
                // net's soft grants from the elastic leftover; hand each eligible elastic its
                // full-or-proportional slice of that quantum so the donated discharge is
                // advertised (and drained) on the donor. By construction topUpTotal <=
                // leftoverTotal: the forward pass capped the elastic-funded quantum at
                // (elasticCap - rigid draw) computed from the SAME converged per-net fields this
                // pass reads, the eligibility sets are identical (settled flags), and on any net
                // the clawback touched (a strand net) the elastic-funded quantum is provably 0
                // (a positive strand forces softFirm to cover every granted soft watt). The
                // clamp below is therefore defensive only; if it ever bound it would tighten
                // (donate less than granted), which the conservation check would surface as a
                // power-from-nothing residual rather than silently over-discharging a store.
                float topUpTotal = n.ElasticFundedSoft;
                if (topUpTotal > 0f && effTotal > 0f)
                {
                    float leftoverTotal = 0f;
                    foreach (var e in n.Elastics)
                        if (!e.Locked && !e.Overloaded) leftoverTotal += e.EffDischarge - e.Share;
                    if (leftoverTotal > 0f)
                    {
                        if (topUpTotal > leftoverTotal) topUpTotal = leftoverTotal;
                        foreach (var e in n.Elastics)
                        {
                            if (e.Locked || e.Overloaded) continue;
                            float leftover = e.EffDischarge - e.Share;
                            if (leftover <= 0f) continue;
                            e.Share += topUpTotal >= leftoverTotal
                                ? leftover
                                : leftover * (topUpTotal / leftoverTotal);
                        }
                    }
                }
            }

            // ----------------------------------------------------------------
            // 4.9 NET LIVENESS: the per-net LIVE / DEAD verdict for the consumer Powered-ownership
            //     layer (NetLiveness), computed from the converged post-clawback state BEFORE the
            //     caches publish so a dead net's shares and totals can be zeroed at the source.
            //     LIVE = rigid demand fully funded AND an energized feed exists (the same supply
            //     expression the dead-input cue and the healthy-set inNetMet gate use). DEAD_UNMET
            //     (demand the allocator could not fund: generation-short root nets, the step-up and
            //     cable-limited partial-delivery carve-outs) arms a 60 s hold against demand-
            //     collapse flapping; DEAD_NOSUPPLY re-arms the tick supply returns. Consumed the
            //     same tick by the SetPowerFromThread false-edge block, the producer dead-net
            //     advertise zeroing, and the PoweredOwnership sweep. Zeroing a dead net's caches
            //     below is what makes the verdict conservation-exact: no provider on the net means
            //     an EMPTY Providers array at ENFORCE, ConsumePower never calls ReceivePower,
            //     accumulator debts freeze, and the vanilla power-ON edge cannot fire (the
            //     zero-Potential corollary, Research/GameClasses/PowerTick.md). GATHER is
            //     untouched (next tick re-reads real supply), so recovery is never deadlocked.
            // ----------------------------------------------------------------
            var liveness = new Dictionary<long, byte>(netList.Count);
            foreach (var n in netList)
            {
                bool hasSupply = n.GenSupply + n.InflowCommitted + AvailableElastic(n) > Eps;
                byte formula = n.Unmet > Eps ? NetLiveness.DeadUnmet
                             : hasSupply ? NetLiveness.Live
                             : NetLiveness.DeadNoSupply;
                liveness[n.Id] = NetLiveness.ApplyHold(n.Id, formula, currentTick);
            }
            NetLiveness.Publish(liveness, currentTick);
            bool IsDeadNet(CableNetwork net)
            {
                return net != null
                       && liveness.TryGetValue(net.ReferenceId, out byte v)
                       && v != NetLiveness.Live;
            }

            // ----------------------------------------------------------------
            // 5. PUBLISH: write the share caches (ENFORCE's postfixes read these).
            // ----------------------------------------------------------------
            foreach (var e in elastics)
            {
                // For APCs the GetGeneratedPower surface bundles passthrough + cell (vanilla
                // AvailablePower), so the cap must include the total committed passthrough (rigid +
                // soft); the matching Seg is found below. A store discharging onto a DEAD net
                // publishes a zero share: all-or-nothing, nothing drains into a dark subnet.
                SoftSupplyShareCache.SetShare(e.RefId, IsDeadNet(e.OutNet) ? 0f : e.Share);
            }
            // Storage charge shares: the forward sweep's per-device soft grants. The soft-demand
            // GetUsedPower postfixes cap the reported charge demand to these, so each store charges
            // exactly what the allocator granted. A store charging on a DEAD net publishes zero:
            // its charge bill would be a phantom no dark supplier ever funds.
            foreach (var s in softs)
                SoftDemandShareCache.SetShare(s.RefId, IsDeadNet(s.InNet) ? 0f : s.Share);

            // Charge-grant snapshot for the charge-delivery audit (§8.8 fifth surface): every
            // store's granted charge this tick, with the charge-side net (the audit's Served gate)
            // and the store kind (efficiency-band handling). Published even when empty so a stale
            // tick's grants never linger into the comparison.
            var chargeGrants = new Dictionary<long, ChargeDeliveryAudit.Grant>(softs.Count);
            foreach (var s in softs)
            {
                if (s.InNet == null) continue;
                chargeGrants[s.RefId] = new ChargeDeliveryAudit.Grant
                {
                    // Dead-net zeroing, same as the caches and the write-back plan: a store charging
                    // on a DEAD net is credited nothing, so the audit must expect nothing (a store
                    // on a faulted solar bus was the live counterexample: raw grant vs zero credit).
                    Granted = IsDeadNet(s.InNet) ? 0f : s.Share,
                    NetId = s.InNet.ReferenceId,
                    Kind = s.Kind,
                };
            }
            ChargeDeliveryAudit.PublishGrants(chargeGrants);

            // Discharge-grant snapshot for the discharge-delivery audit (the charge audit's
            // second direction): every rostered battery / umbilical elastic's granted discharge
            // this tick (rigid share + soft top-up), with the discharge-side net (the audit's
            // Served gate) and store kind. The APC cell is deliberately NOT published: vanilla
            // drains it by deferred ledger settlement (UsePower drains min(stored, _powerProvided)
            // from the PREVIOUS tick's shortfall, a load-bearing mechanism this mod keeps), so a
            // same-tick granted-vs-drained comparison is structurally lag-prone there; see the
            // DischargeDeliveryAudit class doc. Published even when empty so a stale tick's
            // grants never linger into the comparison.
            var dischargeGrants = new Dictionary<long, DischargeDeliveryAudit.Grant>(elastics.Count);
            foreach (var e in elastics)
            {
                if (e.OutNet == null) continue;
                if (e.Kind == ChargeDeliveryAudit.KindApcCell) continue;
                dischargeGrants[e.RefId] = new DischargeDeliveryAudit.Grant
                {
                    // Dead-net zeroing, symmetric with the charge audit and the write-back plan.
                    Granted = IsDeadNet(e.OutNet) ? 0f : e.Share,
                    NetId = e.OutNet.ReferenceId,
                    Kind = e.Kind,
                };
            }
            DischargeDeliveryAudit.PublishGrants(dischargeGrants);

            // Publish each routed contributor's exact converged PRESENTATION TOTALS so its
            // GetGeneratedPower / GetUsedPower report the real in-tick flow (no headroom), soft
            // included. Output side = TotalThrough (rigid Throughput + granted SoftThrough); input
            // side = TotalPull (rigid Pull + granted SoftPull == TotalThrough * max(m,1) + the
            // contributor's own quiescent draw whenever it conducts). Publishing totals for EVERY seg
            // kind is what guarantees a granted soft flow has a carrier on both terminals of its
            // segment: a battery charging behind a transformer or wireless pair sees the charge
            // advertised downstream AND billed upstream in the same tick. Inactive contributors
            // (deprioritized / overloaded / cycle-faulted) carry all-zero totals, so they report 0 both ways.
            foreach (var seg in segs)
            {
                float totalThrough = seg.Throughput + seg.SoftThrough;
                float totalPull = seg.Pull + seg.SoftPull;
                // A seg whose OUTPUT net is verdict-DEAD delivers nothing and bills nothing: the
                // partial-delivery carve-outs (a step-up on a short input, a cable-limited seg)
                // would otherwise trickle ratio-scaled power into a dark subnet, evaporating
                // accumulator debts at partial price and billing upstream for undelivered flow.
                // Zeroing the published totals routes every cache-governed surface (advertise AND
                // input bill) through the same all-or-nothing verdict; the model values stay
                // untouched, so next tick's solve re-decides from real state.
                if (IsDeadNet(seg.OutNet))
                {
                    totalThrough = 0f;
                    totalPull = 0f;
                }
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
                    if (IsDeadNet(seg.OutNet)) cellShare = 0f;   // all-or-nothing on a dead subnet
                    ApcCellDischargeCache.SetShare(seg.RefId, cellShare);
                    SoftSupplyShareCache.SetShare(seg.RefId, totalThrough + cellShare);
                    // Presentation totals for the APC too, so every routed seg kind publishes the same
                    // (TotalThrough, TotalPull) surface (nothing reads the APC entry yet; the APC
                    // patches bill quiescent + cell charge themselves on top of the passthrough cache).
                    TransformerSupplyCache.Set(seg.RefId, totalThrough, totalPull);
                }
            }

            // Powered presentation + ledger-settle roster, swapped atomically like the share
            // caches. HEALTHY = enrolled this tick, carrying no fault (not locked / deprioritized /
            // overloaded; segmenters are never CURRENT-MISMATCH candidates), and either conducting flow or
            // sitting idle on an input network that has effective supply with its rigid demand
            // met. With vanilla ApplyState retired, PoweredPresentation.ReconcileEnforceTail is
            // the ONLY writer of a segmenter's Powered flag and asserts both edges from this
            // verdict: an idle healthy charger presents powered (the diagnostic trap this kills),
            // an idle seg on a DARK input (night-time solar feed) presents dark in line with the
            // dead-input hover cue. A pair publishes health under both halves' ReferenceIds so
            // transmitter and receiver present the same verdict.
            var presentationRoster = new List<PoweredPresentation.EnrolledSeg>(segs.Count);
            foreach (var seg in segs)
            {
                bool conducts = seg.Throughput + seg.SoftThrough > Eps;
                bool inNetMet = false;
                if (!conducts && seg.InNet != null && nets.TryGetValue(seg.InNet.ReferenceId, out var inRec))
                    inNetMet = inRec.GenSupply + inRec.InflowCommitted + AvailableElastic(inRec) > Eps
                               && inRec.Unmet <= Eps;
                bool healthy = IsActive(seg) && (conducts || inNetMet);
                presentationRoster.Add(new PoweredPresentation.EnrolledSeg
                {
                    RefId = seg.RefId,
                    Anchor = seg.AnchorDevice,
                    Partner = seg.PartnerDevice,
                    InNet = seg.InNet,
                    OutNet = seg.OutNet,
                    Healthy = healthy,
                    TotalThrough = seg.Throughput + seg.SoftThrough,
                    TotalPull = seg.Pull + seg.SoftPull,
                    // Ledger-settle eligibility (LedgerAdoption.SettleEnforceTail). The APC is
                    // excluded: its positive _powerProvided is how vanilla UsePower drains the
                    // internal cell one tick after the cell covers a shortfall, and its
                    // GetUsedPower uses Max(ledger, quiescent) so negatives are inert; settling
                    // would hand the cell free energy. The transformer is settled because the
                    // always-on fresh-pull billing replaces the vanilla ledger handshake.
                    SettleLedger = seg.Kind == SegKind.PtPair
                                   || seg.Kind == SegKind.Transformer,
                });
            }
            PoweredPresentation.Publish(presentationRoster);
            SegControlSnapshot.Publish(controlSnapshot);

            // ----------------------------------------------------------------
            // 5.5 WRITE-BACK PLAN (decision 24 stage 3): the converged results the write-back
            //     applies after this method returns. Net numbers feed the HUD/MP/logic fields;
            //     store credits and debits ARE the delivery (vanilla ApplyState no longer runs),
            //     zeroed on dead nets exactly like the published caches so all-or-nothing holds
            //     at the settlement layer too.
            // ----------------------------------------------------------------
            var writePlan = new Core.WriteBack.Plan();
            foreach (var n in netList)
            {
                float elasticGrantedTotal = 0f;
                foreach (var e in n.Elastics) elasticGrantedTotal += e.Share;
                writePlan.Nets.Add(new Core.WriteBack.NetResult
                {
                    Network = n.Network,
                    Id = n.Id,
                    Required = n.RigidDemand + n.PullsGranted + n.SoftPullsGranted + n.SoftGrantedLocal,
                    Current = n.RigidServed + n.PullsGranted + n.SoftPullsGranted + n.SoftGrantedLocal,
                    Potential = n.GenSupply + n.InflowCommitted + elasticGrantedTotal,
                });
            }
            foreach (var s in softs)
            {
                float amount = IsDeadNet(s.InNet) ? 0f : s.Share;
                if (amount <= 0f || s.Owner == null) continue;
                writePlan.Credits.Add(new Core.WriteBack.StoreCredit
                {
                    RefId = s.RefId,
                    Kind = s.Kind,
                    Amount = amount,
                    Owner = s.Owner,
                });
            }
            foreach (var e in elastics)
            {
                float amount = IsDeadNet(e.OutNet) ? 0f : e.Share;
                if (amount <= 0f || e.Owner == null) continue;
                writePlan.Debits.Add(new Core.WriteBack.StoreDebit
                {
                    RefId = e.RefId,
                    Kind = e.Kind,
                    Amount = amount,
                    Owner = e.Owner,
                });
            }
            Core.WriteBack.Current = writePlan;

            // ----------------------------------------------------------------
            // 6. CONSERVATION CHECK (always on): audit the converged grants. Per net, granted
            //    inflow == granted outflow within tolerance; per seg, TotalPull == TotalThrough *
            //    max(m,1) + quiescent. A violation is a code bug in the allocator, never a player
            //    problem; warnings are throttled per net / per seg.
            // ----------------------------------------------------------------
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

        // Option C gate: true when a cable burn is in flight on either side of this contributor's span,
        // so the allocator must not commit a durable lockout against a topology that is about to split.
        private static bool SegNetPending(Seg seg)
            => (seg.InNet != null && SplitPendingRegistry.IsPending(seg.InNet.ReferenceId))
               || (seg.OutNet != null && SplitPendingRegistry.IsPending(seg.OutNet.ReferenceId));

        // =====================================================================
        // ALLOCATOR: topological backward-demand / forward-supply
        // sweep, iterated to a fixed point with RE-DECIDABLE deprioritization + overload.
        // Computes each contributor's exact in-tick throughput (no headroom) and
        // avoids the 60-second freeze of a deprioritization that another device's overload
        // would have relieved (the 2c case).
        // =====================================================================

        // A contributor is eligible to DESIRE power (backward pass) when it is not locked and not
        // overloaded. Deprioritized is deliberately IGNORED here: the forward pass re-decides deprioritization
        // every round, so a previously-deprioritized contributor must still present its desired pull to be
        // reconsidered. The inflow this can leave committed toward a still-deprioritized consumer at the final state is
        // removed after the loop by the stranded-inflow clawback in RunAtomic, not by changing this gate.
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
        // RE-DECIDES deprioritization against the settled upstream supply), then the overload pass (which
        // RE-DECIDES overload against the post-deprioritization demand). Forward-before-overload means a
        // deprioritization that relieves an over-demanded network is honoured the same round, killing the
        // deprioritization<->overload 2-cycle. Converges when the (deprioritized, overload) sets stop changing. Bounded by 2N+4 rounds (the
        // hard cap; N = segmenter count): a tick that exhausts the bound keeps the last settled state
        // (internally consistent, safe) and logs the throttled warning below. A detected 2-cycle
        // resolves to the safe union of the two states, then settles throughputs.

        // Non-convergence diagnostic throttle (the TickDurationWatchdog idiom): hitting the round cap
        // is safe but worth one aggregated line, not one per tick at 2 Hz. The tick counter never
        // resets on world load, so no load-boundary reset is needed.
        private const int NonConvergenceWarnCooldownTicks = 600;   // ~5 minutes at 2 Hz
        private static int _lastNonConvergenceWarnTick = -NonConvergenceWarnCooldownTicks;

        private static void RunAllocationLoop(List<Net> topo, List<Net> topoRev, List<Seg> segs,
            List<Elastic> elastics, List<Net> netList)
        {
            // Clean slate once per tick. Within the loop DEPRIORITIZED is re-decided every round (ForwardSupplyAndDeprioritize
            // clears it); OVERLOAD only ever GROWS (sticky: committed on detection, reset only by the 60 s
            // timeout or a player turn-off), so it is cleared here once and never inside a round. The
            // overload kind bit and payload pair travel with the flag: written by the detector that
            // first trips the device, cleared only here.
            foreach (var seg in segs)
            {
                seg.Deprioritized = false;
                seg.Overloaded = false;
                seg.CableOverloaded = false;
                seg.OverloadValueW = 0f;
                seg.OverloadCapW = 0f;
                seg.OverloadStorageW = 0f;
                // The deprioritized decision fields (ShortfallW / Reason / VictimPriority) are NOT
                // reset here, matching the sticky DeprioritizedNeedsW / UpstreamDemandW /
                // UpstreamSupplyW triple: they are only read when Deprioritized is set, and the
                // 2-cycle union re-mark reuses the values from the round that last marked the seg.
            }
            foreach (var e in elastics)
            {
                e.Overloaded = false;
                e.CableOverloaded = false;
                e.OverloadValueW = 0f;
                e.OverloadCapW = 0f;
            }

            int maxRounds = 2 * segs.Count + 4;
            HashSet<long> prevDeprioritized = null, prevOver = null, prevEl = null;
            HashSet<long> prev2Deprioritized = null, prev2Over = null, prev2El = null;
            bool converged = false;

            for (int round = 0; round < maxRounds; round++)
            {
                BackwardDesirePass(topoRev);
                // Overload is evaluated BEFORE deprioritization and is grow-only (precedence CYCLE > CURRENT-MISMATCH >
                // CABLE-OVERLOADED > DEVICE-OVERLOADED > DEPRIORITIZED, POWER.md decision 3). Only DEPRIORITIZED is
                // re-decidable within a tick: a transformer that
                // structurally cannot serve its downstream is diagnosed as OVERLOAD here, before the deprioritization
                // pass could mislabel it as input-starved. The structural rule is desire-based (pre-deprioritization);
                // the supply rules (elastic / cable) need the forward pass's Unmet, so they run after it.
                DetectStructuralOverload(netList, segs);
                ForwardSupplyAndDeprioritize(topo, segs, settleOnly: false);
                DetectSupplyOverload(netList, elastics);

                var curDeprioritized = CollectFlagged(segs, deprioritized: true);
                var curOver = CollectFlagged(segs, deprioritized: false);
                var curEl = CollectFlaggedElastic(elastics);

                if (prevDeprioritized != null && curDeprioritized.SetEquals(prevDeprioritized)
                    && curOver.SetEquals(prevOver) && curEl.SetEquals(prevEl))
                {
                    converged = true;
                    break;
                }
                if (prev2Deprioritized != null && curDeprioritized.SetEquals(prev2Deprioritized)
                    && curOver.SetEquals(prev2Over) && curEl.SetEquals(prev2El))
                {
                    // 2-cycle between two states: OR the intermediate state's flags in (safe superset,
                    // never under-protective), then settle throughputs without re-deciding.
                    foreach (var seg in segs)
                    {
                        if (prevDeprioritized.Contains(seg.RefId)) seg.Deprioritized = true;
                        if (prevOver.Contains(seg.RefId)) seg.Overloaded = true;
                    }
                    foreach (var e in elastics) if (prevEl.Contains(e.RefId)) e.Overloaded = true;
                    BackwardDesirePass(topoRev);
                    ForwardSupplyAndDeprioritize(topo, segs, settleOnly: true);
                    converged = true;
                    break;
                }
                prev2Deprioritized = prevDeprioritized; prev2Over = prevOver; prev2El = prevEl;
                prevDeprioritized = curDeprioritized; prevOver = curOver; prevEl = curEl;
            }

            if (!converged)
            {
                int tick = ElectricityTickCounter.CurrentTick;
                if (tick - _lastNonConvergenceWarnTick >= NonConvergenceWarnCooldownTicks)
                {
                    _lastNonConvergenceWarnTick = tick;
                    UnityEngine.Debug.LogWarning(
                        $"[PowerGridPlus] Allocator did not converge in {maxRounds} rounds ({segs.Count} segmenter(s), {netList.Count} network(s), tick {tick}); using last settled state (internally consistent, safe).");
                }
            }
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
                    // A contributor with rigid desire presents throughput * m + quiescent. An
                    // idle always-on-quiescent contributor (APC) still presents its bare
                    // quiescent: vanilla bills that draw whenever the device is ON, so leaving
                    // it out of the demand model starves the net by exactly one quiescent while
                    // the allocator calls it served (the persistent 160/150 partial-power
                    // finding). Every other seg kind keeps the idle-bills-nothing model.
                    c.DesiredPull = (DesireActive(c) && c.DesiredThroughput > 0f)
                        ? c.DesiredThroughput * Mathf.Max(c.InputDrawFactor, 1f) + c.UsedPower
                        : (DesireActive(c) && c.QuiescentAlwaysOn ? c.UsedPower : 0f);
                    pulls += c.DesiredPull;
                    // Soft gates on IsActive, NOT DesireActive: rigid must keep a deprioritized
                    // contributor's claim visible so the forward pass can re-decide the
                    // deprioritization, but soft never drives a deprioritization, so a deprioritized
                    // contributor's charge desire must NOT size its suppliers -- the delivered soft
                    // would strand on this net (billed upstream, consumed by nobody). A contributor
                    // released from deprioritization next round restores the desire one round later.
                    c.SoftDesiredPull = (IsActive(c) && c.SoftDesiredThroughput > 0f)
                        ? c.SoftDesiredThroughput * Mathf.Max(c.InputDrawFactor, 1f)
                          + (c.DesiredPull > 0f ? 0f : c.UsedPower)
                        : 0f;
                    softPulls += c.SoftDesiredPull;
                }
                n.DesiredDemand = n.RigidDemand + pulls;
                n.SoftDesire = BillableSoftRequestLocal(n) + softPulls;

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
        // to its consumers highest-priority-first. When deciding (settleOnly == false) deprioritization is
        // RE-DECIDED here: if the active consumers' desired RIGID pulls exceed the budget the network can
        // pass, victims are deprioritized (whole, never partial) per the tier-major best-fit rule in
        // SelectDeprioritizationVictims until the rest fit; a network with
        // no supply at all (avail <= Eps) deprioritizes nothing (dead-input idle). settleOnly == true keeps the
        // current deprioritized/overload flags and only recomputes throughputs + per-net fields (used to settle a
        // 2-cycle). After the rigid grants, SOFT (storage charge) is granted per net from the firm
        // residual: deprioritization decisions, budgets, and Unmet never see the soft class.

        // Caller-side scratch for the victim selector's candidate tuples. Power-worker only; the
        // selector itself keeps no state, so its purity / re-entrancy is unaffected by this buffer.
        private static readonly List<(long refId, int priority, float claim, bool stepUp)> _victimCandidateBuffer
            = new List<(long refId, int priority, float claim, bool stepUp)>();

        // Leaf-first victim protection: true while this seg's output net feeds at least one other
        // ACTIVE (unlocked, unoverloaded, not deprioritized) seg, i.e. the seg is a mid-chain hop.
        // Evaluated fresh inside every deciding round, so a hop becomes eligible for deprioritization
        // the round after its whole subtree went dark, and only then.
        private static bool FeedsActiveSeg(Seg c)
        {
            var outNet = c.OutNetModel;
            if (outNet == null) return false;
            var children = outNet.Consumers;
            for (int i = 0; i < children.Count; i++)
            {
                var child = children[i];
                if (!ReferenceEquals(child, c) && IsActive(child)) return true;
            }
            return false;
        }

        private static void ForwardSupplyAndDeprioritize(List<Net> topo, List<Seg> segs, bool settleOnly)
        {
            foreach (var seg in segs)
            {
                seg.Throughput = 0f;
                seg.Pull = 0f;
                seg.SoftThrough = 0f;
                seg.SoftPull = 0f;
                if (!settleOnly) seg.Deprioritized = false;
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

                if (!settleOnly && avail > Eps)
                {
                    float claims = 0f;
                    foreach (var c in n.Consumers)
                        if (!c.Locked && !c.Overloaded && !c.Deprioritized) claims += c.DesiredPull;
                    if (claims > budget + Eps)
                    {
                        // Victim CHOICE is delegated to the pure selector (tier-major best-fit,
                        // POWER.md 8.3 / 8.3.3); this block only feeds it the live candidates and
                        // marks the returned set whole. Locked / overloaded / already-deprioritized segs
                        // never enter (live per-round gates); the step-up and tiny-claim gates are
                        // policy and live inside SelectDeprioritizationVictims. If the selector runs out of
                        // eligible candidates the residual deficit is accepted as-is, exactly as
                        // the old walk's null-victim break did.
                        _victimCandidateBuffer.Clear();
                        foreach (var c in n.Consumers)
                            if (!c.Locked && !c.Overloaded && !c.Deprioritized)
                                // Leaf-first deprioritization (POWER.md §0 decision 24 stage 4): a seg whose
                                // output net still feeds ACTIVE child segs is a HOP and is protected
                                // exactly like a step-up (partial grant, never a victim), so the
                                // deficit forwards downstream until it reaches segs feeding leaf
                                // nets. As children are deprioritized across rounds, their parent
                                // stops being a hop and becomes eligible: deprioritization escalates
                                // leaf-to-trunk, and a no-practical-load trunk chain is never
                                // deprioritized at all.
                                _victimCandidateBuffer.Add((c.RefId, c.Priority, c.DesiredPull, c.StepUp || FeedsActiveSeg(c)));
                        var victims = SelectDeprioritizationVictimsDetailed(_victimCandidateBuffer, claims - budget);
                        for (int vi = 0; vi < victims.Count; vi++)
                        {
                            var victim = victims[vi];
                            foreach (var c in n.Consumers)
                                if (c.RefId == victim.RefId)
                                {
                                    c.Deprioritized = true;
                                    // Hover payload (the block FaultHover renders, POWER.md §11.1):
                                    // the victim's own rigid pull, the net's
                                    // total rigid want this round (local rigid loads + every
                                    // active contributor claim, the same terms the deficit above
                                    // is made of), and the supply the net could actually raise.
                                    c.DeprioritizedNeedsW = c.DesiredPull;
                                    c.DeprioritizedUpstreamDemandW = n.RigidDemand + claims;
                                    c.DeprioritizedUpstreamSupplyW = avail;
                                    // Decision fields the selector recorded as this victim was cut:
                                    // the decision-time shortfall, which reason case applied, and
                                    // the victim's own priority value (the "priority of 80" text).
                                    c.DeprioritizedShortfallW = victim.ShortfallAtCut;
                                    c.DeprioritizedReason = victim.Reason;
                                    c.DeprioritizedVictimPriority = victim.Priority;
                                    break;
                                }
                        }
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

                // ---- Soft grants (storage charge), funding ladder per POWER.md §9.2 ----
                // Pool, consumed in this order: (1) the local firm residual (generators + rigid
                // contributor inflow left after rigid loads and granted rigid pulls), (2) the soft
                // inflow arriving through this net's suppliers (granted when their input nets were
                // processed earlier in topo order), (3) the net's ELASTIC LEFTOVER: eligible
                // discharge capacity the rigid settlement above did not consume (the lowest rung,
                // battery-to-battery transfer). Capped by the weakest cable's remaining headroom
                // so a charge grant cannot push a network past its tier rating. Granted
                // proportionally over local charge requests and active consumers' soft pulls; a
                // deprioritized / locked / overloaded consumer gets zero soft. Note: if any rigid pull on
                // this net was only partially granted, the firm residual is necessarily zero, so a
                // firm-funded soft grant here implies every rigid pull was granted whole.
                //
                // Fixed-point safety: soft never enters budgets, victim selection, overload
                // detection, or the dead-input cue (all rigid-only, §9.6), so funding soft from
                // elastic leftover cannot create a new decision-oscillation mode. The leftover is a
                // pure function of this round's rigid settlement (RigidDemand, survivorPull,
                // firmIn, elasticCap), and none of those inputs is touched by the soft stage; the
                // loop's convergence test stays the (deprioritized, overload, elastic-overload) sets.
                float softLocal = firmIn - n.RigidDemand - survivorPull;
                if (softLocal < 0f) softLocal = 0f;
                float softInflow = 0f;
                foreach (var s in n.Suppliers)
                    if (IsActive(s)) softInflow += s.SoftThrough;
                // Elastic leftover: eligible discharge capacity minus what the rigid settlement
                // consumed. The rigid draw on elastic is max(0, RigidDemand + survivorPull -
                // firmIn), the same formula the final elastic-share pass sizes rigid shares with,
                // so leftover here and (EffDischarge - rigid Share) there agree at the fixed point.
                float rigidElasticDraw = n.RigidDemand + survivorPull - firmIn;
                if (rigidElasticDraw < 0f) rigidElasticDraw = 0f;
                float elasticLeftover = elasticCap - rigidElasticDraw;
                if (elasticLeftover < 0f) elasticLeftover = 0f;
                float softFirm = softLocal + softInflow;
                float softAvail = softFirm + elasticLeftover;
                if (softAvail > 0f)
                {
                    float cableHeadroom = n.WeakestCap - (n.RigidServed + survivorPull);
                    if (cableHeadroom < 0f) cableHeadroom = 0f;
                    if (softAvail > cableHeadroom) softAvail = cableHeadroom;
                }

                float softDemand = BillableSoftRequestLocal(n);
                foreach (var c in n.Consumers)
                    if (IsActive(c) && c.SoftDesiredPull > 0f) softDemand += c.SoftDesiredPull;

                float softRatio = (softAvail > 0f && softDemand > Eps)
                    ? Mathf.Min(1f, softAvail / softDemand)
                    : 0f;

                float softGrantedLocal = 0f;
                foreach (var s in n.Softs)
                {
                    // A store owned by an unbillable contributor gets no grant this round: the
                    // lockout enforcement postfix (CycleFaultEnforcementPatches) zeroes the
                    // owner's vanilla bill at ENFORCE, so a share granted here could never be
                    // billed or delivered and would strand as granted-but-uncredited (the
                    // 464386 finding). GATHER already refuses registry-locked owners; this gate
                    // covers the owner that is deprioritized / overloads inside the deciding loop, and the
                    // billable-desire sums above stop upstream soft inflow from being sized for
                    // it, so nothing strands billed-but-unconsumed either.
                    s.Share = SoftOwnerBillable(s) ? s.Request * softRatio : 0f;
                    softGrantedLocal += s.Share;
                }
                float softPullsGranted = 0f;
                foreach (var c in n.Consumers)
                {
                    if (!IsActive(c) || c.SoftDesiredPull <= 0f) { c.SoftThrough = 0f; c.SoftPull = 0f; continue; }
                    float grant = c.SoftDesiredPull * softRatio;
                    float m = Mathf.Max(c.InputDrawFactor, 1f);
                    // Quiescent rides the soft pull only for the part the rigid pull does not
                    // already carry: zero when the rigid grant covers the quiescent (conducting,
                    // or a fully granted always-on idle pull), the full quiescent when the rigid
                    // side carries none, and the remainder when a quiescent-bearing rigid pull
                    // was granted only partially on a short net. Deriving from the GRANTED pull
                    // keeps the seg invariant (TotalPull == TotalThrough * m + quiescent) exact
                    // in every branch; the old desire-based choice under-carried the quiescent
                    // on a partial rigid grant that also carried soft.
                    float q = Mathf.Max(0f, c.UsedPower - c.Pull);
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
                // Elastic-funded soft quantum: the firm pool (softFirm) is consumed FIRST, so
                // elastic funds only the granted soft the firm could not cover. Measured against
                // the ACTUAL granted totals (per-consumer headroom caps can grant below
                // ratio * demand), so the elastic share pass tops up exactly what the elastics
                // fund and conservation balances by construction. When the cable-headroom clamp
                // binds below softFirm, the whole grant is firm-funded and this reads 0.
                float elasticFunded = softGrantedLocal + softPullsGranted - softFirm;
                n.ElasticFundedSoft = elasticFunded > 0f ? elasticFunded : 0f;
            }
        }

        // Overload pass (re-decidable). Three rules, recomputed fresh from the settled state each round:
        //   1. Per-network capacity hit-max (§8.4): a network whose post-deprioritization demand exceeds gen + elastic
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
        // suppliers' caps overloads those suppliers. Runs BEFORE the deprioritization pass so a transformer that
        // structurally cannot serve its downstream is diagnosed as OVERLOAD (the higher-precedence fault)
        // instead of getting deprioritized first and mislabeled input-starved. GROW-ONLY: never clears within a tick,
        // so the overload commits even if a same-tick deprioritization in its subnetwork would have removed the condition
        // (desired: overload is the structural signal the player must act on). Desire-based, no forward
        // dependency, so it is safe to run before the forward pass.
        private static void DetectStructuralOverload(List<Net> netList, List<Seg> segs)
        {
            foreach (var n in netList)
            {
                float demand = n.RigidDemand;
                foreach (var c in n.Consumers)
                    if (IsActive(c)) demand += c.DesiredPull;
                float elasticCap = AvailableElastic(n);
                float cap = n.GenSupply + elasticCap;
                foreach (var s in n.Suppliers)
                    if (!s.Locked && !s.Deprioritized) cap += s.EffCap;
                if (demand <= cap + Eps) continue;
                foreach (var s in n.Suppliers)
                {
                    if (s.Locked || s.Deprioritized || s.Overloaded) continue;
                    if (s.CapSetting >= float.MaxValue) continue;   // APC: no throughput rating to hit
                    if (s.CapSetting > s.CableCap) continue;        // cable-limited (rule 3), not Setting-limited
                    s.Overloaded = true;                            // includes input-limited PT pairs (taken offline)
                    // Fault-1 hover payload: the net's rigid desire against the combined deliverable
                    // cap, the exact locals of this rule. Net-level numbers, shared by every supplier
                    // the rule flags on this net. StorageW is the elastic (battery) slice of the cap,
                    // so the hover can split it from the upstream gen + supplier caps.
                    s.OverloadValueW = demand;
                    s.OverloadCapW = cap;
                    s.OverloadStorageW = elasticCap;
                }
            }
        }

        // Supply overload (rules 2 and 3): elastic hit-max and the §5.7 cable overflow. Both read the forward
        // pass's Unmet / PullsGranted, so they run AFTER ForwardSupplyAndDeprioritize. GROW-ONLY, like the structural
        // pass: never cleared within a tick. Rule 2 stays in the capacity fault family (OverloadRegistry;
        // the cohort commit in RunAtomic computes its shared payload); rule 3 is the CABLE fault kind
        // (CableOverloadRegistry) and stamps the kind bit plus the (flow, weakest cable cap) payload here.
        private static void DetectSupplyOverload(List<Net> netList, List<Elastic> elastics)
        {
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
                float cableCap = n.WeakestCap;
                if (flow <= cableCap || n.GenSupply > cableCap) continue;
                foreach (var s in n.Suppliers)
                {
                    if (s.Locked || s.Deprioritized || s.Overloaded) continue;
                    s.Overloaded = true;
                    s.CableOverloaded = true;
                    s.OverloadValueW = flow;
                    s.OverloadCapW = cableCap;
                }
                foreach (var e in n.Elastics)
                {
                    if (e.Locked || e.Overloaded) continue;
                    e.Overloaded = true;
                    e.CableOverloaded = true;
                    e.OverloadValueW = flow;
                    e.OverloadCapW = cableCap;
                }
            }
        }

        private static HashSet<long> CollectFlagged(List<Seg> segs, bool deprioritized)
        {
            var set = new HashSet<long>();
            foreach (var seg in segs)
                if (deprioritized ? seg.Deprioritized : seg.Overloaded) set.Add(seg.RefId);
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

        private static bool IsActive(Seg seg) => !seg.Locked && !seg.Deprioritized && !seg.Overloaded;

        // A local store's charge grant is billable only while its OWNER can bill this round:
        // the APC's routed seg active (not locked / deprioritized / overloaded), and the store's own
        // discharge half not elastic-overloaded (the cohort commit stamps the registry in the
        // same tick, which zeroes the owner's vanilla bill via CycleFaultEnforcementPatches).
        // GATHER never enrolls registry-locked owners, so this gate exists for the in-loop
        // decisions only; flags settle at the fixed point, so the converged state carries no
        // desire, no inflow, and no grant for an unbillable store (the 464386 finding).
        private static bool SoftOwnerBillable(Soft s)
            => (s.OwnerSeg == null || IsActive(s.OwnerSeg))
               && (s.OwnerElastic == null || !s.OwnerElastic.Overloaded);

        // The net's local charge desire from billable owners only, recomputed per round so the
        // backward pass never sizes upstream soft inflow for a store that cannot receive it.
        private static float BillableSoftRequestLocal(Net n)
        {
            float sum = 0f;
            foreach (var s in n.Softs)
                if (SoftOwnerBillable(s)) sum += s.Request;
            return sum;
        }

        // Diagnostic tolerance for the shortfall classifier's supply comparisons. kW-scale float
        // sums carry more rounding noise than the allocator's own Eps (0.01 W), and the census's
        // vanilla-side test already works with a 0.5 W margin, so the classification questions
        // ("did the seg have headroom", "did the input net retain supply") use the same 0.5 W.
        private const float DiagEps = 0.5f;

        // End-of-tick shortfall class for one allocator net (the ShortfallDiagnostics byte values),
        // derived entirely from the converged state: Unmet / GenSupply / InflowCommitted /
        // RigidServed / PullsGranted per net, Locked / Deprioritized / Overloaded / EffCap / Throughput /
        // SoftThrough per seg. Diagnostics only; decides nothing. Decision ladder:
        //   Served    - no unmet rigid demand.
        //   Deadlock  - unmet while the allocator's own accounting says supply existed: an ACTIVE
        //               supplier had headroom above its committed flow AND its input net retained
        //               undelivered supply (gen + inflow + elastic beyond what it served and
        //               granted). On a correct allocator this is impossible (an unmet net's
        //               suppliers either sit at their caps or drained their inputs), so it is the
        //               invisible-deadlock regression shape and must be zero on a healthy build.
        //               Checked BEFORE the throttle rung so a genuine routing failure is never
        //               masked by an unrelated closed valve on the same net (e.g. a tripped
        //               battery next to a deadlocked transformer feed).
        //   Throttled - unmet, and some feed valve is deliberately closed: a supplier seg that is
        //               lockout-locked / deprioritized / overloaded or has zero effective capacity
        //               (Setting=0 "firewall", rate-limited to zero), or a locked / overloaded
        //               elastic on the net. Wins over Dry when both apply (a closed valve makes
        //               the upstream state moot; honest darkness either way).
        //   Dry       - unmet with every remaining feed genuinely exhausted: each active supplier
        //               is saturated or draws from an input net with nothing left. Source-side
        //               shortage (dead-input chains, unaimed solar islands).
        private static byte ClassifyNetShortfall(Net n, Dictionary<long, Net> nets)
        {
            if (n.Unmet <= Eps) return ShortfallDiagnostics.Served;

            foreach (var s in n.Suppliers)
            {
                if (!IsActive(s)) continue;   // closed valve: the throttle rung below reports it
                float headroom = s.EffCap - s.Throughput - s.SoftThrough;
                if (headroom <= DiagEps) continue;   // saturated: transport maxed, not a routing failure
                if (s.InNet == null || !nets.TryGetValue(s.InNet.ReferenceId, out var src)) continue;
                float leftover = src.GenSupply + src.InflowCommitted + AvailableElastic(src)
                                 - src.RigidServed - src.PullsGranted;
                if (leftover > DiagEps) return ShortfallDiagnostics.Deadlock;
            }

            foreach (var s in n.Suppliers)
                if (s.Locked || s.Deprioritized || s.Overloaded || s.EffCap <= Eps)
                    return ShortfallDiagnostics.Throttled;
            foreach (var e in n.Elastics)
                if (e.Locked || e.Overloaded)
                    return ShortfallDiagnostics.Throttled;

            return ShortfallDiagnostics.Dry;
        }

        // (priority DESC, ReferenceId ASC): integer-only, MP-deterministic (§8.0.1).
        private static int SupplierOrder(Seg a, Seg b)
        {
            if (a.Priority != b.Priority) return b.Priority.CompareTo(a.Priority);
            return a.RefId.CompareTo(b.RefId);
        }

        // Deprioritized victim selection (POWER.md §8.3 / §8.3.3): tier-major best-fit-decreasing, the
        // deliberate policy that replaced the flat (priority ASC, ReferenceId ASC) walk. In order:
        //
        //   1. Tiers go priority ASC: the lowest tier is deprioritized first, and selection moves to the next
        //      tier only when the current tier is exhausted with deficit remaining.
        //   2. Within the tier, against the remaining deficit D (whole Watts):
        //      a. if any candidate's quantised claim covers D alone, deprioritize the SMALLEST such claim
        //         (tie: lowest ReferenceId) and stop: the deficit is covered;
        //      b. else deprioritize the LARGEST claim (tie: lowest ReferenceId), subtract it from D, and
        //         repeat within the tier.
        //   3. Step-up contributors are never candidates (§5.2), and a claim at or below Eps has
        //      nothing worth reclaiming. Locked / overloaded / already-deprioritized segs are the CALLER's
        //      gates (live per-round state, not policy), as is the dead-input carveout (§8.3.1).
        //
        // Worked §8.3.3 example: same-tier claims 500 / 1000 / 2000, deficit 1000 -> exactly the
        // 1000 W device is deprioritized (the old walk deprioritized the 500 then the 1000: two
        // victims where one sufficed).
        //
        // Quantisation (§8.0.1 / §8.0.5 determinism): float claims floor to whole Watts
        // ((int)Math.Floor); the float deficit rounds UP after the allocator's Eps tolerance
        // ((int)Math.Ceiling(deficit - Eps)). Floor-claims plus ceil-deficit guarantees the
        // selected set restores claims <= budget + Eps in float terms (an under-deprioritization here would
        // leave Unmet > Eps and spuriously trip the elastic hit-max, §8.4 rule 2); the cost is at
        // most one extra small victim when sub-Watt fractions straddle a whole-Watt boundary.
        // Every comparison is integer / ReferenceId only, so the result is a pure deterministic
        // function of (candidates, deficit): input order is irrelevant (sorted internally), no
        // live-net or Unity state, no statics, re-entrant. ScenarioRunner's
        // pgp-deprioritization-victim-fixture scenario reflection-invokes this exact method with
        // synthetic candidate sets; keep the name and signature stable.
        // A victim the selector shed, with the data the deprioritized hover needs: the remaining
        // upstream deficit at the instant it was cut (ShortfallAtCut), which branch cut it
        // (ByBestFit = rule 2a cover vs rule 2b largest-first), the victim's priority tier, and the
        // resolved DeprioritizeReason (computed once the whole set is known).
        internal struct VictimCut
        {
            public long RefId;
            public float ShortfallAtCut;
            public bool ByBestFit;
            public int Priority;
            public DeprioritizeReason Reason;
        }

        // Stable ScenarioRunner-facing shim: pgp-deprioritization-victim-fixture and the chain
        // fixture reflection-invoke this exact (IReadOnlyList<(long,int,float,bool)>, float) ->
        // List<long> signature (name + arity + return type all asserted), so keep it. It delegates
        // to the detailed selector and projects the refIds in the same order.
        internal static List<long> SelectDeprioritizationVictims(
            IReadOnlyList<(long refId, int priority, float claim, bool stepUp)> candidates,
            float deficit)
        {
            var detailed = SelectDeprioritizationVictimsDetailed(candidates, deficit);
            var victims = new List<long>(detailed.Count);
            for (int i = 0; i < detailed.Count; i++) victims.Add(detailed[i].RefId);
            return victims;
        }

        // Detailed victim selection: the SAME policy walk as the shim (POWER.md §8.3 / §8.3.3,
        // tier-major best-fit-decreasing, the identical quantisation and comparisons), with only
        // per-victim recording added. As each victim is taken the remaining deficit and the cutting
        // branch are captured; after the set is known the DeprioritizeReason is resolved per victim.
        // The selection order and set are byte-for-byte identical to the pre-recording walk.
        internal static List<VictimCut> SelectDeprioritizationVictimsDetailed(
            IReadOnlyList<(long refId, int priority, float claim, bool stepUp)> candidates,
            float deficit)
        {
            var victims = new List<VictimCut>();
            if (candidates == null || candidates.Count == 0) return victims;

            int need = deficit - Eps >= int.MaxValue ? int.MaxValue : (int)Math.Ceiling(deficit - Eps);
            if (need <= 0) return victims;

            var pool = new List<(int priority, int claim, long refId)>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.stepUp) continue;        // never deprioritized (§5.2)
                if (c.claim <= Eps) continue;  // nothing to reclaim
                int claim = c.claim >= int.MaxValue ? int.MaxValue : (int)Math.Floor(c.claim);
                pool.Add((c.priority, claim, c.refId));
            }
            if (pool.Count == 0) return victims;

            // (priority ASC, claim DESC, ReferenceId ASC): tier-major, largest-first in a tier.
            pool.Sort((a, b) =>
            {
                if (a.priority != b.priority) return a.priority.CompareTo(b.priority);
                if (a.claim != b.claim) return b.claim.CompareTo(a.claim);
                return a.refId.CompareTo(b.refId);
            });

            bool covered = false;
            int lo = 0;
            while (!covered && lo < pool.Count && need > 0)
            {
                int hi = lo;   // current tier slice [lo..hi] shares pool[lo].priority
                while (hi + 1 < pool.Count && pool[hi + 1].priority == pool[lo].priority) hi++;

                while (lo <= hi && need > 0)
                {
                    if (pool[lo].claim >= need)
                    {
                        // Rule 2a. Claims are DESC, so the entries covering D alone form the
                        // slice's prefix; the last covering claim value is the smallest cover,
                        // and the first entry holding that value is its lowest ReferenceId.
                        int last = lo;
                        while (last + 1 <= hi && pool[last + 1].claim >= need) last++;
                        int first = last;
                        while (first - 1 >= lo && pool[first - 1].claim == pool[last].claim) first--;
                        victims.Add(new VictimCut
                        {
                            RefId = pool[first].refId,
                            ShortfallAtCut = need,       // the remaining deficit this single cut clears
                            ByBestFit = true,            // rule 2a
                            Priority = pool[first].priority,
                        });
                        covered = true;   // deficit covered: selection for this net ends
                        break;
                    }
                    // Rule 2b: largest remaining claim in the tier. claim < need keeps need > 0,
                    // so a cover only ever happens through rule 2a or candidate exhaustion.
                    victims.Add(new VictimCut
                    {
                        RefId = pool[lo].refId,
                        ShortfallAtCut = need,           // remaining deficit before this claim is subtracted
                        ByBestFit = false,               // rule 2b
                        Priority = pool[lo].priority,
                    });
                    need -= pool[lo].claim;
                    lo++;
                }
                // Tier exhausted with deficit remaining: lo == hi + 1 is the next tier's start.
            }

            // Reason: a victim with a surviving same-priority peer (any candidate at its priority
            // not itself a victim, step-ups and tiny claims included) lost a within-tier tie-break,
            // so it reports the branch that cut it (EqualBestFit / EqualLargest). A victim with no
            // such survivor was shed purely on being the lowest priority tier -> LowerPriority.
            for (int vi = 0; vi < victims.Count; vi++)
            {
                var v = victims[vi];
                bool samePrioritySurvivor = false;
                for (int ci = 0; ci < candidates.Count; ci++)
                {
                    if (candidates[ci].priority != v.Priority) continue;
                    long candRef = candidates[ci].refId;
                    bool candIsVictim = false;
                    for (int wj = 0; wj < victims.Count; wj++)
                        if (victims[wj].RefId == candRef) { candIsVictim = true; break; }
                    if (!candIsVictim) { samePrioritySurvivor = true; break; }
                }
                if (!samePrioritySurvivor) v.Reason = DeprioritizeReason.LowerPriority;
                else if (v.ByBestFit) v.Reason = DeprioritizeReason.EqualBestFit;
                else v.Reason = DeprioritizeReason.EqualLargest;
                victims[vi] = v;
            }
            return victims;
        }

        // -------------------------------------------------------------------
        // Per-tick full fault-registry sync (host -> clients), POWER.md §13:
        // each non-empty registry broadcasts its full (refId, remainingTicks)
        // snapshot every tick; an empty registry sends exactly one empty
        // snapshot on the non-empty -> empty transition (so OFF-as-reset
        // clears client mirrors immediately), then goes silent.
        // -------------------------------------------------------------------

        private static bool _deprioritizedWasNonEmpty;
        private static bool _overloadWasNonEmpty;
        private static bool _cycleWasNonEmpty;
        private static bool _currentMismatchWasNonEmpty;
        private static bool _deadInputWasNonEmpty;
        private static bool _cableOverloadWasNonEmpty;

        internal static void SyncFaultSnapshots(int currentTick)
        {
            if (!Assets.Scripts.Networking.NetworkManager.IsActive) return;
            if (!Assets.Scripts.Networking.NetworkManager.IsServer) return;

            SendDeprioritized(DeprioritizedRegistry.SnapshotRemaining(currentTick), ref _deprioritizedWasNonEmpty);
            SendDeviceOverload(OverloadRegistry.SnapshotRemaining(currentTick), ref _overloadWasNonEmpty);
            SendPlain(FaultRegistrySnapshotMessage.KindCycleFault,
                CycleFaultRegistry.SnapshotRemaining(currentTick), ref _cycleWasNonEmpty);
            SendPlain(FaultRegistrySnapshotMessage.KindDeadInput,
                DeadInputRegistry.SnapshotRemaining(), ref _deadInputWasNonEmpty);
            SendWithWattPayload(FaultRegistrySnapshotMessage.KindCableOverload,
                CableOverloadRegistry.SnapshotRemaining(currentTick), ref _cableOverloadWasNonEmpty);

            var currentMismatch = new List<KeyValuePair<long, int>>();
            var violators = new List<string>();
            foreach (var (refId, remaining, names) in CurrentMismatchFaultRegistry.SnapshotRemaining(currentTick))
            {
                currentMismatch.Add(new KeyValuePair<long, int>(refId, remaining));
                violators.Add(names);
            }
            if (currentMismatch.Count > 0 || _currentMismatchWasNonEmpty)
            {
                new FaultRegistrySnapshotMessage
                {
                    Kind = FaultRegistrySnapshotMessage.KindCurrentMismatch,
                    Entries = currentMismatch,
                    Violators = violators,
                }.SendAll(0L);
            }
            _currentMismatchWasNonEmpty = currentMismatch.Count > 0;
        }

        private static void SendPlain(byte kind, IEnumerable<KeyValuePair<long, int>> snapshot, ref bool wasNonEmpty)
        {
            var entries = new List<KeyValuePair<long, int>>();
            foreach (var pair in snapshot) entries.Add(pair);
            if (entries.Count > 0 || wasNonEmpty)
                new FaultRegistrySnapshotMessage { Kind = kind, Entries = entries }.SendAll(0L);
            wasNonEmpty = entries.Count > 0;
        }

        // Cable overload carries a per-entry (flowW, capW) float pair alongside the remaining ticks;
        // the message serialises the pair (the CURRENT-MISMATCH violator-names precedent, numeric
        // edition). Device overload takes the storage-aware SendDeviceOverload path instead.
        private static void SendWithWattPayload(byte kind,
            IEnumerable<(long refId, int remainingTicks, float valueW, float capW)> snapshot,
            ref bool wasNonEmpty)
        {
            var entries = new List<KeyValuePair<long, int>>();
            var values = new List<float>();
            var caps = new List<float>();
            foreach (var (refId, remaining, valueW, capW) in snapshot)
            {
                entries.Add(new KeyValuePair<long, int>(refId, remaining));
                values.Add(valueW);
                caps.Add(capW);
            }
            if (entries.Count > 0 || wasNonEmpty)
            {
                new FaultRegistrySnapshotMessage
                {
                    Kind = kind,
                    Entries = entries,
                    PayloadValuesW = values,
                    PayloadCapsW = caps,
                }.SendAll(0L);
            }
            wasNonEmpty = entries.Count > 0;
        }

        // Device overload additionally carries the storage (elastic/battery) slice of the cap, so it
        // gets its own sender rather than sharing SendWithWattPayload (whose wire shape stays the
        // cable-overload 2-float pair).
        private static void SendDeviceOverload(
            IEnumerable<(long refId, int remainingTicks, float valueW, float capW, float storageW)> snapshot,
            ref bool wasNonEmpty)
        {
            var entries = new List<KeyValuePair<long, int>>();
            var values = new List<float>();
            var caps = new List<float>();
            var storages = new List<float>();
            foreach (var (refId, remaining, valueW, capW, storageW) in snapshot)
            {
                entries.Add(new KeyValuePair<long, int>(refId, remaining));
                values.Add(valueW);
                caps.Add(capW);
                storages.Add(storageW);
            }
            if (entries.Count > 0 || wasNonEmpty)
            {
                new FaultRegistrySnapshotMessage
                {
                    Kind = FaultRegistrySnapshotMessage.KindDeviceOverload,
                    Entries = entries,
                    PayloadValuesW = values,
                    PayloadCapsW = caps,
                    PayloadStoragesW = storages,
                }.SendAll(0L);
            }
            wasNonEmpty = entries.Count > 0;
        }

        // The deprioritized registry carries the locked hover triple (needs, upstream demand,
        // upstream supply) plus the decision fields (shortfall, reason, victim priority) alongside
        // the remaining ticks; the message serialises them all for KindDeprioritized (reason as a
        // byte on the wire).
        private static void SendDeprioritized(
            IEnumerable<(long refId, int remainingTicks, float needsW, float upstreamDemandW, float upstreamSupplyW,
                float shortfallW, DeprioritizeReason reason, int victimPriority)> snapshot,
            ref bool wasNonEmpty)
        {
            var entries = new List<KeyValuePair<long, int>>();
            var needs = new List<float>();
            var upstreamDemand = new List<float>();
            var upstreamSupply = new List<float>();
            var shortfall = new List<float>();
            var reasons = new List<byte>();
            var victimPriorities = new List<int>();
            foreach (var (refId, remaining, needsW, upstreamDemandW, upstreamSupplyW,
                shortfallW, reason, victimPriority) in snapshot)
            {
                entries.Add(new KeyValuePair<long, int>(refId, remaining));
                needs.Add(needsW);
                upstreamDemand.Add(upstreamDemandW);
                upstreamSupply.Add(upstreamSupplyW);
                shortfall.Add(shortfallW);
                reasons.Add((byte)reason);
                victimPriorities.Add(victimPriority);
            }
            if (entries.Count > 0 || wasNonEmpty)
            {
                new FaultRegistrySnapshotMessage
                {
                    Kind = FaultRegistrySnapshotMessage.KindDeprioritized,
                    Entries = entries,
                    PayloadNeedsW = needs,
                    PayloadUpstreamDemandW = upstreamDemand,
                    PayloadUpstreamSupplyW = upstreamSupply,
                    PayloadShortfallW = shortfall,
                    PayloadReason = reasons,
                    PayloadVictimPriority = victimPriorities,
                }.SendAll(0L);
            }
            wasNonEmpty = entries.Count > 0;
        }
    }
}
