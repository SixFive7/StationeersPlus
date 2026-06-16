using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;

namespace PowerGridPlus
{
    /// <summary>
    ///     Info hover line for a transformer whose throughput <c>Setting</c> sits below its rated
    ///     <c>OutputMaximum</c> (POWER.md §5.3, deviation P13). Under PowerGridPlus the in-world dial
    ///     writes Priority, so a Setting below the rated max can only have come from IC10 (or a legacy
    ///     save); this surfaces that advanced use on the case and on/off-button hovers so a throttled
    ///     transformer is never a mystery dark subnet.
    ///
    ///     <para>It is NOT a fault: no flash, no countdown. It stacks below any active fault line, so a
    ///     throttled transformer that also overloads shows both, which incidentally explains the
    ///     overload (a low Setting trips OVERLOAD at the lower threshold, §8.4).</para>
    /// </summary>
    internal static class TransformerThrottleHover
    {
        // Half a watt of slop so float noise on an IC10-written Setting does not trip the warning. A
        // freshly built transformer has Setting bit-identical to OutputMaximum (TransformerSettingInitPatch),
        // so the default case never flags.
        private const double Eps = 0.5;

        // Muted amber: reads as "heads up / advanced" without mimicking the shed orange (#ffa500) or a
        // red fault. Restyle here.
        private const string Color = "#d9a441";

        internal static bool TryGetLine(Thing thing, out string line)
        {
            line = null;
            if (!(thing is Transformer transformer)) return false;
            double set = transformer.Setting;
            double max = transformer.OutputMaximum;
            if (max - set <= Eps) return false;   // at (or effectively at) rated throughput: nothing to flag
            line = $"<color={Color}>(Throttled to {set:0} W of {max:0} W by a custom IC10 \"Setting\" value. The dial sets priority.)</color>";
            return true;
        }
    }
}
