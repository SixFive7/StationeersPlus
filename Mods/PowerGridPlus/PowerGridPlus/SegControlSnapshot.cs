using System.Collections.Generic;

namespace PowerGridPlus
{
    /// <summary>
    ///     GATHER-time control-state snapshot for the segmenter classes: (OnOff, Error) per
    ///     ReferenceId, captured once per tick while the allocator enumerates the segmenter
    ///     roster and consumed by the ENFORCE-phase gates in the segmenter patch files
    ///     (TransformerExploitPatches, PowerTransmitterDrawPatches, StationaryBatteryPatches,
    ///     AreaPowerControlPatches, RocketUmbilicalPatches) in place of live
    ///     <c>__instance.OnOff</c> / <c>.Error</c> reads.
    ///
    ///     <para><b>Why.</b> OnOff and Error are main-thread-writable (player interactions land
    ///     between GATHER and ENFORCE), and the ENFORCE gates re-reading them live was the last
    ///     post-GATHER live control-state read in the solve (the read-once census, angle E). A
    ///     mid-tick toggle now lands on the NEXT tick coherently: the tick's grant is honored as
    ///     granted, which is the same one-tick coherence discipline as the value-method latches.
    ///     IC10 / logic writes were never a hazard (the logic tick runs after the power tick on
    ///     the same worker); this pins the player-interaction window.</para>
    ///
    ///     <para>A miss (device not enumerated at GATHER: just placed, no cables) falls back to
    ///     the caller's live read. During OBSERVE (which runs before this tick's ALLOCATE) the
    ///     map still holds LAST tick's snapshot; OBSERVE's outputs are re-derived at ENFORCE, so
    ///     the one-tick-stale gate values there are harmless and coherent.</para>
    ///
    ///     <para>Threading: built on the allocator worker, swapped by volatile reference,
    ///     read-only afterwards. Cleared on world load.</para>
    /// </summary>
    internal static class SegControlSnapshot
    {
        internal struct Entry
        {
            public bool OnOff;
            public int Error;
        }

        private static volatile Dictionary<long, Entry> _byRef = new Dictionary<long, Entry>();

        /// <summary>Swap in this tick's snapshot (ALLOCATE, allocator worker).</summary>
        internal static void Publish(Dictionary<long, Entry> map)
        {
            _byRef = map;
        }

        internal static bool TryGet(long refId, out bool onOff, out int error)
        {
            if (_byRef.TryGetValue(refId, out var e))
            {
                onOff = e.OnOff;
                error = e.Error;
                return true;
            }
            onOff = false;
            error = 0;
            return false;
        }

        /// <summary>World-load reset.</summary>
        internal static void Clear()
        {
            _byRef = new Dictionary<long, Entry>();
        }
    }
}
