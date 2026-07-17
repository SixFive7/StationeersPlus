using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Objects.Rockets;

namespace PowerGridPlus.Core
{
    /// <summary>
    ///     The per-tick grid snapshot: topology + one boundary read (POWER.md §0 decision 24, the
    ///     B + D1 data plane). Built ONCE at the top of every atomic tick on the power worker;
    ///     everything downstream (PROTECT detectors, the allocator's GATHER, the write-back, the
    ///     ownership sweep) consumes these rows and never touches <c>AllCableNetworks</c> /
    ///     <c>PowerDeviceList</c> or re-reads a device's demand.
    ///
    ///     <para><b>Topology source.</b> Membership is derived from <c>lock(DeviceList)</c> plus the
    ///     same power-port predicate vanilla's list rebuild uses (<c>device.PowerCables[i]
    ///     .CableNetwork == net</c>, decompile 270772-270782), so the snapshot never calls the
    ///     <c>PowerDeviceList</c> getter and never races its UNLOCKED in-place lazy rebuild
    ///     (270690-270699). Rebuilt every tick: that is exactly the per-net list copy vanilla's
    ///     <c>PowerTick.Initialise</c> paid each tick, and it makes topology-event coverage a
    ///     non-problem by construction.</para>
    ///
    ///     <para><b>Demand discipline.</b> One <c>GetUsedPower</c> / <c>GetGeneratedPower</c> sample
    ///     per device per tick, here and nowhere else. The accumulator classes go through
    ///     <see cref="DemandModel"/> (reconstructed formulas + drain bookkeeping, decision 26).
    ///     Segmenter rows also carry one sample of each surface purely for the per-net
    ///     Required/Potential sums (the tier / cycle power-flow gates): those patched surfaces serve
    ///     the previous tick's published totals, which is exactly what vanilla OBSERVE saw.</para>
    /// </summary>
    internal sealed class GridSnapshot
    {
        internal sealed class DeviceRow
        {
            public Device Device;
            public long RefId;
            public Cable PowerCable;            // first power cable on this net (burn adjacency)
            public string DisplayName;          // pretty prefab display name, captured here on the worker (see ResolveDisplayName)

            // Classification (computed once per tick).
            public ElectricalInputOutput Eio;   // null for plain devices
            public bool IsSegmenter;            // Eio != null && registry says segmenter
            public bool IsTransformer;
            public bool IsProducerClass;        // ProducerClassifier.IsProducer
            public bool IsActiveProducer;
            public bool UnknownProducerLike;    // !producer, !EIO, Generated > 0
            public bool DeliveryEffect;         // ReceivePower carries gameplay (WriteBack shim)

            // Control. OnOff is captured for every device (segmenter and plain) so downstream
            // consumers (the cycle graph, the ownership expectation) never re-read it live.
            public bool OnOff;
            public int Error;

            // Segmenter terminals, captured once at the boundary so the cycle graph and any other
            // consumer sees the same instant the adapters billed under.
            public CableNetwork SegInputNet;
            public CableNetwork SegOutputNet;

            // The boundary read.
            public float Demand;                // own-net GetUsedPower semantics (clamped >= 0)
            public float Generated;             // own-net GetGeneratedPower (clamped >= 0)

            // Accumulator drain bookkeeping (decision 26).
            public DemandModel.DebitHome DebitHome;
            public DemandModel.DebitClass DebitClass;
            public float DebitAmount;
        }

        internal sealed class NetRow
        {
            public CableNetwork Network;
            public long Id;
            public readonly List<DeviceRow> Rows = new List<DeviceRow>();

            public float RigidDemand;           // plain-device demand + umbilical quiescent bills
            public float GenSupply;             // plain-device generation (producers)
            public float RequiredSum;           // vanilla-OBSERVE-equivalent Required (segs included)
            public float PotentialSum;          // vanilla-OBSERVE-equivalent Potential (segs included)
            public bool HasNonSegmenterDevice;
            public float WeakestCap;            // CableMax.WeakestCapOnNetwork at build time
            public CableFuse MinFuse;           // lowest-rated fuse on the net (null when none)
            public float MinFuseBreak;
        }

        internal int Tick;
        internal readonly List<NetRow> Nets = new List<NetRow>();
        internal readonly Dictionary<long, NetRow> ById = new Dictionary<long, NetRow>();
        internal readonly List<ElectricalInputOutput> SegmentersSorted = new List<ElectricalInputOutput>();

        /// <summary>Worker-only current snapshot (set by <see cref="Build"/> each tick).</summary>
        internal static GridSnapshot Current { get; private set; }

        internal static void Clear()
        {
            Current = null;
        }

        internal static GridSnapshot Build(int tick)
        {
            var snap = new GridSnapshot { Tick = tick };
            var seenSegs = new HashSet<long>();

            CableNetwork.AllCableNetworks.ForEach(network =>
            {
                try
                {
                if (network == null) return;
                List<Device> devices;
                lock (network.DeviceList)
                {
                    if (network.DeviceList.Count == 0) return;
                    devices = new List<Device>(network.DeviceList);
                }

                var nr = new NetRow { Network = network, Id = network.ReferenceId };

                for (int i = 0; i < devices.Count; i++)
                {
                    var device = devices[i];
                    if (device == null) continue;

                    // Power-port membership: the same predicate the vanilla list rebuild applies.
                    Cable firstPowerCable = null;
                    var powerCables = device.PowerCables;
                    if (powerCables != null)
                    {
                        for (int c = 0; c < powerCables.Length; c++)
                        {
                            var cable = powerCables[c];
                            if (cable != null && cable.CableNetwork == network)
                            {
                                firstPowerCable = cable;
                                break;
                            }
                        }
                    }
                    if (firstPowerCable == null) continue;   // data-only presence: not a power member

                    var row = new DeviceRow
                    {
                        Device = device,
                        RefId = device.ReferenceId,
                        PowerCable = firstPowerCable,
                        DisplayName = ResolveDisplayName(device),
                    };

                    var eio = device as ElectricalInputOutput;
                    row.Eio = eio;
                    row.IsSegmenter = eio != null && SegmentingDeviceRegistry.IsSegmenter(eio);
                    row.IsTransformer = device is Transformer;

                    try
                    {
                        if (!row.IsSegmenter && DemandModel.TryReadSpecial(device, out var special))
                        {
                            row.Demand = DeviceOutputSanitizer.Sanitize(special.Demand, device, generated: false);
                            row.DebitHome = special.Home;
                            row.DebitClass = special.Class;
                            row.DebitAmount = special.DebitAmount;
                            float gen = device.GetGeneratedPower(network);
                            row.Generated = DeviceOutputSanitizer.Sanitize(gen > 0f ? gen : 0f, device, generated: true);
                        }
                        else
                        {
                            float used = device.GetUsedPower(network);
                            float gen = device.GetGeneratedPower(network);
                            row.Demand = DeviceOutputSanitizer.Sanitize(used > 0f ? used : 0f, device, generated: false);
                            row.Generated = DeviceOutputSanitizer.Sanitize(gen > 0f ? gen : 0f, device, generated: true);
                        }
                    }
                    catch
                    {
                        row.Demand = 0f;
                        row.Generated = 0f;
                    }

                    if (row.Demand > 0f) nr.RequiredSum += row.Demand;
                    if (row.Generated > 0f) nr.PotentialSum += row.Generated;

                    if (row.IsSegmenter)
                    {
                        row.OnOff = eio.OnOff;
                        row.Error = eio.Error;
                        row.SegInputNet = eio.InputNetwork;
                        row.SegOutputNet = eio.OutputNetwork;
                        if (seenSegs.Add(eio.ReferenceId)) snap.SegmentersSorted.Add(eio);

                        // The umbilical halves bill their own idle draw on the input network under
                        // vanilla gates the Buffered adapter cannot carry; fund it as plain rigid
                        // demand (POWER.md §7.5, unchanged from the pre-snapshot GATHER).
                        if (device is RocketPowerUmbilical umbilical && network == umbilical.InputNetwork)
                        {
                            float quiescent = Patches.RocketUmbilicalPatches.QuiescentBill(umbilical);
                            if (quiescent > 0f) nr.RigidDemand += quiescent;
                        }
                    }
                    else
                    {
                        nr.HasNonSegmenterDevice = true;
                        nr.RigidDemand += row.Demand;
                        nr.GenSupply += row.Generated;

                        row.IsProducerClass = ProducerClassifier.IsProducer(device);
                        row.IsActiveProducer = row.IsProducerClass && ProducerClassifier.IsActiveProducer(device);
                        row.UnknownProducerLike = !row.IsProducerClass && row.Generated > 0f;
                        row.OnOff = device.OnOff;
                        // Delivery-effect classification (the write-back shim re-delivers these
                        // rows' granted power through ReceivePower). Segmenter rows never reach
                        // this branch, so a segmenter can never be classified even by config.
                        row.DeliveryEffect = DeliveryEffectClassifier.IsDeliveryEffect(device);
                    }

                    nr.Rows.Add(row);
                }

                if (nr.Rows.Count == 0) return;

                lock (network.FuseList)
                {
                    for (int f = 0; f < network.FuseList.Count; f++)
                    {
                        var fuse = network.FuseList[f];
                        if (fuse == null) continue;
                        if (nr.MinFuse == null || fuse.PowerBreak < nr.MinFuseBreak)
                        {
                            nr.MinFuse = fuse;
                            nr.MinFuseBreak = fuse.PowerBreak;
                        }
                    }
                }

                nr.WeakestCap = CableMax.WeakestCapOnNetwork(network);

                snap.Nets.Add(nr);
                snap.ById[nr.Id] = nr;
                }
                catch (System.Exception ex)
                {
                    // One malformed net (a throwing third-party override the per-device guard did
                    // not cover) costs that net's rows, never the whole snapshot.
                    Plugin.Log?.LogWarning(
                        "[PowerGridPlus] Grid snapshot skipped a network: " + ex.Message);
                }
            });

            snap.SegmentersSorted.Sort((a, b) => a.ReferenceId.CompareTo(b.ReferenceId));
            Current = snap;
            return snap;
        }

        // Category prefixes stripped from a prefab id before spacing the PascalCase remainder.
        private static readonly string[] _prefabPrefixes = { "Structure", "Appliance", "Dynamic", "Item" };

        // Cache of prefab id -> pretty name. Prefab ids are a small fixed set, so the string work
        // runs once per distinct prefab; ConcurrentDictionary keeps it worker-safe across ticks.
        private static readonly ConcurrentDictionary<string, string> _prettyNameCache =
            new ConcurrentDictionary<string, string>();

        // Human-readable device name for the fault-hover violator list, resolved from the PREFAB id
        // (never the player rename). Captured here at snapshot BUILD time because the consumer
        // (CurrentMismatchFaultDetector.BuildViolatorNames) runs later in the same tick and must not
        // re-read live device state. This runs on the power worker, not the Unity main thread
        // (AtomicElectricityTickPatch runs the whole pipeline on the UniTask worker), so the source
        // must be worker-safe: PrefabName is a plain managed string field, immutable after prefab
        // registration, and is listed among the safe off-thread reads in
        // Research/Patterns/ThingEnumerationOffMainThread.md. The localized name would need
        // Localization.GetThingName (which calls the Unity utility Animator.StringToHash, off-thread
        // safety unconfirmed), so we prettify the prefab id ourselves instead. GetType().Name is
        // reflection metadata and is likewise worker-safe as the empty-prefab fallback.
        private static string ResolveDisplayName(Device device)
        {
            string prefab = device.PrefabName;
            if (string.IsNullOrEmpty(prefab)) return device.GetType().Name;
            return _prettyNameCache.GetOrAdd(prefab, PrettifyPrefabName);
        }

        // "StructureWallCooler" -> "Wall Cooler": strip a leading category prefix, then insert a
        // space before each uppercase letter that follows a lowercase one. Acronyms ("LED", "APC")
        // and digit runs are left intact; only lower-to-upper transitions split.
        private static string PrettifyPrefabName(string prefabName)
        {
            string s = prefabName;
            for (int p = 0; p < _prefabPrefixes.Length; p++)
            {
                string prefix = _prefabPrefixes[p];
                if (s.Length > prefix.Length && s.StartsWith(prefix, System.StringComparison.Ordinal))
                {
                    s = s.Substring(prefix.Length);
                    break;
                }
            }
            var sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (i > 0 && char.IsUpper(c) && char.IsLower(s[i - 1]))
                    sb.Append(' ');
                sb.Append(c);
            }
            string result = sb.ToString();
            return string.IsNullOrEmpty(result) ? prefabName : result;
        }

        /// <summary>
        ///     Applied after PROTECT when producer-isolation newly locked producers this tick: their
        ///     supply leaves the table in place, so the allocator solves the corrected grid without a
        ///     second observation pass (the old OBSERVE/re-observe pair collapses into this).
        /// </summary>
        internal void ZeroFaultedProducers(int tick)
        {
            for (int n = 0; n < Nets.Count; n++)
            {
                var nr = Nets[n];
                for (int i = 0; i < nr.Rows.Count; i++)
                {
                    var row = nr.Rows[i];
                    if (row.IsSegmenter || row.Generated <= 0f) continue;
                    if (!CurrentMismatchFaultRegistry.IsLockedOut(row.RefId, tick)) continue;
                    nr.GenSupply -= row.Generated;
                    if (nr.GenSupply < 0f) nr.GenSupply = 0f;
                    nr.PotentialSum -= row.Generated;
                    if (nr.PotentialSum < 0f) nr.PotentialSum = 0f;
                    row.Generated = 0f;
                }
            }
        }
    }
}
