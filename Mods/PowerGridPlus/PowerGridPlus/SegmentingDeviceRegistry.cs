using System.Collections.Generic;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Objects.Rockets;

namespace PowerGridPlus
{
    /// <summary>
    ///     Enumerates the "segmenting devices" on the map: every device that holds two distinct cable /
    ///     wireless network references and has Input/Output power-flow semantics (POWER.md §5.0). These are
    ///     the level boundaries the cascade walks and the edges of the cycle-detection graph.
    ///
    ///     Verified concrete classes (all derive from <see cref="ElectricalInputOutput"/>, which exposes
    ///     <c>InputNetwork</c> / <c>OutputNetwork</c> / <c>OnOff</c> / <c>ReferenceId</c> uniformly):
    ///     Transformer, Battery (StationaryBattery + StationBatteryLarge + nuclear), AreaPowerControl,
    ///     PowerTransmitter, PowerReceiver, RocketPowerUmbilicalMale, RocketPowerUmbilicalFemale.
    ///     PowerConnection is excluded: it is vestigial dead code (POWER.md §5.0.1) and is not an
    ///     ElectricalInputOutput.
    ///
    ///     <para>MP determinism: <see cref="EnumerateSorted"/> returns segmenters sorted by
    ///     <c>ReferenceId</c> ascending so every peer walks them in identical order (POWER.md §8.0.1).
    ///     Rebuilt by scanning the live networks on demand rather than maintained via add/remove hooks
    ///     (simpler and equally deterministic).</para>
    /// </summary>
    internal static class SegmentingDeviceRegistry
    {
        // True for the seven concrete segmenting-device classes. Takes an ElectricalInputOutput because
        // all seven derive from it; the concrete-type switch keeps the set exactly the POWER.md §5.0 list.
        internal static bool IsSegmenter(ElectricalInputOutput d)
        {
            switch (d)
            {
                case Transformer _:
                case Battery _:
                case AreaPowerControl _:
                case PowerTransmitter _:
                case PowerReceiver _:
                case RocketPowerUmbilicalMale _:
                case RocketPowerUmbilicalFemale _:
                    return true;
                default:
                    return false;
            }
        }

        // All segmenting devices on the map, deduplicated and sorted by ReferenceId ascending. A segmenter
        // appears on both its input and output network's PowerDeviceList, hence the dedup set.
        internal static List<ElectricalInputOutput> EnumerateSorted()
        {
            var seen = new HashSet<long>();
            var result = new List<ElectricalInputOutput>();
            // CableNetwork.AllCableNetworks is a ConcurrentDensePool (ForEach only, no indexer).
            CableNetwork.AllCableNetworks.ForEach(net =>
            {
                if (net == null) return;
                lock (net.PowerDeviceList)
                {
                    var list = net.PowerDeviceList;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i] is ElectricalInputOutput eio && IsSegmenter(eio) && seen.Add(eio.ReferenceId))
                            result.Add(eio);
                    }
                }
            });
            result.Sort((a, b) => a.ReferenceId.CompareTo(b.ReferenceId));
            return result;
        }
    }
}
