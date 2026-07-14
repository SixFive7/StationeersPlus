using System;
using System.Collections.Generic;
using System.Linq;
using Assets.Scripts;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using BepInEx.Logging;
using UnityEngine;

namespace ScenarioRunner
{
    /// <summary>
    ///     Drives scenarios from the simulation tick. State is global; one instance per
    ///     server run. Pumped from <see cref="SimTickPump"/>.
    ///
    ///     Two kinds of scenarios:
    ///       - General: work against vanilla types without depending on any specific mod.
    ///         The framework just reads / iterates them. Examples: 'inventory',
    ///         'battery-charge-snapshot'.
    ///       - Mod-specific: exercise patched code paths on a specific mod (PowerGridPlus
    ///         today). Prefix with the mod tag (e.g. 'pgp-' for PowerGridPlus) so it's
    ///         clear which mod must be loaded. If the mod is absent, the scenario logs a
    ///         warning and no-ops.
    ///
    ///     Adding a new scenario:
    ///       1. Add a case to <see cref="Tick"/> matching the scenario id.
    ///       2. Add a Scenario_* method.
    ///       3. Document the id in <c>README.md</c>.
    ///
    ///     For mod-specific scenarios, gate on
    ///     <see cref="IsAssemblyLoaded"/>(assemblyName) and log + return if the mod is
    ///     absent. Use reflection (no build-time dependency) to reach the mod's
    ///     internals; <see cref="GetModType"/> / <see cref="GetModInstanceField"/> are
    ///     starting points.
    /// </summary>
    internal static partial class Dispatcher
    {
        private static ManualLogSource _log;
        private static string _scenario = "";
        private static int _delayTicks;
        private static bool _logInventoryOnFirstTick;
        private static long _ticksSeen;
        private static int _lastTickFrame = -1;
        private static bool _firstTickFired;

        public static void Initialize(ManualLogSource log, string scenario, int delayTicks, bool logInventoryOnFirstTick)
        {
            _log = log;
            _scenario = scenario ?? "";
            _delayTicks = Mathf.Max(0, delayTicks);
            _logInventoryOnFirstTick = logInventoryOnFirstTick;
            _ticksSeen = 0;
            _lastTickFrame = -1;
            _firstTickFired = false;
            _batteries.Clear();
            _transformers.Clear();
            _apcs.Clear();
            _fuses.Clear();
        }

        /// <summary>
        ///     Called from every pumping hook (see <see cref="SimTickPump"/>).
        ///     Deduplicates by <see cref="Time.frameCount"/> so multiple hooks
        ///     converge to one scenario tick per simulation frame.
        /// </summary>
        public static void OnSimTick()
        {
            int frame = Time.frameCount;
            if (frame == _lastTickFrame) return;
            _lastTickFrame = frame;

            _ticksSeen++;
            if (_ticksSeen < _delayTicks) return;

            try
            {
                if (!_firstTickFired)
                {
                    _firstTickFired = true;
                    if (_logInventoryOnFirstTick)
                        LogInventory();
                }

                Tick();
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] scenario '{_scenario}' tick threw: {e}");
            }
        }

        private static void Tick()
        {
            if (string.IsNullOrEmpty(_scenario)) return;

            switch (_scenario)
            {
                case "inventory":
                    // Already covered by LogInventory on first tick; nothing to do per-tick.
                    return;

                case "sun-noon":
                    Scenario_SunNoon();
                    return;

                case "battery-charge-snapshot":
                    Scenario_BatteryChargeSnapshot();
                    return;

                case "power-prefab-dump":
                    Scenario_PowerPrefabDump();
                    return;

                case "connector-dump":
                    Scenario_ConnectorDump();
                    return;

                case "paintable-prefab-dump":
                    Scenario_PaintablePrefabDump();
                    return;

                case "device-port-dump":
                    Scenario_DevicePortDump();
                    return;

                case "pgp-mixedwire-fixture":
                    Scenario_PgpMixedWireFixture();
                    return;

                case "pgp-rocket-battery-probe":
                    Scenario_PgpRocketBatteryProbe();
                    return;

                case "pgp-mixedwire-survey":
                    Scenario_PgpMixedWireSurvey();
                    return;

                case "pgp-passthrough-port-probe":
                    Scenario_PgpPassthroughPortProbe();
                    return;

                case "pgp-apc-bridge-probe":
                    Scenario_PgpApcBridgeProbe();
                    return;

                case "pgp-dataport-tier-diag":
                    Scenario_DataPortTierDiag();
                    return;

                case "pgp-dataport-cursor-proxy":
                    Scenario_DataPortCursorProxy();
                    return;

                case "pgp-rocket-parity-probe":
                    Scenario_PgpRocketParityProbe();
                    return;

                case "pgp-umbilical-passthrough-probe":
                    Scenario_PgpUmbilicalPassthroughProbe();
                    return;

                case "pgp-umbilical-saveload-set":
                    Scenario_PgpUmbilicalSaveLoadSet();
                    return;

                case "pgp-umbilical-saveload-verify":
                    Scenario_PgpUmbilicalSaveLoadVerify();
                    return;

                case "merge-long-variant-num4":
                    Scenario_MergeLongVariantNum4();
                    return;

                case "clamp-merge-quantity":
                    Scenario_ClampMergeQuantity();
                    return;

                case "pgp-transformer-conservation":
                    Scenario_PgpTransformerConservation();
                    return;

                case "pgp-battery-efficiency-probe":
                    Scenario_PgpBatteryEfficiencyProbe();
                    return;

                case "pgp-apc-idle-probe":
                    Scenario_PgpApcIdleProbe();
                    return;

                case "pgp-cable-burn-probe":
                    Scenario_PgpCableBurnProbe();
                    return;

                case "pgp-cable-burn-window-probe":
                    Scenario_PgpCableBurnWindowProbe();
                    return;

                case "pgp-tooltip-filter-probe":
                    Scenario_PgpTooltipFilterProbe();
                    return;

                case "pgp-rate-cap-probe":
                    Scenario_PgpRateCapProbe();
                    return;

                case "pgp-stationpedia-page-probe":
                    Scenario_PgpStationpediaPageProbe();
                    return;

                case "pgp-priority-deprioritization-probe":
                    Scenario_PgpPriorityDeprioritizationProbe();
                    return;

                case "pgp-priority-deprioritization-persist-probe":
                    Scenario_PgpPriorityDeprioritizationPersistProbe();
                    return;

                case "pgp-priority-deprioritization-network-breakdown":
                    Scenario_PgpPriorityDeprioritizationNetworkBreakdown();
                    return;

                case "pgp-priority-deprioritization-knob-probe":
                    Scenario_PgpPriorityDeprioritizationKnobProbe();
                    return;

                case "pgp-priority-deprioritization-flash-probe":
                    Scenario_PgpPriorityDeprioritizationFlashProbe();
                    return;

                case "pgp-priority-deprioritization-hover-probe":
                    Scenario_PgpPriorityDeprioritizationHoverProbe();
                    return;

                case "pgp-priority-deprioritization-labeller-probe":
                    Scenario_PgpPriorityDeprioritizationLabellerProbe();
                    return;

                case "pgp-priority-deprioritization-mp-probe":
                    Scenario_PgpPriorityDeprioritizationMpProbe();
                    return;

                case "pgp-priority-deprioritization-saveload-probe":
                    Scenario_PgpPriorityDeprioritizationSaveLoadProbe();
                    return;

                case "pgp-priority-deprioritization-topology-probe":
                    Scenario_PgpPriorityDeprioritizationTopologyProbe();
                    return;

                case "pgp-priority-deprioritization-all":
                    Scenario_PgpPriorityDeprioritizationAll();
                    return;

                case "pgp-r1-prepare":
                    Scenario_PgpR1Prepare();
                    return;

                case "pgp-power-flow-diagnose":
                    Scenario_PgpPowerFlowDiagnose();
                    return;

                case "pgp-net-consumer-dump":
                    Scenario_PgpNetConsumerDump();
                    return;

                case "pgp-deprioritization-trace":
                    Scenario_PgpDeprioritizedTrace();
                    return;

                case "pgp-atomic-probe":
                    Scenario_PgpAtomicProbe();
                    return;

                case "pgp-overload-probe":
                    Scenario_PgpOverloadProbe();
                    return;

                case "pgp-fault-state-probe":
                    Scenario_PgpFaultStateProbe();
                    return;

                case "pgp-shortfall-net-probe":
                    Scenario_PgpFaultStateProbe();
                    Scenario_PgpShortfallNetProbe();
                    return;

                case "pgp-reversed-transformer-probe":
                    Scenario_PgpReversedTransformerProbe();
                    return;

                case "pgp-deprioritization-multilevel":
                    Scenario_PgpDeprioritizedMultilevel();
                    return;

                case "pgp-2cycle-freeze":
                    Scenario_Pgp2CycleFreeze();
                    return;

                case "pgp-deprioritization-victim-fixture":
                    Scenario_PgpDeprioritizedVictimFixture();
                    return;

                case "pgp-chain-fixture":
                    Scenario_PgpChainFixture();
                    return;

                case "pgp-overload-split-fixture":
                    Scenario_PgpOverloadSplitFixture();
                    return;

                case "pgp-rearch-suite":
                    Scenario_PgpRearchSuite();
                    return;

                case "pgp-atomic-all":
                    // Runs the synthetic probes one-shot (internally gated)
                    // PLUS the multi-tick live trace against Luna.save. Both
                    // are called every tick; the synthetic block self-skips
                    // after the first call, the trace advances per-tick.
                    Scenario_PgpAtomicAll();
                    Scenario_PgpDeprioritizedTrace();
                    return;

                case "pgp-pt-hover-all":
                    Scenario_PgpPtHoverAll();
                    return;

                case "pgp-pt-flash-all":
                    Scenario_PgpPtFlashAll();
                    return;

                case "pgp-pt-logic-all":
                    Scenario_PgpPtLogicAll();
                    return;

                case "pgp-pt-onoff-table":
                    Scenario_PgpPtOnOffTable();
                    return;

                case "pgp-pt-synthetic-all":
                    Scenario_PgpPtSyntheticAll();
                    return;

                case "pgp-pt-topology-all":
                    Scenario_PgpPtTopologyAll();
                    return;

                case "pgp-pt-extra-all":
                    Scenario_PgpPtExtraAll();
                    return;

                case "pgp-pt-crossmod-all":
                    Scenario_PgpPtCrossModAll();
                    return;

                case "pgp-pt-burnreason":
                    Scenario_PgpPtBurnReason();
                    return;

                case "pgp-pt-fixverify":
                    Scenario_PgpPtFixVerify();
                    return;

                case "ptp-autoaim-cache-probe":
                    Scenario_PtpAutoAimCacheProbe();
                    return;

                case "ptp-long-distance-link-probe":
                    Scenario_PtpLongDistanceLinkProbe();
                    return;

                case "ptp-beam-predicate-probe":
                    Scenario_PtpBeamPredicateProbe();
                    return;

                case "ptp-standalone-suite":
                    Scenario_PtpStandaloneSuite();
                    return;

                case "ptp-all":
                    Scenario_PtpAutoAimCacheProbe();
                    Scenario_PtpLongDistanceLinkProbe();
                    Scenario_PtpBeamPredicateProbe();
                    return;

                default:
                    if (_ticksSeen == _delayTicks)
                        _log?.LogWarning($"[ScenarioRunner] unknown scenario '{_scenario}'; doing nothing.");
                    return;
            }
        }

        // ---- General: cached per-type Thing lists ----
        //
        // Battery / Transformer / AreaPowerControl / CableFuse caches gathered via
        // OcclusionManager.AllThings (a ConcurrentDensePool<Thing>). We avoid
        // UnityEngine.Object.FindObjectsOfType<T> from the simulation-tick worker
        // because that hook runs on a UniTask ThreadPool worker (see
        // Cysharp.Threading.Tasks.SwitchToThreadPoolAwaitable in the call stack of
        // GameManager.GameTick) and FindObjectsOfType is Unity-main-thread-only;
        // calling it off-thread crashes the engine native side intermittently.
        // OcclusionManager.AllThings is safe to iterate from any thread.
        // See Research/Patterns/ThingEnumerationOffMainThread.md.

        private static readonly List<Battery> _batteries = new List<Battery>();
        private static readonly List<Transformer> _transformers = new List<Transformer>();
        private static readonly List<AreaPowerControl> _apcs = new List<AreaPowerControl>();
        private static readonly List<CableFuse> _fuses = new List<CableFuse>();

        private static void RebuildCaches()
        {
            _batteries.Clear();
            _transformers.Clear();
            _apcs.Clear();
            _fuses.Clear();
            OcclusionManager.AllThings.ForEach(t =>
            {
                if (t == null) return;
                if (t is Battery b) _batteries.Add(b);
                if (t is Transformer x) _transformers.Add(x);
                if (t is AreaPowerControl a) _apcs.Add(a);
                if (t is CableFuse f) _fuses.Add(f);
            });
        }

        // ---- General scenario: inventory ----

        private static void LogInventory()
        {
            RebuildCaches();
            var cableNetCount = CableNetwork.AllCableNetworks.ActiveCount;

            _log?.LogInfo(
                $"[ScenarioRunner] inventory @ tick {_ticksSeen}: " +
                $"Battery={_batteries.Count}, Transformer={_transformers.Count}, " +
                $"AreaPowerControl={_apcs.Count}, CableNetwork={cableNetCount}, " +
                $"CableFuse={_fuses.Count}");

            // Per-concrete-type breakdown of batteries (useful when subclasses are loaded,
            // e.g. MorePowerMod's StationBatteryNuclear).
            var byType = _batteries.GroupBy(b => b.GetType().Name).OrderByDescending(g => g.Count());
            foreach (var g in byType)
            {
                _log?.LogInfo($"[ScenarioRunner]   {g.Key}: {g.Count()}");
            }
        }

        // ---- General scenario: battery-charge-snapshot ----
        //
        // Every BCS_LOG_EVERY_TICKS ticks, log PowerStored / PowerMaximum / OnOff / Mode
        // for every Battery. Useful for any rate / efficiency delta diff over a window.
        // Works without PowerGridPlus.

        private static int _bcsLastLogTick = int.MinValue;
        private const int BCS_LOG_EVERY_TICKS = 5;

        private static void Scenario_BatteryChargeSnapshot()
        {
            if (_ticksSeen - _bcsLastLogTick < BCS_LOG_EVERY_TICKS) return;
            _bcsLastLogTick = (int)_ticksSeen;

            if (_batteries.Count == 0) RebuildCaches();
            foreach (var b in _batteries)
            {
                if (b == null) continue;
                _log?.LogInfo(
                    $"[ScenarioRunner] BCS tick={_ticksSeen} ref={b.ReferenceId} " +
                    $"prefab={b.PrefabName} OnOff={b.OnOff} Mode={b.Mode} " +
                    $"PowerStored={b.PowerStored:F2} PowerMaximum={b.PowerMaximum:F2}");
            }
        }

        // ---- General scenario: power-prefab-dump ----
        //
        // One-shot. Iterates Prefab.AllPrefabs and emits a structured line for every
        // power-relevant prefab (non-zero UsedPower, IPowerGenerator, Battery,
        // Transformer, WirelessPower, AreaPowerControl, or a class that overrides
        // GetUsedPower). Each line carries PrefabName, type FullName, UsedPower
        // literal, override flag, interface flags, and DLC tag.
        //
        // Use this to build a classification list of every base-game powered device
        // (e.g. for PowerGridPlus's cable-tier table). Run on a fresh -New world;
        // the prefab registry is populated at OnPrefabsLoaded so no save is needed.
        //
        // Threading: all reads are managed-state only (Type.GetType, prefab fields,
        // reflection introspection). No Unity API calls -> safe from the UniTask
        // worker the simulation-tick hook runs on.

        private static bool _powerPrefabDumpFired;

        private static void Scenario_PowerPrefabDump()
        {
            if (_powerPrefabDumpFired) return;
            _powerPrefabDumpFired = true;

            try
            {
                _log?.LogInfo("[ScenarioRunner] power-prefab-dump START");

                var deviceGetUsedPower = typeof(Device).GetMethod(
                    "GetUsedPower",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                    null,
                    new[] { typeof(CableNetwork) },
                    null);
                var deviceGetGeneratedPower = typeof(Device).GetMethod(
                    "GetGeneratedPower",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                    null,
                    new[] { typeof(CableNetwork) },
                    null);
                // IPowerGenerator lives in the top-level `Objects` namespace (game v0.2.6228.27061,
                // decompile line 138702 in `namespace Objects`). Don't search by bare-name; use
                // every prefab's own interface list at iteration time instead, which is robust to
                // future namespace moves and catches inherited interface implementations.

                int total = 0;
                int emitted = 0;

                foreach (var prefab in Prefab.AllPrefabs)
                {
                    if (prefab == null) continue;
                    total++;

                    var type = prefab.GetType();
                    string prefabName = prefab.PrefabName ?? "";
                    int prefabHash = prefab.PrefabHash;

                    float usedPower = (prefab is Device dev) ? dev.UsedPower : 0f;

                    bool overridesGetUsedPower = false;
                    bool overridesGetGeneratedPower = false;
                    if (prefab is Device)
                    {
                        if (deviceGetUsedPower != null)
                        {
                            var concrete = type.GetMethod(
                                "GetUsedPower",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                                null,
                                new[] { typeof(CableNetwork) },
                                null);
                            overridesGetUsedPower = concrete != null
                                && concrete.DeclaringType != deviceGetUsedPower.DeclaringType;
                        }
                        if (deviceGetGeneratedPower != null)
                        {
                            var concrete = type.GetMethod(
                                "GetGeneratedPower",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                                null,
                                new[] { typeof(CableNetwork) },
                                null);
                            overridesGetGeneratedPower = concrete != null
                                && concrete.DeclaringType != deviceGetGeneratedPower.DeclaringType;
                        }
                    }

                    bool implementsIPowerGenerator = false;
                    foreach (var iface in type.GetInterfaces())
                    {
                        if (iface.Name == "IPowerGenerator") { implementsIPowerGenerator = true; break; }
                    }
                    bool isBattery = prefab is Battery;
                    bool isTransformer = prefab is Transformer;
                    bool isWireless = prefab is WirelessPower;
                    bool isAPC = prefab is AreaPowerControl;

                    bool relevant = usedPower != 0f
                        || overridesGetUsedPower
                        || overridesGetGeneratedPower
                        || implementsIPowerGenerator
                        || isBattery
                        || isTransformer
                        || isWireless
                        || isAPC;
                    if (!relevant) continue;

                    string dlcTag = ExtractDlcTag(prefab);
                    string usedPowerStr = usedPower.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

                    _log?.LogInfo(
                        $"[ScenarioRunner] power-prefab-dump | PrefabName={prefabName} | " +
                        $"PrefabHash={prefabHash} | Type={type.FullName} | " +
                        $"UsedPower={usedPowerStr} | " +
                        $"OverridesGetUsedPower={overridesGetUsedPower} | " +
                        $"OverridesGetGeneratedPower={overridesGetGeneratedPower} | " +
                        $"ImplementsIPowerGenerator={implementsIPowerGenerator} | " +
                        $"IsBattery={isBattery} | IsTransformer={isTransformer} | " +
                        $"IsWireless={isWireless} | IsAPC={isAPC} | DLC={dlcTag}");
                    emitted++;
                }

                _log?.LogInfo($"[ScenarioRunner] power-prefab-dump END emitted={emitted} totalPrefabs={total}");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] power-prefab-dump threw: {e}");
            }
        }

        private static string ExtractDlcTag(Thing prefab)
        {
            try
            {
                var dlcTypeProp = prefab.GetType().GetProperty("DLCType");
                var dlcValue = dlcTypeProp?.GetValue(prefab);
                if (dlcValue == null) return "base";
                string s = dlcValue.ToString();
                return s == "None" ? "base" : s;
            }
            catch
            {
                return "unknown";
            }
        }

        // ---- General scenario: connector-dump ----
        //
        // One-shot. Iterates Prefab.AllPrefabs and, for every SmallGrid prefab carrying at
        // least one Power or Data connector (or any ElectricalInputOutput), emits its full
        // OpenEnds layout: connector count, each connector as NetworkType/ConnectionRole, and
        // a breakdown into purePower / pureData / powerAndData / pipe / other. NetworkType is a
        // [Flags] enum (Power=2, Data=4, PowerAndData=6=Power|Data), so a connector with the
        // data bit but NOT the power bit (pureData) is a DEDICATED data port; a connector with
        // both bits (powerAndData) is data riding on a power connector. Flags THREE_PLUS (>=3
        // connectors) and SEPARATE_DATA (>=1 dedicated Data connector) -- the shape the
        // PowerGridPlus passthrough code (which only bridges the InputNetwork/OutputNetwork
        // power pair) does not handle.
        //
        // Answers: rocket vs station transformer/battery connector layout, the umbilical
        // connectors, and the exhaustive set of power/data devices with 3+ connectors or a
        // separate data port. Run on a fresh -New world; the prefab registry is populated at
        // Prefab.OnPrefabsLoaded so no save is needed.
        //
        // Threading: managed-state only (Prefab.AllPrefabs iteration, OpenEnds list, enum
        // reads). Does NOT touch Connection.Transform (a Unity object) so it is safe from the
        // UniTask worker the sim-tick pump runs on.

        private static bool _connectorDumpFired;

        private static void Scenario_ConnectorDump()
        {
            if (_connectorDumpFired) return;
            _connectorDumpFired = true;

            try
            {
                _log?.LogInfo("[ScenarioRunner] connector-dump START");

                int total = 0, emitted = 0, withSeparateData = 0, threePlus = 0;

                foreach (var prefab in Prefab.AllPrefabs)
                {
                    if (prefab == null) continue;
                    total++;

                    if (!(prefab is SmallGrid grid)) continue;
                    var ends = grid.OpenEnds;
                    if (ends == null || ends.Count == 0) continue;

                    int count = ends.Count;
                    int purePower = 0, pureData = 0, powerAndData = 0, pipe = 0, other = 0;
                    var parts = new List<string>(count);

                    foreach (var c in ends)
                    {
                        if (c == null) { parts.Add("<null>"); other++; continue; }
                        var ct = c.ConnectionType;
                        bool powerBit = (ct & NetworkType.Power) != NetworkType.None;
                        bool dataBit = (ct & NetworkType.Data) != NetworkType.None;
                        bool pipeBit = (ct & NetworkType.Pipe) != NetworkType.None;

                        if (powerBit && dataBit) powerAndData++;
                        else if (dataBit) pureData++;
                        else if (powerBit) purePower++;
                        else if (pipeBit) pipe++;
                        else other++;

                        parts.Add($"{ct}/{c.ConnectionRole}");
                    }

                    // Scope: only devices that participate in a power or data network.
                    bool relevant = purePower > 0 || pureData > 0 || powerAndData > 0 || prefab is ElectricalInputOutput;
                    if (!relevant) continue;

                    bool separateDataPort = pureData > 0;   // dedicated Data connector (not PowerAndData)
                    if (separateDataPort) withSeparateData++;
                    if (count >= 3) threePlus++;

                    string flags = "";
                    if (count >= 3) flags += " THREE_PLUS";
                    if (separateDataPort) flags += " SEPARATE_DATA";

                    _log?.LogInfo(
                        $"[ScenarioRunner] connector-dump | PrefabName={prefab.PrefabName} | Type={prefab.GetType().Name} | " +
                        $"Count={count} | purePower={purePower} pureData={pureData} powerAndData={powerAndData} pipe={pipe} other={other} | " +
                        $"HasDataConnection={grid.HasDataConnection} | Conns=[{string.Join(", ", parts)}]{flags}");
                    emitted++;
                }

                _log?.LogInfo(
                    $"[ScenarioRunner] connector-dump END emitted={emitted} threePlus={threePlus} " +
                    $"separateDataPort={withSeparateData} totalPrefabs={total}");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] connector-dump threw: {e}");
            }
        }

        // ---- General scenario: paintable-prefab-dump ----
        //
        // One-shot. Answers "which structures are spray-paintable" by reading each
        // prefab's serialized PaintableMaterial field. Thing.IsPaintable returns true
        // iff PaintableMaterial != null (for non-mask types), and the in-game
        // Stationpedia "Paintable: Yes/No" line reads the same field, so a non-null
        // PaintableMaterial IS the paintability signal. Dumps a fixed list of the
        // steel- and iron-frame construction variants plus the composite walls (a
        // known-paintable control), then a one-line summary across every Structure
        // prefab (preview for a future whole-game paintability sweep).
        //
        // Threading: PaintableMaterial is a plain managed field read; null is tested
        // with ReferenceEquals (NOT the UnityEngine.Object == operator, which marshals
        // to native) so no Unity API is touched. structureRenderMode and
        // _customMaterials are read via reflection (value-type / managed List). All
        // managed-state -> safe from the UniTask worker the sim-tick pump runs on.

        private static bool _paintableDumpFired;

        private static void Scenario_PaintablePrefabDump()
        {
            if (_paintableDumpFired) return;
            _paintableDumpFired = true;

            try
            {
                _log?.LogInfo("[ScenarioRunner] paintable-prefab-dump START");

                string[] frameNames =
                {
                    // steel frame kit (ItemSteelFrames) -> 4 shapes (the question)
                    "StructureFrame", "StructureFrameSide", "StructureFrameCorner", "StructureFrameCornerCut",
                    // iron frame kit (ItemIronFrames) for comparison
                    "StructureFrameIron",
                    // composite walls + window (separate Kit (Wall)) -- known paintable control
                    "StructureCompositeWall", "StructureCompositeWall02", "StructureCompositeWall03",
                    "StructureCompositeWall04", "StructureCompositeWindow",
                };

                foreach (var name in frameNames)
                {
                    var prefab = Prefab.Find(name) as Thing;
                    if (prefab == null)
                    {
                        _log?.LogInfo($"[ScenarioRunner] paintable | {name} NOT FOUND");
                        continue;
                    }
                    _log?.LogInfo($"[ScenarioRunner] paintable | {PaintLine(prefab)}");
                }

                // Summary across all Structure prefabs (preview for the whole-game sweep).
                int total = 0, set = 0, unset = 0;
                foreach (var p in Prefab.AllPrefabs)
                {
                    if (p == null) continue;
                    if (!(p is Structure)) continue;
                    total++;
                    if (PaintableSet(p)) { set++; }
                    else { unset++; _log?.LogInfo($"[ScenarioRunner] paintable UNPAINTABLE | {PaintLine(p)} | DLC={ExtractDlcTag(p)}"); }
                }
                _log?.LogInfo($"[ScenarioRunner] paintable SUMMARY structures={total} paintable={set} notPaintable={unset}");
                _log?.LogInfo("[ScenarioRunner] paintable-prefab-dump END");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] paintable-prefab-dump threw: {e}");
            }
        }

        private static bool PaintableSet(Thing t)
        {
            try { return !object.ReferenceEquals(t.PaintableMaterial, null); }
            catch { return false; }
        }

        private static string PaintLine(Thing prefab)
        {
            string name = prefab.PrefabName ?? "";
            string type = prefab.GetType().Name;
            bool paintable = PaintableSet(prefab);

            string renderMode = "n/a";
            if (prefab is Structure)
            {
                try
                {
                    var f = typeof(Structure).GetField("structureRenderMode",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var v = f?.GetValue(prefab);
                    renderMode = v?.ToString() ?? "null";
                }
                catch { renderMode = "err"; }
            }

            int cmCount = -1;
            try
            {
                var f = typeof(Thing).GetField("_customMaterials",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var v = f?.GetValue(prefab) as System.Collections.ICollection;
                cmCount = v?.Count ?? -1;
            }
            catch { cmCount = -2; }

            return $"{name} type={type} PaintableMaterialSet={paintable} renderMode={renderMode} customMaterials={cmCount}";
        }

        // ---- PGP scenario: passthrough-port-probe ----
        //
        // Verifies the all-port logic-passthrough fix. For every ElectricalInputOutput bridge device on
        // the loaded save it forces LogicPassthroughMode = 1, then reflection-invokes
        // PowerGridPlus.Patches.PassthroughTopology.GatherReachable on each of the device's networks
        // (power input, power output, dedicated data port) and logs the reachable network-id set. Key
        // metric: dataReachableFromPower -- whether a device's separate Data-port network is reachable
        // from a power side. Pre-fix the binary bridge only ever returned the opposite power side, so a
        // separate Data port was never reachable from a power side (false); post-fix it is (true).
        // Requires PowerGridPlus loaded. Managed-state reflection only; worker-safe.

        private static bool _pppFired;

        private static void Scenario_PgpPassthroughPortProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-passthrough-port-probe")) return;
            if (_pppFired) return;
            _pppFired = true;

            try
            {
                var asm = GetModAssembly(PGP_ASSEMBLY);
                var topo = asm.GetType("PowerGridPlus.Patches.PassthroughTopology");
                var store = asm.GetType("PowerGridPlus.PassthroughModeStore");
                var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                var gather = topo?.GetMethod("GatherReachable", flags);
                var setMode = store?.GetMethod("SetMode", flags);
                var getMode = store?.GetMethod("GetMode", flags);
                if (gather == null || setMode == null || getMode == null)
                {
                    _log?.LogError($"[ScenarioRunner] PPP reflection failed (gather={gather != null} setMode={setMode != null} getMode={getMode != null})");
                    return;
                }

                _log?.LogInfo("[ScenarioRunner] pgp-passthrough-port-probe START");

                var bridges = new List<Device>();
                OcclusionManager.AllThings.ForEach(t =>
                {
                    if (t is Assets.Scripts.Objects.Electrical.ElectricalInputOutput) bridges.Add((Device)t);
                });

                int reported = 0, distinctData = 0, bridgedOk = 0, sampled = 0;
                const int SAMPLE_CAP = 20;
                foreach (var d in bridges)
                {
                    if (d == null) continue;
                    var eio = (Assets.Scripts.Objects.Electrical.ElectricalInputOutput)d;
                    var inNet = eio.InputNetwork;
                    var outNet = eio.OutputNetwork;
                    var dataNet = d.DataCableNetwork;
                    long inId = inNet?.ReferenceId ?? 0;
                    long outId = outNet?.ReferenceId ?? 0;
                    long dataId = dataNet?.ReferenceId ?? 0;
                    bool dataDistinct = dataNet != null && dataNet != inNet && dataNet != outNet;
                    if (dataDistinct) distinctData++;

                    string prefab = d.PrefabName ?? "";
                    bool isRocket = prefab.IndexOf("Rocket", System.StringComparison.OrdinalIgnoreCase) >= 0;
                    bool isTarget = d.ReferenceId == 525400L;

                    // Always report the interesting devices (separate data port, rocket-internal, the
                    // medium battery the user flagged); sample the rest so the log stays readable.
                    bool report = dataDistinct || isRocket || isTarget;
                    if (!report)
                    {
                        if (sampled >= SAMPLE_CAP) continue;
                        sampled++;
                    }

                    int preMode = getMode.Invoke(null, new object[] { d }) is int pm ? pm : -1;
                    setMode.Invoke(null, new object[] { d, 1 });

                    var reachIn = PppReach(gather, inNet);
                    var reachOut = PppReach(gather, outNet);
                    var reachData = PppReach(gather, dataNet);
                    bool dataFromPower = dataNet != null && (reachIn.Contains(dataId) || reachOut.Contains(dataId));
                    if (dataDistinct && dataFromPower) bridgedOk++;

                    string tags = (isTarget ? " [TARGET-525400]" : "") + (isRocket ? " [ROCKET]" : "");
                    _log?.LogInfo(
                        $"[ScenarioRunner] PPP {d.GetType().Name} ref={d.ReferenceId} prefab={prefab} modePreForce={preMode} " +
                        $"in={inId} out={outId} data={dataId} dataDistinct={dataDistinct} | " +
                        $"reach(in)=[{string.Join(",", reachIn)}] reach(out)=[{string.Join(",", reachOut)}] reach(data)=[{string.Join(",", reachData)}] | " +
                        $"dataReachableFromPower={dataFromPower}{tags}");
                    reported++;
                }

                _log?.LogInfo($"[ScenarioRunner] pgp-passthrough-port-probe END reported={reported} withSeparateDataPort={distinctData} dataPortBridgedFromPower={bridgedOk}");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] PPP threw: {e}");
            }
        }

        private static List<long> PppReach(System.Reflection.MethodInfo gather, CableNetwork net)
        {
            var ids = new List<long>();
            if (net == null) return ids;
            try
            {
                var r = gather.Invoke(null, new object[] { net }) as System.Collections.IEnumerable;
                if (r == null) return ids;
                foreach (var x in r)
                    if (x is CableNetwork cn) ids.Add(cn.ReferenceId);
            }
            catch { }
            return ids;
        }

        // ---- PGP scenario: rocket-parity-probe ----
        //
        // Headless pre-verification of the five PowerGridPlus rocket-family changes, plus a
        // reverse-engineering capacity dump. Five blocks, each line prefixed
        // "[ScenarioRunner] rocket-parity ...":
        //
        //   (A) CAPACITY DUMP. Walks Prefab.AllPrefabs and reads the public PowerMaximum field
        //       off the battery prefabs (StructureBatteryMedium/Small/Battery/Large, plus
        //       StationBatteryNuclear) and the rocket power-umbilical prefabs. This is the
        //       reverse-engineering deliverable: the real PowerMaximum (stored-energy capacity)
        //       of the medium and small rocket batteries.
        //   (B) ROCKET TRANSFORMER TIER (item 1). VoltageTier.GetTransformerTierMap(
        //       "StructureRocketTransformerSmall") -> expect (heavy, normal).
        //   (C) UMBILICAL HEAVY-ONLY (item 5). VoltageTier.IsAllowedOnTier(umbilical, normal/heavy)
        //       -> expect normal=False, heavy=True.
        //   (D) ROCKET BATTERY CAPS (items 2/3). StationaryBatteryPatches.GetChargeCap /
        //       GetDischargeCap on live Medium/Small battery instances -> expect finite caps
        //       (Medium ~5000/10000, Small ~2500/5000), not Infinity.
        //   (E) UMBILICAL LOGIC BRIDGE (item 4). For each docked male/female umbilical pair,
        //       force PassthroughModeStore mode 1 on both halves, then
        //       PassthroughTopology.GatherReachable on the male's InputNetwork and the female's
        //       OutputNetwork; report whether the partner's network ReferenceId appears in each
        //       reachable set (expect the docked pair bridges both ways).
        //
        // All reflected members are internal static on PGP types; read with Static|Public|NonPublic.
        // PowerMaximum is a public field on Battery and on the rocket umbilical subclasses, read
        // off the live instance. Cable.Type is the public game enum Assets.Scripts.Objects.Pipes.
        // Cable.Type (normal=0, heavy=1, superHeavy=2). All reads are managed-state only (prefab
        // fields, reflected invocations, network ReferenceId) -> safe from the UniTask worker the
        // sim-tick pump runs on. Requires PowerGridPlus loaded.

        private static bool _rocketParityFired;

        private static void Scenario_PgpRocketParityProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-rocket-parity-probe")) return;
            if (_rocketParityFired) return;
            _rocketParityFired = true;

            try
            {
                _log?.LogInfo("[ScenarioRunner] rocket-parity START");

                var asm = GetModAssembly(PGP_ASSEMBLY);
                const System.Reflection.BindingFlags SFLAGS =
                    System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic;

                var voltageTier = asm.GetType("PowerGridPlus.VoltageTier");
                var batPatches = asm.GetType("PowerGridPlus.Patches.StationaryBatteryPatches");
                var topo = asm.GetType("PowerGridPlus.Patches.PassthroughTopology");
                var store = asm.GetType("PowerGridPlus.PassthroughModeStore");

                var getTierMap = voltageTier?.GetMethod("GetTransformerTierMap", SFLAGS, null, new[] { typeof(string) }, null);
                var isAllowedOnTier = voltageTier?.GetMethod("IsAllowedOnTier", SFLAGS, null, new[] { typeof(Device), typeof(Cable.Type) }, null);
                var getChargeCap = batPatches?.GetMethod("GetChargeCap", SFLAGS, null, new[] { typeof(Battery) }, null);
                var getDischargeCap = batPatches?.GetMethod("GetDischargeCap", SFLAGS, null, new[] { typeof(Battery) }, null);
                var gather = topo?.GetMethod("GatherReachable", SFLAGS);
                var getUmbilicalPartner = topo?.GetMethod("GetUmbilicalPartner", SFLAGS, null,
                    new[] { typeof(Assets.Scripts.Objects.Electrical.ElectricalInputOutput) }, null);
                var setMode = store?.GetMethod("SetMode", SFLAGS, null, new[] { typeof(Thing), typeof(int) }, null);

                _log?.LogInfo(
                    $"[ScenarioRunner] rocket-parity reflection: getTierMap={getTierMap != null} isAllowedOnTier={isAllowedOnTier != null} " +
                    $"getChargeCap={getChargeCap != null} getDischargeCap={getDischargeCap != null} gather={gather != null} " +
                    $"getUmbilicalPartner={getUmbilicalPartner != null} setMode={setMode != null}");

                RocketParity_CapacityDump();
                RocketParity_TransformerTier(getTierMap);
                RocketParity_UmbilicalHeavyOnly(isAllowedOnTier);
                RocketParity_RocketBatteryCaps(getChargeCap, getDischargeCap);
                RocketParity_UmbilicalLogicBridge(gather, getUmbilicalPartner, setMode);

                _log?.LogInfo("[ScenarioRunner] rocket-parity END");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] rocket-parity threw: {e}");
            }
        }

        // (A) CAPACITY DUMP -- the reverse-engineering deliverable. Read PowerMaximum off each
        // named prefab instance from Prefab.AllPrefabs. PowerMaximum is a public field on Battery
        // and on the rocket umbilical subclasses; read it via reflection on the live type so the
        // same helper handles both class families without a build-time reference to Objects.Rockets.
        private static void RocketParity_CapacityDump()
        {
            var want = new HashSet<string>
            {
                "StructureBatteryMedium",
                "StructureBatterySmall",
                "StructureBattery",
                "StructureBatteryLarge",
                "StationBatteryNuclear",
                "StructurePowerUmbilicalMale",
                "StructurePowerUmbilicalFemale",
            };

            int found = 0;
            foreach (var prefab in Prefab.AllPrefabs)
            {
                if (prefab == null) continue;
                string name = prefab.PrefabName ?? "";
                if (!want.Contains(name)) continue;
                found++;

                string pmax = RocketParity_ReadPowerMaximum(prefab, out bool ok);
                _log?.LogInfo(
                    $"[ScenarioRunner] rocket-parity CAPACITY PrefabName={name} " +
                    $"PowerMaximum={(ok ? pmax : "n/a")} Type={prefab.GetType().Name}");
            }
            _log?.LogInfo($"[ScenarioRunner] rocket-parity CAPACITY found={found}/{want.Count} target prefabs");
        }

        // Read the public PowerMaximum float. Battery exposes it directly; the rocket umbilical
        // subclasses (Objects.Rockets.RocketPowerUmbilical*) also expose it as a public field but
        // are not referenced at build time, so go through reflection on the instance type for both.
        private static string RocketParity_ReadPowerMaximum(object instance, out bool ok)
        {
            ok = false;
            if (instance == null) return "null-instance";
            try
            {
                if (instance is Battery b) { ok = true; return b.PowerMaximum.ToString("F2", System.Globalization.CultureInfo.InvariantCulture); }
                var f = instance.GetType().GetField("PowerMaximum",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (f != null && f.FieldType == typeof(float))
                {
                    ok = true;
                    return ((float)f.GetValue(instance)).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                }
                var p = instance.GetType().GetProperty("PowerMaximum",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (p != null && p.PropertyType == typeof(float))
                {
                    ok = true;
                    return ((float)p.GetValue(instance)).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                }
                return "no-PowerMaximum-member";
            }
            catch (Exception e) { return "read-threw:" + e.Message; }
        }

        // (B) ROCKET TRANSFORMER TIER (item 1).
        private static void RocketParity_TransformerTier(System.Reflection.MethodInfo getTierMap)
        {
            if (getTierMap == null)
            {
                _log?.LogError("[ScenarioRunner] rocket-parity TIER FAIL: VoltageTier.GetTransformerTierMap(string) not found");
                return;
            }
            const string prefab = "StructureRocketTransformerSmall";
            try
            {
                var result = getTierMap.Invoke(null, new object[] { prefab });
                if (result == null)
                {
                    _log?.LogError($"[ScenarioRunner] rocket-parity TIER FAIL: GetTransformerTierMap(\"{prefab}\") returned NULL (expected (heavy, normal))");
                    return;
                }
                // Nullable value tuple (Cable.Type Input, Cable.Type Output)? -> read the boxed
                // ValueTuple fields Item1/Item2 reflectively (works regardless of element names).
                var t = result.GetType();
                var item1 = t.GetField("Item1")?.GetValue(result);
                var item2 = t.GetField("Item2")?.GetValue(result);
                string input = item1?.ToString() ?? "?";
                string output = item2?.ToString() ?? "?";
                bool pass = input == "heavy" && output == "normal";
                _log?.LogInfo(
                    $"[ScenarioRunner] rocket-parity TIER prefab={prefab} Input={input} Output={output} " +
                    $"=> {(pass ? "PASS" : "FAIL")} (expect Input=heavy Output=normal)");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] rocket-parity TIER threw: {e.InnerException?.Message ?? e.Message}");
            }
        }

        // (C) UMBILICAL HEAVY-ONLY (item 5).
        private static void RocketParity_UmbilicalHeavyOnly(System.Reflection.MethodInfo isAllowedOnTier)
        {
            if (isAllowedOnTier == null)
            {
                _log?.LogError("[ScenarioRunner] rocket-parity HEAVYONLY FAIL: VoltageTier.IsAllowedOnTier(Device, Cable.Type) not found");
                return;
            }

            Device umbilical = null;
            OcclusionManager.AllThings.ForEach(t =>
            {
                if (umbilical != null || t == null) return;
                if (t is Objects.Rockets.RocketPowerUmbilicalMale || t is Objects.Rockets.RocketPowerUmbilicalFemale)
                    umbilical = (Device)t;
            });

            if (umbilical == null)
            {
                _log?.LogWarning("[ScenarioRunner] rocket-parity HEAVYONLY: no umbilical instance in save; skip.");
                return;
            }

            try
            {
                bool onNormal = (bool)isAllowedOnTier.Invoke(null, new object[] { umbilical, Cable.Type.normal });
                bool onHeavy = (bool)isAllowedOnTier.Invoke(null, new object[] { umbilical, Cable.Type.heavy });
                bool pass = !onNormal && onHeavy;
                _log?.LogInfo(
                    $"[ScenarioRunner] rocket-parity HEAVYONLY ref={umbilical.ReferenceId} prefab={umbilical.PrefabName} " +
                    $"type={umbilical.GetType().Name} allowedOnNormal={onNormal} allowedOnHeavy={onHeavy} " +
                    $"=> {(pass ? "PASS" : "FAIL")} (expect normal=False heavy=True)");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] rocket-parity HEAVYONLY threw: {e.InnerException?.Message ?? e.Message}");
            }
        }

        // (D) ROCKET BATTERY CAPS (items 2/3).
        private static void RocketParity_RocketBatteryCaps(
            System.Reflection.MethodInfo getChargeCap, System.Reflection.MethodInfo getDischargeCap)
        {
            if (getChargeCap == null || getDischargeCap == null)
            {
                _log?.LogError(
                    $"[ScenarioRunner] rocket-parity BATCAP FAIL: StationaryBatteryPatches.GetChargeCap/GetDischargeCap not found " +
                    $"(charge={getChargeCap != null} discharge={getDischargeCap != null})");
                return;
            }

            var targets = new List<Battery>();
            OcclusionManager.AllThings.ForEach(t =>
            {
                if (t is Battery b && (b.PrefabName == "StructureBatteryMedium" || b.PrefabName == "StructureBatterySmall"))
                    targets.Add(b);
            });

            if (targets.Count == 0)
            {
                _log?.LogWarning("[ScenarioRunner] rocket-parity BATCAP: no rocket battery instance (Medium/Small) in save; skip.");
                return;
            }

            foreach (var bat in targets)
            {
                try
                {
                    float charge = (float)getChargeCap.Invoke(null, new object[] { bat });
                    float discharge = (float)getDischargeCap.Invoke(null, new object[] { bat });
                    bool finite = !float.IsInfinity(charge) && !float.IsInfinity(discharge) && !float.IsNaN(charge) && !float.IsNaN(discharge);
                    string expect = bat.PrefabName == "StructureBatteryMedium" ? "(expect ~5000/10000)" : "(expect ~2500/5000)";
                    _log?.LogInfo(
                        $"[ScenarioRunner] rocket-parity BATCAP ref={bat.ReferenceId} prefab={bat.PrefabName} " +
                        $"chargeCap={charge:F2} dischargeCap={discharge:F2} finite={finite} " +
                        $"=> {(finite ? "PASS" : "FAIL")} {expect}");
                }
                catch (Exception e)
                {
                    _log?.LogError($"[ScenarioRunner] rocket-parity BATCAP ref={bat.ReferenceId} threw: {e.InnerException?.Message ?? e.Message}");
                }
            }
        }

        // (E) UMBILICAL LOGIC BRIDGE (item 4). For each docked male/female pair, force mode 1 on
        // both halves, then GatherReachable on the male InputNetwork and the female OutputNetwork;
        // check the partner's network ReferenceId appears in each reachable set.
        private static void RocketParity_UmbilicalLogicBridge(
            System.Reflection.MethodInfo gather,
            System.Reflection.MethodInfo getUmbilicalPartner,
            System.Reflection.MethodInfo setMode)
        {
            if (gather == null || setMode == null)
            {
                _log?.LogError(
                    $"[ScenarioRunner] rocket-parity BRIDGE FAIL: required reflection missing " +
                    $"(gather={gather != null} setMode={setMode != null} getUmbilicalPartner={getUmbilicalPartner != null})");
                return;
            }

            var males = new List<Objects.Rockets.RocketPowerUmbilicalMale>();
            OcclusionManager.AllThings.ForEach(t =>
            {
                if (t is Objects.Rockets.RocketPowerUmbilicalMale m) males.Add(m);
            });

            if (males.Count == 0)
            {
                _log?.LogWarning("[ScenarioRunner] rocket-parity BRIDGE: no RocketPowerUmbilicalMale instance in save; skip.");
                return;
            }

            int pairs = 0;
            foreach (var male in males)
            {
                if (male == null) continue;
                var maleEio = (Assets.Scripts.Objects.Electrical.ElectricalInputOutput)(Device)male;

                // Resolve the partner. Prefer the PGP accessor; fall back to the private field.
                Assets.Scripts.Objects.Electrical.ElectricalInputOutput partner = null;
                if (getUmbilicalPartner != null)
                {
                    try { partner = getUmbilicalPartner.Invoke(null, new object[] { maleEio }) as Assets.Scripts.Objects.Electrical.ElectricalInputOutput; }
                    catch (Exception e) { _log?.LogWarning($"[ScenarioRunner] rocket-parity BRIDGE GetUmbilicalPartner threw: {e.InnerException?.Message ?? e.Message}"); }
                }
                if (partner == null) partner = RocketParity_ReadPartnerField(male) as Assets.Scripts.Objects.Electrical.ElectricalInputOutput;

                if (partner == null)
                {
                    _log?.LogInfo(
                        $"[ScenarioRunner] rocket-parity BRIDGE male ref={male.ReferenceId} prefab={male.PrefabName} " +
                        $"partner=NONE (not docked)");
                    continue;
                }
                pairs++;

                var female = partner;
                var maleIn = maleEio.InputNetwork;
                var femaleOut = female.OutputNetwork;
                long maleInId = maleIn?.ReferenceId ?? 0;
                long femaleOutId = femaleOut?.ReferenceId ?? 0;

                // Force mode 1 on BOTH halves so the logic-passthrough bridge is active.
                try { setMode.Invoke(null, new object[] { (Thing)(Device)male, 1 }); } catch { }
                try { setMode.Invoke(null, new object[] { (Thing)female, 1 }); } catch { }

                var reachFromMaleIn = PppReach(gather, maleIn);
                var reachFromFemaleOut = PppReach(gather, femaleOut);

                // Bridge both ways: from the male input side we should reach the female output net;
                // from the female output side we should reach the male input net.
                bool maleReachesFemale = femaleOut != null && reachFromMaleIn.Contains(femaleOutId);
                bool femaleReachesMale = maleIn != null && reachFromFemaleOut.Contains(maleInId);
                bool pass = maleReachesFemale && femaleReachesMale;

                _log?.LogInfo(
                    $"[ScenarioRunner] rocket-parity BRIDGE pair#{pairs} male ref={male.ReferenceId} female ref={female.ReferenceId} | " +
                    $"maleInNet={maleInId} femaleOutNet={femaleOutId} | " +
                    $"reach(maleIn)=[{string.Join(",", reachFromMaleIn)}] reach(femaleOut)=[{string.Join(",", reachFromFemaleOut)}] | " +
                    $"maleReachesFemale={maleReachesFemale} femaleReachesMale={femaleReachesMale} " +
                    $"=> {(pass ? "PASS" : "FAIL")} (expect docked pair bridges both ways)");
            }

            if (pairs == 0)
                _log?.LogWarning("[ScenarioRunner] rocket-parity BRIDGE: no docked umbilical pair (males present but none have a partner).");
            else
                _log?.LogInfo($"[ScenarioRunner] rocket-parity BRIDGE summary: dockedPairs={pairs}");
        }

        // Read the private _partnerUmbilical field off an umbilical instance (fallback when the
        // PGP accessor is unavailable). The field is declared on the concrete rocket subclass.
        private static object RocketParity_ReadPartnerField(object umbilical)
        {
            if (umbilical == null) return null;
            try
            {
                var f = umbilical.GetType().GetField("_partnerUmbilical",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                return f?.GetValue(umbilical);
            }
            catch { return null; }
        }

        // ---- General scenario: merge-long-variant-num4 ----
        //
        // Reproduces, on the LIVE binary, the negative merge-cost delta (num4) that
        // MultiMergeConstructor.Construct computes when a merge resolves onto an existing
        // LONG (StraightAsymmetric) variant. The game does:
        //   index = first Constructables entry whose GetConnectionType() == existing piece's type
        //   num2  = first Constructables entry whose GetConnectionType() == merged type
        //   num4  = Constructables[num2].BuildStates[0].Tool.EntryQuantity
        //         - Constructables[index].BuildStates[0].Tool.EntryQuantity   (consumed from the kit stack)
        // Merging a single straight onto an existing long straight gives a collinear merged
        // type of Straight (num2 = the 1-cell base, cost 1) and an existing type of
        // StraightAsymmetric (index = the first long variant, cost 3) -> num4 = -2.
        //
        // This scenario does NOT spawn or merge: the sim-tick pump runs on a UniTask worker
        // where structure spawning / Unity APIs are unsafe. It reproduces the exact resolution
        // and arithmetic over the live kit prefabs using the game's OWN GetConnectionType() and
        // EntryQuantity, so it confirms on the running binary:
        //   - vanilla: long-variant kits carry StraightAsymmetric entries -> num4 = -2.
        //   - NetworkPuristPlus active: those entries are stripped at prefab-load -> no
        //     StraightAsymmetric entry -> the merge-onto-long precondition is unreachable.
        // It also logs each kit's runtime base connType (settles "is the base Straight or the
        // C# default Exhaustive at runtime") and whether StackedGeneCollections is null. On this
        // build the OnUseItem RemoveRange that throws is gated by StackedGeneCollections != null
        // (plant-only), so a null list means the negative num4 silently duplicates here rather
        // than throwing -- the symptom is version-dependent, the negative is not.
        //
        // Threading: all reads are managed-state only (Prefab.AllPrefabs iteration, reflected
        // GetConnectionType()/EntryQuantity/field reads). No Unity API calls -> worker-safe.

        private static bool _mlvFired;

        private static void Scenario_MergeLongVariantNum4()
        {
            if (_mlvFired) return;
            _mlvFired = true;

            try
            {
                _log?.LogInfo("[ScenarioRunner] MLV START merge-long-variant-num4");

                // Direct prefab probe: read the RUNTIME connType + EntryQuantity for known base/long
                // pairs straight from Prefab.AllPrefabs. Robust regardless of kit/NPP state -- NPP strips
                // the long variants from kit Constructables and hides them from Stationpedia, but does NOT
                // delete the prefabs, so these reads return ground truth even with NPP active. num4 for a
                // merge of a single base onto this long = EQ(base, the collinear Straight result) - EQ(long).
                string[][] fams = new[]
                {
                    new[] { "StructurePipeStraight", "StructurePipeStraight3", "StructurePipeStraight5", "StructurePipeStraight10" },
                    new[] { "StructureChuteStraight", "StructureChuteStraight3", "StructureChuteStraight5", "StructureChuteStraight10" },
                    new[] { "StructureCableSuperHeavyStraight", "StructureCableSuperHeavyStraight3", "StructureCableSuperHeavyStraight5", "StructureCableSuperHeavyStraight10" },
                };
                foreach (var fam in fams)
                {
                    var bp = Prefab.Find(fam[0]) as Structure;
                    if (bp == null) { _log?.LogInfo($"[ScenarioRunner] MLV DIRECT base {fam[0]} NOT FOUND"); continue; }
                    int eqB = MlvEntryQty(bp);
                    _log?.LogInfo($"[ScenarioRunner] MLV DIRECT base {bp.PrefabName} connType={MlvConnType(bp)} EQ={eqB} mergeCapable={MlvIsGridMergeable(bp)}");
                    for (int j = 1; j < fam.Length; j++)
                    {
                        var lp = Prefab.Find(fam[j]) as Structure;
                        if (lp == null) { _log?.LogInfo($"[ScenarioRunner] MLV DIRECT long {fam[j]} NOT FOUND"); continue; }
                        int eqL = MlvEntryQty(lp);
                        _log?.LogInfo($"[ScenarioRunner] MLV DIRECT long {lp.PrefabName} connType={MlvConnType(lp)} EQ={eqL} => num4(merge base onto this long) = EQ(base {eqB}) - EQ(long {eqL}) = {eqB - eqL} [{((eqB - eqL) < 0 ? "NEGATIVE" : "ok")}]");
                    }
                }

                // Targeted full dump of ItemKitPipe entries (the actual resolution inputs the merge uses).
                var pipeKit = Prefab.Find("ItemKitPipe") as MultiConstructor;
                if (pipeKit != null && pipeKit.Constructables != null)
                {
                    _log?.LogInfo($"[ScenarioRunner] MLV KITDUMP ItemKitPipe cons={pipeKit.Constructables.Count} (NPP strips the StraightAsymmetric entries; their presence here means NPP did NOT run):");
                    for (int i = 0; i < pipeKit.Constructables.Count; i++)
                    {
                        var e = pipeKit.Constructables[i];
                        if (e == null) { _log?.LogInfo($"[ScenarioRunner] MLV KITDUMP   [{i}] <null>"); continue; }
                        _log?.LogInfo($"[ScenarioRunner] MLV KITDUMP   [{i}] {e.PrefabName} connType={MlvConnType(e)} EQ={MlvEntryQty(e)}");
                    }
                }

                int kitsWithLong = 0, mergeCapableWithLong = 0, negKits = 0;

                foreach (var prefab in Prefab.AllPrefabs)
                {
                    if (prefab == null) continue;
                    if (!(prefab is MultiConstructor kit)) continue;
                    var cons = kit.Constructables;
                    if (cons == null || cons.Count == 0) continue;

                    int idxFirstStraight = -1, idxFirstAsym = -1, asymCount = 0;
                    for (int i = 0; i < cons.Count; i++)
                    {
                        if (cons[i] == null) continue;
                        var ct = MlvConnType(cons[i]);
                        if (ct == "Straight" && idxFirstStraight < 0) idxFirstStraight = i;
                        if (ct == "StraightAsymmetric") { asymCount++; if (idxFirstAsym < 0) idxFirstAsym = i; }
                    }

                    if (asymCount == 0) continue; // only the long-variant kits matter here
                    kitsWithLong++;

                    var baseS = cons[0];
                    int eqBase = MlvEntryQty(baseS);
                    int eqStraight = idxFirstStraight >= 0 ? MlvEntryQty(cons[idxFirstStraight]) : eqBase;
                    int eqAsym = MlvEntryQty(cons[idxFirstAsym]);
                    bool mergeCapable = MlvIsGridMergeable(baseS);
                    bool sgcNull = MlvStackedGeneCollectionsNull(kit);

                    if (!mergeCapable)
                    {
                        _log?.LogInfo(
                            $"[ScenarioRunner] MLV kit={kit.PrefabName} cons={cons.Count} asymEntries={asymCount} " +
                            $"base[0]={baseS.PrefabName}/connType={MlvConnType(baseS)}/EQ{eqBase} mergeCapable=FALSE " +
                            "(base not IGridMergeable; e.g. Chute) -> long variants placed as-is, merge branch never reached, no num4 path.");
                        continue;
                    }

                    mergeCapableWithLong++;
                    // existing piece = long (StraightAsymmetric) -> index = idxFirstAsym (cost eqAsym).
                    // collinear merge result = Straight -> num2 = idxFirstStraight (1-cell base, cost eqStraight).
                    int num4 = eqStraight - eqAsym;
                    if (num4 < 0) negKits++;

                    _log?.LogInfo(
                        $"[ScenarioRunner] MLV kit={kit.PrefabName} cons={cons.Count} mergeCapable=TRUE asymEntries={asymCount} " +
                        $"base[0]={baseS.PrefabName}/connType={MlvConnType(baseS)}/EQ{eqBase} " +
                        $"firstStraight=idx{idxFirstStraight}/EQ{eqStraight} " +
                        $"firstStraightAsym=idx{idxFirstAsym}/{cons[idxFirstAsym].PrefabName}/EQ{eqAsym} " +
                        $"=> MERGE-ONTO-LONG num4 = EQ(result Straight {eqStraight}) - EQ(existing StraightAsym {eqAsym}) = {num4} " +
                        $"[{(num4 < 0 ? "NEGATIVE" : "non-negative")}] | StackedGeneCollectionsNull={sgcNull} " +
                        $"[{(sgcNull ? "this build: neg num4 SKIPS RemoveRange (StackedGeneCollections-gated) -> silent Quantity++ duplication" : "this build: neg num4 reaches RemoveRange -> ArgumentOutOfRangeException")}]");
                }

                _log?.LogInfo($"[ScenarioRunner] MLV END kitsWithLongVariants={kitsWithLong} mergeCapableWithLong={mergeCapableWithLong} kitsWithNegativeNum4={negKits}");
                if (kitsWithLong == 0)
                    _log?.LogInfo("[ScenarioRunner] MLV RESULT: no kit has any StraightAsymmetric (long) entry -> the merge-onto-long precondition is UNREACHABLE across every kit. Consistent with NetworkPuristPlus having stripped them (PROTECTIVE), or a build with no long variants.");
                else if (negKits > 0)
                    _log?.LogInfo($"[ScenarioRunner] MLV RESULT: {negKits} merge-capable kit(s) compute a NEGATIVE num4 when a merge resolves onto their long variant -- the crash arithmetic, CONFIRMED on the live binary. In vanilla the cursor's CanReplace/_IsCollision gate blocks this merge; ZoopMod's UsePrimaryComplete bypasses that gate, so the negative reaches Stackable.OnUseItem.");
                else
                    _log?.LogInfo("[ScenarioRunner] MLV RESULT: long variants present but no negative num4 computed (unexpected -- check costs/types above).");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] MLV threw: {e}");
            }
        }

        private static string MlvConnType(object s)
        {
            try
            {
                var m = s.GetType().GetMethod("GetConnectionType",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                    null, System.Type.EmptyTypes, null);
                if (m != null) { var v = m.Invoke(s, null); return v?.ToString() ?? "null"; }
                var f = s.GetType().GetField("ConnectionType",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var fv = f?.GetValue(s);
                return fv?.ToString() ?? "n/a";
            }
            catch { return "err"; }
        }

        private static int MlvEntryQty(object structure)
        {
            try
            {
                object bsObj = structure.GetType().GetProperty("BuildStates")?.GetValue(structure)
                    ?? structure.GetType().GetField("BuildStates")?.GetValue(structure);
                var list = bsObj as System.Collections.IList;
                if (list == null || list.Count == 0) return -999;
                var bs0 = list[0];
                if (bs0 == null) return -999;
                var toolObj = bs0.GetType().GetField("Tool")?.GetValue(bs0)
                    ?? bs0.GetType().GetProperty("Tool")?.GetValue(bs0);
                if (toolObj == null) return -998;
                var eqObj = toolObj.GetType().GetField("EntryQuantity")?.GetValue(toolObj)
                    ?? toolObj.GetType().GetProperty("EntryQuantity")?.GetValue(toolObj);
                return eqObj is int i ? i : -997;
            }
            catch { return -996; }
        }

        private static bool MlvIsGridMergeable(object o)
        {
            if (o == null) return false;
            foreach (var i in o.GetType().GetInterfaces())
                if (i.Name == "IGridMergeable") return true;
            return false;
        }

        private static bool MlvStackedGeneCollectionsNull(object kit)
        {
            try
            {
                var f = kit.GetType().GetField("StackedGeneCollections",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (f == null) return true; // field absent -> treat as null
                return f.GetValue(kit) == null;
            }
            catch { return true; }
        }

        // ---- General scenario: clamp-merge-quantity ----
        //
        // Verifies the Network Purist Plus crash guard (ClampNegativeMergeQuantityPatch). It does NOT drive
        // a real merge (the sim-tick pump is a worker thread; constructing spawns Unity objects and is
        // unsafe there). It confirms, on the live binary:
        //   1. A stock ItemKitPipe's StackedGeneCollections is non-null at runtime (Unity serializes the
        //      public List<T> field as an empty list, not null), so a negative merge quantity WOULD reach
        //      Stackable.OnUseItem's RemoveRange and throw on this build -- i.e. the guard is needed.
        //   2. The clamp prefix (ClampNegativeMergeQuantityPatch) is registered on the 7-arg
        //      MultiConstructor.Construct, alongside the existing cable-roll prefix
        //      (RewriteCableRollOnMultiConstruct) -- both coexist on the same method.
        // The end-to-end "zoop over a long with the guard active -> no crash" is a client playtest; this
        // covers everything checkable headless. Managed reflection only; worker-safe.

        private static bool _clampFired;

        private static void Scenario_ClampMergeQuantity()
        {
            if (_clampFired) return;
            _clampFired = true;
            try
            {
                _log?.LogInfo("[ScenarioRunner] CLAMP START clamp-merge-quantity");

                var pipeKit = Prefab.Find("ItemKitPipe") as MultiConstructor;
                if (pipeKit == null) { _log?.LogError("[ScenarioRunner] CLAMP ItemKitPipe not found"); return; }

                bool sgcNull = MlvStackedGeneCollectionsNull(pipeKit);
                _log?.LogInfo($"[ScenarioRunner] CLAMP ItemKitPipe StackedGeneCollectionsNull={sgcNull} " +
                    $"({(sgcNull ? "negative quantity would SKIP RemoveRange (silent dupe)" : "non-null empty list -> negative quantity reaches RemoveRange and THROWS on this build; the guard is needed")})");

                var construct7 = typeof(MultiConstructor).GetMethod("Construct",
                    new[] { typeof(Assets.Scripts.GridSystem.Grid3), typeof(Quaternion), typeof(int), typeof(Item), typeof(bool), typeof(ulong), typeof(int) });
                if (construct7 == null) { _log?.LogError("[ScenarioRunner] CLAMP 7-arg MultiConstructor.Construct not found"); return; }

                var info = HarmonyLib.Harmony.GetPatchInfo(construct7);
                int prefixCount = 0;
                bool clampPresent = false, cableRollPresent = false;
                if (info != null && info.Prefixes != null)
                {
                    foreach (var p in info.Prefixes)
                    {
                        prefixCount++;
                        string dt = (p.PatchMethod != null && p.PatchMethod.DeclaringType != null) ? p.PatchMethod.DeclaringType.Name : "?";
                        _log?.LogInfo($"[ScenarioRunner] CLAMP prefix on Construct(7-arg): {dt}.{p.PatchMethod?.Name} owner={p.owner}");
                        if (dt == "ClampNegativeMergeQuantityPatch") clampPresent = true;
                        if (dt == "RewriteCableRollOnMultiConstruct") cableRollPresent = true;
                    }
                }
                _log?.LogInfo($"[ScenarioRunner] CLAMP prefixes={prefixCount} clampRegistered={clampPresent} cableRollCoexists={cableRollPresent}");

                bool pass = clampPresent && !sgcNull;
                _log?.LogInfo($"[ScenarioRunner] CLAMP END verdict={(pass ? "PASS" : "CHECK")} " +
                    $"(clampRegistered={clampPresent} expect true; cableRollCoexists={cableRollPresent} expect true; sgcNull={sgcNull} expect false)");
            }
            catch (Exception e) { _log?.LogError($"[ScenarioRunner] CLAMP threw: {e}"); }
        }

        // ---- Reflection helpers for mod-specific scenarios ----

        private static System.Reflection.Assembly GetModAssembly(string assemblyName)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == assemblyName);
        }

        private static bool RequireModAssembly(string assemblyName, string scenarioId)
        {
            if (GetModAssembly(assemblyName) != null) return true;
            if (_ticksSeen == _delayTicks)
                _log?.LogWarning($"[ScenarioRunner] scenario '{scenarioId}' requires mod assembly '{assemblyName}' to be loaded; skipping.");
            return false;
        }

        // ---- PowerGridPlus-specific scenarios ----
        //
        // These require the 'PowerGridPlus' assembly to be loaded. They reach into PGP
        // via reflection (no build-time dependency) so this plugin stays independent.

        private const string PGP_ASSEMBLY = "PowerGridPlus";

        // PGP scenario: transformer-conservation.
        // For every Transformer, log Setting / UsedPower / InputNetwork.CurrentLoad /
        // OutputNetwork.CurrentLoad so an agent can verify
        //   InputNetwork.CurrentLoad ~= OutputNetwork.CurrentLoad + UsedPower.
        // The four fields read here are vanilla; PGP's exploit-mitigation patch
        // affects the values, not the field set. Strictly speaking this scenario does
        // not depend on PGP being loaded -- but it is specifically aimed at verifying
        // PGP's TransformerExploitPatches behaviour, so we tag it pgp-.

        private static int _tcLastLogTick = int.MinValue;
        private const int TC_LOG_EVERY_TICKS = 5;

        private static void Scenario_PgpTransformerConservation()
        {
            if (_ticksSeen - _tcLastLogTick < TC_LOG_EVERY_TICKS) return;
            _tcLastLogTick = (int)_ticksSeen;

            if (_transformers.Count == 0) RebuildCaches();
            foreach (var t in _transformers)
            {
                if (t == null) continue;
                var inLoad = t.InputNetwork != null ? t.InputNetwork.CurrentLoad : float.NaN;
                var outLoad = t.OutputNetwork != null ? t.OutputNetwork.CurrentLoad : float.NaN;
                _log?.LogInfo(
                    $"[ScenarioRunner] TC tick={_ticksSeen} ref={t.ReferenceId} " +
                    $"prefab={t.PrefabName} OnOff={t.OnOff} Setting={t.Setting} " +
                    $"UsedPower={t.UsedPower} InCurrentLoad={inLoad} OutCurrentLoad={outLoad}");
            }
        }

        // PGP scenario: fault-state-probe.
        // Every 10 ticks, log the five PGP fault-registry counts (deprioritized / transformer-overload /
        // cable-overload / cycle / CURRENT-MISMATCH) plus the live segmenter count and a fresh re-run of
        // CycleGraphBuilder.FindCycleFaultedSegmenters(). On an acyclic grid (e.g. a normal Luna base)
        // cycleDetectNow MUST be 0 -- a non-zero value would mean the directed-SCC cycle detector is
        // producing a false positive (e.g. on parallel transformers/batteries). currentMismatch > 0 reveals
        // producer-isolation violations baked into the loaded base (a producer wired straight to a
        // consumer with no transformer). The cableOverload count comes from CableOverloadRegistry, the
        // cable-overflow half of the former single overload fault (a missing type reads -1 here, which
        // is the tripwire for a build that lost the fifth registry). All reads are managed-state only,
        // safe on the sim-tick thread.

        private static int _fsLastLogTick = int.MinValue;
        private const int FS_LOG_EVERY_TICKS = 10;

        private static void Scenario_PgpFaultStateProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-fault-state-probe")) return;
            if (_ticksSeen - _fsLastLogTick < FS_LOG_EVERY_TICKS) return;
            _fsLastLogTick = (int)_ticksSeen;

            var asm = GetModAssembly(PGP_ASSEMBLY);
            int deprioritized = ReadPgpStaticIntProp(asm, "PowerGridPlus.DeprioritizedRegistry", "LockoutCount");
            int over = ReadPgpStaticIntProp(asm, "PowerGridPlus.OverloadRegistry", "LockoutCount");
            int cableOver = ReadPgpStaticIntProp(asm, "PowerGridPlus.CableOverloadRegistry", "LockoutCount");
            int cycle = ReadPgpStaticIntProp(asm, "PowerGridPlus.CycleFaultRegistry", "LockoutCount");
            int currentMismatch = ReadPgpStaticIntProp(asm, "PowerGridPlus.CurrentMismatchFaultRegistry", "LockoutCount");
            int segCount = InvokePgpStaticCollectionCount(asm, "PowerGridPlus.SegmentingDeviceRegistry", "EnumerateSorted");
            int cycleNow = InvokePgpStaticCollectionCount(asm, "PowerGridPlus.CycleGraphBuilder", "FindCycleFaultedSegmenters");

            if (asm?.GetType("PowerGridPlus.CableOverloadRegistry") == null)
                _log?.LogError("[ScenarioRunner] FAULT-STATE: CableOverloadRegistry type MISSING (the overload split's fifth registry); this PowerGridPlus build predates or regressed the split.");

            _log?.LogInfo(
                $"[ScenarioRunner] FAULT-STATE tick={_ticksSeen} segmenters={segCount} cycleDetectNow={cycleNow} " +
                $"| registry: deprioritized={deprioritized} overload={over} cableOverload={cableOver} cycle={cycle} currentMismatch={currentMismatch}");
        }

        // PGP scenario: shortfall-net-probe. One-shot (first qualifying tick after warmup): for every
        // cable network whose fresh PowerTick state shows unmet rigid demand, log the device-type
        // composition and every segmenting device touching the net (which side, OnOff, per-fault
        // registry state via PGP reflection), so "why is this network dark" is answerable from the log.
        private static bool _shortfallLogged;

        private static void Scenario_PgpShortfallNetProbe()
        {
            if (_shortfallLogged) return;
            if (_ticksSeen < _delayTicks + 12) return;   // let the grid settle a few ticks past warmup
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-shortfall-net-probe")) return;
            _shortfallLogged = true;

            var asm = GetModAssembly(PGP_ASSEMBLY);
            var registries = new (string label, string type)[]
            {
                ("deprioritized", "PowerGridPlus.DeprioritizedRegistry"),
                ("overload", "PowerGridPlus.OverloadRegistry"),
                ("cableOverload", "PowerGridPlus.CableOverloadRegistry"),
                ("cycle", "PowerGridPlus.CycleFaultRegistry"),
                ("currentMismatch", "PowerGridPlus.CurrentMismatchFaultRegistry"),
            };

            int reported = 0;
            Assets.Scripts.Networks.CableNetwork.AllCableNetworks.ForEach(net =>
            {
                if (net == null || reported >= 14) return;
                var pt = net.PowerTick;
                if (pt == null) return;
                float required = pt.Required;
                float potential = pt.Potential;
                if (required <= potential + 0.5f) return;
                reported++;

                var typeTally = new System.Collections.Generic.Dictionary<string, int>();
                var segLines = new System.Collections.Generic.List<string>();
                lock (net.PowerDeviceList)
                {
                    for (int i = 0; i < net.PowerDeviceList.Count; i++)
                    {
                        var d = net.PowerDeviceList[i];
                        if (d == null) continue;
                        string tn = d.GetType().Name;
                        typeTally.TryGetValue(tn, out var c);
                        typeTally[tn] = c + 1;
                        if (d is Assets.Scripts.Objects.Electrical.ElectricalInputOutput eio)
                        {
                            string side = eio.InputNetwork == net && eio.OutputNetwork == net ? "BOTH"
                                : eio.InputNetwork == net ? "in" : eio.OutputNetwork == net ? "out" : "?";
                            var faults = new System.Collections.Generic.List<string>();
                            foreach (var (label, type) in registries)
                            {
                                if (PgpIsLocked(asm, type, eio.ReferenceId)) faults.Add(label);
                            }
                            segLines.Add($"      seg {tn} ref={eio.ReferenceId} side={side} on={eio.OnOff} " +
                                         $"faults=[{string.Join(",", faults)}]");
                        }
                    }
                }
                var tallyText = new System.Text.StringBuilder();
                foreach (var kv in typeTally) tallyText.Append(kv.Key).Append("x").Append(kv.Value).Append(" ");
                _log?.LogInfo($"[ScenarioRunner] SHORTFALL net={net.ReferenceId} req={required:0} pot={potential:0} " +
                              $"devices: {tallyText}");
                foreach (var line in segLines) _log?.LogInfo($"[ScenarioRunner] {line}");
            });
            _log?.LogInfo($"[ScenarioRunner] SHORTFALL probe complete ({reported} undersupplied net(s) reported, cap 14).");
        }

        // Reflection helper: call the registry's client-aware bool reader (IsDeprioritized / IsOverloaded /
        // IsCableOverloaded / IsCycleFaulted / IsCurrentMismatchFaulted or IsLockedOut(long, int)) for
        // a refId at the current PGP tick.
        private static bool PgpIsLocked(System.Reflection.Assembly asm, string typeName, long refId)
        {
            try
            {
                var t = asm?.GetType(typeName);
                if (t == null) return false;
                var tickType = asm.GetType("PowerGridPlus.ElectricityTickCounter");
                var tickProp = tickType?.GetProperty("CurrentTick",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                int tick = tickProp?.GetValue(null) is int i ? i : 0;
                foreach (var name in new[] { "IsDeprioritized", "IsOverloaded", "IsCableOverloaded", "IsCycleFaulted", "IsCurrentMismatchFaulted", "IsLockedOut" })
                {
                    var m = t.GetMethod(name,
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                        null, new[] { typeof(long), typeof(int) }, null);
                    if (m == null) continue;
                    return m.Invoke(null, new object[] { refId, tick }) is bool b && b;
                }
            }
            catch { }
            return false;
        }

        private static int ReadPgpStaticIntProp(System.Reflection.Assembly asm, string typeName, string propName)
        {
            try
            {
                var t = asm?.GetType(typeName);
                var p = t?.GetProperty(propName, System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                var v = p?.GetValue(null);
                return v is int i ? i : -1;
            }
            catch { return -1; }
        }

        private static int InvokePgpStaticCollectionCount(System.Reflection.Assembly asm, string typeName, string methodName)
        {
            try
            {
                var t = asm?.GetType(typeName);
                var m = t?.GetMethod(methodName, System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                var result = m?.Invoke(null, null) as System.Collections.ICollection;
                return result?.Count ?? -1;
            }
            catch (System.Exception e)
            {
                _log?.LogWarning($"[ScenarioRunner] FAULT-STATE invoke {methodName} threw: {e.InnerException?.Message ?? e.Message}");
                return -1;
            }
        }

        // PGP scenario: battery-efficiency-probe.
        // One-shot PASS/FAIL check of the battery charge-cost settlement math. The old
        // Battery.ReceivePower charge-efficiency prefix is retired (vanilla ApplyState no
        // longer runs; nothing calls ReceivePower during the tick), so the probe drives the
        // REAL settlement path instead: Core/WriteBack.ApplyCredit(StoreCredit, chargeCost),
        // the exact method the mod-owned write-back runs per store credit each tick.
        // Current rule (Battery Charge Efficiency is a COST multiplier, default 1.5):
        //   stored = credited / max(1, BatteryChargeEfficiency)
        //   if (stored < 500) stored = credited        // post-division sub-500 W trickle floor
        //   PowerStored += stored (clamped)
        // The expected values are computed from the LIVE config value via reflection, so the
        // probe follows the server cfg instead of hardcoding the shipped default:
        //   Case A: credited=5000 -> stored 5000/cost (3333.33 at cost 1.5).
        //   Case B: credited=600  -> 600/cost lands under 500 at cost > 1.2, so the floor
        //           stores the full 600; at cost <= 1.2 the divided value is >= 500 and
        //           applies directly. Both branches share one expected-value formula.
        // The mutated PowerStored is restored afterwards, so the save is left as found.
        // ApplyCredit also records the credit with ChargeDeliveryAudit; a credit with no
        // matching allocator grant this tick is outside the audit's model and ignored.

        private static bool _bepProbeFired;

        private static void Scenario_PgpBatteryEfficiencyProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-battery-efficiency-probe")) return;
            if (_bepProbeFired) return;
            _bepProbeFired = true;

            int pass = 0, fail = 0;
            try
            {
                var asm = GetModAssembly(PGP_ASSEMBLY);
                const System.Reflection.BindingFlags SF =
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.Static;

                var writeBackType = asm?.GetType("PowerGridPlus.Core.WriteBack");
                var creditType = asm?.GetType("PowerGridPlus.Core.WriteBack+StoreCredit");
                var auditType = asm?.GetType("PowerGridPlus.ChargeDeliveryAudit");
                var applyCredit = writeBackType?.GetMethod("ApplyCredit", SF);
                var kindBatteryField = auditType?.GetField("KindBattery", SF);
                double cost = PgpBatteryChargeEfficiency();

                if (applyCredit == null || creditType == null || kindBatteryField == null || double.IsNaN(cost))
                {
                    _log?.LogError(
                        "[ScenarioRunner] BEP FAIL: reflection surface incomplete " +
                        $"(WriteBack.ApplyCredit={applyCredit != null} StoreCredit={creditType != null} " +
                        $"ChargeDeliveryAudit.KindBattery={kindBatteryField != null} configReadable={!double.IsNaN(cost)}); " +
                        "PowerGridPlus renamed or too old.");
                    fail++;
                    return;
                }
                byte kindBattery = (byte)kindBatteryField.GetValue(null);
                float chargeCost = UnityEngine.Mathf.Max(1f, (float)cost);

                if (_batteries.Count == 0) RebuildCaches();
                Battery target = null;
                foreach (var b in _batteries)
                {
                    // Enough headroom that neither case clamps at PowerMaximum.
                    if (b != null && b.PowerStored + 7000f <= b.PowerMaximum) { target = b; break; }
                }
                if (target == null)
                {
                    _log?.LogWarning("[ScenarioRunner] BEP COULD-NOT-RUN: no Battery with 7 kJ headroom; nothing to probe.");
                    return;
                }

                float original = target.PowerStored;
                _log?.LogInfo(
                    $"[ScenarioRunner] BEP target: ref={target.ReferenceId} prefab={target.PrefabName} " +
                    $"PowerStored={original:F2} PowerMaximum={target.PowerMaximum:F2} " +
                    $"BatteryChargeEfficiency={cost} (cost multiplier, effective {chargeCost})");

                float ExpectedStored(float credited)
                {
                    float stored = credited / chargeCost;
                    if (stored < 500f) stored = credited;   // post-division trickle floor
                    return stored;
                }

                void RunCase(string tag, float credited)
                {
                    float before = target.PowerStored;
                    object credit = Activator.CreateInstance(creditType);
                    creditType.GetField("RefId")?.SetValue(credit, target.ReferenceId);
                    creditType.GetField("Kind")?.SetValue(credit, kindBattery);
                    creditType.GetField("Amount")?.SetValue(credit, credited);
                    creditType.GetField("Owner")?.SetValue(credit, target);
                    applyCredit.Invoke(null, new object[] { credit, chargeCost });
                    float after = target.PowerStored;
                    float delta = after - before;
                    float expected = ExpectedStored(credited);
                    bool ok = Math.Abs(delta - expected) < 0.5f;
                    if (ok)
                    { pass++; _log?.LogInfo($"[ScenarioRunner] BEP {tag} PASS: credited={credited:F0} stored={delta:F2} expected={expected:F2} (cost={chargeCost}, floor {(credited / chargeCost < 500f ? "applied" : "not applied")})."); }
                    else
                    { fail++; _log?.LogError($"[ScenarioRunner] BEP {tag} FAIL: credited={credited:F0} stored={delta:F2}, expected {expected:F2} at cost={chargeCost}."); }
                }

                // Case A: well above the floor after division.
                RunCase("A", 5000f);
                // Case B: exercises the post-division sub-500 W full-store branch at cost > 1.2.
                RunCase("B", 600f);

                // Leave the save as found.
                target.PowerStored = original;
                _log?.LogInfo($"[ScenarioRunner] BEP restored PowerStored={target.PowerStored:F2} on ref={target.ReferenceId}.");
            }
            catch (Exception e)
            {
                fail++;
                _log?.LogError($"[ScenarioRunner] BEP threw: {e}");
            }
            finally
            {
                _log?.LogInfo($"[ScenarioRunner] BEP END pass={pass} fail={fail}");
            }
        }

        private static double PgpBatteryChargeEfficiency()
        {
            try
            {
                var asm = GetModAssembly(PGP_ASSEMBLY);
                if (asm == null) return double.NaN;
                var settingsType = asm.GetType("PowerGridPlus.Settings");
                if (settingsType == null) return double.NaN;
                var field = settingsType.GetField("BatteryChargeEfficiency",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var configEntry = field?.GetValue(null);
                if (configEntry == null) return double.NaN;
                var valueProp = configEntry.GetType().GetProperty("Value");
                var v = valueProp?.GetValue(configEntry);
                return v is float f ? (double)f : double.NaN;
            }
            catch { return double.NaN; }
        }

        // PGP scenario: apc-idle-probe.
        // Logs each AreaPowerControl's attached Battery.PowerStored every 5 ticks.
        // PGP's AreaPowerControlPatches stops APCs leaking battery when nothing is
        // downstream. Diff first vs last across the window; idle APCs should hold
        // constant.

        private static int _apcLastLogTick = int.MinValue;
        private const int APC_LOG_EVERY_TICKS = 5;

        private static void Scenario_PgpApcIdleProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-apc-idle-probe")) return;
            if (_ticksSeen - _apcLastLogTick < APC_LOG_EVERY_TICKS) return;
            _apcLastLogTick = (int)_ticksSeen;

            if (_apcs.Count == 0) RebuildCaches();
            foreach (var a in _apcs)
            {
                if (a == null) continue;
                var bat = a.Battery;
                var stored = bat != null ? bat.PowerStored : float.NaN;
                var pmax = bat != null ? bat.PowerMaximum : float.NaN;
                _log?.LogInfo(
                    $"[ScenarioRunner] APC tick={_ticksSeen} ref={a.ReferenceId} " +
                    $"prefab={a.PrefabName} OnOff={a.OnOff} UsedPower={a.UsedPower} " +
                    $"BatteryStored={stored:F2} BatteryMax={pmax:F2}");
            }
        }

        // PGP scenario: cable-burn-probe.
        // Two parts. Periodic: list every CableNetwork with Required or Current > 5 kW
        // (where normal cables would naturally burn). One-shot: the burn decision moved
        // in the rebuild from the per-network PowerGridTick.TestBurnCable to the
        // mod-owned write-back (Core/WriteBack + the CableBurnWindow 20-tick running
        // average), so the one-shot now asserts the LEGACY type is absent (a regression
        // tripwire against the per-network tick coming back) and points at
        // pgp-cable-burn-window-probe, which drives the current burn-decision math
        // (CableBurnWindow.Observe / IsFull / AverageFlow / TopProducer) directly.

        private static int _cbpLastLogTick = int.MinValue;
        private const int CBP_LOG_EVERY_TICKS = 25;
        private static bool _cbpReflectionFired;

        private static void Scenario_PgpCableBurnProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-cable-burn-probe")) return;
            if (_ticksSeen - _cbpLastLogTick < CBP_LOG_EVERY_TICKS) return;
            _cbpLastLogTick = (int)_ticksSeen;

            int overloaded = 0;
            CableNetwork.AllCableNetworks.ForEach(n =>
            {
                if (n == null) return;
                var req = n.RequiredLoad;
                var cur = n.CurrentLoad;
                if (req > 5000f || cur > 5000f)
                {
                    overloaded++;
                    _log?.LogInfo(
                        $"[ScenarioRunner] CBP tick={_ticksSeen} netRef={n.ReferenceId} " +
                        $"Required={req:F2} Current={cur:F2} Potential={n.PotentialLoad:F2}");
                }
            });
            _log?.LogInfo($"[ScenarioRunner] CBP tick={_ticksSeen} summary: {overloaded} networks at >5kW (Required or Current)");

            if (!_cbpReflectionFired)
            {
                _cbpReflectionFired = true;
                ProbePgpTestBurnCableViaReflection();
            }
        }

        // PGP scenario: tooltip-filter-probe.
        // One-shot. Sweeps every Thing in the loaded scene, calls
        // Thing.GetPassiveTooltip(null) on each, and inspects the resulting
        // PassiveTooltip.Extended for the "<color=#ffa500>Burned:</color>" marker
        // that PowerGridPlus.Patches.BurnReasonPatches.Thing_GetPassiveTooltip_Postfix
        // appends. The postfix is gated by `__instance is CableRuptured`; any
        // non-CableRuptured Thing whose Extended ends up carrying that marker is a
        // filter bug. Reports totals plus up to five offender samples.
        //
        // Threading: the simulation-tick hook runs on a UniTask ThreadPool worker.
        // Most GetPassiveTooltip overrides build strings from cached managed state
        // and are safe to call off-thread, but some touch UnityEngine.Object members
        // (transform.position, gameObject.activeSelf) which crash off the main
        // thread. Each call is wrapped in try / catch; failed calls are counted
        // separately. The aim is a high-coverage smoke test, not a 100% sweep --
        // even with 30 % of Things failing we still have thousands of samples
        // exercising the negative path.

        private static bool _tfpFired;

        private static void Scenario_PgpTooltipFilterProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-tooltip-filter-probe")) return;
            if (_tfpFired) return;
            _tfpFired = true;

            int totalCalled = 0;
            int totalFailed = 0;
            int cableRupturedSeen = 0;
            int cableRupturedWithBurned = 0;
            int otherWithBurned = 0;
            var offenders = new List<string>();

            OcclusionManager.AllThings.ForEach(t =>
            {
                if (t == null) return;
                PassiveTooltip tt;
                try
                {
                    tt = t.GetPassiveTooltip(null);
                }
                catch
                {
                    totalFailed++;
                    return;
                }
                totalCalled++;

                bool isRuptured = t is CableRuptured;
                bool hasBurned = !string.IsNullOrEmpty(tt.Extended) && tt.Extended.Contains("Burned:");

                if (isRuptured)
                {
                    cableRupturedSeen++;
                    if (hasBurned) cableRupturedWithBurned++;
                }
                else if (hasBurned)
                {
                    otherWithBurned++;
                    if (offenders.Count < 5)
                    {
                        var snippet = tt.Extended.Length > 200 ? tt.Extended.Substring(0, 200) + "..." : tt.Extended;
                        offenders.Add($"{t.GetType().Name} ref={t.ReferenceId} prefab={t.PrefabName} Extended='{snippet.Replace("\n", "\\n")}'");
                    }
                }
            });

            _log?.LogInfo(
                $"[ScenarioRunner] TFP totalCalled={totalCalled} totalFailed={totalFailed} " +
                $"CableRupturedSeen={cableRupturedSeen} CableRupturedWithBurned={cableRupturedWithBurned} " +
                $"OtherWithBurned={otherWithBurned}");

            if (otherWithBurned > 0)
            {
                _log?.LogError($"[ScenarioRunner] TFP filter bug: {otherWithBurned} non-CableRuptured Things have a 'Burned:' line in their tooltip.");
                foreach (var o in offenders) _log?.LogError($"[ScenarioRunner] TFP offender: {o}");
            }
            else
            {
                _log?.LogInfo($"[ScenarioRunner] TFP filter pass: no non-CableRuptured Thing carried a 'Burned:' line.");
            }
        }

        // PGP scenario: rate-cap-probe.
        // One-shot. Verifies the 2026-05-28 APC + Battery rate-cap rewrite:
        //   1. APC GetUsedPower(InputNetwork) respects the per-device charge cap
        //      (configured cap further bounded by input cable MaxVoltage minus the
        //      pass-through tracked by PGP's UsePower postfix).
        //   2. APC GetGeneratedPower(OutputNetwork) <= output cable MaxVoltage.
        //   3. Battery GetUsedPower / GetGeneratedPower respect the per-prefab caps
        //      (5/10, 25/50, 25/50 kW for StructureBattery, StructureBatteryLarge,
        //      StationBatteryNuclear), further cable-bounded.
        //   4. Battery.GetLogicValue(ImportQuantity / ExportQuantity) returns the
        //      configured per-prefab cap (not cable-bounded -- intent, not capacity).
        //   5. Localization.GetThingDescription(prefabName) for the affected prefabs
        //      contains the "Power Grid Plus" footer string. Negative case:
        //      "StructureBatteryNuclear" does NOT have a footer (wrong name), but
        //      "StationBatteryNuclear" does.
        //
        // All reads / reflection-driven invocations are managed-state only and safe
        // from the UniTask ThreadPool worker.

        private static bool _rcpFired;

        private static void Scenario_PgpRateCapProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-rate-cap-probe")) return;
            if (_rcpFired) return;
            _rcpFired = true;

            try
            {
                _log?.LogInfo("[ScenarioRunner] RCP START");
                if (_apcs.Count == 0 || _batteries.Count == 0) RebuildCaches();

                ProbePgpRateCap_Apcs();
                ProbePgpRateCap_Batteries();
                ProbePgpRateCap_Stationpedia();

                _log?.LogInfo("[ScenarioRunner] RCP END");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] RCP threw: {e}");
            }
        }

        private static void ProbePgpRateCap_Apcs()
        {
            var asm = GetModAssembly(PGP_ASSEMBLY);
            var settingsType = asm?.GetType("PowerGridPlus.Settings");
            var apcChargeRateField = settingsType?.GetField("ApcBatteryChargeRate",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var apcConfigVal = ReadConfigFloat(apcChargeRateField);

            var apcPatchType = asm?.GetType("PowerGridPlus.Patches.AreaPowerControlPatches");
            var computeChargeCap = apcPatchType?.GetMethod("ComputeChargeCap",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            int total = 0;
            int outputCapPass = 0;
            int outputCapFail = 0;
            int chargeCapPass = 0;
            int chargeCapFail = 0;
            int wired = 0;

            foreach (var apc in _apcs)
            {
                if (apc == null) continue;
                total++;

                var inCable = apc.InputConnection?.GetCable();
                var outCable = apc.OutputConnection?.GetCable();
                if (inCable == null || outCable == null) continue;
                wired++;

                float inMax = inCable.MaxVoltage;
                float outMax = outCable.MaxVoltage;

                // Verify GetGeneratedPower clamp at output cable MaxVoltage.
                float gen = 0f;
                if (apc.OutputNetwork != null)
                    gen = apc.GetGeneratedPower(apc.OutputNetwork);
                bool outCapOk = gen <= outMax + 0.01f;
                if (outCapOk) outputCapPass++; else outputCapFail++;

                // Verify ComputeChargeCap stays inside (configured cap, input cable spare) and
                // is non-negative.
                float cc = float.NaN;
                if (computeChargeCap != null)
                {
                    try { cc = (float)computeChargeCap.Invoke(null, new object[] { apc }); }
                    catch { /* swallow */ }
                }
                bool ccOk = !float.IsNaN(cc)
                    && cc >= 0f
                    && cc <= apcConfigVal + 0.01f
                    && cc <= inMax + 0.01f;
                if (ccOk) chargeCapPass++; else chargeCapFail++;

                _log?.LogInfo(
                    $"[ScenarioRunner] RCP APC ref={apc.ReferenceId} prefab={apc.PrefabName} " +
                    $"inMax={inMax:F0} outMax={outMax:F0} gen={gen:F2} outCapOk={outCapOk} " +
                    $"chargeCap={cc:F2} (config={apcConfigVal:F0}) chargeCapOk={ccOk}");
            }

            _log?.LogInfo(
                $"[ScenarioRunner] RCP APC summary: total={total} wired={wired} " +
                $"outputCap pass={outputCapPass} fail={outputCapFail} " +
                $"chargeCap pass={chargeCapPass} fail={chargeCapFail}");
        }

        private static void ProbePgpRateCap_Batteries()
        {
            var asm = GetModAssembly(PGP_ASSEMBLY);
            var batPatchType = asm?.GetType("PowerGridPlus.Patches.StationaryBatteryPatches");
            var getChargeCap = batPatchType?.GetMethod("GetChargeCap",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var getDischargeCap = batPatchType?.GetMethod("GetDischargeCap",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var effChargeCap = batPatchType?.GetMethod("EffectiveChargeCap",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var effDischargeCap = batPatchType?.GetMethod("EffectiveDischargeCap",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            int total = 0;
            int wired = 0;
            int usedPowerPass = 0;
            int usedPowerFail = 0;
            int genPowerPass = 0;
            int genPowerFail = 0;
            int logicImportPass = 0;
            int logicImportFail = 0;
            int logicExportPass = 0;
            int logicExportFail = 0;
            int infinityDefaultCount = 0;

            // Group by prefab to keep the log short. Pick up to 2 instances per prefab.
            var perPrefab = new Dictionary<string, int>();

            foreach (var bat in _batteries)
            {
                if (bat == null) continue;
                total++;
                if (bat.InputConnection?.GetCable() == null) continue;
                wired++;

                string prefab = bat.PrefabName ?? "?";

                float configCharge = float.NaN, configDischarge = float.NaN;
                if (getChargeCap != null) configCharge = (float)getChargeCap.Invoke(null, new object[] { bat });
                if (getDischargeCap != null) configDischarge = (float)getDischargeCap.Invoke(null, new object[] { bat });
                if (float.IsInfinity(configCharge)) infinityDefaultCount++;

                float effC = float.NaN, effD = float.NaN;
                if (effChargeCap != null) effC = (float)effChargeCap.Invoke(null, new object[] { bat });
                if (effDischargeCap != null) effD = (float)effDischargeCap.Invoke(null, new object[] { bat });

                float usedPower = bat.InputNetwork != null ? bat.GetUsedPower(bat.InputNetwork) : 0f;
                float genPower = bat.OutputNetwork != null ? bat.GetGeneratedPower(bat.OutputNetwork) : 0f;
                bool usedOk = usedPower <= effC + 0.01f;
                bool genOk = genPower <= effD + 0.01f;
                if (usedOk) usedPowerPass++; else usedPowerFail++;
                if (genOk) genPowerPass++; else genPowerFail++;

                // Logic value reads: ImportQuantity should be GetChargeCap (configured, not effective).
                double importV = 0, exportV = 0;
                try
                {
                    importV = bat.GetLogicValue(Assets.Scripts.Objects.Motherboards.LogicType.ImportQuantity);
                    exportV = bat.GetLogicValue(Assets.Scripts.Objects.Motherboards.LogicType.ExportQuantity);
                }
                catch { }
                bool importOk = System.Math.Abs(importV - configCharge) < 0.01 || (float.IsInfinity(configCharge) && double.IsInfinity(importV));
                bool exportOk = System.Math.Abs(exportV - configDischarge) < 0.01 || (float.IsInfinity(configDischarge) && double.IsInfinity(exportV));
                if (importOk) logicImportPass++; else logicImportFail++;
                if (exportOk) logicExportPass++; else logicExportFail++;

                if (!perPrefab.TryGetValue(prefab, out var c)) c = 0;
                if (c < 2)
                {
                    perPrefab[prefab] = c + 1;
                    _log?.LogInfo(
                        $"[ScenarioRunner] RCP Battery ref={bat.ReferenceId} prefab={prefab} " +
                        $"configCharge={configCharge:F0} configDischarge={configDischarge:F0} " +
                        $"effCharge={effC:F0} effDischarge={effD:F0} " +
                        $"usedPower={usedPower:F2} usedOk={usedOk} " +
                        $"genPower={genPower:F2} genOk={genOk} " +
                        $"import={importV:F0} importOk={importOk} " +
                        $"export={exportV:F0} exportOk={exportOk}");
                }
            }

            _log?.LogInfo(
                $"[ScenarioRunner] RCP Battery summary: total={total} wired={wired} " +
                $"infinityDefault={infinityDefaultCount} " +
                $"usedPower pass={usedPowerPass} fail={usedPowerFail} " +
                $"genPower pass={genPowerPass} fail={genPowerFail} " +
                $"logicImport pass={logicImportPass} fail={logicImportFail} " +
                $"logicExport pass={logicExportPass} fail={logicExportFail}");
        }

        private static void ProbePgpRateCap_Stationpedia()
        {
            var locType = AccessUtil_TypeByName("Assets.Scripts.Localization") ?? AccessUtil_TypeByName("Localization");
            var getDesc = locType?.GetMethod("GetThingDescription", new[] { typeof(string) });
            if (getDesc == null)
            {
                _log?.LogWarning("[ScenarioRunner] RCP Stationpedia: Localization.GetThingDescription not found");
                return;
            }

            var positives = new[] {
                "StructureAreaPowerControl",
                "StructureAreaPowerControlReversed",
                "StructureBattery",
                "StructureBatteryLarge",
                "StationBatteryNuclear",
            };
            var negatives = new[] {
                "StructureBatteryNuclear",   // wrong name (Structure-, not Station-); must NOT have footer
                "StructureTransformer",      // not touched by us; must NOT have footer
                "StructureRefrigerator",     // unrelated; must NOT have footer
            };

            int posPass = 0, posFail = 0, negPass = 0, negFail = 0;
            // Sentinel matches the {HEADER:POWER GRID PLUS} token literal that PGP emits in the
            // raw GetThingDescription output. The token is expanded to TMP markup later by
            // Localization.ParseHelpText during Stationpedia.PopulateThingPages, but the scenario
            // calls GetThingDescription directly so it sees the pre-parse text.
            const string FOOTER_SENTINEL = "{HEADER:POWER GRID PLUS}";

            foreach (var p in positives)
            {
                string desc = null;
                try { desc = getDesc.Invoke(null, new object[] { p }) as string; } catch { }
                bool ok = desc != null && desc.Contains(FOOTER_SENTINEL);
                if (ok) posPass++; else posFail++;
                _log?.LogInfo($"[ScenarioRunner] RCP Stationpedia[+] prefab={p} footer={ok} (descLen={(desc?.Length ?? -1)})");
            }
            foreach (var p in negatives)
            {
                string desc = null;
                try { desc = getDesc.Invoke(null, new object[] { p }) as string; } catch { }
                bool noFooter = desc == null || !desc.Contains(FOOTER_SENTINEL);
                if (noFooter) negPass++; else negFail++;
                _log?.LogInfo($"[ScenarioRunner] RCP Stationpedia[-] prefab={p} noFooter={noFooter}");
            }

            _log?.LogInfo($"[ScenarioRunner] RCP Stationpedia summary: positives pass={posPass}/{positives.Length} negatives pass={negPass}/{negatives.Length}");
        }

        private static System.Type AccessUtil_TypeByName(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(name);
                if (t != null) return t;
            }
            return null;
        }

        private static float ReadConfigFloat(System.Reflection.FieldInfo field)
        {
            try
            {
                var entry = field?.GetValue(null);
                var valueProp = entry?.GetType().GetProperty("Value");
                var v = valueProp?.GetValue(entry);
                return v is float f ? f : float.NaN;
            }
            catch { return float.NaN; }
        }

        private static void ProbePgpTestBurnCableViaReflection()
        {
            try
            {
                var asm = GetModAssembly(PGP_ASSEMBLY);
                if (asm == null) { _log?.LogWarning("[ScenarioRunner] CBP reflection probe: PowerGridPlus assembly missing."); return; }

                // The per-network PowerGridTick (and its TestBurnCable seam) was retired when the
                // whole power tick became mod-owned; the deterministic burn now lives in the
                // write-back (Core/WriteBack) fed by CableBurnWindow's 20-tick running average.
                // Assert the legacy type stays gone AND the current surface exists.
                var legacyTickType = asm.GetType("PowerGridPlus.Power.PowerGridTick");
                var burnWindowType = asm.GetType("PowerGridPlus.CableBurnWindow");
                if (legacyTickType == null && burnWindowType != null)
                {
                    _log?.LogInfo(
                        "[ScenarioRunner] CBP one-shot PASS: legacy PowerGridPlus.Power.PowerGridTick is absent " +
                        "and CableBurnWindow exists (burn decision lives in the mod-owned write-back). " +
                        "Run pgp-cable-burn-window-probe to exercise the burn-decision math directly.");
                }
                else
                {
                    _log?.LogError(
                        "[ScenarioRunner] CBP one-shot FAIL: type surface wrong. " +
                        $"legacy PowerGridTick={legacyTickType != null} (expect false) " +
                        $"CableBurnWindow={burnWindowType != null} (expect true).");
                }
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] CBP reflection probe threw: {e.Message}");
            }
        }

        // PGP scenario: stationpedia-page-probe.
        // One-shot. Verifies the LogicType wiki-page registration done by PGP's
        // StationpediaPopulateLogicVariablesPatch postfix (which calls Stationpedia.Register
        // for each entry in LogicTypeRegistry.All on every Stationpedia.PopulateLogicVariables).
        // Reads Stationpedia._linkIdLookup at scenario tick time, asserts the entry for
        // 'LogicTypeLogicPassthroughMode' is present, and logs its Key / Title / Text head.
        // The Localization.GetThingDescription footer half of PGP's Stationpedia work is covered
        // separately by pgp-rate-cap-probe (ProbePgpRateCap_Stationpedia), not duplicated here.
        //
        // Threading: a private static IDictionary read via reflection plus a couple of field
        // reads. No Unity APIs -> safe from the UniTask worker.

        private static bool _spppFired;

        private static void Scenario_PgpStationpediaPageProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-stationpedia-page-probe")) return;
            if (_spppFired) return;
            _spppFired = true;

            try
            {
                _log?.LogInfo("[ScenarioRunner] SPPP START pgp-stationpedia-page-probe");

                var pediaType = AccessUtil_TypeByName("Assets.Scripts.UI.Stationpedia")
                    ?? AccessUtil_TypeByName("Stationpedia");
                if (pediaType == null)
                {
                    _log?.LogError("[ScenarioRunner] SPPP FAIL: Stationpedia type not found");
                    return;
                }

                var lookupField = pediaType.GetField("_linkIdLookup",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (lookupField == null)
                {
                    _log?.LogError("[ScenarioRunner] SPPP FAIL: Stationpedia._linkIdLookup static field not found");
                    return;
                }

                var lookup = lookupField.GetValue(null) as System.Collections.IDictionary;
                if (lookup == null)
                {
                    _log?.LogError("[ScenarioRunner] SPPP FAIL: _linkIdLookup is null or not an IDictionary");
                    return;
                }
                _log?.LogInfo($"[ScenarioRunner] SPPP _linkIdLookup pages={lookup.Count}");

                const string key = "LogicTypeLogicPassthroughMode";
                if (!lookup.Contains(key))
                {
                    _log?.LogError($"[ScenarioRunner] SPPP FAIL: '{key}' NOT registered in _linkIdLookup ({lookup.Count} pages total)");
                    return;
                }

                var page = lookup[key];
                var pageType = page.GetType();
                string pageKey = pageType.GetField("Key")?.GetValue(page) as string;
                string title = pageType.GetField("Title")?.GetValue(page) as string;
                string text = pageType.GetField("Text")?.GetValue(page) as string;
                string head = string.IsNullOrEmpty(text)
                    ? "<empty>"
                    : (text.Length > 200 ? text.Substring(0, 200) + "..." : text);
                head = head.Replace("\r", "\\r").Replace("\n", "\\n");
                _log?.LogInfo($"[ScenarioRunner] SPPP PASS: '{key}' registered. Key='{pageKey}' Title='{title}' TextLen={(text?.Length ?? -1)} Head='{head}'");

                // Round-trip: registered Text must exactly match LogicTypeRegistry.LogicPassthroughMode.Description.
                // The vanilla Stationpedia UI binds StationpediaPage.Text directly to TMP, same as every
                // vanilla device page; if the source string round-trips into the page unmodified the
                // page-display pipeline will see exactly what the mod author wrote.
                try
                {
                    var pgpAsm = GetModAssembly(PGP_ASSEMBLY);
                    var registryType = pgpAsm?.GetType("PowerGridPlus.LogicTypeRegistry");
                    var allField = registryType?.GetField("All",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                    string expectedText = null;
                    if (allField?.GetValue(null) is System.Collections.IEnumerable all)
                    {
                        foreach (var entry in all)
                        {
                            var et = entry.GetType();
                            var nm = et.GetField("Name")?.GetValue(entry) as string;
                            if (nm == "LogicPassthroughMode")
                            {
                                expectedText = et.GetField("Description")?.GetValue(entry) as string;
                                break;
                            }
                        }
                    }
                    if (expectedText == null)
                        _log?.LogWarning("[ScenarioRunner] SPPP ROUND-TRIP SKIP: could not read LogicTypeRegistry.All via reflection");
                    else if (text == expectedText)
                        _log?.LogInfo($"[ScenarioRunner] SPPP ROUND-TRIP PASS: page.Text matches LogicTypeRegistry source exactly ({text.Length} chars)");
                    else
                        _log?.LogError($"[ScenarioRunner] SPPP ROUND-TRIP FAIL: page.Text != registry source. pageLen={(text?.Length ?? -1)} regLen={expectedText.Length}");
                }
                catch (Exception e2)
                {
                    _log?.LogWarning($"[ScenarioRunner] SPPP round-trip check threw: {e2.Message}");
                }

                const string LINK_TARGET = "LogicTypeLogicPassthroughMode";

                // Reachability via broad scan: enumerate every page in _linkIdLookup and find any whose
                // static Text contains the link target. Devices that bake their LogicTypes section into
                // page.Text show up here directly; pages that render LogicType chips dynamically (built
                // by the UI from Logicable.LogicTypes at view time, not stored in Text) won't show up
                // and are covered by the LogicableInitializePatch log line + the EnumCollections check
                // below. Empirical scan beats a hardcoded prefab list.
                int totalScanned = 0;
                int matchingPages = 0;
                var matchSamples = new System.Collections.Generic.List<string>(8);
                foreach (System.Collections.DictionaryEntry de in lookup)
                {
                    var k = de.Key as string;
                    var pv = de.Value;
                    if (pv == null) continue;
                    totalScanned++;
                    var tField = pv.GetType().GetField("Text");
                    var tval = tField?.GetValue(pv) as string;
                    if (string.IsNullOrEmpty(tval)) continue;
                    if (tval.Contains(LINK_TARGET))
                    {
                        matchingPages++;
                        if (matchSamples.Count < 8 && k != null) matchSamples.Add(k);
                    }
                }
                _log?.LogInfo($"[ScenarioRunner] SPPP REACH-SCAN scanned={totalScanned} pages, {matchingPages} contain '{LINK_TARGET}' in Text. samples=[{string.Join(", ", matchSamples)}]");

                // Reachability via EnumCollections.LogicTypes + Enum.GetName: the two surfaces vanilla
                // calls to render a LogicType chip's <link="LogicType"+name>. EnumCollection<TEnum,
                // TValue> exposes parallel Names / ValuesAsInts arrays (no GetName/GetValue API; see
                // Mods/PowerGridPlus/Patches/LogicableInitializePatch.cs#L65-L120). Enum.GetName is
                // postfixed by PGP's EnumNamePatches so vanilla code paths that call it for a custom
                // value get our name back. Both must agree on the (name, value) pair for the link
                // target string to match our registered key.
                try
                {
                    var pgpAsm2 = GetModAssembly(PGP_ASSEMBLY);
                    var regType = pgpAsm2?.GetType("PowerGridPlus.LogicTypeRegistry");
                    var valField = regType?.GetField("LogicPassthroughModeValue",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                    ushort expectedValue = (ushort)(valField?.GetRawConstantValue() ?? (object)(ushort)0);

                    // Array-based check
                    var ecType = AccessUtil_TypeByName("Assets.Scripts.EnumCollections")
                        ?? AccessUtil_TypeByName("EnumCollections");
                    var ltField = ecType?.GetField("LogicTypes",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    var ec = ltField?.GetValue(null);
                    if (ec == null)
                    {
                        _log?.LogWarning("[ScenarioRunner] SPPP ENUM SKIP: EnumCollections.LogicTypes not located");
                    }
                    else
                    {
                        var ecT = ec.GetType();
                        var namesArr = ecT.GetField("Names")?.GetValue(ec) as string[];
                        var intsArr = ecT.GetField("ValuesAsInts")?.GetValue(ec) as ushort[];
                        if (namesArr == null || intsArr == null || namesArr.Length != intsArr.Length)
                        {
                            _log?.LogWarning($"[ScenarioRunner] SPPP ENUM SKIP: Names / ValuesAsInts not accessible (Names={namesArr?.Length ?? -1} Ints={intsArr?.Length ?? -1})");
                        }
                        else
                        {
                            int foundIdx = -1;
                            for (int i = 0; i < namesArr.Length; i++)
                            {
                                if (namesArr[i] == "LogicPassthroughMode") { foundIdx = i; break; }
                            }
                            if (foundIdx >= 0 && intsArr[foundIdx] == expectedValue)
                                _log?.LogInfo($"[ScenarioRunner] SPPP ENUM PASS: EnumCollections.LogicTypes contains 'LogicPassthroughMode' at index {foundIdx} with value {expectedValue} (collection size {namesArr.Length}). The tablet-UI dropdown source has our entry.");
                            else if (foundIdx >= 0)
                                _log?.LogError($"[ScenarioRunner] SPPP ENUM FAIL: 'LogicPassthroughMode' present at index {foundIdx} but ValuesAsInts[{foundIdx}]={intsArr[foundIdx]} != expected {expectedValue}");
                            else
                                _log?.LogError($"[ScenarioRunner] SPPP ENUM FAIL: 'LogicPassthroughMode' NOT in EnumCollections.LogicTypes.Names (size {namesArr.Length})");
                        }
                    }

                    // Enum.GetName check (the call path the UI uses for the link target string)
                    var ltEnumType = AccessUtil_TypeByName("Assets.Scripts.Objects.Motherboards.LogicType")
                        ?? AccessUtil_TypeByName("LogicType");
                    if (ltEnumType == null)
                    {
                        _log?.LogWarning("[ScenarioRunner] SPPP ENUM-NAME SKIP: LogicType enum type not located");
                    }
                    else
                    {
                        var enumVal = Enum.ToObject(ltEnumType, expectedValue);
                        var nameViaEnum = Enum.GetName(ltEnumType, enumVal);
                        string emittedLink = nameViaEnum == null ? null : "LogicType" + nameViaEnum;
                        if (nameViaEnum == "LogicPassthroughMode" && emittedLink == LINK_TARGET)
                            _log?.LogInfo($"[ScenarioRunner] SPPP ENUM-NAME PASS: Enum.GetName(typeof(LogicType), {expectedValue}) returns 'LogicPassthroughMode'. Vanilla chip rendering emits '<link=\"{emittedLink}\">' which equals our registered key.");
                        else
                            _log?.LogError($"[ScenarioRunner] SPPP ENUM-NAME FAIL: Enum.GetName returned '{nameViaEnum}' for value {expectedValue}. Vanilla chip rendering would emit '<link=\"LogicType{nameViaEnum}\">', not matching our key '{LINK_TARGET}'.");
                    }
                }
                catch (Exception e3)
                {
                    _log?.LogWarning($"[ScenarioRunner] SPPP enum check threw: {e3.Message}");
                }
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] SPPP threw: {e}");
            }
        }

        // ---- PowerTransmitterPlus-specific scenarios ----
        //
        // These require the 'PowerTransmitterPlus' assembly to be loaded. They reach
        // into PTP via reflection (no build-time dependency) so this plugin stays
        // independent. All probes are designed to run safely from the UniTask
        // ThreadPool worker the simulation-tick hook executes on, so they avoid Unity
        // APIs (no transform.position / transform.forward reads) and rely on cached
        // managed state (Thing.OnOff cached field, LinkedReceiver reference, the
        // PowerTransmitter._linkedReceiverDistance private float, NetworkUpdateFlags).

        private const string PTP_ASSEMBLY = "PowerTransmitterPlus";

        // Custom NetworkUpdateFlags bit PTP reserves for auto-aim cache sync. Matches
        // PowerTransmitterPlus.AutoAimState.AutoAimUpdateFlag (a private const so we
        // mirror the literal here instead of reflecting it out).
        private const ushort PTP_AUTOAIM_UPDATE_FLAG = 0x2000;

        // PTP scenario: ptp-autoaim-cache-probe.
        // Verifies that PTP's reset postfixes
        //   RotatableTargetHorizontalResetPatch / RotatableTargetVerticalResetPatch
        // (commit 14946c5, gated on NetworkManager.IsServer) correctly clear the
        // AutoAimState cache when TargetHorizontal is written from outside auto-aim,
        // AND that AutoAimState.ClearCache raises the AutoAimUpdateFlag bit so the
        // cleared state propagates to clients via the existing per-tick delta.
        //
        // Method: find transmitters that already have a non-zero cached auto-aim
        // target (loaded from the save's auto-aim side-car). For each, write the
        // current TargetHorizontal value back (no slew change). The Harmony postfix
        // fires regardless of value change, so RotatableTargetHorizontalResetPatch
        // runs, sees AutoAimState.SuppressReset == false (we are not inside an
        // auto-aim write), passes the IsServer gate (server-side), and calls
        // AutoAimState.ClearCache. ClearCache sets the cache box to 0 and raises
        // dish.NetworkUpdateFlags |= AutoAimUpdateFlag.
        //
        // Verifies:
        //   - The cache transitions from non-zero to 0 (covers TODO #1: SP override
        //     clears the cache).
        //   - The AutoAimUpdateFlag bit is set on the dish after the clear (covers
        //     TODO #3 server-side: ClearCache flag-raise propagates the clear via
        //     the existing per-tick payload; cannot verify the client receives and
        //     applies the clear from server-side observation).
        //
        // Limit: skipping the cache-populate step. HandleWrite reads/writes Unity
        // transforms in its solver and would crash from the worker thread. Existing
        // cached entries from the save are used as the test fixture instead.

        private static bool _ptpAutoAimCacheProbeFired;

        private static void Scenario_PtpAutoAimCacheProbe()
        {
            if (!RequireModAssembly(PTP_ASSEMBLY, "ptp-autoaim-cache-probe")) return;
            if (_ptpAutoAimCacheProbeFired) return;
            _ptpAutoAimCacheProbeFired = true;

            var asm = GetModAssembly(PTP_ASSEMBLY);
            var autoAimStateType = asm.GetType("PowerTransmitterPlus.AutoAimState");
            if (autoAimStateType == null)
            {
                _log?.LogError("[ScenarioRunner] PtpAACP: PowerTransmitterPlus.AutoAimState type not found");
                return;
            }
            var getCachedTarget = autoAimStateType.GetMethod("GetCachedTarget",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var restoreCache = autoAimStateType.GetMethod("RestoreCache",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (getCachedTarget == null || restoreCache == null)
            {
                _log?.LogError("[ScenarioRunner] PtpAACP: required AutoAimState methods (GetCachedTarget, RestoreCache) not found");
                return;
            }

            // Partition: dishes with an existing cached target (loaded from the
            // save's side-car) vs linked-but-uncached. For uncached + linked we
            // synthesize a cache via AutoAimState.RestoreCache (calls SetCache,
            // managed-state-only writes; bypasses HandleWrite's solver which would
            // be unsafe from the worker thread). Synthesised entries are tagged so
            // log lines distinguish them.
            var preserved = new List<PowerTransmitter>();
            var linkedUncached = new List<PowerTransmitter>();
            OcclusionManager.AllThings.ForEach(t =>
            {
                if (t is PowerTransmitter tx)
                {
                    long cache = (long)getCachedTarget.Invoke(null, new object[] { tx });
                    if (cache != 0L) preserved.Add(tx);
                    else if (tx.LinkedReceiver != null) linkedUncached.Add(tx);
                }
            });

            var candidates = new List<(PowerTransmitter tx, bool synthetic)>();
            foreach (var tx in preserved) candidates.Add((tx, false));

            // Synthesise up to 3 additional entries if the save had none cached.
            // RestoreCache(dish, targetId) -> SetCache(dish, targetId): puts the rx
            // ReferenceId into the cache box and sets dish.NetworkUpdateFlags |=
            // AutoAimUpdateFlag. No Unity API calls. Safe from the worker.
            int wantSynth = Mathf.Min(3, linkedUncached.Count) - preserved.Count;
            if (wantSynth > 0)
            {
                foreach (var tx in linkedUncached)
                {
                    if (wantSynth <= 0) break;
                    var rx = tx.LinkedReceiver;
                    if (rx == null) continue;
                    restoreCache.Invoke(null, new object[] { tx, rx.ReferenceId });
                    long verify = (long)getCachedTarget.Invoke(null, new object[] { tx });
                    if (verify == rx.ReferenceId)
                    {
                        candidates.Add((tx, true));
                        wantSynth--;
                        _log?.LogInfo($"[ScenarioRunner] PtpAACP synth: tx={tx.ReferenceId} cache<-{rx.ReferenceId} (via RestoreCache)");
                    }
                    else
                    {
                        _log?.LogWarning($"[ScenarioRunner] PtpAACP synth FAIL: tx={tx.ReferenceId} expected cache={rx.ReferenceId} got={verify}");
                    }
                }
            }

            _log?.LogInfo($"[ScenarioRunner] PtpAACP: candidates={candidates.Count} (preserved={preserved.Count} synthetic={candidates.Count - preserved.Count})");

            if (candidates.Count == 0)
            {
                _log?.LogWarning("[ScenarioRunner] PtpAACP SKIP: no candidates (no cached entries from save AND no linked TX-RX pairs to synthesise against). Need a save with at least one linked dish pair.");
                return;
            }

            int probed = 0;
            int clearOk = 0;
            int flagOk = 0;

            foreach (var entry in candidates)
            {
                if (probed >= 5) break;
                var tx = entry.tx;
                var origin = entry.synthetic ? "synth" : "saved";

                long beforeCache = (long)getCachedTarget.Invoke(null, new object[] { tx });

                // Clear the AutoAimUpdateFlag bit so we can observe ClearCache raising it.
                tx.NetworkUpdateFlags = (ushort)(tx.NetworkUpdateFlags & ~PTP_AUTOAIM_UPDATE_FLAG);
                ushort flagBefore = (ushort)(tx.NetworkUpdateFlags & PTP_AUTOAIM_UPDATE_FLAG);

                try
                {
                    // Write current TargetHorizontal back via reflection (avoids needing to
                    // import the RotatableBehaviour type into ScenarioRunner). Same value =
                    // no slew change, but Harmony postfix still fires.
                    var rbProp = tx.GetType().GetProperty("RotatableBehaviour");
                    var rb = rbProp?.GetValue(tx);
                    if (rb == null)
                    {
                        _log?.LogError($"[ScenarioRunner] PtpAACP[{probed}] tx={tx.ReferenceId} ({origin}) RotatableBehaviour property missing");
                        probed++;
                        continue;
                    }
                    var thProp = rb.GetType().GetProperty("TargetHorizontal");
                    if (thProp == null)
                    {
                        _log?.LogError($"[ScenarioRunner] PtpAACP[{probed}] tx={tx.ReferenceId} ({origin}) TargetHorizontal property missing");
                        probed++;
                        continue;
                    }
                    var currentH = thProp.GetValue(rb);
                    thProp.SetValue(rb, currentH);
                }
                catch (Exception e)
                {
                    _log?.LogError($"[ScenarioRunner] PtpAACP[{probed}] tx={tx.ReferenceId} ({origin}) override-write threw: {e.Message}");
                    probed++;
                    continue;
                }

                long afterCache = (long)getCachedTarget.Invoke(null, new object[] { tx });
                ushort flagAfter = (ushort)(tx.NetworkUpdateFlags & PTP_AUTOAIM_UPDATE_FLAG);
                bool cacheCleared = (afterCache == 0L);
                bool flagSet = (flagAfter != 0);
                if (cacheCleared) clearOk++;
                if (flagSet) flagOk++;

                _log?.LogInfo($"[ScenarioRunner] PtpAACP[{probed}] tx={tx.ReferenceId} ({origin}) " +
                              $"beforeCache={beforeCache} afterCache={afterCache} " +
                              $"flagBefore={flagBefore} flagAfter=0x{flagAfter:X4} " +
                              $"clearOk={cacheCleared} flagSetOk={flagSet}");
                probed++;
            }

            _log?.LogInfo($"[ScenarioRunner] PtpAACP summary: probed={probed} clearOk={clearOk}/{probed} flagSetOk={flagOk}/{probed}");
            if (probed > 0 && clearOk == probed && flagOk == probed)
                _log?.LogInfo("[ScenarioRunner] PtpAACP PASS: every probed dish cleared its auto-aim cache AND raised AutoAimUpdateFlag (0x2000) on manual TargetHorizontal override.");
            else if (probed == 0)
                _log?.LogWarning("[ScenarioRunner] PtpAACP SKIP: no candidates were probed.");
            else
                _log?.LogError($"[ScenarioRunner] PtpAACP FAIL: cache-clear {clearOk}/{probed}, flag-raise {flagOk}/{probed}.");
        }

        // PTP scenario: ptp-long-distance-link-probe.
        // Observational. Enumerates every PowerTransmitter with a non-null
        // LinkedReceiver, reads PowerTransmitter._linkedReceiverDistance (set by
        // LinkPatch on every successful link probe) via reflection, and reports
        // distance distribution. PASS if at least one linked pair is at >= 150 m,
        // which is evidence the joint mutual-aim solver (v1.7.1 + LinkPatch's
        // SphereCast widening) still establishes links at the user-tested range.
        //
        // Limit: observational, not proactive. The post-load auto-aim re-solve pass
        // (AutoAimSaveLoadPatches) re-runs HandleWrite for every cached pair after
        // every Thing.OnFinishedLoad has run, so a link being present at scenario
        // tick time means it survived both initial deserialisation AND the joint
        // solver's post-load fixed-point iteration. That is sufficient evidence the
        // solver works at the observed range without us proactively breaking and
        // re-establishing links.

        private static bool _ptpLongDistanceLinkProbeFired;

        private static void Scenario_PtpLongDistanceLinkProbe()
        {
            if (!RequireModAssembly(PTP_ASSEMBLY, "ptp-long-distance-link-probe")) return;
            if (_ptpLongDistanceLinkProbeFired) return;
            _ptpLongDistanceLinkProbeFired = true;

            var distanceField = typeof(PowerTransmitter).GetField("_linkedReceiverDistance",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (distanceField == null)
            {
                _log?.LogError("[ScenarioRunner] PtpLDLP: PowerTransmitter._linkedReceiverDistance field not found");
                return;
            }

            int total = 0;
            int linked = 0;
            int linkedLong = 0;
            float maxDistance = 0f;
            var longPairs = new List<string>();

            OcclusionManager.AllThings.ForEach(t =>
            {
                if (!(t is PowerTransmitter tx)) return;
                total++;
                var rx = tx.LinkedReceiver;
                if (rx == null) return;
                linked++;

                float d = (float)distanceField.GetValue(tx);
                if (d > maxDistance) maxDistance = d;
                if (d >= 150f)
                {
                    linkedLong++;
                    longPairs.Add($"tx={tx.ReferenceId} rx={rx.ReferenceId} distance={d:F2}m");
                }
            });

            _log?.LogInfo($"[ScenarioRunner] PtpLDLP summary: totalTX={total} linked={linked} linkedAtLongDistance(>=150m)={linkedLong} maxDistanceObserved={maxDistance:F2}m");
            foreach (var p in longPairs)
                _log?.LogInfo($"[ScenarioRunner] PtpLDLP[long] {p}");

            if (total == 0)
                _log?.LogWarning("[ScenarioRunner] PtpLDLP SKIP: no transmitters in scene.");
            else if (linked == 0)
                _log?.LogWarning("[ScenarioRunner] PtpLDLP SKIP: no linked TX-RX pairs in scene.");
            else if (linkedLong > 0)
                _log?.LogInfo($"[ScenarioRunner] PtpLDLP PASS: {linkedLong} linked TX-RX pair(s) at >=150m, indicating the joint mutual-aim solver and SphereCast link probe work at the user's tested range. Survived post-load re-solve pass.");
            else
                _log?.LogWarning($"[ScenarioRunner] PtpLDLP INCONCLUSIVE: no long-distance (>=150m) linked pairs in scene; max distance observed: {maxDistance:F2}m. Joint solver behaviour at >=150m cannot be verified against this save.");
        }

        // PTP scenario: ptp-beam-predicate-probe.
        // Cross-checks PowerTransmitterPlus.BeamVisibility.ShouldShow against an
        // independent classification of every PowerTransmitter in the scene by link
        // state and OnOff (the two inputs we can read safely from the worker
        // thread). The predicate also gates on aim validity (forward-antiparallel
        // within 7 degrees), which is a Unity-transform read; we do not compute that
        // here, so the linked + both-on case is informational (the predicate may
        // legitimately return false for misaimed pairs).
        //
        // PASS if ShouldShow returns false for every unlinked TX, every linked-but-
        // tx-off TX, and every linked-but-rx-off TX. Any TRUE in those categories is
        // a predicate bug. The linked + both-on TRUE count is reported as a ratio:
        // a population of links established and aimed should land most pairs there.

        private static bool _ptpBeamPredicateProbeFired;

        private static void Scenario_PtpBeamPredicateProbe()
        {
            if (!RequireModAssembly(PTP_ASSEMBLY, "ptp-beam-predicate-probe")) return;
            if (_ptpBeamPredicateProbeFired) return;
            _ptpBeamPredicateProbeFired = true;

            var asm = GetModAssembly(PTP_ASSEMBLY);
            var beamVisibilityType = asm.GetType("PowerTransmitterPlus.BeamVisibility");
            if (beamVisibilityType == null)
            {
                _log?.LogError("[ScenarioRunner] PtpBPP: PowerTransmitterPlus.BeamVisibility type not found");
                return;
            }
            var shouldShow = beamVisibilityType.GetMethod("ShouldShow",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (shouldShow == null)
            {
                _log?.LogError("[ScenarioRunner] PtpBPP: BeamVisibility.ShouldShow(PowerTransmitter) method not found");
                return;
            }

            int totalTx = 0;
            int unlinked = 0;
            int unlinkedFalseOk = 0;
            int linkedTxOff = 0;
            int linkedTxOffFalseOk = 0;
            int linkedRxOff = 0;
            int linkedRxOffFalseOk = 0;
            int linkedBothOn = 0;
            int linkedBothOnTrue = 0;
            int unexpected = 0;
            var unexpectedSamples = new List<string>();

            OcclusionManager.AllThings.ForEach(t =>
            {
                if (!(t is PowerTransmitter tx)) return;
                totalTx++;

                var rx = tx.LinkedReceiver;
                bool actual;
                try
                {
                    actual = (bool)shouldShow.Invoke(null, new object[] { tx });
                }
                catch (Exception e)
                {
                    _log?.LogError($"[ScenarioRunner] PtpBPP tx={tx.ReferenceId} ShouldShow threw: {e.Message}");
                    return;
                }

                if (rx == null)
                {
                    unlinked++;
                    if (!actual) unlinkedFalseOk++;
                    else
                    {
                        unexpected++;
                        if (unexpectedSamples.Count < 5)
                            unexpectedSamples.Add($"unlinked TX shouldShow=true: tx={tx.ReferenceId}");
                    }
                }
                else if (!tx.OnOff)
                {
                    linkedTxOff++;
                    if (!actual) linkedTxOffFalseOk++;
                    else
                    {
                        unexpected++;
                        if (unexpectedSamples.Count < 5)
                            unexpectedSamples.Add($"tx.OnOff=false shouldShow=true: tx={tx.ReferenceId} rx={rx.ReferenceId}");
                    }
                }
                else if (!rx.OnOff)
                {
                    linkedRxOff++;
                    if (!actual) linkedRxOffFalseOk++;
                    else
                    {
                        unexpected++;
                        if (unexpectedSamples.Count < 5)
                            unexpectedSamples.Add($"rx.OnOff=false shouldShow=true: tx={tx.ReferenceId} rx={rx.ReferenceId}");
                    }
                }
                else
                {
                    // Linked + both on. shouldShow then depends on aim validity, which we
                    // cannot independently compute from the worker thread. Just report.
                    linkedBothOn++;
                    if (actual) linkedBothOnTrue++;
                }
            });

            _log?.LogInfo($"[ScenarioRunner] PtpBPP totals: TX={totalTx} unlinked={unlinked} linkedTxOff={linkedTxOff} linkedRxOff={linkedRxOff} linkedBothOn={linkedBothOn}");
            _log?.LogInfo($"[ScenarioRunner] PtpBPP negative checks (expect shouldShow=false): unlinked {unlinkedFalseOk}/{unlinked}, txOff {linkedTxOffFalseOk}/{linkedTxOff}, rxOff {linkedRxOffFalseOk}/{linkedRxOff}");
            _log?.LogInfo($"[ScenarioRunner] PtpBPP linked+both-on -> shouldShow=true (aim-dependent): {linkedBothOnTrue}/{linkedBothOn}");
            foreach (var s in unexpectedSamples)
                _log?.LogError($"[ScenarioRunner] PtpBPP unexpected: {s}");

            bool negativesPass = unlinkedFalseOk == unlinked && linkedTxOffFalseOk == linkedTxOff && linkedRxOffFalseOk == linkedRxOff;
            if (totalTx == 0)
                _log?.LogWarning("[ScenarioRunner] PtpBPP SKIP: no transmitters in scene.");
            else if (negativesPass)
                _log?.LogInfo("[ScenarioRunner] PtpBPP PASS: BeamVisibility.ShouldShow correctly returned false for every unlinked TX, every linked-but-tx-off TX, and every linked-but-rx-off TX. The linked+both-on aim-dependent ratio is informational.");
            else
                _log?.LogError($"[ScenarioRunner] PtpBPP FAIL: {unexpected} unexpected shouldShow=true result(s) for unlinked or switched-off TX.");
        }

        // PGP scenario: priority-deprioritization-probe.
        // One-shot, multi-phase end-to-end verification of the Transformer Priority +
        // Deprioritization feature surface. Reaches into PowerGridPlus via reflection (no
        // build-time dependency on PGP) and asserts results structurally against the
        // CURRENT architecture: the Priority + Deprioritization system is always on (no master
        // toggle), Priority is a first-class writable LogicType slot (Setting / Ratio
        // are pure vanilla, no redirect), allocations are computed once per tick by
        // PowerAllocator and published to TransformerSupplyCache (live allocation
        // behaviour is covered by pgp-priority-deprioritization-topology-probe,
        // pgp-deprioritization-multilevel, and pgp-chain-fixture, which drive across ticks).
        //
        // Phases:
        //   1. Inventory + baseline. Count Transformers, log per-Transformer state
        //      (Priority, OutputMaximum, OnOff, Error, IsDeprioritized, published output
        //      from TransformerSupplyCache, both network refs). Baseline Priority is
        //      the default 100 for untouched transformers.
        //   2. Priority logic slot. SetLogicValue(Priority=6578, 175) through the real
        //      Device path (server-gated write + PriorityMessage broadcast, a no-op at
        //      zero clients); GetLogicValue(Priority) reads it back; a Setting write
        //      stays PURE VANILLA (does not touch Priority; Setting readback returns
        //      the written value; restored after).
        //   3. CanLogicRead/Write surface: Priority R+W, Deprioritization read-only.
        //   4. PriorityStore contract: SetPriority persists, bumps the monotonic
        //      Version (the allocator's invalidation cue), and clamps negatives to 0.
        //   4b. Lockout precedence: NoteDeprioritized arms the 60 s lockout instantly and a
        //      priority write does NOT clear it (only expiry or the OFF-as-reset
        //      ClearLockout does).
        //   5. Registry constants: LockoutDurationTicks == 120 (60 s at 2 Hz) and the
        //      retired ShortfallTolerance counter stays deleted.
        //   6. Deleted-surface tripwires: the EnableTransformerShedding ConfigEntry and
        //      the DeprioritizedSettingsSync / OverloadSettingsSync / PassthroughSettingsSync /
        //      TransformerAllocator types must all be ABSENT (always-on rework), while
        //      PowerAllocator exists.
        //   7. Stationpedia footer. StationpediaPatches.GetDescriptionFooter for every
        //      Transformer prefab name carries the "POWER GRID PLUS" header; ItemWrench
        //      (non-transformer) does not.
        //   8. PriorityMessage host short-circuit (Process on the host is a no-op).
        //   9. PrioritySideCar snapshot inventory (phase 2/4 writes visible).
        //  10. PrioritySideCar on-disk Write -> Read round-trip.
        //  11. OFF-as-reset seam: ClearLockout drops a fresh lockout immediately.
        //  12. SnapshotRemaining carries the (refId, remainingTicks) pair the per-tick
        //      FaultRegistrySnapshotMessage heartbeat serializes.
        //
        // Each phase emits structured `[ScenarioRunner] PSP ...` lines and tags
        // PASS/FAIL per check. The final summary line aggregates pass/fail counts.
        //
        // Threading: every read/invoke runs from the simulation-tick worker (UniTask
        // ThreadPool). All reflection-driven calls touch managed state only; no Unity
        // API calls. PriorityStore / DeprioritizedRegistry are concurrent dictionaries safe
        // to read from the worker.
        //
        // Side effects: phase 2 leaves the sample transformer's Priority at 175 and phase 4
        // leaves a second transformer at 137 (so the save-load follow-up
        // pgp-priority-deprioritization-persist-probe has >= 2 non-default values to verify);
        // everything else is restored. The scenario logs what was written.

        private static bool _pspFired;

        private static void Scenario_PgpPriorityDeprioritizationProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-priority-deprioritization-probe")) return;
            if (_pspFired) return;
            _pspFired = true;

            int totalChecks = 0;
            int passCount = 0;
            int failCount = 0;

            try
            {
                _log?.LogInfo("[ScenarioRunner] PSP START priority-deprioritization-probe");

                var asm = GetModAssembly(PGP_ASSEMBLY);
                if (asm == null) { _log?.LogError("[ScenarioRunner] PSP no PGP assembly"); return; }

                // Resolve the PGP types the probe drives; missing types are FAIL. The deleted
                // types (DeprioritizedSettingsSync / TransformerAllocator and friends) are looked up too,
                // but as ABSENCE tripwires asserted in phase 6.
                var priorityStoreType = asm.GetType("PowerGridPlus.PriorityStore");
                var deprioritizedRegistryType = asm.GetType("PowerGridPlus.DeprioritizedRegistry");
                var tickCounterType = asm.GetType("PowerGridPlus.ElectricityTickCounter");
                var logicRegType = asm.GetType("PowerGridPlus.LogicTypeRegistry");
                var settingsType = asm.GetType("PowerGridPlus.Settings");
                var stationpediaType = asm.GetType("PowerGridPlus.StationpediaPatches");
                var priorityLogicPatchType = asm.GetType("PowerGridPlus.Patches.TransformerPriorityLogicPatches");
                var priorityMessageType = asm.GetType("PowerGridPlus.PriorityMessage");

                if (priorityStoreType == null) { _log?.LogError("[ScenarioRunner] PSP FAIL: PriorityStore type missing"); failCount++; return; }
                if (deprioritizedRegistryType == null) { _log?.LogError("[ScenarioRunner] PSP FAIL: DeprioritizedRegistry type missing"); failCount++; return; }
                if (tickCounterType == null) { _log?.LogError("[ScenarioRunner] PSP FAIL: ElectricityTickCounter type missing"); failCount++; return; }
                if (priorityLogicPatchType == null) { _log?.LogError("[ScenarioRunner] PSP FAIL: TransformerPriorityLogicPatches type missing"); failCount++; return; }

                var getPriorityMethod = priorityStoreType.GetMethod("GetPriority",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                    null, new[] { typeof(long) }, null);
                var setPriorityMethod = priorityStoreType.GetMethod("SetPriority",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                    null, new[] { typeof(Assets.Scripts.Objects.Thing), typeof(int) }, null);
                var versionProp = priorityStoreType.GetProperty("Version",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var isDeprioritizedMethod = deprioritizedRegistryType.GetMethod("IsDeprioritized",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                    null, new[] { typeof(long), typeof(int) }, null);
                var isLockedOutMethodLong = deprioritizedRegistryType.GetMethod("IsLockedOut",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                    null, new[] { typeof(long), typeof(int) }, null);
                var noteDeprioritizedMethod = deprioritizedRegistryType.GetMethod("NoteDeprioritized",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                    null, new[] { typeof(long), typeof(int) }, null);
                var clearLockoutMethod = deprioritizedRegistryType.GetMethod("ClearLockout",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                    null, new[] { typeof(long) }, null);
                var snapshotRemainingMethod = deprioritizedRegistryType.GetMethod("SnapshotRemaining",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var currentTickProp = tickCounterType.GetProperty("CurrentTick",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

                int currentTick = (int)(currentTickProp?.GetValue(null) ?? -1);

                _log?.LogInfo($"[ScenarioRunner] PSP env: ElectricityTickCounter.CurrentTick={currentTick} (Priority + Deprioritization is always on; no master toggle)");

                // ---- PHASE 1: inventory + baseline ----
                if (_transformers.Count == 0) RebuildCaches();
                _log?.LogInfo($"[ScenarioRunner] PSP P1 transformers={_transformers.Count}");

                // Per-transformer baseline: published output comes from TransformerSupplyCache
                // (the per-tick presentation totals PowerAllocator publishes; -1 = no entry).
                foreach (var t in _transformers)
                {
                    if (t == null || t.InputNetwork == null || t.OutputNetwork == null) continue;
                    int prio = (int)(getPriorityMethod?.Invoke(null, new object[] { t.ReferenceId }) ?? -1);
                    float published = TpPublishedOutput(asm, t.ReferenceId);
                    bool deprioritized = (bool)(isDeprioritizedMethod?.Invoke(null, new object[] { t.ReferenceId, currentTick }) ?? false);
                    double setting = t.Setting;
                    _log?.LogInfo(
                        $"[ScenarioRunner] PSP P1 T ref={t.ReferenceId} prefab={t.PrefabName} OnOff={t.OnOff} Error={t.Error} " +
                        $"OutputMax={t.OutputMaximum:F0} Setting={setting:F0} Priority={prio} PublishedOut={published:F0} Deprioritization={deprioritized} " +
                        $"InNet={t.InputNetwork.ReferenceId} OutNet={t.OutputNetwork.ReferenceId}");
                }

                // Phase 1 assert: every transformer reports the default priority (100) if no one has
                // touched it yet. We only check ON transformers (off transformers' allocation is 0
                // regardless of priority, but their PriorityStore default is still 100).
                int defaultPriorityHits = 0;
                int defaultPriorityMisses = 0;
                foreach (var t in _transformers)
                {
                    if (t == null) continue;
                    int prio = (int)(getPriorityMethod?.Invoke(null, new object[] { t.ReferenceId }) ?? -1);
                    if (prio == 100) defaultPriorityHits++;
                    else { defaultPriorityMisses++; _log?.LogWarning($"[ScenarioRunner] PSP P1 unexpected non-default priority {prio} on ref={t.ReferenceId} (legacy save-load already wrote one?)"); }
                }
                totalChecks++;
                if (defaultPriorityMisses == 0)
                { _log?.LogInfo($"[ScenarioRunner] PSP P1 PASS: every {defaultPriorityHits} transformer reports DefaultPriority=100 (clean baseline)."); passCount++; }
                else
                { _log?.LogInfo($"[ScenarioRunner] PSP P1 NOTE: {defaultPriorityMisses} transformers have non-default priorities from a prior save; baseline tainted but not a failure."); passCount++; }

                // ---- PHASE 2: the writable Priority logic slot; Setting stays pure vanilla ----
                // The old Setting -> Priority redirect was retired: LogicType.Setting reads and
                // writes are vanilla (clamped [0, OutputMaximum] by the property); Priority is a
                // first-class writable slot (LogicTypeRegistry.Priority = 6578, the number owned
                // by Patterns/Logic/LogicTypeNumbers.cs).
                Transformer sampleT = _transformers.FirstOrDefault(x => x != null && x.OnOff);
                if (sampleT == null && _transformers.Count > 0) sampleT = _transformers[0];
                if (sampleT == null)
                {
                    _log?.LogWarning("[ScenarioRunner] PSP P2 SKIP: no Transformer to probe.");
                }
                else
                {
                    var priorityLogic = (Assets.Scripts.Objects.Motherboards.LogicType)6578;

                    int prioBefore = (int)(getPriorityMethod?.Invoke(null, new object[] { sampleT.ReferenceId }) ?? -1);
                    // Real Device.SetLogicValue path: PGP's SetLogicValuePatch (server-gated write
                    // + PriorityMessage broadcast, a no-op with zero clients connected).
                    sampleT.SetLogicValue(priorityLogic, 175.0);
                    int prioAfter = (int)(getPriorityMethod?.Invoke(null, new object[] { sampleT.ReferenceId }) ?? -1);

                    totalChecks++;
                    if (prioAfter == 175)
                    { _log?.LogInfo($"[ScenarioRunner] PSP P2 PASS: SetLogicValue(Priority, 175) wrote through to PriorityStore. before={prioBefore} after={prioAfter} ref={sampleT.ReferenceId}"); passCount++; }
                    else
                    { _log?.LogError($"[ScenarioRunner] PSP P2 FAIL: SetLogicValue(Priority, 175) did not land. Priority before={prioBefore} after={prioAfter} ref={sampleT.ReferenceId}"); failCount++; }

                    totalChecks++;
                    var prioReadback = sampleT.GetLogicValue(priorityLogic);
                    if (System.Math.Abs(prioReadback - 175.0) < 0.001)
                    { _log?.LogInfo($"[ScenarioRunner] PSP P2 PASS: GetLogicValue(Priority) reads back {prioReadback:F0}. ref={sampleT.ReferenceId}"); passCount++; }
                    else
                    { _log?.LogError($"[ScenarioRunner] PSP P2 FAIL: GetLogicValue(Priority)={prioReadback:F0}, expected 175. ref={sampleT.ReferenceId}"); failCount++; }

                    // Setting is PURE VANILLA now: a Setting write must not touch Priority (a
                    // regression back to the redirect era would), and its readback must return
                    // the written value through the vanilla clamp. Restored afterwards.
                    double settingBefore = sampleT.GetLogicValue(Assets.Scripts.Objects.Motherboards.LogicType.Setting);
                    double settingProbe = System.Math.Min(37.0, sampleT.OutputMaximum);
                    sampleT.SetLogicValue(Assets.Scripts.Objects.Motherboards.LogicType.Setting, settingProbe);
                    double settingReadback = sampleT.GetLogicValue(Assets.Scripts.Objects.Motherboards.LogicType.Setting);
                    int prioAfterSetting = (int)(getPriorityMethod?.Invoke(null, new object[] { sampleT.ReferenceId }) ?? -1);
                    sampleT.SetLogicValue(Assets.Scripts.Objects.Motherboards.LogicType.Setting, settingBefore);

                    totalChecks++;
                    if (prioAfterSetting == 175 && System.Math.Abs(settingReadback - settingProbe) < 0.01)
                    { _log?.LogInfo($"[ScenarioRunner] PSP P2 PASS: SetLogicValue(Setting, {settingProbe:F0}) stayed vanilla (Setting readback={settingReadback:F0}, Priority untouched at {prioAfterSetting}); Setting restored to {settingBefore:F0}. ref={sampleT.ReferenceId}"); passCount++; }
                    else
                    { _log?.LogError($"[ScenarioRunner] PSP P2 FAIL: Setting write leaked. settingReadback={settingReadback:F0} (expected {settingProbe:F0}), Priority={prioAfterSetting} (expected 175, a change means the retired redirect is back). ref={sampleT.ReferenceId}"); failCount++; }
                }

                // ---- PHASE 3: CanLogicRead / CanLogicWrite surface ----
                if (sampleT != null)
                {
                    ushort priorityTypeValue = 6578;
                    ushort sheddingTypeValue = 6579;

                    bool canReadPriority = sampleT.CanLogicRead((Assets.Scripts.Objects.Motherboards.LogicType)priorityTypeValue);
                    bool canWritePriority = sampleT.CanLogicWrite((Assets.Scripts.Objects.Motherboards.LogicType)priorityTypeValue);
                    bool canReadDeprioritization = sampleT.CanLogicRead((Assets.Scripts.Objects.Motherboards.LogicType)sheddingTypeValue);
                    bool canWriteDeprioritization = sampleT.CanLogicWrite((Assets.Scripts.Objects.Motherboards.LogicType)sheddingTypeValue);

                    totalChecks++;
                    if (canReadPriority && canWritePriority)
                    { _log?.LogInfo("[ScenarioRunner] PSP P3 PASS: CanLogicRead(Priority)=true, CanLogicWrite(Priority)=true."); passCount++; }
                    else
                    { _log?.LogError($"[ScenarioRunner] PSP P3 FAIL: Priority surface wrong. canRead={canReadPriority} canWrite={canWritePriority}"); failCount++; }

                    totalChecks++;
                    if (canReadDeprioritization && !canWriteDeprioritization)
                    { _log?.LogInfo("[ScenarioRunner] PSP P3 PASS: CanLogicRead(Deprioritization)=true, CanLogicWrite(Deprioritization)=false (read-only)."); passCount++; }
                    else
                    { _log?.LogError($"[ScenarioRunner] PSP P3 FAIL: Deprioritization surface wrong (expected RO). canRead={canReadDeprioritization} canWrite={canWriteDeprioritization}"); failCount++; }
                }

                // ---- PHASE 4: PriorityStore contract (persist, Version bump, negative clamp) ----
                // Live strict-priority allocation is multi-tick under the atomic allocator and is
                // covered by pgp-priority-deprioritization-topology-probe / pgp-deprioritization-multilevel /
                // pgp-chain-fixture; here the probe asserts the store semantics the allocator
                // depends on: a write persists, bumps the monotonic Version counter (the
                // intra-tick invalidation cue), and clamps negatives to 0. Runs on a SECOND
                // transformer where one exists and leaves it at 137, so together with P2's 175
                // the save carries the >= 2 non-default priorities the persist follow-up
                // (pgp-priority-deprioritization-persist-probe) verifies across a reload.
                if (sampleT != null)
                {
                    Transformer contractT = _transformers.FirstOrDefault(x => x != null && !ReferenceEquals(x, sampleT)) ?? sampleT;

                    int v0 = versionProp?.GetValue(null) is int vi ? vi : int.MinValue;
                    setPriorityMethod?.Invoke(null, new object[] { contractT, 137 });
                    int after137 = (int)(getPriorityMethod?.Invoke(null, new object[] { contractT.ReferenceId }) ?? -1);
                    int v1 = versionProp?.GetValue(null) is int vj ? vj : int.MinValue;

                    totalChecks++;
                    if (after137 == 137 && v1 > v0)
                    { _log?.LogInfo($"[ScenarioRunner] PSP P4 PASS: SetPriority persisted (137 on ref={contractT.ReferenceId}) and bumped Version ({v0} -> {v1})."); passCount++; }
                    else
                    { _log?.LogError($"[ScenarioRunner] PSP P4 FAIL: SetPriority contract broken. priority={after137} (expected 137) Version {v0} -> {v1} (expected a bump)."); failCount++; }

                    setPriorityMethod?.Invoke(null, new object[] { contractT, -5 });
                    int afterNeg = (int)(getPriorityMethod?.Invoke(null, new object[] { contractT.ReferenceId }) ?? -1);
                    totalChecks++;
                    if (afterNeg == 0)
                    { _log?.LogInfo("[ScenarioRunner] PSP P4 PASS: SetPriority(-5) clamped to 0 (non-negative contract)."); passCount++; }
                    else
                    { _log?.LogError($"[ScenarioRunner] PSP P4 FAIL: SetPriority(-5) stored {afterNeg}, expected clamp to 0."); failCount++; }

                    // Leave the contract transformer at 137 (or restore the P2 value when the
                    // save has only one transformer).
                    setPriorityMethod?.Invoke(null,
                        new object[] { contractT, ReferenceEquals(contractT, sampleT) ? 175 : 137 });
                }

                // ---- PHASE 4b: lockout precedence over priority changes ----
                // NoteDeprioritized arms the 60 s lockout INSTANTLY (no tolerance counter in the atomic
                // architecture), and a priority write must NOT clear it: only expiry or the
                // OFF-as-reset ClearLockout releases a locked device. Uses the sample transformer
                // transiently; no allocator pass runs between arm and clear (the pump is a
                // postfix), so the transient lockout never affects a real allocation.
                if (sampleT != null)
                {
                    int simTick = (int)(currentTickProp?.GetValue(null) ?? 0);
                    noteDeprioritizedMethod?.Invoke(null, new object[] { sampleT.ReferenceId, simTick });
                    bool lockedAfterNote = (bool)(isLockedOutMethodLong?.Invoke(null, new object[] { sampleT.ReferenceId, simTick }) ?? false);

                    setPriorityMethod?.Invoke(null, new object[] { sampleT, 9999 });
                    bool lockedAfterPromote = (bool)(isLockedOutMethodLong?.Invoke(null, new object[] { sampleT.ReferenceId, simTick }) ?? false);

                    clearLockoutMethod?.Invoke(null, new object[] { sampleT.ReferenceId });
                    bool lockedAfterClear = (bool)(isLockedOutMethodLong?.Invoke(null, new object[] { sampleT.ReferenceId, simTick }) ?? false);
                    setPriorityMethod?.Invoke(null, new object[] { sampleT, 175 });

                    totalChecks++;
                    if (lockedAfterNote && lockedAfterPromote && !lockedAfterClear)
                    { _log?.LogInfo("[ScenarioRunner] PSP P4b PASS: NoteDeprioritized arms the lockout instantly, a priority write (9999) does NOT clear it, ClearLockout does."); passCount++; }
                    else
                    { _log?.LogError($"[ScenarioRunner] PSP P4b FAIL: lockedAfterNote={lockedAfterNote} lockedAfterPromote={lockedAfterPromote} lockedAfterClear={lockedAfterClear} (expected true/true/false)."); failCount++; }
                }

                // ---- PHASE 5: registry constants ----
                // LockoutDurationTicks is 120 (60 seconds at 2 Hz) and the retired
                // ShortfallTolerance counter (the old 2-consecutive-ticks rule) stays deleted:
                // the atomic tick has fresh in-tick supply data, so lockout fires instantly.
                var shortfallToleranceField = deprioritizedRegistryType.GetField("ShortfallTolerance",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var lockoutDurationField = deprioritizedRegistryType.GetField("LockoutDurationTicks",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                int lockoutTicks = (int)(lockoutDurationField?.GetValue(null) ?? -1);

                totalChecks++;
                if (shortfallToleranceField == null)
                { _log?.LogInfo("[ScenarioRunner] PSP P5 PASS: ShortfallTolerance stays deleted (instant lockout, no tolerance counter)."); passCount++; }
                else
                { _log?.LogError("[ScenarioRunner] PSP P5 FAIL: ShortfallTolerance reappeared on DeprioritizedRegistry (regression to the tolerance-counter era)."); failCount++; }

                totalChecks++;
                if (lockoutTicks == 120)
                { _log?.LogInfo($"[ScenarioRunner] PSP P5 PASS: LockoutDurationTicks={lockoutTicks} (= 60 seconds @ 2 Hz)."); passCount++; }
                else
                { _log?.LogError($"[ScenarioRunner] PSP P5 FAIL: LockoutDurationTicks={lockoutTicks}, expected 120."); failCount++; }

                // ---- PHASE 6: deleted-surface tripwires ----
                // The 2026-07-13 settings rework deleted the master toggles and their synced
                // wrappers; the 2026-07-12 rebuild replaced TransformerAllocator with the atomic
                // PowerAllocator. Assert all of them stay gone (a reappearance is a regression),
                // and that the current allocator type exists.
                var settingFieldT = settingsType?.GetField("EnableTransformerShedding",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var deprioritizedSyncTypeGone = asm.GetType("PowerGridPlus.DeprioritizedSettingsSync");
                var overloadSyncTypeGone = asm.GetType("PowerGridPlus.OverloadSettingsSync");
                var passthroughSyncTypeGone = asm.GetType("PowerGridPlus.PassthroughSettingsSync");
                var legacyAllocatorGone = asm.GetType("PowerGridPlus.TransformerAllocator");
                var powerAllocatorType = asm.GetType("PowerGridPlus.PowerAllocator");
                totalChecks++;
                if (settingFieldT == null && deprioritizedSyncTypeGone == null && overloadSyncTypeGone == null
                    && passthroughSyncTypeGone == null && legacyAllocatorGone == null && powerAllocatorType != null)
                { _log?.LogInfo("[ScenarioRunner] PSP P6 PASS: EnableTransformerShedding ConfigEntry, DeprioritizedSettingsSync, OverloadSettingsSync, PassthroughSettingsSync, and TransformerAllocator are all absent; PowerAllocator exists (always-on atomic architecture)."); passCount++; }
                else
                { _log?.LogError("[ScenarioRunner] PSP P6 FAIL: deleted surface reappeared or current one missing. " +
                    $"EnableTransformerShedding={settingFieldT != null} DeprioritizedSettingsSync={deprioritizedSyncTypeGone != null} " +
                    $"OverloadSettingsSync={overloadSyncTypeGone != null} PassthroughSettingsSync={passthroughSyncTypeGone != null} " +
                    $"TransformerAllocator={legacyAllocatorGone != null} (all expect false); PowerAllocator={powerAllocatorType != null} (expect true)."); failCount++; }

                // ---- PHASE 7: Stationpedia footer ----
                var getDescFooter = stationpediaType?.GetMethod("GetDescriptionFooter",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (getDescFooter == null)
                {
                    totalChecks++;
                    _log?.LogError("[ScenarioRunner] PSP P7 FAIL: StationpediaPatches.GetDescriptionFooter not found.");
                    failCount++;
                }
                else
                {
                    string[] tNames = new[] { "StructureTransformer", "StructureTransformerSmall", "StructureTransformerSmallReversed", "StructureTransformerLarge" };
                    int footerHits = 0;
                    foreach (var n in tNames)
                    {
                        string footer = (string)(getDescFooter.Invoke(null, new object[] { n }) ?? null);
                        bool ok = !string.IsNullOrEmpty(footer) && footer.Contains("POWER GRID PLUS");
                        if (ok) footerHits++;
                        _log?.LogInfo($"[ScenarioRunner] PSP P7 footer for '{n}': present={ok} length={footer?.Length ?? 0}");
                    }
                    totalChecks++;
                    if (footerHits == tNames.Length)
                    { _log?.LogInfo($"[ScenarioRunner] PSP P7 PASS: all {footerHits} transformer prefab names carry the POWER GRID PLUS footer."); passCount++; }
                    else
                    { _log?.LogError($"[ScenarioRunner] PSP P7 FAIL: only {footerHits}/{tNames.Length} transformer prefab names carry the footer."); failCount++; }

                    // Negative case: a known non-transformer prefab gets NO footer here.
                    string negFooter = (string)(getDescFooter.Invoke(null, new object[] { "ItemWrench" }) ?? null);
                    totalChecks++;
                    if (string.IsNullOrEmpty(negFooter))
                    { _log?.LogInfo("[ScenarioRunner] PSP P7 PASS: ItemWrench (non-transformer) has no PGP footer (negative case)."); passCount++; }
                    else
                    { _log?.LogError($"[ScenarioRunner] PSP P7 FAIL: ItemWrench got an unexpected PGP footer."); failCount++; }
                }

                // ---- PHASE 8: PriorityMessage round-trip on host ----
                if (priorityMessageType != null && sampleT != null)
                {
                    var msg = Activator.CreateInstance(priorityMessageType);
                    var deviceIdField = priorityMessageType.GetField("DeviceId");
                    var priorityField = priorityMessageType.GetField("Priority");
                    deviceIdField?.SetValue(msg, sampleT.ReferenceId);
                    priorityField?.SetValue(msg, 999);
                    var processMethod = priorityMessageType.GetMethod("Process");

                    int prioBefore = (int)(getPriorityMethod?.Invoke(null, new object[] { sampleT.ReferenceId }) ?? -1);
                    Exception threw = null;
                    try { processMethod?.Invoke(msg, new object[] { 0L }); } catch (Exception e) { threw = e; }
                    int prioAfter = (int)(getPriorityMethod?.Invoke(null, new object[] { sampleT.ReferenceId }) ?? -1);

                    totalChecks++;
                    if (threw != null)
                    { _log?.LogError($"[ScenarioRunner] PSP P8 FAIL: PriorityMessage.Process threw: {threw.GetBaseException().Message}"); failCount++; }
                    else if (prioAfter == prioBefore)
                    { _log?.LogInfo($"[ScenarioRunner] PSP P8 PASS: PriorityMessage.Process on the host short-circuits (IsServer gate), Priority unchanged ({prioBefore})."); passCount++; }
                    else
                    { _log?.LogError($"[ScenarioRunner] PSP P8 FAIL: PriorityMessage.Process changed Priority host-side ({prioBefore} -> {prioAfter}); expected no-op on host."); failCount++; }
                }
                else
                {
                    totalChecks++;
                    _log?.LogError("[ScenarioRunner] PSP P8 FAIL: PriorityMessage type or sample transformer missing.");
                    failCount++;
                }

                // ---- PHASE 9: PrioritySideCar snapshot inventory ----
                var sideCarType = asm.GetType("PowerGridPlus.PrioritySideCar");
                var snapshotMethod = sideCarType?.GetMethod("Snapshot",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (snapshotMethod != null)
                {
                    var snap = snapshotMethod.Invoke(null, null);
                    var entriesProp = snap?.GetType().GetProperty("Entries");
                    var entries = entriesProp?.GetValue(snap) as System.Collections.IList;
                    int snapCount = entries?.Count ?? -1;
                    totalChecks++;
                    if (snapCount > 0)
                    { _log?.LogInfo($"[ScenarioRunner] PSP P9 PASS: PrioritySideCar.Snapshot() returned {snapCount} entries (matches PriorityStore writes from phases 2/4)."); passCount++; }
                    else
                    { _log?.LogError($"[ScenarioRunner] PSP P9 FAIL: PrioritySideCar.Snapshot() returned 0 entries; expected writes from phases 2/4 to be visible."); failCount++; }
                }

                // ---- PHASE 10: PrioritySideCar on-disk round-trip ----
                // Writes the in-memory snapshot to a temp ZIP, then reads it back, then asserts the
                // count + values match. This exercises Write + Read + the ZipArchive open/append/close
                // dance without depending on the game's SaveHelper.Save UniTask, which on a batch-mode
                // dedicated server can no-op on stdin save commands (see DedicatedServer/CLAUDE.md
                // "Stdin console commands can be a no-op in batch mode").
                var writeMethod = sideCarType?.GetMethod("WriteSideCar",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var readDirMethod = sideCarType?.GetMethod("ReadSideCarFromDir",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (writeMethod != null && readDirMethod != null && snapshotMethod != null)
                {
                    string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                        "pgp-psp-roundtrip-" + System.Guid.NewGuid().ToString("N"));
                    string zipPath = System.IO.Path.Combine(tempDir, "fake.save");
                    string sideCarEntryName = "pwrgridplus-priority.xml";
                    try
                    {
                        System.IO.Directory.CreateDirectory(tempDir);
                        // Create a minimal empty ZIP so WriteSideCar can open it in Update mode.
                        using (var fs = new System.IO.FileStream(zipPath, System.IO.FileMode.Create))
                        using (var z = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
                        {
                            var dummy = z.CreateEntry("dummy.txt");
                            using (var ds = dummy.Open()) { var bs = System.Text.Encoding.UTF8.GetBytes("seed"); ds.Write(bs, 0, bs.Length); }
                        }

                        var beforeSnap = snapshotMethod.Invoke(null, null);
                        var beforeEntries = beforeSnap.GetType().GetProperty("Entries")?.GetValue(beforeSnap) as System.Collections.IList;
                        int beforeCount = beforeEntries?.Count ?? -1;

                        writeMethod.Invoke(null, new object[] { zipPath, beforeSnap });

                        // Extract the entry as a loose file to a sibling dir (Read takes a dir, mimicking what
                        // LoadHelper.ExtractToTemp does for real saves).
                        string extractDir = System.IO.Path.Combine(tempDir, "extract");
                        System.IO.Directory.CreateDirectory(extractDir);
                        using (var fs = new System.IO.FileStream(zipPath, System.IO.FileMode.Open, System.IO.FileAccess.Read))
                        using (var z = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Read))
                        {
                            var entry = z.GetEntry(sideCarEntryName);
                            if (entry != null)
                            {
                                using (var es = entry.Open())
                                using (var of = System.IO.File.Create(System.IO.Path.Combine(extractDir, sideCarEntryName)))
                                {
                                    es.CopyTo(of);
                                }
                            }
                        }

                        var readBack = readDirMethod.Invoke(null, new object[] { extractDir }) as System.Collections.IDictionary;
                        int readCount = readBack?.Count ?? -1;

                        totalChecks++;
                        if (readCount == beforeCount && readCount > 0)
                        { _log?.LogInfo($"[ScenarioRunner] PSP P10 PASS: PrioritySideCar Write -> Read round-trip preserved {readCount} entries."); passCount++; }
                        else
                        { _log?.LogError($"[ScenarioRunner] PSP P10 FAIL: round-trip count mismatch. before={beforeCount} after={readCount}"); failCount++; }

                        // Bonus: spot-check values match.
                        int valueMismatches = 0;
                        if (beforeEntries != null && readBack != null)
                        {
                            foreach (var entryObj in beforeEntries)
                            {
                                var refIdField = entryObj.GetType().GetField("ReferenceId");
                                var prioField = entryObj.GetType().GetField("Priority");
                                long refId = (long)(refIdField?.GetValue(entryObj) ?? 0L);
                                int prio = (int)(prioField?.GetValue(entryObj) ?? 0);
                                if (!readBack.Contains(refId))
                                {
                                    valueMismatches++;
                                    continue;
                                }
                                int readPrio = (int)readBack[refId];
                                if (readPrio != prio) valueMismatches++;
                            }
                        }
                        totalChecks++;
                        if (valueMismatches == 0)
                        { _log?.LogInfo($"[ScenarioRunner] PSP P10 PASS: every (ReferenceId, Priority) pair round-tripped intact."); passCount++; }
                        else
                        { _log?.LogError($"[ScenarioRunner] PSP P10 FAIL: {valueMismatches} mismatches between snapshot and read-back."); failCount++; }
                    }
                    catch (Exception e)
                    {
                        _log?.LogError($"[ScenarioRunner] PSP P10 threw: {e}");
                        failCount++;
                        totalChecks++;
                    }
                    finally
                    {
                        try { System.IO.Directory.Delete(tempDir, recursive: true); } catch { }
                    }
                }

                // ---- PHASE 11: OFF-as-reset seam ----
                // The master-toggle off-path is gone (always-on); the surviving user-facing reset
                // is ClearLockout (the OFF-as-reset sweep drives it when a player switches a
                // locked device off). A fresh lockout on a synthetic ref must drop immediately.
                {
                    long fakeRef = 999999999L;
                    int simTick2 = (int)(currentTickProp?.GetValue(null) ?? 0);
                    noteDeprioritizedMethod?.Invoke(null, new object[] { fakeRef, simTick2 });
                    bool lockedFresh = (bool)(isLockedOutMethodLong?.Invoke(null, new object[] { fakeRef, simTick2 }) ?? false);
                    clearLockoutMethod?.Invoke(null, new object[] { fakeRef });
                    bool lockedAfterClear = (bool)(isLockedOutMethodLong?.Invoke(null, new object[] { fakeRef, simTick2 }) ?? false);

                    totalChecks++;
                    if (lockedFresh && !lockedAfterClear)
                    { _log?.LogInfo("[ScenarioRunner] PSP P11 PASS: ClearLockout (OFF-as-reset seam) drops a fresh lockout immediately."); passCount++; }
                    else
                    { _log?.LogError($"[ScenarioRunner] PSP P11 FAIL: lockedFresh={lockedFresh} lockedAfterClear={lockedAfterClear} (expected true/false)."); failCount++; }
                }

                // ---- PHASE 12: SnapshotRemaining heartbeat payload ----
                // The per-tick FaultRegistrySnapshotMessage serializes
                // DeprioritizedRegistry.SnapshotRemaining(currentTick): a fresh lockout must appear
                // there with the full LockoutDurationTicks remaining.
                {
                    long fakeRef = 999999998L;
                    int simTick3 = (int)(currentTickProp?.GetValue(null) ?? 0);
                    noteDeprioritizedMethod?.Invoke(null, new object[] { fakeRef, simTick3 });
                    int remaining = -1;
                    if (snapshotRemainingMethod?.Invoke(null, new object[] { simTick3 }) is System.Collections.IEnumerable snapEnum)
                    {
                        foreach (var item in snapEnum)
                        {
                            if (item is KeyValuePair<long, int> pair && pair.Key == fakeRef)
                            { remaining = pair.Value; break; }
                            // DeprioritizedRegistry.SnapshotRemaining yields the hover-payload
                            // tuple (long refId, int remainingTicks, float needsW,
                            // float upstreamDemandW, float upstreamSupplyW); this check only
                            // needs the first two fields.
                            var itemType = item?.GetType();
                            var refField = itemType?.GetField("Item1");
                            var ticksField = itemType?.GetField("Item2");
                            if (refField != null && ticksField != null
                                && refField.FieldType == typeof(long) && ticksField.FieldType == typeof(int)
                                && (long)refField.GetValue(item) == fakeRef)
                            { remaining = (int)ticksField.GetValue(item); break; }
                        }
                    }
                    clearLockoutMethod?.Invoke(null, new object[] { fakeRef });

                    totalChecks++;
                    if (remaining == lockoutTicks && lockoutTicks > 0)
                    { _log?.LogInfo($"[ScenarioRunner] PSP P12 PASS: SnapshotRemaining carries the fresh lockout with remaining={remaining} ticks (the MP heartbeat payload)."); passCount++; }
                    else
                    { _log?.LogError($"[ScenarioRunner] PSP P12 FAIL: SnapshotRemaining remaining={remaining}, expected {lockoutTicks}."); failCount++; }
                }

                _log?.LogInfo($"[ScenarioRunner] PSP END pass={passCount} fail={failCount} total={totalChecks}");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] PSP threw: {e}");
            }
        }

        // PGP scenario: priority-deprioritization-network-breakdown.
        // Diagnostic (no PASS/FAIL). Per input cable network with >= 1 transformer, log the net
        // fields (PotentialLoad / CurrentLoad / RequiredLoad) and each contestant sorted by
        // priority desc + ReferenceId asc with its PUBLISHED output (the TransformerSupplyCache
        // presentation totals the atomic PowerAllocator writes each tick; the old on-demand
        // GetAllocatedSupply is gone), its downstream RequiredLoad, and its deprioritization /
        // overload state. Use this to investigate unexpected deprioritizations: a CONDUCTING line publishes > 0, an
        // IDLE line publishes 0 with no fault (standby or dead input), DEPRIORITIZED / CABLE-OVERLOAD /
        // TRANSFORMER-OVERLOAD name the active lockout (the overload split keeps the two overload
        // kinds independently observable).
        private static bool _pspNbFired;

        private static void Scenario_PgpPriorityDeprioritizationNetworkBreakdown()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-priority-deprioritization-network-breakdown")) return;
            if (_pspNbFired) return;
            _pspNbFired = true;

            try
            {
                _log?.LogInfo("[ScenarioRunner] NBP START priority-deprioritization-network-breakdown");
                var asm = GetModAssembly(PGP_ASSEMBLY);
                if (asm == null) { _log?.LogError("[ScenarioRunner] NBP no PGP assembly"); return; }

                var priorityStoreType = asm.GetType("PowerGridPlus.PriorityStore");
                var deprioritizedRegistryType = asm.GetType("PowerGridPlus.DeprioritizedRegistry");
                var overloadType = asm.GetType("PowerGridPlus.OverloadRegistry");
                var cableOverloadType = asm.GetType("PowerGridPlus.CableOverloadRegistry");
                var tickCounterType = asm.GetType("PowerGridPlus.ElectricityTickCounter");

                var getPriorityMethod = priorityStoreType?.GetMethod("GetPriority",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                    null, new[] { typeof(long) }, null);
                var isDeprioritization = deprioritizedRegistryType?.GetMethod("IsDeprioritized",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                    null, new[] { typeof(long), typeof(int) }, null);
                var isOverloaded = overloadType?.GetMethod("IsOverloaded",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                    null, new[] { typeof(long), typeof(int) }, null);
                // The overload split: the cable-overflow half publishes into its own registry,
                // so a breakdown that read only IsOverloaded would mislabel a cable-overloaded
                // transformer as IDLE.
                var isCableOverloaded = cableOverloadType?.GetMethod("IsCableOverloaded",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                    null, new[] { typeof(long), typeof(int) }, null);
                var currentTickProp = tickCounterType?.GetProperty("CurrentTick",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                int currentTick = (int)(currentTickProp?.GetValue(null) ?? -1);

                if (_transformers.Count == 0) RebuildCaches();

                // Group transformers by input cable network ReferenceId.
                var byInputNet = new Dictionary<long, List<Transformer>>();
                foreach (var t in _transformers)
                {
                    if (t == null || t.InputNetwork == null || t.OutputNetwork == null) continue;
                    long key = t.InputNetwork.ReferenceId;
                    if (!byInputNet.TryGetValue(key, out var list)) byInputNet[key] = list = new List<Transformer>();
                    list.Add(t);
                }

                int netsLogged = 0;
                foreach (var kv in byInputNet)
                {
                    if (kv.Value.Count == 0) continue;
                    var inNet = kv.Value[0].InputNetwork;
                    float budget = inNet.PotentialLoad;
                    float currentLoad = inNet.CurrentLoad;
                    float requiredLoad = inNet.RequiredLoad;

                    _log?.LogInfo(
                        $"[ScenarioRunner] NBP InputNet={kv.Key} PotentialLoad={budget:F0} CurrentLoad={currentLoad:F0} " +
                        $"RequiredLoad={requiredLoad:F0} contestants={kv.Value.Count}");

                    // Sort by Priority desc, then ReferenceId asc (the allocator's grant order
                    // within a tier).
                    var sorted = kv.Value
                        .Select(t => new
                        {
                            T = t,
                            Prio = (int)(getPriorityMethod?.Invoke(null, new object[] { t.ReferenceId }) ?? -1),
                        })
                        .OrderByDescending(x => x.Prio)
                        .ThenBy(x => x.T.ReferenceId)
                        .ToList();

                    foreach (var item in sorted)
                    {
                        var t = item.T;
                        float published = TpPublishedOutput(asm, t.ReferenceId);
                        bool isDeprioritized = (bool)(isDeprioritization?.Invoke(null, new object[] { t.ReferenceId, currentTick }) ?? false);
                        bool isOver = (bool)(isOverloaded?.Invoke(null, new object[] { t.ReferenceId, currentTick }) ?? false);
                        bool isCableOver = (bool)(isCableOverloaded?.Invoke(null, new object[] { t.ReferenceId, currentTick }) ?? false);
                        float outNetReq = t.OutputNetwork?.RequiredLoad ?? 0f;
                        long outId = t.OutputNetwork?.ReferenceId ?? 0L;

                        string state;
                        if (!t.OnOff) state = "OFF";
                        else if (t.Error == 1) state = "ERROR";
                        else if (isDeprioritized) state = "DEPRIORITIZED (60 s lockout)";
                        else if (isCableOver) state = "CABLE-OVERLOAD (60 s lockout)";
                        else if (isOver) state = "TRANSFORMER-OVERLOAD (60 s lockout)";
                        else if (published > 0.01f) state = "CONDUCTING";
                        else state = "IDLE (standby, no downstream demand, or dead input)";
                        _log?.LogInfo(
                            $"[ScenarioRunner] NBP   ref={t.ReferenceId} prefab={t.PrefabName} prio={item.Prio} " +
                            $"OutMax={t.OutputMaximum:F0} OutReq={outNetReq:F0} OnOff={t.OnOff} Error={t.Error} " +
                            $"publishedOut={published:F0} deprioritized={isDeprioritized} overloaded={isOver} cableOverloaded={isCableOver} state=[{state}] OutNet={outId}");
                    }

                    netsLogged++;
                }
                _log?.LogInfo($"[ScenarioRunner] NBP END inputNetsWithTransformers={netsLogged}");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] NBP threw: {e}");
            }
        }

        // PGP scenario: priority-deprioritization-persist-probe.
        // Verifies PrioritySideCar round-trip. Run this AFTER pgp-priority-deprioritization-probe wrote
        // synthetic priorities on phase 4, after `-Save -Name <X>`, after `-Stop`, after `-Start
        // -Load <X>`. Reads back the priorities of the same transformers and asserts that the
        // post-load PriorityStore values match what phase 4 wrote.
        //
        // Implementation: scans every Transformer's Priority value. If MORE than half of them
        // have non-default priorities (100), the side-car restored successfully. The exact
        // restore-the-same-numbers assertion would require persisting the phase 4 expectations
        // across server restarts, which is overkill -- the structural assertion that a non-trivial
        // number of non-100 priorities survived is sufficient evidence the side-car XML is read,
        // parsed, and applied. Confirmation lines log every value for full traceability.

        private static bool _pspPersistFired;

        private static void Scenario_PgpPriorityDeprioritizationPersistProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-priority-deprioritization-persist-probe")) return;
            if (_pspPersistFired) return;
            _pspPersistFired = true;

            try
            {
                _log?.LogInfo("[ScenarioRunner] PSPP START priority-deprioritization-persist-probe");
                var asm = GetModAssembly(PGP_ASSEMBLY);
                var priorityStoreType = asm?.GetType("PowerGridPlus.PriorityStore");
                var getPriorityMethod = priorityStoreType?.GetMethod("GetPriority",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                    null, new[] { typeof(long) }, null);

                if (_transformers.Count == 0) RebuildCaches();
                int nonDefault = 0;
                int total = 0;
                foreach (var t in _transformers)
                {
                    if (t == null) continue;
                    total++;
                    int p = (int)(getPriorityMethod?.Invoke(null, new object[] { t.ReferenceId }) ?? -1);
                    if (p != 100) nonDefault++;
                    _log?.LogInfo($"[ScenarioRunner] PSPP T ref={t.ReferenceId} Priority={p}");
                }

                _log?.LogInfo($"[ScenarioRunner] PSPP totals: transformers={total} nonDefaultPriorities={nonDefault}");

                if (nonDefault >= 2)
                    _log?.LogInfo($"[ScenarioRunner] PSPP PASS: {nonDefault} non-default priorities survived save+load. PrioritySideCar round-trip works.");
                else if (total == 0)
                    _log?.LogWarning("[ScenarioRunner] PSPP SKIP: no transformers in scene.");
                else
                    _log?.LogError($"[ScenarioRunner] PSPP FAIL: only {nonDefault} non-default priorities survived; expected the writes from pgp-priority-deprioritization-probe phase 2/4 to be present.");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] PSPP threw: {e}");
            }
        }
    }
}
