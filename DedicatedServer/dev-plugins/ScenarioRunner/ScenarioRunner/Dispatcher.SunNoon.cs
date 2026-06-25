using System;
using System.Linq;
using System.Reflection;
using Assets.Scripts;          // OcclusionManager -> handle to Assembly-CSharp
using UnityEngine;             // Vector3, Mathf

namespace ScenarioRunner
{
    // Scenario: sun-noon
    //
    // Pins the world sun at its highest point (zenith / "noon") and freezes it there for the
    // whole session, including across a reload (the scenario re-arms every tick). Built for
    // walking a save with constant lighting / full solar while reproducing bugs.
    //
    // Mechanism (see Research/GameClasses/OrbitalSimulation.md):
    //   * The sun has no stored angle. Its direction is OrbitalSimulation.WorldSunVector,
    //     recomputed from the orbital clock (SimulationTimeSeconds) every frame and scaled by
    //     TimeScale. Noon = WorldSunVector.y at its maximum (sun nearest Vector3.up). Freeze =
    //     TimeScale == 0 (UpdateAllBodies advances the clock by delta * TimeScale).
    //   * SimulationTimeSeconds (SerializeDeltaState bit 2) AND TimeScale (bit 1) are both
    //     network-synced to clients, so a connected client sees the same frozen noon; the
    //     freeze is host-authoritative.
    //
    // Threading: this fires from the ElectricityTick UniTask worker, NOT the Unity main thread
    // (Research/Patterns/ThingEnumerationOffMainThread.md). The private SetAllBodies(double) is
    // pure managed math (sets SimulationTimeSeconds + WorldSunVector, no Unity scene-graph
    // writes), so the zenith scan is worker-safe. We deliberately do NOT call the public
    // SetSimulationTime(double): it also runs HandleUpdate(), which writes
    // WorldSunTransform.position / .LookAt (Unity, main-thread only). We also set TimeScale = 0
    // BEFORE scanning, so any concurrent main-thread UpdateEachFrame is idempotent on our
    // SimulationTimeSeconds writes (delta * 0 == 0) and the scan cannot be raced.
    //
    // Reflection only (no build-time dependency on the celestial types). Resolves
    // OrbitalSimulation off the Assembly-CSharp handle once and caches the members.
    internal static partial class Dispatcher
    {
        private static bool _sunReflectionTried;
        private static bool _sunReflectionOk;
        private static object _sunSystem;             // OrbitalSimulation.System (the singleton)
        private static FieldInfo _sunWorldSunVectorF; // static Vector3 WorldSunVector
        private static FieldInfo _sunSimTimeF;        // double SimulationTimeSeconds (instance)
        private static PropertyInfo _sunTimeScaleP;   // double TimeScale (instance, get)
        private static PropertyInfo _sunIsValidP;     // static bool IsValid
        private static MethodInfo _sunSetAllBodiesM;  // void SetAllBodies(double) (instance, private)
        private static MethodInfo _sunSetTimeScaleM;  // static void SetTimeScale(float)
        private static readonly object[] _sunArg1 = new object[1];

        private static bool _sunNoonDone;

        private static void Scenario_SunNoon()
        {
            if (!EnsureSunReflection()) return;

            try
            {
                if (!SunIsValid()) return;   // wait until the simulation is live

                if (!_sunNoonDone)
                {
                    _sunNoonDone = true;
                    SunNoon_FreezeThenScan();
                }
                else
                {
                    // Cheap per-tick re-arm: hold the freeze even if a world rebuild or a
                    // join re-sync nudges the scale off zero.
                    double ts = SunTimeScale();
                    if (Math.Abs(ts) > 1e-9)
                    {
                        SunSetTimeScaleZero();
                        _log?.LogInfo($"[ScenarioRunner] sun-noon re-armed TimeScale=0 (was {ts:F4})");
                    }
                }
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] sun-noon threw: {e}");
            }
        }

        private static void SunNoon_FreezeThenScan()
        {
            // 1) Freeze first, so the worker-thread scan below cannot fight a concurrent
            //    main-thread UpdateEachFrame (delta * 0 == 0 -> idempotent on our writes).
            SunSetTimeScaleZero();

            double baseTime = SunSimTime();
            float startY = SunVector().y;

            // 2) Scan forward for the SimulationTimeSeconds that maximizes WorldSunVector.y.
            //    A Lunar solar day is a few hundred sim-seconds; SCAN_SPAN covers many days,
            //    so the global max over the window is the true zenith regardless of phase.
            const double SCAN_SPAN = 10000.0;
            const int COARSE_STEPS = 4000;
            double coarseStep = SCAN_SPAN / COARSE_STEPS;

            double bestOffset = 0.0;
            float bestY = float.NegativeInfinity;
            for (int i = 0; i <= COARSE_STEPS; i++)
            {
                double off = i * coarseStep;
                SunSetAllBodies(baseTime + off);
                float y = SunVector().y;
                if (y > bestY) { bestY = y; bestOffset = off; }
            }

            // 3) Refine around the coarse best.
            const int FINE_STEPS = 200;
            double lo = bestOffset - coarseStep;
            double hi = bestOffset + coarseStep;
            double fineStep = (hi - lo) / FINE_STEPS;
            for (int i = 0; i <= FINE_STEPS; i++)
            {
                double off = lo + i * fineStep;
                SunSetAllBodies(baseTime + off);
                float y = SunVector().y;
                if (y > bestY) { bestY = y; bestOffset = off; }
            }

            // 4) Land on zenith; re-confirm the freeze.
            double noonTime = baseTime + bestOffset;
            SunSetAllBodies(noonTime);
            Vector3 v = SunVector();
            float elevDeg = Mathf.Asin(Mathf.Clamp(v.y, -1f, 1f)) * Mathf.Rad2Deg;
            if (Math.Abs(SunTimeScale()) > 1e-9) SunSetTimeScaleZero();

            _log?.LogInfo(
                $"[ScenarioRunner] sun-noon SET baseSimTime={baseTime:F2} startSunY={startY:F4} -> " +
                $"noonSimTime={noonTime:F2} (offset={bestOffset:F2}) sunVec=({v.x:F3},{v.y:F3},{v.z:F3}) " +
                $"elevation={elevDeg:F2}deg sunY={v.y:F4} TimeScale={SunTimeScale():F4} (frozen). " +
                "SimulationTime + TimeScale are network-synced to the client.");
        }

        // ---- reflection helpers ----

        private static bool EnsureSunReflection()
        {
            if (_sunReflectionTried) return _sunReflectionOk;
            _sunReflectionTried = true;
            try
            {
                var asm = typeof(OcclusionManager).Assembly;
                Type t = asm.GetType("Assets.Scripts.OrbitalSimulation");
                if (t == null)
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException rtle) { types = rtle.Types.Where(x => x != null).ToArray(); }
                    t = types.FirstOrDefault(x => x != null && x.Name == "OrbitalSimulation");
                }
                if (t == null)
                {
                    _log?.LogError("[ScenarioRunner] sun-noon: OrbitalSimulation type not found");
                    return false;
                }

                const BindingFlags SP = BindingFlags.Static | BindingFlags.Public;
                const BindingFlags IP = BindingFlags.Instance | BindingFlags.Public;
                const BindingFlags IM = BindingFlags.Instance | BindingFlags.NonPublic;

                _sunSystem = t.GetProperty("System", SP)?.GetValue(null);
                _sunIsValidP = t.GetProperty("IsValid", SP);
                _sunWorldSunVectorF = t.GetField("WorldSunVector", SP);
                _sunSimTimeF = t.GetField("SimulationTimeSeconds", IP);
                _sunTimeScaleP = t.GetProperty("TimeScale", IP);
                _sunSetAllBodiesM = t.GetMethod("SetAllBodies", IM, null, new[] { typeof(double) }, null);
                _sunSetTimeScaleM = t.GetMethod("SetTimeScale", SP, null, new[] { typeof(float) }, null);

                _sunReflectionOk =
                    _sunSystem != null && _sunWorldSunVectorF != null && _sunSimTimeF != null &&
                    _sunTimeScaleP != null && _sunSetAllBodiesM != null && _sunSetTimeScaleM != null;

                if (!_sunReflectionOk)
                    _log?.LogError(
                        $"[ScenarioRunner] sun-noon reflection incomplete: system={_sunSystem != null} " +
                        $"wsv={_sunWorldSunVectorF != null} simT={_sunSimTimeF != null} ts={_sunTimeScaleP != null} " +
                        $"setAllBodies={_sunSetAllBodiesM != null} setTimeScale={_sunSetTimeScaleM != null}");

                return _sunReflectionOk;
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] sun-noon reflection threw: {e}");
                return false;
            }
        }

        private static bool SunIsValid()
        {
            try
            {
                if (_sunIsValidP == null) return true;
                return _sunIsValidP.GetValue(null) is bool b && b;
            }
            catch { return true; }
        }

        private static double SunSimTime()
        {
            try { return _sunSimTimeF.GetValue(_sunSystem) is double d ? d : 0.0; }
            catch { return 0.0; }
        }

        private static double SunTimeScale()
        {
            try { return _sunTimeScaleP.GetValue(_sunSystem) is double d ? d : 0.0; }
            catch { return 0.0; }
        }

        private static Vector3 SunVector()
        {
            try { return _sunWorldSunVectorF.GetValue(null) is Vector3 v ? v : Vector3.zero; }
            catch { return Vector3.zero; }
        }

        private static void SunSetAllBodies(double t)
        {
            _sunArg1[0] = t;
            _sunSetAllBodiesM.Invoke(_sunSystem, _sunArg1);
        }

        private static void SunSetTimeScaleZero()
        {
            _sunArg1[0] = 0f;
            _sunSetTimeScaleM.Invoke(null, _sunArg1);
        }
    }
}
