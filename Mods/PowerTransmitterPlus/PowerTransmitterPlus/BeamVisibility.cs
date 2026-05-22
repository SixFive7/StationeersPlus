using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Util;
using UnityEngine;

namespace PowerTransmitterPlus
{
    // Pure predicate: should a transmitter's beam currently be visible?
    //
    // Beam shows iff ALL of:
    //   1. The transmitter has a linked receiver (LinkedReceiver != null).
    //   2. Both RayTransforms exist (placement / lifecycle null guard).
    //   3. Both dishes have their on/off switch on (Thing.OnOff true on both).
    //   4. Current aim is still valid: forwards antiparallel within
    //      AimToleranceDegrees, matching the tolerance vanilla's link
    //      establishment uses in PowerTransmitter.TryContactReceiver
    //      (replaced in LinkPatch.cs:131).
    //
    // Intentionally NOT a gate:
    //   - Power flow (VisualizerIntensity / watts delivered). A linked pair
    //     with zero load still shows the beam (pulse train frozen) so players
    //     can see "the link is established" while troubleshooting why power
    //     is not flowing. This is the v1.5.1 design and stays.
    //   - Brownout (Powered == false). Same diagnosis-friendly reason: a
    //     transmitter with no source power but switched on still shows the
    //     link so the player can see what they wired.
    //   - Error state (Error != 0). A broken / self-shorted / damaged dish
    //     still shows the link so the player can see the configuration
    //     that is now broken.
    //
    // The function is pure and side-effect-free. Reads cached animator-backed
    // Thing state (OnOff) and live transform poses (RayTransform.forward).
    // Both are safe to read from the main thread, which is where every
    // visibility trigger fires (interactable updates, slew steps, link
    // reference changes; see RotationPatches, OnOffPatches,
    // LinkVisibilityPatch and Research/GameClasses/Interactable.md).
    internal static class BeamVisibility
    {
        // Same tolerance as PowerTransmitter.TryContactReceiver / LinkPatch.cs
        // uses to establish the link. Matching keeps the semantics consistent:
        // the beam is visible iff the link could be (re-)established right
        // now. Auto-aim converges to well under one degree per iteration
        // (AutoAimPatches drives RayTransform.forward to within ~1 cm of the
        // partner's predicted endpoint), so within-tolerance micro-corrections
        // during tracking do not flicker the beam.
        internal const float AimToleranceDegrees = 7f;

        internal static bool ShouldShow(PowerTransmitter tx)
        {
            if (tx == null) return false;

            var rx = tx.LinkedReceiver;
            if (rx == null) return false;

            if (tx.RayTransform == null || rx.RayTransform == null) return false;

            if (!tx.OnOff || !rx.OnOff) return false;

            // Forward-antiparallel check, same as LinkPatch.cs:131-132. Reads
            // the live RayTransform pose on both dishes, so a slewing dish
            // drops out of tolerance as soon as its forward diverges from the
            // partner's by more than AimToleranceDegrees.
            var angle = Vector3.Angle(tx.RayTransform.forward, rx.RayTransform.forward);
            if (!RocketMath.Approximately(angle, 180f, AimToleranceDegrees)) return false;

            return true;
        }

        // Diagnostic helper. Pretty-prints predicate inputs and result.
        // Tolerates nulls so it is safe to call in any state the predicate
        // itself accepts. Called by BeamManager.ReevaluateVisibility when
        // PowerTransmitterPlusPlugin.BeamDiagnosticLogging is on.
        internal static string Describe(PowerTransmitter tx)
        {
            if (tx == null) return "tx=null";

            var rx = tx.LinkedReceiver;

            string aimStr;
            if (tx.RayTransform == null || rx == null || rx.RayTransform == null)
            {
                aimStr = "n/a";
            }
            else
            {
                var angle = Vector3.Angle(tx.RayTransform.forward, rx.RayTransform.forward);
                aimStr = $"{angle:F2}deg (offBy180={180f - angle:F2}, tol={AimToleranceDegrees})";
            }

            return $"tx={tx.ReferenceId} " +
                   $"link={(rx == null ? "null" : rx.ReferenceId.ToString())} " +
                   $"txOnOff={tx.OnOff} " +
                   $"rxOnOff={(rx == null ? "n/a" : rx.OnOff.ToString())} " +
                   $"aim={aimStr} " +
                   $"shouldShow={ShouldShow(tx)}";
        }
    }
}
