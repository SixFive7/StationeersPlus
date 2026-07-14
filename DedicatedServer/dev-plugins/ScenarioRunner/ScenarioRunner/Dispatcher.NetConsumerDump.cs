using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;

namespace ScenarioRunner
{
    // Scenario: pgp-net-consumer-dump
    //
    // Answers "which consumers does the PowerGridPlus allocator actually count behind this
    // transformer pair": for the subtree under the hardcoded target transformer's OUTPUT
    // network, every device whose rigid demand feeds the structural-overload sum, with prefab
    // names and draws, exactly as the allocator saw them on the dumped tick.
    //
    // Data source is the mod's own per-tick model, PowerGridPlus.Core.GridSnapshot.Current,
    // read via reflection (ScenarioRunner keeps no build-time PGP dependency). The snapshot is
    // built once at the top of every atomic power tick and DeviceRow.Demand IS the allocator's
    // single boundary read (one GetUsedPower sample per device per tick), so this scenario
    // never calls GetUsedPower itself: a re-sample could disagree with what the allocator
    // billed. NetRow.RigidDemand sums only NON-segmenter rows (plus umbilical quiescent
    // bills); a segmenter row's own Demand is not part of it, which is why segmenter rows are
    // tagged SEG in the per-device lines.
    //
    // Walk: start at the target's OUTPUT net's NetRow. Log the net header (RigidDemand /
    // GenSupply) and one line per row with Demand > 0 or Generated > 0. Then find every
    // segmenter row in the snapshot whose SegInputNet is the current net (a downstream bridge
    // drawing from here), log it, and recurse into its SegOutputNet (visited set, depth cap
    // NCD_MAX_DEPTH). For the target's output net only, one extra line counts the zero-demand
    // (idle) devices so idle vs drawing is visible at a glance.
    //
    // Threading: the scenario pump is a postfix on the same worker that runs the power tick,
    // so GridSnapshot.Current here is the just-completed tick's model, same-thread and
    // race-free. Everything read is managed state (snapshot fields, PrefabName, ReferenceId,
    // network reference ids). DisplayName may touch Unity/localization, so it is per-device
    // guarded and degrades to PrefabName off-thread (Dispatcher.DevicePortDump.cs precedent).
    //
    // One-shot; fires once, NCD_SETTLE_TICKS after the configured scenario delay. Target ids
    // are hardcoded -- edit `_ncdTargetRef` / `_ncdSiblingRef` and rebuild (the
    // pgp-power-flow-diagnose convention). Requires PowerGridPlus loaded; otherwise warns and
    // no-ops.
    internal static partial class Dispatcher
    {
        private static bool _ncdFired;
        private const int NCD_SETTLE_TICKS = 10;
        private const int NCD_MAX_DEPTH = 8;
        private const int NCD_IDLE_PREFAB_CAP = 15;

        // The transformer pair behind the structural-overload verdict under investigation.
        private const long _ncdTargetRef = 511092L;   // the walk starts at THIS device's output net
        private const long _ncdSiblingRef = 511091L;  // one-line summary + pairSetting only

        // Reflection view over PowerGridPlus.Core.GridSnapshot (+NetRow, +DeviceRow), plus the
        // indexes the walk needs (net id -> NetRow object, deduped segmenter roster).
        private sealed class NcdView
        {
            public FieldInfo NrId, NrRows, NrRigid, NrGen;
            public FieldInfo DrDevice, DrRefId, DrOnOff, DrDemand, DrGenerated,
                DrIsSegmenter, DrSegIn, DrSegOut, DrIsTransformer, DrIsProducerClass;
            public readonly Dictionary<long, object> NetById = new Dictionary<long, object>();
            public readonly List<NcdSeg> Segmenters = new List<NcdSeg>();

            public bool RowFieldsOk =>
                NrId != null && NrRows != null && NrRigid != null && NrGen != null
                && DrDevice != null && DrRefId != null && DrOnOff != null && DrDemand != null
                && DrGenerated != null && DrIsSegmenter != null && DrSegIn != null
                && DrSegOut != null && DrIsTransformer != null && DrIsProducerClass != null;
        }

        // One segmenter, deduped across its (up to two) membership rows. A segmenter is a
        // member of both its nets, so the snapshot holds a row for it on each; the input-side
        // row is preferred because its Demand (own-net GetUsedPower semantics) is the draw the
        // bridge desires FROM the net it hangs downstream of.
        private sealed class NcdSeg
        {
            public long RefId;
            public string Prefab;
            public bool OnOff;
            public float Demand;
            public float Generated;
            public long InNetId;        // 0 = none
            public long OutNetId;       // 0 = none
            public bool FromInputRow;   // the kept row was the membership row on its own input net
        }

        private static void Scenario_PgpNetConsumerDump()
        {
            if (_ncdFired) return;
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-net-consumer-dump")) return;
            if (_ticksSeen < _delayTicks + NCD_SETTLE_TICKS) return;
            _ncdFired = true;

            try
            {
                _log?.LogInfo($"[ScenarioRunner] NCD START net-consumer-dump target ref={_ncdTargetRef} sibling ref={_ncdSiblingRef}");

                // ---- 1. Resolve the target + sibling Things ----
                Thing target = null, sibling = null;
                OcclusionManager.AllThings.ForEach(t =>
                {
                    if (t == null) return;
                    if (t.ReferenceId == _ncdTargetRef) target = t;
                    else if (t.ReferenceId == _ncdSiblingRef) sibling = t;
                });
                NcdLogThingSummary("TARGET", _ncdTargetRef, target);
                NcdLogThingSummary("SIBLING", _ncdSiblingRef, sibling);

                var targetEio = target as ElectricalInputOutput;
                if (targetEio == null)
                {
                    _log?.LogError(
                        $"[ScenarioRunner] NCD target ref={_ncdTargetRef} is not an ElectricalInputOutput " +
                        $"(found: {(target == null ? "nothing" : target.GetType().FullName)}); cannot resolve an output net. END");
                    return;
                }
                var outNet = targetEio.OutputNetwork;
                if (outNet == null)
                {
                    _log?.LogError("[ScenarioRunner] NCD target has no OutputNetwork; nothing to walk. END");
                    return;
                }
                long outNetId = outNet.ReferenceId;

                // ---- 2. Reflect the mod's per-tick snapshot ----
                var asm = GetModAssembly(PGP_ASSEMBLY);
                var snapT = asm.GetType("PowerGridPlus.Core.GridSnapshot");
                var netRowT = asm.GetType("PowerGridPlus.Core.GridSnapshot+NetRow");
                var devRowT = asm.GetType("PowerGridPlus.Core.GridSnapshot+DeviceRow");
                const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var currentProp = snapT?.GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var tickF = snapT?.GetField("Tick", I);
                var netsF = snapT?.GetField("Nets", I);

                var v = new NcdView
                {
                    NrId = netRowT?.GetField("Id", I),
                    NrRows = netRowT?.GetField("Rows", I),
                    NrRigid = netRowT?.GetField("RigidDemand", I),
                    NrGen = netRowT?.GetField("GenSupply", I),
                    DrDevice = devRowT?.GetField("Device", I),
                    DrRefId = devRowT?.GetField("RefId", I),
                    DrOnOff = devRowT?.GetField("OnOff", I),
                    DrDemand = devRowT?.GetField("Demand", I),
                    DrGenerated = devRowT?.GetField("Generated", I),
                    DrIsSegmenter = devRowT?.GetField("IsSegmenter", I),
                    DrSegIn = devRowT?.GetField("SegInputNet", I),
                    DrSegOut = devRowT?.GetField("SegOutputNet", I),
                    DrIsTransformer = devRowT?.GetField("IsTransformer", I),
                    DrIsProducerClass = devRowT?.GetField("IsProducerClass", I),
                };

                _log?.LogInfo(
                    "[ScenarioRunner] NCD reflection: " +
                    $"GridSnapshot={snapT != null} NetRow={netRowT != null} DeviceRow={devRowT != null} " +
                    $"Current={currentProp != null} Tick={tickF != null} Nets={netsF != null} rowFields={v.RowFieldsOk}");

                if (currentProp == null || netsF == null || !v.RowFieldsOk)
                {
                    _log?.LogError("[ScenarioRunner] NCD reflection incomplete; the GridSnapshot member layout changed. END");
                    return;
                }

                object snap = currentProp.GetValue(null);
                if (snap == null)
                {
                    _log?.LogError("[ScenarioRunner] NCD GridSnapshot.Current is null (power tick not run yet, or the mod cleared it). END");
                    return;
                }

                int snapTick = tickF != null ? (int)tickF.GetValue(snap) : -1;
                NcdIndexSnapshot(v, netsF.GetValue(snap) as IEnumerable);
                _log?.LogInfo(
                    $"[ScenarioRunner] NCD SNAPSHOT tick={snapTick} nets={v.NetById.Count} " +
                    $"uniqueSegmenters={v.Segmenters.Count} startNet={outNetId} (target OUTPUT net)");

                // ---- 3. Recursive subtree walk ----
                var visited = new HashSet<long>();
                int netCount = 0;
                float rigidSum = 0f;
                NcdWalkNet(v, outNetId, 0, visited, ref netCount, ref rigidSum, outNetId);

                // ---- 4. Totals ----
                var txTarget = target as Transformer;
                var txSibling = sibling as Transformer;
                string pairSetting =
                    txTarget != null && txSibling != null ? (txTarget.Setting + txSibling.Setting).ToString("F1")
                    : txTarget != null ? $"{txTarget.Setting:F1}(target-only)"
                    : txSibling != null ? $"{txSibling.Setting:F1}(sibling-only)"
                    : "n/a";
                _log?.LogInfo(
                    $"[ScenarioRunner] NCD SUMMARY subtreeNets={netCount} totalRigidDemand={rigidSum:F1} pairSetting={pairSetting} " +
                    "note=the structural rule compares the OUTPUT net's rigid demand plus downstream bridges' rigid desires " +
                    "against the pair's combined effective capacity");
                _log?.LogInfo("[ScenarioRunner] NCD END");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] NCD threw: {e}");
            }
        }

        // ---- pgp-net-consumer-dump helpers ----

        // One-line summary of a pair member: type, prefab, OnOff, Setting, both power nets.
        private static void NcdLogThingSummary(string tag, long wantedRef, Thing t)
        {
            try
            {
                if (t == null)
                {
                    _log?.LogWarning($"[ScenarioRunner] NCD {tag} ref={wantedRef} NOT FOUND in OcclusionManager.AllThings");
                    return;
                }
                var dev = t as Device;
                var eio = t as ElectricalInputOutput;
                var xfmr = t as Transformer;
                string inId = eio?.InputNetwork != null ? eio.InputNetwork.ReferenceId.ToString() : "none";
                string outId = eio?.OutputNetwork != null ? eio.OutputNetwork.ReferenceId.ToString() : "none";
                string setting = xfmr != null ? xfmr.Setting.ToString("F1") : "n/a";
                string outMax = xfmr != null ? xfmr.OutputMaximum.ToString("F1") : "n/a";
                _log?.LogInfo(
                    $"[ScenarioRunner] NCD {tag} ref={t.ReferenceId} type={t.GetType().Name} prefab={t.PrefabName} " +
                    $"OnOff={(dev != null ? dev.OnOff.ToString() : "n/a")} Setting={setting} OutputMaximum={outMax} " +
                    $"in={inId} out={outId}");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] NCD {tag} summary threw: {e}");
            }
        }

        // Index the snapshot: net id -> NetRow object, plus a deduped segmenter roster (a
        // segmenter has a membership row on each of its nets; keep the input-side row's values
        // when both exist, see NcdSeg).
        private static void NcdIndexSnapshot(NcdView v, IEnumerable nets)
        {
            if (nets == null) return;
            var segByRef = new Dictionary<long, NcdSeg>();
            foreach (var nr in nets)
            {
                if (nr == null) continue;
                long netId = (long)v.NrId.GetValue(nr);
                v.NetById[netId] = nr;

                if (!(v.NrRows.GetValue(nr) is IEnumerable rows)) continue;
                foreach (var row in rows)
                {
                    if (row == null) continue;
                    if (!(bool)v.DrIsSegmenter.GetValue(row)) continue;

                    long refId = (long)v.DrRefId.GetValue(row);
                    var segIn = v.DrSegIn.GetValue(row) as CableNetwork;
                    var segOut = v.DrSegOut.GetValue(row) as CableNetwork;
                    long inId = segIn != null ? segIn.ReferenceId : 0L;
                    long outId = segOut != null ? segOut.ReferenceId : 0L;
                    bool fromInputRow = inId != 0L && inId == netId;

                    if (segByRef.TryGetValue(refId, out var existing) && (existing.FromInputRow || !fromInputRow))
                        continue;

                    var dev = v.DrDevice.GetValue(row) as Device;
                    segByRef[refId] = new NcdSeg
                    {
                        RefId = refId,
                        Prefab = dev != null ? (dev.PrefabName ?? dev.GetType().Name) : "<null-device>",
                        OnOff = (bool)v.DrOnOff.GetValue(row),
                        Demand = (float)v.DrDemand.GetValue(row),
                        Generated = (float)v.DrGenerated.GetValue(row),
                        InNetId = inId,
                        OutNetId = outId,
                        FromInputRow = fromInputRow,
                    };
                }
            }
            v.Segmenters.AddRange(segByRef.Values);
            v.Segmenters.Sort((a, b) => a.RefId.CompareTo(b.RefId));
        }

        // Dump one net's rows, then recurse into every downstream bridge (segmenter whose
        // SegInputNet is this net). idleDetailNetId gets the one-line idle-device count.
        private static void NcdWalkNet(NcdView v, long netId, int depth, HashSet<long> visited,
            ref int netCount, ref float rigidSum, long idleDetailNetId)
        {
            if (visited.Contains(netId)) return;
            if (depth > NCD_MAX_DEPTH)
            {
                _log?.LogWarning($"[ScenarioRunner] NCD depth cap {NCD_MAX_DEPTH} hit at net {netId}; subtree below is NOT counted.");
                return;
            }
            visited.Add(netId);

            if (!v.NetById.TryGetValue(netId, out var nr))
            {
                _log?.LogInfo(
                    $"[ScenarioRunner] NCD NET {netId} depth={depth} rigidDemand=n/a genSupply=n/a " +
                    "(net not in snapshot: no power members this tick)");
            }
            else
            {
                netCount++;
                float rigid = (float)v.NrRigid.GetValue(nr);
                float gen = (float)v.NrGen.GetValue(nr);
                rigidSum += rigid;

                var rowObjs = new List<object>();
                if (v.NrRows.GetValue(nr) is IEnumerable rows)
                    foreach (var r in rows)
                        if (r != null) rowObjs.Add(r);

                _log?.LogInfo(
                    $"[ScenarioRunner] NCD NET {netId} depth={depth} rigidDemand={rigid:F1} genSupply={gen:F1} rows={rowObjs.Count}");

                int idle = 0;
                var idlePrefabs = new Dictionary<string, int>();
                foreach (var row in rowObjs)
                {
                    float demand = (float)v.DrDemand.GetValue(row);
                    float generated = (float)v.DrGenerated.GetValue(row);
                    var dev = v.DrDevice.GetValue(row) as Device;
                    string prefab = dev != null ? (dev.PrefabName ?? dev.GetType().Name) : "<null-device>";

                    if (demand <= 0f && generated <= 0f)
                    {
                        idle++;
                        idlePrefabs.TryGetValue(prefab, out int n);
                        idlePrefabs[prefab] = n + 1;
                        continue;
                    }

                    long refId = (long)v.DrRefId.GetValue(row);
                    bool onOff = (bool)v.DrOnOff.GetValue(row);
                    bool isSeg = (bool)v.DrIsSegmenter.GetValue(row);
                    bool isXfmr = (bool)v.DrIsTransformer.GetValue(row);
                    bool isProd = (bool)v.DrIsProducerClass.GetValue(row);

                    string display = "";
                    try
                    {
                        // DisplayName may touch Unity/localization; degrade to PrefabName
                        // off-thread (Dispatcher.DevicePortDump.cs precedent).
                        var dn = dev?.DisplayName;
                        if (!string.IsNullOrEmpty(dn) && dn != prefab) display = $" \"{dn}\"";
                    }
                    catch { }

                    string seg = "";
                    if (isSeg)
                    {
                        var segIn = v.DrSegIn.GetValue(row) as CableNetwork;
                        var segOut = v.DrSegOut.GetValue(row) as CableNetwork;
                        seg = $" SEG in={(segIn != null ? segIn.ReferenceId.ToString() : "none")}" +
                              $" out={(segOut != null ? segOut.ReferenceId.ToString() : "none")}";
                    }
                    string marks = (isXfmr ? " XFMR" : "") + (isProd ? " PRODUCER" : "");

                    _log?.LogInfo(
                        $"[ScenarioRunner] NCD   {prefab}{display} ref={refId} OnOff={onOff} " +
                        $"demand={demand:F1} gen={generated:F1}{seg}{marks}");
                }

                if (netId == idleDetailNetId)
                {
                    string breakdown = "";
                    if (idle > 0)
                    {
                        var parts = idlePrefabs
                            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
                            .Take(NCD_IDLE_PREFAB_CAP)
                            .Select(kv => $"{kv.Key} x{kv.Value}");
                        string more = idlePrefabs.Count > NCD_IDLE_PREFAB_CAP
                            ? $", +{idlePrefabs.Count - NCD_IDLE_PREFAB_CAP} more prefabs"
                            : "";
                        breakdown = " [" + string.Join(", ", parts) + more + "]";
                    }
                    _log?.LogInfo($"[ScenarioRunner] NCD IDLE net={netId} zeroDemandDevices={idle}{breakdown}");
                }
            }

            // Downstream bridges: every segmenter drawing FROM this net; recurse into its
            // output side. Runs even when the net had no NetRow (finds nothing there by
            // construction: a segmenter membership row would have given the net a NetRow).
            foreach (var s in v.Segmenters)
            {
                if (s.InNetId != netId) continue;
                string outTok = s.OutNetId != 0L ? s.OutNetId.ToString() : "none";
                _log?.LogInfo(
                    $"[ScenarioRunner] NCD BRIDGE from net {netId}: {s.Prefab} ref={s.RefId} OnOff={s.OnOff} " +
                    $"demandAtInput={s.Demand:F1} gen={s.Generated:F1} segIn={s.InNetId} segOut={outTok}" +
                    (s.FromInputRow ? "" : " (values from a non-input membership row)"));
                if (s.OutNetId != 0L)
                    NcdWalkNet(v, s.OutNetId, depth + 1, visited, ref netCount, ref rigidSum, idleDetailNetId);
            }
        }
    }
}
