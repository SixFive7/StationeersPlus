using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;

namespace PowerGridPlus
{
    /// <summary>
    ///     Detects a transformer whose throughput <c>Setting</c> sits below its rated
    ///     <c>OutputMaximum</c> (POWER.md §5.3, deviation P13). Under PowerGridPlus the in-world dial
    ///     writes Priority, so a Setting below the rated max can only have come from IC10 (or a legacy
    ///     save); the THROTTLED info block (rendered by <see cref="FaultHover"/>, locked template)
    ///     surfaces that advanced use on the casing and on/off-button hovers so a throttled
    ///     transformer is never a mystery dark subnet.
    ///
    ///     <para>It is NOT a fault: no flash, no countdown, and every active fault (plus the
    ///     dead-input cue) outranks it in the one-block-per-hover resolution.</para>
    /// </summary>
    internal static class TransformerThrottleHover
    {
        // Half a watt of slop so float noise on an IC10-written Setting does not trip the note. A
        // freshly built transformer has Setting bit-identical to OutputMaximum (TransformerSettingInitPatch),
        // so the default case never flags.
        private const double Eps = 0.5;

        // True when the hovered thing is a transformer throttled below its rated throughput;
        // FaultHover renders the block from the returned pair.
        internal static bool TryGetThrottle(Thing thing, out float settingW, out float maximumW)
        {
            settingW = 0f;
            maximumW = 0f;
            if (!(thing is Transformer transformer)) return false;
            double set = transformer.Setting;
            double max = transformer.OutputMaximum;
            if (max - set <= Eps) return false;   // at (or effectively at) rated throughput: nothing to flag
            settingW = (float)set;
            maximumW = (float)max;
            return true;
        }
    }
}
