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
    internal static class Dispatcher
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

                case "battery-charge-snapshot":
                    Scenario_BatteryChargeSnapshot();
                    return;

                case "power-prefab-dump":
                    Scenario_PowerPrefabDump();
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

                case "pgp-tooltip-filter-probe":
                    Scenario_PgpTooltipFilterProbe();
                    return;

                case "pgp-rate-cap-probe":
                    Scenario_PgpRateCapProbe();
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
                // decompile line 138702 in `namespace Objects`). Don't search by bare-name — use
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

        // PGP scenario: battery-efficiency-probe.
        // One-shot. Directly invokes Battery.ReceivePower(InputNetwork, powerAdded)
        // against the first OnOff Battery with headroom, logs PowerStored before /
        // after each call. Used to verify PGP's
        //   StationaryBatteryPatches.ChargeEfficiencyControl
        // math:
        //   charged = BatteryChargeEfficiency * powerAdded
        //   if (charged < 500) charged = powerAdded     // sub-500 W trickle floor
        //   PowerStored += charged (clamped)
        // Run twice across two server starts, once at BatteryChargeEfficiency = 1.0
        // and once at 0.5; the two log lines compared confirm the math.

        private static bool _bepProbeFired;

        private static void Scenario_PgpBatteryEfficiencyProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-battery-efficiency-probe")) return;
            if (_bepProbeFired) return;
            _bepProbeFired = true;

            if (_batteries.Count == 0) RebuildCaches();
            Battery target = null;
            foreach (var b in _batteries)
            {
                if (b != null && b.OnOff && b.InputNetwork != null && b.PowerStored + 5000f <= b.PowerMaximum)
                {
                    target = b;
                    break;
                }
            }
            if (target == null)
            {
                _log?.LogWarning("[ScenarioRunner] pgp-battery-efficiency-probe: no OnOff Battery with headroom + InputNetwork; nothing to probe.");
                return;
            }

            _log?.LogInfo(
                $"[ScenarioRunner] BEP target: ref={target.ReferenceId} prefab={target.PrefabName} " +
                $"PowerStored={target.PowerStored:F2} PowerMaximum={target.PowerMaximum:F2} " +
                $"BatteryChargeEfficiency_setting={PgpBatteryChargeEfficiency()}");

            // Probe A: large powerAdded above the 500 W trickle floor.
            //   Expected at eff=1.0: delta = 5000.  At eff=0.5: delta = 2500.
            var beforeA = target.PowerStored;
            target.ReceivePower(target.InputNetwork, 5000f);
            var afterA = target.PowerStored;
            _log?.LogInfo(
                $"[ScenarioRunner] BEP A: powerAdded=5000 before={beforeA:F2} after={afterA:F2} delta={afterA - beforeA:F2}");

            // Probe B: small powerAdded below the 500 W trickle floor.
            //   Expected at any eff: delta = 200 (trickle floor: charged = powerAdded).
            var beforeB = target.PowerStored;
            target.ReceivePower(target.InputNetwork, 200f);
            var afterB = target.PowerStored;
            _log?.LogInfo(
                $"[ScenarioRunner] BEP B: powerAdded=200  before={beforeB:F2} after={afterB:F2} delta={afterB - beforeB:F2}");
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
        // (where normal cables would naturally burn). One-shot at tick (Delay+25):
        // reflect-invoke PowerGridPlus.Power.PowerGridTick.TestBurnCable(10000,10000)
        // against every network's PowerTick and tally would-burn counts by tier.
        // Verifies PGP's burn-decision formula plus the NEW-1 super-heavy carve-out
        // without needing a real overload.

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
                var tickType = asm.GetType("PowerGridPlus.Power.PowerGridTick");
                if (tickType == null) { _log?.LogWarning("[ScenarioRunner] CBP reflection probe: PowerGridPlus.Power.PowerGridTick type not found."); return; }
                var testBurnCable = tickType.GetMethod("TestBurnCable",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                if (testBurnCable == null) { _log?.LogWarning("[ScenarioRunner] CBP reflection probe: TestBurnCable method not found."); return; }

                int probedNets = 0;
                int wouldBurn = 0;
                int normalCableNets = 0;
                int heavyCableNets = 0;
                int superHeavyCableNets = 0;

                CableNetwork.AllCableNetworks.ForEach(n =>
                {
                    if (n == null) return;
                    var pt = n.PowerTick;
                    if (pt == null || pt.GetType() != tickType) return;
                    probedNets++;

                    float minMaxVoltage = float.PositiveInfinity;
                    if (n.CableList != null)
                    {
                        foreach (var c in n.CableList)
                        {
                            if (c == null) continue;
                            if (c.MaxVoltage < minMaxVoltage) minMaxVoltage = c.MaxVoltage;
                        }
                    }
                    if (minMaxVoltage == 5000f) normalCableNets++;
                    else if (minMaxVoltage == 100000f) heavyCableNets++;
                    else if (minMaxVoltage == 500000f) superHeavyCableNets++;

                    var result = testBurnCable.Invoke(pt, new object[] { 10000.0f, 10000.0f });
                    if (result != null) wouldBurn++;
                });

                _log?.LogInfo(
                    $"[ScenarioRunner] CBP reflection probe: probedNets={probedNets} " +
                    $"wouldBurn={wouldBurn} normalNets={normalCableNets} heavyNets={heavyCableNets} " +
                    $"superHeavyNets={superHeavyCableNets} (synthetic powerUsed=10000 W against TestBurnCable)");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] CBP reflection probe threw: {e.Message}");
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
    }
}
