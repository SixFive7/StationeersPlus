using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;

namespace ScenarioRunner
{
    // Scenario: pgp-dataport-tier-diag
    //
    // PASS/FAIL regression test for "exclusive data-only ports accept any cable tier and never burn".
    // A device's data-only port (an OpenEnd whose ConnectionType == NetworkType.Data, no Power bit)
    // must be invisible to the power-tier rules. On a LOADED save this probe checks:
    //
    //   PART A  Prefab census: every prefab that exposes a dedicated Data-only connector (the universe).
    //
    //   PART B  Per live device with a separate data-only port: its data network vs its power
    //           network(s). THE KEY RUNTIME INVARIANT: a device whose data port is on its OWN network
    //           (dataDistinct=True) must NOT be in that network's PowerDeviceList -- the game builds
    //           PowerDeviceList from power-bit cables (RefreshPowerAndDataDeviceLists), so the tier
    //           rule (IsAllowedOnTier over PowerDeviceList) and the burn path can never reach it.
    //           realBugShape counts the violations of that invariant (must be 0).
    //
    //   PART C  Per data network: which PowerDeviceList members are not allowed on the net's own tier.
    //           These are POWER-port members (separate concern, power rules unchanged); a data-only
    //           port device must never appear here.
    //
    //   PART D  Cursor carve-out, tested headless: reflect PGP's private
    //           VoltageTierPatches.CursorAttachesToPowerPortOf(Cable, Device) and assert that a cable
    //           drawn from a device's SEPARATE data network does NOT register as attaching to a power
    //           port of that device (so the cursor never applies the device's tier rule to a data
    //           placement). Power-cable sanity: a power cable DOES register true.
    //
    //   VERDICT runtimeInvariant + cursorCarveOut PASS/FAIL.
    //
    // Requires PowerGridPlus loaded. Reads cached fields only (worker-thread safe).
    internal static partial class Dispatcher
    {
        private static bool _dataPortTierDiagFired;

        private static void Scenario_DataPortTierDiag()
        {
            if (_dataPortTierDiagFired) return;

            const string PGP = "PowerGridPlus";
            if (!RequireModAssembly(PGP, "pgp-dataport-tier-diag")) return;
            _dataPortTierDiagFired = true;

            try
            {
                _log?.LogInfo("[ScenarioRunner] DPT START");

                var asm = GetModAssembly(PGP);
                var voltageTierT = asm.GetType("PowerGridPlus.VoltageTier");
                var enforcerT = asm.GetType("PowerGridPlus.VoltageTierEnforcer");
                var patchesT = asm.GetType("PowerGridPlus.Patches.VoltageTierPatches");
                var isAllowedOnTier = voltageTierT?.GetMethod("IsAllowedOnTier",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                var detectViolation = enforcerT?.GetMethod("DetectViolation",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                var cursorAttaches = patchesT?.GetMethod("CursorAttachesToPowerPortOf",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                if (isAllowedOnTier == null || detectViolation == null || cursorAttaches == null)
                {
                    _log?.LogError($"[ScenarioRunner] DPT FAIL reflection: isAllowedOnTier={(isAllowedOnTier != null)} detectViolation={(detectViolation != null)} cursorAttaches={(cursorAttaches != null)}");
                    return;
                }

                // ---- PART A: prefab census of devices with a separate data-only port ----
                var sepDataPrefabs = new List<string>();
                int prefabTotal = 0;
                foreach (var prefab in Prefab.AllPrefabs)
                {
                    if (prefab == null) continue;
                    prefabTotal++;
                    if (!(prefab is SmallGrid grid)) continue;
                    var ends = grid.OpenEnds;
                    if (ends == null || ends.Count == 0) continue;
                    bool hasPureData = false, hasPower = false;
                    foreach (var c in ends)
                    {
                        if (c == null) continue;
                        var ct = c.ConnectionType;
                        if (ct == NetworkType.Data) hasPureData = true;
                        if ((ct & NetworkType.Power) != NetworkType.None) hasPower = true;
                    }
                    if (hasPureData)
                        sepDataPrefabs.Add($"{prefab.PrefabName}|{prefab.GetType().Name}|hasPowerPort={hasPower}");
                }
                sepDataPrefabs.Sort();
                _log?.LogInfo($"[ScenarioRunner] DPT PART-A separateDataPrefabs={sepDataPrefabs.Count} of {prefabTotal}");
                foreach (var s in sepDataPrefabs)
                    _log?.LogInfo($"[ScenarioRunner] DPT PART-A {s}");

                // ---- PART B: live separate-data-port devices ----
                var liveDevices = new List<Device>();
                OcclusionManager.AllThings.ForEach(t => { if (t is Device dv) liveDevices.Add(dv); });

                int sepLive = 0, realBugShape = 0;
                var dataNets = new HashSet<CableNetwork>();
                var distinctPairs = new List<KeyValuePair<Device, CableNetwork>>();

                foreach (var d in liveDevices)
                {
                    if (d == null || d.OpenEnds == null) continue;
                    bool hasPureData = false;
                    foreach (var c in d.OpenEnds)
                        if (c != null && c.ConnectionType == NetworkType.Data) { hasPureData = true; break; }
                    if (!hasPureData) continue;
                    sepLive++;

                    var dataNet = d.DataCableNetwork;
                    if (dataNet != null) dataNets.Add(dataNet);

                    var powerNets = new HashSet<CableNetwork>();
                    if (d.PowerCables != null)
                        foreach (var pc in d.PowerCables)
                            if (pc?.CableNetwork != null) powerNets.Add(pc.CableNetwork);

                    bool dataDistinct = dataNet != null && !powerNets.Contains(dataNet);

                    bool inDataNetPowerList = false;
                    if (dataNet != null)
                    {
                        lock (dataNet.PowerDeviceList)
                            for (int i = 0; i < dataNet.PowerDeviceList.Count; i++)
                                if (dataNet.PowerDeviceList[i] == d) { inDataNetPowerList = true; break; }
                    }

                    // The invariant violation: a SEPARATE data network listing its device as a power device.
                    if (dataDistinct && inDataNetPowerList) realBugShape++;
                    if (dataDistinct) distinctPairs.Add(new KeyValuePair<Device, CableNetwork>(d, dataNet));

                    _log?.LogInfo(
                        $"[ScenarioRunner] DPT PART-B ref={d.ReferenceId} prefab={d.PrefabName} type={d.GetType().Name} " +
                        $"dataNet={(dataNet?.ReferenceId ?? 0)} dataTier={(dataNet != null ? NetTierStr(dataNet) : "none")} " +
                        $"dataDistinct={dataDistinct} inDataNetPowerList={inDataNetPowerList} powerNets={powerNets.Count} " +
                        $"violation={DescribeViolation(detectViolation, dataNet)}");
                }
                _log?.LogInfo($"[ScenarioRunner] DPT PART-B END sepLive={sepLive} realBugShape={realBugShape} distinctDataPorts={distinctPairs.Count}");

                // ---- PART C: per data network, would-burn members at the net's own tier ----
                foreach (var net in dataNets)
                {
                    if (net == null) continue;
                    Cable.Type? nt = NetCableType(net, out bool mixed);
                    var bad = new List<string>();
                    int members = 0;
                    if (nt.HasValue)
                    {
                        lock (net.PowerDeviceList)
                        {
                            members = net.PowerDeviceList.Count;
                            for (int i = 0; i < net.PowerDeviceList.Count; i++)
                            {
                                var m = net.PowerDeviceList[i];
                                if (m == null) continue;
                                bool allowed;
                                try { allowed = (bool)isAllowedOnTier.Invoke(null, new object[] { m, nt.Value }); }
                                catch { allowed = true; }
                                if (!allowed) bad.Add($"{m.GetType().Name}#{m.ReferenceId}");
                            }
                        }
                    }
                    _log?.LogInfo(
                        $"[ScenarioRunner] DPT PART-C dataNet={net.ReferenceId} tier={(nt.HasValue ? nt.Value.ToString() : "empty")} mixed={mixed} " +
                        $"powerMembers={members} wouldBurn={bad.Count} [{string.Join(",", bad)}]");
                }

                // ---- PART D: cursor carve-out logic, tested headless ----
                int dChecked = 0, dDataExempt = 0, dDataLeak = 0, dPowerSanity = 0, dPowerSkip = 0;
                foreach (var pair in distinctPairs)
                {
                    var dev = pair.Key;
                    var dnet = pair.Value;
                    Cable sampleData = FirstCable(dnet);
                    if (sampleData == null) continue;
                    dChecked++;
                    bool dataAttaches;
                    try { dataAttaches = (bool)cursorAttaches.Invoke(null, new object[] { sampleData, dev }); }
                    catch (Exception e) { _log?.LogError($"[ScenarioRunner] DPT PART-D invoke threw: {e.InnerException?.Message ?? e.Message}"); break; }
                    if (!dataAttaches) dDataExempt++;
                    else { dDataLeak++; _log?.LogWarning($"[ScenarioRunner] DPT PART-D LEAK: data cable on net {dnet.ReferenceId} registers as power-port attach of {dev.PrefabName}#{dev.ReferenceId}"); }

                    Cable samplePower = FirstPowerCable(dev);
                    if (samplePower != null)
                    {
                        bool pAttaches;
                        try { pAttaches = (bool)cursorAttaches.Invoke(null, new object[] { samplePower, dev }); }
                        catch { pAttaches = false; }
                        if (pAttaches) dPowerSanity++;
                    }
                    else dPowerSkip++;
                }
                _log?.LogInfo($"[ScenarioRunner] DPT PART-D cursorCarveOut checked={dChecked} dataExempt={dDataExempt} dataLEAK={dDataLeak} powerSanityTrue={dPowerSanity} powerSkip={dPowerSkip}");

                // ---- VERDICT ----
                bool runtimePass = realBugShape == 0;
                bool cursorPass = dDataLeak == 0;
                _log?.LogInfo($"[ScenarioRunner] DPT VERDICT runtimeInvariant={(runtimePass ? "PASS" : "FAIL")} (realBugShape={realBugShape}) cursorCarveOut={(cursorPass ? "PASS" : "FAIL")} (dataLeak={dDataLeak})");
                _log?.LogInfo("[ScenarioRunner] DPT END");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] DPT threw: {e.InnerException?.Message ?? e.Message}\n{e.StackTrace}");
            }
        }

        private static Cable FirstCable(CableNetwork net)
        {
            if (net == null) return null;
            lock (net.CableList)
                foreach (var c in net.CableList)
                    if (c != null) return c;
            return null;
        }

        private static Cable FirstPowerCable(Device d)
        {
            if (d?.PowerCables == null) return null;
            foreach (var c in d.PowerCables)
                if (c != null) return c;
            return null;
        }

        private static string DescribeViolation(MethodInfo detectViolation, CableNetwork net)
        {
            if (net == null) return "n/a";
            try
            {
                var v = detectViolation.Invoke(null, new object[] { net });
                if (v == null) return "null";
                var kind = v.GetType().GetField("Kind")?.GetValue(v);
                var dev = v.GetType().GetField("Device")?.GetValue(v) as Device;
                return kind + (dev != null ? $"({dev.GetType().Name}#{dev.ReferenceId})" : "");
            }
            catch (Exception e) { return "err:" + (e.InnerException?.Message ?? e.Message); }
        }

        private static string NetTierStr(CableNetwork net)
        {
            var t = NetCableType(net, out bool mixed);
            return (t.HasValue ? t.Value.ToString() : "empty") + (mixed ? "(MIXED)" : "");
        }

        private static Cable.Type? NetCableType(CableNetwork net, out bool mixed)
        {
            Cable.Type? type = null;
            mixed = false;
            lock (net.CableList)
            {
                foreach (var c in net.CableList)
                {
                    if (c == null) continue;
                    if (!type.HasValue) type = c.CableType;
                    else if (type.Value != c.CableType) mixed = true;
                }
            }
            return type;
        }
    }
}
