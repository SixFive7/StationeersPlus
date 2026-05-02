using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Util;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;

namespace PowerTransmitterPlus
{
    // Drop the right-axis antiparallel check from PowerTransmitter.TryContactReceiver.
    //
    // Vanilla TryContactReceiver (PowerTransmitter.cs:240-257) requires all of:
    //   1. Physics.Raycast from RayTransform.position along RayTransform.forward hits a collider
    //   2. The hit collider belongs to a PowerReceiver (via Thing._colliderLookup)
    //   3. The hit transform is the receiver's DishTarget
    //   4. RocketMath.Approximately(Vector3.Angle(TX.RayTransform.forward, RX.RayTransform.forward), 180, 7)
    //   5. RocketMath.Approximately(Vector3.Angle(TX.RayTransform.right,   RX.RayTransform.right),   180, 7)
    //
    // Conditions 1-4 are correct for any pair of dishes. Condition 5 (the right-
    // axis antiparallel check) is the problem: for two floor-mounted dishes both
    // axles spin around world up, so when forwards are antiparallel the rights
    // are GEOMETRICALLY FORCED antiparallel. The check is an extra-strict
    // tautology in vanilla. For non-floor placements (e.g. wall TX + ceiling RX)
    // the two root frames have different world-up axes; even when auto-aim
    // drives forwards within the 7 deg tolerance, the rights end up dozens of
    // degrees apart because H and V only control aim direction, not roll around
    // the forward axis. Empirically observed on 2026-04-26 with auto-aim on
    // wall TX + ceiling RX: forwards angle 178.92 deg (well within 7 deg of
    // 180), rights angle 56.01 deg (124 deg outside the tolerance). No setting
    // of H or V on either dish can satisfy condition 5.
    //
    // Conditions 1-4 are sufficient to confirm aim and line-of-sight reachability.
    // Replacing TryContactReceiver with a forwards-only variant unblocks non-
    // floor pairs without changing behaviour for floor-only pairs (where the
    // right-axis check is redundant). Gated on NonFloorPlacementPatched, so
    // when the host disables non-floor placement vanilla behaviour is preserved
    // exactly.
    //
    // Implementation notes:
    //   - Thing._colliderLookup is `public static Dictionary<Collider, Thing>`
    //     (Thing.cs:634) so accessible without reflection.
    //   - PowerTransmitter.LinkedReceiver and PowerReceiver.LinkedPowerTransmitter
    //     are public, settable directly.
    //   - PowerTransmitter._linkedReceiverDistance is `private float`. Harmony's
    //     three-underscore parameter convention is unreliable for fields whose
    //     names start with underscore (would require four underscores in the
    //     param name and fails-fast at PatchAll time if the count is wrong).
    //     Use a cached AccessTools.Field setter instead: more direct, more
    //     readable, matches the in-repo AccessToolsRecipes.md pattern.
    //   - Returning false from the Prefix skips the vanilla body. The replacement
    //     covers every state vanilla would write: clears LinkedReceiver / partner,
    //     re-runs the link probe, sets the trio of fields on success.
    [HarmonyPatch(typeof(PowerTransmitter), "TryContactReceiver")]
    public static class TryContactReceiverPatch
    {
        private static readonly FieldInfo LinkedReceiverDistanceField =
            AccessTools.Field(typeof(PowerTransmitter), "_linkedReceiverDistance");

        // Static buffer for the swept hit list. Sized for "every collider
        // along a 200 m beam through a busy base"; sphere broadphase typically
        // returns 5-20 hits, occasionally more in dense interiors. 64 leaves
        // generous headroom while staying small enough to keep the per-hit
        // walk cheap. Static (not [ThreadStatic]) because TryContactReceiver
        // is called from Unity's main thread; PowerTick threading concerns
        // the WirelessPower.VisualizerIntensity setter only (see
        // Research/GameSystems/PowerTickThreading.md).
        private const int HitBufferSize = 64;
        private const float SphereCastRadius = 0.5f;
        private static readonly RaycastHit[] HitBuffer = new RaycastHit[HitBufferSize];


        [UsedImplicitly]
        public static bool Prefix(PowerTransmitter __instance)
        {
            if (!GameManager.RunSimulation || GameManager.GameState != GameState.Running)
                return false;

            if (__instance.LinkedReceiver != null)
            {
                __instance.LinkedReceiver.LinkedPowerTransmitter = null;
            }
            __instance.LinkedReceiver = null;

            var rayT = __instance.RayTransform;
            if (rayT == null) return false;

            // SphereCast with a 0.5 m radius tolerates any sub-degree aim
            // residual or mid-slew jitter that a narrow Raycast would miss.
            // Filter post-hit: accept the FIRST swept hit (closest to origin
            // by hit.distance) whose collider belongs to a PowerReceiver and
            // whose transform is that receiver's DishTarget. Walls, pipes,
            // cables, the dish's own arm geometry, etc. all fail this filter
            // and are silently skipped, so the broadened ray cannot link to
            // anything that is not actually a receiver dish.
            //
            // Stationeers exposes no content-typed Physics layer (see
            // Research/GameClasses/Layers.md), so we cannot pre-filter the
            // cast; the post-hit walk is the only correct mechanism.
            Vector3 origin = rayT.position;
            Vector3 direction = rayT.TransformDirection(Vector3.forward);
            int hitCount = Physics.SphereCastNonAlloc(
                origin, SphereCastRadius, direction, HitBuffer, float.PositiveInfinity);
            if (hitCount <= 0) return false;

            // SphereCastNonAlloc does not guarantee distance-sorted output;
            // walk every hit and pick the smallest-distance match.
            PowerReceiver bestRx = null;
            float bestDistance = float.PositiveInfinity;
            for (int i = 0; i < hitCount; i++)
            {
                var hit = HitBuffer[i];
                if (hit.transform == null) continue;
                if (!Thing._colliderLookup.TryGetValue(hit.collider, out var thing)) continue;
                if (!(thing is PowerReceiver rx)) continue;
                if (hit.transform != rx.DishTarget) continue;
                if (hit.distance >= bestDistance) continue;
                bestRx = rx;
                bestDistance = hit.distance;
            }
            if (bestRx == null) return false;

            // Forwards-antiparallel gate (vanilla condition 4). Preserved as a
            // sanity check: rejects cases where a stale autoaim cache pointed
            // at the right receiver but the dish has been manually slewed
            // partway off-axis. The right-axis antiparallel gate (vanilla
            // condition 5) stays dropped because non-floor pairs cannot
            // satisfy it geometrically (see header comment).
            if (!RocketMath.Approximately(Vector3.Angle(rayT.forward, bestRx.RayTransform.forward), 180f, 7f))
                return false;

            // Use the exact ray-origin to DishTarget distance for
            // _linkedReceiverDistance instead of SphereCast's contact distance,
            // which underestimates by up to SphereCastRadius. The exact
            // distance feeds PowerTransmitterPlus's overridden distance-cost
            // curve cleanly.
            float linkDistance = Vector3.Distance(origin, bestRx.DishTarget.position);

            __instance.LinkedReceiver = bestRx;
            bestRx.LinkedPowerTransmitter = __instance;
            LinkedReceiverDistanceField?.SetValue(__instance, linkDistance);
            return false;
        }
    }
}
