using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Objects.Rockets;

namespace PowerGridPlus
{
    /// <summary>
    ///     Classifies the "segmenting devices": every device that holds two distinct cable /
    ///     wireless network references and has Input/Output power-flow semantics (POWER.md §5.0).
    ///     These are the level boundaries the allocator models and the edges of the cycle graph.
    ///
    ///     Verified concrete classes (all derive from <see cref="ElectricalInputOutput"/>, which exposes
    ///     <c>InputNetwork</c> / <c>OutputNetwork</c> / <c>OnOff</c> / <c>ReferenceId</c> uniformly):
    ///     Transformer, Battery (StationaryBattery + StationBatteryLarge + nuclear), AreaPowerControl,
    ///     PowerTransmitter, PowerReceiver, RocketPowerUmbilicalMale, RocketPowerUmbilicalFemale.
    ///     PowerConnection is excluded: it is vestigial dead code (POWER.md §5.0.1) and is not an
    ///     ElectricalInputOutput.
    ///
    ///     <para>The old on-demand map scan (EnumerateSorted) is gone: the per-tick roster now comes
    ///     from GridSnapshot.SegmentersSorted, built during the single boundary read (sorted by
    ///     ReferenceId ascending for MP determinism, POWER.md §8.0.1).</para>
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
    }
}
