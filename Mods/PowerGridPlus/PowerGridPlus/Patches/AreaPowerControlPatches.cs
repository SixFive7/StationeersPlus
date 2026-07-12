// Derived from Re-Volt by Sukasa (https://github.com/sukasa/revolt), MIT License (Copyright (c) 2025 Sukasa).
// Power Grid Plus adjustments (2026-05-28):
//   1. Removed the UsePower patch from earlier PGP versions. That patch dropped the vanilla
//      `_powerProvided > 0` drain gate and caused the APC's internal cell to net-drain whenever
//      downstream load >= BatteryChargeRate. Vanilla UsePower is correct; we now leave it alone.
//   2. Replaced the hard-coded vanilla BatteryChargeRate field reference with the server-authoritative
//      `Settings.ApcBatteryChargeRate` config (default 1000 W displayed).
//   3. Added a cable-headroom cap on charge: this APC's charge contribution is capped at the
//      remaining MaxVoltage on the input cable after the APC's own downstream pass-through is
//      subtracted. A single APC can never blow its input cable just by charging.
//   4. Added a cable cap on output: this APC's generated power cannot exceed the output cable's
//      MaxVoltage. A single APC can never supply more than its output cable physically carries.

using System.Collections.Concurrent;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using UnityEngine;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Closes the APC leak, applies the configured charge rate, and caps both charge demand and
    ///     output supply by the relevant cable's MaxVoltage so a single APC never blows the cable it
    ///     is wired to.
    /// </summary>
    [HarmonyPatch(typeof(AreaPowerControl))]
    public static class AreaPowerControlPatches
    {
        /// <summary>
        ///     Per-device charge cap: configured rate, further capped by the input cable's remaining
        ///     MaxVoltage after this APC's own downstream pass-through is subtracted. Returns 0 when
        ///     the cable is at or over MaxVoltage already from THIS APC's own contribution. The
        ///     pass-through figure is the allocator's own published total from the previous tick
        ///     (ApcPassthroughCache); the old vanilla-UsePower tracker died with ApplyState.
        /// </summary>
        internal static float ComputeChargeCap(AreaPowerControl apc)
        {
            float configCap = Settings.ApcBatteryChargeRate.Value;
            if (configCap <= 0f) return 0f;

            var inputCable = apc.InputConnection?.GetCable();
            if (inputCable == null) return configCap;
            // Runtime per-tier cap (CableMax), not the serialized per-instance MaxVoltage
            // (POWER.md §0.2 non-mutating decision).
            float maxVoltage = CableMax.For(inputCable);
            if (maxVoltage <= 0f) return 0f;

            // Per-APC pass-through: the allocator's published routed total for this APC. Falls
            // back to 0 when roster-absent (first tick after load, errored); that's the most
            // permissive case and self-corrects after one tick. Multi-APC output-network case:
            // each APC's entry carries only its own share, so the formula does not deadlock.
            ApcPassthroughCache.TryGet(apc.ReferenceId, out float passthrough);
            float cableSpare = maxVoltage - passthrough;
            if (cableSpare <= 0f) return 0f;

            return Mathf.Min(configCap, cableSpare);
        }

        /// <summary>
        ///     The APC's funded quiescent this tick: its idle UsedPower when the vanilla bill gate
        ///     holds (OnOff and an output network, decompile 391035) AND the allocator's published
        ///     totals fund it (a positive TransformerSupplyCache pull: idle healthy publishes
        ///     (0, quiescent), conducting publishes throughput + quiescent, inactive publishes
        ///     all-zero, roster-absent misses). Single source of truth for the GetUsedPower bill
        ///     and the ReceivePower delivery burn, so what is billed is exactly what is burned.
        /// </summary>
        internal static float FundedQuiescent(AreaPowerControl apc)
        {
            // Snapshot OnOff (SegControlSnapshot): billed == burned under one tick-coherent
            // verdict; a mid-tick toggle lands next tick. Live fallback for an un-enumerated APC.
            bool onOff = SegControlSnapshot.TryGet(apc.ReferenceId, out bool snapOn, out _)
                ? snapOn : apc.OnOff;
            if (!onOff || apc.OutputNetwork == null) return 0f;
            return TransformerSupplyCache.TryGetInputDraw(apc.ReferenceId, out var fundedPull) && fundedPull > 0f
                ? apc.UsedPower
                : 0f;
        }

        // The old ReceivePower delivery-alignment prefix is retired with vanilla ApplyState: the
        // write-back (Core/WriteBack) credits the cell exactly the granted share and records it
        // for the charge-delivery audit; no delivered stream exists to burn or split any more.

        [HarmonyPrefix, HarmonyPatch(nameof(AreaPowerControl.GetUsedPower))]
        public static bool GetUsedPowerPatch(CableNetwork cableNetwork, AreaPowerControl __instance, ref float __result)
        {
            __result = 0.0f;

            if (__instance.InputNetwork == null || cableNetwork != __instance.InputNetwork)
                return false;

            float usedPower = 0.0f;
            // Snapshot OnOff (SegControlSnapshot), matching FundedQuiescent's gate.
            bool onOff = SegControlSnapshot.TryGet(__instance.ReferenceId, out bool snapOn, out _)
                ? snapOn : __instance.OnOff;
            if (onOff)
            {
                if (__instance.OutputNetwork != null)
                {
                    // Quiescent idle draw. The OUTER gate is exactly vanilla's (OnOff &&
                    // OutputNetwork != null, 0.2.6403 decompile 391035): an output-less APC
                    // bills no idle draw. WITHIN it the quiescent follows the allocator's
                    // PUBLISHED totals rather than being billed unconditionally (FundedQuiescent:
                    // an enrolled APC publishes TotalPull >= quiescent whenever it was granted
                    // anything, idle healthy = (0, quiescent), conducting = throughput +
                    // quiescent), so a positive published pull means an upstream advertise funds
                    // the draw. All-zero totals are the inactive-contributor contract (shed /
                    // overloaded / cycle-locked): bill 0 like the transformer does, instead of a
                    // vanilla quiescent nobody funds (the shed-APC residual of the 160/150
                    // finding). A cache MISS (roster-absent: errored, short-circuited, just
                    // placed) also bills 0; vanilla would bill the quiescent there, a deliberate
                    // deviation consistent with the errored-transformer 0-bill. On an unmet net
                    // a partially funded quiescent-only pull still bills the full quiescent
                    // (vanilla-faithful honest darkness; such a net is never classified Served).
                    // The ReceivePower burn uses the same helper, so billed == burned.
                    usedPower += FundedQuiescent(__instance);

                    // Passthrough draw on the input network. Vanilla bills it from _powerProvided, the
                    // accumulator filled during the PREVIOUS tick's ApplyState, so it lags one tick. The
                    // allocator sizes upstream supply to the APC's CURRENT passthrough, so we bill the
                    // fresh figure here (total passthrough: rigid + soft-charge flow); otherwise the input
                    // network is short by the one-tick demand change (the net-503288 flicker). An APC not
                    // in this tick's roster caches no value and bills 0 passthrough until the next tick
                    // includes it.
                    if (ApcPassthroughCache.TryGet(__instance.ReferenceId, out var freshPassthrough))
                        usedPower += freshPassthrough;
                }

                if ((bool)__instance.Battery && !__instance.Battery.IsCharged)
                {
                    float chargeCap = ComputeChargeCap(__instance);
                    float chargePortion = Mathf.Min(chargeCap, __instance.Battery.PowerDelta);
                    // Soft charge (POWER.md §7.5): the allocator grants the APC's internal-cell charge
                    // a per-tick share; only the CHARGE portion caps to it, the passthrough stays as
                    // billed above. No fresh share means the APC is outside this tick's allocator
                    // roster (errored, short-circuited, missing a terminal, or the first tick after
                    // load), and its cell got no soft grant either, so bill 0 charge instead of an
                    // uncapped unmodelled draw: symmetric with the transformer / wireless
                    // "not in the roster -> report 0" convention.
                    if (chargePortion > 0f)
                        chargePortion = SoftDemandShareCache.TryGetShare(__instance.ReferenceId, out var share)
                            ? Mathf.Min(chargePortion, share)
                            : 0f;
                    usedPower += chargePortion;
                }
                else if (SoftDemandShareCache.TryGetShare(__instance.ReferenceId, out var mootShare)
                         && mootShare > 0f)
                {
                    // Fresh charge share, but the cell can take no charge: it crossed the
                    // Mode-based IsCharged (or was pulled) between the ALLOCATE grant and this
                    // bill. The bill correctly carries no charge; mark the grant moot for the
                    // charge-delivery audit so the legitimate fill-edge is not flagged.
                    ChargeDeliveryAudit.MarkChargeGateClosed(__instance.ReferenceId);
                }
            }

            __result = usedPower;
            return false;
        }

        // ------------------------------------------------------------------
        // Soft-power LogicTypes (POWERTODO 0.2.7.5): MaxChargeSpeed / MaxDischargeSpeed report the
        // configured caps; ChargeSpeed / DischargeSpeed report the live allocator shares (the same
        // cache values ALLOCATE writes; not latched). APC declares its own CanLogicRead /
        // GetLogicValue, so the patches attach per-class without the inherited-method trap.
        // ------------------------------------------------------------------

        [HarmonyPostfix, HarmonyPatch(nameof(AreaPowerControl.CanLogicRead))]
        public static void CanLogicRead_Postfix(Assets.Scripts.Objects.Motherboards.LogicType logicType, ref bool __result)
        {
            if (logicType == LogicTypeRegistry.MaxChargeSpeed
                || logicType == LogicTypeRegistry.MaxDischargeSpeed
                || logicType == LogicTypeRegistry.ChargeSpeed
                || logicType == LogicTypeRegistry.DischargeSpeed)
                __result = true;
        }

        [HarmonyPostfix, HarmonyPatch(nameof(AreaPowerControl.GetLogicValue))]
        public static void GetLogicValue_Postfix(AreaPowerControl __instance, Assets.Scripts.Objects.Motherboards.LogicType logicType, ref double __result)
        {
            if (logicType == LogicTypeRegistry.MaxChargeSpeed)
                __result = ComputeChargeCap(__instance);
            else if (logicType == LogicTypeRegistry.MaxDischargeSpeed)
                __result = Settings.ApcBatteryDischargeRate.Value;
            else if (logicType == LogicTypeRegistry.ChargeSpeed)
                __result = SoftDemandShareCache.GetActualOrZero(__instance.ReferenceId);
            else if (logicType == LogicTypeRegistry.DischargeSpeed)
            {
                // CELL-ONLY discharge rate, consistent with battery / umbilical DischargeSpeed (P9). The
                // allocator stamps the APC cell's elastic share into ApcCellDischargeCache, separate from
                // the bundled SoftSupplyShareCache entry that feeds the GetGeneratedPower surface.
                __result = ApcCellDischargeCache.GetActualOrZero(__instance.ReferenceId);
            }
        }

        /// <summary>
        ///     Cap APC output at the output cable's MaxVoltage. A single APC can never push more
        ///     than its output cable physically carries; if the cell + upstream potential together
        ///     could supply 50 kW but the cable is normal (5 kW), only 5 kW flows.
        /// </summary>
        [HarmonyPostfix, HarmonyPatch(nameof(AreaPowerControl.GetGeneratedPower))]
        public static void GetGeneratedPowerPatch(CableNetwork cableNetwork, AreaPowerControl __instance, ref float __result)
        {
            __result = DeviceOutputSanitizer.Sanitize(__result, __instance, generated: true);
            if (__instance.OutputNetwork == null || cableNetwork != __instance.OutputNetwork) return;
            if (__result <= 0f) return;

            var outputCable = __instance.OutputConnection?.GetCable();
            if (outputCable == null) return;
            float maxVoltage = CableMax.For(outputCable);
            if (maxVoltage <= 0f) { __result = 0f; return; }

            if (__result > maxVoltage)
                __result = maxVoltage;

            // Elastic supply (POWER.md §7.3.0.1): vanilla AvailablePower bundles passthrough + cell,
            // so the allocator writes the APC's share as total committed passthrough (rigid + soft) +
            // the cell's elastic discharge share. Stale share = float.MaxValue (vanilla fallback).
            __result = Mathf.Min(__result, SoftSupplyShareCache.GetShare(__instance.ReferenceId));
        }
    }
}
