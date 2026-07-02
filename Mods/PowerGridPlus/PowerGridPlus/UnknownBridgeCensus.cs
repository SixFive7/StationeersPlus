using System;
using System.Collections.Generic;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;

namespace PowerGridPlus
{
    /// <summary>
    ///     One-shot diagnostic census of UNMODELLED bridge devices (Stage 2b unknown-bridge lane).
    ///     On the first atomic tick after a world load, every scene device that is an
    ///     <see cref="ElectricalInputOutput"/> subclass but is neither in
    ///     <see cref="SegmentingDeviceRegistry"/>'s known segmenter set nor described by any
    ///     <see cref="ISegAdapter"/> gets reported -- one Info line per TYPE, not per device.
    ///
    ///     <para>Reporting is the ONLY handling: an unknown bridge keeps its vanilla power methods
    ///     unpatched, so inside OBSERVE/ENFORCE it behaves exactly as vanilla would, and the
    ///     allocator's GATHER sums its GetUsedPower / GetGeneratedPower as plain rigid demand /
    ///     generation on each of its networks (the segmenter skip only applies to the known set).
    ///     That is the conservative fallback; the census just makes the gap visible instead of
    ///     silent when a third-party mod ships its own two-port power device.</para>
    ///
    ///     <para>Lifecycle: armed at plugin load (covers the session's first world, including fresh
    ///     worlds that never pass through XmlSaveLoad.LoadWorld) and re-armed by
    ///     FaultRegistryLoadPatches on every world load. Runs on the power worker inside
    ///     AtomicElectricityTickPatch, before OBSERVE; managed reads only, no Unity API. Only the
    ///     simulating peer runs the atomic tick, so the census is host-side.</para>
    /// </summary>
    internal static class UnknownBridgeCensus
    {
        private static bool _pending = true;

        /// <summary>Arm the census to run on the next atomic tick (world-load hook).</summary>
        internal static void Arm() => _pending = true;

        /// <summary>Run the census once if armed; otherwise a single flag check.</summary>
        internal static void RunIfPending()
        {
            if (!_pending) return;
            _pending = false;
            try
            {
                Run();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"Unknown-bridge census failed (diagnostic only, sim unaffected): {e.Message}");
            }
        }

        private static void Run()
        {
            // Scene-wide device walk (the same pool the vanilla phase-2 device tick iterates), so
            // unwired bridges are counted too. Dedupe by ReferenceId; count unique devices per type.
            var seen = new HashSet<long>();
            var counts = new Dictionary<Type, int>();
            ElectricityManager.AllPoweredThings.ForEach(powered =>
            {
                if (!(powered is ElectricalInputOutput eio)) return;
                if (SegmentingDeviceRegistry.IsSegmenter(eio)) return;
                if (SegAdapters.AnyDescribes(eio)) return;
                if (!seen.Add(eio.ReferenceId)) return;
                var type = eio.GetType();
                counts.TryGetValue(type, out int n);
                counts[type] = n + 1;
            });
            if (counts.Count == 0) return;

            var types = new List<Type>(counts.Keys);
            types.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));
            foreach (var type in types)
            {
                int n = counts[type];
                Plugin.Log?.LogInfo(
                    $"Unmodeled bridge device type {type.FullName} ({n} instance{(n == 1 ? "" : "s")}): left on vanilla behavior");
            }
        }
    }
}
