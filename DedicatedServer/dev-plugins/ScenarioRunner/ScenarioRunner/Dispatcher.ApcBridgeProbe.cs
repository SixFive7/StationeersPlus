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

namespace ScenarioRunner
{
    // Scenario: pgp-apc-bridge-probe
    //
    // Diagnoses, on a live save, why logic passthrough across APCs (and possibly small
    // transformers) is not working: the reported failure is airlock IC10s that can no longer
    // reach their force-field doors across an APC. Separates three hypotheses:
    //
    //   (H1) The merge machinery or the APC default gate is broken: even a forced dirty +
    //        refresh does not produce a merged data-device list.
    //   (H2) The machinery works but the merged DataDeviceLists are STALE. The 2026-07-12
    //        rebuild removed the only every-tick reader of CableNetwork.PowerDeviceList
    //        (vanilla PowerTick.Initialise). That getter tests
    //        `PowerDeviceListDirty || DataDeviceListDirty` and its call to
    //        RefreshPowerAndDataDeviceLists was what let the mod's merge postfix (which only
    //        merges when DataDeviceListDirty was true at entry) run within a tick of any
    //        change. With no per-tick reader, a dirtied net stays dirty and its cached
    //        _dataDeviceList stays stale until some other consumer happens to read a property.
    //   (H3) The circuits / force fields are simply unpowered (the base is power-starved);
    //        merge state is irrelevant because the housings are dark.
    //
    // Every survey read goes through the RAW private fields (_dataDeviceList,
    // PowerDeviceListDirty, DataDeviceListDirty; verified against the 0.2.6403.27689 decompile
    // and Research/GameClasses/CableNetwork.md) so the probe itself never triggers the lazy
    // refresh and never masks the staleness it is hunting. CableNetwork.DeviceList is a plain
    // public readonly field (no refresh on read), so reading it is also side-effect free.
    //
    // THE DECISIVE HEAL TEST is the one intentional side effect: on the first enabled bridge
    // whose merge verdict is false in a witnessable direction, it (a) logs the before state,
    // (b) calls PowerGridPlus's PassthroughTopology.DirtyBridgeNetworks(device) via reflection
    // (sets BOTH dirty flags on all the bridge's nets), (c) reads the DataDeviceList PROPERTY
    // on the input and output nets (the lazy-refresh trigger: the getter tests the power flag,
    // which DirtyBridgeNetworks just set, so the vanilla refresh plus the mod's merge postfix
    // must run right here), and (d) re-checks the merge verdict from the raw field.
    // Heal flips the verdict to true -> H2 proven (machinery fine, refresh cadence gone).
    // Heal stays false -> H1 indicated (machinery or gate broken).
    //
    // Merge verdict definition: the merge postfix (LogicPassthroughPatches) copies each
    // reachable net's DeviceList entries into the local _dataDeviceList. So "mergeInToOut" is
    // true iff the input-side net's RAW _dataDeviceList contains at least one far-side witness:
    // a Device physically in the output net's DeviceList, excluding the bridge itself and any
    // device that is also physically on the input net (a both-sides device proves nothing).
    // "Witnessable" means such a far-side device exists at all; a bridge whose far side holds
    // nothing but the bridge can never show a merge and is excluded from the merged/stale
    // buckets rather than counted as broken.
    //
    // Threading: runs on the ElectricityTick postfix worker. Managed field reads, HashSet math,
    // and calling DirtyBridgeNetworks / the DataDeviceList getter there match what vanilla and
    // the mod already do on that thread. No Unity API is touched (Thing identity comparisons
    // follow the same pattern the other scenarios use).
    //
    // One-shot; fires once, APCB_SETTLE_TICKS after the configured scenario delay.
    // Requires PowerGridPlus loaded; otherwise warns and no-ops.
    internal static partial class Dispatcher
    {
        private static bool _apcbFired;
        private const int APCB_SETTLE_TICKS = 10;
        private const int APCB_APC_LOG_CAP = 25;
        private const int APCB_XFMR_LOG_CAP = 15;
        private const int APCB_FF_LOG_CAP = 25;
        private const int APCB_HOUSING_LOG_CAP = 10;

        private sealed class ApcbBridgeReport
        {
            public Device Dev;
            public CableNetwork InNet;
            public CableNetwork OutNet;
            public int Mode = -1;          // -1 = reflection unresolved / threw
            public bool? Bridge;           // null = reflection unresolved / threw
            public bool WitnessInToOut;
            public bool WitnessOutToIn;
            public bool MergeInToOut;
            public bool MergeOutToIn;

            public bool On => Bridge ?? (Mode == 1);
            public bool Witnessable => WitnessInToOut || WitnessOutToIn;
            public bool Stale => (WitnessInToOut && !MergeInToOut) || (WitnessOutToIn && !MergeOutToIn);
            public bool Merged => Witnessable && !Stale;
        }

        private static void Scenario_PgpApcBridgeProbe()
        {
            if (_apcbFired) return;
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-apc-bridge-probe")) return;
            if (_ticksSeen < _delayTicks + APCB_SETTLE_TICKS) return;
            _apcbFired = true;

            try
            {
                _log?.LogInfo("[ScenarioRunner] APCB START apc-bridge-probe");

                var asm = GetModAssembly(PGP_ASSEMBLY);
                const BindingFlags SF = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

                var syncT = asm.GetType("PowerGridPlus.PassthroughDefaultsSync");
                var settingsT = asm.GetType("PowerGridPlus.Settings");
                var storeT = asm.GetType("PowerGridPlus.PassthroughModeStore");
                var topoT = asm.GetType("PowerGridPlus.Patches.PassthroughTopology");

                var effApc = syncT?.GetProperty("EffectiveApc", SF);
                var effSmall = syncT?.GetProperty("EffectiveSmallTransformer", SF);
                var getMode = storeT?.GetMethod("GetMode", SF, null, new[] { typeof(Thing) }, null);
                var isEnabledBridge = topoT?.GetMethod("IsEnabledBridge", SF, null, new[] { typeof(Device) }, null);
                var dirtyBridge = topoT?.GetMethod("DirtyBridgeNetworks", SF, null, new[] { typeof(Device) }, null);

                // Game-side private fields (names verified in the 0.2.6403.27689 decompile:
                // protected readonly List<Device> _dataDeviceList; protected bool PowerDeviceListDirty;
                // protected bool DataDeviceListDirty).
                var dataListField = AccessTools.Field(typeof(CableNetwork), "_dataDeviceList");
                var powerDirtyField = AccessTools.Field(typeof(CableNetwork), "PowerDeviceListDirty");
                var dataDirtyField = AccessTools.Field(typeof(CableNetwork), "DataDeviceListDirty");

                _log?.LogInfo(
                    "[ScenarioRunner] APCB reflection: " +
                    $"EffectiveApc={effApc != null} EffectiveSmallTransformer={effSmall != null} " +
                    $"getMode={getMode != null} isEnabledBridge={isEnabledBridge != null} dirtyBridge={dirtyBridge != null} " +
                    $"_dataDeviceList={dataListField != null} PowerDeviceListDirty={powerDirtyField != null} " +
                    $"DataDeviceListDirty={dataDirtyField != null}");

                // ---- 1. Config / gate state ----
                _log?.LogInfo(
                    "[ScenarioRunner] APCB CONFIG " +
                    $"PassthroughDefaultsSync.EffectiveApc={ApcbStaticBool(effApc)} " +
                    $"PassthroughDefaultsSync.EffectiveSmallTransformer={ApcbStaticBool(effSmall)} " +
                    $"Settings.ApcPassthroughDefault.Value={ApcbConfigBool(settingsT, "ApcPassthroughDefault")} " +
                    $"Settings.SmallTransformerPassthroughDefault.Value={ApcbConfigBool(settingsT, "SmallTransformerPassthroughDefault")}");

                _log?.LogInfo(
                    "[ScenarioRunner] APCB criteria: merge verdicts read the RAW _dataDeviceList field (never the " +
                    "property, so the probe cannot trigger the refresh it is hunting) and look for a far-side witness " +
                    "(a Device physically in the opposite net's DeviceList, not the bridge, not on the near net). " +
                    "merged = every witnessable direction true; stale = a witnessable direction false; a bridge with " +
                    "no witnessable direction counts in neither bucket.");

                // ---- 2. Per-APC survey ----
                // AreaPowerControl has no AllAreaPowerControllers static in 0.2.6403.27689 (checked);
                // walk OcclusionManager.AllThings via the shared caches like every other scenario.
                RebuildCaches();
                var apcs = _apcs.Where(a => a != null).OrderBy(a => a.ReferenceId).ToList();

                int mode1 = 0, mergedCount = 0, staleCount = 0;
                ApcbBridgeReport healTarget = null;
                string healKind = null;

                var apcReports = new List<ApcbBridgeReport>(apcs.Count);
                foreach (var apc in apcs)
                {
                    var r = ApcbSurvey(apc, apc.InputNetwork, apc.OutputNetwork, getMode, isEnabledBridge, dataListField);
                    apcReports.Add(r);
                    if (!r.On) continue;
                    mode1++;
                    if (r.Merged) mergedCount++;
                    if (r.Stale) staleCount++;
                    if (healTarget == null && r.Bridge == true && r.Stale) { healTarget = r; healKind = "APC"; }
                }

                for (int i = 0; i < apcReports.Count && i < APCB_APC_LOG_CAP; i++)
                    ApcbLogBridgeLine("APC", apcReports[i], powerDirtyField, dataDirtyField, dataListField);
                if (apcReports.Count > APCB_APC_LOG_CAP)
                    _log?.LogInfo($"[ScenarioRunner] APCB APC lines capped at {APCB_APC_LOG_CAP}; {apcReports.Count - APCB_APC_LOG_CAP} more surveyed (counted in totals).");
                _log?.LogInfo($"[ScenarioRunner] APCB APC TOTALS apcs={apcReports.Count} mode1={mode1} merged={mergedCount} stale={staleCount}");

                // ---- 3. Small transformer survey ----
                var smalls = _transformers.Where(t => t != null
                        && (t.PrefabName == "StructureTransformerSmall" || t.PrefabName == "StructureTransformerSmallReversed"))
                    .OrderBy(t => t.ReferenceId).ToList();

                int xfmrMode1 = 0, xfmrMerged = 0, xfmrStale = 0;
                var xfmrReports = new List<ApcbBridgeReport>(smalls.Count);
                foreach (var t in smalls)
                {
                    var r = ApcbSurvey(t, t.InputNetwork, t.OutputNetwork, getMode, isEnabledBridge, dataListField);
                    xfmrReports.Add(r);
                    if (!r.On) continue;
                    xfmrMode1++;
                    if (r.Merged) xfmrMerged++;
                    if (r.Stale) xfmrStale++;
                    // Fallback heal target only: the primary pick is an APC (the reported failure site).
                    if (healTarget == null && r.Bridge == true && r.Stale) { healTarget = r; healKind = "XFMR-fallback"; }
                }

                for (int i = 0; i < xfmrReports.Count && i < APCB_XFMR_LOG_CAP; i++)
                    ApcbLogBridgeLine("XFMR", xfmrReports[i], powerDirtyField, dataDirtyField, dataListField);
                if (xfmrReports.Count > APCB_XFMR_LOG_CAP)
                    _log?.LogInfo($"[ScenarioRunner] APCB XFMR lines capped at {APCB_XFMR_LOG_CAP}; {xfmrReports.Count - APCB_XFMR_LOG_CAP} more surveyed (counted in totals).");
                _log?.LogInfo($"[ScenarioRunner] APCB XFMR TOTALS smallTransformers={xfmrReports.Count} mode1={xfmrMode1} merged={xfmrMerged} stale={xfmrStale}");

                // ---- 4. Force-field + circuit-housing survey (separates H3) ----
                var forceFields = new List<Thing>();
                var housings = new List<CircuitHousing>();
                OcclusionManager.AllThings.ForEach(t =>
                {
                    if (t == null) return;
                    if (t.GetType().Name.IndexOf("ForceField", StringComparison.Ordinal) >= 0) forceFields.Add(t);
                    if (t is CircuitHousing ch) housings.Add(ch);
                });
                forceFields.Sort((a, b) => a.ReferenceId.CompareTo(b.ReferenceId));
                housings.Sort((a, b) => a.ReferenceId.CompareTo(b.ReferenceId));

                var ffSet = new HashSet<Thing>(forceFields);
                int ffPowered = 0;
                int ffLogged = 0;
                foreach (var ff in forceFields)
                {
                    var d = ff as Device;
                    if (d != null && d.Powered) ffPowered++;
                    if (ffLogged >= APCB_FF_LOG_CAP) continue;
                    ffLogged++;
                    if (d != null)
                    {
                        string powerNet = d.PowerCableNetwork != null ? d.PowerCableNetwork.ReferenceId.ToString() : "none";
                        _log?.LogInfo(
                            $"[ScenarioRunner] APCB FF type={ff.GetType().Name} ref={ff.ReferenceId} " +
                            $"Powered={d.Powered} OnOff={d.OnOff} powerNet={powerNet}");
                    }
                    else
                    {
                        _log?.LogInfo(
                            $"[ScenarioRunner] APCB FF type={ff.GetType().Name} ref={ff.ReferenceId} " +
                            "Powered=n/a OnOff=n/a powerNet=n/a (not a Device)");
                    }
                }
                if (forceFields.Count > APCB_FF_LOG_CAP)
                    _log?.LogInfo($"[ScenarioRunner] APCB FF lines capped at {APCB_FF_LOG_CAP}; {forceFields.Count - APCB_FF_LOG_CAP} more counted.");
                if (forceFields.Count == 0)
                    _log?.LogInfo("[ScenarioRunner] APCB FF none: no Thing in the scene has 'ForceField' in its type name.");

                int housingsDark = 0;
                foreach (var ch in housings)
                    if (!ch.Powered) housingsDark++;

                _log?.LogInfo($"[ScenarioRunner] APCB HOUSING count={housings.Count} (CircuitHousing incl. subclasses) dark={housingsDark}");
                for (int i = 0; i < housings.Count && i < APCB_HOUSING_LOG_CAP; i++)
                {
                    var ch = housings[i];
                    var dataNet = ch.DataCableNetwork;
                    string ffSeen = "n/a";
                    if (dataNet != null && dataListField != null)
                    {
                        try
                        {
                            var raw = dataListField.GetValue(dataNet) as List<Device>;
                            bool seen = false;
                            if (raw != null)
                            {
                                for (int j = 0; j < raw.Count; j++)
                                {
                                    var rd = raw[j];
                                    if (rd != null && ffSet.Contains(rd)) { seen = true; break; }
                                }
                            }
                            ffSeen = seen.ToString();
                        }
                        catch (Exception e) { ffSeen = "err:" + e.GetBaseException().GetType().Name; }
                    }
                    _log?.LogInfo(
                        $"[ScenarioRunner] APCB HOUSING ref={ch.ReferenceId} Powered={ch.Powered} OnOff={ch.OnOff} " +
                        $"dataNet={(dataNet != null ? dataNet.ReferenceId.ToString() : "none")} ffInRawDataList={ffSeen}");
                }

                // ---- 5. THE DECISIVE HEAL TEST ----
                string healWorked = "skipped";
                if (healTarget == null)
                {
                    _log?.LogInfo("[ScenarioRunner] APCB HEAL skipped: no enabled bridge with a witnessable-but-unmerged direction (nothing to heal; if stale=0 everywhere the merge lists are already current).");
                }
                else if (dirtyBridge == null)
                {
                    _log?.LogWarning("[ScenarioRunner] APCB HEAL skipped: DirtyBridgeNetworks not resolved via reflection.");
                }
                else
                {
                    try
                    {
                        var dev = healTarget.Dev;
                        var inNet = healTarget.InNet;
                        var outNet = healTarget.OutNet;

                        // (a) Before state.
                        _log?.LogInfo(
                            $"[ScenarioRunner] APCB HEAL target={healKind} ref={dev.ReferenceId} prefab={dev.PrefabName} before: " +
                            $"in={ApcbNetState(inNet, powerDirtyField, dataDirtyField, dataListField)} " +
                            $"out={ApcbNetState(outNet, powerDirtyField, dataDirtyField, dataListField)} " +
                            $"witnessInToOut={healTarget.WitnessInToOut} witnessOutToIn={healTarget.WitnessOutToIn} " +
                            $"mergeInToOut={healTarget.MergeInToOut} mergeOutToIn={healTarget.MergeOutToIn}");

                        // (b) The mod's own invalidation: sets BOTH dirty flags on every net this bridge joins.
                        dirtyBridge.Invoke(null, new object[] { dev });
                        _log?.LogInfo(
                            "[ScenarioRunner] APCB HEAL DirtyBridgeNetworks invoked; flags now " +
                            $"in={ApcbNetState(inNet, powerDirtyField, dataDirtyField, dataListField)} " +
                            $"out={ApcbNetState(outNet, powerDirtyField, dataDirtyField, dataListField)}");

                        // (c) The one intentional side effect: read the PROPERTY. The getter tests the power
                        // flag (set in step b), so it must run RefreshPowerAndDataDeviceLists, whose entry saw
                        // DataDeviceListDirty=true, so PGP's merge postfix must merge right here.
                        int inCount = inNet != null ? inNet.DataDeviceList.Count : -1;
                        int outCount = outNet != null ? outNet.DataDeviceList.Count : -1;
                        _log?.LogInfo($"[ScenarioRunner] APCB HEAL property read: in.DataDeviceList.Count={inCount} out.DataDeviceList.Count={outCount}");

                        // (d) Re-check from the RAW field.
                        ApcbMergeVerdict(dev, inNet, outNet, dataListField,
                            out bool w1, out bool w2, out bool m1, out bool m2);
                        bool healedAll = (!w1 || m1) && (!w2 || m2);
                        healWorked = healedAll ? "true" : "false";
                        _log?.LogInfo(
                            $"[ScenarioRunner] APCB HEAL result mergeInToOut={m1} mergeOutToIn={m2} " +
                            $"(before {healTarget.MergeInToOut}/{healTarget.MergeOutToIn}) -> " +
                            (healedAll
                                ? "H2 PROVEN: machinery fine; the refresh cadence is gone (no every-tick PowerDeviceList reader since the 2026-07-12 rebuild removed vanilla PowerTick.Initialise)."
                                : "H1 INDICATED: dirty + property read did NOT produce the merge; the merge machinery or the gate is broken."));
                    }
                    catch (Exception e)
                    {
                        healWorked = "skipped";
                        _log?.LogError($"[ScenarioRunner] APCB HEAL threw: {e}");
                    }
                }

                // ---- 6. Summary ----
                _log?.LogInfo(
                    $"[ScenarioRunner] APCB SUMMARY apcs={apcReports.Count} mode1={mode1} merged={mergedCount} stale={staleCount} " +
                    $"healWorked={healWorked} housingsDark={housingsDark}/{housings.Count} " +
                    $"forceFields={forceFields.Count} ffPowered={ffPowered}");
                _log?.LogInfo("[ScenarioRunner] APCB END");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] APCB threw: {e}");
            }
        }

        // ---- pgp-apc-bridge-probe helpers ----

        // Read an internal static bool property (PassthroughDefaultsSync.Effective*). Null-tolerant.
        private static string ApcbStaticBool(PropertyInfo p)
        {
            if (p == null) return "unresolved";
            try { return p.GetValue(null)?.ToString() ?? "null"; }
            catch (Exception e) { return "threw:" + e.GetBaseException().GetType().Name; }
        }

        // Read Settings.<fieldName>.Value (a BepInEx ConfigEntry<bool> static field). Null-tolerant.
        private static string ApcbConfigBool(Type settingsT, string fieldName)
        {
            try
            {
                var f = settingsT?.GetField(fieldName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (f == null) return "unresolved";
                var entry = f.GetValue(null);
                if (entry == null) return "null-entry";
                var v = entry.GetType().GetProperty("Value")?.GetValue(entry);
                return v?.ToString() ?? "null";
            }
            catch (Exception e) { return "threw:" + e.GetBaseException().GetType().Name; }
        }

        private static ApcbBridgeReport ApcbSurvey(Device dev, CableNetwork inNet, CableNetwork outNet,
            MethodInfo getMode, MethodInfo isEnabledBridge, FieldInfo dataListField)
        {
            var r = new ApcbBridgeReport { Dev = dev, InNet = inNet, OutNet = outNet };
            try { if (getMode != null) r.Mode = (int)getMode.Invoke(null, new object[] { (Thing)dev }); } catch { }
            try { if (isEnabledBridge != null) r.Bridge = (bool)isEnabledBridge.Invoke(null, new object[] { dev }); } catch { }
            ApcbMergeVerdict(dev, inNet, outNet, dataListField,
                out r.WitnessInToOut, out r.WitnessOutToIn, out r.MergeInToOut, out r.MergeOutToIn);
            return r;
        }

        // Merge verdict from the RAW _dataDeviceList field (no property read, no refresh side effect).
        // mergeInToOut: the input net's raw data list holds a far-side witness from the output net.
        // A witness is a Device physically in the far net's DeviceList, excluding the bridge itself
        // and any device also physically on the near net (a both-sides device proves nothing).
        private static void ApcbMergeVerdict(Device dev, CableNetwork inNet, CableNetwork outNet, FieldInfo dataListField,
            out bool witnessInToOut, out bool witnessOutToIn, out bool mergeInToOut, out bool mergeOutToIn)
        {
            witnessInToOut = false; witnessOutToIn = false; mergeInToOut = false; mergeOutToIn = false;
            if (inNet == null || outNet == null || inNet == outNet || dataListField == null) return;

            var inMembers = new HashSet<Device>();
            var inDevs = inNet.DeviceList;
            for (int i = 0; i < inDevs.Count; i++)
                if (inDevs[i] != null) inMembers.Add(inDevs[i]);

            var outMembers = new HashSet<Device>();
            var outDevs = outNet.DeviceList;
            for (int i = 0; i < outDevs.Count; i++)
                if (outDevs[i] != null) outMembers.Add(outDevs[i]);

            foreach (var d in outMembers)
                if (d != dev && !inMembers.Contains(d)) { witnessInToOut = true; break; }
            foreach (var d in inMembers)
                if (d != dev && !outMembers.Contains(d)) { witnessOutToIn = true; break; }

            List<Device> inRaw = null, outRaw = null;
            try { inRaw = dataListField.GetValue(inNet) as List<Device>; } catch { }
            try { outRaw = dataListField.GetValue(outNet) as List<Device>; } catch { }

            if (witnessInToOut && inRaw != null)
            {
                for (int i = 0; i < inRaw.Count; i++)
                {
                    var d = inRaw[i];
                    if (d != null && d != dev && outMembers.Contains(d) && !inMembers.Contains(d)) { mergeInToOut = true; break; }
                }
            }
            if (witnessOutToIn && outRaw != null)
            {
                for (int i = 0; i < outRaw.Count; i++)
                {
                    var d = outRaw[i];
                    if (d != null && d != dev && inMembers.Contains(d) && !outMembers.Contains(d)) { mergeOutToIn = true; break; }
                }
            }
        }

        // One net as a compact token: id(devs=..,Pdirty=..,Ddirty=..,rawData=..). All raw-field reads.
        private static string ApcbNetState(CableNetwork net, FieldInfo powerDirtyField, FieldInfo dataDirtyField, FieldInfo dataListField)
        {
            if (net == null) return "none";
            string p = "?", d = "?", raw = "?";
            try { if (powerDirtyField != null) p = ((bool)powerDirtyField.GetValue(net)).ToString(); } catch { p = "err"; }
            try { if (dataDirtyField != null) d = ((bool)dataDirtyField.GetValue(net)).ToString(); } catch { d = "err"; }
            try { raw = (dataListField?.GetValue(net) as List<Device>)?.Count.ToString() ?? "?"; } catch { raw = "err"; }
            int devs = -1;
            try { devs = net.DeviceList != null ? net.DeviceList.Count : -1; } catch { }
            return $"{net.ReferenceId}(devs={devs},Pdirty={p},Ddirty={d},rawData={raw})";
        }

        private static void ApcbLogBridgeLine(string tag, ApcbBridgeReport r,
            FieldInfo powerDirtyField, FieldInfo dataDirtyField, FieldInfo dataListField)
        {
            var dev = r.Dev;
            CableNetwork dataNet = null;
            try { dataNet = dev.DataCableNetwork; } catch { }
            _log?.LogInfo(
                $"[ScenarioRunner] APCB {tag} ref={dev.ReferenceId} prefab={dev.PrefabName} " +
                $"mode={(r.Mode >= 0 ? r.Mode.ToString() : "?")} bridge={(r.Bridge.HasValue ? r.Bridge.Value.ToString() : "?")} " +
                $"in={ApcbNetState(r.InNet, powerDirtyField, dataDirtyField, dataListField)} " +
                $"out={ApcbNetState(r.OutNet, powerDirtyField, dataDirtyField, dataListField)} " +
                $"data={ApcbNetState(dataNet, powerDirtyField, dataDirtyField, dataListField)} " +
                $"witnessInToOut={r.WitnessInToOut} witnessOutToIn={r.WitnessOutToIn} " +
                $"mergeInToOut={r.MergeInToOut} mergeOutToIn={r.MergeOutToIn}");
        }
    }
}
