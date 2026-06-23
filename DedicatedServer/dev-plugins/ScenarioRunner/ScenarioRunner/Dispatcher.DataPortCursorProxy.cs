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
    // Scenario: pgp-dataport-cursor-proxy
    //
    // Headless proxy for the placement-cursor ghost COLOR on a data-only port. The ghost color is
    // exactly Cable.CanConstruct().CanConstruct (true = green, false = red), and PowerGridPlus's only
    // contribution is Cable_CanConstruct_Postfix, whose two reject branches are:
    //   pass (a) cable-to-cable: reject if the cursor would power-merge into a different-tier network.
    //   pass (b) cable-to-device: reject if the cursor attaches to a POWER port of a wrong-tier device,
    //            gated by CursorAttachesToPowerPortOf (false for a data-only port).
    //
    // ConnectedCables (pass a) reads Transform.position and is main-thread only, so this probe does NOT
    // invoke the full postfix on the ElectricityTick worker (it would throw and be swallowed to a false
    // green). It computes the color from the SAME predicates the postfix uses -- the worker-safe ones
    // called for real, pass (a) derived from topology:
    //   * pass (b): CursorAttachesToPowerPortOf(cursor, device) is called for real. It is tier-independent,
    //     so the cursor's tier never changes the answer at a data port: any tier gets the same result.
    //   * pass (a): a placed cable on a SEPARATE single-tier CableNetwork has, by definition, no
    //     different-tier power-adjacent cable (adjacency would have merged them into one network), so it
    //     cannot pass-a-reject. A fresh run laid clear of power cables likewise has no power-adjacency.
    //
    // Reports: the ghost color per data-only port (expect all green); "carve-out wins" = (device, tier)
    // pairs the device REJECTS on a power port but ACCEPTS on its data port (each a green the device's own
    // rule would otherwise make red); the power-port CONTRAST (a wrong tier on a power port is still red,
    // so the greens are not vacuous); and a VERDICT. Worker-safe; reads cached fields + the two predicates.
    internal static partial class Dispatcher
    {
        private static bool _dataPortCursorProxyFired;
        private static readonly Cable.Type[] DpcpTiers = { Cable.Type.normal, Cable.Type.heavy, Cable.Type.superHeavy };

        private static void Scenario_DataPortCursorProxy()
        {
            if (_dataPortCursorProxyFired) return;
            const string PGP = "PowerGridPlus";
            if (!RequireModAssembly(PGP, "pgp-dataport-cursor-proxy")) return;
            _dataPortCursorProxyFired = true;

            try
            {
                _log?.LogInfo("[ScenarioRunner] DPCP START");
                var asm = GetModAssembly(PGP);
                var patchesT = asm.GetType("PowerGridPlus.Patches.VoltageTierPatches");
                var voltageTierT = asm.GetType("PowerGridPlus.VoltageTier");
                var cursorAttaches = patchesT?.GetMethod("CursorAttachesToPowerPortOf", BindingFlags.NonPublic | BindingFlags.Static);
                var isAllowedOnTier = voltageTierT?.GetMethod("IsAllowedOnTier", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                if (cursorAttaches == null || isAllowedOnTier == null)
                {
                    _log?.LogError($"[ScenarioRunner] DPCP FAIL reflection cursorAttaches={(cursorAttaches != null)} isAllowedOnTier={(isAllowedOnTier != null)}");
                    return;
                }

                var devices = new List<Device>();
                OcclusionManager.AllThings.ForEach(t => { if (t is Device d) devices.Add(d); });

                int dataPorts = 0, ghostGreen = 0, ghostRed = 0, dataPortLeak = 0, carveOutWins = 0;
                var leakSamples = new List<string>();
                var winSamples = new List<string>();

                foreach (var d in devices)
                {
                    if (d?.OpenEnds == null) continue;
                    bool hasPureData = false;
                    foreach (var oe in d.OpenEnds) if (oe != null && oe.ConnectionType == NetworkType.Data) { hasPureData = true; break; }
                    if (!hasPureData) continue;
                    var dataNet = d.DataCableNetwork;
                    if (dataNet == null) continue;
                    var powerNets = new HashSet<CableNetwork>();
                    if (d.PowerCables != null) foreach (var pc in d.PowerCables) if (pc?.CableNetwork != null) powerNets.Add(pc.CableNetwork);
                    if (powerNets.Contains(dataNet)) continue; // data port shares a network with a power port; not the carve-out case
                    var dataCable = FirstCable(dataNet);
                    if (dataCable == null) continue;
                    dataPorts++;

                    // pass (b): the postfix applies the device's tier rule only when the cursor attaches to a
                    // POWER port. For a data-only port this must be false -> the rule is skipped for ANY tier.
                    bool attaches;
                    try { attaches = (bool)cursorAttaches.Invoke(null, new object[] { dataCable, d }); } catch { attaches = true; }
                    if (attaches) { dataPortLeak++; if (leakSamples.Count < 10) leakSamples.Add($"{d.PrefabName}#{d.ReferenceId}"); }

                    // pass (a): a separate single-tier data network cannot power-merge into a different tier
                    // (adjacency would have merged it). A mixed net would reject.
                    NetCableType(dataNet, out bool mixed);
                    bool green = !attaches && !mixed;
                    if (green) ghostGreen++; else ghostRed++;

                    // carve-out wins: tiers the device REJECTS on a power port but ACCEPTS on its data port,
                    // because pass (b) is skipped. Each is a green the device's own rule would have made red.
                    if (!attaches)
                        foreach (var T in DpcpTiers)
                        {
                            bool allowed; try { allowed = (bool)isAllowedOnTier.Invoke(null, new object[] { d, T }); } catch { allowed = true; }
                            if (!allowed) { carveOutWins++; if (winSamples.Count < 12) winSamples.Add($"{d.GetType().Name}#{d.ReferenceId}+{T}"); }
                        }
                }

                _log?.LogInfo($"[ScenarioRunner] DPCP DATA-PORT dataPorts={dataPorts} ghostGreen={ghostGreen} ghostRed={ghostRed} dataPortLeak={dataPortLeak}");
                _log?.LogInfo($"[ScenarioRunner] DPCP CARVE-OUT-WINS {carveOutWins} (device rejects this tier on a power port but a data run of it gets a GREEN ghost) e.g. {string.Join(", ", winSamples)}");
                foreach (var s in leakSamples) _log?.LogWarning($"[ScenarioRunner] DPCP LEAK (data port subjected to the tier rule): {s}");

                // CONTRAST: power-port placements still enforce the tier rule, so the greens above are not vacuous.
                int contrastDevices = 0, contrastWrongTierRejects = 0;
                foreach (var d in devices)
                {
                    if (contrastDevices >= 30) break;
                    var pc = FirstPowerCable(d);
                    if (pc == null) continue;
                    bool attaches; try { attaches = (bool)cursorAttaches.Invoke(null, new object[] { pc, d }); } catch { attaches = false; }
                    if (!attaches) continue;
                    contrastDevices++;
                    foreach (var T in DpcpTiers)
                    {
                        bool allowed; try { allowed = (bool)isAllowedOnTier.Invoke(null, new object[] { d, T }); } catch { allowed = true; }
                        if (!allowed) { contrastWrongTierRejects++; break; }
                    }
                }
                _log?.LogInfo($"[ScenarioRunner] DPCP CONTRAST power-port devices checked={contrastDevices}, of which a wrong tier on the power port is correctly RED={contrastWrongTierRejects}");

                bool pass = dataPortLeak == 0 && ghostRed == 0;
                _log?.LogInfo($"[ScenarioRunner] DPCP VERDICT dataPortGhostAlwaysGreen={(pass ? "PASS" : "FAIL")} (leak={dataPortLeak}, red={ghostRed}, carveOutWins={carveOutWins}). A fresh data run of ANY tier to a data-only port is accepted (green); merging a different tier into a power network is still rejected (red).");
                _log?.LogInfo("[ScenarioRunner] DPCP END");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] DPCP threw: {e.InnerException?.Message ?? e.Message}\n{e.StackTrace}");
            }
        }
    }
}
