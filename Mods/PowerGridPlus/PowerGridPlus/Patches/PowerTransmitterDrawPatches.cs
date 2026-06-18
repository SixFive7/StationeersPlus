using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Fresh input-draw reporting for the wireless PowerTransmitter, the analog of the transformer
    ///     and APC fixes.
    ///
    ///     <para>Vanilla <c>PowerTransmitter.GetUsedPower(InputNetwork)</c> bills
    ///     <c>min(MaxPowerTransmission, _powerProvided)</c>, where <c>_powerProvided</c> is the
    ///     one-tick-lagging downstream-consumption accumulator (filled during the PREVIOUS tick's
    ///     ApplyState; see Research/GameClasses/Device.md). The allocator sizes the input network's
    ///     supply to the PT/PR pair's CURRENT pull (delivered throughput * the PowerTransmitterPlus
    ///     distance multiplier + the pair's quiescent), so billing the stale <c>_powerProvided</c>
    ///     leaves the input network short by the one-tick demand change -- the same flicker the APC had
    ///     on net 503288. This prefix reports the fresh pull the allocator cached in
    ///     <see cref="TransformerSupplyCache"/> (keyed by the transmitter's ReferenceId, which is the
    ///     PT-pair seg's RefId). An inactive pair (shed / overloaded / cycle-faulted) caches a pull of 0,
    ///     so it draws nothing.</para>
    ///
    ///     <para>Only the transmitter bills the pair's input cable draw; a pure receiver's
    ///     <c>InputNetwork</c> is null, so its <c>GetUsedPower</c> already returns 0 (no patch needed).
    ///     The cycle-fault enforcement postfix (CycleFaultEnforcementPatches) still runs after this and
    ///     zeros a cycle-faulted transmitter, which is consistent (its cached pull is already 0).</para>
    /// </summary>
    [HarmonyPatch(typeof(PowerTransmitter))]
    public static class PowerTransmitterDrawPatches
    {
        [HarmonyPrefix, HarmonyPatch(nameof(PowerTransmitter.GetUsedPower))]
        public static bool GetUsedPowerPatch(CableNetwork cableNetwork, PowerTransmitter __instance, ref float __result)
        {
            if (__instance.InputNetwork == null || cableNetwork != __instance.InputNetwork)
                return true;   // not the input cable network: let vanilla return 0
            if (!__instance.OnOff || __instance.Error == 1)
                return true;   // off / error: vanilla handles it

            if (TransformerSupplyCache.TryGetInputDraw(__instance.ReferenceId, out var fresh))
            {
                __result = fresh < 0f ? 0f : fresh;
                return false;
            }
            return true;   // no fresh value this tick (unlinked / not in roster): vanilla
        }
    }
}
