using Assets.Scripts;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     The false-edge block of the consumer Powered-ownership layer (PoweredOwnership).
    ///     Vanilla's only tick-driven un-power is ApplyState's else-branch, and both of its edges
    ///     funnel through the NON-VIRTUAL <c>Device.SetPowerFromThread</c> (decompile 371648; its
    ///     only vanilla callers are ApplyState's two edges at 271933 / 271938). Prefixing the
    ///     funnel therefore catches the false edge for EVERY class, including a third-party
    ///     subclass that overrides <c>AllowSetPower</c> (a per-override patch forest would miss
    ///     those). The ON edge always passes: powering on is permissive, and on a DEAD net it
    ///     cannot fire anyway (zeroed advertise means ConsumePower fails; the zero-Potential
    ///     corollary on Research/GameClasses/PowerTick.md).
    ///
    ///     <para>The block applies only to devices the ownership sweep drives: host side, layer
    ///     on, verdict published this tick, plain consumer (segmenters keep the healthy-set
    ///     policy and their own AllowSetPower postfixes), not exempt, not quarantined, on a
    ///     cabled network. A DEAD-verdict net lets the vanilla false through (it agrees with the
    ///     sweep); a LIVE or unclassified net blocks it (unclassified = freeze, so a mid-tick
    ///     cable split cannot cancel a print). Cable-less devices stay fully vanilla.</para>
    ///
    ///     <para>Skipping the original of an async <c>UniTaskVoid</c> method skips the state
    ///     machine entirely; the default return is inert and the caller's <c>.Forget()</c> is a
    ///     no-op on it.</para>
    /// </summary>
    [HarmonyPatch(typeof(Device), nameof(Device.SetPowerFromThread))]
    public static class DeviceSetPowerFromThreadBlockPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Device __instance, CableNetwork cableNetwork, bool hasPower)
        {
            if (hasPower) return true;                          // ON edge: always vanilla
            if (PoweredOwnership.ModWriteWindow) return true;   // the sweep's own writes
            if (!GameManager.RunSimulation) return true;
            if (!PoweredOwnership.OwnershipActiveNow()) return true;
            if (__instance is ElectricalInputOutput eio && SegmentingDeviceRegistry.IsSegmenter(eio))
                return true;                                    // healthy-set policy owns segmenters
            if (PoweredOwnership.IsQuarantined(__instance.ReferenceId)) return true;
            if (PoweredOwnership.IsExemptDevice(__instance)) return true;
            var ownNet = __instance.PowerCableNetwork;
            if (ownNet == null) return true;                    // cable-less: vanilla darkens it
            if (NetLiveness.TryGetVerdict(ownNet.ReferenceId, out byte verdict)
                && verdict != NetLiveness.Live)
                return true;                                    // DEAD: aligned with the sweep, let it land
            return false;                                       // LIVE or unclassified: block (freeze)
        }
    }

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
            if (!Settings.EnableDevicePoweredOwnership.Value
                || !Settings.DecouplePoweredFromOnOff.Value) return true;
            if (cableNetwork == null) return true;              // cable-loss darkening stays vanilla
            if (__instance is ElectricalInputOutput eio && SegmentingDeviceRegistry.IsSegmenter(eio))
                return true;
            if (PoweredOwnership.IsQuarantined(__instance.ReferenceId)) return true;
            if (PoweredOwnership.IsExemptDevice(__instance)) return true;
            return false;                                       // the sweep owns both edges
        }
    }
}
