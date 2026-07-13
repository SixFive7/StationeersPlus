using Assets.Scripts;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    // The old DeviceSetPowerFromThreadBlockPatch is retired with vanilla ApplyState: the funnel's
    // only vanilla callers were ApplyState's two edges (decompile 271933 / 271938), and the atomic
    // tick never calls ApplyState any more. The ownership sweep and the presentation reconcile are
    // now the ONLY SetPowerFromThread callers, so there is no false edge left to block (POWER.md
    // §0 decision 24 stage 3).

    /// <summary>
    ///     Suppresses vanilla's event-driven Powered writes (<c>Device.AssessPower</c>: OnOff
    ///     toggles, wiring changes) for owned devices, keeping the ownership sweep the single
    ///     Powered writer. Powered follows the snapshot coupling (verdict AND structure AND
    ///     OnOff), so a toggle edge lands within one tick (up to half a second): the NEXT tick's
    ///     snapshot carries the new OnOff and the sweep asserts the edge. That latency is the
    ///     deliberate race-free single-writer trade; letting this vanilla write through, or
    ///     adding a live-OnOff freshness guard to the sweep, would be a second boundary read
    ///     acting mid-tick, exactly what the snapshot pipeline exists to eliminate.
    ///
    ///     <para>A null network passes through: <c>AssessPower(null, ...)</c> is how vanilla
    ///     darkens a device that lost its last power cable, and a cable-less device never reaches
    ///     the sweep. Class overrides of AssessPower (Bench, UnPoweredDoor, the landing pads,
    ///     WallLightBattery) are not dispatched through this base-method patch, which is correct:
    ///     those classes are exempt or handle their own propagation.</para>
    /// </summary>
    [HarmonyPatch(typeof(Device), "AssessPower")]
    public static class DeviceAssessPowerSuppressPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Device __instance, CableNetwork cableNetwork, bool isOn)
        {
            if (!GameManager.RunSimulation) return true;
            if (cableNetwork == null) return true;              // cable-loss darkening stays vanilla
            if (__instance is ElectricalInputOutput eio && SegmentingDeviceRegistry.IsSegmenter(eio))
                return true;
            if (PoweredOwnership.IsQuarantined(__instance.ReferenceId)) return true;
            if (PoweredOwnership.IsExemptDevice(__instance)) return true;
            return false;                                       // the sweep owns both edges
        }
    }
}
