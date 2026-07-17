using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Util;
using UnityEngine;

namespace ScenarioRunner
{
    // Scenario: pgp-fresh-device-trace
    //
    // Root-cause trace for the fresh-device 60 s darkness report (2026-07-15): a newly
    // constructed device behind a healthy transformer stays unpowered for ~60 seconds, then
    // works forever. Signature matches PowerGridPlus's DEAD_UNMET hold (NetLiveness: one
    // DEAD_UNMET tick arms a 120-tick hold on the net).
    //
    // The scenario spawns a REAL chain on a fresh world (all world mutation enqueued to the
    // Unity main thread, MixedWireFixture pattern):
    //
    //   StructureRTG -> heavy corner tap -> heavy straights -> StructureTransformerSmall
    //     -> normal straights (one mid-run 4-way junction as a vertical tap) -> consumers
    //
    // Connection law (decompile 312730-312745 + FillConnected 312896-312941): a device port
    // connects to the cable occupying the port's LocalGrid cell only when that cable has an
    // open end whose LocalGrid equals the port's FacingGrid. Port geometry is discovered by
    // probe spawns (each prefab's power ports logged as LocalGrid/FacingGrid offsets from the
    // registered body); cable pieces are oriented by a lattice-rotation solver.
    //
    // Two construction paths against the downstream (normal) net:
    //   EVENT 1 (spawn path):      spawn StructureWallLight COMPLETE above the end corner.
    //   EVENT 2 (completion path): spawn StructureConsole at CurrentBuildStateIndex=0 above
    //                              the mid-run junction, soak ~24 ticks, then set the index
    //                              to final on the main thread (the tool-stroke completion;
    //                              IsStructureCompleted flips exactly there).
    //
    // Per tick (one greppable line, prefix "FDT t="): both nets' snapshot state (RigidDemand,
    // GenSupply, row count), NetLiveness verdict + the private _deadUnmetHoldUntil entry,
    // ShortfallDiagnostics class, the transformer's TransformerSupplyCache totals + fault
    // registry locks + live OnOff/Error, and each consumer's live state (row demand,
    // IsStructureCompleted, OnOff, Powered). On any tick where the downstream net carries
    // rigid demand but is not LIVE, a full row dump fires (capped).
    //
    // Managed-state reflection only on the tick worker; world mutation only via enqueued
    // main-thread actions. Everything spawned is destroyed in the final step.
    internal static partial class Dispatcher
    {
        private const string FDT = "[ScenarioRunner] FDT";
        private const int FdSettleTicks = 12;
        private const int FdWatch1Ticks = 40;    // event 1 window (spawn path proven clean; short)
        private const int FdIncubateTicks = 24;  // event 2: incomplete-on-net soak
        private const int FdWatch2Ticks = 40;    // event 2 window (completion path proven clean; short)
        private const int FdStubSoakTicks = 20;  // event 3: isolated demand stub soak (hold re-arm)
        private const int FdWatch3Ticks = 150;   // event 3 window (must cover the 120-tick hold)
        private const int FdDumpCap = 24;

        private static int _fdStep;
        private static int _fdWait;
        private static int _fdPhaseTicks;
        private static volatile bool _fdBusy;
        private static volatile string _fdErr;
        private static bool _fdDone;
        private static int _fdDumps;

        private sealed class FdPort
        {
            public ConnectionRole Role;
            public Vector3 LocalOff;
            public Vector3 FacingOff;
        }

        private sealed class FdPortInfo
        {
            public string Prefab;
            public Vector3 Body;
            public readonly List<FdPort> Ports = new List<FdPort>();
            public int BuildStates;
            public FdPort Input;    // role Input, else first
            public FdPort Output;   // role Output
        }

        private static readonly Dictionary<string, FdPortInfo> _fdProbe = new Dictionary<string, FdPortInfo>();
        private static readonly List<Thing> _fdSpawned = new List<Thing>();
        private static readonly List<Thing> _fdProbeThings = new List<Thing>();

        private static Transformer _fdXf;
        private static Device _fdRtg;
        private static Device _fdLight1, _fdLight2, _fdLight3;
        private static string _fdConsumerA, _fdConsumerB;
        private static Vector3 _fdL1Pos, _fdL2Pos, _fdL3Pos;
        private static Quaternion _fdL1Rot = Quaternion.identity, _fdL2Rot = Quaternion.identity;
        private static int _fdEvent1Tick = -1, _fdEvent2SpawnTick = -1, _fdEvent2Tick = -1;
        private static int _fdEvent3StubTick = -1, _fdEvent3BridgeTick = -1;
        // Stub geometry, computed at build time; the bridge cell stays empty until EVENT 3b.
        private static Vector3 _fdOutAxis, _fdBridgeCell;
        private static Vector3[] _fdStubStraightCells;
        private static Vector3 _fdStubCornerCell;
        private static Quaternion _fdStubCornerRot;

        // PGP reflection seams (resolved once).
        private static bool _fdSeamsOk;
        private static PropertyInfo _fdSnapCurrent;      // GridSnapshot.Current
        private static FieldInfo _fdSnapById;            // GridSnapshot.ById
        private static FieldInfo _fdNrRigid, _fdNrGen, _fdNrRows, _fdNrWeakest;
        private static FieldInfo _fdRowDevice, _fdRowDemand, _fdRowGenerated, _fdRowSeg, _fdRowOnOff, _fdRowError;
        private static MethodInfo _fdTryVerdict;         // NetLiveness.TryGetVerdict(long, out byte)
        private static FieldInfo _fdHoldUntil;           // NetLiveness._deadUnmetHoldUntil
        private static PropertyInfo _fdPublishedTick;    // NetLiveness.PublishedTick
        private static MethodInfo _fdTscOut, _fdTscIn;   // TransformerSupplyCache.TryGetOutput/TryGetInputDraw
        private static MethodInfo _fdTryClassify;        // ShortfallDiagnostics.TryClassify(long, out byte)
        private static PropertyInfo _fdTick;             // ElectricityTickCounter.CurrentTick
        private static MethodInfo _fdDepriLocked, _fdOverLocked, _fdCableLocked, _fdCycleLocked, _fdVvfLocked;

        private static void Scenario_PgpFreshDeviceTrace()
        {
            if (_fdDone) return;
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-fresh-device-trace")) return;
            if (!GameManager.RunSimulation) return;

            try
            {
                FdTick();
            }
            catch (Exception e)
            {
                _log?.LogError($"{FDT} step {_fdStep} threw: {e}");
                _fdDone = true;
            }
        }

        private static void FdTick()
        {
            if (_fdErr != null)
            {
                _log?.LogError($"{FDT} main-thread action failed: {_fdErr}");
                _fdErr = null;
                _fdDone = true;
                return;
            }
            if (_fdBusy) { FdTrace(); return; }
            if (_fdWait > 0) { _fdWait--; FdTrace(); return; }

            switch (_fdStep)
            {
                case 0:
                    if (!FdResolveSeams()) { _fdDone = true; return; }
                    FdEnqueue(FdProbeSpawnMainThread);
                    _fdWait = 2;
                    _fdStep = 1;
                    break;

                case 1:
                    FdEnqueue(FdBuildChainMainThread);
                    _fdWait = 3;
                    _fdStep = 2;
                    _fdPhaseTicks = 0;
                    break;

                case 2:
                    FdTrace();
                    if (++_fdPhaseTicks >= FdSettleTicks)
                    {
                        FdEnqueue(FdSpawnLight1MainThread);
                        _fdStep = 3;
                        _fdPhaseTicks = 0;
                        _fdWait = 1;
                    }
                    break;

                case 3:
                    FdTrace();
                    if (++_fdPhaseTicks >= FdWatch1Ticks)
                    {
                        if (_fdConsumerB != null)
                        {
                            FdEnqueue(FdSpawnLight2MainThread);
                            _fdStep = 4;
                            _fdPhaseTicks = 0;
                            _fdWait = 1;
                        }
                        else
                        {
                            _log?.LogWarning($"{FDT} no multi-build-state consumer candidate found; completion path skipped.");
                            _fdStep = 6;
                        }
                    }
                    break;

                case 4:
                    FdTrace();
                    if (++_fdPhaseTicks >= FdIncubateTicks)
                    {
                        FdEnqueue(FdCompleteLight2MainThread);
                        _fdStep = 5;
                        _fdPhaseTicks = 0;
                        _fdWait = 1;
                    }
                    break;

                case 5:
                    FdTrace();
                    if (++_fdPhaseTicks >= FdWatch2Ticks)
                    {
                        FdEnqueue(FdSpawnStubMainThread);
                        _fdStep = 6;
                        _fdPhaseTicks = 0;
                        _fdWait = 1;
                    }
                    break;

                case 6:
                    FdTrace();
                    if (++_fdPhaseTicks >= FdStubSoakTicks)
                    {
                        FdEnqueue(FdBridgeStubMainThread);
                        _fdStep = 7;
                        _fdPhaseTicks = 0;
                        _fdWait = 1;
                    }
                    break;

                case 7:
                    FdTrace();
                    if (++_fdPhaseTicks >= FdWatch3Ticks) _fdStep = 8;
                    break;

                case 8:
                    FdEnqueue(FdCleanupMainThread);
                    _fdWait = 2;
                    _fdStep = 9;
                    break;

                case 9:
                    _log?.LogInfo($"{FDT} SUMMARY event1(spawn-complete)@tick={_fdEvent1Tick} " +
                                  $"event2(spawn-incomplete)@tick={_fdEvent2SpawnTick} event2(complete)@tick={_fdEvent2Tick} " +
                                  $"event3(stub)@tick={_fdEvent3StubTick} event3(bridge)@tick={_fdEvent3BridgeTick} " +
                                  $"consumerA={_fdConsumerA} consumerB={_fdConsumerB ?? "-"} DONE");
                    _fdDone = true;
                    break;
            }
        }

        // ---- seams ----

        private static bool FdResolveSeams()
        {
            var asm = GetModAssembly(PGP_ASSEMBLY);
            const BindingFlags SF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
            const BindingFlags IF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

            var snapT = asm.GetType("PowerGridPlus.Core.GridSnapshot");
            _fdSnapCurrent = snapT?.GetProperty("Current", SF);
            _fdSnapById = snapT?.GetField("ById", IF);
            var nrT = asm.GetType("PowerGridPlus.Core.GridSnapshot+NetRow");
            _fdNrRigid = nrT?.GetField("RigidDemand", IF);
            _fdNrGen = nrT?.GetField("GenSupply", IF);
            _fdNrRows = nrT?.GetField("Rows", IF);
            _fdNrWeakest = nrT?.GetField("WeakestCap", IF);
            var rowT = asm.GetType("PowerGridPlus.Core.GridSnapshot+DeviceRow");
            _fdRowDevice = rowT?.GetField("Device", IF);
            _fdRowDemand = rowT?.GetField("Demand", IF);
            _fdRowGenerated = rowT?.GetField("Generated", IF);
            _fdRowSeg = rowT?.GetField("IsSegmenter", IF);
            _fdRowOnOff = rowT?.GetField("OnOff", IF);
            _fdRowError = rowT?.GetField("Error", IF);

            var liveT = asm.GetType("PowerGridPlus.NetLiveness");
            _fdTryVerdict = liveT?.GetMethod("TryGetVerdict", SF);
            _fdHoldUntil = liveT?.GetField("_deadUnmetHoldUntil", SF);
            _fdPublishedTick = liveT?.GetProperty("PublishedTick", SF);

            var tscT = asm.GetType("PowerGridPlus.TransformerSupplyCache");
            _fdTscOut = tscT?.GetMethod("TryGetOutput", SF);
            _fdTscIn = tscT?.GetMethod("TryGetInputDraw", SF);

            _fdTryClassify = asm.GetType("PowerGridPlus.ShortfallDiagnostics")?.GetMethod("TryClassify", SF);
            _fdTick = asm.GetType("PowerGridPlus.ElectricityTickCounter")?.GetProperty("CurrentTick", SF);

            _fdDepriLocked = asm.GetType("PowerGridPlus.DeprioritizedRegistry")
                ?.GetMethod("IsLockedOut", SF, null, new[] { typeof(long), typeof(int) }, null);
            _fdOverLocked = asm.GetType("PowerGridPlus.OverloadRegistry")
                ?.GetMethod("IsLockedOut", SF, null, new[] { typeof(long), typeof(int) }, null);
            _fdCableLocked = asm.GetType("PowerGridPlus.CableOverloadRegistry")
                ?.GetMethod("IsLockedOut", SF, null, new[] { typeof(long), typeof(int) }, null);
            _fdCycleLocked = asm.GetType("PowerGridPlus.CycleFaultRegistry")
                ?.GetMethod("IsCycleFaulted", SF, null, new[] { typeof(long), typeof(int) }, null);
            _fdVvfLocked = asm.GetType("PowerGridPlus.CurrentMismatchFaultRegistry")
                ?.GetMethod("IsLockedOut", SF, null, new[] { typeof(long), typeof(int) }, null);

            _fdSeamsOk = _fdSnapCurrent != null && _fdSnapById != null && _fdNrRigid != null && _fdNrRows != null
                && _fdRowDevice != null && _fdRowDemand != null && _fdTryVerdict != null && _fdHoldUntil != null
                && _fdTscOut != null && _fdTscIn != null && _fdTick != null;
            _log?.LogInfo($"{FDT} seams: snap={_fdSnapCurrent != null} byId={_fdSnapById != null} verdict={_fdTryVerdict != null} " +
                          $"hold={_fdHoldUntil != null} tsc={_fdTscOut != null}/{_fdTscIn != null} classify={_fdTryClassify != null} " +
                          $"tick={_fdTick != null} locks={_fdDepriLocked != null}{_fdOverLocked != null}{_fdCableLocked != null}{_fdCycleLocked != null}{_fdVvfLocked != null} ok={_fdSeamsOk}");
            if (!UnityMainThreadDispatcher.Exists())
            {
                _log?.LogError($"{FDT} UnityMainThreadDispatcher missing; cannot spawn.");
                return false;
            }
            return _fdSeamsOk;
        }

        private static void FdEnqueue(Action action)
        {
            _fdBusy = true;
            UnityMainThreadDispatcher.Instance().Enqueue(() =>
            {
                try { action(); }
                catch (Exception e) { _fdErr = e.ToString(); }
                finally { _fdBusy = false; }
            });
        }

        // ---- main-thread: probe ----

        private static readonly string[] FdConsumerCandidates =
            { "StructureWallLight", "StructureGrowLight", "StructureConsole", "StructureWallCooler", "StructureWallHeater" };

        private static readonly string[] FdCablePieceCandidates =
        {
            "StructureCableCorner", "StructureCableCornerH",
            "StructureCableJunction", "StructureCableJunctionH",
            "StructureCableJunction4", "StructureCableJunctionH4",
            "StructureCableStraight", "StructureCableStraightH",
        };

        private static Thing FdSpawn(string prefabName, Vector3 pos, Quaternion rot, List<Thing> track)
        {
            var prefab = Prefab.Find(prefabName) as Structure;
            if (prefab == null) throw new Exception($"prefab '{prefabName}' not found");
            var thing = OnServer.Create<Structure>(prefab, pos, rot);
            if (thing == null) throw new Exception($"'{prefabName}' spawned null");
            track.Add(thing);
            return thing;
        }

        private static void FdProbeOne(string prefabName, Vector3 pos)
        {
            var thing = FdSpawn(prefabName, pos, Quaternion.identity, _fdProbeThings);
            var grid = thing as SmallGrid;
            var st = thing as Structure;
            var info = new FdPortInfo { Prefab = prefabName, Body = thing.ThingTransformPosition, BuildStates = st?.BuildStates?.Count ?? -1 };
            var parts = new List<string>();
            if (grid != null && grid.OpenEnds != null)
            {
                foreach (var conn in grid.OpenEnds)
                {
                    if (conn == null) continue;
                    if ((conn.ConnectionType & NetworkType.Power) == NetworkType.None) continue;
                    var port = new FdPort
                    {
                        Role = conn.ConnectionRole,
                        LocalOff = conn.LocalGrid.ToVector3() - info.Body,
                        FacingOff = conn.FacingGrid.ToVector3() - info.Body,
                    };
                    info.Ports.Add(port);
                    parts.Add($"{port.Role}:{conn.ConnectionType} local=({port.LocalOff.x:0.##},{port.LocalOff.y:0.##},{port.LocalOff.z:0.##}) facing=({port.FacingOff.x:0.##},{port.FacingOff.y:0.##},{port.FacingOff.z:0.##})");
                    if (port.Role == ConnectionRole.Output) { if (info.Output == null) info.Output = port; }
                    else if (info.Input == null || port.Role == ConnectionRole.Input) info.Input = port;
                }
            }
            _fdProbe[prefabName] = info;
            _log?.LogInfo($"{FDT} PROBE {prefabName} body=({info.Body.x:0.##},{info.Body.y:0.##},{info.Body.z:0.##}) buildStates={info.BuildStates} ports=[{string.Join(" | ", parts)}]");
        }

        private static void FdProbeSpawnMainThread()
        {
            var basePos = new Vector3(440f, 400f, 400f);
            int i = 0;
            FdProbeOne("StructureRTG", basePos + new Vector3(0f, 0f, 4f * i++));
            FdProbeOne("StructureTransformerSmall", basePos + new Vector3(0f, 0f, 4f * i++));
            foreach (var c in FdConsumerCandidates)
            {
                try { FdProbeOne(c, basePos + new Vector3(0f, 0f, 4f * i)); }
                catch (Exception e) { _log?.LogInfo($"{FDT} PROBE {c} unavailable: {e.Message}"); }
                i++;
            }
            foreach (var c in FdCablePieceCandidates)
            {
                try { FdProbeOne(c, basePos + new Vector3(4f, 0f, 4f * i)); }
                catch (Exception e) { _log?.LogInfo($"{FDT} PROBE {c} unavailable: {e.Message}"); }
                i++;
            }
            foreach (var t in _fdProbeThings)
            {
                if (t != null && !t.IsBeingDestroyed) OnServer.Destroy(t);
            }
            _fdProbeThings.Clear();

            _fdConsumerA = null; _fdConsumerB = null;
            foreach (var c in FdConsumerCandidates)
            {
                FdPortInfo pi;
                if (!_fdProbe.TryGetValue(c, out pi) || pi.Input == null) continue;
                if (_fdConsumerA == null) _fdConsumerA = c;
                if (_fdConsumerB == null && pi.BuildStates > 1) _fdConsumerB = c;
            }
            _log?.LogInfo($"{FDT} PROBE chose consumerA={_fdConsumerA ?? "NONE"} consumerB={_fdConsumerB ?? "NONE(multi-state)"}");
        }

        // ---- main-thread: build chain ----

        private static readonly Vector3 FdUp = new Vector3(0f, 0.5f, 0f);
        private static readonly Vector3 FdDown = new Vector3(0f, -0.5f, 0f);

        private static Quaternion FdCableRot(Vector3 axis)
        {
            // Straights at identity run along Z; yaw 90 maps Z onto X.
            return Mathf.Abs(axis.x) > 0.5f ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.identity;
        }

        // Find a lattice rotation q such that for every pair, q * from[i] lands within tolerance
        // of to[i]. from/to carry 0.5-length cell offsets. Returns identity + false when unsolvable.
        private static bool FdSolveRot(Vector3[] from, Vector3[] to, out Quaternion rot)
        {
            foreach (var x in new[] { 0f, 90f, 180f, 270f })
                foreach (var y in new[] { 0f, 90f, 180f, 270f })
                    foreach (var z in new[] { 0f, 90f, 180f, 270f })
                    {
                        var q = Quaternion.Euler(x, y, z);
                        bool ok = true;
                        for (int i = 0; i < from.Length && ok; i++)
                            if ((q * from[i] - to[i]).sqrMagnitude > 0.01f) ok = false;
                        if (ok) { rot = q; return true; }
                    }
            rot = Quaternion.identity;
            return false;
        }

        // Pick, from a piece's identity port offsets, an assignment onto the wanted directions and
        // the rotation that realizes it (tries every permutation of the wanted subset).
        private static bool FdOrientPiece(FdPortInfo piece, Vector3[] want, out Quaternion rot)
        {
            var offs = new List<Vector3>();
            foreach (var p in piece.Ports) offs.Add(p.LocalOff);
            rot = Quaternion.identity;
            if (offs.Count < want.Length) return false;
            // permutations of indices choosing want.Length out of offs.Count
            var idx = new int[want.Length];
            return FdOrientRecurse(offs, want, idx, 0, new bool[offs.Count], ref rot);
        }

        private static bool FdOrientRecurse(List<Vector3> offs, Vector3[] want, int[] idx, int depth, bool[] used, ref Quaternion rot)
        {
            if (depth == want.Length)
            {
                var from = new Vector3[want.Length];
                for (int i = 0; i < want.Length; i++) from[i] = offs[idx[i]];
                return FdSolveRot(from, want, out rot);
            }
            for (int i = 0; i < offs.Count; i++)
            {
                if (used[i]) continue;
                used[i] = true;
                idx[depth] = i;
                if (FdOrientRecurse(offs, want, idx, depth + 1, used, ref rot)) { used[i] = false; return true; }
                used[i] = false;
            }
            return false;
        }

        private static FdPortInfo FdPiece(string name)
        {
            FdPortInfo pi;
            if (!_fdProbe.TryGetValue(name, out pi) || pi.Ports.Count == 0)
                throw new Exception($"cable piece '{name}' not probed or portless");
            return pi;
        }

        private static void FdBuildChainMainThread()
        {
            FdPortInfo rtg = _fdProbe["StructureRTG"];
            FdPortInfo xf = _fdProbe["StructureTransformerSmall"];
            if (rtg.Input == null) throw new Exception("RTG probe has no power port");
            if (xf.Input == null || xf.Output == null) throw new Exception("transformer probe missing Input/Output port");
            if (_fdConsumerA == null) throw new Exception("no consumer candidate with a power port");

            var A = new Vector3(460f, 400f, 400f);
            Vector3 dH = new Vector3(1f, 0f, 0f);   // run axis, chosen freely (RTG port is vertical)

            // RTG at identity; its port cell is A + rtg.Input.LocalOff (probed: (0,0.5,0), i.e. the
            // cell above) and its FacingGrid is the body cell, so the tap piece above must carry a
            // DOWN open end plus a horizontal end continuing the run.
            _fdRtg = (Device)FdSpawn("StructureRTG", A, Quaternion.identity, _fdSpawned);
            Vector3 tapCell = A + rtg.Input.LocalOff;

            var heavyCorner = FdPiece("StructureCableCornerH");
            Quaternion tapRot;
            Vector3 downTo = FdAxisScale(rtg.Input.FacingOff - rtg.Input.LocalOff);   // (0,-0.5,0)
            if (!FdOrientPiece(heavyCorner, new[] { downTo, 0.5f * dH }, out tapRot))
                throw new Exception("heavy corner cannot be oriented (down + run axis)");
            FdSpawn("StructureCableCornerH", tapCell, tapRot, _fdSpawned);

            var heavyCells = new List<Vector3> { tapCell };
            for (int k = 1; k <= 5; k++)
            {
                Vector3 cell = tapCell + 0.5f * k * dH;
                FdSpawn("StructureCableStraightH", cell, FdCableRot(dH), _fdSpawned);
                heavyCells.Add(cell);
            }
            Vector3 cLast = heavyCells[heavyCells.Count - 1];

            // Transformer: yaw so its INPUT port points back along the run (-dH); the input port
            // cell lands on cLast (mutual facing with cLast's forward open end).
            Vector3 xfInAxis = FdAxisScale(xf.Input.LocalOff - xf.Input.FacingOff);
            Quaternion r1;
            if (!FdSolveRot(new[] { xfInAxis }, new[] { -0.5f * dH }, out r1))
                throw new Exception("transformer input rotation unsolvable");
            Vector3 B = cLast - r1 * xf.Input.LocalOff;
            _fdXf = (Transformer)FdSpawn("StructureTransformerSmall", B, r1, _fdSpawned);

            // Normal run from the OUTPUT port cell along the rotated output axis.
            Vector3 outLocalRot = r1 * xf.Output.LocalOff;
            Vector3 outAxis = FdAxisScale(r1 * (xf.Output.LocalOff - xf.Output.FacingOff)) * 2f;   // unit axis
            if (outAxis == Vector3.zero) throw new Exception("transformer output axis unresolvable");
            Vector3 CO = B + outLocalRot;

            // Two mid-run 4-way junctions as vertical taps (m=2 consumer B, m=4 consumer A); the
            // spare down ends dangle. The run's last straight (m=5) keeps a free forward open end
            // at CO+3.0*outAxis so EVENT 3b can bridge the demand stub along the same axis.
            var junction4 = FdPiece("StructureCableJunction4");
            Quaternion jRot;
            if (!FdOrientPiece(junction4, new[] { 0.5f * outAxis, -0.5f * outAxis, FdUp }, out jRot))
                throw new Exception("4-way junction cannot be oriented (run axis + up)");
            var normCorner = FdPiece("StructureCableCorner");
            Quaternion eRot;
            if (!FdOrientPiece(normCorner, new[] { -0.5f * outAxis, FdUp }, out eRot))
                throw new Exception("normal corner cannot be oriented (back + up)");

            // Demand-stub geometry for EVENT 3: a 3-straight isolated run beyond an EMPTY bridge
            // cell, ending in an up-corner; consumer A instance lands on it at EVENT 3a. The stub
            // CABLES are spawned FIRST, before the main normal run, so the stub CableNetwork gets
            // the LOWER ReferenceId: PGP's CableNetworkMergeDeterministicPatch sorts merge input
            // by id ascending, so the stub then SURVIVES the EVENT 3b merge (the real-world shape:
            // the trunk-side net's id is refreshed by any cable cut/rebuild after the stub was
            // built). The bridge cell stays empty until EVENT 3b.
            _fdOutAxis = outAxis;
            _fdBridgeCell = CO + 0.5f * 6 * outAxis;
            _fdStubStraightCells = new[]
            {
                CO + 0.5f * 7 * outAxis,
                CO + 0.5f * 8 * outAxis,
                CO + 0.5f * 9 * outAxis,
            };
            _fdStubCornerCell = CO + 0.5f * 10 * outAxis;
            _fdStubCornerRot = eRot;
            foreach (var cell in _fdStubStraightCells)
                FdSpawn("StructureCableStraight", cell, FdCableRot(_fdOutAxis), _fdSpawned);
            FdSpawn("StructureCableCorner", _fdStubCornerCell, _fdStubCornerRot, _fdSpawned);

            Vector3 junctionCell = CO + 0.5f * 2 * outAxis;
            Vector3 junction2Cell = CO + 0.5f * 4 * outAxis;
            for (int m = 0; m <= 5; m++)
            {
                Vector3 cell = CO + 0.5f * m * outAxis;
                if (m == 2 || m == 4) FdSpawn("StructureCableJunction4", cell, jRot, _fdSpawned);
                else FdSpawn("StructureCableStraight", cell, FdCableRot(outAxis), _fdSpawned);
            }

            // Consumer poses: each consumer's port at identity points DOWN with facing = own cell
            // (probed), so the body sits directly above the tap piece at identity rotation.
            FdPortInfo ca = _fdProbe[_fdConsumerA];
            _fdL1Rot = FdConsumerRot(ca, out var l1PortDown);
            _fdL1Pos = junction2Cell - _fdL1Rot * ca.Input.LocalOff;
            _fdL3Pos = _fdStubCornerCell - _fdL1Rot * ca.Input.LocalOff;
            if (_fdConsumerB != null)
            {
                FdPortInfo cb = _fdProbe[_fdConsumerB];
                _fdL2Rot = FdConsumerRot(cb, out var l2PortDown);
                _fdL2Pos = junctionCell - _fdL2Rot * cb.Input.LocalOff;
            }

            if (_fdXf.InteractOnOff != null && !_fdXf.OnOff) OnServer.Interact(_fdXf.InteractOnOff, 1);
            _log?.LogInfo($"{FDT} BUILD rtg={_fdRtg.ReferenceId} xf={_fdXf.ReferenceId} tap=({tapCell.x:0.##},{tapCell.y:0.##},{tapCell.z:0.##}) " +
                          $"xfBody=({B.x:0.##},{B.y:0.##},{B.z:0.##}) normal0=({CO.x:0.##},{CO.y:0.##},{CO.z:0.##}) outAxis=({outAxis.x:0.#},{outAxis.y:0.#},{outAxis.z:0.#}) " +
                          $"junction=({junctionCell.x:0.##},{junctionCell.y:0.##},{junctionCell.z:0.##}) junction2=({junction2Cell.x:0.##},{junction2Cell.y:0.##},{junction2Cell.z:0.##}) " +
                          $"bridge=({_fdBridgeCell.x:0.##},{_fdBridgeCell.y:0.##},{_fdBridgeCell.z:0.##}) " +
                          $"xfOnOff={_fdXf.OnOff} xfSetting={_fdXf.Setting:0} l1@({_fdL1Pos.x:0.##},{_fdL1Pos.y:0.##},{_fdL1Pos.z:0.##}) l2@({_fdL2Pos.x:0.##},{_fdL2Pos.y:0.##},{_fdL2Pos.z:0.##})");
        }

        private static Vector3 FdAxisScale(Vector3 v)
        {
            // snap to the dominant axis, preserving 0.5 cell length
            if (v == Vector3.zero) return Vector3.zero;
            float ax = Mathf.Abs(v.x), ay = Mathf.Abs(v.y), az = Mathf.Abs(v.z);
            if (ax >= ay && ax >= az) return new Vector3(0.5f * Mathf.Sign(v.x), 0f, 0f);
            if (ay >= ax && ay >= az) return new Vector3(0f, 0.5f * Mathf.Sign(v.y), 0f);
            return new Vector3(0f, 0f, 0.5f * Mathf.Sign(v.z));
        }

        private static Quaternion FdConsumerRot(FdPortInfo info, out bool portDown)
        {
            // Want the consumer's port axis pointing DOWN (tap pieces present an UP open end).
            Vector3 axis = FdAxisScale(info.Input.LocalOff - info.Input.FacingOff);
            portDown = (axis - FdDown).sqrMagnitude < 0.01f;
            if (portDown) return Quaternion.identity;
            Quaternion q;
            if (FdSolveRot(new[] { axis }, new[] { FdDown }, out q)) return q;
            return Quaternion.identity;
        }

        // ---- main-thread: events ----

        private static void FdForceOn(Device d)
        {
            if (d == null) return;
            if (!d.OnOff && d.InteractOnOff != null) OnServer.Interact(d.InteractOnOff, 1);
        }

        private static void FdSpawnLight1MainThread()
        {
            var st = (Structure)FdSpawn(_fdConsumerA, _fdL1Pos, _fdL1Rot, _fdSpawned);
            _fdLight1 = st as Device;
            int final = st.BuildStates != null ? st.BuildStates.Count - 1 : 0;
            int idxAtSpawn = st.CurrentBuildStateIndex;
            if (st.CurrentBuildStateIndex != final)
            {
                st.CurrentBuildStateIndex = final;
                st.UpdateStateVisualizer();
            }
            FdForceOn(_fdLight1);
            _fdEvent1Tick = FdNow();
            _log?.LogInfo($"{FDT} EVENT1 spawn-complete {_fdConsumerA} ref={st.ReferenceId} idxAtSpawn={idxAtSpawn} " +
                          $"final={final} completed={st.IsStructureCompleted} onoff={(_fdLight1 != null && _fdLight1.OnOff)} " +
                          $"powered={(_fdLight1 != null && _fdLight1.Powered)} usedPower={(_fdLight1 != null ? _fdLight1.UsedPower : -1f):0.#} tick={_fdEvent1Tick}");
        }

        private static void FdSpawnLight2MainThread()
        {
            var st = (Structure)FdSpawn(_fdConsumerB, _fdL2Pos, _fdL2Rot, _fdSpawned);
            _fdLight2 = st as Device;
            int idxAtSpawn = st.CurrentBuildStateIndex;
            st.CurrentBuildStateIndex = 0;      // under-construction state, on the net
            st.UpdateStateVisualizer();
            FdForceOn(_fdLight2);
            _fdEvent2SpawnTick = FdNow();
            _log?.LogInfo($"{FDT} EVENT2a spawn-incomplete {_fdConsumerB} ref={st.ReferenceId} idxAtSpawn={idxAtSpawn} " +
                          $"forcedIdx=0 states={st.BuildStates?.Count ?? -1} completed={st.IsStructureCompleted} " +
                          $"onoff={(_fdLight2 != null && _fdLight2.OnOff)} tick={_fdEvent2SpawnTick}");
        }

        private static void FdCompleteLight2MainThread()
        {
            var st = _fdLight2 as Structure;
            if (st == null) throw new Exception("light2 missing at completion step");
            int final = st.BuildStates.Count - 1;
            st.CurrentBuildStateIndex = final;   // the tool-stroke completion write
            st.UpdateStateVisualizer();
            FdForceOn(_fdLight2);
            _fdEvent2Tick = FdNow();
            _log?.LogInfo($"{FDT} EVENT2b completed {_fdConsumerB} ref={st.ReferenceId} idx={st.CurrentBuildStateIndex} " +
                          $"completed={st.IsStructureCompleted} onoff={_fdLight2.OnOff} tick={_fdEvent2Tick}");
        }

        private static void FdSpawnStubMainThread()
        {
            // The wiring-order construction flow: the consumer is built and switched ON at the
            // end of an isolated cable stub (spawned at BUILD time, so its net id is LOWER than
            // the main run's) that does not yet reach the powered net. The stub net carries rigid
            // demand with zero supply, which the liveness formula reads as DEAD_UNMET (not
            // DEAD_NOSUPPLY), re-arming the 120-tick hold every tick.
            var st = (Structure)FdSpawn(_fdConsumerA, _fdL3Pos, _fdL1Rot, _fdSpawned);
            _fdLight3 = st as Device;
            int final = st.BuildStates != null ? st.BuildStates.Count - 1 : 0;
            if (st.CurrentBuildStateIndex != final)
            {
                st.CurrentBuildStateIndex = final;
                st.UpdateStateVisualizer();
            }
            FdForceOn(_fdLight3);
            _fdEvent3StubTick = FdNow();
            _log?.LogInfo($"{FDT} EVENT3a stub built {_fdConsumerA} ref={st.ReferenceId} completed={st.IsStructureCompleted} " +
                          $"onoff={(_fdLight3 != null && _fdLight3.OnOff)} stubNet={(_fdLight3?.PowerCableNetwork?.ReferenceId ?? -1)} tick={_fdEvent3StubTick}");
        }

        private static void FdBridgeStubMainThread()
        {
            long stubNetBefore = _fdLight3?.PowerCableNetwork?.ReferenceId ?? -1;
            long dnBefore = _fdXf?.OutputNetwork?.ReferenceId ?? -1;
            // Merge survivorship is decided by the NEW cable's OpenEnds enumeration order:
            // CableNetwork.Merge keeps ConnectedNetworks(cable)[0] (decompile 271129-271145) and
            // ConnectedNetworks fills in FillConnected order = the placed piece's OpenEnds order.
            // Orient the bridge so its FIRST open end faces the STUB side: the stub net becomes
            // the survivor and its DEAD_UNMET hold entry now covers the whole merged net.
            Quaternion rot = FdCableRot(_fdOutAxis);
            FdPortInfo straight;
            if (_fdProbe.TryGetValue("StructureCableStraight", out straight) && straight.Ports.Count > 0)
            {
                Quaternion q;
                if (FdSolveRot(new[] { FdAxisScale(straight.Ports[0].LocalOff) }, new[] { 0.5f * _fdOutAxis }, out q))
                    rot = q;
                else
                    _log?.LogWarning($"{FDT} bridge orientation solve failed; falling back to run-axis rotation");
            }
            FdSpawn("StructureCableStraight", _fdBridgeCell, rot, _fdSpawned);
            long stubNetAfter = _fdLight3?.PowerCableNetwork?.ReferenceId ?? -1;
            long dnAfter = _fdXf?.OutputNetwork?.ReferenceId ?? -1;
            _fdEvent3BridgeTick = FdNow();
            _log?.LogInfo($"{FDT} EVENT3b bridge placed at ({_fdBridgeCell.x:0.##},{_fdBridgeCell.y:0.##},{_fdBridgeCell.z:0.##}) firstEndTowardStub=true " +
                          $"stubNet {stubNetBefore}->{stubNetAfter} dnNet {dnBefore}->{dnAfter} tick={_fdEvent3BridgeTick}");
        }

        private static void FdCleanupMainThread()
        {
            foreach (var t in _fdSpawned)
            {
                if (t == null || t.IsBeingDestroyed) continue;
                if (Referencable.Find<Thing>(t.ReferenceId) == null) continue;
                OnServer.Destroy(t);
            }
            _fdSpawned.Clear();
            _log?.LogInfo($"{FDT} CLEANUP done");
        }

        // ---- worker: per-tick trace ----

        private static int FdNow()
        {
            return _fdTick != null && _fdTick.GetValue(null) is int i ? i : -1;
        }

        private static string FdVerdictName(long netId, out int holdUntil)
        {
            holdUntil = int.MinValue;
            var dict = _fdHoldUntil?.GetValue(null) as System.Collections.IDictionary;
            if (dict != null && dict.Contains(netId)) holdUntil = (int)dict[netId];
            if (_fdTryVerdict == null) return "?";
            var args = new object[] { netId, (byte)0 };
            bool has = (bool)_fdTryVerdict.Invoke(null, args);
            if (!has) return "none";
            switch ((byte)args[1])
            {
                case 1: return "LIVE";
                case 2: return "DEAD_UNMET";
                case 3: return "DEAD_NOSUPPLY";
                default: return "v" + args[1];
            }
        }

        private static object FdNetRow(long netId)
        {
            var snap = _fdSnapCurrent?.GetValue(null);
            if (snap == null) return null;
            var byId = _fdSnapById.GetValue(snap) as System.Collections.IDictionary;
            if (byId == null || !byId.Contains(netId)) return null;
            return byId[netId];
        }

        private static string FdNetBrief(string tag, long netId)
        {
            if (netId < 0) return $"{tag}[absent]";
            int hold;
            string v = FdVerdictName(netId, out hold);
            string holdS = hold == int.MinValue ? "-" : hold.ToString();
            var nr = FdNetRow(netId);
            if (nr == null) return $"{tag}[id={netId} v={v} hold={holdS} NOT-IN-SNAPSHOT]";
            float rigid = (float)_fdNrRigid.GetValue(nr);
            float gen = _fdNrGen != null ? (float)_fdNrGen.GetValue(nr) : -1f;
            var rows = _fdNrRows.GetValue(nr) as System.Collections.IList;
            string cls = "-";
            if (_fdTryClassify != null)
            {
                var args = new object[] { netId, (byte)0 };
                if ((bool)_fdTryClassify.Invoke(null, args)) cls = args[1].ToString();
            }
            return $"{tag}[id={netId} v={v} hold={holdS} rig={rigid:0.#} gen={gen:0.#} rows={rows?.Count ?? -1} cls={cls}]";
        }

        private static string FdDeviceBrief(string tag, Device d, long dnNet)
        {
            if (d == null) return $"{tag}[-]";
            string demand = "-";
            var nr = FdNetRow(dnNet);
            if (nr != null)
            {
                var rows = _fdNrRows.GetValue(nr) as System.Collections.IList;
                if (rows != null)
                {
                    foreach (var row in rows)
                    {
                        var dev = _fdRowDevice.GetValue(row) as Device;
                        if (dev != null && dev.ReferenceId == d.ReferenceId)
                        {
                            demand = ((float)_fdRowDemand.GetValue(row)).ToString("0.#");
                            break;
                        }
                    }
                }
            }
            var st = d as Structure;
            return $"{tag}[ref={d.ReferenceId} dem={demand} cmp={(st != null && st.IsStructureCompleted ? 1 : 0)} " +
                   $"on={(d.OnOff ? 1 : 0)} pow={(d.Powered ? 1 : 0)} err={d.Error}]";
        }

        private static void FdTrace()
        {
            if (_fdStep < 2 || _fdXf == null) return;
            int tick = FdNow();

            long upId = _fdXf.InputNetwork != null ? _fdXf.InputNetwork.ReferenceId : -1;
            long dnId = _fdXf.OutputNetwork != null ? _fdXf.OutputNetwork.ReferenceId : -1;

            float thr = 0f, pull = 0f;
            if (_fdTscOut != null)
            {
                var a1 = new object[] { _fdXf.ReferenceId, 0f };
                if ((bool)_fdTscOut.Invoke(null, a1)) thr = (float)a1[1];
                var a2 = new object[] { _fdXf.ReferenceId, 0f };
                if ((bool)_fdTscIn.Invoke(null, a2)) pull = (float)a2[1];
            }
            string locks = FdLock(_fdDepriLocked, _fdXf.ReferenceId, tick) + FdLock(_fdOverLocked, _fdXf.ReferenceId, tick)
                         + FdLock(_fdCableLocked, _fdXf.ReferenceId, tick) + FdLock(_fdCycleLocked, _fdXf.ReferenceId, tick)
                         + FdLock(_fdVvfLocked, _fdXf.ReferenceId, tick);

            long stubId = _fdLight3 != null && _fdLight3.PowerCableNetwork != null
                ? _fdLight3.PowerCableNetwork.ReferenceId : -1;
            string stubPart = _fdLight3 == null ? ""
                : (stubId == dnId ? " stub[merged->dn]" : " " + FdNetBrief("stub", stubId));
            string line = $"{FDT} t={tick} step={_fdStep} " +
                          FdNetBrief("up", upId) + " " + FdNetBrief("dn", dnId) + stubPart + " " +
                          $"xf[thr={thr:0.#} pull={pull:0.#} on={(_fdXf.OnOff ? 1 : 0)} err={_fdXf.Error} locksDOCCM={locks} set={_fdXf.Setting:0}] " +
                          FdDeviceBrief("L1", _fdLight1, dnId) + " " + FdDeviceBrief("L2", _fdLight2, dnId) + " " +
                          FdDeviceBrief("L3", _fdLight3, stubId);
            _log?.LogInfo(line);

            // Focus dump on any tick where the downstream net carries demand but is not LIVE.
            if (dnId >= 0 && _fdDumps < FdDumpCap)
            {
                int hold;
                string v = FdVerdictName(dnId, out hold);
                var nr = FdNetRow(dnId);
                if (nr != null && v != "LIVE")
                {
                    float rigid = (float)_fdNrRigid.GetValue(nr);
                    if (rigid > 0.01f || v == "DEAD_UNMET")
                    {
                        _fdDumps++;
                        var rows = _fdNrRows.GetValue(nr) as System.Collections.IList;
                        if (rows != null)
                        {
                            foreach (var row in rows)
                            {
                                var dev = _fdRowDevice.GetValue(row) as Device;
                                float dem = (float)_fdRowDemand.GetValue(row);
                                float gen = _fdRowGenerated != null ? (float)_fdRowGenerated.GetValue(row) : -1f;
                                bool seg = _fdRowSeg != null && (bool)_fdRowSeg.GetValue(row);
                                bool on = _fdRowOnOff != null && (bool)_fdRowOnOff.GetValue(row);
                                int err = _fdRowError != null ? (int)_fdRowError.GetValue(row) : -1;
                                var stc = dev as Structure;
                                _log?.LogInfo($"{FDT}   DUMP dn row ref={dev?.ReferenceId} {dev?.PrefabName} dem={dem:0.#} gen={gen:0.#} " +
                                              $"seg={seg} rowOn={on} rowErr={err} liveCmp={(stc != null && stc.IsStructureCompleted ? 1 : 0)} livePow={(dev != null && dev.Powered ? 1 : 0)}");
                            }
                        }
                        var upRow = FdNetRow(upId);
                        if (upRow != null)
                        {
                            var rows2 = _fdNrRows.GetValue(upRow) as System.Collections.IList;
                            if (rows2 != null)
                            {
                                foreach (var row in rows2)
                                {
                                    var dev = _fdRowDevice.GetValue(row) as Device;
                                    float dem = (float)_fdRowDemand.GetValue(row);
                                    float gen = _fdRowGenerated != null ? (float)_fdRowGenerated.GetValue(row) : -1f;
                                    bool seg = _fdRowSeg != null && (bool)_fdRowSeg.GetValue(row);
                                    _log?.LogInfo($"{FDT}   DUMP up row ref={dev?.ReferenceId} {dev?.PrefabName} dem={dem:0.#} gen={gen:0.#} seg={seg}");
                                }
                            }
                        }
                    }
                }
            }
        }

        private static string FdLock(MethodInfo m, long refId, int tick)
        {
            if (m == null) return "?";
            return (bool)m.Invoke(null, new object[] { refId, tick }) ? "1" : "0";
        }
    }
}
