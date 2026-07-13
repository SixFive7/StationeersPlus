using Assets.Scripts;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Serves the IC10 <c>Power</c> logic value from the net-liveness verdict instead of the
    ///     Powered flag. The flag follows the vanilla coupling (verdict AND structure complete AND
    ///     OnOff, PoweredOwnership), so hover / UI show a switched-off device as unpowered; scripts
    ///     keep reading Power as "this device's power network is energized", the value they saw
    ///     under the decoupled build. 1 = the device's power cable network is verdict-LIVE
    ///     (NetLiveness), 0 = dead or unclassified. Read On for the switch state.
    ///
    ///     <para>Scope: only devices the ownership sweep owns; every skip below leaves the vanilla
    ///     read (<c>Powered ? 1 : 0</c>) in place. Skipped: the non-simulating side (a client never
    ///     publishes verdicts; its flag is synced from the host), cable-less devices (no net, no
    ///     verdict), incomplete structures (the base method zeroes every logic read there),
    ///     no-power-state devices (the sweep never writes them), segmenters per
    ///     SegmentingDeviceRegistry (Transformer, Battery, AreaPowerControl, PowerTransmitter /
    ///     PowerReceiver, the umbilical halves: the presentation roster owns them),
    ///     PoweredOwnership-exempt classes and emergency-light prefabs (self-owned Powered), and
    ///     quarantined devices (given back to vanilla wholesale).</para>
    ///
    ///     <para>Targets the base-declared virtual on <see cref="Device"/>: attribute-targeting a
    ///     subclass that only inherits the method throws "Undefined target method" at PatchAll
    ///     time (the InheritedLogicablePassthroughLogicPatches trap), and the fabricator family
    ///     and every other plain consumer dispatch through this base implementation anyway.
    ///     Threading: GetLogicValue runs inside CircuitHolders.Execute in the atomic tick (power
    ///     worker) and from main-thread UI readers; NetLiveness.TryGetVerdict reads the
    ///     volatile-published verdict map, which is never mutated after publish, so both sides are
    ///     safe. Between publishes the read serves the previous tick's verdict, the same cadence
    ///     the flag itself updates on; no live freshness gate, per the single-boundary-read
    ///     mandate.</para>
    /// </summary>
    [HarmonyPatch(typeof(Device))]
    public static class PowerLogicReadPatches
    {
        [HarmonyPostfix, HarmonyPatch(nameof(Device.GetLogicValue), new[] { typeof(LogicType) })]
        public static void PowerReadsNetLiveness(Device __instance, LogicType logicType, ref double __result)
        {
            if (logicType != LogicType.Power) return;
            if (!GameManager.RunSimulation) return;
            var net = __instance.PowerCableNetwork;
            if (net == null) return;                            // cable-less stays vanilla
            if (!__instance.IsStructureCompleted) return;       // base already returned 0 for everything
            if (!__instance.HasPowerState) return;              // the sweep never owns these
            if (__instance is ElectricalInputOutput eio && SegmentingDeviceRegistry.IsSegmenter(eio))
                return;
            if (PoweredOwnership.IsExemptDevice(__instance)) return;
            var emergencyPrefabs = EmergencyLightSupport.PrefabNames;
            if (emergencyPrefabs != null && emergencyPrefabs.Contains(__instance.PrefabName)) return;
            if (PoweredOwnership.IsQuarantined(__instance.ReferenceId)) return;

            __result = NetLiveness.TryGetVerdict(net.ReferenceId, out byte verdict)
                       && verdict == NetLiveness.Live ? 1.0 : 0.0;
        }
    }
}
