// Emergency-light behaviour for Stationeers Wall Light Battery devices.
//
// Inspired by the Battery Backup Light mod by alliephante (MIT License, Copyright (c) 2025 alliephante):
// https://github.com/alliephante/StationeersEmergencyBatteryLight
//
// The upstream mod's transpiler removes the vanilla per-tick WallLightBattery.CheckPowerState() call
// from WallLightBattery.OnPowerTick. On the host that removal turns an otherwise benign two-writer
// interaction into a visible ~1.5 s on/off flicker on lit emergency lights: every power tick the cable
// network's tick writes Powered=false on a cable-power-starved device, and the only thing that turns
// it back on is the reactive Interactable cascade through CheckPowerState, which lands on a different
// thread/frame phase and beats out of phase with the off-write. The flicker is purely host-side;
// connected clients have RunSimulation=false and never run OnServer.Interact, so the ping-pong does
// not surface as a visible flicker on the client.
//
// This reimplementation runs as a HarmonyPostfix on WallLightBattery.OnPowerTick, AFTER the vanilla
// CheckPowerState call. The vanilla per-tick Powered re-assert is preserved, so a cell-powered emergency
// light stays stably lit while its cable network is dead. The OnOff toggle logic (Mode 0, three-tick
// shortfall latch, OnServer.Interact(InteractOnOff, ...)) matches the upstream behaviour; the latch is
// held in an in-memory dictionary keyed by Thing.ReferenceId rather than via extra interactables, since
// it does not need to persist across saves or sync to clients (the host computes a fresh latch from the
// live network state within a few ticks of any startup).

using System;
using System.Collections.Concurrent;
using System.Linq;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Structures;
using HarmonyLib;

// WallLightBattery.WasPoweredByCableLastTick is private (game v0.2.6228.27061); bind once via
// Harmony's AccessTools so the call sites stay readable.

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Shared helpers for the emergency-light patches: the two-tick shortfall latch and the
    ///     conflict-detection gate that lets a user's existing Battery Backup Light install keep
    ///     working when both mods are present.
    /// </summary>
    internal static class EmergencyLightSupport
    {
        // Two-tick shortfall latch keyed by Thing.ReferenceId. Transient; the latch rebuilds itself
        // from the live network state within three ticks of any startup. Value is
        // (this-tick-shortfall, last-tick-shortfall), shifted at the end of every tick.
        internal static readonly ConcurrentDictionary<long, (bool prev, bool prev2)> ShortfallLatch =
            new ConcurrentDictionary<long, (bool, bool)>();

        // Configured emergency-light prefab names (Settings.EmergencyLightPrefabs, comma-separated,
        // default StructureWallLightBattery). Parsed once; settings are immutable mid-session.
        private static System.Collections.Generic.HashSet<string> _prefabNames;

        internal static System.Collections.Generic.HashSet<string> PrefabNames
        {
            get
            {
                if (_prefabNames == null)
                {
                    var raw = Settings.EmergencyLightPrefabs?.Value ?? "StructureWallLightBattery";
                    _prefabNames = new System.Collections.Generic.HashSet<string>(
                        raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()),
                        StringComparer.OrdinalIgnoreCase);
                }
                return _prefabNames;
            }
        }

        // Fast accessor for WallLightBattery.WasPoweredByCableLastTick (private property:
        // _lastPoweredByCableOnTick >= GameManager.GameTickCount). Bound once at class load.
        internal static readonly Func<WallLightBattery, bool> WasPoweredByCableLastTick =
            AccessTools.MethodDelegate<Func<WallLightBattery, bool>>(
                AccessTools.PropertyGetter(typeof(WallLightBattery), "WasPoweredByCableLastTick"));

        // Type name of the third-party Battery Backup Light mod's plugin class. We scan the
        // AppDomain for this type rather than querying BepInEx Chainloader.PluginInfos, because
        // StationeersLaunchPad-loaded mods are not registered with BepInEx Chainloader at all,
        // so the PluginInfos dictionary does not contain them. Verified empirically on game
        // v0.2.6228.27061: SLP loads Workshop_*/Local_* mods through its own mechanism and only
        // BepInEx-bootstrap-loaded plugins (StationeersLaunchPad itself, NetworkBufferFix,
        // InspectorPlus, Power Grid Plus) show up in PluginInfos.
        internal const string UpstreamPluginTypeName = "BatteryLight.Scripts.BatteryLightPlugin";

        private static bool _upstreamCheckDone;
        private static bool _upstreamLoaded;

        // Returns true when the third-party Battery Backup Light mod is NOT loaded, i.e. when it is
        // safe for our patches to drive the emergency-light behaviour. Cached after first call.
        internal static bool UpstreamMissing()
        {
            if (_upstreamCheckDone) return !_upstreamLoaded;

            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (asm.GetType(UpstreamPluginTypeName, throwOnError: false) != null)
                        {
                            _upstreamLoaded = true;
                            break;
                        }
                    }
                    catch { /* unreflectable assembly, skip */ }
                }
            }
            catch { _upstreamLoaded = false; }
            _upstreamCheckDone = true;

            if (_upstreamLoaded)
            {
                Plugin.Log.LogInfo(
                    "Battery Backup Light (third-party, " + UpstreamPluginTypeName + ") detected; " +
                    "Power Grid Plus emergency-light patches are inactive. Uninstall that mod to switch " +
                    "to the Power Grid Plus flicker-free implementation.");
            }
            return !_upstreamLoaded;
        }
    }

    /// <summary>
    ///     Adds a Mode interactable to the StructureWallLightBattery source prefab so a player can opt
    ///     individual lights out of emergency-light behaviour (Mode 0 = emergency backup, Mode 1 = plain
    ///     wall light that may also draw from its internal cell). Idempotent: skipped when the prefab
    ///     already has a Mode interactable, which covers the migration case from a save that was
    ///     previously running the third-party Battery Backup Light mod.
    /// </summary>
    [HarmonyPatch(typeof(Prefab), "LoadAll")]
    public static class WallLightBatteryPrefabPatch
    {
        [HarmonyPrefix]
        public static void AddModeInteractable()
        {
            if (!Settings.EnableEmergencyLights.Value) return;
            if (!EmergencyLightSupport.UpstreamMissing()) return;

            try
            {
                // Every configured prefab (Settings.EmergencyLightPrefabs) gets the Mode
                // interactable. Entries must be WallLightBattery-class prefabs: the per-tick toggle
                // below patches WallLightBattery.OnPowerTick, so a configured name whose prefab is a
                // different class gets the Mode knob but no emergency behaviour (logged).
                var wanted = new System.Collections.Generic.HashSet<string>(
                    EmergencyLightSupport.PrefabNames, StringComparer.OrdinalIgnoreCase);
                foreach (var thing in WorldManager.Instance.SourcePrefabs)
                {
                    if (thing == null || string.IsNullOrEmpty(thing.PrefabName)) continue;
                    if (!wanted.Remove(thing.PrefabName)) continue;

                    if (!(thing is WallLightBattery))
                    {
                        Plugin.Log.LogWarning($"Emergency Light Prefabs entry '{thing.PrefabName}' is not a WallLightBattery-class light; the emergency toggle will not drive it.");
                        continue;
                    }
                    if (thing.Interactables.Any(i => i != null && i.Action == InteractableType.Mode))
                        continue;

                    var mode = new Interactable
                    {
                        Action = InteractableType.Mode,
                        ActionName = "Mode",
                        JoinInProgressSync = true,
                        Parent = thing,
                    };
                    thing.Interactables.Add(mode);
                    Plugin.Log.LogInfo($"Added Mode interactable to {thing.PrefabName} for emergency-light behaviour.");
                }
                foreach (var missing in wanted)
                    Plugin.Log.LogWarning($"Emergency Light Prefabs entry '{missing}' not found among prefabs; skipped.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Failed to add emergency-light Mode interactable(s): " + e);
            }
        }
    }

    /// <summary>
    ///     Per-tick emergency-light toggle. Runs as a postfix after vanilla WallLightBattery.OnPowerTick
    ///     so the vanilla CheckPowerState re-assert is preserved and a cell-powered emergency light
    ///     stays stably lit. The Mode 0 / shortfall-latch logic matches the upstream Battery Backup
    ///     Light behaviour, minus the flicker.
    ///
    ///     <para>The decision is computed here (device tick) but the OnOff write is QUEUED and issued
    ///     at the start of the NEXT atomic tick, pre-OBSERVE (see
    ///     <see cref="EmergencyLightToggleQueue"/>): an interact fired mid-tick from the worker lands
    ///     on the main thread at an arbitrary later frame, which can flip the light's draw between
    ///     ALLOCATE's read and ENFORCE's re-read of the same tick and desync vanilla's Required from
    ///     the allocator's grants (a transition-clustered partial-power dip). Only the WHEN of the
    ///     write moves; the latch semantics are unchanged.</para>
    /// </summary>
    [HarmonyPatch(typeof(WallLightBattery), "OnPowerTick")]
    public static class WallLightBatteryEmergencyTickPatch
    {
        [HarmonyPostfix]
        public static void EmergencyToggleOnOff(WallLightBattery __instance)
        {
            if (!Settings.EnableEmergencyLights.Value) return;
            if (!EmergencyLightSupport.UpstreamMissing()) return;
            if (__instance == null || __instance.Mode != 0) return;
            // Only prefabs on the configured list (Settings.EmergencyLightPrefabs) get the
            // emergency behaviour; other WallLightBattery-class prefabs stay vanilla.
            if (!EmergencyLightSupport.PrefabNames.Contains(__instance.PrefabName)) return;
            if (!__instance.BatterySlot.Contains<BatteryCell>(out _)) return;

            var refId = ((Thing)__instance).ReferenceId;
            EmergencyLightSupport.ShortfallLatch.TryGetValue(refId, out var stored);
            bool prevShortfall = stored.prev;
            bool prevPrevShortfall = stored.prev2;

            bool hasCable = __instance.PowerCableNetwork != null;
            bool shortfall = hasCable && __instance.PowerCableNetwork.RequiredLoad > __instance.PowerCableNetwork.PotentialLoad;
            bool gridFeedingNow = EmergencyLightSupport.WasPoweredByCableLastTick(__instance);

            if (__instance.OnOff && gridFeedingNow && hasCable && !shortfall && !prevShortfall && !prevPrevShortfall)
            {
                // Grid is feeding the light and has been short-free for three consecutive ticks. Park
                // the backup light off; vanilla power flow charges its internal cell.
                EmergencyLightToggleQueue.Enqueue(__instance, 0);
            }
            else if (!__instance.OnOff && !gridFeedingNow && (shortfall || prevShortfall || prevPrevShortfall || !hasCable))
            {
                // Cable stopped feeding the light AND the network is short (now or in the last two
                // ticks) or the cable is gone entirely. Turn the backup light on; the cell powers
                // the lamp until grid comes back or the cell empties.
                EmergencyLightToggleQueue.Enqueue(__instance, 1);
            }

            EmergencyLightSupport.ShortfallLatch[refId] = (shortfall, prevShortfall);
        }
    }

    /// <summary>
    ///     Deferred OnOff writes for the emergency-light toggle: decisions enqueue during the device
    ///     tick and drain at the start of the NEXT atomic tick, pre-OBSERVE
    ///     (AtomicElectricityTickPatch), so the flip is issued at a fixed tick-boundary phase instead
    ///     of mid-tick. One pending entry per light (last decision wins), keyed by ReferenceId.
    ///
    ///     <para>Threading: enqueue (device tick) and drain (next tick's prefix) both run on the
    ///     power worker, serialized by the tick; a ConcurrentDictionary keeps the map safe across
    ///     pool-thread handoffs, matching <see cref="EmergencyLightSupport.ShortfallLatch"/>. The
    ///     drain still goes through <c>OnServer.Interact</c>, which from a worker thread enqueues
    ///     into vanilla's <c>Interactable.QueuedInteractions</c> for the main thread's next
    ///     <c>GameManager.Update</c> (0.2.6403 decompile 39696-39703, 205169), so the state write
    ///     itself lands within one main-thread frame of the tick boundary; the one-tick deferral
    ///     plus fixed-phase issue is what keeps it out of the deciding tick entirely.</para>
    /// </summary>
    internal static class EmergencyLightToggleQueue
    {
        private static readonly ConcurrentDictionary<long, (WallLightBattery light, int state)> _pending =
            new ConcurrentDictionary<long, (WallLightBattery, int)>();

        internal static void Enqueue(WallLightBattery light, int state)
        {
            if (light == null) return;
            _pending[((Thing)light).ReferenceId] = (light, state);
        }

        /// <summary>Issue all pending toggles. Called pre-OBSERVE by the atomic tick prefix.</summary>
        internal static void Drain()
        {
            if (_pending.IsEmpty) return;
            foreach (var pair in _pending)
            {
                var (light, state) = pair.Value;
                if (light == null || light.IsBeingDestroyed || light.InteractOnOff == null) continue;
                if (light.OnOff == (state == 1)) continue;   // self-resolved since the decision; nothing to write
                OnServer.Interact(light.InteractOnOff, state, true);
            }
            _pending.Clear();
        }

        /// <summary>World-load reset: drop pending toggles that reference the previous world's lights.</summary>
        internal static void Clear()
        {
            _pending.Clear();
        }
    }
}
