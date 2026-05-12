using Assets.Scripts.Networking;
using Assets.Scripts.Objects.Electrical;
using UnityEngine;

namespace NetworkPuristPlus
{
    // Straight single-tile cables (Cable : SmallSingleGrid, IsStraight == true) have an unconstrained
    // "roll" about their run axis: the game treats Cable as SmartRotate.ConnectionType.Exhaustive (24
    // box orientations), and for a "straight along axis A" piece 4 of those 24 are connectivity-identical
    // -- the 0/90/180/270 deg rolls about A -- but visually distinct, because the heavy / super-heavy
    // cable mesh carries a red band that breaks the symmetry. ZoopMod hard-codes one roll, the manual
    // R key picks another, blueprints propagate whatever was copied; the result is a network where the
    // band jumps between adjacent pieces. The roll is purely cosmetic for a straight piece (the two open
    // ends sit on the run axis, so rolling about it moves neither end -- connectivity / network / collision
    // / the merge system / save-load are all roll-blind), and the cable mesh follows transform.rotation
    // 1:1 (there is no dynamic re-skin like pipes have), so re-rolling a placed straight cable is safe.
    //
    // Canonical roll = the "Straight" set: one rotation per run axis, the same set SmartRotate uses for
    // ConnectionType.Straight (RotationsList[Straight] == [RotX, RotY, RotZ]):
    //   Z run -> Euler(0,0,90)  (forward +Z;  == SmartRotate.RotZ.Rotation; also what ZoopMod uses for Z)
    //   X run -> Euler(0,90,0)  (forward +X;  == SmartRotate.RotY.Rotation)
    //   Y run -> Euler(90,0,0)  (forward -Y;  == SmartRotate.RotX.Rotation)
    // This matches ZoopMod's Z-run roll and the build cursor once it is switched to ConnectionType.Straight
    // (see LongVariantRegistry.AlignCableCursors), so ZoopMod-placed Z cables come out already canonical.
    //
    // Long-variant cables (StructureCableSuperHeavyStraight3 / 5 / 10) are NOT touched here: they are
    // handled by the long-piece rebuild (ReplaceLongPiecesOnLoadPatch) and the build-time rewrite
    // (RewriteLongVariantOnConstructPatch); their single-tile replacements are born canonical and then
    // pass through the on-register normalisation like any other freshly built cable.
    internal static class CableRoll
    {
        // One canonical rotation per run axis. These are exactly RotationsList[SmartRotate.ConnectionType.Straight].
        private static readonly Quaternion CanonZ = Quaternion.Euler(0f, 0f, 90f);
        private static readonly Quaternion CanonX = Quaternion.Euler(0f, 90f, 0f);
        private static readonly Quaternion CanonY = Quaternion.Euler(90f, 0f, 0f);

        // The canonical rotation for the run axis implied by `rotation` -- i.e. whichever world axis the
        // local forward (the cable run direction) maps to. Rotations on placed pieces are always 90-deg
        // box orientations, so the dominant component is unambiguous (with a little FP noise tolerated).
        internal static Quaternion Canonical(Quaternion rotation)
        {
            Vector3 f = rotation * Vector3.forward;
            float ax = Mathf.Abs(f.x), ay = Mathf.Abs(f.y), az = Mathf.Abs(f.z);
            if (ax >= ay && ax >= az) return CanonX;   // runs along world X
            if (ay >= az) return CanonY;               // runs along world Y
            return CanonZ;                             // runs along world Z
        }

        // True if this cable is a straight piece we should re-roll: not a corner/tee/cross, not a long
        // variant, not on its way out.
        internal static bool IsNormalisableStraight(Cable c) =>
            c != null && !c.IsBeingDestroyed && c.IsStraight && !LongVariantRegistry.LongToBase.ContainsKey(c.PrefabHash);

        // Re-roll a placed straight cable to the canonical orientation for its run axis. Cosmetic only:
        // changes the transform rotation, the registered rotation (what the save records), the network-sync
        // Direction field, and -- on the server -- raises the transform-delta flag so connected clients get
        // the new rotation. Does NOT re-register the cable (which would re-run Cable.OnRegistered ->
        // CableNetwork.Merge, a full network rebuild); the occupied cell of a single-tile SmallSingleGrid
        // is rotation-invariant, so there is nothing to re-register. Returns true if the rotation changed.
        internal static bool Normalise(Cable c)
        {
            if (!IsNormalisableStraight(c)) return false;
            Quaternion cur = c.ThingTransformRotation;
            Quaternion canon = Canonical(cur);
            if (Quaternion.Angle(cur, canon) < 1f) return false;

            c.ThingTransform.rotation = canon;   // visible mesh follows this frame; no re-skin call needed
            c.RegisteredRotation = canon;        // StructureSaveData.RegisteredWorldRotation is written from this
            c.Direction = canon;                 // keep the join-package Direction consistent (network-sync-only field)
            if (NetworkManager.IsServer)
                c.NetworkUpdateFlags |= 1;       // bit 1 == Thing transform delta -> already-connected clients
            return true;
        }
    }
}
