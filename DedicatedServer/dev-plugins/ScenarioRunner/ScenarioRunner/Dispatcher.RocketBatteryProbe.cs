using System;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Util;
using Objects.Rockets;

namespace ScenarioRunner
{
    // Scenario: pgp-rocket-battery-probe
    //
    // Targeted probe for the 2026-07-14 blocker: rocket-mounted StructureBatteryMedium 703648
    // does not charge although the female power umbilical 614953 next to it holds a full cell.
    // Logs, for 8 consecutive settled ticks: both devices' live fields (net ids, PowerStored,
    // Powered, Error), whether they share one runtime network, the PowerGridPlus snapshot row
    // data for that network (RigidDemand, per-row Demand/Generated), and the allocator share
    // caches (the battery's soft charge grant, the female's elastic discharge grant), all via
    // reflection. Read-only; managed state only; runs on the tick worker like every scenario.
    internal static partial class Dispatcher
    {
        private const long RbpBatteryRef = 703648L;
        private const long RbpFemaleRef = 614953L;

        private static int _rbpLogged;
        private static int _rbpSettle;
        private static MethodInfo _rbpSoftActual, _rbpSupplyActual, _rbpSoftTry, _rbpSupplyTry, _rbpActiveFault;
        private static PropertyInfo _rbpSnapCurrent, _rbpTick;

        private static void Scenario_PgpRocketBatteryProbe()
        {
            if (_rbpLogged >= 8) return;
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-rocket-battery-probe")) return;
            if (!GameManager.RunSimulation) return;
            if (++_rbpSettle < 10) return;   // let the world and the allocator settle

            var asm = GetModAssembly(PGP_ASSEMBLY);
            if (_rbpSoftActual == null)
            {
                _rbpSoftActual = asm.GetType("PowerGridPlus.SoftDemandShareCache")
                    ?.GetMethod("GetActualOrZero", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                _rbpSupplyActual = asm.GetType("PowerGridPlus.SoftSupplyShareCache")
                    ?.GetMethod("GetActualOrZero", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                _rbpSnapCurrent = asm.GetType("PowerGridPlus.Core.GridSnapshot")
                    ?.GetProperty("Current", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                _rbpSoftTry = asm.GetType("PowerGridPlus.SoftDemandShareCache")
                    ?.GetMethod("TryGetShare", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                _rbpSupplyTry = asm.GetType("PowerGridPlus.SoftSupplyShareCache")
                    ?.GetMethod("TryGetShare", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                _rbpActiveFault = asm.GetType("PowerGridPlus.FaultHover")
                    ?.GetMethod("ActiveFault", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                _rbpTick = asm.GetType("PowerGridPlus.ElectricityTickCounter")
                    ?.GetProperty("CurrentTick", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (_rbpSoftActual == null || _rbpSupplyActual == null)
                {
                    _log?.LogError("[ScenarioRunner] RBP seams unresolved (share caches); probe disabled.");
                    _rbpLogged = int.MaxValue;
                    return;
                }
            }

            var battery = Referencable.Find<Battery>(RbpBatteryRef);
            var female = Referencable.Find<RocketPowerUmbilicalFemale>(RbpFemaleRef);
            if (battery == null || female == null)
            {
                if (_rbpSettle == 10)
                    _log?.LogWarning($"[ScenarioRunner] RBP targets missing (battery={(battery != null)} female={(female != null)}); wrong save?");
                return;
            }

            _rbpLogged++;
            var isOperableProp = battery.GetType().GetProperty("IsOperable",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            object operable = isOperableProp?.GetValue(battery) ?? "?";
            _log?.LogInfo($"[ScenarioRunner] RBP battery flags: IsStructureCompleted={battery.IsStructureCompleted} IsBroken={battery.IsBroken} IsOperable={operable} buildState={battery.CurrentBuildState}");
            long bNet = battery.PowerCableNetwork?.ReferenceId ?? -1;
            long bInNet = battery.InputNetwork?.ReferenceId ?? -1;
            long bOutNet = battery.OutputNetwork?.ReferenceId ?? -1;
            long fOutNet = female.OutputNetwork?.ReferenceId ?? -1;
            double bGrant = (float)_rbpSoftActual.Invoke(null, new object[] { RbpBatteryRef });
            double fGrant = (float)_rbpSupplyActual.Invoke(null, new object[] { RbpFemaleRef });

            _log?.LogInfo($"[ScenarioRunner] RBP tick={_rbpLogged} battery: stored={battery.PowerStored:0} powered={battery.Powered} onoff={battery.OnOff} error={battery.Error} mode={battery.Mode} pcn={bNet} in={bInNet} out={bOutNet} chargeGrant={bGrant:0.0}");
            _log?.LogInfo($"[ScenarioRunner] RBP tick={_rbpLogged} female: stored={female.PowerStored:0} powered={female.Powered} onoff={female.OnOff} error={female.Error} out={fOutNet} dischargeGrant={fGrant:0.0} sameNet={(bInNet == fOutNet && bInNet != -1)}");

            string enrolled = "?";
            if (_rbpSoftTry != null && _rbpSupplyTry != null)
            {
                var argsB = new object[] { RbpBatteryRef, null };
                bool bEnrolled = (bool)_rbpSoftTry.Invoke(null, argsB);
                var argsF = new object[] { RbpFemaleRef, null };
                bool fEnrolled = (bool)_rbpSupplyTry.Invoke(null, argsF);
                enrolled = $"batterySoftEnrolled={bEnrolled}(share={argsB[1]}) femaleElasticEnrolled={fEnrolled}(share={argsF[1]})";
            }
            string faults = "?";
            if (_rbpActiveFault != null && _rbpTick != null)
            {
                int tick = (int)_rbpTick.GetValue(null);
                var bFault = _rbpActiveFault.Invoke(null, new object[] { RbpBatteryRef, tick });
                var fFault = _rbpActiveFault.Invoke(null, new object[] { RbpFemaleRef, tick });
                faults = $"batteryFault={bFault} femaleFault={fFault}";
            }
            _log?.LogInfo($"[ScenarioRunner] RBP enrollment: {enrolled} {faults}");

            var liveT = GetModAssembly(PGP_ASSEMBLY).GetType("PowerGridPlus.NetLiveness");
            var tryVerdict = liveT?.GetMethod("TryGetVerdict", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            string verdict = "?";
            if (tryVerdict != null)
            {
                var args = new object[] { bInNet, null };
                bool has = (bool)tryVerdict.Invoke(null, args);
                verdict = has ? args[1].ToString() : "none";
            }
            var bNetObj = battery.InputNetwork;
            _log?.LogInfo($"[ScenarioRunner] RBP net {bInNet} live: verdict={verdict} pot={bNetObj?.PotentialLoad ?? -1:0} req={bNetObj?.RequiredLoad ?? -1:0} cur={bNetObj?.CurrentLoad ?? -1:0}");

            if (_rbpLogged == 1)
            {
                foreach (var pair in new (string label, Device dev)[] { ("battery", battery), ("female", female) })
                {
                    var cables = pair.dev.PowerCables;
                    string list = "null";
                    if (cables != null)
                    {
                        var parts = new System.Collections.Generic.List<string>();
                        foreach (var c in cables)
                            parts.Add(c == null ? "null" : $"{c.ReferenceId}@net{c.CableNetwork?.ReferenceId ?? -1}");
                        list = parts.Count == 0 ? "EMPTY" : string.Join(" ", parts);
                    }
                    var eio = pair.dev as ElectricalInputOutput;
                    var inC = eio?.InputConnection?.GetCable();
                    var outC = eio?.OutputConnection?.GetCable();
                    _log?.LogInfo($"[ScenarioRunner] RBP {pair.label} cables: PowerCable={(pair.dev.PowerCable?.ReferenceId ?? -1)} PowerCables=[{list}] inConn={(inC?.ReferenceId ?? -1)}@net{(inC?.CableNetwork?.ReferenceId ?? -1)} outConn={(outC?.ReferenceId ?? -1)}@net{(outC?.CableNetwork?.ReferenceId ?? -1)}");
                }
            }

            var net = Referencable.Find<CableNetwork>(bInNet);
            if (net == null)
            {
                _log?.LogInfo($"[ScenarioRunner] RBP net {bInNet}: object NOT FOUND via Referencable");
            }
            else
            {
                int deviceCount, powerDeviceCount, cableCount;
                lock (net.DeviceList) deviceCount = net.DeviceList.Count;
                lock (net.PowerDeviceList) powerDeviceCount = net.PowerDeviceList.Count;
                lock (net.CableList) cableCount = net.CableList.Count;
                bool inPool = false;
                CableNetwork.AllCableNetworks.ForEach(n => { if (n != null && n.ReferenceId == bInNet) inPool = true; });
                _log?.LogInfo($"[ScenarioRunner] RBP net {bInNet}: cables={cableCount} DeviceList={deviceCount} PowerDeviceList={powerDeviceCount} inAllCableNetworks={inPool}");
            }

            var snap = _rbpSnapCurrent?.GetValue(null);
            if (snap != null)
            {
                var byId = snap.GetType().GetField("ById",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(snap) as System.Collections.IDictionary;
                foreach (long netId in new[] { bInNet, fOutNet })
                {
                    if (byId == null) { _log?.LogInfo("[ScenarioRunner] RBP snapshot ById unresolved"); break; }
                    if (netId == -1 || !byId.Contains(netId)) { _log?.LogInfo($"[ScenarioRunner] RBP net {netId}: NOT IN SNAPSHOT"); continue; }
                    var nr = byId[netId];
                    var nrT = nr.GetType();
                    float rigid = (float)nrT.GetField("RigidDemand").GetValue(nr);
                    float gen = (float)(nrT.GetField("GenSupply")?.GetValue(nr) ?? 0f);
                    float weakest = (float)(nrT.GetField("WeakestCap")?.GetValue(nr) ?? -1f);
                    var rows = nrT.GetField("Rows").GetValue(nr) as System.Collections.IList;
                    _log?.LogInfo($"[ScenarioRunner] RBP net {netId}: RigidDemand={rigid:0.0} GenSupply={gen:0.0} WeakestCap={weakest:0} rows={rows?.Count ?? -1}");
                    if (rows != null)
                    {
                        foreach (var row in rows)
                        {
                            var rT = row.GetType();
                            var dev = rT.GetField("Device").GetValue(row) as Device;
                            float demand = (float)rT.GetField("Demand").GetValue(row);
                            float generated = (float)(rT.GetField("Generated")?.GetValue(row) ?? 0f);
                            bool seg = (bool)(rT.GetField("IsSegmenter")?.GetValue(row) ?? false);
                            _log?.LogInfo($"[ScenarioRunner] RBP   row {dev?.ReferenceId} {dev?.PrefabName} demand={demand:0.0} gen={generated:0.0} seg={seg}");
                        }
                    }
                    if (bInNet == fOutNet) break;   // one net, log once
                }
            }
        }
    }
}
