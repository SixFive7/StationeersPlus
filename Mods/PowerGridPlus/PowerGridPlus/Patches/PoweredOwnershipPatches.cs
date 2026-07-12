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
    ///     toggles, wiring changes) for owned devices while the OnOff-orthogonality mode is on.
    ///     Under orthogonality a toggle-OFF must NOT drop Powered (powered-but-off is a valid
    ///     state); without this prefix the toggle writes false instantly and the sweep re-raises
    ///     it up to half a second later, a guaranteed per-toggle flicker. The sweep owns both
    ///     edges within the tick instead.
    ///
    ///     <para>A null network passes through: <c>AssessPower(null, ...)</c> is how vanilla
    ///     darkens a device that lost its last power cable, and a cable-less device never reaches
    ///     the sweep. In compat mode (orthogonality off) vanilla is untouched: its OnOff-false
    ///     edge matches the compat expectation exactly. Class overrides of AssessPower (Bench,
    ///     UnPoweredDoor, the landing pads, WallLightBattery) are not dispatched through this
    ///     base-method patch, which is correct: those classes are exempt or handle their own
    ///     propagation.</para>
    /// </summary>
    [HarmonyPatch(typeof(Device), "AssessPower")]
    public static class DeviceAssessPowerSuppressPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Device __instance, CableNetwork cableNetwork, bool isOn)
        {
            if (!GameManager.RunSimulation) return true;
            if (!Settings.DecouplePoweredFromOnOff.Value) return true;
            if (cableNetwork == null) return true;              // cable-loss darkening stays vanilla
            if (__instance is ElectricalInputOutput eio && SegmentingDeviceRegistry.IsSegmenter(eio))
                return true;
            if (PoweredOwnership.IsQuarantined(__instance.ReferenceId)) return true;
            if (PoweredOwnership.IsExemptDevice(__instance)) return true;
            return false;                                       // the sweep owns both edges
        }
    }
}
