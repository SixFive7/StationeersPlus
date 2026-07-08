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
        ///     Per-APC pass-through magnitude tracked from the previous power tick's UsePower call.
        ///     Vanilla UsePower(Output, powerUsed) supplies the exact downstream draw routed through
        ///     this APC last tick -- the value we need to subtract from input-cable MaxVoltage when
        ///     computing the charge headroom. CurrentLoad on the output network is wrong here
        ///     because it includes contributions from sibling suppliers on the same output bus.
        ///     ReferenceId keys the entry (stable across save/load, never reused); the dictionary
        ///     entry leaks if an APC is destroyed mid-session, but the leak is bounded by APC count.
        /// </summary>
        private static readonly ConcurrentDictionary<long, float> _lastPassthrough = new ConcurrentDictionary<long, float>();

        /// <summary>
        ///     Per-device charge cap: configured rate, further capped by the input cable's remaining
        ///     MaxVoltage after this APC's own downstream pass-through is subtracted. Returns 0 when
        ///     the cable is at or over MaxVoltage already from THIS APC's own contribution.
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

            // Per-APC pass-through: last tick's powerUsed routed through THIS APC. Falls back to 0
            // on the first tick after world load (no entry yet); that's the most permissive case
            // and self-corrects after one tick. Multi-APC output-network case: each APC tracks
            // only its own share, so the formula does not deadlock.
            float passthrough = 0f;
            _lastPassthrough.TryGetValue(apc.ReferenceId, out passthrough);
            float cableSpare = maxVoltage - passthrough;
            if (cableSpare <= 0f) return 0f;

            return Mathf.Min(configCap, cableSpare);
        }

        /// <summary>
        ///     Track per-APC downstream pass-through every tick. Vanilla UsePower(OutputNetwork, powerUsed)
        ///     is the canonical "downstream pulled this much from this APC this tick" signal.
        /// </summary>
        [HarmonyPostfix, HarmonyPatch(nameof(AreaPowerControl.UsePower))]
        public static void TrackPassthroughPatch(AreaPowerControl __instance, CableNetwork cableNetwork, float powerUsed)
        {
            if (__instance == null) return;
            if (__instance.OutputNetwork == null || cableNetwork != __instance.OutputNetwork) return;
            _lastPassthrough[__instance.ReferenceId] = powerUsed;
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
            if (!apc.OnOff || apc.OutputNetwork == null) return 0f;
            return TransformerSupplyCache.TryGetInputDraw(apc.ReferenceId, out var fundedPull) && fundedPull > 0f
                ? apc.UsedPower
                : 0f;
        }

        // Delivery-side alignment (the charge-delivery audit's contract: credit == grant on a
        // served network). Vanilla ApplyState delivers a device's bill in one ReceivePower call
        // PER CONTRIBUTING PROVIDER (PowerTick.ConsumePower), so every per-call adjustment here
        // rides a per-tick allowance (DeliveryTickLedger) instead of applying per chunk:
        //   1. The device's own quiescent is BURNED out of the stream first, at most once per
        //      tick and only up to the FUNDED amount (the same FundedQuiescent the bill used).
        //      The old unconditional per-chunk `powerAdded -= UsedPower` over-subtracted once per
        //      provider and kept subtracting for shed / roster-absent APCs whose bill carried no
        //      quiescent, under-crediting the cell by up to one quiescent per tick.
        //   2. The cell credit comes straight out of the delivered stream, cumulatively capped at
        //      the allocator's granted charge share for the tick (plus the physical PowerDelta /
        //      ComputeChargeCap caps). The old negative-ledger gate shuttled passthrough watts
        //      through the cell on conducting APCs (credited beyond the share, re-drained by
        //      UsePower a tick later) and starved the credit entirely on passthrough down-ramps.
        //   3. Only the remainder (the passthrough component) reduces the vanilla _powerProvided
        //      ledger, which keeps the vanilla cell-cover feature intact: UsePower still drains
        //      the cell when the downstream draw outruns what the input repaid.
        [HarmonyPrefix, HarmonyPatch(nameof(AreaPowerControl.ReceivePower))]
        public static bool ReceivePowerPatch(CableNetwork cableNetwork, float powerAdded, AreaPowerControl __instance, ref float ____powerProvided)
        {
            if (!Settings.EnableAreaPowerControlFix.Value)
                return true;

            if (__instance.InputNetwork == null || cableNetwork != __instance.InputNetwork)
                return false;

            long refId = __instance.ReferenceId;
            powerAdded -= DeliveryTickLedger.TakeQuiescentBurn(refId, FundedQuiescent(__instance), powerAdded);
            if (powerAdded <= 0.0f)
                return false;

            var cell = __instance.Battery;
            if ((bool)cell && !cell.IsCharged)
            {
                float grantedShare = SoftDemandShareCache.TryGetShare(refId, out var share) ? share : 0f;
                float credit = DeliveryTickLedger.TakeShareCredit(refId, grantedShare,
                    Mathf.Min(powerAdded, Mathf.Min(cell.PowerDelta, ComputeChargeCap(__instance))));
                if (credit > 0f)
                {
                    cell.PowerStored += credit;
                    powerAdded -= credit;
                    // Record the exact credited amount at the source (argument-derived by
                    // construction): the APC needs no observation bracket, and the cell's own
                    // float storage rounding never enters the audit.
                    ChargeDeliveryAudit.RecordCredit(refId, ChargeDeliveryAudit.KindApcCell, credit);
                }
            }
            else if (SoftDemandShareCache.TryGetShare(refId, out var mootShare) && mootShare > 0f)
            {
                // A charge share exists but the cell can take none (reads full, or was pulled):
                // the cell crossed its Mode-based IsCharged between the ALLOCATE grant and this
                // delivery (Mode updates through a main-thread interact, so the flip can land
                // inside the tick). The grant is moot this tick; tell the audit so the
                // legitimate fill-edge (granted > 0, credited 0) is recognized instead of
                // flagged (the farm-APC tick-868 finding).
                ChargeDeliveryAudit.MarkChargeGateClosed(refId);
            }

            if (powerAdded > 0.0f)
                ____powerProvided -= powerAdded;
            return false;
        }

        [HarmonyPrefix, HarmonyPatch(nameof(AreaPowerControl.GetUsedPower))]
        public static bool GetUsedPowerPatch(CableNetwork cableNetwork, AreaPowerControl __instance, ref float __result)
        {
            if (!Settings.EnableAreaPowerControlFix.Value)
                return true;

            __result = 0.0f;

            if (__instance.InputNetwork == null || cableNetwork != __instance.InputNetwork)
                return false;

            float usedPower = 0.0f;
            if (__instance.OnOff)
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
                __result = ApcDischargeRateRegistry.GetDischargeRate(__instance.ReferenceId);
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
            if (!Settings.EnableAreaPowerControlFix.Value) return;
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
