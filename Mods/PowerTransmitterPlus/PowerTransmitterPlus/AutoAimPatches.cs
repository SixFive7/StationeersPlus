using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Assets.Scripts;
using Assets.Scripts.Networking;
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

        // Parallel id-keyed tracking for enumeration at save / join-sync time.
        // _target's CWT is the source of truth for instance-keyed lookups;
        // _tracked duplicates the key set by ReferenceId so SnapshotEntries
        // can yield stable (dishId, targetId) pairs without depending on the
        // CWT's enumerator (inconsistent across Unity Mono builds). Dead
        // entries (dish destroyed) self-clean during SnapshotEntries via
        // WeakReference.TryGetTarget; no explicit OnDestroy patch needed.
        private static readonly Dictionary<long, WeakReference<WirelessPower>> _tracked =
            new Dictionary<long, WeakReference<WirelessPower>>();

        // Custom NetworkUpdateFlags bit reserved for per-tick auto-aim target id
        // sync. Vanilla's WirelessPower hierarchy uses 1, 2, 4, 8, 16, 32, 64,
        // 128, 256, 512 across Thing -> Structure -> Device -> ElectricalInputOutput
        // -> WirelessPower; 0x2000 is free here. Mirrors EquipmentPlus's 0x4000
        // pattern on SensorLenses (see Research/Protocols/EquipmentPlusNetworking.md
        // and Research/GameSystems/NetworkUpdateFlags.md).
        //
        // Setting this flag on a dish causes the host's next SerializeDeltaState
        // tick to call WirelessPower.BuildUpdate, where our postfix appends the
        // cached target ReferenceId. The receiving client's ProcessUpdate postfix
        // reads it back and updates its cache. Without this, vanilla's per-tick
        // delta (bit 256) carries only TargetHorizontal/TargetVertical, and the
        // setter writes on the client fire our reset postfix which wipes the
        // cache entry the join-time IJoinSuffixSerializer populated.
        internal const ushort AutoAimUpdateFlag = 0x2000;

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

            // Mark the dish for per-tick delta sync. Has no effect on clients
            // because only the host's SerializeDeltaState reads NetworkUpdateFlags;
            // setting the flag on a client-side instance is a harmless no-op.
            // Set BEFORE the zero-id early-return so that explicit clears
            // (HandleWrite(dish, 0L) on the host) propagate to clients too.
            dish.NetworkUpdateFlags |= AutoAimUpdateFlag;

            // Skip tracking for explicit clears: the CWT keeps the entry but
            // _tracked exists only to enumerate non-zero-targeted dishes. Live
            // ClearCache calls bypass SetCache entirely; this guard handles
            // the HandleWrite(dish, 0) path.
            if (id == 0L) return;

            var refId = dish.ReferenceId;
            if (refId != 0L && !_tracked.ContainsKey(refId))
            {
                _tracked[refId] = new WeakReference<WirelessPower>(dish);
            }
        }

        internal static void ClearCache(WirelessPower dish)
        {
            if (_target.TryGetValue(dish, out var box)) box.Value = 0L;
        }

        // Public restore entry point for the side-car save load and the MP
        // join snapshot. Bypasses HandleWrite's geometry solve (vanilla servo
        // restore has already aimed the dish at the saved pose by the time
        // this runs); we only need to put the target ReferenceId back into
        // the cache so GetLogicValue(MicrowaveAutoAimTarget) returns it.
        //
        // Skips zero ids because the side-car serializer only emits non-zero
        // entries; a zero on this path means "no auto-aim was active for this
        // dish at save time", which is the default state and needs no action.
        internal static void RestoreCache(WirelessPower dish, long targetId)
        {
            if (dish == null || targetId == 0L) return;
            SetCache(dish, targetId);
        }

        // Apply an authoritative target-id update received from the host's
        // per-tick delta. Unlike RestoreCache (which is for snapshot restore
        // and ignores zero), this writes the value verbatim including 0 so
        // a host-side "auto-aim cleared" propagates to clients and IC10
        // reads on both peers stay coherent.
        //
        // Called only from WirelessPowerProcessUpdateAutoAimPatch on clients.
        // The flag set inside SetCache is harmless on clients (no
        // SerializeDeltaState runs there), so we do not need to suppress it.
        internal static void ApplyDeltaUpdate(WirelessPower dish, long targetId)
        {
            if (dish == null) return;
            if (targetId == 0L) ClearCache(dish);
            else SetCache(dish, targetId);
        }

        // Yields (dishReferenceId, targetReferenceId) pairs for every dish
        // with a non-zero cached target. Used by AutoAimSideCar at save time
        // and AutoAimSnapshotSync at MP join time. Walks _tracked, resolves
        // the WeakReference, reads _target via the live dish instance, drops
        // dead-ref entries inline. Materialises into a list so the caller can
        // enumerate freely without holding a reference into _tracked.
        internal static IEnumerable<KeyValuePair<long, long>> SnapshotEntries()
        {
            List<long> dead = null;
            var entries = new List<KeyValuePair<long, long>>(_tracked.Count);
            foreach (var kv in _tracked)
            {
                if (!kv.Value.TryGetTarget(out var dish) || dish == null)
                {
                    if (dead == null) dead = new List<long>();
                    dead.Add(kv.Key);
                    continue;
                }
                if (!_target.TryGetValue(dish, out var box) || box.Value == 0L) continue;
                entries.Add(new KeyValuePair<long, long>(dish.ReferenceId, box.Value));
            }
            if (dead != null)
            {
                foreach (var k in dead) _tracked.Remove(k);
            }
            return entries;
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

        // Write path. Redundant writes to the same target ID short-circuit on
        // the cache-hit check at the top. Unresolved ids leave cache untouched
        // so a subsequent rewrite of the same id will retry the lookup (covers
        // the "id referred to a thing that did not yet exist at first write"
        // case).
        //
        // The aim solve is a fixed-point iteration: the (H, V) we want depends
        // on RayTransform.position, which itself depends on (H, V) (RayTransform
        // is a child of the rotating dish hierarchy). One-shot aim leaves a
        // residual error proportional to the perpendicular component of the
        // root-to-RayTransform offset against the aim direction; for non-floor
        // mounts that residual is enough at typical link distances to make
        // vanilla's narrow Physics.Raycast miss the receiver's small DishTarget
        // collider. We iterate until convergence: at each step compute the aim
        // direction from a candidate origin, solve (H, V), then predict where
        // RayTransform would actually be at that pose by temporarily writing
        // the local rotations and reading RayTransform.position. The contraction
        // factor of this iteration is approximately |root-to-RayTransform offset|
        // / link_distance, so for D = 42 m and offset ~= 1.94 m, k ~= 0.046:
        // 2-3 iterations reach mm precision. Cap at 10 iterations covers
        // distances down to ~5 m comfortably; below ~|offset| (~ 2 m) the
        // iteration mathematically does not converge but that placement is
        // pathological. Early-break when origin moves less than 1 cm between
        // iterations keeps the steady-state cost at one iteration.
        internal const int MaxAimIterations = 10;
        internal const float AimConvergenceTolerance = 0.01f; // 1 cm

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

            // Target: the OTHER dish's actual ray endpoint:
            //   - PowerReceiver: DishTarget (the collider TryContactReceiver tests
            //     for hit.transform == rx.DishTarget). Pointing TX.RayTransform.forward
            //     directly at this point is what makes the raycast actually hit.
            //   - PowerTransmitter: RayTransform (when an RX auto-aims at a TX).
            //   - other Thing types: fall back to root.
            Vector3 toPos;
            if (target is PowerReceiver targetRx && targetRx.DishTarget != null)
                toPos = targetRx.DishTarget.position;
            else if (target is PowerTransmitter targetTx && targetTx.RayTransform != null)
                toPos = targetTx.RayTransform.position;
            else
                toPos = target.transform.position;

            if (dish.RayTransform == null || dish.AxleTransform == null || dish.DishTransform == null)
                return;

            Vector3 origin = dish.RayTransform.position;
            double h = dish.RotatableBehaviour != null ? dish.RotatableBehaviour.TargetHorizontal : 0.0;
            double v = dish.RotatableBehaviour != null ? dish.RotatableBehaviour.TargetVertical : 1.0;

            for (int iter = 0; iter < MaxAimIterations; iter++)
            {
                Vector3 diff = toPos - origin;
                if (diff.sqrMagnitude < 1e-6f) return;

                Vector3 dWorld = diff.normalized;
                Vector3 dLocal = dish.transform.InverseTransformDirection(dWorld);

                double sinA = -dLocal.y;
                if (sinA > 1.0) sinA = 1.0;
                else if (sinA < -1.0) sinA = -1.0;
                double alpha = Math.Asin(sinA);              // [-pi/2, pi/2]
                v = 0.5 - alpha / Math.PI;                   // [0, 1]

                double cosA = Math.Cos(alpha);
                if (cosA > 1e-6)
                {
                    double theta = Math.Atan2(dLocal.x, dLocal.z);
                    h = theta / (2.0 * Math.PI);
                    if (h < 0.0) h += 1.0;
                }
                // else: target is along dish-local up/down axis, azimuth undefined;
                // keep h from previous iteration.

                Vector3 newOrigin = PredictRayPosition(dish, h, v);
                Vector3 delta = newOrigin - origin;
                origin = newOrigin;

                if (delta.sqrMagnitude < AimConvergenceTolerance * AimConvergenceTolerance) break;
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

        // Predict where RayTransform.position would land if the dish were posed
        // at the given (H, V), without committing to the slew. Temporarily
        // writes the local rotations on AxleTransform / DishTransform, reads
        // RayTransform.position (Unity recomputes the world chain on read),
        // and restores the saved rotations.
        //
        // Side-effect-free at the game level: direct Transform.localRotation
        // writes do not flow through the WirelessPower.Horizontal / Vertical
        // property setters, so they do not set NetworkUpdateFlags |= 256, do
        // not call BeamManager.RefreshIfVisible, and do not invalidate the
        // RotatableBehaviour servo state. This is purely a synchronous Unity
        // transform recomputation.
        private static Vector3 PredictRayPosition(WirelessPower dish, double h, double v)
        {
            var savedAxle = dish.AxleTransform.localRotation;
            var savedDish = dish.DishTransform.localRotation;
            try
            {
                dish.AxleTransform.localRotation = Quaternion.Euler(0f, (float)(h * dish.MaximumHorizontal), 0f);
                dish.DishTransform.localRotation = Quaternion.Euler(Mathf.Lerp(90f, -90f, (float)v), 0f, 0f);
                return dish.RayTransform.position;
            }
            finally
            {
                dish.AxleTransform.localRotation = savedAxle;
                dish.DishTransform.localRotation = savedDish;
            }
        }
    }

    // Intercept writes to MicrowaveAutoAimTarget. Everything else passes through
    // to vanilla SetLogicValue. Gated whole-class on AutoAimPatched: when the
    // feature is off at boot, this patch is never applied and vanilla handles
    // the write (which no-ops for an unregistered LogicType).
    [HarmonyPatch(typeof(WirelessPower), nameof(WirelessPower.SetLogicValue))]
    public static class WirelessPowerSetLogicValuePatch
    {
        [UsedImplicitly]
        public static bool Prepare() => PowerTransmitterPlusPlugin.AutoAimPatched;

        [UsedImplicitly]
        public static bool Prefix(WirelessPower __instance, LogicType logicType, double value)
        {
            if ((ushort)logicType != LogicTypeRegistry.AutoAimTargetValue) return true;
            AutoAimState.HandleWrite(__instance, (long)value);
            return false;
        }
    }

    // Mark MicrowaveAutoAimTarget writable on transmitter and receiver. Gated
    // with the same Prepare so the writable bit is not advertised when the
    // feature is disabled (tablet and IC10 treat it as an ordinary unknown
    // LogicType and refuse the write).
    [HarmonyPatch(typeof(WirelessPower), nameof(WirelessPower.CanLogicWrite))]
    public static class WirelessPowerCanLogicWritePatch
    {
        [UsedImplicitly]
        public static bool Prepare() => PowerTransmitterPlusPlugin.AutoAimPatched;

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
    // a thread-static suppression flag so this postfix skips them. Gated on
    // AutoAimPatched: no cache exists to invalidate when the feature is off.
    [HarmonyPatch(typeof(RotatableBehaviour), nameof(RotatableBehaviour.TargetHorizontal), MethodType.Setter)]
    public static class RotatableTargetHorizontalResetPatch
    {
        [UsedImplicitly]
        public static bool Prepare() => PowerTransmitterPlusPlugin.AutoAimPatched;

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
        public static bool Prepare() => PowerTransmitterPlusPlugin.AutoAimPatched;

        [UsedImplicitly]
        public static void Postfix(RotatableBehaviour __instance)
        {
            if (AutoAimState.SuppressReset) return;
            if (AutoAimState.TryGetOwner(__instance, out var dish)) AutoAimState.ClearCache(dish);
        }
    }

    // Per-tick auto-aim target sync (host-side write). Whenever HandleWrite
    // updates the cache on the host, SetCache sets AutoAimUpdateFlag on the
    // dish's NetworkUpdateFlags. The next SerializeDeltaState tick calls
    // BuildUpdate; vanilla writes its own bit-conditional payloads (256:
    // TargetH/V; 512: VisualizerIntensity), then our postfix appends the
    // cached target ReferenceId when our bit is set.
    //
    // Order matters: Postfix runs strictly after vanilla's body so our 8 bytes
    // append to the end of the dish's per-Thing slice in the delta stream. The
    // matching ProcessUpdate Postfix on clients reads in the same order.
    [HarmonyPatch(typeof(WirelessPower), nameof(WirelessPower.BuildUpdate))]
    public static class WirelessPowerBuildUpdateAutoAimPatch
    {
        [UsedImplicitly]
        public static bool Prepare() => PowerTransmitterPlusPlugin.AutoAimPatched;

        [UsedImplicitly]
        public static void Postfix(WirelessPower __instance, RocketBinaryWriter writer, ushort networkUpdateType)
        {
            if (!Thing.IsNetworkUpdateRequired(AutoAimState.AutoAimUpdateFlag, networkUpdateType)) return;
            writer.WriteInt64(AutoAimState.GetCachedTarget(__instance));
        }
    }

    // Per-tick auto-aim target sync (client-side read). Counterpart to the
    // BuildUpdate postfix above. Runs after vanilla's ProcessUpdate body, so
    // by the time we read our 8 bytes the vanilla setter writes for bits 256
    // and 512 have already fired. Those setter writes triggered the reset
    // postfix on the client which cleared the cache entry; this postfix
    // immediately re-populates it with the host's authoritative value.
    [HarmonyPatch(typeof(WirelessPower), nameof(WirelessPower.ProcessUpdate))]
    public static class WirelessPowerProcessUpdateAutoAimPatch
    {
        [UsedImplicitly]
        public static bool Prepare() => PowerTransmitterPlusPlugin.AutoAimPatched;

        [UsedImplicitly]
        public static void Postfix(WirelessPower __instance, RocketBinaryReader reader, ushort networkUpdateType)
        {
            if (!Thing.IsNetworkUpdateRequired(AutoAimState.AutoAimUpdateFlag, networkUpdateType)) return;
            AutoAimState.ApplyDeltaUpdate(__instance, reader.ReadInt64());
        }
    }
}
