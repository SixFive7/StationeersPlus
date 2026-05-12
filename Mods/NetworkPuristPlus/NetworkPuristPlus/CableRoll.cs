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
    // Canonical roll = the orientation a vanilla fresh-cursor straight cable comes out at (Z run -> identity,
    // band on top), with the X/Y-axis members chosen to mate with the largest number of corner pieces:
    //   Z run -> Quaternion.identity        (forward +Z, band/local-up on world +Y -- a fresh-cursor Z straight)
    //   X run -> Euler(0,90,0)              (forward +X, band on world +Y)
    //   Y run -> Euler(270,0,90)            (forward +Y, band on world -X)
    // The X and Y members are the per-axis plurality optimum over a 12k-thing test save: of the per-run-axis
    // canonical rolls, these put the band on the world face the largest single share of corner cables exposes
    // their band to at that leg. See Research/GameSystems/PlacementOrientation.md
    // ("Why straight-cable roll normalisation does not fix the band-seam at a corner cable").
    //
    // IMPORTANT -- this does NOT fully eliminate the band-seam at corner cables. Corner pieces have a fixed
    // band orientation that the mod does not touch (re-rolling a corner is connectivity-relevant, not cosmetic
    // -- its open ends are off the run axis), and no single canonical roll per run axis mates with every
    // corner: a corner's band-exit face is per-corner-rotation (not even a function of its leg-direction pair
    // -- the "L flipped over" cases), and a straight that runs between two corners with contradictory band
    // faces can match at one end only. This choice mates with the most corners; a full corner-seam fix is a
    // larger, separate feature (re-roll each corner-adjacent straight per its specific corner) -- see TODO.md.
    //
    // Long-variant cables (StructureCableSuperHeavyStraight3 / 5 / 10) are NOT touched here: they are
    // handled by the long-piece rebuild (ReplaceLongPiecesOnLoadPatch) and the build-time rewrite
    // (RewriteLongVariantOnConstructPatch); their single-tile replacements are born canonical and then
    // pass through the on-register normalisation like any other freshly built cable.
    //
    // (Changing these constants means the next world load re-rolls every straight cable to the new canonical
    // -- one more one-time conversion, expected and cosmetic-only.)
    internal static class CableRoll
    {
        // One canonical rotation per run axis: a fresh-cursor straight for the Z run (identity), and the
        // per-axis plurality corner-mating optimum for the X and Y runs (see the class comment).
        private static readonly Quaternion CanonZ = Quaternion.identity;            // Z run: forward +Z, band (local up) on world +Y
        private static readonly Quaternion CanonX = Quaternion.Euler(0f, 90f, 0f);  // X run: forward +X, band on world +Y
        private static readonly Quaternion CanonY = Quaternion.Euler(270f, 0f, 90f); // Y run: forward +Y, band on world -X

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
