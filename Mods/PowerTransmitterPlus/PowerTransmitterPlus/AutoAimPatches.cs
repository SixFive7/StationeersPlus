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

        // Public restore entry point for the side-car save load and the multiplayer
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

        // Re-run the aim solve for an already-cached target, bypassing the
        // cache-hit short-circuit in HandleWrite. Used by the post-load
        // re-solve pass: walks every dish with a cached target and recomputes
        // (H, V) under the current solver. If the cached target id no longer
        // resolves to a Thing, clears the cache as if the user had cleared it.
        //
        // Must run on the host (single-player or server). On a client joining
        // a remote host, IJoinSuffixSerializer.DeserializeJoinSuffix already
        // restores authoritative cache values; the host's re-solve pass has
        // recomputed (H, V) and the client receives those via the existing
        // flag-256 delta, so no client-side pass is needed.
        internal static void ResolveCachedTarget(WirelessPower dish, long targetId)
        {
            if (dish == null) return;
            if (targetId == 0L)
            {
                SetCache(dish, 0L);
                return;
            }

            var target = Thing.Find(targetId);
            if (target == null || target == dish)
            {
                // Stale or self-referential target. Clear the cache as if the
                // user manually cleared MicrowaveAutoAimTarget. SetCache(0)
                // also flags the dish for per-tick sync so connected clients
                // observe the cleared value.
                SetCache(dish, 0L);
                return;
            }

            // Force HandleWrite to take the full solve path: clear the cached
            // box value (without firing the network flag, which HandleWrite
            // will set when it succeeds), then re-enter HandleWrite.
            if (_target.TryGetValue(dish, out var box)) box.Value = 0L;
            HandleWrite(dish, targetId);
        }

        // Yields (dishReferenceId, targetReferenceId) pairs for every dish
        // with a non-zero cached target. Used by AutoAimSideCar at save time
        // and AutoAimSnapshotSync at multiplayer join time. Walks _tracked, resolves
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
        // collider.
        //
        // For dish-to-dish targets (the partner is itself a WirelessPower whose
        // own DishTarget moves with its pose), a single-side iteration treats
        // the partner's pose as static and ends up aimed at where the partner's
        // DishTarget WAS at solve time, not where it ends up after both dishes
        // slew. At long range that residual exceeds the partner's small
        // DishTarget collider and the link raycast fails. The fix is a JOINT
        // mutual-aim solve: solve simultaneously for both dishes' (H, V) at the
        // unique fixed point where each dish's RayTransform.forward passes
        // through the other's DishTarget. This fixed point depends only on the
        // two roots' (rotation-invariant) positions and orientations, so each
        // dish independently arrives at the same shared answer when configured.
        // The partner does not need to have us cached as its target for this to
        // work; if the partner is later configured (or already is), it runs the
        // same joint solve and lands on the same fixed point. We write only our
        // own half of the pose; the partner's predicted (H, V) stays virtual.
        //
        // Both inner and outer iterations seed from a canonical pivot-to-pivot
        // pose computed from the immutable root positions, so the result is
        // invariant under the dish's prior aim history. A dish switching
        // between multiple targets always converges to the same answer for any
        // given pairing.
        //
        // Combined contraction factor for the joint solve is approximately
        // (|self_offset| / D) * (|partner_offset| / D), so at D = 200 m the
        // residual drops by ~10000x per outer iteration. One outer pass reaches
        // sub-mm precision; the cap is a safety net.
        internal const int MaxAimIterations = 10;
        internal const int MaxOuterIterations = 5;
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

            if (dish.RayTransform == null || dish.AxleTransform == null || dish.DishTransform == null)
                return;

            // Pick the partner's ray endpoint (the point our forward should
            // pass through). Mirrors the constraint TryContactReceiver enforces
            // when target is a PowerReceiver, and the natural pivot when target
            // is a PowerTransmitter (an RX auto-aiming back at a TX).
            //   - PowerReceiver: DishTarget (the collider tested for hit.transform).
            //   - PowerTransmitter: RayTransform.
            //   - other Thing types: fall back to root, single-side solve.
            var partner = target as WirelessPower;
            Transform partnerEndpoint = ResolveEndpoint(target);
            Transform myEndpoint = ResolveEndpoint(dish);

            // Canonical seed: aim from each dish's root toward the other's
            // root. Depends only on rotation-invariant positions. Both dishes
            // computing the joint solve seed the same way for any given pair,
            // so the result is independent of prior aim history and the order
            // in which the two dishes were configured.
            (double h, double v) = SeedFromRootDirection(dish, target.transform.position);
            (double ph, double pv) = partner != null
                ? SeedFromRootDirection(partner, dish.transform.position)
                : (0.0, 0.0);

            Vector3 toPos;
            if (partner != null && partnerEndpoint != null)
            {
                // Joint mutual-aim solve. We virtually predict the partner at
                // (ph, pv) to read where its DishTarget/RayTransform would land,
                // then solve our (h, v) against that virtual point. We update
                // (ph, pv) by predicting the partner aiming back at our virtual
                // (h, v), and iterate. Always run regardless of the partner's
                // own cache: the fixed point is symmetric and the partner's
                // eventual solve (or current pose, when configured) lands on
                // the same answer.
                toPos = PredictEndpoint(partner, partnerEndpoint, ph, pv);
                for (int outer = 0; outer < MaxOuterIterations; outer++)
                {
                    var mine = SolveAim(dish, toPos, h, v);
                    h = mine.H; v = mine.V;

                    // Where would partner's forward need to point to hit our
                    // current virtual ray endpoint? Predict our endpoint at
                    // (h, v) to feed the partner solve.
                    Vector3 partnerToPos = myEndpoint != null
                        ? PredictEndpoint(dish, myEndpoint, h, v)
                        : dish.transform.position;
                    var theirs = SolveAim(partner, partnerToPos, ph, pv);
                    ph = theirs.H; pv = theirs.V;

                    Vector3 newToPos = PredictEndpoint(partner, partnerEndpoint, ph, pv);
                    Vector3 delta = newToPos - toPos;
                    toPos = newToPos;
                    if (delta.sqrMagnitude < AimConvergenceTolerance * AimConvergenceTolerance) break;
                }
            }
            else
            {
                // Non-dish target: partner is static, single-side solve is
                // exact. Aim at the target's root.
                toPos = target.transform.position;
                var mine = SolveAim(dish, toPos, h, v);
                h = mine.H; v = mine.V;
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

        // Pick the dish's "ray endpoint" for joint-solve constraints:
        //   PowerReceiver -> DishTarget (link raycast tests hit.transform == DishTarget)
        //   PowerTransmitter -> RayTransform (the ray origin)
        //   anything else -> null (caller falls back to root)
        private static Transform ResolveEndpoint(Thing thing)
        {
            if (thing is PowerReceiver rx && rx.DishTarget != null) return rx.DishTarget;
            if (thing is PowerTransmitter tx && tx.RayTransform != null) return tx.RayTransform;
            return null;
        }

        // Canonical seed: solve (H, V) from the dish-local direction of
        // (targetRoot - dishRoot). Both arguments are rotation-invariant, so
        // every call with the same (dish, target) pair produces the same seed
        // regardless of prior aim history. Used for both dish and partner in
        // the joint solve so both sides start from the same canonical pose.
        private static (double H, double V) SeedFromRootDirection(WirelessPower dish, Vector3 targetWorld)
        {
            Vector3 diff = targetWorld - dish.transform.position;
            if (diff.sqrMagnitude < 1e-6f) return (0.0, 1.0);

            Vector3 dLocal = dish.transform.InverseTransformDirection(diff.normalized);
            return SolveLocal(dLocal, 0.0, 1.0);
        }

        // Inverse of dish-local Euler: world unit direction (in dish-local
        // frame) -> (H, V). At the dish-local pole (|dLocal.y| ~= 1) azimuth
        // is undefined; keep the supplied prior H. V always solvable from
        // the y-component alone.
        private static (double H, double V) SolveLocal(Vector3 dLocal, double priorH, double priorV)
        {
            double sinA = -dLocal.y;
            if (sinA > 1.0) sinA = 1.0;
            else if (sinA < -1.0) sinA = -1.0;
            double alpha = Math.Asin(sinA);          // [-pi/2, pi/2]
            double v = 0.5 - alpha / Math.PI;        // [0, 1]

            double h = priorH;
            double cosA = Math.Cos(alpha);
            if (cosA > 1e-6)
            {
                double theta = Math.Atan2(dLocal.x, dLocal.z);
                h = theta / (2.0 * Math.PI);
                if (h < 0.0) h += 1.0;
            }
            return (h, v);
        }

        internal struct AimSolution
        {
            public double H;
            public double V;
        }

        // Inner fixed-point solve: given a fixed target position, iterate
        // (H, V) on `dish` until its predicted RayTransform.position is
        // self-consistent with its (H, V). The aim direction is recomputed at
        // each step from the virtually-posed RayTransform, so the iteration
        // converges to a pose where RayTransform.forward points exactly at
        // the target. Caller seeds (h, v); for the joint solve this is the
        // canonical pivot-to-pivot seed, not the dish's current pose, so the
        // result is independent of prior aim history.
        internal static AimSolution SolveAim(WirelessPower dish, Vector3 toPos, double h, double v)
        {
            if (dish.RayTransform == null || dish.AxleTransform == null || dish.DishTransform == null)
                return new AimSolution { H = h, V = v };

            // Prime the origin from the canonical seed pose, not the dish's
            // current RayTransform.position, so the iteration is order-
            // independent.
            Vector3 origin = PredictEndpoint(dish, dish.RayTransform, h, v);
            for (int iter = 0; iter < MaxAimIterations; iter++)
            {
                Vector3 diff = toPos - origin;
                if (diff.sqrMagnitude < 1e-6f) break;

                Vector3 dLocal = dish.transform.InverseTransformDirection(diff.normalized);
                (h, v) = SolveLocal(dLocal, h, v);

                Vector3 newOrigin = PredictEndpoint(dish, dish.RayTransform, h, v);
                Vector3 delta = newOrigin - origin;
                origin = newOrigin;
                if (delta.sqrMagnitude < AimConvergenceTolerance * AimConvergenceTolerance) break;
            }
            return new AimSolution { H = h, V = v };
        }

        // Predict where `endpoint`.position would land if `dish` were posed at
        // (h, v), without committing to the slew. Temporarily writes the local
        // rotations on AxleTransform / DishTransform, reads endpoint.position
        // (Unity recomputes the world chain on read), and restores the saved
        // rotations. `endpoint` is any descendant of dish.AxleTransform: pass
        // dish.RayTransform to predict the ray origin, or rx.DishTarget to
        // predict the partner's link-raycast target point.
        //
        // Side-effect-free at the game level: direct Transform.localRotation
        // writes do not flow through the WirelessPower.Horizontal / Vertical
        // property setters, so they do not set NetworkUpdateFlags |= 256, do
        // not call BeamManager.RefreshIfVisible, and do not invalidate the
        // RotatableBehaviour servo state. This is purely a synchronous Unity
        // transform recomputation.
        internal static Vector3 PredictEndpoint(WirelessPower dish, Transform endpoint, double h, double v)
        {
            if (dish.AxleTransform == null || dish.DishTransform == null || endpoint == null)
                return Vector3.zero;

            var savedAxle = dish.AxleTransform.localRotation;
            var savedDish = dish.DishTransform.localRotation;
            try
            {
                dish.AxleTransform.localRotation = Quaternion.Euler(0f, (float)(h * dish.MaximumHorizontal), 0f);
                dish.DishTransform.localRotation = Quaternion.Euler(Mathf.Lerp(90f, -90f, (float)v), 0f, 0f);
                return endpoint.position;
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
