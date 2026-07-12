using System.Collections.Generic;

namespace PowerGridPlus
{
    /// <summary>
    ///     Per-tick segmenter control snapshot: (OnOff, Error) per ReferenceId, captured from the
    ///     boundary read's rows and consumed by the presentation shims in the segmenter patch
    ///     files (TransformerExploitPatches, PowerTransmitterDrawPatches, StationaryBatteryPatches,
    ///     AreaPowerControlPatches, RocketUmbilicalPatches) in place of live
    ///     <c>__instance.OnOff</c> / <c>.Error</c> reads, so a tooltip or third-party caller sees
    ///     the same control verdict the allocator billed under.
    ///
    ///     <para>OnOff and Error are main-thread-writable; a mid-tick toggle lands on the NEXT
    ///     tick coherently. A miss (device not in the snapshot: just placed, no cables) falls back
    ///     to the caller's live read. During the boundary read itself the map still holds LAST
    ///     tick's snapshot, which is exactly the vanilla-OBSERVE-equivalent value the per-net sums
    ///     want.</para>
    ///
    ///     <para>Threading: built on the allocator worker, swapped by volatile reference,
    ///     read-only afterwards. Cleared at the load boundary.</para>
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
