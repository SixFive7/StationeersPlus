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

            // Vanilla narrow Raycast. With the auto-aim improvement that
            // iterates RayTransform -> DishTarget to a fixed point, the aim is
            // precise enough for the narrow ray to hit the receiver's
            // DishTarget collider directly, even on non-floor mounts. Avoids
            // the false-positive obstacle problem a SphereCast would bring
            // (catching cables / pipes the narrow ray naturally passes by).
            if (!Physics.Raycast(rayT.position, rayT.TransformDirection(Vector3.forward), out var hit, float.PositiveInfinity))
                return false;
            if (hit.transform == null) return false;
            if (!Thing._colliderLookup.TryGetValue(hit.collider, out var thing)) return false;
            if (!(thing is PowerReceiver rx)) return false;
            if (hit.transform != rx.DishTarget) return false;
            if (!RocketMath.Approximately(Vector3.Angle(rayT.forward, rx.RayTransform.forward), 180f, 7f))
                return false;

            // Vanilla also requires the right-axis antiparallel check; we skip
            // it because non-floor pairs cannot satisfy it geometrically.

            __instance.LinkedReceiver = rx;
            rx.LinkedPowerTransmitter = __instance;
            LinkedReceiverDistanceField?.SetValue(__instance, hit.distance);
            return false;
        }
    }
}
