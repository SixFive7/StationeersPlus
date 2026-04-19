using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;

namespace PowerTransmitterPlus
{
    // MicrowaveAutoAimTarget implementation. The write path resolves the target
    // Thing from its ReferenceId, computes Horizontal/Vertical from the dish's
    // transform frame, and writes them as RotatableBehaviour targets so the
    // built-in servo animates the motion. TryContactReceiver decides when the
    // actual link is established, same as manual aiming. Writing 0 disables.
    // Manually overriding Horizontal or Vertical afterwards clears the cached
    // target so the dish returns to full manual control.
    internal static class AutoAimState
    {
        private static readonly ConditionalWeakTable<WirelessPower, StrongBox<long>> _target =
            new ConditionalWeakTable<WirelessPower, StrongBox<long>>();

        [ThreadStatic] private static bool _suppressReset;

        internal static bool SuppressReset
        {
            get => _suppressReset;
            set => _suppressReset = value;
        }

        internal static long GetCachedTarget(WirelessPower dish)
        {
            return _target.TryGetValue(dish, out var box) ? box.Value : 0L;
        }

        private static void SetCache(WirelessPower dish, long id)
        {
            if (_target.TryGetValue(dish, out var box)) box.Value = id;
            else _target.Add(dish, new StrongBox<long>(id));
        }

        internal static void ClearCache(WirelessPower dish)
        {
            if (_target.TryGetValue(dish, out var box)) box.Value = 0L;
        }

        private static readonly FieldInfo ParentRotatableField =
            AccessTools.Field(typeof(RotatableBehaviour), "_parentRotatable");

        internal static bool TryGetOwner(RotatableBehaviour rb, out WirelessPower dish)
        {
            dish = null;
            if (rb == null || ParentRotatableField == null) return false;
            dish = ParentRotatableField.GetValue(rb) as WirelessPower;
            return dish != null;
        }

        // Write path. Redundant writes short-circuit on the cache-hit check.
        // Unresolved ids leave cache untouched so a subsequent rewrite of the
        // same id will retry the lookup (covers the "id referred to a thing
        // that did not yet exist at first write" case).
        internal static void HandleWrite(WirelessPower dish, long newId)
        {
            if (dish == null) return;

            var cached = GetCachedTarget(dish);
            if (cached == newId) return;

            if (newId == 0L)
            {
                SetCache(dish, 0L);
                return;
            }

            var target = Thing.Find(newId);
            if (target == null || target == dish) return;

            // Use pivot-to-pivot geometry. Both our origin and the target
            // point must be invariant under dish rotation; otherwise the
            // aim we compute depends on the current (wrong) pose of either
            // dish and drifts as dishes rotate.
            //
            //   Origin: dish.transform.position is the TX/RX root, fixed.
            //           (RayTransform / DishTransform are children of the
            //           rotating Head, so they move.)
            //   Target: target.transform.position is the target's root,
            //           fixed. For a PowerReceiver, its DishTarget collider
            //           is a Head child that swings with the RX's current
            //           aim; aiming at DishTarget would lock us onto the
            //           RX's current (possibly wrong) aim and fail to track
            //           when the RX subsequently moves to correct.
            //
            // With pivot-to-pivot, when BOTH dishes auto-aim at each other's
            // root, the forward axes become anti-parallel along the pivot-
            // pivot line; the base-game TryContactReceiver raycast from
            // TX.RayTransform then passes through RX.DishTarget because the
            // latter ends up on that same line once RX is correctly aimed.
            var from = dish.transform.position;
            var toTransform = target.transform;
            if (toTransform == null) return;

            Vector3 diff = toTransform.position - from;
            if (diff.sqrMagnitude < 1e-6f) return;

            Vector3 dWorld = diff.normalized;
            Vector3 dLocal = dish.transform.InverseTransformDirection(dWorld);

            double sinA = -dLocal.y;
            if (sinA > 1.0) sinA = 1.0;
            else if (sinA < -1.0) sinA = -1.0;
            double alpha = Math.Asin(sinA);              // [-pi/2, pi/2]
            double v = 0.5 - alpha / Math.PI;            // [0, 1]

            double cosA = Math.Cos(alpha);
            double h;
            if (cosA > 1e-6)
            {
                double theta = Math.Atan2(dLocal.x, dLocal.z);
                h = theta / (2.0 * Math.PI);
                if (h < 0.0) h += 1.0;
            }
            else
            {
                // Target is along the dish's local up/down axis; azimuth is
                // undefined. Keep the current horizontal target.
                h = dish.RotatableBehaviour != null ? dish.RotatableBehaviour.TargetHorizontal : 0.0;
            }

            SetCache(dish, newId);

            if (dish.RotatableBehaviour == null) return;

            _suppressReset = true;
            try
            {
                dish.RotatableBehaviour.TargetHorizontal = h;
                dish.RotatableBehaviour.TargetVertical = v;
            }
            finally
            {
                _suppressReset = false;
            }
        }
    }

    // Intercept writes to MicrowaveAutoAimTarget. Everything else passes through
    // to vanilla SetLogicValue.
    [HarmonyPatch(typeof(WirelessPower), nameof(WirelessPower.SetLogicValue))]
    public static class WirelessPowerSetLogicValuePatch
    {
        [UsedImplicitly]
        public static bool Prefix(WirelessPower __instance, LogicType logicType, double value)
        {
            if ((ushort)logicType != LogicTypeRegistry.AutoAimTargetValue) return true;
            AutoAimState.HandleWrite(__instance, (long)value);
            return false;
        }
    }

    // Mark MicrowaveAutoAimTarget writable on transmitter and receiver.
    [HarmonyPatch(typeof(WirelessPower), nameof(WirelessPower.CanLogicWrite))]
    public static class WirelessPowerCanLogicWritePatch
    {
        [UsedImplicitly]
        public static void Postfix(WirelessPower __instance, LogicType logicType, ref bool __result)
        {
            if ((ushort)logicType != LogicTypeRegistry.AutoAimTargetValue) return;
            if (__instance is PowerTransmitter || __instance is PowerReceiver) __result = true;
        }
    }

    // Any non-auto-aim write to the dish's target horizontal or vertical
    // (player action, IC10 s d0 Horizontal, tablet adjustment, etc.) clears
    // the cached target so auto-aim relinquishes control. Our own writes set
    // a thread-static suppression flag so this postfix skips them.
    [HarmonyPatch(typeof(RotatableBehaviour), nameof(RotatableBehaviour.TargetHorizontal), MethodType.Setter)]
    public static class RotatableTargetHorizontalResetPatch
    {
        [UsedImplicitly]
        public static void Postfix(RotatableBehaviour __instance)
        {
            if (AutoAimState.SuppressReset) return;
            if (AutoAimState.TryGetOwner(__instance, out var dish)) AutoAimState.ClearCache(dish);
        }
    }

    [HarmonyPatch(typeof(RotatableBehaviour), nameof(RotatableBehaviour.TargetVertical), MethodType.Setter)]
    public static class RotatableTargetVerticalResetPatch
    {
        [UsedImplicitly]
        public static void Postfix(RotatableBehaviour __instance)
        {
            if (AutoAimState.SuppressReset) return;
            if (AutoAimState.TryGetOwner(__instance, out var dish)) AutoAimState.ClearCache(dish);
        }
    }
}
