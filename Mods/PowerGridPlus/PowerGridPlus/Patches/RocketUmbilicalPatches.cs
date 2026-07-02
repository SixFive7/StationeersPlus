using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using HarmonyLib;
using Objects.Rockets;
using UnityEngine;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Rocket power umbilical support (POWERTODO 0.2.7.5b). Both halves carry an internal buffer
    ///     cell (shared abstract base RocketPowerUmbilical since game 0.2.6403) and face their grids
    ///     like batteries: vanilla GetUsedPower = own draw + cell headroom on the input network,
    ///     GetGeneratedPower = PowerStored on the output network (re-verified 0.2.6403.27689; see
    ///     Research/GameClasses/RocketPowerUmbilical.md, including the Female's laxer gates). The
    ///     cell-to-cell crossing between the halves is vanilla phase 2 and moves power BOTH ways
    ///     since 0.2.6403 (Male push station -> rocket every tick; Female TransferProgress-gated
    ///     pull from RocketNetwork.Batteries pushed rocket -> station). These patches only shape the
    ///     grid-facing halves and are direction-agnostic; the allocator models each half as a
    ///     buffered store (UmbilicalAdapter in SegAdapters.cs), never the crossing itself.
    ///
    ///     <para>Rate caps: when <c>EnableRocketUmbilicalLimits</c> is on, charge demand caps at
    ///     RocketUmbilicalChargeRate and discharge at RocketUmbilicalDischargeRate (both further
    ///     capped by the cable tier cap). When off, vanilla full-PowerMaximum-per-tick behaviour
    ///     applies. The elastic share caps (SoftSupply/SoftDemandShareCache, written by
    ///     PowerAllocator) apply regardless, mirroring batteries.</para>
    ///
    ///     <para>LogicTypes (master-toggle gated): MaxChargeSpeed / MaxDischargeSpeed report the
    ///     configured caps (cable-capped); ChargeSpeed / DischargeSpeed report the live allocator
    ///     shares. The Male declares its own CanLogicRead (patched directly); the Female declares no
    ///     logic methods, so its exposure rides the Device-base declarations with an instance filter
    ///     (the base patch only fires for classes without their own override, which includes the
    ///     Female). GetLogicValue is base-declared for both halves.</para>
    /// </summary>
    [HarmonyPatch]
    public static class RocketUmbilicalPatches
    {
        // ------------------------------------------------------------------
        // Rate caps + elastic shares.
        // ------------------------------------------------------------------

        private static float ChargeCap(ElectricalInputOutput umbilical)
        {
            var cable = umbilical.InputConnection?.GetCable();
            return Mathf.Min(Settings.RocketUmbilicalChargeRate.Value, CableMax.For(cable));
        }

        private static float DischargeCap(ElectricalInputOutput umbilical)
        {
            var cable = umbilical.OutputConnection?.GetCable();
            return Mathf.Min(Settings.RocketUmbilicalDischargeRate.Value, CableMax.For(cable));
        }

        private static void CapUsed(ElectricalInputOutput umbilical, float ownDraw, ref float result)
        {
            result = DeviceOutputSanitizer.Sanitize(result, umbilical, generated: false);
            if (result <= ownDraw) return;
            if (Settings.EnableRocketUmbilicalLimits.Value)
                result = Mathf.Min(result, ownDraw + ChargeCap(umbilical));
            if (SoftDemandShareCache.TryGetShare(umbilical.ReferenceId, out var share))
                result = Mathf.Min(result, ownDraw + share);
        }

        private static void CapGenerated(ElectricalInputOutput umbilical, ref float result)
        {
            result = DeviceOutputSanitizer.Sanitize(result, umbilical, generated: true);
            if (result <= 0f) return;
            if (Settings.EnableRocketUmbilicalLimits.Value)
                result = Mathf.Min(result, DischargeCap(umbilical));
            result = Mathf.Min(result, SoftSupplyShareCache.GetShare(umbilical.ReferenceId));
        }

        [HarmonyPostfix, HarmonyPatch(typeof(RocketPowerUmbilicalMale), nameof(RocketPowerUmbilicalMale.GetUsedPower))]
        public static void MaleUsed(RocketPowerUmbilicalMale __instance, ref float __result)
            => CapUsed(__instance, __instance.UsedPower, ref __result);

        [HarmonyPostfix, HarmonyPatch(typeof(RocketPowerUmbilicalFemale), nameof(RocketPowerUmbilicalFemale.GetUsedPower))]
        public static void FemaleUsed(RocketPowerUmbilicalFemale __instance, ref float __result)
            => CapUsed(__instance, __instance.UsedPower, ref __result);

        [HarmonyPostfix, HarmonyPatch(typeof(RocketPowerUmbilicalMale), nameof(RocketPowerUmbilicalMale.GetGeneratedPower))]
        public static void MaleGenerated(RocketPowerUmbilicalMale __instance, ref float __result)
            => CapGenerated(__instance, ref __result);

        [HarmonyPostfix, HarmonyPatch(typeof(RocketPowerUmbilicalFemale), nameof(RocketPowerUmbilicalFemale.GetGeneratedPower))]
        public static void FemaleGenerated(RocketPowerUmbilicalFemale __instance, ref float __result)
            => CapGenerated(__instance, ref __result);

        // ------------------------------------------------------------------
        // LogicType exposure (gated on EnableRocketUmbilicalLimits).
        // ------------------------------------------------------------------

        private static bool IsSoftPowerLogicType(LogicType logicType)
        {
            return logicType == LogicTypeRegistry.MaxChargeSpeed
                || logicType == LogicTypeRegistry.MaxDischargeSpeed
                || logicType == LogicTypeRegistry.ChargeSpeed
                || logicType == LogicTypeRegistry.DischargeSpeed;
        }

        private static bool TryGetSoftPowerValue(ElectricalInputOutput umbilical, LogicType logicType, out double value)
        {
            if (logicType == LogicTypeRegistry.MaxChargeSpeed) { value = ChargeCap(umbilical); return true; }
            if (logicType == LogicTypeRegistry.MaxDischargeSpeed) { value = DischargeCap(umbilical); return true; }
            if (logicType == LogicTypeRegistry.ChargeSpeed)
            {
                value = SoftDemandShareCache.GetActualOrZero(umbilical.ReferenceId);
                return true;
            }
            if (logicType == LogicTypeRegistry.DischargeSpeed)
            {
                value = SoftSupplyShareCache.GetActualOrZero(umbilical.ReferenceId);
                return true;
            }
            value = 0.0;
            return false;
        }

        // Male declares its own CanLogicRead; the Female inherits the Device base declaration.
        [HarmonyPostfix, HarmonyPatch(typeof(RocketPowerUmbilicalMale), nameof(RocketPowerUmbilicalMale.CanLogicRead))]
        public static void MaleCanRead(LogicType logicType, ref bool __result)
        {
            if (Settings.EnableRocketUmbilicalLimits.Value && IsSoftPowerLogicType(logicType))
                __result = true;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Device), nameof(Device.CanLogicRead), typeof(LogicType))]
        public static void DeviceCanRead(Device __instance, LogicType logicType, ref bool __result)
        {
            if (!(__instance is RocketPowerUmbilicalFemale)) return;
            if (Settings.EnableRocketUmbilicalLimits.Value && IsSoftPowerLogicType(logicType))
                __result = true;
        }

        // GetLogicValue is base-declared for both halves; the filter scopes the patch to them.
        // Explicit LogicType arg disambiguates from the slot-keyed overload (AmbiguousMatchException
        // otherwise; the same applies to CanLogicRead above).
        [HarmonyPostfix, HarmonyPatch(typeof(Device), nameof(Device.GetLogicValue), typeof(LogicType))]
        public static void DeviceGetValue(Device __instance, LogicType logicType, ref double __result)
        {
            if (!Settings.EnableRocketUmbilicalLimits.Value) return;
            if (!(__instance is ElectricalInputOutput umbilical)) return;
            if (!(__instance is RocketPowerUmbilicalMale) && !(__instance is RocketPowerUmbilicalFemale)) return;
            if (TryGetSoftPowerValue(umbilical, logicType, out var value))
                __result = value;
        }
    }
}
