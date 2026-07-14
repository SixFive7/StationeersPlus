using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networking;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Util;
using UnityEngine;

namespace ScenarioRunner
{
    // Scenario: pgp-mixedwire-fixture
    //
    // Live-world proof of the mixed-tier prevention and repair set (POWER.md decision 31):
    // the registration guard (WiringGuardPatches: cross-tier merge refusal with drop-as-kit,
    // cell-theft capture with victim re-seat), the per-kind detection gates (activity gate
    // for Mixed, flow gate for device kinds), the event-evicted tier cache, and the
    // stack-aware ResolveMixedTierNetwork repair (seat survivor, destroy thief, no ghost).
    //
    // Unlike the synthetic fixtures, this one SPAWNS real cables: every world mutation is
    // marshalled to the Unity main thread through PowerGridPlus's UnityMainThreadDispatcher
    // (resolved by reflection), because this pump runs on the UniTask ThreadPool worker.
    // The fixture is a per-tick state machine: each step waits for its enqueued main-thread
    // action to land (plus a tick or two for end-of-frame destroys) before asserting.
    //
    // Cables spawn as straight pieces at identity rotation (open ends along Z, verified from the
    // Luna_mixedwire save geometry) in an out-of-the-way column at world (400, 400, 400);
    // everything spawned is destroyed in the final step. Requires a running simulation (the
    // suite's force-unpause covers headless runs) and the PowerGridPlus assembly.
    //
    // Phases:
    //   P0  seams: WiringGuard.Suspended, PlayerConsole.LastBroadcast, GetTierInfo,
    //       DetectViolation(NetRow), ResolveMixedTierNetwork, UnityMainThreadDispatcher.
    //   P1  tier refusal: a super-heavy straight registered adjacent to a normal straight is
    //       destroyed by the guard, the normal net stays single-tier, the console line reads
    //       "refused super heavy cable joining a normal network, dropped as kit", and a kit
    //       item exists at the refusal spot.
    //   P2  theft refusal: a super-heavy straight registered INTO the normal straight's cell
    //       is destroyed, the victim is re-seated (SmallCell.Cable is the victim again), the
    //       console line reads "stacked on a normal cable", and a kit item exists.
    //   P3  with the guard suspended, a stacked print (cluster C) is AUTO-repaired by the
    //       per-tick backstop within the wait window, with no manual invocation and no
    //       electrical activity: the StackedTheft bypass of the activity gate, end to end
    //       (thief destroyed without wreckage, survivor seated, console line).
    //   P4  gate matrix on a pure adjacent mixed pair (cluster D, no theft) via a
    //       reflection-built snapshot NetRow: all-zero sums -> None (idle mixed wiring is
    //       left alone); RigidDemand only -> Mixed (the incident deadlock shape);
    //       PotentialSum only -> Mixed (live feed, nothing drawing yet).
    //   P5  a forged orphan (cluster E: cell slot emptied directly, the seated-thief-destroyed
    //       aftermath) is AUTO-re-seated by the backstop and its network rebuilt so it merges
    //       back with its neighbor ("Repaired orphaned normal cable" console line).
    //   P5b cluster C, pure mixed and idle after its repair, is NOT burned by the backstop
    //       (the activity gate holds for burns).
    //   P6  a manual resolve then burns C's residual boundary cable ("burned normal cable
    //       joining a super heavy network") and the super-heavy straight survives.
    //
    // Emits per-assertion "MWF P<n> PASS/FAIL" lines and a final
    // "[ScenarioRunner] MWF SUMMARY pass=N fail=M".
    internal static partial class Dispatcher
    {
        private const int MwDone = -1;

        private static int _mwStep;
        private static int _mwWait;
        private static int _mwPass;
        private static int _mwFail;
        private static volatile bool _mwBusy;
        private static volatile string _mwActionError;

        // Straight cables at identity rotation run along Z (verified from the Luna_mixedwire save:
        // x-axis trunk rows carry yaw-90 quaternions), so the chains here step Z by one 0.5 m cell.
        private static readonly Vector3 MwPosA = new Vector3(400f, 400f, 400f);
        private static readonly Vector3 MwPosA2 = new Vector3(400f, 400f, 400.5f);
        private static readonly Vector3 MwPosB = new Vector3(402f, 400f, 400f);
        private static readonly Vector3 MwPosC = new Vector3(404f, 400f, 400f);
        private static readonly Vector3 MwPosC2 = new Vector3(404f, 400f, 400.5f);

        private static Cable _mwN1, _mwS1, _mwN2, _mwS2, _mwN3, _mwN4, _mwS3, _mwN5, _mwS5, _mwN6, _mwN7;
        private static readonly Vector3 MwPosD = new Vector3(406f, 400f, 400f);
        private static readonly Vector3 MwPosD2 = new Vector3(406f, 400f, 400.5f);
        private static readonly Vector3 MwPosE = new Vector3(408f, 400f, 400f);
        private static readonly Vector3 MwPosE2 = new Vector3(408f, 400f, 400.5f);
        private static readonly List<Thing> _mwSpawned = new List<Thing>();

        // PowerGridPlus seams (resolved in P0).
        private static FieldInfo _mwSuspendedF, _mwLastBroadcastF;
        private static MethodInfo _mwGetTierInfo, _mwDetectNetRow, _mwResolveMixed, _mwInvalidateNet, _mwRequestRecheck;
        private static FieldInfo _mwTierInfoMixedF, _mwViolationKindF;
        private static Type _mwNetRowT;
        private static FieldInfo _mwNrNetworkF, _mwNrRigidF, _mwNrReqF, _mwNrPotF;

        private static void MwCheck(string id, bool ok, string detail)
        {
            if (ok) { _mwPass++; _log?.LogInfo($"[ScenarioRunner] MWF {id} PASS: {detail}"); }
            else { _mwFail++; _log?.LogError($"[ScenarioRunner] MWF {id} FAIL: {detail}"); }
        }

        private static void Scenario_PgpMixedWireFixture()
        {
            if (_mwStep == MwDone) return;
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-mixedwire-fixture")) return;
            if (!GameManager.RunSimulation) return;

            try
            {
                MwTick();
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] MWF step {_mwStep} threw: {e}");
                _mwFail++;
                MwFinish();
            }
        }

        private static void MwTick()
        {
            if (_mwBusy) return;
            if (_mwActionError != null)
            {
                MwCheck($"P{_mwStep}", false, $"main-thread action failed: {_mwActionError}");
                _mwActionError = null;
                MwFinish();
                return;
            }
            if (_mwWait > 0) { _mwWait--; return; }

            switch (_mwStep)
            {
                case 0:
                    MwStep0Seams();
                    break;

                case 1:
                    MwCheck("P1a", MwAlive(_mwN1) && _mwN1.CableNetwork != null,
                        $"normal straight registered on its own net (net {(_mwN1?.CableNetwork == null ? "null" : _mwN1.CableNetwork.ReferenceId.ToString())})");
                    MwEnqueue(() => _mwS1 = MwSpawnCable("StructureCableSuperHeavyStraight", MwPosA2));
                    _mwWait = 3;
                    _mwStep = 2;
                    break;

                case 2:
                {
                    string last = MwLastBroadcast();
                    bool refused = _mwS1 == null || !MwAlive(_mwS1);
                    bool intact = MwAlive(_mwN1) && !MwMixed(_mwN1.CableNetwork);
                    bool line = last != null && last.Contains("refused super heavy cable joining a normal network")
                                && last.Contains("dropped as kit");
                    MwCheck("P1", refused && intact && line && MwKitNear(MwPosA2),
                        $"tier refusal (refused={refused} intact={intact} kit={MwKitNear(MwPosA2)} line=\"{last}\")");
                    MwEnqueue(() => _mwN2 = MwSpawnCable("StructureCableStraight", MwPosB));
                    _mwWait = 2;
                    _mwStep = 3;
                    break;
                }

                case 3:
                    MwCheck("P2a", MwAlive(_mwN2), "victim straight registered");
                    MwEnqueue(() => _mwS2 = MwSpawnCable("StructureCableSuperHeavyStraight", MwPosB));
                    _mwWait = 3;
                    _mwStep = 4;
                    break;

                case 4:
                {
                    string last = MwLastBroadcast();
                    bool refused = _mwS2 == null || !MwAlive(_mwS2);
                    bool reseated = MwAlive(_mwN2) && _mwN2.SmallCell != null && ReferenceEquals(_mwN2.SmallCell.Cable, _mwN2);
                    bool line = last != null && last.Contains("stacked on a normal cable");
                    MwCheck("P2", refused && reseated && line && MwKitNear(MwPosB),
                        $"theft refusal with re-seat (refused={refused} reseated={reseated} kit={MwKitNear(MwPosB)} line=\"{last}\")");
                    // Suspended mega-spawn: cluster C (normal pair + super stacked over N3), cluster D
                    // (pure adjacent mixed pair, no theft), cluster E (normal pair for the orphan case).
                    // With StackedTheft bypassing the activity gate, the per-tick backstop auto-repairs
                    // C's stack within a tick or two: the wait window is the test.
                    MwEnqueue(() =>
                    {
                        _mwSuspendedF.SetValue(null, true);
                        _mwN3 = MwSpawnCable("StructureCableStraight", MwPosC);
                        _mwN4 = MwSpawnCable("StructureCableStraight", MwPosC2);
                        _mwS3 = MwSpawnCable("StructureCableSuperHeavyStraight", MwPosC);
                        _mwN5 = MwSpawnCable("StructureCableStraight", MwPosD);
                        _mwS5 = MwSpawnCable("StructureCableSuperHeavyStraight", MwPosD2);
                        _mwN6 = MwSpawnCable("StructureCableStraight", MwPosE);
                        _mwN7 = MwSpawnCable("StructureCableStraight", MwPosE2);
                    });
                    _mwWait = 5;
                    _mwStep = 5;
                    break;
                }

                case 5:
                {
                    // P3: cluster C's stack auto-repaired by the backstop, with NO manual invocation and
                    // NO electrical activity (the StackedTheft bypass end-to-end).
                    string last = MwLastBroadcast();
                    bool thiefGone = !MwAlive(_mwN3);
                    bool survivorSeated = MwAlive(_mwS3) && _mwS3.SmallCell != null && ReferenceEquals(_mwS3.SmallCell.Cable, _mwS3);
                    bool repairLine = last != null && last.Contains("removed normal cable stacked on a super heavy cable");
                    MwCheck("P3", thiefGone && survivorSeated && repairLine,
                        $"idle stack AUTO-repaired (thiefGone={thiefGone} survivorSeated={survivorSeated} line=\"{last}\")");

                    // P4: gate matrix on cluster D (pure mixed, no theft): idle mixed wiring is left
                    // alone; demand or supply alone escalates.
                    var dnet = _mwS5?.CableNetwork;
                    bool dMerged = MwAlive(_mwS5) && dnet != null && ReferenceEquals(dnet, _mwN5.CableNetwork);
                    if (dMerged)
                    {
                        string idle = MwGateKind(dnet, 0f, 0f, 0f);
                        string demand = MwGateKind(dnet, 5f, 0f, 0f);
                        string supply = MwGateKind(dnet, 0f, 0f, 5f);
                        MwCheck("P4", idle == "None" && demand == "Mixed" && supply == "Mixed",
                            $"gate matrix on pure-mixed idle={idle} demandOnly={demand} supplyOnly={supply}");
                    }
                    else
                    {
                        MwCheck("P4", false, "gate matrix skipped: cluster D did not merge");
                    }

                    // Forge the orphan: empty E's cell slot directly (the seated-thief-destroyed
                    // aftermath) and let the backstop repair it. The direct field write fires none of
                    // the eviction hooks (in production the aftermath always rides a cable destroy,
                    // whose Remove postfix evicts cache-wide), so evict the forged net explicitly.
                    // Evict + kick the recheck exactly like the production destroy path does (E's net is
                    // device-less, so the per-tick backstop cannot see it; only the recheck can).
                    MwEnqueue(() =>
                    {
                        if (_mwN6 == null || _mwN6.SmallCell == null) return;
                        _mwN6.SmallCell.Cable = null;
                        if (_mwN6.CableNetwork != null)
                            _mwInvalidateNet.Invoke(null, new object[] { _mwN6.CableNetwork.ReferenceId });
                        _mwRequestRecheck.Invoke(null, null);
                    });
                    _mwWait = 5;
                    _mwStep = 6;
                    break;
                }

                case 6:
                {
                    // P5: the orphan was auto-re-seated and its network rebuilt (merged back with N7).
                    string last = MwLastBroadcast();
                    bool reseated = MwAlive(_mwN6) && _mwN6.SmallCell != null && ReferenceEquals(_mwN6.SmallCell.Cable, _mwN6);
                    bool merged = reseated && _mwN6.CableNetwork != null && ReferenceEquals(_mwN6.CableNetwork, _mwN7.CableNetwork);
                    bool line = last != null && last.Contains("Repaired orphaned normal cable");
                    MwCheck("P5", reseated && merged && line,
                        $"orphan AUTO-re-seated (reseated={reseated} mergedHome={merged} line=\"{last}\")");

                    // P5b: cluster C, now pure mixed and idle, was NOT burned by the backstop (the
                    // activity gate holds for burns).
                    bool cIntact = MwAlive(_mwN4) && MwAlive(_mwS3) && MwMixed(_mwN4.CableNetwork);
                    MwCheck("P5b", cIntact, $"idle pure-mixed cluster left unburned (intact={cIntact})");

                    MwEnqueue(() => _mwResolveMixed.Invoke(null, new object[] { _mwN4?.CableNetwork, null }));
                    _mwWait = 3;
                    _mwStep = 7;
                    break;
                }

                case 7:
                {
                    // The burn's Remove postfix schedules a recheck that can legitimately land another
                    // broadcast (e.g. a late orphan repair) after the burn line, so the line assert
                    // accepts either being last; the burn itself is proven by N4's destruction.
                    string last = MwLastBroadcast();
                    bool burned = !MwAlive(_mwN4);
                    bool survivor = MwAlive(_mwS3);
                    bool line = last != null && (last.Contains("burned normal cable joining a super heavy network")
                                                 || last.Contains("Repaired orphaned"));
                    MwCheck("P6", burned && survivor && line,
                        $"boundary burn (burned={burned} survivor={survivor} line=\"{last}\")");
                    MwEnqueue(MwCleanupMainThread);
                    _mwWait = 2;
                    _mwStep = 8;
                    break;
                }

                case 8:
                    MwFinish();
                    break;
            }
        }

        private static void MwStep0Seams()
        {
            var asm = GetModAssembly(PGP_ASSEMBLY);
            var missing = new List<string>();

            var guardT = asm.GetType("PowerGridPlus.Patches.WiringGuard");
            _mwSuspendedF = guardT?.GetField("Suspended", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            if (_mwSuspendedF == null) missing.Add("WiringGuard.Suspended");

            var consoleT = asm.GetType("PowerGridPlus.PlayerConsole");
            _mwLastBroadcastF = consoleT?.GetField("LastBroadcast", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            if (_mwLastBroadcastF == null) missing.Add("PlayerConsole.LastBroadcast");

            var enfT = asm.GetType("PowerGridPlus.VoltageTierEnforcer");
            _mwNetRowT = asm.GetType("PowerGridPlus.Core.GridSnapshot+NetRow");
            _mwGetTierInfo = enfT?.GetMethod("GetTierInfo", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            _mwDetectNetRow = _mwNetRowT == null ? null : enfT?.GetMethod(
                "DetectViolation", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static,
                null, new[] { _mwNetRowT }, null);
            _mwTierInfoMixedF = asm.GetType("PowerGridPlus.VoltageTierEnforcer+TierInfo")
                ?.GetField("Mixed", BindingFlags.Public | BindingFlags.Instance);
            _mwViolationKindF = asm.GetType("PowerGridPlus.VoltageTierEnforcer+TierViolation")
                ?.GetField("Kind", BindingFlags.Public | BindingFlags.Instance);
            if (_mwGetTierInfo == null) missing.Add("VoltageTierEnforcer.GetTierInfo");
            if (_mwDetectNetRow == null) missing.Add("VoltageTierEnforcer.DetectViolation(NetRow)");
            if (_mwTierInfoMixedF == null) missing.Add("TierInfo.Mixed");
            if (_mwViolationKindF == null) missing.Add("TierViolation.Kind");

            _mwNrNetworkF = _mwNetRowT?.GetField("Network");
            _mwNrRigidF = _mwNetRowT?.GetField("RigidDemand");
            _mwNrReqF = _mwNetRowT?.GetField("RequiredSum");
            _mwNrPotF = _mwNetRowT?.GetField("PotentialSum");
            if (_mwNrNetworkF == null || _mwNrRigidF == null || _mwNrReqF == null || _mwNrPotF == null)
                missing.Add("NetRow fields");

            var tierT = asm.GetType("PowerGridPlus.VoltageTier");
            _mwResolveMixed = tierT?.GetMethod("ResolveMixedTierNetwork", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            if (_mwResolveMixed == null) missing.Add("VoltageTier.ResolveMixedTierNetwork");

            _mwInvalidateNet = enfT?.GetMethod("InvalidateNet", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            if (_mwInvalidateNet == null) missing.Add("VoltageTierEnforcer.InvalidateNet");
            _mwRequestRecheck = enfT?.GetMethod("RequestRecheck", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            if (_mwRequestRecheck == null) missing.Add("VoltageTierEnforcer.RequestRecheck");

            if (!UnityMainThreadDispatcher.Exists())
                missing.Add("UnityMainThreadDispatcher instance");

            bool ok = missing.Count == 0;
            MwCheck("P0", ok, ok ? "all seams resolved" : "missing: " + string.Join(", ", missing));
            if (!ok) { MwFinish(); return; }

            _log?.LogInfo("[ScenarioRunner] MWF START mixedwire-fixture");
            MwEnqueue(() => _mwN1 = MwSpawnCable("StructureCableStraight", MwPosA));
            _mwWait = 2;
            _mwStep = 1;
        }

        private static void MwEnqueue(Action action)
        {
            if (!UnityMainThreadDispatcher.Exists())
                throw new InvalidOperationException("UnityMainThreadDispatcher does not exist");
            _mwBusy = true;
            UnityMainThreadDispatcher.Instance().Enqueue(() =>
            {
                try { action(); }
                catch (Exception e) { _mwActionError = e.Message; }
                finally { _mwBusy = false; }
            });
        }

        // Main thread only (called from enqueued actions).
        private static Cable MwSpawnCable(string prefabName, Vector3 pos)
        {
            var prefab = Prefab.Find(prefabName) as Structure;
            if (prefab == null) throw new Exception($"prefab '{prefabName}' not found");
            var thing = OnServer.Create<Structure>(prefab, pos, Quaternion.identity);
            var cable = thing as Cable;
            if (cable == null) throw new Exception($"'{prefabName}' spawned as {thing?.GetType().Name ?? "null"}, not Cable");
            _mwSpawned.Add(cable);
            return cable;
        }

        private static bool MwAlive(Cable cable)
            => cable != null && !cable.IsBeingDestroyed && Referencable.Find<Cable>(cable.ReferenceId) != null;

        private static bool MwMixed(CableNetwork net)
        {
            if (net == null) return false;
            var info = _mwGetTierInfo.Invoke(null, new object[] { net });
            return (bool)_mwTierInfoMixedF.GetValue(info);
        }

        private static string MwLastBroadcast() => _mwLastBroadcastF.GetValue(null) as string;

        private static string MwGateKind(CableNetwork net, float rigid, float required, float potential)
        {
            var nr = Activator.CreateInstance(_mwNetRowT);
            _mwNrNetworkF.SetValue(nr, net);
            _mwNrRigidF.SetValue(nr, rigid);
            _mwNrReqF.SetValue(nr, required);
            _mwNrPotF.SetValue(nr, potential);
            var violation = _mwDetectNetRow.Invoke(null, new[] { nr });
            return _mwViolationKindF.GetValue(violation)?.ToString() ?? "null";
        }

        private static bool MwKitNear(Vector3 pos)
        {
            // The refunded coil is a DynamicThing spawned mid-air at the cable position; by assert
            // time it is falling, so match horizontally and allow a long drop below the spawn point.
            bool found = false;
            OcclusionManager.AllThings.ForEach(t =>
            {
                if (found || t == null || t.IsBeingDestroyed) return;
                if (!(t is Item) || t is Structure) return;
                var d = t.ThingTransformPosition - pos;
                if (d.x * d.x + d.z * d.z < 1f && d.y > -300f && d.y < 2f) found = true;
            });
            return found;
        }

        // Main thread only.
        private static void MwCleanupMainThread()
        {
            _mwSuspendedF.SetValue(null, false);
            foreach (var thing in _mwSpawned)
            {
                if (thing == null || thing.IsBeingDestroyed) continue;
                if (Referencable.Find<Thing>(thing.ReferenceId) == null) continue;
                OnServer.Destroy(thing);
            }
            // Sweep any kit items the refusals dropped at the fixture column.
            var spots = new[] { MwPosA, MwPosA2, MwPosB, MwPosC, MwPosC2 };
            var kits = new List<Thing>();
            OcclusionManager.AllThings.ForEach(t =>
            {
                if (t == null || t.IsBeingDestroyed || !(t is Item) || t is Structure) return;
                foreach (var s in spots)
                {
                    if ((t.ThingTransformPosition - s).sqrMagnitude < 1f) { kits.Add(t); return; }
                }
            });
            foreach (var kit in kits) OnServer.Destroy(kit);
        }

        private static void MwFinish()
        {
            _log?.LogInfo($"[ScenarioRunner] MWF SUMMARY pass={_mwPass} fail={_mwFail}");
            _mwStep = MwDone;
        }

        // Scenario: pgp-mixedwire-survey
        //
        // Passive observer for a heal run on a corrupted save: every 10th simulation tick, list
        // every cable network whose tier scan reports Mixed or StackedTheft, with cable count,
        // published load fields (what the pre-burn re-detect can see), and the split-pending
        // state. Read-only; the mod's own enforcement does the healing.
        private static int _mwsTicks;
        private static FieldInfo _mwTierInfoTheftF;
        private static MethodInfo _mwIsPending;

        private static void Scenario_PgpMixedWireSurvey()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-mixedwire-survey")) return;
            if (!GameManager.RunSimulation) return;

            if (_mwGetTierInfo == null || _mwTierInfoTheftF == null || _mwIsPending == null)
            {
                var asm = GetModAssembly(PGP_ASSEMBLY);
                var enfT = asm.GetType("PowerGridPlus.VoltageTierEnforcer");
                _mwGetTierInfo = enfT?.GetMethod("GetTierInfo", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                var infoT = asm.GetType("PowerGridPlus.VoltageTierEnforcer+TierInfo");
                _mwTierInfoMixedF = infoT?.GetField("Mixed");
                _mwTierInfoTheftF = infoT?.GetField("StackedTheft");
                _mwIsPending = asm.GetType("PowerGridPlus.SplitPendingRegistry")
                    ?.GetMethod("IsPending", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                if (_mwGetTierInfo == null || _mwTierInfoMixedF == null || _mwTierInfoTheftF == null || _mwIsPending == null)
                {
                    _log?.LogError("[ScenarioRunner] MWS seams unresolved; survey disabled.");
                    _mwsTicks = int.MinValue;
                    return;
                }
            }
            if (_mwsTicks == int.MinValue) return;
            _mwsTicks++;
            if (_mwsTicks % 10 != 1) return;

            int flagged = 0;
            CableNetwork.AllCableNetworks.ForEach(net =>
            {
                if (net == null) return;
                var info = _mwGetTierInfo.Invoke(null, new object[] { net });
                bool mixed = (bool)_mwTierInfoMixedF.GetValue(info);
                bool theft = (bool)_mwTierInfoTheftF.GetValue(info);
                if (!mixed && !theft) return;
                flagged++;
                int count;
                lock (net.CableList) count = net.CableList.Count;
                bool pending = (bool)_mwIsPending.Invoke(null, new object[] { net.ReferenceId });
                _log?.LogInfo($"[ScenarioRunner] MWS tick={_mwsTicks} net={net.ReferenceId} cables={count} mixed={mixed} theft={theft} pot={net.PotentialLoad:0} req={net.RequiredLoad:0} pending={pending}");
            });
            _log?.LogInfo($"[ScenarioRunner] MWS tick={_mwsTicks} flaggedNets={flagged}");
        }
    }
}
