using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Objects.Rockets;
using UnityEngine;

namespace PowerGridPlus
{
    /// <summary>
    ///     How a bridge device carries power between its two terminals, from the allocator's point
    ///     of view (POWER.md §5.0 / the Stage 2b segment contract):
    ///     <list type="bullet">
    ///       <item><b>Routed</b>: power crosses the device within the tick it is granted. The device
    ///       becomes one allocator Seg (a pull-through contributor): the output side delivers
    ///       TotalThrough, the input side bills TotalThrough * max(m, 1) + quiescent in the same
    ///       tick. Transformer, linked wireless pair, APC.</item>
    ///       <item><b>Buffered</b>: power stops in an internal cell; nothing crosses within the
    ///       tick. The device contributes storage roster entries instead of a Seg: its cell's charge
    ///       side is a Soft demander, its discharge side an Elastic supplier, and the physical
    ///       crossing to the far side happens outside the allocator (the rocket umbilical's vanilla
    ///       phase-2 cell-to-cell transfer).</item>
    ///     </list>
    /// </summary>
    internal enum SegAdapterKind
    {
        Routed,
        Buffered,
    }

    /// <summary>
    ///     A bridge device's per-tick PHYSICAL description: everything the allocator's GATHER phase
    ///     consumes about the device, produced by its <see cref="ISegAdapter"/>. Deliberately carries
    ///     no allocator policy -- priority, lockout state, and shed bookkeeping are attached by GATHER
    ///     (PowerAllocator) on top of this description.
    ///
    ///     <para>Routed fields. <see cref="EffectiveCapacity"/> is carried pre-computed (rather than
    ///     re-derived as min(CapacitySetting, RateLimit) - Quiescent by the consumer) so the adapter
    ///     preserves the exact arithmetic shape of the pre-contract code and the allocator's numbers
    ///     stay bit-identical across the refactor.</para>
    ///
    ///     <para>Buffered fields. <see cref="HasSoft"/> / <see cref="HasElastic"/> are explicit
    ///     presence flags (never inferred from the value: a configured rate of 0 still enrolls the
    ///     store, exactly as before the contract).</para>
    /// </summary>
    internal struct SegSpec
    {
        public SegAdapterKind Kind;
        public CableNetwork InNet;             // billing terminal (charge side for Buffered); may be null for Buffered
        public CableNetwork OutNet;            // delivery terminal (discharge side for Buffered); may be null for Buffered

        // --- Routed ---
        public float CapacitySetting;          // §8.4 hit-max threshold (float.MaxValue = unrated, e.g. APC)
        public float RateLimit;                // cable-bound cap: min(input cable cap [/ m], output cable cap)
        public float EffectiveCapacity;        // min(CapacitySetting, RateLimit) - Quiescent, clamped to >= 0
        public float Multiplier;               // input-side draw per unit delivered (PT-pair distance overhead m); 0 = no overhead (treated as 1)
        public float Quiescent;                // the device's (pair's) own idle draw, billed once per tick when it conducts
        public bool QuiescentAlwaysOn;         // vanilla bills the quiescent whenever the device is ON (APC), not only when it conducts
        public long PartnerRefId;              // second half of a pair whose own fault state also locks the seg (0 = none)
        public bool StepUp;                    // steps a lower tier up to a higher one: never sheds (§5.2)

        // --- Buffered ---
        public bool HasSoft;                   // cell charge side present this tick
        public float SoftRequest;              // min(charge rate cap, input cable cap, cell headroom)
        public bool HasElastic;                // cell discharge side present this tick
        public float ElasticCapacity;          // min(discharge rate cap, output cable cap, cell store)
    }

    /// <summary>
    ///     The segment contract between a bridge device class and the allocator. One adapter per
    ///     modelled device class; GATHER consults the adapter instead of open-coding the device's
    ///     internals, and the unknown-bridge census (<see cref="UnknownBridgeCensus"/>) uses
    ///     <see cref="Describes"/> to tell modelled classes from unmodelled ones.
    ///
    ///     <para>Threading: both methods run inside GATHER on the UniTask power worker. Managed
    ///     reads only -- no Unity API.</para>
    /// </summary>
    internal interface ISegAdapter
    {
        /// <summary>The flow model this adapter's devices present.</summary>
        SegAdapterKind Kind { get; }

        /// <summary>
        ///     Pure TYPE membership: true when this adapter is responsible for the device's class,
        ///     regardless of the device's current state. Both halves of a paired bridge belong to
        ///     their pair's adapter even though only the anchor half yields a description.
        /// </summary>
        bool Describes(ElectricalInputOutput device);

        /// <summary>
        ///     Produce the device's description for this tick. False when the device presents no
        ///     flow surface right now (errored, unlinked, missing or short-circuited terminals, or
        ///     it is the non-anchor half of a pair). The caller has already filtered switched-off
        ///     devices (the roster-wide OnOff gate), so implementations do not re-check OnOff on the
        ///     device itself (a pair's PARTNER is still checked; only the enumerated device is
        ///     pre-filtered).
        /// </summary>
        bool TryDescribe(ElectricalInputOutput device, out SegSpec spec);
    }

    /// <summary>
    ///     Adapter registry plus the shared helpers the adapters need. The four instances cover the
    ///     modelled bridge classes: Transformer / linked wireless pair / APC (Routed) and the rocket
    ///     power umbilical (Buffered). Battery and PowerReceiver carry no adapter of their own:
    ///     the battery is pure storage (enrolled directly by GATHER), and the receiver is described
    ///     through its linked transmitter by <see cref="WirelessPairAdapter"/>.
    /// </summary>
    internal static class SegAdapters
    {
        // Finite stand-in for an "unlimited" (config-0) cap. Never float.MaxValue: the allocator's
        // structural-overload detector sums supplier EffCaps, and a MaxValue term overflows that
        // sum to +Infinity.
        internal const float RateSentinel = 1e9f;

        internal static readonly TransformerAdapter Transformer = new TransformerAdapter();
        internal static readonly WirelessPairAdapter WirelessPair = new WirelessPairAdapter();
        internal static readonly ApcAdapter Apc = new ApcAdapter();
        internal static readonly UmbilicalAdapter Umbilical = new UmbilicalAdapter();

        internal static readonly ISegAdapter[] All = { Transformer, WirelessPair, Apc, Umbilical };

        /// <summary>True when any adapter models the device's class (unknown-bridge census filter).</summary>
        internal static bool AnyDescribes(ElectricalInputOutput device)
        {
            for (int i = 0; i < All.Length; i++)
                if (All[i].Describes(device)) return true;
            return false;
        }

        /// <summary>
        ///     Step-up classification (§5.2): the contributor lifts a lower cable tier onto a higher
        ///     one, so shedding it can never relieve its input network. Unknown tiers are not step-up.
        /// </summary>
        internal static bool IsStepUp(CableNetwork inNet, CableNetwork outNet)
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
    }

    /// <summary>
    ///     Routed contributor: a wired Transformer. Capacity = the player-set Setting, bounded by the
    ///     weaker of its two cables; quiescent = its own idle draw. No input-side draw overhead.
    /// </summary>
    internal sealed class TransformerAdapter : ISegAdapter
    {
        public SegAdapterKind Kind => SegAdapterKind.Routed;

        public bool Describes(ElectricalInputOutput device) => device is Transformer;

        public bool TryDescribe(ElectricalInputOutput device, out SegSpec spec)
        {
            spec = default;
            if (!(device is Transformer t)) return false;
            if (t.Error == 1 || t.InputNetwork == null || t.OutputNetwork == null) return false;
            if (t.InputNetwork.ReferenceId == t.OutputNetwork.ReferenceId) return false;
            var inCable = t.InputConnection?.GetCable();
            var outCable = t.OutputConnection?.GetCable();
            float capSetting = (float)t.Setting;
            float cableCap = Mathf.Min(CableMax.For(inCable), CableMax.For(outCable));
            float effCap = Mathf.Min(capSetting, cableCap) - t.UsedPower;
            if (effCap < 0f) effCap = 0f;
            spec = new SegSpec
            {
                Kind = SegAdapterKind.Routed,
                InNet = t.InputNetwork,
                OutNet = t.OutputNetwork,
                CapacitySetting = capSetting,
                RateLimit = cableCap,
                EffectiveCapacity = effCap,
                Multiplier = 0f,               // no distance overhead; the allocator treats 0 as m = 1
                Quiescent = t.UsedPower,
                StepUp = SegAdapters.IsStepUp(t.InputNetwork, t.OutputNetwork),
            };
            return true;
        }
    }

    /// <summary>
    ///     Routed contributor: a linked PowerTransmitter / PowerReceiver pair, modelled as ONE
    ///     contributor anchored on the transmitter (§6.2). Consumes
    ///     <see cref="PowerTransmitterPlusInterop"/> for the link rating and the source-draw
    ///     multiplier m: the ModApi tier when a 1.9.0+ PowerTransmitterPlus is loaded, the legacy
    ///     reflection tier against the shipped Workshop 1.8.0, or the vanilla curve when the mod is
    ///     absent. The receiver half never yields its own description.
    /// </summary>
    internal sealed class WirelessPairAdapter : ISegAdapter
    {
        public SegAdapterKind Kind => SegAdapterKind.Routed;

        public bool Describes(ElectricalInputOutput device)
            => device is PowerTransmitter || device is PowerReceiver;

        public bool TryDescribe(ElectricalInputOutput device, out SegSpec spec)
        {
            spec = default;
            if (!(device is PowerTransmitter pt)) return false;
            var pr = pt.LinkedReceiver;
            if (pr == null || !pr.OnOff || pt.InputNetwork == null || pr.OutputNetwork == null) return false;
            if (pt.InputNetwork.ReferenceId == pr.OutputNetwork.ReferenceId) return false;

            // The PT/PR pair's cap is a STATIC link RATING, never the live
            // InputNetwork.PotentialLoad. Reading the live potential (as vanilla / PTP
            // GetGeneratedPower do) created a cross-tick zero fixed point: on a
            // transformer-fed source the potential reads 0 until something pulls, so the cap
            // collapsed to 0, the pair desired 0, nothing pulled, and a false OVERLOAD re-armed
            // forever. The forward supply sweep is the only throttle on actually-delivered
            // power; the rating only sizes a genuine OVERLOAD breach (delivered demand above
            // what the link itself can carry, independent of the source).
            float linkRate;
            float ptpCap = PowerTransmitterPlusInterop.EffectiveMaxCapacityOrAbsent();
            if (ptpCap >= 0f)
            {
                // PowerTransmitterPlus loaded: rating = the configured Max Transfer Capacity
                // (0 = unlimited -> a large FINITE sentinel, never float.MaxValue). PTP reprices
                // distance as a source-side overhead m (folded into the input-cable bound
                // below), not an output derate, so no distance term enters the rating here.
                linkRate = ptpCap > 0f ? ptpCap : SegAdapters.RateSentinel;
            }
            else
            {
                // Vanilla (no PowerTransmitterPlus): rating = MaxPowerTransmission minus the
                // vanilla distance-DELIVERY loss (PowerLossOverDistance), independent of the
                // live source potential.
                float maxT = PowerTransmitter.MaxPowerTransmission;
                float dist = PowerTransmitterPlusInterop.LinkedReceiverDistance(pt);
                float loss;
                try { loss = pt.PowerLossOverDistance.Evaluate(Mathf.Clamp01(dist / 500f)) * maxT; }
                catch { loss = Mathf.Min(dist * 10f, maxT); }
                if (float.IsNaN(loss) || loss < 0f) loss = 0f;
                linkRate = Mathf.Max(0f, maxT - loss);
            }

            // Source-draw multiplier (POWER.md §6.3 / §8.4.2): PowerTransmitterPlus inflates the
            // INPUT-side draw to delivered * m (m = 1 + k * distance_km); vanilla m = 1. The
            // output cable carries delivered; the input cable carries delivered * m. Clamp to a
            // finite sentinel so an unlimited (config-0) tier cannot reach the EffCap sum as
            // +Infinity.
            float m = PowerTransmitterPlusInterop.SourceDrawMultiplier(pt);
            var inCable = pt.InputConnection?.GetCable();
            var outCable = pr.OutputConnection?.GetCable();
            float cableCap = Mathf.Min(Mathf.Min(CableMax.For(inCable) / m, CableMax.For(outCable)), SegAdapters.RateSentinel);

            // staticCap <= cableCap always, so the §5.7 "CapSetting > CableCap" cable-bound
            // exclusion can no longer fire for a PT pair: a cable-bound breach now reads as a
            // genuine OVERLOAD instead of silently under-delivering with no hover.
            float staticCap = Mathf.Min(linkRate, cableCap);
            float effCap = staticCap - pt.UsedPower - pr.UsedPower;
            if (effCap < 0f) effCap = 0f;
            spec = new SegSpec
            {
                Kind = SegAdapterKind.Routed,
                InNet = pt.InputNetwork,
                OutNet = pr.OutputNetwork,
                CapacitySetting = staticCap,
                RateLimit = cableCap,
                EffectiveCapacity = effCap,
                Multiplier = m,
                Quiescent = pt.UsedPower + pr.UsedPower,
                PartnerRefId = pr.ReferenceId,   // a cycle-faulted receiver locks the pair too
                StepUp = SegAdapters.IsStepUp(pt.InputNetwork, pr.OutputNetwork),
            };
            return true;
        }
    }

    /// <summary>
    ///     Routed contributor: a wired AreaPowerControl. No vanilla throughput rating
    ///     (CapacitySetting = float.MaxValue, so the §8.4 hit-max rule never applies); the cable
    ///     bound is the only capacity. The APC's internal battery cell is NOT part of this routed
    ///     description -- GATHER enrolls the cell separately as storage (elastic discharge + soft
    ///     charge) alongside the seg.
    /// </summary>
    internal sealed class ApcAdapter : ISegAdapter
    {
        public SegAdapterKind Kind => SegAdapterKind.Routed;

        public bool Describes(ElectricalInputOutput device) => device is AreaPowerControl;

        public bool TryDescribe(ElectricalInputOutput device, out SegSpec spec)
        {
            spec = default;
            if (!(device is AreaPowerControl apc)) return false;
            if (apc.Error == 1 || apc.InputNetwork == null || apc.OutputNetwork == null) return false;
            if (apc.InputNetwork.ReferenceId == apc.OutputNetwork.ReferenceId) return false;
            var inCable = apc.InputConnection?.GetCable();
            var outCable = apc.OutputConnection?.GetCable();
            float cableCap = Mathf.Min(CableMax.For(inCable), CableMax.For(outCable));
            float effCap = cableCap - apc.UsedPower;
            if (effCap < 0f) effCap = 0f;
            spec = new SegSpec
            {
                Kind = SegAdapterKind.Routed,
                InNet = apc.InputNetwork,
                OutNet = apc.OutputNetwork,
                CapacitySetting = float.MaxValue,   // no vanilla throughput rating; §8.4 hit-max does not apply
                RateLimit = cableCap,
                EffectiveCapacity = effCap,
                Multiplier = 0f,
                Quiescent = apc.UsedPower,
                // Vanilla bills an ON APC's idle draw whenever it has an output network
                // (Max(_powerProvided, UsedPower), 0.2.6403 decompile 391035), conducting or
                // not, so the allocator must fund a quiescent-only pull for an idle APC or
                // the vanilla Required sits one quiescent above Potential on a served net
                // (the persistent 160/150 partial-power finding). Transformer and wireless
                // billing is cache-driven and bills 0 idle, so only the APC carries this.
                QuiescentAlwaysOn = true,
                StepUp = false,                     // APC is never tier-classified step-up
            };
            return true;
        }
    }

    /// <summary>
    ///     Buffered bridge: the rocket power umbilical pair (RocketPowerUmbilicalMale /
    ///     RocketPowerUmbilicalFemale, shared abstract base RocketPowerUmbilical since game
    ///     0.2.6403). Formalizes the STORE-AND-FORWARD model the allocator has always applied to
    ///     umbilicals, deliberately NOT a routed seg:
    ///
    ///     <list type="bullet">
    ///       <item>Each half is an independent buffered store on its own grid. Charging the cell is
    ///       a Soft request on the half's InputNetwork; discharging the cell is Elastic supply on
    ///       its OutputNetwork. Both ride the unified flow classes, so a cell charges behind a
    ///       transformer or wireless pair like any battery.</item>
    ///       <item>The PHYSICAL crossing between the two cells happens outside the allocator, in
    ///       vanilla phase 2 (device OnPowerTick, after ENFORCE), as direct cell-to-cell
    ///       ReceivePower(null, ...) calls with no cable network involved. Since game 0.2.6403 the
    ///       crossing is BIDIRECTIONAL: the Male pushes min(partner headroom, own store) into the
    ///       Female every tick (station -> rocket), and the Female runs its own
    ///       TransferProgress-gated transfer that pulls the partner's headroom out of
    ///       RocketNetwork.Batteries into its cell and pushes it to the Male (rocket -> station).
    ///       Neither direction is modelled here; the allocator only sees each cell's level change
    ///       between ticks. See Research/GameClasses/RocketPowerUmbilical.md.</item>
    ///       <item>Store-and-forward latency follows: grid -> near cell this tick, cell -> partner
    ///       cell in phase 2, partner cell -> far grid on a later tick.</item>
    ///     </list>
    ///
    ///     <para>Rate caps come from the Server - Rocket Umbilical settings when enabled (else the
    ///     cell's PowerMaximum, i.e. vanilla behaviour), both further bounded by the respective
    ///     cable's tier cap, mirroring RocketUmbilicalPatches.</para>
    /// </summary>
    internal sealed class UmbilicalAdapter : ISegAdapter
    {
        public SegAdapterKind Kind => SegAdapterKind.Buffered;

        public bool Describes(ElectricalInputOutput device) => device is RocketPowerUmbilical;

        public bool TryDescribe(ElectricalInputOutput device, out SegSpec spec)
        {
            spec = default;
            if (!(device is RocketPowerUmbilical umbilical)) return false;
            if (umbilical.Error == 1) return false;
            if (umbilical.InputNetwork != null && umbilical.OutputNetwork != null
                && umbilical.InputNetwork.ReferenceId == umbilical.OutputNetwork.ReferenceId) return false;   // short-circuit gate
            float stored = umbilical.PowerStored;
            float maximum = umbilical.PowerMaximum;
            bool limits = Settings.EnableRocketUmbilicalLimits.Value;
            float chargeRate = limits ? Settings.RocketUmbilicalChargeRate.Value : maximum;
            float dischargeRate = limits ? Settings.RocketUmbilicalDischargeRate.Value : maximum;
            spec.Kind = SegAdapterKind.Buffered;
            spec.InNet = umbilical.InputNetwork;
            spec.OutNet = umbilical.OutputNetwork;
            if (umbilical.OutputNetwork != null && stored > 0f)
            {
                var outCable = umbilical.OutputConnection?.GetCable();
                spec.HasElastic = true;
                spec.ElasticCapacity = Mathf.Min(Mathf.Min(dischargeRate, CableMax.For(outCable)), stored);
            }
            if (umbilical.InputNetwork != null)
            {
                float headroom = maximum - stored;
                if (headroom > 0f)
                {
                    var inCable = umbilical.InputConnection?.GetCable();
                    spec.HasSoft = true;
                    spec.SoftRequest = Mathf.Min(Mathf.Min(chargeRate, CableMax.For(inCable)), headroom);
                }
            }
            return spec.HasElastic || spec.HasSoft;
        }
    }
}
