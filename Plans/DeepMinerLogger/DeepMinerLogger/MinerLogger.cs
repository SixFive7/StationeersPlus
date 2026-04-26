using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Objects.Pipes;
using BepInEx.Logging;
using HarmonyLib;

namespace DeepMinerLogger
{
    // Snapshot of all tracked state for one CombustionDeepMiner at one atmospheric tick.
    // Change detection compares every field against the previously written snapshot; if any
    // field differs beyond its per-field epsilon, a new row is written.
    internal sealed class MinerSnapshot
    {
        public long Tick;

        // InternalCombustion
        public float Rpm;
        public float Stress;
        public float Throttle;
        public float CombustionLimiter;
        public bool DidCombustionLastTick;
        public bool GainedStress;
        public double TargetPressurekPa;
        public double NormalCombustionEnergyJ;

        // Chamber atmosphere
        public double ChamberTemperatureK;
        public double ChamberPressurekPa;
        public double ChamberTotalMoles;
        public double CombustionEnergyJ;
        public double RatioOxygen;
        public double RatioHydrogen;
        public double RatioSteam;
        public double RatioPollutant;
        public double RatioCarbonDioxide;
        public double RatioNitrogen;
        public double RatioNitrousOxide;
        public double RatioOzone;

        // Device state
        public bool OnOff;
        public bool Powered;
        public int Error;
        public bool IsStructureCompleted;

        // Pipe validity
        public bool IsInputValid;
        public bool IsOutputValid;
        public bool IsInput2Valid;

        // Input pipe details
        public bool InputNetworkNull;
        public int InputStructureCount;
        public bool InputAwaitingEvent;
        public double InputPipePressurekPa;
        public double InputPipeTemperatureK;
        public double InputPipeTotalMoles;

        // Output pipe details
        public bool OutputNetworkNull;
        public int OutputStructureCount;
        public bool OutputAwaitingEvent;
        public double OutputPipePressurekPa;
        public double OutputPipeTemperatureK;
        public double OutputPipeTotalMoles;

        // DeepMiner-side
        public bool ThingInTheWay;
        public bool DeepMinablesSet;
        public bool CanMineResult;
        public bool IsReachedBedRock;

        // ProgrammableChip slot (built-in miner chip)
        public bool ChipPresent;
        public int CodeErrorState;
        public bool CompilationError;

        public MinerSnapshot Clone() => (MinerSnapshot)MemberwiseClone();
    }

    internal sealed class MinerSession
    {
        private const double EpsRatio = 0.001;
        private const double EpsNormal = 0.01;
        private const double EpsLoose = 0.1;

        private readonly long _referenceId;
        private readonly string _path;
        private StreamWriter _writer;
        private MinerSnapshot _prev;
        public long TickCounter;

        public MinerSession(long referenceId, string logDir)
        {
            _referenceId = referenceId;
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            _path = Path.Combine(logDir, $"miner_{referenceId}_{stamp}.csv");
            try
            {
                _writer = new StreamWriter(new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    AutoFlush = true,
                    NewLine = "\n"
                };
                _writer.WriteLine(HeaderRow());
            }
            catch (Exception e)
            {
                MinerLogger.Log?.LogError($"Failed to open CSV '{_path}': {e}");
                _writer = null;
            }
        }

        public void Consider(MinerSnapshot snap)
        {
            if (_writer == null) return;
            if (_prev == null || HasChanged(_prev, snap))
            {
                try
                {
                    _writer.WriteLine(FormatRow(snap));
                }
                catch (Exception e)
                {
                    MinerLogger.Log?.LogError($"CSV write failed: {e}");
                }
                _prev = snap.Clone();
            }
        }

        private static bool Diff(double a, double b, double eps) => Math.Abs(a - b) > eps;

        private static bool HasChanged(MinerSnapshot a, MinerSnapshot b)
        {
            if (Diff(a.Rpm, b.Rpm, EpsNormal)) return true;
            if (Diff(a.Stress, b.Stress, EpsNormal)) return true;
            if (Diff(a.Throttle, b.Throttle, EpsNormal)) return true;
            if (Diff(a.CombustionLimiter, b.CombustionLimiter, EpsNormal)) return true;
            if (a.DidCombustionLastTick != b.DidCombustionLastTick) return true;
            if (a.GainedStress != b.GainedStress) return true;
            if (Diff(a.TargetPressurekPa, b.TargetPressurekPa, EpsNormal)) return true;
            if (Diff(a.NormalCombustionEnergyJ, b.NormalCombustionEnergyJ, EpsLoose)) return true;

            if (Diff(a.ChamberTemperatureK, b.ChamberTemperatureK, EpsNormal)) return true;
            if (Diff(a.ChamberPressurekPa, b.ChamberPressurekPa, EpsNormal)) return true;
            if (Diff(a.ChamberTotalMoles, b.ChamberTotalMoles, 0.0001)) return true;
            if (Diff(a.CombustionEnergyJ, b.CombustionEnergyJ, EpsLoose)) return true;
            if (Diff(a.RatioOxygen, b.RatioOxygen, EpsRatio)) return true;
            if (Diff(a.RatioHydrogen, b.RatioHydrogen, EpsRatio)) return true;
            if (Diff(a.RatioSteam, b.RatioSteam, EpsRatio)) return true;
            if (Diff(a.RatioPollutant, b.RatioPollutant, EpsRatio)) return true;
            if (Diff(a.RatioCarbonDioxide, b.RatioCarbonDioxide, EpsRatio)) return true;
            if (Diff(a.RatioNitrogen, b.RatioNitrogen, EpsRatio)) return true;
            if (Diff(a.RatioNitrousOxide, b.RatioNitrousOxide, EpsRatio)) return true;
            if (Diff(a.RatioOzone, b.RatioOzone, EpsRatio)) return true;

            if (a.OnOff != b.OnOff) return true;
            if (a.Powered != b.Powered) return true;
            if (a.Error != b.Error) return true;
            if (a.IsStructureCompleted != b.IsStructureCompleted) return true;

            if (a.IsInputValid != b.IsInputValid) return true;
            if (a.IsOutputValid != b.IsOutputValid) return true;
            if (a.IsInput2Valid != b.IsInput2Valid) return true;

            if (a.InputNetworkNull != b.InputNetworkNull) return true;
            if (a.InputStructureCount != b.InputStructureCount) return true;
            if (a.InputAwaitingEvent != b.InputAwaitingEvent) return true;
            if (Diff(a.InputPipePressurekPa, b.InputPipePressurekPa, EpsNormal)) return true;
            if (Diff(a.InputPipeTemperatureK, b.InputPipeTemperatureK, EpsNormal)) return true;
            if (Diff(a.InputPipeTotalMoles, b.InputPipeTotalMoles, 0.0001)) return true;

            if (a.OutputNetworkNull != b.OutputNetworkNull) return true;
            if (a.OutputStructureCount != b.OutputStructureCount) return true;
            if (a.OutputAwaitingEvent != b.OutputAwaitingEvent) return true;
            if (Diff(a.OutputPipePressurekPa, b.OutputPipePressurekPa, EpsNormal)) return true;
            if (Diff(a.OutputPipeTemperatureK, b.OutputPipeTemperatureK, EpsNormal)) return true;
            if (Diff(a.OutputPipeTotalMoles, b.OutputPipeTotalMoles, 0.0001)) return true;

            if (a.ThingInTheWay != b.ThingInTheWay) return true;
            if (a.DeepMinablesSet != b.DeepMinablesSet) return true;
            if (a.CanMineResult != b.CanMineResult) return true;
            if (a.IsReachedBedRock != b.IsReachedBedRock) return true;

            if (a.ChipPresent != b.ChipPresent) return true;
            if (a.CodeErrorState != b.CodeErrorState) return true;
            if (a.CompilationError != b.CompilationError) return true;

            return false;
        }

        private static string HeaderRow() => string.Join(",", new[]
        {
            "tick",
            "rpm", "stress", "throttle", "cl", "didCombustion", "gainedStress",
            "targetPkPa", "normalCombustionEnergyJ",
            "chamberTK", "chamberPkPa", "chamberMoles", "combustionEnergyJ",
            "rO2", "rH2", "rSteam", "rPollutant", "rCO2", "rN2", "rN2O", "rO3",
            "onoff", "powered", "error", "structureCompleted",
            "isInputValid", "isOutputValid", "isInput2Valid",
            "inputNull", "inputStructureCount", "inputAwaitingEvent",
            "inputPkPa", "inputTK", "inputMoles",
            "outputNull", "outputStructureCount", "outputAwaitingEvent",
            "outputPkPa", "outputTK", "outputMoles",
            "thingInTheWay", "deepMinablesSet", "canMine", "reachedBedRock",
            "chipPresent", "codeErrorState", "compilationError"
        });

        private static string FormatRow(MinerSnapshot s)
        {
            var sb = new StringBuilder(640);
            var ic = CultureInfo.InvariantCulture;
            sb.Append(s.Tick).Append(',');
            sb.Append(s.Rpm.ToString("0.###", ic)).Append(',');
            sb.Append(s.Stress.ToString("0.###", ic)).Append(',');
            sb.Append(s.Throttle.ToString("0.###", ic)).Append(',');
            sb.Append(s.CombustionLimiter.ToString("0.###", ic)).Append(',');
            sb.Append(s.DidCombustionLastTick ? '1' : '0').Append(',');
            sb.Append(s.GainedStress ? '1' : '0').Append(',');
            sb.Append(s.TargetPressurekPa.ToString("0.##", ic)).Append(',');
            sb.Append(s.NormalCombustionEnergyJ.ToString("0.##", ic)).Append(',');

            sb.Append(s.ChamberTemperatureK.ToString("0.##", ic)).Append(',');
            sb.Append(s.ChamberPressurekPa.ToString("0.##", ic)).Append(',');
            sb.Append(s.ChamberTotalMoles.ToString("0.######", ic)).Append(',');
            sb.Append(s.CombustionEnergyJ.ToString("0.##", ic)).Append(',');
            sb.Append(s.RatioOxygen.ToString("0.####", ic)).Append(',');
            sb.Append(s.RatioHydrogen.ToString("0.####", ic)).Append(',');
            sb.Append(s.RatioSteam.ToString("0.####", ic)).Append(',');
            sb.Append(s.RatioPollutant.ToString("0.####", ic)).Append(',');
            sb.Append(s.RatioCarbonDioxide.ToString("0.####", ic)).Append(',');
            sb.Append(s.RatioNitrogen.ToString("0.####", ic)).Append(',');
            sb.Append(s.RatioNitrousOxide.ToString("0.####", ic)).Append(',');
            sb.Append(s.RatioOzone.ToString("0.####", ic)).Append(',');

            sb.Append(s.OnOff ? '1' : '0').Append(',');
            sb.Append(s.Powered ? '1' : '0').Append(',');
            sb.Append(s.Error).Append(',');
            sb.Append(s.IsStructureCompleted ? '1' : '0').Append(',');

            sb.Append(s.IsInputValid ? '1' : '0').Append(',');
            sb.Append(s.IsOutputValid ? '1' : '0').Append(',');
            sb.Append(s.IsInput2Valid ? '1' : '0').Append(',');

            sb.Append(s.InputNetworkNull ? '1' : '0').Append(',');
            sb.Append(s.InputStructureCount).Append(',');
            sb.Append(s.InputAwaitingEvent ? '1' : '0').Append(',');
            sb.Append(s.InputPipePressurekPa.ToString("0.##", ic)).Append(',');
            sb.Append(s.InputPipeTemperatureK.ToString("0.##", ic)).Append(',');
            sb.Append(s.InputPipeTotalMoles.ToString("0.######", ic)).Append(',');

            sb.Append(s.OutputNetworkNull ? '1' : '0').Append(',');
            sb.Append(s.OutputStructureCount).Append(',');
            sb.Append(s.OutputAwaitingEvent ? '1' : '0').Append(',');
            sb.Append(s.OutputPipePressurekPa.ToString("0.##", ic)).Append(',');
            sb.Append(s.OutputPipeTemperatureK.ToString("0.##", ic)).Append(',');
            sb.Append(s.OutputPipeTotalMoles.ToString("0.######", ic)).Append(',');

            sb.Append(s.ThingInTheWay ? '1' : '0').Append(',');
            sb.Append(s.DeepMinablesSet ? '1' : '0').Append(',');
            sb.Append(s.CanMineResult ? '1' : '0').Append(',');
            sb.Append(s.IsReachedBedRock ? '1' : '0').Append(',');

            sb.Append(s.ChipPresent ? '1' : '0').Append(',');
            sb.Append(s.CodeErrorState).Append(',');
            sb.Append(s.CompilationError ? '1' : '0');
            return sb.ToString();
        }
    }

    internal static class MinerLogger
    {
        internal static ManualLogSource Log;
        private static string _logDir;
        private static readonly Dictionary<long, MinerSession> _sessions = new Dictionary<long, MinerSession>();
        private static readonly object _sync = new object();

        // Reflection handles cached at first use.
        private static bool _reflectionInit;
        private static FieldInfo _fiInternalCombustion; // CombustionDeepMiner._internalCombustion
        private static PropertyInfo _piRpm, _piStress, _piThrottle, _piCombustionLimiter, _piDidCombustion;
        private static FieldInfo _fiGainedStress, _fiTargetPressure, _fiNormalCombustionCache;
        private static FieldInfo _fiDeepMinables, _fiIsReachedBedRock;
        private static PropertyInfo _piThingInTheWay;
        private static PropertyInfo _piCanMine;
        private static PropertyInfo _piProgrammableChip;
        private static PropertyInfo _piCodeErrorState;
        private static PropertyInfo _piCompilationError;
        private static PropertyInfo _piIsInput2Valid;

        public static void Initialize(ManualLogSource log, string logDir)
        {
            Log = log;
            _logDir = logDir;
        }

        public static void OnTick(CombustionDeepMiner miner)
        {
            if (miner == null) return;
            try
            {
                EnsureReflection();
                long refId = miner.ReferenceId;
                MinerSession session;
                lock (_sync)
                {
                    if (!_sessions.TryGetValue(refId, out session))
                    {
                        session = new MinerSession(refId, _logDir);
                        _sessions[refId] = session;
                    }
                }
                session.TickCounter++;
                var snap = Capture(miner, session.TickCounter);
                session.Consider(snap);
            }
            catch (Exception e)
            {
                Log?.LogError($"OnTick failed: {e}");
            }
        }

        private static void EnsureReflection()
        {
            if (_reflectionInit) return;

            var tMiner = typeof(CombustionDeepMiner);
            _fiInternalCombustion = tMiner.GetField("_internalCombustion", BindingFlags.Instance | BindingFlags.NonPublic);

            // ProgrammableChip + CodeErrorState are on the DeviceInputOutputImportExportCircuit layer or inherited.
            _piProgrammableChip = FindPropertyUpward(tMiner, "ProgrammableChip");
            _piCodeErrorState = FindPropertyUpward(tMiner, "CodeErrorState");

            // DeepMiner private fields.
            var tDeep = typeof(DeepMiner);
            _fiDeepMinables = tDeep.GetField("_deepMinables", BindingFlags.Instance | BindingFlags.NonPublic);
            _fiIsReachedBedRock = tDeep.GetField("_isReachedBedRock", BindingFlags.Instance | BindingFlags.NonPublic);
            _piThingInTheWay = FindPropertyUpward(tMiner, "ThingInTheWay");
            _piCanMine = FindPropertyUpward(tMiner, "CanMine"); // may be a method

            // IsInput2Valid lives on DeviceInputOutput.
            _piIsInput2Valid = FindPropertyUpward(tMiner, "IsInput2Valid");

            // InternalCombustion type and its members.
            var tIc = AccessTools.TypeByName("InternalCombustion");
            if (tIc != null)
            {
                _piRpm = tIc.GetProperty("Rpm", BindingFlags.Instance | BindingFlags.Public);
                _piStress = tIc.GetProperty("Stress", BindingFlags.Instance | BindingFlags.Public);
                _piThrottle = tIc.GetProperty("Throttle", BindingFlags.Instance | BindingFlags.Public);
                _piCombustionLimiter = tIc.GetProperty("CombustionLimiter", BindingFlags.Instance | BindingFlags.Public);
                _piDidCombustion = tIc.GetProperty("DidCombustionLastTick", BindingFlags.Instance | BindingFlags.Public);
                _fiGainedStress = tIc.GetField("_gainedStress", BindingFlags.Instance | BindingFlags.NonPublic);
                _fiTargetPressure = tIc.GetField("_targetPressure", BindingFlags.Instance | BindingFlags.NonPublic);
                _fiNormalCombustionCache = tIc.GetField("_normalCombustionEnergyCache", BindingFlags.Instance | BindingFlags.NonPublic);
            }

            _reflectionInit = true;
        }

        private static PropertyInfo FindPropertyUpward(Type t, string name)
        {
            while (t != null)
            {
                var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null) return p;
                t = t.BaseType;
            }
            return null;
        }

        private static double StructToDouble(object o)
        {
            if (o == null) return 0;
            var t = o.GetType();
            var m = t.GetMethod("ToDouble", BindingFlags.Instance | BindingFlags.Public);
            if (m != null) return (double)m.Invoke(o, null);
            var mf = t.GetMethod("ToFloat", BindingFlags.Instance | BindingFlags.Public);
            if (mf != null) return (float)mf.Invoke(o, null);
            var f = t.GetField("_value", BindingFlags.Instance | BindingFlags.NonPublic);
            if (f != null) return Convert.ToDouble(f.GetValue(o));
            try { return Convert.ToDouble(o); } catch { return 0; }
        }

        private static double ReadDoubleProp(object o, string name)
        {
            if (o == null) return 0;
            var p = o.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (p == null) return 0;
            var v = p.GetValue(o, null);
            if (v == null) return 0;
            if (v is double d) return d;
            if (v is float f) return f;
            return StructToDouble(v);
        }

        private static MinerSnapshot Capture(CombustionDeepMiner miner, long tick)
        {
            var s = new MinerSnapshot { Tick = tick };

            // Device-level
            s.OnOff = miner.OnOff;
            s.Powered = miner.Powered;
            s.Error = miner.Error;
            s.IsStructureCompleted = miner.IsStructureCompleted;

            s.IsInputValid = miner.IsInputValid;
            s.IsOutputValid = miner.IsOutputValid;
            if (_piIsInput2Valid != null)
            {
                try { s.IsInput2Valid = (bool)_piIsInput2Valid.GetValue(miner, null); } catch { }
            }

            // Pipe networks
            var input = miner.InputNetwork;
            s.InputNetworkNull = input == null;
            if (!s.InputNetworkNull) CaptureNetwork(input, out s.InputStructureCount, out s.InputAwaitingEvent, out s.InputPipePressurekPa, out s.InputPipeTemperatureK, out s.InputPipeTotalMoles);

            var output = miner.OutputNetwork;
            s.OutputNetworkNull = output == null;
            if (!s.OutputNetworkNull) CaptureNetwork(output, out s.OutputStructureCount, out s.OutputAwaitingEvent, out s.OutputPipePressurekPa, out s.OutputPipeTemperatureK, out s.OutputPipeTotalMoles);

            // InternalCombustion
            object ic = _fiInternalCombustion?.GetValue(miner);
            if (ic != null)
            {
                if (_piRpm != null) s.Rpm = (float)_piRpm.GetValue(ic, null);
                if (_piStress != null) s.Stress = (float)_piStress.GetValue(ic, null);
                if (_piThrottle != null) s.Throttle = (float)_piThrottle.GetValue(ic, null);
                if (_piCombustionLimiter != null) s.CombustionLimiter = (float)_piCombustionLimiter.GetValue(ic, null);
                if (_piDidCombustion != null)
                {
                    try { s.DidCombustionLastTick = (bool)_piDidCombustion.GetValue(ic, null); } catch { }
                }
                if (_fiGainedStress != null)
                {
                    try { s.GainedStress = (bool)_fiGainedStress.GetValue(ic); } catch { }
                }
                if (_fiTargetPressure != null) s.TargetPressurekPa = StructToDouble(_fiTargetPressure.GetValue(ic));
                if (_fiNormalCombustionCache != null) s.NormalCombustionEnergyJ = StructToDouble(_fiNormalCombustionCache.GetValue(ic));
            }

            // Chamber atmosphere - direct typed access
            // CombustionEnergy is a public field on Atmosphere (not a property), and per-gas
            // ratios live on GasMixture as the GetGasTypeRatio(GasType) method, not as Ratio*
            // properties. Verbatim verification in Research/GameClasses/CombustionDeepMiner.md.
            var atm = miner.InternalAtmosphere;
            if (atm != null)
            {
                s.ChamberTemperatureK = atm.Temperature.ToDouble();
                s.ChamberPressurekPa = atm.PressureGassesAndLiquids.ToDouble();
                s.ChamberTotalMoles = atm.TotalMoles.ToDouble();
                s.CombustionEnergyJ = atm.CombustionEnergy.ToDouble();

                // GasMixture is a struct; no null check.
                var mix = atm.GasMixture;
                s.RatioOxygen = mix.GetGasTypeRatio(Chemistry.GasType.Oxygen);
                s.RatioHydrogen = mix.GetGasTypeRatio(Chemistry.GasType.Hydrogen);
                s.RatioSteam = mix.GetGasTypeRatio(Chemistry.GasType.Steam);
                s.RatioPollutant = mix.GetGasTypeRatio(Chemistry.GasType.Pollutant);
                s.RatioCarbonDioxide = mix.GetGasTypeRatio(Chemistry.GasType.CarbonDioxide);
                s.RatioNitrogen = mix.GetGasTypeRatio(Chemistry.GasType.Nitrogen);
                s.RatioNitrousOxide = mix.GetGasTypeRatio(Chemistry.GasType.NitrousOxide);
                s.RatioOzone = mix.GetGasTypeRatio(Chemistry.GasType.Ozone);
            }

            // DeepMiner-side
            if (_piThingInTheWay != null)
            {
                try
                {
                    var thing = _piThingInTheWay.GetValue(miner, null);
                    s.ThingInTheWay = IsUnityRefLive(thing);
                }
                catch { }
            }
            if (_fiDeepMinables != null)
            {
                try
                {
                    var dm = _fiDeepMinables.GetValue(miner);
                    s.DeepMinablesSet = dm != null;
                }
                catch { }
            }
            if (_fiIsReachedBedRock != null)
            {
                try { s.IsReachedBedRock = (bool)_fiIsReachedBedRock.GetValue(miner); } catch { }
            }
            try { s.CanMineResult = miner.CanMine(); } catch { }

            // Programmable chip
            if (_piProgrammableChip != null)
            {
                try
                {
                    var chip = _piProgrammableChip.GetValue(miner, null);
                    s.ChipPresent = IsUnityRefLive(chip);
                    if (s.ChipPresent)
                    {
                        if (_piCompilationError == null)
                            _piCompilationError = FindPropertyUpward(chip.GetType(), "CompilationError");
                        if (_piCompilationError != null)
                        {
                            try { s.CompilationError = (bool)_piCompilationError.GetValue(chip, null); } catch { }
                        }
                    }
                }
                catch { }
            }
            if (_piCodeErrorState != null)
            {
                try { s.CodeErrorState = Convert.ToInt32(_piCodeErrorState.GetValue(miner, null)); } catch { }
            }

            return s;
        }

        private static double ReadRatio(object atm, string name)
        {
            // Try on Atmosphere first.
            var p = atm.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (p != null)
            {
                try
                {
                    var v = p.GetValue(atm, null);
                    if (v is double d) return d;
                    if (v is float f) return f;
                    return StructToDouble(v);
                }
                catch { }
            }
            // Fallback: GasMixture
            var pgm = atm.GetType().GetProperty("GasMixture", BindingFlags.Instance | BindingFlags.Public);
            if (pgm != null)
            {
                var mix = pgm.GetValue(atm, null);
                if (mix != null)
                {
                    var pr = mix.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                    if (pr != null)
                    {
                        try
                        {
                            var v = pr.GetValue(mix, null);
                            if (v is double d) return d;
                            if (v is float f) return f;
                            return StructToDouble(v);
                        }
                        catch { }
                    }
                }
            }
            return 0;
        }

        private static void CaptureNetwork(object network, out int structureCount, out bool awaitingEvent, out double pkPa, out double tK, out double moles)
        {
            structureCount = 0;
            awaitingEvent = false;
            pkPa = 0; tK = 0; moles = 0;
            if (network == null) return;
            var t = network.GetType();

            // IsAwaitingEvent
            var pAwait = t.GetProperty("IsAwaitingEvent", BindingFlags.Instance | BindingFlags.Public);
            if (pAwait != null)
            {
                try { awaitingEvent = (bool)pAwait.GetValue(network, null); } catch { }
            }

            // StructureList -> Count
            var pList = t.GetProperty("StructureList", BindingFlags.Instance | BindingFlags.Public)
                      ?? t.GetProperty("Structures", BindingFlags.Instance | BindingFlags.Public);
            if (pList != null)
            {
                try
                {
                    var lst = pList.GetValue(network, null);
                    if (lst != null)
                    {
                        var pCount = lst.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
                        if (pCount != null) structureCount = (int)pCount.GetValue(lst, null);
                    }
                }
                catch { }
            }

            // Atmosphere -> Temperature / Pressure / TotalMoles
            var pAtm = t.GetProperty("Atmosphere", BindingFlags.Instance | BindingFlags.Public);
            if (pAtm != null)
            {
                try
                {
                    var atm = pAtm.GetValue(network, null);
                    if (atm != null)
                    {
                        pkPa = ReadDoubleProp(atm, "PressureGassesAndLiquids");
                        tK = ReadDoubleProp(atm, "Temperature");
                        moles = ReadDoubleProp(atm, "TotalMoles");
                    }
                }
                catch { }
            }
        }

        private static bool IsUnityRefLive(object o)
        {
            if (o == null) return false;
            if (o is UnityEngine.Object uo) return uo != null;
            return true;
        }
    }
}
